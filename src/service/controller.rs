use std::env;
use std::process::Command;
use std::sync::Arc;

use anyhow::{Context, Result, anyhow};

use crate::domain::models::{ElevatedServiceAction, ServiceStartup, ServiceState, ServiceStatus};
use crate::process_utils::{configure_hidden, hidden_command};
use crate::service::SERVICE_NAME;
use crate::storage::config_store::ConfigStore;

#[cfg(not(windows))]
use std::process::Stdio;

pub trait ServiceController: Send + Sync {
    fn start(&self) -> Result<()>;
    fn stop(&self) -> Result<()>;
    fn install(&self, startup: ServiceStartup) -> Result<()>;
    fn uninstall(&self) -> Result<()>;
    fn set_startup(&self, startup: ServiceStartup) -> Result<()>;
    fn run_elevated(&self, action: ElevatedServiceAction) -> Result<()>;
    fn status(&self) -> Result<ServiceStatus>;
}

pub fn current_service_controller(store: ConfigStore) -> Arc<dyn ServiceController> {
    #[cfg(windows)]
    {
        let _ = store;
        return Arc::new(WindowsServiceController);
    }
    #[cfg(not(windows))]
    {
        return Arc::new(UnixServiceController { store });
    }
    #[allow(unreachable_code)]
    Arc::new(UnsupportedServiceController { _store: store })
}

#[cfg(windows)]
struct WindowsServiceController;

#[cfg(windows)]
impl ServiceController for WindowsServiceController {
    fn start(&self) -> Result<()> {
        let status = self.status()?;
        if !status.installed {
            return Err(anyhow!("service is not registered"));
        }
        if matches!(status.state, ServiceState::Running) {
            return Ok(());
        }
        run_sc_checked(&["start", SERVICE_NAME]).map(|_| ())
    }

    fn stop(&self) -> Result<()> {
        let status = self.status()?;
        if !status.installed {
            return Err(anyhow!("service is not registered"));
        }
        if matches!(status.state, ServiceState::Stopped) {
            return Ok(());
        }
        run_sc_checked(&["stop", SERVICE_NAME]).map(|_| ())
    }

    fn install(&self, startup: ServiceStartup) -> Result<()> {
        let status = self.status()?;
        if !status.installed {
            let exe = service_host_executable()?;
            let bin_path = format!("\"{}\" internal run-service", exe.display());
            run_sc_checked(&[
                "create",
                SERVICE_NAME,
                "binPath=",
                &bin_path,
                "start=",
                startup_sc_value(startup),
                "DisplayName=",
                "proxyz",
            ])?;
        }
        self.set_startup(startup)
    }

    fn uninstall(&self) -> Result<()> {
        let status = self.status()?;
        if !status.installed {
            return Ok(());
        }
        let _ = run_sc_checked(&["stop", SERVICE_NAME]);
        run_sc_checked(&["delete", SERVICE_NAME]).map(|_| ())
    }

    fn set_startup(&self, startup: ServiceStartup) -> Result<()> {
        let status = self.status()?;
        if !status.installed {
            return Err(anyhow!("service is not registered"));
        }
        run_sc_checked(&["config", SERVICE_NAME, "start=", startup_sc_value(startup)]).map(|_| ())
    }

    fn run_elevated(&self, action: ElevatedServiceAction) -> Result<()> {
        let exe = service_host_executable()?;
        let mut args = vec!["internal".to_string(), "service-admin".to_string()];
        match action {
            ElevatedServiceAction::Install(startup) => {
                args.push("install".to_string());
                args.push("--startup".to_string());
                args.push(startup_cli_value(startup).to_string());
            }
            ElevatedServiceAction::Uninstall => args.push("uninstall".to_string()),
            ElevatedServiceAction::Start => args.push("start".to_string()),
            ElevatedServiceAction::Stop => args.push("stop".to_string()),
            ElevatedServiceAction::SetStartup(startup) => {
                args.push("set-startup".to_string());
                args.push("--startup".to_string());
                args.push(startup_cli_value(startup).to_string());
            }
        }
        run_with_uac(exe.as_os_str(), &args)
    }

    fn status(&self) -> Result<ServiceStatus> {
        let query = run_sc_raw(&["query", SERVICE_NAME])?;
        let query_text = sc_output_text(&query);
        if query_text.contains("FAILED 1060") {
            return Ok(ServiceStatus {
                installed: false,
                state: ServiceState::Stopped,
                startup: ServiceStartup::Unknown,
                detail: query_text,
            });
        }

        let state = if query_text.contains("RUNNING") {
            ServiceState::Running
        } else if query_text.contains("STOPPED") {
            ServiceState::Stopped
        } else {
            ServiceState::Unknown
        };
        let qc = run_sc_raw(&["qc", SERVICE_NAME])?;
        let startup = parse_startup_mode(&sc_output_text(&qc));
        Ok(ServiceStatus {
            installed: true,
            state,
            startup,
            detail: query_text,
        })
    }
}

#[cfg(not(windows))]
struct UnixServiceController {
    store: ConfigStore,
}

#[cfg(not(windows))]
impl ServiceController for UnixServiceController {
    fn start(&self) -> Result<()> {
        let status = self.status()?;
        if matches!(status.state, ServiceState::Running) {
            return Ok(());
        }

        let exe = env::current_exe().context("failed to locate current executable")?;
        let child = Command::new(exe)
            .args(["internal", "run-service"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .context("failed to spawn background daemon")?;

        self.store.ensure_parent_dirs()?;
        std::fs::write(self.store.pid_file_path(), child.id().to_string())
            .context("failed to write daemon PID file")?;
        Ok(())
    }

    fn stop(&self) -> Result<()> {
        let pid_file = self.store.pid_file_path();
        if !pid_file.exists() {
            return Ok(());
        }

        let pid = std::fs::read_to_string(&pid_file)
            .context("failed to read daemon PID file")?
            .trim()
            .to_string();
        Command::new("kill")
            .args(["-TERM", &pid])
            .status()
            .context("failed to terminate daemon process")?;
        let _ = std::fs::remove_file(pid_file);
        Ok(())
    }

    fn install(&self, _startup: ServiceStartup) -> Result<()> {
        Err(anyhow!(
            "service installation management is only implemented on Windows"
        ))
    }

    fn uninstall(&self) -> Result<()> {
        Err(anyhow!(
            "service installation management is only implemented on Windows"
        ))
    }

    fn set_startup(&self, _startup: ServiceStartup) -> Result<()> {
        Err(anyhow!(
            "service installation management is only implemented on Windows"
        ))
    }

    fn run_elevated(&self, action: ElevatedServiceAction) -> Result<()> {
        match action {
            ElevatedServiceAction::Start => self.start(),
            ElevatedServiceAction::Stop => self.stop(),
            ElevatedServiceAction::Install(_)
            | ElevatedServiceAction::Uninstall
            | ElevatedServiceAction::SetStartup(_) => Err(anyhow!(
                "elevated service management is only implemented on Windows"
            )),
        }
    }

    fn status(&self) -> Result<ServiceStatus> {
        let pid_file = self.store.pid_file_path();
        if !pid_file.exists() {
            return Ok(ServiceStatus {
                installed: false,
                state: ServiceState::Stopped,
                startup: ServiceStartup::Unknown,
                detail: "daemon not started".to_string(),
            });
        }
        let pid = std::fs::read_to_string(&pid_file)
            .context("failed to read daemon PID file")?
            .trim()
            .to_string();
        let output = Command::new("kill")
            .args(["-0", &pid])
            .output()
            .context("failed to query daemon status")?;
        let state = if output.status.success() {
            ServiceState::Running
        } else {
            ServiceState::Stopped
        };
        Ok(ServiceStatus {
            installed: true,
            state,
            startup: ServiceStartup::Unknown,
            detail: format!("pid={pid}"),
        })
    }
}

struct UnsupportedServiceController {
    _store: ConfigStore,
}

impl ServiceController for UnsupportedServiceController {
    fn start(&self) -> Result<()> {
        Err(anyhow!(
            "service control is not implemented for this platform"
        ))
    }

    fn stop(&self) -> Result<()> {
        Err(anyhow!(
            "service control is not implemented for this platform"
        ))
    }

    fn install(&self, _startup: ServiceStartup) -> Result<()> {
        Err(anyhow!(
            "service control is not implemented for this platform"
        ))
    }

    fn uninstall(&self) -> Result<()> {
        Err(anyhow!(
            "service control is not implemented for this platform"
        ))
    }

    fn set_startup(&self, _startup: ServiceStartup) -> Result<()> {
        Err(anyhow!(
            "service control is not implemented for this platform"
        ))
    }

    fn run_elevated(&self, _action: ElevatedServiceAction) -> Result<()> {
        Err(anyhow!(
            "service control is not implemented for this platform"
        ))
    }

    fn status(&self) -> Result<ServiceStatus> {
        Ok(ServiceStatus {
            installed: false,
            state: ServiceState::Unknown,
            startup: ServiceStartup::Unknown,
            detail: "service control is not implemented for this platform".to_string(),
        })
    }
}

#[cfg(windows)]
fn run_sc_raw(args: &[&str]) -> Result<std::process::Output> {
    hidden_command("sc.exe")
        .args(args)
        .output()
        .with_context(|| format!("failed to execute sc.exe {}", args.join(" ")))
}

#[cfg(windows)]
fn run_sc_checked(args: &[&str]) -> Result<String> {
    let output = run_sc_raw(args)?;
    let text = sc_output_text(&output);
    if output.status.success() {
        return Ok(text);
    }
    Err(anyhow!("sc.exe {} failed: {}", args.join(" "), text))
}

#[cfg(windows)]
fn sc_output_text(output: &std::process::Output) -> String {
    let stdout = crate::process_utils::decode_output_text(&output.stdout);
    let stderr = crate::process_utils::decode_output_text(&output.stderr);
    let stdout = stdout.trim();
    let stderr = stderr.trim();
    match (stdout.is_empty(), stderr.is_empty()) {
        (false, false) => format!("{stdout}\n{stderr}"),
        (false, true) => stdout.to_string(),
        (true, false) => stderr.to_string(),
        (true, true) => "no output".to_string(),
    }
}

#[cfg(windows)]
fn parse_startup_mode(raw: &str) -> ServiceStartup {
    if raw.contains("AUTO_START") {
        ServiceStartup::Automatic
    } else if raw.contains("DEMAND_START") {
        ServiceStartup::Manual
    } else if raw.contains("DISABLED") {
        ServiceStartup::Disabled
    } else {
        ServiceStartup::Unknown
    }
}

#[cfg(windows)]
fn startup_sc_value(startup: ServiceStartup) -> &'static str {
    match startup {
        ServiceStartup::Automatic => "auto",
        ServiceStartup::Manual | ServiceStartup::Unknown => "demand",
        ServiceStartup::Disabled => "disabled",
    }
}

#[cfg(windows)]
fn startup_cli_value(startup: ServiceStartup) -> &'static str {
    match startup {
        ServiceStartup::Automatic => "automatic",
        ServiceStartup::Manual => "manual",
        ServiceStartup::Disabled => "disabled",
        ServiceStartup::Unknown => "manual",
    }
}

#[cfg(windows)]
fn run_with_uac(exe: &std::ffi::OsStr, args: &[String]) -> Result<()> {
    let exe = powershell_quote(&exe.to_string_lossy());
    let arg_list = args
        .iter()
        .map(|arg| format!("'{}'", powershell_quote(arg)))
        .collect::<Vec<_>>()
        .join(", ");
    let script = format!(
        "$p = Start-Process -FilePath '{exe}' -ArgumentList @({arg_list}) -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
    );
    let mut command = Command::new("powershell");
    configure_hidden(&mut command);
    let output = command
        .args(["-NoProfile", "-NonInteractive", "-Command", &script])
        .output()
        .context("failed to launch elevated PowerShell helper")?;
    if output.status.success() {
        return Ok(());
    }
    Err(anyhow!(
        "elevated service action failed: {}",
        sc_output_text(&output)
    ))
}

#[cfg(windows)]
fn powershell_quote(value: &str) -> String {
    value.replace('\'', "''")
}

#[cfg(windows)]
fn service_host_executable() -> Result<std::path::PathBuf> {
    let current = env::current_exe().context("failed to locate current executable")?;
    let Some(stem) = current.file_stem().and_then(|value| value.to_str()) else {
        return Ok(current);
    };
    if stem.ends_with("-gui") {
        let base_stem = stem.trim_end_matches("-gui");
        let base_path = current.with_file_name(format!("{base_stem}.exe"));
        if base_path.exists() {
            return Ok(base_path);
        }
    }
    Ok(current)
}

use std::process::Command;
use std::sync::Arc;

use anyhow::{Context, Result, anyhow};

use crate::domain::models::{AppSettings, ProxySettings};
use crate::process_utils::{configure_hidden, decode_output_text};

#[cfg(target_os = "linux")]
mod linux;
#[cfg(target_os = "macos")]
mod macos;
#[cfg(windows)]
mod windows;

pub trait ProxyBackend: Send + Sync {
    fn current_settings(&self) -> Result<ProxySettings>;
    fn apply_settings(&self, settings: &ProxySettings, app_settings: &AppSettings) -> Result<()>;
    fn disable(&self, app_settings: &AppSettings) -> Result<()>;
    fn backend_name(&self) -> &'static str;
}

pub fn current_proxy_backend() -> Arc<dyn ProxyBackend> {
    #[cfg(windows)]
    {
        return Arc::new(windows::WindowsProxyBackend);
    }
    #[cfg(target_os = "macos")]
    {
        return Arc::new(macos::MacOsProxyBackend);
    }
    #[cfg(target_os = "linux")]
    {
        return Arc::new(linux::LinuxProxyBackend);
    }
    #[allow(unreachable_code)]
    Arc::new(UnsupportedProxyBackend)
}

pub fn run_command_capture(program: &str, args: &[&str]) -> Result<String> {
    let mut command = Command::new(program);
    configure_hidden(&mut command);
    let output = command
        .args(args)
        .output()
        .with_context(|| format!("failed to execute {}", program))?;

    if !output.status.success() {
        return Err(anyhow!(
            "{} failed: {}",
            program,
            decode_output_text(&output.stderr).trim()
        ));
    }

    Ok(decode_output_text(&output.stdout))
}

pub fn normalize_proxy_endpoint(input: &str) -> String {
    input
        .trim()
        .trim_start_matches("http://")
        .trim_start_matches("https://")
        .trim_start_matches("socks5://")
        .trim_end_matches('/')
        .to_string()
}

pub fn split_host_port(input: &str, default_port: u16) -> Result<(String, u16)> {
    let normalized = normalize_proxy_endpoint(input);
    let without_credentials = normalized
        .rsplit('@')
        .next()
        .unwrap_or(normalized.as_str())
        .to_string();
    if let Some((host, port)) = without_credentials.rsplit_once(':') {
        let parsed_port = port
            .parse::<u16>()
            .with_context(|| format!("failed to parse proxy port from endpoint {}", input))?;
        Ok((host.to_string(), parsed_port))
    } else {
        Ok((without_credentials, default_port))
    }
}

struct UnsupportedProxyBackend;

impl ProxyBackend for UnsupportedProxyBackend {
    fn current_settings(&self) -> Result<ProxySettings> {
        Ok(ProxySettings::default())
    }

    fn apply_settings(&self, _settings: &ProxySettings, _app_settings: &AppSettings) -> Result<()> {
        Err(anyhow!(
            "proxy backend is not implemented for this platform"
        ))
    }

    fn disable(&self, _app_settings: &AppSettings) -> Result<()> {
        Err(anyhow!(
            "proxy backend is not implemented for this platform"
        ))
    }

    fn backend_name(&self) -> &'static str {
        "unsupported"
    }
}

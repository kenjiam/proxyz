use anyhow::{Context, Result};

use crate::adapters::proxy::{ProxyBackend, run_command_capture, split_host_port};
use crate::domain::models::{AppSettings, ProxySettings};

pub struct LinuxProxyBackend;

impl ProxyBackend for LinuxProxyBackend {
    fn current_settings(&self) -> Result<ProxySettings> {
        let mode = run_command_capture("gsettings", &["get", "org.gnome.system.proxy", "mode"])
            .context("gsettings is required to read proxy settings on Linux")?;
        let enabled = mode.contains("manual");

        let http_host =
            run_command_capture("gsettings", &["get", "org.gnome.system.proxy.http", "host"])?;
        let http_port =
            run_command_capture("gsettings", &["get", "org.gnome.system.proxy.http", "port"])?;
        let https_host = run_command_capture(
            "gsettings",
            &["get", "org.gnome.system.proxy.https", "host"],
        )?;
        let https_port = run_command_capture(
            "gsettings",
            &["get", "org.gnome.system.proxy.https", "port"],
        )?;
        let bypass = run_command_capture(
            "gsettings",
            &["get", "org.gnome.system.proxy", "ignore-hosts"],
        )?;

        let http_host = strip_gsettings_string(&http_host);
        let https_host = strip_gsettings_string(&https_host);
        let http = (!http_host.is_empty()).then(|| format!("{}:{}", http_host, http_port.trim()));
        let https =
            (!https_host.is_empty()).then(|| format!("{}:{}", https_host, https_port.trim()));

        Ok(ProxySettings {
            enabled,
            http,
            https,
            all: None,
            bypass: bypass
                .trim()
                .trim_start_matches('[')
                .trim_end_matches(']')
                .split(',')
                .map(str::trim)
                .map(strip_gsettings_string)
                .filter(|item| !item.is_empty())
                .collect(),
        })
    }

    fn apply_settings(&self, settings: &ProxySettings, _app_settings: &AppSettings) -> Result<()> {
        if !settings.enabled {
            return self.disable(_app_settings);
        }

        run_command_capture(
            "gsettings",
            &["set", "org.gnome.system.proxy", "mode", "manual"],
        )?;
        if let Some(http) = settings.http.clone().or_else(|| settings.all.clone()) {
            let (host, port) = split_host_port(&http, 8080)?;
            run_command_capture(
                "gsettings",
                &["set", "org.gnome.system.proxy.http", "host", &host],
            )?;
            run_command_capture(
                "gsettings",
                &[
                    "set",
                    "org.gnome.system.proxy.http",
                    "port",
                    &port.to_string(),
                ],
            )?;
        }
        if let Some(https) = settings.https.clone().or_else(|| settings.all.clone()) {
            let (host, port) = split_host_port(&https, 8080)?;
            run_command_capture(
                "gsettings",
                &["set", "org.gnome.system.proxy.https", "host", &host],
            )?;
            run_command_capture(
                "gsettings",
                &[
                    "set",
                    "org.gnome.system.proxy.https",
                    "port",
                    &port.to_string(),
                ],
            )?;
        }

        let bypass_list = if settings.bypass.is_empty() {
            "[]".to_string()
        } else {
            let joined = settings
                .bypass
                .iter()
                .map(|item| format!("'{}'", item))
                .collect::<Vec<_>>()
                .join(", ");
            format!("[{joined}]")
        };
        run_command_capture(
            "gsettings",
            &[
                "set",
                "org.gnome.system.proxy",
                "ignore-hosts",
                &bypass_list,
            ],
        )?;
        Ok(())
    }

    fn disable(&self, _app_settings: &AppSettings) -> Result<()> {
        run_command_capture(
            "gsettings",
            &["set", "org.gnome.system.proxy", "mode", "none"],
        )?;
        Ok(())
    }

    fn backend_name(&self) -> &'static str {
        "linux-gsettings"
    }
}

fn strip_gsettings_string(raw: &str) -> String {
    raw.trim().trim_matches('\'').to_string()
}

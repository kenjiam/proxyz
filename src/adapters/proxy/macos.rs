use anyhow::{Context, Result};

use crate::adapters::proxy::{ProxyBackend, run_command_capture, split_host_port};
use crate::domain::models::{AppSettings, ProxySettings};

pub struct MacOsProxyBackend;

impl ProxyBackend for MacOsProxyBackend {
    fn current_settings(&self) -> Result<ProxySettings> {
        let services = network_services()?;
        let Some(service) = services.first() else {
            return Ok(ProxySettings::default());
        };

        let web = run_command_capture("networksetup", &["-getwebproxy", service])?;
        let secure = run_command_capture("networksetup", &["-getsecurewebproxy", service])?;
        let bypass = run_command_capture("networksetup", &["-getproxybypassdomains", service])?;

        Ok(ProxySettings {
            enabled: web.contains("Enabled: Yes") || secure.contains("Enabled: Yes"),
            http: parse_networksetup_proxy(&web),
            https: parse_networksetup_proxy(&secure),
            all: None,
            bypass: bypass
                .lines()
                .map(str::trim)
                .filter(|line| !line.is_empty())
                .map(ToString::to_string)
                .collect(),
        })
    }

    fn apply_settings(&self, settings: &ProxySettings, _app_settings: &AppSettings) -> Result<()> {
        let services = network_services()?;
        for service in services {
            if settings.enabled {
                if let Some(http) = settings.http.clone().or_else(|| settings.all.clone()) {
                    let (host, port) = split_host_port(&http, 8080)?;
                    run_command_capture(
                        "networksetup",
                        &[
                            "-setwebproxy",
                            service.as_str(),
                            host.as_str(),
                            &port.to_string(),
                        ],
                    )?;
                    run_command_capture(
                        "networksetup",
                        &["-setwebproxystate", service.as_str(), "on"],
                    )?;
                }
                if let Some(https) = settings.https.clone().or_else(|| settings.all.clone()) {
                    let (host, port) = split_host_port(&https, 8080)?;
                    run_command_capture(
                        "networksetup",
                        &[
                            "-setsecurewebproxy",
                            service.as_str(),
                            host.as_str(),
                            &port.to_string(),
                        ],
                    )?;
                    run_command_capture(
                        "networksetup",
                        &["-setsecurewebproxystate", service.as_str(), "on"],
                    )?;
                }
                if settings.bypass.is_empty() {
                    run_command_capture(
                        "networksetup",
                        &["-setproxybypassdomains", service.as_str(), ""],
                    )?;
                } else {
                    let mut args = vec!["-setproxybypassdomains", service.as_str()];
                    let bypass_refs = settings
                        .bypass
                        .iter()
                        .map(String::as_str)
                        .collect::<Vec<_>>();
                    args.extend(bypass_refs);
                    run_command_capture("networksetup", &args)?;
                }
            } else {
                run_command_capture(
                    "networksetup",
                    &["-setwebproxystate", service.as_str(), "off"],
                )?;
                run_command_capture(
                    "networksetup",
                    &["-setsecurewebproxystate", service.as_str(), "off"],
                )?;
            }
        }
        Ok(())
    }

    fn disable(&self, app_settings: &AppSettings) -> Result<()> {
        self.apply_settings(&ProxySettings::default(), app_settings)
    }

    fn backend_name(&self) -> &'static str {
        "macos-networksetup"
    }
}

fn network_services() -> Result<Vec<String>> {
    let output = run_command_capture("networksetup", &["-listallnetworkservices"])
        .context("failed to enumerate macOS network services")?;
    Ok(output
        .lines()
        .skip(1)
        .map(str::trim)
        .filter(|line| !line.is_empty() && !line.starts_with('*'))
        .map(ToString::to_string)
        .collect())
}

fn parse_networksetup_proxy(raw: &str) -> Option<String> {
    if !raw.contains("Enabled: Yes") {
        return None;
    }
    let mut host = None;
    let mut port = None;
    for line in raw.lines() {
        let trimmed = line.trim();
        if let Some(value) = trimmed.strip_prefix("Server: ") {
            host = Some(value.trim().to_string());
        } else if let Some(value) = trimmed.strip_prefix("Port: ") {
            port = Some(value.trim().to_string());
        }
    }
    match (host, port) {
        (Some(host), Some(port)) => Some(format!("{host}:{port}")),
        _ => None,
    }
}

use anyhow::Result;

use crate::adapters::network::{SsidProvider, run_command};

pub struct WindowsSsidProvider;

impl SsidProvider for WindowsSsidProvider {
    fn current_ssid(&self) -> Result<Option<String>> {
        let output = run_command("netsh", &["wlan", "show", "interfaces"])?;
        for line in output.lines() {
            let trimmed = line.trim();
            if trimmed.starts_with("SSID") && !trimmed.starts_with("BSSID") {
                if let Some((_, value)) = trimmed.split_once(':') {
                    let ssid = value.trim();
                    if !ssid.is_empty() {
                        return Ok(Some(ssid.to_string()));
                    }
                }
            }
        }
        Ok(None)
    }

    fn provider_name(&self) -> &'static str {
        "windows-netsh"
    }
}

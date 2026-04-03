use anyhow::Result;

use crate::adapters::network::{SsidProvider, run_command};

pub struct LinuxSsidProvider;

impl SsidProvider for LinuxSsidProvider {
    fn current_ssid(&self) -> Result<Option<String>> {
        let output = run_command("nmcli", &["-t", "-f", "active,ssid", "dev", "wifi"])?;
        for line in output.lines() {
            if let Some(ssid) = line.strip_prefix("yes:") {
                let ssid = ssid.trim();
                if !ssid.is_empty() {
                    return Ok(Some(ssid.to_string()));
                }
            }
        }
        Ok(None)
    }

    fn provider_name(&self) -> &'static str {
        "linux-nmcli"
    }
}

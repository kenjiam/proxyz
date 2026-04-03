use anyhow::Result;

use crate::adapters::network::{SsidProvider, run_command};

pub struct MacOsSsidProvider;

impl SsidProvider for MacOsSsidProvider {
    fn current_ssid(&self) -> Result<Option<String>> {
        let output = run_command(
            "/System/Library/PrivateFrameworks/Apple80211.framework/Versions/Current/Resources/airport",
            &["-I"],
        )?;
        for line in output.lines() {
            let trimmed = line.trim();
            if let Some(value) = trimmed.strip_prefix("SSID: ") {
                let ssid = value.trim();
                if !ssid.is_empty() {
                    return Ok(Some(ssid.to_string()));
                }
            }
        }
        Ok(None)
    }

    fn provider_name(&self) -> &'static str {
        "macos-airport"
    }
}

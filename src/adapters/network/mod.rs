use std::process::Command;
use std::sync::Arc;

use anyhow::{Context, Result};

use crate::process_utils::{configure_hidden, decode_output_text};

#[cfg(target_os = "linux")]
mod linux;
#[cfg(target_os = "macos")]
mod macos;
#[cfg(windows)]
mod windows;

pub trait SsidProvider: Send + Sync {
    fn current_ssid(&self) -> Result<Option<String>>;
    fn provider_name(&self) -> &'static str;
}

pub fn current_ssid_provider() -> Arc<dyn SsidProvider> {
    #[cfg(windows)]
    {
        return Arc::new(windows::WindowsSsidProvider);
    }
    #[cfg(target_os = "macos")]
    {
        return Arc::new(macos::MacOsSsidProvider);
    }
    #[cfg(target_os = "linux")]
    {
        return Arc::new(linux::LinuxSsidProvider);
    }
    #[allow(unreachable_code)]
    Arc::new(UnsupportedSsidProvider)
}

pub fn run_command(program: &str, args: &[&str]) -> Result<String> {
    let mut command = Command::new(program);
    configure_hidden(&mut command);
    let output = command
        .args(args)
        .output()
        .with_context(|| format!("failed to execute {}", program))?;

    if !output.status.success() {
        return Ok(decode_output_text(&output.stdout));
    }

    Ok(decode_output_text(&output.stdout))
}

struct UnsupportedSsidProvider;

impl SsidProvider for UnsupportedSsidProvider {
    fn current_ssid(&self) -> Result<Option<String>> {
        Ok(None)
    }

    fn provider_name(&self) -> &'static str {
        "unsupported"
    }
}

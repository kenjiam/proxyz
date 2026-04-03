pub mod controller;

#[cfg(not(windows))]
mod unix_host;
#[cfg(windows)]
mod windows_host;

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::Duration;

use anyhow::Result;
use log::{error, info};

use crate::application::services::AppService;

pub const SERVICE_NAME: &str = "proxyz";

pub fn run_service_loop(app: Arc<AppService>, stop_flag: Arc<AtomicBool>) -> Result<()> {
    info!("service loop started");
    let mut last_ssid: Option<String> = None;
    while !stop_flag.load(Ordering::SeqCst) {
        match app.monitor_cycle(last_ssid.as_deref()) {
            Ok(outcome) => {
                last_ssid = outcome.observed_ssid.clone();
                if !outcome.action_summary.is_empty() {
                    info!("{}", outcome.action_summary);
                }
            }
            Err(error) => error!("service cycle failed: {error:#}"),
        }

        let interval = app.poll_interval();
        let mut slept = 0;
        while slept < interval && !stop_flag.load(Ordering::SeqCst) {
            thread::sleep(Duration::from_secs(1));
            slept += 1;
        }
    }
    info!("service loop stopped");
    Ok(())
}

#[cfg(windows)]
pub fn run_service_host(app: Arc<AppService>) -> Result<()> {
    windows_host::run(app)
}

#[cfg(not(windows))]
pub fn run_service_host(app: Arc<AppService>) -> Result<()> {
    unix_host::run(app)
}

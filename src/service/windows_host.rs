use std::ffi::OsString;
use std::sync::Arc;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering};

use anyhow::{Context, Result};
use windows_service::define_windows_service;
use windows_service::service::{
    ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState as WinServiceState,
    ServiceStatus, ServiceType,
};
use windows_service::service_control_handler::{self, ServiceControlHandlerResult};
use windows_service::service_dispatcher;

use crate::application::services::AppService;
use crate::service::{SERVICE_NAME, run_service_loop};

static SERVICE_APP: OnceLock<Arc<AppService>> = OnceLock::new();

define_windows_service!(ffi_service_main, proxyz_service_main);

pub fn run(app: Arc<AppService>) -> Result<()> {
    let _ = SERVICE_APP.set(app);
    service_dispatcher::start(SERVICE_NAME, ffi_service_main)
        .context("failed to connect to the Windows service dispatcher")?;
    Ok(())
}

fn proxyz_service_main(_arguments: Vec<OsString>) {
    if let Err(error) = run_inner() {
        log::error!("windows service failed: {error:#}");
    }
}

fn run_inner() -> Result<()> {
    let stop_flag = Arc::new(AtomicBool::new(false));
    let stop_flag_handler = Arc::clone(&stop_flag);

    let status_handle =
        service_control_handler::register(SERVICE_NAME, move |control_event| match control_event {
            ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
            ServiceControl::Stop => {
                stop_flag_handler.store(true, Ordering::SeqCst);
                ServiceControlHandlerResult::NoError
            }
            _ => ServiceControlHandlerResult::NotImplemented,
        })
        .context("failed to register Windows service control handler")?;

    status_handle.set_service_status(ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: WinServiceState::Running,
        controls_accepted: ServiceControlAccept::STOP,
        exit_code: ServiceExitCode::Win32(0),
        checkpoint: 0,
        wait_hint: std::time::Duration::default(),
        process_id: None,
    })?;

    let app = SERVICE_APP
        .get()
        .cloned()
        .context("service application context is not initialized")?;
    run_service_loop(app, Arc::clone(&stop_flag))?;

    status_handle.set_service_status(ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: WinServiceState::Stopped,
        controls_accepted: ServiceControlAccept::empty(),
        exit_code: ServiceExitCode::Win32(0),
        checkpoint: 0,
        wait_hint: std::time::Duration::default(),
        process_id: None,
    })?;
    Ok(())
}

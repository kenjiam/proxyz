use std::sync::Arc;
use std::sync::atomic::AtomicBool;

use anyhow::Result;

use crate::application::services::AppService;
use crate::service::run_service_loop;

pub fn run(app: Arc<AppService>) -> Result<()> {
    run_service_loop(app, Arc::new(AtomicBool::new(false)))
}

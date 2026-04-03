mod app;
#[cfg(windows)]
mod tray;

use std::sync::Arc;

use anyhow::Result;

use crate::application::services::AppService;

pub fn launch(service: Arc<AppService>) -> Result<()> {
    app::launch(service)
}

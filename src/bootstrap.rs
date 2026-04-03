use std::fs::File;
use std::sync::Arc;

use anyhow::Result;
use simplelog::{ColorChoice, CombinedLogger, Config, TermLogger, TerminalMode, WriteLogger};

use crate::adapters::network::current_ssid_provider;
use crate::adapters::proxy::current_proxy_backend;
use crate::application::services::{AppService, level_filter};
use crate::service::controller::current_service_controller;
use crate::storage::config_store::ConfigStore;

pub fn create_app() -> Result<Arc<AppService>> {
    let store = ConfigStore::discover()?;
    init_logging(&store)?;

    Ok(Arc::new(AppService::new(
        store.clone(),
        current_proxy_backend(),
        current_ssid_provider(),
        current_service_controller(store),
    )))
}

pub fn init_logging(store: &ConfigStore) -> Result<()> {
    store.ensure_parent_dirs()?;
    let log_level = level_filter(store.bootstrap_log_level());
    let log_file = File::create(store.log_path())?;
    let _ = CombinedLogger::init(vec![
        TermLogger::new(
            log_level,
            Config::default(),
            TerminalMode::Mixed,
            ColorChoice::Auto,
        ),
        WriteLogger::new(log_level, Config::default(), log_file),
    ]);
    Ok(())
}

use std::fs;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use directories::ProjectDirs;
use serde::Serialize;

use crate::domain::models::{AppSettings, AppState, LogLevel, ProfileCatalog};

#[derive(Clone, Debug)]
pub struct ConfigStore {
    config_path: PathBuf,
    profiles_path: PathBuf,
    log_path: PathBuf,
    runtime_dir: PathBuf,
}

impl ConfigStore {
    pub fn discover() -> Result<Self> {
        let project_dirs = ProjectDirs::from("dev", "proxyz", "proxyz")
            .context("failed to determine the OS-specific configuration directory")?;
        let config_dir = project_dirs.config_dir().to_path_buf();
        let data_dir = project_dirs.data_local_dir().to_path_buf();
        let runtime_dir = data_dir.join("runtime");

        Ok(Self {
            config_path: config_dir.join("config.json"),
            profiles_path: config_dir.join("profiles.json"),
            log_path: data_dir.join("proxyz.log"),
            runtime_dir,
        })
    }

    pub fn from_paths(
        config_path: PathBuf,
        profiles_path: PathBuf,
        log_path: PathBuf,
        runtime_dir: PathBuf,
    ) -> Self {
        Self {
            config_path,
            profiles_path,
            log_path,
            runtime_dir,
        }
    }

    pub fn config_path(&self) -> &Path {
        &self.config_path
    }

    pub fn profiles_path(&self) -> &Path {
        &self.profiles_path
    }

    pub fn log_path(&self) -> &Path {
        &self.log_path
    }

    pub fn runtime_dir(&self) -> &Path {
        &self.runtime_dir
    }

    pub fn pid_file_path(&self) -> PathBuf {
        self.runtime_dir.join("proxyz.pid")
    }

    pub fn ensure_parent_dirs(&self) -> Result<()> {
        for path in [&self.config_path, &self.profiles_path] {
            if let Some(parent) = path.parent() {
                fs::create_dir_all(parent).with_context(|| {
                    format!("failed to create config directory {}", parent.display())
                })?;
            }
        }
        if let Some(parent) = self.log_path.parent() {
            fs::create_dir_all(parent)
                .with_context(|| format!("failed to create log directory {}", parent.display()))?;
        }
        fs::create_dir_all(&self.runtime_dir).with_context(|| {
            format!(
                "failed to create runtime directory {}",
                self.runtime_dir.display()
            )
        })?;
        Ok(())
    }

    pub fn load(&self) -> Result<AppState> {
        self.migrate_legacy_layout()?;
        let settings = self.load_settings()?;
        let catalog = self.load_profiles_catalog()?;
        Ok(AppState::from_parts(settings, catalog))
    }

    pub fn load_settings(&self) -> Result<AppSettings> {
        self.ensure_parent_dirs()?;
        if !self.config_path.exists() {
            return Ok(AppSettings::default());
        }

        let raw = fs::read_to_string(&self.config_path)
            .with_context(|| format!("failed to read {}", self.config_path.display()))?;
        if let Ok(settings) = serde_json::from_str::<AppSettings>(&raw) {
            return Ok(settings);
        }
        if let Ok(state) = serde_json::from_str::<AppState>(&raw) {
            return Ok(state.settings);
        }

        Err(anyhow::anyhow!(
            "failed to parse {} as settings JSON",
            self.config_path.display()
        ))
    }

    pub fn load_profiles_catalog(&self) -> Result<ProfileCatalog> {
        self.ensure_parent_dirs()?;
        if self.profiles_path.exists() {
            let raw = fs::read_to_string(&self.profiles_path)
                .with_context(|| format!("failed to read {}", self.profiles_path.display()))?;
            return serde_json::from_str::<ProfileCatalog>(&raw)
                .with_context(|| format!("failed to parse {}", self.profiles_path.display()));
        }

        if self.config_path.exists() {
            let raw = fs::read_to_string(&self.config_path)
                .with_context(|| format!("failed to read {}", self.config_path.display()))?;
            if let Ok(state) = serde_json::from_str::<AppState>(&raw) {
                return Ok(ProfileCatalog::from(&state));
            }
        }

        Ok(ProfileCatalog::new())
    }

    pub fn save(&self, state: &AppState) -> Result<()> {
        self.ensure_parent_dirs()?;
        self.save_settings(&state.settings)?;
        self.save_profiles_catalog(&ProfileCatalog::from(state))
    }

    pub fn save_settings(&self, settings: &AppSettings) -> Result<()> {
        self.ensure_parent_dirs()?;
        self.write_json(&self.config_path, settings)
    }

    pub fn save_profiles_catalog(&self, catalog: &ProfileCatalog) -> Result<()> {
        self.ensure_parent_dirs()?;
        self.write_json(&self.profiles_path, catalog)
    }

    pub fn bootstrap_settings(&self) -> AppSettings {
        self.load_settings().unwrap_or_default()
    }

    pub fn bootstrap_log_level(&self) -> LogLevel {
        self.bootstrap_settings().log_level
    }

    fn migrate_legacy_layout(&self) -> Result<()> {
        self.ensure_parent_dirs()?;
        if self.profiles_path.exists() || !self.config_path.exists() {
            return Ok(());
        }

        let raw = fs::read_to_string(&self.config_path)
            .with_context(|| format!("failed to read {}", self.config_path.display()))?;
        let Ok(legacy_state) = serde_json::from_str::<AppState>(&raw) else {
            return Ok(());
        };

        self.write_json(&self.config_path, &legacy_state.settings)?;
        self.write_json(&self.profiles_path, &ProfileCatalog::from(&legacy_state))
    }

    fn write_json<T: Serialize>(&self, path: &Path, value: &T) -> Result<()> {
        let raw = serde_json::to_string_pretty(value)?;
        let temp_path = path.with_extension("json.tmp");
        fs::write(&temp_path, raw)
            .with_context(|| format!("failed to write {}", temp_path.display()))?;
        fs::rename(&temp_path, path)
            .with_context(|| format!("failed to replace config file {}", path.display()))?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use std::fs;

    use tempfile::tempdir;

    use super::ConfigStore;
    use crate::domain::models::{AppSettings, AppState, ProxyProfile, ProxySettings, UiLanguage};

    #[test]
    fn round_trip_split_storage() {
        let temp_dir = tempdir().unwrap();
        let store = ConfigStore::from_paths(
            temp_dir.path().join("config.json"),
            temp_dir.path().join("profiles.json"),
            temp_dir.path().join("proxyz.log"),
            temp_dir.path().join("runtime"),
        );

        let mut state = AppState::new();
        state.settings = AppSettings {
            ui_language: UiLanguage::English,
            ..AppSettings::default()
        };
        state.profiles.push(ProxyProfile {
            name: "office".to_string(),
            proxy: ProxySettings {
                enabled: true,
                http: Some("http://proxy.local:8080".to_string()),
                https: None,
                all: None,
                bypass: vec!["localhost".to_string()],
            },
            memo: "saved".to_string(),
            post_apply_commands: Vec::new(),
        });

        store.save(&state).unwrap();
        let restored = store.load().unwrap();
        assert_eq!(restored.settings.ui_language, UiLanguage::English);
        assert_eq!(restored.profiles.len(), 1);
        assert_eq!(restored.profiles[0].name, "office");
        assert!(store.profiles_path().exists());
    }

    #[test]
    fn migrates_legacy_single_file_layout() {
        let temp_dir = tempdir().unwrap();
        let store = ConfigStore::from_paths(
            temp_dir.path().join("config.json"),
            temp_dir.path().join("profiles.json"),
            temp_dir.path().join("proxyz.log"),
            temp_dir.path().join("runtime"),
        );

        let mut state = AppState::new();
        state.settings = AppSettings {
            ui_language: UiLanguage::English,
            ..AppSettings::default()
        };
        state.profiles.push(ProxyProfile {
            name: "campus".to_string(),
            proxy: ProxySettings::default(),
            memo: String::new(),
            post_apply_commands: Vec::new(),
        });

        fs::write(
            store.config_path(),
            serde_json::to_string_pretty(&state).unwrap(),
        )
        .unwrap();

        let restored = store.load().unwrap();
        assert_eq!(restored.settings.ui_language, UiLanguage::English);
        assert_eq!(restored.profiles[0].name, "campus");
        assert!(store.profiles_path().exists());
    }
}

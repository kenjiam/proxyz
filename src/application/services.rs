use std::process::Command;
use std::sync::Arc;

use anyhow::{Context, Result, bail};
use log::{error, info};

use crate::adapters::network::SsidProvider;
use crate::adapters::proxy::ProxyBackend;
use crate::domain::models::{
    AppSettings, AppState, ApplyOutcome, CommandExecutionRecord, ElevatedServiceAction,
    ExternalCommand, LogLevel, MonitorOutcome, ProfileCatalog, ProxyProfile, ProxySettings,
    ServiceStartup, ServiceStatus, SsidProfileMapping, SystemSnapshot, UnknownSsidAction,
    ValidationError, WindowsProxyTargets,
};
use crate::service::controller::ServiceController;
use crate::storage::config_store::ConfigStore;

#[derive(Clone)]
pub struct AppService {
    store: ConfigStore,
    proxy_backend: Arc<dyn ProxyBackend>,
    ssid_provider: Arc<dyn SsidProvider>,
    service_controller: Arc<dyn ServiceController>,
}

#[derive(Clone, Debug, Default)]
pub struct ApplyOptions {
    pub assume_yes: bool,
    pub dry_run: bool,
    pub skip_commands: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum SsidResolution {
    Ignore,
    ApplyProfile(String),
}

impl AppService {
    pub fn new(
        store: ConfigStore,
        proxy_backend: Arc<dyn ProxyBackend>,
        ssid_provider: Arc<dyn SsidProvider>,
        service_controller: Arc<dyn ServiceController>,
    ) -> Self {
        Self {
            store,
            proxy_backend,
            ssid_provider,
            service_controller,
        }
    }

    pub fn backend_summary(&self) -> String {
        format!(
            "proxy={}, ssid={}",
            self.proxy_backend.backend_name(),
            self.ssid_provider.provider_name()
        )
    }

    pub fn poll_interval(&self) -> u64 {
        self.load_settings()
            .map(|settings| settings.poll_interval_secs.max(1))
            .unwrap_or(10)
    }

    pub fn load_state(&self) -> Result<AppState> {
        self.store.load()
    }

    pub fn load_settings(&self) -> Result<AppSettings> {
        self.store.load_settings()
    }

    pub fn load_profile_catalog(&self) -> Result<ProfileCatalog> {
        self.store.load_profiles_catalog()
    }

    pub fn list_profiles(&self) -> Result<Vec<ProxyProfile>> {
        let mut profiles = self.load_profile_catalog()?.profiles;
        profiles.sort_by(|left, right| left.name.cmp(&right.name));
        Ok(profiles)
    }

    pub fn save_current_profile(&self, name: &str, memo: Option<String>) -> Result<ProxyProfile> {
        let current = self
            .proxy_backend
            .current_settings()
            .context("failed to read current OS proxy settings")?;
        let profile = ProxyProfile {
            name: name.trim().to_string(),
            proxy: current,
            memo: memo.unwrap_or_default(),
            post_apply_commands: Vec::new(),
        };
        self.upsert_profile(profile.clone())?;
        Ok(profile)
    }

    pub fn upsert_profile(&self, profile: ProxyProfile) -> Result<()> {
        validate_profile(&profile)?;
        let mut catalog = self.load_profile_catalog()?;
        if let Some(existing) = catalog
            .profiles
            .iter_mut()
            .find(|existing| existing.name == profile.name)
        {
            *existing = profile;
        } else {
            catalog.profiles.push(profile);
        }
        self.store.save_profiles_catalog(&catalog)
    }

    pub fn delete_profile(&self, name: &str) -> Result<()> {
        let mut state = self.load_state()?;
        if !state.remove_profile(name) {
            bail!("profile '{name}' does not exist");
        }
        self.store.save(&state)
    }

    pub fn replace_mappings(&self, mappings: Vec<SsidProfileMapping>) -> Result<()> {
        for mapping in &mappings {
            if mapping.ssid.trim().is_empty() {
                bail!("SSID mapping entries require a non-empty SSID");
            }
        }
        let mut catalog = self.load_profile_catalog()?;
        for mapping in &mappings {
            if !catalog
                .profiles
                .iter()
                .any(|profile| profile.name == mapping.profile_name)
            {
                bail!(
                    "SSID mapping references unknown profile '{}'",
                    mapping.profile_name
                );
            }
        }
        catalog.ssid_mappings = mappings;
        self.store.save_profiles_catalog(&catalog)
    }

    pub fn update_settings(&self, settings: AppSettings) -> Result<()> {
        validate_settings(&settings, &self.load_profile_catalog()?.profiles)?;
        self.store.save_settings(&settings)
    }

    pub fn service_start(&self) -> Result<()> {
        self.service_controller.start()
    }

    pub fn service_stop(&self) -> Result<()> {
        self.service_controller.stop()
    }

    pub fn service_status(&self) -> Result<ServiceStatus> {
        self.service_controller.status()
    }

    pub fn service_install(&self, startup: ServiceStartup) -> Result<()> {
        self.service_controller.install(startup)
    }

    pub fn service_uninstall(&self) -> Result<()> {
        self.service_controller.uninstall()
    }

    pub fn service_set_startup(&self, startup: ServiceStartup) -> Result<()> {
        self.service_controller.set_startup(startup)
    }

    pub fn service_run_elevated(&self, action: ElevatedServiceAction) -> Result<()> {
        self.service_controller.run_elevated(action)
    }

    pub fn system_snapshot(&self) -> Result<SystemSnapshot> {
        Ok(SystemSnapshot {
            current_proxy: self.proxy_backend.current_settings()?,
            current_ssid: self.ssid_provider.current_ssid()?,
            service_status: self.service_controller.status()?,
        })
    }

    pub fn set_profile(&self, name: &str, options: &ApplyOptions) -> Result<ApplyOutcome> {
        let state = self.load_state()?;
        let profile = state
            .find_profile(name)
            .cloned()
            .with_context(|| format!("profile '{name}' was not found"))?;

        validate_profile(&profile)?;
        let command_reports = if options.dry_run {
            Vec::new()
        } else {
            self.apply_proxy(&profile.proxy)?;
            if options.skip_commands {
                Vec::new()
            } else {
                self.execute_commands(&profile.post_apply_commands, &state.settings, options)?
            }
        };

        info!("applied profile '{}'", profile.name);
        Ok(ApplyOutcome {
            profile_name: profile.name,
            dry_run: options.dry_run,
            command_reports,
        })
    }

    pub fn monitor_cycle(&self, previous_ssid: Option<&str>) -> Result<MonitorOutcome> {
        let state = self.load_state()?;
        let current_ssid = self.ssid_provider.current_ssid()?;
        if current_ssid.as_deref() == previous_ssid {
            return Ok(MonitorOutcome {
                observed_ssid: current_ssid,
                action_summary: String::new(),
            });
        }
        match resolve_target_profile(&state, current_ssid.as_deref()) {
            SsidResolution::Ignore => Ok(MonitorOutcome {
                observed_ssid: current_ssid,
                action_summary: "no matching SSID action".to_string(),
            }),
            SsidResolution::ApplyProfile(profile_name) => {
                let outcome = self.set_profile(
                    &profile_name,
                    &ApplyOptions {
                        assume_yes: true,
                        dry_run: false,
                        skip_commands: false,
                    },
                )?;
                Ok(MonitorOutcome {
                    observed_ssid: current_ssid,
                    action_summary: format!(
                        "applied profile '{}' from SSID automation",
                        outcome.profile_name
                    ),
                })
            }
        }
    }

    fn apply_proxy(&self, settings: &ProxySettings) -> Result<()> {
        let app_settings = self.load_settings()?;
        if settings.enabled {
            self.proxy_backend.apply_settings(settings, &app_settings)
        } else {
            self.proxy_backend.disable(&app_settings)
        }
    }

    fn execute_commands(
        &self,
        commands: &[ExternalCommand],
        settings: &AppSettings,
        options: &ApplyOptions,
    ) -> Result<Vec<CommandExecutionRecord>> {
        let mut reports = Vec::new();
        for command in commands {
            if command.program.trim().is_empty() {
                bail!("post-apply command contains an empty program name");
            }
            if command.requires_confirmation
                && !settings.skip_command_confirmation
                && !options.assume_yes
            {
                bail!(
                    "command '{}' requires confirmation. Re-run with --yes or enable command confirmation bypass in settings",
                    command.program
                );
            }

            let output = Command::new(&command.program)
                .args(&command.args)
                .output()
                .with_context(|| format!("failed to execute {}", command.program))?;
            let success = output.status.success();
            let summary = summarize_command_output(&output.stdout, &output.stderr);
            let record = CommandExecutionRecord {
                program: command.program.clone(),
                args: command.args.clone(),
                success,
                exit_code: output.status.code(),
                summary: summary.clone(),
            };

            if success {
                info!(
                    "command '{}' finished successfully (exit_code={:?}) {}",
                    command.program, record.exit_code, summary
                );
            } else {
                error!(
                    "command '{}' failed (exit_code={:?}) {}",
                    command.program, record.exit_code, summary
                );
                reports.push(record);
                bail!("command '{}' failed: {}", command.program, summary);
            }

            reports.push(record);
        }
        Ok(reports)
    }
}

pub fn validate_profile(profile: &ProxyProfile) -> Result<()> {
    let errors = collect_profile_validation_errors(profile);
    if errors.is_empty() {
        return Ok(());
    }
    let joined = errors
        .iter()
        .map(|error| format!("{}: {}", error.field, error.message))
        .collect::<Vec<_>>()
        .join(", ");
    bail!("invalid profile '{}': {joined}", profile.name);
}

pub fn collect_profile_validation_errors(profile: &ProxyProfile) -> Vec<ValidationError> {
    let mut errors = Vec::new();
    if profile.name.trim().is_empty() {
        errors.push(ValidationError {
            field: "name".to_string(),
            message: "profile name is required".to_string(),
        });
    }
    if profile.proxy.enabled
        && profile.proxy.http.is_none()
        && profile.proxy.https.is_none()
        && profile.proxy.all.is_none()
    {
        errors.push(ValidationError {
            field: "proxy".to_string(),
            message: "at least one proxy endpoint is required when proxy is enabled".to_string(),
        });
    }
    for (index, command) in profile.post_apply_commands.iter().enumerate() {
        if command.program.trim().is_empty() {
            errors.push(ValidationError {
                field: format!("commands[{index}]"),
                message: "program name is required".to_string(),
            });
        }
    }
    errors
}

pub fn validate_settings(settings: &AppSettings, profiles: &[ProxyProfile]) -> Result<()> {
    if settings.poll_interval_secs == 0 {
        bail!("poll interval must be at least 1 second");
    }
    if settings.command_timeout_secs == 0 {
        bail!("command timeout must be at least 1 second");
    }
    if let Some(default_profile) = settings.default_profile.as_deref() {
        if !profiles
            .iter()
            .any(|profile| profile.name == default_profile)
        {
            bail!("default profile '{}' does not exist", default_profile);
        }
    }
    validate_proxy_targets(&settings.windows_proxy_targets)?;
    Ok(())
}

fn validate_proxy_targets(targets: &WindowsProxyTargets) -> Result<()> {
    #[cfg(windows)]
    if targets.enabled_count() == 0 {
        bail!("at least one Windows proxy target must be selected");
    }

    Ok(())
}

pub fn resolve_target_profile(state: &AppState, current_ssid: Option<&str>) -> SsidResolution {
    if let Some(ssid) = current_ssid {
        if let Some(mapping) = state
            .ssid_mappings
            .iter()
            .find(|mapping| mapping.ssid == ssid)
        {
            return SsidResolution::ApplyProfile(mapping.profile_name.clone());
        }
    }

    match state.settings.unknown_ssid_action {
        UnknownSsidAction::Ignore => SsidResolution::Ignore,
        UnknownSsidAction::ApplyDefaultProfile => state
            .settings
            .default_profile
            .clone()
            .map(SsidResolution::ApplyProfile)
            .unwrap_or(SsidResolution::Ignore),
    }
}

pub fn summarize_command_output(stdout: &[u8], stderr: &[u8]) -> String {
    let preferred = if stdout.is_empty() { stderr } else { stdout };
    let text = crate::process_utils::decode_output_text(preferred);
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return "no output".to_string();
    }

    let lines = trimmed.lines().take(3).collect::<Vec<_>>().join(" | ");
    if lines.len() > 240 {
        format!("{}...", &lines[..240])
    } else {
        lines
    }
}

pub fn level_filter(log_level: LogLevel) -> log::LevelFilter {
    match log_level {
        LogLevel::Error => log::LevelFilter::Error,
        LogLevel::Info => log::LevelFilter::Info,
    }
}

#[cfg(test)]
mod tests {
    use std::sync::{Arc, Mutex};

    use anyhow::Result;
    use tempfile::tempdir;

    use super::{AppService, ApplyOptions, SsidResolution, resolve_target_profile};
    use crate::adapters::network::SsidProvider;
    use crate::adapters::proxy::ProxyBackend;
    use crate::domain::models::{
        AppSettings, ElevatedServiceAction, ProxyProfile, ProxySettings, ServiceStartup,
        ServiceState, ServiceStatus, SsidProfileMapping, UnknownSsidAction, WindowsProxyTargets,
    };
    use crate::service::controller::ServiceController;
    use crate::storage::config_store::ConfigStore;

    struct MockProxyBackend {
        applied: Arc<Mutex<Vec<ProxySettings>>>,
        current: ProxySettings,
    }

    impl ProxyBackend for MockProxyBackend {
        fn current_settings(&self) -> Result<ProxySettings> {
            Ok(self.current.clone())
        }

        fn apply_settings(
            &self,
            settings: &ProxySettings,
            _app_settings: &AppSettings,
        ) -> Result<()> {
            self.applied.lock().unwrap().push(settings.clone());
            Ok(())
        }

        fn disable(&self, _app_settings: &AppSettings) -> Result<()> {
            self.applied.lock().unwrap().push(ProxySettings::default());
            Ok(())
        }

        fn backend_name(&self) -> &'static str {
            "mock"
        }
    }

    struct MockSsidProvider;

    impl SsidProvider for MockSsidProvider {
        fn current_ssid(&self) -> Result<Option<String>> {
            Ok(Some("OfficeWiFi".to_string()))
        }

        fn provider_name(&self) -> &'static str {
            "mock"
        }
    }

    struct MockServiceController;

    impl ServiceController for MockServiceController {
        fn start(&self) -> Result<()> {
            Ok(())
        }

        fn stop(&self) -> Result<()> {
            Ok(())
        }

        fn install(&self, _startup: ServiceStartup) -> Result<()> {
            Ok(())
        }

        fn uninstall(&self) -> Result<()> {
            Ok(())
        }

        fn set_startup(&self, _startup: ServiceStartup) -> Result<()> {
            Ok(())
        }

        fn run_elevated(&self, _action: ElevatedServiceAction) -> Result<()> {
            Ok(())
        }

        fn status(&self) -> Result<ServiceStatus> {
            Ok(ServiceStatus {
                installed: false,
                state: ServiceState::Stopped,
                startup: ServiceStartup::Unknown,
                detail: "mock".to_string(),
            })
        }
    }

    #[test]
    fn resolve_ssid_uses_mapping_first() {
        let mut state = crate::domain::models::AppState::new();
        state.ssid_mappings.push(SsidProfileMapping {
            ssid: "OfficeWiFi".to_string(),
            profile_name: "office".to_string(),
        });
        state.settings.unknown_ssid_action = UnknownSsidAction::ApplyDefaultProfile;
        state.settings.default_profile = Some("fallback".to_string());

        assert_eq!(
            resolve_target_profile(&state, Some("OfficeWiFi")),
            SsidResolution::ApplyProfile("office".to_string())
        );
    }

    #[test]
    fn resolve_ssid_uses_default_when_requested() {
        let mut state = crate::domain::models::AppState::new();
        state.settings = AppSettings {
            unknown_ssid_action: UnknownSsidAction::ApplyDefaultProfile,
            default_profile: Some("fallback".to_string()),
            ..AppSettings::default()
        };

        assert_eq!(
            resolve_target_profile(&state, Some("Unknown")),
            SsidResolution::ApplyProfile("fallback".to_string())
        );
    }

    #[test]
    fn save_and_apply_profile() {
        let temp_dir = tempdir().unwrap();
        let store = ConfigStore::from_paths(
            temp_dir.path().join("config.json"),
            temp_dir.path().join("profiles.json"),
            temp_dir.path().join("proxyz.log"),
            temp_dir.path().join("runtime"),
        );
        let applied = Arc::new(Mutex::new(Vec::new()));
        let service = AppService::new(
            store.clone(),
            Arc::new(MockProxyBackend {
                applied: Arc::clone(&applied),
                current: ProxySettings {
                    enabled: true,
                    http: Some("proxy.local:8080".to_string()),
                    https: None,
                    all: None,
                    bypass: vec![],
                },
            }),
            Arc::new(MockSsidProvider),
            Arc::new(MockServiceController),
        );

        service
            .save_current_profile("office", Some("memo".to_string()))
            .unwrap();
        let outcome = service
            .set_profile(
                "office",
                &ApplyOptions {
                    assume_yes: true,
                    dry_run: false,
                    skip_commands: false,
                },
            )
            .unwrap();

        assert_eq!(outcome.profile_name, "office");
        assert_eq!(applied.lock().unwrap().len(), 1);
    }

    #[test]
    fn invalid_default_profile_is_rejected() {
        let result = super::validate_settings(
            &AppSettings {
                default_profile: Some("missing".to_string()),
                unknown_ssid_action: UnknownSsidAction::ApplyDefaultProfile,
                ..AppSettings::default()
            },
            &[ProxyProfile {
                name: "office".to_string(),
                proxy: ProxySettings::default(),
                memo: String::new(),
                post_apply_commands: Vec::new(),
            }],
        );
        assert!(result.is_err());
    }

    #[cfg(windows)]
    #[test]
    fn missing_windows_proxy_targets_is_rejected() {
        let result = super::validate_settings(
            &AppSettings {
                windows_proxy_targets: WindowsProxyTargets {
                    wininet: false,
                    winhttp: false,
                    env_vars: false,
                },
                ..AppSettings::default()
            },
            &[],
        );
        assert!(result.is_err());
    }
}

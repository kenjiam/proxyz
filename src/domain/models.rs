use chrono::{DateTime, Local};
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub struct ProxyProfile {
    pub name: String,
    pub proxy: ProxySettings,
    pub memo: String,
    pub post_apply_commands: Vec<ExternalCommand>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub struct ProxySettings {
    pub enabled: bool,
    pub http: Option<String>,
    pub https: Option<String>,
    pub all: Option<String>,
    pub bypass: Vec<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub struct ExternalCommand {
    pub program: String,
    pub args: Vec<String>,
    pub note: String,
    pub requires_confirmation: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub struct SsidProfileMapping {
    pub ssid: String,
    pub profile_name: String,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub enum UnknownSsidAction {
    Ignore,
    ApplyDefaultProfile,
}

impl Default for UnknownSsidAction {
    fn default() -> Self {
        Self::Ignore
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub enum LogLevel {
    Error,
    Info,
}

impl Default for LogLevel {
    fn default() -> Self {
        Self::Info
    }
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub enum UiLanguage {
    Japanese,
    English,
}

impl Default for UiLanguage {
    fn default() -> Self {
        Self::Japanese
    }
}

impl UiLanguage {
    pub fn label(self) -> &'static str {
        match self {
            Self::Japanese => "日本語",
            Self::English => "English",
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub struct WindowsProxyTargets {
    pub wininet: bool,
    pub winhttp: bool,
    pub env_vars: bool,
}

impl Default for WindowsProxyTargets {
    fn default() -> Self {
        Self {
            wininet: true,
            winhttp: false,
            env_vars: true,
        }
    }
}

impl WindowsProxyTargets {
    pub fn enabled_count(&self) -> usize {
        [self.wininet, self.winhttp, self.env_vars]
            .into_iter()
            .filter(|enabled| *enabled)
            .count()
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub struct AppSettings {
    pub unknown_ssid_action: UnknownSsidAction,
    pub default_profile: Option<String>,
    pub log_level: LogLevel,
    pub poll_interval_secs: u64,
    pub skip_command_confirmation: bool,
    pub command_timeout_secs: u64,
    #[serde(default)]
    pub ui_language: UiLanguage,
    #[serde(default)]
    pub windows_proxy_targets: WindowsProxyTargets,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            unknown_ssid_action: UnknownSsidAction::Ignore,
            default_profile: None,
            log_level: LogLevel::Info,
            poll_interval_secs: 10,
            skip_command_confirmation: false,
            command_timeout_secs: 30,
            ui_language: UiLanguage::Japanese,
            windows_proxy_targets: WindowsProxyTargets::default(),
        }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub struct ProfileCatalog {
    pub schema_version: u32,
    pub profiles: Vec<ProxyProfile>,
    pub ssid_mappings: Vec<SsidProfileMapping>,
}

impl ProfileCatalog {
    pub fn new() -> Self {
        Self {
            schema_version: 1,
            profiles: Vec::new(),
            ssid_mappings: Vec::new(),
        }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub struct AppState {
    pub schema_version: u32,
    pub profiles: Vec<ProxyProfile>,
    pub ssid_mappings: Vec<SsidProfileMapping>,
    pub settings: AppSettings,
}

impl AppState {
    pub fn new() -> Self {
        Self {
            schema_version: 1,
            profiles: Vec::new(),
            ssid_mappings: Vec::new(),
            settings: AppSettings::default(),
        }
    }

    pub fn from_parts(settings: AppSettings, catalog: ProfileCatalog) -> Self {
        Self {
            schema_version: catalog.schema_version,
            profiles: catalog.profiles,
            ssid_mappings: catalog.ssid_mappings,
            settings,
        }
    }

    pub fn find_profile(&self, name: &str) -> Option<&ProxyProfile> {
        self.profiles.iter().find(|profile| profile.name == name)
    }

    pub fn find_profile_mut(&mut self, name: &str) -> Option<&mut ProxyProfile> {
        self.profiles
            .iter_mut()
            .find(|profile| profile.name == name)
    }

    pub fn remove_profile(&mut self, name: &str) -> bool {
        let before = self.profiles.len();
        self.profiles.retain(|profile| profile.name != name);
        self.ssid_mappings
            .retain(|mapping| mapping.profile_name != name);
        if self.settings.default_profile.as_deref() == Some(name) {
            self.settings.default_profile = None;
        }
        before != self.profiles.len()
    }
}

impl From<&AppState> for ProfileCatalog {
    fn from(state: &AppState) -> Self {
        Self {
            schema_version: state.schema_version,
            profiles: state.profiles.clone(),
            ssid_mappings: state.ssid_mappings.clone(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CommandExecutionRecord {
    pub program: String,
    pub args: Vec<String>,
    pub success: bool,
    pub exit_code: Option<i32>,
    pub summary: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ApplyOutcome {
    pub profile_name: String,
    pub dry_run: bool,
    pub command_reports: Vec<CommandExecutionRecord>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ServiceState {
    Running,
    Stopped,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ServiceStartup {
    Automatic,
    Manual,
    Disabled,
    Unknown,
}

impl ServiceStartup {
    pub fn label(self) -> &'static str {
        match self {
            Self::Automatic => "Automatic",
            Self::Manual => "Manual",
            Self::Disabled => "Disabled",
            Self::Unknown => "Unknown",
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ElevatedServiceAction {
    Install(ServiceStartup),
    Uninstall,
    Start,
    Stop,
    SetStartup(ServiceStartup),
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ServiceStatus {
    pub installed: bool,
    pub state: ServiceState,
    pub startup: ServiceStartup,
    pub detail: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SystemSnapshot {
    pub current_proxy: ProxySettings,
    pub current_ssid: Option<String>,
    pub service_status: ServiceStatus,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MonitorOutcome {
    pub observed_ssid: Option<String>,
    pub action_summary: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ValidationError {
    pub field: String,
    pub message: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TimestampedMessage {
    pub timestamp: DateTime<Local>,
    pub message: String,
    pub is_error: bool,
}

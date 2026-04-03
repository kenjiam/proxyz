use std::fs;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::Result;
use crossbeam_channel::{Receiver, Sender, unbounded};
use eframe::egui::{
    self, Color32, FontData, FontDefinitions, FontFamily, FontId, RichText, Style, TextStyle,
    Visuals,
};

use crate::application::services::{AppService, ApplyOptions, collect_profile_validation_errors};
use crate::domain::models::{
    AppSettings, AppState, ElevatedServiceAction, LogLevel, ProfileCatalog, ProxyProfile,
    ProxySettings, ServiceStartup, SsidProfileMapping, SystemSnapshot, TimestampedMessage,
    UiLanguage, UnknownSsidAction, WindowsProxyTargets,
};
#[cfg(windows)]
use crate::gui::tray::{TrayCommand, TrayEvent, TrayHandle, start as start_tray};
#[cfg(not(windows))]
struct TrayHandle;

pub fn launch(service: Arc<AppService>) -> Result<()> {
    let native_options = eframe::NativeOptions::default();
    eframe::run_native(
        "proxyz",
        native_options,
        Box::new(move |creation_context| {
            install_fonts(&creation_context.egui_ctx);
            install_style(&creation_context.egui_ctx);
            #[cfg(windows)]
            let tray = start_tray(Arc::clone(&service), creation_context.egui_ctx.clone()).ok();
            #[cfg(not(windows))]
            let tray = None;
            Ok(Box::new(ProxyzGui::new(Arc::clone(&service), tray)))
        }),
    )
    .map_err(|error| anyhow::anyhow!(error.to_string()))
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Tab {
    Dashboard,
    Profiles,
    Mappings,
    Service,
    Settings,
}

enum UiEvent {
    Refresh(Result<(ProfileCatalog, Option<SystemSnapshot>)>),
    SaveProfile(Result<()>),
    DeleteProfile(Result<()>),
    ApplyProfile(Result<String>),
    SaveMappings(Result<()>),
    SaveSettings(Result<()>),
    ServiceStart(Result<()>),
    ServiceStop(Result<()>),
    ServiceInstall(Result<()>),
    ServiceUninstall(Result<()>),
    ServiceStartupUpdate(Result<()>),
}

struct ProxyzGui {
    service: Arc<AppService>,
    tx: Sender<UiEvent>,
    rx: Receiver<UiEvent>,
    tab: Tab,
    state: AppState,
    snapshot: Option<SystemSnapshot>,
    selected_profile: Option<String>,
    profile_editor: ProfileEditor,
    mapping_rows: Vec<SsidProfileMapping>,
    settings_form: AppSettings,
    service_startup_selection: ServiceStartup,
    busy_message: Option<String>,
    notices: Vec<TimestampedMessage>,
    last_refresh: Instant,
    tray: Option<TrayHandle>,
    quit_requested: bool,
}

impl ProxyzGui {
    fn new(service: Arc<AppService>, tray: Option<TrayHandle>) -> Self {
        let (tx, rx) = unbounded();
        let initial_state = service.load_state().unwrap_or_else(|_| AppState::new());
        let mut app = Self {
            service,
            tx,
            rx,
            tab: Tab::Dashboard,
            state: initial_state.clone(),
            snapshot: None,
            selected_profile: None,
            profile_editor: ProfileEditor::default(),
            mapping_rows: Vec::new(),
            settings_form: initial_state.settings,
            service_startup_selection: ServiceStartup::Automatic,
            busy_message: Some("状態を読み込んでいます...".to_string()),
            notices: Vec::new(),
            last_refresh: Instant::now() - Duration::from_secs(60),
            tray,
            quit_requested: false,
        };
        app.refresh();
        app
    }

    fn language(&self) -> UiLanguage {
        self.settings_form.ui_language
    }

    fn tr<'a>(&self, ja: &'a str, en: &'a str) -> &'a str {
        match self.language() {
            UiLanguage::Japanese => ja,
            UiLanguage::English => en,
        }
    }

    fn refresh(&mut self) {
        self.busy_message = Some(
            self.tr("更新しています...", "Refreshing state...")
                .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = (|| {
                let catalog = service.load_profile_catalog()?;
                let snapshot = service.system_snapshot().ok();
                Ok((catalog, snapshot))
            })();
            let _ = tx.send(UiEvent::Refresh(result));
        });
    }

    fn push_notice(&mut self, message: impl Into<String>, is_error: bool) {
        self.notices.push(TimestampedMessage {
            timestamp: chrono::Local::now(),
            message: message.into(),
            is_error,
        });
        if self.notices.len() > 5 {
            self.notices.remove(0);
        }
    }

    fn spawn_profile_save(&mut self) {
        let profile = self.profile_editor.to_profile();
        self.busy_message = Some(
            self.tr("プロファイルを保存しています...", "Saving profile...")
                .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = service.upsert_profile(profile);
            let _ = tx.send(UiEvent::SaveProfile(result));
        });
    }

    fn spawn_profile_delete(&mut self, profile_name: String) {
        self.busy_message = Some(
            self.tr("プロファイルを削除しています...", "Deleting profile...")
                .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = service.delete_profile(&profile_name);
            let _ = tx.send(UiEvent::DeleteProfile(result));
        });
    }

    fn spawn_profile_apply(&mut self, profile_name: String) {
        self.busy_message = Some(
            self.tr("プロファイルを適用しています...", "Applying profile...")
                .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = service
                .set_profile(
                    &profile_name,
                    &ApplyOptions {
                        assume_yes: true,
                        dry_run: false,
                        skip_commands: false,
                    },
                )
                .map(|outcome| outcome.profile_name);
            let _ = tx.send(UiEvent::ApplyProfile(result));
        });
    }

    fn spawn_save_mappings(&mut self) {
        let mappings = self.mapping_rows.clone();
        self.busy_message = Some(
            self.tr(
                "SSID マッピングを保存しています...",
                "Saving SSID mappings...",
            )
            .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = service.replace_mappings(mappings);
            let _ = tx.send(UiEvent::SaveMappings(result));
        });
    }

    fn spawn_save_settings(&mut self) {
        let settings = self.settings_form.clone();
        self.busy_message = Some(
            self.tr("設定を保存しています...", "Saving settings...")
                .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = service.update_settings(settings);
            let _ = tx.send(UiEvent::SaveSettings(result));
        });
    }

    fn persist_language_selection(&mut self) {
        let settings = self.settings_form.clone();
        match self.service.update_settings(settings) {
            Ok(()) => {
                self.state.settings = self.settings_form.clone();
            }
            Err(error) => {
                self.settings_form.ui_language = self.state.settings.ui_language;
                self.push_notice(format!("{error:#}"), true);
            }
        }
    }

    fn spawn_service_start(&mut self) {
        self.busy_message = Some(
            self.tr(
                "サービスを開始しています...",
                "Starting background service...",
            )
            .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = if cfg!(windows) {
                service.service_run_elevated(ElevatedServiceAction::Start)
            } else {
                service.service_start()
            };
            let _ = tx.send(UiEvent::ServiceStart(result));
        });
    }

    fn spawn_service_stop(&mut self) {
        self.busy_message = Some(
            self.tr(
                "サービスを停止しています...",
                "Stopping background service...",
            )
            .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = if cfg!(windows) {
                service.service_run_elevated(ElevatedServiceAction::Stop)
            } else {
                service.service_stop()
            };
            let _ = tx.send(UiEvent::ServiceStop(result));
        });
    }

    fn spawn_service_install(&mut self) {
        self.busy_message = Some(
            self.tr(
                "サービス登録を要求しています...",
                "Requesting service registration...",
            )
            .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        let startup = self.service_startup_selection;
        std::thread::spawn(move || {
            let result = if cfg!(windows) {
                service.service_run_elevated(ElevatedServiceAction::Install(startup))
            } else {
                service.service_install(startup)
            };
            let _ = tx.send(UiEvent::ServiceInstall(result));
        });
    }

    fn spawn_service_uninstall(&mut self) {
        self.busy_message = Some(
            self.tr(
                "サービス登録解除を要求しています...",
                "Requesting service unregistration...",
            )
            .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        std::thread::spawn(move || {
            let result = if cfg!(windows) {
                service.service_run_elevated(ElevatedServiceAction::Uninstall)
            } else {
                service.service_uninstall()
            };
            let _ = tx.send(UiEvent::ServiceUninstall(result));
        });
    }

    fn spawn_service_set_startup(&mut self) {
        self.busy_message = Some(
            self.tr(
                "サービス起動設定を更新しています...",
                "Updating service startup mode...",
            )
            .to_string(),
        );
        let tx = self.tx.clone();
        let service = Arc::clone(&self.service);
        let startup = self.service_startup_selection;
        std::thread::spawn(move || {
            let result = if cfg!(windows) {
                service.service_run_elevated(ElevatedServiceAction::SetStartup(startup))
            } else {
                service.service_set_startup(startup)
            };
            let _ = tx.send(UiEvent::ServiceStartupUpdate(result));
        });
    }

    fn consume_events(&mut self) {
        while let Ok(event) = self.rx.try_recv() {
            self.busy_message = None;
            match event {
                UiEvent::Refresh(result) => match result {
                    Ok((catalog, snapshot)) => {
                        self.snapshot = snapshot;
                        self.state.schema_version = catalog.schema_version;
                        self.state.profiles = catalog.profiles.clone();
                        self.state.ssid_mappings = catalog.ssid_mappings.clone();
                        self.mapping_rows = catalog.ssid_mappings.clone();
                        if let Some(snapshot) = &self.snapshot {
                            if snapshot.service_status.installed
                                && !matches!(
                                    snapshot.service_status.startup,
                                    ServiceStartup::Unknown
                                )
                            {
                                self.service_startup_selection = snapshot.service_status.startup;
                            }
                        }
                        if let Some(selected) = self.selected_profile.clone() {
                            if let Some(profile) = self.state.find_profile(&selected) {
                                self.profile_editor = ProfileEditor::from_profile(profile);
                            }
                        } else if let Some(first) = self.state.profiles.first() {
                            self.selected_profile = Some(first.name.clone());
                            self.profile_editor = ProfileEditor::from_profile(first);
                        }
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::SaveProfile(result) => match result {
                    Ok(()) => {
                        self.push_notice(
                            self.tr("プロファイルを保存しました", "Profile saved"),
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::DeleteProfile(result) => match result {
                    Ok(()) => {
                        self.selected_profile = None;
                        self.profile_editor = ProfileEditor::default();
                        self.push_notice(
                            self.tr("プロファイルを削除しました", "Profile deleted"),
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::ApplyProfile(result) => match result {
                    Ok(name) => {
                        self.push_notice(
                            match self.language() {
                                UiLanguage::Japanese => {
                                    format!("プロファイル '{name}' を適用しました")
                                }
                                UiLanguage::English => format!("Applied profile '{name}'"),
                            },
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::SaveMappings(result) => match result {
                    Ok(()) => {
                        self.push_notice(
                            self.tr("SSID マッピングを保存しました", "SSID mappings saved"),
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::SaveSettings(result) => match result {
                    Ok(()) => {
                        self.state.settings = self.settings_form.clone();
                        self.push_notice(self.tr("設定を保存しました", "Settings saved"), false);
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::ServiceStart(result) => match result {
                    Ok(()) => {
                        self.push_notice(
                            self.tr("サービスを開始しました", "Service started"),
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::ServiceStop(result) => match result {
                    Ok(()) => {
                        self.push_notice(
                            self.tr("サービスを停止しました", "Service stopped"),
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::ServiceInstall(result) => match result {
                    Ok(()) => {
                        self.push_notice(
                            self.tr(
                                "サービス登録を要求しました",
                                "Service registration requested",
                            ),
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::ServiceUninstall(result) => match result {
                    Ok(()) => {
                        self.push_notice(
                            self.tr(
                                "サービス登録解除を要求しました",
                                "Service unregistration requested",
                            ),
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
                UiEvent::ServiceStartupUpdate(result) => match result {
                    Ok(()) => {
                        self.push_notice(
                            self.tr(
                                "サービス起動設定を更新しました",
                                "Service startup setting updated",
                            ),
                            false,
                        );
                        self.refresh();
                    }
                    Err(error) => self.push_notice(format!("{error:#}"), true),
                },
            }
        }
    }

    fn draw_top_bar(&mut self, ui: &mut egui::Ui) {
        egui::Frame::new()
            .inner_margin(egui::Margin::symmetric(12, 10))
            .show(ui, |ui| {
                ui.horizontal_wrapped(|ui| {
                    for (tab, label) in [
                        (Tab::Dashboard, self.tr("ダッシュボード", "Dashboard")),
                        (Tab::Profiles, self.tr("プロファイル", "Profiles")),
                        (Tab::Mappings, "SSID"),
                        (Tab::Service, self.tr("サービス", "Service")),
                        (Tab::Settings, self.tr("設定", "Settings")),
                    ] {
                        let selected = self.tab == tab;
                        let text_color = if selected {
                            ui.visuals().panel_fill
                        } else {
                            ui.visuals().widgets.inactive.fg_stroke.color
                        };
                        let fill = if selected {
                            ui.visuals().selection.bg_fill
                        } else {
                            ui.visuals().widgets.inactive.weak_bg_fill
                        };
                        let button =
                            egui::Button::new(RichText::new(label).color(text_color).strong())
                                .fill(fill);
                        if ui.add_sized([120.0, 34.0], button).clicked() {
                            self.tab = tab;
                        }
                    }
                    ui.add_space(10.0);
                    if ui.button(self.tr("更新", "Refresh")).clicked() {
                        self.refresh();
                    }
                    if let Some(message) = &self.busy_message {
                        ui.label(RichText::new(message).italics());
                        ui.spinner();
                    }
                });
            });
    }

    #[cfg(windows)]
    fn consume_tray_events(&mut self, ctx: &egui::Context) {
        let Some(tray) = &self.tray else {
            return;
        };
        let events = tray.events.try_iter().collect::<Vec<_>>();

        for event in events {
            match event {
                TrayEvent::ShowWindow => {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));
                    ctx.send_viewport_cmd(egui::ViewportCommand::Minimized(false));
                    ctx.send_viewport_cmd(egui::ViewportCommand::Focus);
                    self.refresh();
                }
                TrayEvent::QuitRequested => {
                    self.quit_requested = true;
                    ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                }
            }
        }
    }

    fn draw_notices(&mut self, ui: &mut egui::Ui) {
        for notice in &self.notices {
            let color = if notice.is_error {
                Color32::from_rgb(180, 40, 40)
            } else {
                Color32::from_rgb(30, 120, 60)
            };
            ui.colored_label(
                color,
                format!(
                    "[{}] {}",
                    notice.timestamp.format("%H:%M:%S"),
                    notice.message
                ),
            );
        }
    }

    fn draw_dashboard(&mut self, ui: &mut egui::Ui) {
        let snapshot = self.snapshot.clone();
        ui.heading(self.tr("現在の状態", "Current State"));
        ui.separator();
        if let Some(snapshot) = snapshot {
            ui.label(format!(
                "{}: {}",
                self.tr("SSID", "SSID"),
                snapshot
                    .current_ssid
                    .unwrap_or_else(|| self.tr("未接続", "Not connected").to_string())
            ));
            ui.label(format!(
                "{}: {}",
                self.tr("サービス", "Service"),
                service_state_label(self.language(), snapshot.service_status.state)
            ));
            ui.label(format!(
                "{}: {}",
                self.tr("HTTP", "HTTP"),
                snapshot
                    .current_proxy
                    .http
                    .or(snapshot.current_proxy.all.clone())
                    .unwrap_or_else(|| self.tr("未設定", "Not configured").to_string())
            ));
            ui.label(format!(
                "{}: {}",
                self.tr("HTTPS", "HTTPS"),
                snapshot
                    .current_proxy
                    .https
                    .or(snapshot.current_proxy.all.clone())
                    .unwrap_or_else(|| self.tr("未設定", "Not configured").to_string())
            ));
            ui.label(format!(
                "{}: {}",
                self.tr("除外", "Bypass"),
                if snapshot.current_proxy.bypass.is_empty() {
                    self.tr("なし", "None").to_string()
                } else {
                    snapshot.current_proxy.bypass.join(", ")
                }
            ));
        } else {
            ui.label(self.tr(
                "状態はまだ取得できていません。",
                "Snapshot is not available yet.",
            ));
        }

        ui.separator();
        ui.heading(self.tr("保存データ", "Storage"));
        ui.label(format!(
            "{}: {} / {}: {}",
            self.tr("プロファイル", "Profiles"),
            self.state.profiles.len(),
            self.tr("SSID マッピング", "SSID mappings"),
            self.state.ssid_mappings.len()
        ));
        ui.label(format!(
            "{}: {}",
            self.tr("バックエンド", "Backends"),
            self.service.backend_summary()
        ));
    }

    fn draw_profiles(&mut self, ui: &mut egui::Ui) {
        ui.columns(2, |columns| {
            columns[0].heading(self.tr("プロファイル", "Profiles"));
            egui::ScrollArea::vertical()
                .max_height(480.0)
                .show(&mut columns[0], |ui| {
                    for profile in &self.state.profiles {
                        if ui
                            .selectable_label(
                                self.selected_profile.as_deref() == Some(profile.name.as_str()),
                                &profile.name,
                            )
                            .clicked()
                        {
                            self.selected_profile = Some(profile.name.clone());
                            self.profile_editor = ProfileEditor::from_profile(profile);
                        }
                    }
                });
            if columns[0]
                .button(self.tr("新規プロファイル", "New profile"))
                .clicked()
            {
                self.selected_profile = None;
                self.profile_editor = ProfileEditor::default();
            }

            columns[1].heading(self.tr("編集", "Editor"));
            self.profile_editor.draw(&mut columns[1], self.language());

            let draft_profile = self.profile_editor.to_profile();
            let validation_errors = collect_profile_validation_errors(&draft_profile);
            if !validation_errors.is_empty() {
                for error in validation_errors {
                    columns[1].colored_label(
                        Color32::from_rgb(180, 40, 40),
                        format!("{}: {}", error.field, error.message),
                    );
                }
            }

            columns[1].horizontal(|ui| {
                if ui.button(self.tr("保存", "Save")).clicked() {
                    self.spawn_profile_save();
                }
                if ui.button(self.tr("適用", "Apply")).clicked() {
                    self.spawn_profile_apply(draft_profile.name.clone());
                }
                if let Some(selected) = self.selected_profile.clone() {
                    if ui.button(self.tr("削除", "Delete")).clicked() {
                        self.spawn_profile_delete(selected);
                    }
                }
            });
        });
    }

    fn draw_mappings(&mut self, ui: &mut egui::Ui) {
        let select_profile_label = self.tr("プロファイルを選択", "Select profile");
        let remove_label = self.tr("削除", "Remove");
        let add_mapping_label = self.tr("マッピングを追加", "Add mapping");
        let save_mappings_label = self.tr("マッピングを保存", "Save mappings");
        ui.heading(self.tr("SSID マッピング", "SSID Mappings"));
        ui.separator();
        let profile_names = self
            .state
            .profiles
            .iter()
            .map(|profile| profile.name.clone())
            .collect::<Vec<_>>();

        egui::Grid::new("ssid-mappings-grid")
            .striped(true)
            .show(ui, |ui| {
                ui.label(self.tr("SSID", "SSID"));
                ui.label(self.tr("プロファイル", "Profile"));
                ui.end_row();

                let mut remove_index = None;
                for (index, row) in self.mapping_rows.iter_mut().enumerate() {
                    ui.text_edit_singleline(&mut row.ssid);
                    egui::ComboBox::from_id_salt(format!("profile-map-{index}"))
                        .selected_text(if row.profile_name.is_empty() {
                            select_profile_label
                        } else {
                            &row.profile_name
                        })
                        .show_ui(ui, |ui| {
                            for profile_name in &profile_names {
                                ui.selectable_value(
                                    &mut row.profile_name,
                                    profile_name.clone(),
                                    profile_name,
                                );
                            }
                        });
                    if ui.button(remove_label).clicked() {
                        remove_index = Some(index);
                    }
                    ui.end_row();
                }
                if let Some(index) = remove_index {
                    self.mapping_rows.remove(index);
                }
            });

        ui.horizontal(|ui| {
            if ui.button(add_mapping_label).clicked() {
                self.mapping_rows.push(SsidProfileMapping::default());
            }
            if ui.button(save_mappings_label).clicked() {
                self.spawn_save_mappings();
            }
        });
    }

    fn draw_service(&mut self, ui: &mut egui::Ui) {
        ui.heading(self.tr("サービス制御", "Service Control"));
        ui.separator();
        if let Some(snapshot) = &self.snapshot {
            ui.label(format!(
                "{}: {}",
                self.tr("登録済み", "Installed"),
                if snapshot.service_status.installed {
                    self.tr("はい", "Yes")
                } else {
                    self.tr("いいえ", "No")
                }
            ));
            ui.label(format!(
                "{}: {}",
                self.tr("状態", "State"),
                service_state_label(self.language(), snapshot.service_status.state)
            ));
            ui.label(format!(
                "{}: {}",
                self.tr("起動時動作", "Startup at boot"),
                service_startup_label(self.language(), snapshot.service_status.startup)
            ));
            ui.label(format!(
                "{}: {}",
                self.tr("詳細", "Detail"),
                snapshot.service_status.detail
            ));
        } else {
            ui.label(self.tr(
                "サービス状態を取得できません。",
                "Service state unavailable",
            ));
        }

        if cfg!(windows) {
            ui.separator();
            ui.heading(self.tr("Windows サービス登録", "Windows Service Registration"));
            let mut start_at_boot =
                matches!(self.service_startup_selection, ServiceStartup::Automatic);
            if ui
                .checkbox(
                    &mut start_at_boot,
                    self.tr(
                        "スタートアップ時にサービスを起動する",
                        "Start service automatically at sign-in",
                    ),
                )
                .changed()
            {
                self.service_startup_selection = if start_at_boot {
                    ServiceStartup::Automatic
                } else {
                    ServiceStartup::Manual
                };
            }
            ui.horizontal(|ui| {
                if ui
                    .button(self.tr("サービスを登録する (UAC)", "Register service (UAC)"))
                    .clicked()
                {
                    self.spawn_service_install();
                }
                if ui
                    .button(self.tr("サービス登録を解除する (UAC)", "Unregister service (UAC)"))
                    .clicked()
                {
                    self.spawn_service_uninstall();
                }
            });
            ui.horizontal(|ui| {
                if ui
                    .button(self.tr("起動設定を反映する (UAC)", "Apply startup mode (UAC)"))
                    .clicked()
                {
                    self.spawn_service_set_startup();
                }
                ui.label(format!(
                    "{}: {}",
                    self.tr("選択中", "Selected"),
                    service_startup_label(self.language(), self.service_startup_selection)
                ));
            });
            ui.small(self.tr(
                "Windows ではサービス登録と設定変更時に UAC が表示されます。",
                "Windows shows UAC for service registration and startup changes.",
            ));
        }

        ui.horizontal(|ui| {
            let start_label = if cfg!(windows) {
                self.tr("サービスを開始する (UAC)", "Start service (UAC)")
            } else {
                self.tr("開始", "Start")
            };
            if ui.button(start_label).clicked() {
                self.spawn_service_start();
            }
            let stop_label = if cfg!(windows) {
                self.tr("サービスを停止する (UAC)", "Stop service (UAC)")
            } else {
                self.tr("停止", "Stop")
            };
            if ui.button(stop_label).clicked() {
                self.spawn_service_stop();
            }
        });
    }

    fn draw_settings(&mut self, ui: &mut egui::Ui) {
        let ignore_label = self.tr("何もしない", "Ignore");
        let default_profile_action_label =
            self.tr("既定プロファイルを適用", "Apply default profile");
        let none_label = self.tr("なし", "None");
        let info_label = self.tr("Info", "Info");
        let error_label = self.tr("Error", "Error");
        let poll_interval_label = self.tr("SSID 監視間隔 (秒)", "SSID poll interval (sec)");
        let command_timeout_label = self.tr("コマンドタイムアウト (秒)", "Command timeout (sec)");
        let skip_confirmation_label = self.tr(
            "危険なコマンドの確認を省略する",
            "Skip confirmation for commands marked as dangerous",
        );
        ui.heading(self.tr("設定", "Settings"));
        ui.add_space(6.0);
        ui.label(self.tr(
            "アプリ設定と Windows への反映先をここで管理します。",
            "Manage app behavior and Windows proxy targets here.",
        ));
        ui.add_space(10.0);

        ui.group(|ui| {
            ui.heading(self.tr("自動切替", "Automation"));
            ui.separator();
            ui.horizontal(|ui| {
                ui.label(self.tr("未対応 SSID", "Unknown SSID"));
                egui::ComboBox::from_id_salt("unknown-ssid-action")
                    .selected_text(match self.settings_form.unknown_ssid_action {
                        UnknownSsidAction::Ignore => ignore_label,
                        UnknownSsidAction::ApplyDefaultProfile => default_profile_action_label,
                    })
                    .show_ui(ui, |ui| {
                        ui.selectable_value(
                            &mut self.settings_form.unknown_ssid_action,
                            UnknownSsidAction::Ignore,
                            ignore_label,
                        );
                        ui.selectable_value(
                            &mut self.settings_form.unknown_ssid_action,
                            UnknownSsidAction::ApplyDefaultProfile,
                            default_profile_action_label,
                        );
                    });
            });

            ui.horizontal(|ui| {
                ui.label(self.tr("既定プロファイル", "Default profile"));
                egui::ComboBox::from_id_salt("default-profile")
                    .selected_text(
                        self.settings_form
                            .default_profile
                            .clone()
                            .unwrap_or_else(|| none_label.to_string()),
                    )
                    .show_ui(ui, |ui| {
                        ui.selectable_value(
                            &mut self.settings_form.default_profile,
                            None,
                            none_label,
                        );
                        for profile in &self.state.profiles {
                            ui.selectable_value(
                                &mut self.settings_form.default_profile,
                                Some(profile.name.clone()),
                                &profile.name,
                            );
                        }
                    });
            });

            ui.add(
                egui::Slider::new(&mut self.settings_form.poll_interval_secs, 1..=300)
                    .text(poll_interval_label),
            );
        });

        ui.add_space(10.0);
        ui.group(|ui| {
            ui.heading(self.tr("アプリ", "Application"));
            ui.separator();
            ui.horizontal(|ui| {
                ui.label(self.tr("ログレベル", "Log level"));
                egui::ComboBox::from_id_salt("log-level")
                    .selected_text(match self.settings_form.log_level {
                        LogLevel::Error => error_label,
                        LogLevel::Info => info_label,
                    })
                    .show_ui(ui, |ui| {
                        ui.selectable_value(
                            &mut self.settings_form.log_level,
                            LogLevel::Info,
                            info_label,
                        );
                        ui.selectable_value(
                            &mut self.settings_form.log_level,
                            LogLevel::Error,
                            error_label,
                        );
                    });
            });

            ui.horizontal(|ui| {
                let previous_language = self.settings_form.ui_language;
                ui.label(self.tr("表示言語", "Display language"));
                egui::ComboBox::from_id_salt("ui-language")
                    .selected_text(self.settings_form.ui_language.label())
                    .show_ui(ui, |ui| {
                        ui.selectable_value(
                            &mut self.settings_form.ui_language,
                            UiLanguage::Japanese,
                            UiLanguage::Japanese.label(),
                        );
                        ui.selectable_value(
                            &mut self.settings_form.ui_language,
                            UiLanguage::English,
                            UiLanguage::English.label(),
                        );
                    });
                if self.settings_form.ui_language != previous_language {
                    self.persist_language_selection();
                }
            });

            ui.add(
                egui::Slider::new(&mut self.settings_form.command_timeout_secs, 1..=300)
                    .text(command_timeout_label),
            );
            ui.checkbox(
                &mut self.settings_form.skip_command_confirmation,
                skip_confirmation_label,
            );
        });

        #[cfg(windows)]
        {
            ui.add_space(10.0);
            ui.group(|ui| {
                ui.heading(self.tr("Windows 反映先", "Windows Targets"));
                ui.separator();
                ui.label(self.tr(
                    "同じプロファイルをどの Windows プロキシ層へ書き込むかを選びます。",
                    "Choose which Windows proxy layers receive the selected profile.",
                ));
                ui.small(self.tr(
                    "現在値の読み取りは引き続き WinINet を基準にしています。",
                    "Current-state reads still use WinINet as the primary snapshot.",
                ));
                ui.add_space(8.0);
                let wininet_label = self.tr(
                    "WinINet (設定 > ネットワークとインターネット > プロキシ)",
                    "WinINet (Settings > Network & Internet > Proxy)",
                );
                let env_vars_label = self.tr(
                    "環境変数 (HTTP_PROXY / HTTPS_PROXY / ALL_PROXY / NO_PROXY)",
                    "Environment variables (HTTP_PROXY / HTTPS_PROXY / ALL_PROXY / NO_PROXY)",
                );
                let winhttp_label = self.tr(
                    "WinHTTP (netsh winhttp / Windows Update 系)",
                    "WinHTTP (netsh winhttp / Windows Update stack)",
                );
                ui.checkbox(
                    &mut self.settings_form.windows_proxy_targets.wininet,
                    wininet_label,
                );
                ui.checkbox(
                    &mut self.settings_form.windows_proxy_targets.env_vars,
                    env_vars_label,
                );
                ui.checkbox(
                    &mut self.settings_form.windows_proxy_targets.winhttp,
                    winhttp_label,
                );
                ui.add_space(4.0);
                ui.label(format!(
                    "{}: {}",
                    self.tr("選択中", "Selected"),
                    windows_targets_summary(
                        self.language(),
                        &self.settings_form.windows_proxy_targets
                    )
                ));
                if self.settings_form.windows_proxy_targets.enabled_count() == 0 {
                    ui.colored_label(
                        Color32::from_rgb(180, 40, 40),
                        self.tr(
                            "少なくとも 1 つの Windows 反映先を選択してください。",
                            "Select at least one Windows target.",
                        ),
                    );
                }
            });
        }

        ui.add_space(12.0);

        if ui.button(self.tr("設定を保存", "Save settings")).clicked() {
            self.spawn_save_settings();
        }
    }
}

impl eframe::App for ProxyzGui {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.consume_events();
        #[cfg(windows)]
        self.consume_tray_events(ctx);

        #[cfg(windows)]
        if self.tray.is_some()
            && !self.quit_requested
            && ctx.input(|input| input.viewport().close_requested())
        {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
        }

        if self.last_refresh.elapsed() > Duration::from_secs(30) && self.busy_message.is_none() {
            self.last_refresh = Instant::now();
            self.refresh();
        }

        egui::TopBottomPanel::top("top-panel").show(ctx, |ui| {
            self.draw_top_bar(ui);
        });

        egui::TopBottomPanel::bottom("bottom-panel").show(ctx, |ui| {
            self.draw_notices(ui);
        });

        egui::CentralPanel::default().show(ctx, |ui| match self.tab {
            Tab::Dashboard => self.draw_dashboard(ui),
            Tab::Profiles => self.draw_profiles(ui),
            Tab::Mappings => self.draw_mappings(ui),
            Tab::Service => self.draw_service(ui),
            Tab::Settings => self.draw_settings(ui),
        });
    }

    fn on_exit(&mut self, _gl: Option<&eframe::glow::Context>) {
        #[cfg(windows)]
        if let Some(tray) = &self.tray {
            let _ = tray.shutdown.send(TrayCommand::Shutdown);
        }
    }
}

#[derive(Default, Clone)]
struct ProfileEditor {
    name: String,
    memo: String,
    enabled: bool,
    http: String,
    https: String,
    all: String,
    bypass: String,
    commands: Vec<CommandEditor>,
}

impl ProfileEditor {
    fn from_profile(profile: &ProxyProfile) -> Self {
        Self {
            name: profile.name.clone(),
            memo: profile.memo.clone(),
            enabled: profile.proxy.enabled,
            http: profile.proxy.http.clone().unwrap_or_default(),
            https: profile.proxy.https.clone().unwrap_or_default(),
            all: profile.proxy.all.clone().unwrap_or_default(),
            bypass: profile.proxy.bypass.join("\n"),
            commands: profile
                .post_apply_commands
                .iter()
                .map(CommandEditor::from_domain)
                .collect(),
        }
    }

    fn to_profile(&self) -> ProxyProfile {
        ProxyProfile {
            name: self.name.trim().to_string(),
            memo: self.memo.clone(),
            proxy: ProxySettings {
                enabled: self.enabled,
                http: text_to_option(&self.http),
                https: text_to_option(&self.https),
                all: text_to_option(&self.all),
                bypass: self
                    .bypass
                    .lines()
                    .map(str::trim)
                    .filter(|line| !line.is_empty())
                    .map(ToString::to_string)
                    .collect(),
            },
            post_apply_commands: self.commands.iter().map(CommandEditor::to_domain).collect(),
        }
    }

    fn draw(&mut self, ui: &mut egui::Ui, language: UiLanguage) {
        ui.label(tr_lang(language, "名前", "Name"));
        ui.text_edit_singleline(&mut self.name);
        ui.label(tr_lang(language, "メモ", "Memo"));
        ui.text_edit_multiline(&mut self.memo);
        ui.checkbox(
            &mut self.enabled,
            tr_lang(language, "プロキシを有効化", "Enable proxy"),
        );
        ui.label(tr_lang(language, "HTTP プロキシ", "HTTP proxy"));
        ui.text_edit_singleline(&mut self.http);
        ui.label(tr_lang(language, "HTTPS プロキシ", "HTTPS proxy"));
        ui.text_edit_singleline(&mut self.https);
        ui.label(tr_lang(language, "共通プロキシ", "ALL proxy"));
        ui.text_edit_singleline(&mut self.all);
        ui.label(tr_lang(
            language,
            "除外一覧 (1 行 1 件)",
            "Bypass list (one entry per line)",
        ));
        ui.text_edit_multiline(&mut self.bypass);
        ui.separator();
        ui.heading(tr_lang(language, "適用後コマンド", "Post-apply commands"));

        let mut remove_index = None;
        for (index, command) in self.commands.iter_mut().enumerate() {
            ui.group(|ui| {
                ui.horizontal(|ui| {
                    ui.label(match language {
                        UiLanguage::Japanese => format!("コマンド {}", index + 1),
                        UiLanguage::English => format!("Command {}", index + 1),
                    });
                    if ui.button(tr_lang(language, "削除", "Remove")).clicked() {
                        remove_index = Some(index);
                    }
                });
                command.draw(ui, language);
            });
        }
        if let Some(index) = remove_index {
            self.commands.remove(index);
        }
        if ui
            .button(tr_lang(language, "コマンドを追加", "Add command"))
            .clicked()
        {
            self.commands.push(CommandEditor::default());
        }
    }
}

#[derive(Default, Clone)]
struct CommandEditor {
    program: String,
    args: String,
    note: String,
    requires_confirmation: bool,
}

impl CommandEditor {
    fn from_domain(command: &crate::domain::models::ExternalCommand) -> Self {
        Self {
            program: command.program.clone(),
            args: command.args.join("\n"),
            note: command.note.clone(),
            requires_confirmation: command.requires_confirmation,
        }
    }

    fn to_domain(&self) -> crate::domain::models::ExternalCommand {
        crate::domain::models::ExternalCommand {
            program: self.program.trim().to_string(),
            args: self
                .args
                .lines()
                .map(str::trim)
                .filter(|line| !line.is_empty())
                .map(ToString::to_string)
                .collect(),
            note: self.note.clone(),
            requires_confirmation: self.requires_confirmation,
        }
    }

    fn draw(&mut self, ui: &mut egui::Ui, language: UiLanguage) {
        ui.label(tr_lang(language, "プログラム", "Program"));
        ui.text_edit_singleline(&mut self.program);
        ui.label(tr_lang(
            language,
            "引数 (1 行 1 件)",
            "Arguments (one entry per line)",
        ));
        ui.text_edit_multiline(&mut self.args);
        ui.label(tr_lang(language, "メモ", "Note"));
        ui.text_edit_singleline(&mut self.note);
        ui.checkbox(
            &mut self.requires_confirmation,
            tr_lang(
                language,
                "確認必須 / 明示的な許可が必要",
                "Require confirmation / explicit opt-in",
            ),
        );
    }
}

fn text_to_option(value: &str) -> Option<String> {
    let trimmed = value.trim();
    (!trimmed.is_empty()).then(|| trimmed.to_string())
}

fn tr_lang<'a>(language: UiLanguage, ja: &'a str, en: &'a str) -> &'a str {
    match language {
        UiLanguage::Japanese => ja,
        UiLanguage::English => en,
    }
}

fn service_state_label(
    language: UiLanguage,
    state: crate::domain::models::ServiceState,
) -> &'static str {
    match state {
        crate::domain::models::ServiceState::Running => tr_lang(language, "実行中", "Running"),
        crate::domain::models::ServiceState::Stopped => tr_lang(language, "停止中", "Stopped"),
        crate::domain::models::ServiceState::Unknown => tr_lang(language, "不明", "Unknown"),
    }
}

fn service_startup_label(language: UiLanguage, startup: ServiceStartup) -> &'static str {
    match startup {
        ServiceStartup::Automatic => tr_lang(language, "自動", "Automatic"),
        ServiceStartup::Manual => tr_lang(language, "手動", "Manual"),
        ServiceStartup::Disabled => tr_lang(language, "無効", "Disabled"),
        ServiceStartup::Unknown => tr_lang(language, "不明", "Unknown"),
    }
}

fn windows_targets_summary(language: UiLanguage, targets: &WindowsProxyTargets) -> String {
    let mut labels = Vec::new();
    if targets.wininet {
        labels.push(tr_lang(language, "WinINet", "WinINet"));
    }
    if targets.env_vars {
        labels.push(tr_lang(language, "環境変数", "Environment"));
    }
    if targets.winhttp {
        labels.push(tr_lang(language, "WinHTTP", "WinHTTP"));
    }
    if labels.is_empty() {
        tr_lang(language, "未選択", "None").to_string()
    } else {
        labels.join(" / ")
    }
}

fn install_fonts(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();
    let loaded_fonts = load_ui_fonts();
    for (name, data) in &loaded_fonts {
        fonts
            .font_data
            .insert(name.clone(), FontData::from_owned(data.clone()).into());
    }

    let names = loaded_fonts
        .into_iter()
        .map(|(name, _)| name)
        .collect::<Vec<_>>();

    if !names.is_empty() {
        let (body_fonts, strong_fonts) = split_font_roles(&names);
        let proportional = fonts
            .families
            .get(&FontFamily::Proportional)
            .cloned()
            .unwrap_or_default();
        let monospace = fonts
            .families
            .get(&FontFamily::Monospace)
            .cloned()
            .unwrap_or_default();

        let mut proportional_family = body_fonts.clone();
        proportional_family.extend(proportional);
        fonts
            .families
            .insert(FontFamily::Proportional, proportional_family);

        let mut monospace_family = body_fonts.clone();
        monospace_family.extend(monospace);
        fonts
            .families
            .insert(FontFamily::Monospace, monospace_family);
        fonts.families.insert(
            FontFamily::Name(Arc::<str>::from("proxyz-body")),
            body_fonts,
        );
        fonts.families.insert(
            FontFamily::Name(Arc::<str>::from("proxyz-strong")),
            strong_fonts,
        );
    }
    ctx.set_fonts(fonts);
}

fn split_font_roles(names: &[String]) -> (Vec<String>, Vec<String>) {
    let mut strong = Vec::new();
    let mut body = Vec::new();

    for name in names {
        let lower = name.to_ascii_lowercase();
        let is_strong = lower.contains("gothicb")
            || lower.contains("biz-udgothicb")
            || lower.contains("meiryob")
            || lower.contains("yugothb")
            || lower.contains("yumindb");
        if is_strong {
            strong.push(name.clone());
        } else {
            body.push(name.clone());
        }
    }

    if body.is_empty() {
        body = names.to_vec();
    }
    if strong.is_empty() {
        strong = body.clone();
    } else {
        strong.extend(body.clone());
    }

    (body, strong)
}

fn load_ui_fonts() -> Vec<(String, Vec<u8>)> {
    let mut loaded = Vec::new();
    for path in candidate_font_paths() {
        if let Ok(bytes) = fs::read(&path) {
            let name = path
                .file_stem()
                .and_then(|value| value.to_str())
                .unwrap_or("proxyz-ui-font")
                .to_string();
            if loaded.iter().all(|(existing, _)| existing != &name) {
                loaded.push((name, bytes));
            }
        }
    }
    loaded
}

fn candidate_font_paths() -> Vec<PathBuf> {
    #[cfg(windows)]
    {
        return vec![
            PathBuf::from(r"C:\Windows\Fonts\BIZ-UDGothicR.ttc"),
            PathBuf::from(r"C:\Windows\Fonts\BIZ-UDGothicB.ttc"),
            PathBuf::from(r"C:\Windows\Fonts\GOTHIC.TTF"),
            PathBuf::from(r"C:\Windows\Fonts\GOTHICB.TTF"),
            PathBuf::from(r"C:\Windows\Fonts\meiryo.ttc"),
            PathBuf::from(r"C:\Windows\Fonts\meiryob.ttc"),
            PathBuf::from(r"C:\Windows\Fonts\YuGothM.ttc"),
            PathBuf::from(r"C:\Windows\Fonts\YuGothB.ttc"),
            PathBuf::from(r"C:\Windows\Fonts\NotoSansJP-VF.ttf"),
            PathBuf::from(r"C:\Windows\Fonts\NotoSerifJP-VF.ttf"),
            PathBuf::from(r"C:\Windows\Fonts\yumin.ttf"),
            PathBuf::from(r"C:\Windows\Fonts\yumindb.ttf"),
        ];
    }
    #[cfg(target_os = "macos")]
    {
        return vec![
            PathBuf::from("/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc"),
            PathBuf::from("/System/Library/Fonts/Hiragino Sans GB.ttc"),
            PathBuf::from("/Library/Fonts/Arial Unicode.ttf"),
        ];
    }
    #[cfg(target_os = "linux")]
    {
        return vec![
            PathBuf::from("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"),
            PathBuf::from("/usr/share/fonts/opentype/noto/NotoSansJP-Regular.otf"),
            PathBuf::from("/usr/share/fonts/truetype/noto/NotoSansCJK-Regular.ttc"),
        ];
    }
    #[allow(unreachable_code)]
    Vec::new()
}

fn install_style(ctx: &egui::Context) {
    let mut style: Style = (*ctx.style()).clone();
    style.text_styles.insert(
        TextStyle::Heading,
        FontId::new(27.0, FontFamily::Name(Arc::<str>::from("proxyz-strong"))),
    );
    style.text_styles.insert(
        TextStyle::Body,
        FontId::new(18.0, FontFamily::Name(Arc::<str>::from("proxyz-body"))),
    );
    style.text_styles.insert(
        TextStyle::Button,
        FontId::new(16.5, FontFamily::Name(Arc::<str>::from("proxyz-strong"))),
    );
    style.text_styles.insert(
        TextStyle::Small,
        FontId::new(14.5, FontFamily::Name(Arc::<str>::from("proxyz-body"))),
    );
    style.spacing.item_spacing = egui::vec2(12.0, 12.0);
    style.spacing.button_padding = egui::vec2(14.0, 10.0);
    style.spacing.indent = 18.0;
    style.spacing.window_margin = egui::Margin::same(16);
    style.visuals = Visuals::light();
    style.visuals.window_corner_radius = 18.0.into();
    style.visuals.menu_corner_radius = 14.0.into();
    style.visuals.panel_fill = Color32::from_rgb(251, 252, 253);
    style.visuals.override_text_color = Some(Color32::from_rgb(34, 40, 54));
    style.visuals.widgets.noninteractive.corner_radius = 14.0.into();
    style.visuals.widgets.noninteractive.bg_fill = Color32::from_rgb(247, 248, 250);
    style.visuals.widgets.noninteractive.weak_bg_fill = Color32::from_rgb(243, 246, 249);
    style.visuals.widgets.inactive.corner_radius = 14.0.into();
    style.visuals.widgets.inactive.bg_fill = Color32::from_rgb(240, 243, 247);
    style.visuals.widgets.inactive.weak_bg_fill = Color32::from_rgb(236, 240, 245);
    style.visuals.widgets.hovered.corner_radius = 14.0.into();
    style.visuals.widgets.hovered.bg_fill = Color32::from_rgb(226, 235, 243);
    style.visuals.widgets.hovered.weak_bg_fill = Color32::from_rgb(220, 231, 242);
    style.visuals.widgets.active.corner_radius = 14.0.into();
    style.visuals.widgets.active.bg_fill = Color32::from_rgb(210, 225, 239);
    style.visuals.widgets.active.weak_bg_fill = Color32::from_rgb(197, 216, 236);
    style.visuals.widgets.active.fg_stroke.color = style.visuals.panel_fill;
    style.visuals.selection.bg_fill = Color32::from_rgb(26, 100, 173);
    style.visuals.selection.stroke.color = Color32::WHITE;
    ctx.set_style(style);
}

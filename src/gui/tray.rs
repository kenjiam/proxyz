use std::ptr::{null, null_mut};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

use anyhow::{Context, Result, anyhow};
use crossbeam_channel::{Receiver, Sender, unbounded};
use eframe::egui;
use log::{error, info};
use windows_sys::Win32::Foundation::{HWND, POINT};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::Shell::{
    NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NOTIFYICONDATAW, Shell_NotifyIconW,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyMenu, DestroyWindow,
    DispatchMessageW, GetCursorPos, HMENU, IDI_APPLICATION, LoadIconW, MF_DISABLED, MF_GRAYED,
    MF_POPUP, MF_SEPARATOR, MF_STRING, MSG, PM_REMOVE, PeekMessageW, RegisterClassW,
    SetForegroundWindow, TPM_BOTTOMALIGN, TPM_LEFTALIGN, TPM_RETURNCMD, TrackPopupMenu,
    TranslateMessage, WM_APP, WM_CONTEXTMENU, WM_LBUTTONDBLCLK, WM_RBUTTONUP, WNDCLASSW,
};

use crate::application::services::{AppService, ApplyOptions};
use crate::domain::models::{
    ElevatedServiceAction, ProxyProfile, ServiceStartup, ServiceState, SystemSnapshot, UiLanguage,
};

const WM_TRAYICON: u32 = WM_APP + 1;
const ID_OPEN: usize = 1001;
const ID_REFRESH: usize = 1002;
const ID_SERVICE_START: usize = 1101;
const ID_SERVICE_STOP: usize = 1102;
const ID_SERVICE_INSTALL: usize = 1103;
const ID_SERVICE_UNINSTALL: usize = 1104;
const ID_QUIT: usize = 1900;
const PROFILE_ID_BASE: usize = 2000;

pub(crate) enum TrayEvent {
    ShowWindow,
    QuitRequested,
}

pub(crate) enum TrayCommand {
    Shutdown,
}

pub(crate) struct TrayHandle {
    pub(crate) events: Receiver<TrayEvent>,
    pub(crate) shutdown: Sender<TrayCommand>,
}

struct TrayState {
    language: UiLanguage,
    profiles: Vec<ProxyProfile>,
    snapshot: Option<SystemSnapshot>,
    active_profile: Option<String>,
}

pub(crate) fn start(service: Arc<AppService>, ctx: egui::Context) -> Result<TrayHandle> {
    let (event_tx, event_rx) = unbounded();
    let (shutdown_tx, shutdown_rx) = unbounded();

    thread::Builder::new()
        .name("proxyz-tray".to_string())
        .spawn(move || {
            if let Err(error) = run_tray_thread(service, ctx, event_tx, shutdown_rx) {
                error!("tray thread failed: {error:#}");
            }
        })
        .context("failed to start tray thread")?;

    Ok(TrayHandle {
        events: event_rx,
        shutdown: shutdown_tx,
    })
}

fn run_tray_thread(
    service: Arc<AppService>,
    repaint_ctx: egui::Context,
    event_tx: Sender<TrayEvent>,
    shutdown_rx: Receiver<TrayCommand>,
) -> Result<()> {
    unsafe {
        let instance = GetModuleHandleW(null());
        if instance.is_null() {
            return Err(anyhow!("GetModuleHandleW returned null"));
        }

        let class_name = to_wide("proxyz-tray-window");
        let window_name = to_wide("proxyz tray");
        let icon = LoadIconW(null_mut(), IDI_APPLICATION);

        let wnd_class = WNDCLASSW {
            lpfnWndProc: Some(DefWindowProcW),
            hInstance: instance,
            lpszClassName: class_name.as_ptr(),
            hIcon: icon,
            ..Default::default()
        };

        if RegisterClassW(&wnd_class) == 0 {
            return Err(anyhow!("RegisterClassW failed"));
        }

        let hwnd = CreateWindowExW(
            0,
            class_name.as_ptr(),
            window_name.as_ptr(),
            0,
            0,
            0,
            0,
            0,
            null_mut(),
            null_mut(),
            instance,
            null(),
        );
        if hwnd.is_null() {
            return Err(anyhow!("CreateWindowExW failed"));
        }

        let mut icon_data = NOTIFYICONDATAW {
            cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
            hWnd: hwnd,
            uID: 1,
            uFlags: NIF_MESSAGE | NIF_TIP | NIF_ICON,
            uCallbackMessage: WM_TRAYICON,
            hIcon: icon,
            ..Default::default()
        };
        copy_wide_into(&mut icon_data.szTip, "proxyz");

        if Shell_NotifyIconW(NIM_ADD, &icon_data) == 0 {
            DestroyWindow(hwnd);
            return Err(anyhow!("Shell_NotifyIconW(NIM_ADD) failed"));
        }

        let mut should_exit = false;
        while !should_exit {
            while let Ok(command) = shutdown_rx.try_recv() {
                match command {
                    TrayCommand::Shutdown => should_exit = true,
                }
            }

            let mut message = MSG::default();
            while PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE) != 0 {
                match message.message {
                    WM_TRAYICON => match message.lParam as u32 {
                        WM_LBUTTONDBLCLK => {
                            let _ = event_tx.send(TrayEvent::ShowWindow);
                            repaint_ctx.request_repaint();
                        }
                        WM_RBUTTONUP | WM_CONTEXTMENU => {
                            if let Err(error) =
                                show_tray_menu(hwnd, &service, &event_tx, &repaint_ctx)
                            {
                                error!("failed to show tray menu: {error:#}");
                            }
                        }
                        _ => {}
                    },
                    _ => {
                        TranslateMessage(&message);
                        DispatchMessageW(&message);
                    }
                }
            }

            thread::sleep(Duration::from_millis(80));
        }

        Shell_NotifyIconW(NIM_DELETE, &icon_data);
        DestroyWindow(hwnd);
    }

    Ok(())
}

unsafe fn show_tray_menu(
    hwnd: HWND,
    service: &Arc<AppService>,
    event_tx: &Sender<TrayEvent>,
    repaint_ctx: &egui::Context,
) -> Result<()> {
    let tray_state = load_tray_state(service);
    let menu = unsafe { CreatePopupMenu() };
    if menu.is_null() {
        return Err(anyhow!("CreatePopupMenu failed"));
    }

    unsafe {
        append_disabled_label(
            menu,
            &match tray_state.current_ssid_label() {
                Some(label) => label,
                None => tr(tray_state.language, "SSID: 未接続", "SSID: Not connected").to_string(),
            },
        );
        append_disabled_label(
            menu,
            &format!(
                "{}: {}",
                tr(tray_state.language, "サービス", "Service"),
                tray_state.service_state_label()
            ),
        );
    }
    if let Some(active_profile) = &tray_state.active_profile {
        unsafe {
            append_disabled_label(
                menu,
                &format!(
                    "{}: {}",
                    tr(tray_state.language, "現在のプロファイル", "Current profile"),
                    active_profile
                ),
            );
        }
    }

    unsafe {
        AppendMenuW(menu, MF_SEPARATOR, 0, null());
        append_menu_item(
            menu,
            ID_OPEN,
            tr(tray_state.language, "proxyz を開く", "Open proxyz"),
        );
        append_menu_item(
            menu,
            ID_REFRESH,
            tr(tray_state.language, "状態を再取得", "Refresh state"),
        );
    }

    if !tray_state.profiles.is_empty() {
        let submenu = unsafe { CreatePopupMenu() };
        for (index, profile) in tray_state.profiles.iter().enumerate() {
            let label = if tray_state.active_profile.as_deref() == Some(profile.name.as_str()) {
                format!("* {}", profile.name)
            } else {
                profile.name.clone()
            };
            unsafe {
                append_menu_item(submenu, PROFILE_ID_BASE + index, &label);
            }
        }
        unsafe {
            append_submenu(
                menu,
                submenu,
                tr(tray_state.language, "プロファイル", "Profiles"),
            );
        }
    }

    let service_menu = unsafe { CreatePopupMenu() };
    unsafe {
        append_menu_item(
            service_menu,
            ID_SERVICE_START,
            tr(tray_state.language, "サービスを開始", "Start service"),
        );
        append_menu_item(
            service_menu,
            ID_SERVICE_STOP,
            tr(tray_state.language, "サービスを停止", "Stop service"),
        );
        append_menu_item(
            service_menu,
            ID_SERVICE_INSTALL,
            tr(tray_state.language, "サービスを登録", "Register service"),
        );
        append_menu_item(
            service_menu,
            ID_SERVICE_UNINSTALL,
            tr(
                tray_state.language,
                "サービス登録を解除",
                "Unregister service",
            ),
        );
        append_submenu(
            menu,
            service_menu,
            tr(tray_state.language, "サービス", "Service"),
        );

        AppendMenuW(menu, MF_SEPARATOR, 0, null());
        append_menu_item(menu, ID_QUIT, tr(tray_state.language, "終了", "Quit"));
    }

    let mut point = POINT::default();
    unsafe {
        GetCursorPos(&mut point);
        SetForegroundWindow(hwnd);
    }
    let command = unsafe {
        TrackPopupMenu(
            menu,
            TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD,
            point.x,
            point.y,
            0,
            hwnd,
            null(),
        )
    };

    if command != 0 {
        handle_menu_command(
            command as usize,
            service,
            &tray_state,
            event_tx,
            repaint_ctx,
        )?;
    }

    unsafe {
        DestroyMenu(menu);
    }
    Ok(())
}

fn handle_menu_command(
    command: usize,
    service: &Arc<AppService>,
    tray_state: &TrayState,
    event_tx: &Sender<TrayEvent>,
    repaint_ctx: &egui::Context,
) -> Result<()> {
    match command {
        ID_OPEN | ID_REFRESH => {
            let _ = event_tx.send(TrayEvent::ShowWindow);
            repaint_ctx.request_repaint();
        }
        ID_SERVICE_START => {
            service.service_run_elevated(ElevatedServiceAction::Start)?;
        }
        ID_SERVICE_STOP => {
            service.service_run_elevated(ElevatedServiceAction::Stop)?;
        }
        ID_SERVICE_INSTALL => {
            service.service_run_elevated(ElevatedServiceAction::Install(
                tray_state
                    .snapshot
                    .as_ref()
                    .map(|snapshot| snapshot.service_status.startup)
                    .filter(|startup| {
                        matches!(
                            startup,
                            ServiceStartup::Automatic
                                | ServiceStartup::Manual
                                | ServiceStartup::Disabled
                        )
                    })
                    .unwrap_or(ServiceStartup::Automatic),
            ))?;
        }
        ID_SERVICE_UNINSTALL => {
            service.service_run_elevated(ElevatedServiceAction::Uninstall)?;
        }
        ID_QUIT => {
            let _ = event_tx.send(TrayEvent::QuitRequested);
            repaint_ctx.request_repaint();
        }
        id if id >= PROFILE_ID_BASE => {
            let index = id - PROFILE_ID_BASE;
            if let Some(profile) = tray_state.profiles.get(index) {
                service.set_profile(
                    &profile.name,
                    &ApplyOptions {
                        assume_yes: true,
                        dry_run: false,
                        skip_commands: false,
                    },
                )?;
                info!("applied profile '{}' from tray menu", profile.name);
            }
        }
        _ => {}
    }
    Ok(())
}

fn load_tray_state(service: &Arc<AppService>) -> TrayState {
    let catalog = service.load_profile_catalog().unwrap_or_default();
    let snapshot = service.system_snapshot().ok();
    let language = service
        .load_settings()
        .map(|settings| settings.ui_language)
        .unwrap_or(UiLanguage::Japanese);

    let active_profile = snapshot.as_ref().and_then(|snapshot| {
        catalog
            .profiles
            .iter()
            .find(|profile| profile.proxy == snapshot.current_proxy)
            .map(|profile| profile.name.clone())
    });

    TrayState {
        language,
        profiles: catalog.profiles,
        snapshot,
        active_profile,
    }
}

impl TrayState {
    fn current_ssid_label(&self) -> Option<String> {
        self.snapshot.as_ref().and_then(|snapshot| {
            snapshot
                .current_ssid
                .as_ref()
                .map(|ssid| format!("{}: {}", tr(self.language, "SSID", "SSID"), ssid))
        })
    }

    fn service_state_label(&self) -> &'static str {
        match self
            .snapshot
            .as_ref()
            .map(|snapshot| snapshot.service_status.state)
        {
            Some(ServiceState::Running) => tr(self.language, "実行中", "Running"),
            Some(ServiceState::Stopped) => tr(self.language, "停止中", "Stopped"),
            _ => tr(self.language, "不明", "Unknown"),
        }
    }
}

unsafe fn append_menu_item(menu: HMENU, id: usize, label: &str) {
    let wide = to_wide(label);
    unsafe {
        AppendMenuW(menu, MF_STRING, id, wide.as_ptr());
    }
}

unsafe fn append_disabled_label(menu: HMENU, label: &str) {
    let wide = to_wide(label);
    unsafe {
        AppendMenuW(menu, MF_STRING | MF_DISABLED | MF_GRAYED, 0, wide.as_ptr());
    }
}

unsafe fn append_submenu(menu: HMENU, submenu: HMENU, label: &str) {
    let wide = to_wide(label);
    unsafe {
        AppendMenuW(menu, MF_POPUP, submenu as usize, wide.as_ptr());
    }
}

fn tr<'a>(language: UiLanguage, ja: &'a str, en: &'a str) -> &'a str {
    match language {
        UiLanguage::Japanese => ja,
        UiLanguage::English => en,
    }
}

fn to_wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn copy_wide_into(buffer: &mut [u16], value: &str) {
    let wide = to_wide(value);
    let len = wide.len().min(buffer.len());
    buffer[..len].copy_from_slice(&wide[..len]);
}

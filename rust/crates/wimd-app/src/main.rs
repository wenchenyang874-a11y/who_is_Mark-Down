#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod commands;
mod diagnostics;
mod fonts;
mod heartbeat;
mod image_export;
mod imgbb;
mod local_links;
mod pdf;
mod process_usage;
mod recovery;
mod restart_session;
mod secrets;
mod state;
mod updates;

use std::{path::PathBuf, process::Command};

use tauri::{Manager, PhysicalPosition, PhysicalSize};
use wimd_core::settings::SettingsStore;

use crate::state::{AppState, AppStateInit};

#[cfg(windows)]
use windows_sys::Win32::UI::WindowsAndMessaging::{SW_SHOWNOACTIVATE, ShowWindow};

fn main() {
    let result = tauri::Builder::default()
        .setup(|app| {
            let (local_data, legacy_settings_path) =
                if let Ok(test_directory) = std::env::var("WIMD_DATA_DIR") {
                    let test_directory = PathBuf::from(test_directory);
                    (
                        test_directory.clone(),
                        test_directory.join("Legacy/settings.json"),
                    )
                } else {
                    let local_root = app
                        .path()
                        .local_data_dir()
                        .map_err(|error| format!("无法定位本地应用数据目录：{error}"))?;
                    (
                        local_root.join("WIMD"),
                        local_root.join("WhoIsMarkdown").join("settings.json"),
                    )
                };
            std::fs::create_dir_all(&local_data)
                .map_err(|error| format!("无法创建设置目录：{error}"))?;
            crate::restart_session::promote_capture_for_startup(&local_data);
            if crate::restart_session::pending_restore_window_count(&local_data) == 0 {
                crate::restart_session::migrate_legacy_update_restart(
                    &local_data,
                    &local_data.join("Recovery"),
                );
            }
            let arguments = std::env::args_os().collect::<Vec<_>>();
            let restore_worker = arguments
                .iter()
                .skip(1)
                .any(|argument| argument == crate::restart_session::RESTORE_WINDOW_ARGUMENT);
            let restore_window_count =
                crate::restart_session::pending_restore_window_count(&local_data);
            let claimed_update_restart_window =
                crate::restart_session::claim_restore_window(&local_data)
                    .map_err(|error| format!("读取更新恢复窗口失败：{error}"))?;
            let update_restart_window = claimed_update_restart_window
                .as_ref()
                .map(|claimed| claimed.snapshot.clone());
            let update_restart_claim_path = claimed_update_restart_window
                .as_ref()
                .map(|claimed| claimed.claim_path.clone());
            if update_restart_window.is_some() && !restore_worker {
                let remaining = restore_window_count.saturating_sub(1);
                if remaining > 0 {
                    let executable = std::env::current_exe()
                        .map_err(|error| format!("无法定位 WIMD 可执行文件：{error}"))?;
                    for index in 0..remaining {
                        let mut command = Command::new(&executable);
                        command.arg(crate::restart_session::RESTORE_WINDOW_ARGUMENT);
                        if std::env::var_os("WIMD_QA_BACKGROUND_WINDOW").is_some() {
                            command.env_remove("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
                            if let Some(profile) = std::env::var_os("WEBVIEW2_USER_DATA_FOLDER") {
                                let profile = PathBuf::from(profile);
                                let name = profile
                                    .file_name()
                                    .map(|value| value.to_string_lossy().into_owned())
                                    .unwrap_or_else(|| "webview".to_owned());
                                command.env(
                                    "WEBVIEW2_USER_DATA_FOLDER",
                                    profile.with_file_name(format!("{name}-restore-{index}")),
                                );
                            }
                        }
                        command
                            .spawn()
                            .map_err(|error| format!("重新打开更新前窗口失败：{error}"))?;
                    }
                }
            }
            let diagnostics = crate::diagnostics::DiagnosticLog::new(local_data.join("Logs"));
            let panic_diagnostics = diagnostics.clone();
            let previous_panic_hook = std::panic::take_hook();
            std::panic::set_hook(Box::new(move |info| {
                panic_diagnostics.record("ProcessPanic", "Failed");
                previous_panic_hook(info);
            }));
            diagnostics.record("ApplicationStartup", "Started");
            let settings_store = SettingsStore::new_with_legacy(
                local_data.join("settings.json"),
                legacy_settings_path,
            );
            let startup_paths = arguments
                .into_iter()
                .skip(1)
                .filter(|argument| argument != crate::restart_session::RESTORE_WINDOW_ARGUMENT)
                .map(PathBuf::from)
                .collect::<Vec<_>>();
            let state = AppState::new(AppStateInit {
                data_directory: local_data.clone(),
                settings_store,
                startup_paths,
                restart_window_id: crate::restart_session::new_window_id(),
                update_restart_window: update_restart_window.clone(),
                update_restart_claim_path,
                background_directory: local_data.join("Backgrounds"),
                recovery_directory: local_data.join("Recovery"),
                updates_directory: local_data.join("Updates"),
                diagnostics,
            });
            if let Some(window) = app.get_webview_window("main") {
                if let Some(snapshot) = update_restart_window.as_ref() {
                    let _ = window.set_size(PhysicalSize::new(
                        snapshot.window_width,
                        snapshot.window_height,
                    ));
                    if let (Some(x), Some(y)) = (snapshot.window_x, snapshot.window_y) {
                        let _ = window.set_position(PhysicalPosition::new(x, y));
                    }
                    if snapshot.window_maximized {
                        let _ = window.maximize();
                    }
                } else if let Ok(settings) = state.settings_snapshot() {
                    let width = settings.window_width;
                    let height = settings.window_height;
                    let _ = window.set_size(PhysicalSize::new(width, height));
                    if let (Some(x), Some(y)) = (settings.window_x, settings.window_y) {
                        let _ = window.set_position(PhysicalPosition::new(x, y));
                    }
                    if settings.window_maximized {
                        let _ = window.maximize();
                    }
                }
                let background_test = std::env::var_os("WIMD_QA_BACKGROUND_WINDOW").is_some();
                if background_test {
                    let _ = window.set_skip_taskbar(true);
                    let _ = window.set_position(PhysicalPosition::new(-32_000, -32_000));
                    #[cfg(windows)]
                    if let Ok(hwnd) = window.hwnd() {
                        unsafe {
                            ShowWindow(hwnd.0, SW_SHOWNOACTIVATE);
                        }
                    }
                } else {
                    let _ = window.show();
                }
            }
            app.manage(state);
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::bootstrap,
            commands::installed_fonts,
            commands::inspect_recent_paths,
            commands::settings_context,
            commands::open_settings_window,
            commands::close_settings_window,
            commands::ui_heartbeat,
            commands::mark_startup_preview,
            commands::report_frontend_error,
            commands::current_process_usage,
            commands::installer_capture_requested,
            commands::save_update_restart_window,
            commands::finish_update_restart_window,
            commands::consume_installer_restore,
            commands::open_image_viewer,
            commands::get_viewer_image,
            commands::close_image_viewer,
            commands::exit_application,
            commands::open_logs_folder,
            commands::capture_startup_diagnostic,
            commands::check_update,
            commands::download_update,
            commands::cancel_update_download,
            commands::install_update,
            commands::save_recovery_snapshot,
            commands::list_recovery_snapshots,
            commands::remove_recovery_snapshot,
            commands::pick_document,
            commands::pick_save_path,
            commands::pick_pdf_save_path,
            commands::pick_folder,
            commands::pick_image,
            commands::choose_background_image,
            commands::load_background_image,
            commands::load_document,
            commands::save_document,
            commands::export_pdf,
            commands::mark_pdf_window_ready,
            commands::inspect_document_stamp,
            commands::render_preview,
            commands::format_markdown,
            commands::toggle_task,
            commands::load_image,
            commands::prepare_preview_image,
            commands::pick_preview_image_save_path,
            commands::save_preview_image,
            commands::insert_image,
            commands::insert_image_bytes,
            commands::save_settings,
            commands::save_imgbb_api_key,
            commands::clear_imgbb_api_key,
            commands::upload_image_to_imgbb,
            commands::record_recent_path,
            commands::remove_recent_path,
            commands::record_recent_workspace,
            commands::remove_recent_workspace,
            commands::open_workspace,
            commands::workspace_children,
            commands::watch_workspace,
            commands::create_workspace_markdown,
            commands::create_workspace_directory,
            commands::rename_workspace_entry,
            commands::delete_workspace_entry,
            commands::watch_document,
            commands::copy_text,
            commands::read_clipboard_text,
            commands::open_external,
            commands::resolve_local_link,
            commands::open_local_link,
            commands::reveal_in_explorer,
            commands::open_in_new_window,
        ])
        .run(tauri::generate_context!());
    if let Err(error) = result {
        show_fatal_error(&format!("WIMD 启动失败：{error}"));
    }
}

fn show_fatal_error(message: &str) {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::{MB_ICONERROR, MB_OK, MessageBoxW};
        let message = message
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect::<Vec<_>>();
        let title = "WIMD"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect::<Vec<_>>();
        MessageBoxW(
            std::ptr::null_mut(),
            message.as_ptr(),
            title.as_ptr(),
            MB_OK | MB_ICONERROR,
        );
    }
    #[cfg(not(windows))]
    eprintln!("{message}");
}

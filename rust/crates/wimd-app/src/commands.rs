use std::{
    path::{Path, PathBuf},
    process::Command,
    sync::Arc,
};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

use chrono::Utc;
use notify::{RecursiveMode, Watcher};
use serde::Serialize;
use tauri::{
    AppHandle, Emitter, Manager, PhysicalPosition, State, WebviewUrl, WebviewWindow,
    WebviewWindowBuilder,
};
use wimd_core::{
    document::{
        DocumentStamp, LoadedDocument, inspect_document, normalize_path, read_document,
        save_document_atomic,
    },
    formatting::{MarkdownTextEdit, apply_format, apply_table},
    image::{
        LocalImageData, StoredImage, load_local_image, load_managed_background, store_image_bytes,
        store_image_file, store_managed_background,
    },
    markdown::{RenderedMarkdown, render_markdown},
    settings::{ApplicationSettings, ImageStorageMode, Theme},
    shortcut::{SHORTCUTS, ShortcutDefinition, effective_shortcuts},
    task::{TaskToggle, toggle_task_line},
    workspace::{Workspace, WorkspaceEntry},
};

use crate::state::{AppState, SettingsWindowContext, ViewerImagePayload};
use crate::{
    image_export::{self, PreparedImage},
    imgbb, pdf,
    process_usage::{ProcessSample, sample_current_process},
    recovery::{self, RecoverySnapshot},
    restart_session::{self, UpdateRestartWindowSnapshot},
    secrets,
    updates::{self, UpdateInfo},
};

type CommandResult<T> = Result<T, String>;

fn parented_file_dialog(window: &WebviewWindow) -> rfd::FileDialog {
    rfd::FileDialog::new().set_parent(window)
}

fn popup_logical_position(
    parent: &WebviewWindow,
    logical_width: f64,
    logical_height: f64,
) -> Option<(f64, f64)> {
    let scale = parent.scale_factor().ok()?;
    let parent_position = parent.outer_position().ok()?;
    let parent_size = parent.outer_size().ok()?;
    let width = (logical_width * scale).round() as i32;
    let height = (logical_height * scale).round() as i32;
    let mut x = parent_position.x + (parent_size.width as i32 - width) / 2;
    let mut y = parent_position.y + (parent_size.height as i32 - height) / 2;
    if let Ok(Some(monitor)) = parent.current_monitor() {
        let monitor_position = monitor.position();
        let monitor_size = monitor.size();
        x = x.clamp(
            monitor_position.x,
            monitor_position.x + monitor_size.width as i32 - width,
        );
        y = y.clamp(
            monitor_position.y,
            monitor_position.y + monitor_size.height as i32 - height,
        );
    }
    Some((x as f64 / scale, y as f64 / scale))
}

fn position_popup_over_parent(parent: &WebviewWindow, popup: &WebviewWindow) -> CommandResult<()> {
    let parent_position = parent.outer_position().map_err(|error| error.to_string())?;
    let parent_size = parent.outer_size().map_err(|error| error.to_string())?;
    let popup_size = popup.outer_size().map_err(|error| error.to_string())?;
    let mut x = parent_position.x + (parent_size.width as i32 - popup_size.width as i32) / 2;
    let mut y = parent_position.y + (parent_size.height as i32 - popup_size.height as i32) / 2;
    if let Ok(Some(monitor)) = parent.current_monitor() {
        let monitor_position = monitor.position();
        let monitor_size = monitor.size();
        x = x.clamp(
            monitor_position.x,
            monitor_position.x + monitor_size.width as i32 - popup_size.width as i32,
        );
        y = y.clamp(
            monitor_position.y,
            monitor_position.y + monitor_size.height as i32 - popup_size.height as i32,
        );
    }
    popup
        .set_position(PhysicalPosition::new(x, y))
        .map_err(|error| format!("移动弹窗到父窗口所在显示器失败：{error}"))?;
    Ok(())
}

fn log_command_result<T>(
    state: &AppState,
    stage: &'static str,
    started: std::time::Instant,
    result: CommandResult<T>,
) -> CommandResult<T> {
    state.diagnostics.record_with(
        stage,
        if result.is_ok() {
            "Succeeded"
        } else {
            "Failed"
        },
        Some(started.elapsed().as_secs_f64() * 1000.0),
        None,
        result.as_ref().err().map(|_| "CommandFailed"),
    );
    result
}

async fn save_merged_settings_background(
    store: wimd_core::settings::SettingsStore,
    baseline: ApplicationSettings,
    incoming: ApplicationSettings,
) -> CommandResult<ApplicationSettings> {
    tauri::async_runtime::spawn_blocking(move || store.save_merged(&baseline, &incoming))
        .await
        .map_err(|error| format!("保存设置任务失败：{error}"))?
        .map_err(|error| error.to_string())
}

async fn commit_settings_mutation(
    state: &AppState,
    mutate: impl FnOnce(&mut ApplicationSettings),
) -> CommandResult<ApplicationSettings> {
    let (baseline, incoming) = {
        let mut settings = state
            .settings
            .lock()
            .map_err(|_| "设置状态已损坏。".to_owned())?;
        let baseline = settings.clone();
        mutate(&mut settings);
        (baseline, settings.clone())
    };
    match save_merged_settings_background(state.settings_store.clone(), baseline.clone(), incoming)
        .await
    {
        Ok(merged) => {
            *state
                .settings
                .lock()
                .map_err(|_| "设置状态已损坏。".to_owned())? = merged.clone();
            Ok(merged)
        }
        Err(error) => {
            if let Ok(mut settings) = state.settings.lock() {
                *settings = baseline;
            }
            Err(error)
        }
    }
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BootstrapPayload {
    pub settings: ApplicationSettings,
    pub settings_warning: Option<String>,
    pub shortcuts: Vec<ShortcutDefinition>,
    pub startup_paths: Vec<PathBuf>,
    pub update_restart_window: Option<UpdateRestartWindowSnapshot>,
    pub version: String,
    pub settings_path: PathBuf,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WorkspacePayload {
    pub root: PathBuf,
    pub entries: Vec<WorkspaceEntry>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RenameWorkspacePayload {
    pub target: PathBuf,
    pub settings: ApplicationSettings,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DeleteWorkspacePayload {
    pub settings: ApplicationSettings,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RecentPathAvailability {
    pub path: PathBuf,
    pub exists: bool,
    pub is_directory: bool,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DocumentChangedPayload {
    pub path: PathBuf,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BackgroundPayload {
    pub path: PathBuf,
    pub data_uri: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UploadedImagePayload {
    pub url: String,
    pub delete_url: Option<String>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateDownloadProgress {
    pub downloaded_bytes: u64,
    pub total_bytes: u64,
}

#[tauri::command]
pub fn bootstrap(state: State<'_, AppState>) -> CommandResult<BootstrapPayload> {
    state.diagnostics.record_with(
        "ApplicationBootstrap",
        "Succeeded",
        Some(state.diagnostics.elapsed_ms()),
        None,
        None,
    );
    Ok(BootstrapPayload {
        settings: state.settings_snapshot()?,
        settings_warning: state.settings_warning.clone(),
        shortcuts: SHORTCUTS.to_vec(),
        startup_paths: state.startup_paths.clone(),
        update_restart_window: state.update_restart_window.clone(),
        version: env!("CARGO_PKG_VERSION").to_owned(),
        settings_path: state.settings_store.path().to_path_buf(),
    })
}

#[tauri::command]
pub async fn installed_fonts() -> CommandResult<Vec<String>> {
    tauri::async_runtime::spawn_blocking(crate::fonts::installed_font_families)
        .await
        .map_err(|error| format!("字体枚举任务失败：{error}"))?
}

#[tauri::command]
pub async fn inspect_recent_paths(
    paths: Vec<PathBuf>,
) -> CommandResult<Vec<RecentPathAvailability>> {
    tauri::async_runtime::spawn_blocking(move || {
        paths
            .into_iter()
            .take(64)
            .map(|path| {
                let metadata = std::fs::metadata(&path).ok();
                RecentPathAvailability {
                    path,
                    exists: metadata.is_some(),
                    is_directory: metadata.is_some_and(|metadata| metadata.is_dir()),
                }
            })
            .collect()
    })
    .await
    .map_err(|error| format!("最近路径检查任务失败：{error}"))
}

#[tauri::command]
pub fn settings_context(
    window: WebviewWindow,
    state: State<'_, AppState>,
) -> CommandResult<Option<SettingsWindowContext>> {
    state
        .settings_window_context
        .lock()
        .map(|context| context.get(window.label()).cloned())
        .map_err(|_| "设置窗口上下文已损坏。".to_owned())
}

#[tauri::command]
pub async fn open_settings_window(
    app: AppHandle,
    parent: WebviewWindow,
    state: State<'_, AppState>,
    kind: String,
    document_path: Option<PathBuf>,
) -> CommandResult<()> {
    let (title, width, height, minimum_width, minimum_height, resizable) = match kind.as_str() {
        "appearance" => ("外观设置", 820.0, 760.0, 680.0, 560.0, true),
        "preview" => ("预览样式", 680.0, 650.0, 620.0, 560.0, false),
        "images" => ("图片设置", 680.0, 560.0, 620.0, 480.0, true),
        "shortcuts" => ("快捷键设置 - WIMD", 620.0, 650.0, 520.0, 480.0, true),
        "behavior" => ("文件打开与外部修改", 720.0, 600.0, 620.0, 500.0, true),
        _ => return Err("未知的设置窗口类型。".to_owned()),
    };

    let window_id = state
        .next_settings_window_id
        .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let label = format!("settings-{window_id}");
    state
        .settings_window_context
        .lock()
        .map_err(|_| "设置窗口上下文已损坏。".to_owned())?
        .insert(
            label.clone(),
            SettingsWindowContext {
                kind,
                document_path,
            },
        );

    let mut builder =
        WebviewWindowBuilder::new(&app, &label, WebviewUrl::App("settings.html".into()))
            .title(title)
            .inner_size(width, height)
            .min_inner_size(minimum_width, minimum_height)
            .resizable(resizable)
            .parent(&parent)
            .map_err(|error| format!("绑定设置窗口父窗口失败：{error}"))?;
    let background_test = std::env::var_os("WIMD_QA_BACKGROUND_WINDOW").is_some();
    builder = if background_test {
        builder.visible(false).skip_taskbar(true)
    } else if let Some((x, y)) = popup_logical_position(&parent, width, height) {
        builder.position(x, y)
    } else {
        builder.center()
    };
    let window = builder
        .build()
        .map_err(|error| format!("创建设置窗口失败：{error}"))?;
    let context_app = app.clone();
    window.on_window_event(move |event| {
        if matches!(event, tauri::WindowEvent::Destroyed)
            && let Ok(mut contexts) = context_app
                .state::<AppState>()
                .settings_window_context
                .lock()
        {
            contexts.remove(&label);
        }
    });
    Ok(())
}

#[tauri::command]
pub fn close_settings_window(window: WebviewWindow) -> CommandResult<()> {
    // Hide first so WebView2's transitional about:blank state can never appear
    // as a white settings window while the native window is being destroyed.
    window
        .hide()
        .map_err(|error| format!("隐藏设置窗口失败：{error}"))?;
    window
        .destroy()
        .map_err(|error| format!("销毁设置窗口失败：{error}"))
}

#[tauri::command]
pub fn ui_heartbeat(state: State<'_, AppState>) {
    state.heartbeat.beat();
}

#[tauri::command]
pub fn mark_startup_preview(state: State<'_, AppState>, ready: bool) {
    state.diagnostics.record(
        if ready {
            "StartupPreviewReady"
        } else {
            "StartupPreviewDeferred"
        },
        "Point",
    );
}

#[tauri::command]
pub fn report_frontend_error(state: State<'_, AppState>, kind: String) {
    let error_type = match kind.as_str() {
        "UnhandledRejection" => "UnhandledRejection",
        _ => "UnhandledError",
    };
    state
        .diagnostics
        .record_with("FrontendUnhandled", "Failed", None, None, Some(error_type));
}

#[tauri::command]
pub fn current_process_usage() -> CommandResult<ProcessSample> {
    sample_current_process()
}

#[tauri::command]
pub async fn open_logs_folder(state: State<'_, AppState>) -> CommandResult<()> {
    let path = state.diagnostics.logs_directory().to_path_buf();
    tauri::async_runtime::spawn_blocking(move || open::that_detached(path))
        .await
        .map_err(|error| format!("打开日志目录任务失败：{error}"))?
        .map_err(|error| format!("打开日志目录失败：{error}"))
}

#[tauri::command]
pub fn capture_startup_diagnostic(
    app: AppHandle,
    document_path: Option<PathBuf>,
) -> CommandResult<()> {
    let resource_directory = app
        .path()
        .resource_dir()
        .map_err(|error| format!("无法定位诊断脚本目录：{error}"))?;
    let script = resource_directory
        .join("Diagnostics")
        .join("Capture-Startup.ps1");
    if !script.is_file() {
        return Err(format!("未找到启动诊断脚本：{}", script.display()));
    }
    let system_root =
        std::env::var_os("SystemRoot").ok_or_else(|| "无法定位 Windows 系统目录。".to_owned())?;
    let powershell = PathBuf::from(system_root)
        .join("System32")
        .join("WindowsPowerShell")
        .join("v1.0")
        .join("powershell.exe");
    let mut command = Command::new(powershell);
    command
        .arg("-NoProfile")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-File")
        .arg(script);
    if let Some(path) = document_path {
        command.arg("-DocumentPath").arg(path);
    }
    #[cfg(windows)]
    command.creation_flags(0x0800_0000);
    command
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("启动采集诊断脚本失败：{error}"))
}

#[tauri::command]
pub fn installer_capture_requested(state: State<'_, AppState>) -> bool {
    restart_session::is_capture_requested(&state.data_directory)
}

#[tauri::command]
pub async fn save_update_restart_window(
    state: State<'_, AppState>,
    snapshot: UpdateRestartWindowSnapshot,
) -> CommandResult<bool> {
    if !restart_session::is_capture_requested(&state.data_directory) {
        return Ok(false);
    }
    let application_data = state.data_directory.clone();
    let window_id = state.restart_window_id.clone();
    tauri::async_runtime::spawn_blocking(move || {
        restart_session::save_window_snapshot(&application_data, &window_id, snapshot)
    })
    .await
    .map_err(|error| format!("保存更新恢复窗口任务失败：{error}"))??;
    Ok(true)
}

#[tauri::command]
pub async fn finish_update_restart_window(state: State<'_, AppState>) -> CommandResult<bool> {
    let claim_path = state
        .update_restart_claim_path
        .lock()
        .map_err(|_| "更新恢复认领状态已损坏。".to_owned())?
        .take();
    let Some(claim_path) = claim_path else {
        return Ok(false);
    };
    let application_data = state.data_directory.clone();
    tauri::async_runtime::spawn_blocking(move || {
        restart_session::finish_restore_window(&application_data, &claim_path)
    })
    .await
    .map_err(|error| format!("确认更新恢复窗口任务失败：{error}"))??;
    Ok(true)
}

#[tauri::command]
pub fn consume_installer_restore(state: State<'_, AppState>) -> bool {
    restart_session::consume_restore_request(&state.data_directory)
}

#[tauri::command]
pub async fn open_image_viewer(
    app: AppHandle,
    parent: WebviewWindow,
    state: State<'_, AppState>,
    source: String,
    title: String,
) -> CommandResult<()> {
    let request_id = state
        .next_viewer_request
        .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let theme = state.settings_snapshot()?.theme;
    let payload = ViewerImagePayload {
        request_id,
        source,
        title,
        theme: theme_id(theme).to_owned(),
    };
    *state
        .viewer_image
        .lock()
        .map_err(|_| "图片查看器状态已损坏。".to_owned())? = Some(payload.clone());

    if let Some(window) = app.get_webview_window("image-viewer") {
        let _ = position_popup_over_parent(&parent, &window);
        window
            .emit("wimd://viewer-image", payload)
            .map_err(|error| format!("更新图片查看器失败：{error}"))?;
        let _ = window.set_focus();
        return Ok(());
    }

    let mut builder =
        WebviewWindowBuilder::new(&app, "image-viewer", WebviewUrl::App("viewer.html".into()))
            .title("WIMD 图片查看")
            .inner_size(960.0, 720.0)
            .min_inner_size(520.0, 380.0)
            .resizable(true)
            .parent(&parent)
            .map_err(|error| format!("绑定图片查看器父窗口失败：{error}"))?;
    builder = if let Some((x, y)) = popup_logical_position(&parent, 960.0, 720.0) {
        builder.position(x, y)
    } else {
        builder.center()
    };
    let window = builder
        .build()
        .map_err(|error| format!("创建图片查看器失败：{error}"))?;
    window
        .emit("wimd://viewer-image", payload)
        .map_err(|error| format!("发送图片查看请求失败：{error}"))?;
    Ok(())
}

#[tauri::command]
pub fn get_viewer_image(state: State<'_, AppState>) -> CommandResult<Option<ViewerImagePayload>> {
    state
        .viewer_image
        .lock()
        .map(|value| value.clone())
        .map_err(|_| "图片查看器状态已损坏。".to_owned())
}

#[tauri::command]
pub fn close_image_viewer(app: AppHandle) -> CommandResult<()> {
    if let Some(window) = app.get_webview_window("image-viewer") {
        window
            .close()
            .map_err(|error| format!("关闭图片查看器失败：{error}"))?;
    }
    Ok(())
}

#[tauri::command]
pub fn exit_application(app: AppHandle) {
    app.exit(0);
}

fn theme_id(theme: Theme) -> &'static str {
    match theme {
        Theme::System => "system",
        Theme::Light => "light",
        Theme::Dark => "dark",
        Theme::Warm => "warm",
        Theme::Rose => "rose",
        Theme::Sage => "sage",
        Theme::Mist => "mist",
        Theme::Lilac => "lilac",
        Theme::Midnight => "midnight",
    }
}

#[tauri::command]
pub async fn check_update(state: State<'_, AppState>) -> CommandResult<Option<UpdateInfo>> {
    let started = std::time::Instant::now();
    let result = updates::check_latest().await;
    log_command_result(&state, "UpdateCheck", started, result)
}

#[tauri::command]
pub async fn download_update(
    app: AppHandle,
    state: State<'_, AppState>,
    update: UpdateInfo,
) -> CommandResult<PathBuf> {
    let started = std::time::Instant::now();
    let directory = state.updates_directory.clone();
    let cancel = Arc::clone(&state.cancel_update_download);
    cancel.store(false, std::sync::atomic::Ordering::Release);
    let progress_app = app.clone();
    let result = updates::download(
        &update,
        &directory,
        || cancel.load(std::sync::atomic::Ordering::Acquire),
        move |downloaded_bytes, total_bytes| {
            let _ = progress_app.emit(
                "wimd://update-download-progress",
                UpdateDownloadProgress {
                    downloaded_bytes,
                    total_bytes,
                },
            );
        },
    )
    .await;
    log_command_result(&state, "UpdateDownload", started, result)
}

#[tauri::command]
pub fn cancel_update_download(state: State<'_, AppState>) {
    state
        .cancel_update_download
        .store(true, std::sync::atomic::Ordering::Release);
}

#[tauri::command]
pub async fn install_update(
    state: State<'_, AppState>,
    path: PathBuf,
    expected_sha256: String,
) -> CommandResult<()> {
    let started = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || {
        updates::verify_installer(&path, &expected_sha256)?;
        Command::new(&path)
            .spawn()
            .map(|_| ())
            .map_err(|error| format!("启动更新安装程序失败：{error}"))
    })
    .await
    .map_err(|error| format!("启动更新安装任务失败：{error}"))?;
    log_command_result(&state, "UpdateInstall", started, result)
}

#[tauri::command]
pub async fn save_recovery_snapshot(
    state: State<'_, AppState>,
    mut snapshot: RecoverySnapshot,
) -> CommandResult<()> {
    snapshot.owner_process_id = std::process::id();
    let directory = state.recovery_directory.clone();
    tauri::async_runtime::spawn_blocking(move || recovery::save(&directory, &snapshot))
        .await
        .map_err(|error| format!("保存恢复快照任务失败：{error}"))?
}

#[tauri::command]
pub async fn list_recovery_snapshots(
    state: State<'_, AppState>,
) -> CommandResult<Vec<RecoverySnapshot>> {
    let directory = state.recovery_directory.clone();
    tauri::async_runtime::spawn_blocking(move || recovery::list(&directory))
        .await
        .map_err(|error| format!("读取恢复快照任务失败：{error}"))?
}

#[tauri::command]
pub async fn remove_recovery_snapshot(state: State<'_, AppState>, id: String) -> CommandResult<()> {
    let directory = state.recovery_directory.clone();
    tauri::async_runtime::spawn_blocking(move || recovery::remove(&directory, &id))
        .await
        .map_err(|error| format!("删除恢复快照任务失败：{error}"))?
}

#[tauri::command]
pub async fn pick_document(window: WebviewWindow) -> CommandResult<Option<PathBuf>> {
    tauri::async_runtime::spawn_blocking(move || {
        parented_file_dialog(&window)
            .set_title("打开 Markdown 文档")
            .add_filter("Markdown", &["md", "markdown"])
            .add_filter("所有文件", &["*"])
            .pick_file()
    })
    .await
    .map_err(|error| format!("打开文件对话框失败：{error}"))
}

#[tauri::command]
pub async fn pick_save_path(
    window: WebviewWindow,
    suggested_name: String,
) -> CommandResult<Option<PathBuf>> {
    tauri::async_runtime::spawn_blocking(move || {
        let suggested_name = if suggested_name.trim().is_empty() {
            "未命名.md".to_owned()
        } else {
            suggested_name
        };
        parented_file_dialog(&window)
            .set_title("保存 Markdown 文档")
            .set_file_name(suggested_name)
            .add_filter("Markdown", &["md", "markdown"])
            .save_file()
    })
    .await
    .map_err(|error| format!("保存文件对话框失败：{error}"))
}

#[tauri::command]
pub async fn pick_pdf_save_path(
    window: WebviewWindow,
    suggested_name: String,
) -> CommandResult<Option<PathBuf>> {
    tauri::async_runtime::spawn_blocking(move || {
        let suggested_name = if suggested_name.trim().is_empty() {
            "未命名.pdf".to_owned()
        } else {
            let path = PathBuf::from(suggested_name);
            path.with_extension("pdf")
                .file_name()
                .map(|name| name.to_string_lossy().into_owned())
                .unwrap_or_else(|| "未命名.pdf".to_owned())
        };
        parented_file_dialog(&window)
            .set_title("导出 PDF")
            .set_file_name(suggested_name)
            .add_filter("PDF", &["pdf"])
            .save_file()
    })
    .await
    .map_err(|error| format!("PDF 保存对话框失败：{error}"))
}

#[tauri::command]
pub async fn pick_folder(window: WebviewWindow) -> CommandResult<Option<PathBuf>> {
    tauri::async_runtime::spawn_blocking(move || {
        parented_file_dialog(&window)
            .set_title("打开 Markdown 工作区")
            .pick_folder()
    })
    .await
    .map_err(|error| format!("打开文件夹对话框失败：{error}"))
}

#[tauri::command]
pub async fn pick_image(window: WebviewWindow) -> CommandResult<Option<PathBuf>> {
    tauri::async_runtime::spawn_blocking(move || {
        parented_file_dialog(&window)
            .set_title("选择要插入的图片")
            .add_filter("图片", &["png", "jpg", "jpeg", "gif", "bmp", "webp", "svg"])
            .pick_file()
    })
    .await
    .map_err(|error| format!("打开图片对话框失败：{error}"))
}

#[tauri::command]
pub async fn choose_background_image(
    window: WebviewWindow,
    state: State<'_, AppState>,
) -> CommandResult<Option<BackgroundPayload>> {
    let background_directory = state.background_directory.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let Some(source) = parented_file_dialog(&window)
            .set_title("选择 WIMD 背景图片")
            .add_filter("图片", &["png", "jpg", "jpeg", "bmp", "webp"])
            .pick_file()
        else {
            return Ok::<_, wimd_core::CoreError>(None);
        };
        let copied = store_managed_background(&source, &background_directory)?;
        let loaded = load_managed_background(&background_directory, &copied)?;
        Ok(Some(BackgroundPayload {
            path: copied,
            data_uri: loaded.data_uri,
        }))
    })
    .await
    .map_err(|error| format!("选择背景图片任务失败：{error}"))?
    .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn load_background_image(
    state: State<'_, AppState>,
    path: PathBuf,
) -> CommandResult<LocalImageData> {
    let background_directory = state.background_directory.clone();
    tauri::async_runtime::spawn_blocking(move || {
        load_managed_background(&background_directory, path)
    })
    .await
    .map_err(|error| format!("读取背景图片任务失败：{error}"))?
    .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn load_document(
    state: State<'_, AppState>,
    path: PathBuf,
) -> CommandResult<LoadedDocument> {
    let started = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || read_document(path))
        .await
        .map_err(|error| format!("读取文档任务失败：{error}"))?
        .map_err(|error| error.to_string());
    log_command_result(&state, "DocumentLoad", started, result)
}

#[tauri::command]
pub async fn save_document(
    state: State<'_, AppState>,
    path: PathBuf,
    text: String,
    had_utf8_bom: bool,
) -> CommandResult<DocumentStamp> {
    let started = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || {
        save_document_atomic(path, &text, had_utf8_bom)
    })
    .await
    .map_err(|error| format!("保存文档任务失败：{error}"))?
    .map_err(|error| error.to_string());
    log_command_result(&state, "DocumentSave", started, result)
}

#[tauri::command]
pub fn mark_pdf_window_ready(
    window: WebviewWindow,
    state: State<'_, AppState>,
    error: Option<String>,
) -> CommandResult<()> {
    let sender = state
        .pdf_ready_senders
        .lock()
        .map_err(|_| "PDF 窗口状态已损坏。".to_owned())?
        .remove(window.label())
        .ok_or_else(|| "找不到 PDF 窗口等待任务。".to_owned())?;
    sender
        .send(error.map_or(Ok(()), Err))
        .map_err(|_| "PDF 窗口等待任务已结束。".to_owned())
}

#[tauri::command]
pub async fn export_pdf(
    app: AppHandle,
    state: State<'_, AppState>,
    path: PathBuf,
    html: String,
    css: String,
    theme: String,
    inline_style: String,
) -> CommandResult<PathBuf> {
    let started = std::time::Instant::now();
    let path = pdf::ensure_pdf_extension(&path);
    let payload = pdf::PdfDocumentPayload {
        html,
        css,
        theme,
        inline_style,
    };
    let initialization_script = match pdf::initialization_script(&payload) {
        Ok(script) => script,
        Err(error) => {
            let _ = log_command_result::<PathBuf>(&state, "PdfExport", started, Err(error.clone()));
            return Err(error);
        }
    };
    let window_id = state
        .next_pdf_window_id
        .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
    let label = format!("pdf-export-{window_id}");
    let (ready_sender, ready_receiver) = std::sync::mpsc::sync_channel(1);
    state
        .pdf_ready_senders
        .lock()
        .map_err(|_| "PDF 窗口状态已损坏。".to_owned())?
        .insert(label.clone(), ready_sender);

    let window = match WebviewWindowBuilder::new(&app, &label, WebviewUrl::App("pdf.html".into()))
        .title("WIMD PDF")
        .inner_size(816.0, 1056.0)
        .visible(false)
        .decorations(false)
        .skip_taskbar(true)
        .resizable(false)
        .initialization_script(initialization_script)
        .build()
    {
        Ok(window) => window,
        Err(error) => {
            if let Ok(mut senders) = state.pdf_ready_senders.lock() {
                senders.remove(&label);
            }
            let error = format!("创建 PDF 内容窗口失败：{error}");
            let _ = log_command_result::<PathBuf>(&state, "PdfExport", started, Err(error.clone()));
            return Err(error);
        }
    };

    let ready = match tauri::async_runtime::spawn_blocking(move || {
        ready_receiver
            .recv_timeout(std::time::Duration::from_secs(30))
            .map_err(|_| "等待 PDF 内容准备超时。".to_owned())?
    })
    .await
    {
        Ok(ready) => ready,
        Err(error) => {
            let _ = window.destroy();
            if let Ok(mut senders) = state.pdf_ready_senders.lock() {
                senders.remove(&label);
            }
            let error = format!("等待 PDF 内容任务失败：{error}");
            let _ = log_command_result::<PathBuf>(&state, "PdfExport", started, Err(error.clone()));
            return Err(error);
        }
    };
    if let Err(error) = ready {
        let _ = window.destroy();
        if let Ok(mut senders) = state.pdf_ready_senders.lock() {
            senders.remove(&label);
        }
        let _ = log_command_result::<PathBuf>(&state, "PdfExport", started, Err(error.clone()));
        return Err(error);
    }

    let result = pdf::export_window_pdf(window.clone(), path)
        .await
        .map_err(|error| error.to_string());
    let _ = window.destroy();
    if let Ok(mut senders) = state.pdf_ready_senders.lock() {
        senders.remove(&label);
    }
    log_command_result(&state, "PdfExport", started, result)
}

#[tauri::command]
pub async fn inspect_document_stamp(path: PathBuf) -> CommandResult<DocumentStamp> {
    tauri::async_runtime::spawn_blocking(move || inspect_document(path))
        .await
        .map_err(|error| format!("检查文档任务失败：{error}"))?
        .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn render_preview(
    state: State<'_, AppState>,
    markdown: String,
) -> CommandResult<RenderedMarkdown> {
    let started = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || render_markdown(&markdown))
        .await
        .map_err(|error| format!("生成 Markdown 预览失败：{error}"))?;
    state.diagnostics.record_with(
        "MarkdownRender",
        "Succeeded",
        Some(started.elapsed().as_secs_f64() * 1000.0),
        None,
        None,
    );
    Ok(result)
}

#[tauri::command]
pub fn format_markdown(
    text: String,
    selection_start: usize,
    selection_length: usize,
    format: String,
    row_count: Option<usize>,
    column_count: Option<usize>,
) -> CommandResult<MarkdownTextEdit> {
    if format == "table-custom" {
        apply_table(
            &text,
            selection_start,
            selection_length,
            row_count.unwrap_or(3),
            column_count.unwrap_or(3),
        )
        .map_err(|error| error.to_string())
    } else {
        apply_format(&text, selection_start, selection_length, &format)
            .map_err(|error| error.to_string())
    }
}

#[tauri::command]
pub fn toggle_task(text: String, source_line: usize, completed: bool) -> CommandResult<TaskToggle> {
    toggle_task_line(&text, source_line, completed).map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn load_image(document_path: PathBuf, source: String) -> CommandResult<LocalImageData> {
    tauri::async_runtime::spawn_blocking(move || load_local_image(document_path, &source))
        .await
        .map_err(|error| format!("读取图片任务失败：{error}"))?
        .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn prepare_preview_image(
    state: State<'_, AppState>,
    source: String,
) -> CommandResult<PreparedImage> {
    state.image_cache.prepare(&source).await
}

#[tauri::command]
pub async fn pick_preview_image_save_path(
    window: WebviewWindow,
    suggested_name: String,
    format: String,
) -> CommandResult<Option<PathBuf>> {
    let (filter_name, extensions): (&str, &[&str]) = match format.as_str() {
        "png" => ("PNG 图片", &["png"]),
        "html" => ("HTML 网页", &["html"]),
        _ => (
            "所有图片",
            &["png", "jpg", "jpeg", "gif", "bmp", "webp", "svg"],
        ),
    };
    tauri::async_runtime::spawn_blocking(move || {
        parented_file_dialog(&window)
            .set_title("图片另存为")
            .set_file_name(suggested_name)
            .add_filter(filter_name, extensions)
            .save_file()
    })
    .await
    .map_err(|error| format!("图片保存对话框失败：{error}"))
}

#[tauri::command]
pub async fn save_preview_image(
    data_uri: String,
    format: String,
    target: PathBuf,
) -> CommandResult<PathBuf> {
    let parsed_format = image_export::parse_format(&format)?;
    tauri::async_runtime::spawn_blocking(move || {
        image_export::save(&data_uri, parsed_format, &target)
    })
    .await
    .map_err(|error| format!("保存预览图片任务失败：{error}"))?
}

#[tauri::command]
pub async fn insert_image(
    document_path: PathBuf,
    source_path: PathBuf,
    relative_directory: String,
) -> CommandResult<StoredImage> {
    tauri::async_runtime::spawn_blocking(move || {
        store_image_file(document_path, source_path, &relative_directory)
    })
    .await
    .map_err(|error| format!("保存图片任务失败：{error}"))?
    .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn insert_image_bytes(
    document_path: PathBuf,
    bytes: Vec<u8>,
    extension: String,
    relative_directory: String,
) -> CommandResult<StoredImage> {
    tauri::async_runtime::spawn_blocking(move || {
        store_image_bytes(document_path, &bytes, &extension, &relative_directory)
    })
    .await
    .map_err(|error| format!("保存剪贴板图片任务失败：{error}"))?
    .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn save_settings(
    app: AppHandle,
    state: State<'_, AppState>,
    settings: ApplicationSettings,
) -> CommandResult<ApplicationSettings> {
    let baseline = state.settings_snapshot()?;
    let normalized = settings.normalize();
    effective_shortcuts(
        &normalized
            .shortcut_overrides
            .iter()
            .map(|(command, gesture)| (command.clone(), gesture.clone()))
            .collect(),
    )
    .map_err(|error| error.to_string())?;
    let merged =
        save_merged_settings_background(state.settings_store.clone(), baseline, normalized).await?;
    let mut current = state
        .settings
        .lock()
        .map_err(|_| "设置状态已损坏。".to_owned())?;
    *current = merged.clone();
    app.emit_to("main", "wimd://settings-changed", merged.clone())
        .map_err(|error| format!("广播设置更新失败：{error}"))?;
    Ok(merged)
}

#[tauri::command]
pub async fn save_imgbb_api_key(
    state: State<'_, AppState>,
    secret: String,
) -> CommandResult<ApplicationSettings> {
    let protected = secrets::protect_secret(&secret)?;
    commit_settings_mutation(&state, |settings| {
        settings.protected_imgbb_api_key = Some(protected);
        settings.image_storage_mode = ImageStorageMode::ImgBb;
    })
    .await
}

#[tauri::command]
pub async fn clear_imgbb_api_key(state: State<'_, AppState>) -> CommandResult<ApplicationSettings> {
    commit_settings_mutation(&state, |settings| {
        settings.protected_imgbb_api_key = None;
        settings.image_storage_mode = ImageStorageMode::Local;
    })
    .await
}

#[tauri::command]
pub async fn upload_image_to_imgbb(
    state: State<'_, AppState>,
    source_path: Option<PathBuf>,
    bytes: Option<Vec<u8>>,
    extension: Option<String>,
) -> CommandResult<UploadedImagePayload> {
    let protected = state
        .settings_snapshot()?
        .protected_imgbb_api_key
        .ok_or_else(|| "请先在图片设置中保存 ImgBB API Key。".to_owned())?;
    let api_key = secrets::unprotect_secret(&protected)?;
    let (image_bytes, file_name) = if let Some(path) = source_path {
        let file_name = path
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("image.png")
            .to_owned();
        let bytes = tauri::async_runtime::spawn_blocking(move || std::fs::read(path))
            .await
            .map_err(|error| format!("读取图床上传图片任务失败：{error}"))?
            .map_err(|error| format!("读取图床上传图片失败：{error}"))?;
        (bytes, file_name)
    } else {
        let bytes = bytes.ok_or_else(|| "缺少待上传图片。".to_owned())?;
        let extension = extension.unwrap_or_else(|| "png".to_owned());
        (bytes, format!("image.{extension}"))
    };
    let hosted = imgbb::upload(image_bytes, file_name, api_key).await?;
    Ok(UploadedImagePayload {
        url: hosted.url,
        delete_url: hosted.delete_url,
    })
}

#[tauri::command]
pub async fn record_recent_path(
    state: State<'_, AppState>,
    path: PathBuf,
) -> CommandResult<ApplicationSettings> {
    commit_settings_mutation(&state, |settings| {
        settings.record_recent_file(path, Utc::now());
    })
    .await
}

#[tauri::command]
pub async fn remove_recent_path(
    state: State<'_, AppState>,
    path: PathBuf,
) -> CommandResult<ApplicationSettings> {
    commit_settings_mutation(&state, |settings| {
        settings.remove_recent_file(path);
    })
    .await
}

#[tauri::command]
pub async fn record_recent_workspace(
    state: State<'_, AppState>,
    path: PathBuf,
) -> CommandResult<ApplicationSettings> {
    commit_settings_mutation(&state, |settings| {
        settings.record_recent_workspace(path, Utc::now());
    })
    .await
}

#[tauri::command]
pub async fn remove_recent_workspace(
    state: State<'_, AppState>,
    path: PathBuf,
) -> CommandResult<ApplicationSettings> {
    commit_settings_mutation(&state, |settings| {
        settings.remove_recent_workspace(path);
    })
    .await
}

#[tauri::command]
pub async fn open_workspace(
    state: State<'_, AppState>,
    path: PathBuf,
) -> CommandResult<WorkspacePayload> {
    let started = std::time::Instant::now();
    let result = tauri::async_runtime::spawn_blocking(move || {
        let workspace = Workspace::open(&path)?;
        let entries = workspace.children(workspace.root())?;
        Ok::<_, wimd_core::CoreError>(WorkspacePayload {
            root: workspace.root().to_path_buf(),
            entries,
        })
    })
    .await
    .map_err(|error| format!("读取工作区任务失败：{error}"))?
    .map_err(|error| error.to_string());
    log_command_result(&state, "WorkspaceOpen", started, result)
}

#[tauri::command]
pub async fn workspace_children(
    state: State<'_, AppState>,
    root: PathBuf,
    directory: PathBuf,
) -> CommandResult<Vec<WorkspaceEntry>> {
    let started = std::time::Instant::now();
    let result =
        tauri::async_runtime::spawn_blocking(move || Workspace::open(root)?.children(directory))
            .await
            .map_err(|error| format!("读取工作区子目录任务失败：{error}"))?
            .map_err(|error| error.to_string());
    log_command_result(&state, "WorkspaceChildren", started, result)
}

#[tauri::command]
pub async fn watch_workspace(
    app: AppHandle,
    state: State<'_, AppState>,
    root: PathBuf,
) -> CommandResult<()> {
    let normalized = normalize_path(&root).map_err(|error| error.to_string())?;
    let mut watchers = state
        .watchers
        .lock()
        .map_err(|_| "工作区监视状态已损坏。".to_owned())?;
    watchers.remove(&normalized);
    let event_root = normalized.clone();
    let mut watcher =
        notify::recommended_watcher(move |result: Result<notify::Event, notify::Error>| {
            if result.is_ok() {
                let _ = app.emit(
                    "wimd://workspace-changed",
                    DocumentChangedPayload {
                        path: event_root.clone(),
                    },
                );
            }
        })
        .map_err(|error| format!("创建工作区监视器失败：{error}"))?;
    watcher
        .watch(&normalized, RecursiveMode::Recursive)
        .map_err(|error| format!("启动工作区监视失败：{error}"))?;
    watchers.insert(normalized, watcher);
    Ok(())
}

#[tauri::command]
pub async fn create_workspace_markdown(
    root: PathBuf,
    parent: PathBuf,
    name: String,
) -> CommandResult<PathBuf> {
    tauri::async_runtime::spawn_blocking(move || {
        Workspace::open(root)?.create_markdown_file(parent, &name, "# 新文档\n")
    })
    .await
    .map_err(|error| format!("新建文档任务失败：{error}"))?
    .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn create_workspace_directory(
    root: PathBuf,
    parent: PathBuf,
    name: String,
) -> CommandResult<PathBuf> {
    tauri::async_runtime::spawn_blocking(move || {
        Workspace::open(root)?.create_directory(parent, &name)
    })
    .await
    .map_err(|error| format!("新建文件夹任务失败：{error}"))?
    .map_err(|error| error.to_string())
}

#[tauri::command]
pub async fn rename_workspace_entry(
    state: State<'_, AppState>,
    root: PathBuf,
    path: PathBuf,
    new_name: String,
) -> CommandResult<RenameWorkspacePayload> {
    let source = path.clone();
    let target = tauri::async_runtime::spawn_blocking(move || {
        Workspace::open(root)?.rename_entry(path, &new_name)
    })
    .await
    .map_err(|error| format!("重命名任务失败：{error}"))?
    .map_err(|error| error.to_string())?;

    let baseline = state.settings_snapshot()?;
    let mut incoming = baseline.clone();
    incoming.relocate_paths(&source, &target);
    let settings = match save_merged_settings_background(
        state.settings_store.clone(),
        baseline,
        incoming.clone(),
    )
    .await
    {
        Ok(merged) => merged,
        Err(error) => {
            state.diagnostics.record_with(
                "WorkspaceRenameSettings",
                "Failed",
                None,
                None,
                Some("SettingsPersistenceFailed"),
            );
            let _ = error;
            incoming
        }
    };
    *state
        .settings
        .lock()
        .map_err(|_| "设置状态已损坏。".to_owned())? = settings.clone();

    if let Ok(mut watchers) = state.watchers.lock() {
        watchers.retain(|watched, _| !path_is_at_or_below(watched, &source));
    }
    Ok(RenameWorkspacePayload { target, settings })
}

#[tauri::command]
pub async fn delete_workspace_entry(
    state: State<'_, AppState>,
    root: PathBuf,
    path: PathBuf,
) -> CommandResult<DeleteWorkspacePayload> {
    let deleted_path = path.clone();
    tauri::async_runtime::spawn_blocking(move || Workspace::open(root)?.delete_entry(path))
        .await
        .map_err(|error| format!("删除任务失败：{error}"))?
        .map_err(|error| error.to_string())?;
    let baseline = state.settings_snapshot()?;
    let mut incoming = baseline.clone();
    incoming.remove_recent_files_at_or_below(&deleted_path);
    let settings = match save_merged_settings_background(
        state.settings_store.clone(),
        baseline,
        incoming.clone(),
    )
    .await
    {
        Ok(merged) => merged,
        Err(error) => {
            state.diagnostics.record_with(
                "WorkspaceDeleteSettings",
                "Failed",
                None,
                None,
                Some("SettingsPersistenceFailed"),
            );
            let _ = error;
            incoming
        }
    };
    *state
        .settings
        .lock()
        .map_err(|_| "设置状态已损坏。".to_owned())? = settings.clone();
    if let Ok(mut watchers) = state.watchers.lock() {
        watchers.retain(|watched, _| !path_is_at_or_below(watched, &deleted_path));
    }
    Ok(DeleteWorkspacePayload { settings })
}

#[tauri::command]
pub async fn watch_document(
    app: AppHandle,
    state: State<'_, AppState>,
    path: PathBuf,
) -> CommandResult<()> {
    let normalized = normalize_path(&path).map_err(|error| error.to_string())?;
    let mut watchers = state
        .watchers
        .lock()
        .map_err(|_| "文件监视状态已损坏。".to_owned())?;
    watchers.remove(&normalized);

    let event_path = normalized.clone();
    let mut watcher =
        notify::recommended_watcher(move |result: Result<notify::Event, notify::Error>| {
            if result.is_ok() {
                let _ = app.emit(
                    "wimd://document-changed",
                    DocumentChangedPayload {
                        path: event_path.clone(),
                    },
                );
            }
        })
        .map_err(|error| format!("创建文件监视器失败：{error}"))?;
    watcher
        .watch(&normalized, RecursiveMode::NonRecursive)
        .map_err(|error| format!("启动文件监视失败：{error}"))?;
    watchers.insert(normalized, watcher);
    Ok(())
}

#[tauri::command]
pub fn copy_text(text: String) -> CommandResult<()> {
    if text.len() > 4 * 1024 * 1024 {
        return Err("复制内容超过 4 MiB 上限。".to_owned());
    }
    clipboard_win::set_clipboard(clipboard_win::formats::Unicode, text)
        .map_err(|error| format!("写入剪贴板失败：{error}"))
}

#[tauri::command]
pub fn read_clipboard_text() -> CommandResult<String> {
    let text = clipboard_win::get_clipboard_string()
        .map_err(|error| format!("读取剪贴板失败：{error}"))?;
    if text.len() > 4 * 1024 * 1024 {
        return Err("剪贴板文本超过 4 MiB 上限。".to_owned());
    }
    Ok(text)
}

#[tauri::command]
pub async fn open_external(url: String) -> CommandResult<()> {
    let lower = url.to_ascii_lowercase();
    if !(lower.starts_with("http://")
        || lower.starts_with("https://")
        || lower.starts_with("mailto:"))
    {
        return Err("只允许打开 HTTP、HTTPS 或 mailto 链接。".to_owned());
    }
    tauri::async_runtime::spawn_blocking(move || open::that_detached(url))
        .await
        .map_err(|error| format!("打开外部链接任务失败：{error}"))?
        .map_err(|error| format!("打开外部链接失败：{error}"))
}

#[tauri::command]
pub fn resolve_local_link(
    document_path: PathBuf,
    href: String,
) -> CommandResult<crate::local_links::LocalLinkTarget> {
    crate::local_links::resolve_local_link(&document_path, &href)
}

#[tauri::command]
pub async fn open_local_link(document_path: PathBuf, href: String) -> CommandResult<PathBuf> {
    tauri::async_runtime::spawn_blocking(move || -> CommandResult<PathBuf> {
        let target = crate::local_links::resolve_local_link(&document_path, &href)?;
        if !target.exists || !target.path.is_file() {
            return Err("本地链接目标不存在或不是文件。".to_owned());
        }
        open::that_detached(&target.path).map_err(|error| format!("打开本地链接失败：{error}"))?;
        Ok(target.path)
    })
    .await
    .map_err(|error| format!("打开本地链接任务失败：{error}"))?
}

#[tauri::command]
pub async fn reveal_in_explorer(path: PathBuf) -> CommandResult<()> {
    tauri::async_runtime::spawn_blocking(move || -> Result<(), std::io::Error> {
        if path.is_file() {
            Command::new("explorer.exe")
                .arg(format!("/select,{}", path.to_string_lossy()))
                .spawn()?;
        } else {
            Command::new("explorer.exe").arg(&path).spawn()?;
        }
        Ok(())
    })
    .await
    .map_err(|error| format!("打开资源管理器任务失败：{error}"))?
    .map_err(|error| format!("打开资源管理器失败：{error}"))
}

#[tauri::command]
pub async fn open_in_new_window(path: PathBuf) -> CommandResult<()> {
    tauri::async_runtime::spawn_blocking(move || {
        let executable = std::env::current_exe()?;
        Command::new(executable).arg(path).spawn().map(|_| ())
    })
    .await
    .map_err(|error| format!("打开新窗口任务失败：{error}"))?
    .map_err(|error| format!("打开新窗口失败：{error}"))
}

fn path_is_at_or_below(candidate: &Path, root: &Path) -> bool {
    let candidate = candidate
        .to_string_lossy()
        .to_lowercase()
        .trim_end_matches(['\\', '/'])
        .to_owned();
    let root = root
        .to_string_lossy()
        .to_lowercase()
        .trim_end_matches(['\\', '/'])
        .to_owned();
    candidate == root
        || candidate
            .strip_prefix(&root)
            .is_some_and(|suffix| suffix.starts_with(['\\', '/']))
}

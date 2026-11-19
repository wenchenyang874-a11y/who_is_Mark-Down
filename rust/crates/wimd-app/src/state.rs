use std::{
    collections::HashMap,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64},
        mpsc::SyncSender,
    },
};

use notify::RecommendedWatcher;
use serde::Serialize;
use wimd_core::settings::{ApplicationSettings, SettingsStore};

use crate::{
    diagnostics::DiagnosticLog, heartbeat::UiHeartbeatMonitor, image_export::ImageResponseCache,
    restart_session::UpdateRestartWindowSnapshot,
};

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ViewerImagePayload {
    pub request_id: u64,
    pub source: String,
    pub title: String,
    pub theme: String,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsWindowContext {
    pub kind: String,
    pub document_path: Option<PathBuf>,
}

pub struct AppState {
    pub data_directory: PathBuf,
    pub settings_store: SettingsStore,
    pub settings: Mutex<ApplicationSettings>,
    pub settings_warning: Option<String>,
    pub startup_paths: Vec<PathBuf>,
    pub restart_window_id: String,
    pub update_restart_window: Option<UpdateRestartWindowSnapshot>,
    pub update_restart_claim_path: Mutex<Option<PathBuf>>,
    pub background_directory: PathBuf,
    pub recovery_directory: PathBuf,
    pub updates_directory: PathBuf,
    pub diagnostics: DiagnosticLog,
    pub heartbeat: UiHeartbeatMonitor,
    pub watchers: Mutex<HashMap<PathBuf, RecommendedWatcher>>,
    pub viewer_image: Mutex<Option<ViewerImagePayload>>,
    pub settings_window_context: Mutex<HashMap<String, SettingsWindowContext>>,
    pub next_settings_window_id: AtomicU64,
    pub pdf_ready_senders: Mutex<HashMap<String, SyncSender<Result<(), String>>>>,
    pub next_pdf_window_id: AtomicU64,
    pub next_viewer_request: AtomicU64,
    pub cancel_update_download: Arc<AtomicBool>,
    pub image_cache: ImageResponseCache,
}

pub struct AppStateInit {
    pub data_directory: PathBuf,
    pub settings_store: SettingsStore,
    pub startup_paths: Vec<PathBuf>,
    pub restart_window_id: String,
    pub update_restart_window: Option<UpdateRestartWindowSnapshot>,
    pub update_restart_claim_path: Option<PathBuf>,
    pub background_directory: PathBuf,
    pub recovery_directory: PathBuf,
    pub updates_directory: PathBuf,
    pub diagnostics: DiagnosticLog,
}

impl AppState {
    pub fn new(init: AppStateInit) -> Self {
        let loaded = init.settings_store.load();
        let heartbeat = UiHeartbeatMonitor::new(init.diagnostics.clone());
        Self {
            data_directory: init.data_directory,
            settings_store: init.settings_store,
            settings: Mutex::new(loaded.settings),
            settings_warning: loaded.warning,
            startup_paths: init.startup_paths,
            restart_window_id: init.restart_window_id,
            update_restart_window: init.update_restart_window,
            update_restart_claim_path: Mutex::new(init.update_restart_claim_path),
            background_directory: init.background_directory,
            recovery_directory: init.recovery_directory,
            updates_directory: init.updates_directory,
            diagnostics: init.diagnostics,
            heartbeat,
            watchers: Mutex::new(HashMap::new()),
            viewer_image: Mutex::new(None),
            settings_window_context: Mutex::new(HashMap::new()),
            next_settings_window_id: AtomicU64::new(1),
            pdf_ready_senders: Mutex::new(HashMap::new()),
            next_pdf_window_id: AtomicU64::new(1),
            next_viewer_request: AtomicU64::new(1),
            cancel_update_download: Arc::new(AtomicBool::new(false)),
            image_cache: ImageResponseCache::default(),
        }
    }

    pub fn settings_snapshot(&self) -> Result<ApplicationSettings, String> {
        self.settings
            .lock()
            .map(|settings| settings.clone())
            .map_err(|_| "设置状态已损坏。".to_owned())
    }
}

use std::{
    collections::HashMap,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64},
    },
};

use notify::RecommendedWatcher;
use serde::Serialize;
use wimd_core::settings::{ApplicationSettings, SettingsStore};

use crate::{
    diagnostics::DiagnosticLog, heartbeat::UiHeartbeatMonitor, image_export::ImageResponseCache,
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
    pub background_directory: PathBuf,
    pub recovery_directory: PathBuf,
    pub updates_directory: PathBuf,
    pub diagnostics: DiagnosticLog,
    pub heartbeat: UiHeartbeatMonitor,
    pub watchers: Mutex<HashMap<PathBuf, RecommendedWatcher>>,
    pub viewer_image: Mutex<Option<ViewerImagePayload>>,
    pub settings_window_context: Mutex<HashMap<String, SettingsWindowContext>>,
    pub next_settings_window_id: AtomicU64,
    pub next_viewer_request: AtomicU64,
    pub cancel_update_download: Arc<AtomicBool>,
    pub image_cache: ImageResponseCache,
}

impl AppState {
    pub fn new(
        data_directory: PathBuf,
        settings_store: SettingsStore,
        startup_paths: Vec<PathBuf>,
        background_directory: PathBuf,
        recovery_directory: PathBuf,
        updates_directory: PathBuf,
        diagnostics: DiagnosticLog,
    ) -> Self {
        let loaded = settings_store.load();
        let heartbeat = UiHeartbeatMonitor::new(diagnostics.clone());
        Self {
            data_directory,
            settings_store,
            settings: Mutex::new(loaded.settings),
            settings_warning: loaded.warning,
            startup_paths,
            background_directory,
            recovery_directory,
            updates_directory,
            diagnostics,
            heartbeat,
            watchers: Mutex::new(HashMap::new()),
            viewer_image: Mutex::new(None),
            settings_window_context: Mutex::new(HashMap::new()),
            next_settings_window_id: AtomicU64::new(1),
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

use std::{
    collections::{BTreeMap, HashSet},
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    thread,
    time::{Duration, Instant},
};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Deserializer, Serialize};
use uuid::Uuid;

#[cfg(windows)]
use std::os::windows::fs::OpenOptionsExt;

use crate::{
    document::{normalize_path, replace_file},
    error::{CoreError, CoreResult},
    workspace::is_safe_relative_directory,
};

pub const MAXIMUM_RECENT_FILES: usize = 10;

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum Theme {
    #[default]
    System,
    Light,
    Dark,
    Warm,
    Rose,
    Sage,
    Mist,
    Lilac,
    Midnight,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ViewMode {
    EditorOnly,
    #[default]
    EditorAndPreview,
    PreviewOnly,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum FileOpenViewMode {
    EditorOnly,
    EditorAndPreview,
    PreviewOnly,
    #[default]
    RememberLast,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum OrderedListStyle {
    #[default]
    Plain,
    Bold,
    Circle,
    Square,
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct PreviewStyleOverrides {
    #[serde(deserialize_with = "deserialize_optional_flexible_ordered_list_style")]
    pub ordered_list: Option<OrderedListStyle>,
}

impl PreviewStyleOverrides {
    pub fn resolved_ordered_list(&self, fallback: OrderedListStyle) -> OrderedListStyle {
        self.ordered_list.unwrap_or(fallback)
    }

    pub fn is_empty(&self) -> bool {
        self.ordered_list.is_none()
    }
}

#[derive(Clone, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct PreviewStyleFileSettings {
    #[serde(deserialize_with = "deserialize_preview_style_file_map")]
    pub file_overrides: BTreeMap<String, PreviewStyleOverrides>,
}

impl PreviewStyleFileSettings {
    pub fn normalize(mut self) -> Self {
        let mut seen = HashSet::new();
        self.file_overrides = self
            .file_overrides
            .into_iter()
            .filter_map(|(path, overrides)| {
                let path = normalize_absolute_preference_path(&path)?;
                if overrides.is_empty() || !seen.insert(path_key(Path::new(&path))) {
                    None
                } else {
                    Some((path, overrides))
                }
            })
            .collect();
        self
    }

    pub fn override_for(&self, path: Option<&Path>) -> PreviewStyleOverrides {
        let Some(path) = path.filter(|path| path.is_absolute()) else {
            return PreviewStyleOverrides::default();
        };
        let key = path_key(path);
        self.file_overrides
            .iter()
            .find(|(candidate, _)| path_key(Path::new(candidate)) == key)
            .map(|(_, overrides)| overrides.clone())
            .unwrap_or_default()
    }

    pub fn set_override(&mut self, path: &Path, overrides: PreviewStyleOverrides) {
        if !path.is_absolute() {
            return;
        }
        let key = path.to_string_lossy().into_owned();
        if overrides.is_empty() {
            self.file_overrides.remove(&key);
            let normalized_key = path_key(path);
            self.file_overrides
                .retain(|candidate, _| path_key(Path::new(candidate)) != normalized_key);
        } else {
            self.set_override_normalized(path, overrides);
        }
    }

    fn set_override_normalized(&mut self, path: &Path, overrides: PreviewStyleOverrides) {
        let key = path.to_string_lossy().into_owned();
        if overrides.is_empty() {
            self.file_overrides.remove(&key);
            return;
        }
        self.file_overrides
            .retain(|candidate, _| !path_key(Path::new(candidate)).eq(&path_key(path)));
        self.file_overrides.insert(key, overrides);
    }

    fn relocate(&mut self, source: &Path, target: &Path) {
        let relocated = std::mem::take(&mut self.file_overrides)
            .into_iter()
            .map(|(path, overrides)| {
                let path_value = PathBuf::from(&path);
                if let Some(relative) = relative_path_when_contained(&path_value, source) {
                    (
                        target.join(relative).to_string_lossy().into_owned(),
                        overrides,
                    )
                } else {
                    (path, overrides)
                }
            })
            .collect();
        self.file_overrides = relocated;
    }
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ImageStorageMode {
    #[default]
    Local,
    ImgBb,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum ExternalFileChangeBehavior {
    #[default]
    Prompt,
    AutoReload,
}

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RecentFile {
    pub path: PathBuf,
    pub last_opened_utc: DateTime<Utc>,
}

#[derive(Clone, Debug, Deserialize, PartialEq, Serialize)]
#[serde(default, rename_all = "camelCase")]
pub struct ApplicationSettings {
    pub recent_files: Vec<RecentFile>,
    pub recent_workspaces: Vec<RecentFile>,
    #[serde(deserialize_with = "deserialize_theme")]
    pub theme: Theme,
    pub editor_font_family: String,
    pub editor_font_size: f64,
    pub preview_font_family: String,
    pub preview_font_size: f64,
    pub sidebar_visible: bool,
    pub sidebar_width: f64,
    pub split_ratio: f64,
    pub window_x: Option<i32>,
    pub window_y: Option<i32>,
    pub window_width: u32,
    pub window_height: u32,
    pub window_maximized: bool,
    pub background_image_path: Option<PathBuf>,
    pub background_opacity: f64,
    #[serde(deserialize_with = "deserialize_file_open_view_mode")]
    pub file_open_view_mode: FileOpenViewMode,
    #[serde(deserialize_with = "deserialize_view_mode")]
    pub last_view_mode: ViewMode,
    #[serde(deserialize_with = "deserialize_ordered_list_style")]
    pub ordered_list_style: OrderedListStyle,
    pub preview_style_files: PreviewStyleFileSettings,
    pub local_image_directory: String,
    #[serde(deserialize_with = "deserialize_image_storage_mode")]
    pub image_storage_mode: ImageStorageMode,
    pub protected_imgbb_api_key: Option<String>,
    #[serde(deserialize_with = "deserialize_external_file_behavior")]
    pub external_file_change_behavior: ExternalFileChangeBehavior,
    #[serde(deserialize_with = "deserialize_external_file_override_map")]
    pub external_file_overrides: BTreeMap<String, ExternalFileChangeBehavior>,
    pub check_for_updates_on_startup: bool,
    pub last_workspace: Option<PathBuf>,
    #[serde(deserialize_with = "deserialize_shortcut_overrides")]
    pub shortcut_overrides: BTreeMap<String, String>,
    #[serde(
        rename = "isRecentPaneExpanded",
        default,
        skip_serializing,
        deserialize_with = "deserialize_optional_bool"
    )]
    legacy_recent_pane_expanded: Option<bool>,
    #[serde(
        rename = "imageInsertion",
        default,
        skip_serializing,
        deserialize_with = "deserialize_optional"
    )]
    legacy_image_insertion: Option<LegacyImageInsertion>,
    #[serde(
        rename = "appearance",
        default,
        skip_serializing,
        deserialize_with = "deserialize_optional"
    )]
    legacy_appearance: Option<LegacyAppearance>,
    #[serde(
        rename = "previewStyles",
        default,
        skip_serializing,
        deserialize_with = "deserialize_optional"
    )]
    legacy_preview_styles: Option<LegacyPreviewStyles>,
    #[serde(
        rename = "externalFileChanges",
        default,
        skip_serializing,
        deserialize_with = "deserialize_optional"
    )]
    legacy_external_file_changes: Option<LegacyExternalFileChanges>,
}

impl Default for ApplicationSettings {
    fn default() -> Self {
        Self {
            recent_files: Vec::new(),
            recent_workspaces: Vec::new(),
            theme: Theme::System,
            editor_font_family: String::new(),
            editor_font_size: 15.0,
            preview_font_family: String::new(),
            preview_font_size: 16.0,
            sidebar_visible: true,
            sidebar_width: 252.0,
            split_ratio: 0.5,
            window_x: None,
            window_y: None,
            window_width: 1320,
            window_height: 820,
            window_maximized: false,
            background_image_path: None,
            background_opacity: 0.18,
            file_open_view_mode: FileOpenViewMode::RememberLast,
            last_view_mode: ViewMode::EditorAndPreview,
            ordered_list_style: OrderedListStyle::Plain,
            preview_style_files: PreviewStyleFileSettings::default(),
            local_image_directory: "img".to_owned(),
            image_storage_mode: ImageStorageMode::Local,
            protected_imgbb_api_key: None,
            external_file_change_behavior: ExternalFileChangeBehavior::Prompt,
            external_file_overrides: BTreeMap::new(),
            check_for_updates_on_startup: false,
            last_workspace: None,
            shortcut_overrides: BTreeMap::new(),
            legacy_recent_pane_expanded: None,
            legacy_image_insertion: None,
            legacy_appearance: None,
            legacy_preview_styles: None,
            legacy_external_file_changes: None,
        }
    }
}

impl ApplicationSettings {
    pub fn merge_changes(baseline: Self, incoming: Self, latest: Self) -> Self {
        let mut merged = latest;
        macro_rules! merge_scalar {
            ($($field:ident),+ $(,)?) => {
                $(
                    if incoming.$field != baseline.$field {
                        merged.$field = incoming.$field;
                    }
                )+
            };
        }

        merge_scalar!(
            theme,
            editor_font_family,
            editor_font_size,
            preview_font_family,
            preview_font_size,
            sidebar_visible,
            sidebar_width,
            split_ratio,
            window_x,
            window_y,
            window_width,
            window_height,
            window_maximized,
            background_image_path,
            background_opacity,
            file_open_view_mode,
            last_view_mode,
            ordered_list_style,
            local_image_directory,
            image_storage_mode,
            protected_imgbb_api_key,
            external_file_change_behavior,
            check_for_updates_on_startup,
            last_workspace,
        );
        let latest_recent_files = std::mem::take(&mut merged.recent_files);
        merged.recent_files = merge_recent_entries(
            latest_recent_files,
            baseline.recent_files,
            incoming.recent_files,
        );
        let latest_recent_workspaces = std::mem::take(&mut merged.recent_workspaces);
        merged.recent_workspaces = merge_recent_entries(
            latest_recent_workspaces,
            baseline.recent_workspaces,
            incoming.recent_workspaces,
        );
        let latest_external_overrides = std::mem::take(&mut merged.external_file_overrides);
        merged.external_file_overrides = merge_map_changes(
            latest_external_overrides,
            baseline.external_file_overrides,
            incoming.external_file_overrides,
        );
        let latest_shortcuts = std::mem::take(&mut merged.shortcut_overrides);
        merged.shortcut_overrides = merge_map_changes(
            latest_shortcuts,
            baseline.shortcut_overrides,
            incoming.shortcut_overrides,
        );
        let latest_preview_styles = std::mem::take(&mut merged.preview_style_files.file_overrides);
        merged.preview_style_files.file_overrides = merge_map_changes(
            latest_preview_styles,
            baseline.preview_style_files.file_overrides,
            incoming.preview_style_files.file_overrides,
        );
        merged.legacy_recent_pane_expanded = None;
        merged.legacy_image_insertion = None;
        merged.legacy_appearance = None;
        merged.legacy_preview_styles = None;
        merged.legacy_external_file_changes = None;
        merged.normalize()
    }

    pub fn normalize(mut self) -> Self {
        self.apply_legacy_fields();
        let mut seen = HashSet::new();
        self.recent_files = self
            .recent_files
            .into_iter()
            .filter(|entry| !entry.path.as_os_str().is_empty())
            .filter(|entry| seen.insert(path_key(&entry.path)))
            .collect();
        self.recent_files
            .sort_by_key(|entry| std::cmp::Reverse(entry.last_opened_utc));
        self.recent_files.truncate(MAXIMUM_RECENT_FILES);
        let mut seen_workspaces = HashSet::new();
        self.recent_workspaces = self
            .recent_workspaces
            .into_iter()
            .filter(|entry| !entry.path.as_os_str().is_empty())
            .filter(|entry| seen_workspaces.insert(path_key(&entry.path)))
            .collect();
        self.recent_workspaces
            .sort_by_key(|entry| std::cmp::Reverse(entry.last_opened_utc));
        self.recent_workspaces.truncate(MAXIMUM_RECENT_FILES);

        self.editor_font_size = normalize_font_size(self.editor_font_size, 15.0);
        self.preview_font_size = normalize_font_size(self.preview_font_size, 16.0);
        self.sidebar_width = self.sidebar_width.clamp(180.0, 520.0);
        self.split_ratio = self.split_ratio.clamp(0.18, 0.82);
        self.window_width = self.window_width.clamp(900, 10_000);
        self.window_height = self.window_height.clamp(600, 10_000);
        self.background_opacity = if self.background_opacity.is_finite() {
            self.background_opacity.clamp(0.0, 1.0)
        } else {
            0.18
        };
        if !is_safe_relative_directory(&self.local_image_directory) {
            self.local_image_directory = "img".to_owned();
        }
        self.protected_imgbb_api_key = self
            .protected_imgbb_api_key
            .filter(|key| !key.trim().is_empty() && key.len() <= 16_384)
            .map(|key| key.trim().to_owned());
        let mut seen_external_paths = HashSet::new();
        self.external_file_overrides = self
            .external_file_overrides
            .into_iter()
            .filter_map(|(path, behavior)| {
                let raw_path = path.trim();
                if raw_path.is_empty() {
                    return None;
                }
                let path = normalize_recent_path(Path::new(raw_path))
                    .to_string_lossy()
                    .into_owned();
                seen_external_paths
                    .insert(path_key(Path::new(&path)))
                    .then_some((path, behavior))
            })
            .collect();
        self.preview_style_files = self.preview_style_files.normalize();
        self.shortcut_overrides.retain(|command, gesture| {
            !command.trim().is_empty()
                && !gesture.trim().is_empty()
                && gesture.len() <= 80
                && !gesture.chars().any(char::is_control)
        });
        self
    }

    fn apply_legacy_fields(&mut self) {
        if let Some(expanded) = self.legacy_recent_pane_expanded.take() {
            self.sidebar_visible = expanded;
        }
        if let Some(image) = self.legacy_image_insertion.take() {
            if let Some(value) = image.storage_mode {
                self.image_storage_mode = value;
            }
            if let Some(value) = image.local_directory {
                self.local_image_directory = value;
            }
            if image.protected_img_bb_api_key.is_some() {
                self.protected_imgbb_api_key = image.protected_img_bb_api_key;
            }
        }
        if let Some(appearance) = self.legacy_appearance.take() {
            if let Some(value) = appearance.theme {
                self.theme = value;
            }
            if appearance.editor_font_family.is_some() {
                self.editor_font_family = appearance.editor_font_family.unwrap_or_default();
            }
            if let Some(value) = appearance.editor_font_size {
                self.editor_font_size = value;
            }
            if appearance.preview_font_family.is_some() {
                self.preview_font_family = appearance.preview_font_family.unwrap_or_default();
            }
            if let Some(value) = appearance.preview_font_size {
                self.preview_font_size = value;
            }
        }
        if let Some(styles) = self.legacy_preview_styles.take()
            && let Some(value) = styles.ordered_list
        {
            self.ordered_list_style = value;
        }
        if let Some(changes) = self.legacy_external_file_changes.take() {
            if let Some(value) = changes.default_behavior {
                self.external_file_change_behavior = value;
            }
            if !changes.file_overrides.is_empty() {
                self.external_file_overrides = changes.file_overrides;
            }
        }
    }

    pub fn record_recent_file(&mut self, path: impl AsRef<Path>, opened_at: DateTime<Utc>) {
        let path = normalize_recent_path(path.as_ref());
        let key = path_key(&path);
        if let Some(existing) = self
            .recent_files
            .iter_mut()
            .find(|entry| path_key(&entry.path) == key)
        {
            // Keep the visible position stable during this session. The next load
            // sorts by this refreshed timestamp.
            existing.last_opened_utc = opened_at;
            return;
        }

        self.recent_files.insert(
            0,
            RecentFile {
                path,
                last_opened_utc: opened_at,
            },
        );
        self.recent_files.truncate(MAXIMUM_RECENT_FILES);
    }

    pub fn remove_recent_file(&mut self, path: impl AsRef<Path>) {
        let key = path_key(path.as_ref());
        self.recent_files
            .retain(|entry| path_key(&entry.path) != key);
    }

    pub fn record_recent_workspace(&mut self, path: impl AsRef<Path>, opened_at: DateTime<Utc>) {
        let path = normalize_recent_path(path.as_ref());
        let key = path_key(&path);
        if let Some(existing) = self
            .recent_workspaces
            .iter_mut()
            .find(|entry| path_key(&entry.path) == key)
        {
            existing.last_opened_utc = opened_at;
        } else {
            self.recent_workspaces.insert(
                0,
                RecentFile {
                    path,
                    last_opened_utc: opened_at,
                },
            );
        }
        self.recent_workspaces.truncate(MAXIMUM_RECENT_FILES);
    }

    pub fn remove_recent_workspace(&mut self, path: impl AsRef<Path>) {
        let key = path_key(path.as_ref());
        self.recent_workspaces
            .retain(|entry| path_key(&entry.path) != key);
    }

    pub fn remove_recent_files_at_or_below(&mut self, path: impl AsRef<Path>) {
        let root = normalize_recent_path(path.as_ref());
        self.recent_files
            .retain(|entry| !path_is_at_or_below(&entry.path, &root));
    }

    pub fn relocate_paths(&mut self, source: impl AsRef<Path>, target: impl AsRef<Path>) {
        let source = normalize_recent_path(source.as_ref());
        let target = normalize_recent_path(target.as_ref());
        self.preview_style_files.relocate(&source, &target);
        for entry in &mut self.recent_files {
            if let Some(relative) = relative_path_when_contained(&entry.path, &source) {
                entry.path = target.join(relative);
            }
        }
        for entry in &mut self.recent_workspaces {
            if let Some(relative) = relative_path_when_contained(&entry.path, &source) {
                entry.path = target.join(relative);
            }
        }
        self.external_file_overrides = std::mem::take(&mut self.external_file_overrides)
            .into_iter()
            .map(|(path, behavior)| {
                let path_value = PathBuf::from(&path);
                if let Some(relative) = relative_path_when_contained(&path_value, &source) {
                    (
                        target.join(relative).to_string_lossy().into_owned(),
                        behavior,
                    )
                } else {
                    (path, behavior)
                }
            })
            .collect();
        if self
            .last_workspace
            .as_ref()
            .is_some_and(|workspace| path_is_at_or_below(workspace, &source))
        {
            self.last_workspace = Some(target);
        }
    }
}

fn merge_map_changes<K, V>(
    mut latest: BTreeMap<K, V>,
    baseline: BTreeMap<K, V>,
    incoming: BTreeMap<K, V>,
) -> BTreeMap<K, V>
where
    K: Ord + Clone,
    V: Clone + PartialEq,
{
    let keys = baseline
        .keys()
        .chain(incoming.keys())
        .cloned()
        .collect::<std::collections::BTreeSet<_>>();
    for key in keys {
        if baseline.get(&key) == incoming.get(&key) {
            continue;
        }
        if let Some(value) = incoming.get(&key) {
            latest.insert(key, value.clone());
        } else {
            latest.remove(&key);
        }
    }
    latest
}

fn merge_recent_entries(
    latest: Vec<RecentFile>,
    baseline: Vec<RecentFile>,
    incoming: Vec<RecentFile>,
) -> Vec<RecentFile> {
    let keyed = |entries: &[RecentFile]| {
        entries
            .iter()
            .map(|entry| (path_key(&entry.path), entry.clone()))
            .collect::<BTreeMap<_, _>>()
    };
    let latest = keyed(&latest);
    let baseline = keyed(&baseline);
    let incoming = keyed(&incoming);
    merge_map_changes(latest, baseline, incoming)
        .into_values()
        .collect()
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsLoad {
    pub settings: ApplicationSettings,
    pub warning: Option<String>,
}

#[derive(Clone, Debug)]
pub struct SettingsStore {
    path: PathBuf,
    legacy_path: Option<PathBuf>,
}

impl SettingsStore {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self {
            path: path.into(),
            legacy_path: None,
        }
    }

    pub fn new_with_legacy(path: impl Into<PathBuf>, legacy_path: impl Into<PathBuf>) -> Self {
        Self {
            path: path.into(),
            legacy_path: Some(legacy_path.into()),
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn load(&self) -> SettingsLoad {
        let _lock = match SettingsTransactionLock::acquire(&self.path) {
            Ok(lock) => lock,
            Err(error) => {
                return SettingsLoad {
                    settings: ApplicationSettings::default(),
                    warning: Some(error.to_string()),
                };
            }
        };
        self.load_unlocked()
    }

    fn load_unlocked(&self) -> SettingsLoad {
        let source_path = if self.path.exists() {
            self.path.as_path()
        } else if let Some(legacy_path) = self
            .legacy_path
            .as_deref()
            .filter(|legacy_path| legacy_path.exists())
        {
            legacy_path
        } else {
            return SettingsLoad {
                settings: ApplicationSettings::default(),
                warning: None,
            };
        };

        match fs::read_to_string(source_path)
            .map_err(|error| CoreError::io("读取设置", source_path, error))
            .and_then(|json| {
                serde_json::from_str::<ApplicationSettings>(&json).map_err(|_| {
                    CoreError::InvalidSettings {
                        path: source_path.to_path_buf(),
                    }
                })
            }) {
            Ok(settings) => SettingsLoad {
                settings: settings.normalize(),
                warning: None,
            },
            Err(error) => SettingsLoad {
                settings: ApplicationSettings::default(),
                warning: Some(error.to_string()),
            },
        }
    }

    pub fn save(&self, settings: &ApplicationSettings) -> CoreResult<()> {
        let _lock = SettingsTransactionLock::acquire(&self.path)?;
        self.save_unlocked(settings)
    }

    pub fn save_merged(
        &self,
        baseline: &ApplicationSettings,
        incoming: &ApplicationSettings,
    ) -> CoreResult<ApplicationSettings> {
        let _lock = SettingsTransactionLock::acquire(&self.path)?;
        let latest = self.load_unlocked().settings;
        let merged = ApplicationSettings::merge_changes(
            baseline.clone().normalize(),
            incoming.clone().normalize(),
            latest,
        );
        self.save_unlocked(&merged)?;
        Ok(merged)
    }

    fn save_unlocked(&self, settings: &ApplicationSettings) -> CoreResult<()> {
        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent)
                .map_err(|error| CoreError::io("创建设置目录", parent, error))?;
        }
        let normalized = settings.clone().normalize();
        let json = serde_json::to_vec_pretty(&normalized)
            .map_err(|error| CoreError::Internal(format!("序列化设置失败：{error}")))?;
        write_atomic_json(&self.path, &json)
    }
}

#[cfg(windows)]
struct SettingsTransactionLock {
    _file: fs::File,
}

#[cfg(windows)]
impl SettingsTransactionLock {
    fn acquire(settings_path: &Path) -> CoreResult<Self> {
        let file_name = settings_path
            .file_name()
            .ok_or_else(|| CoreError::InvalidName("设置文件缺少名称。".to_owned()))?;
        let lock_path =
            settings_path.with_file_name(format!("{}.lock", file_name.to_string_lossy()));
        if let Some(parent) = lock_path.parent() {
            fs::create_dir_all(parent)
                .map_err(|error| CoreError::io("创建设置锁目录", parent, error))?;
        }
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            match OpenOptions::new()
                .create(true)
                .read(true)
                .write(true)
                .truncate(false)
                .share_mode(0)
                .open(&lock_path)
            {
                Ok(file) => return Ok(Self { _file: file }),
                Err(error) if Instant::now() < deadline => {
                    let _ = error;
                    thread::sleep(Duration::from_millis(25));
                }
                Err(error) => {
                    return Err(CoreError::Internal(format!(
                        "等待其他 WIMD 窗口保存设置超时：{error}"
                    )));
                }
            }
        }
    }
}

#[cfg(not(windows))]
struct SettingsTransactionLock {
    _guard: std::sync::MutexGuard<'static, ()>,
}

#[cfg(not(windows))]
impl SettingsTransactionLock {
    fn acquire(_settings_path: &Path) -> CoreResult<Self> {
        static LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
        let guard = LOCK
            .lock()
            .map_err(|_| CoreError::Internal("设置事务锁已损坏。".to_owned()))?;
        Ok(Self { _guard: guard })
    }
}

#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum FlexibleEnum {
    Name(String),
    Number(u32),
}

fn deserialize_flexible_enum<'de, D>(deserializer: D) -> Result<FlexibleEnum, D::Error>
where
    D: Deserializer<'de>,
{
    FlexibleEnum::deserialize(deserializer)
}

fn deserialize_theme<'de, D>(deserializer: D) -> Result<Theme, D::Error>
where
    D: Deserializer<'de>,
{
    let value = deserialize_flexible_enum(deserializer)?;
    Ok(match value {
        FlexibleEnum::Number(value) => match value {
            0 => Theme::System,
            1 => Theme::Light,
            2 => Theme::Dark,
            3 => Theme::Warm,
            4 => Theme::Rose,
            5 => Theme::Sage,
            6 => Theme::Mist,
            7 => Theme::Lilac,
            8 => Theme::Midnight,
            _ => Theme::System,
        },
        FlexibleEnum::Name(value) => match value.as_str() {
            "light" => Theme::Light,
            "dark" => Theme::Dark,
            "warm" => Theme::Warm,
            "rose" => Theme::Rose,
            "sage" => Theme::Sage,
            "mist" => Theme::Mist,
            "lilac" => Theme::Lilac,
            "midnight" => Theme::Midnight,
            _ => Theme::System,
        },
    })
}

fn deserialize_view_mode<'de, D>(deserializer: D) -> Result<ViewMode, D::Error>
where
    D: Deserializer<'de>,
{
    let value = deserialize_flexible_enum(deserializer)?;
    Ok(match value {
        FlexibleEnum::Number(1) => ViewMode::EditorAndPreview,
        FlexibleEnum::Number(2) => ViewMode::PreviewOnly,
        FlexibleEnum::Name(value) if value == "editorAndPreview" => ViewMode::EditorAndPreview,
        FlexibleEnum::Name(value) if value == "previewOnly" => ViewMode::PreviewOnly,
        _ => ViewMode::EditorOnly,
    })
}

fn deserialize_file_open_view_mode<'de, D>(deserializer: D) -> Result<FileOpenViewMode, D::Error>
where
    D: Deserializer<'de>,
{
    let value = deserialize_flexible_enum(deserializer)?;
    Ok(match value {
        FlexibleEnum::Number(1) => FileOpenViewMode::EditorAndPreview,
        FlexibleEnum::Number(2) => FileOpenViewMode::PreviewOnly,
        FlexibleEnum::Number(3) => FileOpenViewMode::RememberLast,
        FlexibleEnum::Name(value) if value == "editorAndPreview" => {
            FileOpenViewMode::EditorAndPreview
        }
        FlexibleEnum::Name(value) if value == "previewOnly" => FileOpenViewMode::PreviewOnly,
        FlexibleEnum::Name(value) if value == "rememberLast" => FileOpenViewMode::RememberLast,
        _ => FileOpenViewMode::EditorOnly,
    })
}

fn deserialize_ordered_list_style<'de, D>(deserializer: D) -> Result<OrderedListStyle, D::Error>
where
    D: Deserializer<'de>,
{
    let value = deserialize_flexible_enum(deserializer)?;
    Ok(match value {
        FlexibleEnum::Number(1) => OrderedListStyle::Bold,
        FlexibleEnum::Number(2) => OrderedListStyle::Circle,
        FlexibleEnum::Number(3) => OrderedListStyle::Square,
        FlexibleEnum::Name(value) if value == "bold" => OrderedListStyle::Bold,
        FlexibleEnum::Name(value) if value == "circle" => OrderedListStyle::Circle,
        FlexibleEnum::Name(value) if value == "square" => OrderedListStyle::Square,
        _ => OrderedListStyle::Plain,
    })
}

fn deserialize_image_storage_mode<'de, D>(deserializer: D) -> Result<ImageStorageMode, D::Error>
where
    D: Deserializer<'de>,
{
    let value = deserialize_flexible_enum(deserializer)?;
    Ok(match value {
        FlexibleEnum::Number(1) => ImageStorageMode::ImgBb,
        FlexibleEnum::Name(value) if value == "imgBb" => ImageStorageMode::ImgBb,
        _ => ImageStorageMode::Local,
    })
}

fn deserialize_external_file_behavior<'de, D>(
    deserializer: D,
) -> Result<ExternalFileChangeBehavior, D::Error>
where
    D: Deserializer<'de>,
{
    let value = deserialize_flexible_enum(deserializer)?;
    Ok(match value {
        FlexibleEnum::Number(1) => ExternalFileChangeBehavior::AutoReload,
        FlexibleEnum::Name(value) if value == "autoReload" => {
            ExternalFileChangeBehavior::AutoReload
        }
        _ => ExternalFileChangeBehavior::Prompt,
    })
}

fn deserialize_optional_bool<'de, D>(deserializer: D) -> Result<Option<bool>, D::Error>
where
    D: Deserializer<'de>,
{
    Option::<bool>::deserialize(deserializer)
}

fn deserialize_optional<'de, D, T>(deserializer: D) -> Result<Option<T>, D::Error>
where
    D: Deserializer<'de>,
    T: Deserialize<'de>,
{
    Option::<T>::deserialize(deserializer)
}

fn deserialize_external_file_override_map<'de, D>(
    deserializer: D,
) -> Result<BTreeMap<String, ExternalFileChangeBehavior>, D::Error>
where
    D: Deserializer<'de>,
{
    let raw = BTreeMap::<String, serde_json::Value>::deserialize(deserializer)?;
    Ok(raw
        .into_iter()
        .filter_map(|(path, value)| {
            external_file_behavior_from_value(value).map(|behavior| (path, behavior))
        })
        .collect())
}

fn deserialize_preview_style_file_map<'de, D>(
    deserializer: D,
) -> Result<BTreeMap<String, PreviewStyleOverrides>, D::Error>
where
    D: Deserializer<'de>,
{
    BTreeMap::<String, PreviewStyleOverrides>::deserialize(deserializer)
}

fn deserialize_shortcut_overrides<'de, D>(
    deserializer: D,
) -> Result<BTreeMap<String, String>, D::Error>
where
    D: Deserializer<'de>,
{
    let raw = BTreeMap::<String, serde_json::Value>::deserialize(deserializer)?;
    Ok(raw
        .into_iter()
        .filter_map(|(command, value)| {
            let gesture = match value {
                serde_json::Value::String(value) => value,
                serde_json::Value::Object(value) => {
                    serde_json::from_value::<LegacyShortcutGesture>(serde_json::Value::Object(
                        value,
                    ))
                    .ok()?
                    .into_gesture()?
                }
                _ => return None,
            };
            Some((map_legacy_shortcut_id(&command).to_owned(), gesture))
        })
        .collect())
}

fn map_legacy_shortcut_id(command: &str) -> &str {
    match command {
        "file.new" => "new",
        "file.open" => "open",
        "file.save" => "save",
        "file.save-as" => "save-as",
        "file.reload" => "reload",
        "view.cycle" => "cycle-view",
        "heading.1" => "heading-1",
        "heading.2" => "heading-2",
        "heading.3" => "heading-3",
        "heading.4" => "heading-4",
        "heading.5" => "heading-5",
        "heading.6" => "heading-6",
        "format.bold" => "bold",
        "format.italic" => "italic",
        "format.strike" => "strike",
        "format.inline-code" => "inline-code",
        "format.code-block" => "code-block",
        "format.link" => "link",
        "format.image" => "image",
        "format.ordered-list" => "ordered-list",
        "format.unordered-list" => "unordered-list",
        "format.quote" => "quote",
        "format.task-list" => "task-list",
        other => other,
    }
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct LegacyImageInsertion {
    #[serde(deserialize_with = "deserialize_optional_flexible_image_storage_mode")]
    storage_mode: Option<ImageStorageMode>,
    local_directory: Option<String>,
    protected_img_bb_api_key: Option<String>,
}

fn deserialize_optional_flexible_image_storage_mode<'de, D>(
    deserializer: D,
) -> Result<Option<ImageStorageMode>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<serde_json::Value>::deserialize(deserializer)?;
    Ok(value.map(|value| match value {
        serde_json::Value::Number(value) if value.as_u64() == Some(1) => ImageStorageMode::ImgBb,
        serde_json::Value::String(value) if value == "imgBb" => ImageStorageMode::ImgBb,
        _ => ImageStorageMode::Local,
    }))
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct LegacyAppearance {
    #[serde(deserialize_with = "deserialize_optional_flexible_theme")]
    theme: Option<Theme>,
    editor_font_family: Option<String>,
    editor_font_size: Option<f64>,
    preview_font_family: Option<String>,
    preview_font_size: Option<f64>,
}

fn deserialize_optional_flexible_theme<'de, D>(deserializer: D) -> Result<Option<Theme>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<serde_json::Value>::deserialize(deserializer)?;
    Ok(value.and_then(|value| match value {
        serde_json::Value::Number(value) => value.as_u64().map(|value| match value {
            1 => Theme::Light,
            2 => Theme::Dark,
            3 => Theme::Warm,
            4 => Theme::Rose,
            5 => Theme::Sage,
            6 => Theme::Mist,
            7 => Theme::Lilac,
            8 => Theme::Midnight,
            _ => Theme::System,
        }),
        serde_json::Value::String(value) => Some(match value.as_str() {
            "light" => Theme::Light,
            "dark" => Theme::Dark,
            "warm" => Theme::Warm,
            "rose" => Theme::Rose,
            "sage" => Theme::Sage,
            "mist" => Theme::Mist,
            "lilac" => Theme::Lilac,
            "midnight" => Theme::Midnight,
            _ => Theme::System,
        }),
        _ => None,
    }))
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct LegacyPreviewStyles {
    #[serde(deserialize_with = "deserialize_optional_flexible_ordered_list_style")]
    ordered_list: Option<OrderedListStyle>,
}

fn deserialize_optional_flexible_ordered_list_style<'de, D>(
    deserializer: D,
) -> Result<Option<OrderedListStyle>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<serde_json::Value>::deserialize(deserializer)?;
    Ok(value.and_then(|value| match value {
        serde_json::Value::Number(value) => value.as_u64().map(|value| match value {
            1 => OrderedListStyle::Bold,
            2 => OrderedListStyle::Circle,
            3 => OrderedListStyle::Square,
            _ => OrderedListStyle::Plain,
        }),
        serde_json::Value::String(value) => Some(match value.as_str() {
            "bold" => OrderedListStyle::Bold,
            "circle" => OrderedListStyle::Circle,
            "square" => OrderedListStyle::Square,
            _ => OrderedListStyle::Plain,
        }),
        _ => None,
    }))
}

#[derive(Clone, Debug, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
struct LegacyExternalFileChanges {
    #[serde(deserialize_with = "deserialize_optional_flexible_external_file_behavior")]
    default_behavior: Option<ExternalFileChangeBehavior>,
    #[serde(deserialize_with = "deserialize_external_file_override_map")]
    file_overrides: BTreeMap<String, ExternalFileChangeBehavior>,
}

fn deserialize_optional_flexible_external_file_behavior<'de, D>(
    deserializer: D,
) -> Result<Option<ExternalFileChangeBehavior>, D::Error>
where
    D: Deserializer<'de>,
{
    let value = Option::<serde_json::Value>::deserialize(deserializer)?;
    Ok(value.and_then(external_file_behavior_from_value))
}

fn external_file_behavior_from_value(
    value: serde_json::Value,
) -> Option<ExternalFileChangeBehavior> {
    match value {
        serde_json::Value::Number(value) => Some(if value.as_u64() == Some(1) {
            ExternalFileChangeBehavior::AutoReload
        } else {
            ExternalFileChangeBehavior::Prompt
        }),
        serde_json::Value::String(value) if value == "autoReload" => {
            Some(ExternalFileChangeBehavior::AutoReload)
        }
        serde_json::Value::String(_) => Some(ExternalFileChangeBehavior::Prompt),
        _ => None,
    }
}

#[derive(Debug, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct LegacyShortcutGesture {
    key: String,
    control: bool,
    shift: bool,
    alt: bool,
}

impl LegacyShortcutGesture {
    fn into_gesture(self) -> Option<String> {
        let key = match self.key.trim() {
            "" => return None,
            "D0" => "0",
            "D1" => "1",
            "D2" => "2",
            "D3" => "3",
            "D4" => "4",
            "D5" => "5",
            "D6" => "6",
            "D7" => "7",
            "D8" => "8",
            "D9" => "9",
            "OemTilde" | "Oem3" => "`",
            "OemPlus" => "+",
            "OemMinus" => "-",
            "Return" => "Enter",
            value => value,
        };
        let mut parts = Vec::new();
        if self.control {
            parts.push("Ctrl");
        }
        if self.shift {
            parts.push("Shift");
        }
        if self.alt {
            parts.push("Alt");
        }
        parts.push(key);
        Some(parts.join("+"))
    }
}

fn write_atomic_json(path: &Path, bytes: &[u8]) -> CoreResult<()> {
    let path = if path.exists() {
        normalize_path(path)?
    } else {
        let parent = path.parent().unwrap_or_else(|| Path::new("."));
        fs::create_dir_all(parent).map_err(|error| CoreError::io("创建设置目录", parent, error))?;
        let parent = fs::canonicalize(parent)
            .map_err(|error| CoreError::io("规范化设置目录", parent, error))?;
        parent.join(
            path.file_name()
                .ok_or_else(|| CoreError::InvalidName("设置文件缺少名称。".to_owned()))?,
        )
    };
    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    let temporary_path = parent.join(format!(".settings.{}.tmp", Uuid::new_v4()));
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary_path)
            .map_err(|error| CoreError::io("创建设置临时文件", &temporary_path, error))?;
        file.write_all(bytes)
            .map_err(|error| CoreError::io("写入设置", &temporary_path, error))?;
        file.flush()
            .map_err(|error| CoreError::io("刷新设置", &temporary_path, error))?;
        file.sync_all()
            .map_err(|error| CoreError::io("同步设置", &temporary_path, error))?;
        drop(file);
        replace_file(&temporary_path, &path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary_path);
    }
    result
}

fn normalize_font_size(value: f64, fallback: f64) -> f64 {
    if value.is_finite() {
        value.clamp(10.0, 32.0)
    } else {
        fallback
    }
}

fn normalize_recent_path(path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir().unwrap_or_default().join(path)
    }
}

fn normalize_absolute_preference_path(value: &str) -> Option<String> {
    let value = value.trim();
    if value.is_empty() {
        return None;
    }
    let path = Path::new(value);
    path.is_absolute()
        .then(|| path.to_string_lossy().into_owned())
}

fn path_key(path: &Path) -> String {
    path.to_string_lossy().to_lowercase()
}

fn relative_path_when_contained(candidate: &Path, root: &Path) -> Option<PathBuf> {
    let candidate_text = candidate.to_string_lossy().replace('/', "\\");
    let root_text = root.to_string_lossy().replace('/', "\\");
    let candidate_text = candidate_text.trim_end_matches('\\');
    let root_text = root_text.trim_end_matches('\\');
    let candidate_key = candidate_text.to_lowercase();
    let root_key = root_text.to_lowercase();
    if candidate_key == root_key {
        return Some(PathBuf::new());
    }
    let suffix = candidate_key.strip_prefix(&root_key)?;
    if !suffix.starts_with('\\') {
        return None;
    }
    let offset = root_text.len();
    Some(PathBuf::from(
        candidate_text[offset..].trim_start_matches('\\'),
    ))
}

fn path_is_at_or_below(candidate: &Path, root: &Path) -> bool {
    relative_path_when_contained(candidate, root).is_some()
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeDelta;

    #[test]
    fn recent_file_timestamp_updates_without_reordering_current_session() {
        let now = Utc::now();
        let mut settings = ApplicationSettings::default();
        settings.record_recent_file("first.md", now - TimeDelta::minutes(2));
        settings.record_recent_file("second.md", now - TimeDelta::minutes(1));
        settings.record_recent_file("first.md", now);

        assert_eq!(
            settings.recent_files[1].path.file_name().unwrap(),
            "first.md"
        );
        assert_eq!(
            settings.recent_files[0].path.file_name().unwrap(),
            "second.md"
        );
    }

    #[test]
    fn load_sorts_recent_files_and_falls_back_safely() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("settings.json");
        fs::write(
            &path,
            r#"{
              "recentFiles": [
                {"path":"older.md","lastOpenedUtc":"2026-01-01T00:00:00Z"},
                {"path":"newer.md","lastOpenedUtc":"2026-02-01T00:00:00Z"}
              ],
              "editorFontSize": 999,
              "splitRatio": 0.01
            }"#,
        )
        .unwrap();

        let loaded = SettingsStore::new(&path).load();

        assert!(loaded.settings.recent_files[0].path.ends_with("newer.md"));
        assert_eq!(loaded.settings.editor_font_size, 32.0);
        assert_eq!(loaded.settings.split_ratio, 0.18);
    }

    #[test]
    fn malformed_settings_do_not_block_startup() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("settings.json");
        fs::write(&path, "{not-json").unwrap();

        let loaded = SettingsStore::new(&path).load();

        assert!(loaded.warning.is_some());
        assert_eq!(loaded.settings.theme, Theme::System);
    }

    #[test]
    fn save_creates_parent_and_round_trips_settings() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("nested/settings.json");
        let store = SettingsStore::new(&path);
        let settings = ApplicationSettings {
            theme: Theme::Mist,
            preview_font_size: 20.0,
            ..ApplicationSettings::default()
        };

        store.save(&settings).unwrap();
        let loaded = store.load();

        assert_eq!(loaded.settings.theme, Theme::Mist);
        assert_eq!(loaded.settings.preview_font_size, 20.0);
    }

    #[test]
    fn recent_workspaces_and_external_overrides_are_normalized() {
        let now = Utc::now();
        let mut settings = ApplicationSettings::default();
        settings.record_recent_workspace("workspace", now);
        settings.external_file_overrides.insert(
            "C:\\docs\\a.md".to_owned(),
            ExternalFileChangeBehavior::AutoReload,
        );
        let normalized = settings.normalize();

        assert_eq!(normalized.recent_workspaces.len(), 1);
        assert_eq!(
            normalized.external_file_overrides["C:\\docs\\a.md"],
            ExternalFileChangeBehavior::AutoReload
        );
    }

    #[test]
    fn legacy_nested_settings_are_migrated_without_losing_shortcuts() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("settings.json");
        fs::write(
            &path,
            r#"{
              "isRecentPaneExpanded": false,
              "imageInsertion": {
                "storageMode": 1,
                "localDirectory": "assets",
                "protectedImgBbApiKey": "protected-key"
              },
              "appearance": {
                "theme": 6,
                "editorFontFamily": "LXGW WenKai Mono",
                "editorFontSize": 18,
                "previewFontFamily": "LXGW WenKai",
                "previewFontSize": 20
              },
              "previewStyles": { "orderedList": 2 },
              "previewStyleFiles": {
                "fileOverrides": {
                  "C:\\docs\\A.md": { "orderedList": 3 }
                }
              },
              "externalFileChanges": {
                "defaultBehavior": 1,
                "fileOverrides": { "C:\\docs\\a.md": 1 }
              },
              "fileOpenViewMode": 2,
              "lastViewMode": 1,
              "shortcutOverrides": {
                "file.save-as": {
                  "key": "S",
                  "control": true,
                  "shift": true,
                  "alt": false
                },
                "format.task-list": {
                  "key": "L",
                  "control": true,
                  "shift": true,
                  "alt": false
                }
              }
            }"#,
        )
        .unwrap();

        let loaded = SettingsStore::new(&path).load();

        assert!(loaded.warning.is_none());
        assert!(!loaded.settings.sidebar_visible);
        assert_eq!(loaded.settings.image_storage_mode, ImageStorageMode::ImgBb);
        assert_eq!(loaded.settings.local_image_directory, "assets");
        assert_eq!(
            loaded.settings.protected_imgbb_api_key.as_deref(),
            Some("protected-key")
        );
        assert_eq!(loaded.settings.theme, Theme::Mist);
        assert_eq!(loaded.settings.editor_font_size, 18.0);
        assert_eq!(loaded.settings.preview_font_size, 20.0);
        assert_eq!(loaded.settings.ordered_list_style, OrderedListStyle::Circle);
        assert_eq!(
            loaded
                .settings
                .preview_style_files
                .override_for(Some(Path::new("C:\\DOCS\\a.md")))
                .ordered_list,
            Some(OrderedListStyle::Square)
        );
        assert_eq!(
            loaded.settings.external_file_change_behavior,
            ExternalFileChangeBehavior::AutoReload
        );
        assert_eq!(
            loaded.settings.external_file_overrides["C:\\docs\\a.md"],
            ExternalFileChangeBehavior::AutoReload
        );
        assert_eq!(
            loaded.settings.file_open_view_mode,
            FileOpenViewMode::PreviewOnly
        );
        assert_eq!(loaded.settings.last_view_mode, ViewMode::EditorAndPreview);
        assert_eq!(
            loaded.settings.shortcut_overrides["save-as"],
            "Ctrl+Shift+S"
        );
        assert_eq!(
            loaded.settings.shortcut_overrides["task-list"],
            "Ctrl+Shift+L"
        );
    }

    #[test]
    fn legacy_settings_directory_is_read_once_and_new_path_is_used_for_save() {
        let directory = tempfile::tempdir().unwrap();
        let current = directory.path().join("WIMD/settings.json");
        let legacy = directory.path().join("WhoIsMarkdown/settings.json");
        fs::create_dir_all(legacy.parent().unwrap()).unwrap();
        fs::write(
            &legacy,
            r#"{
              "appearance": { "theme": 2, "previewFontSize": 19 },
              "fileOpenViewMode": 3
            }"#,
        )
        .unwrap();

        let store = SettingsStore::new_with_legacy(&current, &legacy);
        let loaded = store.load().settings;
        assert_eq!(loaded.theme, Theme::Dark);
        assert_eq!(loaded.preview_font_size, 19.0);
        assert_eq!(loaded.file_open_view_mode, FileOpenViewMode::RememberLast);

        store.save(&loaded).unwrap();
        assert!(current.exists());
        assert!(legacy.exists());
    }

    #[test]
    fn file_preview_styles_follow_path_case_remove_and_workspace_relocation() {
        let mut settings = ApplicationSettings::default();
        settings.preview_style_files.set_override(
            Path::new("C:\\docs\\a.md"),
            PreviewStyleOverrides {
                ordered_list: Some(OrderedListStyle::Circle),
            },
        );
        settings.preview_style_files.set_override(
            Path::new("C:\\docs\\nested\\b.md"),
            PreviewStyleOverrides {
                ordered_list: Some(OrderedListStyle::Square),
            },
        );

        assert_eq!(
            settings
                .preview_style_files
                .override_for(Some(Path::new("C:\\DOCS\\A.md")))
                .ordered_list,
            Some(OrderedListStyle::Circle)
        );

        settings.relocate_paths("c:\\DOCS", "D:\\archive");
        assert_eq!(
            settings
                .preview_style_files
                .override_for(Some(Path::new("D:\\archive\\nested\\b.md")))
                .ordered_list,
            Some(OrderedListStyle::Square)
        );

        settings.preview_style_files.set_override(
            Path::new("D:\\ARCHIVE\\A.md"),
            PreviewStyleOverrides::default(),
        );
        assert!(
            settings
                .preview_style_files
                .override_for(Some(Path::new("D:\\archive\\a.md")))
                .is_empty()
        );
    }

    #[test]
    fn merged_saves_preserve_concurrent_scalar_and_map_changes() {
        let directory = tempfile::tempdir().unwrap();
        let store = SettingsStore::new(directory.path().join("settings.json"));
        let baseline = ApplicationSettings::default();

        let mut appearance = baseline.clone();
        appearance.theme = Theme::Warm;
        appearance.preview_style_files.set_override(
            Path::new("C:\\docs\\a.md"),
            PreviewStyleOverrides {
                ordered_list: Some(OrderedListStyle::Circle),
            },
        );
        let first = store.save_merged(&baseline, &appearance).unwrap();
        assert_eq!(first.theme, Theme::Warm);

        let mut behavior = baseline.clone();
        behavior.editor_font_size = 19.0;
        behavior.preview_style_files.set_override(
            Path::new("C:\\docs\\b.md"),
            PreviewStyleOverrides {
                ordered_list: Some(OrderedListStyle::Square),
            },
        );
        let second = store.save_merged(&baseline, &behavior).unwrap();

        assert_eq!(second.theme, Theme::Warm);
        assert_eq!(second.editor_font_size, 19.0);
        assert_eq!(
            second
                .preview_style_files
                .override_for(Some(Path::new("C:\\docs\\a.md")))
                .ordered_list,
            Some(OrderedListStyle::Circle)
        );
        assert_eq!(
            second
                .preview_style_files
                .override_for(Some(Path::new("C:\\docs\\b.md")))
                .ordered_list,
            Some(OrderedListStyle::Square)
        );
    }

    #[test]
    fn concurrent_merged_saves_are_serialized_on_disk() {
        let directory = tempfile::tempdir().unwrap();
        let store = SettingsStore::new(directory.path().join("settings.json"));
        let baseline = ApplicationSettings::default();
        let barrier = std::sync::Arc::new(std::sync::Barrier::new(2));

        let first_store = store.clone();
        let first_barrier = std::sync::Arc::clone(&barrier);
        let first_baseline = baseline.clone();
        let first = std::thread::spawn(move || {
            let mut incoming = first_baseline.clone();
            incoming.theme = Theme::Sage;
            first_barrier.wait();
            first_store.save_merged(&first_baseline, &incoming)
        });
        let second_store = store.clone();
        let second_barrier = barrier;
        let second_baseline = baseline.clone();
        let second = std::thread::spawn(move || {
            let mut incoming = second_baseline.clone();
            incoming.editor_font_size = 21.0;
            second_barrier.wait();
            second_store.save_merged(&second_baseline, &incoming)
        });

        first.join().unwrap().unwrap();
        second.join().unwrap().unwrap();
        let loaded = store.load().settings;
        assert_eq!(loaded.theme, Theme::Sage);
        assert_eq!(loaded.editor_font_size, 21.0);
    }
}

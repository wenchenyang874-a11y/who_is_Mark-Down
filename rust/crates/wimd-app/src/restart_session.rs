use std::{
    fs,
    path::{Path, PathBuf},
    time::SystemTime,
};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use wimd_core::document::write_bytes_atomic;

use crate::recovery::{self, RecoverySnapshot};

const REQUEST_FILE: &str = "update-restart.request";
const RUST_SESSION_DIRECTORY: &str = "RustUpdateRestart";
const LEGACY_SESSION_DIRECTORY: &str = "UpdateRestart";
const MAXIMUM_REQUEST_BYTES: u64 = 256;
const MAXIMUM_AGE_SECONDS: u64 = 2 * 60 * 60;
const WINDOW_SNAPSHOT_SCHEMA_VERSION: u32 = 1;
const MAXIMUM_WINDOW_SNAPSHOT_BYTES: u64 = 128 * 1024 * 1024;
const MAXIMUM_WINDOW_DOCUMENTS: usize = 32;
const MAXIMUM_WORKSPACE_EXPANDED_PATHS: usize = 4096;
const MAXIMUM_LEGACY_SNAPSHOT_BYTES: u64 = 128 * 1024 * 1024;
const MAXIMUM_LEGACY_WINDOWS: usize = 32;

pub const RESTORE_WINDOW_ARGUMENT: &str = "--restore-update-session";

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateRestartDocumentSnapshot {
    pub id: String,
    pub original_path: Option<PathBuf>,
    pub title: String,
    pub text: String,
    pub saved_text: String,
    pub had_utf8_bom: bool,
    pub line_ending: String,
    pub read_only: bool,
    pub view_mode: String,
    pub selection_start: usize,
    pub selection_end: usize,
    pub scroll_top: f64,
    pub preview_scroll_ratio: f64,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateRestartWorkspaceSelection {
    pub path: PathBuf,
    pub is_directory: bool,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateRestartWindowSnapshot {
    pub schema_version: u32,
    pub saved_at_utc: DateTime<Utc>,
    pub active_document_index: usize,
    pub workspace_root: Option<PathBuf>,
    #[serde(default)]
    pub workspace_expanded: Vec<PathBuf>,
    #[serde(default)]
    pub workspace_selection: Option<UpdateRestartWorkspaceSelection>,
    pub window_x: Option<i32>,
    pub window_y: Option<i32>,
    pub window_width: u32,
    pub window_height: u32,
    pub window_maximized: bool,
    pub documents: Vec<UpdateRestartDocumentSnapshot>,
}

#[derive(Clone, Debug)]
pub struct ClaimedUpdateRestartWindow {
    pub snapshot: UpdateRestartWindowSnapshot,
    pub claim_path: PathBuf,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RestartPhase {
    Capture,
    Restore,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RestartRequest {
    pub phase: RestartPhase,
    pub token: String,
}

pub fn request_path(application_data: &Path) -> PathBuf {
    application_data.join(REQUEST_FILE)
}

pub fn new_window_id() -> String {
    let timestamp = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default();
    format!("{}-{timestamp}", std::process::id())
}

pub fn save_window_snapshot(
    application_data: &Path,
    window_id: &str,
    mut snapshot: UpdateRestartWindowSnapshot,
) -> Result<(), String> {
    if !is_safe_id(window_id) {
        return Err("更新恢复窗口编号无效。".to_owned());
    }
    let request = read_request(application_data)
        .filter(|request| request.phase == RestartPhase::Capture)
        .ok_or_else(|| "当前关闭不是由更新安装器发起。".to_owned())?;
    snapshot.schema_version = WINDOW_SNAPSHOT_SCHEMA_VERSION;
    snapshot.saved_at_utc = Utc::now();
    normalize_window_snapshot(&mut snapshot)?;
    let directory = rust_session_directory(application_data, &request.token);
    fs::create_dir_all(&directory).map_err(|error| format!("创建更新恢复目录失败：{error}"))?;
    let bytes = serde_json::to_vec_pretty(&snapshot)
        .map_err(|error| format!("序列化更新恢复窗口失败：{error}"))?;
    if bytes.len() as u64 > MAXIMUM_WINDOW_SNAPSHOT_BYTES {
        return Err("更新恢复窗口超过 128 MiB 上限。".to_owned());
    }
    write_bytes_atomic(directory.join(format!("{window_id}.json")), &bytes)
        .map_err(|error| error.to_string())
}

pub fn pending_restore_window_count(application_data: &Path) -> usize {
    let Some(request) = read_request(application_data) else {
        return 0;
    };
    if request.phase != RestartPhase::Restore {
        return 0;
    }
    selected_restore_session_token(application_data, &request.token)
        .map(|token| session_snapshot_paths(application_data, &token).len())
        .unwrap_or(0)
}

pub fn claim_restore_window(
    application_data: &Path,
) -> Result<Option<ClaimedUpdateRestartWindow>, String> {
    let Some(request) = read_request(application_data) else {
        return Ok(None);
    };
    if request.phase != RestartPhase::Restore {
        return Ok(None);
    }
    let Some(session_token) = selected_restore_session_token(application_data, &request.token)
    else {
        let directory = rust_session_directory(application_data, &request.token);
        cleanup_restore_request_if_empty(application_data, &directory);
        return Ok(None);
    };
    let directory = rust_session_directory(application_data, &session_token);
    let candidates = session_snapshot_paths(application_data, &session_token);
    for candidate in candidates {
        let claim_name = format!(".{}-{}.claimed", std::process::id(), new_window_id());
        let claim_path = directory.join(claim_name);
        if fs::rename(&candidate, &claim_path).is_err() {
            continue;
        }
        let result = fs::read(&claim_path)
            .ok()
            .and_then(|bytes| serde_json::from_slice::<UpdateRestartWindowSnapshot>(&bytes).ok())
            .and_then(|mut snapshot| {
                if snapshot.schema_version != WINDOW_SNAPSHOT_SCHEMA_VERSION {
                    return None;
                }
                normalize_window_snapshot(&mut snapshot).ok()?;
                Some(snapshot)
            });
        if let Some(snapshot) = result {
            return Ok(Some(ClaimedUpdateRestartWindow {
                snapshot,
                claim_path,
            }));
        }
        let _ = fs::remove_file(&claim_path);
    }
    let directory = rust_session_directory(application_data, &request.token);
    cleanup_restore_request_if_empty(application_data, &directory);
    Ok(None)
}

pub fn finish_restore_window(application_data: &Path, claim_path: &Path) -> Result<(), String> {
    let root = application_data.join(RUST_SESSION_DIRECTORY);
    let normalized_root = root.canonicalize().unwrap_or(root);
    let normalized_claim = claim_path
        .canonicalize()
        .unwrap_or_else(|_| claim_path.to_path_buf());
    if !normalized_claim.starts_with(&normalized_root) {
        return Err("更新恢复认领路径超出应用数据目录。".to_owned());
    }
    if normalized_claim.exists() {
        fs::remove_file(&normalized_claim)
            .map_err(|error| format!("删除更新恢复认领文件失败：{error}"))?;
    }
    if let Some(directory) = normalized_claim.parent() {
        cleanup_restore_request_if_empty(application_data, directory);
    }
    Ok(())
}

pub fn read_request(application_data: &Path) -> Option<RestartRequest> {
    let path = request_path(application_data);
    let metadata = fs::metadata(&path).ok()?;
    if !metadata.is_file() || metadata.len() == 0 || metadata.len() > MAXIMUM_REQUEST_BYTES {
        return None;
    }
    let age = metadata
        .modified()
        .ok()
        .and_then(|modified| SystemTime::now().duration_since(modified).ok())
        .map(|duration| duration.as_secs())
        .unwrap_or(u64::MAX);
    if age > MAXIMUM_AGE_SECONDS {
        let _ = fs::remove_file(path);
        return None;
    }
    let value = fs::read_to_string(path).ok()?;
    let (phase, token) = value.trim().split_once(':')?;
    let phase = match phase {
        "capture" => RestartPhase::Capture,
        "restore" => RestartPhase::Restore,
        _ => return None,
    };
    if token.len() < 8
        || token.len() > 80
        || !token
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        return None;
    }
    Some(RestartRequest {
        phase,
        token: token.to_owned(),
    })
}

pub fn is_capture_requested(application_data: &Path) -> bool {
    read_request(application_data).is_some_and(|request| request.phase == RestartPhase::Capture)
}

pub fn consume_restore_request(application_data: &Path) -> bool {
    if read_request(application_data).is_some_and(|request| request.phase == RestartPhase::Restore)
    {
        let _ = fs::remove_file(request_path(application_data));
        true
    } else {
        false
    }
}

pub fn promote_capture_for_startup(application_data: &Path) -> bool {
    let Some(request) = read_request(application_data) else {
        return false;
    };
    if request.phase != RestartPhase::Capture {
        return false;
    }
    let path = request_path(application_data);
    fs::write(path, format!("restore:{}", request.token)).is_ok()
}

fn rust_session_directory(application_data: &Path, token: &str) -> PathBuf {
    application_data.join(RUST_SESSION_DIRECTORY).join(token)
}

fn selected_restore_session_token(application_data: &Path, request_token: &str) -> Option<String> {
    if !session_snapshot_paths(application_data, request_token).is_empty() {
        return Some(request_token.to_owned());
    }
    let root = application_data.join(RUST_SESSION_DIRECTORY);
    let entries = fs::read_dir(root).ok()?;
    let mut candidates = entries
        .flatten()
        .filter_map(|entry| {
            let path = entry.path();
            let token = path.file_name()?.to_str()?.to_owned();
            if !path.is_dir() || !is_safe_id(&token) {
                return None;
            }
            let modified = fs::metadata(&path)
                .and_then(|metadata| metadata.modified())
                .ok()?;
            let age = SystemTime::now()
                .duration_since(modified)
                .ok()
                .map(|duration| duration.as_secs())
                .unwrap_or(u64::MAX);
            (age <= MAXIMUM_AGE_SECONDS).then_some((token, modified))
        })
        .collect::<Vec<_>>();
    candidates.sort_by_key(|(_, modified)| std::cmp::Reverse(*modified));
    candidates
        .into_iter()
        .map(|(token, _)| token)
        .filter(|token| !session_snapshot_paths(application_data, token).is_empty())
        .next()
}

fn session_snapshot_paths(application_data: &Path, token: &str) -> Vec<PathBuf> {
    let directory = rust_session_directory(application_data, token);
    let Ok(entries) = fs::read_dir(directory) else {
        return Vec::new();
    };
    let mut paths = entries
        .flatten()
        .filter_map(|entry| {
            let path = entry.path();
            let extension = path.extension().and_then(|value| value.to_str());
            let available = match extension {
                Some("json") => true,
                Some("claimed") => claimed_owner_process(&path)
                    .is_none_or(|process_id| !recovery::is_live_wimd_process(process_id)),
                _ => false,
            };
            available.then_some(path)
        })
        .collect::<Vec<_>>();
    paths.sort();
    paths
}

fn cleanup_restore_request_if_empty(application_data: &Path, directory: &Path) {
    let empty = fs::read_dir(directory)
        .map(|mut entries| entries.next().is_none())
        .unwrap_or(true);
    if !empty {
        return;
    }
    let _ = fs::remove_dir(directory);
    if let Some(parent) = directory.parent() {
        let _ = fs::remove_dir(parent);
    }
    let _ = fs::remove_file(request_path(application_data));
}

fn claimed_owner_process(path: &Path) -> Option<u32> {
    path.file_name()?
        .to_str()?
        .strip_prefix('.')?
        .split_once('-')?
        .0
        .parse()
        .ok()
}

fn normalize_window_snapshot(snapshot: &mut UpdateRestartWindowSnapshot) -> Result<(), String> {
    if snapshot.documents.is_empty() || snapshot.documents.len() > MAXIMUM_WINDOW_DOCUMENTS {
        return Err("更新恢复窗口中的文档数量无效。".to_owned());
    }
    let mut total_text_bytes = 0u64;
    for document in &mut snapshot.documents {
        if !is_safe_id(&document.id) {
            return Err("更新恢复文档编号无效。".to_owned());
        }
        document.title = normalize_title(&document.title);
        document.text = document.text.replace('\0', "");
        document.saved_text = document.saved_text.replace('\0', "");
        total_text_bytes = total_text_bytes
            .saturating_add(document.text.len() as u64)
            .saturating_add(document.saved_text.len() as u64);
        if total_text_bytes > MAXIMUM_WINDOW_SNAPSHOT_BYTES {
            return Err("更新恢复窗口正文超过 128 MiB 上限。".to_owned());
        }
        normalize_optional_absolute_path(&mut document.original_path);
        document.selection_start = document.selection_start.min(document.text.len());
        document.selection_end = document.selection_end.min(document.text.len());
        if document.selection_end < document.selection_start {
            std::mem::swap(&mut document.selection_start, &mut document.selection_end);
        }
        document.scroll_top = finite_nonnegative(document.scroll_top);
        document.preview_scroll_ratio = document.preview_scroll_ratio.clamp(0.0, 1.0);
        if !document.preview_scroll_ratio.is_finite() {
            document.preview_scroll_ratio = 0.0;
        }
        document.line_ending = normalize_token(&document.line_ending, "none");
        document.view_mode = match document.view_mode.as_str() {
            "editorOnly" => "editorOnly",
            "previewOnly" => "previewOnly",
            _ => "editorAndPreview",
        }
        .to_owned();
    }
    snapshot.active_document_index = snapshot
        .active_document_index
        .min(snapshot.documents.len().saturating_sub(1));
    normalize_optional_absolute_path(&mut snapshot.workspace_root);
    snapshot
        .workspace_expanded
        .retain(|path| path.is_absolute());
    snapshot
        .workspace_expanded
        .truncate(MAXIMUM_WORKSPACE_EXPANDED_PATHS);
    if let Some(selection) = &snapshot.workspace_selection
        && !selection.path.is_absolute()
    {
        snapshot.workspace_selection = None;
    }
    snapshot.window_x = snapshot
        .window_x
        .map(|value| value.clamp(-100_000, 100_000));
    snapshot.window_y = snapshot
        .window_y
        .map(|value| value.clamp(-100_000, 100_000));
    snapshot.window_width = snapshot.window_width.clamp(900, 16_384);
    snapshot.window_height = snapshot.window_height.clamp(600, 16_384);
    Ok(())
}

fn normalize_optional_absolute_path(path: &mut Option<PathBuf>) {
    if path.as_ref().is_some_and(|value| !value.is_absolute()) {
        *path = None;
    }
}

fn normalize_title(value: &str) -> String {
    let title = value
        .chars()
        .filter(|character| !character.is_control())
        .take(128)
        .collect::<String>();
    if title.trim().is_empty() {
        "未命名-1".to_owned()
    } else {
        title
    }
}

fn normalize_token(value: &str, fallback: &str) -> String {
    let value = value.trim();
    if !value.is_empty()
        && value.len() <= 32
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        value.to_owned()
    } else {
        fallback.to_owned()
    }
}

fn finite_nonnegative(value: f64) -> f64 {
    if value.is_finite() {
        value.clamp(0.0, 10_000_000.0)
    } else {
        0.0
    }
}

fn is_safe_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 80
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
}

/// Imports installer-controlled .NET WIMD windows once when the Rust edition
/// replaces the old process. Only local window/document deeplinks and unsaved
/// text are read; the legacy session directory is removed after a clean pass.
pub fn migrate_legacy_update_restart(application_data: &Path, recovery_directory: &Path) -> usize {
    let Some(request) = read_request(application_data) else {
        return 0;
    };
    if request.phase != RestartPhase::Restore {
        return 0;
    }
    let session_directory = application_data
        .join(LEGACY_SESSION_DIRECTORY)
        .join(&request.token);
    if !session_directory.is_dir() {
        return 0;
    }
    let Ok(entries) = fs::read_dir(&session_directory) else {
        return 0;
    };
    let mut migrated = 0usize;
    let mut failed = false;
    for entry in entries.flatten().take(MAXIMUM_LEGACY_WINDOWS) {
        let path = entry.path();
        if path.extension().and_then(|value| value.to_str()) != Some("json") {
            continue;
        }
        let Ok(metadata) = fs::metadata(&path) else {
            failed = true;
            continue;
        };
        if !metadata.is_file()
            || metadata.len() == 0
            || metadata.len() > MAXIMUM_LEGACY_SNAPSHOT_BYTES
        {
            failed = true;
            continue;
        }
        let Ok(bytes) = fs::read(&path) else {
            failed = true;
            continue;
        };
        let Ok(legacy) = serde_json::from_slice::<LegacyRestartWindow>(&bytes) else {
            failed = true;
            continue;
        };
        let stem = path
            .file_stem()
            .and_then(|value| value.to_str())
            .filter(|value| {
                value
                    .bytes()
                    .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
            })
            .unwrap_or("window");
        let snapshot = legacy.into_snapshot(&request.token, stem);
        match recovery::save(recovery_directory, &snapshot) {
            Ok(()) => migrated += 1,
            Err(_) => failed = true,
        }
    }
    if !failed {
        let _ = fs::remove_dir_all(&session_directory);
        let _ = fs::remove_file(request_path(application_data));
    }
    migrated
}

#[derive(Debug, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct LegacyRestartWindow {
    document_path: Option<PathBuf>,
    document_text: Option<String>,
    saved_document_text: Option<String>,
    untitled_display_name: String,
    has_utf8_bom: bool,
    view_mode: String,
    caret_offset: usize,
    editor_vertical_offset: f64,
    saved_at_utc: Option<DateTime<Utc>>,
}

impl LegacyRestartWindow {
    fn into_snapshot(self, token: &str, stem: &str) -> RecoverySnapshot {
        let original_path = self.document_path.filter(|path| path.is_absolute());
        let text = self.document_text.unwrap_or_default();
        let saved_text = self.saved_document_text.unwrap_or_else(|| text.clone());
        let title = original_path
            .as_deref()
            .and_then(Path::file_name)
            .map(|name| name.to_string_lossy().into_owned())
            .filter(|name| !name.is_empty())
            .unwrap_or_else(|| {
                let title = self.untitled_display_name.trim();
                if title.is_empty() {
                    "未命名-1".to_owned()
                } else {
                    title.to_owned()
                }
            });
        RecoverySnapshot {
            id: format!(
                "legacy-{}-{}",
                token.chars().take(24).collect::<String>(),
                stem.chars().take(32).collect::<String>()
            ),
            owner_process_id: 0,
            original_path,
            title,
            text,
            saved_text,
            had_utf8_bom: self.has_utf8_bom,
            view_mode: normalize_legacy_view_mode(&self.view_mode),
            selection_start: self.caret_offset,
            selection_end: self.caret_offset,
            scroll_top: if self.editor_vertical_offset.is_finite() {
                self.editor_vertical_offset.clamp(0.0, 10_000_000.0)
            } else {
                0.0
            },
            preview_scroll_ratio: 0.0,
            saved_at_utc: self.saved_at_utc.unwrap_or_else(Utc::now),
        }
    }
}

fn normalize_legacy_view_mode(value: &str) -> String {
    match value {
        "EditorOnly" | "editorOnly" => "editorOnly",
        "PreviewOnly" | "previewOnly" => "previewOnly",
        _ => "editorAndPreview",
    }
    .to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capture_and_restore_markers_are_bounded_and_consumable() {
        let directory = tempfile::tempdir().unwrap();
        fs::write(request_path(directory.path()), "capture:abcdefgh-1").unwrap();
        assert!(is_capture_requested(directory.path()));

        fs::write(request_path(directory.path()), "restore:abcdefgh-2").unwrap();
        assert!(consume_restore_request(directory.path()));
        assert!(read_request(directory.path()).is_none());
    }

    #[test]
    fn malformed_request_is_ignored() {
        let directory = tempfile::tempdir().unwrap();
        fs::write(request_path(directory.path()), "capture:../escape").unwrap();
        assert!(!is_capture_requested(directory.path()));
    }

    #[test]
    fn startup_promotes_unfinished_capture_to_restore() {
        let directory = tempfile::tempdir().unwrap();
        fs::write(request_path(directory.path()), "capture:abcdefgh-3").unwrap();
        assert!(promote_capture_for_startup(directory.path()));
        assert_eq!(
            read_request(directory.path()).unwrap().phase,
            RestartPhase::Restore
        );
    }

    #[test]
    fn rust_update_restart_snapshots_round_trip_one_window_per_claim() {
        let directory = tempfile::tempdir().unwrap();
        let token = "rustwindow1234";
        fs::write(request_path(directory.path()), format!("capture:{token}")).unwrap();
        save_window_snapshot(
            directory.path(),
            "window-1",
            test_window_snapshot("doc-1", "一"),
        )
        .unwrap();
        save_window_snapshot(
            directory.path(),
            "window-2",
            test_window_snapshot("doc-2", "二"),
        )
        .unwrap();
        assert_eq!(pending_restore_window_count(directory.path()), 0);

        fs::write(request_path(directory.path()), format!("restore:{token}")).unwrap();
        assert_eq!(pending_restore_window_count(directory.path()), 2);
        let first = claim_restore_window(directory.path()).unwrap().unwrap();
        let second = claim_restore_window(directory.path()).unwrap().unwrap();
        assert_eq!(first.snapshot.documents[0].text, "一");
        assert_eq!(second.snapshot.documents[0].text, "二");
        finish_restore_window(directory.path(), &first.claim_path).unwrap();
        finish_restore_window(directory.path(), &second.claim_path).unwrap();
        assert!(claim_restore_window(directory.path()).unwrap().is_none());
        assert!(!request_path(directory.path()).exists());
        assert!(
            !directory
                .path()
                .join(RUST_SESSION_DIRECTORY)
                .join(token)
                .exists()
        );
    }

    #[test]
    fn missing_legacy_session_does_not_consume_rust_restore_request() {
        let directory = tempfile::tempdir().unwrap();
        fs::write(request_path(directory.path()), "restore:abcdefgh-4").unwrap();
        assert_eq!(
            migrate_legacy_update_restart(directory.path(), &directory.path().join("Recovery")),
            0
        );
        assert!(read_request(directory.path()).is_some());
    }

    #[test]
    fn restore_request_can_fall_back_to_the_only_recent_capture_token() {
        let directory = tempfile::tempdir().unwrap();
        let capture_token = "capture-token-1234";
        let restore_token = "restore-token-5678";
        fs::write(
            request_path(directory.path()),
            format!("capture:{capture_token}"),
        )
        .unwrap();
        save_window_snapshot(
            directory.path(),
            "window-fallback",
            test_window_snapshot("doc-fallback", "fallback"),
        )
        .unwrap();
        fs::write(
            request_path(directory.path()),
            format!("restore:{restore_token}"),
        )
        .unwrap();

        assert_eq!(pending_restore_window_count(directory.path()), 1);
        let claimed = claim_restore_window(directory.path()).unwrap().unwrap();
        assert_eq!(claimed.snapshot.documents[0].text, "fallback");
        finish_restore_window(directory.path(), &claimed.claim_path).unwrap();
        assert!(!request_path(directory.path()).exists());
    }

    #[test]
    fn restore_uses_only_the_newest_mismatched_session_batch() {
        let directory = tempfile::tempdir().unwrap();
        for (token, document_id, text) in [
            ("capture-token-a123", "doc-a", "first"),
            ("capture-token-b456", "doc-b", "second"),
        ] {
            fs::write(request_path(directory.path()), format!("capture:{token}")).unwrap();
            save_window_snapshot(
                directory.path(),
                document_id,
                test_window_snapshot(document_id, text),
            )
            .unwrap();
        }
        fs::write(
            request_path(directory.path()),
            "restore:mismatched-token-7890",
        )
        .unwrap();

        assert_eq!(pending_restore_window_count(directory.path()), 1);
        let claimed = claim_restore_window(directory.path()).unwrap().unwrap();
        finish_restore_window(directory.path(), &claimed.claim_path).unwrap();
        assert!(!request_path(directory.path()).exists());
        assert!(claim_restore_window(directory.path()).unwrap().is_none());
        let remaining = fs::read_dir(directory.path().join(RUST_SESSION_DIRECTORY))
            .unwrap()
            .flatten()
            .flat_map(|entry| fs::read_dir(entry.path()).unwrap().flatten())
            .filter(|entry| {
                entry.path().extension().and_then(|value| value.to_str()) == Some("json")
            })
            .count();
        assert_eq!(remaining, 1);
    }

    #[test]
    fn installer_promotes_the_existing_capture_token() {
        let hooks = include_str!("../windows/installer-hooks.nsh");
        assert!(hooks.contains("!macro WimdPromoteRestartRequest"));
        assert!(hooks.contains("StrCpy $1 \"restore:$1\""));
        assert!(hooks.contains("!insertmacro WimdPromoteRestartRequest"));
    }

    #[test]
    fn legacy_installer_restart_is_imported_and_cleaned_up() {
        let directory = tempfile::tempdir().unwrap();
        let recovery_directory = directory.path().join("Recovery");
        let token = "abcdefgh1234";
        let session = directory.path().join(LEGACY_SESSION_DIRECTORY).join(token);
        fs::create_dir_all(&session).unwrap();
        fs::write(request_path(directory.path()), format!("restore:{token}")).unwrap();
        fs::write(
            session.join("0123456789abcdef0123456789abcdef.json"),
            r#"{
              "documentPath": "C:\\docs\\draft.md",
              "documentText": "未保存正文",
              "savedDocumentText": "已保存正文",
              "hasUtf8Bom": true,
              "viewMode": "PreviewOnly",
              "caretOffset": 7,
              "editorVerticalOffset": 120.5,
              "savedAtUtc": "2026-11-16T00:00:00Z"
            }"#,
        )
        .unwrap();

        assert_eq!(
            migrate_legacy_update_restart(directory.path(), &recovery_directory),
            1
        );
        let snapshots = recovery::list(&recovery_directory).unwrap();
        assert_eq!(snapshots.len(), 1);
        assert_eq!(snapshots[0].text, "未保存正文");
        assert_eq!(snapshots[0].view_mode, "previewOnly");
        assert!(!session.exists());
        assert!(!request_path(directory.path()).exists());
    }

    fn test_window_snapshot(document_id: &str, text: &str) -> UpdateRestartWindowSnapshot {
        UpdateRestartWindowSnapshot {
            schema_version: WINDOW_SNAPSHOT_SCHEMA_VERSION,
            saved_at_utc: Utc::now(),
            active_document_index: 0,
            workspace_root: None,
            workspace_expanded: Vec::new(),
            workspace_selection: None,
            window_x: Some(100),
            window_y: Some(120),
            window_width: 1320,
            window_height: 820,
            window_maximized: false,
            documents: vec![UpdateRestartDocumentSnapshot {
                id: document_id.to_owned(),
                original_path: None,
                title: "未命名-1".to_owned(),
                text: text.to_owned(),
                saved_text: String::new(),
                had_utf8_bom: false,
                line_ending: "none".to_owned(),
                read_only: false,
                view_mode: "editorAndPreview".to_owned(),
                selection_start: 0,
                selection_end: 0,
                scroll_top: 0.0,
                preview_scroll_ratio: 0.0,
            }],
        }
    }
}

use std::{
    fs,
    path::{Path, PathBuf},
    time::SystemTime,
};

use chrono::{DateTime, Utc};
use serde::Deserialize;

use crate::recovery::{self, RecoverySnapshot};

const REQUEST_FILE: &str = "update-restart.request";
const LEGACY_SESSION_DIRECTORY: &str = "UpdateRestart";
const MAXIMUM_REQUEST_BYTES: u64 = 256;
const MAXIMUM_AGE_SECONDS: u64 = 2 * 60 * 60;
const MAXIMUM_LEGACY_SNAPSHOT_BYTES: u64 = 128 * 1024 * 1024;
const MAXIMUM_LEGACY_WINDOWS: usize = 32;

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
        let _ = fs::remove_file(request_path(application_data));
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
}

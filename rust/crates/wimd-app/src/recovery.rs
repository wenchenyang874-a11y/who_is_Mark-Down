use std::{
    fs,
    path::{Path, PathBuf},
};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use wimd_core::document::{normalize_path, write_bytes_atomic};

const MAXIMUM_SNAPSHOT_BYTES: u64 = 128 * 1024 * 1024;
const MAXIMUM_TOTAL_SNAPSHOT_BYTES: u64 = 256 * 1024 * 1024;
const MAXIMUM_SNAPSHOTS: usize = 32;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RecoverySnapshot {
    pub id: String,
    #[serde(default)]
    pub owner_process_id: u32,
    pub original_path: Option<PathBuf>,
    pub title: String,
    pub text: String,
    pub saved_text: String,
    pub had_utf8_bom: bool,
    pub view_mode: String,
    #[serde(default)]
    pub selection_start: usize,
    #[serde(default)]
    pub selection_end: usize,
    #[serde(default)]
    pub scroll_top: f64,
    #[serde(default)]
    pub preview_scroll_ratio: f64,
    pub saved_at_utc: DateTime<Utc>,
}

pub fn save(directory: &Path, snapshot: &RecoverySnapshot) -> Result<(), String> {
    validate_snapshot(snapshot)?;
    fs::create_dir_all(directory).map_err(|error| format!("创建恢复目录失败：{error}"))?;
    let path = snapshot_path(directory, &snapshot.id)?;
    let json = serde_json::to_vec_pretty(snapshot)
        .map_err(|error| format!("序列化恢复快照失败：{error}"))?;
    if json.len() as u64 > MAXIMUM_SNAPSHOT_BYTES {
        return Err("恢复快照超过 128 MiB 上限。".to_owned());
    }
    write_bytes_atomic(path, &json).map_err(|error| error.to_string())
}

pub fn list(directory: &Path) -> Result<Vec<RecoverySnapshot>, String> {
    if !directory.is_dir() {
        return Ok(Vec::new());
    }
    let entries = fs::read_dir(directory).map_err(|error| format!("读取恢复目录失败：{error}"))?;
    let mut candidates = entries
        .flatten()
        .filter_map(|entry| {
            let path = entry.path();
            if path.extension().and_then(|value| value.to_str()) != Some("json") {
                return None;
            }
            let metadata = fs::metadata(&path).ok()?;
            (metadata.is_file() && metadata.len() > 0 && metadata.len() <= MAXIMUM_SNAPSHOT_BYTES)
                .then_some((path, metadata))
        })
        .collect::<Vec<_>>();
    candidates.sort_by_key(|(_, metadata)| std::cmp::Reverse(metadata.modified().ok()));
    let mut snapshots = Vec::new();
    let mut total_bytes = 0u64;
    for (path, metadata) in candidates {
        if snapshots.len() >= MAXIMUM_SNAPSHOTS {
            break;
        }
        let bytes = match fs::read(&path) {
            Ok(bytes) => bytes,
            Err(_) => continue,
        };
        if let Ok(snapshot) = serde_json::from_slice::<RecoverySnapshot>(&bytes)
            && validate_snapshot(&snapshot).is_ok()
            && !is_live_wimd_process(snapshot.owner_process_id)
        {
            total_bytes = total_bytes.saturating_add(metadata.len());
            if total_bytes > MAXIMUM_TOTAL_SNAPSHOT_BYTES {
                break;
            }
            snapshots.push(snapshot);
        }
    }
    snapshots.sort_by_key(|snapshot| std::cmp::Reverse(snapshot.saved_at_utc));
    Ok(snapshots)
}

#[cfg(windows)]
pub(crate) fn is_live_wimd_process(process_id: u32) -> bool {
    use std::{ffi::OsString, os::windows::ffi::OsStringExt};
    use windows_sys::Win32::{
        Foundation::CloseHandle,
        System::Threading::{
            OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, QueryFullProcessImageNameW,
        },
    };

    if process_id == 0 {
        return false;
    }
    let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, process_id) };
    if handle.is_null() {
        return false;
    }
    let mut buffer = vec![0u16; 32_768];
    let mut length = buffer.len() as u32;
    let succeeded =
        unsafe { QueryFullProcessImageNameW(handle, 0, buffer.as_mut_ptr(), &mut length) };
    unsafe {
        CloseHandle(handle);
    }
    if succeeded == 0 {
        return false;
    }
    let path = PathBuf::from(OsString::from_wide(&buffer[..length as usize]));
    let current_name = std::env::current_exe()
        .ok()
        .and_then(|path| path.file_name().map(|name| name.to_owned()));
    path.file_name()
        .zip(current_name)
        .is_some_and(|(candidate, current)| candidate.eq_ignore_ascii_case(&current))
}

#[cfg(not(windows))]
pub(crate) fn is_live_wimd_process(_process_id: u32) -> bool {
    false
}

pub fn remove(directory: &Path, id: &str) -> Result<(), String> {
    let path = snapshot_path(directory, id)?;
    if path.exists() {
        fs::remove_file(path).map_err(|error| format!("删除恢复快照失败：{error}"))?;
    }
    Ok(())
}

fn validate_snapshot(snapshot: &RecoverySnapshot) -> Result<(), String> {
    if snapshot.id.is_empty()
        || snapshot.id.len() > 80
        || !snapshot
            .id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        return Err("恢复快照编号无效。".to_owned());
    }
    if snapshot.text.len() as u64 > MAXIMUM_SNAPSHOT_BYTES {
        return Err("恢复快照正文过大。".to_owned());
    }
    Ok(())
}

fn snapshot_path(directory: &Path, id: &str) -> Result<PathBuf, String> {
    if id.is_empty()
        || id.len() > 80
        || !id
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
    {
        return Err("恢复快照编号无效。".to_owned());
    }
    let directory = normalize_path(directory).map_err(|error| error.to_string())?;
    Ok(directory.join(format!("{id}.json")))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn snapshots_round_trip_and_are_removed() {
        let directory = tempfile::tempdir().unwrap();
        let snapshot = RecoverySnapshot {
            id: "doc-1".to_owned(),
            owner_process_id: 0,
            original_path: Some(PathBuf::from("C:\\docs\\a.md")),
            title: "a.md".to_owned(),
            text: "正文".to_owned(),
            saved_text: "".to_owned(),
            had_utf8_bom: false,
            view_mode: "editorAndPreview".to_owned(),
            selection_start: 0,
            selection_end: 0,
            scroll_top: 0.0,
            preview_scroll_ratio: 0.0,
            saved_at_utc: Utc::now(),
        };
        save(directory.path(), &snapshot).unwrap();
        assert_eq!(list(directory.path()).unwrap()[0].text, "正文");
        remove(directory.path(), "doc-1").unwrap();
        assert!(list(directory.path()).unwrap().is_empty());
    }

    #[test]
    fn snapshot_id_cannot_escape_directory() {
        let directory = tempfile::tempdir().unwrap();
        let snapshot = RecoverySnapshot {
            id: "../escape".to_owned(),
            owner_process_id: 0,
            original_path: None,
            title: "x".to_owned(),
            text: "x".to_owned(),
            saved_text: "".to_owned(),
            had_utf8_bom: false,
            view_mode: "editorOnly".to_owned(),
            selection_start: 0,
            selection_end: 0,
            scroll_top: 0.0,
            preview_scroll_ratio: 0.0,
            saved_at_utc: Utc::now(),
        };
        assert!(save(directory.path(), &snapshot).is_err());
    }

    #[test]
    fn snapshots_from_a_live_wimd_process_are_not_offered_to_another_window() {
        let directory = tempfile::tempdir().unwrap();
        let mut snapshot = RecoverySnapshot {
            id: "doc-live".to_owned(),
            owner_process_id: std::process::id(),
            original_path: Some(PathBuf::from("C:\\docs\\a.md")),
            title: "a.md".to_owned(),
            text: "draft".to_owned(),
            saved_text: String::new(),
            had_utf8_bom: false,
            view_mode: "editorAndPreview".to_owned(),
            selection_start: 0,
            selection_end: 0,
            scroll_top: 0.0,
            preview_scroll_ratio: 0.0,
            saved_at_utc: Utc::now(),
        };
        save(directory.path(), &snapshot).unwrap();
        assert!(list(directory.path()).unwrap().is_empty());

        snapshot.owner_process_id = 0;
        save(directory.path(), &snapshot).unwrap();
        assert_eq!(list(directory.path()).unwrap().len(), 1);
    }
}

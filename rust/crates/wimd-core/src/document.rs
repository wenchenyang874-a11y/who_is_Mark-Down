use std::{
    fs::{self, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    time::UNIX_EPOCH,
};

use serde::Serialize;
use uuid::Uuid;

use crate::error::{CoreError, CoreResult};

const UTF8_BOM: [u8; 3] = [0xEF, 0xBB, 0xBF];
pub const MAXIMUM_DOCUMENT_BYTES: u64 = 64 * 1024 * 1024;

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub enum LineEnding {
    None,
    CrLf,
    Lf,
    Cr,
    Mixed,
    #[default]
    Unknown,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DocumentStamp {
    pub length: u64,
    pub modified_unix_ms: Option<u128>,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LoadedDocument {
    pub path: PathBuf,
    pub text: String,
    pub had_utf8_bom: bool,
    pub line_ending: LineEnding,
    pub read_only: bool,
    pub stamp: DocumentStamp,
}

pub fn normalize_path(path: impl AsRef<Path>) -> CoreResult<PathBuf> {
    let path = path.as_ref();
    if path.as_os_str().is_empty() {
        return Err(CoreError::InvalidName("路径不能为空。".to_owned()));
    }

    if path.exists() {
        return fs::canonicalize(path).map_err(|error| CoreError::io("规范化路径", path, error));
    }

    let parent = path.parent().unwrap_or_else(|| Path::new("."));
    let normalized_parent =
        fs::canonicalize(parent).map_err(|error| CoreError::io("规范化目录", parent, error))?;
    let file_name = path
        .file_name()
        .ok_or_else(|| CoreError::InvalidName("路径缺少文件名。".to_owned()))?;
    Ok(normalized_parent.join(file_name))
}

pub fn is_markdown_path(path: impl AsRef<Path>) -> bool {
    path.as_ref()
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| {
            extension.eq_ignore_ascii_case("md") || extension.eq_ignore_ascii_case("markdown")
        })
}

pub fn read_document(path: impl AsRef<Path>) -> CoreResult<LoadedDocument> {
    let requested_path = path.as_ref();
    let path = normalize_path(requested_path)?;

    if !is_markdown_path(&path) {
        return Err(CoreError::UnsupportedFileType { path });
    }

    let metadata =
        fs::metadata(&path).map_err(|error| CoreError::io("读取文件信息", &path, error))?;
    if !metadata.is_file() {
        return Err(CoreError::UnsupportedFileType { path });
    }
    if metadata.len() > MAXIMUM_DOCUMENT_BYTES {
        return Err(CoreError::FileTooLarge {
            path,
            maximum_bytes: MAXIMUM_DOCUMENT_BYTES,
        });
    }

    // A failed UTF-8 decode must never be made harmless by lossy conversion. The
    // original file remains untouched and the caller receives a specific error.
    let bytes = fs::read(&path).map_err(|error| CoreError::io("读取文档", &path, error))?;
    let had_utf8_bom = bytes.starts_with(&UTF8_BOM);
    let content = if had_utf8_bom {
        &bytes[UTF8_BOM.len()..]
    } else {
        &bytes[..]
    };
    let text = std::str::from_utf8(content)
        .map_err(|_| CoreError::InvalidUtf8 { path: path.clone() })?
        .to_owned();
    let line_ending = detect_line_ending(&text);
    let read_only = metadata.permissions().readonly();
    let stamp = document_stamp(&metadata);

    Ok(LoadedDocument {
        path,
        text,
        had_utf8_bom,
        line_ending,
        read_only,
        stamp,
    })
}

/// Saves through a same-directory temporary file and only replaces the target
/// after the complete file has been flushed and synced. A failed write therefore
/// leaves the previous document intact.
pub fn save_document_atomic(
    path: impl AsRef<Path>,
    text: &str,
    had_utf8_bom: bool,
) -> CoreResult<DocumentStamp> {
    let path = normalize_path(path)?;
    if !is_markdown_path(&path) {
        return Err(CoreError::UnsupportedFileType { path });
    }

    let parent = path
        .parent()
        .ok_or_else(|| CoreError::InvalidName("保存路径缺少目录。".to_owned()))?;
    if !parent.is_dir() {
        return Err(CoreError::MissingPath {
            path: parent.to_path_buf(),
        });
    }

    let temporary_path = parent.join(format!(
        ".{}.{}.tmp",
        path.file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("wimd-document"),
        Uuid::new_v4()
    ));

    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary_path)
            .map_err(|error| CoreError::io("创建临时文档", &temporary_path, error))?;

        if had_utf8_bom {
            file.write_all(&UTF8_BOM)
                .map_err(|error| CoreError::io("写入 UTF-8 BOM", &temporary_path, error))?;
        }
        file.write_all(text.as_bytes())
            .map_err(|error| CoreError::io("写入临时文档", &temporary_path, error))?;
        file.flush()
            .map_err(|error| CoreError::io("刷新临时文档", &temporary_path, error))?;
        file.sync_all()
            .map_err(|error| CoreError::io("同步临时文档", &temporary_path, error))?;
        drop(file);

        replace_file(&temporary_path, &path)?;
        inspect_document(&path)
    })();

    if result.is_err() {
        let _ = fs::remove_file(&temporary_path);
    }

    result
}

/// Writes arbitrary application-owned bytes through the same durable
/// same-directory temporary-file and atomic replacement policy used for
/// documents. PDF exports and managed background copies use this path.
pub fn write_bytes_atomic(path: impl AsRef<Path>, bytes: &[u8]) -> CoreResult<()> {
    let path = normalize_path(path)?;
    let parent = path
        .parent()
        .ok_or_else(|| CoreError::InvalidName("目标路径缺少目录。".to_owned()))?;
    if !parent.is_dir() {
        return Err(CoreError::MissingPath {
            path: parent.to_path_buf(),
        });
    }
    let temporary_path = parent.join(format!(".wimd-{}.tmp", Uuid::new_v4()));
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary_path)
            .map_err(|error| CoreError::io("创建临时文件", &temporary_path, error))?;
        file.write_all(bytes)
            .map_err(|error| CoreError::io("写入临时文件", &temporary_path, error))?;
        file.flush()
            .map_err(|error| CoreError::io("刷新临时文件", &temporary_path, error))?;
        file.sync_all()
            .map_err(|error| CoreError::io("同步临时文件", &temporary_path, error))?;
        drop(file);
        replace_file(&temporary_path, &path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary_path);
    }
    result
}

pub fn inspect_document(path: impl AsRef<Path>) -> CoreResult<DocumentStamp> {
    let path = normalize_path(path)?;
    let metadata =
        fs::metadata(&path).map_err(|error| CoreError::io("读取文件信息", &path, error))?;
    Ok(document_stamp(&metadata))
}

pub fn detect_line_ending(text: &str) -> LineEnding {
    let bytes = text.as_bytes();
    let mut crlf = 0usize;
    let mut lf = 0usize;
    let mut cr = 0usize;
    let mut index = 0usize;

    while index < bytes.len() {
        match bytes[index] {
            b'\r' if bytes.get(index + 1) == Some(&b'\n') => {
                crlf += 1;
                index += 2;
            }
            b'\r' => {
                cr += 1;
                index += 1;
            }
            b'\n' => {
                lf += 1;
                index += 1;
            }
            _ => index += 1,
        }
    }

    let kinds = usize::from(crlf > 0) + usize::from(lf > 0) + usize::from(cr > 0);
    match kinds {
        0 => LineEnding::None,
        1 if crlf > 0 => LineEnding::CrLf,
        1 if lf > 0 => LineEnding::Lf,
        1 => LineEnding::Cr,
        _ => LineEnding::Mixed,
    }
}

fn document_stamp(metadata: &fs::Metadata) -> DocumentStamp {
    DocumentStamp {
        length: metadata.len(),
        modified_unix_ms: metadata
            .modified()
            .ok()
            .and_then(|modified| modified.duration_since(UNIX_EPOCH).ok())
            .map(|duration| duration.as_millis()),
    }
}

/// Commits a fully written same-directory temporary file with the platform
/// replacement primitive. Callers remain responsible for flushing the file.
pub fn commit_atomic_file(temporary_path: &Path, target_path: &Path) -> CoreResult<()> {
    replace_file(temporary_path, target_path)
}

#[cfg(windows)]
pub(crate) fn replace_file(temporary_path: &Path, target_path: &Path) -> CoreResult<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::{
        MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
    };

    let temporary_wide: Vec<u16> = temporary_path
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let target_wide: Vec<u16> = target_path
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();

    // MOVEFILE_REPLACE_EXISTING gives Windows a single atomic replacement step
    // rather than delete-then-rename, which could lose the original on failure.
    let succeeded = unsafe {
        MoveFileExW(
            temporary_wide.as_ptr(),
            target_wide.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    };
    if succeeded == 0 {
        return Err(CoreError::io(
            "原子替换文档",
            target_path,
            std::io::Error::last_os_error(),
        ));
    }
    Ok(())
}

#[cfg(not(windows))]
pub(crate) fn replace_file(temporary_path: &Path, target_path: &Path) -> CoreResult<()> {
    fs::rename(temporary_path, target_path)
        .map_err(|error| CoreError::io("原子替换文档", target_path, error))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_line_endings_without_normalizing_content() {
        assert_eq!(detect_line_ending("a\r\nb\r\n"), LineEnding::CrLf);
        assert_eq!(detect_line_ending("a\nb\n"), LineEnding::Lf);
        assert_eq!(detect_line_ending("a\rb\r"), LineEnding::Cr);
        assert_eq!(detect_line_ending("a\r\nb\n"), LineEnding::Mixed);
        assert_eq!(detect_line_ending("正文"), LineEnding::None);
    }

    #[test]
    fn reads_and_preserves_utf8_bom() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("中文.md");
        let mut bytes = UTF8_BOM.to_vec();
        bytes.extend_from_slice("标题\r\n".as_bytes());
        fs::write(&path, bytes).unwrap();

        let loaded = read_document(&path).unwrap();

        assert!(loaded.had_utf8_bom);
        assert_eq!(loaded.text, "标题\r\n");
        assert_eq!(loaded.line_ending, LineEnding::CrLf);
    }

    #[test]
    fn save_replaces_existing_document_and_keeps_bom_choice() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("文档.md");
        fs::write(&path, "旧内容").unwrap();

        save_document_atomic(&path, "新内容\n", true).unwrap();

        let bytes = fs::read(&path).unwrap();
        assert!(bytes.starts_with(&UTF8_BOM));
        assert_eq!(&bytes[UTF8_BOM.len()..], "新内容\n".as_bytes());
        assert_eq!(
            fs::read_dir(directory.path())
                .unwrap()
                .filter_map(Result::ok)
                .count(),
            1,
            "the temporary file must not remain after a successful save"
        );
    }

    #[test]
    fn rejects_invalid_utf8_without_touching_the_file() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("坏文件.md");
        fs::write(&path, [0xFF, 0xFE, 0xFD]).unwrap();

        let error = read_document(&path).unwrap_err();

        assert!(matches!(error, CoreError::InvalidUtf8 { .. }));
        assert_eq!(fs::read(&path).unwrap(), [0xFF, 0xFE, 0xFD]);
    }

    #[test]
    fn only_markdown_extensions_are_accepted() {
        assert!(is_markdown_path("README.MD"));
        assert!(is_markdown_path("说明.markdown"));
        assert!(!is_markdown_path("说明.txt"));
    }

    #[test]
    fn arbitrary_bytes_replace_atomically_without_leaving_temp_files() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("export.pdf");
        fs::write(&path, b"old").unwrap();

        write_bytes_atomic(&path, b"new").unwrap();

        assert_eq!(fs::read(&path).unwrap(), b"new");
        assert_eq!(
            fs::read_dir(directory.path())
                .unwrap()
                .filter_map(Result::ok)
                .count(),
            1
        );
    }
}

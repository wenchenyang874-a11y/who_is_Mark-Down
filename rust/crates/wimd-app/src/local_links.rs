// Bug fix: local Markdown links were either opened without the old path
// confirmation or rejected as generic external links. Resolve them in Rust,
// reject traversal and executable targets, and let the UI confirm the exact
// normalized path before opening anything.

use std::{
    collections::HashSet,
    path::{Component, Path, PathBuf},
    sync::OnceLock,
};

use serde::Serialize;
use wimd_core::document::normalize_path;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalLinkTarget {
    pub path: PathBuf,
    pub exists: bool,
    pub is_markdown: bool,
}

pub fn resolve_local_link(document_path: &Path, href: &str) -> Result<LocalLinkTarget, String> {
    if href.len() > 4096 || href.chars().any(char::is_control) {
        return Err("本地链接为空、过长或包含控制字符。".to_owned());
    }
    let document_path = normalize_path(document_path)
        .map_err(|error| format!("无法规范化当前文档路径：{error}"))?;
    if !document_path.is_file() {
        return Err("当前文档路径不存在或不是文件。".to_owned());
    }
    let document_directory = document_path
        .parent()
        .ok_or_else(|| "当前文档缺少所在目录。".to_owned())?;
    let relative = href.split(['?', '#']).next().unwrap_or_default().trim();
    if relative.is_empty() {
        return Err("本地链接没有目标路径。".to_owned());
    }
    let decoded = decode_percent_encoded(relative)?;
    let normalized = decoded.replace('/', "\\");
    if normalized.starts_with('\\') || normalized.contains(':') {
        return Err("本地链接必须是当前文档目录内的相对路径。".to_owned());
    }
    let relative_path = Path::new(&normalized);
    if relative_path.components().any(|component| {
        matches!(
            component,
            Component::ParentDir | Component::RootDir | Component::Prefix(_)
        )
    }) {
        return Err("本地链接不能跳出当前文档目录。".to_owned());
    }
    if relative_path
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(is_blocked_extension)
    {
        return Err("出于安全原因，不允许从预览直接打开可执行文件。".to_owned());
    }

    let candidate = document_directory.join(relative_path);
    let exists = candidate.is_file();
    let path = if exists {
        normalize_path(&candidate).map_err(|error| format!("无法规范化本地链接目标：{error}"))?
    } else {
        normalize_parent_for_missing(&candidate)?
    };
    if !path.starts_with(document_directory) {
        return Err("本地链接目标不在当前文档目录内。".to_owned());
    }
    Ok(LocalLinkTarget {
        is_markdown: path
            .extension()
            .and_then(|extension| extension.to_str())
            .is_some_and(|extension| {
                extension.eq_ignore_ascii_case("md") || extension.eq_ignore_ascii_case("markdown")
            }),
        path,
        exists,
    })
}

fn normalize_parent_for_missing(path: &Path) -> Result<PathBuf, String> {
    let parent = path
        .parent()
        .ok_or_else(|| "本地链接目标缺少所在目录。".to_owned())?;
    let name = path
        .file_name()
        .ok_or_else(|| "本地链接目标缺少文件名。".to_owned())?;
    let parent =
        normalize_path(parent).map_err(|error| format!("无法规范化本地链接目录：{error}"))?;
    Ok(parent.join(name))
}

fn decode_percent_encoded(value: &str) -> Result<String, String> {
    let bytes = value.as_bytes();
    let mut decoded = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' {
            if index + 2 >= bytes.len() {
                return Err("本地链接包含不完整的百分号编码。".to_owned());
            }
            let high = hex_value(bytes[index + 1])
                .ok_or_else(|| "本地链接包含无效的百分号编码。".to_owned())?;
            let low = hex_value(bytes[index + 2])
                .ok_or_else(|| "本地链接包含无效的百分号编码。".to_owned())?;
            decoded.push(high << 4 | low);
            index += 3;
        } else {
            decoded.push(bytes[index]);
            index += 1;
        }
    }
    let decoded = String::from_utf8(decoded).map_err(|_| "本地链接不是有效 UTF-8。".to_owned())?;
    if decoded.chars().any(char::is_control) {
        return Err("本地链接包含控制字符。".to_owned());
    }
    Ok(decoded)
}

fn hex_value(value: u8) -> Option<u8> {
    match value {
        b'0'..=b'9' => Some(value - b'0'),
        b'a'..=b'f' => Some(value - b'a' + 10),
        b'A'..=b'F' => Some(value - b'A' + 10),
        _ => None,
    }
}

fn is_blocked_extension(extension: &str) -> bool {
    static BLOCKED: OnceLock<HashSet<String>> = OnceLock::new();
    BLOCKED
        .get_or_init(|| {
            [
                "bat", "cmd", "com", "cpl", "exe", "hta", "jar", "js", "jse", "lnk", "msi", "msp",
                "ps1", "reg", "scr", "sh", "url", "vb", "vbe", "vbs", "wsf", "wsh",
            ]
            .into_iter()
            .map(str::to_owned)
            .collect()
        })
        .contains(&extension.to_ascii_lowercase())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn resolves_percent_encoded_files_inside_the_document_directory() {
        let directory = tempfile::tempdir().unwrap();
        let document = directory.path().join("main.md");
        let target = directory.path().join("子目录").join("说明.md");
        fs::create_dir_all(target.parent().unwrap()).unwrap();
        fs::write(&document, "# main").unwrap();
        fs::write(&target, "# target").unwrap();

        let resolved = resolve_local_link(
            &document,
            "%E5%AD%90%E7%9B%AE%E5%BD%95/%E8%AF%B4%E6%98%8E.md#top",
        )
        .unwrap();

        assert!(resolved.exists);
        assert!(resolved.is_markdown);
        assert_eq!(resolved.path, fs::canonicalize(target).unwrap());
    }

    #[test]
    fn rejects_traversal_executables_and_malformed_encoding() {
        let directory = tempfile::tempdir().unwrap();
        let document = directory.path().join("main.md");
        fs::write(&document, "# main").unwrap();

        assert!(resolve_local_link(&document, "../secret.md").is_err());
        assert!(resolve_local_link(&document, "%2e%2e/secret.md").is_err());
        assert!(resolve_local_link(&document, "run.exe").is_err());
        assert!(resolve_local_link(&document, "bad%2.md").is_err());
    }
}

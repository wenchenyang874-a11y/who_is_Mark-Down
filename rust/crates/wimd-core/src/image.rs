use std::{
    fs::{self, File},
    io::{Read, Write},
    path::{Component, Path, PathBuf},
};

use base64::{Engine as _, engine::general_purpose::STANDARD};
use serde::Serialize;

use crate::{
    document::write_bytes_atomic,
    error::{CoreError, CoreResult},
    workspace::{is_safe_relative_directory, path_is_at_or_below},
};

pub const MAXIMUM_IMAGE_BYTES: u64 = 32 * 1024 * 1024;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StoredImage {
    pub path: PathBuf,
    pub markdown_path: String,
    pub byte_length: u64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalImageData {
    pub data_uri: String,
    pub mime_type: String,
}

pub fn store_image_file(
    document_path: impl AsRef<Path>,
    source_path: impl AsRef<Path>,
    relative_directory: &str,
) -> CoreResult<StoredImage> {
    let source_path = source_path.as_ref();
    let metadata = fs::metadata(source_path)
        .map_err(|error| CoreError::io("读取图片信息", source_path, error))?;
    if metadata.len() > MAXIMUM_IMAGE_BYTES {
        return Err(CoreError::FileTooLarge {
            path: source_path.to_path_buf(),
            maximum_bytes: MAXIMUM_IMAGE_BYTES,
        });
    }

    let mut bytes = Vec::with_capacity(metadata.len() as usize);
    File::open(source_path)
        .map_err(|error| CoreError::io("打开图片", source_path, error))?
        .read_to_end(&mut bytes)
        .map_err(|error| CoreError::io("读取图片", source_path, error))?;
    let source_extension = source_path
        .extension()
        .and_then(|extension| extension.to_str())
        .unwrap_or_default();
    store_image_bytes(document_path, &bytes, source_extension, relative_directory)
}

pub fn store_image_bytes(
    document_path: impl AsRef<Path>,
    bytes: &[u8],
    preferred_extension: &str,
    relative_directory: &str,
) -> CoreResult<StoredImage> {
    if bytes.len() as u64 > MAXIMUM_IMAGE_BYTES {
        return Err(CoreError::FileTooLarge {
            path: document_path.as_ref().to_path_buf(),
            maximum_bytes: MAXIMUM_IMAGE_BYTES,
        });
    }
    let mime_type = detect_image_mime(bytes).ok_or_else(|| CoreError::UnsupportedImage {
        path: PathBuf::from("所选图片"),
    })?;
    let extension = extension_for_mime(mime_type);
    if !preferred_extension.is_empty() && !extension_matches(preferred_extension, extension) {
        return Err(CoreError::UnsupportedImage {
            path: PathBuf::from(format!("image.{preferred_extension}")),
        });
    }

    let document_path = document_path.as_ref();
    let root = document_path
        .parent()
        .ok_or_else(|| CoreError::InvalidName("文档缺少所在目录。".to_owned()))?;
    let root =
        fs::canonicalize(root).map_err(|error| CoreError::io("规范化文档目录", root, error))?;
    let image_directory = create_safe_directory_tree(&root, relative_directory)?;
    let file_name = unique_image_name(&image_directory, extension);
    let target = image_directory.join(&file_name);

    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&target)
        .map_err(|error| CoreError::io("创建图片文件", &target, error))?;
    file.write_all(bytes)
        .map_err(|error| CoreError::io("写入图片文件", &target, error))?;
    file.flush()
        .map_err(|error| CoreError::io("刷新图片文件", &target, error))?;
    file.sync_all()
        .map_err(|error| CoreError::io("同步图片文件", &target, error))?;

    let relative = target
        .strip_prefix(&root)
        .map_err(|_| CoreError::OutsideRoot {
            path: target.clone(),
        })?
        .to_string_lossy()
        .replace('\\', "/");
    Ok(StoredImage {
        path: target,
        markdown_path: format!("./{relative}"),
        byte_length: bytes.len() as u64,
    })
}

pub fn load_local_image(
    document_path: impl AsRef<Path>,
    image_source: &str,
) -> CoreResult<LocalImageData> {
    let document_path = document_path.as_ref();
    let root = document_path
        .parent()
        .ok_or_else(|| CoreError::InvalidName("文档缺少所在目录。".to_owned()))?;
    let root =
        fs::canonicalize(root).map_err(|error| CoreError::io("规范化文档目录", root, error))?;

    let source_without_suffix = image_source
        .split(['?', '#'])
        .next()
        .unwrap_or(image_source)
        .replace('\\', "/");
    if source_without_suffix.starts_with('/')
        || source_without_suffix.starts_with("//")
        || source_without_suffix.contains(':')
    {
        return Err(CoreError::OutsideRoot {
            path: PathBuf::from(image_source),
        });
    }
    let relative_source = source_without_suffix.trim_start_matches("./");
    let relative = Path::new(relative_source);
    if relative
        .components()
        .any(|component| !matches!(component, Component::Normal(_)))
    {
        return Err(CoreError::OutsideRoot {
            path: PathBuf::from(image_source),
        });
    }

    let candidate = root.join(relative);
    let candidate = fs::canonicalize(&candidate)
        .map_err(|error| CoreError::io("读取本地图片", &candidate, error))?;
    if !path_is_at_or_below(&candidate, &root) {
        return Err(CoreError::OutsideRoot { path: candidate });
    }
    let metadata = fs::metadata(&candidate)
        .map_err(|error| CoreError::io("读取图片信息", &candidate, error))?;
    if !metadata.is_file() || metadata.len() > MAXIMUM_IMAGE_BYTES {
        return Err(CoreError::FileTooLarge {
            path: candidate,
            maximum_bytes: MAXIMUM_IMAGE_BYTES,
        });
    }
    let bytes =
        fs::read(&candidate).map_err(|error| CoreError::io("读取图片", &candidate, error))?;
    let mime_type = detect_image_mime(&bytes).ok_or_else(|| CoreError::UnsupportedImage {
        path: candidate.clone(),
    })?;

    Ok(LocalImageData {
        data_uri: format!("data:{mime_type};base64,{}", STANDARD.encode(bytes)),
        mime_type: mime_type.to_owned(),
    })
}

pub fn store_managed_background(
    source_path: impl AsRef<Path>,
    managed_directory: impl AsRef<Path>,
) -> CoreResult<PathBuf> {
    let source_path = source_path.as_ref();
    let metadata = fs::metadata(source_path)
        .map_err(|error| CoreError::io("读取背景图片信息", source_path, error))?;
    if !metadata.is_file() || metadata.len() > MAXIMUM_IMAGE_BYTES {
        return Err(CoreError::FileTooLarge {
            path: source_path.to_path_buf(),
            maximum_bytes: MAXIMUM_IMAGE_BYTES,
        });
    }
    let bytes =
        fs::read(source_path).map_err(|error| CoreError::io("读取背景图片", source_path, error))?;
    let mime_type = detect_image_mime(&bytes).ok_or_else(|| CoreError::UnsupportedImage {
        path: source_path.to_path_buf(),
    })?;
    if mime_type == "image/svg+xml" {
        return Err(CoreError::UnsupportedImage {
            path: source_path.to_path_buf(),
        });
    }

    let directory = managed_directory.as_ref();
    fs::create_dir_all(directory)
        .map_err(|error| CoreError::io("创建背景目录", directory, error))?;
    let directory = fs::canonicalize(directory)
        .map_err(|error| CoreError::io("规范化背景目录", directory, error))?;
    let extension = extension_for_mime(mime_type);
    let target = directory.join(format!("background-{}.{}", uuid::Uuid::new_v4(), extension));
    write_bytes_atomic(&target, &bytes)?;
    Ok(target)
}

pub fn load_managed_background(
    managed_directory: impl AsRef<Path>,
    path: impl AsRef<Path>,
) -> CoreResult<LocalImageData> {
    let directory = fs::canonicalize(managed_directory.as_ref())
        .map_err(|error| CoreError::io("读取背景目录", managed_directory.as_ref(), error))?;
    let path = fs::canonicalize(path.as_ref())
        .map_err(|error| CoreError::io("读取背景副本", path.as_ref(), error))?;
    if !path_is_at_or_below(&path, &directory) || !path.is_file() {
        return Err(CoreError::OutsideRoot { path });
    }
    let metadata =
        fs::metadata(&path).map_err(|error| CoreError::io("读取背景副本信息", &path, error))?;
    if metadata.len() > MAXIMUM_IMAGE_BYTES {
        return Err(CoreError::FileTooLarge {
            path,
            maximum_bytes: MAXIMUM_IMAGE_BYTES,
        });
    }
    let bytes = fs::read(&path).map_err(|error| CoreError::io("读取背景副本", &path, error))?;
    let mime_type = detect_image_mime(&bytes)
        .ok_or_else(|| CoreError::UnsupportedImage { path: path.clone() })?;
    if mime_type == "image/svg+xml" {
        return Err(CoreError::UnsupportedImage { path });
    }
    Ok(LocalImageData {
        data_uri: format!("data:{mime_type};base64,{}", STANDARD.encode(bytes)),
        mime_type: mime_type.to_owned(),
    })
}

pub fn detect_image_mime(bytes: &[u8]) -> Option<&'static str> {
    if bytes.starts_with(&[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A]) {
        return Some("image/png");
    }
    if bytes.starts_with(&[0xFF, 0xD8, 0xFF]) {
        return Some("image/jpeg");
    }
    if bytes.starts_with(b"GIF87a") || bytes.starts_with(b"GIF89a") {
        return Some("image/gif");
    }
    if bytes.starts_with(b"BM") {
        return Some("image/bmp");
    }
    if bytes.len() >= 12 && &bytes[..4] == b"RIFF" && &bytes[8..12] == b"WEBP" {
        return Some("image/webp");
    }

    let prefix = String::from_utf8_lossy(&bytes[..bytes.len().min(2048)]);
    let trimmed = prefix.trim_start_matches('\u{feff}').trim_start();
    if trimmed.starts_with("<svg") || (trimmed.starts_with("<?xml") && trimmed.contains("<svg")) {
        return Some("image/svg+xml");
    }
    None
}

fn extension_for_mime(mime_type: &str) -> &'static str {
    match mime_type {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        "image/webp" => "webp",
        "image/svg+xml" => "svg",
        _ => "bin",
    }
}

fn extension_matches(preferred: &str, actual: &str) -> bool {
    let preferred = preferred.trim_start_matches('.').to_ascii_lowercase();
    preferred == actual || (actual == "jpg" && preferred == "jpeg")
}

fn create_safe_directory_tree(root: &Path, relative_directory: &str) -> CoreResult<PathBuf> {
    if !is_safe_relative_directory(relative_directory) {
        return Err(CoreError::InvalidName(
            "图片目录必须是不含目录穿越的相对路径。".to_owned(),
        ));
    }

    let mut current = root.to_path_buf();
    for component in Path::new(relative_directory).components() {
        let Component::Normal(name) = component else {
            return Err(CoreError::InvalidName(
                "图片目录包含无效路径段。".to_owned(),
            ));
        };
        current.push(name);
        match fs::symlink_metadata(&current) {
            Ok(metadata) if metadata.file_type().is_symlink() => {
                return Err(CoreError::OutsideRoot { path: current });
            }
            Ok(metadata) if !metadata.is_dir() => {
                return Err(CoreError::UnsupportedFileType { path: current });
            }
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                fs::create_dir(&current)
                    .map_err(|error| CoreError::io("创建图片目录", &current, error))?;
            }
            Err(error) => return Err(CoreError::io("检查图片目录", &current, error)),
        }
    }

    let normalized = fs::canonicalize(&current)
        .map_err(|error| CoreError::io("规范化图片目录", &current, error))?;
    if !path_is_at_or_below(&normalized, root) {
        return Err(CoreError::OutsideRoot { path: normalized });
    }
    Ok(normalized)
}

fn unique_image_name(directory: &Path, extension: &str) -> String {
    for index in 1..=10_000 {
        let candidate = format!("image-{index:04}.{extension}");
        if !directory.join(&candidate).exists() {
            return candidate;
        }
    }
    format!("image-{}.{}", uuid::Uuid::new_v4(), extension)
}

#[cfg(test)]
mod tests {
    use super::*;

    const PNG: &[u8] = &[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    #[test]
    fn stores_png_in_configured_document_subdirectory() {
        let directory = tempfile::tempdir().unwrap();
        let document = directory.path().join("doc.md");
        fs::write(&document, "# doc").unwrap();

        let stored = store_image_bytes(&document, PNG, "png", "assets/img").unwrap();

        let expected_directory = fs::canonicalize(directory.path().join("assets/img")).unwrap();
        assert!(stored.path.starts_with(expected_directory));
        assert_eq!(stored.markdown_path, "./assets/img/image-0001.png");
        assert_eq!(fs::read(stored.path).unwrap(), PNG);
    }

    #[test]
    fn rejects_directory_traversal_and_extension_mismatch() {
        let directory = tempfile::tempdir().unwrap();
        let document = directory.path().join("doc.md");
        fs::write(&document, "").unwrap();

        assert!(store_image_bytes(&document, PNG, "png", "../outside").is_err());
        assert!(store_image_bytes(&document, PNG, "jpg", "img").is_err());
    }

    #[test]
    fn local_image_loader_returns_data_uri_and_rejects_escaping_links() {
        let directory = tempfile::tempdir().unwrap();
        let document = directory.path().join("doc.md");
        fs::write(&document, "").unwrap();
        fs::create_dir(directory.path().join("img")).unwrap();
        fs::write(directory.path().join("img/p.png"), PNG).unwrap();

        let loaded = load_local_image(&document, "./img/p.png?cache=1").unwrap();

        assert_eq!(loaded.mime_type, "image/png");
        assert!(loaded.data_uri.starts_with("data:image/png;base64,"));
        assert!(load_local_image(&document, "../p.png").is_err());
    }

    #[test]
    fn detects_common_image_magic_without_trusting_extensions() {
        assert_eq!(detect_image_mime(PNG), Some("image/png"));
        assert_eq!(
            detect_image_mime(&[0xFF, 0xD8, 0xFF, 0]),
            Some("image/jpeg")
        );
        assert_eq!(
            detect_image_mime(b"<svg xmlns=\"http://www.w3.org/2000/svg\"/>"),
            Some("image/svg+xml")
        );
        assert_eq!(detect_image_mime(b"not an image"), None);
    }

    #[test]
    fn managed_background_is_copied_and_loaded_without_referencing_original() {
        let source_dir = tempfile::tempdir().unwrap();
        let managed_dir = tempfile::tempdir().unwrap();
        let source = source_dir.path().join("background.png");
        fs::write(&source, PNG).unwrap();

        let copied = store_managed_background(&source, managed_dir.path()).unwrap();
        fs::remove_file(&source).unwrap();
        let loaded = load_managed_background(managed_dir.path(), &copied).unwrap();

        assert!(copied.exists());
        assert!(loaded.data_uri.starts_with("data:image/png;base64,"));
    }

    #[test]
    fn managed_background_rejects_paths_outside_managed_directory() {
        let managed = tempfile::tempdir().unwrap();
        let outside = tempfile::tempdir().unwrap();
        let image = outside.path().join("outside.png");
        fs::write(&image, PNG).unwrap();

        assert!(load_managed_background(managed.path(), &image).is_err());
    }
}

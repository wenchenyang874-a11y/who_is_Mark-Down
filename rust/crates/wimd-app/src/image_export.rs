use std::{
    collections::{BTreeMap, VecDeque},
    path::Path,
    sync::{Arc, Mutex},
    time::Duration,
};

use base64::{Engine as _, engine::general_purpose::STANDARD};
use serde::Serialize;
use wimd_core::{
    document::write_bytes_atomic,
    image::{MAXIMUM_IMAGE_BYTES, detect_image_mime},
};

const MAXIMUM_REDIRECTS: usize = 5;
const MAXIMUM_CACHE_BYTES: u64 = 32 * 1024 * 1024;
const MAXIMUM_CACHE_ENTRIES: usize = 8;

#[derive(Clone, Copy, Debug)]
pub enum ExportFormat {
    Original,
    Png,
    Html,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreparedImage {
    pub data_uri: String,
    pub mime_type: String,
    pub extension: String,
    #[serde(skip)]
    byte_len: u64,
}

pub async fn prepare(source: &str) -> Result<PreparedImage, String> {
    let bytes = if source.starts_with("data:") {
        decode_data_uri(source)?.0
    } else {
        fetch_remote(source).await?
    };
    prepare_bytes(bytes)
}

fn prepare_bytes(bytes: Vec<u8>) -> Result<PreparedImage, String> {
    if bytes.is_empty() || bytes.len() as u64 > MAXIMUM_IMAGE_BYTES {
        return Err("图片大小必须在 1 字节到 32 MiB 之间。".to_owned());
    }
    let mime = detect_image_mime(&bytes).ok_or_else(|| "无法识别图片格式。".to_owned())?;
    Ok(PreparedImage {
        data_uri: format!("data:{mime};base64,{}", STANDARD.encode(&bytes)),
        mime_type: mime.to_owned(),
        extension: extension_for_mime(mime).to_owned(),
        byte_len: bytes.len() as u64,
    })
}

#[derive(Clone, Default)]
pub struct ImageResponseCache {
    inner: Arc<Mutex<ImageCacheState>>,
}

impl ImageResponseCache {
    pub async fn prepare(&self, source: &str) -> Result<PreparedImage, String> {
        if source.starts_with("data:") {
            return prepare(source).await;
        }
        let key = remote_cache_key(source)?;
        if let Some(cached) = self.get(&key) {
            return Ok(cached);
        }
        let prepared = prepare(source).await?;
        self.insert(key, prepared.clone());
        Ok(prepared)
    }

    fn get(&self, key: &str) -> Option<PreparedImage> {
        let mut state = self.inner.lock().ok()?;
        let prepared = state.entries.get(key)?.clone();
        state.order.retain(|candidate| candidate != key);
        state.order.push_back(key.to_owned());
        Some(prepared)
    }

    fn insert(&self, key: String, prepared: PreparedImage) {
        if prepared.byte_len > MAXIMUM_CACHE_BYTES {
            return;
        }
        let Ok(mut state) = self.inner.lock() else {
            return;
        };
        if let Some(previous) = state.entries.remove(&key) {
            state.bytes = state.bytes.saturating_sub(previous.byte_len);
            state.order.retain(|candidate| candidate != &key);
        }
        state.bytes = state.bytes.saturating_add(prepared.byte_len);
        state.entries.insert(key.clone(), prepared);
        state.order.push_back(key);
        while state.entries.len() > MAXIMUM_CACHE_ENTRIES || state.bytes > MAXIMUM_CACHE_BYTES {
            let Some(oldest) = state.order.pop_front() else {
                break;
            };
            if let Some(removed) = state.entries.remove(&oldest) {
                state.bytes = state.bytes.saturating_sub(removed.byte_len);
            }
        }
    }
}

#[derive(Default)]
struct ImageCacheState {
    entries: BTreeMap<String, PreparedImage>,
    order: VecDeque<String>,
    bytes: u64,
}

fn remote_cache_key(source: &str) -> Result<String, String> {
    let mut url = reqwest::Url::parse(source).map_err(|_| "图片地址无效。".to_owned())?;
    validate_remote_url(&url)?;
    url.set_fragment(None);
    Ok(url.to_string())
}

pub fn save(
    data_uri: &str,
    format: ExportFormat,
    target: &Path,
) -> Result<std::path::PathBuf, String> {
    let (bytes, mime) = decode_data_uri(data_uri)?;
    if bytes.is_empty() || bytes.len() as u64 > MAXIMUM_IMAGE_BYTES {
        return Err("图片大小必须在 1 字节到 32 MiB 之间。".to_owned());
    }
    let actual_mime = detect_image_mime(&bytes).ok_or_else(|| "无法识别图片格式。".to_owned())?;
    let extension = match format {
        ExportFormat::Original => extension_for_mime(actual_mime),
        ExportFormat::Png if actual_mime == "image/png" => "png",
        ExportFormat::Png => {
            return Err("PNG 保存必须由 WebView2 画布转换后提交。".to_owned());
        }
        ExportFormat::Html => "html",
    };
    let target = target.with_extension(extension);
    match format {
        ExportFormat::Original => {
            write_bytes_atomic(&target, &bytes).map_err(|error| error.to_string())?;
            Ok(target)
        }
        ExportFormat::Png => {
            write_bytes_atomic(&target, &bytes).map_err(|error| error.to_string())?;
            Ok(target)
        }
        ExportFormat::Html => {
            let html = format!(
                "<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\"><title>WIMD 图片</title><style>html,body{{height:100%;margin:0;background:#111;}}body{{display:grid;place-items:center;}}img{{max-width:100%;max-height:100%;}}</style></head><body><img alt=\"WIMD 图片\" src=\"data:{};base64,{}\"></body></html>",
                escape_html(mime),
                STANDARD.encode(bytes)
            );
            write_bytes_atomic(&target, html.as_bytes()).map_err(|error| error.to_string())?;
            Ok(target)
        }
    }
}

async fn fetch_remote(source: &str) -> Result<Vec<u8>, String> {
    let mut current = reqwest::Url::parse(source).map_err(|_| "图片地址无效。".to_owned())?;
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(60))
        .redirect(reqwest::redirect::Policy::none())
        .user_agent(format!("WIMD/{}", env!("CARGO_PKG_VERSION")))
        .build()
        .map_err(|_| "无法创建图片读取客户端。".to_owned())?;
    for redirect in 0..=MAXIMUM_REDIRECTS {
        validate_remote_url(&current)?;
        let response = client
            .get(current.clone())
            .send()
            .await
            .map_err(|_| "读取远程图片失败。".to_owned())?;
        if response.status().is_redirection() {
            let location = response
                .headers()
                .get(reqwest::header::LOCATION)
                .and_then(|value| value.to_str().ok())
                .ok_or_else(|| "远程图片重定向缺少目标地址。".to_owned())?;
            current = current
                .join(location)
                .map_err(|_| "远程图片重定向地址无效。".to_owned())?;
            if redirect == MAXIMUM_REDIRECTS {
                return Err("远程图片重定向次数过多。".to_owned());
            }
            continue;
        }
        if !response.status().is_success() {
            return Err(format!(
                "远程图片读取失败（HTTP {}）。",
                response.status().as_u16()
            ));
        }
        let mut response = response;
        if response
            .content_length()
            .is_some_and(|length| length > MAXIMUM_IMAGE_BYTES)
        {
            return Err("远程图片超过 32 MiB 上限。".to_owned());
        }
        let mut bytes = Vec::new();
        while let Some(chunk) = response
            .chunk()
            .await
            .map_err(|_| "读取远程图片失败。".to_owned())?
        {
            if bytes.len() as u64 + chunk.len() as u64 > MAXIMUM_IMAGE_BYTES {
                return Err("远程图片超过 32 MiB 上限。".to_owned());
            }
            bytes.extend_from_slice(&chunk);
        }
        return Ok(bytes);
    }
    Err("远程图片重定向次数过多。".to_owned())
}

fn validate_remote_url(url: &reqwest::Url) -> Result<(), String> {
    if url.scheme() != "http" && url.scheme() != "https" {
        return Err("图片另存只允许 HTTP 或 HTTPS 地址。".to_owned());
    }
    if !url.username().is_empty() || url.password().is_some() {
        return Err("图片地址不能包含用户名或密码。".to_owned());
    }
    Ok(())
}

fn decode_data_uri(value: &str) -> Result<(Vec<u8>, &str), String> {
    let (header, payload) = value
        .split_once(',')
        .ok_or_else(|| "图片数据 URI 缺少内容。".to_owned())?;
    let payload_header = header
        .strip_prefix("data:")
        .ok_or_else(|| "图片数据 URI 格式无效。".to_owned())?;
    let is_base64 = payload_header.ends_with(";base64");
    let mime = payload_header
        .strip_suffix(";base64")
        .unwrap_or(payload_header)
        .split(';')
        .next()
        .unwrap_or_default();
    if !mime.starts_with("image/") {
        return Err("图片数据 URI 格式无效。".to_owned());
    }
    let bytes = if is_base64 {
        STANDARD
            .decode(payload)
            .map_err(|_| "图片数据 URI 的 base64 无效。".to_owned())?
    } else {
        percent_decode(payload)?
    };
    if bytes.is_empty() {
        return Err("图片数据 URI 没有内容。".to_owned());
    }
    Ok((bytes, mime))
}

fn percent_decode(value: &str) -> Result<Vec<u8>, String> {
    let bytes = value.as_bytes();
    let mut result = Vec::with_capacity(bytes.len());
    let mut index = 0;
    while index < bytes.len() {
        if bytes[index] == b'%' {
            if index + 2 >= bytes.len() {
                return Err("图片数据 URI 的百分号编码不完整。".to_owned());
            }
            let high = hex_value(bytes[index + 1])?;
            let low = hex_value(bytes[index + 2])?;
            result.push((high << 4) | low);
            index += 3;
        } else {
            result.push(bytes[index]);
            index += 1;
        }
    }
    Ok(result)
}

fn hex_value(value: u8) -> Result<u8, String> {
    match value {
        b'0'..=b'9' => Ok(value - b'0'),
        b'a'..=b'f' => Ok(value - b'a' + 10),
        b'A'..=b'F' => Ok(value - b'A' + 10),
        _ => Err("图片数据 URI 的百分号编码无效。".to_owned()),
    }
}

fn extension_for_mime(mime: &str) -> &'static str {
    match mime {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        "image/webp" => "webp",
        "image/svg+xml" => "svg",
        _ => "img",
    }
}

fn escape_html(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
}

pub fn parse_format(value: &str) -> Result<ExportFormat, String> {
    match value {
        "original" => Ok(ExportFormat::Original),
        "png" => Ok(ExportFormat::Png),
        "html" => Ok(ExportFormat::Html),
        _ => Err("不支持的图片另存格式。".to_owned()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn data_uri_decode_requires_image_mime_and_base64() {
        assert!(decode_data_uri("data:image/png;base64,iVBORw0KGgo=").is_ok());
        assert!(decode_data_uri("data:text/html;base64,SGk=").is_err());
        assert!(decode_data_uri("data:image/svg+xml;charset=utf-8,%3Csvg%3E%3C%2Fsvg%3E").is_ok());
    }

    #[test]
    fn remote_image_urls_reject_local_and_credentialed_sources() {
        assert!(
            validate_remote_url(&reqwest::Url::parse("https://example.com/a.png").unwrap()).is_ok()
        );
        assert!(validate_remote_url(&reqwest::Url::parse("file:///C:/a.png").unwrap()).is_err());
        assert!(
            validate_remote_url(&reqwest::Url::parse("https://u:p@example.com/a.png").unwrap())
                .is_err()
        );
    }

    #[test]
    fn self_contained_html_has_no_external_image_reference() {
        let directory = tempfile::tempdir().unwrap();
        let target = directory.path().join("image.html");
        let data = "data:image/png;base64,iVBORw0KGgo=";
        save(data, ExportFormat::Html, &target).unwrap();
        let html = std::fs::read_to_string(target).unwrap();
        assert!(html.contains("data:image/png;base64,"));
        assert!(!html.contains("http://"));
        assert!(!html.contains("https://"));
    }

    #[test]
    fn remote_cache_key_strips_fragments_and_rejects_non_web_sources() {
        assert_eq!(
            remote_cache_key("https://example.com/a.png#preview").unwrap(),
            "https://example.com/a.png"
        );
        assert!(remote_cache_key("file:///C:/a.png").is_err());
    }

    #[test]
    fn image_response_cache_evicts_old_entries() {
        let cache = ImageResponseCache::default();
        for index in 0..=MAXIMUM_CACHE_ENTRIES {
            cache.insert(
                format!("https://example.com/{index}.png"),
                PreparedImage {
                    data_uri: "data:image/png;base64,iVBORw0KGgo=".to_owned(),
                    mime_type: "image/png".to_owned(),
                    extension: "png".to_owned(),
                    byte_len: 1,
                },
            );
        }
        let state = cache.inner.lock().unwrap();
        assert_eq!(state.entries.len(), MAXIMUM_CACHE_ENTRIES);
        assert!(!state.entries.contains_key("https://example.com/0.png"));
        assert!(state.entries.contains_key("https://example.com/8.png"));
    }
}

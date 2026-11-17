use std::time::Duration;

use serde::Serialize;
use wimd_core::image::{MAXIMUM_IMAGE_BYTES, detect_image_mime};

const MAXIMUM_RESPONSE_BYTES: usize = 1024 * 1024;
const IMAGE_HOST: &str = "i.ibb.co";

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct HostedImage {
    pub url: String,
    pub delete_url: Option<String>,
}

pub async fn upload(
    mut bytes: Vec<u8>,
    file_name: String,
    api_key: String,
) -> Result<HostedImage, String> {
    if bytes.is_empty() || bytes.len() as u64 > MAXIMUM_IMAGE_BYTES {
        return Err("ImgBB 图片大小必须在 1 字节到 32 MiB 之间。".to_owned());
    }
    let mime = detect_image_mime(&bytes)
        .filter(|mime| *mime != "image/svg+xml")
        .ok_or_else(|| "ImgBB 仅支持 PNG、JPEG、GIF、BMP 和 WebP 图片。".to_owned())?;
    let expected_extension = match mime {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        "image/webp" => "webp",
        _ => return Err("ImgBB 不支持该图片格式。".to_owned()),
    };
    let file_name = sanitize_file_name(&file_name, expected_extension);
    let client = reqwest::Client::builder()
        .timeout(Duration::from_secs(60))
        .redirect(reqwest::redirect::Policy::none())
        .user_agent(format!("WIMD/{}", env!("CARGO_PKG_VERSION")))
        .build()
        .map_err(|_| "无法创建 ImgBB 上传客户端。".to_owned())?;
    let image_part = reqwest::multipart::Part::bytes(bytes.clone())
        .file_name(file_name)
        .mime_str(mime)
        .map_err(|_| "ImgBB 图片类型无效。".to_owned())?;
    let form = reqwest::multipart::Form::new().part("image", image_part);
    let result = async {
        let mut response = client
            .post("https://api.imgbb.com/1/upload")
            .query(&[("key", api_key.trim())])
            .multipart(form)
            .send()
            .await
            .map_err(|_| "ImgBB 上传失败，请检查网络连接或 API Key。".to_owned())?;
        let status = response.status();
        if response.content_length().unwrap_or(0) > MAXIMUM_RESPONSE_BYTES as u64 {
            return Err("ImgBB 返回了异常大的响应。".to_owned());
        }
        let mut body = Vec::new();
        while let Some(chunk) = response
            .chunk()
            .await
            .map_err(|_| "读取 ImgBB 响应失败。".to_owned())?
        {
            if body.len() + chunk.len() > MAXIMUM_RESPONSE_BYTES {
                return Err("ImgBB 返回了异常大的响应。".to_owned());
            }
            body.extend_from_slice(&chunk);
        }
        parse_response(status, &body)
    }
    .await;
    bytes.fill(0);
    result
}

fn parse_response(status: reqwest::StatusCode, bytes: &[u8]) -> Result<HostedImage, String> {
    let json: serde_json::Value =
        serde_json::from_slice(bytes).map_err(|_| "ImgBB 返回了无法解析的响应。".to_owned())?;
    if !status.is_success()
        || json.get("success").and_then(serde_json::Value::as_bool) != Some(true)
    {
        return Err(format!("ImgBB 拒绝了上传（HTTP {}）。", status.as_u16()));
    }
    let data = json
        .get("data")
        .ok_or_else(|| "ImgBB 响应缺少图片信息。".to_owned())?;
    let url = data
        .get("url")
        .and_then(serde_json::Value::as_str)
        .and_then(parse_trusted_image_url)
        .ok_or_else(|| "ImgBB 返回了无效或不受信任的图片地址。".to_owned())?;
    let delete_url = data
        .get("delete_url")
        .and_then(serde_json::Value::as_str)
        .and_then(parse_trusted_delete_url);
    Ok(HostedImage {
        url: url.to_string(),
        delete_url: delete_url.map(|url| url.to_string()),
    })
}

fn parse_trusted_image_url(value: &str) -> Option<reqwest::Url> {
    let url = reqwest::Url::parse(value).ok()?;
    if url.scheme() != "https"
        || url.host_str() != Some(IMAGE_HOST)
        || url.port().is_some()
        || !url.username().is_empty()
        || url.password().is_some()
    {
        return None;
    }
    let extension = std::path::Path::new(url.path())
        .extension()
        .and_then(|extension| extension.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();
    matches!(
        extension.as_str(),
        "png" | "jpg" | "jpeg" | "gif" | "bmp" | "webp"
    )
    .then_some(url)
}

fn parse_trusted_delete_url(value: &str) -> Option<reqwest::Url> {
    let url = reqwest::Url::parse(value).ok()?;
    (url.scheme() == "https"
        && url.host_str() == Some("ibb.co")
        && url.port().is_none()
        && url.username().is_empty()
        && url.password().is_none())
    .then_some(url)
}

fn sanitize_file_name(value: &str, extension: &str) -> String {
    let stem = std::path::Path::new(value)
        .file_stem()
        .and_then(|stem| stem.to_str())
        .unwrap_or("image")
        .chars()
        .filter(|character| character.is_ascii_alphanumeric() || matches!(character, '-' | '_'))
        .take(80)
        .collect::<String>();
    format!(
        "{}.{}",
        if stem.is_empty() { "image" } else { &stem },
        extension
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_only_imgbb_https_image_hosts() {
        assert!(parse_trusted_image_url("https://i.ibb.co/abc/photo.png").is_some());
        assert!(parse_trusted_image_url("http://i.ibb.co/abc/photo.png").is_none());
        assert!(parse_trusted_image_url("https://evil.example/photo.png").is_none());
        assert!(parse_trusted_image_url("https://i.ibb.co/abc/photo.svg").is_none());
    }

    #[test]
    fn file_names_are_reduced_to_safe_ascii_components() {
        assert_eq!(sanitize_file_name("中文 图片.png", "png"), "image.png");
        assert_eq!(sanitize_file_name("../photo-01.png", "png"), "photo-01.png");
    }
}

use std::{
    fs::OpenOptions,
    io::Write,
    time::{Duration, Instant},
};

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

const MAXIMUM_RELEASE_BYTES: usize = 1024 * 1024;
const MAXIMUM_INSTALLER_BYTES: u64 = 256 * 1024 * 1024;
const MAXIMUM_REDIRECTS: usize = 5;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateInfo {
    pub version: String,
    pub tag_name: String,
    pub name: String,
    pub notes: String,
    pub published_at: String,
    pub release_url: String,
    pub asset_name: String,
    pub download_url: String,
    pub size: u64,
    pub sha256_hex: String,
}

pub async fn check_latest() -> Result<Option<UpdateInfo>, String> {
    let client = http_client()?;
    let response = client
        .get("https://api.github.com/repos/wenchenyang874-a11y/who_is_Mark-Down/releases/latest")
        .header("Accept", "application/vnd.github+json")
        .header("X-GitHub-Api-Version", "2022-11-28")
        .send()
        .await
        .map_err(|_| "无法连接 GitHub 检查 WIMD 更新。".to_owned())?;
    if response.status() == reqwest::StatusCode::NOT_FOUND {
        return Ok(None);
    }
    if !response.status().is_success() {
        return Err(format!(
            "GitHub 更新检查失败（HTTP {}）。",
            response.status().as_u16()
        ));
    }
    let bytes = read_bounded_response(response, MAXIMUM_RELEASE_BYTES).await?;
    let info = parse_release(&bytes)?;
    let current = parse_version(env!("CARGO_PKG_VERSION"))?;
    let available = parse_version(&info.version)?;
    Ok((available > current).then_some(info))
}

pub async fn download(
    info: &UpdateInfo,
    destination_directory: &std::path::Path,
    is_cancelled: impl Fn() -> bool,
    mut report_progress: impl FnMut(u64, u64),
) -> Result<std::path::PathBuf, String> {
    validate_asset(info)?;
    if is_cancelled() {
        return Err("更新下载已取消。".to_owned());
    }
    report_progress(0, info.size);
    let client = http_client()?;
    let mut current_url: reqwest::Url =
        reqwest::Url::parse(&info.download_url).map_err(|_| "更新下载地址无效。".to_owned())?;
    let mut response = None;
    for redirect in 0..=MAXIMUM_REDIRECTS {
        validate_download_url(&current_url)?;
        let candidate = client
            .get(current_url.clone())
            .send()
            .await
            .map_err(|_| "无法下载 WIMD 更新安装包。".to_owned())?;
        if candidate.status().is_redirection() {
            let location = candidate
                .headers()
                .get(reqwest::header::LOCATION)
                .and_then(|value| value.to_str().ok())
                .ok_or_else(|| "GitHub 下载重定向缺少目标地址。".to_owned())?;
            current_url = current_url
                .join(location)
                .map_err(|_| "GitHub 下载重定向地址无效。".to_owned())?;
            if redirect == MAXIMUM_REDIRECTS {
                return Err("GitHub 下载重定向次数过多。".to_owned());
            }
            continue;
        }
        if !candidate.status().is_success() {
            return Err(format!(
                "WIMD 安装包下载失败（HTTP {}）。",
                candidate.status().as_u16()
            ));
        }
        response = Some(candidate);
        break;
    }
    let mut response = response.ok_or_else(|| "WIMD 安装包下载没有完成。".to_owned())?;
    if response
        .content_length()
        .is_some_and(|length| length != info.size)
    {
        return Err("安装包下载大小与 Release 元数据不一致。".to_owned());
    }
    std::fs::create_dir_all(destination_directory)
        .map_err(|error| format!("创建更新目录失败：{error}"))?;
    let target = destination_directory.join(&info.asset_name);
    let temporary = destination_directory.join(format!(
        ".{}.{}.download",
        info.asset_name,
        uuid::Uuid::new_v4()
    ));
    let actual = match stream_installer(
        &mut response,
        info,
        &temporary,
        &is_cancelled,
        &mut report_progress,
    )
    .await
    {
        Ok(hash) => hash,
        Err(error) => {
            let _ = std::fs::remove_file(&temporary);
            return Err(error);
        }
    };
    if !constant_time_hex_eq(&actual, &info.sha256_hex) {
        let _ = std::fs::remove_file(&temporary);
        return Err("WIMD 安装包 SHA-256 校验失败。".to_owned());
    }
    report_progress(info.size, info.size);
    if let Err(error) = wimd_core::document::commit_atomic_file(&temporary, &target) {
        let _ = std::fs::remove_file(temporary);
        return Err(error.to_string());
    }
    Ok(target)
}

async fn stream_installer(
    response: &mut reqwest::Response,
    info: &UpdateInfo,
    temporary: &std::path::Path,
    is_cancelled: &impl Fn() -> bool,
    report_progress: &mut impl FnMut(u64, u64),
) -> Result<String, String> {
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(temporary)
        .map_err(|error| format!("创建更新安装包临时文件失败：{error}"))?;
    let mut hasher = Sha256::new();
    let mut downloaded = 0u64;
    let mut last_progress = Instant::now();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|_| "读取 WIMD 安装包失败。".to_owned())?
    {
        if is_cancelled() {
            return Err("更新下载已取消。".to_owned());
        }
        downloaded = downloaded
            .checked_add(chunk.len() as u64)
            .ok_or_else(|| "安装包下载大小溢出。".to_owned())?;
        if downloaded > info.size {
            return Err("安装包下载内容超过 Release 声明大小。".to_owned());
        }
        file.write_all(&chunk)
            .map_err(|error| format!("写入更新安装包失败：{error}"))?;
        hasher.update(&chunk);
        if last_progress.elapsed() >= Duration::from_millis(100) {
            report_progress(downloaded, info.size);
            last_progress = Instant::now();
        }
    }
    if downloaded != info.size {
        return Err("WIMD 安装包下载不完整。".to_owned());
    }
    file.flush()
        .map_err(|error| format!("刷新更新安装包失败：{error}"))?;
    file.sync_all()
        .map_err(|error| format!("同步更新安装包失败：{error}"))?;
    Ok(hasher
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect())
}

pub fn verify_installer(path: &std::path::Path, expected_sha256: &str) -> Result<(), String> {
    let metadata =
        std::fs::metadata(path).map_err(|error| format!("读取更新安装包失败：{error}"))?;
    if !metadata.is_file()
        || metadata.len() > MAXIMUM_INSTALLER_BYTES
        || path.extension().and_then(|value| value.to_str()) != Some("exe")
    {
        return Err("更新安装包路径或类型无效。".to_owned());
    }
    let bytes = std::fs::read(path).map_err(|error| format!("读取更新安装包失败：{error}"))?;
    let actual = hex_sha256(&bytes);
    if !constant_time_hex_eq(&actual, expected_sha256) {
        return Err("更新安装包在下载后发生变化，已拒绝安装。".to_owned());
    }
    Ok(())
}

fn http_client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .timeout(Duration::from_secs(120))
        .redirect(reqwest::redirect::Policy::none())
        .user_agent(format!("WIMD/{}", env!("CARGO_PKG_VERSION")))
        .build()
        .map_err(|_| "无法创建 WIMD 更新客户端。".to_owned())
}

async fn read_bounded_response(
    mut response: reqwest::Response,
    maximum: usize,
) -> Result<Vec<u8>, String> {
    if response.content_length().unwrap_or(0) > maximum as u64 {
        return Err("GitHub Release 响应过大。".to_owned());
    }
    let mut bytes = Vec::new();
    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|_| "读取 GitHub Release 响应失败。".to_owned())?
    {
        if bytes.len() + chunk.len() > maximum {
            return Err("GitHub Release 响应过大。".to_owned());
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(bytes)
}

fn parse_release(bytes: &[u8]) -> Result<UpdateInfo, String> {
    let root: serde_json::Value = serde_json::from_slice(bytes)
        .map_err(|_| "GitHub Release 响应不是有效 JSON。".to_owned())?;
    if root.get("draft").and_then(serde_json::Value::as_bool) == Some(true)
        || root.get("prerelease").and_then(serde_json::Value::as_bool) == Some(true)
    {
        return Err("GitHub 返回的最新版本不是正式 Release。".to_owned());
    }
    let tag_name = required_string(&root, "tag_name")?;
    let version = tag_name
        .strip_prefix('v')
        .filter(|value| parse_version(value).is_ok())
        .ok_or_else(|| "GitHub Release 标签不是有效的三段式版本号。".to_owned())?
        .to_owned();
    let expected_asset_name = format!("WIMD-Setup-v{version}-win-x64.exe");
    let assets = root
        .get("assets")
        .and_then(serde_json::Value::as_array)
        .ok_or_else(|| "GitHub Release 缺少 assets 字段。".to_owned())?;
    let matching: Vec<&serde_json::Value> = assets
        .iter()
        .filter(|asset| {
            asset.get("name").and_then(serde_json::Value::as_str)
                == Some(expected_asset_name.as_str())
        })
        .collect();
    if matching.len() != 1 {
        return Err(format!(
            "Release 必须且只能包含一个预期安装包：{expected_asset_name}"
        ));
    }
    let asset = matching[0];
    let digest = required_string(asset, "digest")?;
    let sha256_hex = digest
        .strip_prefix("sha256:")
        .filter(|hash| hash.len() == 64 && hash.bytes().all(|byte| byte.is_ascii_hexdigit()))
        .ok_or_else(|| "Release 安装包缺少有效的 SHA-256 摘要。".to_owned())?
        .to_owned();
    let size = asset
        .get("size")
        .and_then(serde_json::Value::as_u64)
        .filter(|size| *size > 0 && *size <= MAXIMUM_INSTALLER_BYTES)
        .ok_or_else(|| "Release 安装包大小异常。".to_owned())?;
    let download_url = required_string(asset, "browser_download_url")?;
    let download_uri = reqwest::Url::parse(&download_url)
        .map_err(|_| "Release 安装包下载地址无效。".to_owned())?;
    if download_uri.scheme() != "https" || download_uri.host_str() != Some("github.com") {
        return Err("Release 安装包不是 GitHub 官方下载地址。".to_owned());
    }
    let release_url = required_string(&root, "html_url")?;
    let release_uri =
        reqwest::Url::parse(&release_url).map_err(|_| "Release 页面地址无效。".to_owned())?;
    if release_uri.scheme() != "https" || release_uri.host_str() != Some("github.com") {
        return Err("Release 页面不是 GitHub 官方地址。".to_owned());
    }
    Ok(UpdateInfo {
        version,
        tag_name: tag_name.clone(),
        name: root
            .get("name")
            .and_then(serde_json::Value::as_str)
            .unwrap_or(&tag_name)
            .to_owned(),
        notes: root
            .get("body")
            .and_then(serde_json::Value::as_str)
            .unwrap_or_default()
            .chars()
            .take(20_000)
            .collect(),
        published_at: required_string(&root, "published_at")?,
        release_url,
        asset_name: expected_asset_name,
        download_url,
        size,
        sha256_hex,
    })
}

fn required_string(value: &serde_json::Value, key: &str) -> Result<String, String> {
    value
        .get(key)
        .and_then(serde_json::Value::as_str)
        .filter(|value| !value.trim().is_empty())
        .map(|value| value.trim().to_owned())
        .ok_or_else(|| format!("GitHub Release 缺少字段：{key}"))
}

fn parse_version(value: &str) -> Result<(u64, u64, u64), String> {
    let mut parts = value.split('.');
    let major = parts
        .next()
        .and_then(|part| part.parse::<u64>().ok())
        .ok_or_else(|| "版本号无效。".to_owned())?;
    let minor = parts
        .next()
        .and_then(|part| part.parse::<u64>().ok())
        .ok_or_else(|| "版本号无效。".to_owned())?;
    let patch = parts
        .next()
        .and_then(|part| part.parse::<u64>().ok())
        .ok_or_else(|| "版本号无效。".to_owned())?;
    if parts.next().is_some() {
        return Err("版本号必须是三段式。".to_owned());
    }
    Ok((major, minor, patch))
}

fn validate_asset(info: &UpdateInfo) -> Result<(), String> {
    let version = parse_version(&info.version)?;
    let expected_name = format!(
        "WIMD-Setup-v{}.{}.{}-win-x64.exe",
        version.0, version.1, version.2
    );
    if info.size == 0
        || info.size > MAXIMUM_INSTALLER_BYTES
        || info.sha256_hex.len() != 64
        || !info.sha256_hex.bytes().all(|byte| byte.is_ascii_hexdigit())
        || info.asset_name != expected_name
        || info.tag_name != format!("v{}", info.version)
    {
        return Err("更新安装包元数据无效。".to_owned());
    }
    let url =
        reqwest::Url::parse(&info.download_url).map_err(|_| "更新下载地址无效。".to_owned())?;
    validate_download_url(&url)
}

fn validate_download_url(url: &reqwest::Url) -> Result<(), String> {
    let host = url.host_str().unwrap_or_default();
    if url.scheme() != "https"
        || !(host == "github.com"
            || host == "githubusercontent.com"
            || host.ends_with(".githubusercontent.com"))
    {
        return Err("安装包下载跳转到了非 GitHub HTTPS 地址。".to_owned());
    }
    Ok(())
}

fn hex_sha256(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    digest.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn constant_time_hex_eq(left: &str, right: &str) -> bool {
    if left.len() != right.len() {
        return false;
    }
    let mut difference = 0u8;
    for (left, right) in left.bytes().zip(right.bytes()) {
        difference |= left.to_ascii_lowercase() ^ right.to_ascii_lowercase();
    }
    difference == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn release_parser_rejects_missing_digest() {
        let json = br#"{
          "draft": false,
          "prerelease": false,
          "tag_name": "v2.0.1",
          "name": "WIMD 2.0.1",
          "body": "notes",
          "published_at": "2026-11-16T00:00:00Z",
          "html_url": "https://github.com/wenchenyang874-a11y/who_is_Mark-Down/releases/tag/v2.0.1",
          "assets": [{
            "name": "WIMD-Setup-v2.0.1-win-x64.exe",
            "size": 1,
            "browser_download_url": "https://github.com/wenchenyang874-a11y/who_is_Mark-Down/releases/download/v2.0.1/WIMD-Setup-v2.0.1-win-x64.exe"
          }]
        }"#;
        assert!(parse_release(json).is_err());
    }

    #[test]
    fn release_parser_accepts_one_exact_verified_asset() {
        let json = br#"{
          "draft": false,
          "prerelease": false,
          "tag_name": "v2.0.1",
          "name": "WIMD 2.0.1",
          "body": "notes",
          "published_at": "2026-11-16T00:00:00Z",
          "html_url": "https://github.com/wenchenyang874-a11y/who_is_Mark-Down/releases/tag/v2.0.1",
          "assets": [{
            "name": "WIMD-Setup-v2.0.1-win-x64.exe",
            "size": 10,
            "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            "browser_download_url": "https://github.com/wenchenyang874-a11y/who_is_Mark-Down/releases/download/v2.0.1/WIMD-Setup-v2.0.1-win-x64.exe"
          }]
        }"#;
        let parsed = parse_release(json).unwrap();
        assert_eq!(parsed.version, "2.0.1");
        assert_eq!(parsed.asset_name, "WIMD-Setup-v2.0.1-win-x64.exe");
    }

    #[test]
    fn download_urls_must_use_trusted_https_hosts() {
        assert!(
            validate_download_url(&reqwest::Url::parse("https://github.com/a/b").unwrap()).is_ok()
        );
        assert!(
            validate_download_url(&reqwest::Url::parse("http://github.com/a/b").unwrap()).is_err()
        );
        assert!(
            validate_download_url(&reqwest::Url::parse("https://evil.example/a").unwrap()).is_err()
        );
    }
}

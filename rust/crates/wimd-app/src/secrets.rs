use std::{ptr, slice};

use base64::{Engine as _, engine::general_purpose::STANDARD};
use windows_sys::Win32::{
    Foundation::LocalFree,
    Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
    },
};

const OPTIONAL_ENTROPY: &[u8] = b"WIMD/ImageHosting/ImgBB/v1";

pub fn protect_secret(plaintext: &str) -> Result<String, String> {
    if plaintext.trim().is_empty() {
        return Err("API Key 不能为空。".to_owned());
    }
    let mut clear = plaintext.trim().as_bytes().to_vec();
    let mut entropy = OPTIONAL_ENTROPY.to_vec();
    let input = CRYPT_INTEGER_BLOB {
        cbData: clear.len() as u32,
        pbData: clear.as_mut_ptr(),
    };
    let entropy_blob = CRYPT_INTEGER_BLOB {
        cbData: entropy.len() as u32,
        pbData: entropy.as_mut_ptr(),
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    let succeeded = unsafe {
        CryptProtectData(
            &input,
            ptr::null(),
            &entropy_blob,
            ptr::null(),
            ptr::null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    clear.fill(0);
    entropy.fill(0);
    if succeeded == 0 {
        return Err(format!(
            "无法使用当前 Windows 账户保护 ImgBB API Key：{}",
            std::io::Error::last_os_error()
        ));
    }
    let bytes = unsafe { slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        LocalFree(output.pbData.cast());
    }
    Ok(STANDARD.encode(bytes))
}

pub fn unprotect_secret(protected: &str) -> Result<String, String> {
    if protected.trim().is_empty() {
        return Err("尚未保存 ImgBB API Key。".to_owned());
    }
    let mut encrypted = STANDARD
        .decode(protected.trim())
        .map_err(|_| "已保存的 ImgBB API Key 格式损坏，请重新设置。".to_owned())?;
    let mut entropy = OPTIONAL_ENTROPY.to_vec();
    let input = CRYPT_INTEGER_BLOB {
        cbData: encrypted.len() as u32,
        pbData: encrypted.as_mut_ptr(),
    };
    let entropy_blob = CRYPT_INTEGER_BLOB {
        cbData: entropy.len() as u32,
        pbData: entropy.as_mut_ptr(),
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    let succeeded = unsafe {
        CryptUnprotectData(
            &input,
            ptr::null_mut(),
            &entropy_blob,
            ptr::null(),
            ptr::null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    encrypted.fill(0);
    entropy.fill(0);
    if succeeded == 0 {
        return Err(format!(
            "当前 Windows 账户无法解密 ImgBB API Key：{}",
            std::io::Error::last_os_error()
        ));
    }
    let bytes = unsafe { slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        LocalFree(output.pbData.cast());
    }
    let mut bytes = bytes;
    let text = String::from_utf8(bytes.clone())
        .map_err(|_| "ImgBB API Key 解密结果不是有效 UTF-8。".to_owned());
    bytes.fill(0);
    text
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dpapi_round_trip_uses_current_windows_user() {
        let protected = protect_secret("test-imgbb-key").unwrap();
        assert_ne!(protected, "test-imgbb-key");
        assert_eq!(unprotect_secret(&protected).unwrap(), "test-imgbb-key");
    }

    #[test]
    fn malformed_ciphertext_is_rejected() {
        assert!(unprotect_secret("not-base64!").is_err());
    }
}

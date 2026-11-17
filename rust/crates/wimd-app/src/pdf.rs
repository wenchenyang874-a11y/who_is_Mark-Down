use std::{
    path::{Path, PathBuf},
    sync::mpsc,
    time::Duration,
};

use serde::Serialize;
use tauri::WebviewWindow;
use webview2_com::{
    Microsoft::Web::WebView2::Win32::{ICoreWebView2_16, ICoreWebView2Environment6},
    PrintToPdfStreamCompletedHandler,
};
use windows::core::Interface;

const MAXIMUM_PDF_BYTES: usize = 256 * 1024 * 1024;
const MAXIMUM_PDF_HTML_BYTES: usize = 128 * 1024 * 1024;
const MAXIMUM_PDF_CSS_BYTES: usize = 4 * 1024 * 1024;
const MAXIMUM_PDF_INLINE_STYLE_BYTES: usize = 64 * 1024;
const PRINT_TIMEOUT: Duration = Duration::from_secs(45);

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PdfDocumentPayload {
    pub html: String,
    pub css: String,
    pub theme: String,
    pub inline_style: String,
}

pub fn validate_document_payload(payload: &PdfDocumentPayload) -> Result<(), String> {
    if payload.html.len() > MAXIMUM_PDF_HTML_BYTES {
        return Err("PDF 内容超过 128 MiB 上限。".to_owned());
    }
    if payload.css.len() > MAXIMUM_PDF_CSS_BYTES {
        return Err("PDF 样式超过 4 MiB 上限。".to_owned());
    }
    if payload.inline_style.len() > MAXIMUM_PDF_INLINE_STYLE_BYTES {
        return Err("PDF 内联样式超过 64 KiB 上限。".to_owned());
    }
    Ok(())
}

pub fn initialization_script(payload: &PdfDocumentPayload) -> Result<String, String> {
    validate_document_payload(payload)?;
    let payload =
        serde_json::to_string(payload).map_err(|error| format!("序列化 PDF 内容失败：{error}"))?;
    Ok(format!("window.__WIMD_PDF_PAYLOAD__ = {payload};"))
}

pub async fn export_window_pdf(
    window: WebviewWindow,
    target_path: PathBuf,
) -> Result<PathBuf, String> {
    let target_path =
        wimd_core::document::normalize_path(&target_path).map_err(|error| error.to_string())?;
    let (sender, receiver) = mpsc::channel::<Result<Vec<u8>, String>>();
    let callback_sender = sender.clone();
    let setup_sender = sender;

    window
        .with_webview(move |webview| {
            #[cfg(windows)]
            unsafe {
                let result = (|| -> Result<(), String> {
                    let environment = webview
                        .environment()
                        .cast::<ICoreWebView2Environment6>()
                        .map_err(|error| format!("WebView2 打印环境不可用：{error}"))?;
                    let settings = environment
                        .CreatePrintSettings()
                        .map_err(|error| format!("创建 PDF 打印设置失败：{error}"))?;
                    settings
                        .SetShouldPrintBackgrounds(true)
                        .map_err(|error| format!("设置 PDF 背景打印失败：{error}"))?;
                    settings
                        .SetPageWidth(8.27)
                        .and_then(|_| settings.SetPageHeight(11.69))
                        .and_then(|_| settings.SetMarginTop(0.28))
                        .and_then(|_| settings.SetMarginBottom(0.28))
                        .and_then(|_| settings.SetMarginLeft(0.34))
                        .and_then(|_| settings.SetMarginRight(0.34))
                        .map_err(|error| format!("设置 PDF 页面尺寸失败：{error}"))?;

                    let controller = webview.controller();
                    let core = controller
                        .CoreWebView2()
                        .map_err(|error| format!("获取 WebView2 内容失败：{error}"))?;
                    let print = core
                        .cast::<ICoreWebView2_16>()
                        .map_err(|error| format!("当前 WebView2 不支持 PDF 导出：{error}"))?;
                    let handler = PrintToPdfStreamCompletedHandler::create(Box::new(
                        move |print_result, stream| {
                            let bytes = (|| -> Result<Vec<u8>, String> {
                                print_result
                                    .map_err(|error| format!("WebView2 PDF 生成失败：{error}"))?;
                                let stream = stream
                                    .ok_or_else(|| "WebView2 未返回 PDF 数据流。".to_owned())?;
                                read_stream(&stream)
                            })();
                            let _ = callback_sender.send(bytes);
                            Ok(())
                        },
                    ));
                    print
                        .PrintToPdfStream(&settings, &handler)
                        .map_err(|error| format!("启动 WebView2 PDF 导出失败：{error}"))
                })();
                if let Err(error) = result {
                    let _ = setup_sender.send(Err(error));
                }
            }

            #[cfg(not(windows))]
            {
                let _ = setup_sender.send(Err("PDF 导出当前只支持 Windows。".to_owned()));
            }
        })
        .map_err(|error| format!("提交 WebView2 PDF 导出失败：{error}"))?;

    let bytes = tauri::async_runtime::spawn_blocking(move || {
        receiver
            .recv_timeout(PRINT_TIMEOUT)
            .map_err(|_| "等待 WebView2 PDF 生成超时。".to_owned())?
    })
    .await
    .map_err(|error| format!("等待 PDF 导出任务失败：{error}"))??;

    if bytes.len() > MAXIMUM_PDF_BYTES {
        return Err("生成的 PDF 超过 256 MiB 上限。".to_owned());
    }
    if !bytes.starts_with(b"%PDF-") {
        return Err("WebView2 返回的数据不是 PDF。".to_owned());
    }
    wimd_core::document::write_bytes_atomic(&target_path, &bytes)
        .map_err(|error| error.to_string())?;
    Ok(target_path)
}

#[cfg(windows)]
fn read_stream(stream: &windows::Win32::System::Com::IStream) -> Result<Vec<u8>, String> {
    let mut output = Vec::new();
    let mut buffer = vec![0u8; 64 * 1024];
    loop {
        let mut bytes_read = 0u32;
        let result = unsafe {
            stream.Read(
                buffer.as_mut_ptr().cast(),
                buffer.len() as u32,
                Some(&mut bytes_read),
            )
        };
        result
            .ok()
            .map_err(|error| format!("读取 PDF 数据流失败：{error}"))?;
        if bytes_read == 0 {
            break;
        }
        output.extend_from_slice(&buffer[..bytes_read as usize]);
        if output.len() > MAXIMUM_PDF_BYTES {
            return Err("PDF 数据流超过 256 MiB 上限。".to_owned());
        }
    }
    Ok(output)
}

pub fn ensure_pdf_extension(path: &Path) -> PathBuf {
    if path
        .extension()
        .and_then(|extension| extension.to_str())
        .is_some_and(|extension| extension.eq_ignore_ascii_case("pdf"))
    {
        path.to_path_buf()
    } else {
        path.with_extension("pdf")
    }
}

use std::path::PathBuf;

use thiserror::Error;

pub type CoreResult<T> = Result<T, CoreError>;

#[derive(Debug, Error)]
pub enum CoreError {
    #[error("{operation}失败：{path}：{source}")]
    Io {
        operation: &'static str,
        path: PathBuf,
        #[source]
        source: std::io::Error,
    },

    #[error("文档不是有效的 UTF-8 编码：{path}")]
    InvalidUtf8 { path: PathBuf },

    #[error("文件超过允许的 {maximum_bytes} 字节：{path}")]
    FileTooLarge { path: PathBuf, maximum_bytes: u64 },

    #[error("路径不在允许的目录范围内：{path}")]
    OutsideRoot { path: PathBuf },

    #[error("路径不存在：{path}")]
    MissingPath { path: PathBuf },

    #[error("不支持的文件类型：{path}")]
    UnsupportedFileType { path: PathBuf },

    #[error("名称无效：{0}")]
    InvalidName(String),

    #[error("选区无效：{0}")]
    InvalidSelection(String),

    #[error("当前行不是可切换的 Markdown 任务：{line}")]
    InvalidTaskLine { line: usize },

    #[error("Markdown 格式不受支持：{0}")]
    UnsupportedFormat(String),

    #[error("图片格式不受支持或与本机扩展名不一致：{path}")]
    UnsupportedImage { path: PathBuf },

    #[error("设置文件损坏，已回退到默认值：{path}")]
    InvalidSettings { path: PathBuf },

    #[error("内部错误：{0}")]
    Internal(String),
}

impl CoreError {
    pub fn io(operation: &'static str, path: impl Into<PathBuf>, source: std::io::Error) -> Self {
        Self::Io {
            operation,
            path: path.into(),
            source,
        }
    }
}

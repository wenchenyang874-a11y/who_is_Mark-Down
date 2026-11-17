//! WIMD's platform-independent application core.
//!
//! The core deliberately has no Tauri, WebView2, or UI dependency. Document
//! persistence, Markdown safety, workspace boundaries, and settings can therefore
//! be tested without constructing a desktop application.

pub mod document;
pub mod error;
pub mod formatting;
pub mod image;
pub mod markdown;
pub mod settings;
pub mod shortcut;
pub mod task;
pub mod workspace;

pub use error::{CoreError, CoreResult};

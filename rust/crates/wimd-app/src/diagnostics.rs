use std::{
    collections::HashSet,
    fs::{self, OpenOptions},
    io::{BufWriter, Write},
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicU64, Ordering},
        mpsc::{self, RecvTimeoutError, SyncSender, TrySendError},
    },
    thread,
    time::{Duration, Instant},
};

use chrono::Utc;
use serde::Serialize;
use windows_sys::Win32::System::Threading::GetCurrentThreadId;

const QUEUE_CAPACITY: usize = 2048;
const MAXIMUM_FILE_BYTES: u64 = 2 * 1024 * 1024;
const STARTUP_CAPTURE: Duration = Duration::from_secs(120);
const STARTUP_DETAIL_LIMIT: u64 = 2 * 1024 * 1024;
const STARTUP_SUMMARY_LIMIT: u64 = 128 * 1024;
const STARTUP_DETAIL_FILES: usize = 60;
const STARTUP_SUMMARY_FILES: usize = 200;

#[derive(Clone, Debug)]
pub struct DiagnosticLine {
    pub stage: &'static str,
    pub outcome: &'static str,
    pub duration_ms: Option<f64>,
    pub value: Option<i64>,
    pub error_type: Option<&'static str>,
    queued_at: Instant,
}

#[derive(Debug, Serialize)]
struct DiagnosticRecord {
    session_id: String,
    utc: String,
    elapsed_ms: f64,
    stage: String,
    outcome: String,
    duration_ms: Option<f64>,
    value: Option<i64>,
    error_type: Option<String>,
    managed_thread: u32,
    writer_queue_delay_ms: Option<f64>,
}

struct StartupArchive {
    detail: Option<BufWriter<std::fs::File>>,
    summary: Option<BufWriter<std::fs::File>>,
    summary_keys: HashSet<String>,
    detail_bytes: u64,
    summary_bytes: u64,
    skipped_detail: u64,
    skipped_summary: u64,
    deduplicated_summary: u64,
    finished: bool,
}

impl StartupArchive {
    fn open(directory: &Path, session_id: &str) -> Self {
        let detail_directory = directory.join("Startup");
        let summary_directory = detail_directory.join("Summaries");
        let _ = fs::create_dir_all(&summary_directory);
        trim_startup_files(&detail_directory, "startup-", STARTUP_DETAIL_FILES);
        trim_startup_files(&summary_directory, "startup-", STARTUP_SUMMARY_FILES);
        let name = format!(
            "startup-{}-{}-{session_id}.jsonl",
            Utc::now().format("%Y%m%dT%H%M%S%.3f"),
            std::process::id()
        );
        let detail = OpenOptions::new()
            .create_new(true)
            .append(true)
            .open(detail_directory.join(&name))
            .ok()
            .map(BufWriter::new);
        let summary = OpenOptions::new()
            .create_new(true)
            .append(true)
            .open(summary_directory.join(name))
            .ok()
            .map(BufWriter::new);
        Self {
            detail,
            summary,
            summary_keys: HashSet::new(),
            detail_bytes: 0,
            summary_bytes: 0,
            skipped_detail: 0,
            skipped_summary: 0,
            deduplicated_summary: 0,
            finished: false,
        }
    }

    fn append(&mut self, payload: &[u8], stage: &str, outcome: &str) {
        if self.finished {
            return;
        }
        if self.detail_bytes + payload.len() as u64 <= STARTUP_DETAIL_LIMIT - 1024 {
            if let Some(detail) = self.detail.as_mut()
                && detail.write_all(payload).is_ok()
            {
                self.detail_bytes += payload.len() as u64;
            } else {
                self.skipped_detail += 1;
            }
        } else {
            self.skipped_detail += 1;
            if self.skipped_detail == 1
                && let Some(detail) = self.detail.as_mut()
            {
                let marker =
                    b"{\"stage\":\"StartupArchiveDetailLimit\",\"detail_truncated\":true}\n";
                let _ = detail.write_all(marker);
            }
        }
        if !is_startup_summary_stage(stage) {
            return;
        }
        let key = format!("{stage}:{outcome}");
        if !self.summary_keys.insert(key) {
            self.deduplicated_summary += 1;
            return;
        }
        if self.summary_keys.len() <= 256
            && self.summary_bytes + payload.len() as u64 <= STARTUP_SUMMARY_LIMIT - 1024
        {
            if let Some(summary) = self.summary.as_mut()
                && summary.write_all(payload).is_ok()
            {
                self.summary_bytes += payload.len() as u64;
            } else {
                self.skipped_summary += 1;
            }
        } else {
            self.skipped_summary += 1;
        }
    }

    fn flush(&mut self) {
        if let Some(detail) = self.detail.as_mut() {
            let _ = detail.flush();
        }
        if let Some(summary) = self.summary.as_mut() {
            let _ = summary.flush();
        }
    }

    fn tick(&mut self, elapsed: Duration) {
        if elapsed >= STARTUP_CAPTURE {
            self.finish("CaptureDeadline");
        }
    }

    fn finish(&mut self, reason: &str) {
        if self.finished {
            return;
        }
        self.finished = true;
        let footer = serde_json::to_vec(&serde_json::json!({
            "stage": "StartupArchiveEnd",
            "reason": reason,
            "capture_deadline_ms": STARTUP_CAPTURE.as_millis() as u64,
            "skipped_detail_records": self.skipped_detail,
            "skipped_summary_records": self.skipped_summary,
            "deduplicated_summary_records": self.deduplicated_summary,
            "detail_limit_bytes": STARTUP_DETAIL_LIMIT,
            "summary_limit_bytes": STARTUP_SUMMARY_LIMIT,
        }))
        .unwrap_or_default();
        let mut footer = footer;
        footer.push(b'\n');
        if let Some(mut detail) = self.detail.take() {
            let _ = detail.write_all(&footer);
            let _ = detail.flush();
        }
        if let Some(mut summary) = self.summary.take() {
            let _ = summary.write_all(&footer);
            let _ = summary.flush();
        }
    }
}

#[derive(Clone)]
pub struct DiagnosticLog {
    sender: SyncSender<DiagnosticLine>,
    logs_directory: PathBuf,
    started: Instant,
    overflow_count: Arc<AtomicU64>,
}

impl DiagnosticLog {
    pub fn new(logs_directory: impl Into<PathBuf>) -> Self {
        let logs_directory = logs_directory.into();
        let (sender, receiver) = mpsc::sync_channel(QUEUE_CAPACITY);
        let writer_directory = logs_directory.clone();
        let overflow_count = Arc::new(AtomicU64::new(0));
        let writer_overflow = Arc::clone(&overflow_count);
        let started = Instant::now();
        let session_id = uuid::Uuid::new_v4().simple().to_string()[..16].to_owned();
        let writer_session = session_id.clone();
        thread::Builder::new()
            .name("WIMD diagnostic writer".to_owned())
            .spawn(move || {
                writer_loop(
                    writer_directory,
                    receiver,
                    writer_overflow,
                    started,
                    writer_session,
                )
            })
            .ok();
        Self {
            sender,
            logs_directory,
            started,
            overflow_count,
        }
    }

    pub fn logs_directory(&self) -> &Path {
        &self.logs_directory
    }

    pub fn record(&self, stage: &'static str, outcome: &'static str) {
        self.record_with(stage, outcome, None, None, None);
    }

    pub fn record_with(
        &self,
        stage: &'static str,
        outcome: &'static str,
        duration_ms: Option<f64>,
        value: Option<i64>,
        error_type: Option<&'static str>,
    ) {
        let line = DiagnosticLine {
            stage,
            outcome,
            duration_ms,
            value,
            error_type,
            queued_at: Instant::now(),
        };
        match self.sender.try_send(line) {
            Ok(()) => {}
            Err(TrySendError::Full(_)) => {
                self.overflow_count.fetch_add(1, Ordering::Relaxed);
            }
            Err(TrySendError::Disconnected(_)) => {}
        }
    }

    pub fn elapsed_ms(&self) -> f64 {
        self.started.elapsed().as_secs_f64() * 1000.0
    }
}

fn writer_loop(
    directory: PathBuf,
    receiver: mpsc::Receiver<DiagnosticLine>,
    overflow_count: Arc<AtomicU64>,
    process_started: Instant,
    session_id: String,
) {
    let _ = fs::create_dir_all(&directory);
    trim_old_logs(&directory);
    let mut segment = 0u32;
    let Some(mut output) = open_segment(&directory, segment, &session_id) else {
        return;
    };
    let mut startup_archive = StartupArchive::open(&directory, &session_id);
    let mut reported_overflow = 0u64;
    let mut last_flush = Instant::now();
    loop {
        let received = receiver.recv_timeout(Duration::from_millis(500));
        let disconnected = matches!(received, Err(RecvTimeoutError::Disconnected));
        if let Ok(line) = received {
            let Ok(payload) = write_line(&mut output, &line, process_started, &session_id) else {
                break;
            };
            startup_archive.append(&payload, line.stage, line.outcome);
        }
        let overflow = overflow_count.load(Ordering::Relaxed);
        if overflow > reported_overflow {
            let skipped = overflow - reported_overflow;
            reported_overflow = overflow;
            let line = DiagnosticLine {
                stage: "LogOverflow",
                outcome: "Point",
                duration_ms: None,
                value: i64::try_from(skipped).ok(),
                error_type: None,
                queued_at: Instant::now(),
            };
            let Ok(payload) = write_line(&mut output, &line, process_started, &session_id) else {
                break;
            };
            startup_archive.append(&payload, line.stage, line.outcome);
        }
        if last_flush.elapsed() >= Duration::from_millis(500) {
            let _ = output.flush();
            startup_archive.flush();
            last_flush = Instant::now();
        }
        startup_archive.tick(process_started.elapsed());
        if disconnected {
            break;
        }
        if output
            .get_ref()
            .metadata()
            .is_ok_and(|metadata| metadata.len() >= MAXIMUM_FILE_BYTES)
        {
            let _ = output.flush();
            segment = segment.saturating_add(1);
            let Some(next) = open_segment(&directory, segment, &session_id) else {
                break;
            };
            output = next;
            trim_old_logs(&directory);
        }
    }
    let _ = output.flush();
    startup_archive.finish("LoggerStopped");
}

fn write_line(
    output: &mut BufWriter<std::fs::File>,
    line: &DiagnosticLine,
    process_started: Instant,
    session_id: &str,
) -> std::io::Result<Vec<u8>> {
    let record = DiagnosticRecord {
        session_id: session_id.to_owned(),
        utc: Utc::now().to_rfc3339(),
        elapsed_ms: process_started.elapsed().as_secs_f64() * 1000.0,
        stage: line.stage.to_owned(),
        outcome: line.outcome.to_owned(),
        duration_ms: line.duration_ms,
        value: line.value,
        error_type: line.error_type.map(str::to_owned),
        managed_thread: unsafe { GetCurrentThreadId() },
        writer_queue_delay_ms: Some(line.queued_at.elapsed().as_secs_f64() * 1000.0),
    };
    let mut payload = serde_json::to_vec(&record).map_err(std::io::Error::other)?;
    payload.push(b'\n');
    output.write_all(&payload)?;
    Ok(payload)
}

fn open_segment(
    directory: &Path,
    segment: u32,
    session_id: &str,
) -> Option<BufWriter<std::fs::File>> {
    let path = directory.join(format!(
        "wimd-{}-{}-{session_id}-{segment:04}.jsonl",
        Utc::now().format("%Y%m%dT%H%M%S%.3f"),
        std::process::id()
    ));
    OpenOptions::new()
        .create_new(true)
        .append(true)
        .open(path)
        .ok()
        .map(BufWriter::new)
}

fn trim_old_logs(directory: &Path) {
    let Ok(entries) = fs::read_dir(directory) else {
        return;
    };
    let mut files = entries
        .flatten()
        .filter(|entry| {
            entry.file_name().to_string_lossy().starts_with("wimd-")
                && entry
                    .path()
                    .extension()
                    .and_then(|extension| extension.to_str())
                    == Some("jsonl")
        })
        .filter_map(|entry| {
            let metadata = entry.metadata().ok()?;
            Some((entry.path(), metadata))
        })
        .collect::<Vec<_>>();
    files.sort_by_key(|(_, metadata)| metadata.modified().ok());
    files.reverse();
    for (path, _) in files.into_iter().skip(10) {
        let _ = fs::remove_file(path);
    }
}

fn trim_startup_files(directory: &Path, prefix: &str, keep: usize) {
    let Ok(entries) = fs::read_dir(directory) else {
        return;
    };
    let mut files = entries
        .flatten()
        .filter(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            name.starts_with(prefix)
                && name.ends_with(".jsonl")
                && entry
                    .path()
                    .extension()
                    .and_then(|extension| extension.to_str())
                    == Some("jsonl")
        })
        .filter_map(|entry| {
            let metadata = entry.metadata().ok()?;
            Some((entry.path(), metadata))
        })
        .collect::<Vec<_>>();
    files.sort_by_key(|(_, metadata)| std::cmp::Reverse(metadata.modified().ok()));
    for (path, _) in files.into_iter().skip(keep) {
        let _ = fs::remove_file(path);
    }
}

fn is_startup_summary_stage(stage: &str) -> bool {
    stage.starts_with("Application")
        || stage.starts_with("Startup")
        || stage.starts_with("WebView")
        || stage.starts_with("Document")
        || stage.starts_with("Settings")
        || stage.starts_with("Workspace")
        || stage.starts_with("Markdown")
        || stage.starts_with("Image")
        || stage.starts_with("UiHeartbeat")
        || matches!(
            stage,
            "PreviewInitialize"
                | "PreviewRendererPrepare"
                | "DiagnosticCoverage"
                | "LogOverflow"
                | "FrontendUnhandled"
                | "ProcessPanic"
        )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn startup_archive_keeps_detail_and_deduplicated_summary() {
        let directory = tempfile::tempdir().unwrap();
        let mut archive = StartupArchive::open(directory.path(), "1234567890abcdef");
        let payload = br#"{"stage":"DocumentOpen","outcome":"Started"}"#;
        archive.append(payload, "DocumentOpen", "Started");
        archive.append(payload, "DocumentOpen", "Started");
        archive.finish("TestFinished");

        let startup = directory.path().join("Startup");
        let summaries = startup.join("Summaries");
        let detail = fs::read_dir(&startup)
            .unwrap()
            .flatten()
            .filter_map(|entry| fs::read_to_string(entry.path()).ok())
            .collect::<Vec<_>>()
            .join("\n");
        let summary = fs::read_dir(summaries)
            .unwrap()
            .flatten()
            .filter_map(|entry| fs::read_to_string(entry.path()).ok())
            .collect::<Vec<_>>()
            .join("\n");

        assert_eq!(detail.matches("DocumentOpen").count(), 2);
        assert_eq!(summary.matches("DocumentOpen").count(), 1);
        assert!(detail.contains("StartupArchiveEnd"));
        assert!(summary.contains("\"deduplicated_summary_records\":1"));
    }
}

use std::{
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

use crate::diagnostics::DiagnosticLog;

#[cfg(not(test))]
const CHECK_INTERVAL: Duration = Duration::from_millis(500);
#[cfg(test)]
const CHECK_INTERVAL: Duration = Duration::from_millis(25);
#[cfg(not(test))]
const UNRESPONSIVE_AFTER_MS: u64 = 2_000;
#[cfg(test)]
const UNRESPONSIVE_AFTER_MS: u64 = 100;
#[cfg(not(test))]
const RECOVERY_BELOW_MS: u64 = 1_500;
#[cfg(test)]
const RECOVERY_BELOW_MS: u64 = 75;

/// Watches a lightweight WebView heartbeat on an independent native thread.
/// The monitor never holds a window, document, settings object, or UI callback.
pub struct UiHeartbeatMonitor {
    started: Instant,
    last_beat_ms: Arc<AtomicU64>,
    stopped: Arc<AtomicBool>,
}

impl UiHeartbeatMonitor {
    pub fn new(diagnostics: DiagnosticLog) -> Self {
        let started = Instant::now();
        let last_beat_ms = Arc::new(AtomicU64::new(0));
        let stopped = Arc::new(AtomicBool::new(false));
        let unresponsive = Arc::new(AtomicBool::new(false));
        let worker_started = started;
        let worker_beat = Arc::clone(&last_beat_ms);
        let worker_stopped = Arc::clone(&stopped);
        let worker_unresponsive = Arc::clone(&unresponsive);
        let _ = thread::Builder::new()
            .name("WIMD UI heartbeat observer".to_owned())
            .spawn(move || {
                while !worker_stopped.load(Ordering::Relaxed) {
                    thread::sleep(CHECK_INTERVAL);
                    if worker_stopped.load(Ordering::Relaxed) {
                        break;
                    }
                    let elapsed = worker_started.elapsed().as_millis() as u64;
                    let gap = elapsed.saturating_sub(worker_beat.load(Ordering::Relaxed));
                    if gap >= UNRESPONSIVE_AFTER_MS
                        && !worker_unresponsive.swap(true, Ordering::AcqRel)
                    {
                        diagnostics.record_with(
                            "UiHeartbeat",
                            "Unresponsive",
                            Some(gap as f64),
                            i64::try_from(gap).ok(),
                            Some("UiThreadBlocked"),
                        );
                    } else if gap < RECOVERY_BELOW_MS
                        && worker_unresponsive.swap(false, Ordering::AcqRel)
                    {
                        diagnostics.record_with(
                            "UiHeartbeat",
                            "Recovered",
                            Some(gap as f64),
                            i64::try_from(gap).ok(),
                            None,
                        );
                    }
                }
            });
        Self {
            started,
            last_beat_ms,
            stopped,
        }
    }

    pub fn beat(&self) {
        self.last_beat_ms
            .store(self.started.elapsed().as_millis() as u64, Ordering::Relaxed);
    }
}

impl Drop for UiHeartbeatMonitor {
    fn drop(&mut self) {
        self.stopped.store(true, Ordering::Release);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs, time::Instant};

    #[test]
    fn stalled_heartbeat_is_written_before_ui_recovery() {
        let directory = tempfile::tempdir().unwrap();
        let log = DiagnosticLog::new(directory.path().to_path_buf());
        let monitor = UiHeartbeatMonitor::new(log.clone());
        thread::sleep(Duration::from_millis(220));
        let deadline = Instant::now() + Duration::from_secs(2);
        let mut content = String::new();
        while Instant::now() < deadline {
            content = fs::read_dir(directory.path())
                .unwrap()
                .flatten()
                .filter_map(|entry| fs::read_to_string(entry.path()).ok())
                .collect::<Vec<_>>()
                .join("\n");
            if content.contains("\"stage\":\"UiHeartbeat\"")
                && content.contains("\"outcome\":\"Unresponsive\"")
            {
                break;
            }
            thread::sleep(Duration::from_millis(50));
        }
        assert!(content.contains("\"stage\":\"UiHeartbeat\""));
        assert!(content.contains("\"outcome\":\"Unresponsive\""));
        monitor.beat();
        drop(monitor);
        drop(log);
    }
}

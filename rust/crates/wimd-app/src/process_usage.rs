use serde::Serialize;
use windows_sys::Win32::{
    Foundation::FILETIME,
    System::{
        ProcessStatus::{K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS},
        Threading::{GetCurrentProcess, GetProcessTimes},
    },
};

#[derive(Clone, Copy, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessSample {
    pub processor_time_100ns: u64,
    pub working_set_bytes: u64,
    pub logical_processors: u32,
}

pub fn sample_current_process() -> Result<ProcessSample, String> {
    let process = unsafe { GetCurrentProcess() };
    let mut creation = FILETIME::default();
    let mut exit = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    let succeeded =
        unsafe { GetProcessTimes(process, &mut creation, &mut exit, &mut kernel, &mut user) };
    if succeeded == 0 {
        return Err(format!(
            "读取当前进程 CPU 时间失败：{}",
            std::io::Error::last_os_error()
        ));
    }
    let mut counters = PROCESS_MEMORY_COUNTERS {
        cb: std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
        ..Default::default()
    };
    let succeeded = unsafe {
        K32GetProcessMemoryInfo(
            process,
            &mut counters,
            std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
        )
    };
    if succeeded == 0 {
        return Err(format!(
            "读取当前进程工作集失败：{}",
            std::io::Error::last_os_error()
        ));
    }
    Ok(ProcessSample {
        processor_time_100ns: filetime_value(kernel) + filetime_value(user),
        working_set_bytes: counters.WorkingSetSize as u64,
        logical_processors: std::thread::available_parallelism()
            .map(|count| count.get() as u32)
            .unwrap_or(1),
    })
}

#[cfg(test)]
fn calculate_cpu_percentage(
    previous_processor_time_100ns: u64,
    current_processor_time_100ns: u64,
    elapsed_milliseconds: f64,
    logical_processors: u32,
) -> f64 {
    if elapsed_milliseconds <= 0.0
        || logical_processors == 0
        || current_processor_time_100ns <= previous_processor_time_100ns
    {
        return 0.0;
    }
    let processor_delta_ms =
        (current_processor_time_100ns - previous_processor_time_100ns) as f64 / 10_000.0;
    (processor_delta_ms / elapsed_milliseconds / logical_processors as f64 * 100.0)
        .clamp(0.0, 100.0)
}

fn filetime_value(value: FILETIME) -> u64 {
    (u64::from(value.dwHighDateTime) << 32) | u64::from(value.dwLowDateTime)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cpu_percentage_is_normalized_and_clamped() {
        assert_eq!(calculate_cpu_percentage(0, 10_000_000, 1_000.0, 4), 25.0);
        assert_eq!(calculate_cpu_percentage(100, 50, 1_000.0, 4), 0.0);
        assert_eq!(calculate_cpu_percentage(0, 100_000_000, 1_000.0, 1), 100.0);
    }
}

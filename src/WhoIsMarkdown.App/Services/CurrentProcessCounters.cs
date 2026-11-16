using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Bug fix: Process.Refresh/WorkingSet64 can obtain a system process snapshot
/// on the dispatcher, including during window construction. Query only our own
/// process with lightweight native counters instead. The pseudo handle is not
/// owned and must not be closed; no Process wrapper or background task is kept.
/// These values still exclude Chromium child processes and are not per-window.
/// </summary>
internal static class CurrentProcessCounters
{
    internal static (TimeSpan ProcessorTime, long WorkingSetBytes) Read()
    {
        nint current = new(-1);
        if (!GetProcessTimes(current, out _, out _, out long kernel, out long user))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        MemoryCounters memory = new() { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        if (!K32GetProcessMemoryInfo(current, ref memory, memory.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (TimeSpan.FromTicks(checked(kernel + user)), checked((long)memory.WorkingSet));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);

    // PROCESS_MEMORY_COUNTERS: DWORDs followed by SIZE_Ts (pointer-sized on x64).
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSet;
        internal nuint WorkingSet;
        internal nuint QuotaPeakPagedPool;
        internal nuint QuotaPagedPool;
        internal nuint QuotaPeakNonPagedPool;
        internal nuint QuotaNonPagedPool;
        internal nuint PageFile;
        internal nuint PeakPageFile;
    }
}

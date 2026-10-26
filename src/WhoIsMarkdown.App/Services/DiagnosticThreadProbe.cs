using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WhoIsMarkdown.App.Services;

// Bug fix: wall time alone cannot distinguish CPU work from a blocked startup.
// These are local process/thread counters, not estimates from process-wide JIT.
// Do not suspend the UI thread to obtain a stack: that can deadlock the runtime.
internal static class DiagnosticThreadProbe
{
    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long created, out long exited,
        out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

    internal static int NativeThreadId => GetCurrentThreadId();

    internal static double? CurrentThreadCpuMilliseconds()
        => GetThreadTimes(new nint(-2), out _, out _, out long kernel, out long user)
            ? (kernel + user) / 10000.0 : null;

    internal static DiagnosticThreadSnapshot Read(int nativeThreadId)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            using Process process = Process.GetCurrentProcess();
            // One OS snapshot per slow probe, off the dispatcher. Dispose every
            // wrapper, including non-target threads; never log other identities.
            ProcessThreadCollection threads = process.Threads;
            try
            {
                ProcessThread? target = threads.Cast<ProcessThread>().FirstOrDefault(thread => thread.Id == nativeThreadId);
                if (target is null) return new(null, null, null, null, null, null, elapsed.Elapsed.TotalMilliseconds, -1);
                System.Diagnostics.ThreadState state = target.ThreadState;
                bool hasIo = GetProcessIoCounters(new nint(-1), out IoCounters io);
                return new(target.TotalProcessorTime.TotalMilliseconds, process.TotalProcessorTime.TotalMilliseconds,
                    state.ToString(), state == System.Diagnostics.ThreadState.Wait ? target.WaitReason.ToString() : null,
                    process.WorkingSet64, GC.GetTotalAllocatedBytes(precise: false), elapsed.Elapsed.TotalMilliseconds, null,
                    hasIo ? io.ReadBytes : null, hasIo ? io.WriteBytes : null,
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), ThreadPool.PendingWorkItemCount,
                    ThreadPool.ThreadCount);
            }
            finally { foreach (ProcessThread thread in threads) thread.Dispose(); }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new(null, null, null, null, null, null, elapsed.Elapsed.TotalMilliseconds, exception.HResult);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperations;
        internal ulong WriteOperations;
        internal ulong OtherOperations;
        internal ulong ReadBytes;
        internal ulong WriteBytes;
        internal ulong OtherBytes;
    }
}

internal sealed record DiagnosticThreadSnapshot(double? ThreadCpuMs, double? ProcessCpuMs,
    string? ThreadState, string? WaitReason, long? WorkingSetBytes, long? AllocatedBytes, double ProbeMs, int? Error,
    ulong? ReadBytes = null, ulong? WriteBytes = null, int? Gen0 = null, int? Gen1 = null, int? Gen2 = null,
    long? PendingWorkItems = null, int? PoolThreads = null);

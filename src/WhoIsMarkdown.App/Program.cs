using System.Diagnostics;
using System.Runtime.CompilerServices;
using WhoIsMarkdown.App.Services;

namespace WhoIsMarkdown.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Bug fix: the generated WPF entry constructs Application before logging.
        // Keep WPF type loading/base construction inside the measured boundary.
        long entry = Stopwatch.GetTimestamp();
        DateTimeOffset entryUtc = DateTimeOffset.UtcNow;
        DiagnosticLog.Start(entryUtc);
        DiagnosticLog.Current?.Record(DiagnosticStage.ManagedEntry, DiagnosticOutcome.Succeeded,
            duration: Stopwatch.GetElapsedTime(entry).TotalMilliseconds);
        try { RunApplication(); }
        catch (Exception exception)
        {
            // Application's own unhandled hooks do not exist if WPF's base
            // constructor fails. Keep that early failure visible as well.
            DiagnosticLog.Current?.Error(DiagnosticStage.ProcessUnhandled, exception);
            _ = DiagnosticLog.Current?.StopAsync().Wait(TimeSpan.FromMilliseconds(500));
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication()
    {
        DiagnosticLog.Current?.Mark(DiagnosticStage.ApplicationBootstrapEntered);
        App application = DiagnosticLog.Measure(DiagnosticStage.ApplicationAllocation, () => new App());
        DiagnosticLog.Measure(DiagnosticStage.ApplicationResources, application.InitializeComponent);
        DiagnosticLog.Current?.Mark(DiagnosticStage.ApplicationRunRequested);
        application.Run();
    }
}

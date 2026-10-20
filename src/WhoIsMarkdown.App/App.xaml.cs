using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Lifecycle;

namespace WhoIsMarkdown.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the application lifetime; OnExit disposes the process mutex.")]
public partial class App : Application
{
    internal const string RunningMutexName =
        @"Local\WIMD.UpdateRestart.A278F55A4D4146B4A1D9DA41D8C7D655";
    internal const string RestoreUpdateSessionArgument = "--restore-update-session";

    private Mutex? runningProcessMutex;
    private UpdateRestartSessionStore? updateRestartSessionStore;

    public App()
    {
        // Program logs before WPF construction. Keep an idempotent fallback for
        // hosts constructing App directly rather than entering through Main.
        DiagnosticLog.Start();
        DiagnosticLog.Current?.Mark(DiagnosticStage.ApplicationConstructor);
        DispatcherUnhandledException += (_, args) =>
            DiagnosticLog.Current?.Error(DiagnosticStage.DispatcherUnhandled, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                DiagnosticLog.Current?.Error(DiagnosticStage.ProcessUnhandled, exception);
            FlushDiagnosticsOnExit();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            DiagnosticLog.Current?.Error(DiagnosticStage.UnobservedTask, args.Exception);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        using DiagnosticOperation? startup = DiagnosticLog.Current?.Begin(DiagnosticStage.ApplicationStartup);
        base.OnStartup(e);

        // This mutex is a running-process signal for Inno Setup, not a
        // single-instance lock. Multiple WIMD windows/processes remain supported.
        runningProcessMutex = new Mutex(initiallyOwned: false, RunningMutexName);
        updateRestartSessionStore = new UpdateRestartSessionStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WIMD"));

        IReadOnlyList<UpdateRestartWindowState> restoredStates = DiagnosticLog.Measure(DiagnosticStage.RestoreSession,
            updateRestartSessionStore.ConsumeRequestedWindows);
        if (restoredStates.Count > 0)
        {
            RestoreRequestedWindows(updateRestartSessionStore, restoredStates);
            startup?.Succeed();
            return;
        }

        ShowWindow(state: null, updateRestartSessionStore);
        startup?.Succeed();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLog.Current?.Mark(DiagnosticStage.ApplicationExit, value: e.ApplicationExitCode);
        runningProcessMutex?.Dispose();
        runningProcessMutex = null;
        base.OnExit(e);
        FlushDiagnosticsOnExit();
    }

    private static void FlushDiagnosticsOnExit()
    {
        // WPF's exit callback is synchronous. Bound this final best-effort drain;
        // an unavailable disk must never keep the process alive indefinitely.
        Task? drain = DiagnosticLog.Current?.StopAsync();
        if (drain is not null) _ = drain.Wait(TimeSpan.FromMilliseconds(500));
    }

    private void RestoreRequestedWindows(
        UpdateRestartSessionStore sessionStore,
        IReadOnlyList<UpdateRestartWindowState> states)
    {
        // Restore all prior windows in this process. This avoids Windows command-
        // line size limits when an unsaved Markdown document is large.
        foreach (UpdateRestartWindowState state in states)
        {
            ShowWindow(state, sessionStore);
        }
    }

    private void ShowWindow(
        UpdateRestartWindowState? state,
        UpdateRestartSessionStore sessionStore)
    {
        MainWindow window = DiagnosticLog.Measure(DiagnosticStage.WindowAllocation,
            () => new MainWindow(state, sessionStore));
        if (MainWindow is null)
        {
            MainWindow = window;
        }

        DiagnosticLog.Measure(DiagnosticStage.WindowShow, window.Show);
    }

}

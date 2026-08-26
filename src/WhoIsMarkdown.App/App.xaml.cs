using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // This mutex is a running-process signal for Inno Setup, not a
        // single-instance lock. Multiple WIMD windows/processes remain supported.
        runningProcessMutex = new Mutex(initiallyOwned: false, RunningMutexName);
        updateRestartSessionStore = new UpdateRestartSessionStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WIMD"));

        IReadOnlyList<UpdateRestartWindowState> restoredStates =
            updateRestartSessionStore.ConsumeRequestedWindows();
        if (restoredStates.Count > 0)
        {
            RestoreRequestedWindows(updateRestartSessionStore, restoredStates);
            return;
        }

        ShowWindow(state: null, updateRestartSessionStore);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        runningProcessMutex?.Dispose();
        runningProcessMutex = null;
        base.OnExit(e);
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
        MainWindow window = new(state, sessionStore);
        if (MainWindow is null)
        {
            MainWindow = window;
        }

        window.Show();
    }

}

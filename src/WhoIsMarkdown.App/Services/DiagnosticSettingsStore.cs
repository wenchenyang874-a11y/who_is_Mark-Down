using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Time the actual merged disk transaction, not just the UI enqueue operation.
/// The wrapped Core store retains its existing inter-process locking semantics.
/// </summary>
internal sealed class DiagnosticSettingsStore(IApplicationSettingsStore store, int window) : IApplicationSettingsStore
{
    public ApplicationSettings Load() => store.Load();

    public void Save(ApplicationSettings settings) => DiagnosticLog.Measure(
        DiagnosticStage.SettingsWrite, () => store.Save(settings), window);

    public void SaveChanges(ApplicationSettings baseline, ApplicationSettings settings) => DiagnosticLog.Measure(
        DiagnosticStage.SettingsWrite, () => store.SaveChanges(baseline, settings), window);
}

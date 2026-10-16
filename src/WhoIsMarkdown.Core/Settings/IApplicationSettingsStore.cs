namespace WhoIsMarkdown.Core.Settings;

public interface IApplicationSettingsStore
{
    public ApplicationSettings Load();

    public void Save(ApplicationSettings settings);

    /// <summary>
    /// Applies only local changes to the latest persisted state. Implementations
    /// shared by processes must make the read/merge/replace one transaction.
    /// </summary>
    public void SaveChanges(ApplicationSettings baseline, ApplicationSettings settings)
        => Save(ApplicationSettingsChanges.Merge(Load(), baseline, settings));
}

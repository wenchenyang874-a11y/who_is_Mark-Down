namespace WhoIsMarkdown.Core.Settings;

public interface IApplicationSettingsStore
{
    public ApplicationSettings Load();

    public void Save(ApplicationSettings settings);
}

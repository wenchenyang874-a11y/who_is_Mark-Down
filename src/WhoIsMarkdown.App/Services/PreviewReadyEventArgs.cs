namespace WhoIsMarkdown.App.Services;

public sealed class PreviewReadyEventArgs(bool synchronizeToCaret) : EventArgs
{
    public bool SynchronizeToCaret { get; } = synchronizeToCaret;
}

namespace WhoIsMarkdown.App.Services;

public sealed class PreviewTaskToggleRequestedEventArgs(
    int sourceLine,
    bool isCompleted) : EventArgs
{
    public int SourceLine { get; } = sourceLine;

    public bool IsCompleted { get; } = isCompleted;

    public bool Succeeded { get; set; }
}

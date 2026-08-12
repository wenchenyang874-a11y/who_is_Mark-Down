namespace WhoIsMarkdown.App.Services;

public sealed class PreviewScrollChangedEventArgs(double ratio) : EventArgs
{
    public double Ratio { get; } = Math.Clamp(ratio, 0, 1);
}

namespace WhoIsMarkdown.Core.Updates;

public sealed record UpdateRelease(
    Version Version,
    string TagName,
    string DisplayName,
    string ReleaseNotes,
    DateTimeOffset PublishedAtUtc,
    Uri ReleasePageUri,
    UpdateAsset Installer);

public sealed record UpdateAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string Sha256Hex);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesReceived / TotalBytes * 100, 0, 100);
}

namespace WhoIsMarkdown.Core.Settings;

public sealed record RecentFileEntry(string Path, DateTimeOffset LastOpenedUtc);

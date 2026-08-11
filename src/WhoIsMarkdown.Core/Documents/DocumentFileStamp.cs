namespace WhoIsMarkdown.Core.Documents;

/// <summary>
/// A lightweight file identity used to detect changes made by another process.
/// </summary>
/// <param name="Length">File length in bytes.</param>
/// <param name="LastWriteTimeUtc">Last write timestamp in UTC.</param>
public readonly record struct DocumentFileStamp(long Length, DateTime LastWriteTimeUtc);

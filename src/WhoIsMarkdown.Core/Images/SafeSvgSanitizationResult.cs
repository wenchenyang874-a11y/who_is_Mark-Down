namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Contains the deterministic UTF-8 SVG produced by the static-profile sanitizer.
/// </summary>
public sealed record SafeSvgSanitizationResult(
    byte[] Bytes,
    int RemovedElementCount,
    int RemovedAttributeCount);

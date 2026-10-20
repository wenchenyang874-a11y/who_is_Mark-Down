using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;

namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Describes how user-selected and clipboard images are inserted. The ImgBB API
/// key is stored only as Windows-DPAPI ciphertext; plaintext credentials never
/// enter diagnostics or portable application data.
/// </summary>
public sealed record ImageInsertionSettings
{
    public ImageStorageMode StorageMode { get; init; } = ImageStorageMode.Local;

    public string LocalDirectory { get; init; } = LocalImageStorageService.DefaultRelativeDirectory;

    public RemoteImageTrustMode TrustMode { get; init; } = RemoteImageTrustMode.TrustAll;

    public IReadOnlyList<string> RemoteImageRules { get; init; } = [];

    public string? ProtectedImgBbApiKey { get; init; }

    public ImageInsertionSettings Normalize()
    {
        ImageStorageMode storageMode = Enum.IsDefined(StorageMode)
            ? StorageMode
            : ImageStorageMode.Local;
        string localDirectory;
        try
        {
            localDirectory = LocalImageStorageService.NormalizeRelativeDirectory(LocalDirectory);
        }
        catch (ArgumentException)
        {
            localDirectory = LocalImageStorageService.DefaultRelativeDirectory;
        }

        // Product policy changed: old serialized deny/allow lists must not keep
        // breaking images after upgrading. Keep the fields for JSON compatibility
        // but normalize them to the single current, unrestricted web-image mode.

        string? protectedApiKey = string.IsNullOrWhiteSpace(ProtectedImgBbApiKey)
            || ProtectedImgBbApiKey.Length > 16_384
            ? null
            : ProtectedImgBbApiKey.Trim();

        return this with
        {
            StorageMode = storageMode,
            LocalDirectory = localDirectory,
            TrustMode = RemoteImageTrustMode.TrustAll,
            RemoteImageRules = [],
            ProtectedImgBbApiKey = protectedApiKey,
        };
    }
}

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

    public RemoteImageTrustMode TrustMode { get; init; } = RemoteImageTrustMode.BlockAll;

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

        RemoteImageTrustMode remoteImageTrustMode = Enum.IsDefined(TrustMode)
            ? TrustMode
            : RemoteImageTrustMode.BlockAll;
        IReadOnlyList<string> remoteImageRules;
        try
        {
            remoteImageRules = RemoteImagePolicy.NormalizeRules(RemoteImageRules);
        }
        catch (ArgumentException)
        {
            remoteImageTrustMode = RemoteImageTrustMode.BlockAll;
            remoteImageRules = [];
        }

        string? protectedApiKey = string.IsNullOrWhiteSpace(ProtectedImgBbApiKey)
            || ProtectedImgBbApiKey.Length > 16_384
            ? null
            : ProtectedImgBbApiKey.Trim();

        return this with
        {
            StorageMode = storageMode,
            LocalDirectory = localDirectory,
            TrustMode = remoteImageTrustMode,
            RemoteImageRules = remoteImageRules,
            ProtectedImgBbApiKey = protectedApiKey,
        };
    }
}

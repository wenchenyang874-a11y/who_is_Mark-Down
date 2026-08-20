using System.Net;
using System.Security.Cryptography;

namespace WhoIsMarkdown.Core.Updates;

/// <summary>
/// Downloads a validated release asset through a small HTTPS redirect allowlist,
/// writes it to a temporary file, then publishes it only after exact size and
/// SHA-256 verification. Partial or mismatched installers are always removed.
/// </summary>
public sealed class UpdateInstallerDownloader
{
    public const long MaximumInstallerBytes = 256L * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private readonly HttpClient httpClient;

    public UpdateInstallerDownloader(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<string> DownloadAsync(
        UpdateRelease release,
        string outputDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ValidateAsset(release.Installer);
        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        string targetPath = Path.Combine(directory, release.Installer.Name);
        string temporaryPath = string.Concat(targetPath, ".", Guid.NewGuid().ToString("N"), ".part");

        try
        {
            using HttpResponseMessage response = await SendWithSafeRedirectsAsync(
                release.Installer.DownloadUri,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength
                && contentLength != release.Installer.Size)
            {
                throw new UpdateServiceException("安装包下载大小与 Release 元数据不一致。");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long received = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;
                if (received > release.Installer.Size || received > MaximumInstallerBytes)
                {
                    throw new UpdateServiceException("安装包下载内容超过预期大小。");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new UpdateDownloadProgress(received, release.Installer.Size));
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (received != release.Installer.Size)
            {
                throw new UpdateServiceException("安装包下载不完整。");
            }

            byte[] expectedHash = Convert.FromHexString(release.Installer.Sha256Hex);
            byte[] actualHash = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new UpdateServiceException("安装包 SHA-256 校验失败，文件已删除。");
            }

            await destination.DisposeAsync().ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UpdateServiceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or FormatException)
        {
            throw new UpdateServiceException($"无法下载 WIMD 更新：{exception.Message}", exception);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            ValidateDownloadUri(currentUri);
            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("WIMD-Updater/1.0");
            HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new UpdateServiceException("GitHub 下载重定向缺少目标地址。");
            }

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
        }

        throw new UpdateServiceException("GitHub 下载重定向次数过多。");
    }

    private static void ValidateAsset(UpdateAsset asset)
    {
        if (asset.Size is <= 0 or > MaximumInstallerBytes
            || asset.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !asset.Name.StartsWith("WIMD-Setup-v", StringComparison.Ordinal)
            || !asset.Name.EndsWith("-win-x64.exe", StringComparison.Ordinal)
            || asset.Sha256Hex.Length != 64
            || !asset.Sha256Hex.All(Uri.IsHexDigit))
        {
            throw new UpdateServiceException("Release 安装包元数据无效。");
        }

        ValidateDownloadUri(asset.DownloadUri);
    }

    private static void ValidateDownloadUri(Uri uri)
    {
        bool trustedHost = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !trustedHost)
        {
            throw new UpdateServiceException("安装包下载跳转到了非 GitHub HTTPS 地址。");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup must not hide the original update error.
        }
    }
}

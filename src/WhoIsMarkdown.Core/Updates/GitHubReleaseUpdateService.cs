using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhoIsMarkdown.Core.Updates;

/// <summary>
/// Reads only the repository's public GitHub Releases API. A release is usable
/// only when its tag, installer filename, HTTPS URL, size and GitHub SHA-256
/// digest agree, preventing an ambiguous asset from becoming an update.
/// </summary>
public sealed partial class GitHubReleaseUpdateService
{
    public const string RepositoryOwner = "wenchenyang874-a11y";
    public const string RepositoryName = "who_is_Mark-Down";
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly Uri LatestReleaseUri = new(
        $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");
    private readonly HttpClient httpClient;

    public GitHubReleaseUpdateService(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<UpdateRelease?> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"WIMD/{NormalizeVersion(currentVersion)}");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            byte[] json = await ReadBoundedAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            UpdateRelease release = ParseRelease(json);
            return release.Version > NormalizeVersion(currentVersion) ? release : null;
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
            or JsonException
            or FormatException)
        {
            throw new UpdateServiceException($"无法检查 WIMD 更新：{exception.Message}", exception);
        }
    }

    internal static UpdateRelease ParseRelease(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (GetBoolean(root, "draft") || GetBoolean(root, "prerelease"))
        {
            throw new UpdateServiceException("GitHub 返回的最新版本不是正式 Release。");
        }

        string tagName = GetRequiredString(root, "tag_name");
        Version version = ParseTagVersion(tagName);
        string expectedAssetName = $"WIMD-Setup-v{version}-win-x64.exe";
        JsonElement[] matchingAssets = root.GetProperty("assets")
            .EnumerateArray()
            .Where(item => string.Equals(
                item.GetProperty("name").GetString(),
                expectedAssetName,
                StringComparison.Ordinal))
            .ToArray();
        if (matchingAssets.Length != 1)
        {
            throw new UpdateServiceException($"Release 必须且只能包含一个预期安装包：{expectedAssetName}");
        }

        JsonElement asset = matchingAssets[0];

        string digest = asset.TryGetProperty("digest", out JsonElement digestElement)
            && digestElement.ValueKind == JsonValueKind.String
            ? digestElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        Match digestMatch = Sha256DigestRegex().Match(digest);
        if (!digestMatch.Success)
        {
            throw new UpdateServiceException("Release 安装包缺少有效的 SHA-256 摘要。");
        }

        long size = asset.GetProperty("size").GetInt64();
        if (size is <= 0 or > UpdateInstallerDownloader.MaximumInstallerBytes)
        {
            throw new UpdateServiceException("Release 安装包大小异常。");
        }

        Uri downloadUri = GetRequiredHttpsUri(asset, "browser_download_url");
        if (!downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateServiceException("Release 安装包不是 GitHub 官方下载地址。");
        }

        string displayName = root.TryGetProperty("name", out JsonElement nameElement)
            ? nameElement.GetString()?.Trim() ?? tagName
            : tagName;
        string notes = root.TryGetProperty("body", out JsonElement bodyElement)
            ? bodyElement.GetString() ?? string.Empty
            : string.Empty;
        DateTimeOffset publishedAt = root.GetProperty("published_at").GetDateTimeOffset();

        return new UpdateRelease(
            version,
            tagName,
            displayName,
            notes,
            publishedAt,
            GetRequiredHttpsUri(root, "html_url"),
            new UpdateAsset(expectedAssetName, downloadUri, size, digestMatch.Groups[1].Value));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new UpdateServiceException("GitHub Release 响应过大。");
        }

        await using Stream source = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using MemoryStream destination = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > MaximumResponseBytes)
            {
                throw new UpdateServiceException("GitHub Release 响应过大。");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static Version ParseTagVersion(string tagName)
    {
        Match match = VersionTagRegex().Match(tagName);
        return match.Success && Version.TryParse(match.Groups[1].Value, out Version? version)
            ? version
            : throw new UpdateServiceException("GitHub Release 标签不是有效的三段式版本号。");
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            Math.Max(0, version.Major),
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.True;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!.Trim()
            : throw new UpdateServiceException($"GitHub Release 缺少字段：{propertyName}");
    }

    private static Uri GetRequiredHttpsUri(JsonElement element, string propertyName)
    {
        string value = GetRequiredString(element, propertyName);
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : throw new UpdateServiceException($"GitHub Release 的 {propertyName} 不是 HTTPS 地址。");
    }

    [GeneratedRegex(@"^v(\d+\.\d+\.\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTagRegex();

    [GeneratedRegex(@"^sha256:([0-9a-fA-F]{64})$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256DigestRegex();
}

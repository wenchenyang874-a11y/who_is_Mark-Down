using System.Net;
using System.Security.Cryptography;
using System.Text;
using WhoIsMarkdown.Core.Updates;

namespace WhoIsMarkdown.Core.Tests.Updates;

public sealed class GitHubReleaseUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_WhenNewValidatedReleaseExists_ReturnsInstaller()
    {
        byte[] payload = Encoding.UTF8.GetBytes(CreateReleaseJson(
            "v1.7.0",
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            size: 42));
        using HttpClient client = new(new DelegateHandler((request, _) =>
        {
            Assert.Equal("api.github.com", request.RequestUri?.Host);
            Assert.Contains("WIMD/1.6.4", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }));
        GitHubReleaseUpdateService service = new(client);

        UpdateRelease? release = await service.CheckAsync(
            new Version(1, 6, 4),
            TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 7, 0), release.Version);
        Assert.Equal("WIMD-Setup-v1.7.0-win-x64.exe", release.Installer.Name);
        Assert.Equal(64, release.Installer.Sha256Hex.Length);
    }

    [Fact]
    public async Task CheckAsync_WhenDigestIsMissing_RejectsRelease()
    {
        byte[] payload = Encoding.UTF8.GetBytes(CreateReleaseJson("v1.7.0", string.Empty, size: 42));
        using HttpClient client = new(new DelegateHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            })));
        GitHubReleaseUpdateService service = new(client);

        UpdateServiceException exception = await Assert.ThrowsAsync<UpdateServiceException>(
            () => service.CheckAsync(
                new Version(1, 6, 4),
                TestContext.Current.CancellationToken));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_WhenRedirectAndHashAreValid_PublishesInstallerAtomically()
    {
        byte[] installer = Encoding.UTF8.GetBytes("verified installer bytes");
        string digest = Convert.ToHexString(SHA256.HashData(installer));
        int requestCount = 0;
        using HttpClient client = new(new DelegateHandler((_, _) =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                HttpResponseMessage redirect = new(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/wimd.exe");
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installer),
            });
        }));
        UpdateRelease release = CreateRelease(installer.Length, digest);
        using TemporaryDirectory temporaryDirectory = new();
        UpdateInstallerDownloader downloader = new(client);

        string path = await downloader.DownloadAsync(
            release,
            temporaryDirectory.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path, "*.part"));
    }

    [Fact]
    public async Task DownloadAsync_WhenRedirectLeavesGitHub_RejectsAndDeletesPartialFile()
    {
        using HttpClient client = new(new DelegateHandler((_, _) =>
        {
            HttpResponseMessage redirect = new(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://example.com/wimd.exe");
            return Task.FromResult(redirect);
        }));
        UpdateRelease release = CreateRelease(
            size: 1,
            digest: new string('a', 64));
        using TemporaryDirectory temporaryDirectory = new();
        UpdateInstallerDownloader downloader = new(client);

        await Assert.ThrowsAsync<UpdateServiceException>(
            () => downloader.DownloadAsync(
                release,
                temporaryDirectory.Path,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFiles(temporaryDirectory.Path));
    }

    private static UpdateRelease CreateRelease(long size, string digest)
    {
        return new UpdateRelease(
            new Version(1, 7, 0),
            "v1.7.0",
            "WIMD v1.7.0",
            "notes",
            DateTimeOffset.UtcNow,
            new Uri("https://github.com/wenchenyang874-a11y/who_is_Mark-Down/releases/tag/v1.7.0"),
            new UpdateAsset(
                "WIMD-Setup-v1.7.0-win-x64.exe",
                new Uri("https://github.com/wenchenyang874-a11y/who_is_Mark-Down/releases/download/v1.7.0/WIMD-Setup-v1.7.0-win-x64.exe"),
                size,
                digest));
    }

    private static string CreateReleaseJson(string tag, string digest, long size)
    {
        string version = tag.TrimStart('v');
        return $$"""
            {
              "tag_name": "{{tag}}",
              "name": "WIMD {{tag}}",
              "body": "release notes",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-08-20T00:00:00Z",
              "html_url": "https://github.com/wenchenyang874-a11y/who_is_Mark-Down/releases/tag/{{tag}}",
              "assets": [{
                "name": "WIMD-Setup-v{{version}}-win-x64.exe",
                "size": {{size}},
                "digest": "{{digest}}",
                "browser_download_url": "https://github.com/wenchenyang874-a11y/who_is_Mark-Down/releases/download/{{tag}}/WIMD-Setup-v{{version}}-win-x64.exe"
              }]
            }
            """;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request, cancellationToken);
        }
    }
}

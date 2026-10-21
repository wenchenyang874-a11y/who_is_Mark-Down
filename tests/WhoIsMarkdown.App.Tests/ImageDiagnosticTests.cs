using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using WhoIsMarkdown.App.Services;
using WhoIsMarkdown.Core.Images;
using WhoIsMarkdown.Core.Markdown;
using Xunit;

namespace WhoIsMarkdown.App.Tests;

[Collection("Diagnostic log static isolation")]
public sealed class ImageDiagnosticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 图片诊断_成功或HTTP失败_保留分段结果且不写入图片信息(bool fail)
    {
        string root = Path.Combine(Path.GetTempPath(), "WIMD-image-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        LocalDiagnosticLog? previous = DiagnosticLog.Current;
        LocalDiagnosticLog log = new(Path.Combine(root, "logs"), "qa");
        CoreDiagnosticBridge.Start();
        DiagnosticLog.Current = log;
        try
        {
            using PreviewImageSaveService images = new(new ImageHandler(fail));
            PreviewImageSaveSource source = images.Resolve("https://SECRET_DO_NOT_LOG.example/image?token=SECRET_DO_NOT_LOG",
                null, "SECRET_DO_NOT_LOG", RemoteImagePolicy.TrustAll);
            Task<PreparedPreviewImage> prepare = DiagnosticLog.MeasureAsync(DiagnosticStage.ImagePrepare,
                () => images.PrepareAsync(source, Path.Combine(root, "SECRET_DO_NOT_LOG")));
            if (fail) await Assert.ThrowsAsync<PreviewImageSaveException>(() => prepare);
            else await prepare;
        }
        finally
        {
            DiagnosticLog.Current = previous;
            await log.StopAsync();
        }
        try
        {
            string text = string.Join("\n", Directory.GetFiles(Path.Combine(root, "logs")).Select(File.ReadAllText));
            Assert.DoesNotContain("SECRET_DO_NOT_LOG", text, StringComparison.Ordinal);
            Assert.DoesNotContain(root, text, StringComparison.Ordinal);
            JsonElement[] rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            Assert.Equal(2, rows[0].GetProperty("image_pipeline_revision").GetInt32());
            Assert.Contains(rows, row => IsStage(row, "ImageHttpStatus") && row.GetProperty("value").GetInt64() == (fail ? 503 : 200));
            Assert.Contains(rows, row => IsStage(row, "ImageHttpHeaders") && row.GetProperty("outcome").GetString() == "Succeeded"
                && row.GetProperty("duration_ms").GetDouble() >= 0);
            Assert.Contains(rows, row => IsStage(row, "ImagePrepare") && row.GetProperty("outcome").GetString() == (fail ? "Failed" : "Succeeded"));
            if (!fail)
            {
                foreach (string stage in new[] { "ImageCacheDirectory", "ImageResponseStream", "ImageDownloadBody", "ImageFormatDetect", "ImageCacheWrite", "ImageCacheFlush", "ImageCachePublish" })
                    Assert.Contains(rows, row => IsStage(row, stage) && row.GetProperty("outcome").GetString() == "Succeeded");
                Assert.Contains(rows, row => IsStage(row, "ImageBytes") && row.GetProperty("value").GetInt64() == 8);
                Assert.Contains(rows, row => IsStage(row, "ImageStreamRead") && row.GetProperty("duration_ms").GetDouble() >= 0);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool IsStage(JsonElement row, string stage) =>
        row.TryGetProperty("stage", out JsonElement name) && name.GetString() == stage;

    private sealed class ImageHandler(bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ByteArrayContent body = new([137, 80, 78, 71, 13, 10, 26, 10]);
            body.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = body });
        }
    }
}

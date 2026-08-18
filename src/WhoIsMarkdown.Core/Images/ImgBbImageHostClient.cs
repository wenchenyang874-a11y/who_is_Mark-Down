using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WhoIsMarkdown.Core.Images;

/// <summary>
/// Uploads one explicitly selected image to ImgBB. The API key is used only for
/// the HTTPS request and is never included in exception text; returned URLs must
/// match ImgBB's fixed HTTPS image host before they can enter a Markdown document.
/// </summary>
public sealed class ImgBbImageHostClient : IImageHostClient
{
    public const long MaximumImageBytes = 32L * 1024 * 1024;
    public const string ImageHostName = "i.ibb.co";

    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly Uri UploadEndpoint = new("https://api.imgbb.com/1/upload");
    private static readonly HashSet<string> SupportedExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".bmp",
        ".webp",
    };

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private bool disposed;

    public ImgBbImageHostClient()
        : this(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, ownsHttpClient: true)
    {
    }

    public ImgBbImageHostClient(HttpClient httpClient)
        : this(httpClient, ownsHttpClient: false)
    {
    }

    private ImgBbImageHostClient(HttpClient httpClient, bool ownsHttpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.ownsHttpClient = ownsHttpClient;
    }

    public async Task<HostedImage> UploadAsync(
        Stream imageStream,
        string fileName,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(imageStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ValidateFileName(fileName);

        byte[] imageBytes = await ReadImageBytesAsync(imageStream, cancellationToken).ConfigureAwait(false);
        try
        {
            string endpoint = string.Concat(UploadEndpoint.AbsoluteUri, "?key=", Uri.EscapeDataString(apiKey.Trim()));
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
            using MultipartFormDataContent form = new();
            ByteArrayContent imageContent = new(imageBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(GetMediaType(fileName));
            form.Add(imageContent, "image", Path.GetFileName(fileName));
            request.Content = form;

            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            byte[] responseBytes = await ReadResponseAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            return ParseResponse(response.StatusCode, responseBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ImageHostUploadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or JsonException)
        {
            // HttpClient errors may include the request URI, whose query contains
            // the API key required by ImgBB. Do not retain that exception as an
            // inner exception or surface its message to UI and diagnostic output.
            throw new ImageHostUploadException(
                "ImgBB 上传失败，请检查网络连接、API Key 或图床服务状态。");
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(imageBytes);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static async Task<byte[]> ReadImageBytesAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        if (!stream.CanRead)
        {
            throw new ArgumentException("图片流不可读。", nameof(stream));
        }

        if (stream.CanSeek && (stream.Length <= 0 || stream.Length > MaximumImageBytes))
        {
            throw new ImageHostUploadException("ImgBB 图片大小必须在 1 字节到 32 MB 之间。");
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumImageBytes)
            {
                throw new ImageHostUploadException("ImgBB 单张图片不能超过 32 MB。");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            throw new ImageHostUploadException("不能上传空图片。");
        }

        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream responseStream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16384];
        while (true)
        {
            int read = await responseStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new ImageHostUploadException("ImgBB 返回了异常大的响应。");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static HostedImage ParseResponse(HttpStatusCode statusCode, byte[] responseBytes)
    {
        using JsonDocument json = JsonDocument.Parse(responseBytes);
        JsonElement root = json.RootElement;
        bool succeeded = root.TryGetProperty("success", out JsonElement success)
            && success.ValueKind == JsonValueKind.True;
        if (!succeeded || !IsSuccessStatusCode(statusCode))
        {
            // Treat the service response as untrusted. It may echo request data,
            // so only the numeric status is safe to show to the user.
            throw new ImageHostUploadException(
                $"ImgBB 拒绝了上传（HTTP {(int)statusCode}）。");
        }

        if (!root.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("url", out JsonElement urlElement)
            || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? imageUrl)
            || !IsTrustedImageUrl(imageUrl))
        {
            throw new ImageHostUploadException("ImgBB 返回了无效或不受信任的图片地址。");
        }

        Uri? deleteUrl = null;
        if (data.TryGetProperty("delete_url", out JsonElement deleteElement)
            && Uri.TryCreate(deleteElement.GetString(), UriKind.Absolute, out Uri? parsedDeleteUrl)
            && parsedDeleteUrl.Scheme == Uri.UriSchemeHttps
            && parsedDeleteUrl.IdnHost.Equals("ibb.co", StringComparison.OrdinalIgnoreCase))
        {
            deleteUrl = parsedDeleteUrl;
        }

        return new HostedImage(imageUrl, deleteUrl);
    }

    private static bool IsTrustedImageUrl(Uri uri)
    {
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IdnHost.Equals(ImageHostName, StringComparison.OrdinalIgnoreCase)
            && SupportedExtensions.Contains(Path.GetExtension(uri.AbsolutePath));
    }

    private static void ValidateFileName(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new ImageHostUploadException("ImgBB 上传仅支持 PNG、JPEG、GIF、BMP 和 WebP 图片。");
        }
    }

    private static string GetMediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    private static string? TryGetErrorMessage(JsonElement root)
    {
        return root.TryGetProperty("error", out JsonElement error)
            && error.TryGetProperty("message", out JsonElement message)
            ? message.GetString()
            : null;
    }

    private static bool IsSuccessStatusCode(HttpStatusCode statusCode)
    {
        int numericStatus = (int)statusCode;
        return numericStatus is >= 200 and <= 299;
    }

    private static string Abbreviate(string value)
    {
        const int maximumLength = 160;
        return value.Length <= maximumLength ? value : string.Concat(value.AsSpan(0, maximumLength), "…");
    }
}

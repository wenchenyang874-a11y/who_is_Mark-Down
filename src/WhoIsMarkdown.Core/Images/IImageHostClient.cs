namespace WhoIsMarkdown.Core.Images;

public interface IImageHostClient : IDisposable
{
    public Task<HostedImage> UploadAsync(
        Stream imageStream,
        string fileName,
        string apiKey,
        CancellationToken cancellationToken = default);
}

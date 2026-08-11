namespace WhoIsMarkdown.Core.Documents;

public interface IDocumentFileService
{
    public Task<LoadedDocument> ReadAsync(
        string path,
        CancellationToken cancellationToken = default);

    public Task<DocumentFileStamp> WriteAsync(
        DocumentWriteRequest request,
        CancellationToken cancellationToken = default);

    public DocumentFileStamp Inspect(string path);
}

namespace WhoIsMarkdown.App.Services;

public interface IClipboardTextService
{
    public Task<bool> TrySetTextAsync(string text);
}

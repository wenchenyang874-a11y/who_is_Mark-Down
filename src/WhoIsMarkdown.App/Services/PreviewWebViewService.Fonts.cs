namespace WhoIsMarkdown.App.Services;

public sealed partial class PreviewWebViewService
{
    /// <summary>
    /// Collapsed sections may not have requested their fonts. Explicitly load all
    /// selected host-declared faces before print expands them. Never accept a
    /// pending Promise as success or silently print a fallback after font failure.
    /// </summary>
    private async Task WaitForPdfFontsAsync(CancellationToken cancellationToken)
    {
        if (core is null) throw new InvalidOperationException("预览组件尚未初始化。");
        string? result = await WebViewScriptResult.RunAsync(core,
            """
            (() => {
              window.wimdPdfFontsSettled = null;
              Promise.all([...document.fonts].map(face => face.load()))
                .then(() => { window.wimdPdfFontsSettled = true; },
                      () => { window.wimdPdfFontsSettled = false; });
            })();
            """, "window.wimdPdfFontsSettled", TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(true);
        if (!string.Equals(result, "true", StringComparison.Ordinal))
            throw new InvalidOperationException($"PDF 字体加载失败或超时（完成状态 {result ?? "未返回"}），未覆盖原文件。请检查安装目录中的 Fonts 文件。");
    }
}

namespace WhoIsMarkdown.App.Services;

// Fixed assembly identities only. Assembly.Location/FullName can contain paths
// or arbitrary plugin identifiers and must never become normal log payloads.
// AssemblyLoad is a completion timestamp, NOT a measured assembly load duration.
internal sealed class DiagnosticAssemblyObserver : IDisposable
{
    private readonly LocalDiagnosticLog log;
    private int stopped;

    internal DiagnosticAssemblyObserver(LocalDiagnosticLog log)
    {
        this.log = log;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        DiagnosticAssembly? assembly = Identify(args.LoadedAssembly.GetName().Name);
        if (assembly is not null)
            log.Record(DiagnosticStage.AssemblyLoaded, DiagnosticOutcome.Point, assembly: assembly);
    }

    internal static DiagnosticAssembly? Identify(string? name) => name switch
    {
        "WindowsBase" => DiagnosticAssembly.WindowsBase,
        "PresentationCore" => DiagnosticAssembly.PresentationCore,
        "PresentationFramework" => DiagnosticAssembly.PresentationFramework,
        "PresentationFramework.Fluent" => DiagnosticAssembly.Fluent,
        "ICSharpCode.AvalonEdit" => DiagnosticAssembly.AvalonEdit,
        "Microsoft.Web.WebView2.Core" => DiagnosticAssembly.WebViewCore,
        "Microsoft.Web.WebView2.Wpf" => DiagnosticAssembly.WebViewWpf,
        "Markdig" => DiagnosticAssembly.Markdig,
        "AngleSharp" => DiagnosticAssembly.AngleSharp,
        "AngleSharp.Css" => DiagnosticAssembly.AngleSharpCss,
        "HtmlSanitizer" => DiagnosticAssembly.HtmlSanitizer,
        "System.Xaml" => DiagnosticAssembly.SystemXaml,
        "System.Windows.Extensions" => DiagnosticAssembly.WindowsExtensions,
        "System.Drawing.Common" => DiagnosticAssembly.Drawing,
        _ => null,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0)
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
    }
}

internal enum DiagnosticAssembly
{
    WindowsBase, PresentationCore, PresentationFramework, Fluent, AvalonEdit, WebViewCore, WebViewWpf,
    Markdig, AngleSharp, AngleSharpCss, HtmlSanitizer, SystemXaml, WindowsExtensions, Drawing,
}

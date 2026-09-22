using System.IO;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Single definition of the browser profile WIMD hosts its WebView2 surfaces in.
/// WebView2 refuses to share one user data folder across processes, so every control
/// in the desktop app has to resolve the same path.
/// </summary>
internal static class WebView2UserDataFolder
{
    public static string Resolve() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WIMD",
        "WebView2");
}

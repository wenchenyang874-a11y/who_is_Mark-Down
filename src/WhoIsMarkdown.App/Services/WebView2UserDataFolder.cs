using System.IO;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Single definition of the browser profile WIMD hosts its WebView2 surfaces in.
/// Matching configurations may share the browser process for a user data folder.
/// Keep the requested path consistent; WebView2EnvironmentProvider also separates
/// dispatcher affinity and SDK environment overrides when reusing a creation task.
/// </summary>
internal static class WebView2UserDataFolder
{
    public static string Resolve() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WIMD",
        "WebView2");
}

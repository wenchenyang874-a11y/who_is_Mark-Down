using System.Windows.Media;
using WhoIsMarkdown.Core.Settings;

namespace WhoIsMarkdown.App.Services;

internal static class FontFamilyResolver
{
    public static FontFamily Resolve(string? selection, string fallback)
        => DiagnosticLog.Measure(DiagnosticStage.FontFamilyResolve, () => ResolveCore(selection, fallback));

    private static FontFamily ResolveCore(string? selection, string fallback)
    {
        BundledFont? bundled = BundledFontCatalog.Find(selection);
        if (bundled is null) return new FontFamily(selection ?? fallback);

        // An absolute application base avoids dependence on the document's folder
        // or process working directory. No OS font installation is performed.
        string directory = bundled.LicenseDirectory;
        return new FontFamily(new Uri(AppContext.BaseDirectory),
            $"./Fonts/{directory}/#{bundled.FamilyName}");
    }
}

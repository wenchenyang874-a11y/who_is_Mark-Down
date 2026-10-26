using System.Diagnostics;
using System.IO;

namespace WhoIsMarkdown.App.Services;

internal static class StartupDiagnosticLauncher
{
    internal static ProcessStartInfo CreateStartInfo(string applicationDirectory, string? documentPath = null)
    {
        // The coordinator is deliberately unelevated. Only its recorder asks
        // for UAC; the editor it starts must retain normal user privileges.
        ProcessStartInfo start = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = applicationDirectory,
        };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(applicationDirectory, "Diagnostics", "Capture-Startup.ps1") }) start.ArgumentList.Add(argument);
        if (documentPath is not null)
        {
            start.ArgumentList.Add("-DocumentPath");
            start.ArgumentList.Add(documentPath);
        }
        return start;
    }
}

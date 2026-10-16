using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WhoIsMarkdown.App.Infrastructure;

namespace WhoIsMarkdown.App.Services;

/// <summary>
/// Coalesces environment creation for preview, viewer and offscreen export. Native
/// objects remain dispatcher/STA-affine; only matching profile and SDK environment
/// overrides share a task. Canceling one window stops its wait, never the shared
/// creation. Failed creations and exited browser groups are not sticky cache hits.
/// </summary>
internal static class WebView2EnvironmentProvider
{
    private static readonly ConditionalWeakTable<Dispatcher, SharedInitializationCache<Configuration, CoreWebView2Environment>> Caches = new();
    private static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(15);
    private static int creationAttempts;

    internal static int CreationAttempts => Volatile.Read(ref creationAttempts);

    public static Task<CoreWebView2Environment> GetAsync(string userDataFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
        cancellationToken.ThrowIfCancellationRequested();
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("WebView2 环境必须在 UI/STA 线程准备。");
        }

        Dispatcher dispatcher = Dispatcher.FromThread(Thread.CurrentThread)
            ?? throw new InvalidOperationException("WebView2 环境需要已有的 WPF UI Dispatcher，不能在独立 STA 工作线程创建。");
        if (dispatcher.HasShutdownStarted) throw new OperationCanceledException("UI 线程正在关闭。", cancellationToken);
        string directory = Path.GetFullPath(userDataFolder);
        // The SDK can override the passed folder, runtime and arguments. Including
        // every WEBVIEW2_* variable also keeps test profiles and future overrides
        // isolated without logging argument values or inspecting disk paths.
        string overrides = string.Join('\0', Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(entry => ((string)entry.Key).StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => (string)entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => string.Concat(((string)entry.Key).ToUpperInvariant(), "=", entry.Value)));
        // Do not fold path case: Windows can enable case-sensitive directories.
        // Conservative duplicate entries are safer than merging distinct profiles.
        Configuration configuration = new(directory, overrides);
        SharedInitializationCache<Configuration, CoreWebView2Environment> cache = Caches.GetValue(dispatcher, owner =>
        {
            SharedInitializationCache<Configuration, CoreWebView2Environment> created = new();
            owner.ShutdownStarted += (_, _) => created.Clear();
            return created;
        });
        return WaitForEnvironmentAsync(cache.GetOrCreate(configuration, () => CreateAsync(directory, configuration, cache)), cancellationToken);
    }

    private static async Task<CoreWebView2Environment> WaitForEnvironmentAsync(
        Task<CoreWebView2Environment> creation, CancellationToken cancellationToken)
    {
        try
        {
            return await creation.WaitAsync(InitializationTimeout, cancellationToken).ConfigureAwait(true);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"WebView2 环境准备等待超过 15 秒（环境创建尝试 {CreationAttempts}）；停止等待不代表底层初始化已终止。", exception);
        }
    }

    private static async Task<CoreWebView2Environment> CreateAsync(
        string directory,
        Configuration configuration,
        SharedInitializationCache<Configuration, CoreWebView2Environment> cache)
    {
        Interlocked.Increment(ref creationAttempts);
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null, userDataFolder: directory).ConfigureAwait(true);
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs>? onExited = null;
        onExited = (_, _) =>
        {
            environment.BrowserProcessExited -= onExited;
            cache.Invalidate(configuration, environment);
        };
        environment.BrowserProcessExited += onExited;
        return environment;
    }

    private sealed record Configuration(string Directory, string Overrides);
}

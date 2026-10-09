using WhoIsMarkdown.Core.Documents;

namespace WhoIsMarkdown.Core.Tests.Documents;

public sealed class DocumentChangeMonitorTests
{
    [Fact]
    public async Task SetPath_ReportsChangesAndCanSwitchToAnotherFile()
    {
        using TemporaryDirectory directory = new();
        using DocumentChangeMonitor monitor = new();
        string first = Path.Combine(directory.Path, "first.md");
        string second = Path.Combine(directory.Path, "second.md");
        await WaitForChangeAsync(monitor, first);
        await WaitForChangeAsync(monitor, second);
    }

    [Fact]
    public async Task SetPath_InvalidDirectoryDoesNotPreventLaterRegistration()
    {
        using TemporaryDirectory directory = new();
        using DocumentChangeMonitor monitor = new();
        monitor.SetPath(Path.Combine(directory.Path, "missing", "file.md"));
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await WaitForChangeAsync(monitor, Path.Combine(directory.Path, "valid.md"));
    }

    [Fact]
    public async Task SetPath_RapidChangesKeepLatestRequest()
    {
        using TemporaryDirectory directory = new();
        using DocumentChangeMonitor monitor = new();
        for (int index = 0; index < 100; index++)
        {
            monitor.SetPath(Path.Combine(directory.Path, $"{index}.md"));
        }

        await WaitForChangeAsync(monitor, Path.Combine(directory.Path, "latest.md"));
    }

    [Fact]
    public async Task Dispose_DropsSubscribersAndRejectsNewRequests()
    {
        using TemporaryDirectory directory = new();
        DocumentChangeMonitor monitor = new();
        string path = Path.Combine(directory.Path, "file.md");
        await WaitForChangeAsync(monitor, path);
        monitor.Dispose();
        monitor.Dispose();
        Assert.Throws<ObjectDisposedException>(() => monitor.SetPath(path));
    }

    [Fact]
    public async Task SetPath_NullStopsWatching()
    {
        using TemporaryDirectory directory = new();
        using DocumentChangeMonitor monitor = new();
        string path = Path.Combine(directory.Path, "file.md");
        await WaitForChangeAsync(monitor, path);
        monitor.SetPath(null);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        int events = 0;
        monitor.Changed += (_, _) => Interlocked.Increment(ref events);
        await File.AppendAllTextAsync(path, "after detach", TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref events));
    }

    private static async Task WaitForChangeAsync(DocumentChangeMonitor monitor, string path)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs eventArgs) => signal.TrySetResult();
        monitor.Changed += OnChanged;
        try
        {
            monitor.SetPath(path);
            // Registration is intentionally asynchronous. Pulse until it is live,
            // rather than asserting a machine-dependent fixed startup delay.
            for (int attempt = 0; attempt < 50 && !signal.Task.IsCompleted; attempt++)
            {
                await File.AppendAllTextAsync(path, "change\n", token);
                await Task.Delay(50, token);
            }

            await signal.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
        }
        finally
        {
            monitor.Changed -= OnChanged;
        }
    }
}

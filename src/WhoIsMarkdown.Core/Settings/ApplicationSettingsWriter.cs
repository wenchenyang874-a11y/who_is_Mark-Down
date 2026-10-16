namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Keeps one active write and one replaceable pending snapshot. Preferences never
/// block the UI; flush skips debounce but cannot abort an in-progress filesystem
/// call. Only successful writes advance the local baseline, including across retries.
/// No timer, queued snapshot, or error callback may retain a closed window.
/// </summary>
public sealed class ApplicationSettingsWriter
{
    private readonly object gate = new();
    private readonly IApplicationSettingsStore store;
    private readonly TimeSpan debounce;
    private ApplicationSettings baseline;
    private ApplicationSettings desired;
    private ApplicationSettings? pending;
    private Task? runner;
    private TaskCompletionSource flushSignal = NewSignal();
    private bool flushing;
    private bool stopped;
    private SettingsWriteResult lastResult = new(true);

    public ApplicationSettingsWriter(IApplicationSettingsStore store, ApplicationSettings initial, TimeSpan? debounce = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        baseline = desired = ApplicationSettingsChanges.Capture(initial);
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(200);
        if (this.debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
    }

    public event Action<SettingsWriteResult>? WriteCompleted;

    public bool NeedsFlush
    {
        get { lock (gate) return runner is not null || !lastResult.Succeeded; }
    }

    public SettingsWriteResult LastResult
    {
        get { lock (gate) return lastResult; }
    }

    public void Enqueue(ApplicationSettings settings)
    {
        ApplicationSettings snapshot = ApplicationSettingsChanges.Capture(settings);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            pending = desired = snapshot;
            runner ??= Task.Run(ProcessAsync);
        }
    }

    public async Task<SettingsWriteResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        Task? work;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (runner is null && lastResult.Succeeded) return lastResult;
            flushing = true;
            flushSignal.TrySetResult();
            if (runner is null && !lastResult.Succeeded)
            {
                pending = desired;
                runner = Task.Run(ProcessAsync, CancellationToken.None);
            }
            work = runner;
        }
        if (work is not null) await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate) return lastResult;
    }

    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            pending = null;
            WriteCompleted = null;
            flushSignal.TrySetResult();
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            Task signal;
            bool wait;
            lock (gate)
            {
                if (pending is null || stopped)
                {
                    runner = null;
                    flushing = false;
                    flushSignal = NewSignal();
                    return;
                }
                signal = flushSignal.Task;
                wait = !flushing;
            }
            if (wait) await Task.WhenAny(Task.Delay(debounce), signal).ConfigureAwait(false);
            ApplicationSettings snapshot;
            lock (gate)
            {
                if (stopped) continue;
                snapshot = pending!;
                pending = null;
            }
            SettingsWriteResult result;
            try
            {
                store.SaveChanges(baseline, snapshot);
                baseline = snapshot;
                result = new(true);
            }
            catch (Exception exception)
            {
                // Do not include arbitrary provider exceptions: they might contain
                // a serialized image-host credential or other preference values.
                result = new(false, exception is ApplicationSettingsStoreException error
                    ? error.Message : $"无法保存本地应用设置（{exception.GetType().Name}）。");
            }
            Action<SettingsWriteResult>? callback;
            lock (gate)
            {
                lastResult = result;
                callback = WriteCompleted;
            }
            if (callback is null) continue;
            foreach (Action<SettingsWriteResult> observer in callback.GetInvocationList())
            {
                try { observer(result); }
                catch (Exception)
                {
                    // An observer disappearing during UI shutdown must not fault
                    // the drain task or strand the newest pending preferences.
                }
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record SettingsWriteResult(bool Succeeded, string? ErrorMessage = null);

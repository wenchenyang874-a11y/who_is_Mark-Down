namespace WhoIsMarkdown.App.Infrastructure;

/// <summary>
/// Shares an initialization task, not just its completed value. A caller's canceled
/// wait must not cancel other consumers. Failed creations are evicted for retry;
/// invalidating an older value must never remove a newer replacement for that key.
/// The factory starts on the caller's thread, which preserves native STA affinity.
/// </summary>
internal sealed class SharedInitializationCache<TKey, TValue> where TKey : notnull where TValue : class
{
    private readonly object gate = new();
    private readonly Dictionary<TKey, Entry> entries = [];

    public Task<TValue> GetOrCreate(TKey key, Func<Task<TValue>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Entry entry;
        lock (gate)
        {
            if (entries.TryGetValue(key, out Entry? existing)) return existing.Source.Task;
            entry = new Entry();
            entries.Add(key, entry);
        }

        _ = CompleteAsync(key, entry, factory);
        return entry.Source.Task;
    }

    public void Invalidate(TKey key, TValue value)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out Entry? entry) && ReferenceEquals(entry.Value, value)) entries.Remove(key);
        }
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
    }

    private async Task CompleteAsync(TKey key, Entry entry, Func<Task<TValue>> factory)
    {
        try
        {
            TValue value = await factory().ConfigureAwait(false);
            lock (gate) entry.Value = value;
            entry.Source.TrySetResult(value);
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                if (entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry)) entries.Remove(key);
            }

            if (exception is OperationCanceledException canceled) entry.Source.TrySetCanceled(canceled.CancellationToken);
            else entry.Source.TrySetException(exception);
        }
    }

    private sealed class Entry
    {
        public TaskCompletionSource<TValue> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TValue? Value { get; set; }
    }
}

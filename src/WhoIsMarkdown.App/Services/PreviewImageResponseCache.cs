using System.IO;
using WhoIsMarkdown.Core.Images;

namespace WhoIsMarkdown.App.Services;

internal sealed record CachedPreviewImage(byte[] Bytes, string ContentType, long EntryId);

/// <summary>
/// Bug fix: opening a remote preview image used a second HttpClient download even
/// though Chromium had just received it. This per-preview, bounded LRU observes
/// that original response; it never issues requests or blocks image rendering.
/// Native content acquisition starts on the UI thread, but the documented agile
/// response stream is read on a worker. No Window/control is retained here.
/// </summary>
internal sealed class PreviewImageResponseCache : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> redirects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> identities = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = new();
    private readonly int maximumBytes;
    private readonly int maximumEntries;
    private readonly TimeSpan timeout;
    private CancellationTokenSource lifetime = new();
    private long nextId;
    private int generation;
    private int active;
    private long bytes;
    private bool disposed;

    internal PreviewImageResponseCache(int maximumBytes = 32 * 1024 * 1024, int maximumEntries = 8,
        TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        this.maximumBytes = (int)Math.Min(maximumBytes, PreviewImageSaveService.MaximumImageBytes);
        this.maximumEntries = maximumEntries;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    internal long RetainedBytes { get { lock (gate) return bytes; } }

    internal void RememberRedirect(string source, string destination)
    {
        source = Key(source); destination = Key(destination);
        lock (gate)
        {
            if (disposed || source.Length > 16384 || destination.Length > 16384) return;
            if (redirects.Count >= 64) redirects.Clear();
            redirects[source] = destination;
        }
    }

    internal async Task<CachedPreviewImage?> TryReadAsync(string source, CancellationToken cancellationToken)
    {
        source = Key(source);
        Task<CachedPreviewImage?>? pending = null;
        if (source.Length > 16384) return null;
        lock (gate)
        {
            for (int redirect = 0; redirect < 6 && redirects.TryGetValue(source, out string? next); redirect++) source = next;
            if (!disposed) DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheIdentity, value: Identity(source));
            if (!disposed && entries.TryGetValue(source, out Entry? entry))
            {
                if (entry.Node is not null) { lru.Remove(entry.Node); lru.AddLast(entry.Node); }
                pending = entry.Completion.Task;
            }
        }
        if (pending is null) return null;
        try { return await pending.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(timeout.TotalMilliseconds, 250)), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheWaitExpired);
            return null;
        }
    }

    internal async Task CaptureAsync(string source, string contentType, long? declaredLength,
        Func<Task<Stream>> openContent, int window = 0)
    {
        source = Key(source);
        Entry entry;
        CancellationToken cancellationToken;
        lock (gate)
        {
            if (disposed) return;
            if (active >= 2 || source.Length > 16384 || declaredLength > maximumBytes)
            {
                DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheCaptureSkipped, window,
                    value: active >= 2 ? 1 : source.Length > 16384 ? 2 : 3);
                return;
            }
            if (entries.TryGetValue(source, out Entry? existing) && !existing.Completion.Task.IsCompleted) return;
            Remove(source);
            entry = new(Identity(source), generation);
            entries[source] = entry;
            cancellationToken = lifetime.Token;
            Interlocked.Increment(ref active);
        }

        Task? pending = null;
        using DiagnosticOperation? capture = DiagnosticLog.Current?.Begin(DiagnosticStage.ImageCacheCapture, window);
        DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheIdentity, window, entry.Id);
        try
        {
            // Invoke before the first await: WebView2's response is apartment-bound.
            Task<Stream> native = openContent();
            openContent = null!;
            pending = native;
            Stream? stream = await DiagnosticLog.MeasureAsync(DiagnosticStage.ImageCacheResponseStream,
                () => native.WaitAsync(timeout, cancellationToken), window).ConfigureAwait(false);
            pending = null;
            if (stream is null) { DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheCaptureSkipped, window, value: 4); capture?.Succeed(); return; }
            Task<byte[]> read = Task.Run(async () =>
            {
                using (stream)
                using (MemoryStream buffer = new())
                {
                    byte[] block = new byte[81920];
                    int count;
                    while ((count = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        if (buffer.Length + count > maximumBytes) throw new IOException("Image response exceeds cache capacity.");
                        await buffer.WriteAsync(block.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    }
                    return buffer.ToArray();
                }
            }, CancellationToken.None);
            pending = read;
            byte[] content = await DiagnosticLog.MeasureAsync(DiagnosticStage.ImageCacheResponseRead,
                () => read.WaitAsync(timeout, cancellationToken), window).ConfigureAwait(false);
            pending = null;
            CachedPreviewImage? cached = content.Length == 0 ? null : new(content, contentType, entry.Id);
            lock (gate)
            {
                if (disposed || generation != entry.Generation || !entries.TryGetValue(source, out Entry? current) || current != entry) { capture?.Cancel(); return; }
                if (cached is not null)
                {
                    entry.Node = lru.AddLast(source);
                    entry.Size = content.Length;
                    bytes += content.Length;
                    while (bytes > maximumBytes || lru.Count > maximumEntries) Remove(lru.First!.Value);
                    entry.Completion.TrySetResult(cached);
                    DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheStored, window, value: entry.Id);
                    DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheBytes, window, value: content.Length);
                }
            }
            capture?.Succeed();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or OperationCanceledException or TimeoutException)
        {
            capture?.Fail(exception);
        }
        finally
        {
            entry.Completion.TrySetResult(null);
            lock (gate)
            {
                if (entries.TryGetValue(source, out Entry? current) && current == entry && entry.Node is null) entries.Remove(source);
            }
            // WaitAsync timeout only stops this observer, not native I/O. Keep its
            // admission slot occupied until the actual operation ends; otherwise a
            // broken provider could accumulate unbounded background reads/streams.
            if (pending is not null)
                _ = pending.ContinueWith(completed =>
                {
                    try
                    {
                        if (completed is Task<Stream> response && response.Status == TaskStatus.RanToCompletion) response.Result?.Dispose();
                        _ = completed.Exception;
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                    { DiagnosticLog.Current?.Error(DiagnosticStage.ImageCacheResponseStream, exception, window); }
                    finally { Interlocked.Decrement(ref active); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else Interlocked.Decrement(ref active);
        }
    }

    private static string Key(string source) => Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https" ? new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri : source;

    internal void Clear()
    {
        CancellationTokenSource old;
        lock (gate)
        {
            generation++;
            foreach (Entry entry in entries.Values) entry.Completion.TrySetResult(null);
            entries.Clear(); redirects.Clear(); identities.Clear(); lru.Clear(); bytes = 0;
            old = lifetime; lifetime = new();
        }
        old.Cancel(); old.Dispose();
        DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheCleared);
    }

    private void Remove(string source)
    {
        if (!entries.Remove(source, out Entry? entry)) return;
        if (entry.Node is not null)
        {
            lru.Remove(entry.Node); bytes -= entry.Size;
            DiagnosticLog.Current?.Mark(DiagnosticStage.ImageCacheEvicted, value: entry.Id);
        }
        entry.Completion.TrySetResult(null);
    }

    // Session-local numeric identities connect capture, cache lookup and fallback
    // operations without putting URL strings or reversible URL hashes in the log.
    // Keep this metadata bounded independently of response-body retention.
    private long Identity(string source)
    {
        if (entries.TryGetValue(source, out Entry? entry)) return entry.Id;
        if (identities.TryGetValue(source, out long id)) return id;
        if (identities.Count >= 64)
        {
            string unused = identities.Keys.First(key => !entries.ContainsKey(key));
            identities.Remove(unused);
        }
        identities[source] = ++nextId;
        return nextId;
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        Clear();
        lifetime.Dispose();
    }

    private sealed class Entry(long id, int generation)
    {
        internal long Id { get; } = id;
        internal int Generation { get; } = generation;
        internal int Size { get; set; }
        internal LinkedListNode<string>? Node { get; set; }
        internal TaskCompletionSource<CachedPreviewImage?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

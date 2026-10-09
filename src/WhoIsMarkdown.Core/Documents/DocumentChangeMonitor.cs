namespace WhoIsMarkdown.Core.Documents;

/// <summary>
/// Bug fix: creating/enabling a directory watcher can block on an unavailable drive.
/// All handle operations run on a worker, with only the latest requested path kept.
/// Disposal invalidates pending work and drops subscribers immediately; it does not
/// claim to interrupt an operating-system call already blocked in a filesystem driver.
/// </summary>
public sealed class DocumentChangeMonitor : IDisposable
{
    private readonly object gate = new();
    private FileSystemWatcher? watcher;
    private string? requestedPath;
    private long generation;
    private bool workerRunning;
    private bool disposed;

    public event EventHandler? Changed;

    public void SetPath(string? path)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (string.Equals(path, requestedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            requestedPath = path;
            generation++;
            if (!workerRunning)
            {
                workerRunning = true;
                _ = Task.Run(UpdateWatcher);
            }
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? previous;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            generation++;
            Changed = null;
            previous = watcher;
            watcher = null;
        }

        if (previous is not null)
        {
            _ = Task.Run(previous.Dispose);
        }
    }

    private void UpdateWatcher()
    {
        while (true)
        {
            string? path;
            long version;
            FileSystemWatcher? previous;
            lock (gate)
            {
                if (disposed)
                {
                    workerRunning = false;
                    return;
                }

                path = requestedPath;
                version = generation;
                previous = watcher;
                watcher = null;
            }

            previous?.Dispose();
            FileSystemWatcher? created = CreateWatcher(path);
            lock (gate)
            {
                if (!disposed && version == generation)
                {
                    watcher = created;
                    workerRunning = false;
                    return;
                }
            }

            // A slow registration must never replace a newer document's watcher.
            created?.Dispose();
        }
    }

    private FileSystemWatcher? CreateWatcher(string? path)
    {
        FileSystemWatcher? created = null;
        try
        {
            if (path is null || Path.GetDirectoryName(path) is not { Length: > 0 } directory)
            {
                return null;
            }

            created = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                    | NotifyFilters.CreationTime | NotifyFilters.FileName,
            };
            created.Changed += OnChanged;
            created.Created += OnChanged;
            created.Deleted += OnChanged;
            created.Renamed += OnChanged;
            created.EnableRaisingEvents = true;
            return created;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            created?.Dispose();
            return null;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs)
    {
        EventHandler? handler;
        lock (gate)
        {
            handler = !disposed && ReferenceEquals(sender, watcher) ? Changed : null;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }
}

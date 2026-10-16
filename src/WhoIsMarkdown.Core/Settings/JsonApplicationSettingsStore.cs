using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhoIsMarkdown.Core.Settings;

/// <summary>
/// Persists small user preferences as UTF-8 JSON using same-directory replacement.
/// A malformed settings file falls back to defaults so it cannot prevent startup;
/// I/O and permission errors remain visible to the caller.
/// </summary>
public sealed class JsonApplicationSettingsStore : IApplicationSettingsStore
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object synchronizationRoot = new();
    private readonly string settingsPath;
    private readonly string? legacySettingsPath;
    private readonly string writerMutexName;

    public JsonApplicationSettingsStore(string settingsPath, string? legacySettingsPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = System.IO.Path.GetFullPath(settingsPath);
        this.legacySettingsPath = legacySettingsPath is null ? null : System.IO.Path.GetFullPath(legacySettingsPath);
        string identity = OperatingSystem.IsWindows() ? this.settingsPath.ToUpperInvariant() : this.settingsPath;
        writerMutexName = "WIMD.Settings." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public ApplicationSettings Load() => Load(sortRecentFiles: true);

    private ApplicationSettings Load(bool sortRecentFiles)
    {
        lock (synchronizationRoot)
        {
            string path = File.Exists(settingsPath) ? settingsPath : legacySettingsPath ?? settingsPath;
            if (!File.Exists(path))
            {
                return new ApplicationSettings();
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string json = StrictUtf8.GetString(bytes);
                ApplicationSettings? settings = JsonSerializer.Deserialize<ApplicationSettings>(
                    json,
                    SerializerOptions);
                ApplicationSettings loaded = settings ?? new ApplicationSettings();
                return sortRecentFiles ? loaded.Normalize() : loaded.NormalizeForPersistence();
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
            {
                return new ApplicationSettings();
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                throw CreateException("无法读取本地应用设置。", exception);
            }
        }
    }

    public void Save(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WithWriterLock(() => SaveCore(settings));
    }

    public void SaveChanges(ApplicationSettings baseline, ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(settings);
        // Read + merge + atomic replacement must share one cross-process lock;
        // an instance-only monitor cannot protect independent WIMD windows.
        WithWriterLock(() => SaveCore(ApplicationSettingsChanges.Merge(Load(sortRecentFiles: false), baseline, settings)));
    }

    private void WithWriterLock(Action write)
    {
        lock (synchronizationRoot)
        {
            using Mutex writerMutex = new(false, writerMutexName);
            bool acquired = false;
            try
            {
                try { acquired = writerMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw CreateException("等待其他窗口保存设置超时。", new TimeoutException());
                write();
            }
            finally
            {
                if (acquired) writerMutex.ReleaseMutex();
            }
        }
    }

    private void SaveCore(ApplicationSettings settings)
    {
        lock (synchronizationRoot)
        {
            string directory = System.IO.Path.GetDirectoryName(settingsPath)
                ?? throw new InvalidOperationException("设置文件缺少父目录。");
            string temporaryPath = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                Directory.CreateDirectory(directory);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                    settings.NormalizeForPersistence(),
                    SerializerOptions);

                using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(json);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, settingsPath, overwrite: true);
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                throw CreateException("无法保存本地应用设置。", exception);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private ApplicationSettingsStoreException CreateException(string message, Exception innerException)
    {
        return new ApplicationSettingsStoreException(
            $"{message} 路径：{settingsPath}",
            settingsPath,
            innerException);
    }

    private static bool IsExpectedFileException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            // Best-effort cleanup must not replace the original persistence error.
        }
    }
}

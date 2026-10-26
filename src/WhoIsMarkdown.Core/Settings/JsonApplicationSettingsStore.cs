using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WhoIsMarkdown.Core.Diagnostics;

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

    public ApplicationSettings Load()
        => CoreOperationTrace.Measure("SettingsReadTransaction", LoadTransaction);

    private ApplicationSettings LoadTransaction()
    {
        // Bug fix: on Windows even an atomic destination replacement can briefly
        // reject an unlocked reader. Readers must join the same cross-process
        // transaction lock; UI callers already perform this work off dispatcher.
        ApplicationSettings? result = null;
        WithWriterLock(() => result = Load(sortRecentFiles: true));
        return result!;
    }

    private ApplicationSettings Load(bool sortRecentFiles)
    {
        return WithInstanceLock(() =>
        {
            string path = CoreOperationTrace.Measure("SettingsLocate", () =>
                File.Exists(settingsPath) ? settingsPath : legacySettingsPath ?? settingsPath);
            if (!CoreOperationTrace.Measure("SettingsExists", () => File.Exists(path)))
            {
                CoreOperationTrace.Value("SettingsFallback", 1); // Missing; no path or setting value.
                return new ApplicationSettings();
            }

            try
            {
                byte[] bytes = CoreOperationTrace.Measure("SettingsBytesRead", () => File.ReadAllBytes(path));
                CoreOperationTrace.Value("SettingsReadBytes", bytes.LongLength);
                string json = CoreOperationTrace.Measure("SettingsDecode", () => StrictUtf8.GetString(bytes));
                ApplicationSettings? settings = CoreOperationTrace.Measure("SettingsDeserialize", () =>
                    JsonSerializer.Deserialize<ApplicationSettings>(json, SerializerOptions));
                if (settings is null) CoreOperationTrace.Value("SettingsFallback", 4);
                ApplicationSettings loaded = settings ?? new ApplicationSettings();
                return CoreOperationTrace.Measure("SettingsNormalize", () =>
                    sortRecentFiles ? loaded.Normalize() : loaded.NormalizeForPersistence());
            }
            catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
            {
                CoreOperationTrace.Value("SettingsFallback", exception is JsonException ? 2 : 3);
                return new ApplicationSettings();
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                throw CreateException("无法读取本地应用设置。", exception);
            }
        });
    }

    public void Save(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CoreOperationTrace.Measure("SettingsSaveTransaction", () => { WithWriterLock(() => SaveCore(settings)); return true; });
    }

    public void SaveChanges(ApplicationSettings baseline, ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(settings);
        // Read + merge + atomic replacement must share one cross-process lock;
        // an instance-only monitor cannot protect independent WIMD windows.
        CoreOperationTrace.Measure("SettingsMergeTransaction", () =>
        {
            WithWriterLock(() =>
            {
                ApplicationSettings latest = Load(sortRecentFiles: false);
                ApplicationSettings merged = CoreOperationTrace.Measure("SettingsMerge", () =>
                    ApplicationSettingsChanges.Merge(latest, baseline, settings));
                SaveCore(merged);
            });
            return true;
        });
    }

    private void WithWriterLock(Action write)
    {
        WithInstanceLock(() =>
        {
            using Mutex writerMutex = CoreOperationTrace.Measure("SettingsMutexCreate", () => new Mutex(false, writerMutexName));
            bool acquired = false;
            try
            {
                CoreOperationTrace.Measure("SettingsMutexWait", () =>
                {
                    try { acquired = writerMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { acquired = true; CoreOperationTrace.Value("SettingsMutexAbandoned", 1); }
                    if (!acquired) throw CreateException("等待其他窗口保存设置超时。", new TimeoutException());
                    return true;
                });
                write();
            }
            finally
            {
                if (acquired) CoreOperationTrace.Measure("SettingsMutexRelease", () => { writerMutex.ReleaseMutex(); return true; });
            }
            return true;
        });
    }

    private void SaveCore(ApplicationSettings settings)
    {
        WithInstanceLock(() =>
        {
            string directory = System.IO.Path.GetDirectoryName(settingsPath)
                ?? throw new InvalidOperationException("设置文件缺少父目录。");
            string temporaryPath = System.IO.Path.Combine(
                directory,
                $".{System.IO.Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                CoreOperationTrace.Measure("SettingsDirectoryCreate", () => Directory.CreateDirectory(directory));
                ApplicationSettings normalized = CoreOperationTrace.Measure("SettingsNormalize", settings.NormalizeForPersistence);
                byte[] json = CoreOperationTrace.Measure("SettingsSerialize", () =>
                    JsonSerializer.SerializeToUtf8Bytes(normalized, SerializerOptions));
                CoreOperationTrace.Value("SettingsWriteBytes", json.LongLength);

                FileStream stream = CoreOperationTrace.Measure("SettingsTemporaryOpen", () => new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough));
                try
                {
                    CoreOperationTrace.Measure("SettingsBytesWrite", () => { stream.Write(json); return true; });
                    CoreOperationTrace.Measure("SettingsDiskFlush", () => { stream.Flush(flushToDisk: true); return true; });
                }
                finally { CoreOperationTrace.Measure("SettingsTemporaryClose", () => { stream.Dispose(); return true; }); }

                CoreOperationTrace.Measure("SettingsAtomicReplace", () => { File.Move(temporaryPath, settingsPath, overwrite: true); return true; });
            }
            catch (Exception exception) when (IsExpectedFileException(exception))
            {
                throw CreateException("无法保存本地应用设置。", exception);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
            return true;
        });
    }

    // Measure acquisition separately, including reentrant calls, without changing
    // the existing lock order or holding a new lock while waiting for the mutex.
    private T WithInstanceLock<T>(Func<T> action)
    {
        bool acquired = false;
        try
        {
            CoreOperationTrace.Measure("SettingsInstanceLockWait", () => { Monitor.Enter(synchronizationRoot, ref acquired); return true; });
            return action();
        }
        finally { if (acquired) Monitor.Exit(synchronizationRoot); }
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
            CoreOperationTrace.Measure("SettingsTemporaryCleanup", () =>
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            });
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            // Best-effort cleanup must not replace the original persistence error.
        }
    }
}

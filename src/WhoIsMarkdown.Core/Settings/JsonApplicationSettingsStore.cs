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

    public JsonApplicationSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        this.settingsPath = System.IO.Path.GetFullPath(settingsPath);
    }

    public ApplicationSettings Load()
    {
        lock (synchronizationRoot)
        {
            if (!File.Exists(settingsPath))
            {
                return new ApplicationSettings();
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(settingsPath);
                string json = StrictUtf8.GetString(bytes);
                ApplicationSettings? settings = JsonSerializer.Deserialize<ApplicationSettings>(
                    json,
                    SerializerOptions);
                return (settings ?? new ApplicationSettings()).Normalize();
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
                    settings.Normalize(),
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

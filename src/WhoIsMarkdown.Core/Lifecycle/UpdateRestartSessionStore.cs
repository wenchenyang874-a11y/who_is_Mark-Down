using System.Text;
using System.Text.Json;

namespace WhoIsMarkdown.Core.Lifecycle;

/// <summary>
/// Exchanges one-time window snapshots between the installer-controlled shutdown
/// and the updated WIMD process. A short-lived request token prevents ordinary
/// application exits from creating restore data.
/// </summary>
public sealed class UpdateRestartSessionStore
{
    public const string RequestFileName = "update-restart.request";
    public const string SessionDirectoryName = "UpdateRestart";

    private const int MaximumRequestBytes = 256;
    private const int MaximumSnapshotBytes = 128 * 1024 * 1024;
    private const long MaximumTotalSnapshotBytes = 256L * 1024 * 1024;
    private const int MaximumWindows = 32;
    private static readonly TimeSpan MaximumRequestAge = TimeSpan.FromHours(2);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string sessionRootDirectory;

    public UpdateRestartSessionStore(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        string normalizedApplicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
        sessionRootDirectory = Path.Combine(normalizedApplicationDataDirectory, SessionDirectoryName);
        RequestFilePath = Path.Combine(normalizedApplicationDataDirectory, RequestFileName);
    }

    public string RequestFilePath { get; }

    public bool TrySaveRequestedWindow(string windowSessionId, UpdateRestartWindowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Guid.TryParseExact(windowSessionId, "N", out _))
        {
            return false;
        }

        string? requestToken = TryReadCurrentRequestToken();
        if (requestToken is null)
        {
            return false;
        }

        string sessionDirectory = GetSessionDirectory(requestToken);
        string snapshotPath = Path.Combine(sessionDirectory, $"{windowSessionId}.json");
        string temporaryPath = Path.Combine(sessionDirectory, $".{windowSessionId}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state.Normalize(), SerializerOptions);
            if (bytes.Length > MaximumSnapshotBytes)
            {
                return false;
            }

            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, snapshotPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return false;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public IReadOnlyList<UpdateRestartWindowState> ConsumeRequestedWindows()
    {
        RestartRequest? request = TryReadCurrentRequest();
        if (request is null)
        {
            TryDeleteFile(RequestFilePath);
            return [];
        }

        if (request is not { Phase: RestartRequestPhase.Restore })
        {
            return [];
        }

        string sessionDirectory = GetSessionDirectory(request.Value.Token);
        List<UpdateRestartWindowState> states = [];
        long totalSnapshotBytes = 0;
        try
        {
            if (!Directory.Exists(sessionDirectory))
            {
                return [];
            }

            foreach (string snapshotPath in Directory
                .EnumerateFiles(sessionDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumWindows))
            {
                long snapshotLength = new FileInfo(snapshotPath).Length;
                totalSnapshotBytes += snapshotLength;
                if (snapshotLength > MaximumSnapshotBytes
                    || totalSnapshotBytes > MaximumTotalSnapshotBytes)
                {
                    break;
                }

                UpdateRestartWindowState? state = TryReadSnapshot(snapshotPath);
                if (state is not null)
                {
                    states.Add(state);
                }
            }

            return states
                .OrderBy(state => state.SavedAtUtc)
                .ToArray();
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            return [];
        }
        finally
        {
            TryDeleteFile(RequestFilePath);
            TryDeleteSessionDirectory(sessionDirectory);
        }
    }

    private RestartRequest? TryReadCurrentRequest()
    {
        try
        {
            FileInfo requestFile = new(RequestFilePath);
            if (!requestFile.Exists
                || requestFile.Length is <= 0 or > MaximumRequestBytes)
            {
                return null;
            }

            DateTimeOffset lastWriteUtc = requestFile.LastWriteTimeUtc;
            TimeSpan age = DateTimeOffset.UtcNow - lastWriteUtc;
            if (age > MaximumRequestAge || age < TimeSpan.FromMinutes(-5))
            {
                return null;
            }

            string value = StrictUtf8.GetString(File.ReadAllBytes(RequestFilePath)).Trim();
            return TryParseRequest(value, out RestartRequest request) ? request : null;
        }
        catch (Exception exception) when (exception is DecoderFallbackException
            || IsExpectedFileException(exception))
        {
            return null;
        }
    }

    private string? TryReadCurrentRequestToken()
    {
        RestartRequest? request = TryReadCurrentRequest();
        return request is { Phase: RestartRequestPhase.Capture } ? request.Value.Token : null;
    }

    private static UpdateRestartWindowState? TryReadSnapshot(string path)
    {
        try
        {
            FileInfo snapshot = new(path);
            if (!snapshot.Exists || snapshot.Length is <= 0 or > MaximumSnapshotBytes)
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            _ = StrictUtf8.GetString(bytes);
            return JsonSerializer.Deserialize<UpdateRestartWindowState>(bytes, SerializerOptions)
                ?.Normalize();
        }
        catch (Exception exception) when (exception is JsonException
            or DecoderFallbackException
            || IsExpectedFileException(exception))
        {
            return null;
        }
    }

    private string GetSessionDirectory(string requestToken)
    {
        string path = Path.GetFullPath(Path.Combine(sessionRootDirectory, requestToken));
        string expectedPrefix = Path.GetFullPath(sessionRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新恢复会话路径超出应用数据目录。");
        }

        return path;
    }

    private static bool TryParseRequest(string value, out RestartRequest request)
    {
        request = default;
        int separatorIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        string phaseText = value[..separatorIndex];
        string token = value[(separatorIndex + 1)..];
        RestartRequestPhase phase = phaseText switch
        {
            "capture" => RestartRequestPhase.Capture,
            "restore" => RestartRequestPhase.Restore,
            _ => RestartRequestPhase.Unknown,
        };
        bool valid = phase is not RestartRequestPhase.Unknown
            && token.Length is >= 8 and <= 80
            && token.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_');
        if (valid)
        {
            request = new RestartRequest(phase, token);
        }

        return valid;
    }

    private static bool IsExpectedFileException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException
            or PathTooLongException;
    }

    private static void TryDeleteFile(string path)
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
            // Best-effort cleanup must never block normal WIMD startup or shutdown.
        }
    }

    private void TryDeleteSessionDirectory(string path)
    {
        try
        {
            string expectedPrefix = Path.GetFullPath(sessionRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path);
            if (normalizedPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(normalizedPath))
            {
                Directory.Delete(normalizedPath, recursive: true);
            }
        }
        catch (Exception exception) when (IsExpectedFileException(exception))
        {
            // Stale data is bounded by token directories and ignored after the marker expires.
        }
    }

    private readonly record struct RestartRequest(RestartRequestPhase Phase, string Token);

    private enum RestartRequestPhase
    {
        Unknown,
        Capture,
        Restore,
    }
}

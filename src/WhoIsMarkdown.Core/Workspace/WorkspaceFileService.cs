namespace WhoIsMarkdown.Core.Workspace;

/// <summary>
/// Performs workspace-scoped file operations. Every target is normalized and
/// checked against the selected root before touching disk; child reparse points are
/// rejected so junctions and symbolic links cannot escape the workspace boundary.
/// </summary>
public sealed class WorkspaceFileService : IWorkspaceFileService
{
    private static readonly HashSet<string> MarkdownExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".md",
        ".markdown",
    };

    private static readonly HashSet<string> ReservedDeviceNames = CreateReservedDeviceNames();

    public string Open(string rootPath)
    {
        string root = NormalizePath(rootPath);
        if (!Directory.Exists(root))
        {
            throw CreateException(
                WorkspaceFileOperation.Enumerate,
                root,
                new DirectoryNotFoundException("工作区文件夹不存在。"));
        }

        return root;
    }

    public IReadOnlyList<WorkspaceEntry> GetChildren(string rootPath, string directoryPath)
    {
        string root = Open(rootPath);
        string directory = ValidateExistingPath(root, directoryPath, allowRoot: true);
        if (!Directory.Exists(directory))
        {
            throw CreateException(
                WorkspaceFileOperation.Enumerate,
                directory,
                new DirectoryNotFoundException("工作区目录不存在。"));
        }

        try
        {
            List<WorkspaceEntry> entries = [];
            foreach (DirectoryInfo childDirectory in new DirectoryInfo(directory).EnumerateDirectories())
            {
                if (!childDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entries.Add(new WorkspaceEntry(
                        childDirectory.FullName,
                        childDirectory.Name,
                        IsDirectory: true));
                }
            }

            foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (!file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && MarkdownExtensions.Contains(file.Extension))
                {
                    entries.Add(new WorkspaceEntry(file.FullName, file.Name, IsDirectory: false));
                }
            }

            return entries
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw CreateException(WorkspaceFileOperation.Enumerate, directory, exception);
        }
    }

    public string CreateMarkdownFile(string rootPath, string parentDirectoryPath, string name)
    {
        string root = Open(rootPath);
        string parent = ValidateExistingDirectory(root, parentDirectoryPath);
        string fileName = NormalizeMarkdownFileName(name);
        string target = ValidateNewChildPath(root, parent, fileName);

        try
        {
            using FileStream stream = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Flush(flushToDisk: true);
            return target;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw CreateException(WorkspaceFileOperation.CreateFile, target, exception);
        }
    }

    public string CreateDirectory(string rootPath, string parentDirectoryPath, string name)
    {
        string root = Open(rootPath);
        string parent = ValidateExistingDirectory(root, parentDirectoryPath);
        string directoryName = ValidateLeafName(name);
        string target = ValidateNewChildPath(root, parent, directoryName);

        try
        {
            Directory.CreateDirectory(target);
            return target;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw CreateException(WorkspaceFileOperation.CreateDirectory, target, exception);
        }
    }

    public string Rename(string rootPath, string entryPath, string newName)
    {
        string root = Open(rootPath);
        string source = ValidateExistingPath(root, entryPath, allowRoot: false);
        bool isDirectory = Directory.Exists(source);
        string normalizedName = isDirectory
            ? ValidateLeafName(newName)
            : NormalizeRenamedMarkdownFileName(source, newName);
        string parent = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("工作区条目缺少父目录。");
        string target = ValidateNewChildPath(root, parent, normalizedName);

        try
        {
            if (isDirectory)
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }

            return target;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw CreateException(WorkspaceFileOperation.Rename, source, exception);
        }
    }

    public void Delete(string rootPath, string entryPath)
    {
        string root = Open(rootPath);
        string target = ValidateExistingPath(root, entryPath, allowRoot: false);

        try
        {
            if (Directory.Exists(target))
            {
                EnsureDirectoryTreeContainsNoReparsePoints(target);
                Directory.Delete(target, recursive: true);
            }
            else
            {
                File.Delete(target);
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            throw CreateException(WorkspaceFileOperation.Delete, target, exception);
        }
    }

    private static string ValidateExistingDirectory(string root, string directoryPath)
    {
        string directory = ValidateExistingPath(root, directoryPath, allowRoot: true);
        if (!Directory.Exists(directory))
        {
            throw CreateException(
                WorkspaceFileOperation.Enumerate,
                directory,
                new DirectoryNotFoundException("目标目录不存在。"));
        }

        return directory;
    }

    private static string ValidateExistingPath(string root, string path, bool allowRoot)
    {
        string candidate = NormalizePath(path);
        EnsureWithinRoot(root, candidate, allowRoot);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw CreateException(
                WorkspaceFileOperation.Enumerate,
                candidate,
                new FileNotFoundException("工作区条目不存在。", candidate));
        }

        EnsureNoChildReparsePoint(root, candidate);
        return candidate;
    }

    private static string ValidateNewChildPath(string root, string parent, string name)
    {
        string target = Path.GetFullPath(Path.Combine(parent, name));
        EnsureWithinRoot(root, target, allowRoot: false);
        EnsureNoChildReparsePoint(root, parent);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw CreateException(
                WorkspaceFileOperation.CreateFile,
                target,
                new IOException("同名文件或文件夹已存在。"));
        }

        return target;
    }

    private static void EnsureWithinRoot(string root, string candidate, bool allowRoot)
    {
        string relative = Path.GetRelativePath(root, candidate);
        bool isRoot = relative.Equals(".", StringComparison.Ordinal);
        bool escaped = Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal)
            || relative.StartsWith(
                string.Concat("..", Path.AltDirectorySeparatorChar),
                StringComparison.Ordinal);
        if (escaped || (isRoot && !allowRoot))
        {
            throw new WorkspaceFileException(
                WorkspaceFileOperation.Enumerate,
                candidate,
                "操作目标必须位于当前工作区内，且不能是工作区根目录。");
        }
    }

    private static void EnsureNoChildReparsePoint(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        if (relative.Equals(".", StringComparison.Ordinal))
        {
            return;
        }

        string current = root;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new WorkspaceFileException(
                    WorkspaceFileOperation.Enumerate,
                    current,
                    "为防止越过工作区边界，WIMD 不操作目录联接或符号链接中的内容。");
            }
        }
    }

    private static void EnsureDirectoryTreeContainsNoReparsePoints(string directoryPath)
    {
        // Recursive deletion must not cross a junction or symbolic-link boundary.
        // Scan without following links and reject the complete operation if any are
        // present, leaving the workspace unchanged for the user to inspect manually.
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(directoryPath);
        while (pendingDirectories.TryPop(out string? currentDirectory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new WorkspaceFileException(
                        WorkspaceFileOperation.Delete,
                        entry,
                        "待删除目录包含目录联接或符号链接；为防止越界，WIMD 已取消删除。"
                    );
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pendingDirectories.Push(entry);
                }
            }
        }
    }
    private static string NormalizeMarkdownFileName(string name)
    {
        string normalized = ValidateLeafName(name);
        string extension = Path.GetExtension(normalized);
        if (string.IsNullOrEmpty(extension))
        {
            return string.Concat(normalized, ".md");
        }

        if (!MarkdownExtensions.Contains(extension))
        {
            throw new ArgumentException("工作区编辑文件必须使用 .md 或 .markdown 扩展名。", nameof(name));
        }

        return normalized;
    }

    private static string NormalizeRenamedMarkdownFileName(string source, string newName)
    {
        string normalized = ValidateLeafName(newName);
        if (string.IsNullOrEmpty(Path.GetExtension(normalized)))
        {
            normalized = string.Concat(normalized, Path.GetExtension(source));
        }

        if (!MarkdownExtensions.Contains(Path.GetExtension(normalized)))
        {
            throw new ArgumentException("Markdown 文件必须保留 .md 或 .markdown 扩展名。", nameof(newName));
        }

        return normalized;
    }

    private static string ValidateLeafName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Trim();
        if (!string.Equals(normalized, name, StringComparison.Ordinal)
            || normalized is "." or ".."
            || normalized.EndsWith('.')
            || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(normalized), normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException("名称不能包含路径、首尾空格或 Windows 无效字符。", nameof(name));
        }

        string deviceName = Path.GetFileNameWithoutExtension(normalized);
        if (ReservedDeviceNames.Contains(deviceName))
        {
            throw new ArgumentException("该名称是 Windows 保留设备名，不能使用。", nameof(name));
        }

        return normalized;
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static HashSet<string> CreateReservedDeviceNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
        };
        for (int number = 1; number <= 9; number++)
        {
            names.Add($"COM{number}");
            names.Add($"LPT{number}");
        }

        return names;
    }

    private static bool IsExpectedFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;
    }

    private static WorkspaceFileException CreateException(
        WorkspaceFileOperation operation,
        string path,
        Exception exception)
    {
        return new WorkspaceFileException(
            operation,
            path,
            $"无法完成工作区文件操作“{path}”：{exception.Message}",
            exception);
    }
}

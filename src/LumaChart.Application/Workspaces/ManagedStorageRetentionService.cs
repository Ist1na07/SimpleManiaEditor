using System.Globalization;

namespace LumaChart.Application.Workspaces;

public sealed record ManagedStorageRetentionResult(
    int DirectoriesDeleted,
    long BytesFreed,
    IReadOnlyList<string> Failures);

/// <summary>
/// Bounded retention for application-owned trees that otherwise grow for the
/// lifetime of an installation. Like <see cref="ManagedStorageCleanupService"/>
/// it only deletes direct children of an explicitly supplied root, never follows
/// reparse points, and never touches a protected (in-use) path.
/// </summary>
public static class ManagedStorageRetentionService
{
    /// <summary>
    /// Children touched more recently than this are never removed, even to meet
    /// the size cap: another process or a just-joined room may still be using them.
    /// </summary>
    public static readonly TimeSpan MinimumRetainedAge = TimeSpan.FromDays(1);

    /// <summary>
    /// Removes direct child directories of <paramref name="root"/> whose name
    /// starts with <paramref name="namePrefix"/>: first every unprotected child
    /// idle for longer than <paramref name="maximumAge"/>, then the oldest
    /// unprotected children until the total is at most
    /// <paramref name="maximumTotalBytes"/>.
    /// </summary>
    public static ManagedStorageRetentionResult PruneDirectories(
        string root,
        string namePrefix,
        IEnumerable<string>? protectedPaths,
        DateTimeOffset now,
        TimeSpan maximumAge,
        long maximumTotalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(namePrefix);
        var failures = new List<string>();
        var deleted = 0;
        long freed = 0;

        string fullRoot;
        try
        {
            fullRoot = ValidateRoot(root);
            if (!Directory.Exists(fullRoot) ||
                File.GetAttributes(fullRoot).HasFlag(FileAttributes.ReparsePoint))
                return new ManagedStorageRetentionResult(0, 0, failures);
        }
        catch (Exception exception) when (IsFileSystemError(exception) || exception is InvalidOperationException)
        {
            failures.Add($"{root}: {exception.Message}");
            return new ManagedStorageRetentionResult(0, 0, failures);
        }

        var guarded = (protectedPaths ?? [])
            .Select(TryFullPath)
            .Where(path => path != null)
            .Cast<string>()
            .ToArray();

        var candidates = new List<(DirectoryInfo Directory, DateTime LastActivityUtc, long Bytes, bool Protected)>();
        try
        {
            foreach (var directory in new DirectoryInfo(fullRoot).EnumerateDirectories(
                         namePrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!directory.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) ||
                    directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                var full = TryFullPath(directory.FullName);
                if (full == null || !IsInside(fullRoot, full)) continue;
                var isProtected = guarded.Any(path => IsSameOrInside(full, path) || IsSameOrInside(path, full));
                var (lastActivity, bytes) = Measure(directory);
                candidates.Add((directory, lastActivity, bytes, isProtected));
            }
        }
        catch (Exception exception) when (IsFileSystemError(exception))
        {
            failures.Add($"{fullRoot}: {exception.Message}");
            return new ManagedStorageRetentionResult(0, 0, failures);
        }

        var nowUtc = now.UtcDateTime;
        var total = candidates.Sum(candidate => candidate.Bytes);
        foreach (var candidate in candidates.OrderBy(candidate => candidate.LastActivityUtc))
        {
            if (candidate.Protected) continue;
            var idle = nowUtc - candidate.LastActivityUtc;
            if (idle < MinimumRetainedAge) continue;
            var expired = idle > maximumAge;
            var overCap = maximumTotalBytes >= 0 && total > maximumTotalBytes;
            if (!expired && !overCap) continue;
            try
            {
                candidate.Directory.Delete(recursive: true);
                deleted++;
                freed += candidate.Bytes;
                total -= candidate.Bytes;
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                failures.Add($"{candidate.Directory.FullName}: {exception.Message}");
            }
        }

        return new ManagedStorageRetentionResult(deleted, freed, failures);
    }

    /// <summary>
    /// Removes <c>session_&lt;pid&gt;</c> scratch directories left behind by client
    /// processes that are no longer running (a crash or a killed process skips
    /// the normal shutdown cleanup). The current process and any name that is
    /// not exactly <c>session_</c> followed by a process ID are left alone.
    /// </summary>
    public static ManagedStorageRetentionResult CleanupOrphanedSessionDirectories(
        string scratchRoot,
        int currentProcessId,
        Func<int, bool> isProcessRunning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentNullException.ThrowIfNull(isProcessRunning);
        var failures = new List<string>();
        var deleted = 0;
        long freed = 0;

        string fullRoot;
        try
        {
            fullRoot = ValidateRoot(scratchRoot);
            if (!Directory.Exists(fullRoot) ||
                File.GetAttributes(fullRoot).HasFlag(FileAttributes.ReparsePoint))
                return new ManagedStorageRetentionResult(0, 0, failures);
        }
        catch (Exception exception) when (IsFileSystemError(exception) || exception is InvalidOperationException)
        {
            failures.Add($"{scratchRoot}: {exception.Message}");
            return new ManagedStorageRetentionResult(0, 0, failures);
        }

        IEnumerable<DirectoryInfo> sessions;
        try
        {
            sessions = new DirectoryInfo(fullRoot)
                .EnumerateDirectories("session_*", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception exception) when (IsFileSystemError(exception))
        {
            failures.Add($"{fullRoot}: {exception.Message}");
            return new ManagedStorageRetentionResult(0, 0, failures);
        }

        foreach (var session in sessions)
        {
            if (!TryParseSessionProcessId(session.Name, out var processId) ||
                processId == currentProcessId ||
                session.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            bool running;
            try
            {
                running = isProcessRunning(processId);
            }
            catch
            {
                running = true;
            }
            if (running) continue;
            try
            {
                var (_, bytes) = Measure(session);
                session.Delete(recursive: true);
                deleted++;
                freed += bytes;
            }
            catch (Exception exception) when (IsFileSystemError(exception))
            {
                failures.Add($"{session.FullName}: {exception.Message}");
            }
        }

        return new ManagedStorageRetentionResult(deleted, freed, failures);
    }

    public static bool TryParseSessionProcessId(string? name, out int processId)
    {
        processId = 0;
        const string prefix = "session_";
        if (string.IsNullOrEmpty(name) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length == prefix.Length)
            return false;
        var digits = name.AsSpan(prefix.Length);
        foreach (var character in digits)
            if (character is < '0' or > '9') return false;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out processId) &&
               processId > 0;
    }

    private static (DateTime LastActivityUtc, long Bytes) Measure(DirectoryInfo directory)
    {
        var lastActivity = directory.LastWriteTimeUtc;
        long bytes = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        foreach (var file in directory.EnumerateFiles("*", options))
        {
            bytes += file.Length;
            if (file.LastWriteTimeUtc > lastActivity) lastActivity = file.LastWriteTimeUtc;
        }
        return (lastActivity, bytes);
    }

    private static string ValidateRoot(string path)
    {
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to prune a volume root.");
        return root;
    }

    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return null; }
    }

    private static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != "." && IsRelativeDescendant(relative);
    }

    private static bool IsSameOrInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || IsRelativeDescendant(relative);
    }

    private static bool IsRelativeDescendant(string relative) =>
        !Path.IsPathRooted(relative) &&
        relative != ".." &&
        !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static bool IsFileSystemError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException;
}

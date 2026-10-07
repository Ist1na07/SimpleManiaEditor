using LumaChart.Core.Localization;

namespace LumaChart.Application.Workspaces;

public sealed record ManagedStorageCleanupTarget(
    string RootPath,
    IReadOnlyCollection<string>? ProtectedPaths = null);

public sealed record ManagedStorageCleanupResult(
    int FilesDeleted,
    int DirectoriesDeleted,
    long BytesFreed,
    IReadOnlyList<string> Failures);

/// <summary>
/// Deletes only descendants of explicitly supplied application-owned roots and
/// never follows reparse points. Protected live workspaces are retained.
/// </summary>
public static class ManagedStorageCleanupService
{
    public static ManagedStorageCleanupResult Clean(IEnumerable<ManagedStorageCleanupTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var files = 0;
        var directories = 0;
        long bytes = 0;
        var failures = new List<string>();

        foreach (var target in targets)
        {
            try
            {
                var root = ValidateRoot(target.RootPath);
                if (!Directory.Exists(root)) continue;
                if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
                {
                    failures.Add(LocalizationCatalog.Format("已跳过重解析根目录：{0}", root));
                    continue;
                }
                var protectedPaths = (target.ProtectedPaths ?? [])
                    .Select(TryFullPath)
                    .Where(path => path != null && IsSameOrInside(root, path))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                CleanDirectoryContents(root, root, protectedPaths, ref files, ref directories, ref bytes, failures);
            }
            catch (Exception exception)
            {
                failures.Add($"{target.RootPath}: {exception.Message}");
            }
        }

        return new ManagedStorageCleanupResult(files, directories, bytes, failures);
    }

    private static void CleanDirectoryContents(
        string root,
        string directory,
        IReadOnlyCollection<string> protectedPaths,
        ref int files,
        ref int directories,
        ref long bytes,
        List<string> failures)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
        catch (Exception exception)
        {
            failures.Add($"{directory}: {exception.Message}");
            return;
        }

        foreach (var entry in entries)
        {
            var fullPath = TryFullPath(entry);
            if (fullPath == null || !IsSameOrInside(root, fullPath))
            {
                failures.Add(LocalizationCatalog.Format("已跳过不安全路径：{0}", entry));
                continue;
            }
            if (IsInsideAny(fullPath, protectedPaths)) continue;

            FileAttributes attributes;
            try { attributes = File.GetAttributes(fullPath); }
            catch (Exception exception)
            {
                failures.Add($"{fullPath}: {exception.Message}");
                continue;
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                failures.Add(LocalizationCatalog.Format("已跳过重解析点：{0}", fullPath));
                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                CleanDirectoryContents(root, fullPath, protectedPaths,
                    ref files, ref directories, ref bytes, failures);
                if (IsAncestorOfAny(fullPath, protectedPaths)) continue;
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(fullPath).Any())
                    {
                        Directory.Delete(fullPath, false);
                        directories++;
                    }
                }
                catch (Exception exception) { failures.Add($"{fullPath}: {exception.Message}"); }
                continue;
            }

            try
            {
                var length = new FileInfo(fullPath).Length;
                File.Delete(fullPath);
                files++;
                bytes += length;
            }
            catch (Exception exception) { failures.Add($"{fullPath}: {exception.Message}"); }
        }
    }

    private static string ValidateRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("清理目录不能为空。", nameof(path));
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝清理磁盘根目录。");
        return root;
    }

    private static string? TryFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return null; }
    }

    private static bool IsInsideAny(string candidate, IEnumerable<string> roots) =>
        roots.Any(root => IsSameOrInside(root, candidate));

    private static bool IsAncestorOfAny(string candidate, IEnumerable<string> paths) =>
        paths.Any(path => IsSameOrInside(candidate, path));

    private static bool IsSameOrInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }
}

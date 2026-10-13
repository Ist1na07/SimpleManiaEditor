namespace LumaChart.Application.Workspaces;

/// <summary>Game installations are read-only sources, including paths reached through junctions.</summary>
public static class OsuFileSafetyPolicy
{
    public const string ReadOnlyMessage = "osu! 本体及 Songs 目录仅用于读取。请保存、导出或删除独立创作目录中的副本。";

    public static bool IsProtectedPath(string path)
    {
        var full = Path.GetFullPath(path);
        return HasGameAncestor(full) || HasGameAncestor(ResolvePhysicalPath(full));
    }

    public static void RequireWritable(string path)
    {
        if (IsProtectedPath(path)) throw new InvalidOperationException(ReadOnlyMessage);
    }

    public static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(ResolvePhysicalPath(root), ResolvePhysicalPath(path));
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    // Resolve each existing component, rather than just the selected root: an
    // ordinary folder may contain a junction and the final file may not exist yet.
    public static string ResolvePhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var component in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) continue;
            if ((info.Attributes & FileAttributes.ReparsePoint) == 0) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new IOException("无法验证目录链接的目标，已停止文件操作。");
            current = Path.GetFullPath(target.FullName);
        }
        return Path.GetFullPath(current);
    }

    private static bool HasGameAncestor(string path)
    {
        var directory = new DirectoryInfo(path);
        for (; directory != null; directory = directory.Parent)
        {
            if (directory.Name.Equals("Songs", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(directory.FullName, "osu!.exe")) ||
                File.Exists(Path.Combine(directory.FullName, "osu!.db"))) return true;
        }
        return false;
    }
}

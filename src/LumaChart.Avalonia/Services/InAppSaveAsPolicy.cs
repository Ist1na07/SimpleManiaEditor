namespace LumaChart.Avalonia.Services;

/// <summary>
/// Save As rules for a platform whose save picker cannot return a writable
/// file-system path. The copy has to stay inside an app-owned folder, so the
/// name the user types is the only free part and must not be able to leave it.
/// </summary>
public static class InAppSaveAsPolicy
{
    /// <summary>
    /// Where the copy goes: the chart's own folder when that folder is inside the
    /// app-owned root, so the audio and images it references stay beside it;
    /// otherwise the root itself.
    ///
    /// The containment test matters. A tab bound to an online room reads from
    /// app-internal room-document storage, and a chart opened from a picker
    /// staging area reads from the cache. Saving a copy back into either place
    /// would put the user's work somewhere the library never lists and cache
    /// management may clear.
    /// </summary>
    public static string ResolveDirectory(string? sourcePath, string inAppSaveAsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inAppSaveAsRoot);
        var root = Path.GetFullPath(inAppSaveAsRoot);
        var directory = string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return root;

        var candidate = Path.GetFullPath(directory);
        return IsInsideRoot(candidate, root) ? candidate : root;
    }

    private static bool IsInsideRoot(string candidate, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        var trimmedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(trimmedCandidate, trimmedRoot, comparison) ||
               trimmedCandidate.StartsWith(
                   trimmedRoot + Path.DirectorySeparatorChar,
                   comparison);
    }

    /// <summary>
    /// Returns the file name to use, or null when the request cannot be kept
    /// inside the offered folder. A separator or a relative segment would place
    /// the chart somewhere the app-owned library cannot reopen it.
    /// </summary>
    public static string? SanitizeFileName(string? requested)
    {
        var name = requested?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        // GetFileName strips anything the platform reads as a directory part,
        // including a Windows drive prefix that contains no separator at all.
        if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)) return null;
        return name is "." or ".." ? null : name;
    }
}

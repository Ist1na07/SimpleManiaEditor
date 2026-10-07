namespace LumaChart.Core.IO;

/// <summary>
/// Validates chart-relative resource references (audio, cover, background and
/// key sounds). A chart may only name files inside its own folder: rooted,
/// drive-relative, UNC and parent-directory references let an imported chart
/// make the editor read, or an exported package embed, arbitrary local files.
/// </summary>
public static class ChartResourceReference
{
    /// <summary>
    /// Normalizes separators to '/', removes "." segments and rejects rooted,
    /// drive/stream (':'), UNC, parent-directory and control-character
    /// references. An empty input is valid and stays empty.
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        var candidate = (value ?? string.Empty).Trim().Replace('\\', '/');
        if (candidate.Length == 0) return true;
        if (candidate.StartsWith('/') ||
            candidate.Contains(':') ||
            candidate.Any(char.IsControl))
            return false;

        var parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == "..")) return false;
        var kept = parts.Where(part => part != ".").ToArray();
        if (kept.Length == 0) return false;
        normalized = string.Join('/', kept);
        return true;
    }

    /// <summary>
    /// Import-side policy: a safe reference is kept in normalized form, an
    /// unsafe one is cleared so no later consumer can resolve it outside the
    /// chart folder. Normal charts only ever use plain relative names.
    /// </summary>
    public static string SanitizeImported(string? value) =>
        TryNormalize(value, out var normalized) ? normalized : string.Empty;

    /// <summary>
    /// Resolves <paramref name="reference"/> to an existing file strictly inside
    /// <paramref name="chartDirectory"/>.
    /// </summary>
    public static bool TryResolve(string? reference, string chartDirectory, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(chartDirectory) ||
            !TryNormalize(reference, out var normalized) ||
            normalized.Length == 0)
            return false;
        try
        {
            var root = Path.GetFullPath(chartDirectory);
            var candidate = Path.GetFullPath(Path.Combine(
                root,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsStrictDescendant(root, candidate) || !File.Exists(candidate)) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals(".", StringComparison.Ordinal) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}

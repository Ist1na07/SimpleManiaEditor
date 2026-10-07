namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Keeps the per-folder expansion state outside recyclable item controls.
/// A recycled card must never carry the previous folder's expanded state into
/// its next data context.
/// </summary>
public sealed class WindowsLibraryFolderExpansionState
{
    private readonly HashSet<string> expandedFolderPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsExpanded(string? folderPath)
    {
        var normalized = Normalize(folderPath);
        return normalized != null && expandedFolderPaths.Contains(normalized);
    }

    public void SetExpanded(string? folderPath, bool expanded)
    {
        var normalized = Normalize(folderPath);
        if (normalized == null) return;
        if (expanded)
            expandedFolderPaths.Add(normalized);
        else
            expandedFolderPaths.Remove(normalized);
    }

    private static string? Normalize(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;
        try
        {
            return Path.GetFullPath(folderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }
}

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Preserves the WPF 1.1.0 window-drop routing contract. A batch containing any
/// chart or archive opens those files and ignores audio; an audio-only batch
/// imports only its first supported audio file.
/// </summary>
public static class WindowsFileDropPolicy
{
    private static readonly string[] AudioExtensions =
        [".mp3", ".ogg", ".wav", ".flac", ".m4a", ".aac"];

    public static bool IsSupportedExistingFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(path) &&
        (WindowsSingleInstancePolicy.IsSupportedOpenPath(path) || IsAudioPath(path));

    public static bool IsAudioPath(string path) =>
        AudioExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase);

    public static WindowsFileDropPlan CreatePlan(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var existing = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Cast<string>()
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // WPF imports every archive before opening any loose chart, regardless
        // of Explorer's file-order. Preserve that ordering because an archive
        // may refresh the current library before the loose charts are opened.
        var archives = existing
            .Where(path =>
                Path.GetExtension(path).Equals(".mcz", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".osz", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var charts = existing
            .Where(path =>
                Path.GetExtension(path).Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".osu", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var chartsAndArchives = archives.Concat(charts).ToArray();
        return chartsAndArchives.Length > 0
            ? new WindowsFileDropPlan(chartsAndArchives, AudioPath: null)
            : new WindowsFileDropPlan([], existing.FirstOrDefault(IsAudioPath));
    }
}

public sealed record WindowsFileDropPlan(
    IReadOnlyList<string> ChartAndArchivePaths,
    string? AudioPath)
{
    public bool HasSupportedFiles =>
        ChartAndArchivePaths.Count > 0 || AudioPath != null;
}

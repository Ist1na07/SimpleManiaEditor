using System.Security.Cryptography;

namespace LumaChart.Application.Workspaces;

public sealed record ChartAssetImportResult(string FileName, string DestinationPath, bool Created);

public sealed record ChartNoteSoundCandidate(string FileName, string FullPath);

/// <summary>Copies user-selected chart assets into a chart root using safe, flat names.</summary>
public static class ChartAssetImportService
{
    private static readonly HashSet<string> SupportedAudioExtensions = new(
        [".wav", ".ogg", ".mp3", ".flac", ".m4a", ".aac"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedImageExtensions = new(
        [".png", ".jpg", ".jpeg", ".webp", ".bmp"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns whether a path has one of the supported Note Key sound extensions.</summary>
    public static bool IsSupportedNoteSoundFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        SupportedAudioExtensions.Contains(Path.GetExtension(path));

    public static bool IsSupportedAudioFile(string? path) => IsSupportedNoteSoundFile(path);

    public static bool IsSupportedImageFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        SupportedImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Enumerates supported audio files directly inside the supplied directories.
    /// Repeated directory aliases and file paths are collapsed while distinct files
    /// with the same display name are retained for the caller to disambiguate.
    /// </summary>
    public static IReadOnlyList<ChartNoteSoundCandidate> EnumerateNoteSoundCandidates(
        IEnumerable<string?> directoryPaths)
    {
        ArgumentNullException.ThrowIfNull(directoryPaths);
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var candidates = new Dictionary<string, ChartNoteSoundCandidate>(pathComparer);
        var directories = new HashSet<string>(pathComparer);

        foreach (var directoryPath in directoryPaths)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) continue;
            string directory;
            try { directory = Path.GetFullPath(directoryPath); }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }
            if (!directories.Add(directory) || !Directory.Exists(directory)) continue;

            string[] files;
            try { files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files)
            {
                if (!IsSupportedNoteSoundFile(file)) continue;
                var fullPath = Path.GetFullPath(file);
                var fileName = Path.GetFileName(fullPath);
                if (string.IsNullOrWhiteSpace(fileName)) continue;
                candidates.TryAdd(fullPath, new ChartNoteSoundCandidate(fileName, fullPath));
            }
        }

        return candidates.Values
            .OrderBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.FileName, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.FullPath, StringComparer.Ordinal)
            .ToArray();
    }

    public static ChartAssetImportResult ImportNoteSound(
        string chartPath,
        string sourcePath,
        string? eventualChartPath = null)
        => ImportAudio(chartPath, sourcePath, eventualChartPath);

    public static ChartAssetImportResult ImportAudio(
        string chartPath,
        string sourcePath,
        string? eventualChartPath = null,
        string? preferredFileName = null)
        => ImportAsset(
            chartPath,
            sourcePath,
            eventualChartPath,
            preferredFileName,
            SupportedAudioExtensions,
            "请选择音频文件。",
            "音频仅支持 WAV、OGG、MP3、FLAC、M4A 或 AAC。",
            "目标音频扩展名不受支持。");

    public static ChartAssetImportResult ImportImage(
        string chartPath,
        string sourcePath,
        string? eventualChartPath = null,
        string? preferredFileName = null)
        => ImportAsset(
            chartPath,
            sourcePath,
            eventualChartPath,
            preferredFileName,
            SupportedImageExtensions,
            "请选择图片文件。",
            "图片仅支持 PNG、JPG、JPEG、WEBP 或 BMP。",
            "目标图片扩展名不受支持。");

    /// <summary>
    /// Resolves a chart-relative asset from the recoverable editing cache first,
    /// then from the source chart directory. Newly imported assets therefore work
    /// before the first save without weakening existing source-reference behavior.
    /// </summary>
    public static string? ResolveAssetPath(
        string workingChartPath,
        string? sourceChartPath,
        string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            if (Path.IsPathRooted(reference))
            {
                var rooted = Path.GetFullPath(reference);
                return File.Exists(rooted) ? rooted : null;
            }

            reference = reference.Trim().Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            if (reference.Length == 0 ||
                reference.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => part is "." or "..") ||
                reference.Any(char.IsControl))
                return null;

            var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(workingChartPath));
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                var workingCandidate = Path.GetFullPath(Path.Combine(workingDirectory, reference));
                if (File.Exists(workingCandidate)) return workingCandidate;
            }

            if (string.IsNullOrWhiteSpace(sourceChartPath)) return null;
            var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceChartPath));
            if (string.IsNullOrWhiteSpace(sourceDirectory)) return null;
            var sourceCandidate = Path.GetFullPath(Path.Combine(sourceDirectory, reference));
            return File.Exists(sourceCandidate) ? sourceCandidate : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Locates a missing chart-relative asset in known library roots. A directory
    /// containing a byte-identical copy of the source chart wins immediately; an
    /// otherwise unique filename match is accepted as a compatibility fallback.
    /// The caller is expected to copy the returned file into the editing cache.
    /// </summary>
    public static string? FindRelatedAsset(
        string workingChartPath,
        string? sourceChartPath,
        string? reference,
        IEnumerable<string?> searchRoots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference)) return null;

        string assetFileName;
        string? comparisonChart;
        string? chartFileName;
        try
        {
            assetFileName = Path.GetFileName(reference.Trim());
            if (string.IsNullOrWhiteSpace(assetFileName) ||
                assetFileName is "." or ".." ||
                !IsSupportedAudioFile(assetFileName))
                return null;
            comparisonChart = !string.IsNullOrWhiteSpace(sourceChartPath) && File.Exists(sourceChartPath)
                ? Path.GetFullPath(sourceChartPath)
                : File.Exists(workingChartPath)
                    ? Path.GetFullPath(workingChartPath)
                    : null;
            chartFileName = !string.IsNullOrWhiteSpace(sourceChartPath)
                ? Path.GetFileName(sourceChartPath)
                : Path.GetFileName(workingChartPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var visited = new HashSet<string>(pathComparer);
        string? uniqueFallback = null;
        foreach (var rootValue in searchRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rootValue)) continue;
            string root;
            try { root = Path.GetFullPath(rootValue); }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or NotSupportedException)
            {
                continue;
            }
            if (!Directory.Exists(root) || !visited.Add(root)) continue;

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(root);
            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pendingDirectories.Pop();
                string[] childDirectories;
                try
                {
                    childDirectories = Directory.GetDirectories(directory);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    childDirectories = [];
                }
                foreach (var child in childDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pendingDirectories.Push(child);
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                var assetCandidate = Path.Combine(directory, assetFileName);
                if (!File.Exists(assetCandidate)) continue;
                if (comparisonChart != null &&
                    !string.IsNullOrWhiteSpace(chartFileName))
                {
                    var chartCandidate = Path.Combine(directory, chartFileName);
                    if (File.Exists(chartCandidate) && FilesEqual(comparisonChart, chartCandidate))
                        return Path.GetFullPath(assetCandidate);
                }

                var fullCandidate = Path.GetFullPath(assetCandidate);
                if (uniqueFallback == null)
                    uniqueFallback = fullCandidate;
                else if (!pathComparer.Equals(uniqueFallback, fullCandidate))
                    uniqueFallback = string.Empty;
            }
        }

        return string.IsNullOrWhiteSpace(uniqueFallback) ? null : uniqueFallback;
    }

    private static ChartAssetImportResult ImportAsset(
        string chartPath,
        string sourcePath,
        string? eventualChartPath,
        string? preferredFileName,
        IReadOnlySet<string> supportedExtensions,
        string missingSelectionMessage,
        string unsupportedSourceMessage,
        string unsupportedDestinationMessage)
    {
        if (string.IsNullOrWhiteSpace(chartPath))
            throw new ArgumentException("当前谱面还没有可用的编辑目录。", nameof(chartPath));
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException(missingSelectionMessage, nameof(sourcePath));

        var chart = Path.GetFullPath(chartPath);
        var chartRoot = Path.GetDirectoryName(chart)
                        ?? throw new ArgumentException("谱面路径没有有效目录。", nameof(chartPath));
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("找不到所选资源文件。", source);
        if (!supportedExtensions.Contains(Path.GetExtension(source)))
            throw new NotSupportedException(unsupportedSourceMessage);

        Directory.CreateDirectory(chartRoot);
        var preferredName = string.IsNullOrWhiteSpace(preferredFileName)
            ? Path.GetFileName(source)
            : Path.GetFileName(preferredFileName.Trim());
        if (string.IsNullOrWhiteSpace(preferredName) || preferredName is "." or "..")
            throw new InvalidDataException("资源文件名无效。");
        if (!supportedExtensions.Contains(Path.GetExtension(preferredName)))
            throw new NotSupportedException(unsupportedDestinationMessage);

        var eventualRoot = string.IsNullOrWhiteSpace(eventualChartPath)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(eventualChartPath));
        var fileName = SelectAvailableName(chartRoot, eventualRoot, preferredName, source);
        var destination = Path.Combine(chartRoot, fileName);
        if (File.Exists(destination) && FilesEqual(source, destination))
            return new ChartAssetImportResult(fileName, destination, false);
        File.Copy(source, destination, false);
        return new ChartAssetImportResult(fileName, destination, true);
    }

    private static string SelectAvailableName(
        string chartRoot,
        string? eventualRoot,
        string preferredName,
        string source)
    {
        var stem = Path.GetFileNameWithoutExtension(preferredName);
        var extension = Path.GetExtension(preferredName);
        for (var suffix = 0; ; suffix++)
        {
            var candidateName = suffix == 0 ? preferredName : $"{stem}_{suffix}{extension}";
            var editingCandidate = Path.Combine(chartRoot, candidateName);
            var editingAvailable = !File.Exists(editingCandidate) && !Directory.Exists(editingCandidate) ||
                                   File.Exists(editingCandidate) && FilesEqual(source, editingCandidate);
            if (!editingAvailable) continue;
            if (eventualRoot == null || PathsEqual(eventualRoot, chartRoot)) return candidateName;
            var eventualCandidate = Path.Combine(eventualRoot, candidateName);
            var eventualAvailable = !File.Exists(eventualCandidate) && !Directory.Exists(eventualCandidate) ||
                                    File.Exists(eventualCandidate) && FilesEqual(source, eventualCandidate);
            if (eventualAvailable) return candidateName;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool FilesEqual(string left, string right)
    {
        try
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length) return false;
            using var leftStream = File.OpenRead(left);
            using var rightStream = File.OpenRead(right);
            return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
        }
        catch
        {
            return false;
        }
    }
}

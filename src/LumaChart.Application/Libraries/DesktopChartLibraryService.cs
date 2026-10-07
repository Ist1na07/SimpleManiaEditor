using LumaChart.Core.Localization;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using LumaChart.Application.Workspaces;

namespace LumaChart.Application.Libraries;

public enum DesktopLibrarySort
{
    AddedDescending,
    AddedAscending,
    TitleAscending,
    TitleDescending
}

public readonly record struct DesktopLibraryScanProgress(
    int Processed,
    int Total,
    string CurrentFile);

public sealed class DesktopLibraryChartEntry
{
    private string? searchText;

    public required string ChartPath { get; init; }
    public required string FolderPath { get; init; }
    public required string RelativePath { get; init; }
    public string Format { get; init; } = "Malody";
    public string Title { get; init; } = "Untitled";
    public string TitleOriginal { get; init; } = string.Empty;
    public string Artist { get; init; } = "Unknown Artist";
    public string ArtistOriginal { get; init; } = string.Empty;
    public string Creator { get; init; } = string.Empty;
    public string Version { get; init; } = "Normal";
    public int KeyCount { get; init; } = 4;
    public string AudioFile { get; init; } = string.Empty;
    public string? CoverPath { get; init; }
    public DateTime AddedTime { get; init; }
    public DateTime ModifiedTime { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(TitleOriginal) ? Title : TitleOriginal;
    public string DisplayArtist => string.IsNullOrWhiteSpace(ArtistOriginal) ? Artist : ArtistOriginal;
    public string FormatBadgeText => Format.StartsWith("osu", StringComparison.OrdinalIgnoreCase)
        ? "OSU"
        : "MALODY";
    public string Details => $"{DisplayArtist}  ·  {Version}  ·  {KeyCount}K  ·  {Format}";
    public string PickerTitle => $"{DisplayTitle}  [{Version}]";
    public string PickerDetails => $"{KeyCount}K  ·  {Format}  ·  {Creator}";
    public string AddedText => AddedTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string SearchText => searchText ??=
        ($"{Title}\n{TitleOriginal}\n{Artist}\n{ArtistOriginal}\n{Creator}\n{Version}\n" +
         $"{Format}\n{FormatBadgeText}\n{RelativePath}").ToLowerInvariant();
}

public sealed class DesktopLibraryFolderEntry
{
    public required string FolderPath { get; init; }
    public required string RelativeFolderPath { get; init; }
    public required IReadOnlyList<DesktopLibraryChartEntry> Charts { get; init; }
    public required string DisplayTitle { get; init; }
    public required string DisplayArtist { get; init; }
    public required string CoverPath { get; init; }
    public required DateTime AddedTime { get; init; }
    public required DateTime ModifiedTime { get; init; }
    public required int SongCount { get; init; }
    public required string Formats { get; init; }
    public required string SearchText { get; init; }
    public required string FolderSearchText { get; init; }

    public string FolderName => Path.GetFileName(
        FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public int ChartCount => Charts.Count;
    public string AddedText => AddedTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    public string Details => SongCount > 1
        ? LocalizationCatalog.Format("{0} 首歌曲  ·  {1} 张谱面  ·  {2}", SongCount, ChartCount, Formats)
        : LocalizationCatalog.Format("{0}  ·  {1} 张谱面  ·  {2}", DisplayArtist, ChartCount, Formats);
    public string FormatBadgeText
    {
        get
        {
            var formats = Charts.Select(chart => chart.FormatBadgeText)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return formats.Length == 1 ? formats[0] : "MIXED";
        }
    }
}

public sealed record DesktopLibraryFolderView(
    DesktopLibraryFolderEntry Folder,
    IReadOnlyList<DesktopLibraryChartEntry> VisibleCharts);

/// <summary>
/// UI-free desktop chart library index. Its parsing, grouping, filtering and sorting rules
/// intentionally match the Windows/WPF 1.1.0 library instead of fully importing every chart.
/// </summary>
public static class DesktopChartLibraryService
{
    public static async Task<IReadOnlyList<DesktopLibraryChartEntry>> ScanAsync(
        string root,
        IProgress<DesktopLibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        root = LocalLibraryRootResolver.Resolve(root).ResolvedRoot;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var files = await Task.Run(() => Directory.EnumerateFiles(root, "*", options)
            .Where(path => Path.GetExtension(path).Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".osu", StringComparison.OrdinalIgnoreCase))
            .ToArray(), cancellationToken).ConfigureAwait(false);
        var entries = new ConcurrentBag<DesktopLibraryChartEntry>();
        var processed = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8)
        }, (path, _) =>
        {
            try
            {
                var entry = Path.GetExtension(path).Equals(".osu", StringComparison.OrdinalIgnoreCase)
                    ? ReadOsu(path, root)
                    : ReadMalody(path, root);
                if (entry != null) entries.Add(entry);
            }
            catch
            {
                // One unreadable chart must not prevent the remaining library from loading.
            }

            var current = Interlocked.Increment(ref processed);
            if (current == files.Length || current % 32 == 0)
                progress?.Report(new DesktopLibraryScanProgress(current, files.Length, path));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return entries.ToArray();
    }

    public static IReadOnlyList<DesktopLibraryFolderEntry> GroupByFolder(
        IEnumerable<DesktopLibraryChartEntry> charts,
        string root)
    {
        ArgumentNullException.ThrowIfNull(charts);
        root = LocalLibraryRootResolver.Resolve(root).ResolvedRoot;
        return charts.GroupBy(chart => GroupingFolder(chart, root), StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateFolderEntry(group.Key, root, group.ToList()))
            .ToArray();
    }

    public static IReadOnlyList<DesktopLibraryFolderView> FilterAndSort(
        IEnumerable<DesktopLibraryFolderEntry> folders,
        string? query,
        DesktopLibrarySort sort)
    {
        ArgumentNullException.ThrowIfNull(folders);
        query = query?.Trim().ToLowerInvariant() ?? string.Empty;
        var filtered = folders.Select(folder =>
        {
            IReadOnlyList<DesktopLibraryChartEntry> visible =
                query.Length == 0 || folder.FolderSearchText.Contains(query, StringComparison.Ordinal)
                    ? folder.Charts
                    : folder.Charts.Where(chart => chart.SearchText.Contains(query, StringComparison.Ordinal))
                        .ToArray();
            return new DesktopLibraryFolderView(folder, visible);
        }).Where(view => view.VisibleCharts.Count > 0);

        return sort switch
        {
            DesktopLibrarySort.AddedAscending => filtered
                .OrderBy(view => view.Folder.AddedTime)
                .ToArray(),
            DesktopLibrarySort.TitleAscending => filtered
                .OrderBy(view => view.Folder.DisplayTitle, StringComparer.CurrentCulture)
                .ToArray(),
            DesktopLibrarySort.TitleDescending => filtered
                .OrderByDescending(view => view.Folder.DisplayTitle, StringComparer.CurrentCulture)
                .ToArray(),
            _ => filtered
                .OrderByDescending(view => view.Folder.AddedTime)
                .ToArray()
        };
    }

    private static string GroupingFolder(DesktopLibraryChartEntry chart, string root)
    {
        var folder = new DirectoryInfo(Path.GetFullPath(chart.FolderPath));
        if (chart.Format.Equals("Malody", StringComparison.OrdinalIgnoreCase) &&
            folder.Name.All(char.IsDigit) &&
            folder.Parent is { } parent &&
            parent.Name.StartsWith("_song_", StringComparison.OrdinalIgnoreCase) &&
            IsInsideOrEqual(parent.FullName, root))
            return Path.GetFullPath(parent.FullName);
        return folder.FullName;
    }

    private static DesktopLibraryFolderEntry CreateFolderEntry(
        string folder,
        string root,
        List<DesktopLibraryChartEntry> charts)
    {
        charts = charts.OrderBy(chart => chart.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(chart => chart.KeyCount)
            .ThenBy(chart => chart.Version, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var representative = charts.FirstOrDefault(chart => !string.IsNullOrWhiteSpace(chart.CoverPath)) ?? charts[0];
        var songKeys = charts.Select(chart =>
                $"{chart.Title}\n{chart.TitleOriginal}\n{chart.Artist}\n{chart.ArtistOriginal}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var folderName = Path.GetFileName(
            folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName)) folderName = new DirectoryInfo(folder).Name;
        var relative = Path.GetRelativePath(root, folder);
        if (relative == ".") relative = folderName;
        var added = SafeDirectoryCreationTime(folder, charts.Min(chart => chart.AddedTime));
        var displayTitle = songKeys.Length == 1 ? representative.DisplayTitle : folderName;
        var displayArtist = songKeys.Length == 1 ? representative.DisplayArtist : "多曲谱包";
        var formats = string.Join(" / ", charts.Select(chart => chart.Format)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var folderSearch = $"{folder}\n{relative}\n{displayTitle}\n{displayArtist}".ToLowerInvariant();
        var search = string.Join('\n', charts.Select(chart => chart.SearchText).Append(folderSearch))
            .ToLowerInvariant();
        return new DesktopLibraryFolderEntry
        {
            FolderPath = folder,
            RelativeFolderPath = relative,
            Charts = charts,
            DisplayTitle = displayTitle,
            DisplayArtist = displayArtist,
            CoverPath = representative.CoverPath ?? string.Empty,
            AddedTime = added,
            ModifiedTime = charts.Max(chart => chart.ModifiedTime),
            SongCount = songKeys.Length,
            Formats = formats,
            SearchText = search,
            FolderSearchText = folderSearch
        };
    }

    private static DesktopLibraryChartEntry? ReadMalody(string path, string root)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            32 * 1024,
            FileOptions.SequentialScan);
        using var json = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        var rootElement = json.RootElement;
        if (!rootElement.TryGetProperty("meta", out var meta)) return null;
        var song = meta.TryGetProperty("song", out var songElement) ? songElement : default;
        var mode = meta.TryGetProperty("mode_ext", out var modeElement) ? modeElement : default;
        var info = new FileInfo(path);
        var folder = info.DirectoryName!;
        var cover = String(meta, "cover");
        if (string.IsNullOrWhiteSpace(cover)) cover = String(meta, "background");
        var audio = String(song, "file");
        if (string.IsNullOrWhiteSpace(audio) &&
            rootElement.TryGetProperty("note", out var notes) &&
            notes.ValueKind == JsonValueKind.Array)
        {
            foreach (var note in notes.EnumerateArray())
            {
                if (note.ValueKind != JsonValueKind.Object || Integer(note, "type", 0) != 1) continue;
                audio = String(note, "sound");
                if (!string.IsNullOrWhiteSpace(audio)) break;
            }
        }

        return new DesktopLibraryChartEntry
        {
            ChartPath = info.FullName,
            FolderPath = folder,
            RelativePath = Path.GetRelativePath(root, info.FullName),
            Format = "Malody",
            Title = String(song, "title", "Untitled"),
            TitleOriginal = String(song, "titleorg"),
            Artist = String(song, "artist", "Unknown Artist"),
            ArtistOriginal = String(song, "artistorg"),
            Creator = String(meta, "creator"),
            Version = String(meta, "version", "Normal"),
            KeyCount = Math.Clamp(Integer(mode, "column", 4), 1, 18),
            AudioFile = audio,
            CoverPath = ResolveResource(folder, cover),
            AddedTime = SafeCreationTime(info),
            ModifiedTime = info.LastWriteTime
        };
    }

    private static DesktopLibraryChartEntry ReadOsu(string path, string root)
    {
        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var difficulty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        var background = string.Empty;
        var sawGeneral = false;
        var sawMetadata = false;
        var sawDifficulty = false;
        var sawEvents = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                sawGeneral |= section.Equals("General", StringComparison.OrdinalIgnoreCase);
                sawMetadata |= section.Equals("Metadata", StringComparison.OrdinalIgnoreCase);
                sawDifficulty |= section.Equals("Difficulty", StringComparison.OrdinalIgnoreCase);
                sawEvents |= section.Equals("Events", StringComparison.OrdinalIgnoreCase);
                if ((section.Equals("TimingPoints", StringComparison.OrdinalIgnoreCase) ||
                     section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase)) &&
                    sawGeneral && sawMetadata && sawDifficulty && sawEvents)
                    break;
                continue;
            }

            if (section.Equals("Events", StringComparison.OrdinalIgnoreCase) &&
                line.StartsWith("0,0,", StringComparison.Ordinal))
            {
                var first = line.IndexOf('"');
                var second = first < 0 ? -1 : line.IndexOf('"', first + 1);
                if (first >= 0 && second > first) background = line[(first + 1)..second];
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon < 1) continue;
            var target = section.ToLowerInvariant() switch
            {
                "general" => general,
                "metadata" => metadata,
                "difficulty" => difficulty,
                _ => null
            };
            if (target != null) target[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var info = new FileInfo(path);
        var folder = info.DirectoryName!;
        return new DesktopLibraryChartEntry
        {
            ChartPath = info.FullName,
            FolderPath = folder,
            RelativePath = Path.GetRelativePath(root, info.FullName),
            Format = "osu!mania",
            Title = metadata.GetValueOrDefault("Title", "Untitled"),
            TitleOriginal = metadata.GetValueOrDefault("TitleUnicode", string.Empty),
            Artist = metadata.GetValueOrDefault("Artist", "Unknown Artist"),
            ArtistOriginal = metadata.GetValueOrDefault("ArtistUnicode", string.Empty),
            Creator = metadata.GetValueOrDefault("Creator", string.Empty),
            Version = metadata.GetValueOrDefault("Version", "Normal"),
            KeyCount = Math.Clamp(ParseInt(difficulty.GetValueOrDefault("CircleSize"), 4), 1, 18),
            AudioFile = general.GetValueOrDefault("AudioFilename", string.Empty),
            CoverPath = ResolveResource(folder, background),
            AddedTime = SafeCreationTime(info),
            ModifiedTime = info.LastWriteTime
        };
    }

    private static string String(JsonElement element, string name, string fallback = "") =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int Integer(JsonElement element, string name, int fallback) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    /// <summary>
    /// Resolves a cover or background strictly inside the chart's folder.
    /// Absolute, UNC and ".." references are rejected before File.Exists: a
    /// scanned chart must not make the library probe arbitrary paths or open
    /// SMB connections (which would leak NTLM credentials).
    /// </summary>
    private static string? ResolveResource(string folder, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            return DesktopChartAssetLocalizer.ResolveReference(
                reference,
                Path.Combine(folder, "chart.mc"));
        }
        catch
        {
            return null;
        }
    }

    private static DateTime SafeCreationTime(FileInfo info) =>
        info.CreationTime.Year >= 1980 ? info.CreationTime : info.LastWriteTime;

    private static DateTime SafeDirectoryCreationTime(string folder, DateTime fallback)
    {
        try
        {
            var value = Directory.GetCreationTime(folder);
            return value.Year >= 1980 ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool IsInsideOrEqual(string candidate, string root)
    {
        candidate = Path.GetFullPath(candidate);
        root = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}

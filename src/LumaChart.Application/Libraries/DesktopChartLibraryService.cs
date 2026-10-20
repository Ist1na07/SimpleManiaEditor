using LumaChart.Core.Localization;
using LumaChart.Core.Models;
using System.Diagnostics;
using LumaChart.Core.Timing;
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

/// <summary>Timing/count evidence for one incremental refresh pass.</summary>
public sealed record DesktopLibraryIndexRefreshResult(
    IReadOnlyList<DesktopLibraryChartEntry> Charts,
    int Enumerated,
    int Parsed,
    int Removed,
    double EnumerateMs,
    double ParseMs);

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
    /// <summary>Playable length in seconds: the last hit object's END minus the
    /// first hit object's START (osu!'s FilterCriteria.Length semantics), never
    /// the audio duration. Null means "not indexed yet", not zero.</summary>
    public double? Length { get; set; }
    /// <summary>BPM of the first uninherited timing point (osu's initial BPM). The
    /// index only knows this for charts with a timing section; null means
    /// "unknown", not zero.</summary>
    public double? Bpm { get; set; }
    /// <summary>osu! Metadata Source; empty when the format has none.</summary>
    public string Source { get; init; } = string.Empty;
    public string AudioFile { get; init; } = string.Empty;
    public string? CoverPath { get; init; }
    public DateTime AddedTime { get; init; }
    public DateTime ModifiedTime { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(TitleOriginal) ? Title : TitleOriginal;
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayArtist => string.IsNullOrWhiteSpace(ArtistOriginal) ? Artist : ArtistOriginal;
    [System.Text.Json.Serialization.JsonIgnore]
    public string FormatBadgeText => Format.StartsWith("osu", StringComparison.OrdinalIgnoreCase)
        ? "OSU"
        : "MALODY";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Details => $"{DisplayArtist}  ·  {Version}  ·  {KeyCount}K  ·  {Format}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string PickerTitle => $"{DisplayTitle}  [{Version}]";
    [System.Text.Json.Serialization.JsonIgnore]
    public string PersonalSummary { get; set; } = "";
    /// <summary>Personal tags/note/state, refreshed together with the summary.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public MapperAnnotation? PersonalAnnotation { get; set; }
    /// <summary>Lower-cased personal text for free-text search (tags, note, state).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PersonalSearchText { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string PickerDetails => $"{KeyCount}K  ·  {Format}  ·  {Creator}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string AddedText => AddedTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    [System.Text.Json.Serialization.JsonIgnore]
    public string SearchText => searchText ??=
        ($"{Title}\n{TitleOriginal}\n{Artist}\n{ArtistOriginal}\n{Creator}\n{Version}\n" +
         $"{Format}\n{FormatBadgeText}\n{RelativePath}").ToLowerInvariant();
}

public sealed class DesktopLibraryFolderEntry
{
    private string? searchText;

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
    public required string FolderSearchText { get; init; }

    /// <summary>
    /// Lower-cased search blob for the folder and every chart in it. It is built
    /// on first use: arranging a 46k-chart index must not concatenate a string
    /// per song before anything actually searches.
    /// </summary>
    public string SearchText
    {
        get => searchText ??= string.Join('\n', Charts.Select(chart => chart.SearchText).Append(FolderSearchText)).ToLowerInvariant();
        init => searchText = value;
    }

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
    public static Task<IReadOnlyList<DesktopLibraryChartEntry>> ScanAsync(
        string root,
        IProgress<DesktopLibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        DesktopLibraryMetadataCache? metadataCache = null)
    {
        root = LocalLibraryRootResolver.Resolve(root).ResolvedRoot;
        return ScanRootAsync(root, progress, cancellationToken, metadataCache);
    }

    private static async Task<IReadOnlyList<DesktopLibraryChartEntry>> ScanRootAsync(
        string root,
        IProgress<DesktopLibraryScanProgress>? progress,
        CancellationToken cancellationToken,
        DesktopLibraryMetadataCache? metadataCache)
    {
        // Enumeration stays off the caller's thread exactly like the old scan:
        // a UI caller must never pay the directory walk synchronously.
        var files = await Task.Run(() => EnumerateChartFiles(root), cancellationToken).ConfigureAwait(false);
        return await ScanFilesAsync(root, files, progress, cancellationToken, metadataCache).ConfigureAwait(false);
    }

    /// <summary>Fast directory enumeration only. This is the part an incremental
    /// refresh must pay for on every click; file parsing is skipped for rows whose
    /// cached size and mtime still match.</summary>
    public static string[] EnumerateChartFiles(string root)
    {
        root = LocalLibraryRootResolver.Resolve(root).ResolvedRoot;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        return Directory.EnumerateFiles(root, "*", options)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.StartsWith(".sme-", StringComparison.OrdinalIgnoreCase) || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            .Where(path => Path.GetExtension(path).Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".osu", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static async Task<IReadOnlyList<DesktopLibraryChartEntry>> ScanFilesAsync(
        string root,
        string[] files,
        IProgress<DesktopLibraryScanProgress>? progress,
        CancellationToken cancellationToken,
        DesktopLibraryMetadataCache? metadataCache)
    {
        var entries = new ConcurrentBag<DesktopLibraryChartEntry>();
        var processed = 0;
        // Parse in bounded chunks and flush each chunk. A first import therefore
        // becomes visible to the persisted index while the rest still parses;
        // the UI itself already shows the cached snapshot.
        const int batchSize = 256;
        for (var offset = 0; offset < files.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, files.Length - offset);
            var chunk = new string[count];
            Array.Copy(files, offset, chunk, 0, count);
            await Parallel.ForEachAsync(chunk, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8)
            }, (path, _) =>
            {
                try
                {
                    var entry = metadataCache == null ? ReadChart(path, root) : metadataCache.Read(path, root);
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
            // Keep first-import progress durable in coarse batches. Committing
            // every 256-row chunk made a 1% incremental refresh pay ~80 SQLite
            // fsyncs for only 200 changed rows.
            if (metadataCache is { PendingCount: >= 1024 }) metadataCache.Flush();
        }
        metadataCache?.Flush();
        return entries.ToArray();
    }

    /// <summary>
    /// osu-model incremental refresh: enumerate once, parse only new or
    /// mtime/size-changed charts, and remove rows whose files disappeared. A
    /// non-changed 20k library therefore costs close to enumeration only.
    /// </summary>
    public static async Task<DesktopLibraryIndexRefreshResult> RefreshIncrementalAsync(
        string root,
        MapperLibraryStore store,
        IProgress<DesktopLibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        root = LocalLibraryRootResolver.Resolve(root).ResolvedRoot;
        var cache = new DesktopLibraryMetadataCache(store);
        cache.LoadPersistedHeaders();
        var enumerateWatch = Stopwatch.StartNew();
        var files = await Task.Run(() => EnumerateChartFiles(root), cancellationToken).ConfigureAwait(false);
        enumerateWatch.Stop();
        var removed = cache.ForgetMissingUnderRoot(root, files);
        var readsBefore = cache.HeaderReads;
        var parseWatch = Stopwatch.StartNew();
        var charts = await ScanFilesAsync(root, files, progress, cancellationToken, cache).ConfigureAwait(false);
        cache.Flush();
        parseWatch.Stop();
        return new DesktopLibraryIndexRefreshResult(charts, files.Length,
            (int)Math.Min(int.MaxValue, cache.HeaderReads - readsBefore), removed.Count,
            enumerateWatch.Elapsed.TotalMilliseconds, parseWatch.Elapsed.TotalMilliseconds);
    }

    public static DesktopLibraryChartEntry? ReadChart(string path, string root) =>
        Path.GetExtension(path).Equals(".osu", StringComparison.OrdinalIgnoreCase)
            ? ReadOsu(path, root)
            : Path.GetExtension(path).Equals(".mc", StringComparison.OrdinalIgnoreCase) ? ReadMalody(path, root) : null;

    public static IReadOnlyList<DesktopLibraryFolderEntry> GroupByFolder(
        IEnumerable<DesktopLibraryChartEntry> charts,
        string root, bool useCachedTimes = false)
    {
        ArgumentNullException.ThrowIfNull(charts);
        root = LocalLibraryRootResolver.Resolve(root).ResolvedRoot;
        // Charts of one mapset share a folder, so the grouping folder is resolved
        // once per distinct folder instead of once per chart. This keeps a 46k
        // index from repeating the same path normalisation thousands of times.
        var groups = new Dictionary<string, List<DesktopLibraryChartEntry>>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chart in charts)
        {
            if (!resolved.TryGetValue(chart.FolderPath, out var folder))
                resolved[chart.FolderPath] = folder = GroupingFolder(chart, root);
            if (!groups.TryGetValue(folder, out var group)) groups[folder] = group = [];
            group.Add(chart);
        }
        return groups.Select(pair => CreateFolderEntry(pair.Key, root, pair.Value, useCachedTimes)).ToArray();
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

    /// <summary>
    /// Evaluates a parsed osu!-style query over cached rows. The query is parsed
    /// once by the caller; the per-chart comparison allocates nothing.
    /// </summary>
    public static HashSet<string> SearchPaths(IEnumerable<DesktopLibraryChartEntry> charts, DesktopLibrarySearch search)
    {
        ArgumentNullException.ThrowIfNull(charts);
        ArgumentNullException.ThrowIfNull(search);
        var paths = charts is ICollection<DesktopLibraryChartEntry> known
            ? new HashSet<string>(known.Count, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chart in charts)
            if (search.Matches(chart)) paths.Add(chart.ChartPath);
        return paths;
    }

    /// <summary>Same evaluation as <see cref="SearchPaths"/> but returns the rows.</summary>
    public static IReadOnlyList<DesktopLibraryChartEntry> SearchCharts(IEnumerable<DesktopLibraryChartEntry> charts, DesktopLibrarySearch search)
    {
        ArgumentNullException.ThrowIfNull(charts);
        ArgumentNullException.ThrowIfNull(search);
        var matches = new List<DesktopLibraryChartEntry>();
        foreach (var chart in charts)
            if (search.Matches(chart)) matches.Add(chart);
        return matches;
    }

    private static string GroupingFolder(DesktopLibraryChartEntry chart, string root)
    {
        var folderPath = chart.FolderPath;
        if (!Path.IsPathFullyQualified(folderPath)) folderPath = Path.GetFullPath(folderPath);
        // Entries parsed from disk already carry a normalised folder path; only
        // Malody's numeric _song_ children need to be folded into their parent.
        if (!chart.Format.Equals("Malody", StringComparison.OrdinalIgnoreCase)) return folderPath;
        var folder = new DirectoryInfo(folderPath);
        if (folder.Name.All(char.IsDigit) &&
            folder.Parent is { } parent &&
            parent.Name.StartsWith("_song_", StringComparison.OrdinalIgnoreCase) &&
            IsInsideOrEqual(parent.FullName, root))
            return Path.GetFullPath(parent.FullName);
        return folder.FullName;
    }

    private static DesktopLibraryFolderEntry CreateFolderEntry(
        string folder,
        string root,
        List<DesktopLibraryChartEntry> charts, bool useCachedTimes)
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
        var added = useCachedTimes ? charts.Min(chart => chart.AddedTime) : SafeDirectoryCreationTime(folder, charts.Min(chart => chart.AddedTime));
        var displayTitle = songKeys.Length == 1 ? representative.DisplayTitle : folderName;
        var displayArtist = songKeys.Length == 1 ? representative.DisplayArtist : "多曲谱包";
        var formats = string.Join(" / ", charts.Select(chart => chart.Format)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var folderSearch = $"{folder}\n{relative}\n{displayTitle}\n{displayArtist}".ToLowerInvariant();
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
            FolderSearchText = folderSearch
        };
    }

    private static DesktopLibraryChartEntry? ReadMalody(string path, string root)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
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

        var fallbackBpm = Number(song, "bpm", double.NaN);
        var (timingMap, malodyBpm) = ReadMalodyTiming(rootElement, fallbackBpm);
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
            Length = ReadMalodyLength(rootElement, timingMap),
            Bpm = malodyBpm,
            AudioFile = audio,
            CoverPath = ResolveResource(folder, cover),
            AddedTime = SafeCreationTime(info),
            ModifiedTime = info.LastWriteTime
        };
    }

    private static DesktopLibraryChartEntry? ReadOsu(string path, string root)
    {
        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var difficulty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        var background = string.Empty;
        double? bpm = null;
        double? firstNoteStartMs = null;
        double? lastNoteEndMs = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            if (section.Equals("TimingPoints", StringComparison.OrdinalIgnoreCase))
            {
                if (bpm is null && TryReadFirstTimingPoint(line, out var beatLength) && beatLength > 0)
                    bpm = 60000.0 / beatLength;
                continue;
            }

            if (section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase))
            {
                // osu!'s FilterCriteria.Length is playable time: the last object's
                // END minus the first object's START. A hold's end is its
                // objectParams end time, not the next object's start.
                if (TryReadHitObjectRange(line, out var startMs, out var endMs))
                {
                    if (firstNoteStartMs is null || startMs < firstNoteStartMs) firstNoteStartMs = startMs;
                    if (lastNoteEndMs is null || endMs > lastNoteEndMs) lastNoteEndMs = endMs;
                }
                continue;
            }

            if (section.Equals("Events", StringComparison.OrdinalIgnoreCase) &&
                line.StartsWith("0,0,", StringComparison.Ordinal))
            {
                var quoteStart = line.IndexOf('"');
                var quoteEnd = quoteStart < 0 ? -1 : line.IndexOf('"', quoteStart + 1);
                if (quoteStart >= 0 && quoteEnd > quoteStart) background = line[(quoteStart + 1)..quoteEnd];
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

        // osu!'s omitted Mode defaults to standard (0), not mania.
        if (!general.TryGetValue("Mode", out var mode) || mode != "3") return null;
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
            Length = firstNoteStartMs is { } first && lastNoteEndMs is { } last
                ? Math.Max(0, last - first) / 1000.0
                : null,
            Bpm = bpm,
            Source = metadata.GetValueOrDefault("Source", string.Empty),
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

    private static double Number(JsonElement element, string name, double fallback) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : fallback;

    private static bool TryReadMalodyBeat(JsonElement item, string name, out BeatPosition beat)
    {
        beat = BeatPosition.Zero;
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 3)
            return false;
        if (!TryReadWholeInt(value[0], out var bar) ||
            !TryReadWholeInt(value[1], out var numerator) ||
            !TryReadWholeInt(value[2], out var denominator) || denominator == 0)
            return false;
        try
        {
            beat = new BeatPosition(bar, numerator, denominator);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadWholeInt(JsonElement value, out int number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        if (value.TryGetInt32(out number)) return true;
        if (!value.TryGetDouble(out var real) || !double.IsFinite(real) || real != Math.Round(real) ||
            real is < int.MinValue or > int.MaxValue) return false;
        number = (int)real;
        return true;
    }

    /// <summary>Malody timing rows are BPM events; the first BPM is the cached
    /// header BPM, matching the .osu first-uninherited rule.</summary>
    private static (TimingMap? Map, double? Bpm) ReadMalodyTiming(JsonElement rootElement, double fallbackBpm)
    {
        var timings = new List<TimingEvent>();
        if (rootElement.TryGetProperty("time", out var times) && times.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in times.EnumerateArray())
            {
                if (!TryReadMalodyBeat(item, "beat", out var beat)) continue;
                var bpm = Number(item, "bpm", 120);
                if (!double.IsFinite(bpm) || bpm <= 0) continue;
                timings.Add(new TimingEvent { Beat = beat, Bpm = bpm, Delay = Number(item, "delay", 0) });
            }
        }
        if (timings.Count == 0)
        {
            if (!double.IsFinite(fallbackBpm) || fallbackBpm <= 0) return (null, null);
            timings.Add(new TimingEvent { Bpm = fallbackBpm });
        }
        var map = new TimingMap(timings);
        var first = TimingMap.FindFirstBpmTiming(timings);
        return (map, first?.Bpm);
    }

    /// <summary>Malody playable length uses the same first START / last END rule
    /// as .osu, mapped through the chart's timing section.</summary>
    private static double? ReadMalodyLength(JsonElement rootElement, TimingMap? timingMap)
    {
        if (timingMap is null ||
            !rootElement.TryGetProperty("note", out var notes) ||
            notes.ValueKind != JsonValueKind.Array)
            return null;
        double? firstStart = null;
        double? lastEnd = null;
        foreach (var note in notes.EnumerateArray())
        {
            if (note.ValueKind != JsonValueKind.Object || !note.TryGetProperty("column", out _)) continue;
            // type 1 with a sound is Malody's background-music pseudo note.
            if (Integer(note, "type", 0) == 1 && note.TryGetProperty("sound", out var sound) && sound.ValueKind == JsonValueKind.String) continue;
            if (!TryReadMalodyBeat(note, "beat", out var startBeat)) continue;
            var endBeat = startBeat;
            if (note.TryGetProperty("endbeat", out var endValue) && endValue.ValueKind == JsonValueKind.Array &&
                TryReadMalodyBeat(note, "endbeat", out var parsedEndBeat))
                endBeat = parsedEndBeat;
            try
            {
                var start = timingMap.BeatToMilliseconds(startBeat);
                var end = timingMap.BeatToMilliseconds(endBeat);
                if (firstStart is null || start < firstStart) firstStart = start;
                if (lastEnd is null || end > lastEnd) lastEnd = end;
            }
            catch (ArgumentOutOfRangeException)
            {
                // One out-of-range record must not discard the chart length.
            }
        }
        return firstStart is { } first && lastEnd is { } last ? Math.Max(0, last - first) / 1000.0 : null;
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    /// <summary>
    /// Reads the start time and END time of one .osu hit object. Mania holds
    /// (type 128) and spinners (type 8) carry an explicit end in objectParams;
    /// every other object ends at its start. Slider curve length is irrelevant
    /// for the mania-only library and falls back to the start time.
    /// </summary>
    private static bool TryReadHitObjectRange(string line, out double startMs, out double endMs)
    {
        startMs = 0;
        endMs = 0;
        var span = line.AsSpan();
        var p1 = span.IndexOf(',');
        if (p1 < 0) return false;
        var p2 = span[(p1 + 1)..].IndexOf(',');
        if (p2 < 0) return false;
        p2 += p1 + 1;
        var p3 = span[(p2 + 1)..].IndexOf(',');
        if (p3 < 0) return false;
        p3 += p2 + 1;
        var p4 = span[(p3 + 1)..].IndexOf(',');
        if (p4 < 0) return false;
        p4 += p3 + 1;
        if (!int.TryParse(span[(p2 + 1)..p3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(span[(p3 + 1)..p4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
            return false;
        startMs = start;
        endMs = start;
        if ((type & 128) == 0 && (type & 8) == 0) return true;
        // Skip hitSound (field 5); objectParams starts at field 6.
        var p5 = span[(p4 + 1)..].IndexOf(',');
        if (p5 < 0) return true;
        p5 += p4 + 1;
        var rest = span[(p5 + 1)..];
        var colon = rest.IndexOf(':');
        var comma = rest.IndexOf(',');
        var separator = colon < 0 ? comma : comma < 0 ? colon : Math.Min(colon, comma);
        if (separator >= 0 && int.TryParse(rest[..separator].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
            endMs = end;
        return true;
    }

    /// <summary>
    /// Reads the beat length of the first uninherited timing point. .osu stores
    /// negative beat lengths for inherited (SV) points; the final "uninherited"
    /// field defaults to 1 when omitted by older writers.
    /// </summary>
    private static bool TryReadFirstTimingPoint(string line, out double beatLength)
    {
        beatLength = 0;
        var first = line.IndexOf(',');
        if (first < 0) return false;
        var second = line.IndexOf(',', first + 1);
        var beatText = (second < 0 ? line[(first + 1)..] : line[(first + 1)..second]).Trim();
        if (!double.TryParse(beatText, NumberStyles.Float, CultureInfo.InvariantCulture, out beatLength)) return false;
        if (second < 0) return true;
        // Field 6 is the uninherited flag; field 7 (effects) follows.
        var comma = second;
        for (var index = 0; index < 4; index++)
        {
            comma = line.IndexOf(',', comma + 1);
            if (comma < 0) return true; // missing trailing fields mean "uninherited"
        }
        var end = line.IndexOf(',', comma + 1);
        var flag = (end < 0 ? line[(comma + 1)..] : line[(comma + 1)..end]).Trim();
        return flag.Length == 0 || flag == "1";
    }

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
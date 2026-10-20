using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LumaChart.Application.Libraries;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;

/// <summary>
/// 0.1.38/39 library index v2 suite: full-field index (playable length, BPM,
/// source, file size/mtime), incremental refresh, legacy-schema background
/// backfill, and the osu-style length/bpm search end to end.
/// </summary>
internal static class LibraryIndex038Specs
{
    private const int BenchmarkCharts = 20_000;
    private const int ChangedCharts = 200;
    private const int MigrationCharts = 1_200;
    private static readonly StringBuilder Log = new();
    private static readonly Dictionary<string, object?> Report = new();

    public static void Run(string root, string[] extraArgs)
    {
        _ = extraArgs;
        Report["suite"] = "library-038";
        Report["startedAt"] = DateTimeOffset.Now.ToString("O");
        try
        {
            LengthAndBpmCorrectness(root);
            var benchmark = IncrementalRefresh(root);
            SearchEndToEnd(root, benchmark.Charts);
            MigrationAndBackfill(root);
            StatusHint(root);
            Report["result"] = "PASS";
            WriteArtifacts();
            Console.WriteLine("PASS library038 full-field index, incremental refresh, migration/backfill and length/bpm search");
            Console.WriteLine("library038 artifacts: " + ArtifactDirectory());
        }
        catch (Exception exception)
        {
            Report["result"] = "FAIL";
            Report["failure"] = exception.ToString();
            WriteArtifacts();
            Console.Error.WriteLine(exception);
            throw;
        }
    }

    // ---------------------------------------------------------------- 1. field semantics

    private static void LengthAndBpmCorrectness(string root)
    {
        var songs = Path.Combine(root, "library038", "correctness", "Songs");
        Directory.CreateDirectory(songs);
        WriteChart(songs, "Set A", "a.osu", """
osu file format v14
[General]
AudioFilename: song.wav
Mode:3
[Metadata]
Title:Known Length
Artist:Fixture Artist
Creator:FixtureMapper
Version:7K Insane
Source:FixtureSource
[Difficulty]
CircleSize:7
[Events]
0,0,"bg.jpg",0,0
[TimingPoints]
0,400,4,1,0,70,1,0
1000,-100,4,1,0,70,0,0
2000,300,4,1,0,70,1,0
[HitObjects]
36,192,1000,1,0,0:0:0:0:
109,192,5000,128,0,9000:0:0:0:0:
36,192,6000,1,0,0:0:0:0:
""");
        WriteChart(songs, "Set B", "b.osu", """
osu file format v14
[General]
AudioFilename: song.wav
Mode:3
[Metadata]
Title:Inherited First
Artist:Fixture Artist
Creator:FixtureMapper
Version:4K Hard
[Difficulty]
CircleSize:4
[Events]
0,0,"bg.jpg",0,0
[TimingPoints]
0,-100,4,2,0,60,0,0
0,500,4,2,0,60,1,0
[HitObjects]
36,192,1000,1,0,0:0:0:0:
36,192,3000,1,0,0:0:0:0:
""");
        WriteChart(songs, "Set C", "c.osu", """
osu file format v14
[General]
AudioFilename: song.wav
Mode:3
[Metadata]
Title:Multi Bpm
Artist:Fixture Artist
Creator:FixtureMapper
Version:4K Extra
[Difficulty]
CircleSize:4
[Events]
0,0,"bg.jpg",0,0
[TimingPoints]
0,400,4,1,0,70,1,0
2000,-100,4,1,0,70,0,0
4000,300,4,1,0,70,1,0
[HitObjects]
36,192,1000,1,0,0:0:0:0:
109,192,5000,128,0,7000:0:0:0:0:
""");
        WriteChart(songs, "Set M", "m.mc", """
{"meta":{"creator":"MalodyMapper","version":"4K","mode":0,"mode_ext":{"column":4},"song":{"title":"Malody Length","artist":"Malody Artist","file":"song.wav","bpm":120}},
 "time":[{"beat":[0,0,1],"bpm":120,"delay":0}],
 "note":[{"beat":[0,0,1],"column":0},{"beat":[4,0,1],"endbeat":[6,0,1],"column":1}]}
""");

        var entries = DesktopChartLibraryService.ScanAsync(songs).GetAwaiter().GetResult();
        Require(entries.Count == 4, "correctness scan indexed " + entries.Count + " charts, expected 4");
        var known = entries.Single(entry => entry.Title == "Known Length");
        Require(known.Length is { } knownLength && Math.Abs(knownLength - 8.0) < 1e-9,
            "playable length is " + known.Length + ", expected END-START = 8.0 s");
        Require(known.Bpm is { } knownBpm && Math.Abs(knownBpm - 150) < 1e-6,
            "first uninherited BPM is " + known.Bpm + ", expected 150");
        Require(known.Source == "FixtureSource" && known.KeyCount == 7 && known.Version == "7K Insane",
            "full metadata fields were not cached");
        var inheritedFirst = entries.Single(entry => entry.Title == "Inherited First");
        Require(inheritedFirst.Length is { } inheritedLength && Math.Abs(inheritedLength - 2.0) < 1e-9,
            "inherited-first chart length is " + inheritedFirst.Length + ", expected 2.0 s");
        Require(inheritedFirst.Bpm is { } inheritedBpm && Math.Abs(inheritedBpm - 120) < 1e-6,
            "inherited-first BPM is " + inheritedFirst.Bpm + ", expected the first non-inherited 120");
        var multi = entries.Single(entry => entry.Title == "Multi Bpm");
        Require(multi.Length is { } multiLength && Math.Abs(multiLength - 6.0) < 1e-9,
            "multi-BPM chart length is " + multi.Length + ", expected 6.0 s");
        Require(multi.Bpm is { } multiBpm && Math.Abs(multiBpm - 150) < 1e-6,
            "multi-BPM chart BPM is " + multi.Bpm + ", expected the first non-inherited 150");
        var malody = entries.Single(entry => entry.Title == "Malody Length");
        Require(malody.Length is { } malodyLength && Math.Abs(malodyLength - 3.0) < 1e-9,
            "Malody playable length is " + malody.Length + ", expected 3.0 s");
        Require(malody.Bpm is { } malodyBpm && Math.Abs(malodyBpm - 120) < 1e-6,
            "Malody BPM is " + malody.Bpm + ", expected 120");

        var header = new MapperLibraryStore(Path.Combine(root, "library038", "correctness", "profile"));
        var cache = new DesktopLibraryMetadataCache(header);
        var scanned = DesktopChartLibraryService.ScanAsync(songs, metadataCache: cache).GetAwaiter().GetResult();
        cache.Flush();
        var cachedKnown = header.LoadHeaders().Single(item => item.Path.EndsWith("a.osu", StringComparison.OrdinalIgnoreCase));
        var file = new FileInfo(cachedKnown.Path);
        Require(cachedKnown.Length == file.Length && cachedKnown.ModifiedTicks == File.GetLastWriteTimeUtc(cachedKnown.Path).Ticks,
            "file size/mtime were not persisted in the header index");
        Require(cachedKnown.Entry?.Length is { } persisted && Math.Abs(persisted - 8.0) < 1e-9,
            "playable length was not persisted in the header index");

        Report["correctness"] = new
        {
            charts = entries.Count,
            knownLength = known.Length,
            knownBpm = known.Bpm,
            inheritedFirstLength = inheritedFirst.Length,
            inheritedFirstBpm = inheritedFirst.Bpm,
            multiBpm = multi.Bpm,
            malodyLength = malody.Length,
            malodyBpm = malody.Bpm,
            source = known.Source,
            keyCount = known.KeyCount,
            fileSize = cachedKnown.Length,
            mtimeTicks = cachedKnown.ModifiedTicks,
            scanned = scanned.Count
        };
        Log.AppendLine("[fields] length=END-START: Known Length 8.0s, inherited-first 2.0s, multi-BPM 6.0s, Malody 3.0s; BPM 150/120/150/120; Source/KeyCount/version + size/mtime persisted PASS");
    }

    // ---------------------------------------------------------------- 2. 20k incremental refresh

    private record BenchmarkResult(IReadOnlyList<DesktopLibraryChartEntry> Charts, int ParsedOnIncremental, int Removed);

    private static BenchmarkResult IncrementalRefresh(string root)
    {
        var songs = Path.Combine(root, "library038", "bench20k", "Songs");
        GenerateBenchmarkCharts(songs, BenchmarkCharts);
        var profile = Path.Combine(root, "library038", "bench20k", "profile");
        var store = new MapperLibraryStore(profile);
        var cache = new DesktopLibraryMetadataCache(store);

        var watch = Stopwatch.StartNew();
        var first = DesktopChartLibraryService.ScanAsync(songs, metadataCache: cache).GetAwaiter().GetResult();
        cache.Flush();
        var firstMs = watch.Elapsed.TotalMilliseconds;
        Require(first.Count == BenchmarkCharts, "first import indexed " + first.Count + ", expected " + BenchmarkCharts);
        Require(cache.HeaderReads == BenchmarkCharts, "first import parsed " + cache.HeaderReads + " headers, expected " + BenchmarkCharts);

        var warm = new DesktopLibraryMetadataCache(store);
        watch.Restart();
        warm.LoadPersistedHeaders();
        var warmCharts = warm.CachedCharts;
        var cacheLoadMs = watch.Elapsed.TotalMilliseconds;
        Require(warmCharts.Count == BenchmarkCharts, "warm cache loaded " + warmCharts.Count + ", expected " + BenchmarkCharts);
        Require(warmCharts.All(chart => chart.Length is not null && chart.Bpm is not null),
            "warm cache did not retain full length/BPM fields");

        // A no-op refresh is the best case: every row is a cache hit and only
        // the fast directory enumeration plus per-file stat remains.
        var noOpReads = cache.HeaderReads;
        watch.Restart();
        var noOp = DesktopChartLibraryService.ScanAsync(songs, metadataCache: cache).GetAwaiter().GetResult();
        cache.Flush();
        var noOpMs = watch.Elapsed.TotalMilliseconds;
        Require(noOp.Count == BenchmarkCharts && cache.HeaderReads == noOpReads,
            "no-op refresh reparsed " + (cache.HeaderReads - noOpReads) + " cached files");

        // Change exactly 1% of the files. The new content changes the title and
        // the playable length; the mtime is forced forward so NTFS timestamp
        // granularity cannot hide the change.
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ChangedCharts; index++)
        {
            var chartIndex = index * 97 % BenchmarkCharts;
            var path = Path.Combine(songs, "Set" + (chartIndex / 10).ToString("D4"), "Diff" + chartIndex.ToString("D5") + ".osu");
            File.WriteAllText(path, BenchmarkChart(chartIndex, changed: true));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1).AddTicks(index));
            changed.Add(path);
        }
        var readsBefore = cache.HeaderReads;
        watch.Restart();
        var incremental = DesktopChartLibraryService.ScanAsync(songs, metadataCache: cache).GetAwaiter().GetResult();
        cache.Flush();
        var incrementalMs = watch.Elapsed.TotalMilliseconds;
        var parsedChanged = (int)(cache.HeaderReads - readsBefore);
        Require(incremental.Count == BenchmarkCharts, "incremental scan indexed " + incremental.Count + ", expected " + BenchmarkCharts);
        Require(parsedChanged == ChangedCharts, "incremental refresh parsed " + parsedChanged + " files, expected exactly " + ChangedCharts);

        // Pure directory enumeration: the target cost of a no-op incremental refresh.
        watch.Restart();
        var files = DesktopChartLibraryService.EnumerateChartFiles(songs);
        var enumerateMs = watch.Elapsed.TotalMilliseconds;
        Require(files.Length == BenchmarkCharts, "enumeration saw " + files.Length + " chart files, expected " + BenchmarkCharts);

        // Full parse for comparison, with a fresh database/cache.
        var fullStore = new MapperLibraryStore(Path.Combine(root, "library038", "bench20k", "profile-full"));
        var fullCache = new DesktopLibraryMetadataCache(fullStore);
        watch.Restart();
        var full = DesktopChartLibraryService.ScanAsync(songs, metadataCache: fullCache).GetAwaiter().GetResult();
        fullCache.Flush();
        var fullMs = watch.Elapsed.TotalMilliseconds;
        Require(full.Count == BenchmarkCharts, "full comparison parsed " + full.Count + " charts");

        // Deletion: disappearing files must leave the index.
        var deleted = files.Where((_, index) => index % 2000 == 0).Take(10).ToArray();
        foreach (var path in deleted) File.Delete(path);
        var remaining = DesktopChartLibraryService.EnumerateChartFiles(songs);
        var removed = cache.ForgetMissingUnderRoot(songs, remaining);
        var afterDelete = DesktopChartLibraryService.ScanAsync(songs, metadataCache: cache).GetAwaiter().GetResult();
        cache.Flush();
        Require(removed.Count == 10, "deleted " + removed.Count + " rows, expected 10");
        Require(afterDelete.Count == BenchmarkCharts - 10, "after deletion indexed " + afterDelete.Count + ", expected " + (BenchmarkCharts - 10));
        Require(store.LoadHeaders().Count == BenchmarkCharts - 10, "deleted rows remained in SQLite headers");

        var result = new BenchmarkResult(afterDelete, parsedChanged, removed.Count);
        Report["incremental"] = new
        {
            charts = BenchmarkCharts,
            firstImportMs = Round(firstMs),
            warmCacheLoadMs = Round(cacheLoadMs),
            noOpRefreshMs = Round(noOpMs),
            incremental1PercentMs = Round(incrementalMs),
            pureEnumerationMs = Round(enumerateMs),
            fullParseMs = Round(fullMs),
            parsedOnIncremental = parsedChanged,
            removed = removed.Count,
            firstImportHeaderReads = BenchmarkCharts,
            warmCacheCharts = warmCharts.Count,
            afterDeleteCharts = afterDelete.Count
        };
        Log.AppendLine("[incremental] 20k: first import " + Round(firstMs) + "ms, warm cache " + Round(cacheLoadMs) +
                        "ms, no-op refresh " + Round(noOpMs) + "ms, changed 1% refresh " + Round(incrementalMs) + "ms (parsed " + parsedChanged +
                        ", pure enumeration " + Round(enumerateMs) + "ms), full parse " + Round(fullMs) +
                        "ms, deleted 10 rows PASS");
        return result;
    }

    // ---------------------------------------------------------------- 3. search end to end

    private static void SearchEndToEnd(string root, IReadOnlyList<DesktopLibraryChartEntry> charts)
    {
        _ = root;
        var total = charts.Count;
        int Count(string query) => DesktopChartLibraryService.SearchPaths(charts, DesktopLibrarySearch.Parse(query)).Count;

        var expectedLengthLt60 = charts.Count(chart => chart.Length < 60);
        var expectedLengthGe60 = charts.Count(chart => chart.Length >= 60);
        var expectedBpmGe150 = charts.Count(chart => chart.Bpm >= 150);
        var expectedCombined = charts.Count(chart => chart.Length < 60 && chart.Bpm >= 150);
        Require(Count("length<60") == expectedLengthLt60, "length<60 count mismatch");
        Require(Count("length>=60") == expectedLengthGe60, "length>=60 count mismatch");
        Require(Count("bpm>=150") == expectedBpmGe150, "bpm>=150 count mismatch");
        Require(Count("length<60 bpm>=150") == expectedCombined, "combined length/bpm count mismatch");
        Require(Count("length<60") + Count("length>=60") == total, "length range partition is not exhaustive");
        Require(Count("length<60") > 0 && Count("length<60") < total, "length filter is not selective");

        var lengthSearch = DesktopLibrarySearch.Parse("length<60");
        Require(lengthSearch.HasLengthFilter && lengthSearch.UnsupportedFields.Count == 0,
            "length is still reported as unsupported");
        Require(!lengthSearch.IsEmpty, "length query parsed as empty");
        var unknown = new DesktopLibraryChartEntry
        {
            ChartPath = Path.Combine(root, "unknown.osu"),
            FolderPath = root,
            RelativePath = "unknown.osu",
            Title = "Unknown Length",
            Length = null
        };
        Require(DesktopChartLibraryService.SearchPaths([unknown], DesktopLibrarySearch.Parse("length>=0")).Count == 0,
            "a row with unknown length matched a length range");
        Require(DesktopChartLibraryService.SearchPaths([unknown], DesktopLibrarySearch.Parse("length<1000")).Count == 0,
            "a row with unknown length matched length<1000");
        Require(DesktopLibrarySearch.Parse("ar=9").UnsupportedFields.Contains("ar"),
            "unknown-field fallback stopped reporting ar");

        Report["search"] = new
        {
            rows = total,
            lengthLt60 = expectedLengthLt60,
            lengthGe60 = expectedLengthGe60,
            bpmGe150 = expectedBpmGe150,
            combined = expectedCombined,
            lengthSupported = true,
            unknownLengthExcluded = true,
            arFallback = true
        };
        Log.AppendLine("[search] length<60=" + expectedLengthLt60 + ", length>=60=" + expectedLengthGe60 +
                        ", bpm>=150=" + expectedBpmGe150 + ", combined=" + expectedCombined + "; unknown-length rows excluded; ar fallback kept PASS");
    }

    // ---------------------------------------------------------------- 4. legacy migration/backfill

    private static void MigrationAndBackfill(string root)
    {
        var songs = Path.Combine(root, "library038", "migration", "Songs");
        var profile = Path.Combine(root, "library038", "migration", "profile");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(songs);
        var paths = new List<string>();
        for (var index = 0; index < MigrationCharts; index++)
        {
            var folder = Path.Combine(songs, "Set" + (index / 10).ToString("D3"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "Legacy" + index.ToString("D4") + ".osu");
            File.WriteAllText(path, LegacyChart(index));
            paths.Add(path);
        }
        CreateLegacyDatabase(profile, paths);

        var store = new MapperLibraryStore(profile);
        Require(store.NeedsDerivedBackfill, "legacy database did not request a background backfill");
        store.SetAnnotation(paths[0], new MapperAnnotation(["legacy-tag"], "keep me", "未发布", true));
        var cache = new DesktopLibraryMetadataCache(store);
        cache.LoadPersistedHeaders();
        var before = cache.CachedCharts;
        Require(before.Count == MigrationCharts, "legacy snapshot loaded " + before.Count + " rows, expected " + MigrationCharts);
        Require(before.All(chart => chart.Length is null), "legacy rows unexpectedly already had length");
        Require(before.Any(chart => chart.Length is null), "legacy snapshot did not expose unknown length");
        Require(cache.BackfillTask is not null, "background backfill did not start");
        var search = DesktopLibrarySearch.Parse("length<60");
        var during = DesktopChartLibraryService.SearchPaths(cache.CachedCharts, search);
        Require(during.Count < before.Count, "length query silently returned every legacy row during backfill");

        var watch = Stopwatch.StartNew();
        cache.BackfillTask!.GetAwaiter().GetResult();
        var backfillMs = watch.Elapsed.TotalMilliseconds;
        var after = cache.CachedCharts;
        Require(after.Count == MigrationCharts, "post-backfill snapshot count changed to " + after.Count);
        Require(after.All(chart => chart.Length is not null), "background backfill left rows without length");
        var expected = after.Count(chart => chart.Length < 60);
        Require(DesktopChartLibraryService.SearchPaths(after, search).Count == expected,
            "length<60 count after backfill is not " + expected);
        Require(expected > 0 && expected < after.Count, "migration length distribution is not selective");
        var persisted = store.LoadHeaders();
        Require(persisted.Count == MigrationCharts && persisted.All(item => item.Entry?.Length is not null),
            "backfilled length was not persisted");
        Require(store.GetAnnotation(paths[0]).Tags.Contains("legacy-tag"), "backfill removed personal annotation");

        Report["migration"] = new
        {
            legacyRows = before.Count,
            backfillMs = Round(backfillMs),
            searchDuringBackfill = during.Count,
            lengthLt60AfterBackfill = expected,
            persistedRows = persisted.Count,
            annotationPreserved = true,
            backfillTaskStarted = true
        };
        Log.AppendLine("[migration] " + before.Count + " legacy rows upgraded in place; backfill " + Round(backfillMs) +
                        "ms; length<60 before=" + during.Count + " after=" + expected +
                        "; annotation preserved PASS");
    }

    // ---------------------------------------------------------------- 5. status line

    private static void StatusHint(string root)
    {
        var model = new MainWindowViewModel(new AvaloniaWorkspaceController(
            Path.Combine(root, "library038", "status", "workspace"),
            Path.Combine(root, "library038", "status", "editing"),
            "library038"));
        var entry = new DesktopLibraryChartEntry
        {
            ChartPath = Path.Combine(root, "library038", "status", "pending.osu"),
            FolderPath = Path.Combine(root, "library038", "status"),
            RelativePath = "pending.osu",
            Title = "Pending Backfill",
            Format = "osu!mania",
            Length = null,
            Bpm = 120
        };
        model.LibraryEntries.Add(entry);
        model.SetLibrarySearch("length<60");
        Require(model.Status.Contains("时长未知", StringComparison.Ordinal),
            "missing-length status hint is missing: " + model.Status);
        Require(model.SearchLibrary("length<60").Paths is { Count: 0 }, "pending row matched length<60");
        entry.Length = 42;
        model.SetLibrarySearch("");
        model.SetLibrarySearch("length<60");
        Require(!model.Status.Contains("时长未知", StringComparison.Ordinal),
            "length status hint did not clear after the value was filled: " + model.Status);
        Require(model.SearchLibrary("length<60").Paths is { Count: 1 },
            "filled row did not match length<60");
        Report["status"] = new { missingHint = true, clearedAfterBackfill = true, query = "length<60" };
        Log.AppendLine("[status] length<60 with unknown length shows the 时长未知 hint; after fill it clears and the row matches PASS");
    }

    // ---------------------------------------------------------------- fixtures

    private static void GenerateBenchmarkCharts(string songs, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var folder = Path.Combine(songs, "Set" + (index / 10).ToString("D4"));
            if (index % 10 == 0) Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Diff" + index.ToString("D5") + ".osu"), BenchmarkChart(index, changed: false));
        }
    }

    private static string BenchmarkChart(int index, bool changed)
    {
        var length = 10 + (index % 18) * 10;
        var bpm = 120 + (index % 5) * 20;
        var beatLength = (60000d / bpm).ToString("0.###", CultureInfo.InvariantCulture);
        var keys = index % 2 == 0 ? 7 : 4;
        var title = (changed ? "Changed " : "Index ") + "Song " + index.ToString("D5");
        var end = 1000 + length * 1000;
        return $"""
osu file format v14
[General]
AudioFilename: song.wav
Mode:3
[Metadata]
Title:{title}
Artist:Index Artist {(index % 17).ToString("D2")}
Creator:IndexMapper{(index % 23).ToString("D2")}
Version:{(keys == 7 ? "7K" : "4K")} {index % 5}
Source:Src{index % 4}
[Difficulty]
CircleSize:{keys}
[Events]
0,0,"bg.jpg",0,0
[TimingPoints]
0,{beatLength},4,1,0,70,1,0
[HitObjects]
36,192,1000,1,0,0:0:0:0:
109,192,{length * 1000},128,0,{end}:0:0:0:0:
""";
    }

    private static string LegacyChart(int index)
    {
        var length = 10 + index % 18 * 10;
        var beatLength = (60000d / (120 + index % 5 * 20)).ToString("0.###", CultureInfo.InvariantCulture);
        return $"""
osu file format v14
[General]
AudioFilename: song.wav
Mode:3
[Metadata]
Title:Legacy Song {index:D4}
Artist:Legacy Artist
Creator:LegacyMapper
Version:4K Legacy
Source:OldIndex
[Difficulty]
CircleSize:4
[Events]
0,0,"bg.jpg",0,0
[TimingPoints]
0,{beatLength},4,1,0,70,1,0
[HitObjects]
36,192,1000,1,0,0:0:0:0:
109,192,{length * 1000},128,0,{1000 + length * 1000}:0:0:0:0:
""";
    }

    private static void CreateLegacyDatabase(string profile, IReadOnlyList<string> paths)
    {
        var database = Path.Combine(profile, "mapper-library.sqlite3");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        db.Open();
        using (var schema = db.CreateCommand())
        {
            schema.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE identities(path TEXT PRIMARY KEY COLLATE NOCASE, identity TEXT NOT NULL);
                CREATE INDEX identity_index ON identities(identity);
                CREATE TABLE personal(identity TEXT PRIMARY KEY, payload TEXT NOT NULL);
                CREATE TABLE headers(path TEXT PRIMARY KEY COLLATE NOCASE, length INTEGER NOT NULL,
                    modified INTEGER NOT NULL, payload TEXT, search TEXT NOT NULL, keys INTEGER NOT NULL);
                CREATE INDEX header_keys ON headers(keys);
                CREATE VIRTUAL TABLE lookup USING fts5(path UNINDEXED, search, tokenize='trigram');
                CREATE TABLE index_metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO index_metadata VALUES('header_parser','2');
                """;
            schema.ExecuteNonQuery();
        }
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            var entry = new DesktopLibraryChartEntry
            {
                ChartPath = path,
                FolderPath = Path.GetDirectoryName(path)!,
                RelativePath = Path.GetFileName(path),
                Format = "osu!mania",
                Title = "Legacy Song",
                Artist = "Legacy Artist",
                Creator = "LegacyMapper",
                Version = "4K Legacy",
                KeyCount = 4,
                Bpm = 120,
                Source = "OldIndex",
                AddedTime = info.CreationTime,
                ModifiedTime = info.LastWriteTime
            };
            using var insert = db.CreateCommand();
            insert.CommandText = "INSERT INTO headers(path,length,modified,payload,search,keys) VALUES($p,$l,$m,$j,$s,$k)";
            insert.Parameters.AddWithValue("$p", path);
            insert.Parameters.AddWithValue("$l", info.Length);
            insert.Parameters.AddWithValue("$m", info.LastWriteTimeUtc.Ticks);
            insert.Parameters.AddWithValue("$j", JsonSerializer.Serialize(entry));
            insert.Parameters.AddWithValue("$s", entry.SearchText);
            insert.Parameters.AddWithValue("$k", entry.KeyCount);
            insert.ExecuteNonQuery();
        }
    }

    private static void WriteChart(string songs, string set, string name, string content)
    {
        var folder = Path.Combine(songs, set);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), content);
    }

    // ---------------------------------------------------------------- helpers

    private static double Round(double value) => Math.Round(value, 1);

    private static string ArtifactDirectory() => Path.Combine(Environment.CurrentDirectory, "artifacts");

    private static void WriteArtifacts()
    {
        var directory = ArtifactDirectory();
        Directory.CreateDirectory(directory);
        Report["finishedAt"] = DateTimeOffset.Now.ToString("O");
        File.WriteAllText(Path.Combine(directory, "library-index-038-report.json"),
            JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "library-index-038-report.txt"), Log.ToString());
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using LumaChart.Application.Libraries;
using LumaChart.Avalonia;
using LumaChart.Avalonia.ViewModels;

/// <summary>
/// 0.1.38 osu!-style library search suite. Covers the parser table (every operator,
/// quoted values, unknown-operator fallback), end-to-end filtering over a scanned
/// fixture, consistency with the legacy SQLite search, the 20k-row cost measurement,
/// and the library UI's quick filters (favourite / unpublished).
/// </summary>
internal static class LibrarySearch038Specs
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly StringBuilder Log = new();
    private static readonly Dictionary<string, object?> Report = new();

    public static void Run(string root, string[] extraArgs)
    {
        if (extraArgs.Length >= 2 && extraArgs[0] == "--scan-benchmark")
        {
            ScanBenchmark(extraArgs[1]);
            return;
        }

        Report["suite"] = "search-038";
        Report["startedAt"] = DateTimeOffset.Now.ToString("O");
        try
        {
            ParseTable();
            EndToEndScan(root);
            SqlConsistency(root);
            Performance(root);
            SyntheticScanCost(root);
            UiQuickFilters(root);
            Report["result"] = "PASS";
            WriteArtifacts();
            Console.WriteLine("PASS search038 osu-style query parsing, scanned fixture counts, SQL consistency, 20k timing and library quick filters");
            Console.WriteLine("search038 artifacts: " + ArtifactDirectory());
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

    // ------------------------------------------------------------------ 1. parser table

    private static void ParseTable()
    {
        var charts = new[]
        {
            Chart("Moon Light", "月光", "Fixture Artist", "测试艺术家", "Cache038", "7K Insane", 7, "osu!mania", 180, "Vocaloid",
                new MapperAnnotation(["参考", "LN"], "第二段适合参考，未发布", "未发布", true)),
            Chart("Moon Light II", "", "Other Artist", "", "MapperB", "4K Hard", 4, "osu!mania", 120, ""),
            Chart("Malody Song", "", "Fixture", "", "Cache038", "Normal", 4, "Malody", null, "",
                new MapperAnnotation(["排列参考"], "第二段", "已发布", false)),
            Chart("Neko Night", "", "Neko Artist", "", "NekoMapper", "7K", 7, "osu!mania", 160, ""),
            Chart("Unicode 初音", "", "初音ミク", "", "VocaloidFan", "Extra", 10, "osu!mania", 240, "初音"),
            Chart("Six Key", "", "Fixture", "", "Cache038", "6K", 6, "osu!mania", 100, ""),
        };

        var cases = 0;
        void Check(string query, params int[] expected)
        {
            cases++;
            var search = DesktopLibrarySearch.Parse(query);
            var actual = Enumerable.Range(0, charts.Length).Where(index => search.Matches(charts[index])).ToArray();
            var wanted = expected.OrderBy(index => index).ToArray();
            Require(actual.SequenceEqual(wanted),
                "query \"" + query + "\" matched [" + string.Join(",", actual) + "], expected [" + string.Join(",", wanted) + "]");
        }

        void Unsupported(string query, string field, params int[] expected)
        {
            cases++;
            var search = DesktopLibrarySearch.Parse(query);
            var actual = Enumerable.Range(0, charts.Length).Where(index => search.Matches(charts[index])).ToArray();
            Require(actual.SequenceEqual(expected.OrderBy(index => index)), "unsupported query \"" + query + "\" matched [" + string.Join(",", actual) + "]");
            Require(search.UnsupportedFields.Contains(field), "\"" + query + "\" did not report unsupported field " + field);
        }

        // free text keeps the previous AND-substring behaviour
        Check("", 0, 1, 2, 3, 4, 5);
        Check("moon", 0, 1);
        Check("Moon Light", 0, 1);
        Check("neko", 3);
        Check("-moon", 2, 3, 4, 5);
        Check("\"初音\"", 4);
        Check("malody", 2);

        // text keyword fields (osu! accepts '=' and ':' identically)
        Check("mapper=Cache038", 0, 2, 5);
        Check("mapper:Cache038", 0, 2, 5);
        Check("creator=Cache038", 0, 2, 5);
        Check("author=Cache038", 0, 2, 5);
        Check("mapper=CACHE038", 0, 2, 5);
        Check("mapper=Cache038 key=7", 0);
        Check("mapper=Cache038 -key=7", 2, 5);
        Check("artist=Fixture", 0, 2, 5);
        Check("artist=\"Fixture Artist\"", 0);
        Check("artist!=\"Fixture Artist\"", 1, 2, 3, 4, 5);
        Check("artist=初音ミク", 4);
        Check("title=\"Moon Light\"", 0, 1);
        Check("title=\"Moon Light\"!", 0);
        Check("title=\"moon light\"!", 0);
        Check("title=初音", 4);
        Check("title!=\"Moon Light\"", 2, 3, 4, 5);
        Check("diff=7K", 0, 3);
        Check("diff:7K", 0, 3);
        Check("version=7K", 0, 3);
        Check("version:4K", 1);
        Check("source=Vocaloid", 0);
        Check("source=初音", 4);
        Check("format=osu", 0, 1, 3, 4, 5);
        Check("format:malody", 2);
        Check("format!=osu", 2);
        Check("-format=osu", 2);

        // mania key counts: key/keys, '=' and ':', ranges and lists
        Check("key=7", 0, 3);
        Check("key:7", 0, 3);
        Check("keys=7", 0, 3);
        Check("keys:7", 0, 3);
        Check("keys=4", 1, 2);
        Check("keys=4,7", 0, 1, 2, 3);
        Check("keys<7", 1, 2, 5);
        Check("keys>=7", 0, 3, 4);
        Check("keys!=7", 1, 2, 4, 5);
        Check("-key=7", 1, 2, 4, 5);
        Check("key>7", 4);
        Check("key<=7", 0, 1, 2, 3, 5);
        Check("key=7 key=4");
        Check("key<7 key>4", 5);
        Check("key=99");

        // BPM ranges (osu!'s 0.5 tolerance, strict boundaries)
        Check("bpm>150", 0, 3, 4);
        Check("bpm<170", 1, 3, 5);
        Check("bpm=180", 0);
        Check("bpm=120", 1);
        Check("bpm!=120", 0, 3, 4, 5);
        Check("bpm>=120", 0, 1, 3, 4);
        Check("bpm<=120", 1, 5);
        Check("bpm>100 bpm<200", 0, 1, 3);
        Check("bpm>150 mapper=Cache038", 0);

        // personal annotation fields (favourite/unpublished live here)
        Check("star:true", 0);
        Check("star=true", 0);
        Check("star:yes", 0);
        Check("star=false", 1, 2, 3, 4, 5);
        Check("-star:true", 1, 2, 3, 4, 5);
        Check("tag=参考", 0, 2);
        Check("tag:参考", 0, 2);
        Check("tag=LN", 0);
        Check("tag!=参考", 1, 3, 4, 5);
        Check("-tag:参考", 1, 3, 4, 5);
        Check("state=未发布", 0);
        Check("-state=已发布", 0, 1, 3, 4, 5);
        Check("note=第二段", 0, 2);
        Check("note:\"第二段\"", 0, 2);

        // difficulty bracket (osu's FilterCriteria.SearchText)
        Check("[7K]", 0, 3);
        Check("[7K Insane]", 0);

        // osu! fields without local data fall back to literal text and are reported
        Unsupported("length<120", "length");
        Unsupported("length=120", "length");
        Unsupported("ar=9", "ar");
        Unsupported("star>6", "star");
        Unsupported("ln=50", "ln");

        // malformed input must fall back safely, never throw
        Check("bpm=abc");
        Check("mapper<Cache");
        Check("key=");
        Check("foo=bar");
        Check("state：未发布");
        Check("mapper==x");
        Check("\"unclosed");
        Check("-", 0, 1, 2, 3, 4, 5);
        Require(DesktopLibrarySearch.Parse(new string('a', 4096)) is not null, "long query threw");
        Require(DesktopLibrarySearch.Parse(null).IsEmpty, "null query is not empty");
        Require(DesktopLibrarySearch.Parse("").IsEmpty, "empty query is not empty");
        Require(DesktopLibrarySearch.Parse("star:true").HasPersonalFilters, "favourite query does not need personal data");
        Require(DesktopLibrarySearch.Parse("bpm>120").HasBpmFilter, "bpm query not marked as BPM filter");
        Require(DesktopLibrarySearch.Parse("length<120").UnsupportedFields.Count == 1, "length fallback not reported");

        Report["parse"] = new { cases, fixtureRows = charts.Length, unsupportedFields = new[] { "length", "ar", "star", "ln" } };
        Log.AppendLine("[parse] " + cases + " query cases over " + charts.Length + " fixture rows PASS");
    }

    // ------------------------------------------------------------------ 2. scanned fixture

    private static void EndToEndScan(string root)
    {
        var songs = Path.Combine(root, "search038", "Songs");
        WriteOsu(Path.Combine(songs, "Set Alpha"), "alpha.osu", OsuChart("E2E Alpha", "E2E", "Scan038", "7K", 7, 500, source: "SrcA"));
        WriteOsu(Path.Combine(songs, "Set Beta"), "beta.osu", OsuChart("E2E Beta", "E2E", "Scan038", "4K", 4, 400));
        WriteOsu(Path.Combine(songs, "Set Standard"), "standard.osu", OsuChart("E2E Standard", "E2E", "Scan038", "Normal", 4, 500, mode: "0"));

        var entries = DesktopChartLibraryService.ScanAsync(songs).GetAwaiter().GetResult();
        Require(entries.Count == 2, "scan indexed " + entries.Count + " mania charts, expected 2 (standard mode must be skipped)");
        var alpha = entries.Single(entry => entry.Title == "E2E Alpha");
        var beta = entries.Single(entry => entry.Title == "E2E Beta");
        Require(Math.Abs((alpha.Bpm ?? 0) - 120) < 0.001, "BPM of a 500ms beat length is " + alpha.Bpm + ", expected 120");
        Require(Math.Abs((beta.Bpm ?? 0) - 150) < 0.001, "BPM of a 400ms beat length is " + beta.Bpm + ", expected 150");
        Require(alpha.Source == "SrcA", "metadata Source was not indexed: '" + alpha.Source + "'");
        Require(alpha.KeyCount == 7 && beta.KeyCount == 4, "key counts were not indexed");

        int Count(string query) => DesktopChartLibraryService.SearchPaths(entries, DesktopLibrarySearch.Parse(query)).Count;
        var checks = new (string Query, int Expected)[]
        {
            ("E2E", 2),
            ("mapper=Scan038", 2),
            ("mapper:Scan038", 2),
            ("key=7", 1),
            ("key:7", 1),
            ("keys=7", 1),
            ("artist=E2E", 2),
            ("bpm>=130", 1),
            ("bpm<130", 1),
            ("length<120", 0),
            ("mode=0", 0),
        };
        foreach (var (query, expected) in checks)
        {
            var actual = Count(query);
            Require(actual == expected, "scanned fixture query \"" + query + "\" matched " + actual + ", expected " + expected);
        }
        Require(DesktopChartLibraryService.SearchPaths(entries, DesktopLibrarySearch.Parse("bpm>150")).Count == 0,
            "BPM range matched a chart whose BPM is unknown");

        Report["endToEnd"] = new
        {
            charts = entries.Count,
            bpmAlpha = alpha.Bpm,
            bpmBeta = beta.Bpm,
            source = alpha.Source,
            queries = checks.Select(check => new { query = check.Query, expected = check.Expected }).ToArray()
        };
        Log.AppendLine("[endToEnd] scanned " + entries.Count + " real .osu fixture charts, BPM 120/150 and Source cached; " + checks.Length + " query counts PASS");
    }

    // ------------------------------------------------------------------ 3. legacy SQL consistency

    private static void SqlConsistency(string root)
    {
        var charts = DesktopChartLibraryService.ScanAsync(Path.Combine(root, "search038", "Songs")).GetAwaiter().GetResult();
        var profile = Path.Combine(root, "search038", "profile");
        Directory.CreateDirectory(profile);
        var store = new MapperLibraryStore(profile);
        var headers = charts.Select(chart => new LibraryCachedHeader(chart.ChartPath, new FileInfo(chart.ChartPath).Length,
            File.GetLastWriteTimeUtc(chart.ChartPath).Ticks, chart)).ToArray();
        store.UpdateHeaders(headers);
        var annotated = charts.Single(chart => chart.Title == "E2E Alpha");
        store.SetAnnotations([new KeyValuePair<string, MapperAnnotation>(annotated.ChartPath,
            new MapperAnnotation(["参考"], "第二段", "未发布", true))]);
        var annotations = store.AllAnnotations();
        foreach (var chart in charts)
        {
            if (annotations.TryGetValue(chart.ChartPath, out var annotation))
            {
                chart.PersonalAnnotation = annotation;
                chart.PersonalSearchText = DesktopLibrarySearch.BuildPersonalSearchText(annotation);
            }
        }

        var queries = new[]
        {
            "E2E", "E2E Alpha", "creator:Scan038", "keys:7", "star:true", "tag:参考", "\"参考\"",
            "note:第二段", "-state:已发布", "未发布", "tag:RC", "artist:E2E"
        };
        var compared = 0;
        foreach (var query in queries)
        {
            compared++;
            var legacy = store.Search(query);
            var modern = DesktopChartLibraryService.SearchPaths(charts, DesktopLibrarySearch.Parse(query));
            Require(legacy.SetEquals(modern),
                "legacy SQLite and new search disagree for \"" + query + "\": legacy=[" + string.Join(",", legacy.Select(Path.GetFileName)) +
                "] new=[" + string.Join(",", modern.Select(Path.GetFileName)) + "]");
        }

        Report["sqlConsistency"] = new { queries = compared, result = "identical path sets" };
        Log.AppendLine("[sqlConsistency] " + compared + " shared-vocabulary queries returned identical SQLite and in-memory path sets");
    }

    // ------------------------------------------------------------------ 4. 20k performance

    private static void Performance(string root)
    {
        var charts = GenerateBenchmarkCharts(20000);
        foreach (var chart in charts) _ = chart.SearchText; // warm the cached blob like the real UI

        var textQuery = "benchmark";
        var operatorQuery = "mapper=Bench09 key=7 bpm>=150";

        // Before 0.1.38 the library box resolved text through one lower-cased
        // Contains per row (legacy FilterAndSort) and field syntax through SQLite.
        var legacyText = Measure(5, () => OldContainsFilter(charts, textQuery));
        var legacyOperatorLiteral = Measure(5, () => OldContainsFilter(charts, operatorQuery));
        var newText = Measure(5, () =>
        {
            var search = DesktopLibrarySearch.Parse(textQuery);
            return DesktopChartLibraryService.SearchPaths(charts, search).Count;
        });
        var newOperator = Measure(5, () =>
        {
            var search = DesktopLibrarySearch.Parse(operatorQuery);
            return DesktopChartLibraryService.SearchPaths(charts, search).Count;
        });
        var parseOnly = Measure(50, () => DesktopLibrarySearch.Parse(operatorQuery).UnsupportedFields.Count);

        Require(newText.Count == charts.Length, "plain text search lost rows: " + newText.Count);
        Require(newOperator.Count > 0 && newOperator.Count < charts.Length, "operator search matched " + newOperator.Count + " of " + charts.Length);

        // Same size SQLite/FTS index: this is what the library UI called until 0.1.38.
        var profile = Path.Combine(root, "search038", "perf-index");
        Directory.CreateDirectory(profile);
        var store = new MapperLibraryStore(profile);
        var build = Stopwatch.StartNew();
        store.UpdateHeaders(charts.Select(chart => new LibraryCachedHeader(chart.ChartPath, 1024, 0, chart)).ToArray());
        build.Stop();
        var sqlText = Measure(5, () => store.Search(textQuery).Count);
        var sqlKeys = Measure(5, () => store.Search("keys:7").Count);
        Require(sqlText.Count == charts.Length, "SQLite plain text search count changed: " + sqlText.Count);

        var parsedText = DesktopLibrarySearch.Parse(textQuery);
        var matched = 0;
        var predicateAllocations = Allocations(() =>
        {
            var count = 0;
            foreach (var chart in charts)
                if (parsedText.Matches(chart)) count++;
            matched = count;
        });
        Require(matched == charts.Length, "predicate-only pass count changed: " + matched);

        var allocatedNew = Allocations(() => DesktopChartLibraryService.SearchPaths(charts, DesktopLibrarySearch.Parse(textQuery)));
        var allocatedSelective = Allocations(() => DesktopChartLibraryService.SearchPaths(charts, DesktopLibrarySearch.Parse("bpm>=200")));
        var selective = DesktopChartLibraryService.SearchPaths(charts, DesktopLibrarySearch.Parse("bpm>=200")).Count;
        Require(selective > 0 && selective < charts.Length, "selective query matched " + selective);

        Report["performance"] = new
        {
            rows = charts.Length,
            sqliteIndexBuildMs = Round(build.Elapsed.TotalMilliseconds),
            oldSqliteTextBestMs = Round(sqlText.BestMs),
            oldSqliteTextMedianMs = Round(sqlText.MedianMs),
            oldSqliteKeysBestMs = Round(sqlKeys.BestMs),
            newTextBestMs = Round(newText.BestMs),
            newTextMedianMs = Round(newText.MedianMs),
            newOperatorBestMs = Round(newOperator.BestMs),
            newOperatorMedianMs = Round(newOperator.MedianMs),
            newOperatorMatches = newOperator.Count,
            parsedVsSqliteTextRatio = Round(newText.BestMs / Math.Max(0.0001, sqlText.BestMs)),
            legacyInMemoryTextBestMs = Round(legacyText.BestMs),
            legacyOperatorLiteralMatches = legacyOperatorLiteral.Count,
            predicateOnlyAllocatedBytes = predicateAllocations,
            searchPathsAllocatedBytesAllMatch = allocatedNew,
            searchPathsAllocatedBytesSelective = allocatedSelective,
            selectiveMatches = selective,
            parseOnlyUs = Round(parseOnly.BestMs * 1000)
        };
        Log.AppendLine("[performance] 20k rows: SQLite/FTS (pre-0.1.38 UI path) " + Round(sqlText.BestMs) + "ms vs parsed in-memory " +
            Round(newText.BestMs) + "ms text, " + Round(newOperator.BestMs) + "ms/" + newOperator.Count + " rows operators; per-chart predicate " +
            predicateAllocations + " bytes allocated; parse " + Round(parseOnly.BestMs * 1000) + "us; SQLite index built in " +
            Round(build.Elapsed.TotalMilliseconds) + "ms");
    }

    private static void SyntheticScanCost(string root)
    {
        var directory = Path.Combine(root, "search038", "synthetic-5k");
        Directory.CreateDirectory(directory);
        var write = Stopwatch.StartNew();
        for (var index = 0; index < 5000; index++)
        {
            var folder = Path.Combine(directory, "Set " + (index / 5).ToString("D4"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "chart" + index.ToString("D4") + ".osu"),
                OsuChart("Synthetic " + index, "Bench039", "Scan038", (index % 2 == 0 ? "7K " : "4K ") + index,
                    index % 2 == 0 ? 7 : 4, 400 + (index % 80), hitObjects: 120, timingPoints: 12));
        }
        write.Stop();
        var files = Directory.EnumerateFiles(directory, "*.osu", SearchOption.AllDirectories).ToArray();

        var headerPasses = new List<double>();
        var timingPasses = new List<double>();
        for (var pass = 0; pass < 2; pass++)
        {
            var headerWatch = Stopwatch.StartNew();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, ReadToTimingHeader);
            headerWatch.Stop();
            headerPasses.Add(headerWatch.Elapsed.TotalMilliseconds);
            var timingWatch = Stopwatch.StartNew();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, ReadThroughTimingSection);
            timingWatch.Stop();
            timingPasses.Add(timingWatch.Elapsed.TotalMilliseconds);
        }

        var product = Stopwatch.StartNew();
        var entries = DesktopChartLibraryService.ScanAsync(directory).GetAwaiter().GetResult();
        product.Stop();

        var headerMs = headerPasses.Min();
        var timingMs = timingPasses.Min();
        var delta = (timingMs - headerMs) / Math.Max(1, headerMs) * 100;
        var withBpm = entries.Count(entry => entry.Bpm is not null);
        Require(entries.Count == files.Length, "synthetic scan indexed " + entries.Count + " of " + files.Length);
        Require(withBpm == files.Length, "synthetic scan cached " + withBpm + " BPM values of " + files.Length);

        Report["syntheticScan5k"] = new
        {
            files = files.Length,
            writeMs = Round(write.Elapsed.TotalMilliseconds),
            readToTimingHeaderBestMs = Round(headerMs),
            readThroughTimingSectionBestMs = Round(timingMs),
            headerPassSamples = headerPasses.Select(Round).ToArray(),
            timingPassSamples = timingPasses.Select(Round).ToArray(),
            productScanMs = Round(product.Elapsed.TotalMilliseconds),
            deltaPercent = Round(delta),
            cachedBpm = withBpm
        };
        Log.AppendLine("[scan] 5k synthetic .osu: read-to-TimingPoints " + Round(headerMs) + "ms vs read-through-timing(+BPM) " +
            Round(timingMs) + "ms (" + Round(delta) + "%), full product scan " + Round(product.Elapsed.TotalMilliseconds) + "ms");
    }

    // ------------------------------------------------------------------ 5. library UI quick filters

    private static void UiQuickFilters(string root)
    {
        AppBuilder.Configure<TestApplication>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var songs = Path.Combine(root, "ui038", "Songs");
        WriteOsu(Path.Combine(songs, "Ui Set A"), "a.osu", OsuChart("Ui Favourite", "Ui", "UiMapper", "7K", 7, 500));
        WriteOsu(Path.Combine(songs, "Ui Set B"), "b.osu", OsuChart("Ui Second", "Ui", "UiMapper", "4K", 4, 400));
        WriteOsu(Path.Combine(songs, "Ui Set C"), "c.osu", OsuChart("Ui Third", "Ui", "OtherMapper", "7K", 7, 600));

        var window = new MainWindow([]) { Width = 1440, Height = 960 };
        window.Show();
        WaitUntil(() => Field<bool>(window, "initialized"), 30000, "search-038 window did not initialize");
        var model = Field<MainWindowViewModel>(window, "viewModel");
        Wait(model.AddLibraryRootAsync(songs));
        WaitUntil(() => model.LibraryEntries.Count == 3, 30000, "UI fixture scan indexed " + model.LibraryEntries.Count + " charts");
        model.StopLibraryWatching();
        var favourite = model.LibraryEntries.Single(chart => chart.Title == "Ui Favourite");
        model.SetPersonalAnnotation(favourite.ChartPath, new MapperAnnotation(["参考"], "按钮测试", "未发布", true));

        Find<Button>(window, "Library.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.Pump(250);

        var readOnlyList = Find<ListBox>(window, "Library.ReadOnlyList");
        var workList = Find<ListBox>(window, "Library.List");
        Require(ReadItems(readOnlyList).Length == 3, "read-only pane did not show the 3 scanned charts");

        Find<Button>(window, "Library.Favorites").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ElapseDebounce(window);
        var favouritesRight = ReadItems(readOnlyList);
        Require(favouritesRight.Length == 1 && favouritesRight[0].ChartPath.Equals(favourite.ChartPath, StringComparison.OrdinalIgnoreCase),
            "★ 收藏 quick filter did not filter the read-only pane to the favourite chart (" + favouritesRight.Length + " rows)");
        Require(workList.ItemCount == 0, "★ 收藏 unexpectedly filled the workspace pane (the fixture lives in Songs)");

        Find<Button>(window, "Library.Unpublished").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ElapseDebounce(window);
        var unpublishedRight = ReadItems(readOnlyList);
        Require(unpublishedRight.Length == 1 && unpublishedRight[0].ChartPath.Equals(favourite.ChartPath, StringComparison.OrdinalIgnoreCase),
            "未发布 quick filter did not filter the read-only pane (" + unpublishedRight.Length + " rows)");

        Find<TextBox>(window, "Library.ReadOnlySearch").Text = "";
        Find<TextBox>(window, "Library.Search").Text = "";
        ElapseDebounce(window);
        Require(ReadItems(readOnlyList).Length == 3, "clearing the search boxes did not restore the read-only pane");

        Wait(model.SetLibrarySearchAsync("mapper=UiMapper"));
        Require(model.LibraryFolders.Sum(folder => folder.VisibleCharts.Count) == 2, "view model mapper= filter count is wrong");
        Wait(model.SetLibrarySearchAsync("key=7"));
        Require(model.LibraryFolders.Sum(folder => folder.VisibleCharts.Count) == 2, "view model key= filter count is wrong");
        Wait(model.SetLibrarySearchAsync("bpm>=125"));
        Require(model.LibraryFolders.Sum(folder => folder.VisibleCharts.Count) == 1, "view model bpm>= filter count is wrong");
        Wait(model.SetLibrarySearchAsync("length<120"));
        Require(model.LibraryFolders.Sum(folder => folder.VisibleCharts.Count) == 0, "unsupported length field matched rows");
        Require(model.Status.Contains("length", StringComparison.Ordinal), "unsupported length field is not explained in the status line: " + model.Status);
        Wait(model.SetLibrarySearchAsync(""));
        Require(model.LibraryFolders.Sum(folder => folder.VisibleCharts.Count) == 3, "clearing the view model search did not restore rows");

        window.Close();
        Program.Pump(150);

        Report["ui"] = new
        {
            scanned = 3,
            favouriteButtonRows = favouritesRight.Length,
            unpublishedButtonRows = unpublishedRight.Length,
            workspacePaneRows = workList.ItemCount,
            mapperEquals = 2,
            keyEquals = 2,
            bpmAtLeast = 1,
            lengthUnsupportedStatus = true
        };
        Log.AppendLine("[ui] ★ 收藏/未发布 now filter the read-only Songs pane (1 row each; previously 0 result); mapper=/key=/bpm>= counts 2/2/1, length<120 explains the unsupported field");
    }

    // ------------------------------------------------------------------ real library scan benchmark (manual)

    private static void ScanBenchmark(string songsRoot)
    {
        var files = Directory.EnumerateFiles(songsRoot, "*.osu", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        }).ToArray();

        var headerPasses = new List<double>();
        var timingPasses = new List<double>();
        for (var pass = 0; pass < 2; pass++)
        {
            var headerWatch = Stopwatch.StartNew();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, ReadToTimingHeader);
            headerWatch.Stop();
            headerPasses.Add(headerWatch.Elapsed.TotalMilliseconds);
            var timingWatch = Stopwatch.StartNew();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, ReadThroughTimingSection);
            timingWatch.Stop();
            timingPasses.Add(timingWatch.Elapsed.TotalMilliseconds);
        }

        var product = Stopwatch.StartNew();
        var entries = DesktopChartLibraryService.ScanAsync(songsRoot).GetAwaiter().GetResult();
        product.Stop();
        var mania = entries.Count(entry => entry.Format.StartsWith("osu", StringComparison.OrdinalIgnoreCase));
        var withBpm = entries.Count(entry => entry.Bpm is not null);
        var headerMs = headerPasses.Min();
        var timingMs = timingPasses.Min();
        var delta = (timingMs - headerMs) / Math.Max(1, headerMs) * 100;
        var report = new Dictionary<string, object?>
        {
            ["songsRoot"] = songsRoot,
            ["osuFiles"] = files.Length,
            ["readToTimingHeaderBestMs"] = Round(headerMs),
            ["readThroughTimingSectionBestMs"] = Round(timingMs),
            ["headerPassSamples"] = headerPasses.Select(Round).ToArray(),
            ["timingPassSamples"] = timingPasses.Select(Round).ToArray(),
            ["deltaPercent"] = Round(delta),
            ["productScanMs"] = Round(product.Elapsed.TotalMilliseconds),
            ["entries"] = entries.Count,
            ["maniaEntries"] = mania,
            ["bpmCached"] = withBpm,
            ["bpmPercent"] = mania == 0 ? 0 : Round(withBpm * 100.0 / mania)
        };
        var dir = ArtifactDirectory();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "osu-search-038-real-scan.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------------------ helpers

    private static DesktopLibraryChartEntry Chart(string title, string titleOriginal, string artist, string artistOriginal,
        string creator, string version, int keys, string format, double? bpm, string source, MapperAnnotation? annotation = null)
    {
        var chart = new DesktopLibraryChartEntry
        {
            ChartPath = Path.Combine("C:\\fixture038", (creator + "-" + version + "-" + title).Replace(' ', '_') + ".osu"),
            FolderPath = "C:\\fixture038",
            RelativePath = creator + " - " + title + ".osu",
            Title = title,
            TitleOriginal = titleOriginal,
            Artist = artist,
            ArtistOriginal = artistOriginal,
            Creator = creator,
            Version = version,
            KeyCount = keys,
            Format = format,
            Bpm = bpm,
            Source = source
        };
        if (annotation is { } value)
        {
            chart.PersonalAnnotation = value;
            chart.PersonalSummary = value.Summary;
            chart.PersonalSearchText = DesktopLibrarySearch.BuildPersonalSearchText(value);
        }
        return chart;
    }

    private static DesktopLibraryChartEntry[] GenerateBenchmarkCharts(int count) =>
        Enumerable.Range(0, count).Select(index => new DesktopLibraryChartEntry
        {
            ChartPath = Path.Combine("C:\\bench038", index.ToString("D5") + ".osu"),
            FolderPath = "C:\\bench038",
            RelativePath = "Benchmap " + (index / 5).ToString("D4") + "\\chart" + index.ToString("D5") + ".osu",
            Title = (index % 10 == 0 ? "Benchmark Alpha " : "Song ") + index.ToString("D5"),
            Artist = "Benchmark Artist " + (index % 37).ToString("D2"),
            Creator = "Bench" + (index % 13).ToString("D2"),
            Version = (index % 2 == 0 ? "7K " : "4K ") + index.ToString("D5"),
            KeyCount = index % 2 == 0 ? 7 : 4,
            Format = index % 3 == 0 ? "Malody" : "osu!mania",
            Bpm = 100 + (index % 120)
        }).ToArray();

    private static int OldContainsFilter(DesktopLibraryChartEntry[] charts, string query)
    {
        var lower = query.Trim().ToLowerInvariant();
        if (lower.Length == 0) return charts.Length;
        var matches = 0;
        foreach (var chart in charts)
            if (chart.SearchText.Contains(lower, StringComparison.Ordinal)) matches++;
        return matches;
    }

    private static (double BestMs, double MedianMs, int Count) Measure(int iterations, Func<int> action)
    {
        var samples = new List<double>(iterations);
        var count = 0;
        for (var index = 0; index < iterations; index++)
        {
            var watch = Stopwatch.StartNew();
            count = action();
            watch.Stop();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (samples[0], samples[samples.Count / 2], count);
    }

    private static long Allocations(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static double Round(double value) => Math.Round(value, 2);

    private static string OsuChart(string title, string artist, string creator, string version, int keys, double beatLength,
        string mode = "3", string source = "", int hitObjects = 4, int timingPoints = 1)
    {
        var builder = new StringBuilder();
        builder.Append("osu file format v14\n[General]\nAudioFilename: song.wav\nMode:").Append(mode).Append('\n');
        builder.Append("[Metadata]\nTitle:").Append(title).Append('\n');
        builder.Append("Artist:").Append(artist).Append('\n');
        builder.Append("Creator:").Append(creator).Append('\n');
        builder.Append("Version:").Append(version).Append('\n');
        builder.Append("Source:").Append(source).Append('\n');
        builder.Append("[Difficulty]\nCircleSize:").Append(keys).Append('\n');
        builder.Append("[Events]\n0,0,\"bg.jpg\",0,0\n");
        builder.Append("[TimingPoints]\n");
        builder.Append("100,-100,4,2,0,60,0,0\n");
        for (var index = 0; index < timingPoints; index++)
            builder.Append(100 + index * 1000).Append(',').Append(beatLength.ToString(CultureInfo.InvariantCulture)).Append(",4,2,0,60,1,0\n");
        builder.Append("[HitObjects]\n");
        for (var index = 0; index < hitObjects; index++)
        {
            var time = 1000 + index * 500;
            builder.Append("36,192,").Append(time).Append(",1,0,0:0:0:0:\n");
            builder.Append("109,192,").Append(time + 100).Append(",128,0,").Append(time + 400).Append(":0:0:0:0:\n");
        }
        return builder.ToString();
    }

    private static void WriteOsu(string folder, string name, string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), content);
    }

    /// <summary>Minimal pre-0.1.38 read boundary: stop at the [TimingPoints] header.</summary>
    private static void ReadToTimingHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var section = string.Empty;
            var sawGeneral = false;
            var sawMetadata = false;
            var sawDifficulty = false;
            var sawEvents = false;
            while (reader.ReadLine() is { } raw)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!line.StartsWith('[') || !line.EndsWith(']')) continue;
                section = line[1..^1];
                sawGeneral |= section.Equals("General", StringComparison.OrdinalIgnoreCase);
                sawMetadata |= section.Equals("Metadata", StringComparison.OrdinalIgnoreCase);
                sawDifficulty |= section.Equals("Difficulty", StringComparison.OrdinalIgnoreCase);
                sawEvents |= section.Equals("Events", StringComparison.OrdinalIgnoreCase);
                if ((section.Equals("TimingPoints", StringComparison.OrdinalIgnoreCase) ||
                     section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase)) &&
                    sawGeneral && sawMetadata && sawDifficulty && sawEvents)
                    return;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>0.1.38 read boundary: the same pass, plus the [TimingPoints] section (BPM).</summary>
    private static void ReadThroughTimingSection(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var section = string.Empty;
            var sawGeneral = false;
            var sawMetadata = false;
            var sawDifficulty = false;
            var sawEvents = false;
            double? bpm = null;
            while (reader.ReadLine() is { } raw)
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
                    if (section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase) &&
                        sawGeneral && sawMetadata && sawDifficulty && sawEvents)
                        return;
                    continue;
                }
                if (section.Equals("TimingPoints", StringComparison.OrdinalIgnoreCase) && bpm is null)
                {
                    var beatLength = ParseBeatLength(line);
                    if (beatLength > 0) bpm = 60000 / beatLength;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static double ParseBeatLength(string line)
    {
        var first = line.IndexOf(',');
        if (first < 0) return 0;
        var second = line.IndexOf(',', first + 1);
        var text = (second < 0 ? line[(first + 1)..] : line[(first + 1)..second]).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// The headless dispatcher used by Program.Pump does not run DispatcherTimer
    /// callbacks, so the 180 ms search debounce is advanced by invoking its handler
    /// directly. This is the same handler the UI runs after the user stops typing.
    /// </summary>
    private static void ElapseDebounce(MainWindow window)
    {
        foreach (var field in new[] { "workspaceSearchTimer", "browserSearchTimer" })
        {
            var timer = Field<Avalonia.Threading.DispatcherTimer>(window, field);
            var tick = typeof(Avalonia.Threading.DispatcherTimer)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => typeof(EventHandler).IsAssignableFrom(candidate.FieldType));
            if (tick?.GetValue(timer) is not EventHandler handler)
                throw new InvalidOperationException("no DispatcherTimer.Tick handler on " + field);
            handler(timer, EventArgs.Empty);
        }
        Program.Pump(60);
    }

    private static T Field<T>(object target, string field) =>
        (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;

    private static object? Call(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, Hidden)!.Invoke(target, arguments);

    private static void Wait(Task task)
    {
        Program.PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs, string message)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException(message);
            Program.Pump(20);
        }
    }

    private static T Find<T>(Control root, string automationId) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().First(control => AutomationProperties.GetAutomationId(control) == automationId);

    private static DesktopLibraryChartEntry[] ReadItems(ListBox list) =>
        (list.ItemsSource as IEnumerable)?.Cast<DesktopLibraryChartEntry>().ToArray() ?? Array.Empty<DesktopLibraryChartEntry>();

    private static string ArtifactDirectory() => Path.Combine(Environment.CurrentDirectory, "artifacts");

    private static void WriteArtifacts()
    {
        var directory = ArtifactDirectory();
        Directory.CreateDirectory(directory);
        Report["finishedAt"] = DateTimeOffset.Now.ToString("O");
        File.WriteAllText(Path.Combine(directory, "osu-search-038-report.json"),
            JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "osu-search-038-report.txt"), Log.ToString());
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
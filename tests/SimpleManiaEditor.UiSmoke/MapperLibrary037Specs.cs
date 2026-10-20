using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LumaChart.Application.Libraries;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.ViewModels;
using Microsoft.Data.Sqlite;

internal static class MapperLibrary037Specs
{
    // 0.1.37 曲库即时进入 + 手动刷新索引。此文件由曲库工作流独占维护。
    private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;
    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void WaitUntil(Func<bool> condition, int timeoutMs, string message)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException(message);
            Program.Pump(20);
        }
    }
    private static T Find<T>(Control root, string id) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().First(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run(string root, string[] extraArgs)
    {
        if (extraArgs.Length >= 3 && extraArgs[0] == "--bench")
        {
            Benchmark(root, extraArgs[1], extraArgs[2]);
            return;
        }
        if (extraArgs.Length >= 3 && extraArgs[0] == "--bench-ui")
        {
            BenchmarkWindow(root, extraArgs[1], extraArgs[2]);
            return;
        }
        var fixture = Path.Combine(root, "lib037");
        var songsRoot = SeedSongsFixture(fixture, 12);
        var expected = SeedCache(root, songsRoot, 12);
        expected = CacheFirstNavigation(root, fixture, songsRoot, expected);
        expected = ManualRefresh(root, songsRoot, expected);
        expected = WatcherBulkHint(root, songsRoot, expected);
        expected = IncrementalCreateAndDelete(root, expected);
        CacheLoadingState(root);
        ReadOnlySearchAnnotations(root, songsRoot);
        SyntheticBrowserTiming(root, songsRoot);
        Console.WriteLine("PASS 037 cache-first library entry, explicit refresh, watcher hint, incremental create/delete, read-only/search/annotation and 20k browser timing");
    }

    // ----------------------------------------------------------------- fixtures

    private static string ChartContent(string title) =>
        "osu file format v14\n[General]\nAudioFilename: song.wav\nMode:3\n[Metadata]\nTitle:" + title +
        "\nArtist:Fixture\nCreator:Cache037\nVersion:7K\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n475,500,4,1,0,0,1,0" +
        "\n[HitObjects]\n36,192,1000,1,0,0:0:0:0:\n109,192,1500,128,0,2000:0:0:0:0:\n";

    private static void WriteChart(string folder, string name, string title)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), ChartContent(title));
        var audio = Path.Combine(folder, "song.wav");
        if (!File.Exists(audio)) Program.WriteSimulationWave(audio);
    }

    private static string SeedSongsFixture(string fixture, int folders)
    {
        var songs = Path.Combine(fixture, "Songs");
        for (var index = 0; index < folders; index++)
            WriteChart(Path.Combine(songs, "Mapset " + index.ToString("D2")), "chart.osu", "Fixture " + index.ToString("D2"));
        return songs;
    }

    /// <summary>Isolated sessions per check: a restored tab would change what the
    /// next window opens, and this suite measures library navigation only.</summary>
    private static void ChangeEditingRoot(string root, string name)
    {
        var editing = Path.Combine(root, "editing-" + name);
        Directory.CreateDirectory(editing);
        Environment.SetEnvironmentVariable("LUMACHART_AVALONIA_EDITING_ROOT", editing);
    }

    private static (MainWindow Window, MainWindowViewModel Model) CreateCachedWindow(string root, string tag)
    {
        ChangeEditingRoot(root, tag);
        var window = new MainWindow([]) { Width = 1440, Height = 960 };
        window.Show();
        WaitUntil(() => Field<bool>(window, "initialized"), 30000, tag + ": window did not finish initialization");
        var model = Field<MainWindowViewModel>(window, "viewModel");
        WaitUntil(() => !model.IsLibraryCacheLoading && model.LibraryEntries.Count > 0, 30000,
            tag + ": persisted library index did not fill the list");
        return (window, model);
    }

    /// <summary>One explicit scan (the only one this suite performs implicitly) so
    /// later windows can prove the list comes from the persisted index.</summary>
    private static int SeedCache(string root, string songsRoot, int minimum)
    {
        ChangeEditingRoot(root, "seed");
        var window = new MainWindow([]) { Width = 1280, Height = 900 };
        window.Show();
        WaitUntil(() => Field<bool>(window, "initialized"), 30000, "seed window did not initialize");
        var model = Field<MainWindowViewModel>(window, "viewModel");
        Wait(model.AddLibraryRootAsync(songsRoot));
        Require(model.LibraryEntries.Count >= minimum, "explicit fixture scan indexed " + model.LibraryEntries.Count + " rows");
        var seeded = model.LibraryEntries.Count;
        model.StopLibraryWatching();
        window.Close(); Program.Pump(150);
        return seeded;
    }

    // ----------------------------------------------------------------- 1. cache first

    private static int CacheFirstNavigation(string root, string fixture, string songsRoot, int expected)
    {
        // Hide the Songs tree after it was indexed: a directory scan now finds
        // nothing, so a list that still shows every row proves the persisted index
        // was used and no scan ran.
        var hidden = Path.Combine(fixture, "Songs-hidden");
        Directory.Move(songsRoot, hidden);
        try
        {
            var (window, model) = CreateCachedWindow(root, "cache-first");
            Require(model.LibraryEntries.Count == expected,
                "cache snapshot has " + model.LibraryEntries.Count + " rows, expected " + expected);
            Require(!model.IsScanning && !model.LibraryReconciled, "startup scanned the library instead of reading the index");
            var metadata = Field<DesktopLibraryMetadataCache>(model, "libraryMetadataCache");
            Require(metadata.CacheHits == 0 && metadata.HeaderReads == 0,
                "startup touched individual chart files instead of the persisted headers");
            var watch = Stopwatch.StartNew();
            // All real entry points: brand icon, title bar and the left rail.
            Find<Button>(window, "Library.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Pump(40);
            Require(Field<bool>(window, "libraryVisible") && Field<Grid>(window, "libraryView").IsVisible,
                "brand icon did not open the library page");
            var right = Find<ListBox>(window, "Library.ReadOnlyList");
            Require(right.ItemCount == expected, "cached rows were not immediately usable: " + right.ItemCount + "/" + expected);
            Require(model.Status.Contains("本地索引"), "status does not name the local index source: " + model.Status);
            Require(model.LibraryIndexSummary.Contains("只读缓存"), "header does not name the read-only cache: " + model.LibraryIndexSummary);
            Require(Find<TextBlock>(window, "Library.CacheStatus").Text?.Contains("只读缓存") == true,
                "cache status text is not wired into the library header");
            var titleBar = window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => (button.Content as string) == "曲库");
            Require(titleBar != null, "title bar library entry is missing");
            titleBar!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.Pump(60);
            Require(!model.IsScanning && !model.LibraryReconciled && model.LibraryEntries.Count == expected,
                "a library entry point started a directory scan");
            Require(watch.ElapsedMilliseconds < 3000, "entering the library took " + watch.ElapsedMilliseconds + " ms for " + expected + " cached rows");
            using (var frame = window.CaptureRenderedFrame())
                frame?.Save(Path.Combine(root, "library-037-cache-first.png"), PngBitmapEncoderOptions.Default);
            Console.WriteLine("[037] " + expected + " cached rows ready in " + watch.ElapsedMilliseconds + " ms across three library entries; no directory scan");
            window.Close(); Program.Pump(150);
        }
        finally { Directory.Move(hidden, songsRoot); }
        return expected;
    }

    // ----------------------------------------------------------------- 2. manual refresh

    private static int ManualRefresh(string root, string songsRoot, int expected)
    {
        var (window, model) = CreateCachedWindow(root, "manual-refresh");
        // Deterministic: the manual button must do the work, not a watcher callback.
        model.StopLibraryWatching();
        WriteChart(Path.Combine(songsRoot, "Mapset Manual"), "chart.osu", "Manual Added");
        var button = Find<Button>(window, "Library.Refresh");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(model.IsScanning, "refresh button did not start a scan immediately");
        Require(Field<Button>(window, "libraryRefreshButton").Content?.ToString() == "正在扫描…",
            "refresh button does not show scan progress");
        WaitUntil(() => !model.IsScanning, 60000, "manual refresh did not finish");
        Require(model.LastLibraryScanError == null, "manual refresh failed: " + model.LastLibraryScanError);
        Require(model.LibraryReconciled, "manual refresh did not reconcile the directory");
        Require(model.LibraryLastRefreshTime != null, "manual refresh finished without recording its time");
        var added = model.LibraryEntries.FirstOrDefault(entry => entry.ChartPath.EndsWith(Path.Combine("Mapset Manual", "chart.osu"), StringComparison.OrdinalIgnoreCase));
        Require(added != null, "manual refresh did not index the new mapset");
        Require(model.Status.Contains("曲库已刷新"), "status does not report the refresh: " + model.Status);
        Require(model.LibraryIndexSummary.Contains("上次刷新"), "header does not show the last refresh time: " + model.LibraryIndexSummary);
        Require(Find<TextBlock>(window, "Library.CacheStatus").Text?.Contains("上次刷新") == true,
            "cache status text does not show the last refresh time");
        Require(model.LibraryEntries.Count == expected + 1,
            "manual refresh produced " + model.LibraryEntries.Count + " rows, expected " + (expected + 1));
        window.Close(); Program.Pump(150);
        return expected + 1;
    }

    // ----------------------------------------------------------------- 3. watcher overflow

    private static int WatcherBulkHint(string root, string songsRoot, int expected)
    {
        var (window, model) = CreateCachedWindow(root, "watcher-bulk");
        var before = model.LibraryEntries.Count;
        Require(before == expected, "watcher check started with " + before + " rows, expected " + expected);
        // A new mapset directory is a bulk change for the watcher: it must only
        // hint, never start the expensive scan by itself.
        WriteChart(Path.Combine(songsRoot, "Mapset Bulk"), "chart.osu", "Bulk Added");
        WaitUntil(() => model.LibraryRefreshSuggested, 20000, "watcher overflow never produced a refresh hint");
        Require(!model.IsScanning && !model.LibraryReconciled, "bulk watcher change started a full directory scan");
        Require(model.LibraryEntries.Count == before, "bulk watcher change changed the cached list without a scan");
        Require(model.Status.Contains("刷新索引"), "no non-blocking 刷新索引 hint: " + model.Status);
        Require(Find<TextBlock>(window, "Library.CacheStatus").Text?.Contains("目录有变化") == true,
            "cache status does not show the pending directory change");
        Find<Button>(window, "Library.Refresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(model.IsScanning, "refresh button did not start after a watcher hint");
        WaitUntil(() => !model.IsScanning, 60000, "refresh after watcher hint did not finish");
        Require(!model.LibraryRefreshSuggested, "manual refresh did not clear the pending-change hint");
        Require(model.LibraryEntries.Count == before + 1,
            "refresh after watcher hint produced " + model.LibraryEntries.Count + " rows, expected " + (before + 1));
        window.Close(); Program.Pump(150);
        return before + 1;
    }

    // ----------------------------------------------------------------- 4. incremental create/save/delete

    private static int IncrementalCreateAndDelete(string root, int expected)
    {
        var (window, model) = CreateCachedWindow(root, "incremental");
        var before = model.LibraryEntries.Count;
        Require(before == expected, "incremental check started with " + before + " rows, expected " + expected);
        Wait(model.CreateAsync("Cache037 Incremental"));
        var tab = model.ActiveTab ?? throw new InvalidOperationException("new chart has no active tab");
        Require(model.LibraryEntries.Any(entry => string.Equals(entry.ChartPath, tab.SourcePath, StringComparison.OrdinalIgnoreCase)),
            "a new chart is missing from the cached library");
        Require(!model.IsScanning && !model.LibraryReconciled, "creating a chart triggered a directory scan");
        var save = model.SaveAndVerifyAsync(tab.Id);
        Wait(save);
        Require(save.Result?.StructurallyEqual == true, "save/reload verification failed");
        Require(model.LibraryEntries.Count(entry => string.Equals(entry.ChartPath, tab.SourcePath, StringComparison.OrdinalIgnoreCase)) == 1,
            "save duplicated the library row");
        Require(model.LibraryEntries.Count == before + 1,
            "create/save produced " + model.LibraryEntries.Count + " rows, expected " + (before + 1));
        // File deletions arrive through the watcher's ForgetLibraryFiles path; a
        // deletion must disappear immediately without a full regroup/scan.
        var victim = model.LibraryEntries.First(entry => string.Equals(entry.ChartPath, tab.SourcePath, StringComparison.OrdinalIgnoreCase));
        model.ForgetLibraryFiles([victim.ChartPath]);
        Require(model.LibraryEntries.All(entry => !string.Equals(entry.ChartPath, victim.ChartPath, StringComparison.OrdinalIgnoreCase)),
            "deleted chart stayed in the library list");
        Require(!model.IsScanning && !model.LibraryReconciled, "dropping a deleted chart triggered a directory scan");
        window.Close(); Program.Pump(150);
        return model.LibraryEntries.Count;
    }

    // ----------------------------------------------------------------- 5. cache loading state

    private static void CacheLoadingState(string root)
    {
        ChangeEditingRoot(root, "vm-load");
        var model = new MainWindowViewModel(new AvaloniaWorkspaceController(
            Path.Combine(root, "maps"), Path.Combine(root, "vm-load-cache"), "spec037"));
        var snapshot = model.EnsureLibrarySnapshotLoadedAsync();
        Require(model.IsLibraryCacheLoading, "loading state is not visible before the index read finishes");
        Require(model.Status.Contains("正在载入本地索引"), "no explicit 正在载入本地索引 status: " + model.Status);
        Require(model.LibraryIndexSummary.Contains("载入本地索引"), "header does not show index loading: " + model.LibraryIndexSummary);
        Program.PumpUntil(() => snapshot.IsCompleted);
        snapshot.GetAwaiter().GetResult();
        Require(!model.IsLibraryCacheLoading, "loading state was not cleared");
        Require(model.LibraryEntries.Count > 0, "persisted index produced no rows");
        Require(model.LibraryIndexSummary.StartsWith("只读缓存", StringComparison.Ordinal),
            "header does not name the read-only cache: " + model.LibraryIndexSummary);
        Require(!model.IsScanning && !model.LibraryReconciled, "cache load scanned the directory");
        Console.WriteLine("[037] cache loading state, status text and final " + model.LibraryEntries.Count + " cached rows verified");
    }

    // ----------------------------------------------------------------- 6. no regressions

    private static void ReadOnlySearchAnnotations(string root, string songsRoot)
    {
        var (window, model) = CreateCachedWindow(root, "readonly");
        var path = model.LibraryEntries.Select(entry => entry.ChartPath)
            .First(path => path.StartsWith(songsRoot, StringComparison.OrdinalIgnoreCase));
        Wait((Task)Call(window, "OpenLibraryReadOnlyAsync", path)!);
        var tab = model.ActiveTab ?? throw new InvalidOperationException("read-only tab did not open");
        Require(tab.IsLibraryReadOnly && tab.Session.IsReadOnly && !tab.Session.CanEditNotes,
            "a cached read-only chart opened editable");
        Require(File.Exists(path), "read-only viewing removed the source chart");
        model.SetPersonalAnnotation(path, new MapperAnnotation(["参考"], "缓存测试", "未发布", true));
        var entry = model.LibraryEntries.First(chart => string.Equals(chart.ChartPath, path, StringComparison.OrdinalIgnoreCase));
        Require(entry.PersonalSummary.Contains("★") && entry.PersonalSummary.Contains("参考"),
            "annotation summary missing on the cached row: " + entry.PersonalSummary);
        Wait(model.SetLibrarySearchAsync("star:true"));
        Require(model.LibraryFolders.SelectMany(folder => folder.VisibleCharts)
            .Any(chart => string.Equals(chart.ChartPath, path, StringComparison.OrdinalIgnoreCase)),
            "favorite search did not find the annotated cached chart");
        Wait(model.SetLibrarySearchAsync("未发布"));
        Wait(model.SetLibrarySearchAsync(""));
        Require(model.LibraryFolders.SelectMany(folder => folder.VisibleCharts).Any(),
            "clearing the search did not restore the cached list");
        Require(model.MapperLibrary.Search("Fixture").Contains(path), "FTS search does not return the cached chart");
        window.Close(); Program.Pump(150);
        Console.WriteLine("[037] read-only protection, personal annotation summary, favorite/unpublished search and FTS verified on cached rows");
    }

    // ----------------------------------------------------------------- 7. large browser timing

    private static void SyntheticBrowserTiming(string root, string songsRoot)
    {
        var (window, model) = CreateCachedWindow(root, "timing");
        model.StopLibraryWatching();
        var actual = model.LibraryEntries.ToArray();
        var huge = Enumerable.Range(0, 20000).Select(index => new DesktopLibraryChartEntry
        {
            ChartPath = Path.Combine(songsRoot, "synthetic-" + index.ToString("D5") + ".osu"),
            FolderPath = songsRoot, RelativePath = "synthetic-" + index.ToString("D5") + ".osu",
            Title = "Synthetic " + index.ToString("D5"), Artist = "Benchmark", Creator = "Cache037",
            Version = "7K", Format = "osu!mania", KeyCount = 7
        }).ToArray();
        model.LibraryEntries.Clear();
        foreach (var item in huge) model.LibraryEntries.Add(item);
        var watch = Stopwatch.StartNew();
        Call(window, "RefreshMapperLibraryBrowser");
        Program.Pump(120);
        var first = watch.ElapsedMilliseconds;
        var right = Find<ListBox>(window, "Library.ReadOnlyList");
        Require(right.ItemCount == 20000, "20k cached rows were not handed to the list: " + right.ItemCount);
        Require(right.GetVisualDescendants().OfType<ListBoxItem>().Count() < 100,
            "20k cached rows are not virtualized: " + right.GetVisualDescendants().OfType<ListBoxItem>().Count());
        // Re-entering the library with a resident cache must not re-sort or rebuild.
        var reentry = Stopwatch.StartNew();
        Call(window, "ShowLibrary", false);
        Program.Pump(80);
        var reentryMs = reentry.ElapsedMilliseconds;
        Require(reentryMs < 1500, "re-entering the library with 20k cached rows took " + reentryMs + " ms");
        Console.WriteLine("[037] 20,000 cached rows: first browser build " + first + " ms, library re-entry " + reentryMs + " ms");
        model.LibraryEntries.Clear();
        foreach (var item in actual) model.LibraryEntries.Add(item);
        Call(window, "RefreshMapperLibraryBrowser");
        window.Close(); Program.Pump(150);
    }

// ----------------------------------------------------------------- window benchmark

    private sealed record CatalogState(string[] Roots, string[] Charts);

    /// <summary>
    /// End-to-end library entry timing with the real 46k index at an isolated
    /// path: the SQLite backup is copied, every indexed path is rewritten into a
    /// private Temp Songs root, and the catalog is rebuilt to point there. The
    /// window therefore never watches or reads the user's real osu! Songs tree.
    /// </summary>
    public static void BenchmarkWindow(string root, string sourceDatabase, string sourceCatalog)
    {
        var profile = AvaloniaAppPaths.LocalDataRoot;
        Directory.CreateDirectory(profile);
        var database = Path.Combine(profile, "mapper-library.sqlite3");
        var catalogPath = Path.Combine(profile, "library-catalog.json");
        var songsRoot = Path.Combine(root, "bench-ui-songs");
        Directory.CreateDirectory(songsRoot);
        var catalogSource = JsonSerializer.Deserialize<CatalogState>(File.ReadAllText(sourceCatalog)) ?? new CatalogState([], []);
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var target = new SqliteConnection("Data Source=" + database))
        { source.Open(); target.Open(); source.BackupDatabase(target); }
        using (var db = new SqliteConnection("Data Source=" + database))
        {
            db.Open();
            using var transaction = db.BeginTransaction();
            foreach (var original in catalogSource.Roots)
            {
                var jsonOriginal = JsonSerializer.Serialize(original)[1..^1];
                var jsonReplacement = JsonSerializer.Serialize(songsRoot)[1..^1];
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    "UPDATE headers SET path = replace(path,$a,$b), payload = replace(payload,$ja,$jb), search = replace(search,$a,$b);" +
                    "UPDATE lookup SET path = replace(path,$a,$b), search = replace(search,$a,$b);" +
                    "UPDATE identities SET path = replace(path,$a,$b);";
                command.Parameters.AddWithValue("$a", original);
                command.Parameters.AddWithValue("$b", songsRoot);
                command.Parameters.AddWithValue("$ja", jsonOriginal);
                command.Parameters.AddWithValue("$jb", jsonReplacement);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new CatalogState([songsRoot], [])));

        ChangeEditingRoot(root, "bench-ui");
        var watch = Stopwatch.StartNew();
        var window = new MainWindow([]) { Width = 1440, Height = 960 };
        window.Show();
        WaitUntil(() => Field<bool>(window, "initialized"), 120000, "bench-ui window did not initialize");
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var initializedMs = watch.ElapsedMilliseconds;
        var right = Find<ListBox>(window, "Library.ReadOnlyList");
        WaitUntil(() => !model.IsLibraryCacheLoading && model.LibraryEntries.Count > 40000, 180000, "bench-ui persisted index did not load");
        var cacheLoadedMs = watch.ElapsedMilliseconds;
        WaitUntil(() => right.ItemCount == model.LibraryEntries.Count, 120000, "bench-ui read-only list never reached the cached row count");
        var firstUsableMs = watch.ElapsedMilliseconds;
        Require(!model.IsScanning && !model.LibraryReconciled, "bench-ui window scanned the library instead of reading the index");
        var reentry = Stopwatch.StartNew();
        Call(window, "ShowLibrary", false);
        Program.Pump(30);
        var reentryMs = reentry.ElapsedMilliseconds;
        typeof(MainWindow).GetField("mapperBrowserDirty", Hidden)!.SetValue(window, true);
        var rebuild = Stopwatch.StartNew();
        Call(window, "ShowLibrary", false);
        Program.Pump(60);
        var rebuildMs = rebuild.ElapsedMilliseconds;
        var report = new
        {
            Charts = model.LibraryEntries.Count,
            Folders = model.LibraryFolders.Count,
            InitializedMs = initializedMs,
            CacheLoadedMs = cacheLoadedMs,
            FirstUsableListMs = firstUsableMs,
            ReentryMs = reentryMs,
            ForcedBrowserRebuildMs = rebuildMs,
            MaterializedRows = right.GetVisualDescendants().OfType<ListBoxItem>().Count(),
            DirectoryScan = false,
            RealOsuInstallTouched = false
        };
        var path = Path.Combine(root, "library-037-bench-ui.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report));
        Console.WriteLine(JsonSerializer.Serialize(report));
        Console.WriteLine("PASS 037 isolated 46k window entry benchmark " + path);
        window.Close(); Program.Pump(150);
    }

    // ----------------------------------------------------------------- benchmark

    private static void Report(string text) => Console.WriteLine("[bench] " + text);

    /// <summary>
    /// Component-level timing of the persisted-index snapshot. The database is a
    /// private SQLite read-only backup inside the isolated root; the user's real
    /// profile is never opened for writing and no directory scan runs.
    /// </summary>
    public static void Benchmark(string root, string sourceDatabase, string sourceCatalog)
    {
        var totalWatch = Stopwatch.StartNew();
        var profile = AvaloniaAppPaths.LocalDataRoot;
        Directory.CreateDirectory(profile);
        var database = Path.Combine(profile, "mapper-library.sqlite3");
        var catalog = Path.Combine(profile, "library-catalog.json");
        var watch = Stopwatch.StartNew();
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var target = new SqliteConnection("Data Source=" + database))
        { source.Open(); target.Open(); source.BackupDatabase(target); }
        File.Copy(sourceCatalog, catalog, true);
        Report("copied index " + watch.ElapsedMilliseconds + "ms");

        var workspaceRoot = Path.Combine(root, "bench-maps");
        Directory.CreateDirectory(workspaceRoot);
        DesktopChartLibraryService.GroupByFolder([], workspaceRoot);

        watch.Restart();
        var store = new MapperLibraryStore(profile);
        Report("store constructed " + watch.ElapsedMilliseconds + "ms");
        watch.Restart();
        var headers = store.LoadHeaders();
        var loadHeadersMs = watch.Elapsed.TotalMilliseconds;
        Report("headers " + headers.Count + " in " + Math.Round(loadHeadersMs, 1) + "ms");
        var charts = headers.Select(header => header.Entry).Where(entry => entry != null).Cast<DesktopLibraryChartEntry>().ToArray();

        watch.Restart();
        var folders = DesktopChartLibraryService.GroupByFolder(charts, workspaceRoot, useCachedTimes: true);
        var groupByFolderMs = watch.Elapsed.TotalMilliseconds;
        Report("grouped " + folders.Count + " folders in " + Math.Round(groupByFolderMs, 1) + "ms");

        watch.Restart();
        var views = DesktopChartLibraryService.FilterAndSort(folders, "", DesktopLibrarySort.AddedDescending);
        var filterAndSortMs = watch.Elapsed.TotalMilliseconds;
        Report("filter/sort " + views.Count + " views in " + Math.Round(filterAndSortMs, 1) + "ms");

        watch.Restart();
        var ordered = charts.OrderBy(chart => chart.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(chart => chart.Version, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var browserSortMs = watch.Elapsed.TotalMilliseconds;
        var ordinalWatch = Stopwatch.StartNew();
        _ = charts.OrderBy(chart => chart.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(chart => chart.Version, StringComparer.OrdinalIgnoreCase).ToArray();
        var browserSortOrdinalMs = ordinalWatch.Elapsed.TotalMilliseconds;
        Report("browser sort " + Math.Round(browserSortMs, 1) + "ms (ordinal " + Math.Round(browserSortOrdinalMs, 1) + "ms) " + ordered.Length);

        watch.Restart();
        var model = new MainWindowViewModel(new AvaloniaWorkspaceController(workspaceRoot, Path.Combine(root, "bench-cache"), "benchmark"));
        var viewModelCtorMs = watch.Elapsed.TotalMilliseconds;
        Report("view model constructed " + Math.Round(viewModelCtorMs, 1) + "ms");
        watch.Restart();
        var snapshot = model.LoadCachedLibrarySnapshotAsync();
        Program.PumpUntil(() => snapshot.IsCompleted);
        snapshot.GetAwaiter().GetResult();
        var snapshotTotalMs = watch.Elapsed.TotalMilliseconds;
        Report("snapshot " + model.LibraryEntries.Count + " entries in " + Math.Round(snapshotTotalMs, 1) + "ms");

        var report = new
        {
            Charts = charts.Length,
            Folders = folders.Count,
            LoadHeadersMs = Math.Round(loadHeadersMs, 1),
            GroupByFolderMs = Math.Round(groupByFolderMs, 1),
            FilterAndSortMs = Math.Round(filterAndSortMs, 1),
            BrowserSortCurrentCultureMs = Math.Round(browserSortMs, 1),
            BrowserSortOrdinalMs = Math.Round(browserSortOrdinalMs, 1),
            ViewModelCtorMs = Math.Round(viewModelCtorMs, 1),
            SnapshotTotalMs = Math.Round(snapshotTotalMs, 1),
            ProcessTotalMs = Math.Round(totalWatch.Elapsed.TotalMilliseconds, 1),
            Views = views.Count,
            Ordered = ordered.Length,
            ModelEntries = model.LibraryEntries.Count,
            SourceOpenedReadOnly = true,
            FullDirectoryScan = false
        };
        var path = Path.Combine(root, "library-037-bench.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report));
        Console.WriteLine(JsonSerializer.Serialize(report));
        Console.WriteLine("PASS 037 isolated real-index component benchmark " + path);
    }
}

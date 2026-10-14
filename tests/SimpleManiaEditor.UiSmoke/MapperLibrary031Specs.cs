using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Libraries;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class MapperLibrary031Specs
{
    private const string ChartText = "osu file format v14\n[General]\nAudioFilename: song.mp3\nMode:3\n[Metadata]\nTitle:Lament fixture\nArtist:Fixture\nCreator:Mapper\nVersion:LN\n[Difficulty]\nCircleSize:7\nOverallDifficulty:8\nHPDrainRate:7\n[TimingPoints]\n475,500,4,1,0,70,1,0\n[HitObjects]\n36,192,1000,1,0,0:0:0:0:\n109,192,1500,128,0,2000:0:0:0:0:\n";
    public static void RunStorage(string root)
    {
        var maps = Path.Combine(root, "library031"); var set = Path.Combine(maps, "Original"); Directory.CreateDirectory(set);
        var chart = Path.Combine(set, "fixture.osu"); File.WriteAllText(chart, ChartText); File.WriteAllBytes(Path.Combine(set, "song.mp3"), [1, 2, 3, 4]);
        Directory.CreateDirectory(Path.Combine(set, "sb")); File.WriteAllText(Path.Combine(set, "sb", "asset.txt"), "storyboard asset");
        var profile = Path.Combine(root, "profile031"); var store = new MapperLibraryStore(profile);
        var cache = new DesktopLibraryMetadataCache(store); var entry = cache.Read(chart, maps)!; cache.Flush();
        store.SetAnnotation(chart, new(["LN", "排列参考"], "第二段适合参考，未发布", "未发布", true));
        Require(store.Search("tag:LN state:未发布 keys:7 star:true note:第二段").SetEquals([chart]), "combined personal fields search failed");
        Require(store.Search("\"排列参考\" -state:已发布 creator:Mapper").Contains(chart), "CJK, quote or negative search failed");
        Require(store.Search("第二").Contains(chart) && store.Search("Lament fixture").Contains(chart), "short-CJK or AND substring search failed");
        Require(store.Search("tag:RC").Count == 0 && store.Search("' OR 1=1 --").Count == 0, "search field isolation / SQL parameterization failed");
        var reopen = new MapperLibraryStore(profile); var warm = new DesktopLibraryMetadataCache(reopen); warm.Read(chart, maps);
        Require(warm.CacheHits == 1 && warm.HeaderReads == 0 && reopen.GetAnnotation(chart).Favorite, "persistent header index or annotations lost on restart");
        File.AppendAllText(chart, "\n"); warm.Read(chart, maps); warm.Flush(); Require(warm.HeaderReads == 1, "modified header not invalidated");
        var backupProfile = Path.Combine(root, "personal-restore031"); Directory.CreateDirectory(backupProfile);
        File.Copy(Path.Combine(profile, "library-personal.json"), Path.Combine(backupProfile, "library-personal.json"));
        var restored = new MapperLibraryStore(backupProfile); restored.UpdateHeaders([new(chart, new FileInfo(chart).Length, File.GetLastWriteTimeUtc(chart).Ticks, entry)]);
        Require(restored.GetAnnotation(chart).Favorite && restored.Search("tag:排列参考").Contains(chart), "readable personal backup did not survive a rebuilt database");
        restored.ClearHeaders(); Require(restored.LoadHeaders().Count == 0 && restored.GetAnnotation(chart).Tags.Length == 2, "rebuilding cache removed personal information");
        var hiddenStage = Path.Combine(maps, ".sme-transfer-test"); Directory.CreateDirectory(hiddenStage); File.WriteAllText(Path.Combine(hiddenStage, "staged.osu"), ChartText);
        Require(DesktopChartLibraryService.ScanAsync(maps).GetAwaiter().GetResult().Count == 1, "unpublished staging mapset leaked into library");
        var originalBytes = File.ReadAllBytes(chart);
        var versions = new MapperVersionStore(profile, store); var document = OsuManiaSerializer.Load(chart);
        var first = versions.SaveAsync(chart, document, "初稿", true).GetAwaiter().GetResult()!;
        var same = versions.SaveAsync(chart, document, "不重复", true).GetAwaiter().GetResult(); Require(same == null, "unchanged content generated duplicate revision");
        var session = new EditorSession(); session.ReplaceDocument(document.Clone());
        session.Mutate(d => { d.Notes[0].EndBeat = d.Notes[0].Beat + 1; d.Metadata.OverallDifficulty = 9; d.Timing[0].Delay -= 10; });
        var second = versions.SaveAsync(chart, session.Document, "加长条并校准", false).GetAwaiter().GetResult()!;
        var changes = MapperVersionStore.Diff(versions.Read(chart, first.Id), versions.Read(chart, second.Id));
        Require(changes.Notes.Count >= 2 && changes.Timing.Length >= 2 && changes.Fields.Any(f => f.StartsWith("OverallDifficulty")), "semantic diff missed LN, timing or metadata");
        session.Mutate(d => MapperVersionStore.CopyDocument(d, versions.Read(chart, first.Id)));
        Require(!session.Document.Notes[0].IsHold && session.CanUndo, "version restore is not undoable"); session.Undo(); Require(session.Document.Notes[0].IsHold, "undo restore did not keep current edit");
        Require(new MapperVersionStore(profile, reopen).List(chart).Count == 2 && originalBytes.SequenceEqual(File.ReadAllBytes(chart)), "Git persistence changed source or lost commits");
        var exported = Path.Combine(root, "version-export031"); versions.Export(chart, first.Id, exported);
        Require(File.Exists(Path.Combine(exported, "song.mp3")) && OsuManiaSerializer.Load(Path.Combine(exported, "fixture.osu")).Notes.Count == 2, "version export omitted chart/resources");
        var check = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "--git-dir=" + versions.RepositoryPath(chart), "fsck", "--full" }) check.ArgumentList.Add(arg);
        using (var process = Process.Start(check)!) { var errors = process.StandardError.ReadToEnd(); process.WaitForExit(); Require(process.ExitCode == 0, "not a valid Git repository: " + errors); }

        var transfers = new MapsetTransferService(profile, gameRunning: () => false); var songs = Path.Combine(root, "fake-osu031", "Songs"); Directory.CreateDirectory(songs);
        var installation = Path.GetDirectoryName(songs)!; File.WriteAllText(Path.Combine(installation, "osu!.exe"), "fake, never executed"); File.WriteAllText(Path.Combine(installation, "accidental.osu"), ChartText);
        Reject(() => MapsetTransferService.Preview(installation, maps, true)); Reject(() => MapsetTransferService.Preview(set, installation, false));
        Require(MapsetTransferService.Preview(set, maps, false).Destination == set + " (1)", "copying into the same library should create a new sibling mapset");
        var plan = MapsetTransferService.Preview(set, songs, false); Reject(() => new MapsetTransferService(profile, gameRunning: () => true).ExecuteAsync(plan).GetAwaiter().GetResult()); var done = transfers.ExecuteAsync(plan).GetAwaiter().GetResult();
        store.LinkCopies(chart, Path.Combine(plan.Destination, "fixture.osu"));
        Require(done.State == "Complete" && Directory.Exists(set) && store.GetAnnotation(Path.Combine(plan.Destination, "fixture.osu")).Tags.Contains("排列参考"), "workspace→Songs copy or metadata identity failed");
        var conflict = MapsetTransferService.Preview(set, songs, false); Require(conflict.Destination != plan.Destination, "existing Songs mapset would be overwritten");
        var copied = Path.Combine(plan.Destination, "fixture.osu"); File.AppendAllText(copied, "//edited\n");
        Reject(() => transfers.RollbackAsync(plan.Id).GetAwaiter().GetResult()); Require(File.Exists(copied), "rollback discarded new edits"); File.WriteAllBytes(copied, originalBytes);
        transfers.RollbackAsync(plan.Id).GetAwaiter().GetResult(); Require(Directory.Exists(set) && !Directory.Exists(plan.Destination), "copy rollback failed");
        foreach (var point in new[] { "Copied", "Published", "SourceRemoved" })
        {
            var interrupted = MapsetTransferService.Preview(set, songs, true);
            transfers.FaultInjection = stage => { if (stage == point) throw new IOException("injected " + point); };
            Reject(() => transfers.ExecuteAsync(interrupted).GetAwaiter().GetResult());
            new MapsetTransferService(profile, gameRunning: () => false).RollbackAsync(interrupted.Id).GetAwaiter().GetResult();
            Require(originalBytes.SequenceEqual(File.ReadAllBytes(chart)) && !Directory.Exists(interrupted.Destination), "interrupted move failed recovery at " + point);
        }
        transfers.FaultInjection = null;
        var partial = MapsetTransferService.Preview(set, songs, false);
        transfers.FaultInjection = stage =>
        {
            if (stage != "Copied") return;
            var path = Path.Combine(songs, ".sme-transfer-" + partial.Id, "song.mp3");
            File.WriteAllBytes(path, [1, 2]); throw new IOException("injected truncated staged copy");
        };
        Reject(() => transfers.ExecuteAsync(partial).GetAwaiter().GetResult());
        new MapsetTransferService(profile, gameRunning: () => false).RollbackAsync(partial.Id).GetAwaiter().GetResult();
        Require(File.Exists(chart) && !Directory.Exists(Path.Combine(songs, ".sme-transfer-" + partial.Id)), "truncated stage could not be safely recovered");
        transfers.FaultInjection = null;
        var outgoing = MapsetTransferService.Preview(set, songs, true); transfers.ExecuteAsync(outgoing).GetAwaiter().GetResult(); Require(!Directory.Exists(set), "move retained source");
        var backRoot = Path.Combine(root, "returned031"); Directory.CreateDirectory(backRoot);
        var incoming = MapsetTransferService.Preview(outgoing.Destination, backRoot, true); transfers.ExecuteAsync(incoming).GetAwaiter().GetResult();
        Require(File.Exists(Path.Combine(incoming.Destination, "fixture.osu")) && !Directory.Exists(outgoing.Destination), "Songs→workspace move failed");
        Reject(() => MapsetTransferService.Preview(backRoot, songs, true));
        var mutable = MapsetTransferService.Preview(incoming.Destination, songs, false); File.AppendAllText(Path.Combine(incoming.Destination, "fixture.osu"), "//changed after preview");
        Reject(() => transfers.ExecuteAsync(mutable).GetAwaiter().GetResult()); Require(!Directory.Exists(mutable.Destination), "stale preview was committed");
        var watched = Path.Combine(root, "watch031"); Directory.CreateDirectory(watched);
        using var signaled = new ManualResetEventSlim(); string[] detected = [];
        using (var watcher = new MapperLibraryWatcher([watched], (paths, full) => { detected = paths; signaled.Set(); }))
        {
            File.WriteAllText(Path.Combine(watched, "added.osu"), ChartText);
            Require(signaled.Wait(TimeSpan.FromSeconds(8)) && detected.Any(path => path.EndsWith("added.osu")), "new .osu watcher did not notify incremental indexing");
        }
        Console.WriteLine("PASS persistent SQLite/FTS and short-CJK search, annotations, valid Git/fsck, LN/timing diff, undoable restore, resources, bidirectional copy/move, conflicts, interrupted transaction recovery");
    }
    public static void RunBenchmark(string root)
    {
        var store = new MapperLibraryStore(Path.Combine(root, "benchmark031")); var watch = Stopwatch.StartNew();
        const int count = 200000; var sourceRoot = Path.Combine(root, "synthetic-library031");
        for (var offset = 0; offset < count; offset += 10000)
        {
            store.UpdateHeaders(Enumerable.Range(offset, 10000).Select(i =>
            {
                var path = Path.Combine(sourceRoot, "Set" + i / 4, "Difficulty" + i + ".osu");
                return new LibraryCachedHeader(path, 1000, DateTime.UnixEpoch.Ticks,
                    new DesktopLibraryChartEntry { ChartPath = path, FolderPath = Path.GetDirectoryName(path)!, RelativePath = "Set" + i / 4,
                        Title = "Song " + i, Artist = i % 13 == 0 ? "Albemuth" : "Test artist", Creator = "Mapper " + i % 31, KeyCount = i % 3 == 0 ? 7 : 4, Version = "LN " + i % 10 });
            }));
        }
        var indexMs = watch.Elapsed.TotalMilliseconds; var samples = new List<double>();
        foreach (var query in new[] { "Albemuth keys:7", "Song 19999", "creator:Mapper", "keys:7 -Albemuth", "不存在" })
        {
            watch.Restart(); var results = store.Search(query); samples.Add(watch.Elapsed.TotalMilliseconds);
            Console.WriteLine($"BENCH {query}: {results.Count} hits, {samples[^1]:0.0} ms");
        }
        watch.Restart(); var headers = new MapperLibraryStore(Path.Combine(root, "benchmark031")).LoadHeaders();
        var reopenMs = watch.Elapsed.TotalMilliseconds;
        // Actual filesystem workload as well as database-only search: 50k native chart headers.
        var actual = Path.Combine(root, "actual-songs031"); Directory.CreateDirectory(actual);
        watch.Restart();
        for (var i = 0; i < 50000; i++)
        {
            var folder = Path.Combine(actual, "Set" + i / 4); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Difficulty" + i + ".osu"), ChartText);
        }
        var fixtureMs = watch.Elapsed.TotalMilliseconds;
        var actualStore = new MapperLibraryStore(Path.Combine(root, "actual-index031")); var cold = new DesktopLibraryMetadataCache(actualStore);
        watch.Restart(); var coldResult = DesktopChartLibraryService.ScanAsync(actual, metadataCache: cold).GetAwaiter().GetResult(); cold.Flush(); var coldMs = watch.Elapsed.TotalMilliseconds;
        var hot = new DesktopLibraryMetadataCache(new MapperLibraryStore(Path.Combine(root, "actual-index031")));
        hot.LoadPersistedHeaders();
        watch.Restart(); var hotResult = DesktopChartLibraryService.ScanAsync(actual, metadataCache: hot).GetAwaiter().GetResult(); hot.Flush(); var hotMs = watch.Elapsed.TotalMilliseconds;
        Require(coldResult.Count == 50000 && hotResult.Count == 50000 && hot.HeaderReads == 0 && hot.CacheHits == 50000, "real 50k cold/warm scan changed counts or reparsed cached files");
        var report = new { Charts = count, IndexMs = indexMs, SearchMs = samples, ReopenMs = reopenMs, DatabaseBytes = new FileInfo(store.DatabasePath).Length,
            ActualFiles = 50000, FixtureMs = fixtureMs, ColdScanMs = coldMs, WarmScanMs = hotMs, WarmHeaderReads = hot.HeaderReads };
        File.WriteAllText(Path.Combine(root, "library-benchmark031.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Require(headers.Count == count && store.Search("Albemuth keys:7").Count == Enumerable.Range(0, count).Count(i => i % 13 == 0 && i % 3 == 0), "200k index/search incorrect");
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
    public static void RunUi(MainWindow window, string screenshots)
    {
        var model = (MainWindowViewModel)typeof(MainWindow).GetField("viewModel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var create = model.CreateAsync("Mapper031"); Program.PumpUntil(() => create.IsCompleted); create.GetAwaiter().GetResult();
        var tab = model.ActiveTab!; tab.Session.Mutate(doc => doc.Notes.Add(new ChartNote { Lane = 2, Beat = new BeatPosition(4, 0, 1) }));
        var save = model.SaveAndVerifyAsync(); Program.PumpUntil(() => save.IsCompleted); save.GetAwaiter().GetResult();
        var open = typeof(MainWindow).GetMethod("ShowMapperVersionsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []) as Task;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title!.StartsWith("版本历史")));
        var dialog = window.OwnedWindows.First(w => w.Title!.StartsWith("版本历史")); Program.Pump(150);
        Find<ListBox>(dialog, "Versions.Older").SelectedIndex = 1; Program.Pump(100);
        Require(dialog.GetLogicalDescendants().OfType<MapperDiffSurface>().Any(), "visual version diff missing");
        using (var picture = dialog.CaptureRenderedFrame()!) picture.Save(Path.Combine(screenshots, "versions031.png"), PngBitmapEncoderOptions.Default);
        Find<CheckBox>(dialog, "Versions.RestoreConfirm").IsChecked = true;
        Find<Button>(dialog, "Versions.Restore").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.PumpUntil(() => dialog.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text?.StartsWith("已恢复到编辑器") == true));
        Require(tab.Session.Document.Notes.Count == 0 && tab.Session.Undo() && tab.Session.Document.Notes.Count == 1, "visible version restore/undo did not restore the selected baseline");
        dialog.Close(); Program.PumpUntil(() => open!.IsCompleted);
        var resave = model.SaveAndVerifyAsync(); Program.PumpUntil(() => resave.IsCompleted); resave.GetAwaiter().GetResult();
        typeof(MainWindow).GetMethod("ShowEditorSurface", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false]);
        var chart = model.LibraryEntries.First(e => e.ChartPath == tab.SourcePath);
        var detail = (Task)typeof(MainWindow).GetMethod("ShowLibraryChartDetailsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [chart])!;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "谱面整理"));
        dialog = window.OwnedWindows.First(w => w.Title == "谱面整理");
        Find<TextBox>(dialog, "Library.Tags").Text = "参考, LN"; Find<TextBox>(dialog, "Library.Note").Text = "未发布，第二段需要测试";
        Find<Button>(dialog, "Library.SaveAnnotation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.PumpUntil(() => model.MapperLibrary.GetAnnotation(chart.ChartPath).Tags.Contains("参考")); Program.Pump(80);
        Require(model.MapperLibrary.Search("tag:参考 note:第二段").Contains(chart.ChartPath), "visible annotation editor did not save/search");
        using (var picture = dialog.CaptureRenderedFrame()!) picture.Save(Path.Combine(screenshots, "organize031.png"), PngBitmapEncoderOptions.Default);
        dialog.Close(); Program.PumpUntil(() => detail.IsCompleted);
        var list = (ListBox)typeof(MainWindow).GetField("libraryList", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        list.SelectedItem = model.LibraryFolders.First(folder => folder.VisibleCharts.Any(item => item.ChartPath == chart.ChartPath));
        var batch = (Task)typeof(MainWindow).GetMethod("ShowBatchAnnotationsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [])!;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "批量整理")); dialog = window.OwnedWindows.First(w => w.Title == "批量整理");
        Find<TextBox>(dialog, "Library.BatchTags").Text = "待测图";
        Find<Button>(dialog, "Library.BatchApply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.PumpUntil(() => model.MapperLibrary.GetAnnotation(chart.ChartPath).Tags.Contains("待测图"));
        Require(model.MapperLibrary.GetAnnotation(chart.ChartPath).Note.Contains("第二段"), "batch tags discarded existing notes");
        dialog.Close(); Program.PumpUntil(() => batch.IsCompleted); Program.Pump(100);
        var transfer = (Task)typeof(MainWindow).GetMethod("ShowMapsetTransferAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [chart])!;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "复制 / 双向迁移")); dialog = window.OwnedWindows.First(w => w.Title == "复制 / 双向迁移");
        Find<Button>(dialog, "Transfer.Preview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var previewStatus = Find<TextBlock>(dialog, "Transfer.Result");
        Program.PumpUntil(() => previewStatus.Text != "正在核对所有文件…");
        Require(previewStatus.Text?.Contains("回退入口") == true, "visible transfer preview failed: " + previewStatus.Text);
        Require(!Find<Button>(dialog, "Transfer.Execute").IsEnabled, "transfer execution was enabled without explicit review");
        using (var picture = dialog.CaptureRenderedFrame()!) picture.Save(Path.Combine(screenshots, "transfer031.png"), PngBitmapEncoderOptions.Default);
        dialog.Close(); Program.PumpUntil(() => transfer.IsCompleted);
        Console.WriteLine("PASS visible library annotation editor and version history/diff dialogs");
    }
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new InvalidOperationException("unsafe operation accepted"); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

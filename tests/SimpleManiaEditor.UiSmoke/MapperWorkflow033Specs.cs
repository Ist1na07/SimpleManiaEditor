using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Editing;
using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

internal static class MapperWorkflow033Specs
{
    private const string Chart = "osu file format v14\n[General]\nAudioFilename: song.wav\nMode:3\n[Metadata]\nTitle:Reference033\nArtist:Fixture\nCreator:Mapper\nVersion:7K\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n475,500,4,1,0,0,1,0\n[HitObjects]\n36,192,1000,1,0,0:0:0:0:\n109,192,1500,128,0,2000:0:0:0:0:\n";
    private static string Fixture(string root)
    {
        var game = Path.Combine(root, "game033"); var folder = Path.Combine(game, "Songs", "Source"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(game, "osu!.exe"), "synthetic, never executed");
        File.WriteAllText(Path.Combine(folder, "map.osu"), Chart);
        using var writer = new BinaryWriter(File.Create(Path.Combine(folder, "song.wav")));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 44100 * 2);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(88200); writer.Write(new byte[88200]);
        return Path.Combine(folder, "map.osu");
    }
    public static void Storage(string root)
    {
        var chart = Fixture(root); var source = File.ReadAllBytes(chart); var maps = Path.Combine(root, "work033");
        var standard = Path.Combine(Path.GetDirectoryName(chart)!, "standard.osu");
        File.WriteAllText(standard, Chart.Replace("Mode:3\n", ""));
        Require(DesktopChartLibraryService.ReadChart(standard, Path.GetDirectoryName(chart)!) == null, "omitted Mode (osu standard) leaked into mania library");
        File.WriteAllText(standard, Chart.Replace("Mode:3", "Mode:0"));
        Require(DesktopChartLibraryService.ReadChart(standard, Path.GetDirectoryName(chart)!) == null, "non-mania chart leaked into library");
        var migrationProfile = Path.Combine(root, "index-migration033"); var oldIndex = new MapperLibraryStore(migrationProfile);
        oldIndex.SetAnnotation(chart, new(["keep"], "keep this annotation"));
        oldIndex.UpdateHeaders([new(chart, source.Length, File.GetLastWriteTimeUtc(chart).Ticks, DesktopChartLibraryService.ReadChart(chart, Path.GetDirectoryName(chart)!))]);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + oldIndex.DatabasePath))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE index_metadata SET value='1' WHERE key='header_parser'"; cmd.ExecuteNonQuery(); }
        var migrated = new MapperLibraryStore(migrationProfile);
        Require(migrated.LoadHeaders().Count == 0 && migrated.GetAnnotation(chart).Tags.Contains("keep"), "header-rule migration removed personal data or retained stale non-mania entries");
        Directory.CreateDirectory(maps); var cache = Path.Combine(root, "read-cache033");
        var workspace = new DesktopDocumentWorkspace(maps, cache, "fixture"); workspace.InitializeAsync().GetAwaiter().GetResult();
        var tab = workspace.OpenReadOnlyAsync(chart).GetAwaiter().GetResult();
        Require(tab.SourcePath == chart && tab.IsLibraryReadOnly && tab.Session.IsReadOnly && !tab.Session.CanEditNotes && !tab.Session.CanEditTiming && !tab.Session.CanEditMetadata, "original was copied or not protected");
        Require(!Directory.EnumerateFiles(maps, "*", SearchOption.AllDirectories).Any(), "read-only viewing populated editable workspace");
        var count = tab.Session.Document.Notes.Count; tab.Session.Mutate(d => d.Notes.Clear());
        Require(tab.Session.Document.Notes.Count == count && !tab.Session.CanUndo, "read-only mutation or history allowed");
        Reject(() => workspace.SaveAsync(tab.Id).GetAwaiter().GetResult()); Reject(() => workspace.SaveAsAsync(tab.Id, Path.Combine(maps, "oops.osu")).GetAwaiter().GetResult());
        Require(WindowsAudioTransportViewModel.ResolveDocumentAudioPath(tab) == Path.Combine(Path.GetDirectoryName(chart)!, "song.wav"), "read-only audio no longer resolves to source");
        workspace.PersistAsync().GetAwaiter().GetResult();
        var restored = new DesktopDocumentWorkspace(maps, cache, "fixture"); restored.InitializeAsync().GetAwaiter().GetResult();
        Require(restored.ActiveTab?.IsLibraryReadOnly == true && restored.ActiveTab.Session.IsReadOnly, "restart lost read-only protection");
        var plan = MapsetTransferService.Preview(Path.GetDirectoryName(chart)!, maps, false);
        new MapsetTransferService(Path.Combine(root, "clone033"), gameRunning: () => true).ExecuteAsync(plan).GetAwaiter().GetResult();
        Require(File.Exists(Path.Combine(plan.Destination, "song.wav")), "clone omitted mapset audio");
        var editable = workspace.OpenAsync(Path.Combine(plan.Destination, "map.osu")).GetAwaiter().GetResult();
        Require(!editable.Session.IsReadOnly && !editable.IsLibraryReadOnly && editable.SourcePath != chart, "clone did not enter editable mode");
        editable.Session.Mutate(d => d.Notes.Clear());
        Require(source.SequenceEqual(File.ReadAllBytes(chart)), "read-only view or clone changed original map");
        var notes = new[] { new ChartNote { Lane = 2, Beat = new(8, 0, 1) }, new ChartNote { Lane = 4, Beat = new(8, 1, 2), EndBeat = new(11, 0, 1) } };
        var pattern = PatternLibrary.Capture("跨 BPM 折返", 7, notes, "104 BPM source");
        var seventh = new ChartNote { Beat = new(0, 1, 7) };
        Require(PatternLibrary.ScaleNotes([seventh], 1)[0].Beat == seventh.Beat && PatternLibrary.ScaleNotes([seventh], 2)[0].Beat == new BeatPosition(0, 2, 7), "musical scaling introduced fractional-grid drift");
        var library = new PatternLibrary(Path.Combine(root, "patterns033")); library.Save(pattern);
        var loaded = new PatternLibrary(Path.Combine(root, "patterns033")).List().Single();
        Require(loaded.Notes[0].Beat == "0:0/1" && loaded.Notes[0].Lane == 0, "pattern retained source absolute position");
        var placed = NotePastePlacement.Preview(PatternLibrary.Materialize(loaded, 2), new(16, 0, 1), 3, 7);
        Require(placed[0].Lane == 3 && placed[1].Lane == 5 && placed[0].Beat.Value == 16 && placed[1].Beat.Value == 17 && placed[1].EndBeat!.Value.Value == 22, "relative beats/width/scale incorrect");
        var a = new TimingMap([new TimingEvent { Bpm = 104 }]); var b = new TimingMap([new TimingEvent { Bpm = 180 }]);
        Require(Math.Abs(a.BeatToMilliseconds(new(3, 0, 1)) / b.BeatToMilliseconds(new(3, 0, 1)) - 180d / 104) < .00001, "beat-based reuse is not tempo-independent");
        File.WriteAllText(Path.Combine(library.DirectoryPath, "broken.json"), "broken");
        Require(library.List().Length == 1 && library.ReadWarnings.Count == 1 && File.Exists(Path.Combine(library.DirectoryPath, "broken.json")), "bad pattern file was lost or hid valid patterns");
        Reject(() => library.Save(pattern)); Reject(() => PatternLibrary.Materialize(pattern, double.NaN));
        Console.WriteLine("PASS 033 read-only view/restart/save guard, clone while game is running, source preservation, musical patterns/storage/corruption handling");
    }

    public static void Pointer(string screenshots)
    {
        var session = new EditorSession { SnapDivision = 4 }; session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var surface = new TimelineSurface { Session = session, InteractionProfile = EditorInteractionProfile.OsuMania };
        var window = new Window { Width = 1000, Height = 800, Content = surface }; window.Show(); Program.Pump(50);
        surface.SetCurrentBeat(4); surface.SetZoom(70);
        var target = surface.GetPlacementEvidenceProbe()!.TapTarget;
        Point At(double beat) => new(target.SurfacePoint.X, surface.Bounds.Height * surface.JudgementLinePosition - (beat - 4) * surface.PixelsPerBeat);
        var notes = new[] { new ChartNote { Lane = 0, Beat = BeatPosition.Zero }, new ChartNote { Lane = 2, Beat = new(1, 0, 1), EndBeat = new(2, 0, 1) } };
        surface.BeginPatternPaste(notes); surface.MovePendingPaste(At(4));
        Require(session.Document.Notes.Count == 0 && surface.HandlePatternPasteKey(Key.Enter), "ghost mutated chart before placement or Enter did not pin origin");
        surface.MovePendingPaste(At(7)); Require(Math.Abs(surface.PatternPasteScale - 1.5) < .001, "end-point scaling did not follow grid");
        Require(surface.HandlePatternPasteKey(Key.Enter) && session.Document.Notes.Count == 2 && session.Document.Notes.Max(n => n.EndBeat?.Value ?? n.Beat.Value) == 7 && session.CanUndo, "endpoint placement failed");
        Require(session.Undo() && session.Document.Notes.Count == 0, "pattern is not one undoable action");
        surface.BeginPatternPaste(notes); surface.MovePendingPaste(At(4));
        var division = session.SnapDivision;
        window.MouseWheel(At(4), new Vector(0, 1), RawInputModifiers.Control);
        Require(surface.PatternPasteScale > 1 && session.SnapDivision == division, "pattern Ctrl+wheel changed grid instead of pattern scale");
        surface.CancelPendingPaste(); Require(session.Document.Notes.Count == 0 && !surface.HasPendingPaste, "cancel left pending state or changed notes");
        var picture = new PatternShapeSurface { Notes = notes, Keys = 7, Width = 220, Height = 240 };
        window.Content = picture; Program.Pump(60);
        using (var image = window.CaptureRenderedFrame()!) image.Save(Path.Combine(screenshots, "pattern-shape033.png"), PngBitmapEncoderOptions.Default);
        window.Close(); Program.Pump(20);
        Console.WriteLine("PASS 033 pattern ghost, start/end scaling, Ctrl+wheel precedence, atomic undo and cancellation");
    }

    public static void Workbench(MainWindow window, string root, string screenshots)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel"); var chart = Fixture(Path.Combine(root, "ui033"));
        Wait(model.AddLibraryRootAsync(Path.GetDirectoryName(Path.GetDirectoryName(chart)!)!));
        Wait(model.CreateAsync("MyWorkspace033"));
        Call(window, "ShowLibrary", false); Program.Pump(200);
        var left = Field<ListBox>(window, "libraryList"); var right = Find<ListBox>(window, "Library.ReadOnlyList");
        Require(Field<Grid>(window, "libraryView").IsVisible, "library page was not actually shown");
        Require(left.ItemsSource!.Cast<DesktopLibraryFolderView>().All(v => v.VisibleCharts.All(c => !c.ChartPath.Contains("game033"))) && right.ItemCount == 1, "dual library columns mixed or failed to show source");
        using (var image = window.CaptureRenderedFrame()!) image.Save(Path.Combine(screenshots, "library-split033.png"), PngBitmapEncoderOptions.Default);
        // A large library must materialize only visible rows, then show the
        // correct metadata after recycling/scrolling to the far end.
        // These rows deliberately have no files. Pause the real watcher so a
        // queued mapset-directory event cannot legitimately reconcile them away.
        model.StopLibraryWatching(); Program.Pump(30);
        var actual = model.LibraryEntries.ToArray();
        var huge = Enumerable.Range(0, 20000).Select(i => new DesktopLibraryChartEntry {
            ChartPath = Path.Combine(Path.GetDirectoryName(chart)!, $"fake-{i}.osu"), FolderPath = Path.GetDirectoryName(chart)!,
            RelativePath = $"fake-{i}.osu", Title = $"Synthetic {i:D5}", Artist = "Benchmark", Creator = "LargeLibrary", Version = "7K", Format = "osu!mania", KeyCount = 7 }).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        model.LibraryEntries.Clear(); foreach (var item in huge) model.LibraryEntries.Add(item);
        Call(window, "RefreshMapperLibraryBrowser"); Program.Pump(120);
        Require(right.ItemCount == 20000 && right.GetVisualDescendants().OfType<ListBoxItem>().Count() < 100, $"large library was not virtualized: items={right.ItemCount}, rows={right.GetVisualDescendants().OfType<ListBoxItem>().Count()}, indexed={model.LibraryEntries.Count}, scanning={model.IsScanning}, reconciled={model.LibraryReconciled}");
        right.ScrollIntoView(19999); Program.Pump(150);
        Require(right.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Synthetic 19999") == true), "recycled library row displays stale title");
        Console.WriteLine($"PASS 033 20,000-row library partition/layout/scroll {watch.ElapsedMilliseconds} ms; {right.GetVisualDescendants().OfType<ListBoxItem>().Count()} materialized rows");
        model.LibraryEntries.Clear(); foreach (var item in actual) model.LibraryEntries.Add(item); Call(window, "RefreshMapperLibraryBrowser");
        typeof(MainWindowViewModel).GetField("libraryWatchingStopped", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, false);
        Call(model, "RestartLibraryWatcher");
        Wait((Task)Call(window, "OpenLibraryReadOnlyAsync", chart)!); Program.Pump(120);
        var tab = model.ActiveTab!; Require(tab.IsLibraryReadOnly && tab.SourcePath == chart, "library open did not enter read-only view");
        var sourceBytes = File.ReadAllBytes(chart); tab.Session.Selection.UnionWith(tab.Session.Document.Notes.Select(n => n.Id));
        Wait((Task)Call(window, "AddEditorTrackAsync", EditorTrackKind.Pattern, null)!);
        var localCapture = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "加入主轨选区");
        localCapture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.PumpUntil(() => Field<LumaChart.Avalonia.Infrastructure.EditorTrackWorkspace>(window, "activeTrackWorkspace").Tracks.Any(t => t.Kind == EditorTrackKind.Pattern && t.Session!.Document.Notes.Count == 2));
        var tracks = Field<EditorTrackWorkspace>(window, "activeTrackWorkspace"); var track = tracks.Tracks.Single(t => t.Kind == EditorTrackKind.Pattern);
        Require(!track.Session!.IsReadOnly && tab.Session.IsReadOnly && sourceBytes.SequenceEqual(File.ReadAllBytes(chart)), "extracting a pattern unlocked or changed source");
        var views = (System.Collections.IDictionary)typeof(MainWindow).GetField("secondaryTrackViews", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var view = views[track.Id]!; var save = (Task)Call(window, "SavePatternTrackAsync", view)!;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "保存排列")); var dialog = window.OwnedWindows.Single(w => w.Title == "保存排列");
        Find<TextBox>(dialog, "Pattern.Name").Text = "UI 跨曲排列";
        Find<Button>(dialog, "Pattern.Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(save);
        Require(new PatternLibrary(AvaloniaAppPaths.LocalDataRoot).List().Any(p => p.Name == "UI 跨曲排列"), "visible save did not persist pattern");
        Wait((Task)Call(window, "AddEditorTrackAsync", EditorTrackKind.Reference, chart)!);
        Require(window.GetLogicalDescendants().OfType<EditorOverviewSurface>().Count() >= 2, "reference track density navigation missing");
        using (var image = window.CaptureRenderedFrame()!) image.Save(Path.Combine(screenshots, "readonly-pattern-track033.png"), PngBitmapEncoderOptions.Default);
        var picker = (Task)Call(window, "PickIndexedChartAsync")!;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "添加参考谱面")); dialog = window.OwnedWindows.Single(w => w.Title == "添加参考谱面");
        Require(dialog.GetLogicalDescendants().OfType<ListBox>().Single().ItemCount >= 2, "reference picker did not use indexed library"); dialog.Close(); Wait(picker);
        var shelf = (Task)Call(window, "ShowPatternLibraryAsync")!;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "排列库")); dialog = window.OwnedWindows.Single(w => w.Title == "排列库");
        Require(Find<ListBox>(dialog, "Pattern.LibraryList").ItemCount >= 1, "saved pattern not visible in shelf"); Program.Pump(100);
        using (var image = dialog.CaptureRenderedFrame()!) image.Save(Path.Combine(screenshots, "pattern-shelf033.png"), PngBitmapEncoderOptions.Default);
        dialog.Close(); Wait(shelf);
        Wait((Task)Call(window, "CloneActiveReadOnlyAsync")!);
        Require(model.ActiveTab?.IsLibraryReadOnly == false && model.ActiveTab.SourcePath != chart && sourceBytes.SequenceEqual(File.ReadAllBytes(chart)), "visible clone action did not preserve/read-only separate original");
        shelf = (Task)Call(window, "ShowPatternLibraryAsync")!;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "排列库")); dialog = window.OwnedWindows.Single(w => w.Title == "排列库");
        Find<ListBox>(dialog, "Pattern.LibraryList").SelectedIndex = 0; Find<Button>(dialog, "Pattern.Use").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(shelf);
        var main = Field<TimelineSurface>(window, "timeline"); var previousCount = model.ActiveTab!.Session.Document.Notes.Count;
        Require(main.HasPendingPaste && previousCount == 2, "taking a pattern did not begin non-mutating ghost");
        main.SetCurrentBeat(8); var probe = main.GetPlacementEvidenceProbe()!.TapTarget.SurfacePoint;
        var point = main.TranslatePoint(probe, window)!.Value;
        window.MouseMove(point); main.Focus();
        Call(window,"SetProgrammaticPlaybackBeat",0d);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Program.PumpUntil(() => (bool)Call(window, "IsEditorPlaybackActive")!);
        Console.WriteLine("PASS 033 split-library UI, original read-only viewing, extract/save/shelf workflow, indexed reference chooser and navigation");
    }
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().First(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException) { return; } throw new Exception("Unsafe action accepted"); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
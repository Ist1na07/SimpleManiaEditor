using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;
using Microsoft.Data.Sqlite;

internal static class MapperWorkflow034Specs
{
    private static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Hidden)!.Invoke(target, args);
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Hidden)!.SetValue(target, value);
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Find<T>(Control parent, string id) where T : Control => parent.GetLogicalDescendants().OfType<T>().First(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Marquee()
    {
        foreach (var readOnly in new[] { false, true })
        {
            var session = new EditorSession(); session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
            session.Mutate(d => d.Notes.AddRange(Enumerable.Range(4, 4).Select(beat => new ChartNote { Lane = 3, Beat = new(beat, 0, 1) })));
            session.IsReadOnly = readOnly;
            var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Select };
            var window = new Window { Width = 1000, Height = 800, Content = surface }; window.Show(); Program.Pump(40);
            surface.SetZoom(70); surface.SetCurrentBeat(4);
            var probe = surface.GetPlacementEvidenceProbe()!.TapTarget; var lane = surface.GridLaneWidth;
            var left = probe.SurfacePoint.X - (probe.Lane + .5) * lane;
            double Y(double beat) => surface.Bounds.Height * surface.JudgementLinePosition - (beat - 4) * surface.PixelsPerBeat;
            var start = new Point(left + 2, Y(3.8)); var end = new Point(left + lane * 7 - 2, Y(4.8));
            window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Require(session.Selection.Count == 1, "initial marquee should include beat 4 only");
            surface.SetPlaybackActive(true); Program.Pump(20); surface.SetCurrentBeat(6);
            Require(session.Selection.Count == 3 && session.Document.Notes.Where(n => session.Selection.Contains(n.Id)).All(n => n.Beat.Value <= 6), "stationary mouse during playback did not expand musical selection");
            window.MouseUp(end, MouseButton.Left); surface.SetPlaybackActive(false); window.Close(); Program.Pump(20);
        }
        Console.WriteLine("PASS 034 playback expands marquee with stationary pointer, keeps origin/earlier notes, editable and read-only sessions");
    }

    public static void Workbench(MainWindow window, string root)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var source = (string)typeof(MapperWorkflow033Specs).GetMethod("Fixture", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Path.Combine(root, "readonly034")])!;
        Wait(model.AddLibraryRootAsync(Path.GetDirectoryName(Path.GetDirectoryName(source)!)!));
        var legacyDirectory = Path.Combine(root, "older-build034", "LumaChart Workspace", "Legacy"); Directory.CreateDirectory(legacyDirectory);
        var legacy = Path.Combine(legacyDirectory, "legacy.osu");
        File.WriteAllText(legacy, File.ReadAllText(source).Replace("Title:Reference033", "Title:Legacy034"));
        File.Copy(Path.Combine(Path.GetDirectoryName(source)!, "song.wav"), Path.Combine(legacyDirectory, "song.wav"));
        Field<EditorLibraryCatalog>(model, "libraryCatalog").Remember([legacy]); Wait(model.RefreshLibraryAsync());
        Require(model.LibraryReconciled, "full reconciliation not recorded");
        Call(window, "ShowLibrary", false); Program.Pump(80);
        Require(Field<ListBox>(window, "libraryList").ItemsSource!.Cast<LumaChart.Application.Libraries.DesktopLibraryFolderView>().Any(v => v.VisibleCharts.Any(c => c.ChartPath == legacy)), "legacy personal workspace still excluded from left pane");
        Wait(model.OpenAsync(legacy)); Call(window, "ShowEditorSurface", true);
        var ownTab = model.ActiveTab!;
        Require(ownTab.SourcePath == legacy && !ownTab.Session.IsReadOnly, "opening indexed personal legacy file cloned or locked it");
        Wait((Task)Call(window, "OpenLibraryReadOnlyAsync", source)!); Program.Pump(100); var readOnly = model.ActiveTab!;
        Set(window, "metadataEditorDirty", true);
        Field<TextBox>(window, "metadataOverallDifficultyBox").Text = "not a number";
        Require((bool)Call(window, "TryApplyPendingMetadataEditor", readOnly, false)!, "read-only draft blocked navigation preflight");
        Require((bool)Call(window, "ApplyMetadataEditor", false, true)! && !readOnly.Session.IsDirty, "read-only implicit apply should reset presentation, not demand permission");
        Set(window, "metadataEditorDirty", true);
        var switchButton = window.GetLogicalDescendants().OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == "Editor.ChartTab" && AutomationProperties.GetName(b)?.Contains("Legacy034") == true);
        switchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.Pump(60);
        Require(model.ActiveTab == ownTab && !Field<Control>(window, "roomPermissionNoticeLayer").IsVisible, "actual tab click was blocked by local read-only permission popup");
        model.Activate(readOnly.Id); Set(window, "metadataEditorDirty", true);
        Wait((Task)Call(window, "CloseTabWithPromptsAsync", readOnly)!);
        Require(model.Tabs.All(t => t.Id != readOnly.Id) && File.Exists(source), "read-only tab could not close or source was removed");
        model.Activate(ownTab.Id); ownTab.Session.Selection.UnionWith(ownTab.Session.Document.Notes.Select(n => n.Id));
        Wait((Task)Call(window, "AddSelectionToPatternTrackAsync")!);
        var workspace = Field<EditorTrackWorkspace>(window, "activeTrackWorkspace"); var pattern = workspace.Tracks.Single(t => t.Kind == EditorTrackKind.Pattern);
        var views = (System.Collections.IDictionary)typeof(MainWindow).GetField("secondaryTrackViews", Hidden)!.GetValue(window)!;
        var save = (Task)Call(window, "SavePatternTrackAsync", views[pattern.Id])!;
        Program.PumpUntil(() => window.OwnedWindows.Any(w => w.Title == "保存排列")); var dialog = window.OwnedWindows.Single(w => w.Title == "保存排列");
        Require(Find<TextBox>(dialog, "Pattern.Name").Text?.StartsWith("Legacy034") == true, "pattern has no useful default name"); dialog.Close(); Wait(save);
        Wait((Task)Call(window, "OpenLibraryReadOnlyAsync", source)!); Set(window, "metadataEditorDirty", true);
        window.Close(); Program.PumpUntil(() => !window.IsVisible);
        Require(!Field<Control>(window, "roomPermissionNoticeLayer").IsVisible, "window exit displayed observer permission popup");
        Console.WriteLine("PASS 034 legacy workspace visible/editable in place, local read-only invalid draft cannot block tab switch/close/application exit, default pattern name");
    }

    public static void BenchmarkCache(string root, string sourceDatabase, string sourceCatalog)
    {
        // Snapshot via SQLite's read-only backup API. Never run schema creation,
        // WAL writes or background watchers against the user's actual database.
        var profile = AvaloniaAppPaths.LocalDataRoot; Directory.CreateDirectory(profile);
        using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        using (var target = new SqliteConnection("Data Source=" + Path.Combine(profile, "mapper-library.sqlite3")))
        { source.Open(); target.Open(); source.BackupDatabase(target); }
        File.Copy(sourceCatalog, Path.Combine(profile, "library-catalog.json"));
        object Measure()
        {
            var watch = Stopwatch.StartNew();
            var model = new MainWindowViewModel(new AvaloniaWorkspaceController(Path.Combine(root, "maps"), Path.Combine(root, "cache"), "benchmark"));
            Wait(model.LoadCachedLibrarySnapshotAsync());
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var personal = model.LibraryEntries.Count(c => model.IsWorkspaceChart(c.ChartPath));
            Require(model.LibraryEntries.Count > 0 && !model.LibraryReconciled, "warm loading unexpectedly needed directory reconciliation");
            return new { Charts = model.LibraryEntries.Count, PersonalCharts = personal, SnapshotMilliseconds = elapsed };
        }
        var report = new { First = Measure(), Warm = Measure(), SourceOpenedReadOnly = true, FullDirectoryScan = false };
        File.WriteAllText(Path.Combine(root, "warm-index034.json"), JsonSerializer.Serialize(report));
        Console.WriteLine(JsonSerializer.Serialize(report)); Console.WriteLine("PASS isolated real-index snapshot benchmark " + root);
    }
}

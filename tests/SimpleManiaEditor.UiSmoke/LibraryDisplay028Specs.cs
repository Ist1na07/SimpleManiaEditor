using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Libraries;
using LumaChart.Application.Settings;
using LumaChart.Application.Workspaces;
using LumaChart.Application.Exporting;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class LibraryDisplay028Specs
{
    public static void RunSafety(string root)
    {
        var game = Path.Combine(root, "fake-osu-install");
        var set = Path.Combine(game, "renamed-library", "Test song");
        Directory.CreateDirectory(set);
        File.WriteAllText(Path.Combine(game, "osu!.exe"), "fake fixture, never executed");
        File.WriteAllText(Path.Combine(game, "osu!.db"), "database fixture");
        File.WriteAllText(Path.Combine(game, "osu!.user.cfg"), "ManiaSpeed:20");
        var source = Path.Combine(set, "map.osu");
        OsuManiaSerializer.Save(new ChartDocument { KeyCount = 7, Notes = [new ChartNote { Lane = 0, Beat = new(4, 0, 1) }] }, source);
        var before = Snapshot(game);
        var maps = Path.Combine(root, "safe-028-maps");
        var workspace = new DesktopDocumentWorkspace(maps, Path.Combine(root, "safe-028-editing"), "guest");
        var tab = workspace.OpenAsync(source).GetAwaiter().GetResult();
        Require(OsuFileSafetyPolicy.IsWithin(tab.SourcePath, maps), "renamed osu library did not use a creation copy");
        tab.Session.Mutate(d => d.Metadata.Title = "Edited copy");
        Require(workspace.SaveAsync(tab.Id).GetAwaiter().GetResult().Saved, "creation copy failed to save");
        Reject(() => workspace.SaveAsAsync(tab.Id, source).GetAwaiter().GetResult());
        var exportOptions = new ChartExportOptions(ChartExportFormat.OsuMania, ChartExportMode.SingleChart, true, 1, 1, 1, .1);
        Reject(() => ChartExportService.ExportAsync(new(tab.Session.Document, source, exportOptions)).GetAwaiter().GetResult());
        Reject(() => OsuFullPackageExportService.ExportAsync(new(tab.Session.Document, tab.SourcePath, Path.Combine(game, "export.osz"), Path.Combine(root, "export-028"), 8, 8)).GetAwaiter().GetResult());
        var chart = DesktopChartLibraryService.ReadChart(source, game)!;
        var folder = DesktopChartLibraryService.GroupByFolder([chart], game).Single();
        Require(!DesktopLibraryDeletionPlanner.Create(game, [folder]).CanExecute && !DesktopLibraryChartDeletionPlanner.Create(game, chart).CanExecute,
            "library deletion can still modify an osu install");
        Reject(() => new DesktopDocumentWorkspace(maps, Path.Combine(game, "cache"), "guest"));
        Require(before.SequenceEqual(Snapshot(game)), "opening, save, export or deletion changed original game files");

        if (OperatingSystem.IsWindows())
        {
            var alias = Path.Combine(root, "game-alias-028");
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", alias, set }) info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info)!; process.WaitForExit();
            Require(process.ExitCode == 0, "junction fixture failed: " + process.StandardError.ReadToEnd());
            Require(OsuFileSafetyPolicy.IsProtectedPath(Path.Combine(alias, "not-created.osu")), "ancestor junction bypassed osu write protection");
            Reject(() => workspace.SaveAsAsync(tab.Id, Path.Combine(alias, "alias.osu")).GetAwaiter().GetResult());
            Require(before.SequenceEqual(Snapshot(game)), "junction write protection changed original files");
        }
        Console.WriteLine("PASS protected osu installation, renamed Songs, junctions, source checksums, save/export/delete/cache isolation");
    }

    public static void Run(MainWindow window, string screenshots)
    {
        Require(DisplayScalePolicy.RecommendUiScale(1920, 1080) == .75 && DisplayScalePolicy.RecommendUiScale(2560, 1440) == 1 &&
            DisplayScalePolicy.RecommendUiScale(2560 / 1.25, 1440 / 1.25) == .8, "small-screen defaults ignore effective DPI");
        var auto = new WindowsDisplayScaleSettings().InitializeForFirstScreen(1920, 1080).InitializeForFirstScreen(2560, 1440);
        Require(auto.AutomaticUiScale && auto.UiScale == 1 && auto.WithUiScale(.9).InitializeForFirstScreen(1920, 1080).UiScale == .9,
            "automatic mode overrides manual scale");
        WindowsDisplayScaleSettingsStore.Save(auto.WithUiScale(.7));
        Require(!WindowsDisplayScaleSettingsStore.Load().AutomaticUiScale && WindowsDisplayScaleSettingsStore.Load().UiScale == .7, "manual scale did not persist");
        var package = Path.Combine(Path.GetTempPath(), "sme-package-028-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(package, "app")); File.WriteAllText(Path.Combine(package, "Start-Editor.cmd"), "fixture");
        Require(AvaloniaAppPaths.ResolvePackageRoot(Path.Combine(package, "app")) == package, "default Maps belongs inside app instead of package");

        var model = Field<MainWindowViewModel>(window, "viewModel");
        var original = model.ActiveTab!;
        var maps = model.CreationRoot;
        for (var index = 0; index < 6; index++) Wait(model.CreateAsync("Library028 song " + index));
        Require(model.LibraryEntries.Count(chart => chart.Title.StartsWith("Library028 song ")) == 6, "new charts remain absent from library");
        var old = model.ActiveTab!;
        var alternate = Path.Combine(Path.GetDirectoryName(maps)!, "alternate-028");
        Wait(model.SwitchWorkspaceAsync(alternate));
        Require(model.LibraryEntries.Any(chart => chart.ChartPath == old.SourcePath), "workspace switch lost existing creations");
        var external = Path.Combine(Path.GetDirectoryName(maps)!, "external-028");
        Directory.CreateDirectory(external);
        var imported = Path.Combine(external, "external.osu");
        OsuManiaSerializer.Save(new ChartDocument { KeyCount = 7, Metadata = new ChartMetadata { Title = "External028", AudioFile = "simulation.wav" } }, imported);
        Program.WriteSimulationWave(Path.Combine(external, "simulation.wav"));
        File.WriteAllText(Path.Combine(external, "personal-settings.json"), "private unrelated fixture");
        Directory.CreateDirectory(Path.Combine(external, "Unrelated")); File.WriteAllText(Path.Combine(external, "Unrelated", "secret.txt"), "not a chart resource");
        var sourceBytes = File.ReadAllBytes(imported);
        Wait(model.OpenAsync(imported));
        Require(OsuFileSafetyPolicy.IsWithin(model.ActiveTab!.SourcePath, alternate) && File.Exists(Path.Combine(Path.GetDirectoryName(model.ActiveTab.SourcePath)!, "simulation.wav")),
            "external import missing managed chart/audio copy");
        Require(!File.Exists(Path.Combine(Path.GetDirectoryName(model.ActiveTab.SourcePath)!, "personal-settings.json")) &&
            !Directory.Exists(Path.Combine(Path.GetDirectoryName(model.ActiveTab.SourcePath)!, "Unrelated")), "import copied unrelated source directory content");
        model.ActiveTab.Session.Mutate(d => d.Metadata.Title = "Updated028");
        Wait(model.SaveAndVerifyAsync());
        Require(File.ReadAllBytes(imported).SequenceEqual(sourceBytes) && model.LibraryEntries.Any(chart => chart.Title == "Updated028"), "save modified external original or left stale metadata");
        model.SetLibrarySearch("Library028 song"); Require(model.LibraryFolders.Sum(folder => folder.VisibleCharts.Count) == 6, "title search not finding creations");
        model.SetLibrarySearch("");
        var songs = Path.Combine(Path.GetDirectoryName(maps)!, "readonly-028", "Songs");
        Directory.CreateDirectory(songs); File.Copy(imported, Path.Combine(songs, "readonly.osu"));
        Wait(model.AddLibraryRootAsync(songs)); model.SetLibrarySourceFilter(songs);
        Require(model.LibraryFolders.Sum(folder => folder.VisibleCharts.Count) == 1, "read-only source filter broken");
        Wait(model.RemoveLibraryRootAsync(songs));
        Require(File.Exists(Path.Combine(songs, "readonly.osu")) && model.LibrarySourceFilter == null, "remove library link removed original files");
        Wait(model.SwitchWorkspaceAsync(maps)); model.SetLibrarySearch("");
        var restored = new MainWindowViewModel(new AvaloniaWorkspaceController(maps, Path.Combine(Path.GetDirectoryName(maps)!, "reopen-028"), "guest"));
        Wait(restored.InitializeAsync(restorePreviousSession: false));
        Require(restored.LibraryEntries.Any(chart => chart.Title == "Updated028"), "catalog forgets files after reopening");

        model.Activate(original.Id); Program.Pump(80);
        var add = typeof(MainWindow).GetMethod("AddEditorTrackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Wait((Task)add.Invoke(window, [EditorTrackKind.Draft, null])!);
        var scale = Field<WindowsDisplayScaleController>(window, "displayScaleController");
        var oldScale = scale.CurrentSettings;
        foreach (var (width, height, ratio) in new[] { (1920d, 1080d, .75), (2560d, 1440d, 1d) })
        {
            window.Width = width; window.Height = height; scale.UpdateUiScale(ratio); Program.Pump(160);
            var board = Field<ScrollViewer>(window, "trackScroller");
            var frames = Field<StackPanel>(window, "trackPanels").Children.OfType<EditorTrackPanel>().ToArray();
            var main = frames.Single(frame => frame.TrackKey == "main");
            var draft = frames.Last(frame => frame.TrackKey.StartsWith("track:") && frame.Width == 640);
            Require(board.Viewport.Width >= main.Width + draft.Width + 96 * 2 + 24, "1080p cannot show main, audio and draft together");
            typeof(MainWindow).GetMethod("ShowEditorSurface", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]); Program.Pump(80);
            using var frame = window.CaptureRenderedFrame()!; frame.Save(Path.Combine(screenshots, $"editor-{width:0}x{height:0}-028.png"), PngBitmapEncoderOptions.Default);
        }
        Find<Button>(window, "Library.Home").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.PumpUntil(() => !model.IsScanning); Program.Pump(80);
        Require(Field<Grid>(window, "libraryView").IsEffectivelyVisible && Find<ComboBox>(window, "Library.Source").IsEffectivelyVisible, "brand button does not open searchable library");
        var headerTitle = Field<TextBlock>(window, "libraryTitle");
        var importButton = Find<Button>(window, "Library.ImportChart");
        Require(headerTitle.TranslatePoint(new Point(0, headerTitle.Bounds.Height), window)!.Value.Y < importButton.TranslatePoint(default, window)!.Value.Y,
            "library actions overlap its title/description");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "library-028.png"), PngBitmapEncoderOptions.Default);
        scale.UpdateScales(oldScale.UiScale, oldScale.FontScale); window.Width = 1440; window.Height = 960;
        typeof(MainWindow).GetMethod("ShowEditorSurface", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]); Program.Pump(80);
        Console.WriteLine("PASS 1080p/1440p layout, automatic/manual DPI scale, six creations, import, metadata refresh, search, source links and reopened catalog");
    }
    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => file + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) + ":" + File.GetLastWriteTimeUtc(file).Ticks).ToArray();
    private static void Reject(Action action) { try { action(); } catch (InvalidOperationException exception) when (exception.Message.Contains("osu!") || exception.Message.Contains("Songs 是游戏")) { return; } throw new InvalidOperationException("protected write was allowed"); }
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

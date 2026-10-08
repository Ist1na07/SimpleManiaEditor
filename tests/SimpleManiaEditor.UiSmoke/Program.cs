using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Core.Models;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--seek-benchmark")
            return SeekPerformanceSpecs.Run(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null);
        var root = Path.Combine(Path.GetTempPath(), "sme-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("LUMACHART_AVALONIA_LOCAL_DATA_ROOT", Path.Combine(root, "settings"));
        Environment.SetEnvironmentVariable("LUMACHART_AVALONIA_EDITING_ROOT", Path.Combine(root, "editing"));
        Environment.SetEnvironmentVariable("LUMACHART_AVALONIA_WORKSPACE_ROOT", Path.Combine(root, "maps"));
        var screenshotRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "screenshots");
        Directory.CreateDirectory(screenshotRoot);
        try
        {
            TrackWorkspaceSpecs.Run(root);
            SongsProtectionSpecs.Run(root);
            AppBuilder.Configure<TestApplication>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            RunPointerWorkflow(screenshotRoot);
            InteractionRegressionSpecs.Run();
            PlacementPrecisionSpecs.Run();
            DrawToolSpecs.Run();
            PlatformAudioOutputs.Current = new SimulatedAudioOutputFactory();
            StablePlaytestUiSpecs.Run(screenshotRoot);
            OsuSkinPlaytestSpecs.Run(root, screenshotRoot);
            SkinWorkflowSpecs.Run(root, screenshotRoot);
            RunApplicationSmoke(screenshotRoot);
            Console.WriteLine("PASS headless pointer workflow, shortcuts, profile persistence, application startup and rendering");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { Console.WriteLine("Isolated UI data: " + root); }
    }

    private static void RunPointerWorkflow(string screenshots)
    {
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var timeline = new TimelineSurface { Session = session, Tool = TimelineTool.Tap };
        var window = new Window { Width = 1000, Height = 800, Content = timeline, Background = Brushes.Black };
        window.Show();
        timeline.SetCurrentBeat(4);
        Dispatcher.UIThread.RunJobs();
        var probe = timeline.GetPlacementEvidenceProbe() ?? throw new InvalidOperationException("placement geometry unavailable");
        window.MouseDown(probe.TapTarget.SurfacePoint, MouseButton.Left);
        window.MouseUp(probe.TapTarget.SurfacePoint, MouseButton.Left);
        Require(session.Document.Notes.Count == 1 && !session.Document.Notes[0].IsHold, "pointer did not place tap");
        var tap = session.Document.Notes[0];
        timeline.Tool = TimelineTool.Hold;
        window.MouseDown(probe.HoldStartTarget.SurfacePoint, MouseButton.Left);
        window.MouseMove(probe.HoldEndTarget.SurfacePoint, RawInputModifiers.LeftMouseButton);
        window.MouseUp(probe.HoldEndTarget.SurfacePoint, MouseButton.Left);
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.Any(n => n.IsHold), "pointer did not create LN");
        var hold = session.Document.Notes.First(n => n.IsHold);
        timeline.Tool = TimelineTool.Select;
        session.Selection.Clear();
        Click(window, probe.TapTarget.SurfacePoint, RawInputModifiers.Control);
        Click(window, probe.HoldStartTarget.SurfacePoint, RawInputModifiers.Control);
        Require(session.Selection.SetEquals(new[] { tap.Id, hold.Id }), "Ctrl did not add both notes to selection");
        Click(window, probe.TapTarget.SurfacePoint, RawInputModifiers.Control);
        Require(session.Selection.SetEquals(new[] { hold.Id }), "Ctrl did not toggle selected note");
        var oldZoom = timeline.PixelsPerBeat;
        window.MouseWheel(probe.TapTarget.SurfacePoint, new Vector(0, 1), RawInputModifiers.Alt);
        Require(timeline.PixelsPerBeat > oldZoom, "Alt wheel did not zoom");
        Require(session.Undo() && session.Document.Notes.Count == 1, "LN gesture was not one undo operation");
        Require(session.Redo() && session.Document.Notes.Count == 2, "LN redo failed");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("timeline render failed");
        frame.Save(Path.Combine(screenshots, "timeline.png"), PngBitmapEncoderOptions.Default);
        window.Close();

        var settings = new WindowsEditorShortcutSettings();
        Require(WindowsEditorShortcutGesture.Resolve(settings, Key.D1, KeyModifiers.None, false)?.Id == "ToolSelect", "1 is not select");
        Require(WindowsEditorShortcutGesture.Resolve(settings, Key.D3, KeyModifiers.None, false)?.Id == "ToolHold", "3 is not LN");
        settings.SelectProfile(EditorInteractionProfile.LumaLegacy);
        WindowsEditorShortcutSettingsStore.Save(settings);
        var loaded = WindowsEditorShortcutSettingsStore.Load();
        Require(loaded.Profile == EditorInteractionProfile.LumaLegacy && loaded.Bindings["ToolTap"] == "W", "legacy profile did not persist");
        settings.SelectProfile(EditorInteractionProfile.OsuMania);
        WindowsEditorShortcutSettingsStore.Save(settings);
    }

    private static void RunApplicationSmoke(string screenshots)
    {
        var window = new MainWindow([]) { Width = 1440, Height = 960 };
        window.Show();
        Pump(1500);
        Require((window.Title ?? "").Contains("SimpleManiaEditor", StringComparison.Ordinal), "fork title missing");
        var timeline = (TimelineSurface)typeof(MainWindow).GetField("timeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Require(timeline.InteractionProfile == EditorInteractionProfile.OsuMania, "application did not use osu profile");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("application render failed");
        frame.Save(Path.Combine(screenshots, "application.png"), PngBitmapEncoderOptions.Default);
        var viewModel = (MainWindowViewModel)typeof(MainWindow).GetField("viewModel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var create = viewModel.CreateAsync("UI 7K smoke");
        PumpUntil(() => create.IsCompleted);
        create.GetAwaiter().GetResult();
        var showEditor = typeof(MainWindow).GetMethod("ShowEditorSurface", BindingFlags.Instance | BindingFlags.NonPublic)!;
        showEditor.Invoke(window, showEditor.GetParameters().Select(p => p.DefaultValue).ToArray());
        Pump(100);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("new tab missing");
        Require(tab.Session.Document.KeyCount == 7 && tab.SourcePath.EndsWith(".osu", StringComparison.OrdinalIgnoreCase), "application did not create native 7K");
        timeline.SetCurrentBeat(4);
        timeline.Focus();
        Stroke(window, Key.D2);
        Require(timeline.Tool == TimelineTool.Tap, "real window shortcut 2 did not route to tap");
        var probe = timeline.GetPlacementEvidenceProbe() ?? throw new InvalidOperationException("editor placement targets unavailable");
        var origin = timeline.TranslatePoint(new Point(), window)!.Value;
        Click(window, probe.TapTarget.SurfacePoint + (Vector)origin, RawInputModifiers.None);
        Stroke(window, Key.D3);
        window.MouseDown(probe.HoldStartTarget.SurfacePoint + (Vector)origin, MouseButton.Left);
        window.MouseMove(probe.HoldEndTarget.SurfacePoint + (Vector)origin, RawInputModifiers.LeftMouseButton);
        window.MouseUp(probe.HoldEndTarget.SurfacePoint + (Vector)origin, MouseButton.Left);
        Require(tab.Session.Document.Notes.Count == 2, "real editor window did not place tap/LN");
        tab.Session.Selection.Clear();
        tab.Session.Selection.UnionWith(tab.Session.Document.Notes.Select(n => n.Id));
        Stroke(window, Key.C, RawInputModifiers.Control);
        timeline.SetCurrentBeat(10);
        Stroke(window, Key.V, RawInputModifiers.Control);
        PumpUntil(() => tab.Session.Document.Notes.Count == 4);
        Stroke(window, Key.Z, RawInputModifiers.Control);
        Require(tab.Session.Document.Notes.Count == 2, "window paste undo failed");
        Stroke(window, Key.Y, RawInputModifiers.Control);
        Require(tab.Session.Document.Notes.Count == 4, "window paste redo failed");
        using (var editorFrame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("editor frame missing"))
            editorFrame.Save(Path.Combine(screenshots, "editor.png"), PngBitmapEncoderOptions.Default);
        var checkpoint = viewModel.PersistAsync();
        PumpUntil(() => checkpoint.IsCompleted);
        Require(checkpoint.GetAwaiter().GetResult(), "editor checkpoint failed");
        var save = viewModel.SaveAndVerifyAsync(tab.Id);
        PumpUntil(() => save.IsCompleted);
        Require(save.GetAwaiter().GetResult()?.StructurallyEqual == true, "editor save/reload verification failed");
        var gridPicker = (ComboBox)typeof(MainWindow).GetField("fixedGridPicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Require(gridPicker.IsVisible && gridPicker.TranslatePoint(new Point(), window)!.Value.Y < origin.Y,
            "grid selector is not in fixed top toolbar");
        var currentBeat = timeline.CurrentBeat;
        var gridBefore = tab.Session.SnapDivision;
        window.MouseWheel(probe.TapTarget.SurfacePoint + (Vector)origin, new Vector(0, 1), RawInputModifiers.Control);
        Require(tab.Session.SnapDivision > gridBefore && timeline.CurrentBeat == currentBeat &&
            ((TextBlock)typeof(MainWindow).GetField("fixedGridText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Text == $"1/{tab.Session.SnapDivision}",
            "window Ctrl wheel did not synchronize the fixed grid selector");
        tab.Session.SnapDivision = 4;
        window.MouseWheel(probe.TapTarget.SurfacePoint + (Vector)origin, new Vector(0, 1), RawInputModifiers.Control);
        Require(tab.Session.SnapDivision == 6, "Ctrl wheel included manual-only 1/5 division");
        foreach (var (from, expected) in new[] { (6, 8), (8, 12) })
        {
            tab.Session.SnapDivision = from;
            window.MouseWheel(probe.TapTarget.SurfacePoint + (Vector)origin, new Vector(0, 1), RawInputModifiers.Control);
            Require(tab.Session.SnapDivision == expected, "Ctrl wheel included a manual-only division");
        }
        gridPicker.SelectedItem = 5;
        Require(tab.Session.SnapDivision == 5, "slider could not manually choose 1/5");
        var commandMenu = (Border)typeof(MainWindow).GetField("commandMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        commandMenu.IsVisible = true;
        Dispatcher.UIThread.RunJobs();
        var unifiedBar = window.GetVisualDescendants().OfType<Border>().Single(b => AutomationProperties.GetAutomationId(b) == "Editor.Toolbar.Fixed");
        Require(commandMenu.GetVisualAncestors().Contains(unifiedBar) && gridPicker.GetVisualAncestors().Contains(unifiedBar),
            "commands and presets were not merged into the same fixed bar");
        foreach (var id in new[] { "Editor.Adjustment.Grid", "Editor.Adjustment.Zoom", "Editor.Adjustment.Rate" })
            Require(window.GetVisualDescendants().OfType<Slider>().Single(c => AutomationProperties.GetAutomationId(c) == id).GetVisualAncestors().Contains(unifiedBar), "adjustment control is outside the unified top bar");
        Require(!window.GetVisualDescendants().OfType<Expander>().Any(e => e.Header?.ToString() == "调整"), "standalone adjustment expander remains");
        Require(window.GetVisualDescendants().OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b)?.StartsWith("Editor.Toolbar.Playtest") == true) == 1 &&
            !window.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b)?.StartsWith("Editor.Playtest.From") == true), "duplicate playtest actions remain");
        Require(timeline.TranslatePoint(new Point(), window)!.Value.Y >= commandMenu.TranslatePoint(new Point(0, commandMenu.Bounds.Height), window)!.Value.Y,
            "expanded gear menu overlaps the chart/tools row");
        using (var menuFrame = window.CaptureRenderedFrame()!) menuFrame.Save(Path.Combine(screenshots, "editor-menu.png"), PngBitmapEncoderOptions.Default);
        Require(commandMenu.IsVisible, "merged top commands were collapsed");
        var wavePath = Path.Combine(Path.GetDirectoryName(tab.WorkingPath)!, "simulation.wav");
        WriteSimulationWave(wavePath);
        SeekPerformanceSpecs.CheckCanceledPreparation(wavePath);
        tab.Session.Mutate(d => d.Metadata.AudioFile = "simulation.wav");
        var transport = (WindowsAudioTransportViewModel)typeof(MainWindow).GetField("audioTransport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        PumpUntil(() => transport.PlaybackSnapshot?.IsLoaded == true);
        Stroke(window, Key.Space);
        PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == true);
        var factory = (SimulatedAudioOutputFactory)PlatformAudioOutputs.Current!;
        var pausesBefore = factory.Outputs.Sum(output => output.PauseCalls);
        var devicesBefore = factory.Outputs.Count;
        window.MouseWheel(probe.TapTarget.SurfacePoint + (Vector)origin, new Vector(0, -1));
        var targetBeat = timeline.CurrentBeat;
        Pump(250);
        Require(transport.PlaybackSnapshot?.IsPlaying == true && factory.Outputs.Sum(output => output.PauseCalls) == pausesBefore,
            "wheel seek paused playback instead of continuing");
        Require(factory.Outputs.Count == devicesBefore, "wheel seek recreated a device instead of reusing output");
        Require(transport.PlaybackSnapshot!.Position.TotalMilliseconds >= targetBeat * 500 - 40 &&
            transport.PlaybackSnapshot.Position.TotalMilliseconds < targetBeat * 500 + 700, "wheel did not seek to target time");
        window.MouseWheel(probe.TapTarget.SurfacePoint + (Vector)origin, new Vector(0, -1));
        Stroke(window, Key.Space);
        PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == false);
        Pump(250);
        Require(transport.PlaybackSnapshot?.IsPlaying == false, "pending wheel seek resumed after explicit Space pause");
        Require(factory.Outputs.Any(output => output.Format.SampleRate == 16000 && output.BytesRead > 0) &&
            factory.Outputs.All(output => output.ReadFailure == null), "simulated sink did not consume real DSP output cleanly");
        var fixedRate = (ComboBox)typeof(MainWindow).GetField("fixedRatePicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        fixedRate.SelectedItem = 1.5;
        PumpUntil(() => Math.Abs(transport.PlaybackRate - 1.5) < .001 && transport.PlaybackSnapshot?.PlaybackRate == 1.5);
        Require(transport.PlaybackSnapshot!.PlaybackRate == 1.5, "top rate slider failed to apply >1x playback");
        fixedRate.SelectedItem = 1d;
        PumpUntil(() => Math.Abs(transport.PlaybackRate - 1) < .001 && transport.PlaybackSnapshot?.PlaybackRate == 1);
        var tapToolButton = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Editor.ToolRail.Tap");
        tapToolButton.Focus();
        var clicks = 0;
        tapToolButton.Click += (_, _) => clicks++;
        Stroke(window, Key.Space);
        PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == true);
        Stroke(window, Key.Space);
        PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == false);
        Require(clicks == 0, "Space activated the focused tool button");
        gridPicker.Focus();
        var preservedGrid = tab.Session.SnapDivision;
        Stroke(window, Key.Space);
        PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == true);
        Stroke(window, Key.Space);
        PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == false);
        Require(tab.Session.SnapDivision == preservedGrid, "Space changed the focused grid slider");
        var optionPicker = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.Bounds.Width > 0);
        Require(optionPicker.Focus(), "visible option picker did not receive focus");
        var preservedOption = optionPicker.SelectedIndex;
        Stroke(window, Key.Space);
        PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == true);
        Stroke(window, Key.Space);
        PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == false);
        Require(!optionPicker.IsDropDownOpen && optionPicker.SelectedIndex == preservedOption,
            "Space activated a focused non-text option picker");
        timeline.SetCurrentBeat(4);
        var playtestButton = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Editor.Toolbar.Playtest");
        playtestButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => window.OwnedWindows.OfType<PlaytestWindow>().Any());
        var playtest = window.OwnedWindows.OfType<PlaytestWindow>().Single();
        PumpUntil(() => playtest.Diagnostics.Playing);
        Require(Math.Abs(playtest.Diagnostics.StartMilliseconds - 2000) < .01, "one-click playtest did not start at current time");
        playtest.Close();
        Pump(200);
        var settingsTab = (TabItem)typeof(MainWindow).GetField("settingsInspectorTab", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var inspectorTabs = (TabControl)typeof(MainWindow).GetField("editorInspectorTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        inspectorTabs.SelectedItem = settingsTab;
        Dispatcher.UIThread.RunJobs();
        var skinPicker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "Editor.NoteStyle");
        skinPicker.SelectedIndex = 1;
        Require(timeline.NoteStyle == EditorNoteStyle.Malody, "skin selector did not switch editor rendering");
        using (var skinFrame = window.CaptureRenderedFrame()!) skinFrame.Save(Path.Combine(screenshots, "editor-malody.png"), PngBitmapEncoderOptions.Default);
        skinPicker.SelectedIndex = 0;
        Require(timeline.NoteStyle == EditorNoteStyle.Osu && EditorPresentationSettingsStore.LoadNoteStyle() == EditorNoteStyle.Osu,
            "skin selector did not restore/persist osu default");
        WorkbenchRegressionSpecs.Run(window, screenshots);
        tab.Session.Mutate(d => d.Metadata.AudioFile = "");
        Pump(150);
        save = viewModel.SaveAndVerifyAsync(tab.Id);
        PumpUntil(() => save.IsCompleted);
        Require(save.GetAwaiter().GetResult()?.StructurallyEqual == true, "final audio workflow save failed");
        window.Close();
        Pump(200);
    }

    private static void Click(Window window, Point point, RawInputModifiers modifiers)
    {
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
    }
    internal static void Stroke(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
    internal static void WriteSimulationWave(string path)
    {
        const int rate = 16000;
        const int frames = rate * 20;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 2);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames * 2);
        for (var i = 0; i < frames; i++) writer.Write((short)(Math.Sin(i * Math.PI * 2 * 440 / rate) * 8000));
    }
    internal static void PumpUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("UI workflow did not complete.");
            Pump(10);
        }
    }
    internal static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

internal sealed class TestApplication : global::Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = global::Avalonia.Styling.ThemeVariant.Dark;
        WindowsWpf110ControlTheme.Apply(this);
    }
}

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "sme-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("LUMACHART_AVALONIA_LOCAL_DATA_ROOT", Path.Combine(root, "settings"));
        Environment.SetEnvironmentVariable("LUMACHART_AVALONIA_EDITING_ROOT", Path.Combine(root, "editing"));
        Environment.SetEnvironmentVariable("LUMACHART_AVALONIA_WORKSPACE_ROOT", Path.Combine(root, "maps"));
        var screenshotRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, "screenshots");
        Directory.CreateDirectory(screenshotRoot);
        try
        {
            AppBuilder.Configure<TestApplication>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
            RunPointerWorkflow(screenshotRoot);
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
        window.Close();
        Pump(200);
    }

    private static void Click(Window window, Point point, RawInputModifiers modifiers)
    {
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
    }
    private static void Stroke(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("UI workflow did not complete.");
            Pump(10);
        }
    }
    private static void Pump(int milliseconds)
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

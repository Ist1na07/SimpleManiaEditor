using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using SimpleManiaEditor.PlaytestBridge;

internal static class WorkbenchSafetySpecs
{
    public static void Run(MainWindow window, string screenshots)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var timeline = Field<TimelineSurface>(window, "timeline");
        var transport = Field<WindowsAudioTransportViewModel>(window, "audioTransport");
        var scroller = Field<ScrollViewer>(window, "trackScroller");
        var bar = Field<ScrollBar>(window, "trackBoardScrollbar");
        var global = Find<Border>(window, "Editor.Global.Density");
        var position = global.TranslatePoint(new Point(), window)!.Value;
        bar.Value = bar.Maximum; Program.Pump(100);
        Require(global.TranslatePoint(new Point(), window) == position && !global.GetVisualAncestors().Contains(scroller), "global density moves with board");
        var visibility = ((Control)((Flyout)Find<Button>(window, "Editor.Views").Flyout!).Content!).GetLogicalDescendants().OfType<CheckBox>().Single(b => AutomationProperties.GetAutomationId(b) == "Editor.Views.GlobalDensity");
        visibility.IsChecked = false; Program.Pump(50);
        Require(!global.IsVisible && !EditorPresentationSettingsStore.LoadGlobalDensity(), "global density hide did not persist");
        visibility.IsChecked = true;
        Require(EditorPresentationSettingsStore.LoadGlobalDensity(), "global density show did not persist");
        bar.Value = scroller.Viewport.Width; Program.Pump(50);

        // A queued Play plus a continuous wheel seek must be fully drained
        // before the official child is launched, and stay paused underneath it.
        timeline.NavigateToBeat(4);
        var play = InvokeTask(window, "ToggleAudioPlaybackAsync");
        Program.PumpUntil(() => play.IsCompleted && transport.IsPlaying);
        var wheelPoint = timeline.TranslatePoint(new Point(timeline.Bounds.Width / 2, timeline.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(wheelPoint, new Vector(0, -1));
        var oldRunner = OfficialPlaytestLauncher.ProcessRunner; var runner = new HeldRunner();
        OfficialPlaytestLauncher.ProcessRunner = runner;
        try
        {
            var test = (Task)typeof(MainWindow).GetMethod("StartPlaytestAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [timeline.CurrentBeat, false])!;
            Require(!Field<Grid>(window, "editorView").IsEnabled, "editor input lock was not immediate");
            Program.PumpUntil(() => runner.Request != null);
            Require(!transport.IsPlaying, "music still playing when official host started");
            var notes = model.ActiveTab!.Session.Document.Notes.Count; var beat = timeline.CurrentBeat;
            Program.Stroke(window, Key.Space);
            window.MouseWheel(wheelPoint, new Vector(0, -1));
            window.MouseDown(wheelPoint, MouseButton.Left); window.MouseUp(wheelPoint, MouseButton.Left);
            var ignoredPlay = InvokeTask(window, "ToggleAudioPlaybackAsync");
            Program.Pump(250);
            Require(ignoredPlay.IsCompleted && !transport.IsPlaying && timeline.CurrentBeat == beat && model.ActiveTab.Session.Document.Notes.Count == notes,
                "editor keyboard, wheel, notes or audio remained active during playtest");
            runner.Complete.TrySetResult(); Program.PumpUntil(() => test.IsCompleted);
            test.GetAwaiter().GetResult();
            Require(Field<Grid>(window, "editorView").IsEnabled && !transport.IsPlaying, "return failed to unlock editor or resumed audio");
        }
        finally { runner.Complete.TrySetResult(); OfficialPlaytestLauncher.ProcessRunner = oldRunner; Program.Pump(50); }
        // The same lock applies to the silent hitsound clock on charts without audio.
        var tab = model.ActiveTab!; var audioFile = tab.Session.Document.Metadata.AudioFile;
        tab.Session.Mutate(d => d.Metadata.AudioFile = ""); Program.Pump(100);
        var silent = Field<LumaChart.Avalonia.Audio.WindowsKeySoundPlaybackRuntime>(window, "keySoundRuntime");
        var silentPlay = InvokeTask(window, "ToggleAudioPlaybackAsync"); Program.PumpUntil(() => silentPlay.IsCompleted);
        var pause = InvokeTask(window, "PauseAllEditorPlaybackAsync"); Program.PumpUntil(() => pause.IsCompleted);
        Require(!silent.Snapshot.IsSilentPlaying, "silent clock survives editor suspension");
        tab.Session.Mutate(d => d.Metadata.AudioFile = audioFile); Program.PumpUntil(() => transport.PlaybackSnapshot?.IsLoaded == true);

        var fixture = OsuManiaSerializer.Parse("""
            osu file format v14
            [General]
            Mode:3
            [Difficulty]
            CircleSize:7
            [TimingPoints]
            0,500,4,1,0,100,1,0
            500,-100,4,1,0,100,0,1
            1500,-100,4,1,0,100,0,0
            [HitObjects]
            36,192,500,1,0,0:0:0:0:
            109,192,550,1,0,0:0:0:0:
            """, "ruler.osu");
        var session = new EditorSession(); session.ReplaceDocument(fixture);
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Select };
        var ruler = new EventTimeRuler();
        ruler.SetDocument(session, new TimingMap(fixture.Timing), 3000, []);
        Require(ruler.Events.Count == 3 && ruler.KiaiRanges.Single() == new TimeRulerRange(500, 1500), "ruler lost timing lines or Kiai transitions");
        var host = new Window { Width = 640, Height = 600, Content = new StackPanel { Children = { surface, ruler } } };
        surface.Height = 500; ruler.Height = 64; host.Show(); Program.Pump(100);
        surface.SetCurrentBeat(1);
        using (var frame = host.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "timing-ruler.png"), PngBitmapEncoderOptions.Default);
        var labels = Field<List<TimelineTimingLabel>>(surface, "visibleTimingLabels");
        var label = labels.Single(l => l.Timing.Effect == TimingEffectKind.Scroll && l.Timing.Beat.Value == 1);
        var target = surface.TranslatePoint(label.Bounds.Center, host)!.Value;
        host.MouseDown(target, MouseButton.Right); host.MouseUp(target, MouseButton.Right);
        Require(session.Document.Timing.Count == 3 && !session.CanUndo, "timing right-click changed document/history");
        surface.Tool = TimelineTool.Erase;
        host.MouseDown(target, MouseButton.Left); host.MouseUp(target, MouseButton.Left);
        Require(session.Document.Timing.Count == 3, "eraser deleted timing event");
        double? sought = null; ruler.SeekRequested += beat => sought = beat;
        var rulerPoint = ruler.TranslatePoint(new Point(ruler.Bounds.Width / 2, 20), host)!.Value;
        host.MouseDown(rulerPoint, MouseButton.Left); host.MouseUp(rulerPoint, MouseButton.Left);
        Require(sought.HasValue && Math.Abs(sought.Value - 3) < .001, "ruler did not seek by audio time");
        host.Close();
        var local = EditorMetricTrackSurface.CalculateLocal(fixture, false);
        Require(local.Length > 50 && local.Max(s => s.Value) == 4 && local[0].Value == 0 && local[^1].Value == 0,
            "local density did not use a smooth centred NPS window");
        Require(EditorMetricTrackSurface.Calculate(fixture, false).Length < local.Length, "local analysis still uses global bins");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "workbench-global-ruler.png"), PngBitmapEncoderOptions.Default);
        Console.WriteLine("PASS playtest exclusive music/input/silent-clock ownership, fixed hideable global overviews, timing right/erase safety, Kiai ruler/time seek and continuous local density");
    }
    private sealed class HeldRunner : IOfficialPlaytestProcess
    {
        public PlaytestRequest? Request;
        public TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunAsync(string path) { Request = PlaytestRequest.Read(path); return Complete.Task; }
    }
    private static Task InvokeTask(object root, string name) => (Task)root.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(root, null)!;
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(root)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

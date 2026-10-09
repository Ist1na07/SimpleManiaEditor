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
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;

internal static class StartupPlaybackSpecs
{
    public static void Run(MainWindow window, string screenshots)
    {
        var scroller = Field<ScrollViewer>(window, "trackScroller");
        var timeline = Field<TimelineSurface>(window, "timeline");
        // Test delayed layout from a hidden/shown editor, then resizing and tab rebuilds.
        Invoke(window, "RestoreBoardPanAfterLayout", 0d); Program.Pump(100);
        var main = Field<Control>(window, "mainTrackFrame");
        var point = main.TranslatePoint(new Point(), scroller)!.Value;
        Require(Math.Abs(point.X) < 2, $"main track starts behind empty padding: {point.X}");
        var oldWidth = window.Width; window.Width = oldWidth - 250; Program.Pump(100);
        Require(Math.Abs(main.TranslatePoint(new Point(), scroller)!.Value.X) < 2, "resize lost main alignment");
        window.Width = oldWidth; Program.Pump(100);
        var button = Find<Button>(window, "Editor.Views");
        var flyout = (Flyout)button.Flyout!; flyout.ShowAt(button); Program.Pump(100);
        var difficulty = Find<CheckBox>((Control)flyout.Content!, "Editor.Views.GlobalDifficulty");
        difficulty.IsChecked = false;
        Require(!Find<Border>(window, "Editor.Global.Difficulty").IsVisible, "top display menu failed to hide difficulty");
        difficulty.IsChecked = true;
        Require(Find<Border>(window, "Editor.Global.Difficulty").IsVisible, "top display menu failed to restore difficulty");
        Field<ScrollBar>(window, "trackBoardScrollbar").Value = 0; Program.Pump(50);
        Find<Button>((Control)flyout.Content!, "Editor.Views.Arrange").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.Pump(100);
        Require(Math.Abs(main.TranslatePoint(new Point(), scroller)!.Value.X) < 2, "arrange layout did not restore main track");
        flyout.Hide(); Program.Pump(50);
        var noticeType = typeof(MainWindow).Assembly.GetType("LumaChart.Avalonia.Infrastructure.WindowsWorkspaceRecoveryNoticePolicy")!;
        var notice = noticeType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public)!.Invoke(null,
            [new[] { "E:\\" + new string('x', 260) + ".osu: 工作副本已保留，请另存为新文件。" }])!;
        var noticeText = (string)notice.GetType().GetProperty("Message")!.GetValue(notice)!;
        Require(noticeText.Contains("工作副本已保留，请另存为新文件。"), "long path hides recovery reason");
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var transport = Field<WindowsAudioTransportViewModel>(window, "audioTransport");
        timeline.NavigateToBeat(4); var play = (Task)Invoke(window, "ToggleAudioPlaybackAsync")!;
        Program.PumpUntil(() => play.IsCompleted && transport.IsPlaying);
        // Emulate a stale navigation state at a checkpoint/status refresh.
        timeline.SetCurrentBeat(5);
        typeof(MainWindow).GetMethod("SetProgrammaticPlaybackBeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [5d]);
        var before = timeline.CurrentBeat;
        Invoke(window, "UpdateUi");
        Require(timeline.CurrentBeat == before, "UI refresh rewound active playback to older navigation state");
        var pause = (Task)Invoke(window, "PauseAllEditorPlaybackAsync")!; Program.PumpUntil(() => pause.IsCompleted);
        timeline.NavigateToBeat(4);
        using (var shot = window.CaptureRenderedFrame()!) shot.Save(Path.Combine(screenshots, "startup-layout.png"), PngBitmapEncoderOptions.Default);
        VerifyClock();
        Console.WriteLine("PASS delayed board layout/resize/arrange, visible global-panel recovery, active refresh continuity and quantised audio visual pacing");
    }
    public static void VerifyClock()
    {
        foreach (var rate in new[] { .5, 1d, 2d })
        {
            var clock = new EditorPlaybackVisualClock(); var prior = clock.Sample(0, rate, 0);
            for (var frame = 1; frame <= 600; frame++)
            {
                var time = frame * (1000d / 120);
                var quantised = Math.Floor(time / 20) * 20 * rate;
                var next = clock.Sample(quantised, rate, time);
                if (frame > 4)
                    Require(next - prior >= (1000d / 120) * rate * .974 && next - prior <= (1000d / 120) * rate * 1.026,
                        "quantised device probes still cause stop/go frames");
                Require(Math.Abs(next - time * rate) < 35 * rate, "visual clock drifted from audio");
                prior = next;
            }
            clock.Reset(); Require(clock.Sample(1200, rate, 6000) == 1200, "seek reset was interpolated from old position");
            clock.Reset(); clock.Sample(0, rate, 0); Require(clock.Sample(0, rate, 80) == 0, "startup moves before audio advances");
        }
    }
    private static object? Invoke(object root, string method, params object[] args) => root.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(root, args);
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

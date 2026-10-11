using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using SimpleManiaEditor.PlaytestBridge;

internal static class RequestedWorkflowSpecs
{
    internal static void Run(MainWindow window, string screenshots)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var timeline = Field<TimelineSurface>(window, "timeline");
        var transport = Field<WindowsAudioTransportViewModel>(window, "audioTransport");
        var rate = Field<ComboBox>(window, "playtestRatePicker");
        var editorRate = Field<ComboBox>(window, "fixedRatePicker");
        rate.SelectedItem = .75d; editorRate.SelectedItem = .5d;
        Program.PumpUntil(() => transport.PlaybackRate == .5);
        Require(EditorPresentationSettingsStore.LoadPlaytestRate() == .75 && Field<double>(window, "playtestRate") == .75,
            "independent playtest rate did not persist");
        var previousRunner = OfficialPlaytestLauncher.ProcessRunner; var recorder = new Recorder();
        OfficialPlaytestLauncher.ProcessRunner = recorder;
        try
        {
            Click(Find<Button>(window, "Editor.Toolbar.Playtest"));
            Program.PumpUntil(() => recorder.Request != null);
            Require(recorder.Request!.Rate == .75 && recorder.Request.PreservePitch && transport.PlaybackRate == .5,
                "playtest still uses editor playback rate/pitch");
        }
        finally { OfficialPlaytestLauncher.ProcessRunner = previousRunner; }
        rate.SelectedItem = 1d; editorRate.SelectedItem = 1d; Program.PumpUntil(() => transport.PlaybackRate == 1);
        var slider = Field<Slider>(window, "fixedGridSlider");
        Require(slider.Maximum == 10, "adjustment grid still includes hidden presets");
        Field<ComboBox>(window, "fixedGridPicker").SelectedItem = 5;
        Require(model.ActiveTab!.Session.SnapDivision == 5, "manual quintuplet was overwritten by slider sync");
        slider.Value = 4; Require(model.ActiveTab.Session.SnapDivision == 6, "slider did not skip quintuplets");
        Field<ComboBox>(window, "fixedGridPicker").SelectedItem = 4;
        var appearance = Find<Expander>(window, "Settings.Appearance");
        Require(Find<Slider>(window, "Settings.UiScale").GetLogicalAncestors().Contains(appearance), "display scale is outside appearance");

        var session = model.ActiveTab.Session; var original = session.Document.Clone();
        var tabs = Field<TabControl>(window, "editorInspectorTabs");
        tabs.SelectedItem = Field<TabItem>(window, "timingInspectorTab"); Program.Pump(100);
        session.ReplaceDocument(original.Clone()); Program.Pump(100);
        Require(!Find<Button>(window, "Editor.Command.Undo").IsEnabled && !Find<Button>(window, "Editor.Command.Redo").IsEnabled,
            "empty history buttons remain enabled");
        var count = session.Document.Timing.Count;
        Click(Find<Button>(window, "Timing.Native.Add"));
        Program.PumpUntil(() => window.OwnedWindows.OfType<OsuTimingDialog>().Any());
        var dialog = window.OwnedWindows.OfType<OsuTimingDialog>().Single();
        Require(session.Document.Timing.Count == count && !session.CanUndo, "opening timing dialog already added an event");
        Click(Find<Button>(dialog, "Timing.Dialog.Cancel")); Program.Pump(100);
        Require(session.Document.Timing.Count == count && !session.CanUndo, "cancelled timing draft changed history");
        Click(Find<Button>(window, "Timing.Native.Add"));
        Program.PumpUntil(() => window.OwnedWindows.OfType<OsuTimingDialog>().Any());
        dialog = window.OwnedWindows.OfType<OsuTimingDialog>().Single();
        Find<TextBox>(dialog, "Timing.Dialog.Time").Text = "937.8";
        Find<ComboBox>(dialog, "Timing.Dialog.Type").SelectedIndex = 1;
        Find<TextBox>(dialog, "Timing.Dialog.Value").Text = "1.5";
        Find<CheckBox>(dialog, "Timing.Dialog.Kiai").IsChecked = true;
        using (var shot = dialog.CaptureRenderedFrame()!) shot.Save(Path.Combine(screenshots, "timing-draft.png"), PngBitmapEncoderOptions.Default);
        Click(Find<Button>(dialog, "Timing.Dialog.Apply"));
        Program.PumpUntil(() => session.Document.Timing.Count == count + 1);
        Require(session.CanUndo && Find<Button>(window, "Editor.Command.Undo").IsEnabled && !Find<Button>(window, "Editor.Command.Redo").IsEnabled,
            "history buttons did not follow a committed timing command");
        var green = session.Document.Timing.Single(t => t.Effect == TimingEffectKind.Scroll && t.EffectValue == 1.5);
        Require(OsuTimingPointSettings.Read(green).Kiai, "timing dialog did not apply Kiai");
        Click(Find<Button>(window, "Timing.Native.Edit"));
        Program.PumpUntil(() => window.OwnedWindows.OfType<OsuTimingDialog>().Any());
        dialog = window.OwnedWindows.OfType<OsuTimingDialog>().Single();
        Require(Find<TextBox>(dialog, "Timing.Dialog.Time").Text == "938" && Find<ComboBox>(dialog, "Timing.Dialog.Type").SelectedIndex == 1 &&
            Find<TextBox>(dialog, "Timing.Dialog.Value").Text == "1.5" && Find<CheckBox>(dialog, "Timing.Dialog.Kiai").IsChecked == true,
            "Edit did not load the selected native timing event");
        Click(Find<Button>(dialog, "Timing.Dialog.Cancel")); Program.Pump(50);
        using (var shot = window.CaptureRenderedFrame()!) shot.Save(Path.Combine(screenshots, "timing-edit.png"), PngBitmapEncoderOptions.Default);
        session.Undo(); Program.Pump(50);
        Require(!Find<Button>(window, "Editor.Command.Undo").IsEnabled && Find<Button>(window, "Editor.Command.Redo").IsEnabled,
            "last undo did not disable Undo and enable Redo");
        session.Redo(); Program.Pump(50);
        Require(!Find<Button>(window, "Editor.Command.Redo").IsEnabled, "last redo did not disable Redo");
        session.ReplaceDocument(original.Clone()); Program.Pump(100);
        var offset = Field<TextBox>(window, "metadataOffsetBox");
        offset.Text = "813.333333333319";
        typeof(MainWindow).GetMethod("ApplyTimingMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require(session.Document.GetOrCreateSongOffsetTiming().Delay == -813 && offset.Text == "813", "osu offset did not apply its timestamp/normalize to 1 ms");
        session.ReplaceDocument(original); Program.Pump(100);
        tabs.SelectedItem = Field<TabItem>(window, "settingsInspectorTab"); Program.Pump(100);
        RunPointer(screenshots);
        Console.WriteLine("PASS independent persisted playtest rate, filtered grids, collapsed display settings, native Timing draft/cancel/apply/Kiai and history limits");
    }
    private static void RunPointer(string screenshots)
    {
        const string fixture = "osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n1000,500,4,2,1,70,1,0\n[HitObjects]\n36,192,1500,1,0,0:0:0:0:\n";
        var session = new EditorSession { SnapDivision = 4 }; session.ReplaceDocument(OsuManiaSerializer.Parse(fixture));
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Tap, SnapAllNotes = true };
        var window = new Window { Width = 1000, Height = 800, Content = surface };
        window.Show(); Dispatcher.UIThread.RunJobs(); surface.SetCurrentBeat(0);
        var probe = surface.GetPlacementEvidenceProbe()!;
        Point At(double beat) => new(probe.TapTarget.SurfacePoint.X,
            surface.Bounds.Height * surface.JudgementLinePosition - (beat - surface.CurrentBeat) * surface.PixelsPerBeat - .01);
        window.MouseDown(At(-.25), MouseButton.Left); window.MouseUp(At(-.25), MouseButton.Left);
        Require(session.Document.Notes.Any(n => n.Beat.Value == -.25), "pointer could not place a note before Offset");
        surface.NavigateToBeat(-.5); Require(surface.CurrentBeat == -.5, "negative beat navigation was clamped at Offset");
        var saved = Path.Combine(screenshots, "before-offset.osu"); OsuManiaSerializer.Save(session.Document, saved);
        Require(File.ReadAllText(saved).Contains(",192,875,"), "early note saved at the wrong actual time");
        var doc = OsuManiaSerializer.Parse(fixture); doc.Notes.Clear();
        var first = new ChartNote { Lane = probe.TapTarget.Lane, Beat = BeatPosition.Preserve(0, 100001, 999983) };
        var second = new ChartNote { Lane = (first.Lane + 1) % 7, Beat = BeatPosition.Preserve(1, 100001, 999979), EndBeat = BeatPosition.Preserve(2, 100001, 999979) };
        var firstBeat = first.Beat;
        doc.Notes.AddRange([first, second]); session.ReplaceDocument(doc); session.Selection.UnionWith(doc.Notes.Select(n => n.Id));
        surface.SetCurrentBeat(0); surface.Tool = TimelineTool.Select;
        var serial = session.ChangeSerial; session.SnapDivision = 16; session.SnapDivision = 4;
        Require(session.ChangeSerial == serial && session.Document.Notes[0].Beat == firstBeat, "changing subdivision rewrote existing notes");
        window.MouseDown(At(firstBeat.Value), MouseButton.Left);
        window.MouseMove(At(.76), RawInputModifiers.LeftMouseButton); window.MouseUp(At(.76), MouseButton.Left);
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.All(n => OnGrid(n.Beat) && (!n.EndBeat.HasValue || OnGrid(n.EndBeat.Value))),
            "Ctrl+A batch drag did not snap all heads/tails or rejected imported denominators");
        Require(session.Document.Notes.Single(n => n.Id == first.Id).Beat.Value == .75, "first dragged note did not land on nearest grid");
        Require(session.Undo() && session.Document.Notes.Single(n => n.Id == first.Id).Beat == firstBeat && !session.CanUndo,
            "batch offset drag was not one undo operation");
        surface.SnapAllNotes = false; session.Selection.UnionWith(session.Document.Notes.Select(n => n.Id));
        window.MouseDown(At(firstBeat.Value), MouseButton.Left);
        window.MouseMove(At(firstBeat.Value + .5), RawInputModifiers.LeftMouseButton); window.MouseUp(At(firstBeat.Value + .5), MouseButton.Left);
        Require(Math.Abs(session.Document.Notes.Single(n => n.Id == first.Id).Beat.Value - firstBeat.Value - .5) < .000001,
            "disabled all-snap mode failed to preserve an off-grid relative move");
        window.Close();
        Console.WriteLine("PASS native before-offset pointer placement/save, whole-selection absolute snapping, grid-change preservation and bounded relative drag");
    }
    private static bool OnGrid(BeatPosition value) => Math.Abs(value.Value * 4 - Math.Round(value.Value * 4)) < 1e-8;
    private sealed class Recorder : IOfficialPlaytestProcess
    {
        internal PlaytestRequest? Request;
        public Task RunAsync(string path) { Request = PlaytestRequest.Read(path); return Task.CompletedTask; }
    }
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(root)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

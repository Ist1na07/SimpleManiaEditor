using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using NAudio.Wave;

internal static class EditorInteraction025Specs
{
    public static void RunPointer(string screenshots)
    {
        var session = new EditorSession { SnapDivision = 4 };
        session.ReplaceDocument(OsuManiaSerializer.Parse("osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n475,500,4,2,1,0,1,0\n[HitObjects]\n"));
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Hold };
        var window = new Window { Width = 900, Height = 800, Content = surface };
        window.Show(); Program.Pump(30); surface.SetCurrentBeat(4); surface.SetZoom(140);
        var target = surface.GetPlacementEvidenceProbe()!.TapTarget;
        var laneWidth = (double)typeof(TimelineSurface).GetField("gridLaneWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        var x = target.SurfacePoint.X + (5 - target.Lane) * laneWidth;
        var point = new Point(x, surface.Bounds.Height * surface.JudgementLinePosition - .14 * 140);
        ChartNote[] payload = [new() { Lane = 2, Beat = new(10, 0, 1) }, new() { Lane = 3, Beat = new(10, 1, 4), EndBeat = new(10, 3, 4) }];
        surface.BeginPendingPaste(payload); window.MouseMove(point); Program.Pump(30);
        Require(session.Document.Notes.Count == 0 && surface.HasPendingPaste, "paste preview modified chart");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "paste-preview-025.png"), PngBitmapEncoderOptions.Default);
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Require(!surface.HasPendingPaste && session.Document.Notes.Count == 2 && session.Document.Notes.Min(n => n.Lane) == 5 &&
            Math.Abs(session.Document.Notes.Min(n => n.Beat.Value) - 4.25) < .000002, "click paste lost snapped time or horizontal position");
        Require(session.Undo() && session.Document.Notes.Count == 0 && session.Redo() && session.Document.Notes.Count == 2, "paste was not atomic undo/redo");
        surface.BeginPendingPaste(payload); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
        Require(!surface.HasPendingPaste && session.Document.Notes.Count == 2, "right-cancel deleted underlying notes");
        surface.SetCurrentBeat(0); window.MouseWheel(point, new Vector(0, 1));
        Require(surface.CurrentBeat < 0, "native early scrolling was clamped");
        var before = surface.PixelsPerBeat; window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Alt);
        Require(surface.PixelsPerBeat > before, "Alt-wheel zoom was swallowed");
        window.Close(); Program.Pump(20);
        var calibration = new AudioCalibrationWindow(12); calibration.Show(); Program.Pump(30);
        Require(calibration.CalibrationMilliseconds == 12, "calibration initial value changed");
        using (var frame = calibration.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "calibrator-025.png"), PngBitmapEncoderOptions.Default);
        PlatformAudioOutputs.Current = new SimulatedAudioOutputFactory();
        Wait((Task)Invoke(calibration, "ToggleAsync")!);
        var calibrationAudio = Field<WindowsAudioPlaybackService>(calibration, "audio");
        Require(calibrationAudio.IsPlaying, "calibration click track did not play");
        var use = calibration.GetLogicalDescendants().OfType<Button>().Distinct().Single(b => b.Content?.ToString() == "使用此校准值");
        use.Focus(); calibration.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " "); calibration.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Program.PumpUntil(() => !calibrationAudio.IsPlaying);
        Require(calibration.IsVisible, "Space activated calibration Apply instead of playback");
        var fixture = Field<string>(calibration, "path");
        calibration.Close(); Wait(calibration.Shutdown); Program.Pump(20);
        Require(!File.Exists(fixture), "calibrator did not remove its private stimulus");
        var wav = Path.Combine(Path.GetDirectoryName(screenshots)!, "calibration-click.wav");
        AudioCalibrationWindow.WriteClickTrack(wav);
        using (var reader = new WaveFileReader(wav))
        {
            reader.Position = (long)(1.2 * reader.WaveFormat.AverageBytesPerSecond);
            var buffer = new byte[960]; reader.Read(buffer, 0, buffer.Length);
            Require(reader.WaveFormat.SampleRate == 48000 && reader.TotalTime.TotalSeconds == 120 &&
                Enumerable.Range(0, 480).Any(i => BitConverter.ToInt16(buffer, i * 2) != 0), "calibration stimulus is not an exact 1200ms click");
        }
        File.Delete(wav);
        Console.WriteLine("PASS pointer-following ghost/click/cancel/undo, native early scroll, Alt-wheel and exact calibration stimulus");
    }

    public static void RunWorkbench(MainWindow window, string screenshots)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel"); var original = model.ActiveTab!;
        Wait(model.CreateAsync("Paste025")); Program.Pump(50);
        var session = model.ActiveTab!.Session; var main = Field<TimelineSurface>(window, "timeline");
        var note = new ChartNote { Lane = 2, Beat = new(5, 1, 4), EndBeat = new(5, 3, 4) };
        session.Mutate(d => { d.Timing[0].Delay = -475; d.Notes.Add(note); });
        session.Selection.Clear(); session.Selection.Add(note.Id); Invoke(window, "CopyMixedSelection");
        Wait(Field<Task>(window, "clipboardWriteTask"));
        Wait((Task)Invoke(window, "AddEditorTrackAsync", EditorTrackKind.Draft, null)!); Program.Pump(60);
        var views = Field<IDictionary>(window, "secondaryTrackViews"); var view = views.Values.Cast<object>().Single();
        var track = (EditorTrack)view.GetType().GetProperty("Track")!.GetValue(view)!;
        var draft = (TimelineSurface)view.GetType().GetProperty("Surface")!.GetValue(view)!;
        Require(OsuSourceEnvelope.HasSource(track.Session!.Document), "draft did not inherit native early-time semantics");
        Invoke(window, "RestoreBoardPanAfterLayout", 700d); Program.Pump(60);
        Wait((Task)Invoke(window, "PasteNoteClipboardAsync")!);
        Program.Pump(60);
        Require(main.HasPendingPaste && track.Session.Document.Notes.Count == 0, "main paste was not pending");
        var target = draft.GetPlacementEvidenceProbe()!.TapTarget;
        var origin = draft.TranslatePoint(new Point(), window)!.Value;
        var point = new Point(target.SurfacePoint.X, Math.Min(160, draft.Bounds.Height * .3)) + (Vector)origin;
        window.MouseMove(point); Program.Pump(30);
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "paste-draft-debug-025.png"), PngBitmapEncoderOptions.Default);
        Require(draft.HasPendingPaste && !main.HasPendingPaste, $"ghost did not transfer from main into draft: point={point}, origin={origin}, bounds={draft.Bounds}, canvas={Field<Grid>(window, "trackBoardCanvas").Bounds}, routes={InputElement.PointerMovedEvent.RoutingStrategies}");
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Program.Pump(40);
        Require(track.Session.Document.Notes.Count == 1 && session.Document.Notes.Count == 1, "main-to-draft clipboard placement failed");
        var beforeZoom = main.PixelsPerBeat;
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Alt); Program.Pump(30);
        Require(main.PixelsPerBeat > beforeZoom && draft.PixelsPerBeat == main.PixelsPerBeat, "draft modifier wheel did not update synced zoom");
        main.SetCurrentBeat(20);
        var sourceMap = new TimingMap(track.Session.Document.Timing);
        var expected = sourceMap.BeatToMilliseconds(track.Session.Document.Notes.Single().Beat);
        Invoke(window, "TransferTrackSelectionToMain", view); Program.Pump(30);
        var targetMap = new TimingMap(session.Document.Timing);
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.Any(n => Math.Abs(targetMap.BeatToMilliseconds(n.Beat) - expected) < .001), "transfer incorrectly used the playback position");
        Invoke(window, "TransferTrackSelectionToMain", view);
        Require(session.Document.Notes.Count == 2, "aligned transfer duplicated identical notes");
        Invoke(window, "ClearDraftTrack", view);
        Require(track.Session.Document.Notes.Count == 0 && track.Session.Undo() && track.Session.Document.Notes.Count == 1, "draft clear was not undoable");
        track.SyncScroll = false; draft.NavigateToBeat(-1); Require(draft.CurrentBeat == -1, "draft could not reach before offset");
        var noteTabs = Field<TabControl>(window, "editorInspectorTabs"); noteTabs.SelectedItem = Field<TabItem>(window, "noteInspectorTab");
        var code = Find<TextBox>(window, "Inspector.Note.Code");
        Require(!code.GetLogicalAncestors().OfType<Expander>().Any(), "note code is still collapsed");
        code.Text = "36,192,1234,1,0,0:0:0:0:";
        Find<Button>(window, "Inspector.Note.ImportCode").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(session.Document.Notes.Any(n => Math.Abs(targetMap.BeatToMilliseconds(n.Beat) - 1234) < .001), "code Apply did not import its original time");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "note-code-025.png"), PngBitmapEncoderOptions.Default);
        Require(Find<Button>(window, "Settings.AudioVisualOffset.Calibrator") != null, "calibrator entry missing");
        model.Activate(original.Id); Program.Pump(40);
        Console.WriteLine("PASS main-to-draft ghost placement, aligned transfer, undoable clear, synced modifier wheel and direct code Apply");
    }
    public static void CheckPlayback(MainWindow window, Point pointer)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var surface = Field<TimelineSurface>(window, "timeline");
        var transport = Field<WindowsAudioTransportViewModel>(window, "audioTransport");
        var ratePicker = Field<ComboBox>(window, "fixedRatePicker");
        var calibration = Find<Slider>(window, "Settings.AudioVisualOffset");
        foreach (var (rate, offset) in new[] { (1d, 200), (1d, -200), (.1, 200), (.1, -200) })
        {
            ratePicker.SelectedItem = rate;
            Program.PumpUntil(() => transport.PlaybackSnapshot?.PlaybackRate == rate && Field<int>(window, "fixedRateChangesInFlight") == 0);
            calibration.Value = offset;
            surface.NavigateToBeat(6);
            try { Program.PumpUntil(() => !Field<bool>(window, "editorSeekPending")); }
            catch (TimeoutException ex) { throw new InvalidOperationException($"seek timeout at {rate}x / {offset}ms: running={Field<bool>(window, "editorSeekCommitRunning")}, scheduled={Field<bool>(window, "editorSeekCommitScheduled")}, task={Field<Task>(window, "editorSeekCommitTask").Status}, version={Field<long>(window, "editorSeekTargetVersion")}, pos={transport.PlaybackSnapshot}, error={transport.LastError}", ex); }
            Wait((Task)Invoke(window, "ToggleAudioPlaybackAsync")!);
            Program.PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == true);
            Program.Pump(150);
            var map = new TimingMap(model.ActiveTab!.Session.Document.Timing);
            double navigationStart = double.NaN, target = double.NaN;
            EventHandler<TimelineManualNavigationEventArgs> began = (_, e) =>
            { if (e.ChangesBeat) navigationStart = map.BeatToMilliseconds(BeatPosition.FromDouble(surface.CurrentBeat, 1000000)); };
            EventHandler<double> changed = (_, beat) =>
            { if (!Field<bool>(window, "settingProgrammaticPlaybackBeat")) target = map.BeatToMilliseconds(BeatPosition.FromDouble(beat, 1000000)); };
            surface.ManualNavigationRequested += began; surface.CurrentBeatChanged += changed;
            window.MouseWheel(pointer, new Vector(0, rate == 1 ? .2 : .02));
            surface.ManualNavigationRequested -= began; surface.CurrentBeatChanged -= changed;
            var queued = Field<double>(window, "pendingEditorSeekMilliseconds");
            Require(double.IsFinite(target) && Math.Abs(queued - (target - offset * rate)) < .002,
                $"calibrated wheel used display time: queued={queued}, target={target}, from={navigationStart}, rate={rate}");
            Require(Math.Abs(target - navigationStart + (rate == 1 ? 100 : 10)) < .002,
                $"small wheel was not applied: from={navigationStart}, target={target}, rate={rate}");
            Program.PumpUntil(() => !Field<bool>(window, "editorSeekPending"));
            Program.Pump(160);
            var expected = transport.PlaybackSnapshot!.Position.TotalMilliseconds + offset * rate;
            var visual = map.BeatToMilliseconds(BeatPosition.FromDouble(surface.CurrentBeat, 1000000));
            Require(Math.Abs(visual - expected) < 35 * rate + 5, $"seek reused old visual clock: {visual} vs {expected} at {rate}x");
            var start = visual; Program.Pump(150);
            visual = map.BeatToMilliseconds(BeatPosition.FromDouble(surface.CurrentBeat, 1000000));
            Require(visual - start > 100 * rate, $"{rate}x visual playback stalled while audio was advancing");
            Wait((Task)Invoke(window, "ToggleAudioPlaybackAsync")!);
            Program.PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == false);
            Wait((Task)Invoke(window, "ToggleAudioPlaybackAsync")!); Program.Pump(200);
            expected = transport.PlaybackSnapshot!.Position.TotalMilliseconds + offset * rate;
            visual = map.BeatToMilliseconds(BeatPosition.FromDouble(surface.CurrentBeat, 1000000));
            Require(Math.Abs(visual - expected) < 35 * rate + 5, $"{rate}x pause/resume left the display frozen");
            Wait((Task)Invoke(window, "ToggleAudioPlaybackAsync")!);
            Program.PumpUntil(() => transport.PlaybackSnapshot?.IsPlaying == false);
        }
        calibration.Value = 0; ratePicker.SelectedItem = 1d;
        Program.PumpUntil(() => transport.PlaybackSnapshot?.PlaybackRate == 1);
        Wait((Task)Invoke(window, "ToggleAudioPlaybackAsync")!);
        Find<Button>(window, "Settings.AudioVisualOffset.Calibrator").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.PumpUntil(() => window.OwnedWindows.OfType<AudioCalibrationWindow>().Any());
        Require(transport.PlaybackSnapshot?.IsPlaying == false, "calibrator did not pause editor music");
        var modal = window.OwnedWindows.OfType<AudioCalibrationWindow>().Single(); Field<Slider>(modal, "slider").Value = 123;
        modal.Close((int?)null); Wait(modal.Shutdown);
        Program.PumpUntil(() => Field<SemaphoreSlim>(window, "editorPlaybackCommandGate").CurrentCount == 1);
        Require(calibration.Value == 0 && transport.PlaybackSnapshot?.IsPlaying == false, "cancel changed calibration or resumed editor playback");
        Find<Button>(window, "Settings.AudioVisualOffset.Calibrator").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.PumpUntil(() => window.OwnedWindows.OfType<AudioCalibrationWindow>().Any());
        modal = window.OwnedWindows.OfType<AudioCalibrationWindow>().Single(); Field<Slider>(modal, "slider").Value = 17;
        modal.Close((int?)17); Wait(modal.Shutdown);
        Program.PumpUntil(() => calibration.Value == 17 && Field<SemaphoreSlim>(window, "editorPlaybackCommandGate").CurrentCount == 1);
        Require(EditorPresentationSettingsStore.LoadAudioVisualOffset() == 17, "confirmed calibration was not persisted"); calibration.Value = 0;
        Console.WriteLine("PASS calibrated small wheel seeks and 0.1x visual continuity/pause/resume through real decoder/DSP with simulated output");
    }
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static object? Invoke(object root, string method, params object?[] args) => root.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(root, args);
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}


using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Editing;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using LumaChart.Core.Validation;
using NAudio.Wave;

internal static class Regression030Specs
{
    public static void RunAudio(string root)
    {
        var native = new WaveBuffer(64 * sizeof(float)).FloatBuffer;
        for (var i = 0; i < 64; i++) native[i] = .75f;
        var partial = new PartialProvider();
        var mixer = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(partial, () => partial.Ended);
        Require(mixer.Read(native, 8, 40) == 40, "short music read stopped output");
        for (var i = 0; i < 64; i++) Require(native[i] == (i is >= 8 and < 12 ? .25f : i is >= 12 and < 48 ? 0 : .75f), "native float-view tail clearing damaged PCM or leaked stale samples");
        Require(mixer.Read(native, 8, 40) == 40, "terminal silent period missing");
        for (var i = 8; i < 48; i++) Require(native[i] == 0, "terminal output repeats old native buffer");
        var epoch = new AudioPlaybackEpoch(); var token = epoch.Capture(); epoch.Advance();
        var guarded = new EpochGuardSampleProvider(new PartialProvider(), token);
        for (var i = 8; i < 48; i++) native[i] = .75f;
        Require(guarded.Read(native, 8, 40) == 0, "superseded overlay played");
        for (var i = 8; i < 48; i++) Require(native[i] == 0, "superseded overlay leaked native PCM");

        var previous = PlatformAudioOutputs.Current;
        try
        {
            foreach (var sampleRate in new[] { 44100, 48000 })
            {
                var file = Path.Combine(root, $"rate030-{sampleRate}.wav");
                using (var writer = new WaveFileWriter(file, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)))
                {
                    var samples = new float[sampleRate * 2];
                    for (var second = 0; second < 16; second++)
                    {
                        for (var frame = 0; frame < sampleRate; frame++)
                        { samples[frame * 2] = (float)(.2 * Math.Sin(2 * Math.PI * 431 * frame / sampleRate)); samples[frame * 2 + 1] = (float)(.2 * Math.Sin(2 * Math.PI * 587 * frame / sampleRate)); }
                        writer.WriteSamples(samples, 0, samples.Length);
                    }
                }
                using (var probeSource = AudioDecoder.OpenWaveStream(file))
                using (var probe = PreparedMusicPipeline.Build(probeSource, TimeSpan.FromSeconds(2), .1, true, 1, () => { }))
                {
                    var samples = new float[sampleRate / 10];
                    var read = probe.Volume.Read(samples, 0, samples.Length);
                    var first = -1;
                    for (var i = 0; i < read; i++) if (Math.Abs(samples[i]) > .003) { first = i; break; }
                    Require(first >= 0 && first / 2d / sampleRate * 1000 <= 20,
                        $"active .1x preparation introduces a silent prefix: {first / 2d / sampleRate * 1000:F3} ms");
                }
                var outputs = new SimulatedAudioOutputFactory(); PlatformAudioOutputs.Current = outputs;
                var opens = 0; var slowOpen = true;
                using var engine = new WindowsAudioDeviceEngine(openDecoder: path =>
                { if (Interlocked.Increment(ref opens) > 1 && slowOpen) Thread.Sleep(120); return AudioDecoder.OpenWaveStream(path); });
                using var service = new WindowsAudioPlaybackService(() => engine);
                service.LoadAsync(file).GetAwaiter().GetResult(); service.SetPreservePitchAsync(true).GetAwaiter().GetResult();
                service.PlayAsync().GetAwaiter().GetResult(); Thread.Sleep(80);
                var deviceCount = outputs.Outputs.Count;
                var visual = new EditorPlaybackVisualClock(); var timer = Stopwatch.StartNew();
                foreach (var rate in new[] { .1, 1d, .25, .5, 2d, .1 })
                {
                    var oldRate = service.Snapshot.PlaybackRate;
                    var baseline = service.Position.TotalMilliseconds;
                    var operation = service.SetPlaybackRateAsync(rate);
                    var last = baseline; var lastVisual = double.MinValue; var resets = 0; var maxError = 0d;
                    var start = timer.Elapsed.TotalMilliseconds;
                    var completedAt = -1d;
                    while (completedAt < 0 || timer.Elapsed.TotalMilliseconds - completedAt < 160)
                    {
                        if (operation.IsCompleted && completedAt < 0) completedAt = timer.Elapsed.TotalMilliseconds;
                        Require(timer.Elapsed.TotalMilliseconds - start < 5000, "rate preparation did not finish");
                        var snapshot = service.Snapshot;
                        var position = snapshot.Position.TotalMilliseconds;
                        Require(position + .05 >= last, "playing rate change rewound the audible clock");
                        if (!operation.IsCompleted && opens > 1 && timer.Elapsed.TotalMilliseconds - start < 90)
                            Require(snapshot.PlaybackRate == oldRate, "requested rate reached clock before PCM was prepared");
                        var presented = visual.Sample(position, snapshot.PlaybackRate, timer.Elapsed.TotalMilliseconds);
                        Require(presented + .1 >= lastVisual, "rate transition produced a backward visual frame");
                        maxError = Math.Max(maxError, Math.Abs(presented - position));
                        if (Math.Abs(presented - position) > 8) resets++;
                        last = position; lastVisual = presented; Thread.Sleep(2);
                    }
                    operation.GetAwaiter().GetResult();
                    Require(service.IsPlaying && engine.PlaybackRate == rate && service.Snapshot.PlaybackRate == rate && resets == 0,
                        $"{sampleRate} {oldRate}->{rate}: clock stalled/drifted after rate commit: playing={service.IsPlaying}, engine={engine.PlaybackRate}, clock={service.Snapshot.PlaybackRate}, driftSamples={resets}, maxDrift={maxError:F3}");
                    var before = service.Position; Thread.Sleep(160); var advance = (service.Position - before).TotalMilliseconds;
                    Require(advance > 120 * rate && advance < 210 * rate, "committed rate clock did not advance continuously");
                }
                Require(outputs.Outputs.Count == deviceCount && outputs.Outputs.All(o => o.PauseCalls == 0 && o.ReadFailure == null) &&
                    engine.Diagnostics.UnexpectedPlaybackStopCount == 0 && engine.Diagnostics.TransientMusicZeroCount == 0, "rate switch paused/rebuilt/starved output");
                slowOpen = false;
                var rapid = Task.WhenAll(new[] { service.SetPlaybackRateAsync(1), service.SetPlaybackRateAsync(.1), service.SetPlaybackRateAsync(.5), service.SetPlaybackRateAsync(1) });
                var lastRapidPosition = service.Position.TotalMilliseconds;
                while (!rapid.IsCompleted)
                {
                    var position = service.Position.TotalMilliseconds;
                    Require(position + .05 >= lastRapidPosition, "rapid queued rate changes rewound clock");
                    lastRapidPosition = position; Thread.Sleep(2);
                }
                rapid.GetAwaiter().GetResult(); Thread.Sleep(100);
                Require(service.Snapshot.PlaybackRate == 1 && engine.PlaybackRate == 1 && outputs.Outputs.Count == deviceCount,
                    "rapid rate changes left stale tempo/queue or rebuilt output");
                service.PauseAsync().GetAwaiter().GetResult(); service.SetPlaybackRateAsync(.25).GetAwaiter().GetResult();
                Require(!service.IsPlaying, "paused rate change started playback"); service.PlayAsync().GetAwaiter().GetResult(); Thread.Sleep(80);
                Require(service.IsPlaying && service.Snapshot.PlaybackRate == .25, "pause/rate/resume kept old queue clock");
                Console.WriteLine($"PASS {sampleRate} stereo live all-rate transitions, 120ms slow decoder, no rewind/stall, one continuous output");
            }
        }
        finally { PlatformAudioOutputs.Current = previous; }
        Console.WriteLine("PASS native byte-backed PCM partial/EOF/epoch clearing");
    }

    public static void RunEditor(MainWindow window)
    {
        var symmetry = new CenterSymmetrySettings(new(0, 0, 1), new(16, 0, 1), Enumerable.Range(0, 7).ToArray());
        var previewSession = new EditorSession(); previewSession.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var preview = new TimelineSurface { Session = previewSession, Tool = TimelineTool.Hold };
        var previewWindow = new Window { Width = 1000, Height = 800, Content = preview }; previewWindow.Show(); Program.Pump(30);
        preview.SetCurrentBeat(4); preview.SymmetrySettings = symmetry;
        var target = preview.GetPlacementEvidenceProbe()!.TapTarget;
        var originalId = WindowsProEditorCommand.PlaceTap(previewSession, target.Lane, new(4, 0, 1), new(0, 6), BasicBeatPlacementPolicy.PreserveAuthoredBeat, symmetry: symmetry)!.Value;
        var originalPairId = previewSession.Document.Notes.Single(n => n.Id != originalId).Id;
        Point At(double beat) => new(target.SurfacePoint.X, preview.Bounds.Height * preview.JudgementLinePosition - (beat - 4) * preview.PixelsPerBeat - 1);
        previewWindow.MouseDown(At(4), MouseButton.Left); previewWindow.MouseMove(At(5), RawInputModifiers.LeftMouseButton);
        Require(Field<Guid?>(preview, "placementReplacedCompanionId") == originalPairId && previewSession.Document.Notes.All(n => !n.IsHold),
            "LN preview did not hide the old mirrored tap, or committed before release");
        previewWindow.MouseUp(At(5), MouseButton.Left);
        Require(previewSession.Document.Notes.Count == 2 && previewSession.Document.Notes.All(n => n.IsHold) &&
            Field<Guid?>(preview, "placementReplacedCompanionId") == null, "pointer pair replacement left overlap/stale preview");
        previewWindow.Close(); Program.Pump(20);
        foreach (var reverse in new[] { false, true })
        {
            var session = new EditorSession(); session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
            var id = WindowsProEditorCommand.PlaceTap(session, 1, new(4, 0, 1), new(0, 6), BasicBeatPlacementPolicy.PreserveAuthoredBeat, symmetry: symmetry)!.Value;
            var taps = session.Document.Notes.Select(n => n.Id).ToHashSet();
            WindowsProEditorCommand.PlaceHold(session, 1, new(4, 0, 1), new(reverse ? 2 : 6, 0, 1), new(0, 6), BasicBeatPlacementPolicy.PreserveAuthoredBeat,
                replacedNoteId: id, symmetry: symmetry);
            Require(session.Document.Notes.Count == 2 && session.Document.Notes.All(n => n.IsHold) &&
                !ChartValidator.Validate(session.Document).Any(i => i.NoteIds.Count > 1), "tap-to-LN symmetry left old head/tail tap overlapping");
            Require(session.Undo() && session.Document.Notes.Select(n => n.Id).ToHashSet().SetEquals(taps) && session.Redo() && session.Document.Notes.All(n => n.IsHold), "paired replacement history is not atomic");
        }
        var model = Field<MainWindowViewModel>(window, "viewModel"); var original = model.ActiveTab;
        Wait(model.CreateAsync("Regression030"));
        var main = Field<TimelineSurface>(window, "timeline");
        model.ActiveTab!.Session.ReplaceDocument(OsuManiaSerializer.Parse("osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n475,500,4,2,1,0,1,0\n[HitObjects]\n"));
        Invoke(window, "ShowEditorSurface", true); Wait((Task)Invoke(window, "AddEditorTrackAsync", EditorTrackKind.Draft, null)!);
        var view = ((IDictionary)Field<object>(window, "secondaryTrackViews")).Values.Cast<object>().Last();
        var track = (EditorTrack)view.GetType().GetProperty("Track")!.GetValue(view)!;
        var draft = (TimelineSurface)view.GetType().GetProperty("Surface")!.GetValue(view)!;
        Field<WindowsKeySoundPlaybackRuntime>(window, "keySoundRuntime").StopSilent();
        main.SetPlaybackActive(false); draft.SetPlaybackActive(false);
        Program.Pump(80);
        var scroller = Field<ScrollViewer>(window, "trackScroller");
        var scrollbar = Field<ScrollBar>(window, "trackBoardScrollbar");
        var draftCenter = draft.TranslatePoint(new Point(draft.Bounds.Width / 2, 0), scroller)!.Value.X;
        scrollbar.Value += draftCenter - scroller.Viewport.Width / 2;
        Program.Pump(80);
        var document = track.Session!.Document.Clone(); document.Timing[0].Bpm = 137; track.Session.ReplaceDocument(document);
        foreach (var division in new[] { 4, 12, 24 })
        {
            Invoke(window, "SetQuickSnapDivision", division, true);
            Require(track.Session.SnapDivision == division, "grid change did not immediately reach draft editing");
            foreach (var beat in new[] { 8.031, -1.031 })
            foreach (var direction in new[] { 1d, -1d })
            {
                main.NavigateToBeat(beat); Program.Pump(12);
                var point = draft.TranslatePoint(new Point(draft.Bounds.Width / 2, draft.Bounds.Height / 2), window)!.Value;
                window.MouseWheel(point, new Vector(0, direction)); Program.Pump(25);
                Require(Math.Abs(main.CurrentBeat * division - Math.Round(main.CurrentBeat * division)) < .00001,
                    $"draft wheel did not follow the main grid: div={division}, beat={main.CurrentBeat}, pointer={point}, playing={main.IsPlaybackActive}");
                var mainMap = new TimingMap(model.ActiveTab.Session.Document.Timing); var draftMap = new TimingMap(track.Session.Document.Timing);
                Require(Math.Abs(mainMap.BeatToMilliseconds(BeatPosition.FromDouble(main.CurrentBeat, 1_000_000)) -
                    draftMap.BeatToMilliseconds(BeatPosition.FromDouble(draft.CurrentBeat, 1_000_000))) < .002, "draft/main playheads are not time aligned");
            }
        }
        track.SyncScroll = false; Invoke(window, "SyncTrackViewports"); main.NavigateToBeat(8); draft.NavigateToBeat(3);
        var independentPoint = draft.TranslatePoint(new Point(draft.Bounds.Width / 2, draft.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(independentPoint, new Vector(0, 1)); Program.Pump(20);
        Require(main.CurrentBeat == 8 && draft.CurrentBeat != 3, "independent draft wheel moved the main grid");
        track.SyncScroll = true; Invoke(window, "SyncTrackViewports");
        if (original != null) model.Activate(original.Id);
        Console.WriteLine("PASS forward/reverse paired tap replacement, atomic history, differing draft BPM, early/grid-change wheel alignment and independent scrolling");
    }

    private sealed class PartialProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public bool Ended { get; private set; }
        public int Read(float[] buffer, int offset, int count)
        { if (Ended) return 0; Ended = true; for (var i = 0; i < 4; i++) buffer[offset + i] = .25f; return 4; }
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static object? Invoke(object value, string name, params object?[] args) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, args);
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

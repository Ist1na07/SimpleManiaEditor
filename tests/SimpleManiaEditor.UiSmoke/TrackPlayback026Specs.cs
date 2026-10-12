using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

internal static class TrackPlayback026Specs
{
    public static void Run(MainWindow window)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var original = model.ActiveTab!;
        Wait(model.CreateAsync("TrackPlayback026")); Program.Pump(50);
        Add(window, EditorTrackKind.Draft); Add(window, EditorTrackKind.Draft); Add(window, EditorTrackKind.Density);
        Program.Pump(60);
        var views = Field<IDictionary>(window, "secondaryTrackViews").Values.Cast<object>().ToArray();
        var draftViews = views.Where(v => Property<EditorTrack>(v, "Track").Kind == EditorTrackKind.Draft).ToArray();
        var synced = Property<TimelineSurface>(draftViews[0], "Surface");
        var independent = Property<TimelineSurface>(draftViews[1], "Surface");
        Property<EditorTrack>(draftViews[1], "Track").SyncScroll = false;
        independent.SetCurrentBeat(17); independent.SetPlaybackActive(true);
        synced.Session!.Mutate(d => { d.Timing[0].Bpm = 150; d.Timing[0].Delay = -321; });
        var main = Field<TimelineSurface>(window, "timeline");
        var wave = Field<AudioVisualizationSideSurface>(window, "waveformSideSurface");
        var spectrum = Field<AudioVisualizationSideSurface>(window, "spectrogramSideSurface");
        var oldWaveVisible = wave.IsVisible; var oldSpectrumVisible = spectrum.IsVisible;
        wave.IsVisible = true; spectrum.IsVisible = true;
        Invoke(window, "SyncTrackViewports"); Program.Pump(40);
        Require(!independent.IsPlaybackActive, "disabling synchronized scrolling retained playback wheel behavior");
        var metricView = views.Single(v => Property<EditorTrack>(v, "Track").Kind == EditorTrackKind.Density);
        var metric = Property<EditorMetricTrackSurface>(metricView, "Metric");
        var sourceMap = new TimingMap(synced.Session.Document.Timing);
        var mainMap = new TimingMap(model.ActiveTab!.Session.Document.Timing);
        var mainAnchor = main.TranslatePoint(new Point(0, main.Bounds.Height * main.JudgementLinePosition), window)!.Value.Y;
        var draftAnchor = synced.TranslatePoint(new Point(0, synced.Bounds.Height * synced.JudgementLinePosition), window)!.Value.Y;
        Require(Math.Abs(mainAnchor - draftAnchor) < 1, "full synchronization did not align different-height track anchors");
        var metricAnchor = Field<double?>(metric, "viewportAnchor");
        var initialLayouts = Field<long>(window, "trackViewportLayoutUpdates");
        var initialFrames = Field<long>(window, "trackViewportPlaybackUpdates");
        main.SetPlaybackActive(true);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 500; index++) Invoke(window, "SetProgrammaticPlaybackBeat", 5 + index / 250d);
        watch.Stop();
        Require(Field<long>(window, "trackViewportPlaybackUpdates") - initialFrames == 500 &&
            Field<long>(window, "trackViewportLayoutUpdates") == initialLayouts,
            "playback frames repeated layout/settings synchronization");
        var sourceMilliseconds = sourceMap.BeatToMilliseconds(BeatPosition.FromDouble(synced.CurrentBeat, 1_000_000));
        var targetMilliseconds = mainMap.BeatToMilliseconds(BeatPosition.FromDouble(main.CurrentBeat, 1_000_000));
        Require(Math.Abs(sourceMilliseconds - targetMilliseconds) < .002 && synced.IsPlaybackActive,
            "lightweight playback drifted between maps with different BPM/offset");
        Require(independent.CurrentBeat == 17, "playback moved an independently scrolling track");
        Require(metric.CurrentBeat == main.CurrentBeat && Field<double?>(metric, "viewportAnchor") == metricAnchor,
            "metric playback lost its cached physical anchor");
        Require(Field<double>(wave, "currentBeat") == main.CurrentBeat && Field<double>(spectrum, "currentBeat") == main.CurrentBeat,
            "lightweight playback stopped advancing waveform/spectrum");
        var beforeZoomLayouts = Field<long>(window, "trackViewportLayoutUpdates");
        main.SetZoom(560);
        Require(Field<long>(window, "trackViewportLayoutUpdates") > beforeZoomLayouts && synced.PixelsPerBeat == main.PixelsPerBeat,
            "zoom changes skipped full viewport synchronization");
        var beforeSettingsLayouts = Field<long>(window, "trackViewportLayoutUpdates");
        model.ActiveTab!.Session.SnapDivision = 12;
        main.NoteStyle = EditorNoteStyle.Malody;
        Invoke(window, "SetProgrammaticPlaybackBeat", 8d);
        Require(Field<long>(window, "trackViewportLayoutUpdates") == beforeSettingsLayouts + 1 &&
            synced.Session.SnapDivision == 12 && synced.NoteStyle == EditorNoteStyle.Malody,
            "plain-property grid/skin changes did not trigger a single full synchronization");
        Invoke(window, "SetProgrammaticPlaybackBeat", 8.01d);
        Require(Field<long>(window, "trackViewportLayoutUpdates") == beforeSettingsLayouts + 1,
            "unchanged grid/skin configuration kept triggering full synchronization");
        var lightWatch = Stopwatch.StartNew();
        for (var index = 0; index < 500; index++) Invoke(window, "AdvanceTrackPlaybackViewports");
        lightWatch.Stop();
        var fullWatch = Stopwatch.StartNew();
        for (var index = 0; index < 500; index++) Invoke(window, "SyncTrackViewports");
        fullWatch.Stop();
        main.SetPlaybackActive(true); synced.SetPlaybackActive(true);
        Invoke(window, "ConfigureEditorFrameRateRuntime", false);
        Require(!synced.IsPlaybackActive, "secondary playback navigation remained active after pause");
        wave.IsVisible = oldWaveVisible; spectrum.IsVisible = oldSpectrumVisible; main.NoteStyle = EditorNoteStyle.Osu;
        model.Activate(original.Id); Program.Pump(30);
        Console.WriteLine($"PASS 500 multitrack playback frames ({watch.Elapsed.TotalMilliseconds:0.###} ms); viewport-only 500 updates: light {lightWatch.Elapsed.TotalMilliseconds:0.###} ms / full {fullWatch.Elapsed.TotalMilliseconds:0.###} ms, cross-BPM/offset sync, cached anchors, independent tracks and full zoom sync");
    }

    private static void Add(MainWindow window, EditorTrackKind kind) => Wait((Task)Invoke(window, "AddEditorTrackAsync", kind, null)!);
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static object? Invoke(object root, string name, params object?[] args) => root.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(root, args);
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
    private static T Property<T>(object root, string name) => (T)root.GetType().GetProperty(name)!.GetValue(root)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

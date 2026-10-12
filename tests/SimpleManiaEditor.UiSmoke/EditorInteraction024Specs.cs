using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using NAudio.Wave;

internal static class EditorInteraction024Specs
{
    public static void RunAudio()
    {
        var waveform = new AudioWaveform(10, Enumerable.Repeat(1f, 100).ToArray(), 1000);
        var spectrum = new AudioSpectrogram(10, 8, Enumerable.Repeat((byte)255, 800).ToArray(), 1000, 20, 20000);
        var map = new TimingMap([new TimingEvent { Bpm = 120, Delay = -475 }]);
        var scene = TimelineAudioBackgroundSceneBuilder.Build(new(waveform, spectrum), map, 0, 140, 300, 400, true, true);
        using (scene.Spectrogram)
        {
            // 100ms is beat -0.75, y=405. It must be drawn although it precedes beat zero.
            var row = (int)((405 - scene.TopY) / scene.RowPixels);
            Require(scene.WaveformPeaks![row] > .9, "waveform before offset was cut");
            Require(scene.Spectrogram!.BgraPixels[row * scene.Spectrogram.Width * 4 + 3] > 0, "spectrum before offset was cut");
        }
        var side = new AudioVisualizationSideSurface(AudioVisualizationSideKind.Waveform);
        side.SetViewport(-.75, 140, .79); side.SetPlaybackBeat(-.5);
        Require(Field<double>(side, "currentBeat") == -.5, "audio side still clamps native negative beats");
        Require(EditorAudioVisualCalibration.ChartTime(1000, .5, 100) == 1050 && EditorAudioVisualCalibration.AudioTime(1050, .5, 100) == 1000,
            "global audio/display calibration has wrong rate/sign");
        CheckSlowPitch(1); CheckSlowPitch(2);
        Console.WriteLine("PASS full before-offset audio visualization, signed calibration and actual 0.1x pitch-preserving DSP");
    }

    public static void RunWorkbench(MainWindow window, string screenshots)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel"); var original = model.ActiveTab!;
        var editorRate = Field<ComboBox>(window, "fixedRatePicker"); var testRate = Find<ComboBox>(window, "Editor.Playtest.Rate");
        Require(editorRate.Items.Cast<double>().Contains(.1) && !editorRate.Items.Cast<double>().Contains(.2), "editor slow preset was not replaced");
        Require(!testRate.Items.Cast<double>().Any(r => r is .2 or 2), "playtest unsupported presets remain");
        editorRate.SelectedItem = .1; var transport = Field<WindowsAudioTransportViewModel>(window, "audioTransport");
        Program.PumpUntil(() => transport.PlaybackRate == .1);
        Program.PumpUntil(() => Field<int>(window, "fixedRateChangesInFlight") == 0);
        typeof(MainWindow).GetMethod("PersistPendingEditorViewSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]);
        Require(WindowsEditorViewSettingsStore.Load().PlaybackRate == .1, "0.1x rate was not persisted");
        editorRate.SelectedItem = 1d; Program.PumpUntil(() => transport.PlaybackRate == 1);
        Wait(model.CreateAsync("Offset024")); Program.Pump(60);
        var session = model.ActiveTab!.Session; var surface = Field<TimelineSurface>(window, "timeline");
        var tabs = Field<TabControl>(window, "editorInspectorTabs"); tabs.SelectedItem = Field<TabItem>(window, "timingInspectorTab");
        surface.NavigateToBeat(2); Program.Pump(30);
        var before = new TimingMap(session.Document.Timing).BeatToMilliseconds(BeatPosition.FromDouble(surface.CurrentBeat));
        Click("Timing.Offset.Nudge.10");
        Require(Math.Abs(-TimingMap.FindSongOffsetTiming(session.Document.Timing)!.Delay - 10) < .001, "offset nudge did not apply immediately");
        Require(Math.Abs(new TimingMap(session.Document.Timing).BeatToMilliseconds(BeatPosition.FromDouble(surface.CurrentBeat, 1000000)) - before) < .001,
            "offset nudge changed paused song position");
        Require(session.Undo(), "offset nudge was not undoable");
        surface.NavigateToBeat(2); Program.Pump(30); Click("Timing.OffsetHere");
        Require(Math.Abs(-TimingMap.FindSongOffsetTiming(session.Document.Timing)!.Delay - 1000) < .001, "visible offset-here button did not use current time");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "timing-offset-024.png"), PngBitmapEncoderOptions.Default);
        tabs.SelectedItem = Field<TabItem>(window, "settingsInspectorTab"); Click("Settings.Navigate.3"); Program.Pump(40);
        var serial = session.ChangeSerial;
        var calibration = Find<Slider>(window, "Settings.AudioVisualOffset"); calibration.Value = 100;
        Require(EditorPresentationSettingsStore.LoadAudioVisualOffset() == 100 && session.ChangeSerial == serial, "global calibration modified the chart or was not saved");
        Require(Find<Button>(window, "Settings.AudioVisualOffset.Reset").IsVisible, "global calibration reset is missing");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "audio-calibration-024.png"), PngBitmapEncoderOptions.Default);
        surface.SetCurrentBeat(new TimingMap(session.Document.Timing).MillisecondsToBeat(1050, 1000000).Value);
        SetField("lastCalibratedCursorMilliseconds", (double?)1050); SetField("lastCalibratedCursorRate", .5d);
        SetField("lastCalibratedCursorOffsetMilliseconds", 100); SetField("lastCalibratedCursorTabId", (Guid?)model.ActiveTab!.Id);
        calibration.Value = 200;
        var resumed = (double)typeof(MainWindow).GetMethod("ResolveEditorAudioResumeMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [model.ActiveTab])!;
        Require(Math.Abs(resumed - 1000) < .001, "calibrated pause/resume accumulated the offset or used changed calibration");
        Click("Settings.AudioVisualOffset.Reset"); Require(calibration.Value == 0, "global calibration reset failed");
        model.Activate(original.Id); Program.Pump(40);
        Console.WriteLine("PASS separate rate presets/0.1x transport, obvious offset-here/nudges/undo and persisted global audio calibration");
        void SetField(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        void Click(string id) => Find<Button>(window, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void CheckSlowPitch(int channels)
    {
        var source = new SineSource(channels);
        var type = typeof(WindowsAudioPlaybackService).Assembly.GetType("LumaChart.Avalonia.Audio.WindowsPhaseVocoderTimeStretchSampleProvider")!;
        var stretch = (ISampleProvider)Activator.CreateInstance(type, source, new Func<bool>(() => false), .1)!;
        Require((int)type.GetProperty("StageCount")!.GetValue(stretch)! == 4 && (double)type.GetProperty("PlaybackRate")!.GetValue(stretch)! == .1,
            "0.1x DSP was clamped or has wrong stage count");
        var performance = System.Diagnostics.Stopwatch.StartNew();
        Require((bool)type.GetMethod("Prime")!.Invoke(stretch, null)!, "slow DSP did not prime");
        Console.WriteLine($"0.1x {channels}ch DSP prime {performance.Elapsed.TotalMilliseconds:F1} ms");
        performance.Restart();
        var buffer = new float[48000 * channels]; var total = 0;
        while (total < buffer.Length) { var read = stretch.Read(buffer, total, Math.Min(2048, buffer.Length - total)); Require(read > 0, "slow DSP starved"); total += read; }
        Console.WriteLine($"0.1x {channels}ch DSP 1s render {performance.Elapsed.TotalMilliseconds:F1} ms");
        Require(buffer.All(float.IsFinite), "slow DSP emitted non-finite samples");
        double best = 0, frequency = 0;
        for (double hz = 410; hz <= 470; hz += .5)
        {
            double re = 0, im = 0;
            for (int i = 24000; i < 48000; i++) { var angle = 2 * Math.PI * hz * i / 48000; re += buffer[i * channels] * Math.Cos(angle); im += buffer[i * channels] * Math.Sin(angle); }
            var score = re * re + im * im; if (score > best) { best = score; frequency = hz; }
        }
        Require(best > 1 && Math.Abs(frequency - 440) < 8, $"0.1x pitch is {frequency}Hz instead of 440Hz");
    }
    private sealed class SineSource(int channels) : ISampleProvider
    {
        private long sample;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, channels);
        public int Read(float[] buffer, int offset, int count) { for (int i = 0; i < count; i++) buffer[offset + i] = (float)(.2 * Math.Sin(2 * Math.PI * 440 * (sample++ / channels) / 48000)); return count; }
    }
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

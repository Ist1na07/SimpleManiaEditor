using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;
using NAudio.Wave;

internal static class InteractionRegressionSpecs
{
    public static void Run()
    {
        Require(new WindowsEditorViewSettings().FrameRateLimit == 1000, "default cap is not 1000 FPS");
        CheckFrameDefaultsMigration();
        var settings = new WindowsEditorShortcutSettings { ReverseTimeWheel = false };
        WindowsEditorShortcutSettingsStore.Save(settings);
        Require(!WindowsEditorShortcutSettingsStore.Load().ReverseTimeWheel, "wheel preference was not persisted");
        settings.ReverseTimeWheel = true;
        WindowsEditorShortcutSettingsStore.Save(settings);
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var timeline = new TimelineSurface { Session = session, Tool = TimelineTool.Tap };
        var window = new Window { Width = 1000, Height = 800, Content = timeline };
        window.Show();
        timeline.SetCurrentBeat(4);
        Dispatcher.UIThread.RunJobs();
        var point = new Point(500, 400);
        var before = timeline.CurrentBeat;
        window.MouseWheel(point, new Vector(0, -1));
        Require(timeline.CurrentBeat > before, "osu downward wheel did not advance time");
        before = timeline.CurrentBeat;
        timeline.ReverseTimeWheel = false;
        window.MouseWheel(point, new Vector(0, -1));
        Require(timeline.CurrentBeat < before, "optional legacy wheel direction did not work");
        timeline.ReverseTimeWheel = true;
        var gridRequests = 0;
        timeline.SnapDivisionChangeRequested += (_, step) => { gridRequests += step; session.SnapDivision = 8; };
        before = timeline.CurrentBeat;
        var zoom = timeline.PixelsPerBeat;
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
        Require(gridRequests == 1 && session.SnapDivision == 8 && timeline.CurrentBeat == before && timeline.PixelsPerBeat == zoom,
            "Ctrl wheel changed time/zoom instead of grid");
        session.SnapDivision = 4;
        var probe = timeline.GetPlacementEvidenceProbe() ?? throw new InvalidOperationException("missing reverse gesture probe");
        window.MouseDown(probe.HoldEndTarget.SurfacePoint, MouseButton.Left);
        window.MouseMove(probe.HoldStartTarget.SurfacePoint, RawInputModifiers.LeftMouseButton);
        window.MouseUp(probe.HoldStartTarget.SurfacePoint, MouseButton.Left);
        var hold = session.Document.Notes.Single();
        Require(hold.IsHold && hold.Beat == probe.HoldStartTarget.Beat && hold.EndBeat == probe.HoldEndTarget.Beat,
            "reverse drag with RC tool did not make LN");
        var outside = probe.HoldEndTarget.SurfacePoint - new Vector(0, 31);
        RightClick(window, outside);
        Require(session.Document.Notes.Count == 1, "right click in forgiving LN resize zone deleted LN");
        var neighbor = new ChartNote { Lane = hold.Lane, Beat = BeatPosition.FromDouble(hold.EndBeat!.Value.Value + .125) };
        session.Mutate(d => d.Notes.Add(neighbor));
        RightClick(window, probe.HoldEndTarget.SurfacePoint - new Vector(0, timeline.PixelsPerBeat * .125));
        Require(session.Document.Notes.Count == 1 && session.Document.Notes[0].Id == hold.Id,
            "right click chose enlarged LN tail instead of adjacent tap");
        Require(session.Undo() && session.Document.Notes.Count == 2, "right-delete undo failed");
        window.Close();
        CheckAudioEnvelope();
        Console.WriteLine("PASS wheel preferences, grid modifier, reverse RC-LN, precise right deletion, sample-clock transport fades");
    }

    private static void CheckAudioEnvelope()
    {
        var envelope = new TransportEnvelopeSampleProvider(new ConstantStereoSource());
        var data = new float[1024];
        envelope.FadeIn();
        envelope.Read(data, 0, data.Length);
        Require(data[0] == 0 && Math.Abs(data[^1] - 1) < 1e-6, "start ramp not silent-to-full");
        AssertSmoothStereo(data);
        var fade = envelope.FadeOut();
        envelope.Read(data, 0, data.Length);
        Require(fade.IsCompleted && data[0] == 1 && data[^1] == 0, "pause ramp did not reach silence");
        AssertSmoothStereo(data);
        envelope.FadeIn();
        envelope.Read(data, 0, 192);
        Require(data[0] == 0 && data[190] > 0 && data[190] < 1, "resume ramp restarted discontinuously");
    }

    private static void CheckFrameDefaultsMigration()
    {
        var path = WindowsEditorViewSettingsStore.SettingsPath;
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{\"SchemaVersion\":1,\"FrameRateLimit\":165}");
            Require(WindowsEditorViewSettingsStore.Load().FrameRateLimit == 1000, "old default FPS was not migrated");
            WindowsEditorViewSettingsStore.Save(new WindowsEditorViewSettings().WithFrameRateLimit(165));
            Require(WindowsEditorViewSettingsStore.Load().FrameRateLimit == 165, "explicit new FPS choice was overwritten");
        }
        finally { if (original == null) File.Delete(path); else File.WriteAllBytes(path, original); }
    }

    private static void AssertSmoothStereo(float[] samples)
    {
        for (var i = 0; i < samples.Length; i += 2)
        {
            Require(samples[i] == samples[i + 1], "fade split stereo channels");
            if (i >= 2) Require(Math.Abs(samples[i] - samples[i - 2]) <= 1f / 240 + .00001f, "fade introduced a large amplitude step");
        }
    }
    private static void RightClick(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ConstantStereoSource : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, 1f, offset, count); return count; }
    }
}

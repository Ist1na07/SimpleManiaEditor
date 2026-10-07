using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LumaChart.Avalonia.Audio;

internal static class SeekPerformanceSpecs
{
    public static void CheckCanceledPreparation(string wave)
    {
        var previousOutputs = PlatformAudioOutputs.Current;
        var outputs = new SimulatedAudioOutputFactory();
        PlatformAudioOutputs.Current = outputs;
        try
        {
            using var engine = new WindowsAudioDeviceEngine();
            engine.Load(wave);
            engine.PreservePitch = true;
            engine.PlaybackRate = .5;
            var stages = (Array)typeof(WindowsAudioDeviceEngine).GetField("soundTouchProviders", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            if (stages.Length == 0) throw new InvalidOperationException("1x bypass failed to re-enable pitch preservation after a rate change");
            engine.PlaybackRate = 1;
            stages = (Array)typeof(WindowsAudioDeviceEngine).GetField("soundTouchProviders", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            if (stages.Length != 0) throw new InvalidOperationException("return to 1x failed to bypass time stretching");
            engine.PlaybackRate = 2;
            engine.Play();
            engine.Seek(TimeSpan.FromSeconds(2));
            Thread.Sleep(30);
            if (engine.PlaybackRate != 2 || !engine.IsPlaying || outputs.Outputs.All(o => o.BytesRead == 0) ||
                outputs.Outputs.Any(o => o.ReadFailure != null))
                throw new InvalidOperationException("2x pitch-preserving playback did not consume valid audio");
            engine.Pause();
            engine.PlaybackRate = .25;
            engine.Play();
            var checks = 0;
            try
            {
                engine.Seek(TimeSpan.FromSeconds(12), () =>
                {
                    if (++checks == 2) throw new OperationCanceledException("superseded after DSP preparation");
                });
                throw new InvalidOperationException("superseded preparation was committed");
            }
            catch (OperationCanceledException) { }
            var bytes = outputs.Outputs.Sum(o => o.BytesRead);
            Thread.Sleep(30);
            if (checks != 2 || !engine.IsPlaying || engine.Position >= TimeSpan.FromSeconds(10) ||
                outputs.Outputs.Sum(o => o.BytesRead) <= bytes || outputs.Outputs.Any(o => o.ReadFailure != null))
                throw new InvalidOperationException("canceled preparation damaged the active audio pipeline");
        }
        finally { PlatformAudioOutputs.Current = previousOutputs; }
    }

    public static int Run(string? reportPath = null, string? fixtureDirectory = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "sme-seek-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var wave = Path.Combine(root, "tone.wav");
        Program.WriteSimulationWave(wave);
        var results = new List<object>();
        try
        {
            var fixtures = fixtureDirectory == null ? new[] { wave } : Directory.GetFiles(fixtureDirectory)
                .Where(f => new[] { ".wav", ".mp3", ".ogg" }.Contains(Path.GetExtension(f))).ToArray();
            foreach (var fixture in fixtures)
            foreach (var mode in new[] { (Rate: 1d, Pitch: false), (Rate: 1d, Pitch: true), (Rate: .5, Pitch: true), (Rate: .25, Pitch: true) })
            {
                var outputs = new SimulatedAudioOutputFactory();
                PlatformAudioOutputs.Current = outputs;
                using var engine = new WindowsAudioDeviceEngine();
                engine.Load(fixture);
                engine.PlaybackRate = mode.Rate;
                engine.PreservePitch = mode.Pitch;
                engine.Play();
                Thread.Sleep(50);
                var initialOutputs = outputs.Outputs.Count;
                var durations = new List<double>();
                var streamingDuringSeek = 0;
                foreach (var target in new[] { 3, 7, 2, 9, 4, 8, 1, 6 })
                {
                    var watch = Stopwatch.StartNew();
                    var bytesBefore = outputs.Outputs.Sum(o => o.BytesRead);
                    engine.Seek(TimeSpan.FromSeconds(target));
                    durations.Add(watch.Elapsed.TotalMilliseconds);
                    if (outputs.Outputs.Sum(o => o.BytesRead) > bytesBefore) streamingDuringSeek++;
                    if (!engine.IsPlaying) throw new InvalidOperationException("seek interrupted playing state");
                    Thread.Sleep(10);
                }
                if (outputs.Outputs.Any(o => o.ReadFailure != null)) throw new InvalidOperationException("DSP read failed during benchmark");
                var sorted = durations.Order().ToArray();
                if (outputs.Outputs.Count != initialOutputs) throw new InvalidOperationException("seek recreated the output device");
                results.Add(new { Format = Path.GetExtension(fixture), mode.Rate, PreservePitch = mode.Pitch, Samples = durations.Count,
                    MedianMs = sorted[sorted.Length / 2], P95Ms = sorted[^1],
                    StreamingDuringSeek = streamingDuringSeek,
                    MaxCallbackReadMs = outputs.Outputs.Max(o => o.MaxReadMilliseconds),
                    DeviceRebuilds = outputs.Outputs.Count - initialOutputs });
            }
            var report = JsonSerializer.Serialize(new { Fixture = fixtureDirectory == null
                ? "20s 16kHz mono WAV; simulated 5ms consuming sink; not hardware latency"
                : "Generated 20s fixtures in WAV/MP3/Ogg; simulated 5ms consuming sink; not hardware latency", Results = results },
                new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(report);
            if (reportPath != null) File.WriteAllText(reportPath, report);
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

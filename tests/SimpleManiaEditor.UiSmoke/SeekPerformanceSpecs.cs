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
            SpinWait.SpinUntil(() => outputs.Outputs.Any(o => o.BytesRead > 0 || o.ReadFailure != null), TimeSpan.FromSeconds(1));
            if (engine.PlaybackRate != 2 || !engine.IsPlaying || outputs.Outputs.All(o => o.BytesRead == 0) ||
                outputs.Outputs.Any(o => o.ReadFailure != null))
                throw new InvalidOperationException($"2x pitch-preserving playback did not consume valid audio: rate={engine.PlaybackRate},playing={engine.IsPlaying},bytes={outputs.Outputs.Sum(o => o.BytesRead)},errors={string.Join(';', outputs.Outputs.Select(o => o.ReadFailure?.ToString()))}");
            foreach (var rate in new[] { .1, .25, .5, .75, 1d, 1.1, 1.5, 2 })
            {
                engine.Seek(TimeSpan.FromSeconds(2));
                var deviceCount = outputs.Outputs.Count;
                var beforeRate = engine.Position;
                engine.PlaybackRate = rate;
                if (outputs.Outputs.Count != deviceCount || !engine.IsPlaying || engine.PlaybackRate != rate ||
                    engine.Position < beforeRate - TimeSpan.FromMilliseconds(100))
                    throw new InvalidOperationException($"{rate}x rate boundary rebuilt the device, interrupted playback, or rewound transport");
                var checks = 0;
                try
                {
                    engine.Seek(TimeSpan.FromSeconds(12), () =>
                    {
                        if (++checks == 2) throw new OperationCanceledException("superseded after DSP preparation");
                    });
                    throw new InvalidOperationException($"{rate}x superseded preparation was committed");
                }
                catch (OperationCanceledException) { }
                var bytes = outputs.Outputs.Sum(o => o.BytesRead);
                SpinWait.SpinUntil(() => outputs.Outputs.Sum(o => o.BytesRead) > bytes || outputs.Outputs.Any(o => o.ReadFailure != null), TimeSpan.FromSeconds(1));
                if (checks != 2 || !engine.IsPlaying || engine.Position >= TimeSpan.FromSeconds(10) ||
                    outputs.Outputs.Sum(o => o.BytesRead) <= bytes || outputs.Outputs.Any(o => o.ReadFailure != null) ||
                    outputs.Outputs.Count != deviceCount)
                    throw new InvalidOperationException($"{rate}x canceled preparation damaged the active pipeline: checks={checks}, playing={engine.IsPlaying}, position={engine.Position}, bytes={bytes}->{outputs.Outputs.Sum(o => o.BytesRead)}, errors={string.Join(';', outputs.Outputs.Select(o => o.ReadFailure?.Message))}");
                engine.Seek(TimeSpan.FromSeconds(3));
                if (outputs.Outputs.Count != deviceCount || !engine.IsPlaying ||
                    Math.Abs(engine.Position.TotalSeconds - 3) > .2)
                    throw new InvalidOperationException($"{rate}x prepared seek failed to commit on the existing device");
            }
            Console.WriteLine("PASS all-rate prepared cancellation, continuous old stream, device reuse and safe rate boundaries");
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
        var previousOutputs = PlatformAudioOutputs.Current;
        try
        {
            var fixtures = fixtureDirectory == null ? new[] { wave } : Directory.GetFiles(fixtureDirectory)
                .Where(f => new[] { ".wav", ".mp3", ".ogg" }.Contains(Path.GetExtension(f))).ToArray();
            foreach (var fixture in fixtures)
            foreach (var mode in new[] { (Rate: 1d, Pitch: false), (Rate: 1d, Pitch: true), (Rate: .5, Pitch: true), (Rate: .25, Pitch: true), (Rate: .1, Pitch: true), (Rate: .75, Pitch: true), (Rate: .9, Pitch: true), (Rate: 1.1, Pitch: true), (Rate: 1.25, Pitch: true), (Rate: 1.5, Pitch: true), (Rate: 2d, Pitch: true) })
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
                // Let several complete SoundTouch sequences reach the callback,
                // rather than measuring only the prefetched 20ms between seeks.
                var steadyMilliseconds = mode.Rate < .3 ? 2000 : 500;
                Thread.Sleep(steadyMilliseconds);
                if (outputs.Outputs.Any(o => o.ReadFailure != null)) throw new InvalidOperationException("DSP read failed during benchmark");
                if (!engine.IsPlaying || engine.Diagnostics.UnexpectedPlaybackStopCount != 0 || engine.Diagnostics.TransientMusicZeroCount != 0)
                    throw new InvalidOperationException($"{mode.Rate}x {Path.GetExtension(fixture)} stopped or starved during steady playback");
                var sorted = durations.Order().ToArray();
                if (outputs.Outputs.Count != initialOutputs) throw new InvalidOperationException("seek recreated the output device");
                results.Add(new { Format = Path.GetExtension(fixture), mode.Rate, PreservePitch = mode.Pitch, Samples = durations.Count,
                    MedianMs = sorted[sorted.Length / 2], P95Ms = sorted[^1],
                    StreamingDuringSeek = streamingDuringSeek,
                    MaxCallbackReadMs = outputs.Outputs.Max(o => o.MaxReadMilliseconds),
                    DeviceRebuilds = outputs.Outputs.Count - initialOutputs,
                    SteadyPlaybackMilliseconds = steadyMilliseconds,
                    engine.Diagnostics.TransientMusicZeroCount,
                    engine.Diagnostics.UnexpectedPlaybackStopCount });
            }
            var report = JsonSerializer.Serialize(new { Fixture = fixtureDirectory == null
                ? "20s 16kHz mono WAV; simulated 5ms consuming sink; not hardware latency"
                : "Generated 20s fixtures in WAV/MP3/Ogg; simulated 5ms consuming sink; not hardware latency", Results = results },
                new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(report);
            if (reportPath != null) File.WriteAllText(reportPath, report);
            return 0;
        }
        finally { PlatformAudioOutputs.Current = previousOutputs; Directory.Delete(root, recursive: true); }
    }
}

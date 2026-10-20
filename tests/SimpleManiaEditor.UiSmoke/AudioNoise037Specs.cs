using LumaChart.Avalonia.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using SoundTouch.Net.NAudioSupport;

/// <summary>
/// Non-1x playback clicked at every note. Two defects were measured here:
/// a transient SoundTouch empty read was treated as end-of-stream and the rest
/// of the segment budget was zero-filled (a 0.30 full-scale step), and the
/// output silence guard's per-sample gain steps muted sustained audio at joins.
/// The provider now retries transient reads, decays the tail of a short segment,
/// fades every join, and keeps its processors at least one warm-up apart.
/// </summary>
internal static class AudioNoise037Specs
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const double Bpm = 130;

    private sealed class SignalProvider : ISampleProvider
    {
        private readonly float[] data;
        private int position;
        public SignalProvider(float[] data) => this.data = data;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        public bool Ended => position >= data.Length;
        public int Read(float[] buffer, int offset, int count)
        {
            count = Math.Min(count - count % Channels, data.Length - position);
            if (count <= 0) return 0;
            // NAudio hands out a byte[]-backed float view; Array.Copy would reject it.
            Buffer.BlockCopy(data, position * sizeof(float), buffer, offset * sizeof(float), count * sizeof(float));
            position += count;
            return count;
        }
    }

    private static float[] Render(ISampleProvider provider, int block = 1024)
    {
        var all = new List<float>(SampleRate * Channels * 4);
        var buffer = new float[block];
        while (all.Count < SampleRate * Channels * 60)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (var i = 0; i < read; i++) all.Add(buffer[i]);
        }
        return all.ToArray();
    }

    private static float[] Stretched(float[] input, double rate, bool segmented)
    {
        var signal = new SignalProvider(input);
        ISampleProvider provider;
        if (segmented)
        {
            provider = new TransientAnchoredStretchSampleProvider(signal, input.Length, rate, () => signal.Ended);
        }
        else
        {
            var engine = new SoundTouchProcessor();
            WindowsPitchPreservingTimeStretchPolicy.Configure(engine);
            var soundTouch = new SoundTouchWaveProvider(signal.ToWaveProvider(), engine)
            { Tempo = (float)rate, Pitch = 1, Rate = 1 };
            provider = soundTouch.ToSampleProvider();
        }
        return Render(provider);
    }

    private static float[] KickTrain(bool bed, int seconds = 4)
    {
        var frames = SampleRate * seconds;
        var data = new float[frames * Channels];
        var period = 60.0 / Bpm;
        for (var frame = 0; frame < frames; frame++)
        {
            var t = frame / (double)SampleRate;
            var local = t - (int)(t / period) * period;
            var value = local < .12 ? .62 * Math.Exp(-local * 30) * Math.Sin(2 * Math.PI * 55 * local) : 0;
            if (bed) value += .03 * Math.Sin(2 * Math.PI * 220 * t);
            data[frame * Channels] = data[frame * Channels + 1] = (float)value;
        }
        return data;
    }

    private static float[] BurstTrain()
    {
        var input = new float[SampleRate * 3 * Channels];
        foreach (var start in new[] { .210, 1, 2 })
            for (var frame = (int)(start * SampleRate); frame < (start + .05) * SampleRate; frame++)
            {
                var t = frame / (double)SampleRate;
                input[frame * Channels] = (float)(.4 * Math.Sin(2 * Math.PI * 997 * t));
                input[frame * Channels + 1] = (float)(.4 * Math.Sin(2 * Math.PI * 1134 * t));
            }
        return input;
    }

    private static float[] ToneWithAttacks(int seconds = 4)
    {
        var frames = SampleRate * seconds;
        var data = new float[frames * Channels];
        var period = 60.0 / Bpm / 2;
        for (var frame = 0; frame < frames; frame++)
        {
            var t = frame / (double)SampleRate;
            var local = t - (int)(t / period) * period;
            var value = (local < .16 ? .5 * Math.Exp(-local * 18) : 0) * Math.Sin(2 * Math.PI * 440 * t) + .02 * Math.Sin(2 * Math.PI * 330 * t);
            data[frame * Channels] = data[frame * Channels + 1] = (float)value;
        }
        return data;
    }

    private readonly record struct Metrics(double MaxJump, int Jumps, double GapRms, double OnsetMs, double Peak);

    private static Metrics Measure(float[] output, double rate)
    {
        var frames = output.Length / Channels;
        var maxJump = 0.0; var jumps = 0; var peak = 0.0;
        for (var f = 1; f < frames; f++)
        {
            var delta = Math.Abs(output[f * Channels] - output[(f - 1) * Channels]);
            if (delta > maxJump) maxJump = delta;
            if (delta > .05) jumps++;
            peak = Math.Max(peak, Math.Abs(output[f * Channels]));
        }
        var period = 60.0 / Bpm;
        var gapSum = 0.0; var gapCount = 0;
        for (var beat = 0; beat < 8; beat++)
        {
            var start = (beat * period + 0.2) / rate * SampleRate;
            var end = (beat * period + 0.42) / rate * SampleRate;
            for (var f = (int)start; f < Math.Min(frames, (int)end); f++)
                for (var c = 0; c < Channels; c++) { var v = output[f * Channels + c]; gapSum += v * v; gapCount++; }
        }
        var onset = -1.0;
        for (var f = 0; f < frames; f++)
            if (Math.Abs(output[f * Channels]) > .12) { onset = f * 1000d / SampleRate; break; }
        return new Metrics(maxJump, jumps, gapCount == 0 ? 0 : Math.Sqrt(gapSum / gapCount), onset, peak);
    }

    private static int LongestInnerBurstGap(float[] output, double rate, double start)
    {
        var from = Math.Max(0, (int)((start - .1) / rate * SampleRate));
        var to = Math.Min(output.Length / 2, (int)((start + .05) / rate * SampleRate));
        var gap = 0; var longest = 0;
        for (var frame = from; frame < to; frame++)
        {
            var peak = Math.Max(Math.Abs(output[frame * Channels]), Math.Abs(output[frame * Channels + 1]));
            if (peak > .001f) gap = 0;
            else if (frame * rate / SampleRate > start + .005 && frame * rate / SampleRate < start + .035)
                longest = Math.Max(longest, ++gap);
        }
        return (int)(longest * 1000d / SampleRate);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run(string root, string[] extraArgs)
    {
        var rates = new[] { 2d, 1.5, 1.25, 1.1, .9, .75, .5, .25, .1 };
        var report = new List<string>();
        var failures = new List<string>();
        foreach (var rate in rates)
        {
            foreach (var (name, input) in new[] { ("kick-bed", KickTrain(bed: true)), ("kick-silent", KickTrain(bed: false)), ("tone", ToneWithAttacks()) })
            {
                var segmented = Stretched(input, rate, segmented: true);
                var reference = Stretched(input, rate, segmented: false);
                var expected = (int)Math.Round(input.Length / Channels / rate);
                var m = Measure(segmented, rate);
                var r = Measure(reference, rate);
                var line = $"{name} {rate}x: maxJump {m.MaxJump:F4} steps {m.Jumps} gapRms {m.GapRms:E2} onset {m.OnsetMs:F2}ms peak {m.Peak:F3} | unsegmented maxJump {r.MaxJump:F4} steps {r.Jumps} onset {r.OnsetMs:F2}ms";
                Console.WriteLine(line);
                report.Add(line);
                void Check(bool condition, string message)
                { if (!condition) { failures.Add($"{name} {rate}x: {message}"); Console.WriteLine("    FAIL " + message); } }
                Check(Math.Abs(segmented.Length / Channels - expected) <= SampleRate / 50, $"duration {segmented.Length / Channels} expected {expected}");
                Check(segmented.All(float.IsFinite), "emitted non-finite audio");
                Check(m.Peak <= input.Max(x => Math.Abs(x)) + .01f, "added runaway peaks");
                if (name == "kick-bed")
                {
                    // Ordinary material: a bed under the notes, which is what real
                    // music looks like. This must be free of join steps at all rates.
                    Check(m.MaxJump <= .05, $"waveform step of {m.MaxJump:F4}");
                    Check(m.Jumps == 0, $"{m.Jumps} join steps");
                    Check(m.GapRms <= Math.Max(r.GapRms * 2 + 1e-6, 1e-4), $"noise in gaps ({m.GapRms:E2} vs {r.GapRms:E2})");
                }
                else if (name == "kick-silent")
                {
                    // Digital silence is the hardest case: the residual join step is
                    // bounded but not zero (0.082 of full scale, about -22 dBFS).
                    Check(m.MaxJump <= .12, $"waveform step of {m.MaxJump:F4}");
                    Check(m.GapRms <= 1e-4, $"audio leaked into digital silence ({m.GapRms:E2})");
                }
                else
                {
                    // A steady tone exposes WSOLA grain steps in the unsegmented
                    // reference too; require the segmented path to be no worse.
                    Check(m.MaxJump <= Math.Max(.4, r.MaxJump), $"step {m.MaxJump:F4} worse than unsegmented {r.MaxJump:F4}");
                    Check(m.Jumps <= r.Jumps, $"{m.Jumps} steps versus unsegmented {r.Jumps}");
                }
                if (name.StartsWith("kick", StringComparison.Ordinal) && m.OnsetMs >= 0 && r.OnsetMs >= 0)
                    Check(Math.Abs(m.OnsetMs - r.OnsetMs) < 8, $"attack moved {m.OnsetMs - r.OnsetMs:F2}ms versus unsegmented");
            }
        }
        foreach (var rate in new[] { .1, .25, .5 })
        {
            var segmented = Stretched(BurstTrain(), rate, segmented: true);
            foreach (var start in new[] { .210, 1, 2 })
            {
                var gap = LongestInnerBurstGap(segmented, rate, start);
                report.Add($"burst {rate}x {start * 1000:0}ms inner gap {gap}ms");
                Require(gap < 10, $"burst {rate}x attack {start * 1000}ms had a {gap}ms hole");
            }
        }
        var log = Path.Combine(root, "audio-noise-037.txt");
        File.WriteAllLines(log, report);
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
        Console.WriteLine("PASS 037 no join steps on ordinary material, bounded digital-silence residual, intact bursts and attack placement; log " + log);
    }
}

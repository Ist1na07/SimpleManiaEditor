using System.Diagnostics;
using System.Security.Cryptography;
using LumaChart.Avalonia.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using SoundTouch.Net.NAudioSupport;

internal static class AudioStretch038Specs
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const string RealAudio =
        @"E:\osu!\Songs\beatmap-639151174919692461-Albemuth - 白夜にて\audio.mp3";

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
            Buffer.BlockCopy(data, position * sizeof(float), buffer, offset * sizeof(float), count * sizeof(float));
            position += count;
            return count;
        }
    }

    private static float[] Render(ISampleProvider provider, int maxFrames)
    {
        var all = new List<float>(maxFrames * Channels);
        var buffer = new float[1024];
        while (all.Count < maxFrames * Channels)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (var i = 0; i < read; i++) all.Add(buffer[i]);
        }
        return all.ToArray();
    }

    private static (float[] Pcm, int Segments, double PaddedMs, int Retries) Stretch(float[] input, double rate, bool segmented)
    {
        var signal = new SignalProvider(input);
        ISampleProvider provider;
        TransientAnchoredStretchSampleProvider? anchored = null;
        if (segmented)
        {
            anchored = new TransientAnchoredStretchSampleProvider(signal, input.Length, rate, () => signal.Ended);
            provider = anchored;
        }
        else
        {
            var engine = new SoundTouchProcessor();
            WindowsPitchPreservingTimeStretchPolicy.Configure(engine);
            provider = new SoundTouchWaveProvider(signal.ToWaveProvider(), engine)
            { Tempo = (float)rate, Pitch = 1, Rate = 1 }.ToSampleProvider();
        }
        var pcm = Render(provider, (int)(input.Length / Channels / rate * 1.05) + 1024);
        return (pcm, anchored?.ObservedSegmentCount ?? 0, anchored?.ObservedPaddedMilliseconds ?? 0, anchored?.ObservedEmptyReadRetries ?? 0);
    }

    // ---------- transient integrity ----------
    private static float[] ClickTrain(double spacing, int seconds)
    {
        var frames = SampleRate * seconds;
        var data = new float[frames * Channels];
        for (var frame = 0; frame < frames; frame++)
        {
            var t = frame / (double)SampleRate;
            var local = t - Math.Floor(t / spacing) * spacing - .1;
            if (local < 0 || local >= .08) continue;
            var value = .8 * Math.Exp(-local * 70) * Math.Sin(2 * Math.PI * 1000 * local);
            data[frame * Channels] = data[frame * Channels + 1] = (float)value;
        }
        return data;
    }

    private sealed record ClickReport(int Bursts, int Duplicates, int Missing, double Peak, double FirstOnset)
    {
        public override string ToString() =>
            $"bursts {Bursts} dup {Duplicates} missing {Missing} peak {Peak:F2} onset {FirstOnset:F1}ms";
    }

    private static ClickReport MeasureClicks(float[] output, double rate, double spacing)
    {
        var frames = output.Length / Channels;
        var clusters = new List<(double Start, double Peak)>();
        var start = -1.0; var peak = 0.0; var quiet = 0;
        for (var f = 0; f < frames; f++)
        {
            var value = Math.Abs(output[f * Channels]);
            if (value > .05) { if (start < 0) { start = f * 1000d / SampleRate; peak = 0; } peak = Math.Max(peak, value); quiet = 0; }
            else if (start >= 0 && ++quiet > SampleRate / 100) { clusters.Add((start, peak)); start = -1; }
        }
        if (start >= 0) clusters.Add((start, peak));
        var duplicates = 0;
        for (var i = 1; i < clusters.Count; i++) if (clusters[i].Start - clusters[i - 1].Start < 40) duplicates++;
        var expected = new List<double>();
        for (var at = .1; at < frames * rate / (double)SampleRate - .1; at += spacing) expected.Add(at * 1000 / rate);
        var missing = expected.Count(t => clusters.All(c => Math.Abs(c.Start - t) > 30));
        return new ClickReport(clusters.Count, duplicates, missing,
            clusters.Count == 0 ? 0 : clusters.Max(c => c.Peak),
            clusters.Count == 0 ? -1 : clusters[0].Start);
    }

    // ---------- sustained chord ----------
    private static float[] Chord(int seconds)
    {
        var frames = SampleRate * seconds;
        var data = new float[frames * Channels];
        var freqs = new[] { 220.0, 277.183, 329.628, 440.0 };
        for (var frame = 0; frame < frames; frame++)
        {
            var t = frame / (double)SampleRate;
            var value = 0.0;
            foreach (var frequency in freqs) value += .22 * Math.Sin(2 * Math.PI * frequency * t);
            data[frame * Channels] = data[frame * Channels + 1] = (float)value;
        }
        return data;
    }

    private static double Goertzel(float[] pcm, double frequency, int from, int count)
    {
        var coeff = 2 * Math.Cos(2 * Math.PI * frequency / SampleRate);
        double s0 = 0, s1 = 0, s2 = 0;
        for (var n = 0; n < count; n++) { s0 = pcm[(from + n) * Channels] + coeff * s1 - s2; s2 = s1; s1 = s0; }
        return Math.Sqrt(s1 * s1 + s2 * s2 - coeff * s1 * s2) * 2 / count;
    }

    private sealed record ChordReport(double Rms, double Tone220, double Tonality, double AmDepth, int EnvLagMs)
    {
        public override string ToString() =>
            $"rms {Rms:F4} tone220 {Tone220:F4} tonality {Tonality:F3} amDepth {AmDepth:F3} envLag {EnvLagMs}ms";
    }

    private static ChordReport MeasureChord(float[] output)
    {
        var frames = output.Length / Channels;
        var skip = SampleRate / 2;
        var count = Math.Max(1, Math.Min(SampleRate * 3, frames - skip));
        double sum = 0;
        for (var f = skip; f < skip + count; f++) { var x = output[f * Channels]; sum += x * x; }
        var rms = Math.Sqrt(sum / count);
        var tone220 = Goertzel(output, 220, skip, count);
        var hop = SampleRate * 2 / 1000;
        var env = new List<double>();
        for (var f = skip; f + hop < skip + count; f += hop)
        {
            double e = 0;
            for (var n = 0; n < hop; n++) { var x = output[(f + n) * Channels]; e += x * x; }
            env.Add(Math.Sqrt(e / hop));
        }
        var sorted = env.OrderBy(x => x).ToArray();
        var median = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
        var p5 = sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * .05)];
        var p95 = sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * .95)];
        var depth = median <= 0 ? 0 : (p95 - p5) / median;
        var mean = env.Count == 0 ? 0 : env.Average();
        var variance = env.Sum(x => (x - mean) * (x - mean));
        var bestLag = 0; var best = 0.0;
        if (variance > 1e-15)
            for (var lag = 2; lag <= 60 && lag < env.Count / 2; lag++)
            {
                double s = 0;
                for (var n = 0; n + lag < env.Count; n++) s += (env[n] - mean) * (env[n + lag] - mean);
                var corr = s / variance * env.Count / (env.Count - lag);
                if (corr > best) { best = corr; bestLag = lag; }
            }
        return new ChordReport(rms, tone220, rms <= 0 ? 0 : tone220 / rms, depth, bestLag * 2);
    }

    // ---------- real-time cost ----------
    private sealed record ReadStats(double P50, double P95, double P99, double Max, int Over5, int Over10, int Over20, double AudioMs, double TotalMs)
    {
        public override string ToString() =>
            $"rt {TotalMs / Math.Max(1, AudioMs):F3} p50 {P50:F3} p95 {P95:F3} p99 {P99:F3} max {Max:F3}ms over5 {Over5} over10 {Over10} over20 {Over20}";
    }

    private static ReadStats TimePipeline(double rate, int blockMs, double seconds = 12)
    {
        using var reader = AudioDecoder.OpenWaveStream(RealAudio);
        reader.CurrentTime = TimeSpan.FromSeconds(20);
        using var pipeline = PreparedMusicPipeline.Build(reader, reader.CurrentTime, rate, true, .8f, () => { });
        var stretch = pipeline.Stages.OfType<TransientAnchoredStretchSampleProvider>().FirstOrDefault();
        var block = SampleRate * blockMs / 1000;
        var buffer = new float[block * Channels];
        var times = new List<double>();
        var produced = 0;
        var stopwatch = new Stopwatch();
        while (produced < SampleRate * seconds)
        {
            stopwatch.Restart();
            var read = pipeline.Volume.Read(buffer, 0, buffer.Length);
            stopwatch.Stop();
            times.Add(stopwatch.Elapsed.TotalMilliseconds);
            if (read <= 0) break;
            produced += read / Channels;
        }
        times.Sort();
        double P(double q) => times[Math.Min(times.Count - 1, (int)(times.Count * q))];
        Console.WriteLine($"    [pipeline {rate}x {blockMs}ms] segments {stretch?.ObservedSegmentCount} padded {stretch?.ObservedPaddedMilliseconds:F0}ms retries {stretch?.ObservedEmptyReadRetries}");
        return new ReadStats(P(.5), P(.95), P(.99), times[^1],
            times.Count(x => x > 5), times.Count(x => x > 10), times.Count(x => x > 20),
            produced * 1000d / SampleRate, times.Sum());
    }

    private static string Sha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void Run(string root, string[] extraArgs)
    {
        var report = new List<string>();
        Console.WriteLine("== AudioStretch038Specs measurement ==");
        Console.WriteLine($"real mp3 sha256 before {Sha256(RealAudio)}");

        Console.WriteLine("-- clicks every 250 ms --");
        var clickInput = ClickTrain(.25, 6);
        foreach (var rate in new[] { 2d, 1.5, 1.25, 1.1, .9, .75, .5, .25, .1 })
        {
            var (segmented, segments, padded, retries) = Stretch(clickInput, rate, true);
            var (reference, _, _, _) = Stretch(clickInput, rate, false);
            var line = $"clicks {rate}x: segs {segments} padded {padded:F0} retries {retries} | segmented {MeasureClicks(segmented, rate, .25)} | continuous {MeasureClicks(reference, rate, .25)}";
            Console.WriteLine(line); report.Add(line);
        }

        Console.WriteLine("-- sustained chord --");
        var chordInput = Chord(8);
        foreach (var rate in new[] { 2d, 1.5, 1.25, 1.1, .9, .75, .5, .25, .1 })
        {
            var (segmented, segments, _, _) = Stretch(chordInput, rate, true);
            var (reference, _, _, _) = Stretch(chordInput, rate, false);
            var line = $"chord {rate}x: segs {segments} | segmented {MeasureChord(segmented)} | continuous {MeasureChord(reference)}";
            Console.WriteLine(line); report.Add(line);
        }

        Console.WriteLine("-- real mp3 callback cost --");
        foreach (var rate in new[] { 2d, 1.5, .5, .25, .1 })
            foreach (var blockMs in new[] { 5, 10, 20 })
            {
                var line = $"real {rate}x {blockMs}ms block: {TimePipeline(rate, blockMs)}";
                Console.WriteLine(line); report.Add(line);
            }
        Console.WriteLine($"real mp3 sha256 after {Sha256(RealAudio)}");
        File.WriteAllLines(Path.Combine(root, "audio-stretch-038-measure.txt"), report);
        _ = extraArgs;
    }
}

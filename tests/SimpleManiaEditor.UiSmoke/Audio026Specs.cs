using System.Diagnostics;
using LumaChart.Avalonia.Audio;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static class Audio026Specs
{
    private const int SampleRate = 48_000;
    private static readonly double[] Rates = [.1, .25, .5, .75, .9, 1, 1.1, 1.25, 1.5, 2];

    public static void Run()
    {
        foreach (var rate in Rates)
        {
            foreach (var channels in new[] { 1, 2 })
            {
                var input = Chord(channels, differentStereo: true);
                var small = Render(input, channels, rate, 512);
                var medium = Render(input, channels, rate, 2048);
                var large = Render(input, channels, rate, 8192);
                Require(small.Samples.SequenceEqual(medium.Samples) && small.Samples.SequenceEqual(large.Samples), $"{rate}x {channels}ch changed samples or EOF with device read size");
                var expected = (int)Math.Round(input.Length / rate);
                Require(Math.Abs(small.Samples.Length - expected) <= channels * 16, $"{rate}x {channels}ch duration/EOF is {small.Samples.Length}, expected {expected}");
                Require(small.Samples.All(float.IsFinite), $"{rate}x {channels}ch emitted non-finite audio");
                Require(small.Samples.Max(x => Math.Abs(x)) <= .93f, $"{rate}x {channels}ch added clipping to a bounded chord");
                var leakage = SpectralLeakage(small.Samples, channels, 0, [440, 880, 1760]);
                // A periodic steady chord supplies an objective reference: energy outside the
                // three tones (plus two Hann-window bins each side) must stay low. This catches
                // the former 0.1x chain's 43% / 76% spurious spectral energy without imposing
                // a quality threshold on arbitrary music/transients.
                Require(leakage < .025, $"{rate}x {channels}ch introduced spurious steady-chord energy {leakage:P2}");
                if (channels == 2)
                {
                    var rightLeakage = SpectralLeakage(small.Samples, channels, 1, [550, 1100, 2200]);
                    Require(rightLeakage < .025, $"{rate}x right channel introduced spurious energy {rightLeakage:P2}");
                }
                Console.WriteLine($"Audio 0.26 {rate}x {channels}ch: prime {small.PrimeMs:F2}ms / DSP {small.RenderMs:F2}ms for {small.Samples.Length / (double)(SampleRate * channels):F2}s output; spectral leakage {leakage:P3}");
            }
            var nonharmonic = new float[SampleRate * 2 * 2];
            for (var frame = 0; frame < SampleRate * 2; frame++)
            {
                var t = frame / (double)SampleRate;
                var value = (float)(.25 * Math.Sin(2 * Math.PI * 233 * t) + .20 * Math.Sin(2 * Math.PI * 997 * t)
                    + .15 * Math.Sin(2 * Math.PI * 1373 * t) + .10 * Math.Sin(2 * Math.PI * 2287 * t));
                nonharmonic[frame * 2] = nonharmonic[frame * 2 + 1] = value;
            }
            var nonharmonicOutput = Render(nonharmonic, 2, rate, 2048).Samples;
            var nonharmonicLeakage = SpectralLeakage(nonharmonicOutput, 2, 0, [233, 997, 1373, 2287]);
            Require(nonharmonicLeakage < .025, $"{rate}x changed nonharmonic timbre ({nonharmonicLeakage:P3} outside tones)");
            Console.WriteLine($"Audio nonharmonic {rate}x spectral leakage {nonharmonicLeakage:P3}");
            var dynamicInput = DynamicStereo();
            var dynamicAudio = Render(dynamicInput, 2, rate, 2048).Samples;
            var dynamicReference = Render(dynamicInput, 2, rate, 512).Samples;
            Require(dynamicAudio.SequenceEqual(dynamicReference), $"{rate}x changed percussive/chirping stereo audio with read-block size");
            Require(dynamicAudio.All(float.IsFinite) && dynamicAudio.Max(x => Math.Abs(x)) <= dynamicInput.Max(x => Math.Abs(x)) + .001f,
                $"{rate}x added runaway/clipped peaks to bounded percussive stereo audio");
            var mono = Chord(1, differentStereo: false);
            var identical = new float[mono.Length * 2];
            var leftOnly = new float[mono.Length * 2];
            for (var i = 0; i < mono.Length; i++) { identical[i * 2] = identical[i * 2 + 1] = mono[i]; leftOnly[i * 2] = mono[i]; }
            var same = Render(identical, 2, rate, 2048).Samples;
            var one = Render(leftOnly, 2, rate, 2048).Samples;
            for (var i = 0; i < same.Length; i += 2) Require(same[i] == same[i + 1], $"{rate}x changed identical stereo phase/image");
            for (var i = 1; i < one.Length; i += 2) Require(one[i] == 0, $"{rate}x leaked the left channel into a silent right channel");
            var pulse = new float[SampleRate * 2];
            for (var i = 0; i < SampleRate / 25; i++) pulse[i] = (float)(.6 * Math.Sin(2 * Math.PI * 997 * i / SampleRate));
            var head = Render(pulse, 1, rate, 512).Samples;
            var headReference = Render(pulse, 1, rate, 2048).Samples;
            Require(head.SequenceEqual(headReference) && head.Take(Math.Min(head.Length, SampleRate / 5)).Any(x => Math.Abs(x) > .1), $"{rate}x lost the pulse at song start during priming");
        }
        Console.WriteLine("PASS all-rate real DSP duration/EOF, priming preserves onset, read-block invariance, stereo image/silent channel and steady-chord spectral purity");
    }

    private static float[] Chord(int channels, bool differentStereo)
    {
        var samples = new float[SampleRate * 2 * channels];
        for (var frame = 0; frame < samples.Length / channels; frame++)
        {
            var t = frame / (double)SampleRate;
            for (var c = 0; c < channels; c++)
            {
                var baseFrequency = c == 1 && differentStereo ? 550 : 440;
                samples[frame * channels + c] = (float)(.28 * (Math.Sin(2 * Math.PI * baseFrequency * t) + Math.Sin(2 * Math.PI * baseFrequency * 2 * t) + Math.Sin(2 * Math.PI * baseFrequency * 4 * t)));
            }
        }
        return samples;
    }

    private static float[] DynamicStereo()
    {
        var audio = new float[SampleRate * 2 * 2];
        var random = new Random(26013);
        for (var frame = 0; frame < audio.Length / 2; frame++)
        {
            var t = frame / (double)SampleRate;
            // Distinct left/right chirps, kick-like decaying bursts, and deterministic
            // short noise attacks exercise complex high-amplitude sections. Time-domain
            // overlap should preserve the input peak bound; arbitrary music is not
            // assigned a fabricated spectral "quality score".
            var withinBeat = t % .17;
            var attack = Math.Exp(-withinBeat * 90);
            var noise = (random.NextDouble() * 2 - 1) * attack;
            audio[frame * 2] = (float)(.32 * Math.Sin(2 * Math.PI * (440 * t + 170 * t * t)) + .22 * Math.Sin(2 * Math.PI * 997 * t) + .27 * noise);
            audio[frame * 2 + 1] = (float)(.32 * Math.Sin(2 * Math.PI * (550 * t + 230 * t * t)) + .22 * Math.Sin(2 * Math.PI * 1373 * t) - .27 * noise);
        }
        return audio;
    }

    internal static Rendered Render(float[] samples, int channels, double rate, int chunk)
    {
        using var source = new FixtureStream(samples, channels);
        var watch = Stopwatch.StartNew();
        using var pipeline = PreparedMusicPipeline.Build(source, TimeSpan.Zero, rate, true, 1, () => { });
        var prime = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var output = ReadToEnd(pipeline.Volume, pipeline.Ended, chunk);
        return new Rendered(output, prime, watch.Elapsed.TotalMilliseconds);
    }

    private static float[] ReadToEnd(ISampleProvider provider, Func<bool> ended, int chunk)
    {
        var samples = new List<float>(); var buffer = new float[chunk]; var empty = 0;
        for (var reads = 0; reads < 100_000; reads++)
        {
            var read = provider.Read(buffer, 0, buffer.Length);
            if (read > 0) { samples.AddRange(buffer.AsSpan(0, read).ToArray()); empty = 0; }
            else if (++empty >= 8 && ended()) return samples.ToArray();
            else Require(empty < 64, "DSP remained empty without ending");
        }
        throw new InvalidOperationException("DSP exceeded finite fixture read limit");
    }

    private static double SpectralLeakage(float[] audio, int channels, int channel, double[] tones)
    {
        const int length = 8192;
        var frameCount = audio.Length / channels;
        Require(frameCount >= length, "spectral fixture was truncated");
        var start = (frameCount - length) / 2;
        var fft = new Complex[length];
        for (var i = 0; i < length; i++) fft[i].X = (float)(audio[(start + i) * channels + channel] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (length - 1))));
        FastFourierTransform.FFT(true, 13, fft);
        double total = 0, expected = 0;
        for (var bin = 1; bin < length / 2; bin++)
        {
            var energy = fft[bin].X * (double)fft[bin].X + fft[bin].Y * (double)fft[bin].Y;
            total += energy;
            if (tones.Any(tone => Math.Abs(bin - tone * length / SampleRate) <= 2)) expected += energy;
        }
        Require(total > 1e-8, "DSP turned the chord into silence");
        return Math.Max(0, 1 - expected / total);
    }

    private sealed class FixtureStream : WaveStream
    {
        private readonly byte[] bytes;
        private long position;
        public FixtureStream(float[] samples, int channels) { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels); bytes = new byte[samples.Length * sizeof(float)]; Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length); }
        public override WaveFormat WaveFormat { get; }
        public override long Length => bytes.Length;
        public override long Position { get => position; set => position = Math.Clamp(value, 0, Length) / WaveFormat.BlockAlign * WaveFormat.BlockAlign; }
        public override int Read(byte[] buffer, int offset, int count) { var read = (int)Math.Min(count - count % WaveFormat.BlockAlign, Length - position); Buffer.BlockCopy(bytes, (int)position, buffer, offset, read); position += read; return read; }
    }
    internal sealed record Rendered(float[] Samples, double PrimeMs, double RenderMs);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

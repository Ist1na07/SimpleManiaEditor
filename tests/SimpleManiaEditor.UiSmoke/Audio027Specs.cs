using LumaChart.Avalonia.Audio;
using NAudio.Wave;

internal static class Audio027Specs
{
    public static void Run()
    {
        var waveBuffer = new WaveBuffer(64);
        var aliased = waveBuffer.FloatBuffer;
        for (var i = 0; i < 16; i++) aliased[i] = i + 1;
        var silence = new LeadingSilenceSampleProvider(new NAudio.Wave.SampleProviders.SignalGenerator(48000, 1), 3);
        Require(silence.Read(aliased, 2, 3) == 3 && aliased[0] == 1 && aliased[1] == 2 && aliased[2] == 0 && aliased[3] == 0 && aliased[4] == 0 && aliased[5] == 6,
            "search padding corrupted NAudio's byte/float alias or cleared the wrong sample offset");
        const int sr = 48000;
        var input = new float[sr * 3 * 2];
        double[] attacks = [.210, 1, 2];
        foreach (var start in attacks)
            for (var frame = (int)(start * sr); frame < (start + .05) * sr; frame++)
            {
                var t = frame / (double)sr;
                input[frame * 2] = (float)(.4 * Math.Sin(2 * Math.PI * 997 * t));
                input[frame * 2 + 1] = (float)(.4 * Math.Sin(2 * Math.PI * 1134 * t));
            }
        foreach (var rate in new[] { .1, .25, .5, .75, 1, 1.1, 1.5, 2 })
        {
            var audio = Audio026Specs.Render(input, 2, rate, 512).Samples;
            if (rate == .1 && Environment.GetEnvironmentVariable("SME_AUDIO_DIAGNOSTICS") is { Length: > 0 } diagnostic)
            {
                using var writer = new WaveFileWriter(diagnostic, WaveFormat.CreateIeeeFloatWaveFormat(sr, 2));
                writer.WriteSamples(audio, 0, audio.Length);
            }
            Require(audio.SequenceEqual(Audio026Specs.Render(input, 2, rate, 8192).Samples), "quiet-gap processing depends on output block size");
            foreach (var start in attacks)
            {
                var from = (int)((start - .1) / rate * sr);
                var to = Math.Min(audio.Length / 2, (int)((start + .05) / rate * sr));
                var first = -1;
                var longestGap = 0; var gap = 0;
                for (var frame = from; frame < to; frame++)
                {
                    if (Math.Max(Math.Abs(audio[frame * 2]), Math.Abs(audio[frame * 2 + 1])) > .001f)
                    { if (first < 0) first = frame; gap = 0; }
                    else if (frame * rate / sr > start + .005 && frame * rate / sr < start + .035)
                        longestGap = Math.Max(longestGap, ++gap);
                }
                var onset = first * rate * 1000 / sr;
                Require(first >= 0 && Math.Abs(onset - start * 1000) <= 5, $"{rate}x attack at {start * 1000}ms sounded at {onset:F2}ms");
                Require(longestGap * 1000d / sr < 10, $"{rate}x broke a continuous burst into silent repetitions ({longestGap * 1000d / sr:F2}ms)");
                Console.WriteLine($"Audio 0.27 {rate}x pulse {start * 1000:0}ms: onset {onset:F2}ms; longest quiet gap {longestGap * 1000d / sr:F2}ms");
            }
        }
        CheckStereoSpectrum();
    }

    public static void CheckSong(string path)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        using var raw = AudioDecoder.OpenWaveStream(path);
        var buffer = new float[4096]; var source = raw.ToSampleProvider(); long frames = 0; double original = -1;
        while (frames < raw.WaveFormat.SampleRate && original < 0)
        {
            var count = source.Read(buffer, 0, buffer.Length); if (count == 0) break;
            for (var sample = 0; sample < count; sample += raw.WaveFormat.Channels, frames++)
                if (Enumerable.Range(0, raw.WaveFormat.Channels).Any(c => Math.Abs(buffer[sample + c]) > .001f))
                { original = frames * 1000d / raw.WaveFormat.SampleRate; break; }
        }
        Require(original >= 0, "real song fixture did not start in first second");
        foreach (var rate in new[] { .1, .25, .5, .75, 1 })
            foreach (var seek in new[] { 0d, 100 })
            {
                using var stream = AudioDecoder.OpenWaveStream(path);
                using var pipeline = PreparedMusicPipeline.Build(stream, TimeSpan.FromMilliseconds(seek), rate, true, 1, () => { });
                frames = 0; double onset = -1;
                while (frames < stream.WaveFormat.SampleRate / rate && onset < 0)
                {
                    var count = pipeline.Volume.Read(buffer, 0, buffer.Length); if (count == 0) break;
                    for (var sample = 0; sample < count; sample += stream.WaveFormat.Channels, frames++)
                        if (Enumerable.Range(0, stream.WaveFormat.Channels).Any(c => Math.Abs(buffer[sample + c]) > .001f))
                        { onset = seek + frames * rate * 1000 / stream.WaveFormat.SampleRate; break; }
                }
                Require(Math.Abs(onset - original) < 8, $"real MP3 {rate}x seek {seek}: original {original:F2}, output {onset:F2}");
                Console.WriteLine($"Albemuth {rate}x seek {seek:0}ms: raw {original:F2}ms, output {onset:F2}ms");
            }
        Require(hash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))), "read-only song test changed original");
        Console.WriteLine("PASS real-song onset and seek; original SHA256 " + hash);
    }

    private static void CheckStereoSpectrum()
    {
        var root = Path.Combine(Path.GetTempPath(), "sme-spectrum-027-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            AudioSpectrogram Write(bool reversed)
            {
                var path = Path.Combine(root, reversed ? "opposite.wav" : "same.wav");
                using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
                {
                    var samples = new float[96000];
                    for (var i = 0; i < 48000; i++) { var v = (float)(.5 * Math.Sin(2 * Math.PI * 997 * i / 48000)); samples[i * 2] = v; samples[i * 2 + 1] = reversed ? -v : v; }
                    writer.WriteSamples(samples, 0, samples.Length);
                }
                return AudioWaveformService.LoadVisualizationAsync(path, CancellationToken.None).GetAwaiter().GetResult()!.Spectrogram!;
            }
            var same = Write(false); var opposite = Write(true);
            Require(same.Intensities.SequenceEqual(opposite.Intensities), "audible antiphase stereo still vanishes from spectrogram");
            Require(same.Intensities.Max() > 0, "stereo spectrum was empty");
            Console.WriteLine("PASS same/antiphase stereo have identical spectrum power");
        }
        finally { Directory.Delete(root, true); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

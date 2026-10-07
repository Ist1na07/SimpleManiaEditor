using NAudio.Wave;

namespace LumaChart.Avalonia.Specs;

/// <summary>
/// Android and iOS have no Windows ACM codec, so NAudio's default MP3
/// decompressor fails there and takes playback, waveform/spectrum, BPM
/// analysis, Key sounds and export transcoding down with it for any MP3 chart.
/// The shared decoder now supplies a managed decompressor off Windows.
///
/// These checks run on Windows but exercise the managed decompressor directly,
/// so the path the mobile hosts depend on is covered by the desktop suite.
/// </summary>
internal static class WindowsManagedMp3DecoderSpecs
{
    private const string Mp3FixturePath = @"D:\osu!\Songs\_song_32065\0\1696998774.ogg";
    private const string Mp3FixtureSha256 =
        "5242B47E309FB13A89DCE5E693FD8F466D4D924A902839D51B969144466E6057";

    public static IReadOnlyList<WindowsManagedMp3DecoderSpecResult> Run()
    {
        var results = new List<WindowsManagedMp3DecoderSpecResult>();
        Check(results, "managed-mp3-decoder-is-available-without-windows-acm", VerifyManagedDecoderOpens);
        Check(results, "managed-mp3-decoder-matches-the-acm-baseline", VerifyManagedMatchesAcm);
        Check(results, "managed-mp3-decoder-produces-audible-samples", VerifyManagedIsNotSilent);
        return results;
    }

    /// <summary>
    /// The fixture is an ID3-backed MP3 that carries a misleading .ogg
    /// extension, so it also keeps the container sniffing honest.
    /// </summary>
    private static string RequireFixture()
    {
        if (!File.Exists(Mp3FixturePath))
            throw new FileNotFoundException("MP3 regression fixture is missing.", Mp3FixturePath);
        using var stream = File.OpenRead(Mp3FixturePath);
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        if (!string.Equals(actual, Mp3FixtureSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MP3 fixture changed: {actual}");
        return Mp3FixturePath;
    }

    private static WaveStream OpenManaged(string path) =>
        new Mp3FileReaderBase(
            path,
            waveFormat => new NLayer.NAudioSupport.Mp3FrameDecompressor(waveFormat));

    private static string VerifyManagedDecoderOpens()
    {
        var path = RequireFixture();
        using var managed = OpenManaged(path);
        Require(managed.WaveFormat.SampleRate > 0, "The managed decoder reported no sample rate.");
        Require(managed.WaveFormat.Channels > 0, "The managed decoder reported no channels.");
        Require(managed.TotalTime > TimeSpan.FromSeconds(1), "The managed decoder reported no length.");
        return $"SampleRate={managed.WaveFormat.SampleRate}; Channels={managed.WaveFormat.Channels}; " +
               $"Seconds={managed.TotalTime.TotalSeconds:F3}";
    }

    private static string VerifyManagedMatchesAcm()
    {
        var path = RequireFixture();
        using var acm = new Mp3FileReader(path);
        using var managed = OpenManaged(path);

        Require(
            acm.WaveFormat.SampleRate == managed.WaveFormat.SampleRate,
            $"Sample rate differs: ACM={acm.WaveFormat.SampleRate}, managed={managed.WaveFormat.SampleRate}.");
        Require(
            acm.WaveFormat.Channels == managed.WaveFormat.Channels,
            $"Channel count differs: ACM={acm.WaveFormat.Channels}, managed={managed.WaveFormat.Channels}.");

        // Decoders legitimately differ by a frame of encoder delay, so compare
        // duration with a tolerance rather than demanding identical lengths.
        var difference = Math.Abs((acm.TotalTime - managed.TotalTime).TotalMilliseconds);
        Require(
            difference <= 60,
            $"Duration differs by {difference:F1} ms, beyond one MP3 frame of tolerance.");

        return $"SampleRate={acm.WaveFormat.SampleRate}; Channels={acm.WaveFormat.Channels}; " +
               $"AcmSeconds={acm.TotalTime.TotalSeconds:F3}; ManagedSeconds={managed.TotalTime.TotalSeconds:F3}; " +
               $"DeltaMs={difference:F1}";
    }

    private static string VerifyManagedIsNotSilent()
    {
        var path = RequireFixture();
        using var managed = OpenManaged(path);
        var samples = managed.ToSampleProvider();
        var buffer = new float[managed.WaveFormat.SampleRate * managed.WaveFormat.Channels];

        var peak = 0f;
        double energy = 0;
        var total = 0;
        // Skip the leading silence many encoders emit before the first beat.
        for (var block = 0; block < 12; block++)
        {
            var read = samples.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                Require(float.IsFinite(value), "The managed decoder produced a non-finite sample.");
                peak = Math.Max(peak, Math.Abs(value));
                energy += (double)value * value;
            }
            total += read;
        }

        Require(total > 0, "The managed decoder returned no samples.");
        var rms = Math.Sqrt(energy / total);
        Require(peak > 0.01f, $"The managed decoder produced near-silence; peak={peak:F4}.");
        Require(peak <= 1.5f, $"The managed decoder produced out-of-range samples; peak={peak:F4}.");
        return $"Samples={total}; Peak={peak:F4}; Rms={rms:F4}";
    }

    private static void Check(
        List<WindowsManagedMp3DecoderSpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsManagedMp3DecoderSpecResult(name, true, verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsManagedMp3DecoderSpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsManagedMp3DecoderSpecResult(
    string Name,
    bool Passed,
    string Detail);

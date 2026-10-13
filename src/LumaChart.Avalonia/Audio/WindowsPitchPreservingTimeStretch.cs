using NAudio.Wave;
using SoundTouch;

namespace LumaChart.Avalonia.Audio;

internal static class WindowsPitchPreservingTimeStretchPolicy
{
    // Short, stereo-linked grains limit the temporal movement of attacks. The
    // former 100ms grains repeated percussion and advanced it by ~90ms at .1x.
    public const int SequenceDurationMilliseconds = 24;
    public const int SeekWindowDurationMilliseconds = 28;
    public const int OverlapDurationMilliseconds = 12;
    public const int MaximumReadAttemptsPerDevicePeriod = 4;

    public static double ResolveSoundTouchTempo(double playbackRate) =>
        double.IsFinite(playbackRate) ? Math.Clamp(playbackRate, .1, 2) : 1;

    public static void Configure(SoundTouchProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        processor.SetSetting(SettingId.UseQuickSeek, 0);
        processor.SetSetting(SettingId.SequenceDurationMs, SequenceDurationMilliseconds);
        processor.SetSetting(SettingId.SeekWindowDurationMs, SeekWindowDurationMilliseconds);
        processor.SetSetting(SettingId.OverlapDurationMs, OverlapDurationMilliseconds);
    }
}

/// <summary>
/// Keeps transient empty SoundTouch reads from being interpreted as end-of-stream
/// without decoding a large future block on the real-time WASAPI callback.
/// </summary>
internal sealed class SoundTouchContinuitySampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly Func<bool> sourceReallyEnded;
    private readonly int maximumReadAttemptsPerDevicePeriod;
    private bool sourceEnded;

    public SoundTouchContinuitySampleProvider(
        ISampleProvider source,
        Func<bool> sourceReallyEnded,
        int maximumReadAttemptsPerDevicePeriod =
            WindowsPitchPreservingTimeStretchPolicy.MaximumReadAttemptsPerDevicePeriod)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceReallyEnded);
        if (maximumReadAttemptsPerDevicePeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumReadAttemptsPerDevicePeriod));

        this.source = source;
        this.sourceReallyEnded = sourceReallyEnded;
        this.maximumReadAttemptsPerDevicePeriod = maximumReadAttemptsPerDevicePeriod;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public bool IsEnded => sourceEnded;

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (count == 0) return 0;

        var written = 0;
        for (var attempt = 0;
             attempt < maximumReadAttemptsPerDevicePeriod &&
             written < count &&
             !sourceEnded;
             attempt++)
        {
            var read = source.Read(buffer, offset + written, count - written);
            if (read <= 0)
            {
                if (sourceReallyEnded()) sourceEnded = true;
                continue;
            }

            written += read;
        }
        return written;
    }
}

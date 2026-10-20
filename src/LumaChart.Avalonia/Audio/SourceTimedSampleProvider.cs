using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LumaChart.Avalonia.Audio;

/// <summary>Absolute source-time scheduling metadata. The device mixer resolves it on its sample cursor, not UI time.</summary>
internal sealed class SourceTimedSampleProvider(ISampleProvider source, double sourceMilliseconds, double fallbackDelayMilliseconds) : ISampleProvider
{
    private ISampleProvider? fallback;
    public ISampleProvider Source => source;
    public double SourceMilliseconds => sourceMilliseconds;
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        fallback ??= fallbackDelayMilliseconds <= 0 ? source : new OffsetSampleProvider(source) { DelayBy = TimeSpan.FromMilliseconds(Math.Min(fallbackDelayMilliseconds, 2000)) };
        return fallback.Read(buffer, offset, count);
    }
}

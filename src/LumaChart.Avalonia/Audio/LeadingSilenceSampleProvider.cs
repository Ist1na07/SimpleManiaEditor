using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>Centres the slow WSOLA correlation search without moving the original PCM timeline.</summary>
internal sealed class LeadingSilenceSampleProvider(ISampleProvider source, int frames) : ISampleProvider
{
    private int remaining = checked(frames * source.WaveFormat.Channels);
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        var silence = Math.Min(remaining, count);
        // NAudio's WaveBuffer may expose an aliased byte[] as float[]. Array
        // APIs then count bytes; typed writes keep float offsets correct.
        for (var sample = 0; sample < silence; sample++) buffer[offset + sample] = 0;
        remaining -= silence;
        return silence + (silence < count ? source.Read(buffer, offset + silence, count - silence) : 0);
    }
}

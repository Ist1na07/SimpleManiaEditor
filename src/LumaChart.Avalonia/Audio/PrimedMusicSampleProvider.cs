using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>Retains precomputed startup audio without advancing the audible song onset.</summary>
internal sealed class PrimedMusicSampleProvider(ISampleProvider source, Func<bool> sourceEnded) : ISampleProvider
{
    private readonly float[] startup = new float[
        Math.Max(1, source.WaveFormat.SampleRate / 50) * source.WaveFormat.Channels];
    private int available;
    private int consumed;

    public WaveFormat WaveFormat => source.WaveFormat;
    public bool IsEnded => consumed >= available && sourceEnded();

    public void Prime() => available = source.Read(startup, 0, startup.Length);

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        count -= count % WaveFormat.Channels;
        var retained = Math.Min(count, available - consumed);
        if (retained > 0)
        {
            // NAudio's WaveBuffer exposes a float[] view over a byte[] on the
            // device path; Array.Copy rejects its runtime array type. Copy raw
            // PCM bytes so real outputs and ordinary test arrays behave alike.
            Buffer.BlockCopy(startup, consumed * sizeof(float), buffer, offset * sizeof(float), retained * sizeof(float));
            consumed += retained;
        }
        return retained + (retained < count ? source.Read(buffer, offset + retained, count - retained) : 0);
    }
}

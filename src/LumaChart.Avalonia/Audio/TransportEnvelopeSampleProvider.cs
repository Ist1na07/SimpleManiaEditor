using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>Sample-clock fade at transport boundaries, applied after music and keysound mixing.</summary>
internal sealed class TransportEnvelopeSampleProvider : ISampleProvider
{
    public const double FadeMilliseconds = 5;
    private readonly ISampleProvider source;
    private readonly float step;
    private float gain;
    private int target;
    private int reset;
    private TaskCompletionSource zeroReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TransportEnvelopeSampleProvider(ISampleProvider source)
    {
        this.source = source;
        step = 1f / Math.Max(1, (int)Math.Round(source.WaveFormat.SampleRate * FadeMilliseconds / 1000));
        zeroReached.SetResult();
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public void FadeIn()
    {
        Interlocked.Exchange(ref reset, 1);
        Volatile.Write(ref target, 1);
    }

    public Task FadeOut()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref zeroReached, completion);
        Volatile.Write(ref target, 0);
        return completion.Task;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (Interlocked.Exchange(ref reset, 0) != 0) gain = 0;
        var channels = WaveFormat.Channels;
        for (var i = offset; i < offset + read; i += channels)
        {
            for (var channel = 0; channel < channels && i + channel < offset + read; channel++)
                buffer[i + channel] *= gain;
            var destination = Volatile.Read(ref target);
            gain = destination == 0 ? Math.Max(0, gain - step) : Math.Min(1, gain + step);
        }
        if (read == 0 && count > 0 || Volatile.Read(ref target) == 0 && gain <= 0)
            Volatile.Read(ref zeroReached).TrySetResult();
        return read;
    }
}

using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>Sample-clock fade at transport boundaries, applied after music and keysound mixing.</summary>
internal sealed class TransportEnvelopeSampleProvider : ISampleProvider
{
    public const double FadeMilliseconds = 5;
    private readonly ISampleProvider source;
    private readonly float step;
    private readonly object readGate = new();
    private readonly float[] lastFrame;
    private readonly float[] spliceFrame;
    private readonly int spliceFrames;
    private int spliceRemaining;
    private float gain;
    private int target;
    private int reset;
    private TaskCompletionSource zeroReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TransportEnvelopeSampleProvider(ISampleProvider source)
    {
        this.source = source;
        step = 1f / Math.Max(1, (int)Math.Round(source.WaveFormat.SampleRate * FadeMilliseconds / 1000));
        lastFrame = new float[source.WaveFormat.Channels];
        spliceFrame = new float[lastFrame.Length];
        spliceFrames = Math.Max(1, source.WaveFormat.SampleRate * 3 / 1000);
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
        lock (readGate) return ReadLocked(buffer, offset, count);
    }

    /// <summary>Changes decoder/DSP state only between reads; keeps the device stream alive.</summary>
    public void ChangeSource(Action mutation)
    {
        lock (readGate)
        {
            mutation();
            Array.Copy(lastFrame, spliceFrame, lastFrame.Length);
            spliceRemaining = spliceFrames;
        }
    }

    private int ReadLocked(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (Interlocked.Exchange(ref reset, 0) != 0) gain = 0;
        var channels = WaveFormat.Channels;
        for (var i = offset; i < offset + read; i += channels)
        {
            var blend = spliceRemaining > 0 ? (spliceFrames - spliceRemaining + 1f) / spliceFrames : 1f;
            for (var channel = 0; channel < channels && i + channel < offset + read; channel++)
            {
                var sample = buffer[i + channel] * gain;
                if (spliceRemaining > 0) sample = spliceFrame[channel] * (1 - blend) + sample * blend;
                buffer[i + channel] = sample;
                lastFrame[channel] = sample;
            }
            if (spliceRemaining > 0) spliceRemaining--;
            var destination = Volatile.Read(ref target);
            gain = destination == 0 ? Math.Max(0, gain - step) : Math.Min(1, gain + step);
        }
        if (read == 0 && count > 0 || Volatile.Read(ref target) == 0 && gain <= 0)
            Volatile.Read(ref zeroReached).TrySetResult();
        return read;
    }
}

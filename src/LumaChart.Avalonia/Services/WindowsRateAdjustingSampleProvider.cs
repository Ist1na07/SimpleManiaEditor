using NAudio.Wave;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Streaming linear-interpolation rate control. Speed and pitch intentionally
/// change together, matching the WPF 1.1.0 non-preserve-pitch export path.
/// </summary>
internal sealed class WindowsRateAdjustingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly int channels;
    private float[] buffer;
    private int bufferedFrames;
    private double framePosition;
    private double logicalFrames;
    private bool sourceEnded;
    private double playbackRate = 1;

    public WindowsRateAdjustingSampleProvider(ISampleProvider source)
    {
        this.source = source;
        channels = source.WaveFormat.Channels;
        WaveFormat = source.WaveFormat;
        // Every read below asks the source for whole frames only. 16384 is not
        // a multiple of three, five or six channels: WaveFileReader rejects a
        // request that splits a block, and a decoder that honours it leaves a
        // partial frame behind that shifts every later frame by a channel.
        buffer = new float[16_384 - 16_384 % channels];
    }

    public WaveFormat WaveFormat { get; }

    public double PlaybackRate
    {
        get => playbackRate;
        set => playbackRate = Math.Clamp(value, .1, 4);
    }

    public TimeSpan LogicalPosition =>
        TimeSpan.FromSeconds(logicalFrames / WaveFormat.SampleRate);

    public void Reset(TimeSpan sourcePosition)
    {
        bufferedFrames = 0;
        framePosition = 0;
        logicalFrames = Math.Max(0, sourcePosition.TotalSeconds * WaveFormat.SampleRate);
        sourceEnded = false;
    }

    public int Read(float[] output, int offset, int count)
    {
        var requestedFrames = count / channels;
        if (requestedFrames <= 0) return 0;
        if (sourceEnded) return 0;

        if (Math.Abs(playbackRate - 1) < .000001 &&
            bufferedFrames == 0 &&
            Math.Abs(framePosition) < .000001)
        {
            var read = source.Read(output, offset, requestedFrames * channels);
            logicalFrames += read / channels;
            if (read <= 0) sourceEnded = true;
            return read;
        }

        EnsureCapacity((int)Math.Ceiling(framePosition + requestedFrames * playbackRate + 2));

        var writtenFrames = 0;
        for (; writtenFrames < requestedFrames; writtenFrames++)
        {
            var leftFrame = (int)Math.Floor(framePosition);
            if (!EnsureFrame(leftFrame)) break;
            var rightFrame = leftFrame + 1;
            EnsureFrame(rightFrame);
            rightFrame = Math.Min(rightFrame, bufferedFrames - 1);
            var fraction = framePosition - leftFrame;
            var outputBase = offset + writtenFrames * channels;
            var leftBase = leftFrame * channels;
            var rightBase = rightFrame * channels;
            for (var channel = 0; channel < channels; channel++)
            {
                var left = buffer[leftBase + channel];
                output[outputBase + channel] =
                    left + (buffer[rightBase + channel] - left) * (float)fraction;
            }

            framePosition += playbackRate;
            logicalFrames += playbackRate;
        }

        Compact();
        return writtenFrames * channels;
    }

    private bool EnsureFrame(int requiredFrame)
    {
        while (bufferedFrames <= requiredFrame && !sourceEnded)
        {
            EnsureCapacity(bufferedFrames + 4_096);
            var sampleOffset = bufferedFrames * channels;
            var freeSamples = buffer.Length - sampleOffset;
            var read = source.Read(buffer, sampleOffset, freeSamples - freeSamples % channels);
            if (read <= 0)
            {
                sourceEnded = true;
                break;
            }

            bufferedFrames += read / channels;
        }

        return requiredFrame < bufferedFrames;
    }

    private void EnsureCapacity(int requiredFrames)
    {
        var requiredSamples = Math.Max(channels * 2, requiredFrames * channels);
        if (requiredSamples <= buffer.Length) return;
        var grownSamples = Math.Max(requiredSamples, buffer.Length * 2);
        Array.Resize(ref buffer, grownSamples - grownSamples % channels);
    }

    private void Compact()
    {
        var consumedFrames = Math.Min((int)Math.Floor(framePosition), bufferedFrames);
        if (consumedFrames <= 0) return;
        var remainingFrames = bufferedFrames - consumedFrames;
        if (remainingFrames > 0)
        {
            Array.Copy(
                buffer,
                consumedFrames * channels,
                buffer,
                0,
                remainingFrames * channels);
        }

        bufferedFrames = remainingFrames;
        framePosition -= consumedFrames;
    }
}

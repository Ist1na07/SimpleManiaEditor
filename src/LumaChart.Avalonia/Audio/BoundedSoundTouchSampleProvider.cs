using NAudio.Wave;
using SoundTouch;

namespace LumaChart.Avalonia.Audio;

/// <summary>
/// Feeds a <see cref="SoundTouchProcessor"/> only as much source as the caller
/// asked for. <see cref="SoundTouch.Net.NAudioSupport.SoundTouchWaveProvider"/>
/// fills its own 512-frame (10.7 ms) input buffer; at 0.1x a single such buffer
/// becomes roughly 107 ms of output, i.e. about 15 ms of grain work executed
/// inside one 5-10 ms device callback. Deriving the chunk from the request
/// keeps the work per callback proportional to the audio that callback
/// delivers, so the slow rates stop arriving as periodic deadline misses.
/// </summary>
internal sealed class BoundedSoundTouchSampleProvider : ISampleProvider
{
    // SoundTouch needs a little input to keep its grain pipeline primed. The
    // upper bound only guards against a pathological request; a normal device
    // period never comes near it.
    private const int MinimumChunkFrames = 16;
    private const int MaximumChunkFrames = 4096;

    private readonly ISampleProvider source;
    private readonly int channels;
    private readonly float[] input;
    private bool sourceEnded;
    private bool flushed;

    public BoundedSoundTouchSampleProvider(
        ISampleProvider source,
        SoundTouchProcessor processor,
        double sourceFramesPerOutputFrame)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(processor);
        this.source = source;
        Processor = processor;
        WaveFormat = source.WaveFormat;
        channels = source.WaveFormat.Channels;
        input = new float[MaximumChunkFrames * Math.Max(2, channels)];
        SourceFramesPerOutputFrame = sourceFramesPerOutputFrame;
    }

    public SoundTouchProcessor Processor { get; }
    public WaveFormat WaveFormat { get; }
    /// <summary>Source frames consumed per output frame (the stretch rate).</summary>
    public double SourceFramesPerOutputFrame { get; }
    public bool IsEnded => sourceEnded && flushed && Processor.AvailableSamples == 0;

    public int Read(float[] buffer, int offset, int count)
    {
        count -= count % channels;
        if (count <= 0) return 0;
        var written = 0;
        while (written < count)
        {
            var available = Processor.AvailableSamples;
            if (available > 0)
            {
                var take = Math.Min(available, (count - written) / channels);
                var received = Processor.ReceiveSamples(
                    buffer.AsSpan(offset + written, take * channels), take);
                if (received <= 0) break;
                written += received * channels;
                continue;
            }
            if (sourceEnded)
            {
                if (flushed) break;
                flushed = true;
                Processor.Flush();
                continue;
            }
            Feed((count - written) / channels);
        }
        return written;
    }

    private void Feed(int stillNeededFrames)
    {
        var wanted = (int)Math.Ceiling(stillNeededFrames * SourceFramesPerOutputFrame) + 8;
        wanted = Math.Clamp(wanted, MinimumChunkFrames, MaximumChunkFrames);
        var samples = wanted * channels;
        var read = source.Read(input, 0, samples);
        if (read <= 0)
        {
            sourceEnded = true;
            return;
        }
        read -= read % channels;
        if (read == 0) return;
        Processor.PutSamples(input.AsSpan(0, read), read / channels);
    }
}

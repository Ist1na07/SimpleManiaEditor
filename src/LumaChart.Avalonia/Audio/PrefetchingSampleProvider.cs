using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>
/// Moves the DSP and decoding off the render callback. A dedicated producer
/// thread pulls from the prepared pipeline into a 1.4 s ring; the callback only
/// copies samples out of it. Before this, every device read ran SoundTouch and
/// the decoder inside the callback, so the cost depended on the playback rate
/// (a 5 ms block at 0.1x needed 0.1x-worth of grain work plus a full MP3 frame
/// decode) and occasionally missed the device period.
/// </summary>
internal sealed class PrefetchingSampleProvider : ISampleProvider, IDisposable
{
    // Power of two so the ring index is a mask. 65536 frames at 48 kHz = 1.37 s.
    private const int Capacity = 1 << 16;
    private const int Mask = Capacity - 1;
    // Keep the producer request the size the DSP chain has always been tested
    // with; larger single requests expose rounding stalls inside the segment
    // budget loop at extreme slow rates.
    private const int ProducerChunkFrames = 1024;
    private const double PrimeSeconds = .20;

    private readonly ISampleProvider source;
    private readonly int channels;
    private readonly float[] ring;
    private readonly float[] producerBuffer;
    private readonly Thread thread;
    private readonly CancellationTokenSource stop = new();
    private readonly object producerGate = new();
    private long written;
    private long read;
    private long underrunFrames;
    private volatile bool sourceEnded;
    private volatile bool disposed;

    public PrefetchingSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        WaveFormat = source.WaveFormat;
        channels = source.WaveFormat.Channels;
        ring = new float[Capacity * channels];
        producerBuffer = new float[ProducerChunkFrames * channels];
        thread = new Thread(ProduceLoop)
        {
            IsBackground = true,
            Name = "audio-stretch-producer",
            Priority = ThreadPriority.AboveNormal
        };
        thread.Start();
        // Prime a little so the first callback cannot underrun. A slow first
        // decode only makes this one wait longer; it never runs on the device.
        var deadline = Environment.TickCount64 + 2000;
        while (Volatile.Read(ref written) < (long)(WaveFormat.SampleRate * PrimeSeconds) &&
               !sourceEnded && Environment.TickCount64 < deadline && !disposed)
            Thread.Sleep(1);
    }

    public WaveFormat WaveFormat { get; }
    /// <summary>Frames a reader had to fill with silence because the producer was behind.</summary>
    public long UnderrunFrames => Interlocked.Read(ref underrunFrames);
    public bool IsEnded => sourceEnded && Volatile.Read(ref read) >= Volatile.Read(ref written);

    private void ProduceLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var head = Interlocked.Read(ref written);
                var tail = Interlocked.Read(ref read);
                if (Capacity - (int)(head - tail) <= 0)
                {
                    stop.Token.WaitHandle.WaitOne(2);
                    continue;
                }
                if (!ProduceOnce()) return;
            }
        }
        catch (Exception)
        {
            // A decoder failure upstream ends the stream; the pipeline's ended
            // predicate and PlaybackStopped handle the user-visible side.
            sourceEnded = true;
        }
    }

    /// <summary>Produces one bounded chunk. Returns false when the source ended.</summary>
    private bool ProduceOnce()
    {
        lock (producerGate)
        {
            if (sourceEnded) return false;
            var head = Interlocked.Read(ref written);
            var tail = Interlocked.Read(ref read);
            var free = Capacity - (int)(head - tail);
            if (free <= 0) return true;
            var wantFrames = Math.Min(free, ProducerChunkFrames);
            var got = source.Read(producerBuffer, 0, wantFrames * channels);
            if (got <= 0)
            {
                sourceEnded = true;
                return false;
            }
            got -= got % channels;
            var frames = got / channels;
            for (var f = 0; f < frames; f++)
                Array.Copy(producerBuffer, f * channels, ring, (int)((head + f) & Mask) * channels, channels);
            Interlocked.Exchange(ref written, head + frames);
            return true;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        count -= count % channels;
        if (count <= 0) return 0;
        var wantFrames = count / channels;
        var filled = 0;
        while (filled < wantFrames)
        {
            var tail = Volatile.Read(ref read);
            var available = (int)(Interlocked.Read(ref written) - tail);
            if (available <= 0)
            {
                if (sourceEnded) break;
                // Non-realtime readers (offline render, seek preparation, tests)
                // can outrun the producer; produce a chunk on this thread so such
                // a reader never gets silence the DSP chain did not emit. A paced
                // device always has the ring full and never reaches this branch.
                if (!ProduceOnce() && Volatile.Read(ref read) >= Interlocked.Read(ref written)) break;
                continue;
            }
            var take = Math.Min(wantFrames - filled, available);
            // NAudio's WaveBuffer hands out a float[] view over a byte[]; Array.Copy
            // rejects its runtime type, so copy the raw PCM bytes (same reason as
            // PrimedMusicSampleProvider).
            var firstSlot = (int)(tail & Mask) * channels;
            var remainingFrames = take;
            var destination = offset + filled * channels;
            while (remainingFrames > 0)
            {
                var run = Math.Min(remainingFrames, Capacity - firstSlot / channels);
                Buffer.BlockCopy(ring, firstSlot * sizeof(float), buffer, destination * sizeof(float), run * channels * sizeof(float));
                destination += run * channels;
                remainingFrames -= run;
                firstSlot = 0;
            }
            Volatile.Write(ref read, tail + take);
            filled += take;
        }
        if (filled == wantFrames) return count;
        if (sourceEnded) return filled * channels;
        var missingFrames = wantFrames - filled;
        Array.Clear(buffer, offset + filled * channels, missingFrames * channels);
        Interlocked.Add(ref underrunFrames, missingFrames);
        return count;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        try { thread.Join(500); } catch { }
        stop.Dispose();
        if (source is IDisposable disposable)
            try { disposable.Dispose(); } catch { }
    }
}

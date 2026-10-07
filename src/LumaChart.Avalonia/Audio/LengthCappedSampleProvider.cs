using System.Diagnostics;
using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>
/// Prevents decoders from receiving an extra read after their WaveStream has
/// already advertised an exact decoded PCM length. Some valid-in-practice OGG
/// files make NVorbis spin in that final EOF probe even though every declared
/// sample was returned successfully.
/// </summary>
internal sealed class LengthCappedSampleProvider : ISampleProvider
{
    // The opposite failure: a truncated file, or a header that declares more
    // audio than the stream holds, runs the decoder dry before the declared
    // length. Nothing downstream would ever see the end, so playback stayed on
    // an endless stretch of silence. One empty read is not proof — pipelines
    // probe again within the same device period — so the end is only declared
    // once the decoder has stayed empty across several reads and a span of
    // wall-clock time no healthy decoder needs to produce its next block.
    internal const int PrematureEndZeroReads = 3;
    internal static readonly TimeSpan PrematureEndStallTime = TimeSpan.FromMilliseconds(250);

    private readonly ISampleProvider source;
    private long remainingSamples;
    private int consecutiveZeroReads;
    private long firstZeroReadTimestamp;
    private int prematureEnd;

    public LengthCappedSampleProvider(
        ISampleProvider source,
        long remainingSamples)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        this.remainingSamples = Math.Max(0, remainingSamples);
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public bool IsEnded =>
        Interlocked.Read(ref remainingSamples) <= 0 || Volatile.Read(ref prematureEnd) != 0;
    public long RemainingSamples => Math.Max(0, Interlocked.Read(ref remainingSamples));

    /// <summary>
    /// Whether the decoder ran out before the declared length and stayed out.
    /// </summary>
    public bool EndedBeforeDeclaredLength => Volatile.Read(ref prematureEnd) != 0;

    public static LengthCappedSampleProvider FromWaveStream(WaveStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var provider = stream.ToSampleProvider();
        var blockAlign = Math.Max(1, stream.WaveFormat.BlockAlign);
        var remainingFrames = Math.Max(0, stream.Length - stream.Position) / blockAlign;
        var remainingSamples = checked(
            remainingFrames * provider.WaveFormat.Channels);
        return new LengthCappedSampleProvider(provider, remainingSamples);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var available = Interlocked.Read(ref remainingSamples);
        if (available <= 0 || count <= 0 || Volatile.Read(ref prematureEnd) != 0) return 0;
        var requested = (int)Math.Min(count, available);
        var read = source.Read(buffer, offset, requested);
        if (read > 0)
        {
            Interlocked.Add(ref remainingSamples, -Math.Min(read, available));
            consecutiveZeroReads = 0;
            return read;
        }

        var now = Stopwatch.GetTimestamp();
        if (consecutiveZeroReads++ == 0) firstZeroReadTimestamp = now;
        if (consecutiveZeroReads >= PrematureEndZeroReads &&
            Stopwatch.GetElapsedTime(firstZeroReadTimestamp, now) >= PrematureEndStallTime)
            Volatile.Write(ref prematureEnd, 1);
        return read;
    }
}

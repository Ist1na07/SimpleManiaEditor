using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>
/// Monotonic identity-bound generation used to invalidate samples that belong to
/// an older transport timeline. A token from another playback service can never
/// validate against this owner, even when the numeric generations happen to match.
/// </summary>
internal sealed class AudioPlaybackEpoch
{
    private long generation = 1;

    public AudioPlaybackEpochToken Capture() => new(this, Volatile.Read(ref generation));

    public AudioPlaybackEpochToken Advance() =>
        new(this, Interlocked.Increment(ref generation));

    public bool IsCurrent(AudioPlaybackEpochToken token) =>
        ReferenceEquals(token.Owner, this) &&
        token.Generation == Volatile.Read(ref generation);
}

internal readonly struct AudioPlaybackEpochToken : IEquatable<AudioPlaybackEpochToken>
{
    internal AudioPlaybackEpochToken(AudioPlaybackEpoch owner, long generation)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Generation = generation;
    }

    internal AudioPlaybackEpoch? Owner { get; }
    internal long Generation { get; }
    internal bool IsCurrent => Owner?.IsCurrent(this) == true;

    public bool Equals(AudioPlaybackEpochToken other) =>
        ReferenceEquals(Owner, other.Owner) && Generation == other.Generation;

    public override bool Equals(object? obj) =>
        obj is AudioPlaybackEpochToken other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Owner, Generation);

    public static bool operator ==(
        AudioPlaybackEpochToken left,
        AudioPlaybackEpochToken right) => left.Equals(right);

    public static bool operator !=(
        AudioPlaybackEpochToken left,
        AudioPlaybackEpochToken right) => !left.Equals(right);
}

/// <summary>
/// Outermost playback-bound provider guard. The second check closes the race in
/// which a transport transition occurs while an inner provider is filling the
/// render buffer. Invalid data is explicitly cleared before returning zero.
/// </summary>
internal sealed class EpochGuardSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly AudioPlaybackEpochToken token;

    public EpochGuardSampleProvider(
        ISampleProvider source,
        AudioPlaybackEpochToken token)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.token = token;
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The requested sample range is outside the destination buffer.");

        if (!token.IsCurrent)
        {
            Array.Clear(buffer, offset, count);
            return 0;
        }

        var read = source.Read(buffer, offset, count);
        if (read < 0 || read > count)
            throw new InvalidOperationException("The sample provider returned an invalid sample count.");
        if (token.IsCurrent) return read;

        Array.Clear(buffer, offset, count);
        return 0;
    }
}

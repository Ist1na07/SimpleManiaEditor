namespace LumaChart.Avalonia.Audio;

internal static class WindowsKeySoundSoftLimiter
{
    internal const float Threshold = .90f;
    private const float MaximumMagnitude = .9999f;

    internal static float Limit(float sample)
    {
        if (!float.IsFinite(sample)) return 0;
        var magnitude = MathF.Abs(sample);
        if (magnitude <= Threshold) return sample;
        var knee = 1f - Threshold;
        var limited = Threshold +
                      knee * (1f - MathF.Exp(-(magnitude - Threshold) / knee));
        return MathF.CopySign(MathF.Min(limited, MaximumMagnitude), sample);
    }

    internal static void Process(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        var end = offset + count;
        for (var index = offset; index < end; index++)
            if (!float.IsFinite(buffer[index]) || MathF.Abs(buffer[index]) > Threshold)
                buffer[index] = Limit(buffer[index]);
    }
}

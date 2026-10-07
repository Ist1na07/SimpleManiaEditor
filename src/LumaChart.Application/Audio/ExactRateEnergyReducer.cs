namespace LumaChart.Application.Audio;

/// <summary>
/// Reduces interleaved source-rate samples to a mono RMS energy envelope without
/// truncating a fractional source/analysis-rate ratio. For example, 44.1 kHz to
/// 200 Hz uses alternating 221/220-frame buckets instead of silently running at
/// 200.4545 Hz. Incomplete channel groups are retained across decoder reads.
/// </summary>
public sealed class ExactRateEnergyReducer
{
    private readonly int sourceRate;
    private readonly int targetRate;
    private readonly int channels;
    private long phase;
    private double channelSum;
    private int channelSamples;
    private double accumulatedSquares;
    private int accumulatedSamples;

    public ExactRateEnergyReducer(int sourceRate, int targetRate, int channels = 1)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (targetRate <= 0 || targetRate > sourceRate)
            throw new ArgumentOutOfRangeException(nameof(targetRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        this.sourceRate = sourceRate;
        this.targetRate = targetRate;
        this.channels = channels;
    }

    public bool AddSample(double channelSample, out double energy)
    {
        if (!double.IsFinite(channelSample)) channelSample = 0;
        channelSum += channelSample;
        channelSamples++;
        if (channelSamples < channels)
        {
            energy = 0;
            return false;
        }

        var monoSample = channelSum / channels;
        channelSum = 0;
        channelSamples = 0;
        accumulatedSquares += monoSample * monoSample;
        accumulatedSamples++;
        phase += targetRate;
        if (phase < sourceRate)
        {
            energy = 0;
            return false;
        }

        phase -= sourceRate;
        energy = Math.Sqrt(accumulatedSquares / accumulatedSamples);
        accumulatedSquares = 0;
        accumulatedSamples = 0;
        return true;
    }
}

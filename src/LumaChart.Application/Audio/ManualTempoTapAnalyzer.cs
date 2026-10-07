using LumaChart.Core.Localization;

namespace LumaChart.Application.Audio;

/// <summary>
/// Converts user taps captured against the editor audio clock into a stable
/// quarter-note BPM and the chart Delay that puts beat 0 on the first tapped
/// beat phase (see <see cref="BeatAnalysisResult.OffsetMilliseconds"/>).
/// </summary>
public static class ManualTempoTapAnalyzer
{
    public const int MinimumTapCount = 4;

    public static BeatAnalysisResult Analyze(IEnumerable<double> tapMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(tapMilliseconds);
        var taps = tapMilliseconds
            .Where(double.IsFinite)
            .Where(value => value >= 0)
            .OrderBy(value => value)
            .Distinct()
            .ToArray();
        if (taps.Length < MinimumTapCount)
            throw new InvalidOperationException(LocalizationCatalog.Format("至少需要连续打拍 {0} 次。", MinimumTapCount));

        var intervals = taps
            .Zip(taps.Skip(1), (left, right) => right - left)
            .Where(value => value >= 100 && value <= 2_000)
            .OrderBy(value => value)
            .ToArray();
        if (intervals.Length < MinimumTapCount - 1)
            throw new InvalidOperationException("有效打拍间隔不足，请跟随整拍重新测量。");

        var median = Median(intervals);
        var tolerance = Math.Max(55, median * .35);
        var stableIntervals = intervals
            .Where(value => Math.Abs(value - median) <= tolerance)
            .ToArray();
        if (stableIntervals.Length < MinimumTapCount - 1)
            throw new InvalidOperationException("打拍间隔波动过大，请跟随整拍重新测量。");

        var period = stableIntervals.Average();
        var bpm = 60_000d / period;
        if (!double.IsFinite(bpm) || bpm is < 30 or > 400)
            throw new InvalidOperationException("手动打拍结果超出 30–400 BPM，请重新测量。");

        // Circular mean keeps phase stable when samples straddle the start/end
        // of one beat period.
        var sumX = 0d;
        var sumY = 0d;
        foreach (var tap in taps)
        {
            var angle = 2 * Math.PI * PositiveModulo(tap, period) / period;
            sumX += Math.Cos(angle);
            sumY += Math.Sin(angle);
        }
        var phaseAngle = Math.Atan2(sumY, sumX);
        if (phaseAngle < 0) phaseAngle += 2 * Math.PI;
        // phase is the audio time of the first beat within one period.
        var phase = phaseAngle * period / (2 * Math.PI);
        if (period - phase < .0005) phase = 0;

        var phaseErrors = taps
            .Select(tap =>
            {
                var distance = PositiveModulo(tap - phase + period / 2, period) - period / 2;
                return Math.Abs(distance);
            })
            .OrderBy(value => value)
            .ToArray();
        var instability = Math.Clamp(Median(phaseErrors) / (period / 2), 0, 1);
        var sampleConfidence = Math.Clamp((taps.Length - 3) / 9d, .15, 1);
        var confidence = Math.Clamp(sampleConfidence * (1 - instability * .75), .05, .99);

        return new BeatAnalysisResult(
            Math.Round(bpm, 3),
            BeatAnalysisResult.DelayForFirstBeat(phase),
            Math.Round(instability, 4),
            Math.Round(confidence, 4),
            taps.Length,
            Math.Max(0, (taps[^1] - taps[0]) / 1000));
    }

    private static double PositiveModulo(double value, double divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle];
    }
}

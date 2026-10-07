namespace LumaChart.Application.Audio;

public readonly record struct TempoPrecisionResult(
    double Bpm,
    double RawBpm,
    double Score,
    bool UsedIntegerPrior);

/// <summary>
/// Re-fits a coarse tempo against a complete stable region. The search keeps
/// multiple autocorrelation peaks, uses fractional lags, and treats an integer
/// BPM only as a weak prior when the audio evidence is effectively tied.
/// </summary>
public static class TempoPrecisionRefiner
{
    private const double IntegerCompositeTieRatio = .999;
    private const double IntegerCoherenceTieRatio = .9985;
    private const double LongRegionIntegerCompositeTieRatio = .995;
    private const double LongRegionIntegerCoherenceTieRatio = .993;
    private const double VeryNearIntegerMaximumDistance = .075;
    private const int LongRegionMinimumSeconds = 120;
    private const double FarRangeMinimumEvidence = .05;
    private const double FarRangeRelaxedMinimumAdvantage = -.01;
    private const double FarRangeContradictionAdvantage = -.03;

    public static TempoPrecisionResult Refine(
        IReadOnlyList<double> signal,
        int analysisRate,
        int start,
        int end,
        double coarseBpm) =>
        Refine(signal, analysisRate, start, end, coarseBpm, CancellationToken.None);

    public static TempoPrecisionResult Refine(
        IReadOnlyList<double> signal,
        int analysisRate,
        int start,
        int end,
        double coarseBpm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (analysisRate <= 0) throw new ArgumentOutOfRangeException(nameof(analysisRate));
        start = Math.Clamp(start, 0, signal.Count);
        end = Math.Clamp(end, start, signal.Count);
        if (end - start < analysisRate * 4 || !double.IsFinite(coarseBpm) || coarseBpm is < 60 or > 200)
            return new TempoPrecisionResult(Math.Round(coarseBpm, 3), coarseBpm, 0, false);

        var radius = Math.Max(2, coarseBpm * .035);
        var first = Math.Max(60, coarseBpm - radius);
        var last = Math.Min(200, coarseBpm + radius);
        var coarse = new List<(double Bpm, double Score)>();
        for (var bpm = first; bpm <= last + .0001; bpm += .1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            coarse.Add((bpm, ScoreComponents(signal, analysisRate, start, end, bpm, cancellationToken).Composite));
        }

        var peaks = coarse
            .OrderByDescending(candidate => candidate.Score)
            .Aggregate(new List<(double Bpm, double Score)>(), (selected, candidate) =>
            {
                if (selected.Count < 5 && selected.All(item => Math.Abs(item.Bpm - candidate.Bpm) >= .35))
                    selected.Add(candidate);
                return selected;
            });

        var best = peaks[0];
        foreach (var peak in peaks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var bpm = Math.Max(first, peak.Bpm - .18);
                 bpm <= Math.Min(last, peak.Bpm + .18) + .0001;
                 bpm += .01)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var score = ScoreComponents(signal, analysisRate, start, end, bpm, cancellationToken).Composite;
                if (score > best.Score) best = (bpm, score);
            }
        }

        var rawBest = best;
        // Integer BPM is only a deterministic tie-break. Long-range correlation
        // at 4/8/16 beats must agree too, so a genuine nearby fractional tempo is
        // not flattened merely because integers are common in authored charts.
        var nearestInteger = Math.Round(best.Bpm);
        var integerDistance = Math.Round(Math.Abs(nearestInteger - best.Bpm), 3);
        var useInteger = false;
        if (nearestInteger is >= 60 and <= 200 && integerDistance <= .7)
        {
            var rawScores = ScoreComponents(signal, analysisRate, start, end, best.Bpm, cancellationToken);
            var integerScores = ScoreComponents(signal, analysisRate, start, end, nearestInteger, cancellationToken);
            // The long-range aggregate is intentionally a little more tolerant
            // than the composite score. Real percussion has fills and layered
            // transients that can move only the 8/16-beat terms by roughly one
            // tenth of a percent even when the local and aggregate evidence put
            // the optimum within a few hundredths of an authored integer BPM.
            var strictTie = integerScores.Composite >= rawScores.Composite * IntegerCompositeTieRatio &&
                            integerScores.Coherence >= rawScores.Coherence * IntegerCoherenceTieRatio;
            var isLongRegion = end - start >= analysisRate * LongRegionMinimumSeconds;
            var far = CompareLongRangeCoherence(
                signal, analysisRate, start, end, best.Bpm, nearestInteger, cancellationToken);
            var scale = Math.Max(FarRangeMinimumEvidence,
                Math.Max(Math.Abs(far.RawCoherence), Math.Abs(far.IntegerCoherence)));
            var integerAdvantage = (far.IntegerCoherence - far.RawCoherence) / scale;
            var farSupportsInteger = far.Count >= 2 && far.IntegerCoherence >= FarRangeMinimumEvidence &&
                                     integerAdvantage >= FarRangeRelaxedMinimumAdvantage;
            var farContradictsInteger = far.Count >= 2 && far.RawCoherence >= FarRangeMinimumEvidence &&
                                        integerAdvantage < FarRangeContradictionAdvantage;
            // Long decoded tracks can leave a small encoder/texture residual at
            // an authored integer BPM. Only widen the evidence tie when the
            // fitted result is already within .075 BPM of that integer, the
            // stable region spans at least two minutes, and 32-256-beat phase
            // evidence does not prefer the fractional candidate. The same far
            // evidence vetoes a strict tie when it clearly identifies a genuine
            // nearby fractional tempo such as 174.95 or 175.05.
            strictTie &= !farContradictsInteger;
            var longVeryNearTie = isLongRegion &&
                                  integerDistance <= VeryNearIntegerMaximumDistance &&
                                  integerScores.Composite >= rawScores.Composite * LongRegionIntegerCompositeTieRatio &&
                                  integerScores.Coherence >= rawScores.Coherence * LongRegionIntegerCoherenceTieRatio &&
                                  farSupportsInteger;
            if (strictTie || longVeryNearTie)
            {
                best = (nearestInteger, integerScores.Composite);
                useInteger = true;
            }
        }

        return new TempoPrecisionResult(Math.Round(best.Bpm, 3), Math.Round(rawBest.Bpm, 3),
            Math.Round(best.Score, 6), useInteger);
    }

    public static double Score(
        IReadOnlyList<double> signal,
        int analysisRate,
        int start,
        int end,
        double bpm) =>
        ScoreComponents(signal, analysisRate, start, end, bpm, CancellationToken.None).Composite;

    private static (double Composite, double Coherence) ScoreComponents(
        IReadOnlyList<double> signal,
        int analysisRate,
        int start,
        int end,
        double bpm,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(bpm) || bpm <= 0 || start < 0 || end > signal.Count || start >= end) return (0, 0);
        var lag = 60d * analysisRate / bpm;
        var half = NormalizedCorrelation(signal, start, end, lag * .5, cancellationToken);
        var one = NormalizedCorrelation(signal, start, end, lag, cancellationToken);
        var two = NormalizedCorrelation(signal, start, end, lag * 2, cancellationToken);
        var local = (one + half * .16 + two * .12) / 1.28;

        var length = end - start;
        var weighted = 0d;
        var totalWeight = 0d;
        (double Multiple, double Correlation)[] longRanges =
            [(1, one), (2, two), (4, double.NaN), (8, double.NaN), (16, double.NaN)];
        foreach (var item in longRanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shifted = lag * item.Multiple;
            var overlap = length - shifted;
            if (overlap < length * .5) continue;
            var correlation = double.IsNaN(item.Correlation)
                ? NormalizedCorrelation(signal, start, end, shifted, cancellationToken)
                : item.Correlation;
            var weight = Math.Sqrt(overlap / length);
            weighted += correlation * weight;
            totalWeight += weight;
        }
        var coherence = totalWeight > 0 ? weighted / totalWeight : one;
        return (local * .35 + coherence * .65, coherence);
    }

    private static (double RawCoherence, double IntegerCoherence, int Count) CompareLongRangeCoherence(
        IReadOnlyList<double> signal,
        int analysisRate,
        int start,
        int end,
        double rawBpm,
        double integerBpm,
        CancellationToken cancellationToken)
    {
        var rawLag = 60d * analysisRate / rawBpm;
        var integerLag = 60d * analysisRate / integerBpm;
        var length = end - start;
        var rawWeighted = 0d;
        var integerWeighted = 0d;
        var totalWeight = 0d;
        var count = 0;
        foreach (var multiple in new[] { 32d, 64d, 128d, 256d })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawShift = rawLag * multiple;
            var integerShift = integerLag * multiple;
            var commonOverlap = Math.Min(length - rawShift, length - integerShift);
            if (commonOverlap < length * .5) continue;
            var weight = Math.Sqrt(commonOverlap / length);
            rawWeighted += NormalizedCorrelation(
                signal, start, end, rawShift, cancellationToken) * weight;
            integerWeighted += NormalizedCorrelation(
                signal, start, end, integerShift, cancellationToken) * weight;
            totalWeight += weight;
            count++;
        }

        return totalWeight > 0
            ? (rawWeighted / totalWeight, integerWeighted / totalWeight, count)
            : (0, 0, 0);
    }

    private static double NormalizedCorrelation(
        IReadOnlyList<double> signal,
        int start,
        int end,
        double lag,
        CancellationToken cancellationToken)
    {
        lag = Math.Max(1, lag);
        var first = Math.Max(start + (int)Math.Ceiling(lag), (int)Math.Ceiling(lag));
        if (first >= end) return 0;
        var product = 0d;
        var leftSquares = 0d;
        var rightSquares = 0d;
        for (var index = first; index < end; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var left = signal[index];
            var source = index - lag;
            var sourceLeft = Math.Max(0, (int)Math.Floor(source));
            var fraction = source - sourceLeft;
            var right = sourceLeft + 1 < signal.Count
                ? signal[sourceLeft] * (1 - fraction) + signal[sourceLeft + 1] * fraction
                : signal[sourceLeft];
            product += left * right;
            leftSquares += left * left;
            rightSquares += right * right;
        }
        return product / Math.Sqrt(Math.Max(1e-12, leftSquares * rightSquares));
    }
}

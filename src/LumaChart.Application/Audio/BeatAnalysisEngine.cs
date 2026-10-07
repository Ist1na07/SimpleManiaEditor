using System.IO;

namespace LumaChart.Application.Audio;

/// <param name="OffsetMilliseconds">
/// The chart Delay (Offset field) to write into the beat-0 BPM record. TimingMap
/// places beat 0 at -Delay ms, so a first beat heard at +t ms in the audio is
/// reported as -t. Earlier builds reported +t and every consumer wrote it as
/// Delay, which put beat 0 at -t and left the grid a phase error of 2t.
/// </param>
public readonly record struct BeatAnalysisResult(
    double Bpm,
    double OffsetMilliseconds,
    double Instability,
    double Confidence,
    int OnsetCount,
    double DurationSeconds)
{
    /// <summary>Converts the audio time of a detected first beat into the chart Delay.</summary>
    public static double DelayForFirstBeat(double firstBeatMilliseconds)
    {
        var rounded = Math.Round(firstBeatMilliseconds, 3);
        // Avoid -0, which formats as "-0" in the Offset field.
        return rounded == 0 ? 0 : -rounded;
    }
}

public sealed record TempoTimelineAnalysisResult(
    BeatAnalysisResult SingleTempoReference,
    VariableTempoEstimate Timeline);

public sealed record BeatAnalysisSignal(
    double[] Novelty,
    int AnalysisRate,
    double DurationSeconds);

/// <summary>
/// Platform-neutral BPM/phase analysis. Desktop hosts decode audio and reduce it
/// to an exact-rate energy envelope; the scoring, precision refinement and
/// variable-tempo estimation remain shared and deterministic here.
/// </summary>
public static class BeatAnalysisEngine
{
    public const int DefaultAnalysisRate = 200;
    public const int MaximumAnalysisSeconds = 240;

    public static BeatAnalysisSignal CreateSignal(
        IReadOnlyList<double> energies,
        int analysisRate = DefaultAnalysisRate)
    {
        ArgumentNullException.ThrowIfNull(energies);
        if (analysisRate <= 0) throw new ArgumentOutOfRangeException(nameof(analysisRate));
        if (energies.Count < analysisRate * 8)
            throw new InvalidDataException("音频过短，无法可靠检测 BPM。");

        var envelope = Smooth(energies, 4);
        var novelty = new double[envelope.Length];
        for (var index = 1; index < envelope.Length; index++)
            novelty[index] = Math.Max(0, envelope[index] - envelope[index - 1]);
        novelty = Smooth(novelty, 2);
        RemoveLocalMean(novelty, analysisRate * 2);
        return new BeatAnalysisSignal(
            novelty,
            analysisRate,
            energies.Count / (double)analysisRate);
    }

    public static BeatAnalysisResult Analyze(
        BeatAnalysisSignal signal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ValidateSignal(signal);
        var novelty = signal.Novelty;
        var analysisRate = signal.AnalysisRate;
        if (!novelty.Any(value => value > 1e-12))
            throw new InvalidDataException("音频中没有检测到可用的节拍变化。");

        var bestLag = 0d;
        var bestScore = double.MinValue;
        for (var bpm = 60d; bpm <= 200; bpm += .05)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lag = 60d * analysisRate / bpm;
            var score = HarmonicCorrelation(novelty, lag);
            if (score <= bestScore) continue;
            bestScore = score;
            bestLag = lag;
        }
        if (bestLag <= 0) throw new InvalidDataException("没有检测到稳定节拍。");

        var onsets = FindOnsets(novelty, analysisRate);
        var rawBpm = 60d * analysisRate / RefineLag(novelty, bestLag, cancellationToken);
        var candidates = new[] { rawBpm / 2, rawBpm, rawBpm * 2 }
            .Where(bpm => bpm is >= 60 and <= 200)
            .DistinctBy(bpm => Math.Round(bpm, 1))
            .Select(bpm => EvaluateCandidate(
                novelty,
                onsets,
                bpm,
                analysisRate,
                signal.DurationSeconds,
                cancellationToken))
            .OrderByDescending(candidate => candidate.Rank)
            .ToList();
        if (candidates.Count == 0) throw new InvalidDataException("没有检测到稳定节拍。");

        var selected = candidates[0];
        var precision = TempoPrecisionRefiner.Refine(
            novelty,
            analysisRate,
            0,
            novelty.Length,
            selected.Bpm,
            cancellationToken);
        selected = EvaluateCandidate(
            novelty,
            onsets,
            precision.Bpm,
            analysisRate,
            signal.DurationSeconds,
            cancellationToken,
            refineLag: false);
        var normalizedCorrelation = Math.Clamp(
            selected.Correlation / Math.Max(1e-9, novelty.Sum(value => value * value)),
            0,
            1);
        var confidence = Math.Clamp(
            normalizedCorrelation * .55 +
            (1 - selected.Instability) * .3 +
            selected.DensityFit * .15,
            0,
            1);
        return new BeatAnalysisResult(
            Math.Round(selected.Bpm, 3),
            BeatAnalysisResult.DelayForFirstBeat(selected.Phase * 1000 / analysisRate),
            Math.Round(selected.Instability, 4),
            Math.Round(confidence, 4),
            onsets.Count,
            signal.DurationSeconds);
    }

    public static TempoTimelineAnalysisResult AnalyzeTimeline(
        BeatAnalysisSignal signal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var single = Analyze(signal, cancellationToken);
        var timeline = VariableTempoEstimator.Estimate(
            signal.Novelty,
            signal.AnalysisRate,
            cancellationToken);
        return new TempoTimelineAnalysisResult(single, timeline);
    }

    private static void ValidateSignal(BeatAnalysisSignal signal)
    {
        if (signal.AnalysisRate <= 0 ||
            !double.IsFinite(signal.DurationSeconds) ||
            signal.DurationSeconds <= 0 ||
            signal.Novelty.Length < signal.AnalysisRate * 8)
            throw new ArgumentException("The analysis signal is incomplete.", nameof(signal));
        if (signal.Novelty.Any(value => !double.IsFinite(value) || value < 0))
            throw new ArgumentException("The analysis signal contains an invalid novelty value.", nameof(signal));
    }

    private static TempoCandidate EvaluateCandidate(
        double[] novelty,
        List<int> onsets,
        double bpm,
        int analysisRate,
        double durationSeconds,
        CancellationToken cancellationToken,
        bool refineLag = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var initialLag = 60d * analysisRate / bpm;
        var lag = refineLag ? RefineLag(novelty, initialLag, cancellationToken) : initialLag;
        var refinedBpm = 60d * analysisRate / lag;
        var phase = FindPhase(novelty, lag);
        var instability = CalculateInstability(onsets, phase, lag);
        var correlation = HarmonicCorrelation(novelty, lag);
        var beats = Math.Max(1, durationSeconds * refinedBpm / 60d);
        var onsetsPerBeat = onsets.Count / beats;
        var densityFit = Math.Exp(-Math.Abs(Math.Log(Math.Max(.05, onsetsPerBeat) / 1.65)) * 1.25);
        var tempoPrior = refinedBpm >= 100 ? 1 : .82;
        var rank = correlation * (.55 + .25 * (1 - instability) + .2 * densityFit) * tempoPrior;
        return new TempoCandidate(refinedBpm, phase, instability, correlation, densityFit, rank);
    }

    private static double[] Smooth(IReadOnlyList<double> values, int radius)
    {
        var result = new double[values.Count];
        var sum = 0d;
        for (var index = 0; index < values.Count; index++)
        {
            sum += values[index];
            if (index - radius >= 0) sum -= values[index - radius];
            result[index] = sum / Math.Min(index + 1, radius);
        }
        return result;
    }

    private static void RemoveLocalMean(double[] values, int window)
    {
        var prefix = new double[values.Length + 1];
        for (var index = 0; index < values.Length; index++)
            prefix[index + 1] = prefix[index] + values[index];
        for (var index = 0; index < values.Length; index++)
        {
            var left = Math.Max(0, index - window);
            var mean = (prefix[index + 1] - prefix[left]) / (index - left + 1);
            values[index] = Math.Max(0, values[index] - mean * 1.15);
        }
    }

    private static double HarmonicCorrelation(double[] values, double lag)
    {
        var score = 0d;
        foreach (var multiplier in new[] { .5, 1d, 2d })
        {
            var actualLag = Math.Max(1, lag * multiplier);
            var weight = multiplier == 1 ? 1 : .3;
            for (var index = (int)Math.Ceiling(actualLag); index < values.Length; index++)
            {
                var source = index - actualLag;
                var sourceLeft = Math.Max(0, (int)Math.Floor(source));
                var fraction = source - sourceLeft;
                var previous = sourceLeft + 1 < values.Length
                    ? values[sourceLeft] * (1 - fraction) + values[sourceLeft + 1] * fraction
                    : values[sourceLeft];
                score += values[index] * previous * weight;
            }
        }
        return score;
    }

    private static double RefineLag(
        double[] values,
        double center,
        CancellationToken cancellationToken)
    {
        var bestLag = center;
        var best = double.MinValue;
        for (var lag = Math.Max(1d, center - 3d); lag <= center + 3; lag += .02)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = HarmonicCorrelation(values, lag);
            if (score <= best) continue;
            best = score;
            bestLag = lag;
        }
        return bestLag;
    }

    private static double FindPhase(double[] values, double lag)
    {
        var limit = Math.Max(1, (int)Math.Ceiling(lag * 2));
        var bestPhase = 0d;
        var best = double.MinValue;
        for (var phaseIndex = 0; phaseIndex < limit; phaseIndex++)
        {
            var phase = phaseIndex * .5;
            var score = 0d;
            for (var position = phase; position < values.Length; position += lag)
            {
                var left = Math.Max(0, (int)Math.Floor(position));
                var fraction = position - left;
                score += left + 1 < values.Length
                    ? values[left] * (1 - fraction) + values[left + 1] * fraction
                    : values[left];
            }
            if (score <= best) continue;
            best = score;
            bestPhase = phase;
        }
        return bestPhase;
    }

    private static List<int> FindOnsets(double[] values, int analysisRate)
    {
        var sorted = values.Where(value => value > 0).OrderBy(value => value).ToArray();
        var threshold = sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * .8)];
        var result = new List<int>();
        var minimumDistance = analysisRate / 12;
        for (var index = 1; index < values.Length - 1; index++)
        {
            if (values[index] < threshold ||
                values[index] < values[index - 1] ||
                values[index] < values[index + 1])
                continue;
            if (result.Count > 0 && index - result[^1] < minimumDistance)
            {
                if (values[index] > values[result[^1]]) result[^1] = index;
                continue;
            }
            result.Add(index);
        }
        return result;
    }

    private static double CalculateInstability(List<int> onsets, double phase, double lag)
    {
        if (onsets.Count == 0) return 1;
        var errors = onsets.Select(onset =>
        {
            var relative = (onset - phase) / lag;
            return Math.Abs(relative - Math.Round(relative));
        }).OrderBy(value => value).ToArray();
        return Math.Clamp(errors[errors.Length / 2] * 2, 0, 1);
    }

    private readonly record struct TempoCandidate(
        double Bpm,
        double Phase,
        double Instability,
        double Correlation,
        double DensityFit,
        double Rank);
}

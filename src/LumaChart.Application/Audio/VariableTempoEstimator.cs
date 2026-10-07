using LumaChart.Core.Models;

namespace LumaChart.Application.Audio;

public readonly record struct TempoWindowEstimate(
    double CenterSeconds,
    double Bpm,
    double Confidence,
    double Instability);

public readonly record struct TempoSegmentEstimate(
    double StartSeconds,
    double Bpm,
    double Confidence,
    int SupportingWindows);

/// <param name="InitialOffsetMilliseconds">
/// The chart Delay of the first BPM record: beat 0 is at -InitialOffsetMilliseconds
/// in the audio (see <see cref="BeatAnalysisResult.OffsetMilliseconds"/>).
/// MapToTiming and the boundary alignment integrate from that same origin.
/// </param>
public sealed record VariableTempoEstimate(
    IReadOnlyList<TempoSegmentEstimate> Segments,
    IReadOnlyList<TempoWindowEstimate> Windows,
    double InitialOffsetMilliseconds,
    double DurationSeconds);

public readonly record struct TempoTimingCandidate(
    BeatPosition Beat,
    double Bpm,
    double StartSeconds,
    double Confidence);

/// <summary>
/// Pure tempo-timeline estimation over a non-negative onset novelty envelope.
/// Audio decoding stays in the platform shell, so this algorithm is deterministic
/// and can be exercised by the cross-platform specification project.
/// </summary>
public static class VariableTempoEstimator
{
    private const double MinimumBpm = 60;
    private const double MaximumBpm = 200;
    private const double WindowSeconds = 10;
    private const double HopSeconds = 1.5;
    private const int RequiredStableWindows = 7;
    private static readonly int[] PreferredBoundaryDenominators =
        [1, 2, 4, 3, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128, 192];

    public static VariableTempoEstimate Estimate(
        IReadOnlyList<double> novelty,
        int analysisRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(novelty);
        if (analysisRate < 20) throw new ArgumentOutOfRangeException(nameof(analysisRate));
        if (novelty.Count < analysisRate * 8)
            throw new InvalidDataException("节拍包络过短，无法预测 BPM 时间线。");

        var signal = new double[novelty.Count];
        for (var index = 0; index < novelty.Count; index++)
            signal[index] = double.IsFinite(novelty[index]) ? Math.Max(0, novelty[index]) : 0;

        var duration = signal.Length / (double)analysisRate;
        if (!signal.Any(value => value > 1e-12))
            throw new InvalidDataException("节拍包络中没有可用的节拍变化。");
        var windowFrames = Math.Min(signal.Length, Math.Max(analysisRate * 8, (int)Math.Round(WindowSeconds * analysisRate)));
        var hopFrames = Math.Max(1, (int)Math.Round(HopSeconds * analysisRate));
        var starts = BuildWindowStarts(signal.Length, windowFrames, hopFrames);
        var windows = new List<TempoWindowEstimate>(starts.Count);
        foreach (var start in starts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            windows.Add(EstimateWindow(signal, analysisRate, start, Math.Min(signal.Length, start + windowFrames)));
        }

        if (windows.Count == 0 || windows.All(window => window.Bpm <= 0))
            throw new InvalidDataException("没有检测到可用的局部节拍。");

        var stabilized = StabilizeWindows(windows);
        var segments = BuildSegments(signal, analysisRate, stabilized, duration, cancellationToken,
            out var roughBoundaries);
        if (segments.Count == 0)
            throw new InvalidDataException("没有检测到稳定的 BPM 分段。");
        segments = RefineSegmentTempos(signal, analysisRate, segments, roughBoundaries, duration,
            cancellationToken, out var refinedRoughBoundaries);
        roughBoundaries = refinedRoughBoundaries;

        var firstSegmentEnd = segments.Count > 1 ? segments[1].StartSeconds : Math.Min(duration, 28);
        var phaseEnd = Math.Max(8, Math.Min(duration, firstSegmentEnd - 2));
        var phase = FindPhase(signal, 0, Math.Max(1, (int)Math.Round(phaseEnd * analysisRate)),
            60d * analysisRate / segments[0].Bpm);
        // phase is where the first beat is heard; the chart Delay has the
        // opposite sign (beat 0 at -Delay ms).
        var offset = BeatAnalysisResult.DelayForFirstBeat(phase * 1000d / analysisRate);
        segments = AlignBoundariesToMusicalGrid(signal, analysisRate, segments, roughBoundaries,
            duration, offset, cancellationToken);
        return new VariableTempoEstimate(segments, stabilized, offset, duration);
    }

    public static IReadOnlyList<TempoTimingCandidate> MapToTiming(
        VariableTempoEstimate estimate,
        int snapDenominator = 192)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (estimate.Segments.Count == 0) return [];
        snapDenominator = Math.Clamp(snapDenominator, 1, 1920);

        var result = new List<TempoTimingCandidate>(estimate.Segments.Count);
        var beat = 0d;
        var previousAudioSeconds = -estimate.InitialOffsetMilliseconds / 1000d;
        var previousBpm = estimate.Segments[0].Bpm;
        for (var index = 0; index < estimate.Segments.Count; index++)
        {
            var segment = estimate.Segments[index];
            if (index > 0)
            {
                beat += Math.Max(0, segment.StartSeconds - previousAudioSeconds) * previousBpm / 60d;
                previousAudioSeconds = segment.StartSeconds;
                previousBpm = segment.Bpm;
            }
            var mappedBeat = index == 0
                ? BeatPosition.Zero
                : SnapMappedBeatToPreferredGrid(beat, snapDenominator);
            // Quantization is part of the piecewise timing solution, not merely a
            // display format. Anchor the next segment at the accepted rational
            // boundary so a small detector residual cannot accumulate forever.
            beat = mappedBeat.Value;
            result.Add(new TempoTimingCandidate(
                mappedBeat,
                segment.Bpm,
                segment.StartSeconds,
                segment.Confidence));
        }
        return result;
    }

    private static List<int> BuildWindowStarts(int length, int window, int hop)
    {
        if (length <= window) return [0];
        var result = new List<int>();
        for (var start = 0; start + window <= length; start += hop) result.Add(start);
        var finalStart = length - window;
        if (result.Count == 0 || finalStart - result[^1] >= hop / 2) result.Add(finalStart);
        return result;
    }

    private static TempoWindowEstimate EstimateWindow(double[] signal, int rate, int start, int end)
    {
        var onsets = FindOnsets(signal, rate, start, end);
        var coarse = new List<(double Bpm, double Correlation)>();
        for (var bpm = MinimumBpm; bpm <= MaximumBpm + .001; bpm += .5)
            coarse.Add((bpm, HarmonicCorrelation(signal, start, end, 60d * rate / bpm)));

        var candidates = coarse.OrderByDescending(candidate => candidate.Correlation)
            .Take(18)
            .Select(candidate => EvaluateCandidate(signal, onsets, rate, start, end, candidate.Bpm))
            .ToList();
        var coarseBest = candidates.OrderByDescending(candidate => candidate.Rank).First();
        for (var bpm = Math.Max(MinimumBpm, coarseBest.Bpm - 1);
             bpm <= Math.Min(MaximumBpm, coarseBest.Bpm + 1) + .001;
             bpm += .05)
            candidates.Add(EvaluateCandidate(signal, onsets, rate, start, end, bpm));

        var ordered = candidates.OrderByDescending(candidate => candidate.Rank).ToList();
        var best = ordered[0];
        var competingRank = ordered.FirstOrDefault(candidate => Math.Abs(candidate.Bpm - best.Bpm) >= 3).Rank;
        var contrast = best.Rank <= 1e-9 ? 0 : Math.Clamp((best.Rank - competingRank) / best.Rank, 0, 1);
        var confidence = Math.Clamp(best.NormalizedCorrelation * .48 + (1 - best.Instability) * .27 +
                                    best.DensityFit * .12 + contrast * .13, 0, 1);
        return new TempoWindowEstimate(
            (start + end) / 2d / rate,
            Math.Round(best.Bpm, 3),
            Math.Round(confidence, 4),
            Math.Round(best.Instability, 4));
    }

    private static TempoCandidate EvaluateCandidate(
        double[] signal,
        IReadOnlyList<int> onsets,
        int rate,
        int start,
        int end,
        double bpm)
    {
        var lag = 60d * rate / bpm;
        var correlation = HarmonicCorrelation(signal, start, end, lag);
        var normalizedCorrelation = Math.Clamp(correlation / 1.35, 0, 1);
        var phase = FindPhase(signal, start, end, lag);
        var instability = CalculateInstability(onsets, phase, lag);
        var duration = Math.Max(.001, (end - start) / (double)rate);
        var beats = Math.Max(1, duration * bpm / 60d);
        var onsetsPerBeat = onsets.Count / beats;
        var densityFit = Math.Exp(-Math.Abs(Math.Log(Math.Max(.05, onsetsPerBeat) / 1.25)) * .72);
        var tempoPrior = bpm switch
        {
            < 80 => .72,
            < 95 => .86,
            > 190 => .82,
            > 180 => .92,
            _ => 1
        };
        var rank = normalizedCorrelation * (.58 + .27 * (1 - instability) + .15 * densityFit) * tempoPrior;
        return new TempoCandidate(bpm, instability, normalizedCorrelation, densityFit, rank);
    }

    private static IReadOnlyList<TempoWindowEstimate> StabilizeWindows(IReadOnlyList<TempoWindowEstimate> windows)
    {
        var clusters = new List<TempoCluster>();
        foreach (var window in windows.OrderByDescending(window => window.Confidence))
        {
            var cluster = clusters.OrderBy(candidate => RelativeDifference(candidate.Bpm, window.Bpm))
                .FirstOrDefault(candidate => RelativeDifference(candidate.Bpm, window.Bpm) <= .0125);
            if (cluster == null)
            {
                cluster = new TempoCluster();
                clusters.Add(cluster);
            }
            cluster.Add(window);
        }

        var accepted = clusters.Where(cluster => cluster.Count >= 2 || clusters.Count == 1)
            .OrderBy(cluster => cluster.Bpm)
            .ToArray();
        if (accepted.Length == 0) accepted = [clusters.OrderByDescending(cluster => cluster.Weight).First()];

        var canonical = windows.Select(window =>
        {
            var cluster = accepted.MinBy(candidate => RelativeDifference(candidate.Bpm, window.Bpm))!;
            return window with { Bpm = Math.Round(cluster.Bpm, 3) };
        }).ToArray();

        // A one-window tempo excursion is almost always a transient breakdown or
        // half/double-time ambiguity. Median voting retains genuine changes once
        // they remain stable for neighbouring windows.
        var smoothed = new TempoWindowEstimate[canonical.Length];
        for (var index = 0; index < canonical.Length; index++)
        {
            var left = Math.Max(0, index - 1);
            var right = Math.Min(canonical.Length - 1, index + 1);
            var bpm = WeightedMedian(canonical.Skip(left).Take(right - left + 1)
                .Select(window => (window.Bpm, Math.Max(.05, window.Confidence))));
            smoothed[index] = canonical[index] with { Bpm = bpm };
        }

        var changed = true;
        while (changed && smoothed.Length > 1)
        {
            changed = false;
            var runs = BuildRuns(smoothed);
            // Prefer removing a short A-B-A island before considering edge runs.
            // This prevents an uncertain intro window from swallowing the stable
            // tempo that immediately follows it.
            var shortRunIndex = -1;
            for (var index = 1; index < runs.Count - 1; index++)
            {
                if (runs[index].Length >= RequiredStableWindows ||
                    RelativeDifference(smoothed[runs[index - 1].Start].Bpm,
                        smoothed[runs[index + 1].Start].Bpm) > .02) continue;
                shortRunIndex = index;
                break;
            }
            if (shortRunIndex < 0)
                shortRunIndex = runs.FindIndex(run => run.Length < RequiredStableWindows && runs.Count > 1);
            if (shortRunIndex < 0) break;
            var shortRun = runs[shortRunIndex];
            var replacement = shortRun.Start == 0
                ? smoothed[shortRun.End + 1].Bpm
                : shortRun.End == smoothed.Length - 1
                    ? smoothed[shortRun.Start - 1].Bpm
                    : RelativeDifference(smoothed[shortRun.Start].Bpm, smoothed[shortRun.Start - 1].Bpm) <=
                      RelativeDifference(smoothed[shortRun.Start].Bpm, smoothed[shortRun.End + 1].Bpm)
                        ? smoothed[shortRun.Start - 1].Bpm
                        : smoothed[shortRun.End + 1].Bpm;
            for (var index = shortRun.Start; index <= shortRun.End; index++)
                smoothed[index] = smoothed[index] with { Bpm = replacement };
            changed = true;
        }
        return smoothed;
    }

    private static IReadOnlyList<TempoSegmentEstimate> BuildSegments(
        double[] signal,
        int rate,
        IReadOnlyList<TempoWindowEstimate> windows,
        double duration,
        CancellationToken cancellationToken,
        out IReadOnlyList<double> roughBoundaries)
    {
        var runs = BuildRuns(windows);
        var result = new List<TempoSegmentEstimate>(runs.Count);
        var roughs = new List<double>(runs.Count);
        for (var runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = runs[runIndex];
            var samples = windows.Skip(run.Start).Take(run.Length).ToArray();
            var bpm = WeightedMedian(samples.Select(window => (window.Bpm, Math.Max(.05, window.Confidence))));
            var confidence = samples.Average(window => window.Confidence);
            var startSeconds = 0d;
            var rough = 0d;
            if (runIndex > 0)
            {
                var previous = runs[runIndex - 1];
                rough = (windows[previous.End].CenterSeconds + windows[run.Start].CenterSeconds) / 2;
                startSeconds = RefineBoundary(signal, rate, rough, result[^1].Bpm, bpm, duration);
                startSeconds = Math.Max(result[^1].StartSeconds + 1, startSeconds);
            }
            result.Add(new TempoSegmentEstimate(Math.Round(startSeconds, 3), Math.Round(bpm, 3),
                Math.Round(confidence, 4), run.Length));
            roughs.Add(rough);
        }

        for (var index = result.Count - 1; index > 0; index--)
        {
            if (RelativeDifference(result[index - 1].Bpm, result[index].Bpm) > .02) continue;
            var totalWindows = result[index - 1].SupportingWindows + result[index].SupportingWindows;
            var bpm = (result[index - 1].Bpm * result[index - 1].SupportingWindows +
                       result[index].Bpm * result[index].SupportingWindows) / totalWindows;
            var confidence = (result[index - 1].Confidence * result[index - 1].SupportingWindows +
                              result[index].Confidence * result[index].SupportingWindows) / totalWindows;
            result[index - 1] = result[index - 1] with
            {
                Bpm = Math.Round(bpm, 3), Confidence = Math.Round(confidence, 4), SupportingWindows = totalWindows
            };
            result.RemoveAt(index);
            roughs.RemoveAt(index);
        }
        roughBoundaries = roughs;
        return result;
    }

    private static IReadOnlyList<TempoSegmentEstimate> RefineSegmentTempos(
        double[] signal,
        int rate,
        IReadOnlyList<TempoSegmentEstimate> segments,
        IReadOnlyList<double> roughBoundaries,
        double duration,
        CancellationToken cancellationToken,
        out IReadOnlyList<double> refinedRoughBoundaries)
    {
        var result = new List<TempoSegmentEstimate>(segments.Count);
        var roughs = roughBoundaries.ToList();
        for (var index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            var startSeconds = segment.StartSeconds;
            var endSeconds = index + 1 < segments.Count ? segments[index + 1].StartSeconds : duration;
            var margin = Math.Min(1.25, Math.Max(0, (endSeconds - startSeconds - 5) / 4));
            var start = (int)Math.Round((startSeconds + margin) * rate);
            var end = (int)Math.Round((endSeconds - margin) * rate);
            var refined = TempoPrecisionRefiner.Refine(signal, rate, start, end, segment.Bpm, cancellationToken);
            result.Add(segment with { Bpm = refined.Bpm });
        }

        for (var index = result.Count - 1; index > 0; index--)
        {
            if (RelativeDifference(result[index - 1].Bpm, result[index].Bpm) > .008) continue;
            var totalWindows = result[index - 1].SupportingWindows + result[index].SupportingWindows;
            result[index - 1] = result[index - 1] with
            {
                Bpm = TempoPrecisionRefiner.Refine(signal, rate,
                    (int)Math.Round(result[index - 1].StartSeconds * rate),
                    (int)Math.Round((index + 1 < result.Count ? result[index + 1].StartSeconds : duration) * rate),
                    result[index - 1].Bpm, cancellationToken).Bpm,
                SupportingWindows = totalWindows,
                Confidence = Math.Round((result[index - 1].Confidence * result[index - 1].SupportingWindows +
                                         result[index].Confidence * result[index].SupportingWindows) / totalWindows, 4)
            };
            result.RemoveAt(index);
            roughs.RemoveAt(index);
        }
        refinedRoughBoundaries = roughs;
        return result;
    }

    private static double RefineBoundary(double[] signal, int rate, double roughSeconds,
        double previousBpm, double nextBpm, double duration)
    {
        const double scanRadius = 4.5;
        const double scoreWindow = 7;
        const double step = .25;
        var first = Math.Max(scoreWindow, roughSeconds - scanRadius);
        var last = Math.Min(duration - scoreWindow, roughSeconds + scanRadius);
        var bestCenter = roughSeconds;
        var bestScore = double.MinValue;
        for (var center = first; center <= last + .001; center += step)
        {
            var score = BoundaryScore(signal, rate, center, previousBpm, nextBpm, scoreWindow);
            if (score > bestScore)
            {
                bestScore = score;
                bestCenter = center;
            }
        }
        return Math.Round(bestCenter, 3);
    }

    /// <summary>
    /// The change-point score has a deliberately broad window and therefore a
    /// broad maximum. Resolve only near-ties against a musical grid after BPM and
    /// initial phase have been refined. A clearly stronger acoustic boundary is
    /// never pulled onto a convenient fraction.
    /// </summary>
    private static IReadOnlyList<TempoSegmentEstimate> AlignBoundariesToMusicalGrid(
        double[] signal,
        int rate,
        IReadOnlyList<TempoSegmentEstimate> segments,
        IReadOnlyList<double> roughBoundaries,
        double duration,
        double initialOffsetMilliseconds,
        CancellationToken cancellationToken)
    {
        if (segments.Count <= 1 || roughBoundaries.Count != segments.Count) return segments;

        const double searchSeconds = 2.25;
        const double roughDistancePenalty = .016;
        var result = new List<TempoSegmentEstimate>(segments.Count) { segments[0] };
        var previousBeat = 0d;
        var previousSeconds = -initialOffsetMilliseconds / 1000d;
        var previousBpm = segments[0].Bpm;
        BeatPosition? establishedFraction = null;

        for (var index = 1; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            var rawSeconds = segment.StartSeconds;
            var rawBeat = previousBeat + Math.Max(0, rawSeconds - previousSeconds) * previousBpm / 60d;
            var radiusBeats = searchSeconds * previousBpm / 60d;
            var candidates = new Dictionary<BeatPosition, BoundaryGridCandidate>();

            void AddCandidate(BeatPosition beat)
            {
                if (candidates.ContainsKey(beat)) return;
                var seconds = previousSeconds + (beat.Value - previousBeat) * 60d / previousBpm;
                if (seconds < previousSeconds + 1 || seconds > duration ||
                    Math.Abs(seconds - rawSeconds) > searchSeconds + 1e-9) return;
                var score = BoundaryScore(signal, rate, seconds, previousBpm, segment.Bpm);
                candidates.Add(beat, new BoundaryGridCandidate(beat, seconds, score,
                    Math.Abs(beat.Value - rawBeat)));
            }

            foreach (var denominator in PreferredBoundaryDenominators)
                AddCandidate(BeatPosition.FromDouble(rawBeat).Snap(denominator));

            if (establishedFraction.HasValue)
            {
                var phase = establishedFraction.Value;
                var firstBar = (int)Math.Floor(rawBeat - radiusBeats - phase.Value) - 1;
                var lastBar = (int)Math.Ceiling(rawBeat + radiusBeats - phase.Value) + 1;
                for (var bar = firstBar; bar <= lastBar; bar++)
                    AddCandidate(BeatPosition.Preserve(bar, phase.Numerator, phase.Denominator));
            }

            var rawScore = BoundaryScore(signal, rate, rawSeconds, previousBpm, segment.Bpm);
            var bestAcousticScore = candidates.Count == 0
                ? rawScore
                : Math.Max(rawScore, candidates.Values.Max(candidate => candidate.Score));
            var acousticTolerance = Math.Max(.025, Math.Abs(bestAcousticScore) * .10);
            var minimumAcousticScore = bestAcousticScore - acousticTolerance;
            BoundaryGridCandidate? selected = null;

            if (establishedFraction.HasValue)
            {
                var phase = establishedFraction.Value;
                selected = candidates.Values
                    .Where(candidate => candidate.Score >= minimumAcousticScore &&
                                        SameFraction(candidate.Beat, phase) &&
                                        bestAcousticScore > 0 && candidate.Score > 0 &&
                                        candidate.Score >= bestAcousticScore * .90)
                    .OrderByDescending(candidate => candidate.Score -
                        Math.Abs(candidate.Seconds - roughBoundaries[index]) * roughDistancePenalty)
                    .ThenBy(candidate => candidate.DistanceFromRawBeat)
                    .Cast<BoundaryGridCandidate?>()
                    .FirstOrDefault();
            }

            if (!selected.HasValue)
            {
                foreach (var denominator in PreferredBoundaryDenominators)
                {
                    var candidate = candidates.Values.FirstOrDefault(item =>
                        item.Beat.Denominator == denominator);
                    if (candidate.Beat.Denominator == 0 || candidate.Score < minimumAcousticScore ||
                        candidate.DistanceFromRawBeat > InitialGridCaptureRadius(denominator)) continue;
                    selected = candidate;
                    break;
                }
            }

            double acceptedSeconds;
            BeatPosition acceptedBeat;
            if (selected.HasValue)
            {
                acceptedSeconds = selected.Value.Seconds;
                acceptedBeat = selected.Value.Beat;
                establishedFraction = BeatPosition.Preserve(0, acceptedBeat.Numerator, acceptedBeat.Denominator);
            }
            else
            {
                acceptedSeconds = rawSeconds;
                acceptedBeat = SnapMappedBeatToPreferredGrid(rawBeat, 192);
                establishedFraction = null;
            }

            result.Add(segment with { StartSeconds = Math.Round(acceptedSeconds, 6) });
            previousBeat = acceptedBeat.Value;
            previousSeconds = acceptedSeconds;
            previousBpm = segment.Bpm;
        }
        return result;
    }

    private static double BoundaryScore(double[] signal, int rate, double center,
        double previousBpm, double nextBpm, double scoreWindow = 7)
    {
        var leftStart = Math.Max(0, (int)Math.Round((center - scoreWindow) * rate));
        var split = Math.Clamp((int)Math.Round(center * rate), 0, signal.Length);
        var rightEnd = Math.Min(signal.Length, (int)Math.Round((center + scoreWindow) * rate));
        var previousLeft = HarmonicCorrelation(signal, leftStart, split, 60d * rate / previousBpm);
        var nextLeft = HarmonicCorrelation(signal, leftStart, split, 60d * rate / nextBpm);
        var previousRight = HarmonicCorrelation(signal, split, rightEnd, 60d * rate / previousBpm);
        var nextRight = HarmonicCorrelation(signal, split, rightEnd, 60d * rate / nextBpm);
        return previousLeft - nextLeft + nextRight - previousRight;
    }

    private static bool SameFraction(BeatPosition beat, BeatPosition phase) =>
        new BeatPosition(0, beat.Numerator, beat.Denominator) ==
        new BeatPosition(0, phase.Numerator, phase.Denominator);

    private static double InitialGridCaptureRadius(int denominator) => denominator switch
    {
        1 => .20,
        2 => .18,
        <= 4 => .10,
        <= 8 => .065,
        <= 16 => .035,
        _ => Math.Max(.5 / denominator, .004)
    };

    private static BeatPosition SnapMappedBeatToPreferredGrid(double beat, int maximumDenominator)
    {
        var tolerance = .5 / maximumDenominator + 1e-9;
        foreach (var denominator in PreferredBoundaryDenominators)
        {
            if (denominator > maximumDenominator) continue;
            var candidate = BeatPosition.FromDouble(beat).Snap(denominator);
            if (Math.Abs(candidate.Value - beat) <= tolerance) return candidate;
        }
        return BeatPosition.FromDouble(beat).Snap(maximumDenominator);
    }

    private static List<TempoRun> BuildRuns(IReadOnlyList<TempoWindowEstimate> windows)
    {
        var result = new List<TempoRun>();
        if (windows.Count == 0) return result;
        var start = 0;
        for (var index = 1; index <= windows.Count; index++)
        {
            if (index < windows.Count && RelativeDifference(windows[index - 1].Bpm, windows[index].Bpm) <= .02) continue;
            result.Add(new TempoRun(start, index - 1));
            start = index;
        }
        return result;
    }

    private static double HarmonicCorrelation(double[] signal, int start, int end, double lag) =>
        NormalizedCorrelation(signal, start, end, lag * .5) * .2 +
        NormalizedCorrelation(signal, start, end, lag) +
        NormalizedCorrelation(signal, start, end, lag * 2) * .15;

    private static double NormalizedCorrelation(double[] signal, int start, int end, double lag)
    {
        lag = Math.Max(1, lag);
        var first = Math.Max(start + (int)Math.Ceiling(lag), (int)Math.Ceiling(lag));
        if (first >= end) return 0;
        var product = 0d;
        var leftSquares = 0d;
        var rightSquares = 0d;
        for (var index = first; index < end; index++)
        {
            var left = signal[index];
            var source = index - lag;
            var sourceLeft = Math.Max(0, (int)Math.Floor(source));
            var fraction = source - sourceLeft;
            var right = sourceLeft + 1 < signal.Length
                ? signal[sourceLeft] * (1 - fraction) + signal[sourceLeft + 1] * fraction
                : signal[sourceLeft];
            product += left * right;
            leftSquares += left * left;
            rightSquares += right * right;
        }
        return product / Math.Sqrt(Math.Max(1e-12, leftSquares * rightSquares));
    }

    private static double FindPhase(double[] signal, int start, int end, double lag)
    {
        lag = Math.Max(1, lag);
        var phaseSteps = Math.Max(1, (int)Math.Ceiling(lag * 2));
        var bestPhase = 0d;
        var bestScore = double.MinValue;
        for (var phaseIndex = 0; phaseIndex < phaseSteps; phaseIndex++)
        {
            var phase = phaseIndex * .5;
            var relative = (phase - start) % lag;
            if (relative < 0) relative += lag;
            var first = start + relative;
            var score = 0d;
            for (var position = first; position < end; position += lag)
            {
                var left = Math.Clamp((int)Math.Floor(position), start, end - 1);
                var fraction = position - left;
                var local = left + 1 < end
                    ? signal[left] * (1 - fraction) + signal[left + 1] * fraction
                    : signal[left];
                if (left > start) local = Math.Max(local, signal[left - 1] * .85);
                if (left + 1 < end) local = Math.Max(local, signal[left + 1] * .85);
                score += local;
            }
            if (score <= bestScore) continue;
            bestScore = score;
            bestPhase = phase;
        }
        return bestPhase;
    }

    private static List<int> FindOnsets(double[] signal, int rate, int start, int end)
    {
        var positive = new List<double>(Math.Max(0, end - start));
        for (var index = start; index < end; index++)
            if (signal[index] > 0) positive.Add(signal[index]);
        positive.Sort();
        var threshold = positive.Count == 0 ? 0 : positive[(int)((positive.Count - 1) * .8)];
        if (threshold <= 1e-12) return [];
        var result = new List<int>();
        var minimumDistance = Math.Max(1, rate / 12);
        for (var index = Math.Max(start + 1, 1); index < Math.Min(end - 1, signal.Length - 1); index++)
        {
            if (signal[index] < threshold || signal[index] < signal[index - 1] || signal[index] < signal[index + 1]) continue;
            if (result.Count > 0 && index - result[^1] < minimumDistance)
            {
                if (signal[index] > signal[result[^1]]) result[^1] = index;
                continue;
            }
            result.Add(index);
        }
        return result;
    }

    private static double CalculateInstability(IReadOnlyList<int> onsets, double phase, double lag)
    {
        if (onsets.Count == 0) return 1;
        var errors = onsets.Select(onset =>
        {
            var relative = (onset - phase) / lag;
            return Math.Abs(relative - Math.Round(relative));
        }).OrderBy(value => value).ToArray();
        return Math.Clamp(errors[errors.Length / 2] * 2, 0, 1);
    }

    private static double WeightedMedian(IEnumerable<(double Value, double Weight)> source)
    {
        var values = source.OrderBy(item => item.Value).ToArray();
        if (values.Length == 0) return 0;
        var half = values.Sum(item => item.Weight) / 2;
        var accumulated = 0d;
        foreach (var item in values)
        {
            accumulated += item.Weight;
            if (accumulated >= half) return item.Value;
        }
        return values[^1].Value;
    }

    private static double RelativeDifference(double left, double right) =>
        Math.Abs(left - right) / Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

    private readonly record struct TempoCandidate(double Bpm, double Instability,
        double NormalizedCorrelation, double DensityFit, double Rank);

    private readonly record struct BoundaryGridCandidate(
        BeatPosition Beat,
        double Seconds,
        double Score,
        double DistanceFromRawBeat);

    private readonly record struct TempoRun(int Start, int End)
    {
        public int Length => End >= Start ? End - Start + 1 : 0;
    }

    private sealed class TempoCluster
    {
        private double weightedBpm;
        public int Count { get; private set; }
        public double Weight { get; private set; }
        public double Bpm => Weight <= 0 ? 0 : weightedBpm / Weight;

        public void Add(TempoWindowEstimate window)
        {
            var weight = Math.Max(.05, window.Confidence);
            weightedBpm += window.Bpm * weight;
            Weight += weight;
            Count++;
        }
    }
}

public static class TempoTimelineApplicator
{
    /// <summary>
    /// Replaces only BPM-bearing timing data. Effect-only events are untouched;
    /// a combined BPM/Effect object keeps its identity and Effect fields while
    /// its BPM portion is replaced by a dedicated candidate event.
    /// </summary>
    public static int ReplaceBpmEvents(
        ChartDocument document,
        IReadOnlyList<TempoTimingCandidate> candidates,
        double initialOffsetMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateCandidates(candidates);
        var orderedCandidates = candidates.OrderBy(candidate => candidate.Beat).ToArray();

        var removed = 0;
        for (var index = document.Timing.Count - 1; index >= 0; index--)
        {
            var timing = document.Timing[index];
            if (!timing.Bpm.HasValue) continue;
            removed++;
            if (timing.Effect.HasValue)
            {
                timing.Bpm = null;
                timing.Delay = 0;
            }
            else document.Timing.RemoveAt(index);
        }

        for (var index = 0; index < orderedCandidates.Length; index++)
        {
            var candidate = orderedCandidates[index];
            document.Timing.Add(new TimingEvent
            {
                Beat = candidate.Beat,
                Bpm = candidate.Bpm,
                Delay = index == 0 && double.IsFinite(initialOffsetMilliseconds)
                    ? initialOffsetMilliseconds
                    : 0
            });
        }
        // Stable: List.Sort could swap two Scroll records at one beat, and
        // ScrollSpeedMap lets the last one win, changing the effective speed.
        var ordered = document.Timing
            .Select((timing, index) => (Timing: timing, Index: index))
            .OrderBy(entry => entry.Timing.Beat)
            .ThenBy(entry => entry.Timing.Bpm.HasValue ? 0 : 1)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Timing)
            .ToArray();
        document.Timing.Clear();
        document.Timing.AddRange(ordered);
        return removed;
    }

    /// <summary>
    /// Replaces the BPM timeline and creates deterministic scroll-speed
    /// compensation in the same document mutation. Scroll effects at old or new
    /// BPM boundaries are managed by this operation; other effects and scrolls at
    /// unrelated beats are left untouched.
    /// </summary>
    public static int ReplaceBpmEvents(
        ChartDocument document,
        IReadOnlyList<TempoTimingCandidate> candidates,
        double initialOffsetMilliseconds,
        double scrollCompensationReferenceBpm)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateCandidates(candidates);
        var orderedCandidates = candidates.OrderBy(candidate => candidate.Beat).ToArray();
        if (!double.IsFinite(scrollCompensationReferenceBpm) ||
            scrollCompensationReferenceBpm is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(scrollCompensationReferenceBpm),
                "The scroll-compensation reference BPM must be finite and between 0 and 10000.");

        var ratios = new double[orderedCandidates.Length];
        for (var index = 0; index < orderedCandidates.Length; index++)
        {
            var ratio = scrollCompensationReferenceBpm / orderedCandidates[index].Bpm;
            if (!double.IsFinite(ratio) || ratio is <= 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(scrollCompensationReferenceBpm),
                    "A scroll-compensation ratio must be finite, positive, and no greater than 100.");
            ratios[index] = ratio;
        }

        // Validate everything before the first write. The caller can safely use
        // this method directly as well as inside EditorSession.Mutate.
        var managedBeats = document.Timing
            .Where(timing => timing.Bpm.HasValue)
            .Select(timing => timing.Beat)
            .Concat(orderedCandidates.Select(candidate => candidate.Beat))
            .ToArray();

        // Generated compensation owns Scroll at every old/new BPM boundary.
        // Clearing the whole managed range avoids same-beat ordering ambiguity,
        // removes obsolete compensation when a boundary moves, and makes repeated
        // application semantically idempotent. Non-Scroll effects are untouched.
        for (var index = document.Timing.Count - 1; index >= 0; index--)
        {
            var timing = document.Timing[index];
            if (timing.Effect != TimingEffectKind.Scroll ||
                !managedBeats.Any(beat => beat.CompareTo(timing.Beat) == 0)) continue;
            timing.Effect = null;
            timing.EffectValue = 1;
            timing.EffectValueText = string.Empty;
            timing.EffectFieldName = string.Empty;
            if (!timing.Bpm.HasValue) document.Timing.RemoveAt(index);
        }

        var removed = ReplaceBpmEvents(document, orderedCandidates, initialOffsetMilliseconds);
        for (var index = 0; index < orderedCandidates.Length; index++)
        {
            var candidate = orderedCandidates[index];
            var timing = document.Timing.First(item =>
                item.Bpm.HasValue && item.Beat.CompareTo(candidate.Beat) == 0);
            timing.Effect = TimingEffectKind.Scroll;
            timing.EffectValue = ratios[index];
            timing.EffectValueText = ratios[index].ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            timing.EffectFieldName = string.Empty;
        }
        return removed;
    }

    private static void ValidateCandidates(IReadOnlyList<TempoTimingCandidate> candidates)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("BPM candidates cannot be empty.", nameof(candidates));
        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            if (candidate.Beat.Value < 0 || !double.IsFinite(candidate.Bpm) || candidate.Bpm is <= 0 or > 10_000)
                throw new ArgumentOutOfRangeException(nameof(candidates),
                    "Every BPM candidate must have a non-negative beat and a finite BPM between 0 and 10000.");
            if (candidates.Take(index).Any(previous => previous.Beat.CompareTo(candidate.Beat) == 0))
                throw new ArgumentException("BPM candidates cannot contain the same rational beat twice.", nameof(candidates));
        }
    }
}

// Ported from the MIT-licensed Azusa RC estimator distributed by
// LeoBlackMT/nonebot-plugin-osumania-toolkit. See THIRD-PARTY-NOTICES.md.
namespace LumaChart.Core.Analysis;

internal readonly record struct ReworkReferenceNote(int Column, double Milliseconds);

internal static class ReworkAzusaDifficultyReference
{
    private const int MinimumNoteCount = 80;
    private const double RowToleranceMilliseconds = 2;
    private const double LocalPower = 2.15;
    private const double LengthReferenceNotes = 600;
    private const double LengthExponent = .22;
    private const double LengthCap = 3.5;

    private static readonly double[] DecayWindowsMilliseconds = [140, 280, 560, 980];
    private static readonly double[] DecayWeights = [.34, .30, .22, .14];
    private static readonly string[] GreekLabels =
    [
        "Alpha", "Beta", "Gamma", "Delta", "Epsilon",
        "Emik Zeta", "Thaumiel Eta", "CloverWisp Theta", "Iota", "Kappa"
    ];
    private static readonly (string Suffix, double Offset)[] TierCandidates =
    [
        ("low", -.4),
        ("mid/low", -.2),
        ("mid", 0),
        ("mid/high", .2),
        ("high", .4)
    ];

    private static readonly double[] DanielDanMeans =
    [
        6.562, 6.957, 7.459, 7.939,
        9.095, 9.473, 10.162, 10.782
    ];

    private sealed class Tap(double time, int column)
    {
        public double Time { get; set; } = time;
        public int Column { get; } = column;
        public int Hand { get; } = column < 2 ? 0 : 1;
        public int RowSize { get; set; } = 1;
    }

    private sealed record Curve(
        double[] Speed,
        double[] Stamina,
        double[] Chord,
        double[] Tech,
        double[] Jack,
        double[] Times,
        double[] Density250,
        double[] Density500,
        double[] JackRaw,
        int[] ColumnCounts,
        int ChordNoteCount);

    private readonly record struct Summary(
        double Quantile97,
        double Quantile90,
        double Quantile75,
        double Quantile50,
        double TailMean);

    private readonly record struct CurveStats(
        double AnchorImbalance,
        double ChordRate,
        double JackQuantile95);

    private readonly record struct Blend(
        double? Value,
        double? LowGateSource,
        double? LowGate,
        double? HighGate,
        double? LowBase,
        double? HighBase);

    public static bool TryEstimate(
        IReadOnlyList<ReworkReferenceNote> notes,
        double speedRate,
        double sunnyStar,
        out string label)
    {
        label = string.Empty;
        if (notes.Count < MinimumNoteCount ||
            !double.IsFinite(speedRate) || speedRate <= 0 ||
            !double.IsFinite(sunnyStar))
        {
            return false;
        }

        var taps = notes
            .Where(note => note.Column is >= 0 and < 4 && double.IsFinite(note.Milliseconds))
            .Select(note => new Tap(note.Milliseconds / speedRate, note.Column))
            .OrderBy(note => note.Time)
            .ThenBy(note => note.Column)
            .ToArray();
        if (taps.Length < MinimumNoteCount) return false;

        AnnotateRows(taps, RowToleranceMilliseconds);
        var curve = BuildDifficultyCurve(taps);
        var primaryNumeric = ComputePrimaryNumeric(curve, taps.Length);
        var stats = ComputeCurveStats(curve, taps.Length);
        var sunnyNumeric = Round2(Clamp(2.85 + 1.33 * sunnyStar, -2, 20));

        // QQBot first converts Daniel's star result through the official Dan
        // means before Azusa blends it. Sunny and Daniel stars track closely in
        // the covered Alpha-Theta range, so applying the same boundaries to the
        // self-contained Sunny star is substantially closer than treating the
        // Sunny star as Daniel's out-of-range fallback value.
        var danielReference = EstimateDanielNumericReference(sunnyStar);
        var danielNumeric = danielReference.Value;
        var highest = Math.Max(primaryNumeric, Math.Max(sunnyNumeric, danielNumeric));
        var danielForBlend = danielNumeric;
        if (!danielReference.IsNative && highest < 14)
        {
            var speedDelta = speedRate - 1;
            var factor = speedDelta < 0
                ? Clamp(-speedDelta * .43, 0, 1)
                : Clamp(speedDelta * .35, 0, 1);
            danielForBlend *= factor;
        }

        var blend = ResolveBlend(primaryNumeric, danielForBlend, sunnyNumeric, stats);
        if (blend.Value is not { } blended || !double.IsFinite(blended)) return false;

        var calibrated = CalibrateNumeric(blended, blend.LowGate, blend.HighGate);
        var curveCorrection = ComputeCurveGapResidualCorrection(
            calibrated, blend, stats, primaryNumeric, sunnyNumeric, danielForBlend);
        var preOutput = Clamp(calibrated + curveCorrection, -2, 20);
        var output = CalibrateOutputNumeric(preOutput);
        var referenceCorrection = ComputeReferenceCorrection(output, danielForBlend, sunnyNumeric);
        var finalNumeric = Clamp(output + referenceCorrection, -2, 20);
        label = NumericToRcLabel(finalNumeric);
        return true;
    }

    private static void AnnotateRows(IReadOnlyList<Tap> taps, double toleranceMilliseconds)
    {
        var rowStart = 0;
        for (var index = 1; index <= taps.Count; index++)
        {
            if (index < taps.Count &&
                Math.Abs(taps[index].Time - taps[rowStart].Time) <= toleranceMilliseconds)
            {
                continue;
            }

            var rowSize = index - rowStart;
            for (var rowIndex = rowStart; rowIndex < index; rowIndex++)
                taps[rowIndex].RowSize = rowSize;
            rowStart = index;
        }
    }

    private static Curve BuildDifficultyCurve(IReadOnlyList<Tap> taps)
    {
        var state = new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            ["speed"] = new double[DecayWindowsMilliseconds.Length],
            ["stamina"] = new double[DecayWindowsMilliseconds.Length],
            ["chord"] = new double[DecayWindowsMilliseconds.Length],
            ["tech"] = new double[DecayWindowsMilliseconds.Length],
            ["jack"] = new double[DecayWindowsMilliseconds.Length]
        };
        var lastByColumn = Enumerable.Repeat(-1e9, 4).ToArray();
        var lastByHand = Enumerable.Repeat(-1e9, 2).ToArray();
        var density250 = new double[taps.Count];
        var density500 = new double[taps.Count];
        var jackRaw = new double[taps.Count];
        var columnCounts = new int[4];
        var chordNoteCount = 0;
        var speed = new double[taps.Count];
        var stamina = new double[taps.Count];
        var chord = new double[taps.Count];
        var tech = new double[taps.Count];
        var jack = new double[taps.Count];
        var times = new double[taps.Count];
        var previousTime = taps[0].Time;
        var previousA1 = -1e9;
        var previousA2 = -1e9;
        var previousColumn = 0;
        var cursor250 = 0;
        var cursor500 = 0;

        for (var index = 0; index < taps.Count; index++)
        {
            var tap = taps[index];
            var time = tap.Time;
            var column = tap.Column;
            columnCounts[column]++;
            if (tap.RowSize >= 2) chordNoteCount++;

            var gap = index > 0 ? Math.Max(0, time - previousTime) : 0;
            var sameColumnGap = Math.Max(0, time - lastByColumn[column]);
            var sameHandGap = Math.Max(0, time - lastByHand[tap.Hand]);
            var alternatingGap = Math.Max(0, time - previousA1);

            while (cursor250 < index && time - taps[cursor250].Time > 250) cursor250++;
            while (cursor500 < index && time - taps[cursor500].Time > 500) cursor500++;

            var value250 = (index - cursor250 + 1) / .25;
            var value500 = (index - cursor500 + 1) / .5;
            density250[index] = value250;
            density500[index] = value500;
            var jackValue = Math.Pow(190 / (sameColumnGap + 35), 1.16);
            jackRaw[index] = jackValue;
            var streamValue = Math.Pow(170 / (alternatingGap + 30), 1.07);
            var handStreamValue = Math.Pow(185 / (sameHandGap + 42), 1.08);
            var movement = Math.Abs(column - previousColumn) / 3d;
            var rhythmRatio = SafeDivide(
                Math.Max(alternatingGap, 1),
                Math.Max(time - previousA2, 1),
                1);
            var rhythmComplexity = Math.Abs(Math.Log2(Clamp(rhythmRatio, .2, 5)));
            var extraKeys = Math.Max(0, tap.RowSize - 1d);
            var chordValue = Math.Pow(extraKeys + 1, 1.22) - 1;

            var speedImpulse = .60 * streamValue + .30 * handStreamValue + .10 * jackValue;
            var jackImpulse = jackValue * (1 + .15 * chordValue);
            var staminaImpulse = .48 * (value500 / 11) + .27 * (value250 / 15) + .25 * streamValue;
            var chordImpulse = chordValue * (1 + .10 * Math.Min(1.5, streamValue));
            var techImpulse = .45 * rhythmComplexity + .30 * movement +
                              .25 * (extraKeys > 0 ? 1 + .3 * extraKeys : 0);

            for (var window = 0; window < DecayWindowsMilliseconds.Length; window++)
            {
                var decay = gap <= 0 ? 1 : Math.Exp(-gap / DecayWindowsMilliseconds[window]);
                state["speed"][window] = state["speed"][window] * decay + speedImpulse;
                state["stamina"][window] = state["stamina"][window] * decay + staminaImpulse;
                state["chord"][window] = state["chord"][window] * decay + chordImpulse;
                state["tech"][window] = state["tech"][window] * decay + techImpulse;
                state["jack"][window] = state["jack"][window] * decay + jackImpulse;
            }

            speed[index] = SkillFromState(state["speed"]);
            stamina[index] = SkillFromState(state["stamina"]);
            chord[index] = SkillFromState(state["chord"]);
            tech[index] = SkillFromState(state["tech"]);
            jack[index] = SkillFromState(state["jack"]);
            times[index] = time;

            previousA2 = previousA1;
            previousA1 = time;
            previousTime = time;
            previousColumn = column;
            lastByColumn[column] = time;
            lastByHand[tap.Hand] = time;
        }

        return new Curve(
            speed, stamina, chord, tech, jack, times,
            density250, density500, jackRaw, columnCounts, chordNoteCount);
    }

    private static double SkillFromState(IReadOnlyList<double> state)
    {
        var result = 0d;
        for (var index = 0; index < state.Count; index++)
            result += state[index] * DecayWeights[index];
        return result;
    }

    private static double ComputePrimaryNumeric(Curve curve, int noteCount)
    {
        var speed = Summarize(curve.Speed);
        var stamina = Summarize(curve.Stamina);
        var chord = Summarize(curve.Chord);
        var tech = Summarize(curve.Tech);
        var jack = Summarize(curve.Jack);
        var density250 = PowerMean(curve.Density250, 1.18);
        var density500 = PowerMean(curve.Density500, 1.12);
        var lengthBonus = Math.Min(
            LengthCap,
            Math.Pow(Math.Max(noteCount, 1) / LengthReferenceNotes, LengthExponent));

        var peak = .26 * speed.Quantile97 + .22 * stamina.Quantile97 +
                   .10 * chord.Quantile97 + .10 * tech.Quantile97 + .10 * jack.Quantile97 +
                   .06 * speed.Quantile90 + .04 * stamina.Quantile90 +
                   .02 * chord.Quantile90 + .02 * tech.Quantile90 + .02 * jack.Quantile90;
        var sustained = .18 * speed.Quantile75 + .16 * stamina.Quantile75 +
                        .08 * chord.Quantile75 + .06 * tech.Quantile75 + .08 * jack.Quantile75 +
                        .10 * speed.TailMean + .08 * stamina.TailMean +
                        .04 * chord.TailMean + .04 * tech.TailMean + .04 * jack.TailMean;
        var density = .14 * Math.Log(1 + density250) + .22 * Math.Log(1 + density500);
        var middle = .16 * speed.Quantile50 + .13 * stamina.Quantile50 +
                     .06 * chord.Quantile50 + .06 * tech.Quantile50 + .06 * jack.Quantile50;
        var raw = .52 * peak + .26 * sustained + .10 * density + .08 * middle + .04 * lengthBonus;
        var scaled = .82 + .43 * raw;

        var stats = ComputeCurveStats(curve, noteCount);
        var chordJackBoost = Clamp(
            2.5 * Clamp((stats.ChordRate - .40) * 3.5, 0, 1) *
            Clamp((stats.JackQuantile95 - 1.25) * 2.8, 0, 1) *
            Clamp(1 - stats.AnchorImbalance * 8, 0, 1),
            0,
            2.2);
        var durationSeconds = Math.Max(1, (curve.Times[^1] - curve.Times[0]) / 1000);
        var notesPerSecond = noteCount / durationSeconds;
        var midSpeedBoost = Clamp((notesPerSecond - 9) * .04, 0, .35) *
                            Clamp((19 - notesPerSecond) * .25, 0, 1);
        return Clamp(scaled + chordJackBoost + midSpeedBoost, -2, 20);
    }

    private static CurveStats ComputeCurveStats(Curve curve, int noteCount)
    {
        var maximumColumnCount = curve.ColumnCounts.Max();
        var anchorImbalance = SafeDivide(
            maximumColumnCount / (double)Math.Max(noteCount, 1) - .25,
            .75,
            0);
        var chordRate = SafeDivide(curve.ChordNoteCount, Math.Max(noteCount, 1), 0);
        var jackQuantile95 = Quantile(curve.JackRaw, .95);
        return new CurveStats(anchorImbalance, chordRate, jackQuantile95);
    }

    private static Summary Summarize(double[] values)
    {
        var sorted = values.Order().ToArray();
        var tailCount = Math.Max(8, (int)(sorted.Length * .04));
        var tail = sorted.Skip(Math.Max(0, sorted.Length - tailCount)).ToArray();
        return new Summary(
            QuantileSorted(sorted, .97),
            QuantileSorted(sorted, .90),
            QuantileSorted(sorted, .75),
            QuantileSorted(sorted, .50),
            tail.Length == 0 ? 0 : tail.Average());
    }

    private static Blend ResolveBlend(
        double primary,
        double daniel,
        double sunny,
        CurveStats stats)
    {
        var lowGateSource = daniel;
        var lowGate = Clamp((9.61 - lowGateSource) / 4.94, 0, 1);
        var highGate = 1 - lowGate;

        var lowBase = -8.317 + 1.536 * sunny + .011 * primary + .049 * daniel;
        if (lowGate > 0)
        {
            var primaryPeak = Math.Max(0, primary - 10.4);
            var sunnyPeak = Math.Max(0, sunny - 9.84);
            var lowSunnyCorrection = Math.Pow(Math.Max(0, 7.935 - sunny), 2);
            lowBase += lowGate * (.442 * sunnyPeak + .016 * primaryPeak + .235 * lowSunnyCorrection);
        }

        var highBase = .809 * daniel + .057 * primary + .165 * sunny + .183;
        var highMix = Clamp((lowGateSource - 14.83) / 2.667, 0, 1);
        if (highMix > 0)
        {
            highBase += highMix *
                        (-.154 * Math.Max(0, primary - daniel) +
                         .081 * Math.Max(0, sunny - daniel));
        }
        highBase += Clamp(
            .20 * Math.Max(0, stats.JackQuantile95 - 2.08) *
            Math.Max(0, .24 - stats.ChordRate) *
            Math.Max(0, stats.AnchorImbalance - .10),
            0,
            .25);

        var lowLift = Math.Max(0, 9.889 - lowGateSource) * .257;
        return new Blend(
            lowBase * lowGate + (highBase + lowLift) * highGate,
            lowGateSource,
            lowGate,
            highGate,
            lowBase,
            highBase);
    }

    private static double CalibrateNumeric(double value, double? lowGate, double? highGate)
    {
        var low = PiecewiseBlock(value, LowCalibrationBlocks);
        var high = PiecewiseBlock(value, HighCalibrationBlocks);
        var lowWeight = lowGate is { } lowValue && double.IsFinite(lowValue)
            ? Clamp(lowValue, 0, 1)
            : Math.Max(0, 1 - (highGate ?? 0));
        var highWeight = highGate is { } highValue && double.IsFinite(highValue)
            ? Clamp(highValue, 0, 1)
            : Math.Max(0, 1 - lowWeight);
        var total = lowWeight + highWeight;
        if (total <= 1e-6) return value < 11 ? low : high;
        return (lowWeight * low + highWeight * high) / total;
    }

    private static double ComputeCurveGapResidualCorrection(
        double calibrated,
        Blend blend,
        CurveStats stats,
        double primary,
        double sunny,
        double daniel)
    {
        var x = calibrated;
        var highGate = Clamp(blend.HighGate ?? 0, 0, 1);
        var danielSunny = daniel - sunny;
        var sunnyPrimary = sunny - primary;
        var residual = 4.335282 + -.170459 * x +
                       -1.622303 * Math.Max(0, 11 - x) +
                       1.328125 * Math.Max(0, 12.5 - x) +
                       -.042829 * Math.Max(0, 14 - x) +
                       -.834997 * highGate +
                       3.060352 * highGate * Math.Max(0, 11 - x) +
                       -1.744638 * highGate * Math.Max(0, 12.5 - x) +
                       .409922 * danielSunny + .041072 * sunnyPrimary +
                       -.388231 * highGate * danielSunny +
                       -.170185 * highGate * sunnyPrimary +
                       3.466868 * stats.AnchorImbalance +
                       -1.743778 * stats.ChordRate +
                       -.094758 * stats.JackQuantile95 +
                       2.626366 * stats.AnchorImbalance * stats.JackQuantile95 +
                       1.836357 * stats.ChordRate * stats.JackQuantile95 +
                       -2.612648 * highGate * stats.AnchorImbalance +
                       -2.493596 * highGate * stats.ChordRate;
        return Clamp(residual, -1.2, 1.2);
    }

    private static double ComputeReferenceCorrection(double output, double daniel, double sunny)
    {
        if (output is < 10 or > 17.5) return 0;
        double gate;
        double danielCoefficient;
        double sunnyCoefficient;
        if (output < 11.5)
        {
            gate = Clamp((output - 10) / 1.5, 0, 1);
            danielCoefficient = .10;
            sunnyCoefficient = .06;
        }
        else if (output < 12.5)
        {
            gate = 1;
            danielCoefficient = .20;
            sunnyCoefficient = .13;
        }
        else if (output < 16)
        {
            gate = 1;
            danielCoefficient = .40;
            sunnyCoefficient = .25;
        }
        else
        {
            gate = Clamp((17.5 - output) / 1.5, 0, 1);
            danielCoefficient = .28;
            sunnyCoefficient = .17;
        }

        var correction = danielCoefficient * (daniel - output) +
                         sunnyCoefficient * (sunny - output);
        return Clamp(correction * gate, -1.2, 1.2);
    }

    private static (double Value, bool IsNative) EstimateDanielNumericReference(
        double sunnyStar)
    {
        var boundaries = new (double Lower, double Upper)[DanielDanMeans.Length];
        for (var index = 0; index < DanielDanMeans.Length; index++)
        {
            var mean = DanielDanMeans[index];
            var lower = index > 0
                ? (DanielDanMeans[index - 1] + mean) / 2
                : mean - ((DanielDanMeans[1] + mean) / 2 - mean);
            var upper = index < DanielDanMeans.Length - 1
                ? (mean + DanielDanMeans[index + 1]) / 2
                : mean + (mean - DanielDanMeans[index - 1]) / 2;
            boundaries[index] = (lower, upper);
        }

        for (var index = 0; index < boundaries.Length; index++)
        {
            var boundary = boundaries[index];
            if (sunnyStar < boundary.Lower || sunnyStar >= boundary.Upper) continue;
            var progress = Clamp(
                SafeDivide(
                    sunnyStar - boundary.Lower,
                    boundary.Upper - boundary.Lower,
                    0),
                0,
                1);
            return (Round2(11 + index + progress), true);
        }

        var fallback = sunnyStar >= 6.56
            ? 11 + Clamp((sunnyStar - 6.56) / .58, 0, 9.99)
            : -2 + 13 * Math.Pow(Clamp(sunnyStar / 6.56, 0, 1), 1.72);
        return (Round2(fallback), false);
    }

    private static string NumericToRcLabel(double numeric)
    {
        var clamped = Clamp(numeric, -2.4, 20.4);
        var bestDistance = double.PositiveInfinity;
        var bestBase = -2;
        var bestSuffix = "low";
        for (var baseLevel = -2; baseLevel <= 20; baseLevel++)
        {
            foreach (var tier in TierCandidates)
            {
                var distance = Math.Abs(clamped - (baseLevel + tier.Offset));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestBase = baseLevel;
                bestSuffix = tier.Suffix;
            }
        }
        return $"{FormatRcBaseLabel(bestBase)} {bestSuffix}";
    }

    private static string FormatRcBaseLabel(int baseLevel)
    {
        if (baseLevel <= 0) return $"Intro {Math.Clamp(baseLevel + 3, 1, 3)}";
        if (baseLevel <= 10) return $"Reform {baseLevel}";
        return GreekLabels[Math.Clamp(baseLevel - 11, 0, GreekLabels.Length - 1)];
    }

    private static double PiecewiseLinear(double value, IReadOnlyList<double[]> knots)
    {
        if (value <= knots[0][0]) return knots[0][1];
        if (value >= knots[^1][0]) return knots[^1][1];
        for (var index = 0; index < knots.Count - 1; index++)
        {
            var left = knots[index];
            var right = knots[index + 1];
            if (value < left[0] || value > right[0]) continue;
            return left[1] + SafeDivide(
                (value - left[0]) * (right[1] - left[1]),
                right[0] - left[0],
                0);
        }
        return value;
    }

    private static double PiecewiseBlock(double value, IReadOnlyList<double[]> blocks)
    {
        if (value <= blocks[0][0]) return blocks[0][2];
        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (value >= block[0] && value <= block[1]) return block[2];
            if (index >= blocks.Count - 1) continue;
            var next = blocks[index + 1];
            if (value <= block[1] || value >= next[0]) continue;
            var ratio = SafeDivide(value - block[1], next[0] - block[1], 0);
            return block[2] * (1 - ratio) + next[2] * ratio;
        }
        return blocks[^1][2];
    }

    private static double CalibrateOutputNumeric(double value) =>
        PiecewiseLinear(value, IsotonicPoints);

    private static double Quantile(double[] values, double target) =>
        QuantileSorted(values.Order().ToArray(), target);

    private static double QuantileSorted(IReadOnlyList<double> sorted, double target)
    {
        if (sorted.Count == 0) return 0;
        var position = Clamp(target, 0, 1) * (sorted.Count - 1);
        var left = (int)Math.Floor(position);
        var right = Math.Min(sorted.Count - 1, left + 1);
        var weight = position - left;
        return sorted[left] * (1 - weight) + sorted[right] * weight;
    }

    private static double PowerMean(IEnumerable<double> values, double power)
    {
        var materialized = values as double[] ?? values.ToArray();
        if (materialized.Length == 0) return 0;
        return Math.Pow(
            materialized.Sum(value => Math.Pow(Math.Max(value, 0), power)) / materialized.Length,
            1 / power);
    }

    private static double SafeDivide(double numerator, double denominator, double fallback) =>
        double.IsFinite(numerator) && double.IsFinite(denominator) && Math.Abs(denominator) >= 1e-9
            ? numerator / denominator
            : fallback;

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Max(minimum, Math.Min(maximum, value));

    private static double Round2(double value) =>
        Math.Round(value, 2, MidpointRounding.ToEven);

    private static readonly double[][] LowCalibrationBlocks =
    [
        [1.9220, 1.9220, 1.0000], [2.3660, 2.7684, 1.6667], [2.8394, 2.8394, 2.0000],
        [2.8584, 3.7162, 2.3333], [3.7798, 3.7798, 3.0000], [3.8667, 3.8667, 3.0000],
        [4.2067, 5.2039, 4.3333], [5.2506, 5.7713, 5.0667], [5.8603, 6.1512, 5.3333],
        [6.3292, 6.8785, 6.0000], [7.1715, 7.3617, 6.2000], [7.4079, 7.8734, 7.2000],
        [8.0160, 8.4003, 8.2500], [8.4133, 8.4133, 9.0000], [8.9031, 9.4775, 9.5667],
        [9.6488, 9.6488, 10.0000], [9.8301, 9.8301, 10.3000]
    ];

    private static readonly double[][] HighCalibrationBlocks =
    [
        [11.4336, 11.4336, 10.4000], [11.4436, 11.4436, 10.5000], [11.6012, 11.6665, 10.6500],
        [11.6696, 12.2317, 11.5000], [12.3295, 12.3919, 11.7500], [12.5238, 12.5238, 12.0000],
        [12.5318, 12.8329, 12.1400], [12.8605, 12.9781, 12.2800], [12.9868, 13.1170, 12.7800],
        [13.2003, 13.4418, 12.7857], [13.4660, 13.5829, 12.9250], [13.6044, 13.9924, 13.3667],
        [14.0583, 14.0583, 13.4000], [14.0795, 14.2266, 13.4600], [14.2346, 14.2346, 13.6000],
        [14.2414, 14.2414, 13.7000], [14.2903, 14.2903, 14.0000], [14.3258, 14.4760, 14.1200],
        [14.5365, 14.6006, 14.1333], [14.7269, 14.8716, 14.1333], [15.0048, 15.0048, 14.4000],
        [15.0521, 15.0521, 14.4000], [15.0521, 15.0521, 14.4000], [15.0950, 15.0950, 14.4000],
        [15.2335, 15.2335, 14.4000], [15.2388, 15.5821, 14.7385], [15.6977, 15.7002, 14.8500],
        [15.7535, 16.1593, 15.0667], [16.2009, 16.2958, 15.1000], [16.3172, 16.4748, 15.7600],
        [16.5620, 16.9083, 15.9833], [16.9485, 16.9485, 16.0000], [17.0216, 17.3799, 16.1000],
        [17.4616, 17.4616, 16.4000], [17.5167, 17.5167, 16.4000], [17.5306, 17.9077, 16.6400],
        [18.1973, 18.1973, 17.2000], [18.2026, 18.2026, 17.2000], [18.4562, 19.3477, 17.9500]
    ];

    private static readonly double[][] IsotonicPoints =
    [
        [1.3868, 1.0000], [1.4574, 1.0000], [1.5361, 1.0000], [1.6320, 1.5000],
        [1.9833, 2.5800], [2.2465, 2.6000], [2.3344, 2.8000], [2.5779, 3.4500],
        [3.8277, 3.6000], [4.2824, 4.3429], [4.5665, 4.6250], [4.8016, 4.6750],
        [4.9529, 5.1500], [5.1029, 5.4000], [5.2475, 5.4750], [5.5039, 5.9000],
        [5.6951, 6.0143], [5.9213, 6.4000], [6.0093, 6.9000], [6.1337, 7.2000],
        [6.7092, 7.4400], [7.2846, 7.5000], [7.4233, 7.8000], [7.9790, 8.6000],
        [8.2927, 8.6143], [9.0829, 9.5000], [9.4639, 9.6154], [9.8115, 10.0000],
        [9.8344, 10.4000], [10.0013, 10.4000], [10.0778, 10.5000], [10.1054, 10.5000],
        [10.1435, 10.6000], [10.4782, 10.6462], [10.8866, 10.8000], [11.0934, 11.1727],
        [11.3266, 11.2867], [11.4970, 11.4000], [11.6024, 11.4750], [11.6947, 11.6000],
        [11.8932, 12.0636], [12.0076, 12.3000], [12.2947, 12.4150], [12.7583, 12.4500],
        [12.8756, 12.9000], [12.9268, 12.9000], [13.0042, 13.2000], [13.2387, 13.2694],
        [13.4620, 13.4400], [13.5467, 13.5000], [13.6016, 13.7375], [13.9609, 13.9500],
        [14.1414, 14.0250], [14.2226, 14.0762], [14.3178, 14.1273], [14.3786, 14.1643],
        [14.4421, 14.2182], [14.4825, 14.3000], [14.5063, 14.3750], [14.5452, 14.4778],
        [14.6359, 14.5850], [14.7301, 14.6389], [14.8846, 14.7906], [15.0424, 14.9263],
        [15.2159, 15.0944], [15.3942, 15.1875], [15.5380, 15.3300], [15.8096, 15.5320],
        [16.0262, 16.1000], [16.0702, 16.1000], [16.2738, 16.1267], [16.4723, 16.3579],
        [16.7156, 16.8000], [17.1446, 17.0600], [17.5478, 17.2000], [17.6403, 17.2000],
        [17.7603, 17.2000], [17.8264, 17.6000], [18.1258, 17.9750], [18.5000, 18.2000],
        [19.2000, 18.7000], [20.0000, 19.2000], [21.2000, 19.8000], [22.5000, 20.0000]
    ];
}

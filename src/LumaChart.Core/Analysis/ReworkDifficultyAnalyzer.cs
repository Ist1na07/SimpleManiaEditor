// Ported from the MIT-licensed Sunny Rework implementation used by
// LeoBlackMT/nonebot-plugin-osumania-toolkit. See THIRD-PARTY-NOTICES.md.
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Core.Analysis;

public sealed record ReworkDifficultyPeak(BeatPosition Beat, double Milliseconds, double Intensity);

public sealed record ReworkPatternContribution(string Name, double Ratio);

public sealed record ReworkDifficultyResult(
    double StarRating,
    string ReferenceDifficulty,
    int NoteCount,
    int LongNoteCount,
    double LongNoteRatio,
    double SpeedRate,
    double OverallDifficulty,
    IReadOnlyList<ReworkDifficultyPeak> Peaks,
    string PatternFamily,
    IReadOnlyList<ReworkPatternContribution> Patterns);

/// <summary>
/// Sunny/Star-Rating-Rebirth "Rework" estimator for 4-key mania charts.
/// Editor draft notes are deliberately excluded at the input boundary.
/// </summary>
public static class ReworkDifficultyAnalyzer
{
    private readonly record struct InputNote(int Column, int Hit, int Tail)
    {
        public bool IsLong => Tail >= 0;
    }

    private sealed record LnRepresentation(double[] Points, double[] Prefix, double[] Values);

    private static readonly double[] CrossCoefficients4K = [.175, .25, .05, .25, .175];

    public static ReworkDifficultyResult Analyze(
        ChartDocument document,
        double speedRate = 1,
        double overallDifficulty = 8)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.KeyCount != 4)
            throw new NotSupportedException("Rework 难度分析目前只支持 4K 谱面。");
        if (!double.IsFinite(speedRate) || speedRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(speedRate));
        if (!double.IsFinite(overallDifficulty))
            throw new ArgumentOutOfRangeException(nameof(overallDifficulty));
        overallDifficulty = Math.Clamp(overallDifficulty, 0, 10);

        var map = new TimingMap(document.Timing);
        var timeScale = 1d / speedRate;
        var officialNotes = document.Notes
            .Where(note => !note.IsDraft && note.Lane is >= 0 and < 4)
            .ToArray();
        var referenceNotes = officialNotes
            .Select(note => new ReworkReferenceNote(
                note.Lane,
                Math.Floor(map.BeatToMilliseconds(note.Beat))))
            .OrderBy(note => note.Milliseconds)
            .ThenBy(note => note.Column)
            .ToArray();
        var notes = officialNotes
            .Select(note =>
            {
                var hit = (int)Math.Floor(map.BeatToMilliseconds(note.Beat) * timeScale);
                var tail = note.IsHold
                    ? (int)Math.Floor(map.BeatToMilliseconds(note.EndBeat!.Value) * timeScale)
                    : -1;
                return new InputNote(note.Lane, hit, tail);
            })
            .OrderBy(note => note.Hit)
            .ThenBy(note => note.Column)
            .ToArray();

        if (notes.Length < 2)
            throw new InvalidOperationException("至少需要 2 个正式 Note 才能进行 Rework 难度分析。");

        var longNotes = notes.Where(note => note.IsLong).ToArray();
        var longNoteRatio = (double)longNotes.Length / notes.Length;
        var byColumn = Enumerable.Range(0, 4)
            .Select(column => notes.Where(note => note.Column == column).ToArray())
            .ToArray();
        var tails = longNotes.OrderBy(note => note.Tail).ToArray();
        var duration = Math.Max(1, notes.Max(note => Math.Max(note.Hit, note.Tail)) + 1);

        var hitWindow = .3 * Math.Sqrt((64.5 - Math.Ceiling(overallDifficulty * 3)) / 500);
        hitWindow = Math.Min(hitWindow, .6 * (hitWindow - .09) + .09);
        hitWindow = Math.Max(hitWindow, .001);

        GetCorners(duration, notes, out var allCorners, out var baseCorners, out var aCorners);
        var keyUsage = GetKeyUsage(duration, notes, baseCorners);
        var activeColumns = Enumerable.Range(0, baseCorners.Length)
            .Select(index => Enumerable.Range(0, 4).Where(column => keyUsage[column][index]).ToArray())
            .ToArray();
        var anchor = ComputeAnchor(GetKeyUsage400(duration, notes, baseCorners));
        var (columnDeltas, jBase) = ComputeJ(hitWindow, byColumn, baseCorners);
        var j = Interpolate(allCorners, baseCorners, jBase);
        var x = Interpolate(allCorners, baseCorners,
            ComputeCross(hitWindow, byColumn, activeColumns, baseCorners));
        var lnRepresentation = BuildLongNoteRepresentation(longNotes, duration);
        var p = Interpolate(allCorners, baseCorners,
            ComputePattern(hitWindow, notes, lnRepresentation, anchor, baseCorners));
        var a = Interpolate(allCorners, aCorners,
            ComputeCoordination(activeColumns, columnDeltas, aCorners, baseCorners));
        var r = Interpolate(allCorners, baseCorners,
            ComputeRelease(hitWindow, byColumn, tails, baseCorners));
        var (densityBase, activeKeyBase) = ComputeDensityAndActiveKeys(notes, keyUsage, baseCorners);
        var density = StepInterpolate(allCorners, baseCorners, densityBase);
        var activeKeys = StepInterpolate(allCorners, baseCorners, activeKeyBase);

        var difficulty = new double[allCorners.Length];
        for (var i = 0; i < difficulty.Length; i++)
        {
            var coordinationJack = Math.Pow(a[i], 3 / activeKeys[i]) * Math.Min(j[i], 8 + .85 * j[i]);
            var patternRelease = Math.Pow(a[i], 2d / 3) * (.8 * p[i] + r[i] * 35 / (density[i] + 8));
            var strain = Math.Pow(.4 * Math.Pow(Math.Max(0, coordinationJack), 1.5) +
                                  .6 * Math.Pow(Math.Max(0, patternRelease), 1.5), 2d / 3);
            var technical = Math.Pow(a[i], 3 / activeKeys[i]) * x[i] / (x[i] + strain + 1);
            var value = 2.7 * Math.Sqrt(Math.Max(0, strain)) * Math.Pow(Math.Max(0, technical), 1.5) + strain * .27;
            difficulty[i] = double.IsFinite(value) ? Math.Max(0, value) : 0;
        }

        var starRating = AggregateStarRating(notes, longNotes, allCorners, density, difficulty);
        var peaks = BuildPeaks(map, speedRate, allCorners, density, difficulty);
        var (patternFamily, patterns) = AnalyzePatterns(notes);
        return new ReworkDifficultyResult(
            starRating,
            ReworkDifficultyReference.Estimate(
                starRating,
                longNoteRatio,
                referenceNotes,
                speedRate),
            notes.Length,
            longNotes.Length,
            longNoteRatio,
            speedRate,
            overallDifficulty,
            peaks,
            patternFamily,
            patterns);
    }

    private static (string Family, IReadOnlyList<ReworkPatternContribution> Contributions) AnalyzePatterns(InputNote[] notes)
    {
        var events = notes
            .GroupBy(note => note.Hit)
            .Select(group => new
            {
                Hit = group.Key,
                Mask = group.Aggregate(0, (mask, note) => mask | (1 << note.Column)),
                HasLong = group.Any(note => note.IsLong)
            })
            .OrderBy(item => item.Hit)
            .ToArray();
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        static int KeyCount(int mask)
        {
            var count = 0;
            while (mask != 0)
            {
                count += mask & 1;
                mask >>= 1;
            }
            return count;
        }

        void Add(string name, double value)
        {
            scores[name] = scores.GetValueOrDefault(name) + value;
        }

        for (var i = 1; i < events.Length; i++)
        {
            var current = events[i];
            var previous = events[i - 1];
            var delta = Math.Max(1, current.Hit - previous.Hit);
            // Dense transitions contribute more, while slow isolated notes still remain visible.
            var weight = Math.Clamp(220d / delta, .35, 2.5);
            var currentKeys = KeyCount(current.Mask);
            var previousKeys = KeyCount(previous.Mask);
            var repeatedKeys = KeyCount(current.Mask & previous.Mask);

            if (repeatedKeys > 0)
            {
                if (current.HasLong || previous.HasLong) Add("Longjack", weight);
                else if (currentKeys > 1 || previousKeys > 1) Add("Chordjack", weight);
                else if (delta <= 140) Add("Minijack", weight);
                else Add("Jack", weight);
                continue;
            }

            if (currentKeys >= 4)
            {
                Add("Quadstream", weight);
            }
            else if (currentKeys == 3)
            {
                Add("Handstream", weight);
            }
            else if (currentKeys == 2)
            {
                var alternatingJump = i >= 2 && previousKeys == 2 && events[i - 2].Mask == current.Mask;
                Add(alternatingJump ? "Jumptrill" : "Jumpstream", weight);
            }
            else
            {
                var alternatingSingle = i >= 2 && previousKeys == 1 && events[i - 2].Mask == current.Mask;
                Add(alternatingSingle ? "Trill" : "Stream", weight);
            }
        }

        if (scores.Count == 0)
            return ("Stream", [new ReworkPatternContribution("Stream", 1)]);

        var total = scores.Values.Sum();
        var contributions = scores
            .Select(item => new ReworkPatternContribution(item.Key, item.Value / total))
            .OrderByDescending(item => item.Ratio)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var dominant = contributions[0].Name;
        var family = dominant is "Jack" or "Minijack" or "Longjack" or "Chordjack"
            ? "Jacks"
            : dominant is "Jumpstream" or "Jumptrill" or "Handstream" or "Quadstream"
                ? "Chordstream"
                : "Stream";
        return (family, contributions);
    }

    private static void GetCorners(int duration, InputNote[] notes, out double[] all, out double[] baseline, out double[] coordination)
    {
        var baseSet = new HashSet<int> { 0, duration };
        var aSet = new HashSet<int> { 0, duration };
        foreach (var note in notes)
        {
            baseSet.Add(note.Hit);
            aSet.Add(note.Hit);
            if (note.IsLong)
            {
                baseSet.Add(note.Tail);
                aSet.Add(note.Tail);
            }
        }
        foreach (var point in baseSet.ToArray())
        {
            baseSet.Add(point + 501);
            baseSet.Add(point - 499);
            baseSet.Add(point + 1);
        }
        foreach (var point in aSet.ToArray())
        {
            aSet.Add(point + 1000);
            aSet.Add(point - 1000);
        }
        baseline = baseSet.Where(point => point >= 0 && point <= duration).Order().Select(point => (double)point).ToArray();
        coordination = aSet.Where(point => point >= 0 && point <= duration).Order().Select(point => (double)point).ToArray();
        all = baseline.Concat(coordination).Distinct().Order().ToArray();
    }

    private static bool[][] GetKeyUsage(int duration, InputNote[] notes, double[] corners)
    {
        var result = Enumerable.Range(0, 4).Select(_ => new bool[corners.Length]).ToArray();
        foreach (var note in notes)
        {
            var start = Math.Max(note.Hit - 150, 0);
            var end = note.IsLong ? Math.Min(note.Tail + 150, duration - 1) : note.Hit + 150;
            var left = LowerBound(corners, start);
            var right = LowerBound(corners, end);
            for (var i = left; i < right; i++) result[note.Column][i] = true;
        }
        return result;
    }

    private static double[][] GetKeyUsage400(int duration, InputNote[] notes, double[] corners)
    {
        var result = Enumerable.Range(0, 4).Select(_ => new double[corners.Length]).ToArray();
        foreach (var note in notes)
        {
            var start = Math.Max(note.Hit, 0);
            var end = note.IsLong ? Math.Min(note.Tail, duration - 1) : note.Hit;
            var left400 = LowerBound(corners, start - 400);
            var left = LowerBound(corners, start);
            var right = LowerBound(corners, end);
            var right400 = LowerBound(corners, end + 400);
            var body = 3.75 + Math.Min(end - start, 1500) / 150d;
            for (var i = left; i < right; i++) result[note.Column][i] += body;
            for (var i = left400; i < left; i++)
                result[note.Column][i] += 3.75 - 3.75 / 160000 * Math.Pow(corners[i] - start, 2);
            for (var i = right; i < right400; i++)
                result[note.Column][i] += 3.75 - 3.75 / 160000 * Math.Pow(corners[i] - end, 2);
        }
        return result;
    }

    private static double[] ComputeAnchor(double[][] usage)
    {
        var result = new double[usage[0].Length];
        for (var i = 0; i < result.Length; i++)
        {
            var counts = Enumerable.Range(0, 4).Select(column => usage[column][i]).OrderDescending().ToArray();
            var nonZero = counts.Count(value => value > 0);
            var walk = 0d;
            var maxWalk = 0d;
            for (var k = 0; k < counts.Length - 1; k++)
            {
                if (counts[k] <= 0 || counts[k + 1] <= 0) continue;
                var ratio = counts[k + 1] / counts[k];
                var weight = 1 - 4 * Math.Pow(.5 - ratio, 2);
                walk += counts[k] * weight;
                maxWalk += counts[k];
            }
            var raw = nonZero > 1 ? walk / Math.Max(maxWalk, 1e-9) : 0;
            result[i] = 1 + Math.Min(raw - .18, 5 * Math.Pow(raw - .22, 3));
        }
        return result;
    }

    private static (double[][] Deltas, double[] Smoothed) ComputeJ(double hitWindow, InputNote[][] columns, double[] corners)
    {
        var values = Enumerable.Range(0, 4).Select(_ => new double[corners.Length]).ToArray();
        var deltas = Enumerable.Range(0, 4).Select(_ => Enumerable.Repeat(1e9, corners.Length).ToArray()).ToArray();
        for (var column = 0; column < 4; column++)
        {
            for (var n = 0; n < columns[column].Length - 1; n++)
            {
                var start = columns[column][n].Hit;
                var end = columns[column][n + 1].Hit;
                var delta = Math.Max(1e-6, .001 * (end - start));
                var nerf = 1 - 7e-5 * Math.Pow(.15 + Math.Abs(delta - .08), -4);
                var value = Math.Pow(delta, -1) * Math.Pow(delta + .11 * Math.Pow(hitWindow, .25), -1) * nerf;
                var left = LowerBound(corners, start);
                var right = LowerBound(corners, end);
                for (var i = left; i < right; i++)
                {
                    values[column][i] = value;
                    deltas[column][i] = delta;
                }
            }
        }
        var smoothedByColumn = values.Select(value => Smooth(corners, value, 500, .001, false)).ToArray();
        var result = new double[corners.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var numerator = 0d;
            var denominator = 0d;
            for (var column = 0; column < 4; column++)
            {
                var weight = 1 / deltas[column][i];
                numerator += Math.Pow(Math.Max(smoothedByColumn[column][i], 0), 5) * weight;
                denominator += weight;
            }
            result[i] = Math.Pow(numerator / Math.Max(denominator, 1e-9), .2);
        }
        return (deltas, result);
    }

    private static double[] ComputeCross(double hitWindow, InputNote[][] columns, int[][] active, double[] corners)
    {
        var values = Enumerable.Range(0, 5).Select(_ => new double[corners.Length]).ToArray();
        var fast = Enumerable.Range(0, 5).Select(_ => new double[corners.Length]).ToArray();
        for (var boundary = 0; boundary <= 4; boundary++)
        {
            var pair = boundary switch
            {
                0 => columns[0],
                4 => columns[3],
                _ => columns[boundary - 1].Concat(columns[boundary]).OrderBy(note => note.Hit).ToArray()
            };
            for (var n = 1; n < pair.Length; n++)
            {
                var start = pair[n - 1].Hit;
                var end = pair[n].Hit;
                var left = LowerBound(corners, start);
                var right = LowerBound(corners, end);
                if (right <= left) continue;
                var delta = .001 * (end - start);
                var value = .16 * Math.Pow(Math.Max(hitWindow, delta), -2);
                var leftInactive = !Contains(active[left], boundary - 1) && !Contains(active[Math.Min(right, active.Length - 1)], boundary - 1);
                var rightInactive = !Contains(active[left], boundary) && !Contains(active[Math.Min(right, active.Length - 1)], boundary);
                if (leftInactive || rightInactive) value *= 1 - CrossCoefficients4K[boundary];
                var fastValue = Math.Max(0, .4 * Math.Pow(Math.Max(delta, Math.Max(.06, .75 * hitWindow)), -2) - 80);
                for (var i = left; i < right; i++)
                {
                    values[boundary][i] = value;
                    fast[boundary][i] = fastValue;
                }
            }
        }
        var combined = new double[corners.Length];
        for (var i = 0; i < combined.Length; i++)
        {
            for (var boundary = 0; boundary <= 4; boundary++)
                combined[i] += values[boundary][i] * CrossCoefficients4K[boundary];
            for (var boundary = 0; boundary < 4; boundary++)
                combined[i] += Math.Sqrt(fast[boundary][i] * CrossCoefficients4K[boundary] *
                                         fast[boundary + 1][i] * CrossCoefficients4K[boundary + 1]);
        }
        return Smooth(corners, combined, 500, .001, false);
    }

    private static LnRepresentation BuildLongNoteRepresentation(InputNote[] longNotes, int duration)
    {
        var differences = new Dictionary<int, double>();
        static void Add(Dictionary<int, double> target, int point, double value) =>
            target[point] = target.GetValueOrDefault(point) + value;
        foreach (var note in longNotes)
        {
            Add(differences, Math.Min(note.Hit + 60, note.Tail), 1.3);
            Add(differences, Math.Min(note.Hit + 120, note.Tail), -.3);
            Add(differences, note.Tail, -1);
        }
        var points = differences.Keys.Append(0).Append(duration).Distinct().Order().Select(value => (double)value).ToArray();
        var values = new double[Math.Max(1, points.Length - 1)];
        var prefix = new double[points.Length];
        var current = 0d;
        for (var i = 0; i < points.Length - 1; i++)
        {
            if (differences.TryGetValue((int)points[i], out var change)) current += change;
            values[i] = Math.Min(current, 2.5 + .5 * current);
            prefix[i + 1] = prefix[i] + (points[i + 1] - points[i]) * values[i];
        }
        return new LnRepresentation(points, prefix, values);
    }

    private static double LongNoteSum(double start, double end, LnRepresentation representation)
    {
        if (end <= start || representation.Values.Length == 0) return 0;
        var points = representation.Points;
        start = Math.Clamp(start, points[0], points[^1]);
        end = Math.Clamp(end, points[0], points[^1]);
        if (end <= start) return 0;
        var left = Math.Clamp(UpperBound(points, start) - 1, 0, representation.Values.Length - 1);
        var right = Math.Clamp(UpperBound(points, end) - 1, 0, representation.Values.Length - 1);
        if (left == right) return (end - start) * representation.Values[left];
        return (points[left + 1] - start) * representation.Values[left] +
               representation.Prefix[right] - representation.Prefix[left + 1] +
               (end - points[right]) * representation.Values[right];
    }

    private static double[] ComputePattern(double hitWindow, InputNote[] notes, LnRepresentation ln,
        double[] anchor, double[] corners)
    {
        var step = new double[corners.Length];
        for (var n = 0; n < notes.Length - 1; n++)
        {
            var leftHit = notes[n].Hit;
            var rightHit = notes[n + 1].Hit;
            var deltaTime = rightHit - leftHit;
            if (deltaTime == 0)
            {
                var spike = 1000 * Math.Pow(.02 * (4 / hitWindow - 24), .25);
                var left = LowerBound(corners, leftHit);
                var right = UpperBound(corners, leftHit);
                for (var i = left; i < right; i++) step[i] += spike;
                continue;
            }
            var start = LowerBound(corners, leftHit);
            var end = LowerBound(corners, rightHit);
            if (end <= start) continue;
            var delta = Math.Max(1e-6, .001 * deltaTime);
            var longNoteBoost = 1 + .006 * LongNoteSum(leftHit, rightHit, ln);
            var streamBoost = StreamBooster(delta);
            var curveOffset = delta < 2 * hitWindow / 3 ? delta - hitWindow / 2 : hitWindow / 6;
            var curve = Math.Max(0, 1 - 24 / hitWindow * curveOffset * curveOffset);
            var increase = Math.Pow(delta, -1) * Math.Pow(.08 / hitWindow * curve, .25) * Math.Max(streamBoost, longNoteBoost);
            for (var i = start; i < end; i++)
                step[i] += Math.Min(increase * anchor[i], Math.Max(increase, increase * 2 - 10));
        }
        return Smooth(corners, step, 500, .001, false);
    }

    private static double StreamBooster(double delta)
    {
        var rate = 7.5 / delta;
        return rate is > 160 and < 360
            ? 1 + 1.7e-7 * (rate - 160) * Math.Pow(rate - 360, 2)
            : 1;
    }

    private static double[] ComputeCoordination(int[][] active, double[][] deltas, double[] aCorners, double[] baseCorners)
    {
        var difference = Enumerable.Range(0, 3).Select(_ => new double[baseCorners.Length]).ToArray();
        for (var i = 0; i < baseCorners.Length; i++)
        {
            var columns = active[i];
            for (var pair = 0; pair < columns.Length - 1; pair++)
            {
                var first = columns[pair];
                var second = columns[pair + 1];
                difference[first][i] = Math.Abs(deltas[first][i] - deltas[second][i]) +
                                       .4 * Math.Max(0, Math.Max(deltas[first][i], deltas[second][i]) - .11);
            }
        }
        var step = Enumerable.Repeat(1d, aCorners.Length).ToArray();
        for (var i = 0; i < aCorners.Length; i++)
        {
            var index = Math.Clamp(LowerBound(baseCorners, aCorners[i]), 0, baseCorners.Length - 1);
            var columns = active[index];
            for (var pair = 0; pair < columns.Length - 1; pair++)
            {
                var first = columns[pair];
                var second = columns[pair + 1];
                var value = difference[first][index];
                var maximumDelta = Math.Max(deltas[first][index], deltas[second][index]);
                if (value < .02) step[i] *= Math.Min(.75 + .5 * maximumDelta, 1);
                else if (value < .07) step[i] *= Math.Min(.65 + 5 * value + .5 * maximumDelta, 1);
            }
        }
        return Smooth(aCorners, step, 250, 1, true);
    }

    private static double[] ComputeRelease(double hitWindow, InputNote[][] columns, InputNote[] tails, double[] corners)
    {
        var release = new double[corners.Length];
        if (tails.Length < 2) return release;
        var nextHits = new Dictionary<InputNote, int>[columns.Length];
        for (var columnIndex = 0; columnIndex < columns.Length; columnIndex++)
        {
            var column = columns[columnIndex];
            var lookup = new Dictionary<InputNote, int>();
            for (var noteIndex = 0; noteIndex < column.Length; noteIndex++)
                lookup.TryAdd(column[noteIndex], noteIndex + 1 < column.Length ? column[noteIndex + 1].Hit : 1_000_000_000);
            nextHits[columnIndex] = lookup;
        }
        var indices = new double[tails.Length];
        for (var i = 0; i < tails.Length; i++)
        {
            var note = tails[i];
            var nextHit = nextHits[note.Column].GetValueOrDefault(note, 1_000_000_000);
            var head = .001 * Math.Abs(note.Tail - note.Hit - 80) / hitWindow;
            var tail = .001 * Math.Abs(nextHit - note.Tail - 80) / hitWindow;
            indices[i] = 2 / (2 + Math.Exp(-5 * (head - .75)) + Math.Exp(-5 * (tail - .75)));
        }
        for (var i = 0; i < tails.Length - 1; i++)
        {
            var start = LowerBound(corners, tails[i].Tail);
            var end = LowerBound(corners, tails[i + 1].Tail);
            if (end <= start) continue;
            var delta = Math.Max(1e-6, .001 * (tails[i + 1].Tail - tails[i].Tail));
            var value = .08 * Math.Pow(delta, -.5) / hitWindow * (1 + .8 * (indices[i] + indices[i + 1]));
            for (var j = start; j < end; j++) release[j] = value;
        }
        return Smooth(corners, release, 500, .001, false);
    }

    private static (double[] Density, double[] ActiveKeys) ComputeDensityAndActiveKeys(
        InputNote[] notes, bool[][] usage, double[] corners)
    {
        var hits = notes.Select(note => (double)note.Hit).Order().ToArray();
        var density = new double[corners.Length];
        var active = new double[corners.Length];
        for (var i = 0; i < corners.Length; i++)
        {
            density[i] = LowerBound(hits, corners[i] + 500) - LowerBound(hits, corners[i] - 500);
            active[i] = Math.Max(1, Enumerable.Range(0, 4).Count(column => usage[column][i]));
        }
        return (density, active);
    }

    private static double AggregateStarRating(InputNote[] notes, InputNote[] longNotes,
        double[] corners, double[] density, double[] difficulty)
    {
        var gaps = new double[corners.Length];
        gaps[0] = (corners[1] - corners[0]) / 2;
        gaps[^1] = (corners[^1] - corners[^2]) / 2;
        for (var i = 1; i < gaps.Length - 1; i++) gaps[i] = (corners[i + 1] - corners[i - 1]) / 2;
        var samples = difficulty.Select((value, index) => (Value: value, Weight: density[index] * gaps[index]))
            .OrderBy(sample => sample.Value).ToArray();
        var totalWeight = samples.Sum(sample => sample.Weight);
        if (!double.IsFinite(totalWeight) || totalWeight <= 0) return 0;
        var cumulative = new double[samples.Length];
        var sum = 0d;
        for (var i = 0; i < samples.Length; i++) cumulative[i] = (sum += samples[i].Weight) / totalWeight;
        double Quantile(double target)
        {
            var index = Array.FindIndex(cumulative, value => value >= target);
            return samples[index < 0 ? samples.Length - 1 : index].Value;
        }
        var percentile93 = new[] { .945, .935, .925, .915 }.Average(Quantile);
        var percentile83 = new[] { .845, .835, .825, .815 }.Average(Quantile);
        var weightedMean = Math.Pow(samples.Sum(sample => Math.Pow(sample.Value, 5) * sample.Weight) / totalWeight, .2);
        var rating = .25 * .88 * percentile93 + .2 * .94 * percentile83 + .55 * weightedMean;
        var effectiveNotes = notes.Length + longNotes.Sum(note => .5 * Math.Min(note.Tail - note.Hit, 1000) / 200d);
        rating *= effectiveNotes / (effectiveNotes + 60);
        if (rating > 9) rating = 9 + (rating - 9) / 1.2;
        rating *= .975;
        return double.IsFinite(rating) ? Math.Max(0, rating) : 0;
    }

    private static IReadOnlyList<ReworkDifficultyPeak> BuildPeaks(TimingMap map, double speedRate,
        double[] corners, double[] density, double[] difficulty)
    {
        var selected = new List<int>();
        foreach (var candidate in Enumerable.Range(0, corners.Length)
                     .Where(index => density[index] > 0 && difficulty[index] > 0)
                     .OrderByDescending(index => difficulty[index]))
        {
            if (selected.Any(index => Math.Abs(corners[index] - corners[candidate]) < 1000)) continue;
            selected.Add(candidate);
            if (selected.Count == 8) break;
        }
        return selected.Select(index =>
        {
            var originalMilliseconds = corners[index] * speedRate;
            return new ReworkDifficultyPeak(map.MillisecondsToBeat(originalMilliseconds), originalMilliseconds, difficulty[index]);
        }).ToArray();
    }

    private static double[] Smooth(double[] x, double[] values, double window, double scale, bool average)
    {
        var prefix = new double[x.Length];
        for (var i = 1; i < x.Length; i++)
            prefix[i] = prefix[i - 1] + values[i - 1] * (x[i] - x[i - 1]);
        var result = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            var start = Math.Clamp(x[i] - window, x[0], x[^1]);
            var end = Math.Clamp(x[i] + window, x[0], x[^1]);
            var integral = QueryIntegral(end, x, prefix, values) - QueryIntegral(start, x, prefix, values);
            result[i] = average ? (end > start ? integral / (end - start) : 0) : scale * integral;
        }
        return result;
    }

    private static double QueryIntegral(double query, double[] x, double[] prefix, double[] values)
    {
        var index = Math.Clamp(LowerBound(x, query) - 1, 0, x.Length - 2);
        return prefix[index] + values[index] * (query - x[index]);
    }

    private static double[] Interpolate(double[] queries, double[] x, double[] values)
    {
        var result = new double[queries.Length];
        for (var i = 0; i < queries.Length; i++)
        {
            var query = queries[i];
            if (query <= x[0]) { result[i] = values[0]; continue; }
            if (query >= x[^1]) { result[i] = values[^1]; continue; }
            var right = LowerBound(x, query);
            var left = right - 1;
            var ratio = (query - x[left]) / (x[right] - x[left]);
            result[i] = values[left] + (values[right] - values[left]) * ratio;
        }
        return result;
    }

    private static double[] StepInterpolate(double[] queries, double[] x, double[] values)
    {
        var result = new double[queries.Length];
        for (var i = 0; i < queries.Length; i++)
            result[i] = values[Math.Clamp(UpperBound(x, queries[i]) - 1, 0, values.Length - 1)];
        return result;
    }

    private static bool Contains(int[] values, int value) => value is >= 0 and < 4 && Array.IndexOf(values, value) >= 0;

    private static int LowerBound(double[] values, double target)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (values[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(double[] values, double target)
    {
        var low = 0;
        var high = values.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (values[middle] <= target) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

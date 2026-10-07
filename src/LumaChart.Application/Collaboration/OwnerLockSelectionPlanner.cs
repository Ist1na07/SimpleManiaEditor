using LumaChart.Core.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Application.Collaboration;

/// <summary>
/// An exact, end-exclusive Beat range selected for an owner lock.
/// </summary>
public readonly record struct OwnerLockSelectionRange(
    BeatPosition StartBeat,
    BeatPosition EndBeat)
{
    public bool Intersects(OwnerProtectedRegion region) =>
        StartBeat.CompareTo(region.EndBeat) < 0 &&
        EndBeat.CompareTo(region.StartBeat) > 0;
}

/// <summary>
/// Plans exact single-object locks, continuous multi-object selection locks, and
/// disjoint unions used by automatic locking and coverage/difference operations.
/// </summary>
public static class OwnerLockSelectionPlanner
{
    public const int MaximumAutomaticLockRanges = 4096;
    public const int EmptyGapMeasureBeats = 4;

    /// <summary>
    /// Builds the end-exclusive row occupied by a point Note/Effect. The authored
    /// denominator and active editor division are expanded to their least common
    /// denominator, then exactly one row of that shared grid is protected. Thus a
    /// 5/6 point viewed at 1/4 covers 10/12–11/12, not three 1/12 rows and not an
    /// arbitrarily tiny protocol interval.
    /// </summary>
    public static OwnerLockSelectionRange BuildPointRange(BeatPosition beat, int division)
    {
        ValidateDivision(division);

        var beatDenominator = beat.Denominator == 0 ? 1 : beat.Denominator;
        if (beatDenominator > BeatPosition.MaximumPreservedDenominator)
            return new OwnerLockSelectionRange(
                beat,
                new BeatPosition(beat.Bar, checked(beat.Numerator + 1), beatDenominator));
        var gcd = GreatestCommonDivisor(beatDenominator, division);
        var commonDenominator = checked((long)beatDenominator / gcd * division);
        if (commonDenominator > BeatPosition.MaximumPreservedDenominator)
        {
            // Some imported rational positions are relatively prime to the active
            // division, so their LCM exceeds the wire-format denominator limit.
            // Preserve the authored point exactly and use its finest representable
            // positive row instead of throwing from a WPF selection event or
            // widening the lock to several visible rows.
            var fallbackScale = BeatPosition.MaximumPreservedDenominator / beatDenominator;
            var fallbackDenominator = checked(beatDenominator * fallbackScale);
            var fallbackNumerator = checked(beat.Numerator * fallbackScale);
            return new OwnerLockSelectionRange(
                beat,
                BeatPosition.Preserve(
                    beat.Bar,
                    checked(fallbackNumerator + 1),
                    fallbackDenominator));
        }

        var scaledNumerator = checked((long)beat.Numerator * (commonDenominator / beatDenominator));
        var end = BeatPosition.Preserve(
            beat.Bar,
            checked((int)(scaledNumerator + 1)),
            checked((int)commonDenominator));
        return new OwnerLockSelectionRange(beat, end);
    }

    /// <summary>
    /// Builds the range sent by the interactive selection-lock command. A single
    /// Note/Timing keeps its exact point row or LN span. Two or more selected
    /// objects represent one marquee/Shift selection and therefore become one
    /// continuous half-open bounding interval from the earliest start to the
    /// latest end. Server-side union/difference still preserves both outside
    /// sides when this interval is locked or unlocked.
    /// </summary>
    public static IReadOnlyList<OwnerLockSelectionRange> BuildSelectedObjectRanges(
        IEnumerable<OwnerLockSelectionRange> objectRanges)
    {
        ArgumentNullException.ThrowIfNull(objectRanges);

        var occupied = objectRanges
            .Select(Normalize)
            .Where(range => range.EndBeat.CompareTo(range.StartBeat) > 0)
            .ToArray();
        if (occupied.Length == 0) return [];
        if (occupied.Length == 1) return [occupied[0]];

        var start = occupied[0].StartBeat;
        var end = occupied[0].EndBeat;
        foreach (var range in occupied.Skip(1))
        {
            if (range.StartBeat.CompareTo(start) < 0) start = range.StartBeat;
            if (range.EndBeat.CompareTo(end) > 0) end = range.EndBeat;
        }

        return [new OwnerLockSelectionRange(start, end)];
    }

    /// <summary>
    /// Plans initial owner locks for existing Notes. A tap occupies one row of
    /// the authored/active least-common-denominator grid and an LN occupies its
    /// exact [Beat, EndBeat) span.
    /// Nearby occupied spans form one section; only an empty gap strictly longer
    /// than one 4/4 measure starts a new section.
    /// </summary>
    public static IReadOnlyList<OwnerLockSelectionRange> BuildAutomaticNoteRanges(
        IEnumerable<ChartNote> notes,
        int division)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ValidateDivision(division);

        var occupied = notes
            .Select(note => note.EndBeat is { } end && end.CompareTo(note.Beat) > 0
                ? new OwnerLockSelectionRange(note.Beat, end)
                : BuildPointRange(note.Beat, division))
            .OrderBy(range => range.StartBeat)
            .ThenBy(range => range.EndBeat)
            .ToArray();
        if (occupied.Length == 0) return [];

        var result = new List<OwnerLockSelectionRange>(Math.Min(occupied.Length, MaximumAutomaticLockRanges));
        var current = occupied[0];
        foreach (var next in occupied.Skip(1))
        {
            var emptyGapLimit = AddWholeBeats(current.EndBeat, EmptyGapMeasureBeats);
            if (next.StartBeat.CompareTo(emptyGapLimit) > 0)
            {
                AddAutomaticRange(result, current);
                current = next;
                continue;
            }

            if (next.EndBeat.CompareTo(current.EndBeat) > 0)
                current = current with { EndBeat = next.EndBeat };
        }

        AddAutomaticRange(result, current);
        return result.ToArray();
    }

    public static IReadOnlyList<OwnerLockSelectionRange> BuildDisjointRanges(
        IEnumerable<OwnerLockSelectionRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var ordered = ranges
            .Select(Normalize)
            .Where(range => range.EndBeat.CompareTo(range.StartBeat) > 0)
            .OrderBy(range => range.StartBeat)
            .ThenBy(range => range.EndBeat)
            .ToArray();
        if (ordered.Length == 0) return [];

        var merged = new List<OwnerLockSelectionRange>(ordered.Length)
        {
            ordered[0]
        };
        foreach (var next in ordered.Skip(1))
        {
            var current = merged[^1];
            if (next.StartBeat.CompareTo(current.EndBeat) <= 0)
            {
                if (next.EndBeat.CompareTo(current.EndBeat) > 0)
                    merged[^1] = current with { EndBeat = next.EndBeat };
                continue;
            }

            merged.Add(next);
        }

        return merged.ToArray();
    }

    /// <summary>
    /// Returns true only when every selected half-open interval is already covered
    /// by the existing lock union. This is the toggle rule used by the lock button:
    /// a partly covered mixed selection locks its missing rows instead of deleting
    /// an intersecting lock and silently leaving the rest unlocked.
    /// </summary>
    public static bool IsFullyCovered(
        IEnumerable<OwnerLockSelectionRange> selected,
        IEnumerable<OwnerLockSelectionRange> existing)
    {
        var targets = BuildDisjointRanges(selected);
        if (targets.Count == 0) return false;
        var coverage = BuildDisjointRanges(existing);
        var coverageIndex = 0;
        foreach (var target in targets)
        {
            while (coverageIndex < coverage.Count &&
                   coverage[coverageIndex].EndBeat.CompareTo(target.StartBeat) <= 0)
                coverageIndex++;
            if (coverageIndex >= coverage.Count) return false;

            var candidate = coverage[coverageIndex];
            if (candidate.StartBeat.CompareTo(target.StartBeat) > 0 ||
                candidate.EndBeat.CompareTo(target.EndBeat) < 0)
                return false;
        }

        return true;
    }

    private static OwnerLockSelectionRange Normalize(OwnerLockSelectionRange range) =>
        range.EndBeat.CompareTo(range.StartBeat) < 0
            ? new OwnerLockSelectionRange(range.EndBeat, range.StartBeat)
            : range;

    private static BeatPosition AddWholeBeats(BeatPosition beat, int beats) =>
        BeatPosition.Preserve(
            checked(beat.Bar + beats),
            beat.Numerator,
            beat.Denominator == 0 ? 1 : beat.Denominator);

    private static void AddAutomaticRange(
        ICollection<OwnerLockSelectionRange> result,
        OwnerLockSelectionRange range)
    {
        if (result.Count >= MaximumAutomaticLockRanges)
            throw new InvalidOperationException(
                $"Automatic owner-lock planning exceeds {MaximumAutomaticLockRanges} disjoint sections.");
        result.Add(range);
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0) (left, right) = (right, left % right);
        return Math.Max(1, left);
    }

    private static void ValidateDivision(int division)
    {
        if (division is < 1 or > 1920)
            throw new ArgumentOutOfRangeException(nameof(division), "The lock division must be between 1 and 1920.");
    }
}

using LumaChart.Core.Models;

namespace LumaChart.Core.Editing;

public enum LongNoteToTapMode
{
    IncludeTail,
    Fill,
    HeadOnly
}

public sealed record NoteTransformResult(
    int ConvertedCount,
    int CreatedCount,
    int SkippedCount,
    IReadOnlySet<Guid> Selection);

/// <summary>
/// Deterministic note-type conversions shared by the desktop editor and future
/// mobile clients. Callers are responsible for filtering hidden/locked notes
/// before passing the selection.
/// </summary>
public static class NoteTypeTransformService
{
    public static NoteTransformResult ConvertLongNotesToTaps(
        ChartDocument document,
        IReadOnlySet<Guid> selectedIds,
        int division,
        LongNoteToTapMode mode)
    {
        division = Math.Clamp(division, 1, 1920);
        var selectedLongNotes = document.Notes
            .Where(note => selectedIds.Contains(note.Id) && note.IsHold)
            .OrderBy(note => note.Beat)
            .ToArray();
        var nextSelection = new HashSet<Guid>();
        var converted = 0;
        var created = 0;
        var skipped = 0;

        foreach (var note in selectedLongNotes)
        {
            var tail = note.EndBeat!.Value;
            note.EndBeat = null;
            nextSelection.Add(note.Id);
            converted++;

            IEnumerable<BeatPosition> additions = mode switch
            {
                LongNoteToTapMode.HeadOnly => [],
                LongNoteToTapMode.IncludeTail => [tail],
                _ => FillPositions(note.Beat, tail, division)
            };

            foreach (var beat in additions)
            {
                if (beat.CompareTo(note.Beat) <= 0 ||
                    HasNoteAt(document.Notes, note, beat))
                {
                    skipped++;
                    continue;
                }

                var tap = note.Clone();
                tap.Id = Guid.NewGuid();
                tap.Beat = beat;
                tap.EndBeat = null;
                document.Notes.Add(tap);
                nextSelection.Add(tap.Id);
                created++;
            }
        }

        return new NoteTransformResult(converted, created, skipped, nextSelection);
    }

    public static NoteTransformResult ConvertTapsToLongNotes(
        ChartDocument document,
        IReadOnlySet<Guid> selectedIds,
        int division)
    {
        division = Math.Clamp(division, 1, 1920);
        // Resolve every boundary from the pre-edit chart so converting one tap
        // cannot change the next tap's result in the same batch.
        var allNotes = document.Notes.ToArray();
        var selectedTaps = allNotes
            .Where(note => selectedIds.Contains(note.Id) && !note.IsHold)
            .OrderBy(note => note.Beat)
            .ToArray();
        var nextSelection = new HashSet<Guid>();
        var converted = 0;
        var skipped = 0;

        foreach (var note in selectedTaps)
        {
            var next = allNotes
                .Where(candidate => candidate.Id != note.Id && SameColumn(candidate, note) &&
                                    candidate.Beat.CompareTo(note.Beat) > 0)
                .OrderBy(candidate => candidate.Beat)
                .FirstOrDefault();
            if (next == null)
            {
                skipped++;
                continue;
            }

            var tail = SubtractGridUnit(next.Beat, division);
            if (tail.CompareTo(note.Beat) <= 0)
            {
                skipped++;
                continue;
            }

            note.EndBeat = tail;
            nextSelection.Add(note.Id);
            converted++;
        }

        return new NoteTransformResult(converted, 0, skipped, nextSelection);
    }

    private static IEnumerable<BeatPosition> FillPositions(
        BeatPosition head,
        BeatPosition tail,
        int division)
    {
        var step = 1d / division;
        for (var index = 1; index <= 2_000_000; index++)
        {
            var value = head.Value + index * step;
            if (value > tail.Value + 1e-9) yield break;
            var position = BeatPosition.FromDouble(value, division * Math.Max(1, head.Denominator) <= 1920
                ? division * Math.Max(1, head.Denominator)
                : 1920);
            if (position.CompareTo(tail) > 0) yield break;
            yield return position;
        }
    }

    private static BeatPosition SubtractGridUnit(BeatPosition beat, int division)
    {
        var precision = LeastCommonMultipleWithinLimit(beat.Denominator, division, 1920);
        return BeatPosition.FromDouble(beat.Value - 1d / division, precision);
    }

    private static int LeastCommonMultipleWithinLimit(int left, int right, int limit)
    {
        var gcd = GreatestCommonDivisor(left, right);
        var candidate = (long)left / gcd * right;
        return candidate is > 0 and <= 1920 ? (int)candidate : limit;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0) (left, right) = (right, left % right);
        return Math.Max(1, left);
    }

    private static bool HasNoteAt(IEnumerable<ChartNote> notes, ChartNote source, BeatPosition beat) =>
        notes.Any(candidate => SameColumn(candidate, source) && candidate.Beat.CompareTo(beat) == 0);

    private static bool SameColumn(ChartNote left, ChartNote right) =>
        left.IsDraft == right.IsDraft &&
        (left.IsDraft ? left.DraftColumn == right.DraftColumn : left.Lane == right.Lane);
}

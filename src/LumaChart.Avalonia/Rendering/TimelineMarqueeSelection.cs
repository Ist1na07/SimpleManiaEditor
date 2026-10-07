using Avalonia;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

/// <summary>
/// Allocation-free geometry and indexed candidate filtering used by the
/// single-control timeline marquee. The interval index is deliberately part of
/// this boundary so pointer moves never fall back to scanning the full chart.
/// </summary>
public static class TimelineMarqueeSelection
{
    public static Rect Normalize(Point first, Point second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X),
        Math.Abs(second.Y - first.Y));

    public static int AddIntersectingNotes(
        TimelineNoteIndex index,
        double minimumBeat,
        double maximumBeat,
        Rect marquee,
        Func<ChartNote, Rect> getBounds,
        ISet<Guid> destination,
        List<ChartNote> candidates,
        IReadOnlySet<int>? excludedGroups = null,
        bool includeDrafts = false)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(getBounds);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(candidates);
        if (maximumBeat < minimumBeat) (minimumBeat, maximumBeat) = (maximumBeat, minimumBeat);

        var candidateCount = index.Query(
            minimumBeat,
            maximumBeat,
            candidates,
            includeDrafts);
        foreach (var note in candidates)
        {
            if (excludedGroups?.Contains(note.Group) == true) continue;
            if (IntersectsInclusive(marquee, getBounds(note))) destination.Add(note.Id);
        }
        return candidateCount;
    }

    public static bool ReplaceSelection(HashSet<Guid> selection, IReadOnlySet<Guid> next)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(next);
        if (selection.SetEquals(next)) return false;
        selection.Clear();
        selection.UnionWith(next);
        return true;
    }

    private static bool IntersectsInclusive(Rect left, Rect right) =>
        left.Left <= right.Right && left.Right >= right.Left &&
        left.Top <= right.Bottom && left.Bottom >= right.Top;
}

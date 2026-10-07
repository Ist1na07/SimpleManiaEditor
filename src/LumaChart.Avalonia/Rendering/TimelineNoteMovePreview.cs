using LumaChart.Application.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

/// <summary>
/// Immutable source geometry captured at the beginning of a Note drag. Pointer
/// movement never mutates these values or the editor document.
/// </summary>
public readonly record struct TimelineNoteMoveOrigin(
    int Lane,
    BeatPosition Beat,
    BeatPosition? EndBeat);

/// <summary>
/// A pure Note drag preview. The exact relative move is reused for every selected
/// Note so authored rational expressions retain their denominators.
/// </summary>
public readonly record struct TimelineNoteMovePreviewState(
    int LaneDelta,
    RelativeBeatMove BeatMove)
{
    public double BeatDelta => BeatMove.Delta;
}

public static class TimelineNoteMovePreview
{
    public static TimelineNoteMovePreviewState Resolve(
        BeatPosition anchorOrigin,
        double minimumOriginBeat,
        int minimumLane,
        int maximumLane,
        int keyCount,
        int snapDivision,
        int requestedLaneDelta,
        double requestedBeatDelta)
        => Resolve(
            anchorOrigin,
            minimumOriginBeat,
            minimumLane,
            maximumLane,
            new ProEditorColumnRange(0, Math.Max(1, keyCount) - 1),
            snapDivision,
            requestedLaneDelta,
            requestedBeatDelta);

    public static TimelineNoteMovePreviewState Resolve(
        BeatPosition anchorOrigin,
        double minimumOriginBeat,
        int minimumColumn,
        int maximumColumn,
        ProEditorColumnRange allowedColumns,
        int snapDivision,
        int requestedColumnDelta,
        double requestedBeatDelta)
    {
        if (allowedColumns.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(allowedColumns));
        var laneDelta = Math.Clamp(
            requestedColumnDelta,
            allowedColumns.Minimum - minimumColumn,
            allowedColumns.Maximum - maximumColumn);
        var beatMove = BasicChartEditingService.QuantizeRelativeMove(
            anchorOrigin,
            snapDivision,
            requestedBeatDelta,
            minimumOriginBeat);
        return new TimelineNoteMovePreviewState(laneDelta, beatMove);
    }

    public static TimelineNoteMoveOrigin Apply(
        TimelineNoteMoveOrigin origin,
        TimelineNoteMovePreviewState preview) =>
        new(
            origin.Lane + preview.LaneDelta,
            BasicChartEditingService.ApplyRelativeMove(origin.Beat, preview.BeatMove),
            origin.EndBeat.HasValue
                ? BasicChartEditingService.ApplyRelativeMove(origin.EndBeat.Value, preview.BeatMove)
                : null);

    /// <summary>
    /// Keeps the last paintable destination when a candidate would enter an
    /// owner-protected interval. This mirrors the WPF gesture: the pointer may
    /// continue across a lock, but release commits only the last legal preview.
    /// </summary>
    public static TimelineNoteMovePreviewState KeepLastLegal(
        TimelineNoteMovePreviewState current,
        TimelineNoteMovePreviewState candidate,
        IEnumerable<TimelineNoteMoveOrigin> origins,
        Func<BeatPosition, BeatPosition, bool> intersectsProtectedRegion,
        out bool accepted)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(intersectsProtectedRegion);
        foreach (var origin in origins)
        {
            var destination = Apply(origin, candidate);
            if (!intersectsProtectedRegion(
                    destination.Beat,
                    destination.EndBeat ?? destination.Beat)) continue;
            accepted = false;
            return current;
        }

        accepted = true;
        return candidate;
    }

    public static bool IsInterrupted(long gestureChangeSerial, long currentChangeSerial) =>
        gestureChangeSerial != currentChangeSerial;
}

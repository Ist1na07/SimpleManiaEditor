using Avalonia;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

/// <summary>
/// Pure LN-tail hit and preview rules. Pointer moves never mutate the document;
/// the accepted target is committed once by the shared editing command.
/// </summary>
public static class TimelineHoldTailResize
{
    public static double HitTolerance(double renderedNoteHeight) =>
        Math.Clamp(renderedNoteHeight * 1.15, 18, 40);

    public static bool IsHit(
        Point point,
        double noteLeft,
        double noteRight,
        double headY,
        double tailY,
        double renderedNoteHeight)
    {
        var tailDistance = Math.Abs(point.Y - tailY);
        return point.X >= noteLeft - 12 &&
            point.X <= noteRight + 12 &&
            tailDistance <= HitTolerance(renderedNoteHeight) &&
            tailDistance <= Math.Abs(point.Y - headY);
    }

    public static BeatPosition? ResolveTarget(
        BeatPosition headBeat,
        BeatPosition authoredPointerBeat) =>
        authoredPointerBeat.CompareTo(headBeat) <= 0 ? null : authoredPointerBeat;

    public static BeatPosition? KeepLastLegal(
        BeatPosition headBeat,
        BeatPosition? current,
        BeatPosition? candidate,
        Func<BeatPosition, BeatPosition, bool> intersectsProtectedRegion,
        out bool accepted)
    {
        ArgumentNullException.ThrowIfNull(intersectsProtectedRegion);
        if (intersectsProtectedRegion(headBeat, candidate ?? headBeat))
        {
            accepted = false;
            return current;
        }
        accepted = true;
        return candidate;
    }

    public static bool IsInterrupted(long gestureChangeSerial, long currentChangeSerial) =>
        gestureChangeSerial != currentChangeSerial;

    public static bool HasChange(BeatPosition? original, BeatPosition? current) =>
        !Nullable.Equals(original, current);
}

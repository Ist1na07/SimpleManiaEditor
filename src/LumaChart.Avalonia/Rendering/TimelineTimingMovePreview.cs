using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

/// <summary>
/// Immutable geometry for a live Timing drag. The document is deliberately not
/// involved: pointer moves resolve only an anchor/delta for the single timeline
/// control to paint, while the release command remains the sole mutation.
/// </summary>
public readonly record struct TimelineTimingMovePreviewState(
    BeatPosition AnchorBeat,
    double BeatDelta);

public static class TimelineTimingMovePreview
{
    public static BeatPosition RequestedAnchorBeat(
        BeatPosition anchorOrigin,
        double pointerStartBeat,
        double pointerCurrentBeat)
    {
        var anchorValue = anchorOrigin.Denominator == 0 ? 0 : anchorOrigin.Value;
        return BeatPosition.FromDouble(anchorValue + pointerCurrentBeat - pointerStartBeat);
    }

    /// <summary>
    /// Matches the shared Timing batch move command: clamp the whole batch at
    /// Beat zero, then snap the pointer-anchored row to the active subdivision.
    /// </summary>
    public static TimelineTimingMovePreviewState Resolve(
        BeatPosition anchorOrigin,
        BeatPosition minimumOrigin,
        BeatPosition requestedAnchorBeat,
        int snapDivision)
    {
        snapDivision = Math.Clamp(snapDivision, 1, 1920);
        var anchorValue = anchorOrigin.Denominator == 0 ? 0 : anchorOrigin.Value;
        var minimumValue = minimumOrigin.Denominator == 0 ? 0 : minimumOrigin.Value;
        var requestedValue = requestedAnchorBeat.Denominator == 0 ? 0 : requestedAnchorBeat.Value;
        var targetAnchor = BeatPosition
            .FromDouble(Math.Max(anchorValue - minimumValue, requestedValue))
            .Snap(snapDivision);
        return new TimelineTimingMovePreviewState(
            targetAnchor,
            targetAnchor.Value - anchorValue);
    }

    public static BeatPosition Apply(
        BeatPosition origin,
        double beatDelta,
        int snapDivision) =>
        BeatPosition
            .FromDouble((origin.Denominator == 0 ? 0 : origin.Value) + beatDelta)
            .Snap(Math.Clamp(snapDivision, 1, 1920));

    public static bool IsInterrupted(long gestureChangeSerial, long currentChangeSerial) =>
        gestureChangeSerial != currentChangeSerial;
}

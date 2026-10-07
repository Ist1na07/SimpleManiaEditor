using LumaChart.Core.Models;

namespace LumaChart.Core.Editing;

/// <summary>
/// Pure grid navigation shared by direct timeline seeks and wheel stepping.
/// Navigation positions retain the selected subdivision instead of inheriting
/// an arbitrary floating-point phase from a pixel-derived seek.
/// </summary>
public static class TimelineGridNavigation
{
    public static BeatPosition Snap(double beat, int division)
    {
        division = Math.Clamp(division, 1, 1920);
        return BeatPosition.FromDouble(Math.Max(0, double.IsFinite(beat) ? beat : 0)).Snap(division);
    }

    public static BeatPosition Step(double currentBeat, int division, int direction)
    {
        division = Math.Clamp(division, 1, 1920);
        var current = Snap(currentBeat, division);
        direction = Math.Sign(direction);
        if (direction == 0) return current;

        var units = checked((long)current.Bar * division + current.Numerator);
        units = Math.Max(0, checked(units + direction));
        var bar = Math.DivRem(units, division, out var numerator);
        return BeatPosition.Preserve(checked((int)bar), checked((int)numerator), division);
    }
}

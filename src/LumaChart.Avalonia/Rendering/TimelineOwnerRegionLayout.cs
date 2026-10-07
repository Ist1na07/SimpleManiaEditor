using Avalonia;
using LumaChart.Core.Editing;

namespace LumaChart.Avalonia.Rendering;

public readonly record struct TimelineOwnerRegionVisual(
    OwnerProtectedRegion Region,
    Rect Bounds);

public static class TimelineOwnerRegionLayout
{
    public const double MinimumVisibleHeight = 3;

    public static void BuildVisible(
        IReadOnlyList<OwnerProtectedRegion> regions,
        double minimumBeat,
        double maximumBeat,
        double trackLeft,
        double trackRight,
        Func<double, double> beatToY,
        List<TimelineOwnerRegionVisual> destination)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(beatToY);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        if (maximumBeat < minimumBeat) (minimumBeat, maximumBeat) = (maximumBeat, minimumBeat);
        foreach (var region in regions)
        {
            var logicalStart = Math.Min(region.StartBeat.Value, region.EndBeat.Value);
            var logicalEnd = Math.Max(region.StartBeat.Value, region.EndBeat.Value);
            if (logicalEnd <= minimumBeat || logicalStart > maximumBeat) continue;
            var firstY = beatToY(Math.Max(logicalStart, minimumBeat));
            var secondY = beatToY(Math.Min(logicalEnd, maximumBeat));
            var top = Math.Min(firstY, secondY);
            var bottom = Math.Max(firstY, secondY);
            if (bottom - top < MinimumVisibleHeight)
            {
                var center = (top + bottom) / 2;
                top = center - MinimumVisibleHeight / 2;
                bottom = center + MinimumVisibleHeight / 2;
            }
            destination.Add(new TimelineOwnerRegionVisual(
                region,
                new Rect(
                    trackLeft + 1,
                    top,
                    Math.Max(0, trackRight - trackLeft - 2),
                    bottom - top)));
        }
    }
}

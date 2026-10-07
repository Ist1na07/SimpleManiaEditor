using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

/// <summary>Immutable beat-sorted point index for visible Timing range queries.</summary>
public sealed class TimelineTimingIndex
{
    private TimingEvent[] items = [];

    public int Count => items.Length;

    public void Rebuild(IEnumerable<TimingEvent> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        items = source
            .Select((timing, sourceOrder) => new IndexedTiming(timing, sourceOrder))
            .OrderBy(item => item.Timing.Beat)
            .ThenBy(item => item.SourceOrder)
            .Select(item => item.Timing)
            .ToArray();
    }

    public int Query(double minimumBeat, double maximumBeat, List<TimingEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        if (maximumBeat < minimumBeat) (minimumBeat, maximumBeat) = (maximumBeat, minimumBeat);
        var first = LowerBound(minimumBeat);
        for (var index = first; index < items.Length && items[index].Beat.Value <= maximumBeat; index++)
            destination.Add(items[index]);
        return destination.Count;
    }

    private int LowerBound(double beat)
    {
        var left = 0;
        var right = items.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (items[middle].Beat.Value < beat) left = middle + 1;
            else right = middle;
        }
        return left;
    }

    private readonly record struct IndexedTiming(TimingEvent Timing, int SourceOrder);
}

using LumaChart.Core.Models;

namespace LumaChart.Core.Timing;

/// <summary>
/// Converts musical beats into a cumulative playfield distance. Scroll effects
/// change the distance produced by subsequent beats; BPM deliberately does not
/// live here because <see cref="TimingMap"/> already controls how quickly audio
/// time advances through those beats.
/// </summary>
public sealed class ScrollSpeedMap
{
    private readonly Segment[] segments;

    public ScrollSpeedMap(IEnumerable<TimingEvent> events)
    {
        var points = events.Select((item, index) => (Item: item, Index: index))
            .Where(entry => entry.Item.Effect == TimingEffectKind.Scroll && double.IsFinite(entry.Item.EffectValue))
            .OrderBy(entry => entry.Item.Beat)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();

        var result = new List<Segment> { new(0, 1, 0) };
        foreach (var point in points)
        {
            var beat = Math.Max(0, point.Beat.Value);
            var multiplier = Math.Clamp(point.EffectValue, 0, 100);
            var previous = result[^1];
            var position = previous.StartPosition + (beat - previous.StartBeat) * previous.Multiplier;
            if (Math.Abs(previous.StartBeat - beat) < 1e-9)
                result[^1] = new Segment(beat, multiplier, previous.StartPosition);
            else
                result.Add(new Segment(beat, multiplier, position));
        }
        segments = result.ToArray();
    }

    /// <summary>Returns cumulative distance in scroll-adjusted beats.</summary>
    public double PositionAt(BeatPosition beat)
    {
        var value = beat.Value;
        var segment = segments[FindSegment(value)];
        return segment.StartPosition + (value - segment.StartBeat) * segment.Multiplier;
    }

    public double Distance(BeatPosition from, BeatPosition to) => PositionAt(to) - PositionAt(from);

    public double MultiplierAt(BeatPosition beat) => segments[FindSegment(beat.Value)].Multiplier;

    private int FindSegment(double beat)
    {
        var low = 0;
        var high = segments.Length - 1;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (segments[middle].StartBeat <= beat) low = middle;
            else high = middle - 1;
        }
        return low;
    }

    private readonly record struct Segment(double StartBeat, double Multiplier, double StartPosition);
}

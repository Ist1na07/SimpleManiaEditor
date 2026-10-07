using LumaChart.Core.Models;

namespace LumaChart.Core.Timing;

public sealed class TimingMap
{
    private readonly Segment[] segments;
    private readonly bool timeStartsMonotonic;

    public TimingMap(IEnumerable<TimingEvent> events)
    {
        var orderedPoints = events.Select((item, index) => (Item: item, Index: index))
            .Where(entry => entry.Item.Bpm.HasValue && double.IsFinite(entry.Item.Bpm.Value) && entry.Item.Bpm.Value > 0)
            .OrderBy(entry => entry.Item.Beat)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Item)
            .ToList();
        var points = new List<TimingEvent>(orderedPoints.Count);
        foreach (var point in orderedPoints)
        {
            if (points.Count > 0 && points[^1].Beat.CompareTo(point.Beat) == 0)
            {
                // Same rule as ChartDocument.Normalize: the last record wins but a
                // zero Delay does not erase the overridden record's offset.
                var overridden = points[^1];
                points[^1] = point.Delay == 0 && overridden.Delay != 0 && double.IsFinite(overridden.Delay)
                    ? new TimingEvent { Beat = point.Beat, Bpm = point.Bpm, Delay = overridden.Delay }
                    : point;
            }
            else points.Add(point);
        }
        if (points.Count == 0) points.Add(new TimingEvent { Bpm = 120 });
        if (points[0].Beat.Value > 0) points.Insert(0, new TimingEvent { Bpm = points[0].Bpm, Beat = BeatPosition.Zero });

        segments = new Segment[points.Count];
        double elapsed = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            if (i > 0)
            {
                var previous = points[i - 1];
                elapsed += (current.Beat.Value - previous.Beat.Value) * 60_000d / previous.Bpm!.Value;
            }
            var delay = double.IsFinite(current.Delay) ? current.Delay : 0;
            // At the first BPM point Malody's delay is the BGM offset: a positive
            // value moves beat zero before the audio. At later BPM points it is a
            // real pause inserted before that timing segment. Subtracting every
            // later delay treated those pauses as repeated song offsets and made
            // long multi-song charts lead the audio a little more at each section.
            elapsed += i == 0 ? -delay : delay;
            segments[i] = new Segment(current.Beat.Value, current.Bpm!.Value, elapsed);
        }
        timeStartsMonotonic = true;
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i].StartMilliseconds >= segments[i - 1].StartMilliseconds) continue;
            timeStartsMonotonic = false;
            break;
        }
    }

    public double BeatToMilliseconds(BeatPosition beat)
    {
        var value = beat.Value;
        var segment = segments[FindBeatSegment(value)];
        return segment.StartMilliseconds + (value - segment.StartBeat) * 60_000d / segment.Bpm;
    }

    public BeatPosition MillisecondsToBeat(double milliseconds)
    {
        var segmentIndex = FindTimeSegment(milliseconds);
        var segment = segments[segmentIndex];
        var beat = segment.StartBeat + (milliseconds - segment.StartMilliseconds) * segment.Bpm / 60_000d;
        if (segmentIndex + 1 < segments.Length &&
            milliseconds < segments[segmentIndex + 1].StartMilliseconds &&
            beat > segments[segmentIndex + 1].StartBeat)
            beat = segments[segmentIndex + 1].StartBeat;
        // A very fast final segment can map a late audio position past the int
        // bar range; BeatPosition.FromDouble would throw during playback/seek.
        return BeatPosition.FromDouble(Math.Clamp(beat, -MaximumMappedBeat, MaximumMappedBeat));
    }

    public double BpmAt(BeatPosition beat) => segments[FindBeatSegment(beat.Value)].Bpm;

    /// <summary>
    /// Returns the first BPM record this map plays: the last BPM-bearing record
    /// in source order at the earliest BPM beat (same-beat records are resolved
    /// exactly like the constructor does).
    /// </summary>
    public static TimingEvent? FindFirstBpmTiming(IEnumerable<TimingEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        TimingEvent? result = null;
        foreach (var item in events)
        {
            if (!item.Bpm.HasValue || !double.IsFinite(item.Bpm.Value) || item.Bpm.Value <= 0) continue;
            if (result == null || item.Beat.CompareTo(result.Beat) <= 0) result = item;
        }
        return result;
    }

    /// <summary>
    /// Returns the BPM record whose Delay is the song offset, or null when the
    /// first BPM record starts after beat 0 (the map then plays beat 0 with no
    /// offset and that record's Delay is a section pause). The Offset field,
    /// tempo detection and the Malody BGM offset all use this one rule so they
    /// edit the record the timeline actually plays, never merely the first BPM
    /// row in list order.
    /// </summary>
    public static TimingEvent? FindSongOffsetTiming(IEnumerable<TimingEvent> events)
    {
        var first = FindFirstBpmTiming(events);
        return first != null && first.Beat.CompareTo(BeatPosition.Zero) <= 0 ? first : null;
    }

    private const double MaximumMappedBeat = int.MaxValue - 2d;

    private int FindBeatSegment(double beat)
    {
        var lo = 0; var hi = segments.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (segments[mid].StartBeat <= beat) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private int FindTimeSegment(double ms)
    {
        if (!timeStartsMonotonic)
        {
            for (var i = segments.Length - 1; i >= 0; i--)
                if (segments[i].StartMilliseconds <= ms) return i;
            return 0;
        }

        var lo = 0; var hi = segments.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (segments[mid].StartMilliseconds <= ms) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private readonly record struct Segment(double StartBeat, double Bpm, double StartMilliseconds);
}

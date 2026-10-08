using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Playtest;

/// <summary>Continuous song-time integration of BPM/SV, normalized to the most
/// common BPM by duration (ppy/osu relative beat-length scrolling policy).</summary>
public sealed class ManiaScrollMap
{
    private readonly Segment[] segments;
    public double BaseBpm { get; }

    public ManiaScrollMap(ChartDocument document, TimingMap timing, ScrollSpeedMap scroll)
    {
        var end = document.Notes.Where(n => !n.IsDraft).Select(n => timing.BeatToMilliseconds(n.EndBeat ?? n.Beat))
            .DefaultIfEmpty(1).Max();
        var changes = document.Timing.Where(t => t.Bpm.HasValue || t.Effect == TimingEffectKind.Scroll)
            .Select(t => timing.BeatToMilliseconds(t.Beat)).Append(0).Distinct().Order().ToArray();
        var weights = new Dictionary<double, double>();
        for (var i = 0; i < changes.Length; i++)
        {
            var start = Math.Max(0, changes[i]);
            var stop = Math.Min(end, i + 1 < changes.Length ? changes[i + 1] : end);
            if (stop <= start) continue;
            var bpm = timing.BpmAt(timing.MillisecondsToBeat((start + stop) / 2));
            weights[bpm] = weights.GetValueOrDefault(bpm) + stop - start;
        }
        BaseBpm = weights.Count > 0 ? weights.OrderByDescending(p => p.Value).ThenByDescending(p => p.Key).First().Key
            : timing.BpmAt(BeatPosition.Zero);
        var result = new List<Segment>();
        foreach (var ms in changes)
        {
            // Sample after the change to avoid beat-rounding landing on the old
            // segment; integration itself never quantizes the playback clock.
            var beat = timing.MillisecondsToBeat(ms + .001);
            var slope = timing.BpmAt(beat) / BaseBpm * scroll.MultiplierAt(beat) / 1000;
            var position = result.Count == 0 ? ms * slope : result[^1].Position + (ms - result[^1].Milliseconds) * result[^1].Slope;
            result.Add(new Segment(ms, position, slope));
        }
        segments = result.ToArray();
    }

    public double PositionAt(double milliseconds)
    {
        var low = 0; var high = segments.Length - 1;
        while (low < high) { var middle = (low + high + 1) / 2; if (segments[middle].Milliseconds <= milliseconds) low = middle; else high = middle - 1; }
        var segment = segments[low];
        return segment.Position + (milliseconds - segment.Milliseconds) * segment.Slope;
    }
    private readonly record struct Segment(double Milliseconds, double Position, double Slope);
}

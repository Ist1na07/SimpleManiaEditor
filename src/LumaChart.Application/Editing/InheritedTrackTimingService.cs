using System.Text.Json.Nodes;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Editing;

/// <summary>
/// Scratch/pattern tracks share the main song's timing. Keep their absolute
/// audio positions and their independent edit history when that timing changes.
/// Reference tracks deliberately do not use this service.
/// </summary>
public static class InheritedTrackTimingService
{
    public static bool Synchronize(EditorSession track, ChartDocument owner)
    {
        ArgumentNullException.ThrowIfNull(track); ArgumentNullException.ThrowIfNull(owner);
        if (track.IsReadOnly || track.CanEditTiming || track.CanEditMetadata)
            throw new InvalidOperationException("Only editable derived tracks may inherit timing.");
        if (TimingEquals(track.Document, owner)) return true;
        // Capture once: all projected snapshots must target exactly the same map.
        var target = owner.Timing.Select(t => t.Clone()).ToArray();
        var targetMap = new TimingMap(target);
        return track.TryProjectInheritedDocumentHistory(document =>
        {
            if (!ClockEquals(document.Timing, target))
            {
                var oldMap = new TimingMap(document.Timing);
                foreach (var note in document.Notes)
                {
                    note.Beat = targetMap.MillisecondsToBeat(oldMap.BeatToMilliseconds(note.Beat), BeatPosition.MaximumPreservedDenominator);
                    if (note.EndBeat is { } end)
                        note.EndBeat = targetMap.MillisecondsToBeat(oldMap.BeatToMilliseconds(end), BeatPosition.MaximumPreservedDenominator);
                }
            }
            document.Timing = target.Select(t => t.Clone()).ToList();
            document.Metadata.Bpm = owner.Metadata.Bpm;
        });
    }

    private static bool TimingEquals(ChartDocument left, ChartDocument right)
    {
        if (left.Metadata.Bpm != right.Metadata.Bpm || left.Timing.Count != right.Timing.Count) return false;
        for (var i = 0; i < left.Timing.Count; i++)
        {
            var a = left.Timing[i]; var b = right.Timing[i];
            if (a.Id != b.Id || a.Beat != b.Beat || a.Bpm != b.Bpm || a.Delay != b.Delay || a.Effect != b.Effect ||
                a.EffectValue != b.EffectValue || a.EffectValueText != b.EffectValueText || a.EffectFieldName != b.EffectFieldName ||
                a.OnlineCreatorUserId != b.OnlineCreatorUserId || !JsonNode.DeepEquals(a.UnknownData, b.UnknownData)) return false;
        }
        return true;
    }

    private static bool ClockEquals(IEnumerable<TimingEvent> left, IEnumerable<TimingEvent> right) =>
        left.Where(t => t.Bpm.HasValue).Select(t => (t.Beat, t.Bpm, t.Delay))
            .SequenceEqual(right.Where(t => t.Bpm.HasValue).Select(t => (t.Beat, t.Bpm, t.Delay)));
}

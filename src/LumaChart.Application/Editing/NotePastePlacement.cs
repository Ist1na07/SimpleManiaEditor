using LumaChart.Core.Models;
using LumaChart.Core.IO;

namespace LumaChart.Application.Editing;

/// <summary>A pending paste is immutable until a single placement is committed.</summary>
public static class NotePastePlacement
{
    public static ChartNote[] Preview(IReadOnlyList<ChartNote> source, BeatPosition anchor, int column, int keys)
    {
        if (source.Count == 0) return [];
        var first = source.MinBy(n => n.Beat)!.Beat;
        var left = source.Min(n => n.Lane);
        var width = source.Max(n => n.Lane) - left + 1;
        if (width > keys) throw new InvalidOperationException("排列超出目标轨道的键数。");
        var deltaLane = Math.Clamp(column, 0, keys - width) - left;
        return source.Select(n =>
        {
            var copy = n.Clone(); copy.Id = Guid.NewGuid(); copy.IsDraft = false;
            copy.OnlineCreatorUserId = 0; copy.Lane += deltaLane;
            copy.Beat = BasicChartEditingService.OffsetExact(anchor, n.Beat, first);
            copy.EndBeat = n.EndBeat.HasValue ? BasicChartEditingService.OffsetExact(anchor, n.EndBeat.Value, first) : null;
            return copy;
        }).ToArray();
    }

    public static int Commit(EditorSession session, IReadOnlyList<ChartNote> notes)
    {
        if (session.IsReadOnly || !session.CanEditNotes) throw new InvalidOperationException("当前轨道不允许编辑按键。");
        if (notes.Any(n => n.Lane < 0 || n.Lane >= session.Document.KeyCount || n.Beat.Value < 0 && !OsuSourceEnvelope.HasSource(session.Document)))
            throw new InvalidOperationException("粘贴位置超出轨道范围。");
        if (!session.AllowOwnerProtectedEdits && notes.Any(n => session.IntersectsOwnerProtectedRegion(n.Beat, n.EndBeat ?? n.Beat)))
            throw new InvalidOperationException("粘贴范围包含锁定区域。");
        var occupied = session.Document.Notes.Select(n => (n.Lane, n.Beat, n.EndBeat)).ToHashSet();
        var added = notes.Where(n => occupied.Add((n.Lane, n.Beat, n.EndBeat))).Select(n => n.Clone()).ToArray();
        if (added.Length == 0) return 0;
        session.Mutate(d => d.Notes.AddRange(added));
        session.Selection.Clear(); session.Selection.UnionWith(added.Select(n => n.Id));
        return added.Length;
    }
}

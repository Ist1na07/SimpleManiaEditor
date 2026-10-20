using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

public sealed record HoldTailResizePlan(IReadOnlySet<Guid> ReplacedIds, ChartNote[] Notes);

/// <summary>Pure two-sided preview and a single atomic LN-tail edit.</summary>
public static class SymmetricHoldTailResizeCommand
{
    public static HoldTailResizePlan Preview(EditorSession session, Guid noteId, BeatPosition tail,
        CenterSymmetrySettings? symmetry)
    {
        if (session.IsReadOnly || !session.CanEditNotes) throw new InvalidOperationException("当前轨道不允许调整按键。");
        var original = session.Document.Notes.FirstOrDefault(n => n.Id == noteId)
            ?? throw new InvalidOperationException("调整中的按键已不存在。");
        var source = original.Clone();
        source.EndBeat = tail.CompareTo(source.Beat) > 0 ? tail : null;
        var replaced = new HashSet<Guid> { original.Id };
        var result = new List<ChartNote> { source };
        ChartNote? oldPair = null;
        if (symmetry != null)
        {
            symmetry.Validate(session.Document.KeyCount);
            var columns = symmetry.Columns.Order().ToArray();
            var index = Array.IndexOf(columns, original.Lane);
            var isSingleSymmetricHold = !original.IsDraft && original.EndBeat.HasValue && index >= 0 &&
                columns[columns.Length - 1 - index] == original.Lane &&
                original.Beat.CompareTo(symmetry.Start) >= 0 && original.EndBeat.Value.CompareTo(symmetry.End) <= 0 &&
                CenterSymmetryService.Reflect(symmetry.Start, symmetry.End, original.EndBeat.Value) == original.Beat;
            if (isSingleSymmetricHold)
            {
                // A previously merged central LN is its own companion. Moving
                // one end must move the opposite end, otherwise unioning its two
                // previews would make every inward drag appear unresponsive.
                var reflectedTail = CenterSymmetryService.Reflect(symmetry.Start, symmetry.End, tail);
                source.Beat = tail.CompareTo(reflectedTail) <= 0 ? tail : reflectedTail;
                var end = tail.CompareTo(reflectedTail) >= 0 ? tail : reflectedTail;
                source.EndBeat = end.CompareTo(source.Beat) == 0 ? null : end;
            }
            var reflectedOriginal = CenterSymmetryService.CreateCompanion(original, symmetry, session.Document.KeyCount);
            if (reflectedOriginal != null)
                oldPair = session.Document.Notes.FirstOrDefault(n => n.Id != original.Id && n.IsDraft == original.IsDraft &&
                    CenterSymmetryService.SamePosition(n, reflectedOriginal));
            if (oldPair != null) replaced.Add(oldPair.Id);
            var companion = CenterSymmetryService.CreateCompanion(source, symmetry, session.Document.KeyCount);
            if (companion != null)
            {
                // Keep independently assigned sample/volume and identity on the
                // old pair. Its HEAD moves when the source TAIL is adjusted.
                if (oldPair != null)
                {
                    var retained = oldPair.Clone(); retained.Beat = companion.Beat; retained.EndBeat = companion.EndBeat;
                    companion = retained;
                }
                companion = CenterSymmetryService.MergeOverlappingCompanion(source, companion);
                if (companion != null && CenterSymmetryService.CanAdd(session, companion,
                        session.Document.Notes.Where(n => !replaced.Contains(n.Id))))
                    result.Add(companion);
            }
        }
        if (source.Beat.Value < 0 && !source.IsDraft && !OsuSourceEnvelope.HasSource(session.Document))
            throw new InvalidOperationException("调整范围超出谱面允许的时间范围。");
        if (!session.AllowOwnerProtectedEdits &&
            (session.Document.Notes.Any(n => replaced.Contains(n.Id) && session.IsOwnerProtected(n)) ||
             result.Any(n => session.IntersectsOwnerProtectedRegion(n.Beat, n.EndBeat ?? n.Beat))))
            throw new InvalidOperationException("中心对称调整涉及锁定范围，已保留两个原按键。");
        return new(replaced, result.ToArray());
    }

    public static bool Commit(EditorSession session, Guid noteId, BeatPosition tail,
        CenterSymmetrySettings? symmetry, long expectedSerial)
    {
        if (session.ChangeSerial != expectedSerial) throw new InvalidOperationException("谱面已改变，面尾调整已取消。");
        var plan = Preview(session, noteId, tail, symmetry);
        var serial = session.ChangeSerial;
        session.Mutate(d => { d.Notes.RemoveAll(n => plan.ReplacedIds.Contains(n.Id)); d.Notes.AddRange(plan.Notes); });
        if (serial == session.ChangeSerial) return false;
        session.Selection.Clear(); session.Selection.UnionWith(plan.Notes.Select(n => n.Id));
        return true;
    }
}

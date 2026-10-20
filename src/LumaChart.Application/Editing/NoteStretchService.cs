using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

/// <summary>Affine musical-time mapping: preserves note identities, sound data and LN proportions.</summary>
public static class NoteStretchService
{
    public static BeatPosition Offset(BeatPosition position, BeatPosition from, BeatPosition to) =>
        BeatArithmetic.Offset(to,position,from);

    public static ChartNote[] Preview(IReadOnlyList<ChartNote> notes, BeatPosition start, BeatPosition end,
        BeatPosition targetStart, BeatPosition targetEnd)
    {
        if (targetEnd.CompareTo(targetStart) < 0 || end.CompareTo(start) < 0) throw new ArgumentException("拉伸范围不能倒置。");
        BeatPosition Map(BeatPosition b) => BeatArithmetic.MapRange(b,start,end,targetStart,targetEnd);
        return notes.Select(n => { var copy = n.Clone(); copy.Beat = Map(n.Beat); copy.EndBeat = n.EndBeat.HasValue ? Map(n.EndBeat.Value) : null; return copy; }).ToArray();
    }

    public static void Commit(EditorSession session, IReadOnlyList<ChartNote> notes, long expectedSerial)
    {
        if (session.IsReadOnly || !session.CanEditNotes) throw new InvalidOperationException("当前轨道只读，不能拉伸。");
        if (session.ChangeSerial != expectedSerial) throw new InvalidOperationException("谱面已改变，拉伸已取消，请重新选择。");
        if (notes.Any(n => n.EndBeat.HasValue && n.EndBeat.Value.CompareTo(n.Beat) <= 0))
            throw new InvalidOperationException("拉伸过小会使长条长度归零，已保留原按键。");
        if (!LumaChart.Core.IO.OsuSourceEnvelope.HasSource(session.Document) && notes.Any(n => !n.IsDraft && n.Beat.Value < 0))
            throw new InvalidOperationException("拉伸超出当前谱面允许的时间范围。");
        var originals = session.Document.Notes.ToDictionary(n => n.Id);
        if (notes.Any(n => !originals.ContainsKey(n.Id))) throw new InvalidOperationException("选中的音符已改变。");
        if (!session.AllowOwnerProtectedEdits && notes.Any(n => session.IsOwnerProtected(originals[n.Id]) ||
                session.IntersectsOwnerProtectedRegion(n.Beat, n.EndBeat ?? n.Beat))) throw new InvalidOperationException("拉伸涉及锁定范围。");
        var updates = notes.ToDictionary(n => n.Id);
        session.Mutate(document =>
        {
            foreach (var n in document.Notes)
                if (updates.TryGetValue(n.Id, out var update))
                { n.Beat = update.Beat; n.EndBeat = update.EndBeat; n.Lane = update.Lane; n.DraftColumn = update.DraftColumn; n.IsDraft = update.IsDraft; }
        });
    }
}

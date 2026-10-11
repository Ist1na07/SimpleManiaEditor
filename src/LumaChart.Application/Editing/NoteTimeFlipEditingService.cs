using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

/// <summary>Reflects selected heads and tails around their selection's time centre.</summary>
public static class NoteTimeFlipEditingService
{
    public static int FlipSelection(EditorSession session, IEnumerable<Guid> selectedIds)
    {
        if (session.IsReadOnly || !session.CanEditNotes)
            throw new InvalidOperationException("当前谱面不能编辑音符。");
        var ids = selectedIds.ToHashSet();
        var notes = session.Document.Notes.Where(n => ids.Contains(n.Id)).ToArray();
        if (notes.Length == 0) return 0;
        var start = notes.MinBy(n => n.Beat)!.Beat;
        var end = notes.Max(n => n.EndBeat ?? n.Beat);
        var replacements = notes.Select(n => (Note: n,
            Head: CenterSymmetryService.Reflect(start, end, n.EndBeat ?? n.Beat),
            Tail: n.EndBeat.HasValue ? (BeatPosition?)CenterSymmetryService.Reflect(start, end, n.Beat) : null)).ToArray();
        if (!session.AllowOwnerProtectedEdits && replacements.Any(p => session.IsOwnerProtected(p.Note) ||
            session.IntersectsOwnerProtectedRegion(p.Head, p.Tail ?? p.Head)))
            throw new InvalidOperationException("翻转位置与锁定范围相交，谱面未修改。");
        var changed = replacements.Where(p => p.Head != p.Note.Beat || p.Tail != p.Note.EndBeat).ToDictionary(p => p.Note.Id);
        if (changed.Count == 0) return 0;
        session.Mutate(d =>
        {
            foreach (var note in d.Notes)
                if (changed.TryGetValue(note.Id, out var replacement))
                { note.Beat = replacement.Head; note.EndBeat = replacement.Tail; }
        });
        return changed.Count;
    }
}

using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

/// <summary>Explicit batch quantisation; changing the display grid never calls this.</summary>
public static class NoteGridSnapService
{
    public static int Apply(EditorSession session, bool allNotes)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsReadOnly || !session.CanEditNotes) return 0;
        var division = Math.Clamp(session.SnapDivision, 1, 1920);
        var changes = session.Document.Notes.Where(n => (allNotes || session.Selection.Contains(n.Id)) &&
                (session.AllowOwnerProtectedEdits || !session.IsOwnerProtected(n)))
            .Select(n => (n.Id, Head: n.Beat.Snap(division), Tail: n.EndBeat?.Snap(division), Original: n))
            .Where(n => n.Head.CompareTo(n.Original.Beat) != 0 ||
                n.Tail.HasValue && n.Tail.Value.CompareTo(n.Original.EndBeat!.Value) != 0)
            .ToDictionary(n => n.Id, n => (n.Head, n.Tail));
        if (changes.Count == 0) return 0;
        var serial = session.ChangeSerial;
        session.Mutate(doc =>
        {
            foreach (var note in doc.Notes)
                if (changes.TryGetValue(note.Id, out var change))
                {
                    note.Beat = change.Head;
                    // Keep an existing LN as an LN even when a coarse grid would collapse it.
                    note.EndBeat = change.Tail.HasValue && change.Tail.Value.CompareTo(change.Head) <= 0
                        ? BeatPosition.FromDouble(change.Head.Value + 1d / division, division) : change.Tail;
                }
        });
        return session.ChangeSerial == serial ? 0 : changes.Count;
    }
}

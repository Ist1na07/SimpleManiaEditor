using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

/// <summary>
/// Mirrors one visible-selection batch around the official lane centre.  The
/// caller owns visibility filtering; permission, owner-lock and undo semantics
/// remain centralized here so desktop clients cannot diverge.
/// </summary>
public static class NoteMirrorEditingService
{
    public static int MirrorSelection(EditorSession session, IEnumerable<Guid> selectedNoteIds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selectedNoteIds);

        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能镜像 Note。");
        if (!session.CanEditNotes)
            throw new InvalidOperationException("当前没有编辑 Note 的权限，不能镜像 Note。");

        var requested = selectedNoteIds.Distinct().ToHashSet();
        if (requested.Count == 0) return 0;
        var notes = session.Document.Notes
            .Where(note => requested.Contains(note.Id))
            .ToArray();
        if (notes.Length == 0) return 0;

        if (!session.AllowOwnerProtectedEdits && notes.Any(session.IsOwnerProtected))
            throw new InvalidOperationException(
                "镜像目标与房主锁定选区相交，成员不能移动 Note。");

        var keyCount = Math.Clamp(session.Document.KeyCount, 1, 18);
        var changedIds = notes
            .Where(note => WouldChange(note, keyCount))
            .Select(note => note.Id)
            .ToHashSet();
        if (changedIds.Count == 0) return 0;

        session.Mutate(document =>
        {
            foreach (var note in document.Notes.Where(note => changedIds.Contains(note.Id)))
                Mirror(note, keyCount);
        });
        return changedIds.Count;
    }

    private static bool WouldChange(ChartNote note, int keyCount)
    {
        var sourceColumn = note.IsDraft ? note.DraftColumn : note.Lane;
        var targetColumn = keyCount - 1 - sourceColumn;
        return targetColumn >= 0 && targetColumn < keyCount
            ? note.IsDraft || note.Lane != targetColumn || note.DraftColumn != 0
            : !note.IsDraft || note.DraftColumn != targetColumn || note.Lane != 0;
    }

    private static void Mirror(ChartNote note, int keyCount)
    {
        var sourceColumn = note.IsDraft ? note.DraftColumn : note.Lane;
        var targetColumn = keyCount - 1 - sourceColumn;
        if (targetColumn >= 0 && targetColumn < keyCount)
        {
            note.Lane = targetColumn;
            note.IsDraft = false;
            note.DraftColumn = 0;
        }
        else
        {
            note.IsDraft = true;
            note.DraftColumn = targetColumn;
            note.Lane = 0;
        }
    }
}

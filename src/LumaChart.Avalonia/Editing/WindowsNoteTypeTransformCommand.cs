using LumaChart.Application.Editing;
using LumaChart.Core.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Editing;

/// <summary>
/// Windows Avalonia command adapter for the shared note-type transform engine.
/// It owns editor permission, owner-lock, history, and selection semantics only;
/// the conversion algorithm remains in <see cref="NoteTypeTransformService"/>.
/// </summary>
public static class WindowsNoteTypeTransformCommand
{
    public static int FilterSelectionByNoteType(
        EditorSession session,
        bool longNotes,
        IReadOnlySet<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var visible = session.Document.Notes
            .Where(note => session.Selection.Contains(note.Id) &&
                           (hiddenGroups == null || !hiddenGroups.Contains(note.Group)) &&
                           note.IsHold == longNotes)
            .Select(note => note.Id)
            .ToHashSet();
        session.Selection.IntersectWith(visible);
        return visible.Count;
    }

    public static int CountSelectedLongNotes(
        EditorSession session,
        IReadOnlySet<int>? hiddenGroups = null) =>
        SelectEditableIds(session, static note => note.IsHold, hiddenGroups).Count;

    public static int CountSelectedTaps(
        EditorSession session,
        IReadOnlySet<int>? hiddenGroups = null) =>
        SelectEditableIds(session, static note => !note.IsHold, hiddenGroups).Count;

    public static NoteTransformResult ConvertLongNotesToTaps(
        EditorSession session,
        LongNoteToTapMode mode,
        IReadOnlySet<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureNoteEditAllowed(session);
        var ids = SelectEditableIds(session, static note => note.IsHold, hiddenGroups);
        return Execute(
            session,
            ids,
            document => NoteTypeTransformService.ConvertLongNotesToTaps(
                document,
                ids,
                session.SnapDivision,
                mode));
    }

    public static NoteTransformResult ConvertTapsToLongNotes(
        EditorSession session,
        IReadOnlySet<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureNoteEditAllowed(session);
        var ids = SelectEditableIds(session, static note => !note.IsHold, hiddenGroups);
        return Execute(
            session,
            ids,
            document => NoteTypeTransformService.ConvertTapsToLongNotes(
                document,
                ids,
                session.SnapDivision));
    }

    private static NoteTransformResult Execute(
        EditorSession session,
        IReadOnlySet<Guid> ids,
        Func<ChartDocument, NoteTransformResult> transform)
    {
        if (ids.Count == 0) return new NoteTransformResult(0, 0, 0, new HashSet<Guid>());

        // Match WPF: preview the exact shared transform against a clone before
        // opening a history entry, then reject any generated/extended output
        // that would enter a time-based owner lock.
        var preview = session.Document.Clone();
        var previewResult = transform(preview);
        if (!session.AllowOwnerProtectedEdits && preview.Notes
                .Where(note => previewResult.Selection.Contains(note.Id))
                .Any(note => session.IntersectsOwnerProtectedRegion(
                    note.Beat,
                    note.EndBeat ?? note.Beat)))
            throw new InvalidOperationException("转换目标与房主锁定范围相交，成员不能修改。");

        NoteTransformResult? result = null;
        session.Mutate(document => result = transform(document));
        result ??= new NoteTransformResult(0, 0, 0, new HashSet<Guid>());

        session.Selection.Clear();
        session.Selection.UnionWith(result.Selection);
        return result;
    }

    private static HashSet<Guid> SelectEditableIds(
        EditorSession session,
        Func<ChartNote, bool> predicate,
        IReadOnlySet<int>? hiddenGroups)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Document.Notes
            .Where(note => session.Selection.Contains(note.Id) &&
                           (hiddenGroups == null || !hiddenGroups.Contains(note.Group)) &&
                           predicate(note) &&
                           (session.AllowOwnerProtectedEdits || !session.IsOwnerProtected(note)))
            .Select(note => note.Id)
            .ToHashSet();
    }

    private static void EnsureNoteEditAllowed(EditorSession session)
    {
        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能转换 Note 类型。");
        if (!session.CanEditNotes)
            throw new InvalidOperationException("当前没有编辑 Note 的权限。");
    }
}

using LumaChart.Application.Editing;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Editing;

/// <summary>
/// Thin Windows ProEditor mutations. Pointer previews stay immutable; release
/// commits the complete official/draft identity transition as one undo entry.
/// </summary>
public static class WindowsProEditorCommand
{
    private const double BeatEpsilon = 1e-7;

    public static Guid? PlaceTap(
        EditorSession session,
        int workspaceColumn,
        BeatPosition beat,
        ProEditorColumnRange allowedColumns,
        BasicBeatPlacementPolicy placementPolicy,
        int group = 0, Guid? replacedNoteId = null, long? expectedChangeSerial = null, CenterSymmetrySettings? symmetry = null) =>
        Place(session, workspaceColumn, beat, null, allowedColumns, placementPolicy, group, replacedNoteId, expectedChangeSerial, symmetry);

    public static Guid? PlaceHold(
        EditorSession session,
        int workspaceColumn,
        BeatPosition firstBeat,
        BeatPosition secondBeat,
        ProEditorColumnRange allowedColumns,
        BasicBeatPlacementPolicy placementPolicy,
        int group = 0, Guid? replacedNoteId = null, long? expectedChangeSerial = null, CenterSymmetrySettings? symmetry = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var start = ResolveBeat(session, firstBeat, placementPolicy);
        var end = ResolveBeat(session, secondBeat, placementPolicy);
        if (end.CompareTo(start) < 0) (start, end) = (end, start);
        if (end.CompareTo(start) == 0)
            end = BasicChartEditingService.ApplyRelativeMove(
                start,
                new RelativeBeatMove(1, Math.Clamp(session.SnapDivision, 1, 1920)));
        return Place(
            session,
            workspaceColumn,
            start,
            end,
            allowedColumns,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat,
            group, replacedNoteId, expectedChangeSerial, symmetry);
    }

    public static int MoveSelection(
        EditorSession session,
        int columnDelta,
        RelativeBeatMove beatMove,
        ProEditorColumnRange allowedColumns,
        IEnumerable<Guid>? noteIds = null,
        IReadOnlySet<int>? hiddenGroups = null,
        long? expectedChangeSerial = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureEditable(session, "移动");
        if (expectedChangeSerial.HasValue && session.ChangeSerial != expectedChangeSerial.Value)
            throw new InvalidOperationException("Note 移动已按房间最新状态重新同步");
        if (beatMove.Division is < 1 or > 1920)
            throw new ArgumentOutOfRangeException(nameof(beatMove));

        var selectedIds = (noteIds ?? session.Selection).ToHashSet();
        var selected = session.Document.Notes
            .Where(note => selectedIds.Contains(note.Id) &&
                           hiddenGroups?.Contains(note.Group) != true)
            .ToArray();
        if (selected.Length == 0) return 0;

        foreach (var note in selected)
        {
            if (!session.AllowOwnerProtectedEdits && session.IsOwnerProtected(note))
                throw new InvalidOperationException("该 Note 位于房主锁定选区，成员不能拖动");
            var destinationColumn = checked(ProEditorWorkspaceGeometry.GetColumn(note) + columnDelta);
            if (!allowedColumns.Contains(destinationColumn))
                throw new ArgumentOutOfRangeException(nameof(columnDelta), "目标列超出 ProEditor 工作区。");
            var destinationBeat = BasicChartEditingService.ApplyRelativeMove(note.Beat, beatMove);
            var destinationEnd = note.EndBeat.HasValue
                ? BasicChartEditingService.ApplyRelativeMove(note.EndBeat.Value, beatMove)
                : destinationBeat;
            if (destinationBeat.CompareTo(BeatPosition.Zero) < 0 && !LumaChart.Core.IO.OsuSourceEnvelope.HasSource(session.Document))
                throw new InvalidOperationException("移动后的 Beat 不能早于 0。");
            if (!session.AllowOwnerProtectedEdits &&
                session.IntersectsOwnerProtectedRegion(destinationBeat, destinationEnd))
                throw new InvalidOperationException("目标位置位于房主锁定选区，成员不能移动 Note");
        }

        if (columnDelta == 0 && beatMove.Steps == 0 && beatMove.Correction == 0 && !beatMove.SnapAbsolute) return 0;
        var ids = selected.Select(note => note.Id).ToHashSet();
        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            foreach (var note in document.Notes.Where(note => ids.Contains(note.Id)))
            {
                var destinationColumn = checked(
                    ProEditorWorkspaceGeometry.GetColumn(note) + columnDelta);
                ProEditorWorkspaceGeometry.ApplyColumn(note, destinationColumn, document.KeyCount);
                note.Beat = BasicChartEditingService.ApplyRelativeMove(note.Beat, beatMove);
                if (note.EndBeat.HasValue)
                    note.EndBeat = BasicChartEditingService.ApplyRelativeMove(note.EndBeat.Value, beatMove);
            }
        });
        return session.ChangeSerial == serial ? 0 : selected.Length;
    }

    private static Guid? Place(
        EditorSession session,
        int workspaceColumn,
        BeatPosition firstBeat,
        BeatPosition? endBeat,
        ProEditorColumnRange allowedColumns,
        BasicBeatPlacementPolicy placementPolicy,
        int group, Guid? replacedNoteId = null, long? expectedChangeSerial = null, CenterSymmetrySettings? symmetry = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureEditable(session, "放置");
        if (expectedChangeSerial.HasValue && session.ChangeSerial != expectedChangeSerial)
            throw new InvalidOperationException("放置已按最新谱面状态取消，请重新操作");
        if (replacedNoteId.HasValue)
        {
            var original = session.Document.Notes.FirstOrDefault(n => n.Id == replacedNoteId);
            if (original == null || ProEditorWorkspaceGeometry.GetColumn(original) != workspaceColumn)
                throw new InvalidOperationException("被替换的按键已改变，请重新操作");
            if (!session.AllowOwnerProtectedEdits && session.IsOwnerProtected(original))
                throw new InvalidOperationException("该按键位于锁定选区，不能替换");
        }
        if (!allowedColumns.Contains(workspaceColumn))
            throw new ArgumentOutOfRangeException(nameof(workspaceColumn));
        if (group is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(group));

        var replacedIds = replacedNoteId.HasValue
            ? CenterSymmetryService.ExpandDeletion(session, [replacedNoteId.Value], symmetry)
            : new HashSet<Guid>();

        var start = ResolveBeat(session, firstBeat, placementPolicy);
        BeatPosition? end = endBeat.HasValue
            ? ResolveBeat(session, endBeat.Value, placementPolicy)
            : null;
        if (start.CompareTo(BeatPosition.Zero) < 0 && !LumaChart.Core.IO.OsuSourceEnvelope.HasSource(session.Document)) return null;
        if (end.HasValue && end.Value.CompareTo(start) <= 0)
            throw new ArgumentException("LN End Beat 必须晚于 Beat。", nameof(endBeat));
        if (session.Document.Notes.Any(note =>
                !replacedIds.Contains(note.Id) &&
                ProEditorWorkspaceGeometry.GetColumn(note) == workspaceColumn &&
                Math.Abs(note.Beat.Value - start.Value) <= BeatEpsilon))
            return null;
        if (!session.AllowOwnerProtectedEdits &&
            session.IntersectsOwnerProtectedRegion(start, end ?? start))
            throw new InvalidOperationException("该位置位于房主锁定选区，成员不能放置 Note");

        var id = Guid.NewGuid();
        var note = new ChartNote { Id = id, Beat = start, EndBeat = end, Group = group };
        ProEditorWorkspaceGeometry.ApplyColumn(note, workspaceColumn, session.Document.KeyCount);
        var companion = symmetry == null ? null : CenterSymmetryService.CreateCompanion(note, symmetry, session.Document.KeyCount);
        if (companion != null && !CenterSymmetryService.CanAdd(session, companion,
                session.Document.Notes.Where(n => !replacedIds.Contains(n.Id)).Append(note))) companion = null;
        session.Mutate(document =>
        {
            document.Notes.RemoveAll(n => replacedIds.Contains(n.Id));
            document.Notes.Add(note);
            if (companion != null) document.Notes.Add(companion);
        });
        if (!session.Document.Notes.Any(note => note.Id == id)) return null;
        session.Selection.Clear();
        session.Selection.Add(id);
        if (companion != null) session.Selection.Add(companion.Id);
        return id;
    }

    private static BeatPosition ResolveBeat(
        EditorSession session,
        BeatPosition beat,
        BasicBeatPlacementPolicy placementPolicy) => placementPolicy switch
    {
        BasicBeatPlacementPolicy.SnapToCurrentSubdivision =>
            beat.Snap(Math.Clamp(session.SnapDivision, 1, 1920)),
        BasicBeatPlacementPolicy.PreserveAuthoredBeat => beat,
        _ => throw new ArgumentOutOfRangeException(nameof(placementPolicy))
    };

    private static void EnsureEditable(EditorSession session, string operation)
    {
        if (session.IsReadOnly)
            throw new InvalidOperationException(AvaloniaLocalizationCatalog.Format("当前谱面为只读状态，不能{0} Note", operation));
        if (!session.CanEditNotes)
            throw new InvalidOperationException(AvaloniaLocalizationCatalog.Format("当前权限不能{0} Note", operation));
    }
}

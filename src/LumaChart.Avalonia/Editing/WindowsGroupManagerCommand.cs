using System.Globalization;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Editing;

public sealed record WindowsGroupManagerRow(
    int Group,
    int NoteCount,
    string? DominantSound,
    bool IsVisible,
    bool IsCurrent);

public sealed record WindowsGroupManagerSnapshot(
    int ActivePlacementGroup,
    int VisibleEditableSelectionCount,
    IReadOnlyList<WindowsGroupManagerRow> Rows);

public sealed record WindowsGroupMutationResult(
    int CandidateCount,
    int ChangedCount,
    bool Applied);

/// <summary>
/// Windows Group Manager command boundary. Placement/visibility are per-tab view
/// state; assignment and dissolve are the only document mutations and each opens
/// at most one history entry.
/// </summary>
public static class WindowsGroupManagerCommand
{
    public static WindowsGroupManagerSnapshot ReadSnapshot(DesktopChartTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var notesByGroup = tab.Session.Document.Notes
            .GroupBy(note => note.Group)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var groups = notesByGroup.Keys
            .Append(0)
            .Append(tab.ActivePlacementGroup)
            .Distinct()
            .OrderBy(group => group)
            .ToArray();
        tab.HiddenGroups.IntersectWith(groups);

        var rows = groups.Select(group =>
        {
            var notes = notesByGroup.GetValueOrDefault(group) ?? [];
            var dominantSound = notes
                .Where(note => !string.IsNullOrWhiteSpace(note.Sound))
                .GroupBy(note => note.Sound!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(soundGroup => soundGroup.Count())
                .Select(soundGroup => Path.GetFileName(soundGroup.Key))
                .FirstOrDefault();
            return new WindowsGroupManagerRow(
                group,
                notes.Length,
                dominantSound,
                !tab.HiddenGroups.Contains(group),
                group == tab.ActivePlacementGroup);
        }).ToArray();

        return new WindowsGroupManagerSnapshot(
            tab.ActivePlacementGroup,
            VisibleEditableSelectionIds(tab).Count,
            rows);
    }

    public static int ParseGroupId(string? text)
    {
        if (!int.TryParse(
                text?.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var group) ||
            group is < 0 or > ushort.MaxValue)
            throw new FormatException("Group ID 范围为 0–65535。");
        return group;
    }

    public static void SetPlacementGroup(DesktopChartTab tab, int group)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ValidateGroup(group);
        tab.ActivePlacementGroup = group;
        tab.HiddenGroups.Remove(group);
    }

    public static int CreateAndUse(DesktopChartTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var maximum = tab.Session.Document.Notes
            .Select(note => note.Group)
            .Append(tab.ActivePlacementGroup)
            .DefaultIfEmpty(0)
            .Max();
        var next = maximum >= ushort.MaxValue ? ushort.MaxValue : maximum + 1;
        SetPlacementGroup(tab, next);
        return next;
    }

    public static WindowsGroupMutationResult AssignSelection(DesktopChartTab tab, int group)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ValidateGroup(group);
        EnsureNoteEditAllowed(tab.Session);
        var ids = VisibleEditableSelectionIds(tab);
        if (ids.Count == 0) return new WindowsGroupMutationResult(0, 0, false);
        var changedCount = tab.Session.Document.Notes.Count(note =>
            ids.Contains(note.Id) && note.Group != group);
        tab.HiddenGroups.Remove(group);
        if (changedCount == 0)
            return new WindowsGroupMutationResult(ids.Count, 0, false);
        var serial = tab.Session.ChangeSerial;
        tab.Session.Mutate(document =>
        {
            foreach (var note in document.Notes.Where(note => ids.Contains(note.Id)))
                note.Group = group;
        });
        return new WindowsGroupMutationResult(
            ids.Count,
            changedCount,
            tab.Session.ChangeSerial != serial);
    }

    public static WindowsGroupMutationResult Dissolve(DesktopChartTab tab, int group)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ValidateGroup(group);
        if (group == 0 || tab.HiddenGroups.Contains(group))
            return new WindowsGroupMutationResult(0, 0, false);

        EnsureNoteEditAllowed(tab.Session);
        var ids = tab.Session.Document.Notes
            .Where(note => note.Group == group &&
                           (tab.Session.AllowOwnerProtectedEdits ||
                            !tab.Session.IsOwnerProtected(note)))
            .Select(note => note.Id)
            .ToHashSet();
        tab.HiddenGroups.Remove(group);
        if (tab.ActivePlacementGroup == group) tab.ActivePlacementGroup = 0;
        if (ids.Count == 0) return new WindowsGroupMutationResult(0, 0, false);
        var serial = tab.Session.ChangeSerial;
        tab.Session.Mutate(document =>
        {
            foreach (var note in document.Notes.Where(note => ids.Contains(note.Id)))
                note.Group = 0;
        });
        return new WindowsGroupMutationResult(
            ids.Count,
            ids.Count,
            tab.Session.ChangeSerial != serial);
    }

    public static int SetGroupVisible(DesktopChartTab tab, int group, bool visible)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ValidateGroup(group);
        if (visible) tab.HiddenGroups.Remove(group);
        else tab.HiddenGroups.Add(group);
        return RemoveHiddenSelection(tab);
    }

    public static void ShowAll(DesktopChartTab tab) => tab.HiddenGroups.Clear();

    public static int HideNonzero(DesktopChartTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        tab.HiddenGroups.Clear();
        foreach (var group in tab.Session.Document.Notes
                     .Select(note => note.Group)
                     .Where(group => group != 0)
                     .Distinct())
            tab.HiddenGroups.Add(group);
        return RemoveHiddenSelection(tab);
    }

    public static int RemoveHiddenSelection(DesktopChartTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return RemoveHiddenSelection(tab.Session, tab.HiddenGroups);
    }

    public static int RemoveHiddenSelection(
        EditorSession session,
        IReadOnlySet<int> hiddenGroups)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(hiddenGroups);
        if (hiddenGroups.Count == 0 || session.Selection.Count == 0) return 0;
        var before = session.Selection.Count;
        var hiddenSelectedIds = session.Document.Notes
            .Where(note =>
                session.Selection.Contains(note.Id) &&
                hiddenGroups.Contains(note.Group))
            .Select(note => note.Id)
            .ToArray();
        session.Selection.ExceptWith(hiddenSelectedIds);
        return before - session.Selection.Count;
    }

    public static HashSet<Guid> VisibleSelectionIds(DesktopChartTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return tab.Session.Document.Notes
            .Where(note => tab.Session.Selection.Contains(note.Id) &&
                           !tab.HiddenGroups.Contains(note.Group))
            .Select(note => note.Id)
            .ToHashSet();
    }

    public static HashSet<Guid> VisibleEditableSelectionIds(DesktopChartTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return VisibleEditableSelectionIds(tab.Session, tab.HiddenGroups);
    }

    public static HashSet<Guid> VisibleEditableSelectionIds(
        EditorSession session,
        IReadOnlySet<int> hiddenGroups)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(hiddenGroups);
        return session.Document.Notes
            .Where(note => session.Selection.Contains(note.Id) &&
                           !hiddenGroups.Contains(note.Group) &&
                           (session.AllowOwnerProtectedEdits ||
                            !session.IsOwnerProtected(note)))
            .Select(note => note.Id)
            .ToHashSet();
    }

    private static void ValidateGroup(int group)
    {
        if (group is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(group), "Group ID 范围为 0–65535。");
    }

    private static void EnsureNoteEditAllowed(EditorSession session)
    {
        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能修改 Note 分组。");
        if (!session.CanEditNotes)
            throw new InvalidOperationException("当前没有编辑 Note 分组的权限。");
    }
}

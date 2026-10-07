using LumaChart.Application.Editing;

namespace LumaChart.Avalonia.Editing;

public readonly record struct WindowsEditorHistoryOutcome(
    bool Changed,
    string Status,
    ScopedHistoryResult? ScopedResult);

/// <summary>
/// Keeps WPF 1.1.0's local and live-room undo/redo semantics in one command
/// path so toolbar, floating palette and keyboard shortcuts cannot diverge.
/// </summary>
public static class WindowsEditorHistoryCommand
{
    public static WindowsEditorHistoryOutcome Execute(
        EditorSession session,
        bool liveOnlineRoom,
        bool redo)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!liveOnlineRoom)
        {
            var changed = redo ? session.Redo() : session.Undo();
            return new WindowsEditorHistoryOutcome(
                changed,
                changed
                    ? redo ? "已重做" : "已撤销"
                    : redo ? "没有可重做操作" : "没有可撤销操作",
                null);
        }

        var result = redo ? session.RedoOwnChanges() : session.UndoOwnChanges();
        return new WindowsEditorHistoryOutcome(
            result == ScopedHistoryResult.Applied,
            result switch
            {
                ScopedHistoryResult.Applied =>
                    redo ? "已重做自己的上一项操作" : "已撤销自己的上一项操作",
                ScopedHistoryResult.Conflict =>
                    "该对象后来已被其他成员修改，为避免覆盖对方内容，本次撤销/重做已取消",
                ScopedHistoryResult.PermissionDenied =>
                    "当前房间权限不允许撤销/重做这项操作",
                _ => redo ? "没有可重做的本人操作" : "没有可撤销的本人操作"
            },
            result);
    }
}

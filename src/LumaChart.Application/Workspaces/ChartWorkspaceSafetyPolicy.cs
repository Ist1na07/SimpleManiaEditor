namespace LumaChart.Application.Workspaces;

public enum ChartTabClosePrompt
{
    None,
    UnsavedChanges,
    PreservedRoomDraft
}

/// <summary>
/// Pure safety decisions shared by the Windows shell and cross-platform specs.
/// A preserved room draft is the user's only durable copy until it is exported,
/// even when its in-memory editor session currently reports a clean state.
/// </summary>
public static class ChartWorkspaceSafetyPolicy
{
    public const string OnlineNoteSoundImportBlockedMessage =
        "当前联机协议只同步 Note 的 Key 音文件名，不会把新选择的本地音频分发给其他成员。请在建房前加入 Key 音，或在房间结束后的“房间留存”标签中另存后再选择文件。";

    public static ChartTabClosePrompt GetClosePrompt(bool isDirty, bool isPreservedRoomDraft) =>
        isPreservedRoomDraft
            ? ChartTabClosePrompt.PreservedRoomDraft
            : isDirty
                ? ChartTabClosePrompt.UnsavedChanges
                : ChartTabClosePrompt.None;

    public static bool AllowsLocalNoteSoundImport(bool isOnlineRoomChart) => !isOnlineRoomChart;
}

using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

internal enum OnlineRoomExitChoice
{
    Cancel,
    Confirm
}

/// <summary>
/// Confirms the room-owner close/member leave operation before a live room tab
/// or the application is closed. Copy and action labels match WPF 1.1.0.
/// </summary>
internal sealed class OnlineRoomExitConfirmationDialog :
    WindowsWpf110ChoiceDialog<OnlineRoomExitChoice>
{
    internal const string DialogAutomationId = "OnlineRooms.ExitDialog";
    internal const string MessageAutomationId = "OnlineRooms.ExitDialog.Message";

    public OnlineRoomExitConfirmationDialog(
        string roomName,
        bool isOwner,
        double uiScale,
        double fontScale)
        : base(
            CreateTitle(isOwner),
            CreateMessage(roomName, isOwner),
            WindowsWpf110DialogTone.Warning,
            CreateButtons(isOwner),
            OnlineRoomExitChoice.Cancel,
            uiScale,
            fontScale,
            DialogAutomationId,
            MessageAutomationId,
            CreateTitle(isOwner))
    {
        global::Avalonia.Automation.AutomationProperties.SetName(
            this,
            CreateTitle(isOwner));
    }

    internal static string CreateTitle(bool isOwner) =>
        isOwner ? "关闭联机房间" : "退出联机房间";

    internal static string CreateMessage(string roomName, bool isOwner) =>
        isOwner
            ? AvaloniaLocalizationCatalog.Format("你是“{0}”的房主。关闭后其他成员会离开；当前谱面不会关闭，并将恢复为本地编辑。", roomName)
            : AvaloniaLocalizationCatalog.Format("退出“{0}”后，这张谱将恢复为本地编辑状态。", roomName);

    internal static IReadOnlyList<
        WindowsWpf110DialogButton<OnlineRoomExitChoice>> CreateButtons(
            bool isOwner) =>
    [
        new(
            OnlineRoomExitChoice.Cancel,
            "取消",
            "OnlineRooms.ExitDialog.Cancel",
            IsDefault: true,
            IsCancel: true),
        new(
            OnlineRoomExitChoice.Confirm,
            isOwner ? "关闭房间" : "退出房间",
            "OnlineRooms.ExitDialog.Confirm")
    ];
}

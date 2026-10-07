using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

internal enum PreservedRoomDraftCloseChoice
{
    Cancel,
    SaveAs,
    Discard
}

/// <summary>
/// Protects a retained online-room chart whose editing cache is still the
/// user's only durable copy. Explicitly closing the tab deletes that cache, so
/// a clean retained tab needs the same warning as a dirty one.
/// </summary>
internal sealed class PreservedRoomDraftCloseDialog :
    WindowsWpf110ChoiceDialog<PreservedRoomDraftCloseChoice>
{
    internal const string DialogAutomationId = "PreservedRoomDraftClose.Dialog";
    internal const string DialogAutomationName = "关闭房间留存";
    internal const string MessageAutomationId = "Dialog.Message";

    public PreservedRoomDraftCloseDialog(
        string chartTitle,
        double uiScale,
        double fontScale)
        : base(
            DialogAutomationName,
            CreateMessage(chartTitle),
            WindowsWpf110DialogTone.Warning,
            CreateButtons(),
            PreservedRoomDraftCloseChoice.Cancel,
            uiScale,
            fontScale,
            DialogAutomationId,
            MessageAutomationId,
            DialogAutomationName)
    {
        global::Avalonia.Automation.AutomationProperties.SetName(
            this,
            DialogAutomationName);
    }

    internal static string CreateMessage(string chartTitle) =>
        AvaloniaLocalizationCatalog.Format("“{0}”是房间关闭或被移出时保留的本地成果。\n\n它当前只存在持久编辑缓存中；即使状态显示“已保存”，关闭标签也会删除这份唯一留存。\n\n请先另存到正式谱面目录，或明确选择放弃。", chartTitle);

    internal static IReadOnlyList<
        WindowsWpf110DialogButton<PreservedRoomDraftCloseChoice>> CreateButtons() =>
    [
        new(
            PreservedRoomDraftCloseChoice.Cancel,
            "取消",
            "PreservedRoomDraftClose.Cancel",
            IsCancel: true),
        new(
            PreservedRoomDraftCloseChoice.Discard,
            "放弃并关闭",
            "PreservedRoomDraftClose.Discard"),
        new(
            PreservedRoomDraftCloseChoice.SaveAs,
            "另存为后关闭",
            "PreservedRoomDraftClose.SaveAs",
            IsDefault: true)
    ];
}

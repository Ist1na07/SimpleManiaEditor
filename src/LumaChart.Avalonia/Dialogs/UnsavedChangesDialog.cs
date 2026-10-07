using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

internal enum UnsavedChangesChoice
{
    Cancel,
    Save,
    Discard
}

/// <summary>
/// Windows desktop save/discard/cancel prompt used by explicit tab closes and
/// the application-close preflight. Closing the dialog itself always means cancel.
/// </summary>
internal sealed class UnsavedChangesDialog :
    WindowsWpf110ChoiceDialog<UnsavedChangesChoice>
{
    internal const string DialogAutomationId = "UnsavedChanges.Dialog";
    internal const string DialogAutomationName = "未保存的更改";
    internal const string MessageAutomationId = "Dialog.Message";

    public UnsavedChangesDialog(
        string chartTitle,
        bool applicationClosing,
        double uiScale,
        double fontScale)
        : base(
            "关闭谱面",
            CreateMessage(chartTitle, applicationClosing),
            WindowsWpf110DialogTone.Warning,
            CreateButtons(),
            UnsavedChangesChoice.Cancel,
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

    internal static string CreateMessage(
        string chartTitle,
        bool applicationClosing) =>
        AvaloniaLocalizationCatalog.Format("“{0}”有尚未保存的更改。\n\n是：保存后关闭\n否：放弃更改\n取消：继续编辑{1}", chartTitle, (applicationClosing ? AvaloniaLocalizationCatalog.Text("\n\n关闭应用前会依次处理其他未保存标签。") : string.Empty));

    internal static IReadOnlyList<
        WindowsWpf110DialogButton<UnsavedChangesChoice>> CreateButtons() =>
    [
        new(
            UnsavedChangesChoice.Cancel,
            "取消",
            "UnsavedChanges.Cancel",
            IsDefault: true,
            IsCancel: true),
        new(
            UnsavedChangesChoice.Discard,
            "否",
            "UnsavedChanges.Discard",
            AutomationName: "放弃更改"),
        new(
            UnsavedChangesChoice.Save,
            "是",
            "UnsavedChanges.Save")
    ];
}

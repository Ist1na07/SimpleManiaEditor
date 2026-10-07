namespace LumaChart.Avalonia.Dialogs;

/// <summary>
/// Backup/cache action prompt. WPF 1.1.0 uses LumaDialog for these operations;
/// the cancel choice remains the safe default for confirmation flows.
/// </summary>
internal sealed class SettingsActionDialog : WindowsWpf110ChoiceDialog<bool>
{
    internal const string DialogAutomationId = "Settings.ActionDialog";
    internal const string MessageAutomationId = "Settings.ActionDialog.Message";

    public SettingsActionDialog(
        string title,
        string message,
        string primaryText,
        bool showCancel,
        bool dangerous,
        double uiScale,
        double fontScale)
        : base(
            title,
            message,
            dangerous
                ? WindowsWpf110DialogTone.Warning
                : showCancel
                    ? WindowsWpf110DialogTone.Question
                    : WindowsWpf110DialogTone.Information,
            CreateButtons(primaryText, showCancel),
            closeResult: false,
            uiScale,
            fontScale,
            automationId: DialogAutomationId,
            messageAutomationId: MessageAutomationId)
    {
    }

    internal static IReadOnlyList<WindowsWpf110DialogButton<bool>> CreateButtons(
        string primaryText,
        bool showCancel) =>
        showCancel
            ?
            [
                new WindowsWpf110DialogButton<bool>(
                    false,
                    "取消",
                    "Settings.ActionDialog.Cancel",
                    IsDefault: true,
                    IsCancel: true,
                    AutomationName: "取消"),
                new WindowsWpf110DialogButton<bool>(
                    true,
                    primaryText,
                    "Settings.ActionDialog.Primary",
                    AutomationName: primaryText)
            ]
            :
            [
                new WindowsWpf110DialogButton<bool>(
                    true,
                    primaryText,
                    "Settings.ActionDialog.Primary",
                    IsDefault: true,
                    AutomationName: primaryText)
            ];
}

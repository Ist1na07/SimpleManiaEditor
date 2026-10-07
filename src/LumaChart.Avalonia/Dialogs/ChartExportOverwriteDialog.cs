using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

/// <summary>
/// Owner-bound replacement confirmation used after the Windows save picker has
/// returned a corrected extension. Keeping this prompt inside Avalonia avoids
/// the native file-dialog teardown race and guarantees that it remains above
/// the editor owner, including when the editor is topmost.
/// </summary>
internal sealed class ChartExportOverwriteDialog : WindowsWpf110ChoiceDialog<bool>
{
    internal const string DialogAutomationId = "ChartExport.OverwriteDialog";
    internal const string MessageAutomationId = "ChartExport.OverwriteDialog.Message";
    internal const string DeclineAutomationId = "ChartExport.OverwriteDialog.No";
    internal const string ConfirmAutomationId = "ChartExport.OverwriteDialog.Yes";

    public ChartExportOverwriteDialog(
        string destinationPath,
        double uiScale,
        double fontScale)
        : base(
            "确认另存为",
            CreateMessage(destinationPath),
            WindowsWpf110DialogTone.Warning,
            [
                new(
                    false,
                    "否",
                    DeclineAutomationId,
                    IsCancel: true,
                    AutomationName: "否"),
                new(
                    true,
                    "是",
                    ConfirmAutomationId,
                    IsDefault: true,
                    AutomationName: "是")
            ],
            closeResult: false,
            uiScale,
            fontScale,
            automationId: DialogAutomationId,
            messageAutomationId: MessageAutomationId)
    {
    }

    internal static string CreateMessage(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return AvaloniaLocalizationCatalog.Format("“{0}”已存在。\n\n是否替换它？", Path.GetFileName(destinationPath));
    }
}

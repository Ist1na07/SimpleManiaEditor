namespace LumaChart.Avalonia.Dialogs;

internal enum ChartValidationDialogTone
{
    Information,
    Warning
}

internal sealed record ChartValidationDialogScaleDiagnostics(
    double RequestedUiScale,
    double RequestedFontScale,
    double ActualRootScaleX,
    double ActualRootScaleY,
    double MessageFontSize,
    bool UsesLayoutTransform);

/// <summary>
/// WPF 1.1.0 chart-check message. Feature semantics and legacy diagnostics stay
/// local while all modal chrome and keyboard behavior come from one contract.
/// </summary>
internal sealed class ChartValidationDialog : WindowsWpf110ChoiceDialog<bool>
{
    internal const string DialogAutomationId = "ChartValidation.Dialog";
    internal const string MessageAutomationId = "ChartValidation.Dialog.Message";

    public ChartValidationDialog(
        string message,
        ChartValidationDialogTone tone,
        double uiScale,
        double fontScale)
        : base(
            caption: "谱面检查器",
            message,
            tone == ChartValidationDialogTone.Warning
                ? WindowsWpf110DialogTone.Warning
                : WindowsWpf110DialogTone.Information,
            CreateButtons(),
            closeResult: true,
            uiScale,
            fontScale,
            automationId: DialogAutomationId,
            messageAutomationId: MessageAutomationId)
    {
        ScaleDiagnostics = new ChartValidationDialogScaleDiagnostics(
            Diagnostics.RequestedUiScale,
            Diagnostics.RequestedFontScale,
            Diagnostics.ActualRootScaleX,
            Diagnostics.ActualRootScaleY,
            Diagnostics.MessageFontSize,
            Diagnostics.UsesLayoutTransform);
    }

    public ChartValidationDialogScaleDiagnostics ScaleDiagnostics { get; }

    internal static IReadOnlyList<WindowsWpf110DialogButton<bool>>
        CreateButtons() =>
        [
            new WindowsWpf110DialogButton<bool>(
                true,
                "确定",
                "ChartValidation.Dialog.Confirm",
                IsDefault: true,
                AutomationName: "确定")
        ];
}

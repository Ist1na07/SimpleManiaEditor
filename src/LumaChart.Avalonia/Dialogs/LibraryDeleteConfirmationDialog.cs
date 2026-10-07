namespace LumaChart.Avalonia.Dialogs;

internal enum LibraryDeleteChoice
{
    Cancel,
    Recycle
}

internal enum LibraryDeleteDialogTone
{
    Question,
    Warning
}

internal enum LibraryDeleteDialogButtons
{
    CancelRecycle,
    Confirm
}

internal sealed record LibraryDeleteDialogScaleDiagnostics(
    double RequestedUiScale,
    double RequestedFontScale,
    double ActualRootScaleX,
    double ActualRootScaleY,
    double MessageFontSize,
    bool UsesLayoutTransform);

/// <summary>
/// Windows recycle-bin prompt with WPF 1.1.0 feature labels and the shared
/// LumaDialog chrome/keyboard contract.
/// </summary>
internal sealed class LibraryDeleteConfirmationDialog :
    WindowsWpf110ChoiceDialog<LibraryDeleteChoice>
{
    internal const string DialogAutomationId = "Library.DeleteDialog";
    internal const string MessageAutomationId = "Library.DeleteDialog.Message";

    /// <summary>
    /// WPF-parity shape: the global dialog contract requires this exact
    /// scale-aware constructor, so the permanent-delete behavior lives behind
    /// the optional overload instead of replacing the legacy signature.
    /// </summary>
    public LibraryDeleteConfirmationDialog(
        string message,
        double uiScale,
        double fontScale,
        string caption,
        LibraryDeleteDialogTone tone,
        LibraryDeleteDialogButtons buttonMode)
        : this(
            message,
            uiScale,
            fontScale,
            caption,
            tone,
            buttonMode,
            permanentDelete: false)
    {
    }

    public LibraryDeleteConfirmationDialog(
        string message,
        double uiScale,
        double fontScale,
        string caption,
        LibraryDeleteDialogTone tone,
        LibraryDeleteDialogButtons buttonMode,
        bool permanentDelete = false)
        : base(
            caption,
            message,
            tone == LibraryDeleteDialogTone.Question
                ? WindowsWpf110DialogTone.Question
                : WindowsWpf110DialogTone.Warning,
            BuildButtons(buttonMode, permanentDelete),
            closeResult: LibraryDeleteChoice.Cancel,
            uiScale,
            fontScale,
            automationId: DialogAutomationId,
            messageAutomationId: MessageAutomationId,
            iconAutomationId: "Library.DeleteDialog.Icon",
            iconAutomationHelpText: tone == LibraryDeleteDialogTone.Question
                ? "问题"
                : "警告")
    {
        ScaleDiagnostics = new LibraryDeleteDialogScaleDiagnostics(
            Diagnostics.RequestedUiScale,
            Diagnostics.RequestedFontScale,
            Diagnostics.ActualRootScaleX,
            Diagnostics.ActualRootScaleY,
            Diagnostics.MessageFontSize,
            Diagnostics.UsesLayoutTransform);
    }

    public LibraryDeleteDialogScaleDiagnostics ScaleDiagnostics { get; }

    internal static IReadOnlyList<WindowsWpf110DialogButton<LibraryDeleteChoice>>
        CreateButtons(
            LibraryDeleteDialogButtons buttonMode,
            bool permanentDelete = false) =>
        buttonMode == LibraryDeleteDialogButtons.Confirm
            ?
            [
                new WindowsWpf110DialogButton<LibraryDeleteChoice>(
                    LibraryDeleteChoice.Cancel,
                    "确定",
                    "Library.DeleteDialog.Confirm",
                    IsDefault: true,
                    AutomationName: "确定")
            ]
            :
            [
                new WindowsWpf110DialogButton<LibraryDeleteChoice>(
                    LibraryDeleteChoice.Cancel,
                    "取消",
                    "Library.DeleteDialog.Cancel",
                    IsCancel: true,
                    AutomationName: "取消"),
                new WindowsWpf110DialogButton<LibraryDeleteChoice>(
                    LibraryDeleteChoice.Recycle,
                    permanentDelete ? "删除" : "移入回收站",
                    "Library.DeleteDialog.Recycle",
                    AutomationName: permanentDelete ? "删除" : "移入回收站",
                    IsDestructive: permanentDelete)
            ];

    private static IReadOnlyList<WindowsWpf110DialogButton<LibraryDeleteChoice>>
        BuildButtons(LibraryDeleteDialogButtons buttonMode, bool permanentDelete) =>
        CreateButtons(buttonMode, permanentDelete);
}

using System.Reflection;
using Avalonia.Media;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Editing;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsDialogParitySpecs
{
    public static IReadOnlyList<WindowsDialogParitySpecResult> Run()
    {
        var results = new List<WindowsDialogParitySpecResult>();
        Check(
            results,
            "dialog-wpf-luma-chrome-metrics-tone-and-scaling-are-exact",
            VerifyChromeContract);
        Check(
            results,
            "dialog-unsaved-choice-order-copy-and-automation-remain-compatible",
            VerifyUnsavedContract);
        Check(
            results,
            "dialog-retained-room-draft-protects-clean-and-dirty-only-copy",
            VerifyPreservedRoomDraftContract);
        Check(
            results,
            "dialog-live-room-exit-copy-safe-default-and-retention-match-wpf",
            VerifyOnlineRoomExitContract);
        Check(
            results,
            "dialog-osu-audio-note-and-archive-wrappers-share-global-contract",
            VerifyFeatureWrappers);
        Check(
            results,
            "dialog-library-validation-and-settings-wrappers-share-global-contract",
            VerifyAdditionalWpfMessageWrappers);
        Check(
            results,
            "dialog-chart-export-options-use-measured-wpf-110-geometry",
            VerifyChartExportOptionsContract);
        Check(
            results,
            "dialog-chart-export-results-use-native-wpf-message-box-semantics",
            VerifyChartExportResultContract);
        Check(
            results,
            "dialog-chart-export-destination-corrects-extension-without-silent-overwrite",
            VerifyChartExportDestinationContract);
        Check(
            results,
            "dialog-workspace-recovery-warnings-are-visible-bounded-and-fail-safe",
            VerifyWorkspaceRecoveryNoticeContract);
        return results;
    }

    private static string VerifyChromeContract()
    {
        Require(
            WindowsWpf110ChoiceDialog<bool>.ContractVersion ==
            "wpf-1.1.0-dialog-r1",
            "Global WPF dialog contract version differs.");
        Require(
            WindowsWpf110ChoiceDialog<bool>.BaseWidth == 500 &&
            WindowsWpf110ChoiceDialog<bool>.BaseMinWidth == 420 &&
            WindowsWpf110ChoiceDialog<bool>.BaseMaxWidth == 680 &&
            WindowsWpf110ChoiceDialog<bool>.BaseMaxHeight == 620,
            "Dialog width/min/max metrics differ from WPF 500/420/680/620.");
        Require(
            WindowsWpf110ChoiceDialog<bool>.HeaderHeight == 44 &&
            WindowsWpf110ChoiceDialog<bool>.FooterHeight == 58 &&
            WindowsWpf110ChoiceDialog<bool>.AccentWidth == 3 &&
            WindowsWpf110ChoiceDialog<bool>.MessageFontSize == 13,
            "Dialog row/accent/font metrics differ from WPF 44/58/3/13.");

        var information =
            WindowsWpf110ChoiceDialog<bool>.ResolveTonePalette(
                WindowsWpf110DialogTone.Information);
        var warning =
            WindowsWpf110ChoiceDialog<bool>.ResolveTonePalette(
                WindowsWpf110DialogTone.Warning);
        var error =
            WindowsWpf110ChoiceDialog<bool>.ResolveTonePalette(
                WindowsWpf110DialogTone.Error);
        var question =
            WindowsWpf110ChoiceDialog<bool>.ResolveTonePalette(
                WindowsWpf110DialogTone.Question);
        Require(
            information ==
            new WindowsWpf110DialogPalette(
                Color.Parse("#6E969A"),
                Color.Parse("#20292B"),
                Color.Parse("#9FC3C6")) &&
            warning ==
            new WindowsWpf110DialogPalette(
                Color.Parse("#D39A4B"),
                Color.Parse("#2C251B"),
                Color.Parse("#FFC56F")) &&
            error ==
            new WindowsWpf110DialogPalette(
                Color.Parse("#C96268"),
                Color.Parse("#2B1E21"),
                Color.Parse("#FF969B")) &&
            question ==
            new WindowsWpf110DialogPalette(
                Color.Parse("#6F9DB1"),
                Color.Parse("#1E282E"),
                Color.Parse("#A9D4E5")),
            "Dialog information/warning/error/question palettes differ from WPF.");
        return "contract=wpf-1.1.0-dialog-r1; window=500/420/680/620; " +
               "rows=44/*/58; accent=3; font=13; four tone palettes exact";
    }

    private static string VerifyUnsavedContract()
    {
        Require(
            UnsavedChangesDialog.DialogAutomationId ==
            "UnsavedChanges.Dialog" &&
            UnsavedChangesDialog.DialogAutomationName ==
            "未保存的更改" &&
            UnsavedChangesDialog.MessageAutomationId ==
            "Dialog.Message",
            "Unsaved dialog UIA compatibility identifiers differ.");
        var message = UnsavedChangesDialog.CreateMessage(
            "Resound",
            applicationClosing: true);
        Require(
            message.StartsWith(
                "“Resound”有尚未保存的更改。",
                StringComparison.Ordinal) &&
            message.Contains(
                "是：保存后关闭\n否：放弃更改\n取消：继续编辑",
                StringComparison.Ordinal) &&
            message.EndsWith(
                "关闭应用前会依次处理其他未保存标签。",
                StringComparison.Ordinal),
            "Unsaved prompt no longer matches the WPF three-choice copy.");

        var buttons = UnsavedChangesDialog.CreateButtons();
        Require(buttons.Count == 3, "Unsaved dialog does not have three choices.");
        Require(
            buttons[0] ==
            new WindowsWpf110DialogButton<UnsavedChangesChoice>(
                UnsavedChangesChoice.Cancel,
                "取消",
                "UnsavedChanges.Cancel",
                IsDefault: true,
                IsCancel: true),
            "WPF default/cancel choice is not first.");
        Require(
            buttons[1] ==
            new WindowsWpf110DialogButton<UnsavedChangesChoice>(
                UnsavedChangesChoice.Discard,
                "否",
                "UnsavedChanges.Discard",
                AutomationName: "放弃更改"),
            "Discard does not retain WPF label plus existing physical-GUI name.");
        Require(
            buttons[2] ==
            new WindowsWpf110DialogButton<UnsavedChangesChoice>(
                UnsavedChangesChoice.Save,
                "是",
                "UnsavedChanges.Save"),
            "Save choice differs from WPF YesNoCancel.");
        return "caption=关闭谱面; UIA=未保存的更改; buttons=取消(default)/否/是; " +
               "discard UIA compatibility=放弃更改";
    }

    private static string VerifyPreservedRoomDraftContract()
    {
        RequireGlobalBase<
            PreservedRoomDraftCloseDialog,
            PreservedRoomDraftCloseChoice>();
        Require(
            PreservedRoomDraftCloseDialog.DialogAutomationId ==
            "PreservedRoomDraftClose.Dialog" &&
            PreservedRoomDraftCloseDialog.DialogAutomationName ==
            "关闭房间留存" &&
            PreservedRoomDraftCloseDialog.MessageAutomationId ==
            "Dialog.Message",
            "Retained-room close dialog UIA identifiers differ.");
        var message = PreservedRoomDraftCloseDialog.CreateMessage("Resound");
        Require(
            message.StartsWith(
                "“Resound”是房间关闭或被移出时保留的本地成果。",
                StringComparison.Ordinal) &&
            message.Contains(
                "即使状态显示“已保存”，关闭标签也会删除这份唯一留存。",
                StringComparison.Ordinal) &&
            message.EndsWith(
                "请先另存到正式谱面目录，或明确选择放弃。",
                StringComparison.Ordinal),
            "Retained-room warning no longer matches WPF copy.");

        var buttons = PreservedRoomDraftCloseDialog.CreateButtons();
        Require(
            buttons.Count == 3 &&
            buttons[0].Result == PreservedRoomDraftCloseChoice.Cancel &&
            buttons[0].IsCancel &&
            buttons[1].Result == PreservedRoomDraftCloseChoice.Discard &&
            buttons[1].Label == "放弃并关闭" &&
            buttons[2].Result == PreservedRoomDraftCloseChoice.SaveAs &&
            buttons[2].Label == "另存为后关闭" &&
            buttons[2].IsDefault,
            "Retained-room close choices differ from WPF save-as/discard/cancel.");
        Require(
            ChartWorkspaceSafetyPolicy.GetClosePrompt(
                isDirty: false,
                isPreservedRoomDraft: true) ==
            ChartTabClosePrompt.PreservedRoomDraft &&
            ChartWorkspaceSafetyPolicy.GetClosePrompt(
                isDirty: true,
                isPreservedRoomDraft: true) ==
            ChartTabClosePrompt.PreservedRoomDraft &&
            ChartWorkspaceSafetyPolicy.GetClosePrompt(
                isDirty: true,
                isPreservedRoomDraft: false) ==
            ChartTabClosePrompt.UnsavedChanges &&
            ChartWorkspaceSafetyPolicy.GetClosePrompt(
                isDirty: false,
                isPreservedRoomDraft: false) ==
            ChartTabClosePrompt.None,
            "Shared close-prompt policy no longer protects clean retained drafts.");
        return "retained clean+dirty=save-as/discard/cancel warning; " +
               "ordinary dirty=unsaved; ordinary clean=none";
    }

    private static string VerifyOnlineRoomExitContract()
    {
        RequireGlobalBase<
            OnlineRoomExitConfirmationDialog,
            OnlineRoomExitChoice>();
        Require(
            OnlineRoomExitConfirmationDialog.DialogAutomationId ==
            "OnlineRooms.ExitDialog" &&
            OnlineRoomExitConfirmationDialog.MessageAutomationId ==
            "OnlineRooms.ExitDialog.Message",
            "Online room-exit dialog lost its existing UIA identifiers.");
        Require(
            OnlineRoomExitConfirmationDialog.CreateTitle(isOwner: true) ==
            "关闭联机房间" &&
            OnlineRoomExitConfirmationDialog.CreateTitle(isOwner: false) ==
            "退出联机房间",
            "Owner/member room-exit titles differ from WPF.");
        Require(
            OnlineRoomExitConfirmationDialog.CreateMessage(
                "Retained Room",
                isOwner: true) ==
            "你是“Retained Room”的房主。关闭后其他成员会离开；当前谱面不会关闭，并将恢复为本地编辑。" &&
            OnlineRoomExitConfirmationDialog.CreateMessage(
                "Retained Room",
                isOwner: false) ==
            "退出“Retained Room”后，这张谱将恢复为本地编辑状态。",
            "Owner/member room-exit copy differs from WPF.");

        var ownerButtons =
            OnlineRoomExitConfirmationDialog.CreateButtons(isOwner: true);
        var memberButtons =
            OnlineRoomExitConfirmationDialog.CreateButtons(isOwner: false);
        Require(
            ownerButtons.Count == 2 &&
            ownerButtons[0].Result == OnlineRoomExitChoice.Cancel &&
            ownerButtons[0].Label == "取消" &&
            ownerButtons[0].IsDefault &&
            ownerButtons[0].IsCancel &&
            ownerButtons[1].Result == OnlineRoomExitChoice.Confirm &&
            ownerButtons[1].Label == "关闭房间" &&
            memberButtons.Count == 2 &&
            memberButtons[0].Result == OnlineRoomExitChoice.Cancel &&
            memberButtons[0].IsDefault &&
            memberButtons[0].IsCancel &&
            memberButtons[1].Result == OnlineRoomExitChoice.Confirm &&
            memberButtons[1].Label == "退出房间",
            "Room-exit choices do not keep cancel as the safe default.");
        Require(
            WindowsOnlineRoomRetentionPolicy.ShouldPreserveDraftOnConfirmedExit(
                WindowsOnlineRoomExitContext.ApplicationClose) &&
            !WindowsOnlineRoomRetentionPolicy.ShouldPreserveDraftOnConfirmedExit(
                WindowsOnlineRoomExitContext.ExplicitTabClose),
            "Confirmed exit no longer distinguishes restart retention from explicit deletion.");
        RequireConstructor<OnlineRoomExitConfirmationDialog>(
            typeof(string),
            typeof(bool),
            typeof(double),
            typeof(double));
        return "owner/member WPF copy exact; cancel default; application close retains; " +
               "explicit tab close deletes after confirmation";
    }

    private static string VerifyFeatureWrappers()
    {
        RequireGlobalBase<OsuImportModeDialog, OsuImportChoice>();
        RequireGlobalBase<AudioImportModeDialog, AudioImportMode>();
        RequireGlobalBase<SingleTempoResultDialog, bool>();
        RequireGlobalBase<VariableTempoResultDialog, VariableTempoDialogChoice>();
        RequireGlobalBase<NoteTypeTransformDialog, LongNoteToTapMode?>();
        RequireGlobalBase<ArchiveImportDialog, bool>();

        Require(
            OsuImportModeDialog.DialogAutomationId ==
            "OsuImportMode.Dialog" &&
            OsuImportModeDialog.MessageAutomationId ==
            "OsuImportMode.ChartName" &&
            AudioImportModeDialog.DialogAutomationId ==
            "AudioImport.ModeDialog" &&
            SingleTempoResultDialog.DialogAutomationId ==
            "Tempo.SingleResultDialog" &&
            SingleTempoResultDialog.MessageAutomationId ==
            "Tempo.SingleResult" &&
            VariableTempoResultDialog.DialogAutomationId ==
            "Tempo.VariableResultDialog" &&
            VariableTempoResultDialog.MessageAutomationId ==
            "Tempo.VariablePreview" &&
            NoteTypeTransformDialog.DialogAutomationId ==
            "NoteTransform.Dialog" &&
            ArchiveImportDialog.DialogAutomationId ==
            "ArchiveImport.Dialog",
            "A migrated feature dialog lost its existing GUI automation identity.");

        RequireConstructor<OsuImportModeDialog>(
            typeof(string),
            typeof(double),
            typeof(double));
        RequireConstructor<AudioImportModeDialog>(
            typeof(string),
            typeof(double),
            typeof(double));
        RequireConstructor<NoteTypeTransformDialog>(
            typeof(int),
            typeof(int),
            typeof(double),
            typeof(double));
        RequireConstructor<ArchiveImportDialog>(
            typeof(string),
            typeof(int),
            typeof(int),
            typeof(int),
            typeof(double),
            typeof(double));
        return "seven wrappers inherit the global chrome; stable UIA ids retained; " +
               "UI/font scales required by constructors";
    }

    private static string VerifyAdditionalWpfMessageWrappers()
    {
        RequireGlobalBase<LibraryDeleteConfirmationDialog, LibraryDeleteChoice>();
        RequireGlobalBase<ChartValidationDialog, bool>();
        RequireGlobalBase<SettingsActionDialog, bool>();

        Require(
            LibraryDeleteConfirmationDialog.DialogAutomationId ==
            "Library.DeleteDialog" &&
            LibraryDeleteConfirmationDialog.MessageAutomationId ==
            "Library.DeleteDialog.Message" &&
            ChartValidationDialog.DialogAutomationId ==
            "ChartValidation.Dialog" &&
            ChartValidationDialog.MessageAutomationId ==
            "ChartValidation.Dialog.Message" &&
            SettingsActionDialog.DialogAutomationId ==
            "Settings.ActionDialog" &&
            SettingsActionDialog.MessageAutomationId ==
            "Settings.ActionDialog.Message",
            "An additional WPF message wrapper lost its legacy UIA identity.");

        var recycle = LibraryDeleteConfirmationDialog.CreateButtons(
            LibraryDeleteDialogButtons.CancelRecycle);
        Require(
            recycle.Count == 2 &&
            recycle[0].Result == LibraryDeleteChoice.Cancel &&
            recycle[0].Label == "取消" &&
            recycle[0].IsCancel &&
            recycle[1].Result == LibraryDeleteChoice.Recycle &&
            recycle[1].Label == "移入回收站" &&
            !recycle.Any(button => button.IsDefault),
            "Library recycle prompt differs from WPF custom-choice order/defaults.");

        var validation = ChartValidationDialog.CreateButtons();
        Require(
            validation.Count == 1 &&
            validation[0].Result &&
            validation[0].Label == "确定" &&
            validation[0].IsDefault,
            "Chart validation prompt differs from WPF OK behavior.");

        var settings = SettingsActionDialog.CreateButtons(
            "继续清理",
            showCancel: true);
        Require(
            settings.Count == 2 &&
            !settings[0].Result &&
            settings[0].Label == "取消" &&
            settings[0].IsDefault &&
            settings[0].IsCancel &&
            settings[1].Result &&
            settings[1].Label == "继续清理" &&
            !settings[1].IsDefault,
            "Settings confirmation does not keep WPF No/cancel as safe default.");

        RequireConstructor<LibraryDeleteConfirmationDialog>(
            typeof(string),
            typeof(double),
            typeof(double),
            typeof(string),
            typeof(LibraryDeleteDialogTone),
            typeof(LibraryDeleteDialogButtons));
        RequireConstructor<ChartValidationDialog>(
            typeof(string),
            typeof(ChartValidationDialogTone),
            typeof(double),
            typeof(double));
        RequireConstructor<SettingsActionDialog>(
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(bool),
            typeof(bool),
            typeof(double),
            typeof(double));
        return "library delete/chart validation/settings all inherit shared WPF chrome; " +
               "legacy UIA retained; safe defaults and scale-aware constructors exact";
    }

    private static string VerifyChartExportOptionsContract()
    {
        Require(
            WindowsChartExportDialogParityPolicy.ContractVersion ==
            "wpf-1.1.0-chart-export-dialog-r2",
            "Chart-export dialog contract version differs.");
        Require(
            WindowsChartExportDialogParityPolicy.WindowWidth == 540 &&
            WindowsChartExportDialogParityPolicy.WindowHeight == 700 &&
            WindowsChartExportDialogParityPolicy.HeaderHeight == 44 &&
            WindowsChartExportDialogParityPolicy.HeaderCloseWidth == 40,
            "Chart-export window/header differs from WPF 540x700/44/40 geometry.");
        Require(
            WindowsChartExportDialogParityPolicy.ContentLeftMargin == 24 &&
            WindowsChartExportDialogParityPolicy.ContentTopMargin == 18 &&
            WindowsChartExportDialogParityPolicy.ContentRightMargin == 24 &&
            WindowsChartExportDialogParityPolicy.ContentBottomMargin == 20,
            "Chart-export content margins differ from WPF 24/18/24/20.");
        Require(
            WindowsChartExportDialogParityPolicy.ComboBoxHeight == 16 &&
            WindowsChartExportDialogParityPolicy.NumberBoxHeight == 32,
            "Chart-export field heights differ from measured WPF 16/32 DIP.");
        Require(
            WindowsChartExportDialogParityPolicy.ActionTopMargin == 18 &&
            WindowsChartExportDialogParityPolicy.ActionGap == 6 &&
            WindowsChartExportDialogParityPolicy.ActionHeight == 36 &&
            WindowsChartExportDialogParityPolicy.CancelWidth == 62 &&
            WindowsChartExportDialogParityPolicy.ConfirmWidth == 122,
            "Chart-export actions differ from measured WPF 18/6/36/62/122 geometry.");
        Require(
            WindowsChartExportDialogParityPolicy.InitialPreservePitchEnabled,
            "Initial FullPackage preserve-pitch state no longer matches WPF initialization.");
        RequireConstructor<ChartExportDialog>(typeof(double), typeof(double));
        return "window=540x700, header=44/close40, content=24/18/24/20, " +
               "fields=16/32, actions=18/6/36/62/122, initial preserve-pitch enabled";
    }

    private static string VerifyChartExportResultContract()
    {
        Require(
            WindowsNativeMessageBox.ContractVersion ==
            "wpf-1.1.0-native-message-box-r2",
            "Native message-box contract version differs.");
        var success =
            WindowsChartExportDialogParityPolicy.CreateSuccessResult(
                @"D:\Exports\Resound.mcz");
        Require(
            success.Caption == "导出" &&
            success.Message == "导出完成\n\nD:\\Exports\\Resound.mcz" &&
            success.Icon == WindowsNativeMessageBoxIcon.Information,
            "Export success message/caption/information icon differs from WPF.");
        var failure =
            WindowsChartExportDialogParityPolicy.CreateFailureResult(
                "导出失败",
                "磁盘空间不足");
        Require(
            failure.Caption == "LumaChart Studio" &&
            failure.Message == "导出失败\n\n磁盘空间不足" &&
            failure.Icon == WindowsNativeMessageBoxIcon.Error,
            "Export failure message/caption/error icon differs from WPF ShowError.");
        Require(
            WindowsNativeMessageBox.ResolveFlags(success.Icon) == 0x40 &&
            WindowsNativeMessageBox.ResolveFlags(failure.Icon) == 0x10,
            "Native MessageBoxW OK/information/error flags differ from WPF.");
        return "success=导出/information; failure=LumaChart Studio/error; " +
               "native MessageBoxW OK flags exact";
    }

    private static string VerifyChartExportDestinationContract()
    {
        var root = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "Unicode 空格 export destination"));
        var wrongExtension = Path.Combine(root, "测试谱面.osu.mc");
        var correctedPath = Path.Combine(root, "测试谱面.osu");
        var resolution =
            WindowsChartExportDialogParityPolicy.ResolveDestinationPath(
                wrongExtension,
                ".osu",
                candidate => string.Equals(
                    candidate,
                    correctedPath,
                    StringComparison.OrdinalIgnoreCase));
        Require(
            resolution.SelectedPath == wrongExtension &&
            resolution.DestinationPath == correctedPath &&
            resolution.ExtensionCorrected &&
            resolution.RequiresOverwriteConfirmation &&
            !resolution.DestinationPath.EndsWith(
                ".osu.mc",
                StringComparison.OrdinalIgnoreCase),
            "Wrong export extension was appended or existing corrected target was not protected.");

        var matching =
            WindowsChartExportDialogParityPolicy.ResolveDestinationPath(
                Path.Combine(root, "already-correct.OSU"),
                "osu",
                _ => true);
        Require(
            !matching.ExtensionCorrected &&
            !matching.RequiresOverwriteConfirmation &&
            matching.DestinationPath.EndsWith(
                ".OSU",
                StringComparison.Ordinal),
            "Matching destination extension was rewritten or prompted twice.");

        var overwrite =
            WindowsChartExportDialogParityPolicy.CreateOverwriteConfirmation(
                resolution.DestinationPath);
        Require(
            overwrite.Caption == "确认另存为" &&
            overwrite.Message.Contains(
                "测试谱面.osu",
                StringComparison.Ordinal) &&
            overwrite.Buttons == WindowsNativeMessageBoxButtons.YesNo &&
            overwrite.Icon == WindowsNativeMessageBoxIcon.Warning &&
            WindowsNativeMessageBox.ResolveFlags(
                overwrite.Icon,
                overwrite.Buttons) == 0x34,
            "Corrected existing destination copy no longer matches the WPF Yes/No warning.");
        Require(
            ChartExportOverwriteDialog.CreateMessage(
                resolution.DestinationPath) == overwrite.Message,
            "Owner-bound overwrite dialog message differs from the WPF native copy.");
        RequireConstructor<ChartExportOverwriteDialog>(
            typeof(string),
            typeof(double),
            typeof(double));

        return "picker-appended .osu.mc→.osu; corrected existing target requires owner-bound " +
               "WPF-copy Yes/No warning above the editor; matching .OSU remains unchanged";
    }

    private static string VerifyWorkspaceRecoveryNoticeContract()
    {
        Require(
            WindowsWorkspaceRecoveryNoticePolicy.Create([]) == null,
            "An empty recovery warning set created a modal notice.");
        var warnings = Enumerable
            .Range(1, 10)
            .Select(index =>
                index == 1
                    ? "Manifest\r\nentry was ignored"
                    : $"warning {index}")
            .ToArray();
        var request = WindowsWorkspaceRecoveryNoticePolicy.Create(warnings)
                      ?? throw new InvalidOperationException(
                          "Recovery warnings did not create a notice.");
        Require(
            request.Caption == "工作区恢复提示" &&
            request.Icon == WindowsNativeMessageBoxIcon.Warning &&
            request.Buttons == WindowsNativeMessageBoxButtons.Ok &&
            request.Message.Contains(
                "1. Manifest entry was ignored",
                StringComparison.Ordinal) &&
            request.Message.Contains(
                "8. warning 8",
                StringComparison.Ordinal) &&
            !request.Message.Contains(
                "9. warning 9",
                StringComparison.Ordinal) &&
            request.Message.Contains(
                "另有 2 条提示",
                StringComparison.Ordinal) &&
            request.Message.Contains(
                "不会用这些缓存静默覆盖可读取的源谱面",
                StringComparison.Ordinal) &&
            request.Message.Contains(
                "“另存为”",
                StringComparison.Ordinal),
            "Recovery notice lost its bounded warning list or fail-safe guidance.");
        return "empty=None; visible=8/10; multilineNormalized=True; sourceOverwriteWarning=True";
    }

    private static void RequireGlobalBase<TDialog, TResult>()
    {
        Require(
            typeof(TDialog).BaseType ==
            typeof(WindowsWpf110ChoiceDialog<TResult>),
            $"{typeof(TDialog).Name} does not inherit the global WPF dialog.");
    }

    private static void RequireConstructor<TDialog>(params Type[] parameterTypes)
    {
        var constructor = typeof(TDialog).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null);
        Require(
            constructor != null,
            $"{typeof(TDialog).Name} does not require the expected scale-aware inputs.");
    }

    private static void Check(
        ICollection<WindowsDialogParitySpecResult> results,
        string name,
        Func<string> action)
    {
        try
        {
            results.Add(new WindowsDialogParitySpecResult(name, true, action()));
        }
        catch (Exception exception)
        {
            results.Add(
                new WindowsDialogParitySpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsDialogParitySpecResult(
    string Name,
    bool Passed,
    string Detail);

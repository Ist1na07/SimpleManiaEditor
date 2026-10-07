using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsRoomPermissionNoticeSpecs
{
    public static WindowsRoomPermissionNoticeSpecResult[] Run()
    {
        var note = WindowsRoomPermissionNoticePolicy.Denied("Note");
        var mixed = WindowsRoomPermissionNoticePolicy.Denied("Note / Effect");
        var editor = WindowsRoomPermissionNoticePolicy.Capture(
            "EDITOR ",
            canEditMetadata: true,
            canEditTiming: true,
            canEditNotes: true);
        var editorNotesDenied = WindowsRoomPermissionNoticePolicy.Capture(
            "editor",
            canEditMetadata: true,
            canEditTiming: true,
            canEditNotes: false);
        var viewer = WindowsRoomPermissionNoticePolicy.Capture(
            "viewer",
            canEditMetadata: false,
            canEditTiming: false,
            canEditNotes: false);
        var unchanged = WindowsRoomPermissionNoticePolicy.Updated(editor, editor);
        var permissionChanged =
            WindowsRoomPermissionNoticePolicy.Updated(editor, editorNotesDenied);
        var changedToViewer =
            WindowsRoomPermissionNoticePolicy.Updated(editorNotesDenied, viewer);
        var changedToEditor =
            WindowsRoomPermissionNoticePolicy.Updated(viewer, editor);
        var viewerEditorAccess = WindowsOnlineRoomPermissionPolicy.Resolve(
            isOwner: false,
            memberRole: "viewer",
            canEditMetadata: true,
            canEditTiming: true,
            canEditNotes: true);
        var restoredEditorAccess = WindowsOnlineRoomPermissionPolicy.Resolve(
            isOwner: false,
            memberRole: "editor",
            canEditMetadata: true,
            canEditTiming: true,
            canEditNotes: true);
        var previewViewer =
            WindowsRoomPermissionNoticePolicy.PreviewTimeDenied(readOnly: true);
        var previewEditor =
            WindowsRoomPermissionNoticePolicy.PreviewTimeDenied(readOnly: false);
        var metadataViewer =
            WindowsRoomPermissionNoticePolicy.MetadataApplyDenied("观察者只读");
        var metadata =
            WindowsRoomPermissionNoticePolicy.MetadataApplyDenied("谱面信息");
        var offset =
            WindowsRoomPermissionNoticePolicy.MetadataApplyDenied("Effect");
        var keyCount =
            WindowsRoomPermissionNoticePolicy.MetadataApplyDenied("Note");
        var ownerLock =
            WindowsRoomPermissionNoticePolicy.MetadataApplyDenied("房主锁定区域");
        var tempoStart =
            WindowsRoomPermissionNoticePolicy.TempoAnalysisStartDenied();
        var tempoSingle =
            WindowsRoomPermissionNoticePolicy.TempoAnalysisApplyDenied(
                "Effect",
                variableTempo: false);
        var tempoVariable =
            WindowsRoomPermissionNoticePolicy.TempoAnalysisApplyDenied(
                "观察者只读",
                variableTempo: true);
        var tempoOwnerLock =
            WindowsRoomPermissionNoticePolicy.TempoAnalysisApplyDenied(
                "房主锁定区域",
                variableTempo: false);
        return
        [
            Check(
                WindowsRoomPermissionNoticePolicy.Contract ==
                "wpf-1.1.0-room-permission-notice-r1" &&
                WindowsRoomPermissionNoticePolicy.CardWidth == 470 &&
                WindowsRoomPermissionNoticePolicy.CardPadding == 24 &&
                WindowsRoomPermissionNoticePolicy.CardCornerRadius == 7 &&
                WindowsRoomPermissionNoticePolicy.TitleFontSize == 21 &&
                WindowsRoomPermissionNoticePolicy.MessageTopMargin == 13 &&
                WindowsRoomPermissionNoticePolicy.MessageBottomMargin == 18 &&
                WindowsRoomPermissionNoticePolicy.MessageLineHeight == 22 &&
                WindowsRoomPermissionNoticePolicy.DismissHeight == 38 &&
                WindowsRoomPermissionNoticePolicy.DismissHorizontalPadding == 28 &&
                WindowsRoomPermissionNoticePolicy.DismissVerticalPadding == 7 &&
                WindowsRoomPermissionNoticePolicy.OverlayColor == "#A8111416" &&
                WindowsRoomPermissionNoticePolicy.CardColor == "#1A2023" &&
                WindowsRoomPermissionNoticePolicy.CardBorderColor == "#557075" &&
                WindowsRoomPermissionNoticePolicy.TitleColor == "#E3E9EA" &&
                WindowsRoomPermissionNoticePolicy.MessageColor == "#B4C0C2",
                "room-permission-notice-geometry-and-colors-match-wpf-110",
                $"contract={WindowsRoomPermissionNoticePolicy.Contract}; " +
                $"card={WindowsRoomPermissionNoticePolicy.CardWidth}x" +
                $"{WindowsRoomPermissionNoticePolicy.CardPadding}; " +
                $"overlay={WindowsRoomPermissionNoticePolicy.OverlayColor}"),
            Check(
                note.Title == "没有编辑权限" &&
                note.Message == "无编辑 Note 的权限，请向房主索要。" &&
                mixed.Title == "没有编辑权限" &&
                mixed.Message == "无编辑 Note / Effect 的权限，请向房主索要。",
                "room-permission-notice-copy-matches-wpf-110",
                $"note={note.Title}|{note.Message}; mixed={mixed.Message}"),
            Check(
                WindowsRoomPermissionNoticePolicy.AccessChangeContract ==
                "wpf-1.1.0-room-access-change-notice-r1" &&
                editor.MemberRole == "editor" &&
                unchanged == null,
                "room-access-change-normalizes-role-and-suppresses-unchanged-state",
                $"contract={WindowsRoomPermissionNoticePolicy.AccessChangeContract}; " +
                $"role={editor.MemberRole}; unchanged={unchanged == null}"),
            Check(
                permissionChanged?.Title == "房间权限已更新" &&
                permissionChanged.Message ==
                "谱面信息：允许编辑\nEffect / Timing Edit：允许编辑\nNote：不可编辑",
                "room-access-permission-update-copy-matches-wpf-110",
                $"{permissionChanged?.Title}|{permissionChanged?.Message}"),
            Check(
                changedToViewer?.Title == "房间身份已修改" &&
                changedToViewer.Message ==
                "你的身份已被修改为观察者。\n\n观察者只能浏览、播放与选择，不能修改谱面。" &&
                changedToEditor?.Title == "房间身份已修改" &&
                changedToEditor.Message ==
                "你的身份已被修改为编辑者。\n\n谱面信息：允许编辑\n" +
                "Effect / Timing Edit：允许编辑\nNote：允许编辑",
                "room-access-role-change-copy-matches-wpf-110",
                $"viewer={changedToViewer?.Message}; editor={changedToEditor?.Message}"),
            Check(
                viewerEditorAccess.IsReadOnly &&
                !viewerEditorAccess.CanEditMetadata &&
                !viewerEditorAccess.CanEditTiming &&
                !viewerEditorAccess.CanEditNotes &&
                !restoredEditorAccess.IsReadOnly &&
                restoredEditorAccess.CanEditMetadata &&
                restoredEditorAccess.CanEditTiming &&
                restoredEditorAccess.CanEditNotes,
                "room-access-viewer-to-editor-restores-all-granular-permissions",
                $"viewer={viewerEditorAccess}; restored={restoredEditorAccess}"),
            Check(
                WindowsRoomPermissionNoticePolicy.EditorActionContract ==
                "wpf-1.1.0-editor-permission-action-notice-r1" &&
                previewViewer.Title == "观察者只读" &&
                previewViewer.Message == "观察者不能修改谱面预览时间。" &&
                previewEditor.Title == "没有谱面信息权限" &&
                previewEditor.Message ==
                "设置预览时间需要谱面信息权限，请向房主索要。",
                "room-preview-time-permission-copy-matches-wpf-110",
                $"contract={WindowsRoomPermissionNoticePolicy.EditorActionContract}; " +
                $"viewer={previewViewer.Title}|{previewViewer.Message}; " +
                $"editor={previewEditor.Title}|{previewEditor.Message}"),
            Check(
                metadataViewer?.Title == "观察者只读" &&
                metadataViewer.Message == "观察者不能修改谱面信息、轨道数或 Offset。" &&
                metadata?.Title == "没有谱面信息权限" &&
                metadata.Message ==
                "当前输入包含 Metadata 或 KeyCount 修改，请向房主索要谱面信息权限。" &&
                offset?.Title == "没有 Effect 权限" &&
                offset.Message ==
                "Offset 属于 Timing；请恢复原值或向房主索要 Effect 权限。" &&
                keyCount?.Title == "没有 Note 权限" &&
                keyCount.Message ==
                "降低 KeyCount 会删除右侧 Note；请向房主索要 Note 权限。" &&
                ownerLock == null,
                "room-metadata-apply-permission-copy-matches-wpf-110",
                $"viewer={metadataViewer?.Message}; metadata={metadata?.Message}; " +
                $"offset={offset?.Message}; note={keyCount?.Message}; " +
                $"ownerLockSuppressed={ownerLock == null}"),
            Check(
                tempoStart.Title == "没有 Effect 权限" &&
                tempoStart.Message ==
                "BPM 与 Offset 属于 Timing；请向房主索要 Effect 权限。" &&
                tempoSingle?.Title == "没有 Effect 权限" &&
                tempoSingle.Message ==
                "分析期间 Timing 权限已失效，检测结果未写入。" &&
                tempoVariable?.Title == "没有 Effect 权限" &&
                tempoVariable.Message ==
                "分析期间 Timing 权限已失效，预测结果未写入。" &&
                tempoOwnerLock == null,
                "room-tempo-analysis-permission-copy-matches-wpf-110",
                $"start={tempoStart.Message}; single={tempoSingle?.Message}; " +
                $"variable={tempoVariable?.Message}; " +
                $"ownerLockSuppressed={tempoOwnerLock == null}")
        ];
    }

    private static WindowsRoomPermissionNoticeSpecResult Check(
        bool passed,
        string name,
        string detail) =>
        new(name, passed, detail);
}

internal sealed record WindowsRoomPermissionNoticeSpecResult(
    string Name,
    bool Passed,
    string Detail);

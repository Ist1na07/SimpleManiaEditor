using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Input;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;
using LumaChart.Online;
using System.Security.Cryptography;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsInspectorParitySpecs
{
    public static IReadOnlyList<WindowsInspectorParitySpecResult> Run()
    {
        var results = new List<WindowsInspectorParitySpecResult>();
        Check(results, "inspector-wpf-tab-order-is-exact-and-does-not-wrap-with-audio-tab",
            VerifyTabOrder);
        Check(results, "metadata-scroll-footer-keeps-apply-and-status-physically-reachable",
            VerifyMetadataScrollFooter);
        Check(results, "metadata-inspector-focus-order-matches-wpf-110",
            VerifyMetadataFocusOrder);
        Check(results, "metadata-key-count-display-labels-match-wpf-110",
            VerifyMetadataKeyCountLabels);
        Check(results, "metadata-visible-copy-and-title-typography-match-wpf-110",
            VerifyMetadataVisibleCopy);
        Check(results, "timing-inspector-wpf-layout-metrics-and-action-order-are-exact",
            VerifyTimingLayout);
        Check(results, "timing-effect-display-labels-preserve-wpf-text-and-internal-values",
            VerifyTimingEffectLabels);
        Check(results, "timing-inspector-focus-order-matches-wpf-110",
            VerifyTimingFocusOrder);
        Check(results, "timing-list-shortcuts-route-delete-copy-and-paste",
            VerifyTimingListShortcuts);
        Check(results, "timing-list-shortcuts-recover-physical-keys-under-chinese-ime",
            VerifyImeShortcutRecovery);
        Check(results, "timing-editor-preserves-unapplied-draft-during-same-item-refresh",
            VerifyTimingDraftRefreshPolicy);
        Check(results, "note-inspector-wpf-layout-actions-and-key-sound-entry-are-exact",
            VerifyNoteLayout);
        Check(results, "group-inspector-and-floating-palettes-stay-in-wpf-layout-boundaries",
            VerifyGroupAndPaletteLayout);
        Check(results, "factory-floating-palettes-anchor-quick-top-right-and-adjust-at-inspector-middle",
            VerifyFactoryFloatingPalettePlacement);
        Check(results, "quick-edit-palette-resize-keeps-one-aspect-ratio",
            VerifyQuickEditPaletteResizeAspectRatio);
        Check(results, "group-inspector-static-and-dynamic-focus-order-matches-wpf-110",
            VerifyGroupFocusOrder);
        Check(results, "settings-static-and-runtime-shortcut-focus-order-matches-wpf-110",
            VerifySettingsFocusOrder);
        Check(results, "editor-auto-backup-toast-matches-wpf-110-transient-contract",
            VerifyEditorToast);
        Check(results, "global-follow-toast-matches-wpf-110-queue-and-visual-contract",
            VerifyFollowNotificationToast);
        Check(results, "online-frozen-note-sound-verifies-length-and-sha-before-assignment",
            VerifyFrozenNoteSoundRegistry);
        Check(results, "online-frozen-note-sound-rejects-manifest-path-escape",
            VerifyFrozenNoteSoundPathBoundary);
        Check(results, "timeline-uia-exposes-live-current-beat-scale-and-exact-selected-note",
            VerifyTimelineLiveAutomationState);
        Check(results, "timeline-background-hover-does-not-open-empty-native-tooltip",
            VerifyTimelineBackgroundHoverToolTip);
        return results;
    }

    private static string VerifyQuickEditPaletteResizeAspectRatio()
    {
        const double startWidth = 224;
        const double startHeight = 360;
        var horizontal = WindowsQuickEditPaletteResizePolicy.Resolve(
            startWidth,
            startHeight,
            horizontalDelta: 112,
            verticalDelta: 12,
            workbenchWidth: 1_600,
            workbenchHeight: 900);
        var vertical = WindowsQuickEditPaletteResizePolicy.Resolve(
            startWidth,
            startHeight,
            horizontalDelta: 10,
            verticalDelta: -90,
            workbenchWidth: 1_600,
            workbenchHeight: 900);
        var constrained = WindowsQuickEditPaletteResizePolicy.Resolve(
            startWidth,
            startHeight,
            horizontalDelta: 2_000,
            verticalDelta: 2_000,
            workbenchWidth: 420,
            workbenchHeight: 520);
        var aspectRatio = startWidth / startHeight;
        var expectedMinimumScale =
            WindowsQuickEditPaletteResizePolicy.MinimumWidth / startWidth;

        Require(
            Math.Abs(horizontal.Width / horizontal.Height - aspectRatio) < 1e-12 &&
            Math.Abs(horizontal.Scale - 1.5) < 1e-12 &&
            Math.Abs(vertical.Width / vertical.Height - aspectRatio) < 1e-12 &&
            Math.Abs(vertical.Scale - expectedMinimumScale) < 1e-12 &&
            Math.Abs(constrained.Width / constrained.Height - aspectRatio) < 1e-12 &&
            constrained.Width <= 412 && constrained.Height <= 512,
            "Quick Edit resize changed the palette aspect ratio or escaped its workbench bounds.");
        return $"ratio={aspectRatio:0.000}; horizontal={horizontal.Width:0}x" +
               $"{horizontal.Height:0}; vertical={vertical.Width:0}x{vertical.Height:0}; " +
               $"bounded={constrained.Width:0}x{constrained.Height:0}";
    }

    private static string VerifyTimelineBackgroundHoverToolTip()
    {
        var surface = new TimelineSurface();
        Require(
            global::Avalonia.Controls.ToolTip.GetTip(surface) == null,
            "Timeline background still attaches an empty native ToolTip popup.");
        return "nativeTip=null; creator tooltip is retained inside TimelineSurface rendering";
    }

    private static string VerifyEditorToast()
    {
        Require(
            WindowsTransientToastParityPolicy.EditorRightMargin == 18 &&
            WindowsTransientToastParityPolicy.EditorBottomMargin == 18 &&
            WindowsTransientToastParityPolicy.EditorHorizontalPadding == 13 &&
            WindowsTransientToastParityPolicy.EditorVerticalPadding == 9 &&
            WindowsTransientToastParityPolicy.EditorCornerRadius == 5,
            "Editor toast geometry differs from WPF 1.1.0.");
        Require(
            WindowsTransientToastParityPolicy.EditorDotSize == 7 &&
            WindowsTransientToastParityPolicy.EditorDotRightMargin == 9 &&
            WindowsTransientToastParityPolicy.EditorFontSize == 11 &&
            WindowsTransientToastParityPolicy.EditorDurationMilliseconds == 3000,
            "Editor toast visual timing differs from WPF 1.1.0.");
        Require(
            WindowsTransientToastParityPolicy.FormatAutomaticBackupSuccess(1) ==
                "自动备份成功" &&
            WindowsTransientToastParityPolicy.FormatAutomaticBackupSuccess(3) ==
                "自动备份成功 · 3 张谱面",
            "Editor automatic-backup toast text differs from WPF 1.1.0.");
        return "right/bottom=18; padding=13/9; duration=3000ms";
    }

    private static string VerifyFollowNotificationToast()
    {
        Require(
            WindowsTransientToastParityPolicy.OnlineToastWidth == 360 &&
            WindowsTransientToastParityPolicy.OnlineToastTopMargin == 50 &&
            WindowsTransientToastParityPolicy.OnlineToastRightMargin == 14 &&
            WindowsTransientToastParityPolicy.InvitationToastBottomMargin == 10,
            "Follow notification stack geometry differs from WPF 1.1.0.");
        Require(
            WindowsTransientToastParityPolicy.FollowHorizontalPadding == 14 &&
            WindowsTransientToastParityPolicy.FollowVerticalPadding == 11 &&
            WindowsTransientToastParityPolicy.FollowCornerRadius == 6 &&
            WindowsTransientToastParityPolicy.FollowDotSize == 8 &&
            WindowsTransientToastParityPolicy.FollowDotRightMargin == 10 &&
            WindowsTransientToastParityPolicy.FollowFontSize == 12,
            "Follow notification toast visuals differ from WPF 1.1.0.");
        Require(
            WindowsTransientToastParityPolicy.FollowPollIntervalMilliseconds == 5000 &&
            WindowsTransientToastParityPolicy.FollowDurationMilliseconds == 4000,
            "Follow notification polling or queue duration differs from WPF 1.1.0.");
        Require(
            WindowsTransientToastParityPolicy.FormatFollowNotification("  Alice  ") ==
                "Alice关注了你。" &&
            WindowsTransientToastParityPolicy.FormatFollowNotification(" ") ==
                "匿名用户关注了你。",
            "Follow notification copy or actor normalization differs from WPF 1.1.0.");
        return "360@top50/right14; padding=14/11; poll=5000ms; toast=4000ms";
    }

    private static string VerifyTabOrder()
    {
        var expected = new[] { "谱面", "Timing Edit", "Note", "分组", "设置" };
        Require(WindowsInspectorParityPolicy.Wpf110TabHeaders.SequenceEqual(expected),
            "Inspector tab order differs from WPF 1.1.0.");
        Require(!WindowsInspectorParityPolicy.Wpf110TabHeaders.Contains(
                "音频",
                StringComparer.Ordinal),
            "The Avalonia-only Audio tab still changes WPF tab wrapping and order.");
        return string.Join(" > ", WindowsInspectorParityPolicy.Wpf110TabHeaders);
    }

    private static string VerifyMetadataScrollFooter()
    {
        Require(
            WindowsInspectorParityPolicy.MetadataBottomScrollClearance >= 64,
            "Metadata footer clearance no longer covers the scaled Apply/Status overflow.");
        return $"bottomClearance={WindowsInspectorParityPolicy.MetadataBottomScrollClearance} DIP";
    }

    private static string VerifyMetadataFocusOrder()
    {
        string[] expected =
        [
            "Metadata.Title",
            "Metadata.RomanizeTitle",
            "Metadata.TitleOriginal",
            "Metadata.Artist",
            "Metadata.RomanizeArtist",
            "Metadata.ArtistOriginal",
            "Metadata.Creator",
            "Metadata.Version",
            "Metadata.Bpm",
            "Metadata.KeyCount",
            "Metadata.AudioFile",
            "Metadata.AudioSelect",
            "Metadata.AnalyzeSingleTempo",
            "Metadata.AnalyzeVariableTempo",
            "Metadata.Offset",
            "Metadata.PreviewTime",
            "Metadata.PreviewHere",
            "Metadata.CoverFile",
            "Metadata.CoverSelect",
            "Metadata.BackgroundFile",
            "Metadata.BackgroundSelect",
            "Metadata.Apply"
        ];
        Require(
            WindowsInspectorParityPolicy.Wpf110MetadataFocusOrder
                .SequenceEqual(expected, StringComparer.Ordinal),
            "Metadata focus order differs from WPF 1.1.0.");
        Require(expected
                .Select(WindowsInspectorParityPolicy.ResolveMetadataTabIndex)
                .SequenceEqual(Enumerable.Range(0, expected.Length)),
            "Metadata focus indices are not unique and contiguous.");
        Require(
            !WindowsInspectorParityPolicy.Wpf110MetadataFocusOrder.Contains(
                "Metadata.OverallDifficulty",
                StringComparer.Ordinal) &&
            !WindowsInspectorParityPolicy.Wpf110MetadataFocusOrder.Contains(
                "Metadata.HpDrainRate",
                StringComparer.Ordinal),
            "Avalonia-only OD/HP inputs entered the WPF Metadata focus surface.");
        return $"count={expected.Length}; first={expected[0]}; last={expected[^1]}";
    }

    private static string VerifyMetadataKeyCountLabels()
    {
        var options = WindowsInspectorParityPolicy.Wpf110MetadataKeyCountOptions;
        int[] expectedValues = [4, 5, 6, 7, 8, 9, 10];
        string[] expectedLabels =
            ["4 Keys", "5 Keys", "6 Keys", "7 Keys", "8 Keys", "9 Keys", "10 Keys"];
        Require(
            WindowsInspectorParityPolicy.Wpf110MetadataKeyCountLabel == "轨道数" &&
            options.Select(option => option.Value).SequenceEqual(expectedValues) &&
            options.Select(option => option.Label)
                .SequenceEqual(expectedLabels, StringComparer.Ordinal),
            "Metadata KeyCount options differ from WPF 1.1.0.");
        Require(
            options.All(option => option.ToString() == option.Label),
            "Metadata KeyCount ComboBox exposes raw integer values.");
        return string.Join(
            " / ",
            options.Select(option => $"{option.Value}→{option.Label}"));
    }

    private static string VerifyMetadataVisibleCopy()
    {
        Require(
            WindowsInspectorParityPolicy.Wpf110MetadataTitle == "谱面信息" &&
            WindowsInspectorParityPolicy.Wpf110MetadataSubtitle ==
                "Malody Key / osu!mania 元数据" &&
            WindowsInspectorParityPolicy.Wpf110MetadataTitleAsciiLabel ==
                "歌曲名" &&
            WindowsInspectorParityPolicy.Wpf110MetadataArtistAsciiLabel ==
                "艺术家" &&
            WindowsInspectorParityPolicy.Wpf110MetadataKeyCountLabel == "轨道数" &&
            WindowsInspectorParityPolicy.Wpf110MetadataApplyLabel == "应用谱面信息",
            "Metadata visible copy differs from WPF 1.1.0.");
        Require(
            WindowsInspectorParityPolicy.MetadataTitleFontSize == 25 &&
            WindowsInspectorParityPolicy.MetadataStatValueFontSize == 20 &&
            WindowsInspectorParityPolicy.MetadataStatsTopMargin == 22 &&
            WindowsInspectorParityPolicy.Wpf110MetadataStatLabels
                .SequenceEqual(["Notes", "Holds", "Timing"], StringComparer.Ordinal),
            "Metadata title or stats typography differs from WPF 1.1.0.");
        Require(
            WindowsInspectorParityPolicy.MetadataPairGap == 10 &&
            WindowsInspectorParityPolicy.MetadataRomanizeActionColumnWidth == 84 &&
            WindowsInspectorParityPolicy.MetadataFileActionColumnWidth == 86 &&
            WindowsInspectorParityPolicy.MetadataPreviewActionColumnWidth == 76 &&
            WindowsInspectorParityPolicy.MetadataToolButtonMargin ==
                new Thickness(2, 0) &&
            WindowsInspectorParityPolicy.MetadataSingleTempoActionMargin ==
                new Thickness(3, 7, 3, 2) &&
            WindowsInspectorParityPolicy.MetadataVariableTempoActionMargin ==
                new Thickness(3, 3, 3, 2) &&
            WindowsInspectorParityPolicy.MetadataApplyActionMargin ==
                new Thickness(3, 18, 3, 0),
            "Metadata WPF column widths, gaps or action margins drifted.");
        var stats = WindowsInspectorParityPolicy.MeasureMetadataStats(
            new ChartDocument
            {
                Notes =
                [
                    new ChartNote(),
                    new ChartNote
                    {
                        EndBeat = new BeatPosition(0, 1, 4)
                    },
                    new ChartNote
                    {
                        EndBeat = new BeatPosition(0, 1, 4),
                        IsDraft = true
                    }
                ],
                Timing = [new TimingEvent(), new TimingEvent()]
            });
        Require(
            stats == new WindowsMetadataStats(2, 1, 2),
            $"Metadata stats projection is {stats}, expected 2/1/2.");
        return "谱面信息@25 / Malody Key / osu!mania 元数据 / " +
               "歌曲名 / 艺术家 / 轨道数 / 应用谱面信息 / " +
               "Notes-Holds-Timing@20";
    }

    private static string VerifyTimingLayout()
    {
        var margin = WindowsInspectorParityPolicy.TimingContentMargin;
        Require(margin == new Thickness(22, 14, 22, 20),
            $"Timing content margin is {margin}, expected 22,14,22,20.");
        Require(WindowsInspectorParityPolicy.TimingTitleFontSize == 25 &&
                WindowsInspectorParityPolicy.TimingListTopMargin == 14 &&
                WindowsInspectorParityPolicy.TimingActionsTopMargin == 12 &&
                WindowsInspectorParityPolicy.TimingClipboardActionsTopMargin == 6,
            "Timing layout metrics differ from WPF 1.1.0.");
        var expectedActions = new[] { "新增", "应用", "删除", "复制 Effect", "粘贴到红线" };
        Require(WindowsInspectorParityPolicy.Wpf110TimingActionLabels.SequenceEqual(expectedActions),
            "Timing action order differs from WPF 1.1.0.");
        return $"margin={margin}; title=25; listTop=14; actions={string.Join('/', expectedActions)}";
    }

    private static string VerifyTimingEffectLabels()
    {
        var options = WindowsInspectorParityPolicy.Wpf110TimingEffectOptions;
        string[] expectedValues =
            ["None", "Scroll", "Jump", "HS", "Sign", "Custom"];
        string[] expectedLabels =
            ["无", "Scroll", "Jump", "HS", "Sign", "自定义字段"];
        Require(
            options.Select(option => option.Value)
                .SequenceEqual(expectedValues, StringComparer.Ordinal) &&
            options.Select(option => option.Label)
                .SequenceEqual(expectedLabels, StringComparer.Ordinal),
            "Timing Effect options differ from the WPF 1.1.0 value/label contract.");
        Require(
            options[0].ToString() == "无" &&
            options[^1].ToString() == "自定义字段",
            "Timing Effect ComboBox display text exposes internal enum values.");
        return string.Join(
            " / ",
            options.Select(option => $"{option.Value}→{option.Label}"));
    }

    private static string VerifyTimingFocusOrder()
    {
        string[] expected =
        [
            "Inspector.Timing.Beat",
            "Inspector.Timing.Delay",
            "Inspector.Timing.Bpm",
            "Inspector.Timing.Effect",
            "Inspector.Timing.Value",
            "Inspector.Timing.CustomField",
            "Inspector.Timing.List",
            "Inspector.Timing.Add",
            "Inspector.Timing.Apply",
            "Inspector.Timing.Delete",
            "Inspector.Timing.CopyEffect",
            "Inspector.Timing.PasteAtRedLine"
        ];
        Require(
            WindowsInspectorParityPolicy.Wpf110TimingFocusOrder
                .SequenceEqual(expected, StringComparer.Ordinal),
            "Timing focus order differs from WPF 1.1.0.");
        Require(expected
                .Select(WindowsInspectorParityPolicy.ResolveTimingTabIndex)
                .SequenceEqual(Enumerable.Range(0, expected.Length)),
            "Timing focus indices are not unique and contiguous.");
        return $"count={expected.Length}; first={expected[0]}; last={expected[^1]}";
    }

    private static string VerifyTimingListShortcuts()
    {
        var delete = WindowsInspectorParityPolicy.ResolveTimingListShortcut(
            Key.Delete,
            PhysicalKey.Delete,
            null,
            KeyModifiers.Shift);
        var copy = WindowsInspectorParityPolicy.ResolveTimingListShortcut(
            Key.C,
            PhysicalKey.C,
            "c",
            KeyModifiers.Control);
        var paste = WindowsInspectorParityPolicy.ResolveTimingListShortcut(
            Key.V,
            PhysicalKey.V,
            "v",
            KeyModifiers.Control);
        var textC = WindowsInspectorParityPolicy.ResolveTimingListShortcut(
            Key.C,
            PhysicalKey.C,
            "c",
            KeyModifiers.None);
        Require(delete == WindowsTimingListShortcutCommand.Delete &&
                copy == WindowsTimingListShortcutCommand.CopyEffect &&
                paste == WindowsTimingListShortcutCommand.PasteEffect &&
                textC == WindowsTimingListShortcutCommand.None,
            $"Unexpected routes: delete={delete}; copy={copy}; paste={paste}; plainC={textC}.");
        return $"Delete={delete}; Ctrl+C={copy}; Ctrl+V={paste}";
    }

    private static string VerifyImeShortcutRecovery()
    {
        var copy = WindowsInspectorParityPolicy.ResolveTimingListShortcut(
            Key.ImeProcessed,
            PhysicalKey.C,
            null,
            KeyModifiers.Control);
        var paste = WindowsInspectorParityPolicy.ResolveTimingListShortcut(
            Key.DeadCharProcessed,
            PhysicalKey.V,
            null,
            KeyModifiers.Control);
        var fallback = WindowsInspectorParityPolicy.ResolveTimingListShortcut(
            Key.System,
            PhysicalKey.None,
            null,
            KeyModifiers.Control,
            Key.C);
        Require(copy == WindowsTimingListShortcutCommand.CopyEffect &&
                paste == WindowsTimingListShortcutCommand.PasteEffect &&
                fallback == WindowsTimingListShortcutCommand.CopyEffect,
            $"IME recovery failed: physicalC={copy}; physicalV={paste}; Win32Fallback={fallback}.");
        return $"ImeProcessed/C={copy}; DeadChar/V={paste}; fallback/C={fallback}";
    }

    private static string VerifyTimingDraftRefreshPolicy()
    {
        var timingId = Guid.NewGuid();
        Require(!WindowsInspectorParityPolicy.ShouldPopulateTimingFields(
                false,
                true,
                timingId,
                timingId),
            "A background refresh overwrites the unapplied Timing draft for the same item.");
        Require(WindowsInspectorParityPolicy.ShouldPopulateTimingFields(
                false,
                true,
                timingId,
                Guid.NewGuid()),
            "Selecting a different Timing item does not populate its fields.");
        Require(WindowsInspectorParityPolicy.ShouldPopulateTimingFields(
                true,
                true,
                timingId,
                timingId),
            "A forced refresh cannot commit the applied Timing state.");
        return "same-item draft preserved; selection change and forced commit refresh";
    }

    private static string VerifyNoteLayout()
    {
        Require(WindowsInspectorParityPolicy.NoteContentPadding == new Thickness(22, 14, 22, 24),
            "Note Inspector padding differs from WPF 1.1.0.");
        Require(WindowsInspectorParityPolicy.NoteTitleFontSize == 25,
            "Note Inspector title size differs from WPF 1.1.0.");
        var expected = new[] { "选择已有 Key 音", "选择文件", "应用", "删除" };
        Require(WindowsInspectorParityPolicy.Wpf110NoteActionLabels.SequenceEqual(expected),
            "Note Inspector action entries differ from WPF 1.1.0.");
        return $"padding=22,14,22,24; title=25; actions={string.Join('/', expected)}";
    }

    private static string VerifyGroupAndPaletteLayout()
    {
        Require(WindowsInspectorParityPolicy.GroupContentPadding == new Thickness(18, 14, 18, 24),
            "Group Inspector padding differs from WPF 1.1.0.");
        Require(WindowsInspectorParityPolicy.GroupTitleFontSize == 24,
            "Group Inspector title size differs from WPF 1.1.0.");
        Require(WindowsInspectorParityPolicy.FloatingPaletteColumnSpan == 1,
            "Quick-edit or adjustment palette can overlap the Inspector by default.");
        Require(WindowsInspectorParityPolicy.FloatingPalettesAreSeparatedByDefault,
            "Quick-edit and adjustment palettes overlap each other at their default positions.");
        Require(WindowsInspectorParityPolicy.QuickValuesPaletteDefaultRightMargin == 262,
            "The adjustment palette default right margin no longer preserves the 8 DIP gap.");
        Require(WindowsInspectorParityPolicy.QuickValuesControlHeight == 26,
            "The adjustment palette controls no longer fit the 1080p workbench.");
        Require(WindowsInspectorParityPolicy.QuickValuesSectionGap == 3,
            "The adjustment palette section spacing no longer matches the compact WPF layout.");
        return "groupPadding=18,14,18,24; title=24; paletteSpan=timeline-only; " +
               "paletteGap=8; adjustControlHeight=26";
    }

    private static string VerifyFactoryFloatingPalettePlacement()
    {
        var placement = WindowsFloatingPaletteLayoutPolicy.Resolve(
            workbenchWidth: 1600,
            workbenchHeight: 1000,
            rightBoundary: 1244,
            quickWidth: 234,
            quickHeight: 300,
            valuesWidth: 112,
            valuesHeight: 330,
            preferredQuickTop: WindowsFloatingPaletteLayoutPolicy.DefaultQuickTop,
            paletteGap: WindowsInspectorParityPolicy.FloatingPaletteGap);
        Require(
            placement.QuickLeft == 1010 &&
            placement.QuickTop == 54 &&
            placement.ValuesLeft == 1132 &&
            placement.ValuesTop == 362,
            $"Unexpected factory palette placement: {placement}.");

        var compact = WindowsFloatingPaletteLayoutPolicy.Resolve(
            workbenchWidth: 700,
            workbenchHeight: 560,
            rightBoundary: 696,
            quickWidth: 234,
            quickHeight: 300,
            valuesWidth: 112,
            valuesHeight: 330,
            preferredQuickTop: 54,
            paletteGap: 8);
        Require(
            compact.QuickLeft >= 4 &&
            compact.ValuesLeft >= 4 &&
            compact.QuickTop >= 4 &&
            compact.ValuesTop + 330 <= 556,
            $"Compact factory palette placement escaped the workbench: {compact}.");
        var highDpi1080 = WindowsFloatingPaletteLayoutPolicy.Resolve(
            workbenchWidth: 1280,
            workbenchHeight: 655,
            rightBoundary: 916,
            quickWidth: 234,
            quickHeight: 300,
            valuesWidth: 112,
            valuesHeight: 309,
            preferredQuickTop: WindowsFloatingPaletteLayoutPolicy.DefaultQuickTop,
            paletteGap: WindowsInspectorParityPolicy.FloatingPaletteGap);
        Require(
            highDpi1080.QuickTop == 34 &&
            highDpi1080.ValuesTop == 342 &&
            highDpi1080.QuickTop + 300 + 8 <= highDpi1080.ValuesTop &&
            highDpi1080.ValuesTop + 309 <= 651,
            $"High-DPI 1080p palettes overlap or escape the workbench: {highDpi1080}.");
        return $"quick={placement.QuickLeft},{placement.QuickTop}; " +
               $"adjust={placement.ValuesLeft},{placement.ValuesTop}; right=1244";
    }

    private static string VerifyGroupFocusOrder()
    {
        string[] expected =
        [
            "Inspector.Group.CreateAndUse",
            "Inspector.Group.Id",
            "Inspector.Group.SetCurrent",
            "Inspector.Group.AssignSelection",
            "Inspector.Group.ShowAll",
            "Inspector.Group.HideNonzero",
            "Inspector.Group.ClearSelection"
        ];
        Require(
            WindowsInspectorParityPolicy.Wpf110GroupStaticFocusOrder
                .SequenceEqual(expected, StringComparer.Ordinal),
            "Group static focus order differs from WPF 1.1.0.");
        Require(expected
                .Select(WindowsInspectorParityPolicy.ResolveGroupStaticTabIndex)
                .SequenceEqual(Enumerable.Range(0, expected.Length)),
            "Group static focus indices are not unique and contiguous.");
        Require(
            WindowsInspectorParityPolicy.GroupDynamicFocusStartIndex ==
            expected.Length,
            "Dynamic Group row controls do not follow the static WPF controls.");
        return $"static={expected.Length}; dynamicStart={expected.Length}";
    }

    private static string VerifySettingsFocusOrder()
    {
        // The Avalonia-only language selector precedes the established editor
        // controls. The client intentionally omits KeySound.Preview (试听):
        // the preview feature was removed from both Windows and Android on
        // request, so the WPF 1.1.0 static order is mirrored minus that row.
        string[] expectedStatic =
        [
            "Settings.Language",
            "Settings.Snap",
            "Settings.FixedSnap",
            "Settings.AnchorLine",
            "Settings.AnchorLineAbove",
            "Settings.AnchorCell",
            "Settings.AnchorCellBottom",
            "Settings.GridLaneWidth",
            "Settings.MainEditorHorizontalOffset",
            "Settings.GridBeatHeight",
            "Settings.NoteWidth",
            "Settings.NoteHeight",
            "Settings.NoteOpacity",
            "Settings.JudgementLinePosition",
            "Settings.JudgementLineThickness",
            "Settings.JudgementLineOpacity",
            "Settings.ProEditorColumns",
            "Settings.UiScale",
            "Settings.FontScale",
            "FrameRateLimitBox",
            "Settings.ResetEditorView",
            "Settings.ResetFloatingLayout",
            "Settings.AutoBackupEnabled",
            "Settings.AutoBackupMinutes",
            "Settings.BackupNow",
            "Settings.RestoreBackup",
            "Settings.OpenBackupFolder",
            "Settings.Cache.Backups",
            "Settings.Cache.UnopenedEditing",
            "Settings.Cache.OnlineResources",
            "Settings.Cache.CleanSelected",
            "Settings.Shortcuts.Reset",
            "Audio.Volume",
            "KeySound.Enabled",
            "KeySound.Preset",
            "KeySound.CustomFile",
            "KeySound.Volume",
            "KeySound.LongNoteEnd",
            "KeySound.GlobalOffset",
            "KeySound.ChartOffset",
            "Audio.PreservePitch",
            "Settings.About",
            "Settings.Bilibili",
            "Settings.Sponsor",
            "Settings.CheckForUpdates"
        ];
        Require(
            WindowsInspectorParityPolicy.Wpf110SettingsStaticFocusOrder
                .SequenceEqual(expectedStatic, StringComparer.Ordinal),
            "Settings static focus order differs from WPF 1.1.0.");
        var expectedExpanded = expectedStatic
            .Take(31)
            .Concat(WindowsEditorShortcutSettings.Catalog.Select(
                descriptor => $"Shortcut_{descriptor.Id}"))
            .Concat(expectedStatic.Skip(31))
            .ToArray();
        Require(
            WindowsInspectorParityPolicy.Wpf110SettingsExpandedFocusOrder
                .SequenceEqual(expectedExpanded, StringComparer.Ordinal),
            "Runtime shortcut controls are not inserted at WPF's Settings position.");
        Require(
            WindowsInspectorParityPolicy.SettingsPreservePitchEnabledWithoutLoadedAudio,
            "WPF 1.1.0 keeps Preserve Pitch enabled without a loaded audio stream.");
        Require(
            expectedExpanded
                .Select(WindowsInspectorParityPolicy.ResolveSettingsTabIndex)
                .SequenceEqual(Enumerable.Range(0, expectedExpanded.Length)),
            "Settings focus indices are not unique and contiguous.");
        Require(
            expectedExpanded
                .Reverse()
                .Select(WindowsInspectorParityPolicy.ResolveSettingsTabIndex)
                .SequenceEqual(
                    Enumerable.Range(0, expectedExpanded.Length).Reverse()),
            "Settings reverse focus indices do not preserve WPF order.");
        Require(
            WindowsInspectorParityPolicy.AllowWindowUiMutation(
                teardownStarted: false,
                closePreflightComplete: false) &&
            !WindowsInspectorParityPolicy.AllowWindowUiMutation(
                teardownStarted: true,
                closePreflightComplete: false) &&
            !WindowsInspectorParityPolicy.AllowWindowUiMutation(
                teardownStarted: false,
                closePreflightComplete: true),
            "Window close teardown must suppress late focus/status UI mutation.");
        Require(
            expectedStatic.Length == 45 &&
            expectedExpanded.Length ==
            45 + WindowsEditorShortcutSettings.Catalog.Count,
            "Settings focus surface count does not include the language selector.");
        Require(
            WindowsInspectorParityPolicy.SettingsContentPadding ==
            new Thickness(22, 14, 22, 24) &&
            WindowsInspectorParityPolicy.SettingsSeparatorMargin ==
            new Thickness(0, 18, 0, 10) &&
            WindowsInspectorParityPolicy.SettingsTitleFontSize == 25 &&
            WindowsInspectorParityPolicy.SettingsSectionTitleFontSize == 17 &&
            WindowsInspectorParityPolicy.SettingsKeySoundActionWidth == 90 &&
            WindowsInspectorParityPolicy.SettingsBottomScrollClearance == 64,
            "Settings padding, separator, headings or Key-sound actions differ from WPF.");
        return $"static={expectedStatic.Length}; shortcuts=" +
               $"{WindowsEditorShortcutSettings.Catalog.Count}; expanded=" +
               $"{expectedExpanded.Length}; first={expectedExpanded[0]}; " +
               $"last={expectedExpanded[^1]}; padding=22,14,22,24; " +
               "separator=0,18,0,10; titles=25/17; keyActions=90";
    }

    private static string VerifyFrozenNoteSoundRegistry()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "LumaChart-Avalonia-Frozen-Sound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chartPath = Path.Combine(root, "working.mc");
            var soundPath = Path.Combine(root, "frozen.ogg");
            var chartBytes = new byte[] { 0x7B, 0x7D };
            var soundBytes = new byte[] { 1, 3, 5, 7, 9, 11, 13, 15 };
            File.WriteAllBytes(chartPath, chartBytes);
            File.WriteAllBytes(soundPath, soundBytes);
            static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
            var entries = new[]
            {
                new OnlineRoomResourceEntryDto("working.mc", chartBytes.LongLength, Hash(chartBytes)),
                new OnlineRoomResourceEntryDto("frozen.ogg", soundBytes.LongLength, Hash(soundBytes))
            };
            var registry = WindowsFrozenRoomResourceRegistry.CreateAsync(
                    chartPath,
                    new OnlineRoomResourceManifestDto(
                        entries.Sum(entry => entry.Length),
                        new string('A', 64),
                        "working.mc",
                        entries))
                .GetAwaiter().GetResult();
            Require(registry.Count == 2 && registry.ContainsListedFile(soundPath),
                "The manifest-backed frozen resource was not listed.");
            Require(registry.VerifyAsync(soundPath).GetAwaiter().GetResult(),
                "The unchanged frozen resource did not pass SHA-256 verification.");

            var replaced = soundBytes.ToArray();
            replaced[0] ^= 0x7F;
            File.WriteAllBytes(soundPath, replaced);
            Require(registry.ContainsListedFile(soundPath),
                "The same-length replacement must reach the cryptographic gate.");
            Require(!registry.VerifyAsync(soundPath).GetAwaiter().GetResult(),
                "A same-length post-bind replacement passed the frozen SHA-256 gate.");
            Require(!registry.ContainsListedFile(Path.Combine(root, "unlisted.ogg")),
                "An unlisted resource entered the frozen registry.");
            return $"entries={registry.Count}; unchanged=true; sameLengthReplacement=false; unlisted=false";
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string VerifyFrozenNoteSoundPathBoundary()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "LumaChart-Avalonia-Frozen-Path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var chartPath = Path.Combine(root, "working.mc");
            File.WriteAllBytes(chartPath, [0x7B, 0x7D]);
            var rejected = false;
            try
            {
                _ = WindowsFrozenRoomResourceRegistry.CreateAsync(
                        chartPath,
                        new OnlineRoomResourceManifestDto(
                            2,
                            new string('A', 64),
                            "working.mc",
                            [new OnlineRoomResourceEntryDto(
                                "../escape.ogg",
                                2,
                                new string('B', 64))]))
                    .GetAwaiter().GetResult();
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Require(rejected, "A manifest path escape was not rejected fail closed.");
            return "../escape.ogg rejected before binding";
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string VerifyTimelineLiveAutomationState()
    {
        var note = new ChartNote
        {
            Id = Guid.Parse("2cd84510-6052-4b90-bcd4-8a3615841f48"),
            Lane = 3,
            Beat = BeatPosition.Preserve(320, 2, 12)
        };
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [note]
        });
        session.Selection.Add(note.Id);
        var surface = new TimelineSurface { Session = session };
        surface.SetCurrentBeat(319.75);
        surface.SetZoom(150);
        var peer = ControlAutomationPeer.CreatePeerForElement(surface) ??
                   throw new InvalidOperationException("TimelineSurface did not create an automation peer.");
        var before = peer.GetHelpText();
        surface.ConfigureView(
            84,
            150,
            66,
            32,
            1,
            .79,
            1.5,
            .85,
            WindowsNoteAnchorMode.LineAbove);
        var anchored = peer.GetHelpText();
        surface.SetCurrentBeat(320);
        var after = peer.GetHelpText();
        var pendingOperationId =
            Guid.Parse("750c773e-f76b-451b-bf26-9b80dba5a0cf");
        surface.SetRoomSyncDiagnostics(
            "BackingOff",
            "Reconnecting",
            1,
            pendingOperationId,
            2,
            3);
        var reconnecting = peer.GetHelpText();
        surface.ResetRoomSyncDiagnostics();
        var reset = peer.GetHelpText();
        Require(
            before.Contains("CurrentBeat=319.75", StringComparison.Ordinal) &&
            before.Contains("PixelsPerBeat=150", StringComparison.Ordinal) &&
            before.Contains("SelectedNoteCount=1", StringComparison.Ordinal) &&
            before.Contains("SelectedTimingCount=0", StringComparison.Ordinal) &&
            before.Contains("OwnerProtectedRegionCount=0", StringComparison.Ordinal) &&
            before.Contains($"SelectedNoteId={note.Id:D}", StringComparison.Ordinal) &&
            before.Contains("SelectedNoteLane=3", StringComparison.Ordinal) &&
            before.Contains("SelectedNoteBeat=320:2/12", StringComparison.Ordinal) &&
            before.Contains("NoteAnchor=CellBottom", StringComparison.Ordinal) &&
            before.EndsWith("Note 对齐 格内靠下", StringComparison.Ordinal) &&
            anchored.Contains("CurrentBeat=319.75", StringComparison.Ordinal) &&
            anchored.Contains("NoteAnchor=LineAbove", StringComparison.Ordinal) &&
            anchored.EndsWith("Note 对齐 贴在线上", StringComparison.Ordinal) &&
            after.Contains("CurrentBeat=320", StringComparison.Ordinal) &&
            after.Contains("NoteAnchor=LineAbove", StringComparison.Ordinal) &&
            after.EndsWith("Note 对齐 贴在线上", StringComparison.Ordinal) &&
            !after.Contains("CurrentBeat=319.75", StringComparison.Ordinal) &&
            reconnecting.Contains(
                "RoomSyncWorkStatus=BackingOff",
                StringComparison.Ordinal) &&
            reconnecting.Contains(
                "RoomSyncFeedStatus=Reconnecting",
                StringComparison.Ordinal) &&
            reconnecting.Contains(
                "RoomSyncRevision=1",
                StringComparison.Ordinal) &&
            reconnecting.Contains(
                $"RoomSyncPendingOperationId={pendingOperationId:D}",
                StringComparison.Ordinal) &&
            reconnecting.Contains(
                "RoomSyncConsecutiveWorkFailures=2",
                StringComparison.Ordinal) &&
            reconnecting.Contains(
                "RoomSyncConsecutiveFeedFailures=3",
                StringComparison.Ordinal) &&
            reset.Contains("RoomSyncWorkStatus=none", StringComparison.Ordinal) &&
            reset.Contains("RoomSyncFeedStatus=none", StringComparison.Ordinal) &&
            reset.Contains("RoomSyncRevision=-1", StringComparison.Ordinal) &&
            reset.Contains(
                "RoomSyncPendingOperationId=none",
                StringComparison.Ordinal),
            "Timeline UIA state was stale or incomplete. " +
            $"before='{before}'; anchored='{anchored}'; after='{after}'; " +
            $"reconnecting='{reconnecting}'; reset='{reset}'.");
        return $"before={before}; anchored={anchored}; after={after}; " +
               $"reconnecting={reconnecting}; reset={reset}";
    }

    private static void Check(
        ICollection<WindowsInspectorParitySpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsInspectorParitySpecResult(name, true, verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsInspectorParitySpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsInspectorParitySpecResult(
    string Name,
    bool Passed,
    string Detail);

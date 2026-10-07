using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Application.Workspaces;
using LumaChart.Core.Models;
using System.Collections.Specialized;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsEditorSurfaceParitySpecs
{
    public static IReadOnlyList<WindowsEditorSurfaceParitySpecResult> Run()
    {
        return
        [
            VerifyWheelDirectionMatchesWpf(),
            VerifyTimelineGridHierarchyMatchesWpf(),
            VerifyTimelineSubdivisionCheckPalette(),
            VerifyHiddenLongNoteBodyMode(),
            VerifyImeDuplicateCommitGuardHook(),
            VerifyTimelineMeasureNumbersMatchWpf(),
            VerifyHoldInteractionChromeMatchesWpf(),
            VerifyPlaybackHoverCannotEnterSeekPipeline(),
            VerifyManualTimelineNavigationPausesPlayback(),
            VerifyChartExportDefaultsToDownloads(),
            VerifyCompactOnlineSurfacesAreWired(),
            VerifyQuickValueAdjustmentsRestoreTimelineFocus(),
            VerifyNavigationMarkersAreWired(),
            VerifyLargeLibraryStartupStaysResponsive(),
            VerifyPlaybackChromeAndInspectorFitAreWired(),
            VerifyRoomUploadAndActualAvatarsAreWired(),
            VerifyRoomChatPanelClampIsWired(),
            VerifyLoginLibraryAndPlayerListRegressionsAreWired()
        ];
    }

    private static WindowsEditorSurfaceParitySpecResult VerifyTimelineSubdivisionCheckPalette()
    {
        var mainWindowSource = ReadMainWindowSource();
        var timelineSource = ReadProjectSource(
            "LumaChart.Avalonia",
            "Controls",
            "TimelineSurface.cs");
        var normalColors = TimelineSubdivisionCheckPalette.SupportedDenominators
            .Select(denominator => TimelineSubdivisionCheckPalette.Resolve(denominator))
            .ToArray();
        var warning46 = TimelineSubdivisionCheckPalette.Resolve(46);
        var warning47 = TimelineSubdivisionCheckPalette.Resolve(47);
        var warning960 = TimelineSubdivisionCheckPalette.Resolve(960);
        var normalColorsAreBright = normalColors.All(style =>
            Math.Max(style.Color.R, Math.Max(style.Color.G, style.Color.B)) >= 229 &&
            (int)style.Color.R + style.Color.G + style.Color.B >= 360);
        var passed =
            normalColors.All(style => !style.IsWarning) &&
            normalColorsAreBright &&
            normalColors.Select(style => style.Color).Distinct().Count() == normalColors.Length &&
            !TimelineSubdivisionCheckPalette.IsSupportedDenominator(14) &&
            TimelineSubdivisionCheckPalette.IsSupportedDenominator(64) &&
            TimelineSubdivisionCheckPalette.IsSupportedDenominator(96) &&
            !TimelineSubdivisionCheckPalette.IsSupportedDenominator(46) &&
            !TimelineSubdivisionCheckPalette.IsSupportedDenominator(192) &&
            !TimelineSubdivisionCheckPalette.IsSupportedDenominator(480) &&
            !TimelineSubdivisionCheckPalette.IsSupportedDenominator(960) &&
            warning46.IsWarning &&
            warning960.IsWarning &&
            warning46.Color == TimelineSubdivisionCheckPalette.WarningColor &&
            warning46.Color == warning47.Color &&
            warning46.Color == warning960.Color &&
            mainWindowSource.Contains("Editor.SubdivisionCheck", StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "timeline.SetSubdivisionCheckEnabled(enabled)",
                StringComparison.Ordinal) &&
            timelineSource.Contains(
                "ResolveSubdivisionCheckPen(beat.Denominator)",
                StringComparison.Ordinal) &&
            timelineSource.Contains(
                "endBeat.Value.Denominator",
                StringComparison.Ordinal);
        return Result(
            "windows-timeline-subdivision-check-colors-exact-denominators-and-warns-outliers",
            passed,
            $"normal={normalColors.Length}; bright={normalColorsAreBright}; " +
            $"unique={normalColors.Select(style => style.Color).Distinct().Count()}; " +
            $"46/960={warning46.Color}/{warning960.Color}; " +
            $"cellCenter64/96={TimelineSubdivisionCheckPalette.IsSupportedDenominator(64)}/" +
            $"{TimelineSubdivisionCheckPalette.IsSupportedDenominator(96)}");
    }

    private static WindowsEditorSurfaceParitySpecResult VerifyHiddenLongNoteBodyMode()
    {
        var mainWindowSource = ReadMainWindowSource();
        var timelineSource = ReadProjectSource(
            "LumaChart.Avalonia",
            "Controls",
            "TimelineSurface.cs");
        var passed =
            mainWindowSource.Contains(
                "Editor.Extension.HideLongNoteBodies",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "timeline.SetHideLongNoteBodies(enabled);",
                StringComparison.Ordinal) &&
            timelineSource.Contains("HiddenHoldHeadPen", StringComparison.Ordinal) &&
            timelineSource.Contains(
                "Color.Parse(\"#FF9A86B8\")",
                StringComparison.Ordinal) &&
            timelineSource.Contains(
                "HiddenHoldHeadBorderThickness = 3;",
                StringComparison.Ordinal) &&
            timelineSource.Contains(
                "DrawTapNote(context, headRect, headSubdivisionPen ?? HiddenHoldHeadPen);",
                StringComparison.Ordinal) &&
            !timelineSource.Contains("HiddenHoldTailPen", StringComparison.Ordinal) &&
            !timelineSource.Contains("HiddenHoldTailBrush", StringComparison.Ordinal) &&
            timelineSource.Contains(
                "hideLongNoteBodies && note.EndBeat.HasValue",
                StringComparison.Ordinal) &&
            timelineSource.Contains(
                "Math.Abs(pointerY - startY)",
                StringComparison.Ordinal) &&
            !timelineSource.Contains(
                "Math.Abs(pointerY - endY)",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "仅用边框色区分头部 Note",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "Color.Parse(\"#9A86B8\")",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "BorderThickness = new Thickness(3)",
                StringComparison.Ordinal);
        return Result(
            "windows-hidden-ln-body-renders-only-border-colored-head-note",
            passed,
            "head=normal Note with 3-DIP soft purple border; body/tail hidden; " +
            "only the visible head remains clickable");
    }

    private static WindowsEditorSurfaceParitySpecResult VerifyImeDuplicateCommitGuardHook()
    {
        var mainWindowSource = ReadMainWindowSource();
        var passed =
            mainWindowSource.Contains(
                "InputElement.TextInputEvent,",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "imeDuplicateCommitGuard.ObserveKeyDown(",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "imeDuplicateCommitGuard.ShouldSuppressReplay(",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "eventArgs.Handled = true;",
                StringComparison.Ordinal);
        return Result(
            "windows-wechat-ime-shift-replay-is-filtered-before-textbox-insertion",
            passed,
            "first composed TextInput is retained; exact per-character replay is handled in tunnel");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyHoldInteractionChromeMatchesWpf()
    {
        var style = WindowsTimelineVisualParityPolicy.HoldInteractionStyle;
        var source = ReadProjectSource(
            "LumaChart.Avalonia",
            "Controls",
            "TimelineSurface.cs");
        var placementPreview = Between(
            source,
            "private void DrawPlacementPreview(",
            "private Rect GetNoteBounds(");
        var tailPreview = Between(
            source,
            "private void DrawHoldTailResizePreview(",
            "private void DrawPlacementPreview(");
        var passed =
            style == new WindowsTimelineHoldInteractionStyle(true, .94, 7) &&
            placementPreview.Contains(
                "DrawHoldInteractionPreview(",
                StringComparison.Ordinal) &&
            tailPreview.Contains(
                "DrawHoldInteractionPreview(",
                StringComparison.Ordinal) &&
            source.Contains(
                "DrawLongNote(",
                StringComparison.Ordinal) &&
            !placementPreview.Contains(
                "HoldPreviewBrush",
                StringComparison.Ordinal) &&
            !tailPreview.Contains(
                "HoldTailPreviewBrush",
                StringComparison.Ordinal);
        return Result(
            "windows-ln-drag-and-tail-resize-use-final-wpf-note-chrome",
            passed,
            $"finalChrome={style.UseFinalLongNoteChrome}; " +
            $"opacity={style.Opacity:0.00}; tailInset={style.TailLineInset:0}");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyWheelDirectionMatchesWpf()
    {
        var up =
            WindowsTimelineVisualParityPolicy.ResolveWheelDirection(120);
        var down =
            WindowsTimelineVisualParityPolicy.ResolveWheelDirection(-120);
        var idle =
            WindowsTimelineVisualParityPolicy.ResolveWheelDirection(0);
        return Result(
            "windows-timeline-wheel-direction-matches-wpf",
            up == 1 && down == -1 && idle == 0,
            $"up={up}; down={down}; idle={idle}");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyTimelineGridHierarchyMatchesWpf()
    {
        var irregular = WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(
            WindowsTimelineGridLineKind.Irregular);
        var quarter = WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(
            WindowsTimelineGridLineKind.Quarter);
        var sixth = WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(
            WindowsTimelineGridLineKind.Sixth);
        var half = WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(
            WindowsTimelineGridLineKind.Half);
        var beat = WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(
            WindowsTimelineGridLineKind.Beat);
        var bar = WindowsTimelineVisualParityPolicy.ResolveGridLineStyle(
            WindowsTimelineGridLineKind.Bar);
        var configuredIrregular =
            WindowsTimelineVisualParityPolicy.ResolveConfiguredGridLineThickness(
                WindowsTimelineGridLineKind.Irregular,
                1.6,
                3.25);
        var configuredQuarter =
            WindowsTimelineVisualParityPolicy.ResolveConfiguredGridLineThickness(
                WindowsTimelineGridLineKind.Quarter,
                1.6,
                3.25);
        var configuredBeat =
            WindowsTimelineVisualParityPolicy.ResolveConfiguredGridLineThickness(
                WindowsTimelineGridLineKind.Beat,
                1.6,
                3.25);
        var configuredBar =
            WindowsTimelineVisualParityPolicy.ResolveConfiguredGridLineThickness(
                WindowsTimelineGridLineKind.Bar,
                1.6,
                3.25);
        var passed =
            WindowsTimelineVisualParityPolicy.ResolveGridLineKind(4, 0) ==
            WindowsTimelineGridLineKind.Bar &&
            WindowsTimelineVisualParityPolicy.ResolveGridLineKind(4, 4) ==
            WindowsTimelineGridLineKind.Beat &&
            WindowsTimelineVisualParityPolicy.ResolveGridLineKind(4, 2) ==
            WindowsTimelineGridLineKind.Half &&
            WindowsTimelineVisualParityPolicy.ResolveGridLineKind(4, 1) ==
            WindowsTimelineGridLineKind.Quarter &&
            WindowsTimelineVisualParityPolicy.ResolveGridLineKind(6, 1) ==
            WindowsTimelineGridLineKind.Sixth &&
            WindowsTimelineVisualParityPolicy.ResolveGridLineKind(8, 1) ==
            WindowsTimelineGridLineKind.Irregular &&
            irregular == new WindowsTimelineGridLineStyle("#765A6266", .8) &&
            quarter == new WindowsTimelineGridLineStyle("#A8A96552", 1) &&
            sixth == new WindowsTimelineGridLineStyle("#A777679C", 1) &&
            half == new WindowsTimelineGridLineStyle("#B06E8792", 1) &&
            beat == new WindowsTimelineGridLineStyle("#B7929A9F", 1.25) &&
            bar == new WindowsTimelineGridLineStyle("#D0C2B5A3", 1.55) &&
            Math.Abs(configuredIrregular - 1.6) < 1e-12 &&
            Math.Abs(configuredQuarter - 2) < 1e-12 &&
            Math.Abs(configuredBeat - 2.5) < 1e-12 &&
            Math.Abs(configuredBar - 3.25) < 1e-12;
        return Result(
            "windows-timeline-grid-hierarchy-and-colors-match-wpf",
            passed,
            $"kinds=bar/beat/half/quarter/sixth/irregular; " +
            $"colors={bar.Color}/{beat.Color}/{half.Color}/" +
            $"{quarter.Color}/{sixth.Color}/{irregular.Color}; configured=" +
            $"{configuredBar:0.00}/{configuredBeat:0.00}/" +
            $"{configuredQuarter:0.00}/{configuredIrregular:0.00}");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyTimelineMeasureNumbersMatchWpf()
    {
        var source = ReadProjectSource(
            "LumaChart.Avalonia",
            "Controls",
            "TimelineSurface.cs");
        var passed =
            WindowsTimelineVisualParityPolicy.ResolveMeasureNumber(4, 0) == 1 &&
            WindowsTimelineVisualParityPolicy.ResolveMeasureNumber(4, 4) == 2 &&
            WindowsTimelineVisualParityPolicy.ResolveMeasureNumber(4, 1396) == 350 &&
            WindowsTimelineVisualParityPolicy.ResolveMeasureNumber(4, 1397) == null &&
            source.Contains(
                "Matrix.CreateRotation(-Math.PI / 2)",
                StringComparison.Ordinal) &&
            source.Contains(
                "geometry.TrackLeft - MeasureNumberRailWidth",
                StringComparison.Ordinal);
        return Result(
            "windows-timeline-shows-rotated-measure-numbers-left-of-grid",
            passed,
            "whole measures use source index+1; subdivision omitted; " +
            "rotated left rail reserves space from Timing labels");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyPlaybackHoverCannotEnterSeekPipeline()
    {
        var source = ReadMainWindowSource();
        var passed =
            source.Contains(
                "timeline.HoverChanged += (_, status) => statusText.Text = status;",
                StringComparison.Ordinal) &&
            !source.Contains(
                "timeline.HoverChanged += (_, status) => viewModel.SetStatus(status);",
                StringComparison.Ordinal) &&
            source.Contains(
                "SetProgrammaticPlaybackBeat(activeTab.CurrentBeat);",
                StringComparison.Ordinal);
        return Result(
            "windows-playback-hover-is-ui-only-and-programmatic-refresh-cannot-seek",
            passed,
            "hover→statusText only; UpdateUi beat write is guarded as programmatic");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyManualTimelineNavigationPausesPlayback()
    {
        var mainWindowSource = ReadMainWindowSource();
        var timelineSource = ReadProjectSource(
            "LumaChart.Avalonia",
            "Controls",
            "TimelineSurface.cs");
        var queueSeek = Between(
            mainWindowSource,
            "private bool QueueEditorSeek(",
            "private void UpdatePendingEditorSeekTarget(");
        var passed =
            timelineSource.Contains(
                "ManualNavigationRequested?.Invoke(",
                StringComparison.Ordinal) &&
            timelineSource.Contains(
                "SetCurrentBeatFromManualNavigation(",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "timeline.ManualNavigationRequested += Timeline_ManualNavigationRequested;",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "PauseEditorPlaybackForManualNavigationAsync()",
                StringComparison.Ordinal) &&
            mainWindowSource.Contains(
                "resumeAfterSeek: DateTimeOffset.UtcNow > editorSeekResumeSuppressedUntilUtc",
                StringComparison.Ordinal) &&
            queueSeek.Contains(
                "if (!resumeAfterSeek) editorSeekResumeRequested = false;",
                StringComparison.Ordinal) &&
            queueSeek.Contains(
                "resumeAfterSeek && (musicWasPlaying || usesSilent)",
                StringComparison.Ordinal);
        return Result(
            "windows-wheel-and-pointer-navigation-pause-without-resuming-playback",
            passed,
            "wheel/pan emits manual navigation; pending seeks suppress resume; " +
            "non-beat scroll modes pause directly");
    }

    private static WindowsEditorSurfaceParitySpecResult VerifyRoomChatPanelClampIsWired()
    {
        var source = ReadMainWindowSource();
        var queuedClamp = Between(
            source,
            "private void QueueFloatingPaletteClamp()",
            "private void SeparateFloatingPalettesIfNeeded()");
        var visibility = Between(
            source,
            "private void RefreshRoomCollaborationPanelVisibility()",
            "private bool IsRoomChatActivelyVisible()");
        var passed =
            queuedClamp.Contains(
                "ClampRoomCollaborationPosition();",
                StringComparison.Ordinal) &&
            visibility.Contains(
                "if (visible) QueueFloatingPaletteClamp();",
                StringComparison.Ordinal) &&
            source.Contains(
                "roomCollaborationPanel.SizeChanged += (_, _) => QueueFloatingPaletteClamp();",
                StringComparison.Ordinal) &&
            source.Contains(
                "ApplyRoomCollaborationCollapsedState();",
                StringComparison.Ordinal);
        return Result(
            "windows-room-chat-panel-keeps-draggable-header-in-workbench",
            passed,
            "clamped after resize, reveal, collapse and expansion");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyChartExportDefaultsToDownloads()
    {
        var source = ReadMainWindowSource();
        var resolver = Between(
            source,
            "private async Task<IStorageFolder?> ResolveChartExportStartLocationAsync(",
            "private Task RevealExportedFileAsync(");
        var passed =
            source.Contains(
                "SuggestedStartLocation = exportStartLocation",
                StringComparison.Ordinal) &&
            resolver.Contains(
                "WellKnownFolder.Downloads",
                StringComparison.Ordinal) &&
            resolver.Contains(
                "Environment.SpecialFolder.UserProfile",
                StringComparison.Ordinal);
        return Result(
            "windows-chart-export-picker-defaults-to-downloads",
            passed,
            "Windows Downloads known folder; conventional Downloads fallback; " +
            "source directory used only when Downloads cannot be resolved");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyCompactOnlineSurfacesAreWired()
    {
        var source = ReadMainWindowSource();
        var quickButton = Between(
            source,
            "private static Button QuickPaletteButton(",
            "private void RegisterQuickShortcutButton(");
        var quickValueButton = Between(
            source,
            "private static Button QuickValueButton(",
            "private static void ConfigureQuickValueText(");
        var roomPoll = Between(
            source,
            "private async Task RunOnlineRoomPresenceLoopAsync(",
            "private void ApplyOnlineRoomLiveState(");
        var passed =
            source.Contains(
                "async (_, _) => await ToggleFriendsPanelAsync()",
                StringComparison.Ordinal) &&
            source.Contains(
                "private Control BuildFriendsPanel()",
                StringComparison.Ordinal) &&
            source.Contains(
                "private Control BuildRoomCollaborationPanel()",
                StringComparison.Ordinal) &&
            roomPoll.Contains(
                "GetRoomLiveStateAsync(",
                StringComparison.Ordinal) &&
            roomPoll.Contains(
                "roomChartSyncRoomId == roomId ? latestRoomMessageId : 0",
                StringComparison.Ordinal) &&
            !roomPoll.Contains(
                "SendRoomHeartbeatAsync(",
                StringComparison.Ordinal) &&
            source.Contains(
                "roomChatUnreadDot.IsVisible = unread;",
                StringComparison.Ordinal) &&
            source.Contains(
                "editorWorkbench.SizeChanged += (_, _) => QueueFloatingPaletteClamp();",
                StringComparison.Ordinal) &&
            source.Contains(
                "ClampFloatingControl(quickValuesPalette, quickValuesPaletteTransform);",
                StringComparison.Ordinal) &&
            quickButton.Contains("Width = 84", StringComparison.Ordinal) &&
            quickButton.Contains(
                "TextAlignment = TextAlignment.Center",
                StringComparison.Ordinal) &&
            quickValueButton.Contains(
                "BorderBrush = Brushes.Transparent",
                StringComparison.Ordinal) &&
            quickValueButton.Contains(
                "Height = WindowsInspectorParityPolicy.QuickValuesControlHeight",
                StringComparison.Ordinal) &&
            quickValueButton.Contains(
                "VerticalContentAlignment = VerticalAlignment.Center",
                StringComparison.Ordinal) &&
            !quickValueButton.Contains(
                "BorderBrush = StrokeBrush",
                StringComparison.Ordinal);
        return Result(
            "windows-compact-friends-room-chat-and-centered-palettes-wired",
            passed,
            "friends=compact overlay; room=draggable chat+unread; " +
            "quickEdit=centered grid; quickValues=transparent idle borders");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyQuickValueAdjustmentsRestoreTimelineFocus()
    {
        var source = ReadMainWindowSource();
        var quickValueSection = Between(
            source,
            "private Control BuildQuickValueSection(",
            "private static Button QuickValueButton(");
        var passed =
            quickValueSection.Contains(
                "increaseButton.Click += (_, _) => timeline.Focus();",
                StringComparison.Ordinal) &&
            quickValueSection.Contains(
                "decreaseButton.Click += (_, _) => timeline.Focus();",
                StringComparison.Ordinal);
        return Result(
            "windows-quick-value-adjustments-return-focus-to-timeline",
            passed,
            "snap/grid/rate arrow clicks restore timeline focus before Space transport");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyPlaybackChromeAndInspectorFitAreWired()
    {
        var source = ReadMainWindowSource();
        var cancelSeek = Between(
            source,
            "private Task CancelPendingEditorSeek()",
            "private void SetProgrammaticPlaybackBeat(");
        var passed =
            source.Contains(
                "new DispatcherTimer(DispatcherPriority.Normal)",
                StringComparison.Ordinal) &&
            cancelSeek.Contains("return pauseTask.IsCompletedSuccessfully", StringComparison.Ordinal) &&
            !cancelSeek.Contains("Task.WhenAll(pauseTask, commitTask)", StringComparison.Ordinal) &&
            source.Contains("private AvaloniaPath? quickPlayIcon;", StringComparison.Ordinal) &&
            source.Contains(
                "\"M2,1 H6 V16 H2 Z M10,1 H14 V16 H10 Z\"",
                StringComparison.Ordinal) &&
            source.Contains(
                "Data = Geometry.Parse(\"M2,2 L11,11 M11,2 L2,11\")",
                StringComparison.Ordinal) &&
            source.Contains(
                "ConstrainInspectorSlider(audioVolumeSlider);",
                StringComparison.Ordinal) &&
            source.Contains(
                "ApplyWpfPrimaryButtonStyle(check);",
                StringComparison.Ordinal) &&
            !source.Contains("proGridColumnSlider.Width = 104;", StringComparison.Ordinal);
        return Result(
            "windows-playback-command-chrome-and-inspector-fit-regressions-wired",
            passed,
            "seek cancellation nonblocking; play/pause+tab icons explicit; " +
            "settings sliders constrained; update accent; ProEditor slider stretches");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyNavigationMarkersAreWired()
    {
        var source = ReadMainWindowSource();
        var overview = ReadProjectSource(
            "LumaChart.Avalonia",
            "Controls",
            "EditorOverviewSurface.cs");
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var duplicateId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var early = new DesktopNavigationMarker(
            secondId,
            BeatPosition.Preserve(1, 0, 1));
        var late = new DesktopNavigationMarker(
            firstId,
            BeatPosition.Preserve(4, 1, 2));
        var duplicate = new DesktopNavigationMarker(
            duplicateId,
            BeatPosition.Preserve(1, 0, 2));
        var normalized = WindowsNavigationMarkerPolicy.Normalize(
            [late, duplicate, early]);
        var added = WindowsNavigationMarkerPolicy.Add(
            normalized,
            new DesktopNavigationMarker(
                Guid.Parse("00000000-0000-0000-0000-000000000004"),
                BeatPosition.Preserve(7, 0, 1)),
            out var duplicateAdd);
        var full = Enumerable.Range(0, WindowsNavigationMarkerPolicy.MaximumMarkers)
            .Select(index => new DesktopNavigationMarker(
                Guid.NewGuid(),
                BeatPosition.Preserve(index, 0, 1)))
            .ToArray();
        var capped = WindowsNavigationMarkerPolicy.Add(
            full,
            new DesktopNavigationMarker(
                Guid.Parse("00000000-0000-0000-0000-000000000005"),
                BeatPosition.Preserve(100, 0, 1)),
            out var fullDuplicate);
        var removed = WindowsNavigationMarkerPolicy.Remove(
            added,
            firstId);
        var passed =
            normalized.Count == 2 &&
            normalized[0].Beat.Equals(early.Beat) &&
            normalized[1].Beat.Equals(late.Beat) &&
            !duplicateAdd &&
            added.Count == 3 &&
            !fullDuplicate &&
            capped.Count == WindowsNavigationMarkerPolicy.MaximumMarkers &&
            !capped.Any(marker => marker.Beat.Value == 100) &&
            removed.All(marker => marker.Id != firstId) &&
            source.Contains("BuildNavigationMarkerPopup()", StringComparison.Ordinal) &&
            source.Contains(
                "NavigationMarkerSeekRequested += beat",
                StringComparison.Ordinal) &&
            source.Contains(
                "editorOverview.SetNavigationMarkers(activeTab.NavigationMarkers)",
                StringComparison.Ordinal) &&
            source.Contains(
                "PersistPendingNavigationMarkersAsync(force: true)",
                StringComparison.Ordinal) &&
            overview.Contains(
                "NavigationMarkerSeekRequested",
                StringComparison.Ordinal) &&
            overview.Contains(
                "context.DrawEllipse(",
                StringComparison.Ordinal) &&
            overview.Contains(
                "marker.Value.Beat",
                StringComparison.Ordinal);
        return Result(
            "windows-navigation-markers-are-local-exact-and-bounded",
            passed,
            $"normalized={normalized.Count}; added={added.Count}; capped={capped.Count}; " +
            $"removed={removed.Count}; exact-seek={overview.Contains(
                "NavigationMarkerSeekRequested",
                StringComparison.Ordinal)}");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyLargeLibraryStartupStaysResponsive()
    {
        var mainWindow = ReadMainWindowSource();
        var viewModel = ReadProjectSource(
            "LumaChart.Avalonia",
            "ViewModels",
            "MainWindowViewModel.cs");
        var collection = new BatchObservableCollection<int>();
        var notifications = 0;
        NotifyCollectionChangedAction? lastAction = null;
        collection.CollectionChanged += (_, eventArgs) =>
        {
            notifications++;
            lastAction = eventArgs.Action;
        };
        collection.ReplaceAll(Enumerable.Range(0, 4_096));
        var passed =
            collection.Count == 4_096 &&
            notifications == 1 &&
            lastAction == NotifyCollectionChangedAction.Reset &&
            mainWindow.Contains(
                "scanLibrary: !deferInitialLibraryScan",
                StringComparison.Ordinal) &&
            mainWindow.Contains(
                "StartDeferredLibraryScan,",
                StringComparison.Ordinal) &&
            mainWindow.Contains(
                "viewModel.CancelLibraryScan();",
                StringComparison.Ordinal) &&
            viewModel.Contains(
                "libraryEntries.ReplaceAll(entries);",
                StringComparison.Ordinal) &&
            viewModel.Contains(
                "libraryFolders.ReplaceAll(folders);",
                StringComparison.Ordinal);
        return Result(
            "windows-large-library-startup-defers-scan-and-batches-ui-notifications",
            passed,
            $"entries={collection.Count}; notifications={notifications}; action={lastAction}");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyRoomUploadAndActualAvatarsAreWired()
    {
        var source = ReadMainWindowSource();
        var upload = Between(
            source,
            "private async Task InitializeOwnedRoomChartAsync(",
            "private void ApplyInitialRoomSnapshot(");
        var passed =
            source.Contains(
                "\"RoomUpload.Progress.Popup\"",
                StringComparison.Ordinal) &&
            source.Contains(
                "\"RoomUpload.Progress.Bar\"",
                StringComparison.Ordinal) &&
            upload.Contains(
                "ShowRoomUploadProgress(room.Id, room.Name, package);",
                StringComparison.Ordinal) &&
            upload.Contains(
                "UpdateRoomUploadProgress(room.Id, value);",
                StringComparison.Ordinal) &&
            upload.Contains(
                "await HideRoomUploadProgressAsync(room.Id, uploadSucceeded);",
                StringComparison.Ordinal) &&
            source.Contains(
                "private readonly WindowsDefaultUserAvatar titleUserAvatar",
                StringComparison.Ordinal) &&
            source.Contains(
                "BindRemoteAvatar(titleUserAvatar,",
                StringComparison.Ordinal) &&
            source.Contains(
                "BindRemoteAvatar(memberAvatar, member.AvatarKey, member.DisplayName);",
                StringComparison.Ordinal) &&
            source.Contains(
                "BindRemoteAvatar(messageAvatar, message.AvatarKey, message.DisplayName);",
                StringComparison.Ordinal);
        return Result(
            "windows-room-create-upload-progress-and-real-avatars-wired",
            passed,
            "create upload exposes byte progress popup; title/member/chat bind AvatarKey");
    }

    private static WindowsEditorSurfaceParitySpecResult
        VerifyLoginLibraryAndPlayerListRegressionsAreWired()
    {
        var main = ReadMainWindowSource();
        var login = ReadProjectSource(
            "LumaChart.Avalonia",
            "Dialogs",
            "OnlineAccountDialog.cs");
        var community = ReadProjectSource(
            "LumaChart.Avalonia",
            "Dialogs",
            "OnlineCommunityDialog.cs");
        var libraryCard = ReadProjectSource(
            "LumaChart.Avalonia",
            "Controls",
            "LibraryFolderCard.cs");
        var passed =
            login.Contains(
                "HorizontalAlignment = HorizontalAlignment.Center",
                StringComparison.Ordinal) &&
            login.Contains(
                "TextAlignment = TextAlignment.Center",
                StringComparison.Ordinal) &&
            login.Contains(
                "text.Padding = new Thickness(12, 0);",
                StringComparison.Ordinal) &&
            login.Contains(
                "text.VerticalContentAlignment = VerticalAlignment.Center;",
                StringComparison.Ordinal) &&
            main.Contains(
                "MaxWidth = WindowsInspectorParityPolicy.NoteHintMaximumWidth",
                StringComparison.Ordinal) &&
            community.Contains(
                "ApplyPlayerDirectoryListStyles(directoryList);",
                StringComparison.Ordinal) &&
            main.Contains(
                "ApplyCompactPlayerListStyles(roomMemberList);",
                StringComparison.Ordinal) &&
            libraryCard.Contains(
                "SynchronizeCurrentViewFromContainer();",
                StringComparison.Ordinal) &&
            libraryCard.Contains(
                ".Content as DesktopLibraryFolderView;",
                StringComparison.Ordinal);
        return Result(
            "windows-login-note-library-and-player-list-regressions-wired",
            passed,
            "login identity+inputs centered; Note hint bounded; player selection themed; " +
            "recycled library card synchronizes before first click");
    }

    private static string ReadMainWindowSource()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(
            Path.Combine(projectRoot, "LumaChart.Avalonia", "MainWindow.cs"));
    }

    private static string ReadProjectSource(params string[] segments)
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return File.ReadAllText(
            Path.Combine([projectRoot, .. segments]));
    }

    private static string Between(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(
            endMarker,
            Math.Max(0, start),
            StringComparison.Ordinal);
        return start >= 0 && end > start
            ? source[start..end]
            : string.Empty;
    }

    private static WindowsEditorSurfaceParitySpecResult Result(
        string name,
        bool passed,
        string detail) => new(name, passed, detail);
}

internal sealed record WindowsEditorSurfaceParitySpecResult(
    string Name,
    bool Passed,
    string Detail);

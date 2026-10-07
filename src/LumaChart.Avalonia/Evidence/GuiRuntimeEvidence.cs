using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using LumaChart.Application.Libraries;
using LumaChart.Application.Validation;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.Verification;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;
using LumaChart.Core.Validation;

namespace LumaChart.Avalonia.Evidence;

public static class GuiRuntimeEvidenceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static GuiRuntimeEvidence Capture(
        PlatformWindow window,
        TimelineSurface timeline,
        WindowsAudioTransportViewModel audioTransport,
        WindowsAudioVisualizationViewModel audioVisualization,
        DesktopChartTab tab,
        string sourcePath,
        string workloadKind,
        int? syntheticNoteCount,
        TimeSpan probeDuration,
        int warmupTicks,
        int probeTicks,
        int targetRenderSamples)
        => CaptureCore(
            window,
            timeline,
            audioTransport,
            audioVisualization,
            tab,
            sourcePath,
            workloadKind,
            syntheticNoteCount,
            probeDuration,
            warmupTicks,
            probeTicks,
            targetRenderSamples,
            keySoundRuntime: null,
            keySoundSettings: null,
            displayScaleController: null,
            displayScaleSettings: null,
            audioPanels: null,
            libraryDeletion: null,
            chartValidation: null);

    internal static GuiRuntimeEvidence Capture(
        PlatformWindow window,
        TimelineSurface timeline,
        WindowsAudioTransportViewModel audioTransport,
        WindowsAudioVisualizationViewModel audioVisualization,
        DesktopChartTab tab,
        string sourcePath,
        string workloadKind,
        int? syntheticNoteCount,
        TimeSpan probeDuration,
        int warmupTicks,
        int probeTicks,
        int targetRenderSamples,
        WindowsKeySoundPlaybackRuntime keySoundRuntime,
        WindowsKeySoundSettings keySoundSettings)
        => CaptureCore(
            window,
            timeline,
            audioTransport,
            audioVisualization,
            tab,
            sourcePath,
            workloadKind,
            syntheticNoteCount,
            probeDuration,
            warmupTicks,
            probeTicks,
            targetRenderSamples,
            keySoundRuntime,
            keySoundSettings,
            displayScaleController: null,
            displayScaleSettings: null,
            audioPanels: null,
            libraryDeletion: null,
            chartValidation: null);

    internal static GuiRuntimeEvidence Capture(
        PlatformWindow window,
        TimelineSurface timeline,
        WindowsAudioTransportViewModel audioTransport,
        WindowsAudioVisualizationViewModel audioVisualization,
        DesktopChartTab tab,
        string sourcePath,
        string workloadKind,
        int? syntheticNoteCount,
        TimeSpan probeDuration,
        int warmupTicks,
        int probeTicks,
        int targetRenderSamples,
        WindowsKeySoundPlaybackRuntime keySoundRuntime,
        WindowsKeySoundSettings keySoundSettings,
        WindowsDisplayScaleController displayScaleController,
        WindowsDisplayScaleSettings displayScaleSettings)
        => CaptureCore(
            window,
            timeline,
            audioTransport,
            audioVisualization,
            tab,
            sourcePath,
            workloadKind,
            syntheticNoteCount,
            probeDuration,
            warmupTicks,
            probeTicks,
            targetRenderSamples,
            keySoundRuntime,
            keySoundSettings,
            displayScaleController,
            displayScaleSettings,
            audioPanels: null,
            libraryDeletion: null,
            chartValidation: null);

    internal static GuiRuntimeEvidence Capture(
        PlatformWindow window,
        TimelineSurface timeline,
        WindowsAudioTransportViewModel audioTransport,
        WindowsAudioVisualizationViewModel audioVisualization,
        DesktopChartTab tab,
        string sourcePath,
        string workloadKind,
        int? syntheticNoteCount,
        TimeSpan probeDuration,
        int warmupTicks,
        int probeTicks,
        int targetRenderSamples,
        WindowsKeySoundPlaybackRuntime keySoundRuntime,
        WindowsKeySoundSettings keySoundSettings,
        WindowsDisplayScaleController displayScaleController,
        WindowsDisplayScaleSettings displayScaleSettings,
        GuiAudioPanelsRuntimeCapture audioPanels,
        GuiLibraryDeletionRuntimeCapture libraryDeletion,
        GuiChartValidationRuntimeCapture? chartValidation = null,
        bool evidenceExportRevealSuppressed = false,
        bool evidenceInteractionFollowsActiveTab = false,
        bool evidenceFilePickerStartDirectoryInjected = false,
        string? evidenceFilePickerStartDirectory = null)
        => CaptureCore(
            window,
            timeline,
            audioTransport,
            audioVisualization,
            tab,
            sourcePath,
            workloadKind,
            syntheticNoteCount,
            probeDuration,
            warmupTicks,
            probeTicks,
            targetRenderSamples,
            keySoundRuntime,
            keySoundSettings,
            displayScaleController,
            displayScaleSettings,
            audioPanels,
            libraryDeletion,
            chartValidation,
            evidenceExportRevealSuppressed,
            evidenceInteractionFollowsActiveTab,
            evidenceFilePickerStartDirectoryInjected,
            evidenceFilePickerStartDirectory);

    private static GuiRuntimeEvidence CaptureCore(
        PlatformWindow window,
        TimelineSurface timeline,
        WindowsAudioTransportViewModel audioTransport,
        WindowsAudioVisualizationViewModel audioVisualization,
        DesktopChartTab tab,
        string sourcePath,
        string workloadKind,
        int? syntheticNoteCount,
        TimeSpan probeDuration,
        int warmupTicks,
        int probeTicks,
        int targetRenderSamples,
        WindowsKeySoundPlaybackRuntime? keySoundRuntime,
        WindowsKeySoundSettings? keySoundSettings,
        WindowsDisplayScaleController? displayScaleController,
        WindowsDisplayScaleSettings? displayScaleSettings,
        GuiAudioPanelsRuntimeCapture? audioPanels,
        GuiLibraryDeletionRuntimeCapture? libraryDeletion,
        GuiChartValidationRuntimeCapture? chartValidation,
        bool evidenceExportRevealSuppressed = false,
        bool evidenceInteractionFollowsActiveTab = false,
        bool evidenceFilePickerStartDirectoryInjected = false,
        string? evidenceFilePickerStartDirectory = null)
    {
        var screen = window.ScreenFromHost();
        var screenBounds = screen?.Bounds ?? default;
        var workArea = screen?.WorkingArea ?? default;
        var systemScaling = screen?.Scaling is > 0 and var liveScaling && double.IsFinite(liveScaling)
            ? liveScaling
            : displayScaleController?.ScreenScaling is > 0 and var capturedScaling &&
              double.IsFinite(capturedScaling)
                ? capturedScaling
                : 1;
        var effectiveScreenWidth = screenBounds.Width > 0
            ? screenBounds.Width / systemScaling
            : displayScaleController?.EffectiveScreenWidth ?? 0;
        var effectiveScreenHeight = screenBounds.Height > 0
            ? screenBounds.Height / systemScaling
            : displayScaleController?.EffectiveScreenHeight ?? 0;
        var effectiveWorkAreaWidth = workArea.Width > 0
            ? workArea.Width / systemScaling
            : displayScaleController?.EffectiveWorkAreaWidth ?? 0;
        var effectiveWorkAreaHeight = workArea.Height > 0
            ? workArea.Height / systemScaling
            : displayScaleController?.EffectiveWorkAreaHeight ?? 0;
        var displayScaleDiagnostics = displayScaleController?.Diagnostics;
        var avaloniaSettingsPath = WindowsDisplayScaleSettingsStore.SettingsPath;
        var wpfViewSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumaChart Studio",
            "view-settings.json");
        var displayScaleEvidence = displayScaleDiagnostics == null || displayScaleSettings == null
            ? null
            : new GuiDisplayScaleEvidence(
                RequestedUiScale: displayScaleDiagnostics.RequestedUiScale,
                RequestedFontScale: displayScaleDiagnostics.RequestedFontScale,
                ActualRootScaleX: displayScaleDiagnostics.ActualRootScaleX,
                ActualRootScaleY: displayScaleDiagnostics.ActualRootScaleY,
                ActualFontScale: displayScaleDiagnostics.ActualFontScale,
                UsesLayoutTransform: displayScaleDiagnostics.UsesLayoutTransform,
                ScreenScaling: displayScaleDiagnostics.ScreenScaling,
                EffectiveScreenWidth: displayScaleDiagnostics.EffectiveScreenWidth,
                EffectiveScreenHeight: displayScaleDiagnostics.EffectiveScreenHeight,
                EffectiveWorkAreaWidth: displayScaleDiagnostics.EffectiveWorkAreaWidth,
                EffectiveWorkAreaHeight: displayScaleDiagnostics.EffectiveWorkAreaHeight,
                WindowMinimumWidth: displayScaleDiagnostics.WindowMinimumWidth,
                WindowMinimumHeight: displayScaleDiagnostics.WindowMinimumHeight,
                DisplayScaleInitialized: displayScaleSettings.DisplayScaleInitialized,
                LocalDataRoot: AvaloniaAppPaths.LocalDataRoot,
                SettingsPath: avaloniaSettingsPath,
                SettingsFileExists: File.Exists(avaloniaSettingsPath),
                WpfViewSettingsPath: wpfViewSettingsPath,
                SettingsIsolatedFromWpf: !PathsEqual(avaloniaSettingsPath, wpfViewSettingsPath),
                FontDiagnostics: new GuiFontScaleDiagnosticsEvidence(
                    WindowFontSize: displayScaleDiagnostics.WindowFontSize,
                    ScannedObjectCount: displayScaleDiagnostics.FontScannedObjectCount,
                    CandidateCount: displayScaleDiagnostics.FontCandidateCount,
                    ScaledElementCount: displayScaleDiagnostics.FontScaledElementCount,
                    InheritedOrDefaultSkippedCount:
                        displayScaleDiagnostics.FontInheritedOrDefaultSkippedCount,
                    CapturedBaselineCount: displayScaleDiagnostics.FontCapturedBaselineCount));
        var performance = timeline.Performance.Snapshot();
        var audioSnapshot = audioTransport.PlaybackSnapshot;
        var visualization = audioVisualization.Visualization;
        var audioBackground = timeline.GetAudioBackgroundDiagnostics();
        var audioPanelsEvidence = audioPanels == null
            ? null
            : BuildAudioPanelsEvidence(
                window,
                audioPanels,
                audioBackground,
                wpfViewSettingsPath);
        var libraryEvidence = libraryDeletion == null
            ? null
            : BuildLibraryEvidence(libraryDeletion);
        var chartValidationEvidence = chartValidation?.Presentation == null
            ? null
            : BuildChartValidationEvidence(timeline, chartValidation);
        var editorViewSettingsPath = WindowsEditorViewSettingsStore.SettingsPath;
        var editorViewSettings = WindowsEditorViewSettingsStore.Load();
        var settingsEvidence = new GuiSettingsEvidence(
            SettingsPath: editorViewSettingsPath,
            SettingsFileExists: File.Exists(editorViewSettingsPath),
            WpfViewSettingsPath: wpfViewSettingsPath,
            SettingsIsolatedFromWpf: !PathsEqual(editorViewSettingsPath, wpfViewSettingsPath),
            GridLaneWidth: editorViewSettings.GridLaneWidth,
            GridBeatHeight: editorViewSettings.GridBeatHeight,
            MainEditorHorizontalOffset: editorViewSettings.MainEditorHorizontalOffset,
            NoteWidth: editorViewSettings.NoteWidth,
            NoteHeight: editorViewSettings.NoteHeight,
            NoteOpacity: editorViewSettings.NoteOpacity,
            JudgementLinePosition: editorViewSettings.JudgementLinePosition,
            JudgementLineThickness: editorViewSettings.JudgementLineThickness,
            JudgementLineOpacity: editorViewSettings.JudgementLineOpacity,
            TimelineMatchesPersistedView:
                Equal(timeline.GridLaneWidth, editorViewSettings.GridLaneWidth) &&
                Equal(timeline.PixelsPerBeat, editorViewSettings.GridBeatHeight) &&
                Equal(timeline.MainTrackOffset, editorViewSettings.MainEditorHorizontalOffset) &&
                Equal(timeline.NoteWidth, editorViewSettings.NoteWidth) &&
                Equal(timeline.NoteHeight, editorViewSettings.NoteHeight) &&
                Equal(timeline.NoteOpacity, editorViewSettings.NoteOpacity) &&
                Equal(timeline.JudgementLinePosition, editorViewSettings.JudgementLinePosition) &&
                Equal(timeline.JudgementLineThickness, editorViewSettings.JudgementLineThickness) &&
                Equal(timeline.JudgementLineOpacity, editorViewSettings.JudgementLineOpacity),
            AutoBackupEnabled: editorViewSettings.AutoBackupEnabled,
            AutoBackupMinutes: editorViewSettings.AutoBackupMinutes,
            FrameRateLimit: editorViewSettings.FrameRateLimit,
            BackupRoot: AvaloniaAppPaths.EditorBackupRoot,
            BackupFileCount: CountFiles(AvaloniaAppPaths.EditorBackupRoot, "*.mcbackup"),
            DesktopDocumentCacheRoot: AvaloniaAppPaths.DesktopDocumentCacheRoot,
            OpenTabCachePaths: [Path.GetFullPath(tab.CacheDirectory)],
            OnlineRoomResourceCacheRoot: AvaloniaAppPaths.OnlineRoomResourceCacheRoot);
        var shortcutSettingsPath = WindowsEditorShortcutSettingsStore.SettingsPath;
        var shortcutSettings = WindowsEditorShortcutSettingsStore.Load();
        var wpfShortcutSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumaChart Studio",
            "shortcuts.json");
        var shortcutBindings = WindowsEditorShortcutSettings.Catalog.ToDictionary(
            descriptor => descriptor.Id,
            descriptor => shortcutSettings.Bindings.GetValueOrDefault(descriptor.Id) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        var shortcutEvidence = new GuiShortcutEvidence(
            SettingsPath: shortcutSettingsPath,
            SettingsFileExists: File.Exists(shortcutSettingsPath),
            WpfSettingsPath: wpfShortcutSettingsPath,
            SettingsIsolatedFromWpf: !PathsEqual(shortcutSettingsPath, wpfShortcutSettingsPath),
            CatalogCount: WindowsEditorShortcutSettings.Catalog.Count,
            AssignedCount: shortcutBindings.Count(pair => !string.IsNullOrWhiteSpace(pair.Value)),
            UnassignedCount: shortcutBindings.Count(pair => string.IsNullOrWhiteSpace(pair.Value)),
            TextEditingSuppressedActionCount:
                WindowsEditorShortcutSettings.Catalog.Count(item => item.SuppressWhenEditing),
            DuplicateGestureCount: shortcutBindings
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .Count(group => group.Count() > 1),
            AllCatalogActionsRoutable: WindowsEditorShortcutSettings.Catalog.All(descriptor =>
            {
                try
                {
                    _ = WindowsEditorShortcutRouting.Resolve(descriptor.Id);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }),
            Bindings: shortcutBindings);
        var holdTailTarget = timeline.GetSelectedHoldTailEvidenceTarget();
        var holdTailWindowPoint = holdTailTarget == null
            ? null
            : timeline.TranslatePoint(holdTailTarget.SurfacePoint, window);
        var placementProbe = syntheticNoteCount.HasValue
            ? null
            : timeline.GetPlacementEvidenceProbe();
        var proEditorProbe = syntheticNoteCount.HasValue
            ? null
            : timeline.GetProEditorEvidenceProbe();
        var freeTapTarget = placementProbe?.TapTarget ?? (syntheticNoteCount.HasValue
            ? null
            : timeline.GetFreeTapEvidenceTarget());
        var freeTapWindowPoint = freeTapTarget == null
            ? null
            : timeline.TranslatePoint(freeTapTarget.SurfacePoint, window);
        var hiddenGroups = tab.HiddenGroups.OrderBy(group => group).ToArray();
        var visibleDocumentNoteCount = tab.Session.Document.Notes.Count(note =>
            (timeline.ProEditorEnabled || !note.IsDraft) &&
            !tab.HiddenGroups.Contains(note.Group));
        var visibleSelectedNoteCount = tab.Session.Document.Notes.Count(note =>
            tab.Session.Selection.Contains(note.Id) &&
            (timeline.ProEditorEnabled || !note.IsDraft) &&
            !tab.HiddenGroups.Contains(note.Group));
        var visibleEditableSelectionCount = tab.Session.Document.Notes.Count(note =>
            tab.Session.Selection.Contains(note.Id) &&
            (timeline.ProEditorEnabled || !note.IsDraft) &&
            !tab.HiddenGroups.Contains(note.Group) &&
            (tab.Session.AllowOwnerProtectedEdits || !tab.Session.IsOwnerProtected(note)));
        var selectedNotes = tab.Session.Document.Notes
            .Where(note => tab.Session.Selection.Contains(note.Id))
            .OrderBy(note => note.Beat)
            .ThenBy(note => note.Lane)
            .ThenBy(note => note.Id)
            .Take(64)
            .Select(NoteEvidence)
            .ToArray();
        var proEditorRange = timeline.GetWorkspaceColumnRange();
        var configuredProEditorRange = ProEditorWorkspaceGeometry.ResolveColumnRange(
            tab.Session.Document.KeyCount,
            timeline.ProGridColumnCount);
        var proEditorPan = timeline.GetTrackPanState();
        var proEditorGeometry = timeline.GetProEditorGeometryDiagnostics();
        var officialLeftWindowPoint = timeline.TranslatePoint(
            new Point(proEditorGeometry.OfficialTrackLeftDip, 0),
            window);
        var officialRightWindowPoint = timeline.TranslatePoint(
            new Point(proEditorGeometry.OfficialTrackRightDip, 0),
            window);
        var timelineLeftWindowPoint = timeline.TranslatePoint(new Point(0, 0), window);
        var timelineRightWindowPoint = timeline.TranslatePoint(new Point(timeline.Bounds.Width, 0), window);
        var timelineBottomWindowPoint = timeline.TranslatePoint(
            new Point(0, timeline.Bounds.Height),
            window);
        var judgmentLineWindowPoint = timeline.TranslatePoint(
            new Point(0, timeline.Bounds.Height * timeline.JudgementLinePosition),
            window);
        var proEditorNotes = tab.Session.Document.Notes
            .OrderBy(note => note.Beat)
            .ThenBy(ProEditorWorkspaceGeometry.GetColumn)
            .ThenBy(note => note.Id)
            .Take(64)
            .Select(NoteEvidence)
            .ToArray();
        var groupRows = tab.Session.Document.Notes
            .GroupBy(note => note.Group)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var groupManagerRows = groupRows.Keys
            .Append(0)
            .Append(tab.ActivePlacementGroup)
            .Distinct()
            .OrderBy(group => group)
            .Select(group =>
            {
                var notes = groupRows.GetValueOrDefault(group) ?? [];
                var dominantSound = notes
                    .Where(note => !string.IsNullOrWhiteSpace(note.Sound))
                    .GroupBy(note => note.Sound!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(soundGroup => soundGroup.Count())
                    .Select(soundGroup => Path.GetFileName(soundGroup.Key))
                    .FirstOrDefault();
                return new GuiGroupManagerRowEvidence(
                    group,
                    notes.Length,
                    dominantSound,
                    !tab.HiddenGroups.Contains(group),
                    group == tab.ActivePlacementGroup);
            })
            .ToArray();
        var placementProbeEvidence = placementProbe == null
            ? null
            : new GuiPlacementProbeEvidence(
                PlacementTargetEvidence(timeline, window, placementProbe.TapTarget),
                PlacementTargetEvidence(timeline, window, placementProbe.HoldStartTarget),
                PlacementTargetEvidence(timeline, window, placementProbe.HoldEndTarget),
                FindPlacementObservation(
                    tab,
                    placementProbe.TapTarget,
                    expectedEndBeat: null,
                    requireTap: true),
                FindPlacementObservation(
                    tab,
                    placementProbe.HoldStartTarget,
                    placementProbe.HoldEndTarget.Beat,
                     requireTap: false));
        var proEditorProbeEvidence = proEditorProbe == null
            ? null
            : new GuiProEditorProbeEvidence(
                ProEditorTargetEvidence(timeline, window, proEditorProbe.DraftTapTarget),
                ProEditorTargetEvidence(timeline, window, proEditorProbe.OfficialMoveTarget),
                ProEditorTargetEvidence(timeline, window, proEditorProbe.DraftHoldStartTarget),
                ProEditorTargetEvidence(timeline, window, proEditorProbe.DraftHoldEndTarget),
                FindProEditorObservation(
                    tab,
                    proEditorProbe.OfficialMoveTarget,
                    expectedEndBeat: null,
                    requireTap: true),
                FindProEditorObservation(
                    tab,
                    proEditorProbe.DraftHoldStartTarget,
                    proEditorProbe.DraftHoldEndTarget.Beat,
                    requireTap: false));
        var keySoundEvidence = keySoundRuntime == null || keySoundSettings == null
            ? null
            : BuildKeySoundEvidence(
                tab,
                sourcePath,
                hiddenGroups,
                audioSnapshot,
                keySoundRuntime,
                keySoundSettings);
        return new GuiRuntimeEvidence(
            Schema: "lumachart.windows.avalonia.gui-evidence.v14",
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            PlatformScope: "Windows-only",
            ProcessArchitecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            OperatingSystem: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Framework: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            AvaloniaVersion: typeof(global::Avalonia.Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            RenderingPath: "Avalonia Win32 + Skia DrawingContext; one TimelineSurface Control",
            MetricSemantics: new GuiMetricSemantics(
                RenderMilliseconds: "CPU elapsed inside TimelineSurface.Render; not GPU present time",
                FrameIntervalMilliseconds: "Interval between consecutive TimelineSurface.Render entries on the live UI thread; not present-to-present ETW",
                InputToRenderMilliseconds: "Synthetic UI input marker to the next TimelineSurface.Render entry; excludes physical-device and display scan-out latency"),
            Window: new GuiWindowEvidence(
                ClientWidthDip: window.Bounds.Width,
                ClientHeightDip: window.Bounds.Height,
                PositionXPixels: window.Position.X,
                PositionYPixels: window.Position.Y,
                WindowState: window.WindowState.ToString(),
                MinimumWidthDip: window.MinWidth,
                MinimumHeightDip: window.MinHeight,
                ScreenXPixels: screenBounds.X,
                ScreenYPixels: screenBounds.Y,
                ScreenWidthPixels: screenBounds.Width,
                ScreenHeightPixels: screenBounds.Height,
                WorkAreaXPixels: workArea.X,
                WorkAreaYPixels: workArea.Y,
                WorkAreaWidthPixels: workArea.Width,
                WorkAreaHeightPixels: workArea.Height,
                ScreenScaling: systemScaling,
                EffectiveScreenWidthDip: effectiveScreenWidth,
                EffectiveScreenHeightDip: effectiveScreenHeight,
                EffectiveWorkAreaWidthDip: effectiveWorkAreaWidth,
                EffectiveWorkAreaHeightDip: effectiveWorkAreaHeight,
                DisplayName: screen?.DisplayName,
                DisplayScale: displayScaleEvidence),
            Library: libraryEvidence,
            Validation: chartValidationEvidence,
            Settings: settingsEvidence,
            Shortcuts: shortcutEvidence,
            EvidenceExportRevealSuppressed: evidenceExportRevealSuppressed,
            EvidenceInteractionFollowsActiveTab: evidenceInteractionFollowsActiveTab,
            EvidenceFilePickerStartDirectoryInjected: evidenceFilePickerStartDirectoryInjected,
            EvidenceFilePickerStartDirectory:
                evidenceFilePickerStartDirectoryInjected &&
                !string.IsNullOrWhiteSpace(evidenceFilePickerStartDirectory)
                    ? Path.GetFullPath(evidenceFilePickerStartDirectory)
                    : null,
            Chart: new GuiChartEvidence(
                Path.GetFullPath(sourcePath),
                ComputeFileSha256(sourcePath),
                workloadKind,
                syntheticNoteCount,
                ChartStructuralFingerprint.Summarize(tab.Session.Document)),
            Audio: new GuiAudioEvidence(
                UiIntegrated: true,
                State: audioSnapshot?.State.ToString() ?? "Unavailable",
                SourcePath: audioSnapshot?.SourcePath,
                SourceExists: audioSnapshot?.SourcePath is { } audioPath && File.Exists(audioPath),
                PositionMilliseconds: audioSnapshot?.Position.TotalMilliseconds ?? 0,
                DurationMilliseconds: audioSnapshot?.Duration.TotalMilliseconds ?? 0,
                PlaybackRate: audioTransport.PlaybackRate,
                PreservePitch: audioTransport.PreservePitch,
                Volume: audioTransport.Volume,
                Status: audioTransport.Status,
                LastErrorType: audioTransport.LastError?.GetType().FullName,
                Visualization: new GuiAudioVisualizationEvidence(
                    State: audioVisualization.LastError != null
                        ? "Faulted"
                        : visualization != null
                            ? "Ready"
                            : "PendingOrUnavailable",
                    Status: audioVisualization.Status,
                    LastErrorType: audioVisualization.LastError?.GetType().FullName,
                    WaveformCount: visualization?.Waveform?.Count ?? 0,
                    WaveformBucketMilliseconds: visualization?.Waveform?.BucketMilliseconds ?? 0,
                    WaveformDurationMilliseconds: visualization?.Waveform?.DurationMilliseconds ?? 0,
                    SpectrogramFrameCount: visualization?.Spectrogram?.FrameCount ?? 0,
                    SpectrogramFrequencyBands: visualization?.Spectrogram?.FrequencyBands ?? 0,
                    SpectrogramFrameMilliseconds: visualization?.Spectrogram?.FrameMilliseconds ?? 0,
                    SpectrogramDurationMilliseconds: visualization?.Spectrogram?.DurationMilliseconds ?? 0),
                KeySound: keySoundEvidence,
                Panels: audioPanelsEvidence),
            Timeline: new GuiTimelineEvidence(
                SingleCustomControl: true,
                PerNoteAvaloniaControlCount: 0,
                VisibleAreaClipping: timeline.ClipToBounds,
                VisibleRangeIndex: "immutable sorted interval index with subtree maximum-end pruning",
                CurrentBeat: timeline.CurrentBeat,
                SelectedNoteCount: tab.Session.Selection.Count,
                VisibleSelectedNoteCount: visibleSelectedNoteCount,
                VisibleDocumentNoteCount: visibleDocumentNoteCount,
                SelectedTimingCount: timeline.SelectedTimingIds.Count,
                SnapDivision: tab.Session.SnapDivision,
                CurrentTool: timeline.Tool.ToString(),
                ActivePlacementGroup: tab.ActivePlacementGroup,
                HiddenGroups: hiddenGroups,
                GroupManager: new GuiGroupManagerEvidence(
                    tab.ActivePlacementGroup,
                    visibleEditableSelectionCount,
                    groupManagerRows),
                ProEditor: new GuiProEditorEvidence(
                    timeline.ProEditorEnabled,
                    timeline.ProGridColumnCount,
                    new GuiProEditorColumnRangeEvidence(proEditorRange.Minimum, proEditorRange.Maximum),
                    new GuiProEditorColumnRangeEvidence(
                        configuredProEditorRange.Minimum,
                        configuredProEditorRange.Maximum),
                    new GuiProEditorTrackPanEvidence(
                        proEditorPan.Minimum,
                        proEditorPan.Maximum,
                        proEditorPan.Value,
                        proEditorPan.Enabled),
                    new GuiProEditorGeometryEvidence(
                        proEditorGeometry.LaneWidthDip,
                        proEditorGeometry.OfficialTrackLeftDip,
                        proEditorGeometry.OfficialTrackRightDip,
                        officialLeftWindowPoint?.X,
                        officialRightWindowPoint?.X,
                        timelineLeftWindowPoint?.X,
                        timelineRightWindowPoint?.X),
                    tab.Session.Document.Notes.Count(note => !note.IsDraft),
                    tab.Session.Document.Notes.Count(note => note.IsDraft),
                    tab.Session.Document.Notes.Count(note =>
                        !note.IsDraft && !tab.HiddenGroups.Contains(note.Group)),
                    tab.Session.Document.Notes.Count(note =>
                        note.IsDraft && timeline.ProEditorEnabled && !tab.HiddenGroups.Contains(note.Group)),
                    proEditorNotes,
                    proEditorProbeEvidence),
                Geometry: new GuiTimelineGeometryEvidence(
                    timelineLeftWindowPoint?.X,
                    timelineLeftWindowPoint?.Y,
                    timelineRightWindowPoint?.X,
                    timelineBottomWindowPoint?.Y,
                    judgmentLineWindowPoint?.Y,
                    timeline.Bounds.Width,
                    timeline.Bounds.Height),
                SessionChangeSerial: tab.Session.ChangeSerial,
                SessionIsDirty: tab.Session.IsDirty,
                SelectedNotes: selectedNotes,
                SelectedHoldTail: holdTailTarget == null
                    ? null
                    : new GuiHoldTailEvidence(
                        holdTailTarget.NoteId.ToString("D"),
                        holdTailTarget.Lane,
                        BeatEvidence(holdTailTarget.HeadBeat),
                        BeatEvidence(holdTailTarget.EndBeat),
                        holdTailWindowPoint is { } point
                            ? new GuiPointEvidence(point.X, point.Y)
                            : null),
                FreeTapTarget: freeTapTarget == null
                    ? null
                    : new GuiPlacementTargetEvidence(
                        freeTapTarget.Lane,
                        BeatEvidence(freeTapTarget.Beat),
                        freeTapWindowPoint is { } placementPoint
                            ? new GuiPointEvidence(placementPoint.X, placementPoint.Y)
                            : null),
                PlacementProbe: placementProbeEvidence,
                AudioBackground: audioBackground,
                PixelsPerBeat: timeline.PixelsPerBeat,
                ProbeDurationMilliseconds: probeDuration.TotalMilliseconds,
                WarmupTicks: warmupTicks,
                ForcedFullGcBeforeSampling: true,
                ProbeTicks: probeTicks,
                TargetRenderSamples: targetRenderSamples,
                TargetReached: performance.RenderMilliseconds.Samples >= targetRenderSamples,
                Performance: performance));
    }

    private static GuiAudioPanelsEvidence BuildAudioPanelsEvidence(
        PlatformWindow window,
        GuiAudioPanelsRuntimeCapture runtime,
        TimelineAudioBackgroundDiagnostics audioBackground,
        string wpfViewSettingsPath)
    {
        var settingsPath = WindowsAudioPanelSettingsStore.SettingsPath;
        return new GuiAudioPanelsEvidence(
            SettingsPath: settingsPath,
            SettingsFileExists: File.Exists(settingsPath),
            WpfViewSettingsPath: wpfViewSettingsPath,
            SettingsIsolatedFromWpf: !PathsEqual(settingsPath, wpfViewSettingsPath),
            SideCustomSurfaceCount: 2,
            PerSampleAvaloniaControlCount: 0,
            VisibleAreaClipping:
                runtime.WaveformSurface.ClipToBounds &&
                runtime.SpectrogramSurface.ClipToBounds,
            Waveform: BuildAudioPanelEvidence(
                window,
                WindowsAudioPanelKind.Waveform,
                runtime.Settings.Waveform,
                runtime.WaveformPanel,
                runtime.WaveformSurface,
                audioBackground.WaveformVisible),
            Spectrogram: BuildAudioPanelEvidence(
                window,
                WindowsAudioPanelKind.Spectrogram,
                runtime.Settings.Spectrogram,
                runtime.SpectrogramPanel,
                runtime.SpectrogramSurface,
                audioBackground.SpectrogramVisible));
    }

    private static GuiAudioPanelEvidence BuildAudioPanelEvidence(
        PlatformWindow window,
        WindowsAudioPanelKind kind,
        WindowsAudioPanelState state,
        Control panel,
        AudioVisualizationSideSurface surface,
        bool backgroundEnabled)
    {
        var windowPoint = panel.TranslatePoint(default, window);
        var windowBottomRight = panel.TranslatePoint(
            new Point(panel.Bounds.Width, panel.Bounds.Height),
            window);
        var actualWidth = windowPoint is { } topLeft && windowBottomRight is { } bottomRight
            ? Math.Abs(bottomRight.X - topLeft.X)
            : panel.Bounds.Width;
        var actualHeight = windowPoint is { } actualTopLeft && windowBottomRight is { } actualBottomRight
            ? Math.Abs(actualBottomRight.Y - actualTopLeft.Y)
            : panel.Bounds.Height;
        return new GuiAudioPanelEvidence(
            Kind: kind.ToString(),
            Mode: state.Mode.ToString(),
            Collapsed: state.Collapsed,
            Docked: state.Docked,
            RememberedXDip: state.X,
            RememberedYDip: state.Y,
            RememberedWidthDip: state.Width,
            ActualWindowXDip: windowPoint?.X,
            ActualWindowYDip: windowPoint?.Y,
            ActualWidthDip: actualWidth,
            ActualHeightDip: actualHeight,
            SurfaceVisible: surface.IsVisible,
            BackgroundEnabled: backgroundEnabled,
            SurfaceClipToBounds: surface.ClipToBounds,
            Diagnostics: surface.GetDiagnostics());
    }

    private static GuiLibraryEvidence BuildLibraryEvidence(
        GuiLibraryDeletionRuntimeCapture runtime)
    {
        var popup = runtime.PopupScaleDiagnostics;
        var dialog = runtime.DialogScaleDiagnostics;
        var plan = runtime.LastPlan;
        return new GuiLibraryEvidence(
            WorkspaceRoot: Path.GetFullPath(runtime.WorkspaceRoot),
            SelectionMode: runtime.SelectionMode,
            IndexedFolderCount: runtime.IndexedFolderCount,
            VisibleFolderCount: runtime.VisibleFolderCount,
            SelectedFolderCount: runtime.SelectedFolderCount,
            DeleteButtonEnabled: runtime.DeleteButtonEnabled,
            DeleteButtonContent: runtime.DeleteButtonContent,
            DeletionInProgress: runtime.DeletionInProgress,
            ContextMenuScale: popup == null
                ? null
                : new GuiPopupScaleEvidence(
                    popup.UiScale,
                    popup.FontScale,
                    popup.BaseFontSize,
                    popup.AppliedFontSize,
                    popup.PopupRootFound,
                    popup.PopupRootScaleX,
                    popup.PopupRootScaleY,
                    popup.PopupRootWidthDip,
                    popup.PopupRootHeightDip),
            LastDeletion: plan == null
                ? null
                : new GuiLibraryDeletionEvidence(
                    CanExecute: plan.CanExecute,
                    BlockReason: plan.BlockReason.ToString(),
                    BlockMessage: plan.BlockMessage,
                    FolderCount: plan.FolderCount,
                    ChartCount: plan.ChartCount,
                    TargetFullPaths: plan.Targets.Select(target => target.FullPath).ToArray(),
                    BlockingDocumentTitles: plan.BlockingDocumentTitles,
                    PreviewItems: plan.PreviewItems,
                    OmittedPreviewCount: plan.OmittedPreviewCount,
                    ConfirmationMessage: plan.ConfirmationMessage,
                    Confirmed: runtime.Confirmed,
                    Canceled: runtime.Canceled,
                    DialogScale: dialog == null
                        ? null
                        : new GuiLibraryDeleteDialogScaleEvidence(
                            dialog.RequestedUiScale,
                            dialog.RequestedFontScale,
                            dialog.ActualRootScaleX,
                            dialog.ActualRootScaleY,
                            dialog.MessageFontSize,
                            dialog.UsesLayoutTransform),
                    RecycleResults: runtime.RecycleResults.Select(result =>
                            new GuiWindowsRecycleResultEvidence(
                                result.FullPath,
                                result.ShellResult,
                                result.AnyOperationsAborted,
                                result.AllowUndoRequested))
                        .ToArray(),
                    FailureMessages: runtime.FailureMessages));
    }

    private static GuiChartValidationEvidence BuildChartValidationEvidence(
        TimelineSurface timeline,
        GuiChartValidationRuntimeCapture runtime)
    {
        var presentation = runtime.Presentation
                           ?? throw new InvalidOperationException(
                               "Chart validation evidence requires a presentation.");
        var presentationIssues = presentation.Issues.Select(ChartIssueEvidence).ToArray();
        var timelineIssues = timeline.ChartIssues.Select(ChartIssueEvidence).ToArray();
        var presentationNavigation = presentation.Navigation == null
            ? null
            : ChartIssueNavigationEvidence(presentation.Navigation);
        var lastNavigation = runtime.LastNavigation == null
            ? null
            : ChartIssueNavigationEvidence(runtime.LastNavigation);

        return new GuiChartValidationEvidence(
            Issues: presentationIssues,
            TotalIssueCount: presentation.TotalIssueCount,
            OverlappingNoteCount: presentation.OverlappingNoteCount,
            CrossingLongNoteCount: presentation.CrossingLongNoteCount,
            CanSave: presentation.CanSave,
            FirstIssue: presentation.FirstIssue == null
                ? null
                : ChartIssueEvidence(presentation.FirstIssue),
            Navigation: lastNavigation ?? presentationNavigation,
            PresentationNavigation: presentationNavigation,
            LastNavigation: lastNavigation,
            LastNavigationMatchesPresentation:
                NavigationPlansEqual(runtime.LastNavigation, presentation.Navigation),
            ManualCheckMessage: presentation.ManualCheckMessage,
            SaveBlockingMessage: presentation.SaveBlockingMessage,
            Timeline: new GuiChartValidationTimelineCrossCheckEvidence(
                Issues: timelineIssues,
                TotalIssueCount: timeline.ChartIssues.Count,
                OverlappingNoteCount: timeline.ChartIssues.Count(
                    issue => issue.Kind == ChartIssueKind.OverlappingNotes),
                CrossingLongNoteCount: timeline.ChartIssues.Count(
                    issue => issue.Kind == ChartIssueKind.CrossingLongNotes),
                MatchesPresentation: IssueListsEqual(
                    presentation.Issues,
                    timeline.ChartIssues)),
            ManualCheckShown: runtime.ManualCheckShown,
            SaveBlockedShown: runtime.SaveBlockedShown,
            LastDialogMode: runtime.LastDialogMode,
            ManualDialogScale: runtime.ManualDialogScale == null
                ? null
                : ChartValidationDialogScaleEvidence(runtime.ManualDialogScale),
            SaveDialogScale: runtime.SaveDialogScale == null
                ? null
                : ChartValidationDialogScaleEvidence(runtime.SaveDialogScale));
    }

    private static GuiChartIssueEvidence ChartIssueEvidence(ChartIssue issue) => new(
        Kind: issue.Kind.ToString(),
        Beat: BeatEvidence(issue.Beat),
        Lane: issue.Lane,
        NoteIds: issue.NoteIds.Select(noteId => noteId.ToString("D")).ToArray(),
        Message: issue.Message);

    private static GuiChartIssueNavigationEvidence ChartIssueNavigationEvidence(
        DesktopChartIssueNavigationPlan navigation) => new(
        ExactBeat: BeatEvidence(navigation.ExactBeat),
        NoteIds: navigation.NoteIds.Select(noteId => noteId.ToString("D")).ToArray(),
        HiddenGroupsToReveal: navigation.HiddenGroupsToReveal,
        InspectorTabIndex: navigation.InspectorTabIndex,
        HoverStatusText: navigation.HoverStatusText);

    private static GuiChartValidationDialogScaleEvidence ChartValidationDialogScaleEvidence(
        ChartValidationDialogScaleDiagnostics diagnostics) => new(
        RequestedUiScale: diagnostics.RequestedUiScale,
        RequestedFontScale: diagnostics.RequestedFontScale,
        ActualRootScaleX: diagnostics.ActualRootScaleX,
        ActualRootScaleY: diagnostics.ActualRootScaleY,
        MessageFontSize: diagnostics.MessageFontSize,
        UsesLayoutTransform: diagnostics.UsesLayoutTransform);

    private static bool IssueListsEqual(
        IReadOnlyList<ChartIssue> left,
        IReadOnlyList<ChartIssue> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => ChartIssuesEqual(pair.First, pair.Second));

    private static bool ChartIssuesEqual(ChartIssue left, ChartIssue right) =>
        left.Kind == right.Kind &&
        HasExactExpression(left.Beat, right.Beat) &&
        left.Lane == right.Lane &&
        left.NoteIds.SequenceEqual(right.NoteIds) &&
        string.Equals(left.Message, right.Message, StringComparison.Ordinal);

    private static bool NavigationPlansEqual(
        DesktopChartIssueNavigationPlan? left,
        DesktopChartIssueNavigationPlan? right) =>
        left == null || right == null
            ? left == null && right == null
            : HasExactExpression(left.ExactBeat, right.ExactBeat) &&
              left.NoteIds.SequenceEqual(right.NoteIds) &&
              left.HiddenGroupsToReveal.SequenceEqual(right.HiddenGroupsToReveal) &&
              left.InspectorTabIndex == right.InspectorTabIndex &&
              string.Equals(left.HoverStatusText, right.HoverStatusText, StringComparison.Ordinal);

    private static GuiKeySoundEvidence BuildKeySoundEvidence(
        DesktopChartTab tab,
        string sourcePath,
        IReadOnlyList<int> hiddenGroups,
        WindowsAudioPlaybackSnapshot? audioSnapshot,
        WindowsKeySoundPlaybackRuntime runtime,
        WindowsKeySoundSettings settings)
    {
        var runtimeSnapshot = runtime.Snapshot;
        var counters = runtimeSnapshot.Counters;
        var chartOffset = settings.GetChartOffset(sourcePath);
        var options = new WindowsKeySoundPlaybackOptions(
            settings.EnableKeySounds,
            settings.Preset,
            settings.CustomFilePath,
            settings.PlayLongNoteEndSound,
            settings.GlobalOffsetMilliseconds,
            chartOffset);
        var settingsEvidence = new GuiKeySoundSettingsEvidence(
            settings.EnableKeySounds,
            settings.Preset.ToString(),
            settings.Volume,
            settings.CustomFilePath,
            settings.CustomFilePath is { } customPath && File.Exists(customPath),
            settings.PlayLongNoteEndSound,
            settings.GlobalOffsetMilliseconds,
            chartOffset,
            options.EffectiveOffsetMilliseconds);

        GuiKeySoundPlanEvidence planEvidence;
        try
        {
            // GUI evidence is a cold-path capture. Rebuilding the pure plan here
            // independently proves the runtime's published counts against the
            // active document, hidden-group snapshot, source path and settings.
            var expectedPlan = new WindowsKeySoundPlaybackCoordinator(
                tab.Session.Document,
                sourcePath,
                new HashSet<int>(hiddenGroups),
                options);
            var eventCount = expectedPlan.Events.Length;
            var headEventCount = expectedPlan.Events.Count(item => !item.IsLongNoteEnd);
            var longNoteTailEventCount = expectedPlan.Events.Count(item => item.IsLongNoteEnd);
            var customSampleEventCount = expectedPlan.Events.Count(
                item => !string.IsNullOrWhiteSpace(item.CustomFilePath));
            var presetFallbackEventCount = expectedPlan.Events.Count(
                item => string.IsNullOrWhiteSpace(item.CustomFilePath));
            var preloadCount = expectedPlan.PreloadPaths.Length;
            var requestedTabMatches = runtimeSnapshot.RequestedTabId == tab.Id;
            var plannedTabMatches = runtimeSnapshot.PlannedTabId == tab.Id;
            var eventCountMatches = runtimeSnapshot.HasPlan &&
                                    plannedTabMatches &&
                                    runtimeSnapshot.PlannedEventCount == eventCount;
            var preloadCountMatches = runtimeSnapshot.HasPlan &&
                                      plannedTabMatches &&
                                      runtimeSnapshot.PlannedPreloadCount == preloadCount;
            var enabledMatches = runtimeSnapshot.HasPlan &&
                                 plannedTabMatches &&
                                 runtimeSnapshot.IsEnabled == options.Enabled;
            planEvidence = new GuiKeySoundPlanEvidence(
                Path.GetFullPath(sourcePath),
                tab.Session.ChangeSerial,
                hiddenGroups,
                eventCount,
                headEventCount,
                longNoteTailEventCount,
                customSampleEventCount,
                presetFallbackEventCount,
                preloadCount,
                runtimeSnapshot.PlannedEventCount,
                runtimeSnapshot.PlannedPreloadCount,
                requestedTabMatches,
                plannedTabMatches,
                eventCountMatches,
                preloadCountMatches,
                enabledMatches,
                runtimeSnapshot.HasPlan &&
                requestedTabMatches &&
                plannedTabMatches &&
                eventCountMatches &&
                preloadCountMatches &&
                enabledMatches,
                ExpectedPlanErrorType: null);
        }
        catch (Exception exception)
        {
            planEvidence = new GuiKeySoundPlanEvidence(
                Path.GetFullPath(sourcePath),
                tab.Session.ChangeSerial,
                hiddenGroups,
                EventCount: null,
                HeadEventCount: null,
                LongNoteTailEventCount: null,
                CustomSampleEventCount: null,
                PresetFallbackEventCount: null,
                PreloadCount: null,
                runtimeSnapshot.PlannedEventCount,
                runtimeSnapshot.PlannedPreloadCount,
                runtimeSnapshot.RequestedTabId == tab.Id,
                runtimeSnapshot.PlannedTabId == tab.Id,
                RuntimeEventCountMatches: false,
                RuntimePreloadCountMatches: false,
                RuntimeEnabledMatches: false,
                RuntimePlanMatchesExpected: false,
                exception.GetType().FullName);
        }

        // Interaction evidence is intentionally captured after Stop. Preserve
        // the output path actually exercised instead of collapsing that proof
        // back to the current idle transport state.
        var transportMode = counters.SharedAccepted > 0
            ? "shared-music-clock-observed"
            : counters.StandaloneAccepted > 0
                ? "silent-editor-clock-observed"
                : audioSnapshot?.IsPlaying == true
                    ? "shared-music-clock-current"
                    : runtimeSnapshot.IsSilentPlaying
                        ? "silent-editor-clock-current"
                        : "idle-not-yet-observed";
        return new GuiKeySoundEvidence(
            UiIntegrated: true,
            Settings: settingsEvidence,
            Plan: planEvidence,
            Scheduler: new GuiKeySoundSchedulerEvidence(
                runtimeSnapshot.RequestedTabId?.ToString("D"),
                runtimeSnapshot.PlannedTabId?.ToString("D"),
                runtimeSnapshot.PlanGeneration,
                runtimeSnapshot.IsPreparing,
                runtimeSnapshot.HasPlan,
                runtimeSnapshot.IsEnabled,
                runtimeSnapshot.PositionMilliseconds,
                runtimeSnapshot.PlaybackRate,
                counters.PlanRequests,
                counters.PlansPublished,
                counters.PlansSuperseded,
                counters.PlanFailures,
                counters.PreloadAttempts,
                counters.PreloadAccepted,
                counters.PreloadRejected,
                counters.PumpCalls,
                counters.ClockSamplesPublished,
                counters.ClockSamplesReplaced,
                counters.ClockSamplesProcessed,
                counters.TriggersDue,
                counters.TriggersAccepted,
                counters.TriggersRejected,
                counters.SubmittedVoices,
                counters.CursorResets,
                counters.PlaybackInvalidations),
            Output: new GuiKeySoundOutputEvidence(
                transportMode,
                runtimeSnapshot.OwnsKeySoundService,
                counters.SharedAccepted,
                counters.SharedRejected,
                counters.StandaloneAccepted,
                counters.StandaloneRejected,
                counters.PreviewAccepted,
                counters.PreviewRejected),
            Status: runtimeSnapshot.Status,
            LastErrorType: runtimeSnapshot.LastError?.GetType().FullName,
            ValidationLevel: "scheduler-and-output-submission",
            AudibleOrLoopbackValidated: false);
    }

    private static GuiPlacementTargetEvidence PlacementTargetEvidence(
        TimelineSurface timeline,
        PlatformWindow window,
        TimelinePlacementEvidenceTarget target)
    {
        var windowPoint = timeline.TranslatePoint(target.SurfacePoint, window);
        return new GuiPlacementTargetEvidence(
            target.Lane,
            BeatEvidence(target.Beat),
            windowPoint is { } point ? new GuiPointEvidence(point.X, point.Y) : null);
    }

    private static GuiProEditorTargetEvidence ProEditorTargetEvidence(
        TimelineSurface timeline,
        PlatformWindow window,
        TimelinePlacementEvidenceTarget target)
    {
        var windowPoint = timeline.TranslatePoint(target.SurfacePoint, window);
        var keyCount = timeline.Session?.Document.KeyCount ?? 0;
        return new GuiProEditorTargetEvidence(
            target.Lane,
            !ProEditorWorkspaceGeometry.IsOfficialColumn(target.Lane, keyCount),
            BeatEvidence(target.Beat),
            windowPoint is { } point ? new GuiPointEvidence(point.X, point.Y) : null);
    }

    private static GuiSelectedNoteEvidence? FindPlacementObservation(
        DesktopChartTab tab,
        TimelinePlacementEvidenceTarget headTarget,
        LumaChart.Core.Models.BeatPosition? expectedEndBeat,
        bool requireTap)
    {
        var note = tab.Session.Document.Notes
            .Where(candidate =>
                !candidate.IsDraft &&
                candidate.Lane == headTarget.Lane &&
                HasExactExpression(candidate.Beat, headTarget.Beat) &&
                (requireTap
                    ? !candidate.EndBeat.HasValue
                    : candidate.EndBeat.HasValue &&
                      expectedEndBeat.HasValue &&
                      HasExactExpression(candidate.EndBeat.Value, expectedEndBeat.Value)))
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault();
        return note == null
            ? null
            : NoteEvidence(note);
    }

    private static GuiSelectedNoteEvidence? FindProEditorObservation(
        DesktopChartTab tab,
        TimelinePlacementEvidenceTarget headTarget,
        BeatPosition? expectedEndBeat,
        bool requireTap)
    {
        var note = tab.Session.Document.Notes
            .Where(candidate =>
                ProEditorWorkspaceGeometry.GetColumn(candidate) == headTarget.Lane &&
                HasExactExpression(candidate.Beat, headTarget.Beat) &&
                (requireTap
                    ? !candidate.EndBeat.HasValue
                    : candidate.EndBeat.HasValue && expectedEndBeat.HasValue &&
                      HasExactExpression(candidate.EndBeat.Value, expectedEndBeat.Value)))
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault();
        return note == null ? null : NoteEvidence(note);
    }

    private static GuiSelectedNoteEvidence NoteEvidence(ChartNote note) => new(
        note.Id.ToString("D"),
        note.Lane,
        BeatEvidence(note.Beat),
        note.EndBeat.HasValue ? BeatEvidence(note.EndBeat.Value) : null,
        note.Volume,
        note.Sound,
        note.Group,
        note.IsDraft,
        note.IsDraft ? note.DraftColumn : null,
        ProEditorWorkspaceGeometry.GetColumn(note));

    private static bool HasExactExpression(
        LumaChart.Core.Models.BeatPosition left,
        LumaChart.Core.Models.BeatPosition right) =>
        left.Bar == right.Bar &&
        left.Numerator == right.Numerator &&
        left.Denominator == right.Denominator;

    private static GuiBeatEvidence BeatEvidence(LumaChart.Core.Models.BeatPosition beat) =>
        new(beat.Bar, beat.Numerator, beat.Denominator, beat.Value);

    public static async Task WriteAtomicAsync(
        string outputPath,
        GuiRuntimeEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(outputPath)
                        ?? throw new InvalidOperationException("Evidence output has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, evidence, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int CountFiles(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).Count()
                : 0;
        }
        catch
        {
            return -1;
        }
    }

    private static bool Equal(double left, double right) => Math.Abs(left - right) < 1e-9;

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}

internal sealed record GuiAudioPanelsRuntimeCapture(
    WindowsAudioPanelSettings Settings,
    Control WaveformPanel,
    AudioVisualizationSideSurface WaveformSurface,
    Control SpectrogramPanel,
    AudioVisualizationSideSurface SpectrogramSurface);

internal sealed record GuiLibraryDeletionRuntimeCapture(
    string WorkspaceRoot,
    string SelectionMode,
    int IndexedFolderCount,
    int VisibleFolderCount,
    int SelectedFolderCount,
    bool DeleteButtonEnabled,
    string DeleteButtonContent,
    bool DeletionInProgress,
    AvaloniaPopupScaleDiagnostics? PopupScaleDiagnostics,
    DesktopLibraryDeletionPlan? LastPlan,
    LibraryDeleteDialogScaleDiagnostics? DialogScaleDiagnostics,
    IReadOnlyList<WindowsRecycleBinOperationResult> RecycleResults,
    IReadOnlyList<string> FailureMessages,
    bool Confirmed,
    bool Canceled);

internal sealed record GuiChartValidationRuntimeCapture(
    DesktopChartValidationPresentation? Presentation,
    DesktopChartIssueNavigationPlan? LastNavigation,
    bool ManualCheckShown,
    bool SaveBlockedShown,
    ChartValidationDialogScaleDiagnostics? ManualDialogScale,
    ChartValidationDialogScaleDiagnostics? SaveDialogScale,
    string? LastDialogMode);

public sealed record GuiRuntimeEvidence(
    string Schema,
    DateTimeOffset GeneratedAtUtc,
    string PlatformScope,
    string ProcessArchitecture,
    string OperatingSystem,
    string Framework,
    string AvaloniaVersion,
    string RenderingPath,
    GuiMetricSemantics MetricSemantics,
    GuiWindowEvidence Window,
    GuiLibraryEvidence? Library,
    GuiChartValidationEvidence? Validation,
    GuiSettingsEvidence? Settings,
    GuiShortcutEvidence Shortcuts,
    bool EvidenceExportRevealSuppressed,
    bool EvidenceInteractionFollowsActiveTab,
    bool EvidenceFilePickerStartDirectoryInjected,
    string? EvidenceFilePickerStartDirectory,
    GuiChartEvidence Chart,
    GuiAudioEvidence Audio,
    GuiTimelineEvidence Timeline);

public sealed record GuiSettingsEvidence(
    string SettingsPath,
    bool SettingsFileExists,
    string WpfViewSettingsPath,
    bool SettingsIsolatedFromWpf,
    double GridLaneWidth,
    double GridBeatHeight,
    double MainEditorHorizontalOffset,
    double NoteWidth,
    double NoteHeight,
    double NoteOpacity,
    double JudgementLinePosition,
    double JudgementLineThickness,
    double JudgementLineOpacity,
    bool TimelineMatchesPersistedView,
    bool AutoBackupEnabled,
    int AutoBackupMinutes,
    int FrameRateLimit,
    string BackupRoot,
    int BackupFileCount,
    string DesktopDocumentCacheRoot,
    IReadOnlyList<string> OpenTabCachePaths,
    string OnlineRoomResourceCacheRoot);

public sealed record GuiShortcutEvidence(
    string SettingsPath,
    bool SettingsFileExists,
    string WpfSettingsPath,
    bool SettingsIsolatedFromWpf,
    int CatalogCount,
    int AssignedCount,
    int UnassignedCount,
    int TextEditingSuppressedActionCount,
    int DuplicateGestureCount,
    bool AllCatalogActionsRoutable,
    IReadOnlyDictionary<string, string> Bindings);

public sealed record GuiMetricSemantics(
    string RenderMilliseconds,
    string FrameIntervalMilliseconds,
    string InputToRenderMilliseconds);

public sealed record GuiWindowEvidence(
    double ClientWidthDip,
    double ClientHeightDip,
    int PositionXPixels,
    int PositionYPixels,
    string WindowState,
    double MinimumWidthDip,
    double MinimumHeightDip,
    int ScreenXPixels,
    int ScreenYPixels,
    int ScreenWidthPixels,
    int ScreenHeightPixels,
    int WorkAreaXPixels,
    int WorkAreaYPixels,
    int WorkAreaWidthPixels,
    int WorkAreaHeightPixels,
    double ScreenScaling,
    double EffectiveScreenWidthDip,
    double EffectiveScreenHeightDip,
    double EffectiveWorkAreaWidthDip,
    double EffectiveWorkAreaHeightDip,
    string? DisplayName,
    GuiDisplayScaleEvidence? DisplayScale);

public sealed record GuiDisplayScaleEvidence(
    double RequestedUiScale,
    double RequestedFontScale,
    double ActualRootScaleX,
    double ActualRootScaleY,
    double ActualFontScale,
    bool UsesLayoutTransform,
    double ScreenScaling,
    double EffectiveScreenWidth,
    double EffectiveScreenHeight,
    double EffectiveWorkAreaWidth,
    double EffectiveWorkAreaHeight,
    double WindowMinimumWidth,
    double WindowMinimumHeight,
    bool DisplayScaleInitialized,
    string LocalDataRoot,
    string SettingsPath,
    bool SettingsFileExists,
    string WpfViewSettingsPath,
    bool SettingsIsolatedFromWpf,
    GuiFontScaleDiagnosticsEvidence FontDiagnostics);

public sealed record GuiFontScaleDiagnosticsEvidence(
    double WindowFontSize,
    int ScannedObjectCount,
    int CandidateCount,
    int ScaledElementCount,
    int InheritedOrDefaultSkippedCount,
    int CapturedBaselineCount);

public sealed record GuiLibraryEvidence(
    string WorkspaceRoot,
    string SelectionMode,
    int IndexedFolderCount,
    int VisibleFolderCount,
    int SelectedFolderCount,
    bool DeleteButtonEnabled,
    string DeleteButtonContent,
    bool DeletionInProgress,
    GuiPopupScaleEvidence? ContextMenuScale,
    GuiLibraryDeletionEvidence? LastDeletion);

public sealed record GuiPopupScaleEvidence(
    double RequestedUiScale,
    double RequestedFontScale,
    double BaseFontSize,
    double AppliedFontSize,
    bool PopupRootFound,
    double ActualPopupRootScaleX,
    double ActualPopupRootScaleY,
    double PopupRootWidthDip,
    double PopupRootHeightDip);

public sealed record GuiLibraryDeletionEvidence(
    bool CanExecute,
    string BlockReason,
    string? BlockMessage,
    int FolderCount,
    int ChartCount,
    IReadOnlyList<string> TargetFullPaths,
    IReadOnlyList<string> BlockingDocumentTitles,
    IReadOnlyList<string> PreviewItems,
    int OmittedPreviewCount,
    string? ConfirmationMessage,
    bool Confirmed,
    bool Canceled,
    GuiLibraryDeleteDialogScaleEvidence? DialogScale,
    IReadOnlyList<GuiWindowsRecycleResultEvidence> RecycleResults,
    IReadOnlyList<string> FailureMessages);

public sealed record GuiLibraryDeleteDialogScaleEvidence(
    double RequestedUiScale,
    double RequestedFontScale,
    double ActualRootScaleX,
    double ActualRootScaleY,
    double MessageFontSize,
    bool UsesLayoutTransform);

public sealed record GuiWindowsRecycleResultEvidence(
    string FullPath,
    int ShellResult,
    bool AnyOperationsAborted,
    bool AllowUndoRequested);

public sealed record GuiChartValidationEvidence(
    IReadOnlyList<GuiChartIssueEvidence> Issues,
    int TotalIssueCount,
    int OverlappingNoteCount,
    int CrossingLongNoteCount,
    bool CanSave,
    GuiChartIssueEvidence? FirstIssue,
    GuiChartIssueNavigationEvidence? Navigation,
    GuiChartIssueNavigationEvidence? PresentationNavigation,
    GuiChartIssueNavigationEvidence? LastNavigation,
    bool LastNavigationMatchesPresentation,
    string ManualCheckMessage,
    string? SaveBlockingMessage,
    GuiChartValidationTimelineCrossCheckEvidence Timeline,
    bool ManualCheckShown,
    bool SaveBlockedShown,
    string? LastDialogMode,
    GuiChartValidationDialogScaleEvidence? ManualDialogScale,
    GuiChartValidationDialogScaleEvidence? SaveDialogScale);

public sealed record GuiChartIssueEvidence(
    string Kind,
    GuiBeatEvidence Beat,
    int Lane,
    IReadOnlyList<string> NoteIds,
    string Message);

public sealed record GuiChartIssueNavigationEvidence(
    GuiBeatEvidence ExactBeat,
    IReadOnlyList<string> NoteIds,
    IReadOnlyList<int> HiddenGroupsToReveal,
    int InspectorTabIndex,
    string HoverStatusText);

public sealed record GuiChartValidationTimelineCrossCheckEvidence(
    IReadOnlyList<GuiChartIssueEvidence> Issues,
    int TotalIssueCount,
    int OverlappingNoteCount,
    int CrossingLongNoteCount,
    bool MatchesPresentation);

public sealed record GuiChartValidationDialogScaleEvidence(
    double RequestedUiScale,
    double RequestedFontScale,
    double ActualRootScaleX,
    double ActualRootScaleY,
    double MessageFontSize,
    bool UsesLayoutTransform);

public sealed record GuiChartEvidence(
    string SourcePath,
    string SourceSha256,
    string WorkloadKind,
    int? SyntheticNoteCount,
    ChartStructureSummary Structure);

public sealed record GuiAudioEvidence(
    bool UiIntegrated,
    string State,
    string? SourcePath,
    bool SourceExists,
    double PositionMilliseconds,
    double DurationMilliseconds,
    double PlaybackRate,
    bool PreservePitch,
    float Volume,
    string Status,
    string? LastErrorType,
    GuiAudioVisualizationEvidence Visualization,
    GuiKeySoundEvidence? KeySound,
    GuiAudioPanelsEvidence? Panels);

public sealed record GuiAudioVisualizationEvidence(
    string State,
    string Status,
    string? LastErrorType,
    int WaveformCount,
    double WaveformBucketMilliseconds,
    double WaveformDurationMilliseconds,
    int SpectrogramFrameCount,
    int SpectrogramFrequencyBands,
    double SpectrogramFrameMilliseconds,
    double SpectrogramDurationMilliseconds);

public sealed record GuiAudioPanelsEvidence(
    string SettingsPath,
    bool SettingsFileExists,
    string WpfViewSettingsPath,
    bool SettingsIsolatedFromWpf,
    int SideCustomSurfaceCount,
    int PerSampleAvaloniaControlCount,
    bool VisibleAreaClipping,
    GuiAudioPanelEvidence Waveform,
    GuiAudioPanelEvidence Spectrogram);

public sealed record GuiAudioPanelEvidence(
    string Kind,
    string Mode,
    bool Collapsed,
    bool Docked,
    double RememberedXDip,
    double RememberedYDip,
    double RememberedWidthDip,
    double? ActualWindowXDip,
    double? ActualWindowYDip,
    double ActualWidthDip,
    double ActualHeightDip,
    bool SurfaceVisible,
    bool BackgroundEnabled,
    bool SurfaceClipToBounds,
    AudioVisualizationSideDiagnostics Diagnostics);

public sealed record GuiKeySoundEvidence(
    bool UiIntegrated,
    GuiKeySoundSettingsEvidence Settings,
    GuiKeySoundPlanEvidence Plan,
    GuiKeySoundSchedulerEvidence Scheduler,
    GuiKeySoundOutputEvidence Output,
    string Status,
    string? LastErrorType,
    string ValidationLevel,
    bool AudibleOrLoopbackValidated);

public sealed record GuiKeySoundSettingsEvidence(
    bool Enabled,
    string Preset,
    double Volume,
    string? CustomFilePath,
    bool CustomFileExists,
    bool PlayLongNoteEndSound,
    double GlobalOffsetMilliseconds,
    double ChartOffsetMilliseconds,
    double EffectiveOffsetMilliseconds);

public sealed record GuiKeySoundPlanEvidence(
    string SourcePath,
    long SessionChangeSerial,
    IReadOnlyList<int> HiddenGroups,
    int? EventCount,
    int? HeadEventCount,
    int? LongNoteTailEventCount,
    int? CustomSampleEventCount,
    int? PresetFallbackEventCount,
    int? PreloadCount,
    int RuntimeEventCount,
    int RuntimePreloadCount,
    bool RuntimeRequestedTabMatches,
    bool RuntimePlannedTabMatches,
    bool RuntimeEventCountMatches,
    bool RuntimePreloadCountMatches,
    bool RuntimeEnabledMatches,
    bool RuntimePlanMatchesExpected,
    string? ExpectedPlanErrorType);

public sealed record GuiKeySoundSchedulerEvidence(
    string? RequestedTabId,
    string? PlannedTabId,
    long Generation,
    bool Preparing,
    bool HasPlan,
    bool Enabled,
    double PositionMilliseconds,
    double PlaybackRate,
    long PlanRequests,
    long PlansPublished,
    long PlansSuperseded,
    long PlanFailures,
    long PreloadAttempts,
    long PreloadAccepted,
    long PreloadRejected,
    long PumpCalls,
    long ClockSamplesPublished,
    long ClockSamplesReplaced,
    long ClockSamplesProcessed,
    long Due,
    long Accepted,
    long Rejected,
    long SubmittedVoices,
    long Resets,
    long Invalidations);

public sealed record GuiKeySoundOutputEvidence(
    string TransportMode,
    bool OwnsKeySoundService,
    long SharedAccepted,
    long SharedRejected,
    long StandaloneAccepted,
    long StandaloneRejected,
    long AuditionAccepted,
    long AuditionRejected);

public sealed record GuiPointEvidence(double X, double Y);

public sealed record GuiBeatEvidence(
    int Bar,
    int Numerator,
    int Denominator,
    double Value);

public sealed record GuiHoldTailEvidence(
    string NoteId,
    int Lane,
    GuiBeatEvidence HeadBeat,
    GuiBeatEvidence EndBeat,
    GuiPointEvidence? WindowPointDip);

public sealed record GuiSelectedNoteEvidence(
    string NoteId,
    int Lane,
    GuiBeatEvidence Beat,
    GuiBeatEvidence? EndBeat,
    int Volume,
    string? Sound,
    int Group,
    bool IsDraft,
    int? DraftColumn,
    int WorkspaceColumn);

public sealed record GuiPlacementTargetEvidence(
    int Lane,
    GuiBeatEvidence Beat,
    GuiPointEvidence? WindowPointDip);

public sealed record GuiPlacementProbeEvidence(
    GuiPlacementTargetEvidence TapTarget,
    GuiPlacementTargetEvidence HoldStartTarget,
    GuiPlacementTargetEvidence HoldEndTarget,
    GuiSelectedNoteEvidence? TapObservation,
    GuiSelectedNoteEvidence? HoldObservation);

public sealed record GuiGroupManagerRowEvidence(
    int Group,
    int NoteCount,
    string? DominantSound,
    bool IsVisible,
    bool IsCurrent);

public sealed record GuiGroupManagerEvidence(
    int ActivePlacementGroup,
    int VisibleEditableSelectionCount,
    IReadOnlyList<GuiGroupManagerRowEvidence> Rows);

public sealed record GuiProEditorColumnRangeEvidence(int Minimum, int Maximum);

public sealed record GuiProEditorTrackPanEvidence(
    double Minimum,
    double Maximum,
    double Value,
    bool Enabled);

public sealed record GuiProEditorGeometryEvidence(
    double LaneWidthDip,
    double OfficialTrackLeftSurfaceDip,
    double OfficialTrackRightSurfaceDip,
    double? OfficialTrackLeftWindowDip,
    double? OfficialTrackRightWindowDip,
    double? TimelineViewportLeftWindowDip,
    double? TimelineViewportRightWindowDip);

public sealed record GuiProEditorTargetEvidence(
    int WorkspaceColumn,
    bool IsDraft,
    GuiBeatEvidence Beat,
    GuiPointEvidence? WindowPointDip);

public sealed record GuiProEditorProbeEvidence(
    GuiProEditorTargetEvidence DraftTapTarget,
    GuiProEditorTargetEvidence OfficialMoveTarget,
    GuiProEditorTargetEvidence DraftHoldStartTarget,
    GuiProEditorTargetEvidence DraftHoldEndTarget,
    GuiSelectedNoteEvidence? MovedOfficialObservation,
    GuiSelectedNoteEvidence? DraftHoldObservation);

public sealed record GuiProEditorEvidence(
    bool Enabled,
    int TotalColumns,
    GuiProEditorColumnRangeEvidence VisibleColumnRange,
    GuiProEditorColumnRangeEvidence ConfiguredColumnRange,
    GuiProEditorTrackPanEvidence HorizontalPan,
    GuiProEditorGeometryEvidence Geometry,
    int OfficialDocumentNoteCount,
    int DraftDocumentNoteCount,
    int VisibleOfficialNoteCount,
    int VisibleDraftNoteCount,
    IReadOnlyList<GuiSelectedNoteEvidence> DocumentNotes,
    GuiProEditorProbeEvidence? Probe);

public sealed record GuiTimelineEvidence(
    bool SingleCustomControl,
    int PerNoteAvaloniaControlCount,
    bool VisibleAreaClipping,
    string VisibleRangeIndex,
    double CurrentBeat,
    int SelectedNoteCount,
    int VisibleSelectedNoteCount,
    int VisibleDocumentNoteCount,
    int SelectedTimingCount,
    int SnapDivision,
    string CurrentTool,
    int ActivePlacementGroup,
    IReadOnlyList<int> HiddenGroups,
    GuiGroupManagerEvidence GroupManager,
    GuiProEditorEvidence ProEditor,
    GuiTimelineGeometryEvidence Geometry,
    long SessionChangeSerial,
    bool SessionIsDirty,
    IReadOnlyList<GuiSelectedNoteEvidence> SelectedNotes,
    GuiHoldTailEvidence? SelectedHoldTail,
    GuiPlacementTargetEvidence? FreeTapTarget,
    GuiPlacementProbeEvidence? PlacementProbe,
    TimelineAudioBackgroundDiagnostics AudioBackground,
    double PixelsPerBeat,
    double ProbeDurationMilliseconds,
    int WarmupTicks,
    bool ForcedFullGcBeforeSampling,
    int ProbeTicks,
    int TargetRenderSamples,
    bool TargetReached,
    TimelinePerformanceSnapshot Performance);

public sealed record GuiTimelineGeometryEvidence(
    double? LeftWindowDip,
    double? TopWindowDip,
    double? RightWindowDip,
    double? BottomWindowDip,
    double? JudgmentLineYWindowDip,
    double WidthDip,
    double HeightDip);

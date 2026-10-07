using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using LumaChart.Application.Audio;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.Verification;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Online;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsSettingsCacheSpecs
{
    public static IReadOnlyList<WindowsSettingsCacheSpecResult> Run(string isolationRoot)
    {
        Directory.CreateDirectory(isolationRoot);
        var results = new List<WindowsSettingsCacheSpecResult>();
        Check(results, "settings-view-model-clamps-to-wpf-1-1-0-contract", VerifySettingsClamp);
        Check(results, "settings-editor-default-preset-and-track-transparency-direction",
            VerifyEditorPresetAndTrackTransparency);
        Check(results, "settings-editor-background-darkness-roundtrips-and-migrates",
            () => VerifyEditorBackgroundStore(isolationRoot));
        Check(results, "frame-rate-presets-normalization-and-gate-match-wpf-1-1-0",
            VerifyFrameRateContract);
        Check(results, "frame-rate-high-resolution-clock-runs-off-ui-thread",
            VerifyHighRateClock);
        Check(results, "windows-rendering-prefers-low-latency-dxgi-with-safe-fallbacks",
            VerifyWindowsRenderingConfiguration);
        Check(results, "settings-atomic-store-roundtrips-and-stays-isolated-from-wpf",
            () => VerifySettingsStore(isolationRoot));
        Check(results, "settings-corrupt-or-unknown-schema-fails-closed",
            () => VerifySettingsCorruption(isolationRoot));
        Check(results, "note-anchor-geometry-and-exact-rational-beats-match-wpf-1-1-0",
            VerifyNoteAnchorGeometry);
        Check(results, "settings-timeline-view-and-x-offset-update-single-surface-geometry",
            VerifyTimelineGeometry);
        Check(results, "quick-mirror-preserves-authored-beats-and-uses-single-undo",
            VerifyQuickMirrorRoundTrip);
        Check(results, "quick-mirror-permission-lock-and-empty-selection-fail-closed",
            VerifyQuickMirrorGuards);
        Check(results, "quick-mirror-mixed-source-lock-filters-like-wpf",
            VerifyQuickMirrorMixedSourceLock);
        Check(results, "extension-random-permutation-is-consistent-and-single-undo",
            VerifyFixedLaneRandomization);
        Check(results, "extension-full-random-per-beat-preserves-row-lane-uniqueness",
            VerifyPerBeatLaneRandomization);
        Check(results, "extension-randomization-permission-lock-and-draft-guards",
            VerifyLaneRandomizationGuards);
        Check(results, "manual-tempo-taps-resolve-bpm-phase-and-reject-short-samples",
            VerifyManualTempoTapAnalysis);
        Check(results, "backup-archive-roundtrips-rational-chart-and-strips-private-marker",
            () => VerifyBackupRoundTrip(isolationRoot));
        Check(results, "backup-archive-same-second-writes-unique-atomic-files",
            () => VerifyBackupUniquePaths(isolationRoot));
        Check(results, "cache-cleanup-deletes-orphans-and-protects-open-workspace",
            () => VerifyProtectedCacheCleanup(isolationRoot));
        return results;
    }

    private static string VerifySettingsClamp()
    {
        var settings = new WindowsEditorViewSettings(
            double.NegativeInfinity,
            9999,
            732.4,
            -1,
            999,
            0,
            1,
            0,
            double.NaN,
            autoBackupEnabled: false,
            autoBackupMinutes: 0,
            quickPaletteX: double.PositiveInfinity,
            quickPaletteY: -9999,
            quickPaletteCollapsed: true,
            quickValuesPaletteX: double.NaN,
            quickValuesPaletteY: 9999,
            quickValuesPaletteCollapsed: true,
            snapDivision: 0,
            playbackRate: double.PositiveInfinity,
            difficultyCardX: double.NegativeInfinity,
            difficultyCardY: 9999,
            difficultyCardCollapsed: true,
            frameRateLimit: 349,
            noteAnchor: (WindowsNoteAnchorMode)99,
            musicVolume: double.NegativeInfinity,
            preservePitch: true,
            fullscreenMode: (WindowsFullscreenMode)99,
            barLineThickness: double.NaN,
            subdivisionLineThickness: double.PositiveInfinity,
            trackOpacity: double.NaN);
        Require(settings.GridLaneWidth == WindowsEditorViewSettings.DefaultGridLaneWidth,
            "Non-finite lane width did not return to the WPF default.");
        Require(settings.GridBeatHeight == 480 && settings.MainEditorHorizontalOffset == 600,
            "Beat height or X offset did not clamp.");
        Require(settings.NoteWidth == 12 && settings.NoteHeight == 128 && settings.NoteOpacity == .05,
            "Note geometry did not clamp.");
        Require(settings.JudgementLinePosition == .95 &&
                settings.JudgementLineThickness == .5 &&
                settings.JudgementLineOpacity == WindowsEditorViewSettings.DefaultJudgementLineOpacity,
            "Judgement-line values did not clamp or normalize non-finite input.");
        Require(settings.BarLineThickness == WindowsEditorViewSettings.DefaultBarLineThickness,
            "Non-finite bar-line thickness did not return to the visual-parity default.");
        Require(settings.SubdivisionLineThickness ==
                WindowsEditorViewSettings.DefaultSubdivisionLineThickness,
            "Non-finite subdivision-line thickness did not return to the visual-parity default.");
        Require(settings.TrackOpacity == WindowsEditorViewSettings.DefaultTrackOpacity,
            "Non-finite track opacity did not return to the editor default.");
        Require(!settings.AutoBackupEnabled && settings.AutoBackupMinutes == 1,
            "Auto-backup limits did not match WPF.");
        Require(settings.QuickPaletteX == 0 && settings.QuickPaletteY == -4000 &&
                settings.QuickPaletteCollapsed,
            "Quick-palette layout did not normalize to the WPF-compatible bounds.");
        Require(settings.QuickValuesPaletteX == 0 && settings.QuickValuesPaletteY == 4000 &&
                settings.QuickValuesPaletteCollapsed &&
                settings.SnapDivision == WindowsEditorViewSettings.DefaultSnapDivision &&
                settings.PlaybackRate == WindowsEditorViewSettings.DefaultPlaybackRate &&
                settings.MusicVolume == WindowsEditorViewSettings.DefaultMusicVolume &&
                settings.PreservePitch &&
                settings.FullscreenMode == WindowsFullscreenMode.Windowed &&
                settings.FrameRateLimit == 330 &&
                settings.NoteAnchor == WindowsNoteAnchorMode.GridLine &&
                settings.DifficultyCardX == 0 && settings.DifficultyCardY == 4000 &&
                settings.DifficultyCardCollapsed,
            "Quick-values settings did not fail closed to valid WPF-compatible values.");
        var retained = settings
            .WithBarLineThickness(2.75)
            .WithSubdivisionLineThickness(1.35)
            .WithTrackOpacity(.44)
            .WithView(84, 220, 0, 66, 32, 1, .79, 1.5, .85)
            .WithAutoBackup(true, 5)
            .WithQuickPalette(1, 2, false)
            .WithQuickValuesPalette(3, 4, false)
            .WithSnapDivision(8)
            .WithPlaybackRate(.6)
            .WithMusicVolume(.37)
            .WithPreservePitch(true)
            .WithFullscreenMode(WindowsFullscreenMode.Borderless)
            .WithNoteAnchor(WindowsNoteAnchorMode.LineAbove);
        Require(retained.DifficultyCardX == 0 && retained.DifficultyCardY == 4000 &&
                retained.DifficultyCardCollapsed,
            "An unrelated view/settings update discarded difficulty-card layout state.");
        Require(retained.FrameRateLimit == 330,
            "An unrelated view/settings update discarded the frame-rate limit.");
        Require(Math.Abs(retained.BarLineThickness - 2.75) < 1e-12,
            "An unrelated view/settings update discarded the bar-line thickness.");
        Require(Math.Abs(retained.SubdivisionLineThickness - 1.35) < 1e-12,
            "An unrelated view/settings update discarded the subdivision-line thickness.");
        Require(Math.Abs(retained.TrackOpacity - .44) < 1e-12 &&
                Math.Abs(retained.WithFrameRateLimit(999).TrackOpacity - .44) < 1e-12,
            "An unrelated view/settings update discarded the track opacity.");
        Require(retained.NoteAnchor == WindowsNoteAnchorMode.LineAbove &&
                retained.WithFrameRateLimit(999).NoteAnchor == WindowsNoteAnchorMode.LineAbove,
            "An unrelated settings update discarded the note-anchor mode.");
        Require(Math.Abs(retained.MusicVolume - .37) < 1e-12 &&
                retained.PreservePitch &&
                Math.Abs(retained.WithFrameRateLimit(999).MusicVolume - .37) < 1e-12 &&
                retained.WithFrameRateLimit(999).PreservePitch &&
                retained.WithFrameRateLimit(999).FullscreenMode ==
                WindowsFullscreenMode.Borderless,
            "An unrelated settings update discarded audio or fullscreen state.");
        var highRate = retained.WithFrameRateLimit(999);
        Require(highRate.FrameRateLimit == 1000 &&
                highRate.DifficultyCardX == retained.DifficultyCardX &&
                highRate.DifficultyCardCollapsed == retained.DifficultyCardCollapsed,
            "Frame-rate selection did not normalize or discarded unrelated settings.");
        var resetFloating = new WindowsEditorViewSettings(
                quickPaletteX: 41,
                quickPaletteY: -23,
                quickPaletteCollapsed: true,
                quickValuesPaletteX: -52,
                quickValuesPaletteY: 67,
                quickValuesPaletteCollapsed: true,
                snapDivision: 11,
                difficultyCardX: 87,
                difficultyCardY: -91,
                difficultyCardCollapsed: true,
                noteAnchor: WindowsNoteAnchorMode.CellCenter,
                musicVolume: .42,
                preservePitch: true,
                fullscreenMode: WindowsFullscreenMode.Borderless,
                barLineThickness: 2.25,
                subdivisionLineThickness: 1.2,
                trackOpacity: .52)
            .ResetFloatingLayout();
        Require(
            resetFloating.QuickPaletteX == 0 &&
            resetFloating.QuickPaletteY == 0 &&
            !resetFloating.QuickPaletteCollapsed &&
            resetFloating.QuickValuesPaletteX == 0 &&
            resetFloating.QuickValuesPaletteY == 0 &&
            !resetFloating.QuickValuesPaletteCollapsed &&
            resetFloating.DifficultyCardX == 0 &&
            resetFloating.DifficultyCardY == 0 &&
            !resetFloating.DifficultyCardCollapsed &&
            resetFloating.SnapDivision == 11 &&
            resetFloating.NoteAnchor == WindowsNoteAnchorMode.CellCenter &&
            Math.Abs(resetFloating.MusicVolume - .42) < 1e-12 &&
            Math.Abs(resetFloating.BarLineThickness - 2.25) < 1e-12 &&
            Math.Abs(resetFloating.SubdivisionLineThickness - 1.2) < 1e-12 &&
            Math.Abs(resetFloating.TrackOpacity - .52) < 1e-12 &&
            resetFloating.PreservePitch &&
            resetFloating.FullscreenMode == WindowsFullscreenMode.Borderless,
            "WPF reset-floating-layout semantics lost unrelated editor settings.");
        return $"lane={settings.GridLaneWidth}; beat={settings.GridBeatHeight}; " +
               $"x={settings.MainEditorHorizontalOffset}; note={settings.NoteWidth}x{settings.NoteHeight}; " +
               $"line={settings.JudgementLinePosition:R}/{settings.JudgementLineThickness:R}/" +
               $"{settings.JudgementLineOpacity:R}; backup={settings.AutoBackupEnabled}/" +
               $"{settings.AutoBackupMinutes}; quick={settings.QuickPaletteX}/" +
               $"{settings.QuickPaletteY}/{settings.QuickPaletteCollapsed}; values=" +
               $"{settings.QuickValuesPaletteX}/{settings.QuickValuesPaletteY}/" +
               $"{settings.QuickValuesPaletteCollapsed}/1-{settings.SnapDivision}/" +
               $"{settings.PlaybackRate:0.00}x; audio={settings.MusicVolume:0.00}/" +
               $"{settings.PreservePitch}; barLine={settings.BarLineThickness:0.00}; " +
               $"subdivisionLine={settings.SubdivisionLineThickness:0.00}; " +
               $"trackOpacity={settings.TrackOpacity:0.00}; " +
               $"fullscreen={settings.FullscreenMode}; " +
               $"difficulty={settings.DifficultyCardX}/" +
               $"{settings.DifficultyCardY}/{settings.DifficultyCardCollapsed}; " +
               $"frameRate={settings.FrameRateLimit}";
    }

    private static string VerifyEditorPresetAndTrackTransparency()
    {
        var defaults = WindowsEditorViewSettings.Default;
        Require(
            defaults.GridBeatHeight == 280 &&
            defaults.NoteWidth == 77 &&
            defaults.NoteHeight == 41 &&
            Math.Abs(defaults.BarLineThickness - 2.2) < 1e-12 &&
            Math.Abs(defaults.SubdivisionLineThickness - 1.6) < 1e-12 &&
            Math.Abs(WindowsTrackTransparencyPolicy.ToTransparency(
                defaults.TrackOpacity) - .3) < 1e-12,
            "The requested editor view preset was not applied to fresh/reset settings.");
        Require(
            WindowsTrackTransparencyPolicy.ToFillOpacity(0) == 1 &&
            WindowsTrackTransparencyPolicy.ToFillOpacity(1) == 0 &&
            WindowsTrackTransparencyPolicy.ToTransparency(1) == 0 &&
            WindowsTrackTransparencyPolicy.ToTransparency(0) == 1 &&
            WindowsTrackTransparencyPolicy.OpaqueTrackColor == "#000000",
            "Track transparency is reversed: zero must render a fully opaque black lane fill.");
        Require(
            new WindowsEditorViewSettings(trackOpacity: -1).TrackOpacity == 0 &&
            new WindowsEditorViewSettings(trackOpacity: 2).TrackOpacity == 1,
            "Track fill opacity no longer accepts the full transparent-to-opaque range.");
        return "preset=280/77x41/2.20/1.60; transparency=0->opaque-black,100->transparent";
    }

    private static string VerifyEditorBackgroundStore(string isolationRoot)
    {
        const string variable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        var previous = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(isolationRoot, "editor-background-settings");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(variable, root);
            Require(
                Math.Abs(WindowsEditorBackgroundSettings.Default.Darkness - .70) < 1e-12,
                "Fresh/reset overall background darkness is not 70%.");
            var expected = new WindowsEditorBackgroundSettings(.37);
            WindowsEditorBackgroundSettingsStore.Save(expected);
            var actual = WindowsEditorBackgroundSettingsStore.Load();
            using var persisted = JsonDocument.Parse(File.ReadAllBytes(
                WindowsEditorBackgroundSettingsStore.SettingsPath));
            Require(Math.Abs(actual.Darkness - .37) < 1e-12 &&
                    persisted.RootElement.GetProperty("SchemaVersion").GetInt32() == 2 &&
                    Math.Abs(persisted.RootElement.GetProperty("Darkness").GetDouble() - .37) <
                    1e-12 &&
                    !persisted.RootElement.TryGetProperty("Transparency", out _),
                "Overall editor background darkness did not round-trip as schema 2.");
            File.WriteAllText(
                WindowsEditorBackgroundSettingsStore.SettingsPath,
                "{\"SchemaVersion\":1,\"Transparency\":0.42}");
            var migratedCustom = WindowsEditorBackgroundSettingsStore.Load();
            Require(Math.Abs(migratedCustom.Darkness - .42) < 1e-12,
                "A customized schema-1 background value was not retained as darkness.");
            File.WriteAllText(
                WindowsEditorBackgroundSettingsStore.SettingsPath,
                "{\"SchemaVersion\":1,\"Transparency\":0.05}");
            var migratedDefault = WindowsEditorBackgroundSettingsStore.Load();
            Require(Math.Abs(migratedDefault.Darkness - .70) < 1e-12,
                "The old 5% preset was not upgraded to the new 70% darkness preset.");
            File.WriteAllText(WindowsEditorBackgroundSettingsStore.SettingsPath, "{broken");
            var fallback = WindowsEditorBackgroundSettingsStore.Load();
            Require(Math.Abs(fallback.Darkness -
                             WindowsEditorBackgroundSettings.DefaultDarkness) < 1e-12,
                "Corrupt overall background settings did not fail closed to the default.");
            return $"path={WindowsEditorBackgroundSettingsStore.SettingsPath}; " +
                   $"default={WindowsEditorBackgroundSettings.Default.Darkness:P0}; " +
                   $"darkness={actual.Darkness:P0}; migrated={migratedCustom.Darkness:P0}";
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static string VerifyFrameRateContract()
    {
        int[] expected = [30, 60, 90, 120, 144, 165, 240, 330, 1000];
        Require(WindowsFrameRateOptions.Presets.SequenceEqual(expected),
            "Frame-rate presets do not match WPF 1.1.0.");
        Require(
            WindowsFrameRateOptions.Normalize(-100) == 30 &&
            WindowsFrameRateOptions.Normalize(75) == 60 &&
            WindowsFrameRateOptions.Normalize(105) == 90 &&
            WindowsFrameRateOptions.Normalize(155) == 165 &&
            WindowsFrameRateOptions.Normalize(285) == 240 &&
            WindowsFrameRateOptions.Normalize(999) == 1000,
            "Nearest-preset frame-rate normalization differs from WPF.");

        double now = 0;
        var gate = new WindowsFrameRateGate(59, () => now);
        Require(gate.Limit == 60 && gate.ShouldRender(),
            "Frame-rate gate did not normalize or accept the initial frame.");
        now = 5;
        Require(!gate.ShouldRender(), "Frame-rate gate admitted an early frame.");
        now = 15.7;
        Require(gate.ShouldRender(), "Frame-rate gate lost WPF's compositor-jitter tolerance.");
        now = 120;
        Require(gate.ShouldRender(), "Frame-rate gate did not recover after a late-frame gap.");
        now = 120.1;
        Require(!gate.ShouldRender(), "Frame-rate gate failed to rebase after a late-frame gap.");
        gate.SetLimit(119);
        Require(gate.Limit == 120 && gate.ShouldRender(),
            "Changing the frame-rate limit did not reset and normalize the gate.");
        return $"presets={string.Join(',', expected)}; normalized=60/120/165/240/1000; " +
               "gate=initial/early/tolerant/resync";
    }

    private static string VerifyHighRateClock()
    {
        using var pump = new WindowsHighRateFramePump();
        var callerThread = Environment.CurrentManagedThreadId;
        var callbackThread = 0;
        var callbackCount = 0;
        pump.Start(242, () =>
        {
            Volatile.Write(ref callbackThread, Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref callbackCount);
        });
        var normalized = pump.FramesPerSecond;
        var reached = SpinWait.SpinUntil(
            () => Volatile.Read(ref callbackCount) >= 12,
            TimeSpan.FromSeconds(2));
        pump.Stop();
        var ticks = pump.TotalTickCount;
        Require(reached && callbackCount >= 12 && ticks >= callbackCount,
            "High-rate frame clock did not produce the expected callbacks.");
        Require(normalized == 240 && callbackThread != 0 && callbackThread != callerThread,
            "High-rate frame clock was not normalized or did not run off the caller thread.");
        Require(!pump.IsRunning && pump.FramesPerSecond == 0,
            "High-rate frame clock did not stop cleanly.");
        return $"requested=242; normalized={normalized}; callbacks={callbackCount}; " +
               $"ticks={ticks}; workerThread={callbackThread}; callerThread={callerThread}";
    }

    private static string VerifyWindowsRenderingConfiguration()
    {
        var configuration = WindowsRenderingConfiguration.Create();
        Require(
            configuration.RenderingMode.SequenceEqual(
            [
                Win32RenderingMode.AngleEgl,
                Win32RenderingMode.Software
            ]),
            "Windows rendering did not retain ANGLE GPU rendering with software fallback.");
        Require(
            configuration.CompositionMode.SequenceEqual(
            [
                Win32CompositionMode.LowLatencyDxgiSwapChain,
                Win32CompositionMode.WinUIComposition,
                Win32CompositionMode.DirectComposition,
                Win32CompositionMode.RedirectionSurface
            ]),
            "Windows composition did not prefer low-latency DXGI or retain safe fallbacks.");
        return "render=AngleEgl/Software; composition=" +
               "LowLatencyDxgiSwapChain/WinUIComposition/DirectComposition/RedirectionSurface";
    }

    private static string VerifySettingsStore(string isolationRoot)
    {
        const string variable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        var previous = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(isolationRoot, "settings-store");
        var wpfPath = Path.Combine(root, "wpf-view-settings.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(wpfPath, "wpf-authority-must-not-change");
        try
        {
            Environment.SetEnvironmentVariable(variable, root);
            var expected = new WindowsEditorViewSettings(
                97,
                271,
                -230,
                73,
                41,
                .65,
                .64,
                3.5,
                .55,
                autoBackupEnabled: true,
                autoBackupMinutes: 17,
                quickPaletteX: -178,
                quickPaletteY: 96,
                quickPaletteCollapsed: true,
                quickValuesPaletteX: 214,
                quickValuesPaletteY: -133,
                quickValuesPaletteCollapsed: true,
                snapDivision: 7,
                playbackRate: .6,
                difficultyCardX: -92,
                difficultyCardY: 117,
                difficultyCardCollapsed: true,
                frameRateLimit: 330,
                noteAnchor: WindowsNoteAnchorMode.LineAbove,
                musicVolume: .37,
                preservePitch: true,
                fullscreenMode: WindowsFullscreenMode.Borderless,
                barLineThickness: 2.75,
                subdivisionLineThickness: 1.35,
                trackOpacity: .42);
            WindowsEditorViewSettingsStore.Save(expected);
            var actual = WindowsEditorViewSettingsStore.Load();
            using var persisted = JsonDocument.Parse(
                File.ReadAllBytes(WindowsEditorViewSettingsStore.SettingsPath));
            var rootElement = persisted.RootElement;
            var temporaryCount = Directory
                .EnumerateFiles(root, ".editor-view-settings.json.*.tmp")
                .Count();
            Require(actual.GridLaneWidth == 97 && actual.GridBeatHeight == 271 &&
                    actual.MainEditorHorizontalOffset == -230 && actual.NoteWidth == 73 &&
                    actual.NoteHeight == 41 && Math.Abs(actual.NoteOpacity - .65) < 1e-12 &&
                    Math.Abs(actual.JudgementLinePosition - .64) < 1e-12 &&
                    Math.Abs(actual.JudgementLineThickness - 3.5) < 1e-12 &&
                    Math.Abs(actual.JudgementLineOpacity - .55) < 1e-12 &&
                    actual.AutoBackupEnabled && actual.AutoBackupMinutes == 17 &&
                    actual.QuickPaletteX == -178 && actual.QuickPaletteY == 96 &&
                    actual.QuickPaletteCollapsed && actual.QuickValuesPaletteX == 214 &&
                    actual.QuickValuesPaletteY == -133 && actual.QuickValuesPaletteCollapsed &&
                    actual.SnapDivision == 7 && actual.PlaybackRate == .6 &&
                    Math.Abs(actual.MusicVolume - .37) < 1e-12 && actual.PreservePitch &&
                    actual.FullscreenMode == WindowsFullscreenMode.Borderless &&
                    Math.Abs(actual.BarLineThickness - 2.75) < 1e-12 &&
                    Math.Abs(actual.SubdivisionLineThickness - 1.35) < 1e-12 &&
                    Math.Abs(actual.TrackOpacity - .42) < 1e-12 &&
                    actual.FrameRateLimit == 330 &&
                    actual.NoteAnchor == WindowsNoteAnchorMode.LineAbove &&
                    actual.DifficultyCardX == -92 && actual.DifficultyCardY == 117 &&
                    actual.DifficultyCardCollapsed,
                "Editor view settings did not round-trip.");
            Require(rootElement.GetProperty("SchemaVersion").GetInt32() ==
                    WindowsEditorViewSettingsStore.CurrentSchemaVersion,
                "Persisted settings schema is missing.");
            Require(
                rootElement.GetProperty("NoteAnchor").ValueKind == JsonValueKind.Number &&
                rootElement.GetProperty("NoteAnchor").GetInt32() ==
                (int)WindowsNoteAnchorMode.LineAbove,
                "Persisted note-anchor mode is missing.");
            Require(
                Math.Abs(rootElement.GetProperty("MusicVolume").GetDouble() - .37) < 1e-12 &&
                rootElement.GetProperty("PreservePitch").GetBoolean(),
                "Persisted music volume or preserve-pitch is missing.");
            Require(
                rootElement.GetProperty("FullscreenMode").GetInt32() ==
                (int)WindowsFullscreenMode.Borderless,
                "Persisted fullscreen mode is missing.");
            Require(
                Math.Abs(rootElement.GetProperty("BarLineThickness").GetDouble() - 2.75) < 1e-12,
                "Persisted bar-line thickness is missing.");
            Require(
                Math.Abs(rootElement.GetProperty("SubdivisionLineThickness").GetDouble() - 1.35) <
                1e-12,
                "Persisted subdivision-line thickness is missing.");
            Require(
                Math.Abs(rootElement.GetProperty("TrackOpacity").GetDouble() - .42) < 1e-12,
                "Persisted track opacity is missing.");
            Require(temporaryCount == 0, "Atomic settings save left a temporary file.");
            Require(File.ReadAllText(wpfPath) == "wpf-authority-must-not-change",
                "Avalonia settings modified the WPF sentinel.");
            Require(!PathsEqual(WindowsEditorViewSettingsStore.SettingsPath, wpfPath),
                "Avalonia and WPF settings paths are not isolated.");
            return $"path={WindowsEditorViewSettingsStore.SettingsPath}; " +
                   $"schema={rootElement.GetProperty("SchemaVersion").GetInt32()}; " +
                   $"temp={temporaryCount}; x={actual.MainEditorHorizontalOffset}; " +
                   $"quick={actual.QuickPaletteX}/{actual.QuickPaletteY}/" +
                   $"{actual.QuickPaletteCollapsed}; values={actual.QuickValuesPaletteX}/" +
                    $"{actual.QuickValuesPaletteY}/{actual.QuickValuesPaletteCollapsed}/" +
                    $"1-{actual.SnapDivision}/{actual.PlaybackRate:0.00}x; audio=" +
                    $"{actual.MusicVolume:0.00}/{actual.PreservePitch}; barLine=" +
                    $"{actual.BarLineThickness:0.00}; subdivisionLine=" +
                    $"{actual.SubdivisionLineThickness:0.00}; fullscreen=" +
                    $"{actual.FullscreenMode}; frameRate=" +
                   $"{actual.FrameRateLimit}; difficulty=" +
                   $"{actual.DifficultyCardX}/{actual.DifficultyCardY}/" +
                   $"{actual.DifficultyCardCollapsed}";
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static string VerifySettingsCorruption(string isolationRoot)
    {
        const string variable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        var previous = Environment.GetEnvironmentVariable(variable);
        var root = Path.Combine(isolationRoot, "settings-corruption");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable(variable, root);
            File.WriteAllText(WindowsEditorViewSettingsStore.SettingsPath, "{not-json");
            var corrupt = WindowsEditorViewSettingsStore.Load();
            Require(corrupt.GridBeatHeight == WindowsEditorViewSettings.DefaultGridBeatHeight &&
                    corrupt.SnapDivision == WindowsEditorViewSettings.DefaultSnapDivision &&
                    corrupt.PlaybackRate == WindowsEditorViewSettings.DefaultPlaybackRate &&
                    corrupt.MusicVolume == WindowsEditorViewSettings.DefaultMusicVolume &&
                    corrupt.BarLineThickness ==
                    WindowsEditorViewSettings.DefaultBarLineThickness &&
                    corrupt.SubdivisionLineThickness ==
                    WindowsEditorViewSettings.DefaultSubdivisionLineThickness &&
                    !corrupt.PreservePitch &&
                    corrupt.FullscreenMode == WindowsFullscreenMode.Windowed &&
                    corrupt.FrameRateLimit == WindowsEditorViewSettings.DefaultFrameRateLimit &&
                    corrupt.NoteAnchor == WindowsNoteAnchorMode.CellBottom,
                "Corrupt settings did not fail closed to defaults.");
            File.WriteAllText(
                WindowsEditorViewSettingsStore.SettingsPath,
                "{\"SchemaVersion\":999,\"SnapDivision\":7,\"PlaybackRate\":0.6}");
            var unknown = WindowsEditorViewSettingsStore.Load();
            Require(unknown.SnapDivision == WindowsEditorViewSettings.DefaultSnapDivision &&
                    unknown.PlaybackRate == WindowsEditorViewSettings.DefaultPlaybackRate &&
                    unknown.MusicVolume == WindowsEditorViewSettings.DefaultMusicVolume &&
                    unknown.BarLineThickness ==
                    WindowsEditorViewSettings.DefaultBarLineThickness &&
                    unknown.SubdivisionLineThickness ==
                    WindowsEditorViewSettings.DefaultSubdivisionLineThickness &&
                    !unknown.PreservePitch &&
                    unknown.FullscreenMode == WindowsFullscreenMode.Windowed &&
                    unknown.FrameRateLimit == WindowsEditorViewSettings.DefaultFrameRateLimit &&
                    unknown.NoteAnchor == WindowsNoteAnchorMode.CellBottom &&
                    unknown.QuickValuesPaletteX == 0 && unknown.QuickValuesPaletteY == 0 &&
                    unknown.DifficultyCardX == 0 && unknown.DifficultyCardY == 0 &&
                    !unknown.DifficultyCardCollapsed,
                "Unknown settings schema was partially trusted.");
            return "corrupt=defaults; unknownSchema=defaults; snap=4; rate=1.00; " +
                   "volume=0.80; preservePitch=false; frameRate=165";
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static string VerifyNoteAnchorGeometry()
    {
        Require(
            WindowsEditorViewSettings.Default.NoteAnchor ==
            WindowsNoteAnchorMode.CellBottom,
            "Avalonia note-anchor default does not match WPF 1.1.0 CellBottom.");

        const int division = 7;
        const double noteHeight = 41;
        const double pixelsPerBeat = 271;
        var lineAboveOffset = TimelinePlacementGeometry.NoteAnchorBeatOffset(
            WindowsNoteAnchorMode.LineAbove,
            division,
            noteHeight,
            pixelsPerBeat);
        var cellBottomOffset = TimelinePlacementGeometry.NoteAnchorBeatOffset(
            WindowsNoteAnchorMode.CellBottom,
            division,
            noteHeight,
            pixelsPerBeat);
        Require(
            Math.Abs(lineAboveOffset - noteHeight / 2 / pixelsPerBeat) < 1e-12,
            "LineAbove offset differs from WPF.");
        // Deliberately not WPF 1.1.0's formula. That build capped the gap at
        // .42/snapDivision, so the space between a note and its line closed up as
        // the subdivision got finer and every note appeared to slide onto the
        // line when the grid changed — with the chart untouched. Sitting at the
        // bottom of a cell means resting just above the line, and how far above
        // is a function of the note's own height, not of the grid. The offset is
        // now the note-height term alone, which also makes it independent of
        // division: asserted here at two divisions to pin that down.
        Require(
            Math.Abs(cellBottomOffset - (noteHeight / 2 + 3) / pixelsPerBeat) < 1e-12,
            "CellBottom offset is not the fixed note-height gap.");
        Require(
            Math.Abs(cellBottomOffset - TimelinePlacementGeometry.NoteAnchorBeatOffset(
                WindowsNoteAnchorMode.CellBottom,
                division * 4,
                noteHeight,
                pixelsPerBeat)) < 1e-12,
            "CellBottom offset still moves with the snap division.");

        var gridStored = BeatPosition.Preserve(3, 2, division);
        foreach (var anchor in new[]
                 {
                     WindowsNoteAnchorMode.GridLine,
                     WindowsNoteAnchorMode.LineAbove,
                     WindowsNoteAnchorMode.CellBottom
                 })
        {
            var renderBeat = TimelinePlacementGeometry.StoredToRenderBeat(
                gridStored,
                anchor,
                division,
                noteHeight,
                pixelsPerBeat);
            var roundTripped = TimelinePlacementGeometry.PointerToStoredBeat(
                renderBeat,
                anchor,
                division,
                noteHeight,
                pixelsPerBeat);
            Require(
                roundTripped.Bar == gridStored.Bar &&
                roundTripped.Numerator == gridStored.Numerator &&
                roundTripped.Denominator == gridStored.Denominator,
                $"{anchor} pointer conversion did not retain exact 1/{division} authorship.");
        }

        var center = TimelinePlacementGeometry.PointerToStoredBeat(
            3.3,
            WindowsNoteAnchorMode.CellCenter,
            12,
            noteHeight,
            pixelsPerBeat);
        Require(
            center.Bar == 3 && center.Numerator == 7 && center.Denominator == 24,
            "CellCenter did not retain the exact odd/2N rational position.");
        var centerRenderAt12 = TimelinePlacementGeometry.StoredToRenderBeat(
            center,
            WindowsNoteAnchorMode.CellCenter,
            12,
            noteHeight,
            pixelsPerBeat);
        var centerRenderAt5 = TimelinePlacementGeometry.StoredToRenderBeat(
            center,
            WindowsNoteAnchorMode.CellCenter,
            5,
            noteHeight,
            pixelsPerBeat);
        Require(
            Math.Abs(centerRenderAt12 - center.Value) < 1e-12 &&
            Math.Abs(centerRenderAt5 - center.Value) < 1e-12,
            "Changing subdivision moved an existing CellCenter note.");
        var importedGridLine = BeatPosition.Preserve(23, 2, 4);
        var importedGridLineRender = TimelinePlacementGeometry.StoredToRenderBeat(
            importedGridLine,
            WindowsNoteAnchorMode.CellCenter,
            4,
            noteHeight,
            pixelsPerBeat);
        Require(
            Math.Abs(importedGridLineRender - (importedGridLine.Value + .125)) < 1e-12 &&
            Math.Abs(TimelinePlacementGeometry.MaximumStoredRenderBeatAdjustment(
                WindowsNoteAnchorMode.CellCenter,
                4) - .125) < 1e-12,
            "Imported grid-line Note was not visually centered in the active cell.");
        Require(
            TimelinePlacementGeometry.NormalizeAnchor((WindowsNoteAnchorMode)99) ==
            WindowsNoteAnchorMode.GridLine,
            "Invalid note-anchor mode did not fail closed.");
        return $"default=CellBottom; lineAbove={lineAboveOffset:R}; " +
               $"cellBottom={cellBottomOffset:R}; grid={gridStored}; center={center}; " +
               "cellCenterSubdivisionInvariant=true; importedGridLineOffset=0.125";
    }

    private static string VerifyTimelineGeometry()
    {
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote
                {
                    Lane = 1,
                    Beat = BeatPosition.Preserve(3, 10, 12)
                }
            ]
        });
        var surface = new TimelineSurface
        {
            Width = 1000,
            Height = 720,
            Session = session
        };
        surface.Measure(new Size(1000, 720));
        surface.Arrange(new Rect(0, 0, 1000, 720));
        var centered = surface.GetProEditorGeometryDiagnostics();
        surface.ConfigureView(
            96,
            271,
            73,
            41,
            .65,
            .64,
            3.5,
            .55,
            WindowsNoteAnchorMode.LineAbove,
            2.75,
            1.35,
            .42);
        surface.SetBackgroundOpacity(.37);
        surface.SetMainTrackOffset(180);
        var shifted = surface.GetProEditorGeometryDiagnostics();
        Require(Math.Abs(surface.GridLaneWidth - 96) < 1e-12 &&
                Math.Abs(surface.PixelsPerBeat - 271) < 1e-12 &&
                Math.Abs(surface.MainTrackOffset - 180) < 1e-12 &&
                Math.Abs(surface.NoteWidth - 73) < 1e-12 &&
                Math.Abs(surface.NoteHeight - 41) < 1e-12 &&
                Math.Abs(surface.NoteOpacity - .65) < 1e-12 &&
                surface.NoteAnchor == WindowsNoteAnchorMode.LineAbove &&
                Math.Abs(surface.JudgementLinePosition - .64) < 1e-12 &&
                Math.Abs(surface.JudgementLineThickness - 3.5) < 1e-12 &&
                Math.Abs(surface.JudgementLineOpacity - .55) < 1e-12 &&
                Math.Abs(surface.BarLineThickness - 2.75) < 1e-12 &&
                Math.Abs(surface.SubdivisionLineThickness - 1.35) < 1e-12 &&
                Math.Abs(surface.TrackOpacity - .42) < 1e-12 &&
                Math.Abs(surface.BackgroundOpacity - .37) < 1e-12,
            "Timeline did not accept the persisted view contract.");
        Require(Math.Abs(shifted.LaneWidthDip - 96) < 1e-9,
            "Configured lane width was not used by timeline geometry.");
        Require(Math.Abs(
                    shifted.OfficialTrackLeftDip -
                    ((1000 - 96 * 4) / 2 + 180)) < 1e-9,
            "Main editor X offset was not applied after centering.");
        Require(shifted.OfficialTrackLeftDip > centered.OfficialTrackLeftDip,
            "Positive X offset did not move the official track right.");
        Require(surface.GetVisualDescendants().OfType<Control>().Count() == 0,
            "Timeline created child Controls for notes or grid geometry.");
        return $"center={centered.OfficialTrackLeftDip:F3}; " +
               $"shifted={shifted.OfficialTrackLeftDip:F3}; lane={shifted.LaneWidthDip:F3}; " +
               $"beat={surface.PixelsPerBeat}; childControls=0";
    }

    private static string VerifyQuickMirrorRoundTrip()
    {
        var officialBeat = BeatPosition.Preserve(2, 2, 6);
        var officialEndBeat = BeatPosition.Preserve(3, 3, 9);
        var official = new ChartNote
        {
            Lane = 0,
            Beat = officialBeat,
            EndBeat = officialEndBeat
        };
        var draftToOfficial = new ChartNote
        {
            IsDraft = true,
            DraftColumn = 0,
            Beat = BeatPosition.Preserve(4, 5, 7)
        };
        var draftOutside = new ChartNote
        {
            IsDraft = true,
            DraftColumn = 5,
            Beat = BeatPosition.Preserve(6, 7, 11),
            EndBeat = BeatPosition.Preserve(7, 8, 13)
        };
        var draftOutsideBeat = draftOutside.Beat;
        var draftOutsideEndBeat = draftOutside.EndBeat;
        var session = SessionWithDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [official, draftToOfficial, draftOutside]
        });
        session.Selection.UnionWith(session.Document.Notes.Select(note => note.Id));

        var changed = NoteMirrorEditingService.MirrorSelection(session, session.Selection);
        var mirroredOfficial = session.Document.Notes.Single(note => note.Id == official.Id);
        var mirroredDraftToOfficial = session.Document.Notes.Single(note => note.Id == draftToOfficial.Id);
        var mirroredDraftOutside = session.Document.Notes.Single(note => note.Id == draftOutside.Id);
        Require(changed == 3 && !mirroredOfficial.IsDraft && mirroredOfficial.Lane == 3,
            "Official lane did not mirror around the official track centre.");
        Require(!mirroredDraftToOfficial.IsDraft && mirroredDraftToOfficial.Lane == 3,
            "Draft column landing in the official range did not become an official Note.");
        Require(mirroredDraftOutside.IsDraft && mirroredDraftOutside.DraftColumn == -2,
            "Draft column outside the official range did not remain a draft Note.");
        Require(mirroredOfficial.Beat == officialBeat && mirroredOfficial.EndBeat == officialEndBeat &&
                mirroredDraftOutside.Beat == draftOutsideBeat &&
                mirroredDraftOutside.EndBeat == draftOutsideEndBeat,
            "Mirror changed exact authored Beat or EndBeat expressions.");
        Require(session.Undo(), "Mirror did not create one undo entry.");
        Require(!session.Document.Notes.Single(note => note.Id == official.Id).IsDraft &&
                session.Document.Notes.Single(note => note.Id == official.Id).Lane == 0 &&
                !session.Undo(),
            "Mirror undo was not a single batch or did not restore columns.");
        Require(session.Redo() &&
                session.Document.Notes.Single(note => note.Id == draftOutside.Id).DraftColumn == -2,
            "Mirror redo did not restore the mirrored draft column.");
        return $"changed={changed}; official=0->3; draft=0->3,5->-2; " +
               $"beat={officialBeat}; end={officialEndBeat}; singleUndo=true";
    }

    private static string VerifyQuickMirrorGuards()
    {
        var note = new ChartNote { Lane = 0, Beat = BeatPosition.Preserve(1, 1, 3) };
        var empty = SessionWithDocument(new ChartDocument { KeyCount = 4, Notes = [note.Clone()] });
        Require(NoteMirrorEditingService.MirrorSelection(empty, []) == 0 &&
                empty.ChangeSerial == 0 && !empty.Undo(),
            "Empty mirror selection created a mutation or history entry.");

        var readOnly = SessionWithDocument(new ChartDocument { KeyCount = 4, Notes = [note.Clone()] });
        readOnly.IsReadOnly = true;
        Require(ThrowsInvalidOperation(() =>
                    NoteMirrorEditingService.MirrorSelection(readOnly, [readOnly.Document.Notes[0].Id])) &&
                readOnly.Document.Notes[0].Lane == 0 && readOnly.ChangeSerial == 0,
            "Read-only mirror did not fail closed.");

        var noPermission = SessionWithDocument(new ChartDocument { KeyCount = 4, Notes = [note.Clone()] });
        noPermission.CanEditNotes = false;
        Require(ThrowsInvalidOperation(() => NoteMirrorEditingService.MirrorSelection(
                    noPermission,
                    [noPermission.Document.Notes[0].Id])) &&
                noPermission.Document.Notes[0].Lane == 0 && noPermission.ChangeSerial == 0,
            "Note-permission mirror did not fail closed.");

        var locked = SessionWithDocument(new ChartDocument { KeyCount = 4, Notes = [note.Clone()] });
        locked.AddOwnerProtectedRegion(
            BeatPosition.Preserve(1, 0, 1),
            BeatPosition.Preserve(2, 0, 1));
        locked.AllowOwnerProtectedEdits = false;
        Require(ThrowsInvalidOperation(() => NoteMirrorEditingService.MirrorSelection(
                    locked,
                    [locked.Document.Notes[0].Id])) &&
                locked.Document.Notes[0].Lane == 0 && locked.ChangeSerial == 0 && !locked.Undo(),
            "Owner-lock mirror did not fail closed without history.");
        return "empty=true; readOnly=true; notePermission=true; ownerLock=true; mutation=false";
    }

    private static string VerifyQuickMirrorMixedSourceLock()
    {
        var unlocked = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(2, 0, 4)
        };
        var locked = new ChartNote
        {
            Lane = 1,
            Beat = BeatPosition.Preserve(3, 0, 4)
        };
        var session = SessionWithDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [unlocked, locked]
        });
        session.Selection.UnionWith([unlocked.Id, locked.Id]);
        session.SetOwnerProtection([locked.Id], isProtected: true);
        session.AllowOwnerProtectedEdits = false;
        var editable = WindowsGroupManagerCommand.VisibleEditableSelectionIds(
            session,
            new HashSet<int>());

        var changed = NoteMirrorEditingService.MirrorSelection(session, editable);
        var actualUnlocked = session.Document.Notes.Single(note => note.Id == unlocked.Id);
        var actualLocked = session.Document.Notes.Single(note => note.Id == locked.Id);
        Require(
            editable.SetEquals([unlocked.Id]) &&
            changed == 1 &&
            actualUnlocked.Lane == 3 &&
            actualLocked.Lane == 1 &&
            session.Undo() &&
            session.Document.Notes.Single(note => note.Id == unlocked.Id).Lane == 0 &&
            session.Document.Notes.Single(note => note.Id == locked.Id).Lane == 1 &&
            !session.Undo(),
            "The WPF host route did not filter the locked source before one-batch mirror.");
        return $"editable={editable.Count}; changed={changed}; unlocked=0->3; locked=1; singleUndo=true";
    }

    private static string VerifyFixedLaneRandomization()
    {
        var hold = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(1, 0, 4),
            EndBeat = BeatPosition.Preserve(2, 0, 4),
            Sound = "kick.wav",
            Volume = 73,
            Group = 9
        };
        var laneOne = new ChartNote { Lane = 1, Beat = BeatPosition.Preserve(1, 0, 4) };
        var repeatedLane = new ChartNote { Lane = 0, Beat = BeatPosition.Preserve(3, 0, 4) };
        var laneTwo = new ChartNote { Lane = 2, Beat = BeatPosition.Preserve(3, 0, 4) };
        var draft = new ChartNote
        {
            IsDraft = true,
            DraftColumn = -1,
            Lane = 0,
            Beat = BeatPosition.Preserve(4, 0, 4)
        };
        var untouched = new ChartNote { Lane = 3, Beat = BeatPosition.Preserve(5, 0, 4) };
        var session = SessionWithDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [hold, laneOne, repeatedLane, laneTwo, draft, untouched]
        });
        var original = session.Document.Clone();
        var result = NoteLaneRandomizationService.RandomizeSelection(
            session,
            [hold.Id, laneOne.Id, repeatedLane.Id, laneTwo.Id, draft.Id],
            NoteLaneRandomizationMode.FixedPermutation,
            new Random(20260731));

        var actualHold = session.Document.Notes.Single(note => note.Id == hold.Id);
        var actualRepeated = session.Document.Notes.Single(note => note.Id == repeatedLane.Id);
        var mapped = new[]
        {
            actualHold.Lane,
            session.Document.Notes.Single(note => note.Id == laneOne.Id).Lane,
            session.Document.Notes.Single(note => note.Id == laneTwo.Id).Lane
        };
        Require(
            result.SelectedCount == 4 &&
            result.RowCount == 2 &&
            result.ChangedCount > 0 &&
            actualHold.Lane == actualRepeated.Lane &&
            mapped.Distinct().Count() == mapped.Length,
            "Fixed randomization did not retain one bijective lane map across rows.");
        Require(
            actualHold.Beat.Equals(hold.Beat) &&
            actualHold.EndBeat == hold.EndBeat &&
            actualHold.Sound == hold.Sound &&
            actualHold.Volume == hold.Volume &&
            actualHold.Group == hold.Group &&
            session.Document.Notes.Single(note => note.Id == draft.Id).IsDraft &&
            session.Document.Notes.Single(note => note.Id == draft.Id).DraftColumn == -1 &&
            session.Document.Notes.Single(note => note.Id == untouched.Id).Lane == 3,
            "Lane randomization changed non-lane data, a draft, or an unselected Note.");

        var operationId = Guid.Parse("90136d77-7e10-4f43-a981-30c319bfc7c7");
        var batch = OnlineChartSyncCodec.CreateBatch(
            11,
            original,
            session.Document,
            operationId);
        var remote = OnlineChartSyncCodec.ApplyOperations(
            original,
            [
                new AppliedOnlineChartOperation(
                    12,
                    77,
                    batch,
                    DateTimeOffset.Parse("2026-07-31T14:00:00+08:00"))
            ]);
        Require(
            batch.OperationId == operationId &&
            batch.BaseRevision == 11 &&
            batch.UpsertNotes.Count == result.ChangedCount &&
            batch.DeleteNoteIds.Count == 0 &&
            batch.UpsertTiming.Count == 0 &&
            batch.Metadata == null &&
            !batch.KeyCount.HasValue &&
            ChartStructuralFingerprint.Compute(remote) ==
            ChartStructuralFingerprint.Compute(session.Document),
            "The existing room delta codec did not carry the randomized lane batch exactly.");

        Require(
            session.Undo() &&
            session.Document.Notes.Select(note => (note.Id, note.Lane, note.IsDraft, note.DraftColumn))
                .SequenceEqual(original.Notes.Select(note =>
                    (note.Id, note.Lane, note.IsDraft, note.DraftColumn))) &&
            !session.Undo() &&
            session.Redo(),
            "Fixed randomization was not exactly one undo/redo batch.");
        return $"selected={result.SelectedCount}; changed={result.ChangedCount}; " +
               $"rows={result.RowCount}; mapping={string.Join(',', mapped)}; " +
               $"roomUpserts={batch.UpsertNotes.Count}; singleUndo=true";
    }

    private static string VerifyPerBeatLaneRandomization()
    {
        var chord = Enumerable.Range(0, 4)
            .Select(lane => new ChartNote
            {
                Lane = lane,
                Beat = BeatPosition.Preserve(1, 0, 4)
            })
            .ToArray();
        var singles = Enumerable.Range(2, 4)
            .Select(bar => new ChartNote
            {
                Lane = 0,
                Beat = BeatPosition.Preserve(bar, 0, 4)
            })
            .ToArray();
        var session = SessionWithDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [.. chord, .. singles]
        });
        var result = NoteLaneRandomizationService.RandomizeSelection(
            session,
            session.Document.Notes.Select(note => note.Id),
            NoteLaneRandomizationMode.PerBeatPermutation,
            new Random(90210));
        var actualChord = session.Document.Notes
            .Where(note => note.Beat.Equals(BeatPosition.Preserve(1, 0, 4)))
            .Select(note => note.Lane)
            .OrderBy(lane => lane)
            .ToArray();
        Require(
            result.SelectedCount == 8 &&
            result.RowCount == 5 &&
            result.ChangedCount >= singles.Length &&
            actualChord.SequenceEqual([0, 1, 2, 3]) &&
            singles.All(source =>
                session.Document.Notes.Single(note => note.Id == source.Id).Lane != 0),
            "Per-beat randomization did not independently move rows without chord collisions.");
        Require(session.Undo() && !session.Undo(),
            "Per-beat randomization created more than one undo entry.");
        return $"selected={result.SelectedCount}; changed={result.ChangedCount}; " +
               $"rows={result.RowCount}; chordDistinct={actualChord.Length}";
    }

    private static string VerifyLaneRandomizationGuards()
    {
        var note = new ChartNote { Lane = 0, Beat = BeatPosition.Preserve(1, 0, 4) };
        var readOnly = SessionWithDocument(new ChartDocument { KeyCount = 4, Notes = [note.Clone()] });
        readOnly.IsReadOnly = true;
        Require(ThrowsInvalidOperation(() =>
            NoteLaneRandomizationService.RandomizeSelection(
                readOnly,
                [readOnly.Document.Notes[0].Id],
                NoteLaneRandomizationMode.FixedPermutation)),
            "Read-only lane randomization did not fail closed.");

        var noPermission =
            SessionWithDocument(new ChartDocument { KeyCount = 4, Notes = [note.Clone()] });
        noPermission.CanEditNotes = false;
        Require(ThrowsInvalidOperation(() =>
            NoteLaneRandomizationService.RandomizeSelection(
                noPermission,
                [noPermission.Document.Notes[0].Id],
                NoteLaneRandomizationMode.PerBeatPermutation)),
            "Note-permission lane randomization did not fail closed.");

        var locked = SessionWithDocument(new ChartDocument { KeyCount = 4, Notes = [note.Clone()] });
        locked.SetOwnerProtection([locked.Document.Notes[0].Id], isProtected: true);
        locked.AllowOwnerProtectedEdits = false;
        Require(
            ThrowsInvalidOperation(() =>
                NoteLaneRandomizationService.RandomizeSelection(
                    locked,
                    [locked.Document.Notes[0].Id],
                    NoteLaneRandomizationMode.FixedPermutation)) &&
            locked.ChangeSerial == 0 &&
            !locked.Undo(),
            "Owner-lock lane randomization mutated the chart or history.");

        var oneKey = SessionWithDocument(new ChartDocument { KeyCount = 1, Notes = [note.Clone()] });
        var unchanged = NoteLaneRandomizationService.RandomizeSelection(
            oneKey,
            [oneKey.Document.Notes[0].Id],
            NoteLaneRandomizationMode.PerBeatPermutation,
            new Random(1));
        Require(unchanged.ChangedCount == 0 && oneKey.ChangeSerial == 0 && !oneKey.Undo(),
            "A one-key randomization created a no-op history entry.");

        var collisionSource = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(2, 0, 4)
        };
        var blockingHold = new ChartNote
        {
            Lane = 1,
            Beat = BeatPosition.Preserve(1, 0, 4),
            EndBeat = BeatPosition.Preserve(3, 0, 4)
        };
        var collision = SessionWithDocument(new ChartDocument
        {
            KeyCount = 2,
            Notes = [blockingHold, collisionSource]
        });
        var blocked = NoteLaneRandomizationService.RandomizeSelection(
            collision,
            [collisionSource.Id],
            NoteLaneRandomizationMode.FixedPermutation,
            new Random(5));
        Require(
            blocked.CollisionBlocked &&
            blocked.ChangedCount == 0 &&
            collision.Document.Notes.Single(item => item.Id == collisionSource.Id).Lane == 0 &&
            collision.ChangeSerial == 0 &&
            !collision.Undo(),
            "Randomization moved a selected Note into an unselected LN.");
        return "readOnly=true; notePermission=true; ownerLock=true; oneKeyNoOp=true; " +
               "collisionBlocked=true";
    }

    private static string VerifyManualTempoTapAnalysis()
    {
        var taps = Enumerable.Range(0, 12)
            .Select(index => 125d + index * 500 + (index % 3 - 1) * 2)
            .ToArray();
        var result = ManualTempoTapAnalyzer.Analyze(taps);
        // OffsetMilliseconds is the chart Delay (beat 0 at -Delay ms). Taps
        // heard 125 ms into the audio therefore report -125; the old +125 put
        // beat 0 at -125 ms, a quarter-period phase error on every beat.
        Require(
            Math.Abs(result.Bpm - 120) < .15 &&
            Math.Abs(result.OffsetMilliseconds + 125) < 3 &&
            result.OnsetCount == taps.Length &&
            result.Instability < .02 &&
            result.Confidence > .5,
            "Manual tap analysis did not recover the expected BPM/phase.");
        Require(
            ThrowsInvalidOperation(() => ManualTempoTapAnalyzer.Analyze([0, 500, 1_000])),
            "Manual tap analysis accepted fewer than four taps.");
        return $"bpm={result.Bpm:0.###}; offset={result.OffsetMilliseconds:0.###}; " +
               $"instability={result.Instability:0.####}; taps={result.OnsetCount}";
    }

    private static EditorSession SessionWithDocument(ChartDocument document)
    {
        var session = new EditorSession();
        session.ReplaceDocument(document);
        return session;
    }

    private static bool ThrowsInvalidOperation(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string VerifyBackupRoundTrip(string isolationRoot)
    {
        var root = Path.Combine(isolationRoot, "backup-roundtrip");
        var original = Path.Combine(root, "source", "original.mc");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        var document = CreateBackupFixture(original);
        MalodyMcSerializer.Save(document, original);
        var sourceFingerprint = ChartStructuralFingerprint.Compute(document);
        var createdAt = new DateTimeOffset(2026, 7, 24, 10, 20, 30, TimeSpan.FromHours(8));
        var path = ChartBackupArchiveService.CreateBackup(root, document, original, createdAt);
        var loaded = ChartBackupArchiveService.LoadBackup(path);
        Require(loaded.OriginalPath != null && PathsEqual(loaded.OriginalPath, original),
            "Backup original path did not round-trip.");
        Require(loaded.CreatedAt == createdAt, "Backup timestamp did not round-trip.");
        Require(loaded.Document.UnknownRoot["_luma_backup"] == null,
            "Private backup marker leaked into restored chart data.");
        Require(string.Equals(
                ChartStructuralFingerprint.Compute(loaded.Document),
                sourceFingerprint,
                StringComparison.Ordinal),
            "Backup document structural fingerprint changed.");
        Require(document.UnknownRoot["_luma_backup"] == null,
            "Creating a backup mutated the live document.");
        return $"path={path}; created={loaded.CreatedAt:O}; sha={sourceFingerprint}; " +
               $"notes={loaded.Document.Notes.Count}";
    }

    private static string VerifyBackupUniquePaths(string isolationRoot)
    {
        var root = Path.Combine(isolationRoot, "backup-unique");
        var original = Path.Combine(root, "source.mc");
        var document = CreateBackupFixture(original);
        var stamp = new DateTimeOffset(2026, 7, 24, 10, 30, 0, TimeSpan.FromHours(8));
        var first = ChartBackupArchiveService.CreateBackup(root, document, original, stamp);
        var second = ChartBackupArchiveService.CreateBackup(root, document, original, stamp);
        Require(!PathsEqual(first, second) && File.Exists(first) && File.Exists(second),
            "Same-second backups overwrote one another.");
        Require(!Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
            "Backup write left a temporary file.");
        Require(Path.GetFileNameWithoutExtension(second).EndsWith("_2", StringComparison.Ordinal),
            "Unique backup suffix did not match WPF.");
        return $"first={Path.GetFileName(first)}; second={Path.GetFileName(second)}";
    }

    private static string VerifyProtectedCacheCleanup(string isolationRoot)
    {
        var root = Path.Combine(isolationRoot, "cache-cleanup");
        var protectedRoot = Path.Combine(root, "open-tab");
        var orphanRoot = Path.Combine(root, "closed-tab");
        Directory.CreateDirectory(protectedRoot);
        Directory.CreateDirectory(orphanRoot);
        var protectedFile = Path.Combine(protectedRoot, "working.mc");
        var orphanFile = Path.Combine(orphanRoot, "working.mc");
        File.WriteAllText(protectedFile, "open");
        File.WriteAllText(orphanFile, "closed");
        var result = ManagedStorageCleanupService.Clean(
        [
            new ManagedStorageCleanupTarget(root, [protectedRoot])
        ]);
        Require(File.Exists(protectedFile) && Directory.Exists(protectedRoot),
            "Open-tab cache was not protected.");
        Require(!File.Exists(orphanFile) && !Directory.Exists(orphanRoot),
            "Closed-tab cache was not reclaimed.");
        Require(result.FilesDeleted == 1 && result.DirectoriesDeleted == 1 &&
                result.BytesFreed == "closed".Length && result.Failures.Count == 0,
            "Cleanup result counters are incorrect.");
        return $"files={result.FilesDeleted}; dirs={result.DirectoriesDeleted}; " +
               $"bytes={result.BytesFreed}; protected={File.Exists(protectedFile)}";
    }

    private static ChartDocument CreateBackupFixture(string originalPath) => new()
    {
        FilePath = originalPath,
        KeyCount = 4,
        Metadata = new ChartMetadata
        {
            Title = "Backup Spec",
            Artist = "LumaChart",
            Creator = "Specs",
            Version = "Exact rational"
        },
        Notes =
        [
            new ChartNote
            {
                Lane = 2,
                Beat = BeatPosition.Preserve(3, 10, 12),
                EndBeat = BeatPosition.Preserve(4, 1, 3),
                Group = 7
            }
        ],
        Timing =
        [
            new TimingEvent
            {
                Beat = BeatPosition.Zero,
                Bpm = 137.5,
                Delay = 42
            }
        ]
    };

    private static void Check(
        ICollection<WindowsSettingsCacheSpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsSettingsCacheSpecResult(name, true, verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsSettingsCacheSpecResult(name, false, exception.Message));
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsSettingsCacheSpecResult(
    string Name,
    bool Passed,
    string Detail);

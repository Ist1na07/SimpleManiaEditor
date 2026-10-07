using System.Diagnostics;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using LumaChart.Application.Archives;
using LumaChart.Application.Editing;
using LumaChart.Application.Exporting;
using LumaChart.Application.Libraries;
using LumaChart.Application.Settings;
using LumaChart.Application.Validation;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Evidence;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.Verification;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using LumaChart.Core.Validation;
using LumaChart.Online;
using NAudio.Wave;

namespace LumaChart.Avalonia.Specs;

internal static class Program
{
    private const string DisplayScaleRuntimeSpecChildArgument =
        "--display-scale-runtime-spec-child";
    private const string RequiredFixturePath = @"D:\osu!\Songs\Resound\0\1781536331.mc";
    private const string RequiredFixtureSha256 =
        "A2828592A4898D35C1DD9FAC5905322760A152810EDCED60601D2F2C7B74E06E";
    private const string RequiredOsuFixturePath =
        @"D:\osu!\Songs\1005389 Minori Chihara - Michishirube\Minori Chihara - Michishirube (eZmmR) [Dear You,  Gilbert Bougainvillea .].osu";
    private const string RequiredOsuFixtureSha256 =
        "511866870F99BDC7443006FA7420C7A9D21198F49B9EC13B50A32293E7F58E57";
    private const string RequiredMczFixturePath = @"D:\Project\malody_tool\mc2osu\Infinity_Heaven.mcz";
    private const string RequiredMczFixtureSha256 =
        "03A50A7C7750CE4CB4C176B6A9BCB6193F7DC186C15D2C290971C3DE5748D3BD";
    private const string RequiredProEditorFixtureRelativePath =
        @"TestFixtures\malody-noteref-test.mc";
    private const string RequiredProEditorFixtureSha256 =
        "9EF6FD8A61A602DA651C5E4F5F1B5CF7DCC3D1992A76C4A953A9E67AB0B7305C";
    private const int BenchmarkNoteCount = 100_000;
    private const int BenchmarkSampleCount = 2_000;
    private const int MoveDivision = 4;
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 &&
            string.Equals(
                args[0],
                DisplayScaleRuntimeSpecChildArgument,
                StringComparison.Ordinal))
        {
            return args.Length == 2
                ? RunDisplayScaleRuntimeSpecChild(args[1])
                : 2;
        }

        var outputPath = ResolveOutputPath(args);
        var evidenceWriteFailed = false;
        var evidence = new SliceEvidence
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            Environment = new RuntimeEnvironmentEvidence
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Framework = RuntimeInformation.FrameworkDescription,
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString()
            },
            Source = new SourceEvidence { Path = RequiredFixturePath },
            OsuSource = new SourceEvidence { Path = RequiredOsuFixturePath },
            MczSource = new SourceEvidence { Path = RequiredMczFixturePath }
        };

        try
        {
            await RunSliceAsync(evidence).ConfigureAwait(false);
            evidence.Status = "passed";
        }
        catch (Exception exception)
        {
            evidence.Status = "failed";
            evidence.Failure = exception.ToString();
        }
        finally
        {
            try
            {
                await FinalizeSourceEvidenceAsync(
                    evidence,
                    evidence.Source,
                    "original-fixture-unchanged",
                    "The real MC source fixture").ConfigureAwait(false);
                await FinalizeSourceEvidenceAsync(
                    evidence,
                    evidence.OsuSource,
                    "original-osu-fixture-unchanged",
                    "The real osu source fixture").ConfigureAwait(false);
                await FinalizeSourceEvidenceAsync(
                    evidence,
                    evidence.MczSource,
                    "original-mcz-fixture-unchanged",
                    "The real MCZ source fixture").ConfigureAwait(false);

                // The three immutable real-fixture checks above are the final
                // checks in the established 268-check contract. Run new slices
                // only after them so every prior ordinal remains stable.
                if (string.Equals(evidence.Status, "passed", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(evidence.IsolationRoot))
                {
                    try
                    {
                        await VerifyCrossFormatFullPackageExportAsync(
                                evidence,
                                evidence.IsolationRoot)
                            .ConfigureAwait(false);
                        VerifyEvidenceNoteFocusContracts(evidence);
                        await VerifyFourModeChartExportContractsAsync(
                                evidence,
                                evidence.IsolationRoot)
                            .ConfigureAwait(false);
                        var accountResults = await WindowsAccountSessionSpecs.RunAsync(
                                 Path.Combine(evidence.IsolationRoot, "windows-account-session"))
                             .ConfigureAwait(false);
                        foreach (var result in accountResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var roomSyncResilience = await WindowsRoomSyncResilienceSpecs.RunAsync()
                            .ConfigureAwait(false);
                        Require(
                            evidence,
                            roomSyncResilience.Passed,
                            roomSyncResilience.Name,
                            roomSyncResilience.Detail);
                        var roomResourceResilience =
                            await WindowsRoomResourceResilienceSpecs.RunAsync(
                                    Path.Combine(
                                        evidence.IsolationRoot,
                                        "windows-room-resource-resilience"))
                                .ConfigureAwait(false);
                        Require(
                            evidence,
                            roomResourceResilience.Passed,
                            roomResourceResilience.Name,
                            roomResourceResilience.Detail);
                        var chartArchiveResilience =
                            await WindowsChartArchiveResilienceSpecs.RunAsync(
                                    Path.Combine(
                                        evidence.IsolationRoot,
                                        "windows-chart-archive-resilience"))
                                .ConfigureAwait(false);
                        Require(
                            evidence,
                            chartArchiveResilience.Passed,
                            chartArchiveResilience.Name,
                            chartArchiveResilience.Detail);
                        var metadataResults = WindowsMetadataEditorSpecs.Run(
                            Path.Combine(evidence.IsolationRoot, "windows-metadata-editor"));
                        foreach (var result in metadataResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var audioTempoResults = WindowsAudioTempoSpecs.Run(
                            Path.Combine(evidence.IsolationRoot, "windows-audio-tempo"));
                        foreach (var result in audioTempoResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var settingsCacheResults = WindowsSettingsCacheSpecs.Run(
                            Path.Combine(evidence.IsolationRoot, "windows-settings-cache"));
                        foreach (var result in settingsCacheResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var localizationResults = WindowsLocalizationSpecs.Run(
                            Path.Combine(evidence.IsolationRoot, "windows-localization"));
                        foreach (var result in localizationResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsManagedMp3DecoderSpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var pickedFileResults =
                            await WindowsPlatformPickedFileSpecs.RunAsync(
                                    Path.Combine(
                                        evidence.IsolationRoot,
                                        "windows-platform-picked-files"))
                                .ConfigureAwait(false);
                        foreach (var result in pickedFileResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var shortcutResults = WindowsShortcutSpecs.Run(
                            Path.Combine(evidence.IsolationRoot, "windows-shortcuts"));
                        foreach (var result in shortcutResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsNoteSoundPickerParitySpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var controlThemeResults = WindowsControlThemeSpecs.Run();
                        foreach (var result in controlThemeResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var dialogParityResults = WindowsDialogParitySpecs.Run();
                        foreach (var result in dialogParityResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var onlineSurfaceParityResults =
                            WindowsOnlineSurfaceParitySpecs.Run();
                        foreach (var result in onlineSurfaceParityResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsAppUpdatePolicySpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in await WindowsIssueReportSpecs.RunAsync()
                                     .ConfigureAwait(false))
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsAvaloniaInstallerSpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsFormatGuiHarnessSpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsRoomPermissionNoticeSpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var roomRolePresentation =
                            WindowsOnlineRoomRolePresentationSpecs.Run();
                        Require(
                            evidence,
                            roomRolePresentation.Passed,
                            roomRolePresentation.Name,
                            roomRolePresentation.Detail);
                        var singleInstance = await WindowsSingleInstanceSpecs.RunAsync(
                                Path.Combine(
                                    evidence.IsolationRoot,
                                    "windows-single-instance"))
                            .ConfigureAwait(false);
                        Require(
                            evidence,
                            singleInstance.Passed,
                            singleInstance.Name,
                            singleInstance.Detail);
                        foreach (var result in WindowsTimingEffectMemorySpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsOwnerLockEditParitySpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsOnlineCreatorMarkerSpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsLibraryFolderCardSpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsAudioTimingMapCacheSpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsLibraryLauncherSpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var saveFailureRecovery =
                            await WindowsSaveFailureRecoverySpecs.RunAsync(
                                    Path.Combine(
                                        evidence.IsolationRoot,
                                        "windows-save-failure-recovery"),
                                    RequiredFixturePath)
                                .ConfigureAwait(false);
                        Require(
                            evidence,
                            saveFailureRecovery.Passed,
                            saveFailureRecovery.Name,
                            saveFailureRecovery.Detail);
                        var audioLibraryImportResults =
                            await WindowsAudioLibraryImportSpecs.RunAsync(
                                    Path.Combine(
                                        evidence.IsolationRoot,
                                        "windows-audio-library-import"))
                                .ConfigureAwait(false);
                        foreach (var result in audioLibraryImportResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        VerifyMissingAudioCompatibilityRecovery(
                            evidence,
                            Path.Combine(
                                evidence.IsolationRoot,
                                "windows-missing-audio-recovery"));
                        var explicitStartupRecoveryResults =
                            await WindowsExplicitStartupRecoverySpecs.RunAsync(
                                    Path.Combine(
                                        evidence.IsolationRoot,
                                        "windows-explicit-startup-recovery"),
                                    RequiredFixturePath,
                                    RequiredOsuFixturePath)
                                .ConfigureAwait(false);
                        foreach (var result in explicitStartupRecoveryResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var titleBarParityResults = WindowsTitleBarParitySpecs.Run();
                        foreach (var result in titleBarParityResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var inspectorParityResults = WindowsInspectorParitySpecs.Run();
                        foreach (var result in inspectorParityResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in WindowsEditorSurfaceParitySpecs.Run())
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var difficultyResults = WindowsDifficultySpecs.Run(
                            Path.Combine(evidence.IsolationRoot, "windows-difficulty"));
                        foreach (var result in difficultyResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var playtestResults = WindowsPlaytestSpecs.Run(
                            Path.Combine(evidence.IsolationRoot, "windows-playtest"));
                        foreach (var result in playtestResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var osuInPlaceSaveResults =
                            await WindowsOsuInPlaceSaveSpecs.RunAsync(
                                    evidence.IsolationRoot,
                                    RequiredOsuFixturePath,
                                    RequiredOsuFixtureSha256)
                                .ConfigureAwait(false);
                        foreach (var result in osuInPlaceSaveResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        var diagnosticLogResults = WindowsDiagnosticLogSpecs.Run(
                            Path.Combine(
                                evidence.IsolationRoot,
                                "windows-diagnostic-log"));
                        foreach (var result in diagnosticLogResults)
                            Require(evidence, result.Passed, result.Name, result.Detail);
                        foreach (var result in await WindowsAudioVisualizationAlignmentSpecs.RunAsync(
                                         Path.Combine(
                                             evidence.IsolationRoot,
                                             "audio-visualization-alignment"))
                                     .ConfigureAwait(false))
                            Require(evidence, result.Passed, result.Name, result.Detail);
                    }
                    catch (Exception crossFormatException)
                    {
                        evidence.Status = "failed";
                        evidence.Failure = crossFormatException.ToString();
                    }
                }

                outputPath = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
                    ?? throw new InvalidOperationException("The evidence output path has no parent directory."));
                await File.WriteAllTextAsync(
                        outputPath,
                        JsonSerializer.Serialize(evidence, EvidenceJsonOptions))
                    .ConfigureAwait(false);
            }
            catch (Exception writeException)
            {
                Console.Error.WriteLine($"Unable to write evidence: {writeException}");
                evidenceWriteFailed = true;
            }
        }

        if (evidenceWriteFailed) return 2;
        Console.WriteLine($"Avalonia slice verification: {evidence.Status}");
        Console.WriteLine($"Evidence: {outputPath}");
        if (evidence.Failure != null) Console.Error.WriteLine(evidence.Failure);
        return string.Equals(evidence.Status, "passed", StringComparison.Ordinal) ? 0 : 1;
    }

    private static void VerifyMissingAudioCompatibilityRecovery(
        SliceEvidence evidence,
        string root)
    {
        var staleRoot = Path.Combine(root, "stale");
        var editingRoot = Path.Combine(root, "editing");
        var libraryRoot = Path.Combine(root, "library");
        var ambiguousRoot = Path.Combine(root, "ambiguous");
        Directory.CreateDirectory(staleRoot);
        Directory.CreateDirectory(editingRoot);
        Directory.CreateDirectory(Path.Combine(libraryRoot, "Song", "0"));
        Directory.CreateDirectory(Path.Combine(ambiguousRoot, "Other", "0"));

        const string chartName = "chart.mc";
        const string audioName = "music.ogg";
        var staleChart = Path.Combine(staleRoot, chartName);
        var workingChart = Path.Combine(editingRoot, "working.mc");
        var matchingChart = Path.Combine(libraryRoot, "Song", "0", chartName);
        var matchingAudio = Path.Combine(libraryRoot, "Song", "0", audioName);
        File.WriteAllText(staleChart, "same-chart", Encoding.UTF8);
        File.WriteAllText(workingChart, "editing-chart", Encoding.UTF8);
        File.Copy(staleChart, matchingChart);
        File.WriteAllBytes(matchingAudio, [1, 2, 3, 4]);
        File.WriteAllText(
            Path.Combine(ambiguousRoot, "Other", "0", chartName),
            "different-chart",
            Encoding.UTF8);
        File.WriteAllBytes(
            Path.Combine(ambiguousRoot, "Other", "0", audioName),
            [9, 8, 7, 6]);

        var found = ChartAssetImportService.FindRelatedAsset(
            workingChart,
            staleChart,
            audioName,
            [ambiguousRoot, libraryRoot],
            CancellationToken.None);
        var imported = found == null
            ? null
            : ChartAssetImportService.ImportAudio(
                workingChart,
                found,
                staleChart,
                preferredFileName: audioName);
        var resolved = ChartAssetImportService.ResolveAssetPath(
            workingChart,
            staleChart,
            audioName);
        Require(
            evidence,
            found != null &&
            PathsEqual(found, matchingAudio) &&
            imported?.FileName == audioName &&
            resolved != null &&
            PathsEqual(resolved, imported.DestinationPath),
            "windows-missing-audio-recovers-from-byte-identical-library-chart",
            $"found={found}; resolved={resolved}");
    }

    private static async Task RunSliceAsync(SliceEvidence evidence)
    {
        Require(
            evidence,
            File.Exists(RequiredFixturePath),
            "required-real-fixture-present",
            $"Required real MC fixture: {RequiredFixturePath}");
        Require(
            evidence,
            File.Exists(RequiredOsuFixturePath),
            "required-real-osu-fixture-present",
            $"Required real osu fixture: {RequiredOsuFixturePath}");
        Require(
            evidence,
            File.Exists(RequiredMczFixturePath),
            "required-real-mcz-fixture-present",
            $"Required real MCZ fixture: {RequiredMczFixturePath}");

        var sourceInfo = new FileInfo(RequiredFixturePath);
        evidence.Source.SizeBytes = sourceInfo.Length;
        evidence.Source.Sha256Before = await ComputeSha256Async(RequiredFixturePath).ConfigureAwait(false);
        Require(
            evidence,
            string.Equals(
                evidence.Source.Sha256Before,
                RequiredFixtureSha256,
                StringComparison.OrdinalIgnoreCase),
            "required-real-fixture-hash",
            evidence.Source.Sha256Before);

        var osuSourceInfo = new FileInfo(RequiredOsuFixturePath);
        evidence.OsuSource.SizeBytes = osuSourceInfo.Length;
        evidence.OsuSource.Sha256Before = await ComputeSha256Async(RequiredOsuFixturePath).ConfigureAwait(false);
        Require(
            evidence,
            string.Equals(
                evidence.OsuSource.Sha256Before,
                RequiredOsuFixtureSha256,
                StringComparison.OrdinalIgnoreCase),
            "required-real-osu-fixture-hash",
            evidence.OsuSource.Sha256Before);

        var mczSourceInfo = new FileInfo(RequiredMczFixturePath);
        evidence.MczSource.SizeBytes = mczSourceInfo.Length;
        evidence.MczSource.Sha256Before = await ComputeSha256Async(RequiredMczFixturePath).ConfigureAwait(false);
        Require(
            evidence,
            string.Equals(
                evidence.MczSource.Sha256Before,
                RequiredMczFixtureSha256,
                StringComparison.OrdinalIgnoreCase),
            "required-real-mcz-fixture-hash",
            evidence.MczSource.Sha256Before);

        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "LumaChart-Avalonia-Specs",
            $"run-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        var libraryRoot = Path.Combine(runRoot, "library");
        var copiedChartPath = Path.Combine(libraryRoot, "Resound", "0", "1781536331.mc");
        var editingRoot = Path.Combine(runRoot, "editing");
        Directory.CreateDirectory(Path.GetDirectoryName(copiedChartPath)!);
        File.Copy(RequiredFixturePath, copiedChartPath, overwrite: false);
        evidence.IsolationRoot = runRoot;
        evidence.IsolatedChartPath = copiedChartPath;

        var copiedHashBefore = await ComputeSha256Async(copiedChartPath).ConfigureAwait(false);
        Require(
            evidence,
            string.Equals(evidence.Source.Sha256Before, copiedHashBefore, StringComparison.OrdinalIgnoreCase),
            "isolated-copy-matches-source",
            copiedHashBefore);

        var controller = new AvaloniaWorkspaceController(libraryRoot, editingRoot, "AvaloniaSpecs");
        await controller.InitializeAsync().ConfigureAwait(false);
        var library = await controller.ScanAsync().ConfigureAwait(false);
        Require(
            evidence,
            library.Count == 1 && PathsEqual(library[0].ChartPath, copiedChartPath),
            "workspace-scan-real-mc",
            $"entries={library.Count}; title={(library.Count == 1 ? library[0].DisplayTitle : string.Empty)}");

        var tab = await controller.OpenAsync(copiedChartPath).ConfigureAwait(false);
        var session = tab.Session;
        evidence.OriginalChart = ChartStructuralFingerprint.Summarize(session.Document);
        Require(
            evidence,
            session.Document.Notes.Count > 2_000 && session.Document.Timing.Count > 0,
            "workspace-open-real-mc",
            $"notes={session.Document.Notes.Count}; timing={session.Document.Timing.Count}");

        var referencedAudioName = session.Document.Metadata.AudioFile;
        var isolatedAudioPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(copiedChartPath)!,
            referencedAudioName));
        await File.WriteAllBytesAsync(isolatedAudioPath, [0x4f, 0x67, 0x67, 0x53]).ConfigureAwait(false);
        var resolvedAudioPath = WindowsAudioTransportViewModel.ResolveDocumentAudioPath(tab);
        Require(
            evidence,
            resolvedAudioPath != null &&
            PathsEqual(resolvedAudioPath, isolatedAudioPath) &&
            !PathsEqual(Path.GetDirectoryName(resolvedAudioPath)!, tab.CacheDirectory),
            "windows-audio-ui-resolves-relative-assets-from-real-source-not-working-cache",
            $"reference={referencedAudioName}; resolved={resolvedAudioPath}");

        var originalFingerprint = ChartStructuralFingerprint.Compute(session.Document);
        var movementTarget = SelectMovementTarget(session.Document);
        var originalBeat = movementTarget.Beat;
        var expectedBeat = BasicChartEditingService.ApplyRelativeMove(
            originalBeat,
            new RelativeBeatMove(1, MoveDivision));
        var reducedDivisor = GreatestCommonDivisor(expectedBeat.Numerator, expectedBeat.Denominator);
        Require(
            evidence,
            originalBeat.Denominator > 1 && expectedBeat.Numerator != 0 && reducedDivisor > 1,
            "exact-beat-test-is-discriminating",
            $"before={originalBeat}; expected={expectedBeat}; reducibleBy={reducedDivisor}");

        session.SnapDivision = MoveDivision;
        session.Selection.Clear();
        session.Selection.Add(movementTarget.Id);
        var movedCount = BasicChartEditingService.NudgeSelection(session, direction: 1);
        Require(evidence, movedCount == 1, "move-one-selected-note", $"moved={movedCount}");

        var movedNote = session.Document.Notes.Single(note => note.Id == movementTarget.Id);
        Require(
            evidence,
            HasExactExpression(movedNote.Beat, expectedBeat),
            "relative-move-preserves-rational-expression",
            $"actual={movedNote.Beat}; expected={expectedBeat}");
        evidence.ExactBeatMove = new ExactBeatMoveEvidence
        {
            SnapDivision = MoveDivision,
            Before = BeatExpression.From(originalBeat),
            Expected = BeatExpression.From(expectedBeat),
            Actual = BeatExpression.From(movedNote.Beat),
            MathematicalReductionDivisor = reducedDivisor,
            PreservedUnreducedExpression = movedNote.Beat.Denominator == expectedBeat.Denominator && reducedDivisor > 1
        };
        var movedFingerprint = ChartStructuralFingerprint.Compute(session.Document);
        evidence.MovedChart = ChartStructuralFingerprint.Summarize(session.Document);

        Require(evidence, session.Undo(), "undo-move", "Undo returned true.");
        var undoneNote = session.Document.Notes.Single(note => note.Id == movementTarget.Id);
        Require(
            evidence,
            HasExactExpression(undoneNote.Beat, originalBeat) &&
            string.Equals(
                ChartStructuralFingerprint.Compute(session.Document),
                originalFingerprint,
                StringComparison.Ordinal),
            "undo-restores-structured-content",
            $"restoredBeat={undoneNote.Beat}");

        Require(evidence, session.Redo(), "redo-move", "Redo returned true.");
        var redoneNote = session.Document.Notes.Single(note => note.Id == movementTarget.Id);
        Require(
            evidence,
            HasExactExpression(redoneNote.Beat, expectedBeat) &&
            string.Equals(
                ChartStructuralFingerprint.Compute(session.Document),
                movedFingerprint,
                StringComparison.Ordinal),
            "redo-restores-exact-move",
            $"redoneBeat={redoneNote.Beat}");

        var saveResult = await controller.SaveAsync(tab.Id).ConfigureAwait(false);
        Require(
            evidence,
            saveResult.Saved,
            "save-isolated-real-mc",
            $"status={saveResult.Status}; issues={saveResult.Issues.Count}");

        await using (var savedStream = new FileStream(
                         copiedChartPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var imported = await ChartDocumentImporter.ImportAsync(
                    savedStream,
                    copiedChartPath,
                    controller.DefaultCreator)
                .ConfigureAwait(false);
            evidence.ReimportedChart = ChartStructuralFingerprint.Summarize(imported);
            Require(
                evidence,
                string.Equals(
                    ChartStructuralFingerprint.Compute(imported),
                    movedFingerprint,
                    StringComparison.Ordinal),
                "save-reimport-structured-match",
                evidence.ReimportedChart.Sha256);
        }

        Require(
            evidence,
            await controller.CloseAsync(tab.Id, discardUnsavedChanges: false).ConfigureAwait(false),
            "close-saved-tab",
            "Saved tab closed without discard.");
        var reopened = await controller.OpenAsync(copiedChartPath).ConfigureAwait(false);
        evidence.ReopenedChart = ChartStructuralFingerprint.Summarize(reopened.Session.Document);
        Require(
            evidence,
            string.Equals(
                ChartStructuralFingerprint.Compute(reopened.Session.Document),
                movedFingerprint,
                StringComparison.Ordinal),
            "close-reopen-structured-match",
            evidence.ReopenedChart.Sha256);

        var newTab = await controller.CreateAsync("Avalonia Slice New Chart").ConfigureAwait(false);
        Require(
            evidence,
            File.Exists(newTab.SourcePath) &&
            newTab.Format == DesktopChartFormat.MalodyMc &&
            newTab.Session.Document.Notes.Count == 0,
            "create-new-mc-chart",
            newTab.SourcePath);
        await controller.PersistAsync().ConfigureAwait(false);

        ValidateWpfNewChartPath(evidence, newTab.SourcePath, libraryRoot);
        var secondLibraryRoot = Path.Combine(runRoot, "second-library");
        await VerifyExternalOsuAndRootSwitchAsync(
                evidence,
                controller,
                newTab,
                secondLibraryRoot)
            .ConfigureAwait(false);
        await VerifyProjectDirectoryGatesAsync(evidence, controller, runRoot).ConfigureAwait(false);
        await VerifyDesktopRecoveryAndCloseAsync(evidence, runRoot).ConfigureAwait(false);
        await VerifySaveAsAsync(evidence, runRoot).ConfigureAwait(false);
        await VerifyArchiveAndLibraryAsync(
                evidence,
                controller,
                secondLibraryRoot,
                runRoot)
            .ConfigureAwait(false);
        await VerifyMalodyFullPackageExportAsync(evidence, runRoot).ConfigureAwait(false);
        await VerifyOsuFullPackageExportAsync(evidence, runRoot).ConfigureAwait(false);

        VerifyWindowsLibraryContentPolicy(evidence);
        VerifyWindowsLibrarySurfacePolicy(evidence);
        VerifyWindowsPasswordInputPolicy(evidence);
        VerifyOnlineRoomHeaderPresentation(evidence);
        VerifyDesktopLibraryDeletionPlanner(evidence, runRoot);
        VerifyDesktopLibraryChartDeletionPlanner(evidence, runRoot);
        VerifyWindowsLibraryDeletionInfrastructure(evidence, runRoot);
        VerifyDesktopChartValidationPresentation(evidence);
        VerifyIndexedMarqueeSelection(evidence);
        VerifyTimelineToolContracts(evidence);
        VerifyNoteTypeTransformContracts(evidence);
        VerifyNotePropertyContracts(evidence, runRoot);
        await VerifyGroupManagerContractsAsync(evidence, runRoot).ConfigureAwait(false);
        await VerifyProEditorContractsAsync(evidence, runRoot).ConfigureAwait(false);
        VerifyTimelineTimingContracts(evidence);
        VerifyMixedEditorCommandContracts(evidence);
        VerifyWindowsDisplayScaleContracts(evidence, runRoot);
        await VerifyAudioVisualizationContractsAsync(evidence, runRoot).ConfigureAwait(false);
        await VerifyAudioPlaybackEpochContractsAsync(evidence, runRoot).ConfigureAwait(false);
        await VerifyWindowsKeySoundPlannerContractsAsync(evidence, runRoot).ConfigureAwait(false);
        await VerifyWindowsKeySoundRuntimeP1ContractsAsync(evidence, runRoot).ConfigureAwait(false);
        await VerifyWindowsAudioTransportContractsAsync(evidence, runRoot).ConfigureAwait(false);

        evidence.IndexBenchmark = RunTimelineIndexBenchmark();
        Require(
            evidence,
            evidence.IndexBenchmark.IndexedNotes == BenchmarkNoteCount &&
            evidence.IndexBenchmark.QueryMilliseconds.Samples == BenchmarkSampleCount &&
            evidence.IndexBenchmark.HitTestMilliseconds.Samples == BenchmarkSampleCount &&
            evidence.IndexBenchmark.HitCount == BenchmarkSampleCount,
            "timeline-index-100000-note-benchmark",
            $"queryP95={evidence.IndexBenchmark.QueryMilliseconds.P95:F4}ms; " +
            $"hitP95={evidence.IndexBenchmark.HitTestMilliseconds.P95:F4}ms; " +
            $"maxVisible={evidence.IndexBenchmark.MaximumReturnedVisibleNotes}");

    }

    private static async Task VerifyCrossFormatFullPackageExportAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        static void CopyDirectory(string sourceRoot, string destinationRoot)
        {
            foreach (var sourcePath in Directory.EnumerateFiles(
                         sourceRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                var destinationPath = Path.Combine(
                    destinationRoot,
                    Path.GetRelativePath(sourceRoot, sourcePath));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
                                          ?? throw new InvalidOperationException(
                                              "The copied cross-format fixture has no parent directory."));
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
        }

        static async Task<Dictionary<string, string>> CaptureFileTreeAsync(string root)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                result[relative] =
                    $"{info.Length.ToString(CultureInfo.InvariantCulture)}|" +
                    $"{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}|" +
                    await ComputeSha256Async(path).ConfigureAwait(false);
            }

            return result;
        }

        static bool TreesEqual(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var value) &&
                             string.Equals(pair.Value, value, StringComparison.Ordinal));

        static bool ResourceExists(string chartPath, string reference, bool required)
        {
            if (string.IsNullOrWhiteSpace(reference)) return !required;
            var resourcePath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(chartPath)
                ?? throw new InvalidOperationException("The imported chart has no directory."),
                reference));
            return File.Exists(resourcePath);
        }

        static async Task<(int ReferencedCount, int ResolvedCount, string[] Hashes)>
            CaptureReferencedResourceHashesAsync(
            string chartPath,
            ChartDocument document)
        {
            var references = new[]
                {
                    document.Metadata.AudioFile,
                    document.Metadata.CoverFile,
                    document.Metadata.BackgroundFile
                }
                .Concat(document.Notes.Select(note => note.Sound ?? string.Empty))
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .ToArray();
            var hashes = new List<string>(references.Length);
            foreach (var reference in references)
            {
                string resourcePath;
                try
                {
                    resourcePath = Path.IsPathRooted(reference)
                        ? Path.GetFullPath(reference)
                        : Path.GetFullPath(Path.Combine(
                            Path.GetDirectoryName(chartPath)
                            ?? throw new InvalidOperationException("The chart has no directory."),
                            reference));
                }
                catch (Exception exception) when (
                    exception is ArgumentException or IOException or NotSupportedException)
                {
                    continue;
                }

                if (!File.Exists(resourcePath)) continue;
                hashes.Add(await ComputeSha256Async(resourcePath).ConfigureAwait(false));
            }

            hashes.Sort(StringComparer.Ordinal);
            return (references.Length, hashes.Count, hashes.ToArray());
        }

        static async Task<HashSet<string>> CapturePackagedResourceHashesAsync(string chartPath)
        {
            var root = Path.GetDirectoryName(chartPath)
                       ?? throw new InvalidOperationException("The imported chart has no directory.");
            var hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(path =>
                             !Path.GetExtension(path).Equals(".mc", StringComparison.OrdinalIgnoreCase) &&
                             !Path.GetExtension(path).Equals(".osu", StringComparison.OrdinalIgnoreCase)))
            {
                hashes.Add(await ComputeSha256Async(path).ConfigureAwait(false));
            }

            return hashes;
        }

        static bool HasNoOwnedScratchChildren(string scratchRoot)
        {
            var exportsRoot = Path.Combine(scratchRoot, "exports");
            return Directory.Exists(exportsRoot) &&
                   !Directory.EnumerateDirectories(
                           exportsRoot,
                           "*",
                           SearchOption.TopDirectoryOnly)
                       .Any();
        }

        static bool HasNoTemporaryArchive(string destinationRoot) =>
            !Directory.EnumerateFiles(
                    destinationRoot,
                    "*.luma-export.tmp",
                    SearchOption.TopDirectoryOnly)
                .Any();

        var crossFormatRoot = Path.Combine(runRoot, "cross-format-full-package-export");

        // Real MC -> OSZ. The source folder deliberately contains same-format
        // siblings plus every chart/archive decoy that must not leak into an OSZ.
        var mcToOszRoot = Path.Combine(crossFormatRoot, "mc-to-osz");
        var mcSourceRoot = Path.Combine(mcToOszRoot, "source");
        var realMcRoot = Path.GetDirectoryName(RequiredFixturePath)
                         ?? throw new InvalidOperationException("The real MC fixture has no directory.");
        CopyDirectory(realMcRoot, mcSourceRoot);
        var mcSourcePath = Path.Combine(mcSourceRoot, Path.GetFileName(RequiredFixturePath));
        File.Copy(mcSourcePath, Path.Combine(mcSourceRoot, "cross-format-sibling.mc"), overwrite: false);
        await File.WriteAllTextAsync(Path.Combine(mcSourceRoot, "cross-format-decoy.osu"), "decoy osu")
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(mcSourceRoot, "cross-format-decoy.mcz"), [1, 2, 3])
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(mcSourceRoot, "cross-format-decoy.osz"), [4, 5, 6])
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(mcSourceRoot, "cross-format-decoy.luma-part"), [7, 8, 9])
            .ConfigureAwait(false);
        var mcSourceTreeBefore = await CaptureFileTreeAsync(mcSourceRoot).ConfigureAwait(false);

        var mcController = new AvaloniaWorkspaceController(
            mcSourceRoot,
            Path.Combine(mcToOszRoot, "editing"),
            "AvaloniaMcToOszSpecs");
        await mcController.InitializeAsync().ConfigureAwait(false);
        var mcTab = await mcController.OpenAsync(mcSourcePath).ConfigureAwait(false);
        var mcUniqueNote = mcTab.Session.Document.Notes
            .Where(note => note.EndBeat is null)
            .GroupBy(note => note.Beat)
            .First(group => group.Count() == 1)
            .Single();
        var mcMovedBeat = mcUniqueNote.Beat;
        var mcOriginalLane = mcUniqueNote.Lane;
        var mcMovedLane = (mcOriginalLane + 1) % mcTab.Session.Document.KeyCount;
        var mcExportedVersion = mcTab.Session.Document.Metadata.Version + " MC to OSZ Cross Spec";
        mcTab.Session.Mutate(document =>
        {
            document.Metadata.Version = mcExportedVersion;
            document.Notes.Single(note => note.Id == mcUniqueNote.Id).Lane = mcMovedLane;
        });
        var mcDirtyBefore = mcTab.Session.IsDirty;
        var mcSerialBefore = mcTab.Session.ChangeSerial;
        var mcFingerprintBefore = ChartStructuralFingerprint.Compute(mcTab.Session.Document);
        var mcDocumentPathBefore = mcTab.Session.Document.FilePath;
        var mcSourcePathBefore = mcTab.SourcePath;
        var mcWorkingPathBefore = mcTab.WorkingPath;
        var mcExpectedLaneCounts = Enumerable.Range(0, mcTab.Session.Document.KeyCount)
            .Select(lane => mcTab.Session.Document.Notes.Count(note => note.Lane == lane))
            .ToArray();
        var mcExpectedHoldCount = mcTab.Session.Document.Notes.Count(note => note.IsHold);
        var mcExpectedBpmTimingCount = mcTab.Session.Document.Timing.Count(timing => timing.Bpm.HasValue);
        var mcSourceResourceHashes = await CaptureReferencedResourceHashesAsync(
                mcTab.WorkingPath,
                mcTab.Session.Document)
            .ConfigureAwait(false);
        var mcWorkingTreeBefore = await CaptureFileTreeAsync(mcTab.CacheDirectory).ConfigureAwait(false);
        Require(
            evidence,
            Path.GetExtension(mcSourcePath).Equals(".mc", StringComparison.OrdinalIgnoreCase) &&
            Directory.EnumerateFiles(mcSourceRoot, "*.mc", SearchOption.TopDirectoryOnly).Count() == 2 &&
            mcDirtyBefore && mcSerialBefore == 1 && mcMovedLane != mcOriginalLane,
            "cross-format-mc-to-osz-real-dirty-source-is-discriminating",
            $"source={Path.GetExtension(mcSourcePath)}; siblings=2; dirty={mcDirtyBefore}; " +
            $"serial={mcSerialBefore}; beat={mcMovedBeat}; lane={mcOriginalLane}->{mcMovedLane}");

        var mcScratchRoot = Path.Combine(mcToOszRoot, "session-scratch");
        var mcScratchExportsRoot = Path.Combine(mcScratchRoot, "exports");
        Directory.CreateDirectory(mcScratchExportsRoot);
        var mcScratchSentinel = Path.Combine(mcScratchExportsRoot, "keep.sentinel");
        await File.WriteAllTextAsync(mcScratchSentinel, "keep").ConfigureAwait(false);
        var mcOutputRoot = Path.Combine(mcToOszRoot, "output");
        Directory.CreateDirectory(mcOutputRoot);
        var mcDestination = Path.Combine(mcOutputRoot, "cross-format.osz");
        byte[] oldMcDestinationBytes = [0x4F, 0x4C, 0x44, 0x2D, 0x4F, 0x53, 0x5A];
        await File.WriteAllBytesAsync(mcDestination, oldMcDestinationBytes).ConfigureAwait(false);
        const double mcExpectedOd = 8.75;
        const double mcExpectedHp = 3.25;
        var mcResult = await OsuFullPackageExportService.ExportAsync(
                new OsuFullPackageExportRequest(
                    mcTab.Session.CaptureCommittedDocument(),
                    mcTab.SourcePath,
                    mcDestination,
                    mcScratchRoot,
                    mcExpectedOd,
                    mcExpectedHp))
            .ConfigureAwait(false);

        string[] mcArchiveNames;
        string mcExportedText;
        using (var archive = ZipFile.OpenRead(mcDestination))
        {
            var entries = archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)).ToArray();
            mcArchiveNames = entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            var chartEntry = entries.Single(entry =>
                Path.GetExtension(entry.FullName).Equals(".osu", StringComparison.OrdinalIgnoreCase));
            await using var input = chartEntry.Open();
            using var reader = new StreamReader(input, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
            mcExportedText = await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        var mcTimingMap = new TimingMap(mcTab.Session.Document.Timing);
        var mcExpectedHitObjectTime = (int)Math.Round(mcTimingMap.BeatToMilliseconds(mcMovedBeat));
        var mcExpectedHitObjectX = Math.Clamp(
            (int)Math.Round((mcMovedLane + .5) * 512 / mcTab.Session.Document.KeyCount),
            0,
            511);
        var mcOriginalHitObjectX = Math.Clamp(
            (int)Math.Round((mcOriginalLane + .5) * 512 / mcTab.Session.Document.KeyCount),
            0,
            511);
        var mcExpectedHitObjectPrefix = $"{mcExpectedHitObjectX},192,{mcExpectedHitObjectTime},1,0,";
        var mcOriginalHitObjectPrefix = $"{mcOriginalHitObjectX},192,{mcExpectedHitObjectTime},1,0,";
        var mcExportedLines = mcExportedText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var mcSerializedMovedTapCount = mcExportedLines.Count(line =>
            line.StartsWith(mcExpectedHitObjectPrefix, StringComparison.Ordinal));
        var mcSerializedOriginalTapCount = mcExportedLines.Count(line =>
            line.StartsWith(mcOriginalHitObjectPrefix, StringComparison.Ordinal));
        var mcTargetChartCount = mcArchiveNames.Count(name =>
            Path.GetExtension(name).Equals(".osu", StringComparison.OrdinalIgnoreCase));
        var mcLeakedChartArtifact = mcArchiveNames.Any(name =>
            Path.GetExtension(name).Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".mcz", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".osz", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".mc_", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase));
        Require(
            evidence,
            mcTargetChartCount == 1 && !mcLeakedChartArtifact &&
            mcResult.MainChartEntryPath.Equals(
                mcArchiveNames.Single(name =>
                    Path.GetExtension(name).Equals(".osu", StringComparison.OrdinalIgnoreCase)),
                StringComparison.OrdinalIgnoreCase) &&
            mcResult.EntryCount == mcArchiveNames.Length,
            "cross-format-mc-to-osz-emits-one-target-chart-and-excludes-all-sibling-decoys",
            $"targetCharts={mcTargetChartCount}; leaked={mcLeakedChartArtifact}; " +
            $"main={mcResult.MainChartEntryPath}; entries={mcResult.EntryCount}");

        var mcReverseImported = await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                mcDestination,
                Path.Combine(mcToOszRoot, "reverse-import"))
            .ConfigureAwait(false);
        var mcFreshPath = mcReverseImported.ChartPaths.Single();
        ChartDocument mcFreshDocument;
        await using (var input = new FileStream(
                         mcFreshPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            mcFreshDocument = await ChartDocumentImporter.ImportAsync(
                    input,
                    mcFreshPath,
                    mcController.DefaultCreator)
                .ConfigureAwait(false);
        }
        var mcFreshLaneCounts = Enumerable.Range(0, mcFreshDocument.KeyCount)
            .Select(lane => mcFreshDocument.Notes.Count(note => note.Lane == lane))
            .ToArray();
        var mcDirtyLaneMoveRetained =
            mcFreshDocument.KeyCount == mcTab.Session.Document.KeyCount &&
            mcFreshLaneCounts.SequenceEqual(mcExpectedLaneCounts);
        var mcFreshResourceHashes = await CaptureReferencedResourceHashesAsync(
                mcFreshPath,
                mcFreshDocument)
            .ConfigureAwait(false);
        var mcFreshPackageResourceHashes = await CapturePackagedResourceHashesAsync(mcFreshPath)
            .ConfigureAwait(false);
        var mcResourceBytesRetained =
            mcSourceResourceHashes.ResolvedCount > 0 &&
            mcFreshResourceHashes.ReferencedCount == mcFreshResourceHashes.ResolvedCount &&
            mcSourceResourceHashes.Hashes.All(mcFreshPackageResourceHashes.Contains) &&
            mcFreshResourceHashes.Hashes.All(mcFreshPackageResourceHashes.Contains);
        Require(
            evidence,
            mcReverseImported.ChartPaths.Count == 1 &&
            mcFreshDocument.Metadata.Version == mcExportedVersion &&
            Math.Abs(mcFreshDocument.Metadata.OverallDifficulty - mcExpectedOd) < .000001 &&
            Math.Abs(mcFreshDocument.Metadata.HpDrainRate - mcExpectedHp) < .000001 &&
            mcFreshDocument.Notes.Count == mcTab.Session.Document.Notes.Count &&
            mcDirtyLaneMoveRetained &&
            mcSerializedMovedTapCount == 1 && mcSerializedOriginalTapCount == 0 &&
            mcFreshDocument.Notes.Count(note => note.IsHold) == mcExpectedHoldCount &&
            mcFreshDocument.Timing.Count(timing => timing.Bpm.HasValue) == mcExpectedBpmTimingCount &&
            mcResourceBytesRetained &&
            ResourceExists(mcFreshPath, mcFreshDocument.Metadata.AudioFile, required: true) &&
            ResourceExists(mcFreshPath, mcFreshDocument.Metadata.CoverFile, required: false) &&
            ResourceExists(mcFreshPath, mcFreshDocument.Metadata.BackgroundFile, required: false) &&
            mcExportedText.Contains("osu file format v14", StringComparison.Ordinal) &&
            mcExportedText.Contains("OverallDifficulty:8.75", StringComparison.Ordinal) &&
            mcExportedText.Contains("HPDrainRate:3.25", StringComparison.Ordinal),
            "cross-format-mc-to-osz-retains-dirty-snapshot-difficulty-and-resources",
            $"version={mcFreshDocument.Metadata.Version}; od={mcFreshDocument.Metadata.OverallDifficulty}; " +
            $"hp={mcFreshDocument.Metadata.HpDrainRate}; moved={mcDirtyLaneMoveRetained}; " +
            $"serializedTap={mcExpectedHitObjectX}@{mcExpectedHitObjectTime}:" +
            $"{mcSerializedMovedTapCount}; originalTap={mcSerializedOriginalTapCount}; " +
            $"holds={mcExpectedHoldCount}; bpmTiming={mcExpectedBpmTimingCount}; " +
            $"resourceBytes={mcResourceBytesRetained}; " +
            $"sourceResources={mcSourceResourceHashes.ResolvedCount}/{mcSourceResourceHashes.ReferencedCount}; " +
            $"freshResources={mcFreshResourceHashes.ResolvedCount}/{mcFreshResourceHashes.ReferencedCount}; " +
            $"audio={mcFreshDocument.Metadata.AudioFile}");

        var mcSourceTreeAfter = await CaptureFileTreeAsync(mcSourceRoot).ConfigureAwait(false);
        var mcWorkingTreeAfter = await CaptureFileTreeAsync(mcTab.CacheDirectory).ConfigureAwait(false);
        var mcSessionUnchanged =
            mcTab.Session.IsDirty == mcDirtyBefore &&
            mcTab.Session.ChangeSerial == mcSerialBefore &&
            string.Equals(
                ChartStructuralFingerprint.Compute(mcTab.Session.Document),
                mcFingerprintBefore,
                StringComparison.Ordinal) &&
            string.Equals(mcTab.Session.Document.FilePath, mcDocumentPathBefore, StringComparison.Ordinal) &&
            PathsEqual(mcTab.SourcePath, mcSourcePathBefore) &&
            PathsEqual(mcTab.WorkingPath, mcWorkingPathBefore);
        var mcOriginalFixturePostCrossSha = await ComputeSha256Async(RequiredFixturePath)
            .ConfigureAwait(false);
        Require(
            evidence,
            mcSessionUnchanged &&
            TreesEqual(mcSourceTreeBefore, mcSourceTreeAfter) &&
            TreesEqual(mcWorkingTreeBefore, mcWorkingTreeAfter) &&
            File.Exists(mcScratchSentinel) && HasNoOwnedScratchChildren(mcScratchRoot) &&
            HasNoTemporaryArchive(mcOutputRoot) &&
            mcOriginalFixturePostCrossSha.Equals(RequiredFixtureSha256, StringComparison.Ordinal) &&
            !oldMcDestinationBytes.SequenceEqual(await File.ReadAllBytesAsync(mcDestination).ConfigureAwait(false)),
            "cross-format-mc-to-osz-preserves-session-source-working-and-atomic-scratch",
            $"session={mcSessionUnchanged}; source={TreesEqual(mcSourceTreeBefore, mcSourceTreeAfter)}; " +
            $"working={TreesEqual(mcWorkingTreeBefore, mcWorkingTreeAfter)}; " +
            $"scratch={HasNoOwnedScratchChildren(mcScratchRoot)}; temp={HasNoTemporaryArchive(mcOutputRoot)}; " +
            $"original={mcOriginalFixturePostCrossSha}");

        // Real osu -> MCZ. Two real osu siblings plus a valid MC sibling and
        // archive decoys prove the inverse filter. An unreduced injected beat
        // proves the in-memory snapshot reaches the compact MC payload exactly.
        var osuToMczRoot = Path.Combine(crossFormatRoot, "osu-to-mcz");
        var osuSourceRoot = Path.Combine(osuToMczRoot, "source");
        var realOsuRoot = Path.GetDirectoryName(RequiredOsuFixturePath)
                          ?? throw new InvalidOperationException("The real osu fixture has no directory.");
        CopyDirectory(realOsuRoot, osuSourceRoot);
        var osuSourcePath = Path.Combine(osuSourceRoot, Path.GetFileName(RequiredOsuFixturePath));
        File.Copy(RequiredFixturePath, Path.Combine(osuSourceRoot, "cross-format-decoy.mc"), overwrite: false);
        await File.WriteAllBytesAsync(Path.Combine(osuSourceRoot, "cross-format-decoy.mcz"), [1, 2, 3])
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(osuSourceRoot, "cross-format-decoy.osz"), [4, 5, 6])
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(osuSourceRoot, "cross-format-decoy.luma-part"), [7, 8, 9])
            .ConfigureAwait(false);
        var osuSourceTreeBefore = await CaptureFileTreeAsync(osuSourceRoot).ConfigureAwait(false);

        var osuController = new AvaloniaWorkspaceController(
            osuSourceRoot,
            Path.Combine(osuToMczRoot, "editing"),
            "AvaloniaOsuToMczSpecs");
        await osuController.InitializeAsync().ConfigureAwait(false);
        var osuTab = await osuController.OpenAsync(osuSourcePath).ConfigureAwait(false);
        var injectedId = Guid.NewGuid();
        var injectedBeat = BeatPosition.Preserve(
            osuTab.Session.Document.Notes.Select(note => note.Beat.Bar).DefaultIfEmpty().Max() + 8,
            10,
            12);
        var injectedLane = Math.Min(2, osuTab.Session.Document.KeyCount - 1);
        var osuExportedVersion = osuTab.Session.Document.Metadata.Version + " OSU to MCZ Cross Spec";
        osuTab.Session.Mutate(document =>
        {
            document.Metadata.Version = osuExportedVersion;
            document.Notes.Add(new ChartNote
            {
                Id = injectedId,
                Lane = injectedLane,
                Beat = injectedBeat
            });
        });
        var osuDirtyBefore = osuTab.Session.IsDirty;
        var osuSerialBefore = osuTab.Session.ChangeSerial;
        var osuFingerprintBefore = ChartStructuralFingerprint.Compute(osuTab.Session.Document);
        var osuDocumentPathBefore = osuTab.Session.Document.FilePath;
        var osuSourcePathBefore = osuTab.SourcePath;
        var osuWorkingPathBefore = osuTab.WorkingPath;
        var osuWorkingTreeBefore = await CaptureFileTreeAsync(osuTab.CacheDirectory).ConfigureAwait(false);
        var osuExpectedHoldCount = osuTab.Session.Document.Notes.Count(note => note.IsHold);
        var osuSourceResourceHashes = await CaptureReferencedResourceHashesAsync(
                osuTab.WorkingPath,
                osuTab.Session.Document)
            .ConfigureAwait(false);
        Require(
            evidence,
            Path.GetExtension(osuSourcePath).Equals(".osu", StringComparison.OrdinalIgnoreCase) &&
            Directory.EnumerateFiles(osuSourceRoot, "*.osu", SearchOption.TopDirectoryOnly).Count() == 2 &&
            Directory.EnumerateFiles(osuSourceRoot, "*.mc", SearchOption.TopDirectoryOnly).Count() == 1 &&
            osuDirtyBefore && osuSerialBefore == 1 &&
            injectedBeat.Numerator == 10 && injectedBeat.Denominator == 12,
            "cross-format-osu-to-mcz-real-dirty-source-is-discriminating",
            $"source={Path.GetExtension(osuSourcePath)}; osuSiblings=2; mcDecoys=1; " +
            $"dirty={osuDirtyBefore}; serial={osuSerialBefore}; beat={injectedBeat}");

        var osuScratchRoot = Path.Combine(osuToMczRoot, "session-scratch");
        var osuScratchExportsRoot = Path.Combine(osuScratchRoot, "exports");
        Directory.CreateDirectory(osuScratchExportsRoot);
        var osuScratchSentinel = Path.Combine(osuScratchExportsRoot, "keep.sentinel");
        await File.WriteAllTextAsync(osuScratchSentinel, "keep").ConfigureAwait(false);
        var osuOutputRoot = Path.Combine(osuToMczRoot, "output");
        Directory.CreateDirectory(osuOutputRoot);
        var osuDestination = Path.Combine(osuOutputRoot, "cross-format.mcz");
        byte[] oldOsuDestinationBytes = [0x4F, 0x4C, 0x44, 0x2D, 0x4D, 0x43, 0x5A];
        await File.WriteAllBytesAsync(osuDestination, oldOsuDestinationBytes).ConfigureAwait(false);
        var osuResult = await MalodyFullPackageExportService.ExportAsync(
                new MalodyFullPackageExportRequest(
                    osuTab.Session.CaptureCommittedDocument(),
                    osuTab.SourcePath,
                    osuDestination,
                    osuScratchRoot))
            .ConfigureAwait(false);

        string[] osuArchiveNames;
        byte[] osuMainBytes;
        using (var archive = ZipFile.OpenRead(osuDestination))
        {
            var entries = archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)).ToArray();
            osuArchiveNames = entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            var chartEntry = entries.Single(entry =>
                Path.GetExtension(entry.FullName).Equals(".mc", StringComparison.OrdinalIgnoreCase));
            await using var input = chartEntry.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output).ConfigureAwait(false);
            osuMainBytes = output.ToArray();
        }
        var osuTargetChartCount = osuArchiveNames.Count(name =>
            Path.GetExtension(name).Equals(".mc", StringComparison.OrdinalIgnoreCase));
        var osuLeakedChartArtifact = osuArchiveNames.Any(name =>
            Path.GetExtension(name).Equals(".osu", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".mcz", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".osz", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".mc_", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase));
        var compactMain =
            !osuMainBytes.Contains((byte)'\r') &&
            !osuMainBytes.Contains((byte)'\n') &&
            !(osuMainBytes.Length >= 3 &&
              osuMainBytes[0] == 0xEF && osuMainBytes[1] == 0xBB && osuMainBytes[2] == 0xBF);
        var parseableMain = true;
        try { using var _ = JsonDocument.Parse(osuMainBytes); }
        catch (JsonException) { parseableMain = false; }
        Require(
            evidence,
            osuTargetChartCount == 1 && !osuLeakedChartArtifact && compactMain && parseableMain &&
            osuResult.MainChartEntryPath.Equals(
                osuArchiveNames.Single(name =>
                    Path.GetExtension(name).Equals(".mc", StringComparison.OrdinalIgnoreCase)),
                StringComparison.OrdinalIgnoreCase) &&
            osuResult.EntryCount == osuArchiveNames.Length,
            "cross-format-osu-to-mcz-emits-one-compact-target-chart-and-excludes-all-sibling-decoys",
            $"targetCharts={osuTargetChartCount}; leaked={osuLeakedChartArtifact}; " +
            $"compact={compactMain}; parseable={parseableMain}; main={osuResult.MainChartEntryPath}");

        var osuReverseImported = await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                osuDestination,
                Path.Combine(osuToMczRoot, "reverse-import"))
            .ConfigureAwait(false);
        var osuFreshPath = osuReverseImported.ChartPaths.Single();
        ChartDocument osuFreshDocument;
        await using (var input = new FileStream(
                         osuFreshPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            osuFreshDocument = await ChartDocumentImporter.ImportAsync(
                    input,
                    osuFreshPath,
                    osuController.DefaultCreator)
                .ConfigureAwait(false);
        }
        var injectedNoteMatchCount = osuFreshDocument.Notes.Count(note =>
            note.Lane == injectedLane &&
            note.EndBeat is null &&
            note.Beat.Bar == injectedBeat.Bar &&
            note.Beat.Numerator == injectedBeat.Numerator &&
            note.Beat.Denominator == injectedBeat.Denominator);
        var osuFreshResourceHashes = await CaptureReferencedResourceHashesAsync(
                osuFreshPath,
                osuFreshDocument)
            .ConfigureAwait(false);
        var osuFreshPackageResourceHashes = await CapturePackagedResourceHashesAsync(osuFreshPath)
            .ConfigureAwait(false);
        var osuResourceBytesRetained =
            osuSourceResourceHashes.ResolvedCount > 0 &&
            osuFreshResourceHashes.ReferencedCount == osuFreshResourceHashes.ResolvedCount &&
            osuSourceResourceHashes.Hashes.All(osuFreshPackageResourceHashes.Contains) &&
            osuFreshResourceHashes.Hashes.All(osuFreshPackageResourceHashes.Contains);
        Require(
            evidence,
            osuReverseImported.ChartPaths.Count == 1 &&
            osuFreshDocument.Metadata.Version == osuExportedVersion &&
            osuFreshDocument.Notes.Count == osuTab.Session.Document.Notes.Count &&
            osuFreshDocument.Timing.Count == osuTab.Session.Document.Timing.Count &&
            osuFreshDocument.Notes.Count(note => note.IsHold) == osuExpectedHoldCount &&
            injectedNoteMatchCount == 1 &&
            osuResourceBytesRetained &&
            ResourceExists(osuFreshPath, osuFreshDocument.Metadata.AudioFile, required: true) &&
            ResourceExists(osuFreshPath, osuFreshDocument.Metadata.CoverFile, required: false) &&
            ResourceExists(osuFreshPath, osuFreshDocument.Metadata.BackgroundFile, required: false),
            "cross-format-osu-to-mcz-retains-dirty-exact-beat-and-resources",
            $"version={osuFreshDocument.Metadata.Version}; notes={osuFreshDocument.Notes.Count}; " +
            $"timing={osuFreshDocument.Timing.Count}; holds={osuExpectedHoldCount}; " +
            $"exactTap={injectedNoteMatchCount}; resourceBytes={osuResourceBytesRetained}; " +
            $"sourceResources={osuSourceResourceHashes.ResolvedCount}/{osuSourceResourceHashes.ReferencedCount}; " +
            $"freshResources={osuFreshResourceHashes.ResolvedCount}/{osuFreshResourceHashes.ReferencedCount}; " +
            $"audio={osuFreshDocument.Metadata.AudioFile}");

        var osuSourceTreeAfter = await CaptureFileTreeAsync(osuSourceRoot).ConfigureAwait(false);
        var osuWorkingTreeAfter = await CaptureFileTreeAsync(osuTab.CacheDirectory).ConfigureAwait(false);
        var osuSessionUnchanged =
            osuTab.Session.IsDirty == osuDirtyBefore &&
            osuTab.Session.ChangeSerial == osuSerialBefore &&
            string.Equals(
                ChartStructuralFingerprint.Compute(osuTab.Session.Document),
                osuFingerprintBefore,
                StringComparison.Ordinal) &&
            string.Equals(osuTab.Session.Document.FilePath, osuDocumentPathBefore, StringComparison.Ordinal) &&
            PathsEqual(osuTab.SourcePath, osuSourcePathBefore) &&
            PathsEqual(osuTab.WorkingPath, osuWorkingPathBefore);
        var finalOriginalMcSha = await ComputeSha256Async(RequiredFixturePath).ConfigureAwait(false);
        var finalOriginalOsuSha = await ComputeSha256Async(RequiredOsuFixturePath).ConfigureAwait(false);
        var finalOriginalMczSha = await ComputeSha256Async(RequiredMczFixturePath).ConfigureAwait(false);
        Require(
            evidence,
            osuSessionUnchanged &&
            TreesEqual(osuSourceTreeBefore, osuSourceTreeAfter) &&
            TreesEqual(osuWorkingTreeBefore, osuWorkingTreeAfter) &&
            File.Exists(osuScratchSentinel) && HasNoOwnedScratchChildren(osuScratchRoot) &&
            HasNoTemporaryArchive(osuOutputRoot) &&
            finalOriginalMcSha.Equals(RequiredFixtureSha256, StringComparison.Ordinal) &&
            finalOriginalOsuSha.Equals(RequiredOsuFixtureSha256, StringComparison.Ordinal) &&
            finalOriginalMczSha.Equals(RequiredMczFixtureSha256, StringComparison.Ordinal) &&
            !oldOsuDestinationBytes.SequenceEqual(await File.ReadAllBytesAsync(osuDestination).ConfigureAwait(false)),
            "cross-format-osu-to-mcz-preserves-session-source-working-and-atomic-scratch",
            $"session={osuSessionUnchanged}; source={TreesEqual(osuSourceTreeBefore, osuSourceTreeAfter)}; " +
            $"working={TreesEqual(osuWorkingTreeBefore, osuWorkingTreeAfter)}; " +
            $"scratch={HasNoOwnedScratchChildren(osuScratchRoot)}; temp={HasNoTemporaryArchive(osuOutputRoot)}; " +
            $"originals={finalOriginalMcSha}|{finalOriginalOsuSha}|{finalOriginalMczSha}");
    }

    private static void VerifyWindowsLibraryContentPolicy(SliceEvidence evidence)
    {
        var empty = WindowsLibraryContentPolicy.Resolve(
            indexedChartCount: 0,
            visibleFolderCount: 0,
            query: string.Empty,
            isScanning: false);
        Require(
            evidence,
            !empty.ShowLibraryList && empty.ShowEmptyPanel && empty.ShowLauncherActions &&
            empty.EmptyTitle == WindowsLibraryContentPolicy.EmptyLibraryTitle &&
            empty.EmptySubtitle == WindowsLibraryContentPolicy.EmptyLibrarySubtitle,
            "windows-library-content-empty-library-matches-wpf-launcher",
            $"list={empty.ShowLibraryList}; empty={empty.ShowEmptyPanel}; launcher={empty.ShowLauncherActions}; " +
            $"title={empty.EmptyTitle}; subtitle={empty.EmptySubtitle}");

        var noResults = WindowsLibraryContentPolicy.Resolve(
            indexedChartCount: 11,
            visibleFolderCount: 0,
            query: "definitely-no-match",
            isScanning: false);
        Require(
            evidence,
            !noResults.ShowLibraryList && noResults.ShowEmptyPanel && !noResults.ShowLauncherActions &&
            noResults.EmptyTitle == WindowsLibraryContentPolicy.NoSearchResultsTitle &&
            noResults.EmptySubtitle == WindowsLibraryContentPolicy.NoSearchResultsSubtitle,
            "windows-library-content-search-zero-results-matches-wpf-copy-and-hides-launcher",
            $"list={noResults.ShowLibraryList}; empty={noResults.ShowEmptyPanel}; " +
            $"launcher={noResults.ShowLauncherActions}; title={noResults.EmptyTitle}; " +
            $"subtitle={noResults.EmptySubtitle}");

        var results = WindowsLibraryContentPolicy.Resolve(
            indexedChartCount: 11,
            visibleFolderCount: 2,
            query: "resound",
            isScanning: false);
        Require(
            evidence,
            results.ShowLibraryList && !results.ShowEmptyPanel && !results.ShowLauncherActions,
            "windows-library-content-results-show-list-and-hide-empty",
            $"list={results.ShowLibraryList}; empty={results.ShowEmptyPanel}; launcher={results.ShowLauncherActions}");

        var scanning = WindowsLibraryContentPolicy.Resolve(
            indexedChartCount: 0,
            visibleFolderCount: 0,
            query: "resound",
            isScanning: true);
        Require(
            evidence,
            !scanning.ShowLibraryList && !scanning.ShowEmptyPanel && !scanning.ShowLauncherActions,
            "windows-library-content-scan-hides-empty-placeholder",
            $"list={scanning.ShowLibraryList}; empty={scanning.ShowEmptyPanel}; launcher={scanning.ShowLauncherActions}");
    }

    private static void VerifyWindowsLibrarySurfacePolicy(SliceEvidence evidence)
    {
        var empty = WindowsLibrarySurfacePolicy.Resolve(
            libraryRequested: false,
            hasActiveTab: false,
            chartTitle: null,
            chartIsDirty: false);
        var retainedLibrary = WindowsLibrarySurfacePolicy.Resolve(
            libraryRequested: true,
            hasActiveTab: true,
            chartTitle: "Resound",
            chartIsDirty: true);
        var cleanEditor = WindowsLibrarySurfacePolicy.Resolve(
            libraryRequested: false,
            hasActiveTab: true,
            chartTitle: "Resound",
            chartIsDirty: false);
        var dirtyEditor = WindowsLibrarySurfacePolicy.Resolve(
            libraryRequested: false,
            hasActiveTab: true,
            chartTitle: "Resound",
            chartIsDirty: true);

        Require(
            evidence,
            empty.LibraryVisible && !empty.EditorVisible && !empty.SaveStateVisible &&
            !empty.EditorStatusVisible &&
            empty.WindowTitle == WindowsLibrarySurfacePolicy.LibraryWindowTitle &&
            retainedLibrary.LibraryVisible && !retainedLibrary.EditorVisible &&
            !retainedLibrary.SaveStateVisible && !retainedLibrary.EditorStatusVisible &&
            retainedLibrary.WindowTitle == WindowsLibrarySurfacePolicy.LibraryWindowTitle &&
            !cleanEditor.LibraryVisible && cleanEditor.EditorVisible &&
            cleanEditor.SaveStateVisible && cleanEditor.EditorStatusVisible &&
            cleanEditor.WindowTitle == "Resound — LumaChart Studio" &&
            !dirtyEditor.LibraryVisible && dirtyEditor.EditorVisible &&
            dirtyEditor.SaveStateVisible && dirtyEditor.EditorStatusVisible &&
            dirtyEditor.WindowTitle == "Resound * — LumaChart Studio",
            "windows-library-surface-preserves-library-across-active-tab-refresh-and-restores-editor-state",
            $"empty={empty}; retained={retainedLibrary}; clean={cleanEditor}; dirty={dirtyEditor}");
    }

    private static void VerifyWindowsPasswordInputPolicy(SliceEvidence evidence)
    {
        var plainTextBox = new TextBox();
        WindowsImeTextInputPolicy.Apply(plainTextBox);
        var passwordBox = new PasswordTextBoxForSpec { PasswordChar = '●' };
        WindowsPasswordInputPolicy.Apply(passwordBox);
        var peer = passwordBox.CreateAutomationPeerForSpec();
        var valueProvider = peer.GetProvider<IValueProvider>();
        var initialHelp = AutomationProperties.GetHelpText(passwordBox);
        var initialProviderValue = valueProvider?.Value;
        passwordBox.Text = "mima";
        var committedHelp = AutomationProperties.GetHelpText(passwordBox);
        var committedProviderValue = valueProvider?.Value;

        Require(
            evidence,
            InputMethod.GetIsInputMethodEnabled(plainTextBox) &&
            !InputMethod.GetIsInputMethodEnabled(passwordBox) &&
            peer.GetClassName() == "PasswordBox" &&
            valueProvider is not null &&
            initialProviderValue == string.Empty &&
            committedProviderValue == string.Empty &&
            initialHelp == WindowsPasswordInputPolicy.CreateAutomationStatus(null) &&
            committedHelp == WindowsPasswordInputPolicy.CreateAutomationStatus("mima") &&
            !initialHelp.Contains("mima", StringComparison.Ordinal) &&
            !committedHelp.Contains("mima", StringComparison.Ordinal),
            "windows-password-input-disables-ime-and-exposes-only-committed-length",
            $"plainIme={InputMethod.GetIsInputMethodEnabled(plainTextBox)}; " +
            $"passwordIme={InputMethod.GetIsInputMethodEnabled(passwordBox)}; " +
            $"class={peer.GetClassName()}; provider={valueProvider is not null}; " +
            $"providerValueLengths={initialProviderValue?.Length ?? -1}/" +
            $"{committedProviderValue?.Length ?? -1}; " +
            $"initial={initialHelp}; committed={committedHelp}");
    }

    private sealed class PasswordTextBoxForSpec : WindowsPasswordTextBox
    {
        public AutomationPeer CreateAutomationPeerForSpec() =>
            OnCreateAutomationPeer();
    }

    private static void VerifyOnlineRoomHeaderPresentation(SliceEvidence evidence)
    {
        var room = new OnlineRoomSummaryDto(
            Guid.Parse("a1a11111-2222-4333-8444-555555555555"),
            7654321,
            "房间",
            4101,
            "Avalonia 房主",
            1,
            8,
            true,
            false,
            true,
            4439047,
            DateTimeOffset.Parse("2026-07-23T23:00:00+08:00"));
        var initial = OnlineCommunityDialog.FormatActiveRoomSubtitle(room);
        var refreshed = OnlineCommunityDialog.FormatActiveRoomSubtitle(room, 2);
        var automation = OnlineCommunityDialog.CreateActiveRoomAutomationStatus(room, 2);

        Require(
            evidence,
            initial == "1/8 人 · 房主 Avalonia 房主 · 密码房" &&
            refreshed == "2/8 人 · 房主 Avalonia 房主 · 密码房" &&
            automation ==
            $"RoomNumber={room.RoomNumber}; MemberCount=2; " +
            $"MaximumMembers=8; RoomId={room.Id:D}",
            "online-room-header-prefers-authoritative-live-member-count",
            $"initial={initial}; refreshed={refreshed}; automation={automation}");
    }

    private static void VerifyDesktopLibraryDeletionPlanner(SliceEvidence evidence, string runRoot)
    {
        var workspace = Path.Combine(runRoot, "library-deletion-planner", "workspace");
        Directory.CreateDirectory(workspace);

        var empty = DesktopLibraryDeletionPlanner.Create(workspace, []);
        Require(
            evidence,
            !empty.CanExecute &&
            empty.BlockReason == DesktopLibraryDeletionBlockReason.EmptySelection &&
            empty.BlockMessage == null &&
            empty.Targets.Count == 0 &&
            empty.ConfirmationMessage == null,
            "desktop-library-deletion-empty-selection-is-a-silent-no-op",
            $"reason={empty.BlockReason}; targets={empty.Targets.Count}; message={empty.BlockMessage ?? "<null>"}");

        var invalidFolder = CreateDeletionFolderEntry(
            "\0invalid-library-folder",
            "Invalid",
            "Invalid",
            chartCount: 0);
        var invalid = DesktopLibraryDeletionPlanner.Create(workspace, [invalidFolder]);
        Require(
            evidence,
            !invalid.CanExecute &&
            invalid.BlockReason == DesktopLibraryDeletionBlockReason.InvalidFolderPath &&
            invalid.BlockMessage == DesktopLibraryDeletionPlanner.InvalidFolderPathMessage &&
            invalid.Targets.Count == 0,
            "desktop-library-deletion-invalid-selected-path-fails-closed",
            $"reason={invalid.BlockReason}; targets={invalid.Targets.Count}; message={invalid.BlockMessage}");

        var authoredValidPath = Path.Combine(workspace, "Albums", "Intermediate", "..", "Song");
        var validFolder = CreateDeletionFolderEntry(
            authoredValidPath,
            "Normalized Song",
            Path.Combine("Albums", "Song"),
            chartCount: 2);
        var normalized = DesktopLibraryDeletionPlanner.Create(workspace, [validFolder]);
        var workspaceFolder = CreateDeletionFolderEntry(
            workspace,
            "Workspace Root",
            ".",
            chartCount: 1);
        var workspaceRootPlan = DesktopLibraryDeletionPlanner.Create(workspace, [workspaceFolder]);
        var outsidePath = Path.Combine(
            Path.GetDirectoryName(workspace)!,
            Path.GetFileName(workspace) + "-outside");
        var outsideFolder = CreateDeletionFolderEntry(
            outsidePath,
            "Outside",
            "..",
            chartCount: 1);
        var outside = DesktopLibraryDeletionPlanner.Create(workspace, [outsideFolder]);
        Require(
            evidence,
            normalized.CanExecute &&
            normalized.Targets.Count == 1 &&
            PathsEqual(normalized.Targets[0].FullPath, Path.GetFullPath(authoredValidPath)) &&
            !workspaceRootPlan.CanExecute &&
            workspaceRootPlan.BlockReason == DesktopLibraryDeletionBlockReason.UnsafeWorkspaceBoundary &&
            workspaceRootPlan.BlockMessage == DesktopLibraryDeletionPlanner.UnsafeWorkspaceBoundaryMessage &&
            !outside.CanExecute &&
            outside.BlockReason == DesktopLibraryDeletionBlockReason.UnsafeWorkspaceBoundary &&
            outside.BlockMessage == DesktopLibraryDeletionPlanner.UnsafeWorkspaceBoundaryMessage,
            "desktop-library-deletion-normalizes-targets-and-enforces-strict-workspace-boundary",
            $"normalized={normalized.Targets.FirstOrDefault()?.FullPath}; " +
            $"rootReason={workspaceRootPlan.BlockReason}; outsideReason={outside.BlockReason}");

        var parentPath = Path.Combine(workspace, "GroupedSong");
        var childPath = Path.Combine(parentPath, "0");
        var siblingPath = Path.Combine(workspace, "StandaloneSong");
        var parent = CreateDeletionFolderEntry(parentPath, "Grouped", "GroupedSong", chartCount: 3);
        var child = CreateDeletionFolderEntry(childPath, "Grouped Child", @"GroupedSong\0", chartCount: 2);
        var sibling = CreateDeletionFolderEntry(siblingPath, "Standalone", "StandaloneSong", chartCount: 4);
        var deduplicated = DesktopLibraryDeletionPlanner.Create(
            workspace,
            [child, parent, parent, sibling]);
        Require(
            evidence,
            deduplicated.CanExecute &&
            deduplicated.Targets.Count == 2 &&
            // The parent delete also removes the child card's two charts, so
            // the confirmation counts them: 3 + 2 (nested) + 4.
            deduplicated.ChartCount == 9 &&
            deduplicated.Targets.Any(target => PathsEqual(target.FullPath, parentPath)) &&
            deduplicated.Targets.Any(target => PathsEqual(target.FullPath, siblingPath)) &&
            deduplicated.Targets.All(target => !PathsEqual(target.FullPath, childPath)),
            "desktop-library-deletion-parent-selection-deduplicates-descendants-and-identical-paths",
            $"targets={string.Join("|", deduplicated.Targets.Select(target => target.FullPath))}; " +
            $"charts={deduplicated.ChartCount}");

        var blocked = DesktopLibraryDeletionPlanner.Create(
            workspace,
            [parent],
            [
                new DesktopLibraryOpenDocumentReference(
                    "Inside Chart",
                    [Path.Combine(parentPath, "0", "inside.mc")]),
                new DesktopLibraryOpenDocumentReference(
                    "Outside Chart",
                    [Path.Combine(siblingPath, "outside.mc")])
            ]);
        Require(
            evidence,
            !blocked.CanExecute &&
            blocked.BlockReason == DesktopLibraryDeletionBlockReason.OpenDocument &&
            blocked.BlockingDocumentCount == 1 &&
            blocked.BlockingDocumentTitles.SequenceEqual(["Inside Chart"], StringComparer.Ordinal) &&
            blocked.BlockMessage ==
            "选中文件夹中有正在编辑的谱面：Inside Chart。\n\n请先关闭对应谱面标签，再执行删除。",
            "desktop-library-deletion-blocks-open-document-paths-inside-selected-folder",
            $"blocked={blocked.BlockingDocumentCount}; titles={string.Join("|", blocked.BlockingDocumentTitles)}; " +
            $"message={blocked.BlockMessage}");

        var cappedOpenDocuments = new[]
        {
            "A", "A", "B", "C", "D", "E", "F", "G"
        }.Select((title, index) => new DesktopLibraryOpenDocumentReference(
            title,
            [Path.Combine(parentPath, $"open-{index}.mc")])).ToArray();
        var capped = DesktopLibraryDeletionPlanner.Create(workspace, [parent], cappedOpenDocuments);
        var invalidOpenPathIgnored = DesktopLibraryDeletionPlanner.Create(
            workspace,
            [parent],
            [new DesktopLibraryOpenDocumentReference("Invalid", ["\0invalid-open-path"])]);
        Require(
            evidence,
            !capped.CanExecute &&
            capped.BlockingDocumentCount == 8 &&
            capped.BlockingDocumentTitles.SequenceEqual(["A", "B", "C", "D", "E"], StringComparer.Ordinal) &&
            capped.BlockMessage ==
            "选中文件夹中有正在编辑的谱面：A、B、C、D、E。\n\n请先关闭对应谱面标签，再执行删除。" &&
            invalidOpenPathIgnored.CanExecute,
            "desktop-library-deletion-open-document-copy-distincts-and-caps-five-titles",
            $"blocked={capped.BlockingDocumentCount}; titles={string.Join("|", capped.BlockingDocumentTitles)}; " +
            $"invalidIgnored={invalidOpenPathIgnored.CanExecute}");

        var summaryFolders = Enumerable.Range(1, 8)
            .Select(index => CreateDeletionFolderEntry(
                Path.Combine(workspace, "Summary", $"Song{index:00}"),
                $"Song {index}",
                Path.Combine("Summary", $"Song{index:00}"),
                chartCount: index))
            .ToArray();
        var summary = DesktopLibraryDeletionPlanner.Create(workspace, summaryFolders);
        var expectedPreview = string.Join("\n", new[]
        {
            "• Song 1  (Summary\\Song01)",
            "• Song 2  (Summary\\Song02)",
            "• Song 3  (Summary\\Song03)",
            "• Song 4  (Summary\\Song04)",
            "• Song 5  (Summary\\Song05)",
            "• Song 6  (Summary\\Song06)",
            "…以及其他 2 个文件夹"
        });
        var expectedConfirmation =
            "确定将 8 个歌曲文件夹移入回收站吗？\n" +
            "其中包含 36 张谱面，并会一起移除文件夹内的音频和图片。\n\n" +
            expectedPreview;
        Require(
            evidence,
            summary.CanExecute &&
            summary.FolderCount == 8 &&
            summary.ChartCount == 36 &&
            summary.PreviewItems.Count == DesktopLibraryDeletionPlanner.MaximumConfirmationPreviewItems &&
            summary.OmittedPreviewCount == 2 &&
            summary.PreviewText == expectedPreview &&
            summary.ConfirmationMessage == expectedConfirmation &&
            summary.PreviewItems.All(item => !item.Contains("Song 7", StringComparison.Ordinal) &&
                                             !item.Contains("Song 8", StringComparison.Ordinal)),
            "desktop-library-deletion-confirmation-summary-matches-wpf-six-item-preview",
            $"folders={summary.FolderCount}; charts={summary.ChartCount}; " +
            $"previewItems={summary.PreviewItems.Count}; omitted={summary.OmittedPreviewCount}; " +
            $"choice={DesktopLibraryDeletionPlanner.RecycleChoiceLabel}");
    }

    private static void VerifyDesktopLibraryChartDeletionPlanner(
        SliceEvidence evidence,
        string runRoot)
    {
        var workspace = Path.Combine(
            runRoot,
            "library-chart-deletion-planner",
            "workspace");
        var chartFolder = Path.Combine(workspace, "Song");
        Directory.CreateDirectory(chartFolder);
        var chartPath = Path.Combine(
            workspace,
            "Intermediate",
            "..",
            "Song",
            "chart.mc");
        var chart = new DesktopLibraryChartEntry
        {
            ChartPath = chartPath,
            FolderPath = chartFolder,
            RelativePath = Path.Combine("Song", "chart.mc"),
            Title = "Library Chart",
            Version = "Hard",
            Creator = "tester",
            AddedTime = DateTime.UnixEpoch,
            ModifiedTime = DateTime.UnixEpoch
        };

        var invalidChart = new DesktopLibraryChartEntry
        {
            ChartPath = "\0invalid-chart",
            FolderPath = chartFolder,
            RelativePath = "invalid.mc",
            AddedTime = DateTime.UnixEpoch,
            ModifiedTime = DateTime.UnixEpoch
        };
        var outsideChart = new DesktopLibraryChartEntry
        {
            ChartPath = Path.Combine(
                Path.GetDirectoryName(workspace)!,
                "outside.mc"),
            FolderPath = Path.GetDirectoryName(workspace)!,
            RelativePath = "..\\outside.mc",
            AddedTime = DateTime.UnixEpoch,
            ModifiedTime = DateTime.UnixEpoch
        };
        var invalid = DesktopLibraryChartDeletionPlanner.Create(
            workspace,
            invalidChart);
        var outside = DesktopLibraryChartDeletionPlanner.Create(
            workspace,
            outsideChart);
        Require(
            evidence,
            !invalid.CanExecute &&
            invalid.BlockReason ==
                DesktopLibraryChartDeletionBlockReason.InvalidChartPath &&
            invalid.BlockMessage ==
                DesktopLibraryChartDeletionPlanner.InvalidChartPathMessage &&
            !outside.CanExecute &&
            outside.BlockReason ==
                DesktopLibraryChartDeletionBlockReason.UnsafeWorkspaceBoundary &&
            outside.BlockMessage ==
                DesktopLibraryChartDeletionPlanner.UnsafeWorkspaceBoundaryMessage,
            "desktop-library-chart-deletion-invalid-or-outside-path-fails-closed",
            $"invalid={invalid.BlockReason}; outside={outside.BlockReason}");

        var allowed = DesktopLibraryChartDeletionPlanner.Create(
            workspace,
            chart,
            [
                new DesktopLibraryOpenDocumentReference(
                    "Sibling",
                    [Path.Combine(chartFolder, "other.mc")])
            ]);
        var expectedConfirmation =
            "确定将这张谱面移入回收站吗？\n\n" +
            "Library Chart  [Hard]\nSong\\chart.mc\n\n" +
            "只移除谱面文件，不影响同目录的共用音频和图片。";
        Require(
            evidence,
            allowed.CanExecute &&
            allowed.BlockReason ==
                DesktopLibraryChartDeletionBlockReason.None &&
            PathsEqual(allowed.FullPath!, Path.Combine(chartFolder, "chart.mc")) &&
            allowed.ConfirmationMessage == expectedConfirmation,
            "desktop-library-chart-deletion-matches-wpf-single-file-confirmation",
            $"path={allowed.FullPath}; choice={DesktopLibraryChartDeletionPlanner.RecycleChoiceLabel}");

        var blocked = DesktopLibraryChartDeletionPlanner.Create(
            workspace,
            chart,
            [
                new DesktopLibraryOpenDocumentReference(
                    "Open Chart",
                    [Path.Combine(chartFolder, "CHART.MC")])
            ]);
        Require(
            evidence,
            !blocked.CanExecute &&
            blocked.BlockReason ==
                DesktopLibraryChartDeletionBlockReason.OpenDocument &&
            blocked.BlockMessage ==
                DesktopLibraryChartDeletionPlanner.OpenDocumentMessage &&
            blocked.FullPath != null &&
            blocked.ConfirmationMessage == null,
            "desktop-library-chart-deletion-blocks-the-exact-open-chart",
            $"reason={blocked.BlockReason}; message={blocked.BlockMessage}");
    }

    private static DesktopLibraryFolderEntry CreateDeletionFolderEntry(
        string folderPath,
        string displayTitle,
        string relativeFolderPath,
        int chartCount)
    {
        var charts = Enumerable.Range(0, chartCount)
            .Select(index => new DesktopLibraryChartEntry
            {
                ChartPath = Path.Combine(folderPath, $"chart-{index}.mc"),
                FolderPath = folderPath,
                RelativePath = Path.Combine(relativeFolderPath, $"chart-{index}.mc")
            })
            .ToArray();
        return new DesktopLibraryFolderEntry
        {
            FolderPath = folderPath,
            RelativeFolderPath = relativeFolderPath,
            Charts = charts,
            DisplayTitle = displayTitle,
            DisplayArtist = "Specs Artist",
            CoverPath = string.Empty,
            AddedTime = DateTime.UnixEpoch,
            ModifiedTime = DateTime.UnixEpoch,
            SongCount = 1,
            Formats = "Malody",
            SearchText = displayTitle.ToLowerInvariant(),
            FolderSearchText = displayTitle.ToLowerInvariant()
        };
    }

    private static void VerifyWindowsLibraryDeletionInfrastructure(
        SliceEvidence evidence,
        string runRoot)
    {
        var selected = new[] { "folder-a", "folder-b" };
        var preserved = DesktopLibrarySelectionPolicy.ResolveRightClick(selected, "folder-b");
        var converged = DesktopLibrarySelectionPolicy.ResolveRightClick(selected, "folder-c");
        Require(
            evidence,
            preserved.SequenceEqual(selected, StringComparer.Ordinal) &&
            converged.SequenceEqual(["folder-c"], StringComparer.Ordinal),
            "windows-library-context-menu-right-click-preserves-selected-batch-or-converges-to-target",
            $"preserved={string.Join("|", preserved)}; converged={string.Join("|", converged)}");

        var popupSettings = new WindowsDisplayScaleSettings(
            uiScale: 1.35,
            fontScale: 1.25,
            displayScaleInitialized: true);
        var firstPopupPlan = AvaloniaPopupScaleController.Resolve(12, popupSettings);
        var secondPopupPlan = AvaloniaPopupScaleController.Resolve(12, popupSettings);
        Require(
            evidence,
            Math.Abs(firstPopupPlan.UiScale - 1.35) < 1e-12 &&
            Math.Abs(firstPopupPlan.FontScale - 1.25) < 1e-12 &&
            Math.Abs(firstPopupPlan.AppliedFontSize - 15) < 1e-12 &&
            firstPopupPlan == secondPopupPlan,
            "windows-library-popup-scale-uses-independent-stable-ui-and-font-baselines",
            $"ui={firstPopupPlan.UiScale}; font={firstPopupPlan.FontScale}; " +
            $"baseFont={firstPopupPlan.BaseFontSize}; appliedFont={firstPopupPlan.AppliedFontSize}");

        var recyclePath = Path.Combine(runRoot, "library-recycle-descriptor", "target");
        var descriptor = WindowsRecycleBinService.Describe(recyclePath);
        var normalizedRecyclePath = Path.GetFullPath(recyclePath);
        Require(
            evidence,
            descriptor.FullPath == normalizedRecyclePath &&
            descriptor.Operation == WindowsRecycleBinService.FileOperationDelete &&
            // WPF's 0x0454 plus FOF_WANTNUKEWARNING (0x4000): an item that
            // cannot be recycled must prompt instead of being silently destroyed.
            descriptor.Flags == 0x4454 &&
            descriptor.Flags == WindowsRecycleBinService.SafeRecycleFlags &&
            (descriptor.Flags & WindowsRecycleBinService.WpfCompatibleFlags) ==
                WindowsRecycleBinService.WpfCompatibleFlags &&
            descriptor.AllowUndoRequested &&
            descriptor.DoubleNullTerminatedSource == normalizedRecyclePath + '\0' + '\0' &&
            descriptor.DoubleNullTerminatedSource.Count(character => character == '\0') == 2,
            "windows-library-recycle-descriptor-matches-wpf-double-null-and-0x4454-nuke-warning-flags",
            $"operation={descriptor.Operation}; flags=0x{descriptor.Flags:X4}; " +
            $"sourceLength={descriptor.DoubleNullTerminatedSource.Length}; allowUndo={descriptor.AllowUndoRequested}");

        var nonzeroFailedClosed = false;
        var abortedFailedClosed = false;
        try { WindowsRecycleBinService.EnsureSucceeded(5, anyOperationsAborted: false); }
        catch (IOException) { nonzeroFailedClosed = true; }
        try { WindowsRecycleBinService.EnsureSucceeded(0, anyOperationsAborted: true); }
        catch (IOException) { abortedFailedClosed = true; }
        Require(
            evidence,
            nonzeroFailedClosed && abortedFailedClosed,
            "windows-library-recycle-result-and-aborted-state-both-fail-closed",
            $"nonzero={nonzeroFailedClosed}; aborted={abortedFailedClosed}");
    }

    private static void VerifyDesktopChartValidationPresentation(SliceEvidence evidence)
    {
        var draftCollision = new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                    Lane = 0,
                    Beat = BeatPosition.Preserve(0, 1, 7),
                    Group = 3
                },
                new ChartNote
                {
                    Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                    Lane = 0,
                    Beat = BeatPosition.Preserve(0, 1, 7),
                    Group = 4,
                    IsDraft = true,
                    DraftColumn = 0
                }
            ]
        };
        var draftPresentation = DesktopChartValidationPresenter.Create(draftCollision, [3, 4]);
        Require(
            evidence,
            draftPresentation.CanSave &&
            draftPresentation.TotalIssueCount == 0 &&
            draftPresentation.OverlappingNoteCount == 0 &&
            draftPresentation.CrossingLongNoteCount == 0 &&
            draftPresentation.FirstIssue == null &&
            draftPresentation.Navigation == null &&
            draftPresentation.SaveBlockingMessage == null,
            "desktop-chart-validation-keeps-core-draft-ignore-contract",
            $"canSave={draftPresentation.CanSave}; issues={draftPresentation.TotalIssueCount}; " +
            $"manual={draftPresentation.ManualCheckMessage}");
        Require(
            evidence,
            draftPresentation.ManualCheckMessage ==
            DesktopChartValidationPresenter.ManualCheckSuccessMessage &&
            draftPresentation.ManualCheckMessage == "未发现重叠键或 LN 交叉。" &&
            DesktopChartValidationPresenter.DialogTitle == "谱面检查器",
            "desktop-chart-validation-manual-success-copy-matches-wpf",
            $"title={DesktopChartValidationPresenter.DialogTitle}; " +
            $"message={draftPresentation.ManualCheckMessage}");

        var firstLeftId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var firstRightId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var laneTwoLeftId = Guid.Parse("20000000-0000-0000-0000-000000000003");
        var laneTwoRightId = Guid.Parse("20000000-0000-0000-0000-000000000004");
        var crossingLeftId = Guid.Parse("20000000-0000-0000-0000-000000000005");
        var crossingRightId = Guid.Parse("20000000-0000-0000-0000-000000000006");
        var firstBeat = BeatPosition.Preserve(1, 3, 6);
        var problemChart = new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                // Authored before lane 0 to prove final first-issue ordering is
                // Beat then lane, not document insertion order.
                new ChartNote
                {
                    Id = laneTwoLeftId,
                    Lane = 2,
                    Beat = BeatPosition.Preserve(1, 3, 6),
                    Group = 8
                },
                new ChartNote
                {
                    Id = laneTwoRightId,
                    Lane = 2,
                    Beat = new BeatPosition(1, 1, 2),
                    Group = 9
                },
                new ChartNote
                {
                    Id = firstLeftId,
                    Lane = 0,
                    Beat = firstBeat,
                    Group = 5
                },
                new ChartNote
                {
                    Id = firstRightId,
                    Lane = 0,
                    Beat = new BeatPosition(1, 1, 2),
                    Group = 7
                },
                new ChartNote
                {
                    Id = crossingLeftId,
                    Lane = 1,
                    Beat = BeatPosition.Preserve(2, 1, 12),
                    EndBeat = BeatPosition.Preserve(2, 7, 12),
                    Group = 11
                },
                new ChartNote
                {
                    Id = crossingRightId,
                    Lane = 1,
                    Beat = BeatPosition.Preserve(2, 5, 12),
                    EndBeat = BeatPosition.Preserve(3, 1, 12),
                    Group = 12
                }
            ]
        };
        var presentation = DesktopChartValidationPresenter.Create(problemChart, [5, 8, 99]);
        var firstIssue = presentation.FirstIssue;
        Require(
            evidence,
            presentation.Issues.Count == 3 &&
            presentation.Issues[0].Lane == 0 &&
            presentation.Issues[1].Lane == 2 &&
            presentation.Issues[2].Lane == 1 &&
            presentation.Issues[0].Kind == ChartIssueKind.OverlappingNotes &&
            presentation.Issues[1].Kind == ChartIssueKind.OverlappingNotes &&
            presentation.Issues[2].Kind == ChartIssueKind.CrossingLongNotes &&
            firstIssue?.Beat.Bar == 1 &&
            firstIssue.Beat.Numerator == 3 &&
            firstIssue.Beat.Denominator == 6,
            "desktop-chart-validation-preserves-core-beat-then-lane-order-and-exact-first-beat",
            $"order={string.Join("|", presentation.Issues.Select(issue =>
                $"{issue.Beat}@{issue.Lane}:{issue.Kind}"))}; first={firstIssue?.Beat}");
        Require(
            evidence,
            !presentation.CanSave &&
            presentation.TotalIssueCount == 3 &&
            presentation.OverlappingNoteCount == 2 &&
            presentation.CrossingLongNoteCount == 1,
            "desktop-chart-validation-counts-overlap-and-crossing-from-core-results",
            $"total={presentation.TotalIssueCount}; overlap={presentation.OverlappingNoteCount}; " +
            $"crossing={presentation.CrossingLongNoteCount}");

        const string expectedManualMessage =
            "发现 3 项错误：重叠键 2，LN 交叉 1。\n\n" +
            "已定位最靠下的一项：第 1 轨存在重叠 Note（1:3/6）。";
        Require(
            evidence,
            presentation.ManualCheckMessage == expectedManualMessage,
            "desktop-chart-validation-manual-error-copy-matches-wpf",
            presentation.ManualCheckMessage);

        const string expectedSaveBlockingMessage =
            "保存已暂停：发现 3 项谱面错误。\n\n" +
            "重叠键：2\n" +
            "LN 交叉：1\n\n" +
            "已定位并选中最靠下的一项，请修复后再次保存。";
        Require(
            evidence,
            presentation.SaveBlockingMessage == expectedSaveBlockingMessage,
            "desktop-chart-validation-save-blocking-copy-matches-wpf",
            presentation.SaveBlockingMessage ?? "<null>");

        var navigation = presentation.Navigation;
        Require(
            evidence,
            navigation != null &&
            navigation.ExactBeat.Bar == 1 &&
            navigation.ExactBeat.Numerator == 3 &&
            navigation.ExactBeat.Denominator == 6 &&
            navigation.NoteIds.SequenceEqual([firstLeftId, firstRightId]) &&
            navigation.HiddenGroupsToReveal.SequenceEqual([5]) &&
            navigation.InspectorTabIndex ==
            DesktopChartIssueNavigationPlanner.WpfChartInspectorTabIndex &&
            navigation.InspectorTabIndex == 2 &&
            navigation.HoverStatusText == "谱面错误：第 1 轨存在重叠 Note · 1:3/6",
            "desktop-chart-validation-navigation-selects-first-notes-and-reveals-hidden-groups",
            navigation == null
                ? "navigation=<null>"
                : $"beat={navigation.ExactBeat}; notes={string.Join("|", navigation.NoteIds)}; " +
                  $"groups={string.Join("|", navigation.HiddenGroupsToReveal)}; " +
                  $"tab={navigation.InspectorTabIndex}; status={navigation.HoverStatusText}");
    }

    private static void VerifyIndexedMarqueeSelection(SliceEvidence evidence)
    {
        var longHold = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 0,
            Beat = BeatPosition.Zero,
            EndBeat = BeatPosition.Preserve(10, 0, 1)
        };
        var horizontallyOutsideTap = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 1,
            Beat = BeatPosition.Preserve(5, 0, 1)
        };
        var index = new TimelineNoteIndex();
        index.Rebuild([longHold, horizontallyOutsideTap]);
        var candidates = new List<ChartNote>();
        var hits = new HashSet<Guid>();
        var marquee = TimelineMarqueeSelection.Normalize(new Point(55, 55), new Point(5, 45));
        var candidateCount = TimelineMarqueeSelection.AddIntersectingNotes(
            index,
            4.5,
            5.5,
            marquee,
            note => note.IsHold
                ? new Rect(note.Lane * 100 + 10, note.Beat.Value * 10, 30,
                    (note.EndBeat!.Value.Value - note.Beat.Value) * 10)
                : new Rect(note.Lane * 100 + 10, note.Beat.Value * 10 - 5, 30, 10),
            hits,
            candidates);
        Require(
            evidence,
            candidateCount == 2 && hits.SetEquals([longHold.Id]),
            "marquee-ln-body-intersection-uses-interval-index",
            $"candidates={candidateCount}; hits={hits.Count}; longHoldSelected={hits.Contains(longHold.Id)}");

        var hiddenOverlap = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 2,
            Beat = BeatPosition.Preserve(6, 0, 1),
            Group = 7
        };
        var visibleOverlap = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 2,
            Beat = BeatPosition.Preserve(6, 0, 1),
            Group = 8
        };
        var overlapIndex = new TimelineNoteIndex();
        overlapIndex.Rebuild([hiddenOverlap, visibleOverlap]);
        var overlapScratch = new List<ChartNote>();
        var visibleHit = overlapIndex.HitTest(
            2,
            6,
            .1,
            overlapScratch,
            excludedGroups: new HashSet<int> { 7 });
        var visibleMarqueeHits = new HashSet<Guid>();
        var visibleMarqueeCandidates = new List<ChartNote>();
        _ = TimelineMarqueeSelection.AddIntersectingNotes(
            overlapIndex,
            5.9,
            6.1,
            new Rect(0, 0, 100, 100),
            _ => new Rect(10, 10, 20, 20),
            visibleMarqueeHits,
            visibleMarqueeCandidates,
            new HashSet<int> { 7 });
        Require(
            evidence,
            visibleHit?.Id == visibleOverlap.Id && visibleMarqueeHits.SetEquals([visibleOverlap.Id]),
            "timeline-hidden-group-does-not-mask-visible-hit-or-enter-marquee",
            $"hit={visibleHit?.Id}; marquee={string.Join(',', visibleMarqueeHits)}");

        var originalSelection = Guid.NewGuid();
        var selection = new HashSet<Guid> { originalSelection };
        var additive = new HashSet<Guid> { originalSelection };
        additive.UnionWith(hits);
        var additiveChanged = TimelineMarqueeSelection.ReplaceSelection(selection, additive);
        var additivePassed = additiveChanged && selection.SetEquals([originalSelection, longHold.Id]);
        var replacementChanged = TimelineMarqueeSelection.ReplaceSelection(selection, hits);
        Require(
            evidence,
            additivePassed && replacementChanged && selection.SetEquals([longHold.Id]),
            "marquee-shift-adds-and-plain-replaces",
            $"additive={additivePassed}; replacementCount={selection.Count}");

        var manyNotes = new List<ChartNote>(BenchmarkNoteCount);
        for (var noteNumber = 0; noteNumber < BenchmarkNoteCount; noteNumber++)
        {
            manyNotes.Add(new ChartNote
            {
                Id = Guid.NewGuid(),
                Lane = noteNumber % 4,
                Beat = BeatPosition.Preserve(noteNumber / 16, noteNumber % 16, 16)
            });
        }
        var largeIndex = new TimelineNoteIndex();
        largeIndex.Rebuild(manyNotes);
        candidates.Clear();
        hits.Clear();
        var largeCandidateCount = TimelineMarqueeSelection.AddIntersectingNotes(
            largeIndex,
            999.5,
            1000.5,
            new Rect(0, 9_995, 50, 10),
            note => new Rect(note.Lane * 100, note.Beat.Value * 10 - 1, 50, 2),
            hits,
            candidates);
        Require(
            evidence,
            largeIndex.Count == BenchmarkNoteCount && largeCandidateCount is > 0 and < 256 && hits.Count > 0,
            "marquee-100000-note-path-is-range-bounded",
            $"indexed={largeIndex.Count}; candidates={largeCandidateCount}; hits={hits.Count}");
    }

    private static void VerifyTimelineToolContracts(SliceEvidence evidence)
    {
        var tapSession = new EditorSession { SnapDivision = 6 };
        tapSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        var cellCenter = TimelinePlacementGeometry.CellCenterBeat(1, tapSession.SnapDivision);
        var tapId = BasicChartEditingService.PlaceTap(
            tapSession,
            0,
            cellCenter,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        var storedTap = tapSession.Document.Notes.Single(note => note.Id == tapId);
        Require(
            evidence,
            cellCenter.Bar == 1 && cellCenter.Numerator == 1 && cellCenter.Denominator == 12 &&
            HasExactExpression(storedTap.Beat, cellCenter),
            "timeline-tap-preserves-exact-cell-center-beat",
            $"authored={cellCenter}; stored={storedTap.Beat}; division={tapSession.SnapDivision}");

        var holdSession = new EditorSession { SnapDivision = 8 };
        holdSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        var laterBeat = TimelinePlacementGeometry.CellCenterBeat(3.8, holdSession.SnapDivision);
        var earlierBeat = TimelinePlacementGeometry.CellCenterBeat(2.2, holdSession.SnapDivision);
        var reverseHold = TimelinePlacementGeometry.Resolve(
            TimelineTool.Hold,
            laterBeat,
            earlierBeat,
            holdSession.SnapDivision);
        var holdId = BasicChartEditingService.PlaceHold(
            holdSession,
            1,
            reverseHold.StartBeat,
            reverseHold.EndBeat!.Value,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        var storedHold = holdSession.Document.Notes.Single(note => note.Id == holdId);
        var reverseHoldPassed = reverseHold.Kind == TimelinePlacementKind.Hold &&
                                reverseHold.StartBeat.CompareTo(reverseHold.EndBeat!.Value) < 0 &&
                                HasExactExpression(storedHold.Beat, reverseHold.StartBeat) &&
                                HasExactExpression(storedHold.EndBeat!.Value, reverseHold.EndBeat.Value) &&
                                holdSession.Undo() && holdSession.Document.Notes.Count == 0 &&
                                !holdSession.Undo();
        Require(
            evidence,
            reverseHoldPassed,
            "timeline-hold-reverse-normalizes-and-is-one-undo",
            $"gesture={laterBeat}->{earlierBeat}; stored={storedHold.Beat}->{storedHold.EndBeat}; " +
            $"singleUndo={reverseHoldPassed}");

        var indexedHead = BeatPosition.Preserve(1, 3, 12);
        var indexedTail = BeatPosition.Preserve(2, 5, 12);
        var indexedHold = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 2,
            Beat = indexedHead,
            EndBeat = indexedTail
        };
        var tailIndex = new TimelineNoteIndex();
        tailIndex.Rebuild(
            Enumerable.Range(0, 2_000)
                .Select(index => new ChartNote
                {
                    Id = Guid.NewGuid(),
                    Lane = index % 4,
                    Beat = BeatPosition.FromDouble(index / 8d)
                })
                .Append(indexedHold));
        var tailCandidates = new List<ChartNote>();
        var queriedTails = tailIndex.QueryHoldTails(
            indexedTail.Value - .001,
            indexedTail.Value + .001,
            tailCandidates);
        var tailHit = TimelineHoldTailResize.IsHit(
            new Point(130, 102),
            100,
            160,
            200,
            100,
            20);
        var headWinsForShortHold = !TimelineHoldTailResize.IsHit(
            new Point(130, 196),
            100,
            160,
            195,
            190,
            20);
        Require(
            evidence,
            queriedTails == 1 && tailCandidates.Single().Id == indexedHold.Id &&
            tailHit && headWinsForShortHold,
            "timeline-ln-tail-index-and-hit-priority-avoid-full-chart-scan",
            $"notes={tailIndex.Count}; candidates={queriedTails}; " +
            $"tailHit={tailHit}; shortHeadWins={headWinsForShortHold}");

        var authoredTail = TimelinePlacementGeometry.CellCenterBeat(2.6, 6);
        var resolvedTail = TimelineHoldTailResize.ResolveTarget(indexedHead, authoredTail);
        var resolvedTap = TimelineHoldTailResize.ResolveTarget(indexedHead, indexedHead);
        var firstLegal = TimelineHoldTailResize.KeepLastLegal(
            indexedHead,
            indexedTail,
            authoredTail,
            (_, end) => end.Value >= 4,
            out var legalAccepted);
        var retainedLegal = TimelineHoldTailResize.KeepLastLegal(
            indexedHead,
            firstLegal,
            BeatPosition.Preserve(4, 1, 12),
            (_, end) => end.Value >= 4,
            out var lockedAccepted);
        Require(
            evidence,
            resolvedTail.HasValue && HasExactExpression(resolvedTail.Value, authoredTail) &&
            !resolvedTap.HasValue && legalAccepted && !lockedAccepted &&
            Nullable.Equals(firstLegal, authoredTail) && Nullable.Equals(retainedLegal, authoredTail) &&
            !TimelineHoldTailResize.IsInterrupted(7, 7) &&
            TimelineHoldTailResize.IsInterrupted(7, 8),
            "timeline-ln-tail-preview-keeps-exact-target-last-legal-and-interrupts",
            $"authored={authoredTail}; target={resolvedTail}; tap={resolvedTap == null}; " +
            $"legal={legalAccepted}; locked={lockedAccepted}; retained={retainedLegal}");

        var resizeSession = new EditorSession { SnapDivision = 4 };
        resizeSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [indexedHold.Clone()]
        });
        var exactResize = BasicChartEditingService.ResizeHoldTail(
            resizeSession,
            indexedHold.Id,
            authoredTail,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        var exactStored = resizeSession.Document.Notes.Single().EndBeat;
        var exactUndo = resizeSession.Undo();
        var tapResize = BasicChartEditingService.ResizeHoldTail(
            resizeSession,
            indexedHold.Id,
            indexedHead,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        var becameTap = !resizeSession.Document.Notes.Single().IsHold;
        var tapUndo = resizeSession.Undo();
        var noMoreResizeUndo = !resizeSession.Undo();
        Require(
            evidence,
            exactResize && exactStored.HasValue &&
            HasExactExpression(exactStored.Value, authoredTail) && exactUndo &&
            tapResize && becameTap && tapUndo && resizeSession.Document.Notes.Single().IsHold &&
            noMoreResizeUndo,
            "timeline-ln-tail-release-is-one-exact-shared-mutation-and-tap-conversion",
            $"exact={exactStored}; exactUndo={exactUndo}; tap={becameTap}; " +
            $"tapUndo={tapUndo}; singleHistory={noMoreResizeUndo}");

        var tapStart = TimelinePlacementGeometry.CellCenterBeat(4.1, 8);
        var upward = TimelinePlacementGeometry.CellCenterBeat(4.4, 8);
        var downward = TimelinePlacementGeometry.CellCenterBeat(3.8, 8);
        var promoted = TimelinePlacementGeometry.Resolve(TimelineTool.Tap, tapStart, upward, 8);
        var retainedTap = TimelinePlacementGeometry.Resolve(TimelineTool.Tap, tapStart, downward, 8);
        Require(
            evidence,
            promoted.Kind == TimelinePlacementKind.Hold &&
            promoted.EndBeat.HasValue &&
            retainedTap.Kind == TimelinePlacementKind.Tap &&
            !retainedTap.EndBeat.HasValue &&
            HasExactExpression(retainedTap.StartBeat, tapStart),
            "timeline-tap-drag-promotion-matches-wpf-direction",
            $"start={tapStart}; upward={promoted.Kind}; downward={retainedTap.Kind}");

        var eraseSession = new EditorSession();
        var eraseNotes = Enumerable.Range(0, 3)
            .Select(index => new ChartNote
            {
                Id = Guid.NewGuid(),
                Lane = index,
                Beat = BeatPosition.Preserve(index, 1, 8)
            })
            .ToArray();
        var erasedEffect = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(2, 1, 8),
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1.25,
            EffectValueText = "1.25"
        };
        var retainedBpm = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(3, 0, 1),
            Bpm = 180
        };
        eraseSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = eraseNotes.Select(note => note.Clone()).ToList(),
            Timing = [erasedEffect.Clone(), retainedBpm.Clone()]
        });
        var erasedIds = eraseNotes.Take(2).Select(note => note.Id).ToHashSet();
        var erased = TimelineEraseCommit.Commit(
            eraseSession,
            erasedIds,
            new HashSet<Guid> { erasedEffect.Id, retainedBpm.Id });
        var eraseUndoPassed = erased.DeletedNoteCount == 2 && erased.DeletedEffectCount == 1 &&
                              eraseSession.Document.Notes.Count == 1 &&
                              eraseSession.Document.Timing.Count == 1 &&
                              eraseSession.Document.Timing.Single().Id == retainedBpm.Id &&
                              eraseSession.Undo() && eraseSession.Document.Notes.Count == 3 &&
                              eraseSession.Document.Timing.Count == 2 &&
                              !eraseSession.Undo();
        var emptyEraseSession = new EditorSession();
        emptyEraseSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        var emptyErasePassed = !TimelineEraseCommit.Commit(
                                   emptyEraseSession,
                                   new HashSet<Guid>(),
                                   new HashSet<Guid>()).Applied &&
                                !emptyEraseSession.Undo();
        Require(
            evidence,
            eraseUndoPassed && emptyErasePassed,
            "timeline-continuous-mixed-erase-is-one-undo-bpm-safe-and-empty-is-no-history",
            $"notes={erased.DeletedNoteCount}; effects={erased.DeletedEffectCount}; " +
            $"restoredNotes={eraseSession.Document.Notes.Count}; " +
            $"restoredTiming={eraseSession.Document.Timing.Count}; empty={emptyErasePassed}");

        var timingOnlyEffect = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(1, 1, 4),
            Effect = TimingEffectKind.Jump,
            EffectValue = 2,
            EffectValueText = "2"
        };
        var timingOnlySession = new EditorSession
        {
            CanEditNotes = false,
            CanEditTiming = true
        };
        timingOnlySession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [eraseNotes[0].Clone()],
            Timing = [timingOnlyEffect.Clone()]
        });
        var timingOnlyErase = TimelineEraseCommit.Commit(
            timingOnlySession,
            new HashSet<Guid>(),
            new HashSet<Guid> { timingOnlyEffect.Id });
        var timingOnlyNoteRetained = timingOnlySession.Document.Notes.Count == 1;
        var timingOnlyDocumentErased = timingOnlySession.Document.Timing.All(timing =>
            timing.Id != timingOnlyEffect.Id && !timing.Effect.HasValue);
        var timingOnlyUndo = timingOnlySession.Undo();
        var timingOnlyRestored = timingOnlySession.Document.Timing.Any(timing =>
            timing.Id == timingOnlyEffect.Id && timing.Effect == TimingEffectKind.Jump);
        var timingOnlySingleUndo = !timingOnlySession.Undo();
        var timingOnlyPassed = timingOnlyErase.DeletedNoteCount == 0 &&
                               timingOnlyErase.DeletedEffectCount == 1 &&
                               timingOnlyNoteRetained && timingOnlyDocumentErased &&
                               timingOnlyUndo && timingOnlyRestored && timingOnlySingleUndo;
        Require(
            evidence,
            timingOnlyPassed,
            "timeline-effect-erase-works-with-timing-only-permission",
            $"deletedEffects={timingOnlyErase.DeletedEffectCount}; " +
            $"noteRetained={timingOnlyNoteRetained}; erased={timingOnlyDocumentErased}; " +
            $"undo={timingOnlyUndo}; restored={timingOnlyRestored}; singleUndo={timingOnlySingleUndo}");

        var permissionSession = new EditorSession { IsReadOnly = true };
        var readOnlyDenial = TimelineEditPreflight.GetNoteEditDenial(permissionSession, "移动");
        permissionSession.IsReadOnly = false;
        permissionSession.CanEditNotes = false;
        var permissionDenial = TimelineEditPreflight.GetNoteEditDenial(permissionSession, "擦除");
        permissionSession.CanEditNotes = true;
        var allowed = TimelineEditPreflight.GetNoteEditDenial(permissionSession, "放置");
        Require(
            evidence,
            readOnlyDenial?.Contains("只读", StringComparison.Ordinal) == true &&
            permissionDenial?.Contains("权限", StringComparison.Ordinal) == true &&
            allowed == null,
            "timeline-note-edit-preflight-reports-readonly-and-permission",
            $"readonly={readOnlyDenial}; permission={permissionDenial}; allowed={allowed == null}");
    }

    private static void VerifyNoteTypeTransformContracts(SliceEvidence evidence)
    {
        static EditorSession CreateHoldSession(BeatPosition head, BeatPosition tail)
        {
            var session = new EditorSession { SnapDivision = 4 };
            var hold = new ChartNote
            {
                Id = Guid.NewGuid(),
                Lane = 0,
                Beat = head,
                EndBeat = tail
            };
            session.ReplaceDocument(new ChartDocument { KeyCount = 4, Notes = [hold] });
            session.Selection.Add(hold.Id);
            return session;
        }

        var exactHead = BeatPosition.Preserve(1, 3, 6);
        var exactTail = BeatPosition.Preserve(2, 6, 12);

        var visibleTap = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 0,
            Beat = BeatPosition.Preserve(1, 0, 4)
        };
        var visibleHold = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 1,
            Beat = BeatPosition.Preserve(1, 1, 4),
            EndBeat = BeatPosition.Preserve(2, 1, 4)
        };
        var hiddenHold = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 2,
            Beat = BeatPosition.Preserve(1, 2, 4),
            EndBeat = BeatPosition.Preserve(2, 2, 4),
            Group = 9
        };
        var filterSession = new EditorSession();
        filterSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [visibleTap, visibleHold, hiddenHold]
        });
        filterSession.Selection.UnionWith([visibleTap.Id, visibleHold.Id, hiddenHold.Id]);
        var filterSerial = filterSession.ChangeSerial;
        var filteredLongCount = WindowsNoteTypeTransformCommand.FilterSelectionByNoteType(
            filterSession,
            longNotes: true,
            hiddenGroups: new HashSet<int> { 9 });
        var filterUndoCreated = filterSession.Undo();
        Require(
            evidence,
            filteredLongCount == 1 && filterSession.Selection.SetEquals([visibleHold.Id]) &&
            filterSession.ChangeSerial == filterSerial && !filterUndoCreated,
            "note-transform-extension-filter-keeps-only-visible-selected-type-without-history",
            $"kept={filteredLongCount}; hiddenExcluded={!filterSession.Selection.Contains(hiddenHold.Id)}; " +
            $"serial={filterSession.ChangeSerial}; undo={filterUndoCreated}");

        var includeTailSession = CreateHoldSession(exactHead, exactTail);
        var originalHoldId = includeTailSession.Selection.Single();
        var includeTail = WindowsNoteTypeTransformCommand.ConvertLongNotesToTaps(
            includeTailSession,
            LongNoteToTapMode.IncludeTail);
        var convertedHead = includeTailSession.Document.Notes.Single(note => note.Id == originalHoldId);
        var convertedTail = includeTailSession.Document.Notes.Single(note => note.Id != originalHoldId);
        var includeTailSelectionMatches = includeTailSession.Selection.SetEquals(includeTail.Selection);
        var includeTailUndo = includeTailSession.Undo();
        var restoredHold = includeTailSession.Document.Notes.Single();
        var includeTailSingleUndo = !includeTailSession.Undo();
        Require(
            evidence,
            includeTail.ConvertedCount == 1 && includeTail.CreatedCount == 1 &&
            !convertedHead.IsHold && !convertedTail.IsHold &&
            HasExactExpression(convertedHead.Beat, exactHead) &&
            HasExactExpression(convertedTail.Beat, exactTail) &&
            includeTailSelectionMatches && includeTailUndo && restoredHold.IsHold &&
            restoredHold.EndBeat.HasValue &&
            HasExactExpression(restoredHold.Beat, exactHead) &&
            HasExactExpression(restoredHold.EndBeat.Value, exactTail) &&
            includeTailSingleUndo,
            "note-transform-ln-to-tap-include-tail-is-exact-selected-and-one-undo",
            $"converted={includeTail.ConvertedCount}; created={includeTail.CreatedCount}; " +
            $"selection={includeTail.Selection.Count}; undo={includeTailUndo}; single={includeTailSingleUndo}");

        var fillSession = CreateHoldSession(BeatPosition.Zero, BeatPosition.Preserve(1, 0, 1));
        var fill = WindowsNoteTypeTransformCommand.ConvertLongNotesToTaps(
            fillSession,
            LongNoteToTapMode.Fill);
        var expectedFillBeats = new[]
        {
            BeatPosition.Zero,
            BeatPosition.Preserve(0, 1, 4),
            BeatPosition.Preserve(0, 2, 4),
            BeatPosition.Preserve(0, 3, 4),
            BeatPosition.Preserve(1, 0, 1)
        };
        var actualFillBeats = fillSession.Document.Notes.OrderBy(note => note.Beat).Select(note => note.Beat).ToArray();
        Require(
            evidence,
            fill.ConvertedCount == 1 && fill.CreatedCount == 4 &&
            fill.Selection.SetEquals(fillSession.Selection) &&
            actualFillBeats.Length == expectedFillBeats.Length &&
            actualFillBeats.Zip(expectedFillBeats).All(pair => pair.First.CompareTo(pair.Second) == 0),
            "note-transform-ln-to-tap-fill-uses-current-division-shared-engine",
            $"division={fillSession.SnapDivision}; created={fill.CreatedCount}; " +
            $"beats={string.Join(',', actualFillBeats)}");

        var headOnlySession = CreateHoldSession(exactHead, exactTail);
        var headOnly = WindowsNoteTypeTransformCommand.ConvertLongNotesToTaps(
            headOnlySession,
            LongNoteToTapMode.HeadOnly);
        var headOnlyNote = headOnlySession.Document.Notes.Single();
        Require(
            evidence,
            headOnly.ConvertedCount == 1 && headOnly.CreatedCount == 0 &&
            !headOnlyNote.IsHold && HasExactExpression(headOnlyNote.Beat, exactHead) &&
            headOnly.Selection.SetEquals(headOnlySession.Selection) && headOnly.Selection.Count == 1,
            "note-transform-ln-to-tap-head-only-preserves-exact-head-and-selection",
            $"head={headOnlyNote.Beat}; selection={headOnly.Selection.Count}");

        var tapHead = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 2,
            Beat = BeatPosition.Preserve(3, 3, 6)
        };
        var tapSuccessor = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 2,
            Beat = BeatPosition.Preserve(4, 0, 1)
        };
        var tapToLongSession = new EditorSession { SnapDivision = 4 };
        tapToLongSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [tapHead, tapSuccessor]
        });
        tapToLongSession.Selection.Add(tapHead.Id);
        var tapToLong = WindowsNoteTypeTransformCommand.ConvertTapsToLongNotes(tapToLongSession);
        var longNote = tapToLongSession.Document.Notes.Single(note => note.Id == tapHead.Id);
        var expectedLongTail = BeatPosition.Preserve(3, 3, 4);
        var tapToLongUndo = tapToLongSession.Undo();
        var tapToLongSingleUndo = !tapToLongSession.Undo();
        Require(
            evidence,
            tapToLong.ConvertedCount == 1 && tapToLong.CreatedCount == 0 &&
            longNote.IsHold && HasExactExpression(longNote.Beat, tapHead.Beat) &&
            longNote.EndBeat.HasValue && longNote.EndBeat.Value.CompareTo(expectedLongTail) == 0 &&
            tapToLong.Selection.SetEquals(tapToLongSession.Selection) &&
            tapToLong.Selection.SetEquals([tapHead.Id]) &&
            tapToLongUndo && !tapToLongSession.Document.Notes.Single(note => note.Id == tapHead.Id).IsHold &&
            tapToLongSingleUndo,
            "note-transform-tap-to-ln-is-exact-selected-and-one-undo",
            $"tail={longNote.EndBeat}; expected={expectedLongTail}; undo={tapToLongUndo}; " +
            $"single={tapToLongSingleUndo}");

        var unlockedHold = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 0,
            Beat = BeatPosition.Preserve(5, 0, 1),
            EndBeat = BeatPosition.Preserve(6, 0, 1)
        };
        var lockedHold = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 1,
            Beat = BeatPosition.Preserve(7, 0, 1),
            EndBeat = BeatPosition.Preserve(8, 0, 1)
        };
        var sourceLockSession = new EditorSession { AllowOwnerProtectedEdits = false };
        sourceLockSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [unlockedHold, lockedHold]
        });
        sourceLockSession.SetOwnerProtection([lockedHold.Id], isProtected: true);
        sourceLockSession.Selection.UnionWith([unlockedHold.Id, lockedHold.Id]);
        var editableLongCount = WindowsNoteTypeTransformCommand.CountSelectedLongNotes(sourceLockSession);
        var sourceLockResult = WindowsNoteTypeTransformCommand.ConvertLongNotesToTaps(
            sourceLockSession,
            LongNoteToTapMode.HeadOnly);
        Require(
            evidence,
            editableLongCount == 1 && sourceLockResult.ConvertedCount == 1 &&
            !sourceLockSession.Document.Notes.Single(note => note.Id == unlockedHold.Id).IsHold &&
            sourceLockSession.Document.Notes.Single(note => note.Id == lockedHold.Id).IsHold &&
            sourceLockSession.Selection.SetEquals([unlockedHold.Id]),
            "note-transform-source-owner-lock-is-filtered-before-mutation",
            $"eligible={editableLongCount}; converted={sourceLockResult.ConvertedCount}; " +
            $"lockedSelected={sourceLockSession.Selection.Contains(lockedHold.Id)}");

        var targetHead = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 0,
            Beat = BeatPosition.Preserve(1, 0, 1)
        };
        var targetSuccessor = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 0,
            Beat = BeatPosition.Preserve(3, 0, 1)
        };
        var targetLockSession = new EditorSession
        {
            SnapDivision = 4,
            AllowOwnerProtectedEdits = false
        };
        targetLockSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [targetHead, targetSuccessor]
        });
        targetLockSession.AddOwnerProtectedRegion(
            BeatPosition.Preserve(2, 0, 1),
            BeatPosition.Preserve(3, 0, 1));
        targetLockSession.Selection.Add(targetHead.Id);
        var targetLockSerial = targetLockSession.ChangeSerial;
        var targetLockFingerprint = ChartStructuralFingerprint.Compute(targetLockSession.Document);
        var targetLockDenied = ThrowsInvalidOperation(
            () => WindowsNoteTypeTransformCommand.ConvertTapsToLongNotes(targetLockSession),
            "房主锁定");

        var permissionSession = CreateHoldSession(exactHead, exactTail);
        permissionSession.CanEditNotes = false;
        var permissionSerial = permissionSession.ChangeSerial;
        var permissionDenied = ThrowsInvalidOperation(
            () => WindowsNoteTypeTransformCommand.ConvertLongNotesToTaps(
                permissionSession,
                LongNoteToTapMode.HeadOnly),
            "权限");
        permissionSession.CanEditNotes = true;
        permissionSession.IsReadOnly = true;
        var readOnlyDenied = ThrowsInvalidOperation(
            () => WindowsNoteTypeTransformCommand.ConvertLongNotesToTaps(
                permissionSession,
                LongNoteToTapMode.HeadOnly),
            "只读");
        Require(
            evidence,
            targetLockDenied && targetLockSession.ChangeSerial == targetLockSerial &&
            string.Equals(
                ChartStructuralFingerprint.Compute(targetLockSession.Document),
                targetLockFingerprint,
                StringComparison.Ordinal) &&
            targetLockSession.Selection.SetEquals([targetHead.Id]) &&
            permissionDenied && readOnlyDenied && permissionSession.ChangeSerial == permissionSerial &&
            permissionSession.Selection.Count == 1,
            "note-transform-permission-and-target-owner-lock-preflight-is-nonmutating",
            $"targetDenied={targetLockDenied}; targetSerial={targetLockSession.ChangeSerial}; " +
            $"permission={permissionDenied}; readonly={readOnlyDenied}; permissionSerial={permissionSession.ChangeSerial}");
    }

    private static void VerifyNotePropertyContracts(SliceEvidence evidence, string runRoot)
    {
        var noteRoot = Path.Combine(runRoot, "note-property");
        Directory.CreateDirectory(noteRoot);
        var chartPath = Path.Combine(noteRoot, "property-contract.mc");
        var soundPath = Path.Combine(noteRoot, "hit.wav");
        File.WriteAllBytes(soundPath, [0x52, 0x49, 0x46, 0x46]);

        var noteId = Guid.NewGuid();
        var initialBeat = BeatPosition.Preserve(12, 1, 2);
        var initialEndBeat = BeatPosition.Preserve(13, 3, 6);
        var initialUnknown = JsonNode.Parse("""{"skin":"keep","flag":7}""")!.AsObject();
        var note = new ChartNote
        {
            Id = noteId,
            Lane = 1,
            Beat = initialBeat,
            EndBeat = initialEndBeat,
            Volume = 73,
            Sound = "hit.wav",
            Group = 9,
            UnknownData = initialUnknown.DeepClone().AsObject(),
            OnlineCreatorUserId = 41
        };
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            FilePath = chartPath,
            KeyCount = 4,
            Notes = [note]
        });
        session.Selection.Add(noteId);

        var inspector = WindowsNotePropertyCommand.ReadInspectorState(session);
        Require(
            evidence,
            inspector is
            {
                SelectedCount: 1,
                Hint: "Hold Note",
                Fields.Beat: "12:1/2",
                Fields.EndBeat: "13:3/6",
                Fields.Lane: "2",
                Fields.Volume: "73",
                Fields.Sound: "hit.wav",
                Fields.Group: "9"
            },
            "note-property-inspector-shows-exact-single-selection-fields",
            $"hint={inspector.Hint}; beat={inspector.Fields?.Beat}; end={inspector.Fields?.EndBeat}; " +
            $"lane={inspector.Fields?.Lane}; sound={inspector.Fields?.Sound}");

        var selectionShapeSession = new EditorSession();
        var firstSelectionNote = new ChartNote { Lane = 0, Beat = BeatPosition.Zero };
        var secondSelectionNote = new ChartNote { Lane = 1, Beat = BeatPosition.Preserve(1, 0, 1) };
        selectionShapeSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [firstSelectionNote, secondSelectionNote]
        });
        var emptyInspector = WindowsNotePropertyCommand.ReadInspectorState(selectionShapeSession);
        selectionShapeSession.Selection.UnionWith([firstSelectionNote.Id, secondSelectionNote.Id]);
        var multiInspector = WindowsNotePropertyCommand.ReadInspectorState(selectionShapeSession);
        Require(
            evidence,
            emptyInspector is { SelectedCount: 0, Fields: null } &&
            multiInspector is { SelectedCount: 2, Fields: null } &&
            multiInspector.Hint.Contains("2", StringComparison.Ordinal),
            "note-property-inspector-zero-clears-and-multi-preserves-fields-contract",
            $"empty={emptyInspector.Hint}; multi={multiInspector.Hint}");

        var denominatorBaseline = session.Document.Clone();
        var serialBefore = session.ChangeSerial;
        var applyResult = WindowsNotePropertyCommand.Apply(
            session,
            new WindowsNotePropertyFields(
                "12:3/6",
                string.Empty,
                "D:-3",
                "140",
                "hit.wav",
                "65535"));
        var applied = session.Document.Notes.Single(candidate => candidate.Id == noteId);
        var exactAppliedBeat = BeatPosition.Preserve(12, 3, 6);
        Require(
            evidence,
            session.ChangeSerial == serialBefore + 1 &&
            session.Selection.SetEquals([noteId]) &&
            applyResult.NoteId == noteId &&
            HasExactExpression(applied.Beat, exactAppliedBeat) &&
            !applied.EndBeat.HasValue && !applied.IsHold &&
            applied.IsDraft && applied.DraftColumn == -3 && applied.Lane == 0 &&
            applied.Volume == 100 && applied.Group == ushort.MaxValue &&
            applied.Sound == "hit.wav" && applied.OnlineCreatorUserId == 41 &&
            JsonNode.DeepEquals(applied.UnknownData, initialUnknown),
            "note-property-apply-is-exact-atomic-draft-tap-clamped-and-preserves-opaque-state",
            $"serial={session.ChangeSerial}; beat={applied.Beat}; draft={applied.IsDraft}:{applied.DraftColumn}; " +
            $"volume={applied.Volume}; group={applied.Group}; selected={session.Selection.Count}");

        var denominatorBatch = OnlineChartSyncCodec.CreateBatch(
            0,
            denominatorBaseline,
            session.Document,
            Guid.Parse("5a9cfe5c-c285-453c-84f3-777beb9e107a"));
        var onlineNote = denominatorBatch.UpsertNotes.SingleOrDefault();
        Require(
            evidence,
            onlineNote != null && denominatorBatch.UpsertNotes.Count == 1 &&
            onlineNote.Beat == new OnlineSyncBeat(12, 3, 6) &&
            onlineNote.IsDraft && onlineNote.DraftColumn == -3 &&
            onlineNote.Group == ushort.MaxValue && onlineNote.Sound == "hit.wav",
            "note-property-denominator-only-change-produces-raw-online-upsert",
            onlineNote == null
                ? "no upsert"
                : $"beat={onlineNote.Beat.Bar}:{onlineNote.Beat.Numerator}/{onlineNote.Beat.Denominator}; " +
                  $"draft={onlineNote.IsDraft}:{onlineNote.DraftColumn}; group={onlineNote.Group}");

        var undoApplied = session.Undo();
        var undone = session.Document.Notes.Single(candidate => candidate.Id == noteId);
        var onlyOneUndo = !session.Undo();
        var redoApplied = session.Redo();
        var redone = session.Document.Notes.Single(candidate => candidate.Id == noteId);
        Require(
            evidence,
            undoApplied && onlyOneUndo && redoApplied &&
            HasExactExpression(undone.Beat, initialBeat) &&
            undone.EndBeat.HasValue && HasExactExpression(undone.EndBeat.Value, initialEndBeat) &&
            HasExactExpression(redone.Beat, exactAppliedBeat) &&
            redone.IsDraft && redone.DraftColumn == -3 && !redone.EndBeat.HasValue &&
            session.Selection.SetEquals([noteId]),
            "note-property-apply-is-one-undo-and-redo-preserves-exact-expression",
            $"undo={undoApplied}; single={onlyOneUndo}; redo={redoApplied}; " +
            $"undone={undone.Beat}; redone={redone.Beat}");

        var roundTrip = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(session.Document));
        var roundTripNote = roundTrip.Notes.Single(candidate =>
            candidate.IsDraft && candidate.DraftColumn == -3 && candidate.Beat.CompareTo(exactAppliedBeat) == 0);
        Require(
            evidence,
            HasExactExpression(roundTripNote.Beat, exactAppliedBeat) &&
            !roundTripNote.EndBeat.HasValue && roundTripNote.Volume == 100 &&
            roundTripNote.Group == ushort.MaxValue && roundTripNote.Sound == "hit.wav" &&
            JsonNode.DeepEquals(roundTripNote.UnknownData, initialUnknown),
            "note-property-mc-roundtrip-retains-exact-draft-tap-and-properties",
            $"beat={roundTripNote.Beat}; draft={roundTripNote.DraftColumn}; " +
            $"volume={roundTripNote.Volume}; group={roundTripNote.Group}; sound={roundTripNote.Sound}");

        EditorSession CreateGuardSession()
        {
            var guardNote = new ChartNote
            {
                Id = Guid.NewGuid(),
                Lane = 0,
                Beat = BeatPosition.Preserve(1, 0, 1),
                UnknownData = JsonNode.Parse("""{"guard":"keep"}""")!.AsObject()
            };
            var guard = new EditorSession();
            guard.ReplaceDocument(new ChartDocument
            {
                FilePath = chartPath,
                KeyCount = 4,
                Notes = [guardNote]
            });
            guard.Selection.Add(guardNote.Id);
            return guard;
        }

        var officialSession = CreateGuardSession();
        var officialId = officialSession.Selection.Single();
        var officialResult = WindowsNotePropertyCommand.Apply(
            officialSession,
            new WindowsNotePropertyFields(
                "5:5/10",
                "6:7/14",
                "4",
                "-9",
                string.Empty,
                "0"));
        var officialNote = officialSession.Document.Notes.Single(candidate => candidate.Id == officialId);
        Require(
            evidence,
            officialResult.Column == 4 && !officialResult.IsDraft &&
            !officialNote.IsDraft && officialNote.Lane == 3 && officialNote.DraftColumn == 0 &&
            HasExactExpression(officialNote.Beat, BeatPosition.Preserve(5, 5, 10)) &&
            officialNote.EndBeat.HasValue &&
            HasExactExpression(officialNote.EndBeat.Value, BeatPosition.Preserve(6, 7, 14)) &&
            officialNote.Volume == 0 && officialNote.Sound == null && officialNote.Group == 0,
            "note-property-official-lane-ln-and-lower-volume-clamp",
            $"lane={officialNote.Lane}; beat={officialNote.Beat}; end={officialNote.EndBeat}; " +
            $"volume={officialNote.Volume}");

        void RequireRejected(
            string checkName,
            EditorSession target,
            WindowsNotePropertyFields fields,
            string expectedText,
            WindowsNoteSoundAssignmentPolicy? soundPolicy = null)
        {
            var beforeSerial = target.ChangeSerial;
            var beforeFingerprint = ChartStructuralFingerprint.Compute(target.Document);
            var beforeSelection = target.Selection.ToHashSet();
            var rejected = ThrowsInvalidOperation(
                () => WindowsNotePropertyCommand.Apply(target, fields, soundPolicy),
                expectedText);
            Require(
                evidence,
                rejected && target.ChangeSerial == beforeSerial &&
                target.Selection.SetEquals(beforeSelection) &&
                string.Equals(
                    ChartStructuralFingerprint.Compute(target.Document),
                    beforeFingerprint,
                    StringComparison.Ordinal),
                checkName,
                $"rejected={rejected}; serial={target.ChangeSerial}; selected={target.Selection.Count}");
        }

        static WindowsNotePropertyFields ValidFields(string sound = "") => new(
            "2:3/6",
            string.Empty,
            "1",
            "80",
            sound,
            "2");

        RequireRejected(
            "note-property-invalid-beat-is-nonmutating",
            CreateGuardSession(),
            ValidFields() with { Beat = "invalid" },
            "Beat");
        RequireRejected(
            "note-property-negative-beat-is-nonmutating",
            CreateGuardSession(),
            ValidFields() with { Beat = "-1:0/1" },
            "非负");
        RequireRejected(
            "note-property-end-not-after-head-is-nonmutating",
            CreateGuardSession(),
            ValidFields() with { EndBeat = "2:3/6" },
            "End Beat");
        RequireRejected(
            "note-property-official-lane-lower-bound-is-nonmutating",
            CreateGuardSession(),
            ValidFields() with { Lane = "0" },
            "主轨");
        RequireRejected(
            "note-property-official-lane-upper-bound-is-nonmutating",
            CreateGuardSession(),
            ValidFields() with { Lane = "5" },
            "主轨");
        RequireRejected(
            "note-property-draft-lane-lower-bound-is-nonmutating",
            CreateGuardSession(),
            ValidFields() with { Lane = "D:-65" },
            "草稿列");
        RequireRejected(
            "note-property-draft-lane-upper-bound-is-nonmutating",
            CreateGuardSession(),
            ValidFields() with { Lane = "D:68" },
            "草稿列");
        RequireRejected(
            "note-property-invalid-volume-is-nonmutating",
            CreateGuardSession(),
            ValidFields() with { Volume = "8.5" },
            "音量");
        RequireRejected(
            "note-property-group-bounds-are-nonmutating",
            CreateGuardSession(),
            ValidFields() with { Group = "65536" },
            "Group");
        RequireRejected(
            "note-property-sound-must-be-flat-name",
            CreateGuardSession(),
            ValidFields("folder\\hit.wav"),
            "纯文件名");
        RequireRejected(
            "note-property-sound-extension-is-validated",
            CreateGuardSession(),
            ValidFields("hit.txt"),
            "WAV");
        RequireRejected(
            "note-property-sound-must-exist-in-chart-directory",
            CreateGuardSession(),
            ValidFields("missing.wav"),
            "找不到");

        var noSelectionSession = CreateGuardSession();
        noSelectionSession.Selection.Clear();
        RequireRejected(
            "note-property-requires-single-selection-without-side-effects",
            noSelectionSession,
            ValidFields(),
            "只选择一个");
        var multiSelectionSession = CreateGuardSession();
        var secondId = Guid.NewGuid();
        multiSelectionSession.Document.Notes.Add(new ChartNote
        {
            Id = secondId,
            Lane = 1,
            Beat = BeatPosition.Preserve(3, 0, 1)
        });
        multiSelectionSession.Selection.Add(secondId);
        RequireRejected(
            "note-property-requires-single-multi-selection-without-side-effects",
            multiSelectionSession,
            ValidFields(),
            "只选择一个");

        var readOnlySession = CreateGuardSession();
        readOnlySession.IsReadOnly = true;
        RequireRejected(
            "note-property-readonly-preflight-is-nonmutating",
            readOnlySession,
            ValidFields(),
            "只读");
        var permissionSession = CreateGuardSession();
        permissionSession.CanEditNotes = false;
        RequireRejected(
            "note-property-permission-preflight-is-nonmutating",
            permissionSession,
            ValidFields(),
            "权限");
        var sourceLockSession = CreateGuardSession();
        sourceLockSession.AllowOwnerProtectedEdits = false;
        sourceLockSession.SetOwnerProtection(sourceLockSession.Selection, isProtected: true);
        RequireRejected(
            "note-property-source-owner-lock-preflight-is-nonmutating",
            sourceLockSession,
            ValidFields(),
            "房主锁定");
        var targetLockSession = CreateGuardSession();
        targetLockSession.AllowOwnerProtectedEdits = false;
        targetLockSession.AddOwnerProtectedRegion(
            BeatPosition.Preserve(20, 0, 1),
            BeatPosition.Preserve(21, 0, 1));
        RequireRejected(
            "note-property-target-owner-lock-preflight-is-nonmutating",
            targetLockSession,
            ValidFields() with { Beat = "20:0/1" },
            "目标位置");

        var onlineFrozenSession = CreateGuardSession();
        RequireRejected(
            "note-property-online-unfrozen-sound-preflight-is-nonmutating",
            onlineFrozenSession,
            ValidFields("hit.wav"),
            "冻结",
            new WindowsNoteSoundAssignmentPolicy(true, _ => false));
        var onlineAcceptedSession = CreateGuardSession();
        var onlineAccepted = WindowsNotePropertyCommand.Apply(
            onlineAcceptedSession,
            ValidFields("hit.wav"),
            new WindowsNoteSoundAssignmentPolicy(
                true,
                path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(soundPath), StringComparison.OrdinalIgnoreCase)));
        Require(
            evidence,
            onlineAccepted.Sound == "hit.wav" &&
            onlineAcceptedSession.Document.Notes.Single().Sound == "hit.wav" &&
            onlineAcceptedSession.ChangeSerial == 1,
            "note-property-online-frozen-sound-validator-is-injectable",
            $"sound={onlineAccepted.Sound}; serial={onlineAcceptedSession.ChangeSerial}");

        var soundOnlySession = CreateGuardSession();
        var soundOnlyBefore = soundOnlySession.Document.Notes.Single().Clone();
        var soundOnlyResult = WindowsNotePropertyCommand.AssignSound(
            soundOnlySession,
            "hit.wav");
        var soundOnlyAfter = soundOnlySession.Document.Notes.Single();
        var soundOnlyApplied = soundOnlyResult.Applied && soundOnlyResult.Sound == "hit.wav" &&
                               soundOnlyAfter.Sound == "hit.wav" &&
                               soundOnlyAfter.Beat == soundOnlyBefore.Beat &&
                               soundOnlyAfter.EndBeat == soundOnlyBefore.EndBeat &&
                               soundOnlyAfter.Lane == soundOnlyBefore.Lane &&
                               soundOnlyAfter.Volume == soundOnlyBefore.Volume &&
                               soundOnlyAfter.Group == soundOnlyBefore.Group &&
                               soundOnlySession.ChangeSerial == 1;
        soundOnlySession.Undo();
        Require(
            evidence,
            soundOnlyApplied && soundOnlySession.Document.Notes.Single().Sound == null,
            "note-sound-picker-assignment-mutates-only-sound-and-is-one-undo",
            $"applied={soundOnlyApplied}; serial={soundOnlySession.ChangeSerial}; " +
            $"undoSound={soundOnlySession.Document.Notes.Single().Sound ?? "none"}");

        var soundOnlineRejectedSession = CreateGuardSession();
        var rejectedSerial = soundOnlineRejectedSession.ChangeSerial;
        var soundOnlineRejected = ThrowsInvalidOperation(
            () => WindowsNotePropertyCommand.AssignSound(
                soundOnlineRejectedSession,
                "hit.wav",
                new WindowsNoteSoundAssignmentPolicy(true, _ => false)),
            "冻结");
        Require(
            evidence,
            soundOnlineRejected &&
            soundOnlineRejectedSession.ChangeSerial == rejectedSerial &&
            soundOnlineRejectedSession.Document.Notes.Single().Sound == null,
            "note-sound-picker-rejects-unfrozen-online-resource-without-mutation",
            $"rejected={soundOnlineRejected}; serial={soundOnlineRejectedSession.ChangeSerial}");
    }

    private static void VerifyEvidenceNoteFocusContracts(SliceEvidence evidence)
    {
        var equivalentReduced = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(15, 5, 6)
        };
        var exactUnreduced = new ChartNote
        {
            Lane = 1,
            Beat = BeatPosition.Preserve(15, 10, 12)
        };
        var draftExact = new ChartNote
        {
            Lane = 2,
            Beat = BeatPosition.Preserve(15, 10, 12),
            IsDraft = true
        };
        var fallbackFirstTap = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(1, 0, 1)
        };
        var fallbackHold = new ChartNote
        {
            Lane = 1,
            Beat = BeatPosition.Preserve(2, 0, 1),
            EndBeat = BeatPosition.Preserve(3, 0, 1)
        };
        var fallbackSecondTap = new ChartNote
        {
            Lane = 2,
            Beat = BeatPosition.Preserve(4, 0, 1)
        };

        var exact = EvidenceNoteFocusPolicy.Resolve(
            [equivalentReduced, exactUnreduced, draftExact],
            BeatPosition.Preserve(15, 10, 12));
        var equivalentOnly = EvidenceNoteFocusPolicy.Resolve(
            [equivalentReduced, draftExact],
            BeatPosition.Preserve(15, 10, 12));
        var fallback = EvidenceNoteFocusPolicy.Resolve(
            [fallbackSecondTap, fallbackHold, fallbackFirstTap],
            null);

        Require(
            evidence,
            ReferenceEquals(exact, exactUnreduced) &&
            equivalentOnly == null &&
            ReferenceEquals(fallback, fallbackSecondTap),
            "evidence-note-focus-preserves-exact-authored-beat-and-tap-fallback",
            $"exact={exact?.Beat}; equivalentOnly={equivalentOnly?.Beat.ToString() ?? "null"}; " +
            $"fallback={fallback?.Beat}");
    }

    private static async Task VerifyFourModeChartExportContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        static ChartExportOptions Options(
            ChartExportFormat format,
            ChartExportMode mode,
            bool preservePitch = false,
            double rate = 1.2,
            double minimumRate = .8,
            double maximumRate = 1.2,
            double rateStep = .1) =>
            new(
                format,
                mode,
                preservePitch,
                rate,
                minimumRate,
                maximumRate,
                rateStep,
                OsuOverallDifficulty: 8,
                OsuHpDrainRate: 8);

        static bool Close(double left, double right) => Math.Abs(left - right) < .000001;

        static bool ExactBeat(BeatPosition left, BeatPosition right) =>
            left.Bar == right.Bar &&
            left.Numerator == right.Numerator &&
            left.Denominator == right.Denominator;

        static async Task<string> ReadZipEntryTextAsync(ZipArchiveEntry entry)
        {
            await using var input = entry.Open();
            using var reader = new StreamReader(
                input,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 16 * 1024,
                leaveOpen: false);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        static async Task<byte[]> ReadZipEntryBytesAsync(ZipArchiveEntry entry)
        {
            await using var input = entry.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output).ConfigureAwait(false);
            return output.ToArray();
        }

        static async Task<ChartDocument> ImportFreshChartAsync(string chartPath)
        {
            await using var input = new FileStream(
                chartPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ChartDocumentImporter.ImportAsync(
                    input,
                    chartPath,
                    "Avalonia Specs")
                .ConfigureAwait(false);
        }

        static bool ReferencedResourcesExist(string chartPath, ChartDocument document)
        {
            var chartDirectory = Path.GetDirectoryName(chartPath);
            if (string.IsNullOrWhiteSpace(chartDirectory)) return false;

            bool Exists(string? reference, bool required)
            {
                if (string.IsNullOrWhiteSpace(reference)) return !required;
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(chartDirectory, reference));
                    var boundary = Path.GetFullPath(chartDirectory).TrimEnd(
                                       Path.DirectorySeparatorChar,
                                       Path.AltDirectorySeparatorChar) +
                                   Path.DirectorySeparatorChar;
                    return candidate.StartsWith(
                               boundary,
                               StringComparison.OrdinalIgnoreCase) &&
                           File.Exists(candidate);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
                {
                    return false;
                }
            }

            return Exists(document.Metadata.AudioFile, required: true) &&
                   Exists(document.Metadata.CoverFile, required: false) &&
                   Exists(document.Metadata.BackgroundFile, required: false) &&
                   document.Notes.All(note => Exists(note.Sound, required: false));
        }

        static bool ScratchIsClean(string scratchRoot)
        {
            var exportsRoot = Path.Combine(scratchRoot, "exports");
            return !Directory.Exists(exportsRoot) ||
                   !Directory.EnumerateFileSystemEntries(
                           exportsRoot,
                           "*",
                           SearchOption.TopDirectoryOnly)
                       .Any();
        }

        static string[] TemporaryArchives(string destinationRoot) => Directory
            .EnumerateFiles(destinationRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".luma-export.tmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        static string[] TemporarySingleChartParts(string destinationRoot) => Directory
            .EnumerateFiles(destinationRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        static void WritePcm16StereoWave(string path, double seconds)
        {
            const int sampleRate = 44_100;
            const short channels = 2;
            const short bitsPerSample = 16;
            var frameCount = checked((int)Math.Round(sampleRate * seconds));
            var dataBytes = checked(frameCount * channels * (bitsPerSample / 8));
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataBytes);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * (bitsPerSample / 8));
            writer.Write((short)(channels * (bitsPerSample / 8)));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataBytes);
            for (var frame = 0; frame < frameCount; frame++)
            {
                var sample = (short)Math.Round(
                    Math.Sin(2 * Math.PI * 440 * frame / sampleRate) * short.MaxValue * .25);
                writer.Write(sample);
                writer.Write(sample);
            }
        }

        static double EstimateToneFrequency(string path)
        {
            using var reader = new NAudio.Vorbis.VorbisWaveReader(path);
            var samples = reader.ToSampleProvider();
            var channels = samples.WaveFormat.Channels;
            var sampleRate = samples.WaveFormat.SampleRate;
            var buffer = new float[4_096 * channels];
            var crossings = new List<int>();
            var frameIndex = 0;
            var skipFrames = sampleRate / 10;
            var armed = false;
            while (true)
            {
                var read = samples.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                var frames = read / channels;
                for (var frame = 0; frame < frames; frame++, frameIndex++)
                {
                    if (frameIndex < skipFrames) continue;
                    var sample = buffer[frame * channels];
                    if (sample < -.03f)
                    {
                        armed = true;
                    }
                    else if (armed && sample > .03f)
                    {
                        crossings.Add(frameIndex);
                        armed = false;
                    }
                }
            }

            if (crossings.Count < 8) return double.NaN;
            var periods = crossings
                .Zip(crossings.Skip(1), (left, right) => right - left)
                .Order()
                .ToArray();
            var medianPeriod = periods[periods.Length / 2];
            return medianPeriod <= 0 ? double.NaN : sampleRate / (double)medianPeriod;
        }

        var sliceRoot = Path.Combine(runRoot, "four-mode-chart-export");
        var sourceRoot = Path.Combine(sliceRoot, "source");
        var outputRoot = Path.Combine(sliceRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        var defaultOptions = Options(ChartExportFormat.Malody, ChartExportMode.FullPackage);
        var modes = Enum.GetValues<ChartExportMode>();
        var malodyExtensions = modes.Select(mode =>
                ChartExportPlanner.Create(defaultOptions with { Mode = mode }).DestinationExtension)
            .ToArray();
        var osuExtensions = modes.Select(mode =>
                ChartExportPlanner.Create(defaultOptions with
                {
                    Format = ChartExportFormat.OsuMania,
                    Mode = mode
                }).DestinationExtension)
            .ToArray();
        var hiddenFieldsIgnored = new ChartExportOptions(
                ChartExportFormat.Malody,
                ChartExportMode.FullPackage,
                PreservePitch: true,
                Rate: double.NaN,
                MinimumRate: 2,
                MaximumRate: 1,
                RateStep: 0)
            .Rates()
            .SequenceEqual([1d]) &&
            new ChartExportOptions(
                    ChartExportFormat.Malody,
                    ChartExportMode.SingleChart,
                    PreservePitch: false,
                    Rate: double.PositiveInfinity,
                    MinimumRate: double.NaN,
                    MaximumRate: double.NaN,
                    RateStep: double.NaN)
                .Rates()
                .SequenceEqual([1d]) &&
            new ChartExportOptions(
                    ChartExportFormat.Malody,
                    ChartExportMode.SingleRatePackage,
                    PreservePitch: false,
                    Rate: 1.25,
                    MinimumRate: 2,
                    MaximumRate: 1,
                    RateStep: 0)
                .Rates()
                .SequenceEqual([1.25d]);
        Require(
            evidence,
            defaultOptions.Format == ChartExportFormat.Malody &&
            defaultOptions.Mode == ChartExportMode.FullPackage &&
            !defaultOptions.PreservePitch &&
            Close(defaultOptions.Rate, 1.2) &&
            Close(defaultOptions.MinimumRate, .8) &&
            Close(defaultOptions.MaximumRate, 1.2) &&
            Close(defaultOptions.RateStep, .1) &&
            Close(defaultOptions.OsuOverallDifficulty, 8) &&
            Close(defaultOptions.OsuHpDrainRate, 8) &&
            malodyExtensions.SequenceEqual([".mc", ".mcz", ".mcz", ".mcz"]) &&
            osuExtensions.SequenceEqual([".osu", ".osz", ".osz", ".osz"]) &&
            hiddenFieldsIgnored,
            "four-mode-planner-defaults-extensions-and-hidden-rate-fields",
            $"malody={string.Join(',', malodyExtensions)}; osu={string.Join(',', osuExtensions)}; " +
            $"defaults={defaultOptions.Rate:0.####}/{defaultOptions.MinimumRate:0.####}-" +
            $"{defaultOptions.MaximumRate:0.####}/{defaultOptions.RateStep:0.####}; " +
            $"hiddenFieldsIgnored={hiddenFieldsIgnored}");

        var roundedSingle = Options(
                ChartExportFormat.Malody,
                ChartExportMode.SingleRatePackage,
                rate: 1.23456)
            .Rates();
        var nonDivisibleRange = Options(
                ChartExportFormat.Malody,
                ChartExportMode.RatePack,
                minimumRate: .8,
                maximumRate: 1.25,
                rateStep: .2)
            .Rates();
        var hundredRates = Options(
                ChartExportFormat.Malody,
                ChartExportMode.RatePack,
                minimumRate: .2,
                maximumRate: 2.18,
                rateStep: .02)
            .Rates();
        var hundredOneRejected = await ThrowsAsync<InvalidOperationException>(() => Task.FromResult(
                Options(
                        ChartExportFormat.Malody,
                        ChartExportMode.RatePack,
                        minimumRate: .2,
                        maximumRate: 2.2,
                        rateStep: .02)
                    .Rates()))
            .ConfigureAwait(false);
        Require(
            evidence,
            roundedSingle.SequenceEqual([1.2346d]) &&
            nonDivisibleRange.SequenceEqual([.8d, 1d, 1.2d, 1.25d]) &&
            hundredRates.Count == 100 &&
            Close(hundredRates[0], .2) &&
            Close(hundredRates[^1], 2.18) &&
            hundredOneRejected,
            "four-mode-rates-round-to-four-decimals-append-max-and-enforce-one-to-one-hundred",
            $"single={string.Join(',', roundedSingle)}; range={string.Join(',', nonDivisibleRange)}; " +
            $"hundred={hundredRates.Count}; hundredOneRejected={hundredOneRejected}");

        var sourceChartPath = Path.Combine(sourceRoot, "fixture.mc");
        var audioPath = Path.Combine(sourceRoot, "audio.wav");
        var coverPath = Path.Combine(sourceRoot, "cover.png");
        var backgroundPath = Path.Combine(sourceRoot, "background.jpg");
        var keySoundPath = Path.Combine(sourceRoot, "key.wav");
        await File.WriteAllBytesAsync(audioPath, Encoding.ASCII.GetBytes("fixture-audio"))
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(coverPath, Encoding.ASCII.GetBytes("fixture-cover"))
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(backgroundPath, Encoding.ASCII.GetBytes("fixture-background"))
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(keySoundPath, Encoding.ASCII.GetBytes("fixture-key-sound"))
            .ConfigureAwait(false);
        var document = new ChartDocument
        {
            FilePath = sourceChartPath,
            KeyCount = 4,
            Metadata = new ChartMetadata
            {
                Title = "Four mode fixture",
                TitleOriginal = "原曲/标题:2026.",
                Artist = "Avalonia Specs",
                Creator = "谱师/名",
                Version = "Disk version",
                Bpm = 120,
                PreviewTime = 1234,
                AudioFile = Path.GetFileName(audioPath),
                CoverFile = Path.GetFileName(coverPath),
                BackgroundFile = Path.GetFileName(backgroundPath)
            },
            Timing =
            [
                new TimingEvent
                {
                    Beat = BeatPosition.Preserve(0, 0, 1),
                    Bpm = 120,
                    Delay = 80
                }
            ],
            Notes =
            [
                new ChartNote
                {
                    Lane = 2,
                    Beat = BeatPosition.Preserve(3, 10, 12),
                    Sound = Path.GetFileName(keySoundPath)
                },
                new ChartNote
                {
                    Lane = 1,
                    Beat = BeatPosition.Preserve(5, 3, 8),
                    EndBeat = BeatPosition.Preserve(6, 7, 8)
                }
            ]
        };
        var originalTitleNames = new[]
        {
            Options(ChartExportFormat.Malody, ChartExportMode.SingleChart),
            Options(ChartExportFormat.Malody, ChartExportMode.FullPackage),
            Options(ChartExportFormat.OsuMania, ChartExportMode.SingleChart),
            Options(ChartExportFormat.OsuMania, ChartExportMode.RatePack)
        }.Select(options =>
        {
            var extension = ChartExportPlanner.Create(options).DestinationExtension;
            return ChartExportFileNamePolicy.SuggestedFileName(document, extension);
        }).ToArray();
        Require(
            evidence,
            originalTitleNames.SequenceEqual(
                [
                    "原曲_标题_2026 - 谱师_名.mc",
                    "原曲_标题_2026 - 谱师_名.mcz",
                    "原曲_标题_2026 - 谱师_名.osu",
                    "原曲_标题_2026 - 谱师_名.osz"
                ]) &&
            ChartExportFileNamePolicy.SuggestedFileName(
                new ChartDocument
                {
                    Metadata = new ChartMetadata
                    {
                        Title = "Fallback title",
                        TitleOriginal = " ",
                        Creator = "Mapper"
                    }
                },
                ".mc") == "Fallback title - Mapper.mc",
            "all-export-modes-default-to-sanitized-original-song-title-and-mapper",
            $"names={string.Join(',', originalTitleNames)}; fallback=Fallback title - Mapper.mc");
        MalodyMcSerializer.Save(document.Clone(), sourceChartPath);
        var diskHashBefore = await ComputeSha256Async(sourceChartPath).ConfigureAwait(false);
        document.Metadata.Version = "Dirty clone";
        document.Notes[0].Lane = 3;
        var dirtyFingerprint = ChartStructuralFingerprint.Compute(document);

        var packageSourceRoot = Path.Combine(sliceRoot, "live-package-source");
        var localizedCacheRoot = Path.Combine(sliceRoot, "localized-working-cache");
        var sourceResolutionOutputRoot = Path.Combine(sliceRoot, "source-resolution-output");
        Directory.CreateDirectory(packageSourceRoot);
        Directory.CreateDirectory(localizedCacheRoot);
        Directory.CreateDirectory(sourceResolutionOutputRoot);
        var packageSourceChartPath = Path.Combine(packageSourceRoot, "live.mc");
        var localizedWorkingChartPath = Path.Combine(localizedCacheRoot, "working.mc");
        var liveAudioName = "live-audio.wav";
        var liveCoverName = "live-cover.png";
        var liveKeySoundName = "live-key.wav";
        var liveAudioPath = Path.Combine(packageSourceRoot, liveAudioName);
        var liveCoverPath = Path.Combine(packageSourceRoot, liveCoverName);
        var liveKeySoundPath = Path.Combine(packageSourceRoot, liveKeySoundName);
        var cachedAudioPath = Path.Combine(localizedCacheRoot, liveAudioName);
        var cachedCoverPath = Path.Combine(localizedCacheRoot, liveCoverName);
        var cachedKeySoundPath = Path.Combine(localizedCacheRoot, liveKeySoundName);
        var staleAudioBytes = Encoding.UTF8.GetBytes("stale-localized-audio");
        var staleCoverBytes = Encoding.UTF8.GetBytes("stale-localized-cover");
        var staleKeySoundBytes = Encoding.UTF8.GetBytes("stale-localized-key-sound");
        var liveAudioBytes = Encoding.UTF8.GetBytes("latest-source-audio");
        var liveCoverBytes = Encoding.UTF8.GetBytes("latest-source-cover");
        var liveKeySoundBytes = Encoding.UTF8.GetBytes("latest-source-key-sound");
        await File.WriteAllBytesAsync(cachedAudioPath, staleAudioBytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(cachedCoverPath, staleCoverBytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(cachedKeySoundPath, staleKeySoundBytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(liveAudioPath, liveAudioBytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(liveCoverPath, liveCoverBytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(liveKeySoundPath, liveKeySoundBytes).ConfigureAwait(false);
        var localizedDocument = document.Clone();
        localizedDocument.FilePath = localizedWorkingChartPath;
        localizedDocument.Metadata.AudioFile = liveAudioName;
        localizedDocument.Metadata.CoverFile = liveCoverName;
        localizedDocument.Metadata.BackgroundFile = string.Empty;
        localizedDocument.Notes[0].Sound = liveKeySoundName;
        MalodyMcSerializer.Save(localizedDocument.Clone(), localizedWorkingChartPath);
        var packageSourceDocument = localizedDocument.Clone();
        packageSourceDocument.FilePath = packageSourceChartPath;
        MalodyMcSerializer.Save(packageSourceDocument, packageSourceChartPath);

        var liveSourceScratch = Path.Combine(sliceRoot, "live-source-scratch");
        var liveSourcePath = Path.Combine(sourceResolutionOutputRoot, "live-source-rate.mcz");
        var liveSourceTranscoder = new RecordingChartExportAudioTranscoder();
        await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    localizedDocument,
                    liveSourcePath,
                    Options(
                        ChartExportFormat.Malody,
                        ChartExportMode.SingleRatePackage,
                        rate: 1.25),
                    liveSourceScratch,
                    packageSourceChartPath),
                liveSourceTranscoder)
            .ConfigureAwait(false);
        byte[] exportedLiveCoverBytes;
        byte[] exportedLiveKeySoundBytes;
        using (var archive = ZipFile.OpenRead(liveSourcePath))
        {
            exportedLiveCoverBytes = await ReadZipEntryBytesAsync(archive.Entries.Single(entry =>
                    entry.Name.Equals(liveCoverName, StringComparison.OrdinalIgnoreCase)))
                .ConfigureAwait(false);
            exportedLiveKeySoundBytes = await ReadZipEntryBytesAsync(archive.Entries.Single(entry =>
                    entry.Name.Equals(liveKeySoundName, StringComparison.OrdinalIgnoreCase)))
                .ConfigureAwait(false);
        }
        var deletedLiveSourceScratch = Path.Combine(sliceRoot, "deleted-live-source-scratch");
        var deletedLiveSourcePath = Path.Combine(
            sourceResolutionOutputRoot,
            "deleted-live-source-rate.mcz");
        bool deletedLiveAudioRejected;
        File.Delete(liveAudioPath);
        try
        {
            deletedLiveAudioRejected = await ThrowsAsync<FileNotFoundException>(() =>
                    ChartExportService.ExportAsync(
                        new ChartExportRequest(
                            localizedDocument,
                            deletedLiveSourcePath,
                            Options(
                                ChartExportFormat.Malody,
                                ChartExportMode.SingleRatePackage,
                                rate: 1.25),
                            deletedLiveSourceScratch,
                            packageSourceChartPath),
                        new RecordingChartExportAudioTranscoder()))
                .ConfigureAwait(false);
        }
        finally
        {
            await File.WriteAllBytesAsync(liveAudioPath, liveAudioBytes).ConfigureAwait(false);
        }
        Require(
            evidence,
            PathsEqual(liveSourceTranscoder.Calls.Single().SourceAudioPath, liveAudioPath) &&
            exportedLiveCoverBytes.SequenceEqual(liveCoverBytes) &&
            exportedLiveKeySoundBytes.SequenceEqual(liveKeySoundBytes) &&
            deletedLiveAudioRejected &&
            !File.Exists(deletedLiveSourcePath) &&
            ScratchIsClean(liveSourceScratch) &&
            ScratchIsClean(deletedLiveSourceScratch),
            "rate-package-prefers-live-package-source-resources-over-localized-cache",
            $"audio={liveSourceTranscoder.Calls.Single().SourceAudioPath}; " +
            $"coverLive={exportedLiveCoverBytes.SequenceEqual(liveCoverBytes)}; " +
            $"keySoundLive={exportedLiveKeySoundBytes.SequenceEqual(liveKeySoundBytes)}; " +
            $"deletedSourceRejected={deletedLiveAudioRejected}");

        var oneXThreadScratch = Path.Combine(sliceRoot, "one-x-thread-scratch");
        var oneXThreadPath = Path.Combine(sourceResolutionOutputRoot, "one-x-thread.mcz");
        var progressThreadIds = new List<int>();
        var progressSync = new object();
        var callerThreadId = 0;
        Exception? oneXThreadFailure = null;
        ChartExportResult? oneXThreadResult = null;
        var oneXThread = new Thread(() =>
        {
            callerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                oneXThreadResult = ChartExportService.ExportAsync(
                        new ChartExportRequest(
                            localizedDocument,
                            oneXThreadPath,
                            Options(
                                ChartExportFormat.Malody,
                                ChartExportMode.SingleRatePackage,
                                rate: 1),
                            oneXThreadScratch,
                            packageSourceChartPath),
                        progress: new InlineProgress<ChartExportProgress>(_ =>
                        {
                            lock (progressSync)
                                progressThreadIds.Add(Environment.CurrentManagedThreadId);
                        }))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                oneXThreadFailure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "Avalonia Specs rate-package caller"
        };
        oneXThread.Start();
        if (!oneXThread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The 1.0x rate-package worker did not complete.");
        if (oneXThreadFailure != null) ExceptionDispatchInfo.Capture(oneXThreadFailure).Throw();
        Require(
            evidence,
            oneXThreadResult != null && File.Exists(oneXThreadResult.DestinationPath) &&
            progressThreadIds.Count > 0 && progressThreadIds.All(id => id != callerThreadId) &&
            ScratchIsClean(oneXThreadScratch),
            "rate-package-one-x-core-runs-off-caller-thread",
            $"caller={callerThreadId}; progressThreads={string.Join(',', progressThreadIds.Distinct())}; " +
            $"output={oneXThreadResult?.OutputBytes ?? 0}");

        var singleChartPath = Path.Combine(outputRoot, "dirty-single.mc");
        var singleChartResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    singleChartPath,
                    Options(ChartExportFormat.Malody, ChartExportMode.SingleChart)))
            .ConfigureAwait(false);
        var exportedSingle = MalodyMcSerializer.Load(singleChartPath);
        var singleOutputFiles = Directory.EnumerateFiles(
                outputRoot,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        Require(
            evidence,
            singleChartResult.EntryCount == 1 &&
            singleChartResult.ChartEntryPaths.SequenceEqual([singleChartPath]) &&
            exportedSingle.Metadata.Version == "Dirty clone" &&
            exportedSingle.Notes.Any(note => note.Lane == 3 &&
                                             ExactBeat(note.Beat, BeatPosition.Preserve(3, 10, 12))) &&
            string.Equals(
                ChartStructuralFingerprint.Compute(document),
                dirtyFingerprint,
                StringComparison.Ordinal) &&
            string.Equals(
                await ComputeSha256Async(sourceChartPath).ConfigureAwait(false),
                diskHashBefore,
                StringComparison.Ordinal) &&
            singleOutputFiles.Length == 1 &&
            !File.Exists(Path.Combine(outputRoot, Path.GetFileName(audioPath))) &&
            !File.Exists(Path.Combine(outputRoot, Path.GetFileName(keySoundPath))),
            "single-chart-export-uses-dirty-clone-without-source-or-resource-mutation",
            $"version={exportedSingle.Metadata.Version}; outputFiles={singleOutputFiles.Length}; " +
            $"sourceUnchanged={string.Equals(await ComputeSha256Async(sourceChartPath).ConfigureAwait(false), diskHashBefore, StringComparison.Ordinal)}; " +
            $"dirtyUnchanged={string.Equals(ChartStructuralFingerprint.Compute(document), dirtyFingerprint, StringComparison.Ordinal)}");

        var singleRateScratch = Path.Combine(sliceRoot, "single-rate-scratch");
        var singleRatePath = Path.Combine(outputRoot, "single-rate.mcz");
        var singleRateTranscoder = new RecordingChartExportAudioTranscoder();
        var singleRateResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    singleRatePath,
                    Options(
                        ChartExportFormat.Malody,
                        ChartExportMode.SingleRatePackage,
                        preservePitch: true,
                        rate: 1.25),
                    singleRateScratch),
                singleRateTranscoder)
            .ConfigureAwait(false);
        string singleRateMcText;
        string[] singleRateEntries;
        using (var archive = ZipFile.OpenRead(singleRatePath))
        {
            singleRateEntries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToArray();
            singleRateMcText = await ReadZipEntryTextAsync(archive.Entries.Single(entry =>
                    Path.GetExtension(entry.FullName).Equals(".mc", StringComparison.OrdinalIgnoreCase)))
                .ConfigureAwait(false);
        }
        var singleRateDocument = MalodyMcSerializer.Parse(singleRateMcText, "single-rate.mc", "Avalonia Specs");
        var singleCall = singleRateTranscoder.Calls.Single();
        var transformedBeat = singleRateDocument.Notes
            .Single(note => !string.IsNullOrWhiteSpace(note.Sound))
            .Beat;
        Require(
            evidence,
            singleRateResult.ChartEntryPaths.Count == 1 &&
            PathsEqual(singleCall.SourceAudioPath, audioPath) &&
            Close(singleCall.Rate, 1.25) &&
            singleCall.PreservePitch &&
            Close(singleRateDocument.Metadata.Bpm, 150) &&
            singleRateDocument.Metadata.PreviewTime == 987 &&
            singleRateDocument.Metadata.Version == "Dirty clone 1.25x" &&
            Close(singleRateDocument.Timing.Single().Bpm ?? 0, 150) &&
            Close(singleRateDocument.Timing.Single().Delay, 64) &&
            ExactBeat(transformedBeat, BeatPosition.Preserve(3, 10, 12)) &&
            ScratchIsClean(singleRateScratch),
            "single-rate-package-transcoder-and-rational-transform-match-wpf",
            $"call={singleCall.Rate:0.####}/{singleCall.PreservePitch}; bpm={singleRateDocument.Metadata.Bpm}; " +
            $"delay={singleRateDocument.Timing.Single().Delay}; preview={singleRateDocument.Metadata.PreviewTime}; " +
            $"version={singleRateDocument.Metadata.Version}; beat={transformedBeat}; entries={singleRateEntries.Length}");

        var ratePackScratch = Path.Combine(sliceRoot, "rate-pack-scratch");
        var ratePackPath = Path.Combine(outputRoot, "rate-pack.mcz");
        var ratePackTranscoder = new RecordingChartExportAudioTranscoder();
        var ratePackResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    ratePackPath,
                    Options(
                        ChartExportFormat.Malody,
                        ChartExportMode.RatePack,
                        preservePitch: false,
                        minimumRate: .8,
                        maximumRate: 1.25,
                        rateStep: .2),
                    ratePackScratch),
                ratePackTranscoder)
            .ConfigureAwait(false);
        var ratePackDocuments = new List<ChartDocument>();
        string[] ratePackEntries;
        using (var archive = ZipFile.OpenRead(ratePackPath))
        {
            var fileEntries = archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)).ToArray();
            ratePackEntries = fileEntries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            foreach (var entry in fileEntries.Where(entry =>
                         Path.GetExtension(entry.FullName).Equals(".mc", StringComparison.OrdinalIgnoreCase)))
            {
                var text = await ReadZipEntryTextAsync(entry).ConfigureAwait(false);
                ratePackDocuments.Add(MalodyMcSerializer.Parse(text, entry.FullName, "Avalonia Specs"));
            }
        }
        var ratePackRates = ratePackTranscoder.Calls.Select(call => call.Rate).Order().ToArray();
        var sharedNames = new[]
        {
            Path.GetFileName(coverPath),
            Path.GetFileName(backgroundPath),
            Path.GetFileName(keySoundPath),
            Path.GetFileName(audioPath)
        };
        var sharedExactlyOnce = sharedNames.All(name => ratePackEntries.Count(entry =>
            Path.GetFileName(entry).Equals(name, StringComparison.OrdinalIgnoreCase)) == 1);
        var rateOne = ratePackDocuments.Single(exported => exported.Metadata.Version == "Dirty clone");
        Require(
            evidence,
            ratePackResult.ChartEntryPaths.Count == 4 &&
            ratePackDocuments.Count == 4 &&
            ratePackRates.SequenceEqual([.8d, 1.2d, 1.25d]) &&
            ratePackTranscoder.Calls.All(call => !call.PreservePitch) &&
            sharedExactlyOnce &&
            Close(rateOne.Metadata.Bpm, 120) &&
            rateOne.Metadata.PreviewTime == 1234 &&
            ExactBeat(
                rateOne.Notes.Single(note => !string.IsNullOrWhiteSpace(note.Sound)).Beat,
                BeatPosition.Preserve(3, 10, 12)) &&
            ScratchIsClean(ratePackScratch),
            "rate-pack-transcoder-preserve-pitch-and-shared-resource-semantics",
            $"charts={ratePackDocuments.Count}; calls={string.Join(',', ratePackRates)}; " +
            $"preservePitch={string.Join(',', ratePackTranscoder.Calls.Select(call => call.PreservePitch))}; " +
            $"sharedExactlyOnce={sharedExactlyOnce}; entries={ratePackEntries.Length}");

        var ratePackReverseImport =
            await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                    ratePackPath,
                    Path.Combine(sliceRoot, "rate-pack-fresh-import"))
                .ConfigureAwait(false);
        var ratePackFreshDocuments = new List<ChartDocument>();
        foreach (var chartPath in ratePackReverseImport.ChartPaths)
        {
            ratePackFreshDocuments.Add(
                await ImportFreshChartAsync(chartPath).ConfigureAwait(false));
        }
        var ratePackArchiveFingerprints = ratePackDocuments
            .Select(ChartStructuralFingerprint.Compute)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var ratePackFreshFingerprints = ratePackFreshDocuments
            .Select(ChartStructuralFingerprint.Compute)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var ratePackFreshResourcesPresent = ratePackReverseImport.ChartPaths
            .Zip(ratePackFreshDocuments)
            .All(pair => ReferencedResourcesExist(pair.First, pair.Second));
        Require(
            evidence,
            ratePackReverseImport.ChartPaths.Count == 4 &&
            ratePackArchiveFingerprints.SequenceEqual(ratePackFreshFingerprints) &&
            ratePackFreshResourcesPresent,
            "malody-rate-pack-fresh-import-retains-all-chart-structures-and-resources",
            $"charts={ratePackFreshDocuments.Count}; " +
            $"fingerprints={ratePackFreshFingerprints.Length}; " +
            $"resources={ratePackFreshResourcesPresent}");

        var osuScratch = Path.Combine(sliceRoot, "osu-rate-scratch");
        var osuPath = Path.Combine(outputRoot, "single-rate.osz");
        var osuTranscoder = new RecordingChartExportAudioTranscoder();
        await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    osuPath,
                    Options(
                        ChartExportFormat.OsuMania,
                        ChartExportMode.SingleRatePackage,
                        rate: 1.5),
                    osuScratch),
                osuTranscoder)
            .ConfigureAwait(false);
        string[] osuEntries;
        string osuChartText;
        using (var archive = ZipFile.OpenRead(osuPath))
        {
            var fileEntries = archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)).ToArray();
            osuEntries = fileEntries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            osuChartText = await ReadZipEntryTextAsync(fileEntries.Single(entry =>
                    Path.GetExtension(entry.FullName).Equals(".osu", StringComparison.OrdinalIgnoreCase)))
                .ConfigureAwait(false);
        }
        var compactMalody = !singleRateMcText.Contains('\n') &&
                            JsonNode.Parse(singleRateMcText) is JsonObject;
        var flatSafeOsz = osuEntries.All(entry =>
            !entry.Contains('/') &&
            !entry.EndsWith(".mc", StringComparison.OrdinalIgnoreCase) &&
            !entry.EndsWith(".mcz", StringComparison.OrdinalIgnoreCase) &&
            !entry.EndsWith(".osz", StringComparison.OrdinalIgnoreCase) &&
            !entry.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase) &&
            !entry.EndsWith(".luma-export.tmp", StringComparison.OrdinalIgnoreCase));
        Require(
            evidence,
            compactMalody &&
            flatSafeOsz &&
            osuEntries.Count(entry => entry.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)) == 1 &&
            osuEntries.Count(entry => entry.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) == 1 &&
            osuChartText.StartsWith("osu file format v14", StringComparison.Ordinal) &&
            osuTranscoder.Calls is [{ Rate: 1.5, PreservePitch: false }] &&
            ScratchIsClean(osuScratch),
            "malody-rate-package-is-compact-and-osz-package-is-flat-compatible",
            $"compactMc={compactMalody}; osuEntries={string.Join(',', osuEntries)}; " +
            $"osuV14={osuChartText.StartsWith("osu file format v14", StringComparison.Ordinal)}");

        var osuArchiveDocument = ChartDocumentImporter.ImportText(
            osuChartText,
            ".osu",
            "Avalonia Specs");
        var osuReverseImport =
            await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                    osuPath,
                    Path.Combine(sliceRoot, "osu-rate-fresh-import"))
                .ConfigureAwait(false);
        var osuFreshPath = osuReverseImport.ChartPaths.Single();
        var osuFreshDocument =
            await ImportFreshChartAsync(osuFreshPath).ConfigureAwait(false);
        var osuArchiveFingerprint =
            ChartStructuralFingerprint.Compute(osuArchiveDocument);
        var osuFreshFingerprint =
            ChartStructuralFingerprint.Compute(osuFreshDocument);
        var osuFreshResourcesPresent =
            ReferencedResourcesExist(osuFreshPath, osuFreshDocument);
        Require(
            evidence,
            osuReverseImport.ChartPaths.Count == 1 &&
            string.Equals(
                osuArchiveFingerprint,
                osuFreshFingerprint,
                StringComparison.Ordinal) &&
            osuFreshResourcesPresent,
            "osu-single-rate-package-fresh-import-retains-chart-structure-and-resources",
            $"charts={osuReverseImport.ChartPaths.Count}; " +
            $"fingerprint={osuFreshFingerprint}; resources={osuFreshResourcesPresent}");

        var osuSingleChartPath = Path.Combine(outputRoot, "single-chart.osu");
        var osuSingleChartResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    osuSingleChartPath,
                    Options(
                        ChartExportFormat.OsuMania,
                        ChartExportMode.SingleChart)))
            .ConfigureAwait(false);
        var osuSingleChartText =
            await File.ReadAllTextAsync(osuSingleChartPath).ConfigureAwait(false);
        var osuSingleChartArchiveDocument = ChartDocumentImporter.ImportText(
            osuSingleChartText,
            ".osu",
            "Avalonia Specs");
        var osuSingleChartFreshDocument =
            await ImportFreshChartAsync(osuSingleChartPath).ConfigureAwait(false);
        var osuSingleChartFingerprint =
            ChartStructuralFingerprint.Compute(osuSingleChartFreshDocument);
        Require(
            evidence,
            osuSingleChartResult.EntryCount == 1 &&
            osuSingleChartResult.ChartEntryPaths.SequenceEqual([osuSingleChartPath]) &&
            osuSingleChartText.StartsWith("osu file format v14", StringComparison.Ordinal) &&
            string.Equals(
                ChartStructuralFingerprint.Compute(osuSingleChartArchiveDocument),
                osuSingleChartFingerprint,
                StringComparison.Ordinal) &&
            string.Equals(
                await ComputeSha256Async(sourceChartPath).ConfigureAwait(false),
                diskHashBefore,
                StringComparison.Ordinal),
            "osu-single-chart-export-fresh-import-retains-dirty-chart-structure",
            $"entries={osuSingleChartResult.EntryCount}; " +
            $"fingerprint={osuSingleChartFingerprint}; " +
            $"sourceUnchanged={string.Equals(await ComputeSha256Async(sourceChartPath).ConfigureAwait(false), diskHashBefore, StringComparison.Ordinal)}");

        var osuRatePackScratch = Path.Combine(sliceRoot, "osu-rate-pack-scratch");
        var osuRatePackPath = Path.Combine(outputRoot, "rate-pack.osz");
        var osuRatePackTranscoder = new RecordingChartExportAudioTranscoder();
        var osuRatePackResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    osuRatePackPath,
                    Options(
                        ChartExportFormat.OsuMania,
                        ChartExportMode.RatePack,
                        preservePitch: true,
                        minimumRate: .8,
                        maximumRate: 1.25,
                        rateStep: .2),
                    osuRatePackScratch),
                osuRatePackTranscoder)
            .ConfigureAwait(false);
        var osuRatePackArchiveDocuments = new List<ChartDocument>();
        using (var archive = ZipFile.OpenRead(osuRatePackPath))
        {
            foreach (var entry in archive.Entries.Where(entry =>
                         Path.GetExtension(entry.FullName).Equals(
                             ".osu",
                             StringComparison.OrdinalIgnoreCase)))
            {
                var text = await ReadZipEntryTextAsync(entry).ConfigureAwait(false);
                osuRatePackArchiveDocuments.Add(
                    ChartDocumentImporter.ImportText(
                        text,
                        ".osu",
                        "Avalonia Specs"));
            }
        }
        var osuRatePackReverseImport =
            await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                    osuRatePackPath,
                    Path.Combine(sliceRoot, "osu-rate-pack-fresh-import"))
                .ConfigureAwait(false);
        var osuRatePackFreshDocuments = new List<ChartDocument>();
        foreach (var chartPath in osuRatePackReverseImport.ChartPaths)
        {
            osuRatePackFreshDocuments.Add(
                await ImportFreshChartAsync(chartPath).ConfigureAwait(false));
        }
        var osuRatePackArchiveFingerprints = osuRatePackArchiveDocuments
            .Select(ChartStructuralFingerprint.Compute)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var osuRatePackFreshFingerprints = osuRatePackFreshDocuments
            .Select(ChartStructuralFingerprint.Compute)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var osuRatePackFreshResourcesPresent =
            osuRatePackReverseImport.ChartPaths
                .Zip(osuRatePackFreshDocuments)
                .All(pair => ReferencedResourcesExist(pair.First, pair.Second));
        var osuRatePackRates = osuRatePackTranscoder.Calls
            .Select(call => call.Rate)
            .Order()
            .ToArray();
        Require(
            evidence,
            osuRatePackResult.ChartEntryPaths.Count == 4 &&
            osuRatePackReverseImport.ChartPaths.Count == 4 &&
            osuRatePackArchiveDocuments.Count == 4 &&
            osuRatePackArchiveFingerprints.SequenceEqual(
                osuRatePackFreshFingerprints) &&
            osuRatePackFreshResourcesPresent &&
            osuRatePackRates.SequenceEqual([.8d, 1.2d, 1.25d]) &&
            osuRatePackTranscoder.Calls.All(call => call.PreservePitch) &&
            ScratchIsClean(osuRatePackScratch),
            "osu-rate-pack-fresh-import-retains-all-chart-structures-and-resources",
            $"charts={osuRatePackFreshDocuments.Count}; " +
            $"calls={string.Join(',', osuRatePackRates)}; " +
            $"resources={osuRatePackFreshResourcesPresent}");

        byte[] oldTargetBytes = Encoding.UTF8.GetBytes("pre-existing-rate-package-target");
        var canceledScratch = Path.Combine(sliceRoot, "canceled-rate-scratch");
        var canceledPath = Path.Combine(outputRoot, "canceled-rate.mcz");
        await File.WriteAllBytesAsync(canceledPath, oldTargetBytes).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var compressionBoundaryObserved = false;
        var cancellationProgress = new InlineProgress<ChartExportProgress>(progress =>
        {
            if (!progress.Stage.Equals("正在压缩谱包", StringComparison.Ordinal)) return;
            compressionBoundaryObserved = true;
            cancellation.Cancel();
        });
        var cancellationObserved = await ThrowsAsync<OperationCanceledException>(() =>
                ChartExportService.ExportAsync(
                    new ChartExportRequest(
                        document,
                        canceledPath,
                        Options(
                            ChartExportFormat.Malody,
                            ChartExportMode.SingleRatePackage,
                            rate: 1.2),
                        canceledScratch),
                    new RecordingChartExportAudioTranscoder(),
                    cancellationProgress,
                    cancellation.Token))
            .ConfigureAwait(false);
        Require(
            evidence,
            compressionBoundaryObserved &&
            cancellationObserved &&
            (await File.ReadAllBytesAsync(canceledPath).ConfigureAwait(false)).SequenceEqual(oldTargetBytes) &&
            TemporaryArchives(outputRoot).Length == 0 &&
            ScratchIsClean(canceledScratch),
            "rate-package-cancel-before-commit-preserves-target-and-cleans-scratch",
            $"boundary={compressionBoundaryObserved}; canceled={cancellationObserved}; " +
            $"oldPreserved={(await File.ReadAllBytesAsync(canceledPath).ConfigureAwait(false)).SequenceEqual(oldTargetBytes)}; " +
            $"temporaryArchives={TemporaryArchives(outputRoot).Length}; scratchClean={ScratchIsClean(canceledScratch)}");

        var midCompressionScratch = Path.Combine(sliceRoot, "mid-compression-cancel-scratch");
        var midCompressionPath = Path.Combine(outputRoot, "mid-compression-cancel.mcz");
        await File.WriteAllBytesAsync(midCompressionPath, oldTargetBytes).ConfigureAwait(false);
        using var midCompressionCancellation = new CancellationTokenSource();
        Task<bool>? compressionWatcher = null;
        var midCompressionProgress = new InlineProgress<ChartExportProgress>(progress =>
        {
            if (!progress.Stage.Equals("正在压缩谱包", StringComparison.Ordinal)) return;
            compressionWatcher = Task.Run(async () =>
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    if (TemporaryArchives(outputRoot).Length > 0)
                    {
                        midCompressionCancellation.Cancel();
                        return true;
                    }

                    await Task.Delay(2).ConfigureAwait(false);
                }

                return false;
            });
        });
        var midCompressionCancellationObserved = await ThrowsAsync<OperationCanceledException>(() =>
                ChartExportService.ExportAsync(
                    new ChartExportRequest(
                        document,
                        midCompressionPath,
                        Options(
                            ChartExportFormat.Malody,
                            ChartExportMode.SingleRatePackage,
                            rate: 1.2),
                        midCompressionScratch),
                    new LargeChartExportAudioTranscoder(16 * 1024 * 1024),
                    midCompressionProgress,
                    midCompressionCancellation.Token))
            .ConfigureAwait(false);
        var compressionArchiveObserved = compressionWatcher != null &&
                                         await compressionWatcher.ConfigureAwait(false);
        Require(
            evidence,
            compressionArchiveObserved && midCompressionCancellationObserved &&
            (await File.ReadAllBytesAsync(midCompressionPath).ConfigureAwait(false)).SequenceEqual(oldTargetBytes) &&
            TemporaryArchives(outputRoot).Length == 0 &&
            ScratchIsClean(midCompressionScratch),
            "rate-package-cancel-during-compression-preserves-target-and-cleans-scratch",
            $"archiveObserved={compressionArchiveObserved}; canceled={midCompressionCancellationObserved}; " +
            $"oldPreserved={(await File.ReadAllBytesAsync(midCompressionPath).ConfigureAwait(false)).SequenceEqual(oldTargetBytes)}; " +
            $"temporaryArchives={TemporaryArchives(outputRoot).Length}; " +
            $"scratchClean={ScratchIsClean(midCompressionScratch)}");

        var failedScratch = Path.Combine(sliceRoot, "failed-rate-scratch");
        var failedPath = Path.Combine(outputRoot, "failed-rate.mcz");
        await File.WriteAllBytesAsync(failedPath, oldTargetBytes).ConfigureAwait(false);
        Exception? replacementFailure = null;
        await using (var destinationLock = new FileStream(
                         failedPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            try
            {
                await ChartExportService.ExportAsync(
                        new ChartExportRequest(
                            document,
                            failedPath,
                            Options(
                                ChartExportFormat.Malody,
                                ChartExportMode.SingleRatePackage,
                                rate: 1.2),
                            failedScratch),
                        new RecordingChartExportAudioTranscoder())
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                replacementFailure = exception;
            }
        }
        Require(
            evidence,
            replacementFailure != null &&
            (await File.ReadAllBytesAsync(failedPath).ConfigureAwait(false)).SequenceEqual(oldTargetBytes) &&
            TemporaryArchives(outputRoot).Length == 0 &&
            ScratchIsClean(failedScratch),
            "rate-package-failed-atomic-replace-preserves-target-and-cleans-scratch",
            $"failure={replacementFailure?.GetType().Name ?? "none"}; " +
            $"oldPreserved={(await File.ReadAllBytesAsync(failedPath).ConfigureAwait(false)).SequenceEqual(oldTargetBytes)}; " +
            $"temporaryArchives={TemporaryArchives(outputRoot).Length}; scratchClean={ScratchIsClean(failedScratch)}");

        var realAudioRoot = Path.Combine(sliceRoot, "windows-audio-adapter");
        Directory.CreateDirectory(realAudioRoot);
        var realWavePath = Path.Combine(realAudioRoot, "source.wav");
        var rateOggPath = Path.Combine(realAudioRoot, "rate.ogg");
        var preservePitchOggPath = Path.Combine(realAudioRoot, "preserve-pitch.ogg");
        WritePcm16StereoWave(realWavePath, .75);
        var realWaveHash = await ComputeSha256Async(realWavePath).ConfigureAwait(false);
        await WindowsChartExportAudioTranscoder.Instance.ConvertToOggAsync(
                realWavePath,
                rateOggPath,
                1.25,
                preservePitch: false)
            .ConfigureAwait(false);
        await WindowsChartExportAudioTranscoder.Instance.ConvertToOggAsync(
                realWavePath,
                preservePitchOggPath,
                1.25,
                preservePitch: true)
            .ConfigureAwait(false);
        var rateHeader = (await File.ReadAllBytesAsync(rateOggPath).ConfigureAwait(false)).Take(4).ToArray();
        var preservePitchHeader = (await File.ReadAllBytesAsync(preservePitchOggPath).ConfigureAwait(false))
            .Take(4)
            .ToArray();
        double rateDuration;
        double preservePitchDuration;
        int rateSampleRate;
        int preservePitchSampleRate;
        int rateChannels;
        int preservePitchChannels;
        using (var reader = new NAudio.Vorbis.VorbisWaveReader(rateOggPath))
        {
            rateDuration = reader.TotalTime.TotalSeconds;
            rateSampleRate = reader.WaveFormat.SampleRate;
            rateChannels = reader.WaveFormat.Channels;
        }
        using (var reader = new NAudio.Vorbis.VorbisWaveReader(preservePitchOggPath))
        {
            preservePitchDuration = reader.TotalTime.TotalSeconds;
            preservePitchSampleRate = reader.WaveFormat.SampleRate;
            preservePitchChannels = reader.WaveFormat.Channels;
        }
        var rateFrequency = EstimateToneFrequency(rateOggPath);
        var preservePitchFrequency = EstimateToneFrequency(preservePitchOggPath);
        Require(
            evidence,
            Encoding.ASCII.GetString(rateHeader) == "OggS" &&
            Encoding.ASCII.GetString(preservePitchHeader) == "OggS" &&
            rateSampleRate == 44_100 && preservePitchSampleRate == 44_100 &&
            rateChannels == 2 && preservePitchChannels == 2 &&
            rateDuration is > .45 and < .75 &&
            preservePitchDuration is > .35 and < .9 &&
            new FileInfo(rateOggPath).Length > 256 &&
            new FileInfo(preservePitchOggPath).Length > 256 &&
            !File.Exists(rateOggPath + ".luma-part") &&
            !File.Exists(preservePitchOggPath + ".luma-part") &&
            string.Equals(
                await ComputeSha256Async(realWavePath).ConfigureAwait(false),
                realWaveHash,
                StringComparison.Ordinal),
            "windows-audio-adapter-transcodes-valid-rate-and-preserve-pitch-ogg",
            $"rate={rateDuration:0.000}s/{new FileInfo(rateOggPath).Length}B; " +
            $"preservePitch={preservePitchDuration:0.000}s/{new FileInfo(preservePitchOggPath).Length}B; " +
            $"format={rateSampleRate}Hz/{rateChannels}ch");
        Require(
            evidence,
            rateFrequency is > 530 and < 570 &&
            preservePitchFrequency is > 420 and < 460,
            "windows-audio-adapter-rate-and-preserve-pitch-dominant-frequency",
            $"rate={rateFrequency:0.0}Hz; preservePitch={preservePitchFrequency:0.0}Hz");

        var committedSinglePath = Path.Combine(outputRoot, "committed-cancellation-boundary.mc");
        using var committedSingleCancellation = new CancellationTokenSource();
        var committedSingleBoundaryObserved = false;
        var committedSingleProgress = new InlineProgress<ChartExportProgress>(value =>
        {
            if (!value.Stage.Equals("完成", StringComparison.Ordinal)) return;
            committedSingleBoundaryObserved = true;
            committedSingleCancellation.Cancel();
        });
        var committedSingleResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    committedSinglePath,
                    Options(ChartExportFormat.Malody, ChartExportMode.SingleChart)),
                progress: committedSingleProgress,
                cancellationToken: committedSingleCancellation.Token)
            .ConfigureAwait(false);
        var committedSingle = MalodyMcSerializer.Load(committedSinglePath);
        Require(
            evidence,
            committedSingleBoundaryObserved && committedSingleCancellation.IsCancellationRequested &&
            PathsEqual(committedSingleResult.DestinationPath, committedSinglePath) &&
            committedSingle.Metadata.Version == "Dirty clone" &&
            committedSingle.Notes.Any(note =>
                note.Lane == 3 && ExactBeat(note.Beat, BeatPosition.Preserve(3, 10, 12))),
            "single-chart-commit-boundary-does-not-report-late-cancellation",
            $"boundary={committedSingleBoundaryObserved}; canceled={committedSingleCancellation.IsCancellationRequested}; " +
            $"version={committedSingle.Metadata.Version}; bytes={committedSingleResult.OutputBytes}");

        var singleChartOldBytes = Encoding.UTF8.GetBytes("pre-existing-single-chart-target");
        var canceledSingleChartPath = Path.Combine(outputRoot, "canceled-single-chart.mc");
        await File.WriteAllBytesAsync(canceledSingleChartPath, singleChartOldBytes).ConfigureAwait(false);
        using var singleChartCancellation = new CancellationTokenSource();
        var singleChartCommitBoundaryObserved = false;
        string? canceledSingleChartPartPath = null;
        var singleChartCancellationProgress = new InlineProgress<ChartExportProgress>(value =>
        {
            if (!value.Stage.Equals("正在提交单谱文件", StringComparison.Ordinal)) return;
            singleChartCommitBoundaryObserved = true;
            canceledSingleChartPartPath = TemporarySingleChartParts(outputRoot).SingleOrDefault();
            singleChartCancellation.Cancel();
        });
        var singleChartCancellationObserved = await ThrowsAsync<OperationCanceledException>(() =>
                ChartExportService.ExportAsync(
                    new ChartExportRequest(
                        document,
                        canceledSingleChartPath,
                        Options(ChartExportFormat.Malody, ChartExportMode.SingleChart)),
                    progress: singleChartCancellationProgress,
                    cancellationToken: singleChartCancellation.Token))
            .ConfigureAwait(false);
        var canceledSingleChartOldPreserved =
            (await File.ReadAllBytesAsync(canceledSingleChartPath).ConfigureAwait(false))
            .SequenceEqual(singleChartOldBytes);
        Require(
            evidence,
            singleChartCommitBoundaryObserved &&
            canceledSingleChartPartPath != null &&
            PathsEqual(Path.GetDirectoryName(canceledSingleChartPartPath)!, outputRoot) &&
            singleChartCancellationObserved &&
            canceledSingleChartOldPreserved &&
            TemporarySingleChartParts(outputRoot).Length == 0,
            "single-chart-cancel-after-same-directory-staging-preserves-existing-target-and-cleans-part",
            $"boundary={singleChartCommitBoundaryObserved}; part={canceledSingleChartPartPath ?? "none"}; " +
            $"canceled={singleChartCancellationObserved}; oldPreserved={canceledSingleChartOldPreserved}; " +
            $"remainingParts={TemporarySingleChartParts(outputRoot).Length}");

        var failedSingleChartPath = Path.Combine(outputRoot, "failed-single-chart-replace.mc");
        await File.WriteAllBytesAsync(failedSingleChartPath, singleChartOldBytes).ConfigureAwait(false);
        var failedSingleChartPartObserved = false;
        var failedSingleChartProgress = new InlineProgress<ChartExportProgress>(value =>
        {
            if (!value.Stage.Equals("正在提交单谱文件", StringComparison.Ordinal)) return;
            failedSingleChartPartObserved = TemporarySingleChartParts(outputRoot).Any(path =>
                PathsEqual(Path.GetDirectoryName(path)!, outputRoot));
        });
        Exception? singleChartReplacementFailure = null;
        await using (var destinationLock = new FileStream(
                         failedSingleChartPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            try
            {
                await ChartExportService.ExportAsync(
                        new ChartExportRequest(
                            document,
                            failedSingleChartPath,
                            Options(ChartExportFormat.Malody, ChartExportMode.SingleChart)),
                        progress: failedSingleChartProgress)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                singleChartReplacementFailure = exception;
            }
        }
        var failedSingleChartOldPreserved =
            (await File.ReadAllBytesAsync(failedSingleChartPath).ConfigureAwait(false))
            .SequenceEqual(singleChartOldBytes);
        Require(
            evidence,
            failedSingleChartPartObserved &&
            singleChartReplacementFailure != null &&
            failedSingleChartOldPreserved &&
            TemporarySingleChartParts(outputRoot).Length == 0,
            "single-chart-failed-atomic-replace-preserves-existing-target-and-cleans-part",
            $"staged={failedSingleChartPartObserved}; " +
            $"failure={singleChartReplacementFailure?.GetType().Name ?? "none"}; " +
            $"oldPreserved={failedSingleChartOldPreserved}; " +
            $"remainingParts={TemporarySingleChartParts(outputRoot).Length}");

        var successfulSingleChartPath = Path.Combine(outputRoot, "successful-single-chart-replace.mc");
        await File.WriteAllBytesAsync(successfulSingleChartPath, singleChartOldBytes).ConfigureAwait(false);
        var successfulSingleChartPartObserved = false;
        var successfulSingleChartOldVisibleBeforeCommit = false;
        var successfulSingleChartStagedContentValid = false;
        var successfulSingleChartProgress = new InlineProgress<ChartExportProgress>(value =>
        {
            if (!value.Stage.Equals("正在提交单谱文件", StringComparison.Ordinal)) return;
            var partPath = TemporarySingleChartParts(outputRoot).SingleOrDefault();
            successfulSingleChartPartObserved = partPath != null &&
                                                PathsEqual(Path.GetDirectoryName(partPath)!, outputRoot);
            successfulSingleChartOldVisibleBeforeCommit =
                File.ReadAllBytes(successfulSingleChartPath).SequenceEqual(singleChartOldBytes);
            if (partPath == null) return;
            var staged = MalodyMcSerializer.Load(partPath);
            successfulSingleChartStagedContentValid = staged.Metadata.Version == "Dirty clone" &&
                                                      staged.Notes.Any(note =>
                                                          note.Lane == 3 &&
                                                          ExactBeat(
                                                              note.Beat,
                                                              BeatPosition.Preserve(3, 10, 12)));
        });
        var successfulSingleChartResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    successfulSingleChartPath,
                    Options(ChartExportFormat.Malody, ChartExportMode.SingleChart)),
                progress: successfulSingleChartProgress)
            .ConfigureAwait(false);
        var successfulSingleChart = MalodyMcSerializer.Load(successfulSingleChartPath);
        var successfulSingleChartReplaced =
            !(await File.ReadAllBytesAsync(successfulSingleChartPath).ConfigureAwait(false))
            .SequenceEqual(singleChartOldBytes);
        Require(
            evidence,
            successfulSingleChartPartObserved &&
            successfulSingleChartOldVisibleBeforeCommit &&
            successfulSingleChartStagedContentValid &&
            successfulSingleChartReplaced &&
            PathsEqual(successfulSingleChartResult.DestinationPath, successfulSingleChartPath) &&
            successfulSingleChart.Metadata.Version == "Dirty clone" &&
            successfulSingleChartResult.OutputBytes == new FileInfo(successfulSingleChartPath).Length &&
            TemporarySingleChartParts(outputRoot).Length == 0,
            "single-chart-successfully-atomically-replaces-existing-target-from-same-directory-part",
            $"staged={successfulSingleChartPartObserved}; oldVisibleBeforeCommit={successfulSingleChartOldVisibleBeforeCommit}; " +
            $"stagedValid={successfulSingleChartStagedContentValid}; replaced={successfulSingleChartReplaced}; " +
            $"bytes={successfulSingleChartResult.OutputBytes}; remainingParts={TemporarySingleChartParts(outputRoot).Length}");

        var longPathRoot = Path.Combine(sliceRoot, "Unicode 空格 long-path export");
        var longPathDepth = 0;
        while (Path.Combine(longPathRoot, "单谱 输出 with spaces.mc").Length <= 280)
        {
            longPathRoot = Path.Combine(
                longPathRoot,
                $"层级 {longPathDepth++:00} Unicode path segment 0123456789abcdef");
        }
        Directory.CreateDirectory(longPathRoot);
        var longSingleChartPath = Path.Combine(longPathRoot, "单谱 输出 with spaces.mc");
        var longRatePackPath = Path.Combine(longPathRoot, "倍率包 输出 with spaces.mcz");
        var longScratchRoot = Path.Combine(longPathRoot, "会话 scratch with spaces");
        var longSingleResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    longSingleChartPath,
                    Options(ChartExportFormat.Malody, ChartExportMode.SingleChart)))
            .ConfigureAwait(false);
        var longSingleDocument = MalodyMcSerializer.Load(longSingleChartPath);
        var longRateTranscoder = new RecordingChartExportAudioTranscoder();
        var longRateResult = await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    document,
                    longRatePackPath,
                    Options(
                        ChartExportFormat.Malody,
                        ChartExportMode.RatePack,
                        minimumRate: 1,
                        maximumRate: 1.2,
                        rateStep: .2),
                    longScratchRoot),
                longRateTranscoder)
            .ConfigureAwait(false);
        var longRateDocuments = new List<ChartDocument>();
        using (var longRateArchive = ZipFile.OpenRead(longRatePackPath))
        {
            foreach (var entry in longRateArchive.Entries.Where(entry =>
                         !string.IsNullOrWhiteSpace(entry.Name) &&
                         Path.GetExtension(entry.FullName).Equals(".mc", StringComparison.OrdinalIgnoreCase)))
            {
                longRateDocuments.Add(MalodyMcSerializer.Parse(
                    await ReadZipEntryTextAsync(entry).ConfigureAwait(false),
                    entry.FullName,
                    "Avalonia Specs"));
            }
        }
        var longPathsAreOrdinaryAbsolute =
            Path.IsPathFullyQualified(longSingleChartPath) &&
            Path.IsPathFullyQualified(longRatePackPath) &&
            longSingleChartPath.Length > 260 &&
            longRatePackPath.Length > 260 &&
            !longSingleChartPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !longRatePackPath.StartsWith(@"\\?\", StringComparison.Ordinal);
        var longSingleExactBeat = longSingleDocument.Notes.Any(note =>
            note.Lane == 3 && ExactBeat(note.Beat, BeatPosition.Preserve(3, 10, 12)));
        var longRateExactBeats = longRateDocuments.All(exported => exported.Notes.Any(note =>
            note.Lane == 3 && ExactBeat(note.Beat, BeatPosition.Preserve(3, 10, 12))));
        Require(
            evidence,
            longPathsAreOrdinaryAbsolute &&
            PathsEqual(longSingleResult.DestinationPath, longSingleChartPath) &&
            PathsEqual(longRateResult.DestinationPath, longRatePackPath) &&
            longSingleExactBeat &&
            longRateResult.ChartEntryPaths.Count == 2 &&
            longRateDocuments.Count == 2 &&
            longRateExactBeats &&
            longRateTranscoder.Calls.Count == 1 &&
            Close(longRateTranscoder.Calls[0].Rate, 1.2) &&
            ScratchIsClean(longScratchRoot) &&
            TemporarySingleChartParts(longPathRoot).Length == 0 &&
            TemporaryArchives(longPathRoot).Length == 0,
            "chart-export-supports-unicode-spaces-and-ordinary-absolute-paths-over-260-characters",
            $"singleLength={longSingleChartPath.Length}; rateLength={longRatePackPath.Length}; " +
            $"singleExact={longSingleExactBeat}; rateCharts={longRateDocuments.Count}; " +
            $"rateExact={longRateExactBeats}; calls={longRateTranscoder.Calls.Count}; " +
            $"scratchClean={ScratchIsClean(longScratchRoot)}");
    }

    private static async Task VerifyGroupManagerContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var root = Path.Combine(runRoot, "group-manager");
        var library = Path.Combine(root, "library");
        var editing = Path.Combine(root, "editing");
        Directory.CreateDirectory(library);
        var workspace = new DesktopDocumentWorkspace(library, editing, "Avalonia Specs");
        await workspace.InitializeAsync().ConfigureAwait(false);
        var tab = await workspace.CreateAsync(new ChartDocument
        {
            KeyCount = 4,
            Metadata = new ChartMetadata { Title = "Group Manager Contract" },
            Notes =
            [
                new ChartNote
                {
                    Lane = 0,
                    Beat = BeatPosition.Preserve(1, 3, 6),
                    Volume = 73,
                    UnknownData = JsonNode.Parse("""{"keep":"ungrouped"}""")!.AsObject()
                },
                new ChartNote
                {
                    Lane = 1,
                    Beat = BeatPosition.Preserve(2, 2, 6),
                    Group = 2,
                    Sound = "tone.wav"
                },
                new ChartNote
                {
                    Lane = 2,
                    Beat = BeatPosition.Preserve(3, 5, 10),
                    Group = 2,
                    Sound = "tone.wav"
                },
                new ChartNote
                {
                    Lane = 3,
                    Beat = BeatPosition.Preserve(4, 7, 14),
                    Group = 3,
                    Sound = "other.wav"
                }
            ]
        }, "Group Manager Contract").ConfigureAwait(false);
        var session = tab.Session;
        var ungrouped = session.Document.Notes.Single(note => note.Group == 0);
        var groupTwo = session.Document.Notes
            .Where(note => note.Group == 2)
            .OrderBy(note => note.Beat)
            .ToArray();
        var unlocked = groupTwo[0];
        var locked = groupTwo[1];
        var hidden = session.Document.Notes.Single(note => note.Group == 3);
        session.AllowOwnerProtectedEdits = false;
        session.SetOwnerProtection([locked.Id], isProtected: true);

        tab.ActivePlacementGroup = 5;
        tab.HiddenGroups.UnionWith([3, 77]);
        session.Selection.UnionWith([ungrouped.Id, unlocked.Id, locked.Id, hidden.Id]);
        var initialSerial = session.ChangeSerial;
        var initialSnapshot = WindowsGroupManagerCommand.ReadSnapshot(tab);
        Require(
            evidence,
            initialSnapshot.Rows.Select(row => row.Group).SequenceEqual([0, 2, 3, 5]) &&
            initialSnapshot.Rows.Single(row => row.Group == 2) is
                { NoteCount: 2, DominantSound: "tone.wav" } &&
            initialSnapshot.ActivePlacementGroup == 5 &&
            initialSnapshot.VisibleEditableSelectionCount == 2 &&
            tab.HiddenGroups.SetEquals([3]) &&
            session.ChangeSerial == initialSerial && !session.IsDirty,
            "group-manager-snapshot-has-zero-current-sorted-counts-and-dominant-sound",
            $"rows={string.Join(',', initialSnapshot.Rows.Select(row => row.Group))}; " +
            $"visibleEditable={initialSnapshot.VisibleEditableSelectionCount}; serial={session.ChangeSerial}");

        var selectionBeforeCreate = session.Selection.ToHashSet();
        var createdGroup = WindowsGroupManagerCommand.CreateAndUse(tab);
        WindowsGroupManagerCommand.SetPlacementGroup(tab, ushort.MaxValue);
        var cappedGroup = WindowsGroupManagerCommand.CreateAndUse(tab);
        Require(
            evidence,
            createdGroup == 6 && cappedGroup == ushort.MaxValue &&
            tab.ActivePlacementGroup == ushort.MaxValue &&
            session.Selection.SetEquals(selectionBeforeCreate) &&
            session.ChangeSerial == initialSerial && !session.IsDirty && !session.Undo(),
            "group-manager-create-and-use-is-view-state-only-and-caps-at-65535",
            $"created={createdGroup}; capped={cappedGroup}; selected={session.Selection.Count}; " +
            $"serial={session.ChangeSerial}");

        session.Selection.Clear();
        session.Selection.UnionWith([unlocked.Id, locked.Id]);
        var visibilitySerial = session.ChangeSerial;
        var removedByHide = WindowsGroupManagerCommand.SetGroupVisible(tab, 2, visible: false);
        var visibilityNoHistory = !session.Undo();
        WindowsGroupManagerCommand.SetPlacementGroup(tab, 2);
        Require(
            evidence,
            removedByHide == 2 && session.Selection.Count == 0 &&
            !tab.HiddenGroups.Contains(2) && tab.ActivePlacementGroup == 2 &&
            session.ChangeSerial == visibilitySerial && !session.IsDirty && visibilityNoHistory,
            "group-manager-hide-removes-selection-and-set-placement-unhides-without-history",
            $"removed={removedByHide}; hidden={tab.HiddenGroups.Contains(2)}; " +
            $"serial={session.ChangeSerial}; noHistory={visibilityNoHistory}");

        tab.HiddenGroups.Add(3);
        session.Selection.UnionWith([ungrouped.Id, unlocked.Id, locked.Id, hidden.Id]);
        var exactUngroupedBeat = ungrouped.Beat;
        var exactUnlockedBeat = unlocked.Beat;
        var ungroupedUnknown = ungrouped.UnknownData?.DeepClone();
        var assignSerial = session.ChangeSerial;
        var assigned = WindowsGroupManagerCommand.AssignSelection(tab, 7);
        var assignedUngrouped = session.Document.Notes.Single(note => note.Id == ungrouped.Id);
        var assignedUnlocked = session.Document.Notes.Single(note => note.Id == unlocked.Id);
        var assignedLocked = session.Document.Notes.Single(note => note.Id == locked.Id);
        var assignedHidden = session.Document.Notes.Single(note => note.Id == hidden.Id);
        Require(
            evidence,
            assigned is { CandidateCount: 2, ChangedCount: 2, Applied: true } &&
            session.ChangeSerial == assignSerial + 1 && !tab.HiddenGroups.Contains(7) &&
            assignedUngrouped.Group == 7 && assignedUnlocked.Group == 7 &&
            assignedLocked.Group == 2 && assignedHidden.Group == 3 &&
            HasExactExpression(assignedUngrouped.Beat, exactUngroupedBeat) &&
            HasExactExpression(assignedUnlocked.Beat, exactUnlockedBeat) &&
            assignedUngrouped.Volume == 73 &&
            JsonNode.DeepEquals(assignedUngrouped.UnknownData, ungroupedUnknown),
            "group-manager-assign-filters-hidden-and-owner-lock-and-preserves-exact-opaque-state",
            $"candidate={assigned.CandidateCount}; changed={assigned.ChangedCount}; " +
            $"groups={assignedUngrouped.Group}/{assignedUnlocked.Group}/{assignedLocked.Group}/{assignedHidden.Group}");
        var assignUndo = session.Undo();
        var assignSingleUndo = !session.Undo();
        Require(
            evidence,
            assignUndo && assignSingleUndo &&
            session.Document.Notes.Single(note => note.Id == ungrouped.Id).Group == 0 &&
            session.Document.Notes.Single(note => note.Id == unlocked.Id).Group == 2 &&
            HasExactExpression(
                session.Document.Notes.Single(note => note.Id == unlocked.Id).Beat,
                exactUnlockedBeat),
            "group-manager-assign-is-one-undo",
            $"undo={assignUndo}; single={assignSingleUndo}; serial={session.ChangeSerial}");

        session.Selection.Clear();
        session.Selection.Add(unlocked.Id);
        var noChangeSerial = session.ChangeSerial;
        var noChangeAssignment = WindowsGroupManagerCommand.AssignSelection(tab, 2);
        Require(
            evidence,
            noChangeAssignment is { CandidateCount: 1, ChangedCount: 0, Applied: false } &&
            session.ChangeSerial == noChangeSerial && !session.Undo(),
            "group-manager-same-group-assignment-does-not-create-history",
            $"candidate={noChangeAssignment.CandidateCount}; changed={noChangeAssignment.ChangedCount}; " +
            $"serial={session.ChangeSerial}");

        tab.ActivePlacementGroup = 2;
        tab.HiddenGroups.Clear();
        session.CanEditNotes = false;
        var deniedSerial = session.ChangeSerial;
        var deniedFingerprint = ChartStructuralFingerprint.Compute(session.Document);
        var deniedActive = tab.ActivePlacementGroup;
        var deniedHidden = tab.HiddenGroups.ToHashSet();
        var assignDenied = ThrowsInvalidOperation(
            () => WindowsGroupManagerCommand.AssignSelection(tab, 8),
            "权限");
        var dissolveDenied = ThrowsInvalidOperation(
            () => WindowsGroupManagerCommand.Dissolve(tab, 2),
            "权限");
        Require(
            evidence,
            assignDenied && dissolveDenied && session.ChangeSerial == deniedSerial &&
            tab.ActivePlacementGroup == deniedActive && tab.HiddenGroups.SetEquals(deniedHidden) &&
            string.Equals(
                ChartStructuralFingerprint.Compute(session.Document),
                deniedFingerprint,
                StringComparison.Ordinal),
            "group-manager-permission-preflight-is-fully-nonmutating",
            $"assign={assignDenied}; dissolve={dissolveDenied}; active={tab.ActivePlacementGroup}; " +
            $"serial={session.ChangeSerial}");

        session.CanEditNotes = true;
        var dissolveSerial = session.ChangeSerial;
        var dissolved = WindowsGroupManagerCommand.Dissolve(tab, 2);
        Require(
            evidence,
            dissolved is { CandidateCount: 1, ChangedCount: 1, Applied: true } &&
            tab.ActivePlacementGroup == 0 && session.ChangeSerial == dissolveSerial + 1 &&
            session.Document.Notes.Single(note => note.Id == unlocked.Id).Group == 0 &&
            session.Document.Notes.Single(note => note.Id == locked.Id).Group == 2,
            "group-manager-dissolve-clears-only-unlocked-notes-and-resets-active-group",
            $"changed={dissolved.ChangedCount}; unlocked={session.Document.Notes.Single(note => note.Id == unlocked.Id).Group}; " +
            $"locked={session.Document.Notes.Single(note => note.Id == locked.Id).Group}");
        var dissolveUndo = session.Undo();
        var dissolveSingleUndo = !session.Undo();
        Require(
            evidence,
            dissolveUndo && dissolveSingleUndo &&
            session.Document.Notes.Single(note => note.Id == unlocked.Id).Group == 2,
            "group-manager-dissolve-is-one-undo",
            $"undo={dissolveUndo}; single={dissolveSingleUndo}");

        var tapSession = new EditorSession { SnapDivision = 12 };
        tapSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        var exactTapBeat = BeatPosition.Preserve(8, 7, 24);
        var tapId = BasicChartEditingService.PlaceTap(
            tapSession,
            1,
            exactTapBeat,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat,
            13);
        var placedTap = tapSession.Document.Notes.Single();
        var tapUndo = tapSession.Undo();
        var tapSingleUndo = !tapSession.Undo();
        Require(
            evidence,
            tapId.HasValue && placedTap.Group == 13 &&
            HasExactExpression(placedTap.Beat, exactTapBeat) &&
            tapUndo && tapSession.Document.Notes.Count == 0 && tapSingleUndo,
            "group-placement-tap-inherits-group-in-creation-mutation-and-one-undo",
            $"group={placedTap.Group}; beat={placedTap.Beat}; undo={tapUndo}; single={tapSingleUndo}");

        var holdSession = new EditorSession { SnapDivision = 12 };
        holdSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        var exactHoldHead = BeatPosition.Preserve(9, 5, 24);
        var exactHoldTail = BeatPosition.Preserve(10, 11, 24);
        var holdId = BasicChartEditingService.PlaceHold(
            holdSession,
            2,
            exactHoldHead,
            exactHoldTail,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat,
            14);
        var placedHold = holdSession.Document.Notes.Single();
        var holdUndo = holdSession.Undo();
        Require(
            evidence,
            holdId.HasValue && placedHold.Group == 14 &&
            HasExactExpression(placedHold.Beat, exactHoldHead) &&
            placedHold.EndBeat.HasValue &&
            HasExactExpression(placedHold.EndBeat.Value, exactHoldTail) &&
            holdUndo && holdSession.Document.Notes.Count == 0 && !holdSession.Undo(),
            "group-placement-ln-inherits-group-with-exact-head-tail-and-one-undo",
            $"group={placedHold.Group}; head={placedHold.Beat}; tail={placedHold.EndBeat}; undo={holdUndo}");

        tab.HiddenGroups.Clear();
        tab.HiddenGroups.Add(3);
        session.Selection.Clear();
        session.Selection.Add(hidden.Id);
        var hiddenInspector = WindowsNotePropertyCommand.ReadInspectorState(session, tab.HiddenGroups);
        var hiddenTransformCount = WindowsNoteTypeTransformCommand.CountSelectedTaps(
            session,
            tab.HiddenGroups);
        var hiddenClipboard = MixedSelectionEditingService.Copy(
            session,
            session.Selection,
            [],
            tab.HiddenGroups);
        Require(
            evidence,
            hiddenInspector.SelectedCount == 0 && hiddenTransformCount == 0 &&
            hiddenClipboard.NoteCount == 0,
            "group-hidden-notes-are-filtered-from-inspector-transform-and-copy",
            $"inspector={hiddenInspector.SelectedCount}; transform={hiddenTransformCount}; " +
            $"clipboard={hiddenClipboard.NoteCount}");

        var nudgeSession = new EditorSession { SnapDivision = 12 };
        var visibleNudgeId = Guid.NewGuid();
        var hiddenNudgeId = Guid.NewGuid();
        var lockedNudgeId = Guid.NewGuid();
        var visibleNudgeBeat = BeatPosition.Preserve(10, 3, 12);
        var hiddenNudgeBeat = BeatPosition.Preserve(11, 5, 12);
        var lockedNudgeBeat = BeatPosition.Preserve(12, 7, 12);
        nudgeSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote { Id = visibleNudgeId, Lane = 0, Beat = visibleNudgeBeat },
                new ChartNote { Id = hiddenNudgeId, Lane = 1, Beat = hiddenNudgeBeat, Group = 7 },
                new ChartNote { Id = lockedNudgeId, Lane = 2, Beat = lockedNudgeBeat }
            ]
        });
        nudgeSession.SetOwnerProtection([lockedNudgeId], isProtected: true);
        nudgeSession.AllowOwnerProtectedEdits = false;
        nudgeSession.Selection.UnionWith([visibleNudgeId, hiddenNudgeId, lockedNudgeId]);
        var nudgeTargets = WindowsGroupManagerCommand.VisibleEditableSelectionIds(
            nudgeSession,
            new HashSet<int> { 7 });
        var nudgeSerial = nudgeSession.ChangeSerial;
        var nudged = BasicChartEditingService.NudgeSelection(nudgeSession, 1, nudgeTargets);
        var nudgedVisible = nudgeSession.Document.Notes.Single(note => note.Id == visibleNudgeId);
        var nudgedHidden = nudgeSession.Document.Notes.Single(note => note.Id == hiddenNudgeId);
        var nudgedLocked = nudgeSession.Document.Notes.Single(note => note.Id == lockedNudgeId);
        var nudgeUndo = nudgeSession.Undo();
        Require(
            evidence,
            nudgeTargets.SetEquals([visibleNudgeId]) && nudged == 1 &&
            nudgeSession.ChangeSerial == nudgeSerial + 2 &&
            HasExactExpression(nudgedVisible.Beat, BeatPosition.Preserve(10, 4, 12)) &&
            HasExactExpression(nudgedHidden.Beat, hiddenNudgeBeat) &&
            HasExactExpression(nudgedLocked.Beat, lockedNudgeBeat) &&
            nudgeUndo && !nudgeSession.Undo() &&
            HasExactExpression(
                nudgeSession.Document.Notes.Single(note => note.Id == visibleNudgeId).Beat,
                visibleNudgeBeat),
            "group-keyboard-nudge-targets-only-visible-unlocked-selection",
            $"targets={nudgeTargets.Count}; moved={nudged}; undo={nudgeUndo}; " +
            $"hidden={nudgedHidden.Beat}; locked={nudgedLocked.Beat}");

        const int largeSelectionCount = 100_000;
        var largeNotes = Enumerable.Range(0, largeSelectionCount)
            .Select(index => new ChartNote
            {
                Id = Guid.NewGuid(),
                Lane = index % 4,
                Beat = BeatPosition.FromDouble(index / 8d),
                Group = index % 2
            })
            .ToList();
        var largeSelectionSession = new EditorSession();
        largeSelectionSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = largeNotes
        });
        largeSelectionSession.Selection.UnionWith(largeNotes.Select(note => note.Id));
        var emptyHiddenWatch = Stopwatch.StartNew();
        var emptyHiddenRemoved = WindowsGroupManagerCommand.RemoveHiddenSelection(
            largeSelectionSession,
            new HashSet<int>());
        emptyHiddenWatch.Stop();
        var populatedHiddenWatch = Stopwatch.StartNew();
        var populatedHiddenRemoved = WindowsGroupManagerCommand.RemoveHiddenSelection(
            largeSelectionSession,
            new HashSet<int> { 1 });
        populatedHiddenWatch.Stop();
        Require(
            evidence,
            emptyHiddenRemoved == 0 &&
            largeSelectionSession.Selection.Count == largeSelectionCount / 2 &&
            populatedHiddenRemoved == largeSelectionCount / 2 &&
            emptyHiddenWatch.Elapsed < TimeSpan.FromSeconds(2) &&
            populatedHiddenWatch.Elapsed < TimeSpan.FromSeconds(2),
            "group-hidden-selection-pruning-is-linear-at-100000-notes",
            $"empty={emptyHiddenWatch.Elapsed.TotalMilliseconds:F3}ms/{emptyHiddenRemoved}; " +
            $"populated={populatedHiddenWatch.Elapsed.TotalMilliseconds:F3}ms/{populatedHiddenRemoved}; " +
            $"remaining={largeSelectionSession.Selection.Count}");

        var roundTrip = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(session.Document));
        Require(
            evidence,
            roundTrip.Notes.Count(note => note.Group == 2) == 2 &&
            roundTrip.Notes.Single(note => note.Group == 3).Sound == "other.wav" &&
            TimelineSurface.GetGroupColorIndex(1) == 0 &&
            TimelineSurface.GetGroupColorIndex(12) == 11 &&
            TimelineSurface.GetGroupColorIndex(13) == 0 &&
            TimelineSurface.GetGroupColorIndex(ushort.MaxValue) == 2,
            "group-mc-roundtrip-and-fixed-12-color-cycle",
            $"group2={roundTrip.Notes.Count(note => note.Group == 2)}; " +
            $"colors={TimelineSurface.GetGroupColorIndex(1)}/{TimelineSurface.GetGroupColorIndex(12)}/" +
            $"{TimelineSurface.GetGroupColorIndex(13)}/{TimelineSurface.GetGroupColorIndex(ushort.MaxValue)}");

        var secondTab = await workspace.CreateAsync(
            new ChartDocument { KeyCount = 4, Metadata = new ChartMetadata { Title = "Second Group Tab" } },
            "Second Group Tab").ConfigureAwait(false);
        tab.ActivePlacementGroup = 21;
        tab.HiddenGroups.Clear();
        tab.HiddenGroups.Add(2);
        secondTab.ActivePlacementGroup = 9;
        secondTab.HiddenGroups.Add(7);
        await workspace.PersistAsync().ConfigureAwait(false);
        var manifest = await File.ReadAllTextAsync(workspace.ManifestPath).ConfigureAwait(false);
        Require(
            evidence,
            tab.ActivePlacementGroup == 21 && tab.HiddenGroups.SetEquals([2]) &&
            secondTab.ActivePlacementGroup == 9 && secondTab.HiddenGroups.SetEquals([7]) &&
            !manifest.Contains("ActivePlacementGroup", StringComparison.Ordinal) &&
            !manifest.Contains("HiddenGroups", StringComparison.Ordinal),
            "group-view-state-is-per-tab-and-absent-from-workspace-manifest",
            $"first={tab.ActivePlacementGroup}:{string.Join(',', tab.HiddenGroups)}; " +
            $"second={secondTab.ActivePlacementGroup}:{string.Join(',', secondTab.HiddenGroups)}");

        var boundsFingerprint = ChartStructuralFingerprint.Compute(session.Document);
        var boundsSerial = session.ChangeSerial;
        var lowerRejected = false;
        var upperRejected = false;
        try { WindowsGroupManagerCommand.SetPlacementGroup(tab, -1); }
        catch (ArgumentOutOfRangeException) { lowerRejected = true; }
        try { WindowsGroupManagerCommand.SetPlacementGroup(tab, ushort.MaxValue + 1); }
        catch (ArgumentOutOfRangeException) { upperRejected = true; }
        Require(
            evidence,
            lowerRejected && upperRejected && session.ChangeSerial == boundsSerial &&
            string.Equals(
                ChartStructuralFingerprint.Compute(session.Document),
                boundsFingerprint,
                StringComparison.Ordinal),
            "group-id-bounds-reject-without-document-mutation",
            $"lower={lowerRejected}; upper={upperRejected}; serial={session.ChangeSerial}");

        var probeSession = new EditorSession
        {
            SnapDivision = 12,
            AllowOwnerProtectedEdits = false
        };
        probeSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote { Lane = 0, Beat = BeatPosition.Preserve(4, 0, 1) },
                new ChartNote
                {
                    Lane = 1,
                    Beat = BeatPosition.Preserve(3, 1, 2),
                    EndBeat = BeatPosition.Preserve(5, 1, 2)
                },
                new ChartNote { Lane = 2, Beat = BeatPosition.Preserve(6, 0, 1) }
            ]
        });
        probeSession.ReplaceOwnerProtectedRegions(
        [
            new OwnerProtectedRegion(
                Guid.NewGuid(),
                BeatPosition.Preserve(4, 3, 4),
                BeatPosition.Preserve(5, 1, 4))
        ]);
        var probeSourceNotes = probeSession.Document.Notes.ToArray();
        var probeSurface = new TimelineSurface
        {
            Width = 1000,
            Height = 720,
            Session = probeSession,
            PlacementGroup = 27
        };
        probeSurface.Measure(new Size(1000, 720));
        probeSurface.Arrange(new Rect(0, 0, 1000, 720));
        probeSurface.SetCurrentBeat(5);
        var synchronousChartIssueNotifications = 0;
        probeSurface.ChartIssuesChanged += (_, _) =>
            synchronousChartIssueNotifications++;
        var placementProbe = probeSurface.GetPlacementEvidenceProbe();
        Require(
            evidence,
            placementProbe != null &&
            synchronousChartIssueNotifications == 0,
            "timeline-index-defers-chart-issue-notification-past-render-boundary",
            $"probe={placementProbe != null}; synchronous={synchronousChartIssueNotifications}");
        var targetsSafe = placementProbe != null;
        if (placementProbe is { } frozenProbe)
        {
            var tolerance = 20 / probeSurface.PixelsPerBeat;
            var safeInset = TimelineEdgeScroll.EdgeBand + 10;
            var targetPoints = new[]
            {
                frozenProbe.TapTarget.SurfacePoint,
                frozenProbe.HoldStartTarget.SurfacePoint,
                frozenProbe.HoldEndTarget.SurfacePoint
            };
            var tapClear = probeSourceNotes.All(note =>
                note.Lane != frozenProbe.TapTarget.Lane ||
                !RangesOverlap(
                    frozenProbe.TapTarget.Beat.Value - tolerance,
                    frozenProbe.TapTarget.Beat.Value + tolerance,
                    note.Beat.Value,
                    note.EndBeat?.Value ?? note.Beat.Value));
            var holdClear = probeSourceNotes.All(note =>
                note.Lane != frozenProbe.HoldStartTarget.Lane ||
                !RangesOverlap(
                    frozenProbe.HoldStartTarget.Beat.Value - tolerance,
                    frozenProbe.HoldEndTarget.Beat.Value + tolerance,
                    note.Beat.Value,
                    note.EndBeat?.Value ?? note.Beat.Value));
            var tapIdAtProbe = BasicChartEditingService.PlaceTap(
                probeSession,
                frozenProbe.TapTarget.Lane,
                frozenProbe.TapTarget.Beat,
                BasicBeatPlacementPolicy.PreserveAuthoredBeat,
                probeSurface.PlacementGroup);
            var holdIdAtProbe = BasicChartEditingService.PlaceHold(
                probeSession,
                frozenProbe.HoldStartTarget.Lane,
                frozenProbe.HoldStartTarget.Beat,
                frozenProbe.HoldEndTarget.Beat,
                BasicBeatPlacementPolicy.PreserveAuthoredBeat,
                probeSurface.PlacementGroup);
            var frozenAgain = probeSurface.GetPlacementEvidenceProbe();
            targetsSafe =
                tapClear && holdClear &&
                targetPoints.All(point =>
                    point.Y >= safeInset && point.Y <= probeSurface.Bounds.Height - safeInset) &&
                frozenProbe.HoldStartTarget.Lane == frozenProbe.HoldEndTarget.Lane &&
                frozenProbe.HoldEndTarget.Beat.CompareTo(frozenProbe.HoldStartTarget.Beat) > 0 &&
                !probeSession.IntersectsOwnerProtectedRegion(
                    frozenProbe.TapTarget.Beat,
                    frozenProbe.TapTarget.Beat) &&
                !probeSession.IntersectsOwnerProtectedRegion(
                    frozenProbe.HoldStartTarget.Beat,
                    frozenProbe.HoldEndTarget.Beat) &&
                frozenProbe.TapTarget.Beat.Denominator == probeSession.SnapDivision &&
                frozenProbe.HoldStartTarget.Beat.Denominator == probeSession.SnapDivision &&
                frozenProbe.HoldEndTarget.Beat.Denominator == probeSession.SnapDivision &&
                tapIdAtProbe.HasValue && holdIdAtProbe.HasValue &&
                probeSession.Document.Notes.Single(note => note.Id == tapIdAtProbe.Value).Group == 27 &&
                probeSession.Document.Notes.Single(note => note.Id == holdIdAtProbe.Value).Group == 27 &&
                ReferenceEquals(placementProbe, frozenAgain);
        }
        Require(
            evidence,
            targetsSafe,
            "timeline-frozen-tap-hold-evidence-probe-avoids-notes-locks-and-edge-scroll",
            placementProbe == null
                ? "probe=null"
                : $"tap={placementProbe.TapTarget.Lane}:{placementProbe.TapTarget.Beat}; " +
                  $"hold={placementProbe.HoldStartTarget.Lane}:" +
                  $"{placementProbe.HoldStartTarget.Beat}->{placementProbe.HoldEndTarget.Beat}");

        static bool RangesOverlap(
            double firstStart,
            double firstEnd,
            double secondStart,
            double secondEnd) =>
            firstStart <= secondEnd && secondStart <= firstEnd;
    }

    private static async Task VerifyProEditorContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var defaultRange = ProEditorWorkspaceGeometry.ResolveColumnRange(4, 12);
        var extendedRange = ProEditorWorkspaceGeometry.ResolveColumnRange(
            4,
            12,
            [new ChartNote { IsDraft = true, DraftColumn = 10 }]);
        var pan = ProEditorWorkspaceGeometry.ResolveTrackPan(
            viewportWidth: 1000,
            baseTrackLeft: 250,
            officialTrackWidth: 400,
            laneWidth: 100,
            requestedOffset: double.PositiveInfinity,
            enabled: true);
        Require(
            evidence,
            defaultRange == new ProEditorColumnRange(-4, 7) && defaultRange.Count == 12 &&
            extendedRange == new ProEditorColumnRange(-4, 10) && extendedRange.Count == 15 &&
            pan.Enabled && pan.Value == 0 && pan.Minimum <= pan.Value && pan.Value <= pan.Maximum,
            "pro-editor-geometry-4k-default-and-existing-draft-extension",
            $"default={defaultRange.Minimum}..{defaultRange.Maximum}; " +
            $"extended={extendedRange.Minimum}..{extendedRange.Maximum}; pan={pan.Value}");

        var previewOriginBeat = BeatPosition.Preserve(10, 4, 12);
        var preview = TimelineNoteMovePreview.Resolve(
            previewOriginBeat,
            minimumOriginBeat: previewOriginBeat.Value,
            minimumColumn: -4,
            maximumColumn: 3,
            allowedColumns: defaultRange,
            snapDivision: 12,
            requestedColumnDelta: 99,
            requestedBeatDelta: 1d / 12);
        var previewApplied = TimelineNoteMovePreview.Apply(
            new TimelineNoteMoveOrigin(-4, previewOriginBeat, BeatPosition.Preserve(11, 10, 12)),
            preview);
        Require(
            evidence,
            preview.LaneDelta == 4 && preview.BeatMove == new RelativeBeatMove(1, 12) &&
            previewApplied.Lane == 0 &&
            HasExactExpression(previewApplied.Beat, BeatPosition.Preserve(10, 5, 12)) &&
            previewApplied.EndBeat.HasValue &&
            HasExactExpression(previewApplied.EndBeat.Value, BeatPosition.Preserve(11, 11, 12)),
            "pro-editor-preview-clamps-columns-and-preserves-rational-relative-move",
            $"delta={preview.LaneDelta}; move={preview.BeatMove.Steps}/{preview.BeatMove.Division}; " +
            $"beat={previewApplied.Beat}; end={previewApplied.EndBeat}");

        var indexedOfficial = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(2, 4, 12)
        };
        var indexedLeftDraft = new ChartNote
        {
            IsDraft = true,
            DraftColumn = -2,
            Beat = BeatPosition.Preserve(2, 4, 12)
        };
        var indexedRightDraft = new ChartNote
        {
            IsDraft = true,
            DraftColumn = 6,
            Beat = BeatPosition.Preserve(3, 4, 12),
            EndBeat = BeatPosition.Preserve(4, 4, 12)
        };
        var draftIndex = new TimelineNoteIndex();
        draftIndex.Rebuild([indexedOfficial, indexedLeftDraft, indexedRightDraft]);
        var scratch = new List<ChartNote>();
        var officialQueryCount = draftIndex.Query(1, 5, scratch);
        var officialQueryIds = scratch.Select(note => note.Id).ToHashSet();
        var allQueryCount = draftIndex.Query(1, 5, scratch, includeDrafts: true);
        var allQueryIds = scratch.Select(note => note.Id).ToHashSet();
        var defaultDraftHit = draftIndex.HitTest(-2, indexedLeftDraft.Beat.Value, .0001, scratch);
        var includedDraftHit = draftIndex.HitTest(
            -2,
            indexedLeftDraft.Beat.Value,
            .0001,
            scratch,
            includeDrafts: true);
        var defaultTailCount = draftIndex.QueryHoldTails(3, 5, scratch);
        var includedTailCount = draftIndex.QueryHoldTails(3, 5, scratch, includeDrafts: true);
        Require(
            evidence,
            draftIndex.Count == 3 && draftIndex.OfficialCount == 1 &&
            officialQueryCount == 1 && officialQueryIds.SetEquals([indexedOfficial.Id]) &&
            allQueryCount == 3 &&
            allQueryIds.SetEquals([indexedOfficial.Id, indexedLeftDraft.Id, indexedRightDraft.Id]) &&
            defaultDraftHit == null && includedDraftHit?.Id == indexedLeftDraft.Id &&
            defaultTailCount == 0 && includedTailCount == 1 && scratch.Single().Id == indexedRightDraft.Id,
            "pro-editor-index-default-excludes-drafts-and-opt-in-query-hit-includes-them",
            $"count={draftIndex.Count}/{draftIndex.OfficialCount}; query={officialQueryCount}/{allQueryCount}; " +
            $"tail={defaultTailCount}/{includedTailCount}; hit={includedDraftHit?.Id}");

        var placementSession = new EditorSession { SnapDivision = 12 };
        placementSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        var leftTapBeat = BeatPosition.Preserve(8, 7, 24);
        var rightHoldHead = BeatPosition.Preserve(9, 5, 24);
        var rightHoldTail = BeatPosition.Preserve(10, 11, 24);
        var leftTapId = WindowsProEditorCommand.PlaceTap(
            placementSession,
            -4,
            leftTapBeat,
            defaultRange,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat,
            13);
        var duplicateLeftHold = WindowsProEditorCommand.PlaceHold(
            placementSession,
            -4,
            leftTapBeat,
            BeatPosition.Preserve(9, 1, 24),
            defaultRange,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat,
            99);
        var rightHoldId = WindowsProEditorCommand.PlaceHold(
            placementSession,
            7,
            rightHoldHead,
            rightHoldTail,
            defaultRange,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat,
            14);
        var serialBeforeDuplicateRight = placementSession.ChangeSerial;
        var duplicateRightTap = WindowsProEditorCommand.PlaceTap(
            placementSession,
            7,
            rightHoldHead,
            defaultRange,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat,
            100);
        var leftTap = placementSession.Document.Notes.Single(note => note.Id == leftTapId);
        var rightHold = placementSession.Document.Notes.Single(note => note.Id == rightHoldId);
        Require(
            evidence,
            leftTapId.HasValue && rightHoldId.HasValue &&
            duplicateLeftHold == null && duplicateRightTap == null &&
            placementSession.Document.Notes.Count == 2 &&
            placementSession.ChangeSerial == serialBeforeDuplicateRight &&
            leftTap.IsDraft && leftTap.DraftColumn == -4 && leftTap.Group == 13 && !leftTap.IsHold &&
            HasExactExpression(leftTap.Beat, leftTapBeat) &&
            rightHold.IsDraft && rightHold.DraftColumn == 7 && rightHold.Group == 14 && rightHold.IsHold &&
            HasExactExpression(rightHold.Beat, rightHoldHead) &&
            rightHold.EndBeat.HasValue && HasExactExpression(rightHold.EndBeat.Value, rightHoldTail),
            "pro-editor-left-right-draft-placement-preserves-exact-group-and-rejects-duplicate-head",
            $"notes={placementSession.Document.Notes.Count}; serial={placementSession.ChangeSerial}; " +
            $"left={leftTap.DraftColumn}:{leftTap.Beat}/g{leftTap.Group}; " +
            $"right={rightHold.DraftColumn}:{rightHold.Beat}->{rightHold.EndBeat}/g{rightHold.Group}");

        var crossingId = Guid.NewGuid();
        var crossingBeat = BeatPosition.Preserve(20, 4, 12);
        var crossingEnd = BeatPosition.Preserve(21, 10, 12);
        var crossingUnknown = JsonNode.Parse("""{"opaque":{"value":17}}""")!.AsObject();
        var crossingSession = new EditorSession { SnapDivision = 12 };
        crossingSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote
                {
                    Id = crossingId,
                    Lane = 0,
                    Beat = crossingBeat,
                    EndBeat = crossingEnd,
                    Sound = "cross.wav",
                    Volume = 73,
                    Group = 7,
                    UnknownData = crossingUnknown.DeepClone().AsObject(),
                    OnlineCreatorUserId = 42
                }
            ]
        });
        crossingSession.Selection.Add(crossingId);
        var crossingSerial = crossingSession.ChangeSerial;
        var crossingMoved = WindowsProEditorCommand.MoveSelection(
            crossingSession,
            -1,
            new RelativeBeatMove(1, 12),
            defaultRange,
            expectedChangeSerial: crossingSerial);
        var crossedDraft = crossingSession.Document.Notes.Single();
        var crossingUndo = crossingSession.Undo();
        var crossingSingleUndo = !crossingSession.Undo();
        var crossedOfficialAgain = crossingSession.Document.Notes.Single();
        var crossingRedo = crossingSession.Redo();
        var crossingSingleRedo = !crossingSession.Redo();
        var crossedDraftAgain = crossingSession.Document.Notes.Single();
        Require(
            evidence,
            crossingMoved == 1 && crossingSession.ChangeSerial == crossingSerial + 3 &&
            crossedDraft.Id == crossingId && crossedDraft.IsDraft && crossedDraft.DraftColumn == -1 &&
            crossedDraft.Lane == 0 && crossedDraft.Sound == "cross.wav" &&
            crossedDraft.Volume == 73 && crossedDraft.Group == 7 &&
            crossedDraft.OnlineCreatorUserId == 42 &&
            JsonNode.DeepEquals(crossedDraft.UnknownData, crossingUnknown) &&
            HasExactExpression(crossedDraft.Beat, BeatPosition.Preserve(20, 5, 12)) &&
            crossedDraft.EndBeat.HasValue &&
            HasExactExpression(crossedDraft.EndBeat.Value, BeatPosition.Preserve(21, 11, 12)) &&
            crossingUndo && crossingSingleUndo && !crossedOfficialAgain.IsDraft &&
            crossedOfficialAgain.Lane == 0 && HasExactExpression(crossedOfficialAgain.Beat, crossingBeat) &&
            crossedOfficialAgain.EndBeat.HasValue &&
            HasExactExpression(crossedOfficialAgain.EndBeat.Value, crossingEnd) &&
            crossingRedo && crossingSingleRedo && crossedDraftAgain.IsDraft &&
            crossedDraftAgain.DraftColumn == -1 && crossedDraftAgain.Id == crossingId &&
            crossedDraftAgain.Sound == "cross.wav" && crossedDraftAgain.Volume == 73 &&
            crossedDraftAgain.Group == 7 && crossedDraftAgain.OnlineCreatorUserId == 42 &&
            JsonNode.DeepEquals(crossedDraftAgain.UnknownData, crossingUnknown),
            "pro-editor-official-draft-crossing-is-one-mutation-undo-redo-and-preserves-opaque-state",
            $"moved={crossingMoved}; serial={crossingSession.ChangeSerial}; undo={crossingUndo}/{crossingSingleUndo}; " +
            $"redo={crossingRedo}/{crossingSingleRedo}; column={crossedDraftAgain.DraftColumn}");

        var returnSession = new EditorSession { SnapDivision = 12 };
        returnSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [crossedDraftAgain.Clone()]
        });
        returnSession.Selection.Add(crossingId);
        var returnSerial = returnSession.ChangeSerial;
        var returnedCount = WindowsProEditorCommand.MoveSelection(
            returnSession,
            1,
            new RelativeBeatMove(-1, 12),
            defaultRange,
            expectedChangeSerial: returnSerial);
        var returnedOfficial = returnSession.Document.Notes.Single();
        var returnUndo = returnSession.Undo();
        var returnSingleUndo = !returnSession.Undo();
        var restoredDraft = returnSession.Document.Notes.Single();
        var returnRedo = returnSession.Redo();
        var returnSingleRedo = !returnSession.Redo();
        var returnedOfficialAgain = returnSession.Document.Notes.Single();
        Require(
            evidence,
            returnedCount == 1 && returnSession.ChangeSerial == returnSerial + 3 &&
            !returnedOfficial.IsDraft && returnedOfficial.Lane == 0 && returnedOfficial.DraftColumn == 0 &&
            returnedOfficial.Id == crossingId && returnedOfficial.Sound == "cross.wav" &&
            returnedOfficial.Volume == 73 && returnedOfficial.Group == 7 &&
            returnedOfficial.OnlineCreatorUserId == 42 &&
            JsonNode.DeepEquals(returnedOfficial.UnknownData, crossingUnknown) &&
            HasExactExpression(returnedOfficial.Beat, crossingBeat) &&
            returnedOfficial.EndBeat.HasValue &&
            HasExactExpression(returnedOfficial.EndBeat.Value, crossingEnd) &&
            returnUndo && returnSingleUndo && restoredDraft.IsDraft && restoredDraft.DraftColumn == -1 &&
            HasExactExpression(restoredDraft.Beat, BeatPosition.Preserve(20, 5, 12)) &&
            restoredDraft.EndBeat.HasValue &&
            HasExactExpression(restoredDraft.EndBeat.Value, BeatPosition.Preserve(21, 11, 12)) &&
            returnRedo && returnSingleRedo && !returnedOfficialAgain.IsDraft &&
            returnedOfficialAgain.Lane == 0 && returnedOfficialAgain.Id == crossingId &&
            returnedOfficialAgain.Sound == "cross.wav" && returnedOfficialAgain.Volume == 73 &&
            returnedOfficialAgain.Group == 7 && returnedOfficialAgain.OnlineCreatorUserId == 42 &&
            JsonNode.DeepEquals(returnedOfficialAgain.UnknownData, crossingUnknown),
            "pro-editor-draft-official-crossing-is-one-mutation-undo-redo-and-preserves-opaque-state",
            $"moved={returnedCount}; serial={returnSession.ChangeSerial}; undo={returnUndo}/{returnSingleUndo}; " +
            $"redo={returnRedo}/{returnSingleRedo}; lane={returnedOfficialAgain.Lane}");

        var multiOfficialId = Guid.NewGuid();
        var multiDraftId = Guid.NewGuid();
        var multiSession = new EditorSession { SnapDivision = 12 };
        multiSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote
                {
                    Id = multiOfficialId,
                    Lane = 3,
                    Beat = BeatPosition.Preserve(30, 2, 12)
                },
                new ChartNote
                {
                    Id = multiDraftId,
                    IsDraft = true,
                    DraftColumn = 7,
                    Beat = BeatPosition.Preserve(31, 5, 12)
                }
            ]
        });
        multiSession.Selection.UnionWith([multiOfficialId, multiDraftId]);
        var multiSerial = multiSession.ChangeSerial;
        var multiMoved = WindowsProEditorCommand.MoveSelection(
            multiSession,
            1,
            new RelativeBeatMove(-1, 12),
            extendedRange,
            expectedChangeSerial: multiSerial);
        var movedOfficial = multiSession.Document.Notes.Single(note => note.Id == multiOfficialId);
        var movedDraft = multiSession.Document.Notes.Single(note => note.Id == multiDraftId);
        var multiUndo = multiSession.Undo();
        Require(
            evidence,
            multiMoved == 2 && multiSession.ChangeSerial == multiSerial + 2 &&
            movedOfficial.IsDraft && movedOfficial.DraftColumn == 4 &&
            movedDraft.IsDraft && movedDraft.DraftColumn == 8 &&
            HasExactExpression(movedOfficial.Beat, BeatPosition.Preserve(30, 1, 12)) &&
            HasExactExpression(movedDraft.Beat, BeatPosition.Preserve(31, 4, 12)) &&
            multiUndo && !multiSession.Undo() &&
            !multiSession.Document.Notes.Single(note => note.Id == multiOfficialId).IsDraft &&
            multiSession.Document.Notes.Single(note => note.Id == multiOfficialId).Lane == 3 &&
            multiSession.Document.Notes.Single(note => note.Id == multiDraftId).DraftColumn == 7,
            "pro-editor-multiselect-crosses-boundary-with-one-relative-command",
            $"moved={multiMoved}; serial={multiSession.ChangeSerial}; " +
            $"columns={movedOfficial.DraftColumn}/{movedDraft.DraftColumn}; undo={multiUndo}");

        var hiddenSession = CreateGuardSession(group: 9);
        var hiddenId = hiddenSession.Document.Notes.Single().Id;
        hiddenSession.Selection.Add(hiddenId);
        var hiddenFingerprint = ChartStructuralFingerprint.Compute(hiddenSession.Document);
        var hiddenSerial = hiddenSession.ChangeSerial;
        var hiddenMoved = WindowsProEditorCommand.MoveSelection(
            hiddenSession,
            1,
            new RelativeBeatMove(1, 12),
            defaultRange,
            hiddenGroups: new HashSet<int> { 9 },
            expectedChangeSerial: hiddenSerial);
        var readOnlySession = CreateGuardSession();
        readOnlySession.IsReadOnly = true;
        var permissionSession = CreateGuardSession();
        permissionSession.CanEditNotes = false;
        var readOnlyRejected = RejectMove(readOnlySession, "只读", defaultRange);
        var permissionRejected = RejectMove(permissionSession, "权限", defaultRange);
        Require(
            evidence,
            hiddenMoved == 0 && hiddenSession.ChangeSerial == hiddenSerial &&
            string.Equals(ChartStructuralFingerprint.Compute(hiddenSession.Document), hiddenFingerprint, StringComparison.Ordinal) &&
            !hiddenSession.Undo() && readOnlyRejected && permissionRejected &&
            IsUnchangedWithoutHistory(readOnlySession) && IsUnchangedWithoutHistory(permissionSession),
            "pro-editor-hidden-readonly-and-permission-guards-are-nonmutating",
            $"hidden={hiddenMoved}/{hiddenSession.ChangeSerial}; readonly={readOnlyRejected}; " +
            $"permission={permissionRejected}");

        var normalModeTransitionSession = CreateGuardSession();
        var normalModeTransitionId = normalModeTransitionSession.Document.Notes.Single().Id;
        var normalModeSurface = new TimelineSurface { Session = normalModeTransitionSession };
        var normalModeSelectionEvents = 0;
        normalModeSurface.SelectionChanged += (_, _) => normalModeSelectionEvents++;
        normalModeTransitionSession.Mutate(document =>
            ProEditorWorkspaceGeometry.ApplyColumn(
                document.Notes.Single(note => note.Id == normalModeTransitionId),
                column: 4,
                document.KeyCount));
        var selectionWasPrunedBySessionChange = normalModeTransitionSession.Selection.Count == 0;
        var selectionWasPrunedDirectly = normalModeSurface.PruneNonInteractiveNoteSelection();
        var normalModeSelectionDeliveryValid = selectionWasPrunedBySessionChange
            ? !selectionWasPrunedDirectly && normalModeSelectionEvents == 1
            : selectionWasPrunedDirectly && normalModeSelectionEvents == 0;
        var convertedNormalModeDraft = normalModeTransitionSession.Document.Notes.Single();
        var normalModeUndo = normalModeTransitionSession.Undo();
        var normalModeSingleUndo = !normalModeTransitionSession.Undo();
        var restoredNormalModeOfficial = normalModeTransitionSession.Document.Notes.Single();
        var draftClipboardSource = new EditorSession();
        var draftClipboardId = Guid.NewGuid();
        draftClipboardSource.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote
                {
                    Id = draftClipboardId,
                    IsDraft = true,
                    DraftColumn = 4,
                    Beat = BeatPosition.Preserve(2, 1, 8)
                }
            ]
        });
        draftClipboardSource.Selection.Add(draftClipboardId);
        var draftClipboard = MixedSelectionEditingService.Copy(
            draftClipboardSource,
            draftClipboardSource.Selection,
            []);
        var normalPasteSession = new EditorSession();
        normalPasteSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        var normalPasteSurface = new TimelineSurface { Session = normalPasteSession };
        var normalPaste = MixedSelectionEditingService.Paste(
            normalPasteSession,
            draftClipboard,
            BeatPosition.Preserve(3, 0, 1));
        var selectedAfterPostMutationPaste = normalPasteSession.Selection.Count;
        var postMutationPastePruned = normalPasteSurface.PruneNonInteractiveNoteSelection();
        var pastedDraft = normalPasteSession.Document.Notes.Single();
        var normalPasteUndo = normalPasteSession.Undo();
        var normalPasteSingleUndo = !normalPasteSession.Undo();
        Require(
            evidence,
            convertedNormalModeDraft.IsDraft && convertedNormalModeDraft.DraftColumn == 4 &&
            normalModeTransitionSession.Selection.Count == 0 && normalModeSelectionDeliveryValid &&
            normalModeUndo && normalModeSingleUndo &&
            !restoredNormalModeOfficial.IsDraft && restoredNormalModeOfficial.Lane == 0 &&
            normalModeTransitionSession.ChangeSerial == 2 &&
            draftClipboard.NoteCount == 1 && normalPaste.NoteCount == 1 &&
            selectedAfterPostMutationPaste == 1 && postMutationPastePruned &&
            normalPasteSession.Selection.Count == 0 &&
            pastedDraft.IsDraft && pastedDraft.DraftColumn == 4 &&
            normalPasteUndo && normalPasteSingleUndo && normalPasteSession.Document.Notes.Count == 0,
            "pro-editor-normal-mode-prunes-newly-draft-and-post-mutation-paste-selection-without-extra-history",
            $"draft={convertedNormalModeDraft.IsDraft}:{convertedNormalModeDraft.DraftColumn}; " +
            $"selection={normalModeTransitionSession.Selection.Count}; " +
            $"delivery={selectionWasPrunedBySessionChange}/{selectionWasPrunedDirectly}; " +
            $"events={normalModeSelectionEvents}; serial={normalModeTransitionSession.ChangeSerial}; " +
            $"undo={normalModeUndo}/{normalModeSingleUndo}; " +
            $"pasteSelection={selectedAfterPostMutationPaste}->" +
            $"{normalPasteSession.Selection.Count}; pasteUndo={normalPasteUndo}/{normalPasteSingleUndo}");

        var sourceOwnerSession = CreateGuardSession();
        sourceOwnerSession.AllowOwnerProtectedEdits = false;
        sourceOwnerSession.SetOwnerProtection(
            [sourceOwnerSession.Document.Notes.Single().Id],
            isProtected: true);
        var targetOwnerSession = CreateGuardSession();
        targetOwnerSession.AllowOwnerProtectedEdits = false;
        targetOwnerSession.AddOwnerProtectedRegion(
            BeatPosition.Preserve(5, 2, 12),
            BeatPosition.Preserve(5, 3, 12));
        var staleSession = CreateGuardSession();
        var sourceOwnerRejected = RejectMove(sourceOwnerSession, "房主锁定", defaultRange);
        var targetOwnerRejected = RejectMove(targetOwnerSession, "目标位置", defaultRange);
        var staleRejected = RejectMove(
            staleSession,
            "最新状态",
            defaultRange,
            expectedSerial: staleSession.ChangeSerial + 1);
        Require(
            evidence,
            sourceOwnerRejected && targetOwnerRejected && staleRejected &&
            IsUnchangedWithoutHistory(sourceOwnerSession) &&
            IsUnchangedWithoutHistory(targetOwnerSession) &&
            IsUnchangedWithoutHistory(staleSession) &&
            TimelineNoteMovePreview.IsInterrupted(17, 18),
            "pro-editor-source-target-owner-and-stale-guards-are-nonmutating",
            $"source={sourceOwnerRejected}; target={targetOwnerRejected}; stale={staleRejected}");

        var fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            RequiredProEditorFixtureRelativePath));
        var fixtureExists = File.Exists(fixturePath);
        var fixtureHash = fixtureExists
            ? await ComputeSha256Async(fixturePath).ConfigureAwait(false)
            : string.Empty;
        Require(
            evidence,
            fixtureExists && string.Equals(
                fixtureHash,
                RequiredProEditorFixtureSha256,
                StringComparison.OrdinalIgnoreCase),
            "pro-editor-malody-noteref-fixture-present-with-trusted-hash",
            $"path={fixturePath}; sha256={fixtureHash}");

        var fixtureDocument = MalodyMcSerializer.Load(fixturePath);
        var fixtureOfficial = fixtureDocument.Notes.Where(note => !note.IsDraft).ToArray();
        var fixtureDraft = fixtureDocument.Notes.Where(note => note.IsDraft).ToArray();
        Require(
            evidence,
            fixtureDocument.KeyCount == 4 && fixtureOfficial.Length == 2 && fixtureDraft.Length == 3 &&
            fixtureDraft.Select(note => note.DraftColumn).Order().SequenceEqual([8, 9, 10]) &&
            fixtureDocument.Notes.Count(note =>
                HasExactExpression(note.Beat, BeatPosition.Preserve(0, 4, 12))) == 3 &&
            fixtureDraft.Single(note => note.DraftColumn == 9) is var lowerDraft &&
            HasExactExpression(lowerDraft.Beat, BeatPosition.Preserve(0, 8, 12)),
            "pro-editor-malody-noteref-parses-two-official-three-draft-and-exact-expression",
            $"key={fixtureDocument.KeyCount}; official={fixtureOfficial.Length}; draft={fixtureDraft.Length}; " +
            $"columns={string.Join(',', fixtureDraft.Select(note => note.DraftColumn).Order())}");

        var serializedMc = MalodyMcSerializer.Serialize(fixtureDocument);
        var serializedRoot = JsonNode.Parse(serializedMc)!.AsObject();
        var serializedRoundTrip = MalodyMcSerializer.Parse(serializedMc);
        Require(
            evidence,
            serializedRoot["noteref"] is JsonArray serializedNoteref && serializedNoteref.Count == 3 &&
            !serializedRoot.ContainsKey("_luma_draft_notes") &&
            serializedRoundTrip.Notes.Count(note => !note.IsDraft) == 2 &&
            serializedRoundTrip.Notes.Count(note => note.IsDraft) == 3 &&
            serializedRoundTrip.Notes.Where(note => note.IsDraft)
                .Select(note => note.DraftColumn).Order().SequenceEqual([8, 9, 10]) &&
            serializedRoundTrip.Notes.Count(note =>
                HasExactExpression(note.Beat, BeatPosition.Preserve(0, 4, 12))) == 3,
            "pro-editor-malody-serialize-uses-noteref-only-and-roundtrips-drafts",
            $"noteref={(serializedRoot["noteref"] as JsonArray)?.Count}; " +
            $"legacy={serializedRoot.ContainsKey("_luma_draft_notes")}; " +
            $"notes={serializedRoundTrip.Notes.Count}");

        var proRoot = Path.Combine(runRoot, "pro-editor");
        Directory.CreateDirectory(proRoot);
        var savedMcPath = Path.Combine(proRoot, "noteref-roundtrip.mc");
        MalodyMcSerializer.Save(fixtureDocument.Clone(), savedMcPath);
        var reopenedMc = MalodyMcSerializer.Load(savedMcPath);
        Require(
            evidence,
            reopenedMc.Notes.Count(note => !note.IsDraft) == 2 &&
            reopenedMc.Notes.Count(note => note.IsDraft) == 3 &&
            reopenedMc.Notes.Where(note => note.IsDraft)
                .Select(note => note.DraftColumn).Order().SequenceEqual([8, 9, 10]) &&
            reopenedMc.Notes.Count(note =>
                HasExactExpression(note.Beat, BeatPosition.Preserve(0, 4, 12))) == 3,
            "pro-editor-malody-save-and-fresh-reopen-preserves-noteref",
            $"path={savedMcPath}; official={reopenedMc.Notes.Count(note => !note.IsDraft)}; " +
            $"draft={reopenedMc.Notes.Count(note => note.IsDraft)}");

        var deterministicSession = new EditorSession { SnapDivision = 8 };
        deterministicSession.ReplaceDocument(fixtureDocument.Clone());
        var deterministicRange = ProEditorWorkspaceGeometry.ResolveColumnRange(
            deterministicSession.Document.KeyCount,
            ProEditorWorkspaceGeometry.DefaultTotalColumns,
            deterministicSession.Document.Notes);
        var deterministicHead = BeatPosition.Preserve(0, 1, 8);
        var deterministicTail = BeatPosition.Preserve(0, 5, 8);
        var deterministicTapId = WindowsProEditorCommand.PlaceTap(
            deterministicSession,
            workspaceColumn: 4,
            deterministicHead,
            deterministicRange,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        var deterministicMoved = WindowsProEditorCommand.MoveSelection(
            deterministicSession,
            columnDelta: -1,
            new RelativeBeatMove(0, 8),
            deterministicRange,
            expectedChangeSerial: deterministicSession.ChangeSerial);
        var deterministicHoldId = WindowsProEditorCommand.PlaceHold(
            deterministicSession,
            workspaceColumn: 4,
            deterministicHead,
            deterministicTail,
            deterministicRange,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        var deterministicPath = Path.Combine(proRoot, "deterministic-edit-chain.mc");
        MalodyMcSerializer.Save(deterministicSession.Document, deterministicPath);
        var deterministicBytes = await File.ReadAllBytesAsync(deterministicPath).ConfigureAwait(false);
        var deterministicHash = Convert.ToHexString(SHA256.HashData(deterministicBytes));
        var deterministicReopened = MalodyMcSerializer.Load(deterministicPath);
        const string expectedDeterministicHash =
            "6995CDE6268B0BF071718286363C45112E50829D9C063AC12A82D01EED50E68B";
        Require(
            evidence,
            deterministicTapId.HasValue && deterministicHoldId.HasValue && deterministicMoved == 1 &&
            deterministicSession.ChangeSerial == 3 && deterministicBytes.Length == 591 &&
            string.Equals(deterministicHash, expectedDeterministicHash, StringComparison.Ordinal) &&
            deterministicReopened.Notes.Count == 7 &&
            deterministicReopened.Notes.Count(note => !note.IsDraft) == 3 &&
            deterministicReopened.Notes.Count(note => note.IsDraft) == 4 &&
            deterministicReopened.Notes.Count(note => note.IsHold) == 1 &&
            deterministicReopened.Notes.Any(note =>
                note.Id != Guid.Empty && !note.IsDraft && note.Lane == 3 &&
                HasExactExpression(note.Beat, deterministicHead) && !note.IsHold) &&
            deterministicReopened.Notes.Any(note =>
                note.Id != Guid.Empty && note.IsDraft && note.DraftColumn == 4 &&
                HasExactExpression(note.Beat, deterministicHead) &&
                note.EndBeat.HasValue && HasExactExpression(note.EndBeat.Value, deterministicTail)) &&
            string.Equals(
                ChartStructuralFingerprint.Compute(deterministicReopened),
                expectedDeterministicHash,
                StringComparison.Ordinal),
            "pro-editor-deterministic-fixture-edit-chain-has-trusted-compact-mc-hash",
            $"serial={deterministicSession.ChangeSerial}; moved={deterministicMoved}; " +
            $"bytes={deterministicBytes.Length}; sha256={deterministicHash}; " +
            $"notes={deterministicReopened.Notes.Count}; " +
            $"official={deterministicReopened.Notes.Count(note => !note.IsDraft)}; " +
            $"draft={deterministicReopened.Notes.Count(note => note.IsDraft)}; " +
            $"ln={deterministicReopened.Notes.Count(note => note.IsHold)}");

        const string localDataVariable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        const string workspaceVariable = "LUMACHART_AVALONIA_WORKSPACE_ROOT";
        var previousLocalDataRoot = Environment.GetEnvironmentVariable(localDataVariable);
        var previousWorkspaceRoot = Environment.GetEnvironmentVariable(workspaceVariable);
        var settingsLocalRoot = Path.Combine(proRoot, "settings-local");
        var settingsWorkspaceRoot = Path.Combine(proRoot, "settings-workspace");
        var settingsRoundTrip = false;
        var settingsDetail = string.Empty;
        try
        {
            Environment.SetEnvironmentVariable(localDataVariable, settingsLocalRoot);
            Environment.SetEnvironmentVariable(workspaceVariable, settingsWorkspaceRoot);
            Directory.CreateDirectory(settingsLocalRoot);
            Directory.CreateDirectory(settingsWorkspaceRoot);
            AvaloniaAppPaths.SaveWorkspacePreferences(
                settingsWorkspaceRoot,
                DesktopLibrarySort.TitleAscending);
            var defaultColumns = AvaloniaAppPaths.LoadProGridColumns();
            AvaloniaAppPaths.SaveProGridColumns(65);
            var maximumColumns = AvaloniaAppPaths.LoadProGridColumns();
            AvaloniaAppPaths.SaveProGridColumns(0);
            var minimumColumns = AvaloniaAppPaths.LoadProGridColumns();
            AvaloniaAppPaths.SaveProGridColumns(12);
            using var settingsDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(AvaloniaAppPaths.WorkspaceSettingsPath)
                    .ConfigureAwait(false));
            var settingsRoot = settingsDocument.RootElement;
            var persistedRoot = settingsRoot.GetProperty("WorkspaceRoot").GetString();
            var persistedSort = settingsRoot.GetProperty("LibrarySort").GetString();
            var persistedColumns = settingsRoot.GetProperty("ProGridColumns").GetInt32();
            var orphanTemporaryFiles = Directory
                .EnumerateFiles(settingsLocalRoot, ".workspace.json.*.tmp")
                .Count();
            settingsRoundTrip =
                defaultColumns == 12 && maximumColumns == 64 && minimumColumns == 1 &&
                persistedColumns == 12 &&
                string.Equals(
                    Path.GetFullPath(persistedRoot ?? string.Empty),
                    Path.GetFullPath(settingsWorkspaceRoot),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    persistedSort,
                    DesktopLibrarySort.TitleAscending.ToString(),
                    StringComparison.Ordinal) &&
                orphanTemporaryFiles == 0;
            settingsDetail =
                $"columns={defaultColumns}/{maximumColumns}/{minimumColumns}/{persistedColumns}; " +
                $"root={persistedRoot}; sort={persistedSort}; temp={orphanTemporaryFiles}";
        }
        finally
        {
            Environment.SetEnvironmentVariable(localDataVariable, previousLocalDataRoot);
            Environment.SetEnvironmentVariable(workspaceVariable, previousWorkspaceRoot);
        }
        Require(
            evidence,
            settingsRoundTrip,
            "pro-editor-grid-column-settings-atomically-roundtrip-and-clamp-without-losing-workspace",
            settingsDetail);

        var osuDocument = new ChartDocument
        {
            KeyCount = 4,
            Metadata = new ChartMetadata
            {
                Title = "ProEditor osu export",
                Artist = "LumaChart Specs",
                Creator = "Avalonia Specs",
                Version = "Draft exclusion",
                Bpm = 120
            },
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
            Notes =
            [
                new ChartNote { Lane = 1, Beat = BeatPosition.Preserve(1, 0, 1) },
                new ChartNote { IsDraft = true, DraftColumn = -2, Beat = BeatPosition.Preserve(1, 4, 12) },
                new ChartNote { IsDraft = true, DraftColumn = 8, Beat = BeatPosition.Preserve(2, 4, 12) }
            ]
        };
        var osuPath = Path.Combine(proRoot, "draft-exclusion.osu");
        OsuManiaSerializer.Save(osuDocument, osuPath);
        var reopenedOsu = OsuManiaSerializer.Load(osuPath);
        Require(
            evidence,
            reopenedOsu.Notes.Count == 1 && reopenedOsu.Notes.All(note => !note.IsDraft) &&
            reopenedOsu.Notes.Single().Lane == 1,
            "pro-editor-osu-serialize-excludes-all-draft-notes",
            $"source={osuDocument.Notes.Count}; reopened={reopenedOsu.Notes.Count}; " +
            $"draft={reopenedOsu.Notes.Count(note => note.IsDraft)}");

        const int mixedCount = 100_000;
        var mixedNotes = Enumerable.Range(0, mixedCount).Select(index => new ChartNote
        {
            Lane = index % 4,
            IsDraft = (index & 1) != 0,
            DraftColumn = 6 + index % 6,
            Beat = BeatPosition.FromDouble(index / 16d)
        }).ToArray();
        var mixedIndex = new TimelineNoteIndex();
        mixedIndex.Rebuild(mixedNotes);
        var mixedScratch = new List<ChartNote>();
        var boundedOfficialCount = mixedIndex.Query(1000, 1001, mixedScratch);
        var boundedOfficialOnly = mixedScratch.All(note => !note.IsDraft);
        var boundedAllCount = mixedIndex.Query(1000, 1001, mixedScratch, includeDrafts: true);
        var draftTarget = mixedNotes[16001];
        var defaultLargeDraftHit = mixedIndex.HitTest(
            draftTarget.DraftColumn,
            draftTarget.Beat.Value,
            .0001,
            mixedScratch);
        var includedLargeDraftHit = mixedIndex.HitTest(
            draftTarget.DraftColumn,
            draftTarget.Beat.Value,
            .0001,
            mixedScratch,
            includeDrafts: true);
        Require(
            evidence,
            mixedIndex.Count == mixedCount && mixedIndex.OfficialCount == mixedCount / 2 &&
            mixedIndex.DraftColumnExtent == new ProEditorColumnRange(7, 11) &&
            boundedOfficialCount is > 0 and <= 10 && boundedOfficialOnly &&
            boundedAllCount is > 0 and <= 20 && boundedAllCount > boundedOfficialCount &&
            defaultLargeDraftHit == null && includedLargeDraftHit?.Id == draftTarget.Id,
            "pro-editor-100000-mixed-index-keeps-default-and-opt-in-queries-bounded",
            $"indexed={mixedIndex.Count}/{mixedIndex.OfficialCount}; " +
            $"query={boundedOfficialCount}/{boundedAllCount}; hit={includedLargeDraftHit?.Id}");

        var mixedSurfaceSession = new EditorSession();
        mixedSurfaceSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = mixedNotes.Select(note => note.Clone()).ToList()
        });
        var mixedSurface = new TimelineSurface { Session = mixedSurfaceSession };
        mixedSurface.Measure(new Size(1000, 720));
        mixedSurface.Arrange(new Rect(0, 0, 1000, 720));
        var serialBeforeViewToggle = mixedSurfaceSession.ChangeSerial;
        mixedSurface.SetProEditor(enabled: true);
        var expandedSurfaceRange = mixedSurface.GetWorkspaceColumnRange();
        var initialPanState = mixedSurface.GetTrackPanState();
        var requestedPan = initialPanState.Minimum +
                           (initialPanState.Maximum - initialPanState.Minimum) * .25;
        mixedSurface.SetTrackPanOffset(requestedPan);
        var retainedPanState = mixedSurface.GetTrackPanState();
        mixedSurface.SetProEditor(enabled: false);
        var normalSurfaceRange = mixedSurface.GetWorkspaceColumnRange();
        var normalTrackBounds = mixedSurface.GetActiveTrackBounds();
        mixedSurface.SetProEditor(enabled: true);
        var restoredPanState = mixedSurface.GetTrackPanState();
        var restoredTrackBounds = mixedSurface.GetActiveTrackBounds();
        var rangeLoopStarted = Stopwatch.GetTimestamp();
        var cachedRangesStable = true;
        for (var index = 0; index < 4096; index++)
            cachedRangesStable &= mixedSurface.GetWorkspaceColumnRange() == expandedSurfaceRange;
        var rangeLoopMilliseconds = Stopwatch.GetElapsedTime(rangeLoopStarted).TotalMilliseconds;
        var selectedOfficialId = mixedSurfaceSession.Document.Notes.First(note => !note.IsDraft).Id;
        var selectedDraftId = mixedSurfaceSession.Document.Notes.First(note => note.IsDraft).Id;
        mixedSurfaceSession.Selection.UnionWith([selectedOfficialId, selectedDraftId]);
        mixedSurface.SetProEditor(enabled: false);
        var pruneLoopStarted = Stopwatch.GetTimestamp();
        var selectionPruningStable = true;
        for (var index = 0; index < 4096; index++)
            selectionPruningStable &= !mixedSurface.PruneNonInteractiveNoteSelection();
        var pruneLoopMilliseconds = Stopwatch.GetElapsedTime(pruneLoopStarted).TotalMilliseconds;
        Require(
            evidence,
            expandedSurfaceRange == new ProEditorColumnRange(-4, 11) &&
            normalSurfaceRange == new ProEditorColumnRange(0, 3) &&
            initialPanState.Enabled && retainedPanState.Enabled && restoredPanState.Enabled &&
            Math.Abs(retainedPanState.Value) > .001 &&
            Math.Abs(restoredPanState.Value - retainedPanState.Value) < .001 &&
            Math.Abs(
                restoredTrackBounds.Left - normalTrackBounds.Left - retainedPanState.Value) < .001 &&
            cachedRangesStable && rangeLoopMilliseconds < 1000 &&
            selectionPruningStable && pruneLoopMilliseconds < 1000 &&
            mixedSurfaceSession.Selection.SetEquals([selectedOfficialId]) &&
            mixedSurfaceSession.ChangeSerial == serialBeforeViewToggle &&
            !mixedSurfaceSession.IsDirty && !mixedSurfaceSession.Undo() &&
            mixedSurfaceSession.Document.Notes.Count == mixedCount &&
            mixedSurfaceSession.Document.Notes.Count(note => note.IsDraft) == mixedCount / 2,
            "pro-editor-100000-surface-workspace-range-is-cached-and-view-toggle-is-nonmutating",
            $"range={expandedSurfaceRange.Minimum}..{expandedSurfaceRange.Maximum}; " +
            $"normal={normalSurfaceRange.Minimum}..{normalSurfaceRange.Maximum}; " +
            $"pan={retainedPanState.Value:F3}->{restoredPanState.Value:F3}; " +
            $"rangeLoops=4096/{rangeLoopMilliseconds:F3}ms; " +
            $"pruneLoops=4096/{pruneLoopMilliseconds:F3}ms; serial={mixedSurfaceSession.ChangeSerial}");

        static EditorSession CreateGuardSession(int group = 0)
        {
            var session = new EditorSession { SnapDivision = 12 };
            var noteId = Guid.NewGuid();
            session.ReplaceDocument(new ChartDocument
            {
                KeyCount = 4,
                Notes =
                [
                    new ChartNote
                    {
                        Id = noteId,
                        Lane = 0,
                        Beat = BeatPosition.Preserve(5, 1, 12),
                        Group = group,
                        UnknownData = JsonNode.Parse("""{"guard":true}""")!.AsObject()
                    }
                ]
            });
            session.Selection.Add(noteId);
            return session;
        }

        static bool RejectMove(
            EditorSession session,
            string expectedText,
            ProEditorColumnRange range,
            long? expectedSerial = null) => ThrowsInvalidOperation(
            () => WindowsProEditorCommand.MoveSelection(
                session,
                0,
                new RelativeBeatMove(1, 12),
                range,
                expectedChangeSerial: expectedSerial ?? session.ChangeSerial),
            expectedText);

        static bool IsUnchangedWithoutHistory(EditorSession session)
        {
            var note = session.Document.Notes.Single();
            return !note.IsDraft && note.Lane == 0 &&
                   HasExactExpression(note.Beat, BeatPosition.Preserve(5, 1, 12)) &&
                   session.ChangeSerial == 0 && !session.IsDirty && !session.Undo();
        }
    }

    private static bool ThrowsInvalidOperation(Action action, string expectedText)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message.Contains(expectedText, StringComparison.Ordinal);
        }
    }

    private static void VerifyTimelineTimingContracts(SliceEvidence evidence)
    {
        var firstTiming = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(5, 1, 8),
            Bpm = 180
        };
        var secondTiming = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(5, 1, 8),
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1.25,
            EffectValueText = "1.25"
        };
        var timingIndex = new TimelineTimingIndex();
        timingIndex.Rebuild([firstTiming, secondTiming]);
        var timingCandidates = new List<TimingEvent>();
        _ = timingIndex.Query(5, 6, timingCandidates);
        var labels = new List<TimelineTimingLabel>();
        TimelineTimingLayout.BuildLabels(
            timingCandidates,
            500,
            _ => 200,
            _ => 80,
            labels);
        var deliberatelyOverlapped = new[]
        {
            labels[0],
            labels[1] with { Bounds = labels[0].Bounds }
        };
        var topmostHit = TimelineTimingLayout.HitTestTopmost(
            deliberatelyOverlapped,
            labels[0].Bounds.Center);
        var categoriesPassed = TimelineTimingLayout.Category(new TimingEvent { Bpm = 120 }) ==
                               TimelineTimingCategory.Bpm &&
                               TimelineTimingLayout.Category(new TimingEvent { Effect = TimingEffectKind.Scroll }) ==
                               TimelineTimingCategory.Scroll &&
                               TimelineTimingLayout.Category(new TimingEvent { Effect = TimingEffectKind.Jump }) ==
                               TimelineTimingCategory.Jump &&
                               TimelineTimingLayout.Category(new TimingEvent { Effect = TimingEffectKind.HS }) ==
                               TimelineTimingCategory.HS &&
                               TimelineTimingLayout.Category(new TimingEvent { Effect = TimingEffectKind.Sign }) ==
                               TimelineTimingCategory.Sign &&
                               TimelineTimingLayout.Category(new TimingEvent { Effect = TimingEffectKind.Custom }) ==
                               TimelineTimingCategory.Custom;
        var selectedDeleteBatch = TimelineTimingLayout.ResolveDeleteBatch(
            new HashSet<Guid> { firstTiming.Id, secondTiming.Id },
            secondTiming.Id);
        var unselectedTimingId = Guid.NewGuid();
        var singleDeleteBatch = TimelineTimingLayout.ResolveDeleteBatch(
            new HashSet<Guid> { firstTiming.Id, secondTiming.Id },
            unselectedTimingId);
        var deleteBatchPassed = selectedDeleteBatch.Count == 2 &&
                                selectedDeleteBatch.Contains(firstTiming.Id) &&
                                selectedDeleteBatch.Contains(secondTiming.Id) &&
                                singleDeleteBatch.Count == 1 &&
                                singleDeleteBatch[0] == unselectedTimingId;
        Require(
            evidence,
            labels.Count == 2 &&
            labels.All(label => Math.Abs(label.Bounds.Height - 22) < .000001) &&
            labels[1].Bounds.Right < labels[0].Bounds.Left &&
            topmostHit?.Timing.Id == secondTiming.Id &&
            categoriesPassed && deleteBatchPassed,
            "timeline-timing-same-beat-stack-and-topmost-hit",
            $"labels={labels.Count}; firstX={labels[0].Bounds.X:F1}; secondX={labels[1].Bounds.X:F1}; " +
            $"topmost={topmostHit?.Timing.Id}; categories={categoriesPassed}; deleteBatch={deleteBatchPassed}");

        var mixedNote = new ChartNote
        {
            Id = Guid.NewGuid(),
            Lane = 0,
            Beat = BeatPosition.Preserve(5, 0, 1),
            EndBeat = BeatPosition.Preserve(6, 0, 1)
        };
        var mixedNoteIndex = new TimelineNoteIndex();
        mixedNoteIndex.Rebuild([mixedNote]);
        var mixedMarquee = new Rect(10, 40, 140, 20);
        var mixedCandidates = new List<ChartNote>();
        var noteSelection = new HashSet<Guid>();
        _ = TimelineMarqueeSelection.AddIntersectingNotes(
            mixedNoteIndex,
            4.9,
            5.1,
            mixedMarquee,
            _ => new Rect(110, 45, 30, 20),
            noteSelection,
            mixedCandidates);
        var mixedTiming = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(5, 0, 1),
            Effect = TimingEffectKind.Jump,
            EffectValueText = "1"
        };
        labels.Clear();
        TimelineTimingLayout.BuildLabels([mixedTiming], 100, _ => 50, _ => 40, labels);
        var timingSelection = new HashSet<Guid>();
        _ = TimelineTimingLayout.AddMarqueeHits(labels, mixedMarquee, 100, timingSelection);
        Require(
            evidence,
            noteSelection.SetEquals([mixedNote.Id]) && timingSelection.SetEquals([mixedTiming.Id]),
            "timeline-marquee-selects-note-ln-and-timing-together",
            $"notes={noteSelection.Count}; timings={timingSelection.Count}");

        var manyTimings = new List<TimingEvent>(BenchmarkNoteCount);
        for (var timingNumber = 0; timingNumber < BenchmarkNoteCount; timingNumber++)
        {
            manyTimings.Add(new TimingEvent
            {
                Id = Guid.NewGuid(),
                Beat = BeatPosition.Preserve(timingNumber / 16, timingNumber % 16, 16),
                Bpm = 120
            });
        }
        var largeTimingIndex = new TimelineTimingIndex();
        largeTimingIndex.Rebuild(manyTimings);
        timingCandidates.Clear();
        var timingCandidateCount = largeTimingIndex.Query(999.5, 1000.5, timingCandidates);
        Require(
            evidence,
            largeTimingIndex.Count == BenchmarkNoteCount && timingCandidateCount is > 0 and < 256,
            "timeline-timing-100000-range-is-bounded",
            $"indexed={largeTimingIndex.Count}; candidates={timingCandidateCount}");

        var moveAnchor = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(2, 0, 4),
            Bpm = 150
        };
        var moveCompanion = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(1, 0, 4),
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1.5,
            EffectValueText = "1.5"
        };
        var lockedCompanion = new TimingEvent
        {
            Id = Guid.NewGuid(),
            Beat = BeatPosition.Preserve(4, 0, 4),
            Effect = TimingEffectKind.Jump,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var timingMoveSession = new EditorSession { SnapDivision = 4 };
        timingMoveSession.ReplaceDocument(new ChartDocument
        {
            Timing = [moveAnchor.Clone(), moveCompanion.Clone(), lockedCompanion.Clone()]
        });
        timingMoveSession.ReplaceOwnerProtectedRegions(
        [
            new OwnerProtectedRegion(
                Guid.NewGuid(),
                lockedCompanion.Beat,
                BeatPosition.Preserve(4, 1, 4))
        ]);
        timingMoveSession.AllowOwnerProtectedEdits = false;
        var previewSerial = timingMoveSession.ChangeSerial;
        var requestedAnchor = TimelineTimingMovePreview.RequestedAnchorBeat(
            moveAnchor.Beat,
            pointerStartBeat: 10,
            pointerCurrentBeat: 11.26);
        var timingPreview = TimelineTimingMovePreview.Resolve(
            moveAnchor.Beat,
            moveCompanion.Beat,
            requestedAnchor,
            timingMoveSession.SnapDivision);
        var expectedCompanionBeat = TimelineTimingMovePreview.Apply(
            moveCompanion.Beat,
            timingPreview.BeatDelta,
            timingMoveSession.SnapDivision);
        var previewStayedPure = timingMoveSession.ChangeSerial == previewSerial &&
                                timingMoveSession.Document.Timing.Single(timing =>
                                    timing.Id == moveAnchor.Id).Beat == moveAnchor.Beat &&
                                timingMoveSession.Document.Timing.Single(timing =>
                                    timing.Id == moveCompanion.Id).Beat == moveCompanion.Beat;
        var timingMoveResult = TimingBatchMoveService.MoveTo(
            timingMoveSession,
            [moveAnchor.Id, moveCompanion.Id, lockedCompanion.Id],
            moveAnchor.Id,
            requestedAnchor);
        var movedAnchor = timingMoveSession.Document.Timing.Single(timing => timing.Id == moveAnchor.Id);
        var movedCompanion = timingMoveSession.Document.Timing.Single(timing => timing.Id == moveCompanion.Id);
        var retainedLockedCompanion = timingMoveSession.Document.Timing.Single(timing =>
            timing.Id == lockedCompanion.Id);
        var timingMovePassed = previewStayedPure &&
                               timingMoveResult.MovedCount == 2 &&
                               timingMoveResult.MovableTimingIds.ToHashSet().SetEquals(
                                   [moveAnchor.Id, moveCompanion.Id]) &&
                               timingMoveResult.FilteredTimingIds.SequenceEqual([lockedCompanion.Id]) &&
                               movedAnchor.Beat == timingPreview.AnchorBeat &&
                               movedCompanion.Beat == expectedCompanionBeat &&
                               retainedLockedCompanion.Beat == lockedCompanion.Beat &&
                               movedAnchor.Beat.Denominator == timingMoveSession.SnapDivision &&
                               movedCompanion.Beat.Denominator == timingMoveSession.SnapDivision &&
                               timingMoveSession.ChangeSerial == previewSerial + 1 &&
                               timingMoveSession.Undo() &&
                               timingMoveSession.Document.Timing.Single(timing =>
                                   timing.Id == moveAnchor.Id).Beat == moveAnchor.Beat &&
                               timingMoveSession.Document.Timing.Single(timing =>
                                   timing.Id == moveCompanion.Id).Beat == moveCompanion.Beat &&
                               !timingMoveSession.Undo();
        Require(
            evidence,
            timingMovePassed,
            "timeline-timing-pointer-preview-is-pure-and-matches-single-release-command",
            $"preview={timingPreview.AnchorBeat}; companion={expectedCompanionBeat}; " +
            $"moved={timingMoveResult.MovedCount}; filtered={timingMoveResult.FilteredTimingIds.Count}");

        var guardedTimingId = Guid.NewGuid();
        var guardedTimingSession = new EditorSession { SnapDivision = 4 };
        guardedTimingSession.ReplaceDocument(new ChartDocument
        {
            Timing =
            [
                new TimingEvent
                {
                    Id = guardedTimingId,
                    Beat = BeatPosition.Zero,
                    Effect = TimingEffectKind.Sign,
                    EffectValue = 1,
                    EffectValueText = "1"
                }
            ]
        });
        guardedTimingSession.ReplaceOwnerProtectedRegions(
        [
            new OwnerProtectedRegion(
                Guid.NewGuid(),
                BeatPosition.Preserve(1, 1, 4),
                BeatPosition.Preserve(1, 2, 4))
        ]);
        guardedTimingSession.AllowOwnerProtectedEdits = false;
        var guardedSerial = guardedTimingSession.ChangeSerial;
        var targetLockRejected = false;
        try
        {
            _ = TimingBatchMoveService.MoveTo(
                guardedTimingSession,
                [guardedTimingId],
                guardedTimingId,
                BeatPosition.Preserve(1, 1, 4));
        }
        catch (InvalidOperationException exception)
        {
            targetLockRejected = exception.Message.Contains("目标位置", StringComparison.Ordinal);
        }
        var nonNegativePreview = TimelineTimingMovePreview.Resolve(
            BeatPosition.Preserve(0, 1, 4),
            BeatPosition.Zero,
            BeatPosition.FromDouble(-10),
            4);
        var guardPassed = targetLockRejected &&
                          guardedTimingSession.ChangeSerial == guardedSerial &&
                          guardedTimingSession.Document.Timing.Single(timing =>
                              timing.Id == guardedTimingId).Beat == BeatPosition.Zero &&
                          !guardedTimingSession.Undo() &&
                          nonNegativePreview.AnchorBeat == BeatPosition.Preserve(0, 1, 4) &&
                          TimelineTimingMovePreview.Apply(
                              BeatPosition.Zero,
                              nonNegativePreview.BeatDelta,
                              4) == BeatPosition.Zero &&
                          TimelineTimingMovePreview.IsInterrupted(7, 8) &&
                          !TimelineTimingMovePreview.IsInterrupted(7, 7);
        Require(
            evidence,
            guardPassed,
            "timeline-timing-move-guards-target-lock-zero-and-remote-serial",
            $"targetRejected={targetLockRejected}; serial={guardedTimingSession.ChangeSerial}; " +
            $"nonNegative={nonNegativePreview.AnchorBeat}; stale={TimelineTimingMovePreview.IsInterrupted(7, 8)}");

        var noteMoveOrigins = new[]
        {
            new TimelineNoteMoveOrigin(
                1,
                BeatPosition.Preserve(0, 1, 4),
                BeatPosition.Preserve(0, 2, 4)),
            new TimelineNoteMoveOrigin(
                2,
                BeatPosition.Preserve(1, 0, 4),
                null)
        };
        var lastLegalNotePreview = TimelineNoteMovePreview.Resolve(
            noteMoveOrigins[0].Beat,
            noteMoveOrigins.Min(origin => origin.Beat.Value),
            minimumLane: 1,
            maximumLane: 2,
            keyCount: 4,
            snapDivision: 4,
            requestedLaneDelta: 8,
            requestedBeatDelta: .26);
        var noteMoveGuardSession = new EditorSession();
        noteMoveGuardSession.ReplaceOwnerProtectedRegions(
        [
            new OwnerProtectedRegion(
                Guid.NewGuid(),
                BeatPosition.Preserve(1, 2, 4),
                BeatPosition.Preserve(1, 3, 4))
        ]);
        var lockedCandidate = TimelineNoteMovePreview.Resolve(
            noteMoveOrigins[0].Beat,
            noteMoveOrigins.Min(origin => origin.Beat.Value),
            minimumLane: 1,
            maximumLane: 2,
            keyCount: 4,
            snapDivision: 4,
            requestedLaneDelta: -8,
            requestedBeatDelta: .51);
        var retainedPreview = TimelineNoteMovePreview.KeepLastLegal(
            lastLegalNotePreview,
            lockedCandidate,
            noteMoveOrigins,
            noteMoveGuardSession.IntersectsOwnerProtectedRegion,
            out var lockedCandidateAccepted);
        var movedFirstOrigin = TimelineNoteMovePreview.Apply(noteMoveOrigins[0], retainedPreview);
        var noteMovePreviewPassed = lastLegalNotePreview.LaneDelta == 1 &&
                                    lastLegalNotePreview.BeatMove == new RelativeBeatMove(1, 4) &&
                                    !lockedCandidateAccepted &&
                                    retainedPreview == lastLegalNotePreview &&
                                    movedFirstOrigin.Lane == 2 &&
                                    movedFirstOrigin.Beat == BeatPosition.Preserve(0, 2, 4) &&
                                    movedFirstOrigin.EndBeat == BeatPosition.Preserve(0, 3, 4) &&
                                    TimelineNoteMovePreview.IsInterrupted(11, 12) &&
                                    !TimelineNoteMovePreview.IsInterrupted(11, 11);
        Require(
            evidence,
            noteMovePreviewPassed,
            "timeline-note-move-keeps-last-legal-rational-preview-and-rejects-stale-serial",
            $"legalLaneDelta={lastLegalNotePreview.LaneDelta}; legalBeatMove={lastLegalNotePreview.BeatMove}; " +
            $"lockedAccepted={lockedCandidateAccepted}; destination={movedFirstOrigin.Beat}");

        var edgeScrollTop = TimelineEdgeScroll.Resolve(10, -42, 1000, .1);
        var edgeScrollBottom = TimelineEdgeScroll.Resolve(10, 1042, 1000, .1);
        var edgeScrollCenter = TimelineEdgeScroll.Resolve(10, 500, 1000, .016);
        var edgeScrollFloor = TimelineEdgeScroll.Resolve(.1, 1200, 1000, .05);
        Require(
            evidence,
            Math.Abs(edgeScrollTop.Intensity - 2) < .000001 &&
            Math.Abs(edgeScrollTop.ElapsedSeconds - .05) < .000001 &&
            Math.Abs(edgeScrollTop.NextBeat - 10.8) < .000001 &&
            Math.Abs(edgeScrollBottom.Intensity + 2) < .000001 &&
            Math.Abs(edgeScrollBottom.NextBeat - 9.2) < .000001 &&
            Math.Abs(edgeScrollCenter.Intensity) < .000001 &&
            Math.Abs(edgeScrollCenter.NextBeat - 10) < .000001 &&
            Math.Abs(edgeScrollFloor.NextBeat) < .000001,
            "timeline-edge-scroll-matches-wpf-band-speed-direction-and-zero-floor",
            $"top={edgeScrollTop.Intensity}/{edgeScrollTop.NextBeat}; " +
            $"bottom={edgeScrollBottom.Intensity}/{edgeScrollBottom.NextBeat}; floor={edgeScrollFloor.NextBeat}");

        var region = new OwnerProtectedRegion(
            Guid.NewGuid(),
            BeatPosition.Preserve(2, 0, 1),
            BeatPosition.Preserve(2, 1, 1920));
        var regionVisuals = new List<TimelineOwnerRegionVisual>();
        TimelineOwnerRegionLayout.BuildVisible(
            [region],
            1,
            3,
            100,
            500,
            beat => 300 - beat * 48,
            regionVisuals);
        var regionSession = new EditorSession();
        regionSession.ReplaceOwnerProtectedRegions([region]);
        var insideProtected = regionSession.IsBeatOwnerProtected(region.StartBeat);
        var outsideProtected = regionSession.IsBeatOwnerProtected(region.EndBeat);
        Require(
            evidence,
            regionVisuals.Count == 1 &&
            Math.Abs(regionVisuals[0].Bounds.Height - TimelineOwnerRegionLayout.MinimumVisibleHeight) < .000001 &&
            Math.Abs(regionVisuals[0].Bounds.Left - 101) < .000001 &&
            Math.Abs(regionVisuals[0].Bounds.Right - 499) < .000001 &&
            insideProtected && !outsideProtected,
            "timeline-owner-region-minimum-visual-does-not-expand-logic",
            $"visualHeight={regionVisuals[0].Bounds.Height:F3}; inside={insideProtected}; endExclusive={!outsideProtected}");
    }

    private static void VerifyMixedEditorCommandContracts(SliceEvidence evidence)
    {
        var inputPolicyPassed =
            WindowsAvaloniaEditorInputPolicy.PreserveNotesForTimingListSelection(KeyModifiers.Shift) &&
            WindowsAvaloniaEditorInputPolicy.PreserveNotesForTimingListSelection(
                KeyModifiers.Shift | KeyModifiers.Control) &&
            !WindowsAvaloniaEditorInputPolicy.PreserveNotesForTimingListSelection(KeyModifiers.None) &&
            !WindowsAvaloniaEditorInputPolicy.PreserveNotesForTimingListSelection(KeyModifiers.Control) &&
            WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(Key.C, KeyModifiers.Control) &&
            WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(Key.V, KeyModifiers.Control) &&
            WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(Key.Delete, KeyModifiers.None) &&
            WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(Key.Z, KeyModifiers.Control) &&
            !WindowsAvaloniaEditorInputPolicy.IsTextEditingShortcut(Key.S, KeyModifiers.Control) &&
            WindowsAvaloniaEditorInputPolicy.IsTextEditingControl(new TextBox()) &&
            WindowsAvaloniaEditorInputPolicy.IsTextEditingControl(new ComboBox()) &&
            !WindowsAvaloniaEditorInputPolicy.IsTextEditingControl(new Button());
        var timingOnlyToolSession = new EditorSession
        {
            CanEditNotes = false,
            CanEditTiming = true
        };
        var viewerAccess = WindowsOnlineRoomPermissionPolicy.Resolve(
            false,
            "viewer",
            canEditMetadata: true,
            canEditTiming: true,
            canEditNotes: true);
        var editorAccess = WindowsOnlineRoomPermissionPolicy.Resolve(
            false,
            "editor",
            canEditMetadata: true,
            canEditTiming: false,
            canEditNotes: true);
        var ownerAccess = WindowsOnlineRoomPermissionPolicy.Resolve(
            true,
            "viewer",
            canEditMetadata: false,
            canEditTiming: false,
            canEditNotes: false);
        inputPolicyPassed = inputPolicyPassed &&
                            viewerAccess.IsReadOnly &&
                            !viewerAccess.CanEditMetadata &&
                            !viewerAccess.CanEditTiming &&
                            !viewerAccess.CanEditNotes &&
                            !editorAccess.IsReadOnly &&
                            editorAccess.CanEditMetadata &&
                            !editorAccess.CanEditTiming &&
                            editorAccess.CanEditNotes &&
                            !ownerAccess.IsReadOnly &&
                            ownerAccess.CanEditMetadata &&
                            ownerAccess.CanEditTiming &&
                            ownerAccess.CanEditNotes &&
                            WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(
                                timingOnlyToolSession,
                                TimelineTool.Select) &&
                            WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(
                                timingOnlyToolSession,
                                TimelineTool.Erase) &&
                            !WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(
                                timingOnlyToolSession,
                                TimelineTool.Tap) &&
                            !WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(
                                timingOnlyToolSession,
                                TimelineTool.Hold);
        var primaryTimingA = Guid.NewGuid();
        var primaryTimingB = Guid.NewGuid();
        var timingSelectionSurface = new TimelineSurface();
        timingSelectionSurface.SetSelectedTimings(
            [primaryTimingA, primaryTimingB],
            primaryTimingB);
        timingSelectionSurface.SetSelectedTimings([primaryTimingA, primaryTimingB]);
        inputPolicyPassed = inputPolicyPassed &&
                            timingSelectionSurface.PrimarySelectedTimingId == primaryTimingB;
        timingSelectionSurface.SetSelectedTimings([primaryTimingA]);
        inputPolicyPassed = inputPolicyPassed &&
                            timingSelectionSurface.PrimarySelectedTimingId == primaryTimingA;
        Require(
            evidence,
            inputPolicyPassed,
            "timing-list-shift-mixed-selection-and-text-editor-shortcut-policy",
            $"inputPolicy={inputPolicyPassed}");

        var noteId = Guid.NewGuid();
        var bpmId = Guid.NewGuid();
        var effectId = Guid.NewGuid();
        var source = new EditorSession { SnapDivision = 8 };
        source.ReplaceDocument(new ChartDocument
        {
            Notes =
            [
                new ChartNote
                {
                    Id = noteId,
                    Lane = 1,
                    Beat = BeatPosition.Preserve(1, 0, 1)
                }
            ],
            Timing =
            [
                new TimingEvent { Id = bpmId, Beat = BeatPosition.Zero, Bpm = 120 },
                new TimingEvent
                {
                    Id = effectId,
                    Beat = BeatPosition.Preserve(1, 1, 2),
                    Effect = TimingEffectKind.Scroll,
                    EffectValue = 1.25
                }
            ]
        });
        source.Selection.Add(noteId);
        var clipboard = MixedSelectionEditingService.Copy(
            source,
            source.Selection,
            [bpmId, effectId]);
        var destination = new EditorSession { SnapDivision = 8 };
        destination.ReplaceDocument(new ChartDocument());
        var pasted = MixedSelectionEditingService.Paste(
            destination,
            clipboard,
            BeatPosition.Preserve(4, 0, 1));
        var pastedEffect = destination.Document.Timing.Single(timing => timing.Effect.HasValue);
        var timingOnlyDestination = new EditorSession { SnapDivision = 8 };
        timingOnlyDestination.ReplaceDocument(new ChartDocument());
        var timingOnlyPaste = MixedSelectionEditingService.Paste(
            timingOnlyDestination,
            clipboard.EffectsOnly(),
            BeatPosition.Preserve(6, 0, 1));
        var mixedPastePassed = clipboard.NoteCount == 1 && clipboard.EffectCount == 1 &&
                               pasted.NoteCount == 1 && pasted.EffectCount == 1 &&
                               destination.Selection.SetEquals(pasted.NoteIds) &&
                               pastedEffect.Id == pasted.TimingIds.Single() &&
                               destination.Document.Notes.Single().Beat == BeatPosition.Preserve(4, 0, 1) &&
                               pastedEffect.Beat == BeatPosition.Preserve(4, 1, 2) &&
                               destination.ChangeSerial == 1 && destination.Undo() && !destination.Undo() &&
                               timingOnlyPaste.NoteCount == 0 && timingOnlyPaste.EffectCount == 1 &&
                               timingOnlyDestination.Document.Notes.Count == 0 &&
                               timingOnlyDestination.Document.Timing.Single(timing => timing.Effect.HasValue).Beat ==
                               BeatPosition.Preserve(6, 0, 1);
        Require(
            evidence,
            mixedPastePassed,
            "mixed-note-effect-copy-paste-aligns-to-redline-and-selects-results",
            $"clipboard={clipboard.NoteCount}+{clipboard.EffectCount}; " +
            $"pasted={pasted.NoteCount}+{pasted.EffectCount}; effectBeat={pastedEffect.Beat}");

        var batchSession = new EditorSession();
        batchSession.ReplaceDocument(new ChartDocument
        {
            Timing =
            [
                new TimingEvent { Id = bpmId, Beat = BeatPosition.Zero, Bpm = 120 },
                new TimingEvent
                {
                    Id = effectId,
                    Beat = BeatPosition.Preserve(1, 0, 1),
                    Effect = TimingEffectKind.Jump
                }
            ]
        });
        var batchDeleted = TimingBatchDeleteService.DeleteMany(batchSession, [bpmId, effectId]);
        var batchDeletePassed = batchDeleted.DeletedTimingIds.ToHashSet().SetEquals([bpmId, effectId]) &&
                                batchSession.Document.Timing.Count == 1 &&
                                batchSession.Document.Timing[0].Bpm == 120 &&
                                batchSession.Document.Timing.All(timing =>
                                    timing.Id != bpmId && timing.Id != effectId) &&
                                batchSession.ChangeSerial == 1 && batchSession.Undo() &&
                                batchSession.Document.Timing.Count == 2 && !batchSession.Undo();
        Require(
            evidence,
            batchDeletePassed,
            "timing-list-and-timeline-batch-delete-includes-bpm-effect-one-undo",
            $"deleted={batchDeleted.DeletedCount}; oneUndo={batchDeletePassed}");
    }

    private static void VerifyWindowsDisplayScaleContracts(
        SliceEvidence evidence,
        string runRoot)
    {
        static bool Equal(double left, double right) => Math.Abs(left - right) < 1e-12;

        var normalizationPassed =
            Equal(DisplayScalePolicy.NormalizeUiScale(.79), .8) &&
            Equal(DisplayScalePolicy.NormalizeUiScale(.825), .8) &&
            Equal(DisplayScalePolicy.NormalizeUiScale(.875), .9) &&
            Equal(DisplayScalePolicy.NormalizeUiScale(1.024), 1) &&
            Equal(DisplayScalePolicy.NormalizeUiScale(1.026), 1.05) &&
            Equal(DisplayScalePolicy.NormalizeUiScale(2.01), 2) &&
            Equal(DisplayScalePolicy.NormalizeUiScale(double.NaN), 1) &&
            Equal(DisplayScalePolicy.NormalizeUiScale(double.PositiveInfinity), 1) &&
            Equal(DisplayScalePolicy.NormalizeUiScale(double.NegativeInfinity), 1) &&
            Equal(DisplayScalePolicy.NormalizeFontScale(.79), .8) &&
            Equal(DisplayScalePolicy.NormalizeFontScale(.825), .8) &&
            Equal(DisplayScalePolicy.NormalizeFontScale(.875), .9) &&
            Equal(DisplayScalePolicy.NormalizeFontScale(1.61), 1.6) &&
            Equal(DisplayScalePolicy.NormalizeFontScale(double.NaN), 1) &&
            Equal(DisplayScalePolicy.NormalizeFontScale(double.PositiveInfinity), 1);
        Require(
            evidence,
            normalizationPassed,
            "windows-display-scale-policy-normalizes-wpf-five-percent-boundaries-and-invalid-values",
            $"ui=.79->{DisplayScalePolicy.NormalizeUiScale(.79):R}; " +
            $".825->{DisplayScalePolicy.NormalizeUiScale(.825):R}; " +
            $".875->{DisplayScalePolicy.NormalizeUiScale(.875):R}; " +
            $"font1.61->{DisplayScalePolicy.NormalizeFontScale(1.61):R}");

        var stepButtonsPassed =
            Equal(
                WindowsDisplayScaleStepPolicy.ResolvePercent(100, 80, 200, -1),
                95) &&
            Equal(
                WindowsDisplayScaleStepPolicy.ResolvePercent(100, 80, 200, 1),
                105) &&
            Equal(
                WindowsDisplayScaleStepPolicy.ResolvePercent(82, 80, 200, 1),
                85) &&
            Equal(
                WindowsDisplayScaleStepPolicy.ResolvePercent(83, 80, 200, -1),
                80) &&
            Equal(
                WindowsDisplayScaleStepPolicy.ResolvePercent(80, 80, 200, -1),
                80) &&
            Equal(
                WindowsDisplayScaleStepPolicy.ResolvePercent(200, 80, 200, 1),
                200);
        Require(
            evidence,
            stepButtonsPassed,
            "windows-display-scale-step-buttons-use-bounded-five-percent-ticks",
            $"step={WindowsDisplayScaleStepPolicy.StepPercent:R}; " +
            $"100-={WindowsDisplayScaleStepPolicy.ResolvePercent(100, 80, 200, -1):R}; " +
            $"100+={WindowsDisplayScaleStepPolicy.ResolvePercent(100, 80, 200, 1):R}");

        var recommendationPassed =
            Equal(DisplayScalePolicy.RecommendUiScale(1366, 768), 1) &&
            Equal(DisplayScalePolicy.RecommendUiScale(1920, 1080), 1) &&
            Equal(DisplayScalePolicy.RecommendUiScale(2560, 1440), 1.35) &&
            Equal(DisplayScalePolicy.RecommendUiScale(3840, 2160), 2) &&
            Equal(DisplayScalePolicy.RecommendUiScale(3840 / 1.25, 2160 / 1.25), 1.6) &&
            Equal(DisplayScalePolicy.RecommendUiScale(3840 / 1.5, 2160 / 1.5), 1.35) &&
            Equal(DisplayScalePolicy.RecommendUiScale(3840 / 2d, 2160 / 2d), 1) &&
            Equal(DisplayScalePolicy.RecommendUiScale(1968, 1107), 1.05) &&
            Equal(DisplayScalePolicy.RecommendUiScale(double.NaN, 1080), 1) &&
            Equal(DisplayScalePolicy.RecommendUiScale(1920, double.PositiveInfinity), 1) &&
            Equal(DisplayScalePolicy.RecommendUiScale(0, 1080), 1) &&
            Equal(DisplayScalePolicy.RecommendUiScale(7680, 4320), 2);
        Require(
            evidence,
            recommendationPassed,
            "windows-display-scale-policy-recommends-from-effective-dips-with-wpf-away-from-zero-rounding",
            $"1366x768={DisplayScalePolicy.RecommendUiScale(1366, 768):R}; " +
            $"1440p={DisplayScalePolicy.RecommendUiScale(2560, 1440):R}; " +
            $"4K100={DisplayScalePolicy.RecommendUiScale(3840, 2160):R}; " +
            $"4K125={DisplayScalePolicy.RecommendUiScale(3840 / 1.25, 2160 / 1.25):R}; " +
            $"4K150={DisplayScalePolicy.RecommendUiScale(3840 / 1.5, 2160 / 1.5):R}; " +
            $"4K200={DisplayScalePolicy.RecommendUiScale(3840 / 2d, 2160 / 2d):R}; " +
            $"midpoint={DisplayScalePolicy.RecommendUiScale(1968, 1107):R}");

        var firstScreen = new WindowsDisplayScaleSettings(
                uiScale: .85,
                fontScale: 1.4,
                displayScaleInitialized: false)
            .InitializeForFirstScreen(3840 / 1.25, 2160 / 1.25);
        var customized = firstScreen
            .WithUiScale(1.35)
            .WithFontScale(1.25);
        var initializedAgain = customized.InitializeForFirstScreen(3840, 2160);
        var initializationPassed =
            firstScreen.DisplayScaleInitialized &&
            Equal(firstScreen.UiScale, 1.6) &&
            Equal(firstScreen.FontScale, 1) &&
            initializedAgain.DisplayScaleInitialized &&
            Equal(initializedAgain.UiScale, 1.35) &&
            Equal(initializedAgain.FontScale, 1.25) &&
            Equal(customized.UiScale, 1.35) &&
            Equal(customized.FontScale, 1.25);
        Require(
            evidence,
            initializationPassed,
            "windows-display-scale-settings-first-screen-recommends-once-and-preserves-later-user-values",
            $"first={firstScreen.UiScale:R}/{firstScreen.FontScale:R}/{firstScreen.DisplayScaleInitialized}; " +
            $"again={initializedAgain.UiScale:R}/{initializedAgain.FontScale:R}/{initializedAgain.DisplayScaleInitialized}");

        const string localDataVariable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        var previousLocalDataRoot = Environment.GetEnvironmentVariable(localDataVariable);
        var isolatedRoot = Path.Combine(runRoot, "display-scale-settings");
        try
        {
            Environment.SetEnvironmentVariable(localDataVariable, isolatedRoot);
            Directory.CreateDirectory(isolatedRoot);
            var sameRootWpfSentinel = Path.Combine(isolatedRoot, "view-settings.json");
            const string sentinelContents =
                "{\"UiScale\":1.8,\"FontScale\":1.5,\"DisplayScaleInitialized\":true}";
            File.WriteAllText(sameRootWpfSentinel, sentinelContents, Encoding.UTF8);

            var missing = WindowsDisplayScaleSettingsStore.Load();
            var expectedPath = Path.Combine(isolatedRoot, "display-scale-settings.json");
            var actualWpfPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LumaChart Studio",
                "view-settings.json");
            var isolationPassed =
                PathsEqual(WindowsDisplayScaleSettingsStore.SettingsPath, expectedPath) &&
                !PathsEqual(WindowsDisplayScaleSettingsStore.SettingsPath, sameRootWpfSentinel) &&
                !PathsEqual(WindowsDisplayScaleSettingsStore.SettingsPath, actualWpfPath) &&
                Equal(missing.UiScale, 1) &&
                Equal(missing.FontScale, 1) &&
                !missing.DisplayScaleInitialized &&
                File.ReadAllText(sameRootWpfSentinel, Encoding.UTF8) == sentinelContents;
            Require(
                evidence,
                isolationPassed,
                "windows-display-scale-settings-missing-file-defaults-and-path-is-isolated-from-wpf-view-settings",
                $"path={WindowsDisplayScaleSettingsStore.SettingsPath}; " +
                $"default={missing.UiScale:R}/{missing.FontScale:R}/{missing.DisplayScaleInitialized}; " +
                $"wpf={actualWpfPath}");

            WindowsDisplayScaleSettingsStore.Save(
                new WindowsDisplayScaleSettings(1.1, .95, displayScaleInitialized: true));
            WindowsDisplayScaleSettingsStore.Save(
                new WindowsDisplayScaleSettings(1.35, 1.2, displayScaleInitialized: true));
            var loaded = WindowsDisplayScaleSettingsStore.Load();
            var temporaryFiles = Directory.GetFiles(
                isolatedRoot,
                ".display-scale-settings.json.*.tmp",
                SearchOption.TopDirectoryOnly);
            using var persistedJson = JsonDocument.Parse(
                File.ReadAllBytes(WindowsDisplayScaleSettingsStore.SettingsPath));
            var roundTripPassed =
                Equal(loaded.UiScale, 1.35) &&
                Equal(loaded.FontScale, 1.2) &&
                loaded.DisplayScaleInitialized &&
                persistedJson.RootElement.GetProperty("SchemaVersion").GetInt32() ==
                WindowsDisplayScaleSettingsStore.CurrentSchemaVersion &&
                temporaryFiles.Length == 0 &&
                File.ReadAllText(sameRootWpfSentinel, Encoding.UTF8) == sentinelContents;
            Require(
                evidence,
                roundTripPassed,
                "windows-display-scale-settings-schema-roundtrip-replace-and-temporary-cleanup",
                $"loaded={loaded.UiScale:R}/{loaded.FontScale:R}/{loaded.DisplayScaleInitialized}; " +
                $"schema={persistedJson.RootElement.GetProperty("SchemaVersion").GetInt32()}; " +
                $"temps={temporaryFiles.Length}");

            File.WriteAllText(WindowsDisplayScaleSettingsStore.SettingsPath, "{not-json");
            var corrupt = WindowsDisplayScaleSettingsStore.Load();
            File.WriteAllText(
                WindowsDisplayScaleSettingsStore.SettingsPath,
                "{\"SchemaVersion\":999,\"UiScale\":1.8,\"FontScale\":1.5," +
                "\"DisplayScaleInitialized\":true}");
            var unknownSchema = WindowsDisplayScaleSettingsStore.Load();
            var fallbackPassed =
                Equal(corrupt.UiScale, 1) &&
                Equal(corrupt.FontScale, 1) &&
                !corrupt.DisplayScaleInitialized &&
                Equal(unknownSchema.UiScale, 1) &&
                Equal(unknownSchema.FontScale, 1) &&
                !unknownSchema.DisplayScaleInitialized;
            Require(
                evidence,
                fallbackPassed,
                "windows-display-scale-settings-corrupt-or-unknown-schema-fails-closed-to-defaults",
                $"corrupt={corrupt.UiScale:R}/{corrupt.FontScale:R}/{corrupt.DisplayScaleInitialized}; " +
                $"schema999={unknownSchema.UiScale:R}/{unknownSchema.FontScale:R}/{unknownSchema.DisplayScaleInitialized}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(localDataVariable, previousLocalDataRoot);
        }

        VerifyWindowsDisplayScaleMetrics(evidence);
        VerifyWindowsDisplayScaleRuntimeController(evidence);
    }

    private static void VerifyWindowsDisplayScaleMetrics(SliceEvidence evidence)
    {
        static bool Equal(double left, double right) => Math.Abs(left - right) < 1e-9;

        var desktop150 = WindowsDisplayScaleController.CalculateMetrics(
            new PixelRect(-2560, 0, 2560, 1600),
            new PixelRect(-2560, 0, 2560, 1560),
            1.5);
        var fourK100 = WindowsDisplayScaleController.CalculateMetrics(
            new PixelRect(0, 0, 3840, 2160),
            new PixelRect(0, 0, 3840, 2080),
            1);
        var fourK125 = WindowsDisplayScaleController.CalculateMetrics(
            new PixelRect(0, 0, 3840, 2160),
            new PixelRect(0, 0, 3840, 2080),
            1.25);
        var fourK150 = WindowsDisplayScaleController.CalculateMetrics(
            new PixelRect(0, 0, 3840, 2160),
            new PixelRect(0, 0, 3840, 2080),
            1.5);
        var fourK200 = WindowsDisplayScaleController.CalculateMetrics(
            new PixelRect(0, 0, 3840, 2160),
            new PixelRect(0, 0, 3840, 2080),
            2);
        var invalidScalingAndEmptyWorkArea = WindowsDisplayScaleController.CalculateMetrics(
            new PixelRect(40, 80, 1920, 1080),
            default,
            double.NaN);

        var passed =
            Equal(desktop150.Scaling, 1.5) &&
            Equal(desktop150.EffectiveScreenWidth, 2560 / 1.5) &&
            Equal(desktop150.EffectiveScreenHeight, 1600 / 1.5) &&
            Equal(desktop150.EffectiveWorkAreaWidth, 2560 / 1.5) &&
            Equal(desktop150.EffectiveWorkAreaHeight, 1560 / 1.5) &&
            desktop150.ScreenPixelBounds.X == -2560 &&
            Equal(fourK100.EffectiveScreenWidth, 3840) &&
            Equal(fourK100.EffectiveScreenHeight, 2160) &&
            Equal(fourK100.EffectiveWorkAreaHeight, 2080) &&
            Equal(fourK125.EffectiveScreenWidth, 3072) &&
            Equal(fourK125.EffectiveScreenHeight, 1728) &&
            Equal(fourK125.EffectiveWorkAreaHeight, 1664) &&
            Equal(fourK150.EffectiveScreenWidth, 2560) &&
            Equal(fourK150.EffectiveScreenHeight, 1440) &&
            Equal(fourK150.EffectiveWorkAreaHeight, 2080 / 1.5) &&
            Equal(fourK200.EffectiveScreenWidth, 1920) &&
            Equal(fourK200.EffectiveScreenHeight, 1080) &&
            Equal(fourK200.EffectiveWorkAreaHeight, 1040) &&
            Equal(invalidScalingAndEmptyWorkArea.Scaling, 1) &&
            invalidScalingAndEmptyWorkArea.WorkAreaPixelBounds ==
            invalidScalingAndEmptyWorkArea.ScreenPixelBounds &&
            Equal(invalidScalingAndEmptyWorkArea.EffectiveWorkAreaWidth, 1920) &&
            Equal(invalidScalingAndEmptyWorkArea.EffectiveWorkAreaHeight, 1080);
        Require(
            evidence,
            passed,
            "windows-display-scale-controller-converts-physical-screen-and-workarea-pixels-to-effective-dips",
            $"2560x1600@150={desktop150.EffectiveScreenWidth:F3}x{desktop150.EffectiveScreenHeight:F3}/" +
            $"work{desktop150.EffectiveWorkAreaWidth:F3}x{desktop150.EffectiveWorkAreaHeight:F3}; " +
            $"4K=100:{fourK100.EffectiveScreenWidth:F0}x{fourK100.EffectiveScreenHeight:F0}," +
            $"125:{fourK125.EffectiveScreenWidth:F0}x{fourK125.EffectiveScreenHeight:F0}," +
            $"150:{fourK150.EffectiveScreenWidth:F0}x{fourK150.EffectiveScreenHeight:F0}," +
            $"200:{fourK200.EffectiveScreenWidth:F0}x{fourK200.EffectiveScreenHeight:F0}; " +
            $"invalid={invalidScalingAndEmptyWorkArea.Scaling:R}/" +
            $"fallback={invalidScalingAndEmptyWorkArea.WorkAreaPixelBounds}");
    }

    private static void VerifyWindowsDisplayScaleRuntimeController(SliceEvidence evidence)
    {
        var result = RunDisplayScaleRuntimeSpecIsolated();
        Require(
            evidence,
            result.LayoutPassed,
            "windows-display-scale-controller-updates-real-layout-transform-minimums-without-resizing-current-window",
            result.LayoutDetail);
        Require(
            evidence,
            result.FontPassed,
            "windows-display-font-controller-uses-stable-weak-baselines-and-explicit-materialization-rescan",
            result.FontDetail);
    }

    private static DisplayScaleRuntimeSpecResult RunDisplayScaleRuntimeSpecIsolated()
    {
        var resultPath = Path.Combine(
            Path.GetTempPath(),
            $"LumaChart-Avalonia-display-scale-runtime-{Guid.NewGuid():N}.json");
        try
        {
            var processPath = Environment.ProcessPath
                              ?? throw new InvalidOperationException(
                                  "Unable to resolve the current Specs executable path.");
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(processPath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            }
            startInfo.ArgumentList.Add(DisplayScaleRuntimeSpecChildArgument);
            startInfo.ArgumentList.Add(resultPath);

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException(
                                    "Unable to start the isolated display-scale runtime Spec process.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw new TimeoutException(
                    "The isolated display-scale runtime Spec exceeded 30 seconds.");
            }

            var outputText = standardOutput.GetAwaiter().GetResult();
            var errorText = standardError.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "The isolated display-scale runtime Spec failed. " +
                    $"exit={process.ExitCode}; stdout={outputText}; stderr={errorText}");
            }
            if (!File.Exists(resultPath))
            {
                throw new InvalidOperationException(
                    "The isolated display-scale runtime Spec did not write its result. " +
                    $"stdout={outputText}; stderr={errorText}");
            }

            return JsonSerializer.Deserialize<DisplayScaleRuntimeSpecResult>(
                       File.ReadAllBytes(resultPath),
                       EvidenceJsonOptions)
                   ?? throw new InvalidOperationException(
                       "The isolated display-scale runtime Spec wrote an empty result.");
        }
        finally
        {
            try
            {
                if (File.Exists(resultPath)) File.Delete(resultPath);
            }
            catch (IOException)
            {
                // The per-run temporary root cleanup will handle a transiently
                // locked child result without hiding the primary Spec outcome.
            }
        }
    }

    private static int RunDisplayScaleRuntimeSpecChild(string resultPath)
    {
        try
        {
            var result = RunDisplayScaleRuntimeSpecOnDedicatedStaThread();
            File.WriteAllBytes(
                resultPath,
                JsonSerializer.SerializeToUtf8Bytes(result, EvidenceJsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static DisplayScaleRuntimeSpecResult RunDisplayScaleRuntimeSpecOnDedicatedStaThread()
    {
        DisplayScaleRuntimeSpecResult? result = null;
        ExceptionDispatchInfo? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                result = ExecuteDisplayScaleRuntimeSpec();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = "LumaChart Avalonia Specs STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!completed.Wait(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("The dedicated Avalonia Specs STA thread did not complete.");
        thread.Join();
        failure?.Throw();
        return result ?? throw new InvalidOperationException(
            "The dedicated Avalonia Specs STA thread returned no result.");
    }

    private static DisplayScaleRuntimeSpecResult ExecuteDisplayScaleRuntimeSpec()
    {
        static bool Equal(double left, double right) => Math.Abs(left - right) < 1e-9;

        AppBuilder.Configure<global::Avalonia.Application>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .SetupWithoutStarting();

        var emptyScreenRoot = new LayoutTransformControl { Child = new Grid() };
        var emptyScreenWindow = new PlatformWindow { Content = emptyScreenRoot };
        var screensField = typeof(TopLevel).GetField(
                               "_screens",
                               BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new MissingFieldException(typeof(TopLevel).FullName, "_screens");
        var emptyScreenImpl = DispatchProxy.Create<
            global::Avalonia.Platform.IScreenImpl,
            EmptyScreenDispatchProxy>();
        var emptyScreens = Activator.CreateInstance(
                               typeof(Screens),
                               BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                               binder: null,
                               args: [emptyScreenImpl],
                               culture: CultureInfo.InvariantCulture)
                           ?? throw new InvalidOperationException(
                               "Unable to create the empty Avalonia Screens test seam.");
        screensField.SetValue(emptyScreenWindow, emptyScreens);
        var emptyScreenController = new WindowsDisplayScaleController(
            emptyScreenWindow,
            emptyScreenRoot,
            new AvaloniaWindowFontScaleController(emptyScreenWindow),
            WindowsDisplayScaleSettings.Default);
        var emptyScreenSettings = emptyScreenController.InitializeForScreen(screen: null);
        var emptyScreenPreserved =
            !emptyScreenSettings.DisplayScaleInitialized &&
            Equal(emptyScreenSettings.UiScale, 1) &&
            Equal(emptyScreenSettings.FontScale, 1) &&
            Equal(emptyScreenController.EffectiveScreenWidth, 0) &&
            Equal(emptyScreenController.EffectiveScreenHeight, 0);

        var explicitText = new TextBlock { Text = "explicit", FontSize = 16 };
        var inheritedButton = new Button { Content = "inherited" };
        var content = new StackPanel
        {
            Children =
            {
                explicitText,
                inheritedButton
            }
        };
        var layoutRoot = new LayoutTransformControl { Child = content };
        var window = new PlatformWindow
        {
            Content = layoutRoot,
            FontSize = 20,
            Width = 1600,
            Height = 1000
        };
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        var fontController = new AvaloniaWindowFontScaleController(window);
        var controller = new WindowsDisplayScaleController(
            window,
            layoutRoot,
            fontController,
            WindowsDisplayScaleSettings.Default);

        controller.UpdateScales(1.35, 1.25);
        var firstDiagnostics = controller.Diagnostics;
        var layoutPassed =
            emptyScreenPreserved &&
            controller.CurrentSettings.DisplayScaleInitialized &&
            Equal(controller.CurrentSettings.UiScale, 1.35) &&
            Equal(controller.CurrentSettings.FontScale, 1.25) &&
            Equal(controller.ActualUiScale, 1.35) &&
            Equal(controller.ActualFontScale, 1.25) &&
            !layoutRoot.UseRenderTransform &&
            ReferenceEquals(layoutRoot.LayoutTransform, controller.InterfaceScaleTransform) &&
            firstDiagnostics.UsesLayoutTransform &&
            Equal(window.MinWidth, 1080 * 1.35) &&
            Equal(window.MinHeight, 680 * 1.35) &&
            Equal(window.Width, originalWidth) &&
            Equal(window.Height, originalHeight);
        var layoutDetail =
            $"emptyScreenInitialized={emptyScreenSettings.DisplayScaleInitialized}; " +
            $"settings={controller.CurrentSettings.UiScale:R}/{controller.CurrentSettings.FontScale:R}/" +
            $"{controller.CurrentSettings.DisplayScaleInitialized}; " +
            $"actual={controller.ActualUiScale:R}/{controller.ActualFontScale:R}; " +
            $"layout={firstDiagnostics.UsesLayoutTransform}; " +
            $"min={window.MinWidth:R}x{window.MinHeight:R}; size={window.Width:R}x{window.Height:R}";

        var firstWindowFontSize = window.FontSize;
        var firstExplicitFontSize = explicitText.FontSize;
        var firstInheritedFontSize = inheritedButton.FontSize;
        var firstFontPass =
            Equal(firstWindowFontSize, 25) &&
            Equal(firstExplicitFontSize, 20) &&
            Equal(firstInheritedFontSize, 25);
        controller.UpdateFontScale(.8);
        var stableBaselinePass =
            Equal(window.FontSize, 16) &&
            Equal(explicitText.FontSize, 12.8) &&
            Equal(inheritedButton.FontSize, 16);
        var dynamicText = new TextBlock { Text = "dynamic", FontSize = 10 };
        content.Children.Add(dynamicText);
        var unchangedBeforeExplicitRescan = Equal(dynamicText.FontSize, 10);
        controller.ApplyFontToMaterializedContent();
        var finalDiagnostics = controller.Diagnostics;
        var fontPassed =
            firstFontPass &&
            stableBaselinePass &&
            unchangedBeforeExplicitRescan &&
            Equal(dynamicText.FontSize, 8) &&
            finalDiagnostics.FontScaledElementCount >= 2 &&
            finalDiagnostics.FontInheritedOrDefaultSkippedCount >= 1 &&
            finalDiagnostics.FontCapturedBaselineCount >= 2 &&
            finalDiagnostics.FontScannedObjectCount >= 5;
        var fontDetail =
            $"first={firstWindowFontSize:R}/{firstExplicitFontSize:R}/{firstInheritedFontSize:R}; " +
            $"stable={window.FontSize:R}/{explicitText.FontSize:R}/{inheritedButton.FontSize:R}; " +
            $"dynamic={dynamicText.FontSize:R}; scanned={finalDiagnostics.FontScannedObjectCount}; " +
            $"scaled={finalDiagnostics.FontScaledElementCount}; " +
            $"skipped={finalDiagnostics.FontInheritedOrDefaultSkippedCount}; " +
            $"baselines={finalDiagnostics.FontCapturedBaselineCount}";
        return new DisplayScaleRuntimeSpecResult(
            layoutPassed,
            layoutDetail,
            fontPassed,
            fontDetail);
    }

    private static async Task VerifyAudioVisualizationContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var limits = AudioWaveformService.CapacityDiagnostics;
        Require(
            evidence,
            limits.MaximumBuckets == 2_000_000 &&
            limits.MaximumSpectrogramFrames == 180_000 &&
            limits.MaximumCachedBytes >= 4L *
            (2_000_000L * sizeof(float) + 180_000L * 96),
            "audio-visualization-capacity-matches-wpf-and-cache-holds-four-max-scenes",
            $"wave={limits.MaximumBuckets:N0}; spectrum={limits.MaximumSpectrogramFrames:N0}; " +
            $"cache={limits.MaximumCachedBytes:N0}");

        var audioRoot = Path.Combine(runRoot, "audio-visualization");
        Directory.CreateDirectory(audioRoot);
        var wavePath = Path.Combine(audioRoot, "deterministic-tone.wav");
        const int sampleRate = 44_100;
        using (var writer = new WaveFileWriter(wavePath, new WaveFormat(sampleRate, 16, 1)))
        {
            var pcm = new byte[sampleRate * 2];
            for (var sample = 0; sample < sampleRate; sample++)
            {
                var value = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * sample / sampleRate) * 12_000);
                pcm[sample * 2] = (byte)(value & 0xff);
                pcm[sample * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            writer.Write(pcm, 0, pcm.Length);
        }
        var decoded = await AudioWaveformService.LoadVisualizationAsync(wavePath).ConfigureAwait(false);
        Require(
            evidence,
            decoded.Waveform is { Count: > 0 } &&
            decoded.Spectrogram is { FrameCount: > 0, FrequencyBands: 96 } &&
            decoded.Waveform.Count <= limits.MaximumBuckets &&
            decoded.Spectrogram.FrameCount <= limits.MaximumSpectrogramFrames,
            "audio-visualization-real-wav-decodes-waveform-and-spectrum-off-ui-path",
            $"wave={decoded.Waveform?.Count ?? 0}; frames={decoded.Spectrogram?.FrameCount ?? 0}; " +
            $"bands={decoded.Spectrogram?.FrequencyBands ?? 0}");

        var timingMap = new TimingMap(
        [
            new TimingEvent
            {
                Beat = BeatPosition.Zero,
                Bpm = 120,
                Delay = 343
            }
        ]);
        var judgementMilliseconds = TimelineAudioBackgroundSceneBuilder.MillisecondsAtY(
            timingMap,
            2.5,
            100,
            200,
            200);
        var scene = TimelineAudioBackgroundSceneBuilder.Build(
            decoded,
            timingMap,
            2.5,
            100,
            200,
            400,
            includeWaveform: true,
            includeSpectrogram: true);
        Require(
            evidence,
            Math.Abs(judgementMilliseconds - 907) < .001 &&
            scene.WaveformPeaks?.Length == 1200 &&
            scene.Spectrogram is { Width: 96, Height: 1200 } &&
            scene.Spectrogram.BgraPixels.Any(value => value != 0),
            "audio-background-scene-uses-timing-map-delay-and-three-viewport-clipping",
            $"judgementMs={judgementMilliseconds:F3}; rows={scene.WaveformPeaks?.Length ?? 0}; " +
            $"spectrum={scene.Spectrogram?.Width ?? 0}x{scene.Spectrogram?.Height ?? 0}");

        VerifyWindowsAudioPanelContracts(evidence, runRoot, decoded, timingMap);

        var staleVisualization = new AudioVisualization(
            new AudioWaveform(2, [0.1f], 2),
            null);
        var winnerVisualization = new AudioVisualization(
            new AudioWaveform(2, [0.9f], 2),
            null);
        var staleCompletion = new TaskCompletionSource<AudioVisualization>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var revisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stale.wav"] = "stale-r1",
            ["winner.wav"] = "winner-r1"
        };
        await using (var coordinator = new WindowsAudioVisualizationViewModel(
                         (path, _) => path.Equals("stale.wav", StringComparison.OrdinalIgnoreCase)
                             ? staleCompletion.Task
                             : Task.FromResult(winnerVisualization),
                         path => revisions.GetValueOrDefault(path)))
        {
            var staleTab = Guid.NewGuid();
            var winnerTab = Guid.NewGuid();
            coordinator.Activate(staleTab, "stale.wav", "stale.wav");
            var staleLoad = coordinator.PendingLoad;
            coordinator.Activate(winnerTab, "winner.wav", "winner.wav");
            await coordinator.PendingLoad.ConfigureAwait(false);
            staleCompletion.TrySetResult(staleVisualization);
            await staleLoad.ConfigureAwait(false);
            Require(
                evidence,
                coordinator.ActiveTabId == winnerTab &&
                ReferenceEquals(coordinator.Visualization, winnerVisualization) &&
                coordinator.LastError == null,
                "audio-visualization-tab-switch-is-latest-wins-even-if-stale-loader-ignores-cancel",
                $"active={coordinator.ActiveTabId}; winner={ReferenceEquals(coordinator.Visualization, winnerVisualization)}");
        }

        var revision = "r1";
        var revisionLoads = 0;
        await using (var coordinator = new WindowsAudioVisualizationViewModel(
                         (_, _) => Task.FromResult(++revisionLoads == 1
                             ? staleVisualization
                             : winnerVisualization),
                         _ => revision))
        {
            var tab = Guid.NewGuid();
            coordinator.Activate(tab, "revision.wav", "revision.wav");
            await coordinator.PendingLoad.ConfigureAwait(false);
            revision = "r2";
            coordinator.Activate(tab, "revision.wav", "revision.wav");
            await coordinator.PendingLoad.ConfigureAwait(false);
            Require(
                evidence,
                revisionLoads == 2 && ReferenceEquals(coordinator.Visualization, winnerVisualization),
                "audio-visualization-same-path-source-revision-reloads-and-publishes-latest",
                $"loads={revisionLoads}; winner={ReferenceEquals(coordinator.Visualization, winnerVisualization)}");
        }
    }

    private static void VerifyWindowsAudioPanelContracts(
        SliceEvidence evidence,
        string runRoot,
        AudioVisualization visualization,
        TimingMap timingMap)
    {
        static bool Equal(double left, double right) => Math.Abs(left - right) < 1e-9;

        var defaults = WindowsAudioPanelSettings.Default;
        Require(
            evidence,
            defaults.Waveform.Mode == WindowsAudioPanelDisplayMode.Side &&
            !defaults.Waveform.Collapsed &&
            defaults.Waveform.Docked &&
            Equal(defaults.Waveform.X, 4000) &&
            Equal(defaults.Waveform.Y, 0) &&
            Equal(defaults.Waveform.Width, 92) &&
            defaults.Spectrogram.Mode == WindowsAudioPanelDisplayMode.Side &&
            defaults.Spectrogram.Collapsed &&
            defaults.Spectrogram.Docked &&
            Equal(defaults.Spectrogram.X, 4000) &&
            Equal(defaults.Spectrogram.Y, 0) &&
            Equal(defaults.Spectrogram.Width, 132),
            "windows-audio-panels-factory-state-matches-wpf-wave-expanded-and-spectrum-collapsed",
            $"wave={defaults.Waveform.Mode}/{defaults.Waveform.Collapsed}/" +
            $"{defaults.Waveform.Docked}/{defaults.Waveform.Width:R}; " +
            $"spectrum={defaults.Spectrogram.Mode}/{defaults.Spectrogram.Collapsed}/" +
            $"{defaults.Spectrogram.Docked}/{defaults.Spectrogram.Width:R}");

        var docked = WindowsAudioPanelLayoutPolicy.Resolve(
            defaults,
            parentWidth: 1000,
            parentHeight: 700,
            new WindowsAudioTrackBounds(400, 700));
        var waveformRememberedAfterResize = WindowsAudioPanelLayoutPolicy.ClampAfterResize(
            defaults.Waveform,
            WindowsAudioPanelKind.Waveform,
            parentWidth: 1000,
            parentHeight: 700);
        var spectrogramRememberedAfterResize = WindowsAudioPanelLayoutPolicy.ClampAfterResize(
            defaults.Spectrogram,
            WindowsAudioPanelKind.Spectrogram,
            parentWidth: 1000,
            parentHeight: 700);
        Require(
            evidence,
            Equal(docked.Waveform.X, 300) &&
            Equal(docked.Waveform.Width, 92) &&
            Equal(docked.Waveform.Y, 0) &&
            !docked.Waveform.ToolbarOnly &&
            Equal(docked.Spectrogram.X, 708) &&
            Equal(docked.Spectrogram.Width, 92) &&
            Equal(docked.Spectrogram.Height, 25) &&
            docked.Spectrogram.ToolbarOnly &&
            Equal(waveformRememberedAfterResize.X, 4000) &&
            Equal(spectrogramRememberedAfterResize.X, 4000),
            "windows-audio-panels-dock-on-opposite-track-sides-with-eight-dip-gap",
            $"wave={docked.Waveform.X:R}/{docked.Waveform.Width:R}; " +
            $"spectrum={docked.Spectrogram.X:R}/{docked.Spectrogram.Width:R}/" +
            $"{docked.Spectrogram.Height:R}");

        var freeSettings = new WindowsAudioPanelSettings(
            new WindowsAudioPanelState(
                450,
                0,
                120,
                collapsed: false,
                docked: false,
                WindowsAudioPanelDisplayMode.Side),
            new WindowsAudioPanelState(
                460,
                36,
                150,
                collapsed: true,
                docked: false,
                WindowsAudioPanelDisplayMode.Side));
        var free = WindowsAudioPanelLayoutPolicy.Resolve(
            freeSettings,
            parentWidth: 1000,
            parentHeight: 700,
            new WindowsAudioTrackBounds(400, 700));
        Require(
            evidence,
            Equal(free.Waveform.X, 450) &&
            Equal(free.Waveform.Y, 0) &&
            Equal(free.Spectrogram.X, 460) &&
            Equal(free.Spectrogram.Y, 36),
            "windows-audio-panels-manual-overlap-remains-valid-and-is-not-auto-repositioned",
            $"wave={free.Waveform.X:R}/{free.Waveform.Y:R}; " +
            $"spectrum={free.Spectrogram.X:R}/{free.Spectrogram.Y:R}");

        var expandedDrag = WindowsAudioPanelLayoutPolicy.ApplyDrag(
            defaults.Waveform,
            WindowsAudioPanelKind.Waveform,
            proposedX: 931,
            proposedY: 240,
            parentWidth: 1000,
            parentHeight: 700);
        var toolbarDrag = WindowsAudioPanelLayoutPolicy.ApplyDrag(
            defaults.Spectrogram,
            WindowsAudioPanelKind.Spectrogram,
            proposedX: -12,
            proposedY: 900,
            parentWidth: 1000,
            parentHeight: 700);
        Require(
            evidence,
            Equal(expandedDrag.X, 908) &&
            Equal(expandedDrag.Y, 0) &&
            !expandedDrag.Docked &&
            Equal(toolbarDrag.X, 0) &&
            Equal(toolbarDrag.Y, 595) &&
            !toolbarDrag.Docked,
            "windows-audio-panels-drag-clamps-x-toolbar-y-and-detaches-auto-dock",
            $"expanded={expandedDrag.X:R}/{expandedDrag.Y:R}/{expandedDrag.Docked}; " +
            $"toolbar={toolbarDrag.X:R}/{toolbarDrag.Y:R}/{toolbarDrag.Docked}");

        var sideScene = TimelineAudioBackgroundSceneBuilder.BuildSidePanel(
            visualization,
            timingMap,
            sceneBeat: 2.5,
            pixelsPerBeat: 100,
            judgementY: 200,
            viewportHeight: 400,
            includeWaveform: true,
            includeSpectrogram: true);
        var sideAlpha = sideScene.Spectrogram?.BgraPixels
            .Where((_, index) => (index & 3) == 3)
            .ToArray() ?? [];
        var wpfSideAlpha =
            sideAlpha.Any(alpha => alpha == 255) &&
            sideAlpha.All(alpha => alpha is 0 or 255);
        Require(
            evidence,
            Equal(sideScene.RowPixels, 1) &&
            sideScene.WaveformPeaks?.Length == 1200 &&
            sideScene.Spectrogram is { Width: 96, Height: 1200 } &&
            wpfSideAlpha,
            "windows-audio-side-scenes-use-wpf-alpha-one-row-dip-and-three-viewport-worker-results",
            $"rowDip={sideScene.RowPixels:R}; wave={sideScene.WaveformPeaks?.Length ?? 0}; " +
            $"spectrum={sideScene.Spectrogram?.Width ?? 0}x{sideScene.Spectrogram?.Height ?? 0}; " +
            $"binaryAlpha={wpfSideAlpha}; opaqueRows={sideAlpha.Count(alpha => alpha == 255)}");

        var retainedForwardTranslation = TimelineRetainedAudioScenePolicy.Translation(
            currentBeat: 20,
            sceneBeat: 0,
            pixelsPerBeat: 100,
            viewportHeight: 400);
        var retainedReverseTranslation = TimelineRetainedAudioScenePolicy.Translation(
            currentBeat: 0,
            sceneBeat: 20,
            pixelsPerBeat: 100,
            viewportHeight: 400);
        Require(
            evidence,
            Equal(retainedForwardTranslation, 400) &&
            Equal(retainedReverseTranslation, -400),
            "windows-audio-retained-scene-clamps-expired-frame-instead-of-flashing-empty",
            $"forward={retainedForwardTranslation:R}; reverse={retainedReverseTranslation:R}");

        var negativeDelayMap = new TimingMap(
        [
            new TimingEvent
            {
                Beat = BeatPosition.Zero,
                Bpm = 120,
                Delay = -1000
            }
        ]);
        var negativeDelayVisualization = new AudioVisualization(
            new AudioWaveform(
                2,
                Enumerable.Repeat(.8f, 1000).ToArray(),
                2000),
            new AudioSpectrogram(
                10,
                2,
                Enumerable.Repeat((byte)200, 400).ToArray(),
                2000,
                40,
                16_000));
        var negativeDelayScene = TimelineAudioBackgroundSceneBuilder.BuildSidePanel(
            negativeDelayVisualization,
            negativeDelayMap,
            sceneBeat: 0,
            pixelsPerBeat: 100,
            judgementY: 100,
            viewportHeight: 200,
            includeWaveform: true,
            includeSpectrogram: true);
        const int negativeBeatRow = 450;
        var negativeSpectrumRowTransparent =
            negativeDelayScene.Spectrogram?.BgraPixels
                .Skip(negativeBeatRow * 2 * 4)
                .Take(2 * 4)
                .Where((_, index) => (index & 3) == 3)
                .All(alpha => alpha == 0) == true;
        Require(
            evidence,
            negativeDelayScene.WaveformPeaks?[negativeBeatRow] == 0 &&
            negativeSpectrumRowTransparent,
            "windows-audio-scenes-never-render-negative-beats-even-when-negative-delay-maps-to-positive-ms",
            $"wave={negativeDelayScene.WaveformPeaks?[negativeBeatRow]}; " +
            $"spectrumTransparent={negativeSpectrumRowTransparent}");

        const string localDataVariable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
        var previousLocalDataRoot = Environment.GetEnvironmentVariable(localDataVariable);
        var isolatedRoot = Path.Combine(runRoot, "audio-panel-settings");
        try
        {
            Environment.SetEnvironmentVariable(localDataVariable, isolatedRoot);
            Directory.CreateDirectory(isolatedRoot);
            var wpfSentinel = Path.Combine(isolatedRoot, "view-settings.json");
            const string sentinelContents = "{\"WaveformPanelWidth\":177}";
            File.WriteAllText(wpfSentinel, sentinelContents, Encoding.UTF8);

            var custom = new WindowsAudioPanelSettings(
                new WindowsAudioPanelState(
                    123,
                    -20,
                    177,
                    collapsed: true,
                    docked: false,
                    WindowsAudioPanelDisplayMode.Background),
                new WindowsAudioPanelState(
                    456,
                    31,
                    211,
                    collapsed: false,
                    docked: false,
                    WindowsAudioPanelDisplayMode.Side));
            WindowsAudioPanelSettingsStore.Save(defaults);
            WindowsAudioPanelSettingsStore.Save(custom);
            var loaded = WindowsAudioPanelSettingsStore.Load();
            var temporaryFiles = Directory.GetFiles(
                isolatedRoot,
                ".audio-panel-settings.json.*.tmp",
                SearchOption.TopDirectoryOnly);
            using var persistedJson = JsonDocument.Parse(
                File.ReadAllBytes(WindowsAudioPanelSettingsStore.SettingsPath));
            var persistedPass =
                PathsEqual(
                    WindowsAudioPanelSettingsStore.SettingsPath,
                    Path.Combine(isolatedRoot, "audio-panel-settings.json")) &&
                Equal(loaded.Waveform.X, 123) &&
                Equal(loaded.Waveform.Y, -20) &&
                Equal(loaded.Waveform.Width, 177) &&
                loaded.Waveform.Collapsed &&
                !loaded.Waveform.Docked &&
                loaded.Waveform.Mode == WindowsAudioPanelDisplayMode.Background &&
                Equal(loaded.Spectrogram.X, 456) &&
                Equal(loaded.Spectrogram.Y, 31) &&
                Equal(loaded.Spectrogram.Width, 211) &&
                !loaded.Spectrogram.Collapsed &&
                !loaded.Spectrogram.Docked &&
                loaded.Spectrogram.Mode == WindowsAudioPanelDisplayMode.Side &&
                persistedJson.RootElement.GetProperty("SchemaVersion").GetInt32() ==
                WindowsAudioPanelSettingsStore.CurrentSchemaVersion &&
                temporaryFiles.Length == 0 &&
                File.ReadAllText(wpfSentinel, Encoding.UTF8) == sentinelContents;
            Require(
                evidence,
                persistedPass,
                "windows-audio-panel-settings-are-isolated-atomic-and-roundtrip-both-panels",
                $"path={WindowsAudioPanelSettingsStore.SettingsPath}; " +
                $"wave={loaded.Waveform.X:R}/{loaded.Waveform.Y:R}/{loaded.Waveform.Width:R}/" +
                $"{loaded.Waveform.Mode}; spectrum={loaded.Spectrogram.X:R}/" +
                $"{loaded.Spectrogram.Y:R}/{loaded.Spectrogram.Width:R}/{loaded.Spectrogram.Mode}; " +
                $"temps={temporaryFiles.Length}");

            File.WriteAllText(WindowsAudioPanelSettingsStore.SettingsPath, "{not-json");
            var corrupt = WindowsAudioPanelSettingsStore.Load();
            File.WriteAllText(
                WindowsAudioPanelSettingsStore.SettingsPath,
                "{\"SchemaVersion\":999,\"Waveform\":{},\"Spectrogram\":{}}");
            var unknownSchema = WindowsAudioPanelSettingsStore.Load();
            File.WriteAllText(
                WindowsAudioPanelSettingsStore.SettingsPath,
                "{\"SchemaVersion\":1,\"Waveform\":{},\"Spectrogram\":{}}");
            var incomplete = WindowsAudioPanelSettingsStore.Load();
            var fallbackPass =
                IsDefaultAudioPanelSettings(corrupt) &&
                IsDefaultAudioPanelSettings(unknownSchema) &&
                IsDefaultAudioPanelSettings(incomplete) &&
                File.ReadAllText(wpfSentinel, Encoding.UTF8) == sentinelContents;
            Require(
                evidence,
                fallbackPass,
                "windows-audio-panel-settings-corrupt-incomplete-or-unknown-schema-fail-closed",
                $"corrupt={IsDefaultAudioPanelSettings(corrupt)}; " +
                $"unknown={IsDefaultAudioPanelSettings(unknownSchema)}; " +
                $"incomplete={IsDefaultAudioPanelSettings(incomplete)}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(localDataVariable, previousLocalDataRoot);
        }
    }

    private static bool IsDefaultAudioPanelSettings(WindowsAudioPanelSettings settings) =>
        settings.Waveform.Mode == WindowsAudioPanelDisplayMode.Side &&
        !settings.Waveform.Collapsed &&
        settings.Waveform.Docked &&
        Math.Abs(settings.Waveform.Width - 92) < 1e-9 &&
        settings.Spectrogram.Mode == WindowsAudioPanelDisplayMode.Side &&
        settings.Spectrogram.Collapsed &&
        settings.Spectrogram.Docked &&
        Math.Abs(settings.Spectrogram.Width - 132) < 1e-9;

    private static Task VerifyWindowsKeySoundPlannerContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var keySoundRoot = Path.Combine(runRoot, "key-sound-planner");
        Directory.CreateDirectory(keySoundRoot);
        var chartPath = Path.Combine(keySoundRoot, "planner.mc");
        var noteSoundPath = Path.Combine(keySoundRoot, "note.wav");
        File.WriteAllBytes(chartPath, [0x7b, 0x7d]);
        File.WriteAllBytes(noteSoundPath, [0x52, 0x49, 0x46, 0x46]);

        var malformedBackground = MalodyMcSerializer.Parse(
            """
            {
              "meta": {
                "creator": "compat",
                "mode": 0,
                "mode_ext": { "column": 4 },
                "song": { "title": "Malformed BGM", "artist": "compat", "file": "" }
              },
              "time": [{ "beat": [0,0,1], "bpm": 120 }],
              "note": [
                { "beat": [0,0,1], "type": 1, "sound": "song.ogg", "column": 3, "offset": 357 },
                { "beat": [1,0,1], "column": 0 }
              ]
            }
            """,
            "malformed-background.mc",
            "Avalonia compatibility specs");
        var malformedPlanner = new WindowsKeySoundPlaybackCoordinator(
            malformedBackground,
            chartPath,
            new HashSet<int>(),
            new WindowsKeySoundPlaybackOptions(
                Enabled: true,
                Preset: KeySoundPreset.Snare,
                CustomFilePath: null,
                PlayLongNoteEndSound: true,
                GlobalOffsetMilliseconds: 0,
                ChartOffsetMilliseconds: 0));
        Require(
            evidence,
            malformedBackground.Notes.Count == 1 &&
            malformedBackground.UnknownNotes.Count == 1 &&
            string.Equals(
                malformedBackground.Metadata.AudioFile,
                "song.ogg",
                StringComparison.Ordinal) &&
            malformedPlanner.Events.Length == 1 &&
            malformedPlanner.Events[0].CustomFilePath == null,
            "windows-malody-background-control-never-becomes-a-key-sound-even-with-malformed-column",
            $"notes={malformedBackground.Notes.Count}; " +
            $"unknown={malformedBackground.UnknownNotes.Count}; " +
            $"audio={malformedBackground.Metadata.AudioFile}; " +
            $"keyEvents={malformedPlanner.Events.Length}; " +
            $"custom={(malformedPlanner.Events.IsEmpty ? "missing" : malformedPlanner.Events[0].CustomFilePath ?? "none")}");

        var customId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var hiddenId = Guid.NewGuid();
        var draftId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var hiddenGroups = new HashSet<int> { 7 };
        var document = new ChartDocument
        {
            FilePath = chartPath,
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
            Notes =
            [
                new ChartNote
                {
                    Id = customId,
                    Lane = 0,
                    Beat = new BeatPosition(1, 0, 1),
                    Sound = "note.wav",
                    Volume = 50
                },
                new ChartNote
                {
                    Id = missingId,
                    Lane = 1,
                    Beat = new BeatPosition(2, 0, 1),
                    Sound = "missing.wav",
                    Volume = 0
                },
                new ChartNote
                {
                    Id = hiddenId,
                    Lane = 2,
                    Beat = new BeatPosition(3, 0, 1),
                    Group = 7
                },
                new ChartNote
                {
                    Id = draftId,
                    IsDraft = true,
                    DraftColumn = 8,
                    Beat = new BeatPosition(4, 0, 1)
                },
                new ChartNote
                {
                    Id = holdId,
                    Lane = 3,
                    Beat = new BeatPosition(5, 0, 1),
                    EndBeat = new BeatPosition(6, 0, 1),
                    Sound = "note.wav",
                    Volume = 80
                }
            ]
        };
        var options = new WindowsKeySoundPlaybackOptions(
            Enabled: true,
            Preset: KeySoundPreset.HandDrum,
            CustomFilePath: null,
            PlayLongNoteEndSound: true,
            GlobalOffsetMilliseconds: 12,
            ChartOffsetMilliseconds: -4);
        var planner = new WindowsKeySoundPlaybackCoordinator(
            document,
            chartPath,
            hiddenGroups,
            options);

        var customHead = planner.Events.Single(item => item.NoteId == customId);
        var missingFallback = planner.Events.Single(item => item.NoteId == missingId);
        var holdEvents = planner.Events.Where(item => item.NoteId == holdId).ToArray();
        Require(
            evidence,
            planner.Events.Length == 4 &&
            planner.Events.All(item => item.NoteId != hiddenId && item.NoteId != draftId) &&
            customHead.Preset == KeySoundPreset.Custom &&
            customHead.CustomFilePath != null &&
            PathsEqual(customHead.CustomFilePath, noteSoundPath) &&
            Math.Abs(customHead.Gain - .5f) < 1e-6 &&
            missingFallback.Preset == KeySoundPreset.HandDrum &&
            missingFallback.CustomFilePath == null &&
            Math.Abs(missingFallback.Gain - 1f) < 1e-6 &&
            holdEvents.Length == 2 && holdEvents.Count(item => item.IsLongNoteEnd) == 1,
            "windows-key-sound-filters-draft-hidden-and-applies-custom-volume-ln-tail",
            $"events={planner.Events.Length}; custom={customHead.Preset}/{customHead.Gain:R}; " +
            $"fallback={missingFallback.Preset}/{missingFallback.Gain:R}; holdEvents={holdEvents.Length}");
        Require(
            evidence,
            planner.PreloadPaths.Length == 1 &&
            PathsEqual(planner.PreloadPaths[0], noteSoundPath),
            "windows-key-sound-preload-paths-are-existing-custom-files-only-and-deduplicated",
            $"preloads={string.Join('|', planner.PreloadPaths)}");

        document.Notes.Add(new ChartNote { Lane = 0, Beat = new BeatPosition(7, 0, 1) });
        hiddenGroups.Clear();
        Require(
            evidence,
            planner.Events.Length == 4 && planner.Events.All(item => item.NoteId != hiddenId),
            "windows-key-sound-plan-snapshots-document-and-hidden-groups",
            $"eventsAfterMutation={planner.Events.Length}; hiddenPresent={planner.Events.Any(item => item.NoteId == hiddenId)}");

        var chordA = Guid.NewGuid();
        var chordB = Guid.NewGuid();
        var silentCustom = Guid.NewGuid();
        var later = Guid.NewGuid();
        var schedulingDocument = new ChartDocument
        {
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
            Notes =
            [
                new ChartNote { Id = chordA, Lane = 0, Beat = new BeatPosition(1, 0, 1) },
                new ChartNote { Id = silentCustom, Lane = 1, Beat = new BeatPosition(1, 0, 1), Sound = "note.wav", Volume = 0 },
                new ChartNote { Id = chordB, Lane = 2, Beat = new BeatPosition(1, 1, 1000) },
                new ChartNote { Id = later, Lane = 3, Beat = new BeatPosition(1, 3, 1000) }
            ]
        };
        var schedulingOptions = options with
        {
            Preset = KeySoundPreset.Snare,
            PlayLongNoteEndSound = false,
            GlobalOffsetMilliseconds = 0,
            ChartOffsetMilliseconds = 0
        };
        var schedulingPlanner = new WindowsKeySoundPlaybackCoordinator(
            schedulingDocument,
            chartPath,
            new HashSet<int>(),
            schedulingOptions);
        var beforeHorizon = schedulingPlanner.TakeDue(307.9, 1, 12);
        var firstChord = schedulingPlanner.TakeDue(308, 1, 12);
        var secondChord = schedulingPlanner.TakeDue(309.5, 1, 12);
        var expectedChordVoiceGain = WindowsKeySoundService.ChordVoiceGain(2);
        Require(
            evidence,
            beforeHorizon.Length == 0 &&
            firstChord.Length == 2 &&
            firstChord.All(item => item.ChordVoiceCount == 2) &&
            firstChord.All(item => Math.Abs(item.Gain - expectedChordVoiceGain) < 1e-6) &&
            firstChord.All(item => Math.Abs(item.DelayMilliseconds - 180) < 1e-6) &&
            firstChord.All(item => item.NoteId != silentCustom) &&
            secondChord.Length == 1 && secondChord[0].NoteId == later,
            "windows-key-sound-lookahead-chord-zero-gain-and-delay-use-current-loudness-curve",
            $"before={beforeHorizon.Length}; first={firstChord.Length}; " +
            $"voices={(firstChord.IsEmpty ? 0 : firstChord[0].ChordVoiceCount)}; " +
            $"gain={(firstChord.IsEmpty ? 0 : firstChord[0].Gain):R}; " +
            $"delay={(firstChord.IsEmpty ? 0 : firstChord[0].DelayMilliseconds):R}; second={secondChord.Length}");

        var lateDocument = new ChartDocument
        {
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
            Notes = [new ChartNote { Lane = 0, Beat = BeatPosition.Zero }]
        };
        var latePlanner = new WindowsKeySoundPlaybackCoordinator(
            lateDocument,
            chartPath,
            new HashSet<int>(),
            schedulingOptions);
        var late = latePlanner.TakeDue(100.001, 1, 0);
        var resetAtBoundary = schedulingPlanner.ResetCursor(500.75, 1);
        var expectedResetThreshold = WindowsKeySoundPlaybackCoordinator.ResetThresholdMilliseconds(
            500.75,
            1,
            0);
        // Deliberately no longer WPF 1.1.0's number here. That build added the
        // key-sound compensation after dividing the note distance by the rate,
        // which treats a value the user dialled in by ear as real time: at 0.5x
        // a -25 ms setting reached the note 12.5 chart-milliseconds out, so key
        // sounds drifted against the music at every rate but 1x. The
        // compensation is chart time and is now divided with the distance, which
        // is what keeps it matched at every rate.
        //
        //   was  (500 - 400) / .5 + -25  = 175
        //   now  (500 - 400 + -25) / .5  = 150
        //
        // The reset threshold above is unchanged; its offset term is zero in
        // this case and the tolerance is real time either way.
        var rateOffsetDelay = WindowsKeySoundPlaybackCoordinator.WallClockDelay(500, 400, .5, -25);
        Require(
            evidence,
            late.Length == 0 && latePlanner.NextEventIndex == 1 &&
            Math.Abs(expectedResetThreshold - 500) < 1e-9 && resetAtBoundary == 0 &&
            Math.Abs(rateOffsetDelay - 150) < 1e-9,
            "windows-key-sound-late-skip-reset-threshold-and-rate-scaled-offset",
            $"late={late.Length}; lateIndex={latePlanner.NextEventIndex}; " +
            $"threshold={expectedResetThreshold:R}; resetIndex={resetAtBoundary}; rateOffsetDelay={rateOffsetDelay:R}");

        var globalCustomMissing = new WindowsKeySoundPlaybackCoordinator(
            lateDocument,
            chartPath,
            new HashSet<int>(),
            schedulingOptions with
            {
                Preset = KeySoundPreset.Custom,
                CustomFilePath = Path.Combine(keySoundRoot, "missing-global.wav")
            });
        var globalCustomNull = new WindowsKeySoundPlaybackCoordinator(
            lateDocument,
            chartPath,
            new HashSet<int>(),
            schedulingOptions with
            {
                Preset = KeySoundPreset.Custom,
                CustomFilePath = null
            });
        var globalCustomExisting = new WindowsKeySoundPlaybackCoordinator(
            lateDocument,
            chartPath,
            new HashSet<int>(),
            schedulingOptions with
            {
                Preset = KeySoundPreset.Custom,
                CustomFilePath = noteSoundPath
            });
        var missingGlobalEvent = globalCustomMissing.Events.Single();
        var nullGlobalEvent = globalCustomNull.Events.Single();
        var existingGlobalEvent = globalCustomExisting.Events.Single();
        Require(
            evidence,
            missingGlobalEvent.Preset == KeySoundPreset.Snare &&
            missingGlobalEvent.CustomFilePath == null &&
            nullGlobalEvent.Preset == KeySoundPreset.Snare &&
            nullGlobalEvent.CustomFilePath == null &&
            existingGlobalEvent.Preset == KeySoundPreset.Custom &&
            existingGlobalEvent.CustomFilePath != null &&
            PathsEqual(existingGlobalEvent.CustomFilePath, noteSoundPath),
            "windows-key-sound-global-custom-missing-or-null-falls-back-to-snare",
            $"missing={missingGlobalEvent.Preset}/{missingGlobalEvent.CustomFilePath ?? "none"}; " +
            $"null={nullGlobalEvent.Preset}/{nullGlobalEvent.CustomFilePath ?? "none"}; " +
            $"existing={existingGlobalEvent.Preset}/{existingGlobalEvent.CustomFilePath}");

        using (var cancellation = new CancellationTokenSource())
        {
            var probes = 0;
            var cancellationDocument = new ChartDocument
            {
                Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
                Notes = Enumerable.Range(0, 2_048)
                    .Select(index => new ChartNote
                    {
                        Lane = index % 4,
                        Beat = new BeatPosition(index, 0, 1),
                        Sound = "note.wav"
                    })
                    .ToList()
            };
            var canceled = false;
            try
            {
                _ = new WindowsKeySoundPlaybackCoordinator(
                    cancellationDocument,
                    chartPath,
                    new HashSet<int>(),
                    schedulingOptions,
                    _ =>
                    {
                        if (Interlocked.Increment(ref probes) == 300)
                            cancellation.Cancel();
                        return true;
                    },
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            Require(
                evidence,
                canceled && probes >= 300 && probes < cancellationDocument.Notes.Count,
                "windows-key-sound-plan-build-observes-cancellation-within-large-note-walk",
                $"canceled={canceled}; probes={probes}; notes={cancellationDocument.Notes.Count}");
        }

        var previousSettingsRoot = Environment.GetEnvironmentVariable(
            "LUMACHART_AVALONIA_LOCAL_DATA_ROOT");
        var isolatedSettingsRoot = Path.Combine(keySoundRoot, "settings");
        try
        {
            Environment.SetEnvironmentVariable(
                "LUMACHART_AVALONIA_LOCAL_DATA_ROOT",
                isolatedSettingsRoot);
            var normalizedSettings = new WindowsKeySoundSettings(
                enableKeySounds: true,
                volume: 1.5,
                preset: KeySoundPreset.Custom,
                customFilePath: noteSoundPath,
                playLongNoteEndSound: true,
                globalOffset: 900,
                perChartOffsets:
                [
                    new KeyValuePair<string, int>(chartPath, -900),
                    new KeyValuePair<string, int>(Path.Combine(keySoundRoot, "zero.mc"), 0)
                ]);
            WindowsKeySoundSettingsStore.Save(normalizedSettings);
            var loadedSettings = WindowsKeySoundSettingsStore.Load();
            var removedOffset = loadedSettings.SetChartOffset(chartPath, .4);
            var temporaryFiles = Directory.GetFiles(
                isolatedSettingsRoot,
                ".key-sound-settings.json.*.tmp",
                SearchOption.TopDirectoryOnly);
            Require(
                evidence,
                loadedSettings.EnableKeySounds &&
                Math.Abs(loadedSettings.Volume - 1) < 1e-12 &&
                loadedSettings.Preset == KeySoundPreset.Custom &&
                loadedSettings.CustomFilePath != null &&
                PathsEqual(loadedSettings.CustomFilePath, noteSoundPath) &&
                loadedSettings.PlayLongNoteEndSound &&
                loadedSettings.GlobalOffsetMilliseconds == 500 &&
                loadedSettings.GetChartOffset(chartPath) == -500 &&
                removedOffset.GetChartOffset(chartPath) == 0 &&
                temporaryFiles.Length == 0,
                "windows-key-sound-settings-normalize-roundtrip-and-atomic-cleanup",
                $"enabled={loadedSettings.EnableKeySounds}; volume={loadedSettings.Volume:R}; " +
                $"preset={loadedSettings.Preset}; global={loadedSettings.GlobalOffsetMilliseconds}; " +
                $"chart={loadedSettings.GetChartOffset(chartPath)}; removed={removedOffset.GetChartOffset(chartPath)}; " +
                $"temps={temporaryFiles.Length}");

            File.WriteAllText(WindowsKeySoundSettingsStore.SettingsPath, "{not-json");
            var corruptFallback = WindowsKeySoundSettingsStore.Load();
            Require(
                evidence,
                !corruptFallback.EnableKeySounds &&
                Math.Abs(corruptFallback.Volume - WindowsKeySoundSettings.DefaultVolume) < 1e-12 &&
                corruptFallback.Preset == KeySoundPreset.Snare &&
                corruptFallback.PerChartOffsets.Count == 0,
                "windows-key-sound-settings-corruption-fails-closed-to-defaults",
                $"enabled={corruptFallback.EnableKeySounds}; volume={corruptFallback.Volume:R}; " +
                $"preset={corruptFallback.Preset}; offsets={corruptFallback.PerChartOffsets.Count}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "LUMACHART_AVALONIA_LOCAL_DATA_ROOT",
                previousSettingsRoot);
        }

        return Task.CompletedTask;
    }

    private static async Task VerifyWindowsKeySoundRuntimeP1ContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var runtimeRoot = Path.Combine(runRoot, "key-sound-runtime-p1");
        Directory.CreateDirectory(runtimeRoot);
        var chartPath = Path.Combine(runtimeRoot, "runtime.mc");
        var customPath = Path.Combine(runtimeRoot, "runtime-custom.wav");
        await File.WriteAllTextAsync(chartPath, "{}", Encoding.UTF8).ConfigureAwait(false);
        WriteKeySoundSpecWave(customPath, 660);

        var enabled = new WindowsKeySoundPlaybackOptions(
            Enabled: true,
            Preset: KeySoundPreset.Snare,
            CustomFilePath: null,
            PlayLongNoteEndSound: false,
            GlobalOffsetMilliseconds: 0,
            ChartOffsetMilliseconds: 0);

        await VerifyKeySoundPlanRuntimeConcurrencyAsync(
                evidence,
                runtimeRoot,
                chartPath,
                enabled)
            .ConfigureAwait(false);
        await VerifyKeySoundSilentClockLifecycleAsync(
                evidence,
                chartPath,
                enabled)
            .ConfigureAwait(false);
        await VerifyKeySoundSharedClockRuntimeAsync(
                evidence,
                runtimeRoot,
                chartPath,
                enabled)
            .ConfigureAwait(false);
        await VerifyKeySoundHundredThousandChordAsync(
                evidence,
                runtimeRoot,
                chartPath,
                enabled)
            .ConfigureAwait(false);
        await VerifyKeySoundPinnedPlanCacheAsync(
                evidence,
                runtimeRoot,
                chartPath,
                enabled)
            .ConfigureAwait(false);
        await VerifyKeySoundPreviewFallbackAsync(
                evidence,
                runtimeRoot,
                chartPath,
                enabled)
            .ConfigureAwait(false);
        await VerifyAudioViewModelSwitchTrackingAsync(evidence, runtimeRoot)
            .ConfigureAwait(false);
    }

    private static async Task VerifyKeySoundPlanRuntimeConcurrencyAsync(
        SliceEvidence evidence,
        string runtimeRoot,
        string chartPath,
        WindowsKeySoundPlaybackOptions enabled)
    {
        var customPath = Path.Combine(runtimeRoot, "runtime-custom.wav");
        var tab = Guid.NewGuid();
        await using var transport = new WindowsAudioTransportViewModel();
        await using var keySounds = new WindowsKeySoundService();
        SetPrivateField(keySounds, "output", new FakeWavePlayer(PlaybackState.Playing));
        await using var runtime = new WindowsKeySoundPlaybackRuntime(
            transport,
            keySounds,
            ownsKeySoundService: false);

        await runtime.RequestPlanAsync(
                tab,
                CreateKeySoundSpecDocument(Guid.NewGuid(), new BeatPosition(2, 0, 1)),
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 900,
                playbackRate: 1)
            .ConfigureAwait(false);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var beforeNoClock = runtime.Snapshot;
        var noClock = runtime.Pump();
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var afterNoClock = runtime.Snapshot;
        Require(
            evidence,
            noClock.Output == WindowsKeySoundPlaybackOutput.None &&
            noClock.Due == 0 && noClock.Accepted == 0 && noClock.Rejected == 0 &&
            afterNoClock.Counters.TriggersDue == beforeNoClock.Counters.TriggersDue &&
            afterNoClock.Counters.StandaloneAccepted == beforeNoClock.Counters.StandaloneAccepted,
            "windows-key-sound-runtime-without-clock-never-consumes-or-falls-back",
            $"output={noClock.Output}; due={afterNoClock.Counters.TriggersDue - beforeNoClock.Counters.TriggersDue}; " +
            $"accepted={afterNoClock.Counters.StandaloneAccepted - beforeNoClock.Counters.StandaloneAccepted}");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var canceledPreparation = runtime.RequestPlanAsync(
                tab,
                CreateKeySoundSpecDocument(Guid.NewGuid(), BeatPosition.Zero),
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1,
                canceled.Token);
            var immediate = runtime.Snapshot;
            await canceledPreparation.ConfigureAwait(false);
            await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
            var final = runtime.Snapshot;
            Require(
                evidence,
                !immediate.HasPlan && immediate.PlannedTabId == null &&
                !final.HasPlan && final.PlannedTabId == null && !final.IsPreparing,
                "windows-key-sound-same-tab-cancellation-clears-old-plan-and-never-resurrects",
                $"immediate={immediate.HasPlan}/{immediate.PlannedTabId}; " +
                $"final={final.HasPlan}/{final.PlannedTabId}/{final.IsPreparing}");
        }

        await runtime.RequestPlanAsync(
                tab,
                CreateKeySoundSpecDocument(Guid.NewGuid(), BeatPosition.Zero),
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        var failuresBefore = runtime.Snapshot.Counters.PlanFailures;
        var invalidDocument = new ChartDocument { Timing = null! };
        var failedPreparation = runtime.RequestPlanAsync(
            tab,
            invalidDocument,
            chartPath,
            new HashSet<int>(),
            enabled,
            currentPositionMilliseconds: 0,
            playbackRate: 1);
        var failureImmediate = runtime.Snapshot;
        await failedPreparation.ConfigureAwait(false);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var failureFinal = runtime.Snapshot;
        Require(
            evidence,
            !failureImmediate.HasPlan && failureImmediate.PlannedTabId == null &&
            !failureFinal.HasPlan && failureFinal.PlannedTabId == null &&
            failureFinal.Counters.PlanFailures == failuresBefore + 1 &&
            failureFinal.LastError != null,
            "windows-key-sound-same-tab-plan-failure-never-restores-previous-plan",
            $"immediate={failureImmediate.HasPlan}; final={failureFinal.HasPlan}; " +
            $"failures={failureFinal.Counters.PlanFailures - failuresBefore}; " +
            $"error={failureFinal.LastError?.GetType().Name ?? "none"}");

        await runtime.RequestPlanAsync(
                tab,
                CreateKeySoundSpecDocument(Guid.NewGuid(), BeatPosition.Zero),
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        var staleId = Guid.NewGuid();
        var winnerId = Guid.NewGuid();
        Task stalePreparation;
        Task winningPreparation;
        WindowsKeySoundPlaybackRuntimeSnapshot whilePreparing;
        var cacheGate = GetPrivateField<object>(keySounds, "cacheGate");
        Monitor.Enter(cacheGate);
        try
        {
            stalePreparation = runtime.RequestPlanAsync(
                tab,
                CreateKeySoundSpecDocument(staleId, new BeatPosition(1, 2, 5), "runtime-custom.wav"),
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1);
            winningPreparation = runtime.RequestPlanAsync(
                tab,
                CreateKeySoundSpecDocument(winnerId, new BeatPosition(1, 7, 10), "runtime-custom.wav"),
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1);
            whilePreparing = runtime.Snapshot;
        }
        finally
        {
            Monitor.Exit(cacheGate);
        }
        await Task.WhenAll(stalePreparation, winningPreparation).ConfigureAwait(false);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var latest = runtime.Snapshot;
        var winner = GetPrivateField<WindowsKeySoundPlaybackCoordinator>(
            runtime,
            "workerCoordinator");
        Require(
            evidence,
            File.Exists(customPath) && whilePreparing.IsPreparing && !whilePreparing.HasPlan &&
            latest.HasPlan && latest.PlannedTabId == tab && !latest.IsPreparing &&
            winner.Events.Count(item => item.NoteId == winnerId) == 1 &&
            winner.Events.All(item => item.NoteId != staleId) &&
            latest.Counters.PlansSuperseded >= 1,
            "windows-key-sound-plan-preparation-is-serialized-latest-wins-with-no-old-plan-window",
            $"preparing={whilePreparing.IsPreparing}/{whilePreparing.HasPlan}; " +
            $"winner={winner.Events.Single().NoteId}; superseded={latest.Counters.PlansSuperseded}");
    }

    private static async Task VerifyKeySoundSilentClockLifecycleAsync(
        SliceEvidence evidence,
        string chartPath,
        WindowsKeySoundPlaybackOptions enabled)
    {
        var disabled = enabled with { Enabled = false };
        var tabA = Guid.NewGuid();
        var tabB = Guid.NewGuid();
        await using var transport = new WindowsAudioTransportViewModel();
        await using var keySounds = new WindowsKeySoundService();
        SetPrivateField(keySounds, "output", new FakeWavePlayer(PlaybackState.Playing));
        await using var runtime = new WindowsKeySoundPlaybackRuntime(
            transport,
            keySounds,
            ownsKeySoundService: false);
        await runtime.RequestPlanAsync(
                tabA,
                CreateKeySoundSpecDocument(Guid.NewGuid(), BeatPosition.Zero),
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        runtime.SeekSilent(0);
        _ = await runtime.StartSilentAsync().ConfigureAwait(false);
        var scheduled = runtime.Pump();
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        runtime.InvalidatePlayback();
        var mixer = GetPrivateField<ISampleProvider>(keySounds, "mixer");
        var staleBuffer = Enumerable.Repeat(1f, 8_192).ToArray();
        var staleRead = mixer.Read(staleBuffer, 0, staleBuffer.Length);
        var previewAccepted = runtime.Preview(KeySoundPreset.Snare);
        var previewBuffer = new float[8_192];
        var previewRead = mixer.Read(previewBuffer, 0, previewBuffer.Length);
        Require(
            evidence,
            scheduled.Output == WindowsKeySoundPlaybackOutput.SilentEditorClock &&
            scheduled.Due == 0 && scheduled.Accepted == 0 &&
            staleRead == staleBuffer.Length && staleBuffer.All(value => value == 0) &&
            previewAccepted && previewRead == previewBuffer.Length &&
            previewBuffer.Any(value => Math.Abs(value) > 1e-6f),
            "windows-key-sound-runtime-discontinuity-cancels-scheduled-but-not-preview",
            $"immediate={scheduled.Due}/{scheduled.Accepted}; staleNonzero={staleBuffer.Count(value => value != 0)}; " +
            $"preview={previewAccepted}/{previewBuffer.Count(value => Math.Abs(value) > 1e-6f)}");

        await runtime.RequestPlanAsync(
                tabA,
                new ChartDocument
                {
                    Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
                },
                chartPath,
                new HashSet<int>(),
                disabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        runtime.StopSilent();
        runtime.SeekSilent(1_234.5);
        var firstStart = await runtime.StartSilentAsync().ConfigureAwait(false);
        runtime.PauseSilent();
        var paused = runtime.Snapshot;
        var stablePaused = runtime.Snapshot;
        runtime.SetSilentRate(.625);
        var rateChanged = runtime.Snapshot;
        runtime.SeekSilent(2_222.25);
        var sought = runtime.Snapshot;
        var secondStart = await runtime.StartSilentAsync().ConfigureAwait(false);
        runtime.PauseSilent();
        var resumed = runtime.Snapshot;
        Require(
            evidence,
            firstStart && secondStart &&
            paused.PositionMilliseconds >= 1_234.5 &&
            paused.PositionMilliseconds - 1_234.5 < 5_000 &&
            Math.Abs(stablePaused.PositionMilliseconds - paused.PositionMilliseconds) < 1e-9 &&
            Math.Abs(rateChanged.PositionMilliseconds - paused.PositionMilliseconds) < 1e-9 &&
            Math.Abs(rateChanged.PlaybackRate - .625) < 1e-12 &&
            Math.Abs(sought.PositionMilliseconds - 2_222.25) < 1e-9 &&
            resumed.PositionMilliseconds >= 2_222.25 &&
            resumed.PositionMilliseconds - 2_222.25 < 5_000 && !resumed.IsSilentPlaying,
            "windows-key-sound-runtime-silent-clock-seek-rate-pause-continuity",
            $"start={firstStart}/{secondStart}; paused={paused.PositionMilliseconds:R}; " +
            $"stable={stablePaused.PositionMilliseconds:R}; rate={rateChanged.PlaybackRate:R}; " +
            $"sought={sought.PositionMilliseconds:R}; resumed={resumed.PositionMilliseconds:R}");

        _ = await runtime.StartSilentAsync().ConfigureAwait(false);
        using (var canceledSwitch = new CancellationTokenSource())
        {
            canceledSwitch.Cancel();
            var switchPreparation = runtime.RequestPlanAsync(
                tabB,
                new ChartDocument
                {
                    Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
                },
                chartPath,
                new HashSet<int>(),
                disabled,
                currentPositionMilliseconds: 77,
                playbackRate: .8,
                canceledSwitch.Token);
            var immediate = runtime.Snapshot;
            await switchPreparation.ConfigureAwait(false);
            var final = runtime.Snapshot;
            Require(
                evidence,
                immediate.RequestedTabId == tabB && !immediate.HasPlan && !immediate.IsSilentPlaying &&
                final.RequestedTabId == tabB && !final.HasPlan && !final.IsSilentPlaying,
                "windows-key-sound-runtime-tab-switch-clears-plan-and-silent-clock-immediately",
                $"immediate={immediate.RequestedTabId}/{immediate.HasPlan}/{immediate.IsSilentPlaying}; " +
                $"final={final.RequestedTabId}/{final.HasPlan}/{final.IsSilentPlaying}");
        }

        await runtime.RequestPlanAsync(
                tabA,
                new ChartDocument
                {
                    Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
                },
                chartPath,
                new HashSet<int>(),
                disabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        _ = await runtime.StartSilentAsync().ConfigureAwait(false);
        runtime.Deactivate();
        var deactivated = runtime.Snapshot;
        Require(
            evidence,
            deactivated.RequestedTabId == null && deactivated.PlannedTabId == null &&
            !deactivated.HasPlan && !deactivated.IsPreparing && !deactivated.IsSilentPlaying &&
            Math.Abs(deactivated.PositionMilliseconds) < 1e-12 &&
            Math.Abs(deactivated.PlaybackRate - 1) < 1e-12,
            "windows-key-sound-runtime-deactivate-clears-plan-preparation-and-silent-clock",
            $"requested={deactivated.RequestedTabId}; planned={deactivated.PlannedTabId}; " +
            $"plan={deactivated.HasPlan}; preparing={deactivated.IsPreparing}; " +
            $"silent={deactivated.IsSilentPlaying}; position={deactivated.PositionMilliseconds:R}");
    }

    private static async Task VerifyKeySoundSharedClockRuntimeAsync(
        SliceEvidence evidence,
        string runtimeRoot,
        string chartPath,
        WindowsKeySoundPlaybackOptions enabled)
    {
        var audioPath = Path.Combine(runtimeRoot, "shared-clock.fake");
        await File.WriteAllBytesAsync(audioPath, [0x41]).ConfigureAwait(false);
        var tab = Guid.NewGuid();
        var engine = new FakeWindowsAudioDeviceEngine();
        await using var audio = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(engine),
            TimeSpan.Zero);
        await audio.LoadAsync(audioPath).ConfigureAwait(false);
        await audio.PlayAsync().ConfigureAwait(false);
        await using var transport = new WindowsAudioTransportViewModel();
        SetPrivateField(transport, "service", audio);
        SetPrivateField(transport, "serviceReady", true);
        SetPrivateField(transport, "activeTabId", (Guid?)tab);
        SetPrivateField(transport, "requestedAudioPath", audioPath);
        SetPrivateField(transport, "status", "共享音频时钟已载入");

        await using var keySounds = new WindowsKeySoundService();
        SetPrivateField(keySounds, "output", new FakeWavePlayer(PlaybackState.Playing));
        await using var runtime = new WindowsKeySoundPlaybackRuntime(
            transport,
            keySounds,
            ownsKeySoundService: false);
        await runtime.RequestPlanAsync(
                tab,
                CreateKeySoundSpecDocument(Guid.NewGuid(), BeatPosition.Zero),
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);

        var invalidatedDuringCapture = 0;
        engine.OutputLatencyReadAction = () =>
        {
            if (Interlocked.Exchange(ref invalidatedDuringCapture, 1) == 0)
                _ = audio.InvalidatePlaybackEpoch();
        };
        var positionReadsBefore = engine.PositionReadCount;
        var latencyReadsBefore = engine.OutputLatencyReadCount;
        var strictBefore = runtime.Snapshot;
        var strictPump = runtime.Pump();
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var strictAfter = runtime.Snapshot;
        Require(
            evidence,
            strictPump.Output == WindowsKeySoundPlaybackOutput.SharedMusicClock &&
            strictPump.Due == 0 && strictPump.Accepted == 0 &&
            strictAfter.Counters.TriggersDue == strictBefore.Counters.TriggersDue &&
            strictAfter.Counters.SharedAccepted == strictBefore.Counters.SharedAccepted &&
            strictAfter.Counters.StandaloneAccepted == strictBefore.Counters.StandaloneAccepted &&
            engine.AcceptedOverlayCount == 0 &&
            engine.PositionReadCount == positionReadsBefore + 1 &&
            engine.OutputLatencyReadCount == latencyReadsBefore + 1,
            "windows-key-sound-playing-snapshot-with-null-lease-is-strict-shared-and-does-not-consume",
            $"output={strictPump.Output}; due={strictAfter.Counters.TriggersDue - strictBefore.Counters.TriggersDue}; " +
            $"shared={strictAfter.Counters.SharedAccepted - strictBefore.Counters.SharedAccepted}; " +
            $"standalone={strictAfter.Counters.StandaloneAccepted - strictBefore.Counters.StandaloneAccepted}; " +
            $"reads={engine.PositionReadCount - positionReadsBefore}/{engine.OutputLatencyReadCount - latencyReadsBefore}");

        engine.OutputLatencyReadAction = null;
        var validBefore = runtime.Snapshot;
        var validPump = runtime.Pump();
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var validAfter = runtime.Snapshot;
        Require(
            evidence,
            validPump.Output == WindowsKeySoundPlaybackOutput.SharedMusicClock &&
            validPump.Due == 0 && validPump.Accepted == 0 &&
            validAfter.Counters.TriggersDue - validBefore.Counters.TriggersDue == 1 &&
            validAfter.Counters.SharedAccepted - validBefore.Counters.SharedAccepted == 1 &&
            validAfter.Counters.SubmittedVoices - validBefore.Counters.SubmittedVoices == 1 &&
            engine.AcceptedOverlayCount == 1,
            "windows-key-sound-valid-shared-lease-completes-on-single-scheduler-owner",
            $"immediate={validPump.Due}/{validPump.Accepted}; " +
            $"completed={validAfter.Counters.TriggersDue - validBefore.Counters.TriggersDue}/" +
            $"{validAfter.Counters.SharedAccepted - validBefore.Counters.SharedAccepted}; " +
            $"voices={validAfter.Counters.SubmittedVoices - validBefore.Counters.SubmittedVoices}");

        runtime.ResetPlaybackCursor(0, 1);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        engine.BlockNextOverlay();
        var mailboxBefore = runtime.Snapshot;
        var firstPumpClock = Stopwatch.StartNew();
        _ = runtime.Pump();
        firstPumpClock.Stop();
        await engine.OverlayEntered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _ = runtime.Pump();
        _ = runtime.Pump();
        var mailboxWhileBlocked = runtime.Snapshot;
        engine.ReleaseOverlay();
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var mailboxAfter = runtime.Snapshot;
        Require(
            evidence,
            firstPumpClock.Elapsed < TimeSpan.FromMilliseconds(100) &&
            mailboxWhileBlocked.Counters.ClockSamplesPublished -
                mailboxBefore.Counters.ClockSamplesPublished == 3 &&
            mailboxWhileBlocked.Counters.ClockSamplesReplaced -
                mailboxBefore.Counters.ClockSamplesReplaced >= 1 &&
            mailboxAfter.Counters.ClockSamplesProcessed -
                mailboxBefore.Counters.ClockSamplesProcessed == 2 &&
            mailboxAfter.Counters.SubmittedVoices -
                mailboxBefore.Counters.SubmittedVoices == 1,
            "windows-key-sound-clock-mailbox-capacity-one-replaces-only-clock-samples",
            $"pumpMs={firstPumpClock.Elapsed.TotalMilliseconds:R}; " +
            $"published={mailboxWhileBlocked.Counters.ClockSamplesPublished - mailboxBefore.Counters.ClockSamplesPublished}; " +
            $"replaced={mailboxWhileBlocked.Counters.ClockSamplesReplaced - mailboxBefore.Counters.ClockSamplesReplaced}; " +
            $"processed={mailboxAfter.Counters.ClockSamplesProcessed - mailboxBefore.Counters.ClockSamplesProcessed}; " +
            $"voices={mailboxAfter.Counters.SubmittedVoices - mailboxBefore.Counters.SubmittedVoices}");

        runtime.ResetPlaybackCursor(0, 1);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        engine.BlockNextOverlay();
        _ = runtime.Pump();
        await engine.OverlayEntered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        using var installRaceCancellation = new CancellationTokenSource();
        var preloadsBeforeRace = runtime.Snapshot.Counters.PreloadAccepted;
        var installRace = runtime.RequestPlanAsync(
            tab,
            CreateKeySoundSpecDocument(
                Guid.NewGuid(),
                BeatPosition.Zero,
                "runtime-custom.wav"),
            chartPath,
            new HashSet<int>(),
            enabled,
            currentPositionMilliseconds: 0,
            playbackRate: 1,
            installRaceCancellation.Token);
        var raceDeadline = Stopwatch.StartNew();
        while (runtime.Snapshot.Counters.PreloadAccepted == preloadsBeforeRace &&
               raceDeadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(1).ConfigureAwait(false);
        installRaceCancellation.Cancel();
        var waitedForActorOwnership = !installRace.IsCompleted;
        engine.ReleaseOverlay();
        await installRace.ConfigureAwait(false);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var raceFinal = runtime.Snapshot;
        var workerAfterRace = typeof(WindowsKeySoundPlaybackRuntime)
            .GetField("workerCoordinator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime);
        Require(
            evidence,
            raceDeadline.Elapsed < TimeSpan.FromSeconds(5) && waitedForActorOwnership &&
            !raceFinal.HasPlan && !raceFinal.IsPreparing &&
            workerAfterRace == null && GetKeySoundPlanPinCount(keySounds) == 0,
            "windows-key-sound-cancel-after-install-queued-transfers-then-clears-pin-ownership-on-actor",
            $"queued={raceDeadline.Elapsed.TotalMilliseconds:R}ms; awaitedActor={waitedForActorOwnership}; " +
            $"plan={raceFinal.HasPlan}/{raceFinal.IsPreparing}; worker={workerAfterRace != null}; " +
            $"pins={GetKeySoundPlanPinCount(keySounds)}");
    }

    private static async Task VerifyKeySoundHundredThousandChordAsync(
        SliceEvidence evidence,
        string runtimeRoot,
        string chartPath,
        WindowsKeySoundPlaybackOptions enabled)
    {
        const int noteCount = 100_000;
        var audioPath = Path.Combine(runtimeRoot, "hundred-thousand.fake");
        await File.WriteAllBytesAsync(audioPath, [0x42]).ConfigureAwait(false);
        var tab = Guid.NewGuid();
        var engine = new FakeWindowsAudioDeviceEngine();
        await using var audio = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(engine),
            TimeSpan.Zero);
        await audio.LoadAsync(audioPath).ConfigureAwait(false);
        await audio.PlayAsync().ConfigureAwait(false);
        await using var transport = new WindowsAudioTransportViewModel();
        SetPrivateField(transport, "service", audio);
        SetPrivateField(transport, "serviceReady", true);
        SetPrivateField(transport, "activeTabId", (Guid?)tab);
        SetPrivateField(transport, "requestedAudioPath", audioPath);
        await using var keySounds = new WindowsKeySoundService();
        await using var runtime = new WindowsKeySoundPlaybackRuntime(
            transport,
            keySounds,
            ownsKeySoundService: false);
        var document = new ChartDocument
        {
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
            Notes = Enumerable.Range(0, noteCount)
                .Select(index => new ChartNote
                {
                    Id = Guid.NewGuid(),
                    Lane = index & 3,
                    Beat = BeatPosition.Zero
                })
                .ToList()
        };
        var preparationClock = Stopwatch.StartNew();
        await runtime.RequestPlanAsync(
                tab,
                document,
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        preparationClock.Stop();
        var before = runtime.Snapshot;
        var pumpClock = Stopwatch.StartNew();
        var pump = runtime.Pump();
        pumpClock.Stop();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await runtime.WaitForSchedulerIdleAsync(deadline.Token).ConfigureAwait(false);
        var after = runtime.Snapshot;
        Require(
            evidence,
            pump.Output == WindowsKeySoundPlaybackOutput.SharedMusicClock &&
            pump.Due == 0 && pump.Accepted == 0 && pump.Rejected == 0 &&
            pumpClock.Elapsed < TimeSpan.FromMilliseconds(100) &&
            after.Counters.TriggersDue - before.Counters.TriggersDue == noteCount &&
            after.Counters.SharedAccepted - before.Counters.SharedAccepted == noteCount &&
            after.Counters.TriggersRejected - before.Counters.TriggersRejected == 0 &&
            after.Counters.SubmittedVoices - before.Counters.SubmittedVoices == 1 &&
            engine.AcceptedOverlayCount == 1,
            "windows-key-sound-100k-chord-pump-is-immediate-and-submits-one-grouped-provider",
            $"prepareMs={preparationClock.Elapsed.TotalMilliseconds:R}; pumpMs={pumpClock.Elapsed.TotalMilliseconds:R}; " +
            $"due={after.Counters.TriggersDue - before.Counters.TriggersDue}; " +
            $"accepted={after.Counters.SharedAccepted - before.Counters.SharedAccepted}; " +
            $"voices={after.Counters.SubmittedVoices - before.Counters.SubmittedVoices}; " +
            $"overlays={engine.AcceptedOverlayCount}");

        var staleEpoch = audio.CapturePlaybackEpoch();
        _ = audio.InvalidatePlaybackEpoch();
        var staleTrigger = new WindowsKeySoundPlaybackTrigger(
            Guid.Empty,
            IsLongNoteEnd: false,
            EventMilliseconds: 0,
            KeySoundPreset.Custom,
            CustomFilePath: null,
            Gain: 1,
            DelayMilliseconds: 0,
            WallClockDelayMilliseconds: 0,
            ChordVoiceCount: noteCount);
        var staleBatch = ImmutableArray.CreateRange(
            Enumerable.Repeat(staleTrigger, noteCount));
        var lastErrorBefore = keySounds.LastError;
        var staleClock = Stopwatch.StartNew();
        var staleResult = keySounds.PlayThroughBatch(audio, staleEpoch, staleBatch);
        staleClock.Stop();
        Require(
            evidence,
            staleResult.AcceptedTriggers == 0 && staleResult.RejectedTriggers == noteCount &&
            staleResult.SubmittedVoices == 0 &&
            ReferenceEquals(keySounds.LastError, lastErrorBefore) &&
            staleClock.Elapsed < TimeSpan.FromMilliseconds(100),
            "windows-key-sound-stale-100k-batch-rejects-before-sample-resolution",
            $"elapsedMs={staleClock.Elapsed.TotalMilliseconds:R}; " +
            $"result={staleResult.AcceptedTriggers}/{staleResult.RejectedTriggers}/{staleResult.SubmittedVoices}; " +
            $"errorChanged={!ReferenceEquals(keySounds.LastError, lastErrorBefore)}");
    }

    private static async Task VerifyKeySoundPinnedPlanCacheAsync(
        SliceEvidence evidence,
        string runtimeRoot,
        string chartPath,
        WindowsKeySoundPlaybackOptions enabled)
    {
        const int customCount = 34;
        const long cacheBudgetBytes = 32L * 1024 * 1024;
        var notes = new List<ChartNote>(customCount);
        for (var index = 0; index < customCount; index++)
        {
            var fileName = $"pinned-{index:D2}.wav";
            WriteKeySoundSpecWave(
                Path.Combine(runtimeRoot, fileName),
                220 + index * 7,
                durationMilliseconds: index < 11 ? 9_000 : 20);
            notes.Add(new ChartNote
            {
                Id = Guid.NewGuid(),
                Lane = index & 3,
                Beat = BeatPosition.Zero,
                Sound = fileName,
                Volume = 100
            });
        }
        var document = new ChartDocument
        {
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
            Notes = notes
        };
        var tab = Guid.NewGuid();
        await using var transport = new WindowsAudioTransportViewModel();
        await using var keySounds = new WindowsKeySoundService();
        SetPrivateField(keySounds, "output", new FakeWavePlayer(PlaybackState.Playing));
        await using var runtime = new WindowsKeySoundPlaybackRuntime(
            transport,
            keySounds,
            ownsKeySoundService: false);
        await runtime.RequestPlanAsync(
                tab,
                document,
                chartPath,
                new HashSet<int>(),
                enabled,
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        var pinnedCount = keySounds.CachedCustomSoundCount;
        var pinnedBytes = keySounds.CachedCustomSoundBytes;
        var pinnedLeases = GetKeySoundPlanPinCount(keySounds);
        runtime.SeekSilent(0);
        var started = await runtime.StartSilentAsync().ConfigureAwait(false);
        var before = runtime.Snapshot;
        var pump = runtime.Pump();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await runtime.WaitForSchedulerIdleAsync(deadline.Token).ConfigureAwait(false);
        var after = runtime.Snapshot;
        Require(
            evidence,
            pinnedCount == customCount && pinnedLeases == customCount &&
            pinnedBytes > cacheBudgetBytes && started &&
            pump.Output == WindowsKeySoundPlaybackOutput.SilentEditorClock &&
            pump.Due == 0 && pump.Accepted == 0 &&
            after.Counters.TriggersDue - before.Counters.TriggersDue == customCount &&
            after.Counters.StandaloneAccepted - before.Counters.StandaloneAccepted == customCount &&
            after.Counters.SubmittedVoices - before.Counters.SubmittedVoices == customCount,
            "windows-key-sound-active-plan-pins-more-than-32-files-and-32mib-until-played",
            $"cache={pinnedCount}/{pinnedBytes}; pins={pinnedLeases}; started={started}; " +
            $"completed={after.Counters.TriggersDue - before.Counters.TriggersDue}/" +
            $"{after.Counters.StandaloneAccepted - before.Counters.StandaloneAccepted}; " +
            $"voices={after.Counters.SubmittedVoices - before.Counters.SubmittedVoices}");

        await runtime.RequestPlanAsync(
                tab,
                new ChartDocument
                {
                    Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
                },
                chartPath,
                new HashSet<int>(),
                enabled with { Enabled = false },
                currentPositionMilliseconds: 0,
                playbackRate: 1)
            .ConfigureAwait(false);
        await runtime.WaitForSchedulerIdleAsync().ConfigureAwait(false);
        Require(
            evidence,
            GetKeySoundPlanPinCount(keySounds) == 0 &&
            keySounds.CachedCustomSoundCount <= 32 &&
            keySounds.CachedCustomSoundBytes <= cacheBudgetBytes,
            "windows-key-sound-plan-replacement-releases-pins-and-restores-cache-budgets",
            $"cache={keySounds.CachedCustomSoundCount}/{keySounds.CachedCustomSoundBytes}; " +
            $"pins={GetKeySoundPlanPinCount(keySounds)}");
    }

    private static async Task VerifyKeySoundPreviewFallbackAsync(
        SliceEvidence evidence,
        string runtimeRoot,
        string chartPath,
        WindowsKeySoundPlaybackOptions enabled)
    {
        _ = chartPath;
        _ = enabled;
        var missingPath = Path.Combine(runtimeRoot, "missing-preview.wav");
        var corruptPath = Path.Combine(runtimeRoot, "corrupt-preview.wav");
        await File.WriteAllBytesAsync(corruptPath, [0x00, 0x01, 0x02, 0x03])
            .ConfigureAwait(false);
        await using var transport = new WindowsAudioTransportViewModel();
        await using var keySounds = new WindowsKeySoundService();
        SetPrivateField(keySounds, "output", new FakeWavePlayer(PlaybackState.Playing));
        await using var runtime = new WindowsKeySoundPlaybackRuntime(
            transport,
            keySounds,
            ownsKeySoundService: false);
        var stateChanged = 0;
        runtime.StateChanged += (_, _) => stateChanged++;
        var syncNull = runtime.Preview(KeySoundPreset.Custom, null);
        var asyncNull = await runtime.PreviewAsync(KeySoundPreset.Custom, null)
            .ConfigureAwait(false);
        var asyncMissing = await runtime.PreviewAsync(KeySoundPreset.Custom, missingPath)
            .ConfigureAwait(false);
        var corruptRejected = !await runtime.PreviewAsync(KeySoundPreset.Custom, corruptPath)
            .ConfigureAwait(false);
        var mixer = GetPrivateField<ISampleProvider>(keySounds, "mixer");
        var buffer = new float[8_192];
        var read = mixer.Read(buffer, 0, buffer.Length);
        var snapshot = runtime.Snapshot;
        Require(
            evidence,
            syncNull && asyncNull && asyncMissing && corruptRejected &&
            read == buffer.Length && buffer.Any(value => Math.Abs(value) > 1e-6f) &&
            snapshot.Counters.PreviewAccepted == 3 &&
            snapshot.Counters.PreviewRejected == 1 &&
            stateChanged == 4,
            "windows-key-sound-custom-preview-null-or-missing-falls-back-to-snare-but-corrupt-existing-rejects",
            $"accepted={syncNull}/{asyncNull}/{asyncMissing}; corruptRejected={corruptRejected}; " +
            $"audio={buffer.Count(value => Math.Abs(value) > 1e-6f)}; " +
            $"counters={snapshot.Counters.PreviewAccepted}/{snapshot.Counters.PreviewRejected}; " +
            $"stateChanged={stateChanged}");
    }

    private static async Task VerifyAudioViewModelSwitchTrackingAsync(
        SliceEvidence evidence,
        string runtimeRoot)
    {
        var controller = new AvaloniaWorkspaceController(
            Path.Combine(runtimeRoot, "view-model-library"),
            Path.Combine(runtimeRoot, "view-model-editing"),
            "KeySoundSwitchTrackingSpecs");
        await controller.InitializeAsync().ConfigureAwait(false);
        var firstTab = await controller.CreateAsync("Switch Tracking A").ConfigureAwait(false);
        var secondTab = await controller.CreateAsync("Switch Tracking B").ConfigureAwait(false);
        var audioPath = Path.Combine(runtimeRoot, "switch-tracking.fake");
        await File.WriteAllBytesAsync(audioPath, [0x43]).ConfigureAwait(false);
        var engine = new FakeWindowsAudioDeviceEngine();
        var oldService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(engine),
            TimeSpan.Zero);
        await oldService.LoadAsync(audioPath).ConfigureAwait(false);
        await oldService.PlayAsync().ConfigureAwait(false);
        engine.BlockNextPause();
        var blockedOldPause = oldService.PauseAsync();
        await engine.PauseEntered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var viewModel = new WindowsAudioTransportViewModel();
        SetPrivateField(viewModel, "service", oldService);
        SetPrivateField(viewModel, "serviceReady", true);
        SetPrivateField(viewModel, "activeTabId", (Guid?)Guid.NewGuid());
        SetPrivateField(viewModel, "requestedAudioPath", audioPath);
        viewModel.Activate(firstTab);
        viewModel.Activate(secondTab);
        var trackedBeforeDispose = GetPrivateField<HashSet<Task>>(viewModel, "switchTasks").Count;
        var dispose = viewModel.DisposeAsync().AsTask();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _ = await CaptureFailureAsync(blockedOldPause).ConfigureAwait(false);
        Require(
            evidence,
            trackedBeforeDispose >= 1 && dispose.IsCompleted &&
            engine.DisposeCount == 1 &&
            GetPrivateField<HashSet<Task>>(viewModel, "switchTasks").Count == 0,
            "windows-audio-view-model-switch-abandons-blocked-old-chain-without-stalling",
            $"tracked={trackedBeforeDispose}; completed={dispose.IsCompleted}; " +
            $"disposed={engine.DisposeCount}; remaining={GetPrivateField<HashSet<Task>>(viewModel, "switchTasks").Count}");

        var staleEngine = new FakeWindowsAudioDeviceEngine();
        var staleService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(staleEngine),
            TimeSpan.Zero);
        await staleService.LoadAsync(audioPath).ConfigureAwait(false);
        await using var staleViewModel = new WindowsAudioTransportViewModel();
        var staleTabId = Guid.NewGuid();
        SetPrivateField(staleViewModel, "service", staleService);
        SetPrivateField(staleViewModel, "activeTabId", (Guid?)staleTabId);
        SetPrivateField(staleViewModel, "requestedAudioPath", audioPath);
        SetPrivateField(staleViewModel, "status", "旧标签音频已载入");
        staleEngine.PlaybackRateFailure = new IOException("deterministic stale command failure");
        staleEngine.BlockNextPlaybackRateSet();
        var staleCommand = staleViewModel.SetPlaybackRateAsync(.75);
        await staleEngine.PlaybackRateSetEntered.WaitAsync(TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        staleViewModel.Activate(firstTab);
        var winnerStatus = staleViewModel.Status;
        staleEngine.ReleasePlaybackRateSet();
        await staleCommand.ConfigureAwait(false);
        Require(
            evidence,
            staleViewModel.ActiveTabId == firstTab.Id &&
            string.Equals(staleViewModel.Status, winnerStatus, StringComparison.Ordinal) &&
            string.Equals(winnerStatus, "当前谱面没有引用音频", StringComparison.Ordinal) &&
            staleViewModel.LastError == null,
            "windows-audio-view-model-stale-command-continuation-cannot-overwrite-new-tab",
            $"active={staleViewModel.ActiveTabId}; winner={firstTab.Id}; " +
            $"status={staleViewModel.Status}; error={staleViewModel.LastError?.GetType().Name ?? "none"}");
    }

    private static async Task VerifyWindowsKeySoundRuntimeContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var runtimeRoot = Path.Combine(runRoot, "key-sound-runtime");
        Directory.CreateDirectory(runtimeRoot);
        var chartPath = Path.Combine(runtimeRoot, "runtime.mc");
        var customPath = Path.Combine(runtimeRoot, "runtime-custom.wav");
        await File.WriteAllTextAsync(chartPath, "{}", Encoding.UTF8).ConfigureAwait(false);
        const int sampleRate = 44_100;
        using (var writer = new WaveFileWriter(customPath, new WaveFormat(sampleRate, 16, 1)))
        {
            var pcm = new byte[sampleRate / 20 * 2];
            for (var sample = 0; sample < pcm.Length / 2; sample++)
            {
                var value = (short)Math.Round(
                    Math.Sin(2 * Math.PI * 660 * sample / sampleRate) * 8_000);
                pcm[sample * 2] = (byte)(value & 0xff);
                pcm[sample * 2 + 1] = (byte)((value >> 8) & 0xff);
            }
            writer.Write(pcm, 0, pcm.Length);
        }

        static ChartDocument OneNoteDocument(
            Guid id,
            BeatPosition beat,
            string? sound = null) => new()
        {
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
            Notes =
            [
                new ChartNote
                {
                    Id = id,
                    Lane = 0,
                    Beat = beat,
                    Sound = sound,
                    Volume = 100
                }
            ]
        };

        static ChartDocument EmptyDocument() => new()
        {
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
        };

        var enabled = new WindowsKeySoundPlaybackOptions(
            Enabled: true,
            Preset: KeySoundPreset.Snare,
            CustomFilePath: null,
            PlayLongNoteEndSound: false,
            GlobalOffsetMilliseconds: 0,
            ChartOffsetMilliseconds: 0);
        var disabled = enabled with { Enabled = false };
        var tabA = Guid.NewGuid();
        var tabB = Guid.NewGuid();
        var oldNoteId = Guid.NewGuid();
        var staleNoteId = Guid.NewGuid();
        var winnerNoteId = Guid.NewGuid();
        var transport = new WindowsAudioTransportViewModel();
        await using var keySounds = new WindowsKeySoundService();
        var fakeOutput = new FakeWavePlayer(PlaybackState.Playing);
        SetPrivateField(keySounds, "output", fakeOutput);

        await using (var runtime = new WindowsKeySoundPlaybackRuntime(
                         transport,
                         keySounds,
                         ownsKeySoundService: false))
        {
            await runtime.RequestPlanAsync(
                    tabA,
                    OneNoteDocument(oldNoteId, new BeatPosition(2, 0, 1)),
                    chartPath,
                    new HashSet<int>(),
                    enabled,
                    currentPositionMilliseconds: 900,
                    playbackRate: 1)
                .ConfigureAwait(false);
            runtime.ResetPlaybackCursor(900, 1);
            var coordinatorBeforeUnavailablePump = GetPrivateField<WindowsKeySoundPlaybackCoordinator>(
                runtime,
                "coordinator");
            var unavailablePump = runtime.Pump();
            var unavailableSnapshot = runtime.Snapshot;
            Require(
                evidence,
                unavailablePump.Output == WindowsKeySoundPlaybackOutput.None &&
                unavailablePump.Due == 0 && unavailablePump.Accepted == 0 &&
                coordinatorBeforeUnavailablePump.NextEventIndex == 0 &&
                unavailableSnapshot.Counters.TriggersDue == 0 &&
                unavailableSnapshot.Counters.StandaloneAccepted == 0,
                "windows-key-sound-runtime-no-clock-does-not-fallback-or-consume-due",
                $"output={unavailablePump.Output}; due={unavailablePump.Due}; " +
                $"index={coordinatorBeforeUnavailablePump.NextEventIndex}; " +
                $"standalone={unavailableSnapshot.Counters.StandaloneAccepted}");

            runtime.SeekSilent(900);
            var silentStarted = await runtime.StartSilentAsync().ConfigureAwait(false);
            var cacheGate = GetPrivateField<object>(keySounds, "cacheGate");
            Task stalePreparation;
            Task winningPreparation;
            WindowsKeySoundPlaybackCoordinator oldCoordinatorDuringPreparation;
            WindowsKeySoundPlaybackPumpResult preparationPump;
            WindowsKeySoundPlaybackRuntimeSnapshot preparingSnapshot;
            Monitor.Enter(cacheGate);
            try
            {
                stalePreparation = runtime.RequestPlanAsync(
                    tabA,
                    OneNoteDocument(staleNoteId, new BeatPosition(1, 2, 5), "runtime-custom.wav"),
                    chartPath,
                    new HashSet<int>(),
                    enabled,
                    currentPositionMilliseconds: 0,
                    playbackRate: 1);
                winningPreparation = runtime.RequestPlanAsync(
                    tabA,
                    OneNoteDocument(winnerNoteId, new BeatPosition(1, 7, 10), "runtime-custom.wav"),
                    chartPath,
                    new HashSet<int>(),
                    enabled,
                    currentPositionMilliseconds: 0,
                    playbackRate: 1);
                preparingSnapshot = runtime.Snapshot;
                oldCoordinatorDuringPreparation = GetPrivateField<WindowsKeySoundPlaybackCoordinator>(
                    runtime,
                    "coordinator");
                preparationPump = runtime.Pump();
            }
            finally
            {
                Monitor.Exit(cacheGate);
            }

            Require(
                evidence,
                silentStarted && preparingSnapshot.IsPreparing &&
                oldCoordinatorDuringPreparation.Events.Single().NoteId == oldNoteId &&
                oldCoordinatorDuringPreparation.NextEventIndex == 0 &&
                preparationPump.Output == WindowsKeySoundPlaybackOutput.SilentEditorClock &&
                preparationPump.Due == 0 && preparationPump.Accepted == 0,
                "windows-key-sound-runtime-same-tab-preparation-does-not-pump-old-plan",
                $"started={silentStarted}; preparing={preparingSnapshot.IsPreparing}; " +
                $"old={oldCoordinatorDuringPreparation.Events.Single().NoteId}; " +
                $"index={oldCoordinatorDuringPreparation.NextEventIndex}; " +
                $"output={preparationPump.Output}; due={preparationPump.Due}");

            await Task.WhenAll(stalePreparation, winningPreparation).ConfigureAwait(false);
            var winnerCoordinator = GetPrivateField<WindowsKeySoundPlaybackCoordinator>(
                runtime,
                "coordinator");
            var latestSnapshot = runtime.Snapshot;
            Require(
                evidence,
                latestSnapshot.PlannedTabId == tabA && !latestSnapshot.IsPreparing &&
                winnerCoordinator.Events.Count(item => item.NoteId == winnerNoteId) == 1 &&
                winnerCoordinator.Events.All(item => item.NoteId != staleNoteId) &&
                winnerCoordinator.NextEventIndex == 1 &&
                latestSnapshot.Counters.PlansSuperseded >= 1 &&
                latestSnapshot.Counters.PlansPublished == 2,
                "windows-key-sound-runtime-latest-wins-and-resets-at-latest-pump-sample",
                $"planned={latestSnapshot.PlannedTabId}; preparing={latestSnapshot.IsPreparing}; " +
                $"winner={winnerCoordinator.Events.Single().NoteId}; index={winnerCoordinator.NextEventIndex}; " +
                $"published={latestSnapshot.Counters.PlansPublished}; " +
                $"superseded={latestSnapshot.Counters.PlansSuperseded}");

            var scheduledNoteId = Guid.NewGuid();
            await runtime.RequestPlanAsync(
                    tabA,
                    OneNoteDocument(scheduledNoteId, BeatPosition.Zero),
                    chartPath,
                    new HashSet<int>(),
                    enabled,
                    currentPositionMilliseconds: 0,
                    playbackRate: 1)
                .ConfigureAwait(false);
            runtime.SeekSilent(0);
            var scheduledPump = runtime.Pump();
            runtime.InvalidatePlayback();
            var mixer = GetPrivateField<ISampleProvider>(keySounds, "mixer");
            var staleVoiceBuffer = Enumerable.Repeat(1f, 8_192).ToArray();
            var staleVoiceRead = mixer.Read(staleVoiceBuffer, 0, staleVoiceBuffer.Length);
            var previewAccepted = runtime.Preview(KeySoundPreset.Snare);
            var previewBuffer = new float[8_192];
            var previewRead = mixer.Read(previewBuffer, 0, previewBuffer.Length);
            Require(
                evidence,
                scheduledPump.Output == WindowsKeySoundPlaybackOutput.SilentEditorClock &&
                scheduledPump.Due == 1 && scheduledPump.Accepted == 1 &&
                staleVoiceRead == staleVoiceBuffer.Length && staleVoiceBuffer.All(value => value == 0) &&
                previewAccepted && previewRead == previewBuffer.Length &&
                previewBuffer.Any(value => Math.Abs(value) > 1e-6f) &&
                runtime.Snapshot.Counters.PreviewAccepted == 1,
                "windows-key-sound-runtime-discontinuity-cancels-scheduled-but-not-preview",
                $"pump={scheduledPump.Due}/{scheduledPump.Accepted}; " +
                $"staleRead={staleVoiceRead}; staleNonzero={staleVoiceBuffer.Count(value => value != 0)}; " +
                $"preview={previewAccepted}/{previewRead}; " +
                $"previewNonzero={previewBuffer.Count(value => Math.Abs(value) > 1e-6f)}");

            await runtime.RequestPlanAsync(
                    tabA,
                    EmptyDocument(),
                    chartPath,
                    new HashSet<int>(),
                    disabled,
                    currentPositionMilliseconds: 0,
                    playbackRate: 1)
                .ConfigureAwait(false);
            runtime.StopSilent();
            runtime.SeekSilent(1_234.5);
            var firstStart = await runtime.StartSilentAsync().ConfigureAwait(false);
            runtime.PauseSilent();
            var paused = runtime.Snapshot;
            var pausedAgain = runtime.Snapshot;
            runtime.SetSilentRate(.625);
            var rateChanged = runtime.Snapshot;
            runtime.SeekSilent(2_222.25);
            var sought = runtime.Snapshot;
            var secondStart = await runtime.StartSilentAsync().ConfigureAwait(false);
            runtime.PauseSilent();
            var resumedPause = runtime.Snapshot;
            Require(
                evidence,
                firstStart && secondStart &&
                paused.PositionMilliseconds >= 1_234.5 &&
                paused.PositionMilliseconds - 1_234.5 < 5_000 &&
                Math.Abs(pausedAgain.PositionMilliseconds - paused.PositionMilliseconds) < 1e-9 &&
                Math.Abs(rateChanged.PositionMilliseconds - paused.PositionMilliseconds) < 1e-9 &&
                Math.Abs(rateChanged.PlaybackRate - .625) < 1e-12 &&
                Math.Abs(sought.PositionMilliseconds - 2_222.25) < 1e-9 &&
                resumedPause.PositionMilliseconds >= 2_222.25 &&
                resumedPause.PositionMilliseconds - 2_222.25 < 5_000 &&
                !resumedPause.IsSilentPlaying,
                "windows-key-sound-runtime-silent-clock-seek-rate-pause-continuity",
                $"start={firstStart}/{secondStart}; paused={paused.PositionMilliseconds:R}; " +
                $"stable={pausedAgain.PositionMilliseconds:R}; rate={rateChanged.PlaybackRate:R}; " +
                $"sought={sought.PositionMilliseconds:R}; resumed={resumedPause.PositionMilliseconds:R}");

            _ = await runtime.StartSilentAsync().ConfigureAwait(false);
            using var canceledSwitch = new CancellationTokenSource();
            canceledSwitch.Cancel();
            var switchPreparation = runtime.RequestPlanAsync(
                tabB,
                EmptyDocument(),
                chartPath,
                new HashSet<int>(),
                disabled,
                currentPositionMilliseconds: 77,
                playbackRate: .8,
                canceledSwitch.Token);
            var immediateSwitch = runtime.Snapshot;
            await switchPreparation.ConfigureAwait(false);
            var canceledSwitchResult = runtime.Snapshot;
            Require(
                evidence,
                immediateSwitch.RequestedTabId == tabB && !immediateSwitch.HasPlan &&
                !immediateSwitch.IsSilentPlaying &&
                canceledSwitchResult.RequestedTabId == tabB && !canceledSwitchResult.HasPlan &&
                !canceledSwitchResult.IsSilentPlaying,
                "windows-key-sound-runtime-tab-switch-clears-plan-and-silent-clock-immediately",
                $"immediate={immediateSwitch.RequestedTabId}/{immediateSwitch.HasPlan}/" +
                $"{immediateSwitch.IsSilentPlaying}; final={canceledSwitchResult.RequestedTabId}/" +
                $"{canceledSwitchResult.HasPlan}/{canceledSwitchResult.IsSilentPlaying}");

            await runtime.RequestPlanAsync(
                    tabA,
                    EmptyDocument(),
                    chartPath,
                    new HashSet<int>(),
                    disabled,
                    currentPositionMilliseconds: 0,
                    playbackRate: 1)
                .ConfigureAwait(false);
            _ = await runtime.StartSilentAsync().ConfigureAwait(false);
            runtime.Deactivate();
            var deactivated = runtime.Snapshot;
            Require(
                evidence,
                deactivated.RequestedTabId == null && deactivated.PlannedTabId == null &&
                !deactivated.HasPlan && !deactivated.IsPreparing && !deactivated.IsSilentPlaying &&
                Math.Abs(deactivated.PositionMilliseconds) < 1e-12 &&
                Math.Abs(deactivated.PlaybackRate - 1) < 1e-12,
                "windows-key-sound-runtime-deactivate-clears-plan-preparation-and-silent-clock",
                $"requested={deactivated.RequestedTabId}; planned={deactivated.PlannedTabId}; " +
                $"plan={deactivated.HasPlan}; preparing={deactivated.IsPreparing}; " +
                $"silent={deactivated.IsSilentPlaying}; position={deactivated.PositionMilliseconds:R}");
        }
        await transport.DisposeAsync().ConfigureAwait(false);

        var viewModelRoot = Path.Combine(runtimeRoot, "view-model-generation");
        var viewModelLibrary = Path.Combine(viewModelRoot, "library");
        var viewModelEditing = Path.Combine(viewModelRoot, "editing");
        var viewModelController = new AvaloniaWorkspaceController(
            viewModelLibrary,
            viewModelEditing,
            "KeySoundRuntimeSpecs");
        await viewModelController.InitializeAsync().ConfigureAwait(false);
        var winnerTab = await viewModelController.CreateAsync("Audio Generation Winner")
            .ConfigureAwait(false);
        var staleAudioPath = Path.Combine(viewModelRoot, "stale.fake");
        await File.WriteAllBytesAsync(staleAudioPath, [0x41]).ConfigureAwait(false);
        var staleEngine = new FakeWindowsAudioDeviceEngine();
        var staleService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(staleEngine),
            TimeSpan.Zero);
        await staleService.LoadAsync(staleAudioPath).ConfigureAwait(false);
        await using (var viewModel = new WindowsAudioTransportViewModel())
        {
            var staleTabId = Guid.NewGuid();
            SetPrivateField(viewModel, "service", staleService);
            SetPrivateField(viewModel, "activeTabId", (Guid?)staleTabId);
            SetPrivateField(viewModel, "requestedAudioPath", staleAudioPath);
            SetPrivateField(viewModel, "status", "旧标签音频已载入");
            var expectedFailure = new IOException("deterministic stale command failure");
            staleEngine.PlaybackRateFailure = expectedFailure;
            staleEngine.BlockNextPlaybackRateSet();
            var staleCommand = viewModel.SetPlaybackRateAsync(.75);
            await staleEngine.PlaybackRateSetEntered
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            viewModel.Activate(winnerTab);
            var winnerStatus = viewModel.Status;
            staleEngine.ReleasePlaybackRateSet();
            await staleCommand.ConfigureAwait(false);
            Require(
                evidence,
                viewModel.ActiveTabId == winnerTab.Id &&
                string.Equals(viewModel.Status, winnerStatus, StringComparison.Ordinal) &&
                string.Equals(winnerStatus, "当前谱面没有引用音频", StringComparison.Ordinal) &&
                viewModel.LastError == null &&
                staleEngine.DisposeCount <= 1,
                "windows-audio-view-model-stale-command-continuation-cannot-overwrite-new-tab",
                $"active={viewModel.ActiveTabId}; winner={winnerTab.Id}; " +
                $"status={viewModel.Status}; error={viewModel.LastError?.GetType().Name ?? "none"}; " +
                $"oldDispose={staleEngine.DisposeCount}");
        }
    }

    private static async Task VerifyAudioPlaybackEpochContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var firstEpoch = new AudioPlaybackEpoch();
        var secondEpoch = new AudioPlaybackEpoch();
        var firstToken = firstEpoch.Capture();
        var secondToken = secondEpoch.Capture();
        var advancedFirstToken = firstEpoch.Advance();
        Require(
            evidence,
            !firstEpoch.IsCurrent(firstToken) &&
            firstEpoch.IsCurrent(advancedFirstToken) &&
            secondEpoch.IsCurrent(secondToken) &&
            !firstEpoch.IsCurrent(secondToken) &&
            !secondEpoch.IsCurrent(advancedFirstToken) &&
            advancedFirstToken != secondToken,
            "windows-audio-epoch-token-is-owner-bound-and-monotonic",
            $"firstOld={firstEpoch.IsCurrent(firstToken)}; " +
            $"firstNew={firstEpoch.IsCurrent(advancedFirstToken)}; " +
            $"foreign={firstEpoch.IsCurrent(secondToken)}");

        var beforeReadEpoch = new AudioPlaybackEpoch();
        var beforeReadToken = beforeReadEpoch.Capture();
        var unreadSource = new DeterministicSampleProvider(.25f);
        var invalidBeforeRead = new EpochGuardSampleProvider(unreadSource, beforeReadToken);
        _ = beforeReadEpoch.Advance();
        var beforeReadBuffer = Enumerable.Repeat(1f, 16).ToArray();
        var invalidBeforeReadCount = invalidBeforeRead.Read(
            beforeReadBuffer,
            0,
            beforeReadBuffer.Length);
        Require(
            evidence,
            invalidBeforeReadCount == 0 &&
            unreadSource.ReadCount == 0 &&
            beforeReadBuffer.All(value => value == 0),
            "windows-audio-epoch-guard-invalid-before-read-clears-without-source-read",
            $"returned={invalidBeforeReadCount}; sourceReads={unreadSource.ReadCount}; " +
            $"nonzero={beforeReadBuffer.Count(value => value != 0)}");

        var duringReadEpoch = new AudioPlaybackEpoch();
        var duringReadToken = duringReadEpoch.Capture();
        var invalidatingSource = new DeterministicSampleProvider(
            .5f,
            () => { _ = duringReadEpoch.Advance(); });
        var invalidDuringRead = new EpochGuardSampleProvider(invalidatingSource, duringReadToken);
        var duringReadBuffer = Enumerable.Repeat(1f, 16).ToArray();
        var invalidDuringReadCount = invalidDuringRead.Read(
            duringReadBuffer,
            0,
            duringReadBuffer.Length);
        Require(
            evidence,
            invalidDuringReadCount == 0 &&
            invalidatingSource.ReadCount == 1 &&
            duringReadBuffer.All(value => value == 0),
            "windows-audio-epoch-guard-invalid-during-read-clears-returned-buffer",
            $"returned={invalidDuringReadCount}; sourceReads={invalidatingSource.ReadCount}; " +
            $"nonzero={duringReadBuffer.Count(value => value != 0)}");

        var audioRoot = Path.Combine(runRoot, "fake-audio-epoch");
        Directory.CreateDirectory(audioRoot);
        var audioPath = Path.Combine(audioRoot, "epoch.fake");
        await File.WriteAllBytesAsync(audioPath, [0x45, 0x50, 0x4f, 0x43, 0x48])
            .ConfigureAwait(false);

        var boundaryEngine = new FakeWindowsAudioDeviceEngine();
        await using (var boundaryService = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(boundaryEngine),
                         TimeSpan.Zero))
        {
            await boundaryService.LoadAsync(audioPath).ConfigureAwait(false);
            await boundaryService.PlayAsync().ConfigureAwait(false);
            await VerifyPlaybackEpochBoundaryAsync(
                    evidence,
                    boundaryService,
                    boundaryEngine,
                    "windows-audio-overlay-stop-invalidates-captured-provider",
                    () => boundaryService.StopAsync())
                .ConfigureAwait(false);

            await boundaryService.PlayAsync().ConfigureAwait(false);
            await VerifyPlaybackEpochBoundaryAsync(
                    evidence,
                    boundaryService,
                    boundaryEngine,
                    "windows-audio-overlay-seek-invalidates-captured-provider",
                    () => boundaryService.SeekAsync(TimeSpan.FromSeconds(4)))
                .ConfigureAwait(false);

            await VerifyPlaybackEpochBoundaryAsync(
                    evidence,
                    boundaryService,
                    boundaryEngine,
                    "windows-audio-overlay-rate-invalidates-captured-provider",
                    () => boundaryService.SetPlaybackRateAsync(.75))
                .ConfigureAwait(false);

            await VerifyPlaybackEpochBoundaryAsync(
                    evidence,
                    boundaryService,
                    boundaryEngine,
                    "windows-audio-overlay-pause-invalidates-captured-provider",
                    () => boundaryService.PauseAsync())
                .ConfigureAwait(false);

            await boundaryService.PlayAsync().ConfigureAwait(false);
            var volumeSource = new DeterministicSampleProvider(.375f);
            var volumeToken = boundaryService.CapturePlaybackEpoch();
            var volumeAccepted = boundaryService.TryPlayOverlay(volumeSource, volumeToken);
            var volumeProvider = boundaryEngine.LastAcceptedOverlayProvider;
            await boundaryService.SetVolumeAsync(.4f).ConfigureAwait(false);
            var volumeBuffer = Enumerable.Repeat(1f, 12).ToArray();
            var volumeRead = volumeProvider?.Read(volumeBuffer, 0, volumeBuffer.Length) ?? -1;
            var sameTokenAccepted = boundaryService.TryPlayOverlay(
                new DeterministicSampleProvider(.125f),
                volumeToken);
            Require(
                evidence,
                volumeAccepted &&
                boundaryService.IsPlaybackEpochCurrent(volumeToken) &&
                volumeRead == volumeBuffer.Length &&
                volumeBuffer.All(value => Math.Abs(value - .375f) < 1e-6f) &&
                volumeSource.ReadCount == 1 &&
                sameTokenAccepted &&
                Math.Abs(boundaryService.Volume - .4f) < 1e-6f,
                "windows-audio-overlay-volume-preserves-captured-provider",
                $"accepted={volumeAccepted}/{sameTokenAccepted}; current={boundaryService.IsPlaybackEpochCurrent(volumeToken)}; " +
                $"read={volumeRead}; sourceReads={volumeSource.ReadCount}; volume={boundaryService.Volume:R}");
        }

        var ownerEngine = new FakeWindowsAudioDeviceEngine();
        var foreignEngine = new FakeWindowsAudioDeviceEngine();
        await using (var ownerService = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(ownerEngine),
                         TimeSpan.Zero))
        await using (var foreignService = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(foreignEngine),
                         TimeSpan.Zero))
        {
            await ownerService.LoadAsync(audioPath).ConfigureAwait(false);
            await foreignService.LoadAsync(audioPath).ConfigureAwait(false);
            await ownerService.PlayAsync().ConfigureAwait(false);
            await foreignService.PlayAsync().ConfigureAwait(false);
            var ownerToken = ownerService.CapturePlaybackEpoch();
            var ownAccepted = ownerService.TryPlayOverlay(
                new DeterministicSampleProvider(.25f),
                ownerToken);
            var foreignRejected = !foreignService.TryPlayOverlay(
                new DeterministicSampleProvider(.25f),
                ownerToken);
            Require(
                evidence,
                ownAccepted &&
                foreignRejected &&
                ownerEngine.AcceptedOverlayCount == 1 &&
                foreignEngine.AcceptedOverlayCount == 0 &&
                ownerService.IsPlaybackEpochCurrent(ownerToken) &&
                !foreignService.IsPlaybackEpochCurrent(ownerToken),
                "windows-audio-overlay-rejects-token-from-different-service",
                $"own={ownAccepted}; foreignRejected={foreignRejected}; " +
                $"accepted={ownerEngine.AcceptedOverlayCount}/{foreignEngine.AcceptedOverlayCount}");
        }

        var disposeEngine = new FakeWindowsAudioDeviceEngine();
        var disposeService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(disposeEngine),
            TimeSpan.Zero);
        await disposeService.LoadAsync(audioPath).ConfigureAwait(false);
        await disposeService.PlayAsync().ConfigureAwait(false);
        var disposeSource = new DeterministicSampleProvider(.625f);
        var disposeToken = disposeService.CapturePlaybackEpoch();
        var disposeAccepted = disposeService.TryPlayOverlay(disposeSource, disposeToken);
        var disposeProvider = disposeEngine.LastAcceptedOverlayProvider;
        await disposeService.DisposeAsync().ConfigureAwait(false);
        var disposedBuffer = Enumerable.Repeat(1f, 12).ToArray();
        var disposedRead = disposeProvider?.Read(disposedBuffer, 0, disposedBuffer.Length) ?? -1;
        var disposedResubmitRejected = !disposeService.TryPlayOverlay(
            new DeterministicSampleProvider(.25f),
            disposeToken);
        Require(
            evidence,
            disposeAccepted &&
            !disposeService.IsPlaybackEpochCurrent(disposeToken) &&
            disposedRead == 0 &&
            disposedBuffer.All(value => value == 0) &&
            disposeSource.ReadCount == 0 &&
            disposedResubmitRejected,
            "windows-audio-dispose-invalidates-captured-overlay",
            $"accepted={disposeAccepted}; current={disposeService.IsPlaybackEpochCurrent(disposeToken)}; " +
            $"read={disposedRead}; sourceReads={disposeSource.ReadCount}; resubmitRejected={disposedResubmitRejected}");

        var notificationEngine = new FakeWindowsAudioDeviceEngine();
        await using (var notificationService = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(notificationEngine),
                         TimeSpan.Zero))
        {
            await notificationService.LoadAsync(audioPath).ConfigureAwait(false);
            var expectedPrevious = notificationService.Snapshot;
            var notificationStates = new List<WindowsAudioPlaybackState>();
            var notificationChainValid = true;
            var committedStateVisible = true;
            notificationService.StateChanged += (_, eventArgs) =>
            {
                var observed = notificationService.Snapshot;
                notificationChainValid &= eventArgs.Previous == expectedPrevious;
                committedStateVisible &=
                    observed.State == eventArgs.Current.State &&
                    observed.SourcePath == eventArgs.Current.SourcePath &&
                    Math.Abs(observed.PlaybackRate - eventArgs.Current.PlaybackRate) < 1e-12 &&
                    Math.Abs(observed.Volume - eventArgs.Current.Volume) < 1e-6f &&
                    notificationEngine.IsPlaying == eventArgs.Current.IsPlaying &&
                    Math.Abs(notificationEngine.PlaybackRate - eventArgs.Current.PlaybackRate) < 1e-12 &&
                    Math.Abs(notificationEngine.Volume - eventArgs.Current.Volume) < 1e-6f;
                notificationStates.Add(eventArgs.Current.State);
                expectedPrevious = eventArgs.Current;
            };

            await notificationService.PlayAsync().ConfigureAwait(false);
            await notificationService.SetPlaybackRateAsync(.65).ConfigureAwait(false);
            await notificationService.SetVolumeAsync(.35f).ConfigureAwait(false);
            await notificationService.PauseAsync().ConfigureAwait(false);
            var expectedStates = new[]
            {
                WindowsAudioPlaybackState.Playing,
                WindowsAudioPlaybackState.Playing,
                WindowsAudioPlaybackState.Playing,
                WindowsAudioPlaybackState.Paused
            };
            Require(
                evidence,
                notificationChainValid &&
                committedStateVisible &&
                notificationStates.SequenceEqual(expectedStates) &&
                notificationService.Snapshot.State == WindowsAudioPlaybackState.Paused &&
                Math.Abs(notificationService.Snapshot.PlaybackRate - .65) < 1e-12 &&
                Math.Abs(notificationService.Snapshot.Volume - .35f) < 1e-6f,
                "windows-audio-state-notifications-observe-atomic-committed-snapshot",
                $"chain={notificationChainValid}; committed={committedStateVisible}; " +
                $"states={string.Join(',', notificationStates)}; " +
                $"rate={notificationService.Snapshot.PlaybackRate:R}; volume={notificationService.Snapshot.Volume:R}");
        }
    }

    private static async Task VerifyPlaybackEpochBoundaryAsync(
        SliceEvidence evidence,
        WindowsAudioPlaybackService service,
        FakeWindowsAudioDeviceEngine engine,
        string checkName,
        Func<Task> boundary)
    {
        var source = new DeterministicSampleProvider(.25f);
        var token = service.CapturePlaybackEpoch();
        var accepted = service.TryPlayOverlay(source, token);
        var guardedProvider = engine.LastAcceptedOverlayProvider;
        var beforeBuffer = Enumerable.Repeat(1f, 12).ToArray();
        var beforeRead = guardedProvider?.Read(beforeBuffer, 0, beforeBuffer.Length) ?? -1;

        await boundary().ConfigureAwait(false);

        var afterBuffer = Enumerable.Repeat(1f, 12).ToArray();
        var afterRead = guardedProvider?.Read(afterBuffer, 0, afterBuffer.Length) ?? -1;
        var resubmitRejected = !service.TryPlayOverlay(
            new DeterministicSampleProvider(.125f),
            token);
        Require(
            evidence,
            accepted &&
            beforeRead == beforeBuffer.Length &&
            beforeBuffer.All(value => Math.Abs(value - .25f) < 1e-6f) &&
            source.ReadCount == 1 &&
            !service.IsPlaybackEpochCurrent(token) &&
            afterRead == 0 &&
            afterBuffer.All(value => value == 0) &&
            resubmitRejected,
            checkName,
            $"accepted={accepted}; before={beforeRead}; after={afterRead}; " +
            $"sourceReads={source.ReadCount}; current={service.IsPlaybackEpochCurrent(token)}; " +
            $"resubmitRejected={resubmitRejected}");
    }

    private static async Task VerifyWindowsAudioTransportContractsAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var audioRoot = Path.Combine(runRoot, "fake-audio-transport");
        Directory.CreateDirectory(audioRoot);
        var initialPath = Path.Combine(audioRoot, "initial.fake");
        var supersededPath = Path.Combine(audioRoot, "superseded.fake");
        var winnerPath = Path.Combine(audioRoot, "winner.fake");
        await File.WriteAllBytesAsync(initialPath, [0x01]).ConfigureAwait(false);
        await File.WriteAllBytesAsync(supersededPath, [0x02]).ConfigureAwait(false);
        await File.WriteAllBytesAsync(winnerPath, [0x03]).ConfigureAwait(false);

        var stalledRecoveryEngine = new FakeWindowsAudioDeviceEngine(blockLoad: true);
        var recoveredEngine = new FakeWindowsAudioDeviceEngine();
        var recoveryServices = new[]
        {
            new WindowsAudioPlaybackService(
                CreateFakeEngineFactory(stalledRecoveryEngine),
                TimeSpan.Zero),
            new WindowsAudioPlaybackService(
                CreateFakeEngineFactory(recoveredEngine),
                TimeSpan.Zero)
        };
        var recoveryServiceIndex = -1;
        await using (var recoveringTransport = new WindowsAudioTransportViewModel(
                         () => recoveryServices[Interlocked.Increment(ref recoveryServiceIndex)],
                         TimeSpan.Zero,
                         TimeSpan.FromMilliseconds(120)))
        {
            var recoveryTab = CreateAudioTransportSpecTab(
                Path.Combine(audioRoot, "self-recovering.mc"),
                initialPath);
            recoveringTransport.Activate(recoveryTab);
            await stalledRecoveryEngine.LoadEntered
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            await WaitForPredicateAsync(
                    () => recoveringTransport.LastError is TimeoutException,
                    "bounded audio load timeout",
                    timeoutMilliseconds: 2_000,
                    diagnostics: () =>
                        $"status={recoveringTransport.Status}; " +
                        $"error={recoveringTransport.LastError?.GetType().Name ?? "none"}")
                .ConfigureAwait(false);
            var recovered = await recoveringTransport.EnsureAudioReadyAsync(recoveryTab)
                .ConfigureAwait(false);
            await recoveringTransport.TogglePlayPauseAsync().ConfigureAwait(false);
            Require(
                evidence,
                recovered &&
                recoveryServiceIndex == 1 &&
                stalledRecoveryEngine.DisposeCount == 1 &&
                recoveringTransport.IsLoaded &&
                recoveringTransport.IsPlaying &&
                recoveringTransport.LastError == null,
                "windows-audio-stalled-load-times-out-and-play-click-can-self-recover",
                $"recovered={recovered}; services={recoveryServiceIndex + 1}; " +
                $"stalledDisposed={stalledRecoveryEngine.DisposeCount}; " +
                $"state={recoveringTransport.PlaybackSnapshot?.State}; " +
                $"error={recoveringTransport.LastError?.GetType().Name ?? "none"}");
        }

        var initialEngine = new FakeWindowsAudioDeviceEngine();
        var supersededEngine = new FakeWindowsAudioDeviceEngine(blockLoad: true);
        var winnerEngine = new FakeWindowsAudioDeviceEngine();
        await using (var service = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(initialEngine, supersededEngine, winnerEngine),
                         TimeSpan.Zero))
        {
            await service.LoadAsync(initialPath).ConfigureAwait(false);
            var stalePlaybackStopped = initialEngine.CapturePlaybackStoppedCallback();
            var forwardedPlaybackStopped = 0;
            service.PlaybackStopped += (_, _) => Interlocked.Increment(ref forwardedPlaybackStopped);

            var supersededLoad = service.LoadAsync(supersededPath);
            await supersededEngine.LoadEntered.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var winnerLoad = service.LoadAsync(winnerPath);
            supersededEngine.ReleaseLoad();
            var supersededFailure = await CaptureFailureAsync(supersededLoad).ConfigureAwait(false);
            await winnerLoad.ConfigureAwait(false);

            var latestSnapshot = service.Snapshot;
            Require(
                evidence,
                supersededFailure is OperationCanceledException &&
                latestSnapshot.State == WindowsAudioPlaybackState.Stopped &&
                latestSnapshot.SourcePath != null &&
                PathsEqual(latestSnapshot.SourcePath, winnerPath) &&
                initialEngine.DisposeCount == 1 &&
                supersededEngine.DisposeCount == 1 &&
                winnerEngine.DisposeCount == 0,
                "windows-audio-load-latest-wins-with-staged-engine-cleanup",
                $"superseded={supersededFailure?.GetType().Name ?? "none"}; " +
                $"source={latestSnapshot.SourcePath}; state={latestSnapshot.State}; " +
                $"dispose={initialEngine.DisposeCount}/{supersededEngine.DisposeCount}/{winnerEngine.DisposeCount}");

            stalePlaybackStopped();
            var afterStaleCallback = service.Snapshot;
            Require(
                evidence,
                forwardedPlaybackStopped == 0 &&
                afterStaleCallback.SourcePath != null &&
                PathsEqual(afterStaleCallback.SourcePath, winnerPath) &&
                afterStaleCallback.State == WindowsAudioPlaybackState.Stopped,
                "windows-audio-stale-engine-callback-cannot-overwrite-winner",
                $"forwarded={forwardedPlaybackStopped}; source={afterStaleCallback.SourcePath}; " +
                $"state={afterStaleCallback.State}");
        }

        var replayEngine = new FakeWindowsAudioDeviceEngine();
        await using (var service = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(replayEngine),
                         TimeSpan.Zero))
        {
            await service.LoadAsync(initialPath).ConfigureAwait(false);
            await service.SeekAsync(replayEngine.Duration).ConfigureAwait(false);
            await service.PlayAsync().ConfigureAwait(false);
            var replayed = service.Snapshot;
            Require(
                evidence,
                replayed.State == WindowsAudioPlaybackState.Playing &&
                replayEngine.IsPlaying &&
                replayEngine.Position == TimeSpan.Zero &&
                replayed.Position == TimeSpan.Zero &&
                replayEngine.SeekCount == 2,
                "windows-audio-play-at-end-rewinds-before-starting",
                $"state={replayed.State}; enginePlaying={replayEngine.IsPlaying}; " +
                $"position={replayed.Position.TotalMilliseconds:F3}; " +
                $"enginePosition={replayEngine.Position.TotalMilliseconds:F3}; " +
                $"seeks={replayEngine.SeekCount}");
        }

        var immediateEngine = new FakeWindowsAudioDeviceEngine();
        await using (var service = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(immediateEngine),
                         TimeSpan.FromMilliseconds(250)))
        {
            await service.LoadAsync(initialPath).ConfigureAwait(false);
            var playTask = service.PlayAsync();
            await playTask.ConfigureAwait(false);
            var immediatePendingSeek = service.SeekAsync(TimeSpan.FromSeconds(10));
            var pauseTask = service.PauseAsync();
            var immediatePendingSeekFailure = await CaptureFailureAsync(immediatePendingSeek)
                .ConfigureAwait(false);
            await pauseTask.ConfigureAwait(false);
            var anchor = TimeSpan.FromSeconds(30);
            Require(
                evidence,
                immediatePendingSeekFailure is OperationCanceledException &&
                immediateEngine.SeekCount == 0 &&
                service.Snapshot.State == WindowsAudioPlaybackState.Paused &&
                WindowsAudioDeviceEngine.DefaultRequestedOutputLatencyMilliseconds == 40 &&
                !WindowsAudioResumePolicy.RequiresSeek(
                    anchor,
                    anchor + TimeSpan.FromMilliseconds(
                        WindowsAudioResumePolicy.MaximumPositionDriftMilliseconds)) &&
                WindowsAudioResumePolicy.RequiresSeek(
                    anchor,
                    anchor + TimeSpan.FromMilliseconds(
                        WindowsAudioResumePolicy.MaximumPositionDriftMilliseconds + .001)),
                "windows-audio-pause-cancels-pending-seek-and-resume-skips-redundant-seek",
                $"seekFailure={immediatePendingSeekFailure?.GetType().Name ?? "none"}; " +
                $"seeks={immediateEngine.SeekCount}; state={service.Snapshot.State}; " +
                $"latency={WindowsAudioDeviceEngine.DefaultRequestedOutputLatencyMilliseconds:0}ms");
        }

        var refreshEngine = new FakeWindowsAudioDeviceEngine();
        await using (var service = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(refreshEngine),
                         TimeSpan.Zero))
        {
            await service.LoadAsync(initialPath).ConfigureAwait(false);
            var sample = new SilentSampleProvider();
            var stoppedOverlayRejected = !service.TryPlayOverlay(sample);
            await service.PlayAsync().ConfigureAwait(false);
            var playingOverlayAccepted = service.TryPlayOverlay(sample);

            var expectedRefreshFailure = new IOException("deterministic fake output rebuild failure");
            refreshEngine.RefreshFailure = expectedRefreshFailure;
            var refreshFailure = await CaptureFailureAsync(
                    service.RefreshDefaultOutputDeviceAsync(resumePlayback: true))
                .ConfigureAwait(false);
            var faulted = service.Snapshot;
            var faultedOverlayRejected = !service.TryPlayOverlay(sample);
            Require(
                evidence,
                ReferenceEquals(refreshFailure, expectedRefreshFailure) &&
                faulted.State == WindowsAudioPlaybackState.Faulted &&
                ReferenceEquals(faulted.LastError, expectedRefreshFailure) &&
                !faulted.IsPlaying &&
                !refreshEngine.HasOutput,
                "windows-audio-refresh-failure-publishes-recoverable-fault",
                $"failure={refreshFailure?.GetType().Name ?? "none"}; state={faulted.State}; " +
                $"hasOutput={refreshEngine.HasOutput}; isPlaying={faulted.IsPlaying}");

            refreshEngine.RefreshFailure = null;
            await service.RefreshDefaultOutputDeviceAsync(resumePlayback: true).ConfigureAwait(false);
            var recovered = service.Snapshot;
            Require(
                evidence,
                recovered.State == WindowsAudioPlaybackState.Playing &&
                recovered.LastError == null &&
                refreshEngine.HasOutput &&
                refreshEngine.IsPlaying &&
                refreshEngine.RefreshCount == 2,
                "windows-audio-refresh-recovers-intended-playing-state",
                $"state={recovered.State}; hasOutput={refreshEngine.HasOutput}; " +
                $"enginePlaying={refreshEngine.IsPlaying}; refreshes={refreshEngine.RefreshCount}");

            await service.PauseAsync().ConfigureAwait(false);
            var pausedOverlayRejected = !service.TryPlayOverlay(sample);
            Require(
                evidence,
                stoppedOverlayRejected && playingOverlayAccepted &&
                faultedOverlayRejected && pausedOverlayRejected &&
                refreshEngine.AcceptedOverlayCount == 1,
                "windows-audio-overlay-rejects-inactive-or-missing-output",
                $"stoppedRejected={stoppedOverlayRejected}; playingAccepted={playingOverlayAccepted}; " +
                $"faultedRejected={faultedOverlayRejected}; pausedRejected={pausedOverlayRejected}; " +
                $"accepted={refreshEngine.AcceptedOverlayCount}");
        }

        var automaticEngine = new FakeWindowsAudioDeviceEngine();
        var automaticService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(automaticEngine),
            TimeSpan.Zero);
        await using (var transport = new WindowsAudioTransportViewModel(
                         () => automaticService,
                         TimeSpan.FromMilliseconds(120)))
        {
            var tab = CreateAudioTransportSpecTab(
                Path.Combine(audioRoot, "automatic-device-refresh.mc"),
                initialPath);
            transport.Activate(tab);
            await WaitForPredicateAsync(
                    () => transport.IsLoaded,
                    "automatic device refresh transport load")
                .ConfigureAwait(false);
            await transport.TogglePlayPauseAsync().ConfigureAwait(false);

            automaticEngine.LoseOutputAndRaiseDefaultOutputDeviceChanged(wasPlaying: true);
            automaticEngine.RaiseDefaultOutputDeviceChanged(wasPlaying: false);
            automaticEngine.RaiseDefaultOutputDeviceChanged(wasPlaying: false);
            await WaitForPredicateAsync(
                    () => automaticEngine.RefreshCount == 1 &&
                          transport.PlaybackSnapshot?.IsPlaying == true &&
                          transport.Status == "默认播放设备已切换，播放已继续",
                    "coalesced automatic device refresh",
                    diagnostics: () =>
                        $"refreshes={automaticEngine.RefreshCount}; " +
                        $"engine={automaticEngine.HasOutput}/{automaticEngine.IsPlaying}; " +
                        $"snapshot={transport.PlaybackSnapshot?.State}/" +
                        $"{transport.PlaybackSnapshot?.IsPlaying}; " +
                        $"status={transport.Status}; " +
                        $"error={transport.LastError?.Message ?? "none"}")
                .ConfigureAwait(false);
            Require(
                evidence,
                automaticEngine.RefreshCount == 1 &&
                automaticEngine.HasOutput &&
                automaticEngine.IsPlaying &&
                transport.PlaybackSnapshot?.State == WindowsAudioPlaybackState.Playing &&
                transport.LastError == null,
                "windows-audio-default-device-notifications-coalesce-and-auto-resume",
                $"refreshes={automaticEngine.RefreshCount}; " +
                $"engine={automaticEngine.HasOutput}/{automaticEngine.IsPlaying}; " +
                $"state={transport.PlaybackSnapshot?.State}; status={transport.Status}");

            var expectedAutomaticFailure =
                new IOException("deterministic automatic endpoint failure");
            automaticEngine.RefreshFailure = expectedAutomaticFailure;
            automaticEngine.LoseOutputAndRaiseDefaultOutputDeviceChanged(wasPlaying: true);
            await WaitForPredicateAsync(
                    () => automaticEngine.RefreshCount == 2 &&
                          ReferenceEquals(transport.LastError, expectedAutomaticFailure),
                    "automatic device refresh failure")
                .ConfigureAwait(false);
            var automaticFault = transport.PlaybackSnapshot;
            automaticEngine.RefreshFailure = null;
            automaticEngine.RaiseDefaultOutputDeviceChanged(wasPlaying: false);
            await WaitForPredicateAsync(
                    () => automaticEngine.RefreshCount == 3 &&
                          transport.LastError == null &&
                          transport.PlaybackSnapshot?.State == WindowsAudioPlaybackState.Paused &&
                          transport.Status == "默认播放设备已切换",
                    "automatic device refresh recovery")
                .ConfigureAwait(false);
            Require(
                evidence,
                automaticFault?.State == WindowsAudioPlaybackState.Faulted &&
                ReferenceEquals(automaticFault.LastError, expectedAutomaticFailure) &&
                automaticEngine.RefreshCount == 3 &&
                automaticEngine.HasOutput &&
                !automaticEngine.IsPlaying &&
                transport.PlaybackSnapshot?.State == WindowsAudioPlaybackState.Paused &&
                transport.LastError == null,
                "windows-audio-automatic-device-fault-retries-on-next-notification",
                $"fault={automaticFault?.State}/{automaticFault?.LastError?.Message}; " +
                $"refreshes={automaticEngine.RefreshCount}; " +
                $"recovered={transport.PlaybackSnapshot?.State}/{automaticEngine.HasOutput}; " +
                $"status={transport.Status}");
        }

        var interruptedEngine = new FakeWindowsAudioDeviceEngine();
        var interruptedService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(interruptedEngine),
            TimeSpan.Zero);
        await using (var transport = new WindowsAudioTransportViewModel(
                         () => interruptedService,
                         TimeSpan.FromSeconds(5)))
        {
            var tab = CreateAudioTransportSpecTab(
                Path.Combine(audioRoot, "unexpected-output-stop.mc"),
                initialPath);
            transport.Activate(tab);
            await WaitForPredicateAsync(
                    () => transport.IsLoaded,
                    "unexpected output stop transport load")
                .ConfigureAwait(false);
            await transport.TogglePlayPauseAsync().ConfigureAwait(false);

            var recoveryClock = Stopwatch.StartNew();
            interruptedEngine.LoseOutputAndRaiseUnexpectedPlaybackStop(wasPlaying: true);
            await WaitForPredicateAsync(
                    () => interruptedEngine.RefreshCount == 1 &&
                          transport.PlaybackSnapshot?.IsPlaying == true &&
                          transport.Status == "音频输出已恢复，播放已继续",
                    "immediate unexpected output stop recovery",
                    timeoutMilliseconds: 2_000)
                .ConfigureAwait(false);
            recoveryClock.Stop();
            Require(
                evidence,
                recoveryClock.Elapsed < TimeSpan.FromSeconds(2) &&
                interruptedEngine.HasOutput &&
                interruptedEngine.IsPlaying &&
                transport.LastError == null,
                "windows-audio-unexpected-stop-recovers-without-endpoint-debounce",
                $"elapsedMs={recoveryClock.Elapsed.TotalMilliseconds:F1}; " +
                $"refreshes={interruptedEngine.RefreshCount}; " +
                $"engine={interruptedEngine.HasOutput}/{interruptedEngine.IsPlaying}; " +
                $"status={transport.Status}");
        }

        var sourceEnded = false;
        var transientSource = new TransientZeroSampleProvider();
        var resilientMixer = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(
            transientSource,
            () => sourceEnded);
        var resilientBuffer = new float[8];
        var primingRead = resilientMixer.Read(resilientBuffer, 0, resilientBuffer.Length);
        Array.Clear(resilientBuffer);
        var resumedRead = resilientMixer.Read(resilientBuffer, 0, resilientBuffer.Length);
        var resumedSamplesMatch = resilientBuffer.All(
            sample => Math.Abs(sample - .25f) < .0001f);
        sourceEnded = true;
        Array.Fill(resilientBuffer, .75f);
        var terminalSilenceRead = resilientMixer.Read(
            resilientBuffer,
            0,
            resilientBuffer.Length);
        var terminalSilenceWritten = resilientBuffer.All(sample => Math.Abs(sample) < .0001f);
        var endedRead = resilientMixer.Read(resilientBuffer, 0, resilientBuffer.Length);
        Require(
            evidence,
            primingRead == resilientBuffer.Length &&
            resumedRead == resilientBuffer.Length &&
            resumedSamplesMatch &&
            terminalSilenceRead == resilientBuffer.Length &&
            terminalSilenceWritten &&
            endedRead == 0 &&
            transientSource.ReadCount == 4,
            "windows-audio-eof-flushes-one-silent-device-period",
            $"priming={primingRead}; resumed={resumedRead}; " +
            $"terminal={terminalSilenceRead}/{terminalSilenceWritten}; ended={endedRead}; " +
            $"sourceReads={transientSource.ReadCount}");

        var musicOnlyMixer = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(
            new DeterministicSampleProvider(.8f),
            static () => false);
        var musicOnlyBuffer = new float[8];
        _ = musicOnlyMixer.Read(musicOnlyBuffer, 0, musicOnlyBuffer.Length);
        var mixedLimiter = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(
            new DeterministicSampleProvider(.8f),
            static () => false);
        Require(
            evidence,
            mixedLimiter.TryEnqueue(new DeterministicSampleProvider(.8f)),
            "windows-audio-key-overlay-limiter-accepts-voice",
            "overlay accepted");
        var limitedMixBuffer = new float[8];
        _ = mixedLimiter.Read(limitedMixBuffer, 0, limitedMixBuffer.Length);
        Require(
            evidence,
            musicOnlyBuffer.All(sample => Math.Abs(sample - .8f) < .0001f) &&
            limitedMixBuffer.All(sample => sample > .9f && sample < 1f),
            "windows-audio-key-overlay-limiter-bounds-mix-without-touching-music-only",
            $"music={musicOnlyBuffer[0]:0.0000}; mixed={limitedMixBuffer[0]:0.0000}");

        var standaloneFormat = WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
        var standaloneMixer = new WindowsKeySoundService.QueuedCachedSoundMixer(
            standaloneFormat);
        var standaloneBuffer = new float[8];
        var initialSilentRead = standaloneMixer.Read(
            standaloneBuffer,
            0,
            standaloneBuffer.Length);
        var initialEofRead = standaloneMixer.Read(
            standaloneBuffer,
            0,
            standaloneBuffer.Length);
        var initialEofMarked = standaloneMixer.EndOfStreamReturned;
        var standaloneSound = new WindowsKeySoundService.CachedSound(
            [.5f, .5f, -.5f, -.5f],
            standaloneFormat);
        var firstAccepted = standaloneMixer.TryEnqueue(
            standaloneSound,
            1,
            0,
            null,
            softLimit: true);
        var firstEnqueueClearedEof = !standaloneMixer.EndOfStreamReturned;
        Array.Fill(standaloneBuffer, .75f);
        var firstVoiceRead = standaloneMixer.Read(
            standaloneBuffer,
            0,
            standaloneBuffer.Length);
        var firstVoiceTailSilent = standaloneBuffer[4..].All(
            sample => Math.Abs(sample) < .0001f);
        Array.Fill(standaloneBuffer, .75f);
        var firstTerminalRead = standaloneMixer.Read(
            standaloneBuffer,
            0,
            standaloneBuffer.Length);
        var firstTerminalSilent = standaloneBuffer.All(
            sample => Math.Abs(sample) < .0001f);
        var firstEofRead = standaloneMixer.Read(
            standaloneBuffer,
            0,
            standaloneBuffer.Length);
        var firstEofMarked = standaloneMixer.EndOfStreamReturned;
        var secondAccepted = standaloneMixer.TryEnqueue(
            standaloneSound,
            1,
            0,
            null,
            softLimit: true);
        var secondEnqueueClearedEof = !standaloneMixer.EndOfStreamReturned;
        var secondVoiceRead = standaloneMixer.Read(
            standaloneBuffer,
            0,
            standaloneBuffer.Length);
        Array.Fill(standaloneBuffer, .75f);
        var secondTerminalRead = standaloneMixer.Read(
            standaloneBuffer,
            0,
            standaloneBuffer.Length);
        var secondTerminalSilent = standaloneBuffer.All(
            sample => Math.Abs(sample) < .0001f);
        var secondEofRead = standaloneMixer.Read(
            standaloneBuffer,
            0,
            standaloneBuffer.Length);
        Require(
            evidence,
            initialSilentRead == standaloneBuffer.Length &&
            initialEofRead == 0 &&
            initialEofMarked &&
            firstAccepted &&
            firstEnqueueClearedEof &&
            firstVoiceRead == standaloneBuffer.Length &&
            firstVoiceTailSilent &&
            firstTerminalRead == standaloneBuffer.Length &&
            firstTerminalSilent &&
            firstEofRead == 0 &&
            firstEofMarked &&
            secondAccepted &&
            secondEnqueueClearedEof &&
            secondVoiceRead == standaloneBuffer.Length &&
            secondTerminalRead == standaloneBuffer.Length &&
            secondTerminalSilent &&
            secondEofRead == 0,
            "windows-key-sound-standalone-flushes-silence-and-restarts-after-eof",
            $"initial={initialSilentRead}/{initialEofRead}; " +
            $"first={firstVoiceRead}/{firstTerminalRead}/{firstEofRead}; " +
            $"second={secondVoiceRead}/{secondTerminalRead}/{secondEofRead}");

        var rateSourceEnded = false;
        var transientRateSource = new TransientZeroSampleProvider();
        var resilientRateProvider = new RateAdjustingSampleProvider(
            transientRateSource,
            () => rateSourceEnded);
        var resilientRateMixer = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(
            resilientRateProvider,
            () => rateSourceEnded);
        var resilientRateBuffer = new float[8];
        var ratePrimingRead = resilientRateMixer.Read(
            resilientRateBuffer,
            0,
            resilientRateBuffer.Length);
        Array.Clear(resilientRateBuffer);
        var rateResumedRead = resilientRateMixer.Read(
            resilientRateBuffer,
            0,
            resilientRateBuffer.Length);
        var rateResumedSamplesMatch = resilientRateBuffer.All(
            sample => Math.Abs(sample - .25f) < .0001f);
        rateSourceEnded = true;
        Array.Fill(resilientRateBuffer, .75f);
        var rateTerminalSilenceRead = resilientRateMixer.Read(
            resilientRateBuffer,
            0,
            resilientRateBuffer.Length);
        var rateTerminalSilenceWritten = resilientRateBuffer.All(
            sample => Math.Abs(sample) < .0001f);
        var rateEndedRead = resilientRateMixer.Read(
            resilientRateBuffer,
            0,
            resilientRateBuffer.Length);
        Require(
            evidence,
            ratePrimingRead == resilientRateBuffer.Length &&
            rateResumedRead == resilientRateBuffer.Length &&
            rateResumedSamplesMatch &&
            rateTerminalSilenceRead == resilientRateBuffer.Length &&
            rateTerminalSilenceWritten &&
            rateEndedRead == 0 &&
            transientRateSource.ReadCount == 4,
            "windows-audio-rate-provider-flushes-one-silent-device-period",
            $"priming={ratePrimingRead}; resumed={rateResumedRead}; " +
            $"terminal={rateTerminalSilenceRead}/{rateTerminalSilenceWritten}; " +
            $"ended={rateEndedRead}; " +
            $"sourceReads={transientRateSource.ReadCount}");

        var exactLengthSource = new DeterministicSampleProvider(.5f);
        var exactLengthProvider = new LengthCappedSampleProvider(
            exactLengthSource,
            remainingSamples: 8);
        var exactLengthBuffer = new float[8];
        var exactLengthRead = exactLengthProvider.Read(
            exactLengthBuffer,
            0,
            exactLengthBuffer.Length);
        var suppressedEofProbe = exactLengthProvider.Read(
            exactLengthBuffer,
            0,
            exactLengthBuffer.Length);
        Require(
            evidence,
            exactLengthRead == exactLengthBuffer.Length &&
            suppressedEofProbe == 0 &&
            exactLengthProvider.IsEnded &&
            exactLengthProvider.RemainingSamples == 0 &&
            exactLengthSource.ReadCount == 1,
            "windows-audio-known-pcm-length-suppresses-decoder-eof-overread",
            $"read={exactLengthRead}; eof={suppressedEofProbe}; " +
            $"remaining={exactLengthProvider.RemainingSamples}; " +
            $"sourceReads={exactLengthSource.ReadCount}");

        var canceledRefreshEngine = new FakeWindowsAudioDeviceEngine();
        var replacementRefreshEngine = new FakeWindowsAudioDeviceEngine();
        var canceledRefreshService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(canceledRefreshEngine),
            TimeSpan.Zero);
        var replacementRefreshService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(replacementRefreshEngine),
            TimeSpan.Zero);
        var refreshServices = new[] { canceledRefreshService, replacementRefreshService };
        var refreshServiceIndex = -1;
        await using (var transport = new WindowsAudioTransportViewModel(
                         () => refreshServices[Interlocked.Increment(ref refreshServiceIndex)],
                         TimeSpan.FromMilliseconds(120)))
        {
            var firstTab = CreateAudioTransportSpecTab(
                Path.Combine(audioRoot, "device-refresh-canceled.mc"),
                initialPath);
            var secondTab = CreateAudioTransportSpecTab(
                Path.Combine(audioRoot, "device-refresh-winner.mc"),
                winnerPath);
            transport.Activate(firstTab);
            await WaitForPredicateAsync(
                    () => transport.IsLoaded && transport.ActiveTabId == firstTab.Id,
                    "first device refresh tab load")
                .ConfigureAwait(false);
            await transport.TogglePlayPauseAsync().ConfigureAwait(false);
            canceledRefreshEngine.LoseOutputAndRaiseDefaultOutputDeviceChanged(wasPlaying: true);
            transport.Activate(secondTab);
            await WaitForPredicateAsync(
                    () => transport.IsLoaded &&
                          transport.ActiveTabId == secondTab.Id &&
                          PathsEqual(
                              transport.PlaybackSnapshot?.SourcePath ?? string.Empty,
                              winnerPath),
                    "replacement device refresh tab load")
                .ConfigureAwait(false);
            await Task.Delay(180).ConfigureAwait(false);
            Require(
                evidence,
                canceledRefreshEngine.RefreshCount == 0 &&
                canceledRefreshEngine.DisposeCount == 1 &&
                replacementRefreshEngine.RefreshCount == 0 &&
                transport.ActiveTabId == secondTab.Id &&
                transport.LastError == null,
                "windows-audio-tab-switch-cancels-stale-device-recovery",
                $"oldRefresh={canceledRefreshEngine.RefreshCount}; " +
                $"oldDispose={canceledRefreshEngine.DisposeCount}; " +
                $"newRefresh={replacementRefreshEngine.RefreshCount}; " +
                $"active={transport.ActiveTabId}; error={transport.LastError?.Message ?? "none"}");
        }

        var beforeGateEngine = new FakeWindowsAudioDeviceEngine();
        var beforeGateWinner = new FakeWindowsAudioDeviceEngine();
        await using (var service = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(beforeGateEngine, beforeGateWinner),
                         TimeSpan.Zero))
        {
            await service.LoadAsync(initialPath).ConfigureAwait(false);
            beforeGateEngine.BlockNextPlaybackRateSet();
            var blockingOperation = service.SetPlaybackRateAsync(.75);
            await beforeGateEngine.PlaybackRateSetEntered
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            var staleSeek = service.SeekAsync(TimeSpan.FromSeconds(11));
            var replacementLoad = service.LoadAsync(winnerPath);
            beforeGateEngine.ReleasePlaybackRateSet();
            await blockingOperation.ConfigureAwait(false);
            var seekFailure = await CaptureFailureAsync(staleSeek).ConfigureAwait(false);
            await replacementLoad.ConfigureAwait(false);
            Require(
                evidence,
                seekFailure is OperationCanceledException &&
                beforeGateEngine.SeekCount == 0 &&
                beforeGateWinner.SeekCount == 0 &&
                service.Snapshot.SourcePath != null &&
                PathsEqual(service.Snapshot.SourcePath, winnerPath),
                "windows-audio-seek-generation-change-before-gate-does-not-execute",
                $"failure={seekFailure?.GetType().Name ?? "none"}; " +
                $"seeks={beforeGateEngine.SeekCount}/{beforeGateWinner.SeekCount}; " +
                $"source={service.Snapshot.SourcePath}");
        }

        var afterGateEngine = new FakeWindowsAudioDeviceEngine();
        var afterGateWinner = new FakeWindowsAudioDeviceEngine();
        await using (var service = new WindowsAudioPlaybackService(
                         CreateFakeEngineFactory(afterGateEngine, afterGateWinner),
                         TimeSpan.Zero))
        {
            await service.LoadAsync(initialPath).ConfigureAwait(false);
            var seekEnteredGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSeek = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            service.BeforeSeekExecutionAsync = async cancellationToken =>
            {
                seekEnteredGate.TrySetResult();
                await releaseSeek.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            };
            var staleSeek = service.SeekAsync(TimeSpan.FromSeconds(17));
            await seekEnteredGate.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var replacementLoad = service.LoadAsync(winnerPath);
            releaseSeek.TrySetResult();
            var seekFailure = await CaptureFailureAsync(staleSeek).ConfigureAwait(false);
            await replacementLoad.ConfigureAwait(false);
            Require(
                evidence,
                seekFailure is OperationCanceledException &&
                afterGateEngine.SeekCount == 0 &&
                afterGateWinner.SeekCount == 0 &&
                service.Snapshot.SourcePath != null &&
                PathsEqual(service.Snapshot.SourcePath, winnerPath),
                "windows-audio-seek-generation-change-after-gate-does-not-execute",
                $"failure={seekFailure?.GetType().Name ?? "none"}; " +
                $"seeks={afterGateEngine.SeekCount}/{afterGateWinner.SeekCount}; " +
                $"source={service.Snapshot.SourcePath}");
        }

        var lifecycleEngine = new FakeWindowsAudioDeviceEngine();
        var lifecycleService = new WindowsAudioPlaybackService(
            CreateFakeEngineFactory(lifecycleEngine),
            TimeSpan.FromSeconds(1));
        await lifecycleService.LoadAsync(initialPath).ConfigureAwait(false);
        var pendingSeek = lifecycleService.SeekAsync(TimeSpan.FromSeconds(3));
        await lifecycleService.DisposeAsync().ConfigureAwait(false);
        var pendingSeekFailure = await CaptureFailureAsync(pendingSeek).ConfigureAwait(false);
        var afterDisposeFailure = await CaptureFailureAsync(lifecycleService.PlayAsync()).ConfigureAwait(false);
        await lifecycleService.DisposeAsync().ConfigureAwait(false);
        Require(
            evidence,
            lifecycleService.Snapshot.State == WindowsAudioPlaybackState.Disposed &&
            lifecycleEngine.DisposeCount == 1 &&
            pendingSeekFailure != null &&
            afterDisposeFailure is ObjectDisposedException &&
            !lifecycleService.TryPlayOverlay(new SilentSampleProvider()),
            "windows-audio-dispose-cancels-pending-work-and-is-idempotent",
            $"state={lifecycleService.Snapshot.State}; disposals={lifecycleEngine.DisposeCount}; " +
            $"seekFailure={pendingSeekFailure?.GetType().Name ?? "none"}; " +
            $"afterDispose={afterDisposeFailure?.GetType().Name ?? "none"}");
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static DesktopChartTab CreateAudioTransportSpecTab(
        string chartPath,
        string audioPath)
    {
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            Metadata = new ChartMetadata
            {
                Title = Path.GetFileNameWithoutExtension(chartPath),
                AudioFile = Path.GetFullPath(audioPath)
            },
            Timing =
            [
                new TimingEvent
                {
                    Beat = BeatPosition.Zero,
                    Bpm = 120
                }
            ]
        });
        var constructor = typeof(DesktopChartTab).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 7);
        return (DesktopChartTab)constructor.Invoke(
        [
            Guid.NewGuid(),
            session,
            Path.GetFullPath(chartPath),
            Path.GetDirectoryName(Path.GetFullPath(chartPath))!,
            Path.GetFullPath(chartPath),
            DesktopChartFormat.MalodyMc,
            OsuNoteTimingImportMode.PreserveMilliseconds
        ]);
    }

    private static async Task WaitForPredicateAsync(
        Func<bool> predicate,
        string description,
        int timeoutMilliseconds = 5_000,
        Func<string>? diagnostics = null)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timeout.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                var details = diagnostics?.Invoke();
                throw new TimeoutException(
                    string.IsNullOrWhiteSpace(details)
                        ? $"Timed out waiting for {description}."
                        : $"Timed out waiting for {description}: {details}");
            }
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static ChartDocument CreateKeySoundSpecDocument(
        Guid id,
        BeatPosition beat,
        string? sound = null) => new()
    {
        Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
        Notes =
        [
            new ChartNote
            {
                Id = id,
                Lane = 0,
                Beat = beat,
                Sound = sound,
                Volume = 100
            }
        ]
    };

    private static void WriteKeySoundSpecWave(
        string path,
        int frequency,
        int durationMilliseconds = 50)
    {
        const int sampleRate = 44_100;
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));
        var sampleCount = Math.Max(1, sampleRate * durationMilliseconds / 1_000);
        var pcm = new byte[sampleCount * 2];
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var value = (short)Math.Round(
                Math.Sin(2 * Math.PI * frequency * sample / sampleRate) * 8_000);
            pcm[sample * 2] = (byte)(value & 0xff);
            pcm[sample * 2 + 1] = (byte)((value >> 8) & 0xff);
        }
        writer.Write(pcm, 0, pcm.Length);
    }

    private static int GetKeySoundPlanPinCount(WindowsKeySoundService service)
    {
        var cacheGate = GetPrivateField<object>(service, "cacheGate");
        var cache = GetPrivateField<object>(service, "cache");
        lock (cacheGate)
        {
            var values = cache.GetType().GetProperty("Values")?.GetValue(cache)
                as System.Collections.IEnumerable
                ?? throw new InvalidOperationException("Key sound cache values are unavailable.");
            var total = 0;
            foreach (var entry in values)
            {
                var pinCount = entry?.GetType().GetProperty("PinCount")?.GetValue(entry)
                    ?? throw new InvalidOperationException("Key sound pin count is unavailable.");
                total += (int)pinCount;
            }
            return total;
        }
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(target);
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        var value = field.GetValue(target);
        return value is T typed
            ? typed
            : throw new InvalidCastException(
                $"{target.GetType().FullName}.{fieldName} is not {typeof(T).FullName}.");
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        ArgumentNullException.ThrowIfNull(target);
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private static Func<IWindowsAudioDeviceEngine> CreateFakeEngineFactory(
        params FakeWindowsAudioDeviceEngine[] engines)
    {
        var next = -1;
        return () =>
        {
            var index = Interlocked.Increment(ref next);
            if ((uint)index >= (uint)engines.Length)
                throw new InvalidOperationException("Fake audio engine factory was exhausted.");
            return engines[index];
        };
    }

    private sealed class FakeWindowsAudioDeviceEngine(bool blockLoad = false)
        : IWindowsAudioDeviceEngine
    {
        private readonly TaskCompletionSource loadEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource loadRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource playbackRateSetEntered = CompletedSource();
        private TaskCompletionSource playbackRateSetRelease = CompletedSource();
        private TaskCompletionSource pauseEntered = CompletedSource();
        private TaskCompletionSource pauseRelease = CompletedSource();
        private TaskCompletionSource overlayEntered = CompletedSource();
        private TaskCompletionSource overlayRelease = CompletedSource();
        private double playbackRate = 1;
        private bool preservePitch;
        private float volume = 1;
        private TimeSpan position;
        private int blockPlaybackRateSet;
        private int blockPause;
        private int blockOverlay;
        private int disposed;
        private int disposeCount;
        private int seekCount;
        private int refreshCount;
        private int acceptedOverlayCount;
        private int positionReadCount;
        private int outputLatencyReadCount;
        private ISampleProvider? acceptedOverlayProvider;

        public bool IsLoaded { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool HasOutput { get; private set; }
        public TimeSpan Position
        {
            get
            {
                Interlocked.Increment(ref positionReadCount);
                return position;
            }
            private set => position = value;
        }
        public TimeSpan Duration { get; private set; }
        public double OutputLatencyMilliseconds
        {
            get
            {
                Interlocked.Increment(ref outputLatencyReadCount);
                OutputLatencyReadAction?.Invoke();
                return 12;
            }
        }

        public double PlaybackRate
        {
            get => playbackRate;
            set
            {
                ThrowIfDisposed();
                if (Interlocked.Exchange(ref blockPlaybackRateSet, 0) != 0)
                {
                    playbackRateSetEntered.TrySetResult();
                    playbackRateSetRelease.Task.GetAwaiter().GetResult();
                }
                if (PlaybackRateFailure != null) throw PlaybackRateFailure;
                playbackRate = value;
            }
        }

        public bool PreservePitch
        {
            get => preservePitch;
            set
            {
                ThrowIfDisposed();
                preservePitch = value;
            }
        }

        public float Volume
        {
            get => volume;
            set
            {
                ThrowIfDisposed();
                volume = value;
            }
        }

        public Exception? RefreshFailure { get; set; }
        public Exception? PlaybackRateFailure { get; set; }
        public Action? OutputLatencyReadAction { get; set; }
        public Task LoadEntered => loadEntered.Task;
        public Task PlaybackRateSetEntered => playbackRateSetEntered.Task;
        public Task PauseEntered => pauseEntered.Task;
        public Task OverlayEntered => overlayEntered.Task;
        public int DisposeCount => Volatile.Read(ref disposeCount);
        public int SeekCount => Volatile.Read(ref seekCount);
        public int RefreshCount => Volatile.Read(ref refreshCount);
        public int AcceptedOverlayCount => Volatile.Read(ref acceptedOverlayCount);
        public int PositionReadCount => Volatile.Read(ref positionReadCount);
        public int OutputLatencyReadCount => Volatile.Read(ref outputLatencyReadCount);
        public ISampleProvider? LastAcceptedOverlayProvider =>
            Volatile.Read(ref acceptedOverlayProvider);

        public event EventHandler? PlaybackStopped;
        public event EventHandler<AudioOutputDeviceChangedEventArgs>? DefaultOutputDeviceChanged;

        public void Load(string path)
        {
            ThrowIfDisposed();
            _ = path;
            loadEntered.TrySetResult();
            if (blockLoad) loadRelease.Task.GetAwaiter().GetResult();
            ThrowIfDisposed();
            IsLoaded = true;
            IsPlaying = false;
            HasOutput = true;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.FromMinutes(2);
        }

        public void Play()
        {
            ThrowIfDisposed();
            if (IsLoaded && HasOutput) IsPlaying = true;
        }

        public void Pause()
        {
            ThrowIfDisposed();
            if (Interlocked.Exchange(ref blockPause, 0) != 0)
            {
                pauseEntered.TrySetResult();
                pauseRelease.Task.GetAwaiter().GetResult();
                ThrowIfDisposed();
            }
            IsPlaying = false;
        }

        public void Stop()
        {
            ThrowIfDisposed();
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void Seek(TimeSpan position)
        {
            ThrowIfDisposed();
            Interlocked.Increment(ref seekCount);
            Position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Duration.Ticks));
        }

        public void RefreshDefaultOutputDevice(bool resumePlayback)
        {
            ThrowIfDisposed();
            Interlocked.Increment(ref refreshCount);
            if (RefreshFailure != null)
            {
                HasOutput = false;
                IsPlaying = false;
                throw RefreshFailure;
            }
            HasOutput = IsLoaded;
            IsPlaying = HasOutput && resumePlayback;
        }

        public bool TryPlayOverlay(
            ISampleProvider provider,
            AudioPlaybackEpochToken playbackEpoch)
        {
            ThrowIfDisposed();
            if (!IsLoaded || !HasOutput || !IsPlaying || !playbackEpoch.IsCurrent) return false;
            if (Interlocked.Exchange(ref blockOverlay, 0) != 0)
            {
                overlayEntered.TrySetResult();
                overlayRelease.Task.GetAwaiter().GetResult();
                ThrowIfDisposed();
                if (!IsLoaded || !HasOutput || !IsPlaying || !playbackEpoch.IsCurrent) return false;
            }
            Volatile.Write(
                ref acceptedOverlayProvider,
                new EpochGuardSampleProvider(provider, playbackEpoch));
            Interlocked.Increment(ref acceptedOverlayCount);
            return true;
        }

        public void ReleaseLoad() => loadRelease.TrySetResult();

        public void BlockNextPlaybackRateSet()
        {
            playbackRateSetEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            playbackRateSetRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref blockPlaybackRateSet, 1);
        }

        public void ReleasePlaybackRateSet() => playbackRateSetRelease.TrySetResult();

        public void BlockNextPause()
        {
            pauseEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pauseRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref blockPause, 1);
        }

        public void ReleasePause() => pauseRelease.TrySetResult();

        public void BlockNextOverlay()
        {
            overlayEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            overlayRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref blockOverlay, 1);
        }

        public void ReleaseOverlay() => overlayRelease.TrySetResult();

        public Action CapturePlaybackStoppedCallback()
        {
            var handlers = PlaybackStopped;
            return () => handlers?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseDefaultOutputDeviceChanged(bool wasPlaying) =>
            DefaultOutputDeviceChanged?.Invoke(this, new AudioOutputDeviceChangedEventArgs(wasPlaying));

        public void LoseOutputAndRaiseDefaultOutputDeviceChanged(bool wasPlaying)
        {
            ThrowIfDisposed();
            HasOutput = false;
            IsPlaying = false;
            DefaultOutputDeviceChanged?.Invoke(
                this,
                new AudioOutputDeviceChangedEventArgs(wasPlaying));
        }

        public void LoseOutputAndRaiseUnexpectedPlaybackStop(bool wasPlaying)
        {
            ThrowIfDisposed();
            HasOutput = false;
            IsPlaying = false;
            DefaultOutputDeviceChanged?.Invoke(
                this,
                new AudioOutputDeviceChangedEventArgs(
                    wasPlaying,
                    AudioOutputDeviceChangeReason.UnexpectedPlaybackStop));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Interlocked.Increment(ref disposeCount);
            IsPlaying = false;
            HasOutput = false;
            loadRelease.TrySetResult();
            playbackRateSetRelease.TrySetResult();
            pauseRelease.TrySetResult();
            overlayRelease.TrySetResult();
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
            Volatile.Read(ref disposed) != 0,
            this);

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            source.TrySetResult();
            return source;
        }
    }

    private sealed class FakeWavePlayer(PlaybackState initialState) : IWavePlayer
    {
        private PlaybackState state = initialState;
        private int disposed;

        public PlaybackState PlaybackState => state;
        public float Volume { get; set; } = 1;
        public IWaveProvider? Provider { get; private set; }
        public WaveFormat OutputWaveFormat =>
            Provider?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);

        public event EventHandler<StoppedEventArgs>? PlaybackStopped;

        public void Init(IWaveProvider waveProvider)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            Provider = waveProvider ?? throw new ArgumentNullException(nameof(waveProvider));
            state = PlaybackState.Stopped;
        }

        public void Play()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            state = PlaybackState.Playing;
        }

        public void Pause()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            state = PlaybackState.Paused;
        }

        public void Stop()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            state = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            state = PlaybackState.Stopped;
            Provider = null;
        }
    }

    private sealed class DeterministicSampleProvider(
        float sampleValue,
        Action? afterFill = null) : ISampleProvider
    {
        private int readCount;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);

        public int ReadCount => Volatile.Read(ref readCount);

        public int Read(float[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Array.Fill(buffer, sampleValue, offset, count);
            Interlocked.Increment(ref readCount);
            afterFill?.Invoke();
            return count;
        }
    }

    private sealed class SilentSampleProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return 0;
        }
    }

    private sealed class TransientZeroSampleProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
        public int ReadCount { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            ReadCount++;
            if (ReadCount <= 2 || ReadCount >= 4) return 0;
            Array.Fill(buffer, .25f, offset, count);
            return count;
        }
    }

    private static void ValidateWpfNewChartPath(
        SliceEvidence evidence,
        string sourcePath,
        string libraryRoot)
    {
        var folder = Path.GetDirectoryName(sourcePath)
                     ?? throw new InvalidOperationException("The new chart has no project directory.");
        var folderName = Path.GetFileName(folder);
        const string expectedPrefix = "Avalonia Slice New Chart_";
        var timestampText = folderName.StartsWith(expectedPrefix, StringComparison.Ordinal)
            ? folderName[expectedPrefix.Length..]
            : string.Empty;
        var timestampValid = DateTime.TryParseExact(
            timestampText,
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
        var unixNameValid = long.TryParse(
            Path.GetFileNameWithoutExtension(sourcePath),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var unixSeconds) && unixSeconds > 0;
        var passed = IsInsideOrEqual(sourcePath, libraryRoot) &&
                     timestampValid &&
                     unixNameValid &&
                     Path.GetExtension(sourcePath).Equals(".mc", StringComparison.OrdinalIgnoreCase);
        Require(
            evidence,
            passed,
            "wpf-new-chart-path-shape",
            $"folder={folderName}; file={Path.GetFileName(sourcePath)}; unix={unixSeconds}");
    }

    private static async Task VerifyExternalOsuAndRootSwitchAsync(
        SliceEvidence evidence,
        AvaloniaWorkspaceController controller,
        DesktopChartTab dirtyTab,
        string secondLibraryRoot)
    {
        var originalRoot = controller.WorkspaceRoot;
        var countBeforeOpen = controller.Tabs.Count;
        var osuTab = await controller.OpenAsync(
                RequiredOsuFixturePath,
                OsuNoteTimingImportMode.CalibrateCommonSubdivisions)
            .ConfigureAwait(false);
        Require(
            evidence,
            PathsEqual(controller.WorkspaceRoot, originalRoot),
            "external-open-keeps-library-root",
            $"root={controller.WorkspaceRoot}; source={osuTab.SourcePath}");

        var duplicate = await controller.OpenAsync(
                Path.Combine(
                    Path.GetDirectoryName(RequiredOsuFixturePath)!,
                    ".",
                    Path.GetFileName(RequiredOsuFixturePath)),
                OsuNoteTimingImportMode.CalibrateCommonSubdivisions)
            .ConfigureAwait(false);
        Require(
            evidence,
            duplicate.Id == osuTab.Id && controller.Tabs.Count == countBeforeOpen + 1,
            "external-absolute-path-dedup",
            $"tab={osuTab.Id}; count={controller.Tabs.Count}");

        var document = osuTab.Session.Document;
        var holdCount = document.Notes.Count(note => note.IsHold);
        var bpmCount = document.Timing.Count(timing => timing.Bpm.HasValue);
        var scrollCount = document.Timing.Count(timing => timing.Effect == TimingEffectKind.Scroll);
        var audioPath = ResolveChartReference(osuTab.WorkingPath, document.Metadata.AudioFile);
        var backgroundPath = ResolveChartReference(osuTab.WorkingPath, document.Metadata.BackgroundFile);
        var resourcesLocalized = audioPath != null &&
                                 backgroundPath != null &&
                                 File.Exists(audioPath) &&
                                 File.Exists(backgroundPath) &&
                                 IsInsideOrEqual(audioPath, osuTab.CacheDirectory) &&
                                 IsInsideOrEqual(backgroundPath, osuTab.CacheDirectory);
        evidence.OsuImport = new OsuImportEvidence
        {
            SourcePath = RequiredOsuFixturePath,
            TimingImportMode = osuTab.OsuTimingImportMode.ToString(),
            KeyCount = document.KeyCount,
            NoteCount = document.Notes.Count,
            HoldNoteCount = holdCount,
            BpmEventCount = bpmCount,
            ScrollEventCount = scrollCount,
            LocalizedAudioPath = audioPath,
            LocalizedBackgroundPath = backgroundPath,
            ResourcesLocalized = resourcesLocalized
        };
        Require(
            evidence,
            osuTab.OsuTimingImportMode == OsuNoteTimingImportMode.CalibrateCommonSubdivisions &&
            document.KeyCount == 4 &&
            document.Notes.Count == 2_468 &&
            holdCount == 303 &&
            bpmCount == 2 &&
            scrollCount == 11,
            "real-osu-calibrated-structure",
            $"mode={osuTab.OsuTimingImportMode}; {document.KeyCount}K; " +
            $"notes={document.Notes.Count}; ln={holdCount}; bpm={bpmCount}; scroll={scrollCount}");
        Require(
            evidence,
            resourcesLocalized,
            "real-osu-resources-localized",
            $"audio={audioPath}; background={backgroundPath}");

        dirtyTab.Session.Mutate(chart => chart.Metadata.Version = "Dirty Root Switch Evidence");
        controller.Activate(dirtyTab.Id);
        var orderedTabIds = controller.Tabs.Select(tab => tab.Id).ToArray();
        var dirtyFingerprint = ChartStructuralFingerprint.Compute(dirtyTab.Session.Document);
        Directory.CreateDirectory(secondLibraryRoot);
        await controller.SwitchRootAsync(secondLibraryRoot).ConfigureAwait(false);
        var preserved = controller.Tabs.Select(tab => tab.Id).SequenceEqual(orderedTabIds) &&
                        controller.ActiveTab?.Id == dirtyTab.Id &&
                        dirtyTab.Session.IsDirty &&
                        string.Equals(
                            ChartStructuralFingerprint.Compute(dirtyTab.Session.Document),
                            dirtyFingerprint,
                            StringComparison.Ordinal);
        Require(
            evidence,
            PathsEqual(controller.WorkspaceRoot, secondLibraryRoot) && preserved,
            "switch-root-preserves-tabs-active-and-dirty",
            $"root={controller.WorkspaceRoot}; tabs={controller.Tabs.Count}; " +
            $"active={controller.ActiveTab?.Id}; dirty={dirtyTab.Session.IsDirty}");
    }

    private static async Task VerifyProjectDirectoryGatesAsync(
        SliceEvidence evidence,
        AvaloniaWorkspaceController controller,
        string runRoot)
    {
        var countBefore = controller.Tabs.Count;
        var emptyProject = Path.Combine(runRoot, "project-empty-direct");
        var nested = Path.Combine(emptyProject, "nested");
        Directory.CreateDirectory(nested);
        File.Copy(RequiredFixturePath, Path.Combine(nested, "nested-only.mc"), overwrite: false);
        var emptyRejected = await ThrowsAsync<DesktopProjectDirectoryValidationException>(async () =>
        {
            _ = await controller.OpenProjectDirectoryAsync(emptyProject).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Require(
            evidence,
            emptyRejected && controller.Tabs.Count == countBefore,
            "project-directory-direct-child-zero-gate",
            $"nestedIgnored=true; tabs={controller.Tabs.Count}");

        var oversizedProject = Path.Combine(runRoot, "project-129-direct");
        Directory.CreateDirectory(oversizedProject);
        for (var index = 0; index < DesktopDocumentWorkspace.MaximumProjectChartFiles + 1; index++)
        {
            var path = Path.Combine(
                oversizedProject,
                $"gate-{index.ToString("D3", CultureInfo.InvariantCulture)}.mc");
            await File.WriteAllTextAsync(path, "{}", Encoding.UTF8).ConfigureAwait(false);
        }
        var oversizedRejected = await ThrowsAsync<DesktopProjectDirectoryValidationException>(async () =>
        {
            _ = await controller.OpenProjectDirectoryAsync(oversizedProject).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Require(
            evidence,
            oversizedRejected && controller.Tabs.Count == countBefore,
            "project-directory-129-gate",
            $"directFiles={DesktopDocumentWorkspace.MaximumProjectChartFiles + 1}; tabs={controller.Tabs.Count}");
    }

    private static async Task VerifyDesktopRecoveryAndCloseAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var recoveryRoot = Path.Combine(runRoot, "recovery-library");
        var recoveryEditingRoot = Path.Combine(runRoot, "recovery-editing");
        var sourcePath = Path.Combine(recoveryRoot, "source", "recovery.mc");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.Copy(RequiredFixturePath, sourcePath, overwrite: false);

        var first = new AvaloniaWorkspaceController(recoveryRoot, recoveryEditingRoot, "AvaloniaSpecs");
        await first.InitializeAsync().ConfigureAwait(false);
        var firstTab = await first.OpenAsync(sourcePath).ConfigureAwait(false);
        var sourceFingerprint = ChartStructuralFingerprint.Compute(firstTab.Session.Document);
        first.UpdateViewState(firstTab.Id, 42.25);
        firstTab.Session.Mutate(chart => chart.Metadata.Version = "Unsynchronized Working Evidence");
        await first.PersistAsync().ConfigureAwait(false);

        var readableRestore = new AvaloniaWorkspaceController(
            recoveryRoot,
            recoveryEditingRoot,
            "AvaloniaSpecs");
        await readableRestore.InitializeAsync().ConfigureAwait(false);
        var readableTab = readableRestore.Tabs.Single(tab => PathsEqual(tab.SourcePath, sourcePath));
        var readableSourceWon = !readableTab.Session.IsDirty &&
                                readableRestore.ActiveTab?.Id == readableTab.Id &&
                                Math.Abs(readableTab.CurrentBeat - 42.25) < 1e-9 &&
                                string.Equals(
                                    ChartStructuralFingerprint.Compute(readableTab.Session.Document),
                                    sourceFingerprint,
                                    StringComparison.Ordinal);
        Require(
            evidence,
            readableSourceWon,
            "readable-source-wins-unsynchronized-working-cache",
            $"dirty={readableTab.Session.IsDirty}; beat={readableTab.CurrentBeat}; " +
            $"active={readableRestore.ActiveTab?.Id}");

        const string localSaveTitle = "Avalonia Local Save Winner";
        readableTab.Session.Mutate(chart => chart.Metadata.Title = localSaveTitle);
        var externalReplacement = MalodyMcSerializer.Load(sourcePath);
        externalReplacement.Metadata.Title = "External Source Interloper";
        MalodyMcSerializer.Save(externalReplacement, sourcePath);
        var externalHash = await ComputeSha256Async(sourcePath).ConfigureAwait(false);
        var saveResult = await readableRestore.SaveAsync(readableTab.Id).ConfigureAwait(false);
        var savedDocument = MalodyMcSerializer.Load(sourcePath);
        var savedHash = await ComputeSha256Async(sourcePath).ConfigureAwait(false);
        var externalChangeOverwritten = saveResult.Saved &&
                                        savedDocument.Metadata.Title == localSaveTitle &&
                                        !string.Equals(externalHash, savedHash, StringComparison.OrdinalIgnoreCase);
        Require(
            evidence,
            externalChangeOverwritten,
            "save-overwrites-external-source-change",
            $"status={saveResult.Status}; title={savedDocument.Metadata.Title}; hashChanged={externalHash != savedHash}");

        const string missingSourceTitle = "Recoverable Missing Source Working";
        readableTab.Session.Mutate(chart => chart.Metadata.Title = missingSourceTitle);
        await readableRestore.PersistAsync().ConfigureAwait(false);
        var sourceBackup = sourcePath + ".source-backup";
        File.Move(sourcePath, sourceBackup, overwrite: false);

        var missingRestore = new AvaloniaWorkspaceController(
            recoveryRoot,
            recoveryEditingRoot,
            "AvaloniaSpecs");
        await missingRestore.InitializeAsync().ConfigureAwait(false);
        var missingTab = missingRestore.Tabs.Single(tab => PathsEqual(tab.SourcePath, sourcePath));
        var missingRetained = missingTab.SourceMissing &&
                              missingTab.Session.IsDirty &&
                              missingTab.Session.Document.Metadata.Title == missingSourceTitle &&
                              missingRestore.RecoveryWarnings.Any(warning =>
                                  warning.Contains("source is missing", StringComparison.OrdinalIgnoreCase));
        Require(
            evidence,
            missingRetained,
            "missing-source-retains-dirty-working-cache",
            $"missing={missingTab.SourceMissing}; dirty={missingTab.Session.IsDirty}; " +
            $"warnings={missingRestore.RecoveryWarnings.Count}");

        var attributedObjectCount = 0;
        missingTab.Session.ApplyRemoteMutation(document =>
        {
            if (document.Notes.FirstOrDefault() is { } note)
            {
                note.OnlineCreatorUserId = 4101;
                attributedObjectCount++;
            }
            if (document.Timing.FirstOrDefault() is { } timing)
            {
                timing.OnlineCreatorUserId = 4202;
                attributedObjectCount++;
            }
        });
        missingTab.Session.ReplaceOwnerProtectedRegions(
        [
            new OwnerProtectedRegion(
                Guid.NewGuid(),
                BeatPosition.Preserve(2, 1, 4),
                BeatPosition.Preserve(3, 3, 4))
        ]);
        missingTab.Session.AllowOwnerProtectedEdits = false;
        missingTab.Session.IsReadOnly = true;
        missingTab.Session.CanEditMetadata = false;
        missingTab.Session.CanEditTiming = false;
        missingTab.Session.CanEditNotes = false;

        var clearedAttributionCount =
            WindowsOnlineRoomRetentionPolicy.PrepareForLocalEditing(missingTab);
        var materializedRoot = Path.Combine(runRoot, "online-room-documents");
        var materializedSource = Path.Combine(materializedRoot, "room-a", "chart.mc");
        var siblingSource = Path.Combine(
            Path.GetDirectoryName(materializedRoot)!,
            Path.GetFileName(materializedRoot) + "-sibling",
            "chart.mc");
        var preservedSource = WindowsOnlineRoomRetentionPolicy.CreatePreservedSourcePath(
            materializedRoot,
            Guid.Parse("b8000000-0000-0000-0000-000000000001"),
            missingTab.Id,
            DateTimeOffset.Parse(
                "2026-07-29T12:34:56.1234567Z",
                CultureInfo.InvariantCulture),
            ".mc");
        var roomRetentionPassed =
            WindowsOnlineRoomRetentionPolicy.IsMaterializedRoomSource(
                materializedSource,
                materializedRoot) &&
            WindowsOnlineRoomRetentionPolicy.IsMaterializedRoomSource(
                preservedSource,
                materializedRoot) &&
            preservedSource.EndsWith(
                $"20260729T1234561234567Z-{missingTab.Id:N}.mc",
                StringComparison.Ordinal) &&
            !WindowsOnlineRoomRetentionPolicy.IsMaterializedRoomSource(
                materializedRoot,
                materializedRoot) &&
            !WindowsOnlineRoomRetentionPolicy.IsMaterializedRoomSource(
                siblingSource,
                materializedRoot) &&
            clearedAttributionCount == attributedObjectCount &&
            missingTab.Session.Document.Notes.All(note => note.OnlineCreatorUserId == 0) &&
            missingTab.Session.Document.Timing.All(timing => timing.OnlineCreatorUserId == 0) &&
            missingTab.Session.OwnerProtectedRegions.Count == 0 &&
            missingTab.Session.AllowOwnerProtectedEdits &&
            !missingTab.Session.IsReadOnly &&
            missingTab.Session.CanEditMetadata &&
            missingTab.Session.CanEditTiming &&
            missingTab.Session.CanEditNotes;
        Require(
            evidence,
            roomRetentionPassed,
            "restarted-room-document-becomes-fully-editable-retained-local-tab",
            $"clearedAttribution={clearedAttributionCount}/{attributedObjectCount}; " +
            $"locks={missingTab.Session.OwnerProtectedRegions.Count}; " +
            $"readOnly={missingTab.Session.IsReadOnly}; " +
            $"inside={WindowsOnlineRoomRetentionPolicy.IsMaterializedRoomSource(materializedSource, materializedRoot)}; " +
            $"sibling={WindowsOnlineRoomRetentionPolicy.IsMaterializedRoomSource(siblingSource, materializedRoot)}; " +
            $"preserved={preservedSource}");

        var cacheDirectory = missingTab.CacheDirectory;
        var closed = await missingRestore.CloseAsync(
                missingTab.Id,
                discardUnsavedChanges: true)
            .ConfigureAwait(false);
        var cacheDeleted = closed && !Directory.Exists(cacheDirectory);
        Require(
            evidence,
            cacheDeleted,
            "explicit-close-deletes-desktop-cache",
            $"closed={closed}; cacheExists={Directory.Exists(cacheDirectory)}; cache={cacheDirectory}");

        await VerifyPreservedRoomDraftRecoveryAsync(evidence, runRoot).ConfigureAwait(false);

        evidence.WorkspaceRecovery = new WorkspaceRecoveryEvidence
        {
            SourcePath = sourcePath,
            SourceReadableWonUnsynchronizedWorking = readableSourceWon,
            CurrentBeatRestored = readableTab.CurrentBeat,
            ExternalSourceChangeOverwritten = externalChangeOverwritten,
            MissingSourceRetainedDirtyWorking = missingRetained,
            RestartedRoomBecameEditableRetainedLocal = roomRetentionPassed,
            ClearedOnlineAttributionCount = clearedAttributionCount,
            ExplicitCloseDeletedCache = cacheDeleted,
            CacheDirectory = cacheDirectory
        };
    }

    private static async Task VerifyPreservedRoomDraftRecoveryAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var libraryRoot = Path.Combine(runRoot, "retained-room-library");
        var editingRoot = Path.Combine(runRoot, "retained-room-editing");
        var sourcePath = Path.Combine(
            runRoot,
            "online-room-documents",
            "room-source.mc");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.Copy(RequiredFixturePath, sourcePath, overwrite: false);
        var sourceHashBefore = await ComputeSha256Async(sourcePath).ConfigureAwait(false);

        var first = new AvaloniaWorkspaceController(libraryRoot, editingRoot, "AvaloniaSpecs");
        await first.InitializeAsync().ConfigureAwait(false);
        var tab = await first.OpenAsync(sourcePath).ConfigureAwait(false);
        const string retainedVersion = "Retained Room Authority";
        tab.Session.ApplyRemoteMutation(document => document.Metadata.Version = retainedVersion);
        var retainedFingerprint = ChartStructuralFingerprint.Compute(tab.Session.Document);
        var preserved = await first.PreserveRoomDraftAsync(
                tab.Id,
                246810,
                "Retained Room",
                DateTimeOffset.Parse(
                    "2026-07-29T12:34:56Z",
                    CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        const string postRetentionVersion = "Retained Room Authority - Local Edit";
        tab.Session.Mutate(document => document.Metadata.Version = postRetentionVersion);
        await first.PersistAsync().ConfigureAwait(false);
        var postRetentionFingerprint = ChartStructuralFingerprint.Compute(tab.Session.Document);
        await File.AppendAllTextAsync(tab.WorkingPath, Environment.NewLine).ConfigureAwait(false);

        var restarted = new AvaloniaWorkspaceController(libraryRoot, editingRoot, "AvaloniaSpecs");
        await restarted.InitializeAsync().ConfigureAwait(false);
        var restored = restarted.Tabs.Single(candidate => PathsEqual(candidate.SourcePath, sourcePath));
        var sourceHashAfterRestore = await ComputeSha256Async(sourcePath).ConfigureAwait(false);
        var restoredRetained =
            preserved &&
            restored.IsPreservedRoomDraft &&
            restored.PreservedRoomNumber == 246810 &&
            restored.PreservedRoomName == "Retained Room" &&
            restored.PreservedRoomEndedUtc == new DateTime(2026, 7, 29, 12, 34, 56, DateTimeKind.Utc) &&
            restored.Session.IsDirty &&
            restored.Session.Document.Metadata.Version == postRetentionVersion &&
            string.Equals(
                ChartStructuralFingerprint.Compute(restored.Session.Document),
                postRetentionFingerprint,
                StringComparison.Ordinal) &&
            !string.Equals(postRetentionFingerprint, retainedFingerprint, StringComparison.Ordinal) &&
            restarted.RecoveryWarnings.Any(warning =>
                warning.Contains("retained room draft changed", StringComparison.Ordinal)) &&
            string.Equals(sourceHashAfterRestore, sourceHashBefore, StringComparison.OrdinalIgnoreCase);
        Require(
            evidence,
            restoredRetained,
            "retained-room-draft-restores-trusted-working-copy-and-metadata",
            $"preserved={preserved}; dirty={restored.Session.IsDirty}; " +
            $"room={restored.PreservedRoomNumber}/{restored.PreservedRoomName}; " +
            $"postRetentionEdit={restored.Session.Document.Metadata.Version == postRetentionVersion}; " +
            $"sourceUnchanged={sourceHashAfterRestore == sourceHashBefore}");

        var saveAsPath = Path.Combine(libraryRoot, "retained-save-as.mc");
        var saved = await restarted.SaveAsAsync(restored.Id, saveAsPath).ConfigureAwait(false);
        var clearedAfterSaveAs =
            saved.Saved &&
            !restored.IsPreservedRoomDraft &&
            restored.PreservedRoomNumber == 0 &&
            restored.PreservedRoomName.Length == 0 &&
            restored.PreservedRoomEndedUtc == null &&
            !restored.Session.IsDirty &&
            PathsEqual(restored.SourcePath, saveAsPath) &&
            string.Equals(
                await ComputeSha256Async(sourcePath).ConfigureAwait(false),
                sourceHashBefore,
                StringComparison.OrdinalIgnoreCase);
        Require(
            evidence,
            clearedAfterSaveAs,
            "retained-room-save-as-clears-draft-state-without-touching-old-source",
            $"saved={saved.Status}; retained={restored.IsPreservedRoomDraft}; " +
            $"dirty={restored.Session.IsDirty}; destination={restored.SourcePath}");
    }

    private static async Task VerifySaveAsAsync(SliceEvidence evidence, string runRoot)
    {
        var libraryRoot = Path.Combine(runRoot, "save-as-library");
        var editingRoot = Path.Combine(runRoot, "save-as-editing");
        var originalSource = Path.Combine(libraryRoot, "original", "original.mc");
        var occupiedSource = Path.Combine(libraryRoot, "occupied", "occupied.mc");
        Directory.CreateDirectory(Path.GetDirectoryName(originalSource)!);
        Directory.CreateDirectory(Path.GetDirectoryName(occupiedSource)!);
        File.Copy(RequiredFixturePath, originalSource, overwrite: false);
        File.Copy(RequiredFixturePath, occupiedSource, overwrite: false);
        var originalHashBefore = await ComputeSha256Async(originalSource).ConfigureAwait(false);

        var workspace = new DesktopDocumentWorkspace(libraryRoot, editingRoot, "AvaloniaSpecs");
        await workspace.InitializeAsync().ConfigureAwait(false);
        var tab = await workspace.OpenAsync(
                originalSource,
                OsuNoteTimingImportMode.CalibrateCommonSubdivisions)
            .ConfigureAwait(false);
        var tabId = tab.Id;
        var originalCacheDirectory = tab.CacheDirectory;
        var originalWorkingPath = tab.WorkingPath;
        const string mcSaveAsTitle = "Desktop Save As MC Evidence";
        tab.Session.Mutate(chart => chart.Metadata.Title = mcSaveAsTitle);

        var mcDestination = Path.Combine(libraryRoot, "save-as-target", "retargeted.mc");
        var mcSaveAs = await workspace.SaveAsAsync(tab.Id, mcDestination).ConfigureAwait(false);
        var originalHashAfterMcSaveAs = await ComputeSha256Async(originalSource).ConfigureAwait(false);
        var mcReloaded = MalodyMcSerializer.Load(mcDestination);
        var mcSaveAsPassed = mcSaveAs.Saved &&
                             File.Exists(mcDestination) &&
                             tab.Id == tabId &&
                             PathsEqual(tab.SourcePath, mcDestination) &&
                             tab.Format == DesktopChartFormat.MalodyMc &&
                             PathsEqual(tab.CacheDirectory, originalCacheDirectory) &&
                             PathsEqual(tab.WorkingPath, originalWorkingPath) &&
                             !tab.Session.IsDirty &&
                             tab.OsuTimingImportMode == OsuNoteTimingImportMode.CalibrateCommonSubdivisions &&
                             mcReloaded.Metadata.Title == mcSaveAsTitle &&
                             string.Equals(
                                 originalHashBefore,
                                 originalHashAfterMcSaveAs,
                                 StringComparison.OrdinalIgnoreCase);
        Require(
            evidence,
            mcSaveAsPassed,
            "save-as-mc-retargets-tab-and-preserves-old-source",
            $"status={mcSaveAs.Status}; source={tab.SourcePath}; cache={tab.CacheDirectory}; " +
            $"oldHashUnchanged={originalHashBefore == originalHashAfterMcSaveAs}; " +
            $"mode={tab.OsuTimingImportMode}");

        var restarted = new DesktopDocumentWorkspace(libraryRoot, editingRoot, "AvaloniaSpecs");
        await restarted.InitializeAsync().ConfigureAwait(false);
        var restartedTab = restarted.Tabs.Single(candidate => candidate.Id == tabId);
        var restartCachePassed = PathsEqual(restartedTab.SourcePath, mcDestination) &&
                                 PathsEqual(restartedTab.CacheDirectory, originalCacheDirectory) &&
                                 PathsEqual(restartedTab.WorkingPath, originalWorkingPath) &&
                                 restartedTab.Format == DesktopChartFormat.MalodyMc &&
                                 restartedTab.OsuTimingImportMode ==
                                 OsuNoteTimingImportMode.CalibrateCommonSubdivisions;
        Require(
            evidence,
            restartCachePassed,
            "save-as-restart-preserves-cache-path-and-retarget",
            $"source={restartedTab.SourcePath}; cache={restartedTab.CacheDirectory}; " +
            $"mode={restartedTab.OsuTimingImportMode}");

        var occupiedTab = await restarted.OpenAsync(occupiedSource).ConfigureAwait(false);
        var occupiedHashBefore = await ComputeSha256Async(occupiedSource).ConfigureAwait(false);
        var occupiedRejected = await ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await restarted.SaveAsAsync(restartedTab.Id, occupiedSource).ConfigureAwait(false);
        }).ConfigureAwait(false);
        var occupiedHashAfter = await ComputeSha256Async(occupiedSource).ConfigureAwait(false);
        var occupiedPassed = occupiedRejected &&
                             PathsEqual(occupiedTab.SourcePath, occupiedSource) &&
                             PathsEqual(restartedTab.SourcePath, mcDestination) &&
                             string.Equals(
                                 occupiedHashBefore,
                                 occupiedHashAfter,
                                 StringComparison.OrdinalIgnoreCase);
        Require(
            evidence,
            occupiedPassed,
            "save-as-open-target-rejected-without-write",
            $"rejected={occupiedRejected}; targetHashUnchanged={occupiedHashBefore == occupiedHashAfter}; " +
            $"source={restartedTab.SourcePath}");

        const string osuSaveAsTitle = "Missing Source Save As osu Evidence";
        restartedTab.Session.Mutate(chart => chart.Metadata.Title = osuSaveAsTitle);
        File.Delete(mcDestination);
        Require(
            evidence,
            !File.Exists(mcDestination),
            "save-as-source-missing-precondition",
            mcDestination);
        var osuDestination = Path.Combine(libraryRoot, "save-as-target", "retargeted.osu");
        var osuSaveAs = await restarted.SaveAsAsync(restartedTab.Id, osuDestination).ConfigureAwait(false);
        await using var osuStream = new FileStream(
            osuDestination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var osuReloaded = await ChartDocumentImporter.ImportAsync(
                osuStream,
                osuDestination,
                "AvaloniaSpecs",
                OsuNoteTimingImportMode.CalibrateCommonSubdivisions)
            .ConfigureAwait(false);
        var missingSourceOsuPassed = osuSaveAs.Saved &&
                                     File.Exists(osuDestination) &&
                                     restartedTab.Id == tabId &&
                                     PathsEqual(restartedTab.SourcePath, osuDestination) &&
                                     restartedTab.Format == DesktopChartFormat.OsuMania &&
                                     restartedTab.OsuTimingImportMode ==
                                     OsuNoteTimingImportMode.CalibrateCommonSubdivisions &&
                                     PathsEqual(restartedTab.CacheDirectory, originalCacheDirectory) &&
                                     !restartedTab.SourceMissing &&
                                     !restartedTab.Session.IsDirty &&
                                     osuReloaded.Metadata.Title == osuSaveAsTitle &&
                                     osuReloaded.KeyCount == restartedTab.Session.Document.KeyCount &&
                                     osuReloaded.Notes.Count == restartedTab.Session.Document.Notes.Count;
        Require(
            evidence,
            missingSourceOsuPassed,
            "save-as-missing-source-to-osu-preserves-mode",
            $"status={osuSaveAs.Status}; source={restartedTab.SourcePath}; format={restartedTab.Format}; " +
            $"mode={restartedTab.OsuTimingImportMode}; notes={osuReloaded.Notes.Count}");

        var reopenedOriginal = await restarted.OpenAsync(originalSource).ConfigureAwait(false);
        var reopenedIndependent = reopenedOriginal.Id != restartedTab.Id &&
                                  PathsEqual(reopenedOriginal.SourcePath, originalSource) &&
                                  !PathsEqual(reopenedOriginal.CacheDirectory, restartedTab.CacheDirectory) &&
                                  !PathsEqual(reopenedOriginal.WorkingPath, restartedTab.WorkingPath) &&
                                  reopenedOriginal.Session.Document.Metadata.Title != osuSaveAsTitle &&
                                  string.Equals(
                                      await ComputeSha256Async(originalSource).ConfigureAwait(false),
                                      originalHashBefore,
                                      StringComparison.OrdinalIgnoreCase);
        Require(
            evidence,
            reopenedIndependent,
            "save-as-old-source-reopens-with-independent-cache",
            $"retargetedCache={restartedTab.CacheDirectory}; reopenedCache={reopenedOriginal.CacheDirectory}; " +
            $"tabs={restarted.Tabs.Count}");

        var finalRestart = new DesktopDocumentWorkspace(libraryRoot, editingRoot, "AvaloniaSpecs");
        await finalRestart.InitializeAsync().ConfigureAwait(false);
        var finalRetargeted = finalRestart.Tabs.Single(candidate => candidate.Id == tabId);
        var finalOriginal = finalRestart.Tabs.Single(candidate =>
            PathsEqual(candidate.SourcePath, originalSource));
        var finalRestartPassed = PathsEqual(finalRetargeted.SourcePath, osuDestination) &&
                                 PathsEqual(finalRetargeted.CacheDirectory, originalCacheDirectory) &&
                                 finalRetargeted.OsuTimingImportMode ==
                                 OsuNoteTimingImportMode.CalibrateCommonSubdivisions &&
                                 !PathsEqual(finalOriginal.CacheDirectory, finalRetargeted.CacheDirectory);
        Require(
            evidence,
            finalRestartPassed,
            "save-as-osu-retarget-and-independent-cache-survive-restart",
            $"retargeted={finalRetargeted.CacheDirectory}; original={finalOriginal.CacheDirectory}; " +
            $"mode={finalRetargeted.OsuTimingImportMode}");

        evidence.SaveAs = new SaveAsEvidence
        {
            OriginalSourcePath = originalSource,
            OriginalSourceSha256Before = originalHashBefore,
            OriginalSourceSha256After = await ComputeSha256Async(originalSource).ConfigureAwait(false),
            McDestinationPath = mcDestination,
            OsuDestinationPath = osuDestination,
            TabRetargeted = mcSaveAsPassed && missingSourceOsuPassed,
            OriginalCacheDirectory = originalCacheDirectory,
            RestartedCacheDirectory = finalRetargeted.CacheDirectory,
            OccupiedTargetRejected = occupiedPassed,
            OccupiedTargetSha256Before = occupiedHashBefore,
            OccupiedTargetSha256After = occupiedHashAfter,
            MissingSourceSavedAsOsu = missingSourceOsuPassed,
            TimingImportMode = finalRetargeted.OsuTimingImportMode.ToString(),
            ReopenedOriginalCacheDirectory = finalOriginal.CacheDirectory,
            OriginalReopenedWithIndependentCache = reopenedIndependent && finalRestartPassed
        };
    }

    private static async Task VerifyArchiveAndLibraryAsync(
        SliceEvidence evidence,
        AvaloniaWorkspaceController controller,
        string libraryRoot,
        string runRoot)
    {
        Require(
            evidence,
            PathsEqual(controller.WorkspaceRoot, libraryRoot),
            "archive-import-target-root-active",
            controller.WorkspaceRoot);

        _ = await controller.CreateAsync("Zulu Library Sort Fixture").ConfigureAwait(false);
        _ = await controller.CreateAsync("Alpha Library Sort Fixture").ConfigureAwait(false);
        var promotedFolder = Path.Combine(libraryRoot, "_song_4242", "0");
        Directory.CreateDirectory(promotedFolder);
        var promotedChartPath = Path.Combine(promotedFolder, "promoted.mc");
        File.Copy(RequiredFixturePath, promotedChartPath, overwrite: false);

        var imported = await controller.ImportArchiveAsync(RequiredMczFixturePath).ConfigureAwait(false);
        var archiveHashAfterImport = await ComputeSha256Async(RequiredMczFixturePath).ConfigureAwait(false);
        var safeDestinations = IsInsideOrEqual(imported.Archive.DestinationRoot, libraryRoot) &&
                               imported.Archive.ChartPaths.All(path =>
                                   File.Exists(path) && IsInsideOrEqual(path, imported.Archive.DestinationRoot));
        var archivePassed = imported.Archive.ChartPaths.Count == 8 &&
                            imported.SupportedChartPaths.Count == 8 &&
                            imported.UnsupportedChartPaths.Count == 0 &&
                            imported.BrokenChartPaths.Count == 0 &&
                            safeDestinations &&
                            string.Equals(
                                archiveHashAfterImport,
                                RequiredMczFixtureSha256,
                                StringComparison.OrdinalIgnoreCase);
        evidence.ArchiveImport = new ArchiveImportEvidence
        {
            SourcePath = RequiredMczFixturePath,
            DestinationRoot = imported.Archive.DestinationRoot,
            ExtractedFileCount = imported.Archive.ExtractedFiles,
            ExtractedBytes = imported.Archive.ExtractedBytes,
            ChartPathCount = imported.Archive.ChartPaths.Count,
            SupportedChartCount = imported.SupportedChartPaths.Count,
            UnsupportedChartCount = imported.UnsupportedChartPaths.Count,
            BrokenChartCount = imported.BrokenChartPaths.Count,
            SafeDestinations = safeDestinations,
            SourceSha256AfterImport = archiveHashAfterImport
        };
        Require(
            evidence,
            archivePassed,
            "real-mcz-safe-import-eight-valid-mc",
            $"files={imported.Archive.ExtractedFiles}; charts={imported.Archive.ChartPaths.Count}; " +
            $"supported={imported.SupportedChartPaths.Count}; unsupported={imported.UnsupportedChartPaths.Count}; " +
            $"broken={imported.BrokenChartPaths.Count}; safe={safeDestinations}");

        var charts = await controller.ScanAsync().ConfigureAwait(false);
        var folders = DesktopChartLibraryService.GroupByFolder(charts, controller.ResolvedWorkspaceRoot);
        var promotedGroup = folders.Single(folder => folder.Charts.Any(chart =>
            PathsEqual(chart.ChartPath, promotedChartPath)));
        var promotedCorrectly = PathsEqual(
            promotedGroup.FolderPath,
            Path.GetDirectoryName(promotedFolder)!);
        var archiveGroup = folders.Single(folder => folder.Charts.Any(chart =>
            imported.Archive.ChartPaths.Any(path => PathsEqual(path, chart.ChartPath))));

        var folderQuery = Path.GetFileName(imported.Archive.DestinationRoot).ToLowerInvariant();
        var folderSearch = DesktopChartLibraryService.FilterAndSort(
            folders,
            folderQuery,
            DesktopLibrarySort.TitleAscending);
        var folderSearchPassed = folderSearch.Count == 1 && folderSearch[0].VisibleCharts.Count == 8;
        const string chartQuery = "1622528438.mc";
        var chartSearch = DesktopChartLibraryService.FilterAndSort(
            folders,
            chartQuery,
            DesktopLibrarySort.TitleAscending);
        var chartSearchPassed = chartSearch.Count == 1 &&
                                chartSearch[0].VisibleCharts.Count == 1 &&
                                Path.GetFileName(chartSearch[0].VisibleCharts[0].ChartPath)
                                    .Equals(chartQuery, StringComparison.OrdinalIgnoreCase);

        var titleAscending = DesktopChartLibraryService.FilterAndSort(
            folders,
            query: null,
            sort: DesktopLibrarySort.TitleAscending);
        var titleDescending = DesktopChartLibraryService.FilterAndSort(
            folders,
            query: null,
            sort: DesktopLibrarySort.TitleDescending);
        var addedAscending = DesktopChartLibraryService.FilterAndSort(
            folders,
            query: null,
            sort: DesktopLibrarySort.AddedAscending);
        var addedDescending = DesktopChartLibraryService.FilterAndSort(
            folders,
            query: null,
            sort: DesktopLibrarySort.AddedDescending);
        var expectedTitleAscending = folders
            .OrderBy(folder => folder.DisplayTitle, StringComparer.CurrentCulture)
            .Select(folder => folder.FolderPath);
        var expectedTitleDescending = folders
            .OrderByDescending(folder => folder.DisplayTitle, StringComparer.CurrentCulture)
            .Select(folder => folder.FolderPath);
        var titleSortsPassed = titleAscending.Select(view => view.Folder.FolderPath)
                                   .SequenceEqual(expectedTitleAscending, StringComparer.OrdinalIgnoreCase) &&
                               titleDescending.Select(view => view.Folder.FolderPath)
                                   .SequenceEqual(expectedTitleDescending, StringComparer.OrdinalIgnoreCase);
        var addedSortsPassed = IsNondecreasing(addedAscending.Select(view => view.Folder.AddedTime)) &&
                               IsNonincreasing(addedDescending.Select(view => view.Folder.AddedTime));
        var sortsPassed = titleSortsPassed && addedSortsPassed;
        var libraryPassed = charts.Count == 11 &&
                            folders.Count == 4 &&
                            promotedCorrectly &&
                            archiveGroup.ChartCount == 8 &&
                            folderSearchPassed &&
                            chartSearchPassed &&
                            sortsPassed;
        evidence.Library = new LibraryEvidence
        {
            ScannedChartCount = charts.Count,
            GroupCount = folders.Count,
            ArchiveGroupChartCount = archiveGroup.ChartCount,
            MalodyNumericFolderPromoted = promotedCorrectly,
            FolderSearchVisibleChartCount = folderSearch.Sum(view => view.VisibleCharts.Count),
            ChartSearchVisibleChartCount = chartSearch.Sum(view => view.VisibleCharts.Count),
            TitleSortsPassed = titleSortsPassed,
            AddedSortsPassed = addedSortsPassed
        };
        Require(
            evidence,
            libraryPassed,
            "desktop-library-group-search-sort",
            $"charts={charts.Count}; groups={folders.Count}; archiveGroup={archiveGroup.ChartCount}; " +
            $"promoted={promotedCorrectly}; folderSearch={folderSearchPassed}; " +
            $"chartSearch={chartSearchPassed}; sorts={sortsPassed}; run={runRoot}");
    }

    private static async Task VerifyOsuFullPackageExportAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        static async Task<Dictionary<string, string>> CaptureTreeAsync(string root)
        {
            var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                state[relative] =
                    $"{info.Length.ToString(CultureInfo.InvariantCulture)}|" +
                    $"{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}|" +
                    await ComputeSha256Async(path).ConfigureAwait(false);
            }

            return state;
        }

        static bool TreesEqual(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var value) &&
                             string.Equals(pair.Value, value, StringComparison.Ordinal));

        static async Task<string> ReadTextAsync(ZipArchiveEntry entry)
        {
            await using var input = entry.Open();
            using var reader = new StreamReader(input, Encoding.UTF8, true, 64 * 1024, leaveOpen: false);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        static async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry)
        {
            await using var input = entry.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output).ConfigureAwait(false);
            return output.ToArray();
        }

        var sliceRoot = Path.Combine(runRoot, "osu-full-package-export");
        var sourceRoot = Path.Combine(sliceRoot, "source");
        Directory.CreateDirectory(sourceRoot);
        var realSourceRoot = Path.GetDirectoryName(RequiredOsuFixturePath)
                             ?? throw new InvalidOperationException("The real osu fixture has no directory.");
        foreach (var source in Directory.EnumerateFiles(realSourceRoot, "*", SearchOption.TopDirectoryOnly))
            File.Copy(source, Path.Combine(sourceRoot, Path.GetFileName(source)), overwrite: false);

        var mainSourcePath = Path.Combine(sourceRoot, Path.GetFileName(RequiredOsuFixturePath));
        var siblingOsuPaths = Directory.EnumerateFiles(sourceRoot, "*.osu", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Require(
            evidence,
            siblingOsuPaths.Length == 2 && File.Exists(mainSourcePath),
            "osu-full-package-real-source-has-two-sibling-osu",
            $"charts={siblingOsuPaths.Length}; main={Path.GetFileName(mainSourcePath)}");
        var nonMainSiblingPath = siblingOsuPaths.Single(path => !PathsEqual(path, mainSourcePath));
        var nonMainSiblingBytes = await File.ReadAllBytesAsync(nonMainSiblingPath).ConfigureAwait(false);
        var nonMainSiblingArchiveExact = false;

        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "excluded.mc"), "exclude mc").ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "excluded.mcz"), [1, 2, 3]).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "excluded.osz"), [4, 5, 6]).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "excluded.luma-part"), [7, 8, 9]).ConfigureAwait(false);
        var sourceTreeBefore = await CaptureTreeAsync(sourceRoot).ConfigureAwait(false);

        var editingRoot = Path.Combine(sliceRoot, "editing");
        var controller = new AvaloniaWorkspaceController(sourceRoot, editingRoot, "AvaloniaOszExportSpecs");
        await controller.InitializeAsync().ConfigureAwait(false);
        var tab = await controller.OpenAsync(mainSourcePath).ConfigureAwait(false);
        var uniqueBeatGroup = tab.Session.Document.Notes
            .GroupBy(note => note.Beat)
            .First(group => group.Count() == 1);
        var movedNoteId = uniqueBeatGroup.Single().Id;
        var movedBeat = uniqueBeatGroup.Key;
        var originalLane = uniqueBeatGroup.Single().Lane;
        var movedLane = (originalLane + 1) % tab.Session.Document.KeyCount;
        var exportedVersion = tab.Session.Document.Metadata.Version + " OSZ Spec";
        tab.Session.Mutate(document =>
        {
            document.Metadata.Version = exportedVersion;
            document.Notes.Single(note => note.Id == movedNoteId).Lane = movedLane;
        });
        var dirtyBefore = tab.Session.IsDirty;
        var serialBefore = tab.Session.ChangeSerial;
        var fingerprintBefore = ChartStructuralFingerprint.Compute(tab.Session.Document);
        var documentPathBefore = tab.Session.Document.FilePath;
        var sourcePathBefore = tab.SourcePath;
        var workingPathBefore = tab.WorkingPath;
        Require(
            evidence,
            dirtyBefore && serialBefore == 1 && movedLane != originalLane,
            "osu-full-package-dirty-working-snapshot-is-discriminating",
            $"dirty={dirtyBefore}; serial={serialBefore}; beat={movedBeat}; lane={originalLane}->{movedLane}");

        var scratchRoot = Path.Combine(sliceRoot, "session-scratch");
        var scratchExportsRoot = Path.Combine(scratchRoot, "exports");
        Directory.CreateDirectory(scratchExportsRoot);
        var scratchSentinel = Path.Combine(scratchExportsRoot, "keep.sentinel");
        await File.WriteAllTextAsync(scratchSentinel, "keep").ConfigureAwait(false);
        var destination = Path.Combine(sliceRoot, "output", "full-package.osz");
        const double expectedOd = 6.25;
        const double expectedHp = 4.75;
        var result = await controller.ExportOsuFullPackageAsync(
                tab.Id,
                destination,
                expectedOd,
                expectedHp)
            .ConfigureAwait(false);
        Require(
            evidence,
            File.Exists(destination) &&
            PathsEqual(result.DestinationArchivePath, destination) &&
            result.MainChartEntryPath.Equals(Path.GetFileName(mainSourcePath), StringComparison.OrdinalIgnoreCase) &&
            result.ArchiveBytes == new FileInfo(destination).Length,
            "osu-full-package-result-points-to-original-main-name",
            $"main={result.MainChartEntryPath}; entries={result.EntryCount}; bytes={result.ArchiveBytes}");

        string exportedMainText;
        string[] archiveEntryNames;
        int archiveEntryCount;
        using (var archive = ZipFile.OpenRead(destination))
        {
            var fileEntries = archive.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Name)).ToArray();
            archiveEntryCount = archive.Entries.Count;
            archiveEntryNames = fileEntries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            var osuEntries = fileEntries.Where(entry =>
                    Path.GetExtension(entry.FullName).Equals(".osu", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var excluded = archiveEntryNames.Any(name =>
                name.EndsWith(".mc", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".mcz", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".osz", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase));
            Require(
                evidence,
                osuEntries.Length == siblingOsuPaths.Length && !excluded,
                "osu-full-package-keeps-osu-siblings-and-excludes-other-chart-artifacts",
                $"osu={osuEntries.Length}; excluded={excluded}; entries={string.Join(';', archiveEntryNames)}");

            var mainEntry = fileEntries.Single(entry => entry.FullName.Replace('\\', '/').Equals(
                result.MainChartEntryPath,
                StringComparison.OrdinalIgnoreCase));
            var nonMainSiblingEntry = osuEntries.Single(entry => Path.GetFileName(entry.FullName).Equals(
                Path.GetFileName(nonMainSiblingPath),
                StringComparison.OrdinalIgnoreCase));
            nonMainSiblingArchiveExact = nonMainSiblingBytes.SequenceEqual(
                await ReadBytesAsync(nonMainSiblingEntry).ConfigureAwait(false));
            exportedMainText = await ReadTextAsync(mainEntry).ConfigureAwait(false);
        }
        Require(
            evidence,
            result.EntryCount == archiveEntryCount,
            "osu-full-package-result-entry-count-matches-archive",
            $"result={result.EntryCount}; archive={archiveEntryCount}");
        Require(
            evidence,
            exportedMainText.Contains("osu file format v14", StringComparison.Ordinal) &&
            exportedMainText.Contains("OverallDifficulty:6.25", StringComparison.Ordinal) &&
            exportedMainText.Contains("HPDrainRate:4.75", StringComparison.Ordinal) &&
            exportedMainText.Contains($"Version:{exportedVersion}", StringComparison.Ordinal),
            "osu-full-package-main-entry-is-v14-with-requested-difficulty-and-dirty-metadata",
            $"v14={exportedMainText.Contains("osu file format v14", StringComparison.Ordinal)}; " +
            $"od={exportedMainText.Contains("OverallDifficulty:6.25", StringComparison.Ordinal)}; " +
            $"hp={exportedMainText.Contains("HPDrainRate:4.75", StringComparison.Ordinal)}");

        var reverseImported = await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                destination,
                Path.Combine(sliceRoot, "reverse-import"))
            .ConfigureAwait(false);
        var freshMainPath = reverseImported.ChartPaths.Single(path =>
            Path.GetFileName(path).Equals(Path.GetFileName(mainSourcePath), StringComparison.OrdinalIgnoreCase));
        var freshNonMainSiblingPath = reverseImported.ChartPaths.Single(path =>
            Path.GetFileName(path).Equals(Path.GetFileName(nonMainSiblingPath), StringComparison.OrdinalIgnoreCase));
        var nonMainSiblingFreshExact = nonMainSiblingBytes.SequenceEqual(
            await File.ReadAllBytesAsync(freshNonMainSiblingPath).ConfigureAwait(false));
        ChartDocument freshDocument;
        await using (var freshStream = new FileStream(
                         freshMainPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            freshDocument = await ChartDocumentImporter.ImportAsync(
                    freshStream,
                    freshMainPath,
                    controller.DefaultCreator)
                .ConfigureAwait(false);
        }

        var freshMoved = freshDocument.Notes.Any(note =>
            note.Beat.CompareTo(movedBeat) == 0 && note.Lane == movedLane);
        var freshAudioPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(freshMainPath)!,
            freshDocument.Metadata.AudioFile));
        var freshBackgroundPath = string.IsNullOrWhiteSpace(freshDocument.Metadata.BackgroundFile)
            ? null
            : Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(freshMainPath)!,
                freshDocument.Metadata.BackgroundFile));
        Require(
            evidence,
            reverseImported.ChartPaths.Count == siblingOsuPaths.Length &&
            freshDocument.Metadata.Version == exportedVersion &&
            Math.Abs(freshDocument.Metadata.OverallDifficulty - expectedOd) < .000001 &&
            Math.Abs(freshDocument.Metadata.HpDrainRate - expectedHp) < .000001 &&
            freshMoved &&
            File.Exists(freshAudioPath) &&
            (freshBackgroundPath == null || File.Exists(freshBackgroundPath)),
            "osu-full-package-fresh-import-retains-dirty-chart-difficulty-and-resources",
            $"charts={reverseImported.ChartPaths.Count}; version={freshDocument.Metadata.Version}; " +
            $"od={freshDocument.Metadata.OverallDifficulty}; hp={freshDocument.Metadata.HpDrainRate}; " +
            $"moved={freshMoved}; audio={File.Exists(freshAudioPath)}; " +
             $"background={freshBackgroundPath == null || File.Exists(freshBackgroundPath)}");
        Require(
            evidence,
            nonMainSiblingArchiveExact && nonMainSiblingFreshExact,
            "osu-full-package-sibling-osu-bytes-are-exact-source-archive-fresh",
            $"archive={nonMainSiblingArchiveExact}; fresh={nonMainSiblingFreshExact}; " +
            $"sibling={Path.GetFileName(nonMainSiblingPath)}");

        var sourceTreeAfter = await CaptureTreeAsync(sourceRoot).ConfigureAwait(false);
        var sessionUnchanged = tab.Session.IsDirty == dirtyBefore &&
                               tab.Session.ChangeSerial == serialBefore &&
                               string.Equals(
                                   ChartStructuralFingerprint.Compute(tab.Session.Document),
                                   fingerprintBefore,
                                   StringComparison.Ordinal) &&
                               string.Equals(tab.Session.Document.FilePath, documentPathBefore, StringComparison.Ordinal) &&
                               PathsEqual(tab.SourcePath, sourcePathBefore) &&
                               PathsEqual(tab.WorkingPath, workingPathBefore);
        Require(
            evidence,
            sessionUnchanged && TreesEqual(sourceTreeBefore, sourceTreeAfter),
            "osu-full-package-export-does-not-save-dirty-tab-or-touch-source-tree",
            $"session={sessionUnchanged}; source={TreesEqual(sourceTreeBefore, sourceTreeAfter)}");
        Require(
            evidence,
            File.Exists(scratchSentinel) &&
            !Directory.EnumerateDirectories(scratchExportsRoot, "*", SearchOption.TopDirectoryOnly).Any(),
            "osu-full-package-success-cleans-only-owned-scratch-directory",
            $"sentinel={File.Exists(scratchSentinel)}; " +
            $"remaining={Directory.EnumerateDirectories(scratchExportsRoot).Count()}");

        var invalidDifficultyRejected = await ThrowsAsync<ArgumentOutOfRangeException>(() =>
                OsuFullPackageExportService.ExportAsync(
                    new OsuFullPackageExportRequest(
                        tab.Session.CaptureCommittedDocument(),
                        tab.SourcePath,
                        Path.Combine(sliceRoot, "output", "invalid.osz"),
                        scratchRoot,
                        OverallDifficulty: 10.01,
                        HpDrainRate: 8)))
            .ConfigureAwait(false);
        var pickerChosenPath = Path.Combine(sliceRoot, "output", "picker-name.mcz");
        var pickerChosenResult = await OsuFullPackageExportService.ExportAsync(
                new OsuFullPackageExportRequest(
                    tab.Session.CaptureCommittedDocument(),
                    tab.SourcePath,
                    pickerChosenPath,
                    scratchRoot))
            .ConfigureAwait(false);
        bool pickerChosenNamePreserved;
        using (var pickerChosenArchive = ZipFile.OpenRead(pickerChosenPath))
            pickerChosenNamePreserved =
                pickerChosenArchive.Entries.Count == pickerChosenResult.EntryCount;
        Require(
            evidence,
            invalidDifficultyRejected &&
            pickerChosenNamePreserved &&
            PathsEqual(pickerChosenResult.DestinationArchivePath, pickerChosenPath),
            "osu-full-package-rejects-invalid-difficulty-and-preserves-picker-filename",
            $"difficulty={invalidDifficultyRejected}; pickerName={pickerChosenNamePreserved}; " +
            $"path={Path.GetFileName(pickerChosenResult.DestinationArchivePath)}");
    }

    private static async Task VerifyMalodyFullPackageExportAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        static async Task<Dictionary<string, string>> CaptureTreeAsync(string root)
        {
            var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(root, directory)
                    .Replace(Path.DirectorySeparatorChar, '/');
                state[$"D:{relative}"] = string.Empty;
            }
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(path);
                var relative = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                state[$"F:{relative}"] =
                    $"{info.Length.ToString(CultureInfo.InvariantCulture)}|" +
                    $"{info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}|" +
                    await ComputeSha256Async(path).ConfigureAwait(false);
            }
            return state;
        }

        static bool TreeStatesEqual(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var value) &&
                             string.Equals(pair.Value, value, StringComparison.Ordinal));

        static async Task<byte[]> ReadEntryBytesAsync(ZipArchiveEntry entry)
        {
            await using var input = entry.Open();
            using var output = new MemoryStream();
            await input.CopyToAsync(output).ConfigureAwait(false);
            return output.ToArray();
        }

        static string ResolveFreshResource(string chartPath, string reference) =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(chartPath)!, reference));

        var sliceRoot = Path.Combine(runRoot, "malody-full-package-export");
        var sourceParent = Path.Combine(sliceRoot, "source-parent");
        var extracted = await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                RequiredMczFixturePath,
                sourceParent)
            .ConfigureAwait(false);
        var sourceCharts = extracted.ChartPaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Require(
            evidence,
            sourceCharts.Length == 8,
            "malody-full-package-real-source-has-eight-sibling-mc",
            $"charts={sourceCharts.Length}; root={extracted.DestinationRoot}");

        var mainSourcePath = sourceCharts.Single(path =>
            Path.GetFileName(path).Equals("1622528438.mc", StringComparison.OrdinalIgnoreCase));
        var sourceRoot = Path.GetDirectoryName(mainSourcePath)
                         ?? throw new InvalidOperationException("The MCZ export source has no directory.");
        var prettySiblingPath = sourceCharts.First(path => !PathsEqual(path, mainSourcePath));
        var prettySibling = JsonNode.Parse(await File.ReadAllTextAsync(prettySiblingPath).ConfigureAwait(false))
                            ?? throw new InvalidDataException("The sibling MC fixture is empty.");
        await File.WriteAllTextAsync(
                prettySiblingPath,
                prettySibling.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);
        Require(
            evidence,
            (await File.ReadAllTextAsync(prettySiblingPath).ConfigureAwait(false)).Contains('\n'),
            "malody-full-package-sibling-compact-test-is-discriminating",
            Path.GetFileName(prettySiblingPath));

        var excludedRootOsu = Path.Combine(sourceRoot, "excluded-root.osu");
        var excludedNestedRoot = Path.Combine(sourceRoot, "excluded-nested");
        Directory.CreateDirectory(excludedNestedRoot);
        await File.WriteAllTextAsync(excludedRootOsu, "osu exclusion fixture").ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(excludedNestedRoot, "excluded-nested.osu"),
                "nested osu exclusion fixture")
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "excluded.mcz"), [1, 2, 3])
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "excluded.osz"), [4, 5, 6])
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "excluded.luma-part"), [7, 8, 9])
            .ConfigureAwait(false);

        const string collisionAudioName = "luma-export-collision.ogg";
        const string collisionImageName = "luma-export-collision.jpg";
        const string collisionSoundName = "luma-export-collision.wav";
        byte[] sourceAudioBytes = [0x53, 0x52, 0x43, 0x2D, 0x41];
        byte[] sourceImageBytes = [0x53, 0x52, 0x43, 0x2D, 0x49];
        byte[] sourceSoundBytes = [0x53, 0x52, 0x43, 0x2D, 0x53];
        byte[] workingAudioBytes = [0x57, 0x4F, 0x52, 0x4B, 0x2D, 0x41];
        byte[] workingImageBytes = [0x57, 0x4F, 0x52, 0x4B, 0x2D, 0x49];
        byte[] workingSoundBytes = [0x57, 0x4F, 0x52, 0x4B, 0x2D, 0x53];
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, collisionAudioName), sourceAudioBytes)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, collisionImageName), sourceImageBytes)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, collisionSoundName), sourceSoundBytes)
            .ConfigureAwait(false);
        var sourceTreeBefore = await CaptureTreeAsync(sourceRoot).ConfigureAwait(false);

        var editingRoot = Path.Combine(sliceRoot, "editing");
        var controller = new AvaloniaWorkspaceController(sourceRoot, editingRoot, "AvaloniaMczExportSpecs");
        await controller.InitializeAsync().ConfigureAwait(false);
        var tab = await controller.OpenAsync(mainSourcePath).ConfigureAwait(false);
        var workingRoot = Path.GetDirectoryName(tab.WorkingPath)
                          ?? throw new InvalidOperationException("The MCZ export working chart has no directory.");
        await File.WriteAllBytesAsync(Path.Combine(workingRoot, collisionAudioName), workingAudioBytes)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(workingRoot, collisionImageName), workingImageBytes)
            .ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(workingRoot, collisionSoundName), workingSoundBytes)
            .ConfigureAwait(false);

        var injectedId = Guid.NewGuid();
        var injectedBeat = BeatPosition.Preserve(
            tab.Session.Document.Notes.Select(note => note.Beat.Bar).DefaultIfEmpty().Max() + 8,
            2,
            14);
        tab.Session.Mutate(document =>
        {
            document.Metadata.AudioFile = collisionAudioName;
            document.Metadata.CoverFile = collisionImageName;
            document.Metadata.BackgroundFile = collisionImageName;
            document.Notes.Add(new ChartNote
            {
                Id = injectedId,
                Lane = 0,
                Beat = injectedBeat,
                Sound = collisionSoundName
            });
        });
        var sessionDirtyBefore = tab.Session.IsDirty;
        var sessionSerialBefore = tab.Session.ChangeSerial;
        var sessionFingerprintBefore = ChartStructuralFingerprint.Compute(tab.Session.Document);
        var documentFilePathBefore = tab.Session.Document.FilePath;
        var sourcePathBefore = tab.SourcePath;
        var workingPathBefore = tab.WorkingPath;
        Require(
            evidence,
            sessionDirtyBefore && sessionSerialBefore == 1 &&
            documentFilePathBefore != null && PathsEqual(documentFilePathBefore, workingPathBefore) &&
            injectedBeat.Numerator == 2 && injectedBeat.Denominator == 14,
            "malody-full-package-dirty-working-snapshot-is-discriminating",
            $"dirty={sessionDirtyBefore}; serial={sessionSerialBefore}; beat={injectedBeat}; " +
            $"source={sourcePathBefore}; working={workingPathBefore}");

        var scratchRoot = Path.Combine(sliceRoot, "session-scratch");
        var scratchExportsRoot = Path.Combine(scratchRoot, "exports");
        Directory.CreateDirectory(scratchExportsRoot);
        var scratchSentinel = Path.Combine(scratchExportsRoot, "keep.sentinel");
        await File.WriteAllTextAsync(scratchSentinel, "keep").ConfigureAwait(false);
        var destinationArchivePath = Path.Combine(sliceRoot, "output", "full-package.mcz");
        var result = await MalodyFullPackageExportService.ExportAsync(
                new MalodyFullPackageExportRequest(
                    tab.Session.CaptureCommittedDocument(),
                    tab.SourcePath,
                    destinationArchivePath,
                    scratchRoot))
            .ConfigureAwait(false);
        Require(
            evidence,
            File.Exists(destinationArchivePath) &&
            PathsEqual(result.DestinationArchivePath, destinationArchivePath) &&
            result.ArchiveBytes == new FileInfo(destinationArchivePath).Length &&
            result.MainChartEntryPath.Equals(Path.GetFileName(mainSourcePath), StringComparison.OrdinalIgnoreCase),
            "malody-full-package-result-points-to-original-main-name",
            $"main={result.MainChartEntryPath}; entries={result.EntryCount}; bytes={result.ArchiveBytes}");

        byte[] mainChartBytes;
        string[] entryNames;
        int archiveEntryCount;
        using (var archive = ZipFile.OpenRead(destinationArchivePath))
        {
            var fileEntries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .ToArray();
            archiveEntryCount = archive.Entries.Count;
            entryNames = fileEntries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
            var mainEntry = fileEntries.Single(entry =>
                entry.FullName.Replace('\\', '/').Equals(
                    result.MainChartEntryPath,
                    StringComparison.OrdinalIgnoreCase));
            mainChartBytes = await ReadEntryBytesAsync(mainEntry).ConfigureAwait(false);

            var mcEntries = fileEntries.Where(entry =>
                    Path.GetExtension(entry.FullName).Equals(".mc", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var everyMcCompactAndParseable = mcEntries.Length == sourceCharts.Length;
            foreach (var entry in mcEntries)
            {
                var bytes = await ReadEntryBytesAsync(entry).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(bytes);
                try { using var _ = JsonDocument.Parse(bytes); }
                catch (JsonException) { everyMcCompactAndParseable = false; }
                everyMcCompactAndParseable &=
                    !text.Contains('\r') && !text.Contains('\n') &&
                    !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            }
            Require(
                evidence,
                everyMcCompactAndParseable,
                "malody-full-package-keeps-eight-sibling-mc-and-compacts-every-entry",
                $"mcEntries={mcEntries.Length}; expected={sourceCharts.Length}");

            var excluded = entryNames.Any(name =>
                name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".mcz", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".osz", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".mc_", StringComparison.OrdinalIgnoreCase));
            Require(
                evidence,
                !excluded,
                "malody-full-package-excludes-other-charts-archives-parts-and-shadow-mc",
                string.Join(";", entryNames.Where(name =>
                    name.EndsWith(".osu", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".mcz", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".osz", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".mc_", StringComparison.OrdinalIgnoreCase))));

            async Task<bool> EntryMatchesAsync(string name, byte[] expected)
            {
                var entry = fileEntries.SingleOrDefault(candidate =>
                    candidate.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));
                return entry != null &&
                       (await ReadEntryBytesAsync(entry).ConfigureAwait(false)).SequenceEqual(expected);
            }

            var resourceCopiesPassed =
                await EntryMatchesAsync(collisionAudioName, sourceAudioBytes).ConfigureAwait(false) &&
                await EntryMatchesAsync("luma-export-collision_2.ogg", workingAudioBytes).ConfigureAwait(false) &&
                await EntryMatchesAsync(collisionImageName, sourceImageBytes).ConfigureAwait(false) &&
                await EntryMatchesAsync("luma-export-collision_2.jpg", workingImageBytes).ConfigureAwait(false) &&
                await EntryMatchesAsync(collisionSoundName, sourceSoundBytes).ConfigureAwait(false) &&
                await EntryMatchesAsync("luma-export-collision_2.wav", workingSoundBytes).ConfigureAwait(false);
            Require(
                evidence,
                resourceCopiesPassed,
                "malody-full-package-resolves-working-resources-and-preserves-source-collisions-as-suffix-two",
                string.Join(";", entryNames.Where(name => name.StartsWith(
                    "luma-export-collision",
                    StringComparison.OrdinalIgnoreCase))));
        }
        Require(
            evidence,
            result.EntryCount == archiveEntryCount,
            "malody-full-package-result-entry-count-matches-archive",
            $"result={result.EntryCount}; archive={archiveEntryCount}");

        var mainChartText = Encoding.UTF8.GetString(mainChartBytes);
        var exportedDocument = MalodyMcSerializer.Parse(
            mainChartText,
            mainSourcePath,
            controller.DefaultCreator);
        var expectedExportedDocument = tab.Session.CaptureCommittedDocument();
        expectedExportedDocument.Metadata.AudioFile = "luma-export-collision_2.ogg";
        expectedExportedDocument.Metadata.CoverFile = "luma-export-collision_2.jpg";
        expectedExportedDocument.Metadata.BackgroundFile = "luma-export-collision_2.jpg";
        expectedExportedDocument.Notes.Single(note => note.Id == injectedId).Sound =
            "luma-export-collision_2.wav";
        var exportedInjected = exportedDocument.Notes.Single(note =>
            note.Sound == "luma-export-collision_2.wav" && note.Beat.CompareTo(injectedBeat) == 0);
        Require(
            evidence,
            exportedInjected.Beat.Bar == injectedBeat.Bar &&
            exportedInjected.Beat.Numerator == injectedBeat.Numerator &&
            exportedInjected.Beat.Denominator == injectedBeat.Denominator &&
            string.Equals(
                ChartStructuralFingerprint.Compute(exportedDocument),
                ChartStructuralFingerprint.Compute(expectedExportedDocument),
                StringComparison.Ordinal),
            "malody-full-package-main-chart-is-current-dirty-document-with-exact-rational-beat",
            $"beat={exportedInjected.Beat}; fingerprint={ChartStructuralFingerprint.Compute(exportedDocument)}");

        var reverseImported = await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                destinationArchivePath,
                Path.Combine(sliceRoot, "reverse-import"))
            .ConfigureAwait(false);
        var freshMainPath = reverseImported.ChartPaths.Single(path =>
            Path.GetFileName(path).Equals(Path.GetFileName(mainSourcePath), StringComparison.OrdinalIgnoreCase));
        ChartDocument freshDocument;
        await using (var freshStream = new FileStream(
                         freshMainPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            freshDocument = await ChartDocumentImporter.ImportAsync(
                    freshStream,
                    freshMainPath,
                    controller.DefaultCreator)
                .ConfigureAwait(false);
        }
        var freshInjected = freshDocument.Notes.Single(note =>
            note.Sound == "luma-export-collision_2.wav" && note.Beat.CompareTo(injectedBeat) == 0);
        var freshResourcesPresent =
            File.Exists(ResolveFreshResource(freshMainPath, freshDocument.Metadata.AudioFile)) &&
            File.Exists(ResolveFreshResource(freshMainPath, freshDocument.Metadata.CoverFile)) &&
            File.Exists(ResolveFreshResource(freshMainPath, freshInjected.Sound!));
        Require(
            evidence,
            reverseImported.ChartPaths.Count == sourceCharts.Length &&
            freshResourcesPresent &&
            string.Equals(
                ChartStructuralFingerprint.Compute(freshDocument),
                ChartStructuralFingerprint.Compute(expectedExportedDocument),
                StringComparison.Ordinal),
            "malody-full-package-fresh-import-retains-structure-and-resources",
            $"charts={reverseImported.ChartPaths.Count}; resources={freshResourcesPresent}; " +
            $"fingerprint={ChartStructuralFingerprint.Compute(freshDocument)}");

        var sourceTreeAfterSuccess = await CaptureTreeAsync(sourceRoot).ConfigureAwait(false);
        var sessionUnchanged = tab.Session.IsDirty == sessionDirtyBefore &&
                               tab.Session.ChangeSerial == sessionSerialBefore &&
                               string.Equals(
                                   ChartStructuralFingerprint.Compute(tab.Session.Document),
                                   sessionFingerprintBefore,
                                   StringComparison.Ordinal) &&
                               string.Equals(tab.Session.Document.FilePath, documentFilePathBefore, StringComparison.Ordinal) &&
                               PathsEqual(tab.SourcePath, sourcePathBefore) &&
                               PathsEqual(tab.WorkingPath, workingPathBefore);
        Require(
            evidence,
            sessionUnchanged && TreeStatesEqual(sourceTreeBefore, sourceTreeAfterSuccess),
            "malody-full-package-export-does-not-save-dirty-tab-or-touch-source-tree",
            $"sessionUnchanged={sessionUnchanged}; sourceEntriesBefore={sourceTreeBefore.Count}; " +
            $"sourceEntriesAfter={sourceTreeAfterSuccess.Count}");
        Require(
            evidence,
            File.Exists(scratchSentinel) &&
            !Directory.EnumerateDirectories(scratchExportsRoot, "*", SearchOption.TopDirectoryOnly).Any(),
            "malody-full-package-success-cleans-only-owned-scratch-directory",
            $"sentinel={File.Exists(scratchSentinel)}; " +
            $"remainingDirectories={Directory.EnumerateDirectories(scratchExportsRoot).Count()}");

        var failedDocument = tab.Session.CaptureCommittedDocument();
        failedDocument.Metadata.AudioFile = "missing-required-audio.ogg";
        var failedDestination = Path.Combine(sliceRoot, "output", "must-not-exist.mcz");
        var missingAudioRejected = await ThrowsAsync<FileNotFoundException>(() =>
                MalodyFullPackageExportService.ExportAsync(
                    new MalodyFullPackageExportRequest(
                        failedDocument,
                        tab.SourcePath,
                        failedDestination,
                        scratchRoot)))
            .ConfigureAwait(false);
        var sourceTreeAfterFailure = await CaptureTreeAsync(sourceRoot).ConfigureAwait(false);
        Require(
            evidence,
            missingAudioRejected &&
            !File.Exists(failedDestination) &&
            File.Exists(scratchSentinel) &&
            !Directory.EnumerateDirectories(scratchExportsRoot, "*", SearchOption.TopDirectoryOnly).Any() &&
            TreeStatesEqual(sourceTreeBefore, sourceTreeAfterFailure),
            "malody-full-package-failure-cleans-owned-scratch-and-preserves-source",
            $"rejected={missingAudioRejected}; destination={File.Exists(failedDestination)}; " +
            $"sentinel={File.Exists(scratchSentinel)}; sourceUnchanged={TreeStatesEqual(sourceTreeBefore, sourceTreeAfterFailure)}");

        var previousEditingRootOverride = Environment.GetEnvironmentVariable(
            "LUMACHART_AVALONIA_EDITING_ROOT");
        var processEditingRoot = Path.Combine(sliceRoot, "process-editing-root");
        var persistentSentinel = Path.Combine(processEditingRoot, "keep-persistent.txt");
        var siblingSessionRoot = Path.Combine(
            processEditingRoot,
            "_scratch",
            $"session_{Environment.ProcessId}_sibling");
        var siblingSentinel = Path.Combine(siblingSessionRoot, "keep-sibling.txt");
        try
        {
            Environment.SetEnvironmentVariable("LUMACHART_AVALONIA_EDITING_ROOT", processEditingRoot);
            Directory.CreateDirectory(siblingSessionRoot);
            await File.WriteAllTextAsync(persistentSentinel, "persistent").ConfigureAwait(false);
            await File.WriteAllTextAsync(siblingSentinel, "sibling").ConfigureAwait(false);

            File.Delete(mainSourcePath);
            var deletedSourceDestination = Path.Combine(sliceRoot, "output", "source-deleted.mcz");
            var deletedSourceResult = await controller.ExportMalodyFullPackageAsync(
                    tab.Id,
                    deletedSourceDestination)
                .ConfigureAwait(false);
            using var deletedSourceArchive = ZipFile.OpenRead(deletedSourceDestination);
            var deletedSourceMainEntries = deletedSourceArchive.Entries.Count(entry =>
                Path.GetFileName(entry.FullName).Equals(
                    Path.GetFileName(mainSourcePath),
                    StringComparison.OrdinalIgnoreCase));
            var deletedSourceSessionUnchanged =
                tab.Session.IsDirty == sessionDirtyBefore &&
                tab.Session.ChangeSerial == sessionSerialBefore &&
                string.Equals(
                    ChartStructuralFingerprint.Compute(tab.Session.Document),
                    sessionFingerprintBefore,
                    StringComparison.Ordinal) &&
                PathsEqual(tab.SourcePath, sourcePathBefore) &&
                PathsEqual(tab.WorkingPath, workingPathBefore);
            Require(
                evidence,
                !File.Exists(mainSourcePath) &&
                File.Exists(deletedSourceDestination) &&
                deletedSourceResult.MainChartEntryPath.Equals(
                    Path.GetFileName(mainSourcePath),
                    StringComparison.OrdinalIgnoreCase) &&
                deletedSourceMainEntries == 1 &&
                deletedSourceSessionUnchanged,
                "malody-full-package-open-tab-can-export-after-source-file-is-externally-removed",
                $"destination={File.Exists(deletedSourceDestination)}; mainEntries={deletedSourceMainEntries}; " +
                $"sessionUnchanged={deletedSourceSessionUnchanged}");

            var ownedSessionRoot = AvaloniaAppPaths.SessionScratchRoot;
            AvaloniaAppPaths.CleanupSessionScratchRoot();
            Require(
                evidence,
                !Directory.Exists(ownedSessionRoot) &&
                File.Exists(persistentSentinel) &&
                File.Exists(siblingSentinel),
                "avalonia-normal-shutdown-cleanup-removes-only-owned-session-scratch",
                $"ownedExists={Directory.Exists(ownedSessionRoot)}; " +
                $"persistent={File.Exists(persistentSentinel)}; sibling={File.Exists(siblingSentinel)}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "LUMACHART_AVALONIA_EDITING_ROOT",
                previousEditingRootOverride);
        }

        await VerifyMalodyFullPackageSafetyGatesAsync(evidence, runRoot).ConfigureAwait(false);
    }

    private static async Task VerifyMalodyFullPackageSafetyGatesAsync(
        SliceEvidence evidence,
        string runRoot)
    {
        var safetyRoot = Path.Combine(runRoot, "malody-full-package-safety");
        Directory.CreateDirectory(safetyRoot);
        await VerifyMalodyScratchPruningAsync(evidence, safetyRoot).ConfigureAwait(false);
        await VerifyMalodyReparseResourcesAsync(evidence, safetyRoot).ConfigureAwait(false);
        await VerifyMalodyAtomicDestinationAsync(evidence, safetyRoot).ConfigureAwait(false);
    }

    private static async Task VerifyMalodyScratchPruningAsync(
        SliceEvidence evidence,
        string safetyRoot)
    {
        var sourceRoot = Path.Combine(safetyRoot, "source-containing-managed-scratch");
        var sourcePath = Path.Combine(sourceRoot, "scratch-prune.mc");
        var audioPath = Path.Combine(sourceRoot, "scratch-prune.ogg");
        var sessionScratchRoot = Path.Combine(sourceRoot, "managed-scratch", "session_specs");
        var scratchSentinel = Path.Combine(sessionScratchRoot, "must-not-be-packaged.secret");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(sessionScratchRoot);
        byte[] audioBytes = [0x4F, 0x67, 0x67, 0x53, 0x50, 0x52, 0x55, 0x4E, 0x45];
        await File.WriteAllBytesAsync(audioPath, audioBytes).ConfigureAwait(false);
        await File.WriteAllTextAsync(scratchSentinel, "managed scratch sentinel").ConfigureAwait(false);
        var document = CreateMalodyExportSafetyDocument("Scratch prune", Path.GetFileName(audioPath));
        MalodyMcSerializer.Save(document, sourcePath);

        var destination = Path.Combine(safetyRoot, "scratch-prune-output", "scratch-prune.mcz");
        var result = await MalodyFullPackageExportService.ExportAsync(
                new MalodyFullPackageExportRequest(
                    document,
                    sourcePath,
                    destination,
                    sessionScratchRoot))
            .ConfigureAwait(false);
        using var archive = ZipFile.OpenRead(destination);
        var entryNames = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToArray();
        var scratchWasPruned =
            result.EntryCount == 2 &&
            entryNames.Length == 2 &&
            entryNames.Any(name => name.Equals("scratch-prune.mc", StringComparison.OrdinalIgnoreCase)) &&
            entryNames.Any(name => name.Equals("scratch-prune.ogg", StringComparison.OrdinalIgnoreCase)) &&
            entryNames.All(name => !name.Contains("managed-scratch", StringComparison.OrdinalIgnoreCase)) &&
            entryNames.All(name => !name.Contains("must-not-be-packaged", StringComparison.OrdinalIgnoreCase)) &&
            File.Exists(scratchSentinel) &&
            !Directory.EnumerateDirectories(
                    Path.Combine(sessionScratchRoot, "exports"),
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Any();
        Require(
            evidence,
            scratchWasPruned,
            "malody-full-package-prunes-managed-scratch-when-source-contains-staging",
            $"entries={string.Join(';', entryNames)}; sentinel={File.Exists(scratchSentinel)}; " +
            $"ownedExportDirectories={Directory.EnumerateDirectories(Path.Combine(sessionScratchRoot, "exports")).Count()}");
    }

    private static async Task VerifyMalodyReparseResourcesAsync(
        SliceEvidence evidence,
        string safetyRoot)
    {
        var targetRoot = Path.Combine(safetyRoot, "reparse-targets");
        Directory.CreateDirectory(targetRoot);

        var fileSourceRoot = Path.Combine(safetyRoot, "file-symlink-source");
        Directory.CreateDirectory(fileSourceRoot);
        var fileTarget = Path.Combine(targetRoot, "file-symlink-target.ogg");
        byte[] fileTargetBytes = [0x4F, 0x67, 0x67, 0x53, 0x46, 0x49, 0x4C, 0x45];
        await File.WriteAllBytesAsync(fileTarget, fileTargetBytes).ConfigureAwait(false);
        var fileLink = Path.Combine(fileSourceRoot, "linked-audio.ogg");
        var fileLinkDetail = string.Empty;
        var fileLinkCreated = false;
        var fileLinkPrivilegeUnavailable = false;
        try
        {
            File.CreateSymbolicLink(fileLink, fileTarget);
            fileLinkCreated = File.Exists(fileLink) &&
                              File.GetAttributes(fileLink).HasFlag(FileAttributes.ReparsePoint);
            fileLinkDetail = $"directValidationAvailable=True; directCoverage=True; " +
                             $"exists={File.Exists(fileLink)}; attributes={File.GetAttributes(fileLink)}";
        }
        catch (Exception exception)
        {
            fileLinkPrivilegeUnavailable = (exception.HResult & 0xFFFF) == 1314;
            fileLinkDetail = $"directValidationAvailable=False; directCoverage=False; " +
                             $"fallbackCoverage=managed-scratch-pruning; {exception.GetType().Name}; " +
                             $"hresult=0x{exception.HResult:X8}; " +
                             $"privilegeUnavailable={fileLinkPrivilegeUnavailable}; {exception.Message}";
        }

        try
        {
            Require(
                evidence,
                fileLinkCreated || fileLinkPrivilegeUnavailable,
                "malody-full-package-file-symlink-direct-validation-capability-and-coverage-are-explicitly-recorded",
                fileLinkDetail);
            var fallbackScratchRoot = Path.Combine(
                fileSourceRoot,
                "managed-scratch",
                "session_specs");
            var fallbackResource = Path.Combine(fallbackScratchRoot, "pruned-audio.ogg");
            var originalReference = Path.GetFileName(fileLink);
            var expectedReference = Path.GetFileName(fileLink);
            var exportScratchRoot = Path.Combine(safetyRoot, "file-symlink-scratch");
            if (!fileLinkCreated)
            {
                Directory.CreateDirectory(fallbackScratchRoot);
                await File.WriteAllBytesAsync(fallbackResource, fileTargetBytes).ConfigureAwait(false);
                originalReference = Path.GetRelativePath(fileSourceRoot, fallbackResource);
                expectedReference = Path.GetFileName(fallbackResource);
                exportScratchRoot = fallbackScratchRoot;
            }

            var fileChartPath = Path.Combine(fileSourceRoot, "file-symlink-or-pruned.mc");
            var fileDocument = CreateMalodyExportSafetyDocument(
                fileLinkCreated ? "File symlink" : "Explicitly pruned source resource",
                originalReference);
            MalodyMcSerializer.Save(fileDocument, fileChartPath);
            var fileDestination = Path.Combine(
                safetyRoot,
                "reparse-output",
                "file-symlink-or-pruned.mcz");
            await MalodyFullPackageExportService.ExportAsync(
                    new MalodyFullPackageExportRequest(
                        fileDocument,
                        fileChartPath,
                        fileDestination,
                        exportScratchRoot))
                .ConfigureAwait(false);
            var fileResult = await InspectExportedResourceAsync(
                    fileDestination,
                    Path.GetFileName(fileChartPath),
                    expectedReference,
                    fileTargetBytes)
                .ConfigureAwait(false);
            Require(
                evidence,
                fileResult.Passed,
                "malody-full-package-materializes-source-contained-resource-when-reparse-or-pruning-omits-staging-copy",
                $"mechanism={(fileLinkCreated ? "file-symlink" : "managed-scratch-pruning")}; " +
                $"directFileSymlinkCoverage={fileLinkCreated}; originalReference={originalReference}; " +
                fileResult.Detail);
        }
        finally
        {
            if (File.Exists(fileLink)) File.Delete(fileLink);
        }

        var junctionSourceRoot = Path.Combine(safetyRoot, "directory-junction-source");
        var junctionTargetRoot = Path.Combine(targetRoot, "junction-target-directory");
        Directory.CreateDirectory(junctionSourceRoot);
        Directory.CreateDirectory(junctionTargetRoot);
        var junctionAudioPath = Path.Combine(junctionTargetRoot, "junction-audio.ogg");
        byte[] junctionAudioBytes = [0x4F, 0x67, 0x67, 0x53, 0x4A, 0x55, 0x4E, 0x43];
        await File.WriteAllBytesAsync(junctionAudioPath, junctionAudioBytes).ConfigureAwait(false);
        var collisionDirectory = Path.Combine(junctionSourceRoot, Path.GetFileName(junctionAudioPath));
        var collisionSentinel = Path.Combine(collisionDirectory, "directory-content.sentinel");
        byte[] collisionSentinelBytes = [0x44, 0x49, 0x52, 0x2D, 0x4B, 0x45, 0x45, 0x50];
        Directory.CreateDirectory(collisionDirectory);
        await File.WriteAllBytesAsync(collisionSentinel, collisionSentinelBytes).ConfigureAwait(false);
        var junctionPath = Path.Combine(junctionSourceRoot, "junction-assets");
        var junctionCreation = await CreateWindowsJunctionAsync(junctionPath, junctionTargetRoot)
            .ConfigureAwait(false);
        try
        {
            Require(
                evidence,
                junctionCreation.Created,
                "malody-full-package-directory-junction-spec-created-real-reparse-point",
                junctionCreation.Detail);
            var junctionChartPath = Path.Combine(junctionSourceRoot, "directory-junction.mc");
            var originalReference = Path.Combine("junction-assets", Path.GetFileName(junctionAudioPath));
            var junctionDocument = CreateMalodyExportSafetyDocument(
                "Directory junction",
                originalReference);
            MalodyMcSerializer.Save(junctionDocument, junctionChartPath);
            var junctionDestination = Path.Combine(
                safetyRoot,
                "reparse-output",
                "directory-junction.mcz");
            await MalodyFullPackageExportService.ExportAsync(
                    new MalodyFullPackageExportRequest(
                        junctionDocument,
                        junctionChartPath,
                        junctionDestination,
                        Path.Combine(safetyRoot, "directory-junction-scratch")))
                .ConfigureAwait(false);
            var junctionResult = await InspectExportedResourceAsync(
                    junctionDestination,
                    Path.GetFileName(junctionChartPath),
                    "junction-audio_2.ogg",
                    junctionAudioBytes)
                .ConfigureAwait(false);
            Require(
                evidence,
                junctionResult.Passed,
                "malody-full-package-materializes-directory-junction-resource-instead-of-emitting-broken-reference",
                $"originalReference={originalReference}; {junctionResult.Detail}");

            var archiveSentinelName = $"{Path.GetFileName(collisionDirectory)}/{Path.GetFileName(collisionSentinel)}";
            byte[]? archivedSentinelBytes = null;
            using (var archive = ZipFile.OpenRead(junctionDestination))
            {
                var sentinelEntry = archive.Entries.SingleOrDefault(entry => entry.FullName.Replace('\\', '/').Equals(
                    archiveSentinelName,
                    StringComparison.OrdinalIgnoreCase));
                if (sentinelEntry != null)
                {
                    await using var sentinelStream = sentinelEntry.Open();
                    using var sentinelOutput = new MemoryStream();
                    await sentinelStream.CopyToAsync(sentinelOutput).ConfigureAwait(false);
                    archivedSentinelBytes = sentinelOutput.ToArray();
                }
            }
            var sourceSentinelBytes = await File.ReadAllBytesAsync(collisionSentinel).ConfigureAwait(false);
            Require(
                evidence,
                junctionResult.Passed &&
                archivedSentinelBytes != null &&
                archivedSentinelBytes.SequenceEqual(collisionSentinelBytes) &&
                sourceSentinelBytes.SequenceEqual(collisionSentinelBytes),
                "malody-full-package-directory-collision-uses-suffix-two-and-preserves-directory-content",
                $"resourceReference=junction-audio_2.ogg; directoryEntry={archiveSentinelName}; " +
                $"archivedSentinel={archivedSentinelBytes?.Length ?? -1}; sourceSentinel={sourceSentinelBytes.Length}");
        }
        finally
        {
            if (Directory.Exists(junctionPath)) Directory.Delete(junctionPath);
        }
    }

    private static async Task VerifyMalodyAtomicDestinationAsync(
        SliceEvidence evidence,
        string safetyRoot)
    {
        var sourceRoot = Path.Combine(safetyRoot, "atomic-source");
        Directory.CreateDirectory(sourceRoot);
        var sourcePath = Path.Combine(sourceRoot, "atomic.mc");
        var audioPath = Path.Combine(sourceRoot, "atomic.ogg");
        byte[] audioBytes = [0x4F, 0x67, 0x67, 0x53, 0x41, 0x54, 0x4F, 0x4D];
        await File.WriteAllBytesAsync(audioPath, audioBytes).ConfigureAwait(false);
        var document = CreateMalodyExportSafetyDocument("Atomic destination", Path.GetFileName(audioPath));
        MalodyMcSerializer.Save(document, sourcePath);
        var scratchRoot = Path.Combine(safetyRoot, "atomic-scratch");
        var outputRoot = Path.Combine(safetyRoot, "atomic-output");
        Directory.CreateDirectory(outputRoot);
        byte[] oldArchiveBytes = Encoding.UTF8.GetBytes("pre-existing archive must survive");

        static string[] TemporaryArchives(string folder) => Directory
            .EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".luma-export.tmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var canceledDestination = Path.Combine(outputRoot, "cancel-preserves-old.mcz");
        await File.WriteAllBytesAsync(canceledDestination, oldArchiveBytes).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var commitStageObserved = false;
        var temporaryArchiveObservedBeforeCommit = false;
        var cancelProgress = new InlineProgress<MalodyFullPackageExportProgress>(progress =>
        {
            if (!progress.Stage.Equals("正在提交谱包", StringComparison.Ordinal)) return;
            commitStageObserved = true;
            temporaryArchiveObservedBeforeCommit = TemporaryArchives(outputRoot).Length == 1;
            cancellation.Cancel();
        });
        var cancellationObserved = await ThrowsAsync<OperationCanceledException>(() =>
                MalodyFullPackageExportService.ExportAsync(
                    new MalodyFullPackageExportRequest(
                        document,
                        sourcePath,
                        canceledDestination,
                        scratchRoot),
                    cancelProgress,
                    cancellation.Token))
            .ConfigureAwait(false);
        var canceledOldPreserved =
            cancellationObserved &&
            commitStageObserved &&
            temporaryArchiveObservedBeforeCommit &&
            (await File.ReadAllBytesAsync(canceledDestination).ConfigureAwait(false))
                .SequenceEqual(oldArchiveBytes) &&
            TemporaryArchives(outputRoot).Length == 0;
        Require(
            evidence,
            canceledOldPreserved,
            "malody-full-package-cancellation-after-zip-validation-before-commit-preserves-old-target-and-cleans-temp-archive",
            $"afterZipValidationBeforeCommit={commitStageObserved}; " +
            $"temporaryArchiveObservedBeforeCommit={temporaryArchiveObservedBeforeCommit}; " +
            $"canceled={cancellationObserved}; commitBoundary=token-check-immediately-before-file-move; " +
            $"oldPreserved={(await File.ReadAllBytesAsync(canceledDestination).ConfigureAwait(false)).SequenceEqual(oldArchiveBytes)}; " +
            $"temporaryArchives={TemporaryArchives(outputRoot).Length}");

        var lockedDestination = Path.Combine(outputRoot, "failed-replace-preserves-old.mcz");
        await File.WriteAllBytesAsync(lockedDestination, oldArchiveBytes).ConfigureAwait(false);
        Exception? replacementFailure = null;
        await using (var destinationLock = new FileStream(
                         lockedDestination,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            try
            {
                await MalodyFullPackageExportService.ExportAsync(
                        new MalodyFullPackageExportRequest(
                            document,
                            sourcePath,
                            lockedDestination,
                            scratchRoot))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                replacementFailure = exception;
            }
        }

        var failedOldPreserved =
            replacementFailure != null &&
            (await File.ReadAllBytesAsync(lockedDestination).ConfigureAwait(false))
                .SequenceEqual(oldArchiveBytes) &&
            TemporaryArchives(outputRoot).Length == 0;
        Require(
            evidence,
            failedOldPreserved,
            "malody-full-package-failed-atomic-replace-preserves-old-target-and-cleans-temp-archive",
            $"failure={replacementFailure?.GetType().Name ?? "none"}; " +
            $"oldPreserved={(await File.ReadAllBytesAsync(lockedDestination).ConfigureAwait(false)).SequenceEqual(oldArchiveBytes)}; " +
            $"temporaryArchives={TemporaryArchives(outputRoot).Length}");

        var success = await MalodyFullPackageExportService.ExportAsync(
                new MalodyFullPackageExportRequest(
                    document,
                    sourcePath,
                    lockedDestination,
                    scratchRoot))
            .ConfigureAwait(false);
        var successResource = await InspectExportedResourceAsync(
                lockedDestination,
                Path.GetFileName(sourcePath),
                Path.GetFileName(audioPath),
                audioBytes)
            .ConfigureAwait(false);
        Require(
            evidence,
            success.ArchiveBytes > 0 &&
            !(await File.ReadAllBytesAsync(lockedDestination).ConfigureAwait(false)).SequenceEqual(oldArchiveBytes) &&
            TemporaryArchives(outputRoot).Length == 0 &&
            successResource.Passed,
            "malody-full-package-successfully-replaces-old-target-from-same-directory-temp-with-valid-archive",
            $"bytes={success.ArchiveBytes}; temporaryArchives={TemporaryArchives(outputRoot).Length}; " +
            successResource.Detail);
    }

    private static ChartDocument CreateMalodyExportSafetyDocument(
        string title,
        string audioReference) =>
        new()
        {
            Metadata = new ChartMetadata
            {
                Title = title,
                Artist = "Avalonia Specs",
                Creator = "Avalonia Specs",
                Version = "Safety",
                Bpm = 120,
                AudioFile = audioReference
            },
            KeyCount = 4,
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
            Notes =
            [
                new ChartNote
                {
                    Lane = 1,
                    Beat = BeatPosition.Preserve(1, 2, 8)
                }
            ]
        };

    private static async Task<(bool Passed, string Detail)> InspectExportedResourceAsync(
        string archivePath,
        string mainChartEntryName,
        string expectedReference,
        byte[] expectedResourceBytes)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var fileEntries = archive.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .ToArray();
            var mainEntry = fileEntries.Single(entry => entry.FullName.Replace('\\', '/').Equals(
                mainChartEntryName,
                StringComparison.OrdinalIgnoreCase));
            string mainText;
            await using (var mainStream = mainEntry.Open())
            using (var reader = new StreamReader(mainStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                mainText = await reader.ReadToEndAsync().ConfigureAwait(false);
            var exported = MalodyMcSerializer.Parse(mainText, mainChartEntryName, "Avalonia Specs");
            var resourceEntry = fileEntries.SingleOrDefault(entry => entry.FullName.Replace('\\', '/').Equals(
                expectedReference,
                StringComparison.OrdinalIgnoreCase));
            if (resourceEntry == null)
                return (false, $"reference={exported.Metadata.AudioFile}; missingEntry={expectedReference}");
            await using var resourceStream = resourceEntry.Open();
            using var resourceBytes = new MemoryStream();
            await resourceStream.CopyToAsync(resourceBytes).ConfigureAwait(false);
            var passed = exported.Metadata.AudioFile.Equals(
                             expectedReference,
                             StringComparison.OrdinalIgnoreCase) &&
                         resourceBytes.ToArray().SequenceEqual(expectedResourceBytes);
            return (
                passed,
                $"reference={exported.Metadata.AudioFile}; entry={resourceEntry.FullName}; bytes={resourceEntry.Length}");
        }
        catch (Exception exception)
        {
            return (false, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task<(bool Created, string Detail)> CreateWindowsJunctionAsync(
        string junctionPath,
        string targetPath)
    {
        if (!OperatingSystem.IsWindows()) return (false, "The Windows junction Spec requires Windows.");
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(Path.GetFullPath(junctionPath));
        startInfo.ArgumentList.Add(Path.GetFullPath(targetPath));
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Unable to start cmd.exe for the junction Spec.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        var created = process.ExitCode == 0 &&
                      Directory.Exists(junctionPath) &&
                      File.GetAttributes(junctionPath).HasFlag(FileAttributes.ReparsePoint);
        return (
            created,
            $"exit={process.ExitCode}; exists={Directory.Exists(junctionPath)}; " +
            $"attributes={(Directory.Exists(junctionPath) ? File.GetAttributes(junctionPath) : 0)}; " +
            $"stdout={output.Trim()}; stderr={error.Trim()}");
    }

    private static ChartNote SelectMovementTarget(ChartDocument document)
    {
        return document.Notes
            .Where(note => !note.IsDraft && !note.IsHold && note.Beat.Denominator > 1)
            .Select(note => new
            {
                Note = note,
                Expected = BasicChartEditingService.ApplyRelativeMove(
                    note.Beat,
                    new RelativeBeatMove(1, MoveDivision))
            })
            .Where(candidate => candidate.Expected.Numerator != 0 &&
                                GreatestCommonDivisor(
                                    candidate.Expected.Numerator,
                                    candidate.Expected.Denominator) > 1)
            .Where(candidate => !document.Notes.Any(other =>
                other.Id != candidate.Note.Id &&
                !other.IsDraft &&
                other.Lane == candidate.Note.Lane &&
                other.Beat.CompareTo(candidate.Expected) == 0))
            .OrderByDescending(candidate => candidate.Note.Beat.Denominator)
            .ThenBy(candidate => candidate.Note.Beat)
            .Select(candidate => candidate.Note)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The real MC fixture has no collision-free note that can discriminate rational-expression reduction.");
    }

    private static TimelineIndexBenchmarkEvidence RunTimelineIndexBenchmark()
    {
        var notes = new List<ChartNote>(BenchmarkNoteCount);
        for (var index = 0; index < BenchmarkNoteCount; index++)
        {
            var beat = BeatPosition.Preserve(index / 16, index % 16, 16);
            notes.Add(new ChartNote
            {
                Lane = index % 4,
                Beat = beat,
                EndBeat = index % 17 == 0
                    ? BasicChartEditingService.ApplyRelativeMove(beat, new RelativeBeatMove(8, 16))
                    : null
            });
        }

        var noteIndex = new TimelineNoteIndex();
        var buildStarted = Stopwatch.GetTimestamp();
        noteIndex.Rebuild(notes);
        var buildMilliseconds = Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds;
        var queryScratch = new List<ChartNote>(512);
        var hitScratch = new List<ChartNote>(32);

        for (var warmup = 0; warmup < 100; warmup++)
        {
            var minimum = warmup * 13.75 % 6_200;
            noteIndex.Query(minimum, minimum + 12, queryScratch);
            var target = notes[(warmup * 977) % notes.Count];
            _ = noteIndex.HitTest(target.Lane, target.Beat.Value, 0.001, hitScratch);
        }

        var querySamples = new double[BenchmarkSampleCount];
        long totalReturned = 0;
        var maximumReturned = 0;
        for (var sample = 0; sample < BenchmarkSampleCount; sample++)
        {
            var minimum = sample * 137.125 % 6_200;
            var started = Stopwatch.GetTimestamp();
            var returned = noteIndex.Query(minimum, minimum + 12, queryScratch);
            querySamples[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            totalReturned += returned;
            maximumReturned = Math.Max(maximumReturned, returned);
        }

        var hitSamples = new double[BenchmarkSampleCount];
        var hitCount = 0;
        var maximumHitCandidates = 0;
        for (var sample = 0; sample < BenchmarkSampleCount; sample++)
        {
            var target = notes[(sample * 7_919) % notes.Count];
            var started = Stopwatch.GetTimestamp();
            var hit = noteIndex.HitTest(target.Lane, target.Beat.Value, 0.001, hitScratch);
            hitSamples[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (hit != null) hitCount++;
            maximumHitCandidates = Math.Max(maximumHitCandidates, hitScratch.Count);
        }

        return new TimelineIndexBenchmarkEvidence
        {
            IndexedNotes = noteIndex.Count,
            BuildMilliseconds = buildMilliseconds,
            QueryMilliseconds = Summarize(querySamples),
            HitTestMilliseconds = Summarize(hitSamples),
            QuerySamples = BenchmarkSampleCount,
            HitTestSamples = BenchmarkSampleCount,
            HitCount = hitCount,
            TotalReturnedVisibleNotes = totalReturned,
            AverageReturnedVisibleNotes = totalReturned / (double)BenchmarkSampleCount,
            MaximumReturnedVisibleNotes = maximumReturned,
            MaximumVisitedNotes = maximumReturned,
            MaximumHitTestCandidates = maximumHitCandidates,
            MeasurementScope =
                "CPU-only TimelineNoteIndex query and hit-test latency. This is not GUI render-frame, " +
                "display-present, refresh-rate, or physical input-latency evidence."
        };
    }

    private static PercentileEvidence Summarize(double[] samples)
    {
        Array.Sort(samples);
        return new PercentileEvidence
        {
            Samples = samples.Length,
            P50 = Percentile(samples, 0.50),
            P95 = Percentile(samples, 0.95),
            P99 = Percentile(samples, 0.99),
            Maximum = samples[^1]
        };
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        var position = percentile * (ordered.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return ordered[lower];
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    private static bool HasExactExpression(BeatPosition actual, BeatPosition expected) =>
        actual.Bar == expected.Bar &&
        actual.Numerator == expected.Numerator &&
        actual.Denominator == expected.Denominator;

    private static int GreatestCommonDivisor(int left, int right)
    {
        left = Math.Abs(left);
        right = Math.Abs(right);
        while (right != 0) (left, right) = (right, left % right);
        return Math.Max(1, left);
    }

    private static string? ResolveChartReference(string chartPath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            return Path.IsPathRooted(reference)
                ? Path.GetFullPath(reference)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(chartPath)!, reference));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsInsideOrEqual(string candidate, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static async Task<bool> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private sealed class RecordingChartExportAudioTranscoder : IChartExportAudioTranscoder
    {
        public List<ChartExportAudioCall> Calls { get; } = [];

        public async Task ConvertToOggAsync(
            string sourceAudioPath,
            string destinationOggPath,
            double rate,
            bool preservePitch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new ChartExportAudioCall(
                Path.GetFullPath(sourceAudioPath),
                Path.GetFullPath(destinationOggPath),
                rate,
                preservePitch));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationOggPath)
                                      ?? throw new InvalidOperationException(
                                          "The fake audio output has no parent directory."));
            await File.WriteAllBytesAsync(
                    destinationOggPath,
                    Encoding.UTF8.GetBytes($"OggS|rate={rate:0.####}|preservePitch={preservePitch}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class LargeChartExportAudioTranscoder(int outputBytes) : IChartExportAudioTranscoder
    {
        public async Task ConvertToOggAsync(
            string sourceAudioPath,
            string destinationOggPath,
            double rate,
            bool preservePitch,
            CancellationToken cancellationToken = default)
        {
            _ = sourceAudioPath;
            _ = rate;
            _ = preservePitch;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationOggPath)
                                      ?? throw new InvalidOperationException(
                                          "The large fake audio output has no parent directory."));
            await using var output = new FileStream(
                destinationOggPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var random = new Random(0x4C554D41);
            var buffer = new byte[128 * 1024];
            var remaining = outputBytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(buffer.Length, remaining);
                random.NextBytes(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                remaining -= count;
            }
        }
    }

    private sealed record ChartExportAudioCall(
        string SourceAudioPath,
        string DestinationOggPath,
        double Rate,
        bool PreservePitch);

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> callback;

        public InlineProgress(Action<T> callback)
        {
            this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Report(T value) => callback(value);
    }

    private static bool IsNondecreasing(IEnumerable<DateTime> values)
    {
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext()) return true;
        var previous = enumerator.Current;
        while (enumerator.MoveNext())
        {
            if (enumerator.Current < previous) return false;
            previous = enumerator.Current;
        }
        return true;
    }

    private static bool IsNonincreasing(IEnumerable<DateTime> values)
    {
        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext()) return true;
        var previous = enumerator.Current;
        while (enumerator.MoveNext())
        {
            if (enumerator.Current > previous) return false;
            previous = enumerator.Current;
        }
        return true;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static async Task FinalizeSourceEvidenceAsync(
        SliceEvidence evidence,
        SourceEvidence source,
        string checkName,
        string description)
    {
        if (!File.Exists(source.Path)) return;
        source.Sha256After = await ComputeSha256Async(source.Path).ConfigureAwait(false);
        source.OriginalUnchanged = !string.IsNullOrWhiteSpace(source.Sha256Before) &&
                                   string.Equals(
                                       source.Sha256Before,
                                       source.Sha256After,
                                       StringComparison.OrdinalIgnoreCase);
        AddCheck(
            evidence,
            checkName,
            source.OriginalUnchanged,
            source.OriginalUnchanged
                ? $"{description} hash is unchanged."
                : $"{description} hash changed unexpectedly.",
            throwOnFailure: false);
        if (source.OriginalUnchanged) return;
        evidence.Status = "failed";
        evidence.Failure ??= $"{description} was modified.";
    }

    private static void Require(
        SliceEvidence evidence,
        bool condition,
        string name,
        string detail)
    {
        AddCheck(evidence, name, condition, detail, throwOnFailure: true);
    }

    private static void AddCheck(
        SliceEvidence evidence,
        string name,
        bool passed,
        string detail,
        bool throwOnFailure)
    {
        evidence.Checks.Add(new CheckEvidence { Name = name, Passed = passed, Detail = detail });
        if (!passed && throwOnFailure) throw new InvalidOperationException($"{name}: {detail}");
    }

    private static string ResolveOutputPath(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--output=", StringComparison.Ordinal))
                return args[index]["--output=".Length..];
            if (args[index].Equals("--output", StringComparison.Ordinal) && index + 1 < args.Length)
                return args[index + 1];
            if (!args[index].StartsWith("-", StringComparison.Ordinal)) return args[index];
        }

        return Path.Combine(
            Path.GetTempPath(),
            "LumaChart-Avalonia-Specs",
            $"slice-evidence-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private sealed record DisplayScaleRuntimeSpecResult(
        bool LayoutPassed,
        string LayoutDetail,
        bool FontPassed,
        string FontDetail);

    private class EmptyScreenDispatchProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_ScreenCount" => 0,
                "get_AllScreens" => Array.Empty<global::Avalonia.Platform.Screen>(),
                "RequestScreenDetails" => Task.FromResult(false),
                _ => null
            };
    }
}

internal sealed class SliceEvidence
{
    public string Schema { get; init; } = "lumachart.windows.avalonia.slice-specs.v2";
    public DateTimeOffset GeneratedUtc { get; set; }
    public string Status { get; set; } = "running";
    public RuntimeEnvironmentEvidence Environment { get; set; } = new();
    public SourceEvidence Source { get; set; } = new();
    public SourceEvidence OsuSource { get; set; } = new();
    public SourceEvidence MczSource { get; set; } = new();
    public string? IsolationRoot { get; set; }
    public string? IsolatedChartPath { get; set; }
    public List<CheckEvidence> Checks { get; } = [];
    public ChartStructureSummary? OriginalChart { get; set; }
    public ChartStructureSummary? MovedChart { get; set; }
    public ChartStructureSummary? ReimportedChart { get; set; }
    public ChartStructureSummary? ReopenedChart { get; set; }
    public ExactBeatMoveEvidence? ExactBeatMove { get; set; }
    public OsuImportEvidence? OsuImport { get; set; }
    public WorkspaceRecoveryEvidence? WorkspaceRecovery { get; set; }
    public SaveAsEvidence? SaveAs { get; set; }
    public ArchiveImportEvidence? ArchiveImport { get; set; }
    public LibraryEvidence? Library { get; set; }
    public TimelineIndexBenchmarkEvidence? IndexBenchmark { get; set; }
    public string? Failure { get; set; }
}

internal sealed class RuntimeEnvironmentEvidence
{
    public string OperatingSystem { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;
    public string ProcessArchitecture { get; set; } = string.Empty;
}

internal sealed class SourceEvidence
{
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? Sha256Before { get; set; }
    public string? Sha256After { get; set; }
    public bool OriginalUnchanged { get; set; }
}

internal sealed class CheckEvidence
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string Detail { get; set; } = string.Empty;
}

internal sealed class ExactBeatMoveEvidence
{
    public int SnapDivision { get; set; }
    public BeatExpression Before { get; set; } = new();
    public BeatExpression Expected { get; set; } = new();
    public BeatExpression Actual { get; set; } = new();
    public int MathematicalReductionDivisor { get; set; }
    public bool PreservedUnreducedExpression { get; set; }
}

internal sealed class OsuImportEvidence
{
    public string SourcePath { get; set; } = string.Empty;
    public string TimingImportMode { get; set; } = string.Empty;
    public int KeyCount { get; set; }
    public int NoteCount { get; set; }
    public int HoldNoteCount { get; set; }
    public int BpmEventCount { get; set; }
    public int ScrollEventCount { get; set; }
    public string? LocalizedAudioPath { get; set; }
    public string? LocalizedBackgroundPath { get; set; }
    public bool ResourcesLocalized { get; set; }
}

internal sealed class WorkspaceRecoveryEvidence
{
    public string SourcePath { get; set; } = string.Empty;
    public bool SourceReadableWonUnsynchronizedWorking { get; set; }
    public double CurrentBeatRestored { get; set; }
    public bool ExternalSourceChangeOverwritten { get; set; }
    public bool MissingSourceRetainedDirtyWorking { get; set; }
    public bool RestartedRoomBecameEditableRetainedLocal { get; set; }
    public int ClearedOnlineAttributionCount { get; set; }
    public bool ExplicitCloseDeletedCache { get; set; }
    public string CacheDirectory { get; set; } = string.Empty;
}

internal sealed class SaveAsEvidence
{
    public string OriginalSourcePath { get; set; } = string.Empty;
    public string OriginalSourceSha256Before { get; set; } = string.Empty;
    public string OriginalSourceSha256After { get; set; } = string.Empty;
    public string McDestinationPath { get; set; } = string.Empty;
    public string OsuDestinationPath { get; set; } = string.Empty;
    public bool TabRetargeted { get; set; }
    public string OriginalCacheDirectory { get; set; } = string.Empty;
    public string RestartedCacheDirectory { get; set; } = string.Empty;
    public bool OccupiedTargetRejected { get; set; }
    public string OccupiedTargetSha256Before { get; set; } = string.Empty;
    public string OccupiedTargetSha256After { get; set; } = string.Empty;
    public bool MissingSourceSavedAsOsu { get; set; }
    public string TimingImportMode { get; set; } = string.Empty;
    public string ReopenedOriginalCacheDirectory { get; set; } = string.Empty;
    public bool OriginalReopenedWithIndependentCache { get; set; }
}

internal sealed class ArchiveImportEvidence
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationRoot { get; set; } = string.Empty;
    public int ExtractedFileCount { get; set; }
    public long ExtractedBytes { get; set; }
    public int ChartPathCount { get; set; }
    public int SupportedChartCount { get; set; }
    public int UnsupportedChartCount { get; set; }
    public int BrokenChartCount { get; set; }
    public bool SafeDestinations { get; set; }
    public string SourceSha256AfterImport { get; set; } = string.Empty;
}

internal sealed class LibraryEvidence
{
    public int ScannedChartCount { get; set; }
    public int GroupCount { get; set; }
    public int ArchiveGroupChartCount { get; set; }
    public bool MalodyNumericFolderPromoted { get; set; }
    public int FolderSearchVisibleChartCount { get; set; }
    public int ChartSearchVisibleChartCount { get; set; }
    public bool TitleSortsPassed { get; set; }
    public bool AddedSortsPassed { get; set; }
}

internal sealed class BeatExpression
{
    public int Bar { get; set; }
    public int Numerator { get; set; }
    public int Denominator { get; set; }
    public string Text { get; set; } = string.Empty;

    public static BeatExpression From(BeatPosition beat) => new()
    {
        Bar = beat.Bar,
        Numerator = beat.Numerator,
        Denominator = beat.Denominator,
        Text = beat.ToString()
    };
}

internal sealed class TimelineIndexBenchmarkEvidence
{
    public int IndexedNotes { get; set; }
    public double BuildMilliseconds { get; set; }
    public PercentileEvidence QueryMilliseconds { get; set; } = new();
    public PercentileEvidence HitTestMilliseconds { get; set; } = new();
    public int QuerySamples { get; set; }
    public int HitTestSamples { get; set; }
    public int HitCount { get; set; }
    public long TotalReturnedVisibleNotes { get; set; }
    public double AverageReturnedVisibleNotes { get; set; }
    public int MaximumReturnedVisibleNotes { get; set; }
    public int MaximumVisitedNotes { get; set; }
    public int MaximumHitTestCandidates { get; set; }
    public string MeasurementScope { get; set; } = string.Empty;
}

internal sealed class PercentileEvidence
{
    public int Samples { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public double Maximum { get; set; }
}

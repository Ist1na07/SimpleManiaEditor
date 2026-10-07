using System.Globalization;
using System.Security.Cryptography;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.Verification;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsOsuInPlaceSaveSpecs
{
    private const int ExpectedKeyCount = 4;
    private const int ExpectedNoteCount = 2_468;
    private const int ExpectedHoldCount = 303;
    private const int ExpectedBpmCount = 2;
    private const int ExpectedScrollCount = 11;
    private const double MillisecondTolerance = 1.1;

    public static async Task<IReadOnlyList<WindowsOsuInPlaceSaveSpecResult>> RunAsync(
        string isolationRoot,
        string fixturePath,
        string expectedFixtureSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isolationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFixtureSha256);

        var root = Path.Combine(Path.GetFullPath(isolationRoot), "windows-osu-in-place-save");
        Directory.CreateDirectory(root);
        var results = new List<WindowsOsuInPlaceSaveSpecResult>();
        results.Add(await CheckAsync(
                "real-osu-in-place-preserve-milliseconds-save-close-reopen",
                () => VerifyScenarioAsync(
                    root,
                    fixturePath,
                    expectedFixtureSha256,
                    OsuNoteTimingImportMode.PreserveMilliseconds))
            .ConfigureAwait(false));
        results.Add(await CheckAsync(
                "real-osu-in-place-calibrated-save-close-reopen",
                () => VerifyScenarioAsync(
                    root,
                    fixturePath,
                    expectedFixtureSha256,
                    OsuNoteTimingImportMode.CalibrateCommonSubdivisions))
            .ConfigureAwait(false));
        results.Add(await CheckAsync(
                "real-osu-calibrated-view-model-save-roundtrip-uses-tab-import-mode",
                () => VerifyViewModelRoundTripModeAsync(
                    root,
                    fixturePath,
                    expectedFixtureSha256))
            .ConfigureAwait(false));
        results.Add(await CheckAsync(
                "real-osu-in-place-source-fixture-remains-immutable",
                async () =>
                {
                    var actual = await ComputeSha256Async(fixturePath).ConfigureAwait(false);
                    Require(
                        actual.Equals(expectedFixtureSha256, StringComparison.OrdinalIgnoreCase),
                        $"fixture hash changed: {actual}");
                    return $"sha256={actual}";
                })
            .ConfigureAwait(false));
        return results;
    }

    private static async Task<string> VerifyViewModelRoundTripModeAsync(
        string root,
        string fixturePath,
        string expectedFixtureSha256)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(fixturePath))
                              ?? throw new InvalidOperationException(
                                  "The real osu fixture has no source directory.");
        var scenarioRoot = Path.Combine(root, "view-model-calibrated-roundtrip");
        var libraryRoot = Path.Combine(scenarioRoot, "library");
        var projectRoot = Path.Combine(libraryRoot, "real-osu-isolated");
        var editingRoot = Path.Combine(scenarioRoot, "editing");
        Directory.CreateDirectory(projectRoot);
        CopyDirectory(sourceDirectory, projectRoot);
        var isolatedPath = Path.Combine(projectRoot, Path.GetFileName(fixturePath));
        Require(
            (await ComputeSha256Async(isolatedPath).ConfigureAwait(false))
            .Equals(expectedFixtureSha256, StringComparison.OrdinalIgnoreCase),
            "isolated view-model fixture hash mismatch");

        var controller = new AvaloniaWorkspaceController(
            libraryRoot,
            editingRoot,
            "AvaloniaOsuViewModelRoundTripSpecs");
        await controller.InitializeAsync().ConfigureAwait(false);
        var tab = await controller.OpenAsync(
                isolatedPath,
                OsuNoteTimingImportMode.CalibrateCommonSubdivisions)
            .ConfigureAwait(false);
        var target = SelectTarget(tab.Session.Document);
        var expectedVolume = target.Volume == 73 ? 74 : 73;
        tab.Session.Selection.Clear();
        tab.Session.Selection.Add(target.Id);
        _ = WindowsNotePropertyCommand.Apply(
            tab.Session,
            new WindowsNotePropertyFields(
                target.Beat.ToString(),
                target.EndBeat?.ToString() ?? string.Empty,
                (target.Lane + 1).ToString(CultureInfo.InvariantCulture),
                expectedVolume.ToString(CultureInfo.InvariantCulture),
                target.Sound ?? string.Empty,
                target.Group.ToString(CultureInfo.InvariantCulture)));

        var viewModel = new MainWindowViewModel(controller);
        var roundTrip = await viewModel.SaveAndVerifyAsync(tab.Id).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "view-model save roundtrip returned null");
        Require(roundTrip.StructurallyEqual,
            $"calibrated view-model roundtrip differed: before={roundTrip.BeforeSave.Sha256}; " +
            $"after={roundTrip.AfterReload.Sha256}");
        Require(
            viewModel.Status.Contains("结构化复核通过", StringComparison.Ordinal),
            $"view-model status={viewModel.Status}");
        Require(
            tab.OsuTimingImportMode == OsuNoteTimingImportMode.CalibrateCommonSubdivisions,
            $"tab mode changed to {tab.OsuTimingImportMode}");
        Require(
            (await ComputeSha256Async(fixturePath).ConfigureAwait(false))
            .Equals(expectedFixtureSha256, StringComparison.OrdinalIgnoreCase),
            "real fixture changed during view-model roundtrip");

        return $"mode={tab.OsuTimingImportMode}; before={roundTrip.BeforeSave.Sha256}; " +
               $"after={roundTrip.AfterReload.Sha256}; status={viewModel.Status}";
    }

    private static async Task<string> VerifyScenarioAsync(
        string root,
        string fixturePath,
        string expectedFixtureSha256,
        OsuNoteTimingImportMode importMode)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(fixturePath))
                              ?? throw new InvalidOperationException(
                                  "The real osu fixture has no source directory.");
        var scenarioRoot = Path.Combine(root, importMode.ToString());
        var libraryRoot = Path.Combine(scenarioRoot, "library");
        var projectRoot = Path.Combine(libraryRoot, "real-osu-isolated");
        var editingRoot = Path.Combine(scenarioRoot, "editing");
        Directory.CreateDirectory(projectRoot);
        CopyDirectory(sourceDirectory, projectRoot);
        var isolatedPath = Path.Combine(projectRoot, Path.GetFileName(fixturePath));

        var isolatedHashBefore = await ComputeSha256Async(isolatedPath).ConfigureAwait(false);
        Require(
            isolatedHashBefore.Equals(expectedFixtureSha256, StringComparison.OrdinalIgnoreCase),
            $"isolated copy hash mismatch: {isolatedHashBefore}");
        var sourceTreeBefore = await CaptureFileTreeAsync(projectRoot).ConfigureAwait(false);

        var controller = new AvaloniaWorkspaceController(
            libraryRoot,
            editingRoot,
            "AvaloniaOsuInPlaceSpecs");
        await controller.InitializeAsync().ConfigureAwait(false);
        var tab = await controller.OpenAsync(isolatedPath, importMode).ConfigureAwait(false);
        Require(tab.Format == DesktopChartFormat.OsuMania, $"format={tab.Format}");
        Require(tab.OsuTimingImportMode == importMode, $"mode={tab.OsuTimingImportMode}");
        RequireExpectedStructure(tab.Session.Document);
        RequireReferencedResources(projectRoot, tab.Session.Document);

        var beforeFingerprint = ChartStructuralFingerprint.Compute(tab.Session.Document);
        var beforeSerial = tab.Session.ChangeSerial;
        var target = SelectTarget(tab.Session.Document);
        var targetStartMilliseconds = new TimingMap(tab.Session.Document.Timing)
            .BeatToMilliseconds(target.Beat);
        var targetEndMilliseconds = target.EndBeat is BeatPosition endBeat
            ? new TimingMap(tab.Session.Document.Timing).BeatToMilliseconds(endBeat)
            : (double?)null;
        var originalVolume = target.Volume;
        var expectedVolume = originalVolume == 73 ? 74 : 73;
        Require(expectedVolume != originalVolume, "the evidence edit is not discriminating");
        var fields = new WindowsNotePropertyFields(
            target.Beat.ToString(),
            target.EndBeat?.ToString() ?? string.Empty,
            (target.Lane + 1).ToString(CultureInfo.InvariantCulture),
            expectedVolume.ToString(CultureInfo.InvariantCulture),
            target.Sound ?? string.Empty,
            target.Group.ToString(CultureInfo.InvariantCulture));

        tab.Session.Selection.Clear();
        tab.Session.Selection.Add(target.Id);
        var applied = WindowsNotePropertyCommand.Apply(tab.Session, fields);
        Require(applied.Volume == expectedVolume, $"applied volume={applied.Volume}");
        Require(tab.Session.IsDirty, "the edited osu tab was not marked dirty");
        Require(
            tab.Session.ChangeSerial == beforeSerial + 1,
            $"edit serial={tab.Session.ChangeSerial}; expected={beforeSerial + 1}");
        var editedFingerprint = ChartStructuralFingerprint.Compute(tab.Session.Document);
        Require(
            !editedFingerprint.Equals(beforeFingerprint, StringComparison.Ordinal),
            "the property edit did not change the structural fingerprint");

        Require(tab.Session.Undo(), "undo returned false");
        Require(
            ChartStructuralFingerprint.Compute(tab.Session.Document)
                .Equals(beforeFingerprint, StringComparison.Ordinal),
            "undo did not restore the exact pre-edit document");
        Require(tab.Session.Redo(), "redo returned false");
        Require(
            ChartStructuralFingerprint.Compute(tab.Session.Document)
                .Equals(editedFingerprint, StringComparison.Ordinal),
            "redo did not restore the exact edited document");

        var save = await controller.SaveAsync(tab.Id).ConfigureAwait(false);
        Require(save.Saved, $"save status={save.Status}; issues={save.Issues.Count}");
        Require(!tab.Session.IsDirty, "successful in-place save left the tab dirty");
        Require(
            Path.GetFullPath(tab.SourcePath).Equals(
                Path.GetFullPath(isolatedPath),
                StringComparison.OrdinalIgnoreCase),
            $"source path retargeted to {tab.SourcePath}");
        Require(tab.Format == DesktopChartFormat.OsuMania, $"format changed to {tab.Format}");
        Require(tab.OsuTimingImportMode == importMode, $"mode changed to {tab.OsuTimingImportMode}");

        var savedText = await File.ReadAllTextAsync(isolatedPath).ConfigureAwait(false);
        Require(
            savedText.StartsWith("osu file format v14", StringComparison.Ordinal),
            "the in-place output is not an osu file format v14 document");
        var isolatedHashAfter = await ComputeSha256Async(isolatedPath).ConfigureAwait(false);
        Require(
            !isolatedHashAfter.Equals(isolatedHashBefore, StringComparison.OrdinalIgnoreCase),
            "the isolated source bytes did not change after the discriminating edit");

        var independentlyParsed = OsuManiaSerializer.Load(isolatedPath, importMode);
        RequireExpectedStructure(independentlyParsed);
        RequireReferencedResources(projectRoot, independentlyParsed);
        RequireEditedNote(
            independentlyParsed,
            target.Lane,
            target.IsHold,
            targetStartMilliseconds,
            targetEndMilliseconds,
            expectedVolume,
            "independent parser");

        await controller.PersistAsync().ConfigureAwait(false);
        var restoredController = new AvaloniaWorkspaceController(
            libraryRoot,
            editingRoot,
            "AvaloniaOsuInPlaceSpecs");
        await restoredController.InitializeAsync().ConfigureAwait(false);
        Require(restoredController.Tabs.Count == 1, $"restored tabs={restoredController.Tabs.Count}");
        var restored = restoredController.Tabs.Single();
        Require(restored.Format == DesktopChartFormat.OsuMania, $"restored format={restored.Format}");
        Require(restored.OsuTimingImportMode == importMode, $"restored mode={restored.OsuTimingImportMode}");
        Require(!restored.Session.IsDirty, "restored saved tab is dirty");
        Require(
            Path.GetFullPath(restored.SourcePath).Equals(
                Path.GetFullPath(isolatedPath),
                StringComparison.OrdinalIgnoreCase),
            $"restored source={restored.SourcePath}");
        RequireEditedNote(
            restored.Session.Document,
            target.Lane,
            target.IsHold,
            targetStartMilliseconds,
            targetEndMilliseconds,
            expectedVolume,
            "restored workspace");

        Require(
            await restoredController.CloseAsync(restored.Id, discardUnsavedChanges: false)
                .ConfigureAwait(false),
            "the saved restored tab could not be closed");
        var reopenedController = new AvaloniaWorkspaceController(
            libraryRoot,
            editingRoot,
            "AvaloniaOsuInPlaceSpecs");
        await reopenedController.InitializeAsync().ConfigureAwait(false);
        Require(reopenedController.Tabs.Count == 0, "the explicitly closed tab returned from cache");
        var reopened = await reopenedController.OpenAsync(isolatedPath, importMode).ConfigureAwait(false);
        RequireExpectedStructure(reopened.Session.Document);
        Require(!reopened.Session.IsDirty, "freshly reopened saved osu tab is dirty");
        Require(reopened.OsuTimingImportMode == importMode, $"reopened mode={reopened.OsuTimingImportMode}");
        RequireEditedNote(
            reopened.Session.Document,
            target.Lane,
            target.IsHold,
            targetStartMilliseconds,
            targetEndMilliseconds,
            expectedVolume,
            "fresh reopen");
        RequireReferencedResources(reopened.CacheDirectory, reopened.Session.Document);

        var sourceTreeAfter = await CaptureFileTreeAsync(projectRoot).ConfigureAwait(false);
        Require(
            TreesEqualExcept(sourceTreeBefore, sourceTreeAfter, Path.GetFileName(isolatedPath)),
            "an in-place save changed a sibling source resource");
        Require(
            !Directory.EnumerateFiles(
                    scenarioRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Any(path =>
                    path.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)),
            "atomic-save scratch files remain in the isolated scenario");
        var fixtureHashAfter = await ComputeSha256Async(fixturePath).ConfigureAwait(false);
        Require(
            fixtureHashAfter.Equals(expectedFixtureSha256, StringComparison.OrdinalIgnoreCase),
            $"real fixture hash changed: {fixtureHashAfter}");

        return
            $"mode={importMode}; source={Path.GetFileName(isolatedPath)}; " +
            $"notes={independentlyParsed.Notes.Count}; ln={independentlyParsed.Notes.Count(note => note.IsHold)}; " +
            $"editedLane={target.Lane + 1}; editedMs={targetStartMilliseconds:F3}; " +
            $"volume={originalVolume}->{expectedVolume}; sha256={isolatedHashAfter}";
    }

    private static ChartNote SelectTarget(ChartDocument document)
    {
        var map = new TimingMap(document.Timing);
        return document.Notes
            .Where(note =>
                !note.IsDraft &&
                string.IsNullOrWhiteSpace(note.Sound) &&
                double.IsFinite(map.BeatToMilliseconds(note.Beat)))
            .OrderBy(note => note.Beat)
            .ThenBy(note => note.Lane)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The real osu fixture has no safe Note for an in-place property edit.");
    }

    private static void RequireEditedNote(
        ChartDocument document,
        int lane,
        bool isHold,
        double startMilliseconds,
        double? endMilliseconds,
        int expectedVolume,
        string phase)
    {
        var map = new TimingMap(document.Timing);
        var matches = document.Notes.Where(note =>
                !note.IsDraft &&
                note.Lane == lane &&
                note.IsHold == isHold &&
                note.Volume == expectedVolume &&
                Math.Abs(map.BeatToMilliseconds(note.Beat) - startMilliseconds) <=
                MillisecondTolerance &&
                EndMatches(map, note.EndBeat, endMilliseconds))
            .ToArray();
        Require(
            matches.Length == 1,
            $"{phase} edited-note matches={matches.Length}; lane={lane + 1}; " +
            $"ms={startMilliseconds:F3}; volume={expectedVolume}");
    }

    private static bool EndMatches(
        TimingMap map,
        BeatPosition? actualEndBeat,
        double? expectedEndMilliseconds)
    {
        if (!expectedEndMilliseconds.HasValue) return !actualEndBeat.HasValue;
        return actualEndBeat.HasValue &&
               Math.Abs(map.BeatToMilliseconds(actualEndBeat.Value) -
                        expectedEndMilliseconds.Value) <= MillisecondTolerance;
    }

    private static void RequireExpectedStructure(ChartDocument document)
    {
        Require(document.KeyCount == ExpectedKeyCount, $"keys={document.KeyCount}");
        Require(document.Notes.Count == ExpectedNoteCount, $"notes={document.Notes.Count}");
        Require(
            document.Notes.Count(note => note.IsHold) == ExpectedHoldCount,
            $"ln={document.Notes.Count(note => note.IsHold)}");
        Require(
            document.Timing.Count(timing => timing.Bpm.HasValue) == ExpectedBpmCount,
            $"bpm={document.Timing.Count(timing => timing.Bpm.HasValue)}");
        Require(
            document.Timing.Count(timing => timing.Effect == TimingEffectKind.Scroll) ==
            ExpectedScrollCount,
            $"scroll={document.Timing.Count(timing => timing.Effect == TimingEffectKind.Scroll)}");
    }

    private static void RequireReferencedResources(string root, ChartDocument document)
    {
        RequireReferencedResource(root, document.Metadata.AudioFile, required: true, "audio");
        RequireReferencedResource(root, document.Metadata.BackgroundFile, required: false, "background");
    }

    private static void RequireReferencedResource(
        string root,
        string? reference,
        bool required,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            Require(!required, $"{kind} reference is empty");
            return;
        }

        var safeName = Path.GetFileName(reference);
        Require(
            safeName.Equals(reference, StringComparison.Ordinal),
            $"{kind} reference is not a flat safe name: {reference}");
        var path = Path.Combine(root, safeName);
        Require(File.Exists(path), $"{kind} resource is missing: {path}");
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destinationRoot,
                Path.GetRelativePath(sourceRoot, directory)));
        }

        foreach (var sourcePath in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(
                destinationRoot,
                Path.GetRelativePath(sourceRoot, sourcePath));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static async Task<Dictionary<string, string>> CaptureFileTreeAsync(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            result[Path.GetRelativePath(root, path)] =
                await ComputeSha256Async(path).ConfigureAwait(false);
        }

        return result;
    }

    private static bool TreesEqualExcept(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after,
        string changedRelativePath)
    {
        var beforeRetained = before
            .Where(pair => !pair.Key.Equals(changedRelativePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var afterRetained = after
            .Where(pair => !pair.Key.Equals(changedRelativePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return beforeRetained.Length == afterRetained.Length &&
               beforeRetained.Zip(afterRetained).All(pair =>
                   pair.First.Key.Equals(pair.Second.Key, StringComparison.OrdinalIgnoreCase) &&
                   pair.First.Value.Equals(pair.Second.Value, StringComparison.OrdinalIgnoreCase));
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

    private static async Task<WindowsOsuInPlaceSaveSpecResult> CheckAsync(
        string name,
        Func<Task<string>> verify)
    {
        try
        {
            return new WindowsOsuInPlaceSaveSpecResult(
                name,
                true,
                await verify().ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return new WindowsOsuInPlaceSaveSpecResult(name, false, exception.ToString());
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsOsuInPlaceSaveSpecResult(
    string Name,
    bool Passed,
    string Detail);

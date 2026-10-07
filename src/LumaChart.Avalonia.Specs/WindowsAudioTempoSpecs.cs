using System.Security.Cryptography;
using System.Runtime.InteropServices;
using LumaChart.Application.Audio;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using NAudio.Wave;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsAudioTempoSpecs
{
    private const string ResoundAudio = @"D:\osu!\Songs\Resound\0\Resound - Nhato.ogg";
    private const string PositiveParadeAudio = @"D:\osu!\Songs\_song_38474\0\1770113980.ogg";
    private const string Bpm175Audio = @"D:\osu!\Songs\I_Can_Fly_In_The_Universe\1776979146.ogg";

    public static IReadOnlyList<WindowsAudioTempoSpecResult> Run(string isolationRoot)
    {
        Directory.CreateDirectory(isolationRoot);
        var results = new List<WindowsAudioTempoSpecResult>();
        Check(results, "audio-import-copies-deduplicates-and-avoids-eventual-collisions", () =>
            VerifyAudioImport(isolationRoot));
        Check(results, "audio-reference-assignment-enforces-metadata-permission-and-one-undo", VerifyAudioAssignment);
        Check(results, "audio-import-workflow-transcodes-and-commits-one-metadata-mutation", () =>
            VerifyAudioImportWorkflowSuccess(isolationRoot));
        Check(results, "audio-import-workflow-contains-codec-failure-and-cleans-scratch", () =>
            VerifyAudioImportWorkflowCodecFailure(isolationRoot));
        Check(results, "audio-import-workflow-cancels-on-tab-change-and-removes-created-copy", () =>
            VerifyAudioImportWorkflowTabChange(isolationRoot));
        Check(results, "audio-import-workflow-rolls-back-created-copy-after-late-permission-revocation", () =>
            VerifyAudioImportWorkflowPermissionRevocation(isolationRoot));
        Check(results, "audio-and-tempo-ui-boundaries-match-wpf-catch-all-contract",
            VerifyAudioAndTempoUiFailureBoundaries);
        Check(results, "audio-library-creation-distinguishes-cancel-denial-and-failure",
            VerifyAudioLibraryCreationCompletionStatus);
        Check(results, "audio-resolver-prefers-unsaved-working-cache-asset", () =>
            VerifyWorkingAudioResolution(isolationRoot).GetAwaiter().GetResult());
        Check(results, "metadata-offset-is-atomic-and-requires-timing-permission", VerifyMetadataOffsetPermission);
        Check(results, "single-tempo-apply-is-stale-safe-permission-aware-and-undoable", VerifySingleTempoApply);
        Check(results, "variable-tempo-plan-counts-managed-scroll-boundaries", VerifyTimelinePlan);
        Check(results, "variable-tempo-apply-preserves-effects-and-uses-one-undo", VerifyTimelineApply);
        Check(results, "variable-tempo-scroll-compensation-is-deterministic-and-undoable", VerifyScrollCompensation);
        Check(results, "variable-tempo-apply-rejects-stale-and-owner-locked-plans", VerifyTimelineGuards);
        Check(
            results,
            "variable-tempo-apply-rejects-late-timing-permission-revocation",
            VerifyTimelinePermissionRevocation);
        Check(results, "windows-beat-analyzer-rejects-silence-and-honors-cancellation", () =>
            VerifyAnalyzerFailureBoundaries(isolationRoot));
        Check(results, "windows-beat-analyzer-caps-ogg-eof-and-keeps-ui-cancelable",
            VerifyAnalyzerResponsivenessBoundaries);
        Check(results, "windows-real-resound-single-tempo-detects-140-without-source-mutation", VerifyRealResound);
        Check(results, "windows-real-positive-parade-detects-four-authored-tempo-segments", VerifyRealPositiveParade);
        Check(results, "windows-real-long-track-retains-175-bpm-calibration", VerifyRealBpm175);
        return results;
    }

    private static string VerifyAudioImport(string isolationRoot)
    {
        var root = FreshDirectory(isolationRoot, "audio-import");
        var external = Path.Combine(root, "external");
        var working = Path.Combine(root, "working");
        var eventual = Path.Combine(root, "eventual");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(working);
        Directory.CreateDirectory(eventual);
        var source = Path.Combine(external, "song.wav");
        File.WriteAllBytes(source, [1, 2, 3, 4, 5, 6]);
        File.WriteAllBytes(Path.Combine(eventual, "song.wav"), [9, 9, 9]);
        var sourceHash = Sha256(source);
        var chartPath = Path.Combine(working, "working.mc");
        var eventualChart = Path.Combine(eventual, "source.mc");

        var first = ChartAssetImportService.ImportAudio(chartPath, source, eventualChart);
        Require(first.Created && first.FileName == "song_1.wav", "Eventual-source collision was not avoided.");
        Require(File.ReadAllBytes(first.DestinationPath).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }),
            "Imported audio bytes changed.");
        var second = ChartAssetImportService.ImportAudio(chartPath, source, eventualChart);
        Require(!second.Created && second.FileName == first.FileName,
            "Identical audio import was not deduplicated.");
        Require(Sha256(source) == sourceHash, "External source audio changed during import.");
        return $"file={first.FileName}; createdFirst={first.Created}; createdSecond={second.Created}; sourceUnchanged=True";
    }

    private static string VerifyAudioAssignment()
    {
        var session = SessionWithDocument(CreateDocument());
        session.CanEditMetadata = false;
        var denied = ChartMetadataEditingService.SetAudioFile(session, "song.ogg");
        Require(denied.Status == ChartMetadataApplyStatus.Denied && session.ChangeSerial == 0,
            "Denied audio assignment mutated the document.");
        session.CanEditMetadata = true;
        var applied = ChartMetadataEditingService.SetAudioFile(session, "song.ogg");
        Require(applied.Status == ChartMetadataApplyStatus.Applied &&
                session.Document.Metadata.AudioFile == "song.ogg" && session.ChangeSerial == 1,
            applied.Message);
        Require(session.Undo() && session.Document.Metadata.AudioFile.Length == 0,
            "Audio assignment was not one undo step.");
        var unsafeResult = false;
        try { ChartMetadataEditingService.SetAudioFile(session, "../escape.ogg"); }
        catch (ArgumentException) { unsafeResult = true; }
        Require(unsafeResult, "Unsafe relative audio reference was accepted.");
        return "metadataDenied=True; assigned=song.ogg; oneUndo=True; traversalRejected=True";
    }

    private static string VerifyAudioImportWorkflowSuccess(string isolationRoot)
    {
        var root = FreshDirectory(isolationRoot, "audio-import-workflow-success");
        var external = Path.Combine(root, "external");
        var working = Path.Combine(root, "working");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(working);
        var source = Path.Combine(external, "song.mp3");
        File.WriteAllBytes(source, [1, 2, 3, 4, 5, 6]);
        var sourceHash = Sha256(source);
        var progressReported = false;
        var workflow = new WindowsChartAudioImportWorkflow(
            (input, output, progress, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(input == source, "Workflow passed the wrong source to the transcoder.");
                File.WriteAllBytes(output, [0x4F, 0x67, 0x67, 0x53, 7, 8, 9]);
                progress?.Report(1);
                progressReported = true;
                return Task.CompletedTask;
            },
            ChartAssetImportService.ImportAudio);
        var session = SessionWithDocument(CreateDocument());

        var result = workflow.RunAsync(
                session,
                Path.Combine(working, "working.mc"),
                sourceChartPath: null,
                source,
                convertToOgg: true,
                isCurrentContext: () => true)
            .GetAwaiter()
            .GetResult();

        Require(
            result.Status == WindowsChartAudioImportStatus.Imported &&
            result.Asset is { Created: true, FileName: "song.ogg" } &&
            result.MetadataApply?.Status == ChartMetadataApplyStatus.Applied,
            $"Unexpected workflow result: {result.Status}.");
        var imported = result.Asset
                       ?? throw new InvalidOperationException(
                           "Successful workflow did not return its imported asset.");
        Require(
            session.Document.Metadata.AudioFile == "song.ogg" &&
            session.ChangeSerial == 1,
            "Successful workflow did not commit exactly one metadata mutation.");
        Require(
            File.ReadAllBytes(imported.DestinationPath)
                .SequenceEqual(new byte[] { 0x4F, 0x67, 0x67, 0x53, 7, 8, 9 }),
            "Converted audio bytes were not imported.");
        Require(
            progressReported &&
            !Directory.EnumerateFiles(working, ".luma-audio-import-*").Any() &&
            Sha256(source) == sourceHash,
            "Successful workflow left scratch files or changed its source.");
        return "status=Imported; file=song.ogg; serial=1; scratch=0; sourceUnchanged=True";
    }

    private static string VerifyAudioImportWorkflowCodecFailure(string isolationRoot)
    {
        var root = FreshDirectory(isolationRoot, "audio-import-workflow-codec-failure");
        var external = Path.Combine(root, "external");
        var working = Path.Combine(root, "working");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(working);
        var source = Path.Combine(external, "broken.m4a");
        File.WriteAllBytes(source, [10, 20, 30]);
        var workflow = new WindowsChartAudioImportWorkflow(
            (_, output, _, _) =>
            {
                File.WriteAllBytes(output, [0x4F, 0x67, 0x67, 0x53]);
                throw new COMException("codec rejected the stream", unchecked((int)0x80004005));
            },
            ChartAssetImportService.ImportAudio);
        var session = SessionWithDocument(CreateDocument());

        var result = workflow.RunAsync(
                session,
                Path.Combine(working, "working.mc"),
                sourceChartPath: null,
                source,
                convertToOgg: true,
                isCurrentContext: () => true)
            .GetAwaiter()
            .GetResult();

        Require(
            result.Status == WindowsChartAudioImportStatus.Failed &&
            result.Error is COMException,
            "Codec COM failure escaped or was misclassified.");
        Require(
            session.Document.Metadata.AudioFile.Length == 0 &&
            session.ChangeSerial == 0 &&
            !Directory.EnumerateFiles(working, ".luma-audio-import-*").Any(),
            "Codec failure mutated metadata or left conversion scratch.");
        return "status=Failed; error=COMException; serial=0; scratch=0";
    }

    private static string VerifyAudioImportWorkflowTabChange(string isolationRoot)
    {
        var root = FreshDirectory(isolationRoot, "audio-import-workflow-tab-change");
        var external = Path.Combine(root, "external");
        var working = Path.Combine(root, "working");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(working);
        var source = Path.Combine(external, "song.wav");
        File.WriteAllBytes(source, [1, 3, 5, 7]);
        var workflow = new WindowsChartAudioImportWorkflow(
            (_, _, _, _) => Task.CompletedTask,
            ChartAssetImportService.ImportAudio);
        var session = SessionWithDocument(CreateDocument());

        var result = workflow.RunAsync(
                session,
                Path.Combine(working, "working.mc"),
                sourceChartPath: null,
                source,
                convertToOgg: false,
                isCurrentContext: () => false)
            .GetAwaiter()
            .GetResult();

        Require(
            result.Status == WindowsChartAudioImportStatus.Canceled &&
            session.Document.Metadata.AudioFile.Length == 0 &&
            session.ChangeSerial == 0 &&
            !File.Exists(Path.Combine(working, "song.wav")),
            "Tab-change cancellation retained a copied asset or changed metadata.");
        return "status=Canceled; serial=0; createdCopyExists=False";
    }

    private static string VerifyAudioImportWorkflowPermissionRevocation(string isolationRoot)
    {
        var root = FreshDirectory(isolationRoot, "audio-import-workflow-permission-revocation");
        var external = Path.Combine(root, "external");
        var working = Path.Combine(root, "working");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(working);
        var source = Path.Combine(external, "song.flac");
        File.WriteAllBytes(source, [2, 4, 6, 8]);
        var session = SessionWithDocument(CreateDocument());
        var workflow = new WindowsChartAudioImportWorkflow(
            (_, _, _, _) => Task.CompletedTask,
            (chartPath, sourcePath, eventualChartPath, preferredFileName) =>
            {
                var imported = ChartAssetImportService.ImportAudio(
                    chartPath,
                    sourcePath,
                    eventualChartPath,
                    preferredFileName);
                session.CanEditMetadata = false;
                return imported;
            });

        var result = workflow.RunAsync(
                session,
                Path.Combine(working, "working.mc"),
                sourceChartPath: null,
                source,
                convertToOgg: false,
                isCurrentContext: () => true)
            .GetAwaiter()
            .GetResult();

        Require(
            result.Status == WindowsChartAudioImportStatus.Denied &&
            result.MetadataApply?.Status == ChartMetadataApplyStatus.Denied &&
            session.Document.Metadata.AudioFile.Length == 0 &&
            session.ChangeSerial == 0 &&
            !File.Exists(Path.Combine(working, "song.flac")),
            "Late permission revocation retained a copied asset or partially changed metadata.");
        return "status=Denied; serial=0; createdCopyExists=False";
    }

    private static string VerifyAudioAndTempoUiFailureBoundaries()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var avaloniaSource = File.ReadAllText(
            Path.Combine(projectRoot, "LumaChart.Avalonia", "MainWindow.cs"));

        RequireCatchAll(
            avaloniaSource,
            "private async Task RunSingleTempoAnalysisAsync()",
            "private Task StartVariableTempoAnalysisAsync()",
            "Avalonia single-tempo analysis");
        RequireCatchAll(
            avaloniaSource,
            "private async Task RunVariableTempoAnalysisAsync()",
            "private bool TryBeginTempoAnalysis(",
            "Avalonia variable-tempo analysis");
        RequireCatchAll(
            avaloniaSource,
            "private async Task CreateChartFromAudioPathAsync(string selectedPath)",
            "private Task<string?> ShowAudioImportNamePromptAsync(",
            "Avalonia audio-chart creation");
        Require(
            avaloniaSource.Contains(
                "--evidence-audio-permission-revoke-delay-ms",
                StringComparison.Ordinal) &&
            avaloniaSource.Contains(
                "Evidence audio-permission revocation requires a direct evidence chart.",
                StringComparison.Ordinal) &&
            avaloniaSource.Contains(
                "tab.Session.CanEditMetadata = false;",
                StringComparison.Ordinal) &&
            avaloniaSource.Contains(
                "--evidence-second-chart",
                StringComparison.Ordinal) &&
            avaloniaSource.Contains(
                "A second evidence chart requires the primary direct evidence chart.",
                StringComparison.Ordinal) &&
            avaloniaSource.Contains(
                "--evidence-audio-pre-commit-delay-ms",
                StringComparison.Ordinal) &&
            avaloniaSource.Contains(
                "Evidence audio pre-commit delay requires a direct evidence chart.",
                StringComparison.Ordinal),
            "Focused GUI permission-revocation seam is missing or is not evidence-chart scoped.");

        return
            "avaloniaCatchAll=3; wpfCatchAll=3; codecAndComFailuresContained=True; " +
            "permissionRevocationEvidenceScoped=True";
    }

    private static string VerifyAudioLibraryCreationCompletionStatus()
    {
        var imported = WindowsAudioLibraryCreationPolicy.CompletionStatus(
            "Track",
            WindowsChartAudioImportStatus.Imported);
        var canceled = WindowsAudioLibraryCreationPolicy.CompletionStatus(
            "Track",
            WindowsChartAudioImportStatus.Canceled);
        var denied = WindowsAudioLibraryCreationPolicy.CompletionStatus(
            "Track",
            WindowsChartAudioImportStatus.Denied);
        var failed = WindowsAudioLibraryCreationPolicy.CompletionStatus(
            "Track",
            WindowsChartAudioImportStatus.Failed);
        Require(
            imported.Contains("点击保存", StringComparison.Ordinal) &&
            canceled.Contains("已取消", StringComparison.Ordinal) &&
            denied.Contains("权限", StringComparison.Ordinal) &&
            failed.Contains("失败", StringComparison.Ordinal) &&
            !failed.Contains("已取消", StringComparison.Ordinal),
            "Audio library creation collapsed distinct terminal states.");
        return "Imported=save-hint; Canceled=cancel; Denied=permission; Failed=failure";
    }

    private static async Task<string> VerifyWorkingAudioResolution(string isolationRoot)
    {
        var root = FreshDirectory(isolationRoot, "working-audio-resolution");
        var library = Path.Combine(root, "library");
        var editing = Path.Combine(root, "editing");
        Directory.CreateDirectory(library);
        var sourceChart = Path.Combine(library, "source.mc");
        MalodyMcSerializer.Save(CreateDocument(), sourceChart);
        var workspace = new DesktopDocumentWorkspace(library, editing, "spec");
        var tab = await workspace.OpenAsync(sourceChart).ConfigureAwait(false);
        var workingAudio = Path.Combine(Path.GetDirectoryName(tab.WorkingPath)!, "new-audio.ogg");
        File.WriteAllBytes(workingAudio, [0x4F, 0x67, 0x67, 0x53, 1, 2, 3, 4]);
        var applied = ChartMetadataEditingService.SetAudioFile(tab.Session, "new-audio.ogg");
        Require(applied.Status == ChartMetadataApplyStatus.Applied, applied.Message);
        var resolved = WindowsAudioTransportViewModel.ResolveDocumentAudioPath(tab);
        Require(string.Equals(resolved, workingAudio, StringComparison.OrdinalIgnoreCase),
            "Unsaved working-cache audio was not selected before the source fallback.");
        Require(!File.Exists(Path.Combine(library, "new-audio.ogg")),
            "Resolver test unexpectedly wrote the source directory before save.");
        return $"resolvedWorking=True; sourceExistsBeforeSave=False; reference={tab.Session.Document.Metadata.AudioFile}";
    }

    private static string VerifyMetadataOffsetPermission()
    {
        var session = SessionWithDocument(CreateDocument());
        var draft = ChartMetadataEditingService.Capture(session.Document) with { Offset = "245.75" };
        Require(ChartMetadataEditingService.TryParseDraft(draft, out var edit, out var error), error);
        session.CanEditTiming = false;
        var denied = ChartMetadataEditingService.Apply(session, edit!);
        Require(denied.Status == ChartMetadataApplyStatus.Denied && denied.PermissionCategory == "Effect" &&
                session.ChangeSerial == 0,
            "Offset changed without Timing permission.");
        session.CanEditTiming = true;
        var applied = ChartMetadataEditingService.Apply(session, edit!);
        Require(applied.Status == ChartMetadataApplyStatus.Applied && session.ChangeSerial == 1,
            applied.Message);
        Require(Math.Abs(FirstBpm(session.Document).Delay - 245.75) < 1e-9,
            "Offset was not applied to the first BPM Timing.");
        Require(session.Undo() && Math.Abs(FirstBpm(session.Document).Delay - 10) < 1e-9,
            "Combined Metadata/Offset edit did not undo in one step.");
        return "timingDenied=True; offset=245.75; oneUndo=True";
    }

    private static string VerifySingleTempoApply()
    {
        var result = new BeatAnalysisResult(140, 80, .1, .9, 64, 120);
        var session = SessionWithDocument(CreateDocument());
        session.CanEditMetadata = false;
        var applied = TempoDetectionEditingService.ApplySingle(session, result, 0);
        Require(applied.Status == TempoDetectionApplyStatus.Applied && !applied.MetadataBpmUpdated,
            applied.Message);
        Require(FirstBpm(session.Document).Bpm == 140 && FirstBpm(session.Document).Delay == 80,
            "Single result did not update BPM and Delay.");
        Require(session.Document.Metadata.Bpm == 120, "Metadata BPM changed without permission.");
        Require(session.Undo() && FirstBpm(session.Document).Bpm == 120 && FirstBpm(session.Document).Delay == 10,
            "Single BPM apply was not one undo step.");

        session.CanEditTiming = false;
        var denied = TempoDetectionEditingService.ApplySingle(session, result, session.ChangeSerial);
        Require(denied.Status == TempoDetectionApplyStatus.Denied, "Timing permission denial was not enforced.");
        session.CanEditTiming = true;
        var stale = TempoDetectionEditingService.ApplySingle(session, result, session.ChangeSerial + 1);
        Require(stale.Status == TempoDetectionApplyStatus.Stale, "Stale single result was accepted.");
        return "timingApplied=True; metadataPreserved=True; oneUndo=True; denied=True; stale=True";
    }

    private static string VerifyTimelinePlan()
    {
        var document = CreateDocument();
        document.Timing.Add(new TimingEvent
        {
            Beat = new BeatPosition(2, 0, 1),
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1.2
        });
        var estimate = new VariableTempoEstimate(
            [
                new TempoSegmentEstimate(0, 120, .9, 20),
                new TempoSegmentEstimate(4, 150, .8, 18)
            ],
            [],
            25,
            30);
        var plan = TempoDetectionEditingService.CreateTimelinePlan(document, estimate);
        Require(plan.Candidates.Count == 2 && plan.ExistingBpmCount == 1,
            "Timeline plan did not count candidates or existing BPM records.");
        Require(plan.ReferenceBpm == 120, "Timeline plan did not capture the persisted Metadata BPM.");
        return $"candidates={plan.Candidates.Count}; existing={plan.ExistingBpmCount}; managedScroll={plan.ManagedScrollCount}; reference={plan.ReferenceBpm:R}";
    }

    private static string VerifyTimelineApply()
    {
        var document = CreateTimelineDocument();
        var before = document.Clone();
        var session = SessionWithDocument(document);
        var plan = CreateDirectPlan(document.Metadata.Bpm);
        var applied = TempoDetectionEditingService.ApplyTimeline(session, plan, 42, false, 0);
        Require(applied.Status == TempoDetectionApplyStatus.Applied && applied.ReplacedBpmCount == 2,
            applied.Message);
        Require(session.Document.Timing.Count(timing => timing.Bpm.HasValue) == 2,
            "Variable apply did not replace the complete BPM timeline.");
        Require(session.Document.Timing.Any(timing => timing.Effect == TimingEffectKind.Jump),
            "Combined old BPM/Effect record lost its Effect portion.");
        Require(session.Document.Timing.Any(timing => timing.Effect == TimingEffectKind.Custom && timing.Beat == new BeatPosition(7, 0, 1)),
            "Unrelated Effect was removed.");
        Require(session.Undo() && TimingFingerprint(session.Document) == TimingFingerprint(before),
            "Variable BPM apply did not undo as one atomic edit.");
        return "replaced=2; candidates=2; jumpPreserved=True; customPreserved=True; oneUndo=True";
    }

    private static string VerifyScrollCompensation()
    {
        var document = CreateTimelineDocument();
        document.Metadata.Bpm = 120;
        var before = document.Clone();
        var session = SessionWithDocument(document);
        var plan = CreateDirectPlan(120);
        var applied = TempoDetectionEditingService.ApplyTimeline(session, plan, 15, true, 0);
        Require(applied.Status == TempoDetectionApplyStatus.Applied && applied.ScrollCompensationApplied,
            applied.Message);
        var first = session.Document.Timing.Single(timing => timing.Bpm == 120);
        var second = session.Document.Timing.Single(timing => timing.Bpm == 60);
        Require(first.Effect == TimingEffectKind.Scroll && Math.Abs(first.EffectValue - 1) < 1e-9,
            "First compensation ratio is not 1.0.");
        Require(second.Effect == TimingEffectKind.Scroll && Math.Abs(second.EffectValue - 2) < 1e-9,
            "Second compensation ratio is not 2.0.");
        Require(session.Document.Timing.Any(timing => timing.Effect == TimingEffectKind.Custom),
            "Unrelated custom Effect was removed by compensation.");
        Require(session.Undo() && TimingFingerprint(session.Document) == TimingFingerprint(before),
            "Compensated timeline did not undo atomically.");
        return "ratios=1,2; unrelatedEffectPreserved=True; oneUndo=True";
    }

    private static string VerifyTimelineGuards()
    {
        var session = SessionWithDocument(CreateTimelineDocument());
        var plan = CreateDirectPlan(session.Document.Metadata.Bpm);
        var stale = TempoDetectionEditingService.ApplyTimeline(session, plan, 0, false, 1);
        Require(stale.Status == TempoDetectionApplyStatus.Stale && session.ChangeSerial == 0,
            "Stale variable plan mutated the document.");
        session.AllowOwnerProtectedEdits = false;
        session.AddOwnerProtectedRegion(BeatPosition.Zero, new BeatPosition(1, 0, 1));
        var locked = TempoDetectionEditingService.ApplyTimeline(session, plan, 0, false, 0);
        Require(locked.Status == TempoDetectionApplyStatus.Denied && session.ChangeSerial == 0,
            "Owner-locked variable plan partially applied.");
        return "staleRejected=True; ownerLockRejected=True; serial=0";
    }

    private static string VerifyTimelinePermissionRevocation()
    {
        var session = SessionWithDocument(CreateTimelineDocument());
        var before = TimingFingerprint(session.Document);
        var plan = CreateDirectPlan(session.Document.Metadata.Bpm);
        session.CanEditTiming = false;
        var denied = TempoDetectionEditingService.ApplyTimeline(
            session,
            plan,
            25,
            applyScrollCompensation: false,
            expectedChangeSerial: 0);
        Require(
            denied.Status == TempoDetectionApplyStatus.Denied &&
            denied.PermissionCategory == "Effect" &&
            session.ChangeSerial == 0 &&
            TimingFingerprint(session.Document) == before,
            "Late Timing permission revocation partially applied a variable BPM plan.");
        return
            $"status={denied.Status}; permission={denied.PermissionCategory}; " +
            "serial=0; timingUnchanged=True";
    }

    private static string VerifyAnalyzerFailureBoundaries(string isolationRoot)
    {
        var root = FreshDirectory(isolationRoot, "analyzer-boundaries");
        var silent = Path.Combine(root, "silent.wav");
        using (var writer = new WaveFileWriter(silent, new WaveFormat(8_000, 16, 1)))
        {
            var zeros = new byte[8_000 * 2 * 9];
            writer.Write(zeros, 0, zeros.Length);
        }
        var silenceRejected = false;
        try { WindowsBeatAnalyzer.AnalyzeAsync(silent).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { silenceRejected = true; }
        Require(silenceRejected, "Silent audio produced a synthetic BPM.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = false;
        try { WindowsBeatAnalyzer.AnalyzeAsync(ResoundAudio, cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "Pre-canceled real analysis did not stop.");
        return "silenceRejected=True; preCanceled=True";
    }

    private static string VerifyAnalyzerResponsivenessBoundaries()
    {
        using var stream = new ExactLengthPcmWaveStream(
            sampleRate: 8_000,
            durationSeconds: 9);
        var signal = WindowsBeatAnalyzer.ReadSignal(stream);
        Require(
            stream.Position == stream.Length &&
            !stream.ReadPastDeclaredEnd &&
            signal.DurationSeconds is >= 8.99 and <= 9.01,
            "BPM decoder issued an EOF probe after the declared PCM length.");

        using var midDecodeCancellation = new CancellationTokenSource();
        using var cancellableStream = new ExactLengthPcmWaveStream(
            sampleRate: 8_000,
            durationSeconds: 30,
            afterRead: readCalls =>
            {
                if (readCalls == 2) midDecodeCancellation.Cancel();
            });
        var canceledDuringDecode = false;
        try
        {
            WindowsBeatAnalyzer.ReadSignal(
                cancellableStream,
                midDecodeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceledDuringDecode = true;
        }
        Require(
            canceledDuringDecode && cancellableStream.ReadCalls == 2,
            "BPM decode did not observe cancellation between bounded reads.");

        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var analyzerSource = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "LumaChart.Avalonia",
                "Audio",
                "WindowsBeatAnalyzer.cs"));
        var windowSource = File.ReadAllText(
            Path.Combine(projectRoot, "LumaChart.Avalonia", "MainWindow.cs"));
        Require(
            analyzerSource.Contains(
                "LengthCappedSampleProvider.FromWaveStream(source)",
                StringComparison.Ordinal) &&
            analyzerSource.Contains(
                "TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach",
                StringComparison.Ordinal) &&
            analyzerSource.Contains(
                "ThreadPriority.BelowNormal",
                StringComparison.Ordinal),
            "BPM analysis is not isolated on the capped, low-priority Windows worker.");
        Require(
            windowSource.Contains(
                "metadataAnalyzeSingleTempoButton.Content = \"取消 BPM 分析\";",
                StringComparison.Ordinal) &&
            windowSource.Contains(
                "activeTempoAnalysisKind == TempoAnalysisKind.Single",
                StringComparison.Ordinal) &&
            windowSource.Contains(
                "audioAnalysisCancellation.Cancel();",
                StringComparison.Ordinal),
            "The active BPM/Offset operation does not expose an enabled cancel action.");
        return
            $"duration={signal.DurationSeconds:R}; readCalls={stream.ReadCalls}; " +
            "readPastDeclaredEnd=False; midDecodeCanceled=True; " +
            "dedicatedWorker=True; cancelAction=True";
    }

    private static string VerifyRealResound()
    {
        var hash = Sha256(ResoundAudio);
        var result = WindowsBeatAnalyzer.AnalyzeAsync(ResoundAudio).GetAwaiter().GetResult();
        Require(Math.Abs(result.Bpm - 140) < .001, $"Resound detected {result.Bpm:R}, expected 140.");
        Require(Sha256(ResoundAudio) == hash, "Resound source audio changed.");
        return $"bpm={result.Bpm:R}; offset={result.OffsetMilliseconds:R}; confidence={result.Confidence:R}; sourceUnchanged=True";
    }

    private static string VerifyRealPositiveParade()
    {
        var hash = Sha256(PositiveParadeAudio);
        var result = WindowsBeatAnalyzer.AnalyzeTimelineAsync(PositiveParadeAudio).GetAwaiter().GetResult();
        var candidates = VariableTempoEstimator.MapToTiming(result.Timeline);
        var bpms = candidates.Select(candidate => candidate.Bpm).ToArray();
        Require(bpms.SequenceEqual([105d, 155d, 105d, 155d]),
            $"Positive Parade BPM sequence differed: {string.Join(',', bpms.Select(value => value.ToString("R")))}.");
        var beats = candidates.Select(candidate => candidate.Beat.ToString()).ToArray();
        // The first beat is now written as Delay = -phase (beat 0 at +427.5 ms,
        // where the detector hears it). The old 57.5/129.5/165.5 matched the
        // authored chart only by accident: the mirrored origin moved the grid
        // by 2 x 427.5 ms = 1.5 beats at 105 BPM, which cancelled the detector's
        // off-beat phase lock on this song. The first change still lands at
        // the authored 32.43 s; the grid is the detector's own whole-beat grid.
        Require(beats.SequenceEqual(["0:0/1", "56:0/1", "129:0/1", "165:0/1"]),
            $"Positive Parade rational boundaries differed: {string.Join(',', beats)}.");
        Require(Sha256(PositiveParadeAudio) == hash, "Positive Parade source audio changed.");
        return $"segments={candidates.Count}; bpms={string.Join(',', bpms)}; beats={string.Join(',', beats)}; sourceUnchanged=True";
    }

    private static string VerifyRealBpm175()
    {
        var hash = Sha256(Bpm175Audio);
        var result = WindowsBeatAnalyzer.AnalyzeAsync(Bpm175Audio).GetAwaiter().GetResult();
        Require(Math.Abs(result.Bpm - 175) < .001, $"Long-track calibration returned {result.Bpm:R}, expected 175.");
        Require(Sha256(Bpm175Audio) == hash, "175 BPM source audio changed.");
        return $"bpm={result.Bpm:R}; analyzedSeconds={result.DurationSeconds:R}; sourceUnchanged=True";
    }

    private static ChartDocument CreateDocument() => new()
    {
        KeyCount = 4,
        Metadata = new ChartMetadata
        {
            Title = "Audio Tempo Spec",
            Artist = "Luma Artist",
            Creator = "Spec",
            Version = "Normal",
            Bpm = 120
        },
        Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120, Delay = 10 }],
        Notes = [new ChartNote { Lane = 0, Beat = new BeatPosition(1, 0, 1) }]
    };

    private static ChartDocument CreateTimelineDocument()
    {
        var document = CreateDocument();
        document.Timing.Add(new TimingEvent
        {
            Beat = new BeatPosition(4, 0, 1),
            Bpm = 150,
            Effect = TimingEffectKind.Jump,
            EffectValue = 2
        });
        document.Timing.Add(new TimingEvent
        {
            Beat = BeatPosition.Zero,
            Effect = TimingEffectKind.Scroll,
            EffectValue = .5
        });
        document.Timing.Add(new TimingEvent
        {
            Beat = new BeatPosition(7, 0, 1),
            Effect = TimingEffectKind.Custom,
            EffectValue = 3,
            EffectFieldName = "custom"
        });
        document.Normalize();
        return document;
    }

    private static TempoTimelinePlan CreateDirectPlan(double referenceBpm) => new(
        [
            new TempoTimingCandidate(BeatPosition.Zero, 120, 0, .9),
            new TempoTimingCandidate(new BeatPosition(4, 0, 1), 60, 8, .8)
        ],
        ExistingBpmCount: 2,
        ManagedScrollCount: 1,
        ReferenceBpm: referenceBpm);

    private static EditorSession SessionWithDocument(ChartDocument document)
    {
        var session = new EditorSession();
        session.ReplaceDocument(document.Clone());
        return session;
    }

    private static TimingEvent FirstBpm(ChartDocument document) =>
        document.Timing.Where(timing => timing.Bpm.HasValue).OrderBy(timing => timing.Beat).First();

    private static string TimingFingerprint(ChartDocument document) => string.Join(
        "|",
        document.Timing.OrderBy(timing => timing.Beat).ThenBy(timing => timing.Id).Select(timing =>
            $"{timing.Beat};{timing.Bpm:R};{timing.Delay:R};{timing.Effect};{timing.EffectValue:R};{timing.EffectFieldName}"));

    private static string FreshDirectory(string isolationRoot, string name)
    {
        var path = Path.Combine(isolationRoot, name);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RequireCatchAll(
        string source,
        string startMarker,
        string endMarker,
        string operation)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Require(start >= 0 && end > start, $"{operation} source region was not found.");
        var body = source[start..end];
        Require(
            body.Contains("catch (Exception exception)", StringComparison.Ordinal) &&
            !body.Contains("catch (Exception exception) when", StringComparison.Ordinal),
            $"{operation} does not contain the WPF catch-all failure boundary.");
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Check(
        ICollection<WindowsAudioTempoSpecResult> results,
        string name,
        Func<string> verify)
    {
        try { results.Add(new WindowsAudioTempoSpecResult(name, true, verify())); }
        catch (Exception exception) { results.Add(new WindowsAudioTempoSpecResult(name, false, exception.Message)); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsAudioTempoSpecResult(string Name, bool Passed, string Detail);

internal sealed class ExactLengthPcmWaveStream : WaveStream
{
    private readonly long length;
    private readonly Action<int>? afterRead;
    private long position;

    public ExactLengthPcmWaveStream(
        int sampleRate,
        int durationSeconds,
        Action<int>? afterRead = null)
    {
        WaveFormat = new WaveFormat(sampleRate, 16, 1);
        length = checked((long)WaveFormat.AverageBytesPerSecond * durationSeconds);
        this.afterRead = afterRead;
    }

    public override WaveFormat WaveFormat { get; }
    public override long Length => length;
    public override long Position
    {
        get => position;
        set => position = Math.Clamp(value, 0, length);
    }

    public bool ReadPastDeclaredEnd { get; private set; }
    public int ReadCalls { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (position >= length)
        {
            ReadPastDeclaredEnd = true;
            throw new InvalidOperationException(
                "The decoder adapter read beyond the declared PCM length.");
        }
        ReadCalls++;
        var read = (int)Math.Min(count, length - position);
        Array.Clear(buffer, offset, read);
        position += read;
        afterRead?.Invoke(ReadCalls);
        return read;
    }
}

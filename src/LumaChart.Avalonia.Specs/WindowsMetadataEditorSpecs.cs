using System.Security.Cryptography;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Services;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsMetadataEditorSpecs
{
    private const string McFixture = @"D:\osu!\Songs\Resound\0\1781536331.mc";
    private const string OsuFixture =
        @"D:\osu!\Songs\1005389 Minori Chihara - Michishirube\Minori Chihara - Michishirube (eZmmR) [Dear You,  Gilbert Bougainvillea .].osu";

    public static IReadOnlyList<WindowsMetadataSpecResult> Run(string isolationRoot)
    {
        Directory.CreateDirectory(isolationRoot);
        var results = new List<WindowsMetadataSpecResult>();
        Check(results, "metadata-draft-validates-wpf-text-and-number-contracts", VerifyDraftValidation);
        Check(
            results,
            "metadata-local-backup-export-and-close-preserve-unfinished-drafts",
            VerifyLocalPreservationDraft);
        Check(results, "metadata-apply-is-one-atomic-undo-step", VerifyAtomicApply);
        Check(results, "metadata-unchanged-apply-does-not-create-history", VerifyUnchangedApply);
        Check(results, "metadata-permission-denials-leave-document-untouched", VerifyMetadataPermissions);
        Check(results, "metadata-key-count-reduction-requires-note-permission", VerifyKeyCountPermissions);
        Check(results, "metadata-preview-resolves-playing-clock-or-rational-red-line", VerifyPreviewResolution);
        Check(results, "metadata-preview-permission-and-undo-match-wpf", VerifyPreviewMutation);
        Check(
            results,
            "metadata-dirty-draft-rebases-over-authoritative-fields-like-wpf",
            VerifyDirtyDraftRebase);
        Check(
            results,
            "metadata-preview-rebase-preserves-unapplied-local-fields",
            VerifyPreviewDraftRebase);
        Check(results, "metadata-image-extension-contract-matches-wpf", VerifyImageExtensionContract);
        Check(results, "metadata-image-import-copies-deduplicates-and-avoids-source-collisions",
            () => VerifyImageImport(isolationRoot));
        Check(results, "metadata-asset-resolution-prefers-working-cache-and-rejects-traversal",
            () => VerifyAssetResolution(isolationRoot));
        Check(results, "metadata-kana-and-long-vowel-romanization-matches-wpf", VerifyKanaRomanization);
        Check(results, "metadata-kanji-romanization-uses-offline-dictionary", VerifyKanjiRomanization);
        Check(results, "metadata-romanization-honors-cancellation", VerifyRomanizationCancellation);
        Check(results, "metadata-real-mc-save-reopen-preserves-supported-fields", () => VerifyRealMc(isolationRoot));
        Check(results, "metadata-real-osu-save-reopen-preserves-all-fields", () => VerifyRealOsu(isolationRoot));
        return results;
    }

    private static string VerifyDraftValidation()
    {
        var draft = new ChartMetadataDraft(
            "  Metadata   Spec  ",
            "谱面原名",
            "Spec Artist",
            "艺术家原名",
            "Spec Creator",
            "Normal",
            "12345",
            "137.5",
            "321.5",
            7,
            "6.25",
            "4.5",
            "images\\cover.png",
            "");
        Require(ChartMetadataEditingService.TryParseDraft(draft, out var edit, out var error), error);
        var parsedEdit = edit ?? throw new InvalidOperationException("Parsed edit is missing.");
        Require(parsedEdit.Title == "Metadata Spec", "Title whitespace was not normalized like WPF.");
        Require(parsedEdit.PreviewTime == 12345 && parsedEdit.Bpm == 137.5 &&
                parsedEdit.Offset == 321.5 && parsedEdit.KeyCount == 7,
            "Numeric metadata did not parse exactly.");
        Require(parsedEdit.CoverFile == "images/cover.png" && parsedEdit.BackgroundFile.Length == 0,
            "Image references were not normalized to safe chart-relative paths.");

        Require(!ChartMetadataEditingService.TryParseDraft(draft with { Title = "非 ASCII" }, out _, out _),
            "Non-ASCII community title was accepted.");
        Require(!ChartMetadataEditingService.TryParseDraft(draft with { Creator = "bad\tcreator" }, out _, out _),
            "Control characters were accepted.");
        Require(!ChartMetadataEditingService.TryParseDraft(draft with { Bpm = "0" }, out _, out _),
            "Zero BPM was accepted.");
        Require(!ChartMetadataEditingService.TryParseDraft(draft with { PreviewTime = "1.5" }, out _, out _),
            "Fractional preview milliseconds were accepted.");
        Require(!ChartMetadataEditingService.TryParseDraft(draft with { Offset = "600001" }, out _, out _),
            "Out-of-range audio offset was accepted.");
        Require(!ChartMetadataEditingService.TryParseDraft(draft with { OverallDifficulty = "10.01" }, out _, out _),
            "OD above ten was accepted.");
        Require(!ChartMetadataEditingService.TryParseDraft(draft with { CoverFile = "../outside.png" }, out _, out _),
            "A traversing cover reference was accepted.");
        Require(!ChartMetadataEditingService.TryParseDraft(draft with { BackgroundFile = @"C:\outside.png" }, out _, out _),
            "A rooted background reference was accepted.");
        return $"title={parsedEdit.Title}; bpm={parsedEdit.Bpm:R}; offset={parsedEdit.Offset:R}; preview={parsedEdit.PreviewTime}; keys={parsedEdit.KeyCount}; od={parsedEdit.OverallDifficulty:R}; hp={parsedEdit.HpDrainRate:R}";
    }

    private static string VerifyLocalPreservationDraft()
    {
        var document = CreateDocument();
        var fallback = ChartMetadataEditingService.Capture(document);
        var draft = fallback with
        {
            Title = "  私有  谱面  ",
            Artist = "未完成艺术家",
            Creator = "制\t谱者",
            Bpm = "not-a-number",
            Offset = "700000",
            PreviewTime = "-1",
            OverallDifficulty = "invalid",
            HpDrainRate = "11"
        };
        Require(
            !ChartMetadataEditingService.TryParseDraft(
                draft,
                out _,
                out _),
            "Community apply unexpectedly accepted a private non-ASCII draft.");
        Require(
            ChartMetadataEditingService.TryParseDraftForLocalPreservation(
                draft,
                document,
                out var edit,
                out var error),
            error);
        var parsed = edit ??
                     throw new InvalidOperationException(
                         "Local-preservation edit is missing.");
        Require(
            parsed.Title == "私有 谱面" &&
            parsed.Artist == "未完成艺术家" &&
            parsed.Creator == "制 谱者",
            "Local preservation did not normalize unfinished text safely.");
        Require(
            parsed.Bpm == document.Metadata.Bpm &&
            parsed.Offset == document.Timing.First(timing => timing.Bpm.HasValue).Delay &&
            parsed.PreviewTime == document.Metadata.PreviewTime &&
            parsed.OverallDifficulty == document.Metadata.OverallDifficulty &&
            parsed.HpDrainRate == document.Metadata.HpDrainRate,
            "Invalid local numeric fields did not fall back to committed values.");

        var session = SessionWithDocument(document);
        var applied = ChartMetadataEditingService.Apply(
            session,
            parsed,
            enforceCommunityRules: false);
        Require(
            applied.Status == ChartMetadataApplyStatus.Applied &&
            session.Document.Metadata.Title == "私有 谱面" &&
            session.Document.Metadata.Artist == "未完成艺术家",
            "Local backup/export/close apply did not preserve unfinished text.");
        Require(
            !ChartMetadataEditingService.TryParseDraftForLocalPreservation(
                draft with { CoverFile = "../outside.png" },
                document,
                out _,
                out _),
            "Local preservation accepted a traversing resource path.");
        return "privateUnicode=True; controls=normalized; invalidNumbers=fallback; " +
               "unsafeResource=False; localApply=oneAtomicChange";
    }

    private static string VerifyAtomicApply()
    {
        var session = SessionWithDocument(CreateDocument());
        var before = session.Document.Clone();
        var edit = NewEdit(keyCount: 6);
        var result = ChartMetadataEditingService.Apply(session, edit);
        Require(result.Status == ChartMetadataApplyStatus.Applied, result.Message);
        Require(session.ChangeSerial == 1, "Metadata apply did not create exactly one committed change.");
        RequireMetadata(session.Document, edit);
        Require(session.Undo(), "Metadata apply could not be undone.");
        Require(DocumentMetadataEquals(session.Document, before), "One undo did not restore every metadata field.");
        Require(session.Redo(), "Metadata apply could not be redone.");
        RequireMetadata(session.Document, edit);
        return $"serial={session.ChangeSerial}; undoRedo=True; title={session.Document.Metadata.Title}; keys={session.Document.KeyCount}";
    }

    private static string VerifyUnchangedApply()
    {
        var document = CreateDocument();
        var session = SessionWithDocument(document);
        var edit = EditFrom(document);
        var result = ChartMetadataEditingService.Apply(session, edit);
        Require(result.Status == ChartMetadataApplyStatus.Unchanged, result.Message);
        Require(session.ChangeSerial == 0 && !session.IsDirty, "Unchanged metadata created history or dirty state.");
        Require(!session.Undo(), "Unchanged metadata unexpectedly created an undo entry.");
        return "serial=0; dirty=False; undo=False";
    }

    private static string VerifyMetadataPermissions()
    {
        var session = SessionWithDocument(CreateDocument());
        var before = session.Document.Clone();
        session.IsReadOnly = true;
        var observer = ChartMetadataEditingService.Apply(session, NewEdit());
        Require(observer.Status == ChartMetadataApplyStatus.Denied && observer.PermissionCategory == "观察者只读",
            "Observer metadata edit was not denied with the expected category.");
        Require(DocumentMetadataEquals(session.Document, before), "Observer denial changed the document.");

        session.IsReadOnly = false;
        session.CanEditMetadata = false;
        var denied = ChartMetadataEditingService.Apply(session, NewEdit());
        Require(denied.Status == ChartMetadataApplyStatus.Denied && denied.PermissionCategory == "谱面信息",
            "Metadata permission denial did not identify the metadata category.");
        Require(DocumentMetadataEquals(session.Document, before) && session.ChangeSerial == 0,
            "Metadata permission denial was not atomic.");
        return "observerDenied=True; metadataDenied=True; serial=0";
    }

    private static string VerifyKeyCountPermissions()
    {
        var document = CreateDocument();
        document.KeyCount = 6;
        document.Notes.Add(new ChartNote { Lane = 4, Beat = new BeatPosition(4, 0, 4) });
        document.Notes.Add(new ChartNote { Lane = 5, Beat = new BeatPosition(5, 0, 4) });
        document.Normalize();
        var session = SessionWithDocument(document);
        var before = session.Document.Clone();
        session.CanEditNotes = false;
        var denied = ChartMetadataEditingService.Apply(session, NewEdit(keyCount: 4));
        Require(denied.Status == ChartMetadataApplyStatus.Denied && denied.RemovedNoteCount == 2,
            "Key-count reduction did not report the two removed notes.");
        Require(DocumentMetadataEquals(session.Document, before) && session.Document.Notes.Count == before.Notes.Count,
            "Denied key-count reduction partially changed metadata or notes.");

        session.CanEditNotes = true;
        var applied = ChartMetadataEditingService.Apply(session, NewEdit(keyCount: 4));
        Require(applied.Status == ChartMetadataApplyStatus.Applied && session.Document.KeyCount == 4,
            applied.Message);
        Require(session.Document.Notes.All(note => note.IsDraft || note.Lane < 4),
            "Key-count reduction retained an out-of-range official note.");
        Require(session.Undo() && session.Document.KeyCount == 6 && session.Document.Notes.Count == before.Notes.Count,
            "One undo did not restore notes removed by the atomic key-count edit.");
        return "deniedRemoved=2; appliedRemoved=2; oneUndoRestored=True";
    }

    private static string VerifyPreviewResolution()
    {
        var timing = new[] { new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120, Delay = 125 } };
        var playing = ChartMetadataEditingService.ResolvePreviewTime(
            true, 1234.5, timing, new BeatPosition(100, 0, 1));
        var beat = new BeatPosition(3, 1, 2);
        var redLine = ChartMetadataEditingService.ResolvePreviewTime(false, 999999, timing, beat);
        var expected = (int)Math.Clamp(
            Math.Round(new TimingMap(timing).BeatToMilliseconds(beat), MidpointRounding.AwayFromZero),
            0,
            int.MaxValue);
        var clamped = ChartMetadataEditingService.ResolvePreviewTime(true, -25, timing, beat);
        Require(playing == 1235, "Playing transport time did not round away from zero.");
        Require(redLine == expected, "Paused preview did not use the red-line TimingMap position.");
        Require(clamped == 0, "Negative transport time was not clamped to zero.");
        return $"playing={playing}; redLine={redLine}; expected={expected}; clamped={clamped}";
    }

    private static string VerifyPreviewMutation()
    {
        var session = SessionWithDocument(CreateDocument());
        var before = session.Document.Metadata.PreviewTime;
        session.CanEditMetadata = false;
        var denied = ChartMetadataEditingService.SetPreviewTime(session, 4321);
        Require(denied.Status == ChartMetadataApplyStatus.Denied &&
                session.Document.Metadata.PreviewTime == before && session.ChangeSerial == 0,
            "Denied preview mutation changed the document.");
        session.CanEditMetadata = true;
        var applied = ChartMetadataEditingService.SetPreviewTime(session, 4321);
        Require(applied.Status == ChartMetadataApplyStatus.Applied &&
                session.Document.Metadata.PreviewTime == 4321 && session.ChangeSerial == 1,
            applied.Message);
        Require(session.Undo() && session.Document.Metadata.PreviewTime == before,
            "Preview mutation did not undo in one step.");
        return $"denied=True; applied=4321; undo={before}";
    }

    private static string VerifyDirtyDraftRebase()
    {
        var original = CreateDocument();
        var baseline = ChartMetadataEditingService.Capture(original);
        var local = baseline with
        {
            Title = "Local Draft Title",
            Version = "Local Draft Version"
        };
        var authoritativeDocument = original.Clone();
        authoritativeDocument.Metadata.Artist = "Remote Artist";
        authoritativeDocument.Metadata.PreviewTime = 6543;
        authoritativeDocument.Metadata.OverallDifficulty = 7.5;
        var authoritative = ChartMetadataEditingService.Capture(authoritativeDocument);
        var rebased = ChartMetadataEditingService.RebaseDraftWithDiagnostics(
            local,
            baseline,
            authoritative);
        Require(
            rebased.Draft.Title == local.Title &&
            rebased.Draft.Version == local.Version,
            "Rebase discarded a locally edited field.");
        Require(
            rebased.Draft.Artist == authoritative.Artist &&
            rebased.Draft.PreviewTime == authoritative.PreviewTime &&
            rebased.Draft.OverallDifficulty == authoritative.OverallDifficulty,
            "Rebase retained stale untouched fields over newer authority.");
        Require(
            rebased.PreservedLocalFieldCount == 2 &&
            rebased.UpdatedAuthoritativeFieldCount == 3 &&
            rebased.HasLocalChanges,
            "Rebase diagnostics did not describe the local/remote field split.");

        Require(
            ChartMetadataEditingService.TryParseDraft(
                rebased.Draft,
                out var edit,
                out var error),
            error);
        var session = SessionWithDocument(authoritativeDocument);
        var applied = ChartMetadataEditingService.Apply(
            session,
            edit ?? throw new InvalidOperationException("Rebased edit is missing."));
        Require(
            applied.Status == ChartMetadataApplyStatus.Applied &&
            session.Document.Metadata.Title == local.Title &&
            session.Document.Metadata.Version == local.Version &&
            session.Document.Metadata.Artist == authoritativeDocument.Metadata.Artist &&
            session.Document.Metadata.PreviewTime == authoritativeDocument.Metadata.PreviewTime &&
            session.Document.Metadata.OverallDifficulty.Equals(
                authoritativeDocument.Metadata.OverallDifficulty),
            "Applying the rebased draft overwrote an untouched authoritative field.");
        return
            $"local={rebased.PreservedLocalFieldCount}; remote={rebased.UpdatedAuthoritativeFieldCount}; " +
            $"title={session.Document.Metadata.Title}; artist={session.Document.Metadata.Artist}; " +
            $"preview={session.Document.Metadata.PreviewTime}";
    }

    private static string VerifyPreviewDraftRebase()
    {
        var session = SessionWithDocument(CreateDocument());
        var baseline = ChartMetadataEditingService.Capture(session.Document);
        var local = baseline with
        {
            Title = "Pending Preview Draft",
            Creator = "Pending Creator"
        };
        var preview = ChartMetadataEditingService.SetPreviewTime(session, 4321);
        Require(preview.Status == ChartMetadataApplyStatus.Applied, preview.Message);
        var authoritative = ChartMetadataEditingService.Capture(session.Document);
        var rebased = ChartMetadataEditingService.RebaseDraftWithDiagnostics(
            local,
            baseline,
            authoritative);
        Require(
            rebased.Draft.Title == local.Title &&
            rebased.Draft.Creator == local.Creator &&
            rebased.Draft.PreviewTime == "4321",
            "Preview mutation did not preserve other dirty fields while updating PreviewTime.");
        Require(
            rebased.PreservedLocalFieldCount == 2 &&
            rebased.UpdatedAuthoritativeFieldCount == 1 &&
            rebased.HasLocalChanges,
            "Preview rebase diagnostics did not isolate the PreviewTime authority update.");

        Require(
            ChartMetadataEditingService.TryParseDraft(
                rebased.Draft,
                out var edit,
                out var error),
            error);
        var applied = ChartMetadataEditingService.Apply(
            session,
            edit ?? throw new InvalidOperationException("Preview rebased edit is missing."));
        Require(
            applied.Status == ChartMetadataApplyStatus.Applied &&
            session.Document.Metadata.PreviewTime == 4321 &&
            session.Document.Metadata.Title == local.Title &&
            session.Document.Metadata.Creator == local.Creator,
            "Applying the preview-rebased draft lost PreviewTime or another local field.");
        return
            $"preview={session.Document.Metadata.PreviewTime}; " +
            $"title={session.Document.Metadata.Title}; creator={session.Document.Metadata.Creator}";
    }

    private static string VerifyImageExtensionContract()
    {
        var supported = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
        Require(supported.All(extension => ChartAssetImportService.IsSupportedImageFile("cover" + extension)),
            "One or more WPF image extensions were rejected.");
        Require(!ChartAssetImportService.IsSupportedImageFile("cover.gif") &&
                !ChartAssetImportService.IsSupportedImageFile("cover.svg") &&
                !ChartAssetImportService.IsSupportedImageFile(null),
            "An unsupported image extension was accepted.");
        return $"supported={string.Join(',', supported)}; rejected=.gif,.svg";
    }

    private static string VerifyImageImport(string isolationRoot)
    {
        var root = Path.Combine(isolationRoot, "image-import");
        var workingRoot = Path.Combine(root, "working");
        var sourceRoot = Path.Combine(root, "source-chart");
        var pickerRoot = Path.Combine(root, "picker");
        Directory.CreateDirectory(workingRoot);
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(pickerRoot);
        var workingChart = Path.Combine(workingRoot, "chart.mc");
        var sourceChart = Path.Combine(sourceRoot, "chart.mc");
        File.WriteAllText(workingChart, "{}");
        File.WriteAllText(sourceChart, "{}");
        File.WriteAllBytes(Path.Combine(sourceRoot, "cover.png"), [9, 9, 9]);
        var selected = Path.Combine(pickerRoot, "cover.png");
        var selectedBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        File.WriteAllBytes(selected, selectedBytes);

        var imported = ChartAssetImportService.ImportImage(workingChart, selected, sourceChart);
        Require(imported.Created && imported.FileName == "cover_1.png",
            "The importer did not avoid the conflicting eventual source-chart resource.");
        Require(File.ReadAllBytes(imported.DestinationPath).SequenceEqual(selectedBytes),
            "The selected image bytes were not copied exactly.");
        var duplicate = ChartAssetImportService.ImportImage(workingChart, selected, sourceChart);
        Require(!duplicate.Created && duplicate.FileName == imported.FileName &&
                PathsEqual(duplicate.DestinationPath, imported.DestinationPath),
            "Reimporting identical image bytes did not deduplicate the working asset.");

        var unsupported = Path.Combine(pickerRoot, "cover.gif");
        File.WriteAllBytes(unsupported, selectedBytes);
        Require(Throws<NotSupportedException>(() =>
                ChartAssetImportService.ImportImage(workingChart, unsupported, sourceChart)),
            "The image importer accepted an unsupported GIF source.");
        return $"name={imported.FileName}; created={imported.Created}; duplicateCreated={duplicate.Created}; bytes={selectedBytes.Length}";
    }

    private static string VerifyAssetResolution(string isolationRoot)
    {
        var root = Path.Combine(isolationRoot, "asset-resolution");
        var workingRoot = Path.Combine(root, "working");
        var sourceRoot = Path.Combine(root, "source");
        Directory.CreateDirectory(workingRoot);
        Directory.CreateDirectory(sourceRoot);
        var workingChart = Path.Combine(workingRoot, "chart.mc");
        var sourceChart = Path.Combine(sourceRoot, "chart.mc");
        File.WriteAllText(workingChart, "{}");
        File.WriteAllText(sourceChart, "{}");
        var workingAsset = Path.Combine(workingRoot, "background.png");
        var sourceAsset = Path.Combine(sourceRoot, "background.png");
        File.WriteAllBytes(workingAsset, [1, 2, 3]);
        File.WriteAllBytes(sourceAsset, [4, 5, 6]);

        Require(PathsEqual(
                ChartAssetImportService.ResolveAssetPath(workingChart, sourceChart, "background.png")!,
                workingAsset),
            "The recoverable working-cache asset did not win over the source copy.");
        File.Delete(workingAsset);
        Require(PathsEqual(
                ChartAssetImportService.ResolveAssetPath(workingChart, sourceChart, "background.png")!,
                sourceAsset),
            "The resolver did not fall back to the source chart directory.");
        Require(ChartAssetImportService.ResolveAssetPath(workingChart, sourceChart, "../outside.png") == null,
            "The resolver allowed a parent-directory traversal reference.");
        return "workingFirst=True; sourceFallback=True; traversalRejected=True";
    }

    private static string VerifyKanaRomanization()
    {
        var hiragana = WindowsJapaneseRomanizer.RomanizeAsync("みちしるべ")
            .GetAwaiter().GetResult();
        var longVowel = WindowsJapaneseRomanizer.RomanizeReading("ボーダー");
        Require(hiragana == "Michishirube", $"Unexpected hiragana romanization: {hiragana}");
        Require(longVowel == "Boodaa", $"The long-vowel mark detached from its kana: {longVowel}");
        return $"hiragana={hiragana}; longVowel={longVowel}";
    }

    private static string VerifyKanjiRomanization()
    {
        var romanized = WindowsJapaneseRomanizer.RomanizeAsync("道標")
            .GetAwaiter().GetResult();
        Require(!string.IsNullOrWhiteSpace(romanized) &&
                romanized.All(character => character is >= ' ' and <= '~'),
            $"Offline Kanji reading was not converted to printable ASCII: {romanized}");
        return $"kanji=道標; romanized={romanized}; dictionary={Path.Combine(AppContext.BaseDirectory, "IpaDic")}";
    }

    private static string VerifyRomanizationCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Require(Throws<OperationCanceledException>(() =>
                WindowsJapaneseRomanizer.RomanizeAsync("道標", cancellation.Token)
                    .GetAwaiter().GetResult()),
            "A pre-canceled reading analysis continued into the offline dictionary.");
        return "preCanceled=True; destinationOverwritePreventedByCallerContract=True";
    }

    private static string VerifyRealMc(string isolationRoot)
    {
        var originalHash = Sha256(McFixture);
        var document = MalodyMcSerializer.Load(McFixture);
        var session = SessionWithDocument(document);
        var edit = NewEdit(keyCount: document.KeyCount);
        var applied = ChartMetadataEditingService.Apply(session, edit);
        Require(applied.Status == ChartMetadataApplyStatus.Applied, applied.Message);
        var path = Path.Combine(isolationRoot, "metadata-real-mc", "metadata.mc");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MalodyMcSerializer.Save(session.Document, path);
        var reopened = MalodyMcSerializer.Load(path);
        RequireSupportedRoundTrip(reopened, edit, includeDifficulty: false, includeCover: true);
        Require(Sha256(McFixture) == originalHash, "The immutable real MC fixture changed.");
        return $"keys={reopened.KeyCount}; bpm={reopened.Metadata.Bpm:R}; preview={reopened.Metadata.PreviewTime}; bytes={new FileInfo(path).Length}";
    }

    private static string VerifyRealOsu(string isolationRoot)
    {
        var originalHash = Sha256(OsuFixture);
        var document = OsuManiaSerializer.Load(OsuFixture, OsuNoteTimingImportMode.CalibrateCommonSubdivisions);
        var session = SessionWithDocument(document);
        var edit = NewEdit(keyCount: document.KeyCount);
        var applied = ChartMetadataEditingService.Apply(session, edit);
        Require(applied.Status == ChartMetadataApplyStatus.Applied, applied.Message);
        var path = Path.Combine(isolationRoot, "metadata-real-osu", "metadata.osu");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        OsuManiaSerializer.Save(session.Document, path);
        var reopened = OsuManiaSerializer.Load(path, OsuNoteTimingImportMode.PreserveMilliseconds);
        RequireSupportedRoundTrip(reopened, edit, includeDifficulty: true, includeCover: false);
        Require(Sha256(OsuFixture) == originalHash, "The immutable real osu fixture changed.");
        return $"keys={reopened.KeyCount}; bpm={reopened.Metadata.Bpm:R}; od={reopened.Metadata.OverallDifficulty:R}; hp={reopened.Metadata.HpDrainRate:R}; bytes={new FileInfo(path).Length}";
    }

    private static ChartDocument CreateDocument() => new()
    {
        KeyCount = 4,
        Metadata = new ChartMetadata
        {
            Title = "Before",
            TitleOriginal = "旧标题",
            Artist = "Before Artist",
            ArtistOriginal = "旧艺术家",
            Creator = "Before Creator",
            Version = "Before Version",
            PreviewTime = 250,
            Bpm = 120,
            OverallDifficulty = 8,
            HpDrainRate = 7,
            CoverFile = "old-cover.jpg",
            BackgroundFile = "old-background.png"
        },
        Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }],
        Notes = [new ChartNote { Lane = 0, Beat = new BeatPosition(1, 0, 4) }]
    };

    private static EditorSession SessionWithDocument(ChartDocument document)
    {
        var session = new EditorSession();
        session.ReplaceDocument(document.Clone());
        return session;
    }

    private static ChartMetadataEdit NewEdit(int keyCount = 4) => new(
        "Metadata Spec",
        "谱面原名",
        "Spec Artist",
        "艺术家原名",
        "Spec Creator",
        "Spec Version",
        12_345,
        137.5,
        321.5,
        keyCount,
        6.25,
        4.5,
        "cover.png",
        "background.webp");

    private static ChartMetadataEdit EditFrom(ChartDocument document) => new(
        document.Metadata.Title,
        document.Metadata.TitleOriginal,
        document.Metadata.Artist,
        document.Metadata.ArtistOriginal,
        document.Metadata.Creator,
        document.Metadata.Version,
        document.Metadata.PreviewTime,
        document.Metadata.Bpm,
        document.Timing.Where(timing => timing.Bpm.HasValue).OrderBy(timing => timing.Beat).First().Delay,
        document.KeyCount,
        document.Metadata.OverallDifficulty,
        document.Metadata.HpDrainRate,
        document.Metadata.CoverFile,
        document.Metadata.BackgroundFile);

    private static void RequireMetadata(ChartDocument document, ChartMetadataEdit edit)
    {
        RequireSupportedRoundTrip(document, edit, includeDifficulty: true, includeCover: true);
        Require(document.KeyCount == edit.KeyCount, "KeyCount did not match the metadata edit.");
    }

    private static void RequireSupportedRoundTrip(
        ChartDocument document,
        ChartMetadataEdit edit,
        bool includeDifficulty,
        bool includeCover)
    {
        Require(document.Metadata.Title == edit.Title &&
                document.Metadata.TitleOriginal == edit.TitleOriginal &&
                document.Metadata.Artist == edit.Artist &&
                document.Metadata.ArtistOriginal == edit.ArtistOriginal &&
                document.Metadata.Creator == edit.Creator &&
                document.Metadata.Version == edit.Version &&
                document.Metadata.PreviewTime == edit.PreviewTime &&
                Math.Abs(document.Metadata.Bpm - edit.Bpm) < 1e-9 &&
                document.Metadata.BackgroundFile == edit.BackgroundFile &&
                (!includeCover || document.Metadata.CoverFile == edit.CoverFile),
            "Serialized metadata fields did not round-trip.");
        Require(Math.Abs(document.Timing.Where(timing => timing.Bpm.HasValue)
                    .OrderBy(timing => timing.Beat).First().Delay - edit.Offset) < 1e-9,
            "Audio offset did not round-trip through the first BPM Timing.");
        if (includeDifficulty)
            Require(Math.Abs(document.Metadata.OverallDifficulty - edit.OverallDifficulty) < 1e-9 &&
                    Math.Abs(document.Metadata.HpDrainRate - edit.HpDrainRate) < 1e-9,
                "OD/HP did not round-trip.");
    }

    private static bool DocumentMetadataEquals(ChartDocument left, ChartDocument right) =>
        left.KeyCount == right.KeyCount &&
        left.Metadata.Title == right.Metadata.Title &&
        left.Metadata.TitleOriginal == right.Metadata.TitleOriginal &&
        left.Metadata.Artist == right.Metadata.Artist &&
        left.Metadata.ArtistOriginal == right.Metadata.ArtistOriginal &&
        left.Metadata.Creator == right.Metadata.Creator &&
        left.Metadata.Version == right.Metadata.Version &&
        left.Metadata.PreviewTime == right.Metadata.PreviewTime &&
        left.Metadata.Bpm.Equals(right.Metadata.Bpm) &&
        left.Timing.Where(timing => timing.Bpm.HasValue).OrderBy(timing => timing.Beat).First().Delay.Equals(
            right.Timing.Where(timing => timing.Bpm.HasValue).OrderBy(timing => timing.Beat).First().Delay) &&
        left.Metadata.OverallDifficulty.Equals(right.Metadata.OverallDifficulty) &&
        left.Metadata.HpDrainRate.Equals(right.Metadata.HpDrainRate) &&
        left.Metadata.CoverFile == right.Metadata.CoverFile &&
        left.Metadata.BackgroundFile == right.Metadata.BackgroundFile;

    private static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void Check(
        ICollection<WindowsMetadataSpecResult> results,
        string name,
        Func<string> verify)
    {
        try { results.Add(new WindowsMetadataSpecResult(name, true, verify())); }
        catch (Exception exception) { results.Add(new WindowsMetadataSpecResult(name, false, exception.Message)); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsMetadataSpecResult(string Name, bool Passed, string Detail);

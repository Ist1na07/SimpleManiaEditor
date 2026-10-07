using System.Security.Cryptography;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Editing;
using LumaChart.Core.Analysis;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsDifficultySpecs
{
    private const string RealFixturePath = @"D:\osu!\Songs\Resound\0\1781536331.mc";
    private const string RealFixtureSha256 =
        "A2828592A4898D35C1DD9FAC5905322760A152810EDCED60601D2F2C7B74E06E";
    private const string AzusaFixturePath =
        @"D:\osu!\Songs\1005389 Minori Chihara - Michishirube\Minori Chihara - Michishirube (eZmmR) [Dear You,  Gilbert Bougainvillea .].osu";
    private const string AzusaFixtureSha256 =
        "511866870F99BDC7443006FA7420C7A9D21198F49B9EC13B50A32293E7F58E57";
    private const string AzusaBetaFixturePath =
        @"D:\osu!\Songs\Dan%20~%20REFORM%20~%20Jack%20-%20v1\Various Artists - Dan ~ REFORM ~ JackMap Pack (DDMythical) [Paradigm Shift ~ Beta ~ (Marathon)].osu";
    private const string AzusaBetaFixtureSha256 =
        "6F4E424656B9B5D233C2D7DE4830924315BD90C6ED8BC405F509920CA356CABE";

    public static IReadOnlyList<WindowsDifficultySpecResult> Run(string isolationRoot)
    {
        Directory.CreateDirectory(isolationRoot);
        var results = new List<WindowsDifficultySpecResult>();
        Check(results, "rework-real-resound-4k-counts-peaks-and-source-immutable",
            VerifyRealFixture);
        Check(results, "rework-qqbot-azusa-rc-reference-parity-keeps-sunny-star",
            VerifyAzusaReferenceParity);
        Check(results, "rework-qqbot-beta-course-does-not-collapse-to-reform-ten",
            VerifyAzusaBetaCourseParity);
        Check(results, "rework-drafts-are-ignored-and-analysis-does-not-create-history",
            VerifyDraftAndHistoryIsolation);
        Check(results, "rework-non-4k-and-fewer-than-two-official-notes-fail-closed",
            VerifyInputGuards);
        Check(results, "rework-od-clamps-to-zero-ten-and-supports-tenth-step-recalculation",
            VerifyOverallDifficulty);
        Check(results, "rework-stale-tab-or-change-serial-result-is-rejected",
            VerifyStaleAnalysisPolicy);
        Check(results, "difficulty-overview-is-single-control-eight-peak-clipped-and-invertible",
            VerifyOverviewGeometry);
        Check(results, "difficulty-card-size-is-stable-across-neighbor-layout-and-drag-clamps",
            VerifyCardLayout);
        return results;
    }

    private static string VerifyRealFixture()
    {
        Require(File.Exists(RealFixturePath), $"Missing real fixture: {RealFixturePath}");
        var source = new FileInfo(RealFixturePath);
        var hashBefore = ComputeSha256(RealFixturePath);
        var lengthBefore = source.Length;
        var writeBefore = source.LastWriteTimeUtc;
        Require(hashBefore == RealFixtureSha256, "Real fixture SHA-256 is not the approved Resound source.");

        var document = MalodyMcSerializer.Load(RealFixturePath);
        var structuralBefore = MalodyMcSerializer.Serialize(document);
        var result = ReworkDifficultyAnalyzer.Analyze(document.Clone());
        var structuralAfter = MalodyMcSerializer.Serialize(document);
        source.Refresh();
        var hashAfter = ComputeSha256(RealFixturePath);

        Require(document.KeyCount == 4, "The real fixture is no longer 4K.");
        Require(result.NoteCount == 2753 && result.LongNoteCount == 771,
            "Rework did not analyze all 2,753 official Notes / 771 LN.");
        Require(result.Peaks.Count is > 0 and <= 8 &&
                result.Peaks.SequenceEqual(result.Peaks.OrderByDescending(peak => peak.Intensity)),
            "Rework peaks are absent, exceed WPF's eight-marker cap, or are not intensity ordered.");
        Require(result.Patterns.Count > 0 && !string.IsNullOrWhiteSpace(result.PatternFamily),
            "Rework did not produce the WPF pattern summary.");
        Require(double.IsFinite(result.StarRating) && result.StarRating > 0,
            "Rework star result is invalid.");
        Require(structuralBefore == structuralAfter,
            "Difficulty analysis mutated the in-memory chart document.");
        Require(hashAfter == hashBefore && source.Length == lengthBefore &&
                source.LastWriteTimeUtc == writeBefore,
            "Difficulty analysis modified the real source file.");
        return $"star={result.StarRating:0.00}; notes={result.NoteCount}; " +
               $"ln={result.LongNoteCount}; peaks={result.Peaks.Count}; " +
               $"pattern={result.PatternFamily}; sha={hashAfter}";
    }

    private static string VerifyDraftAndHistoryIsolation()
    {
        var document = BuildSmallFixture();
        document.Notes.Add(new ChartNote
        {
            Lane = 3,
            Beat = BeatPosition.Preserve(7, 5, 12),
            IsDraft = true,
            DraftColumn = 8
        });
        var session = new EditorSession();
        session.ReplaceDocument(document, markDirty: false);
        var serialBefore = session.ChangeSerial;
        var dirtyBefore = session.IsDirty;
        var serializedBefore = MalodyMcSerializer.Serialize(session.Document);
        var result = ReworkDifficultyAnalyzer.Analyze(session.Document.Clone());
        var serializedAfter = MalodyMcSerializer.Serialize(session.Document);
        var undoApplied = session.Undo();

        Require(result.NoteCount == 12,
            "Draft Note was included in the Rework input boundary.");
        Require(session.ChangeSerial == serialBefore && session.IsDirty == dirtyBefore &&
                serializedBefore == serializedAfter && !undoApplied,
            "Analysis changed session state, dirtiness, document content, or history.");
        return $"official={result.NoteCount}; total={session.Document.Notes.Count}; " +
               $"serial={session.ChangeSerial}; dirty={session.IsDirty}; undo={undoApplied}";
    }

    private static string VerifyAzusaReferenceParity()
    {
        Require(File.Exists(AzusaFixturePath), $"Missing Azusa parity fixture: {AzusaFixturePath}");
        var hashBefore = ComputeSha256(AzusaFixturePath);
        Require(hashBefore == AzusaFixtureSha256,
            "Azusa parity fixture SHA-256 is not the approved source.");

        var document = OsuManiaSerializer.Load(AzusaFixturePath);
        var result = ReworkDifficultyAnalyzer.Analyze(
            document,
            overallDifficulty: document.Metadata.OverallDifficulty);
        var hashAfter = ComputeSha256(AzusaFixturePath);

        Require(Math.Abs(result.StarRating - 4.454314522775622) < 1e-9,
            "Azusa reference grading changed the Sunny Rework star result.");
        Require(result.ReferenceDifficulty == "Reform 4 high",
            $"Azusa reference grade differs from QQBot: {result.ReferenceDifficulty}");
        Require(hashAfter == hashBefore,
            "Azusa reference analysis modified the real osu! source file.");
        return $"star={result.StarRating:R}; reference={result.ReferenceDifficulty}; " +
               $"ln={result.LongNoteRatio:P2}; sha={hashAfter}";
    }

    private static string VerifyAzusaBetaCourseParity()
    {
        Require(File.Exists(AzusaBetaFixturePath),
            $"Missing Azusa Beta parity fixture: {AzusaBetaFixturePath}");
        var hashBefore = ComputeSha256(AzusaBetaFixturePath);
        Require(hashBefore == AzusaBetaFixtureSha256,
            "Azusa Beta parity fixture SHA-256 is not the approved source.");

        var document = OsuManiaSerializer.Load(AzusaBetaFixturePath);
        var result = ReworkDifficultyAnalyzer.Analyze(
            document,
            overallDifficulty: document.Metadata.OverallDifficulty);
        var hashAfter = ComputeSha256(AzusaBetaFixturePath);

        Require(result.NoteCount == 2274 && result.LongNoteCount == 33,
            "Azusa Beta fixture no longer matches the reported 2,274 Note / 33 LN chart.");
        Require(Math.Abs(result.StarRating - 6.988548052165495) < 1e-9,
            $"Azusa Beta reference grading changed LumaChart's Sunny star baseline: " +
            $"actual={result.StarRating:R}; expected=6.988548052165495");
        Require(result.ReferenceDifficulty == "Beta high",
            $"Azusa Beta grade differs from QQBot: {result.ReferenceDifficulty}");
        Require(hashAfter == hashBefore,
            "Azusa Beta reference analysis modified the real osu! source file.");
        return $"star={result.StarRating:R}; reference={result.ReferenceDifficulty}; " +
               $"notes={result.NoteCount}; ln={result.LongNoteCount}; sha={hashAfter}";
    }

    private static string VerifyInputGuards()
    {
        var nonFourKey = BuildSmallFixture();
        nonFourKey.KeyCount = 5;
        RequireThrows<NotSupportedException>(() =>
            ReworkDifficultyAnalyzer.Analyze(nonFourKey));

        var tooFew = BuildSmallFixture();
        for (var index = 1; index < tooFew.Notes.Count; index++)
            tooFew.Notes[index].IsDraft = true;
        RequireThrows<InvalidOperationException>(() =>
            ReworkDifficultyAnalyzer.Analyze(tooFew));
        return "5K=NotSupportedException; officialNotes=1=InvalidOperationException";
    }

    private static string VerifyOverallDifficulty()
    {
        var document = BuildSmallFixture();
        var low = ReworkDifficultyAnalyzer.Analyze(document.Clone(), overallDifficulty: -2);
        var middle = ReworkDifficultyAnalyzer.Analyze(document.Clone(), overallDifficulty: 7.3);
        var high = ReworkDifficultyAnalyzer.Analyze(document.Clone(), overallDifficulty: 14);
        Require(low.OverallDifficulty == 0 && middle.OverallDifficulty == 7.3 &&
                high.OverallDifficulty == 10,
            "OD clamp or 0.1-step preservation differs from WPF.");
        Require(double.IsFinite(low.StarRating) && double.IsFinite(middle.StarRating) &&
                double.IsFinite(high.StarRating),
            "OD boundary analysis returned non-finite results.");
        return $"od={low.OverallDifficulty:0.0}/{middle.OverallDifficulty:0.0}/" +
               $"{high.OverallDifficulty:0.0}; star={low.StarRating:0.00}/" +
               $"{middle.StarRating:0.00}/{high.StarRating:0.00}";
    }

    private static string VerifyStaleAnalysisPolicy()
    {
        var target = Guid.NewGuid();
        Require(WindowsDifficultyUiPolicy.IsAnalysisCurrent(target, 42, target, 42),
            "Current tab/serial was rejected.");
        Require(!WindowsDifficultyUiPolicy.IsAnalysisCurrent(target, 42, Guid.NewGuid(), 42),
            "A result from another tab was accepted.");
        Require(!WindowsDifficultyUiPolicy.IsAnalysisCurrent(target, 42, target, 43),
            "A stale document serial was accepted.");
        Require(!WindowsDifficultyUiPolicy.IsAnalysisCurrent(target, 42, null, -1),
            "A result was accepted without an active tab.");
        return $"target={target}; current=true; otherTab=false; staleSerial=false; noTab=false";
    }

    private static string VerifyOverviewGeometry()
    {
        var peaks = Enumerable.Range(0, 12)
            .Select(index => new ReworkDifficultyPeak(
                BeatPosition.FromDouble(index + .5),
                index * 1000,
                index % 3 == 0 ? 40 - index : index))
            .ToArray();
        var result = new ReworkDifficultyResult(
            7.25,
            "Lv.17",
            12,
            3,
            .25,
            1,
            8,
            peaks,
            "Jack",
            [new ReworkPatternContribution("Jack", 1)]);
        var surface = new DifficultyOverviewSurface();
        surface.SetResult(result);
        surface.SetEndBeat(32);
        surface.SetCurrentBeat(11.25);
        var diagnostics = surface.GetDiagnostics();
        var beat = 17d / 3;
        var y = DifficultyOverviewSurface.BeatToY(beat, 32, 640);
        var roundTrip = DifficultyOverviewSurface.YToBeat(y, 32, 640);

        Require(diagnostics.PeakCount == 8 && diagnostics.VisualChildCount == 0,
            "Overview created per-Peak controls or did not enforce the eight-Peak view.");
        Require(diagnostics.PeakBeats.SequenceEqual(
                peaks.OrderByDescending(peak => peak.Intensity)
                    .ThenBy(peak => peak.Beat)
                    .Take(8)
                    .Select(peak => peak.Beat.Value)),
            "Overview Peak order differs from the WPF intensity order.");
        Require(Math.Abs(roundTrip - beat) < 1e-12,
            "DIFF Y-to-Beat mapping did not round-trip.");
        return $"peaks={diagnostics.PeakCount}; visualChildren={diagnostics.VisualChildCount}; " +
               $"roundTrip={roundTrip:R}; current={diagnostics.CurrentBeat:R}";
    }

    private static string VerifyCardLayout()
    {
        var expanded = WindowsDifficultyUiPolicy.ResolveCardLayout(
            true, 1200, 700, 300, null, false, 5000, -5000, 310);
        var narrow = WindowsDifficultyUiPolicy.ResolveCardLayout(
            true, 900, 600, 150, null, false, 0, 0, 310);
        var avoided = WindowsDifficultyUiPolicy.ResolveCardLayout(
            true, 1200, 700, 380, 210, false, 0, 0, 310);
        var explicitlyCollapsed = WindowsDifficultyUiPolicy.ResolveCardLayout(
            true, 900, 600, 150, null, true, 0, 0, 999);
        var hidden = WindowsDifficultyUiPolicy.ResolveCardLayout(
            false, 1200, 700, 300, null, false, 0, 0, 310);
        var drag = WindowsDifficultyUiPolicy.ApplyDrag(
            9000, 9000, 1000, 600, 220, 310);

        Require(expanded.Visible && !expanded.Collapsed &&
                expanded.Width == WindowsDifficultyUiPolicy.DefaultCardWidth &&
                expanded.X <= 1200 - expanded.Width,
            "Expanded difficulty card did not keep its independent fixed width.");
        Require(narrow.Visible && !narrow.Collapsed &&
                narrow.Width == WindowsDifficultyUiPolicy.DefaultCardWidth &&
                narrow.AvailableWidth == 129,
            "Narrow pre-track space resized or auto-collapsed the difficulty card.");
        Require(avoided.AvailableWidth == 189 &&
                avoided.Width == WindowsDifficultyUiPolicy.DefaultCardWidth &&
                !avoided.Collapsed,
            "A neighboring waveform panel resized or collapsed the difficulty card.");
        Require(explicitlyCollapsed.Collapsed &&
                explicitlyCollapsed.Width == WindowsDifficultyUiPolicy.DefaultCardWidth &&
                WindowsDifficultyUiPolicy.ResolveCardHeight(false) ==
                WindowsDifficultyUiPolicy.ExpandedCardHeight &&
                WindowsDifficultyUiPolicy.ResolveCardHeight(true) ==
                WindowsDifficultyUiPolicy.CollapsedCardHeight,
            "Difficulty card fixed sizes or explicit collapse state changed.");
        Require(!hidden.Visible, "Difficulty card remained visible for a non-4K chart.");
        Require(drag.X == 765 && drag.Y == 272,
            "Difficulty card drag did not clamp to the four-DIP host edge.");
        return $"expanded={expanded.Width}x{WindowsDifficultyUiPolicy.ExpandedCardHeight}/" +
               $"{expanded.Collapsed}; narrow={narrow.Width}/{narrow.Collapsed}; " +
               $"avoided={avoided.Width}; collapsedHeight=" +
               $"{WindowsDifficultyUiPolicy.CollapsedCardHeight}; " +
               $"drag={drag.X}/{drag.Y}; non4K={hidden.Visible}";
    }

    private static ChartDocument BuildSmallFixture()
    {
        var document = MalodyMcSerializer.Load(RealFixturePath).Clone();
        document.Notes = document.Notes
            .Where(note => !note.IsDraft)
            .OrderBy(note => note.Beat)
            .ThenBy(note => note.Lane)
            .Take(12)
            .Select(note => note.Clone())
            .ToList();
        return document;
    }

    private static string ComputeSha256(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void RequireThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Check(
        ICollection<WindowsDifficultySpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsDifficultySpecResult(name, true, verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsDifficultySpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsDifficultySpecResult(
    string Name,
    bool Passed,
    string Detail);

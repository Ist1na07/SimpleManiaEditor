using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Specs;

internal static class OsuTimingExperienceSpecs
{
    internal const string Fixture = """
        osu file format v14
        [General]
        Mode:3
        [Metadata]
        Title:Timing
        Artist:Fixture
        Creator:Mapper
        Version:7K
        [Difficulty]
        CircleSize:7
        [TimingPoints]
        813.333333333319,333.333333333333,4,2,1,70,1,8
        [HitObjects]
        36,192,1100,1,0,0:0:0:0:
        256,192,1500,128,0,1800:0:0:0:0:
        """;
    public static void Run()
    {
        var session = new EditorSession(); session.ReplaceDocument(OsuManiaSerializer.Parse(Fixture));
        Require(!session.CanUndo && !session.CanRedo, "fresh history is enabled");
        var point = session.Document.Timing.Single();
        Require(OsuTimingPointSettings.Read(point).Effects == 8, "native effect bits were not read");
        var settings = new OsuTimingPointSettings(3, 3, 4, 37, 9);
        var id = OsuTimingEditingService.Apply(session, null, new OsuTimingEdit(937.8, false, 1.5, settings));
        var green = session.Document.Timing.Single(t => t.Id == id);
        Require(Math.Abs(new TimingMap(session.Document.Timing).BeatToMilliseconds(green.Beat) - 938) < .001,
            "absolute event time was not rounded to integer milliseconds");
        var text = NativeText(session.Document);
        Require(text.Contains("938,") && text.Contains(",3,3,4,37,0,9"), "SV/Kiai/bank/index/volume were not exported");
        var recovery = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(session.Document));
        var recoveredGreen = recovery.Timing.Single(t => t.Effect == TimingEffectKind.Scroll);
        Require(OsuTimingPointSettings.Read(recoveredGreen) == settings, "MC recovery lost native green-line attributes");
        Require(NativeText(recovery).Contains(",3,3,4,37,0,9"), "recovery export lost Kiai");
        Require(session.CanUndo && !session.CanRedo && session.Undo() && !session.CanUndo && session.CanRedo,
            "add/undo history limits failed");
        Require(session.Document.Timing.Count == 1 && session.Redo() && !session.CanRedo, "undo/redo did not atomically restore event");
        var firstId = session.Document.Timing.First(t => t.Bpm.HasValue).Id;
        OsuTimingEditingService.Apply(session, firstId, new OsuTimingEdit(801.7, true, 180, new OsuTimingPointSettings(4, 2, 1, 70, 8)));
        var first = session.Document.Timing.First(t => t.Bpm.HasValue);
        Require(Math.Abs(new TimingMap(session.Document.Timing).BeatToMilliseconds(first.Beat) - 802) < .001,
            "first BPM point offset did not move to requested time");
        text = NativeText(session.Document);
        Require(text.Contains("36,192,1100,") && text.Contains("256,192,1500,128,0,1800:"), "timing edit changed existing absolute note times");
        var earlyBeat = new TimingMap(session.Document.Timing).MillisecondsToBeat(500, BeatPosition.MaximumPreservedDenominator);
        session.Mutate(d => d.Notes.Add(new ChartNote { Lane = 2, Beat = earlyBeat }));
        Require(session.Document.Notes.Any(n => n.Lane == 2 && n.Beat.Value < 0), "native note before offset was removed");
        text = NativeText(session.Document);
        Require(text.Contains("182,192,500,"), "note before first red line was not exported at its actual time");
        var offGrid = BeatPosition.Preserve(1, 337, 999_983);
        var moved = BasicChartEditingService.ApplyRelativeMove(offGrid, new RelativeBeatMove(2, 4, .25 - offGrid.Value % .25, true));
        Require(Math.Abs(moved.Value * 4 - Math.Round(moved.Value * 4)) < 1e-12, "snapped drag retained imported denominator");
        var fineHead = new BeatPosition(4, 1, 16); var fineTail = new BeatPosition(4, 3, 32);
        var coarseMove = new RelativeBeatMove(1, 4, SnapAbsolute: true);
        Require(BasicChartEditingService.ApplyRelativeMove(fineHead, coarseMove).Value == 4.3125 &&
                BasicChartEditingService.ApplyRelativeMove(fineTail, coarseMove).Value == 4.34375,
            "coarser grid erased finer authored rhythm or collapsed a short LN");
        var reloaded = OsuManiaSerializer.Parse(text);
        Require(reloaded.Notes.Count == session.Document.Notes.Count, "before-offset note disappeared after native reload");
        var beforeSerial = session.ChangeSerial;
        try { OsuTimingEditingService.Apply(session, null, new OsuTimingEdit(double.NaN, true, 120, settings)); }
        catch (ArgumentException) { }
        Require(session.ChangeSerial == beforeSerial, "invalid timing partially committed");
    }
    private static string NativeText(ChartDocument document) { if (!OsuSourceEnvelope.TrySerialize(document, null, null, out var text)) throw new InvalidOperationException(); return text; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class CenterSymmetrySpecs
{
    public static void Run()
    {
        var settings = new CenterSymmetrySettings(new(8, 0, 1), new(16, 0, 1), Enumerable.Range(0, 7).ToArray());
        var tap = new ChartNote { Lane = 0, Beat = new(9, 0, 1) };
        var mirror = CenterSymmetryService.CreateCompanion(tap, settings, 7)!;
        Require(mirror.Lane == 6 && mirror.Beat.Value == 15 && mirror.Id != tap.Id, "7K tap symmetry is wrong");
        var hold = new ChartNote { Lane = 2, Beat = new(9, 0, 1), EndBeat = new(11, 0, 1), Volume = 37, Sound = "hit.wav" };
        mirror = CenterSymmetryService.CreateCompanion(hold, settings, 7)!;
        Require(mirror.Lane == 4 && mirror.Beat.Value == 13 && mirror.EndBeat!.Value.Value == 15 && mirror.Volume == 37 && mirror.Sound == "hit.wav",
            "LN symmetry lost endpoint order or hitsound");
        Require(CenterSymmetryService.CreateCompanion(new ChartNote { Lane = 3, Beat = new(12, 0, 1) }, settings, 7) == null,
            "self-symmetric middle note was doubled");
        Require(CenterSymmetryService.CreateCompanion(hold.WithLane(1), settings with { Columns = new[] { 0, 2, 4 } }, 7) == null,
            "excluded column was reflected");
        mirror = CenterSymmetryService.CreateCompanion(tap, settings with { Columns = new[] { 4, 0, 2 } }, 7)!;
        Require(mirror.Lane == 4, "partial column order was not canonicalized");
        Require(CenterSymmetryService.CreateCompanion(new ChartNote { Lane = 0, Beat = new(7, 0, 1), EndBeat = new(9, 0, 1) }, settings, 7) == null,
            "a partially out-of-range LN was clipped/reflected");
        var fine = new CenterSymmetrySettings(new(0, 1, 7), new(2, 1, 9), new[] { 0, 1 });
        var fineNote = new ChartNote { Lane = 0, Beat = new(1, 2, 7) };
        mirror = CenterSymmetryService.CreateCompanion(fineNote, fine, 7)!;
        var restored = CenterSymmetryService.CreateCompanion(mirror, fine, 7)!;
        Require(restored.Beat.CompareTo(fineNote.Beat) == 0 && restored.Lane == fineNote.Lane,
            "fine rational reflection does not round-trip exactly");
        var session = new EditorSession(); var doc = new ChartDocument { KeyCount = 7 }; doc.Notes.AddRange([tap, hold]); session.ReplaceDocument(doc);
        session.Selection.UnionWith([tap.Id, hold.Id]);
        Require(CenterSymmetryService.CompleteSelection(session, settings) == 2 && session.Document.Notes.Count == 4, "selection did not complete");
        var serial = session.ChangeSerial; Require(CenterSymmetryService.CompleteSelection(session, settings) == 0 && serial == session.ChangeSerial, "existing pairs added duplicates/history");
        session.Undo(); Require(session.Document.Notes.Count == 2, "selection completion was not one undo");
        session.Mutate(d => d.Notes.Add(new ChartNote { Lane = 6, Beat = new(15, 0, 1), EndBeat = new(16, 0, 1) }));
        serial = session.ChangeSerial;
        Require(CenterSymmetryService.CompleteSelection(session, settings) == 1 && session.Document.Notes.Count == 4,
            "occupied mirrored head blocked the other free companion");
        session.Undo(); session.Undo(); session.AllowOwnerProtectedEdits = false; session.AddOwnerProtectedRegion(new(14, 0, 1), new(16, 0, 1));
        try { CenterSymmetryService.CompleteSelection(session, settings); throw new Exception("protected region accepted"); }
        catch (InvalidOperationException) { }
        Require(session.Document.Notes.Count == 2, "protected destination partially changed chart");
        var negative = OsuManiaSerializer.Parse("osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n1000,500,4,0,0,100,1,0\n[HitObjects]\n");
        var early = new ChartNote { Lane = 0, Beat = new(-1, 1, 4) }; negative.Notes.Add(early); session.ReplaceDocument(negative); session.Selection.Add(early.Id);
        Require(CenterSymmetryService.CompleteSelection(session, new(new(-2, 0, 1), BeatPosition.Zero, new[] { 0, 6 })) == 1 && session.Document.Notes.Any(n => n.Beat.Value == -1.25),
            "native before-offset symmetry failed");
        var fractional = new ChartNote { Lane = 0, Beat = BeatPosition.Preserve(9, 1, 2) };
        session.ReplaceDocument(new ChartDocument { KeyCount = 7, Notes = [fractional,
            new ChartNote { Lane = 6, Beat = BeatPosition.Preserve(14, 3, 6) }] });
        session.Selection.Add(fractional.Id); serial = session.ChangeSerial;
        Require(CenterSymmetryService.CompleteSelection(session, settings) == 0 && session.ChangeSerial == serial,
            "equivalent grid fractions were not recognised as an existing pair");
    }
    private static ChartNote WithLane(this ChartNote note, int lane) { var copy = note.Clone(); copy.Lane = lane; return copy; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

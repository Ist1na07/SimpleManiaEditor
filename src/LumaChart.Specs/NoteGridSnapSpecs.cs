using LumaChart.Application.Editing;
using LumaChart.Core.Models;
using LumaChart.Core.IO;

internal static class NoteGridSnapSpecs
{
    public static void Run()
    {
        var doc = OsuManiaSerializer.Parse("osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n1000,500,4,0,0,100,1,0\n[HitObjects]\n");
        var tap = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(.249, 1_000_000) };
        var hold = new ChartNote { Lane = 2, Beat = BeatPosition.FromDouble(-.126, 1_000_000), EndBeat = BeatPosition.FromDouble(.003, 1_000_000) };
        doc.Notes.AddRange([tap, hold]);
        var session = new EditorSession(); session.ReplaceDocument(doc); session.SnapDivision = 4;
        session.Selection.Add(tap.Id);
        Require(NoteGridSnapService.Apply(session, false) == 1 && session.Document.Notes.Single(n => n.Id == tap.Id).Beat.Value == .25,
            "selected snap changed the wrong notes");
        Require(session.Document.Notes.Single(n => n.Id == hold.Id).Beat.Value == -.126, "selected snap changed an unselected LN");
        session.Undo(); Require(session.Document.Notes.Single(n => n.Id == tap.Id).Beat.Value == .249, "snap undo lost the source position");
        Require(NoteGridSnapService.Apply(session, true) == 2, "all-note snap did not include the LN");
        var snappedHold = session.Document.Notes.Single(n => n.Id == hold.Id);
        Require(snappedHold.Beat.Value == -.25 && snappedHold.EndBeat!.Value.Value == 0, "negative grid snapping is wrong");
        var serial = session.ChangeSerial; Require(NoteGridSnapService.Apply(session, true) == 0 && serial == session.ChangeSerial,
            "an already snapped chart added a useless undo step");
        session.Undo(); Require(session.Document.Notes.Single(n => n.Id == hold.Id).Beat.Value == -.126,
            "all-note snapping was not one undo step");
        session.IsReadOnly = true; Require(NoteGridSnapService.Apply(session, true) == 0, "read-only chart was snapped");
        session.IsReadOnly = false; session.SetOwnerProtection([tap.Id], true);
        session.AllowOwnerProtectedEdits = false;
        Require(NoteGridSnapService.Apply(session, true) == 1 && session.Document.Notes.Single(n => n.Id == tap.Id).Beat.Value == .249,
            "batch snap changed a protected note");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

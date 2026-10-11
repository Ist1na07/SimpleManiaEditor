using LumaChart.Application.Editing;
using LumaChart.Core.Models;

internal static class NoteTimeFlipSpecs
{
    public static void Run()
    {
        var session = new EditorSession();
        var tap = new ChartNote { Lane = 0, Beat = new(2, 1, 7), Sound = "hit.wav", Volume = 37 };
        var hold = new ChartNote { Lane = 6, Beat = new(4, 2, 7), EndBeat = new(6, 1, 7) };
        var document = new ChartDocument { KeyCount = 7 }; document.Notes.AddRange([tap, hold]); session.ReplaceDocument(document);
        Require(NoteTimeFlipEditingService.FlipSelection(session, [tap.Id, hold.Id]) == 2, "flip did not change both notes");
        var flippedTap = session.Document.Notes.Single(n => n.Id == tap.Id);
        var flippedHold = session.Document.Notes.Single(n => n.Id == hold.Id);
        Require(flippedTap.Beat.CompareTo(new BeatPosition(6, 1, 7)) == 0 && flippedTap.Lane == 0 && flippedTap.Sound == "hit.wav" && flippedTap.Volume == 37,
            "flip lost rational position, lane or audio metadata");
        Require(flippedHold.Beat.CompareTo(new BeatPosition(2, 1, 7)) == 0 && flippedHold.EndBeat!.Value.CompareTo(new BeatPosition(4, 0, 1)) == 0,
            "LN head/tail were not reflected in reverse order");
        Require(session.Undo() && session.Document.Notes.Single(n => n.Id == tap.Id).Beat == new BeatPosition(2, 1, 7), "flip was not one undo");
        session.AllowOwnerProtectedEdits = false; session.AddOwnerProtectedRegion(new(5, 0, 1), new(7, 0, 1));
        var serial = session.ChangeSerial;
        try { NoteTimeFlipEditingService.FlipSelection(session, [tap.Id, hold.Id]); throw new Exception("locked destination allowed"); }
        catch (InvalidOperationException) { }
        Require(serial == session.ChangeSerial, "denied flip partially mutated chart");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

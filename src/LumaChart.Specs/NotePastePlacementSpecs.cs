using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Specs;

internal static class NotePastePlacementSpecs
{
    public static void Run()
    {
        var session = new EditorSession { SnapDivision = 16 };
        session.ReplaceDocument(OsuManiaSerializer.Parse("osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n475,500,4,2,1,0,1,0\n[HitObjects]\n"));
        var source = NoteTextCodeService.Parse(session.Document, "// SimpleManiaEditor keys:7\n[HitObjects]\n182,192,2000,1,0,0:0:0:0:\n329,192,2250,128,0,2750:0:0:0:0:");
        if (session.Document.Notes.Count != 0 || source.Length != 2) throw new Exception("Parse modified the document.");
        var preview = NotePastePlacement.Preview(source, new(-1, 1, 4), 6, 7);
        if (preview[0].Lane != 4 || preview[1].Lane != 6 || preview[0].Beat.Value != -.75 || preview[1].Beat.Value != -.25 || preview[1].EndBeat!.Value.Value != .75)
            throw new Exception("Placement lost relative time, negative beat or bounded lane shift.");
        if (NotePastePlacement.Commit(session, preview) != 2 || session.Selection.Count != 2) throw new Exception("Paste did not commit atomically.");
        if (NotePastePlacement.Commit(session, preview) != 0) throw new Exception("Exact overlap was duplicated.");
        if (!session.Undo() || session.Document.Notes.Count != 0 || !session.Redo() || session.Document.Notes.Count != 2)
            throw new Exception("Paste undo/redo failed.");
        var triplet = NotePastePlacement.Preview([new() { Lane = 0, Beat = new(1, 0, 1) }, new() { Lane = 1, Beat = new(1, 1, 3) }], new(2, 1, 4), 0, 7);
        if (triplet[1].Beat != new BeatPosition(2, 7, 12)) throw new Exception("Paste lost exact triplet arithmetic.");
        session.IsReadOnly = true;
        try { NotePastePlacement.Commit(session, source); throw new Exception("Read-only paste was allowed."); }
        catch (InvalidOperationException) { }
        session.IsReadOnly = false; session.AllowOwnerProtectedEdits = false;
        session.AddOwnerProtectedRegion(new(-1, 0, 1), new(1, 0, 1));
        try { NotePastePlacement.Commit(session, preview); throw new Exception("Protected paste was allowed."); }
        catch (InvalidOperationException) { }
    }
}

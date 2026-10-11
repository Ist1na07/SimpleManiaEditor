using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

internal static class NoteTextCodeSpecs
{
    public static void Run()
    {
        var doc = OsuManiaSerializer.Parse("osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n1000,500,4,0,0,100,1,0\n3000,400,4,0,0,100,1,0\n[HitObjects]\n36,192,1251,1,8,2:3:4:37:hit.wav\n475,192,2750,128,4,3500:1:2:3:55:tail.wav\n", noteTimingMode: OsuNoteTimingImportMode.PreserveMilliseconds);
        var code = NoteTextCodeService.Export(doc, doc.Notes);
        Require(code.Contains("1251,1,8,2:3:4:37:hit.wav") && code.Contains("3500:1:2:3:55:tail.wav"), "source sample fields were changed");
        var target = new EditorSession(); target.ReplaceDocument(doc.Clone()); target.Mutate(d => d.Notes.Clear());
        Require(NoteTextCodeService.Import(target, code) == 2, "import did not read both rows");
        var map = new TimingMap(target.Document.Timing);
        Require(Math.Abs(map.BeatToMilliseconds(target.Document.Notes[0].Beat) - 1251) < .001 &&
            Math.Abs(map.BeatToMilliseconds(target.Document.Notes[1].EndBeat!.Value) - 3500) < .001, "absolute import moved times across tempo changes");
        Require(target.Undo() && target.Document.Notes.Count == 0, "import is not one undo");
        NoteTextCodeService.Import(target, code, 4333);
        Require(Math.Abs(map.BeatToMilliseconds(target.Document.Notes[0].Beat) - 4333) < .001 &&
            Math.Abs(map.BeatToMilliseconds(target.Document.Notes[1].EndBeat!.Value) - 6582) < .001, "paste did not preserve millisecond offsets/LN length");
        var serial = target.ChangeSerial;
        foreach (var invalid in new[] { code + "\nmalformed", code.Replace("keys:7", "keys:4"), "36,192,2000,128,0,1000:0:0:0:0:" })
        {
            try { NoteTextCodeService.Import(target, invalid); throw new Exception("invalid input accepted"); }
            catch (ArgumentException) { }
            Require(target.ChangeSerial == serial, "invalid code partly modified chart");
        }
        target.IsReadOnly = true;
        try { NoteTextCodeService.Import(target, code); throw new Exception("readonly accepted"); } catch (InvalidOperationException) { }
        target.IsReadOnly = false; target.AllowOwnerProtectedEdits = false; target.AddOwnerProtectedRegion(new(6, 0, 1), new(20, 0, 1));
        try { NoteTextCodeService.Import(target, code, 4333); throw new Exception("protected target accepted"); } catch (InvalidOperationException) { }
        Require(target.ChangeSerial == serial, "permissions changed history");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

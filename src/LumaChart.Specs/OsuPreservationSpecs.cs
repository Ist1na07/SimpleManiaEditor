using System.Text;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Specs;

internal static class OsuPreservationSpecs
{
    private const string Fixture = """
        osu file format v14
        // retained source comment
        [General]
        AudioFilename: audio.mp3
        Mode: 3
        SampleSet: Normal
        PreviewTime: -1
        [Editor]
        Bookmarks: 1000,2000
        BeatDivisor: 7
        [Metadata]
        Title: 保真测试
        Artist: Artist
        Creator: Mapper
        Version: 7K
        Source: ProbeSource
        Tags: preserve-me
        BeatmapID:42
        BeatmapSetID:43
        [Difficulty]
        CircleSize:7
        OverallDifficulty:8
        HPDrainRate:6
        SliderMultiplier:1.8
        [Events]
        0,0,"background.jpg",0,0
        1,0,"video.mp4",0,0
        2,3000,5000
        5,2000,0,"sample.wav",80
        Sprite,Foreground,Centre,"sprite.png",320,240
         F,0,0,1000,0,1
        [TimingPoints]
        0,500,3,3,2,37,1,1
        1000,-50,3,2,3,45,0,8
        [HitObjects]
        // note comment
        36,192,1000,1,6,2:3:4:0:custom.wav
        256,192,1500,128,8,2000:3:2:1:50:tail.wav
        [Unknown]
        Keep: yes
        """;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "sme-preservation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var text in new[] { Fixture, Fixture.Replace("\n", "\r\n") + "\r\n" })
            {
                var file = Path.Combine(root, "source.osu");
                File.WriteAllText(file, text, new UTF8Encoding(true));
                var original = File.ReadAllBytes(file);
                var map = OsuManiaSerializer.Load(file);
                OsuManiaSerializer.Save(map.Clone(), file);
                Require(File.ReadAllBytes(file).SequenceEqual(original), "unchanged source bytes/BOM/newlines changed");
                var recovered = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(map));
                OsuManiaSerializer.Save(recovered, file);
                Require(File.ReadAllBytes(file).SequenceEqual(original), "recovery copy lost the osu envelope: " + File.ReadAllText(file));
            }
            var document = OsuManiaSerializer.Parse(Fixture);
            document.Notes[0].Lane = 1;
            document.Notes[0].Beat = BeatPosition.FromDouble(2.5);
            var moved = Save(document, root);
            Require(moved.Contains("109,192,1250,1,6,2:3:4:0:custom.wav"), "moved note lost hitsound/volume inheritance");
            Require(moved.Contains("256,192,1500,128,8,2000:3:2:1:50:tail.wav"), "untouched LN changed");
            AssertAuxiliaryData(moved);

            document = OsuManiaSerializer.Parse(Fixture);
            document.Notes[1].EndBeat = BeatPosition.FromDouble(5);
            document.Notes[1].Volume = 73;
            var resized = Save(document, root);
            Require(resized.Contains("2500:3:2:1:73:tail.wav"), "LN resize or sample attributes lost");
            AssertAuxiliaryData(resized);

            document = OsuManiaSerializer.Parse(Fixture);
            document.Notes.RemoveAt(0);
            document.Notes.Add(new ChartNote { Lane = 6, Beat = BeatPosition.FromDouble(6) });
            var replaced = Save(document, root);
            Require(!replaced.Contains("custom.wav") && replaced.Contains("475,192,3000,1,0"), "delete/add did not replace hit objects");
            AssertAuxiliaryData(replaced);

            var fourKey = OsuManiaSerializer.Parse(Fixture.Replace("CircleSize:7", "CircleSize:4", StringComparison.Ordinal));
            var copied = fourKey.Notes[1].Clone();
            copied.Beat = BeatPosition.FromDouble(8);
            copied.EndBeat = BeatPosition.FromDouble(10);
            var sevenKey = OsuManiaSerializer.Parse(Fixture);
            sevenKey.Notes.Add(copied);
            Require(Save(sevenKey, root).Contains("182,192,4000,128,8,5000:"), "cross-layout paste reused source x instead of target column");

            var session = new EditorSession();
            session.ReplaceDocument(OsuManiaSerializer.Parse(Fixture));
            session.Mutate(d => d.Timing[0].Bpm = 240);
            var retimed = Save(session.Document, root);
            Require(retimed.Contains(",1000,1,6,") && retimed.Contains(",1500,128,8,2000:"), "BPM edit changed untouched absolute note times");
            Require(retimed.Contains("0,250,3,3,2,37,1,1"), "BPM edit reset sample/kiai/meter");
            Require(retimed.Contains("1000,-50,3,2,3,45,0,8"), "BPM edit moved inherited timing in absolute time");
            Require(session.Undo() && Save(session.Document, root) == Fixture, "timing undo did not restore source");
            session.Mutate(d => { d.Timing[0].Delay = -2000; d.Notes[0].Volume = 61; });
            var shiftedGrid = Save(session.Document, root);
            Require(session.Document.Notes.Count == 2 && shiftedGrid.Contains(",1000,1,6,2:3:4:61:"), "offset change erased early notes or concurrent property edit");

            VerifyWorkspaceAsync(root).GetAwaiter().GetResult();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyWorkspaceAsync(string root)
    {
        var file = Path.Combine(root, "native.osu");
        File.WriteAllText(file, Fixture, new UTF8Encoding(true));
        var workspace = new DesktopDocumentWorkspace(Path.Combine(root, "library"), Path.Combine(root, "editing"), "Mapper");
        await workspace.InitializeAsync();
        var tab = await workspace.OpenAsync(file);
        tab.Session.Mutate(d => d.Notes[0].Volume = 63);
        await workspace.PersistAsync();
        var saved = await workspace.SaveAsync(tab.Id);
        Require(saved.Status == DesktopChartSaveStatus.Saved, "native workspace save failed");
        AssertAuxiliaryData(File.ReadAllText(file));
        Require(File.ReadAllText(file + ".sme-backup") == Fixture, "previous source backup missing");
        tab.Session.Mutate(d => d.Metadata.Title = "changed");
        File.AppendAllText(file, "\n// external change");
        var disk = File.ReadAllBytes(file);
        var rejected = false;
        try { await workspace.SaveAsync(tab.Id); }
        catch (IOException) { rejected = true; }
        Require(rejected && File.ReadAllBytes(file).SequenceEqual(disk), "external modification overwritten");
        var recovered = new DesktopDocumentWorkspace(Path.Combine(root, "library"), Path.Combine(root, "editing"), "Mapper");
        await recovered.InitializeAsync();
        Require(recovered.Tabs.Any(t => t.Session.Document.Metadata.Title == "changed"), "failed save lost recovery copy");
        var created = await workspace.CreateAsync(new ChartDocument { KeyCount = 7 }, "new", format: DesktopChartFormat.OsuMania);
        Require(created.Format == DesktopChartFormat.OsuMania && created.SourcePath.EndsWith(".osu") && created.Session.Document.KeyCount == 7,
            "new osu workspace was converted to MC");
    }

    private static string Save(ChartDocument document, string root)
    {
        var file = Path.Combine(root, "output.osu");
        OsuManiaSerializer.Save(document, file);
        return File.ReadAllText(file);
    }
    private static void AssertAuxiliaryData(string text)
    {
        foreach (var field in new[] { "Source: ProbeSource", "Tags: preserve-me", "BeatmapID:42", "BeatmapSetID:43", "Bookmarks: 1000,2000",
            "PreviewTime: -1", "SliderMultiplier:1.8", "video.mp4", "2,3000,5000", "sample.wav", "sprite.png", " F,0,0,1000,0,1", "[Unknown]", "Keep: yes" })
            Require(text.Contains(field, StringComparison.Ordinal), "lost source field: " + field);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

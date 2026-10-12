using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Specs;

internal static class OffsetPersistenceSpecs
{
    public static void Run()
    {
        var session = new EditorSession(); session.ReplaceDocument(OsuManiaSerializer.Parse(OsuTimingExperienceSpecs.Fixture));
        var first = TimingMap.FindSongOffsetTiming(session.Document.Timing)!;
        OsuTimingEditingService.Apply(session, first.Id, new(1200, true, 180, OsuTimingPointSettings.Read(first)));
        CheckRoundTrip(session.Document, 1200);
        var cached = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(session.Document));
        CheckRoundTrip(cached, 1200);
        session.ReplaceDocument(cached);
        var draft = ChartMetadataEditingService.Capture(cached) with { Offset = "-1401" };
        Require(ChartMetadataEditingService.TryParseDraft(draft, out var edit, out _), "offset draft rejected");
        ChartMetadataEditingService.Apply(session, edit!, false);
        CheckRoundTrip(session.Document, 1401);
        Require(session.Undo(), "offset was not undoable"); CheckRoundTrip(session.Document, 1200);
        Require(session.Redo(), "offset was not redoable"); CheckRoundTrip(session.Document, 1401);

        var legacyText = OsuTimingExperienceSpecs.Fixture + "\n109,192,500,1,0,0:0:0:0:\n";
        var legacy = OsuManiaSerializer.Parse(legacyText);
        foreach (var note in legacy.Notes)
        {
            note.Beat = BeatPosition.FromDouble(note.Beat.Value + 4, BeatPosition.MaximumPreservedDenominator);
            if (note.EndBeat is { } end) note.EndBeat = BeatPosition.FromDouble(end.Value + 4, BeatPosition.MaximumPreservedDenominator);
            OsuSourceEnvelope.AttachNote(note, note.UnknownData!["_sme_osu_object"]!["line"]!.GetValue<string>(), 7);
        }
        legacy.Timing[0].Beat = new(4, 0, 1); legacy.Timing[0].Delay = 0;
        legacy.Timing.Insert(0, new TimingEvent { Beat = BeatPosition.Zero, Bpm = 180, Delay = 4 * 60000d / 180 - 813.333333333319 });
        OsuSourceEnvelope.Capture(legacy, legacyText);
        legacy.Notes[0].Volume = 37;
        var migrated = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(legacy));
        Require(migrated.Timing.Count(t => t.Bpm.HasValue) == 1 && Math.Abs(-TimingMap.FindSongOffsetTiming(migrated.Timing)!.Delay - 813.333333333319) < .001,
            "legacy untouched lead-in cache did not recover native offset");
        Require(migrated.Notes[0].Beat.Value < 0 && migrated.Notes[0].Volume == 37, "legacy migration lost early note/property edit");
        OsuSourceEnvelope.TrySerialize(migrated, null, null, out var migratedText);
        Require(migratedText.Contains("0:0:0:37:"), "legacy migration masked note volume edit");
        legacy.Timing[0].Delay += 1;
        Require(MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(legacy)).Timing.Count(t => t.Bpm.HasValue) == 2, "edited legacy timing was guessed/overwritten");

        var document = new ChartDocument { KeyCount = 7 };
        new OsuTimingPointSettings(4, 2, 1, 0).Apply(document.GetOrCreateSongOffsetTiming());
        var file = Path.Combine(Path.GetTempPath(), "sme-zero-sample-" + Guid.NewGuid().ToString("N") + ".osu");
        try
        {
            OsuManiaSerializer.Save(document, file);
            var imported = OsuManiaSerializer.Load(file);
            Require(OsuTimingPointSettings.Read(imported.Timing.First()).Volume == 0, "new osu serialization ignored muted timing samples");
            imported.Notes.Add(new ChartNote { Lane = 3, Beat = BeatPosition.FromDouble(4) });
            Require(imported.Notes[0].Volume == 100, "editor preview note gain was muted");
            OsuSourceEnvelope.TrySerialize(imported, null, null, out var text);
            Require(text.Contains("0:0:0:0:"), "new note did not inherit timing sample volume");
        }
        finally { File.Delete(file); }
    }

    private static void CheckRoundTrip(ChartDocument document, double expected)
    {
        Require(OsuSourceEnvelope.TrySerialize(document, null, null, out var text), "missing native export");
        var imported = OsuManiaSerializer.Parse(text);
        Require(Math.Abs(-TimingMap.FindSongOffsetTiming(imported.Timing)!.Delay - expected) < .001,
            $"export/reimport lost offset {expected}");
        Require(text.Contains("36,192,1100,") && text.Contains("256,192,1500,128,0,1800:"), "offset edit changed absolute note times");
        var recovery = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(document));
        Require(Math.Abs(-TimingMap.FindSongOffsetTiming(recovery.Timing)!.Delay - expected) < .001, "recovery cache lost offset");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

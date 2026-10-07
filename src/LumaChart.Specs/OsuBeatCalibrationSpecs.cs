using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

internal static class OsuBeatCalibrationSpecs
{
    public static void Run()
    {
        var straightMap = new TimingMap(
            [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]);

        var twelfthMilliseconds = (2d + 5d / 12) * 500 + 1.4;
        var rawTwelfth = straightMap.MillisecondsToBeat(twelfthMilliseconds);
        var calibratedTwelfth = OsuBeatCalibrator.Calibrate(
            twelfthMilliseconds, rawTwelfth, straightMap);
        Assert(calibratedTwelfth is
                   { Calibrated: true, Division: 12, Beat.Bar: 2, Beat.Numerator: 5, Beat.Denominator: 12 } &&
               calibratedTwelfth.ErrorMilliseconds <= 1.41,
            "a near-grid osu timestamp did not calibrate to an exact common twelfth");

        var intentionallyOffsetMilliseconds = (3d + 1d / 4) * 500 + 2.25;
        var intentionallyOffsetBeat = straightMap.MillisecondsToBeat(intentionallyOffsetMilliseconds);
        var preservedOffset = OsuBeatCalibrator.Calibrate(
            intentionallyOffsetMilliseconds, intentionallyOffsetBeat, straightMap);
        Assert(!preservedOffset.Calibrated && preservedOffset.Beat == intentionallyOffsetBeat,
            "an osu timestamp outside the conservative 2 ms window was force-snapped");

        var unusual = BeatPosition.Preserve(0, 340, 679);
        var unusualMilliseconds = straightMap.BeatToMilliseconds(unusual);
        var repairedUnusual = OsuBeatCalibrator.Calibrate(
            unusualMilliseconds, unusual, straightMap);
        Assert(repairedUnusual is
                   { Calibrated: true, Beat.Bar: 0, Beat.Numerator: 1, Beat.Denominator: 2 } &&
               !OsuBeatCalibrator.CommonDivisions.Contains(678) &&
               !OsuBeatCalibrator.CommonDivisions.Contains(679),
            "an abnormal imported denominator was not replaced by a nearby common subdivision");

        var changingMap = new TimingMap(
        [
            new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 },
            new TimingEvent { Beat = new BeatPosition(4, 0, 1), Bpm = 240 }
        ]);
        var afterChangeMilliseconds = 2_000 + 5d / 12 * 250 + 1;
        var afterChangeRaw = changingMap.MillisecondsToBeat(afterChangeMilliseconds);
        var afterChange = OsuBeatCalibrator.Calibrate(
            afterChangeMilliseconds, afterChangeRaw, changingMap);
        Assert(afterChange is
            { Calibrated: true, Division: 12, Beat.Bar: 4, Beat.Numerator: 5, Beat.Denominator: 12 },
            "calibration ignored the active BPM segment");

        const string osu = """
            osu file format v14
            [General]
            AudioFilename: calibration.ogg
            Mode: 3
            [Metadata]
            Title:Calibration
            Artist:Artist
            Creator:Mapper
            Version:Test
            [Difficulty]
            CircleSize:4
            [TimingPoints]
            0,500,4,2,1,70,1,0
            375.8,-100,4,2,1,70,0,0
            375.8,-50,4,2,1,70,0,0
            [HitObjects]
            64,192,209.2,1,0,0:0:0:100:
            192,192,375.8,128,0,709.3:0:0:0:100:
            320,192,129,1,0,0:0:0:100:
            448,192,124.4,128,0,125.6:0:0:0:100:
            """;

        var rawDocument = ChartDocumentImporter.ImportText(
            osu, "calibration.osu", "Fallback",
            OsuNoteTimingImportMode.PreserveMilliseconds);
        var calibratedDocument = ChartDocumentImporter.ImportText(
            osu, "calibration.osu", "Fallback",
            OsuNoteTimingImportMode.CalibrateCommonSubdivisions);
        var first = calibratedDocument.Notes.Single(note => note.Lane == 0);
        var longNote = calibratedDocument.Notes.Single(note => note.Lane == 1);
        var intentional = calibratedDocument.Notes.Single(note => note.Lane == 2);
        var shortLongNote = calibratedDocument.Notes.Single(note => note.Lane == 3);
        Assert(first.Beat is { Bar: 0, Numerator: 5, Denominator: 12 },
            "parser calibration did not retain the selected common denominator");
        Assert(longNote.Beat is { Bar: 0, Numerator: 3, Denominator: 4 } &&
               longNote.EndBeat is { } longEnd &&
               longEnd is { Bar: 1, Numerator: 5, Denominator: 12 },
            "parser calibration did not independently calibrate the LN head and tail");
        Assert(Math.Abs(intentional.Beat.Value - .258) < .001 &&
               intentional.Beat.CompareTo(new BeatPosition(0, 1, 4)) != 0,
            "parser calibration erased a deliberate >2 ms note offset");
        Assert(shortLongNote.IsHold &&
               shortLongNote.Beat.CompareTo(shortLongNote.EndBeat!.Value) < 0,
            "calibration collapsed a short LN whose endpoints shared one grid candidate");
        var calibratedScroll = calibratedDocument.Timing.Single(timing =>
            timing.Effect == TimingEffectKind.Scroll);
        Assert(calibratedScroll.Beat is { Bar: 0, Numerator: 3, Denominator: 4 } &&
               Math.Abs(calibratedScroll.EffectValue - 2) < .001,
            "parser calibration did not collapse or calibrate the final inherited Scroll Effect timing point");

        var rawMap = new TimingMap(rawDocument.Timing);
        var rawFirst = rawDocument.Notes.Single(note => note.Lane == 0);
        Assert(Math.Abs(rawMap.BeatToMilliseconds(rawFirst.Beat) - 209.2) < .3 &&
               rawFirst.Beat.CompareTo(first.Beat) != 0,
            "the preserve-milliseconds import mode unexpectedly calibrated a Note");
        var rawScroll = rawDocument.Timing.Single(timing =>
            timing.Effect == TimingEffectKind.Scroll);
        Assert(rawScroll.Beat.CompareTo(calibratedScroll.Beat) != 0 &&
               Math.Abs(rawMap.BeatToMilliseconds(rawScroll.Beat) - 375.8) < .3,
            "the preserve-milliseconds import mode unexpectedly calibrated a Scroll Effect");

        var cachedRoundTrip = MalodyMcSerializer.Parse(
            MalodyMcSerializer.Serialize(calibratedDocument));
        var cachedFirst = cachedRoundTrip.Notes.Single(note => note.Lane == 0);
        var cachedLongEnd = cachedRoundTrip.Notes.Single(note => note.Lane == 1).EndBeat;
        Assert(cachedFirst.Beat is { Numerator: 5, Denominator: 12 } &&
               cachedLongEnd is { } roundTripEnd &&
               roundTripEnd is { Bar: 1, Numerator: 5, Denominator: 12 },
            "the calibrated subdivision was reduced while entering the MC working cache");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

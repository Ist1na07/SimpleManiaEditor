using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using LumaChart.Application.Archives;
using LumaChart.Application.Audio;
using LumaChart.Application.Editing;
using LumaChart.Application.Exporting;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

/// <summary>
/// Regression coverage for the core model / format / editing fixes: tempo Delay
/// sign, osu! lead-in objects, serial ABA, same-beat BPM selection, BPM range,
/// osu! SV reset, invariant formatting, resource containment, BOM siblings,
/// Malody round-trip fields, BGM offset, exact paste, hit samples, tolerant
/// Malody parsing, archive totals, red-line rounding, stable ordering and
/// CircleSize parsing.
/// </summary>
internal static class CoreFixSpecs
{
    public static void Run()
    {
        TapTempoDelayPutsBeatsOnTheTaps();
        DetectedTempoDelayPutsBeatsOnTheClicks();
        VariableTempoDelayPutsBeatsOnTheClicks();
        OsuObjectsBeforeFirstRedLineSurviveRoundTrip();
        ReplaceDocumentNeverReusesSerials();
        SameBeatBpmUsesOneRecordEverywhere();
        FirstBpmAfterBeatZeroKeepsOffsetSemantics();
        VeryFastBpmIsKeptAndClamped();
        RateExportRefusesBpmOverflow().GetAwaiter().GetResult();
        OsuRedLinesResetAndCarryScroll();
        OsuExportIsCultureInvariant();
        ResourceReferencesStayInsideTheChartFolder();
        FullPackageContainsOnlyReferencedFiles().GetAwaiter().GetResult();
        MalodyEffectEndBeatAndBarBeginRoundTrip();
        MalodyBackgroundOffsetFollowsDelay().GetAwaiter().GetResult();
        PasteKeepsExactRelativePositions();
        OsuHitSamplesRoundTrip();
        MalodyBadRecordsDoNotAbortTheChart();
        ArchiveRejectsHighOverallCompression().GetAwaiter().GetResult();
        OsuRedLineRoundingDoesNotAccumulate();
        SameBeatOrderingIsStable();
        OsuCircleSizeAcceptsIntegralDecimals();
    }

    private static void TapTempoDelayPutsBeatsOnTheTaps()
    {
        // First beat heard 125 ms into the audio, 120 BPM.
        var taps = Enumerable.Range(0, 16).Select(k => 125d + 500d * k).ToArray();
        var result = ManualTempoTapAnalyzer.Analyze(taps);
        Assert(Math.Abs(result.OffsetMilliseconds + 125) < .01,
            $"tap analysis did not report the chart Delay (-125): {result.OffsetMilliseconds}");
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument());
        var applied = TempoDetectionEditingService.ApplySingle(session, result, session.ChangeSerial);
        Assert(applied.Status == TempoDetectionApplyStatus.Applied, $"tap result was not applied: {applied.Message}");
        AssertBeatsOnGrid(session.Document, taps, "manual tap");
    }

    private static void DetectedTempoDelayPutsBeatsOnTheClicks()
    {
        // A click every 500 ms starting at 125 ms, at the 200 Hz analysis rate.
        const int rate = BeatAnalysisEngine.DefaultAnalysisRate;
        var clicks = Enumerable.Range(0, 60).Select(k => 125d + 500d * k).ToArray();
        var novelty = new double[rate * 32];
        foreach (var click in clicks) novelty[(int)Math.Round(click * rate / 1000)] = 1;
        var result = BeatAnalysisEngine.Analyze(new BeatAnalysisSignal(novelty, rate, novelty.Length / (double)rate));
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument());
        var applied = TempoDetectionEditingService.ApplySingle(session, result, session.ChangeSerial);
        Assert(applied.Status == TempoDetectionApplyStatus.Applied, $"detected result was not applied: {applied.Message}");
        AssertBeatsOnGrid(session.Document, clicks.Take(24).ToArray(), "single BPM detection");

        // Through the real audio front end the envelope smoothing adds about one
        // 5 ms analysis frame of latency, but the beat grid must no longer be
        // mirrored around zero (which put it about 255 ms off here).
        var reducer = new ExactRateEnergyReducer(8_000, rate);
        var energies = new List<double>();
        for (var sample = 0; sample < 8_000 * 32; sample++)
        {
            var milliseconds = sample / 8d;
            var sinceClick = (milliseconds - 125) % 500;
            var value = milliseconds >= 125 && sinceClick < 15 ? Math.Sin(sample * .9) : 0;
            if (reducer.AddSample(value, out var energy)) energies.Add(energy);
        }
        var fromAudio = BeatAnalysisEngine.Analyze(BeatAnalysisEngine.CreateSignal(energies, rate));
        var map = new TimingMap([new TimingEvent { Bpm = fromAudio.Bpm, Delay = fromAudio.OffsetMilliseconds }]);
        for (var beat = 0; beat < 8; beat++)
        {
            var error = map.BeatToMilliseconds(new BeatPosition(beat, 0, 1)) - clicks[beat];
            Assert(Math.Abs(error) < 10,
                $"audio click beat {beat} was {error:0.###} ms away from the click (offset {fromAudio.OffsetMilliseconds})");
        }
    }

    private static void VariableTempoDelayPutsBeatsOnTheClicks()
    {
        const int rate = 200;
        var clicks = Enumerable.Range(0, 120).Select(k => 125d + 500d * k).ToArray();
        var novelty = new double[rate * 64];
        foreach (var click in clicks.Where(click => click < 64_000))
            novelty[(int)Math.Round(click * rate / 1000)] = 1;
        var estimate = VariableTempoEstimator.Estimate(novelty, rate);
        Assert(Math.Abs(estimate.InitialOffsetMilliseconds + 125) < .01,
            $"variable tempo estimate did not report the chart Delay (-125): {estimate.InitialOffsetMilliseconds}");
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument());
        var plan = TempoDetectionEditingService.CreateTimelinePlan(session.Document, estimate);
        var applied = TempoDetectionEditingService.ApplyTimeline(
            session, plan, estimate.InitialOffsetMilliseconds, false, session.ChangeSerial);
        Assert(applied.Status == TempoDetectionApplyStatus.Applied, $"timeline was not applied: {applied.Message}");
        AssertBeatsOnGrid(session.Document, clicks.Take(24).ToArray(), "variable BPM detection");
    }

    private static void AssertBeatsOnGrid(ChartDocument document, IReadOnlyList<double> beatTimes, string source)
    {
        var map = new TimingMap(document.Timing);
        for (var beat = 0; beat < beatTimes.Count; beat++)
        {
            var error = map.BeatToMilliseconds(new BeatPosition(beat, 0, 1)) - beatTimes[beat];
            Assert(Math.Abs(error) <= 1,
                $"{source}: beat {beat} is {error:0.###} ms away from the real beat");
        }
    }

    private static void OsuObjectsBeforeFirstRedLineSurviveRoundTrip()
    {
        const string osu = """
        osu file format v14
        [General]
        AudioFilename: song.ogg
        Mode: 3
        [Metadata]
        Title:Lead In
        Artist:Artist
        Creator:Mapper
        Version:Test
        [Difficulty]
        CircleSize:4
        [TimingPoints]
        1000,500,4,2,1,70,1,0
        [HitObjects]
        64,192,-300,1,0,0:0:0:100:
        192,192,250,1,0,0:0:0:100:
        320,192,1000,1,0,0:0:0:100:
        448,192,1250,128,0,1750:0:0:0:100:
        """;
        var withScroll = osu.Replace("1000,500,4,2,1,70,1,0",
            "1000,500,4,2,1,70,1,0\n-100,-50,4,2,1,70,0,0", StringComparison.Ordinal);
        var chart = OsuManiaSerializer.Parse(withScroll);
        double[] sourceTimes = [-300, 250, 1000, 1250];
        Assert(chart.Notes.Count == 4, $"objects before the first red line were deleted: {chart.Notes.Count} notes left");
        Assert(chart.Timing.Any(timing => timing.Effect == TimingEffectKind.Scroll && timing.EffectValue == 2),
            "an SV point before the first red line was deleted");
        var map = new TimingMap(chart.Timing);
        var times = chart.Notes.OrderBy(note => note.Beat).Select(note => map.BeatToMilliseconds(note.Beat)).ToArray();
        Assert(times.Zip(sourceTimes).All(pair => Math.Abs(pair.First - pair.Second) < .01),
            "lead-in objects changed time: " + string.Join(", ", times));
        var zeroBeat = TimingMap.FindSongOffsetTiming(chart.Timing)!;
        Assert(zeroBeat.Beat == BeatPosition.Zero && Math.Abs(map.BeatToMilliseconds(new BeatPosition(4, 0, 1)) - 1000) < .01,
            "beat zero was not moved back by a whole bar of the first BPM");

        var path = Path.Combine(Path.GetTempPath(), $"lumachart-leadin-{Guid.NewGuid():N}.osu");
        try
        {
            OsuManiaSerializer.Save(chart, path);
            var text = File.ReadAllText(path);
            Assert(text.Contains("\n1000,500,4,2,1,70,1,0", StringComparison.Ordinal),
                "the source red line did not keep its own timestamp");
            foreach (var start in new[] { "-300", "250", "1000", "1250" })
                Assert(text.Contains($",192,{start},", StringComparison.Ordinal), $"object at {start} ms did not round-trip");
            var reopened = OsuManiaSerializer.Load(path);
            var reopenedMap = new TimingMap(reopened.Timing);
            var reopenedTimes = reopened.Notes.OrderBy(note => note.Beat)
                .Select(note => reopenedMap.BeatToMilliseconds(note.Beat)).ToArray();
            Assert(reopened.Notes.Count == 4 &&
                   reopenedTimes.Zip(sourceTimes).All(pair => Math.Abs(pair.First - pair.Second) <= .51),
                "lead-in objects drifted after save/reopen");
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        // Whole-millisecond objects on a fractional first red line read back up
        // to 0.5 ms early. They sit on the red line: no lead-in bar, no deletion.
        var fractional = OsuManiaSerializer.Parse(osu
            .Replace("1000,500,4,2,1,70,1,0", "-321.5,500,4,2,1,70,1,0", StringComparison.Ordinal)
            .Replace("64,192,-300,", "64,192,-322,", StringComparison.Ordinal));
        var fractionalOffset = TimingMap.FindSongOffsetTiming(fractional.Timing)!;
        Assert(fractional.Notes.Count == 4 && fractional.Timing.Count(timing => timing.Bpm.HasValue) == 1 &&
               fractionalOffset.Delay == 321.5 &&
               fractional.Notes.Min(note => note.Beat).CompareTo(BeatPosition.Zero) == 0,
            "an object rounded onto a fractional first red line was treated as a lead-in or deleted");
    }

    private static void ReplaceDocumentNeverReusesSerials()
    {
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument());
        Assert(session.ChangeSerial == 0 && !session.IsDirty, "the initial load changed the fresh-session numbering");
        session.Mutate(document => document.Notes.Add(new ChartNote { Lane = 0, Beat = new BeatPosition(1, 0, 1) }));
        var editedSerial = session.ChangeSerial;
        Assert(editedSerial == 1 && session.IsDirty, "a mutation did not advance the serial");

        session.ReplaceDocument(new ChartDocument { Metadata = new ChartMetadata { Title = "Other" } });
        Assert(session.ChangeSerial > editedSerial && !session.IsDirty,
            $"reload reused serial {session.ChangeSerial} (ABA) or stayed dirty");
        Assert(!session.TryMarkSaved(editedSerial),
            "a save that started before the reload marked the replaced document clean");

        var beforeRestore = session.ChangeSerial;
        session.ReplaceDocument(new ChartDocument(), markDirty: true);
        Assert(session.ChangeSerial > beforeRestore && session.IsDirty,
            "an unsaved replacement was clean or reused a serial");
        session.Mutate(document => document.KeyCount = 5);
        session.Undo();
        Assert(session.IsDirty, "undoing back to the restored content made an unsaved restore look saved");
        Assert(session.TryMarkSaved(session.ChangeSerial) && !session.IsDirty,
            "saving the current serial did not clear dirty state");
    }

    private static void SameBeatBpmUsesOneRecordEverywhere()
    {
        var document = new ChartDocument
        {
            Timing =
            [
                new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120, Delay = 300 },
                new TimingEvent { Beat = BeatPosition.Zero, Bpm = 150 },
                new TimingEvent { Beat = new BeatPosition(4, 0, 1), Bpm = 180, Effect = TimingEffectKind.Scroll, EffectValue = 2 },
                new TimingEvent { Beat = new BeatPosition(4, 0, 1), Bpm = 200, Delay = 0 }
            ]
        };
        var before = new TimingMap(document.Timing);
        document.Normalize();
        var bpm = document.Timing.Where(timing => timing.Bpm.HasValue).ToArray();
        Assert(bpm.Length == 2 && bpm[0].Bpm == 150 && bpm[0].Delay == 300 && bpm[1].Bpm == 200,
            "same-beat BPM records were not merged into the TimingMap winner with the inherited offset");
        Assert(document.Timing.Any(timing => timing.Effect == TimingEffectKind.Scroll && !timing.Bpm.HasValue),
            "merging removed the Effect of an overridden BPM row");
        var after = new TimingMap(document.Timing);
        foreach (var beat in new[] { 0d, 2, 4, 7.5 })
            Assert(Math.Abs(after.BeatToMilliseconds(BeatPosition.FromDouble(beat)) -
                            before.BeatToMilliseconds(BeatPosition.FromDouble(beat))) < 1e-9,
                "merging same-beat BPM changed the played timing");

        var session = new EditorSession();
        session.ReplaceDocument(document);
        Assert(ChartMetadataEditingService.Capture(session.Document).Offset == "300",
            "the Offset field did not show the record TimingMap plays");
        var draft = ChartMetadataEditingService.Capture(session.Document) with { Offset = "-40" };
        Assert(ChartMetadataEditingService.TryParseDraft(draft, out var edit, out var error), error);
        ChartMetadataEditingService.Apply(session, edit!);
        Assert(Math.Abs(new TimingMap(session.Document.Timing).BeatToMilliseconds(BeatPosition.Zero) - 40) < 1e-9,
            "an Offset edit did not move beat zero");

        var tempo = TempoDetectionEditingService.ApplySingle(
            session, new BeatAnalysisResult(128, -75, 0, 1, 16, 8), session.ChangeSerial);
        var played = new TimingMap(session.Document.Timing);
        Assert(tempo.Status == TempoDetectionApplyStatus.Applied &&
               played.BpmAt(BeatPosition.Zero) == 128 &&
               Math.Abs(played.BeatToMilliseconds(BeatPosition.Zero) - 75) < 1e-9,
            "tempo detection edited a different record than the one TimingMap plays");

        var edited = new EditorSession();
        edited.ReplaceDocument(new ChartDocument
        {
            Timing =
            [
                new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 },
                new TimingEvent { Beat = new BeatPosition(8, 0, 1), Bpm = 140 }
            ]
        });
        var movedRow = edited.Document.Timing.Single(timing => timing.Bpm == 120);
        Assert(CompleteTimingEditingService.Update(edited, movedRow.Id,
                   new CompleteTimingEdit(new BeatPosition(8, 0, 1), 175, 0, null)) &&
               new TimingMap(edited.Document.Timing).BpmAt(new BeatPosition(8, 0, 1)) == 175,
            "a BPM row edited onto another BPM beat lost to the older row");
    }

    private static void FirstBpmAfterBeatZeroKeepsOffsetSemantics()
    {
        // TimingMap plays beat 0 with no offset and treats this Delay as a pause
        // before beat 4. The Offset field used to show and edit that pause.
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            Timing = [new TimingEvent { Beat = new BeatPosition(4, 0, 1), Bpm = 120, Delay = 250 }],
            Notes = [new ChartNote { Lane = 0, Beat = new BeatPosition(6, 0, 1) }]
        });
        var before = new TimingMap(session.Document.Timing);
        var draft = ChartMetadataEditingService.Capture(session.Document);
        Assert(draft.Offset == "0", $"the Offset field showed a section pause as the song offset: {draft.Offset}");
        Assert(ChartMetadataEditingService.TryParseDraft(draft with { Offset = "100" }, out var edit, out var error), error);
        ChartMetadataEditingService.Apply(session, edit!);
        var after = new TimingMap(session.Document.Timing);
        foreach (var beat in new[] { 0, 4, 6 })
            Assert(Math.Abs(after.BeatToMilliseconds(new BeatPosition(beat, 0, 1)) -
                            (before.BeatToMilliseconds(new BeatPosition(beat, 0, 1)) - 100)) < 1e-9,
                $"an Offset edit did not shift beat {beat} by the song offset");
        Assert(session.Document.Timing.Single(timing => timing.Beat == new BeatPosition(4, 0, 1)).Delay == 250 &&
               ChartMetadataEditingService.Capture(session.Document).Offset == "100",
            "the Offset edit overwrote the section pause instead of creating the beat-0 offset record");

        var malody = MalodyMcSerializer.Parse("""
        {"meta":{"mode":0,"mode_ext":{"column":4},"song":{"file":"song.ogg"}},
         "time":[{"beat":[4,0,1],"bpm":120}],
         "note":[{"beat":[0,0,1],"sound":"song.ogg","offset":145,"type":1},{"beat":[6,0,1],"column":0}]}
        """);
        Assert(Math.Abs(new TimingMap(malody.Timing).BeatToMilliseconds(new BeatPosition(6, 0, 1)) - (3000 - 145)) < 1e-9,
            "a Malody BGM offset became a section pause when the first BPM starts after beat 0");
    }

    private static void VeryFastBpmIsKeptAndClamped()
    {
        const string json = """
        {"meta":{"mode":0,"mode_ext":{"column":4},"song":{}},
         "time":[{"beat":[0,0,1],"bpm":120},{"beat":[4,0,1],"bpm":50000},{"beat":[5,0,1],"bpm":120},
                 {"beat":[9,0,1],"bpm":5000000},{"beat":[10,0,1],"bpm":120}],
         "note":[{"beat":[8,0,1],"column":0},{"beat":[12,0,1],"column":1}]}
        """;
        var chart = MalodyMcSerializer.Parse(json);
        Assert(chart.Timing.Count(timing => timing.Bpm.HasValue) == 5,
            "a BPM above 10000 was deleted instead of kept");
        Assert(chart.Timing.Any(timing => timing.Bpm == 50_000) &&
               chart.Timing.Any(timing => timing.Bpm == ChartDocument.MaximumTimingBpm),
            "fast BPM segments were not kept/clamped");
        var map = new TimingMap(chart.Timing);
        Assert(Math.Abs(map.BeatToMilliseconds(new BeatPosition(8, 0, 1)) - (2000 + 60_000d / 50_000 + 1500)) < 1e-6,
            "timing after a fast segment drifted");
        // Far past the last segment the mapped beat exceeds the int bar range;
        // it must clamp instead of throwing during playback.
        _ = map.MillisecondsToBeat(1e14);
    }

    private static async Task RateExportRefusesBpmOverflow()
    {
        var chart = new ChartDocument
        {
            FilePath = Path.Combine(Path.GetTempPath(), "never-read.mc"),
            Metadata = new ChartMetadata { AudioFile = "song.ogg" },
            Timing = [new TimingEvent { Bpm = 120 }, new TimingEvent { Beat = new BeatPosition(4, 0, 1), Bpm = 800_000 }]
        };
        var rejected = false;
        try
        {
            await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    chart,
                    Path.Combine(Path.GetTempPath(), $"lumachart-never-{Guid.NewGuid():N}.mcz"),
                    new ChartExportOptions(ChartExportFormat.Malody, ChartExportMode.SingleRatePackage, true, 1.5, 1, 1, .1),
                    Path.Combine(Path.GetTempPath(), $"lumachart-scratch-{Guid.NewGuid():N}")),
                new CopyTranscoder());
        }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "a rate package whose BPM exceeds the supported maximum was exported");
    }

    private static void OsuRedLinesResetAndCarryScroll()
    {
        const string osu = """
        osu file format v14
        [General]
        Mode: 3
        [Difficulty]
        CircleSize:4
        [TimingPoints]
        0,500,4,2,1,70,1,0
        1000,-50,4,2,1,70,0,0
        2000,400,4,2,1,70,1,0
        [HitObjects]
        64,192,2600,1,0,0:0:0:100:
        """;
        var imported = OsuManiaSerializer.Parse(osu);
        var scroll = new ScrollSpeedMap(imported.Timing);
        Assert(scroll.MultiplierAt(BeatPosition.FromDouble(3)) == 2 &&
               scroll.MultiplierAt(BeatPosition.FromDouble(4.5)) == 1,
            "an osu! red line did not reset the imported scroll speed to 1.0");

        var malodyStyle = new ChartDocument
        {
            Timing =
            [
                new TimingEvent { Bpm = 120 },
                new TimingEvent { Beat = new BeatPosition(2, 0, 1), Effect = TimingEffectKind.Scroll, EffectValue = 2 },
                new TimingEvent { Beat = new BeatPosition(4, 0, 1), Bpm = 150 }
            ],
            Notes = [new ChartNote { Lane = 0, Beat = new BeatPosition(6, 0, 1) }]
        };
        var path = Path.Combine(Path.GetTempPath(), $"lumachart-sv-{Guid.NewGuid():N}.osu");
        try
        {
            OsuManiaSerializer.Save(malodyStyle, path);
            var text = File.ReadAllText(path);
            Assert(text.Contains("\n2000,-50,4,2,1,70,0,0", StringComparison.Ordinal),
                "the active Scroll was not re-stated on a green line at the red line");
            var reopened = OsuManiaSerializer.Load(path);
            Assert(new ScrollSpeedMap(reopened.Timing).MultiplierAt(new BeatPosition(5, 0, 1)) == 2,
                "Scroll carried across a BPM change was lost in the osu! round-trip");

            OsuManiaSerializer.Save(imported, path);
            var importedRoundTrip = OsuManiaSerializer.Load(path);
            var importedScroll = new ScrollSpeedMap(importedRoundTrip.Timing);
            Assert(importedScroll.MultiplierAt(BeatPosition.FromDouble(3)) == 2 &&
                   importedScroll.MultiplierAt(BeatPosition.FromDouble(4.5)) == 1,
                "an osu! scroll reset did not survive save/reopen");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void OsuExportIsCultureInvariant()
    {
        var chart = new ChartDocument
        {
            Timing = [new TimingEvent { Bpm = 120, Delay = 125 }],
            Notes =
            [
                new ChartNote { Lane = 0, Beat = BeatPosition.Zero },
                new ChartNote { Lane = 1, Beat = new BeatPosition(1, 0, 1), EndBeat = new BeatPosition(2, 0, 1) }
            ]
        };
        var original = CultureInfo.CurrentCulture;
        foreach (var culture in new[] { "sv-SE", "ar-SA", "fi-FI" })
        {
            var path = Path.Combine(Path.GetTempPath(), $"lumachart-culture-{Guid.NewGuid():N}.osu");
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                OsuManiaSerializer.Save(chart.Clone(), path);
                var text = File.ReadAllText(path);
                Assert(!text.Contains('\u2212') && !text.Contains('\u061C') && text.Contains(",192,-125,1,0,"),
                    $"osu! export used {culture} number formatting");
                var reopened = OsuManiaSerializer.Load(path);
                var map = new TimingMap(reopened.Timing);
                Assert(reopened.Notes.Count == 2 &&
                       Math.Abs(map.BeatToMilliseconds(reopened.Notes[0].Beat) + 125) < .51,
                    $"a negative-time note did not survive export under {culture}");
                Assert(new BeatPosition(0, -1, 4).ToString() == "-1:3/4",
                    $"BeatPosition text used {culture} formatting");
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static void ResourceReferencesStayInsideTheChartFolder()
    {
        foreach (var unsafeReference in new[]
                 {
                     "../secret.ogg", "sub/../../secret.ogg", "C:/Windows/win.ini", "C:secret.ogg",
                     "/etc/passwd", "\\\\server\\share\\a.ogg", "//server/share/a.ogg", "a\u0001.ogg", "file.ogg:stream"
                 })
            Assert(!ChartResourceReference.TryNormalize(unsafeReference, out _),
                $"an unsafe resource reference was accepted: {unsafeReference}");
        Assert(ChartResourceReference.TryNormalize(" sounds\\kick.wav ", out var normalized) && normalized == "sounds/kick.wav" &&
               ChartResourceReference.TryNormalize("./song.ogg", out var dotted) && dotted == "song.ogg",
            "a safe relative reference was rejected or not normalized");

        var chart = MalodyMcSerializer.Parse("""
        {"meta":{"mode":0,"cover":"C:/Users/me/private.png","background":"../bg.jpg",
                 "mode_ext":{"column":4},"song":{"file":"\\\\server\\share\\song.ogg"}},
         "time":[{"beat":[0,0,1],"bpm":120}],
         "note":[{"beat":[1,0,1],"column":0,"sound":"sounds\\kick.wav"},
                 {"beat":[2,0,1],"column":1,"sound":"../../secret.wav"}]}
        """);
        Assert(chart.Metadata.CoverFile == "" && chart.Metadata.BackgroundFile == "" && chart.Metadata.AudioFile == "",
            "unsafe Malody media references were imported");
        Assert(chart.Notes.Single(note => note.Lane == 0).Sound == "sounds/kick.wav" &&
               chart.Notes.Single(note => note.Lane == 1).Sound == null,
            "Malody key sound references were not normalized/cleared");
        var osu = OsuManiaSerializer.Parse("""
        osu file format v14
        [General]
        AudioFilename: ..\..\song.mp3
        Mode: 3
        [Events]
        0,0,"C:\Users\me\bg.png",0,0
        [Difficulty]
        CircleSize:4
        [TimingPoints]
        0,500,4,2,1,70,1,0
        [HitObjects]
        64,192,500,1,0,0:0:0:100:../x.wav
        """);
        Assert(osu.Metadata.AudioFile == "" && osu.Metadata.BackgroundFile == "" && osu.Notes.Single().Sound == null,
            "unsafe osu! references were imported");
    }

    private static async Task FullPackageContainsOnlyReferencedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumachart-package-{Guid.NewGuid():N}");
        var song = Path.Combine(root, "song");
        var outside = Path.Combine(root, "outside-private.png");
        Directory.CreateDirectory(Path.Combine(song, "sounds"));
        Directory.CreateDirectory(Path.Combine(song, "unrelated"));
        try
        {
            File.WriteAllBytes(Path.Combine(song, "song.ogg"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(song, "sounds", "kick.wav"), [4]);
            File.WriteAllBytes(Path.Combine(song, "sibling-bg.jpg"), [5]);
            File.WriteAllText(Path.Combine(song, "diary.txt"), "private");
            File.WriteAllBytes(Path.Combine(song, "unrelated", "photo.png"), [6]);
            File.WriteAllBytes(outside, [7]);
            var sibling = """{"meta":{"mode":0,"background":"sibling-bg.jpg","mode_ext":{"column":4},"song":{"file":"song.ogg"}},"time":[{"beat":[0,0,1],"bpm":120}],"note":[]}""";
            File.WriteAllBytes(Path.Combine(song, "sibling.mc"),
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(sibling)]);
            File.WriteAllText(Path.Combine(song, "broken.mc"), "{ not json");

            var main = new ChartDocument
            {
                Metadata = new ChartMetadata { AudioFile = "song.ogg", CoverFile = outside },
                Notes = [new ChartNote { Lane = 0, Beat = new BeatPosition(1, 0, 1), Sound = "sounds/kick.wav" }]
            };
            var mainPath = Path.Combine(song, "main.mc");
            MalodyMcSerializer.Save(main, mainPath);
            var destination = Path.Combine(root, "out.mcz");
            var result = await MalodyFullPackageExportService.ExportAsync(new MalodyFullPackageExportRequest(
                main, mainPath, destination, Path.Combine(root, "scratch")));

            using var archive = ZipFile.OpenRead(result.DestinationArchivePath);
            var entries = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            // A normal song folder is packaged whole: osu! hitsounds, storyboards
            // and videos are used without being named by the chart.
            string[] expected =
            [
                "broken.mc", "diary.txt", "main.mc", "sibling-bg.jpg", "sibling.mc", "song.ogg",
                "sounds/kick.wav", "unrelated/photo.png"
            ];
            Assert(entries.SequenceEqual(expected),
                "full package did not contain the song folder (and nothing outside it): " + string.Join(", ", entries));
            using (var reader = new StreamReader(archive.GetEntry("broken.mc")!.Open()))
                Assert(reader.ReadToEnd() == "{ not json", "an unreadable sibling chart was not kept verbatim");
            using (var reader = new StreamReader(archive.GetEntry("sibling.mc")!.Open()))
                Assert(JsonNode.Parse(reader.ReadToEnd())?["meta"]?["background"]?.GetValue<string>() == "sibling-bg.jpg",
                    "a BOM sibling chart was not packaged as readable JSON");
            using (var reader = new StreamReader(archive.GetEntry("main.mc")!.Open()))
                Assert(JsonNode.Parse(reader.ReadToEnd())?["meta"]?["cover"]?.GetValue<string>() == "",
                    "a cover outside the chart folder was packaged");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        await ProtectedFolderPackageContainsOnlyReferencedFiles();
    }

    private static async Task ProtectedFolderPackageContainsOnlyReferencedFiles()
    {
        // The Temp root itself is a protected folder: a chart saved directly in
        // it (like one on the Desktop or in Downloads) must not publish its
        // unrelated neighbours.
        var shared = Path.GetTempPath();
        var prefix = $"lumachart-shared-{Guid.NewGuid():N}";
        string Name(string suffix) => $"{prefix}-{suffix}";
        var scratch = Path.Combine(shared, Name("scratch"));
        string[] created =
        [
            Name("song.ogg"), Name("bg.jpg"), Name("diary.txt"), Name("sibling.mc"), Name("main.mc"), Name("out.mcz")
        ];
        try
        {
            File.WriteAllBytes(Path.Combine(shared, Name("song.ogg")), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(shared, Name("bg.jpg")), [4]);
            File.WriteAllText(Path.Combine(shared, Name("diary.txt")), "private");
            var sibling = $$$"""{"meta":{"mode":0,"background":"{{{Name("bg.jpg")}}}","mode_ext":{"column":4},"song":{"file":"{{{Name("song.ogg")}}}"}},"time":[{"beat":[0,0,1],"bpm":120}],"note":[]}""";
            File.WriteAllBytes(Path.Combine(shared, Name("sibling.mc")),
                [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(sibling)]);
            var main = new ChartDocument { Metadata = new ChartMetadata { AudioFile = Name("song.ogg") } };
            var mainPath = Path.Combine(shared, Name("main.mc"));
            MalodyMcSerializer.Save(main, mainPath);
            var result = await MalodyFullPackageExportService.ExportAsync(new MalodyFullPackageExportRequest(
                main, mainPath, Path.Combine(shared, Name("out.mcz")), scratch));

            using var archive = ZipFile.OpenRead(result.DestinationArchivePath);
            var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            Assert(entries.Contains(Name("main.mc")) && entries.Contains(Name("sibling.mc")) &&
                   entries.Contains(Name("song.ogg")) && entries.Contains(Name("bg.jpg")),
                "a protected-folder package lost a sibling chart or a referenced resource");
            Assert(!entries.Contains(Name("diary.txt")) && !entries.Any(name => name.StartsWith(Name("scratch"), StringComparison.Ordinal)),
                "a protected-folder package published unreferenced files or folders");
        }
        finally
        {
            foreach (var file in created)
                try { File.Delete(Path.Combine(shared, file)); } catch { }
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    private static void MalodyEffectEndBeatAndBarBeginRoundTrip()
    {
        const string json = """
        {"meta":{"mode":0,"mode_ext":{"column":4,"bar_begin":3},"song":{}},
         "time":[{"beat":[0,0,1],"bpm":120}],
         "effect":[{"beat":[2,0,1],"endbeat":[3,1,2],"scroll":0.5},{"beat":[4,0,1],"jump":1}],
         "note":[]}
        """;
        var chart = MalodyMcSerializer.Parse(json);
        var root = JsonNode.Parse(MalodyMcSerializer.Serialize(chart))!;
        Assert(root["meta"]?["mode_ext"]?["bar_begin"]?.GetValue<int>() == 3,
            "mode_ext.bar_begin was overwritten");
        var ranged = root["effect"]!.AsArray().Single(effect => effect?["scroll"] is not null)!;
        Assert(ranged["endbeat"]?.ToJsonString() == "[3,1,2]",
            "effect endbeat was dropped");
        Assert(root["effect"]!.AsArray().Single(effect => effect?["jump"] is not null)!["endbeat"] is null,
            "endbeat leaked onto an effect without one");
        var fresh = JsonNode.Parse(MalodyMcSerializer.Serialize(new ChartDocument()))!;
        Assert(fresh["meta"]?["mode_ext"]?["bar_begin"]?.GetValue<int>() == 0,
            "new charts lost the default bar_begin");
    }

    private static async Task MalodyBackgroundOffsetFollowsDelay()
    {
        var chart = MalodyMcSerializer.Parse("""
        {"meta":{"mode":0,"mode_ext":{"column":4},"song":{"file":"song.ogg"}},
         "time":[{"beat":[0,0,1],"bpm":160}],
         "note":[{"beat":[0,0,1],"sound":"song.ogg","vol":100,"offset":145,"type":1},{"beat":[1,0,1],"column":0}]}
        """);
        TimingMap.FindSongOffsetTiming(chart.Timing)!.Delay = 200;
        var edited = JsonNode.Parse(MalodyMcSerializer.Serialize(chart))!["note"]!.AsArray()
            .Single(note => note?["type"]?.GetValue<int>() == 1)!;
        Assert(edited["offset"]?.GetValue<int>() == 200, "a derived BGM offset did not follow an Offset edit");

        var root = Path.Combine(Path.GetTempPath(), $"lumachart-rate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "song.ogg"), [1, 2, 3]);
            var chartPath = Path.Combine(root, "chart.mc");
            MalodyMcSerializer.Save(chart, chartPath);
            var destination = Path.Combine(root, "rate.mcz");
            await ChartExportService.ExportAsync(
                new ChartExportRequest(
                    chart,
                    destination,
                    new ChartExportOptions(ChartExportFormat.Malody, ChartExportMode.SingleRatePackage, true, 2, 1, 1, .1),
                    Path.Combine(root, "scratch")),
                new CopyTranscoder());
            using var archive = ZipFile.OpenRead(destination);
            var entry = archive.Entries.Single(item => item.FullName.EndsWith(".mc", StringComparison.Ordinal));
            using var reader = new StreamReader(entry.Open());
            var rated = JsonNode.Parse(reader.ReadToEnd())!;
            var music = rated["note"]!.AsArray().Single(note => note?["type"]?.GetValue<int>() == 1)!;
            Assert(Math.Abs(rated["time"]![0]!["delay"]!.GetValue<double>() - 100) < 1e-9 &&
                   music["offset"]?.GetValue<int>() == 100,
                "a rate export kept the unscaled BGM offset");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void PasteKeepsExactRelativePositions()
    {
        var source = new EditorSession { SnapDivision = 4 };
        source.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote { Lane = 0, Beat = new BeatPosition(1, 0, 1) },
                new ChartNote { Lane = 0, Beat = new BeatPosition(1, 1, 3) },
                new ChartNote { Lane = 0, Beat = new BeatPosition(1, 2, 3), EndBeat = new BeatPosition(2, 1, 16) },
                new ChartNote { Lane = 1, Beat = new BeatPosition(1, 1, 32) },
                new ChartNote { Lane = 1, Beat = new BeatPosition(1, 1, 16) }
            ]
        });
        source.Selection.UnionWith(source.Document.Notes.Select(note => note.Id));
        var clipboard = BasicChartEditingService.CopySelection(source)!;
        var target = new EditorSession { SnapDivision = 4 };
        target.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        // At a 1/4 grid both lane-1 notes used to snap onto beat 4 and one was
        // dropped as a collision.
        Assert(BasicChartEditingService.Paste(target, clipboard, 0, BeatPosition.FromDouble(4.1)) == 5,
            "paste collapsed notes that only differ below the active grid");
        var pasted = target.Document.Notes.Select(note => note.Beat.Value).OrderBy(value => value).ToArray();
        double[] expected = [4, 4 + 1d / 32, 4 + 1d / 16, 4 + 1d / 3, 4 + 2d / 3];
        Assert(pasted.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-12),
            "paste bent copied triplets/16ths onto the active grid: " + string.Join(", ", pasted));
        Assert(target.Document.Notes.Single(note => note.IsHold).EndBeat!.Value.CompareTo(new BeatPosition(5, 1, 16)) == 0,
            "paste changed an LN tail");

        var mixedClipboard = MixedSelectionEditingService.Copy(source, source.Selection, []);
        var mixedTarget = new EditorSession { SnapDivision = 4 };
        mixedTarget.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [new ChartNote { Lane = 0, Beat = new BeatPosition(8, 1, 3) }]
        });
        var mixed = MixedSelectionEditingService.Paste(mixedTarget, mixedClipboard, new BeatPosition(8, 0, 1));
        Assert(mixed.NoteCount == 4 && mixedTarget.Document.Notes.Count == 5,
            "mixed paste stacked a note on an existing one or dropped a distinct note");
        Assert(mixedTarget.Document.Notes.Any(note => note.Lane == 1 && note.Beat.CompareTo(new BeatPosition(8, 1, 16)) == 0) &&
               mixedTarget.Document.Notes.Any(note => note.Lane == 0 && note.Beat.CompareTo(new BeatPosition(8, 2, 3)) == 0),
            "mixed paste did not keep exact relative positions");
    }

    private static void OsuHitSamplesRoundTrip()
    {
        var chart = new ChartDocument
        {
            Notes =
            [
                new ChartNote { Lane = 0, Beat = new BeatPosition(1, 0, 1), Volume = 0 },
                new ChartNote { Lane = 1, Beat = new BeatPosition(2, 0, 1), Volume = 60, Sound = "kick,snare.wav" }
            ]
        };
        var path = Path.Combine(Path.GetTempPath(), $"lumachart-sample-{Guid.NewGuid():N}.osu");
        try
        {
            OsuManiaSerializer.Save(chart, path);
            var text = File.ReadAllText(path);
            Assert(text.Contains(",1,0,0:0:0:1:\r\n", StringComparison.Ordinal) ||
                   text.Contains(",1,0,0:0:0:1:\n", StringComparison.Ordinal),
                "a muted note was written as osu! 'inherit' volume 0");
            Assert(text.Contains("0:0:0:60:kick_snare.wav", StringComparison.Ordinal),
                "a ',' in a sample file name was written into the hit object line");
            var reopened = OsuManiaSerializer.Load(path);
            Assert(reopened.Notes.Count == 2 && reopened.Notes[0].Volume == 1 &&
                   reopened.Notes[1].Sound == "kick_snare.wav" && reopened.Notes[1].Volume == 60,
                "hit samples did not round-trip");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void MalodyBadRecordsDoNotAbortTheChart()
    {
        const string json = """
        {"meta":{"mode":0,"creator":7,"mode_ext":{"column":4},"song":{"title":"Bad Records"}},
         "time":[{"beat":[0,0,1],"bpm":120},{"beat":[4,0,0],"bpm":90}],
         "effect":[{"beat":["x",0,1],"scroll":2},{"beat":[2,0,1],"scroll":0.5}],
         "note":[{"beat":[1,2.0,4],"column":0},
                 {"beat":[2,0,0],"column":1},
                 {"beat":[3,0,1],"column":2,"sound":1},
                 {"beat":[4,0,1],"endbeat":[5,"a",1],"column":3}]}
        """;
        var chart = MalodyMcSerializer.Parse(json, null, "fallback", out var skipped);
        Assert(skipped == 4, $"bad records were not counted: {skipped}");
        Assert(chart.Metadata.Creator == "fallback", "a non-text creator was not replaced by the fallback");
        Assert(chart.Notes.Count == 2 &&
               chart.Notes.Any(note => note.Beat.CompareTo(new BeatPosition(1, 1, 2)) == 0) &&
               chart.Notes.Any(note => note.Lane == 2 && note.Sound == null),
            "readable notes were lost or integral decimals were rejected");
        Assert(chart.Timing.Count(timing => timing.Bpm.HasValue) == 1 &&
               chart.Timing.Count(timing => timing.Effect == TimingEffectKind.Scroll) == 1,
            "readable timing records were lost");
        var root = JsonNode.Parse(MalodyMcSerializer.Serialize(chart))!;
        var notes = root["note"]!.AsArray();
        Assert(notes.Any(note => note?["beat"]?.ToJsonString() == "[2,0,0]") &&
               notes.Any(note => note?["endbeat"]?.ToJsonString() == "[5,\"a\",1]") &&
               notes.Any(note => note?["sound"]?.ToJsonString() == "1") &&
               root["effect"]!.AsArray().Any(effect => effect?["beat"]?.ToJsonString() == "[\"x\",0,1]"),
            "unreadable notes/effects were not kept verbatim for the next save");
    }

    private static async Task ArchiveRejectsHighOverallCompression()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lumachart-bomb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "bomb.mcz");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var zeros = new byte[512 * 1024];
                for (var index = 0; index < 64; index++)
                {
                    var entry = archive.CreateEntry($"chunk{index}.bin", CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    stream.Write(zeros);
                }
            }
            var destination = Path.Combine(root, "extracted");
            var rejected = false;
            try
            {
                await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(archivePath, destination);
            }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "many small, extremely compressed entries bypassed the compression-ratio check");
            Assert(!Directory.Exists(destination) || !Directory.EnumerateFileSystemEntries(destination).Any(),
                "a rejected archive left extracted data behind");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void OsuRedLineRoundingDoesNotAccumulate()
    {
        var builder = new StringBuilder("""
        osu file format v14
        [General]
        Mode: 3
        [Difficulty]
        CircleSize:4
        [TimingPoints]

        """);
        var redLines = new List<double>();
        var time = 0d;
        var random = new Random(17);
        for (var index = 0; index < 400; index++)
        {
            var beatLength = 60_000d / (97.3 + random.NextDouble() * 110);
            redLines.Add(time);
            builder.Append(time.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(beatLength.ToString("R", CultureInfo.InvariantCulture)).Append(",4,2,1,70,1,0\n");
            time += beatLength * (1 + random.Next(3)) + beatLength * random.NextDouble();
        }
        builder.Append("[HitObjects]\n");
        var chart = OsuManiaSerializer.Parse(builder.ToString());
        var map = new TimingMap(chart.Timing);
        var bpmPoints = chart.Timing.Where(timing => timing.Bpm.HasValue).OrderBy(timing => timing.Beat).ToArray();
        Assert(bpmPoints.Length == redLines.Count, "red lines were lost");
        var worst = bpmPoints.Zip(redLines).Max(pair => Math.Abs(map.BeatToMilliseconds(pair.First.Beat) - pair.Second));
        // One 1/1920-beat rounding at <= 210 BPM is at most ~0.15 ms; the old
        // integration let those residuals accumulate across all 400 changes.
        Assert(worst < .2, $"red-line rounding accumulated across BPM changes: {worst:0.####} ms");
    }

    private static void SameBeatOrderingIsStable()
    {
        var beat = new BeatPosition(4, 0, 1);
        var scrolls = Enumerable.Range(1, 48)
            .Select(index => new TimingEvent { Beat = beat, Effect = TimingEffectKind.Scroll, EffectValue = index / 10d })
            .ToList();
        var document = new ChartDocument { Timing = [new TimingEvent { Bpm = 120 }, .. scrolls] };
        TempoTimelineApplicator.ReplaceBpmEvents(document,
            [new TempoTimingCandidate(BeatPosition.Zero, 130, 0, 1), new TempoTimingCandidate(new BeatPosition(8, 0, 1), 140, 4, 1)],
            0);
        Assert(document.Timing.Where(timing => timing.Effect == TimingEffectKind.Scroll).Select(timing => timing.Id)
                   .SequenceEqual(scrolls.Select(timing => timing.Id)) &&
               new ScrollSpeedMap(document.Timing).MultiplierAt(beat) == 4.8,
            "same-beat Scroll records changed order during BPM replacement");

        var notes = Enumerable.Range(0, 48)
            .Select(index => new ChartNote { Lane = 0, Beat = new BeatPosition(2, 0, 1), Volume = index + 1 })
            .Prepend(new ChartNote { Lane = 0, Beat = new BeatPosition(9, 0, 1) })
            .ToList();
        var chart = new ChartDocument { Notes = notes };
        chart.Normalize();
        Assert(chart.Notes.Take(48).Select(note => note.Volume).SequenceEqual(Enumerable.Range(1, 48)),
            "normalizing reordered identical notes");
    }

    private static void OsuCircleSizeAcceptsIntegralDecimals()
    {
        const string osu = """
        osu file format v14
        [General]
        Mode: 3
        [Difficulty]
        CircleSize:7.0
        [TimingPoints]
        0,500,4,2,1,70,1,0
        [HitObjects]
        """;
        Assert(OsuManiaSerializer.Parse(osu).KeyCount == 7, "CircleSize 7.0 fell back to 4K");
        Assert(OsuManiaSerializer.Parse(osu.Replace("7.0", "7.5", StringComparison.Ordinal)).KeyCount == 4,
            "a non-integral CircleSize was accepted");
    }

    private sealed class CopyTranscoder : IChartExportAudioTranscoder
    {
        public Task ConvertToOggAsync(
            string sourceAudioPath,
            string destinationOggPath,
            double rate,
            bool preservePitch,
            CancellationToken cancellationToken = default)
        {
            File.Copy(sourceAudioPath, destinationOggPath);
            return Task.CompletedTask;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

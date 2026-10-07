using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using Avalonia.Input;
using LumaChart.Application.Playtest;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using SoundTouch.Net.NAudioSupport;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsPlaytestSpecs
{
    private const string LocalDataRootVariable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";

    public static IReadOnlyList<WindowsPlaytestSpecResult> Run(string isolationRoot)
    {
        Directory.CreateDirectory(isolationRoot);
        var results = new List<WindowsPlaytestSpecResult>();
        Check(results, "playtest-wpf-judgement-boundaries-are-exact", VerifyJudgementBoundaries);
        Check(results, "playtest-custom-judgement-windows-clamp-order-and-drive-real-grades",
            VerifyCustomJudgementWindows);
        Check(results, "playtest-dense-lane-keeps-strict-chronological-order", VerifyStrictLaneOrder);
        Check(results, "playtest-ln-early-window-on-time-and-tail-completion", VerifyLongNotes);
        Check(results, "playtest-autoplay-and-from-here-skip-policy", VerifyAutoPlayAndFromHere);
        Check(results, "playtest-ignores-drafts-and-does-not-mutate-source", VerifyDraftAndSourceIsolation);
        Check(results, "playtest-settings-clamp-default-bindings-and-conflict-swap", VerifySettingsPolicy);
        Check(results, "playtest-settings-atomic-roundtrip-and-fail-closed", () =>
            VerifySettingsPersistence(Path.Combine(isolationRoot, "settings")));
        Check(results, "playtest-surface-is-one-control-and-culls-100000-notes", VerifySurfaceCulling);
        Check(results, "playtest-idle-advance-does-not-allocate-per-frame", VerifyIdleAdvanceAllocation);
        Check(results, "playtest-space-transport-consumes-both-key-phases-before-focused-buttons",
            VerifyPlaybackToggleKeyRouting);
        Check(results, "playtest-key-sound-presets-match-five-drum-types-and-keep-approved-wood-tap",
            VerifyProceduralKeySoundPresetPalette);
        Check(results, "playtest-manual-keys-share-low-latency-music-output",
            VerifyManualKeySoundRouting);
        Check(results, "playtest-manual-key-soft-limiter-is-bounded-symmetric-and-transparent",
            VerifyManualKeySoundLimiter);
        Check(results, "playtest-shared-key-sound-keeps-music-level-continuous",
            VerifySharedKeySoundMixing);
        Check(results, "playtest-audio-clock-startup-does-not-jump-backward",
            VerifyAudioClockStartupHandoff);
        Check(results, "audio-decoder-sniffs-misnamed-malody-mp3-before-extension",
            VerifyAudioContainerSniffing);
        Check(results, "playtest-preserve-pitch-extreme-slowdown-uses-realtime-phase-vocoder",
            VerifyPitchPreservingSlowdownContinuity);
        Check(results, "playtest-audio-clock-hot-path-does-not-allocate", () =>
            VerifyAudioClockAllocation(Path.Combine(isolationRoot, "clock")));
        return results;
    }

    private static string VerifyProceduralKeySoundPresetPalette()
    {
        var presets = new[]
        {
            KeySoundPreset.Snare,
            KeySoundPreset.BrightTick,
            KeySoundPreset.HandDrum,
            KeySoundPreset.HandDrum2,
            KeySoundPreset.DeepPulse
        };
        var metrics = new Dictionary<KeySoundPreset, KeySoundPresetMetrics>();
        var fingerprints = new HashSet<ulong>();
        foreach (var preset in presets)
        {
            var first = KeySoundPresetSamples.Create(preset);
            var second = KeySoundPresetSamples.Create(preset);
            Require(first.SequenceEqual(second), $"Preset {preset} is not deterministic.");
            Require(
                first.Length > 0 && first.Length % KeySoundPresetSamples.Channels == 0,
                $"Preset {preset} did not produce complete stereo frames.");
            var metric = MeasurePreset(first);
            Require(
                metric.Peak is > .2 and <= .941 && metric.NonFiniteSamples == 0,
                $"Preset {preset} was silent, clipped or non-finite: {metric}.");
            Require(
                metric.DurationMilliseconds is >= 85 and <= 150,
                $"Preset {preset} escaped the short Key-sound duration envelope: {metric}.");
            metrics[preset] = metric;
            fingerprints.Add(Fingerprint(first));
        }

        Require(
            fingerprints.Count == presets.Length,
            "Two procedural Key-sound presets generated identical sample content.");
        var snare = metrics[KeySoundPreset.Snare];
        var tambourine = metrics[KeySoundPreset.BrightTick];
        var wood = metrics[KeySoundPreset.HandDrum];
        var clap = metrics[KeySoundPreset.HandDrum2];
        var kick = metrics[KeySoundPreset.DeepPulse];
        Require(
            SampleSha256(KeySoundPresetSamples.Create(KeySoundPreset.HandDrum)) ==
                "27BF9159534FCCF955E5EADDEB69BD2D9C983B551F2470650D748D7FB45EE2FE",
            "The user-approved wooden preset changed unexpectedly.");
        Require(
            Math.Abs(snare.DurationMilliseconds - 125) < .1 &&
            Math.Abs(tambourine.DurationMilliseconds - 130) < .1 &&
            Math.Abs(wood.DurationMilliseconds - 130) < .1 &&
            Math.Abs(clap.DurationMilliseconds - 130) < .1 &&
            Math.Abs(kick.DurationMilliseconds - 145) < .1,
            "A drum preset is no longer within the approved short Key-sound envelope.");
        Require(
            snare.ZeroCrossings > tambourine.ZeroCrossings + 250 &&
            tambourine.ZeroCrossings > clap.ZeroCrossings + 400 &&
            clap.ZeroCrossings > wood.ZeroCrossings * 5 &&
            wood.ZeroCrossings > kick.ZeroCrossings * 4 &&
            snare.MeanAbsoluteDelta > tambourine.MeanAbsoluteDelta &&
            tambourine.MeanAbsoluteDelta > clap.MeanAbsoluteDelta * 2 &&
            clap.MeanAbsoluteDelta > wood.MeanAbsoluteDelta * 1.8 &&
            wood.MeanAbsoluteDelta > kick.MeanAbsoluteDelta * 2,
            "Snare, tambourine, hand drum, clap and kick no longer retain " +
            "clearly separated transient and tonal profiles.");

        var customRejected = false;
        try { _ = KeySoundPresetSamples.Create(KeySoundPreset.Custom); }
        catch (ArgumentOutOfRangeException) { customRejected = true; }
        Require(customRejected, "The Custom entry unexpectedly synthesized or embedded a preset sample.");
        return string.Join(
            "; ",
            presets.Select(preset => $"{preset}={metrics[preset]}"));
    }

    private static string VerifyJudgementBoundaries()
    {
        var cases = new (double Offset, PlaytestHitGrade Grade)[]
        {
            (-170, PlaytestHitGrade.Miss),
            (-130, PlaytestHitGrade.Miss),
            (-129.999, PlaytestHitGrade.Good),
            (-85, PlaytestHitGrade.Great),
            (-45, PlaytestHitGrade.Best),
            (0, PlaytestHitGrade.Best),
            (45, PlaytestHitGrade.Best),
            (45.001, PlaytestHitGrade.Great),
            (85, PlaytestHitGrade.Great),
            (85.001, PlaytestHitGrade.Good),
            (129.999, PlaytestHitGrade.Good),
            (130, PlaytestHitGrade.Miss),
            (170, PlaytestHitGrade.Miss)
        };
        foreach (var item in cases)
            Require(
                PlaytestSession.Grade(item.Offset) == item.Grade,
                $"Offset {item.Offset:R} produced {PlaytestSession.Grade(item.Offset)}, expected {item.Grade}.");
        Require(
            PlaytestSession.GradeText(PlaytestHitGrade.Best) == "BEST" &&
            PlaytestSession.GradeText(PlaytestHitGrade.Great) == "GREAT" &&
            PlaytestSession.GradeText(PlaytestHitGrade.Good) == "GOOD" &&
            PlaytestSession.GradeText(PlaytestHitGrade.Miss) == "MISS",
            "Judgement HUD text differs from WPF.");
        return "BEST<=45; GREAT<=85; GOOD<130; MISS through +/-170";
    }

    private static string VerifyCustomJudgementWindows()
    {
        var clamped = new PlaytestJudgementWindows(-5, 70, 999, 50);
        Require(
            clamped.BestMilliseconds == 1 &&
            clamped.GreatMilliseconds == 70 &&
            clamped.GoodMilliseconds == 200 &&
            clamped.MissMilliseconds == 200,
            "Custom judgement windows did not clamp each value into 1-200 ms.");
        var ordered = new PlaytestJudgementWindows(80, 20, 60, 50);
        Require(
            ordered.BestMilliseconds == 80 &&
            ordered.GreatMilliseconds == 80 &&
            ordered.GoodMilliseconds == 80 &&
            ordered.MissMilliseconds == 80,
            "Custom judgement windows did not preserve Best <= Great <= Good <= Miss.");

        var custom = new PlaytestJudgementWindows(20, 40, 60, 90);
        var cases = new (double Offset, PlaytestHitGrade Grade)[]
        {
            (20, PlaytestHitGrade.Best),
            (20.001, PlaytestHitGrade.Great),
            (40, PlaytestHitGrade.Great),
            (40.001, PlaytestHitGrade.Good),
            (59.999, PlaytestHitGrade.Good),
            (60, PlaytestHitGrade.Miss)
        };
        foreach (var item in cases)
            Require(
                PlaytestSession.Grade(item.Offset, custom) == item.Grade,
                $"Custom offset {item.Offset:R} produced the wrong grade.");

        var withinMiss = new PlaytestSession(
            Document(Note(0, BeatPosition.Preserve(1, 0, 1))),
            0,
            autoPlay: false,
            custom);
        var miss = withinMiss.PressLane(0, 590).Single();
        Require(
            miss.Grade == PlaytestHitGrade.Miss &&
            Math.Abs(miss.OffsetMilliseconds - 90) < 1e-9,
            "The custom Miss outer range was not active in real lane judgement.");
        var outsideMiss = new PlaytestSession(
            Document(Note(0, BeatPosition.Preserve(1, 0, 1))),
            0,
            autoPlay: false,
            custom);
        var expired = outsideMiss.PressLane(0, 590.001).Single();
        Require(
            expired.Grade == PlaytestHitGrade.Miss &&
            !expired.PlaySound &&
            expired.Completed,
            "A Note outside the custom Miss outer range was treated as a played hit.");
        return $"clamp={clamped}; ordered={ordered}; custom=20/40/60/90";
    }

    private static string VerifyStrictLaneOrder()
    {
        var first = Note(0, BeatPosition.Preserve(1, 0, 1));
        var second = Note(0, BeatPosition.Preserve(1, 1, 5));
        var session = new PlaytestSession(Document(first, second), 0, autoPlay: false);

        var firstEvents = session.PressLane(0, 590);
        Require(firstEvents.Count == 1 && ReferenceEquals(firstEvents[0].Note.Note, session.Document.Notes[0]),
            "A dense lane selected the nearest later Note instead of its first pending Note.");
        Require(firstEvents[0].Grade == PlaytestHitGrade.Good &&
                Math.Abs(firstEvents[0].OffsetMilliseconds - 90) < 1e-9,
            "The first dense Note did not keep its +90 ms GOOD judgement.");
        var secondEvents = session.PressLane(0, 600);
        Require(secondEvents.Count == 1 && ReferenceEquals(secondEvents[0].Note.Note, session.Document.Notes[1]) &&
                secondEvents[0].Grade == PlaytestHitGrade.Best,
            "The second dense Note did not remain available after judging the first.");
        return $"first={firstEvents[0].OffsetMilliseconds:+0;-0;0}/{firstEvents[0].Grade}; " +
               $"second={secondEvents[0].OffsetMilliseconds:+0;-0;0}/{secondEvents[0].Grade}";
    }

    private static string VerifyLongNotes()
    {
        var document = Document(Note(
            0,
            BeatPosition.Preserve(1, 0, 1),
            BeatPosition.Preserve(2, 0, 1)));

        var early = new PlaytestSession(document, 0, autoPlay: false);
        Require(early.PressLane(0, 500).Single().Grade == PlaytestHitGrade.Best,
            "LN head was not BEST at its exact timestamp.");
        var earlyTail = early.ReleaseLane(0, 829.999).Single();
        Require(earlyTail.Grade == PlaytestHitGrade.Miss && earlyTail.Completed,
            "LN release earlier than tail-170 ms was not MISS/complete.");

        var within = new PlaytestSession(document, 0, autoPlay: false);
        within.PressLane(0, 500);
        var withinTail = within.ReleaseLane(0, 900).Single();
        Require(withinTail.Grade == PlaytestHitGrade.Good,
            "LN release at -100 ms did not degrade to GOOD.");

        var exact = new PlaytestSession(document, 0, autoPlay: false);
        exact.PressLane(0, 500);
        var exactTail = exact.ReleaseLane(0, 1000).Single();
        Require(exactTail.Grade == PlaytestHitGrade.Best && exact.IsComplete,
            "LN exact-tail release did not retain BEST and complete the chart.");

        var held = new PlaytestSession(document, 0, autoPlay: false);
        held.PressLane(0, 500);
        var automaticTail = held.Advance(1000).Single(item => !item.IsHead);
        Require(automaticTail.Grade == PlaytestHitGrade.Best && held.IsComplete,
            "Held LN did not complete at the tail without an early release.");
        return $"early={earlyTail.Grade}; -100ms={withinTail.Grade}; exact={exactTail.Grade}; held={automaticTail.Grade}";
    }

    private static string VerifyAutoPlayAndFromHere()
    {
        var document = Document(
            Note(0, BeatPosition.Zero),
            Note(1, BeatPosition.Preserve(1, 0, 1)),
            Note(2, BeatPosition.Preserve(2, 0, 1)),
            Note(3, BeatPosition.Preserve(3, 0, 1)));
        var automatic = new PlaytestSession(document, 2, autoPlay: true);
        Require(Math.Abs(automatic.StartMilliseconds - 1000) < 1e-9,
            "From-here start did not map beat 2 to 1000 ms at 120 BPM.");
        Require(automatic.Notes[0].Completed && automatic.Notes[1].Completed &&
                !automatic.Notes[2].Completed,
            "AutoPlay from-here skip boundary differs from WPF.");
        Require(automatic.Advance(999.999).Count == 0,
            "AutoPlay judged a Note before its exact timestamp.");
        var first = automatic.Advance(1000).Single();
        var second = automatic.Advance(1500).Single();
        Require(first.Grade == PlaytestHitGrade.Best && second.Grade == PlaytestHitGrade.Best &&
                automatic.Combo == 2 && automatic.Accuracy == 1 && automatic.IsComplete,
            "AutoPlay did not produce two BEST judgements and a full combo from the red line.");

        var manual = new PlaytestSession(document, 2, autoPlay: false);
        Require(manual.Notes[0].Completed && manual.Notes[1].Completed && !manual.Notes[2].Completed,
            "Manual from-here did not skip Notes older than the 170 ms window.");
        var manualHit = manual.PressLane(2, 1000).Single();
        Require(manualHit.Grade == PlaytestHitGrade.Best,
            "Manual from-here did not leave the red-line Note playable.");
        return $"start={automatic.StartMilliseconds:0}ms; autoCombo={automatic.Combo}; manual={manualHit.Grade}";
    }

    private static string VerifyDraftAndSourceIsolation()
    {
        var exactBeat = BeatPosition.Preserve(3, 3, 6);
        var official = Note(0, exactBeat);
        var draft = Note(1, BeatPosition.Preserve(4, 0, 1));
        draft.IsDraft = true;
        draft.DraftColumn = 12;
        var source = Document(official, draft);
        var before = MalodyMcSerializer.Serialize(source);
        var session = new PlaytestSession(source, 0, autoPlay: false);
        Require(session.Notes.Count == 1 && !session.Notes[0].Note.IsDraft,
            "Draft Note entered the playtest runtime.");
        Require(session.Notes[0].Note.Beat.Numerator == 3 &&
                session.Notes[0].Note.Beat.Denominator == 6,
            "Playtest clone reduced an exact rational Beat expression.");
        session.Advance(session.Notes[0].StartMilliseconds + 171);
        var after = MalodyMcSerializer.Serialize(source);
        Require(before == after && !ReferenceEquals(source, session.Document) &&
                !ReferenceEquals(source.Notes[0], session.Document.Notes[0]),
            "Playtest mutated or reused the editor/source document.");
        return $"runtime={session.Notes.Count}; exact={session.Notes[0].Note.Beat}; sourceBytes={before.Length}";
    }

    private static string VerifySettingsPolicy()
    {
        var clamped = new WindowsPlaytestSettings(
            noteStyle: (WindowsPlaytestNoteStyle)999,
            noteWidth: -5,
            noteHeight: 999,
            playfieldWidth: 9999,
            scrollSpeed: -1,
            judgementLinePosition: .1,
            comboYPosition: -1,
            accuracyYPosition: 99,
            bestWindowMilliseconds: -5,
            greatWindowMilliseconds: 70,
            goodWindowMilliseconds: 999,
            missWindowMilliseconds: 50);
        Require(clamped.NoteStyle == WindowsPlaytestNoteStyle.Brick &&
                clamped.NoteWidth == .25 && clamped.NoteHeight == 120 &&
                clamped.PlayfieldWidth == 1600 && clamped.ScrollSpeed == 120 &&
                clamped.JudgementLinePosition == .55 &&
                clamped.ComboYPosition == .04 &&
                clamped.AccuracyYPosition == .45 &&
                clamped.BestWindowMilliseconds == 1 &&
                clamped.GreatWindowMilliseconds == 70 &&
                clamped.GoodWindowMilliseconds == 200 &&
                clamped.MissWindowMilliseconds == 200,
            "Playtest settings clamps differ from WPF EditorViewSettings.Normalize.");
        var four = WindowsPlaytestSettings.CreateDefaultBindings(4);
        var five = WindowsPlaytestSettings.CreateDefaultBindings(5);
        var eighteen = WindowsPlaytestSettings.CreateDefaultBindings(18);
        Require(four.SequenceEqual(new[] { "D", "F", "J", "K" }) && five[2] == "Space" &&
                eighteen.Length == 18 && eighteen.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 18,
            "One or more 1K-18K WPF default playtest bindings differ.");
        var swapped = WindowsPlaytestSettings.AssignBinding(four, 0, "K");
        Require(swapped.SequenceEqual(new[] { "K", "F", "J", "D" }),
            "Binding conflict did not swap the two lanes.");
        four[0] = "Q";
        Require(WindowsPlaytestSettings.Default.BindingsFor(4)[0] == "D",
            "BindingsFor returned mutable store-owned state.");
        return $"clamp={clamped.NoteWidth:0.00}/{clamped.NoteHeight:0}/{clamped.PlayfieldWidth:0}; " +
               $"4K={string.Join(',', WindowsPlaytestSettings.Default.BindingsFor(4))}; swap={string.Join(',', swapped)}";
    }

    private static string VerifySettingsPersistence(string root)
    {
        Directory.CreateDirectory(root);
        var previousRoot = Environment.GetEnvironmentVariable(LocalDataRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalDataRootVariable, root);
            var bindings = new Dictionary<int, string[]>
            {
                [4] = ["A", "S", "K", "L"]
            };
            var expected = new WindowsPlaytestSettings(
                autoPlay: false,
                noteStyle: WindowsPlaytestNoteStyle.Circle,
                noteWidth: .73,
                noteHeight: 41,
                playfieldWidth: 777,
                scrollSpeed: 913,
                judgementLinePosition: .79,
                keyBindings: bindings,
                comboYPosition: .15,
                accuracyYPosition: .24,
                bestWindowMilliseconds: 33,
                greatWindowMilliseconds: 67,
                goodWindowMilliseconds: 111,
                missWindowMilliseconds: 188);
            WindowsPlaytestSettingsStore.Save(expected);
            var path = Path.Combine(root, "playtest-settings.json");
            Require(File.Exists(path) && !Directory.EnumerateFiles(root, "*.tmp").Any(),
                "Atomic playtest settings save left no destination or leaked a temporary file.");
            var loaded = WindowsPlaytestSettingsStore.Load();
            Require(!loaded.AutoPlay && loaded.NoteStyle == WindowsPlaytestNoteStyle.Circle &&
                    loaded.NoteWidth == .73 && loaded.NoteHeight == 41 &&
                    loaded.PlayfieldWidth == 777 && loaded.ScrollSpeed == 913 &&
                    loaded.JudgementLinePosition == .79 &&
                    loaded.ComboYPosition == .15 &&
                    loaded.AccuracyYPosition == .24 &&
                    loaded.BestWindowMilliseconds == 33 &&
                    loaded.GreatWindowMilliseconds == 67 &&
                    loaded.GoodWindowMilliseconds == 111 &&
                    loaded.MissWindowMilliseconds == 188 &&
                    loaded.BindingsFor(4).SequenceEqual(bindings[4]),
                "Playtest settings did not round-trip structurally.");

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    AutoPlay = false,
                    NoteStyle = nameof(WindowsPlaytestNoteStyle.Circle),
                    NoteWidth = .73,
                    NoteHeight = 41,
                    PlayfieldWidth = 777,
                    ScrollSpeed = 913,
                    JudgementLinePosition = .79,
                    KeyBindings = bindings
                }),
                System.Text.Encoding.UTF8);
            var legacy = WindowsPlaytestSettingsStore.Load();
            Require(
                legacy.ComboYPosition == .12 && legacy.AccuracyYPosition == .19 &&
                legacy.BestWindowMilliseconds == 45 &&
                legacy.GreatWindowMilliseconds == 85 &&
                legacy.GoodWindowMilliseconds == 130 &&
                legacy.MissWindowMilliseconds == 170,
                "Additive HUD settings did not preserve defaults for a schema-v1 settings file.");

            WindowsPlaytestSettingsStore.Save(expected.With(noteHeight: 44));
            Require(WindowsPlaytestSettingsStore.Load().NoteHeight == 44 &&
                    !Directory.EnumerateFiles(root, "*.tmp").Any(),
                "Atomic replace did not persist the second settings revision cleanly.");

            File.WriteAllText(path, "{broken-json", System.Text.Encoding.UTF8);
            var corrupt = WindowsPlaytestSettingsStore.Load();
            Require(IsDefault(corrupt), "Corrupt playtest settings did not fail closed to defaults.");
            File.WriteAllText(path, JsonSerializer.Serialize(new { SchemaVersion = 999 }),
                System.Text.Encoding.UTF8);
            var future = WindowsPlaytestSettingsStore.Load();
            Require(IsDefault(future), "Unknown playtest settings schema did not fail closed to defaults.");
            return $"path={path}; bytes={new FileInfo(path).Length}; corrupt/default=true; schema999/default=true";
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalDataRootVariable, previousRoot);
        }
    }

    private static string VerifySurfaceCulling()
    {
        const int noteCount = 100_000;
        var notes = new List<ChartNote>(noteCount)
        {
            Note(0, BeatPosition.Zero, BeatPosition.Preserve(2500, 0, 1))
        };
        for (var index = 1; index < noteCount; index++)
            notes.Add(Note(index % 4, BeatPosition.FromDouble(index / 48d)));
        var session = new PlaytestSession(Document(notes.ToArray()), 0, autoPlay: false);
        var surface = new PlaytestSurface();
        surface.SetSession(session);
        surface.Configure(WindowsPlaytestSettings.Default);
        surface.SetCurrentTime(session.Notes[50_000].StartMilliseconds);
        var diagnostics = surface.EvaluateViewportForDiagnostics(2160);
        Require(diagnostics.RuntimeNoteCount == noteCount && diagnostics.PerNoteControlCount == 0 &&
                diagnostics.SingleCustomControl && diagnostics.VisibleAreaClipping,
            "Playtest surface violated its one-control, zero-per-Note or clipping contract.");
        Require(diagnostics.LastVisitedNotes is > 0 and < 2_000,
            $"A 4K viewport visited {diagnostics.LastVisitedNotes} of {noteCount} Notes instead of a culled window.");
        return $"runtime={diagnostics.RuntimeNoteCount}; visited={diagnostics.LastVisitedNotes}; " +
               $"perNoteControls={diagnostics.PerNoteControlCount}; single={diagnostics.SingleCustomControl}";
    }

    private static string VerifyIdleAdvanceAllocation()
    {
        var session = new PlaytestSession(
            Document(Note(0, BeatPosition.Preserve(100_000, 0, 1))),
            0,
            autoPlay: false);
        for (var index = 0; index < 256; index++) _ = session.Advance(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++) _ = session.Advance(0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(
            allocated <= 1_024,
            $"10,000 idle playtest frames allocated {allocated:N0} bytes.");
        return $"frames=10000; allocated={allocated:N0} bytes";
    }

    private static string VerifyManualKeySoundRouting()
    {
        var musicRoute = WindowsPlaytestManualKeySoundPolicy.Resolve(musicLoaded: true);
        var silentRoute = WindowsPlaytestManualKeySoundPolicy.Resolve(musicLoaded: false);
        var autoplaySingleVoiceGain = WindowsKeySoundService.ChordVoiceGain(1);
        var twoVoiceCombinedGain = WindowsKeySoundService.ChordVoiceGain(2) * 2;
        var threeVoiceCombinedGain = WindowsKeySoundService.ChordVoiceGain(3) * 3;
        var fourVoiceCombinedGain = WindowsKeySoundService.ChordVoiceGain(4) * 4;
        var effectiveFullVolumeSingleGain = WindowsKeySoundService.ApplyOutputBoost(
            autoplaySingleVoiceGain,
            1.5f);
        Require(
            musicRoute.Output == WindowsPlaytestManualKeySoundOutput.SharedMusic &&
            musicRoute.OutputLatencyMilliseconds == 40 &&
            !WindowsPlaytestManualKeySoundPolicy.RequiresStandaloneWarmUp(musicLoaded: true),
            "Music-backed manual Key 音 did not select the shared 40 ms output.");
        Require(
            silentRoute.Output == WindowsPlaytestManualKeySoundOutput.StandaloneLowLatency &&
            silentRoute.OutputLatencyMilliseconds ==
            WindowsKeySoundService.StandaloneOutputLatencyMilliseconds &&
            WindowsPlaytestManualKeySoundPolicy.RequiresStandaloneWarmUp(musicLoaded: false),
            "Silent playtest manual Key 音 did not retain the standalone fallback.");
        Require(
            Math.Abs(musicRoute.Gain - autoplaySingleVoiceGain) < 1e-6 &&
            Math.Abs(silentRoute.Gain - autoplaySingleVoiceGain) < 1e-6 &&
            musicRoute.Gain is > 0 and < 1,
            $"Manual gain {musicRoute.Gain:R} does not match AutoPlay headroom " +
            $"{autoplaySingleVoiceGain:R}.");
        Require(
            Math.Abs(autoplaySingleVoiceGain - WindowsKeySoundService.SingleVoiceGain) < 1e-6 &&
            autoplaySingleVoiceGain >= .65f &&
            autoplaySingleVoiceGain < twoVoiceCombinedGain &&
            twoVoiceCombinedGain < threeVoiceCombinedGain &&
            threeVoiceCombinedGain < fourVoiceCombinedGain &&
            fourVoiceCombinedGain < 2,
            $"Key 音 loudness curve is not louder, progressive and bounded: " +
            $"{autoplaySingleVoiceGain:R}/{twoVoiceCombinedGain:R}/" +
            $"{threeVoiceCombinedGain:R}/{fourVoiceCombinedGain:R}.");
        Require(
            Math.Abs(WindowsKeySoundService.OutputBoost - 4f) < 1e-6f &&
            effectiveFullVolumeSingleGain >= 2.71f &&
            Math.Abs(WindowsKeySoundService.ApplyOutputBoost(1, 1.5f) - 4f) < 1e-6f &&
            WindowsKeySoundService.ApplyOutputBoost(-1, 1.5f) == 0 &&
            Math.Abs(WindowsKeySoundService.ApplyOutputBoost(3, 1.5f) - 6f) < 1e-6f,
            $"Global Key-sound boost is not consistently applied before limiting: " +
            $"boost={WindowsKeySoundService.OutputBoost:R}; " +
            $"single={effectiveFullVolumeSingleGain:R}.");
        var quietestRenderedPeak = new[]
            {
                KeySoundPreset.Snare,
                KeySoundPreset.BrightTick,
                KeySoundPreset.HandDrum,
                KeySoundPreset.HandDrum2,
                KeySoundPreset.DeepPulse
            }
            .Select(KeySoundPresetSamples.Create)
            .Select(samples => samples.Max(sample => MathF.Abs(
                WindowsKeySoundSoftLimiter.Limit(sample * effectiveFullVolumeSingleGain))))
            .Min();
        Require(
            quietestRenderedPeak >= .95f && quietestRenderedPeak < 1,
            $"A full-volume built-in Key sound still cannot rise above the music bed: " +
            $"quietestPeak={quietestRenderedPeak:R}.");
        Require(
            Math.Abs(WindowsKeySoundSettings.DefaultVolume - .85) < 1e-12,
            $"Default Key 音 volume is not the louder 85% baseline: " +
            $"{WindowsKeySoundSettings.DefaultVolume:R}.");
        return $"music={musicRoute.Output}/{musicRoute.OutputLatencyMilliseconds:0}ms; " +
               $"silent={silentRoute.Output}/{silentRoute.OutputLatencyMilliseconds:0}ms; " +
               $"gain={musicRoute.Gain:0.00}; chord={fourVoiceCombinedGain:0.00}; " +
               $"quietestPeak={quietestRenderedPeak:0.000}";
    }

    private static string VerifyPlaybackToggleKeyRouting()
    {
        string[] noSpace = ["D", "F", "J", "K"];
        string[] boundSpace = ["D", "F", "Space", "J", "K"];
        Require(
            WindowsPlaytestKeyRoutingPolicy.IsPlaybackToggle(Key.Enter, noSpace) &&
            WindowsPlaytestKeyRoutingPolicy.IsPlaybackToggle(Key.Space, noSpace),
            "Enter or unbound Space was not routed exclusively to playback.");
        Require(
            !WindowsPlaytestKeyRoutingPolicy.IsPlaybackToggle(Key.Space, boundSpace) &&
            !WindowsPlaytestKeyRoutingPolicy.IsPlaybackToggle(Key.F, noSpace),
            "A bound Space or lane key was incorrectly swallowed by transport routing.");
        return "keydown/key-up share one policy; unbound Space=transport; bound Space=lane";
    }

    private static string VerifyManualKeySoundLimiter()
    {
        Require(
            WindowsKeySoundSoftLimiter.Limit(.75f) == .75f &&
            WindowsKeySoundSoftLimiter.Limit(-.75f) == -.75f,
            "The limiter changed a signal below its knee.");
        var previous = WindowsKeySoundSoftLimiter.Threshold;
        foreach (var input in new[] { 1f, 1.5f, 4f, 8f })
        {
            var positive = WindowsKeySoundSoftLimiter.Limit(input);
            var negative = WindowsKeySoundSoftLimiter.Limit(-input);
            Require(
                positive >= previous && positive < 1 &&
                Math.Abs(negative + positive) < 1e-6f,
                $"Limiter was not monotonic, bounded and symmetric for {input:R}.");
            previous = positive;
        }
        var singleVoiceGain = WindowsKeySoundService.ChordVoiceGain(1);
        var dense = new[]
        {
            4 * singleVoiceGain,
            -8 * singleVoiceGain,
            float.NaN,
            float.PositiveInfinity
        };
        WindowsKeySoundSoftLimiter.Process(dense, 0, dense.Length);
        Require(
            dense[0] is > 0 and < 1 &&
            dense[1] is > -1 and < 0 &&
            dense[2] == 0 && dense[3] == 0,
            "Dense manual voices were not safely bounded or non-finite samples were retained.");
        return $"threshold={WindowsKeySoundSoftLimiter.Threshold:0.00}; dense={dense[0]:0.000}/{dense[1]:0.000}";
    }

    private static string VerifySharedKeySoundMixing()
    {
        const float musicLevel = .35f;
        const float keyLevel = .25f;
        var mixed = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(
            new ConstantSampleProvider(musicLevel),
            () => false);
        Require(mixed.TryEnqueue(new ConstantSampleProvider(keyLevel)),
            "The shared Key-sound overlay was rejected.");
        var audible = new float[8];
        Require(mixed.Read(audible, 0, audible.Length) == audible.Length,
            "The shared mixer did not return a complete device period.");
        var expected = musicLevel + keyLevel;
        Require(audible.All(sample => Math.Abs(sample - expected) < 1e-6f),
            $"The shared Key-sound overlay changed the underlying music gain: " +
            $"expected={expected:R}; actual={string.Join(',', audible.Select(value => value.ToString("R")))}.");

        var bounded = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(
            new ConstantSampleProvider(.8f),
            () => false);
        Require(bounded.TryEnqueue(new ConstantSampleProvider(.8f)),
            "The loud shared Key-sound overlay was rejected.");
        var limited = new float[8];
        Require(bounded.Read(limited, 0, limited.Length) == limited.Length &&
                limited.All(sample => sample is > .9f and < 1f),
            "The additive shared mix was not safely soft-limited.");

        var silentOverlay = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(
            new ConstantSampleProvider(musicLevel),
            () => false);
        Require(silentOverlay.TryEnqueue(new ConstantSampleProvider(0)),
            "The silent scheduling overlay was rejected.");
        var untouched = new float[8];
        Require(silentOverlay.Read(untouched, 0, untouched.Length) == untouched.Length &&
                untouched.All(sample => Math.Abs(sample - musicLevel) < 1e-6f),
            "A silent delayed overlay changed the music before the Key sound started.");
        return $"music={musicLevel:0.00}; key={keyLevel:0.00}; " +
               $"mixed={expected:0.00}; limited={limited[0]:0.000}";
    }

    private static string VerifyAudioClockStartupHandoff()
    {
        var arm = Stopwatch.GetTimestamp();
        var shortDelay = arm + Math.Max(1, Stopwatch.Frequency / 10);
        var longDelay = arm + Math.Max(1, Stopwatch.Frequency * 6 / 10);
        Require(
            WindowsAudioDeviceEngine.ShouldHoldAudioClockStartup(
                false,
                arm,
                shortDelay),
            "The audio clock ran ahead before the endpoint consumed its first sample.");
        Require(
            !WindowsAudioDeviceEngine.ShouldHoldAudioClockStartup(
                true,
                arm,
                shortDelay) &&
            !WindowsAudioDeviceEngine.ShouldHoldAudioClockStartup(
                false,
                arm,
                longDelay),
            "The audio clock startup hold did not release for an advanced clock or fallback path.");
        Require(
            !WindowsAudioDeviceEngine.CanActivateAudioClock(20, 40) &&
            WindowsAudioDeviceEngine.CanActivateAudioClock(40, 40) &&
            WindowsAudioDeviceEngine.CanActivateAudioClock(80, 40),
            "The device clock could take over before it caught the fallback position.");
        return "startup=held-until-device-progress; fallback=after-grace; handoff=monotonic";
    }

    private static string VerifyAudioClockAllocation(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "clock.fake");
        File.WriteAllBytes(path, [0]);
        var engine = new ClockAudioDeviceEngine
        {
            Position = TimeSpan.FromSeconds(12.5),
            Duration = TimeSpan.FromMinutes(3)
        };
        using var service = new WindowsAudioPlaybackService(
            () => engine,
            TimeSpan.Zero);
        service.LoadAsync(path).GetAwaiter().GetResult();
        for (var index = 0; index < 256; index++)
            _ = service.SampleClock();
        var checksum = 0L;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            var clock = service.SampleClock();
            checksum ^= clock.Position.Ticks;
            checksum ^= service.Position.Ticks;
            if (!service.IsLoaded) throw new InvalidOperationException("Loaded clock became empty.");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated <= 1_024, $"10,000 playtest clock samples allocated {allocated:N0} bytes.");
        return $"samples=10000; allocated={allocated:N0} bytes; checksum={checksum}";
    }

    private static string VerifyPitchPreservingSlowdownContinuity()
    {
        var processor = new SoundTouchProcessor
        {
            SampleRate = 44_100,
            Channels = 2,
            Tempo = .5,
            Pitch = 1,
            Rate = 1
        };
        WindowsPitchPreservingTimeStretchPolicy.Configure(processor);
        Require(
            processor.GetSetting(SettingId.UseQuickSeek) == 0 &&
            processor.GetSetting(SettingId.SequenceDurationMs) ==
                WindowsPitchPreservingTimeStretchPolicy.SequenceDurationMilliseconds &&
            processor.GetSetting(SettingId.SeekWindowDurationMs) ==
                WindowsPitchPreservingTimeStretchPolicy.SeekWindowDurationMilliseconds &&
            processor.GetSetting(SettingId.OverlapDurationMs) ==
                WindowsPitchPreservingTimeStretchPolicy.OverlapDurationMilliseconds,
            "Slow preserve-pitch playback lost its high-quality overlap profile.");
        var extremePitchFactor =
            WindowsPitchPreservingTimeStretchPolicy.ResolvePhaseVocoderStagePitchFactor(.2);
        Require(
            WindowsPitchPreservingTimeStretchPolicy.UsesPhaseVocoder(.2) &&
            WindowsPitchPreservingTimeStretchPolicy.UsesPhaseVocoder(.3) &&
            !WindowsPitchPreservingTimeStretchPolicy.UsesPhaseVocoder(.31) &&
            !WindowsPitchPreservingTimeStretchPolicy.UsesPhaseVocoder(.2, channels: 6) &&
            Math.Abs(Math.Pow(extremePitchFactor, 3) - 5) < .00001 &&
            WindowsPitchPreservingTimeStretchPolicy.PhaseVocoderLatencyFrames == 10_752,
            "Extreme preserve-pitch slowdown lost its three-stage phase-vocoder profile.");

        var expected = Enumerable.Range(1, 512)
            .Select(index => index / 1_024f)
            .ToArray();
        var bursty = new IntermittentSampleProvider(expected);
        var continuity = new SoundTouchContinuitySampleProvider(
            bursty,
            () => bursty.IsEnded,
            maximumReadAttemptsPerDevicePeriod: 4);
        var actual = new List<float>(expected.Length);
        var buffer = new float[64];
        int read;
        while ((read = continuity.Read(buffer, 0, buffer.Length)) > 0)
            actual.AddRange(buffer.AsSpan(0, read).ToArray());

        Require(
            continuity.IsEnded && actual.SequenceEqual(expected),
            "Transient empty SoundTouch batches leaked silence or reordered samples.");

        var permanentlyEmpty = new NeverEndingEmptySampleProvider();
        var boundedContinuity = new SoundTouchContinuitySampleProvider(
            permanentlyEmpty,
            () => false,
            maximumReadAttemptsPerDevicePeriod: 3);
        Require(
            boundedContinuity.Read(buffer, 0, buffer.Length) == 0 &&
            permanentlyEmpty.ReadCallCount == 3 &&
            !boundedContinuity.IsEnded,
            "A transiently empty provider can still make one device period decode without a bound.");

        var empty = new FiniteSineSampleProvider(seconds: 0);
        var emptyPhaseVocoder = new WindowsPhaseVocoderTimeStretchSampleProvider(
            empty,
            () => empty.IsEnded,
            .2);
        Require(
            emptyPhaseVocoder.Prime() &&
            emptyPhaseVocoder.Read(buffer, 0, buffer.Length) == 0 &&
            emptyPhaseVocoder.IsEnded,
            "An empty source became a never-ending silent phase-vocoder stream.");

        var extremePrime = PrimePitchPreservingProvider(.2f);
        var slowPrime = PrimePitchPreservingProvider(.5f);
        var normalPrime = PrimePitchPreservingProvider(1f);
        var realOggPath = @"D:\osu!\Songs\_song_39592\0\1777578413.ogg";
        var realOggExtreme = File.Exists(realOggPath)
            ? StreamRealAudioFile(realOggPath, .2f)
            : default;
        var realOggSlow = File.Exists(realOggPath)
            ? StreamRealAudioFile(realOggPath, .5f)
            : default;
        var realOggNormal = File.Exists(realOggPath)
            ? StreamRealAudioFile(realOggPath, 1f)
            : default;
        Require(
            extremePrime.Read == extremePrime.Requested &&
            extremePrime.Audible > extremePrime.Read / 2 &&
            extremePrime.EstimatedFrequency is > 390 and < 490 &&
            extremePrime.SteadyEstimatedFrequency is > 420 and < 460 &&
            extremePrime.SteadyAllocatedBytes <= 1_024 &&
            slowPrime.Read == slowPrime.Requested &&
            slowPrime.Audible > slowPrime.Read / 2 &&
            slowPrime.EstimatedFrequency is > 390 and < 490 &&
            normalPrime.Read == normalPrime.Requested &&
            normalPrime.Audible > normalPrime.Read / 2 &&
            normalPrime.EstimatedFrequency is > 390 and < 490 &&
            (!File.Exists(realOggPath) ||
             realOggExtreme.FullPeriods == realOggExtreme.Periods &&
             realOggExtreme.SilentPeriods == 0 &&
             realOggExtreme.MaximumSteadyReadMilliseconds < 40 &&
             realOggSlow.FullPeriods == realOggSlow.Periods &&
             realOggSlow.SilentPeriods == 0 &&
             realOggSlow.MaximumSteadyReadMilliseconds < 40 &&
             realOggNormal.FullPeriods == realOggNormal.Periods &&
             realOggNormal.SilentPeriods == 0 &&
             realOggNormal.MaximumSteadyReadMilliseconds < 40),
            $"A real SoundTouch slowdown failed to prime audible output: " +
            $"extreme={extremePrime}; slow={slowPrime}; normal={normalPrime}; " +
            $"oggExtreme={realOggExtreme}; oggSlow={realOggSlow}; " +
            $"oggNormal={realOggNormal}.");
        return $"sequence={processor.GetSetting(SettingId.SequenceDurationMs)}ms; " +
               $"seek={processor.GetSetting(SettingId.SeekWindowDurationMs)}ms; " +
               $"overlap={processor.GetSetting(SettingId.OverlapDurationMs)}ms; " +
               $"phase=3x{extremePitchFactor:0.000}/" +
               $"{WindowsPitchPreservingTimeStretchPolicy.PhaseVocoderFftSize}/" +
               $"{WindowsPitchPreservingTimeStretchPolicy.PhaseVocoderOversampling}; " +
               $"quick={processor.GetSetting(SettingId.UseQuickSeek)}; samples={actual.Count}; " +
               $"boundedReads={permanentlyEmpty.ReadCallCount}; " +
               $"extreme={extremePrime.Read}/{extremePrime.Audible}/" +
               $"{extremePrime.EstimatedFrequency:0}->" +
               $"{extremePrime.SteadyEstimatedFrequency:0}Hz/" +
               $"{extremePrime.SteadyAllocatedBytes:N0}B; " +
               $"slow={slowPrime.Read}/{slowPrime.Audible}/" +
               $"{slowPrime.EstimatedFrequency:0}Hz; " +
               $"normal={normalPrime.Read}/{normalPrime.Audible}/" +
               $"{normalPrime.EstimatedFrequency:0}Hz; " +
               $"oggExtreme={realOggExtreme.FullPeriods}/{realOggExtreme.Periods}/" +
               $"{realOggExtreme.MaximumSteadyReadMilliseconds:0.0}ms; " +
               $"oggSlow={realOggSlow.FullPeriods}/{realOggSlow.Periods}/" +
               $"{realOggSlow.MaximumSteadyReadMilliseconds:0.0}ms; " +
               $"oggNormal={realOggNormal.FullPeriods}/{realOggNormal.Periods}/" +
               $"{realOggNormal.MaximumSteadyReadMilliseconds:0.0}ms";
    }

    private static string VerifyAudioContainerSniffing()
    {
        Require(
            AudioDecoder.DetectContainer(
                Encoding.ASCII.GetBytes("ID3\u0003\0\0"),
                ".ogg") == AudioContainerKind.Mp3 &&
            AudioDecoder.DetectContainer(
                [0xFF, 0xFB, 0x90, 0x64],
                ".ogg") == AudioContainerKind.Mp3 &&
            AudioDecoder.DetectContainer(
                Encoding.ASCII.GetBytes("OggS\0\u0002"),
                ".mp3") == AudioContainerKind.OggVorbis &&
            AudioDecoder.DetectContainer(
                [0xFF, 0xF1, 0x50, 0x80],
                ".aac") == AudioContainerKind.Other &&
            AudioDecoder.DetectContainer([], ".ogg") == AudioContainerKind.OggVorbis &&
            AudioDecoder.DetectContainer([], ".mp3") == AudioContainerKind.Mp3,
            "Audio container sniffing no longer overrides misleading Malody extensions safely.");

        var realMisnamedMp3 = @"D:\osu!\Songs\_song_32065\0\1696998774.ogg";
        var realDetail = "fixture=absent";
        if (File.Exists(realMisnamedMp3))
        {
            using var stream = AudioDecoder.OpenWaveStream(realMisnamedMp3);
            var samples = stream.ToSampleProvider();
            var buffer = new float[4_096];
            var read = samples.Read(buffer, 0, buffer.Length);
            Require(
                stream is Mp3FileReader &&
                stream.TotalTime.TotalSeconds is > 200 and < 210 &&
                stream.WaveFormat.SampleRate == 48_000 &&
                stream.WaveFormat.Channels == 2 &&
                read == buffer.Length,
                "The real ID3-backed .ogg Malody resource still cannot be decoded as MP3.");
            realDetail = $"fixture=mp3/{stream.TotalTime.TotalSeconds:0.000}s/" +
                         $"{stream.WaveFormat.SampleRate}Hz/{read}";
        }

        return "ID3.ogg=mp3; frame.ogg=mp3; OggS.mp3=vorbis; " + realDetail;
    }

    private static PitchPreservingPrimeResult PrimePitchPreservingProvider(float tempo)
    {
        var sine = new FiniteSineSampleProvider(seconds: 2);
        var provider = CreatePitchPreservingProvider(
            sine,
            () => sine.IsEnded,
            tempo);
        var devicePeriod = new float[3_528];
        var read = provider.Samples.Read(devicePeriod, 0, devicePeriod.Length);
        var audible = devicePeriod.Take(read)
            .Count(sample => Math.Abs(sample) > 1e-5f);
        var estimatedFrequency = EstimateStereoPositiveCrossingFrequency(devicePeriod, read);
        var steadyRead = 0;
        for (var index = 0; index < 4; index++)
            steadyRead = provider.Samples.Read(devicePeriod, 0, devicePeriod.Length);
        var steadyEstimatedFrequency =
            EstimateStereoPositiveCrossingFrequency(devicePeriod, steadyRead);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 12; index++)
            _ = provider.Samples.Read(devicePeriod, 0, devicePeriod.Length);
        var steadyAllocatedBytes =
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return new PitchPreservingPrimeResult(
            tempo,
            devicePeriod.Length,
            read,
            audible,
            sine.IsEnded,
            provider.IsEnded(),
            estimatedFrequency,
            steadyEstimatedFrequency,
            steadyAllocatedBytes);
    }

    private static double EstimateStereoPositiveCrossingFrequency(
        float[] samples,
        int count)
    {
        var positiveCrossings = 0;
        for (var sample = 2; sample < count; sample += 2)
            if (samples[sample - 2] <= 0 && samples[sample] > 0)
                positiveCrossings++;
        var renderedSeconds = count / 2d / 44_100;
        return renderedSeconds > 0 ? positiveCrossings / renderedSeconds : 0;
    }

    private static SoundTouchStreamingResult StreamRealAudioFile(string path, float tempo)
    {
        using var stream = AudioDecoder.OpenWaveStream(path);
        stream.CurrentTime = TimeSpan.FromSeconds(60);
        var bounded = LengthCappedSampleProvider.FromWaveStream(stream);
        var provider = CreatePitchPreservingProvider(
            bounded,
            () => bounded.IsEnded,
            tempo);
        const int periods = 12;
        var devicePeriod = new float[3_528];
        var fullPeriods = 0;
        var silentPeriods = 0;
        var firstReadMilliseconds = 0d;
        var maximumSteadyReadMilliseconds = 0d;
        for (var period = 0; period < periods; period++)
        {
            Array.Clear(devicePeriod);
            var stopwatch = Stopwatch.StartNew();
            var read = provider.Samples.Read(devicePeriod, 0, devicePeriod.Length);
            stopwatch.Stop();
            if (period == 0)
                firstReadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            else
                maximumSteadyReadMilliseconds = Math.Max(
                    maximumSteadyReadMilliseconds,
                    stopwatch.Elapsed.TotalMilliseconds);
            if (read == devicePeriod.Length) fullPeriods++;
            if (!devicePeriod.Take(read).Any(sample => Math.Abs(sample) > 1e-5f))
                silentPeriods++;
        }
        return new SoundTouchStreamingResult(
            tempo,
            periods,
            fullPeriods,
            silentPeriods,
            firstReadMilliseconds,
            maximumSteadyReadMilliseconds);
    }

    private static PitchPreservingProvider CreatePitchPreservingProvider(
        ISampleProvider source,
        Func<bool> sourceReallyEnded,
        float tempo)
    {
        if (WindowsPitchPreservingTimeStretchPolicy.UsesPhaseVocoder(
                tempo,
                source.WaveFormat.Channels))
        {
            var phaseVocoder = new WindowsPhaseVocoderTimeStretchSampleProvider(
                source,
                sourceReallyEnded,
                tempo);
            Require(phaseVocoder.Prime(), "The phase vocoder could not remove startup latency.");
            return new PitchPreservingProvider(
                phaseVocoder,
                () => phaseVocoder.IsEnded);
        }

        IWaveProvider stageSource = source.ToWaveProvider();
        var processor = new SoundTouchProcessor();
        var stageProvider = new SoundTouchWaveProvider(stageSource, processor)
        {
            Tempo = (float)WindowsPitchPreservingTimeStretchPolicy.ResolveSoundTouchTempo(tempo),
            Pitch = 1,
            Rate = 1
        };
        WindowsPitchPreservingTimeStretchPolicy.Configure(processor);
        stageSource = stageProvider;
        var continuity = new SoundTouchContinuitySampleProvider(
            stageSource.ToSampleProvider(),
            sourceReallyEnded);
        return new PitchPreservingProvider(continuity, () => continuity.IsEnded);
    }

    private static KeySoundPresetMetrics MeasurePreset(float[] samples)
    {
        var frames = samples.Length / KeySoundPresetSamples.Channels;
        var peak = 0d;
        var absoluteDelta = 0d;
        var zeroCrossings = 0;
        var nonFiniteSamples = 0;
        var previous = 0d;
        for (var frame = 0; frame < frames; frame++)
        {
            var left = samples[frame * KeySoundPresetSamples.Channels];
            var right = samples[frame * KeySoundPresetSamples.Channels + 1];
            if (!float.IsFinite(left) || !float.IsFinite(right)) nonFiniteSamples++;
            var mono = (left + right) * .5;
            peak = Math.Max(peak, Math.Abs(mono));
            if (frame > 0)
            {
                absoluteDelta += Math.Abs(mono - previous);
                if ((mono > 0 && previous < 0) || (mono < 0 && previous > 0)) zeroCrossings++;
            }
            previous = mono;
        }
        return new KeySoundPresetMetrics(
            frames * 1_000d / KeySoundPresetSamples.SampleRate,
            peak,
            frames > 1 ? absoluteDelta / (frames - 1) : 0,
            zeroCrossings,
            nonFiniteSamples);
    }

    private static ulong Fingerprint(float[] samples)
    {
        unchecked
        {
            var hash = 14_695_981_039_346_656_037UL;
            foreach (var sample in samples)
            {
                hash ^= (uint)BitConverter.SingleToInt32Bits(sample);
                hash *= 1_099_511_628_211UL;
            }
            return hash;
        }
    }

    private static string SampleSha256(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool IsDefault(WindowsPlaytestSettings settings) =>
        settings.AutoPlay && settings.NoteStyle == WindowsPlaytestNoteStyle.Brick &&
        settings.NoteWidth == .82 && settings.NoteHeight == 28 &&
        settings.PlayfieldWidth == 520 && settings.ScrollSpeed == 760 &&
        settings.JudgementLinePosition == .84 &&
        settings.ComboYPosition == .12 && settings.AccuracyYPosition == .19 &&
        settings.BestWindowMilliseconds == 45 &&
        settings.GreatWindowMilliseconds == 85 &&
        settings.GoodWindowMilliseconds == 130 &&
        settings.MissWindowMilliseconds == 170 &&
        settings.BindingsFor(4).SequenceEqual(new[] { "D", "F", "J", "K" });

    private static ChartDocument Document(params ChartNote[] notes) => new()
    {
        KeyCount = 4,
        Timing =
        [
            new TimingEvent
            {
                Beat = BeatPosition.Zero,
                Bpm = 120
            }
        ],
        Notes = notes.ToList()
    };

    private static ChartNote Note(int lane, BeatPosition beat, BeatPosition? endBeat = null) => new()
    {
        Lane = lane,
        Beat = beat,
        EndBeat = endBeat
    };

    private static void Check(
        ICollection<WindowsPlaytestSpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsPlaytestSpecResult(name, true, verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsPlaytestSpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ClockAudioDeviceEngine : IWindowsAudioDeviceEngine
    {
        public bool IsLoaded { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool HasOutput => IsLoaded;
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
        public double OutputLatencyMilliseconds => 80;
        public double PlaybackRate { get; set; } = 1;
        public bool PreservePitch { get; set; }
        public float Volume { get; set; } = 1;
        public event EventHandler? PlaybackStopped
        {
            add { }
            remove { }
        }
        public event EventHandler<AudioOutputDeviceChangedEventArgs>? DefaultOutputDeviceChanged
        {
            add { }
            remove { }
        }

        public void Load(string path)
        {
            _ = path;
            IsLoaded = true;
        }

        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Stop() => IsPlaying = false;
        public void Seek(TimeSpan position) => Position = position;
        public void RefreshDefaultOutputDevice(bool resumePlayback) =>
            IsPlaying = resumePlayback;
        public bool TryPlayOverlay(
            ISampleProvider provider,
            AudioPlaybackEpochToken playbackEpoch)
        {
            _ = provider;
            _ = playbackEpoch;
            return false;
        }

        public void Dispose()
        {
            IsLoaded = false;
            IsPlaying = false;
        }
    }

    private sealed class ConstantSampleProvider(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Fill(buffer, value, offset, count);
            return count;
        }
    }

    private sealed class IntermittentSampleProvider(float[] samples) : ISampleProvider
    {
        private int position;
        private bool returnGap = true;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
        public bool IsEnded => position >= samples.Length;

        public int Read(float[] buffer, int offset, int count)
        {
            if (returnGap)
            {
                returnGap = false;
                return 0;
            }
            returnGap = true;
            if (IsEnded) return 0;
            var read = Math.Min(count, Math.Min(90, samples.Length - position));
            Array.Copy(samples, position, buffer, offset, read);
            position += read;
            return read;
        }
    }

    private sealed class NeverEndingEmptySampleProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
        public int ReadCallCount { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            _ = buffer;
            _ = offset;
            _ = count;
            ReadCallCount++;
            return 0;
        }
    }

    private sealed class FiniteSineSampleProvider(double seconds) : ISampleProvider
    {
        private readonly int totalFrames = (int)Math.Round(44_100 * seconds);
        private int position;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2);
        public bool IsEnded => position >= totalFrames;

        public int Read(float[] buffer, int offset, int count)
        {
            var requestedFrames = count / WaveFormat.Channels;
            var frames = Math.Min(requestedFrames, totalFrames - position);
            for (var frame = 0; frame < frames; frame++)
            {
                var value = (float)(Math.Sin(
                    2 * Math.PI * 440 * (position + frame) / WaveFormat.SampleRate) * .5);
                var sample = offset + frame * WaveFormat.Channels;
                buffer[sample] = value;
                buffer[sample + 1] = value;
            }
            position += frames;
            return frames * WaveFormat.Channels;
        }
    }

    private readonly record struct KeySoundPresetMetrics(
        double DurationMilliseconds,
        double Peak,
        double MeanAbsoluteDelta,
        int ZeroCrossings,
        int NonFiniteSamples)
    {
        public override string ToString() =>
            $"{DurationMilliseconds:0}ms/peak={Peak:0.000}/" +
            $"delta={MeanAbsoluteDelta:0.0000}/zc={ZeroCrossings}";
    }

    private readonly record struct PitchPreservingProvider(
        ISampleProvider Samples,
        Func<bool> IsEnded);

    private readonly record struct PitchPreservingPrimeResult(
        float Tempo,
        int Requested,
        int Read,
        int Audible,
        bool SourceEnded,
        bool ContinuityEnded,
        double EstimatedFrequency,
        double SteadyEstimatedFrequency,
        long SteadyAllocatedBytes);

    private readonly record struct SoundTouchStreamingResult(
        float Tempo,
        int Periods,
        int FullPeriods,
        int SilentPeriods,
        double FirstReadMilliseconds,
        double MaximumSteadyReadMilliseconds);
}

internal sealed record WindowsPlaytestSpecResult(
    string Name,
    bool Passed,
    string Detail);

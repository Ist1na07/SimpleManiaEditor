using LumaChart.Application.Audio;
using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class VariableTempoEstimatorSpecs
{
    public static void Run()
    {
        KeepsExactAnalysisClockFor44100HzAudio();
        KeepsChannelFramesAcrossDecoderReads();
        RejectsSilentTempoEnvelope();
        CalibratesIntegerTemposWithoutFlatteningFractionalTempo();
        CalibratesNearIntegerTempoWithCompetingTexture();
        UsesConservativeLongTrackIntegerResidualCorridor();
        UsesLongRangeEvidenceForWideNoisyTempos();
        CancelsPrecisionRefinement();
        DetectsStableTempoTimeline();
        KeepsSingleTempoAsOneSegment();
        RejectsAShortFalseTempoIntro();
        AlignsPositiveParadeStyleBoundariesToOneMusicalPhase();
        MapsAudioBoundariesToMusicalBeats();
        PreservesExactComplexBoundarySubdivision();
        ReplacesOnlyBpmDataAndPreservesEffects();
        GeneratesDeterministicScrollCompensation();
    }

    private static void KeepsExactAnalysisClockFor44100HzAudio()
    {
        var reducer = new ExactRateEnergyReducer(44_100, 200);
        var frames = 0;
        for (var index = 0; index < 44_100 * 5; index++)
            if (reducer.AddSample(1, out _)) frames++;
        Assert(frames == 1_000,
            $"44.1 kHz envelope clock drifted: expected 1000 frames, got {frames}");
    }

    private static void KeepsChannelFramesAcrossDecoderReads()
    {
        var reducer = new ExactRateEnergyReducer(sourceRate: 2, targetRate: 1, channels: 2);
        var chunks = new[]
        {
            new[] { 1d },
            new[] { 3d, 2d, 4d, 5d },
            new[] { 7d }
        };
        var energies = new List<double>();
        foreach (var chunk in chunks)
        foreach (var sample in chunk)
            if (reducer.AddSample(sample, out var energy)) energies.Add(energy);

        Assert(energies.Count == 1,
            $"decoder chunk boundaries emitted {energies.Count} energy frames instead of one");
        Assert(Math.Abs(energies[0] - Math.Sqrt(6.5)) < 1e-9,
            $"decoder chunk boundaries changed stereo downmix energy: {energies[0]}");
    }

    private static void RejectsSilentTempoEnvelope()
    {
        try
        {
            VariableTempoEstimator.Estimate(new double[2_000], 100);
            throw new InvalidOperationException("silent envelope produced a BPM estimate");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void CalibratesIntegerTemposWithoutFlatteningFractionalTempo()
    {
        const int rate = 200;
        foreach (var expected in new[] { 105d, 140d, 155d, 175d, 128.5d, 128.25d })
        {
            var novelty = BuildSyntheticNovelty(rate, 56, [(0d, expected)]);
            var coarse = expected switch
            {
                105 => 104.416,
                140 => 139.86,
                155 => 154.638,
                175 => 174.953,
                128.5 => 128.42,
                _ => 128.18
            };
            var result = TempoPrecisionRefiner.Refine(novelty, rate, 0, novelty.Length, coarse);
            Assert(Math.Abs(result.Bpm - expected) <= .08,
                $"precision refit drifted: expected {expected}, got {result.Bpm}");
        }

        var fractional = BuildSyntheticNovelty(rate, 56, [(0d, 128.5d)]);
        var fractionalResult = TempoPrecisionRefiner.Refine(fractional, rate, 0, fractional.Length, 128.42);
        Assert(Math.Abs(fractionalResult.Bpm - Math.Round(fractionalResult.Bpm)) >= .35,
            $"real fractional BPM was forced to an integer: {fractionalResult.Bpm}");
    }

    private static void CalibratesNearIntegerTempoWithCompetingTexture()
    {
        const int rate = 200;
        var novelty = BuildWideNoisyNovelty(rate, 48, 175, seed: 7);
        AddWidePulseTrain(novelty, rate, 174.6, strength: .4);

        var result = TempoPrecisionRefiner.Refine(novelty, rate, 0, novelty.Length, 174.953);
        Assert(result.Bpm == 175 && result.UsedIntegerPrior,
            $"near-integer 175 BPM residual was not calibrated: raw {result.RawBpm}, result {result.Bpm}");
    }

    private static void UsesConservativeLongTrackIntegerResidualCorridor()
    {
        const int rate = 200;
        var nearInteger = BuildWideNoisyNovelty(rate, 240, 175, seed: 19);
        AddWidePulseTrain(nearInteger, rate, 174.6, strength: .44);

        var calibrated = TempoPrecisionRefiner.Refine(nearInteger, rate, 0, nearInteger.Length, 174.953);
        Assert(calibrated.Bpm == 175 && calibrated.UsedIntegerPrior,
            $"long-track 175 BPM residual was not calibrated: raw {calibrated.RawBpm}, result {calibrated.Bpm}");

        foreach (var expected in new[] { 174.9, 174.95, 175.05, 175.2 })
        {
            var fractional = BuildWideNoisyNovelty(rate, 240, expected, seed: (int)(expected * 100));
            var result = TempoPrecisionRefiner.Refine(fractional, rate, 0, fractional.Length, expected);
            Assert(Math.Abs(result.Bpm - expected) <= .08,
                $"long-track fractional BPM drifted: expected {expected}, got {result.Bpm}");
            Assert(!result.UsedIntegerPrior,
                $"long-track fractional BPM was flattened to 175: expected {expected}, raw {result.RawBpm}");
        }

        foreach (var item in new[] { (Duration: 56d, Expected: 174.95), (Duration: 119d, Expected: 175.05) })
        {
            var fractional = BuildWideNoisyNovelty(
                rate, item.Duration, item.Expected, seed: (int)(item.Duration * 100 + item.Expected));
            var result = TempoPrecisionRefiner.Refine(fractional, rate, 0, fractional.Length, item.Expected);
            Assert(Math.Abs(result.Bpm - item.Expected) <= .08 && !result.UsedIntegerPrior,
                $"short fractional BPM was flattened: duration {item.Duration}, expected {item.Expected}, " +
                $"raw {result.RawBpm}, result {result.Bpm}");
        }
    }

    private static void UsesLongRangeEvidenceForWideNoisyTempos()
    {
        const int rate = 200;
        foreach (var item in new[]
                 {
                     (Expected: 105d, Coarse: 104.416),
                     (Expected: 140d, Coarse: 139.86),
                     (Expected: 155d, Coarse: 154.638)
                 })
        {
            var novelty = BuildWideNoisyNovelty(rate, 64, item.Expected, (int)item.Expected);
            var result = TempoPrecisionRefiner.Refine(novelty, rate, 0, novelty.Length, item.Coarse);
            Assert(Math.Abs(result.Bpm - item.Expected) <= .1,
                $"wide/noisy integer tempo drifted: expected {item.Expected}, got {result.Bpm}");
        }

        foreach (var item in new[]
                 {
                      (Expected: 104.4, Coarse: 104.416),
                      (Expected: 139.6, Coarse: 139.86),
                      (Expected: 154.6, Coarse: 154.638),
                      (Expected: 174.6, Coarse: 174.953)
                 })
        {
            var novelty = BuildWideNoisyNovelty(rate, 64, item.Expected, (int)(item.Expected * 10));
            var result = TempoPrecisionRefiner.Refine(novelty, rate, 0, novelty.Length, item.Coarse);
            Assert(Math.Abs(result.Bpm - item.Expected) <= .12,
                $"wide/noisy fractional tempo drifted: expected {item.Expected}, got {result.Bpm}");
            Assert(Math.Abs(result.Bpm - Math.Round(result.Bpm)) >= .25 && !result.UsedIntegerPrior,
                $"wide/noisy fractional tempo was flattened to an integer: {result.Bpm}");
        }
    }

    private static void CancelsPrecisionRefinement()
    {
        const int rate = 200;
        var novelty = BuildSyntheticNovelty(rate, 16, [(0d, 140d)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            TempoPrecisionRefiner.Refine(novelty, rate, 0, novelty.Length, 139.86, cancellation.Token);
            throw new InvalidOperationException("cancelled precision refinement continued running");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void DetectsStableTempoTimeline()
    {
        const int rate = 100;
        var expected = new[]
        {
            (Start: 0d, Bpm: 105d),
            (Start: 32d, Bpm: 155d),
            (Start: 61d, Bpm: 105d),
            (Start: 82d, Bpm: 155d)
        };
        var novelty = BuildSyntheticNovelty(rate, 112, expected);
        var result = VariableTempoEstimator.Estimate(novelty, rate);

        Assert(result.Segments.Count == expected.Length,
            $"expected {expected.Length} stable tempo segments, got {result.Segments.Count}");
        for (var index = 0; index < expected.Length; index++)
        {
            Assert(Math.Abs(result.Segments[index].Bpm - expected[index].Bpm) <= 1.2,
                $"segment {index} BPM drifted: expected {expected[index].Bpm}, got {result.Segments[index].Bpm}");
            if (index > 0)
                Assert(Math.Abs(result.Segments[index].StartSeconds - expected[index].Start) <= 3,
                    $"segment {index} boundary drifted: expected {expected[index].Start}, got {result.Segments[index].StartSeconds}");
        }
    }

    private static void KeepsSingleTempoAsOneSegment()
    {
        const int rate = 100;
        var novelty = BuildSyntheticNovelty(rate, 64, [(0d, 128d)]);
        var result = VariableTempoEstimator.Estimate(novelty, rate);
        Assert(result.Segments.Count == 1, "a stable song was split into false BPM changes");
        Assert(Math.Abs(result.Segments[0].Bpm - 128) <= 1.2,
            $"stable BPM estimate drifted: {result.Segments[0].Bpm}");
    }

    private static void RejectsAShortFalseTempoIntro()
    {
        const int rate = 100;
        var novelty = BuildSyntheticNovelty(rate, 72, [(0d, 140d)]);
        var falsePeriod = 60d / 185;
        for (var time = .11; time < 7.5; time += falsePeriod)
            AddPulse(novelty, time * rate, .72);

        var result = VariableTempoEstimator.Estimate(novelty, rate);
        Assert(result.Segments.Count == 1,
            $"short rhythmic intro created {result.Segments.Count} false tempo segments");
        Assert(Math.Abs(result.Segments[0].Bpm - 140) <= .2,
            $"stable 140 BPM body was not retained: {result.Segments[0].Bpm}");
    }

    private static void MapsAudioBoundariesToMusicalBeats()
    {
        const double delayMilliseconds = 432;
        var first = -.432 + 57.5 * 60d / 105;
        var second = first + 72 * 60d / 155;
        var third = second + 36 * 60d / 105;
        var segments = new[]
        {
            new TempoSegmentEstimate(0, 105, .9, 10),
            new TempoSegmentEstimate(first, 155, .9, 10),
            new TempoSegmentEstimate(second, 105, .9, 10),
            new TempoSegmentEstimate(third, 155, .9, 10)
        };
        var estimate = new VariableTempoEstimate(segments, [], delayMilliseconds, 120);
        var mapped = VariableTempoEstimator.MapToTiming(estimate, 2);
        Assert(mapped.Count == 4, "tempo mapping dropped a segment");
        Assert(mapped[1].Beat is { Bar: 57, Numerator: 1, Denominator: 2 } &&
               mapped[2].Beat is { Bar: 129, Numerator: 1, Denominator: 2 } &&
               mapped[3].Beat is { Bar: 165, Numerator: 1, Denominator: 2 },
            "piecewise tempo integration did not preserve the expected musical change beats");
    }

    private static void AlignsPositiveParadeStyleBoundariesToOneMusicalPhase()
    {
        const int rate = 100;
        // Deliberately noisy transition regions place the raw acoustic maxima
        // away from their shared musical phase, like the real regression file.
        // The pulses start at 0.17 s, so beat 0 belongs at +170 ms (Delay -170).
        // This fixture used to expect 57.5/129.5/165.5: those half beats came
        // from the old mirrored origin (beat 0 at -170 ms), a grid shift of
        // 340 ms = 0.595 beat at 105 BPM. On the corrected grid every boundary
        // is a whole beat and each segment start equals that beat's time
        // (0.17 + 57 * 60 / 105 = 32.741 s, then +72 beats at 155, +36 at 105).
        const double first = 32.8;
        const double second = 60.5;
        const double third = 81.0;
        var novelty = BuildSyntheticNovelty(rate, 112,
        [
            (0d, 105d),
            (first, 155d),
            (second, 105d),
            (third, 155d)
        ]);

        var estimate = VariableTempoEstimator.Estimate(novelty, rate);
        var mapped = VariableTempoEstimator.MapToTiming(estimate);
        Assert(Math.Abs(estimate.InitialOffsetMilliseconds + 170) < .001,
            $"the first beat at 0.17 s was not reported as Delay -170: {estimate.InitialOffsetMilliseconds}");
        Assert(mapped.Count == 4 &&
               mapped[1].Beat is { Bar: 57, Numerator: 0 } &&
               mapped[2].Beat is { Bar: 129, Numerator: 0 } &&
               mapped[3].Beat is { Bar: 165, Numerator: 0 },
            "Positive Parade-style noisy boundaries did not retain one shared musical phase: " +
            string.Join(", ", mapped.Select(candidate => candidate.Beat)) +
            $"; offset={estimate.InitialOffsetMilliseconds}; starts=" +
            string.Join(",", estimate.Segments.Select(segment => segment.StartSeconds)));
    }

    private static void PreservesExactComplexBoundarySubdivision()
    {
        const double delayMilliseconds = 432;
        var complexBeat = BeatPosition.Preserve(57, 17, 24);
        var boundarySeconds = -delayMilliseconds / 1000d + complexBeat.Value * 60d / 105;
        var estimate = new VariableTempoEstimate(
        [
            new TempoSegmentEstimate(0, 105, .9, 10),
            new TempoSegmentEstimate(boundarySeconds, 155, .9, 10)
        ], [], delayMilliseconds, 90);

        var mapped = VariableTempoEstimator.MapToTiming(estimate);
        Assert(mapped[1].Beat is { Bar: 57, Numerator: 17, Denominator: 24 },
            $"a supported 17/24 boundary was pulled onto a simpler grid: {mapped[1].Beat}");
    }

    private static void ReplacesOnlyBpmDataAndPreservesEffects()
    {
        var effectOnly = new TimingEvent
        {
            Beat = new BeatPosition(4, 0, 1), Effect = TimingEffectKind.Scroll,
            EffectValue = 1.25, EffectValueText = "1.25"
        };
        var combined = new TimingEvent
        {
            Beat = new BeatPosition(8, 0, 1), Bpm = 140, Delay = 12,
            Effect = TimingEffectKind.Jump, EffectValue = 2, EffectValueText = "2"
        };
        var document = new ChartDocument();
        document.Timing.Add(new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120, Delay = 300 });
        document.Timing.Add(effectOnly);
        document.Timing.Add(combined);
        var originalBpmCount = document.Timing.Count(timing => timing.Bpm.HasValue);
        var candidates = new[]
        {
            new TempoTimingCandidate(BeatPosition.Zero, 105, 0, .8),
            new TempoTimingCandidate(new BeatPosition(57, 1, 2), 155, 32.4, .8)
        };

        var removed = TempoTimelineApplicator.ReplaceBpmEvents(document, candidates, 432);
        Assert(removed == originalBpmCount && document.Timing.Count(timing => timing.Bpm.HasValue) == 2,
            "BPM replacement did not replace the complete previous BPM timeline");
        Assert(document.Timing.Any(timing => timing.Id == effectOnly.Id && timing.Effect == TimingEffectKind.Scroll) &&
               document.Timing.Any(timing => timing.Id == combined.Id && timing.Effect == TimingEffectKind.Jump && !timing.Bpm.HasValue),
            "BPM replacement deleted or rewrote non-BPM Effect data");
        var first = document.Timing.First(timing => timing.Bpm.HasValue && timing.Beat == BeatPosition.Zero);
        Assert(Math.Abs(first.Delay - 432) < 1e-9, "predicted initial offset was not applied to the first BPM event");
    }

    private static void GeneratesDeterministicScrollCompensation()
    {
        var alternateExpression = BeatPosition.Preserve(4, 3, 6);
        var equivalentReducedBeat = new BeatPosition(4, 1, 2);
        var unrelatedScroll = new TimingEvent
        {
            Beat = new BeatPosition(20, 0, 1), Effect = TimingEffectKind.Scroll,
            EffectValue = .75, EffectValueText = ".75"
        };
        var preservedJump = new TimingEvent
        {
            Beat = equivalentReducedBeat, Effect = TimingEffectKind.Jump,
            EffectValue = 2, EffectValueText = "2"
        };
        var document = new ChartDocument
        {
            Metadata = new ChartMetadata { Bpm = 175 },
            Timing =
            [
                new TimingEvent { Beat = BeatPosition.Zero, Bpm = 175 },
                new TimingEvent
                {
                    Beat = equivalentReducedBeat, Bpm = 140,
                    Effect = TimingEffectKind.Scroll, EffectValue = .5, EffectValueText = ".5"
                },
                new TimingEvent
                {
                    Beat = new BeatPosition(8, 0, 1), Bpm = 120,
                    Effect = TimingEffectKind.Scroll, EffectValue = 1.4, EffectValueText = "1.4"
                },
                new TimingEvent
                {
                    Beat = new BeatPosition(8, 0, 1), Effect = TimingEffectKind.Sign,
                    EffectValue = 1, EffectValueText = "1"
                },
                preservedJump,
                unrelatedScroll
            ]
        };
        var candidates = new[]
        {
            new TempoTimingCandidate(BeatPosition.Zero, 175, 0, .9),
            new TempoTimingCandidate(alternateExpression, 140, 24, .9),
            new TempoTimingCandidate(new BeatPosition(12, 0, 1), 155, 48, .9)
        };

        var session = new EditorSession();
        session.ReplaceDocument(document.Clone());
        var before = SemanticTimingSignature(session.Document);
        session.Mutate(chart => TempoTimelineApplicator.ReplaceBpmEvents(chart, candidates, 91.52016, 175));
        var compensated = session.Document;
        foreach (var candidate in candidates)
        {
            var matching = compensated.Timing.Where(timing =>
                timing.Beat.CompareTo(candidate.Beat) == 0 && timing.Bpm == candidate.Bpm).ToArray();
            Assert(matching.Length == 1 && matching[0].Effect == TimingEffectKind.Scroll,
                $"candidate {candidate.Beat} did not receive one combined BPM/Scroll event");
            var expected = 175d / candidate.Bpm;
            Assert(matching[0].EffectValue == expected &&
                   matching[0].EffectValueText == expected.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                $"candidate {candidate.Beat} used a rounded or incorrect compensation ratio");
        }
        var authoredExpression = compensated.Timing.Single(timing =>
            timing.Bpm == 140 && timing.Effect == TimingEffectKind.Scroll).Beat;
        Assert(authoredExpression is { Numerator: 3, Denominator: 6 },
            "an equivalent 3/6 candidate was reduced instead of retaining its authored subdivision");
        Assert(compensated.Timing.Any(timing => timing.Id == preservedJump.Id && timing.Effect == TimingEffectKind.Jump),
            "a non-Scroll effect at a managed beat was removed");
        Assert(compensated.Timing.Any(timing => timing.Id == unrelatedScroll.Id &&
                                                timing.Effect == TimingEffectKind.Scroll && timing.EffectValue == .75),
            "a Scroll outside the BPM boundary range was changed");
        Assert(!compensated.Timing.Any(timing => timing.Beat.CompareTo(new BeatPosition(8, 0, 1)) == 0 &&
                                                  timing.Effect == TimingEffectKind.Scroll),
            "obsolete compensation remained at an old BPM boundary");

        var firstPass = SemanticTimingSignature(compensated);
        session.Mutate(chart => TempoTimelineApplicator.ReplaceBpmEvents(chart, candidates, 91.52016, 175));
        Assert(SemanticTimingSignature(session.Document) == firstPass,
            "reapplying compensation accumulated or reordered semantic Timing data");
        Assert(session.Undo() && SemanticTimingSignature(session.Document) == firstPass &&
               session.Undo() && SemanticTimingSignature(session.Document) == before,
            "BPM and Scroll compensation were not reversible as one mutation");

        var roundTrip = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(compensated));
        var precise = roundTrip.Timing.Single(timing =>
            timing.Beat.CompareTo(new BeatPosition(12, 0, 1)) == 0 && timing.Effect == TimingEffectKind.Scroll);
        Assert(precise.EffectValue == 175d / 155d &&
               precise.EffectValueText == (175d / 155d).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "compensation ratio lost precision during MC save/reopen");

        var unorderedDocument = new ChartDocument();
        unorderedDocument.Timing.Add(new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 });
        TempoTimelineApplicator.ReplaceBpmEvents(unorderedDocument,
        [
            new TempoTimingCandidate(new BeatPosition(4, 0, 1), 140, 2, .8),
            new TempoTimingCandidate(BeatPosition.Zero, 175, 0, .9)
        ], 432, 175);
        var orderedBpm = unorderedDocument.Timing.Where(timing => timing.Bpm.HasValue).ToArray();
        Assert(orderedBpm.Length == 2 && orderedBpm[0].Beat.CompareTo(BeatPosition.Zero) == 0 &&
               orderedBpm[0].Delay == 432 && orderedBpm[1].Beat.CompareTo(new BeatPosition(4, 0, 1)) == 0 &&
               orderedBpm[1].Delay == 0 && orderedBpm[0].EffectValue == 1 &&
               orderedBpm[1].EffectValue == 1.25,
            "unordered BPM candidates assigned offset or compensation by caller order instead of musical beat order");

        var unchanged = SemanticTimingSignature(document);
        AssertThrows<ArgumentOutOfRangeException>(() =>
            TempoTimelineApplicator.ReplaceBpmEvents(document, candidates, 0, double.NaN));
        Assert(SemanticTimingSignature(document) == unchanged,
            "invalid reference BPM partially modified the document");
        AssertThrows<ArgumentOutOfRangeException>(() =>
            TempoTimelineApplicator.ReplaceBpmEvents(document,
                [new TempoTimingCandidate(BeatPosition.Zero, 60, 0, .9)], 0, 10_000));
        Assert(SemanticTimingSignature(document) == unchanged,
            "an out-of-range compensation ratio partially modified the document");
        AssertThrows<ArgumentException>(() =>
            TempoTimelineApplicator.ReplaceBpmEvents(document,
            [
                new TempoTimingCandidate(BeatPosition.Preserve(1, 3, 6), 140, 0, .9),
                new TempoTimingCandidate(new BeatPosition(1, 1, 2), 155, 1, .9)
            ], 0, 175));
        Assert(SemanticTimingSignature(document) == unchanged,
            "duplicate rational candidate beats partially modified the document");
    }

    private static string SemanticTimingSignature(ChartDocument document) => string.Join("|",
        document.Timing.OrderBy(timing => timing.Beat).ThenBy(timing => timing.Bpm.HasValue ? 0 : 1).Select(timing =>
            $"{timing.Beat.Bar}:{timing.Beat.Numerator}/{timing.Beat.Denominator};" +
            $"{timing.Bpm?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "-"};" +
            $"{timing.Delay.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};" +
            $"{timing.Effect?.ToString() ?? "-"};" +
            $"{timing.EffectValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};{timing.EffectValueText}"));

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            throw new InvalidOperationException($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private static double[] BuildSyntheticNovelty(int rate, double duration,
        IReadOnlyList<(double Start, double Bpm)> segments)
    {
        var result = new double[(int)Math.Ceiling(duration * rate)];
        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            var end = segmentIndex + 1 < segments.Count ? segments[segmentIndex + 1].Start : duration;
            var period = 60 / segment.Bpm;
            for (var time = segment.Start + .17; time < end; time += period)
            {
                AddPulse(result, time * rate, 1);
                AddPulse(result, (time + period / 2) * rate, .24);
            }
        }
        return result;
    }

    private static double[] BuildWideNoisyNovelty(int rate, double duration, double bpm, int seed)
    {
        var result = new double[(int)Math.Ceiling(duration * rate)];
        var random = new Random(seed);
        for (var index = 0; index < result.Length; index++) result[index] = random.NextDouble() * .0015;

        var period = 60 / bpm;
        var beatIndex = 0;
        for (var time = .23; time < duration; time += period)
        {
            var jitterFrames = random.NextDouble() * 1.5 - .75;
            var strength = .82 + random.NextDouble() * .28;
            AddWidePulse(result, time * rate + jitterFrames, strength);
            AddWidePulse(result, (time + period / 2) * rate + jitterFrames * .4, strength * .2);
            if (++beatIndex % 4 == 0)
                AddWidePulse(result, (time + period / 4) * rate, strength * .08);
        }
        return result;
    }

    private static void AddWidePulse(double[] novelty, double frame, double strength)
    {
        var center = (int)Math.Round(frame);
        for (var offset = -7; offset <= 7; offset++)
        {
            var index = center + offset;
            if (index < 0 || index >= novelty.Length) continue;
            var weight = Math.Exp(-(offset * offset) / (2 * 2.4 * 2.4));
            novelty[index] += strength * weight;
        }
    }

    private static void AddWidePulseTrain(double[] novelty, int rate, double bpm, double strength)
    {
        var period = 60 / bpm;
        for (var time = .31; time < novelty.Length / (double)rate; time += period)
            AddWidePulse(novelty, time * rate, strength);
    }

    private static void AddPulse(double[] novelty, double frame, double strength)
    {
        var center = (int)Math.Round(frame);
        if (center < 1 || center >= novelty.Length - 1) return;
        novelty[center - 1] = Math.Max(novelty[center - 1], strength * .35);
        novelty[center] = Math.Max(novelty[center], strength);
        novelty[center + 1] = Math.Max(novelty[center + 1], strength * .5);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

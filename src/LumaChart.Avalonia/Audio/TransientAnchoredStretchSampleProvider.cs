using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using SoundTouch.Net.NAudioSupport;

namespace LumaChart.Avalonia.Audio;

/// <summary>
/// WSOLA may move a future attack into an earlier grain. Bound each processor
/// at detected source transients, and budget segment output on one absolute
/// sample timeline. No processor can read across the next attack boundary.
/// Segment joins are raised-cosine fades: an abrupt budget cut or a transient
/// SoundTouch empty read used to leave a full-scale step in the waveform, which
/// is audible as crackle at every note even when the pitch is preserved.
/// </summary>
internal sealed class TransientAnchoredStretchSampleProvider : ISampleProvider
{
    // A processor needs sequence + overlap input before it can emit a stable
    // grain. Cutting a shorter segment leaves mostly flush padding, so keep
    // detected boundaries at least this far apart in source time.
    private const int MinimumSegmentMilliseconds = 80;
    // SoundTouch reports a transient empty read while it drains its final
    // overlap. Retry boundedly before treating the segment as exhausted.
    private const int MaximumEmptyReadsPerSegment = 4096;
    private const int FadeMilliseconds = 3;
    // Attack segments start at full gain, but a one millisecond ramp removes the
    // half-sample step where a cut segment's digital-silence tail meets the
    // attack's first sample without audibly softening the attack.
    private const int AttackFadeMilliseconds = 1;

    private readonly SegmentedSource input;
    private readonly double rate;
    private readonly int channels;
    private readonly long totalFrames;
    private readonly int fadeFrames;
    private readonly int attackFadeFrames;
    private BoundedSoundTouchSampleProvider? processor;
    private Segment? segment;
    private ISampleProvider? processed;
    private long producedFrames;
    private long segmentFrames;
    private float[] scratch = [];
    private float[] lastFrame = [];
    // Observed per-run behaviour. The suite reports these so a regression in
    // segment count or flushed padding is visible instead of being inferred
    // from the waveform alone.
    private int segmentCount;
    private long paddedFrames;
    private int emptyReadRetries;
    private readonly List<SegmentTrace> traces = [];
    private int tailFrames;
    private bool tailActive;
    // When the processor runs out of real audio it emits digital zeros before
    // the output budget ends. Fade the last audible sample into that silence
    // instead of stepping from a moving waveform to zero (measured 0.196 on
    // bed material at every join).
    private int silenceFadeRemaining;
    private int fadeInRemaining;
    private int fadeInTotal;
    private bool fadeInPending;
    private bool ended;
    public WaveFormat WaveFormat => input.WaveFormat;
    public bool IsEnded => ended || producedFrames >= totalFrames;
    public int ObservedSegmentCount => segmentCount;
    public double ObservedPaddedMilliseconds => paddedFrames * 1000d / input.WaveFormat.SampleRate;
    public int ObservedEmptyReadRetries => emptyReadRetries;
    public IReadOnlyList<SegmentTrace> ObservedTraces => traces;
    public readonly record struct SegmentTrace(long SourceStart, long OutputStart, long OutputFrames, long OutputEnd);
    public TransientAnchoredStretchSampleProvider(ISampleProvider source, long sourceSamples, double rate, Func<bool> sourceEnded)
    {
        input = new SegmentedSource(source, sourceEnded); this.rate = rate; channels = source.WaveFormat.Channels;
        totalFrames = (long)Math.Round(sourceSamples / (channels * rate));
        fadeFrames = Math.Max(1, source.WaveFormat.SampleRate * FadeMilliseconds / 1000);
        attackFadeFrames = Math.Max(1, source.WaveFormat.SampleRate * AttackFadeMilliseconds / 2000);
        lastFrame = new float[channels];
    }
    private static float Fade(double position) => (float)(.5 - .5 * Math.Cos(Math.PI * Math.Clamp(position, 0, 1)));
    public int Read(float[] buffer, int offset, int count)
    {
        count -= count % channels; var written = 0;
        if (scratch.Length < count) scratch = new float[count];
        while (written < count && !IsEnded)
        {
            if (processor == null)
            {
                segment = input.Next(); if (segment == null) { ended = true; break; }
                segmentCount++;
                if (traces.Count < 64) traces.Add(new SegmentTrace(segment.StartFrame, producedFrames, 0, -1));
                var engine = new SoundTouchProcessor();
                engine.SampleRate = input.WaveFormat.SampleRate;
                engine.Channels = input.WaveFormat.Channels;
                WindowsPitchPreservingTimeStretchPolicy.Configure(engine);
                engine.Tempo = (float)rate; engine.Pitch = 1; engine.Rate = 1;
                processor = new BoundedSoundTouchSampleProvider(segment, engine, rate);
                processed = processor;
                segmentFrames = 0; tailFrames = fadeFrames; tailActive = false;
                // A segment that begins on a detected attack must not fade its
                // attack in: the previous segment already faded out into this
                // boundary, so a 3 ms fade-in only made every attack quieter and
                // shorter than the source. Releases still fade in (they begin in
                // silence, so it is inaudible either way).
                fadeInTotal = segment.StartsAtAttack ? attackFadeFrames : fadeFrames;
                fadeInRemaining = fadeInTotal;
                fadeInPending = !segment.StartsAtAttack;

            }
            if (producedFrames >= totalFrames) { ended = true; break; }
            var requested = (int)Math.Min((count - written) / channels, totalFrames - producedFrames) * channels;
            if (requested <= 0) break;
            var read = 0;
            for (var attempt = 0; attempt < MaximumEmptyReadsPerSegment; attempt++)
            {
                read = processed!.Read(scratch, 0, requested);
                if (read > 0) break;
                emptyReadRetries++;
            }
            var end = segment!.EndFrame.HasValue ? (long)Math.Round(segment.EndFrame.Value / rate) : long.MaxValue;
            var hasEnd = segment.EndFrame.HasValue;
            var frames = (int)Math.Min(Math.Max(0, end - producedFrames), read / channels);
            var padding = false;
            if (frames == 0 && hasEnd)
            {
                frames = (int)Math.Min((count - written) / channels, Math.Max(0, end - producedFrames));
                padding = true;
            }
            if (frames == 0)
            {
                if (hasEnd && producedFrames >= end) { processor = null; segment = null; processed = null; continue; }
                if (read == 0) break;
            }
            if (padding && !tailActive) { tailActive = true; tailFrames = fadeFrames; }
            if (padding) paddedFrames += frames;
            // Real samples resumed after a temporary stall: the next padding must
            // start a fresh decay from the new last frame, not from a spent tail.
            if (!padding) tailActive = false;
            for (var f = 0; f < frames; f++)
            {
                if (padding)
                {
                    // SoundTouch can stop short of the segment budget (its flush
                    // is shorter than the theoretical tempo ratio). Zero filling
                    // the remainder used to step straight from the last real
                    // sample to silence; decay that sample over the same 3ms.
                    var tailGain = tailFrames > 0 ? Fade(tailFrames / (double)fadeFrames) : 0f;
                    if (tailFrames > 0) tailFrames--;
                    for (var c = 0; c < channels; c++)
                        buffer[offset + written + f * channels + c] = lastFrame[c] * tailGain;
                    continue;
                }
                // SoundTouch can emit leading silence before its first grain. Keep
                // the fade pending until real audio starts so the attack cannot
                // appear at full scale out of nowhere.
                if (fadeInPending)
                {
                    var silent = true;
                    for (var c = 0; c < channels; c++) if (Math.Abs(scratch[f * channels + c]) > 1e-6f) { silent = false; break; }
                    if (!silent) fadeInPending = false;
                }
                var inGain = fadeInPending ? 0f
                    : fadeInRemaining > 0 ? Fade(1 - fadeInRemaining / (double)fadeInTotal) : 1f;
                if (!fadeInPending && fadeInRemaining > 0) fadeInRemaining--;
                var outGain = 1f;
                if (hasEnd)
                {
                    var remaining = end - (producedFrames + f);
                    if (remaining < fadeFrames) outGain = Fade(remaining / (double)fadeFrames);
                }
                var gain = inGain * outGain;
                var silentFrame = true;
                for (var c = 0; c < channels; c++)
                {
                    var value = scratch[f * channels + c] * gain;
                    if (Math.Abs(value) > 1e-9f) silentFrame = false;
                }
                if (silentFrame && silenceFadeRemaining > 0)
                {
                    var silenceGain = Fade(silenceFadeRemaining / (double)fadeFrames);
                    silenceFadeRemaining--;
                    for (var c = 0; c < channels; c++)
                        buffer[offset + written + f * channels + c] = lastFrame[c] * silenceGain;
                    continue;
                }
                if (!silentFrame) silenceFadeRemaining = fadeFrames;
                else silenceFadeRemaining = 0;
                for (var c = 0; c < channels; c++)
                {
                    var value = scratch[f * channels + c] * gain;
                    buffer[offset + written + f * channels + c] = value;
                    if (Math.Abs(value) > 1e-9f) lastFrame[c] = value;
                }
            }
            segmentFrames += frames; producedFrames += frames; written += frames * channels;
            if (hasEnd && producedFrames >= end)
            {
                if (traces.Count > 0) traces[^1] = traces[^1] with { OutputFrames = producedFrames - traces[^1].OutputStart, OutputEnd = end };
                processor = null; segment = null; processed = null;
            }
        }
        return written;
    }

    private sealed class Segment(SegmentedSource owner, long start, bool startsAtAttack) : ISampleProvider
    {
        // A slow rate needs more output than a short segment's input can carry:
        // at 0.1x a 50 ms burst must fill 500 ms of budget, but the processor
        // consumes its first 24 ms as grain warm-up. Feed the segment's own last
        // 24 ms back as a cross-faded loop instead of stopping: the tail keeps
        // sounding and repeats in phase, which is exactly what the old plain
        // 64 ms replay failed to do (it restarted at an arbitrary phase and let
        // SoundTouch re-seek into the attack).
        private const int LoopMilliseconds = 24;
        // Only segments too short for the processor's 24 ms warm-up need it:
        // longer segments lose a few percent of their tail to the same limit,
        // but looping a long segment's decaying tail would repeat a note body.
        private const int LoopMaximumSegmentMilliseconds = 100;
        private const int LoopCrossfadeMilliseconds = 4;
        private readonly float[] history = new float[Math.Max(1, owner.WaveFormat.SampleRate * LoopMilliseconds / 1000) * owner.WaveFormat.Channels];
        private readonly int crossfadeFrames = Math.Max(1, owner.WaveFormat.SampleRate * LoopCrossfadeMilliseconds / 1000);
        private int stored, cursor;
        private int loopPhase;
        private long loopFrames;
        private long loopLimitFrames = -1;
        public long StartFrame { get; } = start;
        /// <summary>The boundary that opened this segment was a detected attack.</summary>
        public bool StartsAtAttack { get; } = startsAtAttack;
        public long? EndFrame { get; set; }
        public WaveFormat WaveFormat => owner.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            if (!EndFrame.HasValue)
            {
                var read = owner.ReadSegment(this, buffer, offset, count);
                for (var i = 0; i < read; i++)
                {
                    history[cursor] = buffer[offset + i];
                    cursor = (cursor + 1) % history.Length;
                    stored = Math.Min(history.Length, stored + 1);
                }
                return read;
            }
            count -= count % WaveFormat.Channels;
            if (stored == 0 || count <= 0) return 0;
            var segmentFrames = EndFrame.Value - StartFrame;
            if (segmentFrames > WaveFormat.SampleRate * LoopMaximumSegmentMilliseconds / 1000) return 0;
            var loopLength = stored / WaveFormat.Channels;
            if (loopLimitFrames < 0) loopLimitFrames = Math.Max(0, segmentFrames);
            var remaining = loopLimitFrames - loopFrames;
            if (remaining <= 0) return 0;
            var frames = (int)Math.Min(count / WaveFormat.Channels, remaining);
            var first = stored == history.Length ? cursor : 0;
            var steady = Math.Max(1, loopLength - crossfadeFrames);
            for (var f = 0; f < frames; f++)
            {
                var phase = loopPhase;
                for (var c = 0; c < WaveFormat.Channels; c++)
                {
                    var head = history[(first + phase * WaveFormat.Channels + c) % history.Length];
                    if (phase >= steady)
                    {
                        var blend = (phase - steady) / (double)crossfadeFrames;
                        var tail = history[(first + (phase - steady) * WaveFormat.Channels + c) % history.Length];
                        head = (float)(head * (1 - blend) + tail * blend);
                    }
                    buffer[offset + f * WaveFormat.Channels + c] = head;
                }
                loopPhase = phase + 1 >= loopLength ? crossfadeFrames : phase + 1;
            }
            loopFrames += frames;
            return frames * WaveFormat.Channels;
        }
    }
    /// <summary>A source split point. Attacks and releases need different
    /// treatment: an attack starts its next segment at full gain, a release
    /// starts one in silence.</summary>
    private readonly record struct Boundary(long Frame, bool IsAttack);

    private sealed class SegmentedSource
    {
        private readonly ISampleProvider source;
        private readonly Func<bool> sourceEnded;
        private readonly int channels, bucketFrames, minimumSegmentFrames;
        private readonly float[] buffer;
        // Attacks are detected on a 5 ms energy window against a slow baseline.
        // A 1 ms window re-triggered on a steady chord (interference nulls) and
        // split it every 80-120 ms, and each restart destroyed the sustained
        // phase (the measured 220 Hz component fell from .22 to .01); a 5 ms
        // window varies far less on beating and still sees an onset.
        private readonly double[] low, mid, baseline = new double[3];
        // Attack must also be a step up from the recent background: a steady
        // chord with beating varies its 1 ms energy but its 50 ms level stays
        // put, and the 0.1.37 baseline test cut it every ~120 ms.
        private const int BackgroundBuckets = 200;
        private const double AttackBackgroundRatio = 8;
        private double[] bandEnergy = [];
        private float[] bucketPeak = [];
        private readonly double[] levelHistory = new double[BackgroundBuckets];
        private int levelHistoryIndex;
        private double levelHistorySum;
        private int levelHistoryCount;
        private readonly double lowAlpha, midAlpha;
        private readonly Queue<Boundary> boundaries = [];
        private int available, consumed;
        private long bufferStart, readFrames, lastBoundary;
        private bool ended;
        private bool previousBucketAudible;
        private bool boundaryOpenedAtAttack;
        public WaveFormat WaveFormat => source.WaveFormat;
        public SegmentedSource(ISampleProvider source, Func<bool> sourceEnded)
        {
            this.source = source; this.sourceEnded = sourceEnded; channels = source.WaveFormat.Channels; bucketFrames = Math.Max(1, source.WaveFormat.SampleRate / 1000);
            buffer = new float[bucketFrames * channels * 100]; low = new double[channels]; mid = new double[channels];
            minimumSegmentFrames = Math.Max(bucketFrames * 8, source.WaveFormat.SampleRate * MinimumSegmentMilliseconds / 1000);
            lowAlpha = 1 - Math.Exp(-2 * Math.PI * 180 / source.WaveFormat.SampleRate); midAlpha = 1 - Math.Exp(-2 * Math.PI * 1500 / source.WaveFormat.SampleRate);
        }
        public Segment? Next() => ended && consumed >= available ? null : new Segment(this, readFrames, boundaryOpenedAtAttack);
        public int ReadSegment(Segment segment, float[] output, int offset, int count)
        {
            if (segment.EndFrame.HasValue) return 0;
            var written = 0; count -= count % channels;
            while (written < count)
            {
                if (boundaries.TryPeek(out var boundary) && readFrames >= boundary.Frame)
                { boundaryOpenedAtAttack = boundaries.Dequeue().IsAttack; segment.EndFrame = boundary.Frame; break; }
                if (consumed >= available)
                {
                    if (ended) { segment.EndFrame = readFrames; break; }
                    Fill(); if (available == 0) { if (ended) segment.EndFrame = readFrames; break; }
                    continue;
                }
                var n = Math.Min(count - written, available - consumed);
                if (boundaries.TryPeek(out boundary)) n = (int)Math.Min(n, Math.Max(0, boundary.Frame - readFrames) * channels);
                if (n == 0) continue;
                Buffer.BlockCopy(buffer, consumed * 4, output, (offset + written) * 4, n * 4);
                consumed += n; written += n; readFrames += n / channels;
            }
            if (boundaries.TryPeek(out var finalBoundary) && readFrames >= finalBoundary.Frame)
            { boundaryOpenedAtAttack = boundaries.Dequeue().IsAttack; segment.EndFrame = finalBoundary.Frame; }
            else if (ended && consumed >= available) segment.EndFrame = readFrames;
            return written;
        }
        private void Fill()
        {
            bufferStart = readFrames; available = 0; consumed = 0;
            while (available < buffer.Length)
            {
                var n = source.Read(buffer, available, buffer.Length - available); if (n <= 0) { ended = sourceEnded(); break; }
                available += n;
            }
            var bucketCount = available / channels / bucketFrames;
            if (bucketCount <= 0) return;
            if (bandEnergy.Length < bucketCount * 3) bandEnergy = new double[bucketCount * 3];
            if (bucketPeak.Length < bucketCount) bucketPeak = new float[bucketCount];
            for (var index = 0; index < bucketCount; index++)
            {
                var first = index * bucketFrames;
                var frames = Math.Min(bucketFrames, available / channels - first);
                double lowEnergy = 0, midEnergy = 0, highEnergy = 0;
                var peak = 0f;
                for (var f = 0; f < frames; f++)
                    for (var c = 0; c < channels; c++)
                    {
                        var x = buffer[(first + f) * channels + c];
                        low[c] += lowAlpha * (x - low[c]);
                        mid[c] += midAlpha * (x - mid[c]);
                        peak = Math.Max(peak, Math.Abs(x));
                        lowEnergy += low[c] * low[c];
                        midEnergy += (mid[c] - low[c]) * (mid[c] - low[c]);
                        highEnergy += (x - mid[c]) * (x - mid[c]);
                    }
                var scale = 1.0 / Math.Max(1, frames * channels);
                bandEnergy[index * 3] = lowEnergy * scale;
                bandEnergy[index * 3 + 1] = midEnergy * scale;
                bandEnergy[index * 3 + 2] = highEnergy * scale;
                bucketPeak[index] = peak;
            }
            // Second pass: candidates come from the same baseline test as 0.1.37,
            // so the split position does not move. The extra check looks *forward*
            // 5 ms (the buffer is already available) and compares that level with
            // the 50 ms before the candidate: a steady beaten chord keeps both
            // levels equal and is rejected, a drum or a new note over a quieter
            // bed steps up several times and is accepted.
            for (var index = 0; index < bucketCount; index++)
            {
                var absolute = bufferStart + index * bucketFrames;
                var attack = false;
                var bucketLevel = bandEnergy[index * 3] + bandEnergy[index * 3 + 1] + bandEnergy[index * 3 + 2];
                for (var band = 0; band < 3; band++)
                {
                    var energy = bandEnergy[index * 3 + band];
                    attack |= energy > 1e-8 && energy > baseline[band] * 4 && Math.Sqrt(energy) - Math.Sqrt(baseline[band]) > .0001;
                    baseline[band] = baseline[band] * .95 + energy * .05;
                }
                if (attack)
                {
                    var future = 0.0;
                    for (var slot = 0; slot < 5 && index + slot < bucketCount; slot++)
                        future += bandEnergy[(index + slot) * 3] + bandEnergy[(index + slot) * 3 + 1] + bandEnergy[(index + slot) * 3 + 2];
                    // Compare the 5 ms level with the 200 ms median-ish mean:
                    // a beaten chord's instantaneous level swings 3000x at 1 ms
                    // and ~10x at 5 ms, but its 200 ms level does not move at all,
                    // so only a genuine step up from a quieter background is cut.
                    var background = levelHistoryCount == 0 ? 0 : levelHistorySum / levelHistoryCount;
                    attack = future / Math.Max(1, Math.Min(5, bucketCount - index)) > Math.Max(background * AttackBackgroundRatio, 1e-8);
                }
                levelHistory[levelHistoryIndex] = bucketLevel;
                levelHistoryIndex = (levelHistoryIndex + 1) % BackgroundBuckets;
                levelHistorySum += bucketLevel;
                if (levelHistoryCount < BackgroundBuckets) levelHistoryCount++;
                else levelHistorySum -= levelHistory[levelHistoryIndex];
                var peak = bucketPeak[index];
                var audible = peak >= .001f;
                var release = previousBucketAudible && !audible;
                previousBucketAudible = audible;
                var gap = release ? bucketFrames * 2 : minimumSegmentFrames;
                if ((attack || release) && absolute - lastBoundary >= gap)
                { boundaries.Enqueue(new Boundary(absolute, attack)); lastBoundary = absolute; }
            }
        }
    }
}
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

    private readonly SegmentedSource input;
    private readonly double rate;
    private readonly int channels;
    private readonly long totalFrames;
    private readonly int fadeFrames;
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
    private int fadeInRemaining;
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
                fadeInRemaining = segment.StartsAtAttack ? 0 : fadeFrames;
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
                    : fadeInRemaining > 0 ? Fade(1 - fadeInRemaining / (double)fadeFrames) : 1f;
                if (!fadeInPending && fadeInRemaining > 0) fadeInRemaining--;
                var outGain = 1f;
                if (hasEnd)
                {
                    var remaining = end - (producedFrames + f);
                    if (remaining < fadeFrames) outGain = Fade(remaining / (double)fadeFrames);
                }
                var gain = inGain * outGain;
                for (var c = 0; c < channels; c++)
                {
                    var value = scratch[f * channels + c] * gain;
                    buffer[offset + written + f * channels + c] = value;
                    lastFrame[c] = value;
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
        private readonly float[] history = new float[Math.Max(1, owner.WaveFormat.SampleRate * 64 / 1000) * owner.WaveFormat.Channels];
        private int stored, cursor, padding;
        public long StartFrame { get; } = start;
        /// <summary>The boundary that opened this segment was a detected attack.</summary>
        public bool StartsAtAttack { get; } = startsAtAttack;
        public long? EndFrame { get; set; }
        public WaveFormat WaveFormat => owner.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            var read = EndFrame.HasValue ? 0 : owner.ReadSegment(this, buffer, offset, count);
            for (var i = 0; i < read; i++)
            {
                history[cursor] = buffer[offset + i];
                cursor = (cursor + 1) % history.Length;
                stored = Math.Min(history.Length, stored + 1);
            }
            // SoundTouch's flush pads with zeroes. At extreme slow rates that
            // padding can enter valid output before the segment's final frame.
            // Supply bounded past-only context instead; the output budget still
            // stops precisely at EndFrame and never includes the next attack.
            if (EndFrame.HasValue && stored > 0)
            {
                var limit = WaveFormat.SampleRate * WaveFormat.Channels * 128 / 1000;
                var extra = Math.Min(count - read, Math.Max(0, limit - padding));
                var first = stored == history.Length ? cursor : 0;
                for (var i = 0; i < extra; i++)
                    buffer[offset + read + i] = history[(first + (padding + i) % stored) % history.Length];
                padding += extra;
                read += extra;
            }
            return read;
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
        // An attack is a one-bucket energy jump, not "loud versus a slow
        // average": the old baseline test split a steady chord with beating
        // every ~120 ms, and each processor restart destroyed the sustained
        // phase (the measured 220 Hz component fell from .22 to .01).
        private const double AttackEnergyRatio = 3;
        private readonly double[] low, mid;
        private readonly double[] previousBandEnergy = new double[3];
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
            Span<double> energy = stackalloc double[3];
            for (var first = 0; first < available / channels; first += bucketFrames)
            {
                energy.Clear();
                var frames = Math.Min(bucketFrames, available / channels - first);
                var peak = 0f;
                for (var f = 0; f < frames; f++) for (var c = 0; c < channels; c++)
                {
                    var x = buffer[(first + f) * channels + c]; low[c] += lowAlpha * (x - low[c]); mid[c] += midAlpha * (x - mid[c]);
                    peak = Math.Max(peak, Math.Abs(x));
                    energy[0] += low[c] * low[c]; energy[1] += (mid[c] - low[c]) * (mid[c] - low[c]); energy[2] += (x - mid[c]) * (x - mid[c]);
                }
                var absolute = bufferStart + first; var attack = false;
                for (var band = 0; band < 3; band++)
                {
                    var current = energy[band] / Math.Max(1, frames * channels);
                    var previous = previousBandEnergy[band];
                    var level = Math.Sqrt(current);
                    // A real attack rises within one millisecond bucket. The old
                    // test compared with a 20 ms baseline, so a steadily beaten
                    // chord looked like a string of attacks.
                    attack |= current > 1e-8 &&
                        (previous <= 1e-12
                            ? level > .0001
                            : current > previous * AttackEnergyRatio && level - Math.Sqrt(previous) > .0001);
                    previousBandEnergy[band] = current;
                }
                // A silent release is also a boundary: otherwise WSOLA can borrow
                // the following quiet region and truncate the still-audible tail.
                // Boundaries closer than one processor warm-up are coalesced: a
                // segment that short cannot emit anything but flush padding, and
                // the repeated transient detection split every attack into an
                // 8ms segment which was then zero-filled (audible clicks).
                var audible = peak >= .001f;
                var release = previousBucketAudible && !audible;
                previousBucketAudible = audible;
                // Attack boundaries need a processor warm-up of source behind
                // them; a repeated transient detection used to split every attack
                // into an 8ms segment. Releases still cut immediately so a short
                // burst is never merged with the following silence.
                var gap = release ? bucketFrames * 2 : minimumSegmentFrames;
                if ((attack || release) && absolute - lastBoundary >= gap)
                { boundaries.Enqueue(new Boundary(absolute, attack)); lastBoundary = absolute; }
            }
        }
    }
}
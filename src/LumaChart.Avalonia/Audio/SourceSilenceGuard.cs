using NAudio.Wave;

namespace LumaChart.Avalonia.Audio;

/// <summary>
/// WSOLA can repeat an attack into preceding source silence. Keep quiet gaps on
/// the original PCM timeline, independently of grain search and decoder reads.
/// Only source gaps below -60dB are constrained; this is not loudness normalisation.
/// Storage is bounded and both stereo channels always receive the same gain.
/// </summary>
internal sealed class SourceSilenceGuard : ISampleProvider
{
    private readonly ISampleProvider output;
    private readonly RecordingSource input;
    private readonly double rate;
    private readonly double smoothing;
    private readonly double releaseSmoothing;
    private long outputFrames;
    private double gain = 1;
    private double target = 1;

    public SourceSilenceGuard(ISampleProvider output, RecordingSource input, double rate)
    {
        this.output = output; this.input = input; this.rate = rate;
        smoothing = Math.Exp(-1d / (output.WaveFormat.SampleRate * .001));
        // Restoring the original quiet envelope must not step the waveform: a
        // borrowed attack cut to digital zero is a full-scale click at every
        // note. Ramp over a fraction of a millisecond instead.
        releaseSmoothing = Math.Exp(-1d / (output.WaveFormat.SampleRate * ReleaseSeconds));
    }

    public WaveFormat WaveFormat => output.WaveFormat;
    private const double ReleaseSeconds = .0008;
    private double applied = 1;
    public int Read(float[] buffer, int offset, int count)
    {
        var read = output.Read(buffer, offset, count);
        var channels = WaveFormat.Channels;
        for (var sample = 0; sample < read; sample += channels)
        {
            var peak = input.PeakAt((long)Math.Floor(outputFrames++ * rate));
            var outputPeak = 0f;
            for (var channel = 0; channel < channels && sample + channel < read; channel++)
                outputPeak = Math.Max(outputPeak, Math.Abs(buffer[offset + sample + channel]));
            // A bucket the decoder has not reached yet is unknown, not silent:
            // holding the previous decision keeps a strict silence gate without
            // un-muting (a step) when the stretch output briefly runs ahead.
            if (peak is { } known)
            {
                // Source silence always means "attenuate towards zero". The old
                // ratio fell back to 1 whenever the output was also silent, so a
                // borrowed attack that appeared in a silent gap arrived at full
                // scale in one sample.
                target = known >= .001f ? 1 : (outputPeak > 0 ? Math.Min(1d, known / outputPeak) : 0);
                gain = target + (gain - target) * smoothing;
            }
            // Soft decoder pre-ringing must not be amplified into an audible
            // earlier attack by a grain copied from the future. Stay within
            // the original quiet envelope; recover smoothly at the real attack.
            applied += (target - applied) * releaseSmoothing;
            for (var channel = 0; channel < channels && sample + channel < read; channel++)
                buffer[offset + sample + channel] *= (float)applied;
        }
        return read;
    }

    internal sealed class RecordingSource : ISampleProvider
    {
        private const int Capacity = 4096;
        private readonly ISampleProvider source;
        private readonly float[] peaks = new float[Capacity];
        private readonly long[] stamps = Enumerable.Repeat(-1L, Capacity).ToArray();
        private readonly int bucketFrames;
        private long frames;
        public RecordingSource(ISampleProvider source)
        { this.source = source; bucketFrames = Math.Max(1, source.WaveFormat.SampleRate / 1000); }
        public WaveFormat WaveFormat => source.WaveFormat;
        public float? PeakAt(long frame)
        {
            var bucket = frame / bucketFrames;
            var slot = (int)(bucket % Capacity);
            // A future decoder frame cannot be used to infer silence. Stretch
            // providers normally supply lookahead; an unknown bucket reports
            // null so the guard holds its last decision instead of stepping the
            // gain (reporting 0 muted sustained notes, reporting 1 let borrowed
            // audio through and then cut it).
            return stamps[slot] == bucket ? peaks[slot] : null;
        }
        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var channels = WaveFormat.Channels;
            for (var sample = 0; sample < read; sample += channels)
            {
                var bucket = frames++ / bucketFrames;
                var slot = (int)(bucket % Capacity);
                if (stamps[slot] != bucket) { stamps[slot] = bucket; peaks[slot] = 0; }
                for (var channel = 0; channel < channels && sample + channel < read; channel++)
                    peaks[slot] = Math.Max(peaks[slot], Math.Abs(buffer[offset + sample + channel]));
            }
            return read;
        }
    }
}
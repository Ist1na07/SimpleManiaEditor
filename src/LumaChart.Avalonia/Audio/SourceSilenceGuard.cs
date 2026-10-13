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
    private long outputFrames;
    private double gain = 1;

    public SourceSilenceGuard(ISampleProvider output, RecordingSource input, double rate)
    {
        this.output = output; this.input = input; this.rate = rate;
        smoothing = Math.Exp(-1d / (output.WaveFormat.SampleRate * .001));
    }

    public WaveFormat WaveFormat => output.WaveFormat;
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
            var target = peak < .001f && outputPeak > peak ? peak / outputPeak : 1;
            gain = target + (gain - target) * smoothing;
            // Soft decoder pre-ringing must not be amplified into an audible
            // earlier attack by a grain copied from the future. Stay within
            // the original quiet envelope; recover smoothly at the real attack.
            var appliedGain = Math.Min(gain, target);
            for (var channel = 0; channel < channels && sample + channel < read; channel++)
                buffer[offset + sample + channel] *= (float)appliedGain;
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
        public float PeakAt(long frame)
        {
            var bucket = frame / bucketFrames;
            var slot = (int)(bucket % Capacity);
            // A future decoder frame cannot be used to infer silence. Stretch
            // providers normally supply lookahead; missing history is neutral.
            return stamps[slot] == bucket ? peaks[slot] : frame >= frames ? 0 : 1;
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

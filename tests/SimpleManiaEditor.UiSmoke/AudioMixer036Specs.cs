using LumaChart.Avalonia.Audio;
using NAudio.Wave;

internal static class AudioMixer036Specs
{
    public static void Run()
    {
        var music = new ScriptedProvider(4, 40, .2f, 0);
        var underruns = 0;
        var mixer = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(
            music, () => music.Ended, () => underruns++, rate: .5);
        mixer.TryEnqueue(new SourceTimedSampleProvider(new ScriptedProvider(0, 2, 0, .3f), 2, 0));
        var native = new WaveBuffer(48 * sizeof(float)).FloatBuffer;
        Require(mixer.Read(native, 8, 32) == 32 && underruns == 1, "transient decoder stall stopped output or escaped diagnostics");
        for (var i = 8; i < 40; i++) Require(native[i] == 0, "scheduled key advanced through a silent underrun");
        Require(mixer.Read(native, 8, 32) == 32, "music did not recover after decoder stall");
        for (var frame = 0; frame < 16; frame++)
        {
            Require(native[8 + frame * 2] == .2f, "music frames were lost after underrun");
            Require(native[9 + frame * 2] == (frame is >= 4 and < 6 ? .3f : 0), "absolute key delay drifted from real music frames");
        }
        var shortReads = new ScriptedProvider(2, 2, .25f, 0);
        var tailMixer = new WindowsAudioDeviceEngine.QueuedOverlayMixerSampleProvider(shortReads, () => shortReads.Ended);
        Require(tailMixer.Read(native, 8, 32) == 32, "short final PCM block was lost");
        Require(native[8] == .25f && native[10] == .25f && native[12] == 0, "short final PCM was replaced by terminal silence");
        Require(tailMixer.Read(native, 8, 32) == 32 && tailMixer.Read(native, 8, 32) == 0, "terminal silence/EOF contract changed");
        Console.WriteLine("PASS 036 native mixer: transient retry, source/key agreement through underrun, fractional rate, partial tail and terminal silence");
    }

    private sealed class ScriptedProvider(int emptyReads, int frames, float left, float right) : ISampleProvider
    {
        private int calls, position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(1000, 2);
        public bool Ended => position >= frames;
        public int Read(float[] buffer, int offset, int count)
        {
            if (calls++ < emptyReads) return 0;
            var readFrames = Math.Min(count / 2, frames - position);
            for (var i = 0; i < readFrames; i++) { buffer[offset + i * 2] = left; buffer[offset + i * 2 + 1] = right; }
            position += readFrames;
            return readFrames * 2;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

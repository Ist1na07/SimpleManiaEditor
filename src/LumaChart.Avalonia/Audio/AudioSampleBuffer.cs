namespace LumaChart.Avalonia.Audio;

internal static class AudioSampleBuffer
{
    // NAudio WaveBuffer may expose byte[] storage as float[]. Array.Clear uses
    // the runtime byte type, and clears only a quarter of the requested PCM.
    public static void Clear(float[] buffer, int offset, int count)
    {
        for (var index = offset; index < offset + count; index++) buffer[index] = 0;
    }
}

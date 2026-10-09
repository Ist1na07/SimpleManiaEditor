namespace LumaChart.Avalonia.Infrastructure;

/// <summary>Presentation clock following audio without copying device probe jitter.
/// Explicit navigation resets it; it never controls sound or note scheduling.</summary>
public sealed class EditorPlaybackVisualClock
{
    private bool initialized;
    private bool advancing;
    private double previousTime;
    private double previousAudio;
    private double position;
    private double previousRate;
    public void Reset() { initialized = false; advancing = false; }
    public double Sample(double audioMilliseconds, double rate, double nowMilliseconds)
    {
        if (!double.IsFinite(audioMilliseconds) || !double.IsFinite(rate) || rate <= 0 || !double.IsFinite(nowMilliseconds))
        { Reset(); return Math.Max(0, double.IsFinite(audioMilliseconds) ? audioMilliseconds : 0); }
        var elapsed = nowMilliseconds - previousTime;
        if (!initialized || rate != previousRate || elapsed < 0 || elapsed > 250 || Math.Abs(audioMilliseconds - previousAudio) > 150)
        {
            initialized = true; advancing = false; position = audioMilliseconds;
        }
        else if (!advancing && audioMilliseconds <= previousAudio + .001)
            position = audioMilliseconds;
        else
        {
            advancing = true;
            var predicted = position + elapsed * rate;
            // Follow drift at at most 2.5% additional speed. Device clock
            // quantisation is absorbed without stop/go motion or backwards frames.
            var correction = Math.Clamp(audioMilliseconds - predicted, -elapsed * rate * .025, elapsed * rate * .025);
            position = Math.Max(position, predicted + correction);
        }
        previousAudio = audioMilliseconds; previousTime = nowMilliseconds; previousRate = rate;
        return position;
    }
}

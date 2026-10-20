namespace LumaChart.Avalonia.Rendering;

public readonly record struct TimelineEdgeScrollStep(
    double Intensity,
    double ElapsedSeconds,
    double NextBeat)
{
    public bool MovedFrom(double currentBeat) => Math.Abs(NextBeat - currentBeat) >= .000001;
}

/// <summary>
/// Pure edge-scroll timing shared by every captured timeline gesture. Values
/// start strictly outside the viewport: a 42 DIP ramp, up to 4x
/// intensity, 8 beats/second, and at most 50 ms consumed per dispatcher tick.
/// </summary>
public static class TimelineEdgeScroll
{
    public const double EdgeBand = 42;
    public const double BeatsPerSecond = 8;

    public static TimelineEdgeScrollStep Resolve(
        double currentBeat,
        double pointerY,
        double viewportHeight,
        double elapsedSeconds)
    {
        currentBeat = double.IsFinite(currentBeat) ? Math.Max(0, currentBeat) : 0;
        viewportHeight = double.IsFinite(viewportHeight) ? Math.Max(0, viewportHeight) : 0;
        pointerY = double.IsFinite(pointerY) ? pointerY : viewportHeight / 2;
        elapsedSeconds = double.IsFinite(elapsedSeconds)
            ? Math.Clamp(elapsedSeconds, 0, .05)
            : 0;

        double intensity = 0;
        if (pointerY < 0)
            intensity = Math.Min(4, -pointerY / EdgeBand);
        else if (pointerY > viewportHeight)
            intensity = -Math.Min(4, (pointerY - viewportHeight) / EdgeBand);

        var nextBeat = Math.Max(0, currentBeat + intensity * BeatsPerSecond * elapsedSeconds);
        return new TimelineEdgeScrollStep(intensity, elapsedSeconds, nextBeat);
    }
}

namespace LumaChart.Avalonia.Audio;

internal static class WindowsAudioResumePolicy
{
    internal const double MaximumPositionDriftMilliseconds = 50;

    internal static bool RequiresSeek(TimeSpan current, TimeSpan requested)
    {
        var currentMilliseconds = current.TotalMilliseconds;
        var requestedMilliseconds = requested.TotalMilliseconds;
        if (!double.IsFinite(currentMilliseconds) || !double.IsFinite(requestedMilliseconds))
            return true;
        return Math.Abs(currentMilliseconds - requestedMilliseconds) >
               MaximumPositionDriftMilliseconds;
    }
}

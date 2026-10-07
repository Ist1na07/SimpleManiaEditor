namespace LumaChart.Avalonia.Infrastructure;

public static class WindowsTrackTransparencyPolicy
{
    public const string OpaqueTrackColor = "#000000";

    public static double ToFillOpacity(double transparency) =>
        1 - Math.Clamp(double.IsFinite(transparency) ? transparency : 0, 0, 1);

    public static double ToTransparency(double fillOpacity) =>
        1 - Math.Clamp(double.IsFinite(fillOpacity) ? fillOpacity : 1, 0, 1);
}

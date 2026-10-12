namespace LumaChart.Avalonia.Infrastructure;

/// <summary>User display calibration in wall-clock milliseconds; never edits audio or chart timing.</summary>
public static class EditorAudioVisualCalibration
{
    public static double ChartTime(double audioMilliseconds, double rate, int offsetMilliseconds) =>
        audioMilliseconds + Math.Clamp(offsetMilliseconds, -500, 500) * rate;
    public static double AudioTime(double chartMilliseconds, double rate, int offsetMilliseconds) =>
        chartMilliseconds - Math.Clamp(offsetMilliseconds, -500, 500) * rate;
}

namespace LumaChart.Application.Settings;

/// <summary>
/// UI-framework-independent display scaling rules shared by Windows clients.
/// Screen dimensions are effective DIPs after the operating-system DPI scale.
/// </summary>
public static class DisplayScalePolicy
{
    public const double MinimumUiScale = .8;
    public const double MaximumUiScale = 2;
    public const double MinimumFontScale = .8;
    public const double MaximumFontScale = 1.6;

    public static double RecommendUiScale(
        double effectiveScreenWidth,
        double effectiveScreenHeight)
    {
        if (!double.IsFinite(effectiveScreenWidth) ||
            !double.IsFinite(effectiveScreenHeight) ||
            effectiveScreenWidth <= 0 ||
            effectiveScreenHeight <= 0)
        {
            return 1;
        }

        var relative = Math.Min(
            effectiveScreenWidth / 1920d,
            effectiveScreenHeight / 1080d);
        var recommended = Math.Max(
            1,
            Math.Round(relative * 20, MidpointRounding.AwayFromZero) / 20d);
        return NormalizeUiScale(recommended);
    }

    public static double NormalizeUiScale(double value) =>
        Math.Clamp(
            double.IsFinite(value) ? Math.Round(value * 20) / 20d : 1,
            MinimumUiScale,
            MaximumUiScale);

    public static double NormalizeFontScale(double value) =>
        Math.Clamp(
            double.IsFinite(value) ? Math.Round(value * 20) / 20d : 1,
            MinimumFontScale,
            MaximumFontScale);
}

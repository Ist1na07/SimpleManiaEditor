namespace LumaChart.Avalonia.Infrastructure;

public readonly record struct WindowsQuickEditPaletteSize(
    double Width,
    double Height,
    double Scale);

public static class WindowsQuickEditPaletteResizePolicy
{
    public const double MinimumWidth = 180;
    public const double MinimumHeight = 60;
    public const double MaximumWidth = 520;
    public const double MaximumHeight = 620;
    public const double WorkbenchInset = 8;

    public static WindowsQuickEditPaletteSize Resolve(
        double startWidth,
        double startHeight,
        double horizontalDelta,
        double verticalDelta,
        double workbenchWidth,
        double workbenchHeight)
    {
        startWidth = Math.Max(MinimumWidth, FiniteOr(startWidth, MinimumWidth));
        startHeight = Math.Max(MinimumHeight, FiniteOr(startHeight, MinimumHeight));
        horizontalDelta = FiniteOr(horizontalDelta, 0);
        verticalDelta = FiniteOr(verticalDelta, 0);

        var widthScale = 1 + horizontalDelta / startWidth;
        var heightScale = 1 + verticalDelta / startHeight;
        var requestedScale = Math.Abs(widthScale - 1) >= Math.Abs(heightScale - 1)
            ? widthScale
            : heightScale;

        var maximumAvailableWidth = Math.Min(
            MaximumWidth,
            Math.Max(MinimumWidth, FiniteOr(workbenchWidth, MinimumWidth) - WorkbenchInset));
        var maximumAvailableHeight = Math.Min(
            MaximumHeight,
            Math.Max(MinimumHeight, FiniteOr(workbenchHeight, MinimumHeight) - WorkbenchInset));
        var minimumScale = Math.Max(
            MinimumWidth / startWidth,
            MinimumHeight / startHeight);
        var maximumScale = Math.Min(
            maximumAvailableWidth / startWidth,
            maximumAvailableHeight / startHeight);
        var scale = Math.Clamp(
            FiniteOr(requestedScale, 1),
            Math.Min(minimumScale, maximumScale),
            Math.Max(minimumScale, maximumScale));

        return new WindowsQuickEditPaletteSize(
            startWidth * scale,
            startHeight * scale,
            scale);
    }

    private static double FiniteOr(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}

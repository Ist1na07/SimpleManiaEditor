namespace LumaChart.Avalonia.Infrastructure;

public readonly record struct WindowsFloatingPalettePlacement(
    double QuickLeft,
    double QuickTop,
    double ValuesLeft,
    double ValuesTop);

/// <summary>
/// Resolves the WPF-style factory anchors for the two editor palettes.
/// Both palettes hug the left edge of the Inspector. Quick Edit occupies
/// the otherwise empty upper-right area; Adjust is vertically centered
/// and moves below Quick Edit when the two would overlap.
/// </summary>
public static class WindowsFloatingPaletteLayoutPolicy
{
    public const double WorkbenchInset = 4;
    public const double InspectorGap = 6;
    public const double DefaultQuickTop = 54;

    public static WindowsFloatingPalettePlacement Resolve(
        double workbenchWidth,
        double workbenchHeight,
        double rightBoundary,
        double quickWidth,
        double quickHeight,
        double valuesWidth,
        double valuesHeight,
        double preferredQuickTop,
        double paletteGap)
    {
        workbenchWidth = Positive(workbenchWidth);
        workbenchHeight = Positive(workbenchHeight);
        quickWidth = Positive(quickWidth);
        quickHeight = Positive(quickHeight);
        valuesWidth = Positive(valuesWidth);
        valuesHeight = Positive(valuesHeight);
        paletteGap = Math.Max(0, FiniteOr(paletteGap, 0));
        rightBoundary = Math.Clamp(
            FiniteOr(rightBoundary, workbenchWidth - WorkbenchInset),
            WorkbenchInset,
            workbenchWidth - WorkbenchInset);

        var quickLeft = ClampAxis(
            rightBoundary - quickWidth,
            workbenchWidth,
            quickWidth);
        var quickTop = ClampAxis(
            FiniteOr(preferredQuickTop, DefaultQuickTop),
            workbenchHeight,
            quickHeight);
        var maximumQuickTopWithValuesBelow =
            workbenchHeight -
            WorkbenchInset -
            valuesHeight -
            paletteGap -
            quickHeight;
        if (maximumQuickTopWithValuesBelow >= WorkbenchInset)
            quickTop = Math.Min(quickTop, maximumQuickTopWithValuesBelow);
        var valuesLeft = ClampAxis(
            rightBoundary - valuesWidth,
            workbenchWidth,
            valuesWidth);
        var centeredValuesTop = (workbenchHeight - valuesHeight) / 2;
        var valuesTop = ClampAxis(
            Math.Max(centeredValuesTop, quickTop + quickHeight + paletteGap),
            workbenchHeight,
            valuesHeight);
        return new WindowsFloatingPalettePlacement(
            quickLeft,
            quickTop,
            valuesLeft,
            valuesTop);
    }

    private static double ClampAxis(double requested, double extent, double size) =>
        Math.Clamp(
            FiniteOr(requested, WorkbenchInset),
            WorkbenchInset,
            Math.Max(WorkbenchInset, extent - size - WorkbenchInset));

    private static double Positive(double value) =>
        Math.Max(1, FiniteOr(value, 1));

    private static double FiniteOr(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}

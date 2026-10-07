namespace LumaChart.Avalonia.Infrastructure;

public enum WindowsTimelineGridLineKind
{
    Irregular,
    Quarter,
    Sixth,
    Eighth,
    Twelfth,
    Sixteenth,
    Half,
    Beat,
    Bar
}

public readonly record struct WindowsTimelineGridLineStyle(
    string Color,
    double Thickness);

public readonly record struct WindowsTimelineHoldInteractionStyle(
    bool UseFinalLongNoteChrome,
    double Opacity,
    double TailLineInset);

/// <summary>
/// Windows editor timeline input and grid presentation copied from the current
/// WPF authority. Keeping the decision pure makes wheel direction and the
/// subdivision hierarchy independently verifiable.
/// </summary>
public static class WindowsTimelineVisualParityPolicy
{
    public static WindowsTimelineHoldInteractionStyle HoldInteractionStyle =>
        new(
            UseFinalLongNoteChrome: true,
            Opacity: .94,
            TailLineInset: 7);

    public static int ResolveWheelDirection(double deltaY) => Math.Sign(deltaY);

    public static int? ResolveMeasureNumber(int division, int step)
    {
        division = Math.Clamp(division, 1, 1920);
        if (step < 0 || step % division != 0) return null;
        return checked(step / division + 1);
    }

    public static WindowsTimelineGridLineKind ResolveGridLineKind(
        int division,
        int step)
    {
        division = Math.Clamp(division, 1, 1920);
        if (step % division == 0)
            return step % checked(division * 4) == 0
                ? WindowsTimelineGridLineKind.Bar
                : WindowsTimelineGridLineKind.Beat;

        var denominator =
            division / GreatestCommonDivisor(Math.Abs(step), division);
        return denominator switch
        {
            2 => WindowsTimelineGridLineKind.Half,
            3 or 6 => WindowsTimelineGridLineKind.Sixth,
            4 => WindowsTimelineGridLineKind.Quarter,
            _ => WindowsTimelineGridLineKind.Irregular
        };
    }

    public static WindowsTimelineGridLineStyle ResolveGridLineStyle(
        WindowsTimelineGridLineKind kind) =>
        kind switch
        {
            WindowsTimelineGridLineKind.Quarter => new("#A8A96552", 1),
            WindowsTimelineGridLineKind.Sixth => new("#A777679C", 1),
            WindowsTimelineGridLineKind.Eighth => new("#9D4D817B", .95),
            WindowsTimelineGridLineKind.Twelfth => new("#925F748F", .9),
            WindowsTimelineGridLineKind.Sixteenth => new("#835C6E88", .85),
            WindowsTimelineGridLineKind.Half => new("#B06E8792", 1),
            WindowsTimelineGridLineKind.Beat => new("#B7929A9F", 1.25),
            WindowsTimelineGridLineKind.Bar => new("#D0C2B5A3", 1.55),
            _ => new("#765A6266", .8)
        };

    public static double ResolveConfiguredGridLineThickness(
        WindowsTimelineGridLineKind kind,
        double subdivisionBaseThickness,
        double barThickness)
    {
        if (kind == WindowsTimelineGridLineKind.Bar)
            return Math.Clamp(
                double.IsFinite(barThickness) ? barThickness : 1.55,
                .5,
                6);

        var normalizedBase = Math.Clamp(
            double.IsFinite(subdivisionBaseThickness) ? subdivisionBaseThickness : .8,
            .3,
            3);
        return ResolveGridLineStyle(kind).Thickness * normalizedBase / .8;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Max(1, left);
    }
}

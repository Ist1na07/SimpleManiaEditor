using LumaChart.Core.Editing;

internal static class TimelineGridNavigationSpecs
{
    public static void Run()
    {
        var firstSixth = TimelineGridNavigation.Step(0, 6, 1);
        Assert(firstSixth.Bar == 0 && firstSixth.Numerator == 1 && firstSixth.Denominator == 6,
            $"one sixth-division wheel notch moved to {firstSixth} instead of 0:1/6");

        var secondSixth = TimelineGridNavigation.Step(firstSixth.Value, 6, 1);
        Assert(secondSixth.Bar == 0 && secondSixth.Numerator == 2 && secondSixth.Denominator == 6,
            $"the second sixth-division wheel notch did not retain one-cell stepping: {secondSixth}");
        Assert(Math.Abs(secondSixth.Value - firstSixth.Value - 1d / 6) < 1e-12,
            "one wheel notch moved more than one selected subdivision");

        var fromHalfBeat = TimelineGridNavigation.Step(.5, 6, 1);
        Assert(fromHalfBeat.Numerator == 4 && fromHalfBeat.Denominator == 6 &&
               Math.Abs(fromHalfBeat.Value - .5 - 1d / 6) < 1e-12,
            $"the former half-beat wheel step regression remains at sixth division: {fromHalfBeat}");

        var directSeek = TimelineGridNavigation.Snap(17.347, 6);
        Assert(directSeek.Denominator == 6 &&
               Math.Abs(directSeek.Value * 6 - Math.Round(directSeek.Value * 6)) < 1e-12,
            $"a pixel-derived direct seek retained an off-grid phase: {directSeek}");

        var correctedStep = TimelineGridNavigation.Step(17.347, 6, -1);
        Assert(correctedStep.Denominator == 6 &&
               Math.Abs(directSeek.Value - correctedStep.Value - 1d / 6) < 1e-12,
            $"wheel navigation did not start from the aligned direct-seek grid: {correctedStep}");

        var preservedTwelfth = TimelineGridNavigation.Snap(.25, 12);
        Assert(preservedTwelfth.Bar == 0 && preservedTwelfth.Numerator == 3 && preservedTwelfth.Denominator == 12,
            $"timeline navigation unnecessarily reduced the selected subdivision: {preservedTwelfth}");

        var lowerBound = TimelineGridNavigation.Step(0, 6, -1);
        Assert(lowerBound.Value == 0 && lowerBound.Denominator == 6,
            $"backward wheel navigation crossed beat zero: {lowerBound}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace LumaChart.Avalonia.Infrastructure;

public enum EditorGridNotation { Osu, General }

/// <summary>Internal subdivision is per quarter-note beat. General notation is per whole note.</summary>
public static class EditorGridNotationPolicy
{
    public static string Label(int beatDivision, EditorGridNotation style) =>
        $"1/{beatDivision * (style == EditorGridNotation.General ? 4 : 1)}";

    public static int FromDisplayedDenominator(int denominator, EditorGridNotation style) =>
        style == EditorGridNotation.Osu ? denominator :
        denominator > 0 && denominator % 4 == 0 ? denominator / 4 :
        throw new ArgumentOutOfRangeException(nameof(denominator), "General 网格分母须为 4 的倍数。");
}

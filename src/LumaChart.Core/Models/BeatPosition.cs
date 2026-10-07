using System.Globalization;

namespace LumaChart.Core.Models;

/// <summary>A Malody-compatible musical position: bar + numerator / denominator.</summary>
public readonly record struct BeatPosition : IComparable<BeatPosition>
{
    public const int MaximumPreservedDenominator = 1_000_000;

    public int Bar { get; }
    public int Numerator { get; }
    public int Denominator { get; }

    public static BeatPosition Zero => new(0, 0, 1);

    public BeatPosition(int bar, int numerator, int denominator)
        : this(bar, numerator, denominator, preserveDenominator: false)
    {
    }

    private BeatPosition(int bar, int numerator, int denominator, bool preserveDenominator)
    {
        if (denominator == 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        long normalizedNumerator = numerator;
        long normalizedDenominator = denominator;
        if (normalizedDenominator < 0)
        {
            normalizedNumerator = -normalizedNumerator;
            normalizedDenominator = -normalizedDenominator;
        }
        if (preserveDenominator && normalizedDenominator > MaximumPreservedDenominator)
            throw new ArgumentOutOfRangeException(
                nameof(denominator),
                $"A preserved beat denominator cannot exceed {MaximumPreservedDenominator}.");

        var improper = checked((long)bar * normalizedDenominator + normalizedNumerator);
        var whole = improper / normalizedDenominator;
        var remainder = improper % normalizedDenominator;
        if (remainder < 0)
        {
            whole--;
            remainder += normalizedDenominator;
        }
        var gcd = preserveDenominator ? 1 : GreatestCommonDivisor(remainder, normalizedDenominator);
        Bar = checked((int)whole);
        Numerator = checked((int)(remainder / gcd));
        Denominator = checked((int)(normalizedDenominator / gcd));
    }

    /// <summary>
    /// Creates an exact position while retaining the supplied subdivision. Unlike
    /// the normal constructor, equivalent spellings such as 3/6 are not rewritten
    /// as 1/2. The value is still normalized into a non-negative fraction within
    /// its bar.
    /// </summary>
    public static BeatPosition Preserve(int bar, int numerator, int denominator) =>
        new(bar, numerator, denominator, preserveDenominator: true);

    // A record struct can still be created through default(BeatPosition), bypassing
    // the validating constructor. Treat that all-zero representation as Zero so
    // comparison, hashing and UI formatting remain mutually consistent.
    public double Value => Denominator == 0 ? Bar : Bar + (double)Numerator / Denominator;

    public static BeatPosition FromDouble(double value, int precision = 1920)
    {
        if (!double.IsFinite(value)) return Zero;
        var floored = Math.Floor(value);
        if (floored is < int.MinValue or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Beat position is outside the supported range.");
        var bar = (int)floored;
        var numerator = (int)Math.Round((value - bar) * precision, MidpointRounding.AwayFromZero);
        return new BeatPosition(bar, numerator, precision);
    }

    public BeatPosition Snap(int denominator)
    {
        denominator = Math.Clamp(denominator, 1, 1920);
        var unitsValue = Math.Round(Value * denominator, MidpointRounding.AwayFromZero);
        if (unitsValue is < long.MinValue or > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(denominator), "Beat position is outside the supported range.");
        var units = (long)unitsValue;
        var bar = Math.DivRem(units, denominator, out var numerator);
        if (numerator < 0)
        {
            bar--;
            numerator += denominator;
        }
        return Preserve(checked((int)bar), checked((int)numerator), denominator);
    }

    /// <summary>
    /// Snaps a visual position to the center of its subdivision cell and retains
    /// the 2N denominator. Cell numbers are one-based: cell k is (2k-1)/(2N).
    /// </summary>
    public static BeatPosition SnapToCellCenter(double value, int division)
    {
        if (!double.IsFinite(value)) return Zero;
        division = Math.Clamp(division, 1, 1920);
        var floored = Math.Floor(value);
        if (floored is < int.MinValue or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Beat position is outside the supported range.");
        var bar = (int)floored;
        var fraction = value - floored;
        var cell = Math.Clamp((int)Math.Floor(fraction * division), 0, division - 1);
        return Preserve(bar, checked(cell * 2 + 1), checked(division * 2));
    }

    public int CompareTo(BeatPosition other)
    {
        var leftDenominator = Denominator == 0 ? 1 : Denominator;
        var rightDenominator = other.Denominator == 0 ? 1 : other.Denominator;
        var left = ((Int128)Bar * leftDenominator + Numerator) * rightDenominator;
        var right = ((Int128)other.Bar * rightDenominator + other.Numerator) * leftDenominator;
        return left.CompareTo(right);
    }

    public bool Equals(BeatPosition other) => CompareTo(other) == 0;

    public override int GetHashCode()
    {
        var denominator = Denominator == 0 ? 1 : Denominator;
        var gcd = GreatestCommonDivisor(Numerator, denominator);
        return HashCode.Combine(Bar, Numerator / gcd, denominator / gcd);
    }

    // TryParse is culture-invariant. Current-culture formatting wrote U+2212
    // (sv-SE, fi, nb) or U+061C (ar) before negative bars, which TryParse and
    // every file format reject.
    public override string ToString() => Denominator == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Bar}:{Numerator}/1")
        : string.Create(CultureInfo.InvariantCulture, $"{Bar}:{Numerator}/{Denominator}");

    public static bool TryParse(string? text, out BeatPosition position)
    {
        position = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalBeat) &&
            double.IsFinite(decimalBeat))
        {
            try
            {
                position = FromDouble(decimalBeat);
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
            catch (OverflowException) { return false; }
        }

        var colon = text.Split(':', 2, StringSplitOptions.TrimEntries);
        if (colon.Length != 2 || !int.TryParse(colon[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bar)) return false;
        var fraction = colon[1].Split('/', 2, StringSplitOptions.TrimEntries);
        if (fraction.Length != 2 ||
            !int.TryParse(fraction[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var numerator) ||
            !int.TryParse(fraction[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var denominator) || denominator == 0) return false;
        try
        {
            position = Preserve(bar, numerator, denominator);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
        catch (OverflowException) { return false; }
    }

    public static BeatPosition operator +(BeatPosition left, double beats) => FromDouble(left.Value + beats);
    public static BeatPosition operator -(BeatPosition left, double beats) => FromDouble(left.Value - beats);

    private static long GreatestCommonDivisor(long a, long b)
    {
        if (a == 0) return Math.Max(1, b);
        while (b != 0) (a, b) = (b, a % b);
        return Math.Max(1, Math.Abs(a));
    }
}

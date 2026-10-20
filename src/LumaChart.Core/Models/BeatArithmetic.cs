using System.Numerics;

namespace LumaChart.Core.Models;

/// <summary>Exact musical affine arithmetic; reduce before testing the storage precision limit.</summary>
public static class BeatArithmetic
{
    private readonly record struct Fraction(BigInteger N, BigInteger D)
    {
        public static Fraction Of(BeatPosition b) => new((BigInteger)b.Bar * Math.Max(1,b.Denominator)+b.Numerator,Math.Max(1,b.Denominator));
        public static Fraction operator +(Fraction a,Fraction b)=>new(a.N*b.D+b.N*a.D,a.D*b.D);
        public static Fraction operator -(Fraction a,Fraction b)=>new(a.N*b.D-b.N*a.D,a.D*b.D);
        public static Fraction operator *(Fraction a,Fraction b)=>new(a.N*b.N,a.D*b.D);
        public static Fraction operator /(Fraction a,Fraction b)=>b.N.IsZero?throw new DivideByZeroException():new(a.N*b.D,a.D*b.N);
        public BeatPosition Beat(BigInteger preferredDenominator = default)
        {
            var n=N;var d=D;if(d<0){n=-n;d=-d;}
            var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d);n/=gcd;d/=gcd;
            if (preferredDenominator >= d && preferredDenominator <= BeatPosition.MaximumPreservedDenominator && preferredDenominator % d == 0)
            {
                var numerator = n * (preferredDenominator / d);
                return BeatPosition.Preserve(checked((int)(numerator / preferredDenominator)),
                    checked((int)(numerator % preferredDenominator)), (int)preferredDenominator);
            }
            return d<=BeatPosition.MaximumPreservedDenominator
                ?new BeatPosition(checked((int)(n/d)),checked((int)(n%d)),(int)d)
                :BeatPosition.FromDouble((double)n/(double)d,BeatPosition.MaximumPreservedDenominator);
        }
    }
    public static BeatPosition Offset(BeatPosition anchor,BeatPosition value,BeatPosition origin) =>
        (Fraction.Of(anchor)+Fraction.Of(value)-Fraction.Of(origin)).Beat(CommonDenominator(anchor, value, origin));
    public static BeatPosition Reflect(BeatPosition start,BeatPosition end,BeatPosition value) =>
        (Fraction.Of(start)+Fraction.Of(end)-Fraction.Of(value)).Beat(CommonDenominator(start, end, value));
    private static BigInteger CommonDenominator(BeatPosition a, BeatPosition b, BeatPosition c)
    {
        var first = new BigInteger(Math.Max(1, a.Denominator));
        foreach (var denominator in new[] { Math.Max(1, b.Denominator), Math.Max(1, c.Denominator) })
            first = first / BigInteger.GreatestCommonDivisor(first, denominator) * denominator;
        return first;
    }
    public static BeatPosition Scale(BeatPosition value,double scale)
    {
        if(!double.IsFinite(scale)||scale<0)throw new ArgumentOutOfRangeException(nameof(scale));
        for(var d=1;d<=4096;d++)
            if(Math.Abs(scale*d-Math.Round(scale*d))<1e-10)
                return (Fraction.Of(value)*new Fraction((long)Math.Round(scale*d),d)).Beat();
        return BeatPosition.FromDouble(value.Value*scale,BeatPosition.MaximumPreservedDenominator);
    }
    public static BeatPosition MapRange(BeatPosition value,BeatPosition start,BeatPosition end,BeatPosition targetStart,BeatPosition targetEnd)
    {
        var delta=Fraction.Of(value)-Fraction.Of(start);
        return (Fraction.Of(targetStart)+delta*(start==end?new Fraction(1,1):
            (Fraction.Of(targetEnd)-Fraction.Of(targetStart))/(Fraction.Of(end)-Fraction.Of(start)))).Beat();
    }
}

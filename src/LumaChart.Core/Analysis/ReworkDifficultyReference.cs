using System.Globalization;

namespace LumaChart.Core.Analysis;

internal static class ReworkDifficultyReference
{
    private readonly record struct Interval(double Lower, double Upper, string Name);

    private static readonly Interval[] RcIntervals = Parse(RcData);
    private static readonly Interval[] LnIntervals = Parse(LnData);

    public static string Estimate(
        double starRating,
        double longNoteRatio,
        IReadOnlyList<ReworkReferenceNote> notes,
        double speedRate)
    {
        if (longNoteRatio <= .15 &&
            ReworkAzusaDifficultyReference.TryEstimate(
                notes,
                speedRate,
                starRating,
                out var azusaLabel))
        {
            return azusaLabel;
        }

        var rc = Find(RcIntervals, starRating) ?? (starRating switch
        {
            < 1.502 => "< Intro 1 low",
            > 11.129 => "> Theta high",
            _ => "未知 RC 难度"
        });
        if (longNoteRatio <= .15) return rc;
        var ln = Find(LnIntervals, starRating) ?? (starRating switch
        {
            < 4.832 => "< LN 5 mid",
            > 9.589 => "> LN 17 high",
            _ => "未知 LN 难度"
        });
        return $"{rc}  ·  {ln}";
    }

    private static string? Find(IEnumerable<Interval> intervals, double value) =>
        intervals.FirstOrDefault(interval => interval.Lower <= value && value <= interval.Upper) is var match &&
        match.Name is not null ? match.Name : null;

    private static Interval[] Parse(string source) => source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('|', 3, StringSplitOptions.TrimEntries))
        .Select(parts => new Interval(
            double.Parse(parts[0], CultureInfo.InvariantCulture),
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            parts[2]))
        .ToArray();

    private const string RcData = """
        1.502|1.631|Intro 1 low
        1.631|1.760|Intro 1 mid/low
        1.760|1.890|Intro 1 mid
        1.890|2.019|Intro 1 mid/high
        2.019|2.148|Intro 1 high
        2.148|2.278|Intro 2 low
        2.278|2.407|Intro 2 mid/low
        2.407|2.502|Intro 2 mid
        2.502|2.560|Intro 2 mid/high
        2.560|2.619|Intro 2 high
        2.619|2.679|Intro 3 low
        2.679|2.737|Intro 3 mid/low
        2.737|2.821|Intro 3 mid
        2.821|2.929|Intro 3 mid/high
        2.929|3.037|Intro 3 high
        3.037|3.145|Reform 1 low
        3.145|3.253|Reform 1 mid/low
        3.253|3.346|Reform 1 mid
        3.346|3.424|Reform 1 mid/high
        3.424|3.503|Reform 1 high
        3.503|3.581|Reform 2 low
        3.581|3.659|Reform 2 mid/low
        3.659|3.701|Reform 2 mid
        3.701|3.708|Reform 2 mid/high
        3.708|3.714|Reform 2 high
        3.714|3.720|Reform 3 low
        3.720|3.727|Reform 3 mid/low
        3.727|3.810|Reform 3 mid
        3.810|3.970|Reform 3 mid/high
        3.970|4.130|Reform 3 high
        4.130|4.290|Reform 4 low
        4.290|4.450|Reform 4 mid/low
        4.450|4.569|Reform 4 mid
        4.569|4.648|Reform 4 mid/high
        4.648|4.726|Reform 4 high
        4.726|4.804|Reform 5 low
        4.804|4.883|Reform 5 mid/low
        4.883|4.972|Reform 5 mid
        4.972|5.072|Reform 5 mid/high
        5.072|5.173|Reform 5 high
        5.173|5.273|Reform 6 low
        5.273|5.373|Reform 6 mid/low
        5.373|5.441|Reform 6 mid
        5.441|5.476|Reform 6 mid/high
        5.476|5.511|Reform 6 high
        5.511|5.547|Reform 7 low
        5.547|5.582|Reform 7 mid/low
        5.582|5.646|Reform 7 mid
        5.646|5.738|Reform 7 mid/high
        5.738|5.829|Reform 7 high
        5.829|5.921|Reform 8 low
        5.921|6.013|Reform 8 mid/low
        6.013|6.069|Reform 8 mid
        6.069|6.090|Reform 8 mid/high
        6.090|6.110|Reform 8 high
        6.110|6.130|Reform 9 low
        6.130|6.151|Reform 9 mid/low
        6.151|6.205|Reform 9 mid
        6.205|6.294|Reform 9 mid/high
        6.294|6.382|Reform 9 high
        6.382|6.471|Reform 10 low
        6.471|6.560|Reform 10 mid/low
        6.560|6.616|Reform 10 mid
        6.616|6.641|Reform 10 mid/high
        6.641|6.666|Reform 10 high
        6.666|6.691|Alpha low
        6.691|6.716|Alpha mid/low
        6.716|6.773|Alpha mid
        6.773|6.860|Alpha mid/high
        6.860|6.947|Alpha high
        6.947|7.034|Beta low
        7.034|7.121|Beta mid/low
        7.121|7.214|Beta mid
        7.214|7.312|Beta mid/high
        7.312|7.410|Beta high
        7.410|7.509|Gamma low
        7.509|7.607|Gamma mid/low
        7.607|7.705|Gamma mid
        7.705|7.803|Gamma mid/high
        7.803|7.901|Gamma high
        7.901|8.000|Delta low
        8.000|8.098|Delta mid/low
        8.098|8.244|Delta mid
        8.244|8.438|Delta mid/high
        8.438|8.631|Delta high
        8.631|8.825|Epsilon low
        8.825|9.019|Epsilon mid/low
        9.019|9.172|Epsilon mid
        9.172|9.285|Epsilon mid/high
        9.285|9.398|Epsilon high
        9.398|9.511|Emik Zeta low
        9.511|9.624|Emik Zeta mid/low
        9.624|9.742|Emik Zeta mid
        9.742|9.867|Emik Zeta mid/high
        9.867|9.991|Emik Zeta high
        9.991|10.116|Thaumiel Eta low
        10.116|10.241|Thaumiel Eta mid/low
        10.241|10.358|Thaumiel Eta mid
        10.358|10.468|Thaumiel Eta mid/high
        10.468|10.578|Thaumiel Eta high
        10.578|10.689|CloverWisp Theta low
        10.689|10.799|CloverWisp Theta mid/low
        10.799|10.909|CloverWisp Theta mid
        10.909|11.019|CloverWisp Theta mid/high
        11.019|11.129|CloverWisp Theta high
        """;

    private const string LnData = """
        4.832|4.898|LN 5 mid
        4.898|4.963|LN 5 mid/high
        4.963|5.095|LN 5 high
        5.095|5.160|LN 6 low
        5.160|5.143|LN 6 mid/low
        5.143|5.213|LN 6 mid
        5.213|5.264|LN 6 mid/high
        5.264|5.314|LN 6 high
        5.314|5.446|LN 7 low
        5.446|5.521|LN 7 mid/low
        5.521|5.577|LN 7 mid
        5.577|5.631|LN 7 mid/high
        5.631|5.686|LN 7 high
        5.686|5.740|LN 8 low
        5.740|5.794|LN 8 mid/low
        5.794|5.853|LN 8 mid
        5.853|5.917|LN 8 mid/high
        5.917|5.981|LN 8 high
        5.981|6.044|LN 9 low
        6.044|6.108|LN 9 mid/low
        6.108|6.175|LN 9 mid
        6.175|6.246|LN 9 mid/high
        6.246|6.318|LN 9 high
        6.318|6.389|LN 10 low
        6.389|6.461|LN 10 mid/low
        6.461|6.534|LN 10 mid
        6.534|6.611|LN 10 mid/high
        6.611|6.687|LN 10 high
        6.687|6.763|LN 11 low
        6.763|6.839|LN 11 mid/low
        6.839|6.898|LN 11 mid
        6.898|6.920|LN 11 mid/high
        6.920|6.941|LN 11 high
        6.941|7.023|LN 12 low
        7.023|7.068|LN 12 mid/low
        7.068|7.136|LN 12 mid
        7.136|7.225|LN 12 mid/high
        7.225|7.313|LN 12 high
        7.313|7.401|LN 13 low
        7.401|7.490|LN 13 mid/low
        7.490|7.578|LN 13 mid
        7.578|7.665|LN 13 mid/high
        7.665|7.753|LN 13 high
        7.753|7.841|LN 14 low
        7.841|7.929|LN 14 mid/low
        7.929|8.013|LN 14 mid
        8.013|8.093|LN 14 mid/high
        8.093|8.173|LN 14 high
        8.173|8.253|LN 15 low
        8.253|8.333|LN 15 mid/low
        8.333|8.389|LN 15 mid
        8.389|8.428|LN 15 mid/high
        8.428|8.470|LN 15 high
        8.470|8.509|Hypersovae LN 16 low
        8.509|8.548|Hypersovae LN 16 mid/low
        8.548|8.586|Hypersovae LN 16 mid
        8.586|8.635|Hypersovae LN 16 mid/high
        8.635|8.908|Hypersovae LN 16 high
        8.908|9.044|Lnlism LN 17 low
        9.044|9.180|Lnlism LN 17 mid/low
        9.180|9.316|Lnlism LN 17 mid
        9.316|9.452|Lnlism LN 17 mid/high
        9.452|9.589|Lnlism LN 17 high
        """;
}

using LumaChart.Core.Models;

namespace LumaChart.Application.Exporting;

public enum ChartExportFormat
{
    Malody,
    OsuMania
}

public enum ChartExportMode
{
    SingleChart,
    FullPackage,
    SingleRatePackage,
    RatePack
}

public sealed record ChartExportOptions(
    ChartExportFormat Format,
    ChartExportMode Mode,
    bool PreservePitch,
    double Rate,
    double MinimumRate,
    double MaximumRate,
    double RateStep,
    double OsuOverallDifficulty = 8,
    double OsuHpDrainRate = 8)
{
    public IReadOnlyList<double> Rates()
    {
        if (Mode is ChartExportMode.SingleChart or ChartExportMode.FullPackage) return [1];
        if (Mode == ChartExportMode.SingleRatePackage)
        {
            ValidateRate(Rate);
            return [Math.Round(Rate, 4)];
        }

        ValidateRate(MinimumRate);
        ValidateRate(MaximumRate);
        if (!double.IsFinite(RateStep) || RateStep <= 0)
            throw new InvalidOperationException("倍速间隔必须是大于 0 的有效数字。");
        if (MaximumRate < MinimumRate)
            throw new InvalidOperationException("最高倍速不能小于最低倍速。");

        var count = (int)Math.Floor((MaximumRate - MinimumRate) / RateStep + 1e-7) + 1;
        if (count is < 1 or > 100)
            throw new InvalidOperationException("倍速包必须包含 1–100 个倍率。");

        var values = new List<double>(count + 1);
        for (var index = 0; index < count; index++)
            values.Add(Math.Round(MinimumRate + index * RateStep, 4));
        if (values[^1] < MaximumRate - .0001)
            values.Add(Math.Round(MaximumRate, 4));
        if (values.Count > 100)
            throw new InvalidOperationException("倍速包必须包含 1–100 个倍率。");
        return values;
    }

    private static void ValidateRate(double value)
    {
        if (!double.IsFinite(value) || value is < .2 or > 3)
            throw new InvalidOperationException("倍速必须在 0.2–3.0 之间。");
    }
}

public sealed record ChartExportRequest(
    ChartDocument Document,
    string DestinationPath,
    ChartExportOptions Options,
    string? SessionScratchRoot = null,
    string? PackageSourceChartPath = null);

public readonly record struct ChartExportProgress(
    int Completed,
    int Total,
    string Stage);

public sealed record ChartExportResult(
    string DestinationPath,
    IReadOnlyList<string> ChartEntryPaths,
    int EntryCount,
    long OutputBytes);

public sealed record ChartExportPlan(
    ChartExportFormat Format,
    ChartExportMode Mode,
    IReadOnlyList<double> Rates,
    string DestinationExtension,
    bool ProducesArchive,
    bool RequiresSessionScratchRoot,
    bool RequiresAudioTranscoder);

public static class ChartExportPlanner
{
    public static ChartExportPlan Create(ChartExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Format == ChartExportFormat.OsuMania)
        {
            ValidateDifficulty(options.OsuOverallDifficulty, nameof(options.OsuOverallDifficulty));
            ValidateDifficulty(options.OsuHpDrainRate, nameof(options.OsuHpDrainRate));
        }
        var rates = options.Rates();
        var producesArchive = options.Mode != ChartExportMode.SingleChart;
        var extension = options.Format switch
        {
            ChartExportFormat.Malody when producesArchive => ".mcz",
            ChartExportFormat.Malody => ".mc",
            ChartExportFormat.OsuMania when producesArchive => ".osz",
            _ => ".osu"
        };
        var requiresAudioTranscoder = options.Mode is ChartExportMode.SingleRatePackage or ChartExportMode.RatePack &&
                                      rates.Any(rate => Math.Abs(rate - 1) > .000001);
        return new ChartExportPlan(
            options.Format,
            options.Mode,
            rates,
            extension,
            producesArchive,
            producesArchive,
            requiresAudioTranscoder);
    }

    private static void ValidateDifficulty(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value is < 0 or > 10)
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "osu! 的 OD 与 HP 必须是 0–10 之间的数字。");
    }
}

/// <summary>
/// Platform adapter used by rate-package export. Implementations own decoding,
/// time/pitch processing and Ogg/Vorbis encoding; Application only supplies the
/// paths and WPF-compatible rate semantics.
/// </summary>
public interface IChartExportAudioTranscoder
{
    Task ConvertToOggAsync(
        string sourceAudioPath,
        string destinationOggPath,
        double rate,
        bool preservePitch,
        CancellationToken cancellationToken = default);
}

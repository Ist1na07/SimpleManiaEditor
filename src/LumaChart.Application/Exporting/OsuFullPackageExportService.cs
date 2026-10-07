namespace LumaChart.Application.Exporting;

/// <summary>
/// Creates the osu!mania FullPackage 1.0x archive used by the Windows/WPF
/// 1.1.0 client while keeping the caller-owned document unchanged.
/// </summary>
public static class OsuFullPackageExportService
{
    public static async Task<OsuFullPackageExportResult> ExportAsync(
        OsuFullPackageExportRequest request,
        IProgress<OsuFullPackageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateDifficulty(request.OverallDifficulty, nameof(request.OverallDifficulty));
        ValidateDifficulty(request.HpDrainRate, nameof(request.HpDrainRate));

        var result = await ChartFullPackageExportService.ExportAsync(
                new ChartFullPackageExportRequest(
                    request.Document,
                    request.PackageSourceChartPath,
                    request.DestinationArchivePath,
                    request.SessionScratchRoot,
                    ChartFullPackageExportFormat.OsuMania,
                    request.OverallDifficulty,
                    request.HpDrainRate),
                progress == null
                    ? null
                    : value => progress.Report(new OsuFullPackageExportProgress(
                        value.Completed,
                        value.Total,
                        value.Stage)),
                cancellationToken)
            .ConfigureAwait(false);

        return new OsuFullPackageExportResult(
            result.DestinationArchivePath,
            result.MainChartEntryPath,
            result.EntryCount,
            result.ArchiveBytes);
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

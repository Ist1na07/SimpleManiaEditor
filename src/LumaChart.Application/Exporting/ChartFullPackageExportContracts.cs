using LumaChart.Core.Models;

namespace LumaChart.Application.Exporting;

internal enum ChartFullPackageExportFormat
{
    Malody,
    OsuMania
}

internal sealed record ChartFullPackageExportRequest(
    ChartDocument Document,
    string PackageSourceChartPath,
    string DestinationArchivePath,
    string SessionScratchRoot,
    ChartFullPackageExportFormat Format,
    double OsuOverallDifficulty,
    double OsuHpDrainRate);

internal readonly record struct ChartFullPackageExportProgress(
    int Completed,
    int Total,
    string Stage);

internal sealed record ChartFullPackageExportResult(
    string DestinationArchivePath,
    string MainChartEntryPath,
    int EntryCount,
    long ArchiveBytes);

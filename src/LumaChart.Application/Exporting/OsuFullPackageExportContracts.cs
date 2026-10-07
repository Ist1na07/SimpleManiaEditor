using LumaChart.Core.Models;

namespace LumaChart.Application.Exporting;

public sealed record OsuFullPackageExportRequest(
    ChartDocument Document,
    string PackageSourceChartPath,
    string DestinationArchivePath,
    string SessionScratchRoot,
    double OverallDifficulty = 8,
    double HpDrainRate = 8);

public readonly record struct OsuFullPackageExportProgress(
    int Completed,
    int Total,
    string Stage);

public sealed record OsuFullPackageExportResult(
    string DestinationArchivePath,
    string MainChartEntryPath,
    int EntryCount,
    long ArchiveBytes);

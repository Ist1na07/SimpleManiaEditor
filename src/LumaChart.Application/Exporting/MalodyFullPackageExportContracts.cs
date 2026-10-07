using LumaChart.Core.Models;

namespace LumaChart.Application.Exporting;

public sealed record MalodyFullPackageExportRequest(
    ChartDocument Document,
    string PackageSourceChartPath,
    string DestinationArchivePath,
    string SessionScratchRoot);

public readonly record struct MalodyFullPackageExportProgress(
    int Completed,
    int Total,
    string Stage);

public sealed record MalodyFullPackageExportResult(
    string DestinationArchivePath,
    string MainChartEntryPath,
    int EntryCount,
    long ArchiveBytes);

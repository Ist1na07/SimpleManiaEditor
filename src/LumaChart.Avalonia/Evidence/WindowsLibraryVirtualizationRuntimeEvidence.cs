using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumaChart.Avalonia.Evidence;

public sealed record WindowsLibraryVirtualizationRuntimeEvidence(
    string Schema,
    DateTimeOffset GeneratedUtc,
    string Status,
    string Platform,
    int ProcessId,
    string WorkspaceRoot,
    int IndexedChartCount,
    int IndexedFolderCount,
    int VisibleFolderCount,
    double InitializationMilliseconds,
    bool ContainerTemplateOwnsStableCard,
    bool FuncDataTemplateUsed,
    string? ItemsPanelType,
    int ScrollSampleCount,
    int InitialRealizedContainerCount,
    int MinimumRealizedContainerCount,
    int MaximumRealizedContainerCount,
    int UniqueListBoxItemInstances,
    int UniqueCardInstances,
    int StaleBindingMismatchCount,
    int MaximumMaterializedCollapsedChartRows,
    string EvidenceExpandedFolderPath,
    string EvidenceExpandedDisplayTitle,
    int EvidenceExpandedChartRowCount,
    string EvidenceExpandedChartPath,
    bool EvidenceExpandedBindingMatched,
    WindowsLibraryVirtualizationMetricSummary ScrollSettleMilliseconds,
    WindowsLibraryVirtualizationMetricSummary InputQueueDelayMilliseconds,
    WindowsLibraryVirtualizationMemoryEvidence Memory,
    double RenderScaling,
    double? RefreshRate);

public sealed record WindowsLibraryVirtualizationMetricSummary(
    int Samples,
    double Minimum,
    double P50,
    double P95,
    double P99,
    double Maximum);

public sealed record WindowsLibraryVirtualizationMemoryEvidence(
    long ManagedBytesBefore,
    long ManagedBytesAfter,
    long ManagedBytesPeak,
    long PrivateBytesBefore,
    long PrivateBytesAfter,
    long WorkingSetBytesBefore,
    long WorkingSetBytesAfter);

public static class WindowsLibraryVirtualizationRuntimeEvidenceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAtomicAsync(
        string outputPath,
        WindowsLibraryVirtualizationRuntimeEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(evidence);
        outputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(outputPath)
                        ?? throw new InvalidOperationException(
                            "Library virtualization evidence output has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        evidence,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

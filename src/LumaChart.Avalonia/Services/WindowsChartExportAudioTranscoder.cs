using LumaChart.Application.Exporting;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Windows audio boundary for shared rate-package export orchestration.
/// </summary>
public sealed class WindowsChartExportAudioTranscoder : IChartExportAudioTranscoder
{
    public static WindowsChartExportAudioTranscoder Instance { get; } = new();

    public Task ConvertToOggAsync(
        string sourceAudioPath,
        string destinationOggPath,
        double rate,
        bool preservePitch,
        CancellationToken cancellationToken = default) =>
        WindowsAudioTranscoder.ConvertToOggAsync(
            sourceAudioPath,
            destinationOggPath,
            rate,
            preservePitch,
            cancellationToken: cancellationToken);
}

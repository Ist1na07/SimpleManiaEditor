using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;

namespace LumaChart.Avalonia.Services;

internal delegate Task WindowsChartAudioTranscodeOperation(
    string sourcePath,
    string outputPath,
    IProgress<double>? progress,
    CancellationToken cancellationToken);

internal delegate ChartAssetImportResult WindowsChartAudioCopyOperation(
    string chartPath,
    string sourcePath,
    string? eventualChartPath,
    string? preferredFileName);

internal enum WindowsChartAudioImportStatus
{
    Imported,
    Canceled,
    Denied,
    Failed
}

internal sealed record WindowsChartAudioImportResult(
    WindowsChartAudioImportStatus Status,
    ChartAssetImportResult? Asset = null,
    ChartMetadataApplyResult? MetadataApply = null,
    Exception? Error = null);

/// <summary>
/// Owns the Windows audio-import transaction from copy/transcode through the
/// single metadata mutation. UI code supplies the current-tab guard and only
/// presents the result; every non-success path removes newly created assets.
/// </summary>
internal sealed class WindowsChartAudioImportWorkflow(
    WindowsChartAudioTranscodeOperation transcode,
    WindowsChartAudioCopyOperation copy)
{
    public static WindowsChartAudioImportWorkflow CreateDefault() => new(
        (sourcePath, outputPath, progress, cancellationToken) =>
            WindowsAudioTranscoder.ConvertToOggAsync(
                sourcePath,
                outputPath,
                progress: progress,
                cancellationToken: cancellationToken),
        ChartAssetImportService.ImportAudio);

    public async Task<WindowsChartAudioImportResult> RunAsync(
        EditorSession session,
        string workingChartPath,
        string? sourceChartPath,
        string sourceAudioPath,
        bool convertToOgg,
        Func<bool> isCurrentContext,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan preMetadataCommitDelay = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingChartPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAudioPath);
        ArgumentNullException.ThrowIfNull(isCurrentContext);
        if (preMetadataCommitDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(preMetadataCommitDelay));

        ChartAssetImportResult? imported = null;
        string? convertedScratch = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (convertToOgg)
            {
                var workingDirectory = Path.GetDirectoryName(workingChartPath)
                                       ?? throw new InvalidOperationException(
                                           "当前谱面没有工作目录。");
                convertedScratch = Path.Combine(
                    workingDirectory,
                    $".luma-audio-import-{Guid.NewGuid():N}.ogg");
                await transcode(
                    sourceAudioPath,
                    convertedScratch,
                    progress,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                imported = await Task.Run(
                    () => copy(
                        workingChartPath,
                        convertedScratch,
                        sourceChartPath,
                        Path.GetFileNameWithoutExtension(sourceAudioPath) + ".ogg"),
                    cancellationToken);
            }
            else
            {
                imported = await Task.Run(
                    () => copy(
                        workingChartPath,
                        sourceAudioPath,
                        sourceChartPath,
                        preferredFileName: null),
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (preMetadataCommitDelay > TimeSpan.Zero)
                await Task.Delay(preMetadataCommitDelay, cancellationToken);
            if (!isCurrentContext())
                throw new OperationCanceledException(
                    "导入期间已切换谱面。",
                    cancellationToken);

            var apply = ChartMetadataEditingService.SetAudioFile(
                session,
                imported.FileName);
            if (apply.Status == ChartMetadataApplyStatus.Denied)
            {
                DeleteCreatedAsset(imported);
                return new WindowsChartAudioImportResult(
                    WindowsChartAudioImportStatus.Denied,
                    MetadataApply: apply);
            }

            return new WindowsChartAudioImportResult(
                WindowsChartAudioImportStatus.Imported,
                imported,
                apply);
        }
        catch (OperationCanceledException)
        {
            DeleteCreatedAsset(imported);
            return new WindowsChartAudioImportResult(
                WindowsChartAudioImportStatus.Canceled);
        }
        catch (Exception exception)
        {
            // Windows media decoders/transcoders can surface COM and codec
            // exceptions in addition to the usual IO/argument families. This
            // is a user operation boundary, matching WPF's fail-with-message
            // behavior instead of allowing async-void event escalation.
            DeleteCreatedAsset(imported);
            return new WindowsChartAudioImportResult(
                WindowsChartAudioImportStatus.Failed,
                Error: exception);
        }
        finally
        {
            DeleteBestEffort(convertedScratch);
        }
    }

    private static void DeleteCreatedAsset(ChartAssetImportResult? imported)
    {
        if (imported?.Created == true)
            DeleteBestEffort(imported.DestinationPath);
    }

    private static void DeleteBestEffort(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the primary import/cancellation/permission result.
        }
    }
}

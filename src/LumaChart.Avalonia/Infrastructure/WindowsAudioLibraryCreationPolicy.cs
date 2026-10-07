using LumaChart.Avalonia.Services;

namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsAudioLibraryCreationPolicy
{
    public static string CompletionStatus(
        string chartName,
        WindowsChartAudioImportStatus importStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chartName);
        return importStatus switch
        {
            WindowsChartAudioImportStatus.Imported =>
                AvaloniaLocalizationCatalog.Format("已创建“{0}”；当前修改位于软件缓存，点击保存后才会写回曲库目录", chartName),
            WindowsChartAudioImportStatus.Canceled =>
                AvaloniaLocalizationCatalog.Format("已创建“{0}”，但音频导入已取消；空谱面仍可继续编辑", chartName),
            WindowsChartAudioImportStatus.Denied =>
                AvaloniaLocalizationCatalog.Format("已创建“{0}”，但当前权限不允许写入音频；空谱面仍可继续编辑", chartName),
            WindowsChartAudioImportStatus.Failed =>
                AvaloniaLocalizationCatalog.Format("已创建“{0}”，但音频导入失败；空谱面仍可继续编辑", chartName),
            _ => throw new ArgumentOutOfRangeException(
                nameof(importStatus),
                importStatus,
                "Unsupported audio import status.")
        };
    }
}

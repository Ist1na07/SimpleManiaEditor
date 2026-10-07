using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

internal sealed class ArchiveImportDialog : WindowsWpf110ChoiceDialog<bool>
{
    internal const string DialogAutomationId = "ArchiveImport.Dialog";

    public ArchiveImportDialog(
        string destinationRoot,
        int supportedCharts,
        int unsupportedCharts,
        int brokenCharts,
        double uiScale,
        double fontScale)
        : base(
            "谱包导入完成",
            BuildMessage(destinationRoot, supportedCharts, unsupportedCharts, brokenCharts),
            supportedCharts > 0
                ? WindowsWpf110DialogTone.Question
                : WindowsWpf110DialogTone.Information,
            supportedCharts > 0
                ?
                [
                    new(false, "否", "ArchiveImport.Keep", IsCancel: true),
                    new(true, "是", "ArchiveImport.Open", IsDefault: true)
                ]
                :
                [
                    new(false, "确定", "ArchiveImport.Confirm", IsDefault: true, IsCancel: true)
                ],
            false,
            uiScale,
            fontScale,
            DialogAutomationId)
    {
    }

    private static string BuildMessage(
        string destinationRoot,
        int supportedCharts,
        int unsupportedCharts,
        int brokenCharts)
    {
        var summary =
            AvaloniaLocalizationCatalog.Format("谱包已解压到当前曲库：\n{0}\n\n可编辑下落式键音谱面：{1:N0} 张", destinationRoot, supportedCharts);
        if (unsupportedCharts > 0)
            summary += AvaloniaLocalizationCatalog.Format("\n已忽略其他游玩模式：{0:N0} 张", unsupportedCharts);
        if (brokenCharts > 0)
            summary += AvaloniaLocalizationCatalog.Format("\n无法识别或已损坏：{0:N0} 张", brokenCharts);
        if (supportedCharts > 0)
            summary += AvaloniaLocalizationCatalog.Text("\n\n是否现在把这些谱面全部加入工作区？");
        return summary;
    }
}

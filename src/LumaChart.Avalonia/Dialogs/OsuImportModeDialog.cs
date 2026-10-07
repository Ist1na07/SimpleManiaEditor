using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

internal enum OsuImportChoice
{
    Cancel,
    Calibrate,
    Preserve
}

internal sealed class OsuImportModeDialog :
    WindowsWpf110ChoiceDialog<OsuImportChoice>
{
    internal const string DialogAutomationId = "OsuImportMode.Dialog";
    internal const string MessageAutomationId = "OsuImportMode.ChartName";

    public OsuImportModeDialog(
        string chartName,
        double uiScale,
        double fontScale)
        : base(
            "选择 osu! 导入方式",
            AvaloniaLocalizationCatalog.Format("{0}\n\n导入 osu!mania 时，可以根据 TimingPoints / BPM 把 Note 面头、LN 面尾和 Scroll Effect 校准到常用分度。\n\n“校准分度”只会在误差不超过 2 ms 且不超过 1/192 拍时吸附到 1/1–1/48 的常用分度；超出阈值的人工偏移会原样保留，并可避免 678/679 一类异常分母。\n\n“保留毫秒”则完全按 osu! 原始时间换算，不做分度校准。", chartName),
            WindowsWpf110DialogTone.Question,
            [
                new(
                    OsuImportChoice.Cancel,
                    "取消",
                    "OsuImportMode.Cancel",
                    IsCancel: true),
                new(
                    OsuImportChoice.Calibrate,
                    "校准分度（推荐）",
                    "OsuImportMode.Calibrate",
                    IsDefault: true),
                new(
                    OsuImportChoice.Preserve,
                    "保留原始毫秒",
                    "OsuImportMode.Preserve")
            ],
            OsuImportChoice.Cancel,
            uiScale,
            fontScale,
            DialogAutomationId,
            MessageAutomationId)
    {
        global::Avalonia.Automation.AutomationProperties.SetHelpText(
            this,
            AvaloniaLocalizationCatalog.Format("契约={0}; 谱面={1}", ContractVersion, chartName));
    }
}

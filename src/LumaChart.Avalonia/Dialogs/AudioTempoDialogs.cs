using LumaChart.Application.Audio;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

internal enum AudioImportMode
{
    Cancel,
    KeepOriginal,
    ConvertToOgg
}

internal enum VariableTempoDialogChoice
{
    Cancel,
    ApplyBpmOnly,
    ApplyWithScrollCompensation
}

internal sealed class AudioImportModeDialog :
    WindowsWpf110ChoiceDialog<AudioImportMode>
{
    internal const string DialogAutomationId = "AudioImport.ModeDialog";

    public AudioImportModeDialog(
        string fileName,
        double uiScale,
        double fontScale)
        : base(
            "导入音频",
            AvaloniaLocalizationCatalog.Format("{0}\n\n是否将音频转换为 OGG？\n\n是：转换为适合 Malody 打包的 OGG\n否：保留原格式并复制到谱面目录\n取消：不导入", fileName),
            WindowsWpf110DialogTone.Question,
            [
                new(
                    AudioImportMode.Cancel,
                    "取消",
                    "AudioImport.Cancel",
                    IsDefault: true,
                    IsCancel: true),
                new(
                    AudioImportMode.KeepOriginal,
                    "否",
                    "AudioImport.KeepOriginal"),
                new(
                    AudioImportMode.ConvertToOgg,
                    "是",
                    "AudioImport.ConvertToOgg")
            ],
            AudioImportMode.Cancel,
            uiScale,
            fontScale,
            DialogAutomationId)
    {
    }
}

internal sealed class SingleTempoResultDialog :
    WindowsWpf110ChoiceDialog<bool>
{
    internal const string DialogAutomationId = "Tempo.SingleResultDialog";
    internal const string MessageAutomationId = "Tempo.SingleResult";

    public SingleTempoResultDialog(
        BeatAnalysisResult result,
        bool canUpdateMetadataBpm,
        double uiScale,
        double fontScale)
        : base(
            "BPM 检测",
            AvaloniaLocalizationCatalog.Format("检测完成\n\nBPM：{0:0.###}\nDelay：{1:0.###} ms\n稳定度：{2:P1}\n置信度：{3:P1}\n\n是否将 BPM 和 Delay 写入第一个时间记录（1:0/1）{4}？", result.Bpm, result.OffsetMilliseconds, 1 - result.Instability, result.Confidence, (canUpdateMetadataBpm
                ? AvaloniaLocalizationCatalog.Text("，并同步更新谱面信息 BPM")
                : AvaloniaLocalizationCatalog.Text("；当前无谱面信息权限，基准 BPM 将保持不变"))),
            WindowsWpf110DialogTone.Question,
            [
                new(false, "否", "Tempo.SingleCancel", IsDefault: true, IsCancel: true),
                new(true, "是", "Tempo.SingleApply")
            ],
            false,
            uiScale,
            fontScale,
            DialogAutomationId,
            MessageAutomationId)
    {
    }
}

internal sealed class VariableTempoResultDialog :
    WindowsWpf110ChoiceDialog<VariableTempoDialogChoice>
{
    internal const string DialogAutomationId = "Tempo.VariableResultDialog";
    internal const string MessageAutomationId = "Tempo.VariablePreview";

    public VariableTempoResultDialog(
        TempoTimelineAnalysisResult result,
        TempoTimelinePlan plan,
        double uiScale,
        double fontScale)
        : base(
            "预测多个 BPM",
            BuildMessage(result, plan),
            WindowsWpf110DialogTone.Question,
            [
                new(
                    VariableTempoDialogChoice.Cancel,
                    "取消",
                    "Tempo.VariableCancel",
                    IsCancel: true),
                new(
                    VariableTempoDialogChoice.ApplyWithScrollCompensation,
                    AvaloniaLocalizationCatalog.Format("写入 {0} 个 BPM + 流速补正", plan.Candidates.Count),
                    "Tempo.VariableCompensate",
                    IsDefault: true),
                new(
                    VariableTempoDialogChoice.ApplyBpmOnly,
                    "仅写入 BPM",
                    "Tempo.VariableApply")
            ],
            VariableTempoDialogChoice.Cancel,
            uiScale,
            fontScale,
            DialogAutomationId,
            MessageAutomationId)
    {
    }

    private static string BuildMessage(
        TempoTimelineAnalysisResult result,
        TempoTimelinePlan plan)
    {
        var preview = string.Join(
            "\n",
            plan.Candidates.Take(20).Select((candidate, index) =>
                AvaloniaLocalizationCatalog.Format("{0}.  音频 {1}  →  {2}  ·  BPM {3:0.###}  ·  {4:P0}", index + 1, FormatTime(candidate.StartSeconds), candidate.Beat, candidate.Bpm, candidate.Confidence)));
        if (plan.Candidates.Count > 20)
            preview += AvaloniaLocalizationCatalog.Format("\n…以及其他 {0} 个候选段", plan.Candidates.Count - 20);
        return
            AvaloniaLocalizationCatalog.Format("候选 BPM 时间线：\n\n{0}\n\n首段 Delay：{1:0.###} ms\n确认后将整体替换现有 {2} 个 BPM 时间记录。\n\n可同时生成流速补正：每段 Scroll = 谱面信息 BPM {3:0.###} ÷ 该段 BPM。{4}", preview, result.Timeline.InitialOffsetMilliseconds, plan.ExistingBpmCount, plan.ReferenceBpm, (plan.ManagedScrollCount > 0
                ? AvaloniaLocalizationCatalog.Format(AvaloniaLocalizationCatalog.Text(" 为保证结果确定且可重复，旧/新 BPM 位置已有的 {0} 个 Scroll 会被补正值替换；其他位置和其他 Effect 不变。"), plan.ManagedScrollCount)
                : AvaloniaLocalizationCatalog.Text(" 其他位置的 Scroll 和所有其他 Effect 不变。")));
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}

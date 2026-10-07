using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Editing;

namespace LumaChart.Avalonia.Dialogs;

internal sealed class NoteTypeTransformDialog :
    WindowsWpf110ChoiceDialog<LongNoteToTapMode?>
{
    internal const string DialogAutomationId = "NoteTransform.Dialog";

    public NoteTypeTransformDialog(
        int longNoteCount,
        int division,
        double uiScale,
        double fontScale)
        : base(
            "面转米",
            AvaloniaLocalizationCatalog.Format("将 {0} 个选中的面转换为米。\n\n带面尾：保留面头与面尾。\n填充：按当前 1/{1} 分度铺满。\n只转面头：只保留面头。", longNoteCount, division),
            WindowsWpf110DialogTone.Question,
            [
                new(null, "取消", "NoteTransform.Cancel", IsCancel: true),
                new(
                    LongNoteToTapMode.IncludeTail,
                    "带面尾",
                    "NoteTransform.IncludeTail",
                    IsDefault: true),
                new(LongNoteToTapMode.Fill, "填充", "NoteTransform.Fill"),
                new(LongNoteToTapMode.HeadOnly, "只转面头", "NoteTransform.HeadOnly")
            ],
            null,
            uiScale,
            fontScale,
            DialogAutomationId)
    {
    }
}

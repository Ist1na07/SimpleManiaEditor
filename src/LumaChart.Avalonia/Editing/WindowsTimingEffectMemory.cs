using LumaChart.Application.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Editing;

internal sealed record WindowsTimingEffectMemorySnapshot(
    TimingEffectKind Effect,
    string ValueText,
    string CustomFieldName);

/// <summary>
/// WPF 1.1.0 keeps the last successfully authored Effect as editor-local state.
/// The next quick Effect entry reuses the exact mapper-authored JSON number text
/// and remembers the last valid Custom field independently of non-Custom effects.
/// </summary>
internal sealed class WindowsTimingEffectMemory
{
    public const string Contract = "wpf-1.1.0-last-effect-memory-r1";

    private TimingEffectKind effect = TimingEffectKind.Scroll;
    private string valueText = "1";
    private string customFieldName = "custom";

    public WindowsTimingEffectMemorySnapshot Current =>
        new(effect, valueText, customFieldName);

    public void Remember(CompleteTimingEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!edit.Effect.HasValue) return;

        effect = edit.Effect.Value;
        valueText = edit.EffectValueText.Trim();
        if (effect == TimingEffectKind.Custom &&
            !string.IsNullOrWhiteSpace(edit.CustomFieldName))
            customFieldName = edit.CustomFieldName.Trim();
    }
}

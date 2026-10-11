using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private bool osuMinimalMode = EditorPresentationSettingsStore.LoadMinimalMode();
    private readonly List<Control> fullModeControls = [];

    private Control FullModeOnly(Control control)
    {
        fullModeControls.Add(control);
        control.IsVisible = !osuMinimalMode;
        return control;
    }

    private Control BuildEditorModeSettings()
    {
        timeline.HideSvEditing = osuMinimalMode;
        var modes = new ComboBox { ItemsSource = new[] { "osu 最小模式", "osu 完整模式" }, SelectedIndex = osuMinimalMode ? 0 : 1,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(modes, "Settings.EditorMode");
        modes.SelectionChanged += (_, _) =>
        {
            osuMinimalMode = modes.SelectedIndex == 0;
            EditorPresentationSettingsStore.SaveMinimalMode(osuMinimalMode);
            foreach (var control in fullModeControls) control.IsVisible = !osuMinimalMode;
            if (quickEffectButton != null) quickEffectButton.IsVisible = !osuMinimalMode;
            RebuildTrackViews();
            timeline.HideSvEditing = osuMinimalMode;
            foreach (var view in secondaryTrackViews.Values) if (view.Surface != null) view.Surface.HideSvEditing = osuMinimalMode;
            timeline.SetSelectedTiming(null);
            timeline.InvalidateVisual();
            RefreshTimingList(force: true);
            UpdateNativeTimingControls(viewModel.ActiveTab?.Session);
        };
        return SettingsSection("编辑模式", "最小模式隐藏 hitsound 与 SV 编辑。已有事件和音效会原样保留，切换模式不会改谱。", "Settings.Mode",
            new StackPanel { Children = { modes } });
    }

    private void FlipVisibleSelection()
    {
        if (TryExecuteTrackCommand("Flip")) return;
        if (viewModel.ActiveTab is not { } tab) return;
        try
        {
            var count = NoteTimeFlipEditingService.FlipSelection(tab.Session, WindowsGroupManagerCommand.VisibleEditableSelectionIds(tab));
            viewModel.SetStatus(count > 0 ? $"已上下翻转 {count} 个音符，可撤销。" : "请先选择可翻转的音符。");
            UpdateSelectionUi();
        }
        catch (InvalidOperationException ex) { viewModel.SetStatus(ex.Message); }
    }
}

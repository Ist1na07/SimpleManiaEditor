using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private void ConfigureBlankClickFocus()
    {
        AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Control source ||
                WindowsAvaloniaEditorInputPolicy.FindTextEditingControl(FocusManager?.GetFocusedElement() as Control) is not TextBox)
                return;
            if (source.GetVisualAncestors().OfType<Control>().Prepend(source).Any(c =>
                c is TextBox or ComboBox or Button or Slider or ScrollBar or MenuItem or ListBoxItem)) return;
            FocusManager?.Focus(null);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private Control BuildGridSnapActions()
    {
        var selected = new Button { Content = "吸附选中", Padding = new(6, 3) };
        var all = new Button { Content = "吸附全部", Padding = new(6, 3) };
        AutomationProperties.SetAutomationId(selected, "Editor.Grid.SnapSelected");
        AutomationProperties.SetAutomationId(all, "Editor.Grid.SnapAll");
        ToolTip.SetTip(all, "将当前轨全部按键和 LN 两端吸附到当前网格，可撤销。短 LN 至少保留一格；重叠交由检查提示。");
        void Apply(bool allNotes)
        {
            var session = activeSecondaryTrack?.Track.Session ?? viewModel.ActiveTab?.Session;
            if (session == null) return;
            try
            {
                var count = NoteGridSnapService.Apply(session, allNotes);
                viewModel.SetStatus($"已吸附 {count} 个按键到 {GridLabel(session.SnapDivision)} 网格");
            }
            catch (Exception ex) { viewModel.SetStatus($"吸附失败：{ex.Message}"); }
        }
        selected.Click += (_, _) => Apply(false); all.Click += (_, _) => Apply(true);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { selected, all } };
    }

    private Control BuildRenderBackendSelector()
    {
        var picker = new ComboBox { ItemsSource = new[] { "自动 · OpenGL 优先", "兼容 · ANGLE / WinUI", "软件渲染" },
            SelectedIndex = (int)EditorPresentationSettingsStore.LoadRenderBackend() };
        AutomationProperties.SetAutomationId(picker, "Settings.RenderBackend");
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0) return;
            EditorPresentationSettingsStore.SaveRenderBackend((EditorRenderBackend)picker.SelectedIndex);
            viewModel.SetStatus("渲染方式已保存，重启编辑器后生效。");
            RefreshOptionResets();
        };
        var field = TimingField("渲染方式（重启生效）", picker);
        AddOptionReset(picker, () => picker.SelectedIndex == 0, () => picker.SelectedIndex = 0);
        EnableOptionWheel(picker);
        return field;
    }
}

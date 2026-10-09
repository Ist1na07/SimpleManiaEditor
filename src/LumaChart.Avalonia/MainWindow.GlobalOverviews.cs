using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Controls;
using LumaChart.Application.Editing;
using Avalonia.Threading;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly StackPanel globalOverviewPanel = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly CheckBox globalDensityBox = new() { Content = "显示全局密度" };
    private readonly CheckBox globalDifficultyBox = new() { Content = "显示全局难度" };
    private Border? globalDensityPanel;
    private Border? globalDifficultyPanel;
    private Control BuildVisiblePanelsMenu()
    {
        var density = new CheckBox { Content = "全局密度", IsChecked = globalDensityBox.IsChecked };
        var difficulty = new CheckBox { Content = "全局难度", IsChecked = globalDifficultyBox.IsChecked };
        AutomationProperties.SetAutomationId(density, "Editor.Views.GlobalDensity");
        AutomationProperties.SetAutomationId(difficulty, "Editor.Views.GlobalDifficulty");
        density.IsCheckedChanged += (_, _) => globalDensityBox.IsChecked = density.IsChecked;
        difficulty.IsCheckedChanged += (_, _) => globalDifficultyBox.IsChecked = difficulty.IsChecked;
        globalDensityBox.IsCheckedChanged += (_, _) => density.IsChecked = globalDensityBox.IsChecked;
        globalDifficultyBox.IsCheckedChanged += (_, _) => difficulty.IsChecked = globalDifficultyBox.IsChecked;
        var reset = new Button { Content = "整理布局：主轨归位" };
        AutomationProperties.SetAutomationId(reset, "Editor.Views.Arrange");
        reset.Click += (_, _) =>
        {
            var main = (EditorTrackPanel)mainTrackFrame!;
            trackPanels.Children.Remove(main); trackPanels.Children.Insert(0, main);
            activeTrackWorkspace?.BoardOrder.Clear(); activeTrackWorkspace?.MarkLayoutChanged();
            RestoreBoardPanAfterLayout(0);
            quickValuesPaletteTransform.X = 0; quickValuesPaletteTransform.Y = 0;
            editorViewSettings = editorViewSettings.WithQuickValuesPalette(0, 0, editorViewSettings.QuickValuesPaletteCollapsed);
            ApplyQuickValuesPaletteLayout(); MarkEditorViewSettingsDirty();
        };
        var button = new Button { Content = "显示 / 布局 ▾", Padding = new Thickness(6, 3) };
        AutomationProperties.SetAutomationId(button, "Editor.Views");
        button.Flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { density, difficulty, reset } } };
        return button;
    }
    private EditorSession? globalLoadSession;
    private long globalLoadSerial = -1;
    private int globalLoadGeneration;
    private void UpdateGlobalLoad(EditorSession? session)
    {
        if (session?.IsInteractiveMutationActive == true) return;
        if (ReferenceEquals(globalLoadSession, session) && globalLoadSerial == (session?.ChangeSerial ?? -1)) return;
        globalLoadSession = session; globalLoadSerial = session?.ChangeSerial ?? -1;
        var request = ++globalLoadGeneration;
        difficultyOverview.SetFallback([]);
        if (session == null) return;
        var snapshot = session.CaptureCommittedDocument();
        _ = Task.Run(() => EditorMetricTrackSurface.Calculate(snapshot, true)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (request != globalLoadGeneration || task.Status != TaskStatus.RanToCompletion) return;
            difficultyOverview.SetFallback(task.Result);
        }));
    }
    private Control BuildGlobalOverview(Control surface, string title, string id, CheckBox toggle)
    {
        var hide = new Button { Content = "×", Padding = new Thickness(3, 0), FontSize = 11 };
        hide.Click += (_, _) => toggle.IsChecked = false;
        ToolTip.SetTip(hide, "隐藏；可在设置中重新显示");
        var header = new DockPanel { Margin = new Thickness(4), LastChildFill = true };
        DockPanel.SetDock(hide, Dock.Right); header.Children.Add(hide);
        header.Children.Add(new TextBlock { Text = title, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
        var body = new Grid { RowDefinitions = new RowDefinitions(id == "Density" ? "32,28,*" : "32,0,*") };
        body.Children.Add(header);
        if (id == "Density")
        {
            DetachControl(navigationMarkerAddButton); DetachControl(navigationMarkerListButton);
            var markers = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 4, Children = { navigationMarkerAddButton, navigationMarkerListButton } };
            Grid.SetRow(markers, 1); body.Children.Add(markers);
        }
        else ToolTip.SetTip(surface, "全局难度：4K Rework 分析结果；无结果时为实验负荷（非官方星级 / Sunny 7K）");
        Grid.SetRow(surface, 2); body.Children.Add(surface);
        var result = new Border { Width = 78, Background = PanelBrush, BorderBrush = StrokeBrush, BorderThickness = new Thickness(1), Child = body };
        AutomationProperties.SetAutomationId(result, "Editor.Global." + id);
        return result;
    }
    private Control BuildGlobalOverviewSettings()
    {
        globalDensityBox.IsChecked = EditorPresentationSettingsStore.LoadGlobalDensity();
        globalDifficultyBox.IsChecked = EditorPresentationSettingsStore.LoadGlobalDifficulty();
        AutomationProperties.SetAutomationId(globalDensityBox, "Settings.GlobalDensity");
        AutomationProperties.SetAutomationId(globalDifficultyBox, "Settings.GlobalDifficulty");
        void Apply()
        {
            if (globalDensityPanel != null) globalDensityPanel.IsVisible = globalDensityBox.IsChecked == true;
            if (globalDifficultyPanel != null) globalDifficultyPanel.IsVisible = globalDifficultyBox.IsChecked == true;
        }
        globalDensityBox.IsCheckedChanged += (_, _) => { Apply(); EditorPresentationSettingsStore.SaveGlobalDensity(globalDensityBox.IsChecked == true); };
        globalDifficultyBox.IsCheckedChanged += (_, _) => { Apply(); EditorPresentationSettingsStore.SaveGlobalDifficulty(globalDifficultyBox.IsChecked == true); };
        Apply();
        return SettingsSection("全局分析", "固定显示整张谱面；局部分析轨随底板滚动。难度暂无结果时显示实验负荷。", "Settings.GlobalAnalysis",
            new StackPanel { Children = { globalDensityBox, globalDifficultyBox } });
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer recoveryCheckpointTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private bool recoveryCheckpointInProgress;
    private string checkpointRevision = "";
    private ComboBox? fixedGridPicker;
    private bool syncingFixedGridPicker;
    private CheckBox? reverseTimeWheelBox;
    private bool positionInspectorRefreshQueued;

    private void QueuePositionInspectorRefresh()
    {
        if (positionInspectorRefreshQueued) return;
        positionInspectorRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            positionInspectorRefreshQueued = false;
            if (!windowCloseTeardownStarted) UpdateInspector();
        }, DispatcherPriority.Background);
    }

    private void InitializeSimpleMania()
    {
        if (initializeFactoryFloatingLayout && shortcutSettings.Profile == EditorInteractionProfile.OsuMania)
            editorViewSettings = editorViewSettings.WithQuickPalette(0, 0, collapsed: true)
                .WithQuickValuesPalette(0, 0, collapsed: true);
        timeline.InteractionProfile = shortcutSettings.Profile;
        timeline.ReverseTimeWheel = shortcutSettings.ReverseTimeWheel;
        recoveryCheckpointTimer.Tick += async (_, _) => await CheckpointRecoveryAsync();
        recoveryCheckpointTimer.Start();
        Closed += (_, _) => recoveryCheckpointTimer.Stop();
    }

    private async Task CheckpointRecoveryAsync()
    {
        if (!initialized || windowClosePreflightInProgress || recoveryCheckpointInProgress) return;
        var revision = string.Join(';', viewModel.Tabs.Select(t => $"{t.Id}:{t.Session.ChangeSerial}"));
        if (revision == checkpointRevision || !viewModel.Tabs.Any(t => t.Session.IsDirty)) return;
        recoveryCheckpointInProgress = true;
        try
        {
            if (await viewModel.PersistAsync()) checkpointRevision = revision;
        }
        catch (Exception exception)
        {
            viewModel.SetStatus($"恢复副本保存失败：{exception.Message}");
        }
        finally { recoveryCheckpointInProgress = false; }
    }

    private Control BuildInteractionProfileSelector()
    {
        var picker = new ComboBox
        {
            ItemsSource = new[] { "osu!mania（默认）", "Luma legacy" },
            SelectedIndex = shortcutSettings.Profile == EditorInteractionProfile.OsuMania ? 0 : 1,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch
        };
        picker.SelectionChanged += (_, _) =>
        {
            var profile = picker.SelectedIndex == 1 ? EditorInteractionProfile.LumaLegacy : EditorInteractionProfile.OsuMania;
            if (profile == shortcutSettings.Profile) return;
            CancelShortcutCapture();
            shortcutSettings.SelectProfile(profile);
            timeline.InteractionProfile = profile;
            timeline.ReverseTimeWheel = shortcutSettings.ReverseTimeWheel;
            if (reverseTimeWheelBox != null) reverseTimeWheelBox.IsChecked = shortcutSettings.ReverseTimeWheel;
            RefreshShortcutButtons();
            RefreshShortcutLabels();
            shortcutSettingsSaveTimer.Stop();
            shortcutSettingsSaveTimer.Start();
            viewModel.SetStatus(profile == EditorInteractionProfile.OsuMania
                ? "osu!mania：1/2/3 工具，Ctrl 多选及滚轮调网格，Alt+滚轮缩放"
                : "已切换到 Luma legacy 操作方案");
        };
        reverseTimeWheelBox = new CheckBox
        {
            Content = "滚轮向下前进时间（osu!editor 习惯）",
            IsChecked = shortcutSettings.ReverseTimeWheel
        };
        reverseTimeWheelBox.IsCheckedChanged += (_, _) =>
        {
            shortcutSettings.ReverseTimeWheel = reverseTimeWheelBox.IsChecked == true;
            timeline.ReverseTimeWheel = shortcutSettings.ReverseTimeWheel;
            shortcutSettingsSaveTimer.Stop();
            shortcutSettingsSaveTimer.Start();
        };
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "操作方案（切换时重置快捷键）", Foreground = Brushes.White },
                picker,
                reverseTimeWheelBox,
                new TextBlock
                {
                    Text = "1/2/3 工具 · Ctrl 多选 · Ctrl+滚轮网格 · Alt+滚轮缩放",
                    Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, FontSize = 11
                }
            }
        };
    }

    private Control BuildFixedGridToolbar()
    {
        fixedGridPicker = new ComboBox
        {
            ItemsSource = QuickSnapDivisions,
            SelectedItem = editorViewSettings.SnapDivision,
            Width = 88,
            ItemTemplate = new FuncDataTemplate<int>((division, _) => new TextBlock { Text = $"1/{division}" })
        };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(fixedGridPicker, "Editor.Grid.Division");
        fixedGridPicker.SelectionChanged += (_, _) =>
        {
            if (!syncingFixedGridPicker && fixedGridPicker.SelectedItem is int division)
            {
                SetQuickSnapDivision(division);
                timeline.Focus();
            }
        };
        return new Border
        {
            Background = RaisedBrush, Padding = new Thickness(10, 5),
            BorderBrush = StrokeBrush, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "网格", Foreground = Brushes.White, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center },
                    fixedGridPicker,
                    new TextBlock { Text = "Ctrl+滚轮调整网格  ·  Alt+滚轮缩放", Foreground = MutedBrush,
                        FontSize = 11, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center }
                }
            }
        };
    }

    private void SyncFixedGridPicker(int division)
    {
        if (fixedGridPicker == null) return;
        syncingFixedGridPicker = true;
        try
        {
            fixedGridPicker.ItemsSource = QuickSnapDivisions.Contains(division)
                ? QuickSnapDivisions : QuickSnapDivisions.Append(division).Order().ToArray();
            fixedGridPicker.SelectedItem = division;
        }
        finally { syncingFixedGridPicker = false; }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using LumaChart.Avalonia.Controls;
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
    private Slider? fixedGridSlider;
    private TextBlock? fixedGridText;
    private Slider? fixedRateSlider;
    private TextBlock? fixedRateText;
    private bool syncingFixedGridPicker;
    private bool syncingFixedRate;
    private bool fixedRatePointerDown;
    private int fixedRateChangesInFlight;
    private readonly Dictionary<TimelineTool, (Button Button, TextBlock Shortcut)> fixedToolButtons = [];
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
        timeline.NoteStyle = EditorPresentationSettingsStore.LoadNoteStyle();
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
                BuildNoteStyleSelector(),
                new TextBlock
                {
                    Text = "Space 固定播放/暂停（文本输入除外） · 1/2/3/4 工具 · Ctrl+滚轮网格 · Alt+滚轮缩放",
                    Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, FontSize = 11
                }
            }
        };
    }

    private Control BuildFixedGridToolbar()
    {
        fixedGridText = new TextBlock { Foreground = Brushes.White, Width = 44, VerticalAlignment = VerticalAlignment.Center };
        fixedGridSlider = new Slider
        {
            Minimum = 0, Maximum = QuickSnapDivisions.Length - 1,
            TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 1,
            Width = 150, VerticalAlignment = VerticalAlignment.Center
        };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(fixedGridSlider, "Editor.Grid.Division");
        ToolTip.SetTip(fixedGridSlider, "拖动选择全部预设；Ctrl+滚轮跳过 1/5、1/7、1/9、1/10");
        fixedGridSlider.ValueChanged += (_, e) =>
        {
            if (!syncingFixedGridPicker)
                SetQuickSnapDivision(QuickSnapDivisions[Math.Clamp((int)Math.Round(e.NewValue), 0, QuickSnapDivisions.Length - 1)]);
        };
        SyncFixedGridPicker(editorViewSettings.SnapDivision);
        fixedRateText = new TextBlock { Foreground = Brushes.White, Width = 48, VerticalAlignment = VerticalAlignment.Center };
        fixedRateSlider = new Slider
        {
            Minimum = .2, Maximum = 2, TickFrequency = .05, IsSnapToTickEnabled = true,
            SmallChange = .05, LargeChange = .1, Value = editorViewSettings.PlaybackRate,
            Width = 135, VerticalAlignment = VerticalAlignment.Center
        };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(fixedRateSlider, "Editor.Playback.Rate");
        fixedRateSlider.ValueChanged += async (_, e) =>
        {
            if (syncingFixedRate) return;
            fixedRateText.Text = $"{e.NewValue:0.00}x";
            if (fixedRatePointerDown) return;
            await ApplyFixedPlaybackRateAsync(e.NewValue);
        };
        fixedRateSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) => fixedRatePointerDown = true, RoutingStrategies.Tunnel);
        fixedRateSlider.AddHandler(InputElement.PointerReleasedEvent, async (_, _) => await CommitFixedPlaybackRateAsync(), RoutingStrategies.Tunnel);
        fixedRateSlider.PointerCaptureLost += async (_, _) => await CommitFixedPlaybackRateAsync();
        SyncFixedPlaybackRate();
        var playtest = CommandButton("▶ 试玩", async (_, _) => await StartPlaytestAsync(timeline.CurrentBeat, autoStart: true));
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(playtest, "Editor.Toolbar.Playtest");
        ToolTip.SetTip(playtest, "从当前时间位置立即开始试玩");
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { playtest, ToolbarLabel("速度"), fixedRateSlider, fixedRateText }
        });
        var snap = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { ToolbarLabel("网格"), fixedGridSlider, fixedGridText }
        };
        Grid.SetColumn(snap, 2);
        content.Children.Add(snap);
        return new Border
        {
            Background = RaisedBrush, Padding = new Thickness(10, 6), MinHeight = 48,
            BorderBrush = StrokeBrush, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = content
        };
    }

    private static TextBlock ToolbarLabel(string text) => new()
    {
        Text = text, Foreground = MutedBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
    };

    private Control BuildNoteStyleSelector()
    {
        var picker = new ComboBox
        {
            ItemsSource = new[] { "osu style（默认）", "malody style" },
            SelectedIndex = timeline.NoteStyle == EditorNoteStyle.Malody ? 1 : 0
        };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(picker, "Editor.NoteStyle");
        picker.SelectionChanged += (_, _) =>
        {
            timeline.NoteStyle = picker.SelectedIndex == 1 ? EditorNoteStyle.Malody : EditorNoteStyle.Osu;
            timeline.InvalidateVisual();
            try { EditorPresentationSettingsStore.SaveNoteStyle(timeline.NoteStyle); }
            catch (Exception exception) { viewModel.SetStatus($"皮肤设置保存失败：{exception.Message}"); }
        };
        return new StackPanel { Spacing = 5, Children = { ToolbarLabel("编辑器皮肤"), picker } };
    }

    private Control BuildFixedToolRail()
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(6, 12, 8, 8) };
        foreach (var (tool, text) in new[] { (TimelineTool.Select, "选择"), (TimelineTool.Tap, "单键"),
            (TimelineTool.Hold, "长条"), (TimelineTool.Erase, "擦除") })
        {
            var shortcut = new TextBlock { FontSize = 11, Foreground = MutedBrush, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6, 0, 0, 0) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock { Text = text, FontSize = 12 });
            Grid.SetColumn(shortcut, 1);
            row.Children.Add(shortcut);
            var button = new Button { Content = row, Height = 38, Padding = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(3) };
            global::Avalonia.Automation.AutomationProperties.SetAutomationId(button, $"Editor.ToolRail.{tool}");
            button.Click += (_, _) => SetTimelineToolFromShortcut(tool);
            fixedToolButtons.Add(tool, (button, shortcut));
            panel.Children.Add(button);
        }
        RefreshFixedTools();
        return new Border { Background = PanelBrush, BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 0, 1, 0), Child = panel };
    }

    private void RefreshFixedTools()
    {
        foreach (var (tool, controls) in fixedToolButtons)
        {
            var selected = timeline.Tool == tool;
            controls.Button.Background = selected ? AccentBrush : RaisedBrush;
            controls.Button.Foreground = selected ? Brushes.Black : Brushes.White;
            controls.Shortcut.Foreground = selected ? Brushes.Black : MutedBrush;
            controls.Button.BorderBrush = selected ? AccentBrush : StrokeBrush;
            controls.Button.IsEnabled = WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(viewModel.ActiveTab?.Session, tool);
            var gesture = CompactShortcut("Tool" + tool);
            controls.Shortcut.Text = gesture.Length == 2 && gesture[0] == 'D' && char.IsDigit(gesture[1]) ? gesture[1..] : gesture;
            ToolTip.SetTip(controls.Button, $"{ShortcutLabel("Tool" + tool)} · {controls.Shortcut.Text}");
        }
    }

    private void SyncFixedPlaybackRate()
    {
        if (fixedRateSlider == null || fixedRateText == null || fixedRatePointerDown || fixedRateChangesInFlight > 0) return;
        syncingFixedRate = true;
        try { fixedRateSlider.Value = audioTransport.PlaybackRate; fixedRateText.Text = $"{audioTransport.PlaybackRate:0.00}x"; }
        finally { syncingFixedRate = false; }
    }

    private async Task CommitFixedPlaybackRateAsync()
    {
        if (!fixedRatePointerDown || fixedRateSlider == null) return;
        fixedRatePointerDown = false;
        await ApplyFixedPlaybackRateAsync(fixedRateSlider.Value);
    }

    private async Task ApplyFixedPlaybackRateAsync(double value)
    {
        fixedRateChangesInFlight++;
        try { await SetQuickPlaybackRateAsync(value); }
        catch (Exception exception) { viewModel.SetStatus($"修改播放速度失败：{exception.Message}"); }
        finally
        {
            fixedRateChangesInFlight--;
            SyncFixedPlaybackRate();
        }
    }

    private void SyncFixedGridPicker(int division)
    {
        if (fixedGridSlider == null || fixedGridText == null) return;
        syncingFixedGridPicker = true;
        try
        {
            var index = Array.IndexOf(QuickSnapDivisions, division);
            fixedGridSlider.Value = index >= 0 ? index : Array.FindLastIndex(QuickSnapDivisions, value => value < division);
            fixedGridText.Text = $"1/{division}";
        }
        finally { syncingFixedGridPicker = false; }
    }
}

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
    private ComboBox? fixedGridPicker;
    private ComboBox? fixedRatePicker;
    private EditorGridNotation gridNotation;
    private readonly double[] workbenchRates = [.2, .25, .5, .75, .9, 1, 1.1, 1.25, 1.5, 2];
    private readonly double[] workbenchZooms = [12, 24, 70, 140, 280, 480, 960, 1920, 3840];
    private Slider? adjustmentZoomSlider;
    private TextBlock? adjustmentZoomText;
    private StackPanel? fixedToolRailBody;
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
        var layoutRevision = EditorPresentationSettingsStore.LoadLayoutRevision();
        if (layoutRevision < 3)
        {
            editorViewSettings = editorViewSettings.WithPreservePitch(true).WithView(editorViewSettings.GridLaneWidth,
                editorViewSettings.GridBeatHeight, editorViewSettings.MainEditorHorizontalOffset, editorViewSettings.NoteWidth,
                editorViewSettings.NoteHeight == 41 ? 32 : editorViewSettings.NoteHeight, editorViewSettings.NoteOpacity,
                editorViewSettings.JudgementLinePosition, editorViewSettings.JudgementLineThickness, editorViewSettings.JudgementLineOpacity);
            WindowsEditorViewSettingsStore.Save(editorViewSettings);
            EditorPresentationSettingsStore.SaveLayoutRevision(3);
        }
        if (layoutRevision < 2)
        {
            editorViewSettings = editorViewSettings.WithQuickPalette(0, 0, collapsed: false)
                .WithQuickValuesPalette(0, 0, collapsed: false).WithDifficultyCard(0, 0, collapsed: false);
            audioPanelSettings = audioPanelSettings.WithPanels(audioPanelSettings.Waveform.WithCollapsed(false),
                audioPanelSettings.Spectrogram.WithCollapsed(false));
            WindowsEditorViewSettingsStore.Save(editorViewSettings);
            WindowsAudioPanelSettingsStore.Save(audioPanelSettings);
            EditorPresentationSettingsStore.SaveLayoutRevision(3);
        }
        if (layoutRevision < 4)
        {
            editorViewSettings = editorViewSettings.WithView(editorViewSettings.GridLaneWidth,
                editorViewSettings.GridBeatHeight, editorViewSettings.MainEditorHorizontalOffset, editorViewSettings.NoteWidth,
                editorViewSettings.NoteHeight == 32 ? WindowsEditorViewSettings.DefaultNoteHeight : editorViewSettings.NoteHeight,
                editorViewSettings.NoteOpacity, editorViewSettings.JudgementLinePosition,
                editorViewSettings.JudgementLineThickness, editorViewSettings.JudgementLineOpacity);
            WindowsEditorViewSettingsStore.Save(editorViewSettings);
            EditorPresentationSettingsStore.SaveLayoutRevision(4);
        }
        gridNotation = EditorPresentationSettingsStore.LoadGridNotation();
        timeline.InteractionProfile = shortcutSettings.Profile;
        timeline.ReverseTimeWheel = shortcutSettings.ReverseTimeWheel;
        timeline.NoteStyle = EditorPresentationSettingsStore.LoadNoteStyle();
        timeline.PlaybackRateChangeRequested += async (_, direction) => await AdjustWorkbenchRateAsync(direction);
        recoveryCheckpointTimer.Tick += async (_, _) => await CheckpointRecoveryAsync();
        recoveryCheckpointTimer.Start();
        Closed += (_, _) => recoveryCheckpointTimer.Stop();
    }

    private async Task CheckpointRecoveryAsync()
    {
        try { await SaveTrackWorkspacesAsync(); }
        catch (Exception exception) { viewModel.SetStatus($"轨道恢复副本保存失败：{exception.Message}"); return; }
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
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(picker, "Editor.InteractionProfile");
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
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(reverseTimeWheelBox, "Editor.ReverseTimeWheel");
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "操作方案（切换时重置快捷键）", Foreground = Brushes.White },
                picker,
                reverseTimeWheelBox,
                BuildGridNotationSelector(),
                new TextBlock
                {
                    Text = "Space 播放/暂停 · 1/2/3/4 工具 · Ctrl+滚轮网格 · Shift+滚轮倍速 · Alt+滚轮缩放",
                    Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, FontSize = 11
                }
            }
        };
    }

    private Control BuildFixedGridToolbar()
    {
        fixedGridPicker = new ComboBox { Width = 100, ItemsSource = QuickSnapDivisions,
            ItemTemplate = GridOptionTemplate() };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(fixedGridPicker, "Editor.Grid.Division");
        fixedGridPicker.SelectionChanged += (_, _) =>
        {
            if (!syncingFixedGridPicker && fixedGridPicker.SelectedItem is int division) SetQuickSnapDivision(division);
        };
        fixedRatePicker = new ComboBox { Width = 100, ItemsSource = workbenchRates,
            ItemTemplate = new FuncDataTemplate<double>((rate, _) => new TextBlock { Text = $"{rate:0.##}x" }) };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(fixedRatePicker, "Editor.Playback.Rate");
        fixedRatePicker.SelectionChanged += async (_, _) =>
        {
            if (!syncingFixedRate && fixedRatePicker.SelectedItem is double rate) await ApplyFixedPlaybackRateAsync(rate);
        };
        SyncFixedGridPicker(editorViewSettings.SnapDivision);
        SyncFixedPlaybackRate();
        var playtest = CommandButton("▶ 试玩", async (_, _) => await StartPlaytestAsync(timeline.CurrentBeat));
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(playtest, "Editor.Toolbar.Playtest");
        ToolTip.SetTip(playtest, "官方 osu!mania 试玩 · 设置与导入位于右侧设置栏");
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { playtest, ToolbarLabel("倍速"), fixedRatePicker, ToolbarLabel("网格"), fixedGridPicker } };
        return new Border { Background = RaisedBrush, Padding = new Thickness(10, 6), MinHeight = 48,
            BorderBrush = StrokeBrush, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid { Children = { controls } } };
    }

    private FuncDataTemplate<int> GridOptionTemplate() => new((division, _) => new TextBlock { Text = GridLabel(division) });
    private string GridLabel(int division) => EditorGridNotationPolicy.Label(division, gridNotation);

    private Control BuildGridNotationSelector()
    {
        var picker = new ComboBox { ItemsSource = new[] { "osu style：每拍切分", "malody / general：全音符分数" },
            SelectedIndex = gridNotation == EditorGridNotation.General ? 1 : 0 };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(picker, "Editor.GridNotation");
        picker.SelectionChanged += (_, _) =>
        {
            gridNotation = picker.SelectedIndex == 1 ? EditorGridNotation.General : EditorGridNotation.Osu;
            try { EditorPresentationSettingsStore.SaveGridNotation(gridNotation); }
            catch (Exception exception) { viewModel.SetStatus($"网格样式保存失败：{exception.Message}"); }
            RefreshGridNotation();
        };
        return new StackPanel { Spacing = 5, Children = { ToolbarLabel("网格记法（osu 1/4 = general 1/16）"), picker } };
    }

    private void RefreshGridNotation()
    {
        if (fixedGridPicker != null) fixedGridPicker.ItemTemplate = GridOptionTemplate();
        divisionPicker.ItemTemplate = GridOptionTemplate();
        foreach (var box in new[] { settingsSnapBox, settingsFixedSnapBox })
            foreach (var item in box.Items.OfType<ComboBoxItem>())
                if (item.Tag is int division) item.Content = GridLabel(division);
        SyncSettingsSnapBox(viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision);
        SyncSettingsFixedSnapBox(editorViewSettings.FixedSnapDivision);
        RefreshQuickValuesPaletteValues();
        RefreshAdjustmentGridTicks();
    }

    private async Task AdjustWorkbenchRateAsync(int direction)
    {
        var current = fixedRateChangesInFlight > 0 ? requestedWorkbenchRate : audioTransport.PlaybackRate;
        var target = direction > 0 ? workbenchRates.FirstOrDefault(r => r > current + .0001, workbenchRates[^1])
            : workbenchRates.LastOrDefault(r => r < current - .0001, workbenchRates[0]);
        await ApplyFixedPlaybackRateAsync(target);
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
        fixedToolRailBody = panel;
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
            BorderThickness = new Thickness(0, 0, 1, 0), Child = new ScrollViewer { Content = panel,
                HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
    }

    private void RefreshFixedTools()
    {
        foreach (var (tool, controls) in fixedToolButtons)
        {
            var selected = (activeSecondaryTrack?.Surface?.Tool ?? timeline.Tool) == tool;
            controls.Button.Background = selected ? AccentBrush : RaisedBrush;
            controls.Button.Foreground = selected ? Brushes.Black : Brushes.White;
            controls.Shortcut.Foreground = selected ? Brushes.Black : MutedBrush;
            controls.Button.BorderBrush = selected ? AccentBrush : StrokeBrush;
            controls.Button.IsEnabled = WindowsAvaloniaEditorInputPolicy.CanUseTimelineTool(activeSecondaryTrack?.Track.Session ?? viewModel.ActiveTab?.Session, tool);
            var gesture = CompactShortcut("Tool" + tool);
            controls.Shortcut.Text = gesture.Length == 2 && gesture[0] == 'D' && char.IsDigit(gesture[1]) ? gesture[1..] : gesture;
            ToolTip.SetTip(controls.Button, $"{ShortcutLabel("Tool" + tool)} · {controls.Shortcut.Text}");
        }
        RefreshTrackCommandButtons();
    }

    private void SyncFixedPlaybackRate()
    {
        if (fixedRatePointerDown || fixedRateChangesInFlight > 0) return;
        syncingFixedRate = true;
        try
        {
            var rate = audioTransport.PlaybackRate;
            if (fixedRateSlider != null) fixedRateSlider.Value = NearestPreset(workbenchRates, rate);
            if (fixedRateText != null) fixedRateText.Text = $"{rate:0.##}x";
            if (fixedRatePicker != null)
            {
                fixedRatePicker.ItemsSource = workbenchRates.Contains(rate) ? workbenchRates : workbenchRates.Append(rate).Order().ToArray();
                fixedRatePicker.SelectedItem = rate;
            }
        }
        finally { syncingFixedRate = false; RefreshOptionResets(); }
    }

    private async Task CommitFixedPlaybackRateAsync()
    {
        if (!fixedRatePointerDown || fixedRateSlider == null) return;
        fixedRatePointerDown = false;
        await ApplyFixedPlaybackRateAsync(workbenchRates[(int)Math.Round(fixedRateSlider.Value)]);
    }

    private async Task ApplyFixedPlaybackRateAsync(double value)
    {
        fixedRateChangesInFlight++;
        requestedWorkbenchRate = value;
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
        syncingFixedGridPicker = true;
        try
        {
            var index = Array.IndexOf(QuickSnapDivisions, division);
            if (fixedGridSlider != null) fixedGridSlider.Value = Math.Max(0, index >= 0 ? index : Array.FindLastIndex(QuickSnapDivisions, value => value < division));
            if (fixedGridText != null) fixedGridText.Text = GridLabel(division);
            if (fixedGridPicker != null)
            {
                fixedGridPicker.ItemsSource = QuickSnapDivisions.Contains(division) ? QuickSnapDivisions : QuickSnapDivisions.Append(division).Order().ToArray();
                fixedGridPicker.SelectedItem = division;
            }
        }
        finally { syncingFixedGridPicker = false; RefreshOptionResets(); }
    }
}

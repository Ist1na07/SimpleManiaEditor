using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly List<Action> optionResetRefreshers = [];
    private readonly Dictionary<Control, Grid> optionResetHeaders = [];
    private Button? shortcutResetAllButton;

    private void ConfigureEditorOptionControls()
    {
        foreach (var control in this.GetLogicalDescendants().OfType<Control>().Distinct().ToArray())
        {
            if (control is ComboBox picker) EnableOptionWheel(picker);
            else if (control is Slider slider && slider != audioPositionSlider) EnableOptionWheel(slider);
        }
        var defaults = WindowsEditorViewSettings.Default;
        foreach (var (slider, value) in new (Slider, double)[]
        {
            (gridLaneWidthSlider, defaults.GridLaneWidth), (gridBeatHeightSlider, defaults.GridBeatHeight),
            (mainEditorHorizontalOffsetSlider, 0), (noteWidthSlider, defaults.NoteWidth), (noteHeightSlider, defaults.NoteHeight),
            (noteOpacitySlider, 100), (trackOpacitySlider, WindowsTrackTransparencyPolicy.ToTransparency(defaults.TrackOpacity) * 100),
            (backgroundDarknessSlider, WindowsEditorBackgroundSettings.DefaultDarkness * 100),
            (barLineThicknessSlider, defaults.BarLineThickness), (subdivisionLineThicknessSlider, defaults.SubdivisionLineThickness),
            (judgementLinePositionSlider, defaults.JudgementLinePosition * 100),
            (judgementLineThicknessSlider, defaults.JudgementLineThickness), (judgementLineOpacitySlider, defaults.JudgementLineOpacity * 100),
            (spectrogramStrengthSlider, WindowsAudioPanelSettings.DefaultBackgroundSpectrogramStrength * 100),
            (spectrogramDetailSlider, WindowsAudioPanelSettings.DefaultSpectrogramDetail * 100),
            (uiScaleSlider, 100), (fontScaleSlider, 100), (autoBackupIntervalSlider, defaults.AutoBackupMinutes),
            (audioVolumeSlider, defaults.MusicVolume * 100), (audioRateSlider, 1),
            (keySoundVolumeSlider, WindowsKeySoundSettings.DefaultVolume * 100),
            (keySoundGlobalOffsetSlider, 0), (keySoundChartOffsetSlider, 0)
        }) AddOptionReset(slider, () => Close(slider.Value, Math.Clamp(value, slider.Minimum, slider.Maximum)),
            () => slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum));

        AddOptionReset(proGridColumnSlider, () => Close(proGridColumnSlider.Value, Math.Max(proGridColumnSlider.Minimum, AvaloniaAppPaths.DefaultProGridColumns)),
            () => proGridColumnSlider.Value = Math.Max(proGridColumnSlider.Minimum, AvaloniaAppPaths.DefaultProGridColumns));
        foreach (var (box, value) in new (CheckBox?, bool)[]
        {
            (snapAllBox, true), (autoBackupEnabledBox, true), (audioPreservePitchBox, true), (reverseTimeWheelBox, true),
            (keySoundEnabledBox, false), (keySoundLongNoteEndBox, false),
            (waveformBackgroundBox, false), (spectrogramBackgroundBox, false),
            (cacheBackupFilesBox, false), (cacheEditingFilesBox, false), (cacheResourceFilesBox, false)
        }) if (box != null) AddOptionReset(box, () => box.IsChecked == value, () => box.IsChecked = value);

        foreach (var picker in this.GetLogicalDescendants().OfType<ComboBox>().Distinct().ToArray())
        {
            var id = AutomationProperties.GetAutomationId(picker);
            if (id is "Editor.NoteStyle" or "Editor.GridNotation" or "Editor.InteractionProfile" or "Settings.Language" or "KeySound.Preset")
                AddOptionReset(picker, () => picker.SelectedIndex == 0, () => picker.SelectedIndex = 0);
        }
        AddOptionReset(frameRateLimitBox, () => EffectiveFrameRateLimit() == defaults.FrameRateLimit, () =>
            frameRateLimitBox.SelectedItem = frameRateLimitBox.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, defaults.FrameRateLimit)));
        AddOptionReset(settingsSnapBox, () => (viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision) == defaults.SnapDivision,
            () => SetQuickSnapDivision(defaults.SnapDivision));
        AddOptionReset(settingsFixedSnapBox, () => editorViewSettings.FixedSnapDivision == defaults.FixedSnapDivision,
            () => settingsFixedSnapBox.SelectedItem = settingsFixedSnapBox.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, defaults.FixedSnapDivision)));
        if (fixedGridPicker != null) AddOptionReset(fixedGridPicker,
            () => (viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision) == defaults.SnapDivision, () => SetQuickSnapDivision(defaults.SnapDivision));
        if (fixedGridSlider != null) AddOptionReset(fixedGridSlider,
            () => (viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision) == defaults.SnapDivision, () => SetQuickSnapDivision(defaults.SnapDivision));
        if (adjustmentZoomSlider != null) AddOptionReset(adjustmentZoomSlider,
            () => Close(timeline.PixelsPerBeat, defaults.GridBeatHeight), () => timeline.SetZoom(defaults.GridBeatHeight));
        foreach (var rate in new Control?[] { fixedRatePicker, fixedRateSlider }) if (rate != null)
            AddOptionReset(rate, () => Close(audioTransport.PlaybackRate, 1), () => _ = ApplyFixedPlaybackRateAsync(1));
        if (playtestRatePicker != null) AddOptionReset(playtestRatePicker, () => Close(playtestRate, 1), () => playtestRatePicker.SelectedItem = 1d);
        if (anchorLineButton.Parent is Control anchors)
            AddOptionReset(anchors, () => editorViewSettings.NoteAnchor == defaults.NoteAnchor, () =>
            {
                editorViewSettings = editorViewSettings.WithNoteAnchor(defaults.NoteAnchor);
                ApplyEditorViewSettings(); RefreshEditorViewSettingsUi(); MarkEditorViewSettingsDirty(); RefreshOptionResets();
            }, "Settings.NoteAnchor");
        foreach (var pair in shortcutButtons)
        {
            var action = pair.Key;
            AddOptionReset(pair.Value, () => shortcutSettings.Bindings.GetValueOrDefault(action) == WindowsEditorShortcutSettings.CreateDefaults(shortcutSettings.Profile)[action], () =>
            {
                shortcutSettings.Assign(action, WindowsEditorShortcutSettings.CreateDefaults(shortcutSettings.Profile)[action], replaceConflict: true);
                MarkShortcutSettingsDirty(); RefreshShortcutButtons(); RefreshShortcutLabels(); RefreshOptionResets();
            });
        }
        AddOptionReset(keySoundCustomFileText, () => keySoundSettings.CustomFilePath == null, () => ApplyKeySoundSettings(new WindowsKeySoundSettings(
            keySoundSettings.EnableKeySounds, keySoundSettings.Volume, keySoundSettings.Preset, null,
            keySoundSettings.PlayLongNoteEndSound, keySoundSettings.GlobalOffset, keySoundSettings.PerChartOffsets)), "KeySound.CustomFilePath");
        foreach (var (box, initial, step, minimum, maximum) in new (TextBox, double, double, double, double)[]
        {
            (metadataOverallDifficultyBox, 8, .1, 0, 10), (metadataHpDrainRateBox, 8, .1, 0, 10),
            (metadataBpmBox, 120, 1, 1, ChartDocument.MaximumMetadataBpm),
            (metadataOffsetBox, 0, 1, -1_000_000, 1_000_000), (metadataPreviewTimeBox, 0, 100, -1, int.MaxValue)
        })
        {
            box.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
            {
                if (!box.IsEnabled || e.Delta.Y == 0 || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;
                box.Text = Math.Round(Math.Clamp(value + Math.Sign(e.Delta.Y) * step, minimum, maximum), 3).ToString("0.###", CultureInfo.InvariantCulture);
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            AddOptionReset(box, () => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && Close(value, initial),
                () => box.Text = initial.ToString(CultureInfo.InvariantCulture));
        }
        trackBoardScrollbar.SmallChange = 48;
        trackBoardScrollbar.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            var delta = e.Delta.X != 0 ? e.Delta.X : -e.Delta.Y;
            if (delta == 0) return;
            trackBoardScrollbar.Value = Math.Clamp(trackBoardScrollbar.Value + delta * 48, 0, trackBoardScrollbar.Maximum);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddOptionReset(trackBoardScrollbar, () => Close(trackBoardScrollbar.Value, boardEdgePadding), () => trackBoardScrollbar.Value = boardEdgePadding);
        RefreshOptionResets();
    }

    private static bool Close(double first, double second) => Math.Abs(first - second) < .0001;

    private static void EnableOptionWheel(Control control)
    {
        control.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!control.IsEnabled || e.Delta.Y == 0) return;
            var steps = Math.Sign(e.Delta.Y) * Math.Max(1, (int)Math.Abs(e.Delta.Y));
            if (control is ComboBox picker)
            {
                if (picker.IsDropDownOpen || picker.Items.Count == 0) return;
                picker.SelectedIndex = Math.Clamp(picker.SelectedIndex + steps, 0, picker.Items.Count - 1);
            }
            else if (control is Slider slider)
                slider.Value = Math.Clamp(slider.Value + steps * slider.SmallChange, slider.Minimum, slider.Maximum);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private void AddOptionReset(Control control, Func<bool> isDefault, Action reset, string? id = null)
    {
        if (control.Parent is not Control parent) return;
        var row = Grid.GetRow(control); var column = Grid.GetColumn(control);
        var rowSpan = Grid.GetRowSpan(control); var columnSpan = Grid.GetColumnSpan(control);
        var wrapper = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 3, MinWidth = 0 };
        var restore = new Button { Content = "↶", Padding = new Thickness(3, 1), MinWidth = 22, MinHeight = 22,
            VerticalAlignment = VerticalAlignment.Center, Background = RaisedBrush, Foreground = MutedBrush };
        id ??= AutomationProperties.GetAutomationId(control);
        AutomationProperties.SetAutomationId(restore, id + ".Reset");
        AutomationProperties.SetName(restore, "恢复默认值"); ToolTip.SetTip(restore, "恢复默认值");
        void Refresh() { restore.IsVisible = !isDefault(); restore.IsEnabled = control.IsEnabled; }
        restore.Click += (_, _) => { reset(); RefreshOptionResets(); };
        control.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name is "Value" or "SelectedIndex" or "SelectedItem" or "Text" or "IsChecked" or "IsEnabled" or "Content") Refresh();
        };
        if (optionResetHeaders.TryGetValue(control, out var header))
        {
            header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(restore, 2); header.Children.Add(restore);
            optionResetRefreshers.Add(Refresh); Refresh(); return;
        }
        if (parent is Panel panel)
        {
            var index = panel.Children.IndexOf(control); panel.Children.RemoveAt(index); panel.Children.Insert(index, wrapper);
        }
        else if (parent is Decorator decorator) decorator.Child = wrapper;
        else if (parent is ContentControl content) content.Content = wrapper;
        else return;
        Grid.SetRow(wrapper, row); Grid.SetColumn(wrapper, column); Grid.SetRowSpan(wrapper, rowSpan); Grid.SetColumnSpan(wrapper, columnSpan);
        Grid.SetRow(control, 0); Grid.SetColumn(control, 0); Grid.SetRowSpan(control, 1); Grid.SetColumnSpan(control, 1);
        wrapper.Children.Add(control); Grid.SetColumn(restore, 1); wrapper.Children.Add(restore);
        optionResetRefreshers.Add(Refresh); Refresh();
    }

    private void RefreshOptionResets()
    {
        foreach (var refresh in optionResetRefreshers) refresh();
        if (shortcutResetAllButton != null) shortcutResetAllButton.IsVisible = shortcutSettings.Bindings.Any(pair =>
            WindowsEditorShortcutSettings.CreateDefaults(shortcutSettings.Profile).GetValueOrDefault(pair.Key) != pair.Value);
    }
}

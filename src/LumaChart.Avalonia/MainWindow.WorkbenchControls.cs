using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using LumaChart.Avalonia.Controls;
using LumaChart.Application.Editing;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private double requestedWorkbenchRate = 1;
    private bool syncingAdjustmentZoom;
    private Grid? adjustmentGridTicks;
    private Control? timingMetadataSection;

    private Control BuildUnifiedTopToolbar(Border commands, Control presets)
    {
        commands.IsVisible = true; commands.Margin = new Thickness(0); commands.Padding = new Thickness(0);
        commands.HorizontalAlignment = HorizontalAlignment.Stretch; commands.MinWidth = 0; commands.ClipToBounds = true;
        Grid.SetColumn(commands, 0); Grid.SetColumnSpan(commands, 1); Grid.SetRow(commands, 0); Grid.SetRowSpan(commands, 1);
        commands.Background = Brushes.Transparent; commands.BorderThickness = new Thickness(0);
        if (commands.Child is ScrollViewer scroll) scroll.MaxWidth = double.PositiveInfinity;
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        content.Children.Add(commands); Grid.SetColumn(presets, 1); content.Children.Add(presets);
        var bar = new Border { Background = RaisedBrush, Padding = new Thickness(8, 4), MinHeight = 48,
            BorderBrush = StrokeBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = content };
        AutomationProperties.SetAutomationId(bar, "Editor.Toolbar.Fixed");
        return bar;
    }

    private void ApplyMetadataCategory()
    {
        var bpm = metadataBpmBox.Text;
        var offset = metadataOffsetBox.Text;
        try { ApplyMetadataEditor(includeTiming: false); }
        finally
        {
            // The legacy form's refresh covers both categories. Keep an unsubmitted
            // Timing draft while applying only metadata from its own category.
            metadataBpmBox.Text = bpm;
            metadataOffsetBox.Text = offset;
            if (viewModel.ActiveTab is { } tab)
                metadataEditorDirty = CaptureMetadataEditorDraft(tab) != ChartMetadataEditingService.Capture(tab.Session.Document);
        }
    }

    private void ApplyTimingMetadata()
    {
        if (viewModel.ActiveTab is not { } tab) return;
        if (!double.TryParse(metadataOffsetBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offset) || !double.IsFinite(offset) ||
            !double.TryParse(metadataBpmBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bpm) || !double.IsFinite(bpm) || bpm <= 0)
        { viewModel.SetStatus("请输入有效 BPM 和毫秒 Offset，再点击应用。"); return; }
        metadataOffsetBox.Text = FormatTimingMilliseconds(offset);
        var pendingForm = CaptureMetadataEditorDraft(tab);
        var draft = ChartMetadataEditingService.Capture(tab.Session.Document) with
            { Bpm = metadataBpmBox.Text ?? "", Offset = ReadTimingOffsetDraft(tab) };
        if (!ChartMetadataEditingService.TryParseDraftForLocalPreservation(draft, tab.Session.Document, out var edit, out var error) || edit == null)
        { viewModel.SetStatus(error); return; }
        try
        {
            var result = ChartMetadataEditingService.Apply(tab.Session, edit, enforceCommunityRules: false);
            var authoritative = ChartMetadataEditingService.Capture(tab.Session.Document);
            var preserved = result.Status == ChartMetadataApplyStatus.Denied ? pendingForm :
                pendingForm with { Bpm = authoritative.Bpm, Offset = authoritative.Offset };
            PopulateMetadataEditorDraft(tab, preserved);
            metadataEditorBaseline = authoritative;
            metadataEditorChangeSerial = tab.Session.ChangeSerial;
            metadataEditorDirty = preserved != authoritative;
            viewModel.SetStatus(result.Message);
        }
        catch (Exception exception) { viewModel.SetStatus($"Timing 基础信息未应用：{exception.Message}"); }
    }

    private Control BuildAdjustmentSliders()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };
        panel.Children.Add(PresetAdjustment("网格", adjustmentGridDivisions.Length, out fixedGridSlider, out fixedGridText,
            index => GridLabel(adjustmentGridDivisions[index]), "Editor.Adjustment.Grid", out adjustmentGridTicks));
        fixedGridSlider!.ValueChanged += (_, e) =>
        {
            if (!syncingFixedGridPicker) SetQuickSnapDivision(adjustmentGridDivisions[(int)Math.Round(e.NewValue)]);
        };
        panel.Children.Add(PresetAdjustment("缩放", workbenchZooms.Length, out adjustmentZoomSlider, out adjustmentZoomText,
            index => $"{workbenchZooms[index] / 140:0.##}x", "Editor.Adjustment.Zoom", out _));
        adjustmentZoomSlider!.ValueChanged += (_, e) =>
        {
            if (!syncingAdjustmentZoom) timeline.SetZoom(InterpolatePreset(workbenchZooms, e.NewValue));
        };
        panel.Children.Add(PresetAdjustment("倍速", workbenchRates.Length, out fixedRateSlider, out fixedRateText,
            index => $"{workbenchRates[index]:0.##}", "Editor.Adjustment.Rate", out _));
        fixedRateSlider!.ValueChanged += async (_, e) =>
        {
            if (syncingFixedRate) return;
            var value = InterpolatePreset(workbenchRates, e.NewValue);
            fixedRateText!.Text = $"{value:0.##}x";
            if (!fixedRatePointerDown) await ApplyFixedPlaybackRateAsync(value);
        };
        fixedRateSlider.AddHandler(InputElement.PointerPressedEvent, (_, _) => fixedRatePointerDown = true, RoutingStrategies.Tunnel);
        fixedRateSlider.AddHandler(InputElement.PointerReleasedEvent, async (_, _) => await CommitFixedPlaybackRateAsync(), RoutingStrategies.Tunnel);
        fixedRateSlider.PointerCaptureLost += async (_, _) => await CommitFixedPlaybackRateAsync();
        SyncFixedGridPicker(viewModel.ActiveTab?.Session.SnapDivision ?? editorViewSettings.SnapDivision);
        SyncAdjustmentZoom();
        SyncFixedPlaybackRate();
        return panel;
    }

    private Control PresetAdjustment(string title, int count, out Slider slider, out TextBlock value,
        Func<int, string> label, string id, out Grid ticks)
    {
        value = new TextBlock { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(ToolbarLabel(title));
        Grid.SetColumn(value, 1);
        header.Children.Add(value);
        slider = new Slider { Minimum = 0, Maximum = count - 1, TickFrequency = 1,
            TickPlacement = TickPlacement.BottomRight, IsSnapToTickEnabled = title == "网格", SmallChange = 1, LargeChange = 1, Height = 30 };
        AutomationProperties.SetAutomationId(slider, id);
        optionResetHeaders[slider] = header;
        ticks = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", count))) };
        for (var index = 0; index < count; index++)
        {
            var captured = index;
            var trackSlider = slider;
            var text = label(index);
            var tick = new Button { Content = title == "网格" ? text[2..] : text, Padding = new Thickness(0),
                FontSize = 8, Foreground = MutedBrush, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, Height = 20 };
            ToolTip.SetTip(tick, title + " " + text);
            AutomationProperties.SetAutomationId(tick, id + ".Tick." + index);
            tick.Click += (_, _) => trackSlider.Value = captured;
            Grid.SetColumn(tick, index);
            ticks.Children.Add(tick);
        }
        return new StackPanel { Width = 290, Spacing = 0, Children = { header, slider, ticks } };
    }

    private void RefreshAdjustmentGridTicks()
    {
        if (adjustmentGridTicks == null) return;
        for (var index = 0; index < adjustmentGridDivisions.Length; index++)
        {
            var tick = (Button)adjustmentGridTicks.Children[index];
            var label = GridLabel(adjustmentGridDivisions[index]);
            tick.Content = label[2..];
            ToolTip.SetTip(tick, "网格 " + label);
        }
    }

    private void SyncAdjustmentZoom()
    {
        if (adjustmentZoomSlider == null || adjustmentZoomText == null) return;
        syncingAdjustmentZoom = true;
        try
        {
            adjustmentZoomSlider.Value = PresetPosition(workbenchZooms, timeline.PixelsPerBeat);
            adjustmentZoomText.Text = $"{timeline.PixelsPerBeat / 140:0.##}x · {timeline.PixelsPerBeat:0} DIP/beat";
        }
        finally { syncingAdjustmentZoom = false; RefreshOptionResets(); }
    }

    private static double InterpolatePreset(IReadOnlyList<double> values, double position)
    {
        position = Math.Clamp(position, 0, values.Count - 1);
        var index = (int)Math.Floor(position); var next = Math.Min(index + 1, values.Count - 1);
        return values[index] + (values[next] - values[index]) * (position - index);
    }
    private static double PresetPosition(IReadOnlyList<double> values, double value)
    {
        if (value <= values[0]) return 0;
        for (var i = 1; i < values.Count; i++)
            if (value <= values[i]) return i - 1 + (value - values[i - 1]) / (values[i] - values[i - 1]);
        return values.Count - 1;
    }

    private static int NearestPreset(IReadOnlyList<double> presets, double value) =>
        Enumerable.Range(0, presets.Count).MinBy(index => Math.Abs(presets[index] - value));

    private void MergeQuickPaletteIntoToolRail()
    {
        if (fixedToolRailBody == null) return;
        var commands = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 4, RowSpacing = 4 };
        var index = 0;
        foreach (var column in quickEditPaletteBody.Children.OfType<Border>().Select(b => b.Child).OfType<StackPanel>())
        {
            var buttons = column.Children.OfType<Button>().ToArray();
            column.Children.Clear();
            foreach (var button in buttons)
            {
                if (quickTimelineToolButtons.Values.Contains(button) ||
                    quickShortcutButtons.Any(pair => pair.Value.Button == button && pair.Key is "Undo" or "Redo" or "PlayPause")) continue;
                var row = index / 2;
                if (commands.RowDefinitions.Count <= row) commands.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                button.Width = double.NaN;
                button.MinWidth = 0;
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                Grid.SetRow(button, row);
                Grid.SetColumn(button, index++ % 2);
                commands.Children.Add(button);
            }
        }
        fixedToolRailBody.Children.Add(new Border { Height = 1, Background = StrokeBrush, Margin = new Thickness(0, 8) });
        fixedToolRailBody.Children.Add(ToolbarLabel("快捷编辑"));
        fixedToolRailBody.Children.Add(commands);
        fixedToolRailBody.Children.Add(ToolbarLabel("排列与变换"));
        fixedToolRailBody.Children.Add(BuildSymmetryToolButton());
        fixedToolRailBody.Children.Add(BuildPatternLibraryButton());
        fixedToolRailBody.Children.Add(BuildStretchToolButton());
        if (extensionMenuButton != null) { DetachControl(extensionMenuButton); fixedToolRailBody.Children.Add(extensionMenuButton); }
    }
}

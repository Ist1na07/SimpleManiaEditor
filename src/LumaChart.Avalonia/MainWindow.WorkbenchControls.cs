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
        var pendingForm = CaptureMetadataEditorDraft(tab);
        var draft = ChartMetadataEditingService.Capture(tab.Session.Document) with
            { Bpm = metadataBpmBox.Text ?? "", Offset = metadataOffsetBox.Text ?? "" };
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
        panel.Children.Add(PresetAdjustment("网格", QuickSnapDivisions.Length, out fixedGridSlider, out fixedGridText,
            index => GridLabel(QuickSnapDivisions[index]), "Editor.Adjustment.Grid", out adjustmentGridTicks));
        fixedGridSlider!.ValueChanged += (_, e) =>
        {
            if (!syncingFixedGridPicker) SetQuickSnapDivision(QuickSnapDivisions[(int)Math.Round(e.NewValue)]);
        };
        panel.Children.Add(PresetAdjustment("缩放", workbenchZooms.Length, out adjustmentZoomSlider, out adjustmentZoomText,
            index => $"{workbenchZooms[index] / 140:0.##}x", "Editor.Adjustment.Zoom", out _));
        adjustmentZoomSlider!.ValueChanged += (_, e) =>
        {
            if (!syncingAdjustmentZoom) timeline.SetZoom(workbenchZooms[(int)Math.Round(e.NewValue)]);
        };
        panel.Children.Add(PresetAdjustment("倍速", workbenchRates.Length, out fixedRateSlider, out fixedRateText,
            index => $"{workbenchRates[index]:0.##}", "Editor.Adjustment.Rate", out _));
        fixedRateSlider!.ValueChanged += async (_, e) =>
        {
            if (syncingFixedRate) return;
            var value = workbenchRates[(int)Math.Round(e.NewValue)];
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
            TickPlacement = TickPlacement.BottomRight, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 1, Height = 30 };
        AutomationProperties.SetAutomationId(slider, id);
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
        for (var index = 0; index < QuickSnapDivisions.Length; index++)
        {
            var tick = (Button)adjustmentGridTicks.Children[index];
            var label = GridLabel(QuickSnapDivisions[index]);
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
            adjustmentZoomSlider.Value = NearestPreset(workbenchZooms, timeline.PixelsPerBeat);
            adjustmentZoomText.Text = $"{timeline.PixelsPerBeat / 140:0.##}x · {timeline.PixelsPerBeat:0} DIP/beat";
        }
        finally { syncingAdjustmentZoom = false; }
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
                if (quickTimelineToolButtons.Values.Contains(button)) continue;
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
    }
}

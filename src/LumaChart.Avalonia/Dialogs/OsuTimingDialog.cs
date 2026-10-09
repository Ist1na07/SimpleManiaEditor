using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Automation;
using LumaChart.Application.Editing;
using LumaChart.Core.IO;

namespace LumaChart.Avalonia.Dialogs;

/// <summary>Only collects a draft; the owner commits it after Apply.</summary>
internal sealed class OsuTimingDialog : Window
{
    private readonly TextBox time = new();
    private readonly TextBox value = new();
    private readonly TextBox meter = new();
    private readonly TextBox sampleIndex = new();
    private readonly ComboBox type = new();
    private readonly ComboBox sampleSet = new();
    private readonly Slider volume = new() { Minimum = 0, Maximum = 100, SmallChange = 1, TickFrequency = 10 };
    private readonly CheckBox kiai = new() { Content = "Kiai time" };
    private readonly TextBlock valueLabel = new();
    private readonly TextBlock error = new() { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap };
    private readonly int originalEffects;

    internal OsuTimingDialog(bool editing, OsuTimingEdit initial)
    {
        Title = editing ? "编辑 Timing 事件" : "新增 Timing 事件";
        Width = 410; SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1B2023")); Foreground = Brushes.White;
        originalEffects = initial.Settings.Effects;
        type.ItemsSource = new[] { "BPM（红线）", "SV / 音效（绿线）" }; type.SelectedIndex = initial.IsBpm ? 0 : 1;
        sampleSet.ItemsSource = new[] { "默认", "Normal", "Soft", "Drum" }; sampleSet.SelectedIndex = initial.Settings.SampleSet;
        var roundedTime = Math.Round(initial.Milliseconds, MidpointRounding.AwayFromZero);
        time.Text = (roundedTime == 0 ? 0 : roundedTime).ToString("0", CultureInfo.InvariantCulture);
        value.Text = initial.Value.ToString("0.######", CultureInfo.InvariantCulture);
        meter.Text = initial.Settings.Meter.ToString(CultureInfo.InvariantCulture);
        sampleIndex.Text = initial.Settings.SampleIndex.ToString(CultureInfo.InvariantCulture);
        volume.Value = initial.Settings.Volume; kiai.IsChecked = initial.Settings.Kiai;
        var volumeText = new TextBlock { Text = $"{volume.Value:0}%" };
        volume.ValueChanged += (_, e) => volumeText.Text = $"{e.NewValue:0}%";
        type.SelectionChanged += (_, _) =>
        {
            valueLabel.Text = type.SelectedIndex == 0 ? "BPM" : "SV 倍率";
            value.Text = type.SelectedIndex == 0 ? "120" : "1";
        };
        valueLabel.Text = initial.IsBpm ? "BPM" : "SV 倍率";
        Control Field(string title, Control control) => new StackPanel { Spacing = 4, Children = { new TextBlock { Text = title, Foreground = Brushes.LightGray, FontSize = 12 }, control } };
        var apply = new Button { Content = editing ? "应用修改" : "添加事件", HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消" };
        AutomationProperties.SetAutomationId(this, "Timing.Dialog");
        AutomationProperties.SetAutomationId(time, "Timing.Dialog.Time");
        AutomationProperties.SetAutomationId(value, "Timing.Dialog.Value");
        AutomationProperties.SetAutomationId(type, "Timing.Dialog.Type");
        AutomationProperties.SetAutomationId(kiai, "Timing.Dialog.Kiai");
        AutomationProperties.SetAutomationId(apply, "Timing.Dialog.Apply");
        AutomationProperties.SetAutomationId(cancel, "Timing.Dialog.Cancel");
        apply.Click += (_, _) => Apply(); cancel.Click += (_, _) => Close();
        Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
        {
            new TextBlock { Text = Title, FontSize = 19, FontWeight = FontWeight.SemiBold },
            Field("事件时间（ms，精度 1 ms）", time), Field("事件类型", type),
            new StackPanel { Spacing = 4, Children = { valueLabel, value } },
            Field("节拍数", meter), Field("采样组", sampleSet), Field("采样编号（0 = 默认）", sampleIndex),
            Field("事件音量", volume), volumeText, kiai, error,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, apply } }
        } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } else if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
    }
    private void Apply()
    {
        bool Number(TextBox box, out double result) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
        if (!Number(time, out var milliseconds) || Math.Abs(milliseconds) > 1_000_000_000 || !Number(value, out var amount) || amount <= 0 ||
            type.SelectedIndex == 0 && amount > LumaChart.Core.Models.ChartDocument.MaximumTimingBpm ||
            !int.TryParse(meter.Text, out var beats) || beats is < 1 or > 32 || !int.TryParse(sampleIndex.Text, out var index) || index < 0)
        { error.Text = "请填写有效时间、正数 BPM / SV、1–32 节拍数及非负采样编号。"; return; }
        var effects = (originalEffects & ~1) | (kiai.IsChecked == true ? 1 : 0);
        Close(new OsuTimingEdit(Math.Round(milliseconds, MidpointRounding.AwayFromZero), type.SelectedIndex == 0, amount,
            new OsuTimingPointSettings(beats, sampleSet.SelectedIndex, index, (int)Math.Round(volume.Value), effects)));
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private int editorAudioVisualOffsetMilliseconds = EditorPresentationSettingsStore.LoadAudioVisualOffset();
    private readonly Slider editorAudioVisualOffsetSlider = new();
    private double? lastCalibratedCursorMilliseconds;
    private double lastCalibratedCursorRate = 1;
    private int lastCalibratedCursorOffsetMilliseconds;
    private Guid? lastCalibratedCursorTabId;
    private bool editorSeekNeedsFreshClock;
    private long editorSeekFreshClockUntil;

    private Control BuildAudioVisualCalibrationSettings()
    {
        var input = new TextBox { Text = editorAudioVisualOffsetMilliseconds.ToString(), MinWidth = 64, HorizontalAlignment = HorizontalAlignment.Stretch };
        var apply = CommandButton("应用", (_, _) =>
        {
            if (int.TryParse(input.Text, out var value) && value is >= -500 and <= 500) editorAudioVisualOffsetSlider.Value = value;
            else viewModel.SetStatus("音画校准请输入 -500 至 500 ms 的整数。");
        });
        AutomationProperties.SetAutomationId(input, "Settings.AudioVisualOffset.Input");
        AutomationProperties.SetAutomationId(apply, "Settings.AudioVisualOffset.Apply");
        editorAudioVisualOffsetSlider.Minimum = -500; editorAudioVisualOffsetSlider.Maximum = 500;
        editorAudioVisualOffsetSlider.SmallChange = 1; editorAudioVisualOffsetSlider.LargeChange = 10;
        editorAudioVisualOffsetSlider.Value = editorAudioVisualOffsetMilliseconds;
        AutomationProperties.SetAutomationId(editorAudioVisualOffsetSlider, "Settings.AudioVisualOffset");
        editorAudioVisualOffsetSlider.ValueChanged += (_, e) =>
        {
            editorAudioVisualOffsetMilliseconds = (int)Math.Round(e.NewValue);
            input.Text = editorAudioVisualOffsetMilliseconds.ToString();
            EditorPresentationSettingsStore.SaveAudioVisualOffset(editorAudioVisualOffsetMilliseconds);
        };
        var entry = new Grid { ColumnDefinitions = new ColumnDefinitions("*,58"), ColumnSpacing = 6 };
        entry.Children.Add(input); Grid.SetColumn(apply, 1); entry.Children.Add(apply);
        var calibrator = CommandButton("打开音画校准器…", async (_, _) =>
        {
            await editorPlaybackCommandGate.WaitAsync();
            try
            {
                await CancelPendingEditorSeek();
                keySoundRuntime.PauseSilent();
                if (audioTransport.PlaybackSnapshot?.IsPlaying == true) await audioTransport.TogglePlayPauseAsync();
                var calibratorWindow = new LumaChart.Avalonia.Dialogs.AudioCalibrationWindow(editorAudioVisualOffsetMilliseconds);
                var result = await calibratorWindow.ShowDialog<int?>(this);
                await calibratorWindow.Shutdown;
                if (result.HasValue) editorAudioVisualOffsetSlider.Value = result.Value;
            }
            catch (Exception ex) { viewModel.SetStatus($"音画校准器未能完成：{ex.Message}"); }
            finally { editorPlaybackCommandGate.Release(); }
        });
        AutomationProperties.SetAutomationId(calibrator, "Settings.AudioVisualOffset.Calibrator");
        return new StackPanel { Spacing = 6, Children =
        {
            TimingField("编辑器音画校准（ms）", entry),
            editorAudioVisualOffsetSlider, calibrator,
            new TextBlock { Text = "正值让播放画面提前，负值让画面延后。只校准本编辑器，不改变谱面、键音设置或试玩。", FontSize = 12, Foreground = MutedBrush, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap }
        } };
    }

    private double ResolveEditorAudioResumeMilliseconds(DesktopChartTab tab)
    {
        var time = GetAudioTimingMap(tab).BeatToMilliseconds(BeatPosition.FromDouble(timeline.CurrentBeat, BeatPosition.MaximumPreservedDenominator));
        if (lastCalibratedCursorTabId == tab.Id && lastCalibratedCursorMilliseconds is { } previous && Math.Abs(previous - time) < 1)
            time = EditorAudioVisualCalibration.AudioTime(time, lastCalibratedCursorRate, lastCalibratedCursorOffsetMilliseconds);
        return Math.Max(0, time);
    }
}

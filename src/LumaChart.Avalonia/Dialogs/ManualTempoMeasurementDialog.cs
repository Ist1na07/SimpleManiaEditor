using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LumaChart.Application.Audio;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Dialogs;

internal sealed class ManualTempoMeasurementDialog : PlatformWindow
{
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#15181A"));
    private static readonly IBrush RaisedBrush = new SolidColorBrush(Color.Parse("#202528"));
    private static readonly IBrush StrokeBrush = new SolidColorBrush(Color.Parse("#3A4246"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#386C70"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E2E6E7"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#8C979C"));

    private readonly Func<Task> togglePlayback;
    private readonly Func<bool> isPlaying;
    private readonly Func<double?> currentPositionMilliseconds;
    private readonly List<double> taps = [];
    private readonly TextBlock keyText = new();
    private readonly TextBlock countText = new();
    private readonly TextBlock resultText = new();
    private readonly TextBlock hintText = new();
    private readonly Button playbackButton;
    private readonly Button applyButton;
    private readonly DispatcherTimer refreshTimer;
    private Key tapKey = Key.T;
    private bool capturingKey;
    private BeatAnalysisResult? result;

    public ManualTempoMeasurementDialog(
        Func<Task> togglePlayback,
        Func<bool> isPlaying,
        Func<double?> currentPositionMilliseconds,
        double uiScale,
        double fontScale)
    {
        this.togglePlayback = togglePlayback ??
                              throw new ArgumentNullException(nameof(togglePlayback));
        this.isPlaying = isPlaying ?? throw new ArgumentNullException(nameof(isPlaying));
        this.currentPositionMilliseconds = currentPositionMilliseconds ??
                                           throw new ArgumentNullException(
                                               nameof(currentPositionMilliseconds));

        uiScale = double.IsFinite(uiScale) ? Math.Clamp(uiScale, .75, 2) : 1;
        fontScale = double.IsFinite(fontScale) ? Math.Clamp(fontScale, .75, 2) : 1;
        Title = "手动测量 BPM 与 Offset";
        Width = 520 * uiScale;
        Height = 470 * uiScale;
        MinWidth = 440 * uiScale;
        MinHeight = 400 * uiScale;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.None;
        Background = PanelBrush;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        FontSize = 12 * fontScale;
        AutomationProperties.SetAutomationId(this, "Tempo.ManualDialog");

        playbackButton = CreateButton("▶ 播放音乐", AccentBrush);
        playbackButton.Click += async (_, _) => await TogglePlaybackAsync();
        AutomationProperties.SetAutomationId(playbackButton, "Tempo.Manual.PlayPause");

        var tapButton = CreateButton("打拍", RaisedBrush);
        tapButton.Click += (_, _) => RegisterTap();
        AutomationProperties.SetAutomationId(tapButton, "Tempo.Manual.Tap");

        var captureButton = CreateButton("更改按键", RaisedBrush);
        captureButton.Click += (_, _) =>
        {
            capturingKey = true;
            keyText.Text = "请按下新的打拍按键…";
            hintText.Text = "Esc 取消按键设置";
        };
        AutomationProperties.SetAutomationId(captureButton, "Tempo.Manual.CaptureKey");

        var resetButton = CreateButton("重新测量", RaisedBrush);
        resetButton.Click += (_, _) => ResetMeasurement();
        AutomationProperties.SetAutomationId(resetButton, "Tempo.Manual.Reset");

        var cancelButton = CreateButton("取消", RaisedBrush);
        cancelButton.Click += (_, _) => Close(null);
        AutomationProperties.SetAutomationId(cancelButton, "Tempo.Manual.Cancel");

        applyButton = CreateButton("使用结果", AccentBrush);
        applyButton.IsEnabled = false;
        applyButton.Click += (_, _) => Close(result);
        AutomationProperties.SetAutomationId(applyButton, "Tempo.Manual.Apply");

        Content = BuildContent(
            playbackButton,
            tapButton,
            captureButton,
            resetButton,
            cancelButton,
            applyButton,
            fontScale);
        UpdatePresentation();

        AddHandler(
            InputElement.KeyDownEvent,
            Dialog_KeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        refreshTimer.Tick += (_, _) => UpdatePlaybackPresentation();
        Opened += (_, _) =>
        {
            refreshTimer.Start();
            Focus();
        };
        Closed += (_, _) => refreshTimer.Stop();
    }

    private Control BuildContent(
        Button playback,
        Button tap,
        Button capture,
        Button reset,
        Button cancel,
        Button apply,
        double fontScale)
    {
        var header = new Border
        {
            Height = 48,
            Background = new SolidColorBrush(Color.Parse("#1A1E21")),
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                Margin = new Thickness(18, 0),
                Children =
                {
                    new TextBlock
                    {
                        Text = "手动测量 BPM 与 Offset",
                        FontSize = 15 * fontScale,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = TextBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
        header.PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                BeginMoveDrag(eventArgs);
        };

        keyText.Foreground = TextBrush;
        keyText.FontSize = 17 * fontScale;
        keyText.FontWeight = FontWeight.SemiBold;
        keyText.HorizontalAlignment = HorizontalAlignment.Center;
        countText.Foreground = MutedBrush;
        countText.HorizontalAlignment = HorizontalAlignment.Center;
        resultText.Foreground = TextBrush;
        resultText.FontSize = 14 * fontScale;
        resultText.TextWrapping = TextWrapping.Wrap;
        resultText.TextAlignment = TextAlignment.Center;
        resultText.HorizontalAlignment = HorizontalAlignment.Stretch;
        hintText.Foreground = MutedBrush;
        hintText.FontSize = 10 * fontScale;
        hintText.TextWrapping = TextWrapping.Wrap;
        hintText.TextAlignment = TextAlignment.Center;
        hintText.HorizontalAlignment = HorizontalAlignment.Stretch;

        var transport = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 8
        };
        transport.Children.Add(playback);
        Grid.SetColumn(tap, 1);
        transport.Children.Add(tap);

        var keyRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,120"),
            ColumnSpacing = 8
        };
        keyRow.Children.Add(new Border
        {
            Background = RaisedBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            MinHeight = 42,
            Child = keyText
        });
        Grid.SetColumn(capture, 1);
        keyRow.Children.Add(capture);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 8
        };
        footer.Children.Add(reset);
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);
        Grid.SetColumn(apply, 2);
        footer.Children.Add(apply);

        var body = new StackPanel
        {
            Margin = new Thickness(22, 18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "播放歌曲后，跟随四分音符连续打拍。建议打 8–16 次。",
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                },
                transport,
                keyRow,
                countText,
                new Border
                {
                    Background = RaisedBrush,
                    BorderBrush = StrokeBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(16),
                    MinHeight = 100,
                    Child = resultText
                },
                hintText,
                footer
            }
        };
        Grid.SetRow(body, 1);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("48,*"),
            Children = { header, body }
        };
        return new Border
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            Child = root
        };
    }

    private async Task TogglePlaybackAsync()
    {
        try
        {
            await togglePlayback();
            UpdatePlaybackPresentation();
        }
        catch (Exception exception)
        {
            hintText.Text = AvaloniaLocalizationCatalog.Format("无法切换音乐播放：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
            hintText.Foreground = Brushes.IndianRed;
        }
    }

    private void Dialog_KeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (capturingKey)
        {
            eventArgs.Handled = true;
            if (eventArgs.Key == Key.Escape)
            {
                capturingKey = false;
                UpdatePresentation();
                return;
            }
            if (eventArgs.Key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
                return;
            tapKey = eventArgs.Key;
            capturingKey = false;
            ResetMeasurement();
            return;
        }
        if (eventArgs.Key != tapKey || eventArgs.KeyModifiers != KeyModifiers.None)
            return;
        eventArgs.Handled = true;
        RegisterTap();
    }

    private void RegisterTap()
    {
        if (!isPlaying())
        {
            hintText.Text = "请先播放音乐，再跟随整拍打拍。";
            hintText.Foreground = Brushes.IndianRed;
            return;
        }
        var position = currentPositionMilliseconds();
        if (position is not double value || !double.IsFinite(value))
        {
            hintText.Text = "暂时无法读取音频时钟，请重试。";
            hintText.Foreground = Brushes.IndianRed;
            return;
        }
        // Taps are positions within one pass through the song. When playback
        // jumps back (the song ended and playing again restarts it from 0, or a
        // seek), the old taps belong to another pass: every new tap would look
        // earlier than the last one and be dropped below as a double tap, so
        // nothing could be measured any more. Start over from this tap instead.
        if (taps.Count > 0 && value < taps[^1] - 100)
        {
            taps.Clear();
            taps.Add(value);
            Recalculate();
            hintText.Text = "播放位置已回退，已从这一拍重新开始测量。";
            hintText.Foreground = Brushes.IndianRed;
            return;
        }
        if (taps.Count > 0 && value - taps[^1] < 100) return;
        taps.Add(value);
        Recalculate();
    }

    private void Recalculate()
    {
        result = null;
        if (taps.Count >= ManualTempoTapAnalyzer.MinimumTapCount)
        {
            try
            {
                result = ManualTempoTapAnalyzer.Analyze(taps);
            }
            catch (InvalidOperationException exception)
            {
                hintText.Text = exception.Message;
                hintText.Foreground = Brushes.IndianRed;
            }
        }
        UpdatePresentation();
    }

    private void ResetMeasurement()
    {
        taps.Clear();
        result = null;
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        keyText.Text = capturingKey ? "请按键…" : AvaloniaLocalizationCatalog.Format("打拍按键：{0}", tapKey);
        countText.Text = AvaloniaLocalizationCatalog.Format("已采样 {0} 次", taps.Count);
        resultText.Text = result is { } measured
            ? AvaloniaLocalizationCatalog.Format("BPM  {0:0.###}\nOffset  {1:0.###} ms\n稳定度  {2:P0}", measured.Bpm, measured.OffsetMilliseconds, 1 - measured.Instability)
            : AvaloniaLocalizationCatalog.Format("至少连续打拍 {0} 次后显示结果", ManualTempoTapAnalyzer.MinimumTapCount);
        applyButton.IsEnabled = result.HasValue;
        if (!capturingKey)
        {
            hintText.Text = result.HasValue
                ? "继续打拍会持续提高测量稳定性；确认后才会写入 Timing。"
                : AvaloniaLocalizationCatalog.Format("按 {0} 或点击“打拍”；按键只在此窗口内生效。", tapKey);
            hintText.Foreground = MutedBrush;
        }
        UpdatePlaybackPresentation();
    }

    private void UpdatePlaybackPresentation() =>
        playbackButton.Content = isPlaying() ? "Ⅱ 暂停音乐" : "▶ 播放音乐";

    private static Button CreateButton(string text, IBrush background) =>
        new()
        {
            Content = text,
            MinHeight = 38,
            Background = background,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Foreground = TextBrush,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
}

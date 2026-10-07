using LumaChart.Avalonia.Infrastructure;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Core.Analysis;

namespace LumaChart.Avalonia.Dialogs;

internal sealed record DifficultyAnalysisDialogDiagnostics(
    double Width,
    double Height,
    double MinimumWidth,
    double MinimumHeight,
    double UiScale,
    double FontScale,
    bool Resizable,
    bool UsesLayoutTransform,
    double OverallDifficulty,
    int PeakButtonCount);

/// <summary>Windows Avalonia replica of WPF's 4K Rework result window.</summary>
internal sealed class DifficultyAnalysisWindow : PlatformWindow
{
    private static readonly IBrush BackgroundBrush =
        new SolidColorBrush(Color.Parse("#131619"));
    private static readonly IBrush HeaderBrush =
        new SolidColorBrush(Color.Parse("#191D20"));
    private static readonly IBrush PanelBrush =
        new SolidColorBrush(Color.Parse("#1A1E21"));
    private static readonly IBrush SecondaryPanelBrush =
        new SolidColorBrush(Color.Parse("#181C1F"));
    private static readonly IBrush FooterBrush =
        new SolidColorBrush(Color.Parse("#171B1E"));
    private static readonly IBrush RaisedBrush =
        new SolidColorBrush(Color.Parse("#22272A"));
    private static readonly IBrush StrokeBrush =
        new SolidColorBrush(Color.Parse("#30383C"));
    private static readonly IBrush MutedBrush =
        new SolidColorBrush(Color.Parse("#7F898E"));
    private static readonly IBrush AccentBrush =
        new SolidColorBrush(Color.Parse("#88A9AD"));

    private readonly Action<double> navigate;
    private readonly Func<double, CancellationToken, Task<ReworkDifficultyResult>> recalculate;
    private readonly Action<ReworkDifficultyResult> resultUpdated;
    private readonly TextBlock starText = new();
    private readonly TextBlock referenceText = new();
    private readonly TextBlock noteCountText = new();
    private readonly TextBlock longNoteText = new();
    private readonly TextBlock settingsText = new();
    private readonly TextBlock odValueText = new();
    private readonly TextBlock recalculateText = new();
    private readonly StackPanel peakList = new() { Spacing = 5 };
    private readonly Slider odSlider = new();
    private readonly ScaleTransform rootScale;
    private CancellationTokenSource? recalculationCancellation;
    private bool sliderReady;
    private int peakButtonCount;

    public DifficultyAnalysisWindow(
        ReworkDifficultyResult result,
        Action<double> navigateToBeat,
        Func<double, CancellationToken, Task<ReworkDifficultyResult>> recalculateDifficulty,
        Action<ReworkDifficultyResult> onResultUpdated,
        double uiScale,
        double fontScale)
    {
        ArgumentNullException.ThrowIfNull(result);
        navigate = navigateToBeat ?? throw new ArgumentNullException(nameof(navigateToBeat));
        recalculate = recalculateDifficulty ?? throw new ArgumentNullException(nameof(recalculateDifficulty));
        resultUpdated = onResultUpdated ?? throw new ArgumentNullException(nameof(onResultUpdated));
        uiScale = double.IsFinite(uiScale) ? Math.Clamp(uiScale, .75, 2) : 1;
        fontScale = double.IsFinite(fontScale) ? Math.Clamp(fontScale, .75, 2) : 1;
        rootScale = new ScaleTransform(uiScale, uiScale);

        Title = "4K Rework 难度分析";
        Width = 720 * uiScale;
        Height = 650 * uiScale;
        MinWidth = 620 * uiScale;
        MinHeight = 520 * uiScale;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = BackgroundBrush;
        WindowDecorations = WindowDecorations.None;
        CanResize = true;
        ShowInTaskbar = false;
        AutomationProperties.SetAutomationId(this, "DifficultyAnalysis.Dialog");

        var transformedRoot = new LayoutTransformControl
        {
            UseRenderTransform = false,
            LayoutTransform = rootScale,
            Child = BuildContent(fontScale)
        };
        Content = transformedRoot;
        ApplyResult(result);
        odSlider.Value = Math.Clamp(Math.Round(result.OverallDifficulty, 1), 0, 10);
        odValueText.Text = odSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        sliderReady = true;
        Diagnostics = new DifficultyAnalysisDialogDiagnostics(
            Width,
            Height,
            MinWidth,
            MinHeight,
            uiScale,
            fontScale,
            CanResize,
            !transformedRoot.UseRenderTransform &&
            ReferenceEquals(transformedRoot.LayoutTransform, rootScale),
            odSlider.Value,
            peakButtonCount);

        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Escape) return;
            eventArgs.Handled = true;
            Close(true);
        };
        Closing += (_, _) => CancelRecalculation();
    }

    public DifficultyAnalysisDialogDiagnostics Diagnostics { get; private set; }

    private Control BuildContent(double fontScale)
    {
        var root = new Border
        {
            Background = BackgroundBrush,
            BorderBrush = new SolidColorBrush(Color.Parse("#3A4246")),
            BorderThickness = new Thickness(1),
            Child = new Grid { RowDefinitions = new RowDefinitions("44,*,50") }
        };
        var grid = (Grid)root.Child;
        grid.Children.Add(BuildHeader(fontScale));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(24, 20, 24, 18),
            Content = BuildBody(fontScale)
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var footer = new Border
        {
            Background = FooterBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Grid { Margin = new Thickness(18, 7) }
        };
        var footerGrid = (Grid)footer.Child;
        footerGrid.Children.Add(new TextBlock
        {
            Text = "Sunny Rework + Azusa 标级 · 4K",
            Foreground = new SolidColorBrush(Color.Parse("#6F7C80")),
            FontSize = 10 * fontScale,
            VerticalAlignment = VerticalAlignment.Center
        });
        var done = CreateButton("完成", () => Close(true), fontScale, primary: true);
        done.Width = 92;
        done.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetAutomationId(done, "DifficultyAnalysis.Dialog.Done");
        footerGrid.Children.Add(done);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        return root;
    }

    private Control BuildHeader(double fontScale)
    {
        var header = new Border
        {
            Background = HeaderBrush,
            BorderBrush = new SolidColorBrush(Color.Parse("#343B3F")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,44"),
                Margin = new Thickness(16, 0, 8, 0)
            }
        };
        header.PointerPressed += (_, eventArgs) =>
        {
            if (eventArgs.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                BeginMoveDrag(eventArgs);
        };
        var headerGrid = (Grid)header.Child;
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "4K Rework",
                    FontSize = 16 * fontScale,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#253033")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#486065")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(10, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = "正式轨道",
                        Foreground = new SolidColorBrush(Color.Parse("#A9BEC1")),
                        FontSize = 9 * fontScale
                    }
                }
            }
        };
        headerGrid.Children.Add(title);
        var close = CreateButton("×", () => Close(true), fontScale);
        close.MinWidth = 38;
        close.FontSize = 20 * fontScale;
        close.Padding = new Thickness(4, 1);
        AutomationProperties.SetAutomationId(close, "DifficultyAnalysis.Dialog.Close");
        Grid.SetColumn(close, 1);
        headerGrid.Children.Add(close);
        return header;
    }

    private Control BuildBody(double fontScale)
    {
        ConfigureResultText(fontScale);
        var body = new StackPanel();
        body.Children.Add(BuildSummary(fontScale));
        body.Children.Add(BuildOverallDifficulty(fontScale));
        body.Children.Add(BuildMetrics(fontScale));
        body.Children.Add(new TextBlock
        {
            Text = "高难区段",
            FontSize = 17 * fontScale,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        });
        body.Children.Add(new TextBlock
        {
            Text = "按局部强度排序；点击一项会关闭窗口并将编辑器红线定位到该处。",
            Foreground = MutedBrush,
            FontSize = 10 * fontScale,
            Margin = new Thickness(0, 3, 0, 9)
        });
        AutomationProperties.SetAutomationId(peakList, "DifficultyAnalysis.Dialog.Peaks");
        body.Children.Add(peakList);
        body.Children.Add(new TextBlock
        {
            Text = "仅支持 4K。分析包含所有正式分组，无论当前是否隐藏；noteref / 草稿区 Note 始终忽略。",
            Foreground = new SolidColorBrush(Color.Parse("#708084")),
            FontSize = 10 * fontScale,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 9, 0, 0)
        });
        return body;
    }

    private Control BuildSummary(double fontScale)
    {
        var panel = new Border
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(20, 17),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("190,1,*") }
        };
        var grid = (Grid)panel.Child;
        var rating = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "REWORK RESULT",
                    Foreground = new SolidColorBrush(Color.Parse("#77868B")),
                    FontSize = 10 * fontScale,
                    FontWeight = FontWeight.SemiBold
                }
            }
        };
        var starLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 0),
            Children =
            {
                starText,
                new TextBlock
                {
                    Text = "★",
                    FontSize = 18 * fontScale,
                    Foreground = AccentBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(7, 8, 0, 0)
                }
            }
        };
        rating.Children.Add(starLine);
        grid.Children.Add(rating);
        var divider = new Border { Background = new SolidColorBrush(Color.Parse("#333B3F")) };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);
        var reference = new StackPanel
        {
            Margin = new Thickness(20, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = "参考难度",
                    Foreground = MutedBrush,
                    FontSize = 10 * fontScale
                },
                referenceText,
                new TextBlock
                {
                    Text = "参考级别是 Rework 的估算，不等同于人工定级。",
                    Foreground = new SolidColorBrush(Color.Parse("#727D82")),
                    FontSize = 10 * fontScale,
                    Margin = new Thickness(0, 7, 0, 0)
                }
            }
        };
        Grid.SetColumn(reference, 2);
        grid.Children.Add(reference);
        return panel;
    }

    private Control BuildOverallDifficulty(double fontScale)
    {
        var panel = new Border
        {
            Background = SecondaryPanelBrush,
            BorderBrush = new SolidColorBrush(Color.Parse("#2D3438")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            }
        };
        var grid = (Grid)panel.Child;
        grid.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = "分析 OD",
                    FontSize = 12 * fontScale,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White
                },
                new TextBlock
                {
                    Text = "  ·  只影响本次 Rework 计算",
                    Foreground = new SolidColorBrush(Color.Parse("#748085")),
                    FontSize = 10 * fontScale,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        });
        odValueText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        odValueText.FontSize = 15 * fontScale;
        odValueText.FontWeight = FontWeight.SemiBold;
        odValueText.Foreground = new SolidColorBrush(Color.Parse("#B9D0D2"));
        Grid.SetColumn(odValueText, 1);
        grid.Children.Add(odValueText);

        var sliderGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("22,*,28"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        sliderGrid.Children.Add(SliderEndLabel("0", fontScale, HorizontalAlignment.Left));
        odSlider.Minimum = 0;
        odSlider.Maximum = 10;
        odSlider.TickFrequency = .1;
        odSlider.IsSnapToTickEnabled = true;
        odSlider.SmallChange = .1;
        odSlider.LargeChange = 1;
        odSlider.ValueChanged += OdSlider_ValueChanged;
        AutomationProperties.SetAutomationId(odSlider, "DifficultyAnalysis.Dialog.OD");
        AutomationProperties.SetName(odSlider, "分析 OD");
        Grid.SetColumn(odSlider, 1);
        sliderGrid.Children.Add(odSlider);
        var ten = SliderEndLabel("10", fontScale, HorizontalAlignment.Right);
        Grid.SetColumn(ten, 2);
        sliderGrid.Children.Add(ten);
        Grid.SetRow(sliderGrid, 1);
        Grid.SetColumnSpan(sliderGrid, 2);
        grid.Children.Add(sliderGrid);

        recalculateText.Foreground = new SolidColorBrush(Color.Parse("#7E8C90"));
        recalculateText.FontSize = 10 * fontScale;
        recalculateText.Height = 15;
        recalculateText.Margin = new Thickness(22, 1, 0, 0);
        AutomationProperties.SetAutomationId(recalculateText, "DifficultyAnalysis.Dialog.RecalculateStatus");
        Grid.SetRow(recalculateText, 2);
        Grid.SetColumnSpan(recalculateText, 2);
        grid.Children.Add(recalculateText);
        return panel;
    }

    private Control BuildMetrics(double fontScale)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            Margin = new Thickness(0, 12, 0, 18)
        };
        AddMetric(grid, noteCountText, "正式 Note", 0, new Thickness(0, 0, 5, 0), fontScale);
        AddMetric(grid, longNoteText, "LN / 占比", 1, new Thickness(5, 0), fontScale);
        AddMetric(grid, settingsText, "速度 / OD", 2, new Thickness(5, 0, 0, 0), fontScale);
        return grid;
    }

    private void ConfigureResultText(double fontScale)
    {
        starText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        starText.FontSize = 46 * fontScale;
        starText.FontWeight = FontWeight.SemiBold;
        starText.Foreground = new SolidColorBrush(Color.Parse("#E9EEEE"));
        AutomationProperties.SetAutomationId(starText, "DifficultyAnalysis.Dialog.Star");
        referenceText.FontSize = 17 * fontScale;
        referenceText.FontWeight = FontWeight.SemiBold;
        referenceText.Foreground = new SolidColorBrush(Color.Parse("#D7E1E2"));
        referenceText.TextWrapping = TextWrapping.Wrap;
        referenceText.Margin = new Thickness(0, 4, 0, 0);
        AutomationProperties.SetAutomationId(referenceText, "DifficultyAnalysis.Dialog.Reference");
    }

    private async void OdSlider_ValueChanged(
        object? sender,
        RangeBaseValueChangedEventArgs eventArgs)
    {
        _ = sender;
        var od = Math.Clamp(Math.Round(eventArgs.NewValue, 1), 0, 10);
        odValueText.Text = od.ToString("0.0", CultureInfo.InvariantCulture);
        if (!sliderReady) return;

        CancelRecalculation();
        var cancellation = recalculationCancellation = new CancellationTokenSource();
        // Capture the token once: the next slider move (or Closing) cancels and
        // disposes this source, and reading .Token from a disposed source throws
        // ObjectDisposedException. The token itself stays readable.
        var token = cancellation.Token;
        recalculateText.Text = "正在重新计算…";
        try
        {
            await Task.Delay(180, token);
            var result = await recalculate(od, token);
            token.ThrowIfCancellationRequested();
            ApplyResult(result);
            resultUpdated(result);
            recalculateText.Text = AvaloniaLocalizationCatalog.Format("已使用 OD {0:0.0}", od);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // A superseded run must not overwrite the newer run's status.
            if (!token.IsCancellationRequested)
                recalculateText.Text = AvaloniaLocalizationCatalog.Format("重新计算失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
        }
    }

    private void ApplyResult(ReworkDifficultyResult result)
    {
        starText.Text = result.StarRating.ToString("0.00", CultureInfo.InvariantCulture);
        referenceText.Text = result.ReferenceDifficulty;
        noteCountText.Text = result.NoteCount.ToString("N0", CultureInfo.InvariantCulture);
        longNoteText.Text = $"{result.LongNoteCount:N0}  ·  {result.LongNoteRatio:P1}";
        settingsText.Text = $"{result.SpeedRate:0.00}x  ·  OD{result.OverallDifficulty:0.#}";
        peakList.Children.Clear();
        var peaks = Controls.DifficultyOverviewSurface.SelectVisiblePeaks(result.Peaks);
        foreach (var item in peaks.Select((peak, index) => (Peak: peak, Rank: index + 1)))
            peakList.Children.Add(CreatePeakButton(item.Rank, item.Peak));
        peakButtonCount = peakList.Children.Count;
        if (Diagnostics != null)
            Diagnostics = Diagnostics with
            {
                OverallDifficulty = result.OverallDifficulty,
                PeakButtonCount = peakButtonCount
            };
    }

    private Button CreatePeakButton(int rank, ReworkDifficultyPeak peak)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("36,115,*,96")
        };
        grid.Children.Add(PeakText(rank.ToString(CultureInfo.InvariantCulture), "#718589"));
        var beat = PeakText(peak.Beat.ToString(), "#E0E5E6", semiBold: true);
        Grid.SetColumn(beat, 1);
        grid.Children.Add(beat);
        var time = PeakText(FormatTime(peak.Milliseconds), "#909A9E");
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);
        var intensity = PeakText(AvaloniaLocalizationCatalog.Format("强度 {0:0.00}", peak.Intensity), "#B3C7CA");
        intensity.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(intensity, 3);
        grid.Children.Add(intensity);
        var button = CreateButton(string.Empty, () =>
        {
            navigate(peak.Beat.Value);
            Close(true);
        }, 1);
        button.Content = grid;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(13, 9);
        AutomationProperties.SetAutomationId(button, $"DifficultyAnalysis.Dialog.Peak.{rank}");
        AutomationProperties.SetName(button, AvaloniaLocalizationCatalog.Format("高难区段 {0}，{1}", rank, peak.Beat));
        return button;
    }

    private static TextBlock PeakText(string text, string color, bool semiBold = false) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.Parse(color)),
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontWeight = semiBold ? FontWeight.SemiBold : FontWeight.Normal,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static void AddMetric(
        Grid grid,
        TextBlock value,
        string label,
        int column,
        Thickness margin,
        double fontScale)
    {
        value.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        value.FontSize = 17 * fontScale;
        value.FontWeight = FontWeight.SemiBold;
        value.Foreground = new SolidColorBrush(Color.Parse("#E7EBEC"));
        var panel = new Border
        {
            Background = SecondaryPanelBrush,
            BorderBrush = new SolidColorBrush(Color.Parse("#2D3438")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(13, 10),
            Margin = margin,
            Child = new StackPanel
            {
                Children =
                {
                    value,
                    new TextBlock
                    {
                        Text = label,
                        FontSize = 10 * fontScale,
                        Foreground = MutedBrush,
                        Margin = new Thickness(0, 3, 0, 0)
                    }
                }
            }
        };
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    private static TextBlock SliderEndLabel(
        string text,
        double fontScale,
        HorizontalAlignment alignment) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.Parse("#6F7A7E")),
        FontSize = 10 * fontScale,
        HorizontalAlignment = alignment,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button CreateButton(
        string text,
        Action action,
        double fontScale,
        bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 44,
            Padding = new Thickness(10, 6),
            FontSize = 12 * fontScale,
            Background = primary ? AccentBrush : RaisedBrush,
            Foreground = primary ? Brushes.Black : Brushes.White,
            BorderBrush = primary ? AccentBrush : StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static string FormatTime(double milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss\.fff")
            : time.ToString(@"mm\:ss\.fff");
    }

    private void CancelRecalculation()
    {
        recalculationCancellation?.Cancel();
        recalculationCancellation?.Dispose();
        recalculationCancellation = null;
    }
}

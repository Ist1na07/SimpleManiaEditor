using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Application.Exporting;
using LumaChart.Avalonia.Infrastructure;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;

namespace LumaChart.Avalonia.Dialogs;

internal sealed record FullPackageExportDialogScaleDiagnostics(
    double RequestedUiScale,
    double RequestedFontScale,
    double ActualRootScaleX,
    double ActualRootScaleY,
    double ValueFontSize,
    bool UsesLayoutTransform);

/// <summary>
/// WPF-compatible four-mode export chooser for Malody and osu!mania. Stable
/// legacy automation identifiers are retained so the already-authoritative
/// FullPackage GUI paths remain observable while the additional modes are added.
/// </summary>
internal sealed class ChartExportDialog : PlatformWindow
{
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#15181A"));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#1A1E21"));
    private static readonly IBrush FieldBrush = new SolidColorBrush(Color.Parse("#202528"));
    private static readonly IBrush ComboBrush = new SolidColorBrush(Color.Parse("#15181A"));
    private static readonly IBrush ComboStrokeBrush = new SolidColorBrush(Color.Parse("#383E43"));
    private static readonly IBrush ComboTextBrush = new SolidColorBrush(Color.Parse("#E3E6E8"));
    private static readonly IBrush RaisedBrush = new SolidColorBrush(Color.Parse("#242A2D"));
    private static readonly IBrush PrimaryBrush = new SolidColorBrush(Color.Parse("#386C70"));
    private static readonly IBrush StrokeBrush = new SolidColorBrush(Color.Parse("#3A4246"));
    private static readonly IBrush PrimaryStrokeBrush = new SolidColorBrush(Color.Parse("#548F93"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E2E6E7"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#7F8B90"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#9AA5A9"));
    private readonly ScaleTransform rootScale;
    private ComboBox formatBox = null!;
    private ComboBox modeBox = null!;
    private Border osuDifficultyPanel = null!;
    private Border singleRatePanel = null!;
    private Border ratePackPanel = null!;
    private TextBox overallDifficultyBox = null!;
    private TextBox hpDrainRateBox = null!;
    private TextBox rateBox = null!;
    private TextBox minimumRateBox = null!;
    private TextBox maximumRateBox = null!;
    private TextBox rateStepBox = null!;
    private CheckBox preservePitchBox = null!;
    private Border preservePitchIndicator = null!;
    private AvaloniaPath preservePitchIndicatorMark = null!;
    private TextBlock validationText = null!;

    public ChartExportDialog(double uiScale, double fontScale)
    {
        if (!double.IsFinite(uiScale)) uiScale = 1;
        if (!double.IsFinite(fontScale)) fontScale = 1;
        uiScale = Math.Clamp(uiScale, .75, 2);
        fontScale = Math.Clamp(fontScale, .75, 2);
        rootScale = new ScaleTransform(uiScale, uiScale);

        Title = "导出谱面";
        Width = WindowsChartExportDialogParityPolicy.WindowWidth * uiScale;
        Height = WindowsChartExportDialogParityPolicy.WindowHeight * uiScale;
        SizeToContent = SizeToContent.Manual;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.None;
        Background = PanelBrush;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        FontSize = 12 * fontScale;
        AutomationProperties.SetAutomationId(this, "Export.MalodyFullPackage.Dialog");

        var transformedRoot = new LayoutTransformControl
        {
            UseRenderTransform = false,
            LayoutTransform = rootScale,
            Child = BuildContent(fontScale)
        };
        Content = transformedRoot;
        ScaleDiagnostics = new FullPackageExportDialogScaleDiagnostics(
            uiScale,
            fontScale,
            rootScale.ScaleX,
            rootScale.ScaleY,
            13 * fontScale,
            !transformedRoot.UseRenderTransform &&
            ReferenceEquals(transformedRoot.LayoutTransform, rootScale));

    }

    public FullPackageExportDialogScaleDiagnostics ScaleDiagnostics { get; }

    public ChartExportOptions? Options { get; private set; }

    private Control BuildContent(double fontScale)
    {
        var root = new Border
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions(
                    $"{WindowsChartExportDialogParityPolicy.HeaderHeight},*")
            }
        };
        var grid = (Grid)root.Child;

        var header = new Border
        {
            Background = HeaderBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,40"),
                Margin = new Thickness(
                    WindowsChartExportDialogParityPolicy.HeaderLeftMargin,
                    0,
                    WindowsChartExportDialogParityPolicy.HeaderRightMargin,
                    0)
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
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.Children.Add(new TextBlock
        {
            Text = "EXPORT",
            Foreground = new SolidColorBrush(Color.Parse("#75969A")),
            FontSize = 9 * fontScale,
            FontWeight = FontWeight.SemiBold
        });
        title.Children.Add(new TextBlock
        {
            Text = "导出谱面",
            Foreground = TextBrush,
            FontSize = 14 * fontScale,
            FontWeight = FontWeight.SemiBold
        });
        headerGrid.Children.Add(title);
        var close = CreateButton(
            "×",
            Brushes.Transparent,
            Brushes.Transparent,
            Brushes.White,
            () => Close(false));
        close.MinWidth = 34;
        close.Width = WindowsChartExportDialogParityPolicy.HeaderCloseWidth;
        close.Height = double.NaN;
        close.MinWidth = 0;
        close.Padding = new Thickness(0);
        close.BorderThickness = new Thickness(0);
        close.CornerRadius = new CornerRadius(0);
        close.HorizontalAlignment = HorizontalAlignment.Stretch;
        close.VerticalAlignment = VerticalAlignment.Stretch;
        close.FontFamily = new FontFamily("Segoe UI Symbol");
        close.FontSize = 19 * fontScale;
        AutomationProperties.SetAutomationId(close, "Export.MalodyFullPackage.Dialog.Close");
        Grid.SetColumn(close, 1);
        headerGrid.Children.Add(close);
        grid.Children.Add(header);

        var contentGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(
                WindowsChartExportDialogParityPolicy.ContentLeftMargin,
                WindowsChartExportDialogParityPolicy.ContentTopMargin,
                WindowsChartExportDialogParityPolicy.ContentRightMargin,
                WindowsChartExportDialogParityPolicy.ContentBottomMargin)
        };
        Grid.SetRow(contentGrid, 1);
        grid.Children.Add(contentGrid);

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(new TextBlock
        {
            Text = "导出",
            Foreground = TextBrush,
            FontSize = 25 * fontScale,
            FontWeight = FontWeight.SemiBold
        });
        var summary = new TextBlock
        {
            Text = "Malody 是工作格式；osu!mania 使用 v14。整谱与倍速模式会连同资源打包。",
            Foreground = MutedBrush,
            FontSize = 12 * fontScale,
            Margin = new Thickness(0, 5, 0, 4),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(summary, "Export.MalodyFullPackage.Dialog.Summary");
        body.Children.Add(summary);

        body.Children.Add(CreateLabel("目标格式", fontScale));
        formatBox = new ComboBox
        {
            Height = WindowsChartExportDialogParityPolicy.ComboBoxHeight,
            MinHeight = 0,
            Padding = new Thickness(9, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ComboBrush,
            Foreground = ComboTextBrush,
            BorderBrush = ComboStrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            FontSize = 12 * fontScale,
            SelectedIndex = 0,
            ItemsSource = new[]
            {
                "Malody (.mc / .mcz)",
                "osu!mania v14 (.osu / .osz)"
            }
        };
        AutomationProperties.SetAutomationId(formatBox, "Export.MalodyFullPackage.Dialog.Format");
        AutomationProperties.SetName(formatBox, "Malody (.mc / .mcz)");
        formatBox.SelectionChanged += (_, _) =>
        {
            AutomationProperties.SetName(
                formatBox,
                formatBox.SelectedIndex == 1
                    ? "osu!mania v14 (.osu / .osz)"
                    : "Malody (.mc / .mcz)");
            if (osuDifficultyPanel != null)
                osuDifficultyPanel.IsVisible = formatBox.SelectedIndex == 1;
        };
        body.Children.Add(CreateWpfComboHost(formatBox));

        overallDifficultyBox = CreateNumberBox("8", "Export.MalodyFullPackage.Dialog.OsuOverallDifficulty", fontScale);
        hpDrainRateBox = CreateNumberBox("8", "Export.MalodyFullPackage.Dialog.OsuHpDrainRate", fontScale);
        var difficultyGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        var odPanel = new StackPanel();
        odPanel.Children.Add(CreateFieldLabel("OD / Overall Difficulty", fontScale));
        odPanel.Children.Add(overallDifficultyBox);
        difficultyGrid.Children.Add(odPanel);
        var hpPanel = new StackPanel();
        hpPanel.Children.Add(CreateFieldLabel("HP / HP Drain Rate", fontScale));
        hpPanel.Children.Add(hpDrainRateBox);
        Grid.SetColumn(hpPanel, 2);
        difficultyGrid.Children.Add(hpPanel);
        var difficultyContent = new StackPanel();
        difficultyContent.Children.Add(new TextBlock
        {
            Text = "osu! 难度参数",
            Foreground = TextBrush,
            FontSize = 12 * fontScale,
            FontWeight = FontWeight.SemiBold
        });
        difficultyContent.Children.Add(new TextBlock
        {
            Text = "写入 .osu 的 [Difficulty]；范围均为 0–10。",
            Foreground = MutedBrush,
            FontSize = 11 * fontScale,
            Margin = new Thickness(0, 3, 0, 9)
        });
        difficultyContent.Children.Add(difficultyGrid);
        osuDifficultyPanel = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(13),
            Background = new SolidColorBrush(Color.Parse("#1B2023")),
            BorderBrush = new SolidColorBrush(Color.Parse("#343C40")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = difficultyContent
        };
        AutomationProperties.SetAutomationId(osuDifficultyPanel, "Export.MalodyFullPackage.Dialog.OsuDifficultyPanel");
        body.Children.Add(osuDifficultyPanel);
        body.Children.Add(CreateLabel("导出方式", fontScale));
        modeBox = new ComboBox
        {
            Height = WindowsChartExportDialogParityPolicy.ComboBoxHeight,
            MinHeight = 0,
            Padding = new Thickness(9, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ComboBrush,
            Foreground = ComboTextBrush,
            BorderBrush = ComboStrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            FontSize = 12 * fontScale,
            SelectedIndex = 1,
            ItemsSource = new[]
            {
                "单谱文件（不包含资源）",
                "整谱压缩包",
                "单一倍速压缩包",
                "范围倍速包"
            }
        };
        AutomationProperties.SetAutomationId(modeBox, "Export.MalodyFullPackage.Dialog.Mode");
        AutomationProperties.SetName(modeBox, "整谱压缩包");
        modeBox.SelectionChanged += (_, _) => UpdateModeState();
        body.Children.Add(CreateWpfComboHost(modeBox));

        rateBox = CreateNumberBox("1.2", "Export.MalodyFullPackage.Dialog.SingleRate", fontScale);
        var singleRateContent = new StackPanel();
        singleRateContent.Children.Add(CreateFieldLabel("倍速", fontScale));
        singleRateContent.Children.Add(rateBox);
        singleRatePanel = CreateOptionPanel(
            singleRateContent,
            "Export.MalodyFullPackage.Dialog.SingleRatePanel");
        body.Children.Add(singleRatePanel);

        minimumRateBox = CreateNumberBox("0.8", "Export.MalodyFullPackage.Dialog.MinimumRate", fontScale);
        maximumRateBox = CreateNumberBox("1.2", "Export.MalodyFullPackage.Dialog.MaximumRate", fontScale);
        rateStepBox = CreateNumberBox("0.1", "Export.MalodyFullPackage.Dialog.RateStep", fontScale);
        var ratePackGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*")
        };
        var minimumPanel = new StackPanel();
        minimumPanel.Children.Add(CreateFieldLabel("起始倍速", fontScale));
        minimumPanel.Children.Add(minimumRateBox);
        ratePackGrid.Children.Add(minimumPanel);
        var maximumPanel = new StackPanel();
        maximumPanel.Children.Add(CreateFieldLabel("结束倍速", fontScale));
        maximumPanel.Children.Add(maximumRateBox);
        Grid.SetColumn(maximumPanel, 2);
        ratePackGrid.Children.Add(maximumPanel);
        var stepPanel = new StackPanel();
        stepPanel.Children.Add(CreateFieldLabel("间隔", fontScale));
        stepPanel.Children.Add(rateStepBox);
        Grid.SetColumn(stepPanel, 4);
        ratePackGrid.Children.Add(stepPanel);
        ratePackPanel = CreateOptionPanel(
            ratePackGrid,
            "Export.MalodyFullPackage.Dialog.RatePackPanel");
        body.Children.Add(ratePackPanel);

        preservePitchBox = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "音频变速不变调",
                Foreground = TextBrush,
                FontSize = 12 * fontScale,
                Margin = new Thickness(-3, 0, 0, 0)
            },
            Margin = new Thickness(2, 16, 0, 0),
            Background = Brushes.Transparent,
            Foreground = TextBrush,
            FontSize = 12 * fontScale,
            IsChecked = false,
            IsEnabled = WindowsChartExportDialogParityPolicy.InitialPreservePitchEnabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };
        AutomationProperties.SetAutomationId(
            preservePitchBox,
            "Export.MalodyFullPackage.Dialog.PreservePitch");
        AutomationProperties.SetName(preservePitchBox, "音频变速不变调");
        body.Children.Add(CreateWpfCheckBoxHost());
        body.Children.Add(new TextBlock
        {
            Text = "所有倍速谱会保持 Note 的 Beat 不变，同时按倍率修正 BPM、Delay 与预览时间。",
            Foreground = MutedBrush,
            FontSize = 11 * fontScale,
            Margin = new Thickness(22, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        validationText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#E08C82")),
            FontSize = 11 * fontScale,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(validationText, "Export.MalodyFullPackage.Dialog.Validation");
        body.Children.Add(validationText);
        var bodyScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body
        };
        AutomationProperties.SetAutomationId(
            bodyScroll,
            "Export.MalodyFullPackage.Dialog.BodyScroll");
        contentGrid.Children.Add(bodyScroll);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = WindowsChartExportDialogParityPolicy.ActionGap,
            Margin = new Thickness(
                0,
                WindowsChartExportDialogParityPolicy.ActionTopMargin,
                0,
                0)
        };
        var cancel = CreateButton("取消", RaisedBrush, StrokeBrush, TextBrush, () => Close(false));
        cancel.Width = WindowsChartExportDialogParityPolicy.CancelWidth;
        cancel.FontSize = 12 * fontScale;
        AutomationProperties.SetAutomationId(cancel, "Export.MalodyFullPackage.Dialog.Cancel");
        buttons.Children.Add(cancel);
        var export = CreateButton(
            "选择位置并导出",
            PrimaryBrush,
            PrimaryStrokeBrush,
            Brushes.White,
            Confirm);
        export.Width = WindowsChartExportDialogParityPolicy.ConfirmWidth;
        export.FontSize = 12 * fontScale;
        AutomationProperties.SetAutomationId(export, "Export.MalodyFullPackage.Dialog.Confirm");
        buttons.Children.Add(export);
        Grid.SetRow(buttons, 1);
        contentGrid.Children.Add(buttons);
        return root;
    }

    private static TextBlock CreateLabel(string text, double fontScale)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = LabelBrush,
            FontSize = 12 * fontScale,
            Margin = new Thickness(0, 12, 0, 5)
        };
    }

    private static TextBlock CreateFieldLabel(string text, double fontScale) => new()
    {
        Text = text,
        Foreground = LabelBrush,
        FontSize = 11 * fontScale,
        Margin = new Thickness(0, 0, 0, 5)
    };

    private static TextBox CreateNumberBox(string value, string automationId, double fontScale)
    {
        var box = new TextBox
        {
            Text = value,
            MinHeight = WindowsChartExportDialogParityPolicy.NumberBoxHeight,
            Padding = new Thickness(9, 7),
            Background = FieldBrush,
            Foreground = TextBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            FontSize = 13 * fontScale
        };
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    private static Control CreateWpfComboHost(ComboBox comboBox)
    {
        var host = new Grid();
        host.Children.Add(comboBox);
        var arrowCover = new Border
        {
            Width = 27,
            Margin = new Thickness(0, 1, 1, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = ComboBrush,
            IsHitTestVisible = false,
            Child = new AvaloniaPath
            {
                Data = Geometry.Parse("M 0 0 L 4 4 L 8 0 Z"),
                Fill = new SolidColorBrush(Color.Parse("#9DA5AA")),
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        host.Children.Add(arrowCover);
        return host;
    }

    private Control CreateWpfCheckBoxHost()
    {
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        host.PointerPressed += (_, args) =>
        {
            if (!preservePitchBox.IsEnabled ||
                !args.GetCurrentPoint(host).Properties.IsLeftButtonPressed)
            {
                return;
            }
            preservePitchBox.IsChecked = preservePitchBox.IsChecked != true;
            preservePitchBox.Focus();
            args.Handled = true;
        };
        host.Children.Add(preservePitchBox);

        preservePitchIndicatorMark = new AvaloniaPath
        {
            Data = Geometry.Parse("M 2 7 L 5 10 L 12 2"),
            Stroke = Brushes.Black,
            StrokeThickness = 1.6,
            IsVisible = false
        };
        preservePitchIndicator = new Border
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#707070")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = preservePitchIndicatorMark
        };
        var indicatorCover = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(2, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = PanelBrush,
            IsHitTestVisible = false,
            Child = preservePitchIndicator
        };
        host.Children.Add(indicatorCover);
        preservePitchBox.IsCheckedChanged += (_, _) => RefreshPreservePitchIndicator();
        RefreshPreservePitchIndicator();
        return host;
    }

    private void RefreshPreservePitchIndicator()
    {
        if (preservePitchIndicator == null || preservePitchIndicatorMark == null) return;
        preservePitchIndicator.Opacity = preservePitchBox.IsEnabled ? 1 : .55;
        preservePitchIndicatorMark.IsVisible = preservePitchBox.IsChecked == true;
    }

    private static Border CreateOptionPanel(Control content, string automationId)
    {
        var panel = new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(13),
            Background = new SolidColorBrush(Color.Parse("#1B2023")),
            BorderBrush = new SolidColorBrush(Color.Parse("#343C40")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = content
        };
        AutomationProperties.SetAutomationId(panel, automationId);
        return panel;
    }

    private void UpdateModeState()
    {
        if (modeBox == null || singleRatePanel == null || ratePackPanel == null ||
            preservePitchBox == null)
            return;

        var mode = ModeFromSelection();
        AutomationProperties.SetName(modeBox, mode switch
        {
            ChartExportMode.SingleChart => "单谱文件（不包含资源）",
            ChartExportMode.SingleRatePackage => "单一倍速压缩包",
            ChartExportMode.RatePack => "范围倍速包",
            _ => "整谱压缩包"
        });
        singleRatePanel.IsVisible = mode == ChartExportMode.SingleRatePackage;
        ratePackPanel.IsVisible = mode == ChartExportMode.RatePack;
        preservePitchBox.IsEnabled = mode is ChartExportMode.SingleRatePackage or ChartExportMode.RatePack;
        RefreshPreservePitchIndicator();
    }

    private ChartExportMode ModeFromSelection() => modeBox.SelectedIndex switch
    {
        0 => ChartExportMode.SingleChart,
        2 => ChartExportMode.SingleRatePackage,
        3 => ChartExportMode.RatePack,
        _ => ChartExportMode.FullPackage
    };

    private void Confirm()
    {
        validationText.Text = string.Empty;
        var format = formatBox.SelectedIndex == 1
            ? ChartExportFormat.OsuMania
            : ChartExportFormat.Malody;
        var mode = ModeFromSelection();
        if (!TryReadNumber(rateBox.Text, out var rate) ||
            !TryReadNumber(minimumRateBox.Text, out var minimumRate) ||
            !TryReadNumber(maximumRateBox.Text, out var maximumRate) ||
            !TryReadNumber(rateStepBox.Text, out var rateStep) ||
            rate is < .2 or > 3 ||
            minimumRate is < .2 or > 3 ||
            maximumRate is < .2 or > 3 ||
            maximumRate < minimumRate ||
            rateStep is < .01 or > 1)
        {
            validationText.Text =
                "倍速范围为 0.2–3，结束值不能小于起始值，间隔范围为 0.01–1。";
            return;
        }

        var overallDifficulty = 8d;
        var hpDrainRate = 8d;
        if (format == ChartExportFormat.OsuMania &&
            (!TryReadNumber(overallDifficultyBox.Text, out overallDifficulty) ||
             !TryReadNumber(hpDrainRateBox.Text, out hpDrainRate) ||
             overallDifficulty is < 0 or > 10 || hpDrainRate is < 0 or > 10))
        {
            validationText.Text = "osu! 的 OD 与 HP 必须是 0–10 之间的数字。";
            return;
        }

        Options = new ChartExportOptions(
            format,
            mode,
            preservePitchBox.IsChecked == true,
            rate,
            minimumRate,
            maximumRate,
            rateStep,
            overallDifficulty,
            hpDrainRate);
        try
        {
            _ = ChartExportPlanner.Create(Options);
        }
        catch (Exception exception)
        {
            validationText.Text = exception.Message;
            Options = null;
            return;
        }

        Close(true);
    }

    private static bool TryReadNumber(string? text, out double value)
    {
        var parsed = double.TryParse(
                         text,
                         NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out value) ||
                     double.TryParse(
                         text,
                         NumberStyles.Float,
                         CultureInfo.CurrentCulture,
                         out value);
        return parsed && double.IsFinite(value);
    }

    private static Button CreateButton(
        string text,
        IBrush background,
        IBrush border,
        IBrush foreground,
        Action action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 0,
            Height = WindowsChartExportDialogParityPolicy.ActionHeight,
            Padding = new Thickness(18, 9),
            Background = background,
            Foreground = foreground,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += (_, _) => action();
        return button;
    }
}

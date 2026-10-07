using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Online;

namespace LumaChart.Avalonia.Dialogs;

internal sealed class WindowsIssueReportDialog : PlatformWindow
{
    private static readonly IBrush WindowBrush = Brush("#111315");
    private static readonly IBrush PanelBrush = Brush("#181C1F");
    private static readonly IBrush RaisedBrush = Brush("#20282E");
    private static readonly IBrush StrokeBrush = Brush("#3B474D");
    private static readonly IBrush AccentBrush = Brush("#79B8BD");
    private static readonly IBrush TextBrush = Brush("#E3E8E9");
    private static readonly IBrush MutedBrush = Brush("#93A0A5");
    private static readonly IBrush ErrorBrush = Brush("#D39A88");
    private static readonly IBrush SuccessBrush = Brush("#8FC4A5");

    private readonly OnlineAccountSession? session;
    private readonly ComboBox categoryBox = new();
    private readonly TextBox titleBox;
    private readonly TextBox descriptionBox;
    private readonly TextBox stepsBox;
    private readonly TextBox expectedBox;
    private readonly TextBlock statusText = new();
    private readonly Button submitButton;
    private readonly Button copyButton;
    private readonly Button saveButton;
    private readonly Button historyButton;
    private readonly string environmentText;
    private OnlineIssueReportDto? submittedReport;
    private bool actionInProgress;

    public WindowsIssueReportDialog(
        OnlineAccountSession? session,
        string environmentText,
        double uiScale,
        double fontScale)
    {
        this.session = session;
        this.environmentText = environmentText ?? string.Empty;
        uiScale = double.IsFinite(uiScale) ? Math.Clamp(uiScale, .8, 1.6) : 1;
        fontScale = double.IsFinite(fontScale) ? Math.Clamp(fontScale, .8, 1.6) : 1;

        Title = "问题反馈";
        Width = 680 * uiScale;
        Height = 700 * uiScale;
        MinWidth = 560 * uiScale;
        MinHeight = 520 * uiScale;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.None;
        Background = WindowBrush;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        FontSize = 12 * fontScale;
        AutomationProperties.SetAutomationId(this, "IssueReport.Dialog");
        AutomationProperties.SetName(this, "问题反馈");

        categoryBox.ItemsSource = WindowsIssueReportPolicy.Categories;
        categoryBox.SelectedIndex = 0;
        categoryBox.Height = 38;
        categoryBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        categoryBox.Background = RaisedBrush;
        categoryBox.Foreground = TextBrush;
        AutomationProperties.SetAutomationId(categoryBox, "IssueReport.Category");

        titleBox = CreateTextBox("一句话概括问题", 120, false, 38);
        descriptionBox = CreateTextBox("请描述发生了什么", 4000, true, 108);
        stepsBox = CreateTextBox("例如：打开某张谱面 → 点击播放 → 约 10 秒后…", 4000, true, 96);
        expectedBox = CreateTextBox("可选：你原本期望看到的结果", 2000, true, 82);

        copyButton = ActionButton("复制 Issue", RaisedBrush);
        copyButton.Click += async (_, _) => await CopyReportAsync();
        AutomationProperties.SetAutomationId(copyButton, "IssueReport.Copy");

        saveButton = ActionButton("保存到 Downloads", RaisedBrush);
        saveButton.Click += (_, _) => SaveReport();
        AutomationProperties.SetAutomationId(saveButton, "IssueReport.Save");

        historyButton = ActionButton("我的反馈", RaisedBrush);
        historyButton.IsEnabled = session?.IsAuthenticated == true;
        historyButton.Click += async (_, _) => await ShowHistoryAsync(uiScale, fontScale);
        AutomationProperties.SetAutomationId(historyButton, "IssueReport.History");

        submitButton = ActionButton(
            session?.IsAuthenticated == true ? "提交 Issue" : "登录后提交 Issue",
            AccentBrush);
        submitButton.IsEnabled = session?.IsAuthenticated == true;
        submitButton.Click += async (_, _) => await SubmitReportAsync();
        AutomationProperties.SetAutomationId(submitButton, "IssueReport.Submit");

        var close = ActionButton("关闭", RaisedBrush);
        close.Click += (_, _) => Close(submittedReport);
        AutomationProperties.SetAutomationId(close, "IssueReport.Close");

        statusText.Foreground = MutedBrush;
        statusText.TextWrapping = TextWrapping.Wrap;
        statusText.MinHeight = 24;
        AutomationProperties.SetAutomationId(statusText, "IssueReport.Status");

        Content = BuildContent(close, fontScale);
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
    }

    private Control BuildContent(Button close, double fontScale)
    {
        var headerClose = ActionButton("×", RaisedBrush);
        headerClose.Width = 38;
        headerClose.MinWidth = 38;
        headerClose.Click += (_, _) => Close(submittedReport);
        AutomationProperties.SetAutomationId(headerClose, "IssueReport.HeaderClose");
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(24, 20, 24, 0)
        };
        var title = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "提交测试 Issue",
                    FontSize = 21 * fontScale,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = TextBrush
                },
                new TextBlock
                {
                    Text = session?.IsAuthenticated == true
                        ? "提交后项目维护者可以在内容后台查看、标记和跟进。"
                        : "未登录也可以复制或保存；登录后可直接提交到测试反馈库。",
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        header.Children.Add(title);
        Grid.SetColumn(headerClose, 1);
        header.Children.Add(headerClose);

        var form = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(24, 18, 24, 10),
            Children =
            {
                Label("问题类型"),
                categoryBox,
                Label("标题"),
                titleBox,
                Label("问题描述"),
                descriptionBox,
                Label("复现步骤"),
                stepsBox,
                Label("期望结果"),
                expectedBox,
                new TextBlock
                {
                    Text = "不会自动附带谱面路径、账号密码或密钥；下面的环境信息只包含版本、系统和窗口状态。",
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                },
                statusText
            }
        };

        var actions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"),
            ColumnSpacing = 8,
            Margin = new Thickness(24, 0, 24, 20)
        };
        actions.Children.Add(copyButton);
        Grid.SetColumn(saveButton, 1);
        actions.Children.Add(saveButton);
        Grid.SetColumn(historyButton, 2);
        actions.Children.Add(historyButton);
        Grid.SetColumn(submitButton, 3);
        actions.Children.Add(submitButton);
        Grid.SetColumn(close, 4);
        actions.Children.Add(close);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = PanelBrush
        };
        root.Children.Add(header);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = form
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        return new Border
        {
            Margin = new Thickness(1),
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = root
        };
    }

    private async Task CopyReportAsync()
    {
        if (!TryBuildReport(out var report)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            SetStatus("当前系统无法访问剪贴板，请使用“保存到 Downloads”。", error: true);
            return;
        }
        try
        {
            await clipboard.SetTextAsync(WindowsIssueReportPolicy.BuildMarkdown(report));
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.ExternalException or
                TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Another process holding the clipboard (OpenClipboard contention)
            // surfaces here; from this async Click handler it would close the app.
            SetStatus("当前系统无法访问剪贴板，请使用“保存到 Downloads”。", error: true);
            return;
        }
        SetStatus("Issue 内容已复制，可以直接粘贴到反馈区。", success: true);
    }

    private void SaveReport()
    {
        if (!TryBuildReport(out var report)) return;
        try
        {
            var path = WindowsIssueReportPolicy.SaveToDownloads(report, submittedReport?.Id.ToString("D"));
            SetStatus(AvaloniaLocalizationCatalog.Format("已保存到 Downloads：{0}", Path.GetFileName(path)), success: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(AvaloniaLocalizationCatalog.Format("保存反馈失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message)), error: true);
        }
    }

    private async Task SubmitReportAsync()
    {
        if (actionInProgress || session?.IsAuthenticated != true) return;
        if (!TryBuildReport(out var report)) return;
        actionInProgress = true;
        submitButton.IsEnabled = false;
        copyButton.IsEnabled = false;
        saveButton.IsEnabled = false;
        historyButton.IsEnabled = false;
        SetStatus("正在提交 Issue…");
        try
        {
            submittedReport = await session.CreateIssueReportAsync(report);
            SetStatus(
                AvaloniaLocalizationCatalog.Format("已提交，反馈编号：{0:D}。也可以保存一份本地副本。", submittedReport.Id),
                success: true);
            submitButton.Content = "已提交";
        }
        catch (Exception exception) when (
            exception is OnlineApiException or HttpRequestException or System.Net.WebException or IOException or
            InvalidOperationException or OperationCanceledException or
            // A 200 response carrying an HTML error page fails to deserialize.
            System.Text.Json.JsonException or FormatException)
        {
            SetStatus(
                AvaloniaLocalizationCatalog.Format("在线提交失败：{0}。可以改用复制或保存。", AvaloniaLocalizationCatalog.Text(exception.Message)),
                error: true);
            submitButton.IsEnabled = true;
        }
        finally
        {
            actionInProgress = false;
            copyButton.IsEnabled = true;
            saveButton.IsEnabled = true;
            historyButton.IsEnabled = session?.IsAuthenticated == true;
        }
    }

    private async Task ShowHistoryAsync(double uiScale, double fontScale)
    {
        if (session?.IsAuthenticated != true) return;
        var history = new WindowsIssueHistoryDialog(session, uiScale, fontScale);
        await history.ShowDialog(this);
    }

    private bool TryBuildReport(out OnlineCreateIssueReportRequestDto report)
    {
        var category = categoryBox.SelectedItem as WindowsIssueCategory ??
                       WindowsIssueReportPolicy.Categories[0];
        var title = titleBox.Text?.Trim() ?? string.Empty;
        var description = descriptionBox.Text?.Trim() ?? string.Empty;
        if (title.Length is < 3 or > 120)
        {
            SetStatus("标题需要 3～120 个字符。", error: true);
            titleBox.Focus();
            report = default!;
            return false;
        }
        if (description.Length is < 10 or > 4000)
        {
            SetStatus("问题描述需要 10～4000 个字符。", error: true);
            descriptionBox.Focus();
            report = default!;
            return false;
        }
        report = new OnlineCreateIssueReportRequestDto(
            category.Code,
            title,
            description,
            stepsBox.Text?.Trim() ?? string.Empty,
            expectedBox.Text?.Trim() ?? string.Empty,
            environmentText);
        return true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            Close(submittedReport);
            args.Handled = true;
        }
    }

    private void SetStatus(string text, bool error = false, bool success = false)
    {
        statusText.Text = text;
        statusText.Foreground = error ? ErrorBrush : success ? SuccessBrush : MutedBrush;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = TextBrush,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 2, 0, 0)
    };

    private static TextBox CreateTextBox(
        string watermark,
        int maxLength,
        bool acceptsReturn,
        double minHeight) => new()
    {
        PlaceholderText = watermark,
        MaxLength = maxLength,
        AcceptsReturn = acceptsReturn,
        TextWrapping = acceptsReturn ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MinHeight = minHeight,
        Height = acceptsReturn ? double.NaN : minHeight,
        VerticalContentAlignment = acceptsReturn
            ? VerticalAlignment.Top
            : VerticalAlignment.Center,
        Background = RaisedBrush,
        Foreground = TextBrush,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 8),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static Button ActionButton(string text, IBrush background) => new()
    {
        Content = text,
        Height = 38,
        MinWidth = 0,
        Background = background,
        Foreground = TextBrush,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(8, 4)
    };

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
}

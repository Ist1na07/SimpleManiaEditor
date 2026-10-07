using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Online;

namespace LumaChart.Avalonia.Dialogs;

internal sealed class WindowsIssueHistoryDialog : PlatformWindow
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

    private readonly OnlineAccountSession session;
    private readonly double uiScale;
    private readonly double fontScale;
    private readonly StackPanel issueList = new() { Spacing = 8 };
    private readonly TextBlock statusText = new();
    private readonly TextBlock pageText = new();
    private readonly Button previousButton;
    private readonly Button nextButton;
    private readonly Button refreshButton;
    private int currentPage = 1;
    private int totalPages = 1;
    private bool actionInProgress;

    public WindowsIssueHistoryDialog(
        OnlineAccountSession session,
        double uiScale,
        double fontScale)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.uiScale = double.IsFinite(uiScale) ? Math.Clamp(uiScale, .8, 1.6) : 1;
        this.fontScale = double.IsFinite(fontScale) ? Math.Clamp(fontScale, .8, 1.6) : 1;

        Title = "我的反馈";
        Width = 760 * this.uiScale;
        Height = 680 * this.uiScale;
        MinWidth = 560 * this.uiScale;
        MinHeight = 460 * this.uiScale;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowDecorations = WindowDecorations.None;
        Background = WindowBrush;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        FontSize = 12 * this.fontScale;
        AutomationProperties.SetAutomationId(this, "IssueHistory.Dialog");
        AutomationProperties.SetName(this, "我的反馈");

        refreshButton = ActionButton("刷新", RaisedBrush);
        refreshButton.Click += async (_, _) => await LoadPageAsync(currentPage);
        AutomationProperties.SetAutomationId(refreshButton, "IssueHistory.Refresh");

        previousButton = ActionButton("上一页", RaisedBrush);
        previousButton.IsEnabled = false;
        previousButton.Click += async (_, _) => await LoadPageAsync(currentPage - 1);
        AutomationProperties.SetAutomationId(previousButton, "IssueHistory.Previous");

        nextButton = ActionButton("下一页", RaisedBrush);
        nextButton.IsEnabled = false;
        nextButton.Click += async (_, _) => await LoadPageAsync(currentPage + 1);
        AutomationProperties.SetAutomationId(nextButton, "IssueHistory.Next");

        pageText.Foreground = MutedBrush;
        pageText.HorizontalAlignment = HorizontalAlignment.Center;
        pageText.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(pageText, "IssueHistory.Page");

        statusText.Foreground = MutedBrush;
        statusText.TextWrapping = TextWrapping.Wrap;
        statusText.MinHeight = 24;
        AutomationProperties.SetAutomationId(statusText, "IssueHistory.Status");

        var close = ActionButton("关闭", RaisedBrush);
        close.Click += (_, _) => Close();
        AutomationProperties.SetAutomationId(close, "IssueHistory.Close");

        Content = BuildContent(close);
        Opened += async (_, _) => await LoadPageAsync(1);
    }

    private Control BuildContent(Button close)
    {
        var headerClose = ActionButton("×", RaisedBrush);
        headerClose.Width = 38;
        headerClose.MinWidth = 38;
        headerClose.Click += (_, _) => Close();
        AutomationProperties.SetAutomationId(headerClose, "IssueHistory.HeaderClose");

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(24, 20, 24, 0)
        };
        var heading = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "我的反馈",
                    FontSize = 21 * fontScale,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = TextBrush
                },
                new TextBlock
                {
                    Text = "这里可以查看你提交过的 Issue、处理状态和维护者备注。",
                    Foreground = MutedBrush,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        header.Children.Add(heading);
        Grid.SetColumn(headerClose, 1);
        header.Children.Add(headerClose);

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(24, 18, 24, 8)
        };
        toolbar.Children.Add(refreshButton);
        Grid.SetColumn(statusText, 1);
        toolbar.Children.Add(statusText);
        Grid.SetColumn(close, 2);
        toolbar.Children.Add(close);

        var paging = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10,
            Margin = new Thickness(24, 8, 24, 20)
        };
        paging.Children.Add(previousButton);
        Grid.SetColumn(pageText, 1);
        paging.Children.Add(pageText);
        Grid.SetColumn(nextButton, 2);
        paging.Children.Add(nextButton);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = issueList,
            Margin = new Thickness(24, 0)
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Background = PanelBrush
        };
        root.Children.Add(header);
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        Grid.SetRow(paging, 3);
        root.Children.Add(paging);
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

    private async Task LoadPageAsync(int page)
    {
        if (actionInProgress) return;
        actionInProgress = true;
        refreshButton.IsEnabled = false;
        previousButton.IsEnabled = false;
        nextButton.IsEnabled = false;
        SetStatus("正在读取反馈历史…");
        try
        {
            var result = await session.GetMyIssueReportsAsync(Math.Max(1, page));
            currentPage = result.Page;
            totalPages = Math.Max(1, (int)Math.Ceiling(
                result.TotalCount / (double)Math.Max(1, result.PageSize)));
            pageText.Text = result.TotalCount == 0
                ? "暂无反馈"
                : AvaloniaLocalizationCatalog.Format("第 {0}/{1} 页 · 共 {2:N0} 条", currentPage, totalPages, result.TotalCount);
            previousButton.IsEnabled = currentPage > 1;
            nextButton.IsEnabled = currentPage < totalPages;
            issueList.Children.Clear();
            if (result.Issues.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "你还没有提交过反馈。",
                    Foreground = MutedBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 36)
                };
                AutomationProperties.SetAutomationId(empty, "IssueHistory.Empty");
                issueList.Children.Add(Card(empty, new Thickness(18)));
            }
            else
            {
                foreach (var issue in result.Issues)
                    issueList.Children.Add(BuildIssueCard(issue));
            }
            SetStatus("反馈历史已更新。", success: true);
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            SetStatus(AvaloniaLocalizationCatalog.Format("读取反馈历史失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message)), error: true);
        }
        finally
        {
            actionInProgress = false;
            refreshButton.IsEnabled = true;
            if (currentPage > 1) previousButton.IsEnabled = true;
            if (currentPage < totalPages) nextButton.IsEnabled = true;
        }
    }

    private Control BuildIssueCard(OnlineIssueReportDto issue)
    {
        var delete = ActionButton("删除", Brush("#49302E"));
        delete.Width = 76;
        delete.HorizontalAlignment = HorizontalAlignment.Right;
        delete.Click += async (_, _) => await DeleteIssueAsync(issue);
        AutomationProperties.SetAutomationId(delete, $"IssueHistory.Delete.{issue.Id:D}");

        var title = new TextBlock
        {
            Text = issue.Title,
            Foreground = TextBrush,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 10
        };
        header.Children.Add(title);
        Grid.SetColumn(delete, 1);
        header.Children.Add(delete);

        var status = new TextBlock
        {
            Text = $"{CategoryLabel(issue.Category)} · {StatusLabel(issue.Status)} · " +
                   $"{issue.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            Foreground = StatusBrush(issue.Status),
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetAutomationId(status, $"IssueHistory.Status.{issue.Id:D}");

        var children = new List<Control>
        {
            header,
            status,
            new TextBlock
            {
                Text = issue.Description,
                MaxHeight = 56,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#C4CCCF")
            }
        };
        if (!string.IsNullOrWhiteSpace(issue.ResolutionNote))
        {
            children.Add(new TextBlock
            {
                Text = AvaloniaLocalizationCatalog.Format("处理备注：{0}", issue.ResolutionNote),
                TextWrapping = TextWrapping.Wrap,
                Foreground = MutedBrush
            });
        }
        var panel = new StackPanel { Spacing = 7 };
        foreach (var child in children) panel.Children.Add(child);
        return Card(panel, new Thickness(14));
    }

    private async Task DeleteIssueAsync(OnlineIssueReportDto issue)
    {
        if (actionInProgress) return;
        var confirmation = new SettingsActionDialog(
            "删除反馈",
            AvaloniaLocalizationCatalog.Format("确定删除“{0}”吗？删除后维护者将无法继续查看这条反馈。", issue.Title),
            "删除",
            showCancel: true,
            dangerous: true,
            uiScale,
            fontScale);
        if (!await confirmation.ShowDialog<bool>(this)) return;

        actionInProgress = true;
        refreshButton.IsEnabled = false;
        previousButton.IsEnabled = false;
        nextButton.IsEnabled = false;
        SetStatus("正在删除反馈…");
        try
        {
            await session.DeleteMyIssueReportAsync(issue.Id);
            SetStatus("反馈已删除。", success: true);
            actionInProgress = false;
            await LoadPageAsync(currentPage);
        }
        catch (Exception exception) when (IsExpectedOnlineFailure(exception))
        {
            SetStatus(AvaloniaLocalizationCatalog.Format("删除失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message)), error: true);
            actionInProgress = false;
            refreshButton.IsEnabled = true;
        }
    }

    // Both paths run from async Click/Opened handlers, where anything that
    // escapes closes the application. JsonException/FormatException cover a
    // 200 response whose body is an HTML error page rather than JSON.
    private static bool IsExpectedOnlineFailure(Exception exception) =>
        exception is OnlineApiException or HttpRequestException or System.Net.WebException or IOException or
            InvalidOperationException or OperationCanceledException or
            System.Text.Json.JsonException or FormatException;

    private void SetStatus(string text, bool error = false, bool success = false)
    {
        statusText.Text = text;
        statusText.Foreground = error ? ErrorBrush : success ? SuccessBrush : MutedBrush;
    }

    private static Control Card(Control child, Thickness padding) => new Border
    {
        Background = Brush("#20282E"),
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = padding,
        Child = child
    };

    private static Button ActionButton(string text, IBrush background) => new()
    {
        Content = text,
        Height = 38,
        Background = background,
        Foreground = TextBrush,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(10, 4)
    };

    private static string StatusLabel(string? status) => status switch
    {
        "confirmed" => "已确认",
        "fixed" => "已修复",
        "closed" => "已关闭",
        _ => "待处理"
    };

    private static IBrush StatusBrush(string? status) => status switch
    {
        "confirmed" => Brush("#D1B37E"),
        "fixed" => SuccessBrush,
        "closed" => MutedBrush,
        _ => AccentBrush
    };

    private static string CategoryLabel(string? category) => category switch
    {
        "crash" => "崩溃 / 报错",
        "playback" => "播放 / Key 音",
        "editor" => "编辑器 / 谱面",
        "online" => "联机 / 房间",
        "ui" => "界面 / 显示",
        "import-export" => "导入 / 导出",
        _ => "其他"
    };

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
}

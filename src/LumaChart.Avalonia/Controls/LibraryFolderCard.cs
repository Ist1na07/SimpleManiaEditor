using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LumaChart.Application.Libraries;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;

namespace LumaChart.Avalonia.Controls;

public sealed record LibraryFolderCardDiagnostics(
    string? FolderPath,
    string? DisplayTitle,
    int BoundChartCount,
    int MaterializedChartRowCount,
    bool IsExpanded);

/// <summary>
/// Recyclable Windows library card. The visual instance is stable while all
/// displayed values and actions are resolved from the current DataContext.
/// </summary>
public sealed class LibraryFolderCard : Border
{
    private static readonly IBrush ExpandedBrush =
        new SolidColorBrush(Color.Parse("#171B1D"));
    private static readonly IBrush PreviewBrush =
        new SolidColorBrush(Color.Parse("#1B2023"));
    private static readonly IBrush BadgeBrush =
        new SolidColorBrush(Color.Parse("#263538"));
    private static readonly IBrush BadgeStrokeBrush =
        new SolidColorBrush(Color.Parse("#58777B"));
    private static readonly IBrush RaisedBrush =
        new SolidColorBrush(Color.Parse(
            WindowsLibraryLayoutPolicy.FolderActionBackground));
    private static readonly IBrush ActionForegroundBrush =
        new SolidColorBrush(Color.Parse(
            WindowsLibraryLayoutPolicy.FolderActionForeground));
    private static readonly IBrush ExpandedStrokeBrush =
        new SolidColorBrush(Color.Parse("#30373B"));
    private static readonly IBrush ActionStrokeBrush =
        new SolidColorBrush(Color.Parse(
            WindowsLibraryLayoutPolicy.FolderActionBorder));
    private static readonly IBrush ToggleForegroundBrush =
        new SolidColorBrush(Color.Parse(
            WindowsLibraryLayoutPolicy.FolderToggleForeground));
    private static readonly IBrush ToggleActiveForegroundBrush =
        new SolidColorBrush(Color.Parse(
            WindowsLibraryLayoutPolicy.FolderToggleActiveForeground));
    private static readonly IBrush ToggleHoverBrush =
        new SolidColorBrush(Color.Parse(
            WindowsLibraryLayoutPolicy.FolderToggleHoverBackground));
    private static readonly IBrush ToggleExpandedBrush =
        new SolidColorBrush(Color.Parse(
            WindowsLibraryLayoutPolicy.FolderToggleExpandedBackground));
    private readonly Func<string, Task> openChartAsync;
    private readonly Func<DesktopLibraryChartEntry, ContextMenu> createChartContextMenu;
    private readonly WindowsLibraryFolderExpansionState expansionState;
    private readonly Func<string?, IImage?> loadThumbnail;
    private readonly Grid preview = new() { ClipToBounds = true };
    private readonly Image thumbnailImage = new()
    {
        Stretch = Stretch.UniformToFill,
        IsVisible = false
    };
    private readonly LibraryThumbnailPlaceholderSurface thumbnailPlaceholder = new();
    private readonly TextBlock formatBadgeText = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#C8DFE0")),
        FontSize = 9,
        FontWeight = FontWeight.Bold
    };
    private readonly TextBlock titleText = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#E4E7E8")),
        FontSize = 17,
        FontWeight = FontWeight.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock detailsText = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#A3ACB0")),
        FontSize = 12,
        Margin = new Thickness(0, 7, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly TextBlock relativePathText = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#69767B")),
        FontSize = 10,
        Margin = new Thickness(0, 5, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly TextBlock addedText = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#7D888D")),
        FontSize = 10,
        HorizontalAlignment = HorizontalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 8)
    };
    private readonly StackPanel charts = new()
    {
        Spacing = 0
    };
    private readonly Border chartsHost;
    private readonly Button expandButton;
    private readonly Button toggleButton;
    private readonly TextBlock toggleChevron;
    private readonly Grid headerHost;
    private DesktopLibraryFolderView? currentView;
    private bool currentExpanded;
    private long viewportAnchorGeneration;

    public LibraryFolderCard(
        Func<string, Task> openChartAsync,
        Func<DesktopLibraryChartEntry, ContextMenu> createChartContextMenu,
        WindowsLibraryFolderExpansionState expansionState,
        Func<string?, IImage?>? loadThumbnail = null)
    {
        this.openChartAsync = openChartAsync ??
                              throw new ArgumentNullException(nameof(openChartAsync));
        this.createChartContextMenu = createChartContextMenu ??
                                      throw new ArgumentNullException(
                                          nameof(createChartContextMenu));
        this.expansionState = expansionState ??
                              throw new ArgumentNullException(nameof(expansionState));
        this.loadThumbnail =
            loadThumbnail ?? AvaloniaLibraryThumbnailCache.Load;

        preview.Children.Add(thumbnailImage);
        preview.Children.Add(thumbnailPlaceholder);
        AutomationProperties.SetAutomationId(
            thumbnailImage,
            "Library.FolderThumbnailImage");
        AutomationProperties.SetAutomationId(
            thumbnailPlaceholder,
            "Library.FolderThumbnailPlaceholder");
        AutomationProperties.SetName(
            thumbnailPlaceholder,
            "无封面占位图");

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("176,*,150"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 112
        };
        AutomationProperties.SetAutomationId(header, "Library.FolderHeader");
        var thumbnail = new Border
        {
            Width = 160,
            Height = 90,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = PreviewBrush,
            CornerRadius = new CornerRadius(3),
            ClipToBounds = true,
            Child = preview
        };
        AutomationProperties.SetAutomationId(thumbnail, "Library.FolderThumbnail");
        header.Children.Add(thumbnail);

        var formatBadge = new Border
        {
            Background = BadgeBrush,
            BorderBrush = BadgeStrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = formatBadgeText
        };
        var headingLine = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { formatBadge, titleText }
        };
        Grid.SetColumn(titleText, 1);

        var heading = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 18, 0),
            Children = { headingLine, detailsText, relativePathText }
        };
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);

        expandButton = PrimaryButton("选择谱面", ExpandButton_Click);
        AutomationProperties.SetAutomationId(expandButton, "Library.FolderExpand");
        AutomationProperties.SetAutomationId(addedText, "Library.FolderAdded");
        var actions = new StackPanel
        {
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { addedText, expandButton }
        };
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);

        toggleChevron = new TextBlock
        {
            Text = "⌄",
            Foreground = ToggleForegroundBrush,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        toggleButton = new Button
        {
            Width = WindowsLibraryLayoutPolicy.FolderToggleWidth,
            Height = WindowsLibraryLayoutPolicy.FolderToggleHeight,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = ToggleForegroundBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(
                WindowsLibraryLayoutPolicy.FolderToggleCornerRadius),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Content = toggleChevron
        };
        toggleButton.Click += ExpandButton_Click;
        toggleButton.PointerEntered += (_, _) => ApplyToggleButtonVisual();
        toggleButton.PointerExited += (_, _) => ApplyToggleButtonVisual();
        AutomationProperties.SetAutomationId(toggleButton, "Library.FolderToggle");
        headerHost = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("36,*"),
            Height = 112,
            Background = Brushes.Transparent,
            Children = { toggleButton, header }
        };
        headerHost.Tapped += HeaderHost_Tapped;
        AutomationProperties.SetAutomationId(
            headerHost,
            "Library.FolderExpanderHeader");
        Grid.SetColumn(header, 1);

        chartsHost = new Border
        {
            IsVisible = false,
            Background = ExpandedBrush,
            BorderBrush = ExpandedStrokeBrush,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Margin = new Thickness(176, 0, 10, 8),
            Padding = new Thickness(10, 5),
            Child = charts
        };
        AutomationProperties.SetAutomationId(chartsHost, "Library.ChartList");

        Child = new StackPanel
        {
            Spacing = 0,
            Children = { headerHost, chartsHost }
        };
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(3);
        AutomationProperties.SetAutomationId(this, "Library.FolderCard");
        DataContextChanged += (_, _) => ApplyCurrentDataContext();
        ApplyCurrentDataContext();
    }

    public LibraryFolderCardDiagnostics CaptureDiagnostics() => new(
        currentView?.Folder.FolderPath,
        currentView?.Folder.DisplayTitle,
        currentView?.VisibleCharts.Count ?? 0,
        charts.Children.Count,
        currentExpanded);

    public bool SetExpanded(bool expanded)
    {
        SynchronizeCurrentViewFromContainer();
        if (currentView == null) return false;
        expansionState.SetExpanded(currentView.Folder.FolderPath, expanded);
        ApplyExpandedState(expanded);
        return true;
    }

    private void ApplyCurrentDataContext() =>
        ApplyCurrentView(DataContext as DesktopLibraryFolderView);

    private void SynchronizeCurrentViewFromContainer()
    {
        var liveView = this
            .FindAncestorOfType<ListBoxItem>(includeSelf: false)?
            .Content as DesktopLibraryFolderView;
        if (liveView == null || ReferenceEquals(liveView, currentView)) return;
        ApplyCurrentView(liveView);
    }

    private void ApplyCurrentView(DesktopLibraryFolderView? view)
    {
        if (!ReferenceEquals(currentView, view))
            viewportAnchorGeneration++;
        currentView = view;
        if (currentView == null)
        {
            IsVisible = false;
            currentExpanded = false;
            chartsHost.IsVisible = false;
            charts.Children.Clear();
            thumbnailImage.Source = null;
            thumbnailImage.IsVisible = false;
            thumbnailPlaceholder.IsVisible = true;
            addedText.Text = string.Empty;
            AutomationProperties.SetName(this, string.Empty);
            AutomationProperties.SetName(expandButton, string.Empty);
            AutomationProperties.SetHelpText(expandButton, string.Empty);
            AutomationProperties.SetName(toggleButton, string.Empty);
            AutomationProperties.SetHelpText(toggleButton, string.Empty);
            AutomationProperties.SetName(headerHost, string.Empty);
            AutomationProperties.SetHelpText(headerHost, string.Empty);
            return;
        }

        IsVisible = true;
        var folder = currentView.Folder;
        titleText.Text = folder.DisplayTitle;
        detailsText.Text = folder.Details;
        relativePathText.Text = folder.RelativeFolderPath;
        formatBadgeText.Text = folder.FormatBadgeText;
        addedText.Text = folder.AddedText;
        AutomationProperties.SetName(
            this,
            $"{folder.DisplayTitle} · {folder.RelativeFolderPath}");

        thumbnailImage.Source = loadThumbnail(folder.CoverPath);
        thumbnailImage.IsVisible = thumbnailImage.Source != null;
        thumbnailPlaceholder.IsVisible = thumbnailImage.Source == null;
        AutomationProperties.SetName(
            thumbnailImage,
            thumbnailImage.Source == null
                ? string.Empty
                : AvaloniaLocalizationCatalog.Format("封面 · {0}", folder.DisplayTitle));

        ApplyExpandedState(expansionState.IsExpanded(folder.FolderPath));
    }

    private void ApplyExpandedState(bool expanded)
    {
        currentExpanded = currentView != null && expanded;
        charts.Children.Clear();
        if (currentView != null && currentExpanded)
            foreach (var chart in currentView.VisibleCharts)
                charts.Children.Add(BuildChartRow(chart));
        chartsHost.IsVisible = currentExpanded;
        toggleChevron.Text = currentExpanded ? "⌃" : "⌄";
        ApplyToggleButtonVisual();
        expandButton.Content = currentExpanded
            ? "收起谱面"
            : AvaloniaLocalizationCatalog.Format("选择谱面  {0}", currentView?.VisibleCharts.Count ?? 0);
        if (currentView == null)
        {
            AutomationProperties.SetName(expandButton, string.Empty);
            AutomationProperties.SetHelpText(expandButton, string.Empty);
            AutomationProperties.SetName(toggleButton, string.Empty);
            AutomationProperties.SetHelpText(toggleButton, string.Empty);
            AutomationProperties.SetName(headerHost, string.Empty);
            AutomationProperties.SetHelpText(headerHost, string.Empty);
            return;
        }
        var actionLabel = currentExpanded ? "收起谱面" : "展开谱面";
        AutomationProperties.SetName(
            expandButton,
            $"{expandButton.Content} · {currentView.Folder.DisplayTitle} · " +
            currentView.Folder.RelativeFolderPath);
        AutomationProperties.SetHelpText(
            expandButton,
            AvaloniaLocalizationCatalog.Format("状态={0}; 谱面数={1}", (currentExpanded ? AvaloniaLocalizationCatalog.Text("已展开") : AvaloniaLocalizationCatalog.Text("已收起")), currentView.VisibleCharts.Count));
        AutomationProperties.SetName(
            toggleButton,
            $"{actionLabel} · {currentView.Folder.DisplayTitle} · " +
            currentView.Folder.RelativeFolderPath);
        AutomationProperties.SetHelpText(
            toggleButton,
            AvaloniaLocalizationCatalog.Format("状态={0}; 谱面数={1}", (currentExpanded ? AvaloniaLocalizationCatalog.Text("已展开") : AvaloniaLocalizationCatalog.Text("已收起")), currentView.VisibleCharts.Count));
        AutomationProperties.SetName(
            headerHost,
            AvaloniaLocalizationCatalog.Format("{0} · 点击卡片 · {1} · {2}", actionLabel, currentView.Folder.DisplayTitle, currentView.Folder.RelativeFolderPath));
        AutomationProperties.SetHelpText(
            headerHost,
            AvaloniaLocalizationCatalog.Format("单击卡片{0}; 状态={1}; 谱面数={2}", actionLabel, (currentExpanded ? AvaloniaLocalizationCatalog.Text("已展开") : AvaloniaLocalizationCatalog.Text("已收起")), currentView.VisibleCharts.Count));
        ToolTip.SetTip(
            headerHost,
            currentExpanded
                ? "单击卡片收起谱面列表"
                : "单击卡片展开谱面列表");
    }

    private Control BuildChartRow(DesktopLibraryChartEntry chart)
    {
        var badge = new Border
        {
            Background = BadgeBrush,
            BorderBrush = BadgeStrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = chart.FormatBadgeText,
                Foreground = new SolidColorBrush(Color.Parse("#C8DFE0")),
                FontSize = 9,
                FontWeight = FontWeight.Bold
            }
        };
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                badge,
                new TextBlock
                {
                    Text = chart.PickerTitle,
                    Foreground = new SolidColorBrush(Color.Parse("#D9DEDF")),
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        var detailLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = chart.PickerDetails,
                    Foreground = new SolidColorBrush(Color.Parse("#89969A")),
                    FontSize = 10
                },
                new TextBlock
                {
                    Text = "  ·  ",
                    Foreground = new SolidColorBrush(Color.Parse("#5E696E")),
                    FontSize = 10
                },
                new TextBlock
                {
                    Text = chart.RelativePath,
                    Foreground = new SolidColorBrush(Color.Parse("#657277")),
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
        var chartText = new StackPanel
        {
            Children = { heading, detailLine }
        };
        var openButton = PrimaryButton("打开编辑", OpenButton_Click);
        openButton.Width = 110;
        openButton.Margin = new Thickness(12, 0, 0, 0);
        openButton.Tag = chart;
        AutomationProperties.SetAutomationId(openButton, "Library.ChartOpen");
        AutomationProperties.SetName(
            openButton,
            AvaloniaLocalizationCatalog.Format("打开编辑 · {0} · {1}", chart.PickerTitle, chart.RelativePath));

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { chartText, openButton }
        };
        Grid.SetColumn(openButton, 1);

        var host = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#2C3337")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8),
            Child = row,
            ContextMenu = createChartContextMenu(chart)
        };
        AutomationProperties.SetAutomationId(host, "Library.ChartRow");
        AutomationProperties.SetName(host, $"{chart.PickerTitle} · {chart.RelativePath}");
        return host;
    }

    private void ExpandButton_Click(object? sender, RoutedEventArgs eventArgs)
    {
        SetExpandedPreservingViewport(!currentExpanded);
        eventArgs.Handled = true;
    }

    private void HeaderHost_Tapped(object? sender, TappedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.Source is Control source &&
            (source is Button ||
             source.GetVisualAncestors().OfType<Button>().Any()))
            return;
        SetExpandedPreservingViewport(!currentExpanded);
        eventArgs.Handled = true;
    }

    private void SetExpandedPreservingViewport(bool expanded)
    {
        SynchronizeCurrentViewFromContainer();
        var scrollViewer = this.FindAncestorOfType<ScrollViewer>(includeSelf: false);
        var anchorY = scrollViewer == null
            ? null
            : this.TranslatePoint(new Point(0, 0), scrollViewer)?.Y;
        var folderPath = currentView?.Folder.FolderPath;
        if (scrollViewer == null ||
            !anchorY.HasValue ||
            string.IsNullOrWhiteSpace(folderPath))
        {
            SetExpanded(expanded);
            return;
        }

        var generation = ++viewportAnchorGeneration;
        var layoutPasses = 0;
        EventHandler? layoutUpdated = null;
        layoutUpdated = (_, _) =>
        {
            if (generation != viewportAnchorGeneration ||
                ++layoutPasses > 24 ||
                !RestoreViewportAnchor(
                    scrollViewer,
                    folderPath,
                    anchorY.Value))
            {
                if (layoutUpdated != null)
                    scrollViewer.LayoutUpdated -= layoutUpdated;
            }
        };
        scrollViewer.LayoutUpdated += layoutUpdated;

        if (!SetExpanded(expanded))
        {
            scrollViewer.LayoutUpdated -= layoutUpdated;
            return;
        }

        RestoreViewportAnchor(scrollViewer, folderPath, anchorY.Value);
        Dispatcher.UIThread.Post(
            () => RestoreViewportAnchor(
                scrollViewer,
                folderPath,
                anchorY.Value),
            DispatcherPriority.Loaded);
        DispatcherTimer.RunOnce(
            () =>
            {
                if (generation == viewportAnchorGeneration)
                    RestoreViewportAnchor(
                        scrollViewer,
                        folderPath,
                        anchorY.Value);
                scrollViewer.LayoutUpdated -= layoutUpdated;
            },
            TimeSpan.FromMilliseconds(180),
            DispatcherPriority.Render);
    }

    private bool RestoreViewportAnchor(
        ScrollViewer scrollViewer,
        string folderPath,
        double anchorY)
    {
        if (!this.IsAttachedToVisualTree() ||
            !scrollViewer.IsAttachedToVisualTree() ||
            !string.Equals(
                currentView?.Folder.FolderPath,
                folderPath,
                StringComparison.OrdinalIgnoreCase))
            return false;
        var currentY = this.TranslatePoint(new Point(0, 0), scrollViewer)?.Y;
        if (!currentY.HasValue) return false;
        var delta = currentY.Value - anchorY;
        if (Math.Abs(delta) < .5) return true;

        var target = WindowsLibraryViewportAnchorPolicy.ResolveVerticalOffset(
            scrollViewer.Offset.Y,
            scrollViewer.Extent.Height,
            scrollViewer.Viewport.Height,
            anchorY,
            currentY.Value);
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, target);
        return true;
    }

    private void ApplyToggleButtonVisual()
    {
        var highlighted = toggleButton.IsPointerOver;
        toggleButton.Background = highlighted
            ? ToggleHoverBrush
            : currentExpanded
                ? ToggleExpandedBrush
                : Brushes.Transparent;
        toggleChevron.Foreground =
            highlighted || currentExpanded
                ? ToggleActiveForegroundBrush
                : ToggleForegroundBrush;
    }

    private async void OpenButton_Click(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { Tag: DesktopLibraryChartEntry chart }) return;
        await openChartAsync(chart.ChartPath);
    }

    private static Button PrimaryButton(
        string text,
        EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 7),
            Background = RaisedBrush,
            Foreground = ActionForegroundBrush,
            BorderBrush = ActionStrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(
                WindowsLibraryLayoutPolicy.FolderActionCornerRadius)
        };
        button.Click += handler;
        return button;
    }
}

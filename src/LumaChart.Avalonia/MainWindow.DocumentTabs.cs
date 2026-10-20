using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaPath = Avalonia.Controls.Shapes.Path;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia;

/// <summary>
/// 0.1.37 document-tab strip overflow. The title-bar tab strip used to be a
/// clipped <see cref="ScrollViewer"/> with no scrolling affordance, so opening
/// enough charts made the right-hand tabs impossible to click. The strip now
/// scrolls with the wheel and exposes edge arrows that only exist while the tab
/// content overflows; the active tab is kept reachable after switches, new
/// tabs, closes and window resizes. The visual tab contract itself
/// (height/badge/close metrics) stays in <see cref="WindowsTitleBarParityPolicy"/>.
/// </summary>
public sealed partial class MainWindow
{
    private ScrollViewer? documentTabScroll;
    private Button? documentTabPreviousButton;
    private Button? documentTabNextButton;
    private DispatcherTimer? documentTabScrollRepeatTimer;
    private Guid? documentTabLastActiveTabId;
    private double documentTabPendingOffset;
    private int documentTabScrollRepeatDirection;
    private bool documentTabRestoreOffsetPending;
    private bool documentTabRevealActivePending;
    private bool documentTabChromeSyncQueued;
    private bool documentTabApplyingOffset;
    private string documentTabChromeStatus = string.Empty;

    private Control BuildDocumentTabStrip(bool tabletLayout)
    {
        var strip = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = tabBar,
            Margin = new Thickness(tabletLayout ? 8 : 14, 0, tabletLayout ? 8 : 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        documentTabScroll = scroll;
        AutomationProperties.SetAutomationId(scroll, "Editor.ChartTabs.Scroll");
        AutomationProperties.SetAutomationId(tabBar, "Editor.ChartTabs.Panel");
        AutomationProperties.SetName(scroll, "谱面标签栏");
        scroll.PropertyChanged += DocumentTabScroll_PropertyChanged;
        // Tunnel: the wheel must scroll the tab strip before any tab button or
        // an outer surface interprets it. It is scoped to this ScrollViewer, so
        // the timeline and library keep their own wheel gestures.
        scroll.AddHandler(
            InputElement.PointerWheelChangedEvent,
            DocumentTabScroll_PointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        scroll.LayoutUpdated += (_, _) =>
        {
            if (documentTabRestoreOffsetPending || documentTabRevealActivePending)
                FlushDocumentTabChrome();
            else
                UpdateDocumentTabChrome();
        };

        var previousButton = CreateDocumentTabScrollButton(left: true);
        documentTabPreviousButton = previousButton;
        var nextButton = CreateDocumentTabScrollButton(left: false);
        documentTabNextButton = nextButton;
        Grid.SetColumn(previousButton, 0);
        strip.Children.Add(previousButton);
        Grid.SetColumn(scroll, 1);
        strip.Children.Add(scroll);
        Grid.SetColumn(nextButton, 2);
        strip.Children.Add(nextButton);
        Closed += (_, _) => EndDocumentTabScrollPress();
        return strip;
    }

    private Button CreateDocumentTabScrollButton(bool left)
    {
        var button = new Button
        {
            Width = WindowsTitleBarParityPolicy.DocumentTabOverflowButtonWidth,
            Height = WindowsTitleBarParityPolicy.DocumentTabOverflowButtonHeight,
            Margin = left
                ? new Thickness(
                    WindowsTitleBarParityPolicy.DocumentTabOverflowButtonEdgeMargin,
                    0,
                    WindowsTitleBarParityPolicy.DocumentTabOverflowButtonGap,
                    0)
                : new Thickness(
                    WindowsTitleBarParityPolicy.DocumentTabOverflowButtonGap,
                    0,
                    WindowsTitleBarParityPolicy.DocumentTabOverflowButtonEdgeMargin,
                    0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            IsTabStop = false,
            IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new AvaloniaPath
            {
                Width = 12,
                Height = 14,
                Data = Geometry.Parse(left ? "M8,2 L3,7 L8,12" : "M4,2 L9,7 L4,12"),
                Stroke = new SolidColorBrush(Color.Parse("#AEB9BC")),
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var direction = left ? -1 : 1;
        AutomationProperties.SetAutomationId(
            button,
            left ? "Editor.ChartTabs.ScrollPrevious" : "Editor.ChartTabs.ScrollNext");
        AutomationProperties.SetName(
            button,
            left ? "向左滚动标签" : "向右滚动标签");
        ToolTip.SetTip(
            button,
            left ? "向左滚动标签（可按住连续滚动）" : "向右滚动标签（可按住连续滚动）");
        button.PointerEntered += (_, _) =>
        {
            if (button.IsEnabled)
                button.Background = new SolidColorBrush(Color.Parse("#262A2D"));
        };
        button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
        // Press-driven paging plus optional hold repeat; a plain click never
        // waits for a Click event, and the button's own hover/pressed visuals
        // stay untouched because these handlers only observe the tunnel phase.
        button.AddHandler(
            InputElement.PointerPressedEvent,
            (_, eventArgs) => BeginDocumentTabScrollPress(direction, button, eventArgs),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        button.AddHandler(
            InputElement.PointerReleasedEvent,
            (_, _) => EndDocumentTabScrollPress(),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        button.AddHandler(
            InputElement.PointerCaptureLostEvent,
            (_, _) => EndDocumentTabScrollPress(),
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        return button;
    }

    /// <summary>Captures the scroll position and active tab before the tab visuals are rebuilt.</summary>
    private void BeginDocumentTabRebuild()
    {
        var scroll = documentTabScroll;
        if (scroll == null) return;
        // Clearing the children coerces the ScrollViewer offset to zero during
        // the next layout pass; the desired offset has to survive that phase on
        // its own (same approach as the track board pan).
        if (!documentTabRestoreOffsetPending)
            documentTabPendingOffset = scroll.Offset.X;
        documentTabRestoreOffsetPending = true;
        var activeId = viewModel.ActiveTab?.Id;
        if (activeId != documentTabLastActiveTabId)
        {
            documentTabLastActiveTabId = activeId;
            documentTabRevealActivePending = true;
        }
    }

    private void CompleteDocumentTabRebuild() => ScheduleDocumentTabChromeSync();

    private void ScheduleDocumentTabChromeSync()
    {
        if (documentTabScroll == null || documentTabChromeSyncQueued) return;
        documentTabChromeSyncQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                documentTabChromeSyncQueued = false;
                FlushDocumentTabChrome();
            },
            DispatcherPriority.Loaded);
    }

    private void FlushDocumentTabChrome()
    {
        if (windowCloseTeardownStarted) return;
        var scroll = documentTabScroll;
        if (scroll == null || !scroll.IsAttachedToVisualTree()) return;
        var viewport = scroll.Viewport.Width;
        if (viewport <= 0) return;
        if (documentTabRevealActivePending && !IsDocumentTabLayoutReady()) return;
        var extent = scroll.Extent.Width;
        var target = documentTabRestoreOffsetPending
            ? documentTabPendingOffset
            : scroll.Offset.X;
        if (documentTabRevealActivePending)
            target = ResolveActiveDocumentTabOffset(target, extent, viewport);
        documentTabRestoreOffsetPending = false;
        documentTabRevealActivePending = false;
        ApplyDocumentTabOffset(target);
    }

    private bool IsDocumentTabLayoutReady()
    {
        if (tabBar.Children.Count == 0) return true;
        if (tabBar.Bounds.Width <= 0) return false;
        foreach (var child in tabBar.Children)
            if (child.Bounds.Width <= 0)
                return false;
        return true;
    }

    private double ResolveActiveDocumentTabOffset(
        double currentOffset,
        double extent,
        double viewport)
    {
        var tabs = viewModel.Tabs;
        var active = viewModel.ActiveTab;
        if (active == null) return currentOffset;
        var index = -1;
        for (var i = 0; i < tabs.Count; i++)
            if (tabs[i].Id == active.Id)
            {
                index = i;
                break;
            }
        if (index < 0 || index >= tabBar.Children.Count) return currentOffset;
        var child = tabBar.Children[index];
        if (child.Bounds.Width <= 0) return currentOffset;
        var origin = child.TranslatePoint(new Point(0, 0), tabBar);
        if (origin is not { } position) return currentOffset;
        return WindowsTitleBarParityPolicy.ResolveDocumentTabRevealOffset(
            currentOffset,
            extent,
            viewport,
            position.X,
            position.X + child.Bounds.Width);
    }

    private void DocumentTabScroll_PropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (documentTabApplyingOffset) return;
        var scroll = documentTabScroll;
        if (scroll == null) return;
        if (eventArgs.Property == ScrollViewer.OffsetProperty)
        {
            if (!documentTabRestoreOffsetPending)
                documentTabPendingOffset = scroll.Offset.X;
            UpdateDocumentTabChrome();
            return;
        }
        if (eventArgs.Property == ScrollViewer.ViewportProperty)
        {
            // A resize can hide the active tab; bring it back into view.
            documentTabRevealActivePending = true;
            ScheduleDocumentTabChromeSync();
            UpdateDocumentTabChrome();
            return;
        }
        if (eventArgs.Property == ScrollViewer.ExtentProperty)
        {
            ScheduleDocumentTabChromeSync();
            UpdateDocumentTabChrome();
        }
    }

    private void DocumentTabScroll_PointerWheelChanged(
        object? sender,
        PointerWheelEventArgs eventArgs)
    {
        if (eventArgs.Handled) return;
        var scroll = documentTabScroll;
        if (scroll == null || !IsDocumentTabOverflowing(scroll)) return;
        var delta = Math.Abs(eventArgs.Delta.X) > Math.Abs(eventArgs.Delta.Y)
            ? eventArgs.Delta.X
            : eventArgs.Delta.Y;
        if (Math.Abs(delta) < .001) return;
        ScrollDocumentTabsTo(
            scroll.Offset.X - delta * WindowsTitleBarParityPolicy.DocumentTabOverflowWheelStep);
        eventArgs.Handled = true;
    }

    private static bool IsDocumentTabOverflowing(ScrollViewer scroll) =>
        WindowsTitleBarParityPolicy.ShouldShowDocumentTabOverflow(
            scroll.Extent.Width,
            scroll.Viewport.Width);

    private void BeginDocumentTabScrollPress(
        int direction,
        Button button,
        PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
        if (!button.IsEnabled || !button.IsVisible) return;
        documentTabScrollRepeatDirection = direction;
        ScrollDocumentTabsByPage(direction);
        var timer = documentTabScrollRepeatTimer ??= CreateDocumentTabScrollRepeatTimer();
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(
            WindowsTitleBarParityPolicy.DocumentTabOverflowRepeatDelayMilliseconds);
        timer.Start();
    }

    private DispatcherTimer CreateDocumentTabScrollRepeatTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(
                WindowsTitleBarParityPolicy.DocumentTabOverflowRepeatDelayMilliseconds)
        };
        timer.Tick += (_, _) =>
        {
            var direction = documentTabScrollRepeatDirection;
            if (direction == 0)
            {
                timer.Stop();
                return;
            }
            var button = direction < 0 ? documentTabPreviousButton : documentTabNextButton;
            if (button is not { IsVisible: true, IsEnabled: true })
            {
                timer.Stop();
                return;
            }
            if (timer.Interval.TotalMilliseconds >=
                WindowsTitleBarParityPolicy.DocumentTabOverflowRepeatDelayMilliseconds)
            {
                timer.Interval = TimeSpan.FromMilliseconds(
                    WindowsTitleBarParityPolicy.DocumentTabOverflowRepeatIntervalMilliseconds);
            }
            ScrollDocumentTabsByRepeatStep(direction);
        };
        return timer;
    }

    private void EndDocumentTabScrollPress()
    {
        documentTabScrollRepeatDirection = 0;
        documentTabScrollRepeatTimer?.Stop();
    }

    private void ScrollDocumentTabsByPage(int direction)
    {
        var scroll = documentTabScroll;
        if (scroll == null) return;
        var viewport = scroll.Viewport.Width;
        if (viewport <= 0) return;
        var page = Math.Max(
            WindowsTitleBarParityPolicy.DocumentTabOverflowMinimumPage,
            viewport * WindowsTitleBarParityPolicy.DocumentTabOverflowPageFraction);
        ScrollDocumentTabsTo(scroll.Offset.X + direction * page);
    }

    private void ScrollDocumentTabsByRepeatStep(int direction)
    {
        var scroll = documentTabScroll;
        if (scroll == null) return;
        ScrollDocumentTabsTo(
            scroll.Offset.X + direction * WindowsTitleBarParityPolicy.DocumentTabOverflowRepeatStep);
    }

    /// <summary>Explicit user scroll: drops any pending rebuild restore/reveal.</summary>
    private void ScrollDocumentTabsTo(double offset)
    {
        if (documentTabScroll == null) return;
        documentTabRestoreOffsetPending = false;
        documentTabRevealActivePending = false;
        ApplyDocumentTabOffset(offset);
    }

    private void ApplyDocumentTabOffset(double offset)
    {
        var scroll = documentTabScroll;
        if (scroll == null) return;
        var maximum = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        var target = Math.Clamp(offset, 0, maximum);
        if (Math.Abs(scroll.Offset.X - target) > .01)
        {
            documentTabApplyingOffset = true;
            try
            {
                scroll.Offset = new Vector(target, 0);
            }
            finally
            {
                documentTabApplyingOffset = false;
            }
        }
        documentTabPendingOffset = scroll.Offset.X;
        UpdateDocumentTabChrome();
    }

    private void UpdateDocumentTabChrome()
    {
        var scroll = documentTabScroll;
        if (scroll == null) return;
        var extent = scroll.Extent.Width;
        var viewport = scroll.Viewport.Width;
        var offset = scroll.Offset.X;
        var overflowing = WindowsTitleBarParityPolicy.ShouldShowDocumentTabOverflow(
            extent,
            viewport);
        SetDocumentTabScrollButtonState(
            documentTabPreviousButton,
            overflowing,
            overflowing && offset > WindowsTitleBarParityPolicy.DocumentTabOverflowEpsilon);
        SetDocumentTabScrollButtonState(
            documentTabNextButton,
            overflowing,
            overflowing &&
            offset < extent - viewport - WindowsTitleBarParityPolicy.DocumentTabOverflowEpsilon);
        UpdateDocumentTabChromeStatus(extent, viewport, offset, overflowing);
    }

    private static void SetDocumentTabScrollButtonState(
        Button? button,
        bool visible,
        bool enabled)
    {
        if (button == null) return;
        if (button.IsVisible != visible)
        {
            button.IsVisible = visible;
            if (!visible) button.Background = Brushes.Transparent;
        }
        if (button.IsEnabled != enabled) button.IsEnabled = enabled;
    }

    private void UpdateDocumentTabChromeStatus(
        double extent,
        double viewport,
        double offset,
        bool overflowing)
    {
        var page = Math.Max(
            WindowsTitleBarParityPolicy.DocumentTabOverflowMinimumPage,
            viewport * WindowsTitleBarParityPolicy.DocumentTabOverflowPageFraction);
        var status = string.Create(
            CultureInfo.InvariantCulture,
            $"Overflow={overflowing}; Tabs={tabBar.Children.Count}; " +
            $"Extent={extent:0.#}; Viewport={viewport:0.#}; Offset={offset:0.#}; " +
            $"Page={page:0.#}; WheelStep={WindowsTitleBarParityPolicy.DocumentTabOverflowWheelStep:0.#}; " +
            $"PreviousVisible={documentTabPreviousButton?.IsVisible == true}; " +
            $"NextVisible={documentTabNextButton?.IsVisible == true}; " +
            $"PreviousEnabled={documentTabPreviousButton?.IsEnabled == true}; " +
            $"NextEnabled={documentTabNextButton?.IsEnabled == true}");
        if (status == documentTabChromeStatus) return;
        documentTabChromeStatus = status;
        if (documentTabScroll != null)
            AutomationProperties.SetHelpText(documentTabScroll, status);
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using LumaChart.Avalonia.Controls;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly Grid trackBoardCanvas = new() { Background = new SolidColorBrush(Color.Parse("#111518")) };
    private readonly ScrollBar trackBoardScrollbar = new() { Orientation = Orientation.Horizontal, Minimum = 0,
        Height = 24, AllowAutoHide = false, Visibility = ScrollBarVisibility.Visible,
        Background = new SolidColorBrush(Color.Parse("#30393D")) };
    private readonly Dictionary<string, EditorTrackPanel> boardFrames = [];
    private bool updatingBoardScroll;
    private bool dismissTrackMenuClick;

    private Control CreateUnifiedBoard(Control mainContent)
    {
        mainTrackFrame = RegisterBoardFrame("main", "主轨", 640, mainContent);
        // Initialize existing overview behavior, then reparent the renderers into common compact containers.
        _ = BuildOverviewPanel(); _ = BuildDifficultyOverviewPanel();
        DetachControl(editorOverview); DetachControl(difficultyOverview);
        RegisterBoardFrame("density", "DENSITY", 96, editorOverview, compact: true);
        RegisterBoardFrame("difficulty", "DIFF", 96, difficultyOverview, compact: true);
        waveformSideSurface.IsHitTestVisible = true;
        spectrogramSideSurface.IsHitTestVisible = true;
        DetachControl(waveformSideSurface); DetachControl(spectrogramSideSurface);
        waveformSideSurface.Margin = new Thickness(0); spectrogramSideSurface.Margin = new Thickness(0);
        RegisterBoardFrame("waveform", "WAVE", 96, waveformSideSurface, compact: true);
        RegisterBoardFrame("spectrogram", "SPECTRUM", 96, spectrogramSideSurface, compact: true);
        waveformPanel.IsVisible = false; spectrogramPanel.IsVisible = false;
        foreach (var key in new[] { "density", "difficulty", "main", "waveform", "spectrogram" }) trackPanels.Children.Add(boardFrames[key]);
        trackPanels.HorizontalAlignment = HorizontalAlignment.Left;
        trackBoardCanvas.Children.Add(trackPanels);
        trackScroller.Content = trackBoardCanvas;
        trackScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        trackScroller.AddHandler(Control.RequestBringIntoViewEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        trackPanels.SizeChanged += (_, _) => UpdateBoardScrollState();
        trackPanels.LayoutUpdated += (_, _) => SyncTrackViewports();
        trackScroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ViewportProperty) UpdateBoardScrollState();
        };
        trackBoardScrollbar.ValueChanged += (_, e) =>
        {
            if (!updatingBoardScroll) trackScroller.Offset = new Vector(e.NewValue, 0);
        };
        AutomationProperties.SetAutomationId(trackBoardScrollbar, "Editor.Board.Scrollbar");
        trackBoardScrollbar.Resources["ScrollBarSize"] = 24d;
        var boardThumb = new Style(selector => selector.OfType<ScrollBar>().Template().OfType<Thumb>());
        boardThumb.Setters.Add(new Setter(HeightProperty, 12d));
        boardThumb.Setters.Add(new Setter(MinHeightProperty, 12d));
        trackBoardScrollbar.Styles.Add(boardThumb);
        ToolTip.SetTip(trackBoardScrollbar, "左右拖动整个轨道底板");
        AutomationProperties.SetAutomationId(trackBoardCanvas, "Editor.Board.Canvas");
        trackBoardCanvas.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source == trackBoardCanvas && e.GetCurrentPoint(trackBoardCanvas).Properties.IsRightButtonPressed)
            {
                timeline.ClearObjectSelection();
                foreach (var view in secondaryTrackViews.Values) view.Surface?.ClearObjectSelection();
                OpenAddTrackMenu(trackBoardCanvas); e.Handled = true;
            }
        });
        // Timeline/companion surfaces consume their own wheel. Only gestures
        // bubbling from unused board space reach this fallback.
        trackBoardCanvas.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.Handled) timeline.HandleCompanionWheel(e);
        });
        AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!dismissTrackMenuClick || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
                e.Source is not Control source || !source.GetLogicalAncestors().Contains(trackBoardCanvas)) return;
            dismissTrackMenuClick = false;
            addTrackContextMenu?.Close();
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        timeline.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Handled) return;
            activeSecondaryTrack = null; RefreshFixedTools(); UpdateEditorAvailability();
        }, RoutingStrategies.Tunnel);
        timeline.SizeChanged += (_, _) => SyncTrackViewports();
        var root = new Grid { RowDefinitions = new RowDefinitions("*,24") };
        root.Children.Add(trackScroller); Grid.SetRow(trackBoardScrollbar, 1); root.Children.Add(trackBoardScrollbar);
        return root;
    }

    private EditorTrackPanel RegisterBoardFrame(string key, string title, double width, Control content, bool compact = false)
    {
        var panel = new EditorTrackPanel(key, title, width, compact);
        panel.TitleBox.IsReadOnly = true;
        panel.SetContent(content);
        panel.MoveRequested += (_, delta) => MoveBoardFrame(key, delta);
        boardFrames[key] = panel;
        return panel;
    }

    private static void DetachControl(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        else if (control.Parent is Decorator decorator) decorator.Child = null;
        else if (control.Parent is ContentControl content) content.Content = null;
    }

    private void MoveBoardFrame(string key, double delta)
    {
        var panel = boardFrames[key];
        var index = trackPanels.Children.IndexOf(panel);
        if (index < 0) return;
        var target = Math.Clamp(index + Math.Sign(delta), 0, trackPanels.Children.Count - 1);
        if (target == index) return;
        trackPanels.Children.RemoveAt(index); trackPanels.Children.Insert(target, panel);
        if (activeTrackWorkspace != null)
        {
            activeTrackWorkspace.BoardOrder.Clear();
            activeTrackWorkspace.BoardOrder.AddRange(trackPanels.Children.OfType<EditorTrackPanel>().Select(p => p.TrackKey));
            activeTrackWorkspace.MarkLayoutChanged();
        }
        UpdateBoardScrollState();
    }

    private void ApplyBoardOrder()
    {
        if (activeTrackWorkspace == null || activeTrackWorkspace.BoardOrder.Count == 0) return;
        var ordered = activeTrackWorkspace.BoardOrder.Where(boardFrames.ContainsKey).Select(key => boardFrames[key])
            .Where(frame => trackPanels.Children.Contains(frame)).ToList();
        ordered.AddRange(trackPanels.Children.OfType<EditorTrackPanel>().Where(p => !ordered.Contains(p)).ToArray());
        trackPanels.Children.Clear();
        foreach (var frame in ordered) trackPanels.Children.Add(frame);
    }

    private void UpdateBoardScrollState()
    {
        var width = trackPanels.Children.OfType<EditorTrackPanel>().Sum(panel => panel.Width) + Math.Max(0, trackPanels.Children.Count - 1) * trackPanels.Spacing;
        trackBoardCanvas.Width = Math.Max(trackScroller.Viewport.Width, width + 120);
        updatingBoardScroll = true;
        try
        {
            trackBoardScrollbar.Maximum = Math.Max(0, trackBoardCanvas.Width - trackScroller.Viewport.Width);
            trackBoardScrollbar.ViewportSize = Math.Max(1, trackScroller.Viewport.Width);
            trackBoardScrollbar.Value = Math.Clamp(trackScroller.Offset.X, 0, trackBoardScrollbar.Maximum);
        }
        finally { updatingBoardScroll = false; }
    }

    private void SyncBoardAudioViewports()
    {
        var anchor = timeline.TranslatePoint(new Point(0, timeline.Bounds.Height * timeline.JudgementLinePosition), trackPanels);
        foreach (var surface in new[] { waveformSideSurface, spectrogramSideSurface })
        {
            var origin = surface.TranslatePoint(new Point(), trackPanels);
            var line = anchor.HasValue && origin.HasValue && surface.Bounds.Height > 0
                ? (anchor.Value.Y - origin.Value.Y) / surface.Bounds.Height : timeline.JudgementLinePosition;
            surface.SetViewport(timeline.CurrentBeat, timeline.PixelsPerBeat, line);
        }
    }
}

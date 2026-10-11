using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Controls;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditorSession, CenterSymmetrySettings> symmetryStates = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TimelineSurface, object> symmetryWired = new();
    private Button? symmetryButton;
    private EditorSession? SymmetryTargetSession => activeSecondaryTrack?.Track.Session ?? viewModel.ActiveTab?.Session;
    private TimelineSurface SymmetryTargetSurface => activeSecondaryTrack?.Surface ?? timeline;

    private void WireSymmetry(TimelineSurface surface)
    {
        if (symmetryWired.TryGetValue(surface, out _)) return;
        symmetryWired.Add(surface, new());
        surface.SymmetryRangeChanged += (_, settings) =>
        {
            if (surface.Session is not { } current) return;
            if (settings != null)
            {
                symmetryStates.Remove(current); symmetryStates.Add(current, settings);
            }
            else symmetryStates.Remove(current);
            RefreshSymmetryButton();
        };
    }

    private void RefreshSymmetryTool()
    {
        if (viewModel.ActiveTab is not { } tab) { timeline.SymmetrySettings = null; RefreshSymmetryButton(); return; }
        Apply(timeline, tab.Session);
        foreach (var view in secondaryTrackViews.Values)
            if (view.Surface is { } surface && view.Track.Session is { } session)
                Apply(surface, session);
        RefreshSymmetryButton();
        void Apply(TimelineSurface surface, EditorSession session)
        {
            WireSymmetry(surface);
            // Fresh sessions start without an active range. Old numeric tool
            // preferences must not unexpectedly enable the new direct gesture.
            surface.SymmetrySettings = symmetryStates.TryGetValue(session, out var settings) && !session.IsReadOnly ? settings : null;
        }
    }

    private void RefreshSymmetryButton()
    {
        if (symmetryButton == null) return;
        var surface = SymmetryTargetSurface;
        var active = surface.SymmetrySettings != null || surface.IsDefiningSymmetry;
        symmetryButton.Content = $"中心对称 {CompactShortcut("ToggleSymmetry")}";
        symmetryButton.Background = active ? AccentBrush : RaisedBrush;
        symmetryButton.IsEnabled = SymmetryTargetSession is { IsReadOnly: false, CanEditNotes: true } && !playtestInProgress;
        ToolTip.SetTip(symmetryButton, "点击后依次点起点、终点（吸附网格）；拖动框的上下边改变时间，左右边改变参与列。绘制自动生成对称按键，重合时跳过；再次点击关闭。");
    }

    private Control BuildSymmetryToolButton()
    {
        symmetryButton = new Button { Height = 34, Margin = new Thickness(0, 5, 0, 0), Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(symmetryButton, "Editor.QuickSymmetry");
        symmetryButton.Click += (_, _) => ToggleSymmetryTool();
        RefreshSymmetryTool();
        return symmetryButton;
    }

    private void ToggleSymmetryTool()
    {
        if (SymmetryTargetSession is not { IsReadOnly: false, CanEditNotes: true } session || playtestInProgress) return;
        var surface = SymmetryTargetSurface;
        if (surface.SymmetrySettings != null || surface.IsDefiningSymmetry)
        {
            surface.CancelSymmetryRange(); symmetryStates.Remove(session);
            viewModel.SetStatus("已关闭中心对称");
        }
        else
        {
            surface.BeginSymmetryRange();
            viewModel.SetStatus("中心对称：点击起点，再点击终点");
        }
        RefreshSymmetryButton(); surface.Focus();
    }
}

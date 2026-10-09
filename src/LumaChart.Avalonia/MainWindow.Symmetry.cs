using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EditorSession, CenterSymmetryToolState> symmetryStates = new();
    private Button? symmetryButton;
    private Flyout? symmetryFlyout;
    private EditorSession? SymmetryTargetSession => activeSecondaryTrack?.Track.Session ?? viewModel.ActiveTab?.Session;
    private TimelineSurface SymmetryTargetSurface => activeSecondaryTrack?.Surface ?? timeline;
    private string SymmetryTargetKey => activeSecondaryTrack?.Track.Id.ToString() ?? "main";

    private CenterSymmetryToolState GetSymmetryState(EditorSession session, string key)
    {
        if (!symmetryStates.TryGetValue(session, out var state))
        {
            state = CenterSymmetryStateStore.Load(viewModel.ActiveTab!.SourcePath, key, session.Document.KeyCount);
            symmetryStates.Add(session, state);
        }
        try { state.Settings.Validate(session.Document.KeyCount); }
        catch (ArgumentException)
        {
            state = new(false, new(BeatPosition.Zero, new BeatPosition(32, 0, 1), Enumerable.Range(0, session.Document.KeyCount).ToArray()));
            symmetryStates.Remove(session); symmetryStates.Add(session, state);
        }
        return state;
    }

    private void RefreshSymmetryTool()
    {
        if (viewModel.ActiveTab is not { } tab)
        {
            timeline.SymmetrySettings = null;
            if (symmetryButton != null) { symmetryButton.IsEnabled = false; symmetryButton.Content = "中心对车\n未启用"; }
            return;
        }
        var main = GetSymmetryState(tab.Session, "main");
        timeline.SymmetrySettings = main.Enabled ? main.Settings : null;
        foreach (var view in secondaryTrackViews.Values)
            if (view.Surface is { } surface && view.Track.Session is { } session)
            {
                var state = GetSymmetryState(session, view.Track.Id.ToString());
                surface.SymmetrySettings = state.Enabled && !session.IsReadOnly ? state.Settings : null;
            }
        if (symmetryButton == null || SymmetryTargetSession is not { } target) return;
        var current = GetSymmetryState(target, SymmetryTargetKey);
        symmetryButton.Content = $"中心对车 {CompactShortcut("ToggleSymmetry")}\n{(current.Enabled ? "已启用" : "未启用")}";
        symmetryButton.Background = current.Enabled ? AccentBrush : RaisedBrush;
        symmetryButton.IsEnabled = !target.IsReadOnly && target.CanEditNotes && !playtestInProgress;
        ToolTip.SetTip(symmetryButton, $"点击配置；{CompactShortcut("ToggleSymmetry")} 切换。范围 {current.Settings.Start.Value:0.####}～{current.Settings.End.Value:0.####} 拍；参与列 {string.Join(", ", current.Settings.Columns.Order().Select(c => c + 1))}。");
    }

    private Control BuildSymmetryToolButton()
    {
        symmetryButton = new Button { Height = 48, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(symmetryButton, "Editor.ToolRail.Symmetry");
        ToolTip.SetTip(symmetryButton, "点击配置中心对称范围和参与列；F6 切换。只联动范围内完整的单键和 LN，可一次撤销。");
        symmetryButton.Click += (_, _) => OpenSymmetryTool();
        return symmetryButton;
    }

    private void SaveSymmetryState(EditorSession session, string key, CenterSymmetryToolState state)
    {
        state.Settings.Validate(session.Document.KeyCount);
        CenterSymmetryStateStore.Save(viewModel.ActiveTab!.SourcePath, key, state);
        symmetryStates.Remove(session); symmetryStates.Add(session, state); RefreshSymmetryTool();
    }

    private void ToggleSymmetryTool()
    {
        if (SymmetryTargetSession is not { } session || session.IsReadOnly || !session.CanEditNotes || playtestInProgress) return;
        try
        {
            var state = GetSymmetryState(session, SymmetryTargetKey);
            SaveSymmetryState(session, SymmetryTargetKey, state with { Enabled = !state.Enabled });
            viewModel.SetStatus(state.Enabled ? "已关闭中心对车" : "已启用中心对车：范围内绘制会生成对称按键");
        }
        catch (Exception ex) { viewModel.SetStatus($"中心对车设置未保存：{ex.Message}"); }
    }

    private void OpenSymmetryTool()
    {
        if (SymmetryTargetSession is not { } session || session.IsReadOnly || !session.CanEditNotes) return;
        var key = SymmetryTargetKey; var surface = SymmetryTargetSurface;
        var state = GetSymmetryState(session, key);
        var enabled = new CheckBox { Content = "启用实时中心对称", IsChecked = state.Enabled };
        var start = new TextBox { Text = state.Settings.Start.ToString() };
        var end = new TextBox { Text = state.Settings.End.ToString() };
        var columns = Enumerable.Range(0, session.Document.KeyCount).Select(lane => new CheckBox
            { Content = (lane + 1).ToString(), IsChecked = state.Settings.Columns.Contains(lane) }).ToArray();
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush };
        symmetryFlyout?.Hide();
        var flyout = symmetryFlyout = new Flyout();
        var tabId = viewModel.ActiveTab!.Id;
        void EnsureTarget()
        {
            if (viewModel.ActiveTab?.Id != tabId || !ReferenceEquals(SymmetryTargetSession, session) || playtestInProgress)
                throw new InvalidOperationException("谱面或轨道已切换，请重新打开中心对车工具。");
        }
        AutomationProperties.SetAutomationId(start, "Symmetry.Start"); AutomationProperties.SetAutomationId(end, "Symmetry.End");
        AutomationProperties.SetAutomationId(enabled, "Symmetry.Enabled");
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var column in columns) { column.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(column); }
        BeatPosition Parse(TextBox box)
        {
            if ((box.Text ?? "").Contains(':') && BeatPosition.TryParse(box.Text, out var beat)) return beat;
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new ArgumentException("请输入有效拍数，或 bar:numerator/denominator 格式。");
            return BeatPosition.FromDouble(value, 1_000_000).Snap(session.SnapDivision);
        }
        CenterSymmetrySettings ReadSettings() => new(Parse(start), Parse(end), columns.Select((c, index) => (c, index)).Where(v => v.c.IsChecked == true).Select(v => v.index).ToArray());
        var setStart = new Button { Content = "当前位置为起点" }; var setEnd = new Button { Content = "当前位置为终点" };
        setStart.Click += (_, _) => start.Text = BeatPosition.FromDouble(surface.CurrentBeat, session.SnapDivision).Snap(session.SnapDivision).ToString();
        setEnd.Click += (_, _) => end.Text = BeatPosition.FromDouble(surface.CurrentBeat, session.SnapDivision).Snap(session.SnapDivision).ToString();
        var fromSelection = new Button { Content = "使用选区范围" };
        fromSelection.Click += (_, _) =>
        {
            var selected = session.Document.Notes.Where(n => session.Selection.Contains(n.Id)).ToArray();
            if (selected.Length == 0) { feedback.Text = "请先选择按键。"; return; }
            start.Text = selected.MinBy(n => n.Beat)!.Beat.ToString();
            end.Text = selected.MaxBy(n => n.EndBeat ?? n.Beat) is { } last ? (last.EndBeat ?? last.Beat).ToString() : end.Text;
        };
        var apply = new Button { Content = "应用设置" }; var complete = new Button { Content = "补齐选中按键" };
        AutomationProperties.SetAutomationId(apply, "Symmetry.Apply"); AutomationProperties.SetAutomationId(complete, "Symmetry.Complete");
        apply.Click += (_, _) =>
        {
            try { EnsureTarget(); SaveSymmetryState(session, key, new(enabled.IsChecked == true, ReadSettings())); flyout.Hide(); surface.Focus(); }
            catch (Exception ex) { feedback.Text = ex.Message; }
        };
        complete.Click += (_, _) =>
        {
            try { EnsureTarget(); var count = CenterSymmetryService.CompleteSelection(session, ReadSettings()); feedback.Text = $"已新增 {count} 个对称按键，可一次撤销。"; }
            catch (Exception ex) { feedback.Text = ex.Message; }
        };
        flyout.Content = new StackPanel { Width = 330, Spacing = 8, Children =
        {
            new TextBlock { Text = "中心对车", FontSize = 18 },
            new TextBlock { Text = "时间与参与列反向对应；部分列按所选列顺序对称。支持拍数或 beat:n/d；拍数输入按当前网格吸附。", TextWrapping = TextWrapping.Wrap },
            enabled, TimingField("起点（拍）", start), TimingField("终点（拍）", end),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { setStart, setEnd } }, fromSelection,
            new TextBlock { Text = "参与列" }, row,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { apply, complete } }, feedback
        } };
        AutomationProperties.SetAutomationId((Control)flyout.Content, "Symmetry.Settings");
        flyout.ShowAt(symmetryButton!);
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private Button? cloneReadOnlyButton;
    private Control BuildPatternLibraryButton()
    {
        var button = new Button { Content = "排列库…", Height = 34, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(button, "Editor.PatternLibrary");
        button.Click += async (_, _) => await ShowPatternLibraryAsync();
        ToolTip.SetTip(button, "查找已保存的排列并放置；收录选区请在排列库轨道内操作。");
        return button;
    }
    private PatternLibrary? patternLibrary;
    private async Task CloneActiveReadOnlyAsync()
    {
        if (viewModel.ActiveTab is not { IsLibraryReadOnly: true } tab) return;
        var entry = viewModel.LibraryEntries.FirstOrDefault(c => string.Equals(c.ChartPath, tab.SourcePath, StringComparison.OrdinalIgnoreCase)) ??
            await Task.Run(() => LumaChart.Application.Libraries.DesktopChartLibraryService.ReadChart(tab.SourcePath, Path.GetDirectoryName(tab.SourcePath)!));
        if (entry != null) await CloneLibraryChartAsync(entry);
    }

    private PatternLibrary Patterns => patternLibrary ??= new PatternLibrary(AvaloniaAppPaths.LocalDataRoot);

    private async Task AddSelectionToPatternTrackAsync()
    {
        try { await AddSelectionToPatternTrackCoreAsync(); }
        catch (Exception ex) { viewModel.SetStatus("排列轨操作未完成，源谱面保留：" + ex.Message); }
    }

    private async Task AddMainSelectionToPatternTrackAsync(EditorTrack target)
    {
        try { await AddSelectionToPatternTrackCoreAsync(target); }
        catch (Exception ex) { viewModel.SetStatus("排列轨操作未完成，源谱面保留：" + ex.Message); }
    }

    private async Task AddSelectionToPatternTrackCoreAsync(EditorTrack? target = null)
    {
        var tab = viewModel.ActiveTab; if (tab == null) return;
        // A track-local capture button names both ends: main selection -> this track.
        // Never infer its destination (or source) from whichever track was focused last.
        var source = target == null ? activeSecondaryTrack?.Track.Session ?? tab.Session : tab.Session;
        var notes = source.Document.Notes.Where(n => source.Selection.Contains(n.Id) && !n.IsDraft).Select(n => n.Clone()).ToArray();
        if (notes.Length == 0) { viewModel.SetStatus("先框选要收录的按键，再点排列库轨道中的「加入主轨选区」。只读谱面也可以选取。"); return; }
        RefreshTrackWorkspace(tab);
        if (target != null && (target.Kind != EditorTrackKind.Pattern || activeTrackWorkspace?.Tracks.Contains(target) != true))
            return;
        var track = target ?? (activeSecondaryTrack?.Track.Kind == EditorTrackKind.Pattern ? activeSecondaryTrack.Track :
            activeTrackWorkspace!.Tracks.LastOrDefault(t => t.Kind == EditorTrackKind.Pattern && t.Visible));
        if (track == null)
        {
            await AddEditorTrackAsync(EditorTrackKind.Pattern);
            track = activeTrackWorkspace!.Tracks.Last(t => t.Kind == EditorTrackKind.Pattern);
        }
        foreach (var note in notes) { note.Id = Guid.NewGuid(); note.Sound = null; note.UnknownData = null; note.Group = 0; note.OnlineCreatorUserId = 0; }
        var count = NotePastePlacement.Commit(track.Session!, notes);
        if (secondaryTrackViews.TryGetValue(track.Id, out var view)) { view.Surface?.NavigateToBeat(notes.Min(n => n.Beat.Value)); view.Surface?.InvalidateVisual(); }
        await activeTrackWorkspace!.SaveAsync();
        viewModel.SetStatus($"已将 {count} 个按键按原拍数放入排列轨；可继续整理后保存命名。");
    }

    private async Task SavePatternTrackAsync(TrackView view)
    {
        var notes = view.Track.Session!.Document.Notes.ToArray();
        if (notes.Length == 0) { viewModel.SetStatus("排列轨为空，先从主轨框选并加入按键。"); return; }
        var defaultName = string.IsNullOrWhiteSpace(view.Track.Name) || view.Track.Name == "排列库"
            ? $"{viewModel.ActiveTab?.DisplayTitle ?? "排列"} · {notes.Min(n => n.Beat.Value):0.##}–{notes.Max(n => (n.EndBeat ?? n.Beat).Value):0.##}拍"
            : view.Track.Name;
        var name = MapperInput(defaultName[..Math.Min(defaultName.Length, 120)], "Pattern.Name");
        name.PlaceholderText = "例如：7K 中心折返 / 第二段 LN";
        var status = MapperText($"{notes.Length:N0} 个按键 · 保存相对拍数，换 BPM 后保持排列节奏。");
        var shape = new PatternShapeSurface { Notes = notes, Keys = view.Track.Session.Document.KeyCount, Height = 220 };
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(18), Children = { shape, name, status } };
        var window = MapperWindow("保存排列", body, 560, 460);
        body.Children.Add(MapperButton("保存到排列库", "Pattern.Save", async () =>
        {
            try
            {
                var pattern = PatternLibrary.Capture(name.Text ?? "", view.Track.Session.Document.KeyCount, notes, viewModel.ActiveTab?.DisplayTitle ?? "", sourceDocument: view.Track.Session.Document);
                await Task.Run(() => Patterns.Save(pattern));
                view.Track.Name = pattern.Name; activeTrackWorkspace?.MarkLayoutChanged();
                window.Close(); viewModel.SetStatus("排列已保存：" + pattern.Name);
            }
            catch (Exception ex) { status.Text = "未保存：" + ex.Message; }
        }));
        await window.ShowDialog(this);
    }

    private async Task ShowPatternLibraryAsync()
    {
        try
        {
            var patterns = await Task.Run(Patterns.List);
            var search = new TextBox { PlaceholderText = "搜索排列名称或来源…" };
            var list = new ListBox { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            AutomationProperties.SetAutomationId(list, "Pattern.LibraryList");
            list.ItemTemplate = new FuncDataTemplate<MapperPattern>((pattern, _) =>
            {
                if (pattern == null) return null;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), Margin = new Thickness(6), ColumnSpacing = 12, Height = 164, VerticalAlignment = VerticalAlignment.Top };
                row.Children.Add(new PatternShapeSurface { Notes = PatternLibrary.Materialize(pattern), Keys = pattern.Keys, Height = 150 });
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 6, Children = {
                    MapperText(pattern.Name), MapperText($"{pattern.Keys}K · {pattern.Notes.Length} 个按键 · {pattern.SpanBeats:0.###} 拍"),
                    new TextBlock { Text = pattern.SourceTitle, FontSize = 12, Foreground = Brushes.Silver, TextWrapping = TextWrapping.Wrap } } };
                Grid.SetColumn(text, 1); row.Children.Add(text); return row;
            }, supportsRecycling: false);
            var status = MapperText(patterns.Length == 0 ? "排列库为空。在底板空白处右击添加排列库轨，选取主轨按键加入后命名保存。" :
                "选择排列后移动鼠标放置。Ctrl+滚轮缩放；Enter 定起点，再按 Enter 定终点并放置。" + (Patterns.ReadWarnings.Count > 0 ? $"\n有 {Patterns.ReadWarnings.Count} 个损坏文件已保留并跳过。" : ""));
            var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 10, Margin = new Thickness(18) };
            body.Children.Add(search); Grid.SetRow(status, 1); body.Children.Add(status); Grid.SetRow(list, 2); body.Children.Add(list);
            var window = MapperWindow("排列库", body, 720, 800);
            void Choose()
            {
                if (list.SelectedItem is not MapperPattern pattern) return;
                var target = activeSecondaryTrack?.Track.Session?.IsReadOnly == false ? activeSecondaryTrack.Surface : timeline;
                if (target?.Session?.IsReadOnly != false) { status.Text = "当前是只读查看。可先克隆谱面，或在可编辑草稿 / 排列轨中放置。"; return; }
                var notes = PatternLibrary.Materialize(pattern);
                if (notes.Max(n => n.Lane) >= target.Session.Document.KeyCount) { status.Text = "此排列的宽度超过目标轨道键数。"; return; }
                window.Close(); StartPendingNotePaste(notes, target);
                pendingLibraryPattern = pattern; target.BeginPatternPaste(notes); target.Focus();
                viewModel.SetStatus("排列虚影：单击放置；Ctrl+滚轮缩放；Enter 固定起点 / 终点；Esc 取消。");
            }
            var actions = new WrapPanel { Children = { MapperButton("取出排列", "Pattern.Use", Choose), MapperButton("关闭", "Pattern.Close", () => window.Close()) } };
            Grid.SetRow(actions, 3); body.Children.Add(actions);
            void Filter() => list.ItemsSource = patterns.Where(p => (p.Name + "\n" + p.SourceTitle).Contains(search.Text ?? "", StringComparison.CurrentCultureIgnoreCase)).ToArray();
            search.TextChanged += (_, _) => Filter(); list.DoubleTapped += (_, _) => Choose();
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Choose(); } };
            Filter(); await window.ShowDialog(this);
        }
        catch (Exception ex) { viewModel.SetStatus("排列库无法打开，原资料保留：" + ex.Message); }
    }
}

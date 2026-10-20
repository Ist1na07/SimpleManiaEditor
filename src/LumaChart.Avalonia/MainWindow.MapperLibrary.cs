using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using Avalonia.Platform.Storage;
using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Controls;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private sealed class MapperActionCommand(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
    private Window MapperWindow(string title, Control content, double width = 860, double height = 720)
    {
        var scale = displayScaleSettings.UiScale;
        var layout = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = content };
        return new Window { Title = title, Width = Math.Min(width * scale, Bounds.Width * .95), Height = Math.Min(height * scale, Bounds.Height * .95),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#1A1E21")),
            Foreground = Brushes.WhiteSmoke, FontSize = 13, Content = layout };
    }
    private static TextBlock MapperText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 4) };
    private static TextBox MapperInput(string text, string id, bool multiline = false)
    {
        var box = new TextBox { Text = text, AcceptsReturn = multiline, MinHeight = multiline ? 110 : 32, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(box, id); return box;
    }
    private static Button MapperButton(string text, string id, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6), Margin = new Thickness(0, 3, 8, 3) };
        AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => action(); return button;
    }
    private async Task ShowLibraryChartDetailsAsync(DesktopLibraryChartEntry chart)
    {
        var personal = viewModel.MapperLibrary.GetAnnotation(chart.ChartPath);
        var tags = MapperInput(string.Join(", ", personal.Tags), "Library.Tags");
        var note = MapperInput(personal.Note, "Library.Note", true);
        var favorite = new CheckBox { Content = "★ 收藏为参考谱面", IsChecked = personal.Favorite };
        var state = new ComboBox { ItemsSource = new[] { "未分类", "创作中", "待检查", "未发布", "已发布", "参考" }, SelectedItem = personal.State, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(state, "Library.State");
        var status = MapperText(""); var body = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        body.Children.Add(MapperText(chart.PickerTitle + "\n" + chart.Details)); body.Children.Add(MapperText(chart.ChartPath));
        body.Children.Add(favorite); body.Children.Add(MapperText("状态")); body.Children.Add(state);
        body.Children.Add(MapperText("标签（逗号分隔，例如：排列参考, LN, 叠键）")); body.Children.Add(tags);
        body.Children.Add(MapperText("个人备注 · 不会写进 .osu")); body.Children.Add(note); body.Children.Add(status);
        var window = MapperWindow("谱面整理", new ScrollViewer { Content = body }, 720, 760);
        var actions = new WrapPanel();
        actions.Children.Add(MapperButton("保存整理信息", "Library.SaveAnnotation", async () =>
        {
            try { status.Text = "正在保存…"; await viewModel.SetPersonalAnnotationsAsync([new(chart.ChartPath, new((tags.Text ?? "").Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries), note.Text ?? "", state.SelectedItem as string ?? "未分类", favorite.IsChecked == true))]); status.Text = "已保存。复制或迁移到另一处曲库后仍保留这些信息。"; }
            catch (Exception exception) { status.Text = "保存失败：" + exception.Message; }
        }));
        var external = !OsuFileSafetyPolicy.IsWithin(chart.ChartPath, viewModel.CreationRoot);
        actions.Children.Add(MapperButton(external ? "只读查看" : "打开编辑", "Library.DetailOpen", async () => { window.Close(); if (external) await OpenLibraryReadOnlyAsync(chart.ChartPath); else await OpenChartAsync(chart.ChartPath); }));
        if (external) actions.Children.Add(MapperButton("克隆到工作目录并编辑", "Library.DetailClone", async () => { window.Close(); await CloneLibraryChartAsync(chart); }));
        actions.Children.Add(MapperButton("复制 / 迁移整套谱面…", "Library.Transfer", async () => { window.Close(); await ShowMapsetTransferAsync(chart); }));
        actions.Children.Add(MapperButton("关闭", "Library.DetailClose", () => window.Close())); body.Children.Add(actions);
        await window.ShowDialog(this);
    }
    private async Task ShowBatchAnnotationsAsync()
    {
        var charts = libraryList.SelectedItems?.OfType<DesktopLibraryFolderView>().SelectMany(view => view.VisibleCharts)
            .DistinctBy(chart => chart.ChartPath, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (charts.Length == 0) { await ShowSettingsMessageAsync("批量整理", "在曲库中按 Ctrl / Shift 选中歌曲后，再点批量整理。搜索过滤掉的难度不会被修改。"); return; }
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        body.Children.Add(MapperText($"整理 {charts.Length:N0} 张可见谱面 · 个人备注保留原样"));
        body.Children.Add(MapperText(string.Join('\n', charts.Take(8).Select(chart => chart.PickerTitle)) + (charts.Length > 8 ? "\n…" : "")));
        var tags = MapperInput("", "Library.BatchTags"); var mode = new ComboBox { ItemsSource = new[] { "添加标签", "移除标签" }, SelectedIndex = 0 };
        var favorite = new ComboBox { ItemsSource = new[] { "收藏状态不变", "加入收藏", "取消收藏" }, SelectedIndex = 0 };
        var state = new ComboBox { ItemsSource = new[] { "发布状态不变", "未分类", "创作中", "待检查", "未发布", "已发布", "参考" }, SelectedIndex = 0 };
        body.Children.Add(MapperText("标签（逗号分隔，未填写则不修改标签）")); body.Children.Add(tags); body.Children.Add(mode); body.Children.Add(favorite); body.Children.Add(state);
        var status = MapperText(""); body.Children.Add(status);
        var apply = MapperButton("应用到以上所选谱面", "Library.BatchApply", async () =>
        {
            try
            {
                var values = (tags.Text ?? "").Split([',', '，', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim().TrimStart('#')).Where(t => t.Length > 0).ToArray();
                var adding = mode.SelectedIndex == 0; var favored = favorite.SelectedIndex; var stateValue = state.SelectedIndex == 0 ? null : state.SelectedItem as string;
                status.Text = "正在批量整理…";
                var existing = await Task.Run(viewModel.MapperLibrary.AllAnnotations);
                var annotations = charts.Select(chart => { var current = existing.GetValueOrDefault(chart.ChartPath) ?? MapperAnnotation.Empty;
                    return new KeyValuePair<string, MapperAnnotation>(chart.ChartPath, current with { Tags = adding ? current.Tags.Concat(values).ToArray() : current.Tags.Except(values, StringComparer.OrdinalIgnoreCase).ToArray(),
                        Favorite = favored == 0 ? current.Favorite : favored == 1, State = stateValue ?? current.State }); }).ToArray();
                await viewModel.SetPersonalAnnotationsAsync(annotations); status.Text = $"已整理 {charts.Length:N0} 张谱面，备注与 .osu 文件保持原样。";
            }
            catch (Exception exception) { status.Text = "操作未完成：" + exception.Message; }
        }); body.Children.Add(apply);
        var window = MapperWindow("批量整理", new ScrollViewer { Content = body }, 700, 760);
        body.Children.Add(MapperButton("关闭", "Library.BatchClose", () => window.Close())); await window.ShowDialog(this);
    }
    private async Task ShowMapsetTransferAsync(DesktopLibraryChartEntry chart)
    {
        if (viewModel.Tabs.Any(tab => tab.Session.IsDirty && OsuFileSafetyPolicy.IsWithin(tab.SourcePath, chart.FolderPath)))
        { await ShowSettingsMessageAsync("先保存谱面", "此歌曲文件夹还有未保存的修改。请先保存，再复制或迁移整套谱面。"); return; }
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        var destination = MapperInput(viewModel.CreationRoot, "Transfer.Destination");
        var move = new CheckBox { Content = "迁移：完成后移走源文件，并保留可回退副本", IsChecked = false };
        AutomationProperties.SetAutomationId(move, "Transfer.Move");
        var transferRunning = false; var transferCancellation = new CancellationTokenSource();
        var result = MapperText("默认复制完整歌曲文件夹，包含所有难度和资源；源文件保留。迁移成功后关闭原位置的编辑标签。Songs 的双向操作请先退出 osu!。重名会建立新文件夹，永不覆盖旧图。");
        AutomationProperties.SetAutomationId(result, "Transfer.Result");
        var files = new ListBox { Height = 180 };
        var acknowledge = new CheckBox { Content = "已核对源、目标和文件清单，执行以上操作", IsChecked = false };
        MapsetTransferPlan? plan = null; var previewGeneration = 0;
        var execute = new Button { Content = "执行预览中的操作", IsEnabled = false, Padding = new Thickness(12, 7) };
        AutomationProperties.SetAutomationId(execute, "Transfer.Execute");
        void Invalidate() { previewGeneration++; plan = null; acknowledge.IsChecked = false; execute.IsEnabled = false; result.Text = "选项已更改，请重新生成预览。"; }
        var observedDestination = destination.Text; var observedMove = move.IsChecked;
        destination.TextChanged += (_, _) => { if (destination.Text == observedDestination) return; observedDestination = destination.Text; Invalidate(); };
        move.IsCheckedChanged += (_, _) => { if (move.IsChecked == observedMove) return; observedMove = move.IsChecked; Invalidate(); };
        acknowledge.IsCheckedChanged += (_, _) => execute.IsEnabled = plan != null && acknowledge.IsChecked == true;
        body.Children.Add(MapperText("源歌曲文件夹\n" + chart.FolderPath)); body.Children.Add(MapperText("目标曲库根目录（创作目录或 Songs）")); body.Children.Add(destination);
        var choices = new WrapPanel();
        choices.Children.Add(MapperButton("使用创作目录", "Transfer.Workspace", () => destination.Text = viewModel.CreationRoot));
        choices.Children.Add(MapperButton("选择目录…", "Transfer.Pick", async () =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择目标曲库根目录", AllowMultiple = false });
            if (selected.FirstOrDefault()?.TryGetLocalPath() is { } path) destination.Text = path;
        })); body.Children.Add(choices); body.Children.Add(move);
        var preview = MapperButton("生成迁移预览", "Transfer.Preview", async () =>
        {
            Invalidate(); result.Text = "正在核对所有文件…";
            var generation = previewGeneration;
            try
            {
                var path = destination.Text ?? ""; var shouldMove = move.IsChecked == true;
                var candidate = await Task.Run(() => MapsetTransferService.Preview(chart.FolderPath, path, shouldMove));
                if (generation != previewGeneration) return;
                plan = candidate;
                result.Text = $"{(plan.Move ? "迁移" : "复制")} {plan.Files.Length:N0} 个文件 · {plan.Bytes / 1048576d:0.0} MiB\n源：{plan.Source}\n目标：{plan.Destination}\n回退入口：曲库顶部「迁移记录」。";
                files.ItemsSource = plan.Files.Select(file => $"{file.RelativePath}  · {file.Bytes:N0} bytes").ToArray();
            }
            catch (Exception exception) { if (generation == previewGeneration) result.Text = exception.Message; }
        });
        body.Children.Add(preview); body.Children.Add(result); body.Children.Add(files); body.Children.Add(acknowledge); body.Children.Add(execute);
        execute.Click += async (_, _) =>
        {
            if (plan == null || transferRunning) return; transferRunning = true; execute.IsEnabled = false; preview.IsEnabled = false; destination.IsEnabled = false; move.IsEnabled = false;
            var executingPlan = plan; choices.IsEnabled = false; acknowledge.IsEnabled = false;
            try
            {
                await viewModel.Transfers.ExecuteAsync(executingPlan, transferCancellation.Token);
                if (executingPlan.Move) foreach (var tab in viewModel.Tabs.Where(t => OsuFileSafetyPolicy.IsWithin(t.SourcePath, executingPlan.Source)).ToArray()) await viewModel.CloseAsync(tab.Id, discardUnsavedChanges: false);
                await viewModel.AddLibraryRootAsync(Path.GetDirectoryName(executingPlan.Destination)!);
                result.Text = "已完成，文件逐一校验通过。整理信息和版本身份已保留；可在迁移记录中回退。"; plan = null;
            }
            catch (Exception exception) { result.Text = "操作未完成：" + exception.Message + "\n请进入迁移记录恢复。源文件或回退副本会保留。"; }
            finally { transferRunning = false; }
        };
        var window = MapperWindow("复制 / 双向迁移", new ScrollViewer { Content = body });
        window.Closing += (_, e) => { if (transferRunning) { e.Cancel = true; result.Text = "操作进行中，请等待完成或点取消。目标发布后将先完成事务再返回。"; } };
        body.Children.Add(MapperButton("取消进行中的复制", "Transfer.Cancel", () => transferCancellation.Cancel()));
        body.Children.Add(MapperButton("关闭", "Transfer.Close", () => window.Close())); await window.ShowDialog(this); transferCancellation.Dispose();
    }
    private async Task ShowTransferHistoryAsync()
    {
        var body = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        var recovering = false;
        body.Children.Add(MapperText("已完成和中断的事务都在这里。回退会先检查目标是否又被编辑；有新修改时会停止，避免覆盖。移动源文件前保留的副本不会自动删除。"));
        try
        {
            foreach (var record in viewModel.Transfers.History())
            {
                var row = new StackPanel { Spacing = 5 };
                var stateLabel = record.State switch { "Complete" => "已完成", "RolledBack" => "已回退", "Preparing" => "复制准备中断", "Publishing" => "目标发布待核对", "Published" => "目标已发布，事务未完成", "RemovingSource" => "源清理中断，可恢复", "RollingBack" => "回退中断，可重试", _ => record.State };
                var label = MapperText($"{record.Time.LocalDateTime:g} · {stateLabel}\n{record.Plan.Source}\n→ {record.Plan.Destination}"); row.Children.Add(label);
                var confirm = new CheckBox { Content = "确认回退此事务，保留原位置的谱面" }; row.Children.Add(confirm);
                var rollback = MapperButton("回退 / 恢复源位置", "Transfer.Rollback", async () =>
                {
                    if (confirm.IsChecked != true) { label.Text += "\n请先勾选确认。"; return; }
                    if (recovering) return;
                    try
                    {
                        if (viewModel.Tabs.Any(tab => tab.Session.IsDirty && (OsuFileSafetyPolicy.IsWithin(tab.SourcePath, record.Plan.Source) || OsuFileSafetyPolicy.IsWithin(tab.SourcePath, record.Plan.Destination))))
                            throw new IOException("相关谱面还有未保存的编辑，请先保存或关闭，再回退。");
                        recovering = true;
                        await viewModel.Transfers.RollbackAsync(record.Plan.Id);
                        foreach (var tab in viewModel.Tabs.Where(tab => OsuFileSafetyPolicy.IsWithin(tab.SourcePath, record.Plan.Destination)).ToArray()) await viewModel.CloseAsync(tab.Id, discardUnsavedChanges: false);
                        await viewModel.RefreshLibraryAsync(); label.Text = "已回退 · " + record.Plan.Source;
                    }
                    catch (Exception exception) { label.Text += "\n回退停止：" + exception.Message; }
                    finally { recovering = false; }
                }); rollback.IsEnabled = record.State != "RolledBack"; row.Children.Add(rollback); body.Children.Add(new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1), BorderBrush = Brushes.DimGray, Child = row });
            }
        }
        catch (Exception exception) { body.Children.Add(MapperText("无法读取迁移日志，原文件已保留：" + exception.Message)); }
        var window = MapperWindow("迁移记录与中断恢复", new ScrollViewer { Content = body });
        window.Closing += (_, e) => { if (recovering) e.Cancel = true; };
        body.Children.Add(MapperButton("关闭", "Transfer.HistoryClose", () => window.Close())); await window.ShowDialog(this);
    }
    private async Task ShowMapperVersionsAsync()
    {
        var tab = viewModel.ActiveTab;
        if (tab == null) { await ShowSettingsMessageAsync("版本历史", "先打开一张谱面。"); return; }
        await StopAudioAsync();
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 8, Margin = new Thickness(18) };
        var message = MapperInput("", "Versions.Message"); message.PlaceholderText = "记录这次修改，例如：第二段改为 LN，准备测图";
        var resources = new CheckBox { Content = "记录歌曲资源（Git 去重保存；更换音乐、背景后建议勾选）", IsChecked = true };
        var left = new ListBox { HorizontalAlignment = HorizontalAlignment.Stretch }; var right = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(left, "Versions.Older"); AutomationProperties.SetAutomationId(right, "Versions.Newer");
        var selectors = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 8 }; selectors.Children.Add(MapperText("历史版本 · 自动保存 / 命名版本")); Grid.SetColumn(right, 1); selectors.Children.Add(right);
        var picture = new MapperDiffSurface(); AutomationProperties.SetAutomationId(picture, "Versions.VisualDiff");
        var comparisonView = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 8 }; comparisonView.Children.Add(left); Grid.SetColumn(picture, 1); comparisonView.Children.Add(picture);
        left.ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<MapperVersion>((version, _) => new TextBlock { Text = version == null ? "" : version.Message + "\n" + version.Time.LocalDateTime.ToString("MM-dd HH:mm:ss") + " · " + version.Id[..7], FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) });
        var details = new ListBox { MaxHeight = 120 }; var status = MapperText("选择左侧历史版本；右侧默认比较当前未保存内容。红色移除，绿色新增，LN 两端和面身都按毫秒对齐。");
        var bottom = new StackPanel { Spacing = 5 }; bottom.Children.Add(status); bottom.Children.Add(details);
        bottom.Children.Add(MapperText("恢复编辑内容可撤销；需要旧音乐 / 背景时，请另存该版本及资源。"));
        var viewport = new Slider { Minimum = 0, Maximum = 1, Value = 0 }; var span = new ComboBox { ItemsSource = new[] { "1 秒", "5 秒", "10 秒", "全曲" }, SelectedIndex = 2, Width = 95 };
        var viewControls = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 }; viewControls.Children.Add(span); Grid.SetColumn(viewport, 1); viewControls.Children.Add(viewport);
        ChartDocument? selectedDocument = null; MapperSemanticDiff? changes = null;
        void RefreshDiff()
        {
            if (left.SelectedItem is not MapperVersion version) return;
            try
            {
                selectedDocument = viewModel.Versions.Read(tab.SourcePath, version.Id);
                var comparison = right.SelectedItem is MapperVersion newer ? viewModel.Versions.Read(tab.SourcePath, newer.Id) : tab.Session.CaptureCommittedDocument();
                changes = MapperVersionStore.Diff(selectedDocument, comparison);
                picture.SetDocuments(selectedDocument, comparison, changes);
                var map = new TimingMap(comparison.Timing); var oldMap = new TimingMap(selectedDocument.Timing);
                var end = Math.Max(map.BeatToMilliseconds(BeatPosition.FromDouble(comparison.EndBeat)), oldMap.BeatToMilliseconds(BeatPosition.FromDouble(selectedDocument.EndBeat)));
                viewport.Minimum = Math.Min(0, changes.Notes.Select(note => note.StartMs).DefaultIfEmpty(0).Min() - 500);
                viewport.Maximum = Math.Max(1000, end); picture.SpanMs = span.SelectedIndex switch { 0 => 1000, 1 => 5000, 3 => Math.Max(1000, end - viewport.Minimum), _ => 10000 };
                picture.StartMs = viewport.Value;
                var rows = changes.Notes.Select(n => $"{(n.Added ? "+" : "−")} {n.StartMs:0} ms · 第 {n.Lane + 1} 列" + (n.EndMs.HasValue ? $" → {n.EndMs:0} ms LN" : " Tap"));
                details.ItemsSource = rows.Concat(changes.Timing).Concat(changes.Fields).Take(2000).ToArray();
                status.Text = changes.Summary + "\n所选版本相对前版的资源更新：" + string.Join(" · ", viewModel.Versions.ChangedResources(tab.SourcePath, version.Id).Take(4));
            }
            catch (Exception exception) { status.Text = "读取版本失败：" + exception.Message; }
        }
        void RefreshList()
        {
            var versions = viewModel.Versions.List(tab.SourcePath); left.ItemsSource = versions; right.ItemsSource = new object[] { "当前编辑内容（含未保存修改）" }.Concat(versions.Cast<object>()).ToArray(); right.SelectedIndex = 0; left.SelectedIndex = versions.Count > 0 ? 0 : -1;
        }
        left.SelectionChanged += (_, _) => { RefreshDiff(); if (changes?.Notes.Count > 0) viewport.Value = Math.Max(viewport.Minimum, changes.Notes[0].StartMs - 500); }; right.SelectionChanged += (_, _) => RefreshDiff(); span.SelectionChanged += (_, _) => RefreshDiff();
        details.SelectionChanged += (_, _) => { if (changes != null && details.SelectedIndex >= 0 && details.SelectedIndex < changes.Notes.Count) viewport.Value = Math.Max(viewport.Minimum, changes.Notes[details.SelectedIndex].StartMs - 500); };
        viewport.ValueChanged += (_, _) => { picture.StartMs = viewport.Value; picture.InvalidateVisual(); };
        var recordControls = new StackPanel { Spacing = 5 }; recordControls.Children.Add(message); recordControls.Children.Add(resources);
        recordControls.Children.Add(MapperButton("保存一个命名版本", "Versions.Create", async () =>
        {
            try { status.Text = "正在记录版本…"; var result = await viewModel.Versions.SaveAsync(tab.SourcePath, tab.Session.CaptureCommittedDocument(), message.Text ?? "版本", resources.IsChecked == true); RefreshList(); if (result == null) status.Text = "内容没有变化，未重复创建版本。"; }
            catch (Exception exception) { status.Text = exception.Message; }
        })); body.Children.Add(recordControls); Grid.SetRow(selectors, 1); body.Children.Add(selectors); Grid.SetRow(viewControls, 2); body.Children.Add(viewControls);
        Grid.SetRow(comparisonView, 3); body.Children.Add(comparisonView); Grid.SetRow(bottom, 4); body.Children.Add(bottom);
        var actions = new WrapPanel(); var confirm = new CheckBox { Content = "将左侧版本恢复为当前编辑内容", Margin = new Thickness(0, 6, 10, 0) }; actions.Children.Add(confirm);
        AutomationProperties.SetAutomationId(confirm, "Versions.RestoreConfirm");
        actions.Children.Add(MapperButton("恢复（可撤销）", "Versions.Restore", async () =>
        {
            if (tab.Session.IsReadOnly) { status.Text = "当前谱面是只读状态，不能恢复。"; return; }
            if (confirm.IsChecked != true || selectedDocument == null || left.SelectedItem is not MapperVersion version) { status.Text = "先选择版本并勾选恢复确认。"; return; }
            try
            {
                var restore = selectedDocument.Clone();
                await viewModel.Versions.SaveAsync(tab.SourcePath, tab.Session.CaptureCommittedDocument(), "恢复前的编辑内容", false);
                tab.Session.Mutate(doc => MapperVersionStore.CopyDocument(doc, restore));
                await viewModel.Versions.SaveAsync(tab.SourcePath, tab.Session.CaptureCommittedDocument(), "恢复至 " + version.Id[..7], false);
                confirm.IsChecked = false; RefreshList(); status.Text = "已恢复到编辑器，可 Ctrl+Z 撤销；按保存才写回谱面。歌曲资源保持当前状态，需要旧资源请另存该版本。";
            }
            catch (Exception exception) { status.Text = exception.Message; }
        }));
        actions.Children.Add(MapperButton("另存左侧版本及资源…", "Versions.Export", async () =>
        {
            if (left.SelectedItem is not MapperVersion version) return;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择父目录，自动新建版本文件夹" });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } parent) return;
            try { var target = Path.Combine(parent, Path.GetFileNameWithoutExtension(tab.SourcePath) + "-" + version.Id[..7] + "-" + DateTime.Now.ToString("HHmmss")); await Task.Run(() => viewModel.Versions.Export(tab.SourcePath, version.Id, target)); status.Text = "已另存：" + target; }
            catch (Exception exception) { status.Text = exception.Message; }
        }));
        var window = MapperWindow("版本历史 · " + tab.DisplayTitle, body, 1180, 980);
        actions.Children.Add(MapperButton("关闭", "Versions.Close", () => window.Close())); Grid.SetRow(actions, 5); body.Children.Add(actions);
        RefreshList(); await window.ShowDialog(this);
    }
}

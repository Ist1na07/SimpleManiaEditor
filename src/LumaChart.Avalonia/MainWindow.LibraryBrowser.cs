using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Controls;
using LumaChart.Core.IO;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private readonly ListBox readOnlyLibraryList = new();
    private readonly ComboBox readOnlyLibrarySource = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox readOnlyLibrarySearch = new() { PlaceholderText = "搜索歌曲/难度/作者，支持 mapper= artist= key= bpm>= 条件" };
    private readonly TextBlock workspaceLibraryCount = MapperText("");
    private readonly TextBlock readOnlyLibraryCount = MapperText("");
    // 0.1.37: the header names the source of the list (local index cache, how old
    // it is, or that a manual refresh is suggested) and owns the only full-scan
    // trigger, so browsing never waits for a directory walk.
    private readonly TextBlock libraryCacheText = MapperText("");
    private Button libraryRefreshButton = new();
    private long browserEntriesVersion;
    private long browserFoldersVersion;
    private string browserExternalCacheOwner = "";
    private HashSet<string>? browserExternalMatches;
    private DesktopLibraryChartEntry[] browserExternalCache = [];
    private string browserWorkCacheOwner = "";
    private DesktopLibraryFolderView[] browserWorkCache = [];
    private bool mapperBrowserDirty = true;
    private string browserRootSignature = "";
    private readonly DispatcherTimer browserSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer workspaceSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private HashSet<string>? readOnlySearchMatches;
    private string readOnlySearchHint = "";
    private int browserSearchGeneration;
    private bool cloningLibraryChart;
    private sealed record BrowserRoot(string Path) { public override string ToString() => Path; }

    private void BuildMapperLibraryBrowser()
    {
        libraryView.RowDefinitions = new RowDefinitions("Auto,*,Auto");
        libraryView.Margin = new Thickness(0);
        libraryTitle.Text = "曲库"; libraryTitle.FontSize = 20;
        libraryCountText.Margin = new Thickness(12, 4, 16, 0);
        var header = new WrapPanel { Margin = new Thickness(16, 10) };
        header.Children.Add(libraryTitle); header.Children.Add(libraryCountText);
        libraryCacheText.Margin = new Thickness(6, 6, 12, 0); libraryCacheText.FontSize = 12;
        libraryCacheText.Foreground = new SolidColorBrush(Color.Parse("#8FA0A2"));
        libraryCacheText.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(libraryCacheText, "Library.CacheStatus");
        header.Children.Add(libraryCacheText);
        header.Children.Add(MapperButton("新建谱面", "Library.New", async () => await CreateChartAsync()));
        header.Children.Add(MapperButton("导入谱面 / 谱包", "Library.Import", async () => await PickAndOpenChartAsync()));
        header.Children.Add(MapperButton("选择工作目录…", "Library.ChooseWorkspace", async () => await PickWorkspaceAsync()));
        header.Children.Add(BuildAddLibraryRootButton());
        libraryRefreshButton = MapperButton("刷新索引", "Library.Refresh", async () => await RefreshLibraryWithErrorUiAsync());
        ToolTip.SetTip(libraryRefreshButton, "曲库浏览只读取本地索引，不扫描磁盘。点此重新扫描曲库目录（Songs 较大时需要一些时间）。");
        header.Children.Add(libraryRefreshButton);
        header.Children.Add(MapperButton("批量整理", "Library.OrganizeSelected", async () => await ShowBatchAnnotationsAsync()));
        // These quick filters must hit the personal annotation fields on both panes:
        // a favourite / unpublished map can live in the editable workspace or in a
        // read-only Songs root, and before 0.1.38 the button only drove the left pane.
        void ApplyQuickSearch(string query) { librarySearchBox.Text = query; readOnlyLibrarySearch.Text = query; }
        header.Children.Add(MapperButton("★ 收藏", "Library.Favorites", () => ApplyQuickSearch("star:true")));
        header.Children.Add(MapperButton("未发布", "Library.Unpublished", () => ApplyQuickSearch("state:未发布")));
        header.Children.Add(MapperButton("迁移记录", "Library.TransferHistory", async () => await ShowTransferHistoryAsync()));
        libraryView.Children.Add(header);
        libraryStatusText.Margin = new Thickness(14, 4, 14, 8);
        libraryStatusText.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(libraryStatusText, 2); libraryView.Children.Add(libraryStatusText);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*"), Margin = new Thickness(12), ColumnSpacing = 8 };
        Grid.SetRow(columns, 1); libraryView.Children.Add(columns);
        columns.Children.Add(new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, [Grid.ColumnProperty] = 1 });
        Grid Pane(string title, Control filter, Control list, TextBlock count)
        {
            var pane = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"), RowSpacing = 8 };
            pane.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold });
            Grid.SetRow(filter, 2); pane.Children.Add(filter); Grid.SetRow(list, 3); pane.Children.Add(list);
            Grid.SetRow(count, 4); pane.Children.Add(count); return pane;
        }
        librarySearchBox.PlaceholderText = "搜索工作目录 · 支持 mapper= / key= / bpm> 条件";
        AutomationProperties.SetAutomationId(librarySearchBox, "Library.Search");
        librarySearchBox.TextChanged += (_, _) => { workspaceSearchTimer.Stop(); workspaceSearchTimer.Start(); };
        workspaceSearchTimer.Tick += async (_, _) =>
        {
            workspaceSearchTimer.Stop();
            try { await viewModel.SetLibrarySearchAsync(librarySearchBox.Text); }
            catch (Exception ex) { workspaceLibraryCount.Text = "搜索未完成：" + ex.Message; }
        };
        libraryList.Background = Brushes.Transparent; libraryList.BorderThickness = new Thickness(0);
        libraryList.SelectionMode = SelectionMode.Multiple;
        ConfigureLibraryListItemStyles();
        AutomationProperties.SetAutomationId(libraryList, "Library.List");
        libraryList.SelectionChanged += (_, _) => UpdateLibrarySelectionUi();
        libraryList.ContextRequested += LibraryList_ContextRequested;
        libraryList.KeyDown += async (_, e) => { if (e.Key == Key.Delete) { e.Handled = true; await DeleteSelectedLibraryFoldersAsync(); } };
        librarySortBox.ItemsSource = new[] { new LibrarySortChoice("最近加入", DesktopLibrarySort.AddedDescending), new LibrarySortChoice("标题 A → Z", DesktopLibrarySort.TitleAscending) };
        librarySortBox.SelectedIndex = viewModel.LibrarySort == DesktopLibrarySort.TitleAscending ? 1 : 0;
        librarySortBox.SelectionChanged += (_, _) => { if (librarySortBox.SelectedItem is LibrarySortChoice choice) viewModel.SetLibrarySort(choice.Sort); };
        var workFilter = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        workFilter.Children.Add(librarySearchBox); Grid.SetColumn(librarySortBox, 1); workFilter.Children.Add(librarySortBox);
        var left = Pane("工作目录 · 可编辑", workFilter, libraryList, workspaceLibraryCount);
        Grid.SetRow(workspaceText, 1); left.Children.Add(workspaceText); columns.Children.Add(left);
        var rightFilter = new StackPanel { Spacing = 6, Children = { readOnlyLibrarySource, readOnlyLibrarySearch } };
        readOnlyLibraryList.ItemTemplate = LibraryChartTemplate();
        readOnlyLibraryList.Background = Brushes.Transparent;
        readOnlyLibraryList.BorderThickness = new Thickness(1);
        readOnlyLibraryList.DoubleTapped += async (_, _) => { if (readOnlyLibraryList.SelectedItem is DesktopLibraryChartEntry chart) await OpenLibraryReadOnlyAsync(chart.ChartPath); };
        readOnlyLibraryList.KeyDown += async (_, e) => { if (e.Key == Key.Enter && readOnlyLibraryList.SelectedItem is DesktopLibraryChartEntry chart) { e.Handled = true; await OpenLibraryReadOnlyAsync(chart.ChartPath); } };
        AutomationProperties.SetAutomationId(readOnlyLibraryList, "Library.ReadOnlyList");
        AutomationProperties.SetAutomationId(readOnlyLibrarySource, "Library.ReadOnlySource");
        AutomationProperties.SetAutomationId(readOnlyLibrarySearch, "Library.ReadOnlySearch");
        var right = Pane("只读曲库 · 原文件保持不变", rightFilter, readOnlyLibraryList, readOnlyLibraryCount);
        var actions = new WrapPanel();
        actions.Children.Add(MapperButton("只读查看", "Library.ViewReadOnly", async () => { if (readOnlyLibraryList.SelectedItem is DesktopLibraryChartEntry chart) await OpenLibraryReadOnlyAsync(chart.ChartPath); }));
        actions.Children.Add(MapperButton("克隆到工作目录并编辑", "Library.CloneEdit", async () => { if (readOnlyLibraryList.SelectedItem is DesktopLibraryChartEntry chart) await CloneLibraryChartAsync(chart); }));
        actions.Children.Add(MapperButton("标签 / 备注", "Library.ReadOnlyDetails", async () => { if (readOnlyLibraryList.SelectedItem is DesktopLibraryChartEntry chart) await ShowLibraryChartDetailsAsync(chart); }));
        actions.Children.Add(MapperButton("移除此曲库链接", "Library.RemoveReadOnlyRoot", async () => { if (readOnlyLibrarySource.SelectedItem is BrowserRoot root) await viewModel.RemoveLibraryRootAsync(root.Path); }));
        Grid.SetRow(actions, 1); right.Children.Add(actions); Grid.SetColumn(right, 2); columns.Children.Add(right);
        readOnlyLibrarySource.SelectionChanged += (_, _) => { mapperBrowserDirty = true; RefreshMapperLibraryBrowser(); };
        readOnlyLibrarySearch.TextChanged += (_, _) => { browserSearchGeneration++; browserSearchTimer.Stop(); browserSearchTimer.Start(); };
        browserSearchTimer.Tick += (_, _) =>
        {
            browserSearchTimer.Stop(); var generation = browserSearchGeneration; var query = readOnlyLibrarySearch.Text ?? "";
            try
            {
                // 0.1.38: osu!-style query (mapper=, key=, bpm<…) over the cached rows.
                // Parsing happens once; the 46k-row pass takes a few milliseconds on
                // the UI thread, so the debounce no longer needs a worker thread.
                var result = viewModel.SearchLibrary(query);
                if (generation != browserSearchGeneration) return;
                readOnlySearchMatches = result.Paths;
                readOnlySearchHint = result.Paths is null ? "" : viewModel.DescribeLibrarySearch(result.Query);
                mapperBrowserDirty = true; RefreshMapperLibraryBrowser();
            }
            catch (Exception ex) { if (generation == browserSearchGeneration) readOnlyLibraryCount.Text = "搜索未完成：" + ex.Message; }
        };
        viewModel.LibraryEntries.CollectionChanged += (_, _) =>
        {
            mapperBrowserDirty = true; browserEntriesVersion++;
            if (!string.IsNullOrWhiteSpace(readOnlyLibrarySearch.Text)) { browserSearchGeneration++; browserSearchTimer.Stop(); browserSearchTimer.Start(); }
        };
        viewModel.LibraryFolders.CollectionChanged += (_, _) => { mapperBrowserDirty = true; browserFoldersVersion++; };
        Closed += (_, _) => { browserSearchTimer.Stop(); workspaceSearchTimer.Stop(); };
    }

    private static FuncDataTemplate<DesktopLibraryChartEntry> LibraryChartTemplate() => new((chart, _) =>
        new StackPanel { Margin = new Thickness(6), Spacing = 3, Children = {
            new TextBlock { Text = chart?.PickerTitle, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = chart?.Details + " · " + chart?.Creator, FontSize = 12, Foreground = Brushes.Silver, TextTrimming = TextTrimming.CharacterEllipsis }
        } }, supportsRecycling: false);

    private void RefreshMapperLibraryBrowser()
    {
        var signature = string.Join('\n', viewModel.LinkedLibraryRoots);
        if (signature != browserRootSignature)
        {
            browserRootSignature = signature; var selected = (readOnlyLibrarySource.SelectedItem as BrowserRoot)?.Path;
            var roots = viewModel.LinkedLibraryRoots.Select(path => new BrowserRoot(path)).ToArray();
            readOnlyLibrarySource.ItemsSource = roots;
            readOnlyLibrarySource.SelectedItem = roots.FirstOrDefault(r => r.Path == selected) ?? roots.FirstOrDefault();
            mapperBrowserDirty = true;
        }
        if (!mapperBrowserDirty) return; mapperBrowserDirty = false;
        var workRoot = OsuFileSafetyPolicy.ResolvePhysicalPath(viewModel.CreationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        static bool Within(string path, string root) => path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        var selectedFolders = libraryList.SelectedItems?.OfType<DesktopLibraryFolderView>().Select(v => v.Folder.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var selectedChart = (readOnlyLibraryList.SelectedItem as DesktopLibraryChartEntry)?.ChartPath;
        // Same idea for the editable pane: classifying every row of a 46k Songs
        // index is only needed when the entries, folders or roots changed.
        var workSignature = browserFoldersVersion + "|" + browserEntriesVersion + "|" + workRoot + "|" + signature;
        if (browserWorkCacheOwner != workSignature)
        {
            browserWorkCache = viewModel.LibraryFolders.Select(v => v.VisibleCharts.All(c => viewModel.IsWorkspaceChart(c.ChartPath)) ? v : new DesktopLibraryFolderView(v.Folder,
                v.VisibleCharts.Where(c => viewModel.IsWorkspaceChart(c.ChartPath)).ToArray())).Where(v => v.VisibleCharts.Count > 0).ToArray();
            browserWorkCacheOwner = workSignature;
        }
        var work = browserWorkCache;
        if (!ReferenceEquals(libraryList.ItemsSource, work)) libraryList.ItemsSource = work;
        foreach (var item in work.Where(v => selectedFolders.Contains(v.Folder.FolderPath)))
            if (libraryList.SelectedItems?.Contains(item) == false) libraryList.SelectedItems.Add(item);
        var source = (readOnlyLibrarySource.SelectedItem as BrowserRoot)?.Path;
        var sourceRoot = source == null ? "" : OsuFileSafetyPolicy.ResolvePhysicalPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // Header index only: no full chart or audio is decoded while browsing.
        // The culture-aware title ordering of a 46k index is the most expensive
        // step here, so it is reused until the entries, source or search result
        // change. Re-entering the library then does no sorting work at all.
        var externalSignature = browserEntriesVersion + "|" + sourceRoot;
        if (browserExternalCacheOwner != externalSignature || !ReferenceEquals(readOnlySearchMatches, browserExternalMatches))
        {
            browserExternalCache = source == null ? [] : viewModel.LibraryEntries.Where(c => Within(c.ChartPath, sourceRoot) &&
                !Within(c.ChartPath, workRoot) && (readOnlySearchMatches == null || readOnlySearchMatches.Contains(c.ChartPath)))
                .OrderBy(c => c.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Version).ToArray();
            browserExternalCacheOwner = externalSignature; browserExternalMatches = readOnlySearchMatches;
        }
        var external = browserExternalCache;
        if (!ReferenceEquals(readOnlyLibraryList.ItemsSource, external)) readOnlyLibraryList.ItemsSource = external;
        readOnlyLibraryList.SelectedItem = external.FirstOrDefault(c => string.Equals(c.ChartPath, selectedChart, StringComparison.OrdinalIgnoreCase));
        workspaceLibraryCount.Text = $"{work.Length:N0} 首歌曲 · {work.Sum(v => v.VisibleCharts.Count):N0} 张谱面 · 含已收录的旧工作区 · 双击难度编辑";
        readOnlyLibraryCount.Text = source == null
            ? "先添加只读 Songs 曲库。仅索引 mania 谱面，不改动原文件。"
            : $"{external.Length:N0} 张 mania 谱面 · 双击只读查看，编辑前克隆" + (readOnlySearchHint.Length == 0 ? "" : " · " + readOnlySearchHint);
    }

    private async Task OpenLibraryReadOnlyAsync(string path)
    {
        try { await viewModel.OpenReadOnlyAsync(path); timeline.Tool = TimelineTool.Select; ShowEditorSurface(); }
        catch (Exception ex) { viewModel.SetStatus("只读查看失败：" + ex.Message); }
    }

    private async Task CloneLibraryChartAsync(DesktopLibraryChartEntry chart)
    {
        if (cloningLibraryChart) return;
        cloningLibraryChart = true;
        try
        {
            viewModel.SetStatus("正在克隆整套谱面并校验资源，请稍候…");
            // Copy the whole mapset transactionally. No write to Songs, and each
            // explicit clone gets a collision-free destination (no stale reuse).
            var plan = await Task.Run(() => MapsetTransferService.Preview(chart.FolderPath, viewModel.CreationRoot, false));
            await viewModel.Transfers.ExecuteAsync(plan);
            var path = Path.Combine(plan.Destination, Path.GetRelativePath(chart.FolderPath, chart.ChartPath));
            viewModel.MapperLibrary.LinkCopies(chart.ChartPath, path);
            await viewModel.RefreshLibraryAsync(); await OpenChartAsync(path);
        }
        catch (Exception ex) { viewModel.SetStatus("克隆未完成，原文件保留：" + ex.Message); }
        finally { cloningLibraryChart = false; }
    }

    private async Task<string?> PickIndexedChartAsync()
    {
        var search = new TextBox { PlaceholderText = "搜索已索引的工作目录和只读曲库…" };
        var list = new ListBox { ItemTemplate = LibraryChartTemplate() };
        var status = MapperText("从曲库选择参考谱面；可先在曲库页面添加 Songs 目录。");
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(16) };
        body.Children.Add(search); Grid.SetRow(status, 1); body.Children.Add(status); Grid.SetRow(list, 2); body.Children.Add(list);
        var window = MapperWindow("添加参考谱面", body, 860, 680);
        var generation = 0; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        void Choose() { if (list.SelectedItem is DesktopLibraryChartEntry chart) window.Close(chart.ChartPath); }
        var actions = new WrapPanel { Children = { MapperButton("添加为参考轨道", "Reference.Choose", Choose), MapperButton("取消", "Reference.Cancel", () => window.Close()) } };
        Grid.SetRow(actions, 3); body.Children.Add(actions);
        list.DoubleTapped += (_, _) => Choose(); list.KeyDown += (_, e) => { if (e.Key == Key.Enter) Choose(); };
        async Task Filter()
        {
            var ticket = ++generation; var query = search.Text ?? "";
            try
            {
                var matches = viewModel.SearchLibraryCharts(query);
                if (ticket != generation) return;
                list.ItemsSource = matches;
            }
            catch (Exception ex) { if (ticket == generation) status.Text = "索引搜索未完成：" + ex.Message; }
        }
        timer.Tick += async (_, _) => { timer.Stop(); await Filter(); };
        search.TextChanged += (_, _) => { generation++; timer.Stop(); timer.Start(); };
        window.Closed += (_, _) => { generation++; timer.Stop(); };
        await Filter(); return await window.ShowDialog<string?>(this);
    }
}
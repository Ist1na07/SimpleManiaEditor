namespace LumaChart.Avalonia.Infrastructure;

public sealed record WindowsLibraryContentState(
    bool ShowLibraryList,
    bool ShowEmptyPanel,
    bool ShowLauncherActions,
    string EmptyTitle,
    string EmptySubtitle);

public static class WindowsLibraryContentPolicy
{
    public const string EmptyLibraryTitle = "开始使用 LumaChart";
    public const string EmptyLibrarySubtitle = "像代码工作区一样，从一个明确的项目入口开始。";
    public const string NoSearchResultsTitle = "没有符合筛选条件的项目";
    public const string NoSearchResultsSubtitle = "当前搜索没有匹配结果；请清空搜索或更换筛选条件。";

    public static WindowsLibraryContentState Resolve(
        int indexedChartCount,
        int visibleFolderCount,
        string? query,
        bool isScanning)
    {
        if (indexedChartCount < 0)
            throw new ArgumentOutOfRangeException(nameof(indexedChartCount));
        if (visibleFolderCount < 0)
            throw new ArgumentOutOfRangeException(nameof(visibleFolderCount));

        var showLibraryList = visibleFolderCount > 0;
        var showEmptyPanel = !showLibraryList && !isScanning;
        var hasIndexedProjects = indexedChartCount > 0;
        var hasSearch = !string.IsNullOrWhiteSpace(query);
        var showNoSearchResults = showEmptyPanel && hasSearch && hasIndexedProjects;

        return new WindowsLibraryContentState(
            ShowLibraryList: showLibraryList,
            ShowEmptyPanel: showEmptyPanel,
            ShowLauncherActions: showEmptyPanel && !hasIndexedProjects,
            EmptyTitle: showNoSearchResults ? NoSearchResultsTitle : EmptyLibraryTitle,
            EmptySubtitle: showNoSearchResults ? NoSearchResultsSubtitle : EmptyLibrarySubtitle);
    }
}

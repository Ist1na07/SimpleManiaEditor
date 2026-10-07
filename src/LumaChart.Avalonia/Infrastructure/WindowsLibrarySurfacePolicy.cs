namespace LumaChart.Avalonia.Infrastructure;

public static class WindowsLibraryLayoutPolicy
{
    public const string ContractVersion = "wpf-1.1.0-library-layout-r3-stable-expand";
    public const bool BringFocusedFolderIntoView = false;
    public const double HeaderActionOuterMargin = 4d;
    public const double HeaderActionGap = HeaderActionOuterMargin * 2d;
    public const double FilterColumnSpacing = 0d;
    public const double SearchSortGap = 10d;
    public const double SearchFontSize = 13d;
    public const string HeaderTitleForeground = "#E1E5E6";
    public const string EmptyTitleForeground = "#E1E6E7";
    public const string EmptySubtitleForeground = "#829095";
    public const string WorkspaceForeground = "#7E898E";
    public const string SearchForeground = "#E0E4E5";
    public const string SearchCaret = "#9EB8BA";
    public const string SortBackground = "#15181A";
    public const string SortForeground = "#E3E6E8";
    public const string SortBorder = "#383E43";
    public const string FolderActionBackground = "#1A1D20";
    public const string FolderActionForeground = "#D0D4D6";
    public const string FolderActionBorder = "#34393D";
    public const double FolderActionCornerRadius = 3d;
    public const double FolderToggleWidth = 28d;
    public const double FolderToggleHeight = 42d;
    public const double FolderToggleCornerRadius = 4d;
    public const string FolderToggleForeground = "#91A9AC";
    public const string FolderToggleActiveForeground = "#CAE0E2";
    public const string FolderToggleHoverBackground = "#283235";
    public const string FolderToggleExpandedBackground = "#222B2E";

    public static global::Avalonia.Thickness FilterMargin =>
        new(20, 16, 20, 0);

    public static global::Avalonia.Thickness ContentMargin =>
        new(20, 0, 20, 12);

    public static global::Avalonia.Thickness FolderItemPadding =>
        new(10, 8);
}

public static class WindowsLibraryViewportAnchorPolicy
{
    public static double ResolveVerticalOffset(
        double currentOffset,
        double extentHeight,
        double viewportHeight,
        double anchorBefore,
        double anchorAfter)
    {
        var maximum = Math.Max(0, extentHeight - viewportHeight);
        var delta = anchorAfter - anchorBefore;
        return Math.Clamp(currentOffset + delta, 0, maximum);
    }
}

public sealed record WindowsLibrarySurfaceState(
    bool LibraryVisible,
    bool EditorVisible,
    bool SaveStateVisible,
    bool EditorStatusVisible,
    string WindowTitle);

public static class WindowsLibrarySurfacePolicy
{
    public const string LibraryWindowTitle = "曲库 — LumaChart Studio";

    public static WindowsLibrarySurfaceState Resolve(
        bool libraryRequested,
        bool hasActiveTab,
        string? chartTitle,
        bool chartIsDirty)
    {
        var libraryVisible = libraryRequested || !hasActiveTab;
        if (libraryVisible)
            return new WindowsLibrarySurfaceState(
                LibraryVisible: true,
                EditorVisible: false,
                SaveStateVisible: false,
                EditorStatusVisible: false,
                WindowTitle: LibraryWindowTitle);

        var normalizedTitle = string.IsNullOrWhiteSpace(chartTitle)
            ? "Untitled"
            : chartTitle.Trim();
        return new WindowsLibrarySurfaceState(
            LibraryVisible: false,
            EditorVisible: true,
            SaveStateVisible: true,
            EditorStatusVisible: true,
            WindowTitle:
                $"{normalizedTitle}{(chartIsDirty ? " *" : string.Empty)} — LumaChart Studio");
    }
}

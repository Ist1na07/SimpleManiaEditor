using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using LumaChart.Application.Libraries;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsLibraryFolderCardSpecs
{
    public static IReadOnlyList<WindowsLibraryFolderCardSpecResult> Run()
    {
        return
        [
            VerifyExpansionStateIsFolderScoped(),
            VerifyRebindingClearsMaterializedRows(),
            VerifyReboundOpenActionUsesCurrentChart(),
            VerifyExpandActionExposesCurrentState(),
            VerifyCardHeaderIsPrimaryExpansionSurface(),
            VerifyWpfCardGeometryContract(),
            VerifyWpfThumbnailPlaceholderContract(),
            VerifyThumbnailRefreshAndFailureFallback(),
            VerifyOsuPngBackgroundResolvesAsThumbnail(),
            VerifyViewportAnchorPolicy()
        ];
    }

    private static WindowsLibraryFolderCardSpecResult VerifyExpansionStateIsFolderScoped()
    {
        var state = new WindowsLibraryFolderExpansionState();
        var first = CreateView("first", 2);
        var second = CreateView("second", 1);
        state.SetExpanded(first.Folder.FolderPath, true);
        state.SetExpanded(second.Folder.FolderPath, false);
        var passed =
            state.IsExpanded(first.Folder.FolderPath) &&
            !state.IsExpanded(second.Folder.FolderPath) &&
            !state.IsExpanded(null);
        return Result(
            "windows-library-card-expansion-folder-scoped",
            passed,
            $"first={state.IsExpanded(first.Folder.FolderPath)}; " +
            $"second={state.IsExpanded(second.Folder.FolderPath)}");
    }

    private static WindowsLibraryFolderCardSpecResult VerifyRebindingClearsMaterializedRows()
    {
        var state = new WindowsLibraryFolderExpansionState();
        var first = CreateView("first", 3);
        var second = CreateView("second", 1);
        state.SetExpanded(first.Folder.FolderPath, true);
        var card = CreateCard(state, _ => Task.CompletedTask);
        card.DataContext = first;
        var firstDiagnostics = card.CaptureDiagnostics();
        card.DataContext = second;
        var secondDiagnostics = card.CaptureDiagnostics();
        var passed =
            firstDiagnostics.IsExpanded &&
            firstDiagnostics.MaterializedChartRowCount == 3 &&
            !secondDiagnostics.IsExpanded &&
            secondDiagnostics.MaterializedChartRowCount == 0 &&
            string.Equals(
                secondDiagnostics.FolderPath,
                second.Folder.FolderPath,
                StringComparison.OrdinalIgnoreCase) &&
            (AutomationProperties.GetName(card) ?? string.Empty)
            .Contains("second", StringComparison.Ordinal);
        return Result(
            "windows-library-card-rebind-clears-stale-rows",
            passed,
            $"firstRows={firstDiagnostics.MaterializedChartRowCount}; " +
            $"secondRows={secondDiagnostics.MaterializedChartRowCount}; " +
            $"secondExpanded={secondDiagnostics.IsExpanded}");
    }

    private static WindowsLibraryFolderCardSpecResult VerifyReboundOpenActionUsesCurrentChart()
    {
        var opened = new List<string>();
        var state = new WindowsLibraryFolderExpansionState();
        var first = CreateView("first", 1);
        var second = CreateView("second", 1);
        state.SetExpanded(first.Folder.FolderPath, true);
        state.SetExpanded(second.Folder.FolderPath, true);
        var card = CreateCard(
            state,
            path =>
            {
                opened.Add(path);
                return Task.CompletedTask;
            });

        card.DataContext = first;
        ClickOnlyOpenButton(card);
        card.DataContext = second;
        ClickOnlyOpenButton(card);
        var passed =
            opened.Count == 2 &&
            string.Equals(
                opened[0],
                first.VisibleCharts[0].ChartPath,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                opened[1],
                second.VisibleCharts[0].ChartPath,
                StringComparison.OrdinalIgnoreCase);
        return Result(
            "windows-library-card-rebound-action-current-chart",
            passed,
            $"opened={string.Join("|", opened.Select(Path.GetFileName))}");
    }

    private static WindowsLibraryFolderCardSpecResult VerifyExpandActionExposesCurrentState()
    {
        var state = new WindowsLibraryFolderExpansionState();
        var first = CreateView("first", 1);
        var second = CreateView("second", 2);
        var card = CreateCard(state, _ => Task.CompletedTask);
        card.DataContext = first;
        var button = FindByAutomationId<Button>(card, "Library.FolderExpand");
        var toggle = FindByAutomationId<Button>(card, "Library.FolderToggle");
        var collapsedName = AutomationProperties.GetName(button) ?? string.Empty;
        var collapsedHelp = AutomationProperties.GetHelpText(button) ?? string.Empty;
        var collapsedToggleName = AutomationProperties.GetName(toggle) ?? string.Empty;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        var expandedName = AutomationProperties.GetName(button) ?? string.Empty;
        var expandedHelp = AutomationProperties.GetHelpText(button) ?? string.Empty;
        var expandedToggleName = AutomationProperties.GetName(toggle) ?? string.Empty;
        var expandedRows = card.CaptureDiagnostics().MaterializedChartRowCount;
        card.DataContext = second;
        var reboundName = AutomationProperties.GetName(button) ?? string.Empty;
        var reboundHelp = AutomationProperties.GetHelpText(button) ?? string.Empty;
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));
        var toggleHelp = AutomationProperties.GetHelpText(toggle) ?? string.Empty;
        var toggleRows = card.CaptureDiagnostics().MaterializedChartRowCount;
        var passed =
            collapsedName.StartsWith("选择谱面  1 · first", StringComparison.Ordinal) &&
            collapsedHelp == "状态=已收起; 谱面数=1" &&
            collapsedToggleName.StartsWith("展开谱面 · first", StringComparison.Ordinal) &&
            expandedName.StartsWith("收起谱面 · first", StringComparison.Ordinal) &&
            expandedHelp == "状态=已展开; 谱面数=1" &&
            expandedToggleName.StartsWith("收起谱面 · first", StringComparison.Ordinal) &&
            expandedRows == 1 &&
            reboundName.StartsWith("选择谱面  2 · second", StringComparison.Ordinal) &&
            reboundHelp == "状态=已收起; 谱面数=2" &&
            toggleHelp == "状态=已展开; 谱面数=2" &&
            toggleRows == 2;
        return Result(
            "windows-library-card-expand-action-current-state",
            passed,
            $"collapsed={collapsedHelp}; expanded={expandedHelp}; " +
            $"rebound={reboundHelp}; toggle={toggleHelp}; " +
            $"rows={expandedRows}/{toggleRows}");
    }

    private static WindowsLibraryFolderCardSpecResult VerifyWpfCardGeometryContract()
    {
        var state = new WindowsLibraryFolderExpansionState();
        var view = CreateView("visual", 1);
        var card = CreateCard(state, _ => Task.CompletedTask);
        card.DataContext = view;
        card.SetExpanded(true);
        var headerHost = FindByAutomationId<Grid>(
            card,
            "Library.FolderExpanderHeader");
        var header = FindByAutomationId<Grid>(card, "Library.FolderHeader");
        var thumbnail = FindByAutomationId<Border>(card, "Library.FolderThumbnail");
        var toggle = FindByAutomationId<Button>(card, "Library.FolderToggle");
        var chartList = FindByAutomationId<Border>(card, "Library.ChartList");
        var added = FindByAutomationId<TextBlock>(card, "Library.FolderAdded");
        var open = FindByAutomationId<Button>(card, "Library.ChartOpen");
        var passed =
            headerHost.Height == 112 &&
            headerHost.ColumnDefinitions[0].Width.Value == 36 &&
            header.Height == 112 &&
            header.ColumnDefinitions.Count == 3 &&
            header.ColumnDefinitions[0].Width.Value == 176 &&
            header.ColumnDefinitions[1].Width.IsStar &&
            header.ColumnDefinitions[2].Width.Value == 150 &&
            thumbnail.Width == 160 &&
            thumbnail.Height == 90 &&
            toggle.Width == WindowsLibraryLayoutPolicy.FolderToggleWidth &&
            toggle.Height == WindowsLibraryLayoutPolicy.FolderToggleHeight &&
            toggle.BorderThickness == new Thickness(0) &&
            toggle.CornerRadius == new CornerRadius(
                WindowsLibraryLayoutPolicy.FolderToggleCornerRadius) &&
            chartList.Margin.Left == 176 &&
            chartList.Margin.Right == 10 &&
            chartList.Margin.Bottom == 8 &&
            chartList.Padding.Left == 10 &&
            chartList.Padding.Top == 5 &&
            added.Text == view.Folder.AddedText &&
            open.Width == 110 &&
            string.Equals(open.Content as string, "打开编辑", StringComparison.Ordinal);
        return Result(
            "windows-library-card-wpf-geometry",
            passed,
            $"header={header.Height}; toggle={toggle.Width}; " +
            $"thumb={thumbnail.Width}x{thumbnail.Height}; " +
            $"chartLeft={chartList.Margin.Left}; open={open.Width}/{open.Content}");
    }

    private static WindowsLibraryFolderCardSpecResult VerifyCardHeaderIsPrimaryExpansionSurface()
    {
        var state = new WindowsLibraryFolderExpansionState();
        var view = CreateView("card-click", 2);
        var card = CreateCard(state, _ => Task.CompletedTask);
        card.DataContext = view;
        var header = FindByAutomationId<Grid>(
            card,
            "Library.FolderExpanderHeader");
        var toggle = FindByAutomationId<Button>(
            card,
            "Library.FolderToggle");
        var collapsedName = AutomationProperties.GetName(header) ?? string.Empty;
        var collapsedHelp =
            AutomationProperties.GetHelpText(header) ?? string.Empty;
        card.SetExpanded(true);
        var expandedName = AutomationProperties.GetName(header) ?? string.Empty;
        var expandedHelp =
            AutomationProperties.GetHelpText(header) ?? string.Empty;
        var passed =
            collapsedName.StartsWith(
                "展开谱面 · 点击卡片 · card-click",
                StringComparison.Ordinal) &&
            collapsedHelp ==
            "单击卡片展开谱面; 状态=已收起; 谱面数=2" &&
            expandedName.StartsWith(
                "收起谱面 · 点击卡片 · card-click",
                StringComparison.Ordinal) &&
            expandedHelp ==
            "单击卡片收起谱面; 状态=已展开; 谱面数=2" &&
            ReferenceEquals(header.Background, Brushes.Transparent) &&
            toggle.Background is SolidColorBrush toggleBrush &&
            toggleBrush.Color == Color.Parse(
                WindowsLibraryLayoutPolicy.FolderToggleExpandedBackground);
        return Result(
            "windows-library-card-header-is-primary-expansion-surface",
            passed,
            $"collapsed={collapsedHelp}; expanded={expandedHelp}; " +
            $"toggle={toggle.Width}x{toggle.Height}");
    }

    private static WindowsLibraryFolderCardSpecResult VerifyWpfThumbnailPlaceholderContract()
    {
        var state = new WindowsLibraryFolderExpansionState();
        var card = CreateCard(state, _ => Task.CompletedTask);
        card.DataContext = CreateView("placeholder", 1);
        var placeholder = FindByAutomationId<LibraryThumbnailPlaceholderSurface>(
            card,
            "Library.FolderThumbnailPlaceholder");
        var image = FindByAutomationId<Image>(
            card,
            "Library.FolderThumbnailImage");
        var placeholderPeer =
            ControlAutomationPeer.CreatePeerForElement(placeholder);
        var passed =
            placeholder.IsVisible &&
            !placeholder.IsHitTestVisible &&
            placeholderPeer != null &&
            string.Equals(
                placeholderPeer.GetName(),
                "无封面占位图",
                StringComparison.Ordinal) &&
            !image.IsVisible &&
            image.Source == null &&
            LibraryThumbnailPlaceholderSurface.LogicalWidth == 160 &&
            LibraryThumbnailPlaceholderSurface.LogicalHeight == 90 &&
            LibraryThumbnailPlaceholderSurface.FrameLeft == 56 &&
            LibraryThumbnailPlaceholderSurface.FrameTop == 24 &&
            LibraryThumbnailPlaceholderSurface.FrameWidth == 48 &&
            LibraryThumbnailPlaceholderSurface.FrameHeight == 35 &&
            LibraryThumbnailPlaceholderSurface.StrokeThickness == 2 &&
            LibraryThumbnailPlaceholderSurface.SunCenterX == 91.5 &&
            LibraryThumbnailPlaceholderSurface.SunCenterY == 32.5 &&
            LibraryThumbnailPlaceholderSurface.SunRadius == 3.5 &&
            LibraryThumbnailPlaceholderSurface.BackgroundColor == "#191D20" &&
            LibraryThumbnailPlaceholderSurface.DrawingColor == "#4D5B60";
        return Result(
            "windows-library-thumbnail-placeholder-wpf-geometry",
            passed,
            "WPF placeholder 160x90; frame=56,24,48,35; " +
            "stroke=2; sun=91.5,32.5,r3.5; colors=#191D20/#4D5B60; " +
            $"peer={placeholderPeer?.GetType().Name ?? "null"}; " +
            $"visible={placeholder.IsVisible}; imageVisible={image.IsVisible}; " +
            $"imageNull={image.Source == null}");
    }

    private static WindowsLibraryFolderCardSpecResult VerifyThumbnailRefreshAndFailureFallback()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "lumachart-library-thumbnail-specs",
            "same-folder"));
        var coverPath = Path.Combine(root, "cover.jpg");
        var state = new WindowsLibraryFolderExpansionState();
        var returnCover = false;
        var requestedPaths = new List<string?>();
        var card = new LibraryFolderCard(
            _ => Task.CompletedTask,
            _ => new ContextMenu(),
            state,
            path =>
            {
                requestedPaths.Add(path);
                return returnCover ? new DrawingImage() : null;
            });

        card.DataContext = CreateView(
            "cover",
            1,
            coverPath,
            root);
        var placeholder = FindByAutomationId<LibraryThumbnailPlaceholderSurface>(
            card,
            "Library.FolderThumbnailPlaceholder");
        var image = FindByAutomationId<Image>(
            card,
            "Library.FolderThumbnailImage");
        var corruptPlaceholder =
            placeholder.IsVisible &&
            !image.IsVisible &&
            image.Source == null;

        returnCover = true;
        card.DataContext = CreateView(
            "cover-valid",
            1,
            coverPath,
            root);
        var validImageName =
            AutomationProperties.GetName(image) ?? string.Empty;
        var validImage =
            !placeholder.IsVisible &&
            image.IsVisible &&
            image.Source is DrawingImage &&
            string.Equals(
                validImageName,
                "封面 · cover-valid",
                StringComparison.Ordinal);

        returnCover = false;
        card.DataContext = CreateView(
            "cover-invalid",
            1,
            coverPath,
            root);
        var invalidatedFallback =
            placeholder.IsVisible &&
            !image.IsVisible &&
            image.Source == null;
        var samePath =
            requestedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        var passed =
            corruptPlaceholder &&
            validImage &&
            invalidatedFallback &&
            requestedPaths.Count == 3 &&
            requestedPaths.All(path => string.Equals(
                path,
                coverPath,
                StringComparison.OrdinalIgnoreCase)) &&
            AvaloniaLibraryThumbnailCache.MaximumRetainedEntries == 512;
        return Result(
            "windows-library-thumbnail-refresh-and-failure-fallback",
            passed,
            $"fallback→image→fallback=" +
            $"{corruptPlaceholder}/{validImage}/{invalidatedFallback}; " +
            $"loaderCalls={requestedPaths.Count}; " +
            $"samePath={samePath}; " +
            $"cacheBound={AvaloniaLibraryThumbnailCache.MaximumRetainedEntries}");
    }

    private static WindowsLibraryFolderCardSpecResult VerifyOsuPngBackgroundResolvesAsThumbnail()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "lumachart-library-osu-png-specs",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backgroundPath = Path.Combine(root, "背景 图.png");
            File.WriteAllBytes(
                backgroundPath,
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            var chartPath = Path.Combine(root, "真实 背景.osu");
            File.WriteAllText(
                chartPath,
                """
                osu file format v14

                [General]
                AudioFilename: audio.mp3
                Mode: 3

                [Metadata]
                Title:Osu PNG Library
                TitleUnicode:曲库 PNG 背景
                Artist:LumaChart
                ArtistUnicode:亮度图表
                Creator:Avalonia Specs
                Version:真实背景

                [Difficulty]
                CircleSize:7

                [Events]
                //Background and Video events
                0,0,"背景 图.png",0,0

                [TimingPoints]
                0,500,4,2,0,100,1,0

                [HitObjects]
                36,192,0,1,0,0:0:0:0:
                """);

            var charts = DesktopChartLibraryService.ScanAsync(root)
                .GetAwaiter()
                .GetResult();
            var folders = DesktopChartLibraryService.GroupByFolder(charts, root);
            var chart = charts.Single();
            var folder = folders.Single();
            var passed =
                string.Equals(
                    chart.ChartPath,
                    chartPath,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    chart.CoverPath,
                    backgroundPath,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    folder.CoverPath,
                    backgroundPath,
                    StringComparison.OrdinalIgnoreCase) &&
                chart.Format == "osu!mania" &&
                chart.FormatBadgeText == "OSU" &&
                chart.DisplayTitle == "曲库 PNG 背景" &&
                chart.KeyCount == 7 &&
                folder.ChartCount == 1;
            return Result(
                "windows-library-osu-png-background-resolves-as-thumbnail",
                passed,
                $"charts={charts.Count}; folders={folders.Count}; " +
                $"cover={Path.GetFileName(chart.CoverPath)}; " +
                $"format={chart.FormatBadgeText}; title={chart.DisplayTitle}; " +
                $"keys={chart.KeyCount}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static WindowsLibraryFolderCardSpecResult VerifyViewportAnchorPolicy()
    {
        var expanded = WindowsLibraryViewportAnchorPolicy.ResolveVerticalOffset(
            currentOffset: 400,
            extentHeight: 1600,
            viewportHeight: 600,
            anchorBefore: 120,
            anchorAfter: 480);
        var collapsed = WindowsLibraryViewportAnchorPolicy.ResolveVerticalOffset(
            currentOffset: expanded,
            extentHeight: 1240,
            viewportHeight: 600,
            anchorBefore: 480,
            anchorAfter: 120);
        var clamped = WindowsLibraryViewportAnchorPolicy.ResolveVerticalOffset(
            currentOffset: 620,
            extentHeight: 900,
            viewportHeight: 600,
            anchorBefore: 100,
            anchorAfter: 500);
        var passed =
            !WindowsLibraryLayoutPolicy.BringFocusedFolderIntoView &&
            Math.Abs(expanded - 760) < .001 &&
            Math.Abs(collapsed - 400) < .001 &&
            Math.Abs(clamped - 300) < .001;
        return Result(
            "windows-library-card-expand-preserves-viewport-anchor",
            passed,
            $"focusBring={WindowsLibraryLayoutPolicy.BringFocusedFolderIntoView}; " +
            $"expanded={expanded}; collapsed={collapsed}; clamped={clamped}");
    }

    private static LibraryFolderCard CreateCard(
        WindowsLibraryFolderExpansionState state,
        Func<string, Task> openChartAsync)
    {
        return new LibraryFolderCard(
            openChartAsync,
            _ => new ContextMenu(),
            state);
    }

    private static void ClickOnlyOpenButton(LibraryFolderCard card)
    {
        var button = card.GetLogicalDescendants()
            .OfType<Button>()
            .Single(control => string.Equals(
                AutomationProperties.GetAutomationId(control),
                "Library.ChartOpen",
                StringComparison.Ordinal));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static T FindByAutomationId<T>(
        LibraryFolderCard card,
        string automationId)
        where T : Control
    {
        return card.GetLogicalDescendants()
            .OfType<T>()
            .Single(control => string.Equals(
                AutomationProperties.GetAutomationId(control),
                automationId,
                StringComparison.Ordinal));
    }

    private static DesktopLibraryFolderView CreateView(
        string name,
        int chartCount,
        string? coverPath = null,
        string? rootOverride = null)
    {
        var root = rootOverride == null
            ? Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "lumachart-library-card-specs",
                name))
            : Path.GetFullPath(rootOverride);
        var charts = Enumerable.Range(0, chartCount)
            .Select(index => new DesktopLibraryChartEntry
            {
                ChartPath = Path.Combine(root, $"{name}-{index}.mc"),
                FolderPath = root,
                RelativePath = $"{name}\\{name}-{index}.mc",
                Format = "Malody",
                Title = name,
                Artist = "LumaChart",
                Creator = "spec",
                Version = $"Spec {index}",
                KeyCount = 4,
                AddedTime = DateTime.UnixEpoch.AddMinutes(index),
                ModifiedTime = DateTime.UnixEpoch.AddMinutes(index)
            })
            .ToArray();
        var folder = new DesktopLibraryFolderEntry
        {
            FolderPath = root,
            RelativeFolderPath = name,
            Charts = charts,
            DisplayTitle = name,
            DisplayArtist = "LumaChart",
            CoverPath = coverPath ?? string.Empty,
            AddedTime = DateTime.UnixEpoch,
            ModifiedTime = DateTime.UnixEpoch,
            SongCount = 1,
            Formats = "Malody",
            SearchText = name,
            FolderSearchText = name
        };
        return new DesktopLibraryFolderView(folder, charts);
    }

    private static WindowsLibraryFolderCardSpecResult Result(
        string name,
        bool passed,
        string detail) => new(name, passed, detail);
}

internal sealed record WindowsLibraryFolderCardSpecResult(
    string Name,
    bool Passed,
    string Detail);

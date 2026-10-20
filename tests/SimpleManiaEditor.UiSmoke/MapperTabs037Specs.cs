using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;

/// <summary>
/// 0.1.37 document-tab overflow regression. Opens 30-40 real tabs through the
/// public view-model API, then proves every tab (and its close button) can be
/// scrolled into the title bar and really clicked, that the active tab stays
/// visible, that the edge arrows exist only while the tabs overflow, and that
/// the tab strip wheel never steals the editor/library wheel.
/// </summary>
internal static class MapperTabs037Specs
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int DefaultChartCount = 36;
    // Three long tabs happened to fit the 1080-wide strip exactly; four leaves
    // the fit/overflow boundary unambiguous in both directions.
    private const int KeepTabCount = 4;

    public static void Run(string root, string[] extraArgs)
    {
        var chartCount = DefaultChartCount;
        for (var i = 0; i + 1 < extraArgs.Length; i++)
            if (extraArgs[i] == "--charts" &&
                int.TryParse(extraArgs[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested))
                chartCount = Math.Clamp(requested, 12, 44);

        PlatformAudioOutputs.Current = new SimulatedAudioOutputFactory();
        var (gameRoot, chartPaths) = CreateFixture(root, chartCount);
        Console.WriteLine($"tabs037 fixture charts={chartCount} root={root}");

        MainWindow? window = null;
        try
        {
            window = new MainWindow([]) { Width = 1440, Height = 960 };
            window.Show();
            Pump(700);

            var model = Field<MainWindowViewModel>(window, "viewModel");
            var scroll = RequiredField<ScrollViewer>(window, "documentTabScroll");
            var tabPanel = RequiredField<StackPanel>(window, "tabBar");
            var previous = RequiredField<Button>(window, "documentTabPreviousButton");
            var next = RequiredField<Button>(window, "documentTabNextButton");
            var timeline = RequiredField<TimelineSurface>(window, "timeline");

            var editableCount = Math.Min(3, chartCount);
            for (var i = 0; i < editableCount; i++)
                Wait(model.OpenAsync(chartPaths[i]));
            for (var i = editableCount; i < chartCount; i++)
                Wait(model.OpenReadOnlyAsync(chartPaths[i]));
            Call(window, "ShowEditorSurface", true);
            Pump(200);

            Require(model.Tabs.Count == chartCount, $"opening {chartCount} charts produced {model.Tabs.Count} tabs");
            Require(tabPanel.Children.Count == chartCount, "tab visuals do not match the open document count");
            AssertTabMetrics(tabPanel, 0);
            DumpLayout(window, scroll, tabPanel, previous, next, "open");

            // ---- Overflow appears, the newest tab and both arrows are present ----
            var extent = scroll.Extent.Width;
            var viewport = scroll.Viewport.Width;
            Require(
                WindowsTitleBarParityPolicy.ShouldShowDocumentTabOverflow(extent, viewport),
                $"opened tabs do not overflow the title bar: extent={extent:0.##} viewport={viewport:0.##}");
            Require(previous.IsVisible && next.IsVisible, "edge arrows are missing while the tabs overflow");
            Require(previous.IsEnabled, "left arrow should be enabled at the right end");
            Require(!next.IsEnabled, "right arrow should be disabled after opening a new tab at the end");
            Require(scroll.Offset.X > 1, "opening a tab did not scroll the strip to the new active tab");
            Require(IsTabFullyVisible(scroll, tabPanel, chartCount - 1), "the newly activated tab is not visible");
            Require(RequireOverflowChromeLayout(scroll, previous, next, overflowing: true), "overflow arrows do not reserve their layout space");
            Require(
                AutomationProperties.GetHelpText(scroll)?.Contains("Overflow=True", StringComparison.Ordinal) == true,
                "tab strip diagnostics do not report overflow");
            Capture(window, root, "tabs037-overflow-end");
            CaptureTitleBar(window, root, "tabs037-overflow-titlebar");

            // ---- Activation through the view model brings the active tab back ----
            model.Activate(model.Tabs[0].Id);
            Pump(150);
            Require(Math.Abs(scroll.Offset.X) < 1, $"activating the first tab did not scroll back: offset={scroll.Offset.X:0.##}");
            Require(!previous.IsEnabled && next.IsEnabled, "arrow enabled state is wrong at the left end");
            Require(IsTabFullyVisible(scroll, tabPanel, 0), "the newly activated first tab is not visible");

            // ---- Every tab is reachable and really clickable ----
            var middleCaptured = false;
            for (var index = 0; index < chartCount; index++)
            {
                EnsureTabVisible(window, scroll, tabPanel, previous, next, index, allowWheel: index % 3 == 1);
                var expectedId = model.Tabs[index].Id;
                ClickPoint(window, FindTabControl(tabPanel, index, "Editor.ChartTab"));
                Require(model.ActiveTab?.Id == expectedId, $"a real mouse click on visible tab {index} did not activate it");
                Require(IsTabFullyVisible(scroll, tabPanel, index), $"tab {index} was not kept visible after activation");
                if (!middleCaptured && index >= chartCount / 2)
                {
                    middleCaptured = true;
                    Capture(window, root, "tabs037-overflow-middle");
                }
            }

            // ---- Wheel scrolls the strip only, and never the editor ----
            model.Activate(model.Tabs[chartCount / 2].Id);
            Pump(150);
            var stripOffsetBeforeWheel = scroll.Offset.X;
            Require(
                stripOffsetBeforeWheel > 1 &&
                stripOffsetBeforeWheel < scroll.Extent.Width - scroll.Viewport.Width - 1,
                "the middle tab should leave wheel room in both directions");
            var timelineBeatBeforeWheel = timeline.CurrentBeat;
            Wheel(window, scroll, new Vector(0, -1));
            Require(
                Math.Abs(scroll.Offset.X - (stripOffsetBeforeWheel + WindowsTitleBarParityPolicy.DocumentTabOverflowWheelStep)) < 1.5,
                $"wheel over the tab strip did not use the configured step: {stripOffsetBeforeWheel:0.##} -> {scroll.Offset.X:0.##}");
            Require(timeline.CurrentBeat == timelineBeatBeforeWheel, "wheel over the tab strip moved the editor timeline");
            Wheel(window, scroll, new Vector(0, 1));
            Require(Math.Abs(scroll.Offset.X - stripOffsetBeforeWheel) < 1.5, "reverse wheel did not restore the tab offset");

            var tabOffsetBeforeEditorWheel = scroll.Offset.X;
            var editorBeatBeforeWheel = timeline.CurrentBeat;
            var timelinePoint = timeline.TranslatePoint(
                new Point(timeline.Bounds.Width * .5, timeline.Bounds.Height * .35),
                window) ?? throw new InvalidOperationException("timeline is not attached to the window");
            window.MouseWheel(timelinePoint, new Vector(0, -1));
            Pump(120);
            Require(Math.Abs(scroll.Offset.X - tabOffsetBeforeEditorWheel) < .01, "wheel over the editor moved the tab strip");
            Require(
                Math.Abs(timeline.CurrentBeat - editorBeatBeforeWheel) > 1e-9,
                "wheel over the editor no longer navigates the timeline");

            // ---- A tab that the old layout hid can be closed with its close button ----
            model.Activate(model.Tabs[chartCount - 1].Id);
            Pump(150);
            var hiddenIndex = 0;
            Require(!IsTabFullyVisible(scroll, tabPanel, hiddenIndex), "the first tab should be hidden after scrolling to the end");
            EnsureTabVisible(window, scroll, tabPanel, previous, next, hiddenIndex, allowWheel: true);
            var hiddenTab = model.Tabs[hiddenIndex];
            var hiddenSource = hiddenTab.SourcePath;
            var countBeforeHiddenClose = model.Tabs.Count;
            ClickPoint(window, FindTabControl(tabPanel, hiddenIndex, "Editor.ChartTab.Close"));
            PumpUntil(() => model.Tabs.Count == countBeforeHiddenClose - 1);
            Pump(150);
            Require(model.Tabs.All(tab => tab.Id != hiddenTab.Id), "the close button did not close the previously hidden tab");
            Require(File.Exists(hiddenSource), "closing a read-only tab deleted its source file");
            Require(tabPanel.Children.Count == model.Tabs.Count, "tab visuals did not follow the hidden-tab close");
            Capture(window, root, "tabs037-closed-hidden-tab");

            // ---- Closing the active tab selects a visible neighbour ----
            var activeBeforeClose = model.ActiveTab ?? throw new InvalidOperationException("no active tab before close");
            var activeIndexBeforeClose = IndexOfTab(model, activeBeforeClose.Id);
            EnsureTabVisible(window, scroll, tabPanel, previous, next, activeIndexBeforeClose, allowWheel: false);
            ClickPoint(window, FindTabControl(tabPanel, activeIndexBeforeClose, "Editor.ChartTab.Close"));
            PumpUntil(() => model.Tabs.All(tab => tab.Id != activeBeforeClose.Id));
            Pump(150);
            var neighbour = model.ActiveTab ?? throw new InvalidOperationException("closing the active tab left no active tab");
            var neighbourIndex = IndexOfTab(model, neighbour.Id);
            Require(neighbourIndex >= 0, "the neighbour selected after closing is not open");
            Require(IsTabFullyVisible(scroll, tabPanel, neighbourIndex), "the tab selected after closing the active tab is not visible");

            // ---- Window resize keeps the active tab reachable ----
            window.Width = 1080;
            window.Height = 720;
            Pump(300);
            DumpLayout(window, scroll, tabPanel, previous, next, "narrow");
            Require(
                WindowsTitleBarParityPolicy.ShouldShowDocumentTabOverflow(scroll.Extent.Width, scroll.Viewport.Width),
                "the narrower window should overflow");
            Require(previous.IsVisible && next.IsVisible, "edge arrows disappeared after narrowing the window");
            neighbourIndex = IndexOfTab(model, (model.ActiveTab ?? neighbour).Id);
            Require(IsTabFullyVisible(scroll, tabPanel, neighbourIndex), "the active tab is not visible after narrowing the window");
            var lastIndex = model.Tabs.Count - 1;
            EnsureTabVisible(window, scroll, tabPanel, previous, next, lastIndex, allowWheel: true);
            var lastId = model.Tabs[lastIndex].Id;
            ClickPoint(window, FindTabControl(tabPanel, lastIndex, "Editor.ChartTab"));
            Require(model.ActiveTab?.Id == lastId, "the far tab was not clickable in the narrow window");
            Require(IsTabFullyVisible(scroll, tabPanel, lastIndex), "the far tab is not visible after clicking it in the narrow window");
            Capture(window, root, "tabs037-narrow-1080");

            // ---- Shrink to a fitting set: no arrows, no reserved space ----
            window.Width = 1720;
            Pump(300);
            DumpLayout(window, scroll, tabPanel, previous, next, "wide-before-close");
            while (model.Tabs.Count > KeepTabCount)
            {
                var closing = model.Tabs[model.Tabs.Count - 1];
                var task = (Task)(Call(window, "CloseTabWithPromptsAsync", closing)
                    ?? throw new InvalidOperationException("close tab call did not return a task"));
                PumpUntil(() => task.IsCompleted);
                task.GetAwaiter().GetResult();
                Pump(80);
            }
            Pump(250);
            DumpLayout(window, scroll, tabPanel, previous, next, "fit");
            Require(model.Tabs.Count == KeepTabCount, $"expected {KeepTabCount} remaining tabs, got {model.Tabs.Count}");
            Require(
                !WindowsTitleBarParityPolicy.ShouldShowDocumentTabOverflow(scroll.Extent.Width, scroll.Viewport.Width),
                $"four tabs at a wide window still overflow: extent={scroll.Extent.Width:0.##} viewport={scroll.Viewport.Width:0.##}");
            Require(!previous.IsVisible && !next.IsVisible, "overflow arrows must not appear when every tab fits");
            Require(Math.Abs(scroll.Offset.X) < .5, "a fitting tab strip must not keep a scroll offset");
            Require(RequireOverflowChromeLayout(scroll, previous, next, overflowing: false), "hidden arrows still reserve title-bar space");
            Require(
                AutomationProperties.GetHelpText(scroll)?.Contains("Overflow=False", StringComparison.Ordinal) == true,
                "tab strip diagnostics do not report the fitting state");
            for (var index = 0; index < KeepTabCount; index++)
            {
                Require(IsTabFullyVisible(scroll, tabPanel, index), $"fitting tab {index} is not visible");
                var expectedId = model.Tabs[index].Id;
                ClickPoint(window, FindTabControl(tabPanel, index, "Editor.ChartTab"));
                Require(model.ActiveTab?.Id == expectedId, $"fitting tab {index} was not clickable");
            }
            Capture(window, root, "tabs037-fit-1720");
            CaptureTitleBar(window, root, "tabs037-fit-titlebar");

            // ---- Narrow again: the arrows come back only while needed ----
            window.Width = 1080;
            Pump(300);
            DumpLayout(window, scroll, tabPanel, previous, next, "fit-narrow");
            Require(
                WindowsTitleBarParityPolicy.ShouldShowDocumentTabOverflow(scroll.Extent.Width, scroll.Viewport.Width),
                $"four tabs at 1080 should overflow: extent={scroll.Extent.Width:0.##} viewport={scroll.Viewport.Width:0.##}");
            Require(previous.IsVisible && next.IsVisible, "edge arrows did not return after narrowing the fitting set");
            Require(RequireOverflowChromeLayout(scroll, previous, next, overflowing: true), "returning arrows do not reserve their layout space");
            lastIndex = model.Tabs.Count - 1;
            EnsureTabVisible(window, scroll, tabPanel, previous, next, lastIndex, allowWheel: true);
            lastId = model.Tabs[lastIndex].Id;
            ClickPoint(window, FindTabControl(tabPanel, lastIndex, "Editor.ChartTab"));
            Require(model.ActiveTab?.Id == lastId, "the last tab was not clickable after narrowing the fitting set");
            Require(IsTabFullyVisible(scroll, tabPanel, lastIndex), "the last tab is not visible after narrowing the fitting set");
            Capture(window, root, "tabs037-narrow-fit");
            window.Width = 1720;
            Pump(300);
            Require(
                !WindowsTitleBarParityPolicy.ShouldShowDocumentTabOverflow(scroll.Extent.Width, scroll.Viewport.Width),
                "widening the window did not hide the overflow arrows");
            Require(!previous.IsVisible && !next.IsVisible, "overflow arrows stayed visible after the tabs fit again");
            Require(Math.Abs(scroll.Offset.X) < .5, "offset was not reset after the tabs fit again");

            // ---- The library keeps its own wheel ----
            Wait(model.AddLibraryRootAsync(gameRoot));
            Call(window, "ShowLibrary", false);
            Pump(300);
            var libraryList = Field<ListBox>(window, "libraryList");
            var tabOffsetBeforeLibraryWheel = scroll.Offset.X;
            var libraryPoint = libraryList.TranslatePoint(
                new Point(libraryList.Bounds.Width * .5, libraryList.Bounds.Height * .5),
                window);
            Require(libraryPoint.HasValue && libraryList.Bounds.Width > 40 && libraryList.Bounds.Height > 40,
                "the library list is not laid out for the wheel isolation check");
            window.MouseWheel(libraryPoint!.Value, new Vector(0, -1));
            Pump(150);
            Require(Math.Abs(scroll.Offset.X - tabOffsetBeforeLibraryWheel) < .01, "wheel over the library moved the tab strip");
            Call(window, "ShowEditorSurface", true);
            Pump(150);

            Console.WriteLine(
                $"PASS tabs037 charts={chartCount} tabs={model.Tabs.Count} " +
                $"overflowExtent={extent:0.#} overflowViewport={viewport:0.#} " +
                $"remainingExtent={scroll.Extent.Width:0.#} remainingViewport={scroll.Viewport.Width:0.#} " +
                $"screenshots={root}");
        }
        finally
        {
            window?.Close();
            Pump(200);
        }
    }

    private static (string GameRoot, List<string> Paths) CreateFixture(string root, int count)
    {
        var gameRoot = Path.Combine(root, "tabs037-game");
        var songs = Path.Combine(gameRoot, "Songs");
        Directory.CreateDirectory(songs);
        File.WriteAllText(Path.Combine(gameRoot, "osu!.exe"), "synthetic, never executed");
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var folder = Path.Combine(songs, "Overflow" + i.ToString("00", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var chart =
                "osu file format v14\n[General]\nAudioFilename: song.wav\nMode:3\n" +
                "[Metadata]\nTitle:" + Title(i) + "\nArtist:Fixture\nCreator:Mapper\nVersion:7K\n" +
                "[Difficulty]\nCircleSize:7\n" +
                "[TimingPoints]\n475,500,4,1,0,0,1,0\n" +
                "[HitObjects]\n36,192,1000,1,0,0:0:0:0:\n109,192,1500,128,0,2000:0:0:0:0:\n";
            var chartPath = Path.Combine(folder, "map.osu");
            File.WriteAllText(chartPath, chart);
            using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "song.wav"))))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + 8000 * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(8000);
                writer.Write(16000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(8000 * 2);
                writer.Write(new byte[8000 * 2]);
            }
            paths.Add(chartPath);
        }
        return (gameRoot, paths);
    }

    private static string Title(int index) => index switch
    {
        0 => "Overflow Tab A Rather Long Title 00",
        1 => "Overflow Tab A Rather Long Title 01",
        2 => "Overflow Tab A Rather Long Title 02",
        3 => "Short 03",
        4 => "Overflow Tab 04",
        _ => "Overflow Tab " + index.ToString("00", CultureInfo.InvariantCulture)
    };

    private static void AssertTabMetrics(StackPanel tabPanel, int index)
    {
        var border = (Control)tabPanel.Children[index];
        Require(Math.Abs(border.Bounds.Height - WindowsTitleBarParityPolicy.DocumentTabHeight) < .01,
            $"tab height changed: {border.Bounds.Height:0.##}");
        var title = FindTabControl(tabPanel, index, "Editor.ChartTab");
        Require(Math.Abs(title.MinWidth - WindowsTitleBarParityPolicy.DocumentTabOpenMinWidth) < .01 &&
                Math.Abs(title.MaxWidth - WindowsTitleBarParityPolicy.DocumentTabOpenMaxWidth) < .01,
            "the tab title width policy differs from WindowsTitleBarParityPolicy");
        var close = FindTabControl(tabPanel, index, "Editor.ChartTab.Close");
        Require(Math.Abs(close.Bounds.Width - WindowsTitleBarParityPolicy.DocumentTabCloseWidth) < .01,
            $"tab close width changed: {close.Bounds.Width:0.##}");
        Require(
            title.Content is TextBlock { TextTrimming: not null } titleText &&
            !ReferenceEquals(titleText.TextTrimming, TextTrimming.None),
            "a capped tab title no longer trims with an ellipsis");
    }

    private static void EnsureTabVisible(
        Window window,
        ScrollViewer scroll,
        StackPanel tabPanel,
        Button previous,
        Button next,
        int index,
        bool allowWheel)
    {
        var attempts = 0;
        while (!IsTabFullyVisible(scroll, tabPanel, index))
        {
            if (++attempts > 240)
            {
                throw new InvalidOperationException(
                    $"tab {index} could not be scrolled into view: " +
                    $"offset={scroll.Offset.X:0.##} extent={scroll.Extent.Width:0.##} " +
                    $"viewport={scroll.Viewport.Width:0.##} count={tabPanel.Children.Count}");
            }
            var tabLeft = TabLeft(tabPanel, index);
            if (tabLeft < scroll.Offset.X)
            {
                Require(previous.IsVisible && previous.IsEnabled, "the left overflow arrow must be available while scrolling left");
                if (allowWheel && attempts % 2 == 0)
                    Wheel(window, scroll, new Vector(0, 1));
                else
                    ClickPoint(window, previous);
            }
            else
            {
                Require(next.IsVisible && next.IsEnabled, "the right overflow arrow must be available while scrolling right");
                if (allowWheel && attempts % 2 == 0)
                    Wheel(window, scroll, new Vector(0, -1));
                else
                    ClickPoint(window, next);
            }
            Pump(10);
        }
    }

    private static bool IsTabFullyVisible(ScrollViewer scroll, StackPanel tabPanel, int index)
    {
        if (index < 0 || index >= tabPanel.Children.Count) return false;
        var child = tabPanel.Children[index];
        if (child.Bounds.Width <= 0) return false;
        var origin = child.TranslatePoint(new Point(0, 0), tabPanel);
        if (origin is not { } position) return false;
        var offset = scroll.Offset.X;
        var viewport = scroll.Viewport.Width;
        return position.X >= offset - .5 &&
               position.X + child.Bounds.Width <= offset + viewport + .5;
    }

    private static double TabLeft(StackPanel tabPanel, int index) =>
        tabPanel.Children[index].TranslatePoint(new Point(0, 0), tabPanel)?.X ?? 0;

    private static int IndexOfTab(MainWindowViewModel model, Guid id)
    {
        for (var i = 0; i < model.Tabs.Count; i++)
            if (model.Tabs[i].Id == id)
                return i;
        return -1;
    }

    private static Button FindTabControl(StackPanel tabPanel, int index, string automationId)
    {
        var tab = (Control)tabPanel.Children[index];
        return tab.GetLogicalDescendants()
                   .OfType<Button>()
                   .FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == automationId)
               ?? throw new InvalidOperationException($"tab {index} has no {automationId} control");
    }

    private static void ClickPoint(Window window, Control control)
    {
        var point = control.TranslatePoint(
            new Point(control.Bounds.Width * .5, control.Bounds.Height * .5),
            window) ?? throw new InvalidOperationException("control is not attached to the window");
        window.MouseDown(point, MouseButton.Left);
        Pump(12);
        window.MouseUp(point, MouseButton.Left);
        Pump(40);
    }

    private static void Wheel(Window window, ScrollViewer scroll, Vector delta)
    {
        var point = scroll.TranslatePoint(
            new Point(scroll.Bounds.Width * .5, scroll.Bounds.Height * .5),
            window) ?? throw new InvalidOperationException("tab strip is not attached to the window");
        window.MouseWheel(point, delta);
        Pump(25);
    }

    private static bool RequireOverflowChromeLayout(ScrollViewer scroll, Button previous, Button next, bool overflowing)
    {
        if (scroll.Parent is not Grid strip) return false;
        var expected = strip.Bounds.Width - scroll.Margin.Left - scroll.Margin.Right;
        if (overflowing)
        {
            expected -= previous.Bounds.Width + previous.Margin.Left + previous.Margin.Right;
            expected -= next.Bounds.Width + next.Margin.Left + next.Margin.Right;
        }
        return Math.Abs(scroll.Bounds.Width - expected) < 1.5;
    }

    private static void DumpLayout(
        Window window,
        ScrollViewer scroll,
        StackPanel tabPanel,
        Button previous,
        Button next,
        string stage)
    {
        var strip = scroll.Parent as Control;
        var contentWidth = tabPanel.Children.Sum(child =>
            child.Bounds.Width + child.Margin.Left + child.Margin.Right);
        Console.WriteLine(
            $"tabs037[{stage}] window={window.Bounds.Width:0.#}x{window.Bounds.Height:0.#} " +
            $"strip={strip?.Bounds.Width:0.#} scroll={scroll.Bounds.Width:0.#} " +
            $"viewport={scroll.Viewport.Width:0.#} extent={scroll.Extent.Width:0.#} " +
            $"content={contentWidth:0.#} offset={scroll.Offset.X:0.#} tabs={tabPanel.Children.Count} " +
            $"prev={previous.IsVisible}/{previous.IsEnabled} next={next.IsVisible}/{next.IsEnabled}");
    }

    private static void Capture(Window window, string root, string name)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("headless frame capture is unavailable");
        frame.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void CaptureTitleBar(Window window, string root, string name)
    {
        var field = window.GetType().GetField("titleBar", Hidden)
            ?? throw new InvalidOperationException("missing private field titleBar");
        var titleBar = (Control)(field.GetValue(window)
            ?? throw new InvalidOperationException("the title bar was not created"));
        // The title bar renders in layout units, which the auto UI scale makes
        // wider than the window's physical bounds; capture its full width so
        // both overflow arrows stay inside the evidence image.
        var width = Math.Max(1, (int)Math.Ceiling(titleBar.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(WindowsTitleBarParityPolicy.TitleBarHeight + 4));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(titleBar);
        bitmap.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static object? Call(object target, string method, params object?[] args)
    {
        var info = target.GetType().GetMethod(method, Hidden)
            ?? throw new InvalidOperationException($"missing private method {method}");
        return info.Invoke(target, args);
    }

    private static T Field<T>(object target, string field)
    {
        var info = target.GetType().GetField(field, Hidden)
            ?? throw new InvalidOperationException($"missing private field {field}");
        return (T)info.GetValue(target)!;
    }

    private static T RequiredField<T>(object target, string field)
        where T : notnull =>
        Field<T?>(target, field)
        ?? throw new InvalidOperationException($"private field {field} was not created");

    private static void Wait(Task task)
    {
        Program.PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Pump(int milliseconds) => Program.Pump(milliseconds);

    private static void PumpUntil(Func<bool> condition) => Program.PumpUntil(condition);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

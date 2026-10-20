using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;

/// <summary>
/// 0.1.38 read-only chart top bar. Builds a synthetic external osu! mania mapset,
/// opens it read-only through the real library UI (select row -&gt; 只读查看), then
/// proves the document top bar carries one clear 编辑 command that clones the whole
/// mapset into the workspace, opens the copy as an editable tab, names the copy,
/// never changes the source bytes, shows a busy state, and cannot publish two
/// copies from a double click. Also proves a failing clone keeps the source and
/// the read-only tab and surfaces an explicit error.
/// </summary>
internal static class ReadOnlyEdit038Specs
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string TopEditId = "Editor.TopBar.EditReadOnly";
    private const string BusyLabel = "正在复制…";

    public static void Run(string root, string[] extraArgs)
    {
        _ = extraArgs;
        // Program.Run only configures the headless platform for the older UI suites,
        // so this one configures it itself (same options as --library-037).
        AppBuilder.Configure<TestApplication>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        PlatformAudioOutputs.Current = new SimulatedAudioOutputFactory();

        var fixture = CreateFixture(root);
        var hashA = Sha256(fixture.ChartA);
        Console.WriteLine($"readonly038 fixture gameRoot={fixture.GameRoot} chartA={fixture.ChartA} shaA={hashA}");

        var summary = new List<string> { "readonly038 summary", "fixture=" + fixture.GameRoot, "sha256(source A)=" + hashA };
        MainWindow? window = null;
        try
        {
            window = new MainWindow([]) { Width = 1440, Height = 960 };
            window.Show();
            Pump(800);
            WaitUntil(() => Field<bool>(window, "initialized"), 30000, "window did not finish initialization");
            var model = Field<MainWindowViewModel>(window, "viewModel");

            Wait(model.AddLibraryRootAsync(fixture.GameRoot));
            Pump(200);
            var readOnlyList = RequiredField<ListBox>(window, "readOnlyLibraryList");
            Call(window, "ShowLibrary", false);
            Pump(300);

            // ---------- open chart A read-only through the real library UI ----------
            SelectReadOnlyEntry(window, readOnlyList, fixture.ChartA);
            ClickPoint(window, Find<Button>(window, "Library.ViewReadOnly"));
            WaitUntil(() => model.ActiveTab is { IsLibraryReadOnly: true } active && SamePath(active.SourcePath, fixture.ChartA),
                30000, "the read-only chart did not open from the library row");
            Pump(500);
            var readOnlyTabId = model.ActiveTab!.Id;
            Require(model.Tabs.Any(tab => tab.Id == readOnlyTabId && tab.IsLibraryReadOnly), "the library row did not produce a read-only tab");

            // ---------- the document top bar owns the action ----------
            var topBar = Find<Control>(window, "Editor.Toolbar.Fixed");
            var edit = Find<Button>(window, TopEditId);
            Require(edit.IsVisible, "top bar 编辑 is not visible for a read-only chart");
            Require(edit.IsEnabled, "top bar 编辑 is disabled for a read-only chart");
            Require(edit.Bounds.Width > 0 && edit.Bounds.Height > 0,
                $"top bar 编辑 is not laid out: {edit.Bounds.Width:0.#}x{edit.Bounds.Height:0.#}");
            Require(topBar.GetLogicalDescendants().Contains(edit), "top bar 编辑 is not inside Editor.Toolbar.Fixed");
            Require(AutomationProperties.GetName(edit)?.Contains("编辑", StringComparison.Ordinal) == true,
                "top bar 编辑 has no accessible name");
            var editCenter = CenterOf(window, edit);
            var timeline = RequiredField<TimelineSurface>(window, "timeline");
            var timelineTop = timeline.TranslatePoint(new Point(0, 0), window)?.Y
                ?? throw new InvalidOperationException("timeline is not attached to the window");
            Require(editCenter.Y < timelineTop,
                $"top bar 编辑 is not above the chart: buttonY={editCenter.Y:0.#} chartTopY={timelineTop:0.#}");
            Require(!AllButtons(window).Any(button => AutomationProperties.GetAutomationId(button) == "Editor.CloneReadOnly"),
                "the left tool rail still shows a duplicate clone button (Editor.CloneReadOnly)");
            Capture(window, root, "readonly038-top-bar-edit");
            CaptureControl(topBar, root, "readonly038-top-bar-crop");
            summary.Add($"topbar button visible/enabled bounds={edit.Bounds.Width:0.#}x{edit.Bounds.Height:0.#} " +
                        $"buttonY={editCenter.Y:0.#} chartTopY={timelineTop:0.#}");

            // ---------- one real pointer click clones and opens the copy ----------
            var tabsBefore = model.Tabs.Select(tab => tab.Id).ToHashSet();
            var clickAt = CenterOf(window, edit);
            var clickStarted = Environment.TickCount64;
            window.MouseDown(clickAt, MouseButton.Left);
            Pump(10);
            window.MouseUp(clickAt, MouseButton.Left);
            var busyObserved = !edit.IsEnabled;
            var busyLabel = LabelText(edit);
            Pump(80);
            WaitUntil(() => model.Tabs.Count > tabsBefore.Count, 30000, "a real pointer click on top bar 编辑 opened no cloned tab");
            var cloneMilliseconds = Environment.TickCount64 - clickStarted;
            Pump(400);
            if (busyObserved) Require(busyLabel == BusyLabel, $"the busy state showed '{busyLabel}' instead of '{BusyLabel}'");

            var readOnlyTab = model.Tabs.FirstOrDefault(tab => tab.Id == readOnlyTabId)
                ?? throw new InvalidOperationException("the read-only tab disappeared after the clone");
            var cloneTab = model.Tabs.First(tab => !tabsBefore.Contains(tab.Id));
            Require(model.Tabs.Count == tabsBefore.Count + 1,
                $"one click produced {model.Tabs.Count - tabsBefore.Count} tabs");
            Require(readOnlyTab.IsLibraryReadOnly && SamePath(readOnlyTab.SourcePath, fixture.ChartA),
                "the read-only tab changed identity or became editable");
            Require(!cloneTab.IsLibraryReadOnly, "the copy did not open as an editable tab");
            Require(!SamePath(cloneTab.SourcePath, fixture.ChartA), "the copy reused the read-only source path");
            Require(OsuFileSafetyPolicy.IsWithin(cloneTab.SourcePath, model.CreationRoot),
                $"the copy is outside the workspace creation root: {cloneTab.SourcePath}");
            Require(File.Exists(cloneTab.SourcePath), "the cloned chart file is missing");
            var copyFolder = Path.GetDirectoryName(cloneTab.SourcePath)!;
            Require(File.Exists(Path.Combine(copyFolder, "song.wav")), "the cloned mapset is missing its audio file");
            var cloneHash = Sha256(cloneTab.SourcePath);
            Require(cloneHash == hashA, "the cloned chart is not byte-identical to the source");
            Require(Sha256(fixture.ChartA) == hashA, "the read-only source file changed bytes");
            Require(File.Exists(fixture.ChartA), "the read-only source file disappeared");
            Require(model.Status.Contains("已复制到工作区", StringComparison.Ordinal) &&
                    model.Status.Contains(copyFolder, StringComparison.OrdinalIgnoreCase),
                "the status line does not report where the copy went: " + model.Status);
            var toast = Find<Control>(window, "Editor.Toast");
            Require(toast.IsVisible, "the copy toast is not visible");
            Require(Find<TextBlock>(window, "Editor.Toast.Text").Text?.Contains("已复制到工作区", StringComparison.Ordinal) == true,
                "the toast does not report the copy destination");
            Require(!edit.IsVisible, "top bar 编辑 is still visible while an editable tab is active");
            Capture(window, root, "readonly038-cloned-editable-tab");
            summary.Add($"single click: cloneMs={cloneMilliseconds} busyObserved={busyObserved} busyLabel='{busyLabel}' " +
                        $"copy={copyFolder} copySha={cloneHash} sourceUnchanged={Sha256(fixture.ChartA) == hashA} status='{model.Status}'");

            // ---------- a double click cannot publish two copies ----------
            model.Activate(readOnlyTabId);
            Pump(300);
            Require(edit.IsVisible && edit.IsEnabled, "top bar 编辑 did not come back after reactivating the read-only tab");
            var doubleStart = model.Tabs.Count;
            var point = CenterOf(window, edit);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            var busyAtSecondClick = !edit.IsEnabled;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Require(busyAtSecondClick, "the second click did not land while the clone was running; the busy window is too short to test");
            WaitUntil(() => model.Tabs.Count > doubleStart, 30000, "the double click produced no copy at all");
            Pump(1500);
            Require(model.Tabs.Count == doubleStart + 1,
                $"one double-click produced {model.Tabs.Count - doubleStart} copies");
            summary.Add($"double click: tabsBefore={doubleStart} tabsAfter={model.Tabs.Count} busyAtSecondClick={busyAtSecondClick}");

            // ---------- a failing clone keeps the source, the tab and an explicit error ----------
            Call(window, "ShowLibrary", false);
            Pump(350);
            SelectReadOnlyEntry(window, readOnlyList, fixture.ChartB);
            ClickPoint(window, Find<Button>(window, "Library.ViewReadOnly"));
            WaitUntil(() => model.ActiveTab is { IsLibraryReadOnly: true } second && SamePath(second.SourcePath, fixture.ChartB),
                30000, "the second read-only chart did not open");
            Pump(400);
            var hashB = Sha256(fixture.ChartB);
            var tabsBeforeFailure = model.Tabs.Count;
            string failureStatus;
            using (new FileStream(fixture.ChartB, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failButton = Find<Button>(window, TopEditId);
                Require(failButton.IsVisible && failButton.IsEnabled, "top bar 编辑 is missing for the second read-only chart");
                ClickPoint(window, failButton);
                WaitUntil(() => model.Tabs.Count == tabsBeforeFailure && failButton.IsEnabled &&
                                model.Status.Contains("克隆未完成，原文件保留", StringComparison.Ordinal),
                    30000, "a failing clone did not report an explicit error; status=" + model.Status);
                failureStatus = model.Status;
            }

            Pump(150);
            Require(model.Tabs.Count == tabsBeforeFailure, "a failing clone still opened an extra tab");
            Require(Sha256(fixture.ChartB) == hashB, "the read-only source changed bytes after a failed clone");
            Require(model.Tabs.Any(tab => SamePath(tab.SourcePath, fixture.ChartB) && tab.IsLibraryReadOnly),
                "the read-only tab was lost after a failed clone");
            Require(Find<Button>(window, TopEditId).IsEnabled, "top bar 编辑 stayed disabled after the failed clone");
            Require(Find<Button>(window, TopEditId).IsVisible, "top bar 编辑 disappeared after the failed clone");
            Capture(window, root, "readonly038-failed-clone");
            summary.Add($"failed clone: tabs={model.Tabs.Count} sourceUnchanged={Sha256(fixture.ChartB) == hashB} status='{failureStatus}'");

            File.WriteAllLines(Path.Combine(root, "readonly038-summary.txt"), summary);
            foreach (var line in summary) Console.WriteLine(line);
            Console.WriteLine($"PASS readonly-038 top-bar edit clones to workspace, source bytes unchanged, double click single copy, failure keeps source; screenshots={root}");
        }
        finally
        {
            window?.Close();
            Pump(300);
        }
    }

    // ----------------------------------------------------------------- fixtures

    private sealed record Fixture(string GameRoot, string ChartA, string ChartB);

    private static Fixture CreateFixture(string root)
    {
        var gameRoot = Path.Combine(root, "readonly038-osu");
        var songs = Path.Combine(gameRoot, "Songs");
        Directory.CreateDirectory(songs);
        File.WriteAllText(Path.Combine(gameRoot, "osu!.exe"), "synthetic fixture; never executed");
        var chartA = WriteMapset(Path.Combine(songs, "ReadOnly Mapset"), "ReadOnly Source A", 2);
        var chartB = WriteMapset(Path.Combine(songs, "ReadOnly Second"), "ReadOnly Source B", 1);
        return new Fixture(gameRoot, chartA, chartB);
    }

    private static string WriteMapset(string folder, string title, int extraMegabytes)
    {
        Directory.CreateDirectory(folder);
        var chart =
            "osu file format v14\n[General]\nAudioFilename: song.wav\nMode:3\n" +
            "[Metadata]\nTitle:" + title + "\nArtist:ReadOnly038\nCreator:Fixture\nVersion:7K\n" +
            "[Difficulty]\nCircleSize:7\n[TimingPoints]\n475,500,4,1,0,0,1,0\n" +
            "[HitObjects]\n36,192,1000,1,0,0:0:0:0:\n109,192,1500,128,0,2000:0:0:0:0:\n";
        var chartPath = Path.Combine(folder, "chart.osu");
        File.WriteAllText(chartPath, chart);
        Program.WriteSimulationWave(Path.Combine(folder, "song.wav"));
        // Real mapsets carry assets; a few MB keeps the clone's busy window wide
        // enough that the second click of a double click is deterministic.
        File.WriteAllBytes(Path.Combine(folder, "background.png"), new byte[extraMegabytes * 1024 * 1024]);
        return chartPath;
    }

    // ----------------------------------------------------------------- helpers

    private static void SelectReadOnlyEntry(Window window, ListBox list, string chartPath)
    {
        DesktopLibraryChartEntry? entry = null;
        WaitUntil(() =>
        {
            entry = ReadOnlyEntries(list).FirstOrDefault(candidate => SamePath(candidate.ChartPath, chartPath));
            return entry is not null;
        }, 20000, "the read-only library list never showed " + chartPath);
        list.SelectedItem = entry;
        Pump(120);
    }

    private static IEnumerable<DesktopLibraryChartEntry> ReadOnlyEntries(ListBox list) =>
        list.ItemsSource?.Cast<DesktopLibraryChartEntry>() ?? Enumerable.Empty<DesktopLibraryChartEntry>();

    private static T Find<T>(Control root, string id) where T : Control =>
        root.GetLogicalDescendants().OfType<T>()
            .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == id)
        ?? throw new InvalidOperationException("missing automation id " + id);

    private static IEnumerable<Button> AllButtons(Window window) => window.GetLogicalDescendants().OfType<Button>();

    private static string? LabelText(Button button) =>
        button.Content is StackPanel panel ? panel.Children.OfType<TextBlock>().LastOrDefault()?.Text : button.Content as string;

    private static bool SamePath(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Point CenterOf(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width * .5, control.Bounds.Height * .5), window);
        return point ?? throw new InvalidOperationException(
            "control is not attached to the window: " + AutomationProperties.GetAutomationId(control));
    }

    private static void ClickPoint(Window window, Control control)
    {
        var point = CenterOf(window, control);
        window.MouseDown(point, MouseButton.Left);
        Pump(12);
        window.MouseUp(point, MouseButton.Left);
        Pump(40);
    }

    private static void Capture(Window window, string root, string name)
    {
        using var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("headless frame capture is unavailable");
        frame.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void CaptureControl(Control control, string root, string name)
    {
        var width = Math.Max(1, (int)Math.Ceiling(control.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(control.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(control);
        bitmap.Save(Path.Combine(root, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().GetMethod(method, Hidden)?.Invoke(target, args);

    private static T Field<T>(object target, string field)
    {
        var info = target.GetType().GetField(field, Hidden)
            ?? throw new InvalidOperationException("missing private field " + field);
        return (T)info.GetValue(target)!;
    }

    private static T RequiredField<T>(object target, string field) where T : notnull =>
        Field<T?>(target, field) ?? throw new InvalidOperationException("private field " + field + " was not created");

    private static void Wait(Task task)
    {
        Program.PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs, string message)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException(message);
            Program.Pump(20);
        }
    }

    private static void Pump(int milliseconds) => Program.Pump(milliseconds);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

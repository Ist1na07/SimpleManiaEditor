using Avalonia;
using Avalonia.Media;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsTitleBarParitySpecs
{
    public static IReadOnlyList<WindowsTitleBarParitySpecResult> Run()
    {
        var results = new List<WindowsTitleBarParitySpecResult>();
        Check(results, "titlebar-wpf-chrome-metrics-are-exact", VerifyChromeMetrics);
        Check(results, "titlebar-caption-window-state-transitions-are-exact",
            VerifyCaptionWindowStateTransitions);
        Check(results, "titlebar-close-preflight-suspends-fullscreen-without-topmost",
            VerifyClosePreflightFullscreenSafety);
        Check(results, "titlebar-window-snap-placement-and-restore-are-exact",
            VerifyWindowSnapPlacementAndRestore);
        Check(results, "titlebar-borderless-resize-hit-zones-are-exact", VerifyResizeHitZones);
        Check(results, "titlebar-local-malody-tab-presentation-is-exact", VerifyLocalMalodyTab);
        Check(results, "titlebar-osu-live-and-retained-badges-are-exact", VerifyRoomTabBadges);
        Check(results, "titlebar-online-round-trip-latency-label-is-exact", VerifyOnlineLatency);
        return results;
    }

    private static string VerifyCaptionWindowStateTransitions()
    {
        Require(
            WindowsTitleBarParityPolicy.ResolveMaximizeToggle(
                global::Avalonia.Controls.WindowState.Normal) ==
            global::Avalonia.Controls.WindowState.Maximized,
            "Normal window did not maximize.");
        Require(
            WindowsTitleBarParityPolicy.ResolveMaximizeToggle(
                global::Avalonia.Controls.WindowState.Maximized) ==
            global::Avalonia.Controls.WindowState.Normal,
            "Maximized window did not restore.");
        Require(
            WindowsTitleBarParityPolicy.ResolveMaximizeToggle(
                global::Avalonia.Controls.WindowState.Minimized) ==
            global::Avalonia.Controls.WindowState.Maximized &&
            WindowsTitleBarParityPolicy.ResolveMaximizeToggle(
                global::Avalonia.Controls.WindowState.FullScreen) ==
            global::Avalonia.Controls.WindowState.Maximized,
            "Non-maximized WPF states did not resolve to Maximized.");
        return "Normal→Maximized; Maximized→Normal; other→Maximized";
    }

    private static string VerifyClosePreflightFullscreenSafety()
    {
        Require(
            !WindowsClosePreflightResourcePolicy.CanReleaseEditorVisuals(
                teardownConfirmed: false) &&
            WindowsClosePreflightResourcePolicy.CanReleaseEditorVisuals(
                teardownConfirmed: true),
            "Canceled close preflight was allowed to release live editor visuals.");
        Require(
            WindowsTitleBarParityPolicy.ShouldSuspendFullscreenForClosePreflight(
                global::Avalonia.Controls.WindowState.FullScreen),
            "A fullscreen owner was not suspended before showing close confirmation.");
        foreach (var state in new[]
                 {
                     global::Avalonia.Controls.WindowState.Normal,
                     global::Avalonia.Controls.WindowState.Maximized,
                     global::Avalonia.Controls.WindowState.Minimized
                 })
        {
            Require(
                !WindowsTitleBarParityPolicy.ShouldSuspendFullscreenForClosePreflight(state),
                $"Non-fullscreen state {state} was incorrectly suspended.");
            Require(
                WindowsTitleBarParityPolicy.ResolveFullscreenRestoreState(state) == state,
                $"Fullscreen restore changed valid prior state {state}.");
        }

        Require(
            WindowsTitleBarParityPolicy.ResolveFullscreenRestoreState(
                global::Avalonia.Controls.WindowState.FullScreen) ==
            global::Avalonia.Controls.WindowState.Normal,
            "Nested fullscreen restore did not fail closed to a normal window.");
        return "fullscreen owner exits before modal close dialog; cancel restores fullscreen and editor visuals";
    }

    private static string VerifyWindowSnapPlacementAndRestore()
    {
        Require(
            !WindowsWindowSnapPolicy.TryGetNativeWindowBounds(
                IntPtr.Zero,
                out _) &&
            !WindowsWindowSnapPolicy.TryApplyNativeWindowBounds(
                IntPtr.Zero,
                new PixelRect(0, 0, 1980, 1200)),
            "Native HWND bounds fallbacks did not fail closed for a null handle.");
        Require(
            !WindowsWindowSnapPolicy.ShouldApplyAfterMove(
                observedPressed: true,
                isPressed: true,
                TimeSpan.FromSeconds(1)) &&
            !WindowsWindowSnapPolicy.ShouldApplyAfterMove(
                observedPressed: false,
                isPressed: false,
                TimeSpan.FromMilliseconds(249)) &&
            WindowsWindowSnapPolicy.ShouldApplyAfterMove(
                observedPressed: true,
                isPressed: false,
                TimeSpan.Zero) &&
            WindowsWindowSnapPolicy.ShouldApplyAfterMove(
                observedPressed: false,
                isPressed: false,
                TimeSpan.FromMilliseconds(250)),
            "Move-release completion state did not wait for release or the 250 ms fallback.");
        var workingArea = new PixelRect(0, 0, 2560, 1528);
        Require(
            WindowsWindowSnapPolicy.ResolveTarget(
                new PixelPoint(1280, 0),
                workingArea) == WindowsWindowSnapTarget.Maximize,
            "Top-edge drag did not resolve to maximize.");
        Require(
            WindowsWindowSnapPolicy.ResolveTarget(
                new PixelPoint(0, 240),
                workingArea) == WindowsWindowSnapTarget.Left,
            "Left-edge drag did not resolve to left Snap.");
        Require(
            WindowsWindowSnapPolicy.ResolveTarget(
                new PixelPoint(2559, 240),
                workingArea) == WindowsWindowSnapTarget.Right,
            "Right-edge drag did not resolve to right Snap.");
        Require(
            WindowsWindowSnapPolicy.ResolveTarget(
                new PixelPoint(1280, 240),
                workingArea) is null,
            "Interior drag incorrectly resolved to a Snap target.");

        var left = WindowsWindowSnapPolicy.ResolvePlacement(
            WindowsWindowSnapTarget.Left,
            workingArea,
            scaling: 1.5,
            minimumWidthDip: 1080,
            minimumHeightDip: 680);
        var right = WindowsWindowSnapPolicy.ResolvePlacement(
            WindowsWindowSnapTarget.Right,
            workingArea,
            scaling: 1.5,
            minimumWidthDip: 1080,
            minimumHeightDip: 680);
        Require(
            left.Position == new PixelPoint(0, 0) &&
            left.PhysicalSize == new PixelSize(1620, 1528),
            "Left Snap did not respect the WPF 1080 DIP minimum at Windows 150%.");
        Require(
            right.Position == new PixelPoint(940, 0) &&
            right.PhysicalSize == new PixelSize(1620, 1528),
            "Right Snap did not respect the WPF 1080 DIP minimum at Windows 150%.");

        var restoredFromLeft = WindowsWindowSnapPolicy.ResolveRestoreUnderPointer(
            new PixelPoint(135, 29),
            new PixelRect(left.Position, left.PhysicalSize),
            new PixelSize(1980, 1200),
            workingArea,
            titleOffsetPixels: 29);
        var restoredFromRight = WindowsWindowSnapPolicy.ResolveRestoreUnderPointer(
            new PixelPoint(1075, 29),
            new PixelRect(right.Position, right.PhysicalSize),
            new PixelSize(1980, 1200),
            workingArea,
            titleOffsetPixels: 29);
        Require(
            restoredFromLeft == new PixelRect(-30, 0, 1980, 1200),
            "Left Snap drag-away did not preserve the pointer's title-bar ratio.");
        Require(
            restoredFromRight == new PixelRect(910, 0, 1980, 1200),
            "Right Snap drag-away did not preserve the pointer's title-bar ratio.");
        var secondaryWorkingArea = new PixelRect(-2560, 0, 2560, 1440);
        Require(
            WindowsWindowSnapPolicy.ResolveTarget(
                new PixelPoint(-2560, 240),
                secondaryWorkingArea) == WindowsWindowSnapTarget.Left &&
            WindowsWindowSnapPolicy.ResolveTarget(
                new PixelPoint(-1, 240),
                secondaryWorkingArea) == WindowsWindowSnapTarget.Right &&
            WindowsWindowSnapPolicy.ResolveTarget(
                new PixelPoint(-1280, 0),
                secondaryWorkingArea) == WindowsWindowSnapTarget.Maximize,
            "Negative-coordinate secondary-screen Snap targets were not resolved.");
        var secondaryRight = WindowsWindowSnapPolicy.ResolvePlacement(
            WindowsWindowSnapTarget.Right,
            secondaryWorkingArea,
            scaling: 1.25,
            minimumWidthDip: 1080,
            minimumHeightDip: 680);
        Require(
            secondaryRight.Position == new PixelPoint(-1350, 0) &&
            secondaryRight.PhysicalSize == new PixelSize(1350, 1440),
            "Secondary-screen placement did not use that monitor's scale and coordinates.");
        return "native HWND bounds=null-handle-fail-closed; " +
               "release=pressed/observed/250ms; targets=top/left/right/interior; " +
               "150%=1620x1528; restore=-30/910,0,1980x1200; " +
               "secondary=-2560..0@125%";
    }

    private static string VerifyResizeHitZones()
    {
        var size = new Size(1080, 680);
        Require(
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(0, 0), size) ==
            global::Avalonia.Controls.WindowEdge.NorthWest,
            "Top-left resize corner was not resolved.");
        Require(
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(1080, 0), size) ==
            global::Avalonia.Controls.WindowEdge.NorthEast,
            "Top-right resize corner was not resolved.");
        Require(
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(0, 680), size) ==
            global::Avalonia.Controls.WindowEdge.SouthWest,
            "Bottom-left resize corner was not resolved.");
        Require(
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(1080, 680), size) ==
            global::Avalonia.Controls.WindowEdge.SouthEast,
            "Bottom-right resize corner was not resolved.");
        Require(
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(3, 340), size) ==
            global::Avalonia.Controls.WindowEdge.West &&
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(1077, 340), size) ==
            global::Avalonia.Controls.WindowEdge.East &&
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(540, 3), size) ==
            global::Avalonia.Controls.WindowEdge.North &&
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(540, 677), size) ==
            global::Avalonia.Controls.WindowEdge.South,
            "One or more resize edges were not resolved.");
        Require(
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(7, 7), size) is null &&
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(540, 340), size) is null &&
            WindowsTitleBarParityPolicy.ResolveResizeEdge(new Point(-1, 100), size) is null,
            "Resize hit zone escaped the WPF 6 DIP border.");
        return "6 DIP; N/NE/E/SE/S/SW/W/NW; interior and outside rejected";
    }

    private static string VerifyChromeMetrics()
    {
        Require(WindowsTitleBarParityPolicy.TitleBarHeight == 38,
            "Title bar height differs from WPF 38 DIP.");
        Require(WindowsTitleBarParityPolicy.CaptionButtonWidth == 46,
            "Caption button width differs from WPF 46 DIP.");
        Require(!WindowsTitleBarParityPolicy.CaptionButtonsFocusable,
            "Caption buttons must not steal timeline keyboard focus.");
        Require(WindowsTitleBarParityPolicy.ResizeBorderThickness == 6,
            "Resize border differs from WPF WindowChrome 6 DIP.");
        Require(
            WindowsTitleBarParityPolicy.MinimumWindowWidth == 1080 &&
            WindowsTitleBarParityPolicy.MinimumWindowHeight == 680,
            "Minimum window size differs from WPF 1080x680 DIP.");
        Require(WindowsTitleBarParityPolicy.FullscreenRevealHotZoneHeight == 4,
            "Fullscreen reveal hot zone differs from WPF 4 DIP.");
        Require(WindowsTitleBarParityPolicy.FullscreenHideDelayMilliseconds == 480,
            "Fullscreen title hide delay differs from WPF 480 ms.");
        Require(WindowsTitleBarParityPolicy.FullscreenRevealDurationMilliseconds == 145,
            "Fullscreen title reveal animation differs from WPF 145 ms.");
        Require(WindowsTitleBarParityPolicy.FullscreenHideDurationMilliseconds == 125,
            "Fullscreen title hide animation differs from WPF 125 ms.");
        Require(Math.Abs(
                WindowsTitleBarParityPolicy.EvaluateFullscreenTitleBarProgress(
                    revealed: true,
                    .5) - .875) < 1e-12,
            "Fullscreen reveal does not use WPF CubicEase EaseOut.");
        Require(Math.Abs(
                WindowsTitleBarParityPolicy.EvaluateFullscreenTitleBarProgress(
                    revealed: false,
                    .5) - .125) < 1e-12,
            "Fullscreen hide does not use WPF CubicEase EaseIn.");
        return "height=38; caption=46; resize=6; min=1080x680; hot-zone=4; delay=480ms; " +
               "reveal=145ms cubic-out; hide=125ms cubic-in";
    }

    private static string VerifyLocalMalodyTab()
    {
        var tab = WindowsTitleBarParityPolicy.ResolveDocumentTab(
            DesktopChartFormat.MalodyMc,
            "Resound",
            isDirty: true,
            isActive: true,
            isLiveRoom: false,
            isRetainedRoom: false,
            isObserver: false,
            roomNumber: null);
        Require(tab.FormatBadge == "MALODY", "Malody badge text differs.");
        Require(tab.DisplayTitle == "Resound  *", "WPF dirty marker differs.");
        Require(tab.TabBackground == Color.Parse("#2B3437") &&
                tab.TabBorder == Color.Parse("#6F979A"),
            "Active document-tab surface differs from WPF.");
        Require(tab.FormatBackground == Color.Parse("#263538") &&
                tab.FormatBorder == Color.Parse("#58777B") &&
                tab.FormatForeground == Color.Parse("#C8DFE0"),
            "Malody format-badge colors differ from WPF.");
        Require(WindowsTitleBarParityPolicy.DocumentTabHeight == 28 &&
                WindowsTitleBarParityPolicy.DocumentTabMargin ==
                new Thickness(0, 0, 5, 0) &&
                WindowsTitleBarParityPolicy.DocumentTabOpenMinWidth == 88 &&
                WindowsTitleBarParityPolicy.DocumentTabOpenMaxWidth == 190 &&
                WindowsTitleBarParityPolicy.DocumentTabCloseWidth == 25,
            "Document-tab density differs from WPF.");
        return "MALODY; active=#2B3437/#6F979A; height=28; open=88..190; close=25";
    }

    private static string VerifyRoomTabBadges()
    {
        var live = WindowsTitleBarParityPolicy.ResolveDocumentTab(
            DesktopChartFormat.OsuMania,
            "Michishirube",
            isDirty: false,
            isActive: false,
            isLiveRoom: true,
            isRetainedRoom: false,
            isObserver: true,
            roomNumber: 100086);
        Require(live.FormatBadge == "OSU → MC", "osu format badge differs.");
        Require(live.FormatBackground == Color.Parse("#3D3021") &&
                live.FormatBorder == Color.Parse("#926C3B") &&
                live.FormatForeground == Color.Parse("#EECC97"),
            "osu format-badge colors differ from WPF.");
        Require(live.LiveBadgeText == "联机" &&
                live.LiveRoomNumberText == " #100086" &&
                live.RoomStateDescription == "房间观察者，只读",
            "Live observer badge or state differs.");

        var retained = WindowsTitleBarParityPolicy.ResolveDocumentTab(
            DesktopChartFormat.MalodyMc,
            "Room Copy",
            isDirty: true,
            isActive: true,
            isLiveRoom: false,
            isRetainedRoom: true,
            isObserver: false,
            roomNumber: null);
        Require(retained.RetainedBadgeText == "房间留存" &&
                retained.RoomStateDescription == "房间留存，本地可编辑",
            "Retained-room badge or state differs.");

        var invalidRejected = false;
        try
        {
            _ = WindowsTitleBarParityPolicy.ResolveDocumentTab(
                DesktopChartFormat.MalodyMc,
                "Invalid",
                false,
                false,
                isLiveRoom: true,
                isRetainedRoom: true,
                false,
                null);
        }
        catch (ArgumentException)
        {
            invalidRejected = true;
        }
        Require(invalidRejected, "A tab was allowed to be live and retained simultaneously.");
        return "OSU → MC; live #100086/viewer; retained badge; invalid dual-state rejected";
    }

    private static string VerifyOnlineLatency()
    {
        var pending = WindowsTitleBarParityPolicy.ResolveOnlineLatency(null);
        var fast = WindowsTitleBarParityPolicy.ResolveOnlineLatency(80);
        var moderate = WindowsTitleBarParityPolicy.ResolveOnlineLatency(180);
        var slow = WindowsTitleBarParityPolicy.ResolveOnlineLatency(181);
        var clamped = WindowsTitleBarParityPolicy.ResolveOnlineLatency(120_000);
        Require(
            pending.Text == "-- ms" &&
            pending.Foreground == Color.Parse("#809497"),
            "Pending latency label differs from WPF.");
        Require(
            fast.Text == "80 ms" && fast.Foreground == Color.Parse("#89C9AD"),
            "Fast latency label differs from WPF.");
        Require(
            moderate.Text == "180 ms" &&
            moderate.Foreground == Color.Parse("#DAB770"),
            "Moderate latency label differs from WPF.");
        Require(
            slow.Text == "181 ms" && slow.Foreground == Color.Parse("#DC8474"),
            "Slow latency label differs from WPF.");
        Require(clamped.Text == "99999 ms", "Latency clamp differs from WPF.");
        Require(
            pending.ToolTip == "与联机服务器最近一次请求的往返延迟",
            "Latency tooltip differs from WPF.");
        var negativeRejected = false;
        try
        {
            _ = WindowsTitleBarParityPolicy.ResolveOnlineLatency(-1);
        }
        catch (ArgumentOutOfRangeException)
        {
            negativeRejected = true;
        }
        Require(negativeRejected, "Negative latency was accepted.");
        return "--/80/180/181/99999 ms; gray/green/amber/red; exact tooltip";
    }

    private static void Check(
        ICollection<WindowsTitleBarParitySpecResult> results,
        string name,
        Func<string> action)
    {
        try
        {
            results.Add(new WindowsTitleBarParitySpecResult(name, true, action()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsTitleBarParitySpecResult(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsTitleBarParitySpecResult(
    string Name,
    bool Passed,
    string Detail);

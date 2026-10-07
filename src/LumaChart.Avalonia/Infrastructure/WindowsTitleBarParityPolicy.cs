using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LumaChart.Application.Workspaces;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Windows/WPF 1.1.0 title-bar and document-tab metrics. Keeping the visual
/// contract outside MainWindow makes the Windows shell independently testable
/// without moving shared chart or online logic into Avalonia code-behind.
/// </summary>
public static class WindowsTitleBarParityPolicy
{
    public const string ContractVersion = "wpf-1.1.0-titlebar-r1";
    public const double TitleBarHeight = 38;
    public const double CaptionButtonWidth = 46;
    public const bool CaptionButtonsFocusable = false;
    public const double ResizeBorderThickness = 6;
    public const double MinimumWindowWidth = 1080;
    public const double MinimumWindowHeight = 680;
    public const double FullscreenRevealHotZoneHeight = 4;
    public const int FullscreenHideDelayMilliseconds = 480;
    public const int FullscreenRevealDurationMilliseconds = 145;
    public const int FullscreenHideDurationMilliseconds = 125;
    public const double DocumentTabHeight = 28;
    public const double DocumentTabRightMargin = 5;
    public const double DocumentTabOpenMinWidth = 88;
    public const double DocumentTabOpenMaxWidth = 190;
    public const double DocumentTabCloseWidth = 25;

    public static WindowsDocumentTabPresentation ResolveDocumentTab(
        DesktopChartFormat format,
        string displayTitle,
        bool isDirty,
        bool isActive,
        bool isLiveRoom,
        bool isRetainedRoom,
        bool isObserver,
        long? roomNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayTitle);
        if (isLiveRoom && isRetainedRoom)
            throw new ArgumentException("A document tab cannot be live and retained simultaneously.");

        var osu = format == DesktopChartFormat.OsuMania;
        return new WindowsDocumentTabPresentation(
            FormatBadge: osu ? "OSU → MC" : "MALODY",
            DisplayTitle: displayTitle + (isDirty ? "  *" : string.Empty),
            TabBackground: Parse(isActive ? "#2B3437" : "#1D2124"),
            TabBorder: Parse(isActive ? "#6F979A" : "#333A3E"),
            TitleForeground: Parse(isActive ? "#E8EDEE" : "#ACB5B8"),
            FormatBackground: Parse(osu ? "#3D3021" : "#263538"),
            FormatBorder: Parse(osu ? "#926C3B" : "#58777B"),
            FormatForeground: Parse(osu ? "#EECC97" : "#C8DFE0"),
            LiveBadgeText: isLiveRoom ? "联机" : null,
            LiveRoomNumberText: isLiveRoom && roomNumber is > 0
                ? $" #{roomNumber.Value}"
                : string.Empty,
            LiveDescription: isLiveRoom
                ? isObserver ? "房间观察者，只读" : "房间同步中"
                : null,
            RetainedBadgeText: isRetainedRoom ? "房间留存" : null,
            RoomStateDescription: isLiveRoom
                ? isObserver ? "房间观察者，只读" : "房间同步中"
                : isRetainedRoom
                    ? "房间留存，本地可编辑"
                    : "本地谱面");
    }

    public static Thickness DocumentTabMargin =>
        new(0, 0, DocumentTabRightMargin, 0);

    public static Thickness DocumentTabPadding => new(1);

    public static Thickness FormatBadgeMargin => new(7, 4, 1, 4);

    public static Thickness FormatBadgePadding => new(6, 1);

    public static Thickness LiveBadgeMargin => new(4, 4, 0, 4);

    public static Thickness LiveBadgePadding => new(6, 1);

    public static Thickness OpenButtonPadding => new(10, 0, 8, 1);

    public static WindowsOnlineLatencyPresentation ResolveOnlineLatency(
        int? roundTripMilliseconds)
    {
        if (roundTripMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(roundTripMilliseconds));
        var latency = roundTripMilliseconds is int measured
            ? Math.Min(measured, 99_999)
            : (int?)null;
        return new WindowsOnlineLatencyPresentation(
            latency is int value ? $"{value} ms" : "-- ms",
            Parse(latency switch
            {
                <= 80 => "#89C9AD",
                <= 180 => "#DAB770",
                int => "#DC8474",
                _ => "#809497"
            }),
            "与联机服务器最近一次请求的往返延迟");
    }

    public static double EvaluateFullscreenTitleBarProgress(
        bool revealed,
        double progress)
    {
        var normalized = Math.Clamp(progress, 0, 1);
        return revealed
            ? 1 - Math.Pow(1 - normalized, 3)
            : Math.Pow(normalized, 3);
    }

    public static WindowState ResolveMaximizeToggle(WindowState currentState) =>
        currentState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    public static WindowState ResolveFullscreenRestoreState(
        WindowState stateBeforeFullscreen) =>
        stateBeforeFullscreen == WindowState.FullScreen
            ? WindowState.Normal
            : stateBeforeFullscreen;

    public static bool ShouldSuspendFullscreenForClosePreflight(
        WindowState currentState) =>
        currentState == WindowState.FullScreen;

    public static WindowEdge? ResolveResizeEdge(Point point, Size windowSize)
    {
        if (!double.IsFinite(point.X) ||
            !double.IsFinite(point.Y) ||
            !double.IsFinite(windowSize.Width) ||
            !double.IsFinite(windowSize.Height) ||
            windowSize.Width <= 0 ||
            windowSize.Height <= 0 ||
            point.X < 0 ||
            point.Y < 0 ||
            point.X > windowSize.Width ||
            point.Y > windowSize.Height)
        {
            return null;
        }

        var left = point.X <= ResizeBorderThickness;
        var right = point.X >= windowSize.Width - ResizeBorderThickness;
        var top = point.Y <= ResizeBorderThickness;
        var bottom = point.Y >= windowSize.Height - ResizeBorderThickness;
        if (top && left) return WindowEdge.NorthWest;
        if (top && right) return WindowEdge.NorthEast;
        if (bottom && left) return WindowEdge.SouthWest;
        if (bottom && right) return WindowEdge.SouthEast;
        if (left) return WindowEdge.West;
        if (right) return WindowEdge.East;
        if (top) return WindowEdge.North;
        if (bottom) return WindowEdge.South;
        return null;
    }

    private static Color Parse(string value) => Color.Parse(value);
}

public sealed record WindowsDocumentTabPresentation(
    string FormatBadge,
    string DisplayTitle,
    Color TabBackground,
    Color TabBorder,
    Color TitleForeground,
    Color FormatBackground,
    Color FormatBorder,
    Color FormatForeground,
    string? LiveBadgeText,
    string LiveRoomNumberText,
    string? LiveDescription,
    string? RetainedBadgeText,
    string RoomStateDescription);

public sealed record WindowsOnlineLatencyPresentation(
    string Text,
    Color Foreground,
    string ToolTip);

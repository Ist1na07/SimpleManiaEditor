namespace LumaChart.Avalonia.Editing;

public sealed record WindowsDifficultyCardLayout(
    bool Visible,
    bool Collapsed,
    double Width,
    double X,
    double Y,
    double AvailableWidth);

/// <summary>Pure WPF 1.1.0 geometry and stale-result guards for Rework UI.</summary>
public static class WindowsDifficultyUiPolicy
{
    public const double DefaultCardWidth = 220;
    public const double ExpandedCardHeight = 310;
    public const double CollapsedCardHeight = 42;
    public const double CardLeft = 11;
    public const double TrackGap = 10;
    public const double EdgeGap = 4;

    public static bool IsAnalysisCurrent(
        Guid targetTabId,
        long targetChangeSerial,
        Guid? activeTabId,
        long activeChangeSerial) =>
        activeTabId == targetTabId && activeChangeSerial == targetChangeSerial;

    public static WindowsDifficultyCardLayout ResolveCardLayout(
        bool hasFourKeyChart,
        double hostWidth,
        double hostHeight,
        double trackLeft,
        double? leftDockedPanelLeft,
        bool requestedCollapsed,
        double requestedX,
        double requestedY,
        double actualCardHeight)
    {
        if (!hasFourKeyChart ||
            !double.IsFinite(hostWidth) || hostWidth <= 0 ||
            !double.IsFinite(hostHeight) || hostHeight <= 0 ||
            !double.IsFinite(trackLeft) || trackLeft <= 0)
            return new WindowsDifficultyCardLayout(
                false,
                requestedCollapsed,
                DefaultCardWidth,
                0,
                0,
                0);

        _ = actualCardHeight;
        var rightEdge = trackLeft - TrackGap;
        if (leftDockedPanelLeft is { } panelLeft &&
            double.IsFinite(panelLeft) && panelLeft >= 0 && panelLeft < trackLeft)
            rightEdge = Math.Min(rightEdge, panelLeft - TrackGap);
        var availableWidth = rightEdge - CardLeft;
        var collapsed = requestedCollapsed;
        const double width = DefaultCardWidth;
        var height = ResolveCardHeight(collapsed);
        var minimumX = EdgeGap - CardLeft;
        var maximumX = Math.Max(minimumX, hostWidth - width - CardLeft - EdgeGap);
        const double cardTop = 14;
        var minimumY = EdgeGap - cardTop;
        var maximumY = Math.Max(minimumY, hostHeight - height - cardTop - EdgeGap);
        var x = Math.Clamp(FiniteOr(requestedX, 0), minimumX, maximumX);
        var y = Math.Clamp(FiniteOr(requestedY, 0), minimumY, maximumY);
        return new WindowsDifficultyCardLayout(
            true,
            collapsed,
            width,
            x,
            y,
            availableWidth);
    }

    public static double ResolveCardHeight(bool collapsed) =>
        collapsed ? CollapsedCardHeight : ExpandedCardHeight;

    public static (double X, double Y) ApplyDrag(
        double requestedX,
        double requestedY,
        double hostWidth,
        double hostHeight,
        double cardWidth,
        double cardHeight,
        double baseLeft = CardLeft,
        double baseTop = 14)
    {
        var minimumX = EdgeGap - baseLeft;
        var maximumX = Math.Max(
            minimumX,
            FiniteOr(hostWidth, 0) - Math.Max(0, FiniteOr(cardWidth, 0)) - baseLeft - EdgeGap);
        var minimumY = EdgeGap - baseTop;
        var maximumY = Math.Max(
            minimumY,
            FiniteOr(hostHeight, 0) - Math.Max(0, FiniteOr(cardHeight, 0)) - baseTop - EdgeGap);
        return (
            Math.Clamp(FiniteOr(requestedX, 0), minimumX, maximumX),
            Math.Clamp(FiniteOr(requestedY, 0), minimumY, maximumY));
    }

    private static double FiniteOr(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}

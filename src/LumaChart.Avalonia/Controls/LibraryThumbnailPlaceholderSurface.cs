using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;

namespace LumaChart.Avalonia.Controls;

/// <summary>
/// WPF 1.1.0 library placeholder reproduced as one lightweight drawing
/// surface. The original 320x180 drawing is expressed in its displayed
/// 160x90 DIP coordinate space.
/// </summary>
public sealed class LibraryThumbnailPlaceholderSurface : Control
{
    public const double LogicalWidth = 160;
    public const double LogicalHeight = 90;
    public const double FrameLeft = 56;
    public const double FrameTop = 24;
    public const double FrameWidth = 48;
    public const double FrameHeight = 35;
    public const double StrokeThickness = 2;
    public const double SunCenterX = 91.5;
    public const double SunCenterY = 32.5;
    public const double SunRadius = 3.5;

    public const string BackgroundColor = "#191D20";
    public const string DrawingColor = "#4D5B60";

    private static readonly IBrush BackgroundBrush =
        new SolidColorBrush(Color.Parse(BackgroundColor));
    private static readonly IBrush DrawingBrush =
        new SolidColorBrush(Color.Parse(DrawingColor));
    private static readonly IPen DrawingPen =
        new Pen(DrawingBrush, StrokeThickness);

    public LibraryThumbnailPlaceholderSurface()
    {
        IsHitTestVisible = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ControlAutomationPeer(this);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var offsetX = (Bounds.Width - LogicalWidth) / 2;
        var offsetY = (Bounds.Height - LogicalHeight) / 2;
        context.DrawRectangle(BackgroundBrush, null, Bounds);
        context.DrawRectangle(
            null,
            DrawingPen,
            new Rect(
                offsetX + FrameLeft,
                offsetY + FrameTop,
                FrameWidth,
                FrameHeight));
        context.DrawLine(
            DrawingPen,
            new Point(offsetX + 63, offsetY + 50.5),
            new Point(offsetX + 75.5, offsetY + 38.5));
        context.DrawLine(
            DrawingPen,
            new Point(offsetX + 75.5, offsetY + 38.5),
            new Point(offsetX + 88, offsetY + 51));
        context.DrawLine(
            DrawingPen,
            new Point(offsetX + 88, offsetY + 51),
            new Point(offsetX + 96.5, offsetY + 43.5));
        context.DrawEllipse(
            DrawingBrush,
            null,
            new Point(offsetX + SunCenterX, offsetY + SunCenterY),
            SunRadius,
            SunRadius);
    }
}

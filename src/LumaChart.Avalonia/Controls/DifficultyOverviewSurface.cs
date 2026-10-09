using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Analysis;

namespace LumaChart.Avalonia.Controls;

public sealed record DifficultyOverviewDiagnostics(
    double EndBeat,
    double CurrentBeat,
    int PeakCount,
    int VisualChildCount,
    IReadOnlyList<double> PeakBeats);

/// <summary>
/// WPF-compatible, single-control Rework overview. Peak markers are painted in
/// one clipped draw pass; no marker is represented by an Avalonia Control.
/// </summary>
public sealed class DifficultyOverviewSurface : Control
{
    private static readonly IBrush BackgroundBrush =
        new SolidColorBrush(Color.Parse("#141618"));
    private static readonly IBrush RailBrush =
        new SolidColorBrush(Color.Parse("#2B3134"));
    private static readonly IPen BorderPen =
        new Pen(new SolidColorBrush(Color.Parse("#34393D")), 1);
    private static readonly IPen PlayheadPen =
        new Pen(new SolidColorBrush(Color.Parse("#D36974")), 1.25);
    private static readonly IPen MarkerBorderPen =
        new Pen(new SolidColorBrush(Color.FromArgb(185, 235, 231, 218)), .65);
    private static readonly IPen DashPen =
        new Pen(new SolidColorBrush(Color.FromArgb(90, 101, 116, 120)), 1);
    private static readonly IBrush[] MarkerBrushes =
    [
        new SolidColorBrush(Color.FromArgb(225, 181, 157, 86)),
        new SolidColorBrush(Color.FromArgb(225, 197, 138, 78)),
        new SolidColorBrush(Color.FromArgb(225, 211, 119, 71)),
        new SolidColorBrush(Color.FromArgb(225, 214, 94, 77)),
        new SolidColorBrush(Color.FromArgb(225, 216, 70, 83))
    ];

    private ReworkDifficultyResult? result;
    private IReadOnlyList<ReworkDifficultyPeak> visiblePeaks = [];
    private double endBeat = 1;
    private double currentBeat;
    private bool seeking;
    private IReadOnlyList<EditorMetricSample> fallback = [];
    private double fallbackMaximum = 1;
    public void SetFallback(IReadOnlyList<EditorMetricSample> samples)
    {
        fallback = samples;
        fallbackMaximum = Math.Max(1, samples.Count == 0 ? 1 : samples.Max(s => s.Value));
        InvalidateVisual();
    }

    public DifficultyOverviewSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        try
        {
            Cursor = new Cursor(StandardCursorType.Hand);
        }
        catch (InvalidOperationException)
        {
            // Pure geometry Specs intentionally run without a platform cursor
            // factory. The real Windows application always supplies one.
        }
        ToolTip.SetTip(this, "运行难度分析后，这里会按颜色标出高难区段");
    }

    public event Action<double>? SeekRequested;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ControlAutomationPeer(this);

    public void SetResult(ReworkDifficultyResult? value)
    {
        result = value;
        visiblePeaks = SelectVisiblePeaks(value?.Peaks);
        UpdateToolTip(null);
        InvalidateVisual();
    }

    public void SetEndBeat(double value)
    {
        var safe = double.IsFinite(value) ? Math.Max(1, value) : 1;
        if (Math.Abs(endBeat - safe) < 1e-9) return;
        endBeat = safe;
        InvalidateVisual();
    }

    public void SetCurrentBeat(double value)
    {
        var safe = double.IsFinite(value) ? Math.Max(0, value) : 0;
        if (Math.Abs(currentBeat - safe) < 1e-9) return;
        currentBeat = safe;
        InvalidateVisual();
    }

    public DifficultyOverviewDiagnostics GetDiagnostics() => new(
        endBeat,
        currentBeat,
        visiblePeaks.Count,
        VisualChildren.Count,
        visiblePeaks.Select(peak => peak.Beat.Value).ToArray());

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(BackgroundBrush, BorderPen, bounds, 2, 2);
        if (Bounds.Width <= 1 || Bounds.Height <= 1) return;

        if (result == null)
            foreach (var sample in fallback)
            {
                var y = BeatToY(sample.Beat, endBeat, Bounds.Height);
                context.DrawLine(DashPen, new Point(2, y), new Point(2 + sample.Value / fallbackMaximum * Math.Max(1, Bounds.Width - 4), y));
            }

        using (context.PushClip(bounds))
        {
            var rail = new Rect(
                Math.Max(2, Bounds.Width * .39),
                5,
                Math.Max(3, Bounds.Width * .22),
                Math.Max(1, Bounds.Height - 10));
            context.DrawRectangle(RailBrush, null, rail, 1.5, 1.5);

            if (visiblePeaks.Count > 0)
            {
                var maximum = visiblePeaks.Max(peak => peak.Intensity);
                var minimum = visiblePeaks.Min(peak => peak.Intensity);
                var markerHeight = Math.Clamp(Bounds.Height / endBeat * 1.2, 5, 13);
                foreach (var peak in visiblePeaks.Reverse())
                {
                    var strength = maximum <= minimum
                        ? 1
                        : (peak.Intensity - minimum) / (maximum - minimum);
                    var y = BeatToY(peak.Beat.Value, endBeat, Bounds.Height);
                    var marker = new Rect(
                        3,
                        y - markerHeight / 2,
                        Math.Max(1, Bounds.Width - 6),
                        markerHeight);
                    var brushIndex = Math.Clamp(
                        (int)Math.Round(strength * (MarkerBrushes.Length - 1)),
                        0,
                        MarkerBrushes.Length - 1);
                    context.DrawRectangle(
                        MarkerBrushes[brushIndex],
                        MarkerBorderPen,
                        marker,
                        2,
                        2);
                }
            }
            else
            {
                for (var y = 12d; y < Bounds.Height - 8; y += 16)
                    context.DrawLine(
                        DashPen,
                        new Point(Bounds.Width * .35, y),
                        new Point(Bounds.Width * .65, y));
            }

            var playY = BeatToY(currentBeat, endBeat, Bounds.Height);
            context.DrawLine(
                PlayheadPen,
                new Point(2, playY),
                new Point(Math.Max(2, Bounds.Width - 2), playY));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            Bounds.Height <= 1) return;
        Focus();
        seeking = true;
        eventArgs.Pointer.Capture(this);
        SeekAt(eventArgs.GetPosition(this).Y);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        var point = eventArgs.GetPosition(this);
        UpdateToolTip(NearestPeak(point.Y));
        if (!seeking ||
            !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        SeekAt(point.Y);
        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        if (!seeking) return;
        SeekAt(eventArgs.GetPosition(this).Y);
        seeking = false;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        seeking = false;
        base.OnPointerCaptureLost(eventArgs);
    }

    protected override void OnPointerExited(PointerEventArgs eventArgs)
    {
        UpdateToolTip(null);
        base.OnPointerExited(eventArgs);
    }

    private void SeekAt(double y) => SeekRequested?.Invoke(
        YToBeat(y, endBeat, Bounds.Height));

    private ReworkDifficultyPeak? NearestPeak(double y)
    {
        if (visiblePeaks.Count == 0) return null;
        var nearest = visiblePeaks.MinBy(peak => Math.Abs(
            BeatToY(peak.Beat.Value, endBeat, Bounds.Height) - y));
        return nearest != null && Math.Abs(
            BeatToY(nearest.Beat.Value, endBeat, Bounds.Height) - y) <= 11
            ? nearest
            : null;
    }

    private void UpdateToolTip(ReworkDifficultyPeak? peak)
    {
        ToolTip.SetTip(this, peak == null
            ? result == null
                ? "全局实验负荷（非官方星级 / Sunny 7K）；4K 可运行 Rework 分析"
                : AvaloniaLocalizationCatalog.Format("Rework {0:0.00}★ · 点击或拖动定位", result.StarRating)
            : AvaloniaLocalizationCatalog.Format("{0} · 局部强度 {1:0.00}", peak.Beat, peak.Intensity));
    }

    public static IReadOnlyList<ReworkDifficultyPeak> SelectVisiblePeaks(
        IEnumerable<ReworkDifficultyPeak>? peaks) => peaks == null
        ? []
        : peaks.OrderByDescending(peak => peak.Intensity)
            .ThenBy(peak => peak.Beat)
            .Take(8)
            .ToArray();

    public static double BeatToY(double beat, double maximumBeat, double height)
    {
        var safeHeight = double.IsFinite(height) ? Math.Max(0, height) : 0;
        var safeMaximum = double.IsFinite(maximumBeat) ? Math.Max(1, maximumBeat) : 1;
        var safeBeat = double.IsFinite(beat) ? beat : 0;
        return Math.Clamp(safeHeight - safeBeat / safeMaximum * safeHeight, 0, safeHeight);
    }

    public static double YToBeat(double y, double maximumBeat, double height)
    {
        var safeHeight = double.IsFinite(height) ? Math.Max(1, height) : 1;
        var safeMaximum = double.IsFinite(maximumBeat) ? Math.Max(1, maximumBeat) : 1;
        var safeY = double.IsFinite(y) ? y : safeHeight;
        return (1 - Math.Clamp(safeY, 0, safeHeight) / safeHeight) * safeMaximum;
    }
}

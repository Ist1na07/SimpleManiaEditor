using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;
using LumaChart.Core.Validation;

namespace LumaChart.Avalonia.Controls;

/// <summary>
/// WPF-compatible single-control density overview. The histogram is cached by
/// document serial and viewport height so pointer movement and timeline frames
/// never enumerate the full chart.
/// </summary>
public sealed class EditorOverviewSurface : Control
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ControlAutomationPeer(this);

    private static readonly IBrush BackgroundBrush =
        new SolidColorBrush(Color.Parse("#15181A"));
    private static readonly IPen DensityPen =
        new Pen(new SolidColorBrush(Color.Parse("#688D91")), 1.1);
    private static readonly IPen CurrentBeatPen =
        new Pen(new SolidColorBrush(Color.Parse("#E05D69")), 1.5);
    private static readonly IBrush NavigationMarkerBrush =
        new SolidColorBrush(Color.Parse("#F0C56B"));
    private static readonly IPen NavigationMarkerPen =
        new Pen(new SolidColorBrush(Color.Parse("#FFF2DEAA")), 1);
    private static readonly IBrush OverlapIssueBrush =
        new SolidColorBrush(Color.Parse("#FF4E5B"));
    private static readonly IBrush CrossingIssueBrush =
        new SolidColorBrush(Color.Parse("#F49749"));
    private static readonly IPen IssueBorderPen =
        new Pen(new SolidColorBrush(Color.FromArgb(225, 255, 226, 220)), .8);
    private const string DefaultToolTip =
        "密度峰值；红点为重叠键，橙点为 LN 交叉，金色标记为跳转书签，点击可定位";
    private EditorSession? session;
    private double currentBeat;
    private double externalEndBeat;
    private bool seeking;
    private long cachedChangeSerial = long.MinValue;
    private int cachedHeight;
    private double cachedMaximumBeat = 4;
    private double[] cachedDensity = [];
    private IReadOnlyList<ChartIssue> issues = [];
    private IReadOnlyList<DesktopNavigationMarker> navigationMarkers = [];

    public EditorOverviewSurface()
    {
        ClipToBounds = true;
        Focusable = true;
        ToolTip.SetTip(this, DefaultToolTip);
    }

    public EditorSession? Session
    {
        get => session;
        set
        {
            if (ReferenceEquals(session, value))
            {
                InvalidateVisual();
                return;
            }
            if (session != null) session.Changed -= Session_Changed;
            session = value;
            if (session != null) session.Changed += Session_Changed;
            cachedChangeSerial = long.MinValue;
            cachedDensity = [];
            InvalidateVisual();
        }
    }

    public event Action<BeatPosition>? SeekRequested;
    public event Action<BeatPosition>? NavigationMarkerSeekRequested;
    public event Action<ChartIssue>? IssueSeekRequested;

    public void SetIssues(IReadOnlyList<ChartIssue>? value)
    {
        issues = value ?? [];
        UpdateToolTip(null);
        InvalidateVisual();
    }

    public void SetNavigationMarkers(
        IReadOnlyList<DesktopNavigationMarker>? value)
    {
        var normalized = WindowsNavigationMarkerPolicy.Normalize(value);
        if (navigationMarkers.SequenceEqual(normalized)) return;
        navigationMarkers = normalized;
        cachedChangeSerial = long.MinValue;
        UpdateToolTip(null);
        InvalidateVisual();
    }

    public void SetExternalEndBeat(double beat)
    {
        var safe = double.IsFinite(beat) ? Math.Max(0, beat) : 0;
        if (Math.Abs(externalEndBeat - safe) < 1e-9) return;
        externalEndBeat = safe;
        cachedChangeSerial = long.MinValue;
        InvalidateVisual();
    }

    public void SetCurrentBeat(double beat)
    {
        var safe = double.IsFinite(beat) ? Math.Max(0, beat) : 0;
        if (Math.Abs(currentBeat - safe) < 1e-9) return;
        currentBeat = safe;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));
        if (Bounds.Width <= 2 || Bounds.Height <= 2) return;
        EnsureDensityCache();
        if (cachedDensity.Length > 0)
        {
            var maximum = Math.Max(1, cachedDensity.Max());
            for (var index = 0; index < cachedDensity.Length; index++)
            {
                var y = Bounds.Height -
                    (index + .5) / cachedDensity.Length * Bounds.Height;
                var width = Math.Max(1, cachedDensity[index] / maximum * (Bounds.Width - 5));
                context.DrawLine(
                    DensityPen,
                    new Point(2, y),
                    new Point(2 + width, y));
            }
        }

        foreach (var marker in navigationMarkers)
        {
            var center = new Point(
                Math.Max(8, Bounds.Width - 13),
                BeatToY(marker.Beat.Value));
            context.DrawEllipse(
                NavigationMarkerBrush,
                NavigationMarkerPen,
                center,
                4.2,
                4.2);
        }

        foreach (var group in issues.GroupBy(issue => issue.Beat))
        {
            var crossing = group.Any(issue =>
                issue.Kind == ChartIssueKind.CrossingLongNotes);
            var center = new Point(
                Math.Max(5, Bounds.Width - 6),
                BeatToY(group.Key.Value));
            context.DrawEllipse(
                crossing ? CrossingIssueBrush : OverlapIssueBrush,
                IssueBorderPen,
                center,
                3.6,
                3.6);
        }

        var currentY = Bounds.Height -
            Math.Clamp(currentBeat / Math.Max(1, cachedMaximumBeat), 0, 1) * Bounds.Height;
        context.DrawLine(
            CurrentBeatPen,
            new Point(1, currentY),
            new Point(Bounds.Width - 1, currentY));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        if (session == null ||
            !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            Bounds.Height <= 1) return;
        Focus();
        seeking = true;
        eventArgs.Pointer.Capture(this);
        EnsureDensityCache();
        SeekAt(eventArgs.GetPosition(this).Y, locateIssue: true);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        var point = eventArgs.GetPosition(this);
        UpdateToolTip(NearestIssue(point.Y), NearestNavigationMarker(point.Y));
        if (!seeking ||
            !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        SeekAt(point.Y, locateIssue: false);
        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        if (!seeking) return;
        SeekAt(eventArgs.GetPosition(this).Y, locateIssue: false);
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

    private void SeekAt(double y, bool locateIssue)
    {
        if (session == null) return;
        EnsureDensityCache();
        var marker = NearestNavigationMarker(y);
        if (marker != null)
        {
            NavigationMarkerSeekRequested?.Invoke(marker.Value.Beat);
            return;
        }
        var issue = NearestIssue(y);
        if (issue != null)
        {
            if (locateIssue) IssueSeekRequested?.Invoke(issue);
            SeekRequested?.Invoke(issue.Beat);
            return;
        }

        var fraction = 1 - Math.Clamp(y / Math.Max(1, Bounds.Height), 0, 1);
        SeekRequested?.Invoke(BeatPosition.FromDouble(cachedMaximumBeat * fraction));
    }

    private ChartIssue? NearestIssue(double y)
    {
        if (issues.Count == 0) return null;
        var nearest = issues.MinBy(issue => Math.Abs(BeatToY(issue.Beat.Value) - y));
        return nearest != null &&
               Math.Abs(BeatToY(nearest.Beat.Value) - y) <= 10
            ? nearest
            : null;
    }

    private DesktopNavigationMarker? NearestNavigationMarker(double y)
    {
        if (navigationMarkers.Count == 0) return null;
        DesktopNavigationMarker? nearest = navigationMarkers.MinBy(marker =>
            Math.Abs(BeatToY(marker.Beat.Value) - y));
        return nearest.HasValue &&
               Math.Abs(BeatToY(nearest.Value.Beat.Value) - y) <= 10
            ? nearest
            : null;
    }

    private void UpdateToolTip(
        ChartIssue? issue,
        DesktopNavigationMarker? marker = null) => ToolTip.SetTip(
        this,
        marker != null
            ? AvaloniaLocalizationCatalog.Format("跳转标记 · {0} · 点击定位", marker.Value.Beat)
            : issue == null
                ? DefaultToolTip
                : AvaloniaLocalizationCatalog.Format("{0} · {1} · 点击定位", AvaloniaLocalizationCatalog.Text(issue.Message), issue.Beat));

    private double BeatToY(double beat) =>
        Math.Clamp(
            Bounds.Height -
            beat / Math.Max(1, cachedMaximumBeat) * Bounds.Height,
            0,
            Bounds.Height);

    private void EnsureDensityCache()
    {
        var document = session?.Document;
        var height = Math.Clamp((int)Math.Ceiling(Bounds.Height), 64, 512);
        var changeSerial = session?.ChangeSerial ?? -1;
        if (document != null &&
            changeSerial == cachedChangeSerial &&
            height == cachedHeight) return;

        cachedChangeSerial = changeSerial;
        cachedHeight = height;
        cachedMaximumBeat = Math.Max(
            1,
            Math.Max(
                Math.Max(externalEndBeat, document?.EndBeat ?? 1),
                navigationMarkers.Count == 0
                    ? 0
                    : navigationMarkers.Max(marker => marker.Beat.Value)));
        cachedDensity = document == null ? [] : new double[height];
        if (document == null || document.Notes.Count == 0) return;

        foreach (var note in document.Notes.Where(note => !note.IsDraft))
        {
            cachedMaximumBeat = Math.Max(cachedMaximumBeat, note.Beat.Value);
            if (note.EndBeat is { } endBeat)
                cachedMaximumBeat = Math.Max(cachedMaximumBeat, endBeat.Value);
        }
        foreach (var note in document.Notes.Where(note => !note.IsDraft))
        {
            AddBeat(note.Beat.Value, 1);
            if (note.EndBeat is { } endBeat)
            {
                var start = Math.Min(note.Beat.Value, endBeat.Value);
                var end = Math.Max(note.Beat.Value, endBeat.Value);
                var startIndex = DensityIndex(start, height);
                var endIndex = DensityIndex(end, height);
                for (var index = startIndex; index <= endIndex; index++)
                    cachedDensity[index] += .22;
            }
        }

        void AddBeat(double beat, double weight) =>
            cachedDensity[DensityIndex(beat, height)] += weight;
    }

    private int DensityIndex(double beat, int height) => Math.Clamp(
        (int)Math.Floor(Math.Max(0, beat) / Math.Max(1, cachedMaximumBeat) * height),
        0,
        height - 1);

    private void Session_Changed(object? sender, EventArgs eventArgs)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Session_Changed(sender, eventArgs));
            return;
        }
        if (!ReferenceEquals(sender, session)) return;
        cachedChangeSerial = long.MinValue;
        InvalidateVisual();
    }
}

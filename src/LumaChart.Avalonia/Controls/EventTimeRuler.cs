using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Controls;

public sealed record TimeRulerEvent(double Milliseconds, Guid Id, string Label, bool Bpm, bool Kiai);
public sealed record TimeRulerRange(double StartMilliseconds, double EndMilliseconds);

/// <summary>Cached song-wide event overview. Position updates never enumerate the chart.</summary>
public sealed class EventTimeRuler : Control
{
    private readonly Dictionary<string, FormattedText> tickLabels = new(StringComparer.Ordinal);
    private EditorSession? session;
    private long serial = -1;
    private TimingMap? map;
    private double duration;
    private double position;
    private bool dragging;
    private IReadOnlyList<DesktopNavigationMarker> bookmarks = [];
    public IReadOnlyList<TimeRulerEvent> Events { get; private set; } = [];
    public IReadOnlyList<TimeRulerRange> KiaiRanges { get; private set; } = [];
    public double StartMilliseconds { get; private set; }
    public double EndMilliseconds { get; private set; } = 1000;
    public event Action<double>? SeekRequested;
    public event Action<Guid>? EditEventRequested;
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#171C20"));
    private static readonly IBrush Kiai = new SolidColorBrush(Color.Parse("#725B461E"));
    private static readonly IPen Bpm = new Pen(new SolidColorBrush(Color.Parse("#D97878")), 1.5);
    private static readonly IPen Sv = new Pen(new SolidColorBrush(Color.Parse("#71ADA0")), 1);
    private static readonly IPen CursorLine = new Pen(Brushes.White, 2);
    private static readonly IPen Bookmark = new Pen(new SolidColorBrush(Color.Parse("#E8C879")), 2);
    private static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#4C585F")), 1);
    private static readonly Typeface Font = new("Segoe UI");

    public EventTimeRuler()
    {
        ClipToBounds = true;
        AutomationProperties.SetAutomationId(this, "Editor.EventTimeRuler");
        ToolTip.SetTip(this, "时间轴：红线 BPM · 绿线 SV / 采样 · 金色 Kiai / 书签；左键拖动定位");
    }

    public void SetDocument(EditorSession? value, TimingMap? timing, double audioDuration, IReadOnlyList<DesktopNavigationMarker> markers)
    {
        bookmarks = markers;
        if (ReferenceEquals(session, value) && serial == (value?.ChangeSerial ?? -1) && Math.Abs(duration - audioDuration) < 1)
        { InvalidateVisual(); return; }
        session = value; serial = value?.ChangeSerial ?? -1; map = timing; duration = audioDuration;
        if (value == null || timing == null) { Events = []; KiaiRanges = []; StartMilliseconds = 0; EndMilliseconds = 1000; InvalidateVisual(); return; }
        Events = value.Document.Timing.Where(t => t.Bpm.HasValue || t.Effect.HasValue)
            .Select(t => new TimeRulerEvent(timing.BeatToMilliseconds(t.Beat), t.Id,
                t.Bpm.HasValue ? $"BPM {t.Bpm:0.######}" : t.Effect == TimingEffectKind.Scroll ? $"SV {t.EffectValue:0.###}x" : t.Effect?.ToString() ?? "Offset",
                t.Bpm.HasValue, OsuTimingPointSettings.Read(t).Kiai)).OrderBy(e => e.Milliseconds).ThenByDescending(e => e.Bpm).ToArray();
        var noteTimes = value.Document.Notes.Where(n => !n.IsDraft).Select(n => (
            Start: timing.BeatToMilliseconds(n.Beat), End: timing.BeatToMilliseconds(n.EndBeat ?? n.Beat))).ToArray();
        StartMilliseconds = Math.Min(0, Math.Min(Events.Select(e => e.Milliseconds).DefaultIfEmpty(0).Min(), noteTimes.Select(n => n.Start).DefaultIfEmpty(0).Min()));
        EndMilliseconds = Math.Max(StartMilliseconds + 1000, Math.Max(audioDuration,
            Math.Max(noteTimes.Select(n => n.End).DefaultIfEmpty(0).Max(), Events.Select(e => e.Milliseconds).DefaultIfEmpty(0).Max()) + 1000));
        var ranges = new List<TimeRulerRange>(); double? start = null;
        foreach (var point in Events)
        {
            if (point.Kiai && start == null) start = point.Milliseconds;
            else if (!point.Kiai && start.HasValue) { ranges.Add(new(start.Value, point.Milliseconds)); start = null; }
        }
        if (start.HasValue) ranges.Add(new(start.Value, EndMilliseconds));
        KiaiRanges = ranges; InvalidateVisual();
    }

    public void SetCurrentBeat(double beat) { position = map?.BeatToMilliseconds(BeatPosition.FromDouble(beat)) ?? 0; InvalidateVisual(); }
    public double MillisecondsAt(double x) => StartMilliseconds + Math.Clamp((x - 10) / Math.Max(1, Bounds.Width - 20), 0, 1) * (EndMilliseconds - StartMilliseconds);
    private double X(double milliseconds) => 10 + (milliseconds - StartMilliseconds) / (EndMilliseconds - StartMilliseconds) * Math.Max(1, Bounds.Width - 20);
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || map == null) return;
        var nearest = Events.MinBy(t => Math.Abs(X(t.Milliseconds) - e.GetPosition(this).X));
        if (e.ClickCount >= 2 && nearest != null && Math.Abs(X(nearest.Milliseconds) - e.GetPosition(this).X) < 8)
        { EditEventRequested?.Invoke(nearest.Id); e.Handled = true; return; }
        dragging = true; e.Pointer.Capture(this); Seek(e.GetPosition(this)); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var point = e.GetPosition(this);
        if (dragging) Seek(point);
        else
        {
            var nearest = Events.MinBy(t => Math.Abs(X(t.Milliseconds) - point.X));
            ToolTip.SetTip(this, nearest != null && Math.Abs(X(nearest.Milliseconds) - point.X) < 8
                ? $"{nearest.Milliseconds:0} ms · {nearest.Label}" + (nearest.Kiai ? " · Kiai" : "") + " · 双击编辑事件"
                : $"{MillisecondsAt(point.X):0} ms · 红线 BPM / 绿线 SV / 金色 Kiai、书签");
        }
    }
    private void Seek(Point point) { if (map != null) SeekRequested?.Invoke(map.MillisecondsToBeat(MillisecondsAt(point.X), 1_000_000).Value); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); if (!dragging) return; dragging = false; e.Pointer.Capture(null); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); dragging = false; }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Background, new Rect(Bounds.Size));
        foreach (var range in KiaiRanges)
            context.FillRectangle(Kiai, new Rect(X(range.StartMilliseconds), 4, Math.Max(0, X(range.EndMilliseconds) - X(range.StartMilliseconds)), 34));
        foreach (var point in Events) context.DrawLine(point.Bpm ? Bpm : Sv, new Point(X(point.Milliseconds), point.Bpm ? 4 : 17), new Point(X(point.Milliseconds), 38));
        foreach (var marker in bookmarks) { var x = X(map?.BeatToMilliseconds(marker.Beat) ?? 0); context.DrawLine(Bookmark, new(x, 2), new(x, 12)); }
        var secondsPerLabel = (EndMilliseconds - StartMilliseconds) / Math.Max(1, Bounds.Width / 90) / 1000;
        var step = new[] { 1d, 2, 5, 10, 15, 30, 60, 120, 300, 600 }.FirstOrDefault(n => n >= secondsPerLabel, 1200) * 1000;
        for (var time = Math.Ceiling(StartMilliseconds / step) * step; time <= EndMilliseconds; time += step)
        {
            var x = X(time); context.DrawLine(Tick, new(x, 34), new(x, 43));
            var label = TimeSpan.FromMilliseconds(Math.Abs(time)).ToString(@"m\:ss", CultureInfo.InvariantCulture);
            var key = (time < 0 ? "−" : "") + label;
            if (!tickLabels.TryGetValue(key, out var text))
            {
                if (tickLabels.Count >= 256) tickLabels.Clear();
                tickLabels[key] = text = new FormattedText(key, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10, Brushes.Gray);
            }
            context.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, Bounds.Width - text.Width)), 40));
        }
        context.DrawLine(CursorLine, new(X(position), 0), new(X(position), 42));
    }
}

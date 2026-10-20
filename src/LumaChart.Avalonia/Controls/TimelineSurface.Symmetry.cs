using Avalonia;
using Avalonia.Media;
using Avalonia.Input;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Controls;

public sealed partial class TimelineSurface
{
    public bool IsDefiningSymmetry { get; private set; }
    public bool IsAdjustingSymmetry => symmetryDragEdge != null;
    private BeatPosition? symmetryFirstPoint;
    private string? symmetryDragEdge;
    private IPointer? symmetryPointer;
    public event EventHandler<CenterSymmetrySettings?>? SymmetryRangeChanged;

    public void BeginSymmetryRange()
    {
        CancelInteraction(); SymmetrySettings = null; symmetryFirstPoint = null;
        IsDefiningSymmetry = true; InvalidateVisual();
    }
    public void CancelSymmetryRange()
    {
        IsDefiningSymmetry = false; symmetryFirstPoint = null; symmetryDragEdge = null;
        symmetryPointer?.Capture(null); symmetryPointer = null; SymmetrySettings = null;
        SymmetryRangeChanged?.Invoke(this, null); InvalidateVisual();
    }
    private Rect SymmetryBounds(CenterSymmetrySettings settings, TimelineGeometry geometry)
    {
        var first = settings.Columns.Min(); var last = settings.Columns.Max();
        var a = YAtBeat(settings.Start.Value, geometry); var b = YAtBeat(settings.End.Value, geometry);
        return new Rect(geometry.TrackLeft + first * geometry.LaneWidth, Math.Min(a, b),
            (last - first + 1) * geometry.LaneWidth, Math.Abs(b - a));
    }
    private bool HandleSymmetryPress(PointerPressedEventArgs args, Point point, TimelineGeometry geometry)
    {
        if (session == null || session.IsReadOnly || !session.CanEditNotes || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        if (IsDefiningSymmetry)
        {
            if (point.X < geometry.TrackLeft || point.X > geometry.TrackRight) { args.Handled = true; return true; }
            var beat = PointerToNoteBeat(point.Y);
            if (symmetryFirstPoint is not { } first) symmetryFirstPoint = beat;
            else if (first.CompareTo(beat) != 0)
            {
                SymmetrySettings = new(first.CompareTo(beat) < 0 ? first : beat, first.CompareTo(beat) < 0 ? beat : first,
                    Enumerable.Range(0, session.Document.KeyCount).ToArray());
                IsDefiningSymmetry = false; symmetryFirstPoint = null;
                SymmetryRangeChanged?.Invoke(this, SymmetrySettings);
                StatusChanged?.Invoke(this, "中心对称已启用；拖动框边缘调整范围，再次点击工具关闭。");
            }
            args.Handled = true; InvalidateVisual(); return true;
        }
        if (SymmetrySettings is not { } settings) return false;
        var rect = SymmetryBounds(settings, geometry);
        const double grip = 6;
        if (Tool == TimelineTool.Select && point.X >= rect.Left - grip && point.X <= rect.Right + grip && Math.Abs(point.Y - rect.Top) <= grip) symmetryDragEdge = "end";
        else if (Tool == TimelineTool.Select && point.X >= rect.Left - grip && point.X <= rect.Right + grip && Math.Abs(point.Y - rect.Bottom) <= grip) symmetryDragEdge = "start";
        else if (point.Y >= rect.Top && point.Y <= rect.Bottom && Math.Abs(point.X - rect.Left) <= grip) symmetryDragEdge = "left";
        else if (point.Y >= rect.Top && point.Y <= rect.Bottom && Math.Abs(point.X - rect.Right) <= grip) symmetryDragEdge = "right";
        else return false;
        symmetryPointer = args.Pointer; args.Pointer.Capture(this); args.Handled = true; return true;
    }
    private bool HandleSymmetryMove(PointerEventArgs args)
    {
        if (IsDefiningSymmetry) { InvalidateVisual(); args.Handled = true; return true; }
        if (symmetryDragEdge == null || SymmetrySettings is not { } settings || session == null) return false;
        var point = args.GetPosition(this); var geometry = GetGeometry(session.Document.KeyCount);
        var beat = PointerToNoteBeat(point.Y);
        if (symmetryDragEdge == "start" && beat.CompareTo(settings.End) < 0) settings = settings with { Start = beat };
        if (symmetryDragEdge == "end" && beat.CompareTo(settings.Start) > 0) settings = settings with { End = beat };
        if (symmetryDragEdge is "left" or "right")
        {
            var column = Math.Clamp((int)Math.Round((point.X - geometry.TrackLeft) / geometry.LaneWidth), 0, session.Document.KeyCount);
            var first = settings.Columns.Min(); var last = settings.Columns.Max() + 1;
            if (symmetryDragEdge == "left") first = Math.Min(column, last - 1);
            else last = Math.Max(column, first + 1);
            settings = settings with { Columns = Enumerable.Range(first, last - first).ToArray() };
        }
        SymmetrySettings = settings; SymmetryRangeChanged?.Invoke(this, settings);
        args.Handled = true; return true;
    }
    private bool HandleSymmetryRelease(PointerReleasedEventArgs args)
    {
        if (symmetryDragEdge == null) return false;
        HandleSymmetryMove(args); symmetryDragEdge = null;
        SymmetryRangeChanged?.Invoke(this, SymmetrySettings);
        symmetryPointer?.Capture(null); symmetryPointer = null;
        args.Handled = true; return true;
    }
    private static readonly IBrush SymmetryRangeBrush = new SolidColorBrush(Color.Parse("#106BC9C8"));
    private static readonly IPen SymmetryRangePen = new Pen(new SolidColorBrush(Color.Parse("#906BC9C8")), 1);
    private void DrawSymmetryRange(DrawingContext context, TimelineGeometry geometry)
    {
        if (session == null || session.IsReadOnly) return;
        var settings = SymmetrySettings;
        if (IsDefiningSymmetry && symmetryFirstPoint is { } first && placementHoverPoint is { } hover)
        {
            var last = PointerToNoteBeat(hover.Y);
            if (last.CompareTo(first) != 0) settings = new(first.CompareTo(last) < 0 ? first : last,
                first.CompareTo(last) < 0 ? last : first, Enumerable.Range(0, session.Document.KeyCount).ToArray());
        }
        if (settings == null) return;
        var startY = YAtBeat(settings.Start.Value, geometry); var endY = YAtBeat(settings.End.Value, geometry);
        var top = Math.Clamp(Math.Min(startY, endY), 0, Bounds.Height);
        var bottom = Math.Clamp(Math.Max(startY, endY), 0, Bounds.Height);
        if (bottom <= top) return;
        foreach (var column in settings.Columns.Where(c => c >= 0 && c < session.Document.KeyCount))
        {
            var left = geometry.TrackLeft + column * geometry.LaneWidth;
            context.DrawRectangle(SymmetryRangeBrush, SymmetryRangePen, new Rect(left, top, geometry.LaneWidth, bottom - top));
        }
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#FF6BC9C8")), 2), SymmetryBounds(settings, geometry));
        var center = YAtBeat((settings.Start.Value + settings.End.Value) / 2, geometry);
        if (center >= 0 && center <= Bounds.Height)
            context.DrawLine(SymmetryRangePen, new Point(geometry.TrackLeft, center), new Point(geometry.TrackRight, center));
    }

    private void DrawSymmetryCompanionPreview(DrawingContext context, TimelineGeometry geometry, int lane, BeatPosition head, BeatPosition? tail)
    {
        if (SymmetrySettings == null || session == null || lane < 0 || lane >= session.Document.KeyCount) return;
        ChartNote? mirror;
        try { mirror = CenterSymmetryService.CreateCompanion(new ChartNote { Lane = lane, Beat = head, EndBeat = tail }, SymmetrySettings, session.Document.KeyCount); }
        catch (ArgumentException) { return; }
        if (mirror == null) return;
        var source = new ChartNote { Lane = lane, Beat = head, EndBeat = tail };
        if (CenterSymmetryService.MergeOverlappingCompanion(source, mirror) == null) mirror = source;
        var width = GetRenderedNoteWidth(geometry.LaneWidth);
        var left = geometry.TrackLeft + mirror.Lane * geometry.LaneWidth + (geometry.LaneWidth - width) / 2;
        using (context.PushOpacity(.55))
        {
            if (mirror.EndBeat.HasValue)
                DrawLongNote(context, left, NoteYAtBeat(mirror.Beat, geometry), NoteYAtBeat(mirror.EndBeat.Value, geometry), width, noteHeight);
            else
            {
                var y = NoteYAtBeat(mirror.Beat, geometry);
                var rect = new Rect(left, y - noteHeight / 2, width, noteHeight);
                DrawTapNote(context, rect); context.DrawRectangle(null, SymmetryRangePen, rect);
            }
        }
    }
}

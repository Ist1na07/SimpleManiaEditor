using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Controls;

public sealed partial class TimelineSurface
{
    private static readonly IPen StretchPen = new Pen(new SolidColorBrush(Color.Parse("#FFE5BA72")), 1.5);
    private static readonly IBrush StretchFill = new SolidColorBrush(Color.Parse("#0AE5BA72"));
    private static readonly Cursor StretchMoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor StretchEdgeCursor = new(StandardCursorType.SizeNorthSouth);
    private HashSet<Guid> stretchIds = [];
    private ChartNote[] stretchOriginal = [], stretchPreview = [];
    private BeatPosition stretchStart, stretchEnd, stretchPointerBeat;
    private Point stretchPointerPoint;
    private string? stretchHandle;
    private bool stretchDragging, drawingStretchPreview;
    private IPointer? stretchPointer;
    private long stretchSerial;
    public bool IsStretching => stretchOriginal.Length > 0;

    public bool BeginStretch()
    {
        if (session == null || session.IsReadOnly || !session.CanEditNotes) return false;
        var notes = session.Document.Notes.Where(n => session.Selection.Contains(n.Id) && !hiddenGroups.Contains(n.Group)).Select(n => n.Clone()).ToArray();
        if (notes.Length == 0) { StatusChanged?.Invoke(this, "先选择需要拉伸的按键。"); return false; }
        if (!session.AllowOwnerProtectedEdits && notes.Any(session.IsOwnerProtected))
        { StatusChanged?.Invoke(this, "所选按键包含锁定对象，不能拉伸。"); return false; }
        CancelInteraction(); Tool = TimelineTool.Select;
        stretchOriginal = notes; stretchPreview = notes;
        stretchIds = notes.Select(n => n.Id).ToHashSet(); stretchSerial = session.ChangeSerial;
        stretchStart = notes.MinBy(n => n.Beat.Value)!.Beat;
        stretchEnd = notes.MaxBy(n => (n.EndBeat ?? n.Beat).Value) is { } last ? last.EndBeat ?? last.Beat : stretchStart;
        StatusChanged?.Invoke(this, "拉伸：拖动框内平移；拖动上下边拉伸；松手应用，Esc 取消当前拖动 / 退出。");
        InvalidateVisual(); return true;
    }

    private void EndStretch()
    {
        stretchDragging = false; stretchHandle = null;
        var pointer = stretchPointer; stretchPointer = null;
        stretchOriginal = []; stretchPreview = []; stretchIds.Clear();
        if (pointer?.Captured == this) pointer.Capture(null);
        InvalidateVisual();
    }
    private void CancelStretchDrag()
    {
        if (!stretchDragging) return;
        stretchDragging = false; stretchHandle = null; stretchPreview = stretchOriginal;
        var pointer = stretchPointer; stretchPointer = null;
        if (pointer?.Captured == this) pointer.Capture(null);
        InvalidateVisual();
    }
    public void CancelStretch()
    {
        if (stretchDragging) { CancelStretchDrag(); StatusChanged?.Invoke(this, "已取消本次拉伸，按键未改变。"); }
        else EndStretch();
    }
    private bool ValidStretch()
    {
        if (!IsStretching) return false;
        if (session == null || session.IsReadOnly || session.ChangeSerial != stretchSerial || !stretchIds.SetEquals(session.Selection))
        { EndStretch(); return false; }
        return true;
    }
    private Rect StretchBounds(TimelineGeometry geometry)
    {
        var notes = stretchPreview; var first = notes.Min(n => ProEditorWorkspaceGeometry.GetColumn(n)); var last = notes.Max(n => ProEditorWorkspaceGeometry.GetColumn(n));
        var start = YAtBeat(notes.Min(n => n.Beat.Value), geometry); var end = YAtBeat(notes.Max(n => (n.EndBeat ?? n.Beat).Value), geometry);
        return new Rect(geometry.TrackLeft + first * geometry.LaneWidth, Math.Min(start, end) - noteHeight - 6,
            (last - first + 1) * geometry.LaneWidth, Math.Max(noteHeight + 12, Math.Abs(end - start) + noteHeight + 12));
    }
    private bool HandleStretchPress(PointerPressedEventArgs args)
    {
        if (!ValidStretch()) return false;
        var props = args.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) { EndStretch(); args.Handled = true; return true; }
        if (!props.IsLeftButtonPressed) return false;
        var point = args.GetPosition(this); var geometry = GetGeometry(session!.Document.KeyCount); var rect = StretchBounds(geometry);
        if (!rect.Inflate(6).Contains(point)) { EndStretch(); return false; }
        stretchHandle = stretchEnd != stretchStart && Math.Abs(point.Y - rect.Top) <= 7 ? "end" :
            stretchEnd != stretchStart && Math.Abs(point.Y - rect.Bottom) <= 7 ? "start" : "move";
        stretchPointerBeat = PointerToNoteBeat(point.Y); stretchPointerPoint = point;
        stretchPointer = args.Pointer; stretchDragging = true; args.Pointer.Capture(this);
        args.Handled = true; return true;
    }
    private bool HandleStretchMove(PointerEventArgs args)
    {
        if (!ValidStretch()) return false;
        var point = args.GetPosition(this); var geometry = GetGeometry(session!.Document.KeyCount);
        if (!stretchDragging)
        {
            var rect = StretchBounds(geometry);
            if (!rect.Inflate(6).Contains(point)) return false;
            Cursor = stretchEnd != stretchStart && (Math.Abs(point.Y - rect.Top) <= 7 || Math.Abs(point.Y - rect.Bottom) <= 7)
                ? StretchEdgeCursor : StretchMoveCursor;
            args.Handled = true; return true;
        }
        var pointerBeat = PointerToNoteBeat(point.Y);
        var start = stretchStart; var end = stretchEnd;
        var floor = BeatPosition.FromDouble(MinimumEditableBeat, BeatPosition.MaximumPreservedDenominator);
        var gridStep = new BeatPosition(0, 1, session.SnapDivision);
        var minSpan = stretchEnd.Value - stretchStart.Value < gridStep.Value
            ? NoteStretchService.Offset(stretchEnd, stretchStart, BeatPosition.Zero) : gridStep;
        if (stretchHandle == "start")
        {
            start = NoteStretchService.Offset(start, stretchPointerBeat, pointerBeat);
            var limit = NoteStretchService.Offset(end, minSpan, BeatPosition.Zero);
            if (start.CompareTo(limit) > 0) start = limit;
            if (start.CompareTo(floor) < 0) start = floor;
        }
        else if (stretchHandle == "end")
        {
            end = NoteStretchService.Offset(end, stretchPointerBeat, pointerBeat);
            var limit = NoteStretchService.Offset(start, BeatPosition.Zero, minSpan);
            if (end.CompareTo(limit) < 0) end = limit;
        }
        else
        {
            start = NoteStretchService.Offset(start, stretchPointerBeat, pointerBeat);
            if (start.CompareTo(floor) < 0) start = floor;
            end = NoteStretchService.Offset(end, stretchStart, start);
        }
        stretchPreview = NoteStretchService.Preview(stretchOriginal, stretchStart, stretchEnd, start, end);
        if (stretchHandle == "move")
        {
            var range = GetWorkspaceColumnRange();
            var laneDelta = Math.Clamp((int)Math.Round((point.X - stretchPointerPoint.X) / geometry.LaneWidth),
                range.Minimum - stretchOriginal.Min(n => ProEditorWorkspaceGeometry.GetColumn(n)),
                range.Maximum - stretchOriginal.Max(n => ProEditorWorkspaceGeometry.GetColumn(n)));
            foreach (var n in stretchPreview) ProEditorWorkspaceGeometry.ApplyColumn(n, ProEditorWorkspaceGeometry.GetColumn(n) + laneDelta, session.Document.KeyCount);
        }
        InvalidateVisual(); args.Handled = true; return true;
    }
    private bool HandleStretchRelease(PointerReleasedEventArgs args)
    {
        if (!stretchDragging || args.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return false;
        HandleStretchMove(args);
        if (!stretchDragging) { args.Handled = true; return true; }
        var preview = stretchPreview;
        CancelStretchDrag();
        try
        {
            NoteStretchService.Commit(session!, preview, stretchSerial);
            BeginStretch(); SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (IsExpectedEditException(ex)) { EndStretch(); StatusChanged?.Invoke(this, ex.Message); }
        args.Handled = true; return true;
    }
    private void DrawStretchPreview(DrawingContext context, TimelineGeometry geometry)
    {
        if (!ValidStretch()) return;
        if (stretchDragging)
        {
            drawingStretchPreview = true;
            try { DrawNotes(context, geometry, stretchPreview.Where(n => n.Beat.Value <= BeatAtY(-noteHeight, geometry) &&
                (n.EndBeat ?? n.Beat).Value >= BeatAtY(Bounds.Height + noteHeight, geometry)), draggingSelectionPass: false); }
            finally { drawingStretchPreview = false; }
        }
        var rect = StretchBounds(geometry);
        context.DrawRectangle(StretchFill, StretchPen, rect);
        if (stretchStart == stretchEnd) return;
        foreach (var y in new[] { rect.Top, rect.Bottom })
            context.DrawRectangle(StretchPen.Brush, null, new Rect(rect.Center.X - 12, y - 3, 24, 6), 2, 2);
    }
}

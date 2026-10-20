using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using System.Globalization;
using LumaChart.Application.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Controls;

public sealed partial class TimelineSurface
{
    private IReadOnlyList<ChartNote>? pendingPasteNotes;
    private ChartNote[] pendingPastePreview = [];
    private BeatPosition? pasteAnchor;
    private int pasteColumn;
    private ChartNote[]? patternPasteSource;
    private BeatPosition? patternPinnedStart;
    private int patternPinnedColumn;
    private Point lastPastePoint;
    public double PatternPasteScale { get; private set; } = 1;
    public bool IsPatternPaste => patternPasteSource != null;
    public bool HasPendingPaste => pendingPasteNotes != null;
    public event EventHandler? PendingPasteFinished;

    public void BeginPendingPaste(IReadOnlyList<ChartNote> notes)
    {
        if (Session == null || Session.IsReadOnly || !Session.CanEditNotes) return;
        pendingPasteNotes = notes; pasteAnchor = null;
        MovePendingPaste(placementHoverPoint ?? new Point(GetGeometry(Session.Document.KeyCount).TrackLeft, GetGeometry(Session.Document.KeyCount).JudgementY));
    }

    public void MovePendingPaste(Point point)
    {
        if (pendingPasteNotes == null || session == null) return;
        lastPastePoint = point;
        var anchor = PointerToNoteBeat(point.Y);
        var column = Math.Clamp(WorkspaceColumnAtX(point.X, GetGeometry(session.Document.KeyCount)), 0, session.Document.KeyCount - 1);
        if (patternPinnedStart.HasValue && patternPasteSource != null)
        {
            var first=patternPasteSource.MinBy(n=>n.Beat)!.Beat;
            var last=patternPasteSource.MaxBy(n=>(n.EndBeat??n.Beat).Value)!;
            var end=last.EndBeat??last.Beat;
            var span=BeatArithmetic.Offset(BeatPosition.Zero,end,first);
            var targetSpan=anchor.CompareTo(patternPinnedStart.Value)>=0
                ?BeatArithmetic.Offset(BeatPosition.Zero,anchor,patternPinnedStart.Value)
                :BeatArithmetic.Offset(BeatPosition.Zero,patternPinnedStart.Value,anchor);
            PatternPasteScale=Math.Clamp(targetSpan.Value/(span.Value>0?span.Value:1d/64),1d/64,64);
            if(span.Value>0)
            {
                if(targetSpan.Value/span.Value<1d/64||targetSpan.Value/span.Value>64) targetSpan=BeatArithmetic.Scale(span,PatternPasteScale);
                pendingPasteNotes=NoteStretchService.Preview(patternPasteSource,first,end,BeatPosition.Zero,targetSpan);
            }
            else pendingPasteNotes=ScalePatternNotes(patternPasteSource,PatternPasteScale);
            anchor = patternPinnedStart.Value; column = patternPinnedColumn; pasteAnchor = null;
        }
        if (pasteAnchor == anchor && pasteColumn == column) return;
        pasteAnchor = anchor; pasteColumn = column;
        pendingPastePreview = NotePastePlacement.Preview(pendingPasteNotes, anchor, column, session.Document.KeyCount);
        InvalidateVisual();
    }

    public void ClearPendingPastePreview() { pendingPasteNotes = null; pendingPastePreview = []; pasteAnchor = null; patternPasteSource = null; patternPinnedStart = null; PatternPasteScale = 1; InvalidateVisual(); }
    public void CancelPendingPaste() { ClearPendingPastePreview(); PendingPasteFinished?.Invoke(this, EventArgs.Empty); }

    public void BeginPatternPaste(IReadOnlyList<ChartNote> notes, double scale = 1)
    {
        BeginPendingPaste(notes);
        if (pendingPasteNotes == null) return;
        patternPasteSource = notes.Select(n => n.Clone()).ToArray(); PatternPasteScale = scale;
        pendingPasteNotes = ScalePatternNotes(patternPasteSource, scale); pasteAnchor = null; MovePendingPaste(lastPastePoint);
    }

    private static ChartNote[] ScalePatternNotes(IReadOnlyList<ChartNote> source, double scale) => PatternLibrary.ScaleNotes(source, scale);

    public bool HandlePatternPasteKey(Key key)
    {
        if (key != Key.Enter || patternPasteSource == null || pasteAnchor == null) return false;
        if (patternPinnedStart == null)
        {
            patternPinnedStart = pasteAnchor; patternPinnedColumn = pasteColumn;
            StatusChanged?.Invoke(this, "起点已固定；移动鼠标确定终点，再按 Enter 或单击放置。Esc 取消。");
        }
        else CommitPendingPaste();
        return true;
    }

    private bool HandlePatternPasteWheel(PointerWheelEventArgs e)
    {
        if (patternPasteSource == null || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return false;
        var scales = Enumerable.Range(1, 64).Select(i => i / 16d).Concat([1d / 64, 1d / 48, 1d / 32, 1d / 24, 1d / 12, 1d / 3, 2d / 3, 4d / 3, 6, 8, 12, 16, 24, 32, 48, 64]).Order().ToArray();
        PatternPasteScale = e.Delta.Y > 0 ? scales.FirstOrDefault(s => s > PatternPasteScale + .00001, 64) : scales.LastOrDefault(s => s < PatternPasteScale - .00001, 1d / 64);
        patternPinnedStart = null; pendingPasteNotes = ScalePatternNotes(patternPasteSource, PatternPasteScale); pasteAnchor = null;
        MovePendingPaste(lastPastePoint); e.Handled = true;
        StatusChanged?.Invoke(this, $"排列缩放 {PatternPasteScale:0.###}× · 单击放置，Enter 标记起点 / 终点"); return true;
    }

    private FormattedText? patternFreeHint, patternPinnedHint;
    private static readonly IBrush PatternHintBackground = new SolidColorBrush(Color.Parse("#EE20272B"));
    private void DrawPatternPasteHint(DrawingContext context)
    {
        if (patternPasteSource == null) return;
        var text = patternPinnedStart == null
            ? patternFreeHint ??= Hint("排列放置 · 单击放置 · Ctrl+滚轮缩放\nEnter 定起点 / 终点 · Esc / 右键取消")
            : patternPinnedHint ??= Hint("起点已固定 · 移动鼠标拉伸\nEnter / 单击放置 · Esc / 右键取消");
        static FormattedText Hint(string value) => new(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        var x = Math.Clamp(lastPastePoint.X + 18, 4, Math.Max(4, Bounds.Width - text.Width - 20));
        var y = Math.Clamp(lastPastePoint.Y + 22, 4, Math.Max(4, Bounds.Height - text.Height - 16));
        context.DrawRectangle(PatternHintBackground, null,
            new Rect(x - 6, y - 5, text.Width + 12, text.Height + 10), 3, 3);
        context.DrawText(text, new Point(x, y));
    }

    private void CommitPendingPaste()
    {
        try
        {
            var count = NotePastePlacement.Commit(session!, pendingPastePreview);
            CancelPendingPaste(); SelectionChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, $"已放置 {count} 个音符，可一次撤销。");
        }
        catch (InvalidOperationException ex) { StatusChanged?.Invoke(this, ex.Message); }
    }

    private bool HandlePendingPastePress(PointerPressedEventArgs e)
    {
        if (pendingPasteNotes == null) return false;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed) CancelPendingPaste();
        else if (properties.IsLeftButtonPressed)
        {
            MovePendingPaste(e.GetPosition(this)); CommitPendingPaste();
        }
        e.Handled = true; return true;
    }
}

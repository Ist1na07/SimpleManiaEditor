using Avalonia;
using Avalonia.Input;
using LumaChart.Application.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Controls;

public sealed partial class TimelineSurface
{
    private IReadOnlyList<ChartNote>? pendingPasteNotes;
    private ChartNote[] pendingPastePreview = [];
    private BeatPosition? pasteAnchor;
    private int pasteColumn;
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
        var anchor = PointerToNoteBeat(point.Y);
        var column = Math.Clamp(WorkspaceColumnAtX(point.X, GetGeometry(session.Document.KeyCount)), 0, session.Document.KeyCount - 1);
        if (pasteAnchor == anchor && pasteColumn == column) return;
        pasteAnchor = anchor; pasteColumn = column;
        pendingPastePreview = NotePastePlacement.Preview(pendingPasteNotes, anchor, column, session.Document.KeyCount);
        InvalidateVisual();
    }

    public void ClearPendingPastePreview() { pendingPasteNotes = null; pendingPastePreview = []; pasteAnchor = null; InvalidateVisual(); }
    public void CancelPendingPaste() { ClearPendingPastePreview(); PendingPasteFinished?.Invoke(this, EventArgs.Empty); }

    private bool HandlePendingPastePress(PointerPressedEventArgs e)
    {
        if (pendingPasteNotes == null) return false;
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed) CancelPendingPaste();
        else if (properties.IsLeftButtonPressed)
        {
            try
            {
                MovePendingPaste(e.GetPosition(this));
                var count = NotePastePlacement.Commit(session!, pendingPastePreview);
                CancelPendingPaste(); SelectionChanged?.Invoke(this, EventArgs.Empty);
                StatusChanged?.Invoke(this, $"已放置 {count} 个音符，可一次撤销。");
            }
            catch (InvalidOperationException ex) { StatusChanged?.Invoke(this, ex.Message); }
        }
        e.Handled = true; return true;
    }
}

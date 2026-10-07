namespace LumaChart.Application.Editing;

/// <summary>
/// Applies the selection semantics of a direct Timeline object gesture.
/// Wide and mixed selections are intentional only through marquee/Shift; a plain
/// click always collapses to the hit object, even when it was already selected.
/// Ctrl may preserve a Note batch for moving, but never a stale Timing batch.
/// </summary>
public static class TimelineDirectObjectSelection
{
    public static void SelectNote(
        ISet<Guid> noteSelection,
        ISet<Guid> timingSelection,
        Guid noteId,
        bool preserveNoteBatch = false)
    {
        ArgumentNullException.ThrowIfNull(noteSelection);
        ArgumentNullException.ThrowIfNull(timingSelection);

        timingSelection.Clear();
        if (preserveNoteBatch && noteSelection.Contains(noteId)) return;

        noteSelection.Clear();
        noteSelection.Add(noteId);
    }

    /// <summary>
    /// Applies direct-Note selection first, then decides whether the same hit is
    /// a valid drag anchor. A protected hit may remain selected for inspection,
    /// but it must never borrow other movable Notes from a preserved Ctrl batch.
    /// </summary>
    public static bool TrySelectNoteForMove(
        ISet<Guid> noteSelection,
        ISet<Guid> timingSelection,
        Guid noteId,
        bool preserveNoteBatch,
        Func<Guid, bool> canMove)
    {
        ArgumentNullException.ThrowIfNull(canMove);

        SelectNote(noteSelection, timingSelection, noteId, preserveNoteBatch);
        return canMove(noteId);
    }

    public static void SelectTiming(
        ISet<Guid> noteSelection,
        ISet<Guid> timingSelection,
        Guid timingId)
    {
        ArgumentNullException.ThrowIfNull(noteSelection);
        ArgumentNullException.ThrowIfNull(timingSelection);

        noteSelection.Clear();
        timingSelection.Clear();
        timingSelection.Add(timingId);
    }
}

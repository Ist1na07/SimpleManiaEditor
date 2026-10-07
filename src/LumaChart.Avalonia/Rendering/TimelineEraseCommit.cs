using LumaChart.Application.Editing;

namespace LumaChart.Avalonia.Rendering;

/// <summary>
/// Commits all ids accumulated by a continuous erase gesture through the shared
/// mixed-selection command. Note and Effect hits therefore share one document
/// mutation and one undo entry; BPM-only rows and stale ids remain untouched.
/// </summary>
public static class TimelineEraseCommit
{
    public static MixedSelectionDeleteResult Commit(
        EditorSession session,
        IReadOnlySet<Guid> noteIds,
        IReadOnlySet<Guid> timingIds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(noteIds);
        ArgumentNullException.ThrowIfNull(timingIds);
        return MixedSelectionEditingService.Delete(session, noteIds, timingIds);
    }

    /// <summary>Compatibility overload for the original Note-only slice.</summary>
    public static int Commit(EditorSession session, IReadOnlySet<Guid> noteIds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(noteIds);
        return MixedSelectionEditingService.Delete(session, noteIds, []).DeletedNoteCount;
    }
}

using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

public sealed record TimingBatchDeleteResult(IReadOnlyList<Guid> DeletedTimingIds)
{
    public int DeletedCount => DeletedTimingIds.Count;
    public bool Applied => DeletedCount > 0;
}

/// <summary>
/// Deletes Timing-list rows, including BPM-only rows and Effect rows, with the
/// Windows editor's source-lock filtering. Locked rows are skipped; when every
/// selected row is locked the command is a no-op rather than a partial failure.
/// </summary>
public static class TimingBatchDeleteService
{
    public static TimingBatchDeleteResult DeleteMany(
        EditorSession session,
        IEnumerable<Guid> timingIds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timingIds);
        var requested = timingIds.ToHashSet();
        var deletable = session.Document.Timing
            .Where(timing => requested.Contains(timing.Id) &&
                             (session.AllowOwnerProtectedEdits ||
                              !session.IsTimingOwnerProtected(timing)))
            .ToArray();
        if (deletable.Length == 0) return new TimingBatchDeleteResult([]);

        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能删除 Timing / Effect。");
        if (!session.CanEditTiming)
            throw new InvalidOperationException("当前没有编辑 Timing / Effect 的权限。");

        var deletedIds = deletable.Select(timing => timing.Id).ToArray();
        var deletedIdSet = deletedIds.ToHashSet();
        var serial = session.ChangeSerial;
        session.Mutate(document =>
            document.Timing.RemoveAll(timing => deletedIdSet.Contains(timing.Id)));
        return session.ChangeSerial == serial
            ? new TimingBatchDeleteResult([])
            : new TimingBatchDeleteResult(deletedIds);
    }
}

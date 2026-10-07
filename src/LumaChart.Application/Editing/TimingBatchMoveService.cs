using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

public sealed record TimingBatchMoveResult(
    int MovedCount,
    IReadOnlyList<Guid> MovableTimingIds,
    IReadOnlyList<Guid> FilteredTimingIds,
    BeatPosition AnchorBeat)
{
    public bool Applied => MovedCount > 0;
}

/// <summary>
/// Applies the Windows editor's vertical Timing drag as one atomic command. The
/// requested anchor and every moved row are snapped to the active subdivision;
/// unlike Note movement, an authored off-grid Timing denominator is intentionally
/// rewritten to that subdivision by this gesture.
/// </summary>
public static class TimingBatchMoveService
{
    public static TimingBatchMoveResult MoveTo(
        EditorSession session,
        IEnumerable<Guid> timingIds,
        Guid anchorTimingId,
        BeatPosition requestedAnchorBeat)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(timingIds);

        var requestedIds = timingIds.ToHashSet();
        var selected = session.Document.Timing
            .Where(timing => requestedIds.Contains(timing.Id))
            .ToArray();
        if (selected.Length == 0)
            return new TimingBatchMoveResult(0, [], [], BeatPosition.Zero);

        var anchor = selected.FirstOrDefault(timing => timing.Id == anchorTimingId)
                     ?? throw new ArgumentException(
                         "The Timing move anchor must be part of the selected rows.",
                         nameof(anchorTimingId));
        EnsureTimingMoveAllowed(session);

        if (!session.AllowOwnerProtectedEdits && session.IsTimingOwnerProtected(anchor))
            throw new InvalidOperationException(
                "该 Effect 位于房主锁定选区，成员不能移动。");

        var movable = selected
            .Where(timing => session.AllowOwnerProtectedEdits || !session.IsTimingOwnerProtected(timing))
            .ToArray();
        var movableIds = movable.Select(timing => timing.Id).ToArray();
        var movableIdSet = movableIds.ToHashSet();
        var filteredIds = selected
            .Where(timing => !movableIdSet.Contains(timing.Id))
            .Select(timing => timing.Id)
            .ToArray();

        var division = Math.Clamp(session.SnapDivision, 1, 1920);
        var minimumBeat = movable
            .Select(timing => timing.Beat)
            .Aggregate((left, right) => left.CompareTo(right) <= 0 ? left : right);
        var minimumAnchorValue = anchor.Beat.Value - minimumBeat.Value;
        var requestedValue = requestedAnchorBeat.Denominator == 0
            ? 0
            : requestedAnchorBeat.Value;
        var targetAnchor = BeatPosition
            .FromDouble(Math.Max(minimumAnchorValue, requestedValue))
            .Snap(division);
        if (targetAnchor.CompareTo(anchor.Beat) == 0)
            return new TimingBatchMoveResult(0, movableIds, filteredIds, anchor.Beat);

        var delta = targetAnchor.Value - anchor.Beat.Value;
        var targets = movable.ToDictionary(
            timing => timing.Id,
            timing => BeatPosition.FromDouble(timing.Beat.Value + delta).Snap(division));
        if (targets.Values.Any(beat => beat.CompareTo(BeatPosition.Zero) < 0))
            throw new InvalidOperationException("时间记录不能移动到谱面起点之前。");
        if (!session.AllowOwnerProtectedEdits && targets.Values.Any(session.IsBeatOwnerProtected))
            throw new InvalidOperationException(
                "目标位置位于房主锁定选区，成员不能移动时间记录。");

        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            foreach (var timing in document.Timing.Where(timing => targets.ContainsKey(timing.Id)))
                timing.Beat = targets[timing.Id];
        });
        if (session.ChangeSerial == serial)
            return new TimingBatchMoveResult(0, movableIds, filteredIds, anchor.Beat);

        return new TimingBatchMoveResult(
            movable.Length,
            movableIds,
            filteredIds,
            targetAnchor);
    }

    private static void EnsureTimingMoveAllowed(EditorSession session)
    {
        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能移动 Timing / Effect。");
        if (!session.CanEditTiming)
            throw new InvalidOperationException("当前没有编辑 Timing / Effect 的权限。");
    }
}

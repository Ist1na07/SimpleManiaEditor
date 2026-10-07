using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

public enum NoteLaneRandomizationMode
{
    FixedPermutation,
    PerBeatPermutation
}

public readonly record struct NoteLaneRandomizationResult(
    int ChangedCount,
    int SelectedCount,
    int RowCount,
    NoteLaneRandomizationMode Mode,
    bool CollisionBlocked = false);

/// <summary>
/// Randomizes official lanes for one visible selection while keeping permission,
/// owner-lock and single-undo semantics in the shared editing layer.
/// </summary>
public static class NoteLaneRandomizationService
{
    public static NoteLaneRandomizationResult RandomizeSelection(
        EditorSession session,
        IEnumerable<Guid> selectedNoteIds,
        NoteLaneRandomizationMode mode,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selectedNoteIds);

        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能随机排列 Note。");
        if (!session.CanEditNotes)
            throw new InvalidOperationException("当前没有编辑 Note 的权限，不能随机排列 Note。");

        var requested = selectedNoteIds.Distinct().ToHashSet();
        if (requested.Count == 0)
            return new NoteLaneRandomizationResult(0, 0, 0, mode);

        var selected = session.Document.Notes
            .Where(note => requested.Contains(note.Id))
            .ToArray();
        if (selected.Length == 0)
            return new NoteLaneRandomizationResult(0, 0, 0, mode);
        if (!session.AllowOwnerProtectedEdits && selected.Any(session.IsOwnerProtected))
            throw new InvalidOperationException(
                "随机排列目标与房主锁定选区相交，成员不能移动 Note。");

        // Pro Editor draft columns are intentionally kept in place: a K-key
        // permutation has a well-defined domain only for the K official lanes.
        var official = selected
            .Where(note => !note.IsDraft)
            .ToArray();
        var keyCount = Math.Clamp(session.Document.KeyCount, 1, 18);
        var rowCount = official.Select(note => note.Beat).Distinct().Count();
        if (official.Length == 0 || keyCount == 1)
            return new NoteLaneRandomizationResult(0, official.Length, rowCount, mode);

        random ??= Random.Shared;
        Dictionary<Guid, int>? targets = null;
        const int maximumAttempts = 96;
        for (var attempt = 0; attempt < maximumAttempts && targets == null; attempt++)
        {
            var candidate = new Dictionary<Guid, int>(official.Length);
            if (mode == NoteLaneRandomizationMode.FixedPermutation)
            {
                var permutation = CreateChangedPermutation(
                    keyCount,
                    official.Select(note => note.Lane),
                    random);
                foreach (var note in official)
                    candidate[note.Id] = permutation[note.Lane];
            }
            else
            {
                foreach (var row in official.GroupBy(note => note.Beat))
                {
                    var permutation = CreateChangedPermutation(
                        keyCount,
                        row.Select(note => note.Lane),
                        random);
                    foreach (var note in row)
                        candidate[note.Id] = permutation[note.Lane];
                }
            }

            if (!HasAffectedConflict(session.Document, candidate))
                targets = candidate;
        }
        if (targets == null)
            return new NoteLaneRandomizationResult(
                0,
                official.Length,
                rowCount,
                mode,
                CollisionBlocked: true);

        var changedIds = official
            .Where(note => targets[note.Id] != note.Lane)
            .Select(note => note.Id)
            .ToHashSet();
        if (changedIds.Count == 0)
            return new NoteLaneRandomizationResult(0, official.Length, rowCount, mode);

        session.Mutate(document =>
        {
            foreach (var note in document.Notes.Where(note => changedIds.Contains(note.Id)))
                note.Lane = targets[note.Id];
        });
        return new NoteLaneRandomizationResult(
            changedIds.Count,
            official.Length,
            rowCount,
            mode);
    }

    private static bool HasAffectedConflict(
        ChartDocument document,
        IReadOnlyDictionary<Guid, int> targets)
    {
        foreach (var lane in document.Notes
                     .Where(note => !note.IsDraft)
                     .GroupBy(note => targets.TryGetValue(note.Id, out var target)
                         ? target
                         : note.Lane))
        {
            var notes = lane
                .OrderBy(note => note.Beat.Value)
                .ThenBy(note => note.EndBeat?.Value ?? note.Beat.Value)
                .ToArray();
            for (var leftIndex = 0; leftIndex < notes.Length; leftIndex++)
            {
                var left = notes[leftIndex];
                var leftEnd = left.EndBeat?.Value ?? left.Beat.Value;
                for (var rightIndex = leftIndex + 1; rightIndex < notes.Length; rightIndex++)
                {
                    var right = notes[rightIndex];
                    if (right.Beat.Value > leftEnd + 1e-7 &&
                        right.Beat.Value > left.Beat.Value + 1e-7)
                        break;
                    if (!targets.ContainsKey(left.Id) && !targets.ContainsKey(right.Id))
                        continue;
                    if (Conflicts(left, right)) return true;
                }
            }
        }
        return false;
    }

    private static bool Conflicts(ChartNote left, ChartNote right)
    {
        const double epsilon = 1e-7;
        var leftEnd = left.EndBeat?.Value ?? left.Beat.Value;
        var rightEnd = right.EndBeat?.Value ?? right.Beat.Value;
        var sameStart = Math.Abs(left.Beat.Value - right.Beat.Value) <= epsilon;
        var tapInsideHold =
            (!left.IsHold && right.IsHold &&
             left.Beat.Value > right.Beat.Value + epsilon &&
             left.Beat.Value < rightEnd - epsilon) ||
            (!right.IsHold && left.IsHold &&
             right.Beat.Value > left.Beat.Value + epsilon &&
             right.Beat.Value < leftEnd - epsilon);
        var holdOverlap = left.IsHold && right.IsHold &&
                          right.Beat.Value < leftEnd - epsilon &&
                          left.Beat.Value < rightEnd - epsilon;
        return sameStart || tapInsideHold || holdOverlap;
    }

    private static int[] CreateChangedPermutation(
        int keyCount,
        IEnumerable<int> sourceLanes,
        Random random)
    {
        var permutation = Enumerable.Range(0, keyCount).ToArray();
        for (var index = permutation.Length - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (permutation[index], permutation[swap]) =
                (permutation[swap], permutation[index]);
        }

        var used = sourceLanes
            .Where(lane => lane >= 0 && lane < keyCount)
            .Distinct()
            .ToArray();
        if (used.Length == 0 || used.Any(lane => permutation[lane] != lane))
            return permutation;

        // A random shuffle can legally be the identity. An explicit randomize
        // command should still produce a visible result whenever K > 1.
        var source = used[random.Next(used.Length)];
        var candidates = Enumerable.Range(0, keyCount)
            .Where(lane => lane != source)
            .ToArray();
        var target = candidates[random.Next(candidates.Length)];
        (permutation[source], permutation[target]) =
            (permutation[target], permutation[source]);
        return permutation;
    }
}

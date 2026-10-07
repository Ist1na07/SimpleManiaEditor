using LumaChart.Core.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

public sealed class EditorSession
{
    private readonly Stack<HistoryEntry> undo = new();
    private readonly Stack<HistoryEntry> redo = new();
    private readonly HashSet<Guid> ownerProtectedNotes = [];
    private readonly List<OwnerProtectedRegion> ownerProtectedRegions = [];
    private ChartDocument document = new();
    private ChartDocument? interactiveStart;
    private long changeSerial;
    private long savedChangeSerial;
    private bool hasReplacedDocument;

    public ChartDocument Document => document;
    public HashSet<Guid> Selection { get; } = [];
    public IReadOnlySet<Guid> OwnerProtectedNotes => ownerProtectedNotes;
    public IReadOnlyList<OwnerProtectedRegion> OwnerProtectedRegions => ownerProtectedRegions;
    public bool AllowOwnerProtectedEdits { get; set; } = true;
    /// <summary>Client-side observer guard. Remote authoritative mutations remain allowed.</summary>
    public bool IsReadOnly { get; set; }
    public bool CanEditMetadata { get; set; } = true;
    public bool CanEditTiming { get; set; } = true;
    public bool CanEditNotes { get; set; } = true;
    public bool IsDirty => Interlocked.Read(ref changeSerial) != Interlocked.Read(ref savedChangeSerial);
    public long ChangeSerial => Interlocked.Read(ref changeSerial);
    public int SnapDivision { get; set; } = 4;
    public bool IsInteractiveMutationActive => interactiveStart != null;
    public event EventHandler? Changed;
    public event EventHandler<string>? PermissionDenied;

    /// <summary>
    /// Captures the last committed document state. Live mouse gestures mutate the
    /// presentation document in place, but must not leak their intermediate values
    /// to autosave or multiplayer synchronization before mouse-up commits them.
    /// </summary>
    public ChartDocument CaptureCommittedDocument() => (interactiveStart ?? document).Clone();

    /// <summary>
    /// Applies server-only creator attribution without interrupting a live gesture.
    /// Returns false when the authoritative document contains any editable change;
    /// callers must then use the normal remote-mutation path, which safely cancels
    /// the gesture rather than overwriting a real concurrent edit.
    /// </summary>
    public bool TryMergeRemoteAttributionDuringInteractiveMutation(ChartDocument authoritative)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        if (interactiveStart == null || !DocumentEditableContentEquals(interactiveStart, authoritative))
            return false;

        MergeCreatorAttribution(interactiveStart, authoritative);
        MergeCreatorAttribution(document, authoritative);
        return true;
    }

    /// <summary>
    /// Replaces the whole document (initial load, reload, backup restore or an
    /// authoritative room snapshot). It deliberately ignores <see cref="IsReadOnly"/>:
    /// observers must still receive room snapshots; callers restoring local
    /// content own that permission decision.
    /// </summary>
    public void ReplaceDocument(ChartDocument next, bool markDirty = false)
    {
        document = next;
        document.Normalize();
        interactiveStart = null;
        undo.Clear(); redo.Clear(); Selection.Clear(); ownerProtectedNotes.Clear(); ownerProtectedRegions.Clear();
        // Panels, difficulty caches, room sync and the save guard in TryMarkSaved
        // compare serials only. Resetting to 0/1 let an old cached serial match a
        // different document (ABA), so after the first load serials only grow.
        // The first load of a fresh session keeps the historical 0/1 numbering:
        // nothing can have observed a serial for real content before it.
        long serial;
        if (!hasReplacedDocument && Interlocked.Read(ref changeSerial) == 0)
        {
            serial = markDirty ? 1 : 0;
            Interlocked.Exchange(ref changeSerial, serial);
        }
        else serial = Interlocked.Increment(ref changeSerial);
        hasReplacedDocument = true;
        // An unsaved replacement keeps a saved serial that no future change can
        // reach again, so it stays dirty until the next real save.
        Interlocked.Exchange(ref savedChangeSerial, markDirty ? serial - 1 : serial);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Mutate(Action<ChartDocument> mutation)
    {
        if (IsReadOnly) return;
        if (interactiveStart != null) throw new InvalidOperationException("An interactive edit is already active.");
        var previous = document.Clone();
        try
        {
            mutation(document);
            document.Normalize();
            var deniedCategory = GetDeniedEditCategory(previous, document);
            if (deniedCategory != null)
            {
                document = previous;
                PermissionDenied?.Invoke(this, deniedCategory);
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!AllowOwnerProtectedEdits) EnsureOwnerProtectedContentUnchanged(previous, document);
            RemoveMissingState();
            if (DocumentEditableContentEquals(previous, document)) return;
        }
        catch
        {
            document = previous;
            // The document instance changed back to its snapshot. Notify retained
            // views so indexes do not keep references to objects touched by the
            // failed mutation.
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
        undo.Push(new HistoryEntry(previous, document.Clone()));
        if (undo.Count > 200) TrimOldest(undo);
        redo.Clear();
        Interlocked.Increment(ref changeSerial);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies a server-authoritative room edit without creating a local undo entry.
    /// The server has already validated room locks and operation ordering.
    /// </summary>
    public void ApplyRemoteMutation(Action<ChartDocument> mutation)
    {
        if (interactiveStart != null) CancelInteractiveMutation();
        var previous = document.Clone();
        try
        {
            mutation(document);
            document.Normalize();
            RemoveMissingState();
        }
        catch
        {
            document = previous;
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
        Interlocked.Increment(ref changeSerial);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts a live mouse edit that will become a single undo operation.</summary>
    public void BeginInteractiveMutation()
    {
        if (IsReadOnly) return;
        if (interactiveStart != null) return;
        interactiveStart = document.Clone();
    }

    /// <summary>Updates a live edit without filling the undo stack on every mouse move.</summary>
    public void UpdateInteractiveMutation(Action<ChartDocument> mutation)
    {
        if (IsReadOnly) return;
        if (interactiveStart == null) throw new InvalidOperationException("No interactive edit is active.");
        var start = interactiveStart;
        try
        {
            mutation(document);
            document.Normalize();
            var deniedCategory = GetDeniedEditCategory(start, document);
            if (deniedCategory != null)
            {
                document = start;
                interactiveStart = null;
                PermissionDenied?.Invoke(this, deniedCategory);
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (!AllowOwnerProtectedEdits) EnsureOwnerProtectedContentUnchanged(start, document);
            RemoveMissingState();
        }
        catch
        {
            document = start;
            interactiveStart = null;
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
    }

    public void CommitInteractiveMutation()
    {
        if (IsReadOnly) { interactiveStart = null; return; }
        if (interactiveStart == null) return;
        if (DocumentEditableContentEquals(interactiveStart, document))
        {
            interactiveStart = null;
            return;
        }
        undo.Push(new HistoryEntry(interactiveStart, document.Clone()));
        if (undo.Count > 200) TrimOldest(undo);
        interactiveStart = null;
        redo.Clear();
        Interlocked.Increment(ref changeSerial);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CancelInteractiveMutation()
    {
        if (interactiveStart == null) return;
        document = interactiveStart;
        interactiveStart = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (IsReadOnly) return false;
        if (interactiveStart != null) CancelInteractiveMutation();
        if (undo.Count == 0) return false;
        var entry = undo.Pop();
        redo.Push(entry);
        document = entry.Before.Clone();
        RemoveMissingSelection();
        Interlocked.Increment(ref changeSerial); Changed?.Invoke(this, EventArgs.Empty); return true;
    }

    public bool Redo()
    {
        if (IsReadOnly) return false;
        if (interactiveStart != null) CancelInteractiveMutation();
        if (redo.Count == 0) return false;
        var entry = redo.Pop();
        undo.Push(entry);
        document = entry.After.Clone();
        RemoveMissingSelection();
        Interlocked.Increment(ref changeSerial); Changed?.Invoke(this, EventArgs.Empty); return true;
    }

    public void MarkSaved()
    {
        Interlocked.Exchange(ref savedChangeSerial, ChangeSerial);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryMarkSaved(long expectedChangeSerial)
    {
        if (ChangeSerial != expectedChangeSerial) return false;
        Interlocked.Exchange(ref savedChangeSerial, expectedChangeSerial);
        if (ChangeSerial != expectedChangeSerial) return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public int SetOwnerProtection(IEnumerable<Guid> noteIds, bool isProtected)
    {
        var existing = document.Notes.Select(note => note.Id).ToHashSet();
        var changed = 0;
        foreach (var id in noteIds.Where(existing.Contains).Distinct())
        {
            if (isProtected ? ownerProtectedNotes.Add(id) : ownerProtectedNotes.Remove(id)) changed++;
        }
        if (changed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    public bool IsOwnerProtected(Guid noteId)
    {
        var note = document.Notes.FirstOrDefault(candidate => candidate.Id == noteId);
        return note != null && IsOwnerProtected(note);
    }

    /// <summary>
    /// Adds a time-only owner lock. EndBeat is exclusive so the first beat of the
    /// following measure remains editable.
    /// </summary>
    public OwnerProtectedRegion AddOwnerProtectedRegion(BeatPosition startBeat, BeatPosition endBeat)
    {
        if (endBeat.CompareTo(startBeat) < 0) (startBeat, endBeat) = (endBeat, startBeat);
        if (endBeat.CompareTo(startBeat) == 0) endBeat = new BeatPosition(startBeat.Bar + 1, 0, 1);
        var region = new OwnerProtectedRegion(Guid.NewGuid(), startBeat, endBeat);
        ownerProtectedRegions.Add(region);
        Changed?.Invoke(this, EventArgs.Empty);
        return region;
    }

    public int RemoveOwnerProtectedRegionsIntersecting(BeatPosition startBeat, BeatPosition endBeat)
    {
        if (endBeat.CompareTo(startBeat) < 0) (startBeat, endBeat) = (endBeat, startBeat);
        var removed = ownerProtectedRegions.RemoveAll(region =>
            region.StartBeat.CompareTo(endBeat) < 0 && region.EndBeat.CompareTo(startBeat) > 0);
        if (removed > 0) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public void ReplaceOwnerProtectedRegions(IEnumerable<OwnerProtectedRegion> regions)
    {
        var next = regions.DistinctBy(region => region.Id).ToArray();
        if (ownerProtectedRegions.SequenceEqual(next)) return;
        ownerProtectedRegions.Clear();
        ownerProtectedRegions.AddRange(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsOwnerProtected(ChartNote note) => ownerProtectedNotes.Contains(note.Id) ||
        ownerProtectedRegions.Any(region => RegionIntersectsNote(region, note));

    public bool IntersectsOwnerProtectedRegion(BeatPosition startBeat, BeatPosition endBeat)
    {
        if (endBeat.CompareTo(startBeat) < 0) (startBeat, endBeat) = (endBeat, startBeat);
        return ownerProtectedRegions.Any(region => RangeIntersectsRegion(startBeat, endBeat, region));
    }

    public bool IsBeatOwnerProtected(BeatPosition beat) => ownerProtectedRegions.Any(region =>
        beat.CompareTo(region.StartBeat) >= 0 && beat.CompareTo(region.EndBeat) < 0);

    public bool IsTimingOwnerProtected(TimingEvent timing) => IsBeatOwnerProtected(timing.Beat);

    private void RemoveMissingState()
    {
        var noteIds = document.Notes.Select(note => note.Id).ToHashSet();
        Selection.RemoveWhere(id => !noteIds.Contains(id));
        ownerProtectedNotes.RemoveWhere(id => !noteIds.Contains(id));
    }

    private void RemoveMissingSelection() => RemoveMissingState();

    private void EnsureOwnerProtectedContentUnchanged(ChartDocument previous, ChartDocument current)
    {
        var previousNotes = previous.Notes
            .Where(note => ownerProtectedNotes.Contains(note.Id) || ownerProtectedRegions.Any(region => RegionIntersectsNote(region, note)))
            .ToDictionary(note => note.Id, ProtectedNoteState.FromNote);
        var currentNotes = current.Notes
            .Where(note => ownerProtectedNotes.Contains(note.Id) || ownerProtectedRegions.Any(region => RegionIntersectsNote(region, note)))
            .ToDictionary(note => note.Id, ProtectedNoteState.FromNote);
        var notesMatch = previousNotes.Count == currentNotes.Count &&
                         previousNotes.All(pair => currentNotes.TryGetValue(pair.Key, out var value) && value == pair.Value);
        var previousTiming = previous.Timing.Where(IsTimingOwnerProtected)
            .ToDictionary(timing => timing.Id, ProtectedTimingState.FromTiming);
        var currentTiming = current.Timing.Where(IsTimingOwnerProtected)
            .ToDictionary(timing => timing.Id, ProtectedTimingState.FromTiming);
        var timingMatches = previousTiming.Count == currentTiming.Count &&
                            previousTiming.All(pair => currentTiming.TryGetValue(pair.Key, out var value) && value == pair.Value);
        if (notesMatch && timingMatches) return;
        throw new InvalidOperationException("该操作触及房主锁定选区，成员不能在其中新增、修改、移动或删除 Note / Effect。");
    }

    private static bool RegionIntersectsNote(OwnerProtectedRegion region, ChartNote note)
    {
        var noteEnd = note.EndBeat ?? note.Beat;
        return RangeIntersectsRegion(note.Beat, noteEnd, region);
    }

    private static bool RangeIntersectsRegion(
        BeatPosition startBeat, BeatPosition endBeat, OwnerProtectedRegion region)
    {
        // Tap notes and point preflights have a zero-length interval.
        if (startBeat.CompareTo(endBeat) == 0)
            return startBeat.CompareTo(region.StartBeat) >= 0 && startBeat.CompareTo(region.EndBeat) < 0;
        return startBeat.CompareTo(region.EndBeat) < 0 && endBeat.CompareTo(region.StartBeat) > 0;
    }

    private static void TrimOldest(Stack<HistoryEntry> stack)
    {
        var items = stack.Reverse().Skip(1).ToArray();
        stack.Clear(); foreach (var item in items) stack.Push(item);
    }

    /// <summary>
    /// Reverses only the entities touched by the latest local edit. Unrelated remote
    /// room changes are retained. If another member changed the same entity after the
    /// local edit, the undo is rejected instead of overwriting their work.
    /// </summary>
    public ScopedHistoryResult UndoOwnChanges()
    {
        if (IsReadOnly) return ScopedHistoryResult.None;
        if (interactiveStart != null) CancelInteractiveMutation();
        if (undo.Count == 0) return ScopedHistoryResult.None;
        var entry = undo.Peek();
        var result = TryApplyScopedHistory(entry.After, entry.Before);
        if (result != ScopedHistoryResult.Applied) return result;
        undo.Pop();
        redo.Push(entry);
        MarkHistoryApplied();
        return ScopedHistoryResult.Applied;
    }

    /// <summary>Reapplies only the latest locally undone entity changes.</summary>
    public ScopedHistoryResult RedoOwnChanges()
    {
        if (IsReadOnly) return ScopedHistoryResult.None;
        if (interactiveStart != null) CancelInteractiveMutation();
        if (redo.Count == 0) return ScopedHistoryResult.None;
        var entry = redo.Peek();
        var result = TryApplyScopedHistory(entry.Before, entry.After);
        if (result != ScopedHistoryResult.Applied) return result;
        redo.Pop();
        undo.Push(entry);
        MarkHistoryApplied();
        return ScopedHistoryResult.Applied;
    }

    private void MarkHistoryApplied()
    {
        document.Normalize();
        RemoveMissingState();
        Interlocked.Increment(ref changeSerial);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ScopedHistoryResult TryApplyScopedHistory(ChartDocument expected, ChartDocument replacement)
    {
        var candidate = document.Clone();
        if (!TryPatchNotes(candidate, expected, replacement) ||
            !TryPatchTiming(candidate, expected, replacement) ||
            !TryPatchDocumentFields(candidate, expected, replacement)) return ScopedHistoryResult.Conflict;
        candidate.Normalize();
        var deniedCategory = GetDeniedEditCategory(document, candidate);
        if (deniedCategory != null)
        {
            PermissionDenied?.Invoke(this, deniedCategory);
            return ScopedHistoryResult.PermissionDenied;
        }
        if (!AllowOwnerProtectedEdits)
        {
            try { EnsureOwnerProtectedContentUnchanged(document, candidate); }
            catch (InvalidOperationException) { return ScopedHistoryResult.Conflict; }
        }
        document = candidate;
        return ScopedHistoryResult.Applied;
    }

    private static bool TryPatchNotes(ChartDocument candidate, ChartDocument expected, ChartDocument replacement)
    {
        var expectedById = expected.Notes.ToDictionary(note => note.Id);
        var replacementById = replacement.Notes.ToDictionary(note => note.Id);
        var currentById = candidate.Notes.ToDictionary(note => note.Id);
        foreach (var id in expectedById.Keys.Union(replacementById.Keys))
        {
            expectedById.TryGetValue(id, out var expectedNote);
            replacementById.TryGetValue(id, out var replacementNote);
            if (NoteContentEquals(expectedNote, replacementNote)) continue;
            currentById.TryGetValue(id, out var currentNote);
            if (!CanReplaceEntity(currentNote, expectedNote, replacementNote, NoteContentEquals)) return false;
        }
        foreach (var id in expectedById.Keys.Union(replacementById.Keys))
        {
            expectedById.TryGetValue(id, out var expectedNote);
            replacementById.TryGetValue(id, out var replacementNote);
            if (NoteContentEquals(expectedNote, replacementNote)) continue;
            var index = candidate.Notes.FindIndex(note => note.Id == id);
            if (replacementNote == null)
            {
                if (index >= 0) candidate.Notes.RemoveAt(index);
            }
            else if (index >= 0) candidate.Notes[index] = replacementNote.Clone();
            else candidate.Notes.Add(replacementNote.Clone());
        }
        return true;
    }

    private static bool TryPatchTiming(ChartDocument candidate, ChartDocument expected, ChartDocument replacement)
    {
        var expectedById = expected.Timing.ToDictionary(item => item.Id);
        var replacementById = replacement.Timing.ToDictionary(item => item.Id);
        var currentById = candidate.Timing.ToDictionary(item => item.Id);
        foreach (var id in expectedById.Keys.Union(replacementById.Keys))
        {
            expectedById.TryGetValue(id, out var expectedItem);
            replacementById.TryGetValue(id, out var replacementItem);
            if (TimingContentEquals(expectedItem, replacementItem)) continue;
            currentById.TryGetValue(id, out var currentItem);
            if (!CanReplaceEntity(currentItem, expectedItem, replacementItem, TimingContentEquals)) return false;
        }
        foreach (var id in expectedById.Keys.Union(replacementById.Keys))
        {
            expectedById.TryGetValue(id, out var expectedItem);
            replacementById.TryGetValue(id, out var replacementItem);
            if (TimingContentEquals(expectedItem, replacementItem)) continue;
            var index = candidate.Timing.FindIndex(item => item.Id == id);
            if (replacementItem == null)
            {
                if (index >= 0) candidate.Timing.RemoveAt(index);
            }
            else if (index >= 0) candidate.Timing[index] = replacementItem.Clone();
            else candidate.Timing.Add(replacementItem.Clone());
        }
        return true;
    }

    private static bool TryPatchDocumentFields(
        ChartDocument candidate, ChartDocument expected, ChartDocument replacement)
    {
        if (expected.KeyCount != replacement.KeyCount)
        {
            if (candidate.KeyCount != expected.KeyCount && candidate.KeyCount != replacement.KeyCount) return false;
            candidate.KeyCount = replacement.KeyCount;
        }
        var expectedMetadata = MetadataState.From(expected.Metadata);
        var replacementMetadata = MetadataState.From(replacement.Metadata);
        if (expectedMetadata != replacementMetadata)
        {
            var currentMetadata = MetadataState.From(candidate.Metadata);
            if (currentMetadata != expectedMetadata && currentMetadata != replacementMetadata) return false;
            candidate.Metadata = replacement.Metadata.Clone();
        }
        return true;
    }

    private static bool CanReplaceEntity<T>(
        T? current, T? expected, T? replacement, Func<T?, T?, bool> equals) where T : class
    {
        // The replacement may already be present after a retry; otherwise the
        // current value must still match the state produced by this local edit.
        return equals(current, replacement) || equals(current, expected);
    }

    private static bool NoteContentEquals(ChartNote? left, ChartNote? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;
        return left.Id == right.Id && left.Lane == right.Lane && left.Beat == right.Beat &&
               left.EndBeat == right.EndBeat &&
               string.Equals(left.Sound ?? string.Empty, right.Sound ?? string.Empty, StringComparison.Ordinal) &&
               left.Volume == right.Volume &&
               left.Group == right.Group && left.IsDraft == right.IsDraft && left.DraftColumn == right.DraftColumn &&
               System.Text.Json.Nodes.JsonNode.DeepEquals(left.UnknownData, right.UnknownData);
    }

    private static bool TimingContentEquals(TimingEvent? left, TimingEvent? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null) return false;
        return left.Id == right.Id && left.Beat == right.Beat && left.Bpm == right.Bpm &&
               left.Delay.Equals(right.Delay) && left.Effect == right.Effect &&
               left.EffectValue.Equals(right.EffectValue) && left.EffectValueText == right.EffectValueText &&
               left.EffectFieldName == right.EffectFieldName &&
               System.Text.Json.Nodes.JsonNode.DeepEquals(left.UnknownData, right.UnknownData);
    }

    private static bool DocumentEditableContentEquals(ChartDocument left, ChartDocument right) =>
        left.KeyCount == right.KeyCount &&
        MetadataState.From(left.Metadata) == MetadataState.From(right.Metadata) &&
        EntityCollectionsEqual(left.Notes, right.Notes, note => note.Id, NoteContentEquals) &&
        EntityCollectionsEqual(left.Timing, right.Timing, item => item.Id, TimingContentEquals);

    private static void MergeCreatorAttribution(ChartDocument target, ChartDocument source)
    {
        var sourceNotes = source.Notes.ToDictionary(note => note.Id);
        foreach (var note in target.Notes)
            if (sourceNotes.TryGetValue(note.Id, out var sourceNote))
                note.OnlineCreatorUserId = sourceNote.OnlineCreatorUserId;

        var sourceTiming = source.Timing.ToDictionary(item => item.Id);
        foreach (var item in target.Timing)
            if (sourceTiming.TryGetValue(item.Id, out var sourceItem))
                item.OnlineCreatorUserId = sourceItem.OnlineCreatorUserId;
    }

    private string? GetDeniedEditCategory(ChartDocument before, ChartDocument after)
    {
        if (!CanEditNotes && !EntityCollectionsEqual(before.Notes, after.Notes, note => note.Id, NoteContentEquals))
            return "Note";
        if (!CanEditTiming && !EntityCollectionsEqual(before.Timing, after.Timing, item => item.Id, TimingContentEquals))
            return "Effect";
        if (!CanEditMetadata &&
            (before.KeyCount != after.KeyCount || MetadataState.From(before.Metadata) != MetadataState.From(after.Metadata)))
            return "谱面信息";
        return null;
    }

    private static bool EntityCollectionsEqual<T>(
        IReadOnlyCollection<T> left,
        IReadOnlyCollection<T> right,
        Func<T, Guid> getId,
        Func<T?, T?, bool> equals) where T : class
    {
        if (left.Count != right.Count) return false;
        var rightById = right.ToDictionary(getId);
        return left.All(item => rightById.TryGetValue(getId(item), out var other) && equals(item, other));
    }

    private readonly record struct MetadataState(
        string Title, string TitleOriginal, string Artist, string ArtistOriginal, string Creator, string Version,
        string AudioFile, string CoverFile, string BackgroundFile, int PreviewTime, double Bpm, double OverallDifficulty,
        double HpDrainRate)
    {
        public static MetadataState From(ChartMetadata value) => new(
            value.Title, value.TitleOriginal, value.Artist, value.ArtistOriginal, value.Creator, value.Version,
            value.AudioFile, value.CoverFile, value.BackgroundFile, value.PreviewTime, value.Bpm, value.OverallDifficulty,
            value.HpDrainRate);
    }

    private sealed record HistoryEntry(ChartDocument Before, ChartDocument After);

    private readonly record struct ProtectedNoteState(
        int Lane,
        BeatPosition Beat,
        BeatPosition? EndBeat,
        string? Sound,
        int Volume,
        int Group,
        bool IsDraft,
        int DraftColumn)
    {
        public static ProtectedNoteState FromNote(ChartNote note) => new(
            note.Lane,
            note.Beat,
            note.EndBeat,
            note.Sound,
            note.Volume,
            note.Group,
            note.IsDraft,
            note.DraftColumn);
    }

    private readonly record struct ProtectedTimingState(
        BeatPosition Beat,
        double? Bpm,
        double Delay,
        TimingEffectKind? Effect,
        double EffectValue,
        string EffectValueText,
        string EffectFieldName)
    {
        public static ProtectedTimingState FromTiming(TimingEvent timing) => new(
            timing.Beat, timing.Bpm, timing.Delay, timing.Effect, timing.EffectValue,
            timing.EffectValueText, timing.EffectFieldName);
    }
}

public enum ScopedHistoryResult
{
    None,
    Applied,
    Conflict,
    PermissionDenied
}

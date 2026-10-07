using LumaChart.Core.Models;

namespace LumaChart.Core.Editing;

/// <summary>
/// How a concurrent delete and edit of the same Note or Timing event resolve when
/// local intent is reapplied onto a newer server document.
/// </summary>
public enum CollaborationDeleteConflictPolicy
{
    /// <summary>
    /// A delete on either side wins. A Note/Timing another member deleted stays
    /// deleted even if this client edited it meanwhile, and a local delete removes an
    /// entity another member edited. The discarded edit is never resurrected by a
    /// later upsert because it is no longer part of the local document.
    /// </summary>
    DeleteWins,

    /// <summary>
    /// Local intent wins (behavior before the delete-wins decision): a local edit
    /// re-creates an entity the remote side deleted, and a local delete still removes
    /// an entity the remote side edited.
    /// </summary>
    LocalWins
}

/// <summary>
/// Reapplies only edits made after an acknowledged request document onto a newer
/// canonical server snapshot. This is used when the acknowledgement was lost and
/// operation payloads were compacted before the exact OperationId retry arrived.
/// Existing entities merge per field (three-way: request = base, server = remote,
/// current = local), so a concurrent edit of a different field survives.
/// </summary>
public static class CollaborationDocumentMergePolicy
{
    /// <summary>
    /// The single switch for delete-versus-edit conflicts. The product decision is
    /// that deletes win in both directions; change only this value to revisit it.
    /// </summary>
    public const CollaborationDeleteConflictPolicy DeleteConflictPolicy =
        CollaborationDeleteConflictPolicy.DeleteWins;

    public static ChartDocument ReapplyChangesAfterAcknowledgedRequest(
        ChartDocument serverCanonical,
        ChartDocument acknowledgedRequest,
        ChartDocument currentLocal) =>
        ReapplyChangesAfterAcknowledgedRequest(
            serverCanonical, acknowledgedRequest, currentLocal, DeleteConflictPolicy);

    public static ChartDocument ReapplyChangesAfterAcknowledgedRequest(
        ChartDocument serverCanonical,
        ChartDocument acknowledgedRequest,
        ChartDocument currentLocal,
        CollaborationDeleteConflictPolicy deleteConflictPolicy)
    {
        ArgumentNullException.ThrowIfNull(serverCanonical);
        ArgumentNullException.ThrowIfNull(acknowledgedRequest);
        ArgumentNullException.ThrowIfNull(currentLocal);

        var merged = serverCanonical.Clone();
        ReapplyMetadata(
            merged.Metadata, acknowledgedRequest.Metadata, currentLocal.Metadata);
        if (acknowledgedRequest.KeyCount != currentLocal.KeyCount)
            merged.KeyCount = currentLocal.KeyCount;

        ReapplyNotes(merged, acknowledgedRequest, currentLocal, deleteConflictPolicy);
        ReapplyTiming(merged, acknowledgedRequest, currentLocal, deleteConflictPolicy);
        merged.Normalize();
        return merged;
    }

    private static void ReapplyMetadata(
        ChartMetadata target,
        ChartMetadata acknowledged,
        ChartMetadata current)
    {
        if (current.Title != acknowledged.Title) target.Title = current.Title;
        if (current.TitleOriginal != acknowledged.TitleOriginal) target.TitleOriginal = current.TitleOriginal;
        if (current.Artist != acknowledged.Artist) target.Artist = current.Artist;
        if (current.ArtistOriginal != acknowledged.ArtistOriginal) target.ArtistOriginal = current.ArtistOriginal;
        if (current.Creator != acknowledged.Creator) target.Creator = current.Creator;
        if (current.Version != acknowledged.Version) target.Version = current.Version;
        if (current.AudioFile != acknowledged.AudioFile) target.AudioFile = current.AudioFile;
        if (current.CoverFile != acknowledged.CoverFile) target.CoverFile = current.CoverFile;
        if (current.BackgroundFile != acknowledged.BackgroundFile) target.BackgroundFile = current.BackgroundFile;
        if (current.PreviewTime != acknowledged.PreviewTime) target.PreviewTime = current.PreviewTime;
        if (!current.Bpm.Equals(acknowledged.Bpm)) target.Bpm = current.Bpm;
        if (!current.OverallDifficulty.Equals(acknowledged.OverallDifficulty))
            target.OverallDifficulty = current.OverallDifficulty;
        if (!current.HpDrainRate.Equals(acknowledged.HpDrainRate))
            target.HpDrainRate = current.HpDrainRate;
    }

    private static void ReapplyNotes(
        ChartDocument merged,
        ChartDocument acknowledgedRequest,
        ChartDocument currentLocal,
        CollaborationDeleteConflictPolicy deleteConflictPolicy)
    {
        var acknowledged = acknowledgedRequest.Notes.ToDictionary(note => note.Id);
        var current = currentLocal.Notes.ToDictionary(note => note.Id);
        var target = merged.Notes.ToDictionary(note => note.Id);
        // A local delete removes the entity even when the remote side edited it.
        // Both policies agree on this direction.
        foreach (var deletedId in acknowledged.Keys.Where(id => !current.ContainsKey(id)))
        {
            if (!target.Remove(deletedId, out var removed)) continue;
            merged.Notes.Remove(removed);
        }
        foreach (var pair in current)
        {
            var hasBase = acknowledged.TryGetValue(pair.Key, out var old);
            if (hasBase && NoteEquals(old!, pair.Value)) continue;
            if (target.TryGetValue(pair.Key, out var existing))
            {
                if (hasBase) MergeNoteFields(old!, pair.Value, existing);
                else CopyNote(pair.Value, existing);
                continue;
            }

            // The base had this Note and the server no longer does: another member
            // deleted it while this client edited it.
            if (hasBase && deleteConflictPolicy == CollaborationDeleteConflictPolicy.DeleteWins)
                continue;
            var added = pair.Value.Clone();
            merged.Notes.Add(added);
            target[pair.Key] = added;
        }
    }

    private static void ReapplyTiming(
        ChartDocument merged,
        ChartDocument acknowledgedRequest,
        ChartDocument currentLocal,
        CollaborationDeleteConflictPolicy deleteConflictPolicy)
    {
        var acknowledged = acknowledgedRequest.Timing.ToDictionary(item => item.Id);
        var current = currentLocal.Timing.ToDictionary(item => item.Id);
        var target = merged.Timing.ToDictionary(item => item.Id);
        foreach (var deletedId in acknowledged.Keys.Where(id => !current.ContainsKey(id)))
        {
            if (!target.Remove(deletedId, out var removed)) continue;
            merged.Timing.Remove(removed);
        }
        foreach (var pair in current)
        {
            var hasBase = acknowledged.TryGetValue(pair.Key, out var old);
            if (hasBase && TimingEquals(old!, pair.Value)) continue;
            if (target.TryGetValue(pair.Key, out var existing))
            {
                if (hasBase) MergeTimingFields(old!, pair.Value, existing);
                else CopyTiming(pair.Value, existing);
                continue;
            }

            if (hasBase && deleteConflictPolicy == CollaborationDeleteConflictPolicy.DeleteWins)
                continue;
            var added = pair.Value.Clone();
            merged.Timing.Add(added);
            target[pair.Key] = added;
        }
    }

    /// <summary>
    /// Copies only the fields this client changed relative to the base. Fields that
    /// the server validates together move as one group, so a local head move and a
    /// remote tail move can never combine into an inverted LN, and a draft/lane
    /// conversion or BPM/effect replacement is never half-applied.
    /// </summary>
    private static void MergeNoteFields(ChartNote baseNote, ChartNote local, ChartNote target)
    {
        if (local.Beat != baseNote.Beat || local.EndBeat != baseNote.EndBeat)
        {
            target.Beat = local.Beat;
            target.EndBeat = local.EndBeat;
        }
        if (local.Lane != baseNote.Lane ||
            local.IsDraft != baseNote.IsDraft ||
            local.DraftColumn != baseNote.DraftColumn)
        {
            target.Lane = local.Lane;
            target.IsDraft = local.IsDraft;
            target.DraftColumn = local.DraftColumn;
        }
        if (local.Sound != baseNote.Sound) target.Sound = local.Sound;
        if (local.Volume != baseNote.Volume) target.Volume = local.Volume;
        if (local.Group != baseNote.Group) target.Group = local.Group;
        if (local.UnknownData?.ToJsonString() != baseNote.UnknownData?.ToJsonString())
            target.UnknownData = local.UnknownData?.DeepClone().AsObject();
        if (local.OnlineCreatorUserId != baseNote.OnlineCreatorUserId)
            target.OnlineCreatorUserId = local.OnlineCreatorUserId;
    }

    private static void MergeTimingFields(TimingEvent baseItem, TimingEvent local, TimingEvent target)
    {
        if (local.Beat != baseItem.Beat) target.Beat = local.Beat;
        if (!local.Delay.Equals(baseItem.Delay)) target.Delay = local.Delay;
        // A Timing event must keep a BPM or an effect, and a standard effect may not
        // carry a custom field name; these values therefore change as one unit.
        if (local.Bpm != baseItem.Bpm ||
            local.Effect != baseItem.Effect ||
            !local.EffectValue.Equals(baseItem.EffectValue) ||
            local.EffectValueText != baseItem.EffectValueText ||
            local.EffectFieldName != baseItem.EffectFieldName)
        {
            target.Bpm = local.Bpm;
            target.Effect = local.Effect;
            target.EffectValue = local.EffectValue;
            target.EffectValueText = local.EffectValueText;
            target.EffectFieldName = local.EffectFieldName;
        }
        if (local.UnknownData?.ToJsonString() != baseItem.UnknownData?.ToJsonString())
            target.UnknownData = local.UnknownData?.DeepClone().AsObject();
        if (local.OnlineCreatorUserId != baseItem.OnlineCreatorUserId)
            target.OnlineCreatorUserId = local.OnlineCreatorUserId;
    }

    private static bool NoteEquals(ChartNote left, ChartNote right) =>
        left.Id == right.Id && left.Lane == right.Lane && left.Beat == right.Beat &&
        left.EndBeat == right.EndBeat && left.Sound == right.Sound && left.Volume == right.Volume &&
        left.Group == right.Group && left.IsDraft == right.IsDraft &&
        left.DraftColumn == right.DraftColumn &&
        left.UnknownData?.ToJsonString() == right.UnknownData?.ToJsonString() &&
        left.OnlineCreatorUserId == right.OnlineCreatorUserId;

    private static bool TimingEquals(TimingEvent left, TimingEvent right) =>
        left.Id == right.Id && left.Beat == right.Beat && left.Bpm == right.Bpm &&
        left.Delay.Equals(right.Delay) && left.Effect == right.Effect &&
        left.EffectValue.Equals(right.EffectValue) &&
        left.EffectValueText == right.EffectValueText &&
        left.EffectFieldName == right.EffectFieldName &&
        left.UnknownData?.ToJsonString() == right.UnknownData?.ToJsonString() &&
        left.OnlineCreatorUserId == right.OnlineCreatorUserId;

    private static void CopyNote(ChartNote source, ChartNote target)
    {
        target.Lane = source.Lane;
        target.Beat = source.Beat;
        target.EndBeat = source.EndBeat;
        target.Sound = source.Sound;
        target.Volume = source.Volume;
        target.Group = source.Group;
        target.IsDraft = source.IsDraft;
        target.DraftColumn = source.DraftColumn;
        target.UnknownData = source.UnknownData?.DeepClone().AsObject();
        target.OnlineCreatorUserId = source.OnlineCreatorUserId;
    }

    private static void CopyTiming(TimingEvent source, TimingEvent target)
    {
        target.Beat = source.Beat;
        target.Bpm = source.Bpm;
        target.Delay = source.Delay;
        target.Effect = source.Effect;
        target.EffectValue = source.EffectValue;
        target.EffectValueText = source.EffectValueText;
        target.EffectFieldName = source.EffectFieldName;
        target.UnknownData = source.UnknownData?.DeepClone().AsObject();
        target.OnlineCreatorUserId = source.OnlineCreatorUserId;
    }
}

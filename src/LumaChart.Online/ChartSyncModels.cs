using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LumaChart.Core.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Online;

public sealed record OnlineSyncBeat(int Bar, int Numerator, int Denominator);

public sealed record OnlineChartMetadata(
    string Title,
    string TitleOriginal,
    string Artist,
    string ArtistOriginal,
    string Creator,
    string Version,
    string AudioFile,
    string CoverFile,
    string BackgroundFile,
    int PreviewTime,
    double OverallDifficulty,
    double HpDrainRate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Bpm = null);

public sealed record OnlineChartNote(
    Guid Id,
    int Lane,
    OnlineSyncBeat Beat,
    OnlineSyncBeat? EndBeat,
    string? Sound,
    int Volume,
    int Group,
    bool IsDraft,
    int DraftColumn,
    string? UnknownJson,
    long CreatorUserId);

public sealed record OnlineChartTiming(
    Guid Id,
    OnlineSyncBeat Beat,
    double? Bpm,
    double Delay,
    int? Effect,
    double EffectValue,
    string EffectValueText,
    string EffectFieldName,
    string? UnknownJson,
    long CreatorUserId);

public sealed record OnlineChartLock(
    Guid Id,
    OnlineSyncBeat StartBeat,
    OnlineSyncBeat EndBeat,
    int StartLane,
    int EndLane,
    long OwnerUserId);

public sealed record OnlineChartLockRange(
    OnlineSyncBeat StartBeat,
    OnlineSyncBeat EndBeat);

public sealed record InitializeOnlineChartRequest(
    int KeyCount,
    IReadOnlyList<OnlineChartNote> Notes,
    IReadOnlyList<OnlineChartTiming> Timing,
    OnlineChartMetadata? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<OnlineChartLockRange>? InitialLocks = null);

public sealed record OnlineChartOperationBatch(
    long BaseRevision,
    IReadOnlyList<OnlineChartNote> UpsertNotes,
    IReadOnlyList<Guid> DeleteNoteIds,
    IReadOnlyList<OnlineChartTiming> UpsertTiming,
    IReadOnlyList<Guid> DeleteTimingIds,
    Guid OperationId = default,
    OnlineChartMetadata? Metadata = null,
    int? KeyCount = null)
{
    [JsonIgnore]
    public bool IsEmpty => UpsertNotes.Count == 0 && DeleteNoteIds.Count == 0 &&
                           UpsertTiming.Count == 0 && DeleteTimingIds.Count == 0 &&
                           Metadata == null && KeyCount == null;
}

public sealed record OnlineChartSnapshot(
    long Revision,
    int KeyCount,
    IReadOnlyList<OnlineChartNote> Notes,
    IReadOnlyList<OnlineChartTiming> Timing,
    IReadOnlyList<OnlineChartLock> Locks,
    OnlineChartMetadata? Metadata = null);

public sealed record AppliedOnlineChartOperation(
    long Revision,
    long ActorUserId,
    OnlineChartOperationBatch Batch,
    DateTimeOffset CreatedAt);

public sealed record OnlineChartDelta(
    long CurrentRevision,
    IReadOnlyList<AppliedOnlineChartOperation> Operations,
    IReadOnlyList<OnlineChartLock> Locks,
    bool HasMore,
    long? AuthoritativeBaselineRevision = null,
    bool RequiresFullSnapshot = false);

public sealed record OnlineChartApplyResult(
    OnlineChartSnapshot Snapshot,
    AppliedOnlineChartOperation? AppliedOperation = null);

public sealed record OnlineChartSavedSnapshot(
    Guid Id,
    string Name,
    long SourceRevision,
    long CreatorUserId,
    string CreatorDisplayName,
    int NoteCount,
    int TimingCount,
    bool IsAutomatic,
    DateTimeOffset CreatedAt);

public sealed record OnlineChartRollbackResult(
    OnlineChartSnapshot Snapshot,
    OnlineChartSavedSnapshot RestoredSnapshot,
    Guid SafetySnapshotId);

public sealed record OnlineToggleChartLockRequest(
    OnlineSyncBeat StartBeat,
    OnlineSyncBeat EndBeat);

public sealed record OnlineToggleChartLockSelectionRequest(
    IReadOnlyList<OnlineChartLockRange> Ranges);

public sealed record OnlineToggleChartLockSelectionResult(
    bool IsUnlock,
    IReadOnlyList<OnlineChartLock> Locks);

public sealed record OnlineCreateChartSnapshotRequest(string? Name);

public sealed record OnlineRollbackChartSnapshotRequest(Guid OperationId);

public static class OnlineChartSyncCodec
{
    public static InitializeOnlineChartRequest CreateInitializeRequest(ChartDocument document) =>
        CreateInitializeRequest(document, null);

    public static InitializeOnlineChartRequest CreateInitializeRequest(
        ChartDocument document,
        IEnumerable<OnlineChartLockRange>? initialLocks)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new InitializeOnlineChartRequest(
            document.KeyCount,
            document.Notes.Select(ToOnline).ToArray(),
            document.Timing.Select(ToOnline).ToArray(),
            ToOnline(document.Metadata),
            initialLocks?.Select(item => item ??
                    throw new ArgumentException("Initial lock ranges cannot contain null values.", nameof(initialLocks)))
                .ToArray());
    }

    public static OnlineChartLockRange CreateLockRange(BeatPosition startBeat, BeatPosition endBeat) =>
        new(ToOnline(startBeat), ToOnline(endBeat));

    public static OnlineChartOperationBatch CreateBatch(
        long baseRevision,
        ChartDocument baseline,
        ChartDocument current) =>
        CreateBatch(baseRevision, baseline, current, Guid.NewGuid());

    /// <summary>
    /// Creates a batch with a caller-supplied operation identifier. Pure state
    /// reducers use this overload so the identifier source remains injectable and
    /// no random value is produced while reducing state.
    /// </summary>
    public static OnlineChartOperationBatch CreateBatch(
        long baseRevision,
        ChartDocument baseline,
        ChartDocument current,
        Guid operationId)
    {
        var oldNotes = baseline.Notes.ToDictionary(note => note.Id, ToOnline);
        var newNotes = current.Notes.ToDictionary(note => note.Id, ToOnline);
        var upsertNotes = newNotes
            .Where(pair => !oldNotes.TryGetValue(pair.Key, out var old) || old != pair.Value)
            .Select(pair => pair.Value).ToArray();
        var deleteNoteIds = oldNotes.Keys.Where(id => !newNotes.ContainsKey(id)).ToArray();

        var oldTiming = baseline.Timing.ToDictionary(item => item.Id, ToOnline);
        var newTiming = current.Timing.ToDictionary(item => item.Id, ToOnline);
        var upsertTiming = newTiming
            .Where(pair => !oldTiming.TryGetValue(pair.Key, out var old) || old != pair.Value)
            .Select(pair => pair.Value).ToArray();
        var deleteTimingIds = oldTiming.Keys.Where(id => !newTiming.ContainsKey(id)).ToArray();
        var metadata = ToOnline(baseline.Metadata) == ToOnline(current.Metadata)
            ? null
            : ToOnline(current.Metadata);
        int? keyCount = baseline.KeyCount == current.KeyCount ? null : current.KeyCount;
        return new OnlineChartOperationBatch(
            Math.Max(0, baseRevision), upsertNotes, deleteNoteIds, upsertTiming, deleteTimingIds,
            operationId, metadata, keyCount);
    }

    public static ChartDocument CreateCanonicalDocument(ChartDocument template, OnlineChartSnapshot snapshot)
    {
        var result = template.Clone();
        result.KeyCount = snapshot.KeyCount;
        result.Notes = snapshot.Notes.Select(FromOnline).ToList();
        result.Timing = snapshot.Timing.Select(FromOnline).ToList();
        if (snapshot.Metadata != null)
            result.Metadata = FromOnline(snapshot.Metadata, FirstValidBpm(result.Timing));
        result.Normalize();
        return result;
    }

    public static ChartDocument ApplyOperations(ChartDocument baseline, IEnumerable<AppliedOnlineChartOperation> operations)
    {
        var result = baseline.Clone();
        foreach (var operation in operations.OrderBy(item => item.Revision)) ApplyBatch(result, operation.Batch);
        result.Normalize();
        return result;
    }

    public static ChartDocument MergePendingLocalChanges(
        ChartDocument serverCanonical,
        ChartDocument requestDocument,
        ChartDocument currentDocument) =>
        CollaborationDocumentMergePolicy.ReapplyChangesAfterAcknowledgedRequest(
            serverCanonical, requestDocument, currentDocument);

    public static IReadOnlyList<OwnerProtectedRegion> ToOwnerRegions(IEnumerable<OnlineChartLock> locks) =>
        locks.Select(ToOwnerRegion).ToArray();

    private static OwnerProtectedRegion ToOwnerRegion(OnlineChartLock item)
    {
        var start = FromOnline(item.StartBeat);
        var end = FromOnline(item.EndBeat);
        if (end.CompareTo(start) < 0) (start, end) = (end, start);

        // Current servers use full-width lane sentinels and exact, end-exclusive
        // rational Beat ranges. Only old lane rectangles need compatibility
        // expansion to whole measures; expanding a current full-width lock would
        // protect beats that the owner never selected.
        var isCurrentFormat = item.StartLane <= -64 && item.EndLane >= 81 &&
                              end.CompareTo(start) > 0;
        if (!isCurrentFormat)
        {
            start = new BeatPosition(start.Bar / 4 * 4, 0, 1);
            end = new BeatPosition(checked((end.Bar / 4 + 1) * 4), 0, 1);
        }
        return new OwnerProtectedRegion(item.Id, start, end);
    }

    private static void ApplyBatch(ChartDocument document, OnlineChartOperationBatch batch)
    {
        if (batch.Metadata != null) document.Metadata = FromOnline(batch.Metadata, document.Metadata.Bpm);
        if (batch.KeyCount.HasValue) document.KeyCount = Math.Clamp(batch.KeyCount.Value, 1, 18);
        var deleteNotes = batch.DeleteNoteIds.ToHashSet();
        document.Notes.RemoveAll(note => deleteNotes.Contains(note.Id));
        var notes = document.Notes.ToDictionary(note => note.Id);
        foreach (var value in batch.UpsertNotes)
        {
            var note = FromOnline(value);
            if (notes.TryGetValue(note.Id, out var existing)) CopyNote(note, existing);
            else
            {
                document.Notes.Add(note);
                notes[note.Id] = note;
            }
        }

        var deleteTiming = batch.DeleteTimingIds.ToHashSet();
        document.Timing.RemoveAll(item => deleteTiming.Contains(item.Id));
        var timing = document.Timing.ToDictionary(item => item.Id);
        foreach (var value in batch.UpsertTiming)
        {
            var item = FromOnline(value);
            if (timing.TryGetValue(item.Id, out var existing)) CopyTiming(item, existing);
            else
            {
                document.Timing.Add(item);
                timing[item.Id] = item;
            }
        }
    }

    private static OnlineChartNote ToOnline(ChartNote note) => new(
        note.Id,
        note.Lane,
        ToOnline(note.Beat),
        note.EndBeat.HasValue ? ToOnline(note.EndBeat.Value) : null,
        note.Sound,
        note.Volume,
        note.Group,
        note.IsDraft,
        note.DraftColumn,
        note.UnknownData?.ToJsonString(),
        note.OnlineCreatorUserId);

    private static OnlineChartMetadata ToOnline(ChartMetadata metadata) => new(
        metadata.Title, metadata.TitleOriginal, metadata.Artist, metadata.ArtistOriginal,
        metadata.Creator, metadata.Version, metadata.AudioFile, metadata.CoverFile,
        metadata.BackgroundFile, metadata.PreviewTime, metadata.OverallDifficulty, metadata.HpDrainRate,
        metadata.Bpm);

    private static ChartMetadata FromOnline(OnlineChartMetadata metadata, double fallbackBpm) => new()
    {
        Title = metadata.Title,
        TitleOriginal = metadata.TitleOriginal,
        Artist = metadata.Artist,
        ArtistOriginal = metadata.ArtistOriginal,
        Creator = metadata.Creator,
        Version = metadata.Version,
        AudioFile = metadata.AudioFile,
        CoverFile = metadata.CoverFile,
        BackgroundFile = metadata.BackgroundFile,
        PreviewTime = metadata.PreviewTime,
        Bpm = ValidBpm(metadata.Bpm) ?? ValidBpm(fallbackBpm) ?? 120,
        OverallDifficulty = metadata.OverallDifficulty,
        HpDrainRate = metadata.HpDrainRate
    };

    private static double FirstValidBpm(IEnumerable<TimingEvent> timing) =>
        timing.Where(item => item.Bpm.HasValue)
            .OrderBy(item => item.Beat)
            .Select(item => ValidBpm(item.Bpm))
            .FirstOrDefault(value => value.HasValue) ?? 120;

    private static double? ValidBpm(double? value) =>
        value is { } bpm && double.IsFinite(bpm) && bpm is > 0 and <= 10000 ? bpm : null;

    private static OnlineChartTiming ToOnline(TimingEvent item) => new(
        item.Id,
        ToOnline(item.Beat),
        item.Bpm,
        item.Delay,
        item.Effect.HasValue ? (int)item.Effect.Value : null,
        item.EffectValue,
        item.EffectValueText,
        item.EffectFieldName,
        item.UnknownData?.ToJsonString(),
        item.OnlineCreatorUserId);

    private static ChartNote FromOnline(OnlineChartNote note) => new()
    {
        Id = note.Id,
        Lane = note.Lane,
        Beat = FromOnline(note.Beat),
        EndBeat = note.EndBeat == null ? null : FromOnline(note.EndBeat),
        Sound = note.Sound,
        Volume = note.Volume,
        Group = note.Group,
        IsDraft = note.IsDraft,
        DraftColumn = note.DraftColumn,
        UnknownData = ParseObject(note.UnknownJson)
        , OnlineCreatorUserId = note.CreatorUserId
    };

    private static TimingEvent FromOnline(OnlineChartTiming item) => new()
    {
        Id = item.Id,
        Beat = FromOnline(item.Beat),
        Bpm = item.Bpm,
        Delay = item.Delay,
        Effect = item.Effect.HasValue ? (TimingEffectKind)item.Effect.Value : null,
        EffectValue = item.EffectValue,
        EffectValueText = item.EffectValueText,
        EffectFieldName = item.EffectFieldName,
        UnknownData = ParseObject(item.UnknownJson)
        , OnlineCreatorUserId = item.CreatorUserId
    };

    private static OnlineSyncBeat ToOnline(BeatPosition beat) => new(beat.Bar, beat.Numerator, beat.Denominator);
    private static BeatPosition FromOnline(OnlineSyncBeat beat) =>
        BeatPosition.Preserve(beat.Bar, beat.Numerator, beat.Denominator);

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json)?.AsObject(); }
        catch { return null; }
    }

    private static void CopyNote(ChartNote source, ChartNote destination)
    {
        destination.Lane = source.Lane;
        destination.Beat = source.Beat;
        destination.EndBeat = source.EndBeat;
        destination.Sound = source.Sound;
        destination.Volume = source.Volume;
        destination.Group = source.Group;
        destination.IsDraft = source.IsDraft;
        destination.DraftColumn = source.DraftColumn;
        destination.UnknownData = source.UnknownData;
        destination.OnlineCreatorUserId = source.OnlineCreatorUserId;
    }

    private static void CopyTiming(TimingEvent source, TimingEvent destination)
    {
        destination.Beat = source.Beat;
        destination.Bpm = source.Bpm;
        destination.Delay = source.Delay;
        destination.Effect = source.Effect;
        destination.EffectValue = source.EffectValue;
        destination.EffectValueText = source.EffectValueText;
        destination.EffectFieldName = source.EffectFieldName;
        destination.UnknownData = source.UnknownData;
        destination.OnlineCreatorUserId = source.OnlineCreatorUserId;
    }
}

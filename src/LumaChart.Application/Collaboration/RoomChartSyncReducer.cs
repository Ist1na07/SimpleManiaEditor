using System.Text.Json;
using LumaChart.Core.Editing;
using LumaChart.Core.Models;
using LumaChart.Online;

namespace LumaChart.Application.Collaboration;

/// <summary>
/// Pure room-chart synchronization reducer. It performs no I/O, scheduling,
/// random-ID generation, clock reads, or platform dispatch.
/// </summary>
public static class RoomChartSyncReducer
{
    /// <summary>
    /// Server batch limit (RoomChartSyncStore.ValidateBatch): upserts + deletes, plus
    /// one each for metadata and key count. A larger batch is refused as a whole.
    /// </summary>
    public const int MaximumBatchItems = 20_000;

    /// <summary>
    /// The server accepts at most 16 MiB of JSON per chart-operation request (413
    /// otherwise); one MiB is kept as headroom for framing and serializer drift.
    /// </summary>
    public const long MaximumBatchRequestBytes = 15L * 1024 * 1024;

    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reports whether preparing the current local document needs a fresh operation
    /// identifier. Existing pending writes already own their stable identifier and a
    /// read-only pull must not consume a caller's deterministic ID source.
    /// </summary>
    public static bool RequiresNewOperationId(
        RoomChartSyncState state,
        ChartDocument currentLocalDocument)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(currentLocalDocument);
        if (state.PendingWrite != null) return false;
        return !OnlineChartSyncCodec.CreateBatch(
            state.Revision,
            state.CloneCanonicalDocument(),
            currentLocalDocument,
            Guid.Empty).IsEmpty;
    }

    /// <summary>
    /// Prepares a pull or write. When a write is needed, the returned State already
    /// contains the frozen batch and supplied OperationId and must be installed by
    /// the caller before starting HTTP.
    /// </summary>
    public static RoomChartAttemptPreparation PrepareAttempt(
        RoomChartSyncState state,
        ChartDocument currentLocalDocument,
        long currentChangeSerial,
        Guid newOperationId)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(currentLocalDocument);

        if (state.PendingWrite is { } pending)
        {
            var batch = pending.CommitConfirmed
                ? EmptyBatch(state.Revision)
                : pending.Batch;
            return new RoomChartAttemptPreparation(
                state,
                new PreparedRoomChartAttempt(
                    state.Generation,
                    state.Revision,
                    pending.RequestChangeSerial,
                    pending.CloneRequestDocument(),
                    batch,
                    pending,
                    pending.OmitsLocalChanges));
        }

        var requestDocument = currentLocalDocument.Clone();
        var canonical = state.CloneCanonicalDocument();
        var computed = OnlineChartSyncCodec.CreateBatch(
            state.Revision,
            canonical,
            requestDocument,
            newOperationId);
        if (computed.IsEmpty)
        {
            var pull = EmptyBatch(state.Revision);
            return new RoomChartAttemptPreparation(
                state,
                new PreparedRoomChartAttempt(
                    state.Generation,
                    state.Revision,
                    currentChangeSerial,
                    requestDocument,
                    pull,
                    null));
        }

        if (newOperationId == Guid.Empty)
            throw new ArgumentException(
                "A non-empty room-chart write requires a caller-supplied OperationId.",
                nameof(newOperationId));

        // The server refuses an over-limit batch as a whole, which used to discard
        // every edit accumulated while offline or throttled. Send the first part
        // that fits; the rest stays local intent for the following write.
        var limited = LimitBatch(computed, canonical.KeyCount, out var omitsLocalChanges);
        if (omitsLocalChanges)
        {
            requestDocument = OnlineChartSyncCodec.ApplyOperations(
                canonical,
                [new AppliedOnlineChartOperation(state.Revision, 0, limited, default)]);
        }

        var write = new PendingRoomChartWrite(
            limited with { OperationId = newOperationId },
            requestDocument,
            currentChangeSerial,
            omitsLocalChanges: omitsLocalChanges);
        var installed = state.With(pendingWrite: write);
        var attempt = new PreparedRoomChartAttempt(
            installed.Generation,
            installed.Revision,
            write.RequestChangeSerial,
            write.CloneRequestDocument(),
            write.Batch,
            write,
            write.OmitsLocalChanges);
        return new RoomChartAttemptPreparation(installed, attempt);
    }

    /// <summary>
    /// Prepares a read-only pull that keeps the installed pending write. Used while
    /// the server's write budget is exhausted: remote changes keep arriving and are
    /// merged over the pending intent, and the frozen write is sent unchanged once
    /// the budget window ends.
    /// </summary>
    public static PreparedRoomChartAttempt PrepareReadOnlyAttempt(RoomChartSyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PendingWrite is not { } pending)
            throw new InvalidOperationException(
                "A read-only attempt that retains local intent requires a pending write.");
        return new PreparedRoomChartAttempt(
            state.Generation,
            state.Revision,
            pending.RequestChangeSerial,
            pending.CloneRequestDocument(),
            EmptyBatch(state.Revision),
            pending,
            pending.OmitsLocalChanges);
    }

    /// <summary>
    /// Maps a deterministic write rejection to the local changes it refused. Codes
    /// that do not identify them return <see cref="RoomChartRejectedChangeScope.None"/>.
    /// </summary>
    public static RoomChartRejectedChangeScope ClassifyRejectedChanges(string? errorCode) =>
        errorCode?.Trim().ToLowerInvariant() switch
        {
            "selection_locked" => RoomChartRejectedChangeScope.LockedRegions,
            "note_permission_required" => RoomChartRejectedChangeScope.Notes,
            "timing_permission_required" => RoomChartRejectedChangeScope.Timing,
            "metadata_permission_required" => RoomChartRejectedChangeScope.Metadata,
            _ => RoomChartRejectedChangeScope.None
        };

    public static RoomChartDeltaReduction ReduceDelta(
        RoomChartSyncState state,
        PreparedRoomChartAttempt attempt,
        OnlineChartDelta delta,
        ChartDocument currentLocalDocument,
        long currentChangeSerial)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(currentLocalDocument);

        if (attempt.Generation != state.Generation)
            return RoomChartDeltaReduction.Stale(state);

        var observedServerRevision = delta.Operations.Count == 0
            ? delta.CurrentRevision
            : Math.Max(delta.CurrentRevision, delta.Operations.Max(operation => operation.Revision));

        // A rollback/direct recovery can overtake an in-flight request. Generation
        // is the primary guard; this revision guard also rejects a response that was
        // prepared against an older installed state within the same binding.
        if (state.Revision > attempt.RequestedRevision &&
            observedServerRevision < state.Revision)
            return RoomChartDeltaReduction.Stale(state);

        if (CollaborationRevisionPolicy.RequiresFullSnapshot(
                state.Revision, observedServerRevision))
        {
            return NeedSnapshot(
                state,
                attempt,
                RoomChartFullSnapshotReason.RevisionRegression,
                RoomChartApplyMode.OrdinaryRemoteMerge,
                acknowledgePendingWrite: false,
                rebasePendingWrite: state.PendingWrite != null);
        }

        var crossesAuthoritativeBaseline = CollaborationRevisionPolicy.RequiresAuthoritativeReset(
            state.Revision, delta.AuthoritativeBaselineRevision);
        var successfulWriteState = attempt.IsWrite && PendingMatches(state, attempt)
            ? state.With(pendingWrite: state.PendingWrite!.ConfirmCommit())
            : state;

        if (delta.RequiresFullSnapshot)
        {
            var mode = crossesAuthoritativeBaseline
                ? RoomChartApplyMode.AuthoritativeReset
                : RoomChartApplyMode.OrdinaryRemoteMerge;
            return NeedSnapshot(
                successfulWriteState,
                attempt,
                RoomChartFullSnapshotReason.ServerCompaction,
                mode,
                acknowledgePendingWrite: !crossesAuthoritativeBaseline &&
                    PendingMatches(successfulWriteState, attempt) &&
                    successfulWriteState.PendingWrite!.CommitConfirmed,
                rebasePendingWrite: false);
        }

        if (!CollaborationRevisionPolicy.IsAuthoritativeBaselineMarkerValid(
                delta.AuthoritativeBaselineRevision,
                delta.Operations.Select(operation => operation.Revision)))
        {
            var mode = crossesAuthoritativeBaseline
                ? RoomChartApplyMode.AuthoritativeReset
                : RoomChartApplyMode.OrdinaryRemoteMerge;
            return NeedSnapshot(
                successfulWriteState,
                attempt,
                RoomChartFullSnapshotReason.InvalidAuthoritativeBaselineMarker,
                mode,
                acknowledgePendingWrite: !crossesAuthoritativeBaseline &&
                    PendingMatches(successfulWriteState, attempt) &&
                    successfulWriteState.PendingWrite!.CommitConfirmed,
                rebasePendingWrite: false);
        }

        var localRevisionBeforeDelta = state.Revision;
        var applicableOperations = delta.Operations
            .Where(operation => operation.Revision > localRevisionBeforeDelta)
            .OrderBy(operation => operation.Revision)
            .ToArray();
        var authoritativeReset = CollaborationRevisionPolicy.RequiresAuthoritativeReset(
            localRevisionBeforeDelta, delta.AuthoritativeBaselineRevision);
        var nextRevision = applicableOperations.Length == 0
            ? Math.Max(localRevisionBeforeDelta,
                delta.Operations.Count == 0 ? delta.CurrentRevision : localRevisionBeforeDelta)
            : applicableOperations[^1].Revision;
        var serverCanonical = applicableOperations.Length == 0
            ? state.CloneCanonicalDocument()
            : OnlineChartSyncCodec.ApplyOperations(
                state.CloneCanonicalDocument(), applicableOperations);
        var pendingMatches = PendingMatches(state, attempt);
        var writeCommitConfirmed = attempt.IsWrite && pendingMatches;
        var pendingAcknowledged = pendingMatches &&
                                  delta.Operations.Any(operation =>
                                      operation.Batch.OperationId == attempt.PendingWrite!.OperationId);
        // A split write leaves local intent outside its request document even when
        // the editor serial has not moved; that intent must be merged back.
        var concurrentLocalChanges = currentChangeSerial != attempt.RequestChangeSerial ||
                                     attempt.OmitsLocalChanges;

        ChartDocument documentToApply;
        if (authoritativeReset)
        {
            documentToApply = serverCanonical.Clone();
        }
        else if (pendingAcknowledged)
        {
            documentToApply = concurrentLocalChanges
                ? OnlineChartSyncCodec.MergePendingLocalChanges(
                    serverCanonical,
                    attempt.CloneRequestDocument(),
                    currentLocalDocument.Clone())
                : serverCanonical.Clone();
        }
        else if (state.PendingWrite != null)
        {
            // The operation has not appeared in a delivered page yet. Preserve the
            // complete local intent while retaining its receipt-chasing state.
            documentToApply = OnlineChartSyncCodec.MergePendingLocalChanges(
                serverCanonical,
                state.CloneCanonicalDocument(),
                currentLocalDocument.Clone());
        }
        else if (concurrentLocalChanges)
        {
            documentToApply = OnlineChartSyncCodec.MergePendingLocalChanges(
                serverCanonical,
                attempt.CloneRequestDocument(),
                currentLocalDocument.Clone());
        }
        else
        {
            documentToApply = serverCanonical.Clone();
        }

        var previousGeneration = state.Generation;
        var nextGeneration = authoritativeReset
            ? NextGeneration(previousGeneration)
            : previousGeneration;
        var clearPending = authoritativeReset || pendingAcknowledged;
        var nextPending = !clearPending && writeCommitConfirmed
            ? state.PendingWrite?.ConfirmCommit()
            : null;
        var nextState = state.With(
            generation: nextGeneration,
            revision: nextRevision,
            canonicalDocument: serverCanonical,
            pendingWrite: nextPending,
            clearPendingWrite: clearPending);
        var remoteDocumentFieldsChanged = applicableOperations.Any(operation =>
            operation.Batch.Metadata != null || operation.Batch.KeyCount.HasValue);
        var applyDocument = authoritativeReset || applicableOperations.Length > 0;
        var plan = new RoomChartApplyPlan(
            nextState,
            authoritativeReset
                ? RoomChartApplyMode.AuthoritativeReset
                : RoomChartApplyMode.OrdinaryRemoteMerge,
            applyDocument ? documentToApply : currentLocalDocument,
            OnlineChartSyncCodec.ToOwnerRegions(delta.Locks),
            applyDocument,
            pendingAcknowledged,
            pendingWriteRebased: false,
            authoritativeReset ? false : concurrentLocalChanges,
            delta.HasMore,
            remoteDocumentFieldsChanged,
            previousGeneration,
            authoritativeReset ? null : currentChangeSerial);
        return RoomChartDeltaReduction.Applied(plan);
    }

    /// <summary>Applies a snapshot requested by <see cref="ReduceDelta"/>.</summary>
    public static RoomChartFullSnapshotReduction ApplyFullSnapshot(
        RoomChartSyncState state,
        RoomChartFullSnapshotRequest request,
        OnlineChartSnapshot snapshot,
        ChartDocument currentLocalDocument,
        long currentChangeSerial)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(currentLocalDocument);

        if (!request.Matches(state) ||
            request.Attempt.Generation != state.Generation)
            return RoomChartFullSnapshotReduction.Stale(state);

        var previousCanonical = state.CloneCanonicalDocument();
        var serverCanonical = OnlineChartSyncCodec.CreateCanonicalDocument(
            currentLocalDocument, snapshot);
        var previousGeneration = state.Generation;
        var authoritativeReset = request.Mode == RoomChartApplyMode.AuthoritativeReset;
        var pendingAcknowledged = !authoritativeReset && request.AcknowledgePendingWrite &&
                                  PendingMatches(state, request.Attempt);
        var pendingRebased = !authoritativeReset && request.RebasePendingWrite &&
                             state.PendingWrite != null;
        var concurrentLocalChanges = currentChangeSerial != request.Attempt.RequestChangeSerial ||
                                     request.Attempt.OmitsLocalChanges;

        var retriesAcceptedIntent = false;
        var documentToApply = request.ReplayMode switch
        {
            RoomChartFullSnapshotReplayMode.DiscardLocalChanges => serverCanonical.Clone(),
            RoomChartFullSnapshotReplayMode.PreservePostAttemptLocalChanges =>
                OnlineChartSyncCodec.MergePendingLocalChanges(
                    serverCanonical,
                    request.Attempt.CloneRequestDocument(),
                    currentLocalDocument.Clone()),
            RoomChartFullSnapshotReplayMode.PreserveAllLocalChanges =>
                OnlineChartSyncCodec.MergePendingLocalChanges(
                    serverCanonical,
                    previousCanonical,
                    currentLocalDocument.Clone()),
            RoomChartFullSnapshotReplayMode.PreserveAcceptedLocalChanges =>
                ReplayAcceptedLocalChanges(
                    serverCanonical,
                    previousCanonical,
                    request,
                    snapshot.Locks,
                    currentLocalDocument,
                    out retriesAcceptedIntent),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.ReplayMode,
                "Unknown room-chart full-snapshot replay mode.")
        };

        var nextGeneration = authoritativeReset
            ? NextGeneration(previousGeneration)
            : previousGeneration;
        var nextState = state.With(
            generation: nextGeneration,
            revision: snapshot.Revision,
            canonicalDocument: serverCanonical,
            clearPendingWrite: authoritativeReset || pendingAcknowledged || pendingRebased);
        var plan = new RoomChartApplyPlan(
            nextState,
            request.Mode,
            documentToApply,
            OnlineChartSyncCodec.ToOwnerRegions(snapshot.Locks),
            applyDocument: true,
            pendingAcknowledged,
            pendingRebased,
            authoritativeReset
                ? false
                : concurrentLocalChanges || pendingRebased || retriesAcceptedIntent,
            hasMore: false,
            remoteDocumentFieldsChanged: DocumentFieldsDiffer(previousCanonical, serverCanonical),
            previousGeneration,
            authoritativeReset ? null : currentChangeSerial);
        return RoomChartFullSnapshotReduction.Applied(plan);
    }

    /// <summary>
    /// Installs an owner-initiated rollback result as a new authoritative generation.
    /// The returned plan is the only reducer result that instructs a host to replace
    /// the document and clear its scoped undo/redo history directly.
    /// </summary>
    public static RoomChartRollbackReduction ApplyDirectRollback(
        RoomChartSyncState state,
        PreparedRoomChartRollbackAttempt attempt,
        OnlineChartRollbackResult rollback,
        ChartDocument currentLocalDocument)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(currentLocalDocument);

        if (attempt.Generation != state.Generation ||
            rollback.RestoredSnapshot.Id != attempt.SnapshotId ||
            rollback.Snapshot.Revision <= state.Revision)
            return RoomChartRollbackReduction.Stale(state);

        var canonical = OnlineChartSyncCodec.CreateCanonicalDocument(
            currentLocalDocument, rollback.Snapshot);
        var previousGeneration = state.Generation;
        var nextState = state.With(
            generation: NextGeneration(previousGeneration),
            revision: rollback.Snapshot.Revision,
            canonicalDocument: canonical,
            clearPendingWrite: true);
        var plan = new RoomChartApplyPlan(
            nextState,
            RoomChartApplyMode.AuthoritativeReset,
            canonical,
            OnlineChartSyncCodec.ToOwnerRegions(rollback.Snapshot.Locks),
            applyDocument: true,
            pendingWriteAcknowledged: false,
            pendingWriteRebased: false,
            hasConcurrentLocalChanges: false,
            hasMore: false,
            remoteDocumentFieldsChanged: true,
            previousGeneration);
        return new RoomChartRollbackReduction(
            plan, rollback.RestoredSnapshot, rollback.SafetySnapshotId);
    }

    public static PreparedRoomChartRollbackAttempt PrepareDirectRollback(
        RoomChartSyncState state,
        Guid snapshotId,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (snapshotId == Guid.Empty)
            throw new ArgumentException(
                "A direct rollback requires a snapshot identifier.",
                nameof(snapshotId));
        if (operationId == Guid.Empty)
            throw new ArgumentException(
                "A direct rollback requires a caller-supplied OperationId.",
                nameof(operationId));
        return new PreparedRoomChartRollbackAttempt(
            state.Generation, state.Revision, snapshotId, operationId);
    }

    /// <summary>
    /// Atomically retires a definitively rejected write and prepares the only valid
    /// full-snapshot recovery token for the resulting state. Ordinary rejection
    /// replays only edits made after the rejected request; an authoritative baseline
    /// change discards local intent when the snapshot is installed.
    /// </summary>
    public static RoomChartRejectedRecoveryPreparation PrepareRejectedAttemptRecovery(
        RoomChartSyncState state,
        PreparedRoomChartAttempt attempt,
        RoomChartApplyMode mode) =>
        PrepareRejectedAttemptRecovery(state, attempt, mode, RoomChartRejectedChangeScope.None);

    /// <summary>
    /// As above, but when the rejection identifies the refused changes
    /// (<paramref name="rejectedScope"/>) only those are dropped; every other local
    /// edit, including the rest of the rejected request, is replayed and retried.
    /// </summary>
    public static RoomChartRejectedRecoveryPreparation PrepareRejectedAttemptRecovery(
        RoomChartSyncState state,
        PreparedRoomChartAttempt attempt,
        RoomChartApplyMode mode,
        RoomChartRejectedChangeScope rejectedScope)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(attempt);
        if (mode is not RoomChartApplyMode.OrdinaryRemoteMerge and
            not RoomChartApplyMode.AuthoritativeReset)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (attempt.Generation != state.Generation ||
            !attempt.IsWrite ||
            state.PendingWrite?.CommitConfirmed != false ||
            !ReferenceEquals(state.PendingWrite, attempt.PendingWrite))
        {
            throw new InvalidOperationException(
                "Only the current unconfirmed pending write can prepare rejected-attempt recovery.");
        }

        var retiredState = state.With(clearPendingWrite: true);
        var authoritativeReset = mode == RoomChartApplyMode.AuthoritativeReset;
        var replayMode = authoritativeReset
            ? RoomChartFullSnapshotReplayMode.DiscardLocalChanges
            : rejectedScope == RoomChartRejectedChangeScope.None
                ? RoomChartFullSnapshotReplayMode.PreservePostAttemptLocalChanges
                : RoomChartFullSnapshotReplayMode.PreserveAcceptedLocalChanges;
        var request = new RoomChartFullSnapshotRequest(
            retiredState,
            authoritativeReset
                ? RoomChartFullSnapshotReason.AuthoritativeBaselineChanged
                : RoomChartFullSnapshotReason.DeterministicWriteRejection,
            mode,
            replayMode,
            attempt,
            acknowledgePendingWrite: false,
            rebasePendingWrite: false,
            authoritativeReset ? RoomChartRejectedChangeScope.None : rejectedScope);
        return new RoomChartRejectedRecoveryPreparation(retiredState, request);
    }

    /// <summary>
    /// Retires transport idempotency state after a definitive non-commit response
    /// or a permanent room-capacity block. The host document and its history are
    /// deliberately outside this state and remain untouched.
    /// </summary>
    public static RoomChartSyncState RetirePendingWrite(RoomChartSyncState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.PendingWrite == null
            ? state
            : RoomChartSyncState.Create(
                state.Revision,
                state.CloneCanonicalDocument(),
                state.Generation);
    }

    private static RoomChartDeltaReduction NeedSnapshot(
        RoomChartSyncState state,
        PreparedRoomChartAttempt attempt,
        RoomChartFullSnapshotReason reason,
        RoomChartApplyMode mode,
        bool acknowledgePendingWrite,
        bool rebasePendingWrite) =>
        RoomChartDeltaReduction.NeedsSnapshot(
            state,
            new RoomChartFullSnapshotRequest(
                state,
                reason,
                mode,
                mode == RoomChartApplyMode.AuthoritativeReset
                    ? RoomChartFullSnapshotReplayMode.DiscardLocalChanges
                    : acknowledgePendingWrite
                        ? RoomChartFullSnapshotReplayMode.PreservePostAttemptLocalChanges
                        : RoomChartFullSnapshotReplayMode.PreserveAllLocalChanges,
                attempt,
                acknowledgePendingWrite,
                rebasePendingWrite));

    private static bool PendingMatches(
        RoomChartSyncState state,
        PreparedRoomChartAttempt attempt) =>
        attempt.PendingWrite is { } attemptedPending &&
        state.PendingWrite?.OperationId == attemptedPending.OperationId;

    private static long NextGeneration(long generation) => checked(generation + 1);

    /// <summary>
    /// Returns the largest leading part of <paramref name="computed"/> that the
    /// server accepts. Metadata and a growing key count go first (new lanes must
    /// exist before notes move into them), deletes precede upserts so object limits
    /// never overflow midway, and a shrinking key count is held back until the final
    /// part because the server requires every note to fit it.
    /// </summary>
    private static OnlineChartOperationBatch LimitBatch(
        OnlineChartOperationBatch computed,
        int baselineKeyCount,
        out bool omitsLocalChanges)
    {
        omitsLocalChanges = false;
        if (FitsServerLimits(computed)) return computed;

        var keyCountGrows = computed.KeyCount is { } keyCount && keyCount >= baselineKeyCount;
        var headerItems = (computed.Metadata == null ? 0 : 1) + (keyCountGrows ? 1 : 0);
        var bodyItems = computed.DeleteNoteIds.Count + computed.DeleteTimingIds.Count +
                        computed.UpsertTiming.Count + computed.UpsertNotes.Count;
        var take = Math.Min(MaximumBatchItems, headerItems + bodyItems);
        while (true)
        {
            var part = TakeLeadingItems(computed, keyCountGrows, take);
            var isWhole = take >= headerItems + bodyItems && (computed.KeyCount == null || keyCountGrows);
            if (take <= 1 || FitsServerLimits(part))
            {
                // A shrinking key count is excluded from every part, so the batch is
                // never whole in that case even when all other items fit.
                omitsLocalChanges = !isWhole;
                return omitsLocalChanges ? part : computed;
            }
            take /= 2;
        }
    }

    private static OnlineChartOperationBatch TakeLeadingItems(
        OnlineChartOperationBatch computed,
        bool includeKeyCount,
        int count)
    {
        var remaining = count;
        var metadata = computed.Metadata != null && remaining-- > 0 ? computed.Metadata : null;
        int? keyCount = includeKeyCount && remaining-- > 0 ? computed.KeyCount : null;
        var deleteNotes = Take(computed.DeleteNoteIds, ref remaining);
        var deleteTiming = Take(computed.DeleteTimingIds, ref remaining);
        var upsertTiming = Take(computed.UpsertTiming, ref remaining);
        var upsertNotes = Take(computed.UpsertNotes, ref remaining);
        return new OnlineChartOperationBatch(
            computed.BaseRevision,
            upsertNotes,
            deleteNotes,
            upsertTiming,
            deleteTiming,
            computed.OperationId,
            metadata,
            keyCount);

        static T[] Take<T>(IReadOnlyList<T> source, ref int available)
        {
            var taken = source.Take(Math.Max(0, available)).ToArray();
            available -= taken.Length;
            return taken;
        }
    }

    private static bool FitsServerLimits(OnlineChartOperationBatch batch)
    {
        var count = batch.UpsertNotes.Count + batch.DeleteNoteIds.Count +
                    batch.UpsertTiming.Count + batch.DeleteTimingIds.Count +
                    (batch.Metadata == null ? 0 : 1) + (batch.KeyCount.HasValue ? 1 : 0);
        if (count > MaximumBatchItems) return false;
        return JsonSerializer.SerializeToUtf8Bytes(batch, WireJsonOptions).LongLength <=
               MaximumBatchRequestBytes;
    }

    /// <summary>
    /// Replays local intent over a snapshot after a rejection that named what it
    /// refused. Refused changes revert to the pre-request canonical state; the rest
    /// is merged and retried. If nothing matched the refused scope, the ordinary
    /// post-attempt replay applies so a rejected batch is never resent unchanged.
    /// </summary>
    private static ChartDocument ReplayAcceptedLocalChanges(
        ChartDocument serverCanonical,
        ChartDocument previousCanonical,
        RoomChartFullSnapshotRequest request,
        IReadOnlyList<OnlineChartLock> serverLocks,
        ChartDocument currentLocalDocument,
        out bool retriesAcceptedIntent)
    {
        var accepted = WithoutRejectedChanges(
            previousCanonical,
            serverCanonical,
            currentLocalDocument,
            request.RejectedScope,
            OnlineChartSyncCodec.ToOwnerRegions(serverLocks ?? []),
            out var reverted);
        retriesAcceptedIntent = reverted > 0;
        return retriesAcceptedIntent
            ? OnlineChartSyncCodec.MergePendingLocalChanges(
                serverCanonical, previousCanonical, accepted)
            : OnlineChartSyncCodec.MergePendingLocalChanges(
                serverCanonical, request.Attempt.CloneRequestDocument(), currentLocalDocument.Clone());
    }

    private static ChartDocument WithoutRejectedChanges(
        ChartDocument baseline,
        ChartDocument server,
        ChartDocument local,
        RoomChartRejectedChangeScope scope,
        IReadOnlyList<OwnerProtectedRegion> locks,
        out int reverted)
    {
        reverted = 0;
        var result = local.Clone();
        // The wire diff uses the same equality the write path uses.
        var changes = OnlineChartSyncCodec.CreateBatch(0, baseline, local, Guid.Empty);
        var lockedRegions = scope.HasFlag(RoomChartRejectedChangeScope.LockedRegions);

        if (scope.HasFlag(RoomChartRejectedChangeScope.Metadata) &&
            (changes.Metadata != null || changes.KeyCount.HasValue))
        {
            result.Metadata = baseline.Metadata.Clone();
            result.KeyCount = baseline.KeyCount;
            reverted++;
        }

        var baseNotes = baseline.Notes.ToDictionary(note => note.Id);
        var serverNotes = server.Notes.ToDictionary(note => note.Id);
        var localNotes = result.Notes.ToDictionary(note => note.Id);
        var rejectNotes = scope.HasFlag(RoomChartRejectedChangeScope.Notes);
        foreach (var id in changes.UpsertNotes.Select(note => note.Id).Concat(changes.DeleteNoteIds))
        {
            localNotes.TryGetValue(id, out var localNote);
            serverNotes.TryGetValue(id, out var serverNote);
            // Mirrors the server: an upsert is refused when the current server
            // entity or the new value touches a lock; a delete when the current one does.
            var refused = rejectNotes || lockedRegions &&
                          (serverNote != null && locks.Any(region => Touches(region, serverNote)) ||
                           localNote != null && locks.Any(region => Touches(region, localNote)));
            if (!refused) continue;
            if (localNote != null) result.Notes.Remove(localNote);
            if (baseNotes.TryGetValue(id, out var baseNote)) result.Notes.Add(baseNote.Clone());
            reverted++;
        }

        var baseTiming = baseline.Timing.ToDictionary(item => item.Id);
        var serverTiming = server.Timing.ToDictionary(item => item.Id);
        var localTiming = result.Timing.ToDictionary(item => item.Id);
        var rejectTiming = scope.HasFlag(RoomChartRejectedChangeScope.Timing);
        foreach (var id in changes.UpsertTiming.Select(item => item.Id).Concat(changes.DeleteTimingIds))
        {
            localTiming.TryGetValue(id, out var localItem);
            serverTiming.TryGetValue(id, out var serverItem);
            var refused = rejectTiming || lockedRegions &&
                          (serverItem != null && locks.Any(region => Touches(region, serverItem.Beat)) ||
                           localItem != null && locks.Any(region => Touches(region, localItem.Beat)));
            if (!refused) continue;
            if (localItem != null) result.Timing.Remove(localItem);
            if (baseTiming.TryGetValue(id, out var baseItem)) result.Timing.Add(baseItem.Clone());
            reverted++;
        }

        result.Normalize();
        return result;
    }

    private static bool Touches(OwnerProtectedRegion region, ChartNote note)
    {
        var start = note.Beat;
        var end = note.EndBeat ?? note.Beat;
        if (end.CompareTo(start) < 0) (start, end) = (end, start);
        // Tap notes are zero-length: inside [start, end). LNs overlap the range.
        return start.CompareTo(end) == 0
            ? Touches(region, start)
            : start.CompareTo(region.EndBeat) < 0 && end.CompareTo(region.StartBeat) > 0;
    }

    private static bool Touches(OwnerProtectedRegion region, BeatPosition beat) =>
        beat.CompareTo(region.StartBeat) >= 0 && beat.CompareTo(region.EndBeat) < 0;

    private static OnlineChartOperationBatch EmptyBatch(long revision) =>
        new(revision, [], [], [], []);

    private static bool DocumentFieldsDiffer(ChartDocument left, ChartDocument right) =>
        left.KeyCount != right.KeyCount ||
        left.Metadata.Title != right.Metadata.Title ||
        left.Metadata.TitleOriginal != right.Metadata.TitleOriginal ||
        left.Metadata.Artist != right.Metadata.Artist ||
        left.Metadata.ArtistOriginal != right.Metadata.ArtistOriginal ||
        left.Metadata.Creator != right.Metadata.Creator ||
        left.Metadata.Version != right.Metadata.Version ||
        left.Metadata.AudioFile != right.Metadata.AudioFile ||
        left.Metadata.CoverFile != right.Metadata.CoverFile ||
        left.Metadata.BackgroundFile != right.Metadata.BackgroundFile ||
        left.Metadata.PreviewTime != right.Metadata.PreviewTime ||
        !left.Metadata.Bpm.Equals(right.Metadata.Bpm) ||
        !left.Metadata.OverallDifficulty.Equals(right.Metadata.OverallDifficulty) ||
        !left.Metadata.HpDrainRate.Equals(right.Metadata.HpDrainRate);
}

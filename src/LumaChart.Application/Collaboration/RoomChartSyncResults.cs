using LumaChart.Core.Editing;
using LumaChart.Core.Models;
using LumaChart.Online;

namespace LumaChart.Application.Collaboration;

public enum RoomChartApplyMode
{
    OrdinaryRemoteMerge,
    AuthoritativeReset
}

public enum RoomChartDeltaDisposition
{
    Applied,
    RequiresFullSnapshot,
    IgnoredStaleResponse
}

public enum RoomChartFullSnapshotReason
{
    ServerCompaction,
    RevisionRegression,
    InvalidAuthoritativeBaselineMarker,
    DeterministicWriteRejection,
    AuthoritativeBaselineChanged
}

public enum RoomChartFullSnapshotReplayMode
{
    PreserveAllLocalChanges,
    PreservePostAttemptLocalChanges,
    DiscardLocalChanges,

    /// <summary>
    /// A deterministic rejection named the kind of change it refused. Replay all
    /// local intent except the changes in <see cref="RoomChartFullSnapshotRequest.RejectedScope"/>
    /// so the rest is retried. If none of the local changes falls in that scope the
    /// client's view of the rejection is wrong and the ordinary post-attempt replay
    /// applies instead, which guarantees that a rejection is never retried unchanged.
    /// </summary>
    PreserveAcceptedLocalChanges
}

/// <summary>Which local changes a deterministic write rejection refused.</summary>
[Flags]
public enum RoomChartRejectedChangeScope
{
    /// <summary>The rejection does not identify the refused changes.</summary>
    None = 0,
    Notes = 1,
    Timing = 2,
    Metadata = 4,

    /// <summary>Note/Timing changes touching an owner lock (selection_locked).</summary>
    LockedRegions = 8
}

/// <summary>
/// Pure instruction for applying a reducer result to an EditorSession or another
/// platform document host. Only AuthoritativeReset permits clearing undo/redo.
/// </summary>
public sealed class RoomChartApplyPlan
{
    private readonly ChartDocument document;
    private readonly IReadOnlyList<OwnerProtectedRegion> ownerProtectedRegions;

    internal RoomChartApplyPlan(
        RoomChartSyncState state,
        RoomChartApplyMode mode,
        ChartDocument document,
        IReadOnlyList<OwnerProtectedRegion> ownerProtectedRegions,
        bool applyDocument,
        bool pendingWriteAcknowledged,
        bool pendingWriteRebased,
        bool hasConcurrentLocalChanges,
        bool hasMore,
        bool remoteDocumentFieldsChanged,
        long previousGeneration,
        long? localChangeSerial = null)
    {
        State = state;
        Mode = mode;
        this.document = document.Clone();
        this.ownerProtectedRegions = Array.AsReadOnly(ownerProtectedRegions.ToArray());
        ApplyDocument = applyDocument;
        PendingWriteAcknowledged = pendingWriteAcknowledged;
        PendingWriteRebased = pendingWriteRebased;
        HasConcurrentLocalChanges = hasConcurrentLocalChanges;
        HasMore = hasMore;
        RemoteDocumentFieldsChanged = remoteDocumentFieldsChanged;
        PreviousGeneration = previousGeneration;
        LocalChangeSerial = localChangeSerial;
    }

    public RoomChartSyncState State { get; }

    /// <summary>
    /// The editor change serial of the local snapshot that <see cref="Document"/>
    /// was merged from. The reducer runs off the UI thread, so a host must not
    /// replace the editor document when its serial has moved since: doing so would
    /// overwrite edits committed in between. Such a host throws
    /// <see cref="RoomChartStaleLocalSnapshotException"/> and the coordinator merges
    /// again from a fresh capture. Null for authoritative plans that intentionally
    /// replace local content.
    /// </summary>
    public long? LocalChangeSerial { get; }
    public RoomChartApplyMode Mode { get; }
    public ChartDocument Document => document.Clone();
    public IReadOnlyList<OwnerProtectedRegion> OwnerProtectedRegions => ownerProtectedRegions;
    public bool ApplyDocument { get; }
    public bool ClearHistory => Mode == RoomChartApplyMode.AuthoritativeReset;
    public bool PendingWriteAcknowledged { get; }

    /// <summary>
    /// The old transport batch used a regressed BaseRevision and was retired. Its
    /// user intent remains in Document and must be prepared again with a fresh ID.
    /// </summary>
    public bool PendingWriteRebased { get; }

    public bool RequiresFreshWritePreparation => PendingWriteRebased;
    public bool HasConcurrentLocalChanges { get; }
    public bool HasMore { get; }
    public bool RemoteDocumentFieldsChanged { get; }
    public long PreviousGeneration { get; }
    public bool GenerationAdvanced => State.Generation != PreviousGeneration;
    public bool ShouldRequestAnotherSync =>
        HasMore || HasConcurrentLocalChanges || PendingWriteRebased || State.HasPendingWrite;
}

public sealed class RoomChartFullSnapshotRequest
{
    internal RoomChartFullSnapshotRequest(
        RoomChartSyncState state,
        RoomChartFullSnapshotReason reason,
        RoomChartApplyMode mode,
        RoomChartFullSnapshotReplayMode replayMode,
        PreparedRoomChartAttempt attempt,
        bool acknowledgePendingWrite,
        bool rebasePendingWrite,
        RoomChartRejectedChangeScope rejectedScope = RoomChartRejectedChangeScope.None)
    {
        Generation = state.Generation;
        ExpectedRevision = state.Revision;
        ExpectedPendingOperationId = state.PendingWrite?.OperationId;
        ExpectedPendingCommitConfirmed = state.PendingWrite?.CommitConfirmed;
        Reason = reason;
        Mode = mode;
        ReplayMode = replayMode;
        Attempt = attempt;
        AcknowledgePendingWrite = acknowledgePendingWrite;
        RebasePendingWrite = rebasePendingWrite;
        RejectedScope = rejectedScope;
    }

    public long Generation { get; }
    public long ExpectedRevision { get; }
    public Guid? ExpectedPendingOperationId { get; }
    public bool? ExpectedPendingCommitConfirmed { get; }
    public RoomChartFullSnapshotReason Reason { get; }
    public RoomChartApplyMode Mode { get; }
    public RoomChartFullSnapshotReplayMode ReplayMode { get; }
    public PreparedRoomChartAttempt Attempt { get; }
    public bool AcknowledgePendingWrite { get; }
    public bool RebasePendingWrite { get; }
    public RoomChartRejectedChangeScope RejectedScope { get; }
    public bool PreserveLocalChanges => ReplayMode != RoomChartFullSnapshotReplayMode.DiscardLocalChanges;

    internal bool Matches(RoomChartSyncState state) =>
        Generation == state.Generation &&
        ExpectedRevision == state.Revision &&
        ExpectedPendingOperationId == state.PendingWrite?.OperationId &&
        ExpectedPendingCommitConfirmed == state.PendingWrite?.CommitConfirmed;
}

public sealed record RoomChartRejectedRecoveryPreparation(
    RoomChartSyncState State,
    RoomChartFullSnapshotRequest FullSnapshotRequest);

public sealed record RoomChartDeltaReduction(
    RoomChartDeltaDisposition Disposition,
    RoomChartSyncState State,
    RoomChartApplyPlan? ApplyPlan = null,
    RoomChartFullSnapshotRequest? FullSnapshotRequest = null)
{
    internal static RoomChartDeltaReduction Applied(RoomChartApplyPlan plan) =>
        new(RoomChartDeltaDisposition.Applied, plan.State, plan);

    internal static RoomChartDeltaReduction NeedsSnapshot(
        RoomChartSyncState state,
        RoomChartFullSnapshotRequest request) =>
        new(RoomChartDeltaDisposition.RequiresFullSnapshot, state, FullSnapshotRequest: request);

    internal static RoomChartDeltaReduction Stale(RoomChartSyncState state) =>
        new(RoomChartDeltaDisposition.IgnoredStaleResponse, state);
}

public sealed record RoomChartFullSnapshotReduction(
    bool IgnoredStaleResponse,
    RoomChartSyncState State,
    RoomChartApplyPlan? ApplyPlan)
{
    internal static RoomChartFullSnapshotReduction Applied(RoomChartApplyPlan plan) =>
        new(false, plan.State, plan);

    internal static RoomChartFullSnapshotReduction Stale(RoomChartSyncState state) =>
        new(true, state, null);
}

public sealed class RoomChartRollbackReduction
{
    private readonly RoomChartApplyPlan? applyPlan;
    private readonly OnlineChartSavedSnapshot? restoredSnapshot;

    public RoomChartRollbackReduction(
        RoomChartApplyPlan applyPlan,
        OnlineChartSavedSnapshot restoredSnapshot,
        Guid safetySnapshotId)
        : this(false, applyPlan.State, applyPlan, restoredSnapshot, safetySnapshotId)
    {
    }

    private RoomChartRollbackReduction(
        bool ignoredStaleResponse,
        RoomChartSyncState state,
        RoomChartApplyPlan? applyPlan,
        OnlineChartSavedSnapshot? restoredSnapshot,
        Guid safetySnapshotId)
    {
        IgnoredStaleResponse = ignoredStaleResponse;
        State = state;
        this.applyPlan = applyPlan;
        this.restoredSnapshot = restoredSnapshot;
        SafetySnapshotId = safetySnapshotId;
    }

    public bool IgnoredStaleResponse { get; }
    public RoomChartSyncState State { get; }
    public RoomChartApplyPlan ApplyPlan => applyPlan ?? throw new InvalidOperationException(
        "A stale rollback response does not have an apply plan.");
    public OnlineChartSavedSnapshot RestoredSnapshot => restoredSnapshot ??
        throw new InvalidOperationException("A stale rollback response has no restored snapshot.");
    public Guid SafetySnapshotId { get; }

    internal static RoomChartRollbackReduction Stale(RoomChartSyncState state) =>
        new(true, state, null, null, Guid.Empty);
}

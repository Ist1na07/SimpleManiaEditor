using System.Collections.ObjectModel;
using LumaChart.Core.Models;
using LumaChart.Online;

namespace LumaChart.Application.Collaboration;

/// <summary>
/// Immutable protocol state for one bound room chart. UI history and scheduling
/// remain outside this value; <see cref="Generation"/> invalidates in-flight work
/// when a rollback installs a new authoritative editing baseline.
/// </summary>
public sealed class RoomChartSyncState
{
    private readonly ChartDocument canonicalDocument;

    private RoomChartSyncState(
        long generation,
        long revision,
        ChartDocument canonicalDocument,
        PendingRoomChartWrite? pendingWrite)
    {
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        ArgumentNullException.ThrowIfNull(canonicalDocument);

        Generation = generation;
        Revision = revision;
        this.canonicalDocument = CloneNormalized(canonicalDocument);
        PendingWrite = pendingWrite;
    }

    public long Generation { get; }
    public long Revision { get; }
    public PendingRoomChartWrite? PendingWrite { get; }
    public bool HasPendingWrite => PendingWrite != null;

    /// <summary>Returns a caller-owned clone of the last server-canonical document.</summary>
    public ChartDocument CanonicalDocument => canonicalDocument.Clone();

    public static RoomChartSyncState Create(
        long revision,
        ChartDocument canonicalDocument,
        long generation = 0) =>
        new(generation, revision, canonicalDocument, null);

    internal ChartDocument CloneCanonicalDocument() => canonicalDocument.Clone();

    /// <summary>Notes plus timing events, used to size full-chart transfer deadlines.</summary>
    internal int CanonicalObjectCount => canonicalDocument.Notes.Count + canonicalDocument.Timing.Count;

    internal RoomChartSyncState With(
        long? generation = null,
        long? revision = null,
        ChartDocument? canonicalDocument = null,
        PendingRoomChartWrite? pendingWrite = null,
        bool clearPendingWrite = false) =>
        new(
            generation ?? Generation,
            revision ?? Revision,
            canonicalDocument ?? this.canonicalDocument,
            clearPendingWrite ? null : pendingWrite ?? PendingWrite);

    private static ChartDocument CloneNormalized(ChartDocument source)
    {
        var clone = source.Clone();
        clone.Normalize();
        return clone;
    }
}

/// <summary>
/// A deeply frozen, retryable room-chart write. Its batch and OperationId are
/// installed in <see cref="RoomChartSyncState"/> before a transport call begins.
/// </summary>
public sealed class PendingRoomChartWrite
{
    private readonly ChartDocument requestDocument;

    internal PendingRoomChartWrite(
        OnlineChartOperationBatch batch,
        ChartDocument requestDocument,
        long requestChangeSerial,
        bool commitConfirmed = false,
        bool omitsLocalChanges = false)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(requestDocument);
        if (batch.IsEmpty)
            throw new ArgumentException("A pending room-chart write cannot be empty.", nameof(batch));
        if (batch.OperationId == Guid.Empty)
            throw new ArgumentException("A pending room-chart write requires an OperationId.", nameof(batch));

        Batch = Freeze(batch);
        this.requestDocument = requestDocument.Clone();
        this.requestDocument.Normalize();
        RequestChangeSerial = requestChangeSerial;
        CommitConfirmed = commitConfirmed;
        OmitsLocalChanges = omitsLocalChanges;
    }

    public Guid OperationId => Batch.OperationId;
    public long BaseRevision => Batch.BaseRevision;
    public long RequestChangeSerial { get; }

    /// <summary>
    /// The local edits at <see cref="RequestChangeSerial"/> exceeded one server
    /// batch, so this write carries only the first part. Its request document is
    /// the canonical document plus that part; everything else stays local intent
    /// that must be merged back and sent by a following write.
    /// </summary>
    public bool OmitsLocalChanges { get; }
    /// <summary>
    /// The operations endpoint returned success, so this exact write is committed,
    /// but its operation may still be beyond the currently delivered delta page.
    /// Confirmed writes are chased with read-only pulls and are never posted again.
    /// </summary>
    public bool CommitConfirmed { get; }
    public OnlineChartOperationBatch Batch { get; }
    public ChartDocument RequestDocument => requestDocument.Clone();

    internal ChartDocument CloneRequestDocument() => requestDocument.Clone();

    internal PendingRoomChartWrite ConfirmCommit() => CommitConfirmed
        ? this
        : new PendingRoomChartWrite(
            Batch,
            requestDocument,
            RequestChangeSerial,
            commitConfirmed: true,
            omitsLocalChanges: OmitsLocalChanges);

    private static OnlineChartOperationBatch Freeze(OnlineChartOperationBatch batch) => new(
        batch.BaseRevision,
        ReadOnly(batch.UpsertNotes),
        ReadOnly(batch.DeleteNoteIds),
        ReadOnly(batch.UpsertTiming),
        ReadOnly(batch.DeleteTimingIds),
        batch.OperationId,
        batch.Metadata,
        batch.KeyCount);

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());
}

/// <summary>A transport-ready pull or write captured against one state generation.</summary>
public sealed class PreparedRoomChartAttempt
{
    private readonly ChartDocument requestDocument;

    internal PreparedRoomChartAttempt(
        long generation,
        long requestedRevision,
        long requestChangeSerial,
        ChartDocument requestDocument,
        OnlineChartOperationBatch batch,
        PendingRoomChartWrite? pendingWrite,
        bool omitsLocalChanges = false)
    {
        Generation = generation;
        RequestedRevision = requestedRevision;
        RequestChangeSerial = requestChangeSerial;
        this.requestDocument = requestDocument.Clone();
        Batch = batch;
        PendingWrite = pendingWrite;
        OmitsLocalChanges = omitsLocalChanges;
    }

    public long Generation { get; }
    public long RequestedRevision { get; }
    public long RequestChangeSerial { get; }
    public OnlineChartOperationBatch Batch { get; }
    public PendingRoomChartWrite? PendingWrite { get; }
    public bool IsWrite => !Batch.IsEmpty;

    /// <summary>
    /// The request document does not contain every local edit captured at
    /// <see cref="RequestChangeSerial"/>, so a reducer must merge local intent back
    /// even when the editor serial has not moved since.
    /// </summary>
    public bool OmitsLocalChanges { get; }
    public ChartDocument RequestDocument => requestDocument.Clone();

    internal ChartDocument CloneRequestDocument() => requestDocument.Clone();
}

public sealed record RoomChartAttemptPreparation(
    RoomChartSyncState State,
    PreparedRoomChartAttempt Attempt);

/// <summary>
/// A direct rollback request captured against one reducer state. The caller owns
/// the stable OperationId and must retain this attempt across ambiguous retries.
/// </summary>
public sealed class PreparedRoomChartRollbackAttempt
{
    internal PreparedRoomChartRollbackAttempt(
        long generation,
        long requestedRevision,
        Guid snapshotId,
        Guid operationId)
    {
        Generation = generation;
        RequestedRevision = requestedRevision;
        SnapshotId = snapshotId;
        OperationId = operationId;
    }

    public long Generation { get; }
    public long RequestedRevision { get; }
    public Guid SnapshotId { get; }
    public Guid OperationId { get; }
}

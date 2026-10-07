using System.Net;
using LumaChart.Core.Models;
using LumaChart.Online;

namespace LumaChart.Application.Collaboration;

/// <summary>
/// Room-bound chart transport used by <see cref="RoomChartSyncCoordinator"/>.
/// Authentication, endpoint selection, and room-ended policy remain adapter concerns.
/// The coordinator never invokes more than one delta/snapshot/rollback operation at once.
/// </summary>
public interface IRoomChartSyncTransport
{
    Task<OnlineChartDelta> GetDeltaAsync(
        long afterRevision,
        CancellationToken cancellationToken);

    Task<OnlineChartDelta> ApplyOperationsDeltaAsync(
        OnlineChartOperationBatch batch,
        CancellationToken cancellationToken);

    Task<OnlineChartSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    Task<OnlineChartRollbackResult> RollbackAsync(
        Guid snapshotId,
        Guid operationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs one SSE connection until EOF, failure, or cancellation. Notifications are
    /// hints only: the callback must not fetch or apply chart data itself.
    /// </summary>
    Task ListenForRevisionsAsync(
        Func<long, Task> revisionAvailable,
        CancellationToken cancellationToken);
}

/// <summary>
/// Platform adapter for a single editor document. Implementations may marshal these
/// calls to their UI dispatcher; coordinator state and transport remain platform-free.
/// A host must check the supplied token immediately before every document/view side
/// effect and must not perform that side effect after cancellation. This is what makes
/// a transport request overtaken by rollback harmless outside coordinator state too.
/// The coordinator never continues on the thread that completed a host call, so
/// merging and batch building stay off a UI dispatcher.
/// </summary>
public interface IRoomChartSyncDocumentHost
{
    ValueTask<RoomChartLocalSnapshot> CaptureLocalSnapshotAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a plan. When <see cref="RoomChartApplyPlan.LocalChangeSerial"/> no
    /// longer matches the editor, a host should throw
    /// <see cref="RoomChartStaleLocalSnapshotException"/> instead of replacing the
    /// document; the coordinator then recaptures and merges again.
    /// </summary>
    ValueTask ApplyPlanAsync(
        RoomChartApplyPlan plan,
        CancellationToken cancellationToken);

    ValueTask PublishViewStateAsync(
        RoomChartSyncViewState viewState,
        CancellationToken cancellationToken);

    /// <summary>
    /// Receives the complete local document immediately before a deterministic
    /// server rejection removes refused edits from the editor, so the host can keep
    /// it as a recoverable draft or backup. It is awaited before recovery is
    /// installed. Failures are ignored by the coordinator; the default keeps nothing.
    /// </summary>
    ValueTask PreserveRejectedLocalChangesAsync(
        RoomChartRejectedLocalChanges rejected,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>
/// Local content that a definitive server rejection is about to discard. All
/// documents are caller-owned clones.
/// </summary>
public sealed class RoomChartRejectedLocalChanges
{
    private readonly ChartDocument localDocument;
    private readonly ChartDocument requestDocument;

    public RoomChartRejectedLocalChanges(
        ChartDocument localDocument,
        long localChangeSerial,
        ChartDocument requestDocument,
        OnlineChartOperationBatch rejectedBatch,
        long revision,
        string? errorCode,
        HttpStatusCode? statusCode,
        string message,
        RoomChartRejectedChangeScope rejectedScope,
        DateTimeOffset rejectedAt)
    {
        ArgumentNullException.ThrowIfNull(localDocument);
        ArgumentNullException.ThrowIfNull(requestDocument);
        ArgumentNullException.ThrowIfNull(rejectedBatch);
        this.localDocument = localDocument.Clone();
        this.requestDocument = requestDocument.Clone();
        LocalChangeSerial = localChangeSerial;
        RejectedBatch = rejectedBatch;
        Revision = revision;
        ErrorCode = errorCode;
        StatusCode = statusCode;
        Message = message ?? string.Empty;
        RejectedScope = rejectedScope;
        RejectedAt = rejectedAt;
    }

    /// <summary>The whole editor document, including the edits about to be discarded.</summary>
    public ChartDocument LocalDocument => localDocument.Clone();

    public long LocalChangeSerial { get; }

    /// <summary>The document the rejected request was built from.</summary>
    public ChartDocument RequestDocument => requestDocument.Clone();

    public OnlineChartOperationBatch RejectedBatch { get; }
    public Guid OperationId => RejectedBatch.OperationId;

    /// <summary>The room revision the local document was based on.</summary>
    public long Revision { get; }

    public string? ErrorCode { get; }
    public HttpStatusCode? StatusCode { get; }
    public string Message { get; }
    public RoomChartRejectedChangeScope RejectedScope { get; }
    public DateTimeOffset RejectedAt { get; }
}

/// <summary>
/// Thrown by a document host when a plan was merged from a local snapshot that the
/// editor has since moved past. Nothing was applied; the coordinator retries the
/// merge from a fresh capture.
/// </summary>
public sealed class RoomChartStaleLocalSnapshotException : Exception
{
    public RoomChartStaleLocalSnapshotException(long expectedChangeSerial, long currentChangeSerial)
        : base(
            $"The room chart plan was merged from local change {expectedChangeSerial}, " +
            $"but the editor is at {currentChangeSerial}.")
    {
        ExpectedChangeSerial = expectedChangeSerial;
        CurrentChangeSerial = currentChangeSerial;
    }

    public long ExpectedChangeSerial { get; }
    public long CurrentChangeSerial { get; }
}

/// <summary>A caller-owned document image and its monotonic editor change serial.</summary>
public sealed class RoomChartLocalSnapshot
{
    private readonly ChartDocument document;

    public RoomChartLocalSnapshot(ChartDocument document, long changeSerial)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document.Clone();
        this.document.Normalize();
        ChangeSerial = changeSerial;
    }

    public ChartDocument Document => document.Clone();
    public long ChangeSerial { get; }

    internal ChartDocument CloneDocument() => document.Clone();
}

public delegate Task RoomChartSyncDelay(
    TimeSpan delay,
    CancellationToken cancellationToken);

public delegate TimeSpan RoomChartSyncJitter(int consecutiveFailureCount);

public delegate Guid RoomChartSyncOperationIdSource();

public sealed record RoomChartSyncCoordinatorOptions
{
    public TimeSpan WriteThrottle { get; init; } = TimeSpan.FromMilliseconds(40);
    public TimeSpan PullInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan RecoveredStatusHold { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan RetryStatusTail { get; init; } = TimeSpan.FromMilliseconds(1200);
    public TimeSpan WriteBudgetStatusTail { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Deadline for one small transport call (delta pull or ordinary write). The
    /// shared HttpClient only bounds a request after ten minutes, which let a
    /// half-open connection freeze room sync for that long.
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Extra deadline per thousand notes/timing events carried by a large write,
    /// full snapshot or rollback, so a big chart on a slow link is not cut off.
    /// </summary>
    public TimeSpan TransferTimeoutPerThousandItems { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound for a transport deadline. Consecutive timeouts double the
    /// deadline up to this value, so a slow but live link still completes.
    /// </summary>
    public TimeSpan MaximumTransportTimeout { get; init; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        RequireNonNegative(WriteThrottle, nameof(WriteThrottle));
        RequirePositive(PullInterval, nameof(PullInterval));
        RequirePositive(InitialRetryDelay, nameof(InitialRetryDelay));
        RequirePositive(MaximumRetryDelay, nameof(MaximumRetryDelay));
        RequireNonNegative(RecoveredStatusHold, nameof(RecoveredStatusHold));
        RequireNonNegative(RetryStatusTail, nameof(RetryStatusTail));
        RequireNonNegative(WriteBudgetStatusTail, nameof(WriteBudgetStatusTail));
        RequirePositive(RequestTimeout, nameof(RequestTimeout));
        RequireNonNegative(TransferTimeoutPerThousandItems, nameof(TransferTimeoutPerThousandItems));
        RequirePositive(MaximumTransportTimeout, nameof(MaximumTransportTimeout));
        if (MaximumRetryDelay < InitialRetryDelay)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumRetryDelay),
                "Maximum retry delay cannot be shorter than the initial retry delay.");
        if (MaximumTransportTimeout < RequestTimeout)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumTransportTimeout),
                "Maximum transport timeout cannot be shorter than the request timeout.");
    }

    private static void RequirePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name, "The duration must be positive.");
    }

    private static void RequireNonNegative(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(name, "The duration cannot be negative.");
    }
}

public enum RoomChartSyncWorkStatus
{
    Stopped,
    Idle,
    Throttled,
    Synchronizing,
    RollingBack,
    BackingOff,
    WriteBudgetThrottled,
    PermanentlyBlocked,
    Faulted
}

public enum RoomChartSyncFeedStatus
{
    Stopped,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}

/// <summary>
/// Platform-neutral coordinator diagnostics. Message is transport-provided and is
/// never a localized UI sentence assembled by the shared layer.
/// </summary>
public sealed record RoomChartSyncViewState(
    RoomChartSyncWorkStatus WorkStatus,
    RoomChartSyncFeedStatus FeedStatus,
    long Epoch,
    long Generation,
    long Revision,
    Guid? PendingOperationId,
    int ConsecutiveWorkFailures,
    int ConsecutiveFeedFailures,
    DateTimeOffset? RetryAt,
    DateTimeOffset? StatusHoldUntil,
    long? LastFeedRevision,
    string? ErrorCode,
    HttpStatusCode? ErrorStatusCode,
    string? Message)
{
    public bool IsWriteBlocked => WorkStatus == RoomChartSyncWorkStatus.PermanentlyBlocked;
    public string? WorkErrorCode { get; init; }
    public HttpStatusCode? WorkErrorStatusCode { get; init; }
    public string? WorkErrorMessage { get; init; }
    public string? FeedErrorCode { get; init; }
    public HttpStatusCode? FeedErrorStatusCode { get; init; }
    public string? FeedErrorMessage { get; init; }
}

public sealed record RoomChartRollbackOutcome(
    bool IgnoredStaleResponse,
    OnlineChartSavedSnapshot? RestoredSnapshot,
    Guid SafetySnapshotId,
    Guid OperationId);

using System.Net;
using System.Text.Json;
using LumaChart.Core.Editing;
using LumaChart.Online;

namespace LumaChart.Application.Collaboration;

/// <summary>
/// Single-flight room chart synchronization owner. Local edits, periodic pulls,
/// and SSE notifications all coalesce into one work signal; SSE never applies data.
/// </summary>
public sealed class RoomChartSyncCoordinator : IAsyncDisposable
{
    // Deadlines are monotonic TimeProvider timestamps. Wall-clock time is used only
    // for the values shown to the user: a system clock set back by an hour used to
    // freeze synchronization for an hour.
    private const long NoDeadline = long.MinValue;
    private const int MaximumStaleLocalSnapshotRetries = 4;
    private const int MaximumTransportTimeoutEscalation = 4;

    private readonly object gate = new();
    private readonly IRoomChartSyncTransport transport;
    private readonly IRoomChartSyncDocumentHost host;
    private readonly RoomChartSyncCoordinatorOptions options;
    private readonly TimeProvider timeProvider;
    private readonly RoomChartSyncDelay delay;
    private readonly RoomChartSyncJitter jitter;
    private readonly RoomChartSyncOperationIdSource operationIdSource;
    private readonly SemaphoreSlim workSignal = new(0, 1);
    private readonly SemaphoreSlim publishGate = new(1, 1);
    private readonly SemaphoreSlim lifecycleMutex = new(1, 1);

    private RoomChartSyncState state;
    private CancellationTokenSource? lifetime;
    private Task? workTask;
    private Task? pullTask;
    private Task? feedTask;
    private CancellationTokenSource? activeWorkCancellation;
    private CancellationTokenSource? scheduledDelayCancellation;
    private bool started;
    private bool stopping;
    private bool disposed;
    private bool wakePending;
    private bool localChangePending;
    private bool permanentlyBlocked;
    private long epoch;
    private long attemptEpoch;
    private long writeNotBefore = NoDeadline;
    private long nextAttemptAt = NoDeadline;
    private DateTimeOffset? nextAttemptDisplay;
    // chart_write_budget throttles writes only. Reads keep their normal cadence so
    // remote edits and lock changes still arrive during a window of up to an hour.
    private long writeBudgetUntil = NoDeadline;
    private DateTimeOffset? writeBudgetRetryAt;
    private OnlineApiException? writeBudgetError;
    private CancellationTokenSource? writeBudgetTimer;
    private DateTimeOffset? statusHoldUntil;
    private int workFailures;
    private int feedFailures;
    private int consecutiveTransportTimeouts;
    private bool wasDisconnected;
    private RoomChartSyncWorkStatus workStatus = RoomChartSyncWorkStatus.Stopped;
    private RoomChartSyncFeedStatus feedStatus = RoomChartSyncFeedStatus.Stopped;
    private long? lastFeedRevision;
    private string? workErrorCode;
    private HttpStatusCode? workErrorStatusCode;
    private string? workErrorMessage;
    private string? feedErrorCode;
    private HttpStatusCode? feedErrorStatusCode;
    private string? feedErrorMessage;
    private PendingRollback? pendingRollback;
    private RoomChartFullSnapshotRequest? pendingRecovery;
    private bool pendingRecoveryPreservesError;

    public RoomChartSyncCoordinator(
        RoomChartSyncState initialState,
        IRoomChartSyncTransport transport,
        IRoomChartSyncDocumentHost host,
        RoomChartSyncCoordinatorOptions? options = null,
        TimeProvider? timeProvider = null,
        RoomChartSyncDelay? delay = null,
        RoomChartSyncJitter? jitter = null,
        RoomChartSyncOperationIdSource? operationIdSource = null)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(host);

        this.options = options ?? new RoomChartSyncCoordinatorOptions();
        this.options.Validate();
        this.state = initialState;
        this.transport = transport;
        this.host = host;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.delay = delay ?? DefaultDelayAsync;
        this.jitter = jitter ?? DefaultJitter;
        this.operationIdSource = operationIdSource ?? Guid.NewGuid;
    }

    public RoomChartSyncState State
    {
        get { lock (gate) return state; }
    }

    public RoomChartSyncViewState ViewState
    {
        get { lock (gate) return CreateViewStateLocked(); }
    }

    public void Start()
    {
        CancellationToken token;
        CancellationTokenSource? replacedBudgetTimer = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stopping)
                throw new InvalidOperationException(
                    "The room chart coordinator is still stopping.");
            if (started) return;
            started = true;
            epoch = checked(epoch + 1);
            lifetime = new CancellationTokenSource();
            token = lifetime.Token;
            var now = timeProvider.GetTimestamp();
            workStatus = permanentlyBlocked
                ? RoomChartSyncWorkStatus.PermanentlyBlocked
                : nextAttemptAt > now
                    ? CollaborationRevisionPolicy.IsWriteBudgetThrottle(workErrorCode)
                        ? RoomChartSyncWorkStatus.WriteBudgetThrottled
                        : RoomChartSyncWorkStatus.BackingOff
                    : IsWriteBudgetActiveLocked(now)
                        ? RoomChartSyncWorkStatus.WriteBudgetThrottled
                        : RoomChartSyncWorkStatus.Idle;
            feedStatus = RoomChartSyncFeedStatus.Connecting;
            workTask = RunWorkLoopAsync(epoch, token);
            pullTask = RunPullLoopAsync(epoch, token);
            feedTask = RunFeedLoopAsync(epoch, token);
            // The write-budget deadline survives Stop/Start; so must its wake-up.
            if (IsWriteBudgetActiveLocked(now))
                replacedBudgetTimer = ArmWriteBudgetTimerLocked(epoch, token);
        }

        CancelWithoutThrow(replacedBudgetTimer);
        Wake();
    }

    /// <summary>
    /// Coalesces an editor mutation into the current 40 ms throttle window. Repeated
    /// calls do not move the deadline, so continuous input cannot starve a write.
    /// </summary>
    public void NotifyLocalChange()
    {
        lock (gate)
        {
            if (!started || disposed || permanentlyBlocked) return;
            if (!localChangePending)
            {
                localChangePending = true;
                writeNotBefore = AddTimestamp(timeProvider.GetTimestamp(), options.WriteThrottle);
            }
        }
        Wake();
    }

    /// <summary>Requests a pull without bypassing a current write throttle or retry window.</summary>
    public void RequestSynchronization() => Wake();

    /// <summary>
    /// Enqueues one owner rollback on the same single-flight loop. Ambiguous failures
    /// retain the same attempt and OperationId until success or a definitive response.
    /// </summary>
    public Task<RoomChartRollbackOutcome> RollbackAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (snapshotId == Guid.Empty)
            throw new ArgumentException("A rollback requires a snapshot identifier.", nameof(snapshotId));

        Task<RoomChartRollbackOutcome> result;
        CancellationTokenSource? workToCancel = null;
        CancellationTokenSource? delayToCancel = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!started)
                throw new InvalidOperationException("The room chart coordinator has not been started.");
            if (permanentlyBlocked)
                throw new InvalidOperationException(
                    "The room chart coordinator is permanently write-blocked.");
            if (pendingRollback is { } existing)
            {
                if (existing.SnapshotId != snapshotId)
                    throw new InvalidOperationException("Another room chart rollback is already pending.");
                result = existing.Completion.Task;
            }
            else
            {
                var operationId = NextOperationId();
                pendingRollback = new PendingRollback(snapshotId, operationId);
                result = pendingRollback.Completion.Task;
                attemptEpoch = checked(attemptEpoch + 1);
                nextAttemptAt = NoDeadline;
                nextAttemptDisplay = null;
                writeNotBefore = NoDeadline;
                workToCancel = activeWorkCancellation;
                delayToCancel = scheduledDelayCancellation;
            }
        }

        CancelWithoutThrow(workToCancel);
        CancelWithoutThrow(delayToCancel);
        Wake();
        return cancellationToken.CanBeCanceled
            ? result.WaitAsync(cancellationToken)
            : result;
    }

    public async Task StopAsync()
    {
        await lifecycleMutex.WaitAsync().ConfigureAwait(false);
        CancellationTokenSource? cancellation;
        CancellationTokenSource? workCancellation;
        CancellationTokenSource? delayCancellation;
        CancellationTokenSource? budgetTimer;
        Task[] tasks;
        PendingRollback? rollback;
        try
        {
            lock (gate)
            {
                stopping = true;
                if (!started)
                {
                    workStatus = RoomChartSyncWorkStatus.Stopped;
                    feedStatus = RoomChartSyncFeedStatus.Stopped;
                    return;
                }

                started = false;
                epoch = checked(epoch + 1);
                attemptEpoch = checked(attemptEpoch + 1);
                cancellation = lifetime;
                lifetime = null;
                workCancellation = activeWorkCancellation;
                delayCancellation = scheduledDelayCancellation;
                budgetTimer = writeBudgetTimer;
                writeBudgetTimer = null;
                tasks = new[] { workTask, pullTask, feedTask }
                    .Where(task => task != null)
                    .Cast<Task>()
                    .ToArray();
                workTask = null;
                pullTask = null;
                feedTask = null;
                rollback = pendingRollback;
                pendingRollback = null;
                // A deterministic rejection/full-recovery token is reducer state in
                // flight, not disposable scheduler state. Preserve it across a
                // lifecycle Stop/Start so a rejected batch cannot be recreated with
                // a fresh OperationId and chart_baseline_changed keeps its reset mode.
                wakePending = false;
            }

            CancelWithoutThrow(workCancellation);
            CancelWithoutThrow(delayCancellation);
            CancelWithoutThrow(budgetTimer);
            cancellation?.Cancel();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellation?.IsCancellationRequested == true)
            {
            }
            finally
            {
                cancellation?.Dispose();
            }

            // A wake can have been queued while the canceled work item was active.
            // No worker remains after WhenAll, so drain it before a supported restart.
            while (workSignal.Wait(0))
            {
            }

            rollback?.Completion.TrySetCanceled();
            lock (gate)
            {
                workStatus = RoomChartSyncWorkStatus.Stopped;
                feedStatus = RoomChartSyncFeedStatus.Stopped;
            }
        }
        finally
        {
            lock (gate) stopping = false;
            lifecycleMutex.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        await StopAsync().ConfigureAwait(false);
        workSignal.Dispose();
        publishGate.Dispose();
    }

    private async Task RunWorkLoopAsync(long runEpoch, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await workSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!IsCurrentRun(runEpoch)) return;

            while (IsCurrentRun(runEpoch))
            {
                var now = timeProvider.GetTimestamp();
                long due;
                bool blocked;
                CancellationTokenSource? scheduled = null;
                lock (gate)
                {
                    blocked = permanentlyBlocked;
                    // RollbackAsync clears an older work item's due time exactly once.
                    // A transient rollback failure then owns its own retry deadline and
                    // must keep the same OperationId rather than spinning immediately.
                    due = nextAttemptAt;
                    if (pendingRollback == null && localChangePending && writeNotBefore > due)
                        due = writeNotBefore;
                    if (!blocked && due > now)
                    {
                        scheduled = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                        scheduledDelayCancellation = scheduled;
                    }
                    else
                    {
                        wakePending = false;
                    }
                }
                if (blocked) break;

                if (scheduled != null)
                {
                    try
                    {
                        // Arm the timer for the absolute deadline before the throttle
                        // becomes observable or anything awaits, so no clock movement in
                        // between can push the wake-up past the deadline.
                        var wait = DelayUntilAsync(due, scheduled.Token);
                        lock (gate)
                        {
                            if (ReferenceEquals(scheduledDelayCancellation, scheduled) &&
                                nextAttemptAt <= now &&
                                !IsWriteBudgetActiveLocked(now))
                                workStatus = RoomChartSyncWorkStatus.Throttled;
                        }
                        await PublishViewStateAsync(scheduled.Token).ConfigureAwait(false);
                        await wait
                            .WaitAsync(scheduled.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        scheduled.IsCancellationRequested &&
                        !cancellationToken.IsCancellationRequested)
                    {
                        // A newly requested rollback bypasses throttle, backoff, and
                        // Retry-After. Recompute the due time without consuming a signal.
                    }
                    finally
                    {
                        lock (gate)
                        {
                            if (ReferenceEquals(scheduledDelayCancellation, scheduled))
                                scheduledDelayCancellation = null;
                        }
                        scheduled.Dispose();
                    }
                    if (!IsCurrentRun(runEpoch)) return;
                    continue;
                }

                await RunOneWorkItemAsync(runEpoch, cancellationToken).ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task RunOneWorkItemAsync(long runEpoch, CancellationToken cancellationToken)
    {
        PendingRollback? rollback;
        RoomChartFullSnapshotRequest? recovery;
        CancellationTokenSource workCancellation;
        long workAttemptEpoch;
        lock (gate)
        {
            if (!started || disposed || epoch != runEpoch) return;
            workAttemptEpoch = checked(attemptEpoch + 1);
            attemptEpoch = workAttemptEpoch;
            workCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            activeWorkCancellation = workCancellation;
            rollback = pendingRollback;
            recovery = pendingRecovery;
            localChangePending = false;
            writeNotBefore = NoDeadline;
            // Read-only pulls during a write-budget window keep the budget status and
            // its error visible instead of flickering through Synchronizing.
            if (rollback != null || !IsWriteBudgetActiveLocked(timeProvider.GetTimestamp()))
            {
                workStatus = rollback == null
                    ? RoomChartSyncWorkStatus.Synchronizing
                    : RoomChartSyncWorkStatus.RollingBack;
                workErrorCode = null;
                workErrorStatusCode = null;
                workErrorMessage = null;
            }
        }
        var context = new WorkContext(
            runEpoch,
            workAttemptEpoch,
            workCancellation.Token);
        var preempted = false;

        try
        {
            await PublishViewStateAsync(context.CancellationToken).ConfigureAwait(false);
            if (rollback != null)
                await RunRollbackAsync(context, rollback).ConfigureAwait(false);
            else if (recovery != null)
                await RunPendingRecoveryAsync(context, recovery)
                    .ConfigureAwait(false);
            else
                await RunSynchronizationAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            workCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            preempted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            IsCurrentWork(context) &&
            IsTransient(exception, context.CancellationToken))
        {
            ScheduleTransientRetry(exception, feed: false, context: context);
            Wake();
        }
        catch (Exception exception)
        {
            PendingRollback? failedRollback = null;
            var resumeRecovery = false;
            lock (gate)
            {
                if (!IsCurrentWorkLocked(context))
                {
                    preempted = true;
                }
                else if (rollback != null && ReferenceEquals(pendingRollback, rollback))
                {
                    failedRollback = pendingRollback;
                    pendingRollback = null;
                    resumeRecovery = pendingRecovery != null;
                }
                if (!preempted)
                {
                    workStatus = RoomChartSyncWorkStatus.Faulted;
                    SetWorkErrorLocked(exception);
                }
            }
            failedRollback?.Completion.TrySetException(exception);
            if (resumeRecovery) Wake();
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeWorkCancellation, workCancellation))
                    activeWorkCancellation = null;
            }
            workCancellation.Dispose();
        }

        if (!preempted && IsCurrentRun(runEpoch))
            await PublishViewStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSynchronizationAsync(WorkContext context)
    {
        var local = await CaptureLocalSnapshotAsync(context).ConfigureAwait(false);
        if (!IsCurrentWork(context)) return;
        RoomChartSyncState beforePreparation;
        lock (gate)
        {
            beforePreparation = state;
            var now = timeProvider.GetTimestamp();
            if (beforePreparation.PendingWrite == null &&
                localChangePending &&
                writeNotBefore > now)
            {
                // This work item may have been a periodic/SSE pull. If an edit was
                // captured after it started, honor that edit's original 40 ms window
                // instead of turning the immediate pull into an early POST.
                if (!IsWriteBudgetActiveLocked(now))
                    workStatus = RoomChartSyncWorkStatus.Throttled;
                return;
            }
        }
        var operationId = RoomChartSyncReducer.RequiresNewOperationId(
            beforePreparation,
            local.CloneDocument())
            ? NextOperationId()
            : Guid.Empty;
        var preparation = RoomChartSyncReducer.PrepareAttempt(
            beforePreparation,
            local.CloneDocument(),
            local.ChangeSerial,
            operationId);
        if (!IsCurrentWork(context)) return;

        // The frozen write is installed before transport begins. Every ambiguous
        // retry therefore reuses the exact same immutable batch and OperationId.
        lock (gate)
        {
            if (!IsCurrentWorkLocked(context) ||
                !ReferenceEquals(state, beforePreparation)) return;
            state = preparation.State;
        }

        var attempt = preparation.Attempt;
        // During a write-budget window the frozen write waits for its Retry-After,
        // but a read-only pull still merges remote edits over the pending intent.
        if (attempt.IsWrite && IsWriteBudgetActive())
            attempt = RoomChartSyncReducer.PrepareReadOnlyAttempt(preparation.State);

        OnlineChartDelta delta;
        try
        {
            delta = attempt.IsWrite
                ? await CallTransportAsync(
                        token => transport.ApplyOperationsDeltaAsync(attempt.Batch, token),
                        TransferTimeout(CountItems(attempt.Batch)),
                        context.CancellationToken)
                    .ConfigureAwait(false)
                : await CallTransportAsync(
                        token => transport.GetDeltaAsync(attempt.RequestedRevision, token),
                        options.RequestTimeout,
                        context.CancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OnlineApiException exception) when (
            CollaborationRevisionPolicy.IsWriteBudgetThrottle(exception.Code))
        {
            if (!IsCurrentWork(context)) return;
            ScheduleWriteBudgetRetry(context, exception);
            return;
        }
        catch (OnlineApiException exception) when (
            CollaborationRevisionPolicy.IsPermanentRoomWriteBlock(exception.Code))
        {
            lock (gate)
            {
                if (!IsCurrentWorkLocked(context)) return;
                state = RoomChartSyncReducer.RetirePendingWrite(state);
                permanentlyBlocked = true;
                workStatus = RoomChartSyncWorkStatus.PermanentlyBlocked;
                nextAttemptAt = long.MaxValue;
                nextAttemptDisplay = null;
                SetWorkErrorLocked(exception);
            }
            return;
        }
        catch (OnlineApiException exception) when (
            attempt.IsWrite &&
            !IsTransient(exception, context.CancellationToken) &&
            !WasNotEvaluated(exception))
        {
            await HandleDefinitiveWriteRejectionAsync(
                    context, attempt, exception)
                .ConfigureAwait(false);
            return;
        }

        if (!IsCurrentWork(context)) return;
        await ReduceDeltaAndApplyAsync(context, attempt, delta)
            .ConfigureAwait(false);
        CompleteSuccessfulWork(context);
    }

    private async Task ReduceDeltaAndApplyAsync(
        WorkContext context,
        PreparedRoomChartAttempt attempt,
        OnlineChartDelta delta)
    {
        for (var staleRetries = 0; ; staleRetries++)
        {
            var local = await CaptureLocalSnapshotAsync(context).ConfigureAwait(false);
            if (!IsCurrentWork(context)) return;
            RoomChartSyncState current;
            lock (gate) current = state;
            var reduction = RoomChartSyncReducer.ReduceDelta(
                current,
                attempt,
                delta,
                local.CloneDocument(),
                local.ChangeSerial);
            if (!IsCurrentWork(context) || reduction.Disposition ==
                RoomChartDeltaDisposition.IgnoredStaleResponse) return;

            if (reduction.Disposition == RoomChartDeltaDisposition.RequiresFullSnapshot)
            {
                var request = reduction.FullSnapshotRequest ?? throw new InvalidOperationException(
                    "The reducer requested a snapshot without a recovery token.");
                lock (gate)
                {
                    if (!IsCurrentWorkLocked(context) ||
                        !ReferenceEquals(state, current)) return;
                    state = reduction.State;
                    pendingRecovery = request;
                    pendingRecoveryPreservesError = false;
                }
                await RunPendingRecoveryAsync(context, request)
                    .ConfigureAwait(false);
                return;
            }

            var plan = reduction.ApplyPlan ?? throw new InvalidOperationException(
                "The reducer applied a delta without an apply plan.");
            try
            {
                await InstallPlanAsync(context, plan).ConfigureAwait(false);
                return;
            }
            catch (RoomChartStaleLocalSnapshotException) when (
                staleRetries < MaximumStaleLocalSnapshotRetries && IsCurrentWork(context))
            {
                // The editor committed an edit while this merge ran off the UI thread.
                // Nothing was applied and state is unchanged: merge again from a fresh
                // capture so that edit is kept instead of overwritten.
            }
        }
    }

    private async Task HandleDefinitiveWriteRejectionAsync(
        WorkContext context,
        PreparedRoomChartAttempt attempt,
        OnlineApiException exception)
    {
        RoomChartSyncState current;
        lock (gate) current = state;
        var mode = string.Equals(
            exception.Code,
            "chart_baseline_changed",
            StringComparison.OrdinalIgnoreCase)
            ? RoomChartApplyMode.AuthoritativeReset
            : RoomChartApplyMode.OrdinaryRemoteMerge;
        var scope = mode == RoomChartApplyMode.AuthoritativeReset
            ? RoomChartRejectedChangeScope.None
            : RoomChartSyncReducer.ClassifyRejectedChanges(exception.Code);

        // The recovery below removes refused edits from the editor. Hand the whole
        // local document to the host first so the user can recover it.
        await PreserveRejectedLocalChangesAsync(
                context, current, attempt, exception, scope)
            .ConfigureAwait(false);
        if (!IsCurrentWork(context)) return;

        var recovery = RoomChartSyncReducer.PrepareRejectedAttemptRecovery(
            current, attempt, mode, scope);
        lock (gate)
        {
            if (!IsCurrentWorkLocked(context) ||
                !ReferenceEquals(state, current)) return;
            state = recovery.State;
            pendingRecovery = recovery.FullSnapshotRequest;
            pendingRecoveryPreservesError = true;
            SetWorkErrorLocked(exception);
        }
        await RunPendingRecoveryAsync(
                context, recovery.FullSnapshotRequest)
            .ConfigureAwait(false);
    }

    private async Task PreserveRejectedLocalChangesAsync(
        WorkContext context,
        RoomChartSyncState current,
        PreparedRoomChartAttempt attempt,
        OnlineApiException exception,
        RoomChartRejectedChangeScope scope)
    {
        try
        {
            var local = await CaptureLocalSnapshotAsync(context).ConfigureAwait(false);
            if (!IsCurrentWork(context)) return;
            var rejected = new RoomChartRejectedLocalChanges(
                local.CloneDocument(),
                local.ChangeSerial,
                attempt.CloneRequestDocument(),
                attempt.Batch,
                current.Revision,
                exception.Code,
                exception.StatusCode,
                exception.Message,
                scope,
                timeProvider.GetUtcNow());
            await AwaitHostAsync(
                    host.PreserveRejectedLocalChangesAsync(rejected, context.CancellationToken),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A backup that cannot be written must not wedge synchronization: the
            // rejected batch would otherwise be resent and rejected forever.
        }
    }

    private async Task RunPendingRecoveryAsync(
        WorkContext context,
        RoomChartFullSnapshotRequest request)
    {
        int objectCount;
        lock (gate) objectCount = state.CanonicalObjectCount;
        var snapshot = await CallTransportAsync(
                token => transport.GetSnapshotAsync(token),
                TransferTimeout(objectCount),
                context.CancellationToken)
            .ConfigureAwait(false);
        if (!IsCurrentWork(context)) return;
        for (var staleRetries = 0; ; staleRetries++)
        {
            var local = await CaptureLocalSnapshotAsync(context).ConfigureAwait(false);
            if (!IsCurrentWork(context)) return;
            RoomChartSyncState current;
            bool preserveError;
            lock (gate)
            {
                current = state;
                preserveError = pendingRecoveryPreservesError;
            }
            var reduction = RoomChartSyncReducer.ApplyFullSnapshot(
                current,
                request,
                snapshot,
                local.CloneDocument(),
                local.ChangeSerial);
            if (!IsCurrentWork(context)) return;
            if (reduction.IgnoredStaleResponse)
            {
                lock (gate)
                {
                    if (ReferenceEquals(pendingRecovery, request))
                    {
                        pendingRecovery = null;
                        pendingRecoveryPreservesError = false;
                    }
                }
                CompleteSuccessfulWork(context, preserveError);
                return;
            }

            var plan = reduction.ApplyPlan ?? throw new InvalidOperationException(
                "The reducer applied snapshot recovery without an apply plan.");
            try
            {
                await InstallPlanAsync(context, plan).ConfigureAwait(false);
            }
            catch (RoomChartStaleLocalSnapshotException) when (
                staleRetries < MaximumStaleLocalSnapshotRetries && IsCurrentWork(context))
            {
                continue;
            }
            lock (gate)
            {
                if (IsCurrentWorkLocked(context) &&
                    ReferenceEquals(pendingRecovery, request))
                {
                    pendingRecovery = null;
                    pendingRecoveryPreservesError = false;
                }
            }
            CompleteSuccessfulWork(context, preserveError);
            return;
        }
    }

    private async Task RunRollbackAsync(
        WorkContext context,
        PendingRollback rollback)
    {
        RoomChartSyncState before;
        lock (gate) before = state;
        rollback.Attempt ??= RoomChartSyncReducer.PrepareDirectRollback(
            before, rollback.SnapshotId, rollback.OperationId);
        var result = await CallTransportAsync(
                token => transport.RollbackAsync(
                    rollback.SnapshotId,
                    rollback.OperationId,
                    token),
                TransferTimeout(before.CanonicalObjectCount),
                context.CancellationToken)
            .ConfigureAwait(false);
        if (!IsCurrentWork(context)) return;

        var local = await CaptureLocalSnapshotAsync(context).ConfigureAwait(false);
        if (!IsCurrentWork(context)) return;
        RoomChartSyncState current;
        lock (gate) current = state;
        var reduction = RoomChartSyncReducer.ApplyDirectRollback(
            current,
            rollback.Attempt,
            result,
            local.CloneDocument());
        if (!reduction.IgnoredStaleResponse)
            await InstallPlanAsync(context, reduction.ApplyPlan)
                .ConfigureAwait(false);
        if (!IsCurrentWork(context)) return;

        lock (gate)
        {
            if (IsCurrentWorkLocked(context) &&
                ReferenceEquals(pendingRollback, rollback))
            {
                pendingRollback = null;
                if (!reduction.IgnoredStaleResponse)
                {
                    pendingRecovery = null;
                    pendingRecoveryPreservesError = false;
                }
            }
        }
        rollback.Completion.TrySetResult(new RoomChartRollbackOutcome(
            reduction.IgnoredStaleResponse,
            reduction.IgnoredStaleResponse ? null : reduction.RestoredSnapshot,
            reduction.IgnoredStaleResponse ? Guid.Empty : reduction.SafetySnapshotId,
            rollback.OperationId));
        CompleteSuccessfulWork(context);
        Wake();
    }

    private async Task InstallPlanAsync(
        WorkContext context,
        RoomChartApplyPlan plan)
    {
        if (!IsCurrentWork(context)) return;
        await AwaitHostAsync(
                host.ApplyPlanAsync(plan, context.CancellationToken),
                context.CancellationToken)
            .ConfigureAwait(false);
        if (!IsCurrentWork(context)) return;
        bool wake;
        lock (gate)
        {
            if (!IsCurrentWorkLocked(context)) return;
            state = plan.State;
            // While the write budget is exhausted, pending or concurrent local intent
            // cannot be sent anyway; only another delta page justifies an immediate
            // follow-up. The budget timer wakes the write at its deadline.
            wake = plan.HasMore ||
                   plan.ShouldRequestAnotherSync &&
                   !IsWriteBudgetActiveLocked(timeProvider.GetTimestamp());
        }
        if (wake) Wake();
    }

    private Task<RoomChartLocalSnapshot> CaptureLocalSnapshotAsync(WorkContext context) =>
        AwaitHostAsync(
            host.CaptureLocalSnapshotAsync(context.CancellationToken),
            context.CancellationToken);

    /// <summary>
    /// Awaits a host call and guarantees the coordinator does not continue on a UI
    /// dispatcher. A dispatcher operation completes its task on the UI thread without
    /// RunContinuationsAsynchronously, so a plain await ran the reducer, batch building
    /// and JSON measurement there. Such calls (still pending when awaited, or awaited
    /// while a synchronization context is installed) resume on the thread pool. A call
    /// that already completed on a pool thread continues inline, which keeps the
    /// order of state changes and timer registration deterministic.
    /// </summary>
    private static async Task<T> AwaitHostAsync<T>(
        ValueTask<T> operation,
        CancellationToken cancellationToken)
    {
        if (operation.IsCompletedSuccessfully && SynchronizationContext.Current == null)
            return operation.Result;
        return await operation.AsTask()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }

    private static async Task AwaitHostAsync(
        ValueTask operation,
        CancellationToken cancellationToken)
    {
        if (operation.IsCompletedSuccessfully && SynchronizationContext.Current == null)
        {
            operation.GetAwaiter().GetResult();
            return;
        }
        await operation.AsTask()
            .WaitAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }

    /// <summary>
    /// Runs one transport call under its own deadline. A half-open connection was
    /// previously bounded only by the shared ten-minute HttpClient timeout. The
    /// deadline doubles after each consecutive timeout (up to the configured
    /// maximum) so a genuinely slow link still completes a large transfer.
    /// </summary>
    private async Task<T> CallTransportAsync<T>(
        Func<CancellationToken, Task<T>> call,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        int escalation;
        lock (gate) escalation = consecutiveTransportTimeouts;
        var effectiveTimeout = EscalateTimeout(timeout, escalation);
        using var callCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        // The deadline is armed before the request starts, so it is in place by the
        // time anyone can observe the request.
        var deadline = DelayUntilAsync(
            AddTimestamp(timeProvider.GetTimestamp(), effectiveTimeout),
            callCancellation.Token);
        Task<T> operation;
        try
        {
            operation = call(callCancellation.Token);
        }
        catch
        {
            callCancellation.Cancel();
            throw;
        }
        var completed = await Task.WhenAny(operation, deadline).ConfigureAwait(false);
        if (!ReferenceEquals(completed, operation))
        {
            callCancellation.Cancel();
            ObserveAbandoned(operation);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                consecutiveTransportTimeouts = Math.Min(
                    consecutiveTransportTimeouts + 1,
                    MaximumTransportTimeoutEscalation);
            }
            throw new TimeoutException(
                $"The room chart request did not complete within {effectiveTimeout.TotalSeconds:0.#} seconds.");
        }

        // Releases the deadline timer.
        callCancellation.Cancel();
        var result = await operation.ConfigureAwait(false);
        lock (gate) consecutiveTransportTimeouts = 0;
        return result;
    }

    private TimeSpan EscalateTimeout(TimeSpan timeout, int escalation)
    {
        var maximum = options.MaximumTransportTimeout;
        if (timeout >= maximum) return maximum;
        var scaled = (double)timeout.Ticks * (1L << Math.Clamp(escalation, 0, 30));
        return scaled >= maximum.Ticks ? maximum : TimeSpan.FromTicks((long)scaled);
    }

    private TimeSpan TransferTimeout(long items)
    {
        var thousands = Math.Max(0, (items + 999) / 1000);
        var ticks = options.RequestTimeout.Ticks +
                    (double)options.TransferTimeoutPerThousandItems.Ticks * thousands;
        return ticks >= options.MaximumTransportTimeout.Ticks
            ? options.MaximumTransportTimeout
            : TimeSpan.FromTicks((long)ticks);
    }

    private static int CountItems(OnlineChartOperationBatch batch) =>
        batch.UpsertNotes.Count + batch.DeleteNoteIds.Count +
        batch.UpsertTiming.Count + batch.DeleteTimingIds.Count;

    private static void ObserveAbandoned(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task RunPullLoopAsync(long runEpoch, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await DelayUntilAsync(
                    AddTimestamp(timeProvider.GetTimestamp(), options.PullInterval),
                    cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!IsCurrentRun(runEpoch)) return;
            Wake();
        }
    }

    private async Task RunFeedLoopAsync(long runEpoch, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (gate)
            {
                feedStatus = feedFailures == 0
                    ? RoomChartSyncFeedStatus.Connecting
                    : RoomChartSyncFeedStatus.Reconnecting;
            }
            await PublishViewStateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await transport.ListenForRevisionsAsync(
                    revision => OnFeedRevisionAsync(runEpoch, revision),
                    cancellationToken)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!IsCurrentRun(runEpoch)) return;
                throw new IOException("The room chart revision feed ended.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsTransientFeed(exception, cancellationToken))
            {
                // The reconnect timer is armed before publishing (see the work loop).
                var wait = DelayUntilAsync(
                    AddTimestamp(
                        timeProvider.GetTimestamp(),
                        ScheduleTransientRetry(exception, feed: true)),
                    cancellationToken);
                await PublishViewStateAsync(cancellationToken).ConfigureAwait(false);
                await wait
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!FeedRequiresUserAction(exception))
            {
                // A 403/404/400 on the event stream is usually a race with joining,
                // chart initialization or a server deploy. Stopping for good left the
                // room without push notifications for the rest of the session; keep
                // reporting the fault but retry at the slowest backoff.
                TimeSpan retry;
                lock (gate) retry = ApplyRetryAfter(ComputeRetryDelay(8), exception);
                var wait = DelayUntilAsync(
                    AddTimestamp(timeProvider.GetTimestamp(), retry),
                    cancellationToken);
                lock (gate)
                {
                    feedFailures = 8;
                    feedStatus = RoomChartSyncFeedStatus.Faulted;
                    SetFeedErrorLocked(exception);
                }
                await PublishViewStateAsync(cancellationToken).ConfigureAwait(false);
                await wait
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (gate)
                {
                    feedStatus = RoomChartSyncFeedStatus.Faulted;
                    SetFeedErrorLocked(exception);
                }
                await PublishViewStateAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private Task OnFeedRevisionAsync(long runEpoch, long revision)
    {
        if (!IsCurrentRun(runEpoch)) return Task.CompletedTask;
        lock (gate)
        {
            feedStatus = RoomChartSyncFeedStatus.Connected;
            feedFailures = 0;
            lastFeedRevision = revision;
            feedErrorCode = null;
            feedErrorStatusCode = null;
            feedErrorMessage = null;
        }
        Wake();
        return Task.CompletedTask;
    }

    private void CompleteSuccessfulWork(
        WorkContext context,
        bool preserveError = false)
    {
        lock (gate)
        {
            if (!IsCurrentWorkLocked(context)) return;
            workFailures = 0;
            nextAttemptAt = NoDeadline;
            nextAttemptDisplay = null;
            if (IsWriteBudgetActiveLocked(timeProvider.GetTimestamp()) &&
                state.PendingWrite is { CommitConfirmed: false })
            {
                // A read succeeded but the frozen write still waits for the budget
                // window; keep that status, its deadline and its server message.
                workStatus = RoomChartSyncWorkStatus.WriteBudgetThrottled;
                if (writeBudgetError != null) SetWorkErrorLocked(writeBudgetError);
                if (writeBudgetRetryAt is { } retryAt)
                    statusHoldUntil = AddClamped(retryAt, options.WriteBudgetStatusTail);
                return;
            }
            var recovered = wasDisconnected;
            wasDisconnected = false;
            workStatus = RoomChartSyncWorkStatus.Idle;
            if (recovered)
                statusHoldUntil = AddClamped(
                    timeProvider.GetUtcNow(), options.RecoveredStatusHold);
            if (!preserveError)
            {
                workErrorCode = null;
                workErrorStatusCode = null;
                workErrorMessage = null;
            }
        }
    }

    private void ScheduleWriteBudgetRetry(
        WorkContext context,
        OnlineApiException exception)
    {
        var retry = CollaborationRevisionPolicy.NormalizeWriteBudgetRetryAfter(
            exception.RetryAfter);
        CancellationTokenSource? replacedTimer;
        lock (gate)
        {
            if (!IsCurrentWorkLocked(context) || lifetime == null) return;
            workFailures = 0;
            nextAttemptAt = NoDeadline;
            nextAttemptDisplay = null;
            writeBudgetUntil = AddTimestamp(timeProvider.GetTimestamp(), retry);
            writeBudgetRetryAt = AddClamped(timeProvider.GetUtcNow(), retry);
            writeBudgetError = exception;
            statusHoldUntil = AddClamped(
                writeBudgetRetryAt.Value,
                options.WriteBudgetStatusTail);
            workStatus = RoomChartSyncWorkStatus.WriteBudgetThrottled;
            SetWorkErrorLocked(exception);
            replacedTimer = ArmWriteBudgetTimerLocked(context.RunEpoch, lifetime.Token);
        }
        CancelWithoutThrow(replacedTimer);
    }

    /// <summary>Returns the previous timer, which the caller cancels outside the lock.</summary>
    private CancellationTokenSource? ArmWriteBudgetTimerLocked(
        long runEpoch,
        CancellationToken lifetimeToken)
    {
        var previous = writeBudgetTimer;
        var timer = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        writeBudgetTimer = timer;
        _ = RunWriteBudgetTimerAsync(runEpoch, timer);
        return previous;
    }

    private async Task RunWriteBudgetTimerAsync(long runEpoch, CancellationTokenSource timer)
    {
        try
        {
            while (true)
            {
                long until;
                lock (gate) until = writeBudgetUntil;
                if (until <= timeProvider.GetTimestamp()) break;
                await DelayUntilAsync(until, timer.Token)
                    .WaitAsync(timer.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Canceled by Stop or by a newer budget window, which owns its own timer.
            return;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(writeBudgetTimer, timer)) writeBudgetTimer = null;
            }
            timer.Dispose();
        }
        if (IsCurrentRun(runEpoch)) Wake();
    }

    private TimeSpan ScheduleTransientRetry(
        Exception exception,
        bool feed,
        WorkContext? context = null)
    {
        lock (gate)
        {
            if (feed)
            {
                feedFailures = Math.Min(feedFailures + 1, 8);
                feedStatus = RoomChartSyncFeedStatus.Reconnecting;
                SetFeedErrorLocked(exception);
                return ApplyRetryAfter(ComputeRetryDelay(feedFailures), exception);
            }

            if (context is not { } workContext ||
                !IsCurrentWorkLocked(workContext))
                return TimeSpan.Zero;
            workFailures = Math.Min(workFailures + 1, 8);
            wasDisconnected = true;
            var retry = ApplyRetryAfter(
                ComputeRetryDelay(workFailures),
                exception);
            nextAttemptAt = AddTimestamp(timeProvider.GetTimestamp(), retry);
            nextAttemptDisplay = AddClamped(timeProvider.GetUtcNow(), retry);
            statusHoldUntil = AddClamped(nextAttemptDisplay.Value, options.RetryStatusTail);
            workStatus = RoomChartSyncWorkStatus.BackingOff;
            SetWorkErrorLocked(exception);
            return retry;
        }
    }

    private TimeSpan ComputeRetryDelay(int failureCount)
    {
        var exponent = Math.Min(5, Math.Max(0, failureCount - 1));
        var scaledTicks = options.InitialRetryDelay.Ticks * (1L << exponent);
        var baseDelay = TimeSpan.FromTicks(Math.Min(
            options.MaximumRetryDelay.Ticks, scaledTicks));
        var extra = jitter(failureCount);
        if (extra < TimeSpan.Zero)
            throw new InvalidOperationException("Room chart retry jitter cannot be negative.");
        var availableTicks = TimeSpan.MaxValue.Ticks - baseDelay.Ticks;
        return TimeSpan.FromTicks(baseDelay.Ticks + Math.Min(extra.Ticks, availableTicks));
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            // A 200 whose body is not the JSON contract (captive portal, truncated
            // proxy answer) says nothing about whether a write was committed. Retry
            // it like a transport failure with the same OperationId.
            OnlineApiException api when OnlineHttpResponse.IsInvalidResponse(api) => true,
            OnlineApiException api =>
                api.StatusCode == HttpStatusCode.RequestTimeout ||
                api.StatusCode == HttpStatusCode.TooManyRequests ||
                api.StatusCode is { } status && (int)status >= 500,
            HttpRequestException => true,
            IOException => true,
            // A successful HTTP response can still be cut off while its JSON
            // body is in flight. System.Net.Http.Json reports that condition
            // as JsonException rather than IOException. Treat it like the
            // surrounding transport failures so the coordinator retains local
            // work and retries instead of permanently faulting room sync.
            JsonException => true,
            // A per-call deadline expired: the request's outcome is unknown.
            TimeoutException => true,
            // The editor kept changing while a merge was applied; retry the merge.
            RoomChartStaleLocalSnapshotException => true,
            OperationCanceledException when !cancellationToken.IsCancellationRequested => true,
            _ => false
        };

    /// <summary>
    /// A write refused before the server looked at its content: the session or
    /// client version is no longer accepted, the membership ended, or the chart is
    /// not there. Discarding the edits would lose work the server never judged, so
    /// the pending write and the editor document are kept and sync reports a fault.
    /// </summary>
    private static bool WasNotEvaluated(OnlineApiException exception) =>
        exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.UpgradeRequired or
            HttpStatusCode.NotFound ||
        exception.Code?.Trim().ToLowerInvariant() is
            "authentication_required" or "member_required" or "room_not_found" or "room_closed";

    private static bool FeedRequiresUserAction(Exception exception) =>
        exception is OnlineApiException api &&
        (api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.UpgradeRequired ||
         string.Equals(api.Code, "authentication_required", StringComparison.OrdinalIgnoreCase));

    private static bool IsTransientFeed(
        Exception exception,
        CancellationToken cancellationToken) =>
        IsTransient(exception, cancellationToken);

    private static TimeSpan ApplyRetryAfter(TimeSpan retry, Exception exception)
    {
        if (exception is not OnlineApiException { RetryAfter: { } retryAfter } ||
            retryAfter <= TimeSpan.Zero)
            return retry;
        var bounded = retryAfter > TimeSpan.FromHours(1)
            ? TimeSpan.FromHours(1)
            : retryAfter;
        return bounded > retry ? bounded : retry;
    }

    private void SetWorkErrorLocked(Exception exception)
    {
        if (exception is OnlineApiException api)
        {
            workErrorCode = api.Code;
            workErrorStatusCode = api.StatusCode;
        }
        else
        {
            workErrorCode = null;
            workErrorStatusCode = null;
        }
        workErrorMessage = exception.Message;
    }

    private void SetFeedErrorLocked(Exception exception)
    {
        if (exception is OnlineApiException api)
        {
            feedErrorCode = api.Code;
            feedErrorStatusCode = api.StatusCode;
        }
        else
        {
            feedErrorCode = null;
            feedErrorStatusCode = null;
        }
        feedErrorMessage = exception.Message;
    }

    private void Wake()
    {
        lock (gate)
        {
            if (!started || disposed || permanentlyBlocked || wakePending) return;
            wakePending = true;
            workSignal.Release();
        }
    }

    private bool IsCurrentRun(long runEpoch)
    {
        lock (gate) return started && !disposed && epoch == runEpoch;
    }

    private bool IsCurrentWork(WorkContext context)
    {
        lock (gate) return IsCurrentWorkLocked(context);
    }

    private bool IsCurrentWorkLocked(WorkContext context) =>
        started &&
        !disposed &&
        epoch == context.RunEpoch &&
        attemptEpoch == context.AttemptEpoch &&
        !context.CancellationToken.IsCancellationRequested;

    private bool IsWriteBudgetActive()
    {
        lock (gate) return IsWriteBudgetActiveLocked(timeProvider.GetTimestamp());
    }

    private bool IsWriteBudgetActiveLocked(long now) => writeBudgetUntil > now;

    private async Task PublishViewStateAsync(CancellationToken cancellationToken)
    {
        await publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RoomChartSyncViewState view;
            lock (gate) view = CreateViewStateLocked();
            await AwaitHostAsync(
                    host.PublishViewStateAsync(view, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            publishGate.Release();
        }
    }

    private RoomChartSyncViewState CreateViewStateLocked()
    {
        var hasWorkDiagnostic = workErrorCode != null ||
            workErrorStatusCode != null ||
            workErrorMessage != null;
        var retryAt = nextAttemptDisplay ??
                      (IsWriteBudgetActiveLocked(timeProvider.GetTimestamp())
                          ? writeBudgetRetryAt
                          : null);
        return new RoomChartSyncViewState(
            workStatus,
            feedStatus,
            epoch,
            state.Generation,
            state.Revision,
            state.PendingWrite?.OperationId,
            workFailures,
            feedFailures,
            retryAt,
            statusHoldUntil,
            lastFeedRevision,
            hasWorkDiagnostic ? workErrorCode : feedErrorCode,
            hasWorkDiagnostic ? workErrorStatusCode : feedErrorStatusCode,
            hasWorkDiagnostic ? workErrorMessage : feedErrorMessage)
        {
            WorkErrorCode = workErrorCode,
            WorkErrorStatusCode = workErrorStatusCode,
            WorkErrorMessage = workErrorMessage,
            FeedErrorCode = feedErrorCode,
            FeedErrorStatusCode = feedErrorStatusCode,
            FeedErrorMessage = feedErrorMessage
        };
    }

    private Guid NextOperationId()
    {
        var operationId = operationIdSource();
        return operationId != Guid.Empty
            ? operationId
            : throw new InvalidOperationException(
                "The room chart operation ID source returned an empty identifier.");
    }

    private Task DefaultDelayAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, timeProvider, cancellationToken);

    private static TimeSpan DefaultJitter(int _) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(80, 280));

    private static DateTimeOffset AddClamped(DateTimeOffset value, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return value;
        var maximum = DateTimeOffset.MaxValue - value;
        return duration >= maximum ? DateTimeOffset.MaxValue : value + duration;
    }

    private long AddTimestamp(long timestamp, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return timestamp;
        var scaled = (Int128)duration.Ticks * timeProvider.TimestampFrequency / TimeSpan.TicksPerSecond;
        var available = (Int128)long.MaxValue - timestamp;
        return scaled >= available ? long.MaxValue : timestamp + (long)scaled;
    }

    /// <summary>
    /// Waits until an absolute monotonic deadline. The injected delay is relative, so
    /// the remaining time is taken from a clock read made immediately before arming
    /// it; a timer computed from an earlier read would fire late by however long the
    /// arming was delayed (for a stepped test clock: never, if the step came between).
    /// </summary>
    private Task DelayUntilAsync(long deadline, CancellationToken cancellationToken) =>
        delay(RemainingUntil(timeProvider.GetTimestamp(), deadline), cancellationToken);

    /// <summary>Time until a monotonic deadline, rounded up so a timer never fires early.</summary>
    private TimeSpan RemainingUntil(long now, long deadline)
    {
        if (deadline <= now) return TimeSpan.Zero;
        var frequency = timeProvider.TimestampFrequency;
        var ticks = ((Int128)deadline - now) * TimeSpan.TicksPerSecond;
        ticks = (ticks + frequency - 1) / frequency;
        return ticks >= TimeSpan.MaxValue.Ticks ? TimeSpan.MaxValue : TimeSpan.FromTicks((long)ticks);
    }

    private static void CancelWithoutThrow(CancellationTokenSource? cancellation)
    {
        if (cancellation == null) return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private readonly record struct WorkContext(
        long RunEpoch,
        long AttemptEpoch,
        CancellationToken CancellationToken);

    private sealed class PendingRollback(Guid snapshotId, Guid operationId)
    {
        public Guid SnapshotId { get; } = snapshotId;
        public Guid OperationId { get; } = operationId;
        public PreparedRoomChartRollbackAttempt? Attempt { get; set; }
        public TaskCompletionSource<RoomChartRollbackOutcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

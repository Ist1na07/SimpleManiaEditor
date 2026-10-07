using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LumaChart.Application.Collaboration;
using LumaChart.Core.Models;
using LumaChart.Online;

internal static class RoomChartSyncCoordinatorSpecs
{
    internal static readonly DateTimeOffset Origin =
        new(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await TestThrottleCoalescingPullAndFeedWakeAsync();
        await TestImmediateWakeCaptureStillHonorsLocalThrottleAsync();
        await TestStableRetryBudgetAndRecoveredStatusAsync();
        await TestBudgetDeadlineSurvivesStopStartAsync();
        await TestCommittedBacklogCompactionRecoveryAsync();
        await TestPermanentCapacityBlockAsync();
        await TestFeedReconnectAsync();
        await TestRollbackPreemptsCancellationIgnoringPushAsync();
        await TestStaleRollbackDoesNotDiscardPendingRecoveryAsync();
        await TestStopDetachesCancellationIgnoringTransportAsync();
    }

    private static async Task TestThrottleCoalescingPullAndFeedWakeAsync()
    {
        var baseline = CreateDocument("throttle-baseline");
        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock);
        var ids = new DeterministicOperationIds();
        var initialPull = new TaskCompletionSource<OnlineChartDelta>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PullHandler = (call, _, _) => call == 1
            ? initialPull.Task
            : Task.FromResult(EmptyDelta(transport.LastRequestedRevision));

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(() => transport.PullCalls.Count == 1 && transport.HasFeedListener,
            "coordinator did not begin its initial pull and SSE listener");

        host.AddLocalNote(NoteId(1), lane: 1, beat: 4);
        coordinator.NotifyLocalChange();
        coordinator.NotifyLocalChange();
        await transport.EmitRevisionAsync(17);
        coordinator.RequestSynchronization();

        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(39));
        Assert(transport.PushCalls.Count == 0,
            "a local write escaped before the fixed 40 ms throttle window");
        Assert(transport.MaximumConcurrentDataCalls == 1,
            "an SSE/local wake started a second REST operation during an active pull");
        Assert(host.AppliedPlans.Count == 0,
            "SSE applied chart data directly instead of acting only as a wake hint");

        initialPull.SetResult(EmptyDelta(0));
        await EventuallyAsync(
            () => coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Throttled,
            "the coalesced local change did not retain the original throttle deadline");
        coordinator.NotifyLocalChange();
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => transport.PushCalls.Count == 1 && coordinator.State.Revision == 1,
            "the local write did not run at the first 40 ms deadline");

        Assert(transport.PushCalls[0].StartedAt == Origin + TimeSpan.FromMilliseconds(40),
            "continuous notifications moved the 40 ms deadline like a debounce");
        Assert(transport.MaximumConcurrentDataCalls == 1,
            "coalesced pull/write work violated coordinator single-flight");
        Assert(transport.PushCalls.Count == 1 && transport.PullCalls.Count == 1,
            "local and SSE wake signals were not coalesced into one follow-up operation");

        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1959));
        Assert(transport.PullCalls.Count == 1,
            "the periodic pull ran before its 2 second interval");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => transport.PullCalls.Count == 2,
            "the 2 second safety pull did not wake synchronization");
    }

    private static async Task TestStableRetryBudgetAndRecoveredStatusAsync()
    {
        var baseline = CreateDocument("retry-baseline");
        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock);
        var ids = new DeterministicOperationIds();
        transport.PushHandler = (call, batch, _) => call switch
        {
            1 => Task.FromException<OnlineChartDelta>(new HttpRequestException("offline")),
            2 => Task.FromException<OnlineChartDelta>(new OnlineApiException(
                "write budget", "chart_write_budget", HttpStatusCode.TooManyRequests,
                TimeSpan.FromSeconds(3))),
            _ => Task.FromResult(Acknowledge(batch, clock.GetUtcNow()))
        };

        var options = TestOptions() with
        {
            RetryStatusTail = TimeSpan.FromSeconds(2),
            RecoveredStatusHold = TimeSpan.FromSeconds(8)
        };
        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids, options,
            failure => failure == 1 ? TimeSpan.FromMilliseconds(7) : TimeSpan.Zero);
        coordinator.Start();
        await EventuallyAsync(
            () => transport.PullCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
            "retry fixture did not finish its initial pull");

        host.AddLocalNote(NoteId(2), lane: 2, beat: 8);
        coordinator.NotifyLocalChange();
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => transport.PushCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.BackingOff,
            "transient write failure did not enter deterministic backoff");

        var first = transport.PushCalls[0];
        Assert(coordinator.State.PendingWrite?.OperationId == first.Batch.OperationId,
            "the ambiguous failure did not retain the installed pending write");
        Assert(coordinator.ViewState.RetryAt == clock.GetUtcNow() + TimeSpan.FromMilliseconds(407),
            "400 ms exponential retry plus injected jitter was not scheduled exactly");

        coordinator.RequestSynchronization();
        coordinator.NotifyLocalChange();
        await transport.EmitRevisionAsync(18);
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(406));
        Assert(transport.PushCalls.Count == 1,
            "a wake bypassed the active transient retry deadline");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(
            () => transport.PushCalls.Count == 2 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.WriteBudgetThrottled,
            "the exact retry did not surface server write-budget throttling");

        var second = transport.PushCalls[1];
        Assert(second.Batch.OperationId == first.Batch.OperationId &&
               SerializeBatch(second.Batch) == SerializeBatch(first.Batch),
            "ambiguous retry changed the serialized batch or OperationId");
        Assert(coordinator.ViewState.RetryAt == clock.GetUtcNow() + TimeSpan.FromSeconds(3) &&
               coordinator.ViewState.StatusHoldUntil ==
               clock.GetUtcNow() + TimeSpan.FromSeconds(5),
            "Retry-After or its visible status tail was not preserved exactly");

        coordinator.RequestSynchronization();
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(2999));
        Assert(transport.PushCalls.Count == 2,
            "a wake bypassed the server Retry-After deadline");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(
            () => transport.PushCalls.Count == 3 && coordinator.State.Revision == 1,
            "the write was not retried at the server Retry-After deadline");

        var third = transport.PushCalls[2];
        Assert(third.Batch.OperationId == first.Batch.OperationId &&
               SerializeBatch(third.Batch) == SerializeBatch(first.Batch),
            "write-budget retry did not reuse the exact pending request");
        Assert(coordinator.State.PendingWrite == null &&
               coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle &&
               coordinator.ViewState.StatusHoldUntil ==
               clock.GetUtcNow() + TimeSpan.FromSeconds(8),
            "successful recovery did not acknowledge pending state or hold recovered status");
    }

    private static async Task TestImmediateWakeCaptureStillHonorsLocalThrottleAsync()
    {
        var baseline = CreateDocument("capture-throttle-baseline");
        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock);
        var ids = new DeterministicOperationIds();

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(
            () => transport.PullCalls.Count == 1 && transport.HasFeedListener &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
            "capture-throttle fixture did not finish its initial pull");

        // An SSE hint starts immediate read work. While its local capture is blocked,
        // an editor change becomes visible to that capture and starts a 40 ms window.
        // The already-running read must not smuggle the edit into an immediate POST.
        var capture = host.BlockNextCapture();
        await transport.EmitRevisionAsync(31);
        await WithTimeoutAsync(capture.Entered.Task,
            "SSE work did not enter the controlled local capture");
        var localId = NoteId(10);
        host.AddLocalNote(localId, lane: 1, beat: 10);
        coordinator.NotifyLocalChange();
        capture.Release.TrySetResult();
        await DrainAsync();

        Assert(transport.PushCalls.Count == 0,
            "immediate SSE work POSTed an edit captured inside its new 40 ms window");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(39));
        Assert(transport.PushCalls.Count == 0,
            "capture-race write escaped before the original 40 ms deadline");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(
            () => transport.PushCalls.Count == 1 && coordinator.State.Revision == 1,
            "capture-race write did not run at the original 40 ms deadline");
        Assert(transport.PushCalls[0].StartedAt == Origin + TimeSpan.FromMilliseconds(40) &&
               transport.PushCalls[0].Batch.UpsertNotes.Single().Id == localId,
            "capture-race scheduling moved the deadline or lost the captured edit");
    }

    private static async Task TestBudgetDeadlineSurvivesStopStartAsync()
    {
        var baseline = CreateDocument("restart-budget-baseline");
        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock);
        var ids = new DeterministicOperationIds();
        transport.PushHandler = (call, batch, _) => call == 1
            ? Task.FromException<OnlineChartDelta>(new OnlineApiException(
                "write budget", "chart_write_budget", HttpStatusCode.TooManyRequests,
                TimeSpan.FromSeconds(3)))
            : Task.FromResult(Acknowledge(batch, clock.GetUtcNow()));

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(
            () => transport.PullCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
            "restart-budget fixture did not finish its initial pull");

        host.AddLocalNote(NoteId(11), lane: 2, beat: 11);
        coordinator.NotifyLocalChange();
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => transport.PushCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.WriteBudgetThrottled,
            "restart-budget fixture did not enter Retry-After");
        var first = transport.PushCalls[0];
        var retryAt = coordinator.ViewState.RetryAt;
        Assert(retryAt == clock.GetUtcNow() + TimeSpan.FromSeconds(3),
            "restart-budget fixture did not capture the expected deadline");

        await coordinator.StopAsync();
        coordinator.Start();
        await DrainAsync();
        Assert(transport.PushCalls.Count == 1 && coordinator.ViewState.RetryAt == retryAt,
            "Stop/Start cleared Retry-After or retried the pending write immediately");

        coordinator.RequestSynchronization();
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(2999));
        Assert(transport.PushCalls.Count == 1,
            "a restarted coordinator bypassed the retained Retry-After deadline");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(
            () => transport.PushCalls.Count == 2 &&
                  coordinator.State is { Revision: 1, PendingWrite: null },
            "the restarted coordinator did not retry at the retained deadline");
        var second = transport.PushCalls[1];
        Assert(second.Batch.OperationId == first.Batch.OperationId &&
               SerializeBatch(second.Batch) == SerializeBatch(first.Batch),
            "Stop/Start changed the pending batch or OperationId at Retry-After");
    }

    private static async Task TestPermanentCapacityBlockAsync()
    {
        var baseline = CreateDocument("capacity-baseline");
        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock)
        {
            PushHandler = (_, _, _) => Task.FromException<OnlineChartDelta>(
                new OnlineApiException(
                    "room full", "chart_room_capacity", HttpStatusCode.Conflict))
        };
        var ids = new DeterministicOperationIds();

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(
            () => transport.PullCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
            "capacity fixture did not finish its initial pull");

        var plansBeforeWrite = host.AppliedPlans.Count;
        var localId = NoteId(3);
        host.AddLocalNote(localId, lane: 3, beat: 12);
        coordinator.NotifyLocalChange();
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.PermanentlyBlocked,
            "chart_room_capacity did not permanently hard-stop synchronization");

        Assert(coordinator.State.PendingWrite == null &&
               host.Document.Notes.Any(note => note.Id == localId) &&
               host.AppliedPlans.Count == plansBeforeWrite,
            "capacity hard-stop lost local work or retained retryable transport state");
        var restCalls = transport.DataCallCount;
        var operationIdCalls = ids.CallCount;
        coordinator.NotifyLocalChange();
        coordinator.RequestSynchronization();
        await transport.EmitRevisionAsync(99);
        await delays.AdvanceAsync(TimeSpan.FromSeconds(10));
        await DrainAsync();
        Assert(transport.DataCallCount == restCalls && ids.CallCount == operationIdCalls,
            "permanent capacity hard-stop allowed another REST call or OperationId");
    }

    private static async Task TestCommittedBacklogCompactionRecoveryAsync()
    {
        var baseline = CreateDocument("compact-baseline");
        var submittedNoteId = NoteId(20);
        var postRequestNoteId = NoteId(21);
        var remoteNoteId = NoteId(22);
        var serverFull = baseline.Clone();
        serverFull.Notes.Add(new ChartNote
        {
            Id = submittedNoteId,
            Lane = 1,
            Beat = BeatPosition.FromDouble(20),
            OnlineCreatorUserId = 71
        });
        serverFull.Notes.Add(new ChartNote
        {
            Id = remoteNoteId,
            Lane = 2,
            Beat = BeatPosition.FromDouble(24),
            OnlineCreatorUserId = 88
        });
        serverFull.Normalize();

        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock);
        var ids = new DeterministicOperationIds();
        var firstPost = new TaskCompletionSource<OnlineChartDelta>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PushHandler = (call, batch, _) => call == 1
            ? firstPost.Task
            : Task.FromResult(Acknowledge(batch, clock.GetUtcNow()));
        transport.PullHandler = (call, revision, _) => call == 1
            ? Task.FromResult(EmptyDelta(revision))
            : Task.FromResult(new OnlineChartDelta(
                400,
                [],
                [],
                HasMore: false,
                RequiresFullSnapshot: true));
        transport.SnapshotHandler = (_, _) => Task.FromResult(Snapshot(serverFull, 400));

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(
            () => transport.PullCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
            "compact recovery fixture did not finish its initial pull");

        host.AddLocalNote(submittedNoteId, lane: 1, beat: 20);
        coordinator.NotifyLocalChange();
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(() => transport.PushCalls.Count == 1,
            "compact recovery fixture did not begin its initial POST");
        var committedBatch = transport.PushCalls[0].Batch;

        // This edit occurs after the request document was frozen. It must survive
        // snapshot recovery, then become a fresh write against revision 400.
        host.AddLocalNote(postRequestNoteId, lane: 3, beat: 28);
        coordinator.NotifyLocalChange();
        var firstPage = Enumerable.Range(1, 256)
            .Select(revision => new AppliedOnlineChartOperation(
                revision,
                900 + revision,
                new OnlineChartOperationBatch(
                    revision - 1,
                    [], [], [], [],
                    new Guid(revision, 0, 0, new byte[8])),
                clock.GetUtcNow()))
            .ToArray();
        firstPost.SetResult(new OnlineChartDelta(
            400, firstPage, [], HasMore: true));
        await EventuallyAsync(
            () => coordinator.State is
                  {
                      Revision: 256,
                      PendingWrite.CommitConfirmed: true
                  },
            "a successful first 256-operation page did not enter GET-only receipt chasing");

        Assert(transport.PushCalls.Count == 1 &&
               coordinator.State.PendingWrite?.OperationId == committedBatch.OperationId &&
               host.Document.Notes.Any(note => note.Id == submittedNoteId) &&
               host.Document.Notes.Any(note => note.Id == postRequestNoteId),
            "first-page acknowledgement was reposted or lost pending/post-request intent");

        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(39));
        Assert(transport.PullCalls.Count == 1 && transport.PushCalls.Count == 1,
            "receipt chasing bypassed the active local-change throttle");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(
            () => transport.PullCalls.Count == 2 && transport.SnapshotCalls == 1,
            "commit-confirmed state did not chase with GET and recover a compacted snapshot");
        await EventuallyAsync(() => transport.PushCalls.Count == 2,
            "post-request local intent was not prepared after compact recovery");

        var freshBatch = transport.PushCalls[1].Batch;
        Assert(transport.DataCallKinds.Take(5).SequenceEqual(
                   new[] { "GET", "POST", "GET", "SNAPSHOT", "POST" }) &&
               transport.PushCalls.Count(call =>
                   call.Batch.OperationId == committedBatch.OperationId) == 1 &&
               freshBatch.OperationId != committedBatch.OperationId &&
               freshBatch.BaseRevision == 400 &&
               freshBatch.UpsertNotes.Select(note => note.Id)
                   .SequenceEqual(new[] { postRequestNoteId }) &&
               host.Document.Notes.Any(note => note.Id == submittedNoteId) &&
               host.Document.Notes.Any(note => note.Id == remoteNoteId) &&
               host.Document.Notes.Any(note => note.Id == postRequestNoteId),
            "compact recovery repeated the committed POST or failed to retain only post-request intent");
        await EventuallyAsync(
            () => coordinator.State is { Revision: 401, PendingWrite: null },
            "fresh post-recovery local write did not settle");
    }

    private static async Task TestFeedReconnectAsync()
    {
        var baseline = CreateDocument("feed-baseline");
        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock)
        {
            FeedHandler = (call, _, token) => call == 1
                ? Task.FromException(new IOException("feed disconnected"))
                : Task.Delay(Timeout.InfiniteTimeSpan, token)
        };
        var ids = new DeterministicOperationIds();

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(
            () => transport.FeedCalls == 1 &&
                  coordinator.ViewState.FeedStatus == RoomChartSyncFeedStatus.Reconnecting,
            "a failed SSE connection did not enter reconnect state");
        await EventuallyAsync(() => transport.PullCalls.Count == 1,
            "feed fixture did not finish its initial safety pull");

        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(399));
        Assert(transport.FeedCalls == 1,
            "SSE reconnected before its first 400 ms retry delay");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => transport.FeedCalls == 2 && transport.HasFeedListener,
            "SSE did not reconnect after its deterministic retry delay");

        var pullsBeforeHint = transport.PullCalls.Count;
        var plansBeforeHint = host.AppliedPlans.Count;
        await transport.EmitRevisionAsync(27);
        await EventuallyAsync(() => transport.PullCalls.Count == pullsBeforeHint + 1,
            "an SSE revision hint did not wake a REST delta pull");
        Assert(coordinator.ViewState.FeedStatus == RoomChartSyncFeedStatus.Connected &&
               coordinator.ViewState.ConsecutiveFeedFailures == 0 &&
               coordinator.ViewState.LastFeedRevision == 27 &&
               host.AppliedPlans.Count == plansBeforeHint + 1,
            "SSE reconnect state was not recovered or the feed applied data directly");
    }

    private static async Task TestRollbackPreemptsCancellationIgnoringPushAsync()
    {
        var baseline = CreateDocument("rollback-baseline");
        var restored = CreateDocument("authoritative-rollback");
        restored.KeyCount = 6;
        restored.Notes.Add(new ChartNote
        {
            Id = NoteId(40), Lane = 5, Beat = BeatPosition.FromDouble(16),
            OnlineCreatorUserId = 1
        });
        restored.Normalize();

        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock);
        var ids = new DeterministicOperationIds();
        var hungPush = new TaskCompletionSource<OnlineChartDelta>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PushHandler = (_, _, _) => hungPush.Task; // deliberately ignores cancellation
        var snapshotId = Guid.Parse("a5000000-0000-0000-0000-000000000001");
        var safetyId = Guid.Parse("a5000000-0000-0000-0000-000000000002");
        transport.RollbackHandler = (call, requestedSnapshotId, _, _) => call == 1
            ? Task.FromException<OnlineChartRollbackResult>(new HttpRequestException("lost ACK"))
            : Task.FromResult(new OnlineChartRollbackResult(
                Snapshot(restored, revision: 10),
                new OnlineChartSavedSnapshot(
                    requestedSnapshotId, "restored", 7, 1, "Owner",
                    restored.Notes.Count, restored.Timing.Count, false, clock.GetUtcNow()),
                safetyId));

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(
            () => transport.PullCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
            "rollback fixture did not finish its initial pull");

        host.AddLocalNote(NoteId(4), lane: 0, beat: 14);
        coordinator.NotifyLocalChange();
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(() => transport.PushCalls.Count == 1,
            "rollback fixture did not enter its cancellation-ignoring push");
        var lateBatch = transport.PushCalls[0].Batch;

        var rollbackTask = coordinator.RollbackAsync(snapshotId);
        await EventuallyAsync(
            () => transport.RollbackCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.BackingOff,
            "rollback did not preempt the cancellation-ignoring push");
        var rollbackOperationId = transport.RollbackCalls[0].OperationId;
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(399));
        Assert(transport.RollbackCalls.Count == 1,
            "ambiguous rollback retried before its backoff deadline");
        await delays.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => transport.RollbackCalls.Count == 2,
            "ambiguous rollback was not retried");
        Assert(transport.RollbackCalls[1].OperationId == rollbackOperationId,
            "ambiguous rollback retry changed its OperationId");

        var outcome = await WithTimeoutAsync(rollbackTask,
            "authoritative rollback did not complete after preempting the push");
        Assert(!outcome.IgnoredStaleResponse && outcome.OperationId == rollbackOperationId &&
               outcome.SafetySnapshotId == safetyId &&
               coordinator.State is { Generation: 1, Revision: 10, PendingWrite: null } &&
               host.Document.Metadata.Title == "authoritative-rollback" &&
               host.AppliedPlans.Count(plan => plan.ClearHistory) == 1,
            "rollback did not install exactly one authoritative generation");

        var plansAfterRollback = host.AppliedPlans.Count;
        hungPush.SetResult(Acknowledge(lateBatch, clock.GetUtcNow()));
        await DrainAsync();
        Assert(coordinator.State is { Generation: 1, Revision: 10, PendingWrite: null } &&
               host.Document.Metadata.Title == "authoritative-rollback" &&
               host.AppliedPlans.Count == plansAfterRollback,
            "the late pre-rollback push response mutated the authoritative generation");
    }

    private static async Task TestStopDetachesCancellationIgnoringTransportAsync()
    {
        var baseline = CreateDocument("stop-baseline");
        var remote = baseline.Clone();
        var lateNoteId = NoteId(50);
        remote.Notes.Add(new ChartNote
        {
            Id = lateNoteId,
            Lane = 1,
            Beat = BeatPosition.FromDouble(30),
            OnlineCreatorUserId = 90
        });
        remote.Normalize();
        var lateBatch = OnlineChartSyncCodec.CreateBatch(
            0,
            baseline,
            remote,
            Guid.Parse("e5000000-0000-0000-0000-000000000001"));

        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock);
        var ids = new DeterministicOperationIds();
        var hungPull = new TaskCompletionSource<OnlineChartDelta>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PullHandler = (_, _, _) => hungPull.Task; // deliberately ignores cancellation

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(() => transport.PullCalls.Count == 1,
            "stop fixture did not enter its cancellation-ignoring pull");
        var plansBeforeStop = host.AppliedPlans.Count;

        try
        {
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "StopAsync waited for a transport that ignored cancellation");
        }

        Assert(coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Stopped &&
               coordinator.ViewState.FeedStatus == RoomChartSyncFeedStatus.Stopped &&
               coordinator.State.Revision == 0 &&
               host.AppliedPlans.Count == plansBeforeStop,
            "StopAsync did not publish stopped state without mutating the document");

        hungPull.SetResult(Acknowledge(lateBatch, clock.GetUtcNow()));
        await DrainAsync();
        Assert(coordinator.State.Revision == 0 &&
               !host.Document.Notes.Any(note => note.Id == lateNoteId) &&
               host.AppliedPlans.Count == plansBeforeStop,
            "a late response from the detached stopped transport mutated coordinator state");
    }

    private static async Task TestStaleRollbackDoesNotDiscardPendingRecoveryAsync()
    {
        var baseline = CreateDocument("recovery-baseline");
        var recovered = CreateDocument("recovered-full-snapshot");
        var recoveredNoteId = NoteId(60);
        recovered.Notes.Add(new ChartNote
        {
            Id = recoveredNoteId,
            Lane = 2,
            Beat = BeatPosition.FromDouble(32),
            OnlineCreatorUserId = 91
        });
        recovered.Normalize();
        var late = CreateDocument("late-preempted-snapshot");

        var clock = new FakeTimeProvider(Origin);
        var delays = new FakeDelayScheduler(clock);
        var host = new FakeDocumentHost(baseline);
        var transport = new FakeRoomChartTransport(clock);
        var ids = new DeterministicOperationIds();
        var firstSnapshot = new TaskCompletionSource<OnlineChartSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PullHandler = (call, revision, _) => call == 1
            ? Task.FromResult(EmptyDelta(revision))
            : Task.FromResult(new OnlineChartDelta(
                5, [], [], HasMore: false, RequiresFullSnapshot: true));
        transport.SnapshotHandler = (call, _) => call == 1
            ? firstSnapshot.Task // deliberately ignores cancellation
            : Task.FromResult(Snapshot(recovered, 5));
        var rollbackSnapshotId = Guid.Parse("f5000000-0000-0000-0000-000000000001");
        transport.RollbackHandler = (_, requestedSnapshotId, _, _) => Task.FromResult(
            new OnlineChartRollbackResult(
                Snapshot(baseline, 0),
                new OnlineChartSavedSnapshot(
                    requestedSnapshotId, "stale", 0, 1, "Owner",
                    0, baseline.Timing.Count, false, clock.GetUtcNow()),
                Guid.Parse("f5000000-0000-0000-0000-000000000002")));

        await using var coordinator = CreateCoordinator(
            baseline, transport, host, clock, delays, ids);
        coordinator.Start();
        await EventuallyAsync(
            () => transport.PullCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
            "pending-recovery fixture did not finish its initial pull");

        coordinator.RequestSynchronization();
        await EventuallyAsync(() => transport.SnapshotCalls == 1,
            "compact signal did not begin pending full-snapshot recovery");
        var rollback = await WithTimeoutAsync(
            coordinator.RollbackAsync(rollbackSnapshotId),
            "stale rollback did not preempt pending snapshot recovery");
        Assert(rollback.IgnoredStaleResponse,
            "rollback fixture response was not recognized as stale");
        await EventuallyAsync(
            () => transport.SnapshotCalls == 2 &&
                  coordinator.State is { Revision: 5, Generation: 0 },
            "stale rollback discarded the pending recovery token instead of resuming it");
        Assert(host.Document.Metadata.Title == "recovered-full-snapshot" &&
               host.Document.Notes.Any(note => note.Id == recoveredNoteId) &&
               transport.DataCallKinds.Take(5).SequenceEqual(
                   new[] { "GET", "GET", "SNAPSHOT", "ROLLBACK", "SNAPSHOT" }),
            "resumed recovery did not install the authoritative full snapshot");

        var plansAfterRecovery = host.AppliedPlans.Count;
        firstSnapshot.SetResult(Snapshot(late, 99));
        await DrainAsync();
        Assert(coordinator.State is { Revision: 5, Generation: 0 } &&
               host.Document.Metadata.Title == "recovered-full-snapshot" &&
               host.AppliedPlans.Count == plansAfterRecovery,
            "the late preempted recovery response overwrote resumed recovery");
    }

    internal static RoomChartSyncCoordinator CreateCoordinator(
        ChartDocument baseline,
        FakeRoomChartTransport transport,
        FakeDocumentHost host,
        FakeTimeProvider clock,
        FakeDelayScheduler delays,
        DeterministicOperationIds ids,
        RoomChartSyncCoordinatorOptions? options = null,
        RoomChartSyncJitter? jitter = null) =>
        new(
            RoomChartSyncState.Create(0, baseline),
            transport,
            host,
            options ?? TestOptions(),
            clock,
            delays.DelayAsync,
            jitter ?? (_ => TimeSpan.Zero),
            ids.Next);

    internal static RoomChartSyncCoordinatorOptions TestOptions() => new()
    {
        WriteThrottle = TimeSpan.FromMilliseconds(40),
        PullInterval = TimeSpan.FromSeconds(2),
        InitialRetryDelay = TimeSpan.FromMilliseconds(400),
        MaximumRetryDelay = TimeSpan.FromSeconds(8),
        RecoveredStatusHold = TimeSpan.FromSeconds(8),
        RetryStatusTail = TimeSpan.FromMilliseconds(1200)
    };

    internal static ChartDocument CreateDocument(string title)
    {
        var document = new ChartDocument
        {
            KeyCount = 4,
            Metadata = new ChartMetadata
            {
                Title = title,
                Artist = "Spec Artist",
                Creator = "Spec Creator",
                Version = "Spec"
            },
            Timing =
            [
                new TimingEvent
                {
                    Id = Guid.Parse("b0000000-0000-0000-0000-000000000001"),
                    Beat = BeatPosition.Zero,
                    Bpm = 120
                }
            ]
        };
        document.Normalize();
        return document;
    }

    internal static Guid NoteId(int value) =>
        Guid.Parse($"c0000000-0000-0000-0000-{value:X12}");

    internal static OnlineChartDelta EmptyDelta(long revision) =>
        new(revision, [], [], false);

    internal static OnlineChartDelta Acknowledge(
        OnlineChartOperationBatch batch,
        DateTimeOffset now) =>
        new(
            batch.BaseRevision + 1,
            [new AppliedOnlineChartOperation(batch.BaseRevision + 1, 71, batch, now)],
            [],
            false);

    internal static OnlineChartSnapshot Snapshot(ChartDocument document, long revision)
    {
        var initialize = OnlineChartSyncCodec.CreateInitializeRequest(document);
        return new OnlineChartSnapshot(
            revision,
            initialize.KeyCount,
            initialize.Notes,
            initialize.Timing,
            [],
            initialize.Metadata);
    }

    internal static string SerializeBatch(OnlineChartOperationBatch batch) =>
        JsonSerializer.Serialize(batch, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static async Task EventuallyAsync(Func<bool> condition, string message)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(4))
                throw new InvalidOperationException(message);
            await Task.Delay(1);
        }
    }

    internal static async Task<T> WithTimeoutAsync<T>(Task<T> task, string message)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static async Task WithTimeoutAsync(Task task, string message)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static async Task DrainAsync()
    {
        for (var index = 0; index < 20; index++)
            await Task.Yield();
        await Task.Delay(1);
    }

    internal static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal sealed class FakeTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        // The last timestamp each thread read from which clock. See ArmingTimestamp.
        [ThreadStatic] private static FakeTimeProvider? lastReader;
        [ThreadStatic] private static long lastReadTicks;

        private readonly object gate = new();
        private DateTimeOffset now = initial;
        private long monotonicTicks;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate) return now;
        }

        // The coordinator schedules against the monotonic timestamp; drive it from
        // the same fake advance so deadlines stay deterministic.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            long value;
            lock (gate) value = monotonicTicks;
            lastReader = this;
            lastReadTicks = value;
            return value;
        }

        /// <summary>
        /// The instant a relative delay armed on this thread is measured from: the
        /// clock value the arming code last read here. The coordinator computes every
        /// delay from a read taken just before arming it; on a real clock the two are
        /// nanoseconds apart, but a test can step this clock in between, which used to
        /// anchor the timer one whole step late and leave it unfired.
        /// </summary>
        public long ArmingTimestamp() =>
            ReferenceEquals(lastReader, this) ? lastReadTicks : GetTimestamp();

        /// <summary>Moves only the wall clock, as a system time correction does.</summary>
        public void SetWallClock(DateTimeOffset value)
        {
            lock (gate) now = value;
        }

        public void Advance(TimeSpan amount)
        {
            if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            lock (gate)
            {
                now += amount;
                monotonicTicks += amount.Ticks;
            }
        }
    }

    internal sealed class FakeDelayScheduler(FakeTimeProvider clock)
    {
        private readonly object gate = new();
        private readonly List<ScheduledDelay> scheduled = [];

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            if (duration <= TimeSpan.Zero) return Task.CompletedTask;
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            // Like Task.Delay, fake delays elapse on the monotonic clock, so a wall
            // clock correction does not move them.
            var entry = new ScheduledDelay(clock.ArmingTimestamp() + duration.Ticks, cancellationToken);
            bool alreadyDue;
            lock (gate)
            {
                scheduled.Add(entry);
                // AdvanceAsync moves the clock before it scans under this lock. A delay
                // added after that scan but already due would otherwise never fire.
                alreadyDue = entry.Deadline <= clock.GetTimestamp();
                if (alreadyDue) scheduled.Remove(entry);
            }
            if (alreadyDue)
            {
                entry.Complete();
                return entry.Task;
            }
            entry.RegisterCancellation();
            return entry.Task;
        }

        public int PendingCount
        {
            get { lock (gate) return scheduled.Count(item => !item.Task.IsCompleted); }
        }

        public async Task AdvanceAsync(TimeSpan duration)
        {
            clock.Advance(duration);
            for (var pass = 0; pass < 40; pass++)
            {
                List<ScheduledDelay> due;
                lock (gate)
                {
                    due = scheduled
                        .Where(item => !item.Task.IsCompleted && item.Deadline <= clock.GetTimestamp())
                        .ToList();
                    scheduled.RemoveAll(item => item.Task.IsCompleted || due.Contains(item));
                }
                foreach (var entry in due) entry.Complete();
                await Task.Yield();
                if (due.Count == 0) break;
            }
            await DrainAsync();
        }

        private sealed class ScheduledDelay(
            long deadline,
            CancellationToken cancellationToken)
        {
            private readonly TaskCompletionSource completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenRegistration registration;

            public long Deadline { get; } = deadline;
            public Task Task => completion.Task;

            public void RegisterCancellation() =>
                registration = cancellationToken.Register(
                    () => completion.TrySetCanceled(cancellationToken));

            public void Complete()
            {
                registration.Dispose();
                completion.TrySetResult();
            }
        }
    }

    internal sealed class DeterministicOperationIds
    {
        private int calls;
        public int CallCount => Volatile.Read(ref calls);

        public Guid Next()
        {
            var value = Interlocked.Increment(ref calls);
            return Guid.Parse($"d0000000-0000-0000-0000-{value:X12}");
        }
    }

    internal sealed class FakeDocumentHost(ChartDocument initial) : IRoomChartSyncDocumentHost
    {
        private readonly object gate = new();
        private ChartDocument document = initial.Clone();
        private long changeSerial;
        private readonly List<RoomChartApplyPlan> appliedPlans = [];
        private readonly List<RoomChartSyncViewState> viewStates = [];
        private readonly List<RoomChartRejectedLocalChanges> preservedRejections = [];
        private CaptureBlock? nextCaptureBlock;
        private Action? nextBeforeApply;
        private int staleRejections;

        public ChartDocument Document
        {
            get { lock (gate) return document.Clone(); }
        }

        public IReadOnlyList<RoomChartApplyPlan> AppliedPlans
        {
            get { lock (gate) return appliedPlans.ToArray(); }
        }

        public IReadOnlyList<RoomChartSyncViewState> ViewStates
        {
            get { lock (gate) return viewStates.ToArray(); }
        }

        public void AddLocalNote(Guid id, int lane, double beat)
        {
            lock (gate)
            {
                document.Notes.Add(new ChartNote
                {
                    Id = id,
                    Lane = lane,
                    Beat = BeatPosition.FromDouble(beat)
                });
                document.Normalize();
                changeSerial++;
            }
        }

        public CaptureBlock BlockNextCapture()
        {
            lock (gate)
            {
                if (nextCaptureBlock != null)
                    throw new InvalidOperationException("a fake capture is already blocked");
                nextCaptureBlock = new CaptureBlock();
                return nextCaptureBlock;
            }
        }

        public async ValueTask<RoomChartLocalSnapshot> CaptureLocalSnapshotAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureBlock? blocked;
            lock (gate)
            {
                blocked = nextCaptureBlock;
                nextCaptureBlock = null;
            }
            if (blocked != null)
            {
                blocked.Entered.TrySetResult();
                await blocked.Release.Task.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return new RoomChartLocalSnapshot(document, changeSerial);
        }

        public ValueTask ApplyPlanAsync(
            RoomChartApplyPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Action? beforeApply;
            lock (gate)
            {
                beforeApply = nextBeforeApply;
                nextBeforeApply = null;
            }
            // Simulates an editor commit landing between the off-thread merge and
            // the UI-thread apply.
            beforeApply?.Invoke();
            lock (gate)
            {
                if (EnforceLocalChangeSerial && plan.ApplyDocument && !plan.ClearHistory &&
                    plan.LocalChangeSerial is { } expected && expected != changeSerial)
                {
                    staleRejections++;
                    throw new RoomChartStaleLocalSnapshotException(expected, changeSerial);
                }
                appliedPlans.Add(plan);
                if (plan.ApplyDocument) document = plan.Document;
            }
            return ValueTask.CompletedTask;
        }

        /// <summary>Mirrors the Avalonia host's serial check before a document replace.</summary>
        public bool EnforceLocalChangeSerial { get; set; }

        public int StaleRejections
        {
            get { lock (gate) return staleRejections; }
        }

        public IReadOnlyList<RoomChartRejectedLocalChanges> PreservedRejections
        {
            get { lock (gate) return preservedRejections.ToArray(); }
        }

        public void RunBeforeNextApply(Action action)
        {
            lock (gate) nextBeforeApply = action;
        }

        public void MutateLocal(Action<ChartDocument> mutation)
        {
            lock (gate)
            {
                mutation(document);
                document.Normalize();
                changeSerial++;
            }
        }

        public ValueTask PreserveRejectedLocalChangesAsync(
            RoomChartRejectedLocalChanges rejected,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) preservedRejections.Add(rejected);
            return ValueTask.CompletedTask;
        }

        public ValueTask PublishViewStateAsync(
            RoomChartSyncViewState viewState,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) viewStates.Add(viewState);
            return ValueTask.CompletedTask;
        }

        public sealed class CaptureBlock
        {
            public TaskCompletionSource Entered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal sealed class FakeRoomChartTransport(FakeTimeProvider clock) : IRoomChartSyncTransport
    {
        private readonly object gate = new();
        private int activeDataCalls;
        private int maximumConcurrentDataCalls;
        private Func<long, Task>? feedListener;
        private readonly List<PullCall> pullCalls = [];
        private readonly List<PushCall> pushCalls = [];
        private readonly List<RollbackCall> rollbackCalls = [];
        private readonly List<string> dataCallKinds = [];
        private int snapshotCalls;
        private int feedCalls;

        public Func<int, long, CancellationToken, Task<OnlineChartDelta>> PullHandler { get; set; } =
            (_, revision, _) => Task.FromResult(EmptyDelta(revision));
        public Func<int, OnlineChartOperationBatch, CancellationToken, Task<OnlineChartDelta>> PushHandler
            { get; set; } = (_, batch, _) => Task.FromResult(Acknowledge(batch, Origin));
        public Func<int, CancellationToken, Task<OnlineChartSnapshot>> SnapshotHandler { get; set; } =
            (_, _) => Task.FromException<OnlineChartSnapshot>(
                new InvalidOperationException("unexpected snapshot request"));
        public Func<int, Guid, Guid, CancellationToken, Task<OnlineChartRollbackResult>> RollbackHandler
            { get; set; } = (_, _, _, _) => Task.FromException<OnlineChartRollbackResult>(
                new InvalidOperationException("unexpected rollback request"));
        public Func<int, Func<long, Task>, CancellationToken, Task> FeedHandler { get; set; } =
            (_, _, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);

        public IReadOnlyList<PullCall> PullCalls
        {
            get { lock (gate) return pullCalls.ToArray(); }
        }
        public IReadOnlyList<PushCall> PushCalls
        {
            get { lock (gate) return pushCalls.ToArray(); }
        }
        public IReadOnlyList<RollbackCall> RollbackCalls
        {
            get { lock (gate) return rollbackCalls.ToArray(); }
        }
        public IReadOnlyList<string> DataCallKinds
        {
            get { lock (gate) return dataCallKinds.ToArray(); }
        }
        public int SnapshotCalls => Volatile.Read(ref snapshotCalls);
        public int FeedCalls => Volatile.Read(ref feedCalls);
        public int MaximumConcurrentDataCalls => Volatile.Read(ref maximumConcurrentDataCalls);
        public int DataCallCount =>
            PullCalls.Count + PushCalls.Count + SnapshotCalls + RollbackCalls.Count;
        public bool HasFeedListener { get { lock (gate) return feedListener != null; } }
        public long LastRequestedRevision
        {
            get { lock (gate) return pullCalls.Count == 0 ? 0 : pullCalls[^1].AfterRevision; }
        }

        public Task<OnlineChartDelta> GetDeltaAsync(
            long afterRevision,
            CancellationToken cancellationToken) => TrackDataAsync(async () =>
        {
            int call;
            lock (gate)
            {
                call = pullCalls.Count + 1;
                pullCalls.Add(new PullCall(afterRevision, clock.GetUtcNow()));
                dataCallKinds.Add("GET");
            }
            return await PullHandler(call, afterRevision, cancellationToken);
        });

        public Task<OnlineChartDelta> ApplyOperationsDeltaAsync(
            OnlineChartOperationBatch batch,
            CancellationToken cancellationToken) => TrackDataAsync(async () =>
        {
            int call;
            lock (gate)
            {
                call = pushCalls.Count + 1;
                pushCalls.Add(new PushCall(batch, clock.GetUtcNow()));
                dataCallKinds.Add("POST");
            }
            return await PushHandler(call, batch, cancellationToken);
        });

        public Task<OnlineChartSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken) => TrackDataAsync(async () =>
        {
            var call = Interlocked.Increment(ref snapshotCalls);
            lock (gate) dataCallKinds.Add("SNAPSHOT");
            return await SnapshotHandler(call, cancellationToken);
        });

        public Task<OnlineChartRollbackResult> RollbackAsync(
            Guid snapshotId,
            Guid operationId,
            CancellationToken cancellationToken) => TrackDataAsync(async () =>
        {
            int call;
            lock (gate)
            {
                call = rollbackCalls.Count + 1;
                rollbackCalls.Add(new RollbackCall(
                    snapshotId, operationId, clock.GetUtcNow()));
                dataCallKinds.Add("ROLLBACK");
            }
            return await RollbackHandler(call, snapshotId, operationId, cancellationToken);
        });

        public Task ListenForRevisionsAsync(
            Func<long, Task> revisionAvailable,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref feedCalls);
            lock (gate) feedListener = revisionAvailable;
            return FeedHandler(call, revisionAvailable, cancellationToken);
        }

        public Task EmitRevisionAsync(long revision)
        {
            Func<long, Task>? listener;
            lock (gate) listener = feedListener;
            return listener?.Invoke(revision) ?? throw new InvalidOperationException(
                "the fake SSE listener is not connected");
        }

        private async Task<T> TrackDataAsync<T>(Func<Task<T>> action)
        {
            var active = Interlocked.Increment(ref activeDataCalls);
            UpdateMaximum(active);
            try
            {
                return await action();
            }
            finally
            {
                Interlocked.Decrement(ref activeDataCalls);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximumConcurrentDataCalls);
                if (value <= current ||
                    Interlocked.CompareExchange(ref maximumConcurrentDataCalls, value, current) == current)
                    return;
            }
        }
    }

    internal sealed record PullCall(long AfterRevision, DateTimeOffset StartedAt);
    internal sealed record PushCall(OnlineChartOperationBatch Batch, DateTimeOffset StartedAt);
    internal sealed record RollbackCall(
        Guid SnapshotId,
        Guid OperationId,
        DateTimeOffset StartedAt);
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LumaChart.Application.Collaboration;
using LumaChart.Core.Editing;
using LumaChart.Core.Models;
using LumaChart.Online;
using LumaChart.Storage;
using static RoomChartSyncCoordinatorSpecs;

/// <summary>
/// Regression specs for the client networking and room-sync fixes: no local edit
/// is lost to a deterministic rejection, reads continue during write throttling,
/// deadlines are monotonic, stale responses cannot write back after logout, and
/// malformed or partial server data never escapes as an unhandled exception.
/// </summary>
public static class SyncFixSpecs
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:5080/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid RoomId = Guid.Parse("5f000000-0000-0000-0000-000000000001");

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        TestOversizedWritesSplitWithinServerLimits();
        TestPermissionRejectionDropsOnlyRefusedCategory();
        TestFieldLevelMergeAndDeleteWinsPolicy();
        await TestDefinitiveRejectionPreservesRecoverableCopyAsync();
        await TestSelectionLockedRetriesUnlockedEditsAsync();
        await TestUnauthorizedWriteKeepsLocalWorkAsync();
        await TestUnusableWriteResponseIsRetriedAsync();
        await TestWriteBudgetKeepsPullingAndMergingAsync();
        await TestWallClockRollbackDoesNotFreezeRetriesAsync();
        await TestHungTransportTimesOutAndRetriesAsync();
        await TestStaleLocalSnapshotIsMergedAgainAsync();
        await TestFeedRetriesRecoverableErrorsOnlyAsync();
        await TestEventStreamIdleWatchdogAsync();
        await TestOnlyExplicitRoomCodesEndMembershipAsync();
        await TestLateResultsAfterLogoutAreDiscardedAsync();
        await TestRestoreUnauthorizedKeepsNewerLoginAsync();
        await TestServerExpiryIsNotJudgedByLocalClockAsync();
        await TestInvalidSuccessBodiesBecomeOnlineErrorsAsync();
        await TestNullDtoFieldsAreNormalizedAsync();
        await TestResourceCommitAfterLeaveDoesNotThrowAsync();
    }

    private static void TestOversizedWritesSplitWithinServerLimits()
    {
        // Item-count limit: more notes than one server batch accepts.
        var baseline = CreateDocument("split-baseline");
        var local = baseline.Clone();
        local.Metadata.Title = "split-local";
        const int noteCount = RoomChartSyncReducer.MaximumBatchItems + 500;
        for (var index = 0; index < noteCount; index++)
            local.Notes.Add(new ChartNote
            {
                Id = SequentialId(0x51, index),
                Lane = index % 4,
                Beat = new BeatPosition(index, 0, 1)
            });
        local.Normalize();
        var state = RoomChartSyncState.Create(10, baseline);
        var first = RoomChartSyncReducer.PrepareAttempt(
            state, local, currentChangeSerial: 5, SequentialId(0x52, 1));
        var firstBatch = first.Attempt.Batch;
        Require(first.Attempt is { IsWrite: true, OmitsLocalChanges: true } &&
               ItemCount(firstBatch) <= RoomChartSyncReducer.MaximumBatchItems &&
               firstBatch.Metadata != null &&
               first.State.PendingWrite?.OmitsLocalChanges == true,
            "an over-limit write was not reduced to a leading part within the server item limit");

        var acknowledgement = new OnlineChartDelta(
            11, [new AppliedOnlineChartOperation(11, 7, firstBatch, Origin)], [], false);
        var plan = RoomChartSyncReducer.ReduceDelta(
                first.State, first.Attempt, acknowledgement, local, currentChangeSerial: 5)
            .ApplyPlan ?? throw new InvalidOperationException("split acknowledgement produced no plan");
        Require(plan.PendingWriteAcknowledged && plan.HasConcurrentLocalChanges &&
               plan.ShouldRequestAnotherSync && plan.LocalChangeSerial == 5 &&
               plan.State is { Revision: 11, PendingWrite: null } &&
               plan.Document.Notes.Count == noteCount &&
               plan.Document.Metadata.Title == "split-local",
            "acknowledging the first part lost the unsent remainder of the local edits");

        var second = RoomChartSyncReducer.PrepareAttempt(
            plan.State, plan.Document, currentChangeSerial: 5, SequentialId(0x52, 2));
        var sent = firstBatch.UpsertNotes.Concat(second.Attempt.Batch.UpsertNotes)
            .Select(note => note.Id)
            .ToArray();
        Require(second.Attempt is { IsWrite: true, OmitsLocalChanges: false } &&
               second.Attempt.Batch.Metadata == null &&
               sent.Length == noteCount && sent.Distinct().Count() == noteCount,
            "the follow-up write did not send exactly the notes the first part omitted");

        // Byte limit: few items, but large extension data per note.
        var heavy = baseline.Clone();
        for (var index = 0; index < 4_000; index++)
            heavy.Notes.Add(new ChartNote
            {
                Id = SequentialId(0x53, index),
                Lane = index % 4,
                Beat = new BeatPosition(index, 0, 1),
                UnknownData = new JsonObject { ["blob"] = new string('x', 5_000) }
            });
        heavy.Normalize();
        var heavyAttempt = RoomChartSyncReducer.PrepareAttempt(
            RoomChartSyncState.Create(0, baseline), heavy, 1, SequentialId(0x52, 3)).Attempt;
        Require(heavyAttempt.OmitsLocalChanges &&
               JsonSerializer.SerializeToUtf8Bytes(heavyAttempt.Batch, JsonOptions).LongLength <=
               RoomChartSyncReducer.MaximumBatchRequestBytes,
            "a write above the 16 MiB request limit was not split by size");

        // A shrinking key count waits for the final part; a growing one leads.
        var wide = baseline.Clone();
        wide.KeyCount = 7;
        wide.Normalize();
        var narrowed = wide.Clone();
        narrowed.KeyCount = 4;
        for (var index = 0; index < RoomChartSyncReducer.MaximumBatchItems + 10; index++)
            narrowed.Notes.Add(new ChartNote
            {
                Id = SequentialId(0x54, index),
                Lane = index % 4,
                Beat = new BeatPosition(index, 0, 1)
            });
        narrowed.Normalize();
        var shrinkFirst = RoomChartSyncReducer.PrepareAttempt(
            RoomChartSyncState.Create(0, wide), narrowed, 1, SequentialId(0x52, 4)).Attempt;
        Require(shrinkFirst.OmitsLocalChanges && shrinkFirst.Batch.KeyCount == null,
            "a shrinking key count was sent before the notes that must fit it");
        var grown = baseline.Clone();
        grown.KeyCount = 7;
        for (var index = 0; index < RoomChartSyncReducer.MaximumBatchItems + 10; index++)
            grown.Notes.Add(new ChartNote
            {
                Id = SequentialId(0x55, index),
                Lane = 4 + index % 3,
                Beat = new BeatPosition(index, 0, 1)
            });
        grown.Normalize();
        var growFirst = RoomChartSyncReducer.PrepareAttempt(
            RoomChartSyncState.Create(0, baseline), grown, 1, SequentialId(0x52, 5)).Attempt;
        Require(growFirst.OmitsLocalChanges && growFirst.Batch.KeyCount == 7,
            "a growing key count did not precede notes placed in the new lanes");
    }

    private static void TestPermissionRejectionDropsOnlyRefusedCategory()
    {
        var baseline = CreateDocument("permission-baseline");
        var local = baseline.Clone();
        var noteId = NoteId(0x601);
        local.Notes.Add(new ChartNote { Id = noteId, Lane = 1, Beat = BeatPosition.FromDouble(4) });
        local.Timing.Single().Bpm = 150;
        local.Normalize();
        var prepared = RoomChartSyncReducer.PrepareAttempt(
            RoomChartSyncState.Create(2, baseline), local, 3, SequentialId(0x60, 1));
        var scope = RoomChartSyncReducer.ClassifyRejectedChanges("note_permission_required");
        var recovery = RoomChartSyncReducer.PrepareRejectedAttemptRecovery(
            prepared.State, prepared.Attempt, RoomChartApplyMode.OrdinaryRemoteMerge, scope);
        Require(recovery.FullSnapshotRequest is
               {
                   ReplayMode: RoomChartFullSnapshotReplayMode.PreserveAcceptedLocalChanges,
                   RejectedScope: RoomChartRejectedChangeScope.Notes
               },
            "a category permission rejection did not request a scoped replay");
        var plan = RoomChartSyncReducer.ApplyFullSnapshot(
                recovery.State, recovery.FullSnapshotRequest, Snapshot(baseline, 2), local, 3)
            .ApplyPlan ?? throw new InvalidOperationException("scoped rejection produced no plan");
        Require(plan.Document.Notes.All(note => note.Id != noteId) &&
               plan.Document.Timing.Single().Bpm == 150 &&
               plan.HasConcurrentLocalChanges,
            "note_permission_required dropped the permitted timing edit or kept the refused note");
        var retry = RoomChartSyncReducer.PrepareAttempt(
            plan.State, plan.Document, 3, SequentialId(0x60, 2)).Attempt.Batch;
        Require(retry.UpsertNotes.Count == 0 && retry.UpsertTiming.Count == 1,
            "the retry after a scoped rejection did not carry only the accepted category");
    }

    private static void TestFieldLevelMergeAndDeleteWinsPolicy()
    {
        var shared = NoteId(0x701);
        var remoteDeleted = NoteId(0x702);
        var localDeleted = NoteId(0x703);
        var longNote = NoteId(0x704);
        var baseline = CreateDocument("merge-baseline");
        baseline.Notes.Add(new ChartNote { Id = shared, Lane = 1, Beat = BeatPosition.FromDouble(1) });
        baseline.Notes.Add(new ChartNote { Id = remoteDeleted, Lane = 2, Beat = BeatPosition.FromDouble(2) });
        baseline.Notes.Add(new ChartNote { Id = localDeleted, Lane = 3, Beat = BeatPosition.FromDouble(3) });
        baseline.Notes.Add(new ChartNote
        {
            Id = longNote, Lane = 0, Beat = BeatPosition.FromDouble(4), EndBeat = BeatPosition.FromDouble(6)
        });
        baseline.Normalize();

        var remote = baseline.Clone();
        remote.Notes.Single(note => note.Id == shared).Lane = 2;
        remote.Notes.RemoveAll(note => note.Id == remoteDeleted);
        remote.Notes.Single(note => note.Id == localDeleted).Volume = 50;
        remote.Notes.Single(note => note.Id == longNote).EndBeat = BeatPosition.FromDouble(4.25);
        remote.Timing.Single().Delay = 10;
        remote.Normalize();

        var local = baseline.Clone();
        local.Notes.Single(note => note.Id == shared).Volume = 70;
        local.Notes.Single(note => note.Id == remoteDeleted).Volume = 30;
        local.Notes.RemoveAll(note => note.Id == localDeleted);
        local.Notes.Single(note => note.Id == longNote).Beat = BeatPosition.FromDouble(5);
        local.Timing.Single().Bpm = 150;
        local.Normalize();

        Require(CollaborationDocumentMergePolicy.DeleteConflictPolicy ==
               CollaborationDeleteConflictPolicy.DeleteWins,
            "the delete-versus-edit decision is not delete-wins");
        var merged = CollaborationDocumentMergePolicy.ReapplyChangesAfterAcknowledgedRequest(
            remote, baseline, local);
        var mergedShared = merged.Notes.Single(note => note.Id == shared);
        var mergedLong = merged.Notes.Single(note => note.Id == longNote);
        Require(mergedShared is { Lane: 2, Volume: 70 },
            "concurrent edits of different fields of one note did not both survive");
        Require(merged.Notes.All(note => note.Id != remoteDeleted),
            "a remotely deleted note was resurrected by a local field edit");
        Require(merged.Notes.All(note => note.Id != localDeleted),
            "a locally deleted note was restored by a remote field edit");
        Require(mergedLong.Beat == BeatPosition.FromDouble(5) &&
               mergedLong.EndBeat == BeatPosition.FromDouble(6),
            "LN head and tail were merged independently into an inverted note");
        Require(merged.Timing.Single() is { Delay: 10, Bpm: 150 },
            "independent timing field edits were not merged");

        var next = RoomChartSyncReducer.PrepareAttempt(
            RoomChartSyncState.Create(9, remote), merged, 1, SequentialId(0x70, 1)).Attempt.Batch;
        Require(next.UpsertNotes.All(note => note.Id != remoteDeleted) &&
               next.DeleteNoteIds.Contains(localDeleted),
            "the next write resurrected the remote delete or did not send the local delete");

        var localWins = CollaborationDocumentMergePolicy.ReapplyChangesAfterAcknowledgedRequest(
            remote, baseline, local, CollaborationDeleteConflictPolicy.LocalWins);
        Require(localWins.Notes.Any(note => note.Id == remoteDeleted) &&
               localWins.Notes.All(note => note.Id != localDeleted),
            "the alternative delete policy is not selectable from the single switch");
    }

    private static async Task TestDefinitiveRejectionPreservesRecoverableCopyAsync()
    {
        var baseline = CreateDocument("reject-backup");
        var fixture = new Fixture(baseline);
        fixture.Transport.PushHandler = (call, batch, _) => call == 1
            ? Task.FromException<OnlineChartDelta>(new OnlineApiException(
                "invalid", "invalid_operation", HttpStatusCode.BadRequest))
            : Task.FromResult(Acknowledge(batch, fixture.Clock.GetUtcNow()));
        fixture.Transport.SnapshotHandler = (_, _) => Task.FromResult(Snapshot(baseline, 0));
        await using var coordinator = fixture.Create();
        coordinator.Start();
        await fixture.WaitForInitialPullAsync(coordinator);

        var noteId = NoteId(0x801);
        fixture.Host.AddLocalNote(noteId, lane: 1, beat: 4);
        coordinator.NotifyLocalChange();
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => fixture.Host.PreservedRejections.Count == 1 &&
                  fixture.Transport.SnapshotCalls == 1 &&
                  coordinator.State.PendingWrite == null,
            "a deterministic rejection did not hand the discarded edits to the host first");
        var rejected = fixture.Host.PreservedRejections[0];
        Require(rejected is { ErrorCode: "invalid_operation", StatusCode: HttpStatusCode.BadRequest } &&
               rejected.LocalDocument.Notes.Any(note => note.Id == noteId) &&
               rejected.RejectedBatch.UpsertNotes.Any(note => note.Id == noteId),
            "the recoverable copy lacked the rejected edit or its reason");
    }

    private static async Task TestSelectionLockedRetriesUnlockedEditsAsync()
    {
        var baseline = CreateDocument("locked-baseline");
        var fixture = new Fixture(baseline);
        var initialize = OnlineChartSyncCodec.CreateInitializeRequest(baseline);
        var lockedSnapshot = new OnlineChartSnapshot(
            0,
            initialize.KeyCount,
            initialize.Notes,
            initialize.Timing,
            [
                new OnlineChartLock(
                    SequentialId(0x90, 1),
                    new OnlineSyncBeat(8, 0, 1),
                    new OnlineSyncBeat(12, 0, 1),
                    -64,
                    81,
                    1)
            ],
            initialize.Metadata);
        fixture.Transport.PushHandler = (call, batch, _) => call == 1
            ? Task.FromException<OnlineChartDelta>(new OnlineApiException(
                "locked", "selection_locked", HttpStatusCode.Forbidden))
            : Task.FromResult(Acknowledge(batch, fixture.Clock.GetUtcNow()));
        fixture.Transport.SnapshotHandler = (_, _) => Task.FromResult(lockedSnapshot);
        await using var coordinator = fixture.Create();
        coordinator.Start();
        await fixture.WaitForInitialPullAsync(coordinator);

        var free = NoteId(0x901);
        var locked = NoteId(0x902);
        fixture.Host.AddLocalNote(free, lane: 1, beat: 4);
        fixture.Host.AddLocalNote(locked, lane: 2, beat: 10);
        coordinator.NotifyLocalChange();
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => fixture.Transport.PushCalls.Count == 2 &&
                  coordinator.State is { PendingWrite: null, Revision: 1 },
            "the unlocked part of a selection_locked batch was not retried");
        var retry = fixture.Transport.PushCalls[1].Batch;
        Require(retry.UpsertNotes.Select(note => note.Id).SequenceEqual([free]),
            "the retry resent the locked note or dropped the unlocked one");
        Require(fixture.Host.Document.Notes.Any(note => note.Id == free) &&
               fixture.Host.Document.Notes.All(note => note.Id != locked),
            "the editor kept the refused locked-region edit or lost the accepted one");
        Require(fixture.Host.PreservedRejections.Single() is
               {
                   RejectedScope: RoomChartRejectedChangeScope.LockedRegions
               } rejected && rejected.LocalDocument.Notes.Any(note => note.Id == locked),
            "the refused locked-region edit was not kept in the recoverable copy");
    }

    private static async Task TestUnauthorizedWriteKeepsLocalWorkAsync()
    {
        var baseline = CreateDocument("unauthorized-write");
        var fixture = new Fixture(baseline);
        fixture.Transport.PushHandler = (_, _, _) => Task.FromException<OnlineChartDelta>(
            new OnlineApiException("expired", null, HttpStatusCode.Unauthorized));
        await using var coordinator = fixture.Create();
        coordinator.Start();
        await fixture.WaitForInitialPullAsync(coordinator);

        var noteId = NoteId(0xA01);
        fixture.Host.AddLocalNote(noteId, lane: 1, beat: 4);
        coordinator.NotifyLocalChange();
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Faulted,
            "an unauthorized write did not surface as a fault");
        Require(coordinator.State.PendingWrite != null &&
               fixture.Transport.SnapshotCalls == 0 &&
               fixture.Host.PreservedRejections.Count == 0 &&
               fixture.Host.Document.Notes.Any(note => note.Id == noteId),
            "a write the server never evaluated (401) discarded local work");
    }

    private static async Task TestUnusableWriteResponseIsRetriedAsync()
    {
        var baseline = CreateDocument("unusable-response");
        var fixture = new Fixture(baseline);
        fixture.Transport.PushHandler = (call, batch, _) => call == 1
            ? Task.FromException<OnlineChartDelta>(OnlineHttpResponse.InvalidResponse())
            : Task.FromResult(Acknowledge(batch, fixture.Clock.GetUtcNow()));
        await using var coordinator = fixture.Create();
        coordinator.Start();
        await fixture.WaitForInitialPullAsync(coordinator);

        var noteId = NoteId(0xA11);
        fixture.Host.AddLocalNote(noteId, lane: 2, beat: 6);
        coordinator.NotifyLocalChange();
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.BackingOff,
            "an unusable 2xx write response was not retried as ambiguous");
        var first = fixture.Transport.PushCalls[0].Batch;
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(400));
        await EventuallyAsync(
            () => fixture.Transport.PushCalls.Count == 2 &&
                  coordinator.State is { Revision: 1, PendingWrite: null },
            "the ambiguous write was not resent");
        Require(fixture.Transport.PushCalls[1].Batch.OperationId == first.OperationId &&
                fixture.Host.PreservedRejections.Count == 0 &&
                fixture.Host.Document.Notes.Any(note => note.Id == noteId),
            "an unusable response was treated as a definitive rejection");
    }

    private static async Task TestWriteBudgetKeepsPullingAndMergingAsync()
    {
        var baseline = CreateDocument("budget-reads");
        var fixture = new Fixture(baseline);
        var remoteId = NoteId(0xB01);
        var remoteDocument = baseline.Clone();
        remoteDocument.Notes.Add(new ChartNote
        {
            Id = remoteId, Lane = 3, Beat = BeatPosition.FromDouble(12), OnlineCreatorUserId = 88
        });
        remoteDocument.Normalize();
        var remoteOperation = new AppliedOnlineChartOperation(
            1,
            88,
            OnlineChartSyncCodec.CreateBatch(0, baseline, remoteDocument, SequentialId(0xB0, 1)),
            Origin);
        fixture.Transport.PushHandler = (call, batch, _) => call == 1
            ? Task.FromException<OnlineChartDelta>(new OnlineApiException(
                "budget", "chart_write_budget", HttpStatusCode.TooManyRequests,
                TimeSpan.FromSeconds(60)))
            : Task.FromResult(new OnlineChartDelta(
                2,
                [new AppliedOnlineChartOperation(2, 71, batch, Origin)],
                [],
                false));
        fixture.Transport.PullHandler = (call, after, _) => Task.FromResult(call == 1
            ? EmptyDelta(0)
            : after >= 1
                ? EmptyDelta(after)
                : new OnlineChartDelta(1, [remoteOperation], [], false));
        await using var coordinator = fixture.Create();
        coordinator.Start();
        await fixture.WaitForInitialPullAsync(coordinator);

        var localId = NoteId(0xB02);
        fixture.Host.AddLocalNote(localId, lane: 1, beat: 4);
        coordinator.NotifyLocalChange();
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => fixture.Transport.PushCalls.Count == 1 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.WriteBudgetThrottled,
            "the write-budget fixture did not enter its throttle window");
        var throttledAt = fixture.Clock.GetUtcNow();
        var operationId = fixture.Transport.PushCalls[0].Batch.OperationId;

        var pullsBefore = fixture.Transport.PullCalls.Count;
        coordinator.RequestSynchronization();
        await EventuallyAsync(
            () => fixture.Transport.PullCalls.Count > pullsBefore &&
                  fixture.Host.Document.Notes.Any(note => note.Id == remoteId),
            "a write-budget window stopped read-only pulls of remote edits");
        Require(fixture.Transport.PushCalls.Count == 1 &&
               coordinator.State is { Revision: 1 } &&
               coordinator.State.PendingWrite?.OperationId == operationId &&
               fixture.Host.Document.Notes.Any(note => note.Id == localId) &&
               coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.WriteBudgetThrottled &&
               coordinator.ViewState.RetryAt == throttledAt + TimeSpan.FromSeconds(60),
            "a read during the write-budget window lost pending intent or cleared the budget state");

        await fixture.Delays.AdvanceAsync(TimeSpan.FromSeconds(59));
        Require(fixture.Transport.PushCalls.Count == 1,
            "the throttled write was retried before its Retry-After deadline");
        await fixture.Delays.AdvanceAsync(TimeSpan.FromSeconds(1));
        await EventuallyAsync(
            () => fixture.Transport.PushCalls.Count == 2 &&
                  coordinator.State is { Revision: 2, PendingWrite: null },
            "the throttled write was not sent at its deadline");
        Require(fixture.Transport.PushCalls[1].Batch.OperationId == operationId &&
               fixture.Host.Document.Notes.Any(note => note.Id == remoteId) &&
               fixture.Host.Document.Notes.Any(note => note.Id == localId),
            "the deadline write changed OperationId or the merge lost local or remote notes");
    }

    private static async Task TestWallClockRollbackDoesNotFreezeRetriesAsync()
    {
        var baseline = CreateDocument("wall-clock");
        var fixture = new Fixture(baseline);
        fixture.Transport.PushHandler = (call, batch, _) => call == 1
            ? Task.FromException<OnlineChartDelta>(new HttpRequestException("offline"))
            : Task.FromResult(Acknowledge(batch, fixture.Clock.GetUtcNow()));
        await using var coordinator = fixture.Create();
        coordinator.Start();
        await fixture.WaitForInitialPullAsync(coordinator);

        fixture.Host.AddLocalNote(NoteId(0xC01), lane: 1, beat: 4);
        coordinator.NotifyLocalChange();
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(40));
        await EventuallyAsync(
            () => coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.BackingOff,
            "the wall-clock fixture did not enter backoff");
        // A time sync moves the system clock one hour back during the backoff.
        fixture.Clock.SetWallClock(fixture.Clock.GetUtcNow() - TimeSpan.FromHours(1));
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(400));
        await EventuallyAsync(
            () => fixture.Transport.PushCalls.Count == 2 &&
                  coordinator.State is { Revision: 1, PendingWrite: null },
            "a wall-clock rollback froze the retry deadline");
    }

    private static async Task TestHungTransportTimesOutAndRetriesAsync()
    {
        var baseline = CreateDocument("hung-transport");
        var fixture = new Fixture(baseline);
        var hung = new TaskCompletionSource<OnlineChartDelta>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.PullHandler = (call, revision, _) => call == 2
            ? hung.Task // a half-open connection that never answers or observes cancellation
            : Task.FromResult(EmptyDelta(revision));
        await using var coordinator = fixture.Create();
        coordinator.Start();
        await fixture.WaitForInitialPullAsync(coordinator);

        coordinator.RequestSynchronization();
        await EventuallyAsync(() => fixture.Transport.PullCalls.Count == 2,
            "the hung pull did not start");
        await fixture.Delays.AdvanceAsync(TimeSpan.FromSeconds(29));
        Require(coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Synchronizing,
            "the request deadline fired early");
        await fixture.Delays.AdvanceAsync(TimeSpan.FromSeconds(1));
        await EventuallyAsync(
            () => coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.BackingOff,
            "a hung transport call was not abandoned at its deadline");
        await fixture.Delays.AdvanceAsync(TimeSpan.FromMilliseconds(400));
        await EventuallyAsync(
            () => fixture.Transport.PullCalls.Count >= 3 &&
                  coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
            "synchronization did not recover after the transport timeout");
        hung.TrySetResult(EmptyDelta(0));
    }

    private static async Task TestStaleLocalSnapshotIsMergedAgainAsync()
    {
        var baseline = CreateDocument("stale-apply");
        var fixture = new Fixture(baseline);
        fixture.Host.EnforceLocalChangeSerial = true;
        var remoteId = NoteId(0xD01);
        var remoteDocument = baseline.Clone();
        remoteDocument.Notes.Add(new ChartNote
        {
            Id = remoteId, Lane = 2, Beat = BeatPosition.FromDouble(8), OnlineCreatorUserId = 88
        });
        remoteDocument.Normalize();
        var remoteOperation = new AppliedOnlineChartOperation(
            1,
            88,
            OnlineChartSyncCodec.CreateBatch(0, baseline, remoteDocument, SequentialId(0xD0, 1)),
            Origin);
        fixture.Transport.PullHandler = (call, after, _) => Task.FromResult(call == 2
            ? new OnlineChartDelta(1, [remoteOperation], [], false)
            : EmptyDelta(Math.Max(after, call == 1 ? 0 : 1)));
        await using var coordinator = fixture.Create();
        coordinator.Start();
        await fixture.WaitForInitialPullAsync(coordinator);

        var lateLocal = NoteId(0xD02);
        fixture.Host.RunBeforeNextApply(() => fixture.Host.AddLocalNote(lateLocal, lane: 1, beat: 4));
        coordinator.RequestSynchronization();
        await EventuallyAsync(
            () => fixture.Host.StaleRejections == 1 &&
                  fixture.Host.Document.Notes.Any(note => note.Id == remoteId) &&
                  fixture.Host.Document.Notes.Any(note => note.Id == lateLocal),
            "an edit committed between the off-thread merge and the apply was overwritten");
        await EventuallyAsync(
            () => fixture.Transport.PushCalls.Any(call =>
                call.Batch.UpsertNotes.Any(note => note.Id == lateLocal)),
            "the edit kept by the re-merge was not written afterwards");
    }

    private static async Task TestFeedRetriesRecoverableErrorsOnlyAsync()
    {
        var baseline = CreateDocument("feed-errors");
        var recoverable = new Fixture(baseline);
        recoverable.Transport.FeedHandler = (call, _, token) => call == 1
            ? Task.FromException(new OnlineApiException(
                "not ready", "chart_not_ready", HttpStatusCode.NotFound))
            : Task.Delay(Timeout.InfiniteTimeSpan, token);
        await using (var coordinator = recoverable.Create())
        {
            coordinator.Start();
            await EventuallyAsync(
                () => recoverable.Transport.FeedCalls == 1 &&
                      coordinator.ViewState.FeedStatus == RoomChartSyncFeedStatus.Faulted,
                "a non-transient SSE error was not reported");
            await recoverable.Delays.AdvanceAsync(TimeSpan.FromSeconds(8));
            await EventuallyAsync(() => recoverable.Transport.FeedCalls == 2,
                "a recoverable non-transient SSE error left the feed permanently faulted");
        }

        var unauthorized = new Fixture(baseline);
        unauthorized.Transport.FeedHandler = (_, _, _) => Task.FromException(
            new OnlineApiException("expired", null, HttpStatusCode.Unauthorized));
        await using (var coordinator = unauthorized.Create())
        {
            coordinator.Start();
            await EventuallyAsync(
                () => coordinator.ViewState.FeedStatus == RoomChartSyncFeedStatus.Faulted,
                "an unauthorized SSE error was not reported");
            await unauthorized.Delays.AdvanceAsync(TimeSpan.FromSeconds(60));
            await DrainAsync();
            Require(unauthorized.Transport.FeedCalls == 1,
                "an SSE 401 that needs a new sign-in was retried");
        }
    }

    private static async Task TestEventStreamIdleWatchdogAsync()
    {
        Require(OnlineChartEventApi.DefaultIdleTimeout > TimeSpan.FromSeconds(15),
            "the SSE idle timeout does not exceed the server's 15 s keep-alive");
        var keepAlives = 0;
        using var client = new HttpClient(new RoutingHandler((_, _) =>
        {
            var content = new StreamContent(new ScriptedStream(
                Enumerable.Repeat(": keep-alive\n\n", 5), TimeSpan.FromMilliseconds(50),
                () => Interlocked.Increment(ref keepAlives)));
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        // Keep-alives arrive ten times faster than the watchdog; only the final
        // silence may end the stream.
        var api = new OnlineChartEventApi(client, Endpoint) { IdleTimeout = TimeSpan.FromMilliseconds(500) };
        var watch = Stopwatch.StartNew();
        IOException? idle = null;
        try
        {
            await api.ListenForRevisionsAsync(RoomId, "token", _ => Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (IOException exception)
        {
            idle = exception;
        }
        Require(idle != null && keepAlives == 5 && watch.Elapsed >= TimeSpan.FromMilliseconds(500),
            "the SSE watchdog ignored keep-alives or never ended a silent half-open stream");
    }

    private static async Task TestOnlyExplicitRoomCodesEndMembershipAsync()
    {
        var responses = new Queue<Func<HttpResponseMessage>>(
        [
            () => ErrorResponse(HttpStatusCode.NotFound, "chart_not_ready", "not ready"),
            () => ErrorResponse(HttpStatusCode.NotFound, "resources_not_ready", "uploading"),
            () => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("<html>not found</html>", Encoding.UTF8, "text/html")
            },
            () => ErrorResponse(HttpStatusCode.NotFound, "room_not_found", "closed")
        ]);
        var session = await CreateSignedInSessionAsync((request, _) =>
            Task.FromResult(IsPath(request, "api/rooms")
                ? JsonResponse(new OnlineCreatedRoomDto(Room(RoomId)))
                : responses.Dequeue()()));
        await session.CreateRoomAsync("room", false, null, false);

        await ExpectOnlineErrorAsync(() => session.GetRoomChartDeltaAsync(RoomId, 0));
        await ExpectOnlineErrorAsync(() => session.GetRoomResourceManifestAsync(RoomId));
        await ExpectOnlineErrorAsync(() => session.GetRoomChartDeltaAsync(RoomId, 0));
        Require(session.CurrentRoom?.Id == RoomId && session.CanEditNotes,
            "a not-ready 404 was treated as having left the room");
        await ExpectOnlineErrorAsync(() => session.GetRoomChartDeltaAsync(RoomId, 0));
        Require(session.CurrentRoom == null && !session.CanEditNotes,
            "room_not_found did not end the local room membership");
    }

    private static async Task TestLateResultsAfterLogoutAreDiscardedAsync()
    {
        var createStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profileStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProfile = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await CreateSignedInSessionAsync(async (request, _) =>
        {
            if (IsPath(request, "api/rooms"))
            {
                createStarted.TrySetResult();
                await releaseCreate.Task;
                return JsonResponse(new OnlineCreatedRoomDto(Room(RoomId)));
            }
            if (IsPath(request, "api/me/profile"))
            {
                profileStarted.TrySetResult();
                await releaseProfile.Task;
                return JsonResponse(User(41, "late") with { DisplayName = "Late" });
            }
            if (IsPath(request, "api/auth/logout"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            throw new InvalidOperationException($"unexpected request {request.RequestUri}");
        });

        var create = session.CreateRoomAsync("room", false, null, false);
        var profile = session.UpdateProfileAsync("Late", "", false, false);
        await createStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await profileStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await session.LogoutAsync();
        releaseCreate.TrySetResult();
        releaseProfile.TrySetResult();
        var createError = await CaptureOnlineErrorAsync(create);
        var profileError = await CaptureOnlineErrorAsync(profile);
        Require(createError?.Code == "authentication_required" &&
               profileError?.Code == "authentication_required" &&
               session.CurrentRoom == null && session.CurrentUser == null && !session.IsAuthenticated,
            "a create-room or profile response completed after logout wrote state back");
    }

    private static async Task TestRestoreUnauthorizedKeepsNewerLoginAsync()
    {
        var store = new SyncTokenStore { Token = "old-token" };
        var restoreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new RoutingHandler(async (request, _) =>
        {
            if (IsPath(request, "api/me"))
            {
                restoreStarted.TrySetResult();
                await releaseRestore.Task;
                return ErrorResponse(HttpStatusCode.Unauthorized, "unauthorized", "expired");
            }
            if (IsPath(request, "api/auth/login"))
                return JsonResponse(new OnlineLoginResponseDto(
                    "new-token", Origin.AddHours(1), User(42, "fresh")));
            throw new InvalidOperationException($"unexpected request {request.RequestUri}");
        }));
        var session = new OnlineAccountSession(client, Endpoint, store);
        var restore = session.TryRestoreAsync();
        await restoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await session.LoginAsync("fresh", "password", Guid.NewGuid(), "12345");
        releaseRestore.TrySetResult();
        var restored = await restore.WaitAsync(TimeSpan.FromSeconds(4));
        Require(!restored && store.Token == "new-token" && store.DeleteCalls == 0 &&
               session.IsAuthenticated && session.CurrentUser?.Id == 42,
            "a late restore 401 for the old token deleted a newer login");
    }

    private static async Task TestServerExpiryIsNotJudgedByLocalClockAsync()
    {
        var serverNow = Origin;
        // This machine's clock runs twenty minutes fast.
        var localClock = new FakeTimeProvider(serverNow + TimeSpan.FromMinutes(20));
        var loginBodies = new Queue<Func<HttpResponseMessage>>(
        [
            () => JsonResponse(new OnlineLoginResponseDto(
                "session-token", serverNow + TimeSpan.FromMinutes(10), User(43, "skewed"))),
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"accessToken\":\"missing-expiry\",\"user\":{\"id\":44,\"username\":\"nope\"}}",
                    Encoding.UTF8,
                    "application/json")
            }
        ]);
        using var client = new HttpClient(new RoutingHandler((request, _) =>
        {
            if (IsPath(request, "api/auth/osu/start"))
            {
                var start = JsonResponse(new OnlineOsuOAuthStartDto(
                    "https://osu.ppy.sh/oauth/authorize?state=1",
                    "poll-token",
                    serverNow + TimeSpan.FromMinutes(10)));
                start.Headers.Date = serverNow;
                return Task.FromResult(start);
            }
            return Task.FromResult(loginBodies.Dequeue()());
        }));
        var session = new OnlineAccountSession(client, Endpoint, new SyncTokenStore(), localClock);
        var user = await session.LoginAsync("skewed", "password", Guid.NewGuid(), "12345");
        Require(user.Id == 43 && session.IsAuthenticated,
            "a valid login was rejected because the local clock runs fast");
        var missing = await CaptureOnlineErrorAsync(
            session.LoginAsync("nope", "password", Guid.NewGuid(), "12345"));
        Require(missing != null, "a login response without an expiry was accepted");
        var osuStart = await session.StartOsuLoginAsync();
        Require(osuStart.ExpiresAt == localClock.GetUtcNow() + TimeSpan.FromMinutes(10),
            "the osu! poll deadline was not re-expressed on the local clock");
    }

    private static async Task TestInvalidSuccessBodiesBecomeOnlineErrorsAsync()
    {
        using var client = new HttpClient(new RoutingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body>Sign in to the Wi-Fi</body></html>", Encoding.UTF8, "text/html")
            })));
        var rooms = new OnlineRoomApi(client, Endpoint);
        var chart = new OnlineChartApi(client, Endpoint);
        var roomError = await CaptureOnlineErrorAsync(rooms.GetVisibleRoomsAsync(null, 1));
        var chartError = await CaptureOnlineErrorAsync(chart.GetDeltaAsync(RoomId, 0, "token"));
        Require(roomError?.Code == OnlineHttpResponse.InvalidResponseCode &&
               chartError?.Code == OnlineHttpResponse.InvalidResponseCode &&
               roomError.InnerException is JsonException or NotSupportedException,
            "a 200 HTML portal page escaped as JsonException instead of an online error");
    }

    private static async Task TestNullDtoFieldsAreNormalizedAsync()
    {
        var notificationPages = new Queue<string>(
        [
            "{\"nextCursor\":3,\"hasMore\":false,\"notifications\":[]}",
            "{\"nextCursor\":5,\"hasMore\":false,\"notifications\":[" +
            "{\"id\":4,\"type\":null,\"actorUserId\":2,\"createdAt\":\"2026-01-01T00:00:00Z\"}," +
            "{\"id\":5,\"type\":\"follow\",\"actorUserId\":2,\"actorDisplayName\":null," +
            "\"createdAt\":\"2026-01-01T00:00:00Z\"}]}"
        ]);
        var session = await CreateSignedInSessionAsync((request, _) =>
        {
            if (IsPath(request, "api/rooms"))
                return Task.FromResult(JsonResponse(new OnlineCreatedRoomDto(Room(RoomId))));
            if (IsPath(request, $"api/rooms/{RoomId:D}/live"))
            {
                var room = JsonSerializer.Serialize(Room(RoomId), JsonOptions);
                return Task.FromResult(RawJson(
                    $"{{\"room\":{room},\"members\":[{{\"userId\":41,\"username\":null," +
                    "\"memberRole\":null,\"joinedAt\":\"2026-01-01T00:00:00Z\",\"isOnline\":true}]," +
                    "\"messages\":[],\"latestMessageId\":0}"));
            }
            if (IsPath(request, "api/me/notifications"))
                return Task.FromResult(RawJson(notificationPages.Dequeue()));
            throw new InvalidOperationException($"unexpected request {request.RequestUri}");
        });

        Require((await session.PollNotificationsAsync()).Count == 0, "notification cursor setup failed");
        var notifications = await session.PollNotificationsAsync();
        Require(notifications.Count == 1 &&
               notifications[0] is { Id: 5, Type: "follow", ActorDisplayName: "" },
            "a notification without a type reached the UI or missing text stayed null");

        await session.CreateRoomAsync("room", false, null, false);
        var live = await session.GetRoomLiveStateAsync(0);
        Require(live.Value?.Members.Single() is { MemberRole: "viewer", Username: "" } &&
               session.CurrentMemberRole == "viewer" && !session.CanEditMetadata,
            "a member without a role was not normalized to a fail-closed viewer");
    }

    private static async Task TestResourceCommitAfterLeaveDoesNotThrowAsync()
    {
        var commitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await CreateSignedInSessionAsync(async (request, _) =>
        {
            if (IsPath(request, "api/rooms"))
                return JsonResponse(new OnlineCreatedRoomDto(Room(RoomId)));
            if (IsPath(request, $"api/rooms/{RoomId:D}/resources/commit"))
            {
                commitStarted.TrySetResult();
                await releaseCommit.Task;
                return JsonResponse(new OnlineRoomResourceManifestDto(0, "", "chart.mc"));
            }
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            throw new InvalidOperationException($"unexpected request {request.RequestUri}");
        });
        await session.CreateRoomAsync("room", false, null, false);
        var commit = session.CommitRoomResourcesAsync(RoomId, []);
        await commitStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await session.LeaveCurrentRoomAsync();
        releaseCommit.TrySetResult();
        var manifest = await commit.WaitAsync(TimeSpan.FromSeconds(4));
        Require(manifest.TotalBytes == 0 && session.CurrentRoom == null,
            "a resource commit that finished after leaving reinstalled or crashed on the room");
    }

    private static async Task<OnlineAccountSession> CreateSignedInSessionAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route)
    {
        var client = new HttpClient(new RoutingHandler((request, token) =>
            IsPath(request, "api/me") && request.Method == HttpMethod.Get
                ? Task.FromResult(JsonResponse(User(41, "member")))
                : route(request, token)));
        var session = new OnlineAccountSession(
            client, Endpoint, new SyncTokenStore { Token = "member-token" });
        Require(await session.TryRestoreAsync(), "the signed-in session fixture did not restore");
        return session;
    }

    private static async Task ExpectOnlineErrorAsync<T>(Func<Task<T>> action) =>
        Require(await CaptureOnlineErrorAsync(action()) != null,
            "the request was expected to fail with an online error");

    private static async Task<OnlineApiException?> CaptureOnlineErrorAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(4));
            return null;
        }
        catch (OnlineApiException exception)
        {
            return exception;
        }
    }

    // The top-level Program declares its own Assert, which would shadow the
    // coordinator specs' helper imported with using static.
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool IsPath(HttpRequestMessage request, string path) =>
        string.Equals(
            request.RequestUri?.AbsolutePath.Trim('/'),
            path,
            StringComparison.OrdinalIgnoreCase);

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: JsonOptions)
    };

    private static HttpResponseMessage RawJson(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string code, string message) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { code, message }, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

    private static OnlineUserProfileDto User(long id, string username) => new(
        id, username, username.ToUpperInvariant(), "user", "default", Origin.AddYears(-1), 0);

    private static OnlineRoomSummaryDto Room(Guid id) => new(
        id, 1234, "Sync room", 41, "Owner", 1, 8, false, false, true, 0, Origin);

    private static int ItemCount(OnlineChartOperationBatch batch) =>
        batch.UpsertNotes.Count + batch.DeleteNoteIds.Count + batch.UpsertTiming.Count +
        batch.DeleteTimingIds.Count + (batch.Metadata == null ? 0 : 1) + (batch.KeyCount.HasValue ? 1 : 0);

    private static Guid SequentialId(int prefix, int value) =>
        Guid.Parse($"{prefix:X8}-0000-0000-0000-{value:X12}");

    private sealed class Fixture(ChartDocument baseline)
    {
        public FakeTimeProvider Clock { get; } = new(Origin);
        public FakeDocumentHost Host { get; } = new(baseline);
        public DeterministicOperationIds Ids { get; } = new();
        public FakeDelayScheduler Delays => delays ??= new FakeDelayScheduler(Clock);
        public FakeRoomChartTransport Transport => transport ??= new FakeRoomChartTransport(Clock);

        private FakeDelayScheduler? delays;
        private FakeRoomChartTransport? transport;

        public RoomChartSyncCoordinator Create() =>
            CreateCoordinator(baseline, Transport, Host, Clock, Delays, Ids);

        public Task WaitForInitialPullAsync(RoomChartSyncCoordinator coordinator) =>
            EventuallyAsync(
                () => Transport.PullCalls.Count >= 1 &&
                      coordinator.ViewState.WorkStatus == RoomChartSyncWorkStatus.Idle,
                "the coordinator fixture did not finish its initial pull");
    }

    private sealed class RoutingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => route(request, cancellationToken);
    }

    private sealed class SyncTokenStore : ISessionTokenStore
    {
        private readonly object gate = new();
        private string? token;
        private int deleteCalls;

        public string? Token
        {
            get { lock (gate) return token; }
            init => token = value;
        }

        public int DeleteCalls
        {
            get { lock (gate) return deleteCalls; }
        }

        public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
        {
            lock (gate) return ValueTask.FromResult(token);
        }

        public ValueTask SaveAsync(string value, CancellationToken cancellationToken = default)
        {
            lock (gate) token = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                token = null;
                deleteCalls++;
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Emits each line after a pause, then stays silent without closing.</summary>
    private sealed class ScriptedStream(
        IEnumerable<string> lines,
        TimeSpan pause,
        Action onLine) : Stream
    {
        private readonly Queue<byte[]> pending = new(lines.Select(Encoding.UTF8.GetBytes));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(pause, cancellationToken);
            if (!pending.TryDequeue(out var line))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            line.CopyTo(buffer);
            onLine();
            return line.Length;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

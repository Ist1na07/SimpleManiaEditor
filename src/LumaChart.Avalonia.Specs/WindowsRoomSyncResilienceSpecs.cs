using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LumaChart.Application.Collaboration;
using LumaChart.Avalonia.Services;
using LumaChart.Core.Editing;
using LumaChart.Core.Models;
using LumaChart.Online;
using LumaChart.Storage;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsRoomSyncResilienceSpecs
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:43124/");
    private static readonly Guid RoomId =
        Guid.Parse("a7000000-0000-0000-0000-000000000001");
    private static readonly Guid LocalNoteId =
        Guid.Parse("a7000000-0000-0000-0000-000000000002");
    private static readonly Guid RemoteNoteId =
        Guid.Parse("a7000000-0000-0000-0000-000000000003");
    private static readonly Guid MalformedDeltaRemoteNoteId =
        Guid.Parse("a7000000-0000-0000-0000-00000000000a");
    private static readonly Guid StableOperationId =
        Guid.Parse("a7000000-0000-0000-0000-000000000004");
    private static readonly Guid OfflineLocalNoteId =
        Guid.Parse("a7000000-0000-0000-0000-000000000007");
    private static readonly Guid OfflineRemoteNoteId =
        Guid.Parse("a7000000-0000-0000-0000-000000000008");
    private static readonly Guid RecoveryOperationId =
        Guid.Parse("a7000000-0000-0000-0000-000000000009");
    private static readonly Guid RestartCommittedNoteId =
        Guid.Parse("a7000000-0000-0000-0000-00000000000b");
    private static readonly Guid RestartOperationId =
        Guid.Parse("a7000000-0000-0000-0000-00000000000c");
    private static readonly Guid PostRestartNoteId =
        Guid.Parse("a7000000-0000-0000-0000-00000000000d");
    private static readonly Guid PostRestartOperationId =
        Guid.Parse("a7000000-0000-0000-0000-00000000000e");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<WindowsRoomSyncResilienceSpecResult> RunAsync()
    {
        try
        {
            return new WindowsRoomSyncResilienceSpecResult(
                "windows-room-sync-http-ack-loss-sse-reconnect-and-delta-recovery-converge",
                true,
                await VerifyAsync().ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return new WindowsRoomSyncResilienceSpecResult(
                "windows-room-sync-http-ack-loss-sse-reconnect-and-delta-recovery-converge",
                false,
                exception.ToString());
        }
    }

    private static async Task<string> VerifyAsync()
    {
        const string token = "windows-room-sync-resilience-token";
        var baseline = CreateBaseline();
        var server = new ResilientChartServer(baseline, token);
        using var client = new HttpClient(new ResilientChartHandler(server));
        var session = new OnlineAccountSession(
            client,
            Endpoint,
            new MemoryTokenStore(token),
            eventClient: client);
        Require(await session.TryRestoreAsync().ConfigureAwait(false),
            "the authenticated room-sync fixture did not restore its token");

        var host = new ResilienceDocumentHost(baseline);
        var operationIds = new Queue<Guid>(
            [StableOperationId, RecoveryOperationId]);
        await using var coordinator = new RoomChartSyncCoordinator(
            RoomChartSyncState.Create(1, baseline),
            new AvaloniaRoomChartSyncTransport(session, RoomId),
            host,
            new RoomChartSyncCoordinatorOptions
            {
                WriteThrottle = TimeSpan.FromMilliseconds(5),
                PullInterval = TimeSpan.FromSeconds(10),
                InitialRetryDelay = TimeSpan.FromMilliseconds(20),
                MaximumRetryDelay = TimeSpan.FromMilliseconds(80),
                RecoveredStatusHold = TimeSpan.FromMilliseconds(100),
                RetryStatusTail = TimeSpan.FromMilliseconds(50)
            },
            jitter: _ => TimeSpan.Zero,
            operationIdSource: () => operationIds.Dequeue());
        coordinator.Start();
        string liveRecoveryDetail;
        try
        {
            await EventuallyAsync(
                () => server.DeltaRequestCount >= 1 && server.SseConnectionCount >= 1,
                "the coordinator did not establish its initial HTTP delta/SSE paths")
                .ConfigureAwait(false);

            host.AddLocalNote(
                LocalNoteId,
                lane: 2,
                BeatPosition.Preserve(7, 5, 12));
            coordinator.NotifyLocalChange();
            await EventuallyAsync(
                () => coordinator.State is { Revision: >= 2, HasPendingWrite: false } &&
                      server.ChartOperationRequestCount >= 2,
                "the committed-but-unacknowledged write was not retried and acknowledged")
                .ConfigureAwait(false);

            Require(server.CommittedOperationCount == 1,
                "ACK-loss retry committed the same logical operation more than once");
            Require(server.OperationIds.Count >= 2 &&
                    server.OperationIds.All(id => id == StableOperationId),
                "ACK-loss retry did not preserve the stable OperationId");
            Require(server.OperationPayloadHashes.Distinct(StringComparer.Ordinal).Count() == 1,
                "ACK-loss retry changed the serialized operation payload");
            Require(server.Document.Notes.Single(note => note.Id == LocalNoteId).Beat ==
                    BeatPosition.Preserve(7, 5, 12),
                "the server lost or reduced the local exact rational Beat");

            server.FailNextDeltaRequest();
            server.InjectRemoteNote(
                RemoteNoteId,
                lane: 1,
                BeatPosition.Preserve(11, 7, 16));
            await EventuallyAsync(
                () => coordinator.State.Revision >= 3 &&
                      host.Document.Notes.Any(note =>
                          note.Id == RemoteNoteId &&
                          note.Beat == BeatPosition.Preserve(11, 7, 16)),
                "SSE reconnect plus transient delta failure did not converge the remote operation")
                .ConfigureAwait(false);

            Require(server.InjectedDeltaFailureCount == 1,
                "the real HTTP delta path did not consume the injected 503 exactly once");
            Require(server.SseConnectionCount >= 2,
                "the real SSE path did not reconnect after the injected disconnect");
            Require(host.ViewStates.Any(state =>
                        state.FeedStatus == RoomChartSyncFeedStatus.Reconnecting),
                "the UI-facing sync state never exposed SSE reconnecting");
            Require(host.ViewStates.Any(state =>
                        state.WorkStatus == RoomChartSyncWorkStatus.BackingOff),
                "the UI-facing sync state never exposed transient write/delta backoff");
            Require(host.Document.Notes.Any(note =>
                        note.Id == LocalNoteId &&
                        note.Beat == BeatPosition.Preserve(7, 5, 12)) &&
                    host.Document.Notes.Any(note =>
                        note.Id == RemoteNoteId &&
                        note.Beat == BeatPosition.Preserve(11, 7, 16)),
                "final convergence discarded a local or remote exact rational Beat");

            var viewStateCountBeforeMalformedDelta = host.ViewStates.Count;
            server.MalformedNextDeltaResponse();
            server.InjectRemoteNote(
                MalformedDeltaRemoteNoteId,
                lane: 2,
                BeatPosition.Preserve(13, 29, 48));
            await EventuallyAsync(
                () => server.InjectedMalformedDeltaCount == 1 &&
                      coordinator.State.Revision >= 4 &&
                      host.Document.Notes.Any(note =>
                          note.Id == MalformedDeltaRemoteNoteId &&
                          note.Beat == BeatPosition.Preserve(13, 29, 48)),
                "a malformed successful delta response permanently faulted synchronization")
                .ConfigureAwait(false);
            Require(
                host.ViewStates
                    .Skip(viewStateCountBeforeMalformedDelta)
                    .Any(state => state.WorkStatus == RoomChartSyncWorkStatus.BackingOff),
                "malformed delta recovery did not expose transient backoff");

            var viewStateCountBeforeEndpointOutage = host.ViewStates.Count;
            server.SetEndpointAvailable(false);
            host.AddLocalNote(
                OfflineLocalNoteId,
                lane: 3,
                BeatPosition.Preserve(17, 13, 24));
            coordinator.NotifyLocalChange();
            await EventuallyAsync(
                () => server.EndpointOutageFailureCount >= 2 &&
                      host.ViewStates
                          .Skip(viewStateCountBeforeEndpointOutage)
                          .Any(state =>
                              state.WorkStatus == RoomChartSyncWorkStatus.BackingOff) &&
                      host.ViewStates
                          .Skip(viewStateCountBeforeEndpointOutage)
                          .Any(state =>
                              state.FeedStatus == RoomChartSyncFeedStatus.Reconnecting),
                "the whole-endpoint outage did not preserve local work and expose work/feed recovery")
                .ConfigureAwait(false);
            Require(host.Document.Notes.Any(note =>
                        note.Id == OfflineLocalNoteId &&
                        note.Beat == BeatPosition.Preserve(17, 13, 24)),
                "the endpoint outage discarded the unsent exact rational local Beat");

            server.InjectRemoteNote(
                OfflineRemoteNoteId,
                lane: 0,
                BeatPosition.Preserve(23, 19, 32));
            server.SetEndpointAvailable(true);
            await EventuallyAsync(
                () => coordinator.State is { Revision: >= 5, HasPendingWrite: false } &&
                      host.Document.Notes.Any(note =>
                          note.Id == OfflineLocalNoteId &&
                          note.Beat == BeatPosition.Preserve(17, 13, 24)) &&
                      host.Document.Notes.Any(note =>
                          note.Id == OfflineRemoteNoteId &&
                          note.Beat == BeatPosition.Preserve(23, 19, 32)),
                "the restored endpoint did not merge offline local work with newer remote authority")
                .ConfigureAwait(false);

            Require(server.OperationIds.Count(id => id == RecoveryOperationId) == 1,
                "endpoint recovery did not submit the offline edit with one stable OperationId");
            Require(server.Document.Notes.Any(note =>
                        note.Id == OfflineLocalNoteId &&
                        note.Beat == BeatPosition.Preserve(17, 13, 24)) &&
                    server.Document.Notes.Any(note =>
                        note.Id == OfflineRemoteNoteId &&
                        note.Beat == BeatPosition.Preserve(23, 19, 32)),
                "endpoint recovery failed to converge server local/remote exact rational Beats");

            liveRecoveryDetail =
                "real OnlineAccountSession/Avalonia transport: committed ACK loss retried one immutable " +
                $"OperationId with one server commit; SSE connected {server.SseConnectionCount} times; " +
                $"one HTTP 503 delta, one malformed JSON delta and " +
                $"{server.EndpointOutageFailureCount} whole-endpoint failures recovered; " +
                "malformed-response remote 13+29/48 plus offline local 17+13/24 and " +
                "remote 23+19/32 Beats converged";
        }
        finally
        {
            await coordinator.StopAsync().ConfigureAwait(false);
        }

        var restartRecoveryDetail = await VerifyClientRestartRecoveryAsync(server, token)
            .ConfigureAwait(false);
        return $"{liveRecoveryDetail}; {restartRecoveryDetail}";
    }

    private static async Task<string> VerifyClientRestartRecoveryAsync(
        ResilientChartServer server,
        string token)
    {
        using var preRestartClient = new HttpClient(new ResilientChartHandler(server));
        var preRestartSession = new OnlineAccountSession(
            preRestartClient,
            Endpoint,
            new MemoryTokenStore(token),
            eventClient: preRestartClient);
        Require(await preRestartSession.TryRestoreAsync().ConfigureAwait(false),
            "the pre-restart authenticated process did not restore its token");
        var preRestartSnapshot = await preRestartSession.GetRoomChartAsync(RoomId)
            .ConfigureAwait(false);
        var preRestartDocument = OnlineChartSyncCodec.CreateCanonicalDocument(
            CreateBaseline(),
            preRestartSnapshot);
        var preRestartHost = new ResilienceDocumentHost(preRestartDocument);
        server.DropNextOperationResponse();

        await using (var preRestartCoordinator = new RoomChartSyncCoordinator(
                         RoomChartSyncState.Create(
                             preRestartSnapshot.Revision,
                             preRestartDocument),
                         new AvaloniaRoomChartSyncTransport(preRestartSession, RoomId),
                         preRestartHost,
                         new RoomChartSyncCoordinatorOptions
                         {
                             WriteThrottle = TimeSpan.FromMilliseconds(5),
                             PullInterval = TimeSpan.FromSeconds(10),
                             InitialRetryDelay = TimeSpan.FromSeconds(2),
                             MaximumRetryDelay = TimeSpan.FromSeconds(2),
                             RecoveredStatusHold = TimeSpan.FromMilliseconds(100),
                             RetryStatusTail = TimeSpan.FromMilliseconds(50)
                         },
                         jitter: _ => TimeSpan.Zero,
                         operationIdSource: () => RestartOperationId))
        {
            preRestartCoordinator.Start();
            preRestartHost.AddLocalNote(
                RestartCommittedNoteId,
                lane: 1,
                BeatPosition.Preserve(29, 37, 48));
            preRestartCoordinator.NotifyLocalChange();
            await EventuallyAsync(
                    () => server.OperationRequestCount(RestartOperationId) == 1 &&
                          server.CommittedBatchCount(RestartOperationId) == 1 &&
                          preRestartCoordinator.State.HasPendingWrite &&
                          preRestartHost.ViewStates.Any(state =>
                              state.WorkStatus == RoomChartSyncWorkStatus.BackingOff),
                    "the pre-restart process did not stop between server commit and lost ACK retry")
                .ConfigureAwait(false);
            await preRestartCoordinator.StopAsync().ConfigureAwait(false);
        }

        Require(server.OperationRequestCount(RestartOperationId) == 1,
            "the pre-restart process retried after its controlled shutdown");
        Require(server.Document.Notes.Any(note =>
                    note.Id == RestartCommittedNoteId &&
                    note.Beat == BeatPosition.Preserve(29, 37, 48)),
            "the server did not retain the exact rational operation committed before process exit");

        using var restartedClient = new HttpClient(new ResilientChartHandler(server));
        var restartedSession = new OnlineAccountSession(
            restartedClient,
            Endpoint,
            new MemoryTokenStore(token),
            eventClient: restartedClient);
        Require(await restartedSession.TryRestoreAsync().ConfigureAwait(false),
            "the replacement process did not restore the same authenticated token");
        var restartedSnapshot = await restartedSession.GetRoomChartAsync(RoomId)
            .ConfigureAwait(false);
        var restartedDocument = OnlineChartSyncCodec.CreateCanonicalDocument(
            CreateBaseline(),
            restartedSnapshot);
        Require(restartedDocument.Notes.Any(note =>
                    note.Id == RestartCommittedNoteId &&
                    note.Beat == BeatPosition.Preserve(29, 37, 48)),
            "the replacement process snapshot lost the operation whose ACK was dropped");

        var restartedHost = new ResilienceDocumentHost(restartedDocument);
        await using (var restartedCoordinator = new RoomChartSyncCoordinator(
                         RoomChartSyncState.Create(
                             restartedSnapshot.Revision,
                             restartedDocument),
                         new AvaloniaRoomChartSyncTransport(restartedSession, RoomId),
                         restartedHost,
                         new RoomChartSyncCoordinatorOptions
                         {
                             WriteThrottle = TimeSpan.FromMilliseconds(5),
                             PullInterval = TimeSpan.FromMilliseconds(40),
                             InitialRetryDelay = TimeSpan.FromMilliseconds(20),
                             MaximumRetryDelay = TimeSpan.FromMilliseconds(80),
                             RecoveredStatusHold = TimeSpan.FromMilliseconds(100),
                             RetryStatusTail = TimeSpan.FromMilliseconds(50)
                         },
                         jitter: _ => TimeSpan.Zero,
                         operationIdSource: () => PostRestartOperationId))
        {
            restartedCoordinator.Start();
            restartedHost.AddLocalNote(
                PostRestartNoteId,
                lane: 3,
                BeatPosition.Preserve(31, 41, 48));
            restartedCoordinator.NotifyLocalChange();
            await EventuallyAsync(
                    () => restartedCoordinator.State is
                          {
                              HasPendingWrite: false
                          } state &&
                          state.Revision > restartedSnapshot.Revision &&
                          server.CommittedBatchCount(PostRestartOperationId) == 1,
                    "the replacement process could not continue editing after snapshot recovery")
                .ConfigureAwait(false);
            await restartedCoordinator.StopAsync().ConfigureAwait(false);
        }

        Require(server.OperationRequestCount(RestartOperationId) == 1 &&
                server.CommittedBatchCount(RestartOperationId) == 1,
            "replacement startup duplicated the operation committed before the restart");
        Require(server.OperationRequestCount(PostRestartOperationId) == 1 &&
                server.Document.Notes.Any(note =>
                    note.Id == PostRestartNoteId &&
                    note.Beat == BeatPosition.Preserve(31, 41, 48)),
            "replacement editing did not preserve its exact rational Beat");
        return "controlled process stop occurred after exactly-once commit and before ACK retry; " +
               "replacement token restore fetched 29+37/48 from the authoritative snapshot, " +
               "did not duplicate it, and committed new 31+41/48 work";
    }

    private static ChartDocument CreateBaseline()
    {
        var document = new ChartDocument
        {
            KeyCount = 4,
            Metadata = new ChartMetadata
            {
                Title = "Windows room resilience",
                Artist = "LumaChart Specs",
                Creator = "Avalonia",
                Version = "HTTP"
            },
            Timing =
            [
                new TimingEvent
                {
                    Id = Guid.Parse("a7000000-0000-0000-0000-000000000005"),
                    Beat = BeatPosition.Zero,
                    Bpm = 120
                }
            ]
        };
        document.Normalize();
        return document;
    }

    private static async Task EventuallyAsync(Func<bool> condition, string message)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(8))
                throw new InvalidOperationException(message);
            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ResilienceDocumentHost(ChartDocument initial) : IRoomChartSyncDocumentHost
    {
        private readonly object gate = new();
        private ChartDocument document = initial.Clone();
        private long changeSerial;
        private readonly List<RoomChartSyncViewState> viewStates = [];

        public ChartDocument Document
        {
            get { lock (gate) return document.Clone(); }
        }

        public IReadOnlyList<RoomChartSyncViewState> ViewStates
        {
            get { lock (gate) return viewStates.ToArray(); }
        }

        public void AddLocalNote(Guid id, int lane, BeatPosition beat)
        {
            lock (gate)
            {
                document.Notes.Add(new ChartNote { Id = id, Lane = lane, Beat = beat });
                document.Normalize();
                changeSerial++;
            }
        }

        public ValueTask<RoomChartLocalSnapshot> CaptureLocalSnapshotAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate) return ValueTask.FromResult(
                new RoomChartLocalSnapshot(document, changeSerial));
        }

        public ValueTask ApplyPlanAsync(
            RoomChartApplyPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (plan.ApplyDocument) document = plan.Document;
            }
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
    }

    private sealed class ResilientChartServer(ChartDocument initial, string token)
    {
        private readonly object gate = new();
        private readonly string token = token;
        private ChartDocument document = initial.Clone();
        private readonly List<AppliedOnlineChartOperation> operations = [];
        private readonly HashSet<Guid> receipts = [];
        private readonly List<Guid> operationIds = [];
        private readonly List<string> operationPayloadHashes = [];
        private long revision = 1;
        private int deltaRequestCount;
        private int chartOperationRequestCount;
        private int sseConnectionCount;
        private int committedOperationCount;
        private int injectedDeltaFailureCount;
        private int injectedMalformedDeltaCount;
        private int endpointAvailable = 1;
        private int endpointOutageFailureCount;
        private int failNextDeltaRequests;
        private int malformedNextDeltaResponses;
        private int dropNextOperationResponses = 1;

        public ChartDocument Document
        {
            get { lock (gate) return document.Clone(); }
        }

        public IReadOnlyList<Guid> OperationIds
        {
            get { lock (gate) return operationIds.ToArray(); }
        }

        public IReadOnlyList<string> OperationPayloadHashes
        {
            get { lock (gate) return operationPayloadHashes.ToArray(); }
        }

        public int DeltaRequestCount => Volatile.Read(ref deltaRequestCount);
        public int ChartOperationRequestCount => Volatile.Read(ref chartOperationRequestCount);
        public int SseConnectionCount => Volatile.Read(ref sseConnectionCount);
        public int CommittedOperationCount => Volatile.Read(ref committedOperationCount);
        public int InjectedDeltaFailureCount => Volatile.Read(ref injectedDeltaFailureCount);
        public int InjectedMalformedDeltaCount => Volatile.Read(ref injectedMalformedDeltaCount);
        public int EndpointOutageFailureCount =>
            Volatile.Read(ref endpointOutageFailureCount);

        public int OperationRequestCount(Guid operationId)
        {
            lock (gate) return operationIds.Count(id => id == operationId);
        }

        public int CommittedBatchCount(Guid operationId)
        {
            lock (gate) return operations.Count(operation =>
                operation.Batch.OperationId == operationId);
        }

        public void FailNextDeltaRequest() => Interlocked.Increment(ref failNextDeltaRequests);

        public void MalformedNextDeltaResponse() =>
            Interlocked.Increment(ref malformedNextDeltaResponses);

        public void DropNextOperationResponse() =>
            Interlocked.Increment(ref dropNextOperationResponses);

        public void SetEndpointAvailable(bool available) =>
            Volatile.Write(ref endpointAvailable, available ? 1 : 0);

        public void InjectRemoteNote(Guid id, int lane, BeatPosition beat)
        {
            lock (gate)
            {
                var remote = document.Clone();
                remote.Notes.Add(new ChartNote { Id = id, Lane = lane, Beat = beat });
                remote.Normalize();
                var batch = OnlineChartSyncCodec.CreateBatch(
                    revision,
                    document,
                    remote,
                    Guid.Parse("a7000000-0000-0000-0000-000000000006"));
                CommitLocked(batch, actorUserId: 92);
            }
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequireBearer(request);
            if (Volatile.Read(ref endpointAvailable) == 0)
            {
                Interlocked.Increment(ref endpointOutageFailureCount);
                throw new HttpRequestException(
                    "injected whole-endpoint outage during server restart");
            }
            var path = request.RequestUri?.AbsolutePath.TrimStart('/') ?? string.Empty;
            if (request.Method == HttpMethod.Get && path == "api/me")
                return JsonResponse(new OnlineUserProfileDto(
                    86,
                    "resilience-owner",
                    "Resilience Owner",
                    "user",
                    "default",
                    DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
                    12345));
            if (request.Method == HttpMethod.Get &&
                path == $"api/rooms/{RoomId:D}/chart/delta")
                return GetDeltaResponse(request.RequestUri);
            if (request.Method == HttpMethod.Post &&
                path == $"api/rooms/{RoomId:D}/chart/operations/delta")
                return await ApplyResponseAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Method == HttpMethod.Get &&
                path == $"api/rooms/{RoomId:D}/chart/events")
                return GetSseResponse();
            if (request.Method == HttpMethod.Get &&
                path == $"api/rooms/{RoomId:D}/chart")
            {
                lock (gate) return JsonResponse(SnapshotLocked());
            }
            throw new InvalidOperationException(
                $"unexpected resilience request {request.Method} {request.RequestUri}");
        }

        private HttpResponseMessage GetDeltaResponse(Uri? requestUri)
        {
            Interlocked.Increment(ref deltaRequestCount);
            if (TryConsume(ref failNextDeltaRequests))
            {
                Interlocked.Increment(ref injectedDeltaFailureCount);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = JsonContent.Create(
                        new { code = "resilience_delta_unavailable", message = "injected delta outage" },
                        options: JsonOptions)
                };
            }
            if (TryConsume(ref malformedNextDeltaResponses))
            {
                Interlocked.Increment(ref injectedMalformedDeltaCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"fromRevision\":3,\"toRevision\":4,\"operations\":[",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var after = 0L;
            var query = requestUri?.Query.TrimStart('?') ?? string.Empty;
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "after")
                    _ = long.TryParse(parts[1], out after);
            }
            lock (gate) return JsonResponse(DeltaLocked(after));
        }

        private async Task<HttpResponseMessage> ApplyResponseAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref chartOperationRequestCount);
            var payload = await (request.Content ??
                    throw new InvalidOperationException("chart operation body missing"))
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var batch = JsonSerializer.Deserialize<OnlineChartOperationBatch>(payload, JsonOptions) ??
                        throw new InvalidOperationException("chart operation body was invalid");
            OnlineChartDelta delta;
            var drop = false;
            lock (gate)
            {
                operationIds.Add(batch.OperationId);
                operationPayloadHashes.Add(Convert.ToHexString(SHA256.HashData(payload)));
                if (receipts.Add(batch.OperationId)) CommitLocked(batch, actorUserId: 86);
                delta = DeltaLocked(batch.BaseRevision);
                if (dropNextOperationResponses > 0)
                {
                    dropNextOperationResponses--;
                    drop = true;
                }
            }
            if (drop)
                throw new HttpRequestException(
                    "injected connection reset after the server committed the operation");
            return JsonResponse(delta);
        }

        private HttpResponseMessage GetSseResponse()
        {
            var call = Interlocked.Increment(ref sseConnectionCount);
            if (call == 1)
                throw new HttpRequestException("injected SSE connection drop");
            long current;
            lock (gate) current = revision;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes($"data: {current}\n\n"))
            };
            response.Content.Headers.ContentType =
                new MediaTypeHeaderValue("text/event-stream");
            return response;
        }

        private void CommitLocked(OnlineChartOperationBatch batch, long actorUserId)
        {
            revision++;
            var applied = new AppliedOnlineChartOperation(
                revision,
                actorUserId,
                batch,
                DateTimeOffset.UtcNow);
            operations.Add(applied);
            document = OnlineChartSyncCodec.ApplyOperations(document, [applied]);
            committedOperationCount++;
        }

        private OnlineChartDelta DeltaLocked(long after) => new(
            revision,
            operations.Where(operation => operation.Revision > after).ToArray(),
            [],
            false);

        private OnlineChartSnapshot SnapshotLocked()
        {
            var request = OnlineChartSyncCodec.CreateInitializeRequest(document);
            return new OnlineChartSnapshot(
                revision,
                request.KeyCount,
                request.Notes,
                request.Timing,
                [],
                request.Metadata);
        }

        private void RequireBearer(HttpRequestMessage request)
        {
            if (request.Headers.Authorization?.Scheme != "Bearer" ||
                request.Headers.Authorization.Parameter != token)
                throw new InvalidOperationException("room resilience bearer token mismatch");
        }

        private static bool TryConsume(ref int value)
        {
            while (true)
            {
                var observed = Volatile.Read(ref value);
                if (observed <= 0) return false;
                if (Interlocked.CompareExchange(ref value, observed - 1, observed) == observed)
                    return true;
            }
        }

        private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value, options: JsonOptions)
        };
    }

    private sealed class ResilientChartHandler(ResilientChartServer server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => server.SendAsync(request, cancellationToken);
    }

    private sealed class MemoryTokenStore(string token) : ISessionTokenStore
    {
        private string? value = token;

        public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }

        public ValueTask SaveAsync(string token, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value = token;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value = null;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record WindowsRoomSyncResilienceSpecResult(
    string Name,
    bool Passed,
    string Detail);

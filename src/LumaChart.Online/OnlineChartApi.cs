using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

/// <summary>
/// Cross-platform HTTP transport for room chart snapshots, deltas, operations,
/// locks and saved snapshots. The caller owns the injected HttpClient and all
/// local synchronization state.
/// </summary>
public sealed class OnlineChartApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<System.Net.HttpStatusCode, string>? fallbackMessageFactory;

    public OnlineChartApi(
        HttpClient client,
        Uri endpoint,
        TimeProvider? timeProvider = null,
        Func<System.Net.HttpStatusCode, string>? fallbackMessageFactory = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
            throw new ArgumentException("The online endpoint must be absolute.", nameof(endpoint));
        var isLoopbackDevelopment = endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp;
        if (endpoint.Scheme != Uri.UriSchemeHttps && !isLoopbackDevelopment)
            throw new ArgumentException(
                "The online endpoint must use HTTPS unless it is loopback development HTTP.",
                nameof(endpoint));

        this.client = client;
        this.endpoint = endpoint;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.fallbackMessageFactory = fallbackMessageFactory;
    }

    public Task<OnlineChartSnapshot?> InitializeAsync(
        Guid roomId,
        InitializeOnlineChartRequest chart,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<InitializeOnlineChartRequest, OnlineChartSnapshot>(
            HttpMethod.Put, $"api/rooms/{roomId:D}/chart", chart, accessToken, cancellationToken);

    public Task<OnlineChartSnapshot?> GetSnapshotAsync(
        Guid roomId,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<OnlineChartSnapshot>(
            $"api/rooms/{roomId:D}/chart", accessToken, cancellationToken);

    public Task<OnlineChartDelta?> GetDeltaAsync(
        Guid roomId,
        long afterRevision,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<OnlineChartDelta>(
            $"api/rooms/{roomId:D}/chart/delta?after={afterRevision}", accessToken, cancellationToken);

    public Task<OnlineChartApplyResult?> ApplyOperationsAsync(
        Guid roomId,
        OnlineChartOperationBatch batch,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineChartOperationBatch, OnlineChartApplyResult>(
            HttpMethod.Post,
            $"api/rooms/{roomId:D}/chart/operations",
            batch,
            accessToken,
            cancellationToken);

    public Task<OnlineChartDelta?> ApplyOperationsDeltaAsync(
        Guid roomId,
        OnlineChartOperationBatch batch,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineChartOperationBatch, OnlineChartDelta>(
            HttpMethod.Post,
            $"api/rooms/{roomId:D}/chart/operations/delta",
            batch,
            accessToken,
            cancellationToken);

    public Task<List<OnlineChartLock>?> ToggleLockAsync(
        Guid roomId,
        OnlineSyncBeat startBeat,
        OnlineSyncBeat endBeat,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineToggleChartLockRequest, List<OnlineChartLock>>(
            HttpMethod.Post,
            $"api/rooms/{roomId:D}/chart/locks/toggle",
            new OnlineToggleChartLockRequest(startBeat, endBeat),
            accessToken,
            cancellationToken);

    public Task<OnlineToggleChartLockSelectionResult?> ToggleLockSelectionAsync(
        Guid roomId,
        IReadOnlyList<OnlineChartLockRange> ranges,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return SendJsonAsync<OnlineToggleChartLockSelectionRequest, OnlineToggleChartLockSelectionResult>(
            HttpMethod.Post,
            $"api/rooms/{roomId:D}/chart/locks/toggle-selection",
            new OnlineToggleChartLockSelectionRequest(ranges.ToArray()),
            accessToken,
            cancellationToken);
    }

    public Task<List<OnlineChartSavedSnapshot>?> GetSavedSnapshotsAsync(
        Guid roomId,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<List<OnlineChartSavedSnapshot>>(
            $"api/rooms/{roomId:D}/chart/snapshots", accessToken, cancellationToken);

    public Task<OnlineChartSavedSnapshot?> CreateSavedSnapshotAsync(
        Guid roomId,
        string? name,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineCreateChartSnapshotRequest, OnlineChartSavedSnapshot>(
            HttpMethod.Post,
            $"api/rooms/{roomId:D}/chart/snapshots",
            new OnlineCreateChartSnapshotRequest(name),
            accessToken,
            cancellationToken);

    public Task<OnlineChartRollbackResult?> RollbackSavedSnapshotAsync(
        Guid roomId,
        Guid snapshotId,
        Guid operationId,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineRollbackChartSnapshotRequest, OnlineChartRollbackResult>(
            HttpMethod.Post,
            $"api/rooms/{roomId:D}/chart/snapshots/{snapshotId:D}/rollback",
            new OnlineRollbackChartSnapshotRequest(operationId),
            accessToken,
            cancellationToken);

    private async Task<T?> GetJsonAsync<T>(
        string path,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, path, accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<T>(response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<TResponse?> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest payload,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = AuthorizedRequest(method, path, accessToken);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<TResponse>(response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        OnlineHttpResponse.EnsureSuccessAsync(
            response, timeProvider, cancellationToken, fallbackMessageFactory);

    private HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        string? accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

/// <summary>
/// Cross-platform wire transport for discovering, creating and entering rooms.
/// The caller owns the injected HttpClient and all local room state.
/// </summary>
public sealed class OnlineRoomApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<System.Net.HttpStatusCode, string>? fallbackMessageFactory;

    public OnlineRoomApi(
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

    public async Task<OnlineRoomDirectoryDto?> GetVisibleRoomsAsync(
        string? query,
        int page,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/rooms?page={Math.Max(1, page)}&q={Uri.EscapeDataString((query ?? string.Empty).Trim())}";
        using var response = await client.GetAsync(Resolve(path), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<OnlineRoomDirectoryDto>(
                response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OnlineRoomSummaryDto?> FindRoomByNumberAsync(
        long roomNumber,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Get, $"api/rooms/by-number/{roomNumber}", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<OnlineRoomSummaryDto>(
                response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Probes only the HTTP status. This intentionally does not parse an error or
    /// success body because the desktop uses it while classifying lost membership.
    /// </summary>
    public async Task<OnlineRoomAvailability> ProbeRoomByNumberAsync(
        long roomNumber,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Get, $"api/rooms/by-number/{roomNumber}", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return OnlineRoomAvailability.Available;
        return response.StatusCode == System.Net.HttpStatusCode.NotFound
            ? OnlineRoomAvailability.NotFound
            : OnlineRoomAvailability.Unknown;
    }

    public async Task<OnlineCreatedRoomDto?> CreateRoomAsync(
        OnlineCreateRoomRequestDto room,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        using var request = AuthorizedRequest(HttpMethod.Post, "api/rooms", accessToken);
        request.Content = JsonContent.Create(room, options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<OnlineCreatedRoomDto>(
                response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OnlineRoomSummaryDto?> JoinRoomAsync(
        Guid roomId,
        OnlineJoinRoomRequestDto room,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(room);
        using var request = AuthorizedRequest(HttpMethod.Post, $"api/rooms/{roomId:D}/join", accessToken);
        request.Content = JsonContent.Create(room, options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<OnlineRoomSummaryDto>(
                response, JsonOptions, cancellationToken)
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
        var request = new HttpRequestMessage(method, Resolve(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private Uri Resolve(string relativePath) => new(endpoint, relativePath);
}

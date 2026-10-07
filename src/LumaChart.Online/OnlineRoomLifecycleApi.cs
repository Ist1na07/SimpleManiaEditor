using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

/// <summary>
/// Cross-platform HTTP transport for room invitations, membership and live state.
/// The caller owns the injected HttpClient and all local room state.
/// </summary>
public sealed class OnlineRoomLifecycleApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<HttpStatusCode, string>? fallbackMessageFactory;

    public OnlineRoomLifecycleApi(
        HttpClient client,
        Uri endpoint,
        TimeProvider? timeProvider = null,
        Func<HttpStatusCode, string>? fallbackMessageFactory = null)
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

    public Task<OnlineRoomInvitationDto?> InviteAsync(
        Guid roomId,
        long userId,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineRoomInviteRequestDto, OnlineRoomInvitationDto>(
            HttpMethod.Post,
            $"api/rooms/{roomId:D}/invitations",
            new OnlineRoomInviteRequestDto(userId),
            accessToken,
            cancellationToken);

    public async Task<IReadOnlyList<OnlineRoomInvitationDto>?> GetInvitationsAsync(
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, "api/me/invitations", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength == 0) return null;
        return await OnlineHttpResponse
            .ReadJsonAsync<List<OnlineRoomInvitationDto>>(response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OnlineRoomInvitationResponseDto> RespondToInvitationAsync(
        Guid invitationId,
        string action,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Post, $"api/me/invitations/{invitationId:D}/respond", accessToken);
        request.Content = JsonContent.Create(
            new OnlineRoomInviteResponseRequestDto(action), options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return new OnlineRoomInvitationResponseDto(
                OnlineRoomInvitationResponseKind.Canceled, null);
        var room = response.Content.Headers.ContentLength == 0
            ? null
            : await OnlineHttpResponse
                .ReadJsonAsync<OnlineRoomSummaryDto>(response, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        return new OnlineRoomInvitationResponseDto(OnlineRoomInvitationResponseKind.Room, room);
    }

    public Task UpdateMemberRoleAsync(
        Guid roomId,
        long userId,
        string memberRole,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            HttpMethod.Put,
            $"api/rooms/{roomId:D}/members/{userId}/role",
            new OnlineRoomMemberRoleRequestDto(memberRole),
            accessToken,
            cancellationToken);

    public Task UpdateMemberAccessAsync(
        Guid roomId,
        long userId,
        string memberRole,
        bool canEditMetadata,
        bool canEditTiming,
        bool canEditNotes,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            HttpMethod.Put,
            $"api/rooms/{roomId:D}/members/{userId}/access",
            new OnlineRoomMemberAccessRequestDto(
                memberRole, canEditMetadata, canEditTiming, canEditNotes),
            accessToken,
            cancellationToken);

    public async Task RemoveMemberAsync(
        Guid roomId,
        long userId,
        int banMinutes,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Delete,
            $"api/rooms/{roomId:D}/members/{userId}?banMinutes={banMinutes}",
            accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OnlineRoomLifecycleResult<OnlineRoomLiveStateDto>> GetLiveStateAsync(
        Guid roomId,
        long afterMessageId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Get,
            $"api/rooms/{roomId:D}/live?after={afterMessageId}",
            accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var specialOutcome = SpecialOutcome(response.StatusCode);
        if (specialOutcome != OnlineRoomLifecycleOutcome.Success)
            return new OnlineRoomLifecycleResult<OnlineRoomLiveStateDto>(specialOutcome, null);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var state = response.Content.Headers.ContentLength == 0
            ? null
            : await OnlineHttpResponse
                .ReadJsonAsync<OnlineRoomLiveStateDto>(response, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        return new OnlineRoomLifecycleResult<OnlineRoomLiveStateDto>(
            OnlineRoomLifecycleOutcome.Success, state);
    }

    public async Task<OnlineRoomLifecycleResult<OnlineRoomChatMessageDto>> SendChatMessageAsync(
        Guid roomId,
        string message,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(HttpMethod.Post, $"api/rooms/{roomId:D}/chat", accessToken);
        request.Content = JsonContent.Create(new OnlineRoomChatRequestDto(message), options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var specialOutcome = SpecialOutcome(response.StatusCode);
        if (specialOutcome != OnlineRoomLifecycleOutcome.Success)
            return new OnlineRoomLifecycleResult<OnlineRoomChatMessageDto>(specialOutcome, null);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var created = response.Content.Headers.ContentLength == 0
            ? null
            : await OnlineHttpResponse
                .ReadJsonAsync<OnlineRoomChatMessageDto>(response, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        return new OnlineRoomLifecycleResult<OnlineRoomChatMessageDto>(
            OnlineRoomLifecycleOutcome.Success, created);
    }

    public async Task<OnlineRoomLifecycleOutcome> SendHeartbeatAsync(
        Guid roomId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Post, $"api/rooms/{roomId:D}/heartbeat", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var specialOutcome = SpecialOutcome(response.StatusCode);
        if (specialOutcome != OnlineRoomLifecycleOutcome.Success) return specialOutcome;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return OnlineRoomLifecycleOutcome.Success;
    }

    public async Task<OnlineRoomLifecycleOutcome> LeaveAsync(
        Guid roomId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(HttpMethod.Delete, $"api/rooms/{roomId:D}", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return OnlineRoomLifecycleOutcome.NotFound;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return OnlineRoomLifecycleOutcome.Success;
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
        if (response.Content.Headers.ContentLength == 0) return default;
        return await OnlineHttpResponse.ReadJsonAsync<TResponse>(response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendCommandAsync<TRequest>(
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
    }

    private static OnlineRoomLifecycleOutcome SpecialOutcome(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.NotFound => OnlineRoomLifecycleOutcome.NotFound,
        HttpStatusCode.Forbidden => OnlineRoomLifecycleOutcome.Forbidden,
        _ => OnlineRoomLifecycleOutcome.Success
    };

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

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

/// <summary>
/// Cross-platform transport for player discovery, follows and public avatars.
/// The caller owns the injected HttpClient and avatar destination Stream.
/// </summary>
public sealed class OnlineCommunityApi
{
    private const int CopyBufferBytes = 128 * 1024;
    public static int MaximumNotificationPageSize => 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<HttpStatusCode, string>? fallbackMessageFactory;

    public OnlineCommunityApi(
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

    public Task<OnlinePlayerDirectoryDto?> GetPlayersAsync(
        int page,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetAuthorizedJsonAsync<OnlinePlayerDirectoryDto>(
            $"api/players?page={Math.Max(1, page)}", accessToken, cancellationToken);

    public Task<OnlinePlayerDirectoryDto?> SearchPlayersAsync(
        string query,
        int page,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetAuthorizedJsonAsync<OnlinePlayerDirectoryDto>(
            $"api/players/search?page={Math.Max(1, page)}&q={Uri.EscapeDataString(query)}",
            accessToken,
            cancellationToken);

    public Task<OnlinePlayerDirectoryDto?> GetLeaderboardAsync(
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetAuthorizedJsonAsync<OnlinePlayerDirectoryDto>(
            "api/players/leaderboard?limit=50", accessToken, cancellationToken);

    public Task<OnlinePlayerDirectoryDto?> GetFriendsAsync(
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetAuthorizedJsonAsync<OnlinePlayerDirectoryDto>(
            "api/me/friends", accessToken, cancellationToken);

    public Task<OnlinePlayerDirectoryDto?> GetFollowingAsync(
        int page,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetAuthorizedJsonAsync<OnlinePlayerDirectoryDto>(
            $"api/me/following?page={Math.Max(1, page)}", accessToken, cancellationToken);

    public Task<OnlinePlayerDirectoryDto?> GetFollowersAsync(
        int page,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetAuthorizedJsonAsync<OnlinePlayerDirectoryDto>(
            $"api/me/followers?page={Math.Max(1, page)}", accessToken, cancellationToken);

    /// <summary>
    /// Reads account notifications after an exclusive cursor. Pass <c>null</c>
    /// once when an online session starts to establish a high-water mark without
    /// replaying notifications from a previous offline session.
    /// </summary>
    public Task<OnlineUserNotificationPageDto?> GetNotificationsAsync(
        long? after,
        int limit,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        if (after < 0)
            throw new ArgumentOutOfRangeException(nameof(after), "Notification cursors cannot be negative.");
        limit = Math.Clamp(limit, 1, MaximumNotificationPageSize);
        var path = $"api/me/notifications?limit={limit}" +
                   (after.HasValue ? $"&after={after.Value}" : string.Empty);
        return GetAuthorizedJsonAsync<OnlineUserNotificationPageDto>(
            path, accessToken, cancellationToken);
    }

    public Task<OnlinePlayerProfileDto?> GetPlayerAsync(
        long userId,
        Guid? roomId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        var path = $"api/players/{userId}" +
                   (roomId.HasValue ? $"?roomId={roomId.Value:D}" : string.Empty);
        return GetAuthorizedJsonAsync<OnlinePlayerProfileDto>(
            path, accessToken, cancellationToken);
    }

    public Task<OnlineUserProfileDto?> SetFollowAsync(
        long userId,
        bool follow,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendAuthorizedForJsonAsync<OnlineUserProfileDto>(
            follow ? HttpMethod.Put : HttpMethod.Delete,
            $"api/players/{userId}/follow",
            accessToken,
            cancellationToken);

    public async Task<OnlineAvatarDownloadResult> DownloadAvatarAsync(
        string avatarKey,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(avatarKey);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The avatar destination must be writable.", nameof(destination));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(endpoint, $"api/avatars/{Uri.EscapeDataString(avatarKey)}"));
        using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var contentType = response.Content.Headers.ContentType?.ToString();
        var contentLength = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long transferred = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                        buffer.AsMemory(0, CopyBufferBytes), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                transferred += read;
                progress?.Report(transferred);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new OnlineAvatarDownloadResult(contentType, contentLength, transferred);
    }

    private async Task<T?> GetAuthorizedJsonAsync<T>(
        string path,
        string? accessToken,
        CancellationToken cancellationToken)
        where T : class
    {
        using var request = AuthorizedRequest(HttpMethod.Get, path, accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> SendAuthorizedForJsonAsync<T>(
        HttpMethod method,
        string path,
        string? accessToken,
        CancellationToken cancellationToken)
        where T : class
    {
        using var request = AuthorizedRequest(method, path, accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        if (response.Content.Headers.ContentLength == 0) return null;
        return await OnlineHttpResponse.ReadJsonAsync<T>(response, JsonOptions, cancellationToken)
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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

/// <summary>
/// Cross-platform wire transport for the authentication endpoints that create,
/// restore and revoke a server session. The caller owns the injected HttpClient.
/// </summary>
public sealed class OnlineAuthApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<System.Net.HttpStatusCode, string>? fallbackMessageFactory;

    public OnlineAuthApi(
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

    public async Task<OnlineLoginResponseDto?> LoginAsync(
        OnlineLoginRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await client.PostAsJsonAsync(
            Resolve("api/auth/login"), request, JsonOptions, cancellationToken).ConfigureAwait(false);
        await OnlineHttpResponse.EnsureSuccessAsync(
                response, timeProvider, cancellationToken, fallbackMessageFactory)
            .ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<OnlineLoginResponseDto>(
                response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OnlineLoginResponseDto?> RegisterAsync(
        OnlineRegistrationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await client.PostAsJsonAsync(
            Resolve("api/auth/register"), request, JsonOptions, cancellationToken).ConfigureAwait(false);
        await OnlineHttpResponse.EnsureSuccessAsync(
                response, timeProvider, cancellationToken, fallbackMessageFactory)
            .ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<OnlineLoginResponseDto>(
                response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OnlineOsuOAuthStartDto?> StartOsuLoginAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve("api/auth/osu/start"));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await OnlineHttpResponse.EnsureSuccessAsync(
                response, timeProvider, cancellationToken, fallbackMessageFactory)
            .ConfigureAwait(false);
        return ToLocalClock(
            await OnlineHttpResponse.ReadJsonAsync<OnlineOsuOAuthStartDto>(
                    response, JsonOptions, cancellationToken)
                .ConfigureAwait(false),
            response.Headers.Date);
    }

    public async Task<OnlineOsuOAuthStartDto?> StartOsuLinkAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(HttpMethod.Post, "api/me/osu/start", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await OnlineHttpResponse.EnsureSuccessAsync(
                response, timeProvider, cancellationToken, fallbackMessageFactory)
            .ConfigureAwait(false);
        return ToLocalClock(
            await OnlineHttpResponse.ReadJsonAsync<OnlineOsuOAuthStartDto>(
                    response, JsonOptions, cancellationToken)
                .ConfigureAwait(false),
            response.Headers.Date);
    }

    public async Task<OnlineOsuOAuthPollDto?> PollOsuAuthorizationAsync(
        string pollToken,
        CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsJsonAsync(
            Resolve("api/auth/osu/poll"),
            new OnlineOsuOAuthPollRequestDto(pollToken),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await OnlineHttpResponse.EnsureSuccessAsync(
                response, timeProvider, cancellationToken, fallbackMessageFactory)
            .ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<OnlineOsuOAuthPollDto>(
                response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OnlineUserProfileDto?> GetCurrentUserAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, "api/me", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await OnlineHttpResponse.EnsureSuccessAsync(
                response, timeProvider, cancellationToken, fallbackMessageFactory)
            .ConfigureAwait(false);
        return await OnlineHttpResponse.ReadJsonAsync<OnlineUserProfileDto>(
                response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(HttpMethod.Post, "api/auth/logout", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // The desktop contract intentionally performs a local logout for every HTTP
        // status. Only transport failures and caller cancellation escape this method.
    }

    /// <summary>
    /// Re-expresses the server's poll deadline on this machine's clock using the
    /// response Date header. Callers poll "while local now &lt; ExpiresAt"; with a local
    /// clock ten minutes fast the raw server deadline would already look expired and
    /// osu! sign-in could never complete. Without a Date header the value is kept.
    /// </summary>
    private OnlineOsuOAuthStartDto? ToLocalClock(
        OnlineOsuOAuthStartDto? start,
        DateTimeOffset? serverNow)
    {
        if (start == null || serverNow is not { } serverTime ||
            start.ExpiresAt <= DateTimeOffset.UnixEpoch)
            return start;
        return start with { ExpiresAt = timeProvider.GetUtcNow() + (start.ExpiresAt - serverTime) };
    }

    private HttpRequestMessage AuthorizedRequest(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, Resolve(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private Uri Resolve(string relativePath) => new(endpoint, relativePath);
}

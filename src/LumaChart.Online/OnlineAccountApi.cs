using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

/// <summary>
/// Cross-platform HTTP transport for application configuration and account profile operations.
/// The caller owns the injected HttpClient and avatar source Stream.
/// </summary>
public sealed class OnlineAccountApi
{
    private const int CopyBufferBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<HttpStatusCode, string>? fallbackMessageFactory;

    public OnlineAccountApi(
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

    public Task<OnlineAppConfigurationDto?> GetAppConfigurationAsync(
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<OnlineAppConfigurationDto>(
            "api/app-config", null, authorized: false, cancellationToken);

    public Task<OnlineAppConfigurationDto?> GetAppConfigurationAsAdminAsync(
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<OnlineAppConfigurationDto>(
            "api/admin/app-config", accessToken, authorized: true, cancellationToken);

    public Task<OnlineAppConfigurationDto?> UpdateAppConfigurationAsync(
        OnlineUpdateAppConfigurationRequestDto configuration,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineUpdateAppConfigurationRequestDto, OnlineAppConfigurationDto>(
            HttpMethod.Put,
            "api/admin/app-config",
            configuration,
            accessToken,
            authorized: true,
            cancellationToken);

    public Task<OnlineCaptchaChallengeDto?> GetCaptchaAsync(
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<OnlineCaptchaChallengeDto>(
            "api/auth/captcha", null, authorized: false, cancellationToken);

    public Task SendEmailVerificationCodeAsync(
        OnlineSendEmailVerificationCodeRequestDto requestBody,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        var authorized = requestBody.Purpose.Equals(
            "change_email", StringComparison.OrdinalIgnoreCase);
        return SendCommandAsync(
            HttpMethod.Post,
            "api/auth/email-codes",
            requestBody,
            accessToken,
            authorized,
            cancellationToken);
    }

    public Task<OnlineUserProfileDto?> ChangeEmailAsync(
        OnlineChangeEmailRequestDto requestBody,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineChangeEmailRequestDto, OnlineUserProfileDto>(
            HttpMethod.Put,
            "api/me/email",
            requestBody,
            accessToken,
            authorized: true,
            cancellationToken);

    public Task ResetPasswordAsync(
        OnlineResetPasswordRequestDto requestBody,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            HttpMethod.Post,
            "api/auth/reset-password",
            requestBody,
            null,
            authorized: false,
            cancellationToken);

    public Task ChangePasswordAsync(
        OnlineChangePasswordRequestDto requestBody,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendCommandAsync(
            HttpMethod.Post,
            "api/me/password",
            requestBody,
            accessToken,
            authorized: true,
            cancellationToken);

    public Task<OnlineUserProfileDto?> UpdateProfileAsync(
        OnlineUpdateProfileRequestDto requestBody,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineUpdateProfileRequestDto, OnlineUserProfileDto>(
            HttpMethod.Put,
            "api/me/profile",
            requestBody,
            accessToken,
            authorized: true,
            cancellationToken);

    public Task<OnlineUserProfileDto?> UpdateNameColorAsync(
        string? tagName,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineUpdateNameColorRequestDto, OnlineUserProfileDto>(
            HttpMethod.Put,
            "api/me/name-color",
            new OnlineUpdateNameColorRequestDto(tagName),
            accessToken,
            authorized: true,
            cancellationToken);

    public Task<OnlineChartingHeartbeatResponseDto?> SendChartingHeartbeatAsync(
        OnlineChartingHeartbeatRequestDto requestBody,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineChartingHeartbeatRequestDto, OnlineChartingHeartbeatResponseDto>(
            HttpMethod.Post,
            "api/profile/charting/heartbeat",
            requestBody,
            accessToken,
            authorized: true,
            cancellationToken);

    public async Task<OnlineUserProfileDto?> UploadAvatarAsync(
        Stream source,
        long declaredLength,
        string? accessToken,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The avatar source must be readable.", nameof(source));
        if (declaredLength < 0)
            throw new ArgumentOutOfRangeException(nameof(declaredLength));

        using var request = AuthorizedRequest(HttpMethod.Put, "api/me/avatar", accessToken);
        request.Content = new UploadStreamContent(
            source, declaredLength, progress, cancellationToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<OnlineUserProfileDto>(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<OnlineUserProfileDto?> SyncOsuAvatarAsync(
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendWithoutBodyAsync<OnlineUserProfileDto>(
            HttpMethod.Post,
            "api/me/osu/avatar/sync",
            accessToken,
            authorized: true,
            cancellationToken);

    private async Task<T?> GetJsonAsync<T>(
        string path,
        string? accessToken,
        bool authorized,
        CancellationToken cancellationToken)
        where T : class
    {
        using var request = Request(HttpMethod.Get, path, accessToken, authorized);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse?> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest payload,
        string? accessToken,
        bool authorized,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = Request(method, path, accessToken, authorized);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCommandAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest payload,
        string? accessToken,
        bool authorized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = Request(method, path, accessToken, authorized);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
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

    private async Task<TResponse?> SendWithoutBodyAsync<TResponse>(
        HttpMethod method,
        string path,
        string? accessToken,
        bool authorized,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var request = Request(method, path, accessToken, authorized);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        OnlineHttpResponse.EnsureSuccessAsync(
            response, timeProvider, cancellationToken, fallbackMessageFactory);

    private HttpRequestMessage Request(
        HttpMethod method,
        string path,
        string? accessToken,
        bool authorized) => authorized
        ? AuthorizedRequest(method, path, accessToken)
        : new HttpRequestMessage(method, new Uri(endpoint, path));

    private HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        string? accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private sealed class UploadStreamContent(
        Stream source,
        long length,
        IProgress<long>? progress,
        CancellationToken requestCancellationToken) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeSourceAsync(stream, requestCancellationToken);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            if (!requestCancellationToken.CanBeCanceled)
            {
                await SerializeSourceAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!cancellationToken.CanBeCanceled || cancellationToken == requestCancellationToken)
            {
                await SerializeSourceAsync(stream, requestCancellationToken).ConfigureAwait(false);
                return;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellationToken, cancellationToken);
            await SerializeSourceAsync(stream, linkedCancellation.Token).ConfigureAwait(false);
        }

        private async Task SerializeSourceAsync(
            Stream destination,
            CancellationToken cancellationToken)
        {
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
        }

        protected override bool TryComputeLength(out long contentLength)
        {
            contentLength = length;
            return true;
        }
    }
}

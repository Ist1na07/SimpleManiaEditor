using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

/// <summary>
/// Cross-platform streaming transport for room resources.
/// The caller owns the injected HttpClient and all source/destination Streams.
/// </summary>
public sealed class OnlineResourceApi
{
    private const int CopyBufferBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<System.Net.HttpStatusCode, string>? fallbackMessageFactory;

    public OnlineResourceApi(
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

    public async Task<OnlineRoomResourceManifestDto?> GetManifestAsync(
        Guid roomId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = AuthorizedRequest(
            HttpMethod.Get, $"api/rooms/{roomId:D}/resources", accessToken);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await OnlineHttpResponse
            .ReadJsonAsync<OnlineRoomResourceManifestDto>(response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OnlineRoomResourcePlanDto?> PlanAsync(
        Guid roomId,
        IReadOnlyList<OnlineRoomResourceEntryDto> entries,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var request = AuthorizedRequest(
            HttpMethod.Post, $"api/rooms/{roomId:D}/resources/plan", accessToken);
        request.Content = JsonContent.Create(
            new OnlineRoomResourcePlanRequestDto(entries), options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength == 0) return null;
        return await OnlineHttpResponse
            .ReadJsonAsync<OnlineRoomResourcePlanDto>(response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UploadBlobAsync(
        Guid roomId,
        string sha256,
        Stream source,
        long declaredLength,
        string? accessToken,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The resource source must be readable.", nameof(source));
        if (declaredLength < 0)
            throw new ArgumentOutOfRangeException(nameof(declaredLength));

        using var request = AuthorizedRequest(
            HttpMethod.Put, $"api/rooms/{roomId:D}/resources/blobs/{sha256}", accessToken);
        request.Content = new UploadStreamContent(
            source, declaredLength, progress, cancellationToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OnlineRoomResourceManifestDto?> CommitAsync(
        Guid roomId,
        IReadOnlyList<OnlineRoomResourceEntryDto> entries,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        using var request = AuthorizedRequest(
            HttpMethod.Post, $"api/rooms/{roomId:D}/resources/commit", accessToken);
        request.Content = JsonContent.Create(
            new OnlineRoomResourcePlanRequestDto(entries), options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength == 0) return null;
        return await OnlineHttpResponse
            .ReadJsonAsync<OnlineRoomResourceManifestDto>(response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long> DownloadBlobAsync(
        Guid roomId,
        string sha256,
        string? accessToken,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        return await DownloadAsync(
                $"api/rooms/{roomId:D}/resources/files/{sha256}",
                accessToken,
                destination,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<long> DownloadArchiveAsync(
        Guid roomId,
        string? accessToken,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync(
            $"api/rooms/{roomId:D}/resources/archive",
            accessToken,
            destination,
            progress,
            cancellationToken);

    private async Task<long> DownloadAsync(
        string path,
        string? accessToken,
        Stream destination,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The resource destination must be writable.", nameof(destination));

        using var request = AuthorizedRequest(HttpMethod.Get, path, accessToken);
        using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
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
        return transferred;
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

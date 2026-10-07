using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace LumaChart.Online;

/// <summary>
/// Cross-platform streaming transport for room chart revision notifications.
/// The caller owns the injected HttpClient and all reconnect policy.
/// </summary>
public sealed class OnlineChartEventApi
{
    /// <summary>
    /// The server writes a ": keep-alive" comment every 15 seconds. Three missed
    /// keep-alives mean the connection is half-open (NAT timeout, sleeping laptop,
    /// silently dropped Wi-Fi), where ReadLineAsync would otherwise wait forever and
    /// the feed would keep reporting Connected without delivering revisions.
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(45);

    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<System.Net.HttpStatusCode, string>? fallbackMessageFactory;
    private readonly TimeSpan idleTimeout = DefaultIdleTimeout;

    public OnlineChartEventApi(
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

    /// <summary>
    /// Longest silence tolerated while waiting for response headers or the next
    /// line, including keep-alive comments. It must stay above the server's 15 s
    /// keep-alive interval.
    /// </summary>
    public TimeSpan IdleTimeout
    {
        get => idleTimeout;
        init
        {
            if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(IdleTimeout));
            idleTimeout = value;
        }
    }

    public async Task ListenForRevisionsAsync(
        Guid roomId,
        string? accessToken,
        Func<long, Task> revisionAvailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisionAvailable);

        // The event client intentionally has no whole-response timeout. This watchdog
        // is re-armed by every received line, so only a silent connection expires.
        using var idle = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, idle.Token);
        idle.CancelAfter(idleTimeout);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, new Uri(endpoint, $"api/rooms/{roomId:D}/chart/events"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            await OnlineHttpResponse.EnsureSuccessAsync(
                    response, timeProvider, linked.Token, fallbackMessageFactory)
                .ConfigureAwait(false);

            await using var source = await response.Content.ReadAsStreamAsync(linked.Token)
                .ConfigureAwait(false);
            using var reader = new StreamReader(
                source,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                idle.CancelAfter(idleTimeout);
                var line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                if (line == null) return;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!long.TryParse(
                        line.AsSpan(5).Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var rawRevision)) continue;
                // The callback may apply backpressure; it is not part of the idle window.
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                await revisionAvailable(rawRevision).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or ObjectDisposedException &&
            idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw IdleFailure(exception);
        }
    }

    private IOException IdleFailure(Exception innerException) =>
        new(
            $"The room chart event stream sent nothing for {idleTimeout.TotalSeconds:0.###} seconds.",
            innerException);
}

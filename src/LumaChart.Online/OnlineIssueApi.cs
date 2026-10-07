using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

/// <summary>
/// HTTP transport for preview-tester issue reports. Authentication and token
/// invalidation remain owned by <see cref="OnlineAccountSession"/>.
/// </summary>
public sealed class OnlineIssueApi
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly TimeProvider timeProvider;
    private readonly Func<HttpStatusCode, string>? fallbackMessageFactory;

    public OnlineIssueApi(
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

    public Task<OnlineIssueReportDto?> CreateAsync(
        OnlineCreateIssueReportRequestDto report,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineCreateIssueReportRequestDto, OnlineIssueReportDto>(
            HttpMethod.Post,
            "api/me/issues",
            report,
            accessToken,
            cancellationToken);

    public Task<OnlineIssueReportPageDto?> GetMineAsync(
        int page,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<OnlineIssueReportPageDto>(
            $"api/me/issues?page={Math.Max(1, page)}",
            accessToken,
            cancellationToken);

    public async Task DeleteMineAsync(
        Guid issueId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        if (issueId == Guid.Empty)
            throw new ArgumentException("Issue id is required.", nameof(issueId));
        using var request = AuthorizedRequest(
            HttpMethod.Delete,
            $"api/me/issues/{issueId:D}",
            accessToken);
        using var response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsAdminAsync(
        Guid issueId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        if (issueId == Guid.Empty)
            throw new ArgumentException("Issue id is required.", nameof(issueId));
        using var request = AuthorizedRequest(
            HttpMethod.Delete,
            $"api/admin/issues/{issueId:D}",
            accessToken);
        using var response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<OnlineIssueReportPageDto?> GetAsAdminAsync(
        int page,
        string? status,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        var query = $"api/admin/issues?page={Math.Max(1, page)}";
        if (!string.IsNullOrWhiteSpace(status))
            query += $"&status={Uri.EscapeDataString(status.Trim())}";
        return GetJsonAsync<OnlineIssueReportPageDto>(query, accessToken, cancellationToken);
    }

    public Task<OnlineIssueReportDto?> UpdateAsAdminAsync(
        Guid issueId,
        OnlineUpdateIssueReportRequestDto update,
        string? accessToken,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<OnlineUpdateIssueReportRequestDto, OnlineIssueReportDto>(
            HttpMethod.Put,
            $"api/admin/issues/{issueId:D}",
            update,
            accessToken,
            cancellationToken);

    private async Task<T?> GetJsonAsync<T>(
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

    private async Task<TResponse?> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest payload,
        string? accessToken,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var request = AuthorizedRequest(method, path, accessToken);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        string? accessToken)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        OnlineHttpResponse.EnsureSuccessAsync(
            response,
            timeProvider,
            cancellationToken,
            fallbackMessageFactory);

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        if (response.Content.Headers.ContentLength == 0) return null;
        return await OnlineHttpResponse.ReadJsonAsync<T>(response, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}

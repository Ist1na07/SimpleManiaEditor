using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LumaChart.Online;

public static class OnlineHttpResponse
{
    /// <summary>
    /// Error code for a 2xx response whose body is not the JSON contract, such as
    /// a public Wi-Fi sign-in page that answers every request with 200 and HTML.
    /// </summary>
    public const string InvalidResponseCode = "invalid_response";

    private const string InvalidResponseMessage = "服务器返回了无法识别的数据，请检查网络连接后重试。";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default,
        Func<HttpStatusCode, string>? fallbackMessageFactory = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (response.IsSuccessStatusCode) return;

        ApiError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter == null && response.Headers.RetryAfter?.Date is { } retryAt)
        {
            var remaining = retryAt - timeProvider.GetUtcNow();
            retryAfter = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
        }

        throw new OnlineApiException(
            error?.Message ?? fallbackMessageFactory?.Invoke(response.StatusCode) ??
                $"Online request failed with HTTP status {(int)response.StatusCode}.",
            error?.Code,
            response.StatusCode,
            retryAfter);
    }

    /// <summary>
    /// Reads a successful JSON body. A body that is not the expected JSON surfaces as
    /// <see cref="OnlineApiException"/> with <see cref="InvalidResponseCode"/>, so the
    /// dialogs' existing online-error filters handle it instead of an unhandled
    /// <see cref="JsonException"/> reaching the UI dispatcher and closing the app.
    /// A body cut off by the transport still surfaces as its IOException.
    /// </summary>
    public static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw InvalidResponse(exception);
        }
    }

    /// <summary>Creates the shared exception for an unusable successful response.</summary>
    public static OnlineApiException InvalidResponse(Exception? innerException = null) =>
        new(InvalidResponseMessage, InvalidResponseCode, innerException: innerException);

    public static bool IsInvalidResponse(OnlineApiException exception) =>
        string.Equals(exception.Code, InvalidResponseCode, StringComparison.OrdinalIgnoreCase);

    private sealed record ApiError(string Code, string Message);
}

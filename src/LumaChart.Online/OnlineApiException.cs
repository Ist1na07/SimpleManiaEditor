using System.Net;

namespace LumaChart.Online;

public sealed class OnlineApiException(
    string message,
    string? code = null,
    HttpStatusCode? statusCode = null,
    TimeSpan? retryAfter = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string? Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

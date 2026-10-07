using System.Net;
using LumaChart.Avalonia.Services;
using LumaChart.Online;

namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsOsuAuthorizationCoordinator
{
    private static readonly TimeSpan InitialPollDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan MaximumPollDelay = TimeSpan.FromSeconds(4);

    public static async Task<OnlineUserProfileDto> AuthorizeAsync(
        OnlineAccountSession session,
        bool linkCurrentAccount,
        Action<string>? reportStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        OnlineOsuOAuthStartDto start;
        try
        {
            start = linkCurrentAccount
                ? await session.StartOsuLinkAsync(cancellationToken)
                : await session.StartOsuLoginAsync(cancellationToken);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new OnlineApiException(
                "当前服务器尚未启用 osu! 登录与绑定，请等待服务器更新完成后重试。",
                "osu_server_feature_unavailable",
                exception.StatusCode,
                innerException: exception);
        }
        var authorizationUri = ResolveAuthorizationUri(start.AuthorizationUrl)
                               ?? throw new OnlineApiException(
                                   "服务器返回的 osu! 授权链接无效。",
                                   "invalid_osu_authorization_url");
        try
        {
            await PlatformExternalUriServices.OpenAsync(
                authorizationUri,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new OnlineApiException(
                "无法打开系统浏览器，请确认设备已安装并启用浏览器。",
                "browser_launch_failed",
                innerException: exception);
        }

        reportStatus?.Invoke("已打开 osu! 授权页面；完成授权后会自动返回。");
        var delay = InitialPollDelay;
        while (DateTimeOffset.UtcNow < start.ExpiresAt)
        {
            await Task.Delay(delay, cancellationToken);
            try
            {
                using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                pollTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await session.PollOsuAuthorizationAsync(
                    start.PollToken,
                    pollTimeout.Token);
                if (result.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
                {
                    delay = InitialPollDelay;
                    continue;
                }
                if (result.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new OnlineApiException(
                        string.IsNullOrWhiteSpace(result.Message)
                            ? "osu! 授权未完成。"
                            : result.Message,
                        "osu_authorization_failed");
                }
                if (result.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
                    session.CurrentUser is { Id: > 0 } user)
                    return user;
                throw new OnlineApiException("服务器返回的 osu! 授权状态无效。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                reportStatus?.Invoke("等待服务器响应超时，正在自动重试…");
                delay = NextDelay(delay);
            }
            // Android reports a failed request as WebException where Windows
            // raises HttpRequestException. osu! authorization can wait minutes
            // for the browser, so an unhandled one here would end the process
            // mid-flow instead of retrying.
            catch (Exception exception) when (
                exception is HttpRequestException or WebException)
            {
                reportStatus?.Invoke("网络暂时不可用，正在自动重试 osu! 授权状态…");
                delay = NextDelay(delay);
            }
            catch (OnlineApiException exception) when (IsTransient(exception))
            {
                reportStatus?.Invoke("服务器暂时不可用，正在自动重试 osu! 授权状态…");
                delay = exception.RetryAfter is { } retry
                    ? TimeSpan.FromMilliseconds(Math.Clamp(
                        retry.TotalMilliseconds,
                        InitialPollDelay.TotalMilliseconds,
                        MaximumPollDelay.TotalMilliseconds))
                    : NextDelay(delay);
            }
        }
        throw new OnlineApiException(
            "osu! 授权已超时，请重新发起。",
            "oauth_flow_expired");
    }

    internal static Uri? ResolveAuthorizationUri(string? value)
    {
        var uri = WindowsExternalUriPolicy.Resolve(value);
        return uri is
        {
            Scheme: "https",
            Host: "osu.ppy.sh",
            AbsolutePath: "/oauth/authorize"
        }
            ? uri
            : null;
    }

    internal static Uri? ResolveProfileUri(OnlineOsuAccountDto? account)
    {
        if (account is not { UserId: > 0 }) return null;
        var uri = WindowsExternalUriPolicy.Resolve(account.ProfileUrl);
        return uri is { Scheme: "https", Host: "osu.ppy.sh" } &&
               uri.AbsolutePath.Equals(
                   $"/users/{account.UserId}",
                   StringComparison.Ordinal)
            ? uri
            : null;
    }

    internal static string FormatUnavailableFeatureFailure(Exception exception) =>
        exception is OnlineApiException
        {
            StatusCode: HttpStatusCode.NotFound
        }
            ? "当前服务器尚未启用 osu! 登录与绑定，请等待服务器更新完成后重试。"
            : exception.Message;

    private static bool IsTransient(OnlineApiException exception) =>
        exception.StatusCode == HttpStatusCode.TooManyRequests ||
        exception.StatusCode.HasValue &&
        (int)exception.StatusCode.Value >= (int)HttpStatusCode.InternalServerError;

    private static TimeSpan NextDelay(TimeSpan current) =>
        TimeSpan.FromMilliseconds(Math.Min(
            MaximumPollDelay.TotalMilliseconds,
            Math.Max(InitialPollDelay.TotalMilliseconds, current.TotalMilliseconds * 1.7)));
}

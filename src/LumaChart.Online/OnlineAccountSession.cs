using System.Net;
using LumaChart.Storage;

namespace LumaChart.Online;

/// <summary>
/// Platform-neutral account-session orchestration shared by desktop clients.
/// HTTP transport remains in the existing APIs and durable token protection is
/// supplied by the platform through <see cref="ISessionTokenStore"/>.
/// </summary>
public sealed class OnlineAccountSession
{
    public const int MaximumRecentNotificationCount = 100;

    private readonly OnlineAuthApi authApi;
    private readonly OnlineAccountApi accountApi;
    private readonly OnlineIssueApi issueApi;
    private readonly OnlineAdminApi adminApi;
    private readonly OnlineCommunityApi communityApi;
    private readonly OnlineRoomApi roomApi;
    private readonly OnlineRoomLifecycleApi roomLifecycleApi;
    private readonly OnlineResourceApi resourceApi;
    private readonly OnlineChartApi chartApi;
    private readonly OnlineChartEventApi chartEventApi;
    private readonly OnlineSessionTokenState sessionToken;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim notificationPollGate = new(1, 1);
    private readonly object notificationHistoryGate = new();
    private readonly List<OnlineUserNotificationDto> recentNotifications = [];
    // Account, room and permission state is read by the UI thread and written by
    // presence/live pollers on the thread pool. Every check-then-write runs under
    // this gate and the room/permission tuple is replaced as one immutable value.
    private readonly object stateGate = new();
    private OnlineUserProfileDto? currentUser;
    private RoomAccess roomAccess = RoomAccess.None;
    private long notificationCursorUserId;
    private long? notificationCursor;
    private bool notificationPollingUnauthorized;
    private long roomSessionGeneration;
    // Advanced by logout, a different account signing in and token invalidation.
    // A request records it before its first await and may write its result back
    // only while it is unchanged, so a response that finishes after logout can
    // never reinstall a room or an account profile into a signed-out session.
    private long sessionGeneration;

    public OnlineAccountSession(
        HttpClient client,
        Uri endpoint,
        ISessionTokenStore tokenStore,
        TimeProvider? timeProvider = null,
        Func<HttpStatusCode, string>? fallbackMessageFactory = null,
        HttpClient? eventClient = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(tokenStore);

        this.timeProvider = timeProvider ?? TimeProvider.System;
        authApi = new OnlineAuthApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        accountApi = new OnlineAccountApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        issueApi = new OnlineIssueApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        adminApi = new OnlineAdminApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        communityApi = new OnlineCommunityApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        roomApi = new OnlineRoomApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        roomLifecycleApi = new OnlineRoomLifecycleApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        resourceApi = new OnlineResourceApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        chartApi = new OnlineChartApi(
            client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        chartEventApi = new OnlineChartEventApi(
            eventClient ?? client,
            endpoint,
            this.timeProvider,
            fallbackMessageFactory);
        sessionToken = new OnlineSessionTokenState(tokenStore);
    }

    public OnlineUserProfileDto? CurrentUser
    {
        get
        {
            lock (stateGate) return currentUser;
        }
    }

    public string? CurrentAccessToken => sessionToken.CurrentToken;
    public bool IsAuthenticated => sessionToken.CurrentToken != null && CurrentUser is { Id: > 0 };
    public bool CanUseAdminApi =>
        IsAuthenticated &&
        CurrentUser is { Id: > 0 } user &&
        string.Equals(user.Role, "owner", StringComparison.OrdinalIgnoreCase);
    public OnlineRoomSummaryDto? CurrentRoom => ReadRoomAccess().Room;
    public string CurrentMemberRole => ReadRoomAccess().MemberRole;
    public bool CanEditMetadata => ReadRoomAccess().CanEditMetadata;
    public bool CanEditTiming => ReadRoomAccess().CanEditTiming;
    public bool CanEditNotes => ReadRoomAccess().CanEditNotes;
    public IReadOnlyList<OnlineUserNotificationDto> RecentNotifications
    {
        get
        {
            lock (notificationHistoryGate) return recentNotifications.ToArray();
        }
    }

    public event EventHandler? Changed;
    public event EventHandler<OnlineUserNotificationsReceivedEventArgs>? NotificationsReceived;

    /// <summary>
    /// Restores a protected local token. A definitive 401 revokes the local
    /// token, while transient transport failures deliberately preserve it for a
    /// later retry.
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        lock (stateGate)
        {
            generation = ++sessionGeneration;
            currentUser = null;
        }
        ResetNotificationPollingState();
        var token = await sessionToken.RestoreAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            RaiseChanged();
            return false;
        }

        OnlineUserProfileDto? user;
        try
        {
            user = await authApi.GetCurrentUserAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Revoke only the token this restore read. A login that stored a new
            // token while GET /api/me was in flight keeps its session.
            await ClearSessionTokenIfCurrentAsync(token).ConfigureAwait(false);
            RaiseChanged();
            return false;
        }

        var valid = RequireValidUser(user, "服务器没有返回有效的账号资料。");
        lock (stateGate)
        {
            // A logout or another sign-in completed first; its state is newer.
            if (generation != sessionGeneration ||
                !string.Equals(sessionToken.CurrentToken, token, StringComparison.Ordinal))
                return false;
            currentUser = valid;
        }
        RaiseChanged();
        return true;
    }

    public async Task<OnlineUserProfileDto> LoginAsync(
        string username,
        string password,
        Guid captchaId,
        string captchaAnswer,
        CancellationToken cancellationToken = default)
    {
        var response = await authApi.LoginAsync(
                new OnlineLoginRequestDto(username, password, captchaId, captchaAnswer),
                cancellationToken)
            .ConfigureAwait(false);
        return await InstallLoginResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OnlineUserProfileDto> RegisterAsync(
        string username,
        string password,
        string? displayName,
        string email,
        string emailCode,
        CancellationToken cancellationToken = default)
    {
        var response = await authApi.RegisterAsync(
                new OnlineRegistrationRequestDto(username, password, displayName, email, emailCode),
                cancellationToken)
            .ConfigureAwait(false);
        return await InstallLoginResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OnlineOsuOAuthStartDto> StartOsuLoginAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await authApi.StartOsuLoginAsync(cancellationToken).ConfigureAwait(false);
        return RequireValidOsuStart(response);
    }

    public async Task<OnlineOsuOAuthStartDto> StartOsuLinkAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAuthenticatedAsync(
                token => authApi.StartOsuLinkAsync(token, cancellationToken))
            .ConfigureAwait(false);
        return RequireValidOsuStart(response);
    }

    public async Task<OnlineOsuOAuthPollDto> PollOsuAuthorizationAsync(
        string pollToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pollToken))
            throw new ArgumentException("OAuth poll token is required.", nameof(pollToken));
        var generation = CaptureSessionGeneration();
        var response = await authApi.PollOsuAuthorizationAsync(
                pollToken, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OnlineApiException("服务器没有返回 osu! 授权状态。");
        // JSON may omit non-nullable strings; callers compare Status directly.
        response = response with
        {
            Status = response.Status ?? string.Empty,
            Message = response.Message ?? string.Empty
        };
        if (!response.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            return response;
        if (response.Login != null)
        {
            var installed = await InstallLoginResponseAsync(
                    response.Login, cancellationToken)
                .ConfigureAwait(false);
            return response with { Login = response.Login with { User = installed } };
        }
        if (response.User != null)
        {
            if (!IsAuthenticated)
                throw new OnlineApiException(
                    "绑定完成前本地登录会话已经结束，请重新登录后刷新主页。",
                    "authentication_required");
            var installed = InstallAuthoritativeUser(
                response.User, "服务器没有返回绑定后的账号资料。", generation);
            return response with { User = installed };
        }
        throw new OnlineApiException("服务器返回的 osu! 授权结果无效。");
    }

    public async Task<OnlineCaptchaChallengeDto> GetCaptchaAsync(
        CancellationToken cancellationToken = default) =>
        await accountApi.GetCaptchaAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new OnlineApiException("服务器没有返回人机验证码。");

    public Task SendEmailVerificationCodeAsync(
        string email,
        string purpose,
        Guid captchaId,
        string captchaAnswer,
        CancellationToken cancellationToken = default)
    {
        var request = new OnlineSendEmailVerificationCodeRequestDto(
            email,
            purpose,
            captchaId,
            captchaAnswer);
        if (!purpose.Equals("change_email", StringComparison.OrdinalIgnoreCase))
            return accountApi.SendEmailVerificationCodeAsync(
                request,
                sessionToken.CurrentToken,
                cancellationToken);
        return ExecuteAuthenticatedCommandAsync(
            token => accountApi.SendEmailVerificationCodeAsync(
                request,
                token,
                cancellationToken));
    }

    public Task ResetPasswordAsync(
        string email,
        string emailCode,
        string newPassword,
        CancellationToken cancellationToken = default) =>
        accountApi.ResetPasswordAsync(
            new OnlineResetPasswordRequestDto(email, emailCode, newPassword),
            cancellationToken);

    public async Task<OnlineUserProfileDto> ChangeEmailAsync(
        string currentPassword,
        string newEmail,
        string emailCode,
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureSessionGeneration();
        var updated = await ExecuteAuthenticatedAsync(
                token => accountApi.ChangeEmailAsync(
                    new OnlineChangeEmailRequestDto(currentPassword, newEmail, emailCode),
                    token,
                    cancellationToken))
            .ConfigureAwait(false);
        return InstallAuthoritativeUser(updated, "服务器没有返回更新后的账号资料。", generation);
    }

    public Task ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default) =>
        ExecuteAuthenticatedCommandAsync(
                token => accountApi.ChangePasswordAsync(
                    new OnlineChangePasswordRequestDto(currentPassword, newPassword),
                    token,
                    cancellationToken));

    public async Task<OnlineAppConfigurationDto> GetAppConfigurationAsync(
        CancellationToken cancellationToken = default) =>
        await accountApi.GetAppConfigurationAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new OnlineApiException("服务器没有返回应用配置。");

    public async Task<OnlineAppConfigurationDto> GetAppConfigurationAsAdminAsync(
        CancellationToken cancellationToken = default)
    {
        RequireAdminAccess();
        return await ExecuteAuthenticatedAsync(
                async token => await accountApi.GetAppConfigurationAsAdminAsync(
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回应用配置。"))
            .ConfigureAwait(false);
    }

    public async Task<OnlineAppConfigurationDto> UpdateAppConfigurationAsAdminAsync(
        OnlineUpdateAppConfigurationRequestDto configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RequireAdminAccess();
        return await ExecuteAuthenticatedAsync(
                async token => await accountApi.UpdateAppConfigurationAsync(
                        configuration,
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回更新后的应用配置。"))
            .ConfigureAwait(false);
    }

    public async Task<OnlineIssueReportDto> CreateIssueReportAsync(
        OnlineCreateIssueReportRequestDto report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return await ExecuteAuthenticatedAsync(
                async token => await issueApi.CreateAsync(report, token, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回问题反馈编号。"))
            .ConfigureAwait(false);
    }

    public async Task<OnlineIssueReportPageDto> GetMyIssueReportsAsync(
        int page = 1,
        CancellationToken cancellationToken = default) =>
        await ExecuteAuthenticatedAsync(
                async token => await issueApi.GetMineAsync(page, token, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回问题反馈列表。"))
            .ConfigureAwait(false);

    public async Task DeleteMyIssueReportAsync(
        Guid issueId,
        CancellationToken cancellationToken = default)
    {
        if (issueId == Guid.Empty)
            throw new ArgumentException("Issue id is required.", nameof(issueId));
        await ExecuteAuthenticatedAsync(
                async token =>
                {
                    await issueApi.DeleteMineAsync(issueId, token, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                })
            .ConfigureAwait(false);
    }

    public async Task<OnlineIssueReportPageDto> GetIssueReportsAsAdminAsync(
        int page = 1,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        RequireAdminAccess();
        return await ExecuteAuthenticatedAsync(
                async token => await issueApi.GetAsAdminAsync(
                        page,
                        status,
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回测试反馈列表。"))
            .ConfigureAwait(false);
    }

    public async Task<OnlineIssueReportDto> UpdateIssueReportAsAdminAsync(
        Guid issueId,
        OnlineUpdateIssueReportRequestDto update,
        CancellationToken cancellationToken = default)
    {
        if (issueId == Guid.Empty) throw new ArgumentException("Issue id is required.", nameof(issueId));
        ArgumentNullException.ThrowIfNull(update);
        RequireAdminAccess();
        return await ExecuteAuthenticatedAsync(
                async token => await issueApi.UpdateAsAdminAsync(
                        issueId,
                        update,
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回更新后的测试反馈。"))
            .ConfigureAwait(false);
    }

    public async Task DeleteIssueReportAsAdminAsync(
        Guid issueId,
        CancellationToken cancellationToken = default)
    {
        if (issueId == Guid.Empty)
            throw new ArgumentException("Issue id is required.", nameof(issueId));
        RequireAdminAccess();
        await ExecuteAuthenticatedAsync(
                async token =>
                {
                    await issueApi.DeleteAsAdminAsync(issueId, token, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                })
            .ConfigureAwait(false);
    }

    public async Task<OnlinePlayerProfileDto> SetUserCustomLabelAsAdminAsync(
        long userId,
        string? label,
        CancellationToken cancellationToken = default)
    {
        RequireAdminUserId(userId);
        RequireAdminAccess();
        return await ExecuteAuthenticatedAsync(
                async token => await adminApi.SetUserCustomLabelAsync(
                        userId,
                        label,
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回更新后的玩家资料。"))
            .ConfigureAwait(false);
    }

    public async Task<OnlineUserProfileDto> GetUserAsAdminAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        RequireAdminUserId(userId);
        RequireAdminAccess();
        var user = await ExecuteAuthenticatedAsync(
                token => adminApi.GetUserAsync(userId, token, cancellationToken))
            .ConfigureAwait(false);
        return RequireValidUser(user, "服务器没有返回有效的用户资料。");
    }

    public async Task<OnlineUserProfileDto> UpdateUserAsAdminAsync(
        long userId,
        OnlineAdminUpdateUserRequestDto update,
        CancellationToken cancellationToken = default)
    {
        RequireAdminUserId(userId);
        ArgumentNullException.ThrowIfNull(update);
        RequireAdminAccess();
        var generation = CaptureSessionGeneration();
        var updated = RequireValidUser(
            await ExecuteAuthenticatedAsync(
                    token => adminApi.UpdateUserAsync(
                        userId,
                        update,
                        token,
                        cancellationToken))
                .ConfigureAwait(false),
            "服务器没有返回更新后的用户资料。");
        return InstallAdminUserIfCurrent(updated, generation);
    }

    public async Task<OnlineUserProfileDto> UploadUserAvatarAsAdminAsync(
        long userId,
        Stream source,
        long declaredLength,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireAdminUserId(userId);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The avatar source must be readable.", nameof(source));
        if (declaredLength is <= 0 or > 4L * 1024 * 1024)
            throw new OnlineApiException("头像文件必须介于 1 字节和 4 MB 之间。");
        RequireAdminAccess();
        var generation = CaptureSessionGeneration();
        var updated = RequireValidUser(
            await ExecuteAuthenticatedAsync(
                    token => adminApi.UploadUserAvatarAsync(
                        userId,
                        source,
                        declaredLength,
                        token,
                        progress,
                        cancellationToken))
                .ConfigureAwait(false),
            "服务器没有返回更新后的用户资料。");
        return InstallAdminUserIfCurrent(updated, generation);
    }

    public async Task<OnlineUserProfileDto> ResetUserAvatarAsAdminAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        RequireAdminUserId(userId);
        RequireAdminAccess();
        var generation = CaptureSessionGeneration();
        var updated = RequireValidUser(
            await ExecuteAuthenticatedAsync(
                    token => adminApi.ResetUserAvatarAsync(
                        userId,
                        token,
                        cancellationToken))
                .ConfigureAwait(false),
            "服务器没有返回更新后的用户资料。");
        return InstallAdminUserIfCurrent(updated, generation);
    }

    public async Task<OnlineUserProfileDto> UpdateProfileAsync(
        string displayName,
        string bio,
        bool hidePublicAvatar,
        bool hidePublicName,
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureSessionGeneration();
        var updated = await ExecuteAuthenticatedAsync(
                token => accountApi.UpdateProfileAsync(
                    new OnlineUpdateProfileRequestDto(
                        displayName,
                        bio,
                        hidePublicAvatar,
                        hidePublicName),
                    token,
                    cancellationToken))
            .ConfigureAwait(false);
        return InstallAuthoritativeUser(updated, "服务器没有返回更新后的账号资料。", generation);
    }

    public async Task<OnlineUserProfileDto> UpdateNameColorAsync(
        string? tagName,
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureSessionGeneration();
        var updated = await ExecuteAuthenticatedAsync(
                token => accountApi.UpdateNameColorAsync(tagName, token, cancellationToken))
            .ConfigureAwait(false);
        return InstallAuthoritativeUser(updated, "服务器没有返回更新后的账号资料。", generation);
    }

    public async Task<OnlineUserProfileDto> UploadAvatarAsync(
        Stream source,
        long declaredLength,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (declaredLength is <= 0 or > 4L * 1024 * 1024)
            throw new OnlineApiException("头像文件必须介于 1 字节和 4 MB 之间。");
        var generation = CaptureSessionGeneration();
        var updated = await ExecuteAuthenticatedAsync(
                token => accountApi.UploadAvatarAsync(
                    source,
                    declaredLength,
                    token,
                    progress,
                    cancellationToken))
            .ConfigureAwait(false);
        return InstallAuthoritativeUser(updated, "服务器没有返回更新后的账号资料。", generation);
    }

    public async Task<OnlineUserProfileDto> SyncOsuAvatarAsync(
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureSessionGeneration();
        var updated = await ExecuteAuthenticatedAsync(
                token => accountApi.SyncOsuAvatarAsync(token, cancellationToken))
            .ConfigureAwait(false);
        return InstallAuthoritativeUser(updated, "服务器没有返回同步头像后的账号资料。", generation);
    }

    public async Task<OnlineChartingHeartbeatResponseDto?> SendChartingHeartbeatAsync(
        Guid editorSessionId,
        long operationSerial,
        bool isEditing,
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated) return null;
        var generation = CaptureSessionGeneration();
        var result = await ExecuteAuthenticatedAsync(
                token => accountApi.SendChartingHeartbeatAsync(
                    new OnlineChartingHeartbeatRequestDto(editorSessionId, operationSerial, isEditing),
                    token,
                    cancellationToken))
            .ConfigureAwait(false);
        var changed = false;
        lock (stateGate)
        {
            if (result != null &&
                generation == sessionGeneration &&
                currentUser is { } user &&
                user.ChartingSeconds != result.ChartingSeconds)
            {
                currentUser = user with { ChartingSeconds = result.ChartingSeconds };
                changed = true;
            }
        }
        if (changed) RaiseChanged();
        return result;
    }

    public async Task<OnlinePlayerDirectoryDto> GetPlayersAsync(
        int page,
        CancellationToken cancellationToken = default) =>
        RequireDirectory(await ExecuteAuthenticatedAsync(
                token => communityApi.GetPlayersAsync(page, token, cancellationToken))
            .ConfigureAwait(false));

    public async Task<OnlinePlayerDirectoryDto> SearchPlayersAsync(
        string query,
        int page = 1,
        CancellationToken cancellationToken = default) =>
        RequireDirectory(await ExecuteAuthenticatedAsync(
                token => communityApi.SearchPlayersAsync(query, page, token, cancellationToken))
            .ConfigureAwait(false));

    public async Task<OnlinePlayerDirectoryDto> GetLeaderboardAsync(
        CancellationToken cancellationToken = default) =>
        RequireDirectory(await ExecuteAuthenticatedAsync(
                token => communityApi.GetLeaderboardAsync(token, cancellationToken))
            .ConfigureAwait(false));

    public async Task<OnlinePlayerDirectoryDto> GetFriendsAsync(
        CancellationToken cancellationToken = default) =>
        RequireDirectory(await ExecuteAuthenticatedAsync(
                token => communityApi.GetFriendsAsync(token, cancellationToken))
            .ConfigureAwait(false));

    public async Task<OnlinePlayerDirectoryDto> GetFollowingAsync(
        int page,
        CancellationToken cancellationToken = default) =>
        RequireDirectory(await ExecuteAuthenticatedAsync(
                token => communityApi.GetFollowingAsync(page, token, cancellationToken))
            .ConfigureAwait(false));

    public async Task<OnlinePlayerDirectoryDto> GetFollowersAsync(
        int page,
        CancellationToken cancellationToken = default) =>
        RequireDirectory(await ExecuteAuthenticatedAsync(
                token => communityApi.GetFollowersAsync(page, token, cancellationToken))
            .ConfigureAwait(false));

    public async Task<OnlinePlayerProfileDto> GetPlayerAsync(
        long userId,
        Guid? roomId = null,
        CancellationToken cancellationToken = default) =>
        await ExecuteAuthenticatedAsync(
                async token => await communityApi.GetPlayerAsync(
                        userId,
                        roomId,
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回玩家资料。"))
            .ConfigureAwait(false);

    public async Task<OnlineUserProfileDto> SetFollowAsync(
        long userId,
        bool follow,
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureSessionGeneration();
        var updated = await ExecuteAuthenticatedAsync(
                token => communityApi.SetFollowAsync(userId, follow, token, cancellationToken))
            .ConfigureAwait(false);
        return InstallAuthoritativeUser(updated, "服务器没有返回更新后的账号资料。", generation);
    }

    public Task<OnlineAvatarDownloadResult> DownloadAvatarAsync(
        string avatarKey,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        communityApi.DownloadAvatarAsync(avatarKey, destination, progress, cancellationToken);

    public async Task<OnlineRoomDirectoryDto> GetVisibleRoomsAsync(
        string? query,
        int page,
        CancellationToken cancellationToken = default) =>
        await roomApi.GetVisibleRoomsAsync(query, page, cancellationToken).ConfigureAwait(false)
        ?? throw new OnlineApiException("服务器没有返回房间目录。");

    public async Task<OnlineRoomSummaryDto> FindRoomByNumberAsync(
        long roomNumber,
        CancellationToken cancellationToken = default) =>
        await ExecuteAuthenticatedAsync(
                async token => await roomApi.FindRoomByNumberAsync(
                        roomNumber,
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回房间资料。"))
            .ConfigureAwait(false);

    public async Task<OnlineRoomSummaryDto> CreateRoomAsync(
        string name,
        bool isPrivate,
        string? password,
        bool isHidden,
        CancellationToken cancellationToken = default)
    {
        var generation = CaptureSessionGeneration();
        var created = await ExecuteAuthenticatedAsync(
                async token => await roomApi.CreateRoomAsync(
                        new OnlineCreateRoomRequestDto(name, isPrivate, password, isHidden),
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器返回了空房间结果。"))
            .ConfigureAwait(false);
        var room = created.Room ?? throw new OnlineApiException("服务器返回了空房间结果。");
        room = InstallRoom(room, "owner", true, true, true, generation);
        return room;
    }

    public async Task<OnlineRoomSummaryDto> JoinRoomAsync(
        Guid roomId,
        string? password = null,
        string memberRole = "editor",
        CancellationToken cancellationToken = default)
    {
        memberRole = NormalizeMemberRole(memberRole);
        var generation = CaptureSessionGeneration();
        var joined = await ExecuteAuthenticatedAsync(
                async token => await roomApi.JoinRoomAsync(
                        roomId,
                        new OnlineJoinRoomRequestDto(password, memberRole),
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器返回了空房间结果。"))
            .ConfigureAwait(false);
        joined = InstallRoom(
            joined,
            memberRole,
            canEditMetadata: false,
            canEditTiming: false,
            canEditNotes: memberRole == "editor",
            generation);
        await GetRoomLiveStateAsync(0, cancellationToken).ConfigureAwait(false);
        return CurrentRoom ?? joined;
    }

    public async Task<IReadOnlyList<OnlineRoomInvitationDto>> GetRoomInvitationsAsync(
        CancellationToken cancellationToken = default)
    {
        var invitations = await ExecuteAuthenticatedAsync(
                async token => await roomLifecycleApi.GetInvitationsAsync(token, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回房间邀请。"))
            .ConfigureAwait(false);
        // Drop records that cannot be acted on and fill missing display text.
        return invitations
            .Where(invitation => invitation is { } && invitation.Id != Guid.Empty &&
                                 invitation.RoomId != Guid.Empty)
            .Select(invitation => invitation with
            {
                RoomName = invitation.RoomName ?? string.Empty,
                InviterDisplayName = invitation.InviterDisplayName ?? string.Empty,
                InviterAvatarKey = invitation.InviterAvatarKey ?? string.Empty
            })
            .ToArray();
    }

    public async Task<OnlineRoomInvitationDto> InviteToCurrentRoomAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var room = CurrentRoom ?? throw new OnlineApiException("当前没有加入联机房间。");
        return await ExecuteAuthenticatedAsync(
                async token => await roomLifecycleApi.InviteAsync(
                        room.Id,
                        userId,
                        token,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new OnlineApiException("服务器没有返回房间邀请。"))
            .ConfigureAwait(false);
    }

    public async Task<OnlineRoomInvitationResponseDto> RespondToRoomInvitationAsync(
        Guid invitationId,
        bool accept,
        CancellationToken cancellationToken = default) =>
        await RespondToRoomInvitationAsync(
                invitationId,
                accept ? "join" : "cancel",
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<OnlineRoomInvitationResponseDto> RespondToRoomInvitationAsync(
        Guid invitationId,
        string action,
        CancellationToken cancellationToken = default)
    {
        action = NormalizeInvitationAction(action);
        var generation = CaptureSessionGeneration();
        var response = await ExecuteAuthenticatedAsync(
                token => roomLifecycleApi.RespondToInvitationAsync(
                    invitationId,
                    action,
                    token,
                    cancellationToken))
            .ConfigureAwait(false);
        if (action != "cancel" && response.Kind == OnlineRoomInvitationResponseKind.Room)
        {
            var room = response.Room ?? throw new OnlineApiException("服务器没有返回受邀房间资料。");
            // Accepting an invitation is the password-bypass capability granted
            // by the server. Never ask the user to re-enter a hidden-room password.
            var memberRole = action == "watch" ? "viewer" : "editor";
            room = InstallRoom(room, memberRole, false, false, memberRole == "editor", generation);
            response = response with { Room = room };
            await GetRoomLiveStateAsync(0, cancellationToken).ConfigureAwait(false);
        }
        return response;
    }

    public async Task<OnlineRoomLifecycleResult<OnlineRoomLiveStateDto>> GetRoomLiveStateAsync(
        long afterMessageId,
        CancellationToken cancellationToken = default)
    {
        OnlineRoomSummaryDto room;
        long generation;
        lock (stateGate)
        {
            room = roomAccess.Room ?? throw new OnlineApiException("当前没有加入联机房间。");
            generation = roomSessionGeneration;
        }
        var result = await ExecuteAuthenticatedAsync(
                token => roomLifecycleApi.GetLiveStateAsync(
                    room.Id,
                    Math.Max(0, afterMessageId),
                    token,
                    cancellationToken))
            .ConfigureAwait(false);
        if (result.Outcome != OnlineRoomLifecycleOutcome.Success)
        {
            ClearRoomIfCurrent(room.Id, generation);
            return result;
        }

        // A 200 without a usable body proves nothing about membership; a captive
        // portal or proxy can produce it. Report it instead of leaving the room.
        var live = NormalizeLiveState(result.Value) ?? throw OnlineHttpResponse.InvalidResponse();
        lock (stateGate)
        {
            // A live response can finish after an explicit leave, kick/close signal, or
            // a switch to another room. Such an old success must never reinstall the
            // previous room in the local session.
            if (roomSessionGeneration != generation || roomAccess.Room?.Id != room.Id)
                return new OnlineRoomLifecycleResult<OnlineRoomLiveStateDto>(
                    OnlineRoomLifecycleOutcome.Forbidden,
                    null);

            var userId = currentUser?.Id;
            var currentMember = live.Members.FirstOrDefault(member => member.UserId == userId);
            roomAccess = currentMember == null
                ? roomAccess with { Room = live.Room }
                : new RoomAccess(
                    live.Room,
                    NormalizeMemberRole(currentMember.MemberRole),
                    currentMember.CanEditMetadata,
                    currentMember.CanEditTiming,
                    currentMember.CanEditNotes);
        }
        RaiseChanged();
        return new OnlineRoomLifecycleResult<OnlineRoomLiveStateDto>(
            OnlineRoomLifecycleOutcome.Success,
            live);
    }

    public async Task<OnlineRoomLifecycleResult<OnlineRoomChatMessageDto>> SendRoomChatAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var room = CurrentRoom ?? throw new OnlineApiException("当前没有加入联机房间。");
        return await ExecuteAuthenticatedAsync(
                token => roomLifecycleApi.SendChatMessageAsync(
                    room.Id,
                    message,
                    token,
                    cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<OnlineRoomLifecycleOutcome> SendRoomHeartbeatAsync(
        CancellationToken cancellationToken = default)
    {
        OnlineRoomSummaryDto room;
        long generation;
        lock (stateGate)
        {
            room = roomAccess.Room ?? throw new OnlineApiException("当前没有加入联机房间。");
            generation = roomSessionGeneration;
        }
        var outcome = await ExecuteAuthenticatedAsync(
                token => roomLifecycleApi.SendHeartbeatAsync(room.Id, token, cancellationToken))
            .ConfigureAwait(false);
        if (outcome != OnlineRoomLifecycleOutcome.Success)
            ClearRoomIfCurrent(room.Id, generation);
        return outcome;
    }

    public Task UpdateRoomMemberRoleAsync(
        long userId,
        string memberRole,
        CancellationToken cancellationToken = default)
    {
        var room = CurrentRoom ?? throw new OnlineApiException("当前没有加入联机房间。");
        return ExecuteAuthenticatedCommandAsync(
            token => roomLifecycleApi.UpdateMemberRoleAsync(
                room.Id,
                userId,
                NormalizeMemberRole(memberRole),
                token,
                cancellationToken));
    }

    public Task UpdateRoomMemberAccessAsync(
        long userId,
        string memberRole,
        bool canEditMetadata,
        bool canEditTiming,
        bool canEditNotes,
        CancellationToken cancellationToken = default)
    {
        var room = CurrentRoom ?? throw new OnlineApiException("当前没有加入联机房间。");
        return ExecuteAuthenticatedCommandAsync(
            token => roomLifecycleApi.UpdateMemberAccessAsync(
                room.Id,
                userId,
                NormalizeMemberRole(memberRole),
                canEditMetadata,
                canEditTiming,
                canEditNotes,
                token,
                cancellationToken));
    }

    public Task RemoveRoomMemberAsync(
        long userId,
        int banMinutes,
        CancellationToken cancellationToken = default)
    {
        var room = CurrentRoom ?? throw new OnlineApiException("当前没有加入联机房间。");
        return ExecuteAuthenticatedCommandAsync(
            token => roomLifecycleApi.RemoveMemberAsync(
                room.Id,
                userId,
                Math.Max(0, banMinutes),
                token,
                cancellationToken));
    }

    public async Task LeaveCurrentRoomAsync(CancellationToken cancellationToken = default)
    {
        OnlineRoomSummaryDto? room;
        long generation;
        lock (stateGate)
        {
            room = roomAccess.Room;
            generation = roomSessionGeneration;
        }
        if (room == null) return;
        try
        {
            await ExecuteAuthenticatedAsync(
                    token => roomLifecycleApi.LeaveAsync(room.Id, token, cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            ClearRoomIfCurrent(room.Id, generation);
        }
    }

    public async Task<OnlineRoomResourceManifestDto> GetRoomResourceManifestAsync(
        Guid roomId,
        CancellationToken cancellationToken = default) =>
        await ExecuteRoomRequestAsync(
                token => resourceApi.GetManifestAsync(roomId, token, cancellationToken),
                "服务器没有返回资源清单。",
                roomId)
            .ConfigureAwait(false);

    public async Task<OnlineRoomResourcePlanDto> PlanRoomResourcesAsync(
        Guid roomId,
        IReadOnlyList<OnlineRoomResourceEntryDto> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return await ExecuteRoomRequestAsync(
                token => resourceApi.PlanAsync(roomId, entries, token, cancellationToken),
                "服务器没有返回资源复用计划。",
                roomId)
            .ConfigureAwait(false);
    }

    public Task UploadRoomResourceBlobAsync(
        Guid roomId,
        string sha256,
        Stream source,
        long declaredLength,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRoomCommandAsync(
            token => resourceApi.UploadBlobAsync(
                roomId,
                sha256,
                source,
                declaredLength,
                token,
                progress,
                cancellationToken),
            roomId);

    public async Task<OnlineRoomResourceManifestDto> CommitRoomResourcesAsync(
        Guid roomId,
        IReadOnlyList<OnlineRoomResourceEntryDto> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var manifest = await ExecuteRoomRequestAsync(
                token => resourceApi.CommitAsync(roomId, entries, token, cancellationToken),
                "服务器没有返回资源清单。",
                roomId)
            .ConfigureAwait(false);
        var changed = false;
        lock (stateGate)
        {
            // Read the room once under the gate: a concurrent leave can clear it
            // between two unsynchronized reads.
            if (roomAccess.Room is { } current && current.Id == roomId)
            {
                roomAccess = roomAccess with
                {
                    Room = current with
                    {
                        ResourcesReady = true,
                        ResourceBytes = manifest.TotalBytes
                    }
                };
                changed = true;
            }
        }
        if (changed) RaiseChanged();
        return manifest;
    }

    public Task<long> DownloadRoomResourceBlobAsync(
        Guid roomId,
        string sha256,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRoomValueRequestAsync(
            token => resourceApi.DownloadBlobAsync(
                roomId,
                sha256,
                token,
                destination,
                progress,
                cancellationToken),
            roomId);

    public Task<long> DownloadRoomResourceArchiveAsync(
        Guid roomId,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRoomValueRequestAsync(
            token => resourceApi.DownloadArchiveAsync(
                roomId,
                token,
                destination,
                progress,
                cancellationToken),
            roomId);

    public async Task<OnlineChartSnapshot> InitializeRoomChartAsync(
        Guid roomId,
        InitializeOnlineChartRequest chart,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return await ExecuteRoomRequestAsync(
                token => chartApi.InitializeAsync(roomId, chart, token, cancellationToken),
                "服务器没有返回联机谱面快照。",
                roomId)
            .ConfigureAwait(false);
    }

    public Task<OnlineChartSnapshot> GetRoomChartAsync(
        Guid roomId,
        CancellationToken cancellationToken = default) =>
        ExecuteRoomRequestAsync(
            token => chartApi.GetSnapshotAsync(roomId, token, cancellationToken),
            "服务器没有返回联机谱面快照。",
            roomId);

    public Task<OnlineChartDelta> GetRoomChartDeltaAsync(
        Guid roomId,
        long afterRevision,
        CancellationToken cancellationToken = default) =>
        ExecuteRoomRequestAsync(
            token => chartApi.GetDeltaAsync(
                roomId,
                Math.Max(0, afterRevision),
                token,
                cancellationToken),
            "服务器没有返回联机谱面增量。",
            roomId);

    public Task<OnlineChartDelta> ApplyRoomChartOperationsDeltaAsync(
        Guid roomId,
        OnlineChartOperationBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        batch = batch with { BaseRevision = Math.Max(0, batch.BaseRevision) };
        return ExecuteRoomRequestAsync(
            token => chartApi.ApplyOperationsDeltaAsync(roomId, batch, token, cancellationToken),
            "服务器没有返回联机谱面增量。",
            roomId);
    }

    public async Task ListenForRoomChartEventsAsync(
        Guid roomId,
        Func<long, Task> revisionAvailable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisionAvailable);
        await ExecuteRoomCommandAsync(
                token => chartEventApi.ListenForRevisionsAsync(
                    roomId,
                    token,
                    revision => revisionAvailable(Math.Max(0, revision)),
                    cancellationToken),
                roomId)
            .ConfigureAwait(false);
    }

    public Task<OnlineToggleChartLockSelectionResult> ToggleRoomChartLockSelectionAsync(
        Guid roomId,
        IReadOnlyList<OnlineChartLockRange> ranges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        return ExecuteRoomRequestAsync(
            token => chartApi.ToggleLockSelectionAsync(
                roomId,
                ranges,
                token,
                cancellationToken),
            "服务器没有返回选区锁定结果。",
            roomId);
    }

    public async Task<IReadOnlyList<OnlineChartSavedSnapshot>> GetRoomChartSnapshotsAsync(
        Guid roomId,
        CancellationToken cancellationToken = default) =>
        await ExecuteRoomRequestAsync(
                token => chartApi.GetSavedSnapshotsAsync(roomId, token, cancellationToken),
                "服务器没有返回房间快照。",
                roomId)
            .ConfigureAwait(false);

    public Task<OnlineChartSavedSnapshot> CreateRoomChartSnapshotAsync(
        Guid roomId,
        string? name,
        CancellationToken cancellationToken = default) =>
        ExecuteRoomRequestAsync(
            token => chartApi.CreateSavedSnapshotAsync(
                roomId,
                name,
                token,
                cancellationToken),
            "服务器没有返回创建的房间快照。",
            roomId);

    public Task<OnlineChartRollbackResult> RollbackRoomChartSnapshotAsync(
        Guid roomId,
        Guid snapshotId,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        ExecuteRoomRequestAsync(
            token => chartApi.RollbackSavedSnapshotAsync(
                roomId,
                snapshotId,
                operationId,
                token,
                cancellationToken),
            "服务器没有返回快照回滚结果。",
            roomId);

    /// <summary>
    /// Polls follow notifications with the WPF high-water semantics. The first
    /// request after authentication establishes a cursor and never replays old
    /// notifications. Cursor state advances only after every page succeeds.
    /// </summary>
    public async Task<IReadOnlyList<OnlineUserNotificationDto>> PollNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAuthenticated || notificationPollingUnauthorized) return [];
        if (!await notificationPollGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return [];
        try
        {
            if (!IsAuthenticated || notificationPollingUnauthorized) return [];
            if (CurrentUser is not { Id: > 0 } pollingUser) return [];
            var userId = pollingUser.Id;
            var token = RequireAccessToken();
            if (notificationCursorUserId != userId)
            {
                notificationCursor = null;
                notificationCursorUserId = userId;
                notificationPollingUnauthorized = false;
            }

            var startCursor = notificationCursor;
            var page = await ReadNotificationPageAsync(startCursor, token, cancellationToken)
                .ConfigureAwait(false);
            if (page == null) return [];
            if (!startCursor.HasValue)
            {
                if (IsSameSession(userId, token)) notificationCursor = Math.Max(0, page.NextCursor);
                return [];
            }

            var candidateCursor = startCursor.Value;
            var seenIds = new HashSet<long>();
            var notifications = new List<OnlineUserNotificationDto>();
            while (true)
            {
                foreach (var notification in page.Notifications.OrderBy(item => item.Id))
                {
                    if (notification.Id <= candidateCursor || !seenIds.Add(notification.Id)) continue;
                    notifications.Add(notification);
                }

                var pageCursor = page.Notifications.Count == 0
                    ? page.NextCursor
                    : Math.Max(page.NextCursor, page.Notifications.Max(item => item.Id));
                if (pageCursor < candidateCursor || page.HasMore && pageCursor == candidateCursor)
                    throw new OnlineApiException(
                        "服务器返回了无效的关注提醒游标。",
                        "notification_cursor_invalid");
                candidateCursor = pageCursor;
                if (!page.HasMore) break;
                page = await ReadNotificationPageAsync(candidateCursor, token, cancellationToken)
                    .ConfigureAwait(false);
                if (page == null) return [];
            }

            if (!IsSameSession(userId, token)) return [];
            notificationCursor = candidateCursor;
            var committed = notifications.OrderBy(item => item.Id).ToArray();
            PublishNotifications(userId, committed);
            return committed;
        }
        finally
        {
            notificationPollGate.Release();
        }
    }

    /// <summary>
    /// Logout is local-authoritative: server unavailability must never trap the
    /// user in a locally authenticated state.
    /// </summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        // Requests already in flight belong to the session being closed. Their
        // results must not be written back while or after this logout runs.
        lock (stateGate) sessionGeneration++;
        ResetNotificationPollingState();
        if (CurrentRoom != null)
        {
            try
            {
                await LeaveCurrentRoomAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                       OnlineApiException or HttpRequestException or System.Net.WebException or TaskCanceledException)
            {
                ClearRoom();
            }
        }
        var token = sessionToken.CurrentToken;
        Exception? cancellationFailure = null;
        if (token != null)
        {
            try
            {
                await authApi.LogoutAsync(token, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                cancellationFailure = exception;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        await ClearSessionTokenAsync(CancellationToken.None).ConfigureAwait(false);
        lock (stateGate)
        {
            sessionGeneration++;
            currentUser = null;
            ClearRoomLocked();
        }
        ResetNotificationPollingState();
        RaiseChanged();
        if (cancellationFailure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
    }

    private async Task<OnlineUserProfileDto> InstallLoginResponseAsync(
        OnlineLoginResponseDto? response,
        CancellationToken cancellationToken)
    {
        // ExpiresAt is server time. Comparing it with this machine's clock made
        // every sign-in fail whenever the local clock ran fast by more than the
        // session lifetime, so only its presence is validated here; the server
        // remains the authority that rejects an expired token with 401.
        if (response == null ||
            string.IsNullOrWhiteSpace(response.AccessToken) ||
            response.User == null ||
            response.User.Id <= 0 ||
            response.ExpiresAt <= DateTimeOffset.UnixEpoch)
        {
            throw new OnlineApiException("服务器返回的登录会话无效。");
        }

        var user = RequireValidUser(response.User, "服务器返回的账号资料无效。");
        await StoreSessionTokenAsync(response.AccessToken, cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            // A different account invalidates every request of the previous one
            // and the previous account's room membership.
            if (currentUser?.Id != user.Id)
            {
                sessionGeneration++;
                ClearRoomLocked();
            }
            currentUser = user;
        }
        ResetNotificationPollingState();
        RaiseChanged();
        return user;
    }

    private static OnlineOsuOAuthStartDto RequireValidOsuStart(OnlineOsuOAuthStartDto? response)
    {
        if (response == null ||
            string.IsNullOrWhiteSpace(response.AuthorizationUrl) ||
            string.IsNullOrWhiteSpace(response.PollToken) ||
            response.ExpiresAt <= DateTimeOffset.UnixEpoch ||
            !Uri.TryCreate(response.AuthorizationUrl, UriKind.Absolute, out var authorizationUri) ||
            authorizationUri.Scheme != Uri.UriSchemeHttps)
            throw new OnlineApiException("服务器返回的 osu! 授权请求无效。");
        return response;
    }

    private OnlineUserProfileDto InstallAuthoritativeUser(
        OnlineUserProfileDto? user,
        string message,
        long generation)
    {
        var valid = RequireValidUser(user, message);
        lock (stateGate)
        {
            // The request belongs to a session that has since logged out, switched
            // account or lost its token; never reinstall its profile.
            if (generation != sessionGeneration || currentUser is not { Id: > 0 } current)
                throw SessionEnded();
            if (current.Id != valid.Id)
                throw new OnlineApiException("服务器返回了不属于当前会话的账号资料。");
            currentUser = valid;
        }
        RaiseChanged();
        return valid;
    }

    private static OnlinePlayerDirectoryDto RequireDirectory(OnlinePlayerDirectoryDto? directory) =>
        directory ?? throw new OnlineApiException("服务器没有返回玩家目录。");

    private string RequireAccessToken()
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(sessionToken.CurrentToken))
            throw new OnlineApiException("请先登录账号。", "authentication_required");
        return sessionToken.CurrentToken;
    }

    private async Task<T> ExecuteAuthenticatedAsync<T>(Func<string, Task<T>> action)
    {
        var token = RequireAccessToken();
        try
        {
            return await action(token).ConfigureAwait(false);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            await InvalidateUnauthorizedSessionAsync(token).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ExecuteAuthenticatedCommandAsync(Func<string, Task> action)
    {
        var token = RequireAccessToken();
        try
        {
            await action(token).ConfigureAwait(false);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            await InvalidateUnauthorizedSessionAsync(token).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<T> ExecuteRoomRequestAsync<T>(
        Func<string, Task<T?>> action,
        string emptyResponseMessage,
        Guid expectedRoomId)
        where T : class
    {
        var token = RequireAccessToken();
        try
        {
            // A 2xx with an empty/null body is an unusable response, not a refusal:
            // the invalid_response code lets room sync retry it instead of treating
            // an accepted write as definitively rejected.
            return await action(token).ConfigureAwait(false)
                   ?? throw new OnlineApiException(
                       emptyResponseMessage,
                       OnlineHttpResponse.InvalidResponseCode);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            await InvalidateUnauthorizedSessionAsync(token).ConfigureAwait(false);
            throw;
        }
        catch (OnlineApiException exception) when (IsRoomMembershipEnded(exception))
        {
            ClearRoomIfCurrent(expectedRoomId);
            throw;
        }
    }

    private async Task<T> ExecuteRoomValueRequestAsync<T>(
        Func<string, Task<T>> action,
        Guid expectedRoomId)
        where T : struct
    {
        var token = RequireAccessToken();
        try
        {
            return await action(token).ConfigureAwait(false);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            await InvalidateUnauthorizedSessionAsync(token).ConfigureAwait(false);
            throw;
        }
        catch (OnlineApiException exception) when (IsRoomMembershipEnded(exception))
        {
            ClearRoomIfCurrent(expectedRoomId);
            throw;
        }
    }

    private async Task ExecuteRoomCommandAsync(
        Func<string, Task> action,
        Guid expectedRoomId)
    {
        var token = RequireAccessToken();
        try
        {
            await action(token).ConfigureAwait(false);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            await InvalidateUnauthorizedSessionAsync(token).ConfigureAwait(false);
            throw;
        }
        catch (OnlineApiException exception) when (IsRoomMembershipEnded(exception))
        {
            ClearRoomIfCurrent(expectedRoomId);
            throw;
        }
    }

    /// <summary>
    /// Only explicit membership codes end the local room. The room endpoints also
    /// answer 404 for states that are merely not ready yet (resources_not_ready,
    /// chart_not_ready, resource_not_found, snapshot_not_found); treating those as
    /// "left the room" cleared the room of a member who had just joined. Any other
    /// failure, including a 404 without a code from a proxy, is left for a retry.
    /// </summary>
    private static bool IsRoomMembershipEnded(OnlineApiException exception) =>
        string.Equals(exception.Code, "member_required", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(exception.Code, "room_closed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(exception.Code, "room_not_found", StringComparison.OrdinalIgnoreCase);

    private async Task InvalidateUnauthorizedSessionAsync(string observedToken)
    {
        if (!string.Equals(sessionToken.CurrentToken, observedToken, StringComparison.Ordinal)) return;
        var invalidated = true;
        try
        {
            // The comparison is repeated atomically with the delete: a login that
            // stored a new token after this request started must survive its 401.
            invalidated = await ClearSessionTokenIfCurrentAsync(observedToken).ConfigureAwait(false);
        }
        finally
        {
            if (invalidated)
            {
                lock (stateGate)
                {
                    sessionGeneration++;
                    currentUser = null;
                    ClearRoomLocked();
                }
                ResetNotificationPollingState();
                RaiseChanged();
            }
        }
    }

    private async Task<OnlineUserNotificationPageDto?> ReadNotificationPageAsync(
        long? after,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = await communityApi.GetNotificationsAsync(
                    after,
                    OnlineCommunityApi.MaximumNotificationPageSize,
                    token,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new OnlineApiException("服务器没有返回关注提醒。");
            // System.Text.Json does not enforce non-nullable members. A record
            // without a type cannot be rendered, and UI code compares Type directly.
            return page with
            {
                Notifications = (page.Notifications ?? [])
                    .Where(notification => notification is { Id: > 0 } &&
                                           !string.IsNullOrWhiteSpace(notification.Type))
                    .Select(notification => notification with
                    {
                        ActorDisplayName = notification.ActorDisplayName ?? string.Empty,
                        ContextText = notification.ContextText ?? string.Empty
                    })
                    .ToArray()
            };
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (string.Equals(sessionToken.CurrentToken, token, StringComparison.Ordinal))
                notificationPollingUnauthorized = true;
            return null;
        }
    }

    private bool IsSameSession(long userId, string token) =>
        CurrentUser?.Id == userId &&
        string.Equals(sessionToken.CurrentToken, token, StringComparison.Ordinal);

    /// <summary>
    /// Installs a room produced by a request that began at
    /// <paramref name="requestSessionGeneration"/>. A create/join/invitation response
    /// that completes after logout, an account switch or a 401 must not put the
    /// signed-out session back into that room.
    /// </summary>
    private OnlineRoomSummaryDto InstallRoom(
        OnlineRoomSummaryDto room,
        string memberRole,
        bool canEditMetadata,
        bool canEditTiming,
        bool canEditNotes,
        long requestSessionGeneration)
    {
        ArgumentNullException.ThrowIfNull(room);
        room = NormalizeRoom(room);
        lock (stateGate)
        {
            if (requestSessionGeneration != sessionGeneration || currentUser is not { Id: > 0 })
                throw SessionEnded();
            roomSessionGeneration++;
            roomAccess = new RoomAccess(
                room,
                NormalizeMemberRole(memberRole),
                canEditMetadata,
                canEditTiming,
                canEditNotes);
        }
        RaiseChanged();
        return room;
    }

    private void ClearRoom()
    {
        lock (stateGate) ClearRoomLocked();
        RaiseChanged();
    }

    private void ClearRoomLocked()
    {
        roomSessionGeneration++;
        roomAccess = RoomAccess.None;
    }

    private void ClearRoomIfCurrent(Guid expectedRoomId, long? generation = null)
    {
        lock (stateGate)
        {
            if (generation.HasValue && roomSessionGeneration != generation.Value) return;
            if (roomAccess.Room?.Id != expectedRoomId) return;
            ClearRoomLocked();
        }
        RaiseChanged();
    }

    private RoomAccess ReadRoomAccess()
    {
        lock (stateGate) return roomAccess;
    }

    private long CaptureSessionGeneration()
    {
        lock (stateGate) return sessionGeneration;
    }

    private static OnlineApiException SessionEnded() =>
        new("请先登录账号。", "authentication_required");

    private static OnlineRoomSummaryDto NormalizeRoom(OnlineRoomSummaryDto room) => room with
    {
        Name = room.Name ?? string.Empty,
        OwnerDisplayName = room.OwnerDisplayName ?? string.Empty
    };

    /// <summary>
    /// System.Text.Json does not enforce non-nullable members, and the UI compares
    /// roles and names directly. Records without a usable identity are dropped and
    /// missing text becomes empty; a missing role fails closed to viewer.
    /// </summary>
    private static OnlineRoomLiveStateDto? NormalizeLiveState(OnlineRoomLiveStateDto? live)
    {
        if (live?.Room is not { } room || room.Id == Guid.Empty) return null;
        var members = (live.Members ?? [])
            .Where(member => member is { UserId: > 0 })
            .Select(member => member with
            {
                Username = member.Username ?? string.Empty,
                DisplayName = member.DisplayName ?? string.Empty,
                AvatarKey = member.AvatarKey ?? string.Empty,
                MemberRole = NormalizeMemberRole(member.MemberRole),
                CustomLabel = member.CustomLabel ?? string.Empty
            })
            .ToArray();
        var messages = (live.Messages ?? [])
            .Where(message => message is { Id: > 0 })
            .Select(message => message with
            {
                Username = message.Username ?? string.Empty,
                DisplayName = message.DisplayName ?? string.Empty,
                AvatarKey = message.AvatarKey ?? string.Empty,
                MemberRole = NormalizeMemberRole(message.MemberRole),
                Message = message.Message ?? string.Empty,
                CustomLabel = message.CustomLabel ?? string.Empty
            })
            .ToArray();
        return live with { Room = NormalizeRoom(room), Members = members, Messages = messages };
    }

    private static string NormalizeMemberRole(string? memberRole) =>
        memberRole?.Trim().ToLowerInvariant() switch
        {
            "owner" => "owner",
            "editor" => "editor",
            _ => "viewer"
        };

    private static string NormalizeInvitationAction(string? action) =>
        action?.Trim().ToLowerInvariant() switch
        {
            "join" => "join",
            "watch" => "watch",
            "cancel" => "cancel",
            _ => throw new ArgumentException(
                "Invitation action must be join, watch, or cancel.",
                nameof(action))
        };

    private void ResetNotificationPollingState()
    {
        notificationCursorUserId = 0;
        notificationCursor = null;
        notificationPollingUnauthorized = false;
        lock (notificationHistoryGate) recentNotifications.Clear();
    }

    private void PublishNotifications(
        long userId,
        IReadOnlyList<OnlineUserNotificationDto> notifications)
    {
        if (notifications.Count == 0 || CurrentUser?.Id != userId) return;
        lock (notificationHistoryGate)
        {
            recentNotifications.AddRange(notifications);
            if (recentNotifications.Count > MaximumRecentNotificationCount)
            {
                recentNotifications.RemoveRange(
                    0,
                    recentNotifications.Count - MaximumRecentNotificationCount);
            }
        }

        var handlers = NotificationsReceived;
        if (handlers == null) return;
        var eventArgs = new OnlineUserNotificationsReceivedEventArgs(userId, notifications);
        foreach (EventHandler<OnlineUserNotificationsReceivedEventArgs> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch
            {
                // A UI consumer must never roll back a committed notification
                // cursor or prevent other subscribed surfaces from observing it.
            }
        }
    }

    private static OnlineUserProfileDto RequireValidUser(
        OnlineUserProfileDto? user,
        string message)
    {
        if (user is not { Id: > 0 } || string.IsNullOrWhiteSpace(user.Username))
            throw new OnlineApiException(message);
        // Missing JSON members arrive as null despite the non-nullable contract.
        return user with
        {
            DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
            Role = user.Role ?? "user",
            AvatarKey = user.AvatarKey ?? string.Empty,
            Bio = user.Bio ?? string.Empty,
            CustomLabel = user.CustomLabel ?? string.Empty,
            Email = user.Email ?? string.Empty
        };
    }

    private void RequireAdminAccess()
    {
        if (!CanUseAdminApi)
            throw new OnlineApiException("当前账号无权执行管理员操作。", "admin_required");
    }

    private static void RequireAdminUserId(long userId)
    {
        if (userId <= 0)
            throw new ArgumentOutOfRangeException(nameof(userId));
    }

    private OnlineUserProfileDto InstallAdminUserIfCurrent(OnlineUserProfileDto user, long generation)
    {
        lock (stateGate)
        {
            if (generation != sessionGeneration || currentUser?.Id != user.Id) return user;
            currentUser = user;
        }
        RaiseChanged();
        return user;
    }

    private async Task StoreSessionTokenAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            await sessionToken.StoreAsync(token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OnlineApiException(
                "无法安全保存本地登录会话。",
                "session_store_failed",
                innerException: exception);
        }
    }

    private async Task<bool> ClearSessionTokenIfCurrentAsync(string observedToken)
    {
        try
        {
            return await sessionToken.ClearIfCurrentAsync(observedToken, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OnlineApiException(
                "无法安全清除本地登录会话。请检查用户数据目录权限后重试。",
                "session_store_delete_failed",
                innerException: exception);
        }
    }

    private async Task ClearSessionTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await sessionToken.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OnlineApiException(
                "无法安全清除本地登录会话。请检查用户数据目录权限后重试。",
                "session_store_delete_failed",
                innerException: exception);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The room and its permissions, replaced together so readers never mix two rooms.</summary>
    private sealed record RoomAccess(
        OnlineRoomSummaryDto? Room,
        string MemberRole,
        bool CanEditMetadata,
        bool CanEditTiming,
        bool CanEditNotes)
    {
        public static RoomAccess None { get; } = new(null, "viewer", false, false, false);
    }
}

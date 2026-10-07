using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Online;
using LumaChart.Storage;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsAccountSessionSpecs
{
    private static readonly Uri Endpoint = new("http://127.0.0.1:43123/");
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<WindowsAccountSpecResult>> RunAsync(string isolationRoot)
    {
        var results = new List<WindowsAccountSpecResult>();
        await CheckAsync(
            results,
            "account-session-login-persists-token-before-authentication",
            VerifyLoginStoreOrderingAsync);
        await CheckAsync(
            results,
            "account-session-rejects-expired-or-malformed-login-response",
            VerifyInvalidLoginRejectedAsync);
        await CheckAsync(
            results,
            "account-session-register-installs-returned-session",
            VerifyRegisterAsync);
        await CheckAsync(
            results,
            "account-session-osu-login-uses-one-time-browser-handoff-and-secure-session-install",
            VerifyOsuLoginAsync);
        await CheckAsync(
            results,
            "account-session-captcha-email-code-and-reset-use-existing-wire-contracts",
            VerifyRecoveryWorkflowAsync);
        await CheckAsync(
            results,
            "account-session-restores-valid-protected-token",
            VerifyRestoreAsync);
        await CheckAsync(
            results,
            "account-session-restore-401-clears-protected-token",
            VerifyRestoreUnauthorizedAsync);
        await CheckAsync(
            results,
            "account-session-restore-network-failure-preserves-protected-token",
            VerifyRestoreNetworkFailureAsync);
        await CheckAsync(
            results,
            "account-session-secure-write-failure-remains-signed-out",
            VerifySecureWriteFailureAsync);
        await CheckAsync(
            results,
            "account-session-logout-clears-local-token-when-server-is-unreachable",
            VerifyLocalAuthoritativeLogoutAsync);
        await CheckAsync(
            results,
            "windows-dpapi-session-token-round-trips-and-deletes",
            () => VerifyDpapiRoundTripAsync(isolationRoot));
        await CheckAsync(
            results,
            "windows-dpapi-corrupt-ciphertext-fails-closed",
            () => VerifyDpapiCorruptionAsync(isolationRoot));
        await CheckAsync(
            results,
            "windows-online-endpoint-rejects-remote-http-and-allows-loopback-http",
            () => VerifyEndpointPolicyAsync(isolationRoot));
        await CheckAsync(
            results,
            "account-session-profile-security-avatar-and-heartbeat-use-authoritative-state",
            VerifyProfileWorkspaceAsync);
        await CheckAsync(
            results,
            "managed-client-security-owner-admin-routes-fail-closed-without-native-policy",
            VerifyManagedAdminWorkspaceAsync);
        await CheckAsync(
            results,
            "account-session-community-modes-player-profile-and-follow-use-existing-contracts",
            VerifyCommunityWorkspaceAsync);
        await CheckAsync(
            results,
            "account-session-community-401-invalidates-local-protected-session",
            VerifyCommunityUnauthorizedAsync);
        await CheckAsync(
            results,
            "account-session-notification-high-water-and-page-chain-match-wpf",
            VerifyNotificationCursorAsync);
        await CheckAsync(
            results,
            "account-session-room-directory-create-and-leave-preserve-lobby-contracts",
            VerifyRoomLobbyAsync);
        await CheckAsync(
            results,
            "account-session-room-live-chat-members-and-permissions-use-authoritative-state",
            VerifyRoomLiveWorkspaceAsync);
        await CheckAsync(
            results,
            "account-session-hidden-private-invitation-accepts-without-password-rejoin",
            VerifyHiddenRoomInvitationAsync);
        await CheckAsync(
            results,
            "account-session-hidden-private-invitation-watch-installs-viewer-without-password-rejoin",
            VerifyHiddenRoomWatchInvitationAsync);
        await CheckAsync(
            results,
            "account-session-room-forbidden-outcome-clears-live-membership",
            VerifyLostRoomAsync);
        await CheckAsync(
            results,
            "account-session-transient-room-transport-outage-retains-membership-until-authoritative-loss",
            VerifyTransientRoomTransportOutageAsync);
        await CheckAsync(
            results,
            "account-session-stale-live-success-after-leave-cannot-reinstall-room",
            VerifyStaleRoomLiveAfterLeaveAsync);
        await CheckAsync(
            results,
            "account-session-old-room-membership-failure-cannot-clear-new-room",
            VerifyOldRoomFailureCannotClearNewRoomAsync);
        await CheckAsync(
            results,
            "account-session-room-chart-resource-delta-sse-and-rollback-use-shared-wire-contracts",
            VerifyRoomChartWireContractsAsync);
        await CheckAsync(
            results,
            "windows-room-resources-cache-verify-materialize-open-and-upload",
            () => VerifyRoomResourceWorkspaceAsync(isolationRoot));
        await CheckAsync(
            results,
            "account-session-room-chart-membership-loss-retains-account-and-clears-room",
            VerifyRoomChartMembershipLossAsync);
        await CheckAsync(
            results,
            "windows-avalonia-new-chart-uses-authenticated-username-as-creator",
            () => VerifyAuthenticatedCreatorAsync(isolationRoot));
        return results;
    }

    private static async Task<string> VerifyLoginStoreOrderingAsync()
    {
        OnlineAccountSession? session = null;
        var store = new RecordingTokenStore();
        var observedSignedOutDuringWrite = false;
        store.OnSave = _ =>
        {
            observedSignedOutDuringWrite = session is
            {
                IsAuthenticated: false,
                CurrentAccessToken: null,
                CurrentUser: null
            };
        };
        using var client = Client(async request =>
        {
            RequireRequest(request, HttpMethod.Post, "api/auth/login");
            var body = await ReadBodyAsync(request);
            Require(body.GetProperty("username").GetString() == "alice", "login username");
            Require(body.GetProperty("captchaAnswer").GetString() == "12345", "login captcha");
            return JsonResponse(LoginResponse("secure-token", Now.AddHours(1), 41, "alice"));
        });
        session = Session(client, store);
        var user = await session.LoginAsync(
            "alice",
            "correct-horse-battery",
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            "12345");
        Require(observedSignedOutDuringWrite, "token became visible before secure save completed");
        Require(store.Value == "secure-token", "token was not persisted");
        Require(session.IsAuthenticated && user.Id == 41, "session was not installed");
        return "POST /api/auth/login; secure store completed before in-memory authentication";
    }

    private static async Task<string> VerifyInvalidLoginRejectedAsync()
    {
        var store = new RecordingTokenStore();
        // ExpiresAt is server time and is no longer compared with the local clock
        // (a fast local clock made every sign-in fail); a response without an
        // expiry is still malformed and must not reach secure storage.
        using var client = Client(request =>
        {
            RequireRequest(request, HttpMethod.Post, "api/auth/login");
            return Task.FromResult(JsonResponse(LoginResponse("no-expiry", default, 42, "bob")));
        });
        var session = Session(client, store);
        var rejected = false;
        try
        {
            _ = await session.LoginAsync("bob", "password-password", Guid.NewGuid(), "12345");
        }
        catch (OnlineApiException)
        {
            rejected = true;
        }
        Require(rejected, "malformed response was accepted");
        Require(store.SaveCount == 0 && !session.IsAuthenticated, "invalid response changed auth state");
        return "login response without a server expiry rejected before secure storage";
    }

    private static async Task<string> VerifyRegisterAsync()
    {
        var store = new RecordingTokenStore();
        using var client = Client(async request =>
        {
            RequireRequest(request, HttpMethod.Post, "api/auth/register");
            var body = await ReadBodyAsync(request);
            Require(body.GetProperty("emailCode").GetString() == "654321", "registration email code");
            return JsonResponse(LoginResponse("registered-token", Now.AddDays(1), 43, "carol"));
        });
        var session = Session(client, store);
        var user = await session.RegisterAsync(
            "carol",
            "password-password",
            "Carol",
            "carol@example.invalid",
            "654321");
        Require(session.IsAuthenticated && user.Username == "carol", "registered session missing");
        Require(store.Value == "registered-token", "registered token missing");
        return "registration returned an authenticated DPAPI-ready session";
    }

    private static async Task<string> VerifyOsuLoginAsync()
    {
        var step = 0;
        var pollToken = "osu-one-time-poll-token-abcdefghijklmnopqrstuvwxyz";
        var store = new RecordingTokenStore();
        using var client = Client(async request =>
        {
            step++;
            if (step == 1)
            {
                RequireRequest(request, HttpMethod.Post, "api/auth/osu/start");
                return JsonResponse(new OnlineOsuOAuthStartDto(
                    "https://osu.ppy.sh/oauth/authorize?client_id=1&state=test",
                    pollToken,
                    Now.AddMinutes(10)));
            }
            RequireRequest(request, HttpMethod.Post, "api/auth/osu/poll");
            var body = await ReadBodyAsync(request);
            Require(body.GetProperty("pollToken").GetString() == pollToken, "osu poll token");
            if (step == 2)
                return JsonResponse(new OnlineOsuOAuthPollDto("pending"));
            var user = User(44, "osu_mapper") with
            {
                Email = string.Empty,
                HasPasswordLogin = false,
                OsuAccount = new OnlineOsuAccountDto(
                    123456,
                    "osu mapper",
                    "https://osu.ppy.sh/users/123456")
            };
            return JsonResponse(new OnlineOsuOAuthPollDto(
                "completed",
                Login: new OnlineLoginResponseDto(
                    "osu-login-token",
                    Now.AddDays(1),
                    user)));
        });
        var session = Session(client, store);
        var start = await session.StartOsuLoginAsync();
        var pending = await session.PollOsuAuthorizationAsync(start.PollToken);
        Require(pending.Status == "pending" && !session.IsAuthenticated,
            "pending osu flow changed authentication state");
        var completed = await session.PollOsuAuthorizationAsync(start.PollToken);
        Require(completed.Status == "completed" &&
                session.IsAuthenticated &&
                session.CurrentUser is
                {
                    HasPasswordLogin: false,
                    OsuAccount.UserId: 123456
                } &&
                store.Value == "osu-login-token",
            "completed osu flow did not install the protected LumaChart session");
        return "start/pending/completed; no email/password required; token stored before authentication";
    }

    private static async Task<string> VerifyRecoveryWorkflowAsync()
    {
        var step = 0;
        var captchaId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        using var client = Client(async request =>
        {
            step++;
            if (step == 1)
            {
                RequireRequest(request, HttpMethod.Get, "api/auth/captcha");
                return JsonResponse(new OnlineCaptchaChallengeDto(captchaId, "AA==", Now.AddMinutes(2)));
            }
            if (step == 2)
            {
                RequireRequest(request, HttpMethod.Post, "api/auth/email-codes");
                var body = await ReadBodyAsync(request);
                Require(body.GetProperty("purpose").GetString() == "reset", "email purpose");
                Require(body.GetProperty("captchaId").GetGuid() == captchaId, "email captcha id");
                return EmptyResponse();
            }
            RequireRequest(request, HttpMethod.Post, "api/auth/reset-password");
            var reset = await ReadBodyAsync(request);
            Require(reset.GetProperty("emailCode").GetString() == "123456", "reset email code");
            return EmptyResponse();
        });
        var session = Session(client, new RecordingTokenStore());
        var captcha = await session.GetCaptchaAsync();
        await session.SendEmailVerificationCodeAsync(
            "reset@example.invalid",
            "reset",
            captcha.Id,
            "54321");
        await session.ResetPasswordAsync(
            "reset@example.invalid",
            "123456",
            "new-password-value");
        Require(step == 3, "recovery workflow did not complete all requests");
        return "captcha, reset email code and reset-password endpoints preserved";
    }

    private static async Task<string> VerifyRestoreAsync()
    {
        var store = new RecordingTokenStore { Value = "restored-token" };
        using var client = Client(request =>
        {
            RequireRequest(request, HttpMethod.Get, "api/me");
            RequireBearer(request, "restored-token");
            return Task.FromResult(JsonResponse(User(44, "dora")));
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "restore returned false");
        Require(session.IsAuthenticated && session.CurrentUser?.Username == "dora", "restore state missing");
        return "protected token restored through GET /api/me";
    }

    private static async Task<string> VerifyRestoreUnauthorizedAsync()
    {
        var store = new RecordingTokenStore { Value = "revoked-token" };
        using var client = Client(request =>
        {
            RequireRequest(request, HttpMethod.Get, "api/me");
            return Task.FromResult(ErrorResponse(
                HttpStatusCode.Unauthorized,
                "unauthorized",
                "登录会话已失效。"));
        });
        var session = Session(client, store);
        Require(!await session.TryRestoreAsync(), "401 restore returned true");
        Require(store.Value == null && store.DeleteCount == 1, "401 did not clear protected token");
        Require(!session.IsAuthenticated, "401 retained authenticated state");
        return "definitive 401 removed the local protected token";
    }

    private static async Task<string> VerifyRestoreNetworkFailureAsync()
    {
        var store = new RecordingTokenStore { Value = "offline-token" };
        using var client = Client(_ => throw new HttpRequestException("offline"));
        var session = Session(client, store);
        var failed = false;
        try
        {
            _ = await session.TryRestoreAsync();
        }
        catch (HttpRequestException)
        {
            failed = true;
        }
        Require(failed, "network error was swallowed");
        Require(store.Value == "offline-token" && store.DeleteCount == 0, "transient failure deleted token");
        Require(!session.IsAuthenticated, "offline restore claimed authentication");
        return "transport failure preserved token for a later retry";
    }

    private static async Task<string> VerifySecureWriteFailureAsync()
    {
        var store = new RecordingTokenStore
        {
            SaveException = new IOException("secure store denied")
        };
        using var client = Client(_ => Task.FromResult(
            JsonResponse(LoginResponse("must-not-install", Now.AddHours(1), 45, "erin"))));
        var session = Session(client, store);
        OnlineApiException? failure = null;
        try
        {
            _ = await session.LoginAsync("erin", "password-password", Guid.NewGuid(), "12345");
        }
        catch (OnlineApiException exception)
        {
            failure = exception;
        }
        Require(failure?.Code == "session_store_failed", "secure write failure code missing");
        Require(!session.IsAuthenticated && session.CurrentUser == null, "failed write installed user");
        Require(store.DeleteCount == 1, "failed secure write did not attempt rollback delete");
        return "failed secure write rolled back and remained signed out";
    }

    private static async Task<string> VerifyLocalAuthoritativeLogoutAsync()
    {
        var store = new RecordingTokenStore { Value = "logout-token" };
        var step = 0;
        using var client = Client(request =>
        {
            step++;
            if (step == 1) return Task.FromResult(JsonResponse(User(46, "fred")));
            RequireRequest(request, HttpMethod.Post, "api/auth/logout");
            throw new HttpRequestException("server unavailable");
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "logout setup restore failed");
        await session.LogoutAsync();
        Require(store.Value == null && store.DeleteCount == 1, "logout did not clear token");
        Require(!session.IsAuthenticated && session.CurrentUser == null, "logout retained local identity");
        return "server transport failure could not trap the local login";
    }

    private static async Task<string> VerifyDpapiRoundTripAsync(string isolationRoot)
    {
        var settings = Path.Combine(isolationRoot, "dpapi-roundtrip", "settings");
        var locations = Locations(settings, isolationRoot, "dpapi-roundtrip");
        var store = new WindowsDpapiSessionTokenStore(locations);
        const string token = "dpapi-token-中文-123";
        await store.SaveAsync(token);
        Require(File.Exists(store.StorePath), "protected file missing");
        var protectedBytes = await File.ReadAllBytesAsync(store.StorePath);
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(token);
        Require(
            protectedBytes.Length > plaintextBytes.Length,
            "DPAPI ciphertext is not a protected Windows blob");
        Require(
            protectedBytes.AsSpan().IndexOf(plaintextBytes) < 0,
            "DPAPI store contains the plaintext token");
        Require(await store.LoadAsync() == token, "DPAPI round trip mismatch");
        await store.DeleteAsync();
        Require(!File.Exists(store.StorePath) && await store.LoadAsync() == null, "DPAPI delete failed");
        return "CurrentUser DPAPI ciphertext excludes plaintext; save/load/delete passed in isolated settings root";
    }

    private static async Task<string> VerifyDpapiCorruptionAsync(string isolationRoot)
    {
        var settings = Path.Combine(isolationRoot, "dpapi-corrupt", "settings");
        var locations = Locations(settings, isolationRoot, "dpapi-corrupt");
        var store = new WindowsDpapiSessionTokenStore(locations);
        Directory.CreateDirectory(settings);
        await File.WriteAllBytesAsync(store.StorePath, [1, 3, 3, 7, 9, 11]);
        Require(await store.LoadAsync() == null, "corrupt ciphertext returned a token");
        Require(!File.Exists(store.StorePath), "corrupt ciphertext was not removed");
        await File.WriteAllBytesAsync(store.StorePath, new byte[64 * 1024 + 1]);
        Require(await store.LoadAsync() == null, "oversized ciphertext returned a token");
        Require(!File.Exists(store.StorePath), "oversized ciphertext was not removed");
        return "invalid and oversized ciphertext returned signed-out state and was removed";
    }

    private static Task<string> VerifyEndpointPolicyAsync(string isolationRoot)
    {
        var root = Path.Combine(isolationRoot, "endpoint-policy");
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("LUMACHART_SERVER_URL");
        try
        {
            Environment.SetEnvironmentVariable("LUMACHART_SERVER_URL", null);
            var production = WindowsOnlineAccountHost.ResolveEndpoint(root);
            Require(
                production.Scheme == Uri.UriSchemeHttps &&
                production.Host.Equals("api.lumachart.cc", StringComparison.OrdinalIgnoreCase),
                "default production endpoint is not the approved HTTPS origin");
            File.WriteAllText(Path.Combine(root, "server-endpoint.txt"), "http://example.com/");
            var rejected = false;
            try
            {
                _ = WindowsOnlineAccountHost.ResolveEndpoint(root);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Require(rejected, "remote plaintext HTTP was accepted");
            File.WriteAllText(Path.Combine(root, "server-endpoint.txt"), "http://127.0.0.1:5088/");
            var loopback = WindowsOnlineAccountHost.ResolveEndpoint(root);
            Require(loopback.IsLoopback && loopback.Scheme == Uri.UriSchemeHttp, "loopback HTTP rejected");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LUMACHART_SERVER_URL", previous);
        }
        var referencedAssemblies = typeof(WindowsOnlineAccountHost).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Require(
            referencedAssemblies.All(name =>
                !name.Contains("ClientSecurity", StringComparison.OrdinalIgnoreCase)),
            "Avalonia assembly references a native ClientSecurity bridge");
        Require(
            !Directory.EnumerateFiles(
                    AppContext.BaseDirectory,
                    "LumaChart.ClientSecurity*",
                    SearchOption.TopDirectoryOnly)
                .Any(),
            "Avalonia Specs output contains a native ClientSecurity artifact");
        return Task.FromResult(
            "approved production HTTPS origin; remote HTTP rejected; loopback HTTP accepted; " +
            "no native ClientSecurity reference or output");
    }

    private static async Task<string> VerifyProfileWorkspaceAsync()
    {
        var store = new RecordingTokenStore { Value = "profile-token" };
        var step = 0;
        using var client = Client(async request =>
        {
            step++;
            RequireBearer(request, "profile-token");
            switch (step)
            {
                case 1:
                    RequireRequest(request, HttpMethod.Get, "api/me");
                    return JsonResponse(User(51, "profile"));
                case 2:
                {
                    RequireRequest(request, HttpMethod.Put, "api/me/profile");
                    var body = await ReadBodyAsync(request);
                    Require(body.GetProperty("displayName").GetString() == "新显示名", "profile display name");
                    Require(body.GetProperty("hidePublicAvatar").GetBoolean(), "avatar privacy");
                    return JsonResponse(User(51, "profile") with
                    {
                        DisplayName = "新显示名",
                        Bio = "新的简介",
                        HidePublicAvatar = true
                    });
                }
                case 3:
                    RequireRequest(request, HttpMethod.Put, "api/me/name-color");
                    return JsonResponse(User(51, "profile") with
                    {
                        DisplayName = "新显示名",
                        Tags = [new OnlineUserTagDto("mapper", "#79B8BD", true)]
                    });
                case 4:
                {
                    RequireRequest(request, HttpMethod.Put, "api/me/email");
                    var body = await ReadBodyAsync(request);
                    Require(body.GetProperty("emailCode").GetString() == "654321", "change email code");
                    return JsonResponse(User(51, "profile") with { Email = "new@example.invalid" });
                }
                case 5:
                    RequireRequest(request, HttpMethod.Post, "api/me/password");
                    return EmptyResponse();
                case 6:
                    RequireRequest(request, HttpMethod.Put, "api/me/avatar");
                    var bytes = await request.Content!.ReadAsByteArrayAsync();
                    Require(bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "avatar payload");
                    return JsonResponse(User(51, "profile") with { AvatarKey = "avatar-51" });
                default:
                    RequireRequest(request, HttpMethod.Post, "api/profile/charting/heartbeat");
                    return JsonResponse(new OnlineChartingHeartbeatResponseDto(
                        9999,
                        true,
                        Now));
            }
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "profile setup restore failed");
        await session.UpdateProfileAsync("新显示名", "新的简介", true, false);
        Require(session.CurrentUser?.DisplayName == "新显示名", "profile state not installed");
        await session.UpdateNameColorAsync("mapper");
        Require(session.CurrentUser?.Tags?.Single().IsNameColor == true, "name color state not installed");
        await session.ChangeEmailAsync("old-password", "new@example.invalid", "654321");
        Require(session.CurrentUser?.Email == "new@example.invalid", "email state not installed");
        await session.ChangePasswordAsync("old-password", "new-password-value");
        using var avatar = new MemoryStream([1, 2, 3, 4], writable: false);
        await session.UploadAvatarAsync(avatar, avatar.Length);
        Require(session.CurrentUser?.AvatarKey == "avatar-51", "avatar state not installed");
        var heartbeat = await session.SendChartingHeartbeatAsync(Guid.NewGuid(), 17, true);
        Require(heartbeat?.IsCounting == true && session.CurrentUser?.ChartingSeconds == 9999,
            "heartbeat state not installed");
        Require(step == 7, "profile workspace missed a request");
        return "profile, privacy, name color, email, password, avatar and heartbeat preserved";
    }

    private static async Task<string> VerifyManagedAdminWorkspaceAsync()
    {
        var step = 0;
        var owner = User(90, "owner") with { Role = "owner" };
        var target = User(77, "target");
        using var client = Client(async request =>
        {
            step++;
            switch (step)
            {
                case 1:
                    RequireRequest(request, HttpMethod.Get, "api/me");
                    RequireBearer(request, "owner-token");
                    return JsonResponse(owner);
                case 2:
                    RequireRequest(request, HttpMethod.Get, "api/app-config");
                    Require(
                        request.Headers.Authorization == null,
                        "public app configuration unexpectedly sent a bearer token");
                    return JsonResponse(new OnlineAppConfigurationDto(
                        "about",
                        "1.1.0",
                        "1.0.0",
                        "notice",
                        "https://example.invalid/download",
                        Now));
                case 3:
                    RequireRequest(request, HttpMethod.Get, "api/admin/app-config");
                    RequireBearer(request, "owner-token");
                    return JsonResponse(new OnlineAppConfigurationDto(
                        "about",
                        "1.1.0",
                        "1.0.0",
                        "notice",
                        "https://example.invalid/download",
                        Now,
                        "1.2.0.1-beta",
                        "1.2.0.1-beta",
                        "beta notice",
                        "https://example.invalid/beta"));
                case 4:
                {
                    RequireRequest(request, HttpMethod.Put, "api/admin/app-config");
                    RequireBearer(request, "owner-token");
                    var body = await ReadBodyAsync(request);
                    Require(
                        body.GetProperty("latestVersion").GetString() == "1.2.0" &&
                        body.GetProperty("betaLatestVersion").GetString() == "1.2.0.2-beta",
                        "admin app configuration payload mismatch");
                    return JsonResponse(new OnlineAppConfigurationDto(
                        "updated about",
                        "1.2.0",
                        "1.1.0",
                        "updated notice",
                        "https://example.invalid/v1.2.0",
                        Now.AddMinutes(1),
                        "1.2.0.2-beta",
                        "1.2.0.1-beta",
                        "updated beta notice",
                        "https://example.invalid/beta-v1.2.0.2"));
                }
                case 5:
                    RequireRequest(request, HttpMethod.Get, "api/admin/users/77");
                    RequireBearer(request, "owner-token");
                    return JsonResponse(target);
                case 6:
                {
                    RequireRequest(request, HttpMethod.Put, "api/admin/users/77");
                    RequireBearer(request, "owner-token");
                    var body = await ReadBodyAsync(request);
                    Require(
                        body.GetProperty("displayName").GetString() == "TARGET EDITED",
                        "admin user update payload mismatch");
                    return JsonResponse(target with { DisplayName = "TARGET EDITED" });
                }
                case 7:
                {
                    RequireRequest(request, HttpMethod.Put, "api/admin/users/77/label");
                    RequireBearer(request, "owner-token");
                    var body = await ReadBodyAsync(request);
                    Require(
                        body.GetProperty("label").GetString() == "trusted",
                        "admin custom-label payload mismatch");
                    return JsonResponse(new OnlinePlayerProfileDto(
                        77,
                        "TARGET EDITED",
                        "default",
                        "",
                        0,
                        0,
                        0,
                        false,
                        false,
                        true,
                        false,
                        false,
                        "trusted"));
                }
                case 8:
                {
                    RequireRequest(request, HttpMethod.Put, "api/admin/users/77/avatar");
                    RequireBearer(request, "owner-token");
                    var bytes = await request.Content!.ReadAsByteArrayAsync();
                    Require(bytes.SequenceEqual(new byte[] { 1, 2, 3, 4 }), "admin avatar payload mismatch");
                    return JsonResponse(target with { AvatarKey = "avatar-admin" });
                }
                case 9:
                    RequireRequest(request, HttpMethod.Delete, "api/admin/users/77/avatar");
                    RequireBearer(request, "owner-token");
                    return JsonResponse(target with { AvatarKey = "default" });
                default:
                    throw new InvalidOperationException($"Unexpected owner admin request step {step}.");
            }
        });
        var session = Session(
            client,
            new RecordingTokenStore { Value = "owner-token" });
        Require(await session.TryRestoreAsync(), "owner session restore failed");
        Require(session.CanUseAdminApi, "owner admin gate was not enabled");
        var publicConfiguration = await session.GetAppConfigurationAsync();
        Require(publicConfiguration.LatestVersion == "1.1.0", "public app configuration mismatch");
        var adminConfiguration = await session.GetAppConfigurationAsAdminAsync();
        Require(
            adminConfiguration.BetaLatestVersion == "1.2.0.1-beta" &&
            adminConfiguration.BetaDownloadUrl == "https://example.invalid/beta",
            "admin beta app configuration mismatch");
        var updatedConfiguration = await session.UpdateAppConfigurationAsAdminAsync(
            new OnlineUpdateAppConfigurationRequestDto(
                "updated about",
                "1.2.0",
                "1.1.0",
                "updated notice",
                "https://example.invalid/v1.2.0",
                "1.2.0.2-beta",
                "1.2.0.1-beta",
                "updated beta notice",
                "https://example.invalid/beta-v1.2.0.2"));
        Require(
            updatedConfiguration.LatestVersion == "1.2.0" &&
            updatedConfiguration.BetaLatestVersion == "1.2.0.2-beta",
            "admin app configuration mismatch");
        Require((await session.GetUserAsAdminAsync(77)).Id == 77, "admin user lookup mismatch");
        Require(
            (await session.UpdateUserAsAdminAsync(
                77,
                new OnlineAdminUpdateUserRequestDto(
                    "target",
                    "TARGET EDITED",
                    "",
                    []))).DisplayName == "TARGET EDITED",
            "admin user update mismatch");
        Require(
            (await session.SetUserCustomLabelAsAdminAsync(77, "trusted")).CustomLabel == "trusted",
            "admin custom label mismatch");
        await using (var avatar = new MemoryStream([1, 2, 3, 4], writable: false))
            Require(
                (await session.UploadUserAvatarAsAdminAsync(77, avatar, avatar.Length)).AvatarKey ==
                "avatar-admin",
                "admin avatar upload mismatch");
        Require(
            (await session.ResetUserAvatarAsAdminAsync(77)).AvatarKey == "default",
            "admin avatar reset mismatch");
        Require(step == 9, $"owner admin request count mismatch: {step}");

        var nonOwnerRequests = 0;
        using var nonOwnerClient = Client(request =>
        {
            nonOwnerRequests++;
            RequireRequest(request, HttpMethod.Get, "api/me");
            return Task.FromResult(JsonResponse(User(91, "member")));
        });
        var nonOwnerSession = Session(
            nonOwnerClient,
            new RecordingTokenStore { Value = "member-token" });
        Require(await nonOwnerSession.TryRestoreAsync(), "non-owner session restore failed");
        Require(!nonOwnerSession.CanUseAdminApi, "non-owner admin gate was enabled");
        var rejected = false;
        try
        {
            _ = await nonOwnerSession.GetUserAsAdminAsync(77);
        }
        catch (OnlineApiException exception)
        {
            rejected = exception.Code == "admin_required";
        }
        Require(rejected, "non-owner admin call was not rejected locally");
        Require(nonOwnerRequests == 1, "non-owner admin call reached the HTTP transport");
        return "owner config/user/label/avatar routes passed; non-owner rejected before HTTP; native policy absent";
    }

    private static async Task<string> VerifyCommunityWorkspaceAsync()
    {
        var store = new RecordingTokenStore { Value = "community-token" };
        var step = 0;
        using var client = Client(request =>
        {
            step++;
            RequireBearer(request, "community-token");
            if (step == 1)
            {
                RequireRequest(request, HttpMethod.Get, "api/me");
                return Task.FromResult(JsonResponse(User(61, "community")));
            }

            var player = Player(72, "Player 72");
            return step switch
            {
                2 => DirectoryResponse(request, "api/players", player, expectedQuery: "page=2"),
                3 => DirectoryResponse(request, "api/players/search", player, expectedQuery: "q=Player%2072"),
                4 => DirectoryResponse(request, "api/players/leaderboard", player, expectedQuery: "limit=50"),
                5 => DirectoryResponse(request, "api/me/friends", player),
                6 => DirectoryResponse(request, "api/me/following", player, expectedQuery: "page=3"),
                7 => DirectoryResponse(request, "api/me/followers", player, expectedQuery: "page=4"),
                8 => Task.FromResult(PlayerResponse(request, player)),
                9 => Task.FromResult(FollowResponse(request, User(61, "community") with { FollowingCount = 8 })),
                _ => throw new InvalidOperationException($"unexpected community request {request.RequestUri}")
            };
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "community setup restore failed");
        Require((await session.GetPlayersAsync(2)).Players.Single().Id == 72, "players result");
        Require((await session.SearchPlayersAsync("Player 72")).Players.Count == 1, "search result");
        Require((await session.GetLeaderboardAsync()).Players.Count == 1, "leaderboard result");
        Require((await session.GetFriendsAsync()).Players.Single().IsMutual, "friends result");
        Require((await session.GetFollowingAsync(3)).Page == 2, "following result");
        Require((await session.GetFollowersAsync(4)).TotalCount == 205, "followers result");
        Require((await session.GetPlayerAsync(72)).DisplayName == "Player 72", "player profile result");
        await session.SetFollowAsync(72, true);
        Require(session.CurrentUser?.FollowingCount == 8, "authoritative follow count not installed");
        Require(step == 9, "community workspace missed a request");
        return "all/search/rank/friends/following/followers/player/follow contracts preserved";
    }

    private static async Task<string> VerifyCommunityUnauthorizedAsync()
    {
        var store = new RecordingTokenStore { Value = "revoked-community-token" };
        var step = 0;
        using var client = Client(request =>
        {
            step++;
            if (step == 1) return Task.FromResult(JsonResponse(User(62, "revoked")));
            return Task.FromResult(ErrorResponse(
                HttpStatusCode.Unauthorized,
                "unauthorized",
                "登录会话已失效。"));
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "unauthorized setup restore failed");
        var rejected = false;
        try
        {
            _ = await session.GetPlayersAsync(1);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            rejected = true;
        }
        Require(rejected, "community 401 was not surfaced");
        Require(!session.IsAuthenticated && session.CurrentUser == null, "community 401 retained identity");
        Require(store.Value == null && store.DeleteCount == 1, "community 401 retained protected token");
        return "definitive community 401 failed closed and removed the protected token";
    }

    private static async Task<string> VerifyNotificationCursorAsync()
    {
        var store = new RecordingTokenStore { Value = "notification-token" };
        var step = 0;
        using var client = Client(request =>
        {
            step++;
            RequireBearer(request, "notification-token");
            if (step == 1) return Task.FromResult(JsonResponse(User(63, "notify")));
            if (step == 6)
            {
                RequireRequest(request, HttpMethod.Post, "api/auth/logout");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            RequireRequest(request, HttpMethod.Get, "api/me/notifications");
            var query = request.RequestUri?.Query ?? string.Empty;
            return step switch
            {
                2 when !query.Contains("after=", StringComparison.Ordinal) =>
                    Task.FromResult(JsonResponse(new OnlineUserNotificationPageDto(5, false, []))),
                3 when query.Contains("after=5", StringComparison.Ordinal) =>
                    Task.FromResult(JsonResponse(new OnlineUserNotificationPageDto(
                        6,
                        true,
                        [new OnlineUserNotificationDto(6, "follow", 71, "Alice", Now)]))),
                4 when query.Contains("after=6", StringComparison.Ordinal) =>
                    Task.FromResult(JsonResponse(new OnlineUserNotificationPageDto(
                        7,
                        false,
                        [new OnlineUserNotificationDto(7, "follow", 72, "Bob", Now.AddSeconds(1))]))),
                5 when query.Contains("after=7", StringComparison.Ordinal) =>
                    Task.FromResult(JsonResponse(new OnlineUserNotificationPageDto(
                        107,
                        false,
                        Enumerable.Range(8, 100)
                            .Select(id => new OnlineUserNotificationDto(
                                id,
                                "follow",
                                id + 1000L,
                                $"Actor {id}",
                                Now.AddSeconds(id)))
                            .ToArray()))),
                _ => throw new InvalidOperationException($"unexpected notification query {query}")
            };
        });
        var session = Session(client, store);
        var published = new List<OnlineUserNotificationsReceivedEventArgs>();
        session.NotificationsReceived += (_, _) =>
            throw new InvalidOperationException("isolated subscriber failure");
        session.NotificationsReceived += (_, eventArgs) => published.Add(eventArgs);
        Require(await session.TryRestoreAsync(), "notification setup restore failed");
        Require((await session.PollNotificationsAsync()).Count == 0, "bootstrap replayed history");
        Require(published.Count == 0 && session.RecentNotifications.Count == 0,
            "notification bootstrap published history");
        var notifications = await session.PollNotificationsAsync();
        Require(notifications.Select(item => item.Id).SequenceEqual(new long[] { 6, 7 }),
            "notification page chain order");
        Require(published.Count == 1 &&
                published[0].UserId == 63 &&
                published[0].Notifications.Select(item => item.Id)
                    .SequenceEqual(new long[] { 6, 7 }),
            "committed notification broadcast was not isolated and ordered");
        var overflow = await session.PollNotificationsAsync();
        Require(overflow.Count == 100 &&
                overflow[0].Id == 8 &&
                overflow[^1].Id == 107,
            "notification history overflow page was not returned");
        Require(published.Count == 2 &&
                session.RecentNotifications.Count ==
                OnlineAccountSession.MaximumRecentNotificationCount &&
                session.RecentNotifications[0].Id == 8 &&
                session.RecentNotifications[^1].Id == 107,
            "notification broadcast history was not bounded to the newest 100 items");
        await session.LogoutAsync();
        Require(session.RecentNotifications.Count == 0,
            "notification history survived logout");
        Require(step == 6, "notification broadcast/history spec missed a request");
        return "high-water, ordered broadcast, subscriber isolation, bounded history and logout reset";
    }

    private static async Task<string> VerifyRoomLobbyAsync()
    {
        var roomId = Guid.Parse("81111111-2222-3333-4444-555555555555");
        var store = new RecordingTokenStore { Value = "room-lobby-token" };
        var step = 0;
        using var client = Client(async request =>
        {
            step++;
            if (step == 1)
            {
                RequireRequest(request, HttpMethod.Get, "api/me");
                return JsonResponse(User(81, "room-owner"));
            }
            if (step == 2)
            {
                RequireRequest(request, HttpMethod.Get, "api/rooms");
                Require(request.Headers.Authorization == null, "public room directory unexpectedly authorized");
                Require((request.RequestUri?.Query ?? string.Empty).Contains(
                        "q=" + Uri.EscapeDataString("合作"),
                        StringComparison.OrdinalIgnoreCase),
                    "room query missing");
                return JsonResponse(new OnlineRoomDirectoryDto([Room(roomId)], 1, 10, 1));
            }
            RequireBearer(request, "room-lobby-token");
            if (step == 3)
            {
                RequireRequest(request, HttpMethod.Post, "api/rooms");
                var body = await ReadBodyAsync(request);
                Require(body.GetProperty("isPrivate").GetBoolean(), "private room flag");
                Require(body.GetProperty("isHidden").GetBoolean(), "hidden room flag");
                Require(body.GetProperty("password").GetString() == "room-pass", "room password");
                return JsonResponse(new OnlineCreatedRoomDto(Room(roomId) with
                {
                    IsPrivate = true,
                    IsHidden = true
                }));
            }
            RequireRequest(request, HttpMethod.Delete, $"api/rooms/{roomId:D}");
            return EmptyResponse();
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "room lobby setup restore failed");
        Require((await session.GetVisibleRoomsAsync("合作", 1)).Rooms.Single().Id == roomId,
            "room directory result");
        await session.CreateRoomAsync("隐藏协作", true, "room-pass", true);
        Require(session.CurrentRoom?.Id == roomId && session.CurrentMemberRole == "owner" &&
                session.CanEditMetadata && session.CanEditTiming && session.CanEditNotes,
            "owner room state not installed");
        await session.LeaveCurrentRoomAsync();
        Require(session.CurrentRoom == null, "leave retained current room");
        Require(step == 4, "room lobby workflow missed a request");
        return "directory, hidden private create, owner permissions and leave contracts preserved";
    }

    private static async Task<string> VerifyRoomLiveWorkspaceAsync()
    {
        var roomId = Guid.Parse("82222222-2222-3333-4444-555555555555");
        var store = new RecordingTokenStore { Value = "room-live-token" };
        var step = 0;
        using var client = Client(async request =>
        {
            step++;
            RequireBearer(request, "room-live-token");
            switch (step)
            {
                case 1:
                    return JsonResponse(User(82, "room-member"));
                case 2:
                {
                    RequireRequest(request, HttpMethod.Post, $"api/rooms/{roomId:D}/join");
                    var body = await ReadBodyAsync(request);
                    Require(body.GetProperty("memberRole").GetString() == "viewer", "join role");
                    return JsonResponse(Room(roomId));
                }
                case 3:
                    RequireRequest(request, HttpMethod.Get, $"api/rooms/{roomId:D}/live");
                    return JsonResponse(Live(
                        roomId,
                        Member(82, "room-member") with
                        {
                            MemberRole = "viewer",
                            CanEditMetadata = true,
                            CanEditTiming = false,
                            CanEditNotes = true
                        }));
                case 4:
                {
                    RequireRequest(request, HttpMethod.Post, $"api/rooms/{roomId:D}/chat");
                    var body = await ReadBodyAsync(request);
                    Require(body.GetProperty("message").GetString() == "你好，房间", "chat message");
                    return JsonResponse(Chat(9, 82, "你好，房间"));
                }
                case 5:
                    RequireRequest(request, HttpMethod.Post, $"api/rooms/{roomId:D}/heartbeat");
                    return EmptyResponse();
                case 6:
                    RequireRequest(request, HttpMethod.Put, $"api/rooms/{roomId:D}/members/83/access");
                    return EmptyResponse();
                case 7:
                    RequireRequest(request, HttpMethod.Delete, $"api/rooms/{roomId:D}/members/83");
                    Require((request.RequestUri?.Query ?? string.Empty).Contains("banMinutes=60", StringComparison.Ordinal),
                        "ban minutes");
                    return EmptyResponse();
                default:
                    RequireRequest(request, HttpMethod.Delete, $"api/rooms/{roomId:D}");
                    return EmptyResponse();
            }
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "room live setup restore failed");
        await session.JoinRoomAsync(roomId, memberRole: "viewer");
        Require(session.CurrentMemberRole == "viewer" && session.CanEditMetadata &&
                !session.CanEditTiming && session.CanEditNotes,
            "authoritative member access not installed");
        var chat = await session.SendRoomChatAsync("你好，房间");
        Require(chat.Value?.Id == 9, "chat result");
        Require(await session.SendRoomHeartbeatAsync() == OnlineRoomLifecycleOutcome.Success,
            "heartbeat result");
        await session.UpdateRoomMemberAccessAsync(83, "editor", true, true, true);
        await session.RemoveRoomMemberAsync(83, 60);
        await session.LeaveCurrentRoomAsync();
        Require(step == 8 && session.CurrentRoom == null, "room live workflow incomplete");
        return "join/live permissions/chat/heartbeat/member access/ban/leave contracts preserved";
    }

    private static async Task<string> VerifyHiddenRoomInvitationAsync()
    {
        var roomId = Guid.Parse("83333333-2222-3333-4444-555555555555");
        var invitationId = Guid.Parse("84444444-2222-3333-4444-555555555555");
        var store = new RecordingTokenStore { Value = "room-invite-token" };
        var step = 0;
        using var client = Client(async request =>
        {
            step++;
            RequireBearer(request, "room-invite-token");
            switch (step)
            {
                case 1:
                    return JsonResponse(User(84, "invitee"));
                case 2:
                    RequireRequest(request, HttpMethod.Get, "api/me/invitations");
                    return JsonResponse(new[]
                    {
                        new OnlineRoomInvitationDto(
                            invitationId,
                            roomId,
                            "隐藏密码房",
                            90,
                            "Inviter",
                            "default",
                            Now.AddMinutes(5))
                    });
                case 3:
                {
                    RequireRequest(
                        request,
                        HttpMethod.Post,
                        $"api/me/invitations/{invitationId:D}/respond");
                    var body = await ReadBodyAsync(request);
                    Require(body.GetProperty("action").GetString() == "join", "invitation action");
                    return JsonResponse(Room(roomId) with { IsPrivate = true, IsHidden = true });
                }
                case 4:
                    RequireRequest(request, HttpMethod.Get, $"api/rooms/{roomId:D}/live");
                    var live = Live(roomId, Member(84, "invitee"));
                    return JsonResponse(live with
                    {
                        Room = live.Room with { IsPrivate = true, IsHidden = true }
                    });
                case 5:
                    RequireRequest(request, HttpMethod.Post, $"api/rooms/{roomId:D}/invitations");
                    return JsonResponse(new OnlineRoomInvitationDto(
                        Guid.NewGuid(),
                        roomId,
                        "隐藏密码房",
                        84,
                        "Invitee",
                        "default",
                        Now.AddMinutes(5)));
                default:
                    throw new InvalidOperationException($"unexpected hidden invitation request {request.RequestUri}");
            }
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "invitation setup restore failed");
        var invitation = (await session.GetRoomInvitationsAsync()).Single();
        var response = await session.RespondToRoomInvitationAsync(invitation.Id, accept: true);
        Require(response.Kind == OnlineRoomInvitationResponseKind.Room &&
                session.CurrentRoom is { IsPrivate: true, IsHidden: true },
            "hidden invitation did not install room");
        _ = await session.InviteToCurrentRoomAsync(91);
        Require(step == 5, "hidden invitation workflow missed a request");
        return "join response entered hidden password room directly without a join/password request";
    }

    private static async Task<string> VerifyHiddenRoomWatchInvitationAsync()
    {
        var roomId = Guid.Parse("84555555-2222-3333-4444-555555555555");
        var invitationId = Guid.Parse("84666666-2222-3333-4444-555555555555");
        var store = new RecordingTokenStore { Value = "room-watch-token" };
        var step = 0;
        using var client = Client(async request =>
        {
            step++;
            RequireBearer(request, "room-watch-token");
            switch (step)
            {
                case 1:
                    return JsonResponse(User(84, "watcher"));
                case 2:
                    RequireRequest(request, HttpMethod.Get, "api/me/invitations");
                    return JsonResponse(new[]
                    {
                        new OnlineRoomInvitationDto(
                            invitationId,
                            roomId,
                            "隐藏密码观看房",
                            90,
                            "Inviter",
                            "default",
                            Now.AddMinutes(5))
                    });
                case 3:
                {
                    RequireRequest(
                        request,
                        HttpMethod.Post,
                        $"api/me/invitations/{invitationId:D}/respond");
                    var body = await ReadBodyAsync(request);
                    Require(body.GetProperty("action").GetString() == "watch", "watch invitation action");
                    return JsonResponse(Room(roomId) with { IsPrivate = true, IsHidden = true });
                }
                case 4:
                {
                    RequireRequest(request, HttpMethod.Get, $"api/rooms/{roomId:D}/live");
                    var live = Live(roomId, Member(84, "watcher") with
                    {
                        MemberRole = "viewer",
                        CanEditMetadata = false,
                        CanEditTiming = false,
                        CanEditNotes = false
                    });
                    return JsonResponse(live with
                    {
                        Room = live.Room with { IsPrivate = true, IsHidden = true }
                    });
                }
                default:
                    throw new InvalidOperationException($"unexpected watch invitation request {request.RequestUri}");
            }
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "watch invitation setup restore failed");
        var invitation = (await session.GetRoomInvitationsAsync()).Single();
        var response = await session.RespondToRoomInvitationAsync(invitation.Id, "watch");
        Require(response.Kind == OnlineRoomInvitationResponseKind.Room &&
                session.CurrentRoom is { IsPrivate: true, IsHidden: true } &&
                session.CurrentMemberRole == "viewer" &&
                !session.CanEditMetadata && !session.CanEditTiming && !session.CanEditNotes,
            "watch invitation did not install a fail-closed viewer room");
        Require(step == 4, "watch invitation unexpectedly issued a join/password request");
        return "watch response entered hidden password room as fail-closed viewer without join/password request";
    }

    private static async Task<string> VerifyLostRoomAsync()
    {
        var roomId = Guid.Parse("85555555-2222-3333-4444-555555555555");
        var store = new RecordingTokenStore { Value = "lost-room-token" };
        var step = 0;
        using var client = Client(request =>
        {
            step++;
            RequireBearer(request, "lost-room-token");
            return step switch
            {
                1 => Task.FromResult(JsonResponse(User(85, "lost-room"))),
                2 => Task.FromResult(JsonResponse(new OnlineCreatedRoomDto(Room(roomId)))),
                3 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)),
                _ => throw new InvalidOperationException($"unexpected lost-room request {request.RequestUri}")
            };
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "lost-room setup restore failed");
        await session.CreateRoomAsync("Lost Room", false, null, false);
        Require(await session.SendRoomHeartbeatAsync() == OnlineRoomLifecycleOutcome.Forbidden,
            "forbidden heartbeat outcome");
        Require(session.CurrentRoom == null && !session.CanEditNotes, "forbidden outcome retained room state");
        return "forbidden heartbeat cleared membership while preserving the authenticated account";
    }

    private static async Task<string> VerifyTransientRoomTransportOutageAsync()
    {
        var roomId = Guid.Parse("85555555-3333-4444-8555-666666666666");
        var store = new RecordingTokenStore { Value = "transient-room-token" };
        var step = 0;
        using var client = Client(request =>
        {
            step++;
            RequireBearer(request, "transient-room-token");
            return step switch
            {
                1 => Task.FromResult(JsonResponse(User(85, "transient-room"))),
                2 => Task.FromResult(JsonResponse(new OnlineCreatedRoomDto(Room(roomId)))),
                3 => throw new HttpRequestException("simulated TCP blackout"),
                4 => Task.FromResult(EmptyResponse()),
                5 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
                _ => throw new InvalidOperationException(
                    $"unexpected transient-room request {request.RequestUri}")
            };
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "transient-room setup restore failed");
        await session.CreateRoomAsync("Transient Room", false, null, false);

        var transportFailed = false;
        try
        {
            _ = await session.SendRoomHeartbeatAsync();
        }
        catch (HttpRequestException)
        {
            transportFailed = true;
        }
        Require(
            transportFailed &&
            session.CurrentRoom?.Id == roomId &&
            session.CurrentMemberRole == "owner" &&
            session.CanEditMetadata &&
            session.CanEditTiming &&
            session.CanEditNotes,
            "transient transport failure cleared authoritative room state");

        Require(
            await session.SendRoomHeartbeatAsync() == OnlineRoomLifecycleOutcome.Success &&
            session.CurrentRoom?.Id == roomId,
            "room did not recover after transient transport failure");
        Require(
            await session.SendRoomHeartbeatAsync() == OnlineRoomLifecycleOutcome.NotFound &&
            session.CurrentRoom == null &&
            !session.CanEditMetadata &&
            !session.CanEditTiming &&
            !session.CanEditNotes,
            "authoritative room loss did not clear membership after recovery");
        return "TCP failure retained room; successful heartbeat recovered; later 404 cleared membership";
    }

    private static async Task<string> VerifyStaleRoomLiveAfterLeaveAsync()
    {
        var roomId = Guid.Parse("85555555-4444-4444-8555-777777777777");
        var store = new RecordingTokenStore { Value = "stale-live-token" };
        var liveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLive = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var step = 0;
        using var client = Client(async request =>
        {
            var current = Interlocked.Increment(ref step);
            RequireBearer(request, "stale-live-token");
            switch (current)
            {
                case 1:
                    return JsonResponse(User(85, "stale-live"));
                case 2:
                    RequireRequest(request, HttpMethod.Post, "api/rooms");
                    return JsonResponse(new OnlineCreatedRoomDto(Room(roomId)));
                case 3:
                    RequireRequest(request, HttpMethod.Get, $"api/rooms/{roomId:D}/live");
                    liveStarted.TrySetResult();
                    await releaseLive.Task;
                    return JsonResponse(Live(roomId, Member(85, "stale-live")));
                case 4:
                    RequireRequest(request, HttpMethod.Delete, $"api/rooms/{roomId:D}");
                    return EmptyResponse();
                default:
                    throw new InvalidOperationException(
                        $"unexpected stale-live request {request.RequestUri}");
            }
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "stale-live setup restore failed");
        await session.CreateRoomAsync("Stale Live", false, null, false);
        var live = session.GetRoomLiveStateAsync(0);
        await liveStarted.Task;
        await session.LeaveCurrentRoomAsync();
        Require(session.CurrentRoom == null, "leave did not clear current room");
        releaseLive.TrySetResult();
        var staleResult = await live;
        Require(
            staleResult.Outcome == OnlineRoomLifecycleOutcome.Forbidden &&
            staleResult.Value == null &&
            session.CurrentRoom == null,
            "late successful live response reinstalled an explicitly left room");
        return "room generation rejected a successful live response completed after leave";
    }

    private static async Task<string> VerifyOldRoomFailureCannotClearNewRoomAsync()
    {
        var firstRoomId = Guid.Parse("85555555-5555-4444-8555-888888888888");
        var secondRoomId = Guid.Parse("85555555-6666-4444-8555-999999999999");
        var store = new RecordingTokenStore { Value = "room-switch-token" };
        var oldDeltaStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldDelta = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var step = 0;
        using var client = Client(async request =>
        {
            var current = Interlocked.Increment(ref step);
            RequireBearer(request, "room-switch-token");
            switch (current)
            {
                case 1:
                    return JsonResponse(User(85, "room-switch"));
                case 2:
                    RequireRequest(request, HttpMethod.Post, "api/rooms");
                    return JsonResponse(new OnlineCreatedRoomDto(Room(firstRoomId)));
                case 3:
                    RequireRequest(
                        request,
                        HttpMethod.Get,
                        $"api/rooms/{firstRoomId:D}/chart/delta");
                    oldDeltaStarted.TrySetResult();
                    await releaseOldDelta.Task;
                    return ErrorResponse(
                        HttpStatusCode.Forbidden,
                        "member_required",
                        "old room membership ended");
                case 4:
                    RequireRequest(
                        request,
                        HttpMethod.Post,
                        $"api/rooms/{secondRoomId:D}/join");
                    return JsonResponse(Room(secondRoomId));
                case 5:
                    RequireRequest(
                        request,
                        HttpMethod.Get,
                        $"api/rooms/{secondRoomId:D}/live");
                    return JsonResponse(
                        Live(secondRoomId, Member(85, "room-switch")));
                default:
                    throw new InvalidOperationException(
                        $"unexpected room-switch request {request.RequestUri}");
            }
        });
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "room-switch setup restore failed");
        await session.CreateRoomAsync("First Room", false, null, false);
        var oldDelta = session.GetRoomChartDeltaAsync(firstRoomId, 0);
        await oldDeltaStarted.Task;
        await session.JoinRoomAsync(secondRoomId);
        Require(
            session.CurrentRoom?.Id == secondRoomId,
            "new room was not installed before old request completion");
        releaseOldDelta.TrySetResult();
        try
        {
            _ = await oldDelta;
            throw new InvalidOperationException(
                "old room delta unexpectedly succeeded");
        }
        catch (OnlineApiException exception) when (
            exception.StatusCode == HttpStatusCode.Forbidden &&
            exception.Code == "member_required")
        {
        }
        Require(
            session.CurrentRoom?.Id == secondRoomId &&
            session.CanEditNotes,
            "old room membership failure cleared the newer room session");
        return "room-scoped membership failure left the newer joined room intact";
    }

    private static async Task<string> VerifyRoomChartWireContractsAsync()
    {
        var roomId = Guid.Parse("86666666-2222-3333-4444-555555555555");
        var snapshotId = Guid.Parse("87777777-2222-3333-4444-555555555555");
        var operationId = Guid.Parse("88888888-2222-3333-4444-555555555555");
        var token = "room-chart-token";
        var snapshot = new OnlineChartSnapshot(3, 4, [], [], []);
        var delta = new OnlineChartDelta(4, [], [], false);
        var savedSnapshot = new OnlineChartSavedSnapshot(
            snapshotId,
            "manual",
            3,
            86,
            "Owner",
            0,
            1,
            false,
            Now);
        var requests = new HashSet<string>(StringComparer.Ordinal);
        using var client = Client(async request =>
        {
            var path = request.RequestUri?.AbsolutePath.TrimStart('/') ?? string.Empty;
            var key = $"{request.Method.Method} {path}";
            requests.Add(key);
            RequireBearer(request, token);
            if (path == "api/me")
                return JsonResponse(User(86, "chart-owner"));
            if (path == $"api/rooms/{roomId:D}/chart" && request.Method == HttpMethod.Put)
            {
                var body = await ReadBodyAsync(request);
                Require(body.GetProperty("keyCount").GetInt32() == 4, "initialize key count");
                var initialLock = body.GetProperty("initialLocks")[0];
                Require(
                    initialLock.GetProperty("startBeat").GetProperty("numerator").GetInt32() == 5 &&
                    initialLock.GetProperty("startBeat").GetProperty("denominator").GetInt32() == 6,
                    "initialize exact owner lock");
                return JsonResponse(snapshot);
            }
            if (path == $"api/rooms/{roomId:D}/chart" && request.Method == HttpMethod.Get)
                return JsonResponse(snapshot);
            if (path == $"api/rooms/{roomId:D}/chart/delta")
            {
                Require((request.RequestUri?.Query ?? string.Empty).Contains("after=3", StringComparison.Ordinal),
                    "delta after revision");
                return JsonResponse(delta);
            }
            if (path == $"api/rooms/{roomId:D}/chart/operations/delta")
            {
                var body = await ReadBodyAsync(request);
                Require(body.GetProperty("baseRevision").GetInt64() == 3, "operation base revision");
                Require(body.GetProperty("operationId").GetGuid() == operationId, "stable operation id");
                return JsonResponse(delta);
            }
            if (path == $"api/rooms/{roomId:D}/chart/events")
            {
                Require(request.Headers.Accept.Any(item =>
                        item.MediaType == "text/event-stream"),
                    "SSE accept header");
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("data: 9\n\n"))
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return response;
            }
            if (path == $"api/rooms/{roomId:D}/chart/locks/toggle-selection")
            {
                var body = await ReadBodyAsync(request);
                var range = body.GetProperty("ranges")[0];
                Require(range.GetProperty("startBeat").GetProperty("denominator").GetInt32() == 3,
                    "rational lock boundary");
                return JsonResponse(new OnlineToggleChartLockSelectionResult(false, []));
            }
            if (path == $"api/rooms/{roomId:D}/chart/snapshots" && request.Method == HttpMethod.Get)
                return JsonResponse(new[] { savedSnapshot });
            if (path == $"api/rooms/{roomId:D}/chart/snapshots" && request.Method == HttpMethod.Post)
            {
                var body = await ReadBodyAsync(request);
                Require(body.GetProperty("name").GetString() == "manual", "snapshot name");
                return JsonResponse(savedSnapshot);
            }
            if (path == $"api/rooms/{roomId:D}/chart/snapshots/{snapshotId:D}/rollback")
            {
                var body = await ReadBodyAsync(request);
                Require(body.GetProperty("operationId").GetGuid() == operationId, "rollback operation id");
                return JsonResponse(new OnlineChartRollbackResult(
                    snapshot,
                    savedSnapshot,
                    Guid.Parse("89999999-2222-3333-4444-555555555555")));
            }
            throw new InvalidOperationException($"unexpected room chart request {request.Method} {request.RequestUri}");
        });
        var session = Session(client, new RecordingTokenStore { Value = token });
        Require(await session.TryRestoreAsync(), "room chart setup restore failed");
        Require((await session.InitializeRoomChartAsync(
                    roomId,
                    new InitializeOnlineChartRequest(
                        4,
                        [],
                        [],
                        InitialLocks:
                        [
                            new OnlineChartLockRange(
                                new OnlineSyncBeat(8, 5, 6),
                                new OnlineSyncBeat(9, 1, 6))
                        ]))).Revision == 3,
            "initialize result");
        Require((await session.GetRoomChartAsync(roomId)).Revision == 3, "snapshot result");
        Require((await session.GetRoomChartDeltaAsync(roomId, 3)).CurrentRevision == 4, "delta result");
        var batch = new OnlineChartOperationBatch(3, [], [], [], [], operationId);
        Require((await session.ApplyRoomChartOperationsDeltaAsync(roomId, batch)).CurrentRevision == 4,
            "apply delta result");
        long observedRevision = -1;
        await session.ListenForRoomChartEventsAsync(roomId, revision =>
        {
            observedRevision = revision;
            return Task.CompletedTask;
        });
        Require(observedRevision == 9, "SSE revision");
        _ = await session.ToggleRoomChartLockSelectionAsync(
            roomId,
            [new OnlineChartLockRange(new OnlineSyncBeat(4, 1, 3), new OnlineSyncBeat(4, 2, 3))]);
        Require((await session.GetRoomChartSnapshotsAsync(roomId)).Single().Id == snapshotId,
            "snapshot list");
        Require((await session.CreateRoomChartSnapshotAsync(roomId, "manual")).Id == snapshotId,
            "snapshot create");
        Require((await session.RollbackRoomChartSnapshotAsync(
                    roomId,
                    snapshotId,
                    operationId)).RestoredSnapshot.Id == snapshotId,
            "snapshot rollback");
        Require(requests.Count == 10, "room chart wire coverage incomplete");
        return "authenticated initialize/snapshot/delta/idempotent operation/SSE/rational-lock/snapshot/rollback contracts preserved";
    }

    private static async Task<string> VerifyRoomResourceWorkspaceAsync(string isolationRoot)
    {
        var roomId = Guid.Parse("8aaaaaaa-2222-3333-4444-555555555555");
        var root = Path.Combine(isolationRoot, "room-resources", Guid.NewGuid().ToString("N"));
        var sourceController = new AvaloniaWorkspaceController(
            Path.Combine(root, "source-library"),
            Path.Combine(root, "source-editing"),
            "resource-owner");
        await sourceController.InitializeAsync();
        var sourceTab = await sourceController.CreateAsync("Room Resource MC", "resource-owner");
        await sourceController.PersistAsync();
        var chartBytes = await File.ReadAllBytesAsync(sourceTab.WorkingPath);
        var keyBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x11, 0x22, 0x33, 0x44 };
        var chartHash = Convert.ToHexString(SHA256.HashData(chartBytes)).ToLowerInvariant();
        var keyHash = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        var entries = new[]
        {
            new OnlineRoomResourceEntryDto("working.mc", chartBytes.LongLength, chartHash),
            new OnlineRoomResourceEntryDto("keys/hit.wav", keyBytes.LongLength, keyHash)
        };
        var manifest = new OnlineRoomResourceManifestDto(
            chartBytes.LongLength + keyBytes.LongLength,
            new string('0', 64),
            "working.mc",
            entries);
        var downloadBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [chartHash] = chartBytes,
            [keyHash] = keyBytes
        };
        WindowsOnlineRoomUploadPackage? uploadPackage = null;
        var uploadedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var token = "room-resource-token";
        using var client = Client(async request =>
        {
            RequireBearer(request, token);
            var path = request.RequestUri?.AbsolutePath.TrimStart('/') ?? string.Empty;
            if (path == "api/me") return JsonResponse(User(87, "resource-owner"));
            if (path == $"api/rooms/{roomId:D}/resources" && request.Method == HttpMethod.Get)
                return JsonResponse(manifest);
            if (path.StartsWith($"api/rooms/{roomId:D}/resources/files/", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Get)
            {
                var hash = path[(path.LastIndexOf('/') + 1)..];
                Require(downloadBytes.TryGetValue(hash, out var bytes), "unknown download blob");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        bytes ?? throw new InvalidOperationException("download blob missing"))
                };
            }
            if (path == $"api/rooms/{roomId:D}/resources/plan")
            {
                var body = await ReadBodyAsync(request);
                var planned = body.GetProperty("entries").EnumerateArray()
                    .Select(item => item.GetProperty("sha256").GetString() ?? string.Empty)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var missingBytes = uploadPackage?.Files
                    .Where(file => planned.Contains(file.Sha256, StringComparer.OrdinalIgnoreCase))
                    .GroupBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase)
                    .Sum(group => group.First().Length) ?? 0;
                return JsonResponse(new OnlineRoomResourcePlanDto(missingBytes, missingBytes, planned));
            }
            if (path.StartsWith($"api/rooms/{roomId:D}/resources/blobs/", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Put)
            {
                var hash = path[(path.LastIndexOf('/') + 1)..];
                var bytes = await (request.Content ?? throw new InvalidOperationException("upload body missing"))
                    .ReadAsByteArrayAsync();
                Require(Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase),
                    "uploaded blob hash");
                uploadedHashes.Add(hash);
                return EmptyResponse();
            }
            if (path == $"api/rooms/{roomId:D}/resources/commit")
            {
                Require(uploadPackage != null, "upload package missing at commit");
                var currentUploadPackage = uploadPackage
                    ?? throw new InvalidOperationException("upload package missing at commit");
                var body = await ReadBodyAsync(request);
                var committed = body.GetProperty("entries").EnumerateArray()
                    .Select(item => new OnlineRoomResourceEntryDto(
                        item.GetProperty("path").GetString() ?? string.Empty,
                        item.GetProperty("length").GetInt64(),
                        item.GetProperty("sha256").GetString() ?? string.Empty))
                    .ToArray();
                return JsonResponse(new OnlineRoomResourceManifestDto(
                    committed.Sum(item => item.Length),
                    new string('0', 64),
                    currentUploadPackage.ChartEntry,
                    committed));
            }
            throw new InvalidOperationException($"unexpected room resource request {request.Method} {request.RequestUri}");
        });
        var session = Session(client, new RecordingTokenStore { Value = token });
        Require(await session.TryRestoreAsync(), "room resource setup restore failed");
        var serverManifest = await session.GetRoomResourceManifestAsync(roomId);
        var resourceService = new WindowsOnlineRoomResourceService(
            Path.Combine(root, "cache"),
            Path.Combine(root, "documents"),
            Path.Combine(root, "scratch"));
        var initialStatus = await resourceService.InspectAsync(serverManifest);
        Require(initialStatus.MissingBytes == manifest.TotalBytes && initialStatus.ReusedCount == 0,
            "initial resource cache state");
        var materialized = await resourceService.DownloadAndMaterializeAsync(
            session,
            roomId,
            serverManifest);
        Require(File.Exists(materialized.ChartPath) && materialized.ResourcePaths.Count == 2,
            "resource materialization");
        var openedController = new AvaloniaWorkspaceController(
            Path.Combine(root, "opened-library"),
            Path.Combine(root, "opened-editing"),
            "resource-viewer");
        await openedController.InitializeAsync();
        var opened = await openedController.OpenAsync(materialized.ChartPath);
        Require(opened.Session.Document.Metadata.Title == "Room Resource MC",
            "materialized MC did not reopen structurally");
        var reusedStatus = await resourceService.InspectAsync(serverManifest);
        Require(reusedStatus.MissingBytes == 0 && reusedStatus.ReusedCount == 2,
            "content-addressed cache was not reused");
        var traversalRejected = false;
        try
        {
            _ = await resourceService.InspectAsync(new OnlineRoomResourceManifestDto(
                1,
                new string('0', 64),
                "../escape.mc",
                [new OnlineRoomResourceEntryDto("../escape.mc", 1, new string('0', 64))]));
        }
        catch (InvalidDataException)
        {
            traversalRejected = true;
        }
        Require(traversalRejected, "resource path traversal was accepted");
        uploadPackage = await resourceService.BuildUploadPackageAsync(sourceTab);
        var committedManifest = await resourceService.UploadAsync(session, roomId, uploadPackage);
        Require(committedManifest.Entries?.Count == uploadPackage.Files.Count,
            "resource commit entries");
        Require(uploadedHashes.SetEquals(uploadPackage.Files.Select(file => file.Sha256)),
            "resource upload plan/blob coverage");
        return "real MC cache miss/download/SHA/materialize/reopen/cache reuse/traversal rejection/plan-blob-commit passed";
    }

    private static async Task<string> VerifyRoomChartMembershipLossAsync()
    {
        var roomId = Guid.Parse("8bbbbbbb-2222-3333-4444-555555555555");
        var token = "room-chart-membership-token";
        var step = 0;
        using var client = Client(request =>
        {
            step++;
            RequireBearer(request, token);
            return step switch
            {
                1 => Task.FromResult(JsonResponse(User(88, "membership-owner"))),
                2 => Task.FromResult(JsonResponse(new OnlineCreatedRoomDto(Room(roomId)))),
                3 => Task.FromResult(ErrorResponse(
                    HttpStatusCode.Forbidden,
                    "member_required",
                    "房间成员资格已失效。")),
                _ => throw new InvalidOperationException($"unexpected membership request {request.RequestUri}")
            };
        });
        var store = new RecordingTokenStore { Value = token };
        var session = Session(client, store);
        Require(await session.TryRestoreAsync(), "membership setup restore failed");
        await session.CreateRoomAsync("Membership Room", false, null, false);
        Require(session.CurrentRoom?.Id == roomId, "membership setup room missing");
        var rejected = false;
        try
        {
            _ = await session.GetRoomChartAsync(roomId);
        }
        catch (OnlineApiException exception) when (
            exception.StatusCode == HttpStatusCode.Forbidden &&
            exception.Code == "member_required")
        {
            rejected = true;
        }
        Require(rejected, "member_required was not surfaced");
        Require(session.CurrentRoom == null && !session.CanEditNotes,
            "membership loss retained room state");
        Require(session.IsAuthenticated && session.CurrentUser?.Id == 88,
            "membership loss cleared authenticated account");
        Require(store.Value == token && store.DeleteCount == 0,
            "membership loss cleared protected account token");
        return "member_required cleared only room membership and retained authenticated account/local token";
    }

    private static OnlineRoomSummaryDto Room(Guid id) => new(
        id,
        123456,
        "协作房间",
        81,
        "Owner",
        2,
        16,
        false,
        false,
        true,
        1024,
        Now);

    private static OnlineRoomMemberDto Member(long userId, string username) => new(
        userId,
        username,
        username.ToUpperInvariant(),
        "default",
        "editor",
        Now,
        true,
        CanEditNotes: true);

    private static OnlineRoomChatMessageDto Chat(long id, long userId, string message) => new(
        id,
        userId,
        "room-member",
        "ROOM-MEMBER",
        "default",
        "viewer",
        message,
        Now);

    private static OnlineRoomLiveStateDto Live(
        Guid roomId,
        OnlineRoomMemberDto member) => new(
        Room(roomId),
        [member],
        [Chat(1, member.UserId, "existing")],
        1);

    private static Task<HttpResponseMessage> DirectoryResponse(
        HttpRequestMessage request,
        string path,
        OnlinePlayerProfileDto player,
        string? expectedQuery = null)
    {
        RequireRequest(request, HttpMethod.Get, path);
        if (expectedQuery != null)
            Require((request.RequestUri?.Query ?? string.Empty).Contains(expectedQuery, StringComparison.Ordinal),
                $"missing query {expectedQuery}");
        return Task.FromResult(JsonResponse(new OnlinePlayerDirectoryDto(
            3,
            [player],
            2,
            100,
            205)));
    }

    private static HttpResponseMessage PlayerResponse(
        HttpRequestMessage request,
        OnlinePlayerProfileDto player)
    {
        RequireRequest(request, HttpMethod.Get, $"api/players/{player.Id}");
        return JsonResponse(player);
    }

    private static HttpResponseMessage FollowResponse(
        HttpRequestMessage request,
        OnlineUserProfileDto user)
    {
        RequireRequest(request, HttpMethod.Put, "api/players/72/follow");
        return JsonResponse(user);
    }

    private static OnlinePlayerProfileDto Player(long id, string displayName) => new(
        id,
        displayName,
        "default",
        "player bio",
        4567,
        8,
        9,
        true,
        true,
        true,
        false,
        false,
        Tags: [new OnlineUserTagDto("mapper", "#79B8BD", true)]);

    private static async Task<string> VerifyAuthenticatedCreatorAsync(string isolationRoot)
    {
        var root = Path.Combine(isolationRoot, "authenticated-creator", "library");
        var editing = Path.Combine(isolationRoot, "authenticated-creator", "editing");
        var controller = new AvaloniaWorkspaceController(root, editing, "guest");
        await controller.InitializeAsync();
        var tab = await controller.CreateAsync("Account Creator", "alice");
        Require(tab.Session.Document.Metadata.Creator == "alice", "authenticated creator was not applied");
        return "new chart creator matched the authenticated username rule from WPF 1.1.0";
    }

    private static OnlineAccountSession Session(HttpClient client, ISessionTokenStore store) =>
        new(client, Endpoint, store, new FixedTimeProvider(Now));

    private static HttpClient Client(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new DelegateHandler((request, _) => send(request)));

    private static HttpClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new DelegateHandler(send));

    private static WindowsAvaloniaAppStorageLocations Locations(
        string settings,
        string isolationRoot,
        string name) => new(
        settings,
        Path.Combine(isolationRoot, name, "editing"),
        Path.Combine(isolationRoot, name, "resources"),
        Path.Combine(isolationRoot, name, "scratch"));

    private static OnlineLoginResponseDto LoginResponse(
        string token,
        DateTimeOffset expiresAt,
        long userId,
        string username) => new(token, expiresAt, User(userId, username));

    private static OnlineUserProfileDto User(long id, string username) => new(
        id,
        username,
        username.ToUpperInvariant(),
        "user",
        "default",
        Now.AddYears(-1),
        1234,
        Email: $"{username}@example.invalid");

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: JsonOptions)
    };

    private static HttpResponseMessage ErrorResponse(
        HttpStatusCode status,
        string code,
        string message) => new(status)
    {
        Content = JsonContent.Create(new { code, message }, options: JsonOptions)
    };

    private static HttpResponseMessage EmptyResponse() => new(HttpStatusCode.NoContent);

    private static async Task<JsonElement> ReadBodyAsync(HttpRequestMessage request)
    {
        var content = request.Content ?? throw new InvalidOperationException("request body missing");
        return await content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }

    private static void RequireRequest(HttpRequestMessage request, HttpMethod method, string path)
    {
        Require(request.Method == method, $"expected {method}, got {request.Method}");
        Require(request.RequestUri?.AbsolutePath.TrimStart('/') == path, $"unexpected path {request.RequestUri}");
    }

    private static void RequireBearer(HttpRequestMessage request, string token) =>
        Require(
            request.Headers.Authorization?.Scheme == "Bearer" &&
            request.Headers.Authorization.Parameter == token,
            "bearer token mismatch");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckAsync(
        ICollection<WindowsAccountSpecResult> results,
        string name,
        Func<Task<string>> verify)
    {
        try
        {
            results.Add(new WindowsAccountSpecResult(name, true, await verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsAccountSpecResult(name, false, exception.ToString()));
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class RecordingTokenStore : ISessionTokenStore
    {
        public string? Value { get; set; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }
        public Exception? SaveException { get; init; }
        public Action<string>? OnSave { get; set; }

        public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Value);
        }

        public ValueTask SaveAsync(string token, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            OnSave?.Invoke(token);
            if (SaveException != null) throw SaveException;
            Value = token;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            Value = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal sealed record WindowsAccountSpecResult(string Name, bool Passed, string Detail);

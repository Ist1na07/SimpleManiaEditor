using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Online;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsOnlineSurfaceParitySpecs
{
    public static IReadOnlyList<WindowsOnlineSurfaceParitySpecResult> Run()
    {
        var results = new List<WindowsOnlineSurfaceParitySpecResult>();
        Check(
            results,
            "online-shell-overlay-geometry-matches-wpf-110",
            VerifyGeometry);
        Check(
            results,
            "online-shell-account-and-community-surfaces-are-hostable",
            VerifyHostableSurfaces);
        Check(
            results,
            "online-title-friends-and-rooms-reuse-the-active-hosted-surface",
            VerifyHostedTitleNavigationReuse);
        Check(
            results,
            "online-shell-overlay-suppresses-editor-shortcut-routing",
            VerifyShortcutIsolation);
        Check(
            results,
            "online-room-password-overlay-geometry-matches-wpf-110",
            VerifyRoomPasswordGeometry);
        Check(
            results,
            "online-room-password-overlay-preserves-lobby-and-role-actions",
            VerifyRoomPasswordHost);
        Check(
            results,
            "online-invitation-toast-geometry-matches-wpf-110",
            VerifyInvitationToastGeometry);
        Check(
            results,
            "online-startup-restores-session-or-requires-explicit-offline-choice",
            VerifyStartupAccountChoice);
        Check(
            results,
            "online-default-avatar-and-login-identity-match-wpf-110",
            VerifyDefaultAvatar);
        Check(
            results,
            "online-owner-admin-surface-uses-managed-owner-gated-session-routes",
            VerifyOwnerAdminSurface);
        Check(
            results,
            "online-input-limits-and-latest-request-isolation-match-wpf-110",
            VerifyInputLimitsAndRequestIsolation);
        Check(
            results,
            "online-malformed-success-json-is-a-recoverable-windows-ui-failure",
            VerifyMalformedJsonFailureBoundary);
        Check(
            results,
            "online-room-creation-local-validation-stays-inside-the-dialog",
            VerifyRoomCreationValidationBoundary);
        Check(
            results,
            "online-player-directory-and-leaderboard-apply-selected-name-color",
            VerifyPlayerDirectoryNameColor);
        return results;
    }

    private static string VerifyGeometry()
    {
        Require(
            WindowsOnlineSurfaceParityPolicy.ContractVersion ==
            "windows-online-surface-r8",
            "Online surface contract version differs.");
        Require(
            WindowsOnlineSurfaceParityPolicy.LoginCardWidth == 780 &&
            WindowsOnlineSurfaceParityPolicy.LoginCardHeight == 630 &&
            WindowsOnlineSurfaceParityPolicy.LoginIdentityColumnWidth == 310 &&
            WindowsOnlineSurfaceParityPolicy.LoginIdentityPadding == 34,
            "Login card differs from WPF 780x570/310/34 geometry.");
        Require(
            WindowsOnlineSurfaceParityPolicy.LoginFormLeftRightMargin == 44 &&
            WindowsOnlineSurfaceParityPolicy.LoginFormTopMargin == 48 &&
            WindowsOnlineSurfaceParityPolicy.LoginFormBottomMargin == 32 &&
            WindowsOnlineSurfaceParityPolicy.WorkflowCardWidth == 600 &&
            WindowsOnlineSurfaceParityPolicy.WorkflowCardMaxHeight == 780 &&
            WindowsOnlineSurfaceParityPolicy.OfflineButtonRightMargin == 24 &&
            WindowsOnlineSurfaceParityPolicy.OfflineButtonBottomMargin == 22 &&
            WindowsOnlineSurfaceParityPolicy.OfflineButtonWidth == 190 &&
            WindowsOnlineSurfaceParityPolicy.OfflineButtonHeight == 46,
            "Login form/offline action margins differ from WPF.");
        Require(
            WindowsOnlineSurfaceParityPolicy.CommunityHeaderHeight == 58 &&
            WindowsOnlineSurfaceParityPolicy.CommunityNavigationWidth == 190 &&
            WindowsOnlineSurfaceParityPolicy.CommunityNavigationHorizontalMargin == 12 &&
            WindowsOnlineSurfaceParityPolicy.CommunityNavigationVerticalMargin == 18,
            "Community shell differs from WPF 58/190/12x18 geometry.");
        Require(
            WindowsOsuAuthorizationCoordinator.ResolveAuthorizationUri(
                "https://osu.ppy.sh/oauth/authorize?client_id=1") != null &&
            WindowsOsuAuthorizationCoordinator.ResolveAuthorizationUri(
                "https://evil.example/oauth/authorize") == null &&
            WindowsOsuAuthorizationCoordinator.ResolveAuthorizationUri(
                "http://osu.ppy.sh/oauth/authorize") == null,
            "Windows osu! browser handoff did not fail closed to the official HTTPS endpoint.");
        Require(
            WindowsOsuAuthorizationCoordinator.FormatUnavailableFeatureFailure(
                new OnlineApiException(
                    "404",
                    statusCode: System.Net.HttpStatusCode.NotFound)) ==
            "当前服务器尚未启用 osu! 登录与绑定，请等待服务器更新完成后重试。",
            "Missing production osu! routes no longer produce an actionable client message.");
        return "login=780x630, left=310/pad34, form=44/48/32, workflow=600x780, " +
               "offline=190x46@24/22; " +
               "community header=58, nav=190/margin12x18";
    }

    private static string VerifyMalformedJsonFailureBoundary()
    {
        var accountMethod = typeof(OnlineAccountDialog).GetMethod(
            "IsExpectedOnlineFailure",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Online account failure classifier was not found.");
        var communityMethod = typeof(OnlineCommunityDialog).GetMethod(
            "IsExpectedFailure",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Online community failure classifier was not found.");
        var malformed = new JsonException("malformed 2xx response");
        var accountContained =
            accountMethod.Invoke(null, [malformed]) is true;
        var communityContained =
            communityMethod.Invoke(null, [malformed]) is true;
        var programmingErrorExcluded =
            accountMethod.Invoke(
                null,
                [new InvalidOperationException("programming error")]) is false &&
            communityMethod.Invoke(
                null,
                [new InvalidOperationException("programming error")]) is false;
        Require(
            accountContained && communityContained && programmingErrorExcluded,
            "Malformed successful JSON was not contained or programming errors were over-classified.");
        return "JsonException=account+community recoverable; InvalidOperationException=excluded";
    }

    private static string VerifyRoomCreationValidationBoundary()
    {
        var method = typeof(OnlineCommunityDialog).GetMethod(
            "IsRecoverableActionFailure",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Online community action failure classifier was not found.");
        var localValidationContained =
            method.Invoke(
                null,
                [new InvalidOperationException(
                    "请先打开要绑定到房间的谱面；一个房间只编辑一张谱。")]) is true;
        var networkFailureContained =
            method.Invoke(null, [new HttpRequestException("offline")]) is true;
        var programmingArgumentExcluded =
            method.Invoke(null, [new ArgumentException("programming error")]) is false;
        Require(
            localValidationContained &&
            networkFailureContained &&
            programmingArgumentExcluded,
            "Room creation local validation can escape the async dialog boundary.");
        return "missing-chart/current-room validation=inline status; " +
               "network=recoverable; unrelated argument error=excluded";
    }

    private static string VerifyPlayerDirectoryNameColor()
    {
        var selected = new OnlinePlayerProfileDto(
            42,
            "ColorMapper",
            "default",
            string.Empty,
            7200,
            3,
            5,
            false,
            false,
            true,
            false,
            false,
            Tags:
            [
                new OnlineUserTagDto("普通标签", "#BB8855", false),
                new OnlineUserTagDto("名字色", "#37B8E8", true)
            ]);
        var selectedText = OnlineCommunityDialog.BuildPlayerNameText(selected);
        Require(
            selectedText.Foreground is ISolidColorBrush selectedBrush &&
            selectedBrush.Color == Color.Parse("#37B8E8"),
            "Player directory row ignored the selected name-color tag.");
        Require(
            AutomationProperties.GetAutomationId(selectedText) ==
            "OnlineCommunity.Player.42.Name" &&
            AutomationProperties.GetHelpText(selectedText)?.Contains(
                "NameColor=#37B8E8; Source=selected-tag",
                StringComparison.Ordinal) == true,
            "Player directory name-color diagnostics are missing.");

        var fallback = selected with
        {
            Id = 43,
            Tags = [new OnlineUserTagDto("普通标签", "#BB8855", false)]
        };
        Require(
            OnlineCommunityDialog.BuildPlayerNameText(fallback).Foreground is
                ISolidColorBrush fallbackBrush &&
            fallbackBrush.Color == Colors.WhiteSmoke,
            "Player directory default name color differs from WPF WhiteSmoke.");

        var malformed = selected with
        {
            Id = 44,
            Tags = [new OnlineUserTagDto("坏颜色", "not-a-color", true)]
        };
        Require(
            OnlineCommunityDialog.BuildPlayerNameText(malformed).Foreground is
                ISolidColorBrush malformedBrush &&
            malformedBrush.Color == Colors.Gray,
            "Malformed selected name color did not use the WPF gray fallback.");

        return "shared players/search/leaderboard/friends/follow row: " +
               "selected=#37B8E8; default=WhiteSmoke; malformed=Gray";
    }

    private static string VerifyHostableSurfaces()
    {
        Require(
            OnlineAccountDialog.HostedDialogAutomationId ==
            "OnlineAccount.Dialog" &&
            OnlineCommunityDialog.HostedDialogAutomationId ==
            "OnlineCommunity.Dialog",
            "Hosted account/community surfaces lost their legacy UIA identities.");
        RequireConstructor(
            typeof(OnlineAccountDialog),
            typeof(OnlineAccountSession),
            typeof(Action<bool>));
        RequireConstructor(
            typeof(OnlineCommunityDialog),
            typeof(OnlineAccountSession),
            typeof(Func<OnlineRoomSummaryDto, bool, CancellationToken, Task>),
            typeof(Func<Guid, CancellationToken, Task<
                LumaChart.Application.Collaboration.RoomChartRollbackOutcome>>),
            typeof(Action<bool>),
            typeof(Window));
        RequireMethod(typeof(OnlineAccountDialog), "DetachHostedContent");
        RequireMethod(typeof(OnlineAccountDialog), "ActivateHostedAsync");
        RequireMethod(typeof(OnlineAccountDialog), "DisposeHostedSurface");
        RequireField(typeof(OnlineAccountDialog), "loginSurface");
        RequireField(typeof(OnlineAccountDialog), "workflowSurface");
        RequireMethod(typeof(OnlineCommunityDialog), "DetachHostedContent");
        RequireMethod(typeof(OnlineCommunityDialog), "ActivateHostedAsync");
        RequireMethod(typeof(OnlineCommunityDialog), "DisposeHostedSurface");
        RequireMethod(typeof(MainWindow), "ShowHostedOnlineAccountAsync");
        RequireMethod(typeof(MainWindow), "ShowHostedOnlineCommunityAsync");
        return "account/community retain standalone windows but expose detachable content, " +
               "host activation/disposal, cached account sub-surfaces, and main-shell orchestration";
    }

    private static string VerifyHostedTitleNavigationReuse()
    {
        Require(
            Enum.GetValues<OnlineCommunityHostedSection>().SequenceEqual(
                [
                    OnlineCommunityHostedSection.Profile,
                    OnlineCommunityHostedSection.Friends,
                    OnlineCommunityHostedSection.Rooms
                ]),
            "Hosted community title navigation sections changed.");
        RequireMethod(typeof(OnlineCommunityDialog), "NavigateHosted");
        RequireMethod(typeof(OnlineCommunityDialog), "ShowHostedSection");
        RequireField(typeof(OnlineCommunityDialog), "pendingHostedSection");
        RequireField(typeof(MainWindow), "activeHostedOnlineCommunitySurface");
        RequireField(typeof(MainWindow), "pendingOnlineCommunitySection");
        var accountNavigation = typeof(MainWindow).GetMethod(
            "ShowOnlineAccountAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(OnlineCommunityHostedSection)],
            modifiers: null);
        Require(
            accountNavigation != null,
            "Main-window online navigation no longer accepts a requested hosted section.");
        return "profile/friends/rooms share one hosted surface; active navigation is reused";
    }

    private static string VerifyShortcutIsolation()
    {
        Require(
            WindowsOnlineSurfaceParityPolicy.SuppressEditorShortcutRouting(true),
            "Visible online overlay does not suppress editor shortcut routing.");
        Require(
            !WindowsOnlineSurfaceParityPolicy.SuppressEditorShortcutRouting(false),
            "Hidden online overlay suppresses normal editor shortcuts.");
        return "visible overlay=true suppress; hidden overlay=false pass through";
    }

    private static string VerifyRoomPasswordGeometry()
    {
        Require(
            WindowsOnlineSurfaceParityPolicy.RoomPasswordCardWidth == 390 &&
            WindowsOnlineSurfaceParityPolicy.RoomPasswordCardPadding == 24 &&
            WindowsOnlineSurfaceParityPolicy.RoomPasswordCardCornerRadius == 7 &&
            WindowsOnlineSurfaceParityPolicy.RoomPasswordTitleFontSize == 20 &&
            WindowsOnlineSurfaceParityPolicy.RoomPasswordButtonHeight == 38 &&
            WindowsOnlineSurfaceParityPolicy.RoomPasswordButtonGap == 10,
            "Room password card differs from WPF 390/24/7/20/38/10 geometry.");
        Require(
            WindowsOnlineSurfaceParityPolicy.RequiresRoomPasswordOverlay(
                isPrivate: true,
                suppliedPassword: null) &&
            WindowsOnlineSurfaceParityPolicy.RequiresRoomPasswordOverlay(
                isPrivate: true,
                suppliedPassword: string.Empty) &&
            !WindowsOnlineSurfaceParityPolicy.RequiresRoomPasswordOverlay(
                isPrivate: true,
                suppliedPassword: "1234") &&
            !WindowsOnlineSurfaceParityPolicy.RequiresRoomPasswordOverlay(
                isPrivate: false,
                suppliedPassword: null),
            "Room password overlay activation policy differs from WPF.");
        return "card=390 DIP, pad=24, radius=7, title=20, buttons=38/gap10; " +
               "private+empty only";
    }

    private static string VerifyRoomPasswordHost()
    {
        RequireField(typeof(OnlineCommunityDialog), "roomPasswordLayer");
        RequireField(typeof(OnlineCommunityDialog), "pendingPasswordRoom");
        RequireField(typeof(OnlineCommunityDialog), "pendingRoomMemberRole");
        RequireField(typeof(OnlineCommunityDialog), "joinRoomPassword");
        RequireField(typeof(OnlineCommunityDialog), "roomPasswordStatus");
        RequireMethod(typeof(OnlineCommunityDialog), "BuildRoomPasswordLayer");
        RequireMethod(typeof(OnlineCommunityDialog), "BeginJoinRoomAsync");
        RequireMethod(typeof(OnlineCommunityDialog), "ShowRoomPassword");
        RequireMethod(typeof(OnlineCommunityDialog), "CancelRoomPassword");
        RequireMethod(typeof(OnlineCommunityDialog), "ConfirmRoomPasswordAsync");
        RequireMethod(typeof(OnlineCommunityDialog), "JoinRoomCoreAsync");
        RequireMethod(typeof(OnlineCommunityDialog), "RefreshRoomLobbyAsync");
        return "lobby stays mounted under a dedicated hosted password layer; " +
               "editor/viewer actions share one password-safe join core; lobby has explicit refresh";
    }

    private static string VerifyInvitationToastGeometry()
    {
        Require(
            WindowsOnlineSurfaceParityPolicy.InvitationToastWidth == 360 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastTopMargin == 50 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastRightMargin == 14 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastPadding == 14 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastCornerRadius == 6,
            "Invitation toast shell differs from WPF 360/50/14/14/6 geometry.");
        Require(
            WindowsOnlineSurfaceParityPolicy.InvitationToastHeaderFontSize == 10 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastTitleFontSize == 14 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastDetailFontSize == 12 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastTitleTopMargin == 6 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastDetailTopMargin == 5 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastActionsTopMargin == 12 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastButtonHeight == 34 &&
            WindowsOnlineSurfaceParityPolicy.InvitationToastButtonGap == 8,
            "Invitation toast typography/actions differ from WPF 10/14/12/6/5/12/34/8.");
        return "toast=360 DIP, top/right=50/14, pad/radius=14/6, " +
               "text=10/14/12, flow=6/5/12, buttons=34/gap8";
    }

    private static string VerifyStartupAccountChoice()
    {
        Require(
            WindowsOnlineSurfaceParityPolicy.StartupRestoreTimeoutSeconds == 8,
            "Startup session restore no longer matches WPF's 8-second limit.");
        Require(
            WindowsOnlineSurfaceParityPolicy.RequiresStartupAccountChoice(
                evidenceChartMode: false,
                sessionAuthenticated: false),
            "A normal unauthenticated startup no longer requires an account/offline choice.");
        Require(
            !WindowsOnlineSurfaceParityPolicy.RequiresStartupAccountChoice(
                evidenceChartMode: false,
                sessionAuthenticated: true),
            "An authenticated startup incorrectly shows the account choice.");
        Require(
            !WindowsOnlineSurfaceParityPolicy.RequiresStartupAccountChoice(
                evidenceChartMode: true,
                sessionAuthenticated: false),
            "An isolated GUI evidence startup could expose the production account surface.");
        RequireMethod(typeof(MainWindow), "RestoreStartupOnlineAccountAsync");
        return "restore timeout=8s; normal guest=choice, authenticated/evidence=suppressed";
    }

    private static string VerifyDefaultAvatar()
    {
        Require(
            WindowsDefaultUserAvatar.ContractVersion ==
            "wpf-1.1.0-default-user-avatar-r1" &&
            WindowsDefaultUserAvatar.BackgroundColor == "#181D20" &&
            WindowsDefaultUserAvatar.ForegroundColor == "#8AADAF" &&
            WindowsDefaultUserAvatar.BodyGeometry ==
            "M126,400 C134,322 186,274 256,274 S378,322 386,400",
            "Default avatar no longer matches the WPF SVG source.");
        Require(
            WindowsDefaultUserAvatar.ShouldShowDefaultArtwork(null),
            "Default avatar artwork should be visible before a remote image loads.");
        Require(
            !WindowsDefaultUserAvatar.ShouldShowDefaultArtwork(new object()),
            "Default avatar artwork must not remain underneath a transparent remote image.");
        Require(
            WindowsOnlineSurfaceParityPolicy.LoginAvatarSize == 126 &&
            WindowsOnlineSurfaceParityPolicy.LoginAvatarBorderThickness == 1.5 &&
            WindowsOnlineSurfaceParityPolicy.LoginAvatarBorderColor == "#5D7175" &&
            WindowsOnlineSurfaceParityPolicy.LoginTitleTopMargin == 22 &&
            WindowsOnlineSurfaceParityPolicy.LoginDescriptionTopMargin == 9,
            "Login identity avatar/title/description geometry differs from WPF.");
        RequireMethod(typeof(OnlineCommunityDialog), "BuildAvatar");
        return "avatar=512-unit WPF SVG/#181D20/#8AADAF with mutually-exclusive remote image; " +
               "login=126/1.5/#5D7175/22/9";
    }

    private static string VerifyOwnerAdminSurface()
    {
        RequireProperty(typeof(OnlineAccountSession), "CanUseAdminApi");
        RequireMethod(typeof(OnlineAccountSession), "GetAppConfigurationAsync");
        RequireMethod(typeof(OnlineAccountSession), "GetAppConfigurationAsAdminAsync");
        RequireMethod(typeof(OnlineAccountSession), "UpdateAppConfigurationAsAdminAsync");
        RequireMethod(typeof(OnlineAccountSession), "SetUserCustomLabelAsAdminAsync");
        RequireMethod(typeof(OnlineAccountSession), "GetUserAsAdminAsync");
        RequireMethod(typeof(OnlineAccountSession), "UpdateUserAsAdminAsync");
        RequireMethod(typeof(OnlineAccountSession), "UploadUserAvatarAsAdminAsync");
        RequireMethod(typeof(OnlineAccountSession), "ResetUserAvatarAsAdminAsync");
        RequireMethod(typeof(OnlineCommunityDialog), "ShowAdminConfiguration");
        RequireMethod(typeof(OnlineCommunityDialog), "ShowAdminUser");
        RequireMethod(typeof(OnlineCommunityDialog), "BuildAdminUserEditor");
        RequireMethod(typeof(OnlineCommunityDialog), "UploadAdminAvatarAsync");
        RequireMethod(typeof(OnlineCommunityDialog), "ShowAdminDenied");
        Require(
            typeof(OnlineAccountSession).Assembly.GetReferencedAssemblies().All(reference =>
                !(reference.Name ?? string.Empty).Contains(
                    "ClientSecurity",
                    StringComparison.OrdinalIgnoreCase)),
            "Managed account session unexpectedly references a native ClientSecurity bridge.");
        return "owner-only app config/user/tag/name-color/avatar UI calls managed session routes; " +
               "non-owner denied surface exists; no native ClientSecurity reference";
    }

    private static string VerifyInputLimitsAndRequestIsolation()
    {
        Require(
            WindowsOnlineSurfaceParityPolicy.ProfileDisplayNameMaximumLength == 32 &&
            WindowsOnlineSurfaceParityPolicy.ProfileBioMaximumLength == 160 &&
            WindowsOnlineSurfaceParityPolicy.RoomSearchMaximumLength == 40 &&
            WindowsOnlineSurfaceParityPolicy.RoomNameMaximumLength == 40 &&
            WindowsOnlineSurfaceParityPolicy.RoomNumberMaximumLength == 12 &&
            WindowsOnlineSurfaceParityPolicy.RoomPasswordMaximumLength == 64 &&
            WindowsOnlineSurfaceParityPolicy.PlayerSearchMaximumLength == 32,
            "Account/community text limits differ from WPF 1.1.0.");
        Require(
            WindowsOnlineSurfaceParityPolicy.CreateDefaultRoomName("Alice") ==
            "Alice 的写谱房" &&
            WindowsOnlineSurfaceParityPolicy.CreateDefaultRoomName("  ") ==
            "Guest 的写谱房",
            "Default room name no longer matches WPF.");

        RequireField(typeof(OnlineAccountDialog), "loginCaptchaLoadInProgress");
        RequireField(typeof(OnlineAccountDialog), "workflowCaptchaLoadInProgress");
        foreach (var field in new[]
                 {
                     "directoryRequestVersion",
                     "directoryLoadCancellation",
                     "playerProfileRequestVersion",
                     "playerProfileLoadCancellation",
                     "roomDirectoryRequestVersion",
                     "roomDirectoryLoadCancellation",
                     "roomInvitationRequestVersion",
                     "roomInvitationLoadCancellation",
                     "emailCaptchaRequestVersion",
                     "emailCaptchaLoadCancellation",
                     "sectionVersion",
                     "sectionLoadRequestVersion",
                     "sectionLoadCancellation"
                 })
        {
            RequireField(typeof(OnlineCommunityDialog), field);
        }
        RequireMethod(typeof(OnlineCommunityDialog), "CancelDirectoryLoad");
        RequireMethod(typeof(OnlineCommunityDialog), "CancelPlayerProfileLoad");
        RequireMethod(typeof(OnlineCommunityDialog), "CancelRoomDirectoryLoad");
        RequireMethod(typeof(OnlineCommunityDialog), "CancelRoomInvitationLoad");
        RequireMethod(typeof(OnlineCommunityDialog), "CancelEmailCaptchaLoad");
        RequireMethod(typeof(OnlineCommunityDialog), "CancelSectionLoad");
        return "limits=display32/bio160/room-search40/room-name40/room-number12/" +
               "room-password64/player-search32; captcha, directory, profile, invitation, " +
               "email-captcha and section reads use independent latest-request gates";
    }

    private static void RequireConstructor(Type type, params Type[] parameterTypes)
    {
        var constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null);
        Require(
            constructor != null,
            $"{type.Name} does not expose the expected host-aware constructor.");
    }

    private static void RequireMethod(Type type, string name)
    {
        Require(
            type.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null,
            $"{type.Name}.{name} is unavailable.");
    }

    private static void RequireProperty(Type type, string name)
    {
        Require(
            type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null,
            $"{type.Name}.{name} is unavailable.");
    }

    private static void RequireField(Type type, string name)
    {
        Require(
            type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null,
            $"{type.Name}.{name} is unavailable.");
    }

    private static void Check(
        ICollection<WindowsOnlineSurfaceParitySpecResult> results,
        string name,
        Func<string> action)
    {
        try
        {
            results.Add(new WindowsOnlineSurfaceParitySpecResult(
                name,
                true,
                action()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsOnlineSurfaceParitySpecResult(
                name,
                false,
                exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsOnlineSurfaceParitySpecResult(
    string Name,
    bool Passed,
    string Detail);

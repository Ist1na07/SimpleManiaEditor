using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.ObjectModel;
using System.Text.Json;
using LumaChart.Application.Collaboration;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Online;

namespace LumaChart.Avalonia.Dialogs;

public sealed record OnlineRoomUploadPreparation(
    int FileCount,
    long TotalBytes,
    IReadOnlyList<OnlineRoomUploadPreviewFile>? Files = null);

public sealed record OnlineRoomUploadPreviewFile(
    string Path,
    long Length);

public sealed record OnlineRoomResourcePreparation(
    long TotalBytes,
    long MissingBytes,
    int ReusedCount,
    bool HasManifestEntries);

internal enum OnlineCommunityHostedSection
{
    Profile,
    Friends,
    Rooms
}

/// <summary>
/// Windows account profile and community workspace. Transport, authentication
/// and authoritative account-state changes remain in OnlineAccountSession.
/// Text inputs intentionally use explicit buttons and never install global
/// Enter/Escape handlers, so Chinese IME composition is committed by focus loss.
/// </summary>
public sealed class OnlineCommunityDialog : PlatformWindow
{
    internal const string HostedDialogAutomationId = "OnlineCommunity.Dialog";

    private static readonly IBrush WindowBrush = Brush("#111315");
    private static readonly IBrush PanelBrush = Brush("#181C1F");
    private static readonly IBrush RaisedBrush = Brush("#20282E");
    private static readonly IBrush StrokeBrush = Brush("#39464D");
    private static readonly IBrush AccentBrush = Brush("#79B8BD");
    private static readonly IBrush MutedBrush = Brush("#89989E");
    private static readonly IBrush ErrorBrush = Brush("#D39A88");
    private static readonly IBrush SuccessBrush = Brush("#8FC4A5");

    private readonly OnlineAccountSession session;
    private readonly Action<bool>? hostedCompletion;
    private readonly PlatformWindow? hostedOwner;
    private readonly bool openAdminConfigurationOnActivate;
    private readonly Func<CancellationToken, Task<OnlineRoomUploadPreparation>>?
        prepareRoomUpload;
    private readonly Func<OnlineRoomSummaryDto, CancellationToken, Task<OnlineRoomResourcePreparation>>?
        prepareRoomDownload;
    private readonly Func<OnlineRoomSummaryDto, bool, bool, CancellationToken, Task>?
        openRoomChart;
    private readonly Func<Guid, CancellationToken, Task<RoomChartRollbackOutcome>>? rollbackRoomChart;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Border sectionHost = new();
    private readonly AutomationBorder roomPasswordLayer = new()
    {
        Background = Brush("#D9111315"),
        IsVisible = false
    };
    private readonly TextBlock identityText = new();
    private readonly TextBlock globalStatus = new();
    private readonly Button notificationButton;
    private readonly Button roomNavigationButton;
    private readonly DispatcherTimer notificationTimer;
    private readonly DispatcherTimer roomTimer;
    private const int AvatarDecodeWidth = 256;
    private const int AvatarCacheCapacity = 64;
    private readonly Dictionary<string, AvatarCacheEntry> avatarCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<Bitmap?>> avatarLoads = new(StringComparer.Ordinal);
    private long avatarUseClock;
    private readonly List<string> notificationMessages = [];
    private readonly HashSet<long> processedNotificationIds = [];
    private readonly List<OnlineRoomChatMessageDto> roomMessages = [];

    private ListBox? directoryList;
    private TextBlock? directoryStatus;
    private Button? previousPageButton;
    private Button? nextPageButton;
    private TextBox? searchBox;
    private string directoryMode = "players";
    private string directoryQuery = string.Empty;
    private int directoryPage = 1;
    private int directoryTotalPages = 1;
    private long directoryRequestVersion;
    private CancellationTokenSource? directoryLoadCancellation;
    private long playerProfileRequestVersion;
    private CancellationTokenSource? playerProfileLoadCancellation;
    private bool actionInProgress;
    private bool notificationPollInProgress;
    private int unreadNotifications;
    private Guid emailCaptchaId;
    private Bitmap? emailCaptchaBitmap;
    private long emailCaptchaRequestVersion;
    private CancellationTokenSource? emailCaptchaLoadCancellation;
    private int emailCodeCooldownGeneration;
    private bool showingNotifications;
    private bool showingRoomWorkspace;
    private bool roomPollInProgress;
    private bool roomStatusPinned;
    private int roomUnreadMessages;
    private long latestRoomMessageId;
    private int roomDirectoryPage = 1;
    private int roomDirectoryTotalPages = 1;
    private string roomDirectoryQuery = string.Empty;
    private long roomDirectoryRequestVersion;
    private CancellationTokenSource? roomDirectoryLoadCancellation;
    private long roomInvitationRequestVersion;
    private CancellationTokenSource? roomInvitationLoadCancellation;
    private long sectionVersion;
    private long sectionLoadRequestVersion;
    private CancellationTokenSource? sectionLoadCancellation;
    private ListBox? roomDirectoryList;
    private ListBox? roomMemberList;
    private string? roomMemberListRole;
    private ListBox? roomChatList;
    private ObservableCollection<OnlineRoomChatMessageDto>? roomChatItems;
    private Guid? activeRoomViewId;
    private long localRoomExitVersion;
    private ListBox? roomInvitationList;
    private TextBlock? roomHeaderSummary;
    private TextBlock? roomStatus;
    private TextBlock? roomDirectoryStatus;
    private Button? roomPreviousButton;
    private Button? roomNextButton;
    private TextBox? roomSearchBox;
    private OnlineRoomSummaryDto? pendingPasswordRoom;
    private string pendingRoomMemberRole = "editor";
    private TextBlock? roomPasswordPrompt;
    private TextBox? joinRoomPassword;
    private TextBlock? roomPasswordStatus;
    private Button? confirmRoomPasswordButton;
    private Button? roomOpenChartButton;
    private Guid? preparedResourceRoomId;
    private CancellationTokenSource? roomResourceOperationCancellation;
    private bool leaveRoomAfterResourceCancellation;
    private OnlineCommunityHostedSection pendingHostedSection =
        OnlineCommunityHostedSection.Profile;
    private bool activated;
    private bool disposed;
    private bool completed;

    public OnlineCommunityDialog(
        OnlineAccountSession session,
        Func<CancellationToken, Task<OnlineRoomUploadPreparation>>? prepareRoomUpload = null,
        Func<OnlineRoomSummaryDto, bool, bool, CancellationToken, Task>? openRoomChart = null,
        Func<Guid, CancellationToken, Task<RoomChartRollbackOutcome>>? rollbackRoomChart = null,
        Action<bool>? hostedCompletion = null,
        PlatformWindow? hostedOwner = null,
        Func<OnlineRoomSummaryDto, CancellationToken, Task<OnlineRoomResourcePreparation>>?
            prepareRoomDownload = null)
        : this(
            session,
            prepareRoomUpload,
            prepareRoomDownload,
            openRoomChart,
            rollbackRoomChart,
            hostedCompletion,
            hostedOwner,
            openAdminConfigurationOnActivate: false,
            extendedHostContract: true)
    {
    }

    /// <summary>
    /// WPF-parity shape pinned by the online surface specs: the host owner is
    /// declared as <see cref="Window"/> and widened back to the full-surface
    /// <see cref="PlatformWindow"/>. The shared single-view build has no
    /// desktop <c>Window</c> type, so only the desktop compilation exposes it.
    /// </summary>
#if !LUMACHART_SHARED
    internal OnlineCommunityDialog(
        OnlineAccountSession session,
        Func<OnlineRoomSummaryDto, bool, CancellationToken, Task>? openRoomChart,
        Func<Guid, CancellationToken, Task<RoomChartRollbackOutcome>>? rollbackRoomChart,
        Action<bool>? hostedCompletion,
        Window? hostedOwner)
        : this(
            session,
            prepareRoomUpload: null,
            prepareRoomDownload: null,
            openRoomChart == null
                ? null
                : (room, lockExistingNotes, _, cancellationToken) =>
                    openRoomChart(room, lockExistingNotes, cancellationToken),
            rollbackRoomChart,
            hostedCompletion,
            hostedOwner as PlatformWindow,
            openAdminConfigurationOnActivate: false,
            extendedHostContract: true)
    {
    }
#endif

    internal OnlineCommunityDialog(
        OnlineAccountSession session,
        Func<OnlineRoomSummaryDto, bool, CancellationToken, Task>? openRoomChart,
        Func<Guid, CancellationToken, Task<RoomChartRollbackOutcome>>? rollbackRoomChart,
        Action<bool>? hostedCompletion,
        PlatformWindow? hostedOwner)
        : this(
            session,
            prepareRoomUpload: null,
            prepareRoomDownload: null,
            openRoomChart == null
                ? null
                : (room, lockExistingNotes, _, cancellationToken) =>
                    openRoomChart(room, lockExistingNotes, cancellationToken),
            rollbackRoomChart,
            hostedCompletion,
            hostedOwner,
            openAdminConfigurationOnActivate: false,
            extendedHostContract: true)
    {
    }

    internal OnlineCommunityDialog(
        OnlineAccountSession session,
        Func<CancellationToken, Task<OnlineRoomUploadPreparation>>? prepareRoomUpload,
        Func<OnlineRoomSummaryDto, CancellationToken, Task<OnlineRoomResourcePreparation>>?
            prepareRoomDownload,
        Func<OnlineRoomSummaryDto, bool, bool, CancellationToken, Task>? openRoomChart,
        Func<Guid, CancellationToken, Task<RoomChartRollbackOutcome>>? rollbackRoomChart,
        Action<bool>? hostedCompletion,
        PlatformWindow? hostedOwner,
        bool openAdminConfigurationOnActivate)
        : this(
            session,
            prepareRoomUpload,
            prepareRoomDownload,
            openRoomChart,
            rollbackRoomChart,
            hostedCompletion,
            hostedOwner,
            openAdminConfigurationOnActivate,
            extendedHostContract: true)
    {
    }

    private OnlineCommunityDialog(
        OnlineAccountSession session,
        Func<CancellationToken, Task<OnlineRoomUploadPreparation>>? prepareRoomUpload,
        Func<OnlineRoomSummaryDto, CancellationToken, Task<OnlineRoomResourcePreparation>>?
            prepareRoomDownload,
        Func<OnlineRoomSummaryDto, bool, bool, CancellationToken, Task>? openRoomChart,
        Func<Guid, CancellationToken, Task<RoomChartRollbackOutcome>>? rollbackRoomChart,
        Action<bool>? hostedCompletion,
        PlatformWindow? hostedOwner,
        bool openAdminConfigurationOnActivate,
        bool extendedHostContract)
    {
        _ = extendedHostContract;
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.prepareRoomUpload = prepareRoomUpload;
        this.prepareRoomDownload = prepareRoomDownload;
        this.openRoomChart = openRoomChart;
        this.rollbackRoomChart = rollbackRoomChart;
        this.hostedCompletion = hostedCompletion;
        this.hostedOwner = hostedOwner;
        this.openAdminConfigurationOnActivate = openAdminConfigurationOnActivate;
        if (!session.IsAuthenticated)
            throw new InvalidOperationException("The community workspace requires an authenticated session.");

        Title = "LumaChart Online · 个人主页与社区";
        Width = 1180;
        Height = 820;
        MinWidth = 900;
        MinHeight = 650;
        Background = WindowBrush;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        AutomationProperties.SetAutomationId(this, HostedDialogAutomationId);

        var shell = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                $"{WindowsOnlineSurfaceParityPolicy.CommunityNavigationWidth},*")
        };
        var navigation = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(
                WindowsOnlineSurfaceParityPolicy.CommunityNavigationHorizontalMargin,
                WindowsOnlineSurfaceParityPolicy.CommunityNavigationVerticalMargin)
        };
        navigation.Children.Add(new TextBlock
        {
            Text = "LumaChart Online",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        identityText.Foreground = MutedBrush;
        identityText.TextWrapping = TextWrapping.Wrap;
        identityText.Margin = new Thickness(0, 0, 0, 15);
        AutomationProperties.SetAutomationId(identityText, "OnlineCommunity.Identity");
        navigation.Children.Add(identityText);
        navigation.Children.Add(NavigationButton("我的主页", "OnlineCommunity.Navigation.Profile", ShowProfile));
        roomNavigationButton = NavigationButton(
            "联机房间",
            "OnlineCommunity.Navigation.Rooms",
            ShowRooms);
        navigation.Children.Add(roomNavigationButton);
        navigation.Children.Add(NavigationButton("全部玩家", "OnlineCommunity.Navigation.Players", () => ShowDirectory("players")));
        navigation.Children.Add(NavigationButton("制谱排行", "OnlineCommunity.Navigation.Leaderboard", () => ShowDirectory("leaderboard")));
        navigation.Children.Add(NavigationButton("好友", "OnlineCommunity.Navigation.Friends", () => ShowDirectory("friends")));
        navigation.Children.Add(NavigationButton("关注", "OnlineCommunity.Navigation.Following", () => ShowDirectory("following")));
        navigation.Children.Add(NavigationButton("粉丝", "OnlineCommunity.Navigation.Followers", () => ShowDirectory("followers")));
        notificationButton = NavigationButton("通知", "OnlineCommunity.Navigation.Notifications", ShowNotifications);
        navigation.Children.Add(notificationButton);
        navigation.Children.Add(NavigationButton("账号设置", "OnlineCommunity.Navigation.Settings", ShowAccountSettings));
        if (session.CanUseAdminApi)
        {
            navigation.Children.Add(NavigationButton(
                "内容后台",
                "OnlineCommunity.Navigation.Admin",
                ShowAdminConfiguration));
        }
        navigation.Children.Add(new Border
        {
            Height = 1,
            Background = StrokeBrush,
            Margin = new Thickness(0, 12, 0, 7)
        });
        var close = NavigationButton(
            "返回曲库",
            "OnlineCommunity.Navigation.Close",
            () => Complete(true));
        navigation.Children.Add(close);
        globalStatus.Foreground = MutedBrush;
        globalStatus.TextWrapping = TextWrapping.Wrap;
        globalStatus.FontSize = 11;
        globalStatus.Margin = new Thickness(0, 13, 0, 0);
        globalStatus.MinHeight = 42;
        AutomationProperties.SetAutomationId(globalStatus, "OnlineCommunity.GlobalStatus");
        navigation.Children.Add(globalStatus);

        var navigationBorder = new Border
        {
            Background = Brush("#15191B"),
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility =
                    global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = navigation
            }
        };
        shell.Children.Add(navigationBorder);
        sectionHost.Padding = new Thickness(28, 24);
        Grid.SetColumn(sectionHost, 1);
        shell.Children.Add(sectionHost);
        roomPasswordLayer.Child = BuildRoomPasswordLayer();
        AutomationProperties.SetAutomationId(
            roomPasswordLayer,
            "OnlineRooms.Join.PasswordLayer");
        AutomationProperties.SetName(roomPasswordLayer, "加入密码房");
        Grid.SetColumnSpan(roomPasswordLayer, 2);
        roomPasswordLayer.ZIndex = 100;
        shell.Children.Add(roomPasswordLayer);
        Content = shell;

        notificationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        notificationTimer.Tick += async (_, _) => await PollNotificationsAsync();
        roomTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        roomTimer.Tick += async (_, _) => await PollRoomAsync();
        session.Changed += Session_Changed;
        session.NotificationsReceived += Session_NotificationsReceived;
        Opened += async (_, _) => await ActivateAsync();
        Closed += (_, _) => DisposeSurface();
    }

    internal Control DetachHostedContent()
    {
        if (hostedCompletion == null)
            throw new InvalidOperationException(
                "Hosted content requires a hosted completion callback.");
        if (Content is not Control content)
            throw new InvalidOperationException("The community surface has no content.");
        Content = null;
        var hosted = new AutomationBorder { Child = content };
        AutomationProperties.SetAutomationId(hosted, HostedDialogAutomationId);
        AutomationProperties.SetName(hosted, "LumaChart Online 个人主页与社区");
        return hosted;
    }

    internal Task ActivateHostedAsync(
        bool openCurrentRoom = false,
        OnlineCommunityHostedSection initialSection =
            OnlineCommunityHostedSection.Profile)
    {
        pendingHostedSection = initialSection;
        return ActivateAsync(openCurrentRoom);
    }

    internal bool NavigateHosted(OnlineCommunityHostedSection section)
    {
        if (disposed) return false;
        pendingHostedSection = section;
        if (activated) ShowHostedSection(section);
        return true;
    }

    internal void DisposeHostedSurface() => DisposeSurface();

    private async Task ActivateAsync(bool openCurrentRoom = false)
    {
        if (activated || disposed) return;
        activated = true;
        UpdateIdentity();
        ApplyNotifications(session.RecentNotifications, markUnread: false);
        if (openAdminConfigurationOnActivate && session.CanUseAdminApi)
            ShowAdminConfiguration();
        else if (openCurrentRoom && session.CurrentRoom is { } room)
            await PrepareJoinedRoomResourcesAsync(room, lifetime.Token);
        else
            ShowHostedSection(pendingHostedSection);
        await PollNotificationsAsync();
        if (disposed) return;
        notificationTimer.Start();
        roomTimer.Start();
    }

    private void ShowHostedSection(OnlineCommunityHostedSection section)
    {
        switch (section)
        {
            case OnlineCommunityHostedSection.Friends:
                ShowDirectory("friends");
                break;
            case OnlineCommunityHostedSection.Rooms:
                ShowRooms();
                break;
            default:
                ShowProfile();
                break;
        }
    }

    private void Complete(bool result)
    {
        if (completed) return;
        completed = true;
        if (hostedCompletion != null)
        {
            hostedCompletion(result);
            return;
        }

        Close(result);
    }

    private void DisposeSurface()
    {
        if (disposed) return;
        disposed = true;
        notificationTimer.Stop();
        roomTimer.Stop();
        emailCodeCooldownGeneration++;
        CancelDirectoryLoad();
        CancelPlayerProfileLoad();
        CancelRoomDirectoryLoad();
        CancelRoomInvitationLoad();
        CancelEmailCaptchaLoad();
        CancelSectionLoad();
        lifetime.Cancel();
        session.Changed -= Session_Changed;
        session.NotificationsReceived -= Session_NotificationsReceived;
        emailCaptchaBitmap?.Dispose();
        foreach (var entry in avatarCache.Values) entry.Bitmap.Dispose();
        avatarCache.Clear();
        // lifetime is cancelled but deliberately not disposed: operations that
        // were in flight resume after this point, and their catch/finally paths
        // (invitation recovery, resource preparation, captcha reloads) still
        // read lifetime.Token. On a disposed source that getter throws
        // ObjectDisposedException out of an async Click handler, which closes
        // the application.
    }

    private void UpdateIdentity()
    {
        var user = session.CurrentUser;
        identityText.Text = user == null
            ? "登录会话已结束"
            : $"{user.DisplayName}\n@{user.Username} · {user.Role}";
        SetNavigationBadge(notificationButton, "通知", unreadNotifications);
        SetNavigationBadge(
            roomNavigationButton,
            session.CurrentRoom is { } room
                ? AvaloniaLocalizationCatalog.Format("联机房间 · #{0}", room.RoomNumber)
                : "联机房间",
            roomUnreadMessages);
    }

    private static void SetNavigationBadge(Button button, string text, int unreadCount)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (unreadCount > 0)
        {
            content.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                Background = Brush("#E34D4D"),
                CornerRadius = new CornerRadius(3.5),
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        button.Content = content;
        AutomationProperties.SetName(
            button,
            unreadCount > 0 ? AvaloniaLocalizationCatalog.Format("{0} · {1} 条未读", text, unreadCount) : text);
        AutomationProperties.SetHelpText(
            button,
            unreadCount > 0 ? AvaloniaLocalizationCatalog.Format("有 {0} 条新的联机消息或通知", unreadCount) : "没有未读项目");
    }

    private void Session_Changed(object? sender, EventArgs eventArgs)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Session_Changed(sender, eventArgs));
            return;
        }

        UpdateIdentity();
        if (!session.IsAuthenticated)
        {
            globalStatus.Text = "登录会话已结束。";
            globalStatus.Foreground = ErrorBrush;
            notificationTimer.Stop();
        }
    }

    private void ShowProfile()
    {
        showingNotifications = false;
        showingRoomWorkspace = false;
        var user = session.CurrentUser;
        if (user == null)
        {
            ShowSignedOut();
            return;
        }

        var status = StatusText("OnlineCommunity.Profile.Status");
        var displayName = TextField(
            WindowsOnlineSurfaceParityPolicy.ProfileDisplayNameMaximumLength,
            "OnlineCommunity.Profile.DisplayName");
        displayName.Text = user.DisplayName;
        var bio = TextField(
            WindowsOnlineSurfaceParityPolicy.ProfileBioMaximumLength,
            "OnlineCommunity.Profile.Bio");
        bio.Text = user.Bio;
        bio.AcceptsReturn = true;
        bio.TextWrapping = TextWrapping.Wrap;
        bio.Height = 100;
        var hideAvatar = new CheckBox
        {
            Content = "向其他玩家隐藏头像",
            IsChecked = user.HidePublicAvatar,
            Foreground = Brushes.White
        };
        AutomationProperties.SetAutomationId(hideAvatar, "OnlineCommunity.Profile.HideAvatar");
        var hideName = new CheckBox
        {
            Content = "向其他玩家隐藏显示名",
            IsChecked = user.HidePublicName,
            Foreground = Brushes.White
        };
        AutomationProperties.SetAutomationId(hideName, "OnlineCommunity.Profile.HideName");

        var profileHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        profileHeader.Children.Add(BuildAvatar(user.AvatarKey, user.DisplayName, 112));
        var profileIdentity = new StackPanel
        {
            Margin = new Thickness(22, 3, 0, 0),
            Spacing = 5,
            Children =
            {
                new TextBlock
                {
                    Text = user.DisplayName,
                    Foreground = Brushes.White,
                    FontSize = 30,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = $"@{user.Username}  ·  ID {user.Id}  ·  {user.Role}",
                    Foreground = MutedBrush
                },
                new TextBlock
                {
                    Text = AvaloniaLocalizationCatalog.Format("制谱 {0}　关注 {1}　粉丝 {2}", FormatDuration(user.ChartingSeconds), user.FollowingCount, user.FollowerCount),
                    Foreground = AccentBrush
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(user.Email)
                        ? "未绑定邮箱（使用 osu! 登录时为可选）"
                        : AvaloniaLocalizationCatalog.Format("绑定邮箱：{0}", user.Email),
                    Foreground = MutedBrush
                }
            }
        };
        Grid.SetColumn(profileIdentity, 1);
        profileHeader.Children.Add(profileIdentity);

        var uploadAvatar = ActionButton("更换头像", "OnlineCommunity.Profile.UploadAvatar");
        uploadAvatar.Click += async (_, _) => await UploadAvatarAsync(status);
        var osuAccount = user.OsuAccount;
        var osuProfile = ActionButton(
            osuAccount == null
                ? "未绑定 osu! 账户"
                : AvaloniaLocalizationCatalog.Format("osu! 主页：{0} ↗", osuAccount.Username),
            "OnlineCommunity.Profile.OsuProfile");
        osuProfile.IsEnabled = osuAccount != null;
        if (osuAccount != null)
            osuProfile.Click += async (_, _) => await OpenOsuProfileAsync(osuAccount, status);
        var bindOsu = ActionButton(
            "绑定 osu! 账户",
            "OnlineCommunity.Profile.OsuBind");
        bindOsu.IsVisible = osuAccount == null;
        bindOsu.Background = Brush("#7B3F5E");
        bindOsu.BorderBrush = Brush("#A9567C");
        bindOsu.Click += async (_, _) => await LinkOsuAsync(status);
        var syncOsuAvatar = ActionButton(
            "同步 osu! 头像",
            "OnlineCommunity.Profile.OsuAvatarSync");
        syncOsuAvatar.IsVisible = osuAccount != null;
        syncOsuAvatar.Click += async (_, _) => await SyncOsuAvatarAsync(status);
        var avatarActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                uploadAvatar,
                syncOsuAvatar,
                bindOsu
            }
        };
        foreach (var child in avatarActions.Children.OfType<Control>())
            child.Margin = new Thickness(0, 0, 8, 8);
        var saveProfile = PrimaryButton("保存资料与隐私", "OnlineCommunity.Profile.Save");
        saveProfile.Click += async (_, _) =>
        {
            if (actionInProgress) return;
            var viewVersion = sectionVersion;
            var nextDisplayName = displayName.Text?.Trim() ?? string.Empty;
            if (nextDisplayName.Length is < 1 or > 32)
            {
                SetStatus(status, "显示名须为 1～32 个字符。", error: true);
                return;
            }
            if ((bio.Text?.Length ?? 0) >
                WindowsOnlineSurfaceParityPolicy.ProfileBioMaximumLength)
            {
                SetStatus(
                    status,
                    AvaloniaLocalizationCatalog.Format("个人简介不能超过 {0} 个字符。", WindowsOnlineSurfaceParityPolicy.ProfileBioMaximumLength),
                    error: true);
                return;
            }
            await RunActionAsync(
                status,
                "正在保存个人资料…",
                async token =>
                {
                    await session.UpdateProfileAsync(
                        nextDisplayName,
                        bio.Text ?? string.Empty,
                        hideAvatar.IsChecked == true,
                        hideName.IsChecked == true,
                        token);
                    if (viewVersion == sectionVersion)
                    {
                        SetStatus(status, "个人资料与隐私设置已保存。", success: true);
                        ShowProfile();
                    }
                });
        };

        var colorNames = user.Tags?.Select(tag => tag.Name).ToArray() ?? [];
        var selectedColor = user.Tags?.FirstOrDefault(tag => tag.IsNameColor)?.Name;
        var colorBox = new ComboBox
        {
            ItemsSource = colorNames,
            SelectedItem = selectedColor ?? colorNames.FirstOrDefault(),
            MinWidth = 230,
            Height = 38,
            Background = RaisedBrush,
            Foreground = Brushes.White
        };
        AutomationProperties.SetAutomationId(colorBox, "OnlineCommunity.Profile.NameColor");
        var applyColor = ActionButton("应用名字色", "OnlineCommunity.Profile.NameColorApply");
        applyColor.IsEnabled = colorNames.Length > 0;
        applyColor.Click += async (_, _) => await UpdateNameColorAsync(colorBox.SelectedItem as string, status);
        var resetColor = ActionButton("清除名字色", "OnlineCommunity.Profile.NameColorReset");
        resetColor.IsEnabled = selectedColor != null;
        resetColor.Click += async (_, _) => await UpdateNameColorAsync(null, status);
        var colorActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { colorBox, applyColor, resetColor }
        };

        var form = new StackPanel
        {
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 10,
            Children =
            {
                SectionTitle("我的主页", "管理公开资料、隐私、头像与名字色。"),
                Card(profileHeader, new Thickness(20)),
                osuProfile,
                avatarActions,
                Label("显示名"),
                displayName,
                Label("个人简介"),
                bio,
                hideAvatar,
                hideName,
                saveProfile,
                Label("标签名字色"),
                colorActions,
                BuildTags(user.Tags),
                status
            }
        };
        SetSection(new ScrollViewer
        {
            VerticalScrollBarVisibility =
                global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = form
        });
    }

    private async Task UploadAvatarAsync(TextBlock status)
    {
        if (actionInProgress) return;
        var viewVersion = sectionVersion;
        var files = await (hostedOwner ?? this).StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
        {
            Title = "选择头像图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("头像图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }
            ]
        });
        // Not TryGetLocalPath: on iOS a picked item is a security-scoped URL to
        // the file where it actually lives, and the path stops resolving as soon
        // as Avalonia closes the scope. Staging a copy is a no-op on Windows,
        // whose pickers already hand back durable paths.
        string? path;
        try
        {
            // Staging a picked document rethrows its I/O failure; this runs
            // from a Click handler, where it would otherwise close the app.
            path = await PlatformPickedFiles.ResolveLocalPathAsync(
                files.FirstOrDefault(),
                PickedFileRetention.Transient);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            SetStatus(status, FormatFailure(exception), error: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(path)) return;
        FileInfo file;
        try
        {
            file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists) throw new FileNotFoundException("找不到头像图片。", file.FullName);
            if (file.Length is <= 0 or > 4L * 1024 * 1024)
                throw new OnlineApiException("头像文件必须介于 1 字节和 4 MB 之间。");
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            SetStatus(status, FormatFailure(exception), error: true);
            return;
        }

        await RunActionAsync(
            status,
            "正在上传头像…",
            async token =>
            {
                await using var source = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await session.UploadAvatarAsync(source, file.Length, cancellationToken: token);
                if (viewVersion == sectionVersion)
                {
                    SetStatus(status, "头像已更新。", success: true);
                    ShowProfile();
                }
            });
    }

    private async Task LinkOsuAsync(TextBlock status)
    {
        var viewVersion = sectionVersion;
        await RunActionAsync(
            status,
            "正在创建安全的 osu! 绑定请求…",
            async token =>
            {
                var user = await WindowsOsuAuthorizationCoordinator.AuthorizeAsync(
                    session,
                    linkCurrentAccount: true,
                    message => Dispatcher.UIThread.Post(() =>
                    {
                        if (viewVersion == sectionVersion)
                            SetStatus(status, message);
                    }),
                    token);
                if (viewVersion != sectionVersion) return;
                SetStatus(status, AvaloniaLocalizationCatalog.Format("已绑定 osu! 账户：{0}", user.OsuAccount?.Username), success: true);
                ShowProfile();
            },
            TimeSpan.FromMinutes(10));
    }

    private async Task SyncOsuAvatarAsync(TextBlock status)
    {
        var viewVersion = sectionVersion;
        await RunActionAsync(
            status,
            "正在从 osu! 同步头像…",
            async token =>
            {
                await session.SyncOsuAvatarAsync(token);
                if (viewVersion != sectionVersion) return;
                SetStatus(status, "已同步最新的 osu! 头像。", success: true);
                ShowProfile();
            },
            TimeSpan.FromSeconds(45));
    }

    private static async Task OpenOsuProfileAsync(
        OnlineOsuAccountDto account,
        TextBlock status)
    {
        var uri = WindowsOsuAuthorizationCoordinator.ResolveProfileUri(account);
        if (uri == null)
        {
            SetStatus(status, "osu! 主页链接无效，请刷新账号资料。", error: true);
            return;
        }
        try
        {
            await PlatformExternalUriServices.OpenAsync(uri);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus(status, "无法打开系统浏览器，请确认设备已安装并启用浏览器。", error: true);
        }
    }

    private async Task UpdateNameColorAsync(string? tagName, TextBlock status)
    {
        var viewVersion = sectionVersion;
        await RunActionAsync(
            status,
            "正在更新名字色…",
            async token =>
            {
                await session.UpdateNameColorAsync(tagName, token);
                if (viewVersion == sectionVersion)
                {
                    SetStatus(status, tagName == null ? "名字色已清除。" : "名字色已更新。", success: true);
                    ShowProfile();
                }
            });
    }

    private void ShowRooms()
    {
        showingNotifications = false;
        showingRoomWorkspace = true;
        roomUnreadMessages = 0;
        UpdateIdentity();
        if (session.CurrentRoom != null) ShowActiveRoom();
        else ShowRoomLobby();
    }

    private void ShowRoomLobby()
    {
        showingRoomWorkspace = true;
        activeRoomViewId = null;
        roomMessages.Clear();
        latestRoomMessageId = 0;
        roomMemberList = null;
        roomChatList = null;
        roomChatItems = null;
        roomHeaderSummary = null;
        roomStatus = null;

        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.35*,*"),
            ColumnSpacing = 18
        };
        var directoryPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };
        directoryPanel.Children.Add(SectionTitle(
            "联机房间",
            "公开房和未隐藏的密码房会显示在大厅；隐藏房可按房间号或好友邀请进入。"));
        var searchRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 15, 0, 10)
        };
        roomSearchBox = TextField(
            WindowsOnlineSurfaceParityPolicy.RoomSearchMaximumLength,
            "OnlineRooms.Directory.SearchText");
        roomSearchBox.Text = roomDirectoryQuery;
        roomSearchBox.PlaceholderText = "搜索房间名";
        var search = PrimaryButton("搜索", "OnlineRooms.Directory.Search");
        search.Margin = new Thickness(8, 0, 0, 0);
        search.Click += (_, _) =>
        {
            roomDirectoryQuery = roomSearchBox.Text?.Trim() ?? string.Empty;
            _ = LoadRoomDirectoryAsync(1);
        };
        Grid.SetColumn(search, 1);
        var refresh = ActionButton("刷新", "OnlineRooms.Directory.Refresh");
        refresh.Margin = new Thickness(8, 0, 0, 0);
        refresh.Click += (_, _) => _ = RefreshRoomLobbyAsync();
        Grid.SetColumn(refresh, 2);
        searchRow.Children.Add(roomSearchBox);
        searchRow.Children.Add(search);
        searchRow.Children.Add(refresh);
        Grid.SetRow(searchRow, 1);
        directoryPanel.Children.Add(searchRow);

        roomDirectoryList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<OnlineRoomSummaryDto>(
                (room, _) => BuildRoomRow(room),
                supportsRecycling: false)
        };
        AutomationProperties.SetAutomationId(roomDirectoryList, "OnlineRooms.Directory.List");
        Grid.SetRow(roomDirectoryList, 2);
        directoryPanel.Children.Add(roomDirectoryList);
        roomDirectoryStatus = StatusText("OnlineRooms.Directory.Status");
        roomPreviousButton = ActionButton("上一页", "OnlineRooms.Directory.Previous");
        roomPreviousButton.Click += (_, _) => _ = LoadRoomDirectoryAsync(roomDirectoryPage - 1);
        roomNextButton = ActionButton("下一页", "OnlineRooms.Directory.Next");
        roomNextButton.Click += (_, _) => _ = LoadRoomDirectoryAsync(roomDirectoryPage + 1);
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 10, 0, 0)
        };
        footer.Children.Add(roomPreviousButton);
        roomDirectoryStatus.HorizontalAlignment = HorizontalAlignment.Center;
        roomDirectoryStatus.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(roomDirectoryStatus, 1);
        footer.Children.Add(roomDirectoryStatus);
        Grid.SetColumn(roomNextButton, 2);
        footer.Children.Add(roomNextButton);
        Grid.SetRow(footer, 3);
        directoryPanel.Children.Add(footer);
        layout.Children.Add(directoryPanel);

        var createStatus = StatusText("OnlineRooms.Create.Status");
        var roomName = TextField(
            WindowsOnlineSurfaceParityPolicy.RoomNameMaximumLength,
            "OnlineRooms.Create.Name");
        roomName.Text = WindowsOnlineSurfaceParityPolicy.CreateDefaultRoomName(
            session.CurrentUser?.DisplayName);
        roomName.PlaceholderText = "房间名";
        var privateRoom = new CheckBox { Content = "密码房", Foreground = Brushes.White };
        AutomationProperties.SetAutomationId(privateRoom, "OnlineRooms.Create.Private");
        var hiddenRoom = new CheckBox { Content = "从公开大厅隐藏", Foreground = Brushes.White };
        AutomationProperties.SetAutomationId(hiddenRoom, "OnlineRooms.Create.Hidden");
        var lockExistingNotes = new CheckBox
        {
            Content = "创建房间时锁定已有 Note",
            Foreground = Brushes.White
        };
        AutomationProperties.SetAutomationId(
            lockExistingNotes,
            "OnlineRooms.Create.LockExistingNotes");
        ToolTip.SetTip(
            lockExistingNotes,
            "只锁已有 Note 覆盖的区段；连续空白严格超过一整小节时会断开，空白区保持可编辑");
        var roomPassword = PasswordField(
            "OnlineRooms.Create.Password",
            WindowsOnlineSurfaceParityPolicy.RoomPasswordMaximumLength);
        roomPassword.PlaceholderText = "4～64 位房间密码";
        roomPassword.IsEnabled = false;
        privateRoom.IsCheckedChanged += (_, _) =>
        {
            roomPassword.IsEnabled = privateRoom.IsChecked == true;
            if (!roomPassword.IsEnabled) roomPassword.Text = string.Empty;
        };
        CancellationTokenSource? roomCreationCancellation = null;
        var create = PrimaryButton("创建房间", "OnlineRooms.Create.Submit");
        var cancelCreation = ActionButton(
            "取消",
            "OnlineRooms.Create.Cancel");
        cancelCreation.IsVisible = false;
        void SetCreationInputsEnabled(bool enabled)
        {
            roomName.IsEnabled = enabled;
            privateRoom.IsEnabled = enabled;
            hiddenRoom.IsEnabled = enabled;
            lockExistingNotes.IsEnabled = enabled;
            roomPassword.IsEnabled = enabled && privateRoom.IsChecked == true;
        }

        cancelCreation.Click += (_, _) =>
        {
            if (roomCreationCancellation != null)
            {
                cancelCreation.Content = "正在取消";
                cancelCreation.IsEnabled = false;
                roomCreationCancellation.Cancel();
                return;
            }
            if (actionInProgress) return;
            SetCreationInputsEnabled(true);
            create.Content = "创建房间";
            cancelCreation.IsVisible = false;
            SetStatus(createStatus, "已取消创建房间。");
        };

        async Task CreatePreparedRoomAsync(PendingRoomCreationRequest pending)
        {
            if (actionInProgress) return;
            actionInProgress = true;
            create.IsEnabled = false;
            cancelCreation.Content = "取消上传";
            cancelCreation.IsEnabled = true;
            cancelCreation.IsVisible = true;
            SetCreationInputsEnabled(false);
            SetStatus(createStatus, "正在创建房间并上传资源…");
            using var cancellation = CreateTimeout(TimeSpan.FromMinutes(10));
            roomCreationCancellation = cancellation;
            try
            {
                var created = await session.CreateRoomAsync(
                    pending.Name,
                    pending.IsPrivate,
                    pending.Password,
                    pending.IsHidden,
                    cancellation.Token);
                roomMessages.Clear();
                latestRoomMessageId = 0;
                await OpenJoinedRoomChartAsync(
                    created,
                    createStatus,
                    cancellation.Token,
                    pending.LockExistingNotes,
                    usePreparedUpload: true,
                    rethrowOnFailure: true);
            }
            catch (OperationCanceledException)
            {
                await LeaveRoomAfterFailedCreationAsync();
                SetCreationInputsEnabled(true);
                create.Content = "创建房间";
                cancelCreation.IsVisible = false;
                SetStatus(createStatus, "已取消资源上传，未保留房间。");
            }
            catch (Exception exception) when (
                IsExpectedFailure(exception) || exception is InvalidOperationException)
            {
                await LeaveRoomAfterFailedCreationAsync();
                SetCreationInputsEnabled(true);
                SetStatus(createStatus, FormatFailure(exception), error: true);
                create.Content = "重新创建";
                cancelCreation.Content = "取消";
            }
            finally
            {
                roomCreationCancellation = null;
                actionInProgress = false;
                create.IsEnabled = true;
                cancelCreation.IsEnabled = true;
            }
        }

        create.Click += async (_, _) =>
        {
            if (actionInProgress) return;
            var name = roomName.Text?.Trim() ?? string.Empty;
            var password = roomPassword.Text ?? string.Empty;
            if (name.Length is < 1 or >
                WindowsOnlineSurfaceParityPolicy.RoomNameMaximumLength)
            {
                SetStatus(
                    createStatus,
                    AvaloniaLocalizationCatalog.Format("房间名须为 1～{0} 个字符。", WindowsOnlineSurfaceParityPolicy.RoomNameMaximumLength),
                    error: true);
                return;
            }
            if (privateRoom.IsChecked == true &&
                password.Length is < 4 or >
                    WindowsOnlineSurfaceParityPolicy.RoomPasswordMaximumLength)
            {
                SetStatus(
                    createStatus,
                    AvaloniaLocalizationCatalog.Format("密码房密码须为 4～{0} 个字符。", WindowsOnlineSurfaceParityPolicy.RoomPasswordMaximumLength),
                    error: true);
                return;
            }
            if (prepareRoomUpload == null)
            {
                SetStatus(createStatus, "当前编辑器无法准备房间资源。", error: true);
                return;
            }

            OnlineRoomUploadPreparation? preparation = null;
            await RunActionAsync(
                createStatus,
                "正在整理并校验谱面资源…",
                async token =>
                {
                    preparation = await prepareRoomUpload(token);
                },
                TimeSpan.FromMinutes(10));
            // The surface may have closed while resources were prepared or the
            // upload confirmation was open; never create a room from there.
            if (preparation == null || disposed) return;

            var confirmation = await ShowRoomUploadConfirmationAsync(
                preparation,
                lockExistingNotes.IsChecked == true);
            if (disposed) return;
            if (confirmation == null)
            {
                SetStatus(createStatus, "已取消创建房间。");
                return;
            }

            lockExistingNotes.IsChecked = confirmation.LockExistingNotes;
            await CreatePreparedRoomAsync(new PendingRoomCreationRequest(
                name,
                privateRoom.IsChecked == true,
                privateRoom.IsChecked == true ? password : null,
                hiddenRoom.IsChecked == true,
                confirmation.LockExistingNotes));
        };

        var joinStatus = StatusText("OnlineRooms.JoinByNumber.Status");
        var roomNumber = TextField(
            WindowsOnlineSurfaceParityPolicy.RoomNumberMaximumLength,
            "OnlineRooms.JoinByNumber.Number");
        roomNumber.PlaceholderText = "房间号";
        var roomNumberPassword = PasswordField(
            "OnlineRooms.JoinByNumber.Password",
            WindowsOnlineSurfaceParityPolicy.RoomPasswordMaximumLength);
        roomNumberPassword.PlaceholderText = "房间密码（公开房可留空）";
        var findRoom = ActionButton("查找房间", "OnlineRooms.JoinByNumber.Find");
        findRoom.Click += async (_, _) =>
        {
            if (!long.TryParse(roomNumber.Text?.Trim(), out var number) || number <= 0)
            {
                SetStatus(joinStatus, "请输入有效的房间号。", error: true);
                return;
            }
            await RunActionAsync(
                joinStatus,
                "正在查找房间…",
                async token =>
                {
                    var found = await session.FindRoomByNumberAsync(number, token);
                    var enteredPassword = roomNumberPassword.Text ?? string.Empty;
                    if (found.IsPrivate && enteredPassword.Length == 0)
                    {
                        ShowRoomPassword(found, "editor");
                        return;
                    }
                    await JoinRoomCoreAsync(
                        found,
                        enteredPassword.Length == 0 ? null : enteredPassword,
                        "editor",
                        token);
                });
        };

        roomInvitationList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 210,
            ItemTemplate = new FuncDataTemplate<OnlineRoomInvitationDto>(
                (invitation, _) => BuildInvitationRow(invitation),
                supportsRecycling: false)
        };
        AutomationProperties.SetAutomationId(roomInvitationList, "OnlineRooms.Invitations.List");
        var refreshInvitations = ActionButton("刷新邀请", "OnlineRooms.Invitations.Refresh");
        refreshInvitations.Click += (_, _) => _ = LoadRoomInvitationsAsync();

        var side = new StackPanel
        {
            Spacing = 9,
            Children =
            {
                Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Subheading("创建房间"),
                        roomName,
                        privateRoom,
                        roomPassword,
                        hiddenRoom,
                        create,
                        cancelCreation,
                        createStatus
                    }
                }, new Thickness(16)),
                Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Subheading("按房间号加入"),
                        roomNumber,
                        roomNumberPassword,
                        findRoom,
                        joinStatus
                    }
                }, new Thickness(16)),
                Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Subheading("好友邀请"),
                        new TextBlock
                        {
                            Text = "接受邀请会直接进入受邀房间；隐藏密码房不会再次要求密码。",
                            Foreground = MutedBrush,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 11
                        },
                        refreshInvitations,
                        roomInvitationList
                    }
                }, new Thickness(16))
            }
        };
        var sideScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility =
                global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = side
        };
        Grid.SetColumn(sideScroll, 1);
        layout.Children.Add(sideScroll);
        SetSection(layout);
        _ = LoadRoomDirectoryAsync(1);
        _ = LoadRoomInvitationsAsync();
    }

    private async Task RefreshRoomLobbyAsync()
    {
        await Task.WhenAll(
            LoadRoomDirectoryAsync(roomDirectoryPage),
            LoadRoomInvitationsAsync());
    }

    private async Task LoadRoomDirectoryAsync(int page)
    {
        if (roomDirectoryList == null || roomDirectoryStatus == null) return;
        var list = roomDirectoryList;
        var status = roomDirectoryStatus;
        var previous = roomPreviousButton;
        var next = roomNextButton;
        var query = roomDirectoryQuery;
        page = Math.Max(1, page);
        CancelRoomDirectoryLoad();
        var requestVersion = roomDirectoryRequestVersion;
        using var timeout = CreateTimeout(TimeSpan.FromSeconds(20));
        roomDirectoryLoadCancellation = timeout;
        SetStatus(status, "正在载入房间大厅…");
        SetPagingButtons(previous, next, roomDirectoryPage, roomDirectoryTotalPages, false);
        try
        {
            var directory = await session.GetVisibleRoomsAsync(
                query,
                page,
                timeout.Token);
            if (!IsCurrentRoomDirectoryRequest(requestVersion, timeout, list, status)) return;
            roomDirectoryPage = Math.Max(1, directory.Page);
            roomDirectoryTotalPages = Math.Max(
                1,
                (int)Math.Ceiling(directory.TotalCount / (double)Math.Max(1, directory.PageSize)));
            list.ItemsSource = directory.Rooms;
            SetStatus(
                status,
                AvaloniaLocalizationCatalog.Format("第 {0}/{1} 页 · 共 {2} 个房间", roomDirectoryPage, roomDirectoryTotalPages, directory.TotalCount),
                success: true);
        }
        catch (OperationCanceledException) when (
            requestVersion != roomDirectoryRequestVersion ||
            lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!IsCurrentRoomDirectoryRequest(requestVersion, timeout, list, status)) return;
            list.ItemsSource = Array.Empty<OnlineRoomSummaryDto>();
            SetStatus(status, FormatFailure(exception), error: true);
        }
        finally
        {
            if (ReferenceEquals(roomDirectoryLoadCancellation, timeout))
                roomDirectoryLoadCancellation = null;
            if (requestVersion == roomDirectoryRequestVersion &&
                ReferenceEquals(roomDirectoryList, list))
            {
                SetPagingButtons(
                    previous,
                    next,
                    roomDirectoryPage,
                    roomDirectoryTotalPages,
                    true);
            }
        }
    }

    private Control BuildRoomRow(OnlineRoomSummaryDto? room)
    {
        if (room == null) return RecycledTemplatePlaceholder();
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = RaisedBrush,
            Margin = new Thickness(0, 0, 0, 6),
            MinHeight = 76
        };
        var labels = new StackPanel
        {
            Margin = new Thickness(14, 10),
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = $"#{room.RoomNumber} · {room.Name}",
                    Foreground = Brushes.White,
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = AvaloniaLocalizationCatalog.Format("{0} · {1}/{2} 人 · {3} · {4}", room.OwnerDisplayName, room.MemberCount, room.MaximumMembers, (room.IsPrivate ? AvaloniaLocalizationCatalog.Text("密码房") : AvaloniaLocalizationCatalog.Text("公开房")), (room.ResourcesReady ? AvaloniaLocalizationCatalog.Text("资源就绪") : AvaloniaLocalizationCatalog.Text("资源准备中"))),
                    Foreground = MutedBrush,
                    FontSize = 11
                }
            }
        };
        row.Children.Add(labels);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(10, 18, 12, 18)
        };
        var join = ActionButton(
            room.IsPrivate ? "输入密码" : "加入",
            $"OnlineRooms.Room.{room.RoomNumber}.Join");
        join.MinWidth = 72;
        join.Click += async (_, _) => await BeginJoinRoomAsync(room, "editor");
        var watch = ActionButton(
            "旁观",
            $"OnlineRooms.Room.{room.RoomNumber}.Watch");
        watch.MinWidth = 58;
        watch.Click += async (_, _) => await BeginJoinRoomAsync(room, "viewer");
        actions.Children.Add(join);
        actions.Children.Add(watch);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }

    private Control BuildRoomPasswordLayer()
    {
        roomPasswordPrompt = new TextBlock
        {
            Text = "请输入房间密码",
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        };
        AutomationProperties.SetAutomationId(
            roomPasswordPrompt,
            "OnlineRooms.Join.PasswordPrompt");
        joinRoomPassword = PasswordField(
            "OnlineRooms.Join.Password",
            WindowsOnlineSurfaceParityPolicy.RoomPasswordMaximumLength);
        joinRoomPassword.MaxLength = 64;
        roomPasswordStatus = StatusText("OnlineRooms.Join.Status");
        roomPasswordStatus.MinHeight = 20;
        roomPasswordStatus.Margin = new Thickness(0, 8, 0, 0);

        var cancel = ActionButton("取消", "OnlineRooms.Join.Cancel");
        cancel.Height = WindowsOnlineSurfaceParityPolicy.RoomPasswordButtonHeight;
        cancel.HorizontalAlignment = HorizontalAlignment.Stretch;
        cancel.Margin = new Thickness(
            0,
            0,
            WindowsOnlineSurfaceParityPolicy.RoomPasswordButtonGap / 2,
            0);
        cancel.Click += (_, _) => CancelRoomPassword();
        confirmRoomPasswordButton = PrimaryButton("加入", "OnlineRooms.Join.Submit");
        confirmRoomPasswordButton.Height =
            WindowsOnlineSurfaceParityPolicy.RoomPasswordButtonHeight;
        confirmRoomPasswordButton.HorizontalAlignment =
            HorizontalAlignment.Stretch;
        confirmRoomPasswordButton.Margin = new Thickness(
            WindowsOnlineSurfaceParityPolicy.RoomPasswordButtonGap / 2,
            0,
            0,
            0);
        confirmRoomPasswordButton.Click += async (_, _) =>
            await ConfirmRoomPasswordAsync();
        var actions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Thickness(0, 13, 0, 0)
        };
        actions.Children.Add(cancel);
        Grid.SetColumn(confirmRoomPasswordButton, 1);
        actions.Children.Add(confirmRoomPasswordButton);

        var panel = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "加入密码房",
                    FontSize =
                        WindowsOnlineSurfaceParityPolicy.RoomPasswordTitleFontSize,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brush("#E3E7E8")
                },
                roomPasswordPrompt,
                new TextBlock
                {
                    Text = "房间密码",
                    Foreground = MutedBrush,
                    Margin = new Thickness(0, 18, 0, 5)
                },
                joinRoomPassword,
                roomPasswordStatus,
                actions
            }
        };
        var card = new AutomationBorder
        {
            Width = WindowsOnlineSurfaceParityPolicy.RoomPasswordCardWidth,
            Padding = new Thickness(
                WindowsOnlineSurfaceParityPolicy.RoomPasswordCardPadding),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush("#1A1E21"),
            BorderBrush = Brush("#435055"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(
                WindowsOnlineSurfaceParityPolicy.RoomPasswordCardCornerRadius),
            Child = panel
        };
        AutomationProperties.SetAutomationId(
            card,
            "OnlineRooms.Join.PasswordCard");
        return card;
    }

    private async Task BeginJoinRoomAsync(
        OnlineRoomSummaryDto room,
        string memberRole)
    {
        if (actionInProgress) return;
        if (room.IsPrivate)
        {
            ShowRoomPassword(room, memberRole);
            return;
        }

        await RunActionAsync(
            roomDirectoryStatus ?? globalStatus,
            AvaloniaLocalizationCatalog.Format("正在加入“{0}”…", room.Name),
            token => JoinRoomCoreAsync(room, null, memberRole, token),
            TimeSpan.FromMinutes(10));
    }

    private void ShowRoomPassword(
        OnlineRoomSummaryDto room,
        string memberRole)
    {
        pendingPasswordRoom = room;
        pendingRoomMemberRole =
            string.Equals(memberRole, "viewer", StringComparison.Ordinal)
                ? "viewer"
                : "editor";
        if (roomPasswordPrompt != null)
        {
            roomPasswordPrompt.Text =
                AvaloniaLocalizationCatalog.Format("房间 #{0} “{1}”由 {2} 创建，请输入房间密码。", room.RoomNumber, room.Name, room.OwnerDisplayName);
        }
        if (joinRoomPassword != null) joinRoomPassword.Text = string.Empty;
        if (roomPasswordStatus != null) SetStatus(roomPasswordStatus, string.Empty);
        AutomationProperties.SetHelpText(
            roomPasswordLayer,
            $"MemberRole={pendingRoomMemberRole}; RoomId={room.Id:D}; " +
            $"RoomNumber={room.RoomNumber}");
        roomPasswordLayer.IsVisible = true;
        Dispatcher.UIThread.Post(
            () => joinRoomPassword?.Focus(),
            DispatcherPriority.Input);
    }

    private void CancelRoomPassword()
    {
        pendingPasswordRoom = null;
        pendingRoomMemberRole = "editor";
        if (joinRoomPassword != null) joinRoomPassword.Text = string.Empty;
        if (roomPasswordStatus != null) SetStatus(roomPasswordStatus, string.Empty);
        AutomationProperties.SetHelpText(roomPasswordLayer, string.Empty);
        roomPasswordLayer.IsVisible = false;
    }

    private async Task ConfirmRoomPasswordAsync()
    {
        if (actionInProgress ||
            pendingPasswordRoom == null ||
            roomPasswordStatus == null)
            return;
        var password = joinRoomPassword?.Text ?? string.Empty;
        if (password.Length == 0)
        {
            SetStatus(roomPasswordStatus, "请输入房间密码。", error: true);
            return;
        }

        var room = pendingPasswordRoom;
        var memberRole = pendingRoomMemberRole;
        if (confirmRoomPasswordButton != null)
            confirmRoomPasswordButton.IsEnabled = false;
        await RunActionAsync(
            roomPasswordStatus,
            "正在验证房间密码…",
            token => JoinRoomCoreAsync(room, password, memberRole, token),
            TimeSpan.FromMinutes(10));
        if (confirmRoomPasswordButton != null)
            confirmRoomPasswordButton.IsEnabled = true;
    }

    private async Task JoinRoomCoreAsync(
        OnlineRoomSummaryDto room,
        string? password,
        string memberRole,
        CancellationToken cancellationToken)
    {
        OnlineRoomSummaryDto joined;
        try
        {
            joined = await session.JoinRoomAsync(
                room.Id,
                password,
                memberRole,
                cancellationToken);
        }
        catch (Exception exception) when (
            IsExpectedFailure(exception) &&
            session.CurrentRoom is { } established &&
            established.Id == room.Id)
        {
            // The membership may already be authoritative even if the response
            // or the following live-state request was lost. Never ask for the
            // password again; continue through the retriable resource stage.
            joined = established;
        }
        roomMessages.Clear();
        latestRoomMessageId = 0;
        roomPasswordLayer.IsVisible = false;
        pendingPasswordRoom = null;
        cancellationToken.ThrowIfCancellationRequested();

        // Membership is authoritative before any fallible manifest/cache work.
        // Keep the seat if preparation fails and let the same action retry.
        await PrepareJoinedRoomResourcesAsync(joined, cancellationToken);
    }

    private async Task LoadRoomInvitationsAsync()
    {
        if (roomInvitationList == null || !session.IsAuthenticated) return;
        var list = roomInvitationList;
        CancelRoomInvitationLoad();
        var requestVersion = roomInvitationRequestVersion;
        using var timeout = CreateTimeout(TimeSpan.FromSeconds(12));
        roomInvitationLoadCancellation = timeout;
        try
        {
            var invitations = await session.GetRoomInvitationsAsync(timeout.Token);
            if (!IsCurrentRoomInvitationRequest(requestVersion, timeout, list)) return;
            list.ItemsSource = invitations;
        }
        catch (OperationCanceledException) when (
            requestVersion != roomInvitationRequestVersion ||
            lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!IsCurrentRoomInvitationRequest(requestVersion, timeout, list)) return;
            globalStatus.Text = AvaloniaLocalizationCatalog.Format("邀请同步暂时中断：{0}", FormatFailure(exception));
            globalStatus.Foreground = MutedBrush;
        }
        finally
        {
            if (ReferenceEquals(roomInvitationLoadCancellation, timeout))
                roomInvitationLoadCancellation = null;
        }
    }

    private Control BuildInvitationRow(OnlineRoomInvitationDto? invitation)
    {
        if (invitation == null) return RecycledTemplatePlaceholder();
        var row = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 0, 0, 8)
        };
        row.Children.Add(new TextBlock
        {
            Text = AvaloniaLocalizationCatalog.Format("{0} 邀请你加入“{1}”", invitation.InviterDisplayName, invitation.RoomName),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var cancel = ActionButton("取消", $"OnlineRooms.Invitation.{invitation.Id:D}.Cancel");
        cancel.Click += async (_, _) => await RespondToInvitationAsync(invitation, "cancel");
        var watch = ActionButton("观看", $"OnlineRooms.Invitation.{invitation.Id:D}.Watch");
        watch.Click += async (_, _) => await RespondToInvitationAsync(invitation, "watch");
        var join = PrimaryButton("加入", $"OnlineRooms.Invitation.{invitation.Id:D}.Join");
        join.Click += async (_, _) => await RespondToInvitationAsync(invitation, "join");
        actions.Children.Add(cancel);
        actions.Children.Add(watch);
        actions.Children.Add(join);
        row.Children.Add(actions);
        return row;
    }

    private async Task RespondToInvitationAsync(OnlineRoomInvitationDto invitation, string action)
    {
        if (actionInProgress) return;
        actionInProgress = true;
        try
        {
            var entersRoom = action is "join" or "watch";
            if (entersRoom && session.CurrentRoom is { } currentRoom)
            {
                if (currentRoom.Id != invitation.RoomId)
                {
                    SetStatus(globalStatus, "请先退出当前联机房间，再接受新邀请。", error: true);
                    return;
                }

                using var resumeTimeout = CreateTimeout(TimeSpan.FromMinutes(10));
                await ResumeAcceptedInvitationAsync(currentRoom, resumeTimeout.Token);
                return;
            }

            if (!entersRoom && session.CurrentRoom?.Id == invitation.RoomId)
            {
                await LoadRoomInvitationsAsync();
                return;
            }

            using var timeout = CreateTimeout(entersRoom
                ? TimeSpan.FromMinutes(10)
                : TimeSpan.FromSeconds(20));
            var result = await session.RespondToRoomInvitationAsync(
                invitation.Id,
                action,
                timeout.Token);
            if (entersRoom && result.Kind == OnlineRoomInvitationResponseKind.Room)
            {
                await ResumeAcceptedInvitationAsync(
                    result.Room ?? session.CurrentRoom ??
                        throw new OnlineApiException("服务器没有返回受邀房间资料。"),
                    timeout.Token);
            }
            else
            {
                await LoadRoomInvitationsAsync();
            }
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            // Closing cancels the request that brought us here; recovering into
            // a room page of a closed surface would only start new work on it.
            if (disposed) return;
            if ((action is "join" or "watch") &&
                session.CurrentRoom is { } joinedRoom &&
                joinedRoom.Id == invitation.RoomId)
            {
                using var recoveryTimeout = CreateTimeout(TimeSpan.FromMinutes(10));
                await ResumeAcceptedInvitationAsync(joinedRoom, recoveryTimeout.Token);
                return;
            }

            globalStatus.Text = FormatFailure(exception);
            globalStatus.Foreground = ErrorBrush;
        }
        finally
        {
            actionInProgress = false;
        }
    }

    private async Task ResumeAcceptedInvitationAsync(
        OnlineRoomSummaryDto room,
        CancellationToken cancellationToken)
    {
        roomMessages.Clear();
        latestRoomMessageId = 0;
        // Invitations bypass hidden-room passwords, but resource transfer remains
        // an explicit user confirmation just like a normal room join.
        await PrepareJoinedRoomResourcesAsync(room, cancellationToken);
    }

    private async Task PrepareJoinedRoomResourcesAsync(
        OnlineRoomSummaryDto room,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        roomResourceOperationCancellation = operation;
        try
        {
            await PrepareJoinedRoomResourcesCoreAsync(room, operation.Token);
        }
        finally
        {
            if (ReferenceEquals(roomResourceOperationCancellation, operation))
                roomResourceOperationCancellation = null;
            await CompleteRequestedResourceCancellationAsync(room);
        }
    }

    private async Task PrepareJoinedRoomResourcesCoreAsync(
        OnlineRoomSummaryDto room,
        CancellationToken cancellationToken)
    {
        if (session.CurrentRoom?.Id != room.Id) return;
        preparedResourceRoomId = null;
        ShowActiveRoom();
        roomStatusPinned = true;
        if (roomOpenChartButton != null)
        {
            roomOpenChartButton.Content = "正在准备";
            roomOpenChartButton.IsEnabled = false;
        }
        SetStatus(
            roomStatus ?? globalStatus,
            AvaloniaLocalizationCatalog.Format("已加入“{0}”，正在取得资源清单并检查本地缓存。", room.Name));

        if (prepareRoomDownload == null)
        {
            preparedResourceRoomId = room.Id;
            if (roomOpenChartButton != null)
            {
                roomOpenChartButton.Content = "打开谱面";
                roomOpenChartButton.IsEnabled = openRoomChart != null;
            }
            SetStatus(
                roomStatus ?? globalStatus,
                AvaloniaLocalizationCatalog.Format("已加入“{0}”。确认后打开谱面。", room.Name),
                success: true);
            return;
        }

        try
        {
            var preparation = await prepareRoomDownload(room, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (session.CurrentRoom?.Id != room.Id) return;
            preparedResourceRoomId = room.Id;
            if (roomOpenChartButton != null)
            {
                roomOpenChartButton.Content =
                    preparation.MissingBytes == 0 ? "复用并进入" : "下载并进入";
                roomOpenChartButton.IsEnabled = openRoomChart != null;
            }
            var message = preparation.HasManifestEntries
                ? preparation.MissingBytes == 0
                    ? AvaloniaLocalizationCatalog.Format("“{0}”的全部音频、图片与 Key 音已在本地缓存；SHA-256 校验通过，无需下载。", room.Name)
                    : AvaloniaLocalizationCatalog.Format("“{0}”共 {1}；本地已复用 {2:N0} 个文件，仅需下载 {3}。", room.Name, FormatBytes(preparation.TotalBytes), preparation.ReusedCount, FormatBytes(preparation.MissingBytes))
                : AvaloniaLocalizationCatalog.Format("该房间由旧版客户端创建，需要下载整包 {0}。", FormatBytes(room.ResourceBytes));
            SetStatus(roomStatus ?? globalStatus, message, success: true);
        }
        catch (Exception exception) when (
            IsExpectedFailure(exception) || exception is InvalidOperationException)
        {
            if (session.CurrentRoom?.Id != room.Id) return;
            preparedResourceRoomId = null;
            if (roomOpenChartButton != null)
            {
                roomOpenChartButton.Content = "重试准备";
                roomOpenChartButton.IsEnabled = true;
            }
            SetStatus(
                roomStatus ?? globalStatus,
                AvaloniaLocalizationCatalog.Format("已经加入“{0}”，但资源准备暂时失败：{1}。当前席位会保留，重试不会再次要求密码。", room.Name, FormatFailure(exception)),
                error: true);
        }
    }

    private async Task OpenJoinedRoomChartAsync(
        OnlineRoomSummaryDto room,
        TextBlock status,
        CancellationToken cancellationToken,
        bool lockExistingNotes = false,
        bool usePreparedUpload = false,
        bool rethrowOnFailure = false)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.Token);
        roomResourceOperationCancellation = operation;
        try
        {
            await OpenJoinedRoomChartCoreAsync(
                room,
                status,
                operation.Token,
                lockExistingNotes,
                usePreparedUpload,
                rethrowOnFailure);
        }
        finally
        {
            if (ReferenceEquals(roomResourceOperationCancellation, operation))
                roomResourceOperationCancellation = null;
            await CompleteRequestedResourceCancellationAsync(room);
        }
    }

    private async Task OpenJoinedRoomChartCoreAsync(
        OnlineRoomSummaryDto room,
        TextBlock status,
        CancellationToken cancellationToken,
        bool lockExistingNotes,
        bool usePreparedUpload,
        bool rethrowOnFailure)
    {
        if (openRoomChart == null)
        {
            ShowActiveRoom();
            return;
        }

        try
        {
            SetStatus(
                status,
                room.ResourcesReady
                    ? "正在校验资源并打开联机谱面…"
                    : "正在初始化谱面并上传房间资源…");
            await openRoomChart(
                room,
                lockExistingNotes,
                usePreparedUpload,
                cancellationToken);
            Complete(true);
        }
        catch (Exception exception) when (IsExpectedFailure(exception) || exception is InvalidOperationException)
        {
            if (rethrowOnFailure) throw;
            ShowActiveRoom();
            roomStatusPinned = true;
            SetStatus(
                roomStatus ?? globalStatus,
                AvaloniaLocalizationCatalog.Format("已经加入房间，但谱面编辑器尚未打开：{0}", FormatFailure(exception)),
                error: true);
        }
    }

    private async Task LeaveRoomAfterFailedCreationAsync()
    {
        if (session.CurrentRoom == null) return;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            localRoomExitVersion++;
            await session.LeaveCurrentRoomAsync(cleanup.Token);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            // Best effort mirrors WPF: the original upload error remains primary.
        }
    }

    private async Task CompleteRequestedResourceCancellationAsync(
        OnlineRoomSummaryDto room)
    {
        if (!leaveRoomAfterResourceCancellation) return;
        leaveRoomAfterResourceCancellation = false;
        if (session.CurrentRoom?.Id != room.Id)
        {
            ShowRoomLobby();
            return;
        }

        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            localRoomExitVersion++;
            await session.LeaveCurrentRoomAsync(cleanup.Token);
            ShowRoomLobby();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            ShowActiveRoom();
            roomStatusPinned = true;
            SetStatus(
                roomStatus ?? globalStatus,
                AvaloniaLocalizationCatalog.Format("资源操作已取消，但退出房间失败：{0}", FormatFailure(exception)),
                error: true);
        }
    }

    private void ShowActiveRoom()
    {
        var room = session.CurrentRoom;
        if (room == null)
        {
            ShowRoomLobby();
            return;
        }
        showingRoomWorkspace = true;
        activeRoomViewId = room.Id;
        roomStatusPinned = false;
        roomUnreadMessages = 0;
        UpdateIdentity();

        var surface = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto")
        };
        var roomHeader = SectionTitle(
            $"#{room.RoomNumber} · {room.Name}",
            FormatActiveRoomSubtitle(room));
        if (roomHeader.Children[0] is TextBlock roomHeading)
        {
            AutomationProperties.SetAutomationId(
                roomHeading,
                "OnlineRooms.Active.Heading");
            AutomationProperties.SetName(
                roomHeading,
                $"#{room.RoomNumber} · {room.Name}");
        }
        roomHeaderSummary = roomHeader.Children[1] as TextBlock;
        if (roomHeaderSummary != null)
        {
            AutomationProperties.SetAutomationId(
                roomHeaderSummary,
                "OnlineRooms.Active.HeaderSummary");
            UpdateActiveRoomHeader(room, room.MemberCount);
        }
        header.Children.Add(roomHeader);
        var isOwner = room.OwnerUserId == session.CurrentUser?.Id ||
                      string.Equals(session.CurrentMemberRole, "owner", StringComparison.Ordinal);
        var openChart = PrimaryButton(
            preparedResourceRoomId == room.Id ? "打开谱面" : "准备资源",
            "OnlineRooms.Active.OpenChart");
        roomOpenChartButton = openChart;
        openChart.Margin = new Thickness(8, 0);
        openChart.IsEnabled = openRoomChart != null;
        openChart.Click += async (_, _) =>
        {
            if (openRoomChart == null || actionInProgress) return;
            if (room.ResourcesReady &&
                prepareRoomDownload != null &&
                preparedResourceRoomId != room.Id)
            {
                await RunActionAsync(
                    roomStatus ?? globalStatus,
                    "正在准备房间资源…",
                    token => PrepareJoinedRoomResourcesAsync(room, token),
                    TimeSpan.FromMinutes(10));
                return;
            }
            roomStatusPinned = false;
            await RunActionAsync(
                roomStatus ?? globalStatus,
                "正在校验资源并打开联机谱面…",
                async token => await OpenJoinedRoomChartAsync(room, roomStatus ?? globalStatus, token),
                TimeSpan.FromMinutes(10));
        };
        Grid.SetColumn(openChart, 1);
        header.Children.Add(openChart);
        var snapshots = ActionButton("快照", "OnlineRooms.Active.Snapshots");
        snapshots.Margin = new Thickness(8, 0);
        snapshots.Click += (_, _) => _ = ShowRoomSnapshotsAsync();
        Grid.SetColumn(snapshots, 2);
        header.Children.Add(snapshots);
        var invite = ActionButton("邀请好友", "OnlineRooms.Active.InviteFriend");
        invite.Margin = new Thickness(8, 0);
        invite.Click += (_, _) => _ = ShowFriendInvitesAsync();
        Grid.SetColumn(invite, 3);
        header.Children.Add(invite);
        var refresh = ActionButton("刷新", "OnlineRooms.Active.Refresh");
        refresh.Margin = new Thickness(8, 0);
        refresh.Click += (_, _) => _ = PollRoomAsync(force: true);
        Grid.SetColumn(refresh, 4);
        header.Children.Add(refresh);
        var leave = ActionButton(isOwner ? "关闭房间" : "退出房间", "OnlineRooms.Active.Leave");
        leave.Margin = new Thickness(8, 0);
        leave.Click += async (_, _) =>
        {
            if (roomResourceOperationCancellation != null)
            {
                leaveRoomAfterResourceCancellation = true;
                leave.Content = "正在取消";
                leave.IsEnabled = false;
                roomResourceOperationCancellation.Cancel();
                return;
            }
            if (actionInProgress) return;
            if (!await ConfirmRoomExitAsync(room, isOwner)) return;
            actionInProgress = true;
            try
            {
                using var timeout = CreateTimeout(TimeSpan.FromSeconds(15));
                localRoomExitVersion++;
                await session.LeaveCurrentRoomAsync(timeout.Token);
                ShowRoomLobby();
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                if (roomStatus != null) SetStatus(roomStatus, FormatFailure(exception), error: true);
            }
            finally
            {
                actionInProgress = false;
            }
        };
        Grid.SetColumn(leave, 5);
        header.Children.Add(leave);
        surface.Children.Add(header);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("0.85*,1.45*"),
            ColumnSpacing = 14,
            Margin = new Thickness(0, 16, 0, 10)
        };
        roomMemberList = new ListBox
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            ItemTemplate = new FuncDataTemplate<OnlineRoomMemberDto>(
                (member, _) => BuildRoomMemberRow(member),
                supportsRecycling: false)
        };
        AutomationProperties.SetAutomationId(roomMemberList, "OnlineRooms.Active.Members");
        body.Children.Add(roomMemberList);
        var chatPanel = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        roomChatList = new ListBox
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            ItemTemplate = new FuncDataTemplate<OnlineRoomChatMessageDto>(
                (message, _) => BuildChatRow(message),
                supportsRecycling: false)
        };
        AutomationProperties.SetAutomationId(roomChatList, "OnlineRooms.Active.Chat");
        chatPanel.Children.Add(roomChatList);
        var chatInput = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        var chatBox = TextField(500, "OnlineRooms.Active.ChatText");
        chatBox.PlaceholderText = "输入聊天消息";
        var send = PrimaryButton("发送", "OnlineRooms.Active.ChatSend");
        send.Margin = new Thickness(8, 0, 0, 0);
        send.Click += async (_, _) =>
        {
            var message = chatBox.Text?.Trim() ?? string.Empty;
            if (message.Length == 0 || actionInProgress) return;
            await RunActionAsync(
                roomStatus ?? globalStatus,
                "正在发送消息…",
                async token =>
                {
                    var result = await session.SendRoomChatAsync(message, token);
                    if (result.Outcome != OnlineRoomLifecycleOutcome.Success || result.Value == null)
                        throw new OnlineApiException("消息发送失败，房间可能已关闭或成员权限已变化。");
                    AddRoomMessages([result.Value]);
                    chatBox.Text = string.Empty;
                    RefreshActiveRoomLists();
                    SetStatus(roomStatus ?? globalStatus, "消息已发送。", success: true);
                });
        };
        Grid.SetColumn(send, 1);
        chatInput.Children.Add(chatBox);
        chatInput.Children.Add(send);
        Grid.SetRow(chatInput, 1);
        chatPanel.Children.Add(chatInput);
        Grid.SetColumn(chatPanel, 1);
        body.Children.Add(chatPanel);
        Grid.SetRow(body, 1);
        surface.Children.Add(body);

        roomStatus = StatusText("OnlineRooms.Active.Status");
        SetStatus(
            roomStatus,
            AvaloniaLocalizationCatalog.Format("身份 {0} · 权限：元数据 {1} / Timing {2} / Note {3}", WindowsOnlineRoomRolePresentationPolicy.RoleLabel(session.CurrentMemberRole), (session.CanEditMetadata ? AvaloniaLocalizationCatalog.Text("可编辑") : AvaloniaLocalizationCatalog.Text("只读")), (session.CanEditTiming ? AvaloniaLocalizationCatalog.Text("可编辑") : AvaloniaLocalizationCatalog.Text("只读")), (session.CanEditNotes ? AvaloniaLocalizationCatalog.Text("可编辑") : AvaloniaLocalizationCatalog.Text("只读"))));
        Grid.SetRow(roomStatus, 2);
        surface.Children.Add(roomStatus);
        SetSection(surface);
        RefreshActiveRoomLists();
        _ = PollRoomAsync(force: true);
    }

    private async Task<bool> ConfirmRoomExitAsync(OnlineRoomSummaryDto room, bool isOwner)
    {
        var dialog = new PlatformWindow
        {
            Title = isOwner ? "关闭联机房间" : "退出联机房间",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = global::Avalonia.Controls.WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = WindowBrush
        };
        AutomationProperties.SetAutomationId(dialog, "OnlineRooms.ExitDialog");

        var title = new TextBlock
        {
            Text = isOwner ? "关闭联机房间" : "退出联机房间",
            FontSize = 21,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        };
        AutomationProperties.SetAutomationId(title, "OnlineRooms.ExitDialog.Title");
        var message = new TextBlock
        {
            Text = isOwner
                ? AvaloniaLocalizationCatalog.Format("你是“{0}”的房主。关闭后其他成员会离开；当前谱面不会关闭，并将恢复为本地编辑。", room.Name)
                : AvaloniaLocalizationCatalog.Format("退出“{0}”后，这张谱将恢复为本地编辑状态。", room.Name),
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetAutomationId(message, "OnlineRooms.ExitDialog.Message");
        var leaseNote = new Border
        {
            Background = RaisedBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(11, 9),
            Margin = new Thickness(0, 17, 0, 0),
            Child = new TextBlock
            {
                Text = "异常断线未能通知服务器时，房间会在约 90 秒后自动回收。",
                Foreground = MutedBrush,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            }
        };
        AutomationProperties.SetAutomationId(leaseNote, "OnlineRooms.ExitDialog.LeaseNote");
        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var cancel = ActionButton("取消", "OnlineRooms.ExitDialog.Cancel");
        cancel.Height = 40;
        cancel.Click += (_, _) => dialog.Close(false);
        var confirm = ActionButton(
            isOwner ? "关闭房间" : "退出房间",
            "OnlineRooms.ExitDialog.Confirm");
        confirm.Height = 40;
        confirm.Background = Brush("#7D3F38");
        confirm.BorderBrush = Brush("#A76558");
        confirm.Click += (_, _) => dialog.Close(true);
        Grid.SetColumn(confirm, 1);
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        dialog.Content = new Border
        {
            Padding = new Thickness(26),
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = new StackPanel
            {
                Children =
                {
                    title,
                    message,
                    leaseNote,
                    buttons
                }
            }
        };
        return await dialog.ShowDialog<bool>(hostedOwner ?? this);
    }

    private async Task<RoomUploadConfirmationResult?> ShowRoomUploadConfirmationAsync(
        OnlineRoomUploadPreparation preparation,
        bool initialLockExistingNotes)
    {
        var dialog = new PlatformWindow
        {
            Title = "上传房间资源",
            Width = 650,
            MinWidth = 560,
            MaxWidth = 760,
            MaxHeight = 720,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = global::Avalonia.Controls.WindowDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = WindowBrush
        };
        AutomationProperties.SetAutomationId(dialog, "OnlineRooms.Create.UploadDialog");

        var files = new StackPanel { Spacing = 9 };
        foreach (var file in preparation.Files ?? [])
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = file.Path,
                Foreground = new SolidColorBrush(Color.Parse("#DCE6E7")),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var size = new TextBlock
            {
                Text = FormatBytes(file.Length),
                Foreground = new SolidColorBrush(Color.Parse("#78989E")),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 10,
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(size, 1);
            row.Children.Add(size);
            files.Children.Add(row);
        }
        if (files.Children.Count == 0)
        {
            files.Children.Add(new TextBlock
            {
                Text = AvaloniaLocalizationCatalog.Format("{0:N0} 个房间资源文件", preparation.FileCount),
                Foreground = MutedBrush
            });
        }

        var lockExistingNotes = new CheckBox
        {
            Content = "创建房间时锁定已有 Note",
            IsChecked = initialLockExistingNotes,
            Foreground = Brushes.White,
            Margin = new Thickness(1, 15, 0, 0)
        };
        AutomationProperties.SetAutomationId(
            lockExistingNotes,
            "OnlineRooms.Create.UploadDialog.LockExistingNotes");
        ToolTip.SetTip(
            lockExistingNotes,
            "只锁已有 Note 覆盖的区段；连续空白严格超过一整小节时会断开");

        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 9,
            Margin = new Thickness(0, 16, 0, 0),
            Foreground = Brush("#477C81"),
            Background = Brush("#242B2E")
        };
        AutomationProperties.SetAutomationId(
            progress,
            "OnlineRooms.Create.UploadDialog.Progress");

        var cancel = ActionButton(
            "取消",
            "OnlineRooms.Create.UploadDialog.Cancel");
        cancel.Height = 42;
        cancel.Click += (_, _) => dialog.Close(null);
        var confirm = PrimaryButton(
            "创建并上传",
            "OnlineRooms.Create.UploadDialog.Confirm");
        confirm.Height = 42;
        confirm.Click += (_, _) => dialog.Close(
            new RoomUploadConfirmationResult(
                lockExistingNotes.IsChecked == true));
        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 14,
            Margin = new Thickness(0, 20, 0, 0),
            Children = { cancel, confirm }
        };
        Grid.SetColumn(confirm, 1);

        dialog.Content = new Border
        {
            Padding = new Thickness(32, 27),
            Background = PanelBrush,
            BorderBrush = Brush("#485A5E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "上传房间资源",
                        FontSize = 25,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = AvaloniaLocalizationCatalog.Format("将上传 {0:N0} 个文件，共 {1}。确认后才会创建房间，本地原谱面不会被改动。", preparation.FileCount, FormatBytes(preparation.TotalBytes)),
                        Foreground = new SolidColorBrush(Color.Parse("#9DB0B3")),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 21,
                        Margin = new Thickness(0, 8, 0, 17)
                    },
                    new Border
                    {
                        Background = Brush("#14181A"),
                        BorderBrush = Brush("#344247"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(16, 14),
                        Child = new ScrollViewer
                        {
                            MaxHeight = 220,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            Content = files
                        }
                    },
                    lockExistingNotes,
                    progress,
                    new TextBlock
                    {
                        Text = AvaloniaLocalizationCatalog.Format("等待确认   ·   {0}", FormatBytes(preparation.TotalBytes)),
                        Foreground = new SolidColorBrush(Color.Parse("#78989E")),
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 10,
                        Margin = new Thickness(0, 8, 0, 0)
                    },
                    buttons
                }
            }
        };
        dialog.AddHandler(
            InputElement.KeyDownEvent,
            (_, eventArgs) =>
            {
                if (eventArgs.Key != Key.Escape) return;
                eventArgs.Handled = true;
                dialog.Close(null);
            },
            global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return await dialog.ShowDialog<RoomUploadConfirmationResult?>(
            hostedOwner ?? this);
    }

    private async Task ShowRoomSnapshotsAsync()
    {
        var room = session.CurrentRoom;
        if (room == null)
        {
            ShowRoomLobby();
            return;
        }

        showingRoomWorkspace = true;
        var status = StatusText("OnlineRooms.Snapshots.Status");
        var list = new ListBox
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            MinHeight = 360
        };
        AutomationProperties.SetAutomationId(list, "OnlineRooms.Snapshots.List");
        Func<Task>? reload = null;
        long viewVersion = 0;
        reload = async () =>
        {
            await RunSectionLoadAsync(
                viewVersion,
                status,
                "正在读取房间快照…",
                token => session.GetRoomChartSnapshotsAsync(room.Id, token),
                snapshots =>
                {
                list.ItemTemplate = new FuncDataTemplate<OnlineChartSavedSnapshot>(
                    (snapshot, _) => BuildRoomSnapshotRow(snapshot, status, reload!),
                    supportsRecycling: false);
                list.ItemsSource = snapshots;
                SetStatus(
                    status,
                    snapshots.Count == 0 ? "还没有快照。" : AvaloniaLocalizationCatalog.Format("共 {0:N0} 个快照。", snapshots.Count),
                    success: true);
                },
                TimeSpan.FromSeconds(30),
                () => list.ItemsSource = Array.Empty<OnlineChartSavedSnapshot>());
        };

        var owner = room.OwnerUserId == session.CurrentUser?.Id ||
                    session.CurrentMemberRole.Equals("owner", StringComparison.OrdinalIgnoreCase);
        var name = TextField(80, "OnlineRooms.Snapshots.Name");
        name.PlaceholderText = "快照名称（可选）";
        var create = PrimaryButton("创建快照", "OnlineRooms.Snapshots.Create");
        create.IsEnabled = owner;
        create.Click += async (_, _) =>
        {
            if (actionInProgress) return;
            actionInProgress = true;
            SetStatus(status, "正在创建房间快照…");
            try
            {
                using var timeout = CreateTimeout(TimeSpan.FromSeconds(30));
                var created = await session.CreateRoomChartSnapshotAsync(
                    room.Id,
                    string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim(),
                    timeout.Token);
                name.Text = string.Empty;
                SetStatus(status, AvaloniaLocalizationCatalog.Format("已创建快照“{0}”。", created.Name), success: true);
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                SetStatus(status, FormatFailure(exception), error: true);
            }
            finally
            {
                actionInProgress = false;
            }
            await reload();
        };
        var refresh = ActionButton("刷新", "OnlineRooms.Snapshots.Refresh");
        refresh.Click += (_, _) => _ = reload();
        var back = ActionButton("返回房间", "OnlineRooms.Snapshots.Back");
        back.Click += (_, _) => ShowActiveRoom();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { name, create, refresh, back }
        };
        SetSection(new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children =
            {
                SectionTitle(
                    "房间快照",
                    owner
                        ? "房主可创建与回滚；回滚会先由服务器创建安全快照，再安装新的权威基线。"
                        : "成员可查看快照；只有房主可以创建或回滚。"),
                Place(actions, row: 1, margin: new Thickness(0, 16, 0, 10)),
                Place(list, row: 2),
                Place(status, row: 3, margin: new Thickness(0, 10, 0, 0))
            }
        });
        viewVersion = sectionVersion;
        await reload();
    }

    private Control BuildRoomSnapshotRow(
        OnlineChartSavedSnapshot? snapshot,
        TextBlock status,
        Func<Task> reload)
    {
        if (snapshot == null) return RecycledTemplatePlaceholder();
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = RaisedBrush,
            Margin = new Thickness(0, 0, 0, 6)
        };
        row.Children.Add(new StackPanel
        {
            Margin = new Thickness(14, 10),
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = snapshot.Name,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = AvaloniaLocalizationCatalog.Format(
                        "revision {0:N0} · {1:N0} Note / {2:N0} Timing · {3} · {4:yyyy-MM-dd HH:mm:ss}{5}",
                        snapshot.SourceRevision,
                        snapshot.NoteCount,
                        snapshot.TimingCount,
                        snapshot.CreatorDisplayName,
                        snapshot.CreatedAt.ToLocalTime(),
                        snapshot.IsAutomatic
                            ? AvaloniaLocalizationCatalog.Text(" · 自动安全快照")
                            : string.Empty),
                    Foreground = MutedBrush,
                    FontSize = 11
                }
            }
        });

        var room = session.CurrentRoom;
        var owner = room != null &&
                    (room.OwnerUserId == session.CurrentUser?.Id ||
                     session.CurrentMemberRole.Equals("owner", StringComparison.OrdinalIgnoreCase));
        var rollback = ActionButton("回滚", $"OnlineRooms.Snapshot.{snapshot.Id:D}.Rollback");
        rollback.Margin = new Thickness(10);
        rollback.IsEnabled = owner && rollbackRoomChart != null;
        var armed = false;
        rollback.Click += async (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                rollback.Content = "确认回滚";
                SetStatus(
                    status,
                    AvaloniaLocalizationCatalog.Format("再次点击确认回滚到“{0}”；服务器会先创建安全快照。", snapshot.Name),
                    error: true);
                return;
            }
            if (actionInProgress || rollbackRoomChart == null) return;
            actionInProgress = true;
            rollback.IsEnabled = false;
            SetStatus(status, "正在原子回滚房间谱面…");
            try
            {
                using var timeout = CreateTimeout(TimeSpan.FromMinutes(2));
                var outcome = await rollbackRoomChart(snapshot.Id, timeout.Token);
                SetStatus(
                    status,
                    outcome.IgnoredStaleResponse
                        ? "回滚响应已过期，当前谱面未被覆盖。"
                        : AvaloniaLocalizationCatalog.Format("已回滚到“{0}”；安全快照 {1:D}。", outcome.RestoredSnapshot?.Name, outcome.SafetySnapshotId),
                    success: true);
            }
            catch (Exception exception) when (IsExpectedFailure(exception) || exception is InvalidOperationException)
            {
                SetStatus(status, FormatFailure(exception), error: true);
            }
            finally
            {
                actionInProgress = false;
                rollback.IsEnabled = owner && rollbackRoomChart != null;
            }
            await reload();
        };
        Grid.SetColumn(rollback, 1);
        row.Children.Add(rollback);
        return row;
    }

    private Control BuildRoomMemberRow(OnlineRoomMemberDto? member)
    {
        if (member == null) return RecycledTemplatePlaceholder();
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(10, 7)
        };
        row.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = member.DisplayName,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold
                },
                MemberStateText(member)
            }
        });
        if (session.CurrentMemberRole == "owner" && member.UserId != session.CurrentUser?.Id)
        {
            var manage = ActionButton("管理", $"OnlineRooms.Member.{member.UserId}.Manage");
            manage.Height = 32;
            manage.Click += (_, _) => ShowMemberManagement(member);
            Grid.SetColumn(manage, 1);
            row.Children.Add(manage);
        }
        return row;
    }

    private static TextBlock MemberStateText(OnlineRoomMemberDto member)
    {
        var state = new TextBlock
                {
                    Text = WindowsOnlineRoomRolePresentationPolicy.MemberStateLabel(
                        member.MemberRole,
                        member.IsOnline),
                    Foreground = member.IsOnline ? SuccessBrush : MutedBrush,
                    FontSize = 11
                };
        AutomationProperties.SetAutomationId(
            state,
            $"OnlineRooms.Member.{member.UserId}.State");
        return state;
    }

    private Control BuildChatRow(OnlineRoomChatMessageDto? message)
    {
        // Avalonia's virtualizing item presenter can rebuild a recycled
        // FuncDataTemplate once with null content while ItemsSource changes.
        // Treat that lifecycle value as an empty container instead of allowing
        // a live room chat refresh to terminate the Windows client.
        if (message == null) return RecycledTemplatePlaceholder();
        return new StackPanel
        {
            Margin = new Thickness(12, 7),
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = $"{message.DisplayName} · {message.CreatedAt.ToLocalTime():HH:mm:ss}",
                    Foreground = message.UserId == session.CurrentUser?.Id ? AccentBrush : MutedBrush,
                    FontSize = 11
                },
                new TextBlock
                {
                    Text = message.Message,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    private static Control RecycledTemplatePlaceholder() => new Border
    {
        IsVisible = false,
        Width = 0,
        Height = 0
    };

    private void ShowMemberManagement(OnlineRoomMemberDto member)
    {
        var status = StatusText("OnlineRooms.Member.Status");
        var roleOptions =
            WindowsOnlineRoomRolePresentationPolicy.EditableRoleOptions
                .Select(option =>
                {
                    var item = new ComboBoxItem
                    {
                        Content = option.Label,
                        Tag = option.Value
                    };
                    AutomationProperties.SetName(item, option.Label);
                    AutomationProperties.SetAutomationId(
                        item,
                        $"OnlineRooms.Member.Role.{option.Value}");
                    return item;
                })
                .ToArray();
        var initialRole =
            WindowsOnlineRoomRolePresentationPolicy.NormalizeRole(
                member.MemberRole);
        var role = new ComboBox
        {
            ItemsSource = roleOptions,
            SelectedItem = roleOptions.First(item =>
                string.Equals(
                    item.Tag as string,
                    initialRole,
                    StringComparison.Ordinal)),
            Height = 38,
            MinWidth = 220,
            Background = RaisedBrush,
            Foreground = Brushes.White
        };
        AutomationProperties.SetAutomationId(role, "OnlineRooms.Member.Role");
        var metadata = new CheckBox
        {
            Content =
                WindowsOnlineRoomRolePresentationPolicy.MetadataPermissionLabel,
            IsChecked = member.CanEditMetadata,
            Foreground = Brushes.White
        };
        var timing = new CheckBox
        {
            Content =
                WindowsOnlineRoomRolePresentationPolicy.TimingPermissionLabel,
            IsChecked = member.CanEditTiming,
            Foreground = Brushes.White
        };
        var notes = new CheckBox
        {
            Content =
                WindowsOnlineRoomRolePresentationPolicy.NotesPermissionLabel,
            IsChecked = member.CanEditNotes,
            Foreground = Brushes.White
        };
        AutomationProperties.SetAutomationId(metadata, "OnlineRooms.Member.Metadata");
        AutomationProperties.SetAutomationId(timing, "OnlineRooms.Member.Timing");
        AutomationProperties.SetAutomationId(notes, "OnlineRooms.Member.Notes");
        void RefreshPermissionAvailability()
        {
            var selected = role.SelectedItem as ComboBoxItem ??
                roleOptions[1];
            var label = selected.Content as string ?? "观察者";
            AutomationProperties.SetName(role, label);
            AutomationProperties.SetHelpText(role, AvaloniaLocalizationCatalog.Format("房间角色：{0}", label));
            var editor = string.Equals(
                selected.Tag as string,
                "editor",
                StringComparison.Ordinal);
            metadata.IsEnabled = editor;
            timing.IsEnabled = editor;
            notes.IsEnabled = editor;
        }
        role.SelectionChanged += (_, _) => RefreshPermissionAvailability();
        RefreshPermissionAvailability();
        var save = PrimaryButton(
            WindowsOnlineRoomRolePresentationPolicy.SavePermissionsLabel,
            "OnlineRooms.Member.Save");
        save.Click += async (_, _) =>
        {
            await RunActionAsync(
                status,
                "正在保存成员权限…",
                async token =>
                {
                    await session.UpdateRoomMemberAccessAsync(
                        member.UserId,
                        (role.SelectedItem as ComboBoxItem)?.Tag as string ??
                            "viewer",
                        metadata.IsChecked == true,
                        timing.IsChecked == true,
                        notes.IsChecked == true,
                        token);
                    await PollRoomAsync(force: true);
                    ShowActiveRoom();
                });
        };
        var kick = ActionButton(
            WindowsOnlineRoomRolePresentationPolicy.KickLabel,
            "OnlineRooms.Member.Kick");
        kick.Click += async (_, _) => await RemoveMemberAsync(member, 0, status);
        var ban = ActionButton(
            WindowsOnlineRoomRolePresentationPolicy.TemporaryBanLabel,
            "OnlineRooms.Member.Ban");
        ban.Click += async (_, _) => await RemoveMemberAsync(
            member,
            WindowsOnlineRoomRolePresentationPolicy.TemporaryBanMinutes,
            status);
        var back = ActionButton("返回房间", "OnlineRooms.Member.Back");
        back.Click += (_, _) => ShowActiveRoom();
        SetSection(Card(new StackPanel
        {
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
            Children =
            {
                SectionTitle(AvaloniaLocalizationCatalog.Format("管理 {0}", member.DisplayName), "角色与三类权限会由服务器持久保存，成员重新加入后仍保留。"),
                Label("房间角色"),
                role,
                Label(
                    WindowsOnlineRoomRolePresentationPolicy.PermissionHeading),
                metadata,
                timing,
                notes,
                save,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { kick, ban } },
                back,
                status
            }
        }, new Thickness(28)));
    }

    private async Task RemoveMemberAsync(OnlineRoomMemberDto member, int banMinutes, TextBlock status)
    {
        await RunActionAsync(
            status,
            banMinutes > 0 ? "正在封禁并移出成员…" : "正在移出成员…",
            async token =>
            {
                await session.RemoveRoomMemberAsync(member.UserId, banMinutes, token);
                await PollRoomAsync(force: true);
                ShowActiveRoom();
            });
    }

    private async Task ShowFriendInvitesAsync()
    {
        var status = StatusText("OnlineRooms.Friends.Status");
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        AutomationProperties.SetAutomationId(list, "OnlineRooms.Friends.List");
        var back = ActionButton("返回房间", "OnlineRooms.Friends.Back");
        back.Click += (_, _) => ShowActiveRoom();
        SetSection(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                SectionTitle("邀请好友", "只有互相关注的好友可被邀请；隐藏密码房邀请可直接接受进入。"),
                list,
                status,
                back
            }
        });
        var viewVersion = sectionVersion;
        await RunSectionLoadAsync(
            viewVersion,
            status,
            "正在载入好友列表…",
            token => session.GetFriendsAsync(token),
            friends =>
            {
                list.ItemTemplate = new FuncDataTemplate<OnlinePlayerProfileDto>(
                    (friend, _) => BuildFriendInviteRow(friend, status),
                    supportsRecycling: false);
                list.ItemsSource = friends.Players;
                SetStatus(status, AvaloniaLocalizationCatalog.Format("共 {0} 名好友。", friends.Players.Count), success: true);
            },
            TimeSpan.FromSeconds(15));
    }

    private Control BuildFriendInviteRow(OnlinePlayerProfileDto? friend, TextBlock status)
    {
        if (friend == null) return RecycledTemplatePlaceholder();
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = RaisedBrush,
            Margin = new Thickness(0, 0, 0, 6)
        };
        row.Children.Add(new TextBlock
        {
            Text = $"{friend.DisplayName} · {(friend.IsOnline ? "在线" : "离线")}",
            Foreground = Brushes.White,
            Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Center
        });
        var invite = ActionButton("邀请", $"OnlineRooms.Friend.{friend.Id}.Invite");
        invite.Margin = new Thickness(8);
        invite.Click += async (_, _) =>
        {
            await RunActionAsync(
                status,
                "正在发送邀请…",
                async token =>
                {
                    await session.InviteToCurrentRoomAsync(friend.Id, token);
                    SetStatus(status, AvaloniaLocalizationCatalog.Format("已邀请 {0}。", friend.DisplayName), success: true);
                });
        };
        Grid.SetColumn(invite, 1);
        row.Children.Add(invite);
        return row;
    }

    private async Task PollRoomAsync(bool force = false)
    {
        if (roomPollInProgress || session.CurrentRoom is not { } polledRoom || !session.IsAuthenticated) return;
        roomPollInProgress = true;
        var polledRoomId = polledRoom.Id;
        var localExitVersion = localRoomExitVersion;
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(force ? 15 : 8));
            var result = await session.GetRoomLiveStateAsync(latestRoomMessageId, timeout.Token);
            if (disposed) return;
            if (result.Outcome != OnlineRoomLifecycleOutcome.Success || result.Value == null)
            {
                // The session reports a poll that finished after a leave or a
                // switch as Forbidden without clearing anything. Only a poll that
                // actually dropped the polled room (session no longer in any
                // room, and not because this surface left it itself) is a loss.
                if (session.CurrentRoom != null || localExitVersion != localRoomExitVersion) return;
                HandleLostRoom(result.Outcome, polledRoomId);
                return;
            }
            if (session.CurrentRoom?.Id != polledRoomId) return;
            var before = roomMessages.Count;
            AddRoomMessages(result.Value.Messages);
            if (!showingRoomWorkspace && roomMessages.Count > before)
                roomUnreadMessages += roomMessages.Count - before;
            if (showingRoomWorkspace && activeRoomViewId == polledRoomId)
            {
                RefreshActiveRoomLists(result.Value);
                if (roomStatus != null && !roomStatusPinned)
                    SetStatus(
                        roomStatus,
                        AvaloniaLocalizationCatalog.Format("{0} 名成员 · 身份 {1} · 权限 M:{2} T:{3} N:{4}", result.Value.Room.MemberCount, WindowsOnlineRoomRolePresentationPolicy.RoleLabel(session.CurrentMemberRole), BoolMark(session.CanEditMetadata), BoolMark(session.CanEditTiming), BoolMark(session.CanEditNotes)),
                        success: true);
            }
            UpdateIdentity();
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (roomStatus != null) SetStatus(roomStatus, FormatFailure(exception), error: true);
        }
        finally
        {
            roomPollInProgress = false;
        }
    }

    private void AddRoomMessages(IEnumerable<OnlineRoomChatMessageDto> messages)
    {
        var known = roomMessages.Select(item => item.Id).ToHashSet();
        foreach (var message in messages.OrderBy(item => item.Id))
        {
            if (!known.Add(message.Id)) continue;
            roomMessages.Add(message);
            latestRoomMessageId = Math.Max(latestRoomMessageId, message.Id);
        }
        if (roomMessages.Count > 500) roomMessages.RemoveRange(0, roomMessages.Count - 500);
    }

    private void RefreshActiveRoomLists(OnlineRoomLiveStateDto? state = null)
    {
        if (state != null)
        {
            // The poll repeats every 3 s. Rebuilding unchanged member rows each
            // time recreated every row (and its avatar) and dropped hover and
            // selection; the owner-only "管理" button also depends on our role.
            if (roomMemberList != null &&
                !(roomMemberList.ItemsSource is IReadOnlyList<OnlineRoomMemberDto> shownMembers &&
                  string.Equals(roomMemberListRole, session.CurrentMemberRole, StringComparison.Ordinal) &&
                  SameRoomMembers(shownMembers, state.Members)))
            {
                roomMemberList.ItemsSource = state.Members;
                roomMemberListRole = session.CurrentMemberRole;
            }
            UpdateActiveRoomHeader(state.Room, state.Room.MemberCount);
        }
        if (roomChatList != null) SynchronizeRoomChatList(roomChatList);
    }

    /// <summary>
    /// Mirrors roomMessages into the chat list incrementally. Resetting the whole
    /// list on every poll threw away the reader's scroll position and yanked
    /// them back to the bottom every 3 s. New messages are appended, trimmed
    /// ones removed, and the list follows the tail only if the reader was
    /// already there.
    /// </summary>
    private void SynchronizeRoomChatList(ListBox list)
    {
        if (roomChatItems == null || !ReferenceEquals(list.ItemsSource, roomChatItems))
        {
            roomChatItems = new ObservableCollection<OnlineRoomChatMessageDto>(roomMessages);
            list.ItemsSource = roomChatItems;
            if (roomChatItems.Count > 0) list.ScrollIntoView(roomChatItems[^1]);
            return;
        }

        var scroll = list.FindDescendantOfType<ScrollViewer>();
        var followTail = scroll == null ||
                         scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 4;
        var retained = roomMessages.Select(message => message.Id).ToHashSet();
        for (var index = roomChatItems.Count - 1; index >= 0; index--)
            if (!retained.Contains(roomChatItems[index].Id)) roomChatItems.RemoveAt(index);
        var shown = roomChatItems.Select(message => message.Id).ToHashSet();
        var appended = false;
        foreach (var message in roomMessages)
        {
            if (!shown.Add(message.Id)) continue;
            roomChatItems.Add(message);
            appended = true;
        }
        if (appended && followTail) list.ScrollIntoView(roomChatItems[^1]);
    }

    private static bool SameRoomMembers(
        IReadOnlyList<OnlineRoomMemberDto> current,
        IReadOnlyList<OnlineRoomMemberDto> next)
    {
        if (current.Count != next.Count) return false;
        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = next[index];
            // Tags is a list, which record equality compares by reference.
            if (left with { Tags = null } != right with { Tags = null }) return false;
            if (!(left.Tags ?? []).SequenceEqual(right.Tags ?? [])) return false;
        }
        return true;
    }

    internal static string FormatActiveRoomSubtitle(
        OnlineRoomSummaryDto room,
        int? authoritativeMemberCount = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        var memberCount = Math.Max(0, authoritativeMemberCount ?? room.MemberCount);
        return AvaloniaLocalizationCatalog.Format("{0}/{1} 人 · 房主 {2} · {3}{4}", memberCount, Math.Max(1, room.MaximumMembers), room.OwnerDisplayName, (room.IsPrivate ? AvaloniaLocalizationCatalog.Text("密码房") : AvaloniaLocalizationCatalog.Text("公开房")), (room.IsHidden ? AvaloniaLocalizationCatalog.Text(" · 隐藏") : string.Empty));
    }

    internal static string CreateActiveRoomAutomationStatus(
        OnlineRoomSummaryDto room,
        int? authoritativeMemberCount = null)
    {
        ArgumentNullException.ThrowIfNull(room);
        var memberCount = Math.Max(0, authoritativeMemberCount ?? room.MemberCount);
        return $"RoomNumber={room.RoomNumber}; MemberCount={memberCount}; " +
               $"MaximumMembers={Math.Max(1, room.MaximumMembers)}; " +
               $"RoomId={room.Id:D}";
    }

    private void UpdateActiveRoomHeader(
        OnlineRoomSummaryDto room,
        int? authoritativeMemberCount = null)
    {
        if (roomHeaderSummary == null) return;
        roomHeaderSummary.Text = FormatActiveRoomSubtitle(room, authoritativeMemberCount);
        AutomationProperties.SetHelpText(
            roomHeaderSummary,
            CreateActiveRoomAutomationStatus(room, authoritativeMemberCount));
    }

    private void HandleLostRoom(OnlineRoomLifecycleOutcome outcome, Guid lostRoomId)
    {
        roomMessages.Clear();
        latestRoomMessageId = 0;
        var message = outcome == OnlineRoomLifecycleOutcome.Forbidden
            ? "你已被移出房间；已打开的谱面标签仍可继续本地编辑。"
            : "房间已关闭；已打开的谱面标签仍可继续本地编辑。";
        // Only the lost room's own page is replaced by the lobby. Any other page
        // (a profile being edited, settings, a directory) keeps its content and
        // the loss is reported in the global status instead.
        if (!showingRoomWorkspace || activeRoomViewId != lostRoomId)
        {
            UpdateIdentity();
            SetStatus(globalStatus, message, error: true);
            return;
        }
        ShowRoomLobby();
        if (roomDirectoryStatus != null)
            SetStatus(roomDirectoryStatus, message, error: true);
    }

    private static string BoolMark(bool value) => value ? "可编辑" : "只读";

    private void ShowDirectory(string mode)
    {
        showingNotifications = false;
        showingRoomWorkspace = false;
        directoryMode = mode;
        directoryPage = 1;
        if (!mode.Equals("search", StringComparison.Ordinal)) directoryQuery = string.Empty;

        var title = DirectoryTitle(mode);
        var surface = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            MaxWidth = 900,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        surface.Children.Add(SectionTitle(title, DirectoryDescription(mode)));
        var search = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 16, 0, 12)
        };
        searchBox = TextField(
            WindowsOnlineSurfaceParityPolicy.PlayerSearchMaximumLength,
            "OnlineCommunity.Directory.SearchText");
        searchBox.Text = directoryQuery;
        searchBox.PlaceholderText = "搜索玩家";
        var searchButton = PrimaryButton("搜索", "OnlineCommunity.Directory.Search");
        searchButton.Margin = new Thickness(8, 0, 0, 0);
        searchButton.Click += (_, _) =>
        {
            var query = searchBox.Text?.Trim() ?? string.Empty;
            if (query.Length == 0)
            {
                ShowDirectory("players");
                return;
            }
            directoryQuery = query;
            directoryMode = "search";
            _ = LoadDirectoryAsync(1);
        };
        Grid.SetColumn(searchButton, 1);
        search.Children.Add(searchBox);
        search.Children.Add(searchButton);
        Grid.SetRow(search, 1);
        surface.Children.Add(search);

        directoryList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<OnlinePlayerProfileDto>(
                (player, _) => BuildPlayerRow(player),
                supportsRecycling: false)
        };
        ApplyPlayerDirectoryListStyles(directoryList);
        AutomationProperties.SetAutomationId(directoryList, "OnlineCommunity.Directory.List");
        Grid.SetRow(directoryList, 2);
        surface.Children.Add(directoryList);

        directoryStatus = StatusText("OnlineCommunity.Directory.Status");
        previousPageButton = ActionButton("上一页", "OnlineCommunity.Directory.Previous");
        previousPageButton.Click += (_, _) => _ = LoadDirectoryAsync(directoryPage - 1);
        nextPageButton = ActionButton("下一页", "OnlineCommunity.Directory.Next");
        nextPageButton.Click += (_, _) => _ = LoadDirectoryAsync(directoryPage + 1);
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(previousPageButton);
        directoryStatus.HorizontalAlignment = HorizontalAlignment.Center;
        directoryStatus.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(directoryStatus, 1);
        footer.Children.Add(directoryStatus);
        Grid.SetColumn(nextPageButton, 2);
        footer.Children.Add(nextPageButton);
        Grid.SetRow(footer, 3);
        surface.Children.Add(footer);
        SetSection(surface);
        _ = LoadDirectoryAsync(1);
    }

    private async Task LoadDirectoryAsync(int page)
    {
        if (directoryList == null || directoryStatus == null) return;
        var list = directoryList;
        var status = directoryStatus;
        var previous = previousPageButton;
        var next = nextPageButton;
        var mode = directoryMode;
        var query = directoryQuery;
        page = Math.Max(1, page);
        CancelDirectoryLoad();
        var requestVersion = directoryRequestVersion;
        using var timeout = CreateTimeout(TimeSpan.FromSeconds(20));
        directoryLoadCancellation = timeout;
        SetStatus(status, "正在载入玩家目录…");
        SetPagingButtons(previous, next, directoryPage, directoryTotalPages, false);
        try
        {
            OnlinePlayerDirectoryDto directory = mode switch
            {
                "search" => await session.SearchPlayersAsync(query, page, timeout.Token),
                "leaderboard" => await session.GetLeaderboardAsync(timeout.Token),
                "friends" => await session.GetFriendsAsync(timeout.Token),
                "following" => await session.GetFollowingAsync(page, timeout.Token),
                "followers" => await session.GetFollowersAsync(page, timeout.Token),
                _ => await session.GetPlayersAsync(page, timeout.Token)
            };
            if (!IsCurrentDirectoryRequest(requestVersion, timeout, list, status)) return;
            directoryPage = Math.Max(1, directory.Page);
            directoryTotalPages = Math.Max(
                1,
                (int)Math.Ceiling(directory.TotalCount / (double)Math.Max(1, directory.PageSize)));
            list.ItemsSource = directory.Players;
            var countDetail = mode is "leaderboard" or "friends"
                ? AvaloniaLocalizationCatalog.Format("{0} 名玩家", directory.Players.Count)
                : AvaloniaLocalizationCatalog.Format("第 {0}/{1} 页 · 共 {2} 名", directoryPage, directoryTotalPages, directory.TotalCount);
            SetStatus(status, AvaloniaLocalizationCatalog.Format("在线 {0} · {1}", directory.OnlineCount, countDetail), success: true);
        }
        catch (OperationCanceledException) when (
            requestVersion != directoryRequestVersion ||
            lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!IsCurrentDirectoryRequest(requestVersion, timeout, list, status)) return;
            list.ItemsSource = Array.Empty<OnlinePlayerProfileDto>();
            SetStatus(status, FormatFailure(exception), error: true);
        }
        finally
        {
            if (ReferenceEquals(directoryLoadCancellation, timeout))
                directoryLoadCancellation = null;
            if (requestVersion == directoryRequestVersion &&
                ReferenceEquals(directoryList, list))
            {
                SetPagingButtons(previous, next, directoryPage, directoryTotalPages, true);
            }
        }
    }

    private Control BuildPlayerRow(OnlinePlayerProfileDto? player)
    {
        if (player == null) return RecycledTemplatePlaceholder();
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 0, 0, 6),
            Background = RaisedBrush,
            MinHeight = 72
        };
        var avatar = BuildAvatar(player.AvatarKey, player.DisplayName, 48);
        avatar.Margin = new Thickness(12);
        row.Children.Add(avatar);
        var playerName = BuildPlayerNameText(player);
        var labels = new StackPanel
        {
            Margin = new Thickness(4, 11),
            Spacing = 3,
            Children =
            {
                playerName,
                new TextBlock
                {
                    Text = AvaloniaLocalizationCatalog.Format("{0} · 制谱 {1} · 关注 {2} / 粉丝 {3}{4}", (player.IsOnline ? AvaloniaLocalizationCatalog.Text("在线") : AvaloniaLocalizationCatalog.Text("离线")), FormatDuration(player.ChartingSeconds), player.FollowingCount, player.FollowerCount, (player.IsMutual ? AvaloniaLocalizationCatalog.Text(" · 好友") : player.IsFollowing ? AvaloniaLocalizationCatalog.Text(" · 已关注") : string.Empty)),
                    Foreground = player.IsOnline ? SuccessBrush : MutedBrush,
                    FontSize = 11
                }
            }
        };
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        var details = ActionButton("查看资料", $"OnlineCommunity.Player.{player.Id}.Open");
        details.Margin = new Thickness(10, 16, 12, 16);
        details.Click += (_, _) => _ = ShowPlayerAsync(player.Id);
        Grid.SetColumn(details, 2);
        row.Children.Add(details);
        return row;
    }

    internal static TextBlock BuildPlayerNameText(OnlinePlayerProfileDto player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var selectedNameColor = player.Tags?.FirstOrDefault(tag => tag.IsNameColor);
        var name = new TextBlock
        {
            Text = player.DisplayName,
            Foreground = ResolvePlayerNameForeground(player.Tags),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold
        };
        AutomationProperties.SetAutomationId(
            name,
            $"OnlineCommunity.Player.{player.Id}.Name");
        AutomationProperties.SetHelpText(
            name,
            selectedNameColor == null
                ? "NameColor=default; Source=no-selected-tag"
                : $"NameColor={selectedNameColor.Color}; Source=selected-tag; Tag={selectedNameColor.Name}");
        return name;
    }

    internal static IBrush ResolvePlayerNameForeground(
        IReadOnlyList<OnlineUserTagDto>? tags)
    {
        var selected = tags?.FirstOrDefault(tag => tag.IsNameColor);
        if (selected == null) return Brushes.WhiteSmoke;
        try
        {
            return new SolidColorBrush(Color.Parse(selected.Color));
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException)
        {
            return Brushes.Gray;
        }
    }

    private static void ApplyPlayerDirectoryListStyles(ListBox list)
    {
        var itemTheme = new ControlTheme(typeof(ListBoxItem));
        itemTheme.Setters.Add(new Setter(
            TemplatedControl.TemplateProperty,
            new FuncControlTemplate<ListBoxItem>((_, _) =>
            {
                var presenter = new ContentPresenter
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                presenter.Bind(
                    ContentPresenter.ContentProperty,
                    new TemplateBinding(ContentControl.ContentProperty));
                presenter.Bind(
                    ContentPresenter.ContentTemplateProperty,
                    new TemplateBinding(ContentControl.ContentTemplateProperty));
                presenter.Bind(
                    ContentPresenter.PaddingProperty,
                    new TemplateBinding(ContentControl.PaddingProperty));
                var border = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Child = presenter
                };
                border.Bind(
                    Border.BackgroundProperty,
                    new TemplateBinding(TemplatedControl.BackgroundProperty));
                border.Bind(
                    Border.BorderBrushProperty,
                    new TemplateBinding(TemplatedControl.BorderBrushProperty));
                border.Bind(
                    Border.BorderThicknessProperty,
                    new TemplateBinding(TemplatedControl.BorderThicknessProperty));
                return border;
            })));
        list.ItemContainerTheme = itemTheme;

        var item = new Style(selector => selector.OfType<ListBoxItem>());
        item.Setters.Add(new Setter(
            TemplatedControl.BackgroundProperty,
            Brushes.Transparent));
        item.Setters.Add(new Setter(
            TemplatedControl.BorderBrushProperty,
            Brushes.Transparent));
        item.Setters.Add(new Setter(
            TemplatedControl.BorderThicknessProperty,
            new Thickness(1)));
        item.Setters.Add(new Setter(
            ContentControl.PaddingProperty,
            new Thickness(0)));
        item.Setters.Add(new Setter(
            ContentControl.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        list.Styles.Add(item);

        var hover = new Style(selector =>
            selector.OfType<ListBoxItem>().Class(":pointerover"));
        hover.Setters.Add(new Setter(
            TemplatedControl.BackgroundProperty,
            new SolidColorBrush(Color.Parse("#20272A"))));
        list.Styles.Add(hover);

        var selected = new Style(selector =>
            selector.OfType<ListBoxItem>().Class(":selected"));
        selected.Setters.Add(new Setter(
            TemplatedControl.BackgroundProperty,
            new SolidColorBrush(Color.Parse("#20282B"))));
        selected.Setters.Add(new Setter(
            TemplatedControl.BorderBrushProperty,
            new SolidColorBrush(Color.Parse("#3F5055"))));
        selected.Setters.Add(new Setter(
            TemplatedControl.BorderThicknessProperty,
            new Thickness(1)));
        list.Styles.Add(selected);
    }

    private async Task ShowPlayerAsync(long userId)
    {
        if (session.CurrentUser?.Id == userId)
        {
            ShowProfile();
            return;
        }
        var loading = StatusText("OnlineCommunity.Player.Loading");
        SetStatus(loading, "正在载入玩家资料…");
        var back = ActionButton("返回玩家列表", "OnlineCommunity.Player.Loading.Back");
        back.Click += (_, _) => ShowDirectory(directoryMode);
        SetSection(new StackPanel
        {
            Spacing = 12,
            Children = { loading, back }
        });
        var requestVersion = playerProfileRequestVersion;
        using var timeout = CreateTimeout(TimeSpan.FromSeconds(20));
        playerProfileLoadCancellation = timeout;
        try
        {
            var player = await session.GetPlayerAsync(userId, cancellationToken: timeout.Token);
            if (!IsCurrentPlayerProfileRequest(requestVersion, timeout)) return;
            BuildPlayerProfile(player);
        }
        catch (OperationCanceledException) when (
            requestVersion != playerProfileRequestVersion ||
            lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!IsCurrentPlayerProfileRequest(requestVersion, timeout)) return;
            SetStatus(loading, FormatFailure(exception), error: true);
        }
        finally
        {
            if (ReferenceEquals(playerProfileLoadCancellation, timeout))
                playerProfileLoadCancellation = null;
        }
    }

    private void BuildPlayerProfile(OnlinePlayerProfileDto player)
    {
        var status = StatusText("OnlineCommunity.Player.Status");
        var follow = PrimaryButton(
            player.IsFollowing ? "取消关注" : "关注",
            "OnlineCommunity.Player.Follow");
        follow.Click += async (_, _) =>
        {
            if (actionInProgress) return;
            var viewVersion = sectionVersion;
            await RunActionAsync(
                status,
                player.IsFollowing ? "正在取消关注…" : "正在关注…",
                async token =>
                {
                    await session.SetFollowAsync(player.Id, !player.IsFollowing, token);
                    var refreshed = await session.GetPlayerAsync(player.Id, cancellationToken: token);
                    if (viewVersion == sectionVersion) BuildPlayerProfile(refreshed);
                });
        };
        var back = ActionButton("返回玩家列表", "OnlineCommunity.Player.Back");
        back.Click += (_, _) => ShowDirectory(directoryMode);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Children.Add(BuildAvatar(player.AvatarKey, player.DisplayName, 112));
        var identity = new StackPanel
        {
            Margin = new Thickness(22, 3, 0, 0),
            Spacing = 7,
            Children =
            {
                new TextBlock
                {
                    Text = player.DisplayName,
                    Foreground = Brushes.White,
                    FontSize = 30,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = $"{(player.IsOnline ? "当前在线" : "当前离线")} · " +
                           $"{(player.IsMutual ? "互相关注的好友" : player.IsFollowing ? "已关注" : "未关注")}",
                    Foreground = player.IsOnline ? SuccessBrush : MutedBrush
                },
                new TextBlock
                {
                    Text = AvaloniaLocalizationCatalog.Format("制谱 {0}　关注 {1}　粉丝 {2}", FormatDuration(player.ChartingSeconds), player.FollowingCount, player.FollowerCount),
                    Foreground = AccentBrush
                },
                follow
            }
        };
        if (session.CanUseAdminApi)
        {
            var manage = ActionButton("管理用户", "OnlineCommunity.Player.Admin");
            manage.Click += (_, _) => ShowAdminUser(player.Id);
            identity.Children.Add(manage);
        }
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);
        var panel = new StackPanel
        {
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 12,
            Children =
            {
                SectionTitle("玩家资料", "公开资料遵循该玩家的头像和显示名隐私设置。"),
                Card(header, new Thickness(20)),
                Label("个人简介"),
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(player.Bio) ? "这个玩家还没有填写个人简介。" : player.Bio,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 70
                },
                BuildTags(player.Tags),
                status,
                back
            }
        };
        SetSection(new ScrollViewer { Content = panel });
    }

    private void ShowAdminConfiguration()
    {
        showingNotifications = false;
        showingRoomWorkspace = false;
        if (!session.CanUseAdminApi)
        {
            ShowAdminDenied();
            return;
        }

        var status = StatusText("OnlineCommunity.Admin.Configuration.Status");
        var about = TextField(4000, "OnlineCommunity.Admin.Configuration.About");
        about.AcceptsReturn = true;
        about.TextWrapping = TextWrapping.Wrap;
        about.Height = 120;
        var latestVersion = TextField(32, "OnlineCommunity.Admin.Configuration.LatestVersion");
        var minimumVersion = TextField(32, "OnlineCommunity.Admin.Configuration.MinimumVersion");
        var updateNotice = TextField(4000, "OnlineCommunity.Admin.Configuration.UpdateNotice");
        updateNotice.AcceptsReturn = true;
        updateNotice.TextWrapping = TextWrapping.Wrap;
        updateNotice.Height = 140;
        var downloadUrl = TextField(1024, "OnlineCommunity.Admin.Configuration.DownloadUrl");
        var betaLatestVersion = TextField(
            32,
            "OnlineCommunity.Admin.Configuration.BetaLatestVersion");
        var betaMinimumVersion = TextField(
            32,
            "OnlineCommunity.Admin.Configuration.BetaMinimumVersion");
        var betaUpdateNotice = TextField(
            4000,
            "OnlineCommunity.Admin.Configuration.BetaUpdateNotice");
        betaUpdateNotice.AcceptsReturn = true;
        betaUpdateNotice.TextWrapping = TextWrapping.Wrap;
        betaUpdateNotice.Height = 140;
        var betaDownloadUrl = TextField(
            1024,
            "OnlineCommunity.Admin.Configuration.BetaDownloadUrl");

        // Per-platform overrides. Each editor is three optional fields; a blank
        // field inherits the channel value above, so a platform released in
        // lock-step with the others is simply left empty. Windows, Android and
        // iOS are versioned independently, and their installers are different
        // files, so the update target, login gate and download link are set here.
        (StackPanel Panel,
         Func<OnlinePlatformVersionOverrideDto> Read,
         Action<OnlinePlatformVersionOverrideDto?> Write) PlatformOverrideEditor(
            string platformLabel,
            string idPrefix)
        {
            var latest = TextField(32, idPrefix + ".Latest");
            var minimum = TextField(32, idPrefix + ".Minimum");
            var download = TextField(1024, idPrefix + ".Download");
            var pair = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 10
            };
            pair.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children = { Label("最新版本"), latest }
            });
            var minimumPanel = new StackPanel
            {
                Spacing = 6,
                Children = { Label("最低登录版本"), minimum }
            };
            Grid.SetColumn(minimumPanel, 1);
            pair.Children.Add(minimumPanel);
            var panel = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = platformLabel,
                        Foreground = MutedBrush,
                        FontWeight = FontWeight.SemiBold
                    },
                    pair,
                    Label("下载地址"),
                    download
                }
            };
            return (
                panel,
                () => new OnlinePlatformVersionOverrideDto(
                    (latest.Text ?? string.Empty).Trim(),
                    (minimum.Text ?? string.Empty).Trim(),
                    (download.Text ?? string.Empty).Trim()),
                value =>
                {
                    latest.Text = value?.LatestVersion ?? string.Empty;
                    minimum.Text = value?.MinimumLoginVersion ?? string.Empty;
                    download.Text = value?.DownloadUrl ?? string.Empty;
                });
        }

        var stableWindows = PlatformOverrideEditor(
            "Windows", "OnlineCommunity.Admin.Configuration.StableWindows");
        var stableAndroid = PlatformOverrideEditor(
            "Android", "OnlineCommunity.Admin.Configuration.StableAndroid");
        var stableIos = PlatformOverrideEditor(
            "iOS", "OnlineCommunity.Admin.Configuration.StableIos");
        var betaWindows = PlatformOverrideEditor(
            "Windows", "OnlineCommunity.Admin.Configuration.BetaWindows");
        var betaAndroid = PlatformOverrideEditor(
            "Android", "OnlineCommunity.Admin.Configuration.BetaAndroid");
        var betaIos = PlatformOverrideEditor(
            "iOS", "OnlineCommunity.Admin.Configuration.BetaIos");

        StackPanel PlatformOverrideSection(
            params StackPanel[] editors)
        {
            var section = new StackPanel { Spacing = 10 };
            section.Children.Add(new TextBlock
            {
                Text = "分平台覆盖（留空＝沿用上面的通道值）",
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap
            });
            foreach (var editor in editors) section.Children.Add(editor);
            return section;
        }

        var save = PrimaryButton(
            "保存正式版与 Beta 配置",
            "OnlineCommunity.Admin.Configuration.Save");
        save.IsEnabled = false;
        save.Click += async (_, _) =>
        {
            if (!save.IsEnabled || actionInProgress) return;
            await RunActionAsync(
                status,
                "正在保存应用配置…",
                async token =>
                {
                    var updated = await session.UpdateAppConfigurationAsAdminAsync(
                        new OnlineUpdateAppConfigurationRequestDto(
                            about.Text,
                             latestVersion.Text,
                             minimumVersion.Text,
                             updateNotice.Text,
                             downloadUrl.Text,
                             betaLatestVersion.Text,
                             betaMinimumVersion.Text,
                             betaUpdateNotice.Text,
                             betaDownloadUrl.Text,
                             new OnlinePlatformVersionTracksDto(
                                 stableWindows.Read(),
                                 stableAndroid.Read(),
                                 stableIos.Read()),
                             new OnlinePlatformVersionTracksDto(
                                 betaWindows.Read(),
                                 betaAndroid.Read(),
                                 betaIos.Read())),
                         token);
                    about.Text = updated.AboutContent;
                    latestVersion.Text = updated.LatestVersion;
                    minimumVersion.Text = updated.MinimumLoginVersion;
                    updateNotice.Text = updated.UpdateNotice;
                    downloadUrl.Text = updated.DownloadUrl;
                    betaLatestVersion.Text = updated.BetaLatestVersion;
                    betaMinimumVersion.Text = updated.BetaMinimumLoginVersion;
                    betaUpdateNotice.Text = updated.BetaUpdateNotice;
                    betaDownloadUrl.Text = updated.BetaDownloadUrl;
                    stableWindows.Write(updated.StablePlatformTracks?.Windows);
                    stableAndroid.Write(updated.StablePlatformTracks?.Android);
                    stableIos.Write(updated.StablePlatformTracks?.Ios);
                    betaWindows.Write(updated.BetaPlatformTracks?.Windows);
                    betaAndroid.Write(updated.BetaPlatformTracks?.Android);
                    betaIos.Write(updated.BetaPlatformTracks?.Ios);
                    SetStatus(status, "应用配置已保存。", success: true);
                });
        };
        var viewIssues = ActionButton(
            "查看测试反馈",
            "OnlineCommunity.Admin.Issues.Open");
        viewIssues.Click += (_, _) => ShowAdminIssues();

        var versions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 10
        };
        var latestPanel = new StackPanel
        {
            Spacing = 6,
            Children = { Label("正式版最新版本"), latestVersion }
        };
        versions.Children.Add(latestPanel);
        var minimumPanel = new StackPanel
        {
            Spacing = 6,
            Children = { Label("正式版最低登录版本"), minimumVersion }
        };
        Grid.SetColumn(minimumPanel, 1);
        versions.Children.Add(minimumPanel);
        var betaVersions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 10
        };
        var betaLatestPanel = new StackPanel
        {
            Spacing = 6,
            Children = { Label("Beta 最新版本"), betaLatestVersion }
        };
        betaVersions.Children.Add(betaLatestPanel);
        var betaMinimumPanel = new StackPanel
        {
            Spacing = 6,
            Children = { Label("Beta 最低登录版本"), betaMinimumVersion }
        };
        Grid.SetColumn(betaMinimumPanel, 1);
        betaVersions.Children.Add(betaMinimumPanel);
        var content = new StackPanel
        {
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 9,
            Children =
            {
                SectionTitle(
                    "内容后台",
                    "正式版和 Beta 通道分别管理版本提示与下载地址；仅 owner 可保存。"),
                viewIssues,
                Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Label("关于内容"),
                        about,
                        versions,
                        Label("正式版更新提示"),
                        updateNotice,
                        Label("正式版下载地址"),
                        downloadUrl,
                        PlatformOverrideSection(
                            stableWindows.Panel,
                            stableAndroid.Panel,
                            stableIos.Panel),
                        new TextBlock
                        {
                            Text = "BETA 通道",
                            Foreground = AccentBrush,
                            FontSize = 12,
                            FontWeight = FontWeight.SemiBold,
                            Margin = new Thickness(0, 8, 0, 0)
                        },
                        new TextBlock
                        {
                            Text = "仅携带 X-LumaChart-Client-Channel: beta 的客户端会收到以下内容。",
                            Foreground = MutedBrush,
                            TextWrapping = TextWrapping.Wrap
                        },
                        betaVersions,
                        Label("Beta 更新提示"),
                        betaUpdateNotice,
                        Label("Beta 下载地址"),
                        betaDownloadUrl,
                        PlatformOverrideSection(
                            betaWindows.Panel,
                            betaAndroid.Panel,
                            betaIos.Panel),
                        save,
                        status
                    }
                }, new Thickness(18))
            }
        };
        SetSection(new ScrollViewer
        {
            VerticalScrollBarVisibility =
                global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = content
        });
        var viewVersion = sectionVersion;
        _ = RunSectionLoadAsync(
            viewVersion,
            status,
            "正在读取应用配置…",
            token => session.GetAppConfigurationAsAdminAsync(token),
            configuration =>
            {
                about.Text = configuration.AboutContent;
                latestVersion.Text = configuration.LatestVersion;
                minimumVersion.Text = configuration.MinimumLoginVersion;
                updateNotice.Text = configuration.UpdateNotice;
                downloadUrl.Text = configuration.DownloadUrl;
                betaLatestVersion.Text = configuration.BetaLatestVersion;
                betaMinimumVersion.Text = configuration.BetaMinimumLoginVersion;
                betaUpdateNotice.Text = configuration.BetaUpdateNotice;
                betaDownloadUrl.Text = configuration.BetaDownloadUrl;
                stableWindows.Write(configuration.StablePlatformTracks?.Windows);
                stableAndroid.Write(configuration.StablePlatformTracks?.Android);
                stableIos.Write(configuration.StablePlatformTracks?.Ios);
                betaWindows.Write(configuration.BetaPlatformTracks?.Windows);
                betaAndroid.Write(configuration.BetaPlatformTracks?.Android);
                betaIos.Write(configuration.BetaPlatformTracks?.Ios);
                save.IsEnabled = true;
                SetStatus(status, "应用配置已载入。", success: true);
            });
    }

    private void ShowAdminIssues(int page = 1, string? statusFilter = null)
    {
        showingNotifications = false;
        showingRoomWorkspace = false;
        if (!session.CanUseAdminApi)
        {
            ShowAdminDenied();
            return;
        }

        page = Math.Max(1, page);
        var status = StatusText("OnlineCommunity.Admin.Issues.Status");
        var filter = new ComboBox
        {
            Height = 38,
            MinWidth = 150,
            Background = RaisedBrush,
            Foreground = Brushes.White,
            ItemsSource = new[] { "全部状态", "待处理", "已确认", "已修复", "已关闭" },
            SelectedIndex = IssueStatusFilterIndex(statusFilter)
        };
        AutomationProperties.SetAutomationId(filter, "OnlineCommunity.Admin.Issues.Filter");
        var refresh = ActionButton("刷新", "OnlineCommunity.Admin.Issues.Refresh");
        refresh.Click += (_, _) => ShowAdminIssues(1, IssueStatusFilterCode(filter.SelectedIndex));
        var back = ActionButton("返回内容后台", "OnlineCommunity.Admin.Issues.Back");
        back.Click += (_, _) => ShowAdminConfiguration();

        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 8
        };
        toolbar.Children.Add(filter);
        Grid.SetColumn(refresh, 1);
        toolbar.Children.Add(refresh);
        Grid.SetColumn(back, 3);
        toolbar.Children.Add(back);

        var issueList = new StackPanel { Spacing = 8 };
        var previous = ActionButton("上一页", "OnlineCommunity.Admin.Issues.Previous");
        var next = ActionButton("下一页", "OnlineCommunity.Admin.Issues.Next");
        previous.IsEnabled = false;
        next.IsEnabled = false;
        var pageText = new TextBlock
        {
            Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(pageText, "OnlineCommunity.Admin.Issues.Page");
        var paging = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 10
        };
        paging.Children.Add(previous);
        Grid.SetColumn(pageText, 1);
        paging.Children.Add(pageText);
        Grid.SetColumn(next, 2);
        paging.Children.Add(next);

        var content = new StackPanel
        {
            MaxWidth = 920,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 10,
            Children =
            {
                SectionTitle("测试反馈", "查看 Avalonia 测试人员提交的问题，并更新处理状态。"),
                toolbar,
                status,
                issueList,
                paging
            }
        };
        SetSection(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        });

        var viewVersion = sectionVersion;
        _ = RunSectionLoadAsync(
            viewVersion,
            status,
            "正在读取测试反馈…",
            token => session.GetIssueReportsAsAdminAsync(page, statusFilter, token),
            result =>
            {
                var totalPages = Math.Max(1, (int)Math.Ceiling(
                    result.TotalCount / (double)Math.Max(1, result.PageSize)));
                pageText.Text = AvaloniaLocalizationCatalog.Format("第 {0}/{1} 页 · 共 {2:N0} 条", result.Page, totalPages, result.TotalCount);
                previous.IsEnabled = result.Page > 1;
                next.IsEnabled = result.Page < totalPages;
                previous.Click += (_, _) => ShowAdminIssues(result.Page - 1, statusFilter);
                next.Click += (_, _) => ShowAdminIssues(result.Page + 1, statusFilter);
                issueList.Children.Clear();
                if (result.Issues.Count == 0)
                {
                    issueList.Children.Add(Card(new TextBlock
                    {
                        Text = "当前筛选条件下没有测试反馈。",
                        Foreground = MutedBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    }, new Thickness(18)));
                }
                foreach (var issue in result.Issues)
                {
                    var open = ActionButton("查看详情", $"OnlineCommunity.Admin.Issues.Open.{issue.Id:D}");
                    open.Click += (_, _) => ShowAdminIssue(issue, result.Page, statusFilter);
                    var heading = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        ColumnSpacing = 10
                    };
                    heading.Children.Add(new TextBlock
                    {
                        Text = issue.Title,
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    Grid.SetColumn(open, 1);
                    heading.Children.Add(open);
                    issueList.Children.Add(Card(new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            heading,
                            new TextBlock
                            {
                                Text = $"{IssueCategoryLabel(issue.Category)} · " +
                                       $"{issue.ReporterDisplayName} · " +
                                       $"{issue.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · " +
                                       IssueStatusLabel(issue.Status),
                                Foreground = MutedBrush,
                                TextWrapping = TextWrapping.Wrap
                            },
                            new TextBlock
                            {
                                Text = issue.Description,
                                MaxHeight = 52,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = Brush("#C4CCCF")
                            }
                        }
                    }, new Thickness(14)));
                }
                SetStatus(status, "测试反馈已载入。", success: true);
            });
    }

    private void ShowAdminIssue(
        OnlineIssueReportDto issue,
        int returnPage,
        string? statusFilter)
    {
        showingNotifications = false;
        showingRoomWorkspace = false;
        if (!session.CanUseAdminApi)
        {
            ShowAdminDenied();
            return;
        }

        var actionStatus = StatusText("OnlineCommunity.Admin.Issue.Status");
        var state = new ComboBox
        {
            Height = 38,
            MinWidth = 160,
            Background = RaisedBrush,
            Foreground = Brushes.White,
            ItemsSource = new[] { "待处理", "已确认", "已修复", "已关闭" },
            SelectedIndex = IssueStatusIndex(issue.Status)
        };
        AutomationProperties.SetAutomationId(state, "OnlineCommunity.Admin.Issue.State");
        var note = TextField(2000, "OnlineCommunity.Admin.Issue.ResolutionNote");
        note.Text = issue.ResolutionNote;
        note.AcceptsReturn = true;
        note.TextWrapping = TextWrapping.Wrap;
        note.Height = 100;
        note.PlaceholderText = "处理备注（可选）";
        var save = PrimaryButton("保存处理状态", "OnlineCommunity.Admin.Issue.Save");
        save.Click += async (_, _) =>
        {
            if (actionInProgress) return;
            await RunActionAsync(
                actionStatus,
                "正在保存处理状态…",
                async token =>
                {
                    var updated = await session.UpdateIssueReportAsAdminAsync(
                        issue.Id,
                        new OnlineUpdateIssueReportRequestDto(
                            IssueStatusCode(state.SelectedIndex),
                            note.Text ?? string.Empty),
                        token);
                    ShowAdminIssue(updated, returnPage, statusFilter);
                });
        };
        var delete = ActionButton("删除反馈", "OnlineCommunity.Admin.Issue.Delete");
        delete.Click += async (_, _) =>
        {
            if (actionInProgress) return;
            var confirmation = new SettingsActionDialog(
                "删除测试反馈",
                AvaloniaLocalizationCatalog.Format("确定删除“{0}”吗？删除后测试人员和维护者都无法再查看这条反馈。", issue.Title),
                "删除",
                showCancel: true,
                dangerous: true,
                uiScale: 1,
                fontScale: 1);
            // The community surface is hosted as detached content inside MainWindow;
            // this object is not a visible Window while the admin page is open.
            // Use the visible host so Avalonia can create the modal confirmation.
            if (!await confirmation.ShowDialog<bool>(hostedOwner ?? this)) return;
            await RunActionAsync(
                actionStatus,
                "正在删除测试反馈…",
                async token =>
                {
                    await session.DeleteIssueReportAsAdminAsync(issue.Id, token);
                    ShowAdminIssues(returnPage, statusFilter);
                });
        };
        var back = ActionButton("返回反馈列表", "OnlineCommunity.Admin.Issue.Back");
        back.Click += (_, _) => ShowAdminIssues(returnPage, statusFilter);
        var actions = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 8
        };
        actions.Children.Add(back);
        Grid.SetColumn(save, 1);
        actions.Children.Add(save);
        Grid.SetColumn(delete, 2);
        actions.Children.Add(delete);

        var details = new StackPanel
        {
            Spacing = 9,
            Children =
            {
                IssueDetailBlock("问题描述", issue.Description),
                IssueDetailBlock("复现步骤", issue.ReproductionSteps, "暂时无法稳定复现"),
                IssueDetailBlock("期望结果", issue.ExpectedBehavior, "未填写"),
                IssueDetailBlock("环境信息", issue.Environment, "未附带")
            }
        };
        var content = new StackPanel
        {
            MaxWidth = 920,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 10,
            Children =
            {
                SectionTitle(issue.Title,
                    AvaloniaLocalizationCatalog.Format("{0} · {1} · {2:yyyy-MM-dd HH:mm} · 客户端 {3}", IssueCategoryLabel(issue.Category), issue.ReporterDisplayName, issue.CreatedAt.ToLocalTime(), issue.ClientVersion)),
                Card(details, new Thickness(18)),
                Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Label("处理状态"),
                        state,
                        Label("处理备注"),
                        note,
                        actions,
                        actionStatus
                    }
                }, new Thickness(18))
            }
        };
        SetSection(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        });
    }

    private static Control IssueDetailBlock(string title, string? value, string fallback = "未填写") =>
        new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(value) ? fallback : value,
                    Foreground = Brush("#C4CCCF"),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

    private static int IssueStatusFilterIndex(string? status) => status switch
    {
        "open" => 1,
        "confirmed" => 2,
        "fixed" => 3,
        "closed" => 4,
        _ => 0
    };

    private static string? IssueStatusFilterCode(int selectedIndex) => selectedIndex switch
    {
        1 => "open",
        2 => "confirmed",
        3 => "fixed",
        4 => "closed",
        _ => null
    };

    private static int IssueStatusIndex(string? status) => status switch
    {
        "confirmed" => 1,
        "fixed" => 2,
        "closed" => 3,
        _ => 0
    };

    private static string IssueStatusCode(int selectedIndex) => selectedIndex switch
    {
        1 => "confirmed",
        2 => "fixed",
        3 => "closed",
        _ => "open"
    };

    private static string IssueStatusLabel(string? status) => status switch
    {
        "confirmed" => "已确认",
        "fixed" => "已修复",
        "closed" => "已关闭",
        _ => "待处理"
    };

    private static string IssueCategoryLabel(string? category) => category switch
    {
        "crash" => "崩溃 / 报错",
        "playback" => "播放 / Key 音",
        "editor" => "编辑器 / 谱面",
        "online" => "联机 / 房间",
        "ui" => "界面 / 显示",
        "import-export" => "导入 / 导出",
        _ => "其他"
    };

    private void ShowAdminUser(long userId)
    {
        showingNotifications = false;
        showingRoomWorkspace = false;
        if (!session.CanUseAdminApi)
        {
            ShowAdminDenied();
            return;
        }

        var status = StatusText("OnlineCommunity.Admin.User.LoadStatus");
        SetSection(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            Children =
            {
                SectionTitle("用户管理", AvaloniaLocalizationCatalog.Format("正在读取用户 ID {0}…", userId)),
                status
            }
        });
        var viewVersion = sectionVersion;
        _ = RunSectionLoadAsync(
            viewVersion,
            status,
            "正在读取用户资料…",
            token => session.GetUserAsAdminAsync(userId, token),
            user => BuildAdminUserEditor(user));
    }

    private void BuildAdminUserEditor(
        OnlineUserProfileDto user,
        string? successMessage = null)
    {
        if (!session.CanUseAdminApi)
        {
            ShowAdminDenied();
            return;
        }

        var status = StatusText("OnlineCommunity.Admin.User.Status");
        if (!string.IsNullOrWhiteSpace(successMessage))
            SetStatus(status, successMessage, success: true);
        var username = TextField(24, "OnlineCommunity.Admin.User.Username");
        username.Text = user.Username;
        var displayName = TextField(32, "OnlineCommunity.Admin.User.DisplayName");
        displayName.Text = user.DisplayName;
        var bio = TextField(160, "OnlineCommunity.Admin.User.Bio");
        bio.Text = user.Bio;
        bio.AcceptsReturn = true;
        bio.TextWrapping = TextWrapping.Wrap;
        bio.Height = 86;
        var customLabel = TextField(120, "OnlineCommunity.Admin.User.CustomLabel");
        customLabel.Text = user.CustomLabel;
        var tags = user.Tags?.ToList() ?? [];
        var tagsPanel = new StackPanel { Spacing = 5 };
        var tagName = TextField(20, "OnlineCommunity.Admin.User.TagName");
        var tagColor = TextField(7, "OnlineCommunity.Admin.User.TagColor");
        tagColor.Text = "#6FA8DC";
        var nameColor = new ComboBox
        {
            MinWidth = 230,
            Height = 38,
            Background = RaisedBrush,
            Foreground = Brushes.White
        };
        AutomationProperties.SetAutomationId(
            nameColor,
            "OnlineCommunity.Admin.User.NameColor");

        void RefreshTags(string? selectedNameColor = null)
        {
            selectedNameColor ??= tags.FirstOrDefault(tag => tag.IsNameColor)?.Name;
            tagsPanel.Children.Clear();
            foreach (var current in tags.ToArray())
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto") };
                row.Children.Add(new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(2),
                    Background = TryParseTagBrush(current.Color),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var text = new TextBlock
                {
                    Text = current.Name,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                var remove = ActionButton("删除", $"OnlineCommunity.Admin.User.TagRemove.{current.Name}");
                remove.Click += (_, _) =>
                {
                    tags.Remove(current);
                    RefreshTags(
                        string.Equals(
                            nameColor.SelectedItem as string,
                            current.Name,
                            StringComparison.OrdinalIgnoreCase)
                            ? null
                            : nameColor.SelectedItem as string);
                };
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);
                tagsPanel.Children.Add(row);
            }

            var choices = new[] { "不使用标签名字色" }
                .Concat(tags.Select(tag => tag.Name))
                .ToArray();
            nameColor.ItemsSource = choices;
            nameColor.SelectedItem = choices.FirstOrDefault(choice =>
                string.Equals(choice, selectedNameColor, StringComparison.OrdinalIgnoreCase))
                ?? choices[0];
        }

        var addTag = ActionButton("添加标签", "OnlineCommunity.Admin.User.TagAdd");
        addTag.Click += (_, _) =>
        {
            var name = tagName.Text?.Trim() ?? string.Empty;
            var color = tagColor.Text?.Trim().ToUpperInvariant() ?? string.Empty;
            if (name.Length is < 1 or > 20)
            {
                SetStatus(status, "标签名称须为 1～20 个字符。", error: true);
                return;
            }
            if (!IsTagColor(color))
            {
                SetStatus(status, "标签颜色须为 #RRGGBB。", error: true);
                return;
            }
            var existing = tags.FirstOrDefault(tag =>
                tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing == null && tags.Count >= 8)
            {
                SetStatus(status, "每个用户最多 8 个标签。", error: true);
                return;
            }
            var selected = nameColor.SelectedItem as string;
            if (existing != null) tags.Remove(existing);
            tags.Add(new OnlineUserTagDto(name, color));
            tagName.Text = string.Empty;
            RefreshTags(
                existing != null &&
                string.Equals(selected, existing.Name, StringComparison.OrdinalIgnoreCase)
                    ? name
                    : selected);
            SetStatus(status, "标签草稿已更新；点击保存用户资料后提交。");
        };
        var tagInputs = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,150,Auto"),
            ColumnSpacing = 8
        };
        tagInputs.Children.Add(tagName);
        Grid.SetColumn(tagColor, 1);
        tagInputs.Children.Add(tagColor);
        Grid.SetColumn(addTag, 2);
        tagInputs.Children.Add(addTag);
        RefreshTags();

        var saveProfile = PrimaryButton(
            "保存用户资料",
            "OnlineCommunity.Admin.User.Save");
        saveProfile.Click += async (_, _) =>
        {
            var viewVersion = sectionVersion;
            var nextUsername = username.Text?.Trim() ?? string.Empty;
            var nextDisplayName = displayName.Text?.Trim() ?? string.Empty;
            if (nextUsername.Length is < 1 or > 24 ||
                nextDisplayName.Length is < 1 or > 32 ||
                (bio.Text?.Length ?? 0) > 160)
            {
                SetStatus(status, "用户名、显示名或简介长度不符合要求。", error: true);
                return;
            }
            var selectedNameColor = nameColor.SelectedIndex <= 0
                ? null
                : nameColor.SelectedItem as string;
            var submittedTags = tags.Select(tag => tag with
            {
                IsNameColor = string.Equals(
                    tag.Name,
                    selectedNameColor,
                    StringComparison.OrdinalIgnoreCase)
            }).ToArray();
            await RunActionAsync(
                status,
                "正在保存用户资料…",
                async token =>
                {
                    var updated = await session.UpdateUserAsAdminAsync(
                        user.Id,
                        new OnlineAdminUpdateUserRequestDto(
                            nextUsername,
                            nextDisplayName,
                            bio.Text ?? string.Empty,
                            submittedTags),
                        token);
                    if (viewVersion == sectionVersion)
                        BuildAdminUserEditor(updated, "用户资料与标签已保存。");
                });
        };
        var saveLabel = ActionButton(
            "保存自定义标记",
            "OnlineCommunity.Admin.User.CustomLabelSave");
        saveLabel.Click += async (_, _) =>
            await RunActionAsync(
                status,
                "正在保存自定义标记…",
                async token =>
                {
                    await session.SetUserCustomLabelAsAdminAsync(
                        user.Id,
                        customLabel.Text?.Trim(),
                        token);
                    SetStatus(status, "自定义标记已保存。", success: true);
                });
        var uploadAvatar = ActionButton(
            "上传头像",
            "OnlineCommunity.Admin.User.AvatarUpload");
        uploadAvatar.Click += async (_, _) => await UploadAdminAvatarAsync(user.Id, status);
        var resetAvatar = ActionButton(
            "恢复默认头像",
            "OnlineCommunity.Admin.User.AvatarReset");
        resetAvatar.Click += async (_, _) =>
        {
            var viewVersion = sectionVersion;
            await RunActionAsync(
                status,
                "正在恢复默认头像…",
                async token =>
                {
                    var updated = await session.ResetUserAvatarAsAdminAsync(user.Id, token);
                    if (viewVersion == sectionVersion)
                        BuildAdminUserEditor(updated, "用户头像已恢复为默认。");
                });
        };
        var back = ActionButton("返回玩家资料", "OnlineCommunity.Admin.User.Back");
        back.Click += async (_, _) => await ShowPlayerAsync(user.Id);
        var avatarActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { uploadAvatar, resetAvatar }
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Children.Add(BuildAvatar(user.AvatarKey, user.DisplayName, 96));
        var identity = new StackPanel
        {
            Margin = new Thickness(18, 2, 0, 0),
            Spacing = 5,
            Children =
            {
                new TextBlock
                {
                    Text = user.DisplayName,
                    Foreground = Brushes.White,
                    FontSize = 26,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = $"@{user.Username} · ID {user.Id} · {user.Role}",
                    Foreground = MutedBrush
                },
                avatarActions
            }
        };
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);
        var content = new StackPanel
        {
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 9,
            Children =
            {
                SectionTitle("用户管理", "owner 可编辑用户资料、标签、名字色、标记与头像。"),
                Card(header, new Thickness(18)),
                Label("用户名"),
                username,
                Label("显示名"),
                displayName,
                Label("简介"),
                bio,
                Label("标签"),
                tagsPanel,
                tagInputs,
                Label("名字色"),
                nameColor,
                saveProfile,
                Label("自定义标记"),
                customLabel,
                saveLabel,
                status,
                back
            }
        };
        SetSection(new ScrollViewer
        {
            VerticalScrollBarVisibility =
                global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = content
        });
    }

    private async Task UploadAdminAvatarAsync(long userId, TextBlock status)
    {
        if (actionInProgress) return;
        var viewVersion = sectionVersion;
        var files = await (hostedOwner ?? this).StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "选择用户头像图片",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("头像图片")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"]
                    }
                ]
            });
        // Not TryGetLocalPath: on iOS a picked item is a security-scoped URL to
        // the file where it actually lives, and the path stops resolving as soon
        // as Avalonia closes the scope. Staging a copy is a no-op on Windows,
        // whose pickers already hand back durable paths.
        string? path;
        try
        {
            // Staging a picked document rethrows its I/O failure; this runs
            // from a Click handler, where it would otherwise close the app.
            path = await PlatformPickedFiles.ResolveLocalPathAsync(
                files.FirstOrDefault(),
                PickedFileRetention.Transient);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            SetStatus(status, FormatFailure(exception), error: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(path)) return;
        FileInfo file;
        try
        {
            file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists) throw new FileNotFoundException("找不到头像图片。", file.FullName);
            if (file.Length is <= 0 or > 4L * 1024 * 1024)
                throw new OnlineApiException("头像文件必须介于 1 字节和 4 MB 之间。");
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            SetStatus(status, FormatFailure(exception), error: true);
            return;
        }

        await RunActionAsync(
            status,
            "正在上传用户头像…",
            async token =>
            {
                await using var source = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var updated = await session.UploadUserAvatarAsAdminAsync(
                    userId,
                    source,
                    file.Length,
                    cancellationToken: token);
                if (viewVersion == sectionVersion)
                    BuildAdminUserEditor(updated, "用户头像已更新。");
            });
    }

    private void ShowAdminDenied()
    {
        var back = ActionButton("返回我的主页", "OnlineCommunity.Admin.Denied.Back");
        back.Click += (_, _) => ShowProfile();
        SetSection(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
            Children =
            {
                SectionTitle("无权访问内容后台", "仅 owner 账号可以执行用户与应用配置管理。"),
                back
            }
        });
    }

    private static bool IsTagColor(string color) =>
        color.Length == 7 &&
        color[0] == '#' &&
        color.AsSpan(1).IndexOfAnyExcept(
            "0123456789ABCDEFabcdef".AsSpan()) < 0;

    private static IBrush TryParseTagBrush(string color)
    {
        try
        {
            return IsTagColor(color) ? Brush(color) : MutedBrush;
        }
        catch (FormatException)
        {
            return MutedBrush;
        }
    }

    private void ShowAccountSettings()
    {
        showingNotifications = false;
        showingRoomWorkspace = false;
        var user = session.CurrentUser;
        if (user == null)
        {
            ShowSignedOut();
            return;
        }

        emailCaptchaId = Guid.Empty;
        emailCaptchaBitmap?.Dispose();
        emailCaptchaBitmap = null;
        var emailStatus = StatusText("OnlineCommunity.Settings.Email.Status");
        var currentPassword = PasswordField("OnlineCommunity.Settings.Email.CurrentPassword");
        var newEmail = TextField(254, "OnlineCommunity.Settings.Email.NewEmail");
        var captchaAnswer = TextField(8, "OnlineCommunity.Settings.Email.CaptchaAnswer");
        var emailCode = TextField(6, "OnlineCommunity.Settings.Email.Code");
        var captchaImage = new Image { Width = 180, Height = 56, Stretch = Stretch.None };
        AutomationProperties.SetAutomationId(captchaImage, "OnlineCommunity.Settings.Email.CaptchaImage");
        var refreshCaptcha = ActionButton("刷新验证码", "OnlineCommunity.Settings.Email.CaptchaRefresh");
        refreshCaptcha.Click += async (_, _) =>
            await LoadEmailCaptchaAsync(captchaImage, captchaAnswer, emailStatus);
        var sendCode = ActionButton("发送邮箱验证码", "OnlineCommunity.Settings.Email.SendCode");
        sendCode.Click += async (_, _) =>
        {
            if (actionInProgress) return;
            if (string.IsNullOrWhiteSpace(newEmail.Text) ||
                string.IsNullOrWhiteSpace(captchaAnswer.Text) ||
                emailCaptchaId == Guid.Empty)
            {
                SetStatus(emailStatus, "请填写新邮箱和图片验证码。", error: true);
                return;
            }
            await RunActionAsync(
                emailStatus,
                "正在发送邮箱验证码…",
                async token =>
                {
                    await session.SendEmailVerificationCodeAsync(
                        newEmail.Text.Trim(),
                        "change_email",
                        emailCaptchaId,
                        captchaAnswer.Text.Trim(),
                        token);
                    SetStatus(emailStatus, "验证码已发送，10 分钟内有效。", success: true);
                    _ = RunEmailCodeCooldownAsync(sendCode);
                });
        };
        var changeEmail = PrimaryButton("确认更换邮箱", "OnlineCommunity.Settings.Email.Apply");
        changeEmail.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(currentPassword.Text) ||
                string.IsNullOrWhiteSpace(newEmail.Text) ||
                (emailCode.Text?.Trim().Length ?? 0) != 6)
            {
                SetStatus(emailStatus, "请填写当前密码、新邮箱和 6 位邮箱验证码。", error: true);
                return;
            }
            await RunActionAsync(
                emailStatus,
                "正在更换绑定邮箱…",
                async token =>
                {
                    await session.ChangeEmailAsync(
                        currentPassword.Text,
                        newEmail.Text.Trim(),
                        emailCode.Text!.Trim(),
                        token);
                    currentPassword.Text = string.Empty;
                    emailCode.Text = string.Empty;
                    SetStatus(emailStatus, "绑定邮箱已更新。", success: true);
                });
        };
        var captchaRow = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*,Auto") };
        captchaRow.Children.Add(captchaImage);
        captchaAnswer.Margin = new Thickness(8, 9, 8, 9);
        Grid.SetColumn(captchaAnswer, 1);
        captchaRow.Children.Add(captchaAnswer);
        Grid.SetColumn(refreshCaptcha, 2);
        captchaRow.Children.Add(refreshCaptcha);

        var passwordStatus = StatusText("OnlineCommunity.Settings.Password.Status");
        var oldPassword = PasswordField("OnlineCommunity.Settings.Password.Current");
        var newPassword = PasswordField("OnlineCommunity.Settings.Password.New");
        var confirmPassword = PasswordField("OnlineCommunity.Settings.Password.Confirm");
        var changePassword = PrimaryButton("修改密码", "OnlineCommunity.Settings.Password.Apply");
        changePassword.Click += async (_, _) =>
        {
            if (!string.Equals(newPassword.Text, confirmPassword.Text, StringComparison.Ordinal))
            {
                SetStatus(passwordStatus, "两次输入的新密码不一致。", error: true);
                return;
            }
            if (string.IsNullOrEmpty(oldPassword.Text) || (newPassword.Text?.Length ?? 0) < 10)
            {
                SetStatus(passwordStatus, "请填写当前密码和至少 10 位的新密码。", error: true);
                return;
            }
            await RunActionAsync(
                passwordStatus,
                "正在修改密码…",
                async token =>
                {
                    await session.ChangePasswordAsync(oldPassword.Text, newPassword.Text!, token);
                    oldPassword.Text = string.Empty;
                    newPassword.Text = string.Empty;
                    confirmPassword.Text = string.Empty;
                    SetStatus(passwordStatus, "密码已修改。", success: true);
                });
        };

        var logoutStatus = StatusText("OnlineCommunity.Settings.Logout.Status");
        var logout = ActionButton("退出登录", "OnlineCommunity.Settings.Logout");
        logout.Click += async (_, _) =>
        {
            await RunActionAsync(
                logoutStatus,
                "正在退出登录…",
                async token =>
                {
                    await session.LogoutAsync(token);
                    Complete(false);
                });
        };

        var content = new StackPanel
        {
            MaxWidth = 820,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 9,
            Children =
            {
                SectionTitle("账号设置", "密码不会保存；登录令牌使用系统安全存储绑定到当前设备。"),
                Label(AvaloniaLocalizationCatalog.Format("当前绑定邮箱：{0}", (string.IsNullOrWhiteSpace(user.Email) ? AvaloniaLocalizationCatalog.Text("暂时无法读取") : user.Email))),
                Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Subheading("更换绑定邮箱"),
                        Label("当前密码"),
                        currentPassword,
                        Label("新邮箱"),
                        newEmail,
                        captchaRow,
                        sendCode,
                        Label("6 位邮箱验证码"),
                        emailCode,
                        changeEmail,
                        emailStatus
                    }
                }, new Thickness(18)),
                Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Subheading("修改密码"),
                        Label("当前密码"),
                        oldPassword,
                        Label("新密码（至少 10 位）"),
                        newPassword,
                        Label("再次输入新密码"),
                        confirmPassword,
                        changePassword,
                        passwordStatus
                    }
                }, new Thickness(18)),
                Card(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        Subheading("本地会话"),
                        new TextBlock
                        {
                            Text = "退出后将清除本机安全加密令牌；服务器暂时不可用也不会阻止本地退出。",
                            Foreground = MutedBrush,
                            TextWrapping = TextWrapping.Wrap
                        },
                        logout,
                        logoutStatus
                    }
                }, new Thickness(18))
            }
        };
        SetSection(new ScrollViewer
        {
            VerticalScrollBarVisibility =
                global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = content
        });
        _ = LoadEmailCaptchaAsync(captchaImage, captchaAnswer, emailStatus);
    }

    private async Task LoadEmailCaptchaAsync(Image image, TextBox answer, TextBlock status)
    {
        CancelEmailCaptchaLoad();
        var requestVersion = emailCaptchaRequestVersion;
        using var timeout = CreateTimeout(TimeSpan.FromSeconds(30));
        emailCaptchaLoadCancellation = timeout;
        emailCaptchaId = Guid.Empty;
        answer.Text = string.Empty;
        SetStatus(status, "正在载入人机验证码…");
        try
        {
            var challenge = await session.GetCaptchaAsync(timeout.Token);
            if (!IsCurrentEmailCaptchaRequest(requestVersion, timeout)) return;
            var bytes = Convert.FromBase64String(challenge.ImageBase64);
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = DecodeServerImage(stream);
            if (!IsCurrentEmailCaptchaRequest(requestVersion, timeout))
            {
                bitmap.Dispose();
                return;
            }
            image.Source = bitmap;
            emailCaptchaBitmap?.Dispose();
            emailCaptchaBitmap = bitmap;
            emailCaptchaId = challenge.Id;
            SetStatus(status, "请输入图片中的 5 位数字。", success: true);
        }
        catch (OperationCanceledException) when (
            requestVersion != emailCaptchaRequestVersion ||
            lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!IsCurrentEmailCaptchaRequest(requestVersion, timeout)) return;
            SetStatus(status, FormatFailure(exception), error: true);
        }
        finally
        {
            if (ReferenceEquals(emailCaptchaLoadCancellation, timeout))
                emailCaptchaLoadCancellation = null;
        }
    }

    private async Task RunEmailCodeCooldownAsync(Button button)
    {
        var generation = ++emailCodeCooldownGeneration;
        for (var seconds = 60; seconds > 0 && generation == emailCodeCooldownGeneration; seconds--)
        {
            button.IsEnabled = false;
            button.Content = AvaloniaLocalizationCatalog.Format("{0} 秒后重发", seconds);
            try
            {
                await Task.Delay(1000, lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (generation != emailCodeCooldownGeneration) return;
        button.Content = "发送邮箱验证码";
        button.IsEnabled = true;
    }

    private void ShowNotifications()
    {
        showingNotifications = true;
        showingRoomWorkspace = false;
        unreadNotifications = 0;
        UpdateIdentity();
        var list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemsSource = notificationMessages.Count == 0
                ? new[] { "当前没有新的关注通知。" }
                : notificationMessages.ToArray()
        };
        AutomationProperties.SetAutomationId(list, "OnlineCommunity.Notifications.List");
        var refresh = ActionButton("立即刷新", "OnlineCommunity.Notifications.Refresh");
        refresh.Click += async (_, _) =>
        {
            await PollNotificationsAsync();
            ShowNotifications();
        };
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                SectionTitle("通知", "查看新的关注提醒。"),
                refresh,
                list
            }
        };
        SetSection(panel);
    }

    private void Session_NotificationsReceived(
        object? sender,
        OnlineUserNotificationsReceivedEventArgs eventArgs)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
                Session_NotificationsReceived(sender, eventArgs));
            return;
        }
        if (session.CurrentUser?.Id != eventArgs.UserId) return;
        ApplyNotifications(eventArgs.Notifications, markUnread: true);
    }

    private void ApplyNotifications(
        IEnumerable<OnlineUserNotificationDto> notifications,
        bool markUnread)
    {
        if (disposed) return;
        var added = 0;
        foreach (var notification in notifications
                     .Where(item =>
                         item.Type.Equals("follow", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Id))
        {
            if (!processedNotificationIds.Add(notification.Id)) continue;
            var message = WindowsTransientToastParityPolicy.FormatFollowNotification(
                notification.ActorDisplayName);
            notificationMessages.Insert(
                0,
                $"{message}  {notification.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            if (markUnread && !showingNotifications) unreadNotifications++;
            globalStatus.Text = message;
            globalStatus.Foreground = SuccessBrush;
            added++;
        }
        if (notificationMessages.Count > OnlineAccountSession.MaximumRecentNotificationCount)
        {
            notificationMessages.RemoveRange(
                OnlineAccountSession.MaximumRecentNotificationCount,
                notificationMessages.Count -
                OnlineAccountSession.MaximumRecentNotificationCount);
        }
        if (added == 0) return;
        UpdateIdentity();
        if (showingNotifications) ShowNotifications();
    }

    private async Task PollNotificationsAsync()
    {
        if (notificationPollInProgress || !session.IsAuthenticated || lifetime.IsCancellationRequested) return;
        notificationPollInProgress = true;
        try
        {
            using var timeout = CreateTimeout(TimeSpan.FromSeconds(8));
            var notifications = await session.PollNotificationsAsync(timeout.Token);
            notificationTimer.Interval = TimeSpan.FromSeconds(5);
            ApplyNotifications(notifications, markUnread: true);
        }
        catch (OnlineApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            // Retry-After is server data. DispatcherTimer.Interval throws for a
            // negative span or one beyond int.MaxValue milliseconds, so bound it
            // to a sane polling range before assigning.
            var retryAfter = exception.RetryAfter.GetValueOrDefault(TimeSpan.FromSeconds(5));
            notificationTimer.Interval = retryAfter < TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : retryAfter > TimeSpan.FromMinutes(10)
                    ? TimeSpan.FromMinutes(10)
                    : retryAfter;
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            globalStatus.Text = AvaloniaLocalizationCatalog.Format("通知同步暂时中断：{0}", FormatFailure(exception));
            globalStatus.Foreground = MutedBrush;
        }
        finally
        {
            notificationPollInProgress = false;
        }
    }

    private Border BuildAvatar(string avatarKey, string displayName, double size)
    {
        _ = displayName;
        var avatar = new WindowsDefaultUserAvatar(
            size,
            StrokeBrush,
            1,
            "OnlineCommunity.DefaultAvatar");
        if (!string.IsNullOrWhiteSpace(avatarKey) &&
            !avatarKey.Equals("default", StringComparison.OrdinalIgnoreCase))
            _ = LoadAvatarAsync(avatar.RemoteImage, avatarKey);
        return avatar;
    }

    private async Task LoadAvatarAsync(Image image, string avatarKey)
    {
        try
        {
            if (avatarCache.TryGetValue(avatarKey, out var cached))
            {
                ShowCachedAvatar(image, cached);
                return;
            }
            if (!avatarLoads.TryGetValue(avatarKey, out var load))
            {
                load = DownloadAvatarAsync(avatarKey);
                avatarLoads.Add(avatarKey, load);
            }
            try
            {
                await load;
            }
            finally
            {
                // Only in-flight downloads are shared. A finished one lives in
                // avatarCache; a failed one must not stay here, or that avatar
                // could never be retried for the lifetime of the surface.
                if (avatarLoads.TryGetValue(avatarKey, out var current) &&
                    ReferenceEquals(current, load))
                    avatarLoads.Remove(avatarKey);
            }
            if (!lifetime.IsCancellationRequested &&
                avatarCache.TryGetValue(avatarKey, out var loaded))
                ShowCachedAvatar(image, loaded);
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
        }
    }

    private async Task<Bitmap?> DownloadAvatarAsync(string avatarKey)
    {
        using var stream = new MemoryStream();
        await session.DownloadAvatarAsync(avatarKey, stream, cancellationToken: lifetime.Token);
        stream.Position = 0;
        // Decode off the UI thread and only to the largest size shown (112 DIP
        // at up to 2x scaling). Full-resolution uploads decoded on the UI thread
        // stalled scrolling and kept megabytes per avatar alive.
        var bitmap = await Task.Run(() => DecodeServerImage(stream, AvatarDecodeWidth));
        if (disposed)
        {
            bitmap.Dispose();
            return null;
        }
        avatarCache[avatarKey] = new AvatarCacheEntry(bitmap) { LastUsed = ++avatarUseClock };
        TrimAvatarCache();
        return bitmap;
    }

    private void ShowCachedAvatar(Image image, AvatarCacheEntry entry)
    {
        entry.LastUsed = ++avatarUseClock;
        entry.Images.RemoveAll(reference =>
            !reference.TryGetTarget(out var shown) || !ReferenceEquals(shown.Source, entry.Bitmap));
        entry.Images.Add(new WeakReference<Image>(image));
        image.Source = entry.Bitmap;
    }

    /// <summary>
    /// Bounded LRU. An entry whose bitmap is still shown by an attached Image is
    /// never evicted (disposing it would make that Image throw from Render);
    /// the cache may then exceed its capacity until those rows go away.
    /// </summary>
    private void TrimAvatarCache()
    {
        while (avatarCache.Count > AvatarCacheCapacity)
        {
            string? victim = null;
            var oldest = long.MaxValue;
            foreach (var pair in avatarCache)
            {
                if (pair.Value.LastUsed >= oldest || IsAvatarDisplayed(pair.Value)) continue;
                victim = pair.Key;
                oldest = pair.Value.LastUsed;
            }
            if (victim == null) return;
            var evicted = avatarCache[victim];
            avatarCache.Remove(victim);
            // A detached row can be attached again later; it must fall back to
            // the default artwork rather than render a disposed bitmap.
            foreach (var reference in evicted.Images)
                if (reference.TryGetTarget(out var image) &&
                    ReferenceEquals(image.Source, evicted.Bitmap))
                    image.Source = null;
            evicted.Bitmap.Dispose();
        }
    }

    private static bool IsAvatarDisplayed(AvatarCacheEntry entry)
    {
        foreach (var reference in entry.Images)
            if (reference.TryGetTarget(out var image) &&
                ReferenceEquals(image.Source, entry.Bitmap) &&
                TopLevel.GetTopLevel(image) != null)
                return true;
        return false;
    }

    private void ShowSignedOut()
    {
        var close = PrimaryButton("返回曲库", "OnlineCommunity.SignedOut.Close");
        close.Click += (_, _) => Complete(false);
        SetSection(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 14,
            Children =
            {
                SectionTitle("登录会话已结束", "请返回曲库后重新登录。"),
                close
            }
        });
    }

    private async Task RunActionAsync(
        TextBlock status,
        string progressText,
        Func<CancellationToken, Task> action,
        TimeSpan? timeout = null)
    {
        if (actionInProgress) return;
        actionInProgress = true;
        SetStatus(status, progressText);
        try
        {
            using var cancellation = CreateTimeout(timeout ?? TimeSpan.FromSeconds(30));
            await action(cancellation.Token);
        }
        catch (Exception exception) when (IsRecoverableActionFailure(exception))
        {
            SetStatus(status, FormatFailure(exception), error: true);
        }
        finally
        {
            actionInProgress = false;
        }
    }

    private async Task RunSectionLoadAsync<T>(
        long viewVersion,
        TextBlock status,
        string progressText,
        Func<CancellationToken, Task<T>> load,
        Action<T> apply,
        TimeSpan? timeout = null,
        Action? applyFailure = null)
    {
        if (viewVersion != sectionVersion || lifetime.IsCancellationRequested) return;
        CancelSectionLoad();
        var requestVersion = sectionLoadRequestVersion;
        using var cancellation = CreateTimeout(timeout ?? TimeSpan.FromSeconds(30));
        sectionLoadCancellation = cancellation;
        SetStatus(status, progressText);
        try
        {
            var result = await load(cancellation.Token);
            if (!IsCurrentSectionLoad(viewVersion, requestVersion, cancellation)) return;
            apply(result);
        }
        catch (OperationCanceledException) when (
            viewVersion != sectionVersion ||
            requestVersion != sectionLoadRequestVersion ||
            lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsRecoverableActionFailure(exception))
        {
            if (!IsCurrentSectionLoad(viewVersion, requestVersion, cancellation)) return;
            applyFailure?.Invoke();
            SetStatus(status, FormatFailure(exception), error: true);
        }
        finally
        {
            if (ReferenceEquals(sectionLoadCancellation, cancellation))
                sectionLoadCancellation = null;
        }
    }

    private CancellationTokenSource CreateTimeout(TimeSpan timeout)
    {
        // A closed surface starts no new request: hand back a source that is
        // already cancelled so the caller's normal cancellation path runs.
        if (disposed)
        {
            var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            return cancelled;
        }
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        source.CancelAfter(timeout);
        return source;
    }

    private static void SetPagingButtons(
        Button? previous,
        Button? next,
        int page,
        int totalPages,
        bool enabled)
    {
        if (previous != null) previous.IsEnabled = enabled && page > 1;
        if (next != null) next.IsEnabled = enabled && page < totalPages;
    }

    private void SetSection(Control content)
    {
        sectionVersion++;
        CancelDirectoryLoad();
        CancelPlayerProfileLoad();
        CancelRoomDirectoryLoad();
        CancelRoomInvitationLoad();
        CancelEmailCaptchaLoad();
        CancelSectionLoad();
        sectionHost.Child = content;
    }

    private bool IsCurrentDirectoryRequest(
        long requestVersion,
        CancellationTokenSource cancellation,
        ListBox list,
        TextBlock status) =>
        requestVersion == directoryRequestVersion &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(directoryLoadCancellation, cancellation) &&
        ReferenceEquals(directoryList, list) &&
        ReferenceEquals(directoryStatus, status);

    private bool IsCurrentPlayerProfileRequest(
        long requestVersion,
        CancellationTokenSource cancellation) =>
        requestVersion == playerProfileRequestVersion &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(playerProfileLoadCancellation, cancellation);

    private bool IsCurrentRoomDirectoryRequest(
        long requestVersion,
        CancellationTokenSource cancellation,
        ListBox list,
        TextBlock status) =>
        requestVersion == roomDirectoryRequestVersion &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(roomDirectoryLoadCancellation, cancellation) &&
        ReferenceEquals(roomDirectoryList, list) &&
        ReferenceEquals(roomDirectoryStatus, status);

    private bool IsCurrentRoomInvitationRequest(
        long requestVersion,
        CancellationTokenSource cancellation,
        ListBox list) =>
        requestVersion == roomInvitationRequestVersion &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(roomInvitationLoadCancellation, cancellation) &&
        ReferenceEquals(roomInvitationList, list);

    private bool IsCurrentEmailCaptchaRequest(
        long requestVersion,
        CancellationTokenSource cancellation) =>
        requestVersion == emailCaptchaRequestVersion &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(emailCaptchaLoadCancellation, cancellation);

    private bool IsCurrentSectionLoad(
        long viewVersion,
        long requestVersion,
        CancellationTokenSource cancellation) =>
        viewVersion == sectionVersion &&
        requestVersion == sectionLoadRequestVersion &&
        !cancellation.IsCancellationRequested &&
        ReferenceEquals(sectionLoadCancellation, cancellation);

    private void CancelDirectoryLoad()
    {
        directoryRequestVersion++;
        var cancellation = directoryLoadCancellation;
        directoryLoadCancellation = null;
        cancellation?.Cancel();
    }

    private void CancelPlayerProfileLoad()
    {
        playerProfileRequestVersion++;
        var cancellation = playerProfileLoadCancellation;
        playerProfileLoadCancellation = null;
        cancellation?.Cancel();
    }

    private void CancelRoomDirectoryLoad()
    {
        roomDirectoryRequestVersion++;
        var cancellation = roomDirectoryLoadCancellation;
        roomDirectoryLoadCancellation = null;
        cancellation?.Cancel();
    }

    private void CancelRoomInvitationLoad()
    {
        roomInvitationRequestVersion++;
        var cancellation = roomInvitationLoadCancellation;
        roomInvitationLoadCancellation = null;
        cancellation?.Cancel();
    }

    private void CancelEmailCaptchaLoad()
    {
        emailCaptchaRequestVersion++;
        var cancellation = emailCaptchaLoadCancellation;
        emailCaptchaLoadCancellation = null;
        cancellation?.Cancel();
    }

    private void CancelSectionLoad()
    {
        sectionLoadRequestVersion++;
        var cancellation = sectionLoadCancellation;
        sectionLoadCancellation = null;
        cancellation?.Cancel();
    }

    private static Control Place(Control control, int row, Thickness? margin = null)
    {
        Grid.SetRow(control, row);
        if (margin.HasValue) control.Margin = margin.Value;
        return control;
    }

    private Button NavigationButton(string text, string automationId, Action action)
    {
        var button = ActionButton(text, automationId);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Click += (_, _) => action();
        return button;
    }

    private static Border Card(Control content, Thickness padding) => new()
    {
        Background = PanelBrush,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = padding,
        Child = content
    };

    private static Control BuildTags(IReadOnlyList<OnlineUserTagDto>? tags)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var tag in tags ?? [])
        {
            Color color;
            try
            {
                color = Color.Parse(tag.Color);
            }
            catch (FormatException)
            {
                color = Color.Parse("#78909C");
            }
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(color, .18),
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3),
                Margin = new Thickness(0, 0, 7, 7),
                Child = new TextBlock
                {
                    Text = tag.IsNameColor ? AvaloniaLocalizationCatalog.Format("{0} · 名字色", tag.Name) : tag.Name,
                    Foreground = new SolidColorBrush(color),
                    FontSize = 11
                }
            });
        }
        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "暂无标签", Foreground = MutedBrush });
        return panel;
    }

    private static StackPanel SectionTitle(string title, string subtitle) => new()
    {
        Spacing = 3,
        Children =
        {
            new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontSize = 27,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            },
            new TextBlock
            {
                Text = subtitle,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap
            }
        }
    };

    private static TextBlock Subheading(string text) => new()
    {
        Text = text,
        Foreground = Brushes.White,
        FontSize = 18,
        FontWeight = FontWeight.SemiBold
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = MutedBrush,
        FontSize = 12,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static TextBlock StatusText(string automationId)
    {
        var text = new TextBlock
        {
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(text, automationId);
        return text;
    }

    private static TextBox TextField(
        int maximumLength,
        string automationId,
        bool password = false)
    {
        TextBox text = password
            ? new WindowsPasswordTextBox()
            : new TextBox();
        text.MaxLength = maximumLength;
        text.Height = 38;
        text.Padding = new Thickness(10, 6);
        text.Background = RaisedBrush;
        text.Foreground = Brushes.White;
        text.BorderBrush = StrokeBrush;
        text.BorderThickness = new Thickness(1);
        text.CornerRadius = new CornerRadius(4);
        AutomationProperties.SetAutomationId(text, automationId);
        return text;
    }

    private static TextBox PasswordField(string automationId, int maximumLength = 128)
    {
        var text = TextField(maximumLength, automationId, password: true);
        text.PasswordChar = '●';
        WindowsPasswordInputPolicy.Apply(text);
        return text;
    }

    private static Button PrimaryButton(string text, string automationId)
    {
        var button = ActionButton(text, automationId);
        button.Background = AccentBrush;
        button.Foreground = Brushes.Black;
        button.BorderBrush = AccentBrush;
        return button;
    }

    private static Button ActionButton(string text, string automationId)
    {
        var button = new Button
        {
            Content = text,
            Height = 38,
            Padding = new Thickness(12, 6),
            Background = RaisedBrush,
            Foreground = Brushes.White,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    private static void SetStatus(TextBlock status, string text, bool error = false, bool success = false)
    {
        status.Text = text;
        status.Foreground = error ? ErrorBrush : success ? SuccessBrush : MutedBrush;
    }

    private static string DirectoryTitle(string mode) => mode switch
    {
        "search" => "搜索玩家",
        "leaderboard" => "制谱时长排行",
        "friends" => "好友",
        "following" => "我的关注",
        "followers" => "我的粉丝",
        _ => "全部玩家"
    };

    private static string DirectoryDescription(string mode) => mode switch
    {
        "leaderboard" => "按服务器统计的制谱时长查看前 50 名玩家。",
        "friends" => "只有互相关注的玩家才是好友，可以相互发送房间邀请。",
        "following" => "你已关注的玩家。",
        "followers" => "关注你的玩家。",
        _ => "浏览在线社区、搜索玩家并打开公开资料。"
    };

    private static string FormatDuration(long seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return duration.TotalDays >= 1
            ? AvaloniaLocalizationCatalog.Format("{0} 天 {1:hh\\:mm\\:ss}", (int)duration.TotalDays, duration)
            : duration.ToString(@"hh\:mm\:ss");
    }

    private static string FormatBytes(long bytes)
    {
        var safe = Math.Max(0, bytes);
        if (safe >= 1024L * 1024 * 1024)
            return $"{safe / (1024d * 1024 * 1024):0.##} GB";
        if (safe >= 1024L * 1024)
            return $"{safe / (1024d * 1024):0.##} MB";
        if (safe >= 1024)
            return $"{safe / 1024d:0.##} KB";
        return $"{safe} B";
    }

    // Android's HTTP handler reports a cancelled or failed request as
    // System.Net.WebException rather than the HttpRequestException or
    // OperationCanceledException the Windows handler raises. Leaving it out of
    // this list let a poll that was cancelled by sign-out escape every catch
    // and take the whole process down. A captcha or avatar payload that is not
    // an image is reported by DecodeServerImage as InvalidDataException; a bare
    // ArgumentException stays out of this list because the room actions treat it
    // as a programming error that must not be swallowed.
    private static bool IsExpectedFailure(Exception exception) =>
        exception is OnlineApiException or
            HttpRequestException or
            OperationCanceledException or
            System.Net.WebException or
            JsonException or
            FormatException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException;

    /// <summary>
    /// Decodes an image the server sent. Avalonia rejects bytes that are not a
    /// decodable image with ArgumentException; that is bad server data, not a
    /// programming error, so it is reported as InvalidDataException instead.
    /// </summary>
    private static Bitmap DecodeServerImage(Stream stream, int? decodeWidth = null)
    {
        try
        {
            return decodeWidth is { } width
                ? Bitmap.DecodeToWidth(stream, width)
                : new Bitmap(stream);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("服务器返回了无法识别的数据，请检查网络连接后重试。", exception);
        }
    }

    private static bool IsCancellationFailure(Exception exception) =>
        exception is OperationCanceledException ||
        exception is System.Net.WebException
        {
            Status: System.Net.WebExceptionStatus.RequestCanceled
        };

    private static bool IsRecoverableActionFailure(Exception exception) =>
        IsExpectedFailure(exception) || exception is InvalidOperationException;

    private static string FormatFailure(Exception exception) => IsCancellationFailure(exception)
        ? "连接服务器超时，请稍后重试。"
        : exception.Message;

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private sealed record PendingRoomCreationRequest(
        string Name,
        bool IsPrivate,
        string? Password,
        bool IsHidden,
        bool LockExistingNotes);

    private sealed record RoomUploadConfirmationResult(
        bool LockExistingNotes);

    private sealed class AvatarCacheEntry(Bitmap bitmap)
    {
        public Bitmap Bitmap { get; } = bitmap;
        public long LastUsed { get; set; }
        /// <summary>Images this bitmap was assigned to, for the in-use check.</summary>
        public List<WeakReference<Image>> Images { get; } = [];
    }
}

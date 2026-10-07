namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Stable Windows/WPF 1.1.0 geometry and routing contract for the account,
/// login and community surfaces hosted inside the main application shell.
/// Network/session behavior remains owned by LumaChart.Online.
/// </summary>
public static class WindowsOnlineSurfaceParityPolicy
{
    public const string ContractVersion = "windows-online-surface-r8";
    public const int StartupRestoreTimeoutSeconds = 8;

    public const int ProfileDisplayNameMaximumLength = 32;
    public const int ProfileBioMaximumLength = 160;
    public const int RoomSearchMaximumLength = 40;
    public const int RoomNameMaximumLength = 40;
    public const int RoomNumberMaximumLength = 12;
    public const int RoomPasswordMaximumLength = 64;
    public const int PlayerSearchMaximumLength = 32;

    public const double LoginCardWidth = 780;
    public const double LoginCardHeight = 630;
    public const double LoginIdentityColumnWidth = 310;
    public const double LoginIdentityPadding = 34;
    public const double LoginAvatarSize = 126;
    public const double LoginAvatarBorderThickness = 1.5;
    public const string LoginAvatarBorderColor = "#5D7175";
    public const double LoginTitleTopMargin = 22;
    public const double LoginDescriptionTopMargin = 9;
    public const double LoginFormLeftRightMargin = 44;
    public const double LoginFormTopMargin = 48;
    public const double LoginFormBottomMargin = 32;
    public const double WorkflowCardWidth = 600;
    public const double WorkflowCardMaxHeight = 780;
    public const double OfflineButtonRightMargin = 24;
    public const double OfflineButtonBottomMargin = 22;
    public const double OfflineButtonWidth = 190;
    public const double OfflineButtonHeight = 46;

    public const double CommunityHeaderHeight = 58;
    public const double CommunityNavigationWidth = 190;
    public const double CommunityNavigationHorizontalMargin = 12;
    public const double CommunityNavigationVerticalMargin = 18;

    public const double RoomPasswordCardWidth = 390;
    public const double RoomPasswordCardPadding = 24;
    public const double RoomPasswordCardCornerRadius = 7;
    public const double RoomPasswordTitleFontSize = 20;
    public const double RoomPasswordButtonHeight = 38;
    public const double RoomPasswordButtonGap = 10;

    public const double InvitationToastWidth = 360;
    public const double InvitationToastTopMargin = 50;
    public const double InvitationToastRightMargin = 14;
    public const double InvitationToastPadding = 14;
    public const double InvitationToastCornerRadius = 6;
    public const double InvitationToastHeaderFontSize = 10;
    public const double InvitationToastTitleFontSize = 14;
    public const double InvitationToastDetailFontSize = 12;
    public const double InvitationToastTitleTopMargin = 6;
    public const double InvitationToastDetailTopMargin = 5;
    public const double InvitationToastActionsTopMargin = 12;
    public const double InvitationToastButtonHeight = 34;
    public const double InvitationToastButtonGap = 8;

    public static bool RequiresRoomPasswordOverlay(
        bool isPrivate,
        string? suppliedPassword) =>
        isPrivate && string.IsNullOrEmpty(suppliedPassword);

    public static string CreateDefaultRoomName(string? displayName) =>
        AvaloniaLocalizationCatalog.Format("{0} 的写谱房", (string.IsNullOrWhiteSpace(displayName) ? "Guest" : displayName.Trim()));

    public static bool SuppressEditorShortcutRouting(bool surfaceVisible) =>
        surfaceVisible;

    public static bool RequiresStartupAccountChoice(
        bool evidenceChartMode,
        bool sessionAuthenticated) =>
        !evidenceChartMode && !sessionAuthenticated;
}

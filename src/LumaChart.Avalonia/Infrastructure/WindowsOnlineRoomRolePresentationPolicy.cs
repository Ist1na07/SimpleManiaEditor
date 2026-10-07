namespace LumaChart.Avalonia.Infrastructure;

internal sealed record WindowsOnlineRoomRoleOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Windows/WPF 1.1.0 visible room-role and member-management copy.
/// Protocol values remain owner/editor/viewer and never leak into the UI.
/// </summary>
internal static class WindowsOnlineRoomRolePresentationPolicy
{
    public const string Contract = "wpf-1.1.0-online-room-role-presentation-r1";
    public const string PermissionHeading = "房间编辑权限 · 由房主持久分配";
    public const string MetadataPermissionLabel = "允许编辑谱面信息";
    public const string TimingPermissionLabel =
        "允许编辑 Effect（Timing Edit 全部功能）";
    public const string NotesPermissionLabel = "允许编辑 Note";
    public const string SavePermissionsLabel = "保存房间权限";
    public const string KickLabel = "踢出";
    public const string TemporaryBanLabel = "暂时封禁 30 分钟";
    public const int TemporaryBanMinutes = 30;

    public static IReadOnlyList<WindowsOnlineRoomRoleOption> EditableRoleOptions
        { get; } =
    [
        new("editor", "编辑者"),
        new("viewer", "观察者")
    ];

    public static string NormalizeRole(string? memberRole) =>
        memberRole?.Trim().ToLowerInvariant() switch
        {
            "owner" => "owner",
            "viewer" => "viewer",
            _ => "editor"
        };

    public static string RoleLabel(string? memberRole) =>
        NormalizeRole(memberRole) switch
        {
            "owner" => "房主",
            "viewer" => "观察者",
            _ => "编辑者"
        };

    public static string MemberStateLabel(string? memberRole, bool isOnline)
    {
        var role = RoleLabel(memberRole);
        return isOnline ? role : AvaloniaLocalizationCatalog.Format("{0} · 离线", role);
    }

    public static WindowsOnlineRoomRoleOption EditableOption(string? memberRole)
    {
        var normalized = NormalizeRole(memberRole);
        return EditableRoleOptions.First(option =>
            option.Value.Equals(normalized, StringComparison.Ordinal));
    }

    public static string EditableRoleValue(string? visibleLabel) =>
        visibleLabel?.Trim() == "观察者" ? "viewer" : "editor";

    public static string ChangeActionLabel(string? currentRole) =>
        NormalizeRole(currentRole) == "viewer" ? "设为编辑者" : "设为观察者";

    public static string ToggleTargetRole(string? currentRole) =>
        NormalizeRole(currentRole) == "viewer" ? "editor" : "viewer";
}

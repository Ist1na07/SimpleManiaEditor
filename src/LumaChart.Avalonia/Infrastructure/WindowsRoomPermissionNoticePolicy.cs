namespace LumaChart.Avalonia.Infrastructure;

internal sealed record WindowsRoomPermissionNoticePresentation(
    string Title,
    string Message);

internal sealed record WindowsRoomAccessSnapshot(
    string MemberRole,
    bool CanEditMetadata,
    bool CanEditTiming,
    bool CanEditNotes);

/// <summary>
/// Exact Windows/WPF 1.1.0 copy and geometry contract for the editor permission
/// notice that blocks the workbench after a rejected online edit or announces a
/// live room-access change.
/// </summary>
internal static class WindowsRoomPermissionNoticePolicy
{
    public const string Contract = "wpf-1.1.0-room-permission-notice-r1";
    public const string AccessChangeContract =
        "wpf-1.1.0-room-access-change-notice-r1";
    public const string EditorActionContract =
        "wpf-1.1.0-editor-permission-action-notice-r1";
    public const double CardWidth = 470;
    public const double CardPadding = 24;
    public const double CardCornerRadius = 7;
    public const double TitleFontSize = 21;
    public const double MessageTopMargin = 13;
    public const double MessageBottomMargin = 18;
    public const double MessageLineHeight = 22;
    public const double DismissHeight = 38;
    public const double DismissHorizontalPadding = 28;
    public const double DismissVerticalPadding = 7;
    public const string OverlayColor = "#A8111416";
    public const string CardColor = "#1A2023";
    public const string CardBorderColor = "#557075";
    public const string TitleColor = "#E3E9EA";
    public const string MessageColor = "#B4C0C2";

    public static WindowsRoomPermissionNoticePresentation Denied(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        return new WindowsRoomPermissionNoticePresentation(
            "没有编辑权限",
            AvaloniaLocalizationCatalog.Format("无编辑 {0} 的权限，请向房主索要。", category.Trim()));
    }

    public static WindowsRoomPermissionNoticePresentation PreviewTimeDenied(
        bool readOnly) =>
        readOnly
            ? new WindowsRoomPermissionNoticePresentation(
                "观察者只读",
                "观察者不能修改谱面预览时间。")
            : new WindowsRoomPermissionNoticePresentation(
                "没有谱面信息权限",
                "设置预览时间需要谱面信息权限，请向房主索要。");

    public static WindowsRoomPermissionNoticePresentation? MetadataApplyDenied(
        string? permissionCategory) =>
        permissionCategory?.Trim() switch
        {
            "观察者只读" => new WindowsRoomPermissionNoticePresentation(
                "观察者只读",
                "观察者不能修改谱面信息、轨道数或 Offset。"),
            "谱面信息" => new WindowsRoomPermissionNoticePresentation(
                "没有谱面信息权限",
                "当前输入包含 Metadata 或 KeyCount 修改，请向房主索要谱面信息权限。"),
            "Effect" => new WindowsRoomPermissionNoticePresentation(
                "没有 Effect 权限",
                "Offset 属于 Timing；请恢复原值或向房主索要 Effect 权限。"),
            "Note" => new WindowsRoomPermissionNoticePresentation(
                "没有 Note 权限",
                "降低 KeyCount 会删除右侧 Note；请向房主索要 Note 权限。"),
            _ => null
        };

    public static WindowsRoomPermissionNoticePresentation TempoAnalysisStartDenied() =>
        new(
            "没有 Effect 权限",
            "BPM 与 Offset 属于 Timing；请向房主索要 Effect 权限。");

    public static WindowsRoomPermissionNoticePresentation? TempoAnalysisApplyDenied(
        string? permissionCategory,
        bool variableTempo)
    {
        var normalized = permissionCategory?.Trim();
        if (normalized is not ("观察者只读" or "Effect")) return null;
        return new WindowsRoomPermissionNoticePresentation(
            "没有 Effect 权限",
            variableTempo
                ? "分析期间 Timing 权限已失效，预测结果未写入。"
                : "分析期间 Timing 权限已失效，检测结果未写入。");
    }

    public static WindowsRoomAccessSnapshot Capture(
        string? memberRole,
        bool canEditMetadata,
        bool canEditTiming,
        bool canEditNotes) =>
        new(
            NormalizeRole(memberRole),
            canEditMetadata,
            canEditTiming,
            canEditNotes);

    public static WindowsRoomPermissionNoticePresentation? Updated(
        WindowsRoomAccessSnapshot previous,
        WindowsRoomAccessSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (previous == current) return null;

        var roleChanged = !previous.MemberRole.Equals(
            current.MemberRole,
            StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>();
        if (roleChanged)
            lines.Add(AvaloniaLocalizationCatalog.Format("你的身份已被修改为{0}。", RoleText(current.MemberRole)));
        lines.Add(current.MemberRole == "viewer"
            ? "观察者只能浏览、播放与选择，不能修改谱面。"
            : AvaloniaLocalizationCatalog.Format("谱面信息：{0}\nEffect / Timing Edit：{1}\nNote：{2}", PermissionState(current.CanEditMetadata), PermissionState(current.CanEditTiming), PermissionState(current.CanEditNotes)));
        return new WindowsRoomPermissionNoticePresentation(
            roleChanged ? "房间身份已修改" : "房间权限已更新",
            string.Join("\n\n", lines));
    }

    private static string NormalizeRole(string? memberRole) =>
        memberRole?.Trim().ToLowerInvariant() switch
        {
            "owner" => "owner",
            "viewer" => "viewer",
            _ => "editor"
        };

    private static string RoleText(string memberRole) => memberRole switch
    {
        "owner" => "房主",
        "viewer" => "观察者",
        _ => "编辑者"
    };

    private static string PermissionState(bool allowed) =>
        allowed ? "允许编辑" : "不可编辑";
}

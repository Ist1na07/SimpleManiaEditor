namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// WPF 1.1.0 transient, non-interactive editor notification metrics.
/// Keeping the timing and text policy outside MainWindow makes the real
/// automatic-backup path and Windows GUI evidence share one contract.
/// </summary>
public static class WindowsTransientToastParityPolicy
{
    public const double OnlineToastWidth = 360;
    public const double OnlineToastTopMargin = 50;
    public const double OnlineToastRightMargin = 14;
    public const double InvitationToastBottomMargin = 10;
    public const double FollowHorizontalPadding = 14;
    public const double FollowVerticalPadding = 11;
    public const double FollowCornerRadius = 6;
    public const double FollowDotSize = 8;
    public const double FollowDotRightMargin = 10;
    public const double FollowFontSize = 12;
    public const int FollowPollIntervalMilliseconds = 5000;
    public const int FollowDurationMilliseconds = 4000;

    public const string FollowBackground = "#F51B2023";
    public const string FollowBorder = "#FF5D8581";
    public const string FollowDot = "#FF78AAA2";
    public const string FollowForeground = "#FFE0E7E6";

    public const double EditorRightMargin = 18;
    public const double EditorBottomMargin = 18;
    public const double EditorHorizontalPadding = 13;
    public const double EditorVerticalPadding = 9;
    public const double EditorCornerRadius = 5;
    public const double EditorDotSize = 7;
    public const double EditorDotRightMargin = 9;
    public const double EditorFontSize = 11;
    public const int EditorDurationMilliseconds = 3000;

    public const string EditorBackground = "#F21C2425";
    public const string EditorBorder = "#FF5D8581";
    public const string EditorDot = "#FF78AAA2";
    public const string EditorForeground = "#FFD9E4E3";

    public static string FormatAutomaticBackupSuccess(int completed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(completed, 1);
        return completed == 1
            ? "自动备份成功"
            : AvaloniaLocalizationCatalog.Format("自动备份成功 · {0} 张谱面", completed);
    }

    public static string FormatFollowNotification(string? actorDisplayName)
    {
        var actor = string.IsNullOrWhiteSpace(actorDisplayName)
            ? "匿名用户"
            : actorDisplayName.Trim();
        return AvaloniaLocalizationCatalog.Format("{0}关注了你。", actor);
    }
}

using LumaChart.Online;

namespace LumaChart.Avalonia.Infrastructure;

internal sealed record WindowsAppUpdateSnapshot(
    string CurrentVersion,
    OnlineAppConfigurationDto Configuration,
    bool IsUpdateAvailable,
    bool BlocksLogin,
    Uri? DownloadUri);

internal sealed record WindowsAppUpdatePresentation(
    string Title,
    string VersionText,
    string Notice,
    string LaterButtonText,
    string DownloadButtonText,
    bool DownloadEnabled,
    bool BlocksLogin);

/// <summary>
/// Pure managed Windows/WPF 1.1.0 update and minimum-login policy.
/// This deliberately replaces the legacy native/OLLVM version comparison
/// without changing the public app-configuration wire contract.
/// </summary>
internal static class WindowsAppUpdatePolicy
{
    public const string Contract = "wpf-1.1.0-app-update-r1-managed";
    public const double CardWidth = 590;
    public const double CardMaxHeight = 680;
    public const double CardPadding = 28;
    public const double CardCornerRadius = 7;
    public const double TitleFontSize = 23;
    public const double NoticeMaxHeight = 330;
    public const double NoticeLineHeight = 22;
    public const double ActionButtonHeight = 40;
    public const int RequestTimeoutSeconds = 8;
    public const string OverlayColor = "#ED111315";
    public const string CardColor = "#1A1E21";
    public const string CardBorderColor = "#53666B";

    public static int CompareVersions(string? left, string? right) =>
        ParseVersion(left).CompareTo(ParseVersion(right));

    public static WindowsAppUpdateSnapshot Evaluate(
        OnlineAppConfigurationDto configuration,
        string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        return new WindowsAppUpdateSnapshot(
            currentVersion,
            configuration,
            CompareVersions(currentVersion, configuration.LatestVersion) < 0,
            CompareVersions(currentVersion, configuration.MinimumLoginVersion) < 0,
            ResolveDownloadUri(configuration.DownloadUrl));
    }

    public static WindowsAppUpdatePresentation? Present(
        WindowsAppUpdateSnapshot snapshot,
        bool blocksLogin,
        bool showWhenCurrent)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var effectiveBlock = blocksLogin || snapshot.BlocksLogin;
        if (!snapshot.IsUpdateAvailable && !effectiveBlock && !showWhenCurrent) return null;
        var showNotice = snapshot.IsUpdateAvailable || effectiveBlock;
        return new WindowsAppUpdatePresentation(
            effectiveBlock
                ? "必须更新后才能登录"
                : snapshot.IsUpdateAvailable ? "发现新版本" : "已经是最新版本",
            AvaloniaLocalizationCatalog.Format("当前 {0}  →  最新 {1}", snapshot.CurrentVersion, snapshot.Configuration.LatestVersion),
            showNotice
                ? string.IsNullOrEmpty(snapshot.Configuration.UpdateNotice)
                    ? "本次更新暂无公告。"
                    : snapshot.Configuration.UpdateNotice
                : "当前客户端已经是最新版本。",
            effectiveBlock ? "离线使用" : "关闭",
            snapshot.DownloadUri == null ? "暂未提供链接" : "打开下载链接",
            snapshot.DownloadUri != null,
            effectiveBlock);
    }

    public static WindowsAppUpdatePresentation Failure(
        string currentVersion,
        bool blocksLogin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        return new WindowsAppUpdatePresentation(
            blocksLogin ? "暂时无法验证版本" : "检查更新失败",
            AvaloniaLocalizationCatalog.Format("当前版本 {0}", currentVersion),
            blocksLogin
                ? "服务器要求更新，但暂时无法取得更新公告和下载链接。你仍可离线使用，稍后再重试。"
                : "暂时无法连接更新服务器，请稍后再试。",
            blocksLogin ? "离线使用" : "关闭",
            "暂未提供链接",
            DownloadEnabled: false,
            BlocksLogin: blocksLogin);
    }

    public static string SettingsVersionText(
        string currentVersion,
        WindowsAppUpdateSnapshot? snapshot) =>
        snapshot == null
            ? AvaloniaLocalizationCatalog.Format("当前版本 {0}", currentVersion)
            : AvaloniaLocalizationCatalog.Format("当前版本 {0} · 最新 {1}", currentVersion, snapshot.Configuration.LatestVersion);

    private static Version ParseVersion(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        var metadata = normalized.IndexOfAny(['-', '+']);
        if (metadata >= 0) normalized = normalized[..metadata];
        return Version.TryParse(normalized, out var parsed)
            ? parsed
            : new Version(0, 0, 0);
    }

    private static Uri? ResolveDownloadUri(string? value) =>
        WindowsExternalUriPolicy.Resolve(value);
}

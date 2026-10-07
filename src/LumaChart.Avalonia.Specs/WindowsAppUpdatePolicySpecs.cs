using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Online;
using System.Reflection;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsAppUpdatePolicySpecs
{
    private static readonly DateTimeOffset UpdatedAt =
        DateTimeOffset.Parse("2026-07-27T00:00:00Z");

    public static WindowsAppUpdatePolicySpecResult[] Run()
    {
        var currentVersion = WindowsOnlineAccountHost.ClientVersion;
        var availableVersion = NextBetaVersion(currentVersion);
        var requiredVersion = NextMajorVersion(currentVersion);
        var current = Configuration(
            latest: currentVersion,
            minimum: currentVersion,
            notice: "无需更新。",
            download: "https://example.invalid/current");
        var available = Configuration(
            latest: availableVersion,
            minimum: "1.0.0",
            notice: "Windows Avalonia 更新公告。",
            download: "https://example.invalid/update");
        var required = Configuration(
            latest: requiredVersion,
            minimum: requiredVersion,
            notice: string.Empty,
            download: string.Empty);
        var currentSnapshot = WindowsAppUpdatePolicy.Evaluate(
            current,
            WindowsOnlineAccountHost.ClientVersion);
        var availableSnapshot = WindowsAppUpdatePolicy.Evaluate(
            available,
            WindowsOnlineAccountHost.ClientVersion);
        var requiredSnapshot = WindowsAppUpdatePolicy.Evaluate(
            required,
            WindowsOnlineAccountHost.ClientVersion);
        var httpDownloadSnapshot = WindowsAppUpdatePolicy.Evaluate(
            Configuration(
                latest: availableVersion,
                minimum: "1.0.0",
                notice: string.Empty,
                download: "http://127.0.0.1/update"),
            WindowsOnlineAccountHost.ClientVersion);
        var fileDownloadSnapshot = WindowsAppUpdatePolicy.Evaluate(
            Configuration(
                latest: availableVersion,
                minimum: "1.0.0",
                notice: string.Empty,
                download: "file:///C:/Windows/System32/calc.exe"),
            WindowsOnlineAccountHost.ClientVersion);
        var customSchemeSnapshot = WindowsAppUpdatePolicy.Evaluate(
            Configuration(
                latest: availableVersion,
                minimum: "1.0.0",
                notice: string.Empty,
                download: "lumachart-update://install/1.2.0"),
            WindowsOnlineAccountHost.ClientVersion);
        var currentPresentation = WindowsAppUpdatePolicy.Present(
            currentSnapshot,
            blocksLogin: false,
            showWhenCurrent: true);
        var availablePresentation = WindowsAppUpdatePolicy.Present(
            availableSnapshot,
            blocksLogin: false,
            showWhenCurrent: false);
        var requiredPresentation = WindowsAppUpdatePolicy.Present(
            requiredSnapshot,
            blocksLogin: false,
            showWhenCurrent: false);
        var failed = WindowsAppUpdatePolicy.Failure(
            WindowsOnlineAccountHost.ClientVersion,
            blocksLogin: true);

        return
        [
            Check(
                WindowsAppUpdatePolicy.Contract ==
                "wpf-1.1.0-app-update-r1-managed" &&
                WindowsAppUpdatePolicy.CardWidth == 590 &&
                WindowsAppUpdatePolicy.CardMaxHeight == 680 &&
                WindowsAppUpdatePolicy.CardPadding == 28 &&
                WindowsAppUpdatePolicy.CardCornerRadius == 7 &&
                WindowsAppUpdatePolicy.TitleFontSize == 23 &&
                WindowsAppUpdatePolicy.NoticeMaxHeight == 330 &&
                WindowsAppUpdatePolicy.NoticeLineHeight == 22 &&
                WindowsAppUpdatePolicy.ActionButtonHeight == 40 &&
                WindowsAppUpdatePolicy.RequestTimeoutSeconds == 8 &&
                WindowsAppUpdatePolicy.OverlayColor == "#ED111315" &&
                WindowsAppUpdatePolicy.CardColor == "#1A1E21" &&
                WindowsAppUpdatePolicy.CardBorderColor == "#53666B",
                "app-update-geometry-and-colors-match-wpf-110",
                $"contract={WindowsAppUpdatePolicy.Contract}; " +
                $"card={WindowsAppUpdatePolicy.CardWidth}x" +
                $"{WindowsAppUpdatePolicy.CardMaxHeight}; " +
                $"padding={WindowsAppUpdatePolicy.CardPadding}"),
            Check(
                WindowsAppUpdatePolicy.CompareVersions("1.1.0", "v1.1.0+build.7") == 0 &&
                WindowsAppUpdatePolicy.CompareVersions("1.1.0", "1.1.1") < 0 &&
                WindowsAppUpdatePolicy.CompareVersions("1.1.0", "1.2") < 0 &&
                WindowsAppUpdatePolicy.CompareVersions("1.1.0", "2.0.0") < 0 &&
                WindowsAppUpdatePolicy.CompareVersions("1.1.0", "malformed") > 0 &&
                WindowsAppUpdatePolicy.CompareVersions("2", "1.1.0") < 0,
                "app-update-managed-version-comparison-preserves-wpf-normalization",
                "equal metadata ignored; patch/minor/major advance; malformed and single-component versions normalize to 0.0.0"),
            Check(
                !currentSnapshot.IsUpdateAvailable &&
                !currentSnapshot.BlocksLogin &&
                currentSnapshot.DownloadUri?.AbsoluteUri ==
                "https://example.invalid/current" &&
                currentPresentation?.Title == "已经是最新版本" &&
                currentPresentation.VersionText == $"当前 {currentVersion}  →  最新 {currentVersion}" &&
                currentPresentation.Notice == "当前客户端已经是最新版本。" &&
                currentPresentation.LaterButtonText == "关闭" &&
                currentPresentation.DownloadButtonText == "打开下载链接" &&
                currentPresentation.DownloadEnabled &&
                !currentPresentation.BlocksLogin,
                "app-update-current-version-presentation-matches-wpf-110",
                $"title={currentPresentation?.Title}; version={currentPresentation?.VersionText}; " +
                $"notice={currentPresentation?.Notice}"),
            Check(
                availableSnapshot.IsUpdateAvailable &&
                !availableSnapshot.BlocksLogin &&
                availablePresentation?.Title == "发现新版本" &&
                availablePresentation.VersionText == $"当前 {currentVersion}  →  最新 {availableVersion}" &&
                availablePresentation.Notice == "Windows Avalonia 更新公告。" &&
                availablePresentation.LaterButtonText == "关闭" &&
                availablePresentation.DownloadButtonText == "打开下载链接" &&
                availablePresentation.DownloadEnabled &&
                !availablePresentation.BlocksLogin &&
                httpDownloadSnapshot.DownloadUri?.Scheme == "http" &&
                fileDownloadSnapshot.DownloadUri == null &&
                customSchemeSnapshot.DownloadUri == null,
                "app-update-available-presentation-matches-wpf-110",
                $"title={availablePresentation?.Title}; version={availablePresentation?.VersionText}; " +
                $"notice={availablePresentation?.Notice}; externalSchemes=http/https-only"),
            Check(
                requiredSnapshot.IsUpdateAvailable &&
                requiredSnapshot.BlocksLogin &&
                requiredSnapshot.DownloadUri == null &&
                requiredPresentation?.Title == "必须更新后才能登录" &&
                requiredPresentation.VersionText == $"当前 {currentVersion}  →  最新 {requiredVersion}" &&
                requiredPresentation.Notice == "本次更新暂无公告。" &&
                requiredPresentation.LaterButtonText == "离线使用" &&
                requiredPresentation.DownloadButtonText == "暂未提供链接" &&
                !requiredPresentation.DownloadEnabled &&
                requiredPresentation.BlocksLogin,
                "app-update-minimum-version-blocks-login-but-keeps-offline-mode",
                $"title={requiredPresentation?.Title}; blocks={requiredPresentation?.BlocksLogin}; " +
                $"later={requiredPresentation?.LaterButtonText}"),
            Check(
                WindowsAppUpdatePolicy.Present(
                    currentSnapshot,
                    blocksLogin: false,
                    showWhenCurrent: false) == null &&
                WindowsAppUpdatePolicy.SettingsVersionText(
                    WindowsOnlineAccountHost.ClientVersion,
                    currentSnapshot) ==
                $"当前版本 {currentVersion} · 最新 {currentVersion}" &&
                WindowsAppUpdatePolicy.SettingsVersionText(
                    WindowsOnlineAccountHost.ClientVersion,
                    snapshot: null) == $"当前版本 {currentVersion}",
                "app-update-startup-current-version-stays-silent-and-settings-stays-informative",
                $"startupPrompt=null; configuredSettings=当前版本 {currentVersion} · 最新 {currentVersion}; " +
                $"offlineSettings=当前版本 {currentVersion}"),
            Check(
                failed.Title == "暂时无法验证版本" &&
                failed.VersionText == $"当前版本 {currentVersion}" &&
                failed.Notice ==
                "服务器要求更新，但暂时无法取得更新公告和下载链接。你仍可离线使用，稍后再重试。" &&
                failed.LaterButtonText == "离线使用" &&
                failed.DownloadButtonText == "暂未提供链接" &&
                !failed.DownloadEnabled &&
                failed.BlocksLogin,
                "app-update-required-policy-fetch-failure-preserves-offline-route",
                $"title={failed.Title}; notice={failed.Notice}"),
            Check(
                HasField(typeof(MainWindow), "updateLayer") &&
                HasField(typeof(MainWindow), "aboutLayer") &&
                HasField(typeof(MainWindow), "appUpdateSnapshot") &&
                HasMethod(typeof(MainWindow), "RefreshAppUpdateSnapshotAsync") &&
                HasMethod(typeof(MainWindow), "RefreshUpdatePolicyAndShowAsync") &&
                HasMethod(typeof(MainWindow), "EnsureLoginVersionAllowedAsync") &&
                HasMethod(typeof(MainWindow), "ShowUpdatePromptAsync") &&
                HasField(typeof(OnlineAccountDialog), "clientUpdateRequired") &&
                HasField(
                    typeof(OnlineCommunityDialog),
                    "openAdminConfigurationOnActivate"),
                "app-update-main-shell-login-and-owner-admin-routing-are-wired",
                "main shell hosts about/update layers; startup/manual login share managed gate; owner About action can open content backend directly"),
            Check(
                typeof(MainWindow).Assembly.GetName().Version ==
                    NumericVersion(currentVersion) &&
                typeof(MainWindow).Assembly
                    .GetCustomAttribute<
                        System.Reflection.AssemblyFileVersionAttribute>()?
                    .Version == NumericVersion(currentVersion).ToString(4) &&
                typeof(MainWindow).Assembly
                    .GetCustomAttribute<
                        System.Reflection.AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion.Length > 0,
                "app-update-binary-version-metadata-matches-current-client-protocol",
                $"assembly={typeof(MainWindow).Assembly.GetName().Version}; " +
                $"file={typeof(MainWindow).Assembly.GetCustomAttribute<System.Reflection.AssemblyFileVersionAttribute>()?.Version}; " +
                $"informational={typeof(MainWindow).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion}"),
            Check(
                typeof(WindowsAppUpdatePolicy).Assembly
                    .GetReferencedAssemblies()
                    .All(reference =>
                        !reference.Name!.Contains(
                            "ClientSecurity",
                            StringComparison.OrdinalIgnoreCase)),
                "app-update-policy-has-no-native-clientsecurity-or-ollvm-dependency",
                "pure managed System.Version comparison; no ClientSecurity assembly reference")
        ];
    }

    private static OnlineAppConfigurationDto Configuration(
        string latest,
        string minimum,
        string notice,
        string download) =>
        new(
            "LumaChart Studio",
            latest,
            minimum,
            notice,
            download,
            UpdatedAt);

    private static Version NumericVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var metadata = normalized.IndexOfAny(['-', '+']);
        if (metadata >= 0) normalized = normalized[..metadata];
        return Version.Parse(normalized);
    }

    private static string NextBetaVersion(string currentVersion)
    {
        var current = NumericVersion(currentVersion);
        var revision = Math.Max(0, current.Revision) + 1;
        return $"{current.Major}.{current.Minor}.{current.Build}.{revision}-beta";
    }

    private static string NextMajorVersion(string currentVersion)
    {
        var current = NumericVersion(currentVersion);
        return $"{current.Major + 1}.0.0";
    }

    private static WindowsAppUpdatePolicySpecResult Check(
        bool passed,
        string name,
        string detail) =>
        new(name, passed, detail);

    private static bool HasField(Type type, string name) =>
        type.GetField(
            name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic) != null;

    private static bool HasMethod(Type type, string name) =>
        type.GetMethod(
            name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic) != null;
}

internal sealed record WindowsAppUpdatePolicySpecResult(
    string Name,
    bool Passed,
    string Detail);

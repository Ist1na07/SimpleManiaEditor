namespace LumaChart.Avalonia.Infrastructure;

public static class AvaloniaClientBuildInfo
{
    // Keep the fourth numeric component so consecutive beta packages can be
    // compared by the existing client/server Version policy after the suffix
    // is removed (for example beta 3 becomes 1.2.0.3-beta).
    // 1.3.0.0 ships from one version on all three clients. It changes shared
    // editor, audio, sync and storage code on every platform, so there is no
    // per-platform split. Bring the #if WINDOWS split back the next time only
    // one platform moves.
    public const string ProtocolVersion = "1.3.0.0";
    public const string DisplayVersion = "1.3.0.0";
    public const string Channel = "beta";

    // The platform this client runs on. The server versions Windows, Android and
    // iOS independently — different installers, different build cadence — and
    // resolves the update target, login gate and download link per platform from
    // this. Derived from the same build constants that already split the version:
    // WINDOWS for the desktop project, LUMACHART_IOS for the iOS shared compile,
    // and Android as the remaining shared-compile case.
#if WINDOWS
    public const string Platform = "windows";
#elif LUMACHART_IOS
    public const string Platform = "ios";
#else
    public const string Platform = "android";
#endif
}

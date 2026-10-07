using Avalonia.Controls;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Decides whether an external platform close can bypass the asynchronous
/// interactive close preflight. Windows Restart Manager uses a
/// non-programmatic WM_CLOSE for installed files on current Windows builds.
/// </summary>
internal static class WindowsExternalApplicationShutdownPolicy
{
    public static bool CanCloseImmediately(
        WindowCloseReason closeReason,
        bool isProgrammatic,
        bool hasUnsavedDocuments,
        bool hasBlockingWork,
        bool hasActiveOnlineRoom) =>
        !isProgrammatic &&
        closeReason is WindowCloseReason.WindowClosing or
            WindowCloseReason.ApplicationShutdown or
            WindowCloseReason.OSShutdown &&
        !hasUnsavedDocuments &&
        !hasBlockingWork &&
        !hasActiveOnlineRoom;
}

public static class WindowsClosePreflightResourcePolicy
{
    /// <summary>
    /// Preview resources remain owned by the live editor until every close
    /// confirmation and workspace persistence step has succeeded.
    /// </summary>
    public static bool CanReleaseEditorVisuals(bool teardownConfirmed) =>
        teardownConfirmed;
}

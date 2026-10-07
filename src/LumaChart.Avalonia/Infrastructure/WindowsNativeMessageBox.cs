using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace LumaChart.Avalonia.Infrastructure;

internal enum WindowsNativeMessageBoxIcon : uint
{
    None = 0x00000000,
    Error = 0x00000010,
    Question = 0x00000020,
    Warning = 0x00000030,
    Information = 0x00000040
}

internal enum WindowsNativeMessageBoxButtons : uint
{
    Ok = 0x00000000,
    OkCancel = 0x00000001,
    YesNo = 0x00000004
}

internal sealed record WindowsNativeMessageBoxRequest(
    string Message,
    string Caption,
    WindowsNativeMessageBoxIcon Icon,
    WindowsNativeMessageBoxButtons Buttons = WindowsNativeMessageBoxButtons.Ok);

internal static class WindowsNativeMessageBox
{
    public const string ContractVersion = "wpf-1.1.0-native-message-box-r2";
    public const uint OkButtonFlag = 0x00000000;
    public const int YesResult = 6;

    public static int Show(PlatformWindow owner, WindowsNativeMessageBoxRequest request)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Show(owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, request);
    }

    public static int ShowForLastActivePopup(
        PlatformWindow owner,
        WindowsNativeMessageBoxRequest request)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var ownerHandle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var popupHandle = ownerHandle == IntPtr.Zero
            ? IntPtr.Zero
            : GetLastActivePopup(ownerHandle);
        return Show(
            popupHandle != IntPtr.Zero && IsWindow(popupHandle)
                ? popupHandle
                : ownerHandle,
            request);
    }

    public static int Show(IntPtr ownerHandle, WindowsNativeMessageBoxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Caption);

        if (!OperatingSystem.IsWindows())
        {
            // Returning silently here left every export result, import failure
            // and operation error invisible on platforms without a native
            // message box. No caller reads the result, so a one-way notice is
            // enough to keep them all working.
            Services.PlatformNoticeServices.Current?.Show(
                request.Caption,
                request.Message,
                ResolveSeverity(request.Icon));
            return 0;
        }

        return MessageBox(
            ownerHandle,
            request.Message,
            request.Caption,
            ResolveFlags(request.Icon, request.Buttons));
    }

    private static Services.PlatformNoticeSeverity ResolveSeverity(
        WindowsNativeMessageBoxIcon icon) => icon switch
    {
        WindowsNativeMessageBoxIcon.Error => Services.PlatformNoticeSeverity.Error,
        WindowsNativeMessageBoxIcon.Warning => Services.PlatformNoticeSeverity.Warning,
        WindowsNativeMessageBoxIcon.Question => Services.PlatformNoticeSeverity.Question,
        _ => Services.PlatformNoticeSeverity.Information
    };

    internal static uint ResolveFlags(WindowsNativeMessageBoxIcon icon) =>
        ResolveFlags(icon, WindowsNativeMessageBoxButtons.Ok);

    internal static uint ResolveFlags(
        WindowsNativeMessageBoxIcon icon,
        WindowsNativeMessageBoxButtons buttons) =>
        (uint)buttons | (uint)icon;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        IntPtr ownerHandle,
        string text,
        string caption,
        uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);
}

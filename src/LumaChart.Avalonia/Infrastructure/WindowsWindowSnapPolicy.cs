using System.Runtime.InteropServices;
using Avalonia;

namespace LumaChart.Avalonia.Infrastructure;

public enum WindowsWindowSnapTarget
{
    Left,
    Right,
    Maximize
}

public sealed record WindowsWindowSnapPlacement(
    PixelPoint Position,
    PixelSize PhysicalSize);

public static class WindowsWindowSnapPolicy
{
    public const int EdgeTolerancePixels = 2;
    public const int MoveReleaseFallbackDelayMilliseconds = 250;

    public static bool ShouldApplyAfterMove(
        bool observedPressed,
        bool isPressed,
        TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        return !isPressed &&
               (observedPressed ||
                elapsed >= TimeSpan.FromMilliseconds(
                    MoveReleaseFallbackDelayMilliseconds));
    }

    public static WindowsWindowSnapTarget? ResolveTarget(
        PixelPoint cursor,
        PixelRect workingArea,
        int tolerancePixels = EdgeTolerancePixels)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea));
        if (tolerancePixels < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerancePixels));

        if (cursor.Y <= workingArea.Y + tolerancePixels)
            return WindowsWindowSnapTarget.Maximize;
        if (cursor.X <= workingArea.X + tolerancePixels)
            return WindowsWindowSnapTarget.Left;
        if (cursor.X >= workingArea.Right - 1 - tolerancePixels)
            return WindowsWindowSnapTarget.Right;
        return null;
    }

    public static WindowsWindowSnapPlacement ResolvePlacement(
        WindowsWindowSnapTarget target,
        PixelRect workingArea,
        double scaling,
        double minimumWidthDip,
        double minimumHeightDip)
    {
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea));
        if (!double.IsFinite(scaling) || scaling <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaling));
        if (!double.IsFinite(minimumWidthDip) || minimumWidthDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumWidthDip));
        if (!double.IsFinite(minimumHeightDip) || minimumHeightDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumHeightDip));
        if (target == WindowsWindowSnapTarget.Maximize)
            throw new ArgumentException(
                "Maximize uses WindowState rather than a custom placement.",
                nameof(target));

        var minimumWidthPixels = checked((int)Math.Round(minimumWidthDip * scaling));
        var minimumHeightPixels = checked((int)Math.Round(minimumHeightDip * scaling));
        var width = Math.Min(
            workingArea.Width,
            Math.Max((workingArea.Width + 1) / 2, minimumWidthPixels));
        var height = Math.Min(
            workingArea.Height,
            Math.Max(workingArea.Height, minimumHeightPixels));
        var x = target == WindowsWindowSnapTarget.Left
            ? workingArea.X
            : workingArea.Right - width;
        return new WindowsWindowSnapPlacement(
            new PixelPoint(x, workingArea.Y),
            new PixelSize(width, height));
    }

    public static PixelRect ResolveRestoreUnderPointer(
        PixelPoint cursor,
        PixelRect snappedBounds,
        PixelSize restoreSize,
        PixelRect workingArea,
        int titleOffsetPixels)
    {
        if (snappedBounds.Width <= 0 || snappedBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(snappedBounds));
        if (restoreSize.Width <= 0 || restoreSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(restoreSize));
        if (workingArea.Width <= 0 || workingArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(workingArea));

        var horizontalRatio = Math.Clamp(
            (cursor.X - snappedBounds.X) / (double)snappedBounds.Width,
            0,
            1);
        var x = checked((int)Math.Round(cursor.X - restoreSize.Width * horizontalRatio));
        var y = cursor.Y - Math.Max(0, titleOffsetPixels);
        var minimumVisibleWidth = Math.Min(160, restoreSize.Width);
        x = Math.Clamp(
            x,
            workingArea.X - restoreSize.Width + minimumVisibleWidth,
            workingArea.Right - minimumVisibleWidth);
        y = Math.Clamp(
            y,
            workingArea.Y,
            workingArea.Bottom - Math.Min(38, restoreSize.Height));
        return new PixelRect(x, y, restoreSize.Width, restoreSize.Height);
    }

    public static bool TryGetCursorPosition(out PixelPoint point)
    {
        point = default;
        if (!OperatingSystem.IsWindows() || !GetCursorPos(out var nativePoint))
            return false;
        point = new PixelPoint(nativePoint.X, nativePoint.Y);
        return true;
    }

    public static bool TryGetNativeWindowBounds(
        IntPtr windowHandle,
        out PixelRect bounds)
    {
        bounds = default;
        if (!OperatingSystem.IsWindows() ||
            windowHandle == IntPtr.Zero ||
            !GetWindowRect(windowHandle, out var nativeBounds))
        {
            return false;
        }

        var width = nativeBounds.Right - nativeBounds.Left;
        var height = nativeBounds.Bottom - nativeBounds.Top;
        if (width <= 0 || height <= 0) return false;
        bounds = new PixelRect(
            nativeBounds.Left,
            nativeBounds.Top,
            width,
            height);
        return true;
    }

    public static bool TryApplyNativeWindowBounds(
        IntPtr windowHandle,
        PixelRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        return OperatingSystem.IsWindows() &&
               windowHandle != IntPtr.Zero &&
               SetWindowPos(
                   windowHandle,
                   IntPtr.Zero,
                   bounds.X,
                   bounds.Y,
                   bounds.Width,
                   bounds.Height,
                   SetWindowPositionFlags.NoActivate |
                   SetWindowPositionFlags.NoZOrder);
    }

    public static bool IsPrimaryMouseButtonPressed() =>
        OperatingSystem.IsWindows() &&
        (GetAsyncKeyState(PrimaryMouseButtonVirtualKey) & 0x8000) != 0;

    private const int PrimaryMouseButtonVirtualKey = 0x01;

    [Flags]
    private enum SetWindowPositionFlags : uint
    {
        NoZOrder = 0x0004,
        NoActivate = 0x0010
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr windowHandle,
        out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        SetWindowPositionFlags flags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

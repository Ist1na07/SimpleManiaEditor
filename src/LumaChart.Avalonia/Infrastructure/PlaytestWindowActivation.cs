using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>Transfer focus from this editor to its visible game window, once per launch.</summary>
internal static class PlaytestWindowActivation
{
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);

    internal static int ForegroundProcessId
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return 0;
            GetWindowThreadProcessId(GetForegroundWindow(), out var id);
            return (int)id;
        }
    }

    internal static async Task TransferAsync(Process process, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) return;
        var deadline = Stopwatch.StartNew();
        // SDL Show/Raise are queued on another process's input loop. A grant to
        // that process alone neither activates a hidden window nor verifies focus.
        while (!cancellation.IsCancellationRequested && !process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            var foreground = ForegroundProcessId;
            if (foreground == process.Id) return;
            if (foreground != Environment.ProcessId) return; // Respect a deliberate application switch.
            nint target = 0;
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != process.Id || !IsWindowVisible(window)) return true;
                target = window;
                return false;
            }, 0);
            if (target != 0)
            {
                if (IsIconic(target)) ShowWindow(target, 9); // SW_RESTORE, only for a minimised game.
                SetForegroundWindow(target);
                if (ForegroundProcessId == process.Id) return;
            }
            try { await Task.Delay(25, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}

using System.Runtime.InteropServices;

/// <summary>A real editor-owned native window for cross-process focus assertions.</summary>
internal sealed class ResidentFocusWindow : IDisposable
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Message message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref Message message);
    [StructLayout(LayoutKind.Sequential)] private struct Message
    {
        public nint Window; public uint Id; public nint WParam; public nint LParam;
        public uint Time; public int X; public int Y; public uint Private;
    }
    private readonly Thread thread;
    private readonly TaskCompletionSource<bool> created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? activation;
    private volatile bool stopping;

    internal ResidentFocusWindow()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Playtest focus fixture" };
        thread.Start();
        created.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private void Run()
    {
        var window = CreateWindowExW(0, "STATIC", "SimpleManiaEditor focus verification", 0x10CF0000, 80, 80, 640, 480, 0, 0, 0, 0);
        if (window == 0) { created.TrySetException(new InvalidOperationException("Focus fixture window creation failed.")); return; }
        created.TrySetResult(true);
        try
        {
            while (!stopping)
            {
                if (Interlocked.Exchange(ref activation, null) is { } requested)
                {
                    ShowWindow(window, 9);
                    requested.TrySetResult(SetForegroundWindow(window));
                }
                while (PeekMessageW(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessageW(ref message); }
                Thread.Sleep(10);
            }
        }
        finally { DestroyWindow(window); }
    }

    internal async Task ActivateAsync()
    {
        var requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref activation, requested);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (LumaChart.Avalonia.Infrastructure.PlaytestWindowActivation.ForegroundProcessId != Environment.ProcessId)
        {
            Console.WriteLine("Waiting for an interactive click on SimpleManiaEditor focus verification (45 seconds); Windows denied automatic foreground activation.");
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (LumaChart.Avalonia.Infrastructure.PlaytestWindowActivation.ForegroundProcessId != Environment.ProcessId && deadline.Elapsed < TimeSpan.FromSeconds(45))
                await Task.Delay(50);
        }
        if (LumaChart.Avalonia.Infrastructure.PlaytestWindowActivation.ForegroundProcessId != Environment.ProcessId)
            throw new InvalidOperationException("Editor fixture did not acquire foreground focus.");
    }

    public void Dispose() { stopping = true; thread.Join(TimeSpan.FromSeconds(3)); }
}

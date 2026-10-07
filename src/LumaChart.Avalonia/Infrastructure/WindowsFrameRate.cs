using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Windows editor frame-rate choices retained from WPF 1.1.0.
/// </summary>
public static class WindowsFrameRateOptions
{
    private static readonly int[] Values = [30, 60, 90, 120, 144, 165, 240, 330, 1000];

    public const int Default = 165;

    public static IReadOnlyList<int> Presets => Values;

    public static int Normalize(int value) =>
        Values.MinBy(candidate => Math.Abs((long)candidate - value));

    /// <summary>
    /// Presets the panel can actually present. Asking for more than the refresh
    /// rate buys nothing: the surplus ticks fall inside one vsync interval, so
    /// the editor paces worse than it would at the panel rate, and the highest
    /// preset also drives a frame pump that destabilises the Android render
    /// thread. A null rate means the platform does not report one and the full
    /// desktop list stands.
    /// </summary>
    public static IReadOnlyList<int> PresentablePresets(double? maximumRefreshRate)
    {
        if (maximumRefreshRate is not { } refresh ||
            !double.IsFinite(refresh) ||
            refresh <= 0)
            return Values;
        // Panels report rates such as 119.98; round before comparing so the
        // matching preset is not filtered out by a fraction of a hertz.
        var cap = (int)Math.Round(refresh);
        var allowed = Values.Where(candidate => candidate <= cap).ToArray();
        return allowed.Length > 0 ? allowed : [Values[0]];
    }

    public static int ClampToPresentable(int value, double? maximumRefreshRate)
    {
        var allowed = PresentablePresets(maximumRefreshRate);
        var normalized = Normalize(value);
        return normalized <= allowed[^1] ? normalized : allowed[^1];
    }
}

/// <summary>
/// Refresh rate of the display the editor presents on. The host sets this when
/// the platform reports one; Windows leaves it unset and keeps the full list.
/// </summary>
public static class PlatformDisplayRefreshRates
{
    public static double? Current { get; set; }
}

/// <summary>
/// Caps UI-side scene updates while leaving audio on its independent device
/// clock. The tolerance and late-frame recovery match WPF 1.1.0.
/// </summary>
internal sealed class WindowsFrameRateGate
{
    private readonly Stopwatch clock;
    private readonly Func<double> readMilliseconds;
    private readonly bool ownsClock;
    private double nextFrameMilliseconds;

    public WindowsFrameRateGate(int limit)
        : this(limit, null)
    {
    }

    internal WindowsFrameRateGate(int limit, Func<double>? readMilliseconds)
    {
        clock = Stopwatch.StartNew();
        ownsClock = readMilliseconds == null;
        this.readMilliseconds = readMilliseconds ?? (() => clock.Elapsed.TotalMilliseconds);
        SetLimit(limit);
    }

    public int Limit { get; private set; }

    public void SetLimit(int value)
    {
        Limit = WindowsFrameRateOptions.Normalize(value);
        nextFrameMilliseconds = 0;
        if (ownsClock) clock.Restart();
    }

    public bool ShouldRender()
    {
        var now = readMilliseconds();
        var interval = 1000d / Limit;
        var earlyTolerance = Math.Min(1, interval * .15);
        if (nextFrameMilliseconds > 0 && now + earlyTolerance < nextFrameMilliseconds)
            return false;

        if (nextFrameMilliseconds <= 0 || now - nextFrameMilliseconds > interval * 4)
        {
            nextFrameMilliseconds = now + interval;
            return true;
        }

        do nextFrameMilliseconds += interval;
        while (nextFrameMilliseconds <= now);
        return true;
    }
}

/// <summary>
/// Windows high-resolution engine clock used for rates above 165 FPS. It only
/// samples engine state; Avalonia visual invalidation remains on the UI thread.
/// </summary>
internal sealed class WindowsHighRateFramePump : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;
    private const uint WaitFailed = 0xFFFFFFFF;
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Thread? thread;
    private long intervalTicks;
    private long totalTicks;

    public bool IsRunning
    {
        get
        {
            lock (sync) return thread is { IsAlive: true };
        }
    }

    public int FramesPerSecond { get; private set; }

    public void Start(int framesPerSecond, Action update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Stop();
        framesPerSecond = WindowsFrameRateOptions.Normalize(framesPerSecond);
        var source = new CancellationTokenSource();
        var worker = new Thread(() => Run(framesPerSecond, update, source.Token))
        {
            IsBackground = true,
            Name = "LumaChart Avalonia high-rate engine clock",
            // The high-rate clock samples audio/editor state only. Giving a
            // 1000 Hz sampler priority over Avalonia's composition thread makes
            // a high preset look less smooth on refresh-limited displays.
            Priority = ThreadPriority.Normal
        };
        lock (sync)
        {
            cancellation = source;
            thread = worker;
            FramesPerSecond = framesPerSecond;
            Interlocked.Exchange(ref intervalTicks, 0);
            Interlocked.Exchange(ref totalTicks, 0);
        }
        worker.Start();
    }

    public long TakeTickCount() => Interlocked.Exchange(ref intervalTicks, 0);

    public long TotalTickCount => Interlocked.Read(ref totalTicks);

    public void Stop()
    {
        CancellationTokenSource? source;
        Thread? worker;
        lock (sync)
        {
            source = cancellation;
            worker = thread;
            cancellation = null;
            thread = null;
            FramesPerSecond = 0;
        }
        if (source == null) return;
        source.Cancel();
        if (worker is { IsAlive: true } && worker != Thread.CurrentThread)
            worker.Join(250);
        source.Dispose();
        Interlocked.Exchange(ref intervalTicks, 0);
    }

    private void Run(int framesPerSecond, Action update, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var interval = Stopwatch.Frequency / (double)framesPerSecond;
        double next = clock.ElapsedTicks;
        // The high-resolution waitable timer is a Win32 object and this call sits
        // outside the try below, on a bare background thread. Reaching it on iOS
        // meant a DllNotFoundException with nothing to catch it, which terminates
        // the process — and the trigger was ordinary: the pump starts whenever
        // the frame-rate limit is above the default and playback is running, and
        // the iOS host itself reports a ProMotion 120Hz panel for that list.
        //
        // Everything below already copes with not having one: WaitPrecisely
        // returns false for a null handle and the loop falls back to Stopwatch
        // pacing with Thread.Sleep, so the pump keeps its behaviour off Windows
        // and only loses the sub-millisecond wait.
        var waitableTimer = OperatingSystem.IsWindows()
            ? CreateWaitableTimerEx(
                IntPtr.Zero,
                null,
                CreateWaitableTimerHighResolution,
                TimerModifyState | Synchronize)
            : IntPtr.Zero;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = clock.ElapsedTicks;
                var remaining = next - now;
                if (remaining > 0)
                {
                    if (!WaitPrecisely(waitableTimer, remaining))
                        Thread.Sleep(remaining * 1000d / Stopwatch.Frequency >= 1 ? 1 : 0);
                    continue;
                }

                try
                {
                    update();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch
                {
                    // A transient chart/audio handoff must not terminate the app.
                }
                Interlocked.Increment(ref intervalTicks);
                Interlocked.Increment(ref totalTicks);

                next += interval;
                if (now - next > interval * 4) next = now + interval;
            }
        }
        finally
        {
            if (waitableTimer != IntPtr.Zero) CloseHandle(waitableTimer);
        }
    }

    private static bool WaitPrecisely(IntPtr timer, double remainingStopwatchTicks)
    {
        if (timer == IntPtr.Zero) return false;
        var dueTime = -(long)Math.Max(
            1,
            Math.Ceiling(remainingStopwatchTicks * 10_000_000d / Stopwatch.Frequency));
        if (!SetWaitableTimerEx(
                timer,
                ref dueTime,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                0))
            return false;
        return WaitForSingleObject(timer, 100) != WaitFailed;
    }

    public void Dispose() => Stop();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(
        IntPtr timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(
        IntPtr timer,
        ref long dueTime,
        int period,
        IntPtr completionRoutine,
        IntPtr completionArgument,
        IntPtr wakeContext,
        uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

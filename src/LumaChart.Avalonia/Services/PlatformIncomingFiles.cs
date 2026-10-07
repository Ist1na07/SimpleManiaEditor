using Avalonia.Threading;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Charts the operating system hands to the app from outside it — "open with
/// LumaChart" from Files, a browser, or a chat app.
///
/// The delivery and the consumer never line up in time. iOS calls the host with
/// the URL whenever it likes, including during launch, and the editor that knows
/// how to open a chart does not exist until Avalonia has finished building the
/// main window. A cold launch is precisely the case a naive "call the editor"
/// implementation drops on the floor, and it is also the common one: tapping a
/// .mcz in Files when the app is not already running.
///
/// So this is a queue with a late-bound consumer. The host delivers whenever it
/// is told to; the editor registers when it is ready and immediately drains
/// whatever arrived first.
///
/// Windows leaves this unused — it opens files through the command line — and
/// nothing here runs unless a host calls <see cref="Deliver"/>.
/// </summary>
public static class PlatformIncomingFiles
{
    private static readonly object Gate = new();
    private static readonly List<string> Pending = [];
    private static Func<string, Task>? handler;

    /// <summary>
    /// Called by the platform host with a path it already owns. The file must
    /// be app-private and durable: on iOS the URL the system supplies is
    /// security-scoped and stops resolving once the scope closes, so the host
    /// copies the bytes before calling this.
    /// </summary>
    public static void Deliver(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        Func<string, Task>? active;
        lock (Gate)
        {
            active = handler;
            if (active == null)
            {
                Pending.Add(path);
                return;
            }
        }

        Invoke(active, path);
    }

    /// <summary>
    /// Registered by the editor once it can open a chart. Anything delivered
    /// before now is replayed in arrival order.
    /// </summary>
    public static void SetHandler(Func<string, Task> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string[] replay;
        lock (Gate)
        {
            handler = value;
            replay = [.. Pending];
            Pending.Clear();
        }

        foreach (var path in replay) Invoke(value, path);
    }

    /// <summary>
    /// Dropped when the editor goes away, so a handler bound to a torn-down
    /// window is never called and later arrivals queue for the next one.
    /// </summary>
    public static void ClearHandler()
    {
        lock (Gate) handler = null;
    }

    private static void Invoke(Func<string, Task> active, string path)
    {
        // Always on the UI thread: opening a chart builds visuals, and the host
        // calls Deliver from whatever thread iOS used.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _ = active(path);
            }
            catch (Exception)
            {
                // The editor reports its own import failures. A throw here would
                // reach the dispatcher as an unhandled exception, which on iOS
                // ends the process.
            }
        });
    }
}

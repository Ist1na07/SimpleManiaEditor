using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;

/// <summary>Real Windows/ANGLE benchmark, isolated from user maps and settings.</summary>
internal static class EditorRenderBenchmark
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint id);
    public static int Run(string root, string? output, string? backend = null)
    {
        Exception? failure = null;
        using var stop = new CancellationTokenSource();
        var options = (Win32PlatformOptions)typeof(MainWindow).Assembly.GetType("LumaChart.Avalonia.Infrastructure.WindowsRenderingConfiguration")!
            .GetMethod("Create")!.Invoke(null, null)!;
        if (backend == "direct") options.CompositionMode = [Win32CompositionMode.DirectComposition];
        if (backend == "swapchain") options.CompositionMode = [Win32CompositionMode.LowLatencyDxgiSwapChain];
        if (backend == "redirect") options.CompositionMode = [Win32CompositionMode.RedirectionSurface];
        if (backend == "wgl") { options.RenderingMode = [Win32RenderingMode.Wgl, Win32RenderingMode.Software]; options.CompositionMode = [Win32CompositionMode.RedirectionSurface]; }
        AppBuilder.Configure<TestApplication>().UseWin32().UseSkia().UseHarfBuzz().With((Win32PlatformOptions)options).SetupWithoutStarting();
        PlatformAudioOutputs.Current = new SimulatedAudioOutputFactory();
        var window = new MainWindow([]) { Width = 1440, Height = 960 };
        window.Show();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Task.Delay(500);
                var model = Field<MainWindowViewModel>(window, "viewModel");
                await model.CreateAsync("Rendering benchmark");
                var show = Method("ShowEditorSurface"); show.Invoke(window, show.GetParameters().Select(p => p.DefaultValue).ToArray());
                var session = model.ActiveTab!.Session;
                session.Mutate(doc =>
                {
                    var count = int.TryParse(Environment.GetEnvironmentVariable("SME_RENDER_NOTES"), out var requested) ? requested : 50000;
                    for (var i = 0; i < count; i++) doc.Notes.Add(new ChartNote { Lane = i % 7, Beat = BeatPosition.FromDouble(i / 8d),
                        EndBeat = i % 17 == 0 ? BeatPosition.FromDouble(i / 8d + .5) : null });
                });
                var timeline = Field<TimelineSurface>(window, "timeline");
                timeline.NavigateToBeat(200);
                await (Task)Method("ToggleAudioPlaybackAsync").Invoke(window, null)!;
                await Task.Delay(3000);
                timeline.Performance.Reset();
                var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
                GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
                var clock = Stopwatch.StartNew();
                var before = timeline.CurrentBeat;
                await Task.Delay(6000);
                var duration = clock.Elapsed.TotalSeconds;
                var stats = timeline.Performance.Snapshot();
                var report = new { NoteCount = session.Document.Notes.Count, Seconds = duration,
                    Active = window.IsActive, ForegroundProcessId = foreground, OwnProcessId = Environment.ProcessId,
                    CpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds,
                    Loop = RenderLoopInfo(),
                    Screens = window.Screens.All.Select(s => new { s.DisplayName, s.Bounds,
                        Frequency = s.GetType().GetProperty("Frequency", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(s) }),
                    Backend = backend ?? "default",
                    RenderedFps = stats.RenderMilliseconds.Samples / duration, ProgressBeats = timeline.CurrentBeat - before,
                    Statistics = stats, Status = Field<TextBlock>(window, "frameRateStatusText").Text };
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
                File.WriteAllText(output ?? Path.Combine(root, "render-benchmark.json"), json);
                await (Task)Method("PauseAllEditorPlaybackAsync").Invoke(window, null)!;
                if (stats.RenderMilliseconds.Samples < 60 || timeline.CurrentBeat <= before)
                    throw new InvalidOperationException("Real editor playback/rendering stalled.");
                session.TryMarkSaved(session.ChangeSerial);
            }
            catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
            finally { window.Close(); stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return failure == null ? 0 : 1;
    }
    private static MethodInfo Method(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object RenderLoopInfo()
    {
        var locator = typeof(AvaloniaLocator).GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        var loop = locator.GetType().GetMethod("GetService", [typeof(Type)])!.Invoke(locator, [typeof(Avalonia.Rendering.IRenderLoop)])!;
        var timer = loop.GetType().GetProperty("Timer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(loop);
        return new { Type = loop.GetType().FullName, Timer = timer?.GetType().FullName,
            DesiredFps = timer?.GetType().GetProperty("DesiredFps")?.GetValue(timer) };
    }
}

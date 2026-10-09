using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using SimpleManiaEditor.PlaytestBridge;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>One editor-owned game process. JSON messages travel only over inherited pipes.</summary>
internal sealed class OfficialPlaytestProcess : IOfficialPlaytestProcess
{
    private readonly string? executableOverride;
    private readonly bool verify;
    internal OfficialPlaytestProcess(string? executable = null, bool verify = false)
    { executableOverride = executable; this.verify = verify; }
    private readonly SemaphoreSlim startGate = new(1);
    private readonly SemaphoreSlim requestGate = new(1);
    private Process? child;
    private TaskCompletionSource<bool>? warm;
    private TaskCompletionSource<bool>? completed;
    private string? activePath;
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
    internal double LastReadyMilliseconds { get; private set; }
    internal int? ProcessId => child is { HasExited: false } ? child.Id : null;
    internal event Action? BecameReady;
    private string Executable => executableOverride ?? Path.Combine(AppContext.BaseDirectory, "osu-host", "SimpleManiaEditor.OsuHost.exe");

    internal async Task PrewarmAsync()
    {
        if (!File.Exists(Executable)) return; // Unpublished development/headless clients.
        await EnsureStartedAsync();
    }

    private async Task EnsureStartedAsync()
    {
        await startGate.WaitAsync();
        try
        {
            if (child is { HasExited: false }) { await warm!.Task.WaitAsync(TimeSpan.FromSeconds(90)); return; }
            if (!File.Exists(Executable)) throw new FileNotFoundException("官方试玩组件缺失。请使用完整便携包。", Executable);
            child?.Dispose();
            var root = Path.Combine(AvaloniaAppPaths.SessionScratchRoot, "official-playtest", "warm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var settings = WindowsPlaytestSettingsStore.Load();
            var bootstrap = new PlaytestRequest
            {
                SettingsOnly = true, WarmupOnly = true, DataRoot = Path.Combine(AvaloniaAppPaths.LocalDataRoot, "osu-host"),
                SkinPaths = OsuSkinLibrary.List(settings.SkinPath).Where(s => s.Available && s.Path.Length > 0).Select(s => s.Path).ToArray(),
                SelectedSkinPath = settings.SkinPath, ManiaSpeed = settings.ManiaSpeed, FullScreen = settings.FullScreen,
                UpsideDown = settings.UpsideDown, InputOffsetMilliseconds = settings.InputOffsetMilliseconds,
                Volume = verify ? 0 : WindowsEditorViewSettingsStore.Load().MusicVolume,
                KeyBindings = settings.KeyBindings.ToDictionary(p => p.Key, p => p.Value.ToArray())
            };
            var path = Path.Combine(root, "bootstrap.json"); bootstrap.Write(path);
            warm = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var info = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(Executable)!
            };
            info.ArgumentList.Add("--request"); info.ArgumentList.Add(path); info.ArgumentList.Add("--resident");
            if (verify) info.ArgumentList.Add("--verify");
            var process = Process.Start(info) ?? throw new IOException("官方试玩组件未能启动。");
            child = process;
            _ = ReadEventsAsync(process, warm);
            _ = process.StandardError.ReadToEndAsync();
            await warm.Task.WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch { await ShutdownAsync(); child = null; throw; }
        finally { startGate.Release(); }
    }

    private async Task ReadEventsAsync(Process process, TaskCompletionSource<bool> warming)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                var message = ResidentMessage.Parse(line);
                if (message?.Kind == "warm") warming.TrySetResult(true);
                if (message?.Kind == "error" && !warming.Task.IsCompleted) warming.TrySetException(new IOException(message.Detail));
                if (message?.Path != activePath) continue;
                if (message?.Kind == "ready") { LastReadyMilliseconds = message.Milliseconds; BecameReady?.Invoke(); }
                if (message?.Kind == "done") completed?.TrySetResult(true);
                if (message?.Kind == "error") completed?.TrySetException(new IOException(message.Detail));
            }
        }
        catch (Exception exception) { warming.TrySetException(exception); completed?.TrySetException(exception); }
        finally
        {
            var failure = new IOException("官方试玩宿主已退出；再次点击会重新启动。");
            warming.TrySetException(failure);
            if (ReferenceEquals(child, process)) completed?.TrySetException(failure);
        }
    }

    public async Task RunAsync(string requestPath)
    {
        await requestGate.WaitAsync();
        using var activationCancellation = new CancellationTokenSource();
        Task activation = Task.CompletedTask;
        try
        {
            await EnsureStartedAsync();
            activePath = requestPath;
            completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(child!.Id);
            await child!.StandardInput.WriteLineAsync(new ResidentMessage("open", requestPath).ToJson());
            await child.StandardInput.FlushAsync();
            activation = PlaytestWindowActivation.TransferAsync(child, activationCancellation.Token);
            await completed.Task;
        }
        finally
        {
            activationCancellation.Cancel();
            try { await activation; }
            finally { activePath = null; completed = null; requestGate.Release(); }
        }
    }

    internal async Task ShutdownAsync()
    {
        var process = child;
        if (process == null || process.HasExited) return;
        try
        {
            await process.StandardInput.WriteLineAsync(new ResidentMessage("shutdown").ToJson());
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { if (!process.HasExited) process.Kill(); }
    }
    internal void InterruptForVerification()
    {
        if (!verify) throw new InvalidOperationException("Only an isolated test process may be interrupted.");
        if (child is { HasExited: false }) child.Kill();
    }
}

using System.Diagnostics;
using LumaChart.Application.Exporting;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using SimpleManiaEditor.PlaytestBridge;

namespace LumaChart.Avalonia.Infrastructure;

internal interface IOfficialPlaytestProcess
{
    Task RunAsync(string requestPath);
}

internal static class OfficialPlaytestLauncher
{
    internal static IOfficialPlaytestProcess ProcessRunner { get; set; } = new OfficialPlaytestProcess();

    public static async Task RunAsync(ChartDocument snapshot, string sourcePath, double startBeat,
        WindowsPlaytestSettings settings, double rate, bool preservePitch, double volume, bool settingsOnly = false)
    {
        var scratch = Path.Combine(AvaloniaAppPaths.SessionScratchRoot, "official-playtest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var practice = snapshot.Clone();
        var timing = new TimingMap(practice.Timing);
        var start = Math.Max(0, timing.BeatToMilliseconds(BeatPosition.FromDouble(Math.Max(0, startBeat))));
        // Slice the disposable copy, so prior objects are not missed/scored when starting mid-song.
        // Timing, SV, background, full audio and hitsounds retain their original timestamps.
        OsuFullPackageExportResult? package = null;
        if (!settingsOnly)
        {
            practice.Notes.RemoveAll(n => n.IsDraft || timing.BeatToMilliseconds(n.Beat) < start - .01);
            if (practice.Notes.Count == 0) throw new InvalidOperationException("当前位置之后没有可试玩的正式按键。");
            package = await OsuFullPackageExportService.ExportAsync(new(practice, sourcePath,
                Path.Combine(scratch, "practice.osz"), scratch, practice.Metadata.OverallDifficulty, practice.Metadata.HpDrainRate));
        }
        var skinPaths = OsuSkinLibrary.List(settings.SkinPath).Where(s => s.Path.Length > 0 && s.Available).Select(s => s.Path).ToList();
        var incomingSkins = Path.Combine(AvaloniaAppPaths.LocalDataRoot, "official-skin-imports");
        if (Directory.Exists(incomingSkins)) skinPaths.AddRange(Directory.EnumerateFiles(incomingSkins, "*.osk"));
        var settingsImportPath = Path.Combine(AvaloniaAppPaths.LocalDataRoot, "official-settings-import.txt");
        var request = new PlaytestRequest
        {
            BeatmapArchive = package?.DestinationArchivePath ?? "", ChartEntry = package?.MainChartEntryPath ?? "", SettingsOnly = settingsOnly,
            DataRoot = Path.Combine(AvaloniaAppPaths.LocalDataRoot, "osu-host"), StartMilliseconds = start,
            Rate = rate, PreservePitch = preservePitch, Volume = volume, AutoPlay = settings.AutoPlay,
            ManiaSpeed = settings.ManiaSpeed, UpsideDown = settings.UpsideDown, FullScreen = settings.FullScreen,
            InputOffsetMilliseconds = settings.InputOffsetMilliseconds, SelectedSkinPath = settings.SkinPath,
            SkinPaths = skinPaths.ToArray(), KeyBindings = settings.KeyBindings.ToDictionary(p => p.Key, p => p.Value.ToArray()),
            SettingsImportRevision = File.Exists(settingsImportPath) ? File.ReadAllText(settingsImportPath).Trim() : ""
        };
        // Ensure the active key count exists even when the old settings only contain custom layouts.
        request.KeyBindings[snapshot.KeyCount] = settings.BindingsFor(snapshot.KeyCount);
        var requestPath = Path.Combine(scratch, "request.json");
        request.Write(requestPath);
        await ProcessRunner.RunAsync(requestPath);
    }
}

internal sealed class OfficialPlaytestProcess : IOfficialPlaytestProcess
{
    public async Task RunAsync(string requestPath)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "osu-host", "SimpleManiaEditor.OsuHost.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("官方试玩组件缺失。请使用完整便携包，或运行 scripts/Publish.ps1 构建。", executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
        using var process = Process.Start(start) ?? throw new IOException("官方试玩组件未能启动。");
        await process.WaitForExitAsync();
        var error = requestPath + ".error.txt";
        if (process.ExitCode != 0 || File.Exists(error))
            throw new IOException(File.Exists(error) ? File.ReadAllText(error) : $"官方试玩组件异常退出（{process.ExitCode}）。");
        if (!File.Exists(requestPath + ".ready.json")) throw new IOException("官方试玩尚未完成加载。日志位于独立 osu-host/logs 目录。");
    }
}

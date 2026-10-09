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
        var start = Math.Max(0, timing.BeatToMilliseconds(BeatPosition.FromDouble(startBeat)));
        // Slice the disposable copy, so prior objects are not missed/scored when starting mid-song.
        // Timing, SV, background, full audio and hitsounds retain their original timestamps.
        OsuFullPackageExportResult? package = null;
        if (!settingsOnly)
        {
            package = await OfficialPlaytestPackageCache.PrepareAsync(practice, sourcePath, start, scratch);
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

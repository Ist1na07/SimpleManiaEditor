using System.IO.Compression;
using LumaChart.Application.Exporting;
using LumaChart.Application.Libraries;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>Cache compressed assets, rewriting only the disposable .osu entry after edits.
/// Uses the exporter's normalized references; never reimplements resource resolution.</summary>
internal static class OfficialPlaytestPackageCache
{
    private static readonly SemaphoreSlim Gate = new(1);
    private static readonly List<FileSystemWatcher> Watchers = [];
    private static string key = "";
    private static long assetRevision;
    private static long cachedRevision = -1;
    private static OsuFullPackageExportResult? template;
    private static Dictionary<string, string> references = [];
    internal static int ResourceBuildCount { get; private set; }
    private static volatile bool reliable;

    internal static async Task<OsuFullPackageExportResult> PrepareAsync(ChartDocument snapshot, string sourcePath,
        double startMilliseconds, string scratch)
    {
        await Gate.WaitAsync();
        try
        {
            var document = snapshot.Clone(); document.FilePath ??= sourcePath; document.Notes.RemoveAll(n => n.IsDraft);
            var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
            var workingRoot = Path.GetDirectoryName(Path.GetFullPath(document.FilePath))!;
            var resourceNames = new[] { document.Metadata.AudioFile, document.Metadata.BackgroundFile, document.Metadata.CoverFile }
                .Concat(document.Notes.Select(n => n.Sound ?? "")).Where(s => s.Length > 0).Distinct().Order(StringComparer.Ordinal);
            var nextKey = string.Join('\n', new[] { sourcePath, document.FilePath, AvaloniaAppPaths.SessionScratchRoot }.Concat(resourceNames));
            if (key != nextKey)
            {
                foreach (var watcher in Watchers) watcher.Dispose(); Watchers.Clear();
                key = nextKey; template = null; references = []; reliable = true; Interlocked.Increment(ref assetRevision);
                foreach (var root in new[] { sourceRoot, workingRoot }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(root) || DesktopLibraryDeletionPlanner.IsProtectedFolder(root)) { reliable = false; continue; }
                    var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    watcher.Changed += AssetChanged; watcher.Created += AssetChanged; watcher.Deleted += AssetChanged;
                    watcher.Renamed += (_, _) => Interlocked.Increment(ref assetRevision);
                    watcher.Error += (_, _) => { reliable = false; Interlocked.Increment(ref assetRevision); };
                    watcher.EnableRaisingEvents = true; Watchers.Add(watcher);
                }
            }
            var revision = Interlocked.Read(ref assetRevision);
            if (template == null || cachedRevision != revision || !File.Exists(template.DestinationArchivePath) || !reliable)
            {
                var cacheRoot = Path.Combine(AvaloniaAppPaths.SessionScratchRoot, "official-playtest", "assets-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(cacheRoot);
                ChartDocument? normalized = null;
                template = await OsuFullPackageExportService.ExportAsync(new(document, sourcePath,
                    Path.Combine(cacheRoot, "template.osz"), cacheRoot, document.Metadata.OverallDifficulty, document.Metadata.HpDrainRate,
                    CaptureNormalizedDocument: result => normalized = result));
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                void Remember(string from, string to) { if (from.Length > 0) map[from] = to; }
                Remember(document.Metadata.AudioFile, normalized!.Metadata.AudioFile);
                Remember(document.Metadata.BackgroundFile, normalized.Metadata.BackgroundFile);
                Remember(document.Metadata.CoverFile, normalized.Metadata.CoverFile);
                var normalizedNotes = normalized.Notes.ToDictionary(n => n.Id);
                foreach (var note in document.Notes)
                    if (note.Sound is { Length: > 0 } sound && normalizedNotes.TryGetValue(note.Id, out var target)) Remember(sound, target.Sound ?? "");
                references = map; cachedRevision = revision; ResourceBuildCount++;
            }
            string Normalize(string name) => references.GetValueOrDefault(name, name);
            document.Metadata.AudioFile = Normalize(document.Metadata.AudioFile);
            document.Metadata.BackgroundFile = Normalize(document.Metadata.BackgroundFile);
            document.Metadata.CoverFile = Normalize(document.Metadata.CoverFile);
            foreach (var note in document.Notes) if (note.Sound != null) note.Sound = Normalize(note.Sound);
            var timing = new TimingMap(document.Timing);
            document.Notes.RemoveAll(n => timing.BeatToMilliseconds(n.Beat) < startMilliseconds - .01);
            if (document.Notes.Count == 0) throw new InvalidOperationException("当前位置之后没有可试玩的正式按键。");
            var destination = Path.Combine(scratch, "practice.osz");
            var chart = Path.Combine(scratch, "practice.osu");
            // Both copying compressed bytes and serializing happen off the UI thread.
            await Task.Run(() =>
            {
                File.Copy(template.DestinationArchivePath, destination);
                OsuManiaSerializer.Save(document, chart);
                using var zip = ZipFile.Open(destination, ZipArchiveMode.Update);
                zip.GetEntry(template.MainChartEntryPath)?.Delete();
                zip.CreateEntryFromFile(chart, template.MainChartEntryPath, CompressionLevel.Fastest);
            });
            return template with { DestinationArchivePath = destination, ArchiveBytes = new FileInfo(destination).Length };
        }
        finally { Gate.Release(); }
    }

    private static void AssetChanged(object sender, FileSystemEventArgs e)
    {
        var extension = Path.GetExtension(e.FullPath);
        if (extension.Equals(".osu", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mc", StringComparison.OrdinalIgnoreCase)) return;
        if (e.FullPath.StartsWith(AvaloniaAppPaths.SessionScratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.Increment(ref assetRevision);
    }
}

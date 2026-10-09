using System.IO.Compression;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class PlaytestPackageCacheSpecs
{
    public static void Run(string root)
    {
        var source = Path.Combine(root, "package-source");
        var working = Path.Combine(root, "package-working");
        Directory.CreateDirectory(source); Directory.CreateDirectory(working);
        Program.WriteSimulationWave(Path.Combine(source, "song.wav"));
        Program.WriteSimulationWave(Path.Combine(working, "song.wav"));
        Directory.CreateDirectory(Path.Combine(working, "samples"));
        File.Copy(Path.Combine(working, "song.wav"), Path.Combine(working, "samples", "hit.wav"));
        var chart = new ChartDocument { FilePath = Path.Combine(working, "working.osu"), KeyCount = 7 };
        chart.Metadata.AudioFile = "song.wav";
        chart.Notes.Add(new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(2), Sound = "samples/hit.wav" });
        chart.Notes.Add(new ChartNote { Lane = 3, Beat = BeatPosition.FromDouble(6), EndBeat = BeatPosition.FromDouble(8) });
        OsuManiaSerializer.Save(chart, chart.FilePath!);
        var sourcePath = Path.Combine(source, "map.osu");
        File.Copy(chart.FilePath!, sourcePath);
        var sourceHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath));
        var before = OfficialPlaytestPackageCache.ResourceBuildCount;
        var first = Prepare(chart, sourcePath, 0, root);
        var firstDocument = Read(first, root);
        Require(firstDocument.Metadata.AudioFile != "song.wav", "fixture did not exercise normalized resource collision");
        var baseline = OfficialPlaytestPackageCache.ResourceBuildCount;
        Require(baseline == before + 1, "asset template was not built once");
        chart.Metadata.Title = "Latest unsaved edit";
        chart.Notes.Add(new ChartNote { Lane = 6, Beat = BeatPosition.FromDouble(9), Sound = "samples/hit.wav" });
        chart.Timing.Add(new TimingEvent { Beat = BeatPosition.FromDouble(4), Bpm = 180 });
        var edited = Read(Prepare(chart, sourcePath, 0, root), root);
        Require(OfficialPlaytestPackageCache.ResourceBuildCount == baseline, "note/timing edits recompressed unchanged assets");
        Require(edited.Metadata.Title == chart.Metadata.Title && edited.Notes.Count == 3 && edited.Timing.Any(t => Math.Abs((t.Bpm ?? 0) - 180) < .001), "cached assets lost current note/timing/metadata edits");
        var cut = Read(Prepare(chart, sourcePath, 2000, root), root);
        Require(cut.Notes.Count == 2 && cut.Notes.All(n => n.Lane != 0), "current-time copy retained past notes or lost future notes");
        var audioPath = Path.Combine(working, "song.wav");
        using (var stream = new FileStream(audioPath, FileMode.Open, FileAccess.Write)) { stream.Position = stream.Length - 2; stream.Write([0x10, 0x20]); }
        Program.Pump(300);
        var updated = Prepare(chart, sourcePath, 0, root);
        Require(OfficialPlaytestPackageCache.ResourceBuildCount == baseline + 1, "changed song audio did not invalidate cached assets");
        using (var zip = ZipFile.OpenRead(updated.DestinationArchivePath))
        {
            var entry = zip.GetEntry(Read(updated, root).Metadata.AudioFile)!;
            using var bytes = new MemoryStream(); using var data = entry.Open(); data.CopyTo(bytes);
            Require(bytes.ToArray().SequenceEqual(File.ReadAllBytes(audioPath)), "cache reused stale song audio");
        }
        Require(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath)).SequenceEqual(sourceHash), "practice cache wrote the source map");
        Console.WriteLine("PASS compressed asset reuse after note/timing edits, normalized references, current-time slicing, resource invalidation and source isolation");
    }

    private static LumaChart.Application.Exporting.OsuFullPackageExportResult Prepare(ChartDocument chart, string source, double start, string root)
    {
        var scratch = Path.Combine(root, "practice-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        var task = OfficialPlaytestPackageCache.PrepareAsync(chart, source, start, scratch);
        Program.PumpUntil(() => task.IsCompleted); return task.GetAwaiter().GetResult();
    }
    private static ChartDocument Read(LumaChart.Application.Exporting.OsuFullPackageExportResult package, string root)
    {
        using var zip = ZipFile.OpenRead(package.DestinationArchivePath);
        var path = Path.Combine(root, "read-" + Guid.NewGuid().ToString("N") + ".osu");
        zip.GetEntry(package.MainChartEntryPath)!.ExtractToFile(path);
        return OsuManiaSerializer.Load(path);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

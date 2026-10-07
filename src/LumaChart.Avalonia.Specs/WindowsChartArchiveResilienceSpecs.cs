using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using LumaChart.Application.Archives;
using LumaChart.Avalonia.Services;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsChartArchiveResilienceSpecs
{
    public static async Task<WindowsChartArchiveResilienceSpecResult> RunAsync(
        string isolationRoot)
    {
        try
        {
            return new WindowsChartArchiveResilienceSpecResult(
                "windows-chart-archive-abnormal-paths-fail-closed-and-remain-atomic",
                true,
                await VerifyAsync(isolationRoot).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return new WindowsChartArchiveResilienceSpecResult(
                "windows-chart-archive-abnormal-paths-fail-closed-and-remain-atomic",
                false,
                exception.ToString());
        }
    }

    private static async Task<string> VerifyAsync(string isolationRoot)
    {
        var root = Path.GetFullPath(Path.Combine(
            isolationRoot,
            Guid.NewGuid().ToString("N")));
        var sourceController = new AvaloniaWorkspaceController(
            Path.Combine(root, "source-library"),
            Path.Combine(root, "source-editing"),
            "archive-resilience-owner");
        await sourceController.InitializeAsync().ConfigureAwait(false);
        var sourceTab = await sourceController
            .CreateAsync("Archive Resilience", "archive-resilience-owner")
            .ConfigureAwait(false);
        await sourceController.PersistAsync().ConfigureAwait(false);
        var validMc = await File.ReadAllBytesAsync(sourceTab.WorkingPath)
            .ConfigureAwait(false);
        var unsupportedNode = JsonNode.Parse(validMc)
                              ?? throw new InvalidDataException(
                                  "the valid MC fixture did not parse as JSON");
        unsupportedNode["meta"]!["mode"] = 4;
        var unsupportedMc = Encoding.UTF8.GetBytes(unsupportedNode.ToJsonString());
        var brokenMc = Encoding.UTF8.GetBytes("{not-json");
        var keyBytes = Enumerable.Range(0, 4096)
            .Select(index => (byte)((index * 17 + 29) & 0xff))
            .ToArray();

        var archivesRoot = Path.Combine(root, "archives");
        Directory.CreateDirectory(archivesRoot);
        var mixedArchive = Path.Combine(archivesRoot, "mixed charts.mcz");
        CreateArchive(
            mixedArchive,
            ("charts/valid.mc", validMc, null),
            ("charts/unsupported.mc", unsupportedMc, null),
            ("charts/broken.mc", brokenMc, null),
            ("assets/key.wav", keyBytes, null));

        var importLibraryRoot = Path.Combine(root, "import-library");
        var importController = new AvaloniaWorkspaceController(
            importLibraryRoot,
            Path.Combine(root, "import-editing"),
            "archive-resilience-viewer");
        await importController.InitializeAsync().ConfigureAwait(false);
        var concurrentPlans = await Task.WhenAll(
                importController.ImportArchiveAsync(mixedArchive),
                importController.ImportArchiveAsync(mixedArchive))
            .ConfigureAwait(false);
        Require(concurrentPlans.Select(plan => plan.Archive.DestinationRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "concurrent same-name archive imports did not allocate independent destinations");
        foreach (var plan in concurrentPlans)
        {
            Require(plan.Archive.ExtractedFiles == 4 &&
                    plan.Archive.ChartPaths.Count == 3 &&
                    plan.SupportedChartPaths.Count == 1 &&
                    plan.UnsupportedChartPaths.Count == 1 &&
                    plan.BrokenChartPaths.Count == 1,
                "mixed archive classification did not match WPF valid/unsupported/broken semantics: " +
                $"files={plan.Archive.ExtractedFiles}, charts={plan.Archive.ChartPaths.Count}, " +
                $"supported={plan.SupportedChartPaths.Count}, " +
                $"unsupported={plan.UnsupportedChartPaths.Count}, broken={plan.BrokenChartPaths.Count}");
            Require(IsStrictDescendant(importLibraryRoot, plan.Archive.DestinationRoot) &&
                    plan.Archive.ChartPaths.All(path =>
                        IsStrictDescendant(plan.Archive.DestinationRoot, path)),
                "mixed archive escaped the active Avalonia library");
            Require(File.Exists(Path.Combine(
                        plan.Archive.DestinationRoot,
                        "assets",
                        "key.wav")) &&
                    (await File.ReadAllBytesAsync(Path.Combine(
                            plan.Archive.DestinationRoot,
                            "assets",
                            "key.wav"))
                        .ConfigureAwait(false)).AsSpan().SequenceEqual(keyBytes),
                "mixed archive resource bytes changed during extraction");
        }

        var opened = await importController.OpenImportedChartsAsync(
                concurrentPlans[0].SupportedChartPaths)
            .ConfigureAwait(false);
        Require(opened.OpenedTabs.Count == 1 && opened.FailedPaths.Count == 0 &&
                opened.OpenedTabs[0].Session.Document.Metadata.Title == "Archive Resilience",
            "the valid chart from a mixed archive did not open structurally");

        var unsafeRoot = Path.Combine(root, "unsafe-destinations");
        Directory.CreateDirectory(unsafeRoot);
        var sentinelPath = Path.Combine(unsafeRoot, "preserve.sentinel");
        await File.WriteAllTextAsync(sentinelPath, "preserve").ConfigureAwait(false);

        var traversalArchive = Path.Combine(archivesRoot, "traversal.mcz");
        CreateArchive(traversalArchive, ("../escaped.mc", validMc, null));
        await RequireRejectedAndAtomicAsync(
                traversalArchive,
                unsafeRoot,
                exception => exception is InvalidDataException,
                "path traversal")
            .ConfigureAwait(false);
        Require(!File.Exists(Path.Combine(root, "escaped.mc")) &&
                !File.Exists(Path.Combine(unsafeRoot, "escaped.mc")),
            "path traversal created an escaped chart");

        var duplicateArchive = Path.Combine(archivesRoot, "duplicate.osz");
        CreateArchive(
            duplicateArchive,
            ("Charts/Same.osu", MinimalOsu(), null),
            ("charts/same.OSU", MinimalOsu(), null));
        await RequireRejectedAndAtomicAsync(
                duplicateArchive,
                unsafeRoot,
                exception => exception is InvalidDataException,
                "case-insensitive duplicate")
            .ConfigureAwait(false);

        var linkArchive = Path.Combine(archivesRoot, "link.mcz");
        CreateArchive(
            linkArchive,
            ("linked.mc", validMc, 0xA000 << 16));
        await RequireRejectedAndAtomicAsync(
                linkArchive,
                unsafeRoot,
                exception => exception is InvalidDataException,
                "symbolic-link entry")
            .ConfigureAwait(false);

        var bombArchive = Path.Combine(archivesRoot, "compression-bomb.osz");
        CreateArchive(
            bombArchive,
            ("bomb.osu", new byte[2 * 1024 * 1024], null));
        await RequireRejectedAndAtomicAsync(
                bombArchive,
                unsafeRoot,
                exception => exception is InvalidDataException &&
                             exception.Message.Contains(
                                 "compression ratio",
                                 StringComparison.OrdinalIgnoreCase),
                "compression-ratio bomb")
            .ConfigureAwait(false);

        var cancellationArchive = Path.Combine(archivesRoot, "cancel.mcz");
        CreateArchive(cancellationArchive, ("valid.mc", validMc, null));
        using var cancellation = new CancellationTokenSource();
        var cancelCreator = new CancelAfterCreateDirectoryCreator(cancellation);
        var cancellationFailure = await CaptureFailureAsync(async () =>
            {
                _ = await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                        cancellationArchive,
                        unsafeRoot,
                        cancelCreator,
                        cancellation.Token)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);
        Require(cancellationFailure is OperationCanceledException,
            $"archive cancellation returned {cancellationFailure.GetType().Name}");
        RequireNoOwnedStaging(unsafeRoot, "cancellation");
        Require(await File.ReadAllTextAsync(sentinelPath).ConfigureAwait(false) == "preserve",
            "an abnormal archive path changed the pre-existing destination sentinel");

        return "real ZIP/NTFS/Avalonia workspace: mixed valid/unsupported/broken charts classified " +
               "1/1/1; two same-name concurrent imports stayed independent; traversal, case-insensitive " +
               "duplicates, symbolic links and >200x compression were rejected; cancellation removed " +
               "owned staging; valid MC reopened and all sentinel/resource bytes remained exact";
    }

    private static async Task RequireRejectedAndAtomicAsync(
        string archivePath,
        string destinationParent,
        Func<Exception, bool> expected,
        string phase)
    {
        var existingDirectories = Directory.EnumerateDirectories(destinationParent)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failure = await CaptureFailureAsync(async () =>
            {
                _ = await ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                        archivePath,
                        destinationParent,
                        WindowsArchiveDirectoryCreator.Instance)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);
        Require(expected(failure),
            $"{phase} returned an unexpected {failure.GetType().Name}: {failure.Message}");
        var newDirectories = Directory.EnumerateDirectories(destinationParent)
            .Select(Path.GetFullPath)
            .Where(path => !existingDirectories.Contains(path))
            .ToArray();
        Require(newDirectories.Length == 0,
            $"{phase} published or leaked directories: {string.Join(", ", newDirectories)}");
        RequireNoOwnedStaging(destinationParent, phase);
    }

    private static async Task<Exception> CaptureFailureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("the abnormal archive unexpectedly succeeded");
    }

    private static void CreateArchive(
        string path,
        params (string Name, byte[] Bytes, int? ExternalAttributes)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name, CompressionLevel.SmallestSize);
            if (item.ExternalAttributes.HasValue)
                entry.ExternalAttributes = item.ExternalAttributes.Value;
            using var output = entry.Open();
            output.Write(item.Bytes);
        }
    }

    private static byte[] MinimalOsu() => Encoding.UTF8.GetBytes(
        "osu file format v14\r\n\r\n" +
        "[General]\r\nAudioFilename: audio.ogg\r\nMode: 3\r\n\r\n" +
        "[Metadata]\r\nTitle:Archive Resilience\r\nArtist:LumaChart\r\n" +
        "Creator:Avalonia Specs\r\nVersion:Atomic\r\n\r\n" +
        "[Difficulty]\r\nCircleSize:4\r\nOverallDifficulty:8\r\nHPDrainRate:8\r\n\r\n" +
        "[TimingPoints]\r\n0,500,4,2,1,50,1,0\r\n\r\n" +
        "[HitObjects]\r\n64,192,1000,1,0,0:0:0:0:\r\n");

    private static void RequireNoOwnedStaging(string root, string phase)
    {
        var staging = Directory.EnumerateDirectories(
                root,
                "*.lumachart-importing",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        Require(staging.Length == 0,
            $"{phase} left owned archive staging: {string.Join(", ", staging)}");
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(root),
            Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               !relative.Equals(".", StringComparison.Ordinal) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(
                   ".." + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal) &&
               !relative.StartsWith(
                   ".." + Path.AltDirectorySeparatorChar,
                   StringComparison.Ordinal);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CancelAfterCreateDirectoryCreator(
        CancellationTokenSource cancellation) : IArchiveDirectoryCreator
    {
        public bool TryCreate(string absolutePath)
        {
            if (!WindowsArchiveDirectoryCreator.Instance.TryCreate(absolutePath))
                return false;
            cancellation.Cancel();
            return true;
        }
    }
}

internal sealed record WindowsChartArchiveResilienceSpecResult(
    string Name,
    bool Passed,
    string Detail);

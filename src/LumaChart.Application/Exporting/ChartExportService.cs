using LumaChart.Core.Localization;
using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Application.Exporting;

/// <summary>
/// Platform-neutral entry point for the four Windows/WPF 1.1.0 export modes.
/// FullPackage remains delegated to its existing hardened implementation.
/// Rate-package audio is supplied by an injected platform adapter.
/// </summary>
public static class ChartExportService
{
    public static async Task<ChartExportResult> ExportAsync(
        ChartExportRequest request,
        IChartExportAudioTranscoder? audioTranscoder = null,
        IProgress<ChartExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if (string.IsNullOrWhiteSpace(request.Document.FilePath))
            throw new InvalidOperationException("请先保存谱面再导出。");

        var plan = ChartExportPlanner.Create(request.Options);
        if (plan.RequiresAudioTranscoder && audioTranscoder == null)
            throw new InvalidOperationException("倍速包导出需要可用的音频转码器。");

        return request.Options.Mode switch
        {
            ChartExportMode.SingleChart => await ExportSingleChartAsync(
                request,
                progress,
                cancellationToken).ConfigureAwait(false),
            ChartExportMode.FullPackage => await ExportFullPackageAsync(
                request,
                progress,
                cancellationToken).ConfigureAwait(false),
            ChartExportMode.SingleRatePackage or ChartExportMode.RatePack => await ExportRatePackageAsync(
                request,
                plan,
                audioTranscoder,
                progress,
                cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Options.Mode, "未知的导出模式。")
        };
    }

    private static Task<ChartExportResult> ExportSingleChartAsync(
        ChartExportRequest request,
        IProgress<ChartExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var snapshot = request.Document.Clone();
        var destinationPath = Path.GetFullPath(request.DestinationPath);
        progress?.Report(new(0, 1, "正在写入单谱文件"));
        return Task.Run(
            () =>
            {
                var destinationFolder = Path.GetDirectoryName(destinationPath)
                                        ?? throw new InvalidOperationException(
                                            "The chart destination has no directory.");
                Directory.CreateDirectory(destinationFolder);
                var temporaryChartPath = CreateTemporaryChartPath(
                    destinationFolder,
                    Path.GetFileName(destinationPath));
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SaveChart(
                        snapshot,
                        temporaryChartPath,
                        request.Options.Format,
                        request.Options.OsuOverallDifficulty,
                        request.Options.OsuHpDrainRate);

                    // The completed chart remains beside its destination so the
                    // final rename cannot cross filesystems. Cancellation must
                    // win before this commit boundary, but a cancellation raised
                    // after the rename must not turn a successful export into a
                    // reported failure.
                    progress?.Report(new(0, 1, "正在提交单谱文件"));
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporaryChartPath, destinationPath, overwrite: true);

                    var result = new ChartExportResult(
                        destinationPath,
                        [destinationPath],
                        1,
                        new FileInfo(destinationPath).Length);
                    progress?.Report(new(1, 1, "完成"));
                    return result;
                }
                finally
                {
                    TryDeleteTemporaryFile(temporaryChartPath);
                }
            },
            cancellationToken);
    }

    private static async Task<ChartExportResult> ExportFullPackageAsync(
        ChartExportRequest request,
        IProgress<ChartExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sessionScratchRoot = RequireScratchRoot(request);
        var packageSourcePath = GetPackageSourcePath(request);
        if (request.Options.Format == ChartExportFormat.Malody)
        {
            var result = await MalodyFullPackageExportService.ExportAsync(
                    new MalodyFullPackageExportRequest(
                        request.Document,
                        packageSourcePath,
                        request.DestinationPath,
                        sessionScratchRoot),
                    progress == null
                        ? null
                        : new DelegatingProgress<MalodyFullPackageExportProgress>(value => progress.Report(
                            new ChartExportProgress(value.Completed, value.Total, value.Stage))),
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChartExportResult(
                result.DestinationArchivePath,
                [result.MainChartEntryPath],
                result.EntryCount,
                result.ArchiveBytes);
        }

        var osuResult = await OsuFullPackageExportService.ExportAsync(
                new OsuFullPackageExportRequest(
                    request.Document,
                    packageSourcePath,
                    request.DestinationPath,
                    sessionScratchRoot,
                    request.Options.OsuOverallDifficulty,
                    request.Options.OsuHpDrainRate),
                progress == null
                    ? null
                    : new DelegatingProgress<OsuFullPackageExportProgress>(value => progress.Report(
                        new ChartExportProgress(value.Completed, value.Total, value.Stage))),
                cancellationToken)
            .ConfigureAwait(false);
        return new ChartExportResult(
            osuResult.DestinationArchivePath,
            [osuResult.MainChartEntryPath],
            osuResult.EntryCount,
            osuResult.ArchiveBytes);
    }

    private static Task<ChartExportResult> ExportRatePackageAsync(
        ChartExportRequest request,
        ChartExportPlan plan,
        IChartExportAudioTranscoder? audioTranscoder,
        IProgress<ChartExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var workingChartPath = Path.GetFullPath(request.Document.FilePath!);
        var packageSourceChartPath = GetPackageSourcePath(request);
        var destinationPath = Path.GetFullPath(request.DestinationPath);
        var sessionScratchRoot = RequireScratchRoot(request);

        // The Avalonia workspace boundary already supplies a private committed
        // document clone. Dispatch before taking this service's defensive clone so
        // cloning, resource copies, the valid 1.0x path, serialization and ZIP work
        // cannot run on the calling/UI thread.
        return Task.Run(
            () => ExportRatePackageCoreAsync(
                request.Document,
                request.Options,
                plan,
                workingChartPath,
                packageSourceChartPath,
                destinationPath,
                sessionScratchRoot,
                audioTranscoder,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private static async Task<ChartExportResult> ExportRatePackageCoreAsync(
        ChartDocument sourceDocument,
        ChartExportOptions options,
        ChartExportPlan plan,
        string workingChartPath,
        string packageSourceChartPath,
        string destinationPath,
        string sessionScratchRoot,
        IChartExportAudioTranscoder? audioTranscoder,
        IProgress<ChartExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceSnapshot = sourceDocument.Clone();
        sourceSnapshot.FilePath = workingChartPath;
        EnsureRatesKeepBpmInRange(sourceSnapshot, plan.Rates);
        var exportRoot = Path.GetFullPath(Path.Combine(sessionScratchRoot, "exports"));
        Directory.CreateDirectory(exportRoot);
        var temporaryRoot = Path.GetFullPath(Path.Combine(exportRoot, Guid.NewGuid().ToString("N")));
        if (!IsStrictDescendant(exportRoot, temporaryRoot))
            throw new InvalidOperationException("The export staging path escaped its managed scratch root.");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            progress?.Report(new(0, plan.Rates.Count + 1, "正在收集谱面资源"));
            var sharedResources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var chartPaths = new List<string>(plan.Rates.Count);
            for (var index = 0; index < plan.Rates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rate = plan.Rates[index];
                progress?.Report(new(index, plan.Rates.Count + 1, LocalizationCatalog.Format("正在生成 {0:0.####}x", rate)));
                var chartPath = await WriteRateChartAsync(
                        sourceSnapshot,
                        packageSourceChartPath,
                        temporaryRoot,
                        options,
                        rate,
                        sharedResources,
                        audioTranscoder,
                        cancellationToken)
                    .ConfigureAwait(false);
                chartPaths.Add(chartPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (options.Format == ChartExportFormat.Malody)
                CompactMalodyCharts(temporaryRoot, cancellationToken);
            progress?.Report(new(plan.Rates.Count, plan.Rates.Count + 1, "正在压缩谱包"));
            var entryCount = await CommitArchiveAsync(
                    temporaryRoot,
                    destinationPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var entryPaths = chartPaths
                .Select(path => Path.GetRelativePath(temporaryRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .ToArray();
            var result = new ChartExportResult(
                destinationPath,
                entryPaths,
                entryCount,
                new FileInfo(destinationPath).Length);
            progress?.Report(new(plan.Rates.Count + 1, plan.Rates.Count + 1, "完成"));
            return result;
        }
        finally
        {
            DeleteTemporaryExportDirectory(exportRoot, temporaryRoot);
        }
    }

    private static async Task<string> WriteRateChartAsync(
        ChartDocument source,
        string packageSourceChartPath,
        string outputFolder,
        ChartExportOptions options,
        double rate,
        Dictionary<string, string> sharedResources,
        IChartExportAudioTranscoder? audioTranscoder,
        CancellationToken cancellationToken)
    {
        var clone = source.Clone();
        NormalizeResourceReferences(
            clone,
            packageSourceChartPath,
            outputFolder,
            includeAudio: Math.Abs(rate - 1) < .000001,
            sharedResources);
        if (Math.Abs(rate - 1) > .000001)
        {
            var audioPath = ResolveReference(source.Metadata.AudioFile, packageSourceChartPath);
            if (audioPath == null)
                throw new FileNotFoundException("倍速包需要可读取的谱面音频。", source.Metadata.AudioFile);
            var audioName = $"{SafeName(Path.GetFileNameWithoutExtension(audioPath))}_{RateText(rate)}x.ogg";
            var audioOutput = UniqueFile(Path.Combine(outputFolder, audioName));
            await audioTranscoder!.ConvertToOggAsync(
                    audioPath,
                    audioOutput,
                    rate,
                    options.PreservePitch,
                    cancellationToken)
                .ConfigureAwait(false);
            clone.Metadata.AudioFile = Path.GetFileName(audioOutput);
            TransformTiming(clone, rate);
        }

        var extension = options.Format == ChartExportFormat.Malody ? ".mc" : ".osu";
        var baseName = options.Format == ChartExportFormat.Malody
            ? string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{RateText(rate)}x")
            : BuildOsuChartFileName(clone);
        var chartPath = UniqueFile(Path.Combine(outputFolder, baseName + extension));
        SaveChart(clone, chartPath, options.Format, options.OsuOverallDifficulty, options.OsuHpDrainRate);
        return chartPath;
    }

    /// <summary>
    /// A rate multiplies every BPM. Normalization clamps anything above
    /// ChartDocument.MaximumTimingBpm, which would silently shift all later
    /// notes in that rate's chart, so such a package is refused up front.
    /// </summary>
    private static void EnsureRatesKeepBpmInRange(ChartDocument document, IReadOnlyList<double> rates)
    {
        var fastestBpm = document.Timing
            .Where(timing => timing.Bpm.HasValue && double.IsFinite(timing.Bpm.Value))
            .Select(timing => timing.Bpm!.Value)
            .DefaultIfEmpty(0)
            .Max();
        foreach (var rate in rates)
        {
            if (Math.Abs(rate - 1) <= .000001 || fastestBpm * rate <= ChartDocument.MaximumTimingBpm) continue;
            throw new InvalidOperationException(LocalizationCatalog.Format(
                "倍速 {0}x 会使 BPM 超过上限 {1}，已中止导出。",
                RateText(rate),
                ChartDocument.MaximumTimingBpm.ToString("0", CultureInfo.InvariantCulture)));
        }
    }

    private static void TransformTiming(ChartDocument document, double rate)
    {
        foreach (var timing in document.Timing)
        {
            if (timing.Bpm.HasValue) timing.Bpm *= rate;
            timing.Delay /= rate;
        }
        document.Metadata.Bpm *= rate;
        document.Metadata.PreviewTime = (int)Math.Max(0, Math.Round(document.Metadata.PreviewTime / rate));
        document.Metadata.Version = $"{document.Metadata.Version} {RateText(rate)}x";
    }

    private static void NormalizeResourceReferences(
        ChartDocument document,
        string packageSourceChartPath,
        string outputFolder,
        bool includeAudio,
        Dictionary<string, string> sharedResources)
    {
        if (includeAudio)
            document.Metadata.AudioFile = NormalizeReference(
                document.Metadata.AudioFile,
                packageSourceChartPath,
                outputFolder,
                required: true,
                sharedResources);
        document.Metadata.CoverFile = NormalizeReference(
            document.Metadata.CoverFile,
            packageSourceChartPath,
            outputFolder,
            required: false,
            sharedResources);
        document.Metadata.BackgroundFile = NormalizeReference(
            document.Metadata.BackgroundFile,
            packageSourceChartPath,
            outputFolder,
            required: false,
            sharedResources);
        foreach (var note in document.Notes.Where(note => !string.IsNullOrWhiteSpace(note.Sound)))
            note.Sound = NormalizeReference(
                note.Sound!,
                packageSourceChartPath,
                outputFolder,
                required: false,
                sharedResources);
    }

    private static string NormalizeReference(
        string reference,
        string packageSourceChartPath,
        string outputFolder,
        bool required,
        Dictionary<string, string> sharedResources)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
        var resolved = ResolveReference(reference, packageSourceChartPath);
        if (resolved == null)
        {
            if (required) throw new FileNotFoundException("找不到谱面引用的资源。", reference);
            return string.Empty;
        }
        if (sharedResources.TryGetValue(resolved, out var existing)) return existing;

        var target = UniqueFile(Path.Combine(outputFolder, SafeName(Path.GetFileName(resolved))));
        File.Copy(resolved, target, overwrite: false);
        var normalized = Path.GetFileName(target);
        sharedResources[resolved] = normalized;
        return normalized;
    }

    private static async Task<int> CommitArchiveAsync(
        string temporaryRoot,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var destinationFolder = Path.GetDirectoryName(destinationPath)
                                ?? throw new InvalidOperationException("The archive destination has no directory.");
        Directory.CreateDirectory(destinationFolder);
        var temporaryArchivePath = CreateTemporaryArchivePath(destinationFolder, Path.GetFileName(destinationPath));
        try
        {
            // ZipFile.CreateFromDirectory has no cancellation overload. Stream
            // every staged file instead so cancellation can interrupt compression,
            // while the caller's previous target remains untouched until commit.
            cancellationToken.ThrowIfCancellationRequested();
            await CreateArchiveAsync(
                    temporaryRoot,
                    temporaryArchivePath,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            int entryCount;
            using (var archive = ZipFile.OpenRead(temporaryArchivePath))
                entryCount = archive.Entries.Count;
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryArchivePath, destinationPath, overwrite: true);
            return entryCount;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryArchivePath)) File.Delete(temporaryArchivePath);
            }
            catch
            {
                // Do not replace the primary export failure.
            }
        }
    }

    private static async Task CreateArchiveAsync(
        string sourceRoot,
        string destinationArchivePath,
        CancellationToken cancellationToken)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        await using var output = new FileStream(
            destinationArchivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceRoot, sourcePath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                await using var source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var entryStream = entry.Open();
                await source.CopyToAsync(entryStream, 128 * 1024, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void SaveChart(
        ChartDocument document,
        string path,
        ChartExportFormat format,
        double osuOverallDifficulty,
        double osuHpDrainRate)
    {
        if (format == ChartExportFormat.Malody)
            MalodyMcSerializer.Save(document, path);
        else
            OsuManiaSerializer.Save(document, path, osuOverallDifficulty, osuHpDrainRate);
    }

    private static void CompactMalodyCharts(string root, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var path in Directory.EnumerateFiles(root, "*", options)
                     .Where(path => Path.GetExtension(path).Equals(".mc", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(path);
            // Utf8 JSON parsing does not skip a UTF-8 BOM.
            var json = bytes.AsMemory(bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0);
            using var document = JsonDocument.Parse(json);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
                   {
                       Indented = false,
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
                document.RootElement.WriteTo(writer);
            File.WriteAllBytes(path, output.ToArray());
        }
    }

    private static string GetPackageSourcePath(ChartExportRequest request) =>
        string.IsNullOrWhiteSpace(request.PackageSourceChartPath)
            ? Path.GetFullPath(request.Document.FilePath!)
            : Path.GetFullPath(request.PackageSourceChartPath);

    private static string RequireScratchRoot(ChartExportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionScratchRoot))
            throw new ArgumentException("谱包导出需要会话临时目录。", nameof(request));
        return Path.GetFullPath(request.SessionScratchRoot);
    }

    // Only files inside the chart folder may be packaged. Rooted, UNC and ".."
    // references used to copy arbitrary local files into a shared archive.
    private static string? ResolveReference(string reference, string chartPath) =>
        ChartResourceReference.TryResolve(reference, Path.GetDirectoryName(chartPath) ?? string.Empty, out var resolved)
            ? resolved
            : null;

    private static void DeleteTemporaryExportDirectory(string exportRoot, string temporaryRoot)
    {
        try
        {
            var target = Path.GetFullPath(temporaryRoot);
            if (!IsStrictDescendant(exportRoot, target)) return;
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
        catch
        {
            // Cleanup must not replace the original export failure.
        }
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               !relative.Equals(".", StringComparison.Ordinal) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string CreateTemporaryArchivePath(string destinationFolder, string destinationFileName)
    {
        destinationFolder = Path.GetFullPath(destinationFolder);
        var safeFileName = SafeName(destinationFileName);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(
                destinationFolder,
                $".{safeFileName}.{Guid.NewGuid():N}.luma-export.tmp");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to allocate a unique temporary archive path.");
    }

    private static string CreateTemporaryChartPath(string destinationFolder, string destinationFileName)
    {
        destinationFolder = Path.GetFullPath(destinationFolder);
        var safeFileName = SafeName(destinationFileName);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(
                destinationFolder,
                $".{safeFileName}.{Guid.NewGuid():N}.luma-part");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to allocate a unique temporary chart path.");
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup must not replace the primary export failure.
        }
    }

    private static string BuildOsuChartFileName(ChartDocument document) =>
        $"{SafeName(document.Metadata.Artist)} - {SafeName(document.Metadata.Title)} " +
        $"({SafeName(document.Metadata.Creator)}) [{SafeName(document.Metadata.Version)}]";

    private static string RateText(double rate) =>
        rate.ToString("0.####", CultureInfo.InvariantCulture);

    private static string SafeName(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        value = value.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "Untitled" : value;
    }

    private static string UniqueFile(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)
                     ?? throw new InvalidOperationException("A package file has no directory.");
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(folder, $"{stem}_{index}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private sealed class DelegatingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

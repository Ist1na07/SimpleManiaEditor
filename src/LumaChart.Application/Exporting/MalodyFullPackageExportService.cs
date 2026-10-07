using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LumaChart.Application.Libraries;
using LumaChart.Core.IO;
using LumaChart.Core.Localization;
using LumaChart.Core.Models;

namespace LumaChart.Application.Exporting;

/// <summary>
/// Creates the Malody FullPackage 1.0x archive used by the Windows/WPF 1.1.0
/// client. The current working document supplies chart content and localized
/// resource references, while the original source path supplies the package
/// directory tree and sibling charts (only sibling charts and referenced files
/// when that folder is a shared or sensitive location).
/// </summary>
public static class MalodyFullPackageExportService
{
    public static async Task<MalodyFullPackageExportResult> ExportAsync(
        MalodyFullPackageExportRequest request,
        IProgress<MalodyFullPackageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ChartFullPackageExportService.ExportAsync(
                new ChartFullPackageExportRequest(
                    request.Document,
                    request.PackageSourceChartPath,
                    request.DestinationArchivePath,
                    request.SessionScratchRoot,
                    ChartFullPackageExportFormat.Malody,
                    OsuOverallDifficulty: 8,
                    OsuHpDrainRate: 8),
                progress == null
                    ? null
                    : value => progress.Report(new MalodyFullPackageExportProgress(
                        value.Completed,
                        value.Total,
                        value.Stage)),
                cancellationToken)
            .ConfigureAwait(false);

        return new MalodyFullPackageExportResult(
            result.DestinationArchivePath,
            result.MainChartEntryPath,
            result.EntryCount,
            result.ArchiveBytes);
    }
}

internal static class ChartFullPackageExportService
{
    private const int ProgressTotal = 2;

    /// <summary>
    /// Upper bound for the resources staged into one full package. Only
    /// referenced files are collected now, so a real song stays far below it;
    /// the bound stops a hostile or accidental reference set from filling the
    /// disk or producing an archive other clients cannot import.
    /// </summary>
    internal const long MaximumPackageResourceBytes = 2L * 1024 * 1024 * 1024;

    internal static Task<ChartFullPackageExportResult> ExportAsync(
        ChartFullPackageExportRequest request,
        Action<ChartFullPackageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Document);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackageSourceChartPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationArchivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionScratchRoot);
        if (string.IsNullOrWhiteSpace(request.Document.FilePath))
            throw new InvalidOperationException("请先保存谱面再导出。");

        // Freeze the committed caller-owned state before moving work off the UI
        // thread. Both serializers normalize their input and set FilePath, so only
        // this private clone may be passed to them.
        var snapshot = request.Document.Clone();
        var workingChartPath = Path.GetFullPath(request.Document.FilePath);
        snapshot.FilePath = workingChartPath;
        var packageSourceChartPath = Path.GetFullPath(request.PackageSourceChartPath);
        var destinationArchivePath = Path.GetFullPath(request.DestinationArchivePath);
        var sessionScratchRoot = Path.GetFullPath(request.SessionScratchRoot);

        return Task.Run(
            () => ExportCore(
                snapshot,
                workingChartPath,
                packageSourceChartPath,
                destinationArchivePath,
                sessionScratchRoot,
                request.Format,
                request.OsuOverallDifficulty,
                request.OsuHpDrainRate,
                progress,
                cancellationToken),
            cancellationToken);
    }

    private static ChartFullPackageExportResult ExportCore(
        ChartDocument snapshot,
        string workingChartPath,
        string packageSourceChartPath,
        string destinationArchivePath,
        string sessionScratchRoot,
        ChartFullPackageExportFormat format,
        double osuOverallDifficulty,
        double osuHpDrainRate,
        Action<ChartFullPackageExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var exportRoot = Path.GetFullPath(Path.Combine(sessionScratchRoot, "exports"));
        Directory.CreateDirectory(exportRoot);
        var temporaryRoot = Path.GetFullPath(Path.Combine(exportRoot, Guid.NewGuid().ToString("N")));
        if (!IsStrictDescendant(exportRoot, temporaryRoot))
            throw new InvalidOperationException("The export staging path escaped its managed scratch root.");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            progress?.Invoke(new(0, ProgressTotal, "正在收集谱面资源"));
            var packageSourceRoot = Path.GetDirectoryName(packageSourceChartPath)
                                    ?? throw new InvalidOperationException("The package source has no directory.");
            var chartExtension = format == ChartFullPackageExportFormat.Malody ? ".mc" : ".osu";
            var keepSiblingCharts = Path.GetExtension(packageSourceChartPath)
                .Equals(chartExtension, StringComparison.OrdinalIgnoreCase);
            // A full package is the whole song folder: osu! hitsound samples,
            // storyboards and their images or videos are used without being named
            // by the chart. A chart saved directly in a shared or sensitive folder
            // (drive or share root, profile, Desktop/Documents/Downloads, system,
            // Temp or program folders) would publish everything beside it, so
            // there only same-format sibling charts and referenced files are kept.
            var budget = new ResourceBudget(MaximumPackageResourceBytes, sessionScratchRoot);
            var referencedResourcesOnly = DesktopLibraryDeletionPlanner.IsProtectedFolder(packageSourceRoot);
            if (!referencedResourcesOnly)
                CopyResourceTree(
                    packageSourceRoot,
                    temporaryRoot,
                    keepSiblingCharts ? chartExtension : null,
                    sessionScratchRoot,
                    exportRoot,
                    budget,
                    cancellationToken);
            else if (keepSiblingCharts)
                StageSiblingCharts(
                    packageSourceRoot,
                    packageSourceChartPath,
                    temporaryRoot,
                    chartExtension,
                    budget,
                    cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new(0, ProgressTotal, "正在生成 1x"));
            var sharedResources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            NormalizeResourceReferences(
                snapshot,
                workingChartPath,
                temporaryRoot,
                packageSourceRoot,
                sharedResources,
                budget,
                referencedResourcesOnly);

            var mainChartPath = keepSiblingCharts
                ? Path.Combine(temporaryRoot, Path.GetFileName(packageSourceChartPath))
                : UniqueFile(Path.Combine(
                    temporaryRoot,
                    format == ChartFullPackageExportFormat.Malody
                        ? string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{RateText(1)}x.mc")
                        : BuildOsuChartFileName(snapshot)));
            if (format == ChartFullPackageExportFormat.Malody)
            {
                MalodyMcSerializer.Save(snapshot, mainChartPath);
                CompactMalodyCharts(temporaryRoot, cancellationToken);
            }
            else
            {
                OsuManiaSerializer.Save(
                    snapshot,
                    mainChartPath,
                    osuOverallDifficulty,
                    osuHpDrainRate);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new(1, ProgressTotal, "正在压缩谱包"));
            var destinationFolder = Path.GetDirectoryName(destinationArchivePath)
                                    ?? throw new InvalidOperationException("The archive destination has no directory.");
            Directory.CreateDirectory(destinationFolder);
            var temporaryArchivePath = CreateTemporaryArchivePath(
                destinationFolder,
                Path.GetFileName(destinationArchivePath));
            try
            {
                ZipFile.CreateFromDirectory(
                    temporaryRoot,
                    temporaryArchivePath,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false);

                // ZipFile.CreateFromDirectory has no cancellation overload. A
                // cancellation requested while it was compressing must still win
                // before the completed archive replaces the caller's destination.
                cancellationToken.ThrowIfCancellationRequested();
                int entryCount;
                using (var archive = ZipFile.OpenRead(temporaryArchivePath))
                    entryCount = archive.Entries.Count;

                // This synchronous progress boundary means the ZIP is complete
                // and readable but the destination has not yet been committed.
                // Cancellation after the following check races with the commit
                // itself and is therefore treated as crossing the commit boundary.
                progress?.Invoke(new(1, ProgressTotal, "正在提交谱包"));
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryArchivePath, destinationArchivePath, overwrite: true);

                var mainChartEntryPath = Path.GetRelativePath(temporaryRoot, mainChartPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                var result = new ChartFullPackageExportResult(
                    destinationArchivePath,
                    mainChartEntryPath,
                    entryCount,
                    new FileInfo(destinationArchivePath).Length);
                progress?.Invoke(new(ProgressTotal, ProgressTotal, "完成"));
                return result;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryArchivePath)) File.Delete(temporaryArchivePath);
                }
                catch
                {
                    // Do not replace the primary export failure. The name is
                    // unique, is never returned, and remains confined to the
                    // user-selected destination directory if Windows still holds it.
                }
            }
        }
        finally
        {
            DeleteTemporaryExportDirectory(exportRoot, temporaryRoot);
        }
    }

    private static void NormalizeResourceReferences(
        ChartDocument document,
        string workingChartPath,
        string outputFolder,
        string copiedResourceRoot,
        Dictionary<string, string> sharedResources,
        ResourceBudget budget,
        bool referencedResourcesOnly)
    {
        document.Metadata.AudioFile = NormalizeReference(
            document.Metadata.AudioFile,
            workingChartPath,
            outputFolder,
            required: true,
            copiedResourceRoot,
            sharedResources,
            budget,
            referencedResourcesOnly);
        document.Metadata.CoverFile = NormalizeReference(
            document.Metadata.CoverFile,
            workingChartPath,
            outputFolder,
            required: false,
            copiedResourceRoot,
            sharedResources,
            budget,
            referencedResourcesOnly);
        document.Metadata.BackgroundFile = NormalizeReference(
            document.Metadata.BackgroundFile,
            workingChartPath,
            outputFolder,
            required: false,
            copiedResourceRoot,
            sharedResources,
            budget,
            referencedResourcesOnly);
        foreach (var note in document.Notes.Where(note => !string.IsNullOrWhiteSpace(note.Sound)))
        {
            note.Sound = NormalizeReference(
                note.Sound!,
                workingChartPath,
                outputFolder,
                required: false,
                copiedResourceRoot,
                sharedResources,
                budget,
                referencedResourcesOnly);
        }
    }

    private static string NormalizeReference(
        string reference,
        string workingChartPath,
        string outputFolder,
        bool required,
        string copiedResourceRoot,
        Dictionary<string, string> sharedResources,
        ResourceBudget budget,
        bool referencedResourcesOnly)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
        var resolved = ResolveReference(reference, workingChartPath);
        if (resolved == null)
        {
            if (required) throw new FileNotFoundException("找不到谱面引用的资源。", reference);
            return string.Empty;
        }

        if (IsStrictDescendant(copiedResourceRoot, resolved))
        {
            var relative = Path.GetRelativePath(copiedResourceRoot, resolved);
            var staged = Path.GetFullPath(Path.Combine(outputFolder, relative));
            if (IsStrictDescendant(outputFolder, staged) && File.Exists(staged))
                return relative.Replace(Path.DirectorySeparatorChar, '/');
            // In a referenced-only package nothing was staged yet, so the file
            // keeps its relative layout. In a whole-folder package lexical
            // containment is not enough: CopyResourceTree deliberately prunes
            // reparse points and managed scratch data. Materialize such a target
            // at the package root rather than emitting a broken reference to a
            // file that was never staged.
            if (referencedResourcesOnly &&
                StageAtRelativePath(resolved, copiedResourceRoot, outputFolder, budget) is { } stagedRelative)
                return stagedRelative;
        }
        if (sharedResources.TryGetValue(resolved, out var existing)) return existing;

        budget.Reserve(new FileInfo(resolved).Length);
        var target = UniqueFile(Path.Combine(outputFolder, SafeName(Path.GetFileName(resolved))));
        File.Copy(resolved, target, overwrite: false);
        var normalized = Path.GetFileName(target);
        sharedResources[resolved] = normalized;
        return normalized;
    }

    // Only files inside the chart folder may be packaged. Rooted, UNC and ".."
    // references used to copy arbitrary local files into a shared archive.
    private static string? ResolveReference(string reference, string chartPath) =>
        ChartResourceReference.TryResolve(reference, Path.GetDirectoryName(chartPath) ?? string.Empty, out var resolved)
            ? resolved
            : null;

    /// <summary>
    /// Copies <paramref name="resolved"/> to the same relative location below
    /// <paramref name="outputFolder"/> when it lies inside
    /// <paramref name="sourceRoot"/>. Returns the '/'-separated relative path, or
    /// null when the file is outside the package source or inside the managed
    /// session scratch area, whose internal layout is never published.
    /// </summary>
    private static string? StageAtRelativePath(
        string resolved,
        string sourceRoot,
        string outputFolder,
        ResourceBudget budget)
    {
        if (!IsStrictDescendant(sourceRoot, resolved) || budget.IsManagedScratch(resolved)) return null;
        var relative = Path.GetRelativePath(Path.GetFullPath(sourceRoot), Path.GetFullPath(resolved));
        var staged = Path.GetFullPath(Path.Combine(outputFolder, relative));
        if (!IsStrictDescendant(outputFolder, staged)) return null;
        if (!File.Exists(staged))
        {
            budget.Reserve(new FileInfo(resolved).Length);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)
                                      ?? throw new InvalidOperationException("A package entry has no directory."));
            File.Copy(resolved, staged, overwrite: false);
        }
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Copies the song folder tree (the normal full-package content). Reparse
    /// points are not followed, so a junction or symlink cannot pull files from
    /// outside the folder; a referenced target behind one is materialized later
    /// by NormalizeReference. Other-format charts, archives and in-progress
    /// files are skipped and every copied byte counts against the size budget.
    /// </summary>
    private static void CopyResourceTree(
        string sourceFolder,
        string outputFolder,
        string? includedChartExtension,
        string sessionScratchRoot,
        string exportRoot,
        ResourceBudget budget,
        CancellationToken cancellationToken)
    {
        sourceFolder = Path.GetFullPath(sourceFolder);
        outputFolder = Path.GetFullPath(outputFolder);
        sessionScratchRoot = Path.GetFullPath(sessionScratchRoot);
        exportRoot = Path.GetFullPath(exportRoot);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var excludedRoot = IsStrictDescendant(sourceFolder, sessionScratchRoot)
            ? sessionScratchRoot
            : IsStrictDescendant(sourceFolder, exportRoot)
                ? exportRoot
                : IsStrictDescendant(sourceFolder, outputFolder)
                    ? outputFolder
                    : null;
        var pending = new Stack<string>();
        pending.Push(sourceFolder);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullDirectory = Path.GetFullPath(directory);
                if (excludedRoot != null && IsSameOrDescendant(excludedRoot, fullDirectory)) continue;
                pending.Push(fullDirectory);
            }

            foreach (var source in Directory.EnumerateFiles(current, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullSource = Path.GetFullPath(source);
                if (excludedRoot != null && IsSameOrDescendant(excludedRoot, fullSource)) continue;
                var extension = Path.GetExtension(fullSource);
                var chartFile = extension.Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".osu", StringComparison.OrdinalIgnoreCase);
                var shadowedCompressedChart = extension.Equals(".mc_", StringComparison.OrdinalIgnoreCase) &&
                                              File.Exists(fullSource[..^1]);
                if (chartFile && !extension.Equals(includedChartExtension, StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".mcz", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".osz", StringComparison.OrdinalIgnoreCase) ||
                    shadowedCompressedChart ||
                    fullSource.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = Path.GetRelativePath(sourceFolder, fullSource);
                var destination = Path.GetFullPath(Path.Combine(outputFolder, relative));
                if (!IsStrictDescendant(outputFolder, destination))
                    throw new InvalidOperationException("A package resource escaped the export staging directory.");
                budget.Reserve(new FileInfo(fullSource).Length);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)
                                          ?? throw new InvalidOperationException("A package entry has no directory."));
                File.Copy(fullSource, destination, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Referenced-only mode for a protected source folder: stages the other
    /// charts of the same song (same folder and format, not recursive)
    /// byte-for-byte, plus the resources each of them references. A sibling
    /// that cannot be read as a chart is still kept verbatim; it simply
    /// contributes no resources.
    /// </summary>
    private static void StageSiblingCharts(
        string sourceRoot,
        string mainSourceChartPath,
        string outputFolder,
        string chartExtension,
        ResourceBudget budget,
        CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var mainChart = Path.GetFullPath(mainSourceChartPath);
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*" + chartExtension, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullSource = Path.GetFullPath(source);
            // The main chart is replaced by the working document, whose own
            // references are collected separately.
            if (!Path.GetExtension(fullSource).Equals(chartExtension, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullSource, mainChart, StringComparison.OrdinalIgnoreCase))
                continue;

            var bytes = File.ReadAllBytes(fullSource);
            budget.Reserve(bytes.LongLength);
            var destination = Path.GetFullPath(Path.Combine(outputFolder, Path.GetFileName(fullSource)));
            if (!IsStrictDescendant(outputFolder, destination))
                throw new InvalidOperationException("A package resource escaped the export staging directory.");
            File.WriteAllBytes(destination, bytes);

            var references = chartExtension.Equals(".mc", StringComparison.OrdinalIgnoreCase)
                ? ReadMalodyReferences(bytes)
                : ReadOsuReferences(bytes);
            foreach (var reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ChartResourceReference.TryResolve(reference, sourceRoot, out var resolved))
                    StageAtRelativePath(resolved, sourceRoot, outputFolder, budget);
            }
        }
    }

    private static IReadOnlyList<string> ReadMalodyReferences(byte[] bytes)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(WithoutUtf8Bom(bytes).Span);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return [];
        }

        var result = new List<string>();
        void Add(JsonNode? node)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                result.Add(text);
        }
        var meta = root?["meta"] as JsonObject;
        Add(meta?["cover"]);
        Add(meta?["background"]);
        Add((meta?["song"] as JsonObject)?["file"]);
        if (root?["note"] is JsonArray notes)
            foreach (var note in notes.OfType<JsonObject>())
                Add(note["sound"]);
        return result;
    }

    private static IReadOnlyList<string> ReadOsuReferences(byte[] bytes)
    {
        var result = new List<string>();
        var section = string.Empty;
        foreach (var raw in System.Text.Encoding.UTF8.GetString(WithoutUtf8Bom(bytes).Span).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }
            if (section.Equals("General", StringComparison.OrdinalIgnoreCase) &&
                line.StartsWith("AudioFilename:", StringComparison.OrdinalIgnoreCase))
                result.Add(line["AudioFilename:".Length..].Trim());
            else if (section.Equals("Events", StringComparison.OrdinalIgnoreCase))
            {
                // Background/video events carry a quoted file name.
                var firstQuote = line.IndexOf('"');
                var secondQuote = firstQuote < 0 ? -1 : line.IndexOf('"', firstQuote + 1);
                if (secondQuote > firstQuote + 1) result.Add(line[(firstQuote + 1)..secondQuote]);
            }
            else if (section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase))
            {
                var sample = line[(line.LastIndexOf(',') + 1)..];
                var fileName = sample[(sample.LastIndexOf(':') + 1)..].Trim();
                if (fileName.Length > 0 && sample.Contains(':')) result.Add(fileName);
            }
        }
        return result;
    }

    private static ReadOnlyMemory<byte> WithoutUtf8Bom(byte[] bytes) =>
        bytes.AsMemory(bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0);

    /// <summary>
    /// Rewrites staged .mc files as compact JSON. Sibling charts are foreign
    /// input: a UTF-8 BOM is skipped (it used to fail the whole export) and a
    /// sibling that is not valid JSON keeps its original bytes.
    /// </summary>
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
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(WithoutUtf8Bom(File.ReadAllBytes(path)));
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                continue;
            }
            using (document)
            {
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
    }

    private sealed class ResourceBudget(long limit, string sessionScratchRoot)
    {
        private readonly string scratchRoot = Path.GetFullPath(sessionScratchRoot);
        private long used;

        public bool IsManagedScratch(string path) => IsStrictDescendant(scratchRoot, path);

        public void Reserve(long bytes)
        {
            used = checked(used + Math.Max(0, bytes));
            if (used <= limit) return;
            throw new InvalidOperationException(LocalizationCatalog.Format(
                "谱包资源总量超过 {0} GB 上限，已中止导出。",
                limit / (1024 * 1024 * 1024)));
        }
    }

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
            // Cleanup must not replace the original export failure. The root is
            // process-owned scratch and can be reclaimed at session shutdown.
        }
    }

    private static bool IsSameOrDescendant(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative.Equals(".", StringComparison.Ordinal) ||
               !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
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

    private static string BuildOsuChartFileName(ChartDocument document) =>
        $"{SafeName(document.Metadata.Artist)} - {SafeName(document.Metadata.Title)} " +
        $"({SafeName(document.Metadata.Creator)}) [{SafeName(document.Metadata.Version)}].osu";

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
}

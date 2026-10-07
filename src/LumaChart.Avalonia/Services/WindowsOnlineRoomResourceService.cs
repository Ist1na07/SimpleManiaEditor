using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Online;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Windows filesystem adapter for the shared room-resource transport. It keeps
/// content-addressed downloads outside the WPF cache, validates every path/hash,
/// and materializes a caller-owned MC source that the desktop workspace can open.
/// </summary>
public sealed class WindowsOnlineRoomResourceService
{
    private const int CopyBufferBytes = 128 * 1024;
    private const int MaximumFiles = 4096;
    private const long MaximumSingleFileBytes = 512L * 1024 * 1024;
    private const long MaximumWorkspaceBytes = 1024L * 1024 * 1024;
    private const double MaximumArchiveCompressionRatio = 200d;
    private readonly string cacheRoot;
    private readonly string documentRoot;
    private readonly string scratchRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> cacheDownloadGates =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowsOnlineRoomResourceService(
        string cacheRoot,
        string documentRoot,
        string scratchRoot)
    {
        this.cacheRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(cacheRoot)
                ? throw new ArgumentException("A resource cache root is required.", nameof(cacheRoot))
                : cacheRoot);
        this.documentRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(documentRoot)
                ? throw new ArgumentException("A room document root is required.", nameof(documentRoot))
                : documentRoot);
        this.scratchRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(scratchRoot)
                ? throw new ArgumentException("A room scratch root is required.", nameof(scratchRoot))
                : scratchRoot);
    }

    public static WindowsOnlineRoomResourceService CreateDefault() => new(
        AvaloniaAppPaths.OnlineRoomResourceCacheRoot,
        AvaloniaAppPaths.OnlineRoomDocumentRoot,
        Path.Combine(AvaloniaAppPaths.SessionScratchRoot, "room-transfers"));

    public async Task<WindowsOnlineRoomResourceCacheStatus> InspectAsync(
        OnlineRoomResourceManifestDto manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var entries = ValidateEntries(manifest);
        if (entries.Count == 0)
            return new WindowsOnlineRoomResourceCacheStatus(
                manifest.TotalBytes,
                manifest.TotalBytes,
                0,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Directory.CreateDirectory(cacheRoot);
        var validHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long missingBytes = 0;
        var reusedCount = 0;
        foreach (var entry in DistinctBlobs(entries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CachePath(entry.Sha256);
            var valid = File.Exists(path) && new FileInfo(path).Length == entry.Length;
            if (valid)
                valid = string.Equals(
                    await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false),
                    entry.Sha256,
                    StringComparison.OrdinalIgnoreCase);
            if (valid)
            {
                validHashes.Add(entry.Sha256);
                reusedCount++;
            }
            else
            {
                missingBytes = checked(missingBytes + entry.Length);
            }
        }

        return new WindowsOnlineRoomResourceCacheStatus(
            entries.Sum(entry => entry.Length),
            missingBytes,
            reusedCount,
            validHashes);
    }

    public async Task<WindowsOnlineRoomMaterialization> DownloadAndMaterializeAsync(
        OnlineAccountSession session,
        Guid roomId,
        OnlineRoomResourceManifestDto manifest,
        IProgress<WindowsOnlineRoomTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(manifest);
        var entries = ValidateEntries(manifest);
        return entries.Count > 0
            ? await DownloadEntryManifestAsync(
                    session,
                    roomId,
                    manifest,
                    entries,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false)
            : await DownloadLegacyArchiveAsync(
                    session,
                    roomId,
                    manifest,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<WindowsOnlineRoomUploadPackage> BuildUploadPackageAsync(
        DesktopChartTab tab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var root = Path.GetFullPath(tab.CacheDirectory);
        if (!Directory.Exists(root) || !File.Exists(tab.WorkingPath))
            throw new InvalidDataException("当前谱面的编辑工作缓存不完整。");

        // Reparse points are skipped: a junction or symbolic link inside the
        // cache must not pull files from elsewhere into a room upload.
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            })
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                           !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                           !path.EndsWith(".luma-part", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0 || files.Length > MaximumFiles)
            throw new InvalidDataException("房间资源文件数量异常。");

        var result = new List<WindowsOnlineRoomUploadFile>(files.Length);
        long totalBytes = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (!IsInside(root, fullPath))
                throw new InvalidDataException("房间资源包含工作缓存之外的文件。");
            var relative = NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
            if (!paths.Add(relative)) throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间资源路径重复：{0}", relative));
            var length = new FileInfo(fullPath).Length;
            ValidateLength(length);
            totalBytes = checked(totalBytes + length);
            if (totalBytes > MaximumWorkspaceBytes)
                throw new InvalidDataException("房间资源超过 1 GB 限制。");
            var sha256 = await ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false);
            result.Add(new WindowsOnlineRoomUploadFile(relative, length, sha256, fullPath));
        }

        var workingRelative = NormalizeRelativePath(Path.GetRelativePath(root, tab.WorkingPath));
        if (!result.Any(item => item.Path.Equals(workingRelative, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("房间资源缺少 working.mc。");
        return new WindowsOnlineRoomUploadPackage(workingRelative, totalBytes, result);
    }

    public async Task<OnlineRoomResourceManifestDto> UploadAsync(
        OnlineAccountSession session,
        Guid roomId,
        WindowsOnlineRoomUploadPackage package,
        IProgress<WindowsOnlineRoomTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(package);
        var entries = package.Files
            .Select(file => new OnlineRoomResourceEntryDto(file.Path, file.Length, file.Sha256))
            .ToArray();
        var plan = await session.PlanRoomResourcesAsync(roomId, entries, cancellationToken)
            .ConfigureAwait(false);
        if (plan.MissingBytes < 0 || plan.MissingBytes > package.TotalBytes)
            throw new InvalidDataException("服务器返回的资源复用计划无效。");

        var filesByHash = package.Files
            .GroupBy(file => file.Sha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var missing = plan.MissingSha256.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (missing.Any(hash => !filesByHash.ContainsKey(hash)))
            throw new InvalidDataException("服务器请求了清单之外的资源。");

        long transferred = 0;
        foreach (var hash in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = filesByHash[hash];
            await using var source = new FileStream(
                file.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var before = transferred;
            await session.UploadRoomResourceBlobAsync(
                    roomId,
                    hash,
                    source,
                    file.Length,
                    new InlineProgress<long>(bytes => progress?.Report(
                        new WindowsOnlineRoomTransferProgress(
                            before + bytes,
                            plan.MissingBytes,
                            file.Path))),
                    cancellationToken)
                .ConfigureAwait(false);
            transferred = checked(transferred + file.Length);
            progress?.Report(new WindowsOnlineRoomTransferProgress(
                transferred,
                plan.MissingBytes,
                file.Path));
        }

        var manifest = await session.CommitRoomResourcesAsync(roomId, entries, cancellationToken)
            .ConfigureAwait(false);
        _ = ValidateEntries(manifest);
        return manifest;
    }

    private async Task<WindowsOnlineRoomMaterialization> DownloadEntryManifestAsync(
        OnlineAccountSession session,
        Guid roomId,
        OnlineRoomResourceManifestDto manifest,
        IReadOnlyList<ValidatedResourceEntry> entries,
        IProgress<WindowsOnlineRoomTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheRoot);
        var status = await InspectAsync(manifest, cancellationToken).ConfigureAwait(false);
        long transferred = 0;
        var transferTotal = status.MissingBytes;
        foreach (var entry in DistinctBlobs(entries)
                     .Where(entry => !status.ValidHashes.Contains(entry.Sha256)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DownloadBlobToCacheAsync(
                    session,
                    roomId,
                    entry,
                    transferred,
                    transferTotal,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            transferred = checked(transferred + entry.Length);
            progress?.Report(new WindowsOnlineRoomTransferProgress(
                transferred,
                transferTotal,
                entry.Path));
        }

        var repairedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var folder = AllocateDocumentFolder(roomId);
            try
            {
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = ResolveInside(folder, entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await CopyVerifiedFileAsync(
                            CachePath(entry.Sha256),
                            destination,
                            entry,
                            repairCacheOnSourceFailure: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                var normalizedChartEntry = NormalizeRelativePath(
                    string.IsNullOrWhiteSpace(manifest.ChartEntry)
                        ? "working.mc"
                        : manifest.ChartEntry);
                var chartEntry = entries.Single(entry => entry.Path.Equals(
                    normalizedChartEntry,
                    StringComparison.OrdinalIgnoreCase));
                return await CreateMaterializationAsync(
                        folder,
                        normalizedChartEntry,
                        entries.Select(entry => entry.Path).ToArray(),
                        cancellationToken,
                        chartEntry)
                    .ConfigureAwait(false);
            }
            catch (CachedResourceIntegrityException exception)
                when (repairedHashes.Add(exception.Entry.Sha256))
            {
                TryDeleteDirectory(folder);
                transferTotal = checked(transferTotal + exception.Entry.Length);
                await DownloadBlobToCacheAsync(
                        session,
                        roomId,
                        exception.Entry,
                        transferred,
                        transferTotal,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                transferred = checked(transferred + exception.Entry.Length);
                progress?.Report(new WindowsOnlineRoomTransferProgress(
                    transferred,
                    transferTotal,
                    exception.Entry.Path));
            }
            catch
            {
                TryDeleteDirectory(folder);
                throw;
            }
        }
    }

    private async Task DownloadBlobToCacheAsync(
        OnlineAccountSession session,
        Guid roomId,
        ValidatedResourceEntry entry,
        long transferredBefore,
        long transferTotal,
        IProgress<WindowsOnlineRoomTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destinationPath = CachePath(entry.Sha256);
        var gate = cacheDownloadGates.GetOrAdd(
            destinationPath,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsVerifiedCacheEntryAsync(
                    destinationPath,
                    entry,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.part";
            try
            {
                long downloaded;
                long written;
                string actualHash;
                await using (var destination = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 CopyBufferBytes,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using var verifyingDestination = new IncrementalIntegrityWriteStream(
                        destination,
                        entry.Length);
                    downloaded = await session.DownloadRoomResourceBlobAsync(
                            roomId,
                            entry.Sha256,
                            verifyingDestination,
                            new InlineProgress<long>(bytes => progress?.Report(
                                new WindowsOnlineRoomTransferProgress(
                                    transferredBefore + bytes,
                                    transferTotal,
                                    entry.Path))),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await verifyingDestination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    destination.Flush(flushToDisk: true);
                    written = verifyingDestination.BytesWritten;
                    actualHash = verifyingDestination.GetSha256();
                }

                if (downloaded != written || written != entry.Length)
                    throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("资源下载大小不一致：{0}", entry.Path));
                if (!actualHash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("资源 SHA-256 校验失败：{0}", entry.Path));

                // Publish only a fully flushed and verified blob. If an invalid cache
                // entry already exists it remains available for diagnosis/retry until
                // this atomic replacement succeeds; InspectAsync will never reuse it.
                File.Move(temporaryPath, destinationPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<bool> IsVerifiedCacheEntryAsync(
        string path,
        ValidatedResourceEntry entry,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != entry.Length)
            return false;
        return string.Equals(
            await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false),
            entry.Sha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<WindowsOnlineRoomMaterialization> DownloadLegacyArchiveAsync(
        OnlineAccountSession session,
        Guid roomId,
        OnlineRoomResourceManifestDto manifest,
        IProgress<WindowsOnlineRoomTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateLength(manifest.TotalBytes, MaximumWorkspaceBytes);
        ValidateSha256(manifest.Sha256);
        Directory.CreateDirectory(scratchRoot);
        var archivePath = Path.Combine(scratchRoot, $"room_{roomId:N}_{Guid.NewGuid():N}.zip");
        try
        {
            long downloaded;
            await using (var destination = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferBytes,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                downloaded = await session.DownloadRoomResourceArchiveAsync(
                        roomId,
                        destination,
                        new InlineProgress<long>(bytes => progress?.Report(
                            new WindowsOnlineRoomTransferProgress(
                                bytes,
                                manifest.TotalBytes,
                                "legacy-archive"))),
                        cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            if (downloaded != manifest.TotalBytes || new FileInfo(archivePath).Length != manifest.TotalBytes)
                throw new InvalidDataException("房间资源下载不完整。");
            var actualHash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("房间资源 SHA-256 校验失败。");

            var folder = AllocateDocumentFolder(roomId);
            try
            {
                var paths = await ExtractArchiveAsync(archivePath, folder, cancellationToken)
                    .ConfigureAwait(false);
                var chartEntry = string.IsNullOrWhiteSpace(manifest.ChartEntry)
                    ? "working.mc"
                    : manifest.ChartEntry;
                return await CreateMaterializationAsync(
                        folder,
                        chartEntry,
                        paths,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                TryDeleteDirectory(folder);
                throw;
            }
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    private static async Task<IReadOnlyList<string>> ExtractArchiveAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var extracted = new List<string>();
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (extracted.Count >= MaximumFiles)
                throw new InvalidDataException("房间资源文件数量异常。");
            ValidateLength(entry.Length);
            if (entry.CompressedLength == 0 && entry.Length > 0 ||
                entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > MaximumArchiveCompressionRatio)
                throw new InvalidDataException("房间资源压缩比异常。");
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumWorkspaceBytes)
                throw new InvalidDataException("房间资源解压后超过 1 GB 限制。");
            var relative = NormalizeRelativePath(entry.FullName);
            if (!uniquePaths.Add(relative))
                throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间资源路径重复：{0}", relative));
            var destination = ResolveInside(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(output, CopyBufferBytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (output.Length != entry.Length)
                throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间资源解压大小不一致：{0}", relative));
            extracted.Add(relative);
        }
        return extracted;
    }

    private static async Task<WindowsOnlineRoomMaterialization> CreateMaterializationAsync(
        string folder,
        string chartEntry,
        IReadOnlyList<string> resourcePaths,
        CancellationToken cancellationToken,
        ValidatedResourceEntry? verifiedChartEntry = null)
    {
        chartEntry = NormalizeRelativePath(
            string.IsNullOrWhiteSpace(chartEntry) ? "working.mc" : chartEntry);
        if (!Path.GetExtension(chartEntry).Equals(".mc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("房间主谱面不是 MC 文件。");
        var chartPath = ResolveInside(folder, chartEntry);
        if (!File.Exists(chartPath)) throw new InvalidDataException("房间资源缺少主谱面文件。");
        var sourcePath = Path.Combine(folder, "room-source.mc");
        if (!Path.GetFullPath(chartPath).Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            if (verifiedChartEntry == null)
            {
                await CopyFileAsync(chartPath, sourcePath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CopyVerifiedFileAsync(
                        chartPath,
                        sourcePath,
                        verifiedChartEntry,
                        repairCacheOnSourceFailure: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        return new WindowsOnlineRoomMaterialization(
            folder,
            sourcePath,
            resourcePaths.ToArray());
    }

    private IReadOnlyList<ValidatedResourceEntry> ValidateEntries(
        OnlineRoomResourceManifestDto manifest)
    {
        if (manifest.TotalBytes < 0 || manifest.TotalBytes > MaximumWorkspaceBytes)
            throw new InvalidDataException("房间资源清单大小无效。");
        if (manifest.Entries is not { Count: > 0 }) return [];
        if (manifest.Entries.Count > MaximumFiles)
            throw new InvalidDataException("房间资源文件数量异常。");

        var result = new List<ValidatedResourceEntry>(manifest.Entries.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blobLengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var entry in manifest.Entries)
        {
            var relative = NormalizeRelativePath(entry.Path);
            if (!paths.Add(relative)) throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间资源路径重复：{0}", relative));
            ValidateLength(entry.Length);
            ValidateSha256(entry.Sha256);
            if (blobLengths.TryGetValue(entry.Sha256, out var existingLength) &&
                existingLength != entry.Length)
                throw new InvalidDataException("相同资源哈希声明了不同大小。");
            blobLengths[entry.Sha256] = entry.Length;
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumWorkspaceBytes)
                throw new InvalidDataException("房间资源超过 1 GB 限制。");
            result.Add(new ValidatedResourceEntry(
                relative,
                entry.Length,
                entry.Sha256.ToLowerInvariant()));
        }

        var chartEntry = NormalizeRelativePath(
            string.IsNullOrWhiteSpace(manifest.ChartEntry) ? "working.mc" : manifest.ChartEntry);
        if (!paths.Contains(chartEntry))
            throw new InvalidDataException("房间资源清单缺少主谱面条目。");
        return result;
    }

    private static IReadOnlyList<ValidatedResourceEntry> DistinctBlobs(
        IReadOnlyList<ValidatedResourceEntry> entries) => entries
        .GroupBy(entry => entry.Sha256, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();

    private string AllocateDocumentFolder(Guid roomId)
    {
        Directory.CreateDirectory(documentRoot);
        // Invariant culture: the folder name must not follow a non-Gregorian
        // current calendar (for example the Thai Buddhist year).
        var folder = Path.Combine(
            documentRoot,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"room_{roomId:N}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Unreferenced room folders idle for longer than this are removed.</summary>
    public static readonly TimeSpan MaximumRoomDocumentAge = TimeSpan.FromDays(30);

    /// <summary>Upper bound for all materialized room folders together.</summary>
    public const long MaximumRoomDocumentBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Bounded retention for materialized <c>room_*</c> folders. Every folder
    /// that contains, or is contained in, one of <paramref name="inUsePaths"/>
    /// (the sources of open and suspended tabs) is kept, as is the
    /// <c>retained</c> tree of preserved room drafts, which is user work.
    /// </summary>
    public static ManagedStorageRetentionResult PruneRoomDocuments(
        string documentRoot,
        IEnumerable<string> inUsePaths,
        DateTimeOffset now) =>
        ManagedStorageRetentionService.PruneDirectories(
            documentRoot,
            "room_",
            inUsePaths,
            now,
            MaximumRoomDocumentAge,
            MaximumRoomDocumentBytes);

    private string CachePath(string sha256)
    {
        ValidateSha256(sha256);
        return Path.Combine(cacheRoot, sha256.ToLowerInvariant());
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("房间资源路径为空。");
        path = path.Replace('\\', '/').Trim('/');
        if (path.Length == 0 || path.Length > 512 || Path.IsPathRooted(path) || path.Contains(':'))
            throw new InvalidDataException("房间资源路径无效。");
        var segments = path.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidDataException("房间资源路径包含不安全片段。");
        return string.Join('/', segments);
    }

    private static string ResolveInside(string root, string relative)
    {
        var destination = Path.GetFullPath(Path.Combine(
            Path.GetFullPath(root),
            NormalizeRelativePath(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(root, destination))
            throw new InvalidDataException("房间资源路径越过目标目录。");
        return destination;
    }

    private static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               !relative.Equals(".", StringComparison.Ordinal) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void ValidateLength(long length, long maximum = MaximumSingleFileBytes)
    {
        if (length < 0 || length > maximum)
            throw new InvalidDataException("房间资源文件大小无效。");
    }

    private static void ValidateSha256(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("资源 SHA-256 格式不正确。");
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, CopyBufferBytes, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyVerifiedFileAsync(
        string sourcePath,
        string destinationPath,
        ValidatedResourceEntry entry,
        bool repairCacheOnSourceFailure,
        CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length != entry.Length)
                throw CreateSourceIntegrityException(entry, repairCacheOnSourceFailure);
            source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (CachedResourceIntegrityException)
        {
            TryDeleteFile(destinationPath);
            throw;
        }
        catch (Exception exception)
            when (repairCacheOnSourceFailure &&
                  exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(destinationPath);
            throw new CachedResourceIntegrityException(entry, exception);
        }

        try
        {
            await using (source.ConfigureAwait(false))
            {
                await using var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var verifyingDestination = new IncrementalIntegrityWriteStream(
                    destination,
                    entry.Length);
                var buffer = new byte[CopyBufferBytes];
                while (true)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await source.ReadAsync(
                                buffer.AsMemory(),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                        when (repairCacheOnSourceFailure &&
                              exception is IOException or UnauthorizedAccessException)
                    {
                        throw new CachedResourceIntegrityException(entry, exception);
                    }

                    if (bytesRead == 0) break;
                    if (bytesRead > entry.Length - verifyingDestination.BytesWritten)
                    {
                        throw CreateSourceIntegrityException(
                            entry,
                            repairCacheOnSourceFailure);
                    }
                    await verifyingDestination.WriteAsync(
                            buffer.AsMemory(0, bytesRead),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await verifyingDestination.FlushAsync(cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (verifyingDestination.BytesWritten != entry.Length ||
                    !verifyingDestination.GetSha256().Equals(
                        entry.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw CreateSourceIntegrityException(
                        entry,
                        repairCacheOnSourceFailure);
                }
            }
        }
        catch
        {
            TryDeleteFile(destinationPath);
            throw;
        }
    }

    private static Exception CreateSourceIntegrityException(
        ValidatedResourceEntry entry,
        bool repairCacheOnSourceFailure) =>
        repairCacheOnSourceFailure
            ? new CachedResourceIntegrityException(entry)
            : new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间资源在创建本地源副本时发生变化：{0}", entry.Path));

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record ValidatedResourceEntry(string Path, long Length, string Sha256);

    private sealed class CachedResourceIntegrityException : IOException
    {
        public CachedResourceIntegrityException(
            ValidatedResourceEntry entry,
            Exception? innerException = null)
            : base(AvaloniaLocalizationCatalog.Format("本地房间资源缓存在物化期间发生变化：{0}", entry.Path), innerException)
        {
            Entry = entry;
        }

        public ValidatedResourceEntry Entry { get; }
    }

    private sealed class IncrementalIntegrityWriteStream : Stream
    {
        private readonly Stream destination;
        private readonly long maximumLength;
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? completedHash;

        public IncrementalIntegrityWriteStream(Stream destination, long maximumLength)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if (!destination.CanWrite)
                throw new ArgumentException("The integrity destination must be writable.", nameof(destination));
            if (maximumLength < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumLength));
            this.destination = destination;
            this.maximumLength = maximumLength;
        }

        public long BytesWritten { get; private set; }

        public string GetSha256()
        {
            completedHash ??= Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return completedHash;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => destination.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            destination.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ValidateWrite(count);
            destination.Write(buffer, offset, count);
            hash.AppendData(buffer, offset, count);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ValidateWrite(buffer.Length);
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.Span);
            BytesWritten += buffer.Length;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return WriteArrayAsync(buffer, offset, count, cancellationToken);
        }

        private async Task WriteArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            ValidateWrite(count);
            await destination.WriteAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken)
                .ConfigureAwait(false);
            hash.AppendData(buffer, offset, count);
            BytesWritten += count;
        }

        private void ValidateWrite(int count)
        {
            if (completedHash != null)
                throw new InvalidOperationException("The integrity hash has already been finalized.");
            if (count < 0 || BytesWritten > maximumLength - count)
                throw new InvalidDataException("资源下载超过清单声明大小。");
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) hash.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}

public sealed record WindowsOnlineRoomMaterialization(
    string DirectoryPath,
    string ChartPath,
    IReadOnlyList<string> ResourcePaths);

public sealed record WindowsOnlineRoomUploadPackage(
    string ChartEntry,
    long TotalBytes,
    IReadOnlyList<WindowsOnlineRoomUploadFile> Files);

public sealed record WindowsOnlineRoomUploadFile(
    string Path,
    long Length,
    string Sha256,
    string SourcePath);

public sealed record WindowsOnlineRoomResourceCacheStatus(
    long TotalBytes,
    long MissingBytes,
    int ReusedCount,
    IReadOnlySet<string> ValidHashes);

public readonly record struct WindowsOnlineRoomTransferProgress(
    long TransferredBytes,
    long TotalBytes,
    string CurrentPath)
{
    public double Fraction => TotalBytes <= 0
        ? 1
        : Math.Clamp(TransferredBytes / (double)TotalBytes, 0, 1);
}

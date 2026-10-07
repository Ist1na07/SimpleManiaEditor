using System.Globalization;
using System.IO.Compression;

namespace LumaChart.Application.Archives;

/// <summary>
/// Safely extracts Malody and osu! chart archives into a new directory owned by
/// the caller's workspace. The service has no UI or platform-framework dependency.
/// </summary>
public static class ChartArchiveImportService
{
    public const int MaximumFileEntries = 16_384;
    public const long MaximumSingleFileBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    public const double MaximumCompressionRatio = 200d;

    private const int CopyBufferSize = 128 * 1024;
    private const int MaximumDestinationStemCharacters = 80;
    private const string OwnershipMarkerName = ".lumachart-import-owner";

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into a uniquely named child of
    /// <paramref name="destinationParent"/>. A failed or cancelled extraction
    /// removes the destination created by this operation.
    /// </summary>
    public static Task<ChartArchiveImportResult> ExtractToUniqueDirectoryAsync(
        string archivePath,
        string destinationParent,
        CancellationToken cancellationToken = default) =>
        ExtractToUniqueDirectoryAsync(
            archivePath,
            destinationParent,
            ManagedArchiveDirectoryCreator.Instance,
            cancellationToken);

    public static async Task<ChartArchiveImportResult> ExtractToUniqueDirectoryAsync(
        string archivePath,
        string destinationParent,
        IArchiveDirectoryCreator directoryCreator,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationParent);
        ArgumentNullException.ThrowIfNull(directoryCreator);

        archivePath = Path.GetFullPath(archivePath);
        destinationParent = Path.GetFullPath(destinationParent);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("The chart archive does not exist.", archivePath);

        var extension = Path.GetExtension(archivePath);
        if (!extension.Equals(".mcz", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".osz", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only Malody .mcz and osu! .osz chart archives are supported.");

        Directory.CreateDirectory(destinationParent);
        var stem = SanitizeFileName(Path.GetFileNameWithoutExtension(archivePath));
        if (stem.Length > MaximumDestinationStemCharacters)
            stem = stem[..MaximumDestinationStemCharacters].Trim();

        var ownershipToken = Guid.NewGuid().ToString("N");
        var stagingRoot = CreateOwnedStagingRoot(
            destinationParent,
            stem,
            ownershipToken,
            directoryCreator);
        var committed = false;
        try
        {
            var extractedFiles = 0;
            long extractedBytes = 0;
            var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plannedEntries = new List<(ZipArchiveEntry Entry, string RelativePath)>();

            using var archive = ZipFile.OpenRead(archivePath);
            long declaredBytes = 0;
            long compressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectLinkOrReparseEntry(entry);
                if (plannedEntries.Count >= MaximumFileEntries)
                    throw new InvalidDataException(
                        $"The chart archive contains more than {MaximumFileEntries:N0} entries.");

                var isDirectory = string.IsNullOrWhiteSpace(entry.Name);
                var relativePath = ValidateEntry(entry, extractedPaths, isDirectory);
                plannedEntries.Add((entry, relativePath));
                declaredBytes = checked(declaredBytes + entry.Length);
                compressedBytes = checked(compressedBytes + Math.Max(0, entry.CompressedLength));
            }

            // Reject before writing anything. The per-entry ratio check only looks
            // at entries of 1 MiB or more, so thousands of small, extremely
            // compressed entries could still expand far beyond the archive; the
            // streaming limit alone would only stop them after gigabytes of writes.
            if (declaredBytes > MaximumExpandedBytes)
                throw new InvalidDataException("The chart archive declares more expanded data than permitted.");
            if (declaredBytes >= 1024 * 1024 &&
                (compressedBytes <= 0 || declaredBytes / (double)compressedBytes > MaximumCompressionRatio))
                throw new InvalidDataException("The chart archive contains an abnormal overall compression ratio.");

            foreach (var planned in plannedEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(planned.Entry.Name)) continue;
                extractedFiles++;
                var destination = Path.GetFullPath(Path.Combine(
                    stagingRoot,
                    planned.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsStrictDescendant(stagingRoot, destination))
                    throw new InvalidDataException("The chart archive contains a path outside its destination.");

                var directory = Path.GetDirectoryName(destination)
                                ?? throw new InvalidDataException("The chart archive contains an invalid file path.");
                Directory.CreateDirectory(directory);
                await ExtractEntryAsync(
                        planned.Entry,
                        destination,
                        extractedBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                extractedBytes = checked(extractedBytes + planned.Entry.Length);
            }

            var relativeChartPaths = Directory.EnumerateFiles(
                    stagingRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Where(IsOpenableChartPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.GetRelativePath(stagingRoot, path))
                .ToArray();

            var destinationRoot = CommitStagingRoot(destinationParent, stagingRoot, stem);
            committed = true;
            TryDeleteFile(Path.Combine(destinationRoot, OwnershipMarkerName));
            var chartPaths = relativeChartPaths
                .Select(relative => Path.GetFullPath(Path.Combine(destinationRoot, relative)))
                .ToArray();

            return new ChartArchiveImportResult(
                destinationRoot,
                chartPaths,
                extractedBytes,
                extractedFiles);
        }
        catch
        {
            if (!committed)
                TryDeleteOwnedDestination(destinationParent, stagingRoot, ownershipToken);
            throw;
        }
    }

    private static string ValidateEntry(
        ZipArchiveEntry entry,
        HashSet<string> extractedPaths,
        bool isDirectory)
    {
        var relativePath = entry.FullName.Replace('\\', '/');
        if (isDirectory) relativePath = relativePath.TrimEnd('/');
        var parts = relativePath.Split('/');
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.Contains(':') ||
            parts.Any(part =>
                part.Length == 0 ||
                part is "." or ".." ||
                part.EndsWith(' ') ||
                part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                part.Any(character =>
                    char.IsControl(character) ||
                    char.GetUnicodeCategory(character) == UnicodeCategory.Format) ||
                IsReservedWindowsFileName(part)))
            throw new InvalidDataException("The chart archive contains an unsafe or unsupported path.");

        if (!extractedPaths.Add(relativePath))
            throw new InvalidDataException("The chart archive contains duplicate file paths.");
        if (isDirectory && entry.Length != 0)
            throw new InvalidDataException("The chart archive contains a directory entry with data.");
        if (entry.Length < 0 || entry.Length > MaximumSingleFileBytes)
            throw new InvalidDataException("The chart archive contains an abnormally large file.");
        if (entry.Length >= 1024 * 1024 &&
            (entry.CompressedLength <= 0 ||
             entry.Length / (double)entry.CompressedLength > MaximumCompressionRatio))
            throw new InvalidDataException("The chart archive contains an abnormal compression ratio.");

        return relativePath;
    }

    private static void RejectLinkOrReparseEntry(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (unixFileType == 0xA000 || windowsAttributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The chart archive contains a symbolic link or reparse point.");
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destination,
        long alreadyExtractedBytes,
        CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[CopyBufferSize];
        long entryBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            entryBytes = checked(entryBytes + read);
            var totalBytes = checked(alreadyExtractedBytes + entryBytes);
            if (entryBytes > entry.Length || totalBytes > MaximumExpandedBytes)
                throw new InvalidDataException("The chart archive exceeds its declared or permitted expanded size.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (entryBytes != entry.Length)
            throw new InvalidDataException("A chart archive entry length does not match its extracted content.");
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreateOwnedStagingRoot(
        string destinationParent,
        string stem,
        string ownershipToken,
        IArchiveDirectoryCreator directoryCreator)
    {
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var name = $".{stem}.{Guid.NewGuid():N}.lumachart-importing";
            var candidate = Path.GetFullPath(Path.Combine(destinationParent, name));
            if (!IsStrictDescendant(destinationParent, candidate))
                throw new InvalidDataException("The archive destination escaped its workspace root.");

            if (!directoryCreator.TryCreate(candidate)) continue;

            var marker = Path.Combine(candidate, OwnershipMarkerName);
            try
            {
                using var output = new FileStream(
                    marker,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128,
                    FileOptions.WriteThrough);
                using var writer = new StreamWriter(output);
                writer.Write(ownershipToken);
                writer.Flush();
                output.Flush(flushToDisk: true);
            }
            catch
            {
                try { Directory.Delete(candidate, recursive: false); } catch { }
                throw;
            }
            return candidate;
        }

        throw new IOException("Unable to allocate an owned archive staging directory.");
    }

    private static string CommitStagingRoot(
        string destinationParent,
        string stagingRoot,
        string stem)
    {
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var name = suffix == 1 ? stem : $"{stem}_{suffix}";
            var candidate = Path.GetFullPath(Path.Combine(destinationParent, name));
            if (!IsStrictDescendant(destinationParent, candidate))
                throw new InvalidDataException("The archive destination escaped its workspace root.");
            try
            {
                Directory.Move(stagingRoot, candidate);
                return candidate;
            }
            catch (IOException) when (Directory.Exists(candidate) || File.Exists(candidate))
            {
                // A concurrent importer committed this readable name first.
            }
        }

        throw new IOException("Unable to commit a unique chart archive destination.");
    }

    private static bool IsOpenableChartPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".osu", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsReservedWindowsFileName(string part)
    {
        var stem = part.Split('.', 2)[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               char.IsDigit(stem[3]) &&
               stem[3] != '0' &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        value = value.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "chart-archive" : value;
    }

    private static void TryDeleteOwnedDestination(
        string destinationParent,
        string destinationRoot,
        string ownershipToken)
    {
        try
        {
            if (!IsStrictDescendant(destinationParent, destinationRoot) || !Directory.Exists(destinationRoot))
                return;
            var directory = new DirectoryInfo(destinationRoot);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var marker = Path.Combine(destinationRoot, OwnershipMarkerName);
            if (!File.Exists(marker) ||
                !string.Equals(File.ReadAllText(marker), ownershipToken, StringComparison.Ordinal))
                return;
            Directory.Delete(destinationRoot, recursive: true);
        }
        catch
        {
            // Preserve the extraction failure. A caller may surface or log the
            // original exception without it being replaced by cleanup failure.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Reservation cleanup is best effort and must not hide the primary IO error.
        }
    }

}

public sealed record ChartArchiveImportResult(
    string DestinationRoot,
    IReadOnlyList<string> ChartPaths,
    long ExtractedBytes,
    int ExtractedFiles);

/// <summary>
/// Provides platform-specific create-new directory semantics without importing
/// native APIs into the shared application assembly.
/// </summary>
public interface IArchiveDirectoryCreator
{
    bool TryCreate(string absolutePath);
}

public sealed class ManagedArchiveDirectoryCreator : IArchiveDirectoryCreator
{
    public static ManagedArchiveDirectoryCreator Instance { get; } = new();

    public bool TryCreate(string absolutePath)
    {
        if (Directory.Exists(absolutePath) || File.Exists(absolutePath)) return false;
        try
        {
            Directory.CreateDirectory(absolutePath);
            return true;
        }
        catch (IOException) when (Directory.Exists(absolutePath) || File.Exists(absolutePath))
        {
            return false;
        }
    }
}

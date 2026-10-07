using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Application.Workspaces;

public sealed record ChartBackupArchive(
    ChartDocument Document,
    string BackupPath,
    string? OriginalPath,
    DateTimeOffset CreatedAt);

/// <summary>
/// UI-free MC backup archive implementation shared by Windows desktop clients.
/// The caller owns the backup root so WPF and Avalonia never overwrite one
/// another's migration-time settings or cache trees.
/// </summary>
public static class ChartBackupArchiveService
{
    private const string MarkerName = "_luma_backup";
    private const string BackupExtension = ".mcbackup";

    /// <summary>Newest automatic backups kept for one chart.</summary>
    public const int MaximumBackupsPerChart = 30;

    /// <summary>Upper bound for all automatic backups together.</summary>
    public const long MaximumTotalBackupBytes = 512L * 1024 * 1024;

    private static readonly object WriteGate = new();

    public static string CreateBackup(
        string backupRoot,
        ChartDocument source,
        string? originalPath = null,
        DateTimeOffset? timestamp = null) =>
        CreateBackup(
            backupRoot,
            source,
            originalPath,
            timestamp,
            MaximumBackupsPerChart,
            MaximumTotalBackupBytes);

    /// <summary>
    /// Writes one backup and then applies the retention limits: at most
    /// <paramref name="maximumBackupsPerChart"/> per chart folder and at most
    /// <paramref name="maximumTotalBytes"/> overall. Only the oldest
    /// <c>.mcbackup</c> files are removed, and never the backup just written.
    /// </summary>
    public static string CreateBackup(
        string backupRoot,
        ChartDocument source,
        string? originalPath,
        DateTimeOffset? timestamp,
        int maximumBackupsPerChart,
        long maximumTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        backupRoot = ValidateBackupRoot(backupRoot);
        var createdAt = timestamp ?? DateTimeOffset.Now;
        var document = source.Clone();
        originalPath = NormalizePath(originalPath) ?? NormalizePath(source.FilePath);
        document.UnknownRoot[MarkerName] = new JsonObject
        {
            ["original_path"] = originalPath,
            ["created_at"] = createdAt.ToString("O", CultureInfo.InvariantCulture),
            ["editor"] = "LumaChart Studio"
        };

        var identity = originalPath ??
                       $"{document.Metadata.Artist}\n{document.Metadata.Title}\n{document.Metadata.Version}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..10];
        var chartFolder = $"{SafeSegment(document.Metadata.Title, 54)}_{hash}";
        var folder = Path.Combine(backupRoot, chartFolder);
        var display =
            $"{SafeSegment(document.Metadata.Title, 72)} [{SafeSegment(document.Metadata.Version, 40)}]";
        // Invariant culture: a Thai (Buddhist calendar) or other non-Gregorian
        // current culture would otherwise put a different year into the name.
        var stem = string.Create(
            CultureInfo.InvariantCulture,
            $"{createdAt.LocalDateTime:yyyy-MM-dd_HH-mm-ss}__{display}");

        lock (WriteGate)
        {
            Directory.CreateDirectory(folder);
            var path = UniquePath(Path.Combine(folder, stem + BackupExtension));
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(
                    temporary,
                    MalodyMcSerializer.Serialize(document),
                    new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporary);
            }

            PruneBackups(backupRoot, folder, path, maximumBackupsPerChart, maximumTotalBytes);
            return path;
        }
    }

    /// <summary>
    /// Best-effort retention. Failures never fail the backup that was just
    /// written; reparse points are neither followed nor deleted.
    /// </summary>
    private static void PruneBackups(
        string backupRoot,
        string chartFolder,
        string keepPath,
        int maximumBackupsPerChart,
        long maximumTotalBytes)
    {
        try
        {
            var perChart = ListBackups(chartFolder)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .ToArray();
            foreach (var file in perChart.Skip(Math.Max(1, maximumBackupsPerChart)))
                TryDeleteBackup(file, keepPath);

            if (maximumTotalBytes <= 0) return;
            var root = new DirectoryInfo(backupRoot);
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            var all = root
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .SelectMany(directory => ListBackups(directory.FullName))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.Ordinal)
                .ToList();
            var total = all.Sum(file => file.Length);
            foreach (var file in all)
            {
                if (total <= maximumTotalBytes) break;
                var length = file.Length;
                if (TryDeleteBackup(file, keepPath)) total -= length;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Retention is reattempted after the next backup.
        }
    }

    private static IEnumerable<FileInfo> ListBackups(string folder)
    {
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists) return [];
        return directory
            .EnumerateFiles("*" + BackupExtension, SearchOption.TopDirectoryOnly)
            .Where(file =>
                file.Extension.Equals(BackupExtension, StringComparison.OrdinalIgnoreCase) &&
                !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .ToArray();
    }

    private static bool TryDeleteBackup(FileInfo file, string keepPath)
    {
        if (string.Equals(file.FullName, Path.GetFullPath(keepPath), StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ChartBackupArchive LoadBackup(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        var document = MalodyMcSerializer.Parse(File.ReadAllText(path), path);
        var marker = document.UnknownRoot[MarkerName] as JsonObject;
        var originalPath = ReadString(marker, "original_path");
        var createdText = ReadString(marker, "created_at");
        var createdAt = DateTimeOffset.TryParse(
            createdText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : new DateTimeOffset(File.GetLastWriteTime(path));
        document.UnknownRoot.Remove(MarkerName);
        return new ChartBackupArchive(
            document,
            path,
            NormalizePath(originalPath),
            createdAt);
    }

    private static string ValidateBackupRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Backup root is required.", nameof(path));
        var root = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(root)?
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) ||
            string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The backup root cannot be a volume root.");
        return root;
    }

    private static string? ReadString(JsonObject? source, string name)
    {
        if (source?[name] is not JsonValue value) return null;
        return value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string SafeSegment(string? value, int maximumLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Untitled" : value.Trim();
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim().TrimEnd('.');
        if (value.Length > maximumLength) value = value[..maximumLength].TrimEnd();
        return string.IsNullOrWhiteSpace(value) ? "Untitled" : value;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(folder, $"{stem}_{index}{extension}");
            if (!File.Exists(candidate)) return candidate;
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
            // Preserve the primary backup write failure.
        }
    }
}

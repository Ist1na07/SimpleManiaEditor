using Avalonia.Platform.Storage;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// How long a staged copy of a picked document has to remain readable.
/// </summary>
public enum PickedFileRetention
{
    /// <summary>
    /// The caller imports the bytes into a chart or library folder straight
    /// away, so the copy only has to outlive that import.
    /// </summary>
    Transient,

    /// <summary>
    /// The resolved path itself is persisted in settings and read again in a
    /// later session, so it must not live in a prunable cache.
    /// </summary>
    Durable
}

/// <summary>
/// What the staging logic needs from a picked document. Avalonia's own
/// <see cref="IStorageFile"/> cannot be implemented outside Avalonia, so the
/// platform-independent behavior is expressed against this instead.
/// </summary>
public interface IPickedDocument
{
    /// <summary>File name as the picker reported it, extension included.</summary>
    string Name { get; }

    /// <summary>A readable file-system path, or null for a content document.</summary>
    string? LocalPath { get; }

    Task<Stream> OpenReadAsync();
}

/// <summary>
/// Turns a document returned by a platform picker into a path the shared import
/// pipelines can open.
///
/// Windows pickers hand back real file-system paths, which are returned
/// unchanged. Android's Storage Access Framework hands back content documents
/// that have no path at all, so the bytes are copied into an app-private root
/// first. Without this every shared "choose a file" flow reads a null local
/// path on Android and silently does nothing.
/// </summary>
public static class PlatformPickedFiles
{
    private static readonly TimeSpan TransientLifetime = TimeSpan.FromHours(6);

    public static Task<string?> ResolveLocalPathAsync(
        IStorageFile? file,
        PickedFileRetention retention,
        CancellationToken cancellationToken = default) =>
        ResolveLocalPathAsync(
            file == null ? null : new StorageFileDocument(file),
            retention,
            cancellationToken);

    /// <summary>
    /// Returns a readable local path for <paramref name="file"/>, or null when
    /// nothing was picked. Throws the underlying I/O failure when a document was
    /// picked but its bytes cannot be staged, so the caller can report it
    /// instead of behaving like a cancellation.
    /// </summary>
    public static async Task<string?> ResolveLocalPathAsync(
        IPickedDocument? file,
        PickedFileRetention retention,
        CancellationToken cancellationToken = default)
    {
        if (file == null) return null;

#if !LUMACHART_IOS
        var localPath = file.LocalPath;
        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            return Path.GetFullPath(localPath);
#else
        // iOS is deliberately excluded from the shortcut above, and this is not
        // caution — the shortcut is actively wrong here.
        //
        // Avalonia's iOS open-file picker uses UIDocumentPickerViewController
        // with asCopy: false (IOSStorageProvider.cs), so the item is the file
        // where it actually lives — iCloud Drive, another app's container — and
        // its path is only readable inside a security scope that Avalonia opens
        // around each operation and closes again. File.Exists therefore answers
        // true while the picker's scope is still up and false a moment later,
        // and the caller is left holding a path to a file it can never open.
        //
        // That is exactly how it failed: choosing an audio file filled the
        // import dialog, and confirming the name a few seconds later reported
        // "音频文件已不存在，请重新选择。" Copying through OpenReadAsync below is
        // correct on every platform; it is only skipped elsewhere because a
        // desktop pick already is a durable path.
#endif

        var root = retention == PickedFileRetention.Durable
            ? AvaloniaAppPaths.PickedFileDurableRoot
            : AvaloniaAppPaths.PickedFileStagingRoot;
        Directory.CreateDirectory(root);
        if (retention == PickedFileRetention.Transient) PruneStaleStagedFiles(root);

        var destination = CreateUniquePath(root, MakeSafeFileName(file.Name));
        try
        {
            await using var source = await file.OpenReadAsync().ConfigureAwait(false);
            await using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A half-written stage would be read back as a truncated audio or
            // image file, which fails much later and much less clearly.
            TryDelete(destination);
            throw;
        }

        return destination;
    }

    /// <summary>
    /// Drops staged copies old enough that their import has certainly finished.
    /// Pruning by age never removes the file this pick is about to write, which
    /// clearing the whole directory could do while another import still reads
    /// from it.
    /// </summary>
    private static void PruneStaleStagedFiles(string root)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TransientLifetime;
            foreach (var path in Directory.EnumerateFiles(root))
            {
                if (File.GetLastWriteTimeUtc(path) > cutoff) continue;
                TryDelete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Stale stages cost cache space but must never block a pick.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The next prune tries again.
        }
    }

    private static string CreateUniquePath(string root, string fileName)
    {
        var destination = Path.Combine(root, fileName);
        if (!File.Exists(destination)) return destination;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            destination = Path.Combine(root, $"{stem} ({suffix}){extension}");
            if (!File.Exists(destination)) return destination;
        }

        throw new IOException("暂存目录中同名文件过多。");
    }

    private sealed class StorageFileDocument(IStorageFile file) : IPickedDocument
    {
        public string Name => file.Name;
        public string? LocalPath => file.TryGetLocalPath();
        public Task<Stream> OpenReadAsync() => file.OpenReadAsync();
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(safe) ? "picked-file" : safe;
    }
}

using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Windows Avalonia equivalent of the WPF library's weak 320-pixel thumbnail cache.
/// Streams are fully decoded before they are closed, so images never lock song folders.
/// </summary>
internal static class AvaloniaLibraryThumbnailCache
{
    internal const int MaximumRetainedEntries = 512;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        FileStamp stamp;
        try
        {
            stamp = ReadStamp(path);
        }
        catch
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var entry))
            {
                if (entry.Stamp == stamp &&
                    entry.Bitmap.TryGetTarget(out var cached))
                    return cached;

                Cache.Remove(path);
            }
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.SequentialScan);
            var bitmap = Bitmap.DecodeToWidth(stream, 320, BitmapInterpolationMode.HighQuality);
            var decodedStamp = ReadStamp(path);
            if (decodedStamp == stamp)
            {
                lock (Gate)
                {
                    Cache[path] = new CacheEntry(
                        decodedStamp,
                        new WeakReference<Bitmap>(bitmap));
                    TrimToBound();
                }
            }
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    internal static int RetainedEntryCount
    {
        get
        {
            lock (Gate) return Cache.Count;
        }
    }

    internal static void ClearForTesting()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    private static FileStamp ReadStamp(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        if (!info.Exists)
            throw new FileNotFoundException("Thumbnail source was not found.", path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static void TrimToBound()
    {
        while (Cache.Count > MaximumRetainedEntries)
            Cache.Remove(Cache.Keys.First());
    }

    private sealed record CacheEntry(
        FileStamp Stamp,
        WeakReference<Bitmap> Bitmap);

    private readonly record struct FileStamp(
        long Length,
        long LastWriteUtcTicks);
}

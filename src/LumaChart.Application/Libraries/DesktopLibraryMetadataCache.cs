using System.Collections.Concurrent;

namespace LumaChart.Application.Libraries;

/// <summary>Header-only index cache; invalidated by file size and last-write time.</summary>
public sealed class DesktopLibraryMetadataCache
{
    private readonly ConcurrentDictionary<string, Cached> entries = new(StringComparer.OrdinalIgnoreCase);
    public DesktopLibraryChartEntry? Read(string file, string root)
    {
        var info = new FileInfo(file);
        var key = root + "\n" + file;
        if (entries.TryGetValue(key, out var cached) && cached.Length == info.Length && cached.ModifiedUtc == info.LastWriteTimeUtc)
            return cached.Entry;
        var chart = DesktopChartLibraryService.ReadChart(file, root);
        if (entries.Count > 200000) entries.Clear();
        entries[key] = new(info.Length, info.LastWriteTimeUtc, chart);
        return chart;
    }
    private sealed record Cached(long Length, DateTime ModifiedUtc, DesktopLibraryChartEntry? Entry);
}

using System.Collections.Concurrent;

namespace LumaChart.Application.Libraries;

/// <summary>Header-only index cache; invalidated by file size and last-write time.</summary>
public sealed class DesktopLibraryMetadataCache
{
    private readonly ConcurrentDictionary<string, Cached> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LibraryCachedHeader> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly MapperLibraryStore? store;
    private readonly object loadGate = new();
    private bool loaded;
    public long CacheHits;
    public long HeaderReads;
    public IReadOnlyList<DesktopLibraryChartEntry> CachedCharts { get { LoadPersistedHeaders(); return entries.Values.Select(e => e.Entry).Where(e => e != null).Cast<DesktopLibraryChartEntry>().ToArray(); } }
    public DesktopLibraryMetadataCache(MapperLibraryStore? store = null)
    {
        this.store = store;
    }
    public void LoadPersistedHeaders()
    {
        if (Volatile.Read(ref loaded)) return;
        lock (loadGate)
        {
            if (loaded) return;
            if (store != null) foreach (var header in store.LoadHeaders())
                entries[header.Path] = new(header.Length, new DateTime(header.ModifiedTicks, DateTimeKind.Utc), header.Entry);
            Volatile.Write(ref loaded, true);
        }
    }
    public void Flush()
    {
        if (store == null || pending.IsEmpty) return;
        var batch = pending.ToArray(); store.UpdateHeaders(batch.Select(pair => pair.Value));
        foreach (var pair in batch) ((ICollection<KeyValuePair<string, LibraryCachedHeader>>)pending).Remove(pair);
    }
    public DesktopLibraryChartEntry? Read(string file, string root)
    {
        LoadPersistedHeaders();
        var info = new FileInfo(file);
        var key = Path.GetFullPath(file);
        if (entries.TryGetValue(key, out var cached) && cached.Length == info.Length && cached.ModifiedUtc == info.LastWriteTimeUtc)
        { Interlocked.Increment(ref CacheHits); return cached.Entry; }
        Interlocked.Increment(ref HeaderReads);
        var chart = DesktopChartLibraryService.ReadChart(file, root);
        entries[key] = new(info.Length, info.LastWriteTimeUtc, chart);
        pending[key] = new(key, info.Length, info.LastWriteTimeUtc.Ticks, chart);
        return chart;
    }
    private sealed record Cached(long Length, DateTime ModifiedUtc, DesktopLibraryChartEntry? Entry);
}

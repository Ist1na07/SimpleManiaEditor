using System.Collections.Concurrent;

namespace LumaChart.Application.Libraries;

public readonly record struct DesktopLibraryBackfillProgress(int Processed, int Total);

/// <summary>Header-only index cache; invalidated by file size and last-write time.
/// A legacy row that predates playable-length/BPM columns is revisited once in
/// the background, so the UI can keep serving the cached snapshot immediately.</summary>
public sealed class DesktopLibraryMetadataCache
{
    private readonly ConcurrentDictionary<string, Cached> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LibraryCachedHeader> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly MapperLibraryStore? store;
    private readonly object loadGate = new();
    private readonly object backfillGate = new();
    private Task? backfillTask;
    private int backfillProcessed;
    private int backfillTotal;
    private bool loaded;
    public long CacheHits;
    public long HeaderReads;

    public DesktopLibraryMetadataCache(MapperLibraryStore? store = null)
    {
        this.store = store;
    }

    public Task? BackfillTask { get { lock (backfillGate) return backfillTask; } }
    public bool IsBackfilling => BackfillTask is { IsCompleted: false };
    public int PendingCount => pending.Count;
    public int BackfillProcessed => Volatile.Read(ref backfillProcessed);
    public int BackfillTotal => Volatile.Read(ref backfillTotal);

    public IReadOnlyList<DesktopLibraryChartEntry> CachedCharts
    {
        get
        {
            LoadPersistedHeaders();
            return entries.Values.Select(e => e.Entry).Where(e => e != null).Cast<DesktopLibraryChartEntry>().ToArray();
        }
    }

    public IReadOnlyList<string> CachedPaths
    {
        get { LoadPersistedHeaders(); return entries.Keys.ToArray(); }
    }

    public void LoadPersistedHeaders()
    {
        if (Volatile.Read(ref loaded)) return;
        lock (loadGate)
        {
            if (loaded) return;
            var derived = store is null || !store.NeedsDerivedBackfill;
            if (store != null) foreach (var header in store.LoadHeaders())
                entries[header.Path] = new(header.Length, new DateTime(header.ModifiedTicks, DateTimeKind.Utc), header.Entry, derived);
            Volatile.Write(ref loaded, true);
        }
        // Reading a migrated legacy snapshot must stay fast; the parse work runs
        // after the cached list is already available.
        StartBackfillIfNeeded();
    }

    /// <summary>Starts the one-time legacy backfill. Safe to call repeatedly.</summary>
    public Task EnsureBackfillStarted()
    {
        LoadPersistedHeaders();
        StartBackfillIfNeeded();
        lock (backfillGate) return backfillTask ?? Task.CompletedTask;
    }

    private void StartBackfillIfNeeded()
    {
        if (store is not { NeedsDerivedBackfill: true }) return;
        lock (backfillGate)
        {
            if (backfillTask != null) return;
            var targets = entries.Where(pair => NeedsDerivedData(pair.Value.Entry)).ToArray();
            Volatile.Write(ref backfillTotal, targets.Length);
            Volatile.Write(ref backfillProcessed, 0);
            if (targets.Length == 0)
            {
                store.CompleteDerivedBackfill();
                return;
            }
            backfillTask = Task.Run(() => BackfillCoreAsync(targets));
        }
    }

    private async Task BackfillCoreAsync(KeyValuePair<string, Cached>[] targets)
    {
        const int batchSize = 128;
        try
        {
            for (var offset = 0; offset < targets.Length; offset += batchSize)
            {
                var chunk = targets.Skip(offset).Take(batchSize).ToArray();
                var updated = new ConcurrentBag<LibraryCachedHeader>();
                var processed = 0;
                await Parallel.ForEachAsync(chunk, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 4)
                }, (pair, _) =>
                {
                    BackfillOne(pair.Key, pair.Value, updated);
                    Volatile.Write(ref backfillProcessed, offset + Interlocked.Increment(ref processed));
                    return ValueTask.CompletedTask;
                }).ConfigureAwait(false);
                if (!updated.IsEmpty) store!.UpdateHeaders(updated.ToArray());
            }
            store!.CompleteDerivedBackfill();
        }
        catch
        {
            // A failed backfill must never break search or the cached list. The
            // pending flag stays set so the next launch can retry.
        }
    }

    private void BackfillOne(string path, Cached cached, ConcurrentBag<LibraryCachedHeader> updated)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            var parsed = DesktopChartLibraryService.ReadChart(path, Path.GetDirectoryName(path)!);
            var entry = cached.Entry;
            if (entry != null && parsed != null)
            {
                if (parsed.Length is { } length) entry.Length = length;
                if (parsed.Bpm is { } bpm) entry.Bpm = bpm;
            }
            var checkedEntry = new Cached(info.Length, info.LastWriteTimeUtc, entry, true);
            entries[path] = checkedEntry;
            if (entry != null) updated.Add(new LibraryCachedHeader(path, info.Length, info.LastWriteTimeUtc.Ticks, entry));
        }
        catch
        {
            // One unreadable legacy chart must not abort the whole backfill.
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
        {
            Interlocked.Increment(ref CacheHits);
            // An un-backfilled legacy row is parsed on an explicit refresh, but
            // not twice while the background pass already owns it.
            if (cached.DerivedChecked || IsBackfilling) return cached.Entry;
        }
        Interlocked.Increment(ref HeaderReads);
        var chart = DesktopChartLibraryService.ReadChart(file, root);
        entries[key] = new(info.Length, info.LastWriteTimeUtc, chart, true);
        pending[key] = new(key, info.Length, info.LastWriteTimeUtc.Ticks, chart);
        return chart;
    }

    /// <summary>Drops rows under one root that no longer exist on disk. Used by
    /// an explicit refresh so a deleted map cannot linger in the index.</summary>
    public IReadOnlyList<string> ForgetMissingUnderRoot(string root, IEnumerable<string> existingPaths)
    {
        LoadPersistedHeaders();
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        var keep = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        var removed = entries.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !keep.Contains(path))
            .ToArray();
        foreach (var path in removed) entries.TryRemove(path, out _);
        if (removed.Length > 0) store?.ForgetHeaders(removed);
        return removed;
    }

    private static bool NeedsDerivedData(DesktopLibraryChartEntry? entry) =>
        entry != null && (entry.Length is null || entry.Bpm is null);

    private sealed record Cached(long Length, DateTime ModifiedUtc, DesktopLibraryChartEntry? Entry, bool DerivedChecked);
}

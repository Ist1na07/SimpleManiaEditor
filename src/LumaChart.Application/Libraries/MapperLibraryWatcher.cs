using System.Collections.Concurrent;

namespace LumaChart.Application.Libraries;

/// <summary>Coalesces filesystem notifications; never parses or touches a chart on the watcher thread.</summary>
public sealed class MapperLibraryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly ConcurrentDictionary<string, byte> changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string[], bool> changed;
    private readonly Timer timer;
    private int fullRefresh;
    private int disposed;
    public MapperLibraryWatcher(IEnumerable<string> roots, Action<string[], bool> changed)
    {
        this.changed = changed; timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Created += OnChange; watcher.Changed += OnChange; watcher.Deleted += OnChange;
                watcher.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
                watcher.Error += (_, _) => { Interlocked.Exchange(ref fullRefresh, 1); Schedule(); };
                watcher.EnableRaisingEvents = true; watchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Interlocked.Exchange(ref fullRefresh, 1); }
        }
    }
    private void OnChange(object sender, FileSystemEventArgs args) => Queue(args.FullPath);
    private void Queue(string path)
    {
        if (Volatile.Read(ref disposed) != 0 || path.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith(".sme-", StringComparison.OrdinalIgnoreCase))) return;
        if (Path.GetExtension(path).Equals(".osu", StringComparison.OrdinalIgnoreCase)) changes[path] = 0;
        else if (!Path.HasExtension(path)) Interlocked.Exchange(ref fullRefresh, 1);
        else return;
        Schedule();
    }
    private void Schedule() { if (Volatile.Read(ref disposed) == 0) try { timer.Change(750, Timeout.Infinite); } catch (ObjectDisposedException) { } }
    private void Flush()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var batch = changes.Keys.ToArray(); foreach (var path in batch) changes.TryRemove(path, out _);
        changed(batch, Interlocked.Exchange(ref fullRefresh, 0) != 0);
    }
    public void Dispose()
    { if (Interlocked.Exchange(ref disposed, 1) != 0) return; timer.Dispose(); foreach (var watcher in watchers) watcher.Dispose(); }
}

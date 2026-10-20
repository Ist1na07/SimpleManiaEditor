using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool LibraryReconciled { get; private set; }
    private string workspaceClassificationSignature = "";
    private string editablePrefix = "";
    private string[] readOnlyPrefixes = [];
    private readonly Dictionary<string, bool> workspaceFolderSafety = new(StringComparer.OrdinalIgnoreCase);

    private Task? librarySnapshotTask;
    private bool librarySnapshotLoaded;
    private DateTimeOffset? persistedLibraryRefreshTime;
    private const string LibraryRefreshTimeKey = "library_index_refreshed_at";

    /// <summary>True while the persisted header index is being read. Navigation
    /// stays usable: the list fills in as soon as this finishes.</summary>
    public bool IsLibraryCacheLoading { get; private set; }

    /// <summary>A bulk change (watcher overflow or directory storm) was seen.
    /// 0.1.37 only hints; the user decides when to run the expensive scan.</summary>
    public bool LibraryRefreshSuggested { get; private set; }

    /// <summary>When the last explicit 刷新索引 scan finished in this session.</summary>
    public DateTimeOffset? LibraryLastRefreshTime { get; private set; }

    /// <summary>Header text next to the refresh button: where the current list
    /// came from and whether it may be stale.</summary>
    public string LibraryIndexSummary
    {
        get
        {
            if (IsScanning) return "正在扫描目录…";
            if (IsLibraryCacheLoading) return "正在载入本地索引…";
            if (LibraryRefreshSuggested) return "目录有变化 · 点「刷新索引」";
            if (libraryEntries.Count == 0) return "尚无本地索引";
            return FormatLibraryRefreshTime(LibraryLastRefreshTime ?? persistedLibraryRefreshTime) is { } time
                ? "只读缓存 · 上次刷新 " + time
                : "只读缓存";
        }
    }

    public string LibraryRefreshButtonText => IsScanning ? "正在扫描…" : "刷新索引";

    private static string? FormatLibraryRefreshTime(DateTimeOffset? time) =>
        time is { } value ? value.ToLocalTime().ToString("MM-dd HH:mm") : null;

    /// <summary>
    /// Reads the persisted index snapshot only. Library navigation and startup use
    /// this instead of a directory scan; the scan stays behind the explicit
    /// refresh button (see <see cref="RefreshLibraryAsync"/>).
    /// </summary>
    public Task EnsureLibrarySnapshotLoadedAsync()
    {
        if (librarySnapshotLoaded) return Task.CompletedTask;
        return librarySnapshotTask ??= LoadCachedLibrarySnapshotCoreAsync();
    }

    /// <summary>Waits for the persisted index. Kept for the benchmark and specs.</summary>
    public Task LoadCachedLibrarySnapshotAsync() => EnsureLibrarySnapshotLoadedAsync();

    private async Task LoadCachedLibrarySnapshotCoreAsync()
    {
        IsLibraryCacheLoading = true;
        if (libraryEntries.Count == 0) Status = "正在载入本地索引…（可继续浏览，载入完成后自动填充）";
        RaiseStateChanged();
        try
        {
            var scanRoot = ResolvedWorkspaceRoot;
            var snapshot = await Task.Run(() =>
            {
                libraryMetadataCache.LoadPersistedHeaders();
                var allowed = new[] { scanRoot, CreationRoot }.Concat(LinkedLibraryRoots)
                    .Select(p => OsuFileSafetyPolicy.ResolvePhysicalPath(p).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
                var remembered = libraryCatalog.Charts.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var found = libraryMetadataCache.CachedCharts.Where(c => remembered.Contains(c.ChartPath) || allowed.Any(p => c.ChartPath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(c => c.ChartPath, StringComparer.OrdinalIgnoreCase);
                // Older versions parsed remembered personal charts without persisting
                // their headers. Fill these few misses without walking Songs.
                foreach (var file in remembered.Where(p => !found.ContainsKey(p) && File.Exists(p)))
                    try { if (libraryMetadataCache.Read(file, Path.GetDirectoryName(file)!) is { } chart) found[file] = chart; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                libraryMetadataCache.Flush();
                var cached = found.Values.ToArray(); ApplyPersonalSummaries(cached);
                return (Charts: cached, Folders: DesktopChartLibraryService.GroupByFolder(cached, scanRoot, useCachedTimes: true),
                    RefreshedAt: ReadPersistedLibraryRefreshTime());
            });
            if (libraryEntries.Count != 0)
            {
                // A new/save operation won the race and already filled the list.
                librarySnapshotLoaded = true;
                return;
            }
            libraryEntries.ReplaceAll(snapshot.Charts); libraryFolderIndex = snapshot.Folders;
            persistedLibraryRefreshTime = snapshot.RefreshedAt;
            librarySnapshotLoaded = true;
            ApplyLibraryFilterAndSort();
            Status = DescribeCachedLibrary(snapshot.Charts.Length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Navigation must stay usable without a cache: the user can still
            // scan explicitly, and the empty-state actions stay available.
            LibraryWarnings = LibraryWarnings.Append("本地索引载入失败：" + exception.Message).ToArray();
            Status = "本地索引载入失败，可点「刷新索引」扫描目录";
            // Allow a later navigation to retry the read instead of remembering
            // the failure for the rest of the session.
            librarySnapshotTask = null;
        }
        finally
        {
            IsLibraryCacheLoading = false;
            // Saving/creating/deleting charts must still update the list even
            // though startup no longer scans: the watcher is independent of it.
            if (librarySnapshotLoaded && runStartupMaintenance) RestartLibraryWatcher();
            RaiseStateChanged();
        }
    }

    private string DescribeCachedLibrary(int charts)
    {
        var time = FormatLibraryRefreshTime(LibraryLastRefreshTime ?? persistedLibraryRefreshTime);
        return time is null
            ? AvaloniaLocalizationCatalog.Format("已从本地索引载入 {0:N0} 张谱面 · 只读缓存，可能不是最新 · 需要最新目录请点「刷新索引」", charts)
            : AvaloniaLocalizationCatalog.Format("已从本地索引载入 {0:N0} 张谱面 · 只读缓存，可能不是最新（上次刷新 {1}）· 需要最新目录请点「刷新索引」", charts, time);
    }

    private DateTimeOffset? ReadPersistedLibraryRefreshTime()
    {
        try
        {
            return DateTimeOffset.TryParse(MapperLibrary.ReadIndexMetadata(LibraryRefreshTimeKey), out var time) ? time : null;
        }
        catch (Exception) { return null; }
    }

    private void PersistLibraryRefreshTime(DateTimeOffset time)
    {
        persistedLibraryRefreshTime = time;
        try { MapperLibrary.WriteIndexMetadata(LibraryRefreshTimeKey, time.ToString("O")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Bookkeeping must never turn a finished scan into a failure.
            LibraryWarnings = LibraryWarnings.Append("刷新时间未记录：" + exception.Message).Distinct().ToArray();
        }
    }

    /// <summary>
    /// Records that a scan finished: shown in the status line and next to the
    /// refresh button, and persisted so the next launch can say how old the
    /// cached list is.
    /// </summary>
    internal void RecordLibraryScanCompleted(DateTimeOffset time)
    {
        LibraryLastRefreshTime = time;
        LibraryRefreshSuggested = false;
        PersistLibraryRefreshTime(time);
    }

    /// <summary>Bulk watcher notifications (overflow, directory storms) are a hint
    /// only; they never start a full scan by themselves.</summary>
    public void SuggestLibraryRefresh()
    {
        LibraryRefreshSuggested = true;
        Status = "曲库目录有变化，点「刷新索引」更新列表（当前显示本地缓存）";
        RaiseStateChanged();
    }

    public bool IsWorkspaceChart(string path)
    {
        var signature = WorkspaceRoot + "\n" + string.Join('\n', LinkedLibraryRoots);
        if (signature != workspaceClassificationSignature)
        {
            workspaceClassificationSignature = signature;
            editablePrefix = OsuFileSafetyPolicy.ResolvePhysicalPath(CreationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            readOnlyPrefixes = LinkedLibraryRoots.Select(r => OsuFileSafetyPolicy.ResolvePhysicalPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
            workspaceFolderSafety.Clear();
        }
        if (path.StartsWith(editablePrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (readOnlyPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        var folder = Path.GetDirectoryName(path)!;
        if (!workspaceFolderSafety.TryGetValue(folder, out var safe))
            workspaceFolderSafety[folder] = safe = !OsuFileSafetyPolicy.IsProtectedPath(folder);
        return safe;
    }

    public Task SetLibrarySearchAsync(string? query)
    {
        query = query?.Trim() ?? "";
        LibrarySearchQuery = query;
        // Filtering an in-memory 46k-row snapshot takes a few milliseconds. Doing it
        // here (instead of a round trip to SQLite) keeps the search box responsive and
        // lets osu!-style field filters use the typed row fields.
        ApplyLibraryFilterAndSort();
        Status = LibrarySearchStatus();
        RaiseStateChanged();
        return Task.CompletedTask;
    }
    private MapperLibraryWatcher? libraryWatcher;
    private bool libraryWatchingStopped;
    private long libraryWatcherGeneration;
    public void StopLibraryWatching() { libraryWatchingStopped = true; libraryWatcherGeneration++; scanCancellation?.Cancel(); libraryWatcher?.Dispose(); libraryWatcher = null; }
    private void RestartLibraryWatcher()
    {
        if (libraryWatchingStopped) return;
        libraryWatcher?.Dispose();
        var generation = ++libraryWatcherGeneration;
        libraryWatcher = new MapperLibraryWatcher(new[] { ResolvedWorkspaceRoot, CreationRoot }.Concat(LinkedLibraryRoots), (paths, full) =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                if (libraryWatchingStopped || generation != libraryWatcherGeneration) return;
                try
                {
                    // 0.1.37: a bulk notification (overflow, new/removed directory)
                    // no longer rescans every song behind the user's back. The
                    // explicit 刷新索引 button (or a save/new chart, which updates
                    // the index incrementally) is the only full scan trigger.
                    if (full || paths.Length > 256) { SuggestLibraryRefresh(); return; }
                    var removed = paths.Where(path => !File.Exists(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (removed.Count > 0) ForgetLibraryFiles(removed);
                    await UpdateLibraryChartsAsync(paths.Where(File.Exists)); ApplyLibraryFilterAndSort(); RaiseStateChanged();
                }
                catch (Exception exception) { LibraryWarnings = LibraryWarnings.Append("自动索引：" + exception.Message).ToArray(); RaiseStateChanged(); }
            }));
    }

    /// <summary>Drops deleted files from the live index immediately. Used by the
    /// watcher so a deleted map never lingers in the cached list.</summary>
    public void ForgetLibraryFiles(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        MapperLibrary.ForgetHeaders(paths);
        RemoveLibraryCharts(paths);
        ApplyLibraryFilterAndSort();
        RaiseStateChanged();
    }

    /// <summary>
    /// Drops removed charts from the index. Only the folders that actually held
    /// them are regrouped: a full regroup would stat every folder directory on
    /// the UI thread and stall a large library after one file deletion.
    /// </summary>
    private void RemoveLibraryCharts(IReadOnlyCollection<string> removed)
    {
        var remaining = libraryEntries.Where(entry => !removed.Contains(entry.ChartPath)).ToArray();
        libraryEntries.ReplaceAll(remaining);
        var affected = libraryFolderIndex.Where(folder => folder.Charts.Any(chart => removed.Contains(chart.ChartPath)))
            .Select(folder => folder.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (affected.Count == 0) return;
        var regrouped = libraryFolderIndex.Where(folder => affected.Contains(folder.FolderPath))
            .SelectMany(folder => folder.Charts.Where(chart => !removed.Contains(chart.ChartPath))).ToArray();
        libraryFolderIndex = libraryFolderIndex.Where(folder => !affected.Contains(folder.FolderPath))
            .Concat(DesktopChartLibraryService.GroupByFolder(regrouped, ResolvedWorkspaceRoot, useCachedTimes: true)).ToArray();
    }

    private void ApplyPersonalSummaries(IEnumerable<DesktopLibraryChartEntry> entries)
    {
        var annotations = MapperLibrary.AllAnnotations();
        foreach (var entry in entries)
        {
            var annotation = annotations.GetValueOrDefault(entry.ChartPath);
            entry.PersonalAnnotation = annotation;
            entry.PersonalSummary = annotation?.Summary ?? "";
            entry.PersonalSearchText = annotation is null
                ? ""
                : ("\n" + string.Join(' ', annotation.Tags) + "\n" + annotation.Note + "\n" + annotation.State).ToLowerInvariant();
        }
    }
    public void SetPersonalAnnotation(string path, MapperAnnotation annotation)
    {
        MapperLibrary.SetAnnotation(path, annotation);
        ApplyPersonalSummaries(LibraryEntries);
        ApplyLibraryFilterAndSort(); RaiseStateChanged();
    }
    public async Task SetPersonalAnnotationsAsync(IReadOnlyList<KeyValuePair<string, MapperAnnotation>> annotations)
    {
        await Task.Run(() => MapperLibrary.SetAnnotations(annotations));
        ApplyPersonalSummaries(LibraryEntries); ApplyLibraryFilterAndSort(); RaiseStateChanged();
    }
    private long libraryMutationSerial;
    private readonly Dictionary<string, (long Serial, DesktopLibraryChartEntry Entry)> incrementalLibraryEntries = new(StringComparer.OrdinalIgnoreCase);

    // Editing one chart must not enumerate a large linked Songs library.
    private async Task UpdateLibraryChartsAsync(IEnumerable<string> paths)
    {
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try { libraryCatalog.Remember(files); }
        catch (IOException exception) { LibraryWarnings = LibraryWarnings.Append(exception.Message).Distinct().ToArray(); }
        var root = ResolvedWorkspaceRoot;
        var updates = await Task.Run(() => { var entries = files.Where(File.Exists).Select(file => libraryMetadataCache.Read(file, root))
            .Where(entry => entry != null).Cast<DesktopLibraryChartEntry>().ToArray(); libraryMetadataCache.Flush(); return entries; });
        ApplyPersonalSummaries(updates);
        if (updates.Length == 0) return;
        var serial = ++libraryMutationSerial;
        foreach (var entry in updates) incrementalLibraryEntries[entry.ChartPath] = (serial, entry);
        var merged = LibraryEntries.ToDictionary(entry => entry.ChartPath, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in updates) merged[entry.ChartPath] = entry;
        var entries = merged.Values.ToArray();
        libraryFolderIndex = PatchLibraryFolders(libraryFolderIndex, entries, updates, root);
        libraryEntries.ReplaceAll(entries);
        ApplyLibraryFilterAndSort();
        RaiseStateChanged();
    }

    private void MergeConcurrentLibraryUpdates(ref IReadOnlyList<DesktopLibraryChartEntry> entries,
        ref IReadOnlyList<DesktopLibraryFolderEntry> folders, long scanSerial, string root)
    {
        var updates = incrementalLibraryEntries.Values.Where(update => update.Serial > scanSerial).Select(update => update.Entry).ToArray();
        if (updates.Length == 0) return;
        var merged = entries.ToDictionary(entry => entry.ChartPath, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in updates) merged[entry.ChartPath] = entry;
        entries = merged.Values.ToArray();
        folders = PatchLibraryFolders(folders, entries, updates, root);
    }

    private static IReadOnlyList<DesktopLibraryFolderEntry> PatchLibraryFolders(IReadOnlyList<DesktopLibraryFolderEntry> old,
        IEnumerable<DesktopLibraryChartEntry> all, IEnumerable<DesktopLibraryChartEntry> updates, string root)
    {
        var paths = updates.Select(entry => entry.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = DesktopChartLibraryService.GroupByFolder(all.Where(entry => paths.Contains(entry.FolderPath)), root);
        var changedFolders = changed.Select(folder => folder.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return old.Where(folder => !changedFolders.Contains(folder.FolderPath)).Concat(changed).ToArray();
    }
}
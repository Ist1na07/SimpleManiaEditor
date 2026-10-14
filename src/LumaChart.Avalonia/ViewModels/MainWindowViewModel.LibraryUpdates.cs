using LumaChart.Application.Libraries;

namespace LumaChart.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel
{
    private long librarySearchGeneration;
    public async Task SetLibrarySearchAsync(string? query)
    {
        query = query?.Trim() ?? "";
        var generation = ++librarySearchGeneration;
        LibrarySearchQuery = query;
        var matching = query.Length == 0 ? null : await Task.Run(() => MapperLibrary.Search(query));
        if (generation != librarySearchGeneration || LibrarySearchQuery != query) return;
        ApplyLibraryFilterAndSort(matching, searchAlreadyResolved: true);
        RaiseStateChanged();
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
                    if (full || paths.Length > 256) { await RefreshLibraryAsync(); return; }
                    var removed = paths.Where(path => !File.Exists(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (removed.Count > 0)
                    {
                        MapperLibrary.ForgetHeaders(removed);
                        var remaining = LibraryEntries.Where(entry => !removed.Contains(entry.ChartPath)).ToArray();
                        libraryEntries.ReplaceAll(remaining);
                        libraryFolderIndex = DesktopChartLibraryService.GroupByFolder(remaining, ResolvedWorkspaceRoot);
                    }
                    await UpdateLibraryChartsAsync(paths.Where(File.Exists)); ApplyLibraryFilterAndSort(); RaiseStateChanged();
                }
                catch (Exception exception) { LibraryWarnings = LibraryWarnings.Append("自动索引：" + exception.Message).ToArray(); RaiseStateChanged(); }
            }));
    }
    private void ApplyPersonalSummaries(IEnumerable<DesktopLibraryChartEntry> entries)
    {
        var annotations = MapperLibrary.AllAnnotations();
        foreach (var entry in entries) entry.PersonalSummary = annotations.GetValueOrDefault(entry.ChartPath)?.Summary ?? "";
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

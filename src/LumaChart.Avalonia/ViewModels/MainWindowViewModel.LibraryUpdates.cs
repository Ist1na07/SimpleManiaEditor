using LumaChart.Application.Libraries;

namespace LumaChart.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel
{
    private long libraryMutationSerial;
    private readonly Dictionary<string, (long Serial, DesktopLibraryChartEntry Entry)> incrementalLibraryEntries = new(StringComparer.OrdinalIgnoreCase);

    // Editing one chart must not enumerate a large linked Songs library.
    private async Task UpdateLibraryChartsAsync(IEnumerable<string> paths)
    {
        var files = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try { libraryCatalog.Remember(files); }
        catch (IOException exception) { LibraryWarnings = LibraryWarnings.Append(exception.Message).Distinct().ToArray(); }
        var root = ResolvedWorkspaceRoot;
        var updates = await Task.Run(() => files.Where(File.Exists).Select(file => libraryMetadataCache.Read(file, root))
            .Where(entry => entry != null).Cast<DesktopLibraryChartEntry>().ToArray());
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

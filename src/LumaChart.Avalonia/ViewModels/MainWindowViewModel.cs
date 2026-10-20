using System.Collections.ObjectModel;
using Avalonia.Threading;
using LumaChart.Application.Exporting;
using LumaChart.Application.Libraries;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Avalonia.Verification;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly AvaloniaWorkspaceController workspace;
    private readonly bool runStartupMaintenance;
    // Serializes save + read-back verification. Without it a second Ctrl+S
    // could replace the source while the first save's verification still had
    // it open, and that replacement failed.
    private readonly SemaphoreSlim saveVerificationGate = new(1, 1);
    private CancellationTokenSource? scanCancellation;
    private readonly EditorLibraryCatalog libraryCatalog;
    private readonly DesktopLibraryMetadataCache libraryMetadataCache;
    public MapperLibraryStore MapperLibrary { get; }
    public MapperVersionStore Versions { get; }
    public MapsetTransferService Transfers { get; }
    private MixedSelectionClipboard? mixedSelectionClipboard;
    private readonly BatchObservableCollection<DesktopLibraryChartEntry> libraryEntries = [];
    private readonly BatchObservableCollection<DesktopLibraryFolderView> libraryFolders = [];

    public MainWindowViewModel()
        : this(CreateDefaultWorkspaceController())
    {
        // Only the real client's default profile reclaims shared app storage;
        // spec-constructed view models use isolated roots.
        runStartupMaintenance = true;
    }

    public MainWindowViewModel(AvaloniaWorkspaceController workspace)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        LibrarySort = AvaloniaAppPaths.LoadLibrarySort();
        libraryCatalog = new EditorLibraryCatalog(AvaloniaAppPaths.LocalDataRoot);
        MapperLibrary = new MapperLibraryStore(AvaloniaAppPaths.LocalDataRoot);
        libraryMetadataCache = new DesktopLibraryMetadataCache(MapperLibrary);
        Versions = new MapperVersionStore(AvaloniaAppPaths.LocalDataRoot, MapperLibrary);
        Transfers = new MapsetTransferService(AvaloniaAppPaths.LocalDataRoot, MapperLibrary);
        workspace.Changed += Workspace_Changed;
    }

    /// <summary>
    /// Builds the client's workspace. A saved library root that has become
    /// unavailable falls back to the default root for this session only (see
    /// <see cref="WorkspaceRootFallback"/>) instead of failing every launch.
    /// Mobile hosts own a fixed library location and get no fallback.
    /// </summary>
    private static AvaloniaWorkspaceController CreateDefaultWorkspaceController()
    {
        var mobileLibrary = AvaloniaAppPaths.Locations is LumaChart.Storage.IAppLibraryStorageLocation;
        var controller = new AvaloniaWorkspaceController(
            AvaloniaAppPaths.ResolveInitialWorkspaceRoot(),
            AvaloniaAppPaths.PersistentEditingRoot,
            defaultCreator: "guest",
            fallbackWorkspaceRoot: mobileLibrary ? null : AvaloniaAppPaths.ResolveDefaultWorkspaceRoot);
        if (controller.WorkspaceRootFallback is { } fallback)
            AvaloniaAppPaths.ReportWorkspaceRootFallback(
                fallback.RequestedRoot,
                fallback.FallbackRoot,
                fallback.Reason);
        return controller;
    }

    private IReadOnlyList<DesktopLibraryFolderEntry> libraryFolderIndex = [];

    public ObservableCollection<DesktopLibraryChartEntry> LibraryEntries => libraryEntries;
    public ObservableCollection<DesktopLibraryFolderView> LibraryFolders => libraryFolders;
    public int IndexedLibraryFolderCount => libraryFolderIndex.Count;

    /// <summary>An immutable snapshot of the open tabs; safe to enumerate.</summary>
    public IReadOnlyList<DesktopChartTab> Tabs => workspace.Tabs;
    public DesktopChartTab? ActiveTab => workspace.ActiveTab;
    public string WorkspaceRoot => workspace.WorkspaceRoot;
    public string CreationRoot => workspace.CreationRoot;
    public IReadOnlyList<string> LinkedLibraryRoots => libraryCatalog.Roots;
    public IReadOnlyList<string> LibraryWarnings { get; private set; } = [];
    public string? LibrarySourceFilter { get; private set; }

    public async Task AddLibraryRootAsync(string root)
    {
        libraryCatalog.AddRoot(root);
        await RefreshLibraryAsync();
    }

    public async Task RemoveLibraryRootAsync(string root)
    {
        libraryCatalog.RemoveRoot(root);
        if (LibrarySourceFilter == root) LibrarySourceFilter = null;
        await RefreshLibraryAsync();
    }

    public void SetLibrarySourceFilter(string? root)
    {
        LibrarySourceFilter = root;
        ApplyLibraryFilterAndSort();
        RaiseStateChanged();
    }
    public string ResolvedWorkspaceRoot => workspace.ResolvedWorkspaceRoot;

    /// <summary>
    /// Non-null when the saved library root was unavailable at startup and
    /// this session uses the default root. The saved root is kept in settings,
    /// so it returns on a later launch once the drive or share is back.
    /// </summary>
    public WorkspaceRootFallbackNotice? WorkspaceRootFallback =>
        AvaloniaAppPaths.WorkspaceRootFallback is { } notice &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(notice.FallbackRoot),
            Path.TrimEndingDirectorySeparator(workspace.WorkspaceRoot),
            StringComparison.OrdinalIgnoreCase)
            ? notice
            : null;
    public string LibrarySearchQuery { get; private set; } = string.Empty;
    public DesktopLibrarySort LibrarySort { get; private set; } = DesktopLibrarySort.AddedDescending;
    public bool IsScanning { get; private set; }
    public Exception? LastLibraryScanError { get; private set; }
    public string Status { get; private set; } = "正在准备 LumaChart Studio 工作台…";
    public SaveRoundTripResult? LastSaveRoundTrip { get; private set; }
    public IReadOnlyList<string> RecoveryWarnings => workspace.RecoveryWarnings;
    public bool HasMixedSelectionClipboard => mixedSelectionClipboard is { IsEmpty: false };
    public int MixedClipboardNoteCount => mixedSelectionClipboard?.NoteCount ?? 0;
    public int MixedClipboardEffectCount => mixedSelectionClipboard?.EffectCount ?? 0;

    public event EventHandler? StateChanged;

    public async Task InitializeAsync(
        string? startupChartPath = null,
        bool restorePreviousSession = true,
        bool scanLibrary = true)
    {
        try
        {
            // 0.1.37: startup reads the persisted header index only. It never walks
            // Songs on its own, so the first visit to the library shows the cached
            // list immediately; the scan stays behind the explicit refresh action.
            var cachedLibrary = EnsureLibrarySnapshotLoadedAsync();
            if (restorePreviousSession)
                await workspace.InitializeAsync();
            else
                await workspace.InitializeForExplicitStartupAsync();
            if (!string.IsNullOrWhiteSpace(startupChartPath) && File.Exists(startupChartPath))
            {
                await workspace.OpenAsync(startupChartPath);
                AvaloniaAppPaths.SaveWorkspaceRoot(workspace.WorkspaceRoot);
            }
            if (scanLibrary) await cachedLibrary;
            if (runStartupMaintenance) AvaloniaAppPaths.RememberDefaultWorkspaceRoot(workspace.WorkspaceRoot);
            var startupStatus = !restorePreviousSession
                ? "已按文件启动；上次编辑缓存将在下次普通启动时恢复"
                : workspace.RecoveryWarnings.Count == 0
                    ? "LumaChart Studio 客户端已启动"
                    : AvaloniaLocalizationCatalog.Format("已恢复工作区；{0} 条恢复提示", workspace.RecoveryWarnings.Count);
            // While the snapshot is still loading its own message stays visible;
            // once it is in, say where the list came from instead of a generic line.
            if (!IsLibraryCacheLoading)
                Status = LibraryEntries.Count > 0 ? DescribeCachedLibrary(LibraryEntries.Count) : startupStatus;
            RaiseStateChanged();
            if (runStartupMaintenance) StartStorageMaintenance();
            if (scanLibrary) await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            Status = AvaloniaLocalizationCatalog.Format("启动失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Best-effort, background reclamation of app-owned storage that otherwise
    /// grows without bound: scratch trees of crashed client processes and old
    /// materialized room documents. It runs once the workspace is initialized,
    /// so the sources of open and suspended tabs are known and kept, and it is
    /// skipped entirely while the manifest's entries are unknown.
    /// </summary>
    private void StartStorageMaintenance()
    {
        if (!OperatingSystem.IsWindows()) return;
        var inUseSources = workspace.HasUnverifiedManifest
            ? null
            : workspace.GetReferencedSourcePaths();
        _ = Task.Run(() =>
        {
            try
            {
                AvaloniaAppPaths.CleanupOrphanedSessionScratchRoots();
                if (inUseSources != null)
                    WindowsOnlineRoomResourceService.PruneRoomDocuments(
                        AvaloniaAppPaths.OnlineRoomDocumentRoot,
                        inUseSources,
                        DateTimeOffset.UtcNow);
            }
            catch (Exception exception)
            {
                WindowsDiagnosticLog.Current?.WriteException(
                    "startup-storage-maintenance-failed",
                    exception,
                    fatal: false);
            }
        });
    }

    public async Task RefreshLibraryAsync()
    {
        scanCancellation?.Cancel();
        scanCancellation?.Dispose();
        scanCancellation = new CancellationTokenSource();
        var cancellationToken = scanCancellation.Token;
        var scanSerial = libraryMutationSerial;
        var scanRoot = workspace.ResolvedWorkspaceRoot;
        var creationRoot = workspace.CreationRoot;
        try { await LoadCachedLibrarySnapshotAsync(); }
        catch (OperationCanceledException) { return; }
        IsScanning = true;
        LastLibraryScanError = null;
        // An explicit scan clears the "directory changed" hint it satisfies.
        LibraryRefreshSuggested = false;
        Status = AvaloniaLocalizationCatalog.Format("正在扫描曲库目录：{0}（扫描期间列表仍显示本地索引）", WorkspaceRoot);
        RaiseStateChanged();
        // Progress reports are queued asynchronously; once the scan has a result
        // they must never overwrite the final status line.
        var scanProgressClosed = false;
        try
        {
            var warnings = new List<string>();
            try
            {
                var pending = Transfers.History().Count(record => record.State is not ("Complete" or "RolledBack"));
                if (pending > 0) warnings.Add($"有 {pending} 个未完成迁移，请打开「迁移记录」恢复");
            }
            catch (IOException exception) { warnings.Add("迁移记录读取失败：" + exception.Message); }
            try { libraryCatalog.Remember(Tabs.Select(tab => tab.SourcePath)); }
            catch (IOException exception) { warnings.Add(exception.Message); }
            var linkedRoots = libraryCatalog.Roots;
            var rememberedCharts = libraryCatalog.Charts;
            // Throttled progress: scanning Songs is the only operation that can
            // take seconds, so the status line counts what it has indexed while
            // the cached list stays usable underneath.
            var currentScanRoot = scanRoot;
            var lastProgressTick = 0L;
            var scanProgress = new Progress<DesktopLibraryScanProgress>(value =>
            {
                var tick = Environment.TickCount64;
                if (value.Processed < value.Total && tick - Interlocked.Read(ref lastProgressTick) < 200) return;
                Interlocked.Exchange(ref lastProgressTick, tick);
                var text = AvaloniaLocalizationCatalog.Format("正在扫描曲库：{0} {1:N0} / {2:N0}",
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(currentScanRoot)), value.Processed, value.Total);
                Dispatcher.UIThread.Post(() =>
                {
                    // The flag and the status only change on the UI thread, so a
                    // queued progress report can never overwrite a final result.
                    if (scanProgressClosed || scanCancellation?.Token != cancellationToken) return;
                    Status = text;
                    RaiseStateChanged();
                });
            });
            var entries = await Task.Run(async () =>
            {
                var merged = new Dictionary<string, DesktopLibraryChartEntry>(StringComparer.OrdinalIgnoreCase);
                var roots = new[] { scanRoot, creationRoot }
                    .Concat(linkedRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var verifiedRoots = new List<string>();
                foreach (var root in roots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    currentScanRoot = root;
                    try
                    {
                        foreach (var chart in await DesktopChartLibraryService.ScanAsync(root, scanProgress, cancellationToken, libraryMetadataCache))
                            merged[chart.ChartPath] = chart;
                        verifiedRoots.Add(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    { warnings.Add(root + "：" + exception.Message); }
                }
                foreach (var file in rememberedCharts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (merged.ContainsKey(file) || !File.Exists(file)) continue;
                    try
                    {
                        var chart = libraryMetadataCache.Read(file, Path.GetDirectoryName(file)!);
                        if (chart != null) merged[file] = chart;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                }
                libraryMetadataCache.Flush();
                var stale = libraryMetadataCache.CachedCharts.Where(chart => !merged.ContainsKey(chart.ChartPath) &&
                    verifiedRoots.Any(prefix => chart.ChartPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Select(chart => chart.ChartPath).ToArray();
                MapperLibrary.ForgetHeaders(stale);
                return (IReadOnlyList<DesktopLibraryChartEntry>)merged.Values.ToArray();
            }, cancellationToken);
            scanProgressClosed = true;
            LibraryWarnings = warnings;
            var workspaceRoot = scanRoot;
            var indexedFolders = await Task.Run(
                () => DesktopChartLibraryService.GroupByFolder(
                    entries,
                    workspaceRoot),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            MergeConcurrentLibraryUpdates(ref entries, ref indexedFolders, scanSerial, workspaceRoot);
            ApplyPersonalSummaries(entries);
            libraryEntries.ReplaceAll(entries);
            libraryFolderIndex = indexedFolders;
            ApplyLibraryFilterAndSort();
            LibraryReconciled = true;
            var refreshedAt = DateTimeOffset.Now;
            RecordLibraryScanCompleted(refreshedAt);
            Status = entries.Count == 0
                ? "曲库为空，可新建谱面、打开谱面或选择工作目录"
                : AvaloniaLocalizationCatalog.Format("曲库已刷新 · {0:N0} 个文件夹 · {1:N0} 张谱面 · {2:MM-dd HH:mm}", libraryFolderIndex.Count, entries.Count, refreshedAt.ToLocalTime());
        }
        catch (OperationCanceledException)
        {
            scanProgressClosed = true;
            if (scanCancellation?.Token == cancellationToken) Status = "曲库扫描已取消";
        }
        catch (Exception exception)
        {
            scanProgressClosed = true;
            if (scanCancellation?.Token == cancellationToken)
            {
                LastLibraryScanError = exception;
                Status = AvaloniaLocalizationCatalog.Format("曲库扫描失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
            }
        }
        finally
        {
            if (scanCancellation?.Token == cancellationToken)
            {
                IsScanning = false;
                if (runStartupMaintenance) RestartLibraryWatcher();
                RaiseStateChanged();
            }
        }
    }

    public Task<OsuNoteTimingImportMode?> TryGetSynchronizedSuspendedOsuTimingModeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        workspace.TryGetSynchronizedSuspendedOsuTimingModeAsync(
            sourcePath,
            cancellationToken);

    public void CancelLibraryScan() => scanCancellation?.Cancel();

    public async Task SwitchWorkspaceAsync(string root)
    {
        OsuFileSafetyPolicy.RequireWritable(root);
        scanCancellation?.Cancel();
        libraryCatalog.Remember(Tabs.Select(tab => tab.SourcePath));
        await workspace.SwitchRootAsync(root);
        // An explicit choice ends a startup fallback; it is now the saved root.
        AvaloniaAppPaths.ClearWorkspaceRootFallback();
        AvaloniaAppPaths.SaveWorkspacePreferences(workspace.WorkspaceRoot, LibrarySort);
        Status = AvaloniaLocalizationCatalog.Format("工作目录已切换：{0}", workspace.WorkspaceRoot);
        RaiseStateChanged();
        await RefreshLibraryAsync();
    }

    public async Task OpenReadOnlyAsync(string path)
    {
        var tab = await workspace.OpenReadOnlyAsync(path);
        Status = "只读查看 · " + tab.DisplayTitle + " · 可复制排列；点上方「编辑」复制到工作区后可编辑";
        RaiseStateChanged();
    }

    public async Task OpenAsync(string path)
    {
        await OpenAsync(path, osuTimingImportMode: null);
    }

    public async Task OpenAsync(string path, OsuNoteTimingImportMode? osuTimingImportMode)
    {
        var alreadyOpen = Tabs.FirstOrDefault(tab => string.Equals(tab.SourcePath, path, StringComparison.OrdinalIgnoreCase));
        if (alreadyOpen != null) { Activate(alreadyOpen.Id); return; }
        var knownWorkspace = LibraryEntries.Any(c => string.Equals(c.ChartPath, path, StringComparison.OrdinalIgnoreCase)) && IsWorkspaceChart(path);
        var tab = knownWorkspace
            ? await workspace.OpenManagedChartAsync(path, osuTimingImportMode ?? OsuNoteTimingImportMode.PreserveMilliseconds)
            : osuTimingImportMode.HasValue ? await workspace.OpenAsync(path, osuTimingImportMode.Value) : await workspace.OpenAsync(path);
        Status = AvaloniaLocalizationCatalog.Format("已打开 {0} · {1}", tab.DisplayTitle, tab.FormatBadge);
        RaiseStateChanged();
        if (!string.Equals(path, tab.SourcePath, StringComparison.OrdinalIgnoreCase)) MapperLibrary.LinkCopies(path, tab.SourcePath);
        await UpdateLibraryChartsAsync([tab.SourcePath]);
        try { if (Versions.List(tab.SourcePath).Count == 0) await Versions.SaveAsync(tab.SourcePath, tab.Session.CaptureCommittedDocument(), "开始编辑", includeResources: true); }
        catch (Exception exception) { LibraryWarnings = LibraryWarnings.Append("初始版本未记录：" + exception.Message).ToArray(); }
    }

    public async Task<int> OpenProjectDirectoryAsync(string path)
    {
        var opened = await workspace.OpenProjectDirectoryAsync(path);
        Status = AvaloniaLocalizationCatalog.Format("已从项目目录打开 {0:N0} 张谱面", opened.Count);
        RaiseStateChanged();
        await UpdateLibraryChartsAsync(workspace.Tabs.Select(tab => tab.SourcePath));
        return opened.Count;
    }

    public Task<IReadOnlyList<string>> InspectProjectDirectoryAsync(string path) =>
        workspace.InspectProjectDirectoryAsync(path);

    public async Task<AvaloniaArchiveImportPlan> ImportArchiveAsync(string path)
    {
        var imported = await workspace.ImportArchiveAsync(path);
        Status = AvaloniaLocalizationCatalog.Format("已导入谱包 · {0:N0} 张可打开谱面 / {1:N0} 张其他模式 / {2:N0} 张损坏谱面", imported.SupportedChartPaths.Count, imported.UnsupportedChartPaths.Count, imported.BrokenChartPaths.Count);
        RaiseStateChanged();
        await UpdateLibraryChartsAsync(imported.SupportedChartPaths);
        return imported;
    }

    public async Task<AvaloniaArchiveOpenResult> OpenImportedChartsAsync(
        AvaloniaArchiveImportPlan imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var result = await workspace.OpenImportedChartsAsync(imported.SupportedChartPaths);
        Status = result.FailedPaths.Count == 0
            ? AvaloniaLocalizationCatalog.Format("已打开谱包中的 {0:N0} 张谱面", result.OpenedTabs.Count)
            : AvaloniaLocalizationCatalog.Format("已打开 {0:N0} 张；{1:N0} 张打开失败", result.OpenedTabs.Count, result.FailedPaths.Count);
        RaiseStateChanged();
        return result;
    }

    public async Task<MalodyFullPackageExportResult> ExportMalodyFullPackageAsync(
        Guid tabId,
        string destinationArchivePath,
        IProgress<MalodyFullPackageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await workspace.ExportMalodyFullPackageAsync(
            tabId,
            destinationArchivePath,
            progress,
            cancellationToken);
        Status = AvaloniaLocalizationCatalog.Format("已导出 Malody 整谱压缩包 · {0:N0} 项 · {1:N0} 字节", result.EntryCount, result.ArchiveBytes);
        RaiseStateChanged();
        return result;
    }

    public async Task<OsuFullPackageExportResult> ExportOsuFullPackageAsync(
        Guid tabId,
        string destinationArchivePath,
        double overallDifficulty,
        double hpDrainRate,
        IProgress<OsuFullPackageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await workspace.ExportOsuFullPackageAsync(
            tabId,
            destinationArchivePath,
            overallDifficulty,
            hpDrainRate,
            progress,
            cancellationToken);
        Status = AvaloniaLocalizationCatalog.Format("已导出 osu!mania 整谱压缩包 · {0:N0} 项 · {1:N0} 字节", result.EntryCount, result.ArchiveBytes);
        RaiseStateChanged();
        return result;
    }

    public async Task<ChartExportResult> ExportChartAsync(
        Guid tabId,
        string destinationPath,
        ChartExportOptions options,
        IProgress<ChartExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await workspace.ExportChartAsync(
            tabId,
            destinationPath,
            options,
            progress,
            cancellationToken);
        Status = options.Mode == ChartExportMode.SingleChart
            ? AvaloniaLocalizationCatalog.Format("已导出单谱文件 · {0:N0} 字节", result.OutputBytes)
            : AvaloniaLocalizationCatalog.Format("已导出谱包 · {0:N0} 项 · {1:N0} 字节", result.EntryCount, result.OutputBytes);
        RaiseStateChanged();
        return result;
    }

    public async Task CreateAsync(string? title = null, string? creator = null)
    {
        var documentTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        var suggestedName = string.IsNullOrWhiteSpace(title) ? "New Chart" : documentTitle;
        var tab = await workspace.CreateAsync(documentTitle, creator, suggestedName);
        Status = AvaloniaLocalizationCatalog.Format("已新建 {0}", tab.DisplayTitle);
        RaiseStateChanged();
        await UpdateLibraryChartsAsync([tab.SourcePath]);
    }

    public async Task<DesktopNamedAudioChartCreation> CreateNamedAudioChartAsync(
        string? requestedName,
        string? creator = null)
    {
        var result = await workspace.CreateNamedAudioChartAsync(requestedName, creator);
        Status = AvaloniaLocalizationCatalog.Format("已新建 {0}", result.Name);
        RaiseStateChanged();
        await UpdateLibraryChartsAsync([result.Tab.SourcePath]);
        return result;
    }

    public void Activate(Guid tabId)
    {
        workspace.Activate(tabId);
        Status = AvaloniaLocalizationCatalog.Format("已切换到 {0}", workspace.ActiveTab?.DisplayTitle);
        RaiseStateChanged();
    }

    public void UpdateCurrentBeat(double beat)
    {
        // The active tab can be closed between the read and the update; that
        // is not an error for a view-state refresh.
        var tab = ActiveTab;
        if (tab != null) workspace.UpdateViewState(tab.Id, beat);
    }

    public void ReplaceNavigationMarkers(
        Guid tabId,
        IEnumerable<DesktopNavigationMarker> markers) =>
        workspace.ReplaceNavigationMarkers(tabId, markers);

    public Task<SaveRoundTripResult?> SaveAndVerifyAsync() =>
        SaveAndVerifyAsync(ActiveTab?.Id);

    public async Task<SaveRoundTripResult?> SaveAndVerifyAsync(Guid? tabId)
    {
        await saveVerificationGate.WaitAsync();
        try
        {
            var tab = tabId.HasValue
                ? Tabs.FirstOrDefault(candidate => candidate.Id == tabId.Value)
                : ActiveTab;
            if (tab == null) return null;
            if (Versions.List(tab.SourcePath).Count == 0 && File.Exists(tab.SourcePath))
            {
                var original = await Task.Run(() => ChartDocumentImporter.ImportText(File.ReadAllText(tab.SourcePath), tab.SourcePath, "guest"));
                await Versions.SaveAsync(tab.SourcePath, original, "首次保存前", includeResources: true);
            }
            var versionSnapshot = tab.Session.CaptureCommittedDocument();
            var before = ChartStructuralFingerprint.Summarize(versionSnapshot);
            var save = await workspace.SaveAsync(tab.Id);
            return await VerifySavedDocumentAsync(tab, before, save, saveAs: false, versionSnapshot);
        }
        finally
        {
            saveVerificationGate.Release();
        }
    }

    public Task<SaveRoundTripResult?> SaveAsAndVerifyAsync(string destination) =>
        SaveAsAndVerifyAsync(ActiveTab?.Id, destination);

    public async Task<SaveRoundTripResult?> SaveAsAndVerifyAsync(Guid? tabId, string destination)
    {
        await saveVerificationGate.WaitAsync();
        try
        {
            var tab = tabId.HasValue
                ? Tabs.FirstOrDefault(candidate => candidate.Id == tabId.Value)
                : ActiveTab;
            if (tab == null) return null;
            var versionSnapshot = tab.Session.CaptureCommittedDocument();
            var before = ChartStructuralFingerprint.Summarize(versionSnapshot);
            var save = await workspace.SaveAsAsync(tab.Id, destination);
            return await VerifySavedDocumentAsync(tab, before, save, saveAs: true, versionSnapshot);
        }
        finally
        {
            saveVerificationGate.Release();
        }
    }

    private async Task<SaveRoundTripResult?> VerifySavedDocumentAsync(
        DesktopChartTab tab,
        ChartStructureSummary before,
        DesktopChartSaveResult save,
        bool saveAs,
        ChartDocument versionSnapshot)
    {
        if (!save.Saved)
        {
            Status = save.Status switch
            {
                DesktopChartSaveStatus.ValidationFailed => AvaloniaLocalizationCatalog.Format("{0}被谱面检查阻止：{1} 项", (saveAs ? AvaloniaLocalizationCatalog.Text("另存") : AvaloniaLocalizationCatalog.Text("保存")), save.Issues.Count),
                _ => saveAs ? "另存失败" : "保存失败"
            };
            RaiseStateChanged();
            return null;
        }

        // Share write and delete so the read-back can never make a concurrent
        // atomic replace of the source (File.Move over it) fail.
        await using var stream = new FileStream(
            tab.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var reloaded = await ChartDocumentImporter.ImportAsync(
            stream,
            tab.SourcePath,
            workspace.DefaultCreator,
            tab.OsuTimingImportMode);
        var after = ChartStructuralFingerprint.Summarize(reloaded);
        await UpdateLibraryChartsAsync([tab.SourcePath]);
        try { await Versions.SaveAsync(tab.SourcePath, versionSnapshot, "保存", includeResources: Versions.List(tab.SourcePath).Count == 0); }
        catch (Exception exception) { LibraryWarnings = LibraryWarnings.Append("版本记录失败：" + exception.Message).ToArray(); }
        LastSaveRoundTrip = new SaveRoundTripResult(
            tab.SourcePath,
            before,
            after,
            string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal));
        Status = LastSaveRoundTrip.StructurallyEqual
            ? AvaloniaLocalizationCatalog.Format("已{0}并结构化复核通过 · {1:N0} Note / {2:N0} Timing", (saveAs ? AvaloniaLocalizationCatalog.Text("另存") : AvaloniaLocalizationCatalog.Text("保存")), after.NoteCount, after.TimingCount)
            : AvaloniaLocalizationCatalog.Format("文件已{0}，但结构化复核发现差异", (saveAs ? AvaloniaLocalizationCatalog.Text("另存") : AvaloniaLocalizationCatalog.Text("保存")));
        RaiseStateChanged();
        return LastSaveRoundTrip;
    }

    public bool Undo()
    {
        var changed = ActiveTab?.Session.Undo() == true;
        Status = changed ? "已撤销" : "没有可撤销操作";
        RaiseStateChanged();
        return changed;
    }

    public bool Redo()
    {
        var changed = ActiveTab?.Session.Redo() == true;
        Status = changed ? "已重做" : "没有可重做操作";
        RaiseStateChanged();
        return changed;
    }

    public Guid AddTiming(CompleteTimingEdit edit)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        var id = CompleteTimingEditingService.Add(tab.Session, edit);
        Status = id == Guid.Empty ? "Timing / Effect 未写入（权限或锁区拒绝）" : "已新增 Timing / Effect";
        RaiseStateChanged();
        return id;
    }

    public bool UpdateTiming(Guid timingId, CompleteTimingEdit edit)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        var changed = CompleteTimingEditingService.Update(tab.Session, timingId, edit);
        Status = changed ? "已应用 Timing / Effect" : "Timing / Effect 未更改";
        RaiseStateChanged();
        return changed;
    }

    public bool DeleteTiming(Guid timingId)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        var changed = CompleteTimingEditingService.Delete(tab.Session, timingId);
        Status = changed ? "已删除 Timing / Effect" : "Timing / Effect 未删除";
        RaiseStateChanged();
        return changed;
    }

    public TimingBatchDeleteResult DeleteTimings(IEnumerable<Guid> timingIds)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        var result = TimingBatchDeleteService.DeleteMany(tab.Session, timingIds);
        Status = result.Applied
            ? AvaloniaLocalizationCatalog.Format("已删除 {0:N0} 个 Timing / Effect 条目", result.DeletedCount)
            : "没有可删除的 Timing / Effect 条目";
        RaiseStateChanged();
        return result;
    }

    public MixedSelectionClipboard CopyMixedSelection(IEnumerable<Guid> timingIds)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        mixedSelectionClipboard = MixedSelectionEditingService.Copy(
            tab.Session,
            tab.Session.Selection,
            timingIds,
            tab.HiddenGroups);
        Status = mixedSelectionClipboard.IsEmpty
            ? "当前选择中没有可复制的 Note / Effect"
            : AvaloniaLocalizationCatalog.Format("已复制 {0:N0} 个 Note、{1:N0} 个 Effect", mixedSelectionClipboard.NoteCount, mixedSelectionClipboard.EffectCount);
        RaiseStateChanged();
        return mixedSelectionClipboard;
    }

    public MixedSelectionClipboard? EditorClipboard => mixedSelectionClipboard;

    public void SetEditorClipboard(MixedSelectionClipboard clipboard)
    {
        mixedSelectionClipboard = clipboard;
        RaiseStateChanged();
    }

    public MixedSelectionClipboard CopyTimingSelection(IEnumerable<Guid> timingIds)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        mixedSelectionClipboard = MixedSelectionEditingService.Copy(
            tab.Session,
            [],
            timingIds);
        Status = mixedSelectionClipboard.IsEmpty
            ? "当前 Timing 选择中没有可复制的 Effect"
            : AvaloniaLocalizationCatalog.Format("已复制 {0:N0} 个 Effect", mixedSelectionClipboard.EffectCount);
        RaiseStateChanged();
        return mixedSelectionClipboard;
    }

    public MixedSelectionPasteResult PasteMixedSelection(BeatPosition redLineBeat)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        if (mixedSelectionClipboard is not { IsEmpty: false } clipboard)
        {
            Status = "混合剪贴板为空";
            RaiseStateChanged();
            return new MixedSelectionPasteResult([], []);
        }

        var result = MixedSelectionEditingService.Paste(
            tab.Session,
            clipboard,
            redLineBeat,
            tab.HiddenGroups);
        Status = result.Applied
            ? AvaloniaLocalizationCatalog.Format("已在红线位置粘贴 {0:N0} 个 Note、{1:N0} 个 Effect", result.NoteCount, result.EffectCount)
            : "混合剪贴板没有可粘贴的内容";
        RaiseStateChanged();
        return result;
    }

    public MixedSelectionPasteResult PasteTimingSelection(BeatPosition redLineBeat)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        if (mixedSelectionClipboard is not { IsEmpty: false } clipboard || clipboard.EffectCount == 0)
        {
            Status = "Effect 剪贴板为空";
            RaiseStateChanged();
            return new MixedSelectionPasteResult([], []);
        }

        var preservedNoteSelection = tab.Session.Selection.ToArray();
        var result = MixedSelectionEditingService.Paste(
            tab.Session,
            clipboard.EffectsOnly(),
            redLineBeat);
        tab.Session.Selection.Clear();
        tab.Session.Selection.UnionWith(preservedNoteSelection);
        Status = result.Applied
            ? AvaloniaLocalizationCatalog.Format("已在红线位置粘贴 {0:N0} 个 Effect", result.EffectCount)
            : "Effect 剪贴板没有可粘贴的内容";
        RaiseStateChanged();
        return result;
    }

    public MixedSelectionDeleteResult DeleteMixedSelection(IEnumerable<Guid> timingIds, CenterSymmetrySettings? symmetry = null)
    {
        var tab = ActiveTab ?? throw new InvalidOperationException("No chart tab is active.");
        var result = MixedSelectionEditingService.Delete(
            tab.Session,
            tab.Session.Selection,
            timingIds,
            tab.HiddenGroups, symmetry);
        Status = result.Applied
            ? AvaloniaLocalizationCatalog.Format("已删除 {0:N0} 个 Note、{1:N0} 个 Effect", result.DeletedNoteCount, result.DeletedEffectCount)
            : "没有可删除的 Note / Effect";
        RaiseStateChanged();
        return result;
    }

    public async Task<bool> CloseAsync(Guid tabId, bool discardUnsavedChanges) =>
        (await CloseTabAsync(tabId, discardUnsavedChanges)).Closed;

    /// <summary>
    /// Closes a tab only after the workspace manifest without it is saved. A
    /// failed save leaves the tab open and is returned, not thrown.
    /// </summary>
    public async Task<DesktopChartCloseResult> CloseTabAsync(Guid tabId, bool discardUnsavedChanges)
    {
        var result = await workspace.CloseTabAsync(tabId, discardUnsavedChanges);
        Status = result.Status switch
        {
            DesktopChartCloseStatus.Closed => "标签已关闭",
            DesktopChartCloseStatus.PersistFailed => AvaloniaLocalizationCatalog.Format(
                "工作区缓存写入失败：{0}",
                AvaloniaLocalizationCatalog.Text(result.Error?.Message ?? string.Empty)),
            _ => "标签有未保存更改"
        };
        RaiseStateChanged();
        return result;
    }

    /// <inheritdoc cref="DesktopDocumentWorkspace.GetProtectedCacheDirectories"/>
    public IReadOnlyList<string> GetProtectedCacheDirectories() =>
        workspace.GetProtectedCacheDirectories();

    /// <summary>
    /// Runs the settings page's cache cleanup inside the workspace gate. Every
    /// cache an open or suspended tab needs is protected automatically, and no
    /// tab can be opened while the cleanup runs.
    /// </summary>
    public Task<ManagedStorageCleanupResult> RunCacheCleanupAsync(
        IEnumerable<ManagedStorageCleanupTarget> targets,
        CancellationToken cancellationToken = default) =>
        workspace.RunCacheCleanupAsync(targets, cancellationToken);

    public async Task<bool> PersistAsync()
    {
        try
        {
            await workspace.PersistAsync();
            return true;
        }
        catch (Exception exception)
        {
            Status = AvaloniaLocalizationCatalog.Format("工作区缓存写入失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
            RaiseStateChanged();
            return false;
        }
    }

    public async Task<bool> PersistNavigationStateAsync()
    {
        try
        {
            await workspace.PersistNavigationStateAsync();
            return true;
        }
        catch (Exception exception)
        {
            Status = AvaloniaLocalizationCatalog.Format("跳转标记保存失败：{0}", AvaloniaLocalizationCatalog.Text(exception.Message));
            RaiseStateChanged();
            return false;
        }
    }

    public Task<bool> PreserveRoomDraftAsync(
        Guid tabId,
        long roomNumber,
        string? roomName,
        DateTimeOffset endedUtc,
        CancellationToken cancellationToken = default) =>
        workspace.PreserveRoomDraftAsync(
            tabId,
            roomNumber,
            roomName,
            endedUtc,
            cancellationToken);

    public void SetStatus(string status)
    {
        Status = status;
        RaiseStateChanged();
    }

    private string librarySearchParsedText = "";
    private DesktopLibrarySearch librarySearchParsed = DesktopLibrarySearch.Empty;

    /// <summary>
    /// The current search box text parsed into osu!-style filters. Parsed once per
    /// text change; per-chart evaluation never runs a regex.
    /// </summary>
    private DesktopLibrarySearch ParsedLibrarySearch
    {
        get
        {
            if (!string.Equals(librarySearchParsedText, LibrarySearchQuery, StringComparison.Ordinal))
            {
                librarySearchParsed = DesktopLibrarySearch.Parse(LibrarySearchQuery);
                librarySearchParsedText = LibrarySearchQuery;
            }
            return librarySearchParsed;
        }
    }

    /// <summary>
    /// Evaluates a search query over the cached library rows. Supports osu!'s song
    /// select keywords (mapper=, key=, bpm&lt;…) as well as the existing plain text,
    /// quoted phrases, -exclusion and personal tag:/state:/note:/star: fields.
    /// </summary>
    public DesktopLibrarySearchResult SearchLibrary(string? query)
    {
        var parsed = DesktopLibrarySearch.Parse(query);
        return parsed.IsEmpty
            ? new DesktopLibrarySearchResult(parsed, null)
            : new DesktopLibrarySearchResult(parsed, DesktopChartLibraryService.SearchPaths(libraryEntries, parsed));
    }

    /// <summary>Rows matching a query; used by pickers that need the entries themselves.</summary>
    public IReadOnlyList<DesktopLibraryChartEntry> SearchLibraryCharts(string? query)
    {
        var parsed = DesktopLibrarySearch.Parse(query);
        return parsed.IsEmpty
            ? LibraryEntries.ToArray()
            : DesktopChartLibraryService.SearchCharts(libraryEntries, parsed);
    }

    /// <summary>
    /// Explains why a search term had no effect: either the osu! field has no data
    /// in this index (e.g. length) or cached rows predate the BPM column.
    /// </summary>
    public string DescribeLibrarySearch(DesktopLibrarySearch search)
    {
        if (search.UnsupportedFields.Count > 0)
            return AvaloniaLocalizationCatalog.Format("暂不支持 {0}（该条件按文本搜索）", string.Join("、", search.UnsupportedFields));
        if (search.HasBpmFilter && libraryEntries.Any(entry => entry.Bpm is null))
            return "部分谱面缺少 BPM 数据，刷新索引后可补全";
        return "";
    }

    private string LibrarySearchStatus()
    {
        if (LibrarySearchQuery.Length == 0)
            return AvaloniaLocalizationCatalog.Format("曲库显示 {0:N0} 个文件夹", LibraryFolders.Count);
        var hint = DescribeLibrarySearch(ParsedLibrarySearch);
        var text = AvaloniaLocalizationCatalog.Format("搜索“{0}” · {1:N0} 张谱面", LibrarySearchQuery, LibraryFolders.Sum(folder => folder.VisibleCharts.Count));
        return hint.Length == 0 ? text : text + " · " + hint;
    }

    public void SetLibrarySearch(string? query)
    {
        query = query?.Trim() ?? string.Empty;
        if (string.Equals(LibrarySearchQuery, query, StringComparison.Ordinal)) return;
        LibrarySearchQuery = query;
        ApplyLibraryFilterAndSort();
        Status = LibrarySearchStatus();
        RaiseStateChanged();
    }

    public void SetLibrarySort(DesktopLibrarySort sort)
    {
        if (LibrarySort == sort) return;
        LibrarySort = sort;
        AvaloniaAppPaths.SaveWorkspacePreferences(WorkspaceRoot, LibrarySort);
        ApplyLibraryFilterAndSort();
        RaiseStateChanged();
    }

    private void ApplyLibraryFilterAndSort(HashSet<string>? resolvedSearch = null, bool searchAlreadyResolved = false)
    {
        // 0.1.38: osu!-style keyword filters are evaluated over the cached rows in
        // memory. The SQLite/FTS store is still kept for personal bookkeeping and
        // incremental indexing, but field filters need typed columns, not a blob.
        var matching = searchAlreadyResolved
            ? resolvedSearch
            : LibrarySearchQuery.Length == 0 ? null : DesktopChartLibraryService.SearchPaths(libraryEntries, ParsedLibrarySearch);
        var folders = DesktopChartLibraryService.FilterAndSort(libraryFolderIndex, "", LibrarySort);
        if (matching != null) folders = folders.Select(view => new DesktopLibraryFolderView(view.Folder, view.VisibleCharts.Where(chart => matching.Contains(chart.ChartPath)).ToArray()))
            .Where(view => view.VisibleCharts.Count > 0).ToArray();
        if (LibrarySourceFilter is { } filter)
        {
            var root = OsuFileSafetyPolicy.ResolvePhysicalPath(filter).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            folders = folders.Select(view => new DesktopLibraryFolderView(view.Folder,
                view.VisibleCharts.Where(chart => chart.ChartPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToArray()))
                .Where(view => view.VisibleCharts.Count > 0).ToArray();
        }
        libraryFolders.ReplaceAll(folders);
    }

    private void Workspace_Changed(object? sender, DesktopWorkspaceChangedEventArgs eventArgs)
    {
        if (Dispatcher.UIThread.CheckAccess()) RaiseStateChanged();
        else Dispatcher.UIThread.Post(RaiseStateChanged);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

}

public sealed record SaveRoundTripResult(
    string SourcePath,
    ChartStructureSummary BeforeSave,
    ChartStructureSummary AfterReload,
    bool StructurallyEqual);
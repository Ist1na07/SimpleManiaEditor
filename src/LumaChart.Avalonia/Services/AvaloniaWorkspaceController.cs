using LumaChart.Application.Archives;
using LumaChart.Application.Exporting;
using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Windows Avalonia orchestration over the shared, UI-free local workspace.
/// The view never parses, serializes, validates, or writes chart files itself.
/// </summary>
public sealed class AvaloniaWorkspaceController
{
    private readonly DesktopDocumentWorkspace workspace;
    private readonly SemaphoreSlim tabIoGate = new(1, 1);

    public AvaloniaWorkspaceController(
        string workspaceRoot,
        string persistentEditingRoot,
        string defaultCreator,
        Func<string>? fallbackWorkspaceRoot = null)
    {
        DefaultCreator = string.IsNullOrWhiteSpace(defaultCreator) ? "guest" : defaultCreator;
        workspace = new DesktopDocumentWorkspace(
            workspaceRoot,
            persistentEditingRoot,
            DefaultCreator,
            fallbackLibraryRoot: fallbackWorkspaceRoot);
        workspace.Changed += Workspace_Changed;
    }

    public string DefaultCreator { get; }
    public string WorkspaceRoot => workspace.LibraryRoot;
    public string ResolvedWorkspaceRoot => workspace.ResolvedLibraryRoot;

    /// <summary>Non-null when construction fell back from an unavailable root.</summary>
    public DesktopLibraryRootFallback? WorkspaceRootFallback => workspace.LibraryRootFallback;
    public IReadOnlyList<DesktopChartTab> Tabs => workspace.Tabs;
    public DesktopChartTab? ActiveTab => workspace.ActiveTab;
    public IReadOnlyList<string> RecoveryWarnings => workspace.RecoveryWarnings;
    public bool HasUnverifiedManifest => workspace.HasUnverifiedManifest;

    public event EventHandler<DesktopWorkspaceChangedEventArgs>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            await workspace.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task InitializeForExplicitStartupAsync(
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            await workspace.InitializeForExplicitStartupAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task<OsuNoteTimingImportMode?> TryGetSynchronizedSuspendedOsuTimingModeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.TryGetSynchronizedSuspendedOsuTimingModeAsync(
                    sourcePath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task<IReadOnlyList<DesktopLibraryChartEntry>> ScanAsync(
        CancellationToken cancellationToken = default)
    {
        // Re-resolve the path the user selected on every refresh. A Windows
        // junction may be deliberately retargeted between scans; WPF follows
        // that new target without replacing the requested setting.
        await workspace.SwitchLibraryRootAsync(workspace.LibraryRoot, cancellationToken)
            .ConfigureAwait(false);
        return await DesktopChartLibraryService.ScanAsync(
                workspace.ResolvedLibraryRoot,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SwitchRootAsync(string root, CancellationToken cancellationToken = default)
    {
        await workspace.SwitchLibraryRootAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DesktopChartTab> OpenAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task<DesktopChartTab> OpenAsync(
        string sourcePath,
        LumaChart.Core.IO.OsuNoteTimingImportMode osuTimingImportMode,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.OpenAsync(sourcePath, osuTimingImportMode, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task<IReadOnlyList<DesktopChartTab>> OpenProjectDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.OpenProjectDirectoryAsync(directoryPath, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public Task<IReadOnlyList<string>> InspectProjectDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<string>>(
            () => DesktopDocumentWorkspace.EnumerateProjectChartPaths(directoryPath),
            cancellationToken);

    public async Task<AvaloniaArchiveImportPlan> ImportArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        var imported = await Task.Run(
                () => ChartArchiveImportService.ExtractToUniqueDirectoryAsync(
                    archivePath,
                    workspace.LibraryRoot,
                    PlatformArchiveDirectoryCreators.Current
                        ?? ManagedArchiveDirectoryCreator.Instance,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        var supported = new List<string>(imported.ChartPaths.Count);
        var unsupported = new List<string>();
        var broken = new List<string>();
        foreach (var chartPath in imported.ChartPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    chartPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                _ = await ChartDocumentImporter.ImportAsync(
                        stream,
                        chartPath,
                        DefaultCreator,
                        cancellationToken)
                    .ConfigureAwait(false);
                supported.Add(chartPath);
            }
            catch (NotSupportedException)
            {
                unsupported.Add(chartPath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                broken.Add(chartPath);
            }
        }
        return new AvaloniaArchiveImportPlan(imported, supported, unsupported, broken);
    }

    public async Task<AvaloniaArchiveOpenResult> OpenImportedChartsAsync(
        IEnumerable<string> chartPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chartPaths);
        await tabIoGate.WaitAsync(cancellationToken);
        var opened = new List<DesktopChartTab>();
        var failed = new List<string>();
        try
        {
            foreach (var chartPath in chartPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    opened.Add(await workspace.OpenAsync(chartPath, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed.Add(chartPath);
                }
            }
        }
        finally
        {
            tabIoGate.Release();
        }
        return new AvaloniaArchiveOpenResult(opened, failed);
    }

    public async Task<DesktopChartTab> CreateAsync(
        string title,
        string? creator = null,
        string? suggestedName = null,
        CancellationToken cancellationToken = default)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        creator = string.IsNullOrWhiteSpace(creator) ? DefaultCreator : creator.Trim();
        var document = new ChartDocument
        {
            Metadata = new ChartMetadata
            {
                Title = title,
                Artist = "Unknown Artist",
                Creator = creator,
                Version = "Normal",
                Bpm = 120
            },
            KeyCount = 4,
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
        };
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.CreateAsync(
                    document,
                    suggestedName ?? title,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task<DesktopNamedAudioChartCreation> CreateNamedAudioChartAsync(
        string? requestedName,
        string? creator = null,
        CancellationToken cancellationToken = default)
    {
        creator = string.IsNullOrWhiteSpace(creator) ? DefaultCreator : creator.Trim();
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.CreateNamedAudioChartAsync(
                    requestedName,
                    creator,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public bool Activate(Guid tabId) => workspace.Activate(tabId);

    public bool UpdateViewState(Guid tabId, double currentBeat) =>
        workspace.UpdateViewState(tabId, currentBeat);

    public bool ReplaceNavigationMarkers(
        Guid tabId,
        IEnumerable<DesktopNavigationMarker> markers) =>
        workspace.ReplaceNavigationMarkers(tabId, markers);

    /// <inheritdoc cref="DesktopDocumentWorkspace.GetProtectedCacheDirectories"/>
    public IReadOnlyList<string> GetProtectedCacheDirectories() =>
        workspace.GetProtectedCacheDirectories();

    /// <inheritdoc cref="DesktopDocumentWorkspace.GetReferencedSourcePaths"/>
    public IReadOnlyList<string> GetReferencedSourcePaths() =>
        workspace.GetReferencedSourcePaths();

    /// <summary>
    /// Runs managed-storage cleanup while no tab can be opened, saved, exported
    /// or closed; the workspace adds every in-use cache to the protected set.
    /// </summary>
    public async Task<ManagedStorageCleanupResult> RunCacheCleanupAsync(
        IEnumerable<ManagedStorageCleanupTarget> targets,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.RunCacheCleanupAsync(targets, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task<DesktopChartSaveResult> SaveAsync(
        Guid tabId,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.SaveAsync(tabId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task<DesktopChartSaveResult> SaveAsAsync(
        Guid tabId,
        string destination,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.SaveAsAsync(tabId, destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    /// <summary>
    /// Exports the tab's last committed state as a Malody FullPackage 1.0x MCZ.
    /// No save or workspace persistence occurs: dirty state, change serial, source
    /// path, working path, and the caller-owned document remain untouched.
    /// </summary>
    public async Task<MalodyFullPackageExportResult> ExportMalodyFullPackageAsync(
        Guid tabId,
        string destinationArchivePath,
        IProgress<MalodyFullPackageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationArchivePath);
        destinationArchivePath = Path.GetFullPath(destinationArchivePath);

        // This gate also covers operations that can retarget the source or remove
        // and rebuild the localized working cache. The committed clone is captured
        // before Task.Run starts, so a live pointer gesture cannot leak a preview
        // state into the package.
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            var tab = workspace.Tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                      ?? throw new KeyNotFoundException("The requested desktop chart tab is not open.");
            var sourcePath = Path.GetFullPath(tab.SourcePath);

            // Match WPF 1.1.0: an externally removed source file does not block
            // exporting the still-open working document. Its original path still
            // supplies the package directory and the main entry name.
            var committedDocument = tab.Session.CaptureCommittedDocument();
            return await MalodyFullPackageExportService.ExportAsync(
                    new MalodyFullPackageExportRequest(
                        committedDocument,
                        sourcePath,
                        destinationArchivePath,
                        AvaloniaAppPaths.SessionScratchRoot),
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    /// <summary>
    /// Exports the tab's last committed state as an osu!mania FullPackage 1.0x
    /// OSZ without saving or retargeting the open desktop document.
    /// </summary>
    public async Task<OsuFullPackageExportResult> ExportOsuFullPackageAsync(
        Guid tabId,
        string destinationArchivePath,
        double overallDifficulty,
        double hpDrainRate,
        IProgress<OsuFullPackageExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationArchivePath);
        destinationArchivePath = Path.GetFullPath(destinationArchivePath);

        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            var tab = workspace.Tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                      ?? throw new KeyNotFoundException("The requested desktop chart tab is not open.");
            var sourcePath = Path.GetFullPath(tab.SourcePath);
            var committedDocument = tab.Session.CaptureCommittedDocument();
            return await OsuFullPackageExportService.ExportAsync(
                    new OsuFullPackageExportRequest(
                        committedDocument,
                        sourcePath,
                        destinationArchivePath,
                        AvaloniaAppPaths.SessionScratchRoot,
                        overallDifficulty,
                        hpDrainRate),
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    /// <summary>
    /// Exports the tab's last committed dirty-capable snapshot through the same
    /// four modes as Windows/WPF 1.1.0. This never saves, retargets, or mutates
    /// the open desktop tab. Windows audio processing stays behind the shared
    /// Application contract.
    /// </summary>
    public async Task<ChartExportResult> ExportChartAsync(
        Guid tabId,
        string destinationPath,
        ChartExportOptions options,
        IProgress<ChartExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(options);
        destinationPath = Path.GetFullPath(destinationPath);

        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            var tab = workspace.Tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                      ?? throw new KeyNotFoundException("The requested desktop chart tab is not open.");
            var sourcePath = Path.GetFullPath(tab.SourcePath);
            var committedDocument = tab.Session.CaptureCommittedDocument();
            return await ChartExportService.ExportAsync(
                    new ChartExportRequest(
                        committedDocument,
                        destinationPath,
                        options,
                        AvaloniaAppPaths.SessionScratchRoot,
                        sourcePath),
                    WindowsChartExportAudioTranscoder.Instance,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task<bool> CloseAsync(
        Guid tabId,
        bool discardUnsavedChanges,
        CancellationToken cancellationToken = default) =>
        (await CloseTabAsync(tabId, discardUnsavedChanges, cancellationToken)).Closed;

    /// <inheritdoc cref="DesktopDocumentWorkspace.CloseTabAsync"/>
    public async Task<DesktopChartCloseResult> CloseTabAsync(
        Guid tabId,
        bool discardUnsavedChanges,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.CloseTabAsync(tabId, discardUnsavedChanges, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public Task PersistAsync(CancellationToken cancellationToken = default) =>
        workspace.PersistAsync(cancellationToken);

    public Task PersistNavigationStateAsync(
        CancellationToken cancellationToken = default) =>
        workspace.PersistNavigationStateAsync(cancellationToken);

    public async Task<bool> PreserveRoomDraftAsync(
        Guid tabId,
        long roomNumber,
        string? roomName,
        DateTimeOffset endedUtc,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            return await workspace.PreserveRoomDraftAsync(
                    tabId,
                    roomNumber,
                    roomName,
                    endedUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    public async Task ReloadFromSourceAsync(
        Guid tabId,
        CancellationToken cancellationToken = default)
    {
        await tabIoGate.WaitAsync(cancellationToken);
        try
        {
            await workspace.ReloadFromSourceAsync(tabId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tabIoGate.Release();
        }
    }

    private void Workspace_Changed(object? sender, DesktopWorkspaceChangedEventArgs eventArgs) =>
        Changed?.Invoke(this, eventArgs);
}

public sealed record AvaloniaArchiveImportPlan(
    ChartArchiveImportResult Archive,
    IReadOnlyList<string> SupportedChartPaths,
    IReadOnlyList<string> UnsupportedChartPaths,
    IReadOnlyList<string> BrokenChartPaths);

public sealed record AvaloniaArchiveOpenResult(
    IReadOnlyList<DesktopChartTab> OpenedTabs,
    IReadOnlyList<string> FailedPaths);

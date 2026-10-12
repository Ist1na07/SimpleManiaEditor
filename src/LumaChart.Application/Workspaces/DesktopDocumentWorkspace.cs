using LumaChart.Core.Localization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Validation;

namespace LumaChart.Application.Workspaces;

/// <summary>
/// UI-free document workspace for the Windows desktop client. The selected library
/// root controls library discovery and new-project placement only; open tabs retain
/// their absolute source paths when that root changes.
/// </summary>
public sealed class DesktopDocumentWorkspace
{
    public const int MaximumProjectChartFiles = 128;

    private const int ManifestSchemaVersion = 1;
    private const int IoBufferSize = 64 * 1024;
    private const int MaximumSuggestedNameCharacters = 64;
    private const string WorkingChartFileName = "working.mc";
    private const int ManifestReadAttempts = 4;
    private const int ManifestLockAttempts = 50;
    private static readonly TimeSpan ManifestRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string editingRoot;
    private readonly string documentCacheRoot;
    private readonly string manifestPath;
    private readonly string manifestLockPath;
    private readonly string defaultCreator;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<DesktopChartTab> tabs = [];
    private readonly List<string> recoveryWarnings = [];
    private readonly List<DesktopWorkspaceTabManifest> suspendedTabs = [];
    // Immutable views published for callers outside the gate. The UI thread
    // enumerates Tabs while a gated operation may be adding or removing tabs on
    // the thread pool, so it must never observe the mutable list itself.
    private IReadOnlyList<DesktopChartTab> tabSnapshot = [];
    private IReadOnlyList<DesktopWorkspaceTabManifest> suspendedSnapshot = [];
    private Guid? suspendedActiveTabId;
    private string libraryRoot;
    private string resolvedLibraryRoot;
    private readonly Func<string>? creationRootFallback;
    private long documentSnapshotTicket;
    // Set while an existing manifest could not be verified as readable. Until
    // it is readable again or has been quarantined, the manifest must not be
    // replaced by this session's (possibly empty) tab list.
    private bool manifestWriteBlocked;
    private bool manifestWriteBlockedWarningAdded;

    public DesktopDocumentWorkspace(
        string libraryRoot,
        string persistentEditingRoot,
        string defaultCreator,
        TimeProvider? timeProvider = null,
        Func<string>? fallbackLibraryRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentEditingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultCreator);
        creationRootFallback = fallbackLibraryRoot;

        try
        {
            (this.libraryRoot, resolvedLibraryRoot) = PrepareLibraryRoot(libraryRoot);
        }
        catch (Exception exception) when (
            fallbackLibraryRoot != null &&
            IsUnavailableLibraryRootError(exception))
        {
            // A saved root on an unplugged drive, an offline share, a broken
            // junction or a read-only portable folder must not stop the editor
            // from starting. The fallback is reported to the caller and is not
            // persisted here, so the original root returns once it is reachable.
            // The provider is only invoked here so a healthy start never
            // creates the default root as a side effect.
            (this.libraryRoot, resolvedLibraryRoot) = PrepareLibraryRoot(fallbackLibraryRoot());
            LibraryRootFallback = new DesktopLibraryRootFallback(
                libraryRoot,
                this.libraryRoot,
                exception.Message);
        }
        editingRoot = Path.GetFullPath(Path.Combine(persistentEditingRoot, "desktop-workspace"));
        documentCacheRoot = Path.Combine(editingRoot, "documents");
        manifestPath = Path.Combine(editingRoot, "workspace.json");
        manifestLockPath = manifestPath + ".lock";
        this.defaultCreator = defaultCreator;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(documentCacheRoot);
    }

    /// <summary>The exact user-selected root, including a root directory link when applicable.</summary>
    public string LibraryRoot => libraryRoot;

    /// <summary>The final target used by scanners to avoid following descendant reparse points.</summary>
    public string ResolvedLibraryRoot => resolvedLibraryRoot;

    /// <summary>
    /// Non-null when the requested library root was unavailable at construction
    /// and the caller-supplied fallback root is being used for this session only.
    /// </summary>
    public DesktopLibraryRootFallback? LibraryRootFallback { get; }

    public string EditingRoot => editingRoot;
    public string ManifestPath => manifestPath;
    public string DocumentCacheRoot => documentCacheRoot;
    public string DefaultCreator => defaultCreator;

    /// <summary>An immutable snapshot of the open tabs; safe to enumerate from any thread.</summary>
    public IReadOnlyList<DesktopChartTab> Tabs => Volatile.Read(ref tabSnapshot);
    public DesktopChartTab? ActiveTab { get; private set; }
    public IReadOnlyList<string> RecoveryWarnings => recoveryWarnings;

    public event EventHandler<DesktopWorkspaceChangedEventArgs>? Changed;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        InitializeCoreAsync(restorePreviousSession: true, cancellationToken);

    /// <summary>
    /// Initializes a cold launch that was explicitly opened with one or more
    /// chart paths. WPF does not materialize the previous editor session in that
    /// launch, but its recoverable working copies remain available for the next
    /// ordinary launch. Suspended entries are merged into the manifest while the
    /// explicit charts are open so no unsaved work is discarded.
    /// </summary>
    public Task InitializeForExplicitStartupAsync(
        CancellationToken cancellationToken = default) =>
        InitializeCoreAsync(restorePreviousSession: false, cancellationToken);

    /// <summary>
    /// Returns the saved osu timing mode only when an explicitly suspended cache
    /// still matches both its source and working fingerprints. WPF skips the osu
    /// import-choice dialog in exactly that case; a changed source or cache must
    /// ask again before importing.
    /// </summary>
    public async Task<OsuNoteTimingImportMode?> TryGetSynchronizedSuspendedOsuTimingModeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        sourcePath = NormalizeSourcePath(sourcePath, requireExisting: true);
        if (!Path.GetExtension(sourcePath).Equals(".osu", StringComparison.OrdinalIgnoreCase))
            return null;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = suspendedTabs.FirstOrDefault(candidate =>
                ManifestSourcePathEquals(candidate.SourcePath, sourcePath));
            if (item == null ||
                string.IsNullOrWhiteSpace(item.SourceSha256) ||
                string.IsNullOrWhiteSpace(item.WorkingSha256))
                return null;

            string cacheDirectory;
            try
            {
                cacheDirectory = ResolveCacheDirectory(
                    item.CacheDirectoryName,
                    sourcePath);
            }
            catch (InvalidDataException)
            {
                return null;
            }
            var workingPath = Path.Combine(cacheDirectory, WorkingChartFileName);
            if (!File.Exists(workingPath)) return null;

            try
            {
                var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken)
                    .ConfigureAwait(false);
                var workingHash = await ComputeSha256Async(workingPath, cancellationToken)
                    .ConfigureAwait(false);
                return string.Equals(
                           sourceHash,
                           item.SourceSha256,
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           workingHash,
                           item.WorkingSha256,
                           StringComparison.OrdinalIgnoreCase)
                    ? ParseOsuTimingImportMode(item.OsuTimingImportMode)
                    : null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return null;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task InitializeCoreAsync(
        bool restorePreviousSession,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DetachAllTabs();
            tabs.Clear();
            PublishTabs();
            ActiveTab = null;
            recoveryWarnings.Clear();
            suspendedTabs.Clear();
            PublishSuspendedTabs();
            suspendedActiveTabId = null;
            manifestWriteBlockedWarningAdded = false;

            var manifest = await TryReadManifestAsync(cancellationToken).ConfigureAwait(false);
            if (manifest != null)
            {
                if (!restorePreviousSession)
                {
                    suspendedTabs.AddRange(
                        (manifest.Tabs ?? []).OrderBy(item => item.Order));
                    PublishSuspendedTabs();
                    suspendedActiveTabId = manifest.ActiveTabId;
                }
                else
                {
                    var restoredPaths = new HashSet<string>(PathComparer);
                    foreach (var item in (manifest.Tabs ?? []).OrderBy(item => item.Order))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var sourcePath = NormalizeSourcePath(item.SourcePath, requireExisting: false);
                            if (!restoredPaths.Add(sourcePath))
                            {
                                recoveryWarnings.Add($"{sourcePath}: duplicate cached tab was ignored.");
                                continue;
                            }

                            var tab = await RestoreTabAsync(item, sourcePath, cancellationToken)
                                .ConfigureAwait(false);
                            if (tab == null) continue;
                            AttachTab(tab);
                            tabs.Add(tab);
                            PublishTabs();
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            recoveryWarnings.Add($"{item.SourcePath}: {exception.Message}");
                        }
                    }

                    ActiveTab = tabs.FirstOrDefault(tab => tab.Id == manifest.ActiveTabId) ??
                                tabs.FirstOrDefault();
                    // Every restored tab's working copy already matches its
                    // document, so only the manifest needs to be rewritten.
                    await PersistCoreAsync(NoDocumentSnapshots, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        PublishChanged(DesktopWorkspaceChangeKind.Reset, ActiveTab);
    }

    /// <summary>
    /// Changes only the library/create root. Existing tabs, working copies, active
    /// tab, dirty state, and absolute source paths are left untouched.
    /// </summary>
    public async Task SwitchLibraryRootAsync(
        string root,
        CancellationToken cancellationToken = default)
    {
        var prepared = PrepareLibraryRoot(root);
        // Library refreshes call this with an unchanged root. Skip the document
        // snapshots in that common case; the gated comparison below stays
        // authoritative.
        if (PathComparer.Equals(libraryRoot, prepared.Requested) &&
            PathComparer.Equals(resolvedLibraryRoot, prepared.Resolved))
            return;
        var snapshots = CaptureDocumentSnapshots();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (PathComparer.Equals(libraryRoot, prepared.Requested) &&
                PathComparer.Equals(resolvedLibraryRoot, prepared.Resolved))
                return;

            libraryRoot = prepared.Requested;
            resolvedLibraryRoot = prepared.Resolved;
            await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        PublishChanged(DesktopWorkspaceChangeKind.LibraryRoot, ActiveTab);
    }

    public Task<DesktopChartTab> OpenAsync(
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        OpenAsync(sourcePath, OsuNoteTimingImportMode.PreserveMilliseconds, cancellationToken);

    public async Task<DesktopChartTab> OpenAsync(
        string sourcePath,
        OsuNoteTimingImportMode osuTimingImportMode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(osuTimingImportMode))
            throw new ArgumentOutOfRangeException(nameof(osuTimingImportMode));
        sourcePath = NormalizeSourcePath(sourcePath, requireExisting: true);
        sourcePath = await OsuSongsWorkspacePolicy.GetEditableCopyAsync(sourcePath, SafeCreationRoot(), cancellationToken).ConfigureAwait(false);

        var snapshots = CaptureDocumentSnapshots();
        DesktopChartTab tab;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = tabs.FirstOrDefault(candidate => PathComparer.Equals(candidate.SourcePath, sourcePath));
            if (existing != null)
            {
                ActiveTab = existing;
                await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
                tab = existing;
            }
            else
            {
                var suspended = suspendedTabs.FirstOrDefault(item =>
                    ManifestSourcePathEquals(item.SourcePath, sourcePath));
                if (suspended != null)
                {
                    suspendedTabs.Remove(suspended);
                    PublishSuspendedTabs();
                    if (suspendedActiveTabId == suspended.Id)
                        suspendedActiveTabId = null;
                    suspended.OsuTimingImportMode = osuTimingImportMode.ToString();
                    DesktopChartTab? restored = null;
                    try
                    {
                        restored = await RestoreTabAsync(suspended, sourcePath, cancellationToken)
                            .ConfigureAwait(false)
                              ?? throw new InvalidDataException(
                                  "The suspended chart cache could not be restored.");
                        AttachTab(restored);
                        tabs.Add(restored);
                        PublishTabs();
                        ActiveTab = restored;
                        await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
                        tab = restored;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // A restored tab whose persistence failed must not stay
                        // open beside the fresh tab opened below for the same source.
                        if (restored != null && tabs.Remove(restored))
                        {
                            restored.Session.Changed -= Session_Changed;
                            PublishTabs();
                            if (ReferenceEquals(ActiveTab, restored)) ActiveTab = tabs.LastOrDefault();
                        }
                        recoveryWarnings.Add(
                            $"{sourcePath}: suspended cache was ignored: {exception.Message}");
                        tab = await OpenFreshTabCoreAsync(
                                sourcePath,
                                osuTimingImportMode,
                                snapshots,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    tab = await OpenFreshTabCoreAsync(
                            sourcePath,
                            osuTimingImportMode,
                            snapshots,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }

        PublishChanged(DesktopWorkspaceChangeKind.ActiveTab, tab);
        return tab;
    }

    private async Task<DesktopChartTab> OpenFreshTabCoreAsync(
        string sourcePath,
        OsuNoteTimingImportMode osuTimingImportMode,
        IReadOnlyDictionary<DesktopChartTab, DocumentSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var format = GetFormat(sourcePath);
        var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        var cacheDirectory = AllocateCacheDirectory(sourcePath);
        Directory.CreateDirectory(cacheDirectory);
        var workingPath = Path.Combine(cacheDirectory, WorkingChartFileName);
        var document = await LoadSourceAsync(
                sourcePath,
                format,
                osuTimingImportMode,
                cancellationToken)
            .ConfigureAwait(false);
        await DesktopChartAssetLocalizer.LocalizeAsync(
                document,
                sourcePath,
                workingPath,
                cancellationToken)
            .ConfigureAwait(false);
        document.FilePath = workingPath;
        var workingHash = await WriteWorkingCopyAsync(
                document,
                workingPath,
                cancellationToken)
            .ConfigureAwait(false);
        var session = new EditorSession();
        session.ReplaceDocument(document);
        var tab = new DesktopChartTab(
            Guid.NewGuid(),
            session,
            sourcePath,
            cacheDirectory,
            workingPath,
            format,
            osuTimingImportMode)
        {
            SourceSha256 = sourceHash,
            WorkingSha256 = workingHash
        };
        AttachTab(tab);
        tabs.Add(tab);
        PublishTabs();
        ActiveTab = tab;
        await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
        return tab;
    }

    /// <summary>Opens supported chart files directly inside a project directory.</summary>
    public async Task<IReadOnlyList<DesktopChartTab>> OpenProjectDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        var paths = EnumerateProjectChartPaths(directoryPath);
        var opened = new List<DesktopChartTab>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            opened.Add(await OpenAsync(path, cancellationToken).ConfigureAwait(false));
        }
        return opened;
    }

    public static IReadOnlyList<string> EnumerateProjectChartPaths(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        directoryPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException(LocalizationCatalog.Format("找不到项目目录：{0}", directoryPath));

        var paths = Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedSourcePath)
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(path => path, PathComparer)
            .ToArray();
        if (paths.Length == 0)
            throw new DesktopProjectDirectoryValidationException(
                DesktopProjectDirectoryValidationFailure.NoSupportedCharts,
                paths.Length,
                MaximumProjectChartFiles,
                "所选项目目录中没有直接包含的 .mc 或 .osu 谱面。");
        if (paths.Length > MaximumProjectChartFiles)
            throw new DesktopProjectDirectoryValidationException(
                DesktopProjectDirectoryValidationFailure.TooManyCharts,
                paths.Length,
                MaximumProjectChartFiles,
                LocalizationCatalog.Format("项目目录直接包含 {0:N0} 张谱面，超过 {1:N0} 张安全上限。", paths.Length, MaximumProjectChartFiles));
        return paths;
    }

    public async Task<DesktopChartTab> CreateAsync(
        ChartDocument document,
        string? suggestedName = null,
        CancellationToken cancellationToken = default,
        DesktopChartFormat format = DesktopChartFormat.MalodyMc)
    {
        ArgumentNullException.ThrowIfNull(document);
        var (folder, sourcePath) = AllocateNewChartLocation(suggestedName);
        if (format == DesktopChartFormat.OsuMania) sourcePath = Path.ChangeExtension(sourcePath, ".osu");
        try
        {
            var sourceDocument = document.Clone();
            if (string.IsNullOrWhiteSpace(sourceDocument.Metadata.Creator))
                sourceDocument.Metadata.Creator = defaultCreator;
            await WriteSourceSnapshotAtomicAsync(
                sourceDocument,
                sourcePath,
                format,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteEmptyDirectory(folder);
            throw;
        }

        return await OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the WPF 1.1.0 "import audio" project shape: the requested song
    /// folder name is used directly, collisions append _1/_2, and the chart file
    /// name is the current Unix timestamp. The final allocated folder name is
    /// also written into the appropriate title field before the source snapshot
    /// is created.
    /// </summary>
    public async Task<DesktopNamedAudioChartCreation> CreateNamedAudioChartAsync(
        string? requestedName,
        string? creator,
        CancellationToken cancellationToken = default,
        int keyCount = 4,
        DesktopChartFormat format = DesktopChartFormat.MalodyMc,
        int osuSampleVolume = 70)
    {
        var (folder, sourcePath, finalName) = AllocateNamedAudioChartLocation(requestedName);
        if (format == DesktopChartFormat.OsuMania) sourcePath = Path.ChangeExtension(sourcePath, ".osu");
        try
        {
            var document = new ChartDocument();
            document.KeyCount = Math.Clamp(keyCount, 1, 18);
            if (format == DesktopChartFormat.OsuMania)
                new OsuTimingPointSettings(4, 2, 1, osuSampleVolume).Apply(document.GetOrCreateSongOffsetTiming());
            document.Metadata.Version = $"{document.KeyCount}K";
            document.Metadata.Creator = string.IsNullOrWhiteSpace(creator)
                ? defaultCreator
                : creator.Trim();
            var cleanedName = CleanSingleLine(finalName);
            if (IsCommunityAscii(cleanedName))
                document.Metadata.Title = cleanedName;
            else
                document.Metadata.TitleOriginal = cleanedName;
            await WriteSourceSnapshotAtomicAsync(
                document,
                sourcePath,
                format,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteEmptyDirectory(folder);
            throw;
        }

        var tab = await OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        return new DesktopNamedAudioChartCreation(tab, finalName);
    }

    /// <summary>
    /// Activates an open tab. Returns false instead of throwing when the tab was
    /// closed in the meantime; UI callbacks can race a gated close.
    /// </summary>
    public bool Activate(Guid tabId)
    {
        var tab = Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
        if (tab == null) return false;
        if (ReferenceEquals(ActiveTab, tab)) return true;
        ActiveTab = tab;
        PublishChanged(DesktopWorkspaceChangeKind.ActiveTab, tab);
        return true;
    }

    public bool UpdateViewState(Guid tabId, double currentBeat)
    {
        var tab = Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
        if (tab == null) return false;
        tab.CurrentBeat = double.IsFinite(currentBeat)
            ? OsuSourceEnvelope.HasSource(tab.Session.Document) ? currentBeat : Math.Max(0, currentBeat) : 0;
        return true;
    }

    public bool ReplaceNavigationMarkers(
        Guid tabId,
        IEnumerable<DesktopNavigationMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var tab = Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
        if (tab == null) return false;
        tab.ReplaceNavigationMarkers(markers);
        return true;
    }

    /// <summary>
    /// Saves to the tab's absolute source path, rejecting external changes
    /// and an unverified recovery copy.
    /// </summary>
    public async Task<DesktopChartSaveResult> SaveAsync(
        Guid tabId,
        CancellationToken cancellationToken = default)
    {
        // The committed document is cloned on the calling (UI) thread before the
        // first await. After that the workspace runs on the thread pool, where a
        // clone could observe a half-applied in-place edit.
        var requestedTab = Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
        if (requestedTab != null && OsuSongsWorkspacePolicy.IsSongsPath(requestedTab.SourcePath))
        {
            var editable = await OsuSongsWorkspacePolicy.GetEditableCopyAsync(requestedTab.SourcePath, SafeCreationRoot(), cancellationToken).ConfigureAwait(false);
            return await SaveAsAsync(tabId, editable, cancellationToken).ConfigureAwait(false);
        }
        // Serial first, then the clone: an edit racing between the two leaves
        // the tab dirty instead of wrongly marking newer content as saved.
        var requestedChangeSerial = requestedTab?.Session.ChangeSerial ?? 0;
        var snapshots = CaptureDocumentSnapshots();
        DesktopChartTab? tab = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            tab = tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                  ?? throw new KeyNotFoundException("The requested desktop chart tab is not open.");
            if (tab.RequiresRecoverySaveAs)
                throw new IOException("这是旧版校验不一致的恢复副本。编辑已保留，请另存为新文件后继续；不能直接覆盖源谱面。");
            var (savedChangeSerial, snapshot) = ResolveRequestedSnapshot(
                tab,
                requestedTab,
                requestedChangeSerial,
                snapshots);
            var committed = snapshot.Document;
            var issues = ChartValidator.Validate(committed);
            if (issues.Count > 0)
                return new DesktopChartSaveResult(DesktopChartSaveStatus.ValidationFailed, issues);

            tab.WorkingSha256 = await WriteWorkingCopyAsync(
                committed,
                tab.WorkingPath,
                cancellationToken).ConfigureAwait(false);
            tab.PersistedSnapshotTicket = Math.Max(tab.PersistedSnapshotTicket, snapshot.Ticket);
            // Record the newly written working copy before touching the source.
            // If the source replacement fails (locked/read-only/full disk), the
            // next startup must recover this exact dirty cache instead of
            // treating it as an untrusted hash mismatch and replacing it with
            // the unchanged source.
            await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
            if (File.Exists(tab.SourcePath) && !string.IsNullOrEmpty(tab.SourceSha256) &&
                !string.Equals(await ComputeSha256Async(tab.SourcePath, cancellationToken).ConfigureAwait(false),
                    tab.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("源谱面已被其他程序修改；请另存为或重新打开，当前编辑已保存在恢复副本中。");
            await WriteSourceSnapshotAtomicAsync(
                committed,
                tab.SourcePath,
                tab.Format,
                cancellationToken).ConfigureAwait(false);
            tab.SourceSha256 = await ComputeSha256Async(tab.SourcePath, cancellationToken).ConfigureAwait(false);
            tab.SourceMissing = false;
            tab.Session.TryMarkSaved(savedChangeSerial);
            await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
            return new DesktopChartSaveResult(DesktopChartSaveStatus.Saved, []);
        }
        finally
        {
            gate.Release();
            if (tab != null) PublishChanged(DesktopWorkspaceChangeKind.Document, tab);
        }
    }

    /// <summary>
    /// Saves the committed document to a new absolute MC or osu! source path and
    /// retargets the tab without moving its persistent editing cache. The previous
    /// source is never deleted.
    /// </summary>
    public async Task<DesktopChartSaveResult> SaveAsAsync(
        Guid tabId,
        string destination,
        CancellationToken cancellationToken = default)
    {
        destination = NormalizeSourcePath(destination, requireExisting: false);
        if (OsuSongsWorkspacePolicy.IsSongsPath(destination))
            throw new InvalidOperationException("Songs 是游戏谱面库，请另存到独立创作目录；发布到游戏库应使用明确的导出流程。");
        var destinationFormat = GetFormat(destination);

        var requestedTab = Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
        var requestedChangeSerial = requestedTab?.Session.ChangeSerial ?? 0;
        var snapshots = CaptureDocumentSnapshots();
        DesktopChartTab? tab = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            tab = tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                  ?? throw new KeyNotFoundException("The requested desktop chart tab is not open.");
            if (tab.RequiresRecoverySaveAs && PathComparer.Equals(tab.SourcePath, destination))
                throw new IOException("恢复副本请另存为不同文件；源谱面保持原状。");
            var (savedChangeSerial, snapshot) = ResolveRequestedSnapshot(
                tab,
                requestedTab,
                requestedChangeSerial,
                snapshots);
            var committed = snapshot.Document;
            var issues = ChartValidator.Validate(committed);
            if (issues.Count > 0)
                return new DesktopChartSaveResult(DesktopChartSaveStatus.ValidationFailed, issues);

            if (tabs.Any(candidate =>
                    !ReferenceEquals(candidate, tab) &&
                    PathComparer.Equals(candidate.SourcePath, destination)))
                throw new InvalidOperationException(
                    "The save-as destination is already used by another open chart tab.");

            var destinationOsuTimingImportMode = tab.OsuTimingImportMode;
            tab.WorkingSha256 = await WriteWorkingCopyAsync(
                committed,
                tab.WorkingPath,
                cancellationToken).ConfigureAwait(false);
            tab.PersistedSnapshotTicket = Math.Max(tab.PersistedSnapshotTicket, snapshot.Ticket);
            // Keep the old source mapping until the new destination is safely
            // committed, but trust the latest dirty working copy immediately.
            // A failed Save As therefore remains recoverable on restart.
            await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
            await WriteSourceSnapshotAtomicAsync(
                committed,
                destination,
                destinationFormat,
                cancellationToken).ConfigureAwait(false);
            var destinationHash = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);

            tab.SourcePath = destination;
            tab.Format = destinationFormat;
            // WPF keeps the tab's selected osu timing-import policy when its
            // source is retargeted. It remains inert for MC and becomes the
            // default again if the tab is later saved as osu.
            tab.OsuTimingImportMode = destinationOsuTimingImportMode;
            tab.SourceSha256 = destinationHash;
            tab.SourceMissing = false;
            tab.RequiresRecoverySaveAs = false;
            tab.IsPreservedRoomDraft = false;
            tab.PreservedRoomNumber = 0;
            tab.PreservedRoomName = string.Empty;
            tab.PreservedRoomEndedUtc = null;
            tab.Session.TryMarkSaved(savedChangeSerial);
            await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
            return new DesktopChartSaveResult(DesktopChartSaveStatus.Saved, []);
        }
        finally
        {
            gate.Release();
            if (tab != null) PublishChanged(DesktopWorkspaceChangeKind.Document, tab);
        }
    }

    public async Task ReloadFromSourceAsync(
        Guid tabId,
        CancellationToken cancellationToken = default)
    {
        var snapshots = CaptureDocumentSnapshots();
        DesktopChartTab? tab = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            tab = tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                  ?? throw new KeyNotFoundException("The requested desktop chart tab is not open.");
            if (!File.Exists(tab.SourcePath))
                throw new FileNotFoundException("The chart source no longer exists.", tab.SourcePath);

            var document = await LoadSourceAsync(
                tab.SourcePath,
                tab.Format,
                tab.OsuTimingImportMode,
                cancellationToken).ConfigureAwait(false);
            await DesktopChartAssetLocalizer.LocalizeAsync(
                document,
                tab.SourcePath,
                tab.WorkingPath,
                cancellationToken).ConfigureAwait(false);
            document.FilePath = tab.WorkingPath;
            tab.SourceSha256 = await ComputeSha256Async(tab.SourcePath, cancellationToken).ConfigureAwait(false);
            tab.WorkingSha256 = await WriteWorkingCopyAsync(document, tab.WorkingPath, cancellationToken)
                .ConfigureAwait(false);
            // The reloaded working copy supersedes the pre-reload snapshot
            // captured on entry; it must not be written back over it.
            tab.PersistedSnapshotTicket = Interlocked.Increment(ref documentSnapshotTicket);
            tab.SourceMissing = false;
            tab.RequiresRecoverySaveAs = false;
            tab.Session.ReplaceDocument(document);
            await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        if (tab != null) PublishChanged(DesktopWorkspaceChangeKind.Document, tab);
    }

    /// <summary>
    /// Explicitly closes a tab and deletes its independent cache directory. Dirty
    /// tabs remain open unless the caller has already obtained discard consent.
    /// Returns false when the tab stayed open, including when the manifest could
    /// not be persisted; see <see cref="CloseTabAsync"/> for the reason.
    /// </summary>
    public async Task<bool> CloseAsync(
        Guid tabId,
        bool discardUnsavedChanges,
        CancellationToken cancellationToken = default) =>
        (await CloseTabAsync(tabId, discardUnsavedChanges, cancellationToken).ConfigureAwait(false))
        .Closed;

    /// <summary>
    /// Closes a tab only after the manifest without it has been persisted. A
    /// persistence failure restores the tab exactly where it was and is reported
    /// as <see cref="DesktopChartCloseStatus.PersistFailed"/> instead of thrown,
    /// so a failed close can neither lose the tab's recovery entry nor escape to
    /// an unhandled UI exception.
    /// </summary>
    public async Task<DesktopChartCloseResult> CloseTabAsync(
        Guid tabId,
        bool discardUnsavedChanges,
        CancellationToken cancellationToken = default)
    {
        var snapshots = CaptureDocumentSnapshots();
        DesktopChartTab? closed = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            closed = tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            if (closed == null)
                return new DesktopChartCloseResult(DesktopChartCloseStatus.NotOpen, null);
            if (closed.Session.IsDirty && !discardUnsavedChanges)
                return new DesktopChartCloseResult(DesktopChartCloseStatus.HasUnsavedChanges, null);

            var index = tabs.IndexOf(closed);
            var previousActiveTab = ActiveTab;
            tabs.RemoveAt(index);
            if (ReferenceEquals(ActiveTab, closed))
                ActiveTab = tabs.Count == 0 ? null : tabs[Math.Clamp(index, 0, tabs.Count - 1)];
            try
            {
                await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // The published tab list was never changed, so restoring the
                // internal list and active tab fully rolls the close back.
                tabs.Insert(index, closed);
                ActiveTab = previousActiveTab;
                if (exception is OperationCanceledException) throw;
                return new DesktopChartCloseResult(DesktopChartCloseStatus.PersistFailed, exception);
            }

            closed.Session.Changed -= Session_Changed;
            PublishTabs();
            TryDeleteDocumentCache(closed.CacheDirectory);
        }
        finally
        {
            gate.Release();
        }

        PublishChanged(DesktopWorkspaceChangeKind.Tabs, ActiveTab ?? closed);
        return new DesktopChartCloseResult(DesktopChartCloseStatus.Closed, null);
    }

    public async Task PersistAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = CaptureDocumentSnapshots();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task PersistNavigationStateAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistCoreAsync(
                    NoDocumentSnapshots,
                    cancellationToken,
                    writeWorkingCopies: false)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Every document cache directory this workspace may still need: open tabs,
    /// entries suspended by an explicit-file launch, and the manifest itself.
    /// When the manifest could not be verified, the whole cache root is returned
    /// because unknown entries may still reference any of it.
    /// </summary>
    public IReadOnlyList<string> GetProtectedCacheDirectories()
    {
        var protectedPaths = new List<string> { manifestPath, manifestLockPath, Path.Combine(editingRoot, "unverified") };
        if (Volatile.Read(ref manifestWriteBlocked))
            protectedPaths.Add(documentCacheRoot);
        foreach (var tab in Tabs)
            protectedPaths.Add(Path.GetFullPath(tab.CacheDirectory));
        foreach (var item in Volatile.Read(ref suspendedSnapshot))
        {
            try
            {
                var sourcePath = TryNormalizeManifestSourcePath(item.SourcePath);
                if (sourcePath != null)
                    protectedPaths.Add(ResolveCacheDirectory(item.CacheDirectoryName, sourcePath));
                else if (!string.IsNullOrWhiteSpace(item.CacheDirectoryName))
                    protectedPaths.Add(Path.Combine(documentCacheRoot, item.CacheDirectoryName));
            }
            catch (Exception exception) when (
                exception is InvalidDataException or NotSupportedException or ArgumentException or IOException)
            {
                // An unsafe or unrestorable entry cannot name a directory inside
                // the cache root, so there is nothing of it left to protect.
            }
        }
        return protectedPaths
            .Distinct(PathComparer)
            .ToArray();
    }

    /// <summary>
    /// Runs managed-storage cleanup inside the workspace gate. The protected set
    /// is taken inside the gate and no tab can be opened, restored or retargeted
    /// until cleanup finishes, so a cache in use can never be deleted.
    /// </summary>
    public async Task<ManagedStorageCleanupResult> RunCacheCleanupAsync(
        IEnumerable<ManagedStorageCleanupTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var requested = targets.ToArray();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var protectedPaths = GetProtectedCacheDirectories();
            var guarded = requested
                .Select(target => target with
                {
                    ProtectedPaths = (target.ProtectedPaths ?? [])
                        .Concat(protectedPaths)
                        .ToArray()
                })
                .ToArray();
            return await Task.Run(() => ManagedStorageCleanupService.Clean(guarded), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Absolute source paths of open and suspended tabs. Retention passes use
    /// this to keep every materialized document a tab may still restore.
    /// </summary>
    public IReadOnlyList<string> GetReferencedSourcePaths()
    {
        var paths = new List<string>();
        foreach (var tab in Tabs) paths.Add(tab.SourcePath);
        foreach (var item in Volatile.Read(ref suspendedSnapshot))
        {
            var sourcePath = TryNormalizeManifestSourcePath(item.SourcePath);
            if (sourcePath != null) paths.Add(sourcePath);
        }
        return paths.Distinct(PathComparer).ToArray();
    }

    /// <summary>
    /// True when the persisted manifest could not be read and has not yet been
    /// verified or quarantined; its entries are then unknown to this session.
    /// </summary>
    public bool HasUnverifiedManifest => Volatile.Read(ref manifestWriteBlocked);

    /// <summary>
    /// Persists the current committed working copy as a WPF-compatible retained
    /// room draft without overwriting its materialized source file. Unlike ordinary
    /// workspace persistence, this explicitly advances the trusted working-copy
    /// fingerprint so a subsequent launch restores the final room authority instead
    /// of replacing it with the older source snapshot.
    /// </summary>
    public async Task<bool> PreserveRoomDraftAsync(
        Guid tabId,
        long roomNumber,
        string? roomName,
        DateTimeOffset endedUtc,
        CancellationToken cancellationToken = default)
    {
        var requestedTab = Tabs.FirstOrDefault(candidate => candidate.Id == tabId);
        var snapshots = CaptureDocumentSnapshots();
        DesktopChartTab? tab = null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            tab = tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            if (tab == null) return false;

            var (_, snapshot) = ResolveRequestedSnapshot(
                tab,
                requestedTab,
                requestedChangeSerial: 0,
                snapshots);
            tab.WorkingSha256 = await WriteWorkingCopyAsync(
                    snapshot.Document,
                    tab.WorkingPath,
                    cancellationToken)
                .ConfigureAwait(false);
            tab.PersistedSnapshotTicket = Math.Max(tab.PersistedSnapshotTicket, snapshot.Ticket);
            tab.IsPreservedRoomDraft = true;
            tab.PreservedRoomNumber = Math.Max(0, roomNumber);
            tab.PreservedRoomName = roomName?.Trim() ?? string.Empty;
            tab.PreservedRoomEndedUtc = endedUtc.UtcDateTime;
            await PersistCoreAsync(snapshots, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
            if (tab != null) PublishChanged(DesktopWorkspaceChangeKind.Document, tab);
        }
    }

    private async Task<DesktopChartTab?> RestoreTabAsync(
        DesktopWorkspaceTabManifest item,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (item.Id == Guid.Empty) throw new InvalidDataException("A workspace tab has an empty ID.");
        var format = GetFormat(sourcePath);
        if (!string.Equals(item.SourceFormat, FormatName(format), StringComparison.OrdinalIgnoreCase))
            recoveryWarnings.Add($"{sourcePath}: source format was repaired from the file extension.");
        var osuTimingImportMode = ParseOsuTimingImportMode(item.OsuTimingImportMode);
        var cacheDirectory = ResolveCacheDirectory(item.CacheDirectoryName, sourcePath);
        var workingPath = Path.Combine(cacheDirectory, WorkingChartFileName);
        var sourceExists = File.Exists(sourcePath);
        var workingExists = File.Exists(workingPath);

        string? currentSourceHash = null;
        Exception? sourceFingerprintError = null;
        if (sourceExists)
        {
            try
            {
                currentSourceHash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                sourceFingerprintError = exception;
            }
        }

        string? currentWorkingHash = null;
        if (workingExists)
        {
            try
            {
                currentWorkingHash = await ComputeSha256Async(workingPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                recoveryWarnings.Add($"{sourcePath}: cached working copy could not be fingerprinted ({exception.Message}).");
            }
        }

        var sourceMatchesManifest = sourceExists && currentSourceHash != null &&
                                    !string.IsNullOrWhiteSpace(item.SourceSha256) &&
                                    string.Equals(
                                        currentSourceHash,
                                        item.SourceSha256,
                                        StringComparison.OrdinalIgnoreCase);
        var workingMatchesManifest = workingExists && currentWorkingHash != null &&
                                     !string.IsNullOrWhiteSpace(item.WorkingSha256) &&
                                     string.Equals(
                                         currentWorkingHash,
                                         item.WorkingSha256,
                                         StringComparison.OrdinalIgnoreCase);

        ChartDocument? document = null;
        Exception? workingCacheError = null;
        var dirty = false;
        var requiresRecoverySaveAs = item.RequiresRecoverySaveAs;
        var preservedRoomDraft = false;
        if (item.IsPreservedRoomDraft && workingExists)
        {
            try
            {
                document = await LoadWorkingCopyAsync(workingPath, cancellationToken).ConfigureAwait(false);
                preservedRoomDraft = true;
                dirty = true;
                if (!workingMatchesManifest)
                {
                    recoveryWarnings.Add(
                        $"{sourcePath}: retained room draft changed after its last trusted fingerprint; " +
                        "the valid working copy was recovered as dirty.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                workingCacheError = exception;
            }
        }

        if (document == null && workingMatchesManifest && (!sourceExists || sourceMatchesManifest || item.IsDirty))
        {
            try
            {
                document = await LoadWorkingCopyAsync(workingPath, cancellationToken).ConfigureAwait(false);
                preservedRoomDraft = item.IsPreservedRoomDraft;
                dirty = preservedRoomDraft || item.IsDirty;
                if (sourceExists && !sourceMatchesManifest)
                    recoveryWarnings.Add($"{sourcePath}: source changed externally; unsaved edits were recovered separately. Save As or reload before replacing the source.");
                if (!sourceExists)
                {
                    dirty = true;
                    recoveryWarnings.Add($"{sourcePath}: source is missing; the recoverable working copy was retained.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                workingCacheError = exception;
            }
        }

        // Older builds wrote dirty checkpoints without updating their hash.
        // Recover differing valid edits, but require review through Save As.
        if (document == null && workingExists && !workingMatchesManifest && item.IsDirty)
        {
            try
            {
                document = await LoadWorkingCopyAsync(workingPath, cancellationToken).ConfigureAwait(false);
                dirty = true;
                requiresRecoverySaveAs = true;
                recoveryWarnings.Add($"{sourcePath}: 工作副本与旧清单校验不一致，已保留编辑内容；请核对后另存为新文件。源谱面未覆盖。");
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { workingCacheError = exception; }
        }

        var sourceChanged = sourceExists && currentSourceHash != null && !sourceMatchesManifest;
        Exception? sourceLoadError = sourceFingerprintError;
        if (document == null && sourceExists)
        {
            try
            {
                document = await LoadSourceAsync(
                    sourcePath,
                    format,
                    osuTimingImportMode,
                    cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(cacheDirectory);
                await DesktopChartAssetLocalizer.LocalizeAsync(
                    document,
                    sourcePath,
                    workingPath,
                    cancellationToken).ConfigureAwait(false);
                document.FilePath = workingPath;
                if (workingExists && !workingMatchesManifest)
                {
                    var retainedRoot = Path.Combine(editingRoot, "unverified");
                    Directory.CreateDirectory(retainedRoot);
                    File.Copy(workingPath, Path.Combine(retainedRoot,
                        Path.GetFileName(cacheDirectory) + "-" + Guid.NewGuid().ToString("N") + ".mc"));
                }
                currentWorkingHash = await WriteWorkingCopyAsync(document, workingPath, cancellationToken)
                    .ConfigureAwait(false);
                currentSourceHash ??= await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
                dirty = false;
                if (sourceChanged)
                    recoveryWarnings.Add($"{sourcePath}: source changed; the source replaced the old cache.");
                else if (workingExists && !workingMatchesManifest)
                    recoveryWarnings.Add($"{sourcePath}: unsafe or invalid cache was replaced from source.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                sourceLoadError = exception;
                document = null;
            }
        }

        if (document == null && workingExists)
        {
            try
            {
                document = await LoadWorkingCopyAsync(workingPath, cancellationToken).ConfigureAwait(false);
                dirty = true;
                preservedRoomDraft = item.IsPreservedRoomDraft;
                var detail = sourceExists
                    ? sourceLoadError?.Message ?? "source was unreadable"
                    : "source is missing";
                recoveryWarnings.Add($"{sourcePath}: {detail}; the recoverable working copy was retained.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                workingCacheError = exception;
                document = null;
            }
        }

        if (document == null)
        {
            if (!sourceExists)
                throw workingCacheError ?? new FileNotFoundException(
                    "The source and recoverable working copy are both unavailable.",
                    sourcePath);
            throw sourceLoadError ?? workingCacheError ??
                new InvalidDataException("The source and recoverable working copy could not be restored.");
        }

        document.FilePath = workingPath;
        var session = new EditorSession();
        session.ReplaceDocument(document, markDirty: dirty || !sourceExists);
        var tab = new DesktopChartTab(
            item.Id,
            session,
            sourcePath,
            cacheDirectory,
            workingPath,
            format,
            osuTimingImportMode)
        {
            RequiresRecoverySaveAs = requiresRecoverySaveAs,
            CurrentBeat = double.IsFinite(item.CurrentBeat)
                ? OsuSourceEnvelope.HasSource(session.Document) ? item.CurrentBeat : Math.Max(0, item.CurrentBeat) : 0,
            SourceSha256 = dirty && sourceChanged ? item.SourceSha256 : currentSourceHash ?? item.SourceSha256,
            WorkingSha256 = currentWorkingHash ?? item.WorkingSha256,
            SourceMissing = !sourceExists,
            IsPreservedRoomDraft = preservedRoomDraft,
            PreservedRoomNumber = preservedRoomDraft ? Math.Max(0, item.PreservedRoomNumber) : 0,
            PreservedRoomName = preservedRoomDraft ? item.PreservedRoomName?.Trim() ?? string.Empty : string.Empty,
            PreservedRoomEndedUtc = preservedRoomDraft ? item.PreservedRoomEndedUtc : null
        };
        tab.ReplaceNavigationMarkers(ReadNavigationMarkers(item.NavigationMarkers));
        return tab;
    }

    /// <summary>
    /// Reads the manifest without ever letting a failed read turn into an empty
    /// session that the next persist would silently write over. Transient I/O
    /// errors are retried; unparsable or unsupported content is quarantined
    /// beside the manifest; a manifest that stays unreadable blocks manifest
    /// writes until it can be verified or quarantined.
    /// </summary>
    private async Task<DesktopWorkspaceManifest?> TryReadManifestAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref manifestWriteBlocked, false);
        if (!File.Exists(manifestPath)) return null;

        // Reading must not depend on the lock: a read-only editing root or a
        // stuck peer would otherwise turn into an empty session.
        FileStream? manifestLock = null;
        try
        {
            manifestLock = await AcquireManifestLockAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        await using var heldManifestLock = manifestLock;
        byte[]? bytes;
        try
        {
            bytes = await ReadManifestBytesWithRetryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref manifestWriteBlocked, true);
            recoveryWarnings.Add(
                $"Workspace manifest could not be read and will not be overwritten: {exception.Message}");
            return null;
        }
        if (bytes == null) return null;

        if (TryParseManifest(bytes, out var manifest, out var parseError)) return manifest;
        if (!TryQuarantineManifest(out var quarantined))
        {
            Volatile.Write(ref manifestWriteBlocked, true);
            recoveryWarnings.Add(
                $"Workspace manifest is damaged and will not be overwritten: {parseError}");
        }
        else
        {
            recoveryWarnings.Add(
                $"Workspace manifest was damaged and was kept as {Path.GetFileName(quarantined)}: {parseError}");
        }
        return null;
    }

    private async Task<byte[]?> ReadManifestBytesWithRetryAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var source = new FileStream(
                    manifestPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    IoBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                return buffer.ToArray();
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < ManifestReadAttempts)
            {
                // Sharing violations from antivirus or backup tools are usually
                // gone within a moment; an empty session must not win that race.
                await Task.Delay(ManifestRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool TryParseManifest(
        byte[] bytes,
        out DesktopWorkspaceManifest? manifest,
        out string error)
    {
        manifest = null;
        error = string.Empty;
        try
        {
            // Parsing a byte span, unlike the stream reader used before, does not
            // skip a UTF-8 byte order mark; without this a manifest saved with one
            // would be treated as corrupt and quarantined.
            var json = bytes.AsSpan(bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0);
            var parsed = JsonSerializer.Deserialize<DesktopWorkspaceManifest>(json, ManifestJsonOptions);
            if (parsed == null || parsed.SchemaVersion != ManifestSchemaVersion)
                throw new InvalidDataException("The desktop workspace manifest schema is unsupported.");
            manifest = parsed;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Moves an unusable manifest aside as <c>workspace.json.corrupt-&lt;UTC time&gt;</c>
    /// so the next write cannot destroy it. Returns false when the manifest had
    /// to be left in place; the caller then blocks manifest writes.
    /// </summary>
    private bool TryQuarantineManifest(out string quarantinePath)
    {
        quarantinePath = string.Empty;
        var stamp = timeProvider.GetUtcNow().UtcDateTime.ToString(
            "yyyyMMdd-HHmmss-fff",
            System.Globalization.CultureInfo.InvariantCulture);
        for (var suffix = 0; suffix < 100; suffix++)
        {
            var candidate = suffix == 0
                ? $"{manifestPath}.corrupt-{stamp}"
                : $"{manifestPath}.corrupt-{stamp}-{suffix}";
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            try
            {
                File.Move(manifestPath, candidate);
                quarantinePath = candidate;
                return true;
            }
            catch (FileNotFoundException)
            {
                // Nothing is left at the manifest path that a write could destroy.
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(candidate)) continue;
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Re-checks a manifest that could not be verified at startup. A manifest
    /// that has become readable keeps its entries as suspended tabs so they are
    /// merged into the next write instead of being dropped; damaged content is
    /// quarantined. Called with the manifest lock held.
    /// </summary>
    private async Task<bool> TryUnblockManifestWritesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath)) return true;
        byte[]? bytes;
        try
        {
            bytes = await ReadManifestBytesWithRetryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (bytes == null) return true;

        if (TryParseManifest(bytes, out var manifest, out _))
        {
            var openSources = tabs.Select(tab => tab.SourcePath).ToHashSet(PathComparer);
            var knownIds = tabs.Select(tab => tab.Id)
                .Concat(suspendedTabs.Select(item => item.Id))
                .ToHashSet();
            foreach (var item in (manifest!.Tabs ?? []).OrderBy(item => item.Order))
            {
                var source = TryNormalizeManifestSourcePath(item.SourcePath);
                if (!knownIds.Add(item.Id) || (source != null && openSources.Contains(source)))
                    continue;
                suspendedTabs.Add(item);
            }
            suspendedActiveTabId ??= manifest.ActiveTabId;
            PublishSuspendedTabs();
            recoveryWarnings.Add(
                "Workspace manifest became readable; its previous tabs were kept for the next launch.");
            return true;
        }

        if (!TryQuarantineManifest(out var quarantined)) return false;
        recoveryWarnings.Add(
            $"Workspace manifest was damaged and was kept as {Path.GetFileName(quarantined)}.");
        return true;
    }

    /// <summary>
    /// Cross-process lock for manifest reads and writes. The single-instance
    /// guard is per session and user, so another client process (a second
    /// profile, an evidence run, or a different Windows user of a shared cache)
    /// can otherwise interleave with this read-modify-write.
    /// </summary>
    private async Task<FileStream> AcquireManifestLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(editingRoot);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(
                    manifestLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.None);
            }
            catch (IOException) when (attempt < ManifestLockAttempts)
            {
                await Task.Delay(ManifestRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PersistCoreAsync(
        IReadOnlyDictionary<DesktopChartTab, DocumentSnapshot> snapshots,
        CancellationToken cancellationToken,
        bool writeWorkingCopies = true)
    {
        Directory.CreateDirectory(editingRoot);
        if (writeWorkingCopies)
        {
            foreach (var tab in tabs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Only snapshots captured on the caller's thread are written. A
                // tab without one was opened or restored after the caller's
                // request, and its working copy already matches its document.
                if (!snapshots.TryGetValue(tab, out var snapshot) ||
                    snapshot.Ticket <= tab.PersistedSnapshotTicket)
                    continue;
                var workingSha256 = await WriteWorkingCopyAsync(
                    snapshot.Document,
                    tab.WorkingPath,
                    cancellationToken).ConfigureAwait(false);
                tab.PersistedSnapshotTicket = snapshot.Ticket;
                tab.WorkingSha256 = workingSha256;
            }
        }

        await using var manifestLock = await AcquireManifestLockAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref manifestWriteBlocked))
        {
            if (!await TryUnblockManifestWritesAsync(cancellationToken).ConfigureAwait(false))
            {
                // Working copies above are still current; only the manifest that
                // could not be verified is left untouched.
                if (!manifestWriteBlockedWarningAdded)
                {
                    manifestWriteBlockedWarningAdded = true;
                    recoveryWarnings.Add(
                        "Workspace manifest is still unreadable; this session's tab list is not being saved.");
                }
                return;
            }
            Volatile.Write(ref manifestWriteBlocked, false);
        }

        // Built after the unblock above so entries recovered from a manifest
        // that has become readable are part of this very write.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(CreateManifest(), ManifestJsonOptions);
        await WriteBytesAtomicAsync(manifestPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private DesktopWorkspaceManifest CreateManifest()
    {
        var activeItems = tabs
            .Select((tab, index) => CreateManifestItem(tab, index))
            .ToArray();
        var activeSources = activeItems
            .Select(item => TryNormalizeManifestSourcePath(item.SourcePath))
            .Where(path => path != null)
            .Cast<string>()
            .ToHashSet(PathComparer);
        var activeCacheNames = activeItems
            .Select(item => item.CacheDirectoryName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preservedItems = suspendedTabs
            .OrderBy(item => item.Order)
            .Where(item =>
            {
                var source = TryNormalizeManifestSourcePath(item.SourcePath);
                return (source == null || !activeSources.Contains(source)) &&
                       (string.IsNullOrWhiteSpace(item.CacheDirectoryName) ||
                        !activeCacheNames.Contains(item.CacheDirectoryName));
            })
            .Select(CloneManifestItem)
            .ToArray();
        var manifestItems = preservedItems
            .Concat(activeItems)
            .Select((item, index) =>
            {
                item.Order = index;
                return item;
            })
            .ToList();
        return new DesktopWorkspaceManifest
        {
            ActiveTabId = ActiveTab?.Id ??
                          (manifestItems.Any(item => item.Id == suspendedActiveTabId)
                              ? suspendedActiveTabId
                              : manifestItems.FirstOrDefault()?.Id),
            Tabs = manifestItems
        };
    }

    private DesktopWorkspaceTabManifest CreateManifestItem(
        DesktopChartTab tab,
        int order) =>
        new()
        {
            Id = tab.Id,
            Order = order,
            SourcePath = tab.SourcePath,
            CacheDirectoryName = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(tab.CacheDirectory)),
            SourceFormat = FormatName(tab.Format),
            OsuTimingImportMode = tab.OsuTimingImportMode.ToString(),
            SourceSha256 = tab.SourceSha256,
            WorkingSha256 = tab.WorkingSha256,
            IsDirty = tab.Session.IsDirty,
            RequiresRecoverySaveAs = tab.RequiresRecoverySaveAs,
            IsPreservedRoomDraft = tab.IsPreservedRoomDraft,
            PreservedRoomNumber = tab.PreservedRoomNumber,
            PreservedRoomName = tab.PreservedRoomName,
            PreservedRoomEndedUtc = tab.PreservedRoomEndedUtc,
            CurrentBeat = tab.CurrentBeat,
            NavigationMarkers = tab.NavigationMarkers
                .Select(marker => new DesktopNavigationMarkerManifest
                {
                    Id = marker.Id,
                    Bar = marker.Beat.Bar,
                    Numerator = marker.Beat.Numerator,
                    Denominator = marker.Beat.Denominator
                })
                .ToList(),
            LastOpenedUtc = timeProvider.GetUtcNow().UtcDateTime
        };

    private static DesktopWorkspaceTabManifest CloneManifestItem(
        DesktopWorkspaceTabManifest item) =>
        new()
        {
            Id = item.Id,
            Order = item.Order,
            SourcePath = item.SourcePath,
            CacheDirectoryName = item.CacheDirectoryName,
            SourceFormat = item.SourceFormat,
            OsuTimingImportMode = item.OsuTimingImportMode,
            SourceSha256 = item.SourceSha256,
            WorkingSha256 = item.WorkingSha256,
            RequiresRecoverySaveAs = item.RequiresRecoverySaveAs,
            IsDirty = item.IsDirty,
            IsPreservedRoomDraft = item.IsPreservedRoomDraft,
            PreservedRoomNumber = item.PreservedRoomNumber,
            PreservedRoomName = item.PreservedRoomName,
            PreservedRoomEndedUtc = item.PreservedRoomEndedUtc,
            CurrentBeat = item.CurrentBeat,
            NavigationMarkers = item.NavigationMarkers?
                .Select(marker => new DesktopNavigationMarkerManifest
                {
                    Id = marker.Id,
                    Bar = marker.Bar,
                    Numerator = marker.Numerator,
                    Denominator = marker.Denominator
                })
                .ToList() ?? [],
            LastOpenedUtc = item.LastOpenedUtc
        };

    private static IReadOnlyList<DesktopNavigationMarker> ReadNavigationMarkers(
        IEnumerable<DesktopNavigationMarkerManifest>? persisted)
    {
        if (persisted == null) return [];
        var markers = new List<DesktopNavigationMarker>();
        var beats = new HashSet<BeatPosition>();
        foreach (var item in persisted)
        {
            if (item.Id == Guid.Empty ||
                item.Denominator is <= 0 or > BeatPosition.MaximumPreservedDenominator)
                continue;
            try
            {
                var beat = BeatPosition.Preserve(item.Bar, item.Numerator, item.Denominator);
                if (beat.Value < 0 || !beats.Add(beat)) continue;
                markers.Add(new DesktopNavigationMarker(item.Id, beat));
            }
            catch (ArgumentOutOfRangeException) { }
            catch (OverflowException) { }
        }
        return markers;
    }

    private static bool ManifestSourcePathEquals(string candidate, string sourcePath)
    {
        var normalized = TryNormalizeManifestSourcePath(candidate);
        return normalized != null && PathComparer.Equals(normalized, sourcePath);
    }

    private static string? TryNormalizeManifestSourcePath(string candidate)
    {
        try
        {
            return string.IsNullOrWhiteSpace(candidate)
                ? null
                : Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private async Task<ChartDocument> LoadSourceAsync(
        string sourcePath,
        DesktopChartFormat format,
        OsuNoteTimingImportMode osuTimingImportMode,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            IoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await ChartDocumentImporter.ImportAsync(
            source,
            format == DesktopChartFormat.MalodyMc ? ChartImportFormat.MalodyMc : ChartImportFormat.OsuMania,
            defaultCreator,
            osuTimingImportMode,
            cancellationToken).ConfigureAwait(false);
        document.FilePath = sourcePath;
        return document;
    }

    private async Task<ChartDocument> LoadWorkingCopyAsync(
        string workingPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            workingPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            IoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await ChartDocumentImporter.ImportAsync(
            source,
            ChartImportFormat.MalodyMc,
            defaultCreator,
            cancellationToken).ConfigureAwait(false);
        document.FilePath = workingPath;
        return document;
    }

    private static async Task<string> WriteWorkingCopyAsync(
        ChartDocument document,
        string workingPath,
        CancellationToken cancellationToken)
    {
        var snapshot = document.Clone();
        snapshot.FilePath = workingPath;
        var bytes = Utf8WithoutBom.GetBytes(MalodyMcSerializer.Serialize(snapshot));
        await WriteBytesAtomicAsync(workingPath, bytes, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async Task WriteSourceSnapshotAtomicAsync(
        ChartDocument document,
        string destination,
        DesktopChartFormat format,
        CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("The chart source has no directory.");
        Directory.CreateDirectory(directory);

        var output = document.Clone();
        await DesktopChartAssetLocalizer.LocalizeAsync(
            output,
            document.FilePath,
            destination,
            cancellationToken).ConfigureAwait(false);
        output.FilePath = destination;
        // A short fixed-length name: embedding the chart's own file name made
        // the temporary component exceed the 255-character limit for charts
        // whose names were already close to it, so every save failed.
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.luma-part");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (format == DesktopChartFormat.MalodyMc)
            {
                var bytes = Utf8WithoutBom.GetBytes(MalodyMcSerializer.Serialize(output));
                await WriteNewBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                OsuManiaSerializer.Save(
                    output,
                    temporary,
                    output.Metadata.OverallDifficulty,
                    output.Metadata.HpDrainRate);
                await using var stream = new FileStream(
                    temporary,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    IoBufferSize,
                    FileOptions.WriteThrough);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (format == DesktopChartFormat.OsuMania && File.Exists(destination))
                File.Copy(destination, destination + ".sme-backup", overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async Task WriteBytesAtomicAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("The atomic destination has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteNewBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async Task WriteNewBytesAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            IoBufferSize,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private (string Folder, string ChartPath) AllocateNewChartLocation(string? suggestedName)
    {
        Directory.CreateDirectory(libraryRoot);
        var now = timeProvider.GetLocalNow();
        var timestamp = now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var stem = CreateSafeBaseName(suggestedName);
        var preferredFolder = Path.Combine(SafeCreationRoot(), $"{stem}_{timestamp}");
        var folder = AllocateUniqueDirectory(preferredFolder);
        Directory.CreateDirectory(folder);
        var chartPath = Path.Combine(folder, $"{now.ToUnixTimeSeconds()}.mc");
        return (folder, chartPath);
    }

    private (string Folder, string ChartPath, string Name) AllocateNamedAudioChartLocation(
        string? requestedName)
    {
        Directory.CreateDirectory(libraryRoot);
        var now = timeProvider.GetLocalNow();
        var timestamp = now.ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var stem = string.IsNullOrWhiteSpace(requestedName)
            ? timestamp
            : CreateNamedAudioSafeBaseName(requestedName);
        if (stem.Length > 80) stem = stem[..80].Trim();
        if (string.IsNullOrWhiteSpace(stem)) stem = timestamp;

        var folder = Path.Combine(SafeCreationRoot(), stem);
        if (Directory.Exists(folder) || File.Exists(folder))
        {
            for (var suffix = 1; suffix < int.MaxValue; suffix++)
            {
                var candidateName = $"{stem}_{suffix}";
                var candidate = Path.Combine(SafeCreationRoot(), candidateName);
                if (Directory.Exists(candidate) || File.Exists(candidate)) continue;
                stem = candidateName;
                folder = candidate;
                break;
            }
        }

        Directory.CreateDirectory(folder);
        var chartPath = Path.Combine(folder, $"{timestamp}.mc");
        if (File.Exists(chartPath))
        {
            for (var suffix = 1; suffix < int.MaxValue; suffix++)
            {
                var candidate = Path.Combine(folder, $"{timestamp}_{suffix}.mc");
                if (File.Exists(candidate)) continue;
                chartPath = candidate;
                break;
            }
        }
        return (folder, chartPath, stem);
    }

    private static string CreateNamedAudioSafeBaseName(string value)
    {
        value = value.Trim();
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');
        value = value.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "resource" : value;
    }

    private static string AllocateUniqueDirectory(string preferredPath)
    {
        if (!Directory.Exists(preferredPath) && !File.Exists(preferredPath)) return preferredPath;
        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{preferredPath}_{suffix}";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to allocate a unique new-chart directory.");
    }

    private string GetCacheDirectory(string sourcePath)
    {
        sourcePath = NormalizeSourcePath(sourcePath, requireExisting: false);
        var stem = CreateCacheStem(Path.GetFileNameWithoutExtension(sourcePath));
        var pathHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant()))).ToLowerInvariant();
        return Path.Combine(documentCacheRoot, $"{stem}_{pathHash[..16]}");
    }

    private string AllocateCacheDirectory(string sourcePath)
    {
        sourcePath = NormalizeSourcePath(sourcePath, requireExisting: false);
        var preferred = GetCacheDirectory(sourcePath);
        if (!Directory.Exists(preferred) && !File.Exists(preferred)) return preferred;

        var stem = CreateCacheStem(Path.GetFileNameWithoutExtension(sourcePath));
        var pathHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant()))).ToLowerInvariant();
        var fullHashCandidate = Path.Combine(documentCacheRoot, $"{stem}_{pathHash}");
        if (!Directory.Exists(fullHashCandidate) && !File.Exists(fullHashCandidate))
            return fullHashCandidate;

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{fullHashCandidate}_{suffix}";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to allocate an independent desktop chart cache directory.");
    }

    private string ResolveCacheDirectory(string? cacheDirectoryName, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectoryName)) return GetCacheDirectory(sourcePath);
        if (Path.IsPathRooted(cacheDirectoryName) ||
            cacheDirectoryName is "." or ".." ||
            cacheDirectoryName.Contains(':') ||
            cacheDirectoryName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            cacheDirectoryName.IndexOf(Path.AltDirectorySeparatorChar) >= 0 ||
            cacheDirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            cacheDirectoryName.Any(char.IsControl))
            throw new InvalidDataException("The desktop workspace cache directory name is unsafe.");

        var candidate = Path.GetFullPath(Path.Combine(documentCacheRoot, cacheDirectoryName));
        if (!IsInside(documentCacheRoot, candidate) ||
            !PathComparer.Equals(Path.GetDirectoryName(candidate), documentCacheRoot))
            throw new InvalidDataException("The desktop workspace cache directory escaped its root.");
        return candidate;
    }

    private static string CreateCacheStem(string? value)
    {
        var result = CreateSafeBaseName(value);
        if (result.Length > 48) result = result[..48].Trim();
        return string.IsNullOrWhiteSpace(result) ? "chart" : result;
    }

    private static string CreateSafeBaseName(string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "New Chart" : value.Trim();
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        value = value.Trim().TrimEnd('.');
        if (value.Length > MaximumSuggestedNameCharacters)
            value = value[..MaximumSuggestedNameCharacters].Trim().TrimEnd('.');
        if (value.Length > 0 && char.IsHighSurrogate(value[^1])) value = value[..^1];
        return string.IsNullOrWhiteSpace(value) ? "New Chart" : value;
    }

    private static string CleanSingleLine(string value) =>
        string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static bool IsCommunityAscii(string value) =>
        value.All(character => character is >= ' ' and <= '~');

    private string SafeCreationRoot()
    {
        if (!OsuSongsWorkspacePolicy.IsSongsPath(libraryRoot)) return libraryRoot;
        var fallback = creationRootFallback?.Invoke();
        return !string.IsNullOrWhiteSpace(fallback) && !OsuSongsWorkspacePolicy.IsSongsPath(fallback)
            ? fallback : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimpleManiaEditor Maps");
    }

    private static (string Requested, string Resolved) PrepareLibraryRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(requested);
        var resolution = LocalLibraryRootResolver.Resolve(requested);
        return (resolution.RequestedRoot, resolution.ResolvedRoot);
    }

    private static string NormalizeSourcePath(string sourcePath, bool requireExisting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        _ = GetFormat(fullPath);
        if (requireExisting && !File.Exists(fullPath))
            throw new FileNotFoundException("The chart source does not exist.", fullPath);
        return fullPath;
    }

    private static bool IsSupportedSourcePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".osu", StringComparison.OrdinalIgnoreCase);
    }

    private static DesktopChartFormat GetFormat(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".mc", StringComparison.OrdinalIgnoreCase)) return DesktopChartFormat.MalodyMc;
        if (extension.Equals(".osu", StringComparison.OrdinalIgnoreCase)) return DesktopChartFormat.OsuMania;
        throw new NotSupportedException("Only Malody .mc and osu!mania .osu sources are supported.");
    }

    private static string FormatName(DesktopChartFormat format) =>
        format == DesktopChartFormat.OsuMania ? "osu" : "mc";

    private static OsuNoteTimingImportMode ParseOsuTimingImportMode(string? value) =>
        Enum.TryParse<OsuNoteTimingImportMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : OsuNoteTimingImportMode.PreserveMilliseconds;

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            IoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
    }

    private static readonly IReadOnlyDictionary<DesktopChartTab, DocumentSnapshot> NoDocumentSnapshots =
        new Dictionary<DesktopChartTab, DocumentSnapshot>();

    /// <summary>
    /// A committed-document clone taken on the caller's thread. The ticket orders
    /// snapshots so an older one captured before a gate wait never overwrites a
    /// newer working copy written by an operation that acquired the gate first.
    /// </summary>
    private readonly record struct DocumentSnapshot(ChartDocument Document, long Ticket);

    private Dictionary<DesktopChartTab, DocumentSnapshot> CaptureDocumentSnapshots()
    {
        var openTabs = Tabs;
        var snapshots = new Dictionary<DesktopChartTab, DocumentSnapshot>(openTabs.Count);
        foreach (var tab in openTabs) snapshots[tab] = CaptureDocumentSnapshot(tab);
        return snapshots;
    }

    private DocumentSnapshot CaptureDocumentSnapshot(DesktopChartTab tab)
    {
        var ticket = Interlocked.Increment(ref documentSnapshotTicket);
        return new DocumentSnapshot(tab.Session.CaptureCommittedDocument(), ticket);
    }

    /// <summary>
    /// Returns the entry snapshot for the tab a save was requested for. A tab
    /// that was opened only after the request (a rare race) is captured now.
    /// </summary>
    private (long ChangeSerial, DocumentSnapshot Snapshot) ResolveRequestedSnapshot(
        DesktopChartTab tab,
        DesktopChartTab? requestedTab,
        long requestedChangeSerial,
        Dictionary<DesktopChartTab, DocumentSnapshot> snapshots)
    {
        if (ReferenceEquals(tab, requestedTab) && snapshots.TryGetValue(tab, out var snapshot))
            return (requestedChangeSerial, snapshot);
        var changeSerial = tab.Session.ChangeSerial;
        snapshot = CaptureDocumentSnapshot(tab);
        snapshots[tab] = snapshot;
        return (changeSerial, snapshot);
    }

    private void PublishTabs() => Volatile.Write(ref tabSnapshot, tabs.ToArray());

    private void PublishSuspendedTabs() =>
        Volatile.Write(ref suspendedSnapshot, suspendedTabs.ToArray());

    private static bool IsUnavailableLibraryRootError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or
            ArgumentException or System.Security.SecurityException;

    private void AttachTab(DesktopChartTab tab) => tab.Session.Changed += Session_Changed;

    private void DetachAllTabs()
    {
        foreach (var tab in tabs) tab.Session.Changed -= Session_Changed;
    }

    private void Session_Changed(object? sender, EventArgs eventArgs)
    {
        var tab = Tabs.FirstOrDefault(candidate => ReferenceEquals(candidate.Session, sender));
        if (tab != null) PublishChanged(DesktopWorkspaceChangeKind.Document, tab);
    }

    private void PublishChanged(DesktopWorkspaceChangeKind kind, DesktopChartTab? tab) =>
        Changed?.Invoke(this, new DesktopWorkspaceChangedEventArgs(kind, tab));

    private void TryDeleteDocumentCache(string cacheDirectory)
    {
        var target = Path.GetFullPath(cacheDirectory);
        if (!IsInside(documentCacheRoot, target) || PathComparer.Equals(target, documentCacheRoot)) return;
        if (!Directory.Exists(target)) return;
        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch (IOException exception)
        {
            recoveryWarnings.Add($"Closed-tab cache cleanup deferred: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            recoveryWarnings.Add($"Closed-tab cache cleanup deferred: {exception.Message}");
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch
        {
            // Preserve the primary create/write error.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the primary atomic-write error.
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class DesktopWorkspaceManifest
    {
        public int SchemaVersion { get; init; } = ManifestSchemaVersion;
        public Guid? ActiveTabId { get; set; }
        public List<DesktopWorkspaceTabManifest>? Tabs { get; set; } = [];
    }

    private sealed class DesktopWorkspaceTabManifest
    {
        public Guid Id { get; set; }
        public int Order { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string? CacheDirectoryName { get; set; }
        public string SourceFormat { get; set; } = "mc";
        public string OsuTimingImportMode { get; set; } = nameof(Core.IO.OsuNoteTimingImportMode.PreserveMilliseconds);
        public string SourceSha256 { get; set; } = string.Empty;
        public string WorkingSha256 { get; set; } = string.Empty;
        public bool IsDirty { get; set; }
        public bool RequiresRecoverySaveAs { get; set; }
        public bool IsPreservedRoomDraft { get; set; }
        public long PreservedRoomNumber { get; set; }
        public string PreservedRoomName { get; set; } = string.Empty;
        public DateTime? PreservedRoomEndedUtc { get; set; }
        public double CurrentBeat { get; set; }
        public List<DesktopNavigationMarkerManifest>? NavigationMarkers { get; set; }
        public DateTime LastOpenedUtc { get; set; }
    }

    private sealed class DesktopNavigationMarkerManifest
    {
        public Guid Id { get; set; }
        public int Bar { get; set; }
        public int Numerator { get; set; }
        public int Denominator { get; set; }
    }
}

public sealed class DesktopChartTab
{
    internal DesktopChartTab(
        Guid id,
        EditorSession session,
        string sourcePath,
        string cacheDirectory,
        string workingPath,
        DesktopChartFormat format,
        OsuNoteTimingImportMode osuTimingImportMode)
    {
        Id = id;
        Session = session;
        SourcePath = sourcePath;
        CacheDirectory = cacheDirectory;
        WorkingPath = workingPath;
        Format = format;
        OsuTimingImportMode = osuTimingImportMode;
    }

    public Guid Id { get; }
    public EditorSession Session { get; }
    public string SourcePath { get; internal set; }
    public string CacheDirectory { get; }
    public string WorkingPath { get; }
    public DesktopChartFormat Format { get; internal set; }
    public OsuNoteTimingImportMode OsuTimingImportMode { get; internal set; }
    public double CurrentBeat { get; internal set; }
    public bool RequiresRecoverySaveAs { get; internal set; }
    // Replaced as a whole, never mutated in place: the UI thread replaces the
    // markers while a gated persist may be serializing them on the thread pool.
    private IReadOnlyList<DesktopNavigationMarker> navigationMarkers = [];

    public IReadOnlyList<DesktopNavigationMarker> NavigationMarkers => Volatile.Read(ref navigationMarkers);

    /// <summary>
    /// Ticket of the newest document snapshot written to <see cref="WorkingPath"/>.
    /// </summary>
    internal long PersistedSnapshotTicket { get; set; }

    internal void ReplaceNavigationMarkers(IEnumerable<DesktopNavigationMarker> markers)
    {
        var next = new List<DesktopNavigationMarker>();
        var seenBeats = new HashSet<BeatPosition>();
        foreach (var marker in markers
                     .Where(candidate => candidate.Id != Guid.Empty)
                     .Where(candidate => candidate.Beat.Denominator is > 0 and <= BeatPosition.MaximumPreservedDenominator)
                     .Where(candidate => candidate.Beat.Value >= 0 && double.IsFinite(candidate.Beat.Value))
                     .OrderBy(candidate => candidate.Beat)
                     .ThenBy(candidate => candidate.Id))
        {
            if (!seenBeats.Add(marker.Beat)) continue;
            next.Add(marker);
            if (next.Count == DesktopNavigationMarker.MaximumCount) break;
        }
        Volatile.Write(ref navigationMarkers, next.ToArray());
    }

    /// <summary>
    /// Per-tab editor view state matching the Windows/WPF Group Manager. It is
    /// intentionally absent from the workspace manifest and chart serialization:
    /// switching tabs retains it for the current process, while reopening starts
    /// from the ungrouped placement view.
    /// </summary>
    public int ActivePlacementGroup { get; set; }
    public HashSet<int> HiddenGroups { get; } = [];
    public bool SourceMissing { get; internal set; }
    public bool IsPreservedRoomDraft { get; internal set; }
    public long PreservedRoomNumber { get; internal set; }
    public string PreservedRoomName { get; internal set; } = string.Empty;
    public DateTime? PreservedRoomEndedUtc { get; internal set; }
    internal string SourceSha256 { get; set; } = string.Empty;
    internal string WorkingSha256 { get; set; } = string.Empty;

    public string DisplayTitle
    {
        get
        {
            var title = Session.Document.Metadata.Title;
            return string.IsNullOrWhiteSpace(title) || title.Equals("Untitled", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(SourcePath)
                : title;
        }
    }

    public string FormatBadge => Format == DesktopChartFormat.OsuMania ? "OSU!MANIA" : "MALODY";
}

public sealed record DesktopChartSaveResult(
    DesktopChartSaveStatus Status,
    IReadOnlyList<ChartIssue> Issues)
{
    public bool Saved => Status == DesktopChartSaveStatus.Saved;
}

public sealed record DesktopChartCloseResult(
    DesktopChartCloseStatus Status,
    Exception? Error)
{
    public bool Closed => Status == DesktopChartCloseStatus.Closed;
}

public enum DesktopChartCloseStatus
{
    Closed,
    NotOpen,
    HasUnsavedChanges,
    PersistFailed
}

/// <summary>
/// Describes a session that started on a fallback library root because the
/// requested root could not be prepared. The requested root is intentionally
/// not replaced in settings.
/// </summary>
public sealed record DesktopLibraryRootFallback(
    string RequestedRoot,
    string FallbackRoot,
    string Reason);

public sealed record DesktopNamedAudioChartCreation(
    DesktopChartTab Tab,
    string Name);

public sealed record DesktopWorkspaceChangedEventArgs(
    DesktopWorkspaceChangeKind Kind,
    DesktopChartTab? Tab);

public enum DesktopChartFormat
{
    MalodyMc,
    OsuMania
}

public enum DesktopChartSaveStatus
{
    Saved,
    ValidationFailed
}

public enum DesktopWorkspaceChangeKind
{
    Reset,
    Tabs,
    ActiveTab,
    Document,
    LibraryRoot
}

internal static class DesktopChartAssetLocalizer
{
    private const int IoBufferSize = 128 * 1024;

    public static async Task LocalizeAsync(
        ChartDocument document,
        string? originalChartPath,
        string targetChartPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        targetChartPath = Path.GetFullPath(targetChartPath);
        var targetFolder = Path.GetDirectoryName(targetChartPath)
                           ?? throw new InvalidOperationException("The target chart path has no directory.");
        Directory.CreateDirectory(targetFolder);
        var localized = new Dictionary<string, string>(PathComparer);

        async Task<string> LocalizeReferenceAsync(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
            var source = ResolveReference(reference, originalChartPath);
            // An unresolved reference (missing, or pointing outside the chart
            // folder) is kept verbatim. Rewriting it to a bare file name would
            // silently repoint a chart that was only temporarily incomplete.
            if (source == null) return reference;
            if (localized.TryGetValue(source, out var existing)) return existing;
            if (IsInside(targetFolder, source))
            {
                var relative = ToChartRelative(targetFolder, source);
                localized[source] = relative;
                return relative;
            }

            // Keep the reference's folder structure (for example "audio/song.ogg")
            // instead of flattening it into the target folder's root.
            var originalFolder = Path.GetDirectoryName(Path.GetFullPath(originalChartPath!))!;
            var preferredRelative = SafeRelativePath(Path.GetRelativePath(originalFolder, source));
            var destination = await SelectDestinationAsync(
                targetFolder,
                preferredRelative,
                source,
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(destination))
                await CopyAtomicAsync(source, destination, cancellationToken).ConfigureAwait(false);
            var result = ToChartRelative(targetFolder, destination);
            localized[source] = result;
            return result;
        }

        document.Metadata.AudioFile = await LocalizeReferenceAsync(document.Metadata.AudioFile).ConfigureAwait(false);
        document.Metadata.CoverFile = await LocalizeReferenceAsync(document.Metadata.CoverFile).ConfigureAwait(false);
        document.Metadata.BackgroundFile = await LocalizeReferenceAsync(document.Metadata.BackgroundFile)
            .ConfigureAwait(false);
        foreach (var note in document.Notes.Where(note => !string.IsNullOrWhiteSpace(note.Sound)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            note.Sound = await LocalizeReferenceAsync(note.Sound!).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves a chart resource reference strictly inside the chart's own
    /// folder. Rooted, UNC, drive-relative, stream (":") and ".." references
    /// are rejected before any file-system access: probing them would read
    /// arbitrary files into the editing cache (and from there into room
    /// uploads) or open SMB connections that leak NTLM credentials.
    /// </summary>
    internal static string? ResolveReference(string reference, string? chartPath)
    {
        if (string.IsNullOrWhiteSpace(chartPath) ||
            !TryGetSafeRelativeReference(reference, out var relative))
            return null;
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(chartPath));
            if (string.IsNullOrEmpty(folder)) return null;
            var candidate = Path.GetFullPath(Path.Combine(folder, relative));
            if (!IsInside(folder, candidate)) return null;
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Accepts only a plain chart-relative path. The check is purely lexical so
    /// that rejected references never reach the file system.
    /// </summary>
    internal static bool TryGetSafeRelativeReference(string? reference, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var normalized = reference.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            Path.IsPathRooted(reference) ||
            normalized.Contains(':') ||
            normalized.Any(char.IsControl))
            return false;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.Trim() == "..")) return false;
        segments = segments.Where(segment => segment != ".").ToArray();
        if (segments.Length == 0) return false;
        relative = string.Join(Path.DirectorySeparatorChar, segments);
        return true;
    }

    private static async Task<string> SelectDestinationAsync(
        string targetFolder,
        string preferredRelativePath,
        string source,
        CancellationToken cancellationToken)
    {
        var preferredName = Path.GetFileName(preferredRelativePath);
        var subfolder = Path.GetDirectoryName(preferredRelativePath) ?? string.Empty;
        var destinationFolder = Path.Combine(targetFolder, subfolder);
        // A file occupying one of the folder names cannot be replaced by a
        // directory; fall back to the target root rather than failing the save.
        if (subfolder.Length > 0 && HasFileAlongPath(targetFolder, subfolder))
            destinationFolder = targetFolder;
        var stem = Path.GetFileNameWithoutExtension(preferredName);
        var extension = Path.GetExtension(preferredName);
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = suffix == 1 ? preferredName : $"{stem}_{suffix}{extension}";
            var candidate = Path.Combine(destinationFolder, fileName);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            if (File.Exists(candidate) &&
                await FilesEqualAsync(source, candidate, cancellationToken).ConfigureAwait(false))
                return candidate;
        }
        throw new IOException("Unable to allocate a unique localized asset name.");
    }

    private static async Task<bool> FilesEqualAsync(
        string left,
        string right,
        CancellationToken cancellationToken)
    {
        try
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length) return false;
            await using var leftStream = new FileStream(
                left,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var rightStream = new FileStream(
                right,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var leftHash = await SHA256.HashDataAsync(leftStream, cancellationToken).ConfigureAwait(false);
            var rightHash = await SHA256.HashDataAsync(rightStream, cancellationToken).ConfigureAwait(false);
            return leftHash.AsSpan().SequenceEqual(rightHash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task CopyAtomicAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(destination)
                     ?? throw new InvalidOperationException("The localized asset has no destination directory.");
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.luma-part");
        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite,
                             IoBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             IoBufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, IoBufferSize, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Preserve the primary copy error.
            }
        }
    }

    private static string SafeFileName(string reference)
    {
        string value;
        try
        {
            value = Path.GetFileName(reference);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            value = string.Empty;
        }
        if (string.IsNullOrWhiteSpace(value)) value = "resource";
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        value = value.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "resource" : value;
    }

    /// <summary>
    /// Sanitizes each segment of a relative path that is already known to stay
    /// inside its chart folder.
    /// </summary>
    private static string SafeRelativePath(string relativePath)
    {
        var segments = relativePath
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(SafeFileName)
            .ToArray();
        return segments.Length == 0
            ? "resource"
            : Path.Combine(segments);
    }

    private static bool HasFileAlongPath(string root, string relativeFolder)
    {
        var current = root;
        foreach (var segment in relativeFolder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current)) return true;
        }
        return false;
    }

    private static string ToChartRelative(string folder, string path) =>
        Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

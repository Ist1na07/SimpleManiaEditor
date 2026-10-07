using System.Globalization;
using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

/// <summary>
/// Regression specs for the workspace, library and managed-storage fixes:
/// fail-closed library deletion, resource references confined to the chart
/// folder, manifest quarantine, close rollback, cache-cleanup protection,
/// library-root fallback, bounded retention and invariant file-name dates.
/// </summary>
internal static class WorkspaceFixSpecs
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lcws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            VerifyDeletionPlanner(Path.Combine(root, "deletion"));
            await VerifyResourceReferencesStayInsideChartFolderAsync(Path.Combine(root, "resources"));
            await VerifyLongChartFileNameSavesAsync(Path.Combine(root, "long-name"));
            await VerifyDamagedManifestIsQuarantinedAsync(Path.Combine(root, "damaged-manifest"));
            await VerifyUnreadableManifestIsNeverOverwrittenAsync(Path.Combine(root, "unreadable-manifest"));
            await VerifyFailedCloseRollsBackAsync(Path.Combine(root, "close-rollback"));
            await VerifyCleanupProtectsSuspendedCachesAsync(Path.Combine(root, "cleanup"));
            VerifyLibraryRootFallback(Path.Combine(root, "fallback"));
            VerifyBackupRetentionAndInvariantNames(Path.Combine(root, "backups"));
            VerifyManagedStorageRetention(Path.Combine(root, "retention"));
            await VerifyLibraryScanIgnoresOutsideCoversAsync(Path.Combine(root, "scan"));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyDeletionPlanner(string root)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(profile) && !string.IsNullOrWhiteSpace(desktop) &&
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(desktop)) is { } desktopParent &&
            string.Equals(
                Path.TrimEndingDirectorySeparator(desktopParent),
                Path.TrimEndingDirectorySeparator(profile),
                StringComparison.OrdinalIgnoreCase))
        {
            var desktopCard = Folder(desktop, Path.Combine(desktop, "x.mc"));
            var plan = DesktopLibraryDeletionPlanner.Create(profile, [desktopCard]);
            Assert(!plan.CanExecute &&
                   plan.BlockReason == DesktopLibraryDeletionBlockReason.ProtectedFolder &&
                   plan.BlockMessage == DesktopLibraryDeletionPlanner.ProtectedFolderMessage,
                "a library rooted at the user profile could recycle the whole Desktop as a song folder");
        }
        var profileParent = string.IsNullOrWhiteSpace(profile) ? null : Path.GetDirectoryName(profile);
        Assert(profileParent == null || DesktopLibraryDeletionPlanner.IsProtectedFolder(profileParent),
            "a folder containing the user profile was not protected from library deletion");
        Assert(DesktopLibraryDeletionPlanner.IsProtectedFolder(Path.GetPathRoot(Path.GetTempPath())!),
            "a volume root was not protected from library deletion");

        var library = Path.Combine(root, "library");
        var pack = Path.Combine(library, "Pack");
        var nested = Path.Combine(pack, "Nested");
        var grouped = Path.Combine(library, "_song_7");
        var solo = Path.Combine(library, "Solo");
        foreach (var folder in new[] { nested, Path.Combine(grouped, "0"), Path.Combine(grouped, "1"), solo })
            Directory.CreateDirectory(folder);
        foreach (var chart in new[]
                 {
                     Path.Combine(pack, "a.mc"), Path.Combine(nested, "b.mc"),
                     Path.Combine(grouped, "0", "c.mc"), Path.Combine(grouped, "1", "d.osu"),
                     Path.Combine(solo, "s.mc"), Path.Combine(solo, "unindexed-broken.mc")
                 })
            File.WriteAllText(chart, "{}");

        var packCard = Folder(pack, Path.Combine(pack, "a.mc"));
        var nestedCard = Folder(nested, Path.Combine(nested, "b.mc"));
        var packOnly = DesktopLibraryDeletionPlanner.Create(library, [packCard]);
        Assert(!packOnly.CanExecute &&
               packOnly.BlockReason == DesktopLibraryDeletionBlockReason.ContainsOtherChartFolders,
            "deleting a card whose folder also holds another song folder was not refused");
        var packAndNested = DesktopLibraryDeletionPlanner.Create(library, [packCard, nestedCard]);
        Assert(packAndNested.CanExecute && packAndNested.Targets.Count == 1 && packAndNested.ChartCount == 2,
            "selecting both the parent and the nested card did not count every removed chart");

        var groupedCard = Folder(grouped, Path.Combine(grouped, "0", "c.mc"), Path.Combine(grouped, "1", "d.osu"));
        var groupedPlan = DesktopLibraryDeletionPlanner.Create(library, [groupedCard]);
        Assert(groupedPlan.CanExecute && groupedPlan.ChartCount == 2,
            "a Malody _song_ group was refused or miscounted");

        var soloCard = Folder(solo, Path.Combine(solo, "s.mc"));
        var soloPlan = DesktopLibraryDeletionPlanner.Create(library, [soloCard], permanentDeletion: false);
        Assert(soloPlan.CanExecute && soloPlan.ChartCount == 2 && !soloPlan.PermanentDeletion &&
               soloPlan.ConfirmationMessage!.Contains("回收站", StringComparison.Ordinal),
            "the confirmation did not count the unindexed chart that the delete removes");
        var permanent = DesktopLibraryDeletionPlanner.Create(library, [soloCard], permanentDeletion: true);
        Assert(permanent.PermanentDeletion &&
               permanent.ConfirmationMessage!.Contains("永久删除", StringComparison.Ordinal) &&
               !permanent.ConfirmationMessage.Contains("回收站", StringComparison.Ordinal) &&
               DesktopLibraryDeletionPlanner.GetChoiceLabel(true) ==
               DesktopLibraryDeletionPlanner.PermanentDeleteChoiceLabel,
            "a delete without a recycle bin still promised a recycle-bin move");
        Assert(DesktopLibraryDeletionPlanner.DefaultPermanentDeletion == !OperatingSystem.IsWindows(),
            "the default deletion wording does not follow recycle-bin availability");

        var chartEntry = new DesktopLibraryChartEntry
        {
            ChartPath = Path.Combine(solo, "s.mc"),
            FolderPath = solo,
            RelativePath = Path.Combine("Solo", "s.mc")
        };
        var chartPlan = DesktopLibraryChartDeletionPlanner.Create(library, chartEntry, permanentDeletion: true);
        Assert(chartPlan.CanExecute && chartPlan.PermanentDeletion &&
               chartPlan.ConfirmationMessage!.Contains("永久删除", StringComparison.Ordinal) &&
               !chartPlan.ConfirmationMessage.Contains("回收站", StringComparison.Ordinal),
            "a single-chart delete without a recycle bin still promised a recycle-bin move");
    }

    private static async Task VerifyResourceReferencesStayInsideChartFolderAsync(string root)
    {
        var library = Path.Combine(root, "library");
        var song = Path.Combine(library, "Song");
        Directory.CreateDirectory(Path.Combine(song, "sub"));
        File.WriteAllBytes(Path.Combine(song, "sub", "a.ogg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(library, "outside.png"), [4, 5, 6]);
        var absolute = Path.Combine(root, "absolute.png");
        File.WriteAllBytes(absolute, [7, 8, 9]);
        var sourcePath = Path.Combine(song, "chart.mc");
        var document = CreateDocument("References");
        document.Metadata.AudioFile = "sub/a.ogg";
        document.Metadata.CoverFile = "../outside.png";
        document.Metadata.BackgroundFile = absolute;
        document.Notes[0].Sound = "missing/b.wav";
        MalodyMcSerializer.Save(document, sourcePath);

        var workspace = new DesktopDocumentWorkspace(library, Path.Combine(root, "editing"), "Workspace Fix Spec");
        await workspace.InitializeAsync();
        var tab = await workspace.OpenAsync(sourcePath);
        var working = tab.Session.Document;
        var cacheFiles = Directory.EnumerateFiles(tab.CacheDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(tab.CacheDirectory, path).Replace('\\', '/'))
            .ToArray();
        Assert(working.Metadata.AudioFile == "sub/a.ogg" &&
               cacheFiles.Contains("sub/a.ogg") && !cacheFiles.Contains("a.ogg"),
            "a subfolder resource reference was flattened while localizing into the cache");
        Assert(working.Notes[0].Sound == "missing/b.wav" &&
               !cacheFiles.Contains("outside.png") && !cacheFiles.Contains("absolute.png") &&
               !cacheFiles.Contains("b.wav"),
            "an out-of-folder, absolute or missing reference was copied or rewritten");

        // Import may already clear unsafe references; an edit can still set
        // one, and saving must neither resolve nor copy it.
        tab.Session.Mutate(next =>
        {
            next.Metadata.Version = "Saved";
            next.Metadata.CoverFile = "../outside.png";
            next.Metadata.BackgroundFile = absolute;
        });
        var saved = await workspace.SaveAsync(tab.Id);
        var sourceText = File.ReadAllText(sourcePath);
        var reloaded = MalodyMcSerializer.Load(sourcePath);
        Assert(saved.Saved &&
               reloaded.Metadata.AudioFile == "sub/a.ogg" &&
               !File.Exists(Path.Combine(song, "a.ogg")) &&
               !File.Exists(Path.Combine(song, "outside.png")) &&
               !File.Exists(Path.Combine(song, "absolute.png")) &&
               !sourceText.Contains("\"absolute.png\"", StringComparison.Ordinal) &&
               !sourceText.Contains("\"outside.png\"", StringComparison.Ordinal),
            "saving back to the source flattened, resolved or copied resource references");
    }

    private static async Task VerifyLongChartFileNameSavesAsync(string root)
    {
        var library = Path.Combine(root, "library");
        Directory.CreateDirectory(library);
        var sourcePath = Path.Combine(library, new string('n', 230) + ".mc");
        MalodyMcSerializer.Save(CreateDocument("Long Name"), sourcePath);
        var workspace = new DesktopDocumentWorkspace(library, Path.Combine(root, "editing"), "Workspace Fix Spec");
        await workspace.InitializeAsync();
        var tab = await workspace.OpenAsync(sourcePath);
        tab.Session.Mutate(next => next.Metadata.Version = "Long");
        var result = await workspace.SaveAsync(tab.Id);
        Assert(result.Saved && MalodyMcSerializer.Load(sourcePath).Metadata.Version == "Long" &&
               !Directory.EnumerateFiles(library, "*.luma-part").Any(),
            "a chart whose file name is close to the 255-character limit could not be saved");
    }

    private static async Task VerifyDamagedManifestIsQuarantinedAsync(string root)
    {
        var library = Path.Combine(root, "library");
        var editing = Path.Combine(root, "editing");
        Directory.CreateDirectory(library);
        var manifestPath = Path.Combine(editing, "desktop-workspace", "workspace.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        const string damaged = "{\"schemaVersion\":1,\"tabs\":[\"unterminated";
        File.WriteAllText(manifestPath, damaged);

        var workspace = new DesktopDocumentWorkspace(library, editing, "Workspace Fix Spec");
        await workspace.InitializeAsync();
        var quarantined = Directory.GetFiles(Path.GetDirectoryName(manifestPath)!, "workspace.json.corrupt-*");
        Assert(workspace.RecoveryWarnings.Count == 1 && quarantined.Length == 1 &&
               File.ReadAllText(quarantined[0]) == damaged && !workspace.HasUnverifiedManifest,
            "a damaged manifest was not moved aside before the session continued");

        var sourcePath = Path.Combine(library, "after.mc");
        MalodyMcSerializer.Save(CreateDocument("After"), sourcePath);
        await workspace.OpenAsync(sourcePath);
        Assert(File.Exists(manifestPath) && File.ReadAllText(quarantined[0]) == damaged,
            "the quarantined manifest was overwritten by the next persist");
    }

    private static async Task VerifyUnreadableManifestIsNeverOverwrittenAsync(string root)
    {
        var library = Path.Combine(root, "library");
        var editing = Path.Combine(root, "editing");
        Directory.CreateDirectory(library);
        var firstSource = Path.Combine(library, "first.mc");
        var secondSource = Path.Combine(library, "second.mc");
        MalodyMcSerializer.Save(CreateDocument("First"), firstSource);
        MalodyMcSerializer.Save(CreateDocument("Second"), secondSource);

        var original = new DesktopDocumentWorkspace(library, editing, "Workspace Fix Spec");
        await original.InitializeAsync();
        await original.OpenAsync(firstSource);
        var manifestPath = original.ManifestPath;

        DesktopDocumentWorkspace blocked;
        await using (new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            blocked = new DesktopDocumentWorkspace(library, editing, "Workspace Fix Spec");
            await blocked.InitializeAsync();
        }
        Assert(blocked.HasUnverifiedManifest && blocked.Tabs.Count == 0 &&
               blocked.RecoveryWarnings.Count == 1,
            "an unreadable manifest was not reported and write-protected");
        Assert(blocked.GetProtectedCacheDirectories().Contains(
                   Path.GetFullPath(Path.Combine(editing, "desktop-workspace", "documents")),
                   StringComparer.OrdinalIgnoreCase),
            "cache cleanup was not fully blocked while the manifest's entries were unknown");

        // Readable again: its entries must be merged, not overwritten.
        await blocked.OpenAsync(secondSource);
        var restarted = new DesktopDocumentWorkspace(library, editing, "Workspace Fix Spec");
        await restarted.InitializeAsync();
        var restoredSources = restarted.Tabs.Select(tab => Path.GetFileName(tab.SourcePath)).ToArray();
        Assert(!blocked.HasUnverifiedManifest &&
               restoredSources.Contains("first.mc") && restoredSources.Contains("second.mc"),
            "the previous session's tabs were lost when the manifest became readable again");
    }

    private static async Task VerifyFailedCloseRollsBackAsync(string root)
    {
        var library = Path.Combine(root, "library");
        Directory.CreateDirectory(library);
        var firstSource = Path.Combine(library, "first.mc");
        var secondSource = Path.Combine(library, "second.mc");
        MalodyMcSerializer.Save(CreateDocument("First"), firstSource);
        MalodyMcSerializer.Save(CreateDocument("Second"), secondSource);
        var workspace = new DesktopDocumentWorkspace(library, Path.Combine(root, "editing"), "Workspace Fix Spec");
        await workspace.InitializeAsync();
        var first = await workspace.OpenAsync(firstSource);
        var tabsBeforeOpen = workspace.Tabs;
        var second = await workspace.OpenAsync(secondSource);
        Assert(tabsBeforeOpen.Count == 1 && workspace.Tabs.Count == 2,
            "the published tab list was not an immutable snapshot");
        Assert(!workspace.UpdateViewState(Guid.NewGuid(), 4) &&
               !workspace.ReplaceNavigationMarkers(Guid.NewGuid(), []) &&
               !workspace.Activate(Guid.NewGuid()),
            "a view-state update for a closed tab threw or reported success");

        DesktopChartCloseResult failed;
        await using (new FileStream(workspace.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = await workspace.CloseTabAsync(second.Id, discardUnsavedChanges: false);
        }
        Assert(failed.Status == DesktopChartCloseStatus.PersistFailed && failed.Error != null &&
               workspace.Tabs.Count == 2 && workspace.Tabs.Contains(second) &&
               ReferenceEquals(workspace.ActiveTab, second) && Directory.Exists(second.CacheDirectory),
            "a close whose manifest write failed removed the tab or its recovery cache");

        var closed = await workspace.CloseTabAsync(second.Id, discardUnsavedChanges: false);
        Assert(closed.Closed && workspace.Tabs.Count == 1 && ReferenceEquals(workspace.ActiveTab, first) &&
               !Directory.Exists(second.CacheDirectory),
            "the tab could not be closed once the manifest was writable again");
    }

    private static async Task VerifyCleanupProtectsSuspendedCachesAsync(string root)
    {
        var library = Path.Combine(root, "library");
        var editing = Path.Combine(root, "editing");
        Directory.CreateDirectory(library);
        var suspendedSource = Path.Combine(library, "suspended.mc");
        var explicitSource = Path.Combine(library, "explicit.mc");
        MalodyMcSerializer.Save(CreateDocument("Suspended"), suspendedSource);
        MalodyMcSerializer.Save(CreateDocument("Explicit"), explicitSource);

        var previous = new DesktopDocumentWorkspace(library, editing, "Workspace Fix Spec");
        await previous.InitializeAsync();
        var suspendedTab = await previous.OpenAsync(suspendedSource);
        suspendedTab.Session.Mutate(next => next.Metadata.Version = "Unsaved");
        await previous.PersistAsync();

        var explicitLaunch = new DesktopDocumentWorkspace(library, editing, "Workspace Fix Spec");
        await explicitLaunch.InitializeForExplicitStartupAsync();
        var explicitTab = await explicitLaunch.OpenAsync(explicitSource);
        var stray = Path.Combine(explicitLaunch.DocumentCacheRoot, "stray_0000");
        Directory.CreateDirectory(stray);
        File.WriteAllBytes(Path.Combine(stray, "working.mc"), [1]);
        var protectedPaths = explicitLaunch.GetProtectedCacheDirectories();
        Assert(protectedPaths.Contains(Path.GetFullPath(suspendedTab.CacheDirectory), StringComparer.OrdinalIgnoreCase) &&
               protectedPaths.Contains(Path.GetFullPath(explicitTab.CacheDirectory), StringComparer.OrdinalIgnoreCase),
            "the protected cache set omitted a suspended or open tab");

        var result = await explicitLaunch.RunCacheCleanupAsync(
            [new ManagedStorageCleanupTarget(explicitLaunch.DocumentCacheRoot)]);
        Assert(!Directory.Exists(stray) && result.FilesDeleted == 1 &&
               File.Exists(suspendedTab.WorkingPath) && File.Exists(explicitTab.WorkingPath) &&
               MalodyMcSerializer.Load(suspendedTab.WorkingPath).Metadata.Version == "Unsaved",
            "cache cleanup deleted the suspended session's unsaved working copy");
    }

    private static void VerifyLibraryRootFallback(string root)
    {
        Directory.CreateDirectory(root);
        var unavailable = Path.Combine(root, "not-a-directory");
        File.WriteAllText(unavailable, "a file where the library root should be");
        var fallback = Path.Combine(root, "fallback-library");
        var editing = Path.Combine(root, "editing");
        var workspace = new DesktopDocumentWorkspace(
            unavailable,
            editing,
            "Workspace Fix Spec",
            fallbackLibraryRoot: () => fallback);
        Assert(workspace.LibraryRootFallback is { } notice &&
               notice.RequestedRoot == unavailable &&
               string.Equals(workspace.LibraryRoot, Path.GetFullPath(fallback), StringComparison.OrdinalIgnoreCase),
            "an unavailable library root was not replaced by the session fallback");

        var healthy = new DesktopDocumentWorkspace(
            fallback,
            editing,
            "Workspace Fix Spec",
            fallbackLibraryRoot: () => throw new InvalidOperationException("must not be evaluated"));
        Assert(healthy.LibraryRootFallback == null,
            "a reachable library root reported a fallback");

        var threw = false;
        try { _ = new DesktopDocumentWorkspace(unavailable, editing, "Workspace Fix Spec"); }
        catch (IOException) { threw = true; }
        Assert(threw, "without a fallback an unavailable root no longer failed closed");
    }

    private static void VerifyBackupRetentionAndInvariantNames(string root)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var document = CreateDocument("Retention");
            var start = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
            var paths = new List<string>();
            for (var index = 0; index < 5; index++)
            {
                paths.Add(ChartBackupArchiveService.CreateBackup(
                    root,
                    document,
                    Path.Combine(root, "source.mc"),
                    start.AddMinutes(index),
                    maximumBackupsPerChart: 3,
                    maximumTotalBytes: long.MaxValue));
                File.SetLastWriteTimeUtc(paths[^1], start.AddMinutes(index).UtcDateTime);
            }
            var remaining = Directory.GetFiles(Path.GetDirectoryName(paths[0])!, "*.mcbackup");
            Assert(remaining.Length == 3 && paths.Skip(2).All(File.Exists) && !File.Exists(paths[0]),
                "automatic backups were not limited to the newest per chart");
            Assert(paths.All(path => Path.GetFileName(path).StartsWith(
                       start.LocalDateTime.Year.ToString(CultureInfo.InvariantCulture) + "-",
                       StringComparison.Ordinal)),
                "a backup file name followed the Thai Buddhist calendar instead of the invariant one");

            var other = CreateDocument("Other");
            var otherPath = ChartBackupArchiveService.CreateBackup(
                root,
                other,
                Path.Combine(root, "other.mc"),
                start.AddHours(1),
                maximumBackupsPerChart: 30,
                maximumTotalBytes: new FileInfo(remaining[0]).Length);
            var total = Directory.GetFiles(root, "*.mcbackup", SearchOption.AllDirectories);
            Assert(File.Exists(otherPath) && total.Length < 4,
                "the total backup size cap removed the new backup or nothing at all");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static void VerifyManagedStorageRetention(string root)
    {
        var rooms = Path.Combine(root, "rooms");
        var now = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var expired = CreateAgedDirectory(Path.Combine(rooms, "room_expired"), now.AddDays(-60), 10);
        var inUse = CreateAgedDirectory(Path.Combine(rooms, "room_in_use"), now.AddDays(-60), 10);
        var recent = CreateAgedDirectory(Path.Combine(rooms, "room_recent"), now.AddHours(-2), 10);
        var retained = CreateAgedDirectory(Path.Combine(rooms, "retained"), now.AddDays(-400), 10);
        var result = ManagedStorageRetentionService.PruneDirectories(
            rooms,
            "room_",
            [Path.Combine(inUse, "room-source.mc")],
            now,
            TimeSpan.FromDays(30),
            long.MaxValue);
        Assert(result.DirectoriesDeleted == 1 && !Directory.Exists(expired) &&
               Directory.Exists(inUse) && Directory.Exists(recent) && Directory.Exists(retained),
            "room document retention removed an in-use, recent or retained folder, or kept an expired one");

        var older = CreateAgedDirectory(Path.Combine(rooms, "room_older"), now.AddDays(-10), 100);
        var newer = CreateAgedDirectory(Path.Combine(rooms, "room_newer"), now.AddDays(-5), 100);
        ManagedStorageRetentionService.PruneDirectories(
            rooms,
            "room_",
            [Path.Combine(inUse, "room-source.mc")],
            now,
            TimeSpan.FromDays(30),
            maximumTotalBytes: 150);
        Assert(!Directory.Exists(older) && Directory.Exists(newer) && Directory.Exists(inUse) &&
               Directory.Exists(recent),
            "the room document size cap did not remove only the oldest unprotected folder");

        var scratch = Path.Combine(root, "_scratch");
        var alive = CreateAgedDirectory(Path.Combine(scratch, "session_111"), now, 1);
        var dead = CreateAgedDirectory(Path.Combine(scratch, "session_222"), now, 1);
        var own = CreateAgedDirectory(Path.Combine(scratch, "session_333"), now, 1);
        var sibling = CreateAgedDirectory(Path.Combine(scratch, "session_222_sibling"), now, 1);
        var sessions = ManagedStorageRetentionService.CleanupOrphanedSessionDirectories(
            scratch,
            currentProcessId: 333,
            processId => processId == 111);
        Assert(sessions.DirectoriesDeleted == 1 && !Directory.Exists(dead) && Directory.Exists(alive) &&
               Directory.Exists(own) && Directory.Exists(sibling),
            "orphaned scratch cleanup removed a live, own or unrecognized session");
    }

    private static async Task VerifyLibraryScanIgnoresOutsideCoversAsync(string root)
    {
        var library = Path.Combine(root, "library");
        var outsideSong = Path.Combine(library, "OutsideCover");
        var insideSong = Path.Combine(library, "InsideCover");
        Directory.CreateDirectory(outsideSong);
        Directory.CreateDirectory(Path.Combine(insideSong, "img"));
        File.WriteAllBytes(Path.Combine(library, "secret.png"), [1]);
        File.WriteAllBytes(Path.Combine(insideSong, "img", "cover.png"), [2]);
        var outsideDocument = CreateDocument("Outside Cover");
        outsideDocument.Metadata.CoverFile = "../secret.png";
        MalodyMcSerializer.Save(outsideDocument, Path.Combine(outsideSong, "chart.mc"));
        var insideDocument = CreateDocument("Inside Cover");
        insideDocument.Metadata.CoverFile = "img/cover.png";
        MalodyMcSerializer.Save(insideDocument, Path.Combine(insideSong, "chart.mc"));

        var entries = await DesktopChartLibraryService.ScanAsync(library);
        var outsideEntry = entries.Single(entry => entry.Title == "Outside Cover");
        var insideEntry = entries.Single(entry => entry.Title == "Inside Cover");
        Assert(outsideEntry.CoverPath == null &&
               string.Equals(
                   insideEntry.CoverPath,
                   Path.GetFullPath(Path.Combine(insideSong, "img", "cover.png")),
                   StringComparison.OrdinalIgnoreCase),
            "the library scan resolved a cover outside the chart folder or lost one inside it");
    }

    private static string CreateAgedDirectory(string path, DateTimeOffset lastWrite, int bytes)
    {
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "payload.bin");
        File.WriteAllBytes(file, new byte[bytes]);
        File.SetLastWriteTimeUtc(file, lastWrite.UtcDateTime);
        Directory.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    private static DesktopLibraryFolderEntry Folder(string folderPath, params string[] chartPaths)
    {
        var charts = chartPaths
            .Select(chartPath => new DesktopLibraryChartEntry
            {
                ChartPath = chartPath,
                FolderPath = Path.GetDirectoryName(chartPath)!,
                RelativePath = Path.GetFileName(chartPath)
            })
            .ToArray();
        return new DesktopLibraryFolderEntry
        {
            FolderPath = folderPath,
            RelativeFolderPath = Path.GetFileName(folderPath),
            Charts = charts,
            DisplayTitle = Path.GetFileName(folderPath),
            DisplayArtist = "Spec Artist",
            CoverPath = string.Empty,
            AddedTime = DateTime.UnixEpoch,
            ModifiedTime = DateTime.UnixEpoch,
            SongCount = 1,
            Formats = "Malody",
            SearchText = string.Empty,
            FolderSearchText = string.Empty
        };
    }

    private static ChartDocument CreateDocument(string title) => new()
    {
        KeyCount = 4,
        Metadata = new ChartMetadata
        {
            Title = title,
            Artist = "Workspace Fix Artist",
            Creator = "Workspace Fix Creator",
            Version = "Normal"
        },
        Timing =
        {
            new TimingEvent
            {
                Beat = BeatPosition.Zero,
                Bpm = 120
            }
        },
        Notes =
        {
            new ChartNote
            {
                Lane = 0,
                Beat = BeatPosition.Zero
            }
        }
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

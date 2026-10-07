using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LumaChart.Application.Libraries;
using LumaChart.Application.Workspaces;
using LumaChart.Storage;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// The saved library root could not be used at startup, so this session runs
/// on <paramref name="FallbackRoot"/>. The saved setting is left unchanged.
/// </summary>
public sealed record WorkspaceRootFallbackNotice(
    string RequestedRoot,
    string FallbackRoot,
    string Reason);

public static class AvaloniaAppPaths
{
    private const string LocalDataOverride = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
    private const string EditingRootOverride = "LUMACHART_AVALONIA_EDITING_ROOT";
    private const string WorkspaceRootOverride = "LUMACHART_AVALONIA_WORKSPACE_ROOT";
    private const string LegacyEditingCacheFolderName = "LumaChart Editing Cache";
    private const int DefaultProGridColumns = 12;
    private const int MaximumProGridColumns = 64;
    private static IAppStorageLocations? registeredLocations;
    private static WorkspaceRootFallbackNotice? workspaceRootFallback;
    private static readonly object EditingRootMigrationGate = new();
    private static (string Legacy, string Target, string Chosen)? editingRootDecision;

    /// <summary>
    /// Test-client payloads are often launched by double-clicking the exe
    /// instead of using the generated cmd wrapper. When a payload carries its
    /// explicit client profile, derive the same isolated roots and single
    /// instance scope that the wrapper would have supplied. Normal installs do
    /// not contain client-profile.txt and remain unchanged.
    /// </summary>
    public static void ConfigureSiblingClientProfileEnvironment()
    {
        if (!OperatingSystem.IsWindows()) return;

        var profilePath = Path.Combine(AppContext.BaseDirectory, "client-profile.txt");
        if (!File.Exists(profilePath)) return;

        string profile;
        try
        {
            profile = File.ReadAllText(profilePath).Trim();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profile) ||
            !profile.StartsWith("Test-Client-", StringComparison.OrdinalIgnoreCase))
            return;

        var root = Path.GetFullPath(AppContext.BaseDirectory);
        SetEnvironmentIfMissing(LocalDataOverride, Path.Combine(root, "data", "local-data"));
        SetEnvironmentIfMissing(EditingRootOverride, Path.Combine(root, "data", "editing"));
        SetEnvironmentIfMissing(WorkspaceRootOverride, Path.Combine(root, "data", "workspace"));
        SetEnvironmentIfMissing(
            WindowsSingleInstancePolicy.ScopeEnvironment,
            $"avalonia-payload-{NormalizeProfile(profile)}");
    }

    private static void SetEnvironmentIfMissing(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static string NormalizeProfile(string profile)
    {
        var characters = profile
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        return new string(characters).Trim('-').ToLowerInvariant();
    }

    /// <summary>
    /// Host-owned roots win; otherwise the desktop defaults resolve from the
    /// current environment on every access. The root overrides are fixed at
    /// process startup in real clients, but the spec suite switches
    /// <c>LUMACHART_AVALONIA_LOCAL_DATA_ROOT</c> between isolated checks, so a
    /// cached lazy value would leak one check's root into every later one.
    /// </summary>
    public static IAppStorageLocations Locations =>
        Volatile.Read(ref registeredLocations) ?? CreateDefaultLocations();

    public static string LocalDataRoot => Locations.Settings;

    public static string PersistentEditingRoot => Locations.PersistentEditing;

    /// <summary>
    /// Process-owned transient work beneath the Avalonia editing cache. Export
    /// staging must not use the system temporary directory or the WPF client's
    /// scratch tree, and a second Windows client process must receive a distinct
    /// root.
    /// </summary>
    public static string SessionScratchRoot => Locations.SessionScratch;

    /// <summary>
    /// Installs the host-owned storage roots before the shared Avalonia visual
    /// tree is created. Android calls this from its Application constructor;
    /// Windows keeps the lazy desktop defaults.
    /// </summary>
    public static void RegisterLocations(IAppStorageLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ValidateLocation(locations.Settings, nameof(locations.Settings));
        ValidateLocation(locations.PersistentEditing, nameof(locations.PersistentEditing));
        ValidateLocation(locations.ResourceCache, nameof(locations.ResourceCache));
        ValidateLocation(locations.SessionScratch, nameof(locations.SessionScratch));
        Interlocked.Exchange(ref registeredLocations, locations);
    }

    /// <summary>
    /// Removes only this process' transient export tree during a normal client
    /// shutdown. Persistent document caches live outside <c>_scratch</c> and are
    /// deliberately retained for tab recovery.
    /// </summary>
    public static void CleanupSessionScratchRoot()
    {
        try
        {
            var sessionRoot = Path.GetFullPath(SessionScratchRoot);
            var expectedLeaf = $"session_{Environment.ProcessId}";
            var scratchRoot = Path.GetDirectoryName(sessionRoot);
            if (scratchRoot == null ||
                !Path.GetFileName(sessionRoot).Equals(expectedLeaf, StringComparison.Ordinal) ||
                !Path.GetFileName(scratchRoot).Equals("_scratch", StringComparison.Ordinal))
                return;

            if (Directory.Exists(sessionRoot)) Directory.Delete(sessionRoot, recursive: true);
        }
        catch
        {
            // Scratch cleanup must never replace the user's normal shutdown.
            // A later cache-management pass may reclaim an interrupted session.
        }
    }

    /// <summary>
    /// Reclaims <c>_scratch/session_&lt;pid&gt;</c> trees left by client processes
    /// that crashed or were killed before <see cref="CleanupSessionScratchRoot"/>
    /// ran. Only sessions whose process no longer exists are removed; this
    /// process' own session and any unrecognized name are left alone.
    /// </summary>
    public static ManagedStorageRetentionResult? CleanupOrphanedSessionScratchRoots()
    {
        try
        {
            var sessionRoot = Path.GetFullPath(SessionScratchRoot);
            var scratchRoot = Path.GetDirectoryName(sessionRoot);
            if (scratchRoot == null ||
                !Path.GetFileName(sessionRoot).Equals(
                    $"session_{Environment.ProcessId}",
                    StringComparison.Ordinal) ||
                !Path.GetFileName(scratchRoot).Equals("_scratch", StringComparison.Ordinal))
                return null;
            return ManagedStorageRetentionService.CleanupOrphanedSessionDirectories(
                scratchRoot,
                Environment.ProcessId,
                IsProcessRunning);
        }
        catch
        {
            // Best effort; it is retried on the next launch.
            return null;
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            // Another user's process or an access failure: keep its scratch.
            return true;
        }
    }

    public static string WorkspaceSettingsPath => Path.Combine(LocalDataRoot, "workspace.json");

    public static string EditorBackupRoot => Path.Combine(LocalDataRoot, "Backups");

    public static string DiagnosticLogRoot => Path.Combine(LocalDataRoot, "Logs");

    public static string DesktopDocumentCacheRoot => Path.Combine(
        PersistentEditingRoot,
        "desktop-workspace",
        "documents");

    public static string OnlineRoomResourceCacheRoot => Locations.ResourceCache;

    /// <summary>
    /// App-private copies of documents chosen through a platform picker that
    /// returns no readable file-system path. These are imported into a chart or
    /// library folder immediately, so the resource cache is the right home and
    /// old entries are prunable.
    /// </summary>
    public static string PickedFileStagingRoot => Path.Combine(
        Locations.ResourceCache,
        "_picked");

    /// <summary>
    /// Picked documents whose resolved path is persisted in settings, such as a
    /// custom Key-sound sample. Cache pruning would silently break the stored
    /// setting, so these live beside the other persistent editing state.
    /// </summary>
    public static string PickedFileDurableRoot => Path.Combine(
        PersistentEditingRoot,
        "picked-assets");

    public static string OnlineRoomDocumentRoot => Path.Combine(
        LocalDataRoot,
        "online-room-documents");

    /// <summary>
    /// Non-null when <see cref="ResolveInitialWorkspaceRoot"/> could not use the
    /// saved library root (unplugged drive, offline share, broken junction,
    /// read-only folder) and fell back to the default root for this session.
    /// </summary>
    public static WorkspaceRootFallbackNotice? WorkspaceRootFallback =>
        Volatile.Read(ref workspaceRootFallback);

    /// <summary>
    /// Records a fallback detected elsewhere (for example while constructing the
    /// workspace) so settings writes keep the saved root.
    /// </summary>
    public static void ReportWorkspaceRootFallback(
        string requestedRoot,
        string fallbackRoot,
        string reason) =>
        Volatile.Write(
            ref workspaceRootFallback,
            new WorkspaceRootFallbackNotice(requestedRoot, fallbackRoot, reason));

    /// <summary>
    /// Ends the fallback once the user explicitly chooses a library root.
    /// </summary>
    public static void ClearWorkspaceRootFallback() =>
        Volatile.Write(ref workspaceRootFallback, null);

    public static string ResolveInitialWorkspaceRoot()
    {
        if (Locations is IAppLibraryStorageLocation libraryLocation)
            return EnsureDirectory(libraryLocation.Library);

        var explicitRoot = Environment.GetEnvironmentVariable(WorkspaceRootOverride);
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return EnsureDirectory(explicitRoot);

        var ownSetting = ReadOwnWorkspaceSettings()?.WorkspaceRoot;
        if (!string.IsNullOrWhiteSpace(ownSetting))
        {
            try
            {
                var root = EnsureDirectory(ownSetting);
                // Also validate a root junction now: the workspace resolves it
                // during construction, where a failure used to abort startup.
                _ = LocalLibraryRootResolver.Resolve(root);
                return root;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                    ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                // Deliberately not written back: once the drive or share is
                // reachable again the next launch returns to the saved root.
                var fallback = ResolveDefaultWorkspaceRoot();
                ReportWorkspaceRootFallback(ownSetting, fallback, exception.Message);
                return fallback;
            }
        }

        return ResolveDefaultWorkspaceRoot();
    }

    /// <summary>
    /// The root used when no own setting exists: an existing WPF root, else a
    /// folder beside the application, else one under Documents.
    /// </summary>
    public static string ResolveDefaultWorkspaceRoot()
    {
        var wpfSetting = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumaChart Studio",
            "workspace.json");
        var existingWpfRoot = TryReadWorkspaceRoot(wpfSetting);
        if (!string.IsNullOrWhiteSpace(existingWpfRoot) && Directory.Exists(existingWpfRoot))
            return Path.GetFullPath(existingWpfRoot);

        var besideApplication = Path.Combine(AppContext.BaseDirectory, "LumaChart Workspace");
        try { return EnsureDirectory(besideApplication); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EnsureDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "LumaChart Workspace"));
        }
    }

    public static IReadOnlyList<string> EnumerateCompatibilityWorkspaceRoots()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var roots = new HashSet<string>(comparer);
        AddExistingRoot(roots, Environment.GetEnvironmentVariable(WorkspaceRootOverride));
        AddExistingRoot(roots, ReadOwnWorkspaceSettings()?.WorkspaceRoot);
        AddExistingRoot(
            roots,
            TryReadWorkspaceRoot(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LumaChart Studio",
                "workspace.json")));
        return roots.ToArray();
    }

    public static void SaveWorkspaceRoot(string root)
    {
        SaveWorkspacePreferences(root, LoadLibrarySort(), LoadProGridColumns());
    }

    public static DesktopLibrarySort LoadLibrarySort()
    {
        var text = ReadOwnWorkspaceSettings()?.LibrarySort;
        return Enum.TryParse<DesktopLibrarySort>(text, ignoreCase: false, out var sort) &&
               Enum.IsDefined(sort)
            ? sort
            : DesktopLibrarySort.AddedDescending;
    }

    public static int LoadProGridColumns()
    {
        var columns = ReadOwnWorkspaceSettings()?.ProGridColumns;
        return columns is { } value
            ? Math.Clamp(value, 1, MaximumProGridColumns)
            : DefaultProGridColumns;
    }

    public static void SaveWorkspacePreferences(string root, DesktopLibrarySort sort)
    {
        SaveWorkspacePreferences(root, sort, LoadProGridColumns());
    }

    public static void SaveProGridColumns(int columns)
    {
        columns = Math.Clamp(columns, 1, MaximumProGridColumns);
        // Without a saved root, keep it absent. Writing the resolved root here
        // would persist a session-only fallback or a default the user never chose.
        var root = ReadOwnWorkspaceSettings()?.WorkspaceRoot;
        SaveWorkspacePreferences(
            string.IsNullOrWhiteSpace(root) ? null : root,
            LoadLibrarySort(),
            columns);
    }

    private static void SaveWorkspacePreferences(
        string? root,
        DesktopLibrarySort sort,
        int proGridColumns)
    {
        root = PersistableWorkspaceRoot(root);
        proGridColumns = Math.Clamp(proGridColumns, 1, MaximumProGridColumns);
        // A settings file that exists but could not be read this session keeps
        // the user's saved root; it must not be replaced by session values.
        if (!WindowsSettingsFileGuard.CanWrite(WorkspaceSettingsPath)) return;
        Directory.CreateDirectory(LocalDataRoot);
        var temporary = Path.Combine(
            LocalDataRoot,
            $".{Path.GetFileName(WorkspaceSettingsPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new WorkspaceSettings(root, sort.ToString(), proGridColumns),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, WorkspaceSettingsPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Preserve the primary settings-write failure.
            }
        }
    }

    private static string ResolveRootOverride(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? fallback : value);
    }

    private static IAppStorageLocations CreateDefaultLocations()
    {
        var localDataRoot = ResolveRootOverride(
            LocalDataOverride,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LumaChart Studio",
                "Avalonia"));
        var persistentEditingRoot = ResolveDefaultPersistentEditingRoot(localDataRoot);
        return new WindowsAvaloniaAppStorageLocations(
            localDataRoot,
            persistentEditingRoot,
            Path.Combine(localDataRoot, "room-resource-cache"),
            Path.Combine(
                persistentEditingRoot,
                "_scratch",
                $"session_{Environment.ProcessId}"));
    }

    /// <summary>
    /// The recoverable editing cache (including the workspace manifest) is per
    /// Windows user under %LOCALAPPDATA%. It used to live beside the executable,
    /// where every user of a shared installation read and wrote the same
    /// manifest and a portable in-place upgrade replaced it with the folder.
    /// The first launch of the default profile migrates that legacy cache.
    /// </summary>
    private static string ResolveDefaultPersistentEditingRoot(string localDataRoot)
    {
        var explicitRoot = Environment.GetEnvironmentVariable(EditingRootOverride);
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return Path.GetFullPath(explicitRoot);

        var perUserRoot = Path.GetFullPath(Path.Combine(localDataRoot, "Editing Cache"));
        // An isolated local-data profile (specs, evidence runs, test payloads)
        // must never import the shared legacy cache of the real installation.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LocalDataOverride)))
            return perUserRoot;

        var legacyRoot = LegacyPersistentEditingRoot;
        lock (EditingRootMigrationGate)
        {
            if (editingRootDecision is { } decision &&
                PathsEqual(decision.Legacy, legacyRoot) &&
                PathsEqual(decision.Target, perUserRoot))
                return decision.Chosen;

            var chosen = TryMigrateLegacyEditingRoot(legacyRoot, perUserRoot)
                ? perUserRoot
                : legacyRoot;
            editingRootDecision = (legacyRoot, perUserRoot, chosen);
            return chosen;
        }
    }

    /// <summary>The pre-migration editing cache beside the executable.</summary>
    public static string LegacyPersistentEditingRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        LegacyEditingCacheFolderName,
        "Avalonia"));

    /// <summary>
    /// Copies a legacy editing cache into <paramref name="targetRoot"/> when the
    /// legacy tree has a workspace manifest and the target does not. Returns
    /// true when <paramref name="targetRoot"/> should be used; false when the
    /// copy failed and the untouched legacy cache must stay in use, so no
    /// recoverable working copy can be lost either way.
    /// </summary>
    public static bool TryMigrateLegacyEditingRoot(string legacyRoot, string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        legacyRoot = Path.GetFullPath(legacyRoot);
        targetRoot = Path.GetFullPath(targetRoot);
        var legacyManifest = Path.Combine(legacyRoot, "desktop-workspace", "workspace.json");
        var targetManifest = Path.Combine(targetRoot, "desktop-workspace", "workspace.json");
        if (PathsEqual(legacyRoot, targetRoot) ||
            File.Exists(targetManifest) ||
            !File.Exists(legacyManifest))
            return true;

        try
        {
            // Everything except the manifest first; the manifest is published
            // last, so "target manifest exists" always means "copy complete".
            CopyTreeForMigration(legacyRoot, targetRoot, legacyManifest);
            Directory.CreateDirectory(Path.GetDirectoryName(targetManifest)!);
            var temporary = $"{targetManifest}.{Guid.NewGuid():N}.migrating";
            try
            {
                File.Copy(legacyManifest, temporary, overwrite: false);
                if (new FileInfo(temporary).Length != new FileInfo(legacyManifest).Length)
                    throw new IOException("The migrated workspace manifest is incomplete.");
                File.Move(temporary, targetManifest, overwrite: false);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                    // Preserve the primary migration failure.
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or NotSupportedException)
        {
            // A second launch forwarding to this one may have raced the same
            // copy; a published target manifest means the migration completed.
            if (File.Exists(targetManifest)) return true;
            WindowsDiagnosticLog.Current?.WriteException(
                "editing-cache-migration-failed",
                exception,
                fatal: false);
            return false;
        }

        // Best effort: mark the legacy session as migrated so another Windows
        // user of a shared installation does not import the same tabs again.
        // The legacy caches themselves are left in place.
        try
        {
            File.Move(
                legacyManifest,
                legacyManifest + ".migrated-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return true;
    }

    private static void CopyTreeForMigration(string sourceRoot, string targetRoot, string deferredFile)
    {
        var pending = new Stack<(string Source, string Target)>();
        pending.Push((sourceRoot, targetRoot));
        while (pending.Count > 0)
        {
            var (source, target) = pending.Pop();
            Directory.CreateDirectory(target);
            foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var destination = Path.Combine(target, entry.Name);
                if (entry is DirectoryInfo directory)
                {
                    // Process-owned transient export trees are not migrated.
                    if (PathsEqual(source, sourceRoot) &&
                        directory.Name.Equals("_scratch", StringComparison.OrdinalIgnoreCase))
                        continue;
                    pending.Push((directory.FullName, destination));
                    continue;
                }
                if (PathsEqual(entry.FullName, deferredFile)) continue;
                var file = (FileInfo)entry;
                File.Copy(file.FullName, destination, overwrite: true);
                if (new FileInfo(destination).Length != file.Length)
                    throw new IOException($"The migrated file is incomplete: {destination}");
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The root to write into settings. While a startup fallback is active the
    /// fallback root is never persisted; the saved root is written instead.
    /// </summary>
    private static string? PersistableWorkspaceRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var fullPath = Path.GetFullPath(root);
        return WorkspaceRootFallback is { } fallback && PathsEqual(fullPath, fallback.FallbackRoot)
            ? fallback.RequestedRoot
            : fullPath;
    }

    private static OwnWorkspaceSettings? ReadOwnWorkspaceSettings()
    {
        string? path = null;
        try
        {
            path = WorkspaceSettingsPath;
            if (!WindowsSettingsFileGuard.TryReadExisting(path, out var bytes)) return null;
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The workspace settings root is not an object.");
            return new OwnWorkspaceSettings(
                ReadString(root, "WorkspaceRoot", "workspaceRoot"),
                ReadString(root, "LibrarySort", "librarySort"),
                (root.TryGetProperty("ProGridColumns", out var columns) ||
                 root.TryGetProperty("proGridColumns", out columns)) &&
                columns.TryGetInt32(out var value)
                    ? value
                    : null);
        }
        catch (Exception exception)
        {
            if (path != null) WindowsSettingsFileGuard.ReportLoadFailure(path, exception);
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name, string alternateName) =>
        (element.TryGetProperty(name, out var value) ||
         element.TryGetProperty(alternateName, out value)) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record OwnWorkspaceSettings(
        string? WorkspaceRoot,
        string? LibrarySort,
        int? ProGridColumns);

    private static void ValidateLocation(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, name);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Platform storage locations must be fully qualified.", name);
    }

    private static string EnsureDirectory(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AddExistingRoot(ISet<string> roots, string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
        }
    }

    private static string? TryReadWorkspaceRoot(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            // JsonDocument rejects a UTF-8 byte order mark in raw bytes, and the
            // catch below would then silently drop the user's library root.
            using var document = JsonDocument.Parse(bytes.AsMemory(bytes is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0));
            return document.RootElement.TryGetProperty("WorkspaceRoot", out var root)
                ? root.GetString()
                : document.RootElement.TryGetProperty("workspaceRoot", out root)
                    ? root.GetString()
                    : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record WorkspaceSettings(
        string? WorkspaceRoot,
        string LibrarySort,
        int ProGridColumns);
}

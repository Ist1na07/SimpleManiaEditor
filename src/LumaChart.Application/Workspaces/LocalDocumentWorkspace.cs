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
/// Cross-platform local document workspace used by the Android workbench.
/// Source charts stay under the platform-owned Library root while every open
/// tab edits an MC working copy under PersistentEditing.
/// </summary>
public sealed class LocalDocumentWorkspace
{
    private const int ManifestSchemaVersion = 1;
    private const int IoBufferSize = 64 * 1024;
    private const int MaximumSuggestedNameCharacters = 96;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string libraryRoot;
    private readonly string editingRoot;
    private readonly string documentCacheRoot;
    private readonly string manifestPath;
    private readonly string defaultCreator;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<LocalChartTab> tabs = [];
    private readonly List<string> recoveryWarnings = [];

    public LocalDocumentWorkspace(
        string libraryRoot,
        string persistentEditingRoot,
        string defaultCreator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentEditingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultCreator);
        this.libraryRoot = Path.GetFullPath(libraryRoot);
        editingRoot = Path.GetFullPath(Path.Combine(persistentEditingRoot, "local-workspace"));
        documentCacheRoot = Path.Combine(editingRoot, "documents");
        manifestPath = Path.Combine(editingRoot, "workspace.json");
        this.defaultCreator = defaultCreator;
        Directory.CreateDirectory(this.libraryRoot);
        Directory.CreateDirectory(documentCacheRoot);
    }

    public string LibraryRoot => libraryRoot;
    public string EditingRoot => editingRoot;
    public string ManifestPath => manifestPath;
    public IReadOnlyList<LocalChartTab> Tabs => tabs;
    public LocalChartTab? ActiveTab { get; private set; }
    public IReadOnlyList<string> RecoveryWarnings => recoveryWarnings;

    public event EventHandler<LocalWorkspaceChangedEventArgs>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DetachAllTabs();
            tabs.Clear();
            ActiveTab = null;
            recoveryWarnings.Clear();

            var manifest = await TryReadManifestAsync(cancellationToken).ConfigureAwait(false);
            if (manifest == null) return;

            foreach (var item in manifest.Tabs.OrderBy(item => item.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var tab = await RestoreTabAsync(item, cancellationToken).ConfigureAwait(false);
                    if (tab == null) continue;
                    AttachTab(tab);
                    tabs.Add(tab);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    recoveryWarnings.Add($"{item.SourceRelativePath}: {exception.Message}");
                }
            }

            ActiveTab = tabs.FirstOrDefault(tab => tab.Id == manifest.ActiveTabId) ?? tabs.FirstOrDefault();
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        PublishChanged(LocalWorkspaceChangeKind.Reset, ActiveTab);
    }

    public async Task<IReadOnlyList<LocalChartLibraryEntry>> ScanLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        var sourcePaths = EnumerateSupportedSourcePaths(cancellationToken);
        var entries = new List<LocalChartLibraryEntry>(sourcePaths.Count);
        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var format = GetFormat(sourcePath);
                var document = await LoadSourceAsync(sourcePath, format, cancellationToken).ConfigureAwait(false);
                entries.Add(new LocalChartLibraryEntry(
                    sourcePath,
                    format,
                    document.Metadata.Title,
                    document.Metadata.Artist,
                    document.Metadata.Version,
                    document.KeyCount,
                    document.Notes.Count,
                    File.GetLastWriteTimeUtc(sourcePath)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                recoveryWarnings.Add($"Library scan skipped {Path.GetFileName(sourcePath)}: {exception.Message}");
            }
        }

        return entries
            .OrderBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Version, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SourcePath, PathComparer)
            .ToArray();
    }

    public async Task<LocalChartTab> OpenAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        sourcePath = ValidateSourcePath(sourcePath, requireExisting: true);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalChartTab tab;
        try
        {
            var existing = tabs.FirstOrDefault(candidate =>
                PathComparer.Equals(candidate.SourcePath, sourcePath));
            if (existing != null)
            {
                ActiveTab = existing;
                await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
                tab = existing;
            }
            else
            {
                var format = GetFormat(sourcePath);
                var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
                var document = await LoadSourceAsync(sourcePath, format, cancellationToken).ConfigureAwait(false);
                var id = Guid.NewGuid();
                var workingPath = GetWorkingPath(id);
                document.FilePath = workingPath;
                var workingHash = await WriteWorkingCopyAsync(document, workingPath, cancellationToken)
                    .ConfigureAwait(false);
                var session = new EditorSession();
                session.ReplaceDocument(document);
                tab = new LocalChartTab(id, session, sourcePath, workingPath, format)
                {
                    SourceSha256 = sourceHash,
                    WorkingSha256 = workingHash
                };
                AttachTab(tab);
                tabs.Add(tab);
                ActiveTab = tab;
                await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        PublishChanged(LocalWorkspaceChangeKind.ActiveTab, tab);
        return tab;
    }

    public async Task<LocalChartTab> CreateAsync(
        ChartDocument document,
        string? suggestedName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var baseName = CreateSafeBaseName(suggestedName ?? document.Metadata.Title);
        var sourcePath = AllocateUniqueSourcePath(baseName, ".mc");
        var sourceDocument = document.Clone();
        sourceDocument.FilePath = sourcePath;
        await WriteSourceAtomicAsync(sourceDocument, sourcePath, LocalChartFormat.MalodyMc, cancellationToken)
            .ConfigureAwait(false);
        return await OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
    }

    public void Activate(Guid tabId)
    {
        var tab = tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                  ?? throw new KeyNotFoundException("The requested local chart tab is not open.");
        if (ReferenceEquals(ActiveTab, tab)) return;
        ActiveTab = tab;
        PublishChanged(LocalWorkspaceChangeKind.ActiveTab, tab);
    }

    public void UpdateViewState(Guid tabId, double currentBeat)
    {
        var tab = tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                  ?? throw new KeyNotFoundException("The requested local chart tab is not open.");
        tab.CurrentBeat = double.IsFinite(currentBeat) ? Math.Max(0, currentBeat) : 0;
    }

    public async Task<LocalChartSaveResult> SaveAsync(
        Guid tabId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalChartTab? tab = null;
        try
        {
            tab = tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                  ?? throw new KeyNotFoundException("The requested local chart tab is not open.");
            var issues = ChartValidator.Validate(tab.Session.Document);
            if (issues.Count > 0)
                return new LocalChartSaveResult(LocalChartSaveStatus.ValidationFailed, issues);
            if (!File.Exists(tab.SourcePath))
                return new LocalChartSaveResult(LocalChartSaveStatus.SourceMissing, []);

            var currentSourceHash = await ComputeSha256Async(tab.SourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(currentSourceHash, tab.SourceSha256, StringComparison.OrdinalIgnoreCase))
                return new LocalChartSaveResult(LocalChartSaveStatus.SourceChanged, []);

            var sourceSnapshot = tab.Session.Document.Clone();
            sourceSnapshot.FilePath = tab.SourcePath;
            await WriteSourceAtomicAsync(sourceSnapshot, tab.SourcePath, tab.Format, cancellationToken)
                .ConfigureAwait(false);
            tab.SourceSha256 = await ComputeSha256Async(tab.SourcePath, cancellationToken).ConfigureAwait(false);
            tab.SourceMissing = false;
            tab.Session.MarkSaved();
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            return new LocalChartSaveResult(LocalChartSaveStatus.Saved, []);
        }
        finally
        {
            gate.Release();
            if (tab != null) PublishChanged(LocalWorkspaceChangeKind.Document, tab);
        }
    }

    public async Task ReloadFromSourceAsync(
        Guid tabId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalChartTab? tab = null;
        try
        {
            tab = tabs.FirstOrDefault(candidate => candidate.Id == tabId)
                  ?? throw new KeyNotFoundException("The requested local chart tab is not open.");
            if (!File.Exists(tab.SourcePath))
                throw new FileNotFoundException("The chart source no longer exists.", tab.SourcePath);
            var document = await LoadSourceAsync(tab.SourcePath, tab.Format, cancellationToken)
                .ConfigureAwait(false);
            document.FilePath = tab.WorkingPath;
            tab.SourceSha256 = await ComputeSha256Async(tab.SourcePath, cancellationToken).ConfigureAwait(false);
            tab.WorkingSha256 = await WriteWorkingCopyAsync(document, tab.WorkingPath, cancellationToken)
                .ConfigureAwait(false);
            tab.SourceMissing = false;
            tab.Session.ReplaceDocument(document);
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        if (tab != null) PublishChanged(LocalWorkspaceChangeKind.Document, tab);
    }

    public async Task<bool> CloseAsync(
        Guid tabId,
        bool discardUnsavedChanges,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LocalChartTab? closed = null;
        try
        {
            closed = tabs.FirstOrDefault(candidate => candidate.Id == tabId);
            if (closed == null) return false;
            if (closed.Session.IsDirty && !discardUnsavedChanges) return false;
            var index = tabs.IndexOf(closed);
            closed.Session.Changed -= Session_Changed;
            tabs.RemoveAt(index);
            if (ReferenceEquals(ActiveTab, closed))
                ActiveTab = tabs.Count == 0 ? null : tabs[Math.Clamp(index, 0, tabs.Count - 1)];
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
            TryDeleteDocumentCache(closed.Id);
        }
        finally
        {
            gate.Release();
        }

        PublishChanged(LocalWorkspaceChangeKind.Tabs, ActiveTab ?? closed);
        return true;
    }

    public async Task PersistAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PersistCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LocalChartTab?> RestoreTabAsync(
        LocalWorkspaceTabManifest item,
        CancellationToken cancellationToken)
    {
        if (item.Id == Guid.Empty) throw new InvalidDataException("A workspace tab has an empty ID.");
        var sourcePath = ResolveRelativeSourcePath(item.SourceRelativePath);
        var format = GetFormat(sourcePath);
        if (!string.Equals(item.SourceFormat, FormatName(format), StringComparison.OrdinalIgnoreCase))
            recoveryWarnings.Add($"{item.SourceRelativePath}: source format was repaired from the file extension.");
        var workingPath = GetWorkingPath(item.Id);
        var sourceExists = File.Exists(sourcePath);
        var sourceHash = sourceExists
            ? await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false)
            : item.SourceSha256;
        var sourceChanged = sourceExists &&
                            !string.Equals(sourceHash, item.SourceSha256, StringComparison.OrdinalIgnoreCase);
        var workingExists = File.Exists(workingPath);
        var workingHash = workingExists
            ? await ComputeSha256Async(workingPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        var workingChangedOutsideManifest = workingExists &&
                                            !string.Equals(
                                                workingHash,
                                                item.WorkingSha256,
                                                StringComparison.OrdinalIgnoreCase);

        ChartDocument document;
        var dirty = item.IsDirty;
        if (sourceChanged || !workingExists || (workingChangedOutsideManifest && sourceExists))
        {
            if (!sourceExists) return null;
            document = await LoadSourceAsync(sourcePath, format, cancellationToken).ConfigureAwait(false);
            document.FilePath = workingPath;
            item.WorkingSha256 = await WriteWorkingCopyAsync(document, workingPath, cancellationToken)
                .ConfigureAwait(false);
            dirty = false;
            if (sourceChanged)
                recoveryWarnings.Add($"{item.SourceRelativePath}: source changed; the source replaced the old cache.");
            else if (workingChangedOutsideManifest)
                recoveryWarnings.Add($"{item.SourceRelativePath}: cache fingerprint changed; the source replaced the unsafe cache.");
        }
        else
        {
            try
            {
                document = await LoadWorkingCopyAsync(workingPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && sourceExists)
            {
                recoveryWarnings.Add($"{item.SourceRelativePath}: invalid cache was replaced from source ({exception.Message}).");
                document = await LoadSourceAsync(sourcePath, format, cancellationToken).ConfigureAwait(false);
                dirty = false;
            }
            document.FilePath = workingPath;
            item.WorkingSha256 = await WriteWorkingCopyAsync(document, workingPath, cancellationToken)
                .ConfigureAwait(false);
            if (!sourceExists)
            {
                dirty = true;
                if (workingChangedOutsideManifest)
                    recoveryWarnings.Add($"{item.SourceRelativePath}: source is missing; the only recoverable cache was retained.");
            }
        }

        var session = new EditorSession();
        session.ReplaceDocument(document, markDirty: dirty);
        return new LocalChartTab(item.Id, session, sourcePath, workingPath, format)
        {
            CurrentBeat = double.IsFinite(item.CurrentBeat) ? Math.Max(0, item.CurrentBeat) : 0,
            SourceSha256 = sourceHash,
            WorkingSha256 = item.WorkingSha256,
            SourceMissing = !sourceExists
        };
    }

    private async Task<LocalWorkspaceManifest?> TryReadManifestAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath)) return null;
        try
        {
            await using var source = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                IoBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync<LocalWorkspaceManifest>(
                source,
                ManifestJsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (manifest == null || manifest.SchemaVersion != ManifestSchemaVersion)
                throw new InvalidDataException("The local workspace manifest schema is unsupported.");
            return manifest;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            recoveryWarnings.Add($"Workspace manifest was ignored: {exception.Message}");
            return null;
        }
    }

    private async Task PersistCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(editingRoot);
        foreach (var tab in tabs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tab.WorkingSha256 = await WriteWorkingCopyAsync(
                tab.Session.Document,
                tab.WorkingPath,
                cancellationToken).ConfigureAwait(false);
        }
        var manifest = new LocalWorkspaceManifest
        {
            ActiveTabId = ActiveTab?.Id,
            Tabs = tabs.Select((tab, index) => new LocalWorkspaceTabManifest
            {
                Id = tab.Id,
                Order = index,
                SourceRelativePath = GetRelativeSourcePath(tab.SourcePath),
                SourceFormat = FormatName(tab.Format),
                SourceSha256 = tab.SourceSha256,
                WorkingSha256 = tab.WorkingSha256,
                CurrentBeat = tab.CurrentBeat,
                IsDirty = tab.Session.IsDirty
            }).ToList()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        await WriteBytesAtomicAsync(manifestPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChartDocument> LoadSourceAsync(
        string sourcePath,
        LocalChartFormat format,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            IoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await ChartDocumentImporter.ImportAsync(
            source,
            format == LocalChartFormat.MalodyMc ? ChartImportFormat.MalodyMc : ChartImportFormat.OsuMania,
            defaultCreator,
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
        return await ChartDocumentImporter.ImportAsync(
            source,
            ChartImportFormat.MalodyMc,
            defaultCreator,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSourceAtomicAsync(
        ChartDocument document,
        string destination,
        LocalChartFormat format,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("The chart source has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.luma-part");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (format == LocalChartFormat.MalodyMc)
            {
                var bytes = Utf8WithoutBom.GetBytes(MalodyMcSerializer.Serialize(document));
                await WriteNewBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                OsuManiaSerializer.Save(
                    document,
                    temporary,
                    document.Metadata.OverallDifficulty,
                    document.Metadata.HpDrainRate);
                await using var output = new FileStream(
                    temporary,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    IoBufferSize,
                    FileOptions.WriteThrough);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
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

    private static async Task WriteBytesAtomicAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new InvalidOperationException("The atomic destination has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
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

    private List<string> EnumerateSupportedSourcePaths(CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(libraryRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(ValidateChildPath(libraryRoot, child));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    recoveryWarnings.Add($"Library directory skipped: {exception.Message}");
                }
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(file);
                if (extension.Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".osu", StringComparison.OrdinalIgnoreCase))
                    result.Add(ValidateSourcePath(file, requireExisting: true));
            }
        }
        return result;
    }

    private string ValidateSourcePath(string sourcePath, bool requireExisting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = ValidateChildPath(libraryRoot, sourcePath);
        _ = GetFormat(fullPath);
        if (requireExisting && !File.Exists(fullPath))
            throw new FileNotFoundException("The chart source does not exist in the private Library.", fullPath);
        return fullPath;
    }

    private string ResolveRelativeSourcePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("A workspace source path is not relative to Library.");
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return ValidateSourcePath(Path.Combine(libraryRoot, normalized), requireExisting: false);
    }

    private string GetRelativeSourcePath(string sourcePath)
    {
        sourcePath = ValidateSourcePath(sourcePath, requireExisting: false);
        var relative = Path.GetRelativePath(libraryRoot, sourcePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("A chart source escaped the private Library root.");
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private string GetWorkingPath(Guid id)
    {
        var directory = ValidateChildPath(documentCacheRoot, Path.Combine(documentCacheRoot, id.ToString("N")));
        Directory.CreateDirectory(directory);
        return ValidateChildPath(documentCacheRoot, Path.Combine(directory, "working.mc"));
    }

    private string AllocateUniqueSourcePath(string baseName, string extension)
    {
        for (var suffix = 1; suffix <= 10_000; suffix++)
        {
            var name = suffix == 1 ? baseName : $"{baseName} ({suffix})";
            var candidate = ValidateSourcePath(Path.Combine(libraryRoot, name + extension), requireExisting: false);
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to allocate a unique chart name in the private Library.");
    }

    private static LocalChartFormat GetFormat(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".mc", StringComparison.OrdinalIgnoreCase)) return LocalChartFormat.MalodyMc;
        if (extension.Equals(".osu", StringComparison.OrdinalIgnoreCase)) return LocalChartFormat.OsuMania;
        throw new NotSupportedException("Only Malody .mc and osu!mania .osu sources are supported.");
    }

    private static string FormatName(LocalChartFormat format) =>
        format == LocalChartFormat.OsuMania ? "osu" : "mc";

    private static string CreateSafeBaseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) value = "untitled-chart";
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(Math.Min(value.Length, MaximumSuggestedNameCharacters));
        var replacement = false;
        foreach (var character in value.Trim())
        {
            if (builder.Length >= MaximumSuggestedNameCharacters) break;
            if (char.IsControl(character) || invalid.Contains(character) || character is '/' or '\\')
            {
                if (!replacement) builder.Append('_');
                replacement = true;
                continue;
            }
            builder.Append(character);
            replacement = false;
        }
        var result = builder.ToString().Trim().Trim('.');
        if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1];
        return string.IsNullOrWhiteSpace(result) ? "untitled-chart" : result;
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            IoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
    }

    private void AttachTab(LocalChartTab tab) => tab.Session.Changed += Session_Changed;

    private void DetachAllTabs()
    {
        foreach (var tab in tabs) tab.Session.Changed -= Session_Changed;
    }

    private void Session_Changed(object? sender, EventArgs eventArgs)
    {
        var tab = tabs.FirstOrDefault(candidate => ReferenceEquals(candidate.Session, sender));
        if (tab != null) PublishChanged(LocalWorkspaceChangeKind.Document, tab);
    }

    private void PublishChanged(LocalWorkspaceChangeKind kind, LocalChartTab? tab) =>
        Changed?.Invoke(this, new LocalWorkspaceChangedEventArgs(kind, tab));

    private void TryDeleteDocumentCache(Guid id)
    {
        var path = ValidateChildPath(documentCacheRoot, Path.Combine(documentCacheRoot, id.ToString("N")));
        if (!Directory.Exists(path)) return;
        try
        {
            Directory.Delete(path, recursive: true);
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the primary atomic-write exception. App-owned startup
            // cleanup may remove a crash residue later.
        }
    }

    private static string ValidateChildPath(string root, string candidate)
    {
        root = Path.GetFullPath(root);
        candidate = Path.GetFullPath(candidate);
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, PathComparison))
            throw new InvalidDataException("A local workspace path escaped its platform-owned root.");
        return candidate;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class LocalWorkspaceManifest
    {
        public int SchemaVersion { get; init; } = ManifestSchemaVersion;
        public Guid? ActiveTabId { get; set; }
        public List<LocalWorkspaceTabManifest> Tabs { get; set; } = [];
    }

    private sealed class LocalWorkspaceTabManifest
    {
        public Guid Id { get; set; }
        public int Order { get; set; }
        public string SourceRelativePath { get; set; } = string.Empty;
        public string SourceFormat { get; set; } = "mc";
        public string SourceSha256 { get; set; } = string.Empty;
        public string WorkingSha256 { get; set; } = string.Empty;
        public double CurrentBeat { get; set; }
        public bool IsDirty { get; set; }
    }
}

public sealed class LocalChartTab
{
    internal LocalChartTab(
        Guid id,
        EditorSession session,
        string sourcePath,
        string workingPath,
        LocalChartFormat format)
    {
        Id = id;
        Session = session;
        SourcePath = sourcePath;
        WorkingPath = workingPath;
        Format = format;
    }

    public Guid Id { get; }
    public EditorSession Session { get; }
    public string SourcePath { get; }
    public string WorkingPath { get; }
    public LocalChartFormat Format { get; }
    public double CurrentBeat { get; internal set; }
    public bool SourceMissing { get; internal set; }
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

    public string FormatBadge => Format == LocalChartFormat.OsuMania ? "OSU → MC" : "MALODY";
}

public sealed record LocalChartLibraryEntry(
    string SourcePath,
    LocalChartFormat Format,
    string Title,
    string Artist,
    string Version,
    int KeyCount,
    int NoteCount,
    DateTime LastWriteUtc)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? Path.GetFileNameWithoutExtension(SourcePath)
        : Title;
    public string FormatBadge => Format == LocalChartFormat.OsuMania ? "OSU" : "MC";
}

public sealed record LocalChartSaveResult(
    LocalChartSaveStatus Status,
    IReadOnlyList<ChartIssue> Issues)
{
    public bool Saved => Status == LocalChartSaveStatus.Saved;
}

public sealed record LocalWorkspaceChangedEventArgs(
    LocalWorkspaceChangeKind Kind,
    LocalChartTab? Tab);

public enum LocalChartFormat
{
    MalodyMc,
    OsuMania
}

public enum LocalChartSaveStatus
{
    Saved,
    ValidationFailed,
    SourceChanged,
    SourceMissing
}

public enum LocalWorkspaceChangeKind
{
    Reset,
    Tabs,
    ActiveTab,
    Document
}

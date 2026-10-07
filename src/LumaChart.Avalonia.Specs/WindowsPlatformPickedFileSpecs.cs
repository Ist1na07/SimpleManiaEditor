using System.Text;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;

namespace LumaChart.Avalonia.Specs;

/// <summary>
/// Covers the platform picker boundary the Android tablet needs. Windows
/// pickers return real file-system paths and must keep using them untouched;
/// the Storage Access Framework returns documents with no path, which every
/// shared "choose a file" flow previously read as a cancellation.
/// </summary>
internal static class WindowsPlatformPickedFileSpecs
{
    private const string LocalDataRootVariable = "LUMACHART_AVALONIA_LOCAL_DATA_ROOT";
    private const string EditingRootVariable = "LUMACHART_AVALONIA_EDITING_ROOT";

    public static async Task<IReadOnlyList<WindowsPlatformPickedFileSpecResult>> RunAsync(
        string isolationRoot)
    {
        Directory.CreateDirectory(isolationRoot);
        var results = new List<WindowsPlatformPickedFileSpecResult>();
        await CheckAsync(
            results,
            "picked-file-with-local-path-is-used-without-staging",
            () => VerifyLocalPathIsNotCopiedAsync(isolationRoot));
        await CheckAsync(
            results,
            "picked-file-without-local-path-is-staged-into-app-private-cache",
            () => VerifyPathlessDocumentIsStagedAsync(isolationRoot));
        await CheckAsync(
            results,
            "picked-file-durable-retention-stays-outside-the-prunable-resource-cache",
            () => VerifyDurableRetentionAsync(isolationRoot));
        await CheckAsync(
            results,
            "picked-file-staging-prunes-only-stale-copies",
            () => VerifyStalePruningAsync(isolationRoot));
        await CheckAsync(
            results,
            "picked-file-staging-failure-leaves-no-partial-copy",
            () => VerifyFailedStageLeavesNothingAsync(isolationRoot));
        await CheckAsync(
            results,
            "in-app-save-as-name-cannot-leave-the-app-owned-folder",
            () => Task.FromResult(VerifySaveAsNameSanitization()));
        await CheckAsync(
            results,
            "in-app-save-as-directory-prefers-the-chart-folder",
            () => Task.FromResult(VerifySaveAsDirectoryResolution(isolationRoot)));
        return results;
    }

    private static async Task<string> VerifyLocalPathIsNotCopiedAsync(string isolationRoot)
    {
        using var scope = new IsolatedRoots(isolationRoot, "local-path");
        var payload = Encoding.UTF8.GetBytes("real windows picker result");
        var sourcePath = Path.Combine(scope.Root, "picked-audio.mp3");
        await File.WriteAllBytesAsync(sourcePath, payload);

        var resolved = await PlatformPickedFiles.ResolveLocalPathAsync(
            new FakePickedFile("picked-audio.mp3", sourcePath, payload),
            PickedFileRetention.Transient);

        Require(
            PathsEqual(resolved, sourcePath),
            $"A picker result that already has a local path was not used directly: {resolved}");
        Require(
            !Directory.Exists(AvaloniaAppPaths.PickedFileStagingRoot) ||
            Directory.GetFileSystemEntries(AvaloniaAppPaths.PickedFileStagingRoot).Length == 0,
            "A local Windows picker result was copied into the staging root.");
        return $"LocalPath={resolved}; StagedEntries=0";
    }

    private static async Task<string> VerifyPathlessDocumentIsStagedAsync(string isolationRoot)
    {
        using var scope = new IsolatedRoots(isolationRoot, "pathless");
        var payload = Encoding.UTF8.GetBytes("content://document bytes 中文");
        var resolved = await PlatformPickedFiles.ResolveLocalPathAsync(
            new FakePickedFile("曲子.mp3", localPath: null, payload),
            PickedFileRetention.Transient);

        Require(resolved != null, "A path-less picked document produced no staged path.");
        Require(
            IsInside(resolved!, AvaloniaAppPaths.PickedFileStagingRoot),
            $"A staged document landed outside the app-private staging root: {resolved}");
        Require(
            Path.GetFileName(resolved!) == "曲子.mp3",
            $"The staged copy did not keep the document's own name: {Path.GetFileName(resolved!)}");
        Require(
            (await File.ReadAllBytesAsync(resolved!)).SequenceEqual(payload),
            "The staged copy does not match the picked document's bytes.");

        // A second pick of the same name must not overwrite a copy still in use.
        var second = await PlatformPickedFiles.ResolveLocalPathAsync(
            new FakePickedFile("曲子.mp3", localPath: null, payload),
            PickedFileRetention.Transient);
        Require(
            second != null && !PathsEqual(second, resolved),
            "A repeated pick of the same document name reused the previous staged path.");
        return $"Staged={Path.GetFileName(resolved!)}; Second={Path.GetFileName(second!)}";
    }

    private static async Task<string> VerifyDurableRetentionAsync(string isolationRoot)
    {
        using var scope = new IsolatedRoots(isolationRoot, "durable");
        var payload = Encoding.UTF8.GetBytes("custom key sound");
        var resolved = await PlatformPickedFiles.ResolveLocalPathAsync(
            new FakePickedFile("clap.wav", localPath: null, payload),
            PickedFileRetention.Durable);

        Require(resolved != null, "A durable pick produced no staged path.");
        Require(
            IsInside(resolved!, AvaloniaAppPaths.PickedFileDurableRoot),
            $"A durable pick landed outside the durable root: {resolved}");
        // The Key-sound settings persist this path, so cache management must not
        // be able to delete it out from under a later session.
        Require(
            !IsInside(resolved!, AvaloniaAppPaths.OnlineRoomResourceCacheRoot),
            "A durable pick landed inside the resource cache that cache cleanup clears.");
        Require(
            !IsInside(resolved!, AvaloniaAppPaths.DesktopDocumentCacheRoot),
            "A durable pick landed inside the editing cache that cache cleanup clears.");
        return $"Durable={resolved}";
    }

    private static async Task<string> VerifyStalePruningAsync(string isolationRoot)
    {
        using var scope = new IsolatedRoots(isolationRoot, "pruning");
        Directory.CreateDirectory(AvaloniaAppPaths.PickedFileStagingRoot);
        var stale = Path.Combine(AvaloniaAppPaths.PickedFileStagingRoot, "stale.mp3");
        var fresh = Path.Combine(AvaloniaAppPaths.PickedFileStagingRoot, "fresh.mp3");
        await File.WriteAllBytesAsync(stale, [1, 2, 3]);
        await File.WriteAllBytesAsync(fresh, [4, 5, 6]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(9));

        var resolved = await PlatformPickedFiles.ResolveLocalPathAsync(
            new FakePickedFile("new.mp3", localPath: null, [7, 8, 9]),
            PickedFileRetention.Transient);

        Require(!File.Exists(stale), "A stale staged copy was not pruned.");
        Require(
            File.Exists(fresh),
            "A recent staged copy was pruned while its import could still be reading it.");
        Require(resolved != null && File.Exists(resolved), "The new staged copy was pruned.");
        return "Pruned=stale.mp3; Retained=fresh.mp3";
    }

    private static async Task<string> VerifyFailedStageLeavesNothingAsync(string isolationRoot)
    {
        using var scope = new IsolatedRoots(isolationRoot, "failure");
        var failed = false;
        try
        {
            await PlatformPickedFiles.ResolveLocalPathAsync(
                new FakePickedFile(
                    "truncated.ogg",
                    localPath: null,
                    Encoding.UTF8.GetBytes("header"),
                    failAfterBytes: 3),
                PickedFileRetention.Transient);
        }
        catch (IOException)
        {
            failed = true;
        }

        Require(failed, "A failing document read did not surface as an error.");
        var staged = Directory.Exists(AvaloniaAppPaths.PickedFileStagingRoot)
            ? Directory.GetFiles(AvaloniaAppPaths.PickedFileStagingRoot)
            : [];
        Require(
            staged.Length == 0,
            $"A failed stage left a truncated file behind: {string.Join(", ", staged)}");
        return "PartialCopies=0";
    }

    private static string VerifySaveAsNameSanitization()
    {
        string?[] rejected =
        [
            null,
            string.Empty,
            "   ",
            ".",
            "..",
            "../escape",
            @"..\escape",
            "folder/chart",
            @"folder\chart",
            "C:chart",
            "bad\0name"
        ];
        foreach (var candidate in rejected)
            Require(
                InAppSaveAsPolicy.SanitizeFileName(candidate) == null,
                $"An unsafe Save As name was accepted: {candidate ?? "<null>"}");

        Require(
            InAppSaveAsPolicy.SanitizeFileName("  Reply 4K  ") == "Reply 4K",
            "A normal Save As name was not trimmed and accepted.");
        Require(
            InAppSaveAsPolicy.SanitizeFileName("谱面 副本") == "谱面 副本",
            "A non-ASCII Save As name was rejected.");
        return $"Rejected={rejected.Length}; Accepted=2";
    }

    private static string VerifySaveAsDirectoryResolution(string isolationRoot)
    {
        var root = Path.Combine(isolationRoot, "save-as-root");
        var chartFolder = Path.Combine(root, "Reply");
        Directory.CreateDirectory(chartFolder);
        var chartPath = Path.Combine(chartFolder, "reply.mc");
        File.WriteAllText(chartPath, "{}");

        Require(
            PathsEqual(InAppSaveAsPolicy.ResolveDirectory(chartPath, root), chartFolder),
            "Save As did not stay in the chart's own folder beside its audio and images.");
        Require(
            PathsEqual(
                InAppSaveAsPolicy.ResolveDirectory(
                    Path.Combine(root, "missing", "gone.mc"),
                    root),
                root),
            "Save As did not fall back to the app-owned root for a missing chart folder.");
        Require(
            PathsEqual(InAppSaveAsPolicy.ResolveDirectory(null, root), root),
            "Save As did not fall back to the app-owned root for an unknown source path.");
        Require(
            PathsEqual(InAppSaveAsPolicy.ResolveDirectory(Path.Combine(root, "top.mc"), root), root),
            "Save As did not accept the app-owned root itself as the chart folder.");

        // A tab bound to an online room reads from app-internal room-document
        // storage. Saving the copy back there hides the user's work from the
        // library, so an outside folder must fall back to the root even though
        // it exists.
        var outsideFolder = Path.Combine(isolationRoot, "online-room-documents", "room_abc");
        Directory.CreateDirectory(outsideFolder);
        var roomChartPath = Path.Combine(outsideFolder, "u.mc");
        File.WriteAllText(roomChartPath, "{}");
        Require(
            PathsEqual(InAppSaveAsPolicy.ResolveDirectory(roomChartPath, root), root),
            "Save As kept a room-bound chart inside app-internal room storage instead of the library.");
        return $"ChartFolder={Path.GetFileName(chartFolder)}; Fallback={Path.GetFileName(root)}; " +
               "RoomDocumentRedirected=True";
    }

    /// <summary>
    /// Points the shared app paths at a private tree so a staging check cannot
    /// read or delete another spec's cache.
    /// </summary>
    private sealed class IsolatedRoots : IDisposable
    {
        private readonly string? previousLocalData;
        private readonly string? previousEditing;

        public IsolatedRoots(string isolationRoot, string name)
        {
            previousLocalData = Environment.GetEnvironmentVariable(LocalDataRootVariable);
            previousEditing = Environment.GetEnvironmentVariable(EditingRootVariable);
            Root = Path.Combine(isolationRoot, name);
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable(
                LocalDataRootVariable,
                Path.Combine(Root, "local-data"));
            Environment.SetEnvironmentVariable(
                EditingRootVariable,
                Path.Combine(Root, "editing"));
        }

        public string Root { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(LocalDataRootVariable, previousLocalData);
            Environment.SetEnvironmentVariable(EditingRootVariable, previousEditing);
        }
    }

    /// <summary>
    /// Stands in for a picker result. A Windows result carries a real path; an
    /// Android document carries a content URI and no local path at all.
    /// </summary>
    private sealed class FakePickedFile(
        string name,
        string? localPath,
        byte[] payload,
        int? failAfterBytes = null) : IPickedDocument
    {
        public string Name { get; } = name;
        public string? LocalPath { get; } = localPath;

        public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(
            failAfterBytes is { } limit
                ? new FailingStream(payload, limit)
                : new MemoryStream(payload, writable: false));
    }

    /// <summary>A document whose provider dies part-way through the transfer.</summary>
    private sealed class FailingStream(byte[] payload, int failAfterBytes) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= failAfterBytes)
                throw new IOException("The document provider stopped responding.");
            var available = Math.Min(count, failAfterBytes - position);
            Array.Copy(payload, position, buffer, offset, available);
            position += available;
            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static async Task CheckAsync(
        List<WindowsPlatformPickedFileSpecResult> results,
        string name,
        Func<Task<string>> verify)
    {
        try
        {
            results.Add(new WindowsPlatformPickedFileSpecResult(
                name,
                true,
                await verify().ConfigureAwait(false)));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsPlatformPickedFileSpecResult(name, false, exception.Message));
        }
    }

    private static bool IsInside(string path, string root) =>
        Path.GetFullPath(path).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string? left, string? right) =>
        left != null &&
        right != null &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsPlatformPickedFileSpecResult(
    string Name,
    bool Passed,
    string Detail);

using System.Security.Cryptography;
using LumaChart.Online;

namespace LumaChart.Avalonia.Infrastructure;

public sealed record WindowsFrozenRoomResourceFingerprint(
    string RelativePath,
    long Length,
    string Sha256);

/// <summary>
/// Immutable resource authority captured when a Windows room chart is bound.
/// Paths are used only for inexpensive menu filtering; every assignment verifies
/// the current bytes against the frozen length and SHA-256 before it may mutate a Note.
/// </summary>
public sealed class WindowsFrozenRoomResourceRegistry
{
    private const int CopyBufferBytes = 128 * 1024;
    private readonly string root;
    private readonly IReadOnlyDictionary<string, WindowsFrozenRoomResourceFingerprint> entries;

    private WindowsFrozenRoomResourceRegistry(
        string root,
        IReadOnlyDictionary<string, WindowsFrozenRoomResourceFingerprint> entries)
    {
        this.root = root;
        this.entries = entries;
    }

    public int Count => entries.Count;

    public IReadOnlyCollection<WindowsFrozenRoomResourceFingerprint> Entries =>
        entries.Values.ToArray();

    public static async Task<WindowsFrozenRoomResourceRegistry> CreateAsync(
        string chartPath,
        OnlineRoomResourceManifestDto manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var root = ChartRoot(chartPath);
        var manifestEntries = manifest.Entries?.ToArray() ?? [];
        if (manifestEntries.Length == 0)
            return await CaptureLegacyAsync(root, cancellationToken).ConfigureAwait(false);

        var frozen = new Dictionary<string, WindowsFrozenRoomResourceFingerprint>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifestEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(entry.Path);
            if (entry.Length < 0 || !IsSha256(entry.Sha256))
                throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间冻结资源清单无效：{0}", relative));
            var fullPath = ResolveInside(root, relative);
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length != entry.Length)
                throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间冻结资源缺失或长度不符：{0}", relative));
            if (!frozen.TryAdd(
                    relative,
                    new WindowsFrozenRoomResourceFingerprint(
                        relative,
                        entry.Length,
                        entry.Sha256.ToUpperInvariant())))
                throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间冻结资源路径重复：{0}", relative));
        }
        return new WindowsFrozenRoomResourceRegistry(root, frozen);
    }

    public bool ContainsListedFile(string fullPath)
    {
        if (!TryResolve(fullPath, out var resolved, out var fingerprint)) return false;
        try
        {
            return File.Exists(resolved) && new FileInfo(resolved).Length == fingerprint.Length;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public async Task<bool> VerifyAsync(
        string fullPath,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolve(fullPath, out var resolved, out var fingerprint)) return false;
        try
        {
            var information = new FileInfo(resolved);
            if (!information.Exists || information.Length != fingerprint.Length) return false;
            await using var stream = new FileStream(
                resolved,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return string.Equals(actual, fingerprint.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryResolve(
        string fullPath,
        out string resolved,
        out WindowsFrozenRoomResourceFingerprint fingerprint)
    {
        resolved = string.Empty;
        fingerprint = null!;
        try
        {
            var candidate = Path.GetFullPath(fullPath);
            if (!IsInside(root, candidate)) return false;
            var relative = NormalizeRelativePath(Path.GetRelativePath(root, candidate));
            if (!entries.TryGetValue(relative, out var found)) return false;
            resolved = candidate;
            fingerprint = found;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<WindowsFrozenRoomResourceRegistry> CaptureLegacyAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var frozen = new Dictionary<string, WindowsFrozenRoomResourceFingerprint>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(file);
            if (!IsInside(root, fullPath))
                throw new InvalidDataException("房间冻结资源越过了工作缓存边界。");
            var relative = NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
            var length = new FileInfo(fullPath).Length;
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!frozen.TryAdd(
                    relative,
                    new WindowsFrozenRoomResourceFingerprint(relative, length, hash)))
                throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间冻结资源路径重复：{0}", relative));
        }
        return new WindowsFrozenRoomResourceRegistry(root, frozen);
    }

    private static string ChartRoot(string chartPath)
    {
        if (string.IsNullOrWhiteSpace(chartPath))
            throw new ArgumentException("A room chart path is required.", nameof(chartPath));
        var directory = Path.GetDirectoryName(Path.GetFullPath(chartPath));
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new InvalidDataException("房间谱面工作缓存目录不存在。");
        return directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
               Path.DirectorySeparatorChar;
    }

    private static string ResolveInside(string root, string relative)
    {
        var resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(root, resolved))
            throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间冻结资源越过工作缓存边界：{0}", relative));
        return resolved;
    }

    private static bool IsInside(string root, string candidate) =>
        candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("房间冻结资源路径为空。");
        var normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(path) ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException(AvaloniaLocalizationCatalog.Format("房间冻结资源路径无效：{0}", path));
        return normalized;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

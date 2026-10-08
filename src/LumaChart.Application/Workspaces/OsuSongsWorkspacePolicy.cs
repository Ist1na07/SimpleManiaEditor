using System.Security.Cryptography;
using System.Text;

namespace LumaChart.Application.Workspaces;

/// <summary>The game library is an import source. Editable files and resources live outside Songs.</summary>
public static class OsuSongsWorkspacePolicy
{
    private static readonly SemaphoreSlim copyGate = new(1);
    public static bool IsSongsPath(string path) => Path.GetFullPath(path)
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.Equals("Songs", StringComparison.OrdinalIgnoreCase));

    public static async Task<string> GetEditableCopyAsync(string source, string creationRoot, CancellationToken cancellationToken = default)
    {
        source = Path.GetFullPath(source);
        if (!IsSongsPath(source)) return source;
        creationRoot = Path.GetFullPath(creationRoot);
        if (IsSongsPath(creationRoot)) throw new InvalidOperationException("创作目录不能位于 osu! Songs 内。");
        var sourceDirectory = Path.GetDirectoryName(source)!;
        var name = Path.GetFileName(sourceDirectory);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0) name = "mapset";
        if (name.Length > 60) name = name[..60];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceDirectory.ToUpperInvariant())))[..12].ToLowerInvariant();
        var destinationDirectory = Path.GetFullPath(Path.Combine(creationRoot, "Imports", name + "-" + hash));
        if (!destinationDirectory.StartsWith(creationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("创作副本路径越界。");
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        await copyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(() =>
        {
            var pending = new Stack<(string Source, string Destination)>();
            pending.Push((sourceDirectory, destinationDirectory));
            while (pending.TryPop(out var pair))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(pair.Destination);
                foreach (var file in Directory.EnumerateFiles(pair.Source))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.EndsWith(".sme-backup", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(pair.Destination, Path.GetFileName(file));
                    if (!File.Exists(target))
                    {
                        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".sme-import";
                        try { File.Copy(file, temporary, overwrite: false); File.Move(temporary, target, overwrite: false); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                }
                foreach (var child in Directory.EnumerateDirectories(pair.Source))
                    if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                        pending.Push((child, Path.Combine(pair.Destination, Path.GetFileName(child))));
            }
        }, cancellationToken).ConfigureAwait(false); }
        finally { copyGate.Release(); }
        if (!File.Exists(destination)) throw new IOException("未能建立完整创作副本。");
        return destination;
    }
}

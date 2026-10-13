using LumaChart.Core.IO;
using System.Text.RegularExpressions;
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

    public static async Task<string> GetEditableCopyAsync(string source, string creationRoot, CancellationToken cancellationToken = default, bool forceCopy = false)
    {
        source = Path.GetFullPath(source);
        if (!forceCopy && !OsuFileSafetyPolicy.IsProtectedPath(source)) return source;
        creationRoot = Path.GetFullPath(creationRoot);
        OsuFileSafetyPolicy.RequireWritable(creationRoot);
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
        try {
            if (!OsuFileSafetyPolicy.IsProtectedPath(source))
            {
                await CopyReferencedFilesAsync(source, destination, creationRoot, cancellationToken).ConfigureAwait(false);
                return destination;
            }
            await Task.Run(() =>
        {
            var pending = new Stack<(string Source, string Destination)>();
            pending.Push((sourceDirectory, destinationDirectory));
            while (pending.TryPop(out var pair))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!OsuFileSafetyPolicy.IsWithin(pair.Destination, creationRoot)) throw new IOException("创作副本目录链接越界。");
                OsuFileSafetyPolicy.RequireWritable(pair.Destination);
                Directory.CreateDirectory(pair.Destination);
                foreach (var file in Directory.EnumerateFiles(pair.Source))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.EndsWith(".sme-backup", StringComparison.OrdinalIgnoreCase) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".dll" or ".db" or ".cfg" or ".realm") continue;
                    var target = Path.Combine(pair.Destination, Path.GetFileName(file));
                    if (!OsuFileSafetyPolicy.IsWithin(target, creationRoot)) throw new IOException("创作副本文件链接越界。");
                    OsuFileSafetyPolicy.RequireWritable(target);
                    if (!File.Exists(target))
                    {
                        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".sme-import";
                        try {
                            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                            File.Move(temporary, target, overwrite: false);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                }
                foreach (var child in Directory.EnumerateDirectories(pair.Source))
                    if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint) && !OsuFileSafetyPolicy.IsWithin(destinationDirectory, child))
                        pending.Push((child, Path.Combine(pair.Destination, Path.GetFileName(child))));
            }
        }, cancellationToken).ConfigureAwait(false); }
        finally { copyGate.Release(); }
        if (!File.Exists(destination)) throw new IOException("未能建立完整创作副本。");
        return destination;
    }
    // An individual file may live on Desktop or in Temp. Never recursively
    // copy its containing directory: import only this chart and its resources.
    private static async Task CopyReferencedFilesAsync(string source, string destination, string creationRoot, CancellationToken cancellationToken)
    {
        OsuFileSafetyPolicy.RequireWritable(destination);
        if (!OsuFileSafetyPolicy.IsWithin(destination, creationRoot)) throw new IOException("导入目标越界。");
        if (File.Exists(destination)) return; // Preserve edits made to the existing copy.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var document = await ChartDocumentImporter.ImportAsync(input, source, "guest", cancellationToken).ConfigureAwait(false);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            document.Metadata.AudioFile, document.Metadata.CoverFile, document.Metadata.BackgroundFile
        };
        foreach (var note in document.Notes) if (!string.IsNullOrWhiteSpace(note.Sound)) references.Add(note.Sound);
        var folder = Path.GetDirectoryName(source)!;
        if (Path.GetExtension(source).Equals(".osu", StringComparison.OrdinalIgnoreCase))
        {
            // Preserve video/storyboard/custom sample references that the note
            // editor keeps in the native source envelope.
            foreach (Match match in Regex.Matches(await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false), "\"([^\"]+)\""))
                references.Add(match.Groups[1].Value);
            foreach (var file in Directory.EnumerateFiles(folder, "*.osb")) references.Add(Path.GetFileName(file));
            foreach (var file in Directory.EnumerateFiles(folder, "*.wav"))
                if (Regex.IsMatch(Path.GetFileName(file), @"^(normal|soft|drum)-(hit|slider)", RegexOptions.IgnoreCase)) references.Add(Path.GetFileName(file));
            foreach (var storyboard in references.Where(r => r.EndsWith(".osb", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var resolved = DesktopChartAssetLocalizer.ResolveReference(storyboard, source);
                if (resolved == null) continue;
                foreach (Match match in Regex.Matches(await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false), "\"([^\"]+)\"")) references.Add(match.Groups[1].Value);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resource = DesktopChartAssetLocalizer.ResolveReference(reference, source);
            if (resource == null) continue;
            var target = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetRelativePath(folder, resource));
            if (!OsuFileSafetyPolicy.IsWithin(target, creationRoot)) throw new IOException("导入资源目标越界。");
            OsuFileSafetyPolicy.RequireWritable(target);
            if (!File.Exists(target)) await CopyOneAsync(resource, target, cancellationToken).ConfigureAwait(false);
        }
        // Publish the chart last: cancellation cannot leave a usable-looking
        // chart in the library whose requested assets have not been copied.
        await CopyOneAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyOneAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".sme-import";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

}

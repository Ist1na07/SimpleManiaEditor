using System.Text.Json;
using LumaChart.Application.Workspaces;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>References only: never moves, edits or deletes a linked library.</summary>
public sealed class EditorLibraryCatalog
{
    private readonly string path;
    private readonly HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> charts = new(StringComparer.OrdinalIgnoreCase);
    private bool readable = true;
    public IReadOnlyList<string> Roots => roots.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<string> Charts => charts.ToArray();

    public EditorLibraryCatalog(string profileRoot)
    {
        path = Path.Combine(profileRoot, "library-catalog.json");
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<State>(File.ReadAllBytes(path));
            foreach (var root in saved?.Roots ?? []) roots.Add(Path.GetFullPath(root));
            foreach (var chart in saved?.Charts ?? []) charts.Add(Path.GetFullPath(chart));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            readable = false; // Keep an unreadable catalog intact.
        }
    }

    public void Remember(IEnumerable<string> paths)
    {
        var changed = false;
        foreach (var item in paths) changed |= charts.Add(Path.GetFullPath(item));
        if (changed) Save();
    }

    public void AddRoot(string root)
    {
        root = LocalLibraryRootResolver.Resolve(root).RequestedRoot;
        if (roots.Add(root)) Save();
    }

    public void RemoveRoot(string root)
    {
        if (roots.Remove(Path.GetFullPath(root))) Save();
    }

    private void Save()
    {
        if (!readable) throw new IOException("曲库目录清单无法读取，已保留原文件。请检查存储设置。");
        OsuFileSafetyPolicy.RequireWritable(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(Roots.ToArray(), Charts.ToArray()));
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record State(string[] Roots, string[] Charts);
}

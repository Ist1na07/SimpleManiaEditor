using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LumaChart.Avalonia.Infrastructure;

internal sealed record OsuSkinEntry(string Id, string Name, string Path)
{
    public bool Available => Path.Length == 0 || Directory.Exists(Path);
    public override string ToString() => Available ? Name : Name + "（目录不可用）";
}

/// <summary>A persistent skin catalogue, independent from the currently selected skin.
/// Registers user folders read-only. Imported archives already live in our managed root.</summary>
internal static class OsuSkinLibrary
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static string Manifest => System.IO.Path.Combine(AvaloniaAppPaths.LocalDataRoot, "skins", "library.json");
    public static OsuSkinEntry BuiltIn { get; } = new("builtin", "osu!mania · 内置", "");

    public static IReadOnlyList<OsuSkinEntry> List(string selectedPath = "")
    {
        lock (Gate)
        {
            var entries = Load();
            if (!string.IsNullOrEmpty(selectedPath) && !entries.Any(e => SamePath(e.Path, selectedPath)))
            {
                entries.Add(Describe(selectedPath));
                Save(entries);
            }
            return new[] { BuiltIn }.Concat(entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)).ToArray();
        }
    }

    public static IReadOnlyList<OsuSkinEntry> RegisterFolder(string path)
    {
        path = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("皮肤目录不存在。");
        var candidates = IsSkinDirectory(path) ? new[] { path } : Directory.EnumerateDirectories(path)
            .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0 && IsSkinDirectory(p)).Take(2000).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("这个目录没有找到皮肤。请选择单个皮肤目录或 osu! 的 Skins 目录。");
        lock (Gate)
        {
            var entries = Load(); var registered = candidates.Select(Describe).ToArray();
            foreach (var item in registered) { entries.RemoveAll(e => SamePath(e.Path, item.Path)); entries.Add(item); }
            Save(entries); return registered;
        }
    }

    public static bool SamePath(string left, string right) => left.TrimEnd('\\','/').Equals(right.TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase);
    private static bool IsSkinDirectory(string path) => File.Exists(System.IO.Path.Combine(path, "skin.ini")) ||
        Directory.EnumerateFiles(path).Any(p => System.IO.Path.GetFileName(p).StartsWith("mania-", StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileName(p).Equals("7K.ini", StringComparison.OrdinalIgnoreCase));
    private static OsuSkinEntry Describe(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(full);
        try { var ini = System.IO.Path.Combine(full, "skin.ini"); if (File.Exists(ini)) name = OsuIniDocument.ReadSkin(ini).Find("General")?.Text("Name", name) ?? name; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..16], name, full);
    }
    private static List<OsuSkinEntry> Load()
    {
        try
        {
            if (!WindowsSettingsFileGuard.TryReadExisting(Manifest, out var bytes)) return [];
            return (JsonSerializer.Deserialize<List<OsuSkinEntry>>(bytes, JsonOptions) ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e.Path)).DistinctBy(e => e.Id).Take(2000).ToList();
        }
        catch (Exception e) { WindowsSettingsFileGuard.ReportLoadFailure(Manifest, e); throw new IOException("皮肤列表无法读取，现有文件已保留。", e); }
    }
    private static void Save(List<OsuSkinEntry> entries)
    {
        if (!WindowsSettingsFileGuard.CanWrite(Manifest)) throw new IOException("皮肤列表暂时不能写入，未覆盖旧数据。");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Manifest)!);
        var temp = Manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, entries, JsonOptions); file.Flush(true); }
            if (File.Exists(Manifest)) File.Replace(temp, Manifest, Manifest + ".previous", true); else File.Move(temp, Manifest);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

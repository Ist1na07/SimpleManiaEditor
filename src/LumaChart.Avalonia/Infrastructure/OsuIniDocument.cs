using System.Globalization;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>Small tolerant reader shared by skin.ini and stable user cfg import.</summary>
internal sealed class OsuIniDocument
{
    public List<Section> Sections { get; } = [];
    public OsuIniDocument(string text, char separator = ':')
    {
        var current = new Section(""); Sections.Add(current);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            { current = new Section(line[1..^1].Trim()); Sections.Add(current); continue; }
            var split = line.IndexOf(separator);
            if (split < 1) continue;
            var value = line[(split + 1)..].Trim();
            var comment = value.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) value = value[..comment].Trim();
            current.Values[line[..split].Trim()] = value;
        }
    }
    public Section? Find(string name) => Sections.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public Section Mania(int keys) => Sections.FirstOrDefault(s => s.Name.Equals("Mania", StringComparison.OrdinalIgnoreCase) && s.Number("Keys", 0) == keys) ?? new Section("Mania");
    internal sealed class Section(string name)
    {
        public string Name { get; } = name;
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Text(string key, string fallback = "") => Values.GetValueOrDefault(key, fallback);
        public double Number(string key, double fallback) => double.TryParse(Text(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
        public bool Flag(string key, bool fallback) => Values.ContainsKey(key) ? Text(key) == "1" : fallback;
        public double[] Numbers(string key, int count, double fallback, double minimum = 0, double maximum = 1000)
        {
            var items = Text(key).Split(',');
            return Enumerable.Range(0, count).Select(i => i < items.Length && double.TryParse(items[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? Math.Clamp(n, minimum, maximum) : fallback).ToArray();
        }
    }
}

using Avalonia.Input;
using System.Text.RegularExpressions;

namespace LumaChart.Avalonia.Infrastructure;

internal sealed record OsuPlaytestImportResult(WindowsPlaytestSettings Settings, string Summary);

internal static partial class OsuPlaytestImport
{
    // Read only. Never retain the cfg text, which may also contain account information.
    public static OsuPlaytestImportResult Read(string cfgPath, WindowsPlaytestSettings current)
    {
        if (new FileInfo(cfgPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("配置文件过大。");
        var cfg = new OsuIniDocument(File.ReadAllText(cfgPath), '=');
        var values = cfg.Sections.SelectMany(s => s.Values).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var result = current; var imported = new List<int>(); var warnings = new List<string>();
        for (var count = 1; count <= 18; count++)
        {
            if (!values.TryGetValue($"ManiaLayouts{count}K", out var layout)) continue;
            var keys = layout.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeKey).ToArray();
            if (keys.Length != count || keys.Any(k => k == null || k == "Escape") || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != count)
            { warnings.Add($"{count}K 键位无效，保留原设定"); continue; }
            result = result.With(bindingKeyCount: count, bindings: keys.Select(k => k!).ToArray()); imported.Add(count);
        }
        var speed = values.TryGetValue("ManiaSpeed", out var speedText) && int.TryParse(speedText, out var n) && n is >= 1 and <= 40 ? n : current.ManiaSpeed;
        string Hotkey(string name, string fallback) => values.TryGetValue(name, out var text) ? NormalizeKey(text) ?? fallback : fallback;
        result = result.With(maniaSpeed: speed, increaseSpeedKey: Hotkey("keyIncreaseSpeed", current.IncreaseSpeedKey),
            decreaseSpeedKey: Hotkey("keyDecreaseSpeed", current.DecreaseSpeedKey), pauseKey: Hotkey("keyPause", current.PauseKey), retryKey: Hotkey("keyQuickRetry", current.RetryKey));
        if (values.TryGetValue("SkinSamples", out var samples) && samples is "0" or "1") result = result.With(useSkinSamples: samples == "1");
        var root = Path.GetDirectoryName(Path.GetFullPath(cfgPath))!;
        if (values.TryGetValue("Skin", out var skin) && skin is not ("Default" or "User"))
        {
            var skinsRoot = Path.Combine(root, "Skins");
            var path = Path.GetFullPath(Path.Combine(skinsRoot, skin));
            if (path.StartsWith(skinsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) result = result.With(skinPath: path);
        }
        // Old stable BPM scaling has no lazer equivalent; don't silently reinterpret it.
        if (values.GetValueOrDefault("ManiaSpeedBPMScale") == "1") warnings.Add("BPM 落速缩放使用旧 stable 行为，本次不导入；落速按 lazer 定义");
        return new(result, (imported.Count == 0 ? "没有有效的键位可导入，保留原键位；" : $"已导入 {string.Join("、", imported.Select(k => k + "K"))} 键位；") + $"落速 {speed:0.#}。" + string.Join("；", warnings));
    }

    public static string? NormalizeKey(string text)
    {
        var key = text.Trim();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Return"]="Enter", ["Back"]="Back", ["Capital"]="CapsLock", ["LShiftKey"]="LeftShift", ["RShiftKey"]="RightShift",
            ["LControlKey"]="LeftCtrl", ["RControlKey"]="RightCtrl", ["LMenu"]="LeftAlt", ["RMenu"]="RightAlt",
            ["Oemplus"]="OemPlus", ["Oem1"]="OemSemicolon", ["Oem2"]="OemQuestion", ["Oem3"]="OemTilde",
            ["Oem4"]="OemOpenBrackets", ["Oem5"]="OemPipe", ["Oem6"]="OemCloseBrackets", ["Oem7"]="OemQuotes", ["Oem102"]="OemBackslash"
        };
        key = aliases.GetValueOrDefault(key, key);
        if (!KeyName().IsMatch(key) || !Enum.TryParse<Key>(key, true, out var parsed) || !Enum.IsDefined(parsed) || parsed is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed) return null;
        return aliases.Values.FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? parsed.ToString();
    }
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")] private static partial Regex KeyName();
}

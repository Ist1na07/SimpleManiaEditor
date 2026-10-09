using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Core.IO;

/// <summary>
/// Retains the osu source across clones and MC recovery copies. Only fields actually
/// edited by the mapper are rewritten. The envelope is never emitted into an osu file.
/// </summary>
public static class OsuSourceEnvelope
{
    private const string RootKey = "_sme_osu_source";
    private const string ObjectKey = "_sme_osu_object";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static bool HasSource(ChartDocument document) => document.UnknownRoot[RootKey] is JsonObject;
    public static bool HasUtf8Bom(ChartDocument document) => document.UnknownRoot[RootKey]?["bom"]?.GetValue<bool>() == true;
    public static void RememberUtf8Bom(ChartDocument document, bool bom)
    {
        if (document.UnknownRoot[RootKey] is JsonObject source) source["bom"] = bom;
    }

    public static void AttachNote(ChartNote note, string line, int keys)
    {
        note.UnknownData ??= new JsonObject();
        note.UnknownData[ObjectKey] = new JsonObject
        {
            ["line"] = line, ["beat"] = note.Beat.Value, ["end"] = note.EndBeat?.Value,
            ["lane"] = note.Lane, ["keys"] = keys, ["volume"] = note.Volume, ["sound"] = note.Sound ?? ""
        };
    }

    public static void Capture(ChartDocument document, string text)
    {
        var map = new TimingMap(document.Timing);
        var rawTiming = DataLines(text, "TimingPoints").ToArray();
        foreach (var timing in document.Timing)
        {
            var time = map.BeatToMilliseconds(timing.Beat);
            var row = rawTiming.LastOrDefault(line =>
            {
                var parts = line.Split(',');
                return parts.Length >= 2 && Number(parts[0], out var ms) &&
                    Math.Abs(ms - time) < 1 && Number(parts[1], out var length) &&
                    (timing.Bpm.HasValue ? length > 0 && (parts.Length < 7 || parts[6].Trim() != "0")
                        : timing.Effect == TimingEffectKind.Scroll && length < 0 && parts.Length >= 7 && parts[6].Trim() == "0");
            });
            if (row == null) continue;
            timing.UnknownData ??= new JsonObject();
            timing.UnknownData[ObjectKey] = new JsonObject { ["line"] = row, ["time"] = time };
        }
        document.UnknownRoot[RootKey] = new JsonObject
        {
            ["version"] = 1, ["text"] = text,
            ["metadata"] = JsonSerializer.SerializeToNode(document.Metadata),
            ["keys"] = document.KeyCount,
            ["timing"] = TimingSignature(document), ["notes"] = NoteSignature(document),
            ["knownNotes"] = new JsonArray(document.Notes.Where(n => n.UnknownData?[ObjectKey] is JsonObject)
                .Select(n => (JsonNode?)JsonValue.Create(n.UnknownData![ObjectKey]!["line"]!.GetValue<string>())).ToArray())
        };
    }

    public static bool TrySerialize(ChartDocument document, double? od, double? hp, out string text)
    {
        text = "";
        if (document.UnknownRoot[RootKey] is not JsonObject source) return false;
        if (document.Timing.Any(t => t.Effect.HasValue && t.Effect != TimingEffectKind.Scroll))
            throw new NotSupportedException("Jump / HS / Sign / Custom 是 Malody 效果，不能写入 osu!mania；请移除该效果或另存为 .mc。");
        if (source["version"]?.GetValue<int>() != 1)
            throw new NotSupportedException("This osu source envelope requires a newer editor.");
        text = source["text"]!.GetValue<string>();
        var original = source["metadata"]!.Deserialize<ChartMetadata>()!;
        var replacements = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        void Pair(string section, string key, string value, bool changed)
        {
            if (!changed) return;
            if (!replacements.TryGetValue(section, out var pairs)) replacements[section] = pairs = new(StringComparer.OrdinalIgnoreCase);
            pairs[key] = value.Replace('\r', ' ').Replace('\n', ' ');
        }
        var metadata = document.Metadata;
        Pair("General", "AudioFilename", metadata.AudioFile, metadata.AudioFile != original.AudioFile);
        Pair("General", "PreviewTime", metadata.PreviewTime.ToString(Invariant), metadata.PreviewTime != original.PreviewTime);
        Pair("Metadata", "Title", metadata.Title, metadata.Title != original.Title);
        Pair("Metadata", "TitleUnicode", metadata.TitleOriginal, metadata.TitleOriginal != original.TitleOriginal);
        Pair("Metadata", "Artist", metadata.Artist, metadata.Artist != original.Artist);
        Pair("Metadata", "ArtistUnicode", metadata.ArtistOriginal, metadata.ArtistOriginal != original.ArtistOriginal);
        Pair("Metadata", "Creator", metadata.Creator, metadata.Creator != original.Creator);
        Pair("Metadata", "Version", metadata.Version, metadata.Version != original.Version);
        Pair("Difficulty", "CircleSize", document.KeyCount.ToString(Invariant), document.KeyCount != source["keys"]!.GetValue<int>());
        Pair("Difficulty", "OverallDifficulty", (od ?? metadata.OverallDifficulty).ToString("R", Invariant),
            (od ?? metadata.OverallDifficulty) != original.OverallDifficulty);
        Pair("Difficulty", "HPDrainRate", (hp ?? metadata.HpDrainRate).ToString("R", Invariant),
            (hp ?? metadata.HpDrainRate) != original.HpDrainRate);
        // Only maintain this pre-existing extension; never add app-only fields to an imported osu file.
        if (DataLines(text, "Metadata").Any(line => line.StartsWith("LumaChartBPM:", StringComparison.Ordinal)))
            Pair("Metadata", "LumaChartBPM", metadata.Bpm.ToString("R", Invariant), metadata.Bpm != original.Bpm);

        foreach (var (section, pairs) in replacements) text = PatchPairs(text, section, pairs);
        if (metadata.BackgroundFile != original.BackgroundFile)
        {
            var old = DataLines(text, "Events").Where(IsBackground).ToHashSet(StringComparer.Ordinal);
            var background = metadata.BackgroundFile.Replace('\\', '/').Replace("\"", "");
            text = ReplaceRows(text, "Events", old, string.IsNullOrEmpty(background)
                ? [] : [$"0,0,\"{background}\",0,0"]);
        }
        if (TimingSignature(document) != source["timing"]!.GetValue<string>())
        {
            var known = DataLines(text, "TimingPoints").Where(IsTiming).ToHashSet(StringComparer.Ordinal);
            text = ReplaceRows(text, "TimingPoints", known, SerializeTiming(document, text));
        }
        if (NoteSignature(document) != source["notes"]!.GetValue<string>() || document.KeyCount != source["keys"]!.GetValue<int>())
        {
            var known = source["knownNotes"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var map = new TimingMap(document.Timing);
            var notes = document.Notes.Where(n => !n.IsDraft)
                .OrderBy(n => StartMilliseconds(n, map)).ThenBy(n => n.Lane)
                .Select(n => SerializeNote(n, document.KeyCount, source["keys"]!.GetValue<int>(), map)).ToArray();
            text = ReplaceRows(text, "HitObjects", known, notes);
        }
        return true;
    }

    /// <summary>Changing timing in an imported osu map keeps untouched notes at their absolute song times.</summary>
    public static void ReanchorUnmovedNotes(ChartDocument before, ChartDocument after)
    {
        if (!HasSource(before) || TimingSignature(before) == TimingSignature(after)) return;
        var oldMap = new TimingMap(before.Timing);
        ReanchorTiming(before, after, oldMap);
        var newMap = new TimingMap(after.Timing);
        var oldNotes = before.Notes.ToDictionary(n => n.Id);
        foreach (var note in after.Notes)
        {
            if (note.IsDraft || !oldNotes.TryGetValue(note.Id, out var old) || note.Beat != old.Beat || note.EndBeat != old.EndBeat) continue;
            var line = SerializeNote(note, after.KeyCount, before.KeyCount, oldMap);
            var parts = line.Split(',');
            var start = double.Parse(parts[2], Invariant);
            note.Beat = newMap.MillisecondsToBeat(start, BeatPosition.MaximumPreservedDenominator);
            if (old.IsHold)
                note.EndBeat = newMap.MillisecondsToBeat(double.Parse(parts[5].Split(':')[0], Invariant), BeatPosition.MaximumPreservedDenominator);
            // Keep current audio/property edits, but update the timestamp anchor.
            AttachNote(note, line, after.KeyCount);
        }
    }

    private static void ReanchorTiming(ChartDocument before, ChartDocument after, TimingMap oldMap)
    {
        var previous = before.Timing.ToDictionary(t => t.Id);
        var tentative = new TimingMap(after.Timing);
        var targets = after.Timing.Select(t =>
        {
            var untouchedPosition = previous.TryGetValue(t.Id, out var old) && t.Beat == old.Beat && t.Delay == old.Delay;
            var time = untouchedPosition ? oldMap.BeatToMilliseconds(old!.Beat) : tentative.BeatToMilliseconds(t.Beat);
            var source = t.UnknownData?[ObjectKey] as JsonObject;
            if (untouchedPosition && source != null && Math.Abs(time - source["time"]!.GetValue<double>()) < 1e-6)
                time = double.Parse(source["line"]!.GetValue<string>().Split(',')[0], Invariant);
            if (t.UnknownData?["_sme_requested_timing_ms"] is JsonValue requested)
            {
                time = requested.GetValue<double>();
                t.UnknownData.Remove("_sme_requested_timing_ms");
            }
            return (Timing: t, Time: time);
        }).ToArray();
        var red = targets.Where(t => t.Timing.Bpm.HasValue).OrderBy(t => t.Time).ToArray();
        double beat = 0;
        for (var i = 0; i < red.Length; i++)
        {
            if (i > 0) beat += (red[i].Time - red[i - 1].Time) * red[i - 1].Timing.Bpm!.Value / 60_000d;
            red[i].Timing.Beat = PreciseBeat(beat);
            red[i].Timing.Delay = i == 0 ? -red[i].Time : 0;
        }
        var map = new TimingMap(after.Timing);
        foreach (var target in targets.Where(t => !t.Timing.Bpm.HasValue))
            target.Timing.Beat = map.MillisecondsToBeat(target.Time, BeatPosition.MaximumPreservedDenominator);
        foreach (var target in targets)
            if (target.Timing.UnknownData?[ObjectKey] is JsonObject source)
                source["time"] = map.BeatToMilliseconds(target.Timing.Beat);
    }

    private static BeatPosition PreciseBeat(double value) => BeatPosition.FromDouble(value, 1_000_000);

    private static double StartMilliseconds(ChartNote note, TimingMap map)
    {
        var source = note.UnknownData?[ObjectKey] as JsonObject;
        if (source != null && note.Beat.Value == source["beat"]!.GetValue<double>())
            return double.Parse(source["line"]!.GetValue<string>().Split(',')[2], Invariant);
        return Math.Round(map.BeatToMilliseconds(note.Beat));
    }

    private static string SerializeNote(ChartNote note, int keys, int originalKeys, TimingMap map)
    {
        var source = note.UnknownData?[ObjectKey] as JsonObject;
        var sourceKeys = source?["keys"]?.GetValue<int>() ?? originalKeys;
        if (source != null && keys == sourceKeys && note.Lane == source["lane"]!.GetValue<int>() &&
            note.Beat.Value == source["beat"]!.GetValue<double>() &&
            note.EndBeat?.Value == source["end"]?.GetValue<double>() &&
            note.Volume == source["volume"]!.GetValue<int>() &&
            (note.Sound ?? "") == source["sound"]!.GetValue<string>())
            return source["line"]!.GetValue<string>();
        var parts = source?["line"]?.GetValue<string>().Split(',') ?? ["0", "192", "0", "1", "0", "0:0:0:0:"];
        if (keys != sourceKeys || source == null || note.Lane != source["lane"]!.GetValue<int>())
            parts[0] = Math.Clamp((int)((note.Lane + .5) * 512 / keys), 0, 511).ToString(Invariant);
        parts[2] = StartMilliseconds(note, map).ToString("0", Invariant);
        var oldType = int.TryParse(parts[3], Invariant, out var parsedType) ? parsedType : 1;
        var wasHold = (oldType & 128) != 0;
        var sample = wasHold ? string.Join(':', parts[5].Split(':').Skip(1)) : parts[5];
        var sampleParts = sample.Split(':', 5).ToList();
        while (sampleParts.Count < 5) sampleParts.Add("");
        if (source == null || note.Volume != source["volume"]!.GetValue<int>())
            sampleParts[3] = source == null && note.Volume == 100 ? "0" : Math.Clamp(note.Volume, 1, 100).ToString(Invariant);
        if (source == null || (note.Sound ?? "") != source["sound"]!.GetValue<string>())
            sampleParts[4] = (note.Sound ?? "").Replace(':', '_').Replace(',', '_').Replace('\r', ' ').Replace('\n', ' ');
        sample = string.Join(':', sampleParts);
        parts[3] = ((oldType & ~(1 | 128)) | (note.IsHold ? 128 : 1)).ToString(Invariant);
        if (note.IsHold)
        {
            var end = source?["end"] is JsonValue originalEnd && note.EndBeat!.Value.Value == originalEnd.GetValue<double>() && wasHold
                ? parts[5].Split(':')[0]
                : Math.Round(map.BeatToMilliseconds(note.EndBeat!.Value)).ToString("0", Invariant);
            parts[5] = end + ":" + sample;
        }
        else parts[5] = sample;
        return string.Join(',', parts);
    }

    private static string[] SerializeTiming(ChartDocument document, string text)
    {
        var map = new TimingMap(document.Timing);
        var originalRows = DataLines(text, "TimingPoints").Where(IsTiming).Select(l => l.Split(',')).ToArray();
        return document.Timing.Where(t => t.Bpm.HasValue || t.Effect == TimingEffectKind.Scroll)
            .Select((t, order) =>
            {
                var time = map.BeatToMilliseconds(t.Beat);
                var source = t.UnknownData?[ObjectKey] as JsonObject;
                var parts = source?["line"]?.GetValue<string>().Split(',') ??
                    originalRows.LastOrDefault(p => double.Parse(p[0], Invariant) <= time)?.ToArray() ?? ["0", "500", "4", "0", "0", "100", "1", "0"];
                if (parts.Length < 8) Array.Resize(ref parts, 8);
                for (var i = 2; i < 8; i++) parts[i] ??= i == 2 ? "4" : i == 5 ? "100" : "0";
                if (source == null || Math.Abs(time - source["time"]!.GetValue<double>()) > 1e-7)
                    parts[0] = Math.Round(time, MidpointRounding.AwayFromZero).ToString("0", Invariant);
                var value = t.Bpm.HasValue ? 60_000d / t.Bpm.Value : -100d / t.EffectValue;
                if (!Number(parts[1], out var oldValue) || Math.Abs(value - oldValue) > 1e-9 * Math.Max(1, Math.Abs(value)))
                    parts[1] = value.ToString("0.############", Invariant);
                parts[6] = t.Bpm.HasValue ? "1" : "0";
                OsuTimingPointSettings.WriteFields(t, parts);
                return (Time: double.Parse(parts[0], Invariant), Order: order, Line: string.Join(',', parts));
            }).OrderBy(row => row.Time).ThenBy(row => row.Order).Select(row => row.Line).ToArray();
    }

    private static string NoteSignature(ChartDocument document) => JsonSerializer.Serialize(document.Notes.Where(n => !n.IsDraft)
        .Select(n => new { n.Lane, Beat = n.Beat.Value, End = n.EndBeat?.Value, n.Volume, n.Sound }));
    private static string TimingSignature(ChartDocument document) => JsonSerializer.Serialize(document.Timing
        .Select(t => new { Beat = t.Beat.Value, t.Bpm, t.Delay, t.Effect, t.EffectValue,
            Settings = t.UnknownData?[OsuTimingPointSettings.Key], RequestedTime = t.UnknownData?["_sme_requested_timing_ms"] }));
    private static bool Number(string value, out double number) => double.TryParse(value, NumberStyles.Float, Invariant, out number) && double.IsFinite(number);
    private static bool IsTiming(string line) { var parts = line.Split(','); return parts.Length >= 2 && Number(parts[0], out _) && Number(parts[1], out _); }
    private static bool IsBackground(string line) => line.StartsWith("0,0,", StringComparison.Ordinal) || line.StartsWith("Background,", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> DataLines(string text, string section)
    {
        var current = "";
        foreach (var line in Lines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) current = trimmed[1..^1];
            else if (current.Equals(section, StringComparison.OrdinalIgnoreCase) && trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal)) yield return trimmed;
        }
    }
    private static string[] Lines(string text) => Regex.Split(text, "(?<=\\n)");
    private static string Eol(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    private static string Section(string line) { var value = line.Trim(); return value.StartsWith('[') && value.EndsWith(']') ? value[1..^1] : ""; }

    private static string PatchPairs(string text, string section, Dictionary<string, string> changes)
    {
        var lines = Lines(text).ToList();
        var start = lines.FindIndex(l => Section(l).Equals(section, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return text.TrimEnd('\r', '\n') + Eol(text) + $"[{section}]" + Eol(text) + string.Join(Eol(text), changes.Select(p => p.Key + ":" + p.Value)) + Eol(text);
        var end = start + 1;
        while (end < lines.Count && Section(lines[end]).Length == 0) end++;
        var missing = new Dictionary<string, string>(changes, StringComparer.OrdinalIgnoreCase);
        for (var i = start + 1; i < end; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon < 0 || !changes.TryGetValue(lines[i][..colon].Trim(), out var value)) continue;
            var ending = lines[i].EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : lines[i].EndsWith('\n') ? "\n" : "";
            var prefix = lines[i][..(colon + 1)];
            if (colon + 1 < lines[i].Length && lines[i][colon + 1] == ' ') prefix += " ";
            lines[i] = prefix + value + ending;
            missing.Remove(lines[i][..colon].Trim());
        }
        if (missing.Count > 0)
        {
            if (end > 0 && !lines[end - 1].EndsWith('\n')) lines[end - 1] += Eol(text);
            lines.InsertRange(end, missing.Select(p => p.Key + ":" + p.Value + Eol(text)));
        }
        return string.Concat(lines);
    }

    private static string ReplaceRows(string text, string section, HashSet<string> known, string[] rows)
    {
        var result = new List<string>();
        var active = false; var seen = false; var inserted = false;
        void Insert() { if (inserted) return; if (result.Count > 0 && !result[^1].EndsWith('\n')) result[^1] += Eol(text); result.AddRange(rows.Select(r => r + Eol(text))); inserted = true; }
        foreach (var line in Lines(text))
        {
            var name = Section(line);
            if (name.Length > 0)
            {
                if (active) Insert();
                active = name.Equals(section, StringComparison.OrdinalIgnoreCase);
                seen |= active;
            }
            if (active && known.Contains(line.Trim())) { Insert(); continue; }
            result.Add(line);
        }
        if (!seen) { if (result.Count > 0 && !result[^1].EndsWith('\n')) result[^1] += Eol(text); result.Add($"[{section}]" + Eol(text)); }
        if (active || !seen) Insert();
        return string.Concat(result);
    }
}

using LumaChart.Core.Localization;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Core.IO;

public static class MalodyMcSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Default)
    {
        // Match Malody's canonical export shape: compact UTF-8 JSON with no
        // formatting-only expansion in either standalone .mc or .mcz entries.
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static ChartDocument Load(string path)
    {
        var document = Parse(File.ReadAllText(path), path);
        if (!string.IsNullOrWhiteSpace(document.Metadata.AudioFile)) return document;
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var audioFiles = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(file => AudioExtensions.Contains(Path.GetExtension(file))).Take(2).ToArray();
            if (audioFiles.Length == 1) document.Metadata.AudioFile = Path.GetFileName(audioFiles[0]);
        }
        catch { }
        return document;
    }

    public static ChartDocument Parse(string json, string? path = null) =>
        Parse(json, path, Environment.UserName);

    public static ChartDocument Parse(string json, string? path, string defaultCreator) =>
        Parse(json, path, defaultCreator, out _);

    /// <summary>
    /// Parses a Malody key chart. A record whose beat or type cannot be read no
    /// longer aborts the whole chart: bad playable notes and effects are kept
    /// verbatim as unknown data (so a save does not delete them) and bad BPM or
    /// draft records are dropped. <paramref name="skippedRecordCount"/> reports
    /// how many records were not imported as editable objects.
    /// </summary>
    public static ChartDocument Parse(string json, string? path, string defaultCreator, out int skippedRecordCount)
    {
        ArgumentNullException.ThrowIfNull(defaultCreator);
        var skipped = 0;
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Invalid .mc JSON.");
        var meta = root["meta"] as JsonObject ?? [];
        var mode = ReadInt(meta, "mode", 0);
        if (mode != 0)
            throw new NotSupportedException(
                LocalizationCatalog.Format("该 Malody 谱面是 mode {0}，不是下落式 Key 模式。LumaChart 只支持固定轨道键音谱面。", mode));
        var song = meta["song"] as JsonObject ?? [];
        var modeExt = meta["mode_ext"] as JsonObject ?? [];
        var document = new ChartDocument
        {
            FilePath = path,
            KeyCount = Math.Clamp(ReadInt(modeExt, "column", 4), 1, 18),
            Metadata = new ChartMetadata
            {
                Title = ReadString(song, "title", "Untitled"),
                TitleOriginal = ReadString(song, "titleorg", ""),
                Artist = ReadString(song, "artist", "Unknown Artist"),
                ArtistOriginal = ReadString(song, "artistorg", ""),
                Creator = ReadString(meta, "creator", defaultCreator),
                Version = ReadString(meta, "version", "Normal"),
                // Resource references may only name files inside the chart folder.
                AudioFile = ChartResourceReference.SanitizeImported(ReadString(song, "file", "")),
                CoverFile = ChartResourceReference.SanitizeImported(ReadString(meta, "cover", "")),
                BackgroundFile = ChartResourceReference.SanitizeImported(ReadString(meta, "background", "")),
                PreviewTime = ReadInt(meta, "preview", 0),
                Bpm = ReadDouble(song, "bpm", double.NaN)
            },
            Timing = [], Notes = [], UnknownRoot = [],
            UnknownMeta = Without(meta, "creator", "cover", "background", "version", "preview", "mode", "song", "mode_ext"),
            UnknownSong = Without(song, "title", "titleorg", "artist", "artistorg", "file", "bpm"),
            // bar_begin stays with the unknown mode_ext data; Serialize used to
            // overwrite every authored value with 0.
            UnknownModeExt = Without(modeExt, "column")
        };

        foreach (var pair in root)
            if (pair.Key is not ("meta" or "time" or "effect" or "note" or "noteref" or "_luma_draft_notes")) document.UnknownRoot[pair.Key] = pair.Value?.DeepClone();

        if (root["time"] is JsonArray times)
        {
            foreach (var item in times.OfType<JsonObject>())
            {
                if (!TryReadBeat(item["beat"], out var timingBeat))
                {
                    skipped++;
                    continue;
                }
                document.Timing.Add(new TimingEvent
                {
                    Beat = timingBeat,
                    Bpm = ReadDouble(item, "bpm", 120),
                    Delay = ReadDouble(item, "delay", 0),
                    UnknownData = OptionalUnknown(item, "beat", "bpm", "delay")
                });
            }
        }

        if (root["effect"] is JsonArray effects)
        {
            foreach (var item in effects.OfType<JsonObject>())
            {
                if (!TryReadBeat(item["beat"], out var beat))
                {
                    document.UnknownEffects.Add(item.DeepClone().AsObject());
                    skipped++;
                    continue;
                }
                // endbeat belongs to the effect record, not to one numeric field.
                // Keep it on every imported field of this record so a save writes
                // it back instead of silently turning a ranged effect into a point.
                var endBeat = item["endbeat"];
                var foundEffect = false;
                foreach (var pair in item)
                {
                    var name = pair.Key;
                    if (name is "beat" or "endbeat" || pair.Value is not JsonValue value ||
                        !value.TryGetValue<double>(out var number) || !double.IsFinite(number)) continue;
                    var kind = OfficialEffectNames.TryGetValue(name, out var officialKind)
                        ? officialKind
                        : TimingEffectKind.Custom;
                    document.Timing.Add(new TimingEvent
                    {
                        Beat = beat,
                        Effect = kind,
                        EffectFieldName = kind == TimingEffectKind.Custom ? name : string.Empty,
                        EffectValue = number,
                        EffectValueText = value.ToJsonString(),
                        UnknownData = endBeat is null ? null : new JsonObject { ["endbeat"] = endBeat.DeepClone() }
                    });
                    foundEffect = true;
                }
                if (!foundEffect)
                    document.UnknownEffects.Add(item.DeepClone().AsObject());
                else
                {
                    var preserved = item.DeepClone().AsObject();
                    foreach (var pair in item.Where(pair => pair.Key is not ("beat" or "endbeat") &&
                                 pair.Value is JsonValue value && value.TryGetValue<double>(out _)).ToArray())
                        preserved.Remove(pair.Key);
                    if (preserved.Any(pair => pair.Key is not ("beat" or "endbeat")))
                        document.UnknownEffects.Add(preserved);
                }
            }
        }

        if (root["note"] is JsonArray notes)
        {
            var playableColumns = notes.OfType<JsonObject>()
                .Where(item =>
                    item["column"] is not null &&
                    !(ReadInt(item, "type", 0) == 1 && item["sound"] is JsonValue))
                .Select(item => ReadInt(item, "column", int.MinValue))
                .ToArray();
            // Official Malody columns are zero-based. Only migrate the old one-based
            // variant when the data proves it by using the KeyCount column; a valid
            // sparse chart is allowed to have no notes in column zero.
            var usesLegacyOneBasedColumns = playableColumns.Length > 0 &&
                !playableColumns.Contains(0) &&
                playableColumns.Contains(document.KeyCount) &&
                playableColumns.All(column => column >= 1 && column <= document.KeyCount);
            JsonObject? backgroundMusic = null;
            JsonObject? preservedBackgroundMusic = null;
            foreach (var item in notes.OfType<JsonObject>())
            {
                var isBackgroundMusic =
                    ReadInt(item, "type", 0) == 1 &&
                    item["sound"] is JsonValue;
                if (isBackgroundMusic || item["column"] is null)
                {
                    var preserved = item.DeepClone().AsObject();
                    document.UnknownNotes.Add(preserved);
                    if (backgroundMusic == null && isBackgroundMusic)
                    {
                        backgroundMusic = item;
                        preservedBackgroundMusic = preserved;
                    }
                    continue;
                }
                if (!TryReadNote(item, out var note))
                {
                    // Keep the unreadable record verbatim; it round-trips but is
                    // not editable.
                    document.UnknownNotes.Add(item.DeepClone().AsObject());
                    skipped++;
                    continue;
                }
                note.Lane = ReadInt(item, "column", 0) - (usesLegacyOneBasedColumns ? 1 : 0);
                document.Notes.Add(note);
            }
            if (backgroundMusic != null)
            {
                if (string.IsNullOrWhiteSpace(document.Metadata.AudioFile))
                    document.Metadata.AudioFile = ChartResourceReference.SanitizeImported(
                        ReadString(backgroundMusic, "sound", ""));
                if (backgroundMusic["offset"] is JsonValue offsetValue &&
                    offsetValue.TryGetValue<double>(out var musicOffset) &&
                    double.IsFinite(musicOffset))
                {
                    // The record TimingMap uses for the song offset, not merely the
                    // first BPM row in file order. When the first BPM starts after
                    // beat 0 its Delay would be a section pause, so a non-zero BGM
                    // offset gets an explicit beat-0 record instead.
                    var offsetTiming = TimingMap.FindSongOffsetTiming(document.Timing);
                    if (offsetTiming == null && Math.Abs(musicOffset) > .000001)
                        offsetTiming = document.GetOrCreateSongOffsetTiming();
                    if (offsetTiming != null && Math.Abs(offsetTiming.Delay) < .000001 && Math.Abs(musicOffset) > .000001)
                        offsetTiming.Delay = musicOffset;
                    // When the BGM offset is the song offset (Malody stores it as
                    // an integer), it must follow later Offset edits and rate
                    // exports. Dropping the stored copy makes Serialize regenerate
                    // it from the current Delay; a genuinely independent offset is
                    // kept verbatim.
                    if (Math.Abs(musicOffset - (offsetTiming?.Delay ?? 0)) <= .5 + 1e-9)
                        preservedBackgroundMusic!.Remove("offset");
                }
            }
        }

        var draftNotes = root["noteref"] as JsonArray ?? root["_luma_draft_notes"] as JsonArray;
        if (draftNotes != null)
        {
            foreach (var item in draftNotes.OfType<JsonObject>())
            {
                if (!TryReadNote(item, out var draft))
                {
                    skipped++;
                    continue;
                }
                draft.IsDraft = true;
                draft.DraftColumn = ReadInt(item, "column", -1);
                document.Notes.Add(draft);
            }
        }

        document.Normalize();
        skippedRecordCount = skipped;
        return document;
    }

    private static bool TryReadNote(JsonObject item, out ChartNote note)
    {
        note = new ChartNote();
        BeatPosition endBeat = default;
        if (!TryReadBeat(item["beat"], out var beat) ||
            (item["endbeat"] is JsonArray && !TryReadBeat(item["endbeat"], out endBeat)))
            return false;

        // A non-text "sound" (for example 1) is not a key sound. Keep it as
        // unknown data so it round-trips instead of failing the whole chart.
        var hasTextSound = item["sound"] is JsonValue soundValue && soundValue.TryGetValue<string>(out _);
        var sound = hasTextSound
            ? ChartResourceReference.SanitizeImported(item["sound"]!.GetValue<string>())
            : string.Empty;
        note = new ChartNote
        {
            Beat = beat,
            EndBeat = item["endbeat"] is JsonArray ? endBeat : null,
            Sound = sound.Length > 0 ? sound : null,
            Volume = ReadInt(item, "vol", 100),
            Group = Math.Clamp(ReadInt(item, "group", 0), 0, ushort.MaxValue),
            UnknownData = hasTextSound
                ? OptionalUnknown(item, "beat", "column", "endbeat", "sound", "vol", "group")
                : OptionalUnknown(item, "beat", "column", "endbeat", "vol", "group")
        };
        return true;
    }

    public static void Save(ChartDocument document, string path)
    {
        document.Normalize();
        File.WriteAllText(path, Serialize(document));
        document.FilePath = path;
    }

    public static string Serialize(ChartDocument document)
    {
        var unknownRoot = document.UnknownRoot.DeepClone().AsObject();
        var extra = unknownRoot.TryGetPropertyValue("extra", out var preservedExtra)
            ? preservedExtra?.DeepClone()
            : null;
        unknownRoot.Remove("extra");
        // Keep the same top-level envelope/order emitted by Malody itself:
        // meta, time, effect (optional), note, extension data, extra.
        var root = new JsonObject();
        var modeExt = document.UnknownModeExt.DeepClone().AsObject();
        var barBegin = modeExt["bar_begin"]?.DeepClone();
        modeExt.Remove("bar_begin");
        modeExt["column"] = document.KeyCount;
        modeExt["bar_begin"] = barBegin ?? JsonValue.Create(0);
        var song = document.UnknownSong.DeepClone().AsObject();
        song["title"] = document.Metadata.Title;
        song["titleorg"] = document.Metadata.TitleOriginal;
        song["artist"] = document.Metadata.Artist;
        song["artistorg"] = document.Metadata.ArtistOriginal;
        song["file"] = document.Metadata.AudioFile;
        song["bpm"] = document.Metadata.Bpm;
        var meta = document.UnknownMeta.DeepClone().AsObject();
        if (!meta.ContainsKey("id")) meta["id"] = 0;
        if (!meta.ContainsKey("aimode")) meta["aimode"] = "";
        meta["creator"] = document.Metadata.Creator;
        meta["cover"] = document.Metadata.CoverFile;
        meta["background"] = document.Metadata.BackgroundFile;
        meta["version"] = document.Metadata.Version;
        meta["preview"] = document.Metadata.PreviewTime;
        meta["mode"] = 0;
        meta["mode_ext"] = modeExt;
        meta["song"] = song;
        root["meta"] = meta;

        root["time"] = new JsonArray(document.Timing.Where(t => t.Bpm.HasValue).OrderBy(t => t.Beat).Select(t =>
        {
            var item = t.UnknownData?.DeepClone().AsObject() ?? [];
            item["beat"] = WriteBeat(t.Beat);
            item["bpm"] = t.Bpm;
            item["delay"] = t.Delay;
            return (JsonNode)item;
        }).ToArray());

        var effectNodes = document.Timing.Where(t => t.Effect.HasValue).OrderBy(t => t.Beat).Select(t =>
        {
            var name = EffectNumberText.CanonicalName(t.Effect!.Value, t.EffectFieldName);
            var number = EffectNumberText.TryParse(t.EffectValueText, out _)
                ? JsonNode.Parse(t.EffectValueText.Trim())
                : JsonValue.Create(t.EffectValue);
            var effect = new JsonObject { ["beat"] = WriteBeat(t.Beat) };
            // A combined BPM/Effect row keeps Malody time data in UnknownData;
            // only an effect-only row carries the imported effect endbeat.
            if (!t.Bpm.HasValue && t.UnknownData?["endbeat"] is { } endBeat)
                effect["endbeat"] = endBeat.DeepClone();
            effect[name] = number;
            return (JsonNode)effect;
        }).Concat(document.UnknownEffects.Select(e => (JsonNode)e.DeepClone())).ToArray();
        if (effectNodes.Length > 0) root["effect"] = new JsonArray(effectNodes); else root.Remove("effect");

        var officialNotes = document.Notes.Where(n => !n.IsDraft).OrderBy(n => n.Beat).ThenBy(n => n.Lane).Select(note =>
        {
            var obj = note.UnknownData?.DeepClone().AsObject() ?? [];
            obj["beat"] = WriteBeat(note.Beat);
            obj["column"] = note.Lane;
            if (note.EndBeat.HasValue) obj["endbeat"] = WriteBeat(note.EndBeat.Value);
            if (!string.IsNullOrWhiteSpace(note.Sound)) obj["sound"] = note.Sound;
            if (note.Volume != 100) obj["vol"] = note.Volume;
            if (note.Group != 0) obj["group"] = note.Group;
            return (JsonNode)obj;
        }).ToList();
        var preservedNotes = document.UnknownNotes.Select(note => note.DeepClone().AsObject()).ToList();
        var backgroundMusic = preservedNotes.FirstOrDefault(note => ReadInt(note, "type", 0) == 1);
        if (!string.IsNullOrWhiteSpace(document.Metadata.AudioFile))
        {
            backgroundMusic ??= new JsonObject { ["beat"] = WriteBeat(BeatPosition.Zero), ["type"] = 1 };
            if (!preservedNotes.Contains(backgroundMusic)) preservedNotes.Add(backgroundMusic);
            backgroundMusic["sound"] = document.Metadata.AudioFile;
            var timingOffset = TimingMap.FindSongOffsetTiming(document.Timing)?.Delay ?? 0;
            var preservedOffset = ReadDouble(backgroundMusic, "offset", timingOffset);
            backgroundMusic["offset"] = ToMalodyOffset(
                double.IsFinite(preservedOffset) ? preservedOffset : timingOffset);
        }
        root["note"] = new JsonArray(officialNotes.Concat(preservedNotes.Select(note => (JsonNode)note)).ToArray());
        var draftNodes = document.Notes.Where(n => n.IsDraft).OrderBy(n => n.Beat).ThenBy(n => n.DraftColumn).Select(note =>
        {
            var obj = note.UnknownData?.DeepClone().AsObject() ?? [];
            obj["beat"] = WriteBeat(note.Beat);
            obj["column"] = note.DraftColumn;
            if (note.EndBeat.HasValue) obj["endbeat"] = WriteBeat(note.EndBeat.Value);
            if (!string.IsNullOrWhiteSpace(note.Sound)) obj["sound"] = note.Sound;
            if (note.Volume != 100) obj["vol"] = note.Volume;
            if (note.Group != 0) obj["group"] = note.Group;
            return (JsonNode)obj;
        }).ToArray();
        root.Remove("_luma_draft_notes");
        if (draftNodes.Length > 0) root["noteref"] = new JsonArray(draftNodes); else root.Remove("noteref");
        foreach (var pair in unknownRoot)
            root[pair.Key] = pair.Value?.DeepClone();
        root["extra"] = extra;
        return root.ToJsonString(Options);
    }

    private static int ToMalodyOffset(double value) => (int)Math.Clamp(
        Math.Round(value, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);

    private static readonly Dictionary<string, TimingEffectKind> OfficialEffectNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["scroll"] = TimingEffectKind.Scroll, ["jump"] = TimingEffectKind.Jump,
        ["hs"] = TimingEffectKind.HS, ["sign"] = TimingEffectKind.Sign
    };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".mp3", ".wav", ".flac", ".m4a", ".aac"
    };

    /// <summary>
    /// Reads a Malody [bar, numerator, denominator] beat. A missing beat keeps
    /// the historical beat-0 fallback; integral decimals such as 2.0 are
    /// accepted. Wrong types, a zero denominator or an expression that cannot
    /// be represented fail this record only instead of the whole chart.
    /// </summary>
    private static bool TryReadBeat(JsonNode? node, out BeatPosition beat)
    {
        beat = BeatPosition.Zero;
        if (node is null) return true;
        if (node is not JsonArray array || array.Count < 3 ||
            !TryReadInteger(array[0], 0, out var bar) ||
            !TryReadInteger(array[1], 0, out var numerator) ||
            !TryReadInteger(array[2], 1, out var denominator) ||
            denominator == 0)
            return false;
        try
        {
            beat = Math.Abs((long)denominator) <= BeatPosition.MaximumPreservedDenominator
                ? BeatPosition.Preserve(bar, numerator, denominator)
                : new BeatPosition(bar, numerator, denominator);
            return beat.Denominator <= BeatPosition.MaximumPreservedDenominator;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadInteger(JsonNode? node, int fallback, out int value)
    {
        value = fallback;
        if (node is null) return true;
        if (node is not JsonValue json) return false;
        if (json.TryGetValue(out value)) return true;
        if (json.TryGetValue<double>(out var number) && double.IsFinite(number) &&
            number == Math.Round(number) && number is >= int.MinValue and <= int.MaxValue)
        {
            value = (int)number;
            return true;
        }
        value = fallback;
        return false;
    }

    private static JsonArray WriteBeat(BeatPosition beat) => [beat.Bar, beat.Numerator, beat.Denominator];
    private static string ReadString(JsonObject obj, string name, string fallback) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    private static int ReadInt(JsonObject obj, string name, int fallback) =>
        obj[name] is JsonValue && TryReadInteger(obj[name], fallback, out var value) ? value : fallback;
    private static double ReadDouble(JsonObject obj, string name, double fallback) => obj[name] is JsonValue v && v.TryGetValue<double>(out var value) ? value : fallback;

    private static JsonObject Without(JsonObject source, params string[] knownNames)
    {
        var result = source.DeepClone().AsObject();
        foreach (var name in knownNames) result.Remove(name);
        return result;
    }

    private static JsonObject? OptionalUnknown(JsonObject source, params string[] knownNames)
    {
        var result = Without(source, knownNames);
        return result.Count == 0 ? null : result;
    }
}

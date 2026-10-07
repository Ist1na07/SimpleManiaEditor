using LumaChart.Core.Localization;
using System.Globalization;
using System.Text;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Core.IO;

public static class OsuManiaSerializer
{
    private const string LumaChartBpmMetadataKey = "LumaChartBPM";
    // Bounds the lead-in used for objects before the first red line so a corrupt
    // timestamp cannot push beat numbers beyond the int bar range.
    private const double MaximumLeadInBars = 100_000;
    private const double FirstRedLineRoundingToleranceMs = 1;

    public static ChartDocument Load(string path) =>
        Load(path, OsuNoteTimingImportMode.PreserveMilliseconds);

    public static ChartDocument Load(string path, OsuNoteTimingImportMode noteTimingMode) =>
        Parse(File.ReadAllText(path), path, Environment.UserName, noteTimingMode);

    public static ChartDocument Parse(string text, string? path = null) =>
        Parse(text, path, Environment.UserName, OsuNoteTimingImportMode.PreserveMilliseconds);

    public static ChartDocument Parse(
        string text,
        OsuNoteTimingImportMode noteTimingMode,
        string? path = null) =>
        Parse(text, path, Environment.UserName, noteTimingMode);

    public static ChartDocument Parse(string text, string? path, string defaultCreator) =>
        Parse(text, path, defaultCreator, OsuNoteTimingImportMode.PreserveMilliseconds);

    public static ChartDocument Parse(
        string text,
        string? path,
        string defaultCreator,
        OsuNoteTimingImportMode noteTimingMode)
    {
        ArgumentNullException.ThrowIfNull(defaultCreator);
        if (!Enum.IsDefined(noteTimingMode))
            throw new ArgumentOutOfRangeException(nameof(noteTimingMode));
        var sections = ReadSections(text);
        var general = ReadPairs(sections.GetValueOrDefault("General"));
        var mode = ParseInt(general.GetValueOrDefault("Mode"), -1);
        if (mode != 3)
            throw new NotSupportedException(
                mode < 0
                    ? "该 osu! 谱面没有有效的 Mode 字段；LumaChart 只支持 osu!mania（Mode: 3）。"
                    : LocalizationCatalog.Format("该 osu! 谱面是 Mode: {0}，不是 osu!mania。LumaChart 只支持固定轨道键音谱面。", mode));
        var metadata = ReadPairs(sections.GetValueOrDefault("Metadata"));
        var difficulty = ReadPairs(sections.GetValueOrDefault("Difficulty"));
        var keyCount = Math.Clamp(ParseCircleSize(difficulty.GetValueOrDefault("CircleSize")), 1, 18);
        var rawTiming = new List<(double Ms, double Bpm, int Order)>();
        var rawScroll = new List<(double Ms, double Multiplier, int Order)>();
        var timingPointOrder = 0;
        foreach (var line in sections.GetValueOrDefault("TimingPoints") ?? [])
        {
            var parts = line.Split(',');
            var order = timingPointOrder++;
            if (parts.Length < 2 || !TryDouble(parts[0], out var ms) || !double.IsFinite(ms) ||
                !TryDouble(parts[1], out var beatLength) || !double.IsFinite(beatLength)) continue;
            var uninherited = parts.Length < 7 || parts[6].Trim() != "0";
            var value = uninherited ? 60_000d / beatLength : -100d / beatLength;
            if (!double.IsFinite(value)) continue;
            // A very fast red line used to be dropped, which re-timed every later
            // red line with the previous BPM. Clamping keeps each later red line
            // at its own timestamp because beats below are integrated from the
            // same clamped value that TimingMap plays.
            if (uninherited && beatLength > 0)
                rawTiming.Add((ms, Math.Min(value, ChartDocument.MaximumTimingBpm), order));
            else if (!uninherited && beatLength < 0) rawScroll.Add((ms, value, order));
        }
        if (rawTiming.Count == 0) rawTiming.Add((0, 120, -1));
        rawTiming = rawTiming.OrderBy(point => point.Ms).ThenBy(point => point.Order).ToList();
        // osu permits multiple uninherited timing records at one timestamp. The
        // final record is authoritative. Collapse them before converting the first
        // timestamp into Malody delay; otherwise a later same-beat BPM with delay 0
        // discards a non-zero song offset in TimingMap.
        var collapsedTiming = new List<(double Ms, double Bpm, int Order)>(rawTiming.Count);
        foreach (var point in rawTiming)
        {
            if (collapsedTiming.Count > 0 && Math.Abs(collapsedTiming[^1].Ms - point.Ms) < 1e-7)
                collapsedTiming[^1] = point;
            else
                collapsedTiming.Add(point);
        }
        rawTiming = collapsedTiming;
        rawScroll = rawScroll.OrderBy(point => point.Ms).ThenBy(point => point.Order).ToList();
        var collapsedScroll = new List<(double Ms, double Multiplier, int Order)>(rawScroll.Count);
        foreach (var point in rawScroll)
        {
            if (collapsedScroll.Count > 0 && Math.Abs(collapsedScroll[^1].Ms - point.Ms) < 1e-7)
                collapsedScroll[^1] = point;
            else
                collapsedScroll.Add(point);
        }
        rawScroll = collapsedScroll;

        var document = new ChartDocument
        {
            FilePath = path,
            KeyCount = keyCount,
            Metadata = new ChartMetadata
            {
                Title = metadata.GetValueOrDefault("Title") ?? "Untitled",
                TitleOriginal = metadata.GetValueOrDefault("TitleUnicode") ?? "",
                Artist = metadata.GetValueOrDefault("Artist") ?? "Unknown Artist",
                ArtistOriginal = metadata.GetValueOrDefault("ArtistUnicode") ?? "",
                Creator = metadata.GetValueOrDefault("Creator") ?? defaultCreator,
                Version = metadata.GetValueOrDefault("Version") ?? "Normal",
                AudioFile = ChartResourceReference.SanitizeImported(general.GetValueOrDefault("AudioFilename")),
                BackgroundFile = ChartResourceReference.SanitizeImported(ReadBackground(sections.GetValueOrDefault("Events"))),
                // osu! uses -1 as an unspecified preview sentinel. Carrying it
                // into the MC editor creates a value rejected by the save form.
                PreviewTime = Math.Max(0, ParseInt(general.GetValueOrDefault("PreviewTime"), 0)),
                // osu! has no standard chart-level BPM metadata field. Keep an
                // ignorable LumaChart extension so the independent editor value
                // survives our own save/reopen cycle; legacy files still derive
                // it from the first uninherited timing point.
                Bpm = ParseChartBpm(metadata.GetValueOrDefault(LumaChartBpmMetadataKey), rawTiming[0].Bpm),
                OverallDifficulty = Math.Clamp(ParseDouble(difficulty.GetValueOrDefault("OverallDifficulty"), 8), 0, 10),
                HpDrainRate = Math.Clamp(ParseDouble(difficulty.GetValueOrDefault("HPDrainRate"), 8), 0, 10)
            },
            Timing = [], Notes = []
        };

        // Hit objects are stored as whole milliseconds while red lines are not,
        // so an object written on a fractional first red line (for example at
        // -321.5 ms) reads back up to half a millisecond early. That is the red
        // line itself, not a lead-in: map it there instead of onto a negative beat.
        double OnFirstRedLine(double ms) =>
            ms < rawTiming[0].Ms && ms >= rawTiming[0].Ms - FirstRedLineRoundingToleranceMs ? rawTiming[0].Ms : ms;
        rawScroll = rawScroll.Select(point => (OnFirstRedLine(point.Ms), point.Multiplier, point.Order)).ToList();
        var hitObjects = new List<(string[] Parts, double Ms)>();
        foreach (var line in sections.GetValueOrDefault("HitObjects") ?? [])
        {
            var parts = line.Split(',');
            if (parts.Length < 6 || !TryDouble(parts[2], out var ms) || !double.IsFinite(ms)) continue;
            hitObjects.Add((parts, OnFirstRedLine(ms)));
        }

        // osu! times objects and SV points before the first red line with that
        // red line's BPM extended backward. Mapping them onto negative beats let
        // Normalize delete them, so beat 0 moves back by whole 4/4 bars of the
        // first BPM (keeping bar lines in phase) and a BPM record at beat 0 carries
        // the new offset. The source red line stays at its own timestamp.
        var firstBeatLength = 60_000d / rawTiming[0].Bpm;
        var earliestMs = hitObjects.Select(item => item.Ms)
            .Concat(rawScroll.Select(point => point.Ms))
            .DefaultIfEmpty(rawTiming[0].Ms)
            .Min();
        var leadInBars = earliestMs < rawTiming[0].Ms
            ? Math.Ceiling((rawTiming[0].Ms - earliestMs) / (4 * firstBeatLength) - 1e-9)
            : 0;
        if (!double.IsFinite(leadInBars) || leadInBars > MaximumLeadInBars) leadInBars = 0;
        var leadInBeats = (int)leadInBars * 4;
        var zeroBeatMs = rawTiming[0].Ms - leadInBeats * firstBeatLength;
        if (leadInBeats > 0)
            document.Timing.Add(new TimingEvent
            {
                Beat = BeatPosition.Zero,
                Bpm = rawTiming[0].Bpm,
                Delay = -zeroBeatMs
            });

        var redLineBeats = new BeatPosition[rawTiming.Count];
        var anchorBeat = BeatPosition.Zero;
        var anchorMs = zeroBeatMs;
        var anchorBpm = rawTiming[0].Bpm;
        for (var i = 0; i < rawTiming.Count; i++)
        {
            // Integrate from the previous *stored* (1/1920-rounded) red line and
            // the time TimingMap will actually give it. Each rounding residual is
            // then absorbed by the next segment instead of accumulating across
            // many BPM changes.
            var beat = i == 0
                ? new BeatPosition(leadInBeats, 0, 1)
                : BeatPosition.FromDouble(anchorBeat.Value + (rawTiming[i].Ms - anchorMs) * anchorBpm / 60_000d);
            anchorMs += (beat.Value - anchorBeat.Value) * 60_000d / anchorBpm;
            anchorBeat = beat;
            anchorBpm = rawTiming[i].Bpm;
            redLineBeats[i] = beat;
            document.Timing.Add(new TimingEvent
            {
                Beat = beat,
                Bpm = rawTiming[i].Bpm,
                // osu stores the audio timestamp of beat zero directly, while
                // Malody stores the same displacement with the opposite sign.
                Delay = i == 0 && leadInBeats == 0 ? -rawTiming[i].Ms : 0
            });
        }

        var map = new TimingMap(document.Timing);
        // An osu! red line resets the scroll multiplier to 1.0 unless a green line
        // shares its timestamp; Malody Scroll persists across BPM changes. Model
        // the reset explicitly so the imported chart keeps its osu! scroll speed.
        var scrollPoints = rawScroll
            .Select(point => (point.Ms, point.Multiplier, RedLineIndex: -1))
            .ToList();
        var scrollIndex = 0;
        var activeMultiplier = 1d;
        for (var i = 0; i < rawTiming.Count; i++)
        {
            while (scrollIndex < rawScroll.Count && rawScroll[scrollIndex].Ms < rawTiming[i].Ms - 1e-7)
                activeMultiplier = rawScroll[scrollIndex++].Multiplier;
            var sharesGreenLine = scrollIndex < rawScroll.Count &&
                                  Math.Abs(rawScroll[scrollIndex].Ms - rawTiming[i].Ms) < 1e-7;
            if (!sharesGreenLine && activeMultiplier != 1)
                scrollPoints.Add((rawTiming[i].Ms, 1d, i));
            activeMultiplier = 1;
        }
        foreach (var scroll in scrollPoints.OrderBy(point => point.Ms))
        {
            var rawBeat = scroll.RedLineIndex >= 0 ? redLineBeats[scroll.RedLineIndex] : map.MillisecondsToBeat(scroll.Ms);
            document.Timing.Add(new TimingEvent
            {
                Beat = scroll.RedLineIndex < 0 &&
                       noteTimingMode == OsuNoteTimingImportMode.CalibrateCommonSubdivisions
                    ? OsuBeatCalibrator.Calibrate(scroll.Ms, rawBeat, map).Beat
                    : rawBeat,
                Effect = TimingEffectKind.Scroll,
                EffectValue = scroll.Multiplier
            });
        }
        foreach (var (parts, ms) in hitObjects)
        {
            var x = ParseInt(parts[0], 0);
            var type = ParseInt(parts[3], 1);
            var lane = Math.Clamp((int)((long)x * keyCount / 512), 0, keyCount - 1);
            var rawBeat = map.MillisecondsToBeat(ms);
            var noteBeat = noteTimingMode == OsuNoteTimingImportMode.CalibrateCommonSubdivisions
                ? OsuBeatCalibrator.Calibrate(ms, rawBeat, map).Beat
                : rawBeat;
            BeatPosition? end = null;
            BeatPosition? rawEnd = null;
            if ((type & 128) != 0 && parts[5].Split(':')[0] is var endText &&
                TryDouble(endText, out var endMs) && double.IsFinite(endMs))
            {
                rawEnd = map.MillisecondsToBeat(endMs);
                end = noteTimingMode == OsuNoteTimingImportMode.CalibrateCommonSubdivisions
                    ? OsuBeatCalibrator.Calibrate(endMs, rawEnd.Value, map).Beat
                    : rawEnd;
            }
            // A very short or malformed LN must never collapse because each endpoint
            // happened to calibrate to the same grid location. In that case retain the
            // original millisecond conversion for both endpoints.
            if (end.HasValue && end.Value.CompareTo(noteBeat) <= 0 &&
                rawEnd.HasValue && rawEnd.Value.CompareTo(rawBeat) > 0)
            {
                noteBeat = rawBeat;
                end = rawEnd;
            }
            var hitSample = (type & 128) != 0 ? string.Join(':', parts[5].Split(':').Skip(1)) : parts[5];
            var sampleParts = hitSample.Split(':', 5);
            // osu uses 0 as "inherit the active timing point sample volume", not mute.
            // The neutral fallback is 100 until timing-point sample-volume editing is exposed.
            var rawVolume = sampleParts.Length > 3 ? Math.Clamp(ParseInt(sampleParts[3], 100), 0, 100) : 100;
            var volume = rawVolume == 0 ? 100 : rawVolume;
            var sound = sampleParts.Length > 4
                ? ChartResourceReference.SanitizeImported(sampleParts[4])
                : string.Empty;
            document.Notes.Add(new ChartNote
            {
                Lane = lane, Beat = noteBeat, EndBeat = end, Volume = volume,
                Sound = sound.Length > 0 ? sound : null
            });
        }
        document.Normalize();
        return document;
    }

    public static void Save(ChartDocument document, string path, double? overallDifficulty = null, double? hpDrainRate = null)
    {
        var map = new TimingMap(document.Timing);
        var requestedOd = overallDifficulty ?? document.Metadata.OverallDifficulty;
        var requestedHp = hpDrainRate ?? document.Metadata.HpDrainRate;
        var od = double.IsFinite(requestedOd) ? Math.Clamp(requestedOd, 0, 10) : 8;
        var hp = double.IsFinite(requestedHp) ? Math.Clamp(requestedHp, 0, 10) : 8;
        var builder = new StringBuilder();
        // Every numeric field is written with the invariant culture: sv-SE, fi
        // and nb format negatives with U+2212 and ar-SA prefixes U+061C, which
        // osu! and this parser reject (for example a note before the first beat).
        var invariant = CultureInfo.InvariantCulture;
        builder.AppendLine("osu file format v14\n");
        builder.AppendLine("[General]");
        builder.AppendLine($"AudioFilename: {CleanValue(document.Metadata.AudioFile)}");
        builder.AppendLine("AudioLeadIn: 0");
        builder.AppendLine(invariant, $"PreviewTime: {document.Metadata.PreviewTime}");
        builder.AppendLine("Countdown: 0");
        builder.AppendLine("SampleSet: Soft");
        builder.AppendLine("StackLeniency: 0.7");
        builder.AppendLine("Mode: 3");
        builder.AppendLine("LetterboxInBreaks: 0");
        builder.AppendLine("WidescreenStoryboard: 0\n");
        builder.AppendLine("[Editor]");
        builder.AppendLine("DistanceSpacing: 1");
        builder.AppendLine("BeatDivisor: 4");
        builder.AppendLine("GridSize: 4");
        builder.AppendLine("TimelineZoom: 1\n");
        builder.AppendLine("[Metadata]");
        builder.AppendLine($"Title:{CleanValue(document.Metadata.Title)}");
        builder.AppendLine($"TitleUnicode:{CleanValue(string.IsNullOrWhiteSpace(document.Metadata.TitleOriginal) ? document.Metadata.Title : document.Metadata.TitleOriginal)}");
        builder.AppendLine($"Artist:{CleanValue(document.Metadata.Artist)}");
        builder.AppendLine($"ArtistUnicode:{CleanValue(string.IsNullOrWhiteSpace(document.Metadata.ArtistOriginal) ? document.Metadata.Artist : document.Metadata.ArtistOriginal)}");
        builder.AppendLine($"Creator:{CleanValue(document.Metadata.Creator)}");
        builder.AppendLine($"Version:{CleanValue(document.Metadata.Version)}");
        builder.AppendLine($"{LumaChartBpmMetadataKey}:{RoundTripNumber(ChartBpmForSave(document))}");
        builder.AppendLine("Source:");
        builder.AppendLine("Tags:LumaChart Studio Malody mania");
        builder.AppendLine("BeatmapID:-1");
        builder.AppendLine("BeatmapSetID:-1\n");
        builder.AppendLine("[Difficulty]");
        builder.AppendLine($"HPDrainRate:{Number(hp)}");
        builder.AppendLine(invariant, $"CircleSize:{document.KeyCount}");
        builder.AppendLine($"OverallDifficulty:{Number(od)}");
        builder.AppendLine("ApproachRate:5");
        builder.AppendLine("SliderMultiplier:1.4");
        builder.AppendLine("SliderTickRate:1\n");
        builder.AppendLine("[Events]");
        builder.AppendLine("//Background and Video events");
        if (!string.IsNullOrWhiteSpace(document.Metadata.BackgroundFile))
            builder.AppendLine($"0,0,\"{CleanValue(document.Metadata.BackgroundFile).Replace('\\', '/').Replace("\"", "")}\",0,0");
        builder.AppendLine("//Break Periods");
        builder.AppendLine("//Storyboard Layer 0 (Background)");
        builder.AppendLine("//Storyboard Layer 1 (Fail)");
        builder.AppendLine("//Storyboard Layer 2 (Pass)");
        builder.AppendLine("//Storyboard Layer 3 (Foreground)");
        builder.AppendLine("//Storyboard Layer 4 (Overlay)");
        builder.AppendLine("//Storyboard Sound Samples\n");
        builder.AppendLine("[TimingPoints]");
        var scrollEvents = document.Timing
            .Where(t => t.Effect == TimingEffectKind.Scroll && double.IsFinite(t.EffectValue))
            .Select((timing, index) => (Timing: timing, Index: index))
            .OrderBy(entry => entry.Timing.Beat)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Timing)
            .ToArray();
        // osu! resets the scroll multiplier to 1.0 at every red line, while Malody
        // Scroll carries across BPM changes. Re-state the active multiplier on a
        // green line at the same timestamp unless a Scroll record is already there.
        var carriedScroll = document.Timing.Where(t => t.Bpm.HasValue)
            .Select(point => (Point: point, Multiplier: ActiveScrollAt(scrollEvents, point.Beat, out var ownBeat),
                OwnBeat: ownBeat))
            .Where(entry => !entry.OwnBeat && entry.Multiplier > 0 && entry.Multiplier != 1);
        var timingLines = document.Timing.Where(t => t.Bpm.HasValue).Select(point => new
        {
            Time = map.BeatToMilliseconds(point.Beat), Order = 0,
            Line = string.Create(invariant,
                $"{Number(map.BeatToMilliseconds(point.Beat))},{Number(60_000d / point.Bpm!.Value)},4,2,1,70,1,0")
        }).Concat(document.Timing.Where(t => t.Effect == TimingEffectKind.Scroll && t.EffectValue > 0).Select(point => new
        {
            Time = map.BeatToMilliseconds(point.Beat), Order = 1,
            Line = string.Create(invariant,
                $"{Number(map.BeatToMilliseconds(point.Beat))},{Number(-100d / point.EffectValue)},4,2,1,70,0,0")
        })).Concat(carriedScroll.Select(entry => new
        {
            Time = map.BeatToMilliseconds(entry.Point.Beat), Order = 1,
            Line = string.Create(invariant,
                $"{Number(map.BeatToMilliseconds(entry.Point.Beat))},{Number(-100d / entry.Multiplier)},4,2,1,70,0,0")
        })).OrderBy(item => item.Time).ThenBy(item => item.Order);
        foreach (var line in timingLines) builder.AppendLine(line.Line);
        builder.AppendLine("\n[HitObjects]");
        foreach (var note in document.Notes.Where(n => !n.IsDraft).OrderBy(n => n.Beat).ThenBy(n => n.Lane))
        {
            var x = Math.Clamp((int)Math.Round((note.Lane + .5) * 512 / document.KeyCount), 0, 511);
            var start = (int)Math.Round(map.BeatToMilliseconds(note.Beat));
            if (note.IsHold)
            {
                var end = (int)Math.Round(map.BeatToMilliseconds(note.EndBeat!.Value));
                builder.AppendLine(invariant, $"{x},192,{start},128,0,{end}:{HitSample(note)}");
            }
            else builder.AppendLine(invariant, $"{x},192,{start},1,0,{HitSample(note)}");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        document.FilePath = path;
    }

    /// <summary>
    /// Raw multiplier of the last Scroll record at or before <paramref name="beat"/>
    /// (1 when none), in stable source order like ScrollSpeedMap.
    /// </summary>
    private static double ActiveScrollAt(IReadOnlyList<TimingEvent> orderedScroll, BeatPosition beat, out bool ownBeat)
    {
        var multiplier = 1d;
        ownBeat = false;
        foreach (var scroll in orderedScroll)
        {
            var order = scroll.Beat.CompareTo(beat);
            if (order > 0) break;
            multiplier = scroll.EffectValue;
            ownBeat = order == 0;
        }
        return multiplier;
    }

    // osu! reads sample volume 0 as "inherit the timing point volume" (70 here),
    // so a muted LumaChart note is written as the quietest explicit volume. The
    // filename is the last ','/':'-separated field; either separator inside it
    // would truncate the line or shift every later field.
    private static string HitSample(ChartNote note) => string.Create(CultureInfo.InvariantCulture,
        $"0:0:0:{Math.Clamp(note.Volume, 1, 100)}:{CleanValue(note.Sound ?? "").Replace(':', '_').Replace(',', '_')}");
    private static string Number(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);
    private static string RoundTripNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string CleanValue(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static double ParseChartBpm(string? text, double fallback)
    {
        if (TryDouble(text, out var value) && double.IsFinite(value) && value is > 0 and <= 10_000)
            return value;
        return fallback;
    }

    private static double ChartBpmForSave(ChartDocument document)
    {
        if (double.IsFinite(document.Metadata.Bpm) && document.Metadata.Bpm is > 0 and <= 10_000)
            return document.Metadata.Bpm;
        var timingBpm = document.Timing.FirstOrDefault(timing =>
            timing.Bpm.HasValue && double.IsFinite(timing.Bpm.Value) && timing.Bpm.Value is > 0 and <= 10_000)?.Bpm;
        return timingBpm ?? 120;
    }

    private static Dictionary<string, List<string>> ReadSections(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1];
                if (!result.TryGetValue(name, out current)) result[name] = current = [];
            }
            else current?.Add(line);
        }
        return result;
    }

    private static Dictionary<string, string> ReadPairs(List<string>? lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines ?? [])
        {
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return result;
    }
    private static string ReadBackground(List<string>? lines)
    {
        foreach (var line in lines ?? [])
        {
            if (!line.StartsWith("0,0,", StringComparison.Ordinal)) continue;
            var firstQuote = line.IndexOf('"');
            var secondQuote = firstQuote < 0 ? -1 : line.IndexOf('"', firstQuote + 1);
            if (firstQuote >= 0 && secondQuote > firstQuote) return line[(firstQuote + 1)..secondQuote];
        }
        return "";
    }
    // Some editors write CircleSize as "7.0"; integer parsing fell back to 4K.
    private static int ParseCircleSize(string? text) =>
        TryDouble(text, out var value) && double.IsFinite(value) &&
        value == Math.Round(value) && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : 4;
    private static bool TryDouble(string? text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    private static int ParseInt(string? text, int fallback) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static double ParseDouble(string? text, double fallback) => TryDouble(text, out var value) ? value : fallback;
}

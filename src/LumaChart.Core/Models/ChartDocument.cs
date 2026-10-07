using System.Text.Json.Nodes;
using LumaChart.Core.Timing;

namespace LumaChart.Core.Models;

public enum TimingEffectKind { Scroll, Jump, HS, Sign, Custom }

public sealed class TimingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BeatPosition Beat { get; set; } = BeatPosition.Zero;
    public double? Bpm { get; set; }
    public double Delay { get; set; }
    public TimingEffectKind? Effect { get; set; }
    public double EffectValue { get; set; } = 1;
    /// <summary>The exact JSON number entered by the mapper (for example 1 versus 1.0).</summary>
    public string EffectValueText { get; set; } = "";
    /// <summary>Field name used when Effect is Custom. Known effects derive their canonical name.</summary>
    public string EffectFieldName { get; set; } = "";
    public JsonObject? UnknownData { get; set; }
    /// <summary>Runtime-only creator identity used by multiplayer rooms.</summary>
    public long OnlineCreatorUserId { get; set; }

    public TimingEvent Clone() => new()
    {
        Id = Id,
        Beat = Beat,
        Bpm = Bpm,
        Delay = Delay,
        Effect = Effect,
        EffectValue = EffectValue,
        EffectValueText = EffectValueText,
        EffectFieldName = EffectFieldName,
        UnknownData = UnknownData?.DeepClone().AsObject()
        , OnlineCreatorUserId = OnlineCreatorUserId
    };
}

public sealed class ChartNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Lane { get; set; }
    public BeatPosition Beat { get; set; } = BeatPosition.Zero;
    public BeatPosition? EndBeat { get; set; }
    public string? Sound { get; set; }
    public int Volume { get; set; } = 100;
    public int Group { get; set; }
    /// <summary>Editor-only scratch note. DraftColumn is relative to the first official lane.</summary>
    public bool IsDraft { get; set; }
    public int DraftColumn { get; set; }
    public JsonObject? UnknownData { get; set; }
    /// <summary>Runtime-only creator identity used by multiplayer rooms.</summary>
    public long OnlineCreatorUserId { get; set; }

    public bool IsHold => EndBeat.HasValue && EndBeat.Value.CompareTo(Beat) > 0;

    public ChartNote Clone() => new()
    {
        Id = Id,
        Lane = Lane,
        Beat = Beat,
        EndBeat = EndBeat,
        Sound = Sound,
        Volume = Volume,
        Group = Group,
        IsDraft = IsDraft,
        DraftColumn = DraftColumn,
        UnknownData = UnknownData?.DeepClone().AsObject()
        , OnlineCreatorUserId = OnlineCreatorUserId
    };
}

public sealed class ChartMetadata
{
    public string Title { get; set; } = "Untitled";
    public string TitleOriginal { get; set; } = "";
    public string Artist { get; set; } = "Unknown Artist";
    public string ArtistOriginal { get; set; } = "";
    public string Creator { get; set; } = "guest";
    public string Version { get; set; } = "Normal";
    public string AudioFile { get; set; } = "";
    public string CoverFile { get; set; } = "";
    public string BackgroundFile { get; set; } = "";
    public int PreviewTime { get; set; }
    /// <summary>Chart-level reference BPM used by editor metadata and speed compensation.</summary>
    public double Bpm { get; set; } = 120;
    /// <summary>osu! difficulty setting retained for osu round-trips and Rework analysis.</summary>
    public double OverallDifficulty { get; set; } = 8;
    /// <summary>osu! HP drain setting retained for osu round-trips.</summary>
    public double HpDrainRate { get; set; } = 8;

    public ChartMetadata Clone() => (ChartMetadata)MemberwiseClone();
}

public sealed class ChartDocument
{
    /// <summary>
    /// Largest Timing BPM retained by normalization. Malody and osu! charts use
    /// very fast segments as teleport/scroll tricks, so the old 10000 ceiling
    /// deleted real data and re-timed every later note with the previous BPM.
    /// Anything above this bound is clamped instead: the segment was already
    /// near-instant, while TimingMap arithmetic and osu! beatLength text stay
    /// finite and round-trippable.
    /// </summary>
    public const double MaximumTimingBpm = 1_000_000;

    /// <summary>Upper bound of the chart-level reference BPM edited in the metadata form.</summary>
    public const double MaximumMetadataBpm = 10_000;

    public ChartMetadata Metadata { get; set; } = new();
    public int KeyCount { get; set; } = 4;
    public List<ChartNote> Notes { get; set; } = [];
    public List<TimingEvent> Timing { get; set; } = [new() { Bpm = 120 }];
    public JsonObject UnknownRoot { get; set; } = [];
    public JsonObject UnknownMeta { get; set; } = [];
    public JsonObject UnknownSong { get; set; } = [];
    public JsonObject UnknownModeExt { get; set; } = [];
    public List<JsonObject> UnknownEffects { get; set; } = [];
    public List<JsonObject> UnknownNotes { get; set; } = [];
    public string? FilePath { get; set; }

    public double EndBeat => Math.Max(32, Notes.Count == 0 ? 32 : Notes.Max(n => n.EndBeat?.Value ?? n.Beat.Value) + 4);

    public ChartDocument Clone() => new()
    {
        Metadata = Metadata.Clone(),
        KeyCount = KeyCount,
        Notes = Notes.Select(n => n.Clone()).ToList(),
        Timing = Timing.Select(t => t.Clone()).ToList(),
        UnknownRoot = UnknownRoot.DeepClone().AsObject(),
        UnknownMeta = UnknownMeta.DeepClone().AsObject(),
        UnknownSong = UnknownSong.DeepClone().AsObject(),
        UnknownModeExt = UnknownModeExt.DeepClone().AsObject(),
        UnknownEffects = UnknownEffects.Select(e => e.DeepClone().AsObject()).ToList(),
        UnknownNotes = UnknownNotes.Select(e => e.DeepClone().AsObject()).ToList(),
        FilePath = FilePath
    };

    public void Normalize()
    {
        KeyCount = Math.Clamp(KeyCount, 1, 18);
        Metadata.PreviewTime = Math.Max(0, Metadata.PreviewTime);
        Metadata.OverallDifficulty = double.IsFinite(Metadata.OverallDifficulty)
            ? Math.Clamp(Metadata.OverallDifficulty, 0, 10)
            : 8;
        Metadata.HpDrainRate = double.IsFinite(Metadata.HpDrainRate)
            ? Math.Clamp(Metadata.HpDrainRate, 0, 10)
            : 8;
        Notes.RemoveAll(n => (!n.IsDraft && (n.Lane < 0 || n.Lane >= KeyCount)) || n.Beat.Value < 0);
        foreach (var note in Notes)
        {
            if (note.IsDraft) note.DraftColumn = Math.Clamp(note.DraftColumn, -64, KeyCount + 63);
            if (note.EndBeat.HasValue && note.EndBeat.Value.CompareTo(note.Beat) <= 0) note.EndBeat = null;
            note.Volume = Math.Clamp(note.Volume, 0, 100);
            note.Group = Math.Clamp(note.Group, 0, ushort.MaxValue);
        }
        if (!IsNotesSorted())
        {
            // List.Sort is unstable. Equal keys (for example a stacked duplicate)
            // must keep their source order so export order and history diffs do
            // not change between two normalizations of the same content.
            var orderedNotes = Notes.Select((note, index) => (Note: note, Index: index))
                .OrderBy(entry => entry.Note, Comparer<ChartNote>.Create(CompareNotes))
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Note)
                .ToArray();
            Notes.Clear();
            Notes.AddRange(orderedNotes);
        }
        foreach (var timing in Timing)
        {
            timing.Delay = double.IsFinite(timing.Delay) ? timing.Delay : 0;
            timing.EffectValue = double.IsFinite(timing.EffectValue) ? timing.EffectValue : 1;
            if (!EffectNumberText.TryParse(timing.EffectValueText, out var parsedValue) ||
                Math.Abs(parsedValue - timing.EffectValue) > 1e-12 * Math.Max(1, Math.Abs(timing.EffectValue)))
                timing.EffectValueText = timing.EffectValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            else
                timing.EffectValue = parsedValue;
            if (timing.Effect != TimingEffectKind.Custom) timing.EffectFieldName = string.Empty;
            if (timing.Bpm.HasValue)
            {
                var bpm = timing.Bpm.Value;
                // Only values that cannot describe a beat length are dropped. A
                // very fast segment is clamped, never deleted (see MaximumTimingBpm).
                timing.Bpm = !double.IsFinite(bpm) || bpm <= 0 || !double.IsFinite(60_000d / bpm)
                    ? null
                    : Math.Min(bpm, MaximumTimingBpm);
            }
        }
        Timing.RemoveAll(t => t.Beat.Value < 0 || (!t.Bpm.HasValue && !t.Effect.HasValue));
        if (!Timing.Any(t => t.Bpm.HasValue)) Timing.Add(new TimingEvent { Bpm = 120 });
        if (!IsTimingSorted())
        {
            var orderedTiming = Timing.Select((timing, index) => (Timing: timing, Index: index))
                .OrderBy(entry => entry.Timing.Beat)
                .ThenBy(entry => entry.Index)
                .Select(entry => entry.Timing)
                .ToArray();
            Timing.Clear();
            Timing.AddRange(orderedTiming);
        }
        MergeSameBeatBpmEvents();
        if (!double.IsFinite(Metadata.Bpm) || Metadata.Bpm is <= 0 or > MaximumMetadataBpm)
            Metadata.Bpm = Timing.FirstOrDefault(timing =>
                timing.Bpm is > 0 and <= MaximumMetadataBpm)?.Bpm ?? 120;
    }

    /// <summary>
    /// Keeps one BPM record per beat. TimingMap has always let the last record in
    /// source order win, but the Offset field, tempo detection and the Malody BGM
    /// offset used to edit the first one, so an edit could land on a record the
    /// timeline ignored. The survivor inherits a non-zero Delay from an overridden
    /// record because a later zero is almost always a re-entered BPM, not an
    /// intentional reset of the song offset. Effects on overridden rows stay.
    /// Requires <see cref="Timing"/> to be sorted by beat in stable source order.
    /// </summary>
    private void MergeSameBeatBpmEvents()
    {
        TimingEvent? survivor = null;
        for (var index = Timing.Count - 1; index >= 0; index--)
        {
            var timing = Timing[index];
            if (!timing.Bpm.HasValue) continue;
            if (survivor == null || survivor.Beat.CompareTo(timing.Beat) != 0)
            {
                survivor = timing;
                continue;
            }

            if (survivor.Delay == 0 && timing.Delay != 0) survivor.Delay = timing.Delay;
            if (timing.Effect.HasValue)
            {
                timing.Bpm = null;
                timing.Delay = 0;
            }
            else Timing.RemoveAt(index);
        }
    }

    /// <summary>
    /// Returns the beat-0 BPM record that carries the song offset, creating it
    /// when the first BPM record starts later. TimingMap already extends that
    /// first BPM back to beat 0 with no offset and treats the later record's
    /// Delay as a section pause, so an offset must never be written there. The
    /// new record copies the BPM TimingMap was using, so timing is unchanged
    /// until the caller sets its Delay.
    /// </summary>
    public TimingEvent GetOrCreateSongOffsetTiming()
    {
        if (TimingMap.FindSongOffsetTiming(Timing) is { } existing) return existing;
        var first = TimingMap.FindFirstBpmTiming(Timing);
        var created = new TimingEvent { Beat = BeatPosition.Zero, Bpm = first?.Bpm ?? 120 };
        Timing.Insert(0, created);
        return created;
    }

    private bool IsNotesSorted()
    {
        for (var index = 1; index < Notes.Count; index++)
            if (CompareNotes(Notes[index - 1], Notes[index]) > 0) return false;
        return true;
    }

    private bool IsTimingSorted()
    {
        for (var index = 1; index < Timing.Count; index++)
            if (Timing[index - 1].Beat.CompareTo(Timing[index].Beat) > 0) return false;
        return true;
    }

    private static int CompareNotes(ChartNote left, ChartNote right)
    {
        var beatOrder = left.Beat.CompareTo(right.Beat);
        return beatOrder != 0 ? beatOrder :
            (left.IsDraft ? left.DraftColumn : left.Lane).CompareTo(right.IsDraft ? right.DraftColumn : right.Lane);
    }
}

public static class EffectNumberText
{
    private static readonly System.Text.RegularExpressions.Regex JsonNumber = new(
        "^-?(?:0|[1-9][0-9]*)(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        var candidate = text?.Trim() ?? string.Empty;
        return candidate.Length is > 0 and <= 64 && JsonNumber.IsMatch(candidate) &&
               double.TryParse(candidate, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    public static string CanonicalName(TimingEffectKind kind, string? customName = null) => kind switch
    {
        TimingEffectKind.Scroll => "scroll",
        TimingEffectKind.Jump => "jump",
        TimingEffectKind.HS => "hs",
        TimingEffectKind.Sign => "sign",
        _ => customName?.Trim() ?? string.Empty
    };
}

using System.Text.Json;
using LumaChart.Application.Workspaces;
using LumaChart.Core.Models;
using LumaChart.Core.IO;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Editing;

public sealed record PatternNote(int Lane, string Beat, string? EndBeat);
public sealed record MapperPattern(int Version, Guid Id, string Name, int Keys, double SpanBeats,
    PatternNote[] Notes, string SourceTitle, DateTimeOffset Created);

/// <summary>Plain JSON, relative musical beats, no audio/absolute paths or timing dependency.</summary>
public sealed class PatternLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath { get; }
    public List<string> ReadWarnings { get; } = [];
    public PatternLibrary(string profile) => DirectoryPath = Path.Combine(profile, "Patterns");

    public static MapperPattern Capture(string name, int keys, IEnumerable<ChartNote> selected, string sourceTitle = "", ChartDocument? sourceDocument = null)
    {
        var timing = sourceDocument != null && OsuSourceEnvelope.HasSource(sourceDocument) ? new TimingMap(sourceDocument.Timing) : null;
        ChartNote MusicalCopy(ChartNote source)
        {
            var copy=source.Clone();
            BeatPosition Calibrate(BeatPosition beat)=>timing!=null
                ?OsuBeatCalibrator.Calibrate(timing.BeatToMilliseconds(beat),beat,timing,maximumErrorMilliseconds:1.000001).Beat:beat;
            copy.Beat=Calibrate(source.Beat);copy.EndBeat=source.EndBeat.HasValue?Calibrate(source.EndBeat.Value):null;
            if(copy.EndBeat.HasValue && copy.EndBeat.Value.CompareTo(copy.Beat)<=0){copy.Beat=source.Beat;copy.EndBeat=source.EndBeat;}
            return copy;
        }
        var notes = selected.Where(n => !n.IsDraft).Select(MusicalCopy).OrderBy(n => n.Beat).ThenBy(n => n.Lane).ToArray();
        if (notes.Length == 0) throw new InvalidOperationException("请先选取排列中的按键。");
        if (notes.Length > 50000 || keys is < 1 or > 64 || notes.Any(n => n.Lane < 0 || n.Lane >= keys || n.EndBeat.HasValue && n.EndBeat.Value.CompareTo(n.Beat) < 0))
            throw new InvalidOperationException("排列的按键数量、轨道或长度无效。");
        name = name.Trim(); if (name.Length is < 1 or > 120) throw new InvalidOperationException("排列名称需为 1–120 个字符。");
        var first = notes[0].Beat;
        var left = notes.Min(n => n.Lane);
        var relative = notes.Select(n => new PatternNote(n.Lane - left,
            BasicChartEditingService.OffsetExact(BeatPosition.Zero, n.Beat, first).ToString(),
            n.EndBeat.HasValue ? BasicChartEditingService.OffsetExact(BeatPosition.Zero, n.EndBeat.Value, first).ToString() : null)).ToArray();
        return new(1, Guid.NewGuid(), name, keys, Math.Max(1d / 64, notes.Max(n => (n.EndBeat ?? n.Beat).Value) - first.Value), relative, sourceTitle[..Math.Min(sourceTitle.Length, 1024)], DateTimeOffset.UtcNow);
    }

    public static ChartNote[] Materialize(MapperPattern pattern, double scale = 1)
    {
        if (pattern.Version != 1 || pattern.Id == Guid.Empty || pattern.Keys is < 1 or > 64 ||
            pattern.Notes == null || pattern.Notes.Length is < 1 or > 50000 || pattern.Notes.Any(n => n == null) ||
            !double.IsFinite(pattern.SpanBeats) || pattern.SpanBeats <= 0 ||
            !double.IsFinite(scale) || scale is < 1d / 64 or > 64 || string.IsNullOrWhiteSpace(pattern.Name) || pattern.Name.Length > 120)
            throw new InvalidDataException("排列格式或缩放无效。");
        var notes = pattern.Notes.Select(n =>
        {
            if (n.Lane < 0 || n.Lane >= pattern.Keys || !BeatPosition.TryParse(n.Beat, out var beat) || beat.Value < 0 || beat.Value > 1_000_000)
                throw new InvalidDataException("排列音符无效。");
            BeatPosition? end = null;
            if (n.EndBeat != null)
            {
                if (!BeatPosition.TryParse(n.EndBeat, out var tail) || tail.CompareTo(beat) < 0 || tail.Value > 1_000_000) throw new InvalidDataException("排列长条无效。");
                end = tail;
            }
            return new ChartNote { Lane = n.Lane, Beat = beat, EndBeat = end };
        }).ToArray();
        return ScaleNotes(notes, scale);
    }

    public static ChartNote[] ScaleNotes(IReadOnlyList<ChartNote> notes, double scale)
    {
        if (!double.IsFinite(scale) || scale is < 1d / 64 or > 64) throw new ArgumentOutOfRangeException(nameof(scale));
        BeatPosition Scale(BeatPosition beat) => BeatArithmetic.Scale(beat,scale);
        return notes.Select(n => { var copy = n.Clone(); copy.Beat = Scale(n.Beat); copy.EndBeat = n.EndBeat.HasValue ? Scale(n.EndBeat.Value) : null; return copy; }).ToArray();
    }

    public MapperPattern[] List()
    {
        ReadWarnings.Clear(); if (!Directory.Exists(DirectoryPath)) return [];
        var result = new List<MapperPattern>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            try
            {
                if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("排列文件过大。");
                var pattern = JsonSerializer.Deserialize<MapperPattern>(File.ReadAllBytes(file)) ?? throw new InvalidDataException("空排列。");
                _ = Materialize(pattern); result.Add(pattern);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException)
            { ReadWarnings.Add(Path.GetFileName(file) + ": " + ex.Message); }
        return result.OrderByDescending(p => p.Created).ToArray();
    }

    public void Save(MapperPattern pattern)
    {
        _ = Materialize(pattern); OsuFileSafetyPolicy.RequireWritable(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        var target = Path.Combine(DirectoryPath, pattern.Id.ToString("N") + ".json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, pattern, JsonOptions); stream.Flush(true); }
            // A new capture never silently replaces a previously named pattern.
            File.Move(temporary, target, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

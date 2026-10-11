using System.Globalization;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Editing;

/// <summary>Readable osu HitObjects rows. Times are milliseconds, independent of BPM.</summary>
public static class NoteTextCodeService
{
    public static string Export(ChartDocument document, IEnumerable<ChartNote> notes)
    {
        var map = new TimingMap(document.Timing);
        var rows = notes.Where(n => !n.IsDraft).OrderBy(n => n.Beat).ThenBy(n => n.Lane)
            .Select(n => OsuSourceEnvelope.ExportNoteLine(n, document.KeyCount, map)).ToArray();
        return rows.Length == 0 ? "" : $"// SimpleManiaEditor keys:{document.KeyCount}\n[HitObjects]\n" + string.Join('\n', rows);
    }

    public static int Import(EditorSession session, string code, double? pasteAtMilliseconds = null)
    {
        if (session.IsReadOnly || !session.CanEditNotes) throw new InvalidOperationException("当前轨道不允许编辑按键。");
        if (code.Length > 4_000_000) throw new ArgumentException("音符代码过大（最多 4 MB）。");
        var keys = session.Document.KeyCount;
        var map = new TimingMap(session.Document.Timing);
        var rows = new List<(string[] Parts, double Start, double? End)>();
        foreach (var raw in code.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("// SimpleManiaEditor keys:", StringComparison.Ordinal))
            {
                if (!int.TryParse(line.Split(':').Last(), out keys) || keys is < 1 or > 18) throw new ArgumentException("无效的键数。");
                continue;
            }
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line == "[HitObjects]") continue;
            var parts = line.Split(',');
            if (parts.Length != 6 || !int.TryParse(parts[0], out var x) || x is < 0 or > 511 ||
                !int.TryParse(parts[1], out _) || !Number(parts[2], out var start) ||
                !int.TryParse(parts[3], out var type) || (type & (1 | 128)) is not (1 or 128) ||
                !int.TryParse(parts[4], out _)) throw new ArgumentException($"第 {rows.Count + 1} 个音符行无效，请使用 .osu 的 HitObjects 单键或 LN 行。");
            double? end = null;
            if ((type & 128) != 0)
            {
                if (!Number(parts[5].Split(':')[0], out var tail) || tail <= start) throw new ArgumentException("LN 结束时间必须晚于开始时间。");
                end = tail;
            }
            rows.Add((parts, start, end));
            if (rows.Count > 100_000) throw new ArgumentException("一次最多导入 100000 个音符。");
        }
        if (rows.Count == 0) throw new ArgumentException("代码中没有音符。");
        if (keys != session.Document.KeyCount) throw new ArgumentException($"代码为 {keys}K，当前轨道为 {session.Document.KeyCount}K。");
        var shift = pasteAtMilliseconds.HasValue ? pasteAtMilliseconds.Value - rows.Min(r => r.Start) : 0;
        if (!double.IsFinite(shift)) throw new ArgumentException("粘贴位置无效。");
        var notes = rows.Select(row =>
        {
            var parts = row.Parts;
            var sample = (row.End.HasValue ? string.Join(':', parts[5].Split(':').Skip(1)) : parts[5]).Split(':', 5);
            var volume = sample.Length > 3 && int.TryParse(sample[3], out var v) ? Math.Clamp(v, 0, 100) : 0;
            var note = new ChartNote { Lane = int.Parse(parts[0], CultureInfo.InvariantCulture) * keys / 512,
                Beat = map.MillisecondsToBeat(row.Start, 1_000_000), EndBeat = row.End.HasValue ? map.MillisecondsToBeat(row.End.Value, 1_000_000) : null,
                Volume = volume == 0 ? 100 : volume,
                Sound = sample.Length > 4 ? ChartResourceReference.SanitizeImported(sample[4]) : null };
            OsuSourceEnvelope.AttachNote(note, string.Join(',', parts), keys);
            if (shift != 0)
            {
                note.Beat = map.MillisecondsToBeat(row.Start + shift, 1_000_000);
                note.EndBeat = row.End.HasValue ? map.MillisecondsToBeat(row.End.Value + shift, 1_000_000) : null;
            }
            return note;
        }).ToArray();
        if (notes.Any(n => n.Beat.Value < 0) && !OsuSourceEnvelope.HasSource(session.Document)) throw new InvalidOperationException("当前格式不能导入早于起点的音符。");
        if (!session.AllowOwnerProtectedEdits && notes.Any(n => session.IntersectsOwnerProtectedRegion(n.Beat, n.EndBeat ?? n.Beat)))
            throw new InvalidOperationException("导入范围包含锁定区域。");
        session.Mutate(d => d.Notes.AddRange(notes));
        session.Selection.Clear(); session.Selection.UnionWith(notes.Select(n => n.Id));
        return notes.Length;
    }
    private static bool Number(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value) && Math.Abs(value) <= 1_000_000_000;
}

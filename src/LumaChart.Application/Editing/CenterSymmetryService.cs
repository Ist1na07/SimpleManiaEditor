using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

public sealed record CenterSymmetrySettings(BeatPosition Start, BeatPosition End, IReadOnlyList<int> Columns)
{
    public void Validate(int keyCount)
    {
        if (Start.Denominator <= 0 || End.Denominator <= 0 || End.CompareTo(Start) <= 0)
            throw new ArgumentException("中心对称的终点必须晚于起点。");
        if (Columns.Count == 0 || Columns.Distinct().Count() != Columns.Count || Columns.Any(c => c < 0 || c >= keyCount))
            throw new ArgumentException("请选择有效且不重复的参与列。");
    }
}

/// <summary>180-degree note symmetry in time and the ordered participating columns.</summary>
public static class CenterSymmetryService
{
    public static ChartNote? CreateCompanion(ChartNote source, CenterSymmetrySettings settings, int keyCount)
    {
        settings.Validate(keyCount);
        if (source.IsDraft || source.Beat.CompareTo(settings.Start) < 0 ||
            (source.EndBeat ?? source.Beat).CompareTo(settings.End) > 0) return null;
        var columns = settings.Columns.Order().ToArray();
        var index = Array.IndexOf(columns, source.Lane);
        if (index < 0) return null;
        var mirror = source.Clone(); mirror.Id = Guid.NewGuid(); mirror.OnlineCreatorUserId = 0;
        mirror.Lane = columns[columns.Length - 1 - index];
        mirror.Beat = Reflect(settings.Start, settings.End, source.EndBeat ?? source.Beat);
        mirror.EndBeat = source.EndBeat.HasValue ? Reflect(settings.Start, settings.End, source.Beat) : null;
        return SamePosition(source, mirror) ? null : mirror;
    }

    public static bool CanAdd(EditorSession session, ChartNote candidate, IEnumerable<ChartNote> pool)
        => CheckDestination(session, candidate, pool.Where(n => n.IsDraft == candidate.IsDraft &&
            n.Lane == candidate.Lane && n.Beat.CompareTo(candidate.Beat) == 0).ToArray());

    private static bool CheckDestination(EditorSession session, ChartNote candidate, IReadOnlyList<ChartNote> occupied)
    {
        if (candidate.Beat.Value < 0 && !candidate.IsDraft && !LumaChart.Core.IO.OsuSourceEnvelope.HasSource(session.Document))
            throw new InvalidOperationException("中心对称位置早于当前谱面允许范围，未放置任何新按键。");
        if (!session.AllowOwnerProtectedEdits && session.IntersectsOwnerProtectedRegion(candidate.Beat, candidate.EndBeat ?? candidate.Beat))
            throw new InvalidOperationException("中心对称位置位于锁定范围，未放置任何新按键。");
        // Any occupied head is left intact, including a tap at the mirrored LN
        // head. Drawing a source must not fail just because its pair is occupied.
        return occupied.Count == 0;
    }

    public static int CompleteSelection(EditorSession session, CenterSymmetrySettings settings)
    {
        settings.Validate(session.Document.KeyCount);
        if (session.IsReadOnly || !session.CanEditNotes) return 0;
        var additions = new List<ChartNote>();
        // Index once; completing a large selection must not rescan the chart
        // for each reflected note. BeatPosition hashes equivalent fractions equally.
        var occupied = session.Document.Notes.GroupBy(n => (n.IsDraft, n.Lane, n.Beat))
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ChartNote>)group.ToArray());
        foreach (var source in session.Document.Notes.Where(n => session.Selection.Contains(n.Id)))
        {
            var candidate = CreateCompanion(source, settings, session.Document.KeyCount);
            if (candidate == null) continue;
            var key = (candidate.IsDraft, candidate.Lane, candidate.Beat);
            if (!CheckDestination(session, candidate, occupied.GetValueOrDefault(key, []))) continue;
            additions.Add(candidate); occupied[key] = [candidate];
        }
        if (additions.Count == 0) return 0;
        var serial = session.ChangeSerial;
        session.Mutate(doc => doc.Notes.AddRange(additions));
        if (serial == session.ChangeSerial) return 0;
        session.Selection.UnionWith(additions.Select(n => n.Id));
        return additions.Count;
    }

    public static HashSet<Guid> ExpandDeletion(EditorSession session, IEnumerable<Guid> ids, CenterSymmetrySettings? settings)
    {
        var result = ids.ToHashSet();
        if (settings == null) return result;
        settings.Validate(session.Document.KeyCount);
        var occupied = session.Document.Notes.GroupBy(n => (n.IsDraft, n.Lane, n.Beat, n.EndBeat))
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var source in session.Document.Notes.Where(n => result.Contains(n.Id)).ToArray())
        {
            var pair = CreateCompanion(source, settings, session.Document.KeyCount);
            if (pair == null) continue;
            foreach (var note in occupied.GetValueOrDefault((pair.IsDraft, pair.Lane, pair.Beat, pair.EndBeat), []))
                result.Add(note.Id);
        }
        if (!session.AllowOwnerProtectedEdits && session.Document.Notes.Any(n => result.Contains(n.Id) && session.IsOwnerProtected(n)))
            throw new InvalidOperationException("中心对称删除涉及锁定范围，未删除任何按键。");
        return result;
    }

    public static bool SamePosition(ChartNote first, ChartNote second) => first.Lane == second.Lane &&
        first.Beat.CompareTo(second.Beat) == 0 &&
        (first.EndBeat.HasValue == second.EndBeat.HasValue) &&
        (!first.EndBeat.HasValue || first.EndBeat.Value.CompareTo(second.EndBeat!.Value) == 0);

    internal static BeatPosition Reflect(BeatPosition start, BeatPosition end, BeatPosition value)
    {
        long division = start.Denominator;
        foreach (var next in new[] { end.Denominator, value.Denominator })
        {
            var a = division; long b = next;
            while (b != 0) (a, b) = (b, a % b);
            division = division / a * next;
            if (division > BeatPosition.MaximumPreservedDenominator)
                return BeatPosition.FromDouble(start.Value + end.Value - value.Value, BeatPosition.MaximumPreservedDenominator);
        }
        long Ticks(BeatPosition point) => (long)point.Bar * division + (long)point.Numerator * (division / point.Denominator);
        var ticks = checked(Ticks(start) + Ticks(end) - Ticks(value));
        var bar = Math.DivRem(ticks, division, out var numerator);
        if (numerator < 0) { bar--; numerator += division; }
        return BeatPosition.Preserve(checked((int)bar), checked((int)numerator), checked((int)division));
    }
}

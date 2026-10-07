using LumaChart.Core.Localization;
using LumaChart.Core.Models;
using LumaChart.Core.Validation;
using System.Text.Json.Nodes;

namespace LumaChart.Application.Editing;

public enum BasicTimingKind
{
    BpmDelay,
    Scroll,
    Jump,
    HS,
    Sign,
    Custom
}

/// <summary>
/// Controls whether a placement command interprets its beat as a pointer-derived
/// position that still needs grid snapping, or as an exact musical position that
/// has already been authored by the caller (for example, a timeline cell center).
/// </summary>
public enum BasicBeatPlacementPolicy
{
    SnapToCurrentSubdivision,
    PreserveAuthoredBeat
}

public sealed record BasicMetadataEdit(
    string Title,
    string Artist,
    string Creator,
    string Version,
    int KeyCount,
    double? Bpm,
    double OverallDifficulty,
    double HpDrainRate)
{
    public BasicMetadataEdit(
        string title,
        string artist,
        string creator,
        string version,
        int keyCount,
        double overallDifficulty,
        double hpDrainRate)
        : this(title, artist, creator, version, keyCount, null, overallDifficulty, hpDrainRate)
    {
    }
}

public sealed record BasicTimingEdit(
    BeatPosition Beat,
    BasicTimingKind Kind,
    double Value,
    double Delay = 0,
    string CustomFieldName = "");

public readonly record struct RelativeBeatMove(int Steps, int Division)
{
    public double Delta => Division <= 0 ? 0 : (double)Steps / Division;
}

public sealed class ChartNoteClipboard
{
    private readonly ChartNote[] notes;

    internal ChartNoteClipboard(IEnumerable<ChartNote> source)
    {
        notes = source.Select(note => note.Clone()).ToArray();
    }

    public int Count => notes.Length;
    internal IReadOnlyList<ChartNote> Notes => notes;
}

/// <summary>
/// Platform-neutral command boundary for the first complete editing toolset.
/// Every document change is committed through the supplied EditorSession so
/// permission, owner-lock, dirty state and undo semantics stay identical on
/// Windows and Android.
/// </summary>
public static class BasicChartEditingService
{
    private const double Epsilon = 1e-7;

    public static Guid? PlaceTap(EditorSession session, int lane, BeatPosition beat) =>
        PlaceTap(session, lane, beat, BasicBeatPlacementPolicy.SnapToCurrentSubdivision);

    public static Guid? PlaceTap(
        EditorSession session,
        int lane,
        BeatPosition beat,
        BasicBeatPlacementPolicy placementPolicy) =>
        PlaceTap(session, lane, beat, placementPolicy, group: 0);

    public static Guid? PlaceTap(
        EditorSession session,
        int lane,
        BeatPosition beat,
        BasicBeatPlacementPolicy placementPolicy,
        int group)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureLane(session.Document, lane);
        EnsureGroup(group);
        beat = ResolvePlacementBeat(session, beat, placementPolicy);
        if (beat.Value < 0) return null;
        if (HasHeadAt(session.Document, lane, beat)) return null;

        var id = Guid.NewGuid();
        session.Mutate(document => document.Notes.Add(new ChartNote
        {
            Id = id,
            Lane = lane,
            Beat = beat,
            Group = group
        }));
        if (!session.Document.Notes.Any(note => note.Id == id)) return null;
        SelectOnly(session, id);
        return id;
    }

    public static int PlaceTaps(
        EditorSession session,
        IEnumerable<(int Lane, BeatPosition Beat)> points) =>
        PlaceTaps(session, points, BasicBeatPlacementPolicy.SnapToCurrentSubdivision);

    public static int PlaceTaps(
        EditorSession session,
        IEnumerable<(int Lane, BeatPosition Beat)> points,
        BasicBeatPlacementPolicy placementPolicy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(points);
        var occupied = session.Document.Notes.Where(note => !note.IsDraft)
            .Select(note => (note.Lane, Beat: Math.Round(note.Beat.Value, 7)))
            .ToHashSet();
        var additions = new List<ChartNote>();
        foreach (var point in points)
        {
            if (point.Lane < 0 || point.Lane >= session.Document.KeyCount) continue;
            var beat = ResolvePlacementBeat(session, point.Beat, placementPolicy);
            if (beat.Value < 0) continue;
            if (!occupied.Add((point.Lane, Math.Round(beat.Value, 7)))) continue;
            additions.Add(new ChartNote { Id = Guid.NewGuid(), Lane = point.Lane, Beat = beat });
        }
        if (additions.Count == 0) return 0;
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Notes.AddRange(additions));
        if (session.ChangeSerial == serial) return 0;
        session.Selection.Clear();
        session.Selection.UnionWith(additions.Select(note => note.Id));
        return additions.Count;
    }

    public static Guid? PlaceHold(
        EditorSession session,
        int lane,
        BeatPosition firstBeat,
        BeatPosition secondBeat) =>
        PlaceHold(
            session,
            lane,
            firstBeat,
            secondBeat,
            BasicBeatPlacementPolicy.SnapToCurrentSubdivision);

    public static Guid? PlaceHold(
        EditorSession session,
        int lane,
        BeatPosition firstBeat,
        BeatPosition secondBeat,
        BasicBeatPlacementPolicy placementPolicy) =>
        PlaceHold(session, lane, firstBeat, secondBeat, placementPolicy, group: 0);

    public static Guid? PlaceHold(
        EditorSession session,
        int lane,
        BeatPosition firstBeat,
        BeatPosition secondBeat,
        BasicBeatPlacementPolicy placementPolicy,
        int group)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureLane(session.Document, lane);
        EnsureGroup(group);
        var start = ResolvePlacementBeat(session, firstBeat, placementPolicy);
        var end = ResolvePlacementBeat(session, secondBeat, placementPolicy);
        if (end.CompareTo(start) < 0) (start, end) = (end, start);
        if (start.Value < 0) return null;
        if (end.CompareTo(start) == 0)
            end = ApplyRelativeMove(
                start,
                new RelativeBeatMove(1, Math.Clamp(session.SnapDivision, 1, 1920)));
        if (HasHeadAt(session.Document, lane, start)) return null;

        var id = Guid.NewGuid();
        session.Mutate(document => document.Notes.Add(new ChartNote
        {
            Id = id,
            Lane = lane,
            Beat = start,
            EndBeat = end,
            Group = group
        }));
        if (!session.Document.Notes.Any(note => note.Id == id)) return null;
        SelectOnly(session, id);
        return id;
    }

    public static bool ResizeHoldTail(EditorSession session, Guid noteId, BeatPosition tailBeat) =>
        ResizeHoldTail(
            session,
            noteId,
            tailBeat,
            BasicBeatPlacementPolicy.SnapToCurrentSubdivision);

    public static bool ResizeHoldTail(
        EditorSession session,
        Guid noteId,
        BeatPosition tailBeat,
        BasicBeatPlacementPolicy placementPolicy)
    {
        ArgumentNullException.ThrowIfNull(session);
        var source = session.Document.Notes.FirstOrDefault(note => note.Id == noteId);
        if (source == null) return false;
        var resolvedTail = ResolvePlacementBeat(session, tailBeat, placementPolicy);
        var nextEnd = resolvedTail.CompareTo(source.Beat) <= 0 ? (BeatPosition?)null : resolvedTail;
        if (Nullable.Equals(source.EndBeat, nextEnd)) return false;

        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            var note = document.Notes.First(candidate => candidate.Id == noteId);
            note.EndBeat = nextEnd;
        });
        if (session.ChangeSerial == serial) return false;
        SelectOnly(session, noteId);
        return true;
    }

    public static Guid? SelectAt(
        EditorSession session,
        int lane,
        BeatPosition beat,
        bool additive = false,
        double toleranceBeats = .18)
    {
        ArgumentNullException.ThrowIfNull(session);
        var hit = FindHit(session.Document, lane, beat.Value, toleranceBeats);
        if (!additive) session.Selection.Clear();
        if (hit == null) return null;
        if (additive && !session.Selection.Add(hit.Id)) session.Selection.Remove(hit.Id);
        else session.Selection.Add(hit.Id);
        return hit.Id;
    }

    public static int SelectRange(
        EditorSession session,
        int firstLane,
        int secondLane,
        BeatPosition firstBeat,
        BeatPosition secondBeat,
        bool additive = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var minimumLane = Math.Clamp(Math.Min(firstLane, secondLane), 0, session.Document.KeyCount - 1);
        var maximumLane = Math.Clamp(Math.Max(firstLane, secondLane), 0, session.Document.KeyCount - 1);
        var minimumBeat = Math.Max(0, Math.Min(firstBeat.Value, secondBeat.Value));
        var maximumBeat = Math.Max(firstBeat.Value, secondBeat.Value);
        var ids = session.Document.Notes
            .Where(note => !note.IsDraft && note.Lane >= minimumLane && note.Lane <= maximumLane &&
                           note.Beat.Value <= maximumBeat + Epsilon &&
                           (note.EndBeat?.Value ?? note.Beat.Value) >= minimumBeat - Epsilon)
            .Select(note => note.Id)
            .ToArray();
        if (!additive) session.Selection.Clear();
        session.Selection.UnionWith(ids);
        return ids.Length;
    }

    public static int SelectAll(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Selection.Clear();
        session.Selection.UnionWith(session.Document.Notes.Where(note => !note.IsDraft).Select(note => note.Id));
        return session.Selection.Count;
    }

    public static int MoveSelection(EditorSession session, int laneDelta, double beatDelta)
    {
        ArgumentNullException.ThrowIfNull(session);
        var selected = session.Document.Notes
            .Where(note => session.Selection.Contains(note.Id) && !note.IsDraft)
            .ToArray();
        if (selected.Length == 0) return 0;

        var minimumLane = selected.Min(note => note.Lane);
        var maximumLane = selected.Max(note => note.Lane);
        laneDelta = Math.Clamp(laneDelta, -minimumLane, session.Document.KeyCount - 1 - maximumLane);
        var minimumBeat = selected.Min(note => note.Beat.Value);
        var beatMove = QuantizeRelativeMove(
            selected[0].Beat,
            session.SnapDivision,
            beatDelta,
            minimumBeat);
        if (laneDelta == 0 && beatMove.Steps == 0) return 0;

        var ids = selected.Select(note => note.Id).ToHashSet();
        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            foreach (var note in document.Notes.Where(note => ids.Contains(note.Id)))
            {
                note.Lane += laneDelta;
                // Moving is relative to the current subdivision. Exact rational
                // arithmetic retains the common denominator instead of snapping or
                // reducing an existing off-grid expression.
                note.Beat = ApplyRelativeMove(note.Beat, beatMove);
                if (note.EndBeat.HasValue)
                    note.EndBeat = ApplyRelativeMove(note.EndBeat.Value, beatMove);
            }
        });
        return session.ChangeSerial == serial ? 0 : selected.Length;
    }

    /// <summary>
    /// Returns the musical step used when an existing note is moved. Movement is
    /// always one unit of the currently selected subdivision; the origin's
    /// denominator is retained later through a least-common-multiple expression.
    /// </summary>
    public static double RelativeMoveStep(BeatPosition origin, int selectedDivision)
        => 1d / RelativeMoveDivision(origin, selectedDivision);

    public static int RelativeMoveDivision(BeatPosition origin, int selectedDivision)
    {
        _ = origin;
        return Math.Clamp(selectedDivision, 1, 1920);
    }

    /// <summary>Quantizes a drag delta without snapping the note's absolute beat.</summary>
    public static double QuantizeRelativeMoveDelta(
        BeatPosition origin,
        int selectedDivision,
        double rawDelta)
    {
        if (!double.IsFinite(rawDelta)) return 0;
        return QuantizeRelativeMove(origin, selectedDivision, rawDelta).Delta;
    }

    public static RelativeBeatMove QuantizeRelativeMove(
        BeatPosition origin,
        int selectedDivision,
        double rawDelta,
        double minimumOriginBeat = double.PositiveInfinity)
    {
        var division = RelativeMoveDivision(origin, selectedDivision);
        if (!double.IsFinite(rawDelta)) return new RelativeBeatMove(0, division);
        var steps = checked((int)Math.Clamp(
            Math.Round(rawDelta * division, MidpointRounding.AwayFromZero),
            int.MinValue,
            int.MaxValue));
        if (double.IsFinite(minimumOriginBeat) && minimumOriginBeat >= 0)
        {
            var minimumSteps = checked((int)Math.Clamp(
                Math.Ceiling(-minimumOriginBeat * division - Epsilon),
                int.MinValue,
                0));
            steps = Math.Max(steps, minimumSteps);
        }
        return new RelativeBeatMove(steps, division);
    }

    public static BeatPosition ApplyRelativeMove(BeatPosition origin, RelativeBeatMove move)
    {
        if (origin.Denominator == 0) origin = BeatPosition.Zero;
        if (move.Steps == 0) return origin;
        if (move.Division is < 1 or > 1920) throw new ArgumentOutOfRangeException(nameof(move));
        try
        {
            var divisor = GreatestCommonDivisor(origin.Denominator, move.Division);
            var denominator = checked((long)origin.Denominator / divisor * move.Division);
            if (denominator > BeatPosition.MaximumPreservedDenominator)
                throw new InvalidOperationException(
                    LocalizationCatalog.Format("移动后的公分母 {0} 超过上限 {1}；请改用兼容分度。", denominator, BeatPosition.MaximumPreservedDenominator));

            var numerator = checked(
                (long)origin.Numerator * (denominator / origin.Denominator) +
                (long)move.Steps * (denominator / move.Division));
            var barDelta = Math.DivRem(numerator, denominator, out var remainder);
            if (remainder < 0)
            {
                barDelta--;
                remainder += denominator;
            }
            return BeatPosition.Preserve(
                checked((int)((long)origin.Bar + barDelta)),
                checked((int)remainder),
                checked((int)denominator));
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("移动后的 Beat 超出可表示范围。", exception);
        }
    }

    public static int NudgeSelection(
        EditorSession session,
        int direction,
        IEnumerable<Guid>? noteIds = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        direction = Math.Sign(direction);
        if (direction == 0) return 0;
        var selectedIds = (noteIds ?? session.Selection).ToHashSet();
        var selected = session.Document.Notes
            .Where(note => selectedIds.Contains(note.Id))
            .ToArray();
        if (selected.Length == 0) return 0;
        var move = new RelativeBeatMove(direction, Math.Clamp(session.SnapDivision, 1, 1920));
        // A multi-note nudge is atomic: reaching beat zero blocks the complete
        // group instead of collapsing its internal spacing one note at a time.
        if (selected.Any(note => ApplyRelativeMove(note.Beat, move).CompareTo(BeatPosition.Zero) < 0)) return 0;
        var movable = selected.Select(note => note.Id).ToHashSet();

        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            foreach (var note in document.Notes.Where(note => movable.Contains(note.Id)))
            {
                note.Beat = ApplyRelativeMove(note.Beat, move);
                if (note.EndBeat.HasValue)
                    note.EndBeat = ApplyRelativeMove(note.EndBeat.Value, move);
            }
        });
        return session.ChangeSerial == serial ? 0 : movable.Count;
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return Math.Max(1, Math.Abs(left));
    }

    /// <summary>
    /// Returns anchor + (value - origin) as an exact rational that keeps the
    /// common denominator of all three expressions (as ApplyRelativeMove does).
    /// Beyond the preserved-denominator ceiling it falls back to the ordinary
    /// 1/1920 conversion rather than failing a paste.
    /// </summary>
    internal static BeatPosition OffsetExact(BeatPosition anchor, BeatPosition value, BeatPosition origin)
    {
        try
        {
            long anchorDenominator = anchor.Denominator == 0 ? 1 : anchor.Denominator;
            long valueDenominator = value.Denominator == 0 ? 1 : value.Denominator;
            long originDenominator = origin.Denominator == 0 ? 1 : origin.Denominator;
            var denominator = LeastCommonMultiple(
                LeastCommonMultiple(anchorDenominator, valueDenominator),
                originDenominator);
            if (denominator <= BeatPosition.MaximumPreservedDenominator)
            {
                var numerator = checked(
                    ((long)anchor.Bar * anchorDenominator + anchor.Numerator) * (denominator / anchorDenominator) +
                    ((long)value.Bar * valueDenominator + value.Numerator) * (denominator / valueDenominator) -
                    ((long)origin.Bar * originDenominator + origin.Numerator) * (denominator / originDenominator));
                var bar = Math.DivRem(numerator, denominator, out var remainder);
                if (remainder < 0)
                {
                    bar--;
                    remainder += denominator;
                }
                return BeatPosition.Preserve(checked((int)bar), checked((int)remainder), checked((int)denominator));
            }
        }
        catch (OverflowException)
        {
        }
        return BeatPosition.FromDouble(anchor.Value + value.Value - origin.Value);
    }

    private static long LeastCommonMultiple(long left, long right) =>
        checked(left / GreatestCommonDivisor(left, right) * right);

    public static int DeleteSelection(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var ids = session.Selection.ToHashSet();
        if (ids.Count == 0) return 0;
        var count = session.Document.Notes.Count(note => ids.Contains(note.Id));
        if (count == 0) return 0;
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Notes.RemoveAll(note => ids.Contains(note.Id)));
        if (session.ChangeSerial == serial) return 0;
        session.Selection.Clear();
        return count;
    }

    public static bool EraseAt(
        EditorSession session,
        int lane,
        BeatPosition beat,
        double toleranceBeats = .18)
    {
        ArgumentNullException.ThrowIfNull(session);
        var hit = FindHit(session.Document, lane, beat.Value, toleranceBeats);
        if (hit == null) return false;
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Notes.RemoveAll(note => note.Id == hit.Id));
        if (session.ChangeSerial == serial) return false;
        session.Selection.Remove(hit.Id);
        return true;
    }

    public static int EraseRange(
        EditorSession session,
        int firstLane,
        int secondLane,
        BeatPosition firstBeat,
        BeatPosition secondBeat)
    {
        ArgumentNullException.ThrowIfNull(session);
        var minimumLane = Math.Clamp(Math.Min(firstLane, secondLane), 0, session.Document.KeyCount - 1);
        var maximumLane = Math.Clamp(Math.Max(firstLane, secondLane), 0, session.Document.KeyCount - 1);
        var minimumBeat = Math.Max(0, Math.Min(firstBeat.Value, secondBeat.Value));
        var maximumBeat = Math.Max(firstBeat.Value, secondBeat.Value);
        var ids = session.Document.Notes
            .Where(note => !note.IsDraft && note.Lane >= minimumLane && note.Lane <= maximumLane &&
                           note.Beat.Value <= maximumBeat + .18 &&
                           (note.EndBeat?.Value ?? note.Beat.Value) >= minimumBeat - .18)
            .Select(note => note.Id)
            .ToHashSet();
        if (ids.Count == 0) return 0;
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Notes.RemoveAll(note => ids.Contains(note.Id)));
        if (session.ChangeSerial == serial) return 0;
        session.Selection.ExceptWith(ids);
        return ids.Count;
    }

    public static int ErasePoints(
        EditorSession session,
        IEnumerable<(int Lane, BeatPosition Beat)> points,
        double toleranceBeats = .18)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(points);
        var ids = points
            .Select(point => FindHit(session.Document, point.Lane, point.Beat.Value, toleranceBeats)?.Id)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();
        if (ids.Count == 0) return 0;
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Notes.RemoveAll(note => ids.Contains(note.Id)));
        if (session.ChangeSerial == serial) return 0;
        session.Selection.ExceptWith(ids);
        return ids.Count;
    }

    public static ChartNoteClipboard? CopySelection(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var selected = session.Document.Notes
            .Where(note => session.Selection.Contains(note.Id) && !note.IsDraft)
            .OrderBy(note => note.Beat)
            .ThenBy(note => note.Lane)
            .ToArray();
        return selected.Length == 0 ? null : new ChartNoteClipboard(selected);
    }

    public static int Paste(
        EditorSession session,
        ChartNoteClipboard clipboard,
        int anchorLane,
        BeatPosition anchorBeat)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clipboard);
        if (clipboard.Count == 0) return 0;
        EnsureLane(session.Document, anchorLane);
        anchorBeat = Snap(session, anchorBeat);
        if (anchorBeat.CompareTo(BeatPosition.Zero) < 0) anchorBeat = BeatPosition.Zero;
        var sourceMinimumLane = clipboard.Notes.Min(note => note.Lane);
        var sourceMinimumBeat = clipboard.Notes.Select(note => note.Beat)
            .Aggregate((left, right) => left.CompareTo(right) <= 0 ? left : right);
        var existing = session.Document.Notes.Where(note => !note.IsDraft)
            .Select(note => (note.Lane, Beat: Math.Round(note.Beat.Value, 7)))
            .ToHashSet();
        var copies = new List<ChartNote>();
        foreach (var source in clipboard.Notes)
        {
            var lane = anchorLane + source.Lane - sourceMinimumLane;
            if (lane < 0 || lane >= session.Document.KeyCount) continue;
            // Only the anchor is snapped, like MoveSelection moves by whole grid
            // steps. Snapping every copied note on its own bent triplets and 1/16
            // figures onto a coarser grid, and two notes landing on one cell then
            // made the second one disappear as a "collision".
            var beat = OffsetExact(anchorBeat, source.Beat, sourceMinimumBeat);
            if (!existing.Add((lane, Math.Round(beat.Value, 7)))) continue;
            var copy = source.Clone();
            copy.Id = Guid.NewGuid();
            copy.Lane = lane;
            copy.Beat = beat;
            copy.EndBeat = source.EndBeat.HasValue
                ? OffsetExact(anchorBeat, source.EndBeat.Value, sourceMinimumBeat)
                : null;
            copy.OnlineCreatorUserId = 0;
            copies.Add(copy);
        }
        if (copies.Count == 0) return 0;
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Notes.AddRange(copies));
        if (session.ChangeSerial == serial) return 0;
        session.Selection.Clear();
        session.Selection.UnionWith(copies.Select(note => note.Id));
        return copies.Count;
    }

    public static Guid AddTiming(EditorSession session, BasicTimingEdit edit)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edit);
        var id = Guid.NewGuid();
        var timing = CreateTiming(session, id, edit);
        session.Mutate(document => document.Timing.Add(timing));
        return session.Document.Timing.Any(candidate => candidate.Id == id) ? id : Guid.Empty;
    }

    public static bool UpdateTiming(EditorSession session, Guid timingId, BasicTimingEdit edit)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edit);
        var existing = session.Document.Timing.FirstOrDefault(timing => timing.Id == timingId);
        if (existing == null) return false;
        var replacement = CreateTiming(session, timingId, edit);
        replacement.UnknownData = existing.UnknownData?.DeepClone().AsObject();
        replacement.OnlineCreatorUserId = existing.OnlineCreatorUserId;
        var serial = session.ChangeSerial;
        session.Mutate(document => CompleteTimingEditingService.ReplaceTimingRow(document, timingId, replacement));
        return session.ChangeSerial != serial;
    }

    private static TimingEvent CreateTiming(
        EditorSession session,
        Guid id,
        BasicTimingEdit edit)
    {
        var beat = Snap(session, edit.Beat);
        if (beat.Value < 0) throw new ArgumentOutOfRangeException(nameof(edit), "Timing beat cannot be negative.");
        TimingEvent timing;
        if (edit.Kind == BasicTimingKind.BpmDelay)
        {
            if (!double.IsFinite(edit.Value) || edit.Value <= 0 || edit.Value > ChartDocument.MaximumTimingBpm)
                throw new ArgumentOutOfRangeException(nameof(edit), "BPM must be finite, positive and within the supported timing range.");
            if (!double.IsFinite(edit.Delay))
                throw new ArgumentOutOfRangeException(nameof(edit), "Delay must be finite.");
            timing = new TimingEvent { Id = id, Beat = beat, Bpm = edit.Value, Delay = edit.Delay };
        }
        else
        {
            if (!double.IsFinite(edit.Value))
                throw new ArgumentOutOfRangeException(nameof(edit), "Effect value must be finite.");
            var effect = edit.Kind switch
            {
                BasicTimingKind.Scroll => TimingEffectKind.Scroll,
                BasicTimingKind.Jump => TimingEffectKind.Jump,
                BasicTimingKind.HS => TimingEffectKind.HS,
                BasicTimingKind.Sign => TimingEffectKind.Sign,
                BasicTimingKind.Custom => TimingEffectKind.Custom,
                _ => throw new ArgumentOutOfRangeException(nameof(edit))
            };
            var customName = edit.CustomFieldName.Trim();
            if (effect == TimingEffectKind.Custom &&
                (customName.Length is < 1 or > 64 || customName.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '_' && character != '-')))
                throw new ArgumentException("Custom effect name must use 1-64 ASCII letters, digits, '_' or '-'.", nameof(edit));
            timing = new TimingEvent
            {
                Id = id,
                Beat = beat,
                Effect = effect,
                EffectValue = edit.Value,
                EffectValueText = edit.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                EffectFieldName = effect == TimingEffectKind.Custom ? customName : string.Empty
            };
        }
        return timing;
    }

    public static bool DeleteTiming(EditorSession session, Guid timingId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Document.Timing.Any(timing => timing.Id == timingId)) return false;
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Timing.RemoveAll(timing => timing.Id == timingId));
        return session.ChangeSerial != serial;
    }

    public static bool ApplyMetadata(EditorSession session, BasicMetadataEdit edit)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edit);
        var title = RequiredText(edit.Title, nameof(edit.Title));
        var artist = RequiredText(edit.Artist, nameof(edit.Artist));
        var creator = RequiredText(edit.Creator, nameof(edit.Creator));
        var version = RequiredText(edit.Version, nameof(edit.Version));
        if (edit.KeyCount is < 1 or > 18) throw new ArgumentOutOfRangeException(nameof(edit.KeyCount));
        if (edit.Bpm.HasValue &&
            (!double.IsFinite(edit.Bpm.Value) || edit.Bpm.Value is <= 0 or > 10000))
            throw new ArgumentOutOfRangeException(nameof(edit.Bpm));
        if (!double.IsFinite(edit.OverallDifficulty) || edit.OverallDifficulty is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(edit.OverallDifficulty));
        if (!double.IsFinite(edit.HpDrainRate) || edit.HpDrainRate is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(edit.HpDrainRate));

        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            document.Metadata.Title = title;
            document.Metadata.Artist = artist;
            document.Metadata.Creator = creator;
            document.Metadata.Version = version;
            if (edit.Bpm.HasValue) document.Metadata.Bpm = edit.Bpm.Value;
            document.Metadata.OverallDifficulty = edit.OverallDifficulty;
            document.Metadata.HpDrainRate = edit.HpDrainRate;
            document.KeyCount = edit.KeyCount;
        });
        return session.ChangeSerial != serial;
    }

    public static ChartIssue? ValidateAndSelectFirst(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var first = ChartValidator.Validate(session.Document).FirstOrDefault();
        session.Selection.Clear();
        if (first != null) session.Selection.UnionWith(first.NoteIds);
        return first;
    }

    private static ChartNote? FindHit(ChartDocument document, int lane, double beat, double tolerance)
    {
        if (lane < 0 || lane >= document.KeyCount) return null;
        tolerance = Math.Clamp(tolerance, .001, 4);
        return document.Notes
            .Where(note => !note.IsDraft && note.Lane == lane &&
                           beat >= note.Beat.Value - tolerance &&
                           beat <= (note.EndBeat?.Value ?? note.Beat.Value) + tolerance)
            .OrderBy(note => Math.Min(
                Math.Abs(note.Beat.Value - beat),
                note.EndBeat.HasValue ? Math.Abs(note.EndBeat.Value.Value - beat) : double.PositiveInfinity))
            .ThenBy(note => note.Beat)
            .FirstOrDefault();
    }

    private static BeatPosition Snap(EditorSession session, BeatPosition beat) =>
        beat.Snap(Math.Clamp(session.SnapDivision, 1, 1920));

    private static BeatPosition ResolvePlacementBeat(
        EditorSession session,
        BeatPosition beat,
        BasicBeatPlacementPolicy placementPolicy)
    {
        if (beat.Denominator == 0) beat = BeatPosition.Zero;
        return placementPolicy switch
        {
            BasicBeatPlacementPolicy.SnapToCurrentSubdivision => Snap(session, beat),
            BasicBeatPlacementPolicy.PreserveAuthoredBeat => beat,
            _ => throw new ArgumentOutOfRangeException(nameof(placementPolicy))
        };
    }

    private static bool HasHeadAt(ChartDocument document, int lane, BeatPosition beat) =>
        document.Notes.Any(note => !note.IsDraft && note.Lane == lane &&
                                   Math.Abs(note.Beat.Value - beat.Value) <= Epsilon);

    private static void EnsureLane(ChartDocument document, int lane)
    {
        if (lane < 0 || lane >= document.KeyCount) throw new ArgumentOutOfRangeException(nameof(lane));
    }

    private static void EnsureGroup(int group)
    {
        if (group is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(group), "Group ID must be in the range 0-65535.");
    }

    private static void SelectOnly(EditorSession session, Guid noteId)
    {
        session.Selection.Clear();
        session.Selection.Add(noteId);
    }

    private static string RequiredText(string? text, string parameterName)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 256) throw new ArgumentException("Text must contain 1-256 characters.", parameterName);
        return value;
    }
}

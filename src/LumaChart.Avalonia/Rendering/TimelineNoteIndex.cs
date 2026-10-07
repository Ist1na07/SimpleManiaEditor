using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

/// <summary>
/// Immutable start-sorted interval index. Segment maximum-end pruning keeps
/// long notes visible without scanning the complete chart on every frame.
/// </summary>
public sealed class TimelineNoteIndex
{
    private ChartNote[] notes = [];
    private ChartNote[] officialNotes = [];
    private ChartNote[] holdsByEnd = [];
    private ChartNote[] officialHoldsByEnd = [];
    private double[] maximumEndTree = [];
    private double[] officialMaximumEndTree = [];
    private readonly Dictionary<Guid, ChartNote> notesById = [];

    public int Count => notes.Length;
    public int OfficialCount => officialNotes.Length;
    public ProEditorColumnRange? DraftColumnExtent { get; private set; }

    public void Rebuild(IEnumerable<ChartNote> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        notes = source
            .OrderBy(note => note.Beat)
            .ThenBy(ProEditorWorkspaceGeometry.GetColumn)
            .ToArray();
        notesById.Clear();
        foreach (var note in notes) notesById[note.Id] = note;
        officialNotes = notes.Where(note => !note.IsDraft).ToArray();
        var hasDraft = false;
        var minimumDraftColumn = 0;
        var maximumDraftColumn = 0;
        foreach (var note in notes)
        {
            if (!note.IsDraft) continue;
            if (!hasDraft)
            {
                minimumDraftColumn = maximumDraftColumn = note.DraftColumn;
                hasDraft = true;
            }
            else
            {
                minimumDraftColumn = Math.Min(minimumDraftColumn, note.DraftColumn);
                maximumDraftColumn = Math.Max(maximumDraftColumn, note.DraftColumn);
            }
        }
        DraftColumnExtent = hasDraft
            ? new ProEditorColumnRange(minimumDraftColumn, maximumDraftColumn)
            : null;
        holdsByEnd = BuildHoldsByEnd(notes);
        officialHoldsByEnd = BuildHoldsByEnd(officialNotes);
        maximumEndTree = BuildTree(notes);
        officialMaximumEndTree = BuildTree(officialNotes);
    }

    public ChartNote? FindById(Guid id) => notesById.GetValueOrDefault(id);

    private static ChartNote[] BuildHoldsByEnd(IEnumerable<ChartNote> source) => source
            .Where(note => note.EndBeat.HasValue)
            .OrderBy(note => note.EndBeat)
            .ThenBy(ProEditorWorkspaceGeometry.GetColumn)
            .ThenBy(note => note.Beat)
            .ToArray();

    private static double[] BuildTree(ChartNote[] source)
    {
        if (source.Length == 0) return [];
        var tree = new double[source.Length * 4];
        _ = Build(tree, source, 1, 0, source.Length);
        return tree;
    }

    /// <summary>
    /// Returns only LN tails inside an EndBeat window. Pointer hover and drag-start
    /// hit testing use this end-sorted index instead of scanning every chart note.
    /// </summary>
    public int QueryHoldTails(
        double minimumEndBeat,
        double maximumEndBeat,
        List<ChartNote> destination,
        bool includeDrafts = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        var source = includeDrafts ? holdsByEnd : officialHoldsByEnd;
        if (source.Length == 0 || maximumEndBeat < minimumEndBeat) return 0;
        var first = LowerBoundEnd(source, minimumEndBeat);
        var last = UpperBoundEnd(source, maximumEndBeat);
        for (var index = first; index < last; index++) destination.Add(source[index]);
        return destination.Count;
    }

    public int Query(
        double minimumBeat,
        double maximumBeat,
        List<ChartNote> destination,
        bool includeDrafts = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        var source = includeDrafts ? notes : officialNotes;
        var tree = includeDrafts ? maximumEndTree : officialMaximumEndTree;
        if (source.Length == 0 || maximumBeat < minimumBeat) return 0;
        var upperExclusive = UpperBoundStart(source, maximumBeat);
        if (upperExclusive == 0) return 0;
        QueryCore(tree, source, 1, 0, source.Length, upperExclusive, minimumBeat, destination);
        return destination.Count;
    }

    public ChartNote? HitTest(
        int lane,
        double beat,
        double tolerance,
        List<ChartNote> scratch,
        IReadOnlySet<Guid>? excludedIds = null,
        IReadOnlySet<int>? excludedGroups = null,
        bool includeDrafts = false)
    {
        Query(beat - tolerance, beat + tolerance, scratch, includeDrafts);
        ChartNote? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var note in scratch)
        {
            var noteColumn = includeDrafts
                ? ProEditorWorkspaceGeometry.GetColumn(note)
                : note.Lane;
            if (noteColumn != lane ||
                excludedIds?.Contains(note.Id) == true ||
                excludedGroups?.Contains(note.Group) == true) continue;
            var distance = DistanceToInterval(note, beat);
            if (distance >= bestDistance) continue;
            best = note;
            bestDistance = distance;
        }
        return best;
    }

    private static double Build(
        double[] tree,
        ChartNote[] source,
        int node,
        int left,
        int right)
    {
        if (right - left == 1)
            return tree[node] = source[left].EndBeat?.Value ?? source[left].Beat.Value;
        var middle = left + (right - left) / 2;
        return tree[node] = Math.Max(
            Build(tree, source, node * 2, left, middle),
            Build(tree, source, node * 2 + 1, middle, right));
    }

    private static void QueryCore(
        double[] tree,
        ChartNote[] source,
        int node,
        int left,
        int right,
        int upperExclusive,
        double minimumBeat,
        List<ChartNote> destination)
    {
        if (left >= upperExclusive || tree[node] < minimumBeat) return;
        if (right - left == 1)
        {
            destination.Add(source[left]);
            return;
        }
        var middle = left + (right - left) / 2;
        QueryCore(tree, source, node * 2, left, middle, upperExclusive, minimumBeat, destination);
        QueryCore(tree, source, node * 2 + 1, middle, right, upperExclusive, minimumBeat, destination);
    }

    private static int UpperBoundStart(ChartNote[] source, double beat)
    {
        var left = 0;
        var right = source.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (source[middle].Beat.Value <= beat) left = middle + 1;
            else right = middle;
        }
        return left;
    }

    private static int LowerBoundEnd(ChartNote[] source, double beat)
    {
        var left = 0;
        var right = source.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (source[middle].EndBeat!.Value.Value < beat) left = middle + 1;
            else right = middle;
        }
        return left;
    }

    private static int UpperBoundEnd(ChartNote[] source, double beat)
    {
        var left = 0;
        var right = source.Length;
        while (left < right)
        {
            var middle = left + (right - left) / 2;
            if (source[middle].EndBeat!.Value.Value <= beat) left = middle + 1;
            else right = middle;
        }
        return left;
    }

    private static double DistanceToInterval(ChartNote note, double beat)
    {
        var start = note.Beat.Value;
        var end = note.EndBeat?.Value ?? start;
        if (beat < start) return start - beat;
        if (beat > end) return beat - end;
        return 0;
    }
}

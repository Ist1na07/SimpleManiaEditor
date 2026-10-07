using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

public readonly record struct ProEditorColumnRange(int Minimum, int Maximum)
{
    public int Count => Maximum >= Minimum ? Maximum - Minimum + 1 : 0;

    public bool Contains(int column) => column >= Minimum && column <= Maximum;
}

public readonly record struct ProEditorTrackPanState(
    double Minimum,
    double Maximum,
    double Value,
    bool Enabled);

/// <summary>
/// Pure Windows editor workspace-column rules shared by rendering, hit testing and
/// edit commands. Official lanes keep their zero-based columns; draft columns may
/// extend to either side without changing the chart's playable key count.
/// </summary>
public static class ProEditorWorkspaceGeometry
{
    public const int DefaultTotalColumns = 12;
    public const int MaximumTotalColumns = 64;

    public static int NormalizeTotalColumns(int keyCount, int totalColumns)
    {
        keyCount = Math.Clamp(keyCount, 1, 18);
        return Math.Clamp(totalColumns, keyCount, MaximumTotalColumns);
    }

    public static ProEditorColumnRange ResolveColumnRange(
        int keyCount,
        int totalColumns,
        IEnumerable<ChartNote>? notes = null)
    {
        keyCount = Math.Clamp(keyCount, 1, 18);
        totalColumns = NormalizeTotalColumns(keyCount, totalColumns);
        var draftColumns = Math.Max(0, totalColumns - keyCount);
        var leftDraftColumns = draftColumns / 2;
        var rightDraftColumns = draftColumns - leftDraftColumns;
        var minimum = -leftDraftColumns;
        var maximum = keyCount + rightDraftColumns - 1;

        if (notes != null)
        {
            foreach (var note in notes)
            {
                if (!note.IsDraft) continue;
                minimum = Math.Min(minimum, note.DraftColumn);
                maximum = Math.Max(maximum, note.DraftColumn);
            }
        }

        return new ProEditorColumnRange(minimum, maximum);
    }

    public static int GetColumn(ChartNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.IsDraft ? note.DraftColumn : note.Lane;
    }

    public static bool IsOfficialColumn(int column, int keyCount) =>
        column >= 0 && column < Math.Clamp(keyCount, 1, 18);

    public static void ApplyColumn(ChartNote note, int column, int keyCount)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (IsOfficialColumn(column, keyCount))
        {
            note.Lane = column;
            note.IsDraft = false;
            note.DraftColumn = 0;
            return;
        }

        note.Lane = 0;
        note.IsDraft = true;
        note.DraftColumn = column;
    }

    public static ProEditorTrackPanState ResolveTrackPan(
        double viewportWidth,
        double baseTrackLeft,
        double officialTrackWidth,
        double laneWidth,
        double requestedOffset,
        bool enabled)
    {
        if (!enabled || !double.IsFinite(viewportWidth) || viewportWidth <= 0 ||
            !double.IsFinite(baseTrackLeft) || !double.IsFinite(officialTrackWidth) ||
            !double.IsFinite(laneWidth) || officialTrackWidth <= 0 || laneWidth <= 0)
            return new ProEditorTrackPanState(0, 0, 0, false);

        var minimum = -officialTrackWidth + laneWidth - baseTrackLeft;
        var maximum = viewportWidth - laneWidth - baseTrackLeft;
        if (maximum < minimum) maximum = minimum;
        requestedOffset = double.IsFinite(requestedOffset) ? requestedOffset : 0;
        return new ProEditorTrackPanState(
            minimum,
            maximum,
            Math.Clamp(requestedOffset, minimum, maximum),
            true);
    }
}

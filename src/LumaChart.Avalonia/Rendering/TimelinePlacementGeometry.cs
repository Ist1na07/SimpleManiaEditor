using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

public enum TimelinePlacementKind
{
    Tap,
    Hold
}

public readonly record struct TimelinePlacement(
    TimelinePlacementKind Kind,
    BeatPosition StartBeat,
    BeatPosition? EndBeat);

/// <summary>
/// Pure WPF 1.1.0 note-anchor and gesture geometry. Pointer conversion produces
/// an exact rational Beat before the application command receives
/// PreserveAuthoredBeat.
/// </summary>
public static class TimelinePlacementGeometry
{
    public static BeatPosition NearestGrid(double beat, int division)
    {
        division = Math.Clamp(division, 1, 1920);
        return BeatPosition.FromDouble(double.IsFinite(beat) ? beat : 0, division).Snap(division);
    }
    public static BeatPosition CellCenterBeat(double rawBeat, int snapDivision) =>
        BeatPosition.SnapToCellCenter(
            double.IsFinite(rawBeat) ? Math.Max(0, rawBeat) : 0,
            Math.Clamp(snapDivision, 1, 1920));

    public static WindowsNoteAnchorMode NormalizeAnchor(WindowsNoteAnchorMode anchor) =>
        Enum.IsDefined(anchor) ? anchor : WindowsNoteAnchorMode.GridLine;

    public static string DisplayName(WindowsNoteAnchorMode anchor) =>
        NormalizeAnchor(anchor) switch
        {
            WindowsNoteAnchorMode.GridLine => "穿线居中",
            WindowsNoteAnchorMode.LineAbove => "贴在线上",
            WindowsNoteAnchorMode.CellCenter => "格内居中",
            WindowsNoteAnchorMode.CellBottom => "格内靠下",
            _ => "穿线居中"
        };

    public static double NoteAnchorBeatOffset(
        WindowsNoteAnchorMode anchor,
        int snapDivision,
        double noteHeight,
        double pixelsPerBeat)
    {
        anchor = NormalizeAnchor(anchor);
        // snapDivision is no longer read by any anchor: none of them offset by a
        // fraction of the cell any more. It stays on the signature because the
        // conversions that call this — StoredToRenderBeat, PointerToRawStoredBeat
        // — are snap-aware for the CellCenter compatibility path and pass it
        // straight through.
        _ = snapDivision;
        noteHeight = Math.Clamp(
            double.IsFinite(noteHeight) ? noteHeight : WindowsEditorViewSettings.DefaultNoteHeight,
            8,
            128);
        pixelsPerBeat = Math.Max(1, double.IsFinite(pixelsPerBeat) ? pixelsPerBeat : 1);
        var halfNote = noteHeight / 2 / pixelsPerBeat;
        return anchor switch
        {
            WindowsNoteAnchorMode.LineAbove => halfNote,
            // Cell-center stores the exact (2k+1)/(2N) position. Its visual
            // offset must stay zero when the active subdivision later changes.
            WindowsNoteAnchorMode.CellCenter => 0,
            // A fixed gap, not a fraction of the cell. Sitting at the bottom of a
            // cell means the note rests just above its line instead of covering
            // it, and how far "just above" has to be is decided by how tall the
            // note is drawn — three pixels of daylight under it — never by how
            // finely the grid happens to be divided.
            //
            // This used to be capped at .42/snapDivision, which squeezed that gap
            // shut as the subdivision got finer: at 280 px per beat and a 41 px
            // note the offset fell from 23.5 px at 1/4 to 7.4 px at 1/16, so
            // every note visibly slid down onto its line the moment the
            // subdivision changed, with nothing in the chart having changed at
            // all. The cap was protecting against a note box reaching past the
            // next grid line, which a fixed three-pixel gap does not do.
            WindowsNoteAnchorMode.CellBottom => (noteHeight / 2 + 3) / pixelsPerBeat,
            _ => 0
        };
    }

    /// <summary>
    /// Imported and older charts store ordinary grid-line beats even when the
    /// editor is switched to CellCenter. Center those notes visually without
    /// rewriting chart data. A true authored cell-center is already half a cell
    /// from the active grid line and therefore receives no compatibility offset.
    /// </summary>
    public static double CellCenterCompatibilityBeatOffset(
        BeatPosition storedBeat,
        int snapDivision)
    {
        snapDivision = Math.Clamp(snapDivision, 1, 1920);
        var scaled = storedBeat.Value * snapDivision;
        return Math.Abs(scaled - Math.Round(scaled)) <= 1e-9
            ? .5 / snapDivision
            : 0;
    }

    public static double MaximumStoredRenderBeatAdjustment(
        WindowsNoteAnchorMode anchor,
        int snapDivision) =>
        NormalizeAnchor(anchor) == WindowsNoteAnchorMode.CellCenter
            ? .5 / Math.Clamp(snapDivision, 1, 1920)
            : 0;

    public static double PointerToRawStoredBeat(
        double rawRenderBeat,
        WindowsNoteAnchorMode anchor,
        int snapDivision,
        double noteHeight,
        double pixelsPerBeat) =>
        Math.Max(
            0,
            (double.IsFinite(rawRenderBeat) ? rawRenderBeat : 0) -
            NoteAnchorBeatOffset(anchor, snapDivision, noteHeight, pixelsPerBeat));

    public static BeatPosition PointerToStoredBeat(
        double rawRenderBeat,
        WindowsNoteAnchorMode anchor,
        int snapDivision,
        double noteHeight,
        double pixelsPerBeat)
    {
        snapDivision = Math.Clamp(snapDivision, 1, 1920);
        return NormalizeAnchor(anchor) == WindowsNoteAnchorMode.CellCenter
            ? CellCenterBeat(rawRenderBeat, snapDivision)
            : BeatPosition.FromDouble(
                    PointerToRawStoredBeat(
                        rawRenderBeat,
                        anchor,
                        snapDivision,
                        noteHeight,
                        pixelsPerBeat))
                .Snap(snapDivision);
    }

    public static double StoredToRenderBeat(
        BeatPosition storedBeat,
        WindowsNoteAnchorMode anchor,
        int snapDivision,
        double noteHeight,
        double pixelsPerBeat)
    {
        anchor = NormalizeAnchor(anchor);
        var offset = anchor == WindowsNoteAnchorMode.CellCenter
            ? CellCenterCompatibilityBeatOffset(storedBeat, snapDivision)
            : NoteAnchorBeatOffset(anchor, snapDivision, noteHeight, pixelsPerBeat);
        return storedBeat.Value + offset;
    }

    public static TimelinePlacement Resolve(
        TimelineTool tool,
        BeatPosition startBeat,
        BeatPosition currentBeat,
        int snapDivision)
    {
        if (tool is not (TimelineTool.Tap or TimelineTool.Hold))
            throw new ArgumentOutOfRangeException(nameof(tool));
        if (tool == TimelineTool.Tap || currentBeat.CompareTo(startBeat) == 0)
            return new TimelinePlacement(TimelinePlacementKind.Tap, startBeat, null);

        var first = startBeat;
        var second = currentBeat;
        if (second.CompareTo(first) < 0) (first, second) = (second, first);
        return new TimelinePlacement(TimelinePlacementKind.Hold, first, second);
    }

}

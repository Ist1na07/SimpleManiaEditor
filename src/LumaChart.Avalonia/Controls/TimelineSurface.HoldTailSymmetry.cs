using Avalonia;
using Avalonia.Media;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Controls;

public sealed partial class TimelineSurface
{
    private CenterSymmetrySettings? resizingHoldSymmetry;
    private HoldTailResizePlan? resizingHoldPlan;

    private bool UpdateSymmetricHoldTailPlan(BeatPosition? candidate)
    {
        try
        {
            resizingHoldPlan = SymmetricHoldTailResizeCommand.Preview(session!, resizingHoldId,
                candidate ?? resizingHoldHead, resizingHoldSymmetry);
            return true;
        }
        catch (Exception exception) when (IsExpectedEditException(exception))
        { StatusChanged?.Invoke(this, exception.Message); return false; }
    }

    private void DrawSymmetricHoldTailPlan(DrawingContext context, TimelineGeometry geometry)
    {
        if (resizingHoldPlan == null) return;
        var width = GetRenderedNoteWidth(geometry.LaneWidth);
        foreach (var note in resizingHoldPlan.Notes)
        {
            var lane = ProEditorWorkspaceGeometry.GetColumn(note);
            if (lane < geometry.MinimumColumn || lane > geometry.MaximumColumn) continue;
            var left = geometry.TrackLeft + lane * geometry.LaneWidth + (geometry.LaneWidth - width) / 2;
            var headY = NoteYAtBeat(note.Beat, geometry);
            if (note.EndBeat is { } end)
                DrawHoldInteractionPreview(context, left, headY, NoteYAtBeat(end, geometry), width, noteHeight);
            else
            {
                var rect = new Rect(left, headY - noteHeight / 2, width, noteHeight);
                using (context.PushOpacity(WindowsTimelineVisualParityPolicy.HoldInteractionStyle.Opacity))
                    DrawTapNote(context, rect);
                DrawNoteStateOutline(context, rect, false);
            }
        }
    }
}

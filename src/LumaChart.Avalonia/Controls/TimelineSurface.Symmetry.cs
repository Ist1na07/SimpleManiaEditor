using Avalonia;
using Avalonia.Media;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Controls;

public sealed partial class TimelineSurface
{
    private static readonly IBrush SymmetryRangeBrush = new SolidColorBrush(Color.Parse("#106BC9C8"));
    private static readonly IPen SymmetryRangePen = new Pen(new SolidColorBrush(Color.Parse("#906BC9C8")), 1);
    private void DrawSymmetryRange(DrawingContext context, TimelineGeometry geometry)
    {
        if (SymmetrySettings is not { } settings || session == null || session.IsReadOnly) return;
        var startY = YAtBeat(settings.Start.Value, geometry); var endY = YAtBeat(settings.End.Value, geometry);
        var top = Math.Clamp(Math.Min(startY, endY), 0, Bounds.Height);
        var bottom = Math.Clamp(Math.Max(startY, endY), 0, Bounds.Height);
        if (bottom <= top) return;
        foreach (var column in settings.Columns.Where(c => c >= 0 && c < session.Document.KeyCount))
        {
            var left = geometry.TrackLeft + column * geometry.LaneWidth;
            context.DrawRectangle(SymmetryRangeBrush, SymmetryRangePen, new Rect(left, top, geometry.LaneWidth, bottom - top));
        }
        var center = YAtBeat((settings.Start.Value + settings.End.Value) / 2, geometry);
        if (center >= 0 && center <= Bounds.Height)
            context.DrawLine(SymmetryRangePen, new Point(geometry.TrackLeft, center), new Point(geometry.TrackRight, center));
    }

    private void DrawSymmetryCompanionPreview(DrawingContext context, TimelineGeometry geometry, int lane, BeatPosition head, BeatPosition? tail)
    {
        if (SymmetrySettings == null || session == null || lane < 0 || lane >= session.Document.KeyCount) return;
        ChartNote? mirror;
        try { mirror = CenterSymmetryService.CreateCompanion(new ChartNote { Lane = lane, Beat = head, EndBeat = tail }, SymmetrySettings, session.Document.KeyCount); }
        catch (ArgumentException) { return; }
        if (mirror == null) return;
        var width = GetRenderedNoteWidth(geometry.LaneWidth);
        var left = geometry.TrackLeft + mirror.Lane * geometry.LaneWidth + (geometry.LaneWidth - width) / 2;
        using (context.PushOpacity(.55))
        {
            if (mirror.EndBeat.HasValue)
                DrawLongNote(context, left, NoteYAtBeat(mirror.Beat, geometry), NoteYAtBeat(mirror.EndBeat.Value, geometry), width, noteHeight);
            else
            {
                var y = NoteYAtBeat(mirror.Beat, geometry);
                var rect = new Rect(left, y - noteHeight / 2, width, noteHeight);
                DrawTapNote(context, rect); context.DrawRectangle(null, SymmetryRangePen, rect);
            }
        }
    }
}

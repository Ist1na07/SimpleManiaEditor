using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Controls;

/// <summary>Skin-free shape preview: taps and holds share exactly the same width.</summary>
public sealed class PatternShapeSurface : Control
{
    public ChartNote[] Notes { get; init; } = [];
    public int Keys { get; init; } = 7;
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Notes.Length == 0 || Bounds.Width <= 4 || Bounds.Height <= 4) return;
        var start = Notes.Min(n => n.Beat.Value); var end = Notes.Max(n => (n.EndBeat ?? n.Beat).Value);
        var span = Math.Max(1, end - start); var lane = (Bounds.Width - 8) / Math.Max(1, Keys);
        double Y(double beat) => Bounds.Height - 6 - (beat - start) / span * (Bounds.Height - 12);
        foreach (var note in Notes)
        {
            var bottom = Y(note.Beat.Value); var top = note.IsHold ? Y(note.EndBeat!.Value.Value) : bottom - 3;
            context.FillRectangle(Brushes.LightSteelBlue, new Rect(4 + note.Lane * lane, top, Math.Max(1, lane - 2), Math.Max(3, bottom - top)));
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LumaChart.Application.Libraries;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Controls;

/// <summary>Shared millisecond axis, separate old/new lanes; changing BPM does not hide a timing shift.</summary>
public sealed class MapperDiffSurface : Control
{
    private ChartDocument? before, after;
    private MapperSemanticDiff? diff;
    private HashSet<(int Lane, double Start, double? End, bool Added)> changedNotes = [];
    public double StartMs { get; set; }
    public double SpanMs { get; set; } = 10000;
    public void SetDocuments(ChartDocument left, ChartDocument right, MapperSemanticDiff changes)
    { before = left; after = right; diff = changes; changedNotes = changes.Notes.Select(n => (n.Lane, n.StartMs, n.EndMs, n.Added)).ToHashSet(); InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(new SolidColorBrush(Color.Parse("#101619")), new Rect(Bounds.Size));
        if (before == null || after == null || diff == null || Bounds.Height <= 0) return;
        var half = Bounds.Width / 2;
        void Draw(ChartDocument document, bool added, double x)
        {
            var keys = document.KeyCount; var laneWidth = (half - 62) / keys; var map = new TimingMap(document.Timing);
            double Y(double ms) => 24 + (ms - StartMs) / SpanMs * (Bounds.Height - 32);
            for (var key = 0; key <= keys; key++) context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#303B40"))), new Point(x + 52 + key * laneWidth, 24), new Point(x + 52 + key * laneWidth, Bounds.Height));
            for (var ms = Math.Ceiling(StartMs / 1000) * 1000; ms <= StartMs + SpanMs; ms += 1000)
            {
                var text = new FormattedText($"{ms / 1000:0.#}s", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.LightGray);
                context.DrawText(text, new Point(x + 3, Y(ms))); context.DrawLine(new Pen(Brushes.DimGray, .5), new Point(x + 50, Y(ms)), new Point(x + half - 8, Y(ms)));
            }
            foreach (var note in document.Notes.Where(note => !note.IsDraft))
            {
                var ms = Math.Round(map.BeatToMilliseconds(note.Beat)); var end = note.EndBeat.HasValue ? (double?)Math.Round(map.BeatToMilliseconds(note.EndBeat.Value)) : null;
                if ((end ?? ms) < StartMs || ms > StartMs + SpanMs) continue;
                var changed = changedNotes.Contains((note.Lane, ms, end, added));
                var brush = new SolidColorBrush(Color.Parse(changed ? added ? "#74CDB7" : "#EA8795" : "#52636B"));
                var left = x + 54 + note.Lane * laneWidth;
                if (end.HasValue) context.FillRectangle(brush, new Rect(left + laneWidth * .25, Math.Max(24, Y(ms)), laneWidth * .45, Math.Max(3, Math.Min(Bounds.Height, Y(end.Value)) - Math.Max(24, Y(ms)))));
                if (Y(ms) >= 24) context.FillRectangle(brush, new Rect(left, Y(ms), Math.Max(1, laneWidth - 5), 4));
                if (end.HasValue && Y(end.Value) <= Bounds.Height) context.FillRectangle(brush, new Rect(left, Y(end.Value), Math.Max(1, laneWidth - 5), 4));
            }
        }
        Draw(before, false, 0); Draw(after, true, half);
        context.DrawText(new FormattedText("所选历史版本 · 红色为移除", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.LightGray), new Point(8, 2));
        context.DrawText(new FormattedText("比较版本  · 绿色为新增", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.LightGray), new Point(half + 8, 2));
    }
}

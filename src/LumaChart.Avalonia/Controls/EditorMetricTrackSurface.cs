using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System.Globalization;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Controls;

public sealed record EditorMetricSample(double Beat, double Value);

/// <summary>Cached one-second density or explicitly experimental local-load curve.</summary>
public sealed class EditorMetricTrackSurface : Control
{
    private EditorMetricSample[] samples = [];
    private double maximum = 1;
    private double currentBeat;
    private double pixelsPerBeat = 280;
    private int generation;
    private double? viewportAnchor;
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#13181B"));
    private static readonly IPen CurvePen = new Pen(new SolidColorBrush(Color.Parse("#88BEC0")), 1.5);
    private static readonly IPen CursorPen = new Pen(new SolidColorBrush(Color.Parse("#DC7179")), 1);
    public IReadOnlyList<EditorMetricSample> Samples => samples;
    public bool Computing { get; private set; }
    public double CurrentBeat => currentBeat;
    public void CancelPendingAnalysis() => generation++;
    private FormattedText? scaleText;

    public EditorMetricTrackSurface() { ClipToBounds = true; }

    public void SetDocument(ChartDocument source, bool difficulty)
        => SetSnapshotDocument(source.Clone(), difficulty);

    /// <summary>The caller supplies an immutable committed snapshot shared by metric tracks.</summary>
    public void SetSnapshotDocument(ChartDocument document, bool difficulty)
    {
        var request = ++generation;
        Computing = true;
        _ = Task.Run(() => Calculate(document, difficulty)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (request != generation) return;
            Computing = false;
            if (task.IsFaulted) ToolTip.SetTip(this, "分析失败：" + task.Exception?.GetBaseException().Message);
            samples = task.Status == TaskStatus.RanToCompletion ? task.Result : [];
            maximum = Math.Max(1, samples.Length == 0 ? 1 : samples.Max(s => s.Value));
            scaleText = new FormattedText($"0 — {maximum:0.##}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.Parse("#8BA0A5")));
            InvalidateVisual();
        }));
    }

    public void SetViewport(double beat, double scale, double? anchor = null)
    { currentBeat = beat; pixelsPerBeat = scale; viewportAnchor = anchor; InvalidateVisual(); }

    public static EditorMetricSample[] Calculate(ChartDocument document, bool difficulty)
    {
        var map = new TimingMap(document.Timing);
        var bins = new SortedDictionary<long, double>();
        var lastLaneTime = new Dictionary<int, double>();
        foreach (var note in document.Notes.Where(n => !n.IsDraft).OrderBy(n => n.Beat))
        {
            var time = map.BeatToMilliseconds(note.Beat);
            var value = 1d;
            if (difficulty)
            {
                if (lastLaneTime.TryGetValue(note.Lane, out var previous)) value += Math.Exp(-Math.Max(0, time - previous) / 80);
                if (note.IsHold) value += .25;
                lastLaneTime[note.Lane] = time;
            }
            var bin = (long)Math.Floor(time / 1000);
            bins[bin] = bins.GetValueOrDefault(bin) + value;
        }
        return bins.Select(b => new EditorMetricSample(map.MillisecondsToBeat(b.Key * 1000 + 500).Value, b.Value)).OrderBy(s => s.Beat).ToArray();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var cursorY = Math.Clamp(viewportAnchor ?? Bounds.Height * .79, 0, Bounds.Height);
        context.DrawLine(CursorPen, new Point(0, cursorY), new Point(Bounds.Width, cursorY));
        if (scaleText != null) context.DrawText(scaleText, new Point(8, 6));
        var minimumBeat = currentBeat - (Bounds.Height - cursorY + 3) / pixelsPerBeat;
        var maximumBeat = currentBeat + (cursorY + 3) / pixelsPerBeat;
        var low = 0; var high = samples.Length;
        while (low < high) { var middle = (low + high) / 2; if (samples[middle].Beat < minimumBeat) low = middle + 1; else high = middle; }
        for (var index = low; index < samples.Length && samples[index].Beat <= maximumBeat; index++)
        {
            var sample = samples[index];
            var y = cursorY - (sample.Beat - currentBeat) * pixelsPerBeat;
            if (y < -3 || y > Bounds.Height + 3) continue;
            var width = sample.Value / maximum * Math.Max(0, Bounds.Width - 16);
            context.DrawLine(CurvePen, new Point(8, y), new Point(8 + width, y));
        }
    }
}

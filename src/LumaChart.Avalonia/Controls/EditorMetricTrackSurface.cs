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

/// <summary>Cached sliding-window local density or explicitly experimental load curve.</summary>
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
        _ = Task.Run(() => CalculateLocal(document, difficulty)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
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

    /// <summary>500 ms centred window sampled at 8 ms (bounded to 250k points).
    /// Uses two moving indices, so long maps do not require notes × samples work.</summary>
    public static EditorMetricSample[] CalculateLocal(ChartDocument document, bool difficulty)
    {
        var map = new TimingMap(document.Timing);
        var notes = document.Notes.Where(n => !n.IsDraft).OrderBy(n => n.Beat).ToArray();
        if (notes.Length == 0) return [];
        var times = new double[notes.Length]; var weights = new double[notes.Length];
        var laneTimes = new Dictionary<int, double>();
        for (var i = 0; i < notes.Length; i++)
        {
            times[i] = map.BeatToMilliseconds(notes[i].Beat);
            weights[i] = 1;
            if (difficulty)
            {
                if (laneTimes.TryGetValue(notes[i].Lane, out var previous)) weights[i] += Math.Exp(-Math.Max(0, times[i] - previous) / 80);
                if (notes[i].IsHold) weights[i] += .25;
                laneTimes[notes[i].Lane] = times[i];
            }
        }
        var start = times[0] - 300; var end = times[^1] + 300;
        var step = Math.Max(8, (end - start) / 249_999);
        var result = new List<EditorMetricSample>((int)Math.Min(250_000, Math.Ceiling((end - start) / step) + 1));
        var left = 0; var right = 0; var value = 0d;
        for (var time = start; time <= end; time += step)
        {
            while (right < times.Length && times[right] <= time + 250) value += weights[right++];
            while (left < right && times[left] < time - 250) value -= weights[left++];
            result.Add(new(map.MillisecondsToBeat(time, 1_000_000).Value, Math.Max(0, value * 2)));
        }
        return result.ToArray();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var cursorY = Math.Clamp(viewportAnchor ?? Bounds.Height * .79, 0, Bounds.Height);
        context.DrawLine(CursorPen, new Point(0, cursorY), new Point(Bounds.Width, cursorY));
        if (scaleText != null) context.DrawText(scaleText, new Point(8, 6));
        var minimumBeat = currentBeat - (Bounds.Height - cursorY + 3) / pixelsPerBeat;
        var low = 0; var high = samples.Length;
        while (low < high) { var middle = (low + high) / 2; if (samples[middle].Beat < minimumBeat) low = middle + 1; else high = middle; }
        // Rasterize a continuous curve at screen resolution, including the
        // sample immediately outside the viewport. High zoom never exposes
        // sparse one-second histogram rows.
        var index = Math.Max(0, low - 1);
        for (var y = Bounds.Height; y >= 0 && samples.Length > 0; y -= 2)
        {
            var beat = currentBeat + (cursorY - y) / pixelsPerBeat;
            while (index + 1 < samples.Length && samples[index + 1].Beat < beat) index++;
            if (beat < samples[0].Beat || beat > samples[^1].Beat) continue;
            var first = samples[index]; var next = samples[Math.Min(index + 1, samples.Length - 1)];
            var fraction = next.Beat <= first.Beat ? 0 : Math.Clamp((beat - first.Beat) / (next.Beat - first.Beat), 0, 1);
            var width = (first.Value + (next.Value - first.Value) * fraction) / maximum * Math.Max(0, Bounds.Width - 16);
            context.DrawLine(CurvePen, new Point(8, y), new Point(8 + width, y));
        }
    }
}

using System.Diagnostics;

namespace LumaChart.Avalonia.Rendering;

public sealed class TimelinePerformanceRecorder
{
    private const int MaximumSamples = 4096;
    private readonly List<double> renderMilliseconds = [];
    private readonly List<double> frameIntervalsMilliseconds = [];
    private readonly List<double> inputLatencyMilliseconds = [];
    private long previousRenderTimestamp;
    private long pendingInputTimestamp;

    public int MaximumVisitedNotes { get; private set; }

    public long BeginRender()
    {
        var now = Stopwatch.GetTimestamp();
        if (previousRenderTimestamp != 0)
            AddSample(frameIntervalsMilliseconds, Stopwatch.GetElapsedTime(previousRenderTimestamp, now).TotalMilliseconds);
        previousRenderTimestamp = now;

        var input = Interlocked.Exchange(ref pendingInputTimestamp, 0);
        if (input != 0)
            AddSample(inputLatencyMilliseconds, Stopwatch.GetElapsedTime(input, now).TotalMilliseconds);
        return now;
    }

    public void EndRender(long startedAt, int visitedNotes)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        AddSample(renderMilliseconds, elapsed);
        MaximumVisitedNotes = Math.Max(MaximumVisitedNotes, visitedNotes);
    }

    public void MarkInput()
    {
        var now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref pendingInputTimestamp, now, 0);
    }

    public void Reset()
    {
        renderMilliseconds.Clear();
        frameIntervalsMilliseconds.Clear();
        inputLatencyMilliseconds.Clear();
        previousRenderTimestamp = 0;
        pendingInputTimestamp = 0;
        MaximumVisitedNotes = 0;
    }

    public TimelinePerformanceSnapshot Snapshot() =>
        new(
            Summarize(renderMilliseconds),
            Summarize(frameIntervalsMilliseconds),
            Summarize(inputLatencyMilliseconds),
            MaximumVisitedNotes);

    private static void AddSample(List<double> samples, double value)
    {
        if (!double.IsFinite(value) || value < 0) return;
        if (samples.Count == MaximumSamples) samples.RemoveRange(0, MaximumSamples / 4);
        samples.Add(value);
    }

    private static PercentileSummary Summarize(List<double> samples)
    {
        if (samples.Count == 0) return new PercentileSummary(0, 0, 0, 0, 0);
        var ordered = samples.Order().ToArray();
        return new PercentileSummary(
            ordered.Length,
            Percentile(ordered, .50),
            Percentile(ordered, .95),
            Percentile(ordered, .99),
            ordered[^1]);
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        var position = percentile * (ordered.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return ordered[lower];
        var fraction = position - lower;
        return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
    }
}

public sealed record TimelinePerformanceSnapshot(
    PercentileSummary RenderMilliseconds,
    PercentileSummary FrameIntervalMilliseconds,
    PercentileSummary InputToRenderMilliseconds,
    int MaximumVisitedNotes);

public sealed record PercentileSummary(
    int Samples,
    double P50,
    double P95,
    double P99,
    double Maximum);

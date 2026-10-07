using System.Globalization;
using Avalonia;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Rendering;

public enum TimelineTimingCategory
{
    Bpm,
    Scroll,
    Jump,
    HS,
    Sign,
    Custom
}

public readonly record struct TimelineTimingLabel(
    TimingEvent Timing,
    Rect Bounds,
    double AnchorY,
    string Text,
    TimelineTimingCategory Category);

/// <summary>
/// Shared WPF-compatible 22px Timing label geometry used by drawing, hit testing
/// and mixed marquee selection. Later labels are the visual top layer.
/// </summary>
public static class TimelineTimingLayout
{
    public const double LabelHeight = 22;

    public static void BuildLabels(
        IReadOnlyList<TimingEvent> sortedVisibleTimings,
        double trackLeft,
        Func<double, double> beatToY,
        Func<string, double> measureTextWidth,
        List<TimelineTimingLabel> destination)
    {
        ArgumentNullException.ThrowIfNull(sortedVisibleTimings);
        ArgumentNullException.ThrowIfNull(beatToY);
        ArgumentNullException.ThrowIfNull(measureTextWidth);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();

        var previousBeatKey = long.MinValue;
        var stackWidth = 0d;
        foreach (var timing in sortedVisibleTimings)
        {
            var beatKey = checked((long)Math.Round(timing.Beat.Value * 1_000_000));
            if (beatKey != previousBeatKey)
            {
                previousBeatKey = beatKey;
                stackWidth = 0;
            }

            var text = LabelText(timing);
            var width = Math.Clamp(Math.Ceiling(measureTextWidth(text)) + 24, 72, 260);
            var anchorY = beatToY(timing.Beat.Value);
            var bounds = new Rect(
                trackLeft - width - 12 - stackWidth,
                anchorY - LabelHeight / 2,
                width,
                LabelHeight);
            destination.Add(new TimelineTimingLabel(
                timing,
                bounds,
                anchorY,
                text,
                Category(timing)));
            stackWidth += width + 6;
        }
    }

    public static TimelineTimingLabel? HitTestTopmost(
        IReadOnlyList<TimelineTimingLabel> labels,
        Point point)
    {
        ArgumentNullException.ThrowIfNull(labels);
        for (var index = labels.Count - 1; index >= 0; index--)
        {
            var label = labels[index];
            if (ContainsInclusive(label.Bounds, point)) return label;
        }
        return null;
    }

    public static int AddMarqueeHits(
        IReadOnlyList<TimelineTimingLabel> labels,
        Rect marquee,
        double trackLeft,
        ISet<Guid> destination)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(destination);
        var added = 0;
        foreach (var label in labels)
        {
            var anchor = new Rect(trackLeft - 5, label.AnchorY - 5, 17, 10);
            if (!IntersectsInclusive(marquee, label.Bounds) && !IntersectsInclusive(marquee, anchor)) continue;
            if (destination.Add(label.Timing.Id)) added++;
        }
        return added;
    }

    public static IReadOnlyList<Guid> ResolveDeleteBatch(
        IReadOnlySet<Guid> selection,
        Guid hitTimingId)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return selection.Contains(hitTimingId) ? selection.ToArray() : [hitTimingId];
    }

    public static TimelineTimingCategory Category(TimingEvent timing) => timing.Effect switch
    {
        TimingEffectKind.Scroll => TimelineTimingCategory.Scroll,
        TimingEffectKind.Jump => TimelineTimingCategory.Jump,
        TimingEffectKind.HS => TimelineTimingCategory.HS,
        TimingEffectKind.Sign => TimelineTimingCategory.Sign,
        TimingEffectKind.Custom => TimelineTimingCategory.Custom,
        _ => TimelineTimingCategory.Bpm
    };

    public static string LabelText(TimingEvent timing)
    {
        var parts = new List<string>(3);
        if (timing.Bpm.HasValue)
        {
            parts.Add($"BPM {timing.Bpm.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
            if (Math.Abs(timing.Delay) > .0005)
                parts.Add($"Delay {timing.Delay.ToString("0.###", CultureInfo.InvariantCulture)} ms");
        }
        if (timing.Effect.HasValue)
        {
            var prefix = timing.Effect.Value switch
            {
                TimingEffectKind.Scroll => "S",
                TimingEffectKind.Jump => "J",
                TimingEffectKind.HS => "H",
                TimingEffectKind.Sign => "SG",
                _ => "C"
            };
            parts.Add($"{prefix}  {timing.EffectValueText}");
        }
        return parts.Count == 0 ? "Time" : string.Join("  ·  ", parts);
    }

    private static bool ContainsInclusive(Rect rect, Point point) =>
        point.X >= rect.Left && point.X <= rect.Right &&
        point.Y >= rect.Top && point.Y <= rect.Bottom;

    private static bool IntersectsInclusive(Rect left, Rect right) =>
        left.Left <= right.Right && left.Right >= right.Left &&
        left.Top <= right.Bottom && left.Bottom >= right.Top;
}

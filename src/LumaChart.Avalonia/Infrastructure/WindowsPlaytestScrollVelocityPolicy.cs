using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Keeps the configured playtest scroll speed stable across charts whose
/// reference BPM differs. Timing BPM changes inside one chart and Scroll
/// effects remain part of the accumulated scroll-position map.
/// </summary>
public static class WindowsPlaytestScrollVelocityPolicy
{
    public const double DefaultBaseBpm = 120;

    public static double ResolveBaseBpm(ChartDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (IsValidBpm(document.Metadata.Bpm))
            return ResolveLeadingCalibrationBpm(document, document.Metadata.Bpm);
        var timingBpm = document.Timing
            .Where(timing => timing.Bpm.HasValue)
            .Select(timing => timing.Bpm!.Value)
            .FirstOrDefault(IsValidBpm);
        return timingBpm > 0
            ? ResolveLeadingCalibrationBpm(document, timingBpm)
            : DefaultBaseBpm;
    }

    public static double PixelsPerScrollBeat(double pixelsPerSecond, double baseBpm)
    {
        var safeSpeed = double.IsFinite(pixelsPerSecond) && pixelsPerSecond > 0
            ? pixelsPerSecond
            : 760;
        var safeBaseBpm = IsValidBpm(baseBpm) ? baseBpm : DefaultBaseBpm;
        return safeSpeed * 60d / safeBaseBpm;
    }

    private static bool IsValidBpm(double value) =>
        double.IsFinite(value) && value is > 0 and <= 10000;

    private static double ResolveLeadingCalibrationBpm(
        ChartDocument document,
        double metadataBpm)
    {
        var firstNote = document.Notes
            .Where(note => !note.IsDraft)
            .OrderBy(note => note.Beat)
            .FirstOrDefault();
        if (firstNote == null || firstNote.Beat.Value <= 0) return metadataBpm;

        var bpmPoints = document.Timing
            .Select((timing, index) => (Timing: timing, Index: index))
            .Where(entry => entry.Timing.Bpm is double bpm && IsValidBpm(bpm))
            .OrderBy(entry => entry.Timing.Beat)
            .ThenBy(entry => entry.Index)
            .ToArray();
        if (bpmPoints.Length < 2) return metadataBpm;

        var initialBpm = bpmPoints
            .Where(entry => entry.Timing.Beat.Value <= 0)
            .Select(entry => entry.Timing.Bpm!.Value)
            .LastOrDefault();
        if (!IsValidBpm(initialBpm) || !NearlyEqual(metadataBpm, initialBpm))
            return metadataBpm;

        var noteBpm = bpmPoints
            .Where(entry => entry.Timing.Beat.CompareTo(firstNote.Beat) <= 0)
            .Select(entry => entry.Timing.Bpm!.Value)
            .LastOrDefault();
        if (!IsValidBpm(noteBpm) || NearlyEqual(noteBpm, initialBpm))
            return metadataBpm;

        var scrollMap = new ScrollSpeedMap(document.Timing);
        var initialScroll = scrollMap.MultiplierAt(BeatPosition.Zero);
        var noteScroll = scrollMap.MultiplierAt(firstNote.Beat);
        if (!NearlyEqual(noteScroll, 1) ||
            !NearlyEqual(initialBpm * initialScroll, noteBpm, relativeTolerance: .0001))
            return metadataBpm;

        return noteBpm;
    }

    private static bool NearlyEqual(
        double left,
        double right,
        double relativeTolerance = .000001) =>
        Math.Abs(left - right) <=
        relativeTolerance * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
}

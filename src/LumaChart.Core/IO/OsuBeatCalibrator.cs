using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Core.IO;

public enum OsuNoteTimingImportMode
{
    PreserveMilliseconds,
    CalibrateCommonSubdivisions
}

public readonly record struct OsuBeatCalibrationResult(
    BeatPosition Beat,
    bool Calibrated,
    int? Division,
    double ErrorMilliseconds);

/// <summary>
/// Conservatively maps integer-millisecond osu! hit-object timestamps back onto
/// recognizable musical subdivisions. Positions outside the small acceptance window
/// retain the normal millisecond conversion so intentional mapper offsets are not lost.
/// </summary>
public static class OsuBeatCalibrator
{
    public const double DefaultMaximumErrorMilliseconds = 2;
    public const double DefaultMaximumErrorBeats = 1d / 192;

    private static readonly int[] SupportedDivisions =
        [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48];

    public static IReadOnlyList<int> CommonDivisions => SupportedDivisions;

    public static OsuBeatCalibrationResult Calibrate(
        double sourceMilliseconds,
        BeatPosition rawBeat,
        TimingMap timingMap,
        double maximumErrorMilliseconds = DefaultMaximumErrorMilliseconds,
        double maximumErrorBeats = DefaultMaximumErrorBeats)
    {
        ArgumentNullException.ThrowIfNull(timingMap);
        if (!double.IsFinite(sourceMilliseconds) ||
            !double.IsFinite(maximumErrorMilliseconds) || maximumErrorMilliseconds < 0 ||
            !double.IsFinite(maximumErrorBeats) || maximumErrorBeats < 0)
            return new OsuBeatCalibrationResult(rawBeat, false, null, 0);

        foreach (var division in SupportedDivisions)
        {
            var scaled = rawBeat.Value * division;
            if (!double.IsFinite(scaled) || scaled is < long.MinValue or > long.MaxValue) continue;
            var step = checked((long)Math.Round(scaled, MidpointRounding.AwayFromZero));
            var candidateValue = (double)step / division;
            var beatError = Math.Abs(candidateValue - rawBeat.Value);
            if (beatError > maximumErrorBeats + 1e-12) continue;

            var bar = Math.DivRem(step, division, out var numerator);
            if (numerator < 0)
            {
                bar--;
                numerator += division;
            }
            if (bar is < int.MinValue or > int.MaxValue) continue;

            var candidate = BeatPosition.Preserve(
                checked((int)bar),
                checked((int)numerator),
                division);
            var calibratedMilliseconds = timingMap.BeatToMilliseconds(candidate);
            if (!double.IsFinite(calibratedMilliseconds)) continue;
            var millisecondError = Math.Abs(calibratedMilliseconds - sourceMilliseconds);
            if (millisecondError > maximumErrorMilliseconds + 1e-9) continue;

            return new OsuBeatCalibrationResult(candidate, true, division, millisecondError);
        }

        return new OsuBeatCalibrationResult(rawBeat, false, null, 0);
    }
}

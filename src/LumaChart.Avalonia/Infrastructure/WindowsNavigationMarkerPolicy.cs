using LumaChart.Application.Workspaces;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Infrastructure;

public static class WindowsNavigationMarkerPolicy
{
    public const int MaximumMarkers = DesktopNavigationMarker.MaximumCount;

    public static DesktopNavigationMarker Create(double beat)
    {
        var safeBeat = double.IsFinite(beat) ? Math.Max(0, beat) : 0;
        return new DesktopNavigationMarker(
            Guid.NewGuid(),
            BeatPosition.FromDouble(safeBeat));
    }

    public static IReadOnlyList<DesktopNavigationMarker> Add(
        IEnumerable<DesktopNavigationMarker>? existing,
        DesktopNavigationMarker marker,
        out bool duplicate)
    {
        var normalized = Normalize(existing);
        duplicate = normalized.Any(candidate => candidate.Beat.Equals(marker.Beat));
        if (duplicate || normalized.Count >= MaximumMarkers) return normalized;
        return Normalize(normalized.Append(marker));
    }

    public static IReadOnlyList<DesktopNavigationMarker> Remove(
        IEnumerable<DesktopNavigationMarker>? existing,
        Guid markerId) =>
        Normalize(existing).Where(marker => marker.Id != markerId).ToArray();

    public static IReadOnlyList<DesktopNavigationMarker> Normalize(
        IEnumerable<DesktopNavigationMarker>? markers)
    {
        if (markers == null) return [];
        var result = new List<DesktopNavigationMarker>();
        var beats = new HashSet<BeatPosition>();
        foreach (var marker in markers
                     .Where(candidate => candidate.Id != Guid.Empty)
                     .OrderBy(candidate => candidate.Beat)
                     .ThenBy(candidate => candidate.Id))
        {
            if (marker.Beat.Denominator is <= 0 or > BeatPosition.MaximumPreservedDenominator ||
                marker.Beat.Value < 0 ||
                !double.IsFinite(marker.Beat.Value) ||
                !beats.Add(marker.Beat))
                continue;
            result.Add(marker);
            if (result.Count == MaximumMarkers) break;
        }
        return result;
    }
}

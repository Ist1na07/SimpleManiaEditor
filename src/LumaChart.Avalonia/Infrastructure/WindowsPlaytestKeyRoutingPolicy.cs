using Avalonia.Input;

namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsPlaytestKeyRoutingPolicy
{
    internal static bool IsPlaybackToggle(
        Key key,
        IReadOnlyList<string> laneBindings)
    {
        ArgumentNullException.ThrowIfNull(laneBindings);
        if (key == Key.Enter) return true;
        if (key != Key.Space) return false;
        for (var index = 0; index < laneBindings.Count; index++)
            if (laneBindings[index].Equals(
                    nameof(Key.Space),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}

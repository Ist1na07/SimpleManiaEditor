using Avalonia.Input;

namespace LumaChart.Avalonia.Infrastructure;

internal static class WindowsPlaytestKeyRoutingPolicy
{
    internal static bool IsPlaybackToggle(
        Key key,
        IReadOnlyList<string> laneBindings)
    {
        ArgumentNullException.ThrowIfNull(laneBindings);
        if (key is not (Key.Enter or Key.Space)) return false;
        for (var index = 0; index < laneBindings.Count; index++)
            if (laneBindings[index].Equals(
                    key == Key.Space ? nameof(Key.Space) : nameof(Key.Enter),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            else if (key == Key.Enter && laneBindings[index].Equals("Return", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}

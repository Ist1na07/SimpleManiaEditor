using LumaChart.Core.Models;

namespace LumaChart.Application.Workspaces;

public readonly record struct DesktopNavigationMarker(
    Guid Id,
    BeatPosition Beat)
{
    public const int MaximumCount = 64;
}

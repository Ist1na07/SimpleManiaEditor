using LumaChart.Core.Models;

namespace LumaChart.Core.Editing;

public sealed record OwnerProtectedRegion(
    Guid Id,
    BeatPosition StartBeat,
    BeatPosition EndBeat);

namespace LumaChart.Online;

public sealed record OnlineRoomResourceEntryDto(
    string Path,
    long Length,
    string Sha256);

public sealed record OnlineRoomResourceManifestDto(
    long TotalBytes,
    string Sha256,
    string ChartEntry,
    IReadOnlyList<OnlineRoomResourceEntryDto>? Entries = null);

public sealed record OnlineRoomResourcePlanRequestDto(
    IReadOnlyList<OnlineRoomResourceEntryDto> Entries);

public sealed record OnlineRoomResourcePlanDto(
    long TotalBytes,
    long MissingBytes,
    IReadOnlyList<string> MissingSha256);

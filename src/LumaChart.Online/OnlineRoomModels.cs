namespace LumaChart.Online;

public enum OnlineRoomAvailability
{
    Unknown,
    Available,
    NotFound
}

public sealed record OnlineRoomSummaryDto(
    Guid Id,
    long RoomNumber,
    string Name,
    long OwnerUserId,
    string OwnerDisplayName,
    int MemberCount,
    int MaximumMembers,
    bool IsPrivate,
    bool IsHidden,
    bool ResourcesReady,
    long ResourceBytes,
    DateTimeOffset CreatedAt);

public sealed record OnlineRoomDirectoryDto(
    IReadOnlyList<OnlineRoomSummaryDto> Rooms,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record OnlineCreatedRoomDto(OnlineRoomSummaryDto Room);

public sealed record OnlineCreateRoomRequestDto(
    string Name,
    bool IsPrivate,
    string? Password,
    bool IsHidden);

public sealed record OnlineJoinRoomRequestDto(
    string? Password,
    string MemberRole = "editor");

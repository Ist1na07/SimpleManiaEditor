namespace LumaChart.Online;

public sealed record OnlinePlayerProfileDto(
    long Id,
    string DisplayName,
    string AvatarKey,
    string Bio,
    long ChartingSeconds,
    int FollowingCount,
    int FollowerCount,
    bool IsFollowing,
    bool IsMutual,
    bool IsOnline,
    bool IsNameHidden,
    bool IsAvatarHidden,
    string CustomLabel = "",
    IReadOnlyList<OnlineUserTagDto>? Tags = null);

public sealed record OnlinePlayerDirectoryDto(
    int OnlineCount,
    IReadOnlyList<OnlinePlayerProfileDto> Players,
    int Page = 1,
    int PageSize = 100,
    int TotalCount = 0);

/// <summary>
/// Structured account notification. UI layers must render the fields as plain
/// text and compose localized copy themselves; no server-provided markup is used.
/// </summary>
public sealed record OnlineUserNotificationDto(
    long Id,
    string Type,
    long ActorUserId,
    string ActorDisplayName,
    DateTimeOffset CreatedAt,
    string ContextText = "");

public sealed record OnlineUserNotificationPageDto(
    long NextCursor,
    bool HasMore,
    IReadOnlyList<OnlineUserNotificationDto> Notifications);

public sealed record OnlineAvatarDownloadResult(
    string? ContentType,
    long? ContentLength,
    long TransferredBytes);

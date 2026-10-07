namespace LumaChart.Online;

public sealed record OnlineRoomMemberDto(
    long UserId,
    string Username,
    string DisplayName,
    string AvatarKey,
    string MemberRole,
    DateTimeOffset JoinedAt,
    bool IsOnline,
    string CustomLabel = "",
    IReadOnlyList<OnlineUserTagDto>? Tags = null,
    bool CanEditMetadata = false,
    bool CanEditTiming = false,
    bool CanEditNotes = true);

public sealed record OnlineRoomChatMessageDto(
    long Id,
    long UserId,
    string Username,
    string DisplayName,
    string AvatarKey,
    string MemberRole,
    string Message,
    DateTimeOffset CreatedAt,
    string CustomLabel = "",
    IReadOnlyList<OnlineUserTagDto>? Tags = null);

public sealed record OnlineRoomLiveStateDto(
    OnlineRoomSummaryDto Room,
    IReadOnlyList<OnlineRoomMemberDto> Members,
    IReadOnlyList<OnlineRoomChatMessageDto> Messages,
    long LatestMessageId);

public sealed record OnlineRoomInvitationDto(
    Guid Id,
    Guid RoomId,
    string RoomName,
    long InviterUserId,
    string InviterDisplayName,
    string InviterAvatarKey,
    DateTimeOffset ExpiresAt);

public sealed record OnlineRoomInviteRequestDto(long UserId);

public sealed record OnlineRoomInviteResponseRequestDto(string Action);

public sealed record OnlineRoomMemberRoleRequestDto(string MemberRole);

public sealed record OnlineRoomMemberAccessRequestDto(
    string MemberRole,
    bool CanEditMetadata,
    bool CanEditTiming,
    bool CanEditNotes);

public sealed record OnlineRoomChatRequestDto(string Message);

public enum OnlineRoomLifecycleOutcome
{
    Success,
    NotFound,
    Forbidden
}

public sealed record OnlineRoomLifecycleResult<T>(
    OnlineRoomLifecycleOutcome Outcome,
    T? Value)
    where T : class;

public enum OnlineRoomInvitationResponseKind
{
    Room,
    Canceled
}

public sealed record OnlineRoomInvitationResponseDto(
    OnlineRoomInvitationResponseKind Kind,
    OnlineRoomSummaryDto? Room);

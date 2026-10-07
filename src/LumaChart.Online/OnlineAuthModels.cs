namespace LumaChart.Online;

public sealed record OnlineLoginRequestDto(
    string Username,
    string Password,
    Guid CaptchaId,
    string CaptchaAnswer);

public sealed record OnlineRegistrationRequestDto(
    string Username,
    string Password,
    string? DisplayName,
    string Email,
    string EmailCode);

public sealed record OnlineLoginResponseDto(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    OnlineUserProfileDto User);

public sealed record OnlineOsuAccountDto(
    long UserId,
    string Username,
    string ProfileUrl);

public sealed record OnlineOsuOAuthStartDto(
    string AuthorizationUrl,
    string PollToken,
    DateTimeOffset ExpiresAt);

public sealed record OnlineOsuOAuthPollRequestDto(string PollToken);

public sealed record OnlineOsuOAuthPollDto(
    string Status,
    OnlineLoginResponseDto? Login = null,
    OnlineUserProfileDto? User = null,
    string Message = "");

public sealed record OnlineUserTagDto(
    string Name,
    string Color,
    bool IsNameColor = false);

public sealed record OnlineUserProfileDto(
    long Id,
    string Username,
    string DisplayName,
    string Role,
    string AvatarKey,
    DateTimeOffset CreatedAt,
    long ChartingSeconds,
    string Bio = "",
    bool HidePublicAvatar = false,
    bool HidePublicName = false,
    int FollowingCount = 0,
    int FollowerCount = 0,
    string CustomLabel = "",
    IReadOnlyList<OnlineUserTagDto>? Tags = null,
    string Email = "",
    bool HasPasswordLogin = true,
    OnlineOsuAccountDto? OsuAccount = null);

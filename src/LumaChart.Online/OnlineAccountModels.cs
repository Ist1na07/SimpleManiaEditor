namespace LumaChart.Online;

/// <summary>
/// One platform's version track. Any blank field inherits the channel value on
/// the server, so a platform released in lock-step with the others is left empty.
/// Mirrors the server's <c>PlatformVersionOverride</c>.
/// </summary>
public sealed record OnlinePlatformVersionOverrideDto(
    string LatestVersion = "",
    string MinimumLoginVersion = "",
    string DownloadUrl = "");

/// <summary>
/// Per-platform (Windows/Android/iOS) overrides for one channel. Mirrors the
/// server's <c>PlatformVersionTracks</c>. Only present on the admin configuration;
/// ordinary clients receive a flat configuration the server resolved for them.
/// </summary>
public sealed record OnlinePlatformVersionTracksDto(
    OnlinePlatformVersionOverrideDto Windows,
    OnlinePlatformVersionOverrideDto Android,
    OnlinePlatformVersionOverrideDto Ios);

public sealed record OnlineAppConfigurationDto(
    string AboutContent,
    string LatestVersion,
    string MinimumLoginVersion,
    string UpdateNotice,
    string DownloadUrl,
    DateTimeOffset UpdatedAt,
    string BetaLatestVersion = "",
    string BetaMinimumLoginVersion = "",
    string BetaUpdateNotice = "",
    string BetaDownloadUrl = "",
    OnlinePlatformVersionTracksDto? StablePlatformTracks = null,
    OnlinePlatformVersionTracksDto? BetaPlatformTracks = null);

public sealed record OnlineUpdateAppConfigurationRequestDto(
    string? AboutContent,
    string? LatestVersion,
    string? MinimumLoginVersion,
    string? UpdateNotice,
    string? DownloadUrl,
    string? BetaLatestVersion = null,
    string? BetaMinimumLoginVersion = null,
    string? BetaUpdateNotice = null,
    string? BetaDownloadUrl = null,
    OnlinePlatformVersionTracksDto? StablePlatformTracks = null,
    OnlinePlatformVersionTracksDto? BetaPlatformTracks = null);

public sealed record OnlineCaptchaChallengeDto(
    Guid Id,
    string ImageBase64,
    DateTimeOffset ExpiresAt);

public sealed record OnlineSendEmailVerificationCodeRequestDto(
    string Email,
    string Purpose,
    Guid CaptchaId,
    string CaptchaAnswer);

public sealed record OnlineChangeEmailRequestDto(
    string CurrentPassword,
    string NewEmail,
    string EmailCode);

public sealed record OnlineResetPasswordRequestDto(
    string Email,
    string EmailCode,
    string NewPassword);

public sealed record OnlineChangePasswordRequestDto(
    string CurrentPassword,
    string NewPassword);

public sealed record OnlineUpdateProfileRequestDto(
    string? DisplayName,
    string? Bio,
    bool HidePublicAvatar,
    bool HidePublicName);

public sealed record OnlineUpdateNameColorRequestDto(string? TagName);

public sealed record OnlineChartingHeartbeatRequestDto(
    Guid EditorSessionId,
    long OperationSerial,
    bool IsEditing);

public sealed record OnlineChartingHeartbeatResponseDto(
    long ChartingSeconds,
    bool IsCounting,
    DateTimeOffset LastOperationAt);

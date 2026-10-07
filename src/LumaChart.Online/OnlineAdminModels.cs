namespace LumaChart.Online;

public sealed record OnlineSetUserLabelRequestDto(string? Label);

public sealed record OnlineAdminUpdateUserRequestDto(
    string? Username,
    string? DisplayName,
    string? Bio,
    IReadOnlyList<OnlineUserTagDto>? Tags);

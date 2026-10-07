namespace LumaChart.Online;

public sealed record OnlineCreateIssueReportRequestDto(
    string Category,
    string Title,
    string Description,
    string ReproductionSteps,
    string ExpectedBehavior,
    string Environment);

public sealed record OnlineUpdateIssueReportRequestDto(
    string Status,
    string ResolutionNote);

public sealed record OnlineIssueReportDto(
    Guid Id,
    long ReporterUserId,
    string ReporterDisplayName,
    string Category,
    string Title,
    string Description,
    string ReproductionSteps,
    string ExpectedBehavior,
    string Environment,
    string ClientVersion,
    string Status,
    string ResolutionNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record OnlineIssueReportPageDto(
    IReadOnlyList<OnlineIssueReportDto> Issues,
    int Page,
    int PageSize,
    int TotalCount);

using System.Net;
using System.Text;
using System.Text.Json;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Online;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsIssueReportSpecs
{
    public static async Task<WindowsAppUpdatePolicySpecResult[]> RunAsync()
    {
        var report = new OnlineCreateIssueReportRequestDto(
            "playback",
            "Key 音播放偶发电音",
            "开启 Key 音后手打，连续输入时会出现明显电音。",
            "1. 打开谱面\n2. 开启 Key 音\n3. 连续手打",
            "Key 音稳定播放，不出现失真。",
            WindowsIssueReportPolicy.BuildEnvironment(
                "1.1.0",
                1.5,
                1.1,
                1.05,
                1280,
                720,
                authenticated: true,
                inOnlineRoom: false));
        var markdown = WindowsIssueReportPolicy.BuildMarkdown(report, "fixture-id");

        var handler = new IssueHandler();
        using var client = new HttpClient(handler);
        var api = new OnlineIssueApi(
            client,
            new Uri("https://example.invalid/"),
            TimeProvider.System);
        var created = await api.CreateAsync(report, "test-token").ConfigureAwait(false);
        var mine = await api.GetMineAsync(2, "test-token").ConfigureAwait(false);
        var admin = await api.GetAsAdminAsync(3, "confirmed", "owner-token")
            .ConfigureAwait(false);
        var updated = await api.UpdateAsAdminAsync(
                IssueHandler.IssueId,
                new OnlineUpdateIssueReportRequestDto("fixed", "已修复。"),
                "owner-token")
            .ConfigureAwait(false);
        await api.DeleteMineAsync(IssueHandler.IssueId, "test-token")
            .ConfigureAwait(false);
        await api.DeleteAsAdminAsync(IssueHandler.IssueId, "owner-token")
            .ConfigureAwait(false);

        return
        [
            Check(
                WindowsIssueReportPolicy.Contract == "windows-preview-issue-report-r1" &&
                WindowsIssueReportPolicy.Categories.Select(category => category.Code).SequenceEqual(
                ["crash", "playback", "editor", "online", "ui", "import-export", "other"]),
                "issue-report-category-contract-is-stable",
                $"contract={WindowsIssueReportPolicy.Contract}; categories=" +
                string.Join(',', WindowsIssueReportPolicy.Categories.Select(category => category.Code))),
            Check(
                report.Environment.Contains("客户端版本：1.1.0", StringComparison.Ordinal) &&
                report.Environment.Contains("系统缩放：1.5", StringComparison.Ordinal) &&
                report.Environment.Contains("界面缩放：110", StringComparison.Ordinal) &&
                report.Environment.Contains("账号状态：已登录", StringComparison.Ordinal) &&
                !report.Environment.Contains("test-token", StringComparison.Ordinal) &&
                !report.Environment.Contains("密码", StringComparison.Ordinal),
                "issue-report-environment-is-useful-without-session-secrets",
                report.Environment.Replace('\n', ';')),
            Check(
                markdown.Contains("# Key 音播放偶发电音", StringComparison.Ordinal) &&
                markdown.Contains("反馈编号：`fixture-id`", StringComparison.Ordinal) &&
                markdown.Contains("## 问题描述", StringComparison.Ordinal) &&
                markdown.Contains("## 复现步骤", StringComparison.Ordinal) &&
                markdown.Contains("## 期望结果", StringComparison.Ordinal) &&
                markdown.Contains("## 环境信息", StringComparison.Ordinal),
                "issue-report-markdown-is-ready-to-share",
                "title, issue id, description, steps, expectation and environment sections present"),
            Check(
                created?.Id == IssueHandler.IssueId &&
                handler.CreateAuthorized &&
                handler.CreatePayload?.Category == "playback" &&
                handler.CreatePayload?.Title == report.Title,
                "issue-api-create-posts-authenticated-structured-report",
                $"created={created?.Id}; authorized={handler.CreateAuthorized}; " +
                $"category={handler.CreatePayload?.Category}"),
            Check(
                mine is { Page: 2, TotalCount: 1 } &&
                handler.MinePath == "/api/me/issues?page=2" &&
                admin is { Page: 3, TotalCount: 1 } &&
                handler.AdminPath == "/api/admin/issues?page=3&status=confirmed",
                "issue-api-list-routes-preserve-page-and-status",
                $"mine={handler.MinePath}; admin={handler.AdminPath}"),
            Check(
                updated is { Status: "fixed", ResolutionNote: "已修复。" } &&
                handler.UpdateAuthorized &&
                handler.UpdatePath == $"/api/admin/issues/{IssueHandler.IssueId:D}" &&
                handler.UpdatePayload?.Status == "fixed",
                "issue-api-owner-status-update-uses-put",
                $"path={handler.UpdatePath}; status={updated?.Status}"),
            Check(
                handler.DeleteAuthorized &&
                handler.DeletePath == $"/api/me/issues/{IssueHandler.IssueId:D}",
                "issue-api-reporter-delete-uses-delete",
                $"path={handler.DeletePath}; authorized={handler.DeleteAuthorized}"),
            Check(
                handler.AdminDeleteAuthorized &&
                handler.AdminDeletePath == $"/api/admin/issues/{IssueHandler.IssueId:D}",
                "issue-api-owner-delete-uses-admin-delete",
                $"path={handler.AdminDeletePath}; authorized={handler.AdminDeleteAuthorized}")
        ];
    }

    private static WindowsAppUpdatePolicySpecResult Check(
        bool passed,
        string name,
        string detail) => new(name, passed, detail);

    private sealed class IssueHandler : HttpMessageHandler
    {
        internal static readonly Guid IssueId =
            Guid.Parse("11111111-2222-3333-4444-555555555555");
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        public bool CreateAuthorized { get; private set; }
        public OnlineCreateIssueReportRequestDto? CreatePayload { get; private set; }
        public string MinePath { get; private set; } = string.Empty;
        public string AdminPath { get; private set; } = string.Empty;
        public bool UpdateAuthorized { get; private set; }
        public string UpdatePath { get; private set; } = string.Empty;
        public OnlineUpdateIssueReportRequestDto? UpdatePayload { get; private set; }
        public bool DeleteAuthorized { get; private set; }
        public string DeletePath { get; private set; } = string.Empty;
        public bool AdminDeleteAuthorized { get; private set; }
        public string AdminDeletePath { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (request.Method == HttpMethod.Post && path == "/api/me/issues")
            {
                CreateAuthorized = request.Headers.Authorization?.Scheme == "Bearer" &&
                                   request.Headers.Authorization.Parameter == "test-token";
                CreatePayload = await ReadAsync<OnlineCreateIssueReportRequestDto>(
                    request.Content,
                    cancellationToken).ConfigureAwait(false);
                return JsonResponse(Report("open", string.Empty));
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/me/issues", StringComparison.Ordinal))
            {
                MinePath = path;
                return JsonResponse(new OnlineIssueReportPageDto([Report("open", string.Empty)], 2, 30, 1));
            }
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/admin/issues", StringComparison.Ordinal))
            {
                AdminPath = path;
                return JsonResponse(new OnlineIssueReportPageDto([Report("confirmed", string.Empty)], 3, 30, 1));
            }
            if (request.Method == HttpMethod.Put &&
                path == $"/api/admin/issues/{IssueId:D}")
            {
                UpdatePath = path;
                UpdateAuthorized = request.Headers.Authorization?.Parameter == "owner-token";
                UpdatePayload = await ReadAsync<OnlineUpdateIssueReportRequestDto>(
                    request.Content,
                    cancellationToken).ConfigureAwait(false);
                return JsonResponse(Report(
                    UpdatePayload?.Status ?? "open",
                    UpdatePayload?.ResolutionNote ?? string.Empty));
            }
            if (request.Method == HttpMethod.Delete &&
                path == $"/api/me/issues/{IssueId:D}")
            {
                DeletePath = path;
                DeleteAuthorized = request.Headers.Authorization?.Parameter == "test-token";
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Delete &&
                path == $"/api/admin/issues/{IssueId:D}")
            {
                AdminDeletePath = path;
                AdminDeleteAuthorized = request.Headers.Authorization?.Parameter == "owner-token";
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static OnlineIssueReportDto Report(string status, string note) => new(
            IssueId,
            42,
            "Tester",
            "playback",
            "Key 音播放偶发电音",
            "描述",
            "步骤",
            "期望",
            "环境",
            "1.1.0",
            status,
            note,
            DateTimeOffset.Parse("2026-07-31T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-31T00:00:00Z"));

        private static async Task<T?> ReadAsync<T>(
            HttpContent? content,
            CancellationToken cancellationToken)
        {
            if (content == null) return default;
            await using var stream = await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
    }
}

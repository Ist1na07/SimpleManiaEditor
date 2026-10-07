using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LumaChart.Online;

namespace LumaChart.Avalonia.Infrastructure;

internal sealed record WindowsIssueCategory(string Code, string Label)
{
    public override string ToString() => Label;
}

internal static class WindowsIssueReportPolicy
{
    public const string Contract = "windows-preview-issue-report-r1";

    public static IReadOnlyList<WindowsIssueCategory> Categories { get; } =
    [
        new("crash", "崩溃 / 报错"),
        new("playback", "播放 / Key 音"),
        new("editor", "编辑器 / 谱面"),
        new("online", "联机 / 房间"),
        new("ui", "界面 / 显示"),
        new("import-export", "导入 / 导出"),
        new("other", "其他")
    ];

    public static string BuildEnvironment(
        string clientVersion,
        double renderScaling,
        double uiScale,
        double fontScale,
        double viewportWidth,
        double viewportHeight,
        bool authenticated,
        bool inOnlineRoom)
    {
        var culture = CultureInfo.CurrentCulture.Name;
        return string.Join('\n',
            AvaloniaLocalizationCatalog.Format("客户端版本：{0}", clientVersion),
            AvaloniaLocalizationCatalog.Format("系统：{0}", RuntimeInformation.OSDescription.Trim()),
            AvaloniaLocalizationCatalog.Format("进程架构：{0}", RuntimeInformation.ProcessArchitecture),
            $".NET：{RuntimeInformation.FrameworkDescription}",
            AvaloniaLocalizationCatalog.Format("区域：{0}", culture),
            AvaloniaLocalizationCatalog.Format("窗口：{0} × {1} DIP", Math.Round(viewportWidth), Math.Round(viewportHeight)),
            AvaloniaLocalizationCatalog.Format("系统缩放：{0:0.##}", renderScaling),
            AvaloniaLocalizationCatalog.Format("界面缩放：{0:P0}", uiScale),
            AvaloniaLocalizationCatalog.Format("字体缩放：{0:P0}", fontScale),
            AvaloniaLocalizationCatalog.Format("账号状态：{0}", (authenticated ? AvaloniaLocalizationCatalog.Text("已登录") : AvaloniaLocalizationCatalog.Text("离线"))),
            AvaloniaLocalizationCatalog.Format("联机房间：{0}", (inOnlineRoom ? AvaloniaLocalizationCatalog.Text("已加入") : AvaloniaLocalizationCatalog.Text("未加入"))));
    }

    public static string BuildMarkdown(
        OnlineCreateIssueReportRequestDto report,
        string? issueNumber = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var category = Categories.FirstOrDefault(candidate =>
            candidate.Code.Equals(report.Category, StringComparison.OrdinalIgnoreCase))?.Label
            ?? report.Category;
        var builder = new StringBuilder();
        builder.AppendLine($"# {Normalize(report.Title)}");
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(issueNumber))
        {
            builder.AppendLine(AvaloniaLocalizationCatalog.Format("> 反馈编号：`{0}`", Normalize(issueNumber)));
            builder.AppendLine();
        }
        builder.AppendLine(AvaloniaLocalizationCatalog.Format("- 类型：{0}", category));
        builder.AppendLine(AvaloniaLocalizationCatalog.Format("- 生成时间：{0:yyyy-MM-dd HH:mm:ss zzz}", DateTimeOffset.Now));
        builder.AppendLine();
        AppendSection(builder, "问题描述", report.Description, "未填写");
        AppendSection(builder, "复现步骤", report.ReproductionSteps, "暂时无法稳定复现");
        AppendSection(builder, "期望结果", report.ExpectedBehavior, "未填写");
        AppendSection(builder, "环境信息", report.Environment, "未附带");
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string SaveToDownloads(
        OnlineCreateIssueReportRequestDto report,
        string? issueNumber = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        Directory.CreateDirectory(downloads);
        var title = SanitizeFileName(report.Title);
        var prefix = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = $"LumaChart-Issue-{prefix}-{title}";
        var path = Path.Combine(downloads, baseName + ".md");
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(downloads, $"{baseName}-{suffix}.md");
        File.WriteAllText(path, BuildMarkdown(report, issueNumber), new UTF8Encoding(false));
        return path;
    }

    private static void AppendSection(
        StringBuilder builder,
        string title,
        string? content,
        string fallback)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine(string.IsNullOrWhiteSpace(content) ? fallback : Normalize(content));
        builder.AppendLine();
    }

    private static string Normalize(string? value) =>
        (value ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Trim();

    private static string SanitizeFileName(string? value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var character in Normalize(value))
        {
            if (invalid.Contains(character) || char.IsControl(character)) continue;
            builder.Append(character);
            if (builder.Length >= 42) break;
        }
        return builder.Length == 0 ? "Untitled" : builder.ToString().Trim().TrimEnd('.');
    }
}

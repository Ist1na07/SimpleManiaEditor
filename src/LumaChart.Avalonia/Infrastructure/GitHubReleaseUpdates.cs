using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>Reads public releases only; never sends local charts, paths or credentials.</summary>
internal static class GitHubReleaseUpdates
{
    internal static readonly Uri Endpoint = new("https://api.github.com/repos/Ist1na07/SimpleManiaEditor/releases/latest");

    public static async Task<(WindowsAppUpdatePresentation Presentation, Uri? Download)> CheckAsync(
        HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.ParseAdd("SimpleManiaEditor/" + SimpleManiaProduct.Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return (Present("尚无可下载版本", "仓库暂未发布 Release。可打开 GitHub 发布页查看。"), new Uri(SimpleManiaProduct.Releases));
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return Parse(json.RootElement);
    }

    internal static (WindowsAppUpdatePresentation Presentation, Uri? Download) Parse(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new JsonException("GitHub Release 的版本号格式不正确。");
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            release.TryGetProperty("prerelease", out var preview) && preview.GetBoolean())
            throw new JsonException("该 Release 不是正式发布版本。");
        var newer = WindowsAppUpdatePolicy.CompareVersions(SimpleManiaProduct.Version, version.ToString()) < 0;
        var download = new Uri(SimpleManiaProduct.Releases);
        var packaged = false;
        if (release.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                var link = asset.GetProperty("browser_download_url").GetString();
                if (name?.StartsWith("SimpleManiaEditor-", StringComparison.Ordinal) != true ||
                    !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    uri.Host != "github.com" || !uri.AbsolutePath.StartsWith("/Ist1na07/SimpleManiaEditor/releases/download/", StringComparison.Ordinal)) continue;
                download = uri;
                packaged = true;
                break;
            }
        var notice = newer
            ? "下载后完整解压到新文件夹，关闭旧版后启动；保留原有 Maps 与设置。"
            : "当前版本无需更新。";
        if (newer && !packaged) notice = "发现新版源码，暂未提供 Windows 分发包。请查看发布页。";
        if (newer && release.TryGetProperty("body", out var body) && body.GetString() is { Length: > 0 } text)
            notice += "\n\n" + text[..Math.Min(text.Length, 8000)];
        var presentation = Present(newer ? "发现新版本" : "当前版本无需更新", notice) with
        {
            VersionText = $"当前 {SimpleManiaProduct.Version}  →  最新 {version}",
            DownloadButtonText = newer && packaged ? "下载 Windows 分发包" : "打开发布页"
        };
        return (presentation, download);
    }

    internal static WindowsAppUpdatePresentation Present(string title, string notice) =>
        new(title, $"当前版本 {SimpleManiaProduct.Version}", notice, "关闭", "打开发布页", true, false);
}

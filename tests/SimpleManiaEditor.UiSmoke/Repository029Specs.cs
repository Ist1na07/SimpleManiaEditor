using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using LumaChart.Application.Libraries;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;

internal static class Repository029Specs
{
    public static void Run(MainWindow window)
    {
        var opened = new List<string>();
        var charts = new[] { Chart("one"), Chart("two") };
        var folder = DesktopChartLibraryService.GroupByFolder(charts, Path.GetDirectoryName(charts[0].FolderPath)!).Single();
        var card = new LibraryFolderCard(path => { opened.Add(path); return Task.CompletedTask; }, _ => new ContextMenu(), new WindowsLibraryFolderExpansionState(), _ => null)
            { DataContext = new DesktopLibraryFolderView(folder, charts) };
        card.SetExpanded(true);
        var row = card.GetLogicalDescendants().OfType<Border>().First(c => AutomationProperties.GetAutomationId(c) == "Library.ChartRow");
        row.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = row });
        Require(opened.SequenceEqual([charts[0].ChartPath]), "double-click row did not open the chosen difficulty");
        var button = row.GetLogicalDescendants().OfType<Button>().Single();
        button.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = button });
        Require(opened.Count == 1, "button double-click bubbled into a second open");
        card.DataContext = new DesktopLibraryFolderView(folder, [charts[1]]);
        var header = card.GetLogicalDescendants().OfType<Grid>().Single(c => AutomationProperties.GetAutomationId(c) == "Library.FolderExpanderHeader");
        header.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = header });
        Require(opened.SequenceEqual([charts[0].ChartPath, charts[1].ChartPath]), "recycled folder card double-click opened stale difficulty");
        card.DataContext = new DesktopLibraryFolderView(folder, charts);
        card.SetExpanded(false);
        header.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, null!) { Source = header });
        Require(opened.Count == 2 && card.CaptureDiagnostics().IsExpanded, "multi-difficulty folder must show choices instead of guessing");

        using var release = JsonDocument.Parse("""
            {"tag_name":"v0.1.30","draft":false,"prerelease":false,"body":"Release notes","assets":[
                {"name":"SimpleManiaEditor-0.1.30-win-x64.zip","browser_download_url":"https://evil.example/fake.zip"},
                {"name":"SimpleManiaEditor-0.1.30-Distribution-win-x64.zip","browser_download_url":"https://github.com/Ist1na07/SimpleManiaEditor/releases/download/v0.1.30/SimpleManiaEditor-0.1.30-Distribution-win-x64.zip"}]}
            """);
        var update = GitHubReleaseUpdates.Parse(release.RootElement);
        Require(update.Presentation.Title == "发现新版本" && update.Download!.Host == "github.com" && update.Presentation.Notice.Contains("Release notes"), "new release/download validation failed");
        using var current = JsonDocument.Parse($$"""{"tag_name":"v{{SimpleManiaProduct.Version}}","assets":[]} """);
        Require(GitHubReleaseUpdates.Parse(current.RootElement).Presentation.Title == "当前版本无需更新", "current version incorrectly offers upgrade");
        using var malformed = JsonDocument.Parse("""{"tag_name":"arbitrary","assets":[]} """);
        try { GitHubReleaseUpdates.Parse(malformed.RootElement); throw new InvalidOperationException("malformed release accepted"); }
        catch (JsonException) { }
        using var fake = new HttpClient(new FakeReleaseHandler());
        var noRelease = GitHubReleaseUpdates.CheckAsync(fake, default).GetAwaiter().GetResult();
        Require(noRelease.Presentation.Title == "尚无可下载版本" && noRelease.Download!.AbsoluteUri == SimpleManiaProduct.Releases, "404 must mean no release, not latest");
        var all = window.GetLogicalDescendants().OfType<Control>().Distinct().ToArray();
        Require(!all.Any(c => AutomationProperties.GetAutomationId(c) is "Settings.Sponsor" or "Settings.Bilibili" or "About.Sponsor" or "About.Bilibili"), "upstream personal links remain");
        Require(all.OfType<TextBlock>().Single(c => AutomationProperties.GetAutomationId(c) == "Settings.Version").Text!.Contains(SimpleManiaProduct.Version), "settings shows upstream version");
        typeof(MainWindow).GetMethod("ShowAboutAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
        Require(all.OfType<TextBlock>().Single(c => AutomationProperties.GetAutomationId(c) == "About.Content").Text!.Contains("Ist1na"), "about content reset to upstream identity");
        typeof(MainWindow).GetMethod("CloseAbout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
        Console.WriteLine("PASS double-click chart/folder, recycled card, ambiguous difficulties, GitHub update/latest/404/URL safety and fork branding");
    }

    private static DesktopLibraryChartEntry Chart(string name) => new()
    {
        ChartPath = Path.Combine(Path.GetTempPath(), "repository029", name + ".osu"),
        FolderPath = Path.Combine(Path.GetTempPath(), "repository029"), RelativePath = name + ".osu", Title = name, Format = "osu!mania"
    };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class FakeReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Require(request.RequestUri == GitHubReleaseUpdates.Endpoint && request.Headers.Authorization == null && request.Content == null &&
                request.Headers.UserAgent.ToString().Contains(SimpleManiaProduct.Version), "update sends unexpected credentials or local data");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
}

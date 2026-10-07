using System.Net;
using System.Text.RegularExpressions;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Online;
using LumaChart.Storage;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Owns the Windows Avalonia account transport and its platform token store.
/// </summary>
public sealed class WindowsOnlineAccountHost : IDisposable
{
    public const string ClientVersion = AvaloniaClientBuildInfo.ProtocolVersion;
    private static readonly Uri ProductionEndpoint = new("https://api.lumachart.cc/");
    private readonly HttpClient client;
    private readonly HttpClient eventClient;
    private bool disposed;

    private WindowsOnlineAccountHost(
        HttpClient client,
        HttpClient eventClient,
        Uri endpoint,
        string? profileName)
    {
        this.client = client;
        this.eventClient = eventClient;
        Endpoint = endpoint;
        ProfileName = profileName;
        ISessionTokenStore tokenStore = PlatformSessionTokenStores.Current ??
            new WindowsDpapiSessionTokenStore(AvaloniaAppPaths.Locations);
        Session = new OnlineAccountSession(
            client,
            endpoint,
            tokenStore,
            fallbackMessageFactory: FormatHttpFailure,
            eventClient: eventClient);
    }

    public Uri Endpoint { get; }
    public string? ProfileName { get; }
    public OnlineAccountSession Session { get; }

    public static WindowsOnlineAccountHost Create()
    {
        var endpoint = ResolveEndpoint();
        var profileName = ResolveProfileName();
        var client = new HttpClient
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromMinutes(10)
        };
        var eventClient = new HttpClient
        {
            BaseAddress = endpoint,
            // An SSE response is intentionally long-lived; HttpClient.Timeout
            // measures the complete response lifetime rather than idle time.
            // Silence is bounded instead by OnlineChartEventApi's idle watchdog
            // (longer than the server's 15 s keep-alive), and room chart REST calls
            // by the sync coordinator's per-request deadlines.
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-LumaChart-Version",
            ClientVersion);
        eventClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-LumaChart-Version",
            ClientVersion);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-LumaChart-Client-Channel",
            AvaloniaClientBuildInfo.Channel);
        eventClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-LumaChart-Client-Channel",
            AvaloniaClientBuildInfo.Channel);
        // Lets the server resolve the update target, login gate and download
        // link for this platform, which is versioned independently of the others.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-LumaChart-Client-Platform",
            AvaloniaClientBuildInfo.Platform);
        eventClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-LumaChart-Client-Platform",
            AvaloniaClientBuildInfo.Platform);
        if (profileName != null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-LumaChart-Client-Profile",
                profileName);
            eventClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-LumaChart-Client-Profile",
                profileName);
        }
        return new WindowsOnlineAccountHost(client, eventClient, endpoint, profileName);
    }

    public static Uri ResolveEndpoint(string? baseDirectory = null)
    {
        var configured = Environment.GetEnvironmentVariable("LUMACHART_SERVER_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            var file = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "server-endpoint.txt");
            try
            {
                if (File.Exists(file)) configured = File.ReadAllText(file).Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("无法读取联机服务器配置。", exception);
            }
        }

        if (string.IsNullOrWhiteSpace(configured)) return ProductionEndpoint;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint))
            throw new InvalidOperationException("联机服务器地址无效。");
        var isLoopbackDevelopment = endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp;
        if (endpoint.Scheme != Uri.UriSchemeHttps && !isLoopbackDevelopment)
            throw new InvalidOperationException("联机服务器必须使用 HTTPS；仅本机测试可使用 HTTP。");
        return endpoint;
    }

    public static string? ResolveProfileName(string? baseDirectory = null)
    {
        var profile = Environment.GetEnvironmentVariable("LUMACHART_PROFILE")?.Trim();
        if (string.IsNullOrWhiteSpace(profile))
        {
            var file = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "client-profile.txt");
            try
            {
                if (File.Exists(file)) profile = File.ReadAllText(file).Trim();
            }
            catch
            {
                profile = null;
            }
        }
        if (string.IsNullOrWhiteSpace(profile)) return null;
        profile = Regex.Replace(profile, "[^A-Za-z0-9_-]", "-").Trim('-');
        return profile.Length == 0 ? null : profile[..Math.Min(profile.Length, 32)];
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        eventClient.Dispose();
        client.Dispose();
    }

    private static string FormatHttpFailure(HttpStatusCode statusCode) =>
        AvaloniaLocalizationCatalog.Format("服务器请求失败（{0}）。", (int)statusCode);
}

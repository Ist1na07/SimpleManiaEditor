namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Mirrors the WPF 1.1.0 shell-launch boundary: only web links may be handed
/// to Windows. File, executable, custom-protocol, and relative values fail
/// closed before Process.Start is reached.
/// </summary>
internal static class WindowsExternalUriPolicy
{
    public static Uri? Resolve(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http"
            ? uri
            : null;
}

using System.Diagnostics;
using LumaChart.Application.Archives;
using LumaChart.Avalonia.Audio;
using LumaChart.Storage;

namespace LumaChart.Avalonia.Services;

public interface IPlatformExternalUriService
{
    Task OpenExternalUriAsync(
        Uri uri,
        CancellationToken cancellationToken = default);
}

public static class PlatformExternalUriServices
{
    public static IPlatformExternalUriService? Current { get; set; }

    public static Task OpenAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return (Current ?? throw new InvalidOperationException(
                "The platform external URI service is not registered."))
            .OpenExternalUriAsync(uri, cancellationToken);
    }
}

public interface IPlatformTextInputService
{
    bool EnablePasswordInputMethod { get; }
}

public static class PlatformTextInputServices
{
    public static IPlatformTextInputService? Current { get; set; }
}

public static class PlatformSessionTokenStores
{
    public static ISessionTokenStore? Current { get; set; }
}

public static class PlatformArchiveDirectoryCreators
{
    public static IArchiveDirectoryCreator? Current { get; set; }
}

public static class PlatformJapaneseRomanizationServices
{
    public static string? DictionaryRoot { get; set; }
}

internal sealed class WindowsShellExternalUriService : IPlatformExternalUriService
{
    public Task OpenExternalUriAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }
}

internal sealed class WindowsTextInputService : IPlatformTextInputService
{
    public bool EnablePasswordInputMethod => false;
}

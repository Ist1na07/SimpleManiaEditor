using LumaChart.Storage;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Windows-owned durable and transient roots for the Avalonia client.
/// </summary>
public sealed class WindowsAvaloniaAppStorageLocations : IAppStorageLocations
{
    public WindowsAvaloniaAppStorageLocations(
        string settings,
        string persistentEditing,
        string resourceCache,
        string sessionScratch)
    {
        Settings = Path.GetFullPath(settings);
        PersistentEditing = Path.GetFullPath(persistentEditing);
        ResourceCache = Path.GetFullPath(resourceCache);
        SessionScratch = Path.GetFullPath(sessionScratch);
    }

    public string Settings { get; }
    public string PersistentEditing { get; }
    public string ResourceCache { get; }
    public string SessionScratch { get; }
}

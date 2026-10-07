namespace LumaChart.Avalonia.Services;

/// <summary>
/// Platform-owned import boundary. Windows keeps Avalonia StorageProvider;
/// Android uses SAF and returns an app-owned local copy.
/// </summary>
public interface IPlatformFileImportService
{
    bool SupportsWorkspaceDirectorySelection { get; }

    Task<string?> PickChartFileAsync(CancellationToken cancellationToken = default);
}

public static class PlatformFileImportServices
{
    public static IPlatformFileImportService? Current { get; set; }
}

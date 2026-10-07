namespace LumaChart.Avalonia.Services;

/// <summary>
/// Platform export boundary. The shared export pipeline writes a real file and
/// verifies it, so a platform whose destination is not a filesystem path hands
/// back a staging path to write into and takes over once the bytes exist.
/// Windows leaves this unset and keeps using its own save picker.
/// </summary>
public interface IPlatformFileExportService
{
    /// <summary>
    /// App-owned root that Save As must stay inside on a platform whose save
    /// picker cannot return a writable file-system path. The shared editor then
    /// asks for a name in-app and keeps editing the new file, instead of
    /// silently doing nothing with a document URI it cannot bind a tab to.
    /// Windows returns null and keeps its own save picker.
    /// </summary>
    string? InAppSaveAsRoot { get; }

    /// <summary>
    /// Asks the user where the export should go. Returns null when the user
    /// cancels.
    /// </summary>
    Task<IPlatformExportDestination?> PickExportDestinationAsync(
        string suggestedFileName,
        string extension,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A chosen export destination. The caller writes the export to
/// <see cref="StagingPath"/> and then commits; disposing without committing
/// discards the staged bytes.
/// </summary>
public interface IPlatformExportDestination : IAsyncDisposable
{
    /// <summary>Filesystem path the shared export pipeline writes to.</summary>
    string StagingPath { get; }

    /// <summary>What to show the user as the export location.</summary>
    string DisplayName { get; }

    /// <summary>Moves the staged bytes to the destination the user picked.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);
}

public static class PlatformFileExportServices
{
    public static IPlatformFileExportService? Current { get; set; }
}

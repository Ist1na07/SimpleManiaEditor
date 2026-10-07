namespace LumaChart.Storage;

/// <summary>
/// Optional platform-owned private chart library used as the initial workspace.
/// </summary>
public interface IAppLibraryStorageLocation
{
    string Library { get; }
}

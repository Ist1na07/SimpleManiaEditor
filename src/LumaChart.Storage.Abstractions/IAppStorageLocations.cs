namespace LumaChart.Storage;

/// <summary>
/// Provides platform-owned roots for durable settings, editing workspaces,
/// reusable resources and process-scoped scratch data.
/// </summary>
public interface IAppStorageLocations
{
    string Settings { get; }
    string PersistentEditing { get; }
    string ResourceCache { get; }
    string SessionScratch { get; }
}

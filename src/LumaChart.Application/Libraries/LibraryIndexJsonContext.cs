using System.Text.Json.Serialization;

namespace LumaChart.Application.Libraries;

/// <summary>
/// Reflection-free JSON for the persisted library index. A 47k-row snapshot
/// deserializes noticeably faster than reflection-based metadata, and this is
/// the dominant cost of the cache-first library entry.
/// </summary>
[JsonSerializable(typeof(DesktopLibraryChartEntry))]
[JsonSerializable(typeof(MapperAnnotation))]
internal sealed partial class LibraryIndexJsonContext : JsonSerializerContext
{
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LumaChart.Application.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Infrastructure;

public sealed record CenterSymmetryToolState(bool Enabled, CenterSymmetrySettings Settings);

/// <summary>Per-document/track plain JSON; never embedded in the chart export.</summary>
public static class CenterSymmetryStateStore
{
    private sealed record Stored(bool Enabled, string Start, string End, int[] Columns);
    private static string StatePath(string source, string track) => Path.Combine(AvaloniaAppPaths.LocalDataRoot, "drawing-tools",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant() + "|" + track))) + ".json");

    public static CenterSymmetryToolState Load(string source, string track, int keys)
    {
        var path = StatePath(source, track);
        try
        {
            if (WindowsSettingsFileGuard.TryReadExisting(path, out var bytes) && JsonSerializer.Deserialize<Stored>(bytes) is { } value)
            {
                if (!BeatPosition.TryParse(value.Start, out var start) || !BeatPosition.TryParse(value.End, out var end))
                    throw new InvalidDataException("对称工具的范围位置无效。");
                var settings = new CenterSymmetrySettings(start, end, value.Columns);
                settings.Validate(keys); return new(value.Enabled, settings);
            }
        }
        catch (Exception ex) { WindowsSettingsFileGuard.ReportLoadFailure(path, ex); }
        return new(false, new(BeatPosition.Zero, new BeatPosition(32, 0, 1), Enumerable.Range(0, keys).ToArray()));
    }

    public static void Save(string source, string track, CenterSymmetryToolState state)
    {
        var path = StatePath(source, track); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(new Stored(state.Enabled, state.Settings.Start.ToString(), state.Settings.End.ToString(), state.Settings.Columns.ToArray()),
                new JsonSerializerOptions { WriteIndented = true });
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(data); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

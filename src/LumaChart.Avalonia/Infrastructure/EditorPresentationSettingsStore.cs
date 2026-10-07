using System.Text.Json;

namespace LumaChart.Avalonia.Infrastructure;

public enum EditorNoteStyle { Osu, Malody }

/// <summary>Editor appearance preferences kept separately from legacy geometry settings.</summary>
public static class EditorPresentationSettingsStore
{
    public static string SettingsPath => Path.Combine(AvaloniaAppPaths.LocalDataRoot, "editor-presentation.json");

    public static EditorNoteStyle LoadNoteStyle()
    {
        try
        {
            if (!WindowsSettingsFileGuard.TryReadExisting(SettingsPath, out var bytes)) return EditorNoteStyle.Osu;
            var value = JsonSerializer.Deserialize<Preferences>(bytes)?.NoteStyle ?? EditorNoteStyle.Osu;
            return Enum.IsDefined(value) ? value : EditorNoteStyle.Osu;
        }
        catch (Exception exception)
        {
            WindowsSettingsFileGuard.ReportLoadFailure(SettingsPath, exception);
            return EditorNoteStyle.Osu;
        }
    }

    public static void SaveNoteStyle(EditorNoteStyle value)
    {
        if (!WindowsSettingsFileGuard.CanWrite(SettingsPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Preferences { NoteStyle = value });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class Preferences { public EditorNoteStyle NoteStyle { get; set; } = EditorNoteStyle.Osu; }
}

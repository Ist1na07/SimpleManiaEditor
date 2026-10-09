using System.Text.Json;

namespace LumaChart.Avalonia.Infrastructure;

public enum EditorNoteStyle { Osu, Malody }

/// <summary>Editor appearance preferences kept separately from legacy geometry settings.</summary>
public static class EditorPresentationSettingsStore
{
    public static string SettingsPath => Path.Combine(AvaloniaAppPaths.LocalDataRoot, "editor-presentation.json");

    public static EditorNoteStyle LoadNoteStyle()
        => ReadPreferences().NoteStyle;

    public static EditorGridNotation LoadGridNotation() => ReadPreferences().GridNotation;
    public static int LoadLayoutRevision() => ReadPreferences().LayoutRevision;
    public static double LoadPlaytestRate() => ReadPreferences().PlaytestRate;
    public static bool LoadSnapAll() => ReadPreferences().SnapAll;
    public static void SavePlaytestRate(double value) { var preferences = ReadPreferences(); preferences.PlaytestRate = value; Save(preferences); }
    public static void SaveSnapAll(bool value) { var preferences = ReadPreferences(); preferences.SnapAll = value; Save(preferences); }

    private static Preferences ReadPreferences()
    {
        try
        {
            if (!WindowsSettingsFileGuard.TryReadExisting(SettingsPath, out var bytes)) return new();
            var value = JsonSerializer.Deserialize<Preferences>(bytes) ?? new();
            if (!Enum.IsDefined(value.NoteStyle)) value.NoteStyle = EditorNoteStyle.Osu;
            if (!Enum.IsDefined(value.GridNotation)) value.GridNotation = EditorGridNotation.Osu;
            if (!double.IsFinite(value.PlaytestRate) || value.PlaytestRate is < .2 or > 2) value.PlaytestRate = 1;
            return value;
        }
        catch (Exception exception)
        {
            WindowsSettingsFileGuard.ReportLoadFailure(SettingsPath, exception);
            return new();
        }
    }

    public static void SaveNoteStyle(EditorNoteStyle value)
    {
        var preferences = ReadPreferences();
        preferences.NoteStyle = value;
        Save(preferences);
    }

    public static void SaveGridNotation(EditorGridNotation value)
    {
        var preferences = ReadPreferences();
        preferences.GridNotation = value;
        Save(preferences);
    }

    public static void SaveLayoutRevision(int value)
    {
        var preferences = ReadPreferences();
        preferences.LayoutRevision = value;
        Save(preferences);
    }

    private static void Save(Preferences preferences)
    {
        if (!WindowsSettingsFileGuard.CanWrite(SettingsPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class Preferences
    {
        public EditorNoteStyle NoteStyle { get; set; } = EditorNoteStyle.Osu;
        public EditorGridNotation GridNotation { get; set; } = EditorGridNotation.Osu;
        public int LayoutRevision { get; set; }
        public double PlaytestRate { get; set; } = 1;
        public bool SnapAll { get; set; } = true;
    }
}

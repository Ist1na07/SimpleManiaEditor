using System.Text.Json;
using System.Text.Json.Serialization;

namespace LumaChart.Avalonia.Infrastructure;

public sealed class WindowsEditorBackgroundSettings
{
    public const double DefaultDarkness = .70;

    public WindowsEditorBackgroundSettings(double darkness = DefaultDarkness)
    {
        Darkness = Math.Clamp(
            double.IsFinite(darkness) ? darkness : DefaultDarkness,
            0,
            1);
    }

    public static WindowsEditorBackgroundSettings Default => new();

    public double Darkness { get; }

    public WindowsEditorBackgroundSettings WithDarkness(double darkness) => new(darkness);
}

public static class WindowsEditorBackgroundSettingsStore
{
    private const int CurrentSchemaVersion = 2;
    private const double LegacyDefaultTransparency = .05;
    private const string FileName = "editor-background-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsPath => Path.Combine(AvaloniaAppPaths.LocalDataRoot, FileName);

    public static WindowsEditorBackgroundSettings Load()
    {
        string? path = null;
        try
        {
            path = SettingsPath;
            if (!WindowsSettingsFileGuard.TryReadExisting(path, out var bytes))
                return WindowsEditorBackgroundSettings.Default;
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(
                bytes,
                JsonOptions);
            if (persisted == null) return WindowsEditorBackgroundSettings.Default;
            if (persisted.SchemaVersion == CurrentSchemaVersion &&
                persisted.Darkness is { } darkness)
                return new WindowsEditorBackgroundSettings(darkness);
            if (persisted.SchemaVersion == 1 &&
                persisted.Transparency is { } legacyTransparency)
            {
                var migratedDarkness =
                    Math.Abs(legacyTransparency - LegacyDefaultTransparency) < 1e-12
                        ? WindowsEditorBackgroundSettings.DefaultDarkness
                        : legacyTransparency;
                return new WindowsEditorBackgroundSettings(migratedDarkness);
            }
            return WindowsEditorBackgroundSettings.Default;
        }
        catch (Exception exception)
        {
            if (path != null) WindowsSettingsFileGuard.ReportLoadFailure(path, exception);
            return WindowsEditorBackgroundSettings.Default;
        }
    }

    public static void Save(WindowsEditorBackgroundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = AvaloniaAppPaths.LocalDataRoot;
        // An existing file that could not be read this session is kept intact.
        if (!WindowsSettingsFileGuard.CanWrite(Path.Combine(root, FileName))) return;
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(
                temporary,
                JsonSerializer.SerializeToUtf8Bytes(
                    new PersistedSettings
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        Darkness = settings.Darkness
                    },
                    JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class PersistedSettings
    {
        public int SchemaVersion { get; set; }
        public double? Darkness { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Transparency { get; set; }
    }
}

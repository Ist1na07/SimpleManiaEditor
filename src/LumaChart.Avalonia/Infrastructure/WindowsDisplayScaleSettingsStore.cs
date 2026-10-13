using System.Text.Json;
using LumaChart.Application.Settings;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Immutable Windows Avalonia display-scale preferences. These values are kept
/// separate from the WPF view settings so neither client can overwrite the
/// other's persisted UI state during the migration.
/// </summary>
public sealed class WindowsDisplayScaleSettings
{
    public WindowsDisplayScaleSettings(
        double uiScale = 1,
        double fontScale = 1,
        bool displayScaleInitialized = false,
        bool? automaticUiScale = null)
    {
        UiScale = DisplayScalePolicy.NormalizeUiScale(uiScale);
        FontScale = DisplayScalePolicy.NormalizeFontScale(fontScale);
        DisplayScaleInitialized = displayScaleInitialized;
        AutomaticUiScale = automaticUiScale ?? !displayScaleInitialized;
    }

    public static WindowsDisplayScaleSettings Default { get; } = new();

    public bool AutomaticUiScale { get; }
    public double UiScale { get; }
    public double FontScale { get; }
    public bool DisplayScaleInitialized { get; }

    public WindowsDisplayScaleSettings Normalize() => new(
        UiScale,
        FontScale,
        DisplayScaleInitialized, AutomaticUiScale);

    public WindowsDisplayScaleSettings WithUiScale(double value) => new(
        value,
        FontScale,
        true, false);

    public WindowsDisplayScaleSettings WithFontScale(double value) => new(
        UiScale,
        value,
        DisplayScaleInitialized, AutomaticUiScale);

    /// <summary>
    /// Applies the current-monitor recommendation in automatic mode. Width and
    /// height must be effective DIPs, not physical pixels.
    /// </summary>
    public WindowsDisplayScaleSettings InitializeForFirstScreen(
        double effectiveScreenWidth,
        double effectiveScreenHeight)
    {
        if (DisplayScaleInitialized && !AutomaticUiScale) return Normalize();
        return new WindowsDisplayScaleSettings(
            DisplayScalePolicy.RecommendUiScale(
                effectiveScreenWidth,
                effectiveScreenHeight),
            fontScale: FontScale,
            displayScaleInitialized: true, automaticUiScale: true);
    }
}

/// <summary>
/// Atomically persists Windows Avalonia display scaling under Avalonia's own
/// local-data root. It never reads or writes the WPF view-settings.json file.
/// </summary>
public static class WindowsDisplayScaleSettingsStore
{
    public const int CurrentSchemaVersion = 2;
    private const string FileName = "display-scale-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsPath => Path.Combine(
        AvaloniaAppPaths.LocalDataRoot,
        FileName);

    public static WindowsDisplayScaleSettings Load()
    {
        string? path = null;
        try
        {
            path = SettingsPath;
            if (!WindowsSettingsFileGuard.TryReadExisting(path, out var bytes))
                return WindowsDisplayScaleSettings.Default;
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(
                bytes,
                JsonOptions);
            return persisted?.SchemaVersion is 1 or CurrentSchemaVersion
                ? new WindowsDisplayScaleSettings(
                    persisted.UiScale,
                    persisted.FontScale,
                    persisted.DisplayScaleInitialized,
                    persisted.AutomaticUiScale ?? (!persisted.DisplayScaleInitialized || Math.Abs(persisted.UiScale - 1) < .001))
                : WindowsDisplayScaleSettings.Default;
        }
        catch (Exception exception)
        {
            if (path != null) WindowsSettingsFileGuard.ReportLoadFailure(path, exception);
            return WindowsDisplayScaleSettings.Default;
        }
    }

    public static void Save(WindowsDisplayScaleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var root = AvaloniaAppPaths.LocalDataRoot;
        var destination = Path.Combine(root, FileName);
        // An existing file that could not be read this session is kept intact.
        if (!WindowsSettingsFileGuard.CanWrite(destination)) return;
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(
            root,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedSettings
                {
                    SchemaVersion = CurrentSchemaVersion,
                    UiScale = normalized.UiScale,
                    FontScale = normalized.FontScale,
                    DisplayScaleInitialized = normalized.DisplayScaleInitialized,
                    AutomaticUiScale = normalized.AutomaticUiScale
                },
                JsonOptions);
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
                File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, destination);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Preserve the primary settings-write failure.
            }
        }
    }

    private sealed class PersistedSettings
    {
        public bool? AutomaticUiScale { get; set; }
        public int SchemaVersion { get; set; }
        public double UiScale { get; set; } = 1;
        public double FontScale { get; set; } = 1;
        public bool DisplayScaleInitialized { get; set; }
    }
}

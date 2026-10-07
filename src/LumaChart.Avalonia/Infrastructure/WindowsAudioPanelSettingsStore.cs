using System.Text.Json;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// The two audio-visualization presentation modes supported by the Windows
/// editor. Side owns a narrow editor-height surface; Background renders into
/// the timeline and leaves only the floating toolbar visible.
/// </summary>
public enum WindowsAudioPanelDisplayMode
{
    Side,
    Background
}

public enum WindowsAudioPanelKind
{
    Waveform,
    Spectrogram
}

/// <summary>
/// Immutable persisted state for one Windows audio panel. Coordinates and
/// Width use effective DIPs. Use <see cref="Normalize"/> before consuming a
/// state constructed outside <see cref="WindowsAudioPanelSettings"/>.
/// </summary>
public sealed class WindowsAudioPanelState
{
    public WindowsAudioPanelState(
        double x,
        double y,
        double width,
        bool collapsed,
        bool docked,
        WindowsAudioPanelDisplayMode mode)
    {
        X = x;
        Y = y;
        Width = width;
        Collapsed = collapsed;
        Docked = docked;
        Mode = mode;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public bool Collapsed { get; }
    public bool Docked { get; }
    public bool DockRight => Docked;
    public WindowsAudioPanelDisplayMode Mode { get; }
    public bool IsToolbarOnly => Collapsed || Mode == WindowsAudioPanelDisplayMode.Background;

    public WindowsAudioPanelState Normalize(WindowsAudioPanelKind kind)
    {
        var defaultWidth = kind == WindowsAudioPanelKind.Waveform
            ? WindowsAudioPanelSettings.DefaultWaveformWidth
            : WindowsAudioPanelSettings.DefaultSpectrogramWidth;
        var minimumWidth = kind == WindowsAudioPanelKind.Waveform
            ? WindowsAudioPanelSettings.MinimumWaveformWidth
            : WindowsAudioPanelSettings.MinimumSpectrogramWidth;
        var maximumWidth = kind == WindowsAudioPanelKind.Waveform
            ? WindowsAudioPanelSettings.MaximumWaveformWidth
            : WindowsAudioPanelSettings.MaximumSpectrogramWidth;

        return new WindowsAudioPanelState(
            Math.Clamp(FiniteOr(X, WindowsAudioPanelSettings.DefaultPanelX), 0, WindowsAudioPanelSettings.DefaultPanelX),
            Math.Clamp(FiniteOr(Y, 0), -WindowsAudioPanelSettings.MaximumStoredY, WindowsAudioPanelSettings.MaximumStoredY),
            Math.Clamp(FiniteOr(Width, defaultWidth), minimumWidth, maximumWidth),
            Collapsed,
            Docked,
            Enum.IsDefined(Mode) ? Mode : WindowsAudioPanelDisplayMode.Side);
    }

    public WindowsAudioPanelState WithPosition(double x, double y) => new(
        x,
        y,
        Width,
        Collapsed,
        Docked,
        Mode);

    public WindowsAudioPanelState WithWidth(double width) => new(
        X,
        Y,
        width,
        Collapsed,
        Docked,
        Mode);

    public WindowsAudioPanelState WithCollapsed(bool collapsed) => new(
        X,
        Y,
        Width,
        collapsed,
        Docked,
        Mode);

    public WindowsAudioPanelState WithDocked(bool docked) => new(
        X,
        Y,
        Width,
        Collapsed,
        docked,
        Mode);

    public WindowsAudioPanelState WithMode(WindowsAudioPanelDisplayMode mode) => new(
        X,
        Y,
        Width,
        Collapsed,
        Docked,
        mode);

    private static double FiniteOr(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}

/// <summary>
/// Windows Avalonia's isolated waveform and spectrogram layout preferences.
/// This never reads or writes WPF's view-settings.json during migration.
/// </summary>
public sealed class WindowsAudioPanelSettings
{
    public const double DefaultPanelX = 4000;
    public const double MaximumStoredY = 4000;
    public const double DefaultWaveformWidth = 92;
    public const double MinimumWaveformWidth = 56;
    public const double MaximumWaveformWidth = 180;
    public const double DefaultSpectrogramWidth = 132;
    public const double MinimumSpectrogramWidth = 88;
    public const double MaximumSpectrogramWidth = 220;

    public const double DefaultBackgroundSpectrogramStrength = Audio.SpectrogramLevelMapping.DefaultBackgroundStrength;
    public const double MinimumBackgroundSpectrogramStrength = .2;
    public const double DefaultSpectrogramDetail = Audio.SpectrogramLevelMapping.DefaultDetail;

    public WindowsAudioPanelSettings(
        WindowsAudioPanelState? waveform = null,
        WindowsAudioPanelState? spectrogram = null,
        double backgroundSpectrogramStrength = DefaultBackgroundSpectrogramStrength,
        double spectrogramDetail = DefaultSpectrogramDetail)
    {
        Waveform = (waveform ?? CreateDefaultWaveform()).Normalize(WindowsAudioPanelKind.Waveform);
        Spectrogram = (spectrogram ?? CreateDefaultSpectrogram()).Normalize(WindowsAudioPanelKind.Spectrogram);
        BackgroundSpectrogramStrength = double.IsFinite(backgroundSpectrogramStrength)
            ? Math.Clamp(backgroundSpectrogramStrength, MinimumBackgroundSpectrogramStrength, 1)
            : DefaultBackgroundSpectrogramStrength;
        SpectrogramDetail = Audio.SpectrogramLevelMapping.NormalizeDetail(spectrogramDetail);
    }

    public static WindowsAudioPanelSettings Default => new();

    public WindowsAudioPanelState Waveform { get; }
    public WindowsAudioPanelState Spectrogram { get; }

    /// <summary>How prominent the background spectrogram is (0.2–1).</summary>
    public double BackgroundSpectrogramStrength { get; }

    /// <summary>
    /// How much of the spectrum is shown, for the background and the side
    /// panel alike: 0 keeps only the strongest onsets, 1 everything above the
    /// noise floor.
    /// </summary>
    public double SpectrogramDetail { get; }

    public WindowsAudioPanelSettings Normalize() =>
        new(Waveform, Spectrogram, BackgroundSpectrogramStrength, SpectrogramDetail);

    public WindowsAudioPanelSettings WithWaveform(WindowsAudioPanelState waveform)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        return new WindowsAudioPanelSettings(waveform, Spectrogram, BackgroundSpectrogramStrength, SpectrogramDetail);
    }

    public WindowsAudioPanelSettings WithSpectrogram(WindowsAudioPanelState spectrogram)
    {
        ArgumentNullException.ThrowIfNull(spectrogram);
        return new WindowsAudioPanelSettings(Waveform, spectrogram, BackgroundSpectrogramStrength, SpectrogramDetail);
    }

    /// <summary>Replaces both panel layouts and keeps the spectrogram appearance.</summary>
    public WindowsAudioPanelSettings WithPanels(WindowsAudioPanelState waveform, WindowsAudioPanelState spectrogram)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        ArgumentNullException.ThrowIfNull(spectrogram);
        return new WindowsAudioPanelSettings(waveform, spectrogram, BackgroundSpectrogramStrength, SpectrogramDetail);
    }

    public WindowsAudioPanelSettings WithSpectrogramAppearance(double strength, double detail) =>
        new(Waveform, Spectrogram, strength, detail);

    public static WindowsAudioPanelSettings Reset() => Default;

    private static WindowsAudioPanelState CreateDefaultWaveform() => new(
        DefaultPanelX,
        y: 0,
        DefaultWaveformWidth,
        collapsed: false,
        docked: true,
        WindowsAudioPanelDisplayMode.Side);

    private static WindowsAudioPanelState CreateDefaultSpectrogram() => new(
        DefaultPanelX,
        y: 0,
        DefaultSpectrogramWidth,
        collapsed: true,
        docked: true,
        WindowsAudioPanelDisplayMode.Side);
}

/// <summary>
/// Atomically persists Windows Avalonia audio-panel state under Avalonia's own
/// local-data root. Corrupt, incomplete, or unknown-schema data fails closed to
/// the WPF-compatible factory layout.
/// </summary>
public static class WindowsAudioPanelSettingsStore
{
    public const int CurrentSchemaVersion = 1;
    private const string FileName = "audio-panel-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsPath => Path.Combine(AvaloniaAppPaths.LocalDataRoot, FileName);

    public static WindowsAudioPanelSettings Load()
    {
        string? path = null;
        try
        {
            path = SettingsPath;
            if (!WindowsSettingsFileGuard.TryReadExisting(path, out var bytes))
                return WindowsAudioPanelSettings.Default;
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(
                bytes,
                JsonOptions);
            if (persisted?.SchemaVersion != CurrentSchemaVersion ||
                !TryReadPanel(persisted.Waveform, WindowsAudioPanelKind.Waveform, out var waveform) ||
                !TryReadPanel(persisted.Spectrogram, WindowsAudioPanelKind.Spectrogram, out var spectrogram))
            {
                return WindowsAudioPanelSettings.Default;
            }

            // Added after schema 1 shipped; a file without them keeps the defaults.
            return new WindowsAudioPanelSettings(
                waveform,
                spectrogram,
                persisted.BackgroundSpectrogramStrength ?? WindowsAudioPanelSettings.DefaultBackgroundSpectrogramStrength,
                persisted.SpectrogramDetail ?? WindowsAudioPanelSettings.DefaultSpectrogramDetail);
        }
        catch (Exception exception)
        {
            if (path != null) WindowsSettingsFileGuard.ReportLoadFailure(path, exception);
            return WindowsAudioPanelSettings.Default;
        }
    }

    public static void Save(WindowsAudioPanelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        var root = AvaloniaAppPaths.LocalDataRoot;
        var destination = Path.Combine(root, FileName);
        // An existing file that could not be read this session is kept intact.
        if (!WindowsSettingsFileGuard.CanWrite(destination)) return;
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var persisted = new PersistedSettings
            {
                SchemaVersion = CurrentSchemaVersion,
                Waveform = WritePanel(normalized.Waveform),
                Spectrogram = WritePanel(normalized.Spectrogram),
                BackgroundSpectrogramStrength = normalized.BackgroundSpectrogramStrength,
                SpectrogramDetail = normalized.SpectrogramDetail
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions);
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

    private static bool TryReadPanel(
        PersistedPanel? persisted,
        WindowsAudioPanelKind kind,
        out WindowsAudioPanelState state)
    {
        state = kind == WindowsAudioPanelKind.Waveform
            ? WindowsAudioPanelSettings.Default.Waveform
            : WindowsAudioPanelSettings.Default.Spectrogram;
        if (persisted?.X is not double x ||
            persisted.Y is not double y ||
            persisted.Width is not double width ||
            persisted.Collapsed is not bool collapsed ||
            persisted.Docked is not bool docked ||
            !double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(width) ||
            !Enum.TryParse<WindowsAudioPanelDisplayMode>(persisted.Mode, ignoreCase: true, out var mode) ||
            !Enum.IsDefined(mode))
        {
            return false;
        }

        state = new WindowsAudioPanelState(x, y, width, collapsed, docked, mode).Normalize(kind);
        return true;
    }

    private static PersistedPanel WritePanel(WindowsAudioPanelState state) => new()
    {
        X = state.X,
        Y = state.Y,
        Width = state.Width,
        Collapsed = state.Collapsed,
        Docked = state.Docked,
        Mode = state.Mode.ToString()
    };

    private sealed class PersistedSettings
    {
        public int? SchemaVersion { get; set; }
        public PersistedPanel? Waveform { get; set; }
        public PersistedPanel? Spectrogram { get; set; }
        public double? BackgroundSpectrogramStrength { get; set; }
        public double? SpectrogramDetail { get; set; }
    }

    private sealed class PersistedPanel
    {
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Width { get; set; }
        public bool? Collapsed { get; set; }
        public bool? Docked { get; set; }
        public string? Mode { get; set; }
    }
}

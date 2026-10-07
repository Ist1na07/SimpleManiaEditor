using System.Collections.ObjectModel;
using System.Text.Json;
using LumaChart.Avalonia.Audio;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Immutable, normalized Windows editor Key-sound settings. Paths are retained
/// independently from the WPF settings and the Avalonia workspace manifest.
/// </summary>
public sealed class WindowsKeySoundSettings
{
    public const int MinimumOffsetMilliseconds = -500;
    public const int MaximumOffsetMilliseconds = 500;
    public const double DefaultVolume = .85;

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly IReadOnlyDictionary<string, int> perChartOffsets;

    public WindowsKeySoundSettings(
        bool enableKeySounds = false,
        double volume = DefaultVolume,
        KeySoundPreset preset = KeySoundPreset.Snare,
        string? customFilePath = null,
        bool playLongNoteEndSound = false,
        int globalOffset = 0,
        IEnumerable<KeyValuePair<string, int>>? perChartOffsets = null)
    {
        EnableKeySounds = enableKeySounds;
        Volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : DefaultVolume;
        Preset = Enum.IsDefined(preset) ? preset : KeySoundPreset.Snare;
        CustomFilePath = TryCanonicalizePath(customFilePath, out var normalizedCustomFile)
            ? normalizedCustomFile
            : null;
        PlayLongNoteEndSound = playLongNoteEndSound;
        GlobalOffset = NormalizeOffset(globalOffset);
        this.perChartOffsets = NormalizeChartOffsets(perChartOffsets);
    }

    public static WindowsKeySoundSettings Default { get; } = new();

    public bool EnableKeySounds { get; }
    public double Volume { get; }
    public KeySoundPreset Preset { get; }
    public string? CustomFilePath { get; }
    public bool PlayLongNoteEndSound { get; }
    public int GlobalOffset { get; }
    public int GlobalOffsetMilliseconds => GlobalOffset;
    public IReadOnlyDictionary<string, int> PerChartOffsets => perChartOffsets;

    public WindowsKeySoundSettings Normalize() => new(
        EnableKeySounds,
        Volume,
        Preset,
        CustomFilePath,
        PlayLongNoteEndSound,
        GlobalOffset,
        perChartOffsets);

    public WindowsKeySoundSettings WithEnabled(bool value) => new(
        value,
        Volume,
        Preset,
        CustomFilePath,
        PlayLongNoteEndSound,
        GlobalOffset,
        perChartOffsets);

    public WindowsKeySoundSettings WithVolume(double value) => new(
        EnableKeySounds,
        value,
        Preset,
        CustomFilePath,
        PlayLongNoteEndSound,
        GlobalOffset,
        perChartOffsets);

    public WindowsKeySoundSettings WithPreset(KeySoundPreset value) => new(
        EnableKeySounds,
        Volume,
        value,
        CustomFilePath,
        PlayLongNoteEndSound,
        GlobalOffset,
        perChartOffsets);

    public WindowsKeySoundSettings WithCustomFilePath(string? value) => new(
        EnableKeySounds,
        Volume,
        Preset,
        value,
        PlayLongNoteEndSound,
        GlobalOffset,
        perChartOffsets);

    public WindowsKeySoundSettings WithLongNoteEndSound(bool value) => new(
        EnableKeySounds,
        Volume,
        Preset,
        CustomFilePath,
        value,
        GlobalOffset,
        perChartOffsets);

    public WindowsKeySoundSettings WithGlobalOffset(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "A global offset must be finite.");
        return new WindowsKeySoundSettings(
            EnableKeySounds,
            Volume,
            Preset,
            CustomFilePath,
            PlayLongNoteEndSound,
            (int)Math.Round(value),
            perChartOffsets);
    }

    public int GetChartOffset(string? chartPath) =>
        TryCanonicalizePath(chartPath, out var canonicalPath) &&
        perChartOffsets.TryGetValue(canonicalPath, out var offset)
            ? offset
            : 0;

    public WindowsKeySoundSettings SetChartOffset(string chartPath, double offsetMilliseconds)
    {
        if (!TryCanonicalizePath(chartPath, out var canonicalPath))
            throw new ArgumentException("A chart offset requires a valid chart path.", nameof(chartPath));
        if (!double.IsFinite(offsetMilliseconds))
            throw new ArgumentOutOfRangeException(
                nameof(offsetMilliseconds),
                "A chart offset must be finite.");

        var normalizedOffset = NormalizeOffset((int)Math.Round(Math.Clamp(
            offsetMilliseconds,
            MinimumOffsetMilliseconds,
            MaximumOffsetMilliseconds)));
        var next = new Dictionary<string, int>(perChartOffsets, PathComparer);
        if (normalizedOffset == 0) next.Remove(canonicalPath);
        else next[canonicalPath] = normalizedOffset;
        return new WindowsKeySoundSettings(
            EnableKeySounds,
            Volume,
            Preset,
            CustomFilePath,
            PlayLongNoteEndSound,
            GlobalOffset,
            next);
    }

    internal static bool TryCanonicalizePath(string? path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return Path.IsPathFullyQualified(canonicalPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            canonicalPath = string.Empty;
            return false;
        }
    }

    private static int NormalizeOffset(int value) =>
        Math.Clamp(value, MinimumOffsetMilliseconds, MaximumOffsetMilliseconds);

    private static IReadOnlyDictionary<string, int> NormalizeChartOffsets(
        IEnumerable<KeyValuePair<string, int>>? values)
    {
        var normalized = new Dictionary<string, int>(PathComparer);
        if (values != null)
        {
            foreach (var (path, value) in values)
            {
                if (!TryCanonicalizePath(path, out var canonicalPath)) continue;
                var offset = NormalizeOffset(value);
                if (offset == 0) normalized.Remove(canonicalPath);
                else normalized[canonicalPath] = offset;
            }
        }
        return new ReadOnlyDictionary<string, int>(normalized);
    }
}

/// <summary>
/// Loads and atomically persists the Windows Avalonia Key-sound settings file.
/// The path follows AvaloniaAppPaths' local-data override so Specs can isolate it.
/// </summary>
public static class WindowsKeySoundSettingsStore
{
    private const int SchemaVersion = 1;
    private const string FileName = "key-sound-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsPath => Path.Combine(AvaloniaAppPaths.LocalDataRoot, FileName);

    public static WindowsKeySoundSettings Load()
    {
        string? path = null;
        try
        {
            path = SettingsPath;
            if (!WindowsSettingsFileGuard.TryReadExisting(path, out var bytes))
                return WindowsKeySoundSettings.Default;
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(
                bytes,
                JsonOptions);
            if (persisted?.SchemaVersion != SchemaVersion)
                return WindowsKeySoundSettings.Default;
            var preset = Enum.TryParse<KeySoundPreset>(persisted.Preset, ignoreCase: true, out var parsed) &&
                         Enum.IsDefined(parsed)
                ? parsed
                : KeySoundPreset.Snare;
            return new WindowsKeySoundSettings(
                persisted.EnableKeySounds,
                persisted.Volume,
                preset,
                persisted.CustomFilePath,
                persisted.PlayLongNoteEndSound,
                persisted.GlobalOffset,
                persisted.PerChartOffsets);
        }
        catch (Exception exception)
        {
            if (path != null) WindowsSettingsFileGuard.ReportLoadFailure(path, exception);
            return WindowsKeySoundSettings.Default;
        }
    }

    public static void Save(WindowsKeySoundSettings settings)
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
                SchemaVersion = SchemaVersion,
                EnableKeySounds = normalized.EnableKeySounds,
                Volume = normalized.Volume,
                Preset = normalized.Preset.ToString(),
                CustomFilePath = normalized.CustomFilePath,
                PlayLongNoteEndSound = normalized.PlayLongNoteEndSound,
                GlobalOffset = normalized.GlobalOffset,
                PerChartOffsets = normalized.PerChartOffsets
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
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
            File.Move(temporary, destination, overwrite: true);
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

    public static int GetChartOffset(WindowsKeySoundSettings settings, string? chartPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.GetChartOffset(chartPath);
    }

    public static WindowsKeySoundSettings SetChartOffset(
        WindowsKeySoundSettings settings,
        string chartPath,
        double offsetMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SetChartOffset(chartPath, offsetMilliseconds);
    }

    private sealed class PersistedSettings
    {
        public int SchemaVersion { get; set; }
        public bool EnableKeySounds { get; set; }
        public double Volume { get; set; } = WindowsKeySoundSettings.DefaultVolume;
        public string Preset { get; set; } = nameof(KeySoundPreset.Snare);
        public string? CustomFilePath { get; set; }
        public bool PlayLongNoteEndSound { get; set; }
        public int GlobalOffset { get; set; }
        public Dictionary<string, int>? PerChartOffsets { get; set; }
    }
}

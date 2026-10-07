using System.Collections.ObjectModel;
using System.Text.Json;
using LumaChart.Application.Playtest;

namespace LumaChart.Avalonia.Infrastructure;

public enum WindowsPlaytestNoteStyle
{
    Brick,
    Circle
}

public sealed class WindowsPlaytestSettings
{
    private readonly IReadOnlyDictionary<int, string[]> keyBindings;

    public WindowsPlaytestSettings(
        bool autoPlay = true,
        WindowsPlaytestNoteStyle noteStyle = WindowsPlaytestNoteStyle.Brick,
        double noteWidth = .82,
        double noteHeight = 28,
        double playfieldWidth = 520,
        double scrollSpeed = 760,
        double judgementLinePosition = .84,
        IReadOnlyDictionary<int, string[]>? keyBindings = null,
        double comboYPosition = .12,
        double accuracyYPosition = .19,
        double bestWindowMilliseconds = 45,
        double greatWindowMilliseconds = 85,
        double goodWindowMilliseconds = 130,
        double missWindowMilliseconds = 170,
        double inputOffsetMilliseconds = 0)
    {
        AutoPlay = autoPlay;
        NoteStyle = Enum.IsDefined(noteStyle) ? noteStyle : WindowsPlaytestNoteStyle.Brick;
        NoteWidth = double.IsFinite(noteWidth) ? Math.Clamp(noteWidth, .25, 1) : .82;
        NoteHeight = double.IsFinite(noteHeight) ? Math.Clamp(noteHeight, 10, 120) : 28;
        PlayfieldWidth = double.IsFinite(playfieldWidth) ? Math.Clamp(playfieldWidth, 220, 1600) : 520;
        ScrollSpeed = double.IsFinite(scrollSpeed) ? Math.Clamp(scrollSpeed, 120, 2600) : 760;
        JudgementLinePosition = double.IsFinite(judgementLinePosition)
            ? Math.Clamp(judgementLinePosition, .55, .94)
            : .84;
        ComboYPosition = double.IsFinite(comboYPosition)
            ? Math.Clamp(comboYPosition, .04, .45)
            : .12;
        AccuracyYPosition = double.IsFinite(accuracyYPosition)
            ? Math.Clamp(accuracyYPosition, .04, .45)
            : .19;
        JudgementWindows = new PlaytestJudgementWindows(
            bestWindowMilliseconds,
            greatWindowMilliseconds,
            goodWindowMilliseconds,
            missWindowMilliseconds);
        InputOffsetMilliseconds = double.IsFinite(inputOffsetMilliseconds)
            ? Math.Clamp(inputOffsetMilliseconds, -300, 300)
            : 0;
        this.keyBindings = NormalizeBindings(keyBindings);
    }

    public static WindowsPlaytestSettings Default { get; } = new();

    public bool AutoPlay { get; }
    public WindowsPlaytestNoteStyle NoteStyle { get; }
    public double NoteWidth { get; }
    public double NoteHeight { get; }
    public double PlayfieldWidth { get; }
    public double ScrollSpeed { get; }
    public double JudgementLinePosition { get; }
    public double ComboYPosition { get; }
    public double AccuracyYPosition { get; }
    public PlaytestJudgementWindows JudgementWindows { get; }

    /// <summary>
    /// Shifts the moment a press is judged at. Devices differ in how long audio
    /// takes to reach the speaker, so a player who hears the beat late presses
    /// late through no fault of their own; a negative value pulls judgement
    /// earlier to compensate, a positive value pushes it later.
    /// </summary>
    public double InputOffsetMilliseconds { get; }
    public double BestWindowMilliseconds => JudgementWindows.BestMilliseconds;
    public double GreatWindowMilliseconds => JudgementWindows.GreatMilliseconds;
    public double GoodWindowMilliseconds => JudgementWindows.GoodMilliseconds;
    public double MissWindowMilliseconds => JudgementWindows.MissMilliseconds;
    public IReadOnlyDictionary<int, string[]> KeyBindings => keyBindings;

    public string[] BindingsFor(int keyCount) =>
        keyBindings.TryGetValue(Math.Clamp(keyCount, 1, 18), out var bindings)
            ? bindings.ToArray()
            : CreateDefaultBindings(keyCount);

    public WindowsPlaytestSettings With(
        bool? autoPlay = null,
        WindowsPlaytestNoteStyle? noteStyle = null,
        double? noteWidth = null,
        double? noteHeight = null,
        double? playfieldWidth = null,
        double? scrollSpeed = null,
        double? judgementLinePosition = null,
        double? comboYPosition = null,
        double? accuracyYPosition = null,
        double? bestWindowMilliseconds = null,
        double? greatWindowMilliseconds = null,
        double? goodWindowMilliseconds = null,
        double? missWindowMilliseconds = null,
        double? inputOffsetMilliseconds = null,
        int? bindingKeyCount = null,
        string[]? bindings = null)
    {
        var next = keyBindings.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        if (bindingKeyCount.HasValue && bindings != null)
            next[Math.Clamp(bindingKeyCount.Value, 1, 18)] = bindings.ToArray();
        return new WindowsPlaytestSettings(
            autoPlay ?? AutoPlay,
            noteStyle ?? NoteStyle,
            noteWidth ?? NoteWidth,
            noteHeight ?? NoteHeight,
            playfieldWidth ?? PlayfieldWidth,
            scrollSpeed ?? ScrollSpeed,
            judgementLinePosition ?? JudgementLinePosition,
            next,
            comboYPosition ?? ComboYPosition,
            accuracyYPosition ?? AccuracyYPosition,
            bestWindowMilliseconds ?? BestWindowMilliseconds,
            greatWindowMilliseconds ?? GreatWindowMilliseconds,
            goodWindowMilliseconds ?? GoodWindowMilliseconds,
            missWindowMilliseconds ?? MissWindowMilliseconds,
            inputOffsetMilliseconds ?? InputOffsetMilliseconds);
    }

    public static string[] CreateDefaultBindings(int keyCount)
    {
        keyCount = Math.Clamp(keyCount, 1, 18);
        string[]? common = keyCount switch
        {
            1 => ["Space"],
            2 => ["F", "J"],
            3 => ["D", "F", "J"],
            4 => ["D", "F", "J", "K"],
            5 => ["D", "F", "Space", "J", "K"],
            6 => ["S", "D", "F", "J", "K", "L"],
            7 => ["S", "D", "F", "Space", "J", "K", "L"],
            8 => ["A", "S", "D", "F", "J", "K", "L", "OemSemicolon"],
            9 => ["A", "S", "D", "F", "Space", "J", "K", "L", "OemSemicolon"],
            10 => ["A", "S", "D", "F", "G", "H", "J", "K", "L", "OemSemicolon"],
            _ => null
        };
        if (common != null) return common;
        string[] centered = ["A", "S", "D", "F", "G", "Space", "H", "J", "K", "L", "OemSemicolon"];
        string[] extended =
        [
            "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "OemOpenBrackets",
            "OemCloseBrackets", "A", "S", "D", "F", "J", "K"
        ];
        return keyCount <= centered.Length ? centered.ToArray() : extended.Take(keyCount).ToArray();
    }

    public static string[] AssignBinding(
        IReadOnlyList<string> current,
        int lane,
        string replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        if (lane < 0 || lane >= current.Count) throw new ArgumentOutOfRangeException(nameof(lane));
        var result = current.Select(value => value?.Trim() ?? string.Empty).ToArray();
        var normalized = replacement.Trim();
        var duplicate = Array.FindIndex(
            result,
            value => value.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (duplicate >= 0 && duplicate != lane) result[duplicate] = result[lane];
        result[lane] = normalized;
        return result;
    }

    private static IReadOnlyDictionary<int, string[]> NormalizeBindings(
        IReadOnlyDictionary<int, string[]>? source)
    {
        var result = new Dictionary<int, string[]>();
        for (var keyCount = 1; keyCount <= 18; keyCount++)
        {
            var candidate = source != null && source.TryGetValue(keyCount, out var configured)
                ? configured
                : null;
            result[keyCount] = candidate is { Length: > 0 } && candidate.Length == keyCount &&
                               candidate.All(value => !string.IsNullOrWhiteSpace(value))
                ? candidate.Select(value => value.Trim()).ToArray()
                : CreateDefaultBindings(keyCount);
        }
        return new ReadOnlyDictionary<int, string[]>(result);
    }
}

public static class WindowsPlaytestSettingsStore
{
    private const int SchemaVersion = 1;
    private const string FileName = "playtest-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsPath => Path.Combine(AvaloniaAppPaths.LocalDataRoot, FileName);

    public static WindowsPlaytestSettings Load()
    {
        string? path = null;
        try
        {
            path = SettingsPath;
            if (!WindowsSettingsFileGuard.TryReadExisting(path, out var bytes))
                return WindowsPlaytestSettings.Default;
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(
                bytes,
                JsonOptions);
            if (persisted?.SchemaVersion != SchemaVersion) return WindowsPlaytestSettings.Default;
            var style = Enum.TryParse<WindowsPlaytestNoteStyle>(persisted.NoteStyle, true, out var parsed)
                ? parsed
                : WindowsPlaytestNoteStyle.Brick;
            return new WindowsPlaytestSettings(
                persisted.AutoPlay,
                style,
                persisted.NoteWidth,
                persisted.NoteHeight,
                persisted.PlayfieldWidth,
                persisted.ScrollSpeed,
                persisted.JudgementLinePosition,
                persisted.KeyBindings,
                persisted.ComboYPosition,
                persisted.AccuracyYPosition,
                persisted.BestWindowMilliseconds,
                persisted.GreatWindowMilliseconds,
                persisted.GoodWindowMilliseconds,
                persisted.MissWindowMilliseconds,
                persisted.InputOffsetMilliseconds);
        }
        catch (Exception exception)
        {
            if (path != null) WindowsSettingsFileGuard.ReportLoadFailure(path, exception);
            return WindowsPlaytestSettings.Default;
        }
    }

    /// <summary>
    /// Persists the settings. Returns false instead of throwing when the file
    /// cannot be written: this runs from a DispatcherTimer tick and from window
    /// Closing, where an escaping I/O exception terminates the application.
    /// </summary>
    public static bool Save(WindowsPlaytestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string root;
        string destination;
        try
        {
            root = AvaloniaAppPaths.LocalDataRoot;
            destination = Path.Combine(root, FileName);
            // An existing file that could not be read this session is kept intact.
            if (!WindowsSettingsFileGuard.CanWrite(destination)) return false;
            Directory.CreateDirectory(root);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            WindowsSettingsFileGuard.ReportSaveFailure(FileName, exception);
            return false;
        }
        var temporary = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedSettings
                {
                    SchemaVersion = SchemaVersion,
                    AutoPlay = settings.AutoPlay,
                    NoteStyle = settings.NoteStyle.ToString(),
                    NoteWidth = settings.NoteWidth,
                    NoteHeight = settings.NoteHeight,
                    PlayfieldWidth = settings.PlayfieldWidth,
                    ScrollSpeed = settings.ScrollSpeed,
                    JudgementLinePosition = settings.JudgementLinePosition,
                    ComboYPosition = settings.ComboYPosition,
                    AccuracyYPosition = settings.AccuracyYPosition,
                    BestWindowMilliseconds = settings.BestWindowMilliseconds,
                    GreatWindowMilliseconds = settings.GreatWindowMilliseconds,
                    GoodWindowMilliseconds = settings.GoodWindowMilliseconds,
                    MissWindowMilliseconds = settings.MissWindowMilliseconds,
                    InputOffsetMilliseconds = settings.InputOffsetMilliseconds,
                    KeyBindings = settings.KeyBindings.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.ToArray())
                },
                JsonOptions);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(destination))
                File.Replace(temporary, destination, null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, destination);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WindowsSettingsFileGuard.ReportSaveFailure(destination, exception);
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private sealed class PersistedSettings
    {
        public int SchemaVersion { get; set; }
        public bool AutoPlay { get; set; } = true;
        public string NoteStyle { get; set; } = nameof(WindowsPlaytestNoteStyle.Brick);
        public double NoteWidth { get; set; } = .82;
        public double NoteHeight { get; set; } = 28;
        public double PlayfieldWidth { get; set; } = 520;
        public double ScrollSpeed { get; set; } = 760;
        public double JudgementLinePosition { get; set; } = .84;
        public double ComboYPosition { get; set; } = .12;
        public double AccuracyYPosition { get; set; } = .19;
        public double BestWindowMilliseconds { get; set; } = 45;
        public double GreatWindowMilliseconds { get; set; } = 85;
        public double GoodWindowMilliseconds { get; set; } = 130;
        public double MissWindowMilliseconds { get; set; } = 170;
        public double InputOffsetMilliseconds { get; set; }
        public Dictionary<int, string[]> KeyBindings { get; set; } = [];
    }
}

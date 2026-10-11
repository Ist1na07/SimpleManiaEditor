using System.Text.Json;
using Avalonia.Input;
#if !LUMACHART_IOS
using System.Runtime.InteropServices;
using Avalonia.Win32.Input;
#endif

namespace LumaChart.Avalonia.Infrastructure;

public sealed record WindowsEditorShortcutDescriptor(
    string Id,
    string Label,
    bool SuppressWhenEditing);

public sealed record WindowsEditorShortcutAssignmentResult(
    bool Applied,
    string? ConflictAction);

public enum WindowsEditorShortcutCaptureKind
{
    IgnoreModifier,
    Cancel,
    Clear,
    Assign
}

public readonly record struct WindowsEditorShortcutCaptureIntent(
    WindowsEditorShortcutCaptureKind Kind,
    string Gesture);

public enum WindowsEditorShortcutRoute
{
    New,
    Open,
    Save,
    SaveAs,
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    SelectAll,
    ToolSelect,
    ToolTap,
    ToolHold,
    ToolErase,
    ToggleSymmetry,
    PlayPause,
    Delete,
    StepForward,
    StepBack,
    GoHome,
    TogglePro,
    ToggleFullscreen,
    Group,
    SnapIncrease,
    SnapDecrease,
    SnapSetFixed
}

public readonly record struct WindowsEditorShortcutCommand(
    WindowsEditorShortcutRoute Route,
    int? Group = null);

/// <summary>
/// WPF 1.1.0 shortcut catalog and JSON contract. Keeping the catalog outside
/// the window prevents keyboard routing and the settings UI from drifting.
/// </summary>
public sealed class WindowsEditorShortcutSettings
{
    public static IReadOnlyList<WindowsEditorShortcutDescriptor> Catalog { get; } =
    [
        new("New", "新建谱面", false),
        new("Open", "打开谱面", false),
        new("Save", "保存", false),
        new("SaveAs", "另存为", false),
        new("Undo", "撤销", false),
        new("Redo", "重做", false),
        new("Cut", "剪切 Note", true),
        new("Copy", "复制 Note", true),
        new("Paste", "粘贴 Note", true),
        new("SelectAll", "全选 Note", true),
        new("ToolSelect", "选择工具", true),
        new("ToolTap", "单键工具", true),
        new("ToolHold", "长键工具", true),
        new("ToolErase", "擦除工具", true),
        new("ToggleSymmetry", "划定 / 关闭中心对称", true),
        new("PlayPause", "播放 / 暂停", true),
        new("Delete", "删除 Note", true),
        new("StepForward", "向前一格", true),
        new("StepBack", "向后一格", true),
        new("GoHome", "回到开头", true),
        new("TogglePro", "切换 ProEditor", true),
        new("ToggleFullscreen", "全屏 / 退出全屏", true),
        new("Group0", "切换分组 0", true),
        new("Group1", "切换分组 1", true),
        new("Group2", "切换分组 2", true),
        new("Group3", "切换分组 3", true),
        new("Group4", "切换分组 4", true),
        new("Group5", "切换分组 5", true),
        new("Group6", "切换分组 6", true),
        new("Group7", "切换分组 7", true),
        new("Group8", "切换分组 8", true),
        new("Group9", "切换分组 9", true),
        new("SnapIncrease", "加分度", true),
        new("SnapDecrease", "减分度", true),
        new("SnapSetFixed", "切换到固定分度", true)
    ];

    public WindowsEditorShortcutSettings()
        : this(CreateDefaults())
    {
    }

    public WindowsEditorShortcutSettings(IDictionary<string, string>? bindings)
    {
        Bindings = new Dictionary<string, string>(
            bindings ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        Normalize();
    }

    public Dictionary<string, string> Bindings { get; set; }
    public EditorInteractionProfile Profile { get; set; } = EditorInteractionProfile.OsuMania;
    public bool ReverseTimeWheel { get; set; } = true;

    public static Dictionary<string, string> CreateLegacyDefaults() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["New"] = "Ctrl+N",
            ["Open"] = "Ctrl+O",
            ["Save"] = "Ctrl+S",
            ["SaveAs"] = "Ctrl+Shift+S",
            ["Undo"] = "Ctrl+Z",
            ["Redo"] = "Ctrl+Y",
            ["Cut"] = "Ctrl+X",
            ["Copy"] = "Ctrl+C",
            ["Paste"] = "Ctrl+V",
            ["SelectAll"] = "Ctrl+A",
            ["ToolSelect"] = "Q",
            ["ToolTap"] = "W",
            ["ToolHold"] = "E",
            ["ToolErase"] = "R",
            ["ToggleSymmetry"] = "F6",
            ["PlayPause"] = "Space",
            ["Delete"] = "Delete",
            ["StepForward"] = "Up",
            ["StepBack"] = "Down",
            ["GoHome"] = "Home",
            ["TogglePro"] = "P",
            ["ToggleFullscreen"] = "F11",
            ["Group0"] = "D0",
            ["Group1"] = "D1",
            ["Group2"] = "D2",
            ["Group3"] = "D3",
            ["Group4"] = "D4",
            ["Group5"] = "D5",
            ["Group6"] = "D6",
            ["Group7"] = "D7",
            ["Group8"] = "D8",
            ["Group9"] = "D9",
            ["SnapIncrease"] = "=",
            ["SnapDecrease"] = "-",
            ["SnapSetFixed"] = "Ctrl+="
        };

    public static Dictionary<string, string> CreateDefaults(EditorInteractionProfile profile = EditorInteractionProfile.OsuMania)
    {
        var bindings = CreateLegacyDefaults();
        if (profile == EditorInteractionProfile.LumaLegacy) return bindings;
        bindings["ToolSelect"] = "D1";
        bindings["ToolTap"] = "D2";
        bindings["ToolHold"] = "D3";
        bindings["ToolErase"] = "D4";
        for (var i = 0; i <= 9; i++) bindings[$"Group{i}"] = $"Alt+D{i}";
        return bindings;
    }

    public void SelectProfile(EditorInteractionProfile profile)
    {
        Profile = profile;
        ReverseTimeWheel = profile == EditorInteractionProfile.OsuMania;
        Bindings = CreateDefaults(profile);
    }

    public void Normalize()
    {
        Bindings = new Dictionary<string, string>(
            Bindings ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in CreateDefaults(Profile))
            if (!Bindings.TryGetValue(pair.Key, out var value) || value == null)
                Bindings[pair.Key] = pair.Key == "ToggleSymmetry" && Bindings.Any(existing =>
                    existing.Key != pair.Key && string.Equals(existing.Value, pair.Value, StringComparison.OrdinalIgnoreCase))
                    ? string.Empty : pair.Value;
    }

    public void Reset()
    {
        Bindings = CreateDefaults(Profile);
    }

    public WindowsEditorShortcutAssignmentResult Assign(
        string action,
        string gesture,
        bool replaceConflict)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        gesture ??= string.Empty;
        if (!Catalog.Any(item => item.Id.Equals(action, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown shortcut action.");

        var conflict = string.IsNullOrWhiteSpace(gesture)
            ? null
            : Bindings.FirstOrDefault(pair =>
                !pair.Key.Equals(action, StringComparison.OrdinalIgnoreCase) &&
                pair.Value.Equals(gesture, StringComparison.OrdinalIgnoreCase)).Key;
        if (!string.IsNullOrWhiteSpace(conflict) && !replaceConflict)
            return new WindowsEditorShortcutAssignmentResult(false, conflict);
        if (!string.IsNullOrWhiteSpace(conflict)) Bindings[conflict] = string.Empty;
        Bindings[action] = gesture;
        return new WindowsEditorShortcutAssignmentResult(true, conflict);
    }
}

public static class WindowsEditorShortcutGesture
{
    public static Key ResolveReportedKey(
        Key reportedKey,
        PhysicalKey physicalKey,
        string? keySymbol = null,
        Key? pressedKeyFallback = null)
    {
        if (reportedKey is not (Key.System or Key.ImeProcessed or Key.DeadCharProcessed))
            return reportedKey;
        if (physicalKey != PhysicalKey.None) return physicalKey.ToQwertyKey();
        if (!string.IsNullOrEmpty(keySymbol) && keySymbol.Length == 1)
        {
            var symbol = keySymbol[0];
            if (symbol is >= 'a' and <= 'z') symbol = char.ToUpperInvariant(symbol);
            if (symbol is >= 'A' and <= 'Z' &&
                Enum.TryParse<Key>(symbol.ToString(), out var letter))
                return letter;
            if (symbol is >= '0' and <= '9' &&
                Enum.TryParse<Key>($"D{symbol}", out var digit))
                return digit;
        }
        return pressedKeyFallback is { } fallback &&
               fallback is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
            ? fallback
            : reportedKey;
    }

    public static string Format(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key == Key.Space ? "Space" : key.ToString());
        return string.Join('+', parts);
    }

    public static bool IsModifierOnly(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;

    public static WindowsEditorShortcutCaptureIntent InterpretCapture(
        Key key,
        KeyModifiers modifiers)
    {
        if (key == Key.Escape)
            return new WindowsEditorShortcutCaptureIntent(
                WindowsEditorShortcutCaptureKind.Cancel,
                string.Empty);
        if (IsModifierOnly(key))
            return new WindowsEditorShortcutCaptureIntent(
                WindowsEditorShortcutCaptureKind.IgnoreModifier,
                string.Empty);
        if (key == Key.Back && modifiers == KeyModifiers.None)
            return new WindowsEditorShortcutCaptureIntent(
                WindowsEditorShortcutCaptureKind.Clear,
                string.Empty);
        return new WindowsEditorShortcutCaptureIntent(
            WindowsEditorShortcutCaptureKind.Assign,
            Format(key, modifiers));
    }

    public static WindowsEditorShortcutDescriptor? Resolve(
        WindowsEditorShortcutSettings settings,
        Key key,
        KeyModifiers modifiers,
        bool textEditing)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var gesture = Format(key, modifiers);
        return WindowsEditorShortcutSettings.Catalog.FirstOrDefault(descriptor =>
            (!textEditing || !descriptor.SuppressWhenEditing) &&
            settings.Bindings.GetValueOrDefault(descriptor.Id)?
                .Equals(gesture, StringComparison.OrdinalIgnoreCase) == true);
    }
}

public static class WindowsShortcutPhysicalKeyState
{
#if !LUMACHART_IOS
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
#endif

    public static Key? TryResolvePressedKey()
    {
#if LUMACHART_IOS
        // iOS omits Avalonia.Win32, so KeyInterop is not even available to
        // compile against. Callers already treat null as "use the key the
        // toolkit reported", which is the correct behaviour off Windows.
        return null;
#else
        // GetAsyncKeyState is a Win32 entry point. Reaching it anywhere else
        // throws while merely resolving a keystroke, so platforms without it
        // fall back to whatever key the toolkit already reported.
        if (!OperatingSystem.IsWindows()) return null;

        // Mouse buttons, modifiers and IME process keys must never become an
        // editor gesture. The remaining Win32 virtual-key range is converted
        // by Avalonia's own Windows mapping so custom OEM/numpad keys work too.
        for (var virtualKey = 0x08; virtualKey <= 0xFE; virtualKey++)
        {
            if (virtualKey is 0x10 or 0x11 or 0x12 or 0x14 or
                0x15 or 0x17 or 0x19 or 0x1C or 0x1D or
                0x5B or 0x5C or 0xE5)
                continue;
            if ((GetAsyncKeyState(virtualKey) & 0x8000) == 0) continue;
            var key = KeyInterop.KeyFromVirtualKey(virtualKey, 0);
            if (key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed))
                return key;
        }
        return null;
#endif
    }
}

/// <summary>
/// Pure WPF-compatible action routing. MainWindow executes these routes while
/// Specs can prove that every persisted/catalog action remains reachable.
/// </summary>
public static class WindowsEditorShortcutRouting
{
    public static WindowsEditorShortcutCommand Resolve(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (action.StartsWith("Group", StringComparison.Ordinal) &&
            int.TryParse(action.AsSpan(5), out var group) &&
            group is >= 0 and <= 9)
            return new WindowsEditorShortcutCommand(
                WindowsEditorShortcutRoute.Group,
                group);

        return action switch
        {
            "New" => new(WindowsEditorShortcutRoute.New),
            "Open" => new(WindowsEditorShortcutRoute.Open),
            "Save" => new(WindowsEditorShortcutRoute.Save),
            "SaveAs" => new(WindowsEditorShortcutRoute.SaveAs),
            "Undo" => new(WindowsEditorShortcutRoute.Undo),
            "Redo" => new(WindowsEditorShortcutRoute.Redo),
            "Cut" => new(WindowsEditorShortcutRoute.Cut),
            "Copy" => new(WindowsEditorShortcutRoute.Copy),
            "Paste" => new(WindowsEditorShortcutRoute.Paste),
            "SelectAll" => new(WindowsEditorShortcutRoute.SelectAll),
            "ToolSelect" => new(WindowsEditorShortcutRoute.ToolSelect),
            "ToolTap" => new(WindowsEditorShortcutRoute.ToolTap),
            "ToolHold" => new(WindowsEditorShortcutRoute.ToolHold),
            "ToolErase" => new(WindowsEditorShortcutRoute.ToolErase),
            "ToggleSymmetry" => new(WindowsEditorShortcutRoute.ToggleSymmetry),
            "PlayPause" => new(WindowsEditorShortcutRoute.PlayPause),
            "Delete" => new(WindowsEditorShortcutRoute.Delete),
            "StepForward" => new(WindowsEditorShortcutRoute.StepForward),
            "StepBack" => new(WindowsEditorShortcutRoute.StepBack),
            "GoHome" => new(WindowsEditorShortcutRoute.GoHome),
            "TogglePro" => new(WindowsEditorShortcutRoute.TogglePro),
            "ToggleFullscreen" => new(WindowsEditorShortcutRoute.ToggleFullscreen),
            "SnapIncrease" => new(WindowsEditorShortcutRoute.SnapIncrease),
            "SnapDecrease" => new(WindowsEditorShortcutRoute.SnapDecrease),
            "SnapSetFixed" => new(WindowsEditorShortcutRoute.SnapSetFixed),
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "Unknown shortcut action.")
        };
    }
}

/// <summary>
/// Atomic Avalonia-side persistence using the same public Bindings shape and
/// gesture strings as WPF 1.1.0, without modifying WPF's shortcuts.json.
/// </summary>
public static class WindowsEditorShortcutSettingsStore
{
    private const string FileName = "shortcuts.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsPath => Path.Combine(AvaloniaAppPaths.LocalDataRoot, FileName);

    public static WindowsEditorShortcutSettings Load()
    {
        string? path = null;
        try
        {
            path = SettingsPath;
            if (!WindowsSettingsFileGuard.TryReadExisting(path, out var bytes))
                return new WindowsEditorShortcutSettings();
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(
                bytes,
                JsonOptions);
            var settings = new WindowsEditorShortcutSettings(persisted?.Bindings);
            if (Enum.TryParse<EditorInteractionProfile>(persisted?.Profile, out var profile) && Enum.IsDefined(profile))
                settings.Profile = profile;
            settings.ReverseTimeWheel = persisted?.ReverseTimeWheel ?? settings.Profile == EditorInteractionProfile.OsuMania;
            return settings;
        }
        catch (Exception exception)
        {
            if (path != null) WindowsSettingsFileGuard.ReportLoadFailure(path, exception);
            return new WindowsEditorShortcutSettings();
        }
    }

    public static void Save(WindowsEditorShortcutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        var root = AvaloniaAppPaths.LocalDataRoot;
        var destination = Path.Combine(root, FileName);
        // An existing file that could not be read this session is kept intact.
        if (!WindowsSettingsFileGuard.CanWrite(destination)) return;
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedSettings { Bindings = settings.Bindings, Profile = settings.Profile.ToString(), ReverseTimeWheel = settings.ReverseTimeWheel },
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
                File.Replace(
                    temporary,
                    destination,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
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
        public Dictionary<string, string>? Bindings { get; set; }
        public string? Profile { get; set; }
        public bool? ReverseTimeWheel { get; set; }
    }
}

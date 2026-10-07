using System.Globalization;
using System.Text.Json;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// The user's display-language choice. The setting is shared by the Windows,
/// Android, and iOS Avalonia hosts, and is intentionally separate from editor
/// view settings so it never affects chart data or edit history.
/// </summary>
public sealed class AvaloniaLanguageSettings
{
    public AvaloniaLanguageSettings(
        AvaloniaLanguagePreference preference = AvaloniaLanguagePreference.FollowSystem)
    {
        Preference = NormalizePreference(preference);
    }

    public static AvaloniaLanguageSettings Default { get; } = new();

    public AvaloniaLanguagePreference Preference { get; }

    public AvaloniaLanguageSettings WithPreference(AvaloniaLanguagePreference preference) =>
        new(preference);

    public static AvaloniaDisplayLanguage ResolveEffectiveLanguage(
        AvaloniaLanguagePreference preference,
        CultureInfo? systemCulture = null)
    {
        preference = NormalizePreference(preference);
        if (preference != AvaloniaLanguagePreference.FollowSystem)
            return preference switch
            {
                AvaloniaLanguagePreference.SimplifiedChinese =>
                    AvaloniaDisplayLanguage.SimplifiedChinese,
                AvaloniaLanguagePreference.TraditionalChinese =>
                    AvaloniaDisplayLanguage.TraditionalChinese,
                AvaloniaLanguagePreference.Japanese => AvaloniaDisplayLanguage.Japanese,
                AvaloniaLanguagePreference.Korean => AvaloniaDisplayLanguage.Korean,
                AvaloniaLanguagePreference.Spanish => AvaloniaDisplayLanguage.Spanish,
                AvaloniaLanguagePreference.French => AvaloniaDisplayLanguage.French,
                AvaloniaLanguagePreference.German => AvaloniaDisplayLanguage.German,
                AvaloniaLanguagePreference.PortugueseBrazil =>
                    AvaloniaDisplayLanguage.PortugueseBrazil,
                AvaloniaLanguagePreference.Russian => AvaloniaDisplayLanguage.Russian,
                AvaloniaLanguagePreference.Italian => AvaloniaDisplayLanguage.Italian,
                AvaloniaLanguagePreference.Dutch => AvaloniaDisplayLanguage.Dutch,
                AvaloniaLanguagePreference.Polish => AvaloniaDisplayLanguage.Polish,
                AvaloniaLanguagePreference.Turkish => AvaloniaDisplayLanguage.Turkish,
                AvaloniaLanguagePreference.Vietnamese => AvaloniaDisplayLanguage.Vietnamese,
                AvaloniaLanguagePreference.Arabic => AvaloniaDisplayLanguage.Arabic,
                AvaloniaLanguagePreference.Hindi => AvaloniaDisplayLanguage.Hindi,
                AvaloniaLanguagePreference.Thai => AvaloniaDisplayLanguage.Thai,
                AvaloniaLanguagePreference.Indonesian => AvaloniaDisplayLanguage.Indonesian,
                _ => AvaloniaDisplayLanguage.English
            };

        var culture = systemCulture ?? CultureInfo.CurrentUICulture;
        var cultureName = culture.Name.Replace('_', '-');
        if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return UsesTraditionalChinese(cultureName)
                ? AvaloniaDisplayLanguage.TraditionalChinese
                : AvaloniaDisplayLanguage.SimplifiedChinese;
        }

        return culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "ja" => AvaloniaDisplayLanguage.Japanese,
            "ko" => AvaloniaDisplayLanguage.Korean,
            "es" => AvaloniaDisplayLanguage.Spanish,
            "fr" => AvaloniaDisplayLanguage.French,
            "de" => AvaloniaDisplayLanguage.German,
            "pt" => AvaloniaDisplayLanguage.PortugueseBrazil,
            "ru" => AvaloniaDisplayLanguage.Russian,
            "it" => AvaloniaDisplayLanguage.Italian,
            "nl" => AvaloniaDisplayLanguage.Dutch,
            "pl" => AvaloniaDisplayLanguage.Polish,
            "tr" => AvaloniaDisplayLanguage.Turkish,
            "vi" => AvaloniaDisplayLanguage.Vietnamese,
            "ar" => AvaloniaDisplayLanguage.Arabic,
            "hi" => AvaloniaDisplayLanguage.Hindi,
            "th" => AvaloniaDisplayLanguage.Thai,
            "id" => AvaloniaDisplayLanguage.Indonesian,
            _ => AvaloniaDisplayLanguage.English
        };
    }

    private static bool UsesTraditionalChinese(string cultureName) =>
        cultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
        cultureName.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase) ||
        cultureName.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
        cultureName.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
        cultureName.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);

    private static AvaloniaLanguagePreference NormalizePreference(
        AvaloniaLanguagePreference preference) =>
        Enum.IsDefined(preference)
            ? preference
            : AvaloniaLanguagePreference.FollowSystem;
}



/// <summary>
/// Atomically persists the cross-platform Avalonia language preference in the
/// app-private data root supplied by each host.
/// </summary>
public static class AvaloniaLanguageSettingsStore
{
    public const int CurrentSchemaVersion = 1;
    private const string FileName = "language-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string SettingsPath => Path.Combine(
        AvaloniaAppPaths.LocalDataRoot,
        FileName);

    /// <summary>
    /// Reads the saved preference. A missing file means Follow system. A file
    /// that exists but cannot be used also yields the default for this session,
    /// and is reported to <see cref="WindowsSettingsFileGuard"/>: unreadable
    /// content is moved aside and a file that could not be read at all is
    /// protected from <see cref="Save"/>, so the default is never written over
    /// the user's real choice.
    /// </summary>
    public static AvaloniaLanguageSettings Load()
    {
        string? path = null;
        try
        {
            path = SettingsPath;
            if (!WindowsSettingsFileGuard.TryReadExisting(path, out var bytes))
                return AvaloniaLanguageSettings.Default;
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(
                bytes,
                JsonOptions);
            return persisted?.SchemaVersion == CurrentSchemaVersion
                ? new AvaloniaLanguageSettings(persisted.Preference)
                : AvaloniaLanguageSettings.Default;
        }
        catch (Exception exception)
        {
            if (path != null) WindowsSettingsFileGuard.ReportLoadFailure(path, exception);
            return AvaloniaLanguageSettings.Default;
        }
    }

    /// <summary>
    /// Persists the preference atomically. Returns false, writing nothing, while
    /// an existing settings file could not be read this session (see
    /// <see cref="Load"/>); the in-memory preference still applies. A failed
    /// write still throws, which the caller reports as "switched, but not saved".
    /// </summary>
    public static bool Save(AvaloniaLanguageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = AvaloniaAppPaths.LocalDataRoot;
        var destination = Path.Combine(root, FileName);
        if (!WindowsSettingsFileGuard.CanWrite(destination)) return false;
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
                    Preference = settings.Preference
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
                File.Replace(
                    temporary,
                    destination,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            else
                File.Move(temporary, destination);
            return true;
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
        public int SchemaVersion { get; set; }
        public AvaloniaLanguagePreference Preference { get; set; } =
            AvaloniaLanguagePreference.FollowSystem;
    }
}

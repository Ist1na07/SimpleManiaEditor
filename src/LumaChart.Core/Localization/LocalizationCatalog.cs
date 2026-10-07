using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace LumaChart.Core.Localization;

/// <summary>
/// The display languages the client offers.
/// </summary>
/// <remarks>
/// This lives in the lowest project because the text that needs translating is
/// not confined to the UI project. The values are fixed: they are persisted in
/// the user's settings file.
/// </remarks>
public enum LumaChartDisplayLanguage
{
    SimplifiedChinese,
    TraditionalChinese,
    English,
    Japanese,
    Korean,
    Spanish,
    French,
    German,
    PortugueseBrazil,
    Russian,
    Italian,
    Dutch,
    Polish,
    Turkish,
    Vietnamese,
    Arabic,
    Hindi,
    Thai,
    Indonesian
}

/// <summary>
/// What the user chose, which may be "whatever the system uses".
/// </summary>
/// <remarks>
/// The numbers are persisted in the settings file and must not be reordered.
/// </remarks>
public enum LumaChartLanguagePreference
{
    FollowSystem = 0,
    SimplifiedChinese = 1,
    English = 2,
    TraditionalChinese = 3,
    Japanese = 4,
    Korean = 5,
    Spanish = 6,
    French = 7,
    German = 8,
    PortugueseBrazil = 9,
    Russian = 10,
    Italian = 11,
    Dutch = 12,
    Polish = 13,
    Turkish = 14,
    Vietnamese = 15,
    Arabic = 16,
    Hindi = 17,
    Thai = 18,
    Indonesian = 19
}

/// <summary>
/// The display-language catalogs, loaded from the JSON slices embedded in this
/// assembly under <c>Resources/i18n</c>.
///
/// Every key is the Simplified Chinese source text that appears in the code, so
/// a label is its own lookup key and no separate resource identifier has to be
/// threaded through. Lookup is whole-string only: a value is either translated
/// as a complete label or not at all. An earlier revision also replaced
/// recognised substrings inside an unknown sentence, which produced labels
/// mixing three languages in one line ("已Import：{file}；点击\"Apply chart
/// information与 Offset\"后Write Chart。"). Falling back to a whole English or
/// whole Chinese sentence looks worse in one place but always reads.
/// </summary>
public static class LocalizationCatalog
{
    private const string ResourcePrefix = "LumaChart.Core.Resources.i18n.";
    private const int MaxRememberedFormats = 512;

    private static readonly Dictionary<LumaChartDisplayLanguage, string> ResourceCodes =
        new()
        {
            [LumaChartDisplayLanguage.TraditionalChinese] = "zh-Hant",
            [LumaChartDisplayLanguage.English] = "en",
            [LumaChartDisplayLanguage.Japanese] = "ja",
            [LumaChartDisplayLanguage.Korean] = "ko",
            [LumaChartDisplayLanguage.Spanish] = "es",
            [LumaChartDisplayLanguage.French] = "fr",
            [LumaChartDisplayLanguage.German] = "de",
            [LumaChartDisplayLanguage.PortugueseBrazil] = "pt-BR",
            [LumaChartDisplayLanguage.Russian] = "ru",
            [LumaChartDisplayLanguage.Italian] = "it",
            [LumaChartDisplayLanguage.Dutch] = "nl",
            [LumaChartDisplayLanguage.Polish] = "pl",
            [LumaChartDisplayLanguage.Turkish] = "tr",
            [LumaChartDisplayLanguage.Vietnamese] = "vi",
            [LumaChartDisplayLanguage.Arabic] = "ar",
            [LumaChartDisplayLanguage.Hindi] = "hi",
            [LumaChartDisplayLanguage.Thai] = "th",
            [LumaChartDisplayLanguage.Indonesian] = "id"
        };

    private static readonly Dictionary<
        LumaChartDisplayLanguage,
        IReadOnlyDictionary<string, string>> Catalogs = [];

    // A null value marks a translation that several sources share in that
    // language, which therefore cannot be mapped back to any one of them.
    private static readonly Dictionary<
        LumaChartDisplayLanguage,
        IReadOnlyDictionary<string, string?>> ReverseCatalogs = [];

    private static readonly Dictionary<string, FormattedOrigin> FormattedOrigins =
        new(StringComparer.Ordinal);

    private static readonly Queue<string> FormattedOrder = new();
    private static readonly object Gate = new();
    private static LumaChartDisplayLanguage current = LumaChartDisplayLanguage.SimplifiedChinese;

    /// <summary>
    /// The language messages should be built in right now. The UI layer owns the
    /// user's preference and assigns this; code below the UI only reads it, so a
    /// message composed in a service comes out in the same language as the
    /// window that will show it.
    /// </summary>
    public static LumaChartDisplayLanguage CurrentLanguage
    {
        get { lock (Gate) return current; }
        set { lock (Gate) current = value; }
    }

    /// <summary>The languages that ship a catalog file.</summary>
    public static IReadOnlyCollection<LumaChartDisplayLanguage> TranslatedLanguages =>
        ResourceCodes.Keys;

    /// <summary>
    /// The loaded entries for one language, for coverage checks and tooling.
    /// Simplified Chinese returns an empty map because it is the source language.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Snapshot(
        LumaChartDisplayLanguage language) =>
        language == LumaChartDisplayLanguage.SimplifiedChinese
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : Catalog(language);

    public static string Translate(string sourceText, LumaChartDisplayLanguage language)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (language == LumaChartDisplayLanguage.SimplifiedChinese) return sourceText;
        if (Catalog(language).TryGetValue(sourceText, out var translated))
            return translated;
        if (language != LumaChartDisplayLanguage.English &&
            Catalog(LumaChartDisplayLanguage.English).TryGetValue(sourceText, out translated))
            return translated;
        return sourceText;
    }

    /// <summary>
    /// Translates one label into the language in effect right now. Use it for
    /// text chosen in code rather than read off a control — most often a word
    /// picked by a condition and then fed into <see cref="Format"/>.
    /// </summary>
    public static string Text(string sourceText) => Translate(sourceText, CurrentLanguage);

    /// <summary>
    /// Builds a runtime message from a translated template.
    ///
    /// A composed label cannot be a catalog key, because it embeds values that
    /// exist only at run time, and it cannot be assembled by translating the
    /// pieces either — word order differs per language. So the template is the
    /// key: <c>"正在创建“{0}”"</c> is translated whole, then formatted.
    ///
    /// The result is recorded against its template so the UI can rebuild it in
    /// a new language later (see <see cref="TryReformat"/>); a status line
    /// written before the user switches language would otherwise be stranded in
    /// the language it was first rendered in.
    /// </summary>
    public static string Format(string template, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(arguments);
        var formatted = FormatWith(template, CurrentLanguage, arguments);
        Remember(formatted, template, arguments);
        return formatted;
    }

    /// <summary>
    /// Re-renders a string that <see cref="Format"/> produced earlier, in the
    /// language asked for now. Returns false for any other string.
    /// </summary>
    public static bool TryReformat(
        string formattedText,
        LumaChartDisplayLanguage language,
        out string reformatted)
    {
        ArgumentNullException.ThrowIfNull(formattedText);
        FormattedOrigin? origin;
        lock (Gate)
        {
            origin = FormattedOrigins.TryGetValue(formattedText, out var found) ? found : null;
        }

        if (origin is null)
        {
            reformatted = formattedText;
            return false;
        }

        reformatted = FormatWith(origin.Template, language, origin.Arguments);
        if (!ReferenceEquals(reformatted, formattedText))
            Remember(reformatted, origin.Template, origin.Arguments);
        return true;
    }

    /// <summary>
    /// Maps a label that code wrote already translated into
    /// <paramref name="language"/> back to its Simplified Chinese source, so the
    /// label can still follow a later language switch. Returns false — keep the
    /// text as its own source — for anything it cannot map with certainty.
    ///
    /// Deliberately narrow. Many translations are shared by several sources in
    /// one language ("Open" is both 打开 and 待处理 in English), and the earlier
    /// first-match lookup picked one of them at random: a status word came back
    /// as a verb after the next switch. Such shared values are now never mapped.
    /// The search is also confined to the language the label is shown in (plus
    /// English, which <see cref="Translate"/> falls back to): the earlier version
    /// searched every catalog, which parsed all eighteen files on the UI thread
    /// the first time any label changed and let a look-alike word from another
    /// language win.
    /// </summary>
    public static bool TryResolveSourceText(
        string localizedText,
        LumaChartDisplayLanguage language,
        out string sourceText)
    {
        ArgumentNullException.ThrowIfNull(localizedText);
        sourceText = localizedText;
        if (language == LumaChartDisplayLanguage.SimplifiedChinese ||
            !ResourceCodes.ContainsKey(language))
            return false;

        // Text that is itself a source key is not a translation to undo; the
        // Chinese label code writes most of the time lands here.
        if (Catalog(language).ContainsKey(localizedText) ||
            Catalog(LumaChartDisplayLanguage.English).ContainsKey(localizedText))
            return false;

        // A value this language knows is settled here, shared or not; only text
        // the language has no entry for can be an English fallback.
        if (!Reverse(language).TryGetValue(localizedText, out var resolved) &&
            language != LumaChartDisplayLanguage.English)
            Reverse(LumaChartDisplayLanguage.English).TryGetValue(localizedText, out resolved);
        if (resolved is null) return false;

        sourceText = resolved;
        return true;
    }

    private static string FormatWith(
        string template,
        LumaChartDisplayLanguage language,
        object?[] arguments)
    {
        var translated = Translate(template, language);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, translated, arguments);
        }
        catch (FormatException)
        {
            // A translation with a damaged placeholder must not take down the
            // UI; fall back to the source template, which is known to be valid.
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template, arguments);
            }
            catch (FormatException)
            {
                return template;
            }
        }
    }

    private static void Remember(string formatted, string template, object?[] arguments)
    {
        lock (Gate)
        {
            if (FormattedOrigins.ContainsKey(formatted))
            {
                FormattedOrigins[formatted] = new FormattedOrigin(template, arguments);
                return;
            }

            // Bounded so a status line that updates every frame cannot grow this
            // without limit. Oldest-first eviction is enough: only text that is
            // currently on screen ever needs re-rendering.
            if (FormattedOrder.Count >= MaxRememberedFormats)
                FormattedOrigins.Remove(FormattedOrder.Dequeue());

            FormattedOrigins[formatted] = new FormattedOrigin(template, arguments);
            FormattedOrder.Enqueue(formatted);
        }
    }

    private static IReadOnlyDictionary<string, string> Catalog(
        LumaChartDisplayLanguage language)
    {
        lock (Gate)
        {
            if (Catalogs.TryGetValue(language, out var catalog)) return catalog;
            catalog = Load(language);
            Catalogs[language] = catalog;
            return catalog;
        }
    }

    private static IReadOnlyDictionary<string, string?> Reverse(
        LumaChartDisplayLanguage language)
    {
        lock (Gate)
        {
            if (ReverseCatalogs.TryGetValue(language, out var reverse)) return reverse;
            var built = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var pair in Catalog(language))
            {
                // Keys are unique, so a second hit is always a different source.
                if (!built.TryAdd(pair.Value, pair.Key)) built[pair.Value] = null;
            }
            ReverseCatalogs[language] = built;
            return built;
        }
    }

    private static IReadOnlyDictionary<string, string> Load(LumaChartDisplayLanguage language)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ResourceCodes.TryGetValue(language, out var code)) return entries;

        using var stream = typeof(LocalizationCatalog).Assembly
            .GetManifestResourceStream(ResourcePrefix + code + ".json");
        if (stream is null) return entries;

        using var document = JsonDocument.Parse(stream);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            var value = property.Value.GetString();
            if (string.IsNullOrEmpty(value)) continue;
            entries[property.Name] = value;
        }

        return entries;
    }

    private sealed record FormattedOrigin(string Template, object?[] Arguments);
}

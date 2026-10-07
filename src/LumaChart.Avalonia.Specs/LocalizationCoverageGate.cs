using System.Text;
using System.Text.Json;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

/// <summary>
/// Measures each display language against the labels the UI actually shows.
///
/// This exists because the previous localization checks only asserted that a
/// handful of hand-picked words translated, and that certain identifiers were
/// present in MainWindow.cs. Both passed while every language except English
/// sat below 16% of the real UI, so the suite reported "complete multilingual
/// localization" for catalogs that covered 58 of 1400+ labels.
///
/// The gate is a ratchet: <see cref="Floors"/> records where each language
/// stands today, and coverage may only rise. Raise a floor in the same change
/// that raises the translations.
/// </summary>
public static class LocalizationCoverageGate
{
    /// <summary>
    /// Minimum share of whole UI labels each language must translate, in percent.
    ///
    /// English is the fallback for every other language, so it is held at 100%:
    /// with it complete, the worst any language can look is entirely English
    /// rather than a sentence mixing three languages.
    ///
    /// The other languages sit at the level they were actually translated to,
    /// which is where the abandoned rollout left them. They are meant to be
    /// raised to 100% a language at a time; each such change raises the floor
    /// with it, so a later edit cannot quietly undo the work.
    /// </summary>
    private static readonly IReadOnlyDictionary<AvaloniaDisplayLanguage, double> Floors =
        new Dictionary<AvaloniaDisplayLanguage, double>
        {
            [AvaloniaDisplayLanguage.English] = 100.0,
            [AvaloniaDisplayLanguage.Korean] = 100.0,
            [AvaloniaDisplayLanguage.TraditionalChinese] = 100.0,
            [AvaloniaDisplayLanguage.Japanese] = 100.0,
            [AvaloniaDisplayLanguage.Spanish] = 100.0,
            [AvaloniaDisplayLanguage.French] = 100.0,
            [AvaloniaDisplayLanguage.German] = 100.0,
            [AvaloniaDisplayLanguage.PortugueseBrazil] = 100.0,
            [AvaloniaDisplayLanguage.Russian] = 100.0,
            [AvaloniaDisplayLanguage.Italian] = 100.0,
            [AvaloniaDisplayLanguage.Dutch] = 100.0,
            [AvaloniaDisplayLanguage.Polish] = 100.0,
            [AvaloniaDisplayLanguage.Turkish] = 100.0,
            [AvaloniaDisplayLanguage.Vietnamese] = 100.0,
            [AvaloniaDisplayLanguage.Arabic] = 100.0,
            [AvaloniaDisplayLanguage.Hindi] = 100.0,
            [AvaloniaDisplayLanguage.Thai] = 100.0,
            [AvaloniaDisplayLanguage.Indonesian] = 100.0
        };

    /// <summary>
    /// Projects whose Chinese literals can reach the screen. The editor UI is
    /// the obvious one, but it is not the only one: the library card subtitle
    /// ("2 首歌曲 · 81 张谱面") is built in LumaChart.Application, and the
    /// account and analysis errors a user reads come from LumaChart.Online and
    /// LumaChart.Core. Scanning only the Avalonia project reported those as
    /// fully translated while they were still Chinese on screen.
    ///
    /// LumaChart.Server is deliberately absent: it is deployed separately and
    /// is not compiled into the client.
    /// </summary>
    private static readonly string[] ScannedProjects =
    [
        "LumaChart.Avalonia",
        "LumaChart.Application",
        "LumaChart.Core",
        "LumaChart.Online"
    ];

    public static string Verify(string repositoryRoot, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(resourceRoot);

        var exempt = ReadUntranslatable(resourceRoot);
        var literals = new List<LocalizationLiteral>();
        foreach (var project in ScannedProjects)
        {
            var path = Path.Combine(repositoryRoot, project);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(
                    $"The localization gate expects to scan {project}, but it is missing " +
                    $"from {repositoryRoot}. Update ScannedProjects if it was renamed.");
            }

            literals.AddRange(LocalizationSourceScanner.Scan(path));
        }

        var whole = literals
            .Where(x => x.Kind == LocalizationLiteralKind.Whole)
            .Select(x => x.Value)
            .Where(x => !exempt.Contains(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (whole.Length < 1000)
        {
            throw new InvalidOperationException(
                $"The source scan found only {whole.Length} translatable labels. " +
                "That is far below the known size of this UI, so the scanner or " +
                "the source root is wrong and the coverage numbers cannot be trusted.");
        }

        var failures = new List<string>();
        var detail = new StringBuilder();
        foreach (var pair in Floors.OrderByDescending(x => x.Value).ThenBy(x => x.Key.ToString(), StringComparer.Ordinal))
        {
            var catalog = AvaloniaLocalizationCatalog.Snapshot(pair.Key);
            var covered = whole.Count(catalog.ContainsKey);
            var percent = covered * 100.0 / whole.Length;
            detail.Append(detail.Length == 0 ? string.Empty : "; ")
                .Append(pair.Key)
                .Append('=')
                .Append(percent.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                .Append('%');
            if (percent + 0.05 < pair.Value)
            {
                failures.Add(
                    $"{pair.Key} covers {covered}/{whole.Length} labels " +
                    $"({percent:F1}%), below its floor of {pair.Value:F1}%");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Display-language coverage regressed. " + string.Join("; ", failures) +
                ". Add the missing entries to LumaChart.Core/Resources/i18n, " +
                "or lower the floor deliberately if labels were removed.");
        }

        var missingEnglish = whole
            .Where(x => !AvaloniaLocalizationCatalog
                .Snapshot(AvaloniaDisplayLanguage.English).ContainsKey(x))
            .Take(5)
            .ToArray();
        if (missingEnglish.Length > 0)
        {
            throw new InvalidOperationException(
                "These UI labels have no English entry, so a non-Chinese user sees " +
                "Chinese: " + string.Join(" | ", missingEnglish));
        }

        VerifyPlaceholders();
        return detail.ToString();
    }

    /// <summary>
    /// Checks that every translated format template keeps the placeholders of
    /// its source. A translation that drops <c>{1}</c> silently loses a value,
    /// and one that invents <c>{3}</c> throws at the moment the message is
    /// shown; both are caught here rather than in front of a user.
    /// </summary>
    private static void VerifyPlaceholders()
    {
        var problems = new List<string>();
        foreach (var language in AvaloniaLocalizationCatalog.TranslatedLanguages)
        {
            foreach (var pair in AvaloniaLocalizationCatalog.Snapshot(language))
            {
                var source = Placeholders(pair.Key);
                if (source.Count == 0) continue;
                var translated = Placeholders(pair.Value);
                if (source.SetEquals(translated)) continue;
                problems.Add(
                    $"{language} \"{Truncate(pair.Key)}\" has {{{string.Join(",", source.Order())}}} " +
                    $"but its translation has {{{string.Join(",", translated.Order())}}}");
                if (problems.Count >= 5) break;
            }

            if (problems.Count >= 5) break;
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "A translated template does not match its source placeholders: " +
                string.Join("; ", problems));
        }
    }

    private static HashSet<int> Placeholders(string template)
    {
        var found = new HashSet<int>();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') continue;
            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                i++;
                continue;
            }

            var end = i + 1;
            while (end < template.Length && char.IsDigit(template[end])) end++;
            if (end == i + 1) continue;
            if (end < template.Length && template[end] is not ('}' or ':' or ',')) continue;
            found.Add(int.Parse(template.AsSpan(i + 1, end - i - 1)));
        }

        return found;
    }

    private static string Truncate(string value) =>
        value.Length <= 40 ? value : value[..40] + "…";

    /// <summary>
    /// Literals that contain Chinese but are machine-readable data, such as the
    /// diagnostic-log redaction patterns whose alternation branches match key
    /// names. Translating one would change behaviour, not wording.
    /// </summary>
    private static HashSet<string> ReadUntranslatable(string resourceRoot)
    {
        var path = Path.Combine(resourceRoot, "_untranslatable.json");
        var exempt = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The untranslatable-literal list is missing. Without it the gate " +
                "would demand translations for redaction regexes.",
                path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            var value = entry.GetProperty("value").GetString();
            if (!string.IsNullOrEmpty(value)) exempt.Add(value);
        }

        return exempt;
    }
}

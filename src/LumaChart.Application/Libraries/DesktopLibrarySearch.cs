using System.Globalization;
using System.Text.RegularExpressions;

namespace LumaChart.Application.Libraries;

/// <summary>
/// Parsed form of the osu! song-select search box, following ppy/osu's
/// <c>osu.Game/Screens/Select/FilterQueryParser.cs</c> syntax and operator
/// semantics, evaluated against local <see cref="DesktopLibraryChartEntry"/>
/// rows instead of the online beatmap database.
///
/// Supported keyword fields: mapper/creator/author, artist, title, diff/version,
/// source, tag (personal tags, osu's "user tags"), key/keys, bpm, cs, and the
/// local library's own fields format, state, note and star (favourite).
/// Unsupported osu! fields (length, star rating, ar/od/hp/dr, ln, status,
/// dates, divisor) fall back to literal text exactly like osu! does for keys it
/// does not know; they are also reported through <see cref="UnsupportedFields"/>
/// so the UI can say why a term had no effect.
/// </summary>
public sealed class DesktopLibrarySearch
{
    /// <summary>The result of parsing or evaluating a query, kept allocation-free per chart.</summary>
    public static DesktopLibrarySearch Empty { get; } = new();

    // Copied from ppy/osu FilterQueryParser.query_syntax_regex:
    // \b(?<key>\w+)(?<op>(!?(:|=)|(>|<)(:|=)?))(?<value>("".*?""[!]?)|(\S*))
    private static readonly Regex QuerySyntax = new(
        "\\b(?<key>\\w+)(?<op>(!?(:|=)|(>|<)(:|=)?))(?<value>(\".*?\"[!]?)|(\\S*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    // FilterCriteria.SearchText consumes "[difficulty]" segments as difficulty filters.
    private static readonly Regex BracketSegment = new(
        "(?:^|(?<=\\s))\\[([^\\[\\]]*)\\](?=\\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // FilterCriteria.SearchText keeps quoted groups as one term.
    private static readonly Regex QuotedSegment = new(
        "\"[^\"]+\"[!]?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // osu!mania's key filter starts with every reachable key count
    // (2 * ManiaRuleset.MAX_STAGE_KEYS = 20) and narrows that set down.
    private const int MaxKeyCount = 20;

    private readonly List<FreeTerm> freeText = [];
    private readonly List<TextFilter> userTags = [];
    private readonly List<string> unsupportedFields = [];
    private TextFilter? creatorFilter;
    private TextFilter? artistFilter;
    private TextFilter? titleFilter;
    private TextFilter? difficultyFilter;
    private TextFilter? sourceFilter;
    private TextFilter? formatFilter;
    private TextFilter? stateFilter;
    private TextFilter? noteFilter;
    private bool? favoriteFilter;
    private KeyCountFilter? keyFilter;
    private NumberRange? bpmFilter;
    private NumberRange? circleSizeFilter;

    private DesktopLibrarySearch()
    {
    }

    /// <summary>Text that was not consumed as a keyword filter (osu!'s SearchText).</summary>
    public string FreeText { get; private set; } = string.Empty;

    /// <summary>osu! keywords this index cannot evaluate; they became literal text instead.</summary>
    public IReadOnlyList<string> UnsupportedFields => unsupportedFields;

    public bool HasBpmFilter => bpmFilter is { HasFilter: true };

    public bool HasPersonalFilters =>
        userTags.Count > 0 || stateFilter is not null || noteFilter is not null || favoriteFilter is not null;

    public bool IsEmpty => freeText.Count == 0 && userTags.Count == 0 && creatorFilter is null && artistFilter is null &&
                           titleFilter is null && difficultyFilter is null && sourceFilter is null && formatFilter is null &&
                           stateFilter is null && noteFilter is null && favoriteFilter is null && keyFilter is null &&
                           bpmFilter is null && circleSizeFilter is null;

    /// <summary>Parses a query. Malformed input never throws: it degrades to literal text.</summary>
    public static DesktopLibrarySearch Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Empty;
        try
        {
            return ParseCore(query!);
        }
        catch
        {
            // A pathological query must never break the search box. Fall back to
            // plain text matching, which is also what osu! does for unknown keys.
            var fallback = new DesktopLibrarySearch();
            fallback.freeText.Add(new FreeTerm(query!.Trim(), false, false));
            fallback.FreeText = query.Trim();
            return fallback;
        }
    }

    private static DesktopLibrarySearch ParseCore(string query)
    {
        var search = new DesktopLibrarySearch();
        var consumed = new List<(int Start, int Length)>();
        foreach (Match match in QuerySyntax.Matches(query))
        {
            var key = match.Groups["key"].Value.ToLowerInvariant();
            var op = ParseOperator(match.Groups["op"].Value);
            var value = match.Groups["value"].Value;
            var negated = IsNegatedAt(query, match.Index);
            var start = negated ? match.Index - 1 : match.Index;
            if (search.TryApplyKeyword(key, op, value, negated))
                consumed.Add((start, match.Index + match.Length - start));
        }

        var remaining = RemoveRanges(query, consumed);
        search.ParseSearchText(remaining);
        search.FreeText = remaining;
        return search;
    }

    /// <summary>
    /// Lower-cased personal text (tags, note, state) that free-text search looks
    /// through. The UI refreshes it whenever the annotation cache changes.
    /// </summary>
    public static string BuildPersonalSearchText(MapperAnnotation? annotation) =>
        annotation is null
            ? ""
            : ("\n" + string.Join(' ', annotation.Tags) + "\n" + annotation.Note + "\n" + annotation.State).ToLowerInvariant();

    /// <summary>Evaluates the parsed query against one cached library row.</summary>
    public bool Matches(DesktopLibraryChartEntry chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (creatorFilter is { } creator && !creator.Matches(chart.Creator)) return false;
        if (artistFilter is { } artist && !MatchEither(artist, chart.Artist, chart.ArtistOriginal)) return false;
        if (titleFilter is { } title && !MatchEither(title, chart.Title, chart.TitleOriginal)) return false;
        if (difficultyFilter is { } difficulty && !difficulty.Matches(chart.Version)) return false;
        if (sourceFilter is { } source && !source.Matches(chart.Source)) return false;
        if (formatFilter is { } format && !format.Matches(chart.Format)) return false;
        if (keyFilter is { } keys && !keys.Matches(chart.KeyCount)) return false;
        if (bpmFilter is { } bpm && !bpm.Matches(chart.Bpm)) return false;
        if (circleSizeFilter is { } circleSize && !circleSize.Matches(chart.KeyCount)) return false;

        var annotation = chart.PersonalAnnotation;
        var stateValue = annotation is null || annotation.State.Length == 0 ? "未分类" : annotation.State;
        if (stateFilter is { } state && !state.Matches(stateValue)) return false;
        if (noteFilter is { } note && !note.Matches(annotation?.Note ?? string.Empty)) return false;
        if (favoriteFilter is { } favorite && (annotation?.Favorite ?? false) != favorite) return false;
        foreach (var tagFilter in userTags)
            if (!MatchUserTags(tagFilter, annotation?.Tags)) return false;

        foreach (var term in freeText)
            if (!term.Matches(chart)) return false;
        return true;
    }

    private void ParseSearchText(string text)
    {
        if (text.Length == 0) return;

        // 1. "[difficulty]" segments (osu's FilterCriteria.SearchText).
        var bracketRanges = new List<(int Start, int Length)>();
        foreach (Match match in BracketSegment.Matches(text))
        {
            var value = match.Groups[1].Value.Trim();
            if (value.Length == 0) continue;
            difficultyFilter = new TextFilter(value, TextMode.Substring, false);
            bracketRanges.Add((match.Index, match.Length));
        }
        var withoutBrackets = RemoveRanges(text, bracketRanges);

        // 2. Quoted segments keep inline spaces; a trailing '!' means full phrase.
        var quotedRanges = new List<(int Start, int Length)>();
        foreach (Match match in QuotedSegment.Matches(withoutBrackets))
        {
            var raw = match.Value;
            var negated = IsNegatedAt(withoutBrackets, match.Index);
            var fullPhrase = raw.EndsWith('!');
            var term = (fullPhrase ? raw[..^1] : raw).Trim('"');
            if (term.Length == 0) continue;
            freeText.Add(new FreeTerm(term, negated, fullPhrase));
            quotedRanges.Add((negated ? match.Index - 1 : match.Index, match.Length + (negated ? 1 : 0)));
        }
        var withoutQuotes = RemoveRanges(withoutBrackets, quotedRanges).Trim();

        // 3. Everything else splits on spaces, with our legacy '-' negation.
        if (withoutQuotes.Length == 0) return;
        foreach (var token in withoutQuotes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var negated = token.Length > 1 && token[0] == '-';
            var term = negated ? token[1..] : token;
            // A lone "-" was ignored by the previous search box; keep that.
            if (term.Length == 0 || term == "-") continue;
            freeText.Add(new FreeTerm(term, negated, false));
        }
    }

    private bool TryApplyKeyword(string key, Operator op, string value, bool negated)
    {
        if (op == Operator.Unknown) return false;
        switch (key)
        {
            case "creator":
            case "author":
            case "mapper":
                return TryApplyText(ref creatorFilter, op, value, negated);
            case "artist":
                return TryApplyText(ref artistFilter, op, value, negated);
            case "title":
                return TryApplyText(ref titleFilter, op, value, negated);
            case "diff":
            case "version": // local alias: the library column is called Version
                return TryApplyText(ref difficultyFilter, op, value, negated);
            case "source":
                return TryApplyText(ref sourceFilter, op, value, negated);
            case "format": // local field: osu!mania vs Malody
                return TryApplyText(ref formatFilter, op, value, negated);
            case "state": // local personal state
                return TryApplyText(ref stateFilter, op, value, negated);
            case "note": // local personal note
                return TryApplyText(ref noteFilter, op, value, negated);
            case "tag":
                if (!TryParseTextFilter(op, value, negated, out var tagFilter)) return false;
                userTags.Add(tagFilter);
                return true;
            case "key":
            case "keys":
                keyFilter ??= new KeyCountFilter();
                return keyFilter.Apply(op, value, negated);
            case "bpm":
                return TryApplyRange(ref bpmFilter, op, value, 0.5, negated);
            case "cs":
                return TryApplyRange(ref circleSizeFilter, op, value, 0.05, negated);
            case "star":
                // osu!'s star rating is not indexed here; the local library uses
                // star:true for "favourite", which older searches already rely on.
                if (op is Operator.Equal or Operator.NotEqual && TryParseBool(value, out var favorite))
                {
                    favoriteFilter = (op == Operator.NotEqual) ^ negated ? !favorite : favorite;
                    return true;
                }
                AddUnsupported("star");
                return false;
            case "length":
            case "ln":
            case "lns":
            case "ar":
            case "od":
            case "hp":
            case "dr":
            case "sr":
            case "stars":
            case "lastplayed":
            case "ranked":
            case "created":
            case "submitted":
            case "played":
            case "divisor":
            case "status":
                AddUnsupported(key);
                return false;
            default:
                return false;
        }
    }

    private void AddUnsupported(string key)
    {
        if (!unsupportedFields.Contains(key, StringComparer.Ordinal)) unsupportedFields.Add(key);
    }

    private static bool TryApplyText(ref TextFilter? field, Operator op, string value, bool negated)
    {
        if (!TryParseTextFilter(op, value, negated, out var filter)) return false;
        field = filter;
        return true;
    }

    private static bool TryParseTextFilter(Operator op, string value, bool negated, out TextFilter filter)
    {
        filter = default!;
        if (op is not (Operator.Equal or Operator.NotEqual)) return false;
        // osu! only accepts '=' / ':' and '!=' / '!:' for textual filters.
        var exclude = (op == Operator.NotEqual) ^ negated;
        var (term, mode) = ParseTextValue(value);
        filter = new TextFilter(term, mode, exclude);
        return true;
    }

    /// <summary>OptionalTextFilter.SearchTerm handling: quotes pick the match mode.</summary>
    private static (string Term, TextMode Mode) ParseTextValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"')
        {
            if (value.Length >= 3 && value.EndsWith("\"!", StringComparison.Ordinal))
                return (value[1..^2], TextMode.FullPhrase);
            return (value.Trim('"'), TextMode.IsolatedPhrase);
        }
        return (value, TextMode.Substring);
    }

    private static bool TryApplyRange(ref NumberRange? field, Operator op, string value, double tolerance, bool negated)
    {
        // osu! parses these with CompareOptions.AllowDecimalPoint only (no sign).
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return false;
        var range = field ??= new NumberRange();
        range.Apply(op, number, tolerance, negated);
        return true;
    }

    private static Operator ParseOperator(string value) => value switch
    {
        "=" or ":" => Operator.Equal,
        "!=" or "!:" => Operator.NotEqual,
        "<" => Operator.Less,
        "<=" or "<:" => Operator.LessOrEqual,
        ">" => Operator.Greater,
        ">=" or ">:" => Operator.GreaterOrEqual,
        _ => Operator.Unknown
    };

    private static bool IsNegatedAt(string text, int index)
    {
        if (index <= 0 || text[index - 1] != '-') return false;
        return index == 1 || char.IsWhiteSpace(text[index - 2]);
    }

    private static string RemoveRanges(string text, List<(int Start, int Length)> ranges)
    {
        if (ranges.Count == 0) return text;
        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        var builder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (var (start, length) in ranges)
        {
            if (start < position) continue;
            builder.Append(text, position, start - position);
            position = start + length;
        }
        if (position < text.Length) builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    private static bool MatchEither(TextFilter filter, string primary, string secondary) =>
        filter.Exclude
            ? filter.Matches(primary) && filter.Matches(secondary)
            : filter.Matches(primary) || filter.Matches(secondary);

    private static bool MatchUserTags(TextFilter filter, string[]? tags)
    {
        // osu! applies a positive user tag filter against any tag and an excluded
        // one against every tag (i.e. no tag may contain the term).
        if (!filter.Exclude)
        {
            if (tags is null) return false;
            foreach (var tag in tags)
                if (filter.Matches(tag)) return true;
            return false;
        }
        if (tags is null) return true;
        foreach (var tag in tags)
            if (!filter.Matches(tag)) return false;
        return true;
    }

    private static bool TryParseBool(string value, out bool result)
    {
        // osu!'s tryParseBool plus the local library's legacy yes/no spellings.
        switch (value.ToLowerInvariant())
        {
            case "1":
            case "yes":
            case "true":
                result = true;
                return true;
            case "0":
            case "no":
            case "false":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private enum Operator
    {
        Unknown,
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual
    }

    private enum TextMode
    {
        Substring,

        /// <summary>osu! matches quoted text on word boundaries. CJK text has no
        /// word boundaries in practice, so this library keeps substring matching
        /// to preserve the existing CJK search behaviour.</summary>
        IsolatedPhrase,
        FullPhrase
    }

    private sealed class TextFilter
    {
        public TextFilter(string term, TextMode mode, bool exclude)
        {
            Term = term;
            Mode = mode;
            Exclude = exclude;
        }

        public string Term { get; }
        public TextMode Mode { get; }
        public bool Exclude { get; }

        public bool Matches(string? value)
        {
            if (Term.Length == 0) return true;
            if (string.IsNullOrEmpty(value)) return Exclude;
            var hit = Mode == TextMode.FullPhrase
                ? value.Equals(Term, StringComparison.OrdinalIgnoreCase)
                : value.Contains(Term, StringComparison.OrdinalIgnoreCase);
            return Exclude ? !hit : hit;
        }
    }

    private sealed class FreeTerm
    {
        public FreeTerm(string text, bool negated, bool fullPhrase)
        {
            Text = text;
            Lower = text.ToLowerInvariant();
            Negated = negated;
            FullPhrase = fullPhrase;
        }

        public string Text { get; }
        public string Lower { get; }
        public bool Negated { get; }
        public bool FullPhrase { get; }

        public bool Matches(DesktopLibraryChartEntry chart)
        {
            bool hit;
            if (FullPhrase)
            {
                hit = EqualsField(chart.Title, Text) || EqualsField(chart.TitleOriginal, Text) ||
                      EqualsField(chart.Artist, Text) || EqualsField(chart.ArtistOriginal, Text) ||
                      EqualsField(chart.Creator, Text) || EqualsField(chart.Version, Text) ||
                      EqualsField(chart.Source, Text) || EqualsField(chart.Format, Text) ||
                      EqualsField(chart.PersonalAnnotation?.Note, Text);
            }
            else
            {
                var searchText = chart.SearchText;
                hit = searchText.Contains(Lower, StringComparison.Ordinal);
                if (!hit && chart.PersonalSearchText.Length > 0)
                    hit = chart.PersonalSearchText.Contains(Lower, StringComparison.Ordinal);
            }
            return Negated ? !hit : hit;
        }

        private static bool EqualsField(string? value, string term) =>
            !string.IsNullOrEmpty(value) && value.Equals(term, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NumberRange
    {
        private double? min;
        private double? max;
        private bool lowerInclusive;
        private bool upperInclusive;
        private bool invertRange;

        public bool HasFilter => min.HasValue || max.HasValue;

        public void Apply(Operator op, double value, double tolerance, bool negated)
        {
            if (negated) op = Invert(op);
            invertRange = false;
            switch (op)
            {
                default:
                    return;
                case Operator.NotEqual:
                    invertRange = true;
                    goto case Operator.Equal;
                case Operator.Equal:
                    min = value - tolerance;
                    max = value + tolerance;
                    if (tolerance == 0) lowerInclusive = upperInclusive = true;
                    break;
                case Operator.Greater:
                    min = value + tolerance;
                    break;
                case Operator.GreaterOrEqual:
                    min = value - tolerance;
                    if (tolerance == 0) lowerInclusive = true;
                    break;
                case Operator.Less:
                    max = value - tolerance;
                    break;
                case Operator.LessOrEqual:
                    max = value + tolerance;
                    if (tolerance == 0) upperInclusive = true;
                    break;
            }
        }

        public bool Matches(double? value)
        {
            if (!HasFilter) return true;
            // The index may not know this value (for example BPM of an
            // uncached .mc chart); an unknown value cannot satisfy a range.
            if (!value.HasValue) return false;
            var lowerSatisfied = true;
            var upperSatisfied = true;
            if (min is { } low)
            {
                var comparison = value.Value.CompareTo(low);
                lowerSatisfied = comparison > 0 || (comparison == 0 && lowerInclusive);
            }
            if (max is { } high)
            {
                var comparison = value.Value.CompareTo(high);
                upperSatisfied = comparison < 0 || (comparison == 0 && upperInclusive);
            }
            var result = lowerSatisfied && upperSatisfied;
            return invertRange ? !result : result;
        }

        private static Operator Invert(Operator op) => op switch
        {
            Operator.Equal => Operator.NotEqual,
            Operator.NotEqual => Operator.Equal,
            Operator.Less => Operator.GreaterOrEqual,
            Operator.LessOrEqual => Operator.Greater,
            Operator.Greater => Operator.LessOrEqual,
            Operator.GreaterOrEqual => Operator.Less,
            _ => Operator.Unknown
        };
    }

    private sealed class KeyCountFilter
    {
        private readonly HashSet<int> included = Enumerable.Range(1, MaxKeyCount).ToHashSet();

        public bool Matches(int keyCount) => included.Contains(keyCount);

        public bool Apply(Operator op, string value, bool negated)
        {
            // osu!mania: key=7, key=4,7, key<8, key!=6
            var counts = new List<int>();
            foreach (var part in value.Split(','))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) return false;
                counts.Add(count);
            }
            if (counts.Count == 0) return false;
            if (negated) op = Invert(op);
            switch (op)
            {
                case Operator.Equal:
                    included.IntersectWith(counts);
                    return true;
                case Operator.NotEqual:
                    included.ExceptWith(counts);
                    return true;
                case Operator.Less:
                case Operator.LessOrEqual:
                case Operator.Greater:
                case Operator.GreaterOrEqual:
                    if (counts.Count != 1) return false;
                    var pivot = counts[0];
                    included.RemoveWhere(key =>
                        op == Operator.Less ? key >= pivot :
                        op == Operator.LessOrEqual ? key > pivot :
                        op == Operator.Greater ? key <= pivot : key < pivot);
                    return true;
                default:
                    return false;
            }
        }

        private static Operator Invert(Operator op) => op switch
        {
            Operator.Equal => Operator.NotEqual,
            Operator.NotEqual => Operator.Equal,
            Operator.Less => Operator.GreaterOrEqual,
            Operator.LessOrEqual => Operator.Greater,
            Operator.Greater => Operator.LessOrEqual,
            Operator.GreaterOrEqual => Operator.Less,
            _ => Operator.Unknown
        };
    }
}

/// <summary>Result of evaluating a library search query against the cached rows.</summary>
public sealed record DesktopLibrarySearchResult(DesktopLibrarySearch Query, HashSet<string>? Paths)
{
    public bool IsEmpty => Paths is null;
}
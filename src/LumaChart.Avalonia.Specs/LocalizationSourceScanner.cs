using System.Text;

namespace LumaChart.Avalonia.Specs;

/// <summary>
/// Finds every Chinese string literal in the Avalonia UI sources so the
/// coverage gate measures translations against what the UI actually shows,
/// not against whatever happens to be in the catalog already.
///
/// Literals are split into two kinds because they need different fixes:
/// <list type="bullet">
/// <item><see cref="LocalizationLiteralKind.Whole"/> is a complete label and
/// can be a catalog key as-is.</item>
/// <item><see cref="LocalizationLiteralKind.Segment"/> is a chunk between the
/// holes of an interpolated string (<c>$"已邀请 {n} 人"</c>). Those cannot be
/// catalog keys — word order differs per language — so they have to become
/// format templates before they can be translated.</item>
/// </list>
/// </summary>
public static class LocalizationSourceScanner
{
    private static readonly string[] ExcludedFileNames =
    [
        "AvaloniaLocalizationCatalog.cs",
        "AvaloniaLocalizationController.cs",
        "LocalizationCatalog.cs"
    ];

    public static IReadOnlyList<LocalizationLiteral> Scan(string uiSourceRoot)
    {
        ArgumentNullException.ThrowIfNull(uiSourceRoot);
        var results = new List<LocalizationLiteral>();
        foreach (var file in Directory.EnumerateFiles(
                     uiSourceRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(file) ?? string.Empty;
            if (directory.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                directory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
                continue;
            var name = Path.GetFileName(file);
            if (ExcludedFileNames.Contains(name, StringComparer.Ordinal)) continue;
            ScanFile(File.ReadAllText(file), name, results);
        }

        return results;
    }

    private static void ScanFile(
        string text,
        string fileName,
        List<LocalizationLiteral> results)
    {
        var literals = new List<RawLiteral>();
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (c == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                var end = text.IndexOf('\n', index);
                index = end < 0 ? text.Length : end;
            }
            else if (c == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 2;
            }
            else if (c == '\'')
            {
                index = SkipCharLiteral(text, index);
            }
            else if (c == '"' || c == '@' || c == '$')
            {
                var quote = index;
                while (quote < text.Length && (text[quote] == '@' || text[quote] == '$'))
                    quote++;
                if (quote < text.Length && text[quote] == '"')
                    index = ReadString(text, quote, text[index..quote], index, literals);
                else
                    index++;
            }
            else
            {
                index++;
            }
        }

        Classify(text, fileName, literals, results);
    }

    /// <summary>
    /// Judges each literal against the whole `+` chain it belongs to. A chain is
    /// dynamic if any link interpolates, or if `+` joins it to an expression:
    /// <c>$"检测完成\n\n" + $"BPM：{x}"</c> cannot have its first link translated
    /// on its own any more than its second.
    /// </summary>
    private static void Classify(
        string text,
        string fileName,
        List<RawLiteral> literals,
        List<LocalizationLiteral> results)
    {
        var byStart = new Dictionary<int, int>();
        for (var i = 0; i < literals.Count; i++) byStart[literals[i].Start] = i;

        var chainOf = new int[literals.Count];
        var dynamic = new List<bool>();
        Array.Fill(chainOf, -1);
        for (var i = 0; i < literals.Count; i++)
        {
            if (chainOf[i] < 0)
            {
                dynamic.Add(literals[i].HasHole);
                chainOf[i] = dynamic.Count - 1;
            }

            var chain = chainOf[i];
            dynamic[chain] = dynamic[chain] || literals[i].HasHole;
            var after = SkipTrivia(text, literals[i].End);
            if (after >= text.Length || text[after] != '+') continue;
            var next = SkipTrivia(text, after + 1);
            if (byStart.TryGetValue(next, out var j)) chainOf[j] = chain;
            else dynamic[chain] = true;
        }

        for (var i = 0; i < literals.Count; i++)
        {
            var literal = literals[i];
            if (!ContainsChinese(literal.Value)) continue;
            if (literal.HasHole)
            {
                results.Add(new LocalizationLiteral(
                    literal.Value, LocalizationLiteralKind.Segment, fileName));
                continue;
            }

            var kind = dynamic[chainOf[i]]
                ? LocalizationLiteralKind.Concat
                : LocalizationLiteralKind.Whole;
            if (kind == LocalizationLiteralKind.Whole && IsAppendedOrConcatenated(text, literal.Start))
                kind = LocalizationLiteralKind.Concat;
            results.Add(new LocalizationLiteral(literal.Value, kind, fileName));
        }
    }

    private static bool IsAppendedOrConcatenated(string text, int start)
    {
        var before = LastMeaningful(text, start);
        if (before < 0) return false;
        // `… + "x"`, or `summary += "x"` appending to a message built up in steps
        if (text[before] == '+') return true;
        if (text[before] == '=' && before >= 1 && text[before - 1] == '+') return true;

        // A parenthesised ternary arm whose parenthesis itself is concatenated:
        // `base + (flag ? "甲" : "乙")`
        var paren = EnclosingParen(text, start);
        if (paren < 0) return false;
        var beforeParen = LastMeaningful(text, paren);
        if (beforeParen >= 0 && text[beforeParen] == '+') return true;
        var depth = 1;
        var j = paren + 1;
        while (j < text.Length && depth > 0)
        {
            if (text[j] == '(') depth++;
            else if (text[j] == ')') depth--;
            j++;
        }

        var afterParen = SkipTrivia(text, j);
        return afterParen < text.Length && text[afterParen] == '+';
    }

    private static int EnclosingParen(string text, int position)
    {
        var depth = 0;
        var limit = Math.Max(0, position - 4000);
        for (var i = position - 1; i >= limit; i--)
        {
            var c = text[i];
            if (c is ')' or ']' or '}') depth++;
            else if (c is '(' or '[' or '{')
            {
                if (depth == 0) return c == '(' ? i : -1;
                depth--;
            }
        }

        return -1;
    }

    private static int LastMeaningful(string text, int position)
    {
        for (var i = position - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            var lineStart = text.LastIndexOf('\n', Math.Max(i - 1, 0)) + 1;
            var comment = text.IndexOf("//", lineStart, Math.Max(i + 1 - lineStart, 0),
                StringComparison.Ordinal);
            if (comment >= 0)
            {
                i = comment;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static int SkipTrivia(string text, int position)
    {
        var i = position;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }

            break;
        }

        return i;
    }

    private readonly record struct RawLiteral(
        int Start,
        int End,
        string Value,
        bool HasHole);

    private static int SkipCharLiteral(string text, int index)
    {
        var i = index + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == '\'') return i + 1;
            if (text[i] == '\n') return i;
            i++;
        }

        return i;
    }

    private static int ReadString(
        string text,
        int quote,
        string prefix,
        int literalStart,
        List<RawLiteral> literals)
    {
        var verbatim = prefix.Contains('@', StringComparison.Ordinal);
        var interpolated = prefix.Contains('$', StringComparison.Ordinal);

        var fence = 0;
        while (quote + fence < text.Length && text[quote + fence] == '"') fence++;
        if (fence >= 3 && !verbatim)
        {
            var terminator = new string('"', fence);
            var close = text.IndexOf(terminator, quote + fence, StringComparison.Ordinal);
            if (close < 0) close = text.Length;
            var rawEnd = Math.Min(close + fence, text.Length);
            literals.Add(new RawLiteral(
                literalStart, rawEnd, text[(quote + fence)..close], false));
            return rawEnd;
        }

        var segments = new List<string>();
        var buffer = new StringBuilder();
        var i = quote + 1;
        var closed = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (interpolated && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    buffer.Append('{');
                    i += 2;
                    continue;
                }

                i = SkipInterpolationHole(text, i);
                if (buffer.Length > 0)
                {
                    segments.Add(buffer.ToString());
                    buffer.Clear();
                }

                continue;
            }

            if (interpolated && c == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                buffer.Append('}');
                i += 2;
                continue;
            }

            if (verbatim)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    buffer.Append('"');
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    closed = true;
                    break;
                }
            }
            else
            {
                if (c == '\\')
                {
                    buffer.Append(Unescape(text, i, out var consumed));
                    i += consumed;
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    closed = true;
                    break;
                }

                if (c == '\n') break;
            }

            buffer.Append(c);
            i++;
        }

        if (buffer.Length > 0) segments.Add(buffer.ToString());

        // An interpolated string with holes yields one entry per literal chunk;
        // one without holes is an ordinary literal that merely used the $ prefix.
        var hasHole = interpolated && closed && segments.Count > 0 && HadHole(text, quote, i);
        if (hasHole)
        {
            foreach (var segment in segments)
                literals.Add(new RawLiteral(literalStart, i, segment, true));
        }
        else
        {
            literals.Add(new RawLiteral(literalStart, i, string.Concat(segments), false));
        }

        return i;
    }

    private static bool HadHole(string text, int start, int end)
    {
        for (var i = start; i < end - 1 && i < text.Length - 1; i++)
        {
            if (text[i] != '{') continue;
            if (text[i + 1] == '{')
            {
                i++;
                continue;
            }

            return true;
        }

        return false;
    }

    private static int SkipInterpolationHole(string text, int index)
    {
        var depth = 1;
        var i = index + 1;
        while (i < text.Length && depth > 0)
        {
            var c = text[i];
            if (c == '"')
            {
                i = SkipNestedString(text, i);
                continue;
            }

            if (c == '\'')
            {
                i = SkipCharLiteral(text, i);
                continue;
            }

            if (c == '{') depth++;
            else if (c == '}') depth--;
            i++;
        }

        return i;
    }

    private static int SkipNestedString(string text, int index)
    {
        var i = index + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == '"') return i + 1;
            if (text[i] == '\n') return i;
            i++;
        }

        return i;
    }

    private static string Unescape(string text, int index, out int consumed)
    {
        consumed = 2;
        if (index + 1 >= text.Length)
        {
            consumed = 1;
            return string.Empty;
        }

        return text[index + 1] switch
        {
            'n' => "\n",
            't' => "\t",
            'r' => "\r",
            '0' => "\0",
            'a' => "\a",
            'b' => "\b",
            'f' => "\f",
            'v' => "\v",
            _ => text[index + 1].ToString()
        };
    }

    private static bool ContainsChinese(string value)
    {
        foreach (var c in value)
        {
            if (c is >= '一' and <= '鿿') return true;
        }

        return false;
    }
}

public enum LocalizationLiteralKind
{
    /// <summary>A complete, independent label; usable as a catalog key as-is.</summary>
    Whole,

    /// <summary>
    /// A link in a `+` chain that is dynamic overall, or a piece appended with
    /// `+=`. The chain is one translation unit, so these have to be merged into
    /// a single format template before they can be translated.
    /// </summary>
    Concat,

    /// <summary>A literal chunk between interpolation holes.</summary>
    Segment
}

public sealed record LocalizationLiteral(
    string Value,
    LocalizationLiteralKind Kind,
    string FileName);

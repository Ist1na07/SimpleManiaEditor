using System.Globalization;
using System.Text;
using Kawazu;
using WanaKanaNet;

namespace LumaChart.Avalonia.Services;

/// <summary>
/// Offline Japanese reading and Hepburn romanization used by the Windows metadata editor.
/// Keeping this outside the window preserves the WPF 1.1.0 contract without UI coupling.
/// </summary>
internal static class WindowsJapaneseRomanizer
{
    // Only a successfully built converter is cached, keyed by the dictionary
    // root it was built from. A Lazy<T> used to cache the first failure for the
    // whole process: on Android the dictionary is extracted in the background
    // and DictionaryRoot stays null until then, so romanizing before extraction
    // finished disabled it until the next launch. Windows resolves the same
    // root every time, so it still builds the converter once.
    private static readonly object ConverterGate = new();
    private static KawazuConverter? cachedConverter;
    private static string? cachedDictionaryRoot;

    public static async Task<string> RomanizeAsync(
        string? source,
        CancellationToken cancellationToken = default)
    {
        var original = CleanSingleLine(source ?? string.Empty);
        if (original.Length == 0) return string.Empty;
        cancellationToken.ThrowIfCancellationRequested();

        var reading = original;
        if (original.Any(WanaKana.IsKanji))
        {
            var converter = GetConverter();
            try
            {
                reading = await converter.Convert(
                        original,
                        To.Katakana,
                        Mode.Spaced,
                        RomajiSystem.Hepburn,
                        "(",
                        ")")
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A converter whose dictionary turned out to be unusable is not
                // kept; the next request builds a fresh one.
                ForgetConverter(converter);
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        return RomanizeReading(reading);
    }

    private static KawazuConverter GetConverter()
    {
        var dictionaryRoot = PlatformJapaneseRomanizationServices.DictionaryRoot ??
                             Path.Combine(AppContext.BaseDirectory, "IpaDic");
        lock (ConverterGate)
        {
            if (cachedConverter != null &&
                string.Equals(cachedDictionaryRoot, dictionaryRoot, StringComparison.Ordinal))
                return cachedConverter;

            // A construction failure propagates without being cached.
            var converter = new KawazuConverter(dictionaryRoot);
            cachedConverter = converter;
            cachedDictionaryRoot = dictionaryRoot;
            return converter;
        }
    }

    private static void ForgetConverter(KawazuConverter converter)
    {
        lock (ConverterGate)
        {
            if (!ReferenceEquals(cachedConverter, converter)) return;
            cachedConverter = null;
            cachedDictionaryRoot = null;
        }
    }

    internal static string RomanizeReading(string? source)
    {
        var cleaned = CleanSingleLine(source ?? string.Empty);
        if (cleaned.Length == 0) return string.Empty;

        var separated = new StringBuilder(cleaned.Length + 8);
        var previousScript = 0;
        foreach (var character in cleaned)
        {
            // WanaKana classifies the Japanese long-vowel mark as both scripts.
            // Keep it attached to the preceding kana so ボーダー remains Boodaa.
            var script = character == 'ー'
                ? previousScript
                : WanaKana.IsHiragana(character)
                    ? 1
                    : WanaKana.IsKatakana(character)
                        ? 2
                        : 0;
            if (separated.Length > 0 && script != 0 && previousScript != 0 &&
                script != previousScript && separated[^1] != ' ')
                separated.Append(' ');
            separated.Append(character);
            previousScript = script;
        }

        var romanized = WanaKana.ToRomaji(
            separated.ToString(),
            new WanaKanaOptions { PassRomaji = true });
        var ascii = new string(romanized.Normalize()
            .Select(character => character is >= ' ' and <= '~' ? character : ' ')
            .ToArray());
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            CleanSingleLine(ascii).ToLowerInvariant());
    }

    private static string CleanSingleLine(string value) =>
        string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

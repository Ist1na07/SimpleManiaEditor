using Avalonia;
using Avalonia.Media;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Installs the embedded Simplified-Chinese typeface as the default and adds
/// common system CJK families as glyph fallbacks for the mobile hosts.
///
/// The editor theme (<see cref="WindowsWpf110ControlTheme"/>) asks every control
/// for "Segoe UI Variable Text, Microsoft YaHei UI". Both families exist on
/// Windows and neither exists on a phone, so on mobile the request always misses
/// and the font manager falls through to whatever default the platform offers.
/// On stock Android that default plus Skia's per-glyph fallback happens to reach
/// a CJK face; on some OEM ROMs it does not, and Chinese text renders as nothing
/// at all. Because that outcome is fixed by the device's own font set, it
/// survived reinstalling the app and rebooting the device.
///
/// Pointing the default family and first fallback at a bundled complete
/// Simplified-Chinese face removes the dependency for the original Chinese UI.
/// The subsequent named families cover Traditional Chinese, Japanese, and
/// Korean scripts on Android and iOS. Windows is untouched — the desktop
/// project does not import the shared props that embed this font and does not
/// call this.
/// </summary>
public static class MobileFontConfiguration
{
    /// <summary>
    /// The font's own family name, taken from its OpenType name table
    /// (nameID 1, en-US). It must match the text after '#'. The Chinese name is
    /// "思源黑体 CN".
    /// </summary>
    public const string EmbeddedFamilyName = "Source Han Sans CN";

    private const string ResourcePath = "Assets/Fonts/SourceHanSansCN-Regular.otf";

    /// <summary>
    /// Builds the <c>avares://</c> URI for the embedded font. The font is
    /// compiled into whichever assembly imports the shared props —
    /// LumaChart.Avalonia.Shared on Android, LumaChart.Avalonia.Mobile on iOS —
    /// so the assembly name is read from this type at runtime rather than
    /// hard-coded, and the same code is correct for both hosts.
    /// </summary>
    private static string FontUri
    {
        get
        {
            var assemblyName = typeof(MobileFontConfiguration).Assembly.GetName().Name;
            return $"avares://{assemblyName}/{ResourcePath}#{EmbeddedFamilyName}";
        }
    }

    /// <summary>
    /// Registers the embedded face as the default family and as the glyph
    /// fallback. Call this from a mobile host's <c>CustomizeAppBuilder</c>.
    /// </summary>
    public static AppBuilder WithEmbeddedChineseFont(this AppBuilder builder)
    {
        var uri = FontUri;
        return builder.With(new FontManagerOptions
        {
            DefaultFamilyName = uri,
            FontFallbacks = new[]
            {
                new FontFallback { FontFamily = new FontFamily(uri) },
                new FontFallback
                {
                    FontFamily = new FontFamily(
                        "PingFang TC, Hiragino Sans, Apple SD Gothic Neo, " +
                        "Noto Sans CJK TC, Noto Sans CJK JP, Noto Sans CJK KR, " +
                        "Noto Sans Arabic, Noto Sans Devanagari, Noto Sans Thai, " +
                        "Noto Sans, Roboto")
                }
            }
        });
    }
}

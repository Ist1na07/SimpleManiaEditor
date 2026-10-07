using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Editing;

public static class WindowsOnlineCreatorMarkerPolicy
{
    public const string Contract = "wpf-1.1.0-online-creator-marker-r1";
    public const double TimingMarkerSize = 12;

    public static bool ShouldDraw(bool playbackActive, long userId) =>
        !playbackActive && userId > 0;

    public static double NoteMarkerSize(double noteHeight) =>
        Math.Clamp(noteHeight * .58, 11, 15);

    public static int ColorIndex(long userId, int colorCount)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        if (colorCount <= 0) throw new ArgumentOutOfRangeException(nameof(colorCount));
        return (int)((ulong)userId % (uint)colorCount);
    }

    public static string FallbackInitial(string? displayName)
    {
        var normalized = displayName?.Trim() ?? string.Empty;
        return normalized.Length == 0
            ? string.Empty
            : normalized[0].ToString().ToUpperInvariant();
    }

    public static string StatusSuffix(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName)
            ? string.Empty
            : AvaloniaLocalizationCatalog.Format("  ·  创建者 {0}", displayName.Trim());

    public static string? ToolTipText(string? displayName) =>
        string.IsNullOrWhiteSpace(displayName)
            ? null
            : AvaloniaLocalizationCatalog.Format("创建者  {0}", displayName.Trim());
}

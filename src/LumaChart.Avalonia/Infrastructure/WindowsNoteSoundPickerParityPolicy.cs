using Avalonia.Input;

namespace LumaChart.Avalonia.Infrastructure;

public enum WindowsNoteSoundPickerNavigationAction
{
    None,
    Focus,
    CloseAndRestore
}

public readonly record struct WindowsNoteSoundPickerNavigationResult(
    WindowsNoteSoundPickerNavigationAction Action,
    int TargetIndex);

/// <summary>
/// WPF 1.1.0 geometry and keyboard behavior for the dedicated existing
/// Note Key-sound popup. The popup cycles its own focus and Escape returns
/// focus to the button that opened it.
/// </summary>
public static class WindowsNoteSoundPickerParityPolicy
{
    public const double Width = 330;
    public const double MaxHeight = 370;
    public const double Padding = 9;
    public const double CornerRadius = 6;
    public const double BorderThickness = 1;
    public const double VerticalOffset = 5;
    public const double ScrollMaxHeight = 250;
    public const double HeaderFontSize = 13;
    public const double SubtitleFontSize = 9;
    public const double CountFontSize = 10;
    public const double ItemFontSize = 12;
    public const double ItemHorizontalPadding = 11;
    public const double ItemVerticalPadding = 8;

    public const string Background = "#181D20";
    public const string Border = "#4A5A60";
    public const string Header = "#F0F3F3";
    public const string Subtitle = "#7F9095";
    public const string Count = "#82A7AA";
    public const string Item = "#DDE3E4";
    public const string ClearGlyph = "#D69A8D";
    public const string SoundGlyph = "#83AEB1";
    public const string Separator = "#30393D";

    public static KeyboardNavigationMode PopupTabNavigationMode =>
        KeyboardNavigationMode.Cycle;

    public static WindowsNoteSoundPickerNavigationResult ResolveNavigation(
        Key key,
        KeyModifiers modifiers,
        int focusedIndex,
        int focusableCount)
    {
        if (key == Key.Escape)
            return new WindowsNoteSoundPickerNavigationResult(
                WindowsNoteSoundPickerNavigationAction.CloseAndRestore,
                -1);

        if (focusableCount <= 0)
            return default;

        var direction = 0;
        if (key == Key.Tab &&
            (modifiers == KeyModifiers.None || modifiers == KeyModifiers.Shift))
            direction = modifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
        else if (modifiers == KeyModifiers.None && key is Key.Down or Key.Right)
            direction = 1;
        else if (modifiers == KeyModifiers.None && key is Key.Up or Key.Left)
            direction = -1;
        else if (modifiers == KeyModifiers.None && key == Key.Home)
            return Focus(0);
        else if (modifiers == KeyModifiers.None && key == Key.End)
            return Focus(focusableCount - 1);
        else
            return default;

        var origin = focusedIndex >= 0 && focusedIndex < focusableCount
            ? focusedIndex
            : direction > 0
                ? -1
                : 0;
        var target = (origin + direction + focusableCount) % focusableCount;
        return Focus(target);
    }

    private static WindowsNoteSoundPickerNavigationResult Focus(int index) =>
        new(WindowsNoteSoundPickerNavigationAction.Focus, index);
}

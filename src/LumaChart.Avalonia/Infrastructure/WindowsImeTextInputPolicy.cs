using Avalonia.Controls;
using Avalonia.Input;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Restores the WPF text-host boundary explicitly when a reusable account
/// surface is detached from its Window and hosted in the main Avalonia tree.
/// Password controls use <see cref="WindowsPasswordInputPolicy"/> instead.
/// </summary>
public static class WindowsImeTextInputPolicy
{
    public static void Apply(TextBox textBox)
    {
        ArgumentNullException.ThrowIfNull(textBox);
        InputMethod.SetIsInputMethodEnabled(textBox, true);
    }
}

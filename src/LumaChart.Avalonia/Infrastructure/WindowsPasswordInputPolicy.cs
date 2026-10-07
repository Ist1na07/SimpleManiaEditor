using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using LumaChart.Avalonia.Services;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Keeps Avalonia password entry aligned with the Windows/WPF PasswordBox boundary:
/// the active IME is bypassed, physical keys are committed directly, and automation
/// exposes only the visible bullet count rather than the secret value.
/// </summary>
public static class WindowsPasswordInputPolicy
{
    public const string AutomationStatusPrefix =
        "Windows 密码输入; ImeEnabled=false; CommittedLength=";

    public static void Apply(TextBox passwordBox)
    {
        ArgumentNullException.ThrowIfNull(passwordBox);
        var imeEnabled = PlatformTextInputServices.Current?.EnablePasswordInputMethod ?? false;
        InputMethod.SetIsInputMethodEnabled(passwordBox, imeEnabled);
        TextInputOptions.SetContentType(passwordBox, TextInputContentType.Password);
        TextInputOptions.SetIsSensitive(passwordBox, true);
        TextInputOptions.SetAutoCapitalization(passwordBox, false);
        TextInputOptions.SetShowSuggestions(passwordBox, false);
        UpdateAutomationStatus(passwordBox, imeEnabled);
        passwordBox.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty)
                UpdateAutomationStatus(passwordBox, imeEnabled);
        };
    }

    public static string CreateAutomationStatus(string? committedText) =>
        AutomationStatusPrefix + (committedText?.Length ?? 0);

    /// <summary>
    /// The platform whose password-entry boundary is in force. Only the label
    /// differs; iOS enables the input method for the same reason Android does,
    /// except that there the input method is the on-screen keyboard itself and
    /// disabling it leaves the field with no way to be typed into at all.
    /// </summary>
#if LUMACHART_IOS
    private const string InputMethodPlatformLabel = "iOS";
#else
    private const string InputMethodPlatformLabel = "Android";
#endif

    private static void UpdateAutomationStatus(TextBox passwordBox, bool imeEnabled) =>
        AutomationProperties.SetHelpText(
            passwordBox,
            imeEnabled
                ? AvaloniaLocalizationCatalog.Format("{0} 密码输入; ImeEnabled=true; CommittedLength={1}", InputMethodPlatformLabel, passwordBox.Text?.Length ?? 0)
                : CreateAutomationStatus(passwordBox.Text));
}

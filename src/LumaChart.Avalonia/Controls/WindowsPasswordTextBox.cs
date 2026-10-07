using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace LumaChart.Avalonia.Controls;

/// <summary>
/// Windows password entry that retains TextBox editing behavior while preventing
/// the Avalonia UI Automation value provider from returning the clear text.
/// </summary>
public class WindowsPasswordTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new WindowsPasswordTextBoxAutomationPeer(this);

    private sealed class WindowsPasswordTextBoxAutomationPeer(
        WindowsPasswordTextBox owner)
        : TextBoxAutomationPeer(owner)
    {
        private readonly SafePasswordValueProvider valueProvider = new(owner);

        protected override string GetClassNameCore() => "PasswordBox";

        protected override object? GetProviderCore(Type providerType) =>
            providerType == typeof(IValueProvider)
                ? valueProvider
                : base.GetProviderCore(providerType);
    }

    private sealed class SafePasswordValueProvider(
        WindowsPasswordTextBox owner)
        : IValueProvider
    {
        public bool IsReadOnly => owner.IsReadOnly;

        public string Value => string.Empty;

        public void SetValue(string? value)
        {
            if (owner.IsReadOnly)
                throw new InvalidOperationException("The password field is read-only.");
            owner.Text = value ?? string.Empty;
        }
    }
}

using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace LumaChart.Avalonia.Controls;

/// <summary>
/// Border with a generic automation peer. Avalonia's plain Border is visual-only
/// and otherwise omits its AutomationId from the Windows UI Automation tree.
/// </summary>
public sealed class AutomationBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ControlAutomationPeer(this);
}

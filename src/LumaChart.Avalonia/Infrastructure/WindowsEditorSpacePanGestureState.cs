using Avalonia.Input;

namespace LumaChart.Avalonia.Infrastructure;

public enum WindowsEditorSpacePanKeyDownAction
{
    None,
    Arm,
    SuppressRepeat
}

public enum WindowsEditorSpacePanKeyUpAction
{
    None,
    ConsumedPan,
    TogglePlayback
}

/// <summary>
/// Keeps WPF's ProEditor Space-drag gesture separate from the configurable
/// Space playback shortcut. Avalonia does not expose WPF's IsRepeat flag, so
/// the armed state also prevents repeated key-down messages from toggling
/// playback while a pan gesture owns Space.
/// </summary>
public sealed class WindowsEditorSpacePanGestureState
{
    public bool IsArmed { get; private set; }

    public WindowsEditorSpacePanKeyDownAction HandleKeyDown(
        Key key,
        KeyModifiers modifiers,
        bool proEditorEnabled,
        bool pointerOverTimeline)
    {
        if (key != Key.Space) return WindowsEditorSpacePanKeyDownAction.None;
        if (IsArmed) return WindowsEditorSpacePanKeyDownAction.SuppressRepeat;
        if (modifiers != KeyModifiers.None ||
            !proEditorEnabled ||
            !pointerOverTimeline)
            return WindowsEditorSpacePanKeyDownAction.None;

        IsArmed = true;
        return WindowsEditorSpacePanKeyDownAction.Arm;
    }

    public WindowsEditorSpacePanKeyUpAction HandleKeyUp(Key key, bool panned)
    {
        if (key != Key.Space || !IsArmed)
            return WindowsEditorSpacePanKeyUpAction.None;
        IsArmed = false;
        return panned
            ? WindowsEditorSpacePanKeyUpAction.ConsumedPan
            : WindowsEditorSpacePanKeyUpAction.TogglePlayback;
    }

    public bool Cancel()
    {
        var wasArmed = IsArmed;
        IsArmed = false;
        return wasArmed;
    }
}

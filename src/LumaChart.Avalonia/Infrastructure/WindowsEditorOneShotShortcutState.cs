using Avalonia.Input;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Avalonia does not expose a platform-independent repeat flag for key events.
/// Keep one-shot commands latched until their matching key-up arrives.
/// </summary>
public sealed class WindowsEditorOneShotShortcutState
{
    private readonly HashSet<Key> pressedKeys = [];

    public bool TryPress(Key key) => pressedKeys.Add(key);

    public bool Release(Key key) => pressedKeys.Remove(key);

    public void Clear() => pressedKeys.Clear();
}

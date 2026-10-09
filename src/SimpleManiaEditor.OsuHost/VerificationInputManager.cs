using osu.Framework.Input.Handlers;
using osu.Framework.Input.StateChanges;
using osu.Framework.Platform;
using osu.Framework.Input;
using osuTK.Input;
using System.Collections.Immutable;

namespace SimpleManiaEditor.OsuHost;

/// <summary>Only --verify hosts inject keys; they traverse the official input queue.</summary>
internal partial class VerificationInputManager(Func<Key, bool> invoke) : PracticeInputManager(invoke)
{
    private readonly VerificationKeyboard keyboard = new();
    protected override ImmutableArray<InputHandler> InputHandlers => base.InputHandlers.Add(keyboard);
    internal void Press(Key key) => keyboard.Enqueue(new KeyboardKeyInput(key, true));
    internal void Release(Key key) => keyboard.Enqueue(new KeyboardKeyInput(key, false));
    internal void Stroke(Key key) { Press(key); Release(key); }

    private sealed class VerificationKeyboard : InputHandler
    {
        public override bool Initialize(GameHost host) => true;
        public override bool IsActive => true;
        internal void Enqueue(IInput input) => PendingInputs.Enqueue(input);
    }
}

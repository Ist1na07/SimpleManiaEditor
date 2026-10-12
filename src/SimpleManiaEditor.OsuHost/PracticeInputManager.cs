using osu.Framework.Bindables;
using osu.Framework.Input;
using osu.Framework.Input.StateChanges;
using osu.Framework.Input.StateChanges.Events;
using osu.Game.Screens.Play;
using osuTK.Input;

namespace SimpleManiaEditor.OsuHost;

/// <summary>Intercept practice keys before global bindings, focus and pause overlays.
/// Other input follows the upstream OsuUserInputManager behaviour (MIT).</summary>
internal partial class PracticeInputManager(Func<Key, bool> invoke) : UserInputManager
{
    private readonly HashSet<Key> intercepted = [];
    internal bool IsIntercepted(Key key) => intercepted.Contains(key);
    internal int ForwardedSpacePresses { get; private set; }
    internal readonly IBindable<LocalUserPlayingState> PlayingState = new Bindable<LocalUserPlayingState>();
    protected override bool AllowRightClickFromLongTouch => PlayingState.Value != LocalUserPlayingState.Playing;
    protected override void HandleKeyboardKeyStateChange(ButtonStateChangeEvent<Key> change)
    {
        if (change.Kind == ButtonStateChangeKind.Pressed && intercepted.Contains(change.Button)) return;
        if (change.Kind == ButtonStateChangeKind.Pressed && invoke(change.Button))
        { intercepted.Add(change.Button); return; }
        if (change.Kind == ButtonStateChangeKind.Released && intercepted.Remove(change.Button)) return;
        if (change.Kind == ButtonStateChangeKind.Pressed && change.Button == Key.Space) ForwardedSpacePresses++;
        base.HandleKeyboardKeyStateChange(change);
    }
    protected override MouseButtonEventManager CreateButtonEventManagerFor(MouseButton button) =>
        button == MouseButton.Right ? new RightMouseManager(button) : base.CreateButtonEventManagerFor(button);
    private sealed class RightMouseManager(MouseButton button) : MouseButtonEventManager(button)
    {
        public override bool EnableDrag => true;
        public override bool EnableClick => false;
        public override bool ChangeFocusOnClick => false;
    }
}

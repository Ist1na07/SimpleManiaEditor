using osu.Framework.Graphics;
using osu.Framework.Input.Events;
using osuTK.Input;

namespace SimpleManiaEditor.OsuHost;

/// <summary>Practice commands stay above replay/ruleset input and official pause overlays.</summary>
internal partial class PracticeShortcutHandler(Func<Key, bool> invoke) : Drawable
{
    public override bool HandleNonPositionalInput => true;
    public override bool HandlePositionalInput => false;
    protected override bool OnKeyDown(KeyDownEvent e) => !e.Repeat && invoke(e.Key);
}

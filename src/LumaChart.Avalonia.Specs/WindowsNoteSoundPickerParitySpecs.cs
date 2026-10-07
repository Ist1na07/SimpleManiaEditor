using Avalonia.Input;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsNoteSoundPickerParitySpecs
{
    public static IReadOnlyList<WindowsNoteSoundPickerParitySpecResult> Run()
    {
        var results = new List<WindowsNoteSoundPickerParitySpecResult>();
        Check(results, "note-sound-picker-geometry-and-colors-match-wpf-110", () =>
        {
            Require(WindowsNoteSoundPickerParityPolicy.Width == 330 &&
                    WindowsNoteSoundPickerParityPolicy.MaxHeight == 370 &&
                    WindowsNoteSoundPickerParityPolicy.Padding == 9 &&
                    WindowsNoteSoundPickerParityPolicy.CornerRadius == 6 &&
                    WindowsNoteSoundPickerParityPolicy.BorderThickness == 1 &&
                    WindowsNoteSoundPickerParityPolicy.VerticalOffset == 5 &&
                    WindowsNoteSoundPickerParityPolicy.ScrollMaxHeight == 250,
                "Note Sound picker geometry drifted from WPF 1.1.0.");
            Require(WindowsNoteSoundPickerParityPolicy.Background == "#181D20" &&
                    WindowsNoteSoundPickerParityPolicy.Border == "#4A5A60" &&
                    WindowsNoteSoundPickerParityPolicy.Item == "#DDE3E4" &&
                    WindowsNoteSoundPickerParityPolicy.ClearGlyph == "#D69A8D" &&
                    WindowsNoteSoundPickerParityPolicy.SoundGlyph == "#83AEB1",
                "Note Sound picker palette drifted from WPF 1.1.0.");
            return "surface=330x370; padding=9; scroll=250; offset=5";
        });
        Check(results, "note-inspector-tab-order-matches-wpf-110", () =>
        {
            string[] expected =
            [
                "Inspector.Note.Beat",
                "Inspector.Note.EndBeat",
                "Inspector.Note.Lane",
                "Inspector.Note.Volume",
                "Inspector.Note.Sound",
                "Inspector.Note.SoundExisting",
                "Inspector.Note.SoundSelect",
                "Inspector.Note.Group",
                "Inspector.Note.Apply",
                "Inspector.Note.Delete"
            ];
            Require(
                WindowsInspectorParityPolicy.Wpf110NoteFocusOrder
                    .SequenceEqual(expected, StringComparer.Ordinal),
                "Note inspector Tab order drifted from WPF 1.1.0.");
            Require(expected
                    .Select(WindowsInspectorParityPolicy.ResolveNoteTabIndex)
                    .SequenceEqual(Enumerable.Range(0, expected.Length)),
                "Note inspector Tab indices are not unique and contiguous.");
            Require(
                WindowsInspectorParityPolicy
                    .ResolveNoteTabIndex("Inspector.Note.Unknown") == -1,
                "Unknown Note inspector controls unexpectedly entered Tab order.");
            return string.Join(" -> ", expected);
        });
        Check(results, "note-sound-picker-declarative-tab-navigation-cycles", () =>
        {
            Require(
                WindowsNoteSoundPickerParityPolicy.PopupTabNavigationMode ==
                KeyboardNavigationMode.Cycle,
                "Popup declarative Tab navigation no longer cycles like WPF 1.1.0.");
            return "KeyboardNavigation.TabNavigation=Cycle";
        });
        Check(results, "note-sound-picker-tab-cycles-forward-and-backward", () =>
        {
            var forward = Resolve(Key.Tab, KeyModifiers.None, 2, 3);
            var backward = Resolve(Key.Tab, KeyModifiers.Shift, 0, 3);
            Require(forward.Action == WindowsNoteSoundPickerNavigationAction.Focus &&
                    forward.TargetIndex == 0 &&
                    backward.Action == WindowsNoteSoundPickerNavigationAction.Focus &&
                    backward.TargetIndex == 2,
                "Tab or Shift+Tab did not wrap inside the popup.");
            return $"forward={forward.TargetIndex}; backward={backward.TargetIndex}";
        });
        Check(results, "note-sound-picker-arrows-home-and-end-stay-inside-popup", () =>
        {
            var down = Resolve(Key.Down, KeyModifiers.None, 2, 3);
            var up = Resolve(Key.Up, KeyModifiers.None, 0, 3);
            var home = Resolve(Key.Home, KeyModifiers.None, 2, 3);
            var end = Resolve(Key.End, KeyModifiers.None, 0, 3);
            Require(down.TargetIndex == 0 && up.TargetIndex == 2 &&
                    home.TargetIndex == 0 && end.TargetIndex == 2,
                "Directional navigation escaped or selected the wrong popup item.");
            return $"down={down.TargetIndex}; up={up.TargetIndex}; home={home.TargetIndex}; end={end.TargetIndex}";
        });
        Check(results, "note-sound-picker-unfocused-navigation-enters-deterministically", () =>
        {
            var forward = Resolve(Key.Tab, KeyModifiers.None, -1, 4);
            var backward = Resolve(Key.Tab, KeyModifiers.Shift, -1, 4);
            Require(forward.TargetIndex == 0 && backward.TargetIndex == 3,
                "Popup entry navigation did not choose the first/last item.");
            return $"forward={forward.TargetIndex}; backward={backward.TargetIndex}";
        });
        Check(results, "note-sound-picker-escape-always-closes-and-restores-entry-focus", () =>
        {
            var nonEmpty = Resolve(Key.Escape, KeyModifiers.None, 1, 3);
            var empty = Resolve(Key.Escape, KeyModifiers.None, -1, 0);
            Require(nonEmpty.Action ==
                    WindowsNoteSoundPickerNavigationAction.CloseAndRestore &&
                    empty.Action ==
                    WindowsNoteSoundPickerNavigationAction.CloseAndRestore &&
                    nonEmpty.TargetIndex == -1 && empty.TargetIndex == -1,
                "Escape did not resolve to close-and-restore in every popup state.");
            return "nonEmpty=close-and-restore; empty=close-and-restore";
        });
        Check(results, "note-sound-picker-does-not-steal-modified-or-unrelated-keys", () =>
        {
            var modifiedTab = Resolve(Key.Tab, KeyModifiers.Control, 0, 3);
            var modifiedArrow = Resolve(Key.Down, KeyModifiers.Shift, 0, 3);
            var letter = Resolve(Key.Q, KeyModifiers.None, 0, 3);
            Require(modifiedTab.Action == WindowsNoteSoundPickerNavigationAction.None &&
                    modifiedArrow.Action == WindowsNoteSoundPickerNavigationAction.None &&
                    letter.Action == WindowsNoteSoundPickerNavigationAction.None,
                "Popup navigation intercepted a modified or unrelated key.");
            return "Ctrl+Tab=None; Shift+Down=None; Q=None";
        });
        return results;
    }

    private static WindowsNoteSoundPickerNavigationResult Resolve(
        Key key,
        KeyModifiers modifiers,
        int focusedIndex,
        int count) =>
        WindowsNoteSoundPickerParityPolicy.ResolveNavigation(
            key,
            modifiers,
            focusedIndex,
            count);

    private static void Check(
        ICollection<WindowsNoteSoundPickerParitySpecResult> results,
        string name,
        Func<string> verify)
    {
        try
        {
            results.Add(new WindowsNoteSoundPickerParitySpecResult(
                name,
                true,
                verify()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsNoteSoundPickerParitySpecResult(
                name,
                false,
                exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsNoteSoundPickerParitySpecResult(
    string Name,
    bool Passed,
    string Detail);

using Avalonia.Input;

namespace LumaChart.Avalonia.Infrastructure;

public enum EditorInteractionProfile { OsuMania, LumaLegacy }

public static class EditorInteractionPolicy
{
    public static bool AdditiveSelection(EditorInteractionProfile profile, KeyModifiers modifiers) =>
        modifiers.HasFlag(profile == EditorInteractionProfile.OsuMania ? KeyModifiers.Control : KeyModifiers.Shift);

    public static bool PreserveDragBatch(EditorInteractionProfile profile, KeyModifiers modifiers, bool alreadySelected) =>
        profile == EditorInteractionProfile.OsuMania ? alreadySelected : modifiers.HasFlag(KeyModifiers.Control);

    public static bool ZoomWheel(EditorInteractionProfile profile, KeyModifiers modifiers) =>
        modifiers.HasFlag(profile == EditorInteractionProfile.OsuMania ? KeyModifiers.Alt : KeyModifiers.Control);
}

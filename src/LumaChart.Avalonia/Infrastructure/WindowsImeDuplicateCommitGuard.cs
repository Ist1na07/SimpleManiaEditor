using Avalonia.Input;

namespace LumaChart.Avalonia.Infrastructure;

public sealed class WindowsImeDuplicateCommitGuard
{
    private const long CompositionActivityWindowMilliseconds = 5_000;
    private const long DuplicateReplayWindowMilliseconds = 750;

    private long lastCompositionKeyDownMilliseconds = long.MinValue;
    private long shiftCommitKeyDownMilliseconds = long.MinValue;
    private object? commitTarget;
    private string committedText = string.Empty;
    private int replayIndex;

    public void ObserveKeyDown(
        Key reportedKey,
        KeyModifiers modifiers,
        bool textEditing,
        long timestampMilliseconds)
    {
        if (!textEditing)
        {
            Reset();
            return;
        }
        if (reportedKey != Key.ImeProcessed) return;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            if (IsRecent(
                    lastCompositionKeyDownMilliseconds,
                    timestampMilliseconds,
                    CompositionActivityWindowMilliseconds))
            {
                shiftCommitKeyDownMilliseconds = timestampMilliseconds;
                lastCompositionKeyDownMilliseconds = long.MinValue;
                ClearReplay();
            }
            return;
        }
        if (!modifiers.HasFlag(KeyModifiers.Control) &&
            !modifiers.HasFlag(KeyModifiers.Alt) &&
            !modifiers.HasFlag(KeyModifiers.Meta))
            lastCompositionKeyDownMilliseconds = timestampMilliseconds;
    }

    public bool ShouldSuppressReplay(
        object? target,
        string? text,
        long timestampMilliseconds)
    {
        if (target == null || string.IsNullOrEmpty(text) ||
            !IsRecent(
                shiftCommitKeyDownMilliseconds,
                timestampMilliseconds,
                DuplicateReplayWindowMilliseconds))
        {
            Reset();
            return false;
        }

        if (string.IsNullOrEmpty(committedText))
        {
            commitTarget = target;
            committedText = text;
            replayIndex = 0;
            return false;
        }

        if (!ReferenceEquals(commitTarget, target) ||
            text.Length != 1 ||
            replayIndex >= committedText.Length ||
            text[0] != committedText[replayIndex])
        {
            Reset();
            return false;
        }

        replayIndex++;
        if (replayIndex == committedText.Length) Reset();
        return true;
    }

    public void Reset()
    {
        lastCompositionKeyDownMilliseconds = long.MinValue;
        shiftCommitKeyDownMilliseconds = long.MinValue;
        ClearReplay();
    }

    private void ClearReplay()
    {
        commitTarget = null;
        committedText = string.Empty;
        replayIndex = 0;
    }

    private static bool IsRecent(long earlier, long now, long window) =>
        earlier != long.MinValue && now >= earlier && now - earlier <= window;
}

using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

/// <summary>
/// Complete Timing Edit payload matching the Windows/WPF editor. BPM/delay and
/// one effect may coexist in the same timing row, and the mapper-authored JSON
/// number text is retained exactly after surrounding whitespace is removed.
/// </summary>
public sealed record CompleteTimingEdit(
    BeatPosition Beat,
    double? Bpm,
    double Delay,
    TimingEffectKind? Effect,
    string EffectValueText = "1.0",
    string CustomFieldName = "")
{
    public static CompleteTimingEdit FromTiming(TimingEvent timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        return new CompleteTimingEdit(
            timing.Beat,
            timing.Bpm,
            timing.Delay,
            timing.Effect,
            string.IsNullOrWhiteSpace(timing.EffectValueText)
                ? timing.EffectValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : timing.EffectValueText,
            timing.EffectFieldName);
    }
}

/// <summary>
/// UI-independent Timing Edit command boundary. It deliberately does not snap
/// <see cref="CompleteTimingEdit.Beat"/>: WPF accepts the exact rational Beat
/// typed by the mapper and persists that authored representation.
/// </summary>
public static class CompleteTimingEditingService
{
    public static Guid Add(EditorSession session, CompleteTimingEdit edit)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edit);
        var id = Guid.NewGuid();
        var timing = Create(id, edit);
        EnsureTimingEditAllowed(session, timing.Beat);
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Timing.Add(timing));
        return session.ChangeSerial != serial &&
               session.Document.Timing.Any(candidate => candidate.Id == id)
            ? id
            : Guid.Empty;
    }

    public static bool Update(EditorSession session, Guid timingId, CompleteTimingEdit edit)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edit);
        var existing = session.Document.Timing.FirstOrDefault(timing => timing.Id == timingId);
        if (existing == null) return false;

        var replacement = Create(timingId, edit);
        EnsureTimingEditAllowed(session, existing.Beat, replacement.Beat);
        replacement.UnknownData = existing.UnknownData?.DeepClone().AsObject();
        replacement.OnlineCreatorUserId = existing.OnlineCreatorUserId;
        var serial = session.ChangeSerial;
        session.Mutate(document => ReplaceTimingRow(document, timingId, replacement));
        return session.ChangeSerial != serial;
    }

    /// <summary>
    /// Replaces one timing row. ChartDocument.Normalize keeps only the last BPM
    /// record at a beat, so a BPM row edited onto another BPM row's beat moves
    /// to the end: the value the mapper just typed wins instead of being merged
    /// away by an older row that happens to precede it in the list.
    /// </summary>
    internal static void ReplaceTimingRow(ChartDocument document, Guid timingId, TimingEvent replacement)
    {
        var index = document.Timing.FindIndex(timing => timing.Id == timingId);
        if (index < 0) return;
        var overridesBpm = replacement.Bpm.HasValue && document.Timing.Any(timing =>
            timing.Id != timingId && timing.Bpm.HasValue && timing.Beat.CompareTo(replacement.Beat) == 0);
        if (!overridesBpm)
        {
            document.Timing[index] = replacement;
            return;
        }
        document.Timing.RemoveAt(index);
        document.Timing.Add(replacement);
    }

    public static bool Delete(EditorSession session, Guid timingId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var existing = session.Document.Timing.FirstOrDefault(timing => timing.Id == timingId);
        if (existing == null) return false;
        EnsureTimingEditAllowed(session, existing.Beat);
        var serial = session.ChangeSerial;
        session.Mutate(document => document.Timing.RemoveAll(timing => timing.Id == timingId));
        return session.ChangeSerial != serial;
    }

    public static TimingEvent Create(Guid id, CompleteTimingEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var beat = edit.Beat.Denominator == 0 ? BeatPosition.Zero : edit.Beat;
        if (beat.CompareTo(BeatPosition.Zero) < 0)
            throw new ArgumentOutOfRangeException(nameof(edit), "Timing beat cannot be negative.");
        // Same range the document keeps (fast Malody/osu segments are real data),
        // so an imported row above 10000 BPM can still be edited.
        if (edit.Bpm.HasValue &&
            (!double.IsFinite(edit.Bpm.Value) || edit.Bpm.Value <= 0 || edit.Bpm.Value > ChartDocument.MaximumTimingBpm))
            throw new ArgumentOutOfRangeException(nameof(edit), "BPM must be finite, positive and within the supported timing range.");
        if (!double.IsFinite(edit.Delay))
            throw new ArgumentOutOfRangeException(nameof(edit), "Delay must be finite.");
        if (edit.Effect.HasValue && !Enum.IsDefined(edit.Effect.Value))
            throw new ArgumentOutOfRangeException(nameof(edit), "The effect kind is not supported.");
        if (!edit.Bpm.HasValue && !edit.Effect.HasValue)
            throw new ArgumentException("A timing row must contain BPM or an effect.", nameof(edit));

        var valueText = edit.EffectValueText?.Trim() ?? string.Empty;
        if (!EffectNumberText.TryParse(valueText, out var effectValue))
            throw new ArgumentException(
                "Effect value must be a finite JSON number such as 1, 1.0, -0.25 or 2e-3.",
                nameof(edit));

        var customFieldName = string.Empty;
        if (edit.Effect == TimingEffectKind.Custom)
        {
            customFieldName = edit.CustomFieldName?.Trim() ?? string.Empty;
            if (!IsValidCustomFieldName(customFieldName))
                throw new ArgumentException(
                    "Custom effect name must start with an ASCII letter or '_' and contain only ASCII letters, digits, '.', '_' or '-'; beat/endbeat are reserved.",
                    nameof(edit));
        }

        return new TimingEvent
        {
            Id = id,
            Beat = beat,
            Bpm = edit.Bpm,
            Delay = edit.Delay,
            Effect = edit.Effect,
            EffectValue = effectValue,
            EffectValueText = valueText,
            EffectFieldName = customFieldName
        };
    }

    public static bool IsValidCustomFieldName(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length is < 1 or > 64 ||
            candidate.Equals("beat", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("endbeat", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!IsAsciiLetter(candidate[0]) && candidate[0] != '_') return false;
        return candidate.Skip(1).All(character =>
            IsAsciiLetter(character) ||
            char.IsAsciiDigit(character) ||
            character is '_' or '.' or '-');
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static void EnsureTimingEditAllowed(EditorSession session, params BeatPosition[] beats)
    {
        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能编辑 Timing / Effect。");
        if (!session.CanEditTiming)
            throw new InvalidOperationException("当前没有编辑 Timing / Effect 的权限。");
        if (!session.AllowOwnerProtectedEdits && beats.Any(session.IsBeatOwnerProtected))
            throw new InvalidOperationException(
                "该操作触及房主锁定选区，成员不能在其中新增、修改、移动或删除 Timing / Effect。");
    }
}

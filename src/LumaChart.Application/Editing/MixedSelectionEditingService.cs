using LumaChart.Core.Models;

namespace LumaChart.Application.Editing;

/// <summary>
/// The exact Effect payload retained by the Windows mixed-selection clipboard.
/// BPM, Delay, authored number text, custom field names, unknown JSON and runtime
/// creator identity are deliberately not part of this clipboard contract.
/// </summary>
public readonly record struct MixedEffectClipboardItem(
    BeatPosition Beat,
    TimingEffectKind Effect,
    double EffectValue);

public sealed class MixedSelectionClipboard
{
    private readonly ChartNote[] notes;
    private readonly MixedEffectClipboardItem[] effects;
    private readonly IReadOnlyList<MixedEffectClipboardItem> readOnlyEffects;

    internal MixedSelectionClipboard(
        IEnumerable<ChartNote> notes,
        IEnumerable<MixedEffectClipboardItem> effects)
    {
        this.notes = notes.Select(note => note.Clone()).ToArray();
        this.effects = effects.ToArray();
        readOnlyEffects = Array.AsReadOnly(this.effects);
    }

    public int NoteCount => notes.Length;
    public int EffectCount => effects.Length;
    public bool IsEmpty => notes.Length == 0 && effects.Length == 0;
    public IReadOnlyList<MixedEffectClipboardItem> Effects => readOnlyEffects;

    public IReadOnlyList<ChartNote> SnapshotNotes() =>
        notes.Select(note => note.Clone()).ToArray();

    public MixedSelectionClipboard EffectsOnly() => new([], effects);

    internal IReadOnlyList<ChartNote> Notes => notes;
}

public sealed record MixedSelectionDeleteResult(
    IReadOnlyList<Guid> DeletedNoteIds,
    IReadOnlyList<Guid> DeletedTimingIds)
{
    public int DeletedNoteCount => DeletedNoteIds.Count;
    public int DeletedEffectCount => DeletedTimingIds.Count;
    public bool Applied => DeletedNoteCount > 0 || DeletedEffectCount > 0;
}

public sealed record MixedSelectionPasteResult(
    IReadOnlyList<Guid> NoteIds,
    IReadOnlyList<Guid> TimingIds)
{
    public int NoteCount => NoteIds.Count;
    public int EffectCount => TimingIds.Count;
    public bool Applied => NoteCount > 0 || EffectCount > 0;
}

/// <summary>
/// UI-independent Note + Effect selection commands matching the Windows editor.
/// Source locks and hidden Note groups are filters for Copy/Delete. Paste is an
/// atomic placement command: one forbidden target or missing edit permission
/// rejects the complete mixed payload before the single session mutation.
/// </summary>
public static class MixedSelectionEditingService
{
    public static MixedSelectionClipboard Copy(
        EditorSession session,
        IEnumerable<Guid> noteIds,
        IEnumerable<Guid> timingIds,
        IReadOnlySet<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var notes = SelectCommandNotes(session, noteIds, hiddenGroups)
            .Select(note => note.Clone())
            .ToArray();
        var effects = SelectCommandEffects(session, timingIds)
            .OrderBy(timing => timing.Beat)
            .Select(timing => new MixedEffectClipboardItem(
                timing.Beat,
                timing.Effect!.Value,
                timing.EffectValue))
            .ToArray();
        return new MixedSelectionClipboard(notes, effects);
    }

    public static MixedSelectionDeleteResult Delete(
        EditorSession session,
        IEnumerable<Guid> noteIds,
        IEnumerable<Guid> timingIds,
        IReadOnlySet<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var notes = SelectCommandNotes(session, noteIds, hiddenGroups).ToArray();
        var effects = SelectCommandEffects(session, timingIds).ToArray();
        if (notes.Length == 0 && effects.Length == 0)
            return new MixedSelectionDeleteResult([], []);

        EnsureEditAllowed(session, notes.Length > 0, effects.Length > 0);
        var selectedNoteIds = notes.Select(note => note.Id).ToHashSet();
        var selectedTimingIds = effects.Select(timing => timing.Id).ToHashSet();
        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            document.Notes.RemoveAll(note => selectedNoteIds.Contains(note.Id));
            document.Timing.RemoveAll(timing => selectedTimingIds.Contains(timing.Id));
        });
        if (session.ChangeSerial == serial)
            return new MixedSelectionDeleteResult([], []);

        session.Selection.ExceptWith(selectedNoteIds);
        return new MixedSelectionDeleteResult(
            notes.Select(note => note.Id).ToArray(),
            effects.Select(timing => timing.Id).ToArray());
    }

    public static MixedSelectionPasteResult Paste(
        EditorSession session,
        MixedSelectionClipboard clipboard,
        BeatPosition redLineBeat,
        IReadOnlySet<int>? hiddenGroups = null,
        Func<ChartNote, bool>? notePreflight = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clipboard);
        var sourceNotes = clipboard.Notes
            .Where(note => hiddenGroups == null || !hiddenGroups.Contains(note.Group))
            .ToArray();
        var sourceEffects = clipboard.Effects.ToArray();
        if (sourceNotes.Length == 0 && sourceEffects.Length == 0)
            return new MixedSelectionPasteResult([], []);
        if (notePreflight != null && sourceNotes.Any(note => !notePreflight(note.Clone())))
            throw new InvalidOperationException("剪贴板包含当前目标不允许引用的 Note 资源。");

        var division = Math.Clamp(session.SnapDivision, 1, 1920);
        var targetBeat = redLineBeat.Denominator == 0 ? BeatPosition.Zero : redLineBeat;
        targetBeat = targetBeat.Snap(division);
        if (targetBeat.CompareTo(BeatPosition.Zero) < 0) targetBeat = BeatPosition.Zero.Snap(division);
        var minimumBeat = sourceNotes.Select(note => note.Beat)
            .Concat(sourceEffects.Select(effect => effect.Beat))
            .Aggregate((left, right) => left.CompareTo(right) <= 0 ? left : right);

        // Only the shared earliest Beat is aligned to the snapped red line. Every
        // other Note/Effect keeps its exact rational distance from it; snapping
        // each object separately bent triplets and 1/16 figures.
        var notes = Deduplicate(
            session.Document,
            sourceNotes.Select(source => CreatePastedNote(session, source, targetBeat, minimumBeat)));
        var existingEffects = session.Document.Timing
            .Where(timing => timing.Effect.HasValue)
            .Select(EffectKey)
            .ToHashSet();
        var effects = sourceEffects.Select(source => new TimingEvent
            {
                Id = Guid.NewGuid(),
                Beat = BasicChartEditingService.OffsetExact(targetBeat, source.Beat, minimumBeat),
                Effect = source.Effect,
                EffectValue = source.EffectValue
            })
            // Pasting the same Effect onto itself would stack identical rows.
            .Where(effect => existingEffects.Add(EffectKey(effect)))
            .ToArray();

        if (!session.AllowOwnerProtectedEdits)
        {
            var noteHitsLock = notes.Any(note =>
                session.IntersectsOwnerProtectedRegion(note.Beat, note.EndBeat ?? note.Beat));
            var timingHitsLock = effects.Any(session.IsTimingOwnerProtected);
            if (noteHitsLock || timingHitsLock)
                throw new InvalidOperationException("粘贴内容与房主锁定选区相交，成员不能放置。");
        }

        EnsureEditAllowed(session, notes.Length > 0, effects.Length > 0);
        if (notes.Length == 0 && effects.Length == 0)
            return new MixedSelectionPasteResult([], []);
        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            document.Notes.AddRange(notes);
            document.Timing.AddRange(effects);
        });
        if (session.ChangeSerial == serial)
            return new MixedSelectionPasteResult([], []);

        session.Selection.Clear();
        session.Selection.UnionWith(notes.Select(note => note.Id));
        return new MixedSelectionPasteResult(
            notes.Select(note => note.Id).ToArray(),
            effects.Select(effect => effect.Id).ToArray());
    }

    private static IEnumerable<ChartNote> SelectCommandNotes(
        EditorSession session,
        IEnumerable<Guid> noteIds,
        IReadOnlySet<int>? hiddenGroups)
    {
        ArgumentNullException.ThrowIfNull(noteIds);
        var requested = noteIds.ToHashSet();
        return session.Document.Notes.Where(note =>
            requested.Contains(note.Id) &&
            (hiddenGroups == null || !hiddenGroups.Contains(note.Group)) &&
            (session.AllowOwnerProtectedEdits || !session.IsOwnerProtected(note)));
    }

    private static IEnumerable<TimingEvent> SelectCommandEffects(
        EditorSession session,
        IEnumerable<Guid> timingIds)
    {
        ArgumentNullException.ThrowIfNull(timingIds);
        var requested = timingIds.ToHashSet();
        return session.Document.Timing.Where(timing =>
            requested.Contains(timing.Id) &&
            timing.Effect.HasValue &&
            (session.AllowOwnerProtectedEdits || !session.IsTimingOwnerProtected(timing)));
    }

    private static ChartNote CreatePastedNote(
        EditorSession session,
        ChartNote source,
        BeatPosition targetBeat,
        BeatPosition minimumBeat)
    {
        var note = source.Clone();
        note.Id = Guid.NewGuid();
        note.Beat = BasicChartEditingService.OffsetExact(targetBeat, source.Beat, minimumBeat);
        if (!note.IsDraft && note.Lane >= session.Document.KeyCount)
        {
            note.IsDraft = true;
            note.DraftColumn = note.Lane;
            note.Lane = 0;
        }
        if (source.EndBeat.HasValue)
        {
            note.EndBeat = BasicChartEditingService.OffsetExact(targetBeat, source.EndBeat.Value, minimumBeat);
            if (note.EndBeat.Value.CompareTo(note.Beat) <= 0)
                note.EndBeat = note.Beat + 1d / Math.Clamp(session.SnapDivision, 1, 1920);
        }
        return note;
    }

    /// <summary>
    /// Drops pasted Notes that would stack exactly on an existing Note (or on
    /// another pasted Note) in the same lane / draft column. Stacked copies are
    /// invisible in the editor and only surface later as overlap errors.
    /// </summary>
    private static ChartNote[] Deduplicate(ChartDocument document, IEnumerable<ChartNote> pasted)
    {
        var occupied = document.Notes.Select(NoteKey).ToHashSet();
        return pasted.Where(note => occupied.Add(NoteKey(note))).ToArray();
    }

    private static (bool IsDraft, int Column, BeatPosition Beat) NoteKey(ChartNote note) =>
        (note.IsDraft, note.IsDraft ? note.DraftColumn : note.Lane, note.Beat);

    private static (BeatPosition Beat, TimingEffectKind? Effect, double Value, string Name) EffectKey(TimingEvent timing) =>
        (timing.Beat, timing.Effect, timing.EffectValue, timing.EffectFieldName ?? string.Empty);

    private static void EnsureEditAllowed(EditorSession session, bool editsNotes, bool editsTiming)
    {
        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能执行该编辑命令。");
        if (editsNotes && !session.CanEditNotes)
            throw new InvalidOperationException("当前没有编辑 Note 的权限。");
        if (editsTiming && !session.CanEditTiming)
            throw new InvalidOperationException("当前没有编辑 Timing / Effect 的权限。");
    }
}

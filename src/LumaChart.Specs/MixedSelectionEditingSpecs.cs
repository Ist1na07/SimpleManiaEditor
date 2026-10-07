using System.Text.Json.Nodes;
using LumaChart.Application.Editing;
using LumaChart.Core.Models;

internal static class MixedSelectionEditingSpecs
{
    public static void Run()
    {
        TestTimingBatchMove();
        TestTimingBatchMoveGuardsAndFiltering();
        TestTimingBatchDelete();
        TestMixedCopyPasteAlignmentAndFields();
        TestMixedDeleteFilteringAndPermissions();
        TestPasteOwnerLockBoundaries();
    }

    private static void TestTimingBatchMove()
    {
        var anchorId = Guid.NewGuid();
        var companionId = Guid.NewGuid();
        var session = new EditorSession { SnapDivision = 6 };
        session.ReplaceDocument(new ChartDocument
        {
            Timing =
            [
                new TimingEvent
                {
                    Id = anchorId,
                    Beat = BeatPosition.Preserve(0, 4, 6),
                    Bpm = 150,
                    Delay = 12.5,
                    Effect = TimingEffectKind.Custom,
                    EffectValue = .125,
                    EffectValueText = "0.125",
                    EffectFieldName = "camera_zoom",
                    UnknownData = JsonNode.Parse("{\"move\":true}")!.AsObject(),
                    OnlineCreatorUserId = 77
                },
                new TimingEvent
                {
                    Id = companionId,
                    Beat = BeatPosition.Preserve(1, 1, 6),
                    Effect = TimingEffectKind.Scroll,
                    EffectValue = 1.5,
                    EffectValueText = "1.5"
                }
            ]
        });

        var noOp = TimingBatchMoveService.MoveTo(
            session,
            [anchorId, companionId],
            anchorId,
            BeatPosition.Preserve(0, 8, 12));
        Assert(!noOp.Applied && noOp.MovableTimingIds.Count == 2 &&
               session.ChangeSerial == 0 &&
               session.Document.Timing.Single(timing => timing.Id == anchorId).Beat is
                   { Bar: 0, Numerator: 4, Denominator: 6 },
            "same-value Timing movement rewrote the authored denominator or history");

        var result = TimingBatchMoveService.MoveTo(
            session,
            [anchorId, companionId],
            anchorId,
            BeatPosition.Preserve(1, 1, 12));
        var anchor = session.Document.Timing.Single(timing => timing.Id == anchorId);
        var companion = session.Document.Timing.Single(timing => timing.Id == companionId);
        Assert(result.Applied && result.MovedCount == 2 &&
               result.MovableTimingIds.ToHashSet().SetEquals([anchorId, companionId]) &&
               result.FilteredTimingIds.Count == 0 && session.ChangeSerial == 1 &&
               anchor.Beat is { Bar: 1, Numerator: 1, Denominator: 6 } &&
               companion.Beat is { Bar: 1, Numerator: 4, Denominator: 6 },
            "Timing batch movement did not snap every row to the current exact subdivision");
        Assert(anchor is
               {
                   Bpm: 150,
                   Delay: 12.5,
                   Effect: TimingEffectKind.Custom,
                   EffectValue: .125,
                   EffectValueText: "0.125",
                   EffectFieldName: "camera_zoom",
                   OnlineCreatorUserId: 77
               } && anchor.UnknownData?["move"]?.GetValue<bool>() == true,
            "Timing batch movement changed non-Beat fields");
        Assert(session.Undo() &&
               session.Document.Timing.Single(timing => timing.Id == anchorId).Beat is
                   { Bar: 0, Numerator: 4, Denominator: 6 } &&
               session.Document.Timing.Single(timing => timing.Id == companionId).Beat is
                   { Bar: 1, Numerator: 1, Denominator: 6 } &&
               !session.Undo(),
            "Timing batch movement did not create exactly one undo entry");
    }

    private static void TestTimingBatchMoveGuardsAndFiltering()
    {
        var anchorId = Guid.NewGuid();
        var filteredId = Guid.NewGuid();
        var companionId = Guid.NewGuid();
        var filteredSession = new EditorSession { SnapDivision = 4 };
        filteredSession.ReplaceDocument(new ChartDocument
        {
            Timing =
            [
                new TimingEvent { Id = anchorId, Beat = BeatPosition.Zero, Bpm = 120 },
                new TimingEvent
                {
                    Id = filteredId,
                    Beat = BeatPosition.Preserve(1, 3, 12),
                    Effect = TimingEffectKind.Scroll
                },
                new TimingEvent
                {
                    Id = companionId,
                    Beat = BeatPosition.Preserve(2, 0, 1),
                    Effect = TimingEffectKind.Jump
                }
            ]
        });
        filteredSession.AddOwnerProtectedRegion(
            BeatPosition.Preserve(1, 1, 4),
            BeatPosition.Preserve(1, 1, 2));
        filteredSession.AllowOwnerProtectedEdits = false;
        var filteredResult = TimingBatchMoveService.MoveTo(
            filteredSession,
            [anchorId, filteredId, companionId],
            anchorId,
            BeatPosition.Preserve(3, 0, 1));
        Assert(filteredResult.MovedCount == 2 &&
               filteredResult.MovableTimingIds.ToHashSet().SetEquals([anchorId, companionId]) &&
               filteredResult.FilteredTimingIds.SequenceEqual([filteredId]) &&
               filteredSession.Document.Timing.Single(timing => timing.Id == filteredId).Beat is
                   { Bar: 1, Numerator: 3, Denominator: 12 } &&
               filteredSession.Document.Timing.Single(timing => timing.Id == anchorId).Beat is
                   { Bar: 3, Numerator: 0, Denominator: 4 } &&
               filteredSession.Document.Timing.Single(timing => timing.Id == companionId).Beat is
                   { Bar: 5, Numerator: 0, Denominator: 4 },
            "Timing movement did not filter a non-anchor locked source exactly like WPF");
        var lockedAnchorSerial = filteredSession.ChangeSerial;
        Assert(Rejects(() => TimingBatchMoveService.MoveTo(
                   filteredSession,
                   [filteredId],
                   filteredId,
                   BeatPosition.Preserve(4, 0, 1))) &&
               filteredSession.ChangeSerial == lockedAnchorSerial &&
               filteredSession.Document.Timing.Single(timing => timing.Id == filteredId).Beat is
                   { Bar: 1, Numerator: 3, Denominator: 12 },
            "a locked Timing anchor was allowed to start a move");

        var targetId = Guid.NewGuid();
        var targetSession = new EditorSession { SnapDivision = 12 };
        targetSession.ReplaceDocument(new ChartDocument
        {
            Timing = [new TimingEvent { Id = targetId, Beat = BeatPosition.Zero, Bpm = 120 }]
        });
        targetSession.AddOwnerProtectedRegion(
            BeatPosition.Preserve(1, 3, 12),
            BeatPosition.Preserve(2, 0, 1));
        targetSession.AllowOwnerProtectedEdits = false;
        var targetSerial = targetSession.ChangeSerial;
        Assert(Rejects(() => TimingBatchMoveService.MoveTo(
                   targetSession,
                   [targetId],
                   targetId,
                   BeatPosition.Preserve(1, 1, 4))) &&
               targetSession.ChangeSerial == targetSerial &&
               targetSession.Document.Timing.Single().Beat == BeatPosition.Zero,
            "a Timing move into the inclusive lock start was not rejected atomically");
        var boundaryResult = TimingBatchMoveService.MoveTo(
            targetSession,
            [targetId],
            targetId,
            BeatPosition.Preserve(2, 0, 1));
        Assert(boundaryResult.Applied && targetSession.Document.Timing.Single().Beat is
               { Bar: 2, Numerator: 0, Denominator: 12 },
            "the exclusive owner-lock end incorrectly blocked Timing movement");

        var permissionId = Guid.NewGuid();
        var denied = new EditorSession { SnapDivision = 4, CanEditTiming = false };
        denied.ReplaceDocument(new ChartDocument
        {
            Timing = [new TimingEvent { Id = permissionId, Beat = BeatPosition.Zero, Bpm = 120 }]
        });
        Assert(Rejects(() => TimingBatchMoveService.MoveTo(
                   denied,
                   [permissionId],
                   permissionId,
                   BeatPosition.Preserve(1, 0, 1))) &&
               denied.ChangeSerial == 0 && !denied.IsDirty &&
               denied.Document.Timing.Single().Beat == BeatPosition.Zero,
            "CanEditTiming rejection changed Timing data or history");
        denied.CanEditTiming = true;
        denied.IsReadOnly = true;
        Assert(Rejects(() => TimingBatchMoveService.MoveTo(
                   denied,
                   [permissionId],
                   permissionId,
                   BeatPosition.Preserve(1, 0, 1))) &&
               denied.ChangeSerial == 0 && !denied.IsDirty,
            "read-only Timing movement changed data or history");
    }

    private static void TestMixedCopyPasteAlignmentAndFields()
    {
        var visibleNoteId = Guid.NewGuid();
        var hiddenNoteId = Guid.NewGuid();
        var protectedNoteId = Guid.NewGuid();
        var effectId = Guid.NewGuid();
        var bpmOnlyId = Guid.NewGuid();
        var protectedEffectId = Guid.NewGuid();
        var source = new EditorSession { SnapDivision = 12 };
        source.ReplaceDocument(new ChartDocument
        {
            KeyCount = 6,
            Notes =
            [
                new ChartNote
                {
                    Id = visibleNoteId,
                    Lane = 5,
                    Beat = BeatPosition.Preserve(2, 4, 12),
                    EndBeat = BeatPosition.Preserve(2, 10, 12),
                    Sound = "hit.wav",
                    Volume = 64,
                    Group = 1,
                    UnknownData = JsonNode.Parse("{\"note\":true}")!.AsObject(),
                    OnlineCreatorUserId = 91
                },
                new ChartNote
                {
                    Id = hiddenNoteId,
                    Lane = 1,
                    Beat = BeatPosition.Zero,
                    Group = 9
                },
                new ChartNote
                {
                    Id = protectedNoteId,
                    Lane = 2,
                    Beat = BeatPosition.Preserve(3, 1, 4),
                    Group = 1
                }
            ],
            Timing =
            [
                new TimingEvent
                {
                    Id = effectId,
                    Beat = BeatPosition.Preserve(1, 10, 12),
                    Bpm = 180,
                    Delay = 15,
                    Effect = TimingEffectKind.Custom,
                    EffectValue = .125,
                    EffectValueText = "1.25e-1",
                    EffectFieldName = "camera.zoom",
                    UnknownData = JsonNode.Parse("{\"effect\":true}")!.AsObject(),
                    OnlineCreatorUserId = 92
                },
                new TimingEvent
                {
                    Id = bpmOnlyId,
                    Beat = BeatPosition.Zero,
                    Bpm = 120
                },
                new TimingEvent
                {
                    Id = protectedEffectId,
                    Beat = BeatPosition.Preserve(3, 1, 4),
                    Effect = TimingEffectKind.Sign,
                    EffectValue = -1
                }
            ]
        });
        source.SetOwnerProtection([protectedNoteId], true);
        source.AddOwnerProtectedRegion(
            BeatPosition.Preserve(3, 0, 1),
            BeatPosition.Preserve(4, 0, 1));
        source.AllowOwnerProtectedEdits = false;

        var clipboard = MixedSelectionEditingService.Copy(
            source,
            [visibleNoteId, hiddenNoteId, protectedNoteId],
            [effectId, bpmOnlyId, protectedEffectId],
            new HashSet<int> { 9 });
        var copiedNote = clipboard.SnapshotNotes().Single();
        var copiedEffect = clipboard.Effects.Single();
        Assert(clipboard.NoteCount == 1 && clipboard.EffectCount == 1 &&
               copiedNote.Id == visibleNoteId && copiedNote.OnlineCreatorUserId == 91 &&
               copiedNote.UnknownData?["note"]?.GetValue<bool>() == true &&
               copiedEffect == new MixedEffectClipboardItem(
                   BeatPosition.Preserve(1, 10, 12),
                   TimingEffectKind.Custom,
                   .125),
            "mixed copy did not filter hidden/locked/BPM-only rows or preserve the WPF payload");

        var destination = new EditorSession { SnapDivision = 12 };
        destination.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
        });
        var paste = MixedSelectionEditingService.Paste(
            destination,
            clipboard,
            BeatPosition.Preserve(4, 3, 12),
            new HashSet<int> { 9 });
        var note = destination.Document.Notes.Single();
        var effect = destination.Document.Timing.Single(timing => timing.Effect.HasValue);
        Assert(paste.NoteCount == 1 && paste.EffectCount == 1 && destination.ChangeSerial == 1 &&
               note.Id != visibleNoteId && effect.Id != effectId &&
               note.Beat is { Bar: 4, Numerator: 9, Denominator: 12 } &&
               note.EndBeat is { Bar: 5, Numerator: 3, Denominator: 12 } &&
               effect.Beat is { Bar: 4, Numerator: 3, Denominator: 12 },
            "mixed paste did not align the shared earliest Note/Effect Beat to the red line");
        Assert(note is
               {
                   Lane: 0,
                   IsDraft: true,
                   DraftColumn: 5,
                   Sound: "hit.wav",
                   Volume: 64,
                   Group: 1,
                   OnlineCreatorUserId: 91
               } && note.UnknownData?["note"]?.GetValue<bool>() == true,
            "mixed Note paste changed WPF Clone fields or cleared runtime creator identity");
        Assert(effect is
               {
                   Bpm: null,
                   Delay: 0,
                   Effect: TimingEffectKind.Custom,
                   EffectValue: .125,
                   EffectValueText: "0.125",
                   EffectFieldName: "",
                   UnknownData: null,
                   OnlineCreatorUserId: 0
               },
            "mixed Effect paste was silently upgraded to TimingEvent.Clone semantics");
        Assert(destination.Selection.SetEquals(paste.NoteIds) &&
               destination.Undo() && destination.Document.Notes.Count == 0 &&
               destination.Document.Timing.All(timing => !timing.Effect.HasValue) &&
               !destination.Undo(),
            "mixed paste did not select new Notes or create exactly one undo entry");
    }

    private static void TestTimingBatchDelete()
    {
        var bpmId = Guid.NewGuid();
        var effectId = Guid.NewGuid();
        var lockedBpmId = Guid.NewGuid();
        var lockedEffectId = Guid.NewGuid();
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            Timing =
            [
                new TimingEvent { Id = bpmId, Beat = BeatPosition.Zero, Bpm = 120 },
                new TimingEvent
                {
                    Id = effectId,
                    Beat = BeatPosition.Preserve(1, 0, 1),
                    Effect = TimingEffectKind.Scroll
                },
                new TimingEvent
                {
                    Id = lockedBpmId,
                    Beat = BeatPosition.Preserve(2, 3, 12),
                    Bpm = 180
                },
                new TimingEvent
                {
                    Id = lockedEffectId,
                    Beat = BeatPosition.Preserve(2, 1, 2),
                    Effect = TimingEffectKind.Jump
                }
            ]
        });
        session.AddOwnerProtectedRegion(
            BeatPosition.Preserve(2, 1, 4),
            BeatPosition.Preserve(3, 0, 1));
        session.AllowOwnerProtectedEdits = false;
        var deleted = TimingBatchDeleteService.DeleteMany(
            session,
            [bpmId, effectId, lockedBpmId, lockedEffectId]);
        Assert(deleted.DeletedTimingIds.SequenceEqual([bpmId, effectId]) &&
               session.ChangeSerial == 1 &&
               session.Document.Timing.Select(timing => timing.Id).ToHashSet()
                   .SetEquals([lockedBpmId, lockedEffectId]),
            "Timing batch delete did not include BPM-only rows or filter locked rows");
        var allLockedSerial = session.ChangeSerial;
        var allLocked = TimingBatchDeleteService.DeleteMany(
            session,
            [lockedBpmId, lockedEffectId]);
        Assert(!allLocked.Applied && session.ChangeSerial == allLockedSerial,
            "an all-locked Timing delete created history instead of becoming a no-op");
        Assert(session.Undo() && session.Document.Timing.Count == 4 && !session.Undo(),
            "Timing batch delete did not create exactly one undo entry");

        var deniedId = Guid.NewGuid();
        var denied = new EditorSession { CanEditTiming = false };
        denied.ReplaceDocument(new ChartDocument
        {
            Timing = [new TimingEvent { Id = deniedId, Beat = BeatPosition.Zero, Bpm = 120 }]
        });
        Assert(Rejects(() => TimingBatchDeleteService.DeleteMany(denied, [deniedId])) &&
               denied.ChangeSerial == 0 && !denied.IsDirty &&
               denied.Document.Timing.Any(timing => timing.Id == deniedId),
            "CanEditTiming rejection changed a batch delete or its history");
        denied.CanEditTiming = true;
        denied.IsReadOnly = true;
        Assert(Rejects(() => TimingBatchDeleteService.DeleteMany(denied, [deniedId])) &&
               denied.ChangeSerial == 0 && !denied.IsDirty,
            "read-only Timing batch delete changed data or history");
    }

    private static void TestMixedDeleteFilteringAndPermissions()
    {
        var visibleNoteId = Guid.NewGuid();
        var hiddenNoteId = Guid.NewGuid();
        var protectedNoteId = Guid.NewGuid();
        var effectId = Guid.NewGuid();
        var bpmOnlyId = Guid.NewGuid();
        var protectedEffectId = Guid.NewGuid();
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            Notes =
            [
                new ChartNote { Id = visibleNoteId, Lane = 0, Beat = BeatPosition.Zero },
                new ChartNote { Id = hiddenNoteId, Lane = 1, Beat = BeatPosition.Preserve(1, 0, 1), Group = 9 },
                new ChartNote { Id = protectedNoteId, Lane = 2, Beat = BeatPosition.Preserve(2, 0, 1) }
            ],
            Timing =
            [
                new TimingEvent { Id = bpmOnlyId, Beat = BeatPosition.Zero, Bpm = 120 },
                new TimingEvent
                {
                    Id = effectId,
                    Beat = BeatPosition.Preserve(3, 0, 1),
                    Effect = TimingEffectKind.Scroll
                },
                new TimingEvent
                {
                    Id = protectedEffectId,
                    Beat = BeatPosition.Preserve(5, 0, 1),
                    Effect = TimingEffectKind.Jump
                }
            ]
        });
        session.Selection.UnionWith([visibleNoteId, hiddenNoteId, protectedNoteId]);
        session.SetOwnerProtection([protectedNoteId], true);
        session.AddOwnerProtectedRegion(
            BeatPosition.Preserve(5, 0, 1),
            BeatPosition.Preserve(6, 0, 1));
        session.AllowOwnerProtectedEdits = false;

        var deleted = MixedSelectionEditingService.Delete(
            session,
            [visibleNoteId, hiddenNoteId, protectedNoteId],
            [effectId, bpmOnlyId, protectedEffectId],
            new HashSet<int> { 9 });
        Assert(deleted.DeletedNoteIds.SequenceEqual([visibleNoteId]) &&
               deleted.DeletedTimingIds.SequenceEqual([effectId]) &&
               session.ChangeSerial == 1 &&
               session.Document.Notes.Select(note => note.Id).ToHashSet().SetEquals([hiddenNoteId, protectedNoteId]) &&
               session.Document.Timing.Any(timing => timing.Id == bpmOnlyId) &&
               session.Document.Timing.Any(timing => timing.Id == protectedEffectId) &&
               session.Selection.SetEquals([hiddenNoteId, protectedNoteId]),
            "mixed delete did not apply WPF hidden/owner-lock/Effect-only filtering");
        Assert(session.Undo() && session.Document.Notes.Count == 3 &&
               session.Document.Timing.Count == 3 && !session.Undo(),
            "mixed delete did not create exactly one undo entry");

        var deniedNoteId = Guid.NewGuid();
        var deniedEffectId = Guid.NewGuid();
        var denied = new EditorSession { CanEditTiming = false };
        denied.ReplaceDocument(new ChartDocument
        {
            Notes = [new ChartNote { Id = deniedNoteId, Lane = 0, Beat = BeatPosition.Zero }],
            Timing =
            [
                new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 },
                new TimingEvent
                {
                    Id = deniedEffectId,
                    Beat = BeatPosition.Preserve(1, 0, 1),
                    Effect = TimingEffectKind.Scroll
                }
            ]
        });
        denied.Selection.Add(deniedNoteId);
        Assert(Rejects(() => MixedSelectionEditingService.Delete(
                   denied,
                   [deniedNoteId],
                   [deniedEffectId])) &&
               denied.ChangeSerial == 0 && !denied.IsDirty &&
               denied.Document.Notes.Any(note => note.Id == deniedNoteId) &&
               denied.Document.Timing.Any(timing => timing.Id == deniedEffectId) &&
               denied.Selection.Contains(deniedNoteId),
            "mixed delete permission rejection partially changed data, history, or selection");
    }

    private static void TestPasteOwnerLockBoundaries()
    {
        var sourceNoteId = Guid.NewGuid();
        var noteSource = new EditorSession { SnapDivision = 12 };
        noteSource.ReplaceDocument(new ChartDocument
        {
            Notes =
            [
                new ChartNote
                {
                    Id = sourceNoteId,
                    Lane = 0,
                    Beat = BeatPosition.Zero,
                    EndBeat = BeatPosition.Preserve(0, 6, 12),
                    OnlineCreatorUserId = 314
                }
            ]
        });
        var noteClipboard = MixedSelectionEditingService.Copy(noteSource, [sourceNoteId], []);

        var boundary = new EditorSession { SnapDivision = 12 };
        boundary.ReplaceDocument(new ChartDocument());
        boundary.AddOwnerProtectedRegion(
            BeatPosition.Preserve(4, 6, 12),
            BeatPosition.Preserve(5, 0, 1));
        boundary.AllowOwnerProtectedEdits = false;
        var allowed = MixedSelectionEditingService.Paste(
            boundary,
            noteClipboard,
            BeatPosition.Preserve(4, 0, 1));
        Assert(allowed.NoteCount == 1 && boundary.Document.Notes.Single() is
               {
                   Beat: { Bar: 4, Numerator: 0, Denominator: 12 },
                   EndBeat: { Bar: 4, Numerator: 6, Denominator: 12 },
                   OnlineCreatorUserId: 314
               },
            "a Note ending exactly at the half-open owner-lock start was rejected");
        Assert(boundary.Undo(), "the allowed boundary paste was not undoable");
        var boundarySerial = boundary.ChangeSerial;
        Assert(Rejects(() => MixedSelectionEditingService.Paste(
                   boundary,
                   noteClipboard,
                   BeatPosition.Preserve(4, 3, 12))) &&
               boundary.ChangeSerial == boundarySerial && boundary.Document.Notes.Count == 0,
            "an overlapping Note paste was not rejected atomically with exact Beat comparisons");

        var mixedSourceNoteId = Guid.NewGuid();
        var mixedSourceEffectId = Guid.NewGuid();
        var mixedSource = new EditorSession { SnapDivision = 12 };
        mixedSource.ReplaceDocument(new ChartDocument
        {
            Notes =
            [
                new ChartNote
                {
                    Id = mixedSourceNoteId,
                    Lane = 0,
                    Beat = BeatPosition.Preserve(1, 0, 1)
                }
            ],
            Timing =
            [
                new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 },
                new TimingEvent
                {
                    Id = mixedSourceEffectId,
                    Beat = BeatPosition.Zero,
                    Effect = TimingEffectKind.Scroll
                }
            ]
        });
        var mixedClipboard = MixedSelectionEditingService.Copy(
            mixedSource,
            [mixedSourceNoteId],
            [mixedSourceEffectId]);
        var lockedMixed = new EditorSession { SnapDivision = 12 };
        lockedMixed.ReplaceDocument(new ChartDocument());
        lockedMixed.AddOwnerProtectedRegion(
            BeatPosition.Preserve(4, 3, 12),
            BeatPosition.Preserve(4, 4, 12));
        lockedMixed.AllowOwnerProtectedEdits = false;
        Assert(Rejects(() => MixedSelectionEditingService.Paste(
                   lockedMixed,
                   mixedClipboard,
                   BeatPosition.Preserve(4, 3, 12))) &&
               lockedMixed.ChangeSerial == 0 && !lockedMixed.IsDirty &&
               lockedMixed.Document.Notes.Count == 0 &&
               lockedMixed.Document.Timing.All(timing => !timing.Effect.HasValue),
            "one locked Effect target did not reject the complete mixed paste");

        var denied = new EditorSession { SnapDivision = 12, CanEditTiming = false };
        denied.ReplaceDocument(new ChartDocument());
        Assert(Rejects(() => MixedSelectionEditingService.Paste(
                   denied,
                   mixedClipboard,
                   BeatPosition.Preserve(4, 0, 1))) &&
               denied.ChangeSerial == 0 && !denied.IsDirty && denied.Document.Notes.Count == 0,
            "mixed paste permission rejection partially inserted Notes");
        denied.CanEditTiming = true;
        denied.IsReadOnly = true;
        Assert(Rejects(() => MixedSelectionEditingService.Paste(
                   denied,
                   mixedClipboard,
                   BeatPosition.Preserve(4, 0, 1))) &&
               denied.ChangeSerial == 0 && !denied.IsDirty,
            "read-only mixed paste changed data or history");
    }

    private static bool Rejects(Action command)
    {
        try
        {
            command();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using LumaChart.Application.Collaboration;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsOwnerLockEditParitySpecs
{
    private static readonly ProEditorColumnRange FourKeyRange = new(0, 3);

    public static IReadOnlyList<WindowsOwnerLockEditParitySpecResult> Run()
    {
        var results = new List<WindowsOwnerLockEditParitySpecResult>();
        Check(results, "owner-lock-placement-exact-end-exclusive-and-ln-range",
            VerifyPlacementBoundaries);
        Check(results, "owner-lock-note-move-source-filter-and-target-atomicity",
            VerifyNoteMoveBoundaries);
        Check(results, "owner-lock-ln-tail-exact-range-is-nonmutating-on-denial",
            VerifyHoldTailBoundaries);
        Check(results, "owner-lock-timing-add-update-delete-exact-boundaries",
            VerifyTimingEditBoundaries);
        Check(results, "owner-lock-timing-batch-source-filter-and-target-atomicity",
            VerifyTimingMoveBoundaries);
        Check(results, "owner-lock-mixed-delete-filters-source-and-paste-rejects-target-atomically",
            VerifyMixedSelectionBoundaries);
        Check(results, "owner-lock-multiple-ln-and-extreme-rational-selection-remains-exact",
            VerifyMultipleHoldAndExtremeRationalBoundaries);
        return results;
    }

    private static string VerifyPlacementBoundaries()
    {
        var start = BeatPosition.Preserve(10, 3, 12);
        var end = BeatPosition.Preserve(10, 7, 12);
        var tapSession = LockedSession(new ChartDocument { KeyCount = 4 }, start, end, 12);
        Require(ThrowsInvalidOperation(() => WindowsProEditorCommand.PlaceTap(
                    tapSession,
                    0,
                    start,
                    FourKeyRange,
                    BasicBeatPlacementPolicy.PreserveAuthoredBeat)) &&
                tapSession.ChangeSerial == 0 &&
                tapSession.Document.Notes.Count == 0 &&
                !tapSession.Undo(),
            "A Tap at the inclusive lock start was not rejected without history.");

        var allowedTap = WindowsProEditorCommand.PlaceTap(
            tapSession,
            0,
            end,
            FourKeyRange,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        Require(allowedTap.HasValue &&
                tapSession.Document.Notes.Single().Beat == end &&
                tapSession.Undo() &&
                tapSession.Document.Notes.Count == 0 &&
                !tapSession.Undo(),
            "A Tap at the exclusive lock end was not accepted as one undo batch.");

        var before = BeatPosition.Preserve(10, 2, 12);
        var boundaryHoldSession = LockedSession(
            new ChartDocument { KeyCount = 4 },
            start,
            end,
            12);
        var allowedHold = WindowsProEditorCommand.PlaceHold(
            boundaryHoldSession,
            1,
            before,
            start,
            FourKeyRange,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        Require(allowedHold.HasValue &&
                boundaryHoldSession.Document.Notes.Single().Beat == before &&
                boundaryHoldSession.Document.Notes.Single().EndBeat == start,
            "An LN ending exactly at the lock start was incorrectly rejected.");

        var crossingSession = LockedSession(
            new ChartDocument { KeyCount = 4 },
            start,
            end,
            12);
        var crossingEnd = BeatPosition.Preserve(10, 4, 12);
        Require(ThrowsInvalidOperation(() => WindowsProEditorCommand.PlaceHold(
                    crossingSession,
                    1,
                    before,
                    crossingEnd,
                    FourKeyRange,
                    BasicBeatPlacementPolicy.PreserveAuthoredBeat)) &&
                crossingSession.ChangeSerial == 0 &&
                crossingSession.Document.Notes.Count == 0 &&
                !crossingSession.Undo(),
            "An LN crossing into the lock was not rejected before mutation.");
        return $"lock=[{start},{end}); tapEndAllowed=true; holdEndAtStartAllowed=true; crossingRejected=true";
    }

    private static string VerifyNoteMoveBoundaries()
    {
        var unlocked = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(5, 1, 12)
        };
        var locked = new ChartNote
        {
            Lane = 1,
            Beat = BeatPosition.Preserve(5, 4, 12)
        };
        var sourceSession = Session(new ChartDocument
        {
            KeyCount = 4,
            Notes = [unlocked, locked]
        }, 12);
        sourceSession.Selection.UnionWith([unlocked.Id, locked.Id]);
        sourceSession.SetOwnerProtection([locked.Id], isProtected: true);
        sourceSession.AllowOwnerProtectedEdits = false;
        var editable = WindowsGroupManagerCommand.VisibleEditableSelectionIds(
            sourceSession,
            new HashSet<int>());
        var moved = WindowsProEditorCommand.MoveSelection(
            sourceSession,
            0,
            new RelativeBeatMove(1, 12),
            FourKeyRange,
            editable);
        Require(editable.SetEquals([unlocked.Id]) &&
                moved == 1 &&
                sourceSession.Document.Notes.Single(note => note.Id == unlocked.Id).Beat ==
                BeatPosition.Preserve(5, 2, 12) &&
                sourceSession.Document.Notes.Single(note => note.Id == locked.Id).Beat ==
                BeatPosition.Preserve(5, 4, 12) &&
                sourceSession.Undo() &&
                !sourceSession.Undo(),
            "The WPF mixed-source move did not skip only the locked Note.");

        var targetNote = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(6, 0, 12)
        };
        var targetStart = BeatPosition.Preserve(6, 1, 12);
        var targetEnd = BeatPosition.Preserve(6, 2, 12);
        var targetSession = LockedSession(
            new ChartDocument { KeyCount = 4, Notes = [targetNote] },
            targetStart,
            targetEnd,
            12);
        targetSession.Selection.Add(targetNote.Id);
        Require(ThrowsInvalidOperation(() => WindowsProEditorCommand.MoveSelection(
                    targetSession,
                    0,
                    new RelativeBeatMove(1, 12),
                    FourKeyRange,
                    [targetNote.Id])) &&
                targetSession.ChangeSerial == 0 &&
                targetSession.Document.Notes.Single().Beat == targetNote.Beat &&
                !targetSession.Undo(),
            "A Note move into the lock start partially committed.");
        var movedToExclusiveEnd = WindowsProEditorCommand.MoveSelection(
            targetSession,
            0,
            new RelativeBeatMove(2, 12),
            FourKeyRange,
            [targetNote.Id]);
        Require(movedToExclusiveEnd == 1 &&
                targetSession.Document.Notes.Single().Beat == targetEnd,
            "A Note move to the exclusive lock end was rejected.");
        return $"sourceEditable={editable.Count}; sourceMoved={moved}; targetStartRejected=true; targetEndAllowed=true";
    }

    private static string VerifyHoldTailBoundaries()
    {
        var head = BeatPosition.Preserve(20, 0, 12);
        var originalEnd = BeatPosition.Preserve(20, 2, 12);
        var lockStart = BeatPosition.Preserve(20, 3, 12);
        var lockEnd = BeatPosition.Preserve(20, 6, 12);
        var note = new ChartNote
        {
            Lane = 0,
            Beat = head,
            EndBeat = originalEnd
        };
        var session = LockedSession(
            new ChartDocument { KeyCount = 4, Notes = [note] },
            lockStart,
            lockEnd,
            12);
        Require(ThrowsInvalidOperation(() => BasicChartEditingService.ResizeHoldTail(
                    session,
                    note.Id,
                    BeatPosition.Preserve(20, 4, 12),
                    BasicBeatPlacementPolicy.PreserveAuthoredBeat)) &&
                session.ChangeSerial == 0 &&
                session.Document.Notes.Single().EndBeat == originalEnd &&
                !session.Undo(),
            "An LN tail crossing the lock partially committed.");
        var changed = BasicChartEditingService.ResizeHoldTail(
            session,
            note.Id,
            lockStart,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        Require(changed &&
                session.Document.Notes.Single().EndBeat == lockStart &&
                session.Undo() &&
                session.Document.Notes.Single().EndBeat == originalEnd &&
                !session.Undo(),
            "An LN tail ending exactly at the lock start was not one legal undo batch.");
        return $"head={head}; lock=[{lockStart},{lockEnd}); crossingRejected=true; boundaryAllowed={changed}";
    }

    private static string VerifyTimingEditBoundaries()
    {
        var lockStart = BeatPosition.Preserve(30, 3, 12);
        var lockEnd = BeatPosition.Preserve(30, 5, 12);
        var existing = new TimingEvent
        {
            Beat = BeatPosition.Preserve(30, 0, 12),
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var endTiming = new TimingEvent
        {
            Beat = lockEnd,
            Effect = TimingEffectKind.Jump,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var session = LockedSession(
            new ChartDocument { Timing = [existing, endTiming] },
            lockStart,
            lockEnd,
            12);
        var initialTimingCount = session.Document.Timing.Count;
        var startEdit = new CompleteTimingEdit(
            lockStart,
            null,
            0,
            TimingEffectKind.Sign,
            "1");
        Require(ThrowsInvalidOperation(() => CompleteTimingEditingService.Add(session, startEdit)) &&
                session.ChangeSerial == 0 &&
                session.Document.Timing.Count == initialTimingCount,
            "Timing add at the inclusive lock start was not rejected.");
        var added = CompleteTimingEditingService.Add(
            session,
            startEdit with { Beat = lockEnd });
        Require(added != Guid.Empty &&
                session.Document.Timing.Single(timing => timing.Id == added).Beat == lockEnd,
            "Timing add at the exclusive lock end was rejected.");
        Require(ThrowsInvalidOperation(() => CompleteTimingEditingService.Update(
                    session,
                    existing.Id,
                    startEdit)) &&
                session.Document.Timing.Single(timing => timing.Id == existing.Id).Beat ==
                BeatPosition.Preserve(30, 0, 12),
            "Timing update into the lock start partially committed.");
        Require(ThrowsInvalidOperation(() => CompleteTimingEditingService.Delete(
                    session,
                    CreateLockedTiming(session, lockStart))) &&
                session.Document.Timing.Any(timing => timing.Beat == lockStart),
            "Timing delete inside the lock was not rejected.");
        Require(CompleteTimingEditingService.Delete(session, endTiming.Id) &&
                session.Document.Timing.All(timing => timing.Id != endTiming.Id),
            "Timing delete at the exclusive lock end was rejected.");
        return $"lock=[{lockStart},{lockEnd}); addStart=false; addEnd=true; updateStart=false; deleteEnd=true";
    }

    private static string VerifyTimingMoveBoundaries()
    {
        var unlocked = new TimingEvent
        {
            Beat = BeatPosition.Preserve(40, 0, 12),
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var locked = new TimingEvent
        {
            Beat = BeatPosition.Preserve(40, 3, 12),
            Effect = TimingEffectKind.Jump,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var sourceSession = LockedSession(
            new ChartDocument { Timing = [unlocked, locked] },
            locked.Beat,
            BeatPosition.Preserve(40, 4, 12),
            12);
        var sourceResult = TimingBatchMoveService.MoveTo(
            sourceSession,
            [unlocked.Id, locked.Id],
            unlocked.Id,
            BeatPosition.Preserve(40, 1, 12));
        Require(sourceResult.MovedCount == 1 &&
                sourceResult.MovableTimingIds.SequenceEqual([unlocked.Id]) &&
                sourceResult.FilteredTimingIds.SequenceEqual([locked.Id]) &&
                sourceSession.Document.Timing.Single(timing => timing.Id == unlocked.Id).Beat ==
                BeatPosition.Preserve(40, 1, 12) &&
                sourceSession.Document.Timing.Single(timing => timing.Id == locked.Id).Beat ==
                locked.Beat &&
                sourceSession.Undo() &&
                !sourceSession.Undo(),
            "The WPF mixed-source Timing move did not filter only the locked row.");

        var target = new TimingEvent
        {
            Beat = BeatPosition.Preserve(41, 0, 12),
            Effect = TimingEffectKind.Sign,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var targetStart = BeatPosition.Preserve(41, 1, 12);
        var targetEnd = BeatPosition.Preserve(41, 2, 12);
        var targetSession = LockedSession(
            new ChartDocument { Timing = [target] },
            targetStart,
            targetEnd,
            12);
        Require(ThrowsInvalidOperation(() => TimingBatchMoveService.MoveTo(
                    targetSession,
                    [target.Id],
                    target.Id,
                    targetStart)) &&
                targetSession.ChangeSerial == 0 &&
                targetSession.Document.Timing.Single(timing => timing.Id == target.Id).Beat ==
                target.Beat &&
                !targetSession.Undo(),
            "Timing move into the lock start partially committed.");
        var allowed = TimingBatchMoveService.MoveTo(
            targetSession,
            [target.Id],
            target.Id,
            targetEnd);
        Require(allowed.MovedCount == 1 &&
                targetSession.Document.Timing.Single(timing => timing.Id == target.Id).Beat ==
                targetEnd,
            "Timing move to the exclusive lock end was rejected.");
        return $"sourceMoved={sourceResult.MovedCount}; sourceFiltered={sourceResult.FilteredTimingIds.Count}; targetEndAllowed=true";
    }

    private static string VerifyMixedSelectionBoundaries()
    {
        var unlockedNote = new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.Preserve(50, 0, 12)
        };
        var lockedNote = new ChartNote
        {
            Lane = 1,
            Beat = BeatPosition.Preserve(50, 2, 12)
        };
        var unlockedEffect = new TimingEvent
        {
            Beat = BeatPosition.Preserve(50, 0, 12),
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var lockedEffect = new TimingEvent
        {
            Beat = BeatPosition.Preserve(50, 2, 12),
            Effect = TimingEffectKind.Jump,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var deleteSession = LockedSession(
            new ChartDocument
            {
                KeyCount = 4,
                Notes = [unlockedNote, lockedNote],
                Timing = [unlockedEffect, lockedEffect]
            },
            lockedNote.Beat,
            BeatPosition.Preserve(50, 3, 12),
            12);
        var initialDeleteNoteCount = deleteSession.Document.Notes.Count;
        var initialDeleteTimingCount = deleteSession.Document.Timing.Count;
        deleteSession.Selection.UnionWith([unlockedNote.Id, lockedNote.Id]);
        var deleted = MixedSelectionEditingService.Delete(
            deleteSession,
            deleteSession.Selection,
            [unlockedEffect.Id, lockedEffect.Id]);
        Require(deleted.DeletedNoteIds.SequenceEqual([unlockedNote.Id]) &&
                deleted.DeletedTimingIds.SequenceEqual([unlockedEffect.Id]) &&
                deleteSession.Document.Notes.Count == initialDeleteNoteCount - 1 &&
                deleteSession.Document.Notes.Any(note => note.Id == lockedNote.Id) &&
                deleteSession.Document.Notes.All(note => note.Id != unlockedNote.Id) &&
                deleteSession.Document.Timing.Count == initialDeleteTimingCount - 1 &&
                deleteSession.Document.Timing.Any(timing => timing.Id == lockedEffect.Id) &&
                deleteSession.Document.Timing.All(timing => timing.Id != unlockedEffect.Id) &&
                deleteSession.Undo() &&
                deleteSession.Document.Notes.Count == initialDeleteNoteCount &&
                deleteSession.Document.Timing.Count == initialDeleteTimingCount &&
                !deleteSession.Undo(),
            "Mixed delete did not match WPF source-lock filtering and single undo.");

        var clipboardSource = Session(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote
                {
                    Lane = 0,
                    Beat = BeatPosition.Zero
                }
            ],
            Timing =
            [
                new TimingEvent
                {
                    Beat = BeatPosition.Preserve(0, 1, 12),
                    Effect = TimingEffectKind.Sign,
                    EffectValue = 1,
                    EffectValueText = "1"
                }
            ]
        }, 12);
        var clipboard = MixedSelectionEditingService.Copy(
            clipboardSource,
            clipboardSource.Document.Notes.Select(note => note.Id),
            clipboardSource.Document.Timing.Select(timing => timing.Id));
        var pasteTarget = BeatPosition.Preserve(60, 0, 12);
        var pasteSession = LockedSession(
            new ChartDocument { KeyCount = 4 },
            BeatPosition.Preserve(60, 1, 12),
            BeatPosition.Preserve(60, 2, 12),
            12);
        var initialPasteNoteCount = pasteSession.Document.Notes.Count;
        var initialPasteTimingCount = pasteSession.Document.Timing.Count;
        Require(ThrowsInvalidOperation(() => MixedSelectionEditingService.Paste(
                    pasteSession,
                    clipboard,
                    pasteTarget)) &&
                pasteSession.ChangeSerial == 0 &&
                pasteSession.Document.Notes.Count == initialPasteNoteCount &&
                pasteSession.Document.Timing.Count == initialPasteTimingCount &&
                !pasteSession.Undo(),
            "One locked paste target did not cancel the complete mixed payload.");
        return $"deleted={deleted.DeletedNoteCount}/{deleted.DeletedEffectCount}; pasteAtomicRejected=true";
    }

    private static string VerifyMultipleHoldAndExtremeRationalBoundaries()
    {
        const int extremeDenominator = 999_983;
        var firstStart = BeatPosition.Preserve(70, 1, extremeDenominator);
        var firstEnd = BeatPosition.Preserve(70, 2, extremeDenominator);
        var secondStart = BeatPosition.Preserve(70, 500_000, extremeDenominator);
        var secondEnd = BeatPosition.Preserve(71, 7, extremeDenominator);
        var pointRange = OwnerLockSelectionPlanner.BuildPointRange(firstStart, 1920);
        Require(pointRange.StartBeat == firstStart &&
                pointRange.EndBeat ==
                BeatPosition.Preserve(70, 2, extremeDenominator),
            "An extreme authored point was widened, reduced or rejected by lock planning.");

        var selection = OwnerLockSelectionPlanner.BuildSelectedObjectRanges(
        [
            new OwnerLockSelectionRange(secondStart, secondEnd),
            new OwnerLockSelectionRange(firstStart, firstEnd)
        ]);
        Require(selection.Count == 1 &&
                selection[0].StartBeat == firstStart &&
                selection[0].EndBeat == secondEnd &&
                selection[0].StartBeat.Denominator == extremeDenominator &&
                selection[0].EndBeat.Denominator == extremeDenominator,
            "Two LNs did not form one exact continuous extreme-rational lock.");

        var first = new ChartNote
        {
            Lane = 0,
            Beat = firstStart,
            EndBeat = firstEnd
        };
        var second = new ChartNote
        {
            Lane = 1,
            Beat = secondStart,
            EndBeat = secondEnd
        };
        var boundaryTiming = new TimingEvent
        {
            Beat = secondEnd,
            Effect = TimingEffectKind.Sign,
            EffectValue = 1,
            EffectValueText = "1"
        };
        var session = LockedSession(
            new ChartDocument
            {
                KeyCount = 4,
                Notes = [first, second],
                Timing = [boundaryTiming]
            },
            selection[0].StartBeat,
            selection[0].EndBeat,
            1920);
        Require(session.IsOwnerProtected(first) &&
                session.IsOwnerProtected(second) &&
                session.IsBeatOwnerProtected(firstStart) &&
                !session.IsBeatOwnerProtected(secondEnd) &&
                !session.IsTimingOwnerProtected(boundaryTiming),
            "Extreme-rational half-open membership changed at an LN or end boundary.");
        return $"lnCount=2; lock=[{firstStart},{secondEnd}); denominator={extremeDenominator}; endExclusive=true";
    }

    private static Guid CreateLockedTiming(EditorSession session, BeatPosition beat)
    {
        var timing = new TimingEvent
        {
            Beat = beat,
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1,
            EffectValueText = "1"
        };
        session.ApplyRemoteMutation(document => document.Timing.Add(timing));
        return timing.Id;
    }

    private static EditorSession LockedSession(
        ChartDocument document,
        BeatPosition start,
        BeatPosition end,
        int division)
    {
        var session = Session(document, division);
        session.AddOwnerProtectedRegion(start, end);
        session.AllowOwnerProtectedEdits = false;
        return session;
    }

    private static EditorSession Session(ChartDocument document, int division)
    {
        var session = new EditorSession { SnapDivision = division };
        session.ReplaceDocument(document);
        return session;
    }

    private static bool ThrowsInvalidOperation(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void Check(
        ICollection<WindowsOwnerLockEditParitySpecResult> results,
        string name,
        Func<string> check)
    {
        try
        {
            results.Add(new WindowsOwnerLockEditParitySpecResult(name, true, check()));
        }
        catch (Exception exception)
        {
            results.Add(new WindowsOwnerLockEditParitySpecResult(name, false, exception.ToString()));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record WindowsOwnerLockEditParitySpecResult(
    string Name,
    bool Passed,
    string Detail);

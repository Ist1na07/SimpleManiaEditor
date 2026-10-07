using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class BasicChartEditingSpecs
{
    public static void Run()
    {
        var session = new EditorSession { SnapDivision = 4 };
        session.ReplaceDocument(new ChartDocument
        {
            Metadata = new ChartMetadata
            {
                Title = "P0-2",
                Artist = "Artist",
                Creator = "Creator",
                Version = "Normal"
            },
            KeyCount = 4,
            Notes = [],
            Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
        });

        var tapId = BasicChartEditingService.PlaceTap(session, 0, BeatPosition.FromDouble(1.01));
        Assert(tapId.HasValue && session.Document.Notes.Single().Beat == BeatPosition.FromDouble(1),
            "Tap placement did not snap to the active subdivision");
        Assert(BasicChartEditingService.PlaceTap(session, 0, BeatPosition.FromDouble(1.02)) == null &&
               session.Document.Notes.Count == 1,
            "Tap placement allowed a duplicate head on the same lane/beat");

        var exactPlacementSession = new EditorSession { SnapDivision = 4 };
        exactPlacementSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
        var cellCenter = BeatPosition.SnapToCellCenter(.2, 6);
        Assert(cellCenter is { Bar: 0, Numerator: 3, Denominator: 12 },
            "the WPF sixth-grid cell center fixture was not expressed as 3/12");
        var legacySnappedTapId = BasicChartEditingService.PlaceTap(
            exactPlacementSession,
            3,
            cellCenter);
        Assert(legacySnappedTapId.HasValue && exactPlacementSession.Document.Notes.Single(note =>
                   note.Id == legacySnappedTapId) is
                   { Beat: { Bar: 0, Numerator: 1, Denominator: 4 } },
            "the original Tap placement API no longer snaps to the active subdivision");
        var centeredTapId = BasicChartEditingService.PlaceTap(
            exactPlacementSession,
            0,
            cellCenter,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        Assert(centeredTapId.HasValue && exactPlacementSession.Document.Notes.Single(note =>
                   note.Id == centeredTapId) is
                   { Beat: { Bar: 0, Numerator: 3, Denominator: 12 } },
            "exact Tap placement snapped the authored 3/12 cell center a second time");

        var exactHoldStart = BeatPosition.Preserve(1, 3, 12);
        var exactHoldEnd = BeatPosition.Preserve(1, 9, 12);
        var exactHoldId = BasicChartEditingService.PlaceHold(
            exactPlacementSession,
            1,
            exactHoldStart,
            exactHoldEnd,
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        Assert(exactHoldId.HasValue && exactPlacementSession.Document.Notes.Single(note =>
                   note.Id == exactHoldId) is
                   {
                       Beat: { Bar: 1, Numerator: 3, Denominator: 12 },
                       EndBeat: { Bar: 1, Numerator: 9, Denominator: 12 }
                   },
            "exact LN placement rewrote an authored endpoint expression");
        var exactResizeTail = BeatPosition.Preserve(2, 5, 12);
        Assert(exactHoldId.HasValue &&
               BasicChartEditingService.ResizeHoldTail(
                   exactPlacementSession,
                   exactHoldId.Value,
                   exactResizeTail,
                   BasicBeatPlacementPolicy.PreserveAuthoredBeat) &&
               exactPlacementSession.Document.Notes.Single(note => note.Id == exactHoldId) is
                   { EndBeat: { Bar: 2, Numerator: 5, Denominator: 12 } } &&
               exactPlacementSession.Undo() &&
               exactPlacementSession.Document.Notes.Single(note => note.Id == exactHoldId) is
                   { EndBeat: { Bar: 1, Numerator: 9, Denominator: 12 } },
            "exact LN tail resize did not preserve the authored 5/12 expression as one undo");
        var legacyResizeExpected = exactResizeTail.Snap(exactPlacementSession.SnapDivision);
        Assert(BasicChartEditingService.ResizeHoldTail(
                   exactPlacementSession,
                   exactHoldId!.Value,
                   exactResizeTail) &&
               exactPlacementSession.Document.Notes.Single(note => note.Id == exactHoldId).EndBeat ==
                   legacyResizeExpected &&
               exactPlacementSession.Undo(),
            "the original LN tail resize API no longer snaps to the active subdivision");

        var movableExactId = BasicChartEditingService.PlaceTap(
            exactPlacementSession,
            2,
            BeatPosition.Preserve(0, 9, 12),
            BasicBeatPlacementPolicy.PreserveAuthoredBeat);
        Assert(movableExactId.HasValue &&
               BasicChartEditingService.NudgeSelection(exactPlacementSession, -1) == 1 &&
               exactPlacementSession.Document.Notes.Single(note => note.Id == movableExactId) is
                   { Beat: { Bar: 0, Numerator: 6, Denominator: 12 } },
            "an exact 9/12 Tap did not move by 1/4 to unreduced 6/12");

        var exactSerialBeforeFailure = exactPlacementSession.ChangeSerial;
        var exactDirtyBeforeFailure = exactPlacementSession.IsDirty;
        var exactNotesBeforeFailure = exactPlacementSession.Document.Notes.Count;
        var exactSelectionBeforeFailure = exactPlacementSession.Selection.ToHashSet();
        Assert(BasicChartEditingService.PlaceHold(
                   exactPlacementSession,
                   3,
                   BeatPosition.Preserve(-1, 11, 12),
                   BeatPosition.Preserve(0, 3, 12),
                   BasicBeatPlacementPolicy.PreserveAuthoredBeat) == null &&
               exactPlacementSession.ChangeSerial == exactSerialBeforeFailure &&
               exactPlacementSession.IsDirty == exactDirtyBeforeFailure &&
               exactPlacementSession.Document.Notes.Count == exactNotesBeforeFailure &&
               exactPlacementSession.Selection.SetEquals(exactSelectionBeforeFailure),
            "a rejected exact LN placement changed document, history, dirty, or selection state");

        var holdId = BasicChartEditingService.PlaceHold(
            session,
            1,
            BeatPosition.FromDouble(3),
            BeatPosition.FromDouble(2));
        var hold = session.Document.Notes.Single(note => note.Id == holdId);
        Assert(hold.Beat == BeatPosition.FromDouble(2) && hold.EndBeat == BeatPosition.FromDouble(3),
            "reverse LN drag did not normalize its head and tail");
        Assert(BasicChartEditingService.ResizeHoldTail(session, hold.Id, hold.Beat) &&
               !session.Document.Notes.Single(note => note.Id == hold.Id).IsHold,
            "shortening an LN to its head did not degrade it to a Tap");
        Assert(session.Undo() && session.Document.Notes.Single(note => note.Id == hold.Id).IsHold,
            "LN tail resize was not a single undo operation");

        var selected = BasicChartEditingService.SelectRange(
            session,
            0,
            1,
            BeatPosition.FromDouble(.5),
            BeatPosition.FromDouble(3.5));
        Assert(selected == 2 && session.Selection.SetEquals(new[] { tapId!.Value, hold.Id }),
            "range selection did not include both Tap and intersecting LN");
        var clipboard = BasicChartEditingService.CopySelection(session);
        Assert(clipboard?.Count == 2, "copy did not capture the complete selection");
        Assert(BasicChartEditingService.MoveSelection(session, 1, .25) == 2 &&
               session.Document.Notes.Any(note => note.Id == tapId && note.Lane == 1 &&
                                                  note.Beat == BeatPosition.FromDouble(1.25)),
            "batch move did not apply lane and snapped beat deltas atomically");
        Assert(session.Undo() && session.Document.Notes.Any(note => note.Id == tapId && note.Lane == 0),
            "batch move did not undo as one operation");

        var nativeSubdivisionSession = new EditorSession { SnapDivision = 4 };
        var nativeId = Guid.NewGuid();
        nativeSubdivisionSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes = [new ChartNote { Id = nativeId, Lane = 0, Beat = new BeatPosition(0, 5, 6) }]
        });
        nativeSubdivisionSession.Selection.Add(nativeId);
        Assert(Math.Abs(BasicChartEditingService.RelativeMoveStep(
                   nativeSubdivisionSession.Document.Notes[0].Beat, 4) - 1d / 4) < 1e-9,
            "an off-grid note did not use the current movement subdivision");
        Assert(Math.Abs(BasicChartEditingService.QuantizeRelativeMoveDelta(
                   nativeSubdivisionSession.Document.Notes[0].Beat, 4, -.14) + 1d / 4) < 1e-9,
            "relative drag delta did not quantize against the current subdivision");
        Assert(BasicChartEditingService.NudgeSelection(nativeSubdivisionSession, -1) == 1 &&
               nativeSubdivisionSession.Document.Notes.Single().Beat is
                   { Bar: 0, Numerator: 7, Denominator: 12 },
            "5/6 did not move backward by the current 1/4 step to 7/12");
        Assert(nativeSubdivisionSession.Undo() &&
               BasicChartEditingService.NudgeSelection(nativeSubdivisionSession, 1) == 1 &&
               nativeSubdivisionSession.Document.Notes.Single().Beat is
                   { Bar: 1, Numerator: 1, Denominator: 12 },
            "current-subdivision forward movement did not cross the measure boundary exactly");
        nativeSubdivisionSession.Mutate(document =>
            document.Notes.Single().Beat = BeatPosition.Preserve(1, 1, 6));
        Assert(BasicChartEditingService.NudgeSelection(nativeSubdivisionSession, -1) == 1 &&
               nativeSubdivisionSession.Document.Notes.Single().Beat is
                   { Bar: 0, Numerator: 11, Denominator: 12 },
            "current-subdivision reverse movement lost the exact common denominator");
        Assert(Math.Abs(BasicChartEditingService.RelativeMoveStep(new BeatPosition(0, 3, 4), 4) - .25) < 1e-9,
            "a note on the selected grid did not use the selected subdivision step");
        var seventhMove = BasicChartEditingService.QuantizeRelativeMove(
            new BeatPosition(0, 5, 7), 4, 1d / 7);
        Assert(BasicChartEditingService.ApplyRelativeMove(new BeatPosition(0, 5, 7), seventhMove) is
               { Bar: 0, Numerator: 27, Denominator: 28 },
            "relative movement did not retain the exact LCM expression");
        var centeredMove = BasicChartEditingService.ApplyRelativeMove(
            BeatPosition.Preserve(0, 9, 12),
            new RelativeBeatMove(-1, 4));
        Assert(centeredMove is { Bar: 0, Numerator: 6, Denominator: 12 },
            "a sixth-grid cell center did not move by 3/12 without reduction");
        var sixthGridMove = BasicChartEditingService.ApplyRelativeMove(
            BeatPosition.Preserve(0, 4, 6),
            new RelativeBeatMove(-1, 6));
        Assert(sixthGridMove is { Bar: 0, Numerator: 3, Denominator: 6 },
            "movement rewrote 3/6 as an equivalent reduced or current-grid fraction");
        Assert(BasicChartEditingService.ApplyRelativeMove(
                   default,
                   new RelativeBeatMove(1, 4)) is { Bar: 0, Numerator: 1, Denominator: 4 },
            "default BeatPosition did not behave as zero during relative movement");
        var boundaryMove = BasicChartEditingService.QuantizeRelativeMove(
            new BeatPosition(0, 5, 6), 4, -1, minimumOriginBeat: .1);
        Assert(boundaryMove.Steps == 0,
            "group movement crossed beat zero");

        var groupBoundarySession = new EditorSession { SnapDivision = 4 };
        var boundaryId = Guid.NewGuid();
        var laterId = Guid.NewGuid();
        groupBoundarySession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Notes =
            [
                new ChartNote { Id = boundaryId, Lane = 0, Beat = BeatPosition.Zero },
                new ChartNote { Id = laterId, Lane = 1, Beat = new BeatPosition(1, 0, 1) }
            ]
        });
        groupBoundarySession.Selection.UnionWith([boundaryId, laterId]);
        Assert(BasicChartEditingService.NudgeSelection(groupBoundarySession, -1) == 0 &&
               groupBoundarySession.Document.Notes.Single(note => note.Id == laterId).Beat ==
               new BeatPosition(1, 0, 1),
            "a group nudge partially moved notes after the earliest note reached beat zero");

        var denominatorRejected = false;
        try
        {
            _ = BasicChartEditingService.ApplyRelativeMove(
                BeatPosition.Preserve(0, 1, 999_983),
                new RelativeBeatMove(1, 1920));
        }
        catch (InvalidOperationException) { denominatorRejected = true; }
        Assert(denominatorRejected, "movement silently reduced an LCM beyond the denominator ceiling");

        Assert(BasicChartEditingService.Paste(session, clipboard!, 2, BeatPosition.FromDouble(4)) == 2 &&
               session.Selection.Count == 2,
            "paste did not create and select independent note copies");
        var beforeDelete = session.Document.Notes.Count;
        Assert(BasicChartEditingService.DeleteSelection(session) == 2 &&
               session.Document.Notes.Count == beforeDelete - 2 && session.Undo() &&
               session.Document.Notes.Count == beforeDelete,
            "selection delete was not reversible as one operation");

        var bpmId = BasicChartEditingService.AddTiming(session, new BasicTimingEdit(
            BeatPosition.FromDouble(4.5), BasicTimingKind.BpmDelay, 175.25, -12.345678901));
        var bpm = session.Document.Timing.Single(timing => timing.Id == bpmId);
        Assert(bpm.Bpm == 175.25 && bpm.Delay == -12.345678901,
            "BPM/Delay edit lost numeric precision");
        Assert(BasicChartEditingService.UpdateTiming(session, bpmId, new BasicTimingEdit(
                   BeatPosition.FromDouble(4.75), BasicTimingKind.Scroll, .875)) &&
               session.Document.Timing.Single(timing => timing.Id == bpmId) is
                   { Bpm: null, Effect: TimingEffectKind.Scroll, EffectValue: .875 },
            "Timing update did not preserve identity while changing kind and beat");
        var effectId = BasicChartEditingService.AddTiming(session, new BasicTimingEdit(
            BeatPosition.FromDouble(5), BasicTimingKind.Custom, 1.125, CustomFieldName: "camera_zoom"));
        var effect = session.Document.Timing.Single(timing => timing.Id == effectId);
        Assert(effect.Effect == TimingEffectKind.Custom && effect.EffectFieldName == "camera_zoom" &&
               effect.EffectValueText == "1.125",
            "custom effect edit did not preserve its field and exact value text");

        var combinedBeat = BeatPosition.Preserve(6, 3, 12);
        var combinedId = CompleteTimingEditingService.Add(session, new CompleteTimingEdit(
            combinedBeat,
            182.5,
            -7.25,
            TimingEffectKind.Scroll,
            "1.0"));
        var combined = session.Document.Timing.Single(timing => timing.Id == combinedId);
        Assert(combined is
               {
                   Beat: { Bar: 6, Numerator: 3, Denominator: 12 },
                   Bpm: 182.5,
                   Delay: -7.25,
                   Effect: TimingEffectKind.Scroll,
                   EffectValue: 1
               } && combined.EffectValueText == "1.0",
            "complete Timing Edit did not retain combined BPM/delay/effect or authored JSON text");
        var combinedRoundTripDocument = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(session.Document));
        var combinedEffectRoundTrip = combinedRoundTripDocument.Timing
            .Single(timing => timing.Beat == combinedBeat && timing.Effect == TimingEffectKind.Scroll);
        var combinedBpmRoundTrip = combinedRoundTripDocument.Timing
            .Single(timing => timing.Beat == combinedBeat && timing.Bpm.HasValue);
        Assert(combinedBpmRoundTrip.Beat is { Bar: 6, Numerator: 3, Denominator: 12 } &&
               combinedEffectRoundTrip.Beat is { Bar: 6, Numerator: 3, Denominator: 12 } &&
               combinedBpmRoundTrip.Bpm == 182.5 && combinedBpmRoundTrip.Delay == -7.25 &&
               combinedEffectRoundTrip.EffectValueText == "1.0",
            "combined Timing Edit did not preserve its BPM/delay/effect components or 1.0 through MC save/reopen");

        combined.OnlineCreatorUserId = 7721;
        Assert(CompleteTimingEditingService.Update(session, combinedId, new CompleteTimingEdit(
                   BeatPosition.Preserve(6, 9, 12),
                   190,
                   2.5,
                   TimingEffectKind.Custom,
                   "2e-3",
                   "camera.zoom-1")) &&
               session.Document.Timing.Single(timing => timing.Id == combinedId) is
               {
                   Beat: { Bar: 6, Numerator: 9, Denominator: 12 },
                   Bpm: 190,
                   Delay: 2.5,
                   Effect: TimingEffectKind.Custom,
                   EffectValue: .002,
                   EffectValueText: "2e-3",
                   EffectFieldName: "camera.zoom-1",
                   OnlineCreatorUserId: 7721
               },
            "complete Timing update lost identity, combined fields, exact text or runtime creator identity");
        var customRoundTrip = MalodyMcSerializer.Parse(MalodyMcSerializer.Serialize(session.Document))
            .Timing.Single(timing => timing.Beat == BeatPosition.Preserve(6, 9, 12) &&
                                     timing.Effect == TimingEffectKind.Custom);
        Assert(customRoundTrip.Beat is { Bar: 6, Numerator: 9, Denominator: 12 } &&
               customRoundTrip.EffectFieldName == "camera.zoom-1" &&
               customRoundTrip.EffectValueText == "2e-3" &&
               Math.Abs(customRoundTrip.EffectValue - .002) < 1e-12,
            "custom Timing Edit did not preserve 2e-3 through MC save/reopen");

        var timingSerialBeforeFailure = session.ChangeSerial;
        var timingCountBeforeFailure = session.Document.Timing.Count;
        var invalidTimingRejected = false;
        try
        {
            _ = CompleteTimingEditingService.Update(session, combinedId, new CompleteTimingEdit(
                BeatPosition.Zero,
                120,
                0,
                TimingEffectKind.Custom,
                "1",
                "beat"));
        }
        catch (ArgumentException) { invalidTimingRejected = true; }
        Assert(invalidTimingRejected && session.ChangeSerial == timingSerialBeforeFailure &&
               session.Document.Timing.Count == timingCountBeforeFailure &&
               session.Document.Timing.Single(timing => timing.Id == combinedId).EffectValueText == "2e-3",
            "invalid complete Timing Edit changed document/history instead of failing atomically");

        var deniedTimingId = Guid.NewGuid();
        var deniedTimingSession = new EditorSession { CanEditTiming = false };
        deniedTimingSession.ReplaceDocument(new ChartDocument
        {
            Timing =
            [
                new TimingEvent
                {
                    Id = deniedTimingId,
                    Beat = BeatPosition.Preserve(0, 1, 4),
                    Bpm = 120
                }
            ]
        });
        var deniedTimingSerial = deniedTimingSession.ChangeSerial;
        var deniedTimingDirty = deniedTimingSession.IsDirty;
        var permissionRejected = false;
        try
        {
            _ = CompleteTimingEditingService.Update(deniedTimingSession, deniedTimingId, new CompleteTimingEdit(
                BeatPosition.Preserve(0, 3, 12),
                120,
                0,
                null));
        }
        catch (InvalidOperationException) { permissionRejected = true; }
        Assert(permissionRejected && deniedTimingSession.ChangeSerial == deniedTimingSerial &&
               deniedTimingSession.IsDirty == deniedTimingDirty &&
               deniedTimingSession.Document.Timing.Single() is
                   { Beat: { Bar: 0, Numerator: 1, Denominator: 4 }, Bpm: 120 },
            "same-value 1/4 to 3/12 Timing rewrite bypassed permission or changed state on rejection");

        var protectedTimingId = Guid.NewGuid();
        var protectedOutsideId = Guid.NewGuid();
        var protectedTimingSession = new EditorSession();
        protectedTimingSession.ReplaceDocument(new ChartDocument
        {
            Timing =
            [
                new TimingEvent
                {
                    Id = protectedTimingId,
                    Beat = BeatPosition.Preserve(0, 1, 4),
                    Bpm = 120
                },
                new TimingEvent
                {
                    Id = protectedOutsideId,
                    Beat = BeatPosition.Preserve(2, 0, 1),
                    Bpm = 150
                }
            ]
        });
        protectedTimingSession.AddOwnerProtectedRegion(BeatPosition.Zero, BeatPosition.Preserve(1, 0, 1));
        protectedTimingSession.AllowOwnerProtectedEdits = false;
        var protectedSerial = protectedTimingSession.ChangeSerial;
        var protectedDirty = protectedTimingSession.IsDirty;
        var originalBeatRejected = false;
        var targetBeatRejected = false;
        var addRejected = false;
        var deleteRejected = false;
        try
        {
            _ = CompleteTimingEditingService.Update(protectedTimingSession, protectedTimingId, new CompleteTimingEdit(
                BeatPosition.Preserve(2, 1, 4), 120, 0, null));
        }
        catch (InvalidOperationException) { originalBeatRejected = true; }
        try
        {
            _ = CompleteTimingEditingService.Update(protectedTimingSession, protectedOutsideId, new CompleteTimingEdit(
                BeatPosition.Preserve(0, 3, 12), 150, 0, null));
        }
        catch (InvalidOperationException) { targetBeatRejected = true; }
        try
        {
            _ = CompleteTimingEditingService.Add(protectedTimingSession, new CompleteTimingEdit(
                BeatPosition.Preserve(0, 9, 12), 180, 0, null));
        }
        catch (InvalidOperationException) { addRejected = true; }
        try
        {
            _ = CompleteTimingEditingService.Delete(protectedTimingSession, protectedTimingId);
        }
        catch (InvalidOperationException) { deleteRejected = true; }
        Assert(originalBeatRejected && targetBeatRejected && addRejected && deleteRejected &&
               protectedTimingSession.ChangeSerial == protectedSerial &&
               protectedTimingSession.IsDirty == protectedDirty &&
               protectedTimingSession.Document.Timing.Count == 2 &&
               protectedTimingSession.Document.Timing.Single(timing => timing.Id == protectedTimingId).Beat is
                   { Bar: 0, Numerator: 1, Denominator: 4 } &&
               protectedTimingSession.Document.Timing.Single(timing => timing.Id == protectedOutsideId).Beat is
                   { Bar: 2, Numerator: 0, Denominator: 1 },
            "owner lock did not preflight Timing add/update/delete atomically at original and target beats");

        var readOnlyTimingSession = new EditorSession();
        readOnlyTimingSession.ReplaceDocument(new ChartDocument());
        readOnlyTimingSession.IsReadOnly = true;
        var readOnlyTimingCount = readOnlyTimingSession.Document.Timing.Count;
        var readOnlyTimingSerial = readOnlyTimingSession.ChangeSerial;
        var readOnlyTimingDirty = readOnlyTimingSession.IsDirty;
        var readOnlyRejected = false;
        try
        {
            _ = CompleteTimingEditingService.Add(readOnlyTimingSession, new CompleteTimingEdit(
                BeatPosition.Zero, 120, 0, null));
        }
        catch (InvalidOperationException) { readOnlyRejected = true; }
        Assert(readOnlyRejected && readOnlyTimingSession.Document.Timing.Count == readOnlyTimingCount &&
               readOnlyTimingSession.ChangeSerial == readOnlyTimingSerial &&
               readOnlyTimingSession.IsDirty == readOnlyTimingDirty,
            "read-only Timing edit was not rejected before mutation");

        var laneFourId = Guid.NewGuid();
        session.Mutate(document => document.Notes.Add(new ChartNote
        {
            Id = laneFourId,
            Lane = 3,
            Beat = BeatPosition.FromDouble(8)
        }));
        Assert(BasicChartEditingService.ApplyMetadata(session, new BasicMetadataEdit(
                   "Renamed", "New Artist", "New Creator", "Hard", 3, 175.25, 9.4, 6.25)) &&
               session.Document.Metadata.Title == "Renamed" && session.Document.KeyCount == 3 &&
               session.Document.Metadata.Bpm == 175.25 &&
               session.Document.Metadata.OverallDifficulty == 9.4 &&
               session.Document.Metadata.HpDrainRate == 6.25 &&
               session.Document.Notes.All(note => note.Id != laneFourId),
            "Metadata/KeyCount were not applied atomically");
        Assert(session.Undo() && session.Document.KeyCount == 4 &&
               session.Document.Notes.Any(note => note.Id == laneFourId),
            "Metadata/KeyCount atomic edit did not restore removed lanes on undo");

        session.Mutate(document => document.Notes.Add(new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.FromDouble(1)
        }));
        var issue = BasicChartEditingService.ValidateAndSelectFirst(session);
        Assert(issue != null && issue.NoteIds.Count == 2 && session.Selection.SetEquals(issue.NoteIds),
            "validator focus did not select both notes involved in the first structural error");

        var readOnly = new EditorSession { CanEditNotes = false };
        readOnly.ReplaceDocument(new ChartDocument());
        Assert(BasicChartEditingService.PlaceTap(readOnly, 0, BeatPosition.Zero) == null &&
               readOnly.Document.Notes.Count == 0,
            "shared editing command reported success after EditorSession denied Note permission");

        var noOpSession = new EditorSession();
        noOpSession.ReplaceDocument(new ChartDocument
        {
            Metadata = new ChartMetadata { Title = "No-op" },
            Notes = [new ChartNote { Lane = 0, Beat = BeatPosition.Zero, Volume = 100 }]
        });
        var noOpChangedCount = 0;
        noOpSession.Changed += (_, _) => noOpChangedCount++;
        noOpSession.Mutate(document => document.Notes[0].Volume = 101);
        Assert(noOpSession.Document.Notes[0].Volume == 100 &&
               noOpSession.ChangeSerial == 0 && !noOpSession.IsDirty &&
               noOpChangedCount == 0 && !noOpSession.Undo(),
            "a normalized no-op mutation created dirty state, history, notifications, or a revision");

        noOpSession.Mutate(document => document.Metadata.Title = "Saved revision");
        var saveRevision = noOpSession.ChangeSerial;
        noOpSession.Mutate(document => document.Metadata.Title = "Edited during save");
        Assert(!noOpSession.TryMarkSaved(saveRevision) && noOpSession.IsDirty &&
               noOpSession.Document.Metadata.Title == "Edited during save",
            "saving an older revision cleared the dirty state of a newer edit");
        noOpSession.MarkSaved();
        Assert(!noOpSession.IsDirty,
            "marking the current revision as saved did not clear dirty state");

        MixedSelectionEditingSpecs.Run();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

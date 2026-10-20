using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

internal static class MapperIndependent036Specs
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Hidden)!.GetValue(instance)!;
    private static object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Hidden)!.Invoke(instance, args);
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void SymmetricTails()
    {
        var settings = new CenterSymmetrySettings(new(0, 0, 1), new(24, 0, 1), Enumerable.Range(0, 7).ToArray());
        var source = new ChartNote { Lane = 1, Beat = new(8, 0, 1), EndBeat = new(10, 0, 1) };
        var pair = CenterSymmetryService.CreateCompanion(source, settings, 7)!; pair.Sound = "pair.wav"; pair.Volume = 17;
        var session = new EditorSession();
        void Reset(params ChartNote[] extra) => session.ReplaceDocument(new ChartDocument { KeyCount = 7, Notes = new[] { source.Clone(), pair.Clone() }.Concat(extra).ToList() });
        Reset(); var serial = session.ChangeSerial;
        var preview = SymmetricHoldTailResizeCommand.Preview(session, source.Id, new(12, 1, 7), settings);
        Require(session.ChangeSerial == serial && session.Document.Notes.Single(n => n.Id == pair.Id).Beat.Value == 14,
            "tail preview mutated the live chart");
        var reflected = preview.Notes.Single(n => n.Id == pair.Id);
        Require(reflected.Beat == new BeatPosition(11, 6, 7) && reflected.EndBeat!.Value.Value == 16,
            "source tail did not update reflected HEAD exactly");
        Require(SymmetricHoldTailResizeCommand.Commit(session, source.Id, new(12, 0, 1), settings, serial), "symmetric tail did not commit");
        Require(session.Document.Notes.Single(n => n.Id == source.Id).EndBeat!.Value.Value == 12 &&
            session.Document.Notes.Single(n => n.Id == pair.Id).Beat.Value == 12 &&
            session.Document.Notes.Single(n => n.Id == pair.Id).Sound == "pair.wav" && session.Document.Notes.Single(n => n.Id == pair.Id).Volume == 17,
            "symmetric tail lost pair identity/properties or updated its wrong end");
        Require(session.Undo() && session.Document.Notes.Single(n => n.Id == pair.Id).Beat.Value == 14 && !session.CanUndo,
            "symmetric tail change was not one atomic undo");
        Require(session.Redo() && session.Document.Notes.Single(n => n.Id == pair.Id).Beat.Value == 12, "symmetric tail redo failed");
        Reset(); SymmetricHoldTailResizeCommand.Commit(session, source.Id, source.Beat, settings, session.ChangeSerial);
        Require(session.Document.Notes.All(n => !n.EndBeat.HasValue) && session.Document.Notes.Single(n => n.Id == pair.Id).Beat.Value == 16,
            "shrinking source to Tap did not collapse/move the reflected LN");
        var occupied = new ChartNote { Lane = pair.Lane, Beat = new(12, 0, 1) };
        Reset(occupied); SymmetricHoldTailResizeCommand.Commit(session, source.Id, new(12, 0, 1), settings, session.ChangeSerial);
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.Any(n => n.Id == occupied.Id) && session.Document.Notes.All(n => n.Id != pair.Id),
            "tail resize overwrote occupied mirrored head or left stale original companion");
        Reset(); session.AllowOwnerProtectedEdits = false; session.SetOwnerProtection([pair.Id], true);
        try { SymmetricHoldTailResizeCommand.Commit(session, source.Id, new(12, 0, 1), settings, session.ChangeSerial); throw new Exception("locked companion resized"); }
        catch (InvalidOperationException) { }
        Require(session.Document.Notes.Single(n => n.Id == source.Id).EndBeat!.Value.Value == 10 && !session.CanUndo,
            "locked companion caused a partial source edit");
        session.AllowOwnerProtectedEdits = true; session.IsReadOnly = true;
        try { SymmetricHoldTailResizeCommand.Commit(session, source.Id, new(12, 0, 1), settings, session.ChangeSerial); throw new Exception("read-only companion resized"); }
        catch (InvalidOperationException) { }
        session.IsReadOnly = false;
        var central = new ChartNote { Lane = 3, Beat = new(2, 0, 1), EndBeat = new(3, 0, 1) };
        var centerSettings = settings with { End = new(8, 0, 1) };
        var centralPair = CenterSymmetryService.CreateCompanion(central, centerSettings, 7)!;
        session.ReplaceDocument(new ChartDocument { KeyCount = 7, Notes = [central, centralPair] });
        SymmetricHoldTailResizeCommand.Commit(session, central.Id, new(5, 0, 1), centerSettings, session.ChangeSerial);
        Require(session.Document.Notes.Count == 1 && session.Document.Notes[0].Beat.Value == 2 && session.Document.Notes[0].EndBeat!.Value.Value == 6,
            "overlapping same-column tail pair did not become one continuous LN");
        SymmetricHoldTailResizeCommand.Commit(session, central.Id, new(5, 0, 1), centerSettings, session.ChangeSerial);
        Require(session.Document.Notes.Count == 1 && session.Document.Notes[0].Beat.Value == 3 && session.Document.Notes[0].EndBeat!.Value.Value == 5,
            "single self-symmetric LN could not shrink by moving its opposite head");
        Require(session.Undo(), "self-symmetric shrink undo failed");
        SymmetricHoldTailResizeCommand.Commit(session, central.Id, new(4, 0, 1), centerSettings, session.ChangeSerial);
        Require(session.Document.Notes.Count == 1 && session.Document.Notes[0].Beat.Value == 4 && !session.Document.Notes[0].EndBeat.HasValue,
            "self-symmetric LN did not collapse into one Tap at the symmetry axis");
        Require(session.Undo(), "self-symmetric center collapse undo failed");
        SymmetricHoldTailResizeCommand.Commit(session, central.Id, new(3, 0, 1), centerSettings, session.ChangeSerial);
        Require(session.Document.Notes.Count == 1 && session.Document.Notes[0].Beat.Value == 3 && session.Document.Notes[0].EndBeat!.Value.Value == 5,
            "drag across the symmetry axis produced an inverted or missing self-symmetric LN");
        Console.WriteLine("PASS 036 symmetric LN tail pure preview/exact reflected head/property retention, atomic undo/redo, Tap collapse, occupancy, locks/read-only, same-column union");
    }

    public static void SymmetricTailPointer()
    {
        var source = new ChartNote { Lane = 1, Beat = new(8, 0, 1), EndBeat = new(10, 0, 1) };
        var settings = new CenterSymmetrySettings(new(0, 0, 1), new(24, 0, 1), Enumerable.Range(0, 7).ToArray());
        var pair = CenterSymmetryService.CreateCompanion(source, settings, 7)!;
        var session = new EditorSession { SnapDivision = 4 };
        session.ReplaceDocument(new ChartDocument { KeyCount = 7, Notes = [source, pair] }); session.Selection.Add(source.Id);
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Select, SymmetrySettings = settings };
        var window = new Window { Width = 1000, Height = 800, Content = surface }; window.Show(); Program.Pump(30);
        surface.SetZoom(40); surface.SetCurrentBeat(8);
        var target = surface.GetSelectedHoldTailEvidenceTarget()!.SurfacePoint;
        window.MouseDown(target, MouseButton.Left);
        var move = new Point(target.X, surface.Bounds.Height * surface.JudgementLinePosition - 4.13 * surface.PixelsPerBeat);
        window.MouseMove(move, RawInputModifiers.LeftMouseButton);
        var plan = Field<HoldTailResizePlan>(surface, "resizingHoldPlan");
        Require(plan.Notes.Length == 2 && Math.Abs(plan.Notes.Single(n => n.Id == pair.Id).Beat.Value - 11.87) < .001 &&
            session.Document.Notes.Single(n => n.Id == pair.Id).Beat.Value == 14,
            "real tail pointer did not preview both sides continuously without mutating source");
        window.MouseUp(move, MouseButton.Left);
        Require(session.Document.Notes.Single(n => n.Id == source.Id).EndBeat!.Value.Value == 12.25 &&
            session.Document.Notes.Single(n => n.Id == pair.Id).Beat.Value == 11.75,
            "real tail release did not snap and atomically synchronize counterpart");
        Require(session.Undo() && session.Document.Notes.Single(n => n.Id == pair.Id).Beat.Value == 14 && !session.CanUndo,
            "real symmetric tail gesture did not undo in one step");
        window.Close(); Program.Pump(20);
        Console.WriteLine("PASS 036 actual pointer tail drag shows both previews, release-only snap, mirrored head sync, one-step undo");
    }

    public static void InheritedTiming()
    {
        var owner = new ChartDocument { KeyCount = 7, Timing = [new() { Bpm = 120, Delay = -100 }] };
        owner.Normalize();
        var track = new EditorSession { CanEditTiming = false, CanEditMetadata = false };
        track.ReplaceDocument(owner.Clone());
        var hold = new ChartNote { Lane = 2, Beat = new(8, 0, 1), EndBeat = new(10, 0, 1), Sound = "keep.wav", Volume = 37 };
        var tap = new ChartNote { Lane = 4, Beat = new(6, 0, 1) };
        track.Mutate(d => d.Notes.Add(hold.Clone())); track.Mutate(d => d.Notes.Add(tap.Clone()));
        track.Undo(); track.Selection.Add(hold.Id); track.MarkSaved();
        var serial = track.ChangeSerial;
        Require(InheritedTrackTimingService.Synchronize(track, owner) && track.ChangeSerial == serial,
            "identical inherited timing changed the session serial/history");
        owner.Timing[0].Bpm = 240; owner.Timing[0].Delay = -475; owner.Metadata.Bpm = 240;
        Require(InheritedTrackTimingService.Synchronize(track, owner) && track.IsDirty && track.CanUndo && track.CanRedo,
            "timing rebase did not dirty its snapshot or destroyed history");
        void CheckAudio(double expectedHead, double expectedTail)
        {
            var note = track.Document.Notes.Single(n => n.Id == hold.Id); var map = new TimingMap(track.Document.Timing);
            Require(Math.Abs(map.BeatToMilliseconds(note.Beat) - expectedHead) < .001 &&
                Math.Abs(map.BeatToMilliseconds(note.EndBeat!.Value) - expectedTail) < .001,
                "inherited BPM/offset change moved existing LN in audio time");
            Require(note.Sound == "keep.wav" && note.Volume == 37 && track.Document.Timing[0].Bpm == 240 && track.Document.Timing[0].Delay == owner.Timing[0].Delay,
                "inherited rebase lost properties or restored old timing");
        }
        CheckAudio(4100, 5100);
        Require(track.Selection.Contains(hold.Id) && track.Undo() && track.Document.Notes.Count == 0 && !track.CanUndo,
            "timing inheritance added an undo step or lost selection");
        Require(track.Redo(), "timing rebase lost note redo"); CheckAudio(4100, 5100);
        Require(track.Redo() && !track.CanRedo, "timing rebase lost the original redo stack");
        Require(Math.Abs(new TimingMap(track.Document.Timing).BeatToMilliseconds(track.Document.Notes.Single(n => n.Id == tap.Id).Beat) - 3100) < .001,
            "redo restored a note against stale timing");
        var exact = new ChartNote { Lane = 0, Beat = new(0, 1, 7) }; track.Mutate(d => d.Notes.Add(exact));
        owner.Timing.Add(new TimingEvent { Beat = new(2, 0, 1), Effect = TimingEffectKind.Scroll, EffectValue = 2 });
        InheritedTrackTimingService.Synchronize(track, owner);
        Require(track.Document.Notes.Single(n => n.Id == exact.Id).Beat == new BeatPosition(0, 1, 7),
            "SV-only inheritance needlessly rounded exact authored beats");
        owner.Timing[0].Delay = -500; track.BeginInteractiveMutation();
        Require(!InheritedTrackTimingService.Synchronize(track, owner) && track.IsInteractiveMutationActive && track.Document.Timing[0].Delay == -475,
            "timing inheritance interrupted an active pointer edit");
        track.CancelInteractiveMutation(); Require(InheritedTrackTimingService.Synchronize(track, owner), "deferred timing sync did not resume after gesture");
        CheckAudio(4100, 5100);
        var countBeforeFailure = track.Document.Notes.Count; var serialBeforeFailure = track.ChangeSerial; var projectedCount = 0;
        try
        {
            Call(track, "TryProjectInheritedDocumentHistory", new Action<ChartDocument>(d =>
            { d.Notes.Clear(); if (++projectedCount == 2) throw new InvalidOperationException("synthetic projection failure"); }));
            throw new Exception("projection failure fixture did not fail");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        Require(track.Document.Notes.Count == countBeforeFailure && track.ChangeSerial == serialBeforeFailure && track.CanUndo,
            "failed inherited projection changed a live document or history");
        CheckAudio(4100, 5100);
        Console.WriteLine("PASS 036 inherited timing preserves LN/tap audio times, exact SV-only beats, selection, undo/redo order; fast no-op and active-gesture defer");
    }

    public static void PatternCaptureTarget(MainWindow window)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        Wait(model.CreateAsync("CaptureTarget036"));
        Call(window, "ShowEditorSurface", true);
        var tab = model.ActiveTab!;
        tab.Session.Mutate(d => d.Notes.AddRange([
            new ChartNote { Lane = 1, Beat = new(8, 0, 1) },
            new ChartNote { Lane = 4, Beat = new(9, 0, 1), EndBeat = new(10, 0, 1) }
        ]));
        tab.Session.Selection.UnionWith(tab.Session.Document.Notes.Select(n => n.Id));
        Wait((Task)Call(window, "AddEditorTrackAsync", EditorTrackKind.Pattern, null)!);
        Wait((Task)Call(window, "AddEditorTrackAsync", EditorTrackKind.Pattern, null)!);
        var workspace = Field<EditorTrackWorkspace>(window, "activeTrackWorkspace");
        var tracks = workspace.Tracks.Where(t => t.Kind == EditorTrackKind.Pattern).ToArray();
        Require(tracks.Length == 2, "capture fixture did not create two pattern tracks");
        var views = (System.Collections.IDictionary)typeof(MainWindow).GetField("secondaryTrackViews", Hidden)!.GetValue(window)!;
        // A selected, previously focused secondary track must not steal the source.
        tracks[1].Session!.Mutate(d => d.Notes.Add(new ChartNote { Lane = 6, Beat = new(30, 0, 1) }));
        tracks[1].Session!.Selection.Add(tracks[1].Session!.Document.Notes.Single().Id);
        typeof(MainWindow).GetField("activeSecondaryTrack", Hidden)!.SetValue(window, views[tracks[1].Id]);
        var panels = Field<StackPanel>(window, "trackPanels");
        var firstFrame = panels.Children.OfType<EditorTrackPanel>().Single(p => p.TrackKey == "track:" + tracks[0].Id.ToString("N"));
        var capture = firstFrame.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "加入主轨选区");
        tab.Session.IsReadOnly = true;
        capture.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.PumpUntil(() => tracks[0].Session!.Document.Notes.Count == 2);
        tab.Session.IsReadOnly = false;
        Require(tracks[0].Session!.Document.Notes.Any(n => n.Lane == 1 && n.Beat.Value == 8) &&
            tracks[0].Session!.Document.Notes.Any(n => n.Lane == 4 && n.EndBeat?.Value == 10), "track-local capture did not use the main selection at original beats");
        Require(tracks[1].Session!.Document.Notes.Count == 1 && tracks[1].Session!.Document.Notes[0].Beat.Value == 30,
            "capture into the first track changed the last visible pattern track");
        Require(tab.Session.Document.Notes.Count == 2, "capture changed the read-only source");
        Require(tracks[0].Session!.Undo() && tracks[0].Session!.Document.Notes.Count == 0,
            "track-local capture was not one undo entry");
        var reference = workspace.Add(EditorTrackKind.Reference, tab.Session.Document, tab.Session.Document.Clone(), "synthetic-reference036");
        var referenceBpm = reference.Session!.Document.Timing.First(t => t.Bpm.HasValue).Bpm;
        Call(window, "RebuildTrackViews");
        var oldTime = new TimingMap(tracks[1].Session!.Document.Timing).BeatToMilliseconds(tracks[1].Session!.Document.Notes.Single().Beat);
        tab.Session.Mutate(d => { d.Timing.First(t => t.Bpm.HasValue).Bpm = 240; d.Metadata.Bpm = 240; });
        Call(window, "UpdateTrackDocuments", tab);
        Require(tracks.All(t => t.Session!.Document.Timing.First(x => x.Bpm.HasValue).Bpm == 240),
            "main timing change did not propagate into pattern track documents");
        Require(reference.Session.Document.Timing.First(t => t.Bpm.HasValue).Bpm == referenceBpm && reference.Session.IsReadOnly,
            "inherited timing rebased an independent reference chart");
        Require(Math.Abs(new TimingMap(tracks[1].Session!.Document.Timing).BeatToMilliseconds(tracks[1].Session!.Document.Notes.Single().Beat) - oldTime) < .001,
            "main timing propagation moved pre-existing pattern notes in audio time");
        Require(tracks[0].Session!.Redo() && tracks[0].Session!.Document.Notes.Count == 2 &&
            tracks[0].Session!.Document.Timing.First(t => t.Bpm.HasValue).Bpm == 240,
            "pattern redo restored stale timing after main BPM change");
        Wait(workspace.SaveAsync());
        Console.WriteLine("PASS 036 track-local pattern capture targets clicked track, uses main selection despite secondary focus, preserves read-only source, one-step undo");
    }
}

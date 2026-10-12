using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;

internal static class SymmetryUiSpecs
{
    public static void RunPointer(string screenshots)
    {
        var session = new EditorSession(); session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Tap };
        var window = new Window { Width = 1000, Height = 800, Content = surface };
        window.Show(); Program.Pump(50); surface.SetCurrentBeat(4); surface.SetZoom(70);
        var target = surface.GetPlacementEvidenceProbe()!.TapTarget;
        Point At(double beat, double? x = null) => new(x ?? target.SurfacePoint.X, surface.Bounds.Height * surface.JudgementLinePosition - (beat - 4) * surface.PixelsPerBeat - 1);
        void Click(Point point, MouseButton button = MouseButton.Left) { window.MouseDown(point, button); window.MouseUp(point, button); }
        void Drag(Point from, Point to) { window.MouseDown(from, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton); window.MouseUp(to, MouseButton.Left); }
        surface.BeginSymmetryRange(); Click(At(3.04)); Click(At(5.04));
        Require(surface.SymmetrySettings?.Start.Value == 3 && surface.SymmetrySettings.End.Value == 5 && session.Document.Notes.Count == 0,
            $"two snapped range clicks: {surface.SymmetrySettings?.Start} / {surface.SymmetrySettings?.End}, notes={session.Document.Notes.Count}, defining={surface.IsDefiningSymmetry}");
        surface.Tool = TimelineTool.Select;
        Drag(At(5), At(5.5)); Require(surface.SymmetrySettings!.End.Value == 5.5, "top edge did not snap/resize");
        var laneWidth = surface.GridLaneWidth; var left = target.SurfacePoint.X - (target.Lane + .5) * laneWidth;
        Drag(At(4, left), At(4, left + laneWidth));
        Require(surface.SymmetrySettings.Columns.SequenceEqual(Enumerable.Range(1, 6)), "left edge did not adjust participating columns");
        surface.CancelSymmetryRange(); surface.SymmetrySettings = new(BeatPosition.Zero, new(8, 0, 1), Enumerable.Range(0, 7).ToArray());
        surface.Tool = TimelineTool.Tap;
        Drag(At(3.5), At(3.5));
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.Any(n => n.Lane == target.Lane && n.Beat.Value == 3.5) &&
            session.Document.Notes.Any(n => n.Lane == 6 - target.Lane && n.Beat.Value == 4.5), "pointer tap did not create pair");
        Require(session.Undo() && session.Document.Notes.Count == 0, "pair requires more than one undo");
        surface.Tool = TimelineTool.Hold; Drag(At(4), At(3.5));
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.All(n => n.IsHold), "backward LN pair failed");
        using (var image = window.CaptureRenderedFrame()!) image.Save(Path.Combine(screenshots, "symmetry-preview.png"), PngBitmapEncoderOptions.Default);
        session.Undo();
        var old = new ChartNote { Lane = target.Lane, Beat = new(3, 1, 2) };
        var occupied = new ChartNote { Lane = 6 - target.Lane, Beat = new(4, 0, 1) };
        session.Mutate(d => d.Notes.AddRange([old, occupied])); Drag(At(3.5), At(4));
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.Any(n => n.Id == occupied.Id) &&
            session.Document.Notes.Any(n => n.IsHold), "occupied mirror should be skipped while source is replaced");
        session.Undo();
        session.Selection.Clear(); session.Selection.UnionWith([old.Id, occupied.Id]);
        Click(At(3.5), MouseButton.Right);
        Require(session.Document.Notes.Count == 0 && session.Undo() && session.Document.Notes.Count == 2, "right click did not atomically delete selected batch");
        session.Selection.UnionWith([old.Id, occupied.Id]); Click(At(2), MouseButton.Right);
        Require(session.Selection.Count == 0 && session.Document.Notes.Count == 2, "blank right click should clear selection");
        window.Close(); Program.Pump(30);
        Console.WriteLine("PASS direct symmetry range clicks/edge resizing, pairs, occupied-head skip, batch right delete and undo");
    }

    public static void RunWorkbench(MainWindow window)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel"); var session = model.ActiveTab!.Session;
        typeof(MainWindow).GetField("activeSecondaryTrack", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
        Invoke(window, "RefreshFixedTools");
        var button = Find<Button>(window, "Editor.QuickSymmetry");
        var surface = Field<TimelineSurface>(window, "timeline");
        var before = File.ReadAllBytes(model.ActiveTab.SourcePath); var serial = session.ChangeSerial;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(surface.IsDefiningSymmetry && session.ChangeSerial == serial, "quick extension button did not start direct range gesture");
        window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null); window.KeyRelease(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null);
        Require(!surface.IsDefiningSymmetry && surface.SymmetrySettings == null, "F6 failed to cancel range");
        // A transport update between 50 ms inspector ticks must survive an edit.
        Invoke(window, "SetProgrammaticPlaybackBeat", 20.123456d);
        var note = new ChartNote { Lane = 0, Beat = new(50, 1, 4) };
        session.Mutate(d => d.Notes.Add(note)); Program.Pump(500);
        Require(surface.CurrentBeat == 20.123456 && model.ActiveTab.CurrentBeat == 20.123456, "paused non-grid position changed during edit/plan rebuild");
        session.Selection.Clear(); session.Selection.Add(note.Id); Invoke(window, "CopyMixedSelection");
        Program.PumpUntil(() => Field<Task>(window, "clipboardWriteTask").IsCompleted);
        var read = window.Clipboard!.TryGetTextAsync(); Program.PumpUntil(() => read.IsCompleted);
        Require(read.Result!.Contains("[HitObjects]"), "copy did not write note code to system clipboard");
        var count = session.Document.Notes.Count; var paste = (Task)Invoke(window, "PasteNoteClipboardAsync")!;
        Program.PumpUntil(() => paste.IsCompleted);
        Require(session.Document.Notes.Count == count && surface.HasPendingPaste, "clipboard paste committed before click");
        Invoke(window, "CancelNotePaste");
        NotePastePlacement.Commit(session, NotePastePlacement.Preview([note], new BeatPosition(20, 1, 8), 0, 7));
        Require(session.Document.Notes.Count == count + 1, "pending clipboard payload could not be committed");
        session.Undo();
        var external = window.Clipboard!.SetTextAsync("475,192,2000,128,4,2500:0:0:0:55:");
        Program.PumpUntil(() => external.IsCompleted);
        paste = (Task)Invoke(window, "PasteNoteClipboardAsync")!; Program.PumpUntil(() => paste.IsCompleted);
        Require(session.Document.Notes.Count == count && surface.HasPendingPaste, "external paste was not staged");
        var pending = Field<ChartNote[]>(window, "pendingNotePaste");
        Require(pending.Single().IsHold && pending.Single().Lane == 6, "external code ignored in favor of stale notes");
        Invoke(window, "CancelNotePaste");
        var codeBox = Find<TextBox>(window, "Inspector.Note.Code");
        codeBox.Text = "36,192,1234,1,8,2:3:4:37:hit.wav";
        Find<Button>(window, "Inspector.Note.ImportCode").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(session.Document.Notes.Count == count + 1 && surface.CurrentBeat == 20.123456, "Note panel import altered viewport or failed");
        session.Undo(); session.Undo();
        Require(surface.CurrentBeat == 20.123456 && File.ReadAllBytes(model.ActiveTab.SourcePath).SequenceEqual(before), "undo or code import changed position/source");
        var rail = Field<StackPanel>(window, "fixedToolRailBody");
        Require(!rail.GetLogicalDescendants().OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) is "Editor.QuickUndo" or "Editor.QuickRedo" or "Editor.QuickPlayPause"),
            "transport/history duplicate still visible in quick rail");
        Console.WriteLine("PASS quick-extension placement, exact paused cursor, system clipboard copy/paste, original-time import and source isolation");
    }
    private static object? Invoke(MainWindow window, string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Editing;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Rendering;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;

internal static class MapperInteraction035Specs
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object o, string n) => (T)o.GetType().GetField(n, Hidden)!.GetValue(o)!;
    private static object? Call(object o, string n, params object?[] args) => o.GetType().GetMethod(n, Hidden)!.Invoke(o, args);
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    public static void Pointer(string root)
    {
        var session = new EditorSession { SnapDivision = 4 };
        session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Select };
        var window = new Window { Width = 1000, Height = 800, Content = surface }; window.Show(); Program.Pump(30);
        surface.SetZoom(50); surface.SetCurrentBeat(8);
        var probe = surface.GetPlacementEvidenceProbe()!.TapTarget; var laneWidth = surface.GridLaneWidth;
        var left = probe.SurfacePoint.X - (probe.Lane + .5) * laneWidth;
        double Y(double beat) => surface.Bounds.Height * surface.JudgementLinePosition - (beat - 8) * surface.PixelsPerBeat;
        Point At(int lane, double beat) => new(left + (lane + .5) * laneWidth, Y(beat));
        ChartNote Hold() => new() { Lane = 1, Beat = new(8,0,1), EndBeat = new(10,0,1), Sound = "keep.wav", Volume = 37 };
        ChartNote Tap() => new() { Lane = 3, Beat = new(9,0,1) };
        void Reset(params ChartNote[] notes)
        {
            surface.CancelInteraction(); session.IsReadOnly = false;
            session.ReplaceDocument(new ChartDocument { KeyCount = 7, Notes = notes.ToList() });
            session.Selection.Clear(); surface.SetCurrentBeat(8); surface.Tool = TimelineTool.Select;
        }
        void SelectAll() => session.Selection.UnionWith(session.Document.Notes.Select(n => n.Id));
        void Drag(Point from, Point to)
        { window.MouseDown(from, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton); window.MouseUp(to, MouseButton.Left); }
        Reset(Hold(), Tap()); SelectAll(); Require(surface.BeginStretch(), "stretch did not start");
        using (var image = window.CaptureRenderedFrame()!) image.Save(Path.Combine(root,"stretch035.png"),PngBitmapEncoderOptions.Default);
        var serial = session.ChangeSerial;
        var from = At(2,10) - new Vector(0,surface.NoteHeight+6); var to = from - new Vector(0,2*surface.PixelsPerBeat);
        window.MouseDown(from, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Require(session.ChangeSerial == serial && session.Document.Notes.First(n=>n.IsHold).EndBeat!.Value.Value == 10, "stretch preview mutated the document");
        window.MouseUp(to, MouseButton.Left);
        Require(session.Document.Notes.First(n=>n.IsHold).EndBeat!.Value.Value == 12 && session.Document.Notes.First(n=>!n.IsHold).Beat.Value == 10, "upper edge did not stretch LN and interior note together");
        Require(session.Document.Notes.First(n=>n.IsHold).Sound == "keep.wav" && session.Document.Notes.First(n=>n.IsHold).Volume == 37, "stretch lost note properties");
        Require(session.Undo() && session.Document.Notes.First(n=>n.IsHold).EndBeat!.Value.Value == 10 && session.Document.Notes.First(n=>!n.IsHold).Beat.Value == 9, "stretch was not one undo entry");
        Require(session.Redo() && session.Document.Notes.First(n=>!n.IsHold).Beat.Value == 10, "stretch redo failed");
        Reset(Hold(),Tap()); SelectAll(); surface.BeginStretch();
        Drag(At(2,8)+new Vector(0,6), At(2,9)+new Vector(0,6));
        Require(session.Document.Notes.First(n=>n.IsHold).Beat.Value == 9 && session.Document.Notes.First(n=>!n.IsHold).Beat.Value == 9.5 && session.Document.Notes.First(n=>n.IsHold).EndBeat!.Value.Value == 10, "lower edge did not preserve opposite boundary");
        Reset(Hold(),Tap()); SelectAll(); surface.BeginStretch();
        Drag(At(2,9),At(3,10));
        Require(session.Document.Notes.Any(n=>n.Lane==2 && n.Beat.Value==9 && n.EndBeat!.Value.Value==11) && session.Document.Notes.Any(n=>n.Lane==4 && n.Beat.Value==10), "stretch frame body did not translate time and lanes");
        Reset(Hold(),Tap()); SelectAll(); surface.BeginStretch();
        window.MouseDown(At(2,10)-new Vector(0,surface.NoteHeight+6), MouseButton.Left); window.MouseMove(At(2,12)-new Vector(0,surface.NoteHeight+6),RawInputModifiers.LeftMouseButton);
        window.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null); window.KeyRelease(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null); window.MouseUp(At(2,12),MouseButton.Left);
        Require(session.Document.Notes.First(n=>n.IsHold).EndBeat!.Value.Value==10 && surface.IsStretching, "Esc did not abandon drag while retaining frame");
        window.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null); window.KeyRelease(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,null); Require(!surface.IsStretching,"second Esc did not exit stretch");
        session.IsReadOnly=true; Require(!surface.BeginStretch(),"read-only stretch enabled");session.IsReadOnly=false;
        var exact = NoteStretchService.Preview([new ChartNote{Beat=new(0,1,7)}],new(0,0,1),new(1,0,1),new(2,0,1),new(4,0,1));
        Require(exact[0].Beat==new BeatPosition(2,2,7),"stretch drifted from exact sevenths");
        // Old automatic pair selection must not redirect a right click on an unrelated note.
        var old = new ChartNote{Lane=0,Beat=new(9,0,1)}; var newer=Tap(); var pair=new ChartNote{Lane=6,Beat=new(10,0,1)};
        Reset(old,newer,pair);session.Selection.UnionWith([newer.Id,pair.Id]);surface.Tool=TimelineTool.Tap;
        window.MouseDown(At(0,9)-new Vector(0,2),MouseButton.Right);window.MouseUp(At(0,9)-new Vector(0,2),MouseButton.Right);
        Require(session.Document.Notes.Count==2 && session.Document.Notes.All(n=>n.Id!=old.Id),"right click deleted stale automatic selection instead of pointed note");
        window.MouseDown(At(3,9)-new Vector(0,2),MouseButton.Right);window.MouseUp(At(3,9)-new Vector(0,2),MouseButton.Right);
        Require(session.Document.Notes.Count==0,"right click inside explicit batch did not delete the batch");
        foreach(var tool in new[]{TimelineTool.Tap,TimelineTool.Hold})
        {
            Reset();surface.Tool=tool;var bottom=new Point(At(2,8).X,surface.Bounds.Height-8);
            window.MouseDown(bottom,MouseButton.Left);Program.Pump(260);
            Require(surface.CurrentBeat==8,"placing near bottom edge moved paused time");
            window.MouseMove(bottom-new Vector(0,15),RawInputModifiers.LeftMouseButton);Program.Pump(150);
            Require(surface.CurrentBeat==8,"drawing within bottom band auto-scrolled");
            window.MouseUp(bottom-new Vector(0,15),MouseButton.Left);
        }
        Reset();surface.SymmetrySettings=new(new(0,0,1),new(8,0,1),Enumerable.Range(0,7).ToArray());
        WindowsProEditorCommand.PlaceHold(session,3,new(2,0,1),new(5,0,1),new(0,6),BasicBeatPlacementPolicy.PreserveAuthoredBeat,symmetry:surface.SymmetrySettings);
        Require(session.Document.Notes.Count==1 && session.Document.Notes[0].Beat.Value==2 && session.Document.Notes[0].EndBeat!.Value.Value==6,"overlapping reflected LN did not merge");
        Require(session.Undo() && session.Document.Notes.Count==0,"symmetry union undo split into multiple edits");
        WindowsProEditorCommand.PlaceHold(session,3,new(1,0,1),new(2,0,1),new(0,6),BasicBeatPlacementPolicy.PreserveAuthoredBeat,symmetry:surface.SymmetrySettings);
        Require(session.Document.Notes.Count==2,"non-overlapping reflected LNs were merged");
        Reset();surface.BeginPatternPaste([new ChartNote{Lane=0,Beat=new(0,0,1)},new ChartNote{Lane=1,Beat=new(2,0,1)}]);surface.MovePendingPaste(At(1,8));
        Program.Pump(20);
        using (var image = window.CaptureRenderedFrame()!) image.Save(Path.Combine(root,"pattern-hint035.png"),PngBitmapEncoderOptions.Default);
        Require(!surface.HandlePatternPasteKey(Key.Space) && surface.HandlePatternPasteKey(Key.Enter),"pattern still consumes Space or Enter does not pin start");
        surface.MovePendingPaste(At(1,12));surface.HandlePatternPasteKey(Key.Enter);
        Require(session.Document.Notes.Max(n=>n.Beat.Value)==12,"Enter endpoint placement failed");
        window.Close();Program.Pump(20);
        Console.WriteLine("PASS 035 stretch real pointer preview/edges/translation/cancel/undo/exact grid, right-click targeting, bottom placement stability, symmetry LN union, Enter pattern endpoint");
    }

    public static void Workbench(MainWindow window, string root)
    {
        var model=Field<MainWindowViewModel>(window,"viewModel");Wait(model.CreateAsync("Interaction035"));Call(window,"ShowEditorSurface",true);Program.Pump(100);
        Button FindButton(string id)=>window.GetLogicalDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)==id);
        Require(!window.GetLogicalDescendants().OfType<Button>().Any(b=>AutomationProperties.GetAutomationId(b)=="Editor.ToPatternTrack"),"removed capture command remains permanent");
        var rail=Field<StackPanel>(window,"fixedToolRailBody");var symmetry=FindButton("Editor.QuickSymmetry");var pattern=FindButton("Editor.PatternLibrary");var stretch=FindButton("Editor.QuickStretch");
        Require(rail.Children.IndexOf(pattern)==rail.Children.IndexOf(symmetry)+1 && rail.Children.IndexOf(stretch)==rail.Children.IndexOf(pattern)+1,"pattern and transform controls not grouped together");
        Require(rail.Children.IndexOf(symmetry)>rail.Children.IndexOf(FindButton("Editor.ToolRail.Erase"))+2,"extended actions remain among primary tools");
        var main=Field<TimelineSurface>(window,"timeline");model.ActiveTab!.Session.Mutate(d=>d.Notes.Add(new ChartNote{Lane=2,Beat=new(8,0,1)}));model.ActiveTab.Session.Selection.Add(model.ActiveTab.Session.Document.Notes[0].Id);Call(window,"UpdateSelectionUi");
        Require(stretch.IsEnabled,"stretch button not enabled after selecting notes");
        Call(window,"SetTimelineToolFromShortcut",TimelineTool.Tap); stretch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Require(main.IsStretching && main.Tool == TimelineTool.Select,"visible stretch button did not start frame with selection tool");
        Require(!Equals(FindButton("Editor.ToolRail.Select").Background, FindButton("Editor.ToolRail.Tap").Background),"stretch did not update selected tool highlight");
        Call(window,"SetTimelineToolFromShortcut",TimelineTool.Select);Require(!main.IsStretching,"clicking selection tool did not exit stretch");
        // A real blank-board right click captures insertion before main; menu item executes the insertion.
        var canvas=Field<Grid>(window,"trackBoardCanvas");var panels=Field<StackPanel>(window,"trackPanels");var frames=panels.Children.OfType<EditorTrackPanel>().ToArray();
        Call(window,"RestoreBoardPanAfterLayout",-300d);Program.Pump(100);
        var mainFrame=frames.Single(f=>f.TrackKey=="main");var oldMainX=mainFrame.TranslatePoint(new Point(),window)!.Value.X;
        var click=mainFrame.TranslatePoint(new Point(-30,200),window)!.Value;
        window.MouseDown(click,MouseButton.Right);window.MouseUp(click,MouseButton.Right);Program.Pump(30);
        var menu=Field<ContextMenu>(window,"addTrackContextMenu");Require(menu.IsOpen,"blank-board context menu did not open");
        var add=menu.ItemsSource!.Cast<MenuItem>().Single(i=>AutomationProperties.GetAutomationId(i)=="Editor.AddTrack.Draft");add.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Program.PumpUntil(()=>Field<EditorTrackWorkspace>(window,"activeTrackWorkspace").Tracks.Any(t=>t.Kind==EditorTrackKind.Draft));Program.Pump(120);
        var order=panels.Children.OfType<EditorTrackPanel>().ToArray();Require(order[0].TrackKey.StartsWith("track:") && order[1].TrackKey=="main","left-side add appended the track on the right");
        Require(Math.Abs(mainFrame.TranslatePoint(new Point(),window)!.Value.X-oldMainX)<2,"left-side insertion shifted the existing main track on screen");
        Require(Field<EditorTrackWorkspace>(window,"activeTrackWorkspace").BoardOrder[0]==order[0].TrackKey,"insertion order was not persisted");
        menu.Close(); Program.Pump(20);
        using (var image = window.CaptureRenderedFrame()!) image.Save(Path.Combine(root,"toolbar035.png"),PngBitmapEncoderOptions.Default);
        Console.WriteLine("PASS 035 extended control grouping/removal, actual stretch button, left-blank context insertion/order/viewport preservation");
    }
}

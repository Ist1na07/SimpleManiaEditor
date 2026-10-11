using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Application.Exporting;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Application.Editing;
using LumaChart.Core.Models;

internal static class EditorInteraction021Specs
{
    public static void RunPointer()
    {
        var session = new EditorSession(); session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Hold };
        var window = new Window { Width = 1000, Height = 800, Content = surface }; window.Show(); Program.Pump(40);
        surface.SetZoom(140); surface.SetCurrentBeat(4);
        var target = surface.GetPlacementEvidenceProbe()!.TapTarget;
        Point At(double beat) => new(target.SurfacePoint.X, surface.Bounds.Height * surface.JudgementLinePosition - (beat - 4) * 140 - .01);
        void Drag(Point start, Point end) { window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left); }
        var old = new ChartNote { Lane = target.Lane, Beat = new(4, 0, 1) }; session.Mutate(d => d.Notes.Add(old));
        Drag(At(4) + new Vector(0, 3), At(5));
        Require(session.Document.Notes.Count == 1 && session.Document.Notes[0].IsHold && session.Document.Notes[0].Id != old.Id,
            "drawing below snapped head did not atomically replace tap with LN"); session.Undo();
        surface.SymmetrySettings = new(new(3, 0, 1), new(5, 0, 1), Enumerable.Range(0, 7).ToArray());
        Drag(At(5), At(4.5));
        Require(surface.SymmetrySettings.End.Value == 5 && session.Document.Notes.Any(n => n.IsHold), "Hold at time border resized symmetry instead of drawing");
        session.Undo(); surface.Tool = TimelineTool.Tap;
        Drag(At(3), At(3));
        Require(surface.SymmetrySettings.Start.Value == 3 && session.Document.Notes.Count > 1, "Tap at time border was swallowed");
        session.Undo(); surface.Tool = TimelineTool.Select;
        Drag(At(5), At(5.5)); Require(surface.SymmetrySettings.End.Value == 5.5, "Select no longer resizes time border");
        window.Close(); Program.Pump(20);
        Console.WriteLine("PASS below-anchor LN replacement and symmetry borders by tool");
    }

    public static void RunWorkbench(MainWindow window)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel"); var surface = Field<TimelineSurface>(window, "timeline");
        var rail = Field<StackPanel>(window, "fixedToolRailBody");
        Require(Find<Button>(window, "Editor.Extension.Toggle").GetLogicalAncestors().Contains(rail), "note extensions remain in top toolbar");
        Require(!Field<TabControl>(window, "editorInspectorTabs").Items.Cast<TabItem>().Contains(Field<TabItem>(window, "groupInspectorTab")), "disabled group tab remains");
        Require(!window.GetLogicalDescendants().Any(c => c is CheckBox box && AutomationProperties.GetAutomationId(box) is "Settings.GlobalDensity" or "Settings.GlobalDifficulty"), "duplicate overview settings remain");
        var modes = Find<ComboBox>(window, "Settings.EditorMode");
        Require(modes.SelectedIndex == 0 && surface.HideSvEditing, "minimal osu mode is not default");
        modes.SelectedIndex = 1; Program.Pump(40);
        Require(!surface.HideSvEditing && EditorPresentationSettingsStore.LoadMinimalMode() == false, "full mode was not applied/persisted");
        modes.SelectedIndex = 0; Program.Pump(40);
        var keys = Field<WindowsEditorShortcutSettings>(window, "shortcutSettings");
        Require(keys.Bindings["Mirror"] == "Ctrl+H" && keys.Bindings["Flip"] == "Ctrl+G" && keys.Bindings["AddBookmark"] == "Ctrl+B", "new default shortcuts missing");
        var zoom = Find<Slider>(window, "Editor.Adjustment.Zoom"); var beforeZoom = surface.PixelsPerBeat;
        zoom.Value = 3.5; Require(Math.Abs(surface.PixelsPerBeat - 210) < .01, "zoom rounds instead of interpolating");
        // Test actual thumb gesture: tick snapping must stay disabled while held.
        Program.Pump(50); var thumb = zoom.GetVisualDescendants().OfType<Thumb>().First();
        var thumbPoint = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
        window.MouseDown(thumbPoint, MouseButton.Left); window.MouseMove(thumbPoint + new Vector(7, 0), RawInputModifiers.LeftMouseButton);
        Require(!zoom.IsSnapToTickEnabled && Math.Abs(zoom.Value - Math.Round(zoom.Value)) > .01, "thumb drag snapped to preset");
        window.MouseUp(thumbPoint + new Vector(7, 0), MouseButton.Left); surface.SetZoom(beforeZoom);
        // A track click still selects a preset even after continuous thumb dragging.
        var track = zoom.GetVisualDescendants().OfType<Track>().First();
        var trackPoint = track.TranslatePoint(new Point(track.Bounds.Width * .58, track.Bounds.Height / 2), window)!.Value;
        window.MouseDown(trackPoint, MouseButton.Left); window.MouseUp(trackPoint, MouseButton.Left);
        Require(Math.Abs(zoom.Value - Math.Round(zoom.Value)) < .00001, "track click did not select a discrete preset"); surface.SetZoom(beforeZoom);
        var session = model.ActiveTab!.Session; var document = session.Document.Clone();
        var tap = new ChartNote { Lane = 0, Beat = new(42, 0, 1) }; var hold = new ChartNote { Lane = 6, Beat = new(44, 0, 1), EndBeat = new(46, 0, 1) };
        session.Mutate(d => d.Notes.AddRange([tap, hold])); session.Selection.Clear(); session.Selection.UnionWith([tap.Id, hold.Id]); surface.Focus();
        Program.Stroke(window, Key.H, RawInputModifiers.Control);
        Require(session.Document.Notes.Single(n => n.Id == tap.Id).Lane == 6, "Ctrl+H did not mirror selected notes");
        session.Selection.UnionWith([tap.Id, hold.Id]); Program.Stroke(window, Key.G, RawInputModifiers.Control);
        Require(session.Document.Notes.Single(n => n.Id == tap.Id).Beat.Value == 46 && session.Document.Notes.Single(n => n.Id == hold.Id).EndBeat!.Value.Value == 44,
            "Ctrl+G did not flip time/LN endpoints");
        session.ReplaceDocument(document); Program.Pump(40);
        var density = Field<EditorOverviewSurface>(window, "editorOverview"); var difficulty = Field<DifficultyOverviewSurface>(window, "difficultyOverview");
        var originalBeat = surface.CurrentBeat; Invoke(window, "SetProgrammaticPlaybackBeat", 20000d);
        window.KeyPress(Key.B, RawInputModifiers.Control, PhysicalKey.B, "b"); window.KeyRelease(Key.B, RawInputModifiers.Control, PhysicalKey.B, "b"); Program.Pump(80);
        Require(model.ActiveTab!.NavigationMarkers.Any(m => Math.Abs(m.Beat.Value - 20000) < .001), "Ctrl+B did not add current marker");
        Require(density.DisplayEndBeat == difficulty.GetDiagnostics().EndBeat && Math.Abs(density.Bounds.Height - difficulty.Bounds.Height) < .01,
            "global overviews have different scale/heights after marker");
        var point = density.TranslatePoint(new Point(density.Bounds.Width - 13, 0), window)!.Value;
        window.MouseDown(point + new Vector(0, 1), MouseButton.Right); window.MouseUp(point + new Vector(0, 1), MouseButton.Right); Program.Pump(40);
        Require(!model.ActiveTab.NavigationMarkers.Any(m => Math.Abs(m.Beat.Value - 20000) < .001), "right click did not delete marker");
        Invoke(window, "SetProgrammaticPlaybackBeat", originalBeat);
        var export = new ChartExportDialog(1, 1, 6.3, 4.2);
        var completed = export.ShowDialog<bool>(window); Program.Pump(40);
        Require(!export.GetLogicalDescendants().OfType<TextBox>().Any(b => (AutomationProperties.GetAutomationId(b) ?? "").Contains("Difficulty")), "export has duplicate OD/HP controls");
        Find<Button>(export, "Export.Osu.Dialog.Confirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.PumpUntil(() => completed.IsCompleted);
        Require(export.Options is { Format: ChartExportFormat.OsuMania, OsuOverallDifficulty: 6.3, OsuHpDrainRate: 4.2 }, "osu export did not preserve document OD/HP");
        Console.WriteLine("PASS osu modes, toolbar cleanup, smooth thumb drag, Ctrl+B and shared overview range/right-delete");
    }
    private static object? Invoke(MainWindow w, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, args);
    private static T Field<T>(MainWindow w, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

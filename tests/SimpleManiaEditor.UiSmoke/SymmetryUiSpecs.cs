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
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;

internal static class SymmetryUiSpecs
{
    public static void RunPointer(string screenshots)
    {
        var session = new EditorSession(); session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Tap,
            SymmetrySettings = new(BeatPosition.Zero, new(8, 0, 1), Enumerable.Range(0, 7).ToArray()) };
        var window = new Window { Width = 1000, Height = 800, Content = surface };
        window.Show(); Program.Pump(50); surface.SetCurrentBeat(4);
        var target = surface.GetPlacementEvidenceProbe()!.TapTarget;
        Point At(double beat) => new(target.SurfacePoint.X, surface.Bounds.Height * surface.JudgementLinePosition - (beat - 4) * surface.PixelsPerBeat - .01);
        void Drag(double from, double to)
        {
            window.MouseDown(At(from), MouseButton.Left); window.MouseMove(At(to), RawInputModifiers.LeftMouseButton); window.MouseUp(At(to), MouseButton.Left);
        }
        Drag(3.5, 3.5);
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.Any(n => n.Lane == target.Lane && n.Beat.Value == 3.5) &&
            session.Document.Notes.Any(n => n.Lane == 6 - target.Lane && n.Beat.Value == 4.5), "pointer tap did not create exact pair");
        Require(session.Undo() && session.Document.Notes.Count == 0, "pointer pair requires more than one undo");
        surface.Tool = TimelineTool.Hold; Drag(4, 3.5);
        Require(session.Document.Notes.Count == 2 && session.Document.Notes.All(n => n.IsHold) &&
            session.Document.Notes.Any(n => n.Beat.Value == 4 && n.EndBeat!.Value.Value == 4.5), "backward LN pair did not reverse both endpoints");
        using (var image = window.CaptureRenderedFrame()!) image.Save(Path.Combine(screenshots, "symmetry-preview.png"), PngBitmapEncoderOptions.Default);
        Require(session.Undo() && session.Document.Notes.Count == 0, "LN pair is not atomic");
        // Conflict must preserve both the old clicked note and the old target.
        var old = new ChartNote { Lane = target.Lane, Beat = new(3, 1, 2) };
        session.Mutate(d => d.Notes.AddRange([old, new ChartNote { Lane = 6 - target.Lane, Beat = new(4, 0, 1) }]));
        var serial = session.ChangeSerial; Drag(3.5, 4);
        Require(session.ChangeSerial == serial && session.Document.Notes.Any(n => n.Id == old.Id), "conflict partly replaced source note");
        var migrated = new WindowsEditorShortcutSettings(new Dictionary<string, string> { ["ToolTap"] = "F6" });
        Require(migrated.Bindings["ToolTap"] == "F6" && migrated.Bindings["ToggleSymmetry"] == "", "new F6 stole an existing custom binding");
        window.Close(); Program.Pump(30);
        Console.WriteLine("PASS symmetry pointer tap/backward-LN pairs, atomic replacement rejection, undo and F6 migration");
    }

    public static void RunWorkbench(MainWindow window)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel"); var session = model.ActiveTab!.Session;
        typeof(MainWindow).GetField("activeSecondaryTrack", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
        typeof(MainWindow).GetMethod("RefreshFixedTools", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        var button = Find<Button>(window, "Editor.ToolRail.Symmetry");
        var before = File.ReadAllBytes(model.ActiveTab.SourcePath); var serial = session.ChangeSerial;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.Pump(50);
        var flyout = Field<Flyout>(window, "symmetryFlyout"); var panel = (Control)flyout.Content!;
        Find<TextBox>(panel, "Symmetry.Start").Text = "8"; Find<TextBox>(panel, "Symmetry.End").Text = "16";
        Find<CheckBox>(panel, "Symmetry.Enabled").IsChecked = true;
        Find<Button>(panel, "Symmetry.Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.Pump(50);
        var surface = Field<TimelineSurface>(window, "timeline");
        Require(surface.SymmetrySettings?.Start.Value == 8 && surface.SymmetrySettings.End.Value == 16 && session.ChangeSerial == serial,
            "tool settings changed chart history or failed to apply");
        var restored = CenterSymmetryStateStore.Load(model.ActiveTab.SourcePath, "main", 7);
        Require(restored.Enabled && restored.Settings.Start.Value == 8 && restored.Settings.End.Value == 16,
            "tool range and enabled state did not persist");
        CenterSymmetryStateStore.Save(model.ActiveTab.SourcePath, "draft-test", new(false,
            new(new(2, 0, 1), new(4, 0, 1), new[] { 0, 6 })));
        Require(CenterSymmetryStateStore.Load(model.ActiveTab.SourcePath, "draft-test", 7).Settings.Start.Value == 2 &&
            CenterSymmetryStateStore.Load(model.ActiveTab.SourcePath, "main", 7).Settings.Start.Value == 8,
            "draft tool state overwrote main tool range");
        Require(File.ReadAllBytes(model.ActiveTab.SourcePath).SequenceEqual(before), "tool settings modified native source");
        window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null); window.KeyRelease(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null); Require(surface.SymmetrySettings == null, "F6 did not turn off symmetry");
        window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null); window.KeyRelease(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null); Require(surface.SymmetrySettings != null, "F6 did not restore symmetry");
        var note = new ChartNote { Lane = 0, Beat = new(9, 0, 1) }; var count = session.Document.Notes.Count;
        session.Mutate(d => d.Notes.Add(note)); session.Selection.Clear(); session.Selection.Add(note.Id);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.Pump(50);
        panel = (Control)Field<Flyout>(window, "symmetryFlyout").Content!;
        Find<Button>(panel, "Symmetry.Complete").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(session.Document.Notes.Count == count + 2 && session.Document.Notes.Any(n => n.Lane == 6 && n.Beat.Value == 15), "complete-selection action failed");
        session.Undo(); session.Undo();
        Field<Flyout>(window, "symmetryFlyout").Hide();
        surface.Focus(); window.KeyPress(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null); window.KeyRelease(Key.F6, RawInputModifiers.None, PhysicalKey.F6, null); Require(surface.SymmetrySettings == null, "test failed to disable tool");
        Console.WriteLine("PASS visible symmetry setup, per-chart sidecar, F6 toggle and one-undo selection completion");
    }
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

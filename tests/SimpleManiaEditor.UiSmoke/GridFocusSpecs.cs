using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;
using LumaChart.Core.IO;

internal static class GridFocusSpecs
{
    public static void Run(MainWindow window)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var timeline = Field<TimelineSurface>(window, "timeline");
        var text = Field<TextBox>(window, "metadataOffsetBox");
        Field<TabControl>(window, "editorInspectorTabs").SelectedItem = Field<TabItem>(window, "timingInspectorTab");
        Program.Pump(50);
        text.Focus(); Require(ReferenceEquals(window.FocusManager!.GetFocusedElement(), text), "test input did not focus");
        var board = Field<Control>(window, "trackBoardCanvas");
        var scroller = Field<ScrollViewer>(window, "trackScroller");
        Field<ScrollBar>(window, "trackBoardScrollbar").Value = 0; Program.Pump(50);
        var blank = scroller.TranslatePoint(new Point(25, 100), window)!.Value;
        window.MouseDown(blank, MouseButton.Left); window.MouseUp(blank, MouseButton.Left);
        Require(!ReferenceEquals(window.FocusManager.GetFocusedElement(), text), "blank board click left the input focused");
        var session = model.ActiveTab!.Session;
        session.Mutate(doc => doc.Notes.Add(new ChartNote { Lane = 6, Beat = BeatPosition.FromDouble(50.249, 1_000_000) }));
        var note = session.Document.Notes.Single(n => Math.Abs(n.Beat.Value - 50.249) < .00001);
        session.SnapDivision = 4; session.Selection.Clear(); session.Selection.Add(note.Id);
        var button = window.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Editor.Grid.SnapSelected");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(session.Document.Notes.Single(n => n.Id == note.Id).Beat.Value == 50.25, "real snap action failed");
        session.Undo(); Require(session.Document.Notes.Single(n => n.Id == note.Id).Beat.Value == 50.249, "real snap action was not undoable");
        session.Undo(); // Remove test note.
        typeof(MainWindow).GetMethod("RestoreBoardPanAfterLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [0d]);
        Program.Pump(50);
        VerifyDefaultImport(window, model);
        Console.WriteLine("PASS blank-space input focus dismissal and explicit selected-note snapping/undo");
    }
    private static void VerifyDefaultImport(MainWindow window, MainWindowViewModel model)
    {
        var originalId = model.ActiveTab!.Id;
        var path = Path.Combine(Path.GetTempPath(), "sme-grid-" + Guid.NewGuid().ToString("N") + ".osu");
        const string source = "osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n0,500,4,0,0,100,1,0\n[HitObjects]\n36,192,209,1,0,0:0:0:0:\n";
        File.WriteAllText(path, source);
        var open = (Task<bool>)typeof(MainWindow).GetMethod("OpenChartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [path])!;
        Program.PumpUntil(() => open.IsCompleted); Require(open.Result, "default native import failed");
        var document = model.ActiveTab!.Session.Document;
        Require(document.Notes.Single().Beat.CompareTo(new BeatPosition(0, 5, 12)) == 0,
            "osu interaction profile still bypasses import grid calibration");
        Require(File.ReadAllText(path) == source && OsuSourceEnvelope.TrySerialize(document, null, null, out var saved) && saved == source,
            "default calibrated import changed original milliseconds on an untouched save");
        var close = model.CloseAsync(model.ActiveTab.Id, false); Program.PumpUntil(() => close.IsCompleted);
        Require(close.Result, "clean import test could not close"); model.Activate(originalId); Program.Pump(50);
    }
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

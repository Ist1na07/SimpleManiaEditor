using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

internal static class EditorInteraction023Specs
{
    public static void RunPointer()
    {
        var session = new EditorSession(); var tap = new ChartNote { Lane = 0, Beat = new(4, 0, 1) };
        var settings = new CenterSymmetrySettings(new(3, 0, 1), new(7, 0, 1), Enumerable.Range(0, 7).ToArray());
        var pair = CenterSymmetryService.CreateCompanion(tap, settings, 7)!;
        session.ReplaceDocument(new ChartDocument { KeyCount = 7, Notes = [tap, pair] });
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Select, SymmetrySettings = settings };
        var window = new Window { Width = 1000, Height = 800, Content = surface }; window.Show(); Program.Pump(40);
        surface.SetZoom(70); surface.SetCurrentBeat(4); Program.Pump(20);
        var bounds = (Rect)typeof(TimelineSurface).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(m => m.Name == "GetNoteBounds" && m.GetParameters().Length == 2)
            .Invoke(surface, [session.Document.Notes.Single(n => n.Id == tap.Id),
                typeof(TimelineSurface).GetMethod("GetGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [7])])!;
        var gridY = surface.Bounds.Height * surface.JudgementLinePosition;
        var thickness = (double)typeof(TimelineSurface).GetMethod("GridLineHalfThickness", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [tap.Beat])!;
        Require(Math.Abs(bounds.Bottom - (gridY - thickness)) < .00001, "note bottom does not meet grid upper edge");
        window.MouseDown(bounds.Center, MouseButton.Right); window.MouseUp(bounds.Center, MouseButton.Right);
        Require(session.Document.Notes.Count == 0, "right click did not delete the symmetric pair");
        Require(session.Undo() && session.Document.Notes.Count == 2, "right deletion is not one undo");
        window.Close(); Program.Pump(20);
        Console.WriteLine("PASS symmetric right-click deletion and note/grid edge alignment");
    }

    public static void RunWorkbench(MainWindow window, string screenshots)
    {
        Find<TabItem>(window, "Inspector.SettingsTab").IsSelected = true; Program.Pump(40);
        var search = Find<TextBox>(window, "Settings.Search");
        var appearance = Find<Expander>(window, "Settings.Appearance");
        // Prior test expands appearance; exercise search's temporary expansion explicitly.
        appearance.IsExpanded = false;
        Click("Settings.Navigate.2"); Program.Pump(30);
        Require(appearance.IsEffectivelyVisible && !appearance.IsExpanded, "appearance category did not open");
        search.Text = "判定线"; Program.Pump(30);
        Require(appearance.IsEffectivelyVisible && appearance.IsExpanded, "search did not expose folded matching options");
        search.Text = "unmatchable-setting-023"; Program.Pump(20);
        Require(!appearance.IsEffectivelyVisible, "unmatched section remained visible");
        search.Text = ""; Program.Pump(20);
        Require(appearance.IsEffectivelyVisible && !appearance.IsExpanded, "clearing search did not restore expansion/category");
        Click("Settings.Navigate.0"); Program.Pump(20);
        Require(!appearance.IsEffectivelyVisible, "category navigation leaves unrelated settings visible");
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "settings-navigation-023.png"), PngBitmapEncoderOptions.Default);
        var settings = new WindowsEditorShortcutSettings();
        Require(WindowsEditorShortcutGesture.Resolve(settings, Key.F5, KeyModifiers.None, true)?.Id == "Playtest", "F5 not available from option input");
        Require(WindowsEditorShortcutRouting.Resolve("Playtest").Route == WindowsEditorShortcutRoute.Playtest, "F5 command does not route to playtest");
        Console.WriteLine("PASS settings categories, search, temporary fold expansion and configurable F5 routing");
        void Click(string id) => Find<Button>(window, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

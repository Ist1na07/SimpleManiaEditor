using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;

internal static class OptionControlsSpecs
{
    internal static void Run(MainWindow window, string screenshots)
    {
        var timeline = Field<TimelineSurface>(window, "timeline");
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var transport = Field<WindowsAudioTransportViewModel>(window, "audioTransport");
        var grid = Field<ComboBox>(window, "fixedGridPicker");
        grid.SelectedItem = 4;
        Program.Pump(50);
        var beat = timeline.CurrentBeat;
        Wheel(window, grid, 1);
        Require(model.ActiveTab!.Session.SnapDivision == 5, "grid dropdown wheel did not select quintuplets");
        grid.SelectedItem = 4;
        var rate = Field<ComboBox>(window, "fixedRatePicker");
        rate.SelectedItem = 1d; Program.PumpUntil(() => transport.PlaybackRate == 1);
        Wheel(window, rate, 1); Program.PumpUntil(() => Math.Abs(transport.PlaybackRate - 1.1) < .001);
        Require(timeline.CurrentBeat == beat, "rate wheel navigated timeline");
        rate.SelectedItem = 1d; Program.PumpUntil(() => transport.PlaybackRate == 1);
        timeline.SetZoom(280); var zoom = Field<Slider>(window, "adjustmentZoomSlider");
        Wheel(window, zoom, 1); Require(timeline.PixelsPerBeat == 560, "zoom wheel did not select next preset");
        timeline.SetZoom(280);
        foreach (var id in new[] { "Editor.Grid.Division.Reset", "Editor.Playback.Rate.Reset", "Editor.Adjustment.Zoom.Reset", "Editor.Board.Scrollbar.Reset", "Metadata.OverallDifficulty.Reset" })
            Require(!window.GetLogicalDescendants().OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == id), "transient editing option has reset: " + id);
        var height = Field<Slider>(window, "noteHeightSlider");
        height.Value = 40;
        Require(Reset(window, "Settings.NoteHeight").IsVisible, "appearance option reset missing");
        Click(Reset(window, "Settings.NoteHeight"));
        Require(height.Value == 28 && !Reset(window, "Settings.NoteHeight").IsVisible, "appearance reset used saved startup value instead of factory value");

        var tabs = Field<TabControl>(window, "editorInspectorTabs");
        tabs.SelectedItem = Field<TabItem>(window, "metadataInspectorTab"); Program.Pump(50);
        var od = Field<TextBox>(window, "metadataOverallDifficultyBox");
        var hp = Field<TextBox>(window, "metadataHpDrainRateBox");
        Require(od.GetLogicalAncestors().Contains(Field<TabItem>(window, "metadataInspectorTab")), "OD control missing from map metadata");
        var tab = model.ActiveTab;
        var oldOd = tab.Session.Document.Metadata.OverallDifficulty; var oldHp = tab.Session.Document.Metadata.HpDrainRate;
        od.Text = "9.2"; hp.Text = "6.3";
        typeof(MainWindow).GetMethod("ApplyMetadataCategory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require(tab.Session.Document.Metadata.OverallDifficulty == 9.2 && tab.Session.Document.Metadata.HpDrainRate == 6.3, "OD/HP were not committed");
        var saved = Path.Combine(screenshots, "difficulty-roundtrip.osu"); OsuManiaSerializer.Save(tab.Session.Document, saved);
        var reloaded = OsuManiaSerializer.Load(saved);
        Require(reloaded.Metadata.OverallDifficulty == 9.2 && reloaded.Metadata.HpDrainRate == 6.3, "native difficulty roundtrip lost OD/HP");
        tab.Session.Undo();
        Require(tab.Session.Document.Metadata.OverallDifficulty == oldOd && tab.Session.Document.Metadata.HpDrainRate == oldHp,
            "OD/HP did not undo together");
        od.Text = "10.1"; hp.Text = "6.3";
        typeof(MainWindow).GetMethod("ApplyMetadataCategory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require(tab.Session.Document.Metadata.OverallDifficulty == oldOd && tab.Session.Document.Metadata.HpDrainRate == oldHp,
            "invalid OD partially applied HP");
        od.Text = oldOd.ToString(System.Globalization.CultureInfo.InvariantCulture); hp.Text = oldHp.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var scroller = Field<ScrollViewer>(window, "trackScroller");
        var bar = Field<ScrollBar>(window, "trackBoardScrollbar");
        var panels = Field<StackPanel>(window, "trackPanels");
        bar.Value = 0; Program.Pump(50);
        var left = panels.TranslatePoint(new Point(), scroller)!.Value.X;
        Require(left >= scroller.Viewport.Width - 1, "left edge cannot move every track beyond the viewport");
        Wheel(window, bar, -1);
        Require(scroller.Offset.X == 48 && timeline.CurrentBeat == beat, "bottom scrollbar wheel did not pan independently");
        bar.Value = bar.Maximum; Program.Pump(50);
        var right = panels.TranslatePoint(new Point(panels.Bounds.Width, 0), scroller)!.Value.X;
        Require(right <= 1, "right edge cannot move every track beyond the viewport");
        bar.Value = scroller.Viewport.Width; Program.Pump(50);
        Require(Math.Abs(scroller.Offset.X - scroller.Viewport.Width) < 1, "board reset did not restore its starting position");
        tabs.SelectedItem = Field<TabItem>(window, "settingsInspectorTab");
        Console.WriteLine("PASS option wheels/global-setting-only reset visibility, empty board edges, actual factory defaults and OD/HP atomic native roundtrip");
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static Button Reset(Control root, string id) => root.GetLogicalDescendants().OfType<Button>().Distinct().Single(b => AutomationProperties.GetAutomationId(b) == id + ".Reset");
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Wheel(MainWindow window, Control control, int delta)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(point, new Vector(0, delta)); Dispatcher.UIThread.RunJobs();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

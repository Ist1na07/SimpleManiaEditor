using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Application.Editing;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

internal static class TimelineSettings027Specs
{
    public static void Run(MainWindow window, string screenshots)
    {
        var session = new EditorSession();
        session.ReplaceDocument(new ChartDocument
        {
            KeyCount = 7,
            Timing = [new TimingEvent { Beat = new(0, 0, 1), Bpm = 120, Delay = -475 }],
            Notes = [new ChartNote { Beat = new(1, 0, 1), Lane = 0 }, new ChartNote { Beat = new(60, 0, 1), Lane = 1 }]
        });
        var ruler = new EventTimeRuler();
        ruler.SetDocument(session, new TimingMap(session.Document.Timing), 20000, []);
        Require(ruler.StartMilliseconds == 0 && ruler.EndMilliseconds == 20000, "late notes/offset changed full-song ruler extent");
        session.Mutate(d => d.Notes.Clear());
        ruler.SetDocument(session, new TimingMap(session.Document.Timing), 20000, []);
        Require(ruler.EndMilliseconds == 20000, "empty chart lost full audio span");

        var model = Field<MainWindowViewModel>(window, "viewModel");
        var original = model.ActiveTab!;
        Wait(model.CreateAsync("TimelineSettings027")); Program.Pump(50);
        var fresh = model.ActiveTab!;
        var actual = Field<EventTimeRuler>(window, "eventTimeRuler");
        Require(actual.EndMilliseconds == 1000, "fresh chart inherited previous song duration");
        var source = Path.Combine(Path.GetTempPath(), "sme-timeline-027-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            Program.WriteSimulationWave(source);
            var import = (Task)typeof(MainWindow).GetMethod("ImportChartAudioPathAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [fresh, source])!;
            Program.PumpUntil(() => window.OwnedWindows.OfType<AudioImportModeDialog>().Any());
            window.OwnedWindows.OfType<AudioImportModeDialog>().Single().Close(AudioImportMode.KeepOriginal);
            Wait(import);
            Program.PumpUntil(() => Field<WindowsAudioTransportViewModel>(window, "audioTransport").PlaybackSnapshot?.IsLoaded == true);
            Program.PumpUntil(() => actual.EndMilliseconds == 20000);
            Require(fresh.Session.Document.Notes.Count == 0 && actual.StartMilliseconds == 0, "music import needs notes or manual refresh to update ruler");
            fresh.Session.Mutate(d => d.Notes.Add(new ChartNote { Lane = 0, Beat = new(1, 0, 1) }));
            Program.Pump(80);
            Require(actual.EndMilliseconds == 20000, "sparse chart shortened full-song ruler");
        }
        finally { File.Delete(source); }
        Field<TabControl>(window, "editorInspectorTabs").SelectedItem = Field<TabItem>(window, "settingsInspectorTab");
        foreach (var (category, id, screenshot) in new[] { (1, "Settings.ShortcutsSection", "settings-input-027.png"), (2, "Settings.Appearance", "settings-appearance-027.png") })
        {
            Find<Button>(window, "Settings.Navigate." + category).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.Pump(50);
            var content = Find<StackPanel>(window, id);
            Require(content.IsEffectivelyVisible && !content.GetLogicalAncestors().OfType<Expander>().Any(), "categorized settings still require redundant expansion");
            using var frame = window.CaptureRenderedFrame()!; frame.Save(Path.Combine(screenshots, screenshot), PngBitmapEncoderOptions.Default);
        }
        model.Activate(original.Id); Program.Pump(50);
        Console.WriteLine("PASS full audio timeline before/after music import, empty/sparse chart and direct settings categories");
    }
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}


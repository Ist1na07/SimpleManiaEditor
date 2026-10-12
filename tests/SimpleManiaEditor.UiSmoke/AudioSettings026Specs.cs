using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.ViewModels;

internal static class AudioSettings026Specs
{
    public static void Run(MainWindow window, string screenshots)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var original = model.ActiveTab!;
        var create = model.CreateAsync("AudioSettings026");
        Program.PumpUntil(() => create.IsCompleted); create.GetAwaiter().GetResult();
        var music = Path.Combine(Path.GetDirectoryName(model.ActiveTab!.WorkingPath)!, "simulation.wav");
        Program.WriteSimulationWave(music);
        model.ActiveTab.Session.Mutate(d => d.Metadata.AudioFile = "simulation.wav");
        var transport = Field<WindowsAudioTransportViewModel>(window, "audioTransport");
        Program.PumpUntil(() => transport.PlaybackSnapshot?.IsLoaded == true);
        Field<TabControl>(window, "editorInspectorTabs").SelectedItem = Field<TabItem>(window, "settingsInspectorTab");
        Find<Button>(window, "Settings.Navigate.3").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.Pump(80);
        var status = Find<TextBlock>(window, "Audio.RuntimeStatus");
        var refresh = Find<Button>(window, "Audio.RefreshDevice");
        Require(status.IsEffectivelyVisible && refresh.IsEffectivelyVisible && refresh.IsEnabled && !string.IsNullOrWhiteSpace(status.Text),
            "audio status/recovery controls were built but not visible in audio settings");
        var outputs = (SimulatedAudioOutputFactory)PlatformAudioOutputs.Current;
        var before = outputs.Outputs.Count;
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Program.PumpUntil(() => outputs.Outputs.Count > before);
        Program.PumpUntil(() => transport.LastError == null && transport.PlaybackSnapshot?.IsLoaded == true);
        var rateChange = (Task)typeof(MainWindow).GetMethod("ApplyFixedPlaybackRateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [1.03d])!;
        Program.PumpUntil(() => rateChange.IsCompleted); rateChange.GetAwaiter().GetResult(); Program.Pump(60);
        var picker = Field<ComboBox>(window, "fixedRatePicker");
        var items = picker.ItemsSource;
        picker.IsDropDownOpen = true;
        for (var i = 0; i < 20; i++) typeof(MainWindow).GetMethod("SyncFixedPlaybackRate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require(ReferenceEquals(items, picker.ItemsSource) && picker.IsDropDownOpen && (double)picker.SelectedItem! == 1.03,
            "polling rebuilt custom rate options and disturbed the open dropdown");
        picker.IsDropDownOpen = false;
        rateChange = (Task)typeof(MainWindow).GetMethod("ApplyFixedPlaybackRateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [1d])!;
        Program.PumpUntil(() => rateChange.IsCompleted); rateChange.GetAwaiter().GetResult(); Program.Pump(30);
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "audio-settings-026.png"), PngBitmapEncoderOptions.Default);
        model.Activate(original.Id); Program.Pump(50);
        Console.WriteLine("PASS visible audio status and actionable output recovery in categorized settings");
    }

    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static T Field<T>(object root, string name) => (T)root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root)!;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

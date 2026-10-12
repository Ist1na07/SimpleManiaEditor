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
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Application.Editing;
using LumaChart.Core.Models;
using LumaChart.Core.IO;
using LumaChart.Core.Timing;

internal static class EditorInteraction022Specs
{
    public static void RunPointer()
    {
        var session = new EditorSession(); session.ReplaceDocument(new ChartDocument { KeyCount = 7 }); session.SnapDivision = 4;
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Hold };
        var window = new Window { Width = 1000, Height = 800, Content = surface }; window.Show(); Program.Pump(40);
        surface.SetZoom(140); surface.SetCurrentBeat(4);
        var target = surface.GetPlacementEvidenceProbe()!.TapTarget;
        Point At(double beat) => new(target.SurfacePoint.X, surface.Bounds.Height * surface.JudgementLinePosition - (beat - 4) * 140);
        window.MouseDown(At(4), MouseButton.Left); window.MouseMove(At(4.37), RawInputModifiers.LeftMouseButton);
        Require(Math.Abs(Field<BeatPosition>(surface, "placementCurrentBeat").Value - 4.37) < .00001, "new LN preview snaps while dragging");
        Require(session.Document.Notes.Count == 0, "LN preview committed prematurely");
        window.MouseUp(At(4.37), MouseButton.Left);
        Require(session.Document.Notes.Single().EndBeat!.Value.Value == 4.25, "new LN did not snap on release");
        surface.Tool = TimelineTool.Select;
        window.MouseDown(At(4.25) - new Vector(0, 2), MouseButton.Left); window.MouseMove(At(4.63), RawInputModifiers.LeftMouseButton);
        Require(Math.Abs(Field<BeatPosition?>(surface, "resizingHoldCurrentEnd")!.Value.Value - 4.63) < .00001, "tail preview snaps while dragging");
        Require(session.Document.Notes.Single().EndBeat!.Value.Value == 4.25, "tail preview modified document");
        window.MouseUp(At(4.63), MouseButton.Left);
        Require(session.Document.Notes.Single().EndBeat!.Value.Value == 4.75, "tail did not snap on release");
        Require(session.Undo() && session.Document.Notes.Single().EndBeat!.Value.Value == 4.25, "tail is not one undo");
        window.Close(); Program.Pump(20);
        Console.WriteLine("PASS continuous LN creation/tail previews and release snapping");
    }

    public static void RunWorkbench(MainWindow window)
    {
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var original = model.ActiveTab!;
        Field<TabControl>(window, "editorInspectorTabs").SelectedItem = Field<TabItem>(window, "settingsInspectorTab");
        Program.Pump(40);
        var mode = Find<ComboBox>(window, "Settings.EditorMode");
        mode.SelectedIndex = 1; Program.Pump(80);
        Require(mode.SelectedIndex == 1 && mode.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "osu 完整模式"),
            $"selected mode caption does not show full mode: index={mode.SelectedIndex}, texts={string.Join('|', mode.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))}");
        mode.SelectedIndex = 0; Program.Pump(40);
        Wait(model.CreateAsync("Persistence022"));
        var tab = model.ActiveTab!; var surface = Field<TimelineSurface>(window, "timeline");
        Require(OsuTimingPointSettings.Read(tab.Session.Document.Timing.First()).Volume == 0, "minimal new chart sample volume is not zero");
        var audio = Path.Combine(AvaloniaAppPaths.LocalDataRoot, "manual-tempo022.wav"); Program.WriteSimulationWave(audio);
        var import = (Task)Invoke(window, "ImportChartAudioPathCoreAsync", tab, audio)!;
        Program.PumpUntil(() => window.OwnedWindows.OfType<AudioImportModeDialog>().Any());
        Find<Button>(window.OwnedWindows.OfType<AudioImportModeDialog>().Single(), "AudioImport.KeepOriginal").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(import);
        Require(!Field<bool>(window, "audioAnalysisInProgress") && tab.Session.Document.Metadata.Bpm == 120 &&
                TimingMap.FindSongOffsetTiming(tab.Session.Document.Timing)!.Delay == 0, "audio import auto-started tempo analysis");
        surface.NavigateToBeat(2.74);
        Find<Button>(window, "Timing.OffsetHere").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Program.Pump(40);
        Require(Math.Abs(-TimingMap.FindSongOffsetTiming(tab.Session.Document.Timing)!.Delay - 1370) < .001, "offset-here did not use actual playback time");
        Wait((Task)Invoke(window, "AddEditorTrackAsync", EditorTrackKind.Draft, null!)!);
        var workspace = Field<EditorTrackWorkspace>(window, "activeTrackWorkspace");
        var draft = workspace.Tracks.Single(); draft.SyncScroll = false; workspace.MarkLayoutChanged();
        var draftSurface = window.GetLogicalDescendants().OfType<TimelineSurface>().Distinct().Single(t => t != surface && ReferenceEquals(t.Session, draft.Session));
        draftSurface.NavigateToBeat(3.25); draftSurface.SetZoom(280);
        var panels = Field<StackPanel>(window, "trackPanels");
        var draftKey = panels.Children.OfType<EditorTrackPanel>().Single(p => p.TrackKey.StartsWith("track:")).TrackKey;
        Invoke(window, "MoveBoardFrame", draftKey, -1d);
        surface.NavigateToBeat(6.137);
        var scrollbar = Field<ScrollBar>(window, "trackBoardScrollbar"); scrollbar.Value = Math.Min(scrollbar.Maximum, scrollbar.Value + 215); Program.Pump(80);
        var pan = Field<double>(window, "desiredBoardPan");
        var order = panels.Children.OfType<EditorTrackPanel>().Select(p => p.TrackKey).ToArray();
        Wait((Task)Invoke(window, "SaveTrackWorkspacesAsync")!);
        var loaded = new EditorTrackWorkspace(tab.SourcePath, Path.Combine(AvaloniaAppPaths.LocalDataRoot, "tracks"));
        Require(loaded.MainViewBeat == 6.137 && Math.Abs(loaded.BoardPan - pan) < .01 && loaded.BoardOrder.SequenceEqual(order), "view state not persisted");
        Require(loaded.Tracks.Single().ViewBeat == 3.25 && loaded.Tracks.Single().ViewZoom == 280 && loaded.Tracks.Single().Kind == EditorTrackKind.Draft, "independent track viewport not persisted");
        var save = model.SaveAndVerifyAsync(tab.Id); Wait(save); Require(save.Result?.StructurallyEqual == true, "offset save verification failed");
        var source = tab.SourcePath;
        Wait(model.CloseTabAsync(tab.Id, false)); Wait(model.OpenAsync(source)); Program.Pump(100);
        Require(Math.Abs(surface.CurrentBeat - 6.137) < .00001 && Math.Abs(Field<double>(window, "desiredBoardPan") - pan) < .01, "reopen did not restore playback/pan");
        Require(panels.Children.OfType<EditorTrackPanel>().Select(p => p.TrackKey).SequenceEqual(order), "reopen lost board order");
        var reopenedDraft = window.GetLogicalDescendants().OfType<TimelineSurface>().Distinct().Single(t => t != surface);
        Require(reopenedDraft.CurrentBeat == 3.25 && reopenedDraft.PixelsPerBeat == 280, "independent viewport/zoom did not restore");
        Require(Math.Abs(-TimingMap.FindSongOffsetTiming(model.ActiveTab!.Session.Document.Timing)!.Delay - 1370) < .001, "reopen lost offset");
        model.Activate(original.Id); Program.Pump(60);
        Console.WriteLine("PASS mode caption, minimal sample volume, offset-here/save/reopen and per-chart board/track/view restoration");
    }
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static object? Invoke(object w, string name, params object[] args) => w.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, args);
    private static T Field<T>(object w, string name) => (T)w.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
    private static T Find<T>(Control root, string id) where T : Control => root.GetLogicalDescendants().OfType<T>().Distinct().Single(c => AutomationProperties.GetAutomationId(c) == id);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

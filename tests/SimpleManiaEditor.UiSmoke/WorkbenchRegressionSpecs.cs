using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LumaChart.Avalonia;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.ViewModels;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class WorkbenchRegressionSpecs
{
    public static void Run(MainWindow window, string screenshots)
    {
        var timeline = Field<TimelineSurface>(window, "timeline");
        var model = Field<MainWindowViewModel>(window, "viewModel");
        var tab = model.ActiveTab!;
        var transport = Field<WindowsAudioTransportViewModel>(window, "audioTransport");
        Require(transport.PreservePitch, "upgraded/default editor speed did not preserve pitch");
        var grid = Field<ComboBox>(window, "fixedGridPicker");
        var notation = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "Editor.GridNotation");
        grid.SelectedItem = 4;
        notation.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Require(grid.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "1/16") && tab.Session.SnapDivision == 4,
            "general notation did not display whole-note denominator while preserving beat division");
        timeline.NavigateToBeat(4);
        var point = timeline.TranslatePoint(new Point(timeline.Bounds.Width / 2, timeline.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(point, new Vector(0, -1));
        Require(Math.Abs(timeline.CurrentBeat - 4.25) < .0001, $"osu 1/4 / general 1/16 did not move one quarter-beat: beat={timeline.CurrentBeat}, snap={tab.Session.SnapDivision}, pointer={point}, board offset={Field<ScrollViewer>(window, "trackScroller").Offset}, viewport={Field<ScrollViewer>(window, "trackScroller").Viewport}");
        Field<ComboBox>(window, "fixedRatePicker").SelectedItem = 1d;
        Program.PumpUntil(() => transport.PlaybackSnapshot?.PlaybackRate == 1);
        var beat = timeline.CurrentBeat;
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Shift);
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Shift);
        Program.PumpUntil(() => transport.PlaybackSnapshot?.PlaybackRate == 1.25);
        Require(timeline.CurrentBeat == beat && tab.Session.SnapDivision == 4, "Shift wheel changed time/grid instead of speed");
        var gridTick = Button(window, "Editor.Adjustment.Grid.Tick.15");
        Click(gridTick);
        Require(tab.Session.SnapDivision == 64 && grid.SelectedItem is int displayed && displayed == 64, "grid tick failed to synchronize dropdown and internal grid");
        var zoom = timeline.PixelsPerBeat;
        Click(Button(window, "Editor.Adjustment.Zoom.Tick.7"));
        Require(timeline.PixelsPerBeat == 1920, "zoom tick did not select its declared scale");
        timeline.SetZoom(zoom);
        notation.SelectedIndex = 0;
        grid.SelectedItem = 4;
        Require(EditorPresentationSettingsStore.LoadGridNotation() == EditorGridNotation.Osu &&
            EditorPresentationSettingsStore.LoadNoteStyle() == EditorNoteStyle.Osu, "grid/style preference writes overwrote each other");
        Require(window.GetVisualDescendants().OfType<Expander>().Where(e => Equals(e.Header, "调整")).All(e => e.IsExpanded), "adjustment toolbar was not expanded by default");
        Require(Button(window, "Editor.QuickCopy").GetVisualAncestors().Contains(Field<StackPanel>(window, "fixedToolRailBody")), "quick edit commands were not merged into the fixed sidebar");
        var bpm = Field<TextBox>(window, "metadataBpmBox");
        Require(bpm.GetLogicalAncestors().Contains(Field<Control>(window, "timingMetadataSection")), "BPM control still belongs to metadata category");
        var titleBox = Field<TextBox>(window, "metadataTitleBox");
        var originalTitle = tab.Session.Document.Metadata.Title;
        titleBox.Text = "Metadata category applied";
        bpm.Text = "pending invalid timing";
        typeof(MainWindow).GetMethod("ApplyMetadataCategory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require(tab.Session.Document.Metadata.Title == "Metadata category applied" && bpm.Text == "pending invalid timing",
            "metadata apply consumed or discarded a pending Timing draft");
        titleBox.Text = originalTitle;
        bpm.Text = "120";
        typeof(MainWindow).GetMethod("ApplyMetadataCategory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        titleBox.Text = "Still pending general metadata";
        bpm.Text = "121";
        typeof(MainWindow).GetMethod("ApplyTimingMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Require(tab.Session.Document.Metadata.Title == originalTitle && tab.Session.Document.Metadata.Bpm == 121 && titleBox.Text == "Still pending general metadata",
            "Timing apply consumed unrelated metadata drafts");
        titleBox.Text = originalTitle; bpm.Text = "120";
        typeof(MainWindow).GetMethod("ApplyTimingMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        window.Width = 1920; window.Height = 1080;
        Program.Pump(100);
        timeline.NavigateToBeat(8);
        var scroller = Field<ScrollViewer>(window, "trackScroller");
        var board = Field<Grid>(window, "trackBoardCanvas");
        scroller.Offset = new Vector(Math.Max(0, board.Width - scroller.Viewport.Width), 0);
        Program.Pump(50);
        point = board.TranslatePoint(new Point(board.Width - 35, board.Bounds.Height / 2), window)!.Value;
        var boardBar = Field<ScrollBar>(window, "trackBoardScrollbar");
        Require(boardBar.IsVisible && boardBar.Bounds.Height >= 24 && !boardBar.AllowAutoHide && boardBar.Visibility == ScrollBarVisibility.Visible,
            "board scrollbar is hidden/collapsed or auto-hidden");
        var barPoint = boardBar.TranslatePoint(new Point(10, boardBar.Bounds.Height / 2), window)!.Value;
        Require(barPoint.Y > scroller.TranslatePoint(new Point(0, scroller.Bounds.Height), window)!.Value.Y && barPoint.Y < window.Bounds.Height,
            "board scrollbar is not visible beneath the board");
        var boardBeat = timeline.CurrentBeat;
        window.MouseWheel(point, new Vector(0, -1));
        Require(timeline.CurrentBeat > boardBeat, "wheel on blank board did not navigate time");
        tab.Session.Selection.UnionWith(tab.Session.Document.Notes.Select(n => n.Id));
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Require(tab.Session.Selection.Count == 0, "blank board right-click did not deselect notes");
        var menu = Field<ContextMenu>(window, "addTrackContextMenu");
        Require(menu.IsOpen, "right-click blank space did not open track menu");
        scroller.Offset = new Vector(scroller.Viewport.Width, 0); Program.Pump(50);
        var beforeDismiss = tab.Session.Document.Notes.Count;
        timeline.Tool = TimelineTool.Tap;
        var dismissProbe = timeline.GetPlacementEvidenceProbe()!;
        var dismissPoint = timeline.TranslatePoint(dismissProbe.TapTarget.SurfacePoint, window)!.Value;
        window.MouseDown(dismissPoint, MouseButton.Left); window.MouseUp(dismissPoint, MouseButton.Left);
        Require(!menu.IsOpen && tab.Session.Document.Notes.Count == beforeDismiss, "closing the track menu also placed a note");
        window.MouseDown(dismissPoint, MouseButton.Right); window.MouseUp(dismissPoint, MouseButton.Right);
        Require(!menu.IsOpen, "right-click inside a note track opened the board menu");
        scroller.Offset = new Vector(Math.Max(0, board.Width - scroller.Viewport.Width), 0); Program.Pump(50);
        point = board.TranslatePoint(new Point(board.Width - 35, board.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
        menu = Field<ContextMenu>(window, "addTrackContextMenu");
        var offsetBeforeAdd = scroller.Offset.X;
        boardBar.Value = 0;
        Program.Pump(30);
        Require(scroller.Offset.X == 0, "board scrollbar did not move the board");
        boardBar.Value = offsetBeforeAdd;
        Program.Pump(30);
        var mainWidthBeforeAdd = Field<Control>(window, "mainTrackFrame").Width;
        var draftItem = menu.ItemsSource!.OfType<MenuItem>().Single(i => AutomationProperties.GetAutomationId(i) == "Editor.AddTrack.Draft");
        draftItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Program.PumpUntil(() => Field<EditorTrackWorkspace>(window, "activeTrackWorkspace").Tracks.Count == 1);
        Program.Pump(100);
        Require(scroller.Offset.X == offsetBeforeAdd && Field<Control>(window, "mainTrackFrame").Width == mainWidthBeforeAdd,
            $"adding a track resized/repositioned the board: offset {offsetBeforeAdd} -> {scroller.Offset.X}, main width {mainWidthBeforeAdd} -> {Field<Control>(window, "mainTrackFrame").Width}, viewport {scroller.Viewport.Width}");
        boardBar.Value = boardBar.Maximum;
        Program.Pump(50);
        var boardThumb = boardBar.GetVisualDescendants().OfType<Thumb>().Single(t => t.IsVisible);
        Require(boardThumb.Bounds.Height >= 10, $"board thumb is still too thin: {boardThumb.Bounds.Height}");
        var grip = boardThumb.TranslatePoint(new Point(boardThumb.Bounds.Width / 2, boardThumb.Bounds.Height / 2), window)!.Value;
        var dragOffset = scroller.Offset.X;
        window.MouseDown(grip, MouseButton.Left);
        window.MouseMove(grip - new Vector(50, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(grip - new Vector(50, 0), MouseButton.Left);
        Require(scroller.Offset.X < dragOffset, "dragging board scrollbar failed to pan the board");
        var workspace = Field<EditorTrackWorkspace>(window, "activeTrackWorkspace");
        var draft = workspace.Tracks.Single();
        var draftView = View(window, draft.Id);
        var surface = Property<TimelineSurface>(draftView, "Surface");
        var draftFrame = Property<Border>(draftView, "Host");
        scroller.Offset = new Vector(Math.Max(0, draftFrame.TranslatePoint(new Point(), board)!.Value.X - 100), 0);
        Program.Pump(50);
        surface.Tool = TimelineTool.Tap;
        surface.SetCurrentBeat(8);
        var probe = surface.GetPlacementEvidenceProbe()!;
        var mainAnchor = timeline.TranslatePoint(new Point(0, timeline.Bounds.Height * timeline.JudgementLinePosition), window)!.Value.Y;
        var draftAnchor = surface.TranslatePoint(new Point(0, surface.Bounds.Height * surface.JudgementLinePosition), window)!.Value.Y;
        Require(Math.Abs(mainAnchor - draftAnchor) < 1, $"track headers shifted synchronized cursor lines: main={mainAnchor}, draft={draftAnchor}, height={surface.Bounds.Height}, ratio={surface.JudgementLinePosition}");
        var target = surface.TranslatePoint(probe.TapTarget.SurfacePoint, window)!.Value;
        var officialCount = tab.Session.Document.Notes.Count;
        window.MouseDown(target, MouseButton.Left); window.MouseUp(target, MouseButton.Left);
        Require(draft.Session!.Document.Notes.Count == 1 && tab.Session.Document.Notes.Count == officialCount, "draft pointer gesture changed main chart or failed to place note");
        Program.Stroke(window, Key.Z, RawInputModifiers.Control);
        Require(draft.Session.Document.Notes.Count == 0 && tab.Session.Document.Notes.Count == officialCount, "draft undo routed to main history");
        Program.Stroke(window, Key.Y, RawInputModifiers.Control);
        Require(draft.Session.Document.Notes.Count == 1, "draft redo failed");
        Wait(workspace.SaveAsync());
        var panelList = Field<StackPanel>(window, "trackPanels");
        var waveform = panelList.Children.OfType<EditorTrackPanel>().Single(p => p.TrackKey == "waveform");
        var dragStart = waveform.TranslatePoint(new Point(9, 16), window)!.Value;
        var oldOrder = panelList.Children.OfType<EditorTrackPanel>().Select(p => p.TrackKey).ToArray();
        scroller.Offset = new Vector(Math.Max(0, scroller.Viewport.Width + waveform.TranslatePoint(new Point(), panelList)!.Value.X - 10), 0);
        Program.Pump(50);
        dragStart = waveform.TranslatePoint(new Point(9, 16), window)!.Value;
        window.MouseDown(dragStart, MouseButton.Left);
        window.MouseMove(dragStart + new Vector(130, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(dragStart + new Vector(130, 0), MouseButton.Left);
        Require(!panelList.Children.OfType<EditorTrackPanel>().Select(p => p.TrackKey).SequenceEqual(oldOrder), "compact audio track could not be moved by dragging its header");
        Wait(workspace.SaveAsync());
        Require(new EditorTrackWorkspace(tab.SourcePath, Path.GetDirectoryName(workspace.DirectoryPath)!).BoardOrder.Count >= 6,
            "board order did not persist");
        var reopened = new EditorTrackWorkspace(tab.SourcePath, Path.GetDirectoryName(workspace.DirectoryPath)!);
        Require(reopened.Tracks.Single().Session!.Document.Notes.Count == 1, "draft workspace could not reopen placed note");
        var mainSave = model.SaveAndVerifyAsync(tab.Id);
        Program.PumpUntil(() => mainSave.IsCompleted);
        Require(mainSave.GetAwaiter().GetResult()?.StructurallyEqual == true && OsuManiaSerializer.Load(tab.SourcePath).Notes.Count == officialCount,
            "draft note leaked into native osu export");
        draft.Session.Selection.Add(draft.Session.Document.Notes[0].Id);
        timeline.NavigateToBeat(14);
        Click(Property<Border>(draftView, "Host").GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "放入主轨")));
        Require(tab.Session.Document.Notes.Count == officialCount + 1 && draft.Session.Document.Notes.Count == 1, "explicit draft-to-main copy lost source or failed to create official note");
        var referencePath = Path.Combine(Path.GetDirectoryName(tab.WorkingPath)!, "reference.osu");
        OsuManiaSerializer.Save(new ChartDocument { KeyCount = 4, Timing = [new TimingEvent { Bpm = 240 }],
            Notes = [new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(8) }] }, referencePath);
        Add(window, EditorTrackKind.Reference, referencePath);
        var reference = workspace.Tracks.Single(t => t.Kind == EditorTrackKind.Reference);
        var referenceSurface = Property<TimelineSurface>(View(window, reference.Id), "Surface");
        timeline.NavigateToBeat(4);
        Require(reference.Session!.IsReadOnly && Math.Abs(referenceSurface.CurrentBeat - 8) < .0001,
            "reference synchronization used beat equality instead of song time across BPMs");
        timeline.SetZoom(1920); Program.Pump(50);
        var mainY = AxisY(timeline, 6);
        var refY = AxisY(referenceSurface, 12);
        Require(Math.Abs(mainY - refY) < .1, "equal song times rendered at different positions across BPMs/high zoom");
        var wave = Field<AudioVisualizationSideSurface>(window, "waveformSideSurface");
        Require(Field<double>(wave, "pixelsPerBeat") == 1920, "waveform retained the old 480 zoom clamp");
        timeline.SetZoom(zoom);
        reference.SyncScroll = false; workspace.MarkLayoutChanged();
        referenceSurface.NavigateToBeat(6);
        Require(timeline.CurrentBeat == 4, "independent reference navigation moved main chart");
        timeline.NavigateToBeat(10);
        Require(referenceSurface.CurrentBeat == 6, "main scroll changed an unsynchronized reference");
        Add(window, EditorTrackKind.Hitsound);
        var hitsound = workspace.Tracks.Single(t => t.Kind == EditorTrackKind.Hitsound);
        var hitView = View(window, hitsound.Id);
        var noteId = tab.Session.Document.Notes[0].Id;
        hitsound.Session!.Selection.Add(noteId);
        var hitHost = Property<Border>(hitView, "Host");
        hitHost.GetVisualDescendants().OfType<NumericUpDown>().Single().Value = 75;
        Click(hitHost.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "应用")));
        Require(tab.Session.Document.Notes.Single(n => n.Id == noteId).Volume == 75, "hitsound track failed to apply selected-note volume");
        Program.Stroke(window, Key.Z, RawInputModifiers.Control);
        Require(tab.Session.Document.Notes.Single(n => n.Id == noteId).Volume == 100,
            "hitsound undo did not reach the owning main chart history");
        Program.Stroke(window, Key.Y, RawInputModifiers.Control);
        Require(tab.Session.Document.Notes.Single(n => n.Id == noteId).Volume == 75,
            "hitsound redo did not restore the main chart edit");
        Add(window, EditorTrackKind.Density); Add(window, EditorTrackKind.Difficulty);
        var density = Property<EditorMetricTrackSurface>(View(window, workspace.Tracks.Single(t => t.Kind == EditorTrackKind.Density).Id), "Metric");
        Program.PumpUntil(() => !density.Computing);
        Require(density.Samples.Sum(s => s.Value) == tab.Session.Document.Notes.Count, "density track did not reflect official main notes");
        var referenceView = View(window, reference.Id);
        Click(Property<Border>(referenceView, "Host").GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "隐藏")));
        Require(!reference.Visible && workspace.Tracks.Count == 5, "hiding reference discarded its data");
        Wait(workspace.SaveAsync());
        Require(new EditorTrackWorkspace(tab.SourcePath, Path.GetDirectoryName(workspace.DirectoryPath)!).Tracks.Count == 5,
            "track layout did not survive reopening");
        timeline.NavigateToBeat(4);
        Field<ScrollViewer>(window, "trackScroller").Offset = new Vector(Field<ScrollViewer>(window, "trackScroller").Viewport.Width, 0);
        Program.Pump(100);
        using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots, "workbench-tracks.png"), PngBitmapEncoderOptions.Default);
        Field<ComboBox>(window, "fixedRatePicker").SelectedItem = 1d;
        Program.PumpUntil(() => transport.PlaybackSnapshot?.PlaybackRate == 1);
        Console.WriteLine("PASS grid notation, Shift speed, marked sliders, category layout, editable/recoverable draft, reference time sync, hitsound and metric tracks");
    }

    private static void Add(MainWindow window, EditorTrackKind kind, string? path = null)
    {
        var task = (Task)typeof(MainWindow).GetMethod("AddEditorTrackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [kind, path])!;
        Wait(task);
    }
    private static object View(MainWindow window, Guid id) => Field<IDictionary>(window, "secondaryTrackViews")[id]!;
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static Button Button(Control root, string id) => root.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(global::Avalonia.Controls.Button.ClickEvent));
    private static void Wait(Task task) { Program.PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double AxisY(TimelineSurface surface, double beat)
    {
        var geometry = typeof(TimelineSurface).GetMethod("GetGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [surface.Session!.Document.KeyCount]);
        var y = (double)typeof(TimelineSurface).GetMethod("YAtBeat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [beat, geometry])!;
        return surface.TranslatePoint(new Point(0, y), TopLevel.GetTopLevel(surface)!)!.Value.Y;
    }
}

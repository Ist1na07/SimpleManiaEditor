using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Services;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Editing;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia;

public sealed partial class MainWindow
{
    private sealed class TrackView(EditorTrack track)
    {
        public EditorTrack Track { get; } = track;
        public TimelineSurface? Surface { get; set; }
        public EditorMetricTrackSurface? Metric { get; set; }
        public Border Host { get; set; } = new();
        public long MainSerial { get; set; } = -1;
        public TimingMap? Timing { get; set; }
        public long TimingSerial { get; set; } = -1;
    }

    private readonly Dictionary<string, EditorTrackWorkspace> trackWorkspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> trackSourceByTab = [];
    private readonly Dictionary<Guid, TrackView> secondaryTrackViews = [];
    private readonly StackPanel trackPanels = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ScrollViewer trackScroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private EditorTrackWorkspace? activeTrackWorkspace;
    private TrackView? activeSecondaryTrack;
    private ContextMenu? addTrackContextMenu;
    private bool synchronizingTrackViewport;
    private Control? mainTrackFrame;

    private Control BuildTrackWorkbench(Control frame)
    {
        mainTrackFrame = frame;
        trackPanels.Children.Add(frame);
        trackScroller.Content = trackPanels;
        trackScroller.SizeChanged += (_, _) => UpdateMainTrackWidth();
        timeline.SizeChanged += (_, _) => SyncTrackViewports();
        timeline.BlankContextRequested += (_, _) => OpenAddTrackMenu(timeline);
        timeline.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
        {
            activeSecondaryTrack = null;
            RefreshFixedTools();
            UpdateEditorAvailability();
        }, RoutingStrategies.Tunnel);
        trackPanels.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source == trackPanels && e.GetCurrentPoint(trackPanels).Properties.IsRightButtonPressed) OpenAddTrackMenu(trackPanels);
        });
        return trackScroller;
    }

    private void UpdateMainTrackWidth()
    {
        if (mainTrackFrame == null) return;
        if (trackScroller.Bounds.Width <= 0) return;
        mainTrackFrame.Width = Math.Max(320, secondaryTrackViews.Count > 0
            ? Math.Min(640, trackScroller.Bounds.Width) : trackScroller.Bounds.Width);
    }

    private void OpenAddTrackMenu(Control anchor)
    {
        if (viewModel.ActiveTab == null) return;
        var items = new List<MenuItem>();
        foreach (var (kind, name) in new[] { (EditorTrackKind.Draft, "添加草稿按键轨"),
            (EditorTrackKind.Reference, "添加其他谱面对照轨…"), (EditorTrackKind.Hitsound, "添加 hitsound 编辑轨"),
            (EditorTrackKind.Density, "添加密度轨"), (EditorTrackKind.Difficulty, "添加局部负荷轨（实验）") })
        {
            var item = new MenuItem { Header = name };
            AutomationProperties.SetAutomationId(item, $"Editor.AddTrack.{kind}");
            item.Click += async (_, _) =>
            {
                try { await AddEditorTrackAsync(kind); }
                catch (Exception exception) { viewModel.SetStatus($"添加轨道失败：{exception.Message}"); }
            };
            items.Add(item);
        }
        foreach (var hidden in activeTrackWorkspace?.Tracks.Where(t => !t.Visible) ?? [])
        {
            var item = new MenuItem { Header = "显示：" + hidden.Name };
            item.Click += (_, _) => { hidden.Visible = true; activeTrackWorkspace!.MarkLayoutChanged(); RebuildTrackViews(); };
            items.Add(item);
        }
        addTrackContextMenu = new ContextMenu { ItemsSource = items };
        AutomationProperties.SetAutomationId(addTrackContextMenu, "Editor.TrackMenu");
        addTrackContextMenu.Open(anchor);
    }

    private async Task AddEditorTrackAsync(EditorTrackKind kind, string? referencePath = null)
    {
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("请先打开谱面。");
        RefreshTrackWorkspace(tab);
        ChartDocument? reference = null;
        if (kind == EditorTrackKind.Reference)
        {
            referencePath ??= await PickLocalFileAsync(new FilePickerOpenOptions
            {
                Title = "选择只读对照谱面", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("谱面") { Patterns = ["*.osu", "*.mc"] }]
            }, PickedFileRetention.Durable);
            if (string.IsNullOrWhiteSpace(referencePath)) return;
            var importedPath = referencePath;
            reference = await Task.Run(() => Path.GetExtension(importedPath).Equals(".osu", StringComparison.OrdinalIgnoreCase)
                ? OsuManiaSerializer.Parse(File.ReadAllText(importedPath), importedPath)
                : MalodyMcSerializer.Parse(File.ReadAllText(importedPath), importedPath));
            if (viewModel.ActiveTab != tab) return;
        }
        var workspace = activeTrackWorkspace!;
        var track = workspace.Add(kind, tab.Session.Document, reference, referencePath ?? "");
        if (kind == EditorTrackKind.Reference) track.Name = "对照 · " + Path.GetFileNameWithoutExtension(referencePath);
        RebuildTrackViews();
        await workspace.SaveAsync();
        if (!ReferenceEquals(workspace, activeTrackWorkspace)) return;
        Dispatcher.UIThread.Post(() => trackScroller.Offset = new Vector(Math.Max(0, trackScroller.Extent.Width - trackScroller.Viewport.Width), 0), DispatcherPriority.Loaded);
        viewModel.SetStatus($"已添加{track.Name}；轨道资料保存在 {workspace.DirectoryPath}");
    }

    private void RefreshTrackWorkspace(DesktopChartTab? tab)
    {
        if (tab == null) { activeTrackWorkspace = null; RebuildTrackViews(); return; }
        var path = Path.GetFullPath(tab.SourcePath);
        if (!trackWorkspaces.TryGetValue(path, out var workspace))
        {
            workspace = trackSourceByTab.TryGetValue(tab.Id, out var previousPath) && trackWorkspaces.TryGetValue(previousPath, out var previous)
                ? previous.CopyForSource(path)
                : new EditorTrackWorkspace(path, Path.Combine(AvaloniaAppPaths.LocalDataRoot, "tracks"));
            trackWorkspaces.Add(path, workspace);
            if (workspace.LoadFailed) viewModel.SetStatus($"轨道资料读取失败；已保留 {workspace.DirectoryPath}，未进行覆盖。");
            if (workspace.HasPendingChanges) _ = SaveCopiedTrackWorkspaceAsync(workspace);
        }
        trackSourceByTab[tab.Id] = path;
        if (!ReferenceEquals(workspace, activeTrackWorkspace))
        {
            activeTrackWorkspace = workspace;
            RebuildTrackViews();
        }
        UpdateTrackDocuments(tab);
        SyncTrackViewports();
    }

    private void RebuildTrackViews()
    {
        activeSecondaryTrack = null;
        foreach (var view in secondaryTrackViews.Values)
        {
            if (view.Surface != null) view.Surface.Session = null;
            view.Metric?.CancelPendingAnalysis();
        }
        secondaryTrackViews.Clear();
        while (trackPanels.Children.Count > 1) trackPanels.Children.RemoveAt(1);
        if (activeTrackWorkspace != null)
            foreach (var track in activeTrackWorkspace.Tracks.Where(t => t.Visible))
            {
                var view = new TrackView(track);
                secondaryTrackViews.Add(track.Id, view);
                trackPanels.Children.Add(BuildSecondaryTrack(view));
            }
        UpdateMainTrackWidth();
        if (viewModel.ActiveTab is { } tab) UpdateTrackDocuments(tab);
        SyncTrackViewports();
    }

    private Control BuildSecondaryTrack(TrackView view)
    {
        var track = view.Track;
        var title = new TextBox { Text = track.Name, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            FontSize = 12, Foreground = Brushes.White, MinWidth = 70 };
        title.LostFocus += (_, _) => { track.Name = title.Text ?? track.Name; activeTrackWorkspace?.MarkLayoutChanged(); };
        var sync = new CheckBox { Content = "同步", IsChecked = track.SyncScroll, FontSize = 10 };
        sync.IsCheckedChanged += (_, _) => { track.SyncScroll = sync.IsChecked == true; activeTrackWorkspace?.MarkLayoutChanged(); SyncTrackViewports(); };
        var hide = new Button { Content = "隐藏", FontSize = 10, Padding = new Thickness(4) };
        hide.Click += (_, _) => { track.Visible = false; activeTrackWorkspace?.MarkLayoutChanged(); RebuildTrackViews(); };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(6) };
        header.Children.Add(title); Grid.SetColumn(sync, 1); header.Children.Add(sync); Grid.SetColumn(hide, 2); header.Children.Add(hide);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        body.Children.Add(header);
        if (track.Kind is EditorTrackKind.Draft or EditorTrackKind.Reference or EditorTrackKind.Hitsound)
        {
            if (track.Kind == EditorTrackKind.Hitsound)
            {
                var session = new EditorSession { IsReadOnly = true };
                session.ReplaceDocument(viewModel.ActiveTab!.Session.Document.Clone());
                track.Session = session;
            }
            var surface = new TimelineSurface { Session = track.Session, NoteStyle = timeline.NoteStyle,
                InteractionProfile = shortcutSettings.Profile, ReverseTimeWheel = timeline.ReverseTimeWheel };
            surface.ConfigureView(42, timeline.PixelsPerBeat, 37, 26, 1, .79, 1.5, .85,
                editorViewSettings.NoteAnchor);
            surface.Tool = track.Kind == EditorTrackKind.Draft ? timeline.Tool : TimelineTool.Select;
            surface.SetCurrentBeat(track.ViewBeat);
            surface.StatusChanged += (_, text) => viewModel.SetStatus(text);
            surface.BlankContextRequested += (_, _) => OpenAddTrackMenu(surface);
            surface.AddHandler(InputElement.PointerPressedEvent, (_, _) => { activeSecondaryTrack = view; RefreshFixedTools(); }, RoutingStrategies.Tunnel);
            surface.SelectionChanged += (_, _) => RefreshTrackCommandButtons();
            surface.CurrentBeatChanged += (_, beat) =>
            {
                track.ViewBeat = beat;
                if (!track.SyncScroll && !synchronizingTrackViewport) activeTrackWorkspace?.MarkLayoutChanged();
                if (!synchronizingTrackViewport && track.SyncScroll && viewModel.ActiveTab is { } tab)
                {
                    var sourceMap = new TimingMap(track.Session!.Document.Timing);
                    timeline.NavigateToBeat(GetAudioTimingMap(tab).MillisecondsToBeat(sourceMap.BeatToMilliseconds(BeatPosition.FromDouble(beat))).Value);
                }
            };
            surface.PlaybackRateChangeRequested += async (_, direction) => await AdjustWorkbenchRateAsync(direction);
            surface.SnapDivisionChangeRequested += (_, direction) => AdjustQuickSnap(direction);
            view.Surface = surface;
            surface.SizeChanged += (_, _) => SyncTrackViewports();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 0) };
            actions.Children.Add(TrackButton("复制", () => CopyTrackSelection(view)));
            if (track.Kind == EditorTrackKind.Draft)
            {
                actions.Children.Add(TrackButton("粘贴", () => PasteTrackSelection(view)));
                actions.Children.Add(TrackButton("撤销", () => track.Session!.Undo()));
                actions.Children.Add(TrackButton("重做", () => track.Session!.Redo()));
            }
            actions.Children.Add(TrackButton("放入主轨", () => { CopyTrackSelection(view); activeSecondaryTrack = null;
                PasteMixedSelection(); timeline.Focus(); }));
            if (track.Kind == EditorTrackKind.Hitsound)
            {
                var sound = new TextBox { PlaceholderText = "采样文件相对路径", Width = 130 };
                var volume = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 100, Width = 72 };
                surface.SelectionChanged += (_, _) =>
                {
                    var selected = track.Session!.Document.Notes.FirstOrDefault(n => track.Session.Selection.Contains(n.Id));
                    if (selected == null) return;
                    sound.Text = selected.Sound ?? "";
                    volume.Value = selected.Volume;
                };
                var edit = new StackPanel { Spacing = 4, Children = { actions,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { sound, volume,
                        TrackButton("应用", () => ApplyTrackHitsound(view, sound.Text, (int)(volume.Value ?? 100))),
                        TrackButton("清除采样", () => sound.Text = "") } } } };
                Grid.SetRow(edit, 1); body.Children.Add(edit);
            }
            else { Grid.SetRow(actions, 1); body.Children.Add(actions); }
            Grid.SetRow(surface, 2); body.Children.Add(surface);
        }
        else
        {
            var metric = new EditorMetricTrackSurface();
            view.Metric = metric;
            metric.SizeChanged += (_, _) => SyncTrackViewports();
            var info = ToolbarLabel(track.Kind == EditorTrackKind.Density ? "正式键头 / 秒" : "实验性负荷：密度 + 同列间隔 + LN；非星级");
            info.TextWrapping = TextWrapping.Wrap; info.Margin = new Thickness(8);
            Grid.SetRow(info, 1); body.Children.Add(info);
            Grid.SetRow(metric, 2); body.Children.Add(metric);
            metric.SetViewport(track.ViewBeat, timeline.PixelsPerBeat);
            metric.PointerWheelChanged += (_, e) =>
            {
                if (track.SyncScroll || e.KeyModifiers != KeyModifiers.None) timeline.HandleCompanionWheel(e);
                else if (viewModel.ActiveTab is { } tab)
                {
                    var map = GetAudioTimingMap(tab);
                    var milliseconds = map.BeatToMilliseconds(BeatPosition.FromDouble(metric.CurrentBeat)) +
                        (timeline.ReverseTimeWheel ? -e.Delta.Y : e.Delta.Y) * TimelineSurface.PlaybackWheelStepMilliseconds;
                    track.ViewBeat = Math.Max(0, map.MillisecondsToBeat(milliseconds).Value);
                    metric.SetViewport(track.ViewBeat, timeline.PixelsPerBeat);
                    activeTrackWorkspace?.MarkLayoutChanged();
                    e.Handled = true;
                }
            };
            metric.PointerPressed += (_, e) => { activeSecondaryTrack = null; RefreshFixedTools();
                if (e.GetCurrentPoint(metric).Properties.IsRightButtonPressed) OpenAddTrackMenu(metric); };
        }
        view.Host = new Border { Width = view.Surface == null ? 190 : 390, Background = PanelBrush,
            BorderBrush = StrokeBrush, BorderThickness = new Thickness(1), Child = body };
        AutomationProperties.SetAutomationId(view.Host, $"Editor.Track.{track.Kind}.{track.Id:N}");
        return view.Host;
    }

    private Button TrackButton(string title, Action action)
    {
        var button = new Button { Content = title, FontSize = 10, Padding = new Thickness(5, 3) };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception exception) { viewModel.SetStatus($"轨道操作失败：{exception.Message}"); }
        };
        return button;
    }

    private void CopyTrackSelection(TrackView view)
    {
        var session = view.Track.Session!;
        var clipboard = MixedSelectionEditingService.Copy(session, session.Selection, []);
        if (view.Track.Kind == EditorTrackKind.Reference)
            clipboard = clipboard.PatternOnly();
        viewModel.SetEditorClipboard(clipboard);
        viewModel.SetStatus($"已从{view.Track.Name}复制 {clipboard.NoteCount} 个按键");
    }

    private void PasteTrackSelection(TrackView view)
    {
        if (viewModel.EditorClipboard is { IsEmpty: false } clipboard)
            MixedSelectionEditingService.Paste(view.Track.Session!, clipboard.NotesOnly(), BeatPosition.FromDouble(view.Surface!.CurrentBeat));
    }

    private void ApplyTrackHitsound(TrackView view, string? sound, int volume)
    {
        var tab = viewModel.ActiveTab!;
        var ids = view.Track.Session!.Selection.ToHashSet();
        if (ids.Count == 0) { viewModel.SetStatus("请先在 hitsound 轨选择按键。"); return; }
        var relative = sound?.Trim() ?? "";
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
            throw new InvalidOperationException("采样须为歌曲目录内的相对路径。");
        if (relative.Length > 0 && !File.Exists(Path.Combine(Path.GetDirectoryName(tab.SourcePath)!, relative)) &&
            !File.Exists(Path.Combine(Path.GetDirectoryName(tab.WorkingPath)!, relative)))
            throw new FileNotFoundException("歌曲目录中找不到该采样文件。", relative);
        tab.Session.Mutate(document =>
        {
            foreach (var note in document.Notes.Where(n => ids.Contains(n.Id))) { note.Sound = relative.Length == 0 ? null : relative; note.Volume = volume; }
        });
    }

    private void UpdateTrackDocuments(DesktopChartTab tab)
    {
        if (tab.Session.IsInteractiveMutationActive) return;
        ChartDocument? snapshot = null;
        foreach (var view in secondaryTrackViews.Values)
        {
            if (view.MainSerial == tab.Session.ChangeSerial) continue;
            view.MainSerial = tab.Session.ChangeSerial;
            if (view.Track.Kind == EditorTrackKind.Hitsound)
            {
                var selected = view.Track.Session!.Selection.ToArray();
                view.Track.Session.ReplaceDocument(tab.Session.Document.Clone());
                view.Track.Session.Selection.UnionWith(selected);
            }
            if (view.Metric != null)
                view.Metric.SetSnapshotDocument(snapshot ??= tab.Session.CaptureCommittedDocument(), view.Track.Kind == EditorTrackKind.Difficulty);
        }
    }

    private void SyncTrackViewports()
    {
        if (synchronizingTrackViewport || viewModel.ActiveTab is not { } tab) return;
        synchronizingTrackViewport = true;
        try
        {
            var milliseconds = GetAudioTimingMap(tab).BeatToMilliseconds(BeatPosition.FromDouble(timeline.CurrentBeat));
            var anchor = timeline.TranslatePoint(new Point(0, timeline.Bounds.Height * timeline.JudgementLinePosition), trackPanels);
            foreach (var view in secondaryTrackViews.Values)
            {
                if (view.Surface is { } surface)
                {
                    surface.NoteStyle = timeline.NoteStyle;
                    surface.InteractionProfile = timeline.InteractionProfile;
                    surface.ReverseTimeWheel = timeline.ReverseTimeWheel;
                    surface.Session!.SnapDivision = tab.Session.SnapDivision;
                    if (anchor.HasValue && surface.Bounds.Height > 0 && surface.TranslatePoint(new Point(), trackPanels) is { } origin)
                        surface.SetViewportAnchor((anchor.Value.Y - origin.Y) / surface.Bounds.Height);
                    if (!view.Track.SyncScroll) continue;
                    if (view.TimingSerial != surface.Session.ChangeSerial)
                    { view.Timing = new TimingMap(surface.Session.Document.Timing); view.TimingSerial = surface.Session.ChangeSerial; }
                    surface.SetCurrentBeat(Math.Max(0, view.Timing!.MillisecondsToBeat(milliseconds).Value));
                    surface.SetZoom(timeline.PixelsPerBeat);
                    surface.SetPlaybackActive(timeline.IsPlaybackActive);
                }
                else if (view.Track.SyncScroll)
                {
                    view.Track.ViewBeat = timeline.CurrentBeat;
                    var origin = view.Metric?.TranslatePoint(new Point(), trackPanels);
                    view.Metric?.SetViewport(timeline.CurrentBeat, timeline.PixelsPerBeat,
                        anchor.HasValue && origin.HasValue ? anchor.Value.Y - origin.Value.Y : null);
                }
            }
        }
        finally { synchronizingTrackViewport = false; }
    }

    private async Task SaveTrackWorkspacesAsync()
    {
        foreach (var workspace in trackWorkspaces.Values.ToArray()) await workspace.SaveAsync();
    }

    private async Task SaveCopiedTrackWorkspaceAsync(EditorTrackWorkspace workspace)
    {
        try { await workspace.SaveAsync(); }
        catch (Exception exception) { if (!windowCloseTeardownStarted) viewModel.SetStatus($"轨道副本保存失败：{exception.Message}"); }
    }

    private void RefreshTrackCommandButtons()
    {
        if (activeSecondaryTrack?.Track.Session is not { } session) return;
        var selected = session.Selection.Count > 0;
        var editable = !session.IsReadOnly && session.CanEditNotes;
        SetQuickShortcutEnabled("Copy", selected);
        SetQuickShortcutEnabled("Cut", selected && editable);
        SetQuickShortcutEnabled("Paste", editable && viewModel.HasMixedSelectionClipboard);
        SetQuickShortcutEnabled("Undo", editable);
        SetQuickShortcutEnabled("Redo", editable);
        SetQuickShortcutEnabled("SelectAll", true);
        if (quickMirrorButton != null) quickMirrorButton.IsEnabled = editable;
    }

    private bool TryExecuteTrackCommand(string action)
    {
        if (activeSecondaryTrack is not { Surface: { } surface } view) return false;
        var session = view.Track.Session!;
        if (action is "Copy" or "Cut" or "Paste" or "Delete" or "SelectAll" or "Undo" or "Redo" or "Mirror" or "StepForward" or "StepBack" or "GoHome" or "ToolSelect" or "ToolTap" or "ToolHold" or "ToolErase")
        {
            if (action == "Copy") CopyTrackSelection(view);
            else if (action == "ToolSelect") surface.Tool = TimelineTool.Select;
            else if (action == "GoHome") surface.NavigateToBeat(0);
            else if (action is "StepForward" or "StepBack")
            {
                var direction = action == "StepForward" ? 1 : -1;
                if (!session.IsReadOnly && session.Selection.Count > 0) BasicChartEditingService.NudgeSelection(session, direction);
                else surface.NavigateToBeat(TimelineGridNavigation.Step(surface.CurrentBeat, session.SnapDivision, direction).Value);
            }
            else if (action == "SelectAll") { session.Selection.UnionWith(session.Document.Notes.Select(n => n.Id)); surface.InvalidateVisual(); }
            else if (session.IsReadOnly) viewModel.SetStatus("对照和 hitsound 显示轨只读；hitsound 修改请使用轨道中的应用按钮。");
            else if (action == "Undo") session.Undo();
            else if (action == "Redo") session.Redo();
            else if (action == "Paste") PasteTrackSelection(view);
            else if (action == "Mirror") NoteMirrorEditingService.MirrorSelection(session, session.Selection);
            else if (action is "Delete" or "Cut")
            {
                if (action == "Cut") CopyTrackSelection(view);
                var ids = session.Selection.ToHashSet();
                session.Mutate(d => d.Notes.RemoveAll(n => ids.Contains(n.Id)));
            }
            else if (Enum.TryParse<TimelineTool>(action[4..], out var tool)) { surface.Tool = tool; RefreshFixedTools(); }
            RefreshTrackCommandButtons();
            return true;
        }
        return false;
    }
}

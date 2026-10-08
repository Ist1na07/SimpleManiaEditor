using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LumaChart.Application.Playtest;
using LumaChart.Avalonia.Audio;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;
using LumaChart.Application.Workspaces;

namespace LumaChart.Avalonia.Dialogs;

internal sealed record PlaytestWindowDiagnostics(
    double StartMilliseconds,
    int RuntimeNoteCount,
    int KeyCount,
    string WorkingCopyPath,
    bool AutoPlay,
    bool Playing,
    int Combo,
    int Judged,
    double Accuracy,
    bool AudioLoaded,
    bool KeySoundEnabled,
    bool KeySoundWarmUpCompleted,
    bool KeySoundWarmUpSucceeded,
    bool KeySoundIsWarm,
    long KeySoundAccepted,
    long KeySoundRejected,
    long ManualKeySoundAccepted,
    long ManualKeySoundRejected,
    long AutoKeySoundAccepted,
    long AutoKeySoundRejected,
    long KeyDownCount,
    long LanePressAttemptCount,
    long LaneJudgementEventCount,
    long ManualHitJudgementCount,
    string LastManualHitGrade,
    double LastManualHitOffsetMilliseconds,
    string LastResolvedKey,
    double UiScale,
    double FontScale,
    int FrameRateLimit,
    bool HighRateClockEnabled,
    bool HighRateClockRunning,
    long HighRateClockTicks,
    string HighRateClockError,
    PlaytestSurfaceDiagnostics Surface);

/// <summary>Windows Avalonia replica of WPF's modal test-play window.</summary>
internal sealed partial class PlaytestWindow : PlatformWindow
{
    private static readonly IBrush WindowBrush = Brush("#101315");
    private static readonly IBrush HeaderBrush = Brush("#191D20");
    private static readonly IBrush SideBrush = Brush("#171B1E");
    private static readonly IBrush StrokeBrush = Brush("#343B3F");
    private static readonly IBrush ButtonBrush = Brush("#23282B");
    private static readonly IBrush ButtonBorderBrush = Brush("#424A4E");
    private static readonly IBrush TextBrush = Brush("#D9DEDF");
    private static readonly IBrush MutedBrush = Brush("#7F898E");

    private readonly PlaytestSession session;
    private readonly string? audioPath;
    private readonly string? backgroundPath;
    private readonly string workingCopyPath;
    private readonly double playbackRate;
    private readonly bool preservePitch;
    private readonly float musicVolume;
    private readonly WindowsKeySoundSettings keySoundSettings;
    private readonly WindowsAudioPlaybackService audio =
        new(WindowsPlaytestManualKeySoundPolicy.SharedOutputLatencyMilliseconds);
    private readonly WindowsKeySoundService keySounds = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Stopwatch silentClock = new();
    private readonly Stopwatch frameClock = Stopwatch.StartNew();
    private readonly Stopwatch scoreUiClock = new();
    private readonly DispatcherTimer settingsSaveTimer;
    private readonly WindowsHighRateFramePump highRateFramePump = new();
    private readonly WindowsFrameRateGate compositionFrameGate;
    private readonly PlaytestSurface playfield = new();
    private readonly Button pauseButton;
    private readonly Button maximizeButton;
    private readonly TextBlock comboText;
    private readonly TextBlock accuracyText;
    private readonly TextBlock offsetText;
    private readonly TextBlock fpsText;
    private readonly CheckBox autoPlayBox;
    private readonly Slider scrollSpeedSlider;
    private readonly Slider bestWindowSlider;
    private readonly Slider greatWindowSlider;
    private readonly Slider goodWindowSlider;
    private readonly Slider missWindowSlider;
    private readonly TextBlock scrollSpeedLabel;
    private readonly TextBlock bestWindowLabel;
    private readonly TextBlock greatWindowLabel;
    private readonly TextBlock goodWindowLabel;
    private readonly TextBlock missWindowLabel;
    private readonly StackPanel keyBindingPanel;
    private readonly Dictionary<Key, int> laneByKey = [];
    private readonly HashSet<Key> keysDown = [];
    private readonly double uiScale;
    private readonly double fontScale;
    private readonly int frameRateLimit;
    private readonly Dictionary<int, int> touchLanes = [];
    private const double SettingsColumnWidth = 350;
    private Slider? inputOffsetSlider;
    private TextBlock? inputOffsetText;
    private ComboBox? rulesetPicker;
    private TextBlock? stableWindowsText;
    private StackPanel? legacyWindowsPanel;
    private TextBlock? stableScoreText;
    private Button? settingsToggle;
    private Border? settingsSide;
    private bool settingsPanelCollapsed = true;
    private readonly bool liveAutomationDiagnostics =
        string.Equals(
            Environment.GetEnvironmentVariable("LUMACHART_AVALONIA_LIVE_AUTOMATION_DIAGNOSTICS"),
            "1",
            StringComparison.Ordinal);
    private WindowsPlaytestSettings settings;
    private string[] bindings;
    private bool suppressSettings;
    private bool settingsUiReady;
    private bool playing;
    private bool preparing;
    private bool playbackTransitioning;
    private bool animationFrameRequested;
    private bool closing;
    private bool scoreUiDirty = true;
    private bool offsetUiDirty = true;
    private string? highRateClockStartError;
    private int capturingLane = -1;
    private int autoSoundIndex;
    private string pendingOffsetText = "— ms";
    private Task prepareTask = Task.CompletedTask;
    private Task audioReadyTask = Task.CompletedTask;
    private double silentAnchorMilliseconds;
    private long renderedFrames;
    private long presentedFrames;
    private long sampledMillisecondsBits;
    private long keySoundAccepted;
    private long keySoundRejected;
    private long manualKeySoundAccepted;
    private long manualKeySoundRejected;
    private long autoKeySoundAccepted;
    private long autoKeySoundRejected;
    private long keyDownCount;
    private long lanePressAttemptCount;
    private long laneJudgementEventCount;
    private long manualHitJudgementCount;
    private string lastManualHitGrade = nameof(PlaytestHitGrade.None);
    private double lastManualHitOffsetMilliseconds;
    private string lastResolvedKey = nameof(Key.None);
    private bool keySoundWarmUpCompleted;
    private bool keySoundWarmUpSucceeded;
    private int disposed;

    public PlaytestWindow(
        ChartDocument document,
        string? resolvedAudioPath,
        string cacheWorkingCopyPath,
        double startBeat,
        WindowsPlaytestSettings playtestSettings,
        WindowsKeySoundSettings currentKeySoundSettings,
        double currentPlaybackRate,
        bool currentPreservePitch,
        float currentMusicVolume,
        int frameRateLimit,
        double effectiveUiScale,
        double effectiveFontScale,
        bool inheritFullscreen,
        bool autoStart = false,
        string? resolvedBackgroundPath = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheWorkingCopyPath);
        settings = playtestSettings ?? throw new ArgumentNullException(nameof(playtestSettings));
        keySoundSettings = currentKeySoundSettings ?? throw new ArgumentNullException(nameof(currentKeySoundSettings));
        audioPath = string.IsNullOrWhiteSpace(resolvedAudioPath) ? null : resolvedAudioPath;
        backgroundPath = resolvedBackgroundPath;
        workingCopyPath = Path.GetFullPath(cacheWorkingCopyPath);
        playbackRate = double.IsFinite(currentPlaybackRate)
            ? Math.Clamp(currentPlaybackRate, .2, 2)
            : 1;
        preservePitch = currentPreservePitch;
        musicVolume = Math.Clamp(currentMusicVolume, 0, 1);
        uiScale = double.IsFinite(effectiveUiScale) ? Math.Clamp(effectiveUiScale, .75, 2) : 1;
        fontScale = double.IsFinite(effectiveFontScale) ? Math.Clamp(effectiveFontScale, .75, 2) : 1;
        _ = inheritFullscreen; // The player's saved window preference takes precedence.
        bindings = settings.BindingsFor(document.KeyCount);
        session = new PlaytestSession(
            document,
            startBeat,
            settings.AutoPlay,
            settings.JudgementWindows, settings.Ruleset, playbackRate);
        silentAnchorMilliseconds = session.StartMilliseconds;
        sampledMillisecondsBits = BitConverter.DoubleToInt64Bits(session.StartMilliseconds);
        RebuildLaneMap();

        Title = "测试演奏 — SimpleManiaEditor";
        // Desktop sizes belong behind the platform seam. Setting them directly
        // made the window 760 DIP tall on a tablet whose whole viewport is
        // shorter than that, which pushed the header off the top of the screen.
        ConfigureInitialWindowBounds(
            1120 * uiScale,
            760 * uiScale,
            860 * uiScale,
            620 * uiScale);
        // On a device the playfield takes the whole surface instead of sitting
        // in a boxed sheet; the desktop window keeps its own bounds.
        PrefersFullSurface = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = WindowBrush;
        WindowDecorations = WindowDecorations.None;
        CanResize = true;
        ShowInTaskbar = false;
        InputMethod.SetIsInputMethodEnabled(this, false);
        AutomationProperties.SetAutomationId(this, "Playtest.Dialog");

        var rootScale = new ScaleTransform(uiScale, uiScale);
        var built = BuildContent(out pauseButton, out maximizeButton, out comboText, out accuracyText,
            out offsetText, out fpsText, out autoPlayBox, out scrollSpeedSlider,
            out bestWindowSlider, out greatWindowSlider, out goodWindowSlider,
            out missWindowSlider,
            out scrollSpeedLabel, out bestWindowLabel, out greatWindowLabel, out goodWindowLabel,
            out missWindowLabel,
            out keyBindingPanel);
        Content = new LayoutTransformControl
        {
            UseRenderTransform = false,
            LayoutTransform = rootScale,
            Child = built
        };

        // A cap above the panel rate does not add frames, it only makes the
        // tick interval beat against vsync. The visuals then advance unevenly
        // while judgement keeps reading the exact audio clock, so the note
        // under the judgement line can already be outside its window.
        this.frameRateLimit = WindowsFrameRateOptions.ClampToPresentable(
            frameRateLimit,
            PlatformDisplayRefreshRates.Current);
        AutomationProperties.SetHelpText(
            fpsText,
            $"FrameRateLimit={this.frameRateLimit}; " +
            $"HighRateClock={this.frameRateLimit > WindowsFrameRateOptions.CompositionDrivenRate}; " +
            "AudioClockIndependent=true");
        compositionFrameGate = new WindowsFrameRateGate(this.frameRateLimit);
        settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        settingsSaveTimer.Tick += (_, _) =>
        {
            settingsSaveTimer.Stop();
            SaveSettings();
        };

        LoadSettingsUi();
        BuildKeyBindings();
        playfield.SetSession(session);
        playfield.Configure(settings);
        settingsUiReady = true;
        Prepare();
        RefreshAutomationDiagnostics(session.StartMilliseconds);
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, WindowKeyUp, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            if (settings.FullScreen) WindowState = WindowState.FullScreen;
            UpdateMaximizeButton();
            Focus();
            skinLoadTask = LoadSkinAsync(settings.SkinPath);
            await skinLoadTask;
            await LoadBackgroundAsync();
            audioReadyTask = PrepareAudioAsync();
            await audioReadyTask;
            // Closing during the load disposes the audio and frame pump; the
            // rest of this handler would only touch disposed runtime objects.
            if (closing || lifetime.IsCancellationRequested) return;
            settingsUiReady = true;
            RefreshAutomationDiagnostics(CurrentMilliseconds());
            if (this.frameRateLimit > WindowsFrameRateOptions.CompositionDrivenRate)
            {
                try
                {
                    highRateFramePump.Start(this.frameRateLimit, SampleEngineFrame);
                    highRateClockStartError = null;
                }
                catch (Exception exception)
                {
                    highRateClockStartError = exception.Message;
                }
            }
            RequestPlaytestAnimationFrame();
            RefreshAutomationDiagnostics(CurrentMilliseconds());
            if (autoStart) await TogglePauseAsync();
        };
        PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == WindowStateProperty) UpdateMaximizeButton();
        };
#if !LUMACHART_SHARED
        // A key released while another window has focus never reaches KeyUp
        // here. Without this its lane stays lit and the next real press of that
        // key is swallowed as an auto-repeat. (The SingleView surface of the
        // mobile hosts has no window activation to lose.)
        Deactivated += (_, _) => { presentationKeysDown.Clear(); ReleaseAllKeyLanes(); if (playing) _ = PausePlaybackAsync(); };
#endif
        Closing += (_, _) =>
        {
            closing = true;
            highRateFramePump.Stop();
            settingsSaveTimer.Stop();
            SaveSettings();
        };
        Closed += async (_, _) => await DisposeRuntimeAsync();
    }

    public PlaytestWindowDiagnostics Diagnostics => new(
        session.StartMilliseconds,
        session.Notes.Count,
        session.KeyCount,
        workingCopyPath,
        settings.AutoPlay,
        playing,
        session.Combo,
        session.Judged,
        session.Accuracy,
        audio.IsLoaded,
        keySoundSettings.EnableKeySounds,
        keySoundWarmUpCompleted,
        keySoundWarmUpSucceeded,
        keySounds.IsWarm,
        keySoundAccepted,
        keySoundRejected,
        manualKeySoundAccepted,
        manualKeySoundRejected,
        autoKeySoundAccepted,
        autoKeySoundRejected,
        keyDownCount,
        lanePressAttemptCount,
        laneJudgementEventCount,
        manualHitJudgementCount,
        lastManualHitGrade,
        lastManualHitOffsetMilliseconds,
        lastResolvedKey,
        uiScale,
        fontScale,
        frameRateLimit,
        frameRateLimit > WindowsFrameRateOptions.CompositionDrivenRate,
        highRateFramePump.IsRunning,
        highRateFramePump.TotalTickCount,
        highRateClockStartError ?? string.Empty,
        playfield.Diagnostics);

    private Control BuildContent(
        out Button pause,
        out Button maximize,
        out TextBlock combo,
        out TextBlock accuracy,
        out TextBlock offset,
        out TextBlock fps,
        out CheckBox autoPlay,
        out Slider scrollSpeed,
        out Slider bestWindow,
        out Slider greatWindow,
        out Slider goodWindow,
        out Slider missWindow,
        out TextBlock scrollSpeedText,
        out TextBlock bestWindowText,
        out TextBlock greatWindowText,
        out TextBlock goodWindowText,
        out TextBlock missWindowText,
        out StackPanel bindingsPanel)
    {
        var border = new Border
        {
            Background = WindowBrush,
            BorderBrush = Brush("#3B4347"),
            BorderThickness = new Thickness(1),
            Child = new Grid { RowDefinitions = new RowDefinitions("*") }
        };
        var root = (Grid)border.Child;
        var header = new Border
        {
            Height = 44,
            VerticalAlignment = VerticalAlignment.Top,
            Background = HeaderBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new Grid
            {
                Margin = new Thickness(9, 0),
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto")
            }
        };
        header.PointerPressed += (_, eventArgs) =>
        {
            if (!eventArgs.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            if (eventArgs.ClickCount >= 2)
            {
                ToggleMaximizeRestore();
                eventArgs.Handled = true;
                return;
            }
            if (WindowState == WindowState.Normal) BeginMoveDrag(eventArgs);
        };
        var headerGrid = (Grid)header.Child;
        var restart = Button("↺ 重新开始", Prepare);
        AutomationProperties.SetAutomationId(restart, "Playtest.Restart");
        headerGrid.Children.Add(restart);
        pause = Button("▶ 开始", () => _ = TogglePauseAsync());
        AutomationProperties.SetAutomationId(pause, "Playtest.Pause");
        Grid.SetColumn(pause, 1);
        headerGrid.Children.Add(pause);

        var scorePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        combo = ScoreText("0 COMBO", 30, "Playtest.Combo", default);
        combo.HorizontalAlignment = HorizontalAlignment.Center;
        combo.VerticalAlignment = VerticalAlignment.Top;
        combo.IsHitTestVisible = false;
        accuracy = ScoreText("100.00%", 18, "Playtest.Accuracy", default);
        accuracy.HorizontalAlignment = HorizontalAlignment.Center;
        accuracy.VerticalAlignment = VerticalAlignment.Top;
        accuracy.IsHitTestVisible = false;
        offset = ScoreText("— ms", 15, "Playtest.Offset", new Thickness(0, 0, 20, 0));
        offset.FontFamily = new FontFamily("Cascadia Mono,Consolas");
        fps = ScoreText("FPS —", 12, "Playtest.Fps", default);
        fps.Foreground = Brush("#758187");
        fps.FontFamily = new FontFamily("Cascadia Mono,Consolas");
        scorePanel.Children.Add(offset);
        stableScoreText = ScoreText("0000000", 15, "Playtest.Score", new Thickness(0,0,12,0));
        scorePanel.Children.Add(stableScoreText);
        scorePanel.Children.Add(fps);
        Grid.SetColumn(scorePanel, 2);
        headerGrid.Children.Add(scorePanel);

        settingsToggle = Button("设置 ▸", ToggleSettingsPanel);
        settingsToggle.Focusable = false;
        AutomationProperties.SetAutomationId(settingsToggle, "Playtest.ToggleSettings");
        Grid.SetColumn(settingsToggle, 3);
        headerGrid.Children.Add(settingsToggle);

        maximize = Button("□", ToggleMaximizeRestore);
        maximize.Width = 42;
        maximize.Height = 30;
        maximize.Padding = new Thickness(0);
        maximize.Focusable = false;
        AutomationProperties.SetAutomationId(maximize, "Playtest.Maximize");
        Grid.SetColumn(maximize, 4);
        headerGrid.Children.Add(maximize);
        var close = Button("关闭", Close);
        AutomationProperties.SetAutomationId(close, "Playtest.Close");
        Grid.SetColumn(close, 5);
        headerGrid.Children.Add(close);
        root.Children.Add(header);
        header.SetValue(Panel.ZIndexProperty, 20);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*")
        };
        var playfieldHost = new Grid { ClipToBounds = true };
        AttachLaneTouchInput(playfieldHost);
        playfieldHost.Children.Add(playfield);
        combo.SetValue(Panel.ZIndexProperty, 10);
        accuracy.SetValue(Panel.ZIndexProperty, 10);
        combo.IsVisible = accuracy.IsVisible = false;
        playfieldHost.Children.Add(combo);
        playfieldHost.Children.Add(accuracy);
        body.Children.Add(playfieldHost);
        var skinPanel = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        AddPresentationSettings(skinPanel);
        var settingsPanel = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        AddGameSettings(settingsPanel);
        autoPlay = new CheckBox
        {
            Content = "AutoPlay",
            Foreground = Brush("#E4E8E9"),
            FontSize = 14 * fontScale,
            Margin = new Thickness(0, 0, 0, 13)
        };
        AutomationProperties.SetAutomationId(autoPlay, "Playtest.AutoPlay");
        AutomationProperties.SetName(autoPlay, "AutoPlay");
        ToolTip.SetTip(autoPlay, "自动以 BEST 判定演奏谱面；关闭后使用按键手动测试");
        settingsPanel.Children.Add(autoPlay);
        scrollSpeedText = Label("流速");
        settingsPanel.Children.Add(scrollSpeedText);
        scrollSpeed = Slider(1, 40, "Playtest.ScrollSpeed");
        scrollSpeed.TickFrequency = .1;
        scrollSpeed.IsSnapToTickEnabled = true;
        settingsPanel.Children.Add(scrollSpeed);
        settingsPanel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 12) });
        rulesetPicker = new ComboBox { ItemsSource = new[] { "osu! stable 兼容", "旧版自定义判定" },
            SelectedIndex = settings.Ruleset == PlaytestRuleset.OsuStable ? 0 : 1 };
        AutomationProperties.SetAutomationId(rulesetPicker, "Playtest.Ruleset");
        settingsPanel.Children.Add(rulesetPicker);
        stableWindowsText = Label(""); stableWindowsText.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(stableWindowsText, "Playtest.StableWindows");
        settingsPanel.Children.Add(stableWindowsText);
        rulesetPicker.SelectionChanged += (_, _) => SettingsChanged();
        var gameplaySettings = settingsPanel;
        legacyWindowsPanel = new StackPanel { Spacing = 8 };
        gameplaySettings.Children.Add(legacyWindowsPanel);
        settingsPanel = legacyWindowsPanel;
        settingsPanel.Children.Add(new TextBlock
        {
            Text = "自定义判定范围（1–200 ms）",
            FontSize = 15 * fontScale,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#E4E8E9")
        });
        settingsPanel.Children.Add(new TextBlock
        {
            Text = "范围按 Best ≤ Great ≤ Good ≤ Miss 自动约束",
            Foreground = MutedBrush,
            FontSize = 11 * fontScale,
            Margin = new Thickness(0, 2, 0, 9)
        });
        bestWindowText = Label("Best 范围");
        settingsPanel.Children.Add(bestWindowText);
        bestWindow = Slider(1, 200, "Playtest.BestWindow");
        bestWindow.TickFrequency = 1;
        bestWindow.IsSnapToTickEnabled = true;
        settingsPanel.Children.Add(bestWindow);
        greatWindowText = Label("Great 范围");
        settingsPanel.Children.Add(greatWindowText);
        greatWindow = Slider(1, 200, "Playtest.GreatWindow");
        greatWindow.TickFrequency = 1;
        greatWindow.IsSnapToTickEnabled = true;
        settingsPanel.Children.Add(greatWindow);
        goodWindowText = Label("Good 范围");
        settingsPanel.Children.Add(goodWindowText);
        goodWindow = Slider(1, 200, "Playtest.GoodWindow");
        goodWindow.TickFrequency = 1;
        goodWindow.IsSnapToTickEnabled = true;
        settingsPanel.Children.Add(goodWindow);
        missWindowText = Label("Miss 范围");
        settingsPanel.Children.Add(missWindowText);
        missWindow = Slider(1, 200, "Playtest.MissWindow");
        missWindow.TickFrequency = 1;
        missWindow.IsSnapToTickEnabled = true;
        settingsPanel.Children.Add(missWindow);
        settingsPanel = gameplaySettings;
        inputOffsetText = Label("打击偏移");
        settingsPanel.Children.Add(inputOffsetText);
        inputOffsetSlider = Slider(-300, 300, "Playtest.InputOffset");
        inputOffsetSlider.TickFrequency = 1;
        inputOffsetSlider.IsSnapToTickEnabled = true;
        settingsPanel.Children.Add(inputOffsetSlider);
        settingsPanel.Children.Add(new TextBlock
        {
            Text = "正数将输入时间前移（补偿偏晚的打击），负数将输入时间后移。单位为实际毫秒；仅影响手动判定。",
            Foreground = MutedBrush,
            FontSize = 11 * fontScale,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8)
        });
        settingsPanel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 12) });
        var gamePanel = settingsPanel;
        settingsPanel = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        AddBindingImportSettings(settingsPanel);
        settingsPanel.Children.Add(new TextBlock
        {
            Text = "按键绑定",
            FontSize = 15 * fontScale,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#E4E8E9")
        });
        settingsPanel.Children.Add(new TextBlock
        {
            Text = "点击轨道按钮，再按新按键",
            Foreground = MutedBrush,
            FontSize = 11 * fontScale,
            Margin = new Thickness(0, 2, 0, 10)
        });
        bindingsPanel = new StackPanel();
        settingsPanel.Children.Add(bindingsPanel);
        var settingsScroll = new TabControl
        {
            ItemsSource = new[] { SettingsPage("皮肤", skinPanel), SettingsPage("游戏", gamePanel), SettingsPage("按键", settingsPanel) },
            SelectedIndex = 0
        };
        AutomationProperties.SetAutomationId(settingsScroll, "Playtest.SettingsTabs");
        var side = new Border
        {
            Width = SettingsColumnWidth,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 44, 0, 0),
            Background = SideBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = settingsScroll
        };
        settingsSide = side;
        side.SetValue(Panel.ZIndexProperty, 20);
        body.Children.Add(side);
        ApplySettingsPanelVisibility();
        Grid.SetRow(body, 0);
        root.Children.Add(body);
        BuildPresentationOverlay(root, header);

        autoPlay.IsCheckedChanged += (_, _) => SettingsChanged();
        scrollSpeed.ValueChanged += (_, _) => SettingsChanged();
        bestWindow.ValueChanged += (_, _) => SettingsChanged();
        greatWindow.ValueChanged += (_, _) => SettingsChanged();
        goodWindow.ValueChanged += (_, _) => SettingsChanged();
        missWindow.ValueChanged += (_, _) => SettingsChanged();
        if (inputOffsetSlider != null)
            inputOffsetSlider.ValueChanged += (_, _) => SettingsChanged();
        playfield.SizeChanged += (_, _) => UpdateScoreOverlayPositions();
        return border;
    }

    private async Task PrepareAudioAsync()
    {
        keySounds.Volume = (float)keySoundSettings.Volume;
        try
        {
            if (keySoundSettings.Preset == KeySoundPreset.Custom)
                await keySounds.PreloadAsync(keySoundSettings.CustomFilePath, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            // An unreadable or changed custom key sound only loses that sound.
            // This runs from the Opened handler, where an escaping exception
            // would close the whole application instead.
        }
        if (lifetime.IsCancellationRequested) return;
        if (audioPath != null && File.Exists(audioPath))
        {
            try
            {
                await audio.LoadAsync(audioPath, lifetime.Token);
                await audio.SetVolumeAsync(musicVolume, lifetime.Token);
                await audio.SetPreservePitchAsync(preservePitch, lifetime.Token);
                await audio.SetPlaybackRateAsync(playbackRate, lifetime.Token);
                await audio.SeekAsync(TimeSpan.FromMilliseconds(session.StartMilliseconds), lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception exception)
            {
                offsetText.Text = AvaloniaLocalizationCatalog.Format("音频不可用 · {0}", AvaloniaLocalizationCatalog.Text(exception.Message));
            }
        }

        if (lifetime.IsCancellationRequested) return;
        if (!WindowsPlaytestManualKeySoundPolicy.RequiresStandaloneWarmUp(audio.IsLoaded))
        {
            keySoundWarmUpSucceeded = true;
            keySoundWarmUpCompleted = true;
            return;
        }

        try
        {
            keySoundWarmUpSucceeded = await keySounds.WarmUpAsync(lifetime.Token);
            keySoundWarmUpCompleted = true;
        }
        // Closing cancels the gate wait and then disposes the service, so either
        // a cancellation or an ObjectDisposedException can surface here.
        catch (Exception) when (lifetime.IsCancellationRequested) { }
    }

    private void LoadSettingsUi()
    {
        suppressSettings = true;
        autoPlayBox.IsChecked = settings.AutoPlay;
        if (rulesetPicker != null) rulesetPicker.SelectedIndex = settings.Ruleset == PlaytestRuleset.OsuStable ? 0 : 1;
        ConfigureSpeedSlider();
        scrollSpeedSlider.Value = settings.Ruleset == PlaytestRuleset.OsuStable ? settings.ManiaSpeed : settings.ScrollSpeed;
        if (fullScreenBox != null) fullScreenBox.IsChecked = settings.FullScreen;
        if (upsideDownBox != null) upsideDownBox.IsChecked = settings.UpsideDown;
        if (skinSamplesBox != null) skinSamplesBox.IsChecked = settings.UseSkinSamples;
        SyncJudgementWindowSliders();
        suppressSettings = false;
        RefreshSettingsLabels();
        UpdateScoreOverlayPositions();
    }

    private void SettingsChanged()
    {
        if (suppressSettings || !settingsUiReady || session == null) return;
        var previousAutoPlay = settings.AutoPlay;
        var previousRuleset = settings.Ruleset;
        var nextRuleset = rulesetPicker?.SelectedIndex == 1 ? PlaytestRuleset.LegacyCustom : PlaytestRuleset.OsuStable;
        if (previousRuleset != nextRuleset)
        {
            suppressSettings = true;
            ConfigureSpeedSlider(nextRuleset);
            scrollSpeedSlider.Value = nextRuleset == PlaytestRuleset.OsuStable ? settings.ManiaSpeed : settings.ScrollSpeed;
            suppressSettings = false;
        }
        settings = settings.With(
            autoPlay: autoPlayBox.IsChecked == true,
            scrollSpeed: nextRuleset == PlaytestRuleset.LegacyCustom ? scrollSpeedSlider.Value : settings.ScrollSpeed,
            maniaSpeed: nextRuleset == PlaytestRuleset.OsuStable ? scrollSpeedSlider.Value : settings.ManiaSpeed,
            fullScreen: fullScreenBox?.IsChecked ?? settings.FullScreen,
            upsideDown: upsideDownBox?.IsChecked ?? settings.UpsideDown,
            useSkinSamples: skinSamplesBox?.IsChecked ?? settings.UseSkinSamples,
            bestWindowMilliseconds: bestWindowSlider.Value,
            greatWindowMilliseconds: greatWindowSlider.Value,
            goodWindowMilliseconds: goodWindowSlider.Value,
            missWindowMilliseconds: missWindowSlider.Value,
            inputOffsetMilliseconds: inputOffsetSlider?.Value ?? settings.InputOffsetMilliseconds,
            ruleset: nextRuleset);
        suppressSettings = true;
        SyncJudgementWindowSliders();
        suppressSettings = false;
        session.SetAutoPlay(settings.AutoPlay, CurrentMilliseconds());
        session.SetJudgementWindows(settings.JudgementWindows);
        if (previousRuleset != settings.Ruleset)
        {
            session.SetRuleset(settings.Ruleset);
            playfield.SetSession(session);
            Prepare();
        }
        if (!previousAutoPlay && settings.AutoPlay) ResetAutoSoundCursor(CurrentMilliseconds());
        if (previousAutoPlay && !settings.AutoPlay)
            for (var lane = 0; lane < session.KeyCount; lane++)
                if (!IsManualLaneDown(lane)) playfield.SetLanePressed(lane, false);
        playfield.Configure(settings);
        UpdateScoreOverlayPositions();
        RefreshSettingsLabels();
        RefreshAutomationDiagnostics(CurrentMilliseconds());
        if (WindowState == WindowState.FullScreen && !settings.FullScreen) WindowState = WindowState.Normal;
        else if (WindowState != WindowState.FullScreen && settings.FullScreen) WindowState = WindowState.FullScreen;
        settingsSaveTimer.Stop();
        settingsSaveTimer.Start();
    }

    private void RefreshSettingsLabels()
    {
        scrollSpeedLabel.Text = settings.Ruleset == PlaytestRuleset.OsuStable ? $"落速  {settings.ManiaSpeed:0.#} · {WindowsPlaytestScrollVelocityPolicy.ScrollTimeMilliseconds(settings.ManiaSpeed):0} ms" : $"旧版流速 {settings.ScrollSpeed:0} px/s";
        bestWindowLabel.Text = AvaloniaLocalizationCatalog.Format("Best 范围  ±{0:0} ms", settings.BestWindowMilliseconds);
        greatWindowLabel.Text = AvaloniaLocalizationCatalog.Format("Great 范围  ±{0:0} ms", settings.GreatWindowMilliseconds);
        goodWindowLabel.Text = AvaloniaLocalizationCatalog.Format("Good 范围  ±{0:0} ms", settings.GoodWindowMilliseconds);
        missWindowLabel.Text = AvaloniaLocalizationCatalog.Format("Miss 范围  ±{0:0} ms", settings.MissWindowMilliseconds);
        if (inputOffsetText != null)
            inputOffsetText.Text = settings.InputOffsetMilliseconds > .5
                ? AvaloniaLocalizationCatalog.Format("打击偏移  +{0:0} ms", settings.InputOffsetMilliseconds)
                : AvaloniaLocalizationCatalog.Format("打击偏移  {0:0} ms", settings.InputOffsetMilliseconds);
    }

    private void SyncJudgementWindowSliders()
    {
        var stable = settings.Ruleset == PlaytestRuleset.OsuStable;
        if (legacyWindowsPanel != null) legacyWindowsPanel.IsVisible = !stable;
        if (stableScoreText != null) stableScoreText.IsVisible = stable;
        bestWindowSlider.IsEnabled = greatWindowSlider.IsEnabled = goodWindowSlider.IsEnabled = missWindowSlider.IsEnabled = !stable;
        if (stableWindowsText != null)
        {
            stableWindowsText.IsVisible = stable;
            var w = session.StableRules; var rate = w.PlaybackRate;
            stableWindowsText.Text = $"OD {w.OverallDifficulty:0.##} · ScoreV1\nMAX ±{w.Best / rate:0.##} / 300 ±{w.Great / rate:0.##}\n200 ±{w.Good / rate:0.##} / 100 ±{w.Ok / rate:0.##}\n50（早） {w.Meh / rate:0.##} ms · 迟侧超过 100 范围为 MISS\nLN 头尾联合判定；中断后最高 50";
        }
        bestWindowSlider.Value = settings.BestWindowMilliseconds;
        greatWindowSlider.Value = settings.GreatWindowMilliseconds;
        goodWindowSlider.Value = settings.GoodWindowMilliseconds;
        missWindowSlider.Value = settings.MissWindowMilliseconds;
        if (inputOffsetSlider != null)
            inputOffsetSlider.Value = settings.InputOffsetMilliseconds;
    }

    private void UpdateScoreOverlayPositions()
    {
        var height = playfield.Bounds.Height;
        if (!double.IsFinite(height) || height <= 0) return;
        comboText.Margin = new Thickness(
            0,
            Math.Round(height * settings.ComboYPosition),
            0,
            0);
        accuracyText.Margin = new Thickness(
            0,
            Math.Round(height * settings.AccuracyYPosition),
            0,
            0);
    }

    private void BuildKeyBindings()
    {
        keyBindingPanel.Children.Clear();
        for (var lane = 0; lane < session.KeyCount; lane++)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,128") };
            row.Children.Add(new TextBlock
            {
                Text = AvaloniaLocalizationCatalog.Format("轨道 {0}", lane + 1),
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            var captureLane = lane;
            var button = Button(DisplayKey(bindings[lane]), () => BeginKeyCapture(captureLane));
            AutomationProperties.SetAutomationId(button, $"Playtest.Binding.{lane + 1}");
            AutomationProperties.SetName(button, AvaloniaLocalizationCatalog.Format("轨道 {0}：{1}", lane + 1, DisplayKey(bindings[lane])));
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            keyBindingPanel.Children.Add(row);
        }
    }

    private void BeginKeyCapture(int lane)
    {
        capturingLane = lane;
        if (keyBindingPanel.Children[lane] is Grid row && row.Children[1] is Button button)
        {
            button.Content = "请按键…";
            button.Background = Brush("#304F52");
            button.Focus();
        }
    }

    private async void WindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        // async void: anything escaping here reaches the dispatcher's fatal
        // handler and closes the application, editor and unsaved work included.
        // A pause/resume racing the window's close is expected and silent; any
        // other transport failure is reported like the other audio failures.
        try
        {
            await HandleWindowKeyDownAsync(eventArgs);
        }
        catch (Exception) when (lifetime.IsCancellationRequested || closing) { }
        catch (Exception exception)
        {
            offsetText.Text = AvaloniaLocalizationCatalog.Format("音频不可用 · {0}", AvaloniaLocalizationCatalog.Text(exception.Message));
        }
    }

    private async Task HandleWindowKeyDownAsync(KeyEventArgs eventArgs)
    {
        var key = WindowsEditorShortcutGesture.ResolveReportedKey(
            eventArgs.Key,
            eventArgs.PhysicalKey,
            eventArgs.KeySymbol,
            eventArgs.Key is Key.System or Key.ImeProcessed or Key.DeadCharProcessed
                ? WindowsShortcutPhysicalKeyState.TryResolvePressedKey()
                : null);
        keyDownCount++;
        lastResolvedKey = key.ToString();
        if (capturingLane >= 0)
        {
            eventArgs.Handled = true;
            if (WindowsEditorShortcutGesture.IsModifierOnly(key) ||
                key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed)
                return;
            if (key == Key.Escape)
            {
                // Esc cancels the capture. As a lane key it could never be
                // played anyway: outside capture Esc always closes the window.
                capturingLane = -1;
                BuildKeyBindings();
                return;
            }
            var replacement = key.ToString();
            bindings = WindowsPlaytestSettings.AssignBinding(
                bindings,
                capturingLane,
                replacement);
            capturingLane = -1;
            settings = settings.With(
                bindingKeyCount: session.KeyCount,
                bindings: bindings);
            RebuildLaneMap();
            BuildKeyBindings();
            settingsSaveTimer.Stop();
            settingsSaveTimer.Start();
            return;
        }
        if (await HandlePresentationKeyAsync(key, eventArgs)) return;
        if (WindowsPlaytestKeyRoutingPolicy.IsPlaybackToggle(key, bindings))
        {
            eventArgs.Handled = true;
            await TogglePauseAsync();
            return;
        }
        if (!keysDown.Add(key))
        {
            eventArgs.Handled = true;
            return;
        }
        var lane = FindLane(key);
        if (lane < 0) return;
        eventArgs.Handled = true;
        BeginLanePress(lane);
    }

    /// <summary>
    /// One judgement path for every input. A finger on a lane is the same press
    /// as its bound key, so touch cannot drift away from the keyboard's timing
    /// or sound behaviour.
    /// </summary>
    private void BeginLanePress(int lane)
    {
        lanePressAttemptCount++;
        if (!playing || preparing || playbackTransitioning || settings.AutoPlay) return;
        playfield.SetLanePressed(lane, true);
        var judgementEvents = session.PressLane(lane, JudgedMilliseconds());
        laneJudgementEventCount += judgementEvents.Count;
        foreach (var judgement in judgementEvents)
        {
            if (!judgement.PlaySound) continue;
            manualHitJudgementCount++;
            lastManualHitGrade = judgement.Grade.ToString();
            lastManualHitOffsetMilliseconds = judgement.OffsetMilliseconds;
        }
        ProcessEvents(judgementEvents, playImmediateSound: true);
        if (liveAutomationDiagnostics)
            RefreshAutomationDiagnostics(CurrentMilliseconds());
    }

    /// <summary>
    /// Routes touch and pen contacts on the playfield to lane presses. Each
    /// contact is tracked separately so a chord held with several fingers
    /// behaves like the same chord on the keyboard, and a finger that slides
    /// between lanes keeps the lane it started on rather than retriggering.
    /// </summary>
    private void AttachLaneTouchInput(Control surface)
    {
        // Release and capture-lost are bubbling events. Listening on the tunnel
        // phase alone never fires them, which strands the contact in the map:
        // the lane stays lit and, because pointer ids are reused, every later
        // press on that id is rejected as a duplicate.
        const RoutingStrategies laneStrategies =
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble;

        surface.AddHandler(
            PointerPressedEvent,
            (_, eventArgs) =>
            {
                if (eventArgs.Pointer.Type == PointerType.Mouse) return;
                var lane = ResolveTouchLane(surface, eventArgs.GetPosition(surface).X);
                if (lane < 0) return;
                eventArgs.Handled = true;
                if (!touchLanes.TryAdd(eventArgs.Pointer.Id, lane)) return;
                eventArgs.Pointer.Capture(surface);
                BeginLanePress(lane);
            },
            laneStrategies);

        surface.AddHandler(
            PointerReleasedEvent,
            (_, eventArgs) => ReleaseTouchLane(eventArgs.Pointer.Id),
            laneStrategies);
        surface.AddHandler(
            PointerCaptureLostEvent,
            (_, eventArgs) => ReleaseTouchLane(eventArgs.Pointer.Id),
            laneStrategies);
    }

    private void ReleaseTouchLane(int pointerId)
    {
        if (!touchLanes.Remove(pointerId, out var lane)) return;
        EndLanePress(lane);
    }

    /// <summary>
    /// Drops every tracked contact. Playback ending or a pause stops the frame
    /// loop that re-asserts lane highlighting, so a contact left in the map
    /// would keep its lane lit with nothing running to clear it.
    /// </summary>
    private void ReleaseAllTouchLanes()
    {
        if (touchLanes.Count == 0) return;
        foreach (var lane in touchLanes.Values.Distinct().ToArray())
            playfield.SetLanePressed(lane, false);
        touchLanes.Clear();
    }

    /// <summary>
    /// Maps a horizontal position to a lane using the same centred playfield
    /// geometry the surface draws with, so the touch target is exactly the lane
    /// the player sees.
    /// </summary>
    private int ResolveTouchLane(Control surface, double x)
    {
        return playfield.LaneAt(x);
    }

    /// <summary>
    /// Folds the settings column away so the playfield gets the whole surface,
    /// which matters most on a device but is just as useful on a small desktop
    /// window.
    /// </summary>
    private void ToggleSettingsPanel()
    {
        if (playing) _ = PausePlaybackAsync();
        settingsPanelCollapsed = !settingsPanelCollapsed;
        ApplySettingsPanelVisibility();
    }

    private void ApplySettingsPanelVisibility()
    {
        if (settingsSide != null) settingsSide.IsVisible = !settingsPanelCollapsed;
        UpdatePresentation();
        if (settingsToggle != null)
        {
            settingsToggle.Content = settingsPanelCollapsed ? "设置 ◂" : "设置 ▸";
            ToolTip.SetTip(
                settingsToggle,
                settingsPanelCollapsed ? "展开测试演奏设置" : "收起测试演奏设置");
            AutomationProperties.SetName(
                settingsToggle,
                settingsPanelCollapsed ? "展开测试演奏设置" : "收起测试演奏设置");
        }
    }

    private void EndLanePress(int lane)
    {
        if (settings.AutoPlay) return;
        playfield.SetLanePressed(lane, false);
        ProcessEvents(session.ReleaseLane(lane, JudgedMilliseconds()), playImmediateSound: false);
    }

    /// <summary>
    /// Treats every held key as released, the way the lost KeyUp would have.
    /// Held LNs are judged at this moment instead of staying down forever.
    /// </summary>
    private void ReleaseAllKeyLanes()
    {
        if (keysDown.Count == 0) return;
        var released = keysDown.ToArray();
        keysDown.Clear();
        foreach (var key in released)
        {
            var lane = FindLane(key);
            if (lane >= 0) EndLanePress(lane);
        }
    }

    /// <summary>
    /// Persisting playtest preferences is best effort. It runs from a timer tick
    /// and from Closing, where an escaping IO failure (locked or read-only file,
    /// full disk) would close the whole application instead of the playtest.
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            preferencesSaveFailed = !WindowsPlaytestSettingsStore.Save(settings);
            if (preferencesSaveFailed && importStatus != null)
                importStatus.Text = "设置无法保存，请检查本地数据目录权限；本次选择仍然有效。";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                System.Security.SecurityException)
        {
        }
    }

    /// <summary>
    /// The transport clock shifted by the configured input offset. Only manual
    /// judgement uses it; visuals, auto-play and audio stay on the true clock so
    /// the offset moves the judgement window rather than the chart.
    /// </summary>
    private double JudgedMilliseconds() =>
        CurrentMilliseconds() - settings.InputOffsetMilliseconds * playbackRate;

    private void WindowKeyUp(object? sender, KeyEventArgs eventArgs)
    {
        var key = WindowsEditorShortcutGesture.ResolveReportedKey(
            eventArgs.Key,
            eventArgs.PhysicalKey,
            eventArgs.KeySymbol);
        if (presentationKeysDown.Remove(key)) { eventArgs.Handled = true; return; }
        keysDown.Remove(key);
        if (WindowsPlaytestKeyRoutingPolicy.IsPlaybackToggle(key, bindings))
        {
            // Avalonia buttons activate from the completed Space gesture. Consume
            // KeyUp as well as KeyDown so a previously clicked title-bar button
            // cannot maximize, restore or close the window during transport use.
            eventArgs.Handled = true;
            return;
        }
        var lane = FindLane(key);
        if (lane < 0) return;
        eventArgs.Handled = true;
        EndLanePress(lane);
    }

    private void RunFrame()
    {
        renderedFrames++;
        if (frameClock.ElapsedMilliseconds >= 1_000)
        {
            var seconds = Math.Max(.001, frameClock.Elapsed.TotalSeconds);
            var nextFpsText = frameRateLimit > WindowsFrameRateOptions.CompositionDrivenRate
                ? AvaloniaLocalizationCatalog.Format("时钟采样 {0:0} · 画面更新 {1:0}", highRateFramePump.TakeTickCount() / seconds, presentedFrames / seconds)
                : AvaloniaLocalizationCatalog.Format("画面 {0:0} · 更新回调 {1:0}", renderedFrames / seconds, presentedFrames / seconds);
            if (!string.Equals(fpsText.Text, nextFpsText, StringComparison.Ordinal))
                fpsText.Text = nextFpsText;
            renderedFrames = 0;
            presentedFrames = 0;
            frameClock.Restart();
            if (liveAutomationDiagnostics)
                RefreshAutomationDiagnostics(CurrentMilliseconds());
        }
        if (!playing) return;
        var audioClock = audio.SampleClock();
        var current = audioClock.IsLoaded
                ? audioClock.Position.TotalMilliseconds
                : SilentMilliseconds();
        if (settings.AutoPlay) ScheduleAutoPlaySounds(current);
        ProcessEvents(session.Advance(settings.AutoPlay ? current : current - settings.InputOffsetMilliseconds * playbackRate), playImmediateSound: false);
        for (var lane = 0; lane < session.KeyCount; lane++)
            playfield.SetLanePressed(lane, session.IsAutoLanePressed(lane) || IsManualLaneDown(lane));
        playfield.SetCurrentTime(current);
        UpdateScoreUi();
        if (audioClock.IsLoaded && current >= audioClock.Duration.TotalMilliseconds - 5 ||
            !audioClock.IsLoaded && session.IsComplete &&
            current >= (session.Notes.Count == 0 ? 0 : session.Notes[^1].EndMilliseconds) + 500)
            _ = PausePlaybackAsync();
    }

    private void ProcessEvents(
        IReadOnlyList<PlaytestJudgementEvent> events,
        bool playImmediateSound)
    {
        if (events.Count == 0) return;
        var last = events[^1];
        pendingOffsetText =
            $"{last.OffsetMilliseconds / playbackRate:+0;-0;0} ms  {session.JudgementText(last.Grade)}";
        offsetUiDirty = true;
        scoreUiDirty = true;
        foreach (var judgement in events)
            if (judgement.Completed || judgement.IsHead)
                playfield.ReportJudgement(judgement.Grade, judgement.OffsetMilliseconds / playbackRate, judgement.Note.Note.Lane);
        if (playImmediateSound)
        {
            for (var index = 0; index < events.Count; index++)
            {
                if (!events[index].PlaySound) continue;
                PlayHitSound();
                break;
            }
        }
        UpdateScoreUi();
    }

    private void UpdateScoreUi(bool force = false)
    {
        if (!force && !scoreUiDirty && !offsetUiDirty) return;
        if (!force && scoreUiClock.IsRunning && scoreUiClock.ElapsedMilliseconds < 33) return;
        scoreUiClock.Restart();
        if (scoreUiDirty || force)
        {
            scoreUiDirty = false;
            SetScoreText(comboText, $"{session.Combo} COMBO", force);
            if (stableScoreText != null) SetScoreText(stableScoreText, session.Score.ToString("D7", CultureInfo.InvariantCulture), force);
            SetScoreText(
                accuracyText,
                session.Accuracy.ToString("P2", CultureInfo.InvariantCulture),
                force);
        }
        if (offsetUiDirty || force)
        {
            offsetUiDirty = false;
            SetScoreText(offsetText, pendingOffsetText, force);
        }
    }

    private static void SetScoreText(TextBlock target, string value, bool force)
    {
        if (!force && string.Equals(target.Text, value, StringComparison.Ordinal)) return;
        target.Text = value;
        AutomationProperties.SetName(target, value);
    }

    private void PlayHitSound()
    {
        if (!HasPlaytestKeySound) return;
        // A physical hit cannot be scheduled ahead. Keep it on the music
        // transport's low-latency output so Windows never has two competing
        // render clients for one playtest. A rejected shared voice is not
        // redirected to the standalone client while music remains loaded.
        var route = WindowsPlaytestManualKeySoundPolicy.Resolve(audio.IsLoaded);
        var accepted = route.Output == WindowsPlaytestManualKeySoundOutput.SharedMusic
            ? keySounds.PlayThrough(
                audio,
                PlaytestSoundPreset,
                PlaytestSoundFile,
                route.Gain)
            : keySounds.PlayLowLatency(
                PlaytestSoundPreset,
                PlaytestSoundFile,
                route.Gain);
        if (accepted)
        {
            keySoundAccepted++;
            manualKeySoundAccepted++;
        }
        else
        {
            keySoundRejected++;
            manualKeySoundRejected++;
        }
    }

    private void ScheduleAutoPlaySounds(double currentMilliseconds)
    {
        if (!HasPlaytestKeySound) return;
        var notes = session.Notes;
        var outputLatency = audio.IsLoaded
            ? audio.OutputLatencyMilliseconds
            : WindowsKeySoundService.StandaloneOutputLatencyMilliseconds;
        const double lookAhead = 180;
        while (autoSoundIndex < notes.Count)
        {
            var chordStart = autoSoundIndex;
            var chordMilliseconds = notes[chordStart].StartMilliseconds;
            var wallDelay = (chordMilliseconds - currentMilliseconds) / playbackRate;
            if (wallDelay > outputLatency + lookAhead) break;
            do { autoSoundIndex++; }
            while (autoSoundIndex < notes.Count &&
                   Math.Abs(notes[autoSoundIndex].StartMilliseconds - chordMilliseconds) <= .5);
            if (wallDelay < -100) continue;
            var voiceCount = 0;
            for (var index = chordStart; index < autoSoundIndex; index++)
                if (!notes[index].Completed)
                    voiceCount++;
            if (voiceCount == 0) continue;
            var gain = Math.Min(
                1.5f,
                WindowsKeySoundService.ChordVoiceGain(voiceCount) * voiceCount);
            var delay = Math.Max(0, wallDelay - outputLatency);
            var accepted = audio.IsLoaded
                ? keySounds.PlayThrough(
                    audio,
                    PlaytestSoundPreset,
                    PlaytestSoundFile,
                    gain,
                    delay)
                : keySounds.Play(
                    PlaytestSoundPreset,
                    PlaytestSoundFile,
                    gain,
                    delay);
            if (accepted)
            {
                keySoundAccepted++;
                autoKeySoundAccepted++;
            }
            else
            {
                keySoundRejected++;
                autoKeySoundRejected++;
            }
        }
    }

    private void Prepare()
    {
        prepareTask = PrepareAsync();
    }

    private async Task PrepareAsync()
    {
        preparing = true;
        try
        {
            playing = false;
            ReleaseAllTouchLanes();
            silentClock.Reset();
            silentAnchorMilliseconds = session.StartMilliseconds;
            Interlocked.Exchange(
                ref sampledMillisecondsBits,
                BitConverter.DoubleToInt64Bits(session.StartMilliseconds));
            session.Prepare();
            playfield.SetSession(session);
            keysDown.Clear();
            for (var lane = 0; lane < session.KeyCount; lane++) playfield.SetLanePressed(lane, false);
            autoSoundIndex = 0;
            ResetAutoSoundCursor(session.StartMilliseconds);
            comboText.Text = "0 COMBO";
            accuracyText.Text = "100.00%";
            offsetText.Text = "— ms";
            pendingOffsetText = "— ms";
            scoreUiDirty = true;
            offsetUiDirty = true;
            pauseButton.Content = "▶ 开始";
            AutomationProperties.SetName(pauseButton, "开始");
            playfield.SetCurrentTime(session.StartMilliseconds);
            if (audio.IsLoaded)
            {
                // prepareTask is awaited by every start, so a failure here must
                // not stay faulted in it (each later press would rethrow it).
                try
                {
                    await audio.PauseAsync(lifetime.Token);
                    await audio.SeekAsync(TimeSpan.FromMilliseconds(session.StartMilliseconds), lifetime.Token);
                }
                catch (Exception) when (lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    pendingOffsetText = AvaloniaLocalizationCatalog.Format("音频不可用 · {0}", AvaloniaLocalizationCatalog.Text(exception.Message));
                }
            }
            UpdateScoreUi(force: true);
        }
        finally
        {
            preparing = false;
            UpdatePresentation();
            RefreshAutomationDiagnostics(session.StartMilliseconds);
        }
    }

    private async Task TogglePauseAsync()
    {
        // The window is closing: the audio is (being) disposed and there is no
        // playback left to toggle.
        if (lifetime.IsCancellationRequested || playbackTransitioning) return;
        // Starting while Opened was still loading the audio ran on the silent
        // clock, then CurrentMilliseconds() jumped to the paused audio clock the
        // moment IsLoaded flipped and the run froze. Wait for both preparations
        // so a run keeps one clock source; holding the transition flag turns
        // repeated presses during the wait into no-ops instead of a queued
        // start immediately followed by a pause.
        playbackTransitioning = true;
        try
        {
            await prepareTask;
        await audioReadyTask;
        await skinLoadTask;
        }
        finally
        {
            playbackTransitioning = false;
        }
        if (lifetime.IsCancellationRequested) return;
        if (playing)
        {
            await PausePlaybackAsync();
            return;
        }
        playbackTransitioning = true;
        playing = true;
        settingsPanelCollapsed = true;
        ApplySettingsPanelVisibility();
        pauseButton.Content = "Ⅱ 暂停";
        AutomationProperties.SetName(pauseButton, "暂停");
        RefreshAutomationDiagnostics(CurrentMilliseconds());
        try
        {
            if (audio.IsLoaded) await audio.PlayAsync(lifetime.Token);
            else silentClock.Start();
            if (settings.AutoPlay) ScheduleAutoPlaySounds(CurrentMilliseconds());
        }
        catch (Exception) when (lifetime.IsCancellationRequested)
        {
            // Closed mid-start; the runtime is being disposed.
            playing = false;
        }
        catch (Exception exception)
        {
            playing = false;
            ReleaseAllTouchLanes();
            pauseButton.Content = "▶ 开始";
            AutomationProperties.SetName(pauseButton, "开始");
            offsetText.Text = AvaloniaLocalizationCatalog.Format("音频不可用 · {0}", AvaloniaLocalizationCatalog.Text(exception.Message));
        }
        finally
        {
            playbackTransitioning = false;
            UpdatePresentation();
            RefreshAutomationDiagnostics(CurrentMilliseconds());
        }
    }

    private async Task PausePlaybackAsync()
    {
        // A pause racing the window's close finds the lifetime cancelled and the
        // audio disposed (or about to be); there is nothing left to pause.
        if (!playing || lifetime.IsCancellationRequested) return;
        if (audio.IsLoaded)
        {
            try
            {
                await audio.PauseAsync(lifetime.Token);
            }
            catch (Exception) when (lifetime.IsCancellationRequested)
            {
                return;
            }
        }
        else
        {
            silentAnchorMilliseconds += silentClock.Elapsed.TotalMilliseconds * playbackRate;
            silentClock.Reset();
        }
        playing = false;
        ReleaseAllTouchLanes();
        ReleaseAllKeyLanes();
        UpdatePresentation();
        pauseButton.Content = "▶ 继续";
        AutomationProperties.SetName(pauseButton, "继续");
        UpdateScoreUi();
        RefreshAutomationDiagnostics(CurrentMilliseconds());
    }

    /// <summary>
    /// True when the requested cap is below the panel rate and therefore has to
    /// be enforced by us. A platform that reports no refresh rate keeps the
    /// desktop behaviour of always gating.
    /// </summary>
    private bool GatesCompositionFrames() =>
        PlatformDisplayRefreshRates.Current is not { } refreshRate ||
        !double.IsFinite(refreshRate) ||
        frameRateLimit < refreshRate - 1;

    private void RequestPlaytestAnimationFrame()
    {
        if (animationFrameRequested ||
            closing ||
            Volatile.Read(ref disposed) != 0)
            return;
        animationFrameRequested = true;
        RequestAnimationFrame(PlaytestAnimationFrame);
    }

    private void PlaytestAnimationFrame(TimeSpan compositionTimestamp)
    {
        _ = compositionTimestamp;
        animationFrameRequested = false;
        if (closing || Volatile.Read(ref disposed) != 0) return;
        presentedFrames++;
        // The composition callback already paces us. Running a fixed-interval
        // gate on top of it throws away frames the compositor asked for and
        // makes the result less even, so it only engages when the requested cap
        // is genuinely below what the display can present.
        if (!GatesCompositionFrames() || compositionFrameGate.ShouldRender())
            RunFrame();
        RequestPlaytestAnimationFrame();
    }

    private double CurrentMilliseconds()
    {
        var audioClock = audio.SampleClock();
        return audioClock.IsLoaded
            ? audioClock.Position.TotalMilliseconds
            : SilentMilliseconds();
    }

    private double SilentMilliseconds() =>
        silentAnchorMilliseconds + silentClock.Elapsed.TotalMilliseconds * playbackRate;

    private void SampleEngineFrame()
    {
        if (!playing) return;
        Interlocked.Exchange(
            ref sampledMillisecondsBits,
            BitConverter.DoubleToInt64Bits(CurrentMilliseconds()));
    }

    private void ResetAutoSoundCursor(double currentMilliseconds)
    {
        autoSoundIndex = 0;
        while (autoSoundIndex < session.Notes.Count &&
               (session.Notes[autoSoundIndex].Completed ||
                session.Notes[autoSoundIndex].StartMilliseconds < currentMilliseconds - .5))
            autoSoundIndex++;
    }

    private void RebuildLaneMap()
    {
        laneByKey.Clear();
        for (var lane = 0; lane < bindings.Length; lane++)
            if (Enum.TryParse<Key>(bindings[lane], true, out var key)) laneByKey[key] = lane;
    }

    private int FindLane(Key key) => laneByKey.GetValueOrDefault(key, -1);
    private bool IsManualLaneDown(int lane)
    {
        foreach (var pair in laneByKey)
            if (pair.Value == lane && keysDown.Contains(pair.Key))
                return true;
        // Every frame re-asserts lane highlighting from this check, so a held
        // finger has to be visible here or its lane would light up on press and
        // go dark again on the very next frame.
        foreach (var pair in touchLanes)
            if (pair.Value == lane)
                return true;
        return false;
    }

    private void RefreshAutomationDiagnostics(double currentMilliseconds)
    {
        var surface = playfield.Diagnostics;
        var current = double.IsFinite(currentMilliseconds)
            ? currentMilliseconds
            : session.StartMilliseconds;
        var manualKeySoundRoute = WindowsPlaytestManualKeySoundPolicy.Resolve(audio.IsLoaded);
        var detail =
            $"startMilliseconds={session.StartMilliseconds:R}; " +
            $"currentMilliseconds={current:R}; runtimeNotes={session.Notes.Count}; " +
            $"keyCount={session.KeyCount}; autoPlay={settings.AutoPlay}; playing={playing}; " +
            $"preparing={preparing}; playbackTransitioning={playbackTransitioning}; " +
            $"noteStyle={settings.NoteStyle}; noteWidth={settings.NoteWidth:R}; " +
            $"noteHeight={settings.NoteHeight:R}; fieldWidth={settings.PlayfieldWidth:R}; " +
            $"scrollSpeed={settings.ScrollSpeed:R}; judgementLine={settings.JudgementLinePosition:R}; " +
            $"bestWindow={settings.BestWindowMilliseconds:R}; " +
            $"greatWindow={settings.GreatWindowMilliseconds:R}; " +
            $"goodWindow={settings.GoodWindowMilliseconds:R}; " +
            $"missWindow={settings.MissWindowMilliseconds:R}; " +
            $"combo={session.Combo}; judged={session.Judged}; accuracy={session.Accuracy:R}; " +
            $"audioLoaded={audio.IsLoaded}; keySoundEnabled={keySoundSettings.EnableKeySounds}; " +
            $"manualKeySoundOutput={manualKeySoundRoute.Output}; " +
            $"manualKeySoundConfiguredLatencyMilliseconds={manualKeySoundRoute.OutputLatencyMilliseconds:R}; " +
            $"audioOutputLatencyMilliseconds={audio.OutputLatencyMilliseconds:R}; " +
            $"keySoundWarmUpCompleted={keySoundWarmUpCompleted}; " +
            $"keySoundWarmUpSucceeded={keySoundWarmUpSucceeded}; keySoundIsWarm={keySounds.IsWarm}; " +
            $"keySoundAccepted={keySoundAccepted}; keySoundRejected={keySoundRejected}; " +
            $"manualKeySoundAccepted={manualKeySoundAccepted}; manualKeySoundRejected={manualKeySoundRejected}; " +
            $"autoKeySoundAccepted={autoKeySoundAccepted}; autoKeySoundRejected={autoKeySoundRejected}; " +
            $"keySoundLastError={DiagnosticText(keySounds.LastError?.Message)}; " +
            $"keyDownCount={keyDownCount}; lanePressAttemptCount={lanePressAttemptCount}; " +
            $"laneJudgementEventCount={laneJudgementEventCount}; " +
            $"manualHitJudgementCount={manualHitJudgementCount}; " +
            $"lastManualHitGrade={lastManualHitGrade}; " +
            $"lastManualHitOffsetMilliseconds={lastManualHitOffsetMilliseconds:R}; " +
            $"lastResolvedKey={lastResolvedKey}; " +
            $"workingCopy={workingCopyPath}; uiScale={uiScale:R}; fontScale={fontScale:R}; " +
            $"frameRateLimit={frameRateLimit}; " +
            $"highRateClock={frameRateLimit > WindowsFrameRateOptions.CompositionDrivenRate}; " +
            "frameDriver=TopLevel.RequestAnimationFrame; " +
            $"highRateClockRunning={highRateFramePump.IsRunning}; " +
            $"highRateClockTicks={highRateFramePump.TotalTickCount}; " +
            $"highRateClockError={DiagnosticText(highRateClockStartError)}; " +
            $"perNoteControls={surface.PerNoteControlCount}; " +
            $"singleCustomControl={surface.SingleCustomControl}; clipped={surface.VisibleAreaClipping}";
        AutomationProperties.SetHelpText(this, detail);
        AutomationProperties.SetHelpText(playfield, detail);
    }

    private static string DiagnosticText(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');

    private void ToggleMaximizeRestore()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
        }
        else
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        var restore = WindowState is WindowState.Maximized or WindowState.FullScreen;
        maximizeButton.Content = restore ? "❐" : "□";
        ToolTip.SetTip(maximizeButton, restore ? "还原窗口" : "最大化");
        AutomationProperties.SetName(maximizeButton, restore ? "还原窗口" : "最大化");
    }

    private async Task DisposeRuntimeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        playfield.SetSkin(null);
        skinPreview.SetSkin(null);
        activeSkin?.Dispose(); activeSkin = null;
        playfield.SetBackground(null); backgroundImage?.Dispose(); backgroundImage = null;
        highRateFramePump.Dispose();
        try { await audio.DisposeAsync(); } catch { }
        try { await keySounds.DisposeAsync(); } catch { }
        // lifetime is cancelled but deliberately not disposed. Continuations of
        // PrepareAudioAsync, TogglePauseAsync and friends still run after Closed
        // and read lifetime.Token; on a disposed source that getter throws
        // ObjectDisposedException from an async void chain, which closes the
        // application. Without a timer or a requested WaitHandle an undisposed
        // CTS holds only managed state.
    }

    private Button Button(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Foreground = TextBrush,
            Background = ButtonBrush,
            BorderBrush = ButtonBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6),
            Margin = new Thickness(2),
            Cursor = new Cursor(StandardCursorType.Hand),
            FontSize = 12 * fontScale,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => action();
        return button;
    }

    private TextBlock ScoreText(string value, double size, string automationId, Thickness margin)
    {
        var text = new TextBlock
        {
            Text = value,
            Foreground = TextBrush,
            FontSize = size * fontScale,
            FontWeight = size >= 18 ? FontWeight.SemiBold : FontWeight.Normal,
            Margin = margin,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(text, automationId);
        AutomationProperties.SetName(text, value);
        return text;
    }

    private TextBlock Label(string value) => new()
    {
        Text = value,
        Foreground = Brush("#C7CED0"),
        FontSize = 12 * fontScale
    };

    private Slider Slider(double minimum, double maximum, string automationId)
    {
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Margin = new Thickness(0, 3, 0, 8)
        };
        AutomationProperties.SetAutomationId(slider, automationId);
        return slider;
    }

    private static string DisplayKey(string value) => value switch
    {
        "OemSemicolon" => ";",
        "OemOpenBrackets" => "[",
        "OemCloseBrackets" => "]",
        _ => value
    };

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
}

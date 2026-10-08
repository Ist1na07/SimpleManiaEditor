using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using LumaChart.Application.Playtest;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Audio;
using SkiaSharp;

namespace LumaChart.Avalonia.Dialogs;

internal sealed partial class PlaytestWindow
{
    private Border? gameChrome, pauseOverlay;
    private Button? resumeGameButton, retryGameButton, returnGameButton;
    private TextBlock? skinLabel, importStatus;
    private CheckBox? fullScreenBox, upsideDownBox, skinSamplesBox;
    private OsuManiaSkin? activeSkin;
    private int skinGeneration;
    private bool importing;
    private bool skinSoundReady;
    private readonly HashSet<Key> presentationKeysDown = [];
    private Bitmap? backgroundImage;
    private TextBlock? gameStatus;
    private bool HasPlaytestKeySound => settings.UseSkinSamples && skinSoundReady || keySoundSettings.EnableKeySounds;
    private KeySoundPreset PlaytestSoundPreset => settings.UseSkinSamples && skinSoundReady ? KeySoundPreset.Custom : keySoundSettings.Preset;
    private string? PlaytestSoundFile => settings.UseSkinSamples && skinSoundReady ? activeSkin?.HitSoundPath : keySoundSettings.CustomFilePath;

    private void AddPresentationSettings(StackPanel panel)
    {
        skinLabel = Label("皮肤：内置 osu!mania"); skinLabel.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(skinLabel, "Playtest.SkinName"); panel.Children.Add(skinLabel);
        var folder = Button("选择 osu!mania 皮肤目录", () => _ = PickSkinAsync(false));
        AutomationProperties.SetAutomationId(folder, "Playtest.ImportSkinFolder"); panel.Children.Add(folder);
        var archive = Button("导入 .osk 皮肤包", () => _ = PickSkinAsync(true));
        AutomationProperties.SetAutomationId(archive, "Playtest.ImportSkinArchive"); panel.Children.Add(archive);
        var reset = Button("使用内置皮肤", () => _ = UseSkinAsync("")); panel.Children.Add(reset);
        panel.Children.Add(Button("重新加载当前皮肤", () => _ = UseSkinAsync(settings.SkinPath)));
        var import = Button("从 osu!.用户.cfg 导入键位与落速", () => _ = PickConfigAsync());
        AutomationProperties.SetAutomationId(import, "Playtest.ImportOsuConfig"); panel.Children.Add(import);
        var legacy = Button("导入当前皮肤的旧 Key0… 键位", ApplyLegacySkinBindings); panel.Children.Add(legacy);
        importStatus = Label("F1 展开设置；F11 切换全屏。导入只读取本地文件。"); importStatus.TextWrapping = TextWrapping.Wrap; panel.Children.Add(importStatus);
        fullScreenBox = new CheckBox { Content = "默认全屏试玩", Foreground = TextBrush };
        upsideDownBox = new CheckBox { Content = "向上滚动（DDR）", Foreground = TextBrush };
        AutomationProperties.SetAutomationId(fullScreenBox, "Playtest.FullScreen");
        AutomationProperties.SetAutomationId(upsideDownBox, "Playtest.UpsideDown");
        fullScreenBox.IsCheckedChanged += (_, _) => SettingsChanged(); upsideDownBox.IsCheckedChanged += (_, _) => SettingsChanged();
        panel.Children.Add(fullScreenBox); panel.Children.Add(upsideDownBox);
        skinSamplesBox = new CheckBox { Content = "使用皮肤的按键声音", Foreground = TextBrush };
        skinSamplesBox.IsCheckedChanged += (_, _) => SettingsChanged(); panel.Children.Add(skinSamplesBox);
    }

    private void BuildPresentationOverlay(Grid root, Border header)
    {
        gameChrome = header;
        var menu = new StackPanel { Spacing = 12, Width = 250, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        gameStatus = new TextBlock { Text = "PLAYTEST", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = TextBrush, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(gameStatus, "Playtest.Game.Status"); menu.Children.Add(gameStatus);
        resumeGameButton = Button("继续 / 开始", () => _ = TogglePauseAsync());
        retryGameButton = Button("重新开始", () => _ = RetryAsync());
        returnGameButton = Button("返回编辑器", Close);
        foreach (var (button, id) in new[] { (resumeGameButton, "Continue"), (retryGameButton, "Retry"), (returnGameButton, "Return") })
        { button.Height = 52; button.Focusable = false; AutomationProperties.SetAutomationId(button, "Playtest.Game." + id); menu.Children.Add(button); }
        pauseOverlay = new Border { Background = Brush("#B0101315"), Child = menu };
        AutomationProperties.SetAutomationId(pauseOverlay, "Playtest.PauseOverlay"); pauseOverlay.SetValue(Panel.ZIndexProperty, 10); root.Children.Add(pauseOverlay);
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        if (gameChrome != null) gameChrome.IsVisible = !playing || !settingsPanelCollapsed;
        if (pauseOverlay != null) pauseOverlay.IsVisible = !playing && settingsPanelCollapsed;
        if (playfield != null) playfield.Cursor = new Cursor(playing && settingsPanelCollapsed ? StandardCursorType.None : StandardCursorType.Arrow);
        if (gameStatus != null)
        {
            gameStatus.Text = session.IsComplete ? $"试玩结束\n{session.Score:D7}  ·  {session.Accuracy:P2}\n最高连击 {session.MaximumCombo}\n" +
                string.Join("  ", session.Notes.Where(n => n.Completed).GroupBy(n => n.Grade).Select(g => $"{session.JudgementText(g.Key)} {g.Count()}")) : "PLAYTEST\n" + session.Document.Metadata.Title;
            if (resumeGameButton != null) resumeGameButton.IsVisible = !session.IsComplete;
        }
    }

    private void ConfigureSpeedSlider(PlaytestRuleset? mode = null)
    {
        var stable = (mode ?? settings.Ruleset) == PlaytestRuleset.OsuStable;
        scrollSpeedSlider.Minimum = stable ? 1 : 120; scrollSpeedSlider.Maximum = stable ? 40 : 2600;
        scrollSpeedSlider.TickFrequency = stable ? .1 : 10;
    }

    private async Task<bool> HandlePresentationKeyAsync(Key key, KeyEventArgs e)
    {
        bool Is(string name) => Enum.TryParse<Key>(name, true, out var configured) && key == configured;
        if (presentationKeysDown.Contains(key)) { e.Handled = true; return true; }
        if (key != Key.Escape && FindLane(key) >= 0) return false;
        if (key == Key.F11)
        {
            e.Handled = true;
            presentationKeysDown.Add(key);
            settings = settings.With(fullScreen: WindowState != WindowState.FullScreen);
            WindowState = settings.FullScreen ? WindowState.FullScreen : WindowState.Normal;
            if (fullScreenBox != null) { suppressSettings = true; fullScreenBox.IsChecked = settings.FullScreen; suppressSettings = false; }
            SaveSettings(); return true;
        }
        if (key == Key.F1)
        { e.Handled = true; presentationKeysDown.Add(key); if (playing) await PausePlaybackAsync(); ToggleSettingsPanel(); return true; }
        if (key == Key.Escape || Is(settings.PauseKey))
        {
            e.Handled = true;
            presentationKeysDown.Add(key);
            if (!settingsPanelCollapsed) { settingsPanelCollapsed = true; ApplySettingsPanelVisibility(); }
            else await TogglePauseAsync();
            return true;
        }
        // Gameplay bindings take priority over imported global hotkeys.
        var modifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0;
        if (Is(settings.IncreaseSpeedKey) || Is(settings.DecreaseSpeedKey) || modifier && key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract)
        {
            e.Handled = true;
            var increase = Is(settings.IncreaseSpeedKey) || key is Key.OemPlus or Key.Add;
            settings = settings.With(maniaSpeed: settings.ManiaSpeed + (increase ? 1 : -1));
            suppressSettings = true;
            if (settings.Ruleset == PlaytestRuleset.OsuStable) scrollSpeedSlider.Value = settings.ManiaSpeed;
            suppressSettings = false;
            playfield.Configure(settings); RefreshSettingsLabels(); settingsSaveTimer.Stop(); settingsSaveTimer.Start();
            return true;
        }
        if (Is(settings.RetryKey) || key == Key.R && (e.KeyModifiers & KeyModifiers.Control) != 0)
        { e.Handled = true; presentationKeysDown.Add(key); await RetryAsync(); return true; }
        return false;
    }

    private async Task RetryAsync() { await PausePlaybackAsync(); await PrepareAsync(); await TogglePauseAsync(); }

    private async Task PickSkinAsync(bool archive)
    {
        if (importing) return;
        importing = true;
        try
        {
            string? path;
            if (archive)
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "导入 osu! 皮肤包", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("osu! 皮肤") { Patterns = ["*.osk", "*.zip"] }] });
                path = files.FirstOrDefault()?.TryGetLocalPath();
                if (path != null) path = await OsuManiaSkin.ImportArchiveAsync(path, lifetime.Token);
            }
            else
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择包含 skin.ini 的皮肤目录", AllowMultiple = false });
                path = folders.FirstOrDefault()?.TryGetLocalPath();
            }
            if (path != null && !closing) await UseSkinAsync(path);
        }
        catch (Exception e) when (!closing) { if (importStatus != null) importStatus.Text = "皮肤导入失败：" + e.Message; }
        catch (Exception) when (closing) { }
        finally { importing = false; }
    }

    private async Task UseSkinAsync(string path)
    {
        if (await LoadSkinAsync(path)) { settings = settings.With(skinPath: path); SaveSettings(); }
    }

    internal async Task<bool> LoadSkinAsync(string path)
    {
        var generation = ++skinGeneration;
        try
        {
            var next = string.IsNullOrWhiteSpace(path) ? null : await OsuManiaSkin.LoadAsync(path, session.KeyCount, lifetime.Token);
            if (closing || generation != skinGeneration) { next?.Dispose(); return false; }
            var old = activeSkin; activeSkin = next; playfield.SetSkin(next); old?.Dispose();
            skinSoundReady = false;
            if (skinLabel != null) skinLabel.Text = "皮肤：" + (next?.Name ?? "内置 osu!mania");
            if (importStatus != null) importStatus.Text = next == null ? "已使用内置皮肤。" : next.Warnings.Length == 0 ? "皮肤已加载。" : string.Join("\n", next.Warnings.Take(6));
            ApplyPauseSkin();
            foreach (var control in new Control[] { noteWidthSlider, noteHeightSlider, fieldWidthSlider, judgementLineSlider, comboYSlider, accuracyYSlider, styleBox })
            { control.IsEnabled = next == null; ToolTip.SetTip(control, next == null ? null : "当前尺寸、位置与外观由 skin.ini 控制。"); }
            if (next?.HitSoundPath != null)
            {
                var ready = await keySounds.PreloadAsync(next.HitSoundPath, lifetime.Token);
                if (closing || generation != skinGeneration) return false;
                skinSoundReady = ready;
                if (!ready && importStatus != null) importStatus.Text += "\n皮肤按键音无法解码，使用编辑器原音效设定。";
            }
            return true;
        }
        catch (Exception e) when (!closing) { if (importStatus != null) importStatus.Text = "皮肤加载失败，继续使用当前皮肤：" + e.Message; return false; }
        catch (Exception) when (closing) { return false; }
    }

    private async Task LoadBackgroundAsync()
    {
        if (backgroundPath == null || !File.Exists(backgroundPath)) return;
        try
        {
            var image = await Task.Run(() =>
            {
                if (new FileInfo(backgroundPath).Length > 32 * 1024 * 1024) throw new InvalidDataException("背景过大");
                using var data = SKData.Create(backgroundPath); using var codec = SKCodec.Create(data);
                if (codec == null || (long)codec.Info.Width * codec.Info.Height > 16 * 1024 * 1024) throw new InvalidDataException("背景尺寸过大");
                return new Bitmap(backgroundPath);
            }, lifetime.Token);
            if (closing) { image.Dispose(); return; }
            backgroundImage = image; playfield.SetBackground(image);
        }
        catch (Exception) { /* Background artwork never blocks chart play. */ }
    }

    private void ApplyPauseSkin()
    {
        foreach (var (button, name, fallback) in new[] { (resumeGameButton, "pause-continue", "继续 / 开始"), (retryGameButton, "pause-retry", "重新开始"), (returnGameButton, "pause-back", "返回编辑器") })
        {
            if (button == null) continue;
            var image = activeSkin?.Texture(name);
            button.Content = image == null ? fallback : new Image { Source = image.Frames[0], Stretch = Stretch.Uniform, Height = 42 };
        }
        if (pauseOverlay != null)
            pauseOverlay.Background = activeSkin?.Texture("pause-overlay") is { } overlay ? new ImageBrush(overlay.Frames[0]) { Stretch = Stretch.UniformToFill } : Brush("#B0101315");
    }

    private void ApplyLegacySkinBindings()
    {
        if (activeSkin == null) return;
        var keys = Enumerable.Range(0, session.KeyCount).Select(i => OsuPlaytestImport.NormalizeKey(activeSkin.Config.Text("Key" + i))).ToArray();
        if (keys.Any(k => k == null) || keys.Distinct().Count() != keys.Length)
        { if (importStatus != null) importStatus.Text = "皮肤没有完整有效的旧式键位；请导入 osu!.用户.cfg。"; return; }
        ReleaseAllKeyLanes(); bindings = keys.Select(k => k!).ToArray();
        settings = settings.With(bindingKeyCount: session.KeyCount, bindings: bindings); RebuildLaneMap(); BuildKeyBindings(); SaveSettings();
        if (importStatus != null) importStatus.Text = "已导入当前皮肤的键位。";
    }

    private async Task PickConfigAsync()
    {
        if (importing) return; importing = true;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 osu!.用户名.cfg", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("osu! 用户配置") { Patterns = ["osu!.*.cfg", "*.cfg"] }] });
            var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path == null) return;
            var result = await Task.Run(() => OsuPlaytestImport.Read(path, settings), lifetime.Token);
            if (closing) return;
            ReleaseAllKeyLanes(); settings = result.Settings; bindings = settings.BindingsFor(session.KeyCount);
            RebuildLaneMap(); BuildKeyBindings(); LoadSettingsUi(); playfield.Configure(settings);
            await LoadSkinAsync(settings.SkinPath); SaveSettings();
            if (importStatus != null) importStatus.Text = result.Summary;
        }
        catch (Exception e) when (!closing) { if (importStatus != null) importStatus.Text = "配置导入失败：" + e.Message; }
        catch (Exception) when (closing) { }
        finally { importing = false; }
    }
}

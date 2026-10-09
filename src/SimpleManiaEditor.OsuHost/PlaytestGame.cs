using System.Text.Json;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Input.Bindings;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Game;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Input.Bindings;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Configuration;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mania.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Screens;
using osu.Game.Screens.Menu;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using SimpleManiaEditor.PlaytestBridge;
using SixLabors.ImageSharp;
using osu.Framework.Testing;
using osu.Game.Overlays.Settings.Sections;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections.Input;
using osu.Framework.Audio;
using osu.Framework.Bindables;

namespace SimpleManiaEditor.OsuHost;

/// <summary>Official OsuGame with a local practice entry point. Gameplay and UI are upstream.</summary>
internal partial class PlaytestGame : OsuGame
{
    private PlaytestRequest request;
    private string requestPath;
    private readonly bool verify;
    private readonly bool verifySettings;
    private bool launched;
    private bool verificationStarted;
    private PracticePlayer? player;
    public Exception? Failure { get; private set; }
    private readonly bool resident;
    private readonly Action<ResidentMessage>? report;
    private PracticeMenu? menu;
    private readonly System.Diagnostics.Stopwatch launchClock = new();
    private bool residentIdle;
    private bool returning;
    private readonly BindableDouble residentVolume = new(0);
    private double inactiveHz;
    internal void Enqueue(Action action) => Scheduler.Add(action);

    public PlaytestGame(PlaytestRequest request, string requestPath, bool verify, bool verifySettings = false,
        bool resident = false, Action<ResidentMessage>? report = null)
    {
        this.request = request;
        this.requestPath = requestPath;
        this.verify = verify;
        this.verifySettings = verifySettings;
        this.resident = resident;
        this.report = report;
        var api = new DummyAPIAccess();
        api.SetState(APIState.Offline);
        API = api;
    }

    public override void SetHost(GameHost host)
    {
        base.SetHost(host);
        if (!resident) return;
        inactiveHz = host.MaximumInactiveHz;
        // Prime SDL's first-draw visibility before any game frame, then keep the
        // prewarm window hidden. Window commands stay on the official input loop.
        host.DrawThread.Scheduler.Add(() => { host.Window.OnDraw(); host.Window.Hide(); });
        host.Window.Update += () => { if (residentIdle || request.WarmupOnly) host.Window.Hide(); };
    }

    internal void AcceptRequest(string path)
    {
        if (!residentIdle) { report?.Invoke(new("error", path, Detail: "试玩宿主正忙。")); return; }
        try
        {
            var next = PlaytestRequest.Read(path);
            if (!Path.GetFullPath(next.DataRoot).Equals(Path.GetFullPath(request.DataRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("试玩数据目录不匹配。");
            request = next; requestPath = path; launched = false; verificationStarted = false;
            residentIdle = false; player = null; launchClock.Restart();
            residentVolume.Value = 1;
            Host.MaximumInactiveHz = inactiveHz;
            Host.Window.Show(); Host.Window.Raise();
            BeginPractice();
        }
        catch (Exception exception) { report?.Invoke(new("error", path, Detail: exception.Message)); }
    }

    protected override Storage CreateStorage(GameHost host, Storage defaultStorage) => new osu.Game.IO.OsuStorage(host, host.GetStorage(request.DataRoot));
    protected override Loader CreateLoader() => new PracticeStartupLoader(this);

    [BackgroundDependencyLoader]
    private void load(FrameworkConfigManager config)
    {
        if (resident) Audio.AddAdjustment(AdjustableProperty.Volume, residentVolume);
        LocalConfig.SetValue(OsuSetting.ShowFirstRunSetup, false);
        if (!File.Exists(Path.Combine(request.DataRoot, "editor-settings-migrated-v1")))
        {
            LocalConfig.SetValue(OsuSetting.MenuMusic, false);
            config.SetValue(FrameworkSetting.WindowMode, !verify && request.FullScreen ? WindowMode.Fullscreen : WindowMode.Windowed);
            config.SetValue(FrameworkSetting.VolumeUniversal, Math.Clamp(request.Volume, 0, 1));
        }
        if (verify) config.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
    }

    internal async void BeginPractice()
    {
        if (launched) return;
        launched = true;
        try
        {
            // Model managers copy into their content store; PreferHardLinks remains false.
            var parameters = new ImportParameters { ImportImmediately = true };
            var skinMapPath = Path.Combine(request.DataRoot, "editor-skin-imports.json");
            var skinMap = File.Exists(skinMapPath)
                ? JsonSerializer.Deserialize<Dictionary<string, Guid>>(File.ReadAllText(skinMapPath)) ?? [] : [];
            var firstLaunch = !File.Exists(Path.Combine(request.DataRoot, "editor-settings-migrated-v1"));
            foreach (var path in request.SkinPaths.Where(p => Directory.Exists(p) || File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (skinMap.TryGetValue(path, out var id) && SkinManager.GetAllUsableSkins().Any(s => s.ID == id)) continue;
                var importedSkin = await Task.Run(() => SkinManager.Import(new RetainedImportTask(path), parameters));
                if (importedSkin != null) skinMap[path] = importedSkin.ID;
            }
            File.WriteAllText(skinMapPath, JsonSerializer.Serialize(skinMap));
            if (request.WarmupOnly)
            {
                Schedule(() =>
                {
                    ApplySettingsMigration(skinMap, firstLaunch);
                    MusicController.Stop();
                    residentIdle = true;
                    Host.MaximumInactiveHz = 10;
                    Host.Window.Hide();
                    report?.Invoke(new("warm"));
                });
                return;
            }
            if (request.SettingsOnly)
            {
                Schedule(() =>
                {
                    try
                    {
                        Ruleset.Value = RulesetStore.GetRuleset("mania")!;
                        ApplySettingsMigration(skinMap, firstLaunch);
                        Settings.ShowAtControl<SkinSection>();
                        File.WriteAllText(requestPath + ".ready.json", JsonSerializer.Serialize(new { Mode = "settings", Engine = "ppy.osu.Game 2026.916.0" }));
                        report?.Invoke(new("ready", requestPath, launchClock.Elapsed.TotalMilliseconds));
                        if (verify) Scheduler.AddDelayed(async () =>
                        {
                            try
                            {
                                var bindings = Settings.ChildrenOfType<BindingSettings>().Single();
                                var configure = bindings.ChildrenOfType<SettingsButtonV2>().Single();
                                if (!configure.Enabled.Value) throw new InvalidOperationException("Official keyboard configuration is locked in settings-only mode.");
                                configure.Action.Invoke();
                                Scheduler.AddDelayed(async () =>
                                {
                                    try
                                    {
                                        using var shot = await Host.TakeScreenshotAsync();
                                        await shot.SaveAsPngAsync(requestPath + ".png");
                                        if (resident) ReturnToEditor(); else Host.Exit();
                                    }
                                    catch (Exception exception) { Fail(exception); }
                                }, 1000);
                            }
                            catch (Exception exception) { Fail(exception); }
                        }, 3000);
                    }
                    catch (Exception exception) { Fail(exception); }
                });
                return;
            }
            var imported = await Task.Run(() => BeatmapManager.Import(new RetainedImportTask(request.BeatmapArchive), parameters));
            if (imported == null) throw new InvalidDataException("Official beatmap importer rejected the working copy.");
            var beatmapInfo = imported.PerformRead(set => set.Beatmaps.FirstOrDefault(b => (b.Path ?? "").Replace('\\', '/').Equals(request.ChartEntry, StringComparison.OrdinalIgnoreCase))?.Clone())
                ?? throw new InvalidDataException("Official importer did not find the requested difficulty.");
            Schedule(() =>
            {
                try
                {
                    Ruleset.Value = RulesetStore.GetRuleset("mania")!;
                    var nextBeatmap = BeatmapManager.GetWorkingBeatmap(beatmapInfo);
                    Beatmap.Value.TryTransferTrack(nextBeatmap);
                    Beatmap.Value = nextBeatmap;
                    ApplySettingsMigration(skinMap, firstLaunch);
                    var mods = new List<Mod>();
                    if (request.Rate < 1)
                    {
                        var rate = new ManiaModHalfTime();
                        rate.SpeedChange.MinValue = .1; rate.SpeedChange.Value = request.Rate;
                        rate.AdjustPitch.Value = !request.PreservePitch; mods.Add(rate);
                    }
                    else if (request.Rate > 1)
                    {
                        var rate = new ManiaModDoubleTime();
                        rate.SpeedChange.Value = request.Rate;
                        rate.AdjustPitch.Value = !request.PreservePitch; mods.Add(rate);
                    }
                    SelectedMods.Value = mods;
                    var preferencesPath = Path.Combine(request.DataRoot, "practice-preferences.json");
                    var autoPlay = request.AutoPlay;
                    if (File.Exists(preferencesPath))
                    {
                        using var preferences = JsonDocument.Parse(File.ReadAllText(preferencesPath));
                        autoPlay = preferences.RootElement.GetProperty("AutoPlay").GetBoolean();
                    }
                    ScreenStack.Push(new PracticePlayerLoader(() => player = new PracticePlayer(request.StartMilliseconds, preferencesPath, autoPlay, verify)));
                }
                catch (Exception exception) { Fail(exception); }
            });
        }
        catch (Exception exception) { Schedule(() => Fail(exception)); }
    }

    // Official imports normally consume .osz/.osk archives. Keep user's files and
    // our disposable handoff intact, including archives dropped onto the game UI.
    public override Task Import(ImportTask[] imports, ImportParameters parameters = default) =>
        base.Import(imports.Select(RetainedImportTask.Wrap).ToArray(), parameters);

    private sealed class RetainedImportTask : ImportTask
    {
        public RetainedImportTask(string path) : base(path) { }
        private RetainedImportTask(Stream stream, string name) : base(stream, name) { }
        public static ImportTask Wrap(ImportTask task) => task.Stream == null ? new RetainedImportTask(task.Path) : new RetainedImportTask(task.Stream, task.Path);
        public override void DeleteFile() { }
    }

    private void MigrateSettings()
    {
        var config = (ManiaRulesetConfigManager)Dependencies.Get<IRulesetConfigCache>().GetConfigFor(new ManiaRuleset())!;
        config.SetValue(ManiaRulesetSetting.ScrollSpeed, request.ManiaSpeed);
        config.SetValue(ManiaRulesetSetting.ScrollDirection, request.UpsideDown ? ManiaScrollingDirection.Up : ManiaScrollingDirection.Down);
        LocalConfig.SetValue(OsuSetting.AudioOffset, request.InputOffsetMilliseconds);
        var realm = Dependencies.Get<RealmAccess>();
        realm.Write(r =>
        {
            foreach (var (count, keys) in request.KeyBindings)
            {
                if (count < 1 || count > ManiaRuleset.MAX_STAGE_KEYS) continue;
                var defaults = new ManiaRuleset().GetDefaultKeyBindings(count).DistinctBy(b => b.Action).ToArray();
                if (defaults.Length != keys.Length) continue;
                var mapped = keys.Select(ParseInputKey).ToArray();
                if (mapped.Any(k => k == InputKey.None)) continue;
                for (var index = 0; index < defaults.Length; index++)
                {
                    var action = (int)defaults[index].Action;
                    var bindings = r.All<RealmKeyBinding>().Where(b => b.RulesetName == "mania" && b.Variant == count && b.ActionInt == action).ToArray();
                    for (var bindingIndex = 0; bindingIndex < bindings.Length; bindingIndex++)
                        bindings[bindingIndex].KeyCombination = new KeyCombination(bindingIndex == 0 ? mapped[index] : InputKey.None);
                }
            }
        });
    }

    private void ApplySettingsMigration(Dictionary<string, Guid> skinMap, bool firstLaunch)
    {
        var revisionPath = Path.Combine(request.DataRoot, "editor-settings-import-revision.txt");
        var explicitImport = request.SettingsImportRevision.Length > 0 &&
            (!File.Exists(revisionPath) || File.ReadAllText(revisionPath) != request.SettingsImportRevision);
        if (!firstLaunch && !explicitImport) return;
        MigrateSettings();
        if (skinMap.TryGetValue(request.SelectedSkinPath, out var id))
        {
            SkinManager.SetSkinFromConfiguration(id.ToString());
            LocalConfig.SetValue(OsuSetting.Skin, id.ToString());
        }
        File.WriteAllText(Path.Combine(request.DataRoot, "editor-settings-migrated-v1"), "1");
        if (explicitImport) File.WriteAllText(revisionPath, request.SettingsImportRevision);
    }

    private static InputKey ParseInputKey(string key) => key switch
    {
        "OemSemicolon" => InputKey.Semicolon, "OemTilde" => InputKey.Tilde,
        "OemOpenBrackets" => InputKey.BracketLeft, "OemCloseBrackets" => InputKey.BracketRight,
        "OemPlus" => InputKey.Plus, "OemMinus" => InputKey.Minus,
        "OemPipe" => InputKey.BackSlash, "OemQuestion" => InputKey.Slash, "OemQuotes" => InputKey.Quote,
        "OemPeriod" => InputKey.Period, "OemComma" => InputKey.Comma, "OemBackslash" => InputKey.NonUSBackSlash,
        "LeftShift" => InputKey.LShift, "RightShift" => InputKey.RShift,
        "LeftCtrl" => InputKey.LControl, "RightCtrl" => InputKey.RControl,
        "LeftAlt" => InputKey.LAlt, "RightAlt" => InputKey.RAlt,
        "Left" => InputKey.Left, "Right" => InputKey.Right, "Up" => InputKey.Up, "Down" => InputKey.Down,
        "D0" => InputKey.Number0, "D1" => InputKey.Number1, "D2" => InputKey.Number2,
        "D3" => InputKey.Number3, "D4" => InputKey.Number4, "D5" => InputKey.Number5,
        "D6" => InputKey.Number6, "D7" => InputKey.Number7, "D8" => InputKey.Number8, "D9" => InputKey.Number9,
        "Enter" => InputKey.Enter, "Return" => InputKey.Enter,
        _ => Enum.TryParse<InputKey>(key, true, out var parsed) ? parsed : InputKey.None
    };

    protected override void UpdateAfterChildren()
    {
        base.UpdateAfterChildren();
        if (player?.IsLoaded != true || !player.GameplayReady || ScreenStack.CurrentScreen != player || verificationStarted) return;
        verificationStarted = true;
        File.WriteAllText(requestPath + ".ready.json", JsonSerializer.Serialize(new
        {
            Engine = "ppy.osu.Game 2026.916.0", Skin = SkinManager.CurrentSkin.Value.SkinInfo.PerformRead(s => s.Name),
            StartMilliseconds = request.StartMilliseconds, KeyCount = (int)Beatmap.Value.Beatmap.Difficulty.CircleSize,
            OfficialPlayer = player.GetType().BaseType!.FullName, Ruleset = Ruleset.Value.ShortName
        }));
        report?.Invoke(new("ready", requestPath, launchClock.Elapsed.TotalMilliseconds));
        if (verify) Scheduler.AddDelayed(async () =>
        {
            try
            {
                if (player.SongTime < request.StartMilliseconds + 500) throw new InvalidOperationException("Official gameplay clock did not advance.");
                File.WriteAllText(requestPath + ".verified.json", JsonSerializer.Serialize(new
                { Time = player.SongTime, AutoPlay = player.AutoPlaying, Accuracy = player.Score.ScoreInfo.Accuracy, Mods = player.GameplayState.Mods.Select(m => m.Acronym).ToArray() }));
                using var image = await Host.TakeScreenshotAsync();
                await image.SaveAsPngAsync(requestPath + ".png");
                if (verifySettings) VerifySkinSelector();
                else if (resident) ReturnToEditor();
                else Host.Exit();
            }
            catch (Exception exception) { Fail(exception); }
        }, 5500);
    }

    private void VerifySkinSelector()
    {
        // Exercise upstream's actual dropdown/binding, not an imitation selector.
        player!.Pause();
        Settings.ShowAtControl<SkinSection>();
        Scheduler.AddDelayed(async () =>
        {
            try
            {
                var section = Settings.ChildrenOfType<SkinSection>().Single();
                var dropdown = section.ChildrenOfType<FormDropdown<Live<SkinInfo>>>().Single();
                var target = SkinManager.GetAllUsableSkins().First(s => s.ID != SkinManager.CurrentSkinInfo.Value.ID && s.PerformRead(info => !info.Protected && info.Files.Count > 0));
                dropdown.Current.Value = target;
                if (SkinManager.CurrentSkinInfo.Value.ID != target.ID) throw new InvalidOperationException("Official skin dropdown did not apply its selection.");
                var skinName = target.PerformRead(s => s.Name);
                File.WriteAllText(requestPath + ".skin-switched.json", JsonSerializer.Serialize(new { Skin = skinName, Id = target.ID }));
                using var shot = await Host.TakeScreenshotAsync();
                await shot.SaveAsPngAsync(requestPath + ".settings.png");
                if (resident) ReturnToEditor(); else Host.Exit();
            }
            catch (Exception exception) { Fail(exception); }
        }, 1600);
    }

    private void Fail(Exception exception)
    {
        Failure = exception;
        File.WriteAllText(requestPath + ".error.txt", exception.ToString());
        if (resident)
        {
            report?.Invoke(new("error", requestPath, Detail: exception.Message));
            ReturnToEditor();
        }
        else Host.Exit();
    }

    internal void ReturnToEditor()
    {
        if (!resident || residentIdle || returning) return;
        returning = true;
        CloseAllOverlays(true);
        MusicController.Stop();
        if (menu != null && !menu.IsCurrentScreen()) menu.MakeCurrent();
        void FinishReturn()
        {
            if (menu?.IsCurrentScreen() != true) { Schedule(FinishReturn); return; }
            residentIdle = true; returning = false;
            residentVolume.Value = 0; MusicController.Stop();
            Host.MaximumInactiveHz = 10;
            Host.Window.Hide();
            report?.Invoke(new("done", requestPath));
        }
        Schedule(FinishReturn);
    }

    protected override bool OnExiting()
    {
        if (!resident) return base.OnExiting();
        Schedule(ReturnToEditor);
        return true;
    }

    private partial class PracticeStartupLoader(PlaytestGame game) : Loader
    {
        protected override OsuScreen CreateLoadableScreen() => game.menu = new PracticeMenu(game);
    }

    private partial class PracticeMenu(PlaytestGame game) : MainMenu
    {
        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);
            Schedule(game.BeginPractice);
        }
        public override void OnResuming(ScreenTransitionEvent e)
        {
            base.OnResuming(e);
            // Quitting practice returns directly to the mapper's editor.
            if (game.launched && (!game.verify || game.resident))
            {
                if (game.resident) game.ReturnToEditor();
                else game.Host.Exit();
            }
        }
    }

    private partial class PracticePlayerLoader(Func<Player> create) : OsuScreen
    {
        private int retries;
        private bool retry;
        public override bool DisallowExternalBeatmapRulesetChanges => true;
        public override bool? AllowGlobalTrackControl => false;
        [Resolved(CanBeNull = true)]
        private osu.Game.Performance.IHighPerformanceSessionManager? performance { get; set; }
        private IDisposable? highPerformance;

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e); BeginLoad();
        }
        public override void OnResuming(ScreenTransitionEvent e)
        {
            base.OnResuming(e); BeginLoad();
        }
        private void BeginLoad()
        {
            retry = false; ValidForResume = false;
            highPerformance ??= performance?.BeginSession();
            var next = create(); next.RestartCount = retries++;
            next.PrepareLoaderForRestart = _ => { retry = true; ValidForResume = true; };
            LoadComponentAsync(next, loaded =>
            {
                if (this.IsCurrentScreen()) this.Push(loaded);
                else loaded.Dispose();
            });
        }
        public override bool OnExiting(ScreenExitEvent e)
        {
            if (!retry) { highPerformance?.Dispose(); highPerformance = null; }
            return base.OnExiting(e);
        }
        protected override void Dispose(bool isDisposing)
        {
            highPerformance?.Dispose(); highPerformance = null;
            base.Dispose(isDisposing);
        }
    }
}

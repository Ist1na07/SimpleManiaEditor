using osu.Framework.Screens;
using osu.Framework.Allocation;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Game.Beatmaps;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Input.Bindings;
using osu.Game.Rulesets.Mods;
using System.Text.Json;
using osu.Game.Rulesets.Mania.Replays;
using osu.Framework.Graphics;

namespace SimpleManiaEditor.OsuHost;

/// <summary>Official Player with local practice/no-fail and the editor's starting time.
/// The handoff contains only objects starting at/after this time, retaining all timing/audio.
/// No private rendering or scoring API is copied or patched.</summary>
internal partial class PracticePlayer(double startTime, string preferencesPath, bool autoPlay, bool verify = false)
    : Player(new PlayerConfiguration { ShowResults = false }), IKeyBindingHandler<GlobalAction>
{
    [Cached(typeof(IGameplayLeaderboardProvider))]
    private readonly EmptyGameplayLeaderboardProvider leaderboard = new();
    public bool GameplayReady => LoadedBeatmapSuccessfully;
    protected override bool CheckModsAllowFailure() => false;
    protected override bool PauseOnFocusLost => !verify;
    protected override void PrepareReplay()
    {
        if (autoPlay) SetAutoPlay(true);
    }
    protected override ResultsScreen CreateResults(ScoreInfo score) => throw new NotSupportedException();

    protected override GameplayClockContainer CreateGameplayClockContainer(WorkingBeatmap beatmap, double gameplayStart)
    {
        var clock = new MasterGameplayClockContainer(beatmap, gameplayStart);
        if (startTime > gameplayStart) clock.Reset(startTime);
        return clock;
    }

    protected override void LoadComplete()
    {
        base.LoadComplete();
        if (!LoadedBeatmapSuccessfully) return;
        ScoreProcessor.HasCompleted.BindValueChanged(e =>
        {
            if (e.NewValue) Scheduler.AddDelayed(() => { if (this.IsCurrentScreen()) this.Exit(); }, RESULTS_DISPLAY_DELAY);
        });
    }

    private void SetAutoPlay(bool enabled)
    {
        if (enabled)
        {
            var mod = Ruleset.Value.CreateInstance().GetAutoplayMod()!;
            var replay = mod.CreateScoreFromReplayData(GameplayState.Beatmap, GameplayState.Mods);
            replay.Replay.Frames.RemoveAll(f => f.Time < GameplayClockContainer.CurrentTime);
            // The generator's original empty leading frame was sliced away.
            // Keep a neutral state immediately before the new start, so the first
            // frame creates a key-down transition instead of an already-held key.
            replay.Replay.Frames.Insert(0, new ManiaReplayFrame(GameplayClockContainer.CurrentTime - 1, []));
            DrawableRuleset.SetReplayScore(replay);
        }
        else DrawableRuleset.SetReplayScore(null!);
        File.WriteAllText(preferencesPath, JsonSerializer.Serialize(new { AutoPlay = enabled }));
    }

    public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
    {
        if (e.Repeat) return false;
        switch (e.Action)
        {
            case GlobalAction.EditorTestPlayToggleAutoplay:
                SetAutoPlay(DrawableRuleset.ReplayScore == null); return true;
            case GlobalAction.EditorTestPlayToggleQuickPause:
                if (GameplayClockContainer.IsPaused.Value) GameplayClockContainer.Start();
                else GameplayClockContainer.Stop();
                return true;
            default: return false;
        }
    }

    public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e) { }
    public double SongTime => GameplayClockContainer.CurrentTime;
    public bool AutoPlaying => DrawableRuleset.ReplayScore != null;
    public override void OnEntering(ScreenTransitionEvent e)
    {
        base.OnEntering(e);
        // Official EditorPlayer also finishes the game-entry animation.
        FinishTransforms(); Alpha = 1; Scale = osuTK.Vector2.One;
        GameplayClockContainer.FinishTransforms(false, nameof(Alpha));
        GameplayClockContainer.Alpha = 1;
    }
}

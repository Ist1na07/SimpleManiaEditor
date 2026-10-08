// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// Adapted Classic native-mania windows from ppy/osu ManiaHitWindows.cs at
// 7e25f111466f1b5648d86856e5737d852402effe. See src/licenses/osu-MIT.txt.
namespace LumaChart.Application.Playtest;

public enum PlaytestRuleset { LegacyCustom, OsuStable }

public sealed class StableManiaRules
{
    public StableManiaRules(double overallDifficulty, double playbackRate = 1)
    {
        OverallDifficulty = double.IsFinite(overallDifficulty) ? Math.Clamp(overallDifficulty, 0, 10) : 8;
        PlaybackRate = double.IsFinite(playbackRate) ? Math.Clamp(playbackRate, .2, 2) : 1;
        var invertedOd = 10 - OverallDifficulty;
        // Native mania / Classic / ScoreV1. Rate compensation preserves real-time
        // windows; the floor and half millisecond reproduce stable rounding.
        Best = Math.Floor(16 * PlaybackRate) + .5;
        Great = Math.Floor((34 + 3 * invertedOd) * PlaybackRate) + .5;
        Good = Math.Floor((67 + 3 * invertedOd) * PlaybackRate) + .5;
        Ok = Math.Floor((97 + 3 * invertedOd) * PlaybackRate) + .5;
        Meh = Math.Floor((121 + 3 * invertedOd) * PlaybackRate) + .5;
        Miss = Math.Floor((158 + 3 * invertedOd) * PlaybackRate) + .5;
    }

    public double OverallDifficulty { get; }
    public double PlaybackRate { get; }
    public double Best { get; }
    public double Great { get; }
    public double Good { get; }
    public double Ok { get; }
    public double Meh { get; }
    public double Miss { get; }

    public PlaytestHitGrade TapGrade(double offset)
    {
        // Stable expires notes after the late OK window: late 50s are impossible.
        if (!double.IsFinite(offset) || offset >= Ok) return PlaytestHitGrade.Miss;
        var error = Math.Abs(offset);
        return error < Best ? PlaytestHitGrade.Best : error < Great ? PlaytestHitGrade.Great :
            error < Good ? PlaytestHitGrade.Good : error < Ok ? PlaytestHitGrade.Ok :
            error < Meh ? PlaytestHitGrade.Meh : PlaytestHitGrade.Miss;
    }

    // ScoreV1 combined head/release rules from the official osu!mania judgement
    // wiki. This intentionally differs from lazer's two ScoreV2 judgements and
    // its 1.5x release lenience.
    public PlaytestHitGrade HoldGrade(double headError, double releaseOffset, bool broken)
    {
        if (!double.IsFinite(releaseOffset) || releaseOffset < -Meh || releaseOffset >= Ok)
            return PlaytestHitGrade.Miss;
        if (broken || !double.IsFinite(headError)) return PlaytestHitGrade.Meh;
        var combined = Math.Abs(headError) + Math.Abs(releaseOffset);
        return Math.Abs(headError) < Best * 1.2 && combined < Best * 2.4 ? PlaytestHitGrade.Best :
            Math.Abs(headError) < Great * 1.1 && combined < Great * 2.2 ? PlaytestHitGrade.Great :
            Math.Abs(headError) < Good && combined < Good * 2 ? PlaytestHitGrade.Good :
            Math.Abs(headError) < Ok && combined < Ok * 2 ? PlaytestHitGrade.Ok : PlaytestHitGrade.Meh;
    }

    public static double AccuracyValue(PlaytestHitGrade grade) => grade switch
    {
        PlaytestHitGrade.Best or PlaytestHitGrade.Great => 1,
        PlaytestHitGrade.Good => 2d / 3,
        PlaytestHitGrade.Ok => 1d / 3,
        PlaytestHitGrade.Meh => 1d / 6,
        _ => 0
    };
    public static string GradeText(PlaytestHitGrade grade) => grade switch
    {
        PlaytestHitGrade.Best => "MAX", PlaytestHitGrade.Great => "300", PlaytestHitGrade.Good => "200",
        PlaytestHitGrade.Ok => "100", PlaytestHitGrade.Meh => "50", PlaytestHitGrade.Miss => "MISS", _ => ""
    };
}

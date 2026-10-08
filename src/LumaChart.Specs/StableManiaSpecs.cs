using LumaChart.Application.Playtest;
using LumaChart.Core.Models;

internal static class StableManiaSpecs
{
    public static void Run()
    {
        // Independent expected values from ppy/osu Classic and the official wiki.
        foreach (var (od, expected) in new[] { (0d, new[] {16.5,64.5,97.5,127.5,151.5,188.5}),
            (5d, new[] {16.5,49.5,82.5,112.5,136.5,173.5}), (8d, new[] {16.5,40.5,73.5,103.5,127.5,164.5}),
            (10d, new[] {16.5,34.5,67.5,97.5,121.5,158.5}) })
        {
            var rules = new StableManiaRules(od);
            Check(new[] { rules.Best,rules.Great,rules.Good,rules.Ok,rules.Meh,rules.Miss }.SequenceEqual(expected), "OD windows");
            Check(rules.TapGrade(16.499) == PlaytestHitGrade.Best && rules.TapGrade(16.5) == PlaytestHitGrade.Great, "stable half-ms boundary");
            Check(rules.TapGrade(-rules.Ok - 1) == PlaytestHitGrade.Meh && rules.TapGrade(rules.Ok + 1) == PlaytestHitGrade.Miss, "late 50 must be impossible");
        }
        foreach (var rate in new[] {.25,.5,1d,1.5,2d})
        {
            var rules = new StableManiaRules(8, rate);
            Check(rules.TapGrade(15 * rate) == PlaytestHitGrade.Best && rules.TapGrade(30 * rate) == PlaytestHitGrade.Great, "rate changed real-time windows");
        }
        var r = new StableManiaRules(8);
        Check(r.HoldGrade(18,18,false) == PlaytestHitGrade.Best && r.HoldGrade(34,50,false) == PlaytestHitGrade.Great,
            "ScoreV1 did not combine head/tail errors with LN lenience");
        Check(r.HoldGrade(0,0,true) == PlaytestHitGrade.Meh && r.HoldGrade(0,r.Ok,true) == PlaytestHitGrade.Miss, "broken hold cap/late deadline");
        var source = new ChartDocument { KeyCount = 7, Notes = [new ChartNote { Lane=0, Beat=new(2,0,1), EndBeat=new(3,0,1) }] };
        PlaytestSession New(bool auto=false) => new(source,0,auto,ruleset:PlaytestRuleset.OsuStable);
        var hold = New(); hold.PressLane(0,1000); hold.Advance(1500);
        Check(!hold.Notes[0].Completed && hold.Judged == 0, "hold auto-completed at tail without release");
        hold.ReleaseLane(0,1500);
        Check(hold.Judged == 1 && hold.Accuracy == 1 && hold.Notes[0].Grade == PlaytestHitGrade.Best, "perfect combined LN score");
        hold=New(); hold.PressLane(0,1000); hold.ReleaseLane(0,1100); hold.PressLane(0,1300); hold.ReleaseLane(0,1500);
        Check(hold.Judged == 1 && hold.Notes[0].Grade == PlaytestHitGrade.Meh && Math.Abs(hold.Accuracy-1d/6)<1e-10, "break/regrab should cap one LN result to 50");
        hold=New(); hold.PressLane(0,1000); hold.Advance(1604);
        Check(hold.Notes[0].Grade == PlaytestHitGrade.Miss && hold.Judged == 1, "unreleased tail did not miss");
        hold=New(); hold.Advance(1250); hold.PressLane(0,1300); hold.ReleaseLane(0,1500);
        Check(hold.Notes[0].Grade == PlaytestHitGrade.Meh, "missed head could not recover hold body");
        hold=New(true); hold.Advance(1500);
        Check(hold.Judged == 1 && hold.Accuracy == 1 && hold.IsComplete && hold.Score == 1_000_000, "autoplay LN/ScoreV1 perfect score");
        Check(!source.Notes[0].IsDraft && source.Notes[0].Beat.Value == 2, "playtest mutated source");
        var taps = new PlaytestSession(new ChartDocument { KeyCount=7, Notes=[new ChartNote {Lane=0,Beat=new(2,0,1)},new ChartNote {Lane=0,Beat=new(3,0,1)}] },0,false,ruleset:PlaytestRuleset.OsuStable);
        taps.PressLane(0,1030); taps.PressLane(0,1560);
        Check(Math.Abs(taps.Accuracy-5d/6)<1e-10, "300/200 accuracy weights");
        Check(taps.Score is > 700000 and < 850000, "ScoreV1 bonus calculation");
        var tempo = new ChartDocument { KeyCount=7, Timing=[new TimingEvent { Bpm=120 },new TimingEvent { Beat=new(4,0,1),Bpm=240 },
            new TimingEvent {Beat=new(2,0,1),Effect=TimingEffectKind.Scroll,EffectValue=2},
            new TimingEvent {Beat=new(4,0,1),Effect=TimingEffectKind.Scroll,EffectValue=1}],
            Notes=[new ChartNote { Lane=0,Beat=new(8,0,1) }] };
        var scroll = new PlaytestSession(tempo,0,false,ruleset:PlaytestRuleset.OsuStable);
        Check(scroll.StableScrollMap.BaseBpm == 120 && Math.Abs(scroll.ScrollPositionAtMilliseconds(3000)-5)<1e-9, "dominant BPM and piecewise SV integration");
        Check(scroll.ScrollPositionAtMilliseconds(1000.001)>scroll.ScrollPositionAtMilliseconds(1000), "scroll clock was quantized to beat grid");
    }
    private static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
}

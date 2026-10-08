namespace LumaChart.Application.Playtest;

public sealed partial class PlaytestSession
{
    private readonly PlaytestRuntimeNote[] stableHoldsByEnd;
    private int stableTailIndex;
    private double stableScore;
    private double stableBonus = 100;
    private int scoringNoteCount;
    public long Score => (long)Math.Clamp(Math.Round(stableScore), 0, 1_000_000);
    public StableManiaRules StableRules { get; }
    public ManiaScrollMap StableScrollMap { get; }
    public PlaytestRuleset Ruleset { get; private set; }
    public bool UsesStableRules => Ruleset == PlaytestRuleset.OsuStable;
    public double ScrollPositionAtMilliseconds(double milliseconds) => UsesStableRules
        ? StableScrollMap.PositionAt(milliseconds) : ScrollMap.PositionAt(TimingMap.MillisecondsToBeat(milliseconds));
    public string JudgementText(PlaytestHitGrade grade) => UsesStableRules ? StableManiaRules.GradeText(grade) : GradeText(grade);

    public void SetRuleset(PlaytestRuleset ruleset)
    {
        Ruleset = Enum.IsDefined(ruleset) ? ruleset : PlaytestRuleset.OsuStable;
        foreach (var note in notes)
        {
            note.StartScrollPosition = ScrollPositionAtMilliseconds(note.StartMilliseconds);
            note.EndScrollPosition = ScrollPositionAtMilliseconds(note.EndMilliseconds);
        }
        Prepare();
    }

    private void AddStableScore(PlaytestHitGrade grade)
    {
        if (scoringNoteCount == 0) return;
        var (value, bonusValue, change) = grade switch
        {
            PlaytestHitGrade.Best => (320d,32d,2d), PlaytestHitGrade.Great => (300d,32d,1d),
            PlaytestHitGrade.Good => (200d,16d,-8d), PlaytestHitGrade.Ok => (100d,8d,-24d),
            PlaytestHitGrade.Meh => (50d,4d,-44d), _ => (0d,0d,-100d)
        };
        stableBonus = Math.Clamp(stableBonus + change, 0, 100);
        stableScore += 500_000d / scoringNoteCount * (value + bonusValue * Math.Sqrt(stableBonus)) / 320;
    }

    private IReadOnlyList<PlaytestJudgementEvent> PressStable(int lane, double now)
    {
        var events = AdvanceStable(now).ToList();
        var active = activeHolds[lane];
        if (active is { Completed: false, HeadJudged: true } && now < active.EndMilliseconds + StableRules.Ok)
        {
            active.Holding = true;
            return events;
        }
        var queue = laneQueues[lane];
        var index = laneIndexes[lane];
        while (index < queue.Count && queue[index].HeadJudged) index++;
        laneIndexes[lane] = index;
        if (index >= queue.Count) return events;
        var candidate = queue[index];
        var offset = now - candidate.StartMilliseconds;
        if (offset < -StableRules.Miss || offset >= StableRules.Ok) return events;
        candidate.HeadJudged = true;
        candidate.HeadError = Math.Abs(offset);
        candidate.Grade = StableRules.TapGrade(offset);
        laneIndexes[lane]++;
        if (candidate.Note.IsHold)
        {
            candidate.Holding = true;
            activeHolds[lane] = candidate;
        }
        else Complete(candidate);
        events.Add(Event(candidate, offset, true, true));
        return events;
    }

    private IReadOnlyList<PlaytestJudgementEvent> ReleaseStable(int lane, double now)
    {
        var hold = activeHolds[lane];
        if (hold == null || hold.Completed || !hold.Holding) return EmptyEvents;
        hold.Holding = false;
        var offset = now - hold.EndMilliseconds;
        if (offset < -StableRules.Meh)
        {
            hold.HoldBroken = true;
            combo = 0;
            // Body breaks are feedback, not another accuracy judgement.
            return [new PlaytestJudgementEvent(hold, PlaytestHitGrade.Miss, offset, false, false, false)];
        }
        hold.Grade = StableRules.HoldGrade(hold.HeadError, offset, hold.HoldBroken);
        Complete(hold);
        return [Event(hold, offset, false, false)];
    }

    private IReadOnlyList<PlaytestJudgementEvent> AdvanceStable(double now)
    {
        List<PlaytestJudgementEvent>? events = null;
        if (AutoPlay) RunAutoPlay(now, ref events);
        while (globalMissIndex < notes.Count && now - notes[globalMissIndex].StartMilliseconds >= StableRules.Ok)
        {
            var note = notes[globalMissIndex++];
            if (note.HeadJudged) continue;
            note.HeadJudged = true;
            note.Grade = PlaytestHitGrade.Miss;
            if (note.Note.IsHold)
            {
                note.HoldBroken = true;
                activeHolds[note.Note.Lane] = note;
                combo = 0;
            }
            else Complete(note);
            (events ??= []).Add(Event(note, now - note.StartMilliseconds, true, false));
        }
        if (AutoPlay)
        {
            for (var lane = 0; lane < activeHolds.Length; lane++)
            {
                if (activeHolds[lane] is { Holding: true, Completed: false } note && now >= note.EndMilliseconds)
                {
                    note.Holding = false; note.Grade = PlaytestHitGrade.Best;
                    Complete(note);
                    (events ??= []).Add(Event(note, 0, false, false));
                }
            }
        }
        while (stableTailIndex < stableHoldsByEnd.Length && now >= stableHoldsByEnd[stableTailIndex].EndMilliseconds + StableRules.Ok)
        {
            var note = stableHoldsByEnd[stableTailIndex++];
            if (note.Completed) continue;
            note.Holding = false; note.Grade = PlaytestHitGrade.Miss;
            Complete(note);
            (events ??= []).Add(Event(note, now - note.EndMilliseconds, false, false));
        }
        for (var lane = 0; lane < autoReleaseMilliseconds.Length; lane++)
            if (now >= autoReleaseMilliseconds[lane]) autoReleaseMilliseconds[lane] = 0;
        return events ?? EmptyEvents;
    }
}

using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Playtest;

public enum PlaytestHitGrade
{
    None,
    Best,
    Great,
    Good,
    Ok,
    Meh,
    Miss
}

public sealed class PlaytestRuntimeNote
{
    internal PlaytestRuntimeNote(
        ChartNote note,
        double startMilliseconds,
        double endMilliseconds,
        double startScrollPosition,
        double endScrollPosition)
    {
        Note = note;
        StartMilliseconds = startMilliseconds;
        EndMilliseconds = endMilliseconds;
        StartScrollPosition = startScrollPosition;
        EndScrollPosition = endScrollPosition;
    }

    public ChartNote Note { get; }
    public double StartMilliseconds { get; }
    public double EndMilliseconds { get; }
    public double StartScrollPosition { get; internal set; }
    public double EndScrollPosition { get; internal set; }
    internal double HeadError { get; set; }
    internal bool HoldBroken { get; set; }
    public bool HeadJudged { get; internal set; }
    public bool Holding { get; internal set; }
    public bool Completed { get; internal set; }
    public PlaytestHitGrade Grade { get; internal set; }
}

public sealed record PlaytestJudgementEvent(
    PlaytestRuntimeNote Note,
    PlaytestHitGrade Grade,
    double OffsetMilliseconds,
    bool IsHead,
    bool Completed,
    bool PlaySound);

public readonly record struct PlaytestJudgementWindows
{
    public PlaytestJudgementWindows(
        double bestMilliseconds,
        double greatMilliseconds,
        double goodMilliseconds,
        double missMilliseconds)
    {
        BestMilliseconds = Normalize(bestMilliseconds, 45);
        GreatMilliseconds = Math.Max(
            BestMilliseconds,
            Normalize(greatMilliseconds, 85));
        GoodMilliseconds = Math.Max(
            GreatMilliseconds,
            Normalize(goodMilliseconds, 130));
        MissMilliseconds = Math.Max(
            GoodMilliseconds,
            Normalize(missMilliseconds, 170));
    }

    public static PlaytestJudgementWindows Default { get; } = new(45, 85, 130, 170);

    public double BestMilliseconds { get; }
    public double GreatMilliseconds { get; }
    public double GoodMilliseconds { get; }
    public double MissMilliseconds { get; }

    private static double Normalize(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, 1, 200) : fallback;
}

/// <summary>
/// UI-independent WPF 1.1.0 playtest judgement state machine. Windows hosts own
/// clocks, audio, keyboard routing and drawing; this type owns strict lane order,
/// judgement windows, holds, AutoPlay and score state.
/// </summary>
public sealed partial class PlaytestSession
{
    private static readonly IReadOnlyList<PlaytestJudgementEvent> EmptyEvents =
        Array.Empty<PlaytestJudgementEvent>();

    public const double BestWindowMilliseconds = 45;
    public const double GreatWindowMilliseconds = 85;
    public const double GoodWindowMilliseconds = 130;
    public const double MissWindowMilliseconds = 170;

    private readonly List<PlaytestRuntimeNote> notes;
    private readonly List<PlaytestRuntimeNote>[] laneQueues;
    private readonly int[] laneIndexes;
    private readonly PlaytestRuntimeNote?[] activeHolds;
    private readonly double[] autoReleaseMilliseconds;
    private int globalMissIndex;
    private int autoPlayIndex;
    private int combo;
    private int maximumCombo;
    private int judged;
    private int completed;
    private double earnedAccuracy;

    public PlaytestSession(
        ChartDocument source,
        double startBeat,
        bool autoPlay,
        PlaytestJudgementWindows? judgementWindows = null,
        PlaytestRuleset ruleset = PlaytestRuleset.LegacyCustom,
        double playbackRate = 1)
    {
        ArgumentNullException.ThrowIfNull(source);
        Document = source.Clone();
        TimingMap = new TimingMap(Document.Timing);
        ScrollMap = new ScrollSpeedMap(Document.Timing);
        KeyCount = Math.Clamp(Document.KeyCount, 1, 18);
        StartMilliseconds = Math.Max(
            0,
            TimingMap.BeatToMilliseconds(
                BeatPosition.FromDouble(double.IsFinite(startBeat) ? Math.Max(0, startBeat) : 0)));
        notes = Document.Notes
            .Where(note => !note.IsDraft)
            .Select(note => new PlaytestRuntimeNote(
                note,
                TimingMap.BeatToMilliseconds(note.Beat),
                TimingMap.BeatToMilliseconds(note.EndBeat ?? note.Beat),
                ScrollMap.PositionAt(note.Beat),
                ScrollMap.PositionAt(note.EndBeat ?? note.Beat)))
            .OrderBy(note => note.StartMilliseconds)
            .ThenBy(note => note.Note.Lane)
            .ToList();
        laneQueues = Enumerable.Range(0, KeyCount)
            .Select(lane => notes.Where(note => note.Note.Lane == lane).ToList())
            .ToArray();
        laneIndexes = new int[KeyCount];
        activeHolds = new PlaytestRuntimeNote?[KeyCount];
        autoReleaseMilliseconds = new double[KeyCount];
        AutoPlay = autoPlay;
        JudgementWindows = judgementWindows ?? PlaytestJudgementWindows.Default;
        StableRules = new StableManiaRules(Document.Metadata.OverallDifficulty, playbackRate);
        StableScrollMap = new ManiaScrollMap(Document, TimingMap, ScrollMap);
        stableHoldsByEnd = notes.Where(n => n.Note.IsHold).OrderBy(n => n.EndMilliseconds).ToArray();
        SetRuleset(ruleset);
    }

    public ChartDocument Document { get; }
    public TimingMap TimingMap { get; }
    public ScrollSpeedMap ScrollMap { get; }
    public IReadOnlyList<PlaytestRuntimeNote> Notes => notes;
    public int KeyCount { get; }
    public double StartMilliseconds { get; }
    public bool AutoPlay { get; private set; }
    public PlaytestJudgementWindows JudgementWindows { get; private set; }
    public int Combo => combo;
    public int MaximumCombo => maximumCombo;
    public int Judged => judged;
    public double Accuracy => judged == 0 ? 1 : earnedAccuracy / judged;
    public bool IsComplete => completed == notes.Count;

    public void Prepare()
    {
        globalMissIndex = 0;
        autoPlayIndex = 0;
        stableTailIndex = 0;
        Array.Clear(laneIndexes);
        Array.Clear(activeHolds);
        Array.Clear(autoReleaseMilliseconds);
        combo = 0;
        maximumCombo = 0;
        judged = 0;
        completed = 0;
        earnedAccuracy = 0;
        stableScore = 0; stableBonus = 100;
        foreach (var note in notes)
        {
            note.HeadJudged = false;
            note.Holding = false;
            note.Completed = false;
            note.Grade = PlaytestHitGrade.None;
            note.HeadError = double.PositiveInfinity;
            note.HoldBroken = false;
            var skipBefore = AutoPlay
                ? StartMilliseconds - .5
                : StartMilliseconds - (UsesStableRules ? StableRules.Ok : JudgementWindows.MissMilliseconds);
            if (note.StartMilliseconds >= skipBefore) continue;
            if (UsesStableRules && note.Note.IsHold && note.EndMilliseconds >= StartMilliseconds)
            {
                note.HeadJudged = true; note.HoldBroken = true;
                activeHolds[note.Note.Lane] = note;
                continue;
            }
            note.HeadJudged = true;
            note.Completed = true;
            completed++;
        }
        while (globalMissIndex < notes.Count && notes[globalMissIndex].Completed) globalMissIndex++;
        scoringNoteCount = notes.Count - completed;
        while (autoPlayIndex < notes.Count && notes[autoPlayIndex].Completed) autoPlayIndex++;
        for (var lane = 0; lane < laneQueues.Length; lane++)
        {
            while (laneIndexes[lane] < laneQueues[lane].Count &&
                   laneQueues[lane][laneIndexes[lane]].Completed)
                laneIndexes[lane]++;
        }
    }

    public void SetAutoPlay(bool value, double currentMilliseconds)
    {
        if (AutoPlay == value) return;
        AutoPlay = value;
        Array.Clear(autoReleaseMilliseconds);
        if (!AutoPlay) return;
        while (autoPlayIndex < notes.Count &&
               (notes[autoPlayIndex].Completed ||
                notes[autoPlayIndex].StartMilliseconds < currentMilliseconds - .5))
            autoPlayIndex++;
    }

    public void SetJudgementWindows(PlaytestJudgementWindows value) =>
        JudgementWindows = value;

    public IReadOnlyList<PlaytestJudgementEvent> PressLane(int lane, double currentMilliseconds)
    {
        if (AutoPlay || lane < 0 || lane >= laneQueues.Length || !double.IsFinite(currentMilliseconds))
            return EmptyEvents;
        if (UsesStableRules) return PressStable(lane, currentMilliseconds);
        var events = new List<PlaytestJudgementEvent>();
        var queue = laneQueues[lane];
        var index = laneIndexes[lane];
        while (index < queue.Count && queue[index].HeadJudged) index++;
        while (index < queue.Count &&
               currentMilliseconds - queue[index].StartMilliseconds >
               JudgementWindows.MissMilliseconds)
        {
            var stale = queue[index++];
            stale.HeadJudged = true;
            stale.Grade = PlaytestHitGrade.Miss;
            Complete(stale);
            events.Add(Event(stale, currentMilliseconds - stale.StartMilliseconds, true, false));
        }
        laneIndexes[lane] = index;
        if (index >= queue.Count) return events;
        var candidate = queue[index];
        var offset = currentMilliseconds - candidate.StartMilliseconds;
        if (offset < -JudgementWindows.MissMilliseconds ||
            offset > JudgementWindows.MissMilliseconds) return events;
        candidate.HeadJudged = true;
        candidate.Grade = Grade(offset, JudgementWindows);
        laneIndexes[lane] = index + 1;
        if (candidate.Note.IsHold)
        {
            candidate.Holding = true;
            activeHolds[lane] = candidate;
            events.Add(Event(candidate, offset, true, true));
        }
        else
        {
            Complete(candidate);
            events.Add(Event(candidate, offset, true, true));
        }
        return events;
    }

    public IReadOnlyList<PlaytestJudgementEvent> ReleaseLane(int lane, double currentMilliseconds)
    {
        if (AutoPlay || lane < 0 || lane >= activeHolds.Length || !double.IsFinite(currentMilliseconds))
            return EmptyEvents;
        if (UsesStableRules) return ReleaseStable(lane, currentMilliseconds);
        var hold = activeHolds[lane];
        if (hold == null) return EmptyEvents;
        hold.Holding = false;
        var offset = currentMilliseconds - hold.EndMilliseconds;
        hold.Grade = currentMilliseconds <
                     hold.EndMilliseconds - JudgementWindows.MissMilliseconds
            ? PlaytestHitGrade.Miss
            : Worse(hold.Grade, Grade(offset, JudgementWindows));
        Complete(hold);
        return [Event(hold, offset, false, false)];
    }

    public IReadOnlyList<PlaytestJudgementEvent> Advance(double currentMilliseconds)
    {
        if (!double.IsFinite(currentMilliseconds)) return EmptyEvents;
        if (UsesStableRules) return AdvanceStable(currentMilliseconds);
        List<PlaytestJudgementEvent>? events = null;
        if (AutoPlay) RunAutoPlay(currentMilliseconds, ref events);
        while (globalMissIndex < notes.Count &&
               currentMilliseconds - notes[globalMissIndex].StartMilliseconds >
               JudgementWindows.MissMilliseconds)
        {
            var note = notes[globalMissIndex++];
            if (note.HeadJudged) continue;
            note.HeadJudged = true;
            note.Grade = PlaytestHitGrade.Miss;
            Complete(note);
            (events ??= []).Add(
                Event(note, currentMilliseconds - note.StartMilliseconds, true, false));
        }
        for (var lane = 0; lane < activeHolds.Length; lane++)
        {
            var note = activeHolds[lane];
            if (note is { Holding: true } && currentMilliseconds >= note.EndMilliseconds)
            {
                note.Holding = false;
                Complete(note);
                (events ??= []).Add(
                    Event(note, currentMilliseconds - note.EndMilliseconds, false, false));
            }
            if (autoReleaseMilliseconds[lane] > 0 && currentMilliseconds >= autoReleaseMilliseconds[lane])
                autoReleaseMilliseconds[lane] = 0;
        }
        return events ?? EmptyEvents;
    }

    public bool IsAutoLanePressed(int lane) =>
        lane >= 0 && lane < autoReleaseMilliseconds.Length && autoReleaseMilliseconds[lane] > 0;

    public static PlaytestHitGrade Grade(double offsetMilliseconds) =>
        Grade(offsetMilliseconds, PlaytestJudgementWindows.Default);

    public static PlaytestHitGrade Grade(
        double offsetMilliseconds,
        PlaytestJudgementWindows windows) => Math.Abs(offsetMilliseconds) switch
    {
        var offset when offset <= windows.BestMilliseconds => PlaytestHitGrade.Best,
        var offset when offset <= windows.GreatMilliseconds => PlaytestHitGrade.Great,
        var offset when offset < windows.GoodMilliseconds => PlaytestHitGrade.Good,
        _ => PlaytestHitGrade.Miss
    };

    public static string GradeText(PlaytestHitGrade grade) => grade switch
    {
        PlaytestHitGrade.Best => "BEST",
        PlaytestHitGrade.Great => "GREAT",
        PlaytestHitGrade.Good => "GOOD",
        PlaytestHitGrade.Miss => "MISS",
        _ => string.Empty
    };

    private void RunAutoPlay(
        double currentMilliseconds,
        ref List<PlaytestJudgementEvent>? events)
    {
        while (autoPlayIndex < notes.Count && notes[autoPlayIndex].StartMilliseconds <= currentMilliseconds)
        {
            var note = notes[autoPlayIndex++];
            if (note.HeadJudged || note.Completed) continue;
            note.HeadJudged = true;
            note.Grade = PlaytestHitGrade.Best;
            var lane = note.Note.Lane;
            if (lane >= 0 && lane < activeHolds.Length)
            {
                autoReleaseMilliseconds[lane] = note.Note.IsHold
                    ? note.EndMilliseconds
                    : Math.Max(autoReleaseMilliseconds[lane], currentMilliseconds + 65);
                if (note.Note.IsHold)
                {
                    note.Holding = true;
                    activeHolds[lane] = note;
                }
                else Complete(note);
            }
            else Complete(note);
            (events ??= []).Add(Event(note, 0, true, true));
        }
    }

    private void Complete(PlaytestRuntimeNote note)
    {
        if (note.Completed) return;
        var lane = note.Note.Lane;
        if (lane >= 0 && lane < activeHolds.Length && ReferenceEquals(activeHolds[lane], note))
            activeHolds[lane] = null;
        note.Completed = true;
        completed++;
        judged++;
        if (UsesStableRules) AddStableScore(note.Grade);
        earnedAccuracy += UsesStableRules ? StableManiaRules.AccuracyValue(note.Grade) : note.Grade switch
        {
            PlaytestHitGrade.Best => 1,
            PlaytestHitGrade.Great => .8,
            PlaytestHitGrade.Good => .5,
            _ => 0
        };
        if (note.Grade == PlaytestHitGrade.Miss) combo = 0;
        else
        {
            combo++;
            maximumCombo = Math.Max(maximumCombo, combo);
        }
    }

    private static PlaytestJudgementEvent Event(
        PlaytestRuntimeNote note,
        double offset,
        bool isHead,
        bool playSound) => new(
        note,
        note.Grade,
        offset,
        isHead,
        note.Completed,
        playSound);

    private static PlaytestHitGrade Worse(PlaytestHitGrade left, PlaytestHitGrade right) =>
        (PlaytestHitGrade)Math.Max((int)left, (int)right);
}

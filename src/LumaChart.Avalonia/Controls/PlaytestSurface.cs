using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using System.Diagnostics;
using System.Globalization;
using LumaChart.Application.Playtest;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Controls;

public sealed record PlaytestSurfaceDiagnostics(
    int RuntimeNoteCount,
    int PerNoteControlCount,
    int LastVisitedNotes,
    int MaximumVisitedNotes,
    bool VisibleAreaClipping,
    bool SingleCustomControl);

/// <summary>
/// One clipped custom-drawn playfield. Notes, holds and lane slots are drawing
/// primitives; no Note or lane is represented by an Avalonia Control.
/// </summary>
public sealed partial class PlaytestSurface : Control
{
    private static readonly IBrush BackgroundBrush = Brush("#101315");
    private static readonly IBrush LaneABrush = Brush("#171B1E");
    private static readonly IBrush LaneBBrush = Brush("#1B2023");
    private static readonly IBrush NoteFillBrush = Brush("#D7E0E2");
    private static readonly IBrush CenterNoteFillBrush = Brush("#F2C56A");
    private static readonly IBrush LaneLightBrush = Brush("#2035C8FE");
    private static readonly IBrush HoldHeadFillBrush = Brush("#35C8FE");
    private static readonly IBrush HoldFillBrush = Brush("#8C6BA3F9");
    private static readonly IBrush HoldActiveFillBrush = Brush("#C46BA3F9");
    private static readonly IBrush MissFillBrush = Brush("#665A6063");
    private static readonly IBrush SlotFillBrush = Brush("#242A2D");
    private static readonly IBrush SlotPressedFillBrush = Brush("#315A5E");
    private static readonly Pen SlotPen = new(Brush("#68757A"), 2.2);
    private static readonly Pen SlotPressedPen = new(Brush("#8CD5DA"), 2.6);
    private static readonly Pen NotePen = new(Brush("#EEF1F2"), 1.5);
    private static readonly Pen HoldHeadPen = new(Brush("#2E83B4"), 1.5);
    private static readonly Pen HoldBodyPen = new(Brush("#A06BA3F9"), 1.5);
    private static readonly Pen HoldActivePen = new(Brush("#E06BA3F9"), 1.8);

    private PlaytestSession? session;
    private IReadOnlyList<PlaytestRuntimeNote> notes = [];
    private HoldIntervalNode? holdIntervals;
    private bool[] pressed = [];
    private double currentMilliseconds;
    private double currentScrollPosition;
    private WindowsPlaytestNoteStyle style = WindowsPlaytestNoteStyle.Brick;
    private double noteWidth = .82;
    private double noteHeight = 28;
    private double playfieldWidth = 520;
    private double scrollSpeed = 760;
    private double baseBpm = WindowsPlaytestScrollVelocityPolicy.DefaultBaseBpm;
    private double judgementLinePosition = .84;
    private int lastVisitedNotes;
    private int maximumVisitedNotes;
    private readonly Queue<(double Error, long Tick)> hitErrors = new();
    private FormattedText? judgementText;
    private long lastJudgementTick;

    public void ReportJudgement(PlaytestHitGrade grade, double errorMilliseconds, int lane = -1)
    {
        if (session == null) return;
        lastJudgementTick = Stopwatch.GetTimestamp();
        lastGrade = grade;
        if (lane >= 0 && lane < laneHitTicks.Length) laneHitTicks[lane] = lastJudgementTick;
        if (double.IsFinite(errorMilliseconds) && grade != PlaytestHitGrade.Miss)
        {
            if (hitErrors.Count == 32) hitErrors.Dequeue();
            hitErrors.Enqueue((errorMilliseconds, lastJudgementTick));
        }
        judgementText = new FormattedText(session.JudgementText(grade), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 28,
            grade == PlaytestHitGrade.Miss ? Brush("#FF7373") : grade == PlaytestHitGrade.Best ? HoldHeadFillBrush : NoteFillBrush);
        InvalidateVisual();
    }

    public PlaytestSurface()
    {
        ClipToBounds = true;
        Focusable = false;
        AutomationProperties.SetAutomationId(this, "Playtest.Surface");
        AutomationProperties.SetName(this, "测试演奏自绘轨道");
    }

    public PlaytestSurfaceDiagnostics Diagnostics => new(
        notes.Count,
        0,
        lastVisitedNotes,
        maximumVisitedNotes,
        ClipToBounds,
        true);

    public void SetSession(PlaytestSession value)
    {
        session = value ?? throw new ArgumentNullException(nameof(value));
        notes = session.Notes
            .OrderBy(note => note.StartScrollPosition)
            .ThenBy(note => note.StartMilliseconds)
            .ToArray();
        var holds = notes.Where(note => note.Note.IsHold).ToArray();
        holdIntervals = BuildHoldIntervals(holds, 0, holds.Length);
        pressed = new bool[session.KeyCount];
        laneHitTicks = new long[session.KeyCount];
        hitErrors.Clear(); judgementText = null;
        baseBpm = WindowsPlaytestScrollVelocityPolicy.ResolveBaseBpm(session.Document);
        currentMilliseconds = session.StartMilliseconds;
        currentScrollPosition = session.ScrollPositionAtMilliseconds(currentMilliseconds);
        InvalidateVisual();
    }

    public void Configure(WindowsPlaytestSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        style = WindowsPlaytestNoteStyle.Brick;
        noteWidth = 1;
        noteHeight = 18;
        scrollSpeed = settings.ScrollSpeed;
        judgementLinePosition = 402d / 480;
        maniaSpeed = settings.ManiaSpeed;
        upsideDown = settings.UpsideDown;
        InvalidateVisual();
    }

    public void SetCurrentTime(double milliseconds)
    {
        if (session == null || !double.IsFinite(milliseconds)) return;
        if (Math.Abs(currentMilliseconds - milliseconds) < .01) return;
        currentMilliseconds = milliseconds;
        currentScrollPosition = session.ScrollPositionAtMilliseconds(milliseconds);
        InvalidateVisual();
    }

    public void SetLanePressed(int lane, bool value)
    {
        if (lane < 0 || lane >= pressed.Length || pressed[lane] == value) return;
        pressed[lane] = value;
        InvalidateVisual();
    }

    /// <summary>
    /// Runs the same visible-range query used by <see cref="Render"/> without
    /// allocating drawing primitives. This is the deterministic verification seam
    /// for the 100,000-Note single-control/culling contract.
    /// </summary>
    public PlaytestSurfaceDiagnostics EvaluateViewportForDiagnostics(double viewportHeight)
    {
        if (session == null || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
            return Diagnostics;
        var pixelsPerBeat = PixelsPerScrollUnit(viewportHeight);
        var judgementY = viewportHeight * (skin?.HitPosition / 480 ?? judgementLinePosition);
        var margin = CullingMargin(viewportHeight, pixelsPerBeat);
        var minimumStart = currentScrollPosition -
                           (viewportHeight - judgementY) / pixelsPerBeat - margin;
        var maximumStart = currentScrollPosition + judgementY / pixelsPerBeat + margin;
        var visited = 0;
        for (var index = LowerBoundStart(notes, minimumStart);
             index < notes.Count && notes[index].StartScrollPosition <= maximumStart;
             index++)
            visited++;
        CountSpanningHolds(holdIntervals, minimumStart, ref visited);
        lastVisitedNotes = visited;
        maximumVisitedNotes = Math.Max(maximumVisitedNotes, visited);
        return Diagnostics;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        noteHeight = 18 * size.Height / 480;
        playfieldWidth = (session?.KeyCount ?? 7) * 30 * size.Height / 480;
        using var clip = context.PushClip(new Rect(size));
        context.FillRectangle(BackgroundBrush, new Rect(size));
        DrawBackground(context, size);
        if (skin != null)
        {
            DrawSkinStage(context, size);
            if (!PreviewOnly) { DrawHitFeedback(context, size); DrawGameplayHud(context, size); }
            return;
        }
        var keyCount = session?.KeyCount ?? 4;
        var width = Math.Min(size.Width, playfieldWidth);
        var origin = (size.Width - width) / 2;
        var laneWidth = width / Math.Max(1, keyCount);
        for (var lane = 0; lane < keyCount; lane++)
        {
            context.FillRectangle(
                lane % 2 == 0 ? LaneABrush : LaneBBrush,
                new Rect(origin + lane * laneWidth, 0, laneWidth, size.Height));
        }
        var judgementY = size.Height * judgementLinePosition;
        using (context.PushTransform(upsideDown ? new Matrix(1, 0, 0, -1, 0, size.Height) : Matrix.Identity))
        {
            DrawSlots(context, keyCount, origin, laneWidth, judgementY);
            DrawNotes(context, size.Height, origin, laneWidth, judgementY);
        }
        if (!PreviewOnly) { DrawHitFeedback(context, size); DrawGameplayHud(context, size); }
    }

    private void DrawSlots(
        DrawingContext context,
        int keyCount,
        double origin,
        double laneWidth,
        double judgementY)
    {
        var slotWidth = laneWidth * noteWidth;
        for (var lane = 0; lane < keyCount; lane++)
        {
            var active = lane < pressed.Length && pressed[lane];
            if (active) context.FillRectangle(LaneLightBrush, new Rect(origin + lane * laneWidth, 0, laneWidth, judgementY));
            var fill = active ? SlotPressedFillBrush : SlotFillBrush;
            var pen = active ? SlotPressedPen : SlotPen;
            var center = new Point(origin + (lane + .5) * laneWidth, judgementY);
            if (style == WindowsPlaytestNoteStyle.Circle)
                context.DrawEllipse(fill, pen, center, slotWidth / 2, noteHeight / 2);
            else
            {
                var height = Math.Max(24, noteHeight * 1.35);
                context.DrawRectangle(
                    fill,
                    pen,
                    new Rect(center.X - slotWidth / 2, judgementY - height / 2, slotWidth, height),
                    6,
                    6);
            }
        }
    }

    private void DrawNotes(
        DrawingContext context,
        double height,
        double origin,
        double laneWidth,
        double judgementY)
    {
        if (session == null) return;
        var pixelsPerBeat = PixelsPerScrollUnit(height);
        var margin = CullingMargin(height, pixelsPerBeat);
        var minimumStart = currentScrollPosition - (height - judgementY) / pixelsPerBeat - margin;
        var maximumStart = currentScrollPosition + judgementY / pixelsPerBeat + margin;
        var visited = 0;
        for (var index = LowerBoundStart(notes, minimumStart);
             index < notes.Count && notes[index].StartScrollPosition <= maximumStart;
             index++)
        {
            var note = notes[index];
            visited++;
            if (note.Completed || note.EndScrollPosition < minimumStart) continue;
            DrawNote(context, note, origin, laneWidth, judgementY, pixelsPerBeat);
        }
        DrawSpanningHolds(
            context,
            holdIntervals,
            minimumStart,
            origin,
            laneWidth,
            judgementY,
            pixelsPerBeat,
            ref visited);
        lastVisitedNotes = visited;
        maximumVisitedNotes = Math.Max(maximumVisitedNotes, visited);
    }

    private void DrawNote(
        DrawingContext context,
        PlaytestRuntimeNote runtime,
        double origin,
        double laneWidth,
        double judgementY,
        double pixelsPerBeat)
    {
        if (runtime.Note.Lane < 0 || session == null || runtime.Note.Lane >= session.KeyCount) return;
        if (skin != null) { DrawSkinNote(context, runtime, judgementY, pixelsPerBeat); return; }
        var width = laneWidth * noteWidth;
        var x = origin + runtime.Note.Lane * laneWidth + (laneWidth - width) / 2;
        var naturalHeadY = judgementY -
                           (runtime.StartScrollPosition - currentScrollPosition) * pixelsPerBeat;
        var headY = runtime.Note.IsHold && runtime.HeadJudged && runtime.Holding && currentMilliseconds >= runtime.StartMilliseconds
            ? judgementY : naturalHeadY;
        var tailY = judgementY -
                    (runtime.EndScrollPosition - currentScrollPosition) * pixelsPerBeat;
        var fill = runtime.Grade == PlaytestHitGrade.Miss
            ? MissFillBrush
            : session.UsesStableRules ? OsuManiaSkin.DefaultKind(session.KeyCount, runtime.Note.Lane) == "S"
                ? CenterNoteFillBrush : OsuManiaSkin.DefaultKind(session.KeyCount, runtime.Note.Lane) == "2" ? HoldHeadFillBrush : NoteFillBrush
            : runtime.Note.IsHold ? HoldHeadFillBrush : NoteFillBrush;
        if (!runtime.Note.IsHold)
        {
            DrawHead(context, NoteRectangle(x, headY, width), fill, NotePen);
            return;
        }

        var top = Math.Min(headY, tailY);
        var bottom = Math.Max(headY, tailY);
        var bodyWidth = Math.Max(
            6,
            width * (style == WindowsPlaytestNoteStyle.Circle ? .58 : .72));
        var body = new Rect(x + (width - bodyWidth) / 2, top, bodyWidth, Math.Max(1, bottom - top));
        var radius = style == WindowsPlaytestNoteStyle.Circle ? bodyWidth / 2 : 3;
        var bodyFill = runtime.Grade == PlaytestHitGrade.Miss
            ? MissFillBrush
            : runtime.Holding ? HoldActiveFillBrush : HoldFillBrush;
        context.DrawRectangle(
            bodyFill,
            runtime.Holding ? HoldActivePen : HoldBodyPen,
            body,
            radius,
            radius);
        if (!runtime.HeadJudged || runtime.Holding)
            DrawHead(
                context,
                NoteRectangle(x, headY, width),
                fill,
                HoldHeadPen);
        if (session.UsesStableRules)
            DrawHead(context, NoteRectangle(x, tailY, width), fill, HoldHeadPen);
    }

    private double PixelsPerScrollUnit(double height) => session?.UsesStableRules == true ? WindowsPlaytestScrollVelocityPolicy.LazerPixelsPerSecond(maniaSpeed, height) / session.StableRules.PlaybackRate
        : WindowsPlaytestScrollVelocityPolicy.PixelsPerScrollBeat(scrollSpeed, baseBpm);
    private Rect NoteRectangle(double x, double y, double width) =>
        new(x, y - (session?.UsesStableRules == true ? noteHeight : noteHeight / 2), width, noteHeight);

    private void DrawHitFeedback(DrawingContext context, Size size)
    {
        if (session?.UsesStableRules != true) return;
        var elapsed = Stopwatch.GetElapsedTime(lastJudgementTick).TotalSeconds;
        var image = skin?.Texture(lastGrade switch { PlaytestHitGrade.Best => "Hit300g", PlaytestHitGrade.Great => "Hit300", PlaytestHitGrade.Good => "Hit200", PlaytestHitGrade.Ok => "Hit100", PlaytestHitGrade.Meh => "Hit50", _ => "Hit0" },
            lastGrade switch { PlaytestHitGrade.Best => "mania-hit300g", PlaytestHitGrade.Great => "mania-hit300", PlaytestHitGrade.Good => "mania-hit200", PlaytestHitGrade.Ok => "mania-hit100", PlaytestHitGrade.Meh => "mania-hit50", _ => "mania-hit0" });
        if (image != null && elapsed < .6 && judgementText != null)
        {
            var scale = size.Height / 480; var layout = SkinLayout(size);
            using (context.PushOpacity(Math.Min(1, (.6 - elapsed) * 4)))
                DrawSprite(context, image, new Rect(layout.Center - image.LogicalWidth * scale / 2, size.Height * (skin!.ScorePosition / 480) - image.LogicalHeight * scale / 2, image.LogicalWidth * scale, image.LogicalHeight * scale), elapsed);
        }
        else if (judgementText != null && elapsed < .6)
            using (context.PushOpacity(Math.Min(1, (.6 - elapsed) * 4)))
                context.DrawText(judgementText, new Point((skin == null ? size.Width / 2 : SkinLayout(size).Center) - judgementText.Width / 2,
                    size.Height * (skin?.ScorePosition / 480 ?? .36) - judgementText.Height / 2));
        var center = size.Width / 2;
        var y = size.Height - 20;
        context.FillRectangle(SlotFillBrush, new Rect(center - 120, y, 240, 4));
        var perfect = session.StableRules.Best / session.StableRules.PlaybackRate;
        context.FillRectangle(HoldHeadFillBrush, new Rect(center - perfect, y, perfect * 2, 4));
        context.DrawLine(NotePen, new Point(center, y - 4), new Point(center, y + 8));
        foreach (var (error, tick) in hitErrors)
        {
            var age = Stopwatch.GetElapsedTime(tick).TotalSeconds;
            if (age >= 5) continue;
            using (context.PushOpacity(1 - age / 5))
                context.DrawLine(NotePen, new Point(center + Math.Clamp(error, -120, 120), y - 4), new Point(center + Math.Clamp(error, -120, 120), y + 8));
        }
    }

    private void DrawHead(DrawingContext context, Rect rect, IBrush fill, Pen pen)
    {
        if (style == WindowsPlaytestNoteStyle.Circle)
            context.DrawEllipse(
                fill,
                pen,
                new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2),
                rect.Width / 2,
                rect.Height / 2);
        else
            context.DrawRectangle(fill, pen, rect, 4, 4);
    }

    private static int LowerBoundStart(
        IReadOnlyList<PlaytestRuntimeNote> source,
        double scrollPosition)
    {
        var low = 0;
        var high = source.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (source[middle].StartScrollPosition < scrollPosition) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void DrawSpanningHolds(
        DrawingContext context,
        HoldIntervalNode? node,
        double boundary,
        double origin,
        double laneWidth,
        double judgementY,
        double pixelsPerBeat,
        ref int visited)
    {
        if (node == null || node.MaximumEndScrollPosition < boundary) return;
        DrawSpanningHolds(
            context,
            node.Left,
            boundary,
            origin,
            laneWidth,
            judgementY,
            pixelsPerBeat,
            ref visited);
        var note = node.Note;
        visited++;
        if (note.StartScrollPosition < boundary &&
            note.EndScrollPosition >= boundary &&
            !note.Completed)
            DrawNote(context, note, origin, laneWidth, judgementY, pixelsPerBeat);
        if (note.StartScrollPosition < boundary)
            DrawSpanningHolds(
                context,
                node.Right,
                boundary,
                origin,
                laneWidth,
                judgementY,
                pixelsPerBeat,
                ref visited);
    }

    private static HoldIntervalNode? BuildHoldIntervals(
        IReadOnlyList<PlaytestRuntimeNote> source,
        int start,
        int end)
    {
        if (start >= end) return null;
        var middle = start + (end - start) / 2;
        return new HoldIntervalNode(
            source[middle],
            BuildHoldIntervals(source, start, middle),
            BuildHoldIntervals(source, middle + 1, end));
    }

    private static void CountSpanningHolds(
        HoldIntervalNode? node,
        double boundary,
        ref int visited)
    {
        if (node == null || node.MaximumEndScrollPosition < boundary) return;
        CountSpanningHolds(node.Left, boundary, ref visited);
        visited++;
        if (node.Note.StartScrollPosition < boundary)
            CountSpanningHolds(node.Right, boundary, ref visited);
    }

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));

    private sealed class HoldIntervalNode
    {
        public HoldIntervalNode(
            PlaytestRuntimeNote note,
            HoldIntervalNode? left,
            HoldIntervalNode? right)
        {
            Note = note;
            Left = left;
            Right = right;
            MaximumEndScrollPosition = Math.Max(
                note.EndScrollPosition,
                Math.Max(
                    left?.MaximumEndScrollPosition ?? double.NegativeInfinity,
                    right?.MaximumEndScrollPosition ?? double.NegativeInfinity));
        }

        public PlaytestRuntimeNote Note { get; }
        public HoldIntervalNode? Left { get; }
        public HoldIntervalNode? Right { get; }
        public double MaximumEndScrollPosition { get; }
    }
}

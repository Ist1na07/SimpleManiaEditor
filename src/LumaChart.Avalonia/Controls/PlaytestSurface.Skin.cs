using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LumaChart.Application.Playtest;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Controls;

public sealed partial class PlaytestSurface
{
    internal bool PreviewOnly { get; set; }
    private OsuManiaSkin? skin;
    private double maniaSpeed = 20;
    private bool upsideDown;
    private long[] laneHitTicks = [];
    private PlaytestHitGrade lastGrade;
    private Layout? currentSkinLayout;
    private long lastBurstTick;
    private int lastBurstCombo;
    private string hudScore = "0000000", hudAccuracy = "100.00%", hudCombo = "0";
    private FormattedText? scoreFormatted, accuracyFormatted, comboFormatted, helpFormatted;
    private double hudHeight;
    private Bitmap? backgroundImage;
    internal void SetBackground(Bitmap? bitmap) { backgroundImage = bitmap; InvalidateVisual(); }
    private void DrawBackground(DrawingContext context, Size size)
    {
        if (backgroundImage == null) return;
        var ratio = Math.Max(size.Width / backgroundImage.Size.Width, size.Height / backgroundImage.Size.Height);
        var width = backgroundImage.Size.Width * ratio; var height = backgroundImage.Size.Height * ratio;
        using var opacity = context.PushOpacity(.2);
        context.DrawImage(backgroundImage, new Rect(backgroundImage.Size), new Rect((size.Width - width) / 2, (size.Height - height) / 2, width, height));
    }

    private double CullingMargin(double height, double pixelsPerUnit)
    {
        var capHeight = skin == null ? 18 * height / 480 : Math.Max(18 * height / 480, skin.Notes.Concat(skin.Heads).Concat(skin.Tails)
            .Where(t => t != null).Select(t => t!.LogicalHeight / t.LogicalWidth * skin.HeightScaleWidth * height / 480).DefaultIfEmpty(noteHeight).Max());
        return session?.UsesStableRules == true ? capHeight / pixelsPerUnit + .02 : Math.Max(1, capHeight / pixelsPerUnit + .5);
    }

    internal void SetSkin(OsuManiaSkin? value) { skin = value; InvalidateVisual(); }
    internal string? SkinName => skin?.Name;
    internal double ScrollTravelMilliseconds => WindowsPlaytestScrollVelocityPolicy.ScrollTimeMilliseconds(maniaSpeed);

    internal int LaneAt(double x)
    {
        if (session == null) return -1;
        if (skin != null)
        { var layout = SkinLayout(Bounds.Size); for (var lane = 0; lane < layout.X.Length; lane++) if (x >= layout.X[lane] && x <= layout.X[lane] + skin.Widths[lane] * layout.Scale) return lane; return -1; }
        var width = Math.Min(Bounds.Width, session.KeyCount * 30 * Bounds.Height / 480); var offset = (Bounds.Width - width) / 2;
        return x < offset || x >= offset + width ? -1 : Math.Clamp((int)((x - offset) / (width / session.KeyCount)), 0, session.KeyCount - 1);
    }

    private Layout SkinLayout(Size size)
    {
        var count = session?.KeyCount ?? 7; var scale = size.Height / 480;
        var width = skin!.Widths.Sum() + skin.Spacing.Take(count - 1).Sum() + (skin.SplitStages ? skin.StageSeparation : 0);
        var x = new double[count];
        var start = (size.Width - 640 * scale) / 2 + skin.ColumnStart * scale;
        // Preserve the user's offset on wide displays, but keep extreme skins reachable on small windows.
        start = Math.Clamp(start, 0, Math.Max(0, size.Width - width * scale));
        var current = start;
        for (var lane = 0; lane < count; lane++)
        {
            if (skin.SplitStages && lane == count / 2) current += skin.StageSeparation * scale;
            x[lane] = current; current += (skin.Widths[lane] + skin.Spacing[lane]) * scale;
        }
        return new Layout(x, scale, start, width * scale, start + width * scale / 2);
    }

    private void DrawSkinStage(DrawingContext context, Size size)
    {
        if (skin == null || session == null) return;
        var layout = currentSkinLayout = SkinLayout(size); var scale = layout.Scale;
        var judgment = skin.HitPosition * scale; var up = upsideDown || skin.UpsideDown;
        using var flip = context.PushTransform(up ? new Matrix(1, 0, 0, -1, 0, size.Height) : Matrix.Identity);
        for (var lane = 0; lane < session.KeyCount; lane++)
        {
            var rect = new Rect(layout.X[lane], 0, skin.Widths[lane] * scale, size.Height);
            context.FillRectangle(skin.LaneColours[lane], rect);
            if (pressed[lane])
            {
                var light = skin.StageLights[lane];
                if (light != null)
                    DrawSprite(context, light, new Rect(rect.X, skin.Config.Number("LightPosition", 413) * scale - size.Height, rect.Width, size.Height), currentMilliseconds / 1000, skin.Config.Number("LightFramePerSecond", 60));
                else context.FillRectangle(LaneLightBrush, rect);
            }
            var lineWidth = skin.LineWidths[lane] * scale;
            if (lineWidth > 0) context.FillRectangle(skin.Colour("ColourColumnLine", Colors.White), new Rect(rect.X - lineWidth / 2, 0, lineWidth, size.Height));
        }
        var end = layout.X[^1] + skin.Widths[^1] * scale;
        context.FillRectangle(skin.Colour("ColourColumnLine", Colors.White), new Rect(end, 0, skin.LineWidths[^1] * scale, size.Height));
        DrawSkinBarLines(context, layout, judgment, size.Height);
        if (skin.KeysUnderNotes) DrawSkinKeys(context, layout, size.Height, up);
        DrawNotes(context, size.Height, 0, 1, judgment);
        if (!skin.KeysUnderNotes) DrawSkinKeys(context, layout, size.Height, up);
        var hint = skin.Texture("StageHint", "mania-stage-hint");
        if (hint != null) DrawSprite(context, hint, new Rect(layout.Start, judgment - hint.LogicalHeight * scale / 2, layout.Width, hint.LogicalHeight * scale), 0);
        if (skin.Config.Flag("JudgementLine", true)) context.FillRectangle(skin.Colour("ColourJudgementLine", Colors.White), new Rect(layout.Start, judgment, layout.Width, scale));
        var left = skin.Texture("StageLeft", "mania-stage-left"); var right = skin.Texture("StageRight", "mania-stage-right");
        if (left != null) DrawSprite(context, left, new Rect(layout.Start - left.LogicalWidth * scale, 0, left.LogicalWidth * scale, size.Height), 0);
        if (right != null) DrawSprite(context, right, new Rect(end, 0, right.LogicalWidth * scale, size.Height), 0);
        var bottom = skin.Texture("StageBottom", "mania-stage-bottom");
        if (bottom != null) DrawSprite(context, bottom, new Rect(layout.Center - bottom.LogicalWidth * scale / 2, size.Height - bottom.LogicalHeight * scale, bottom.LogicalWidth * scale, bottom.LogicalHeight * scale), currentMilliseconds / 1000, skin.AnimationFps);
        DrawSkinLights(context, layout, judgment);
        if (session.Notes.Count > 0 && currentMilliseconds < session.Notes[0].StartMilliseconds - 500)
        {
            var warning = skin.Texture("WarningArrow", "mania-warningarrow");
            if (warning != null)
                for (var lane = 0; lane < session.KeyCount; lane++)
                {
                    var width = skin.Widths[lane] * scale; var arrowHeight = warning.LogicalHeight / warning.LogicalWidth * width;
                    DrawSprite(context, warning, new Rect(layout.X[lane], judgment - arrowHeight - 30 * scale, width, arrowHeight), 0);
                }
        }
    }

    private void DrawSkinKeys(DrawingContext context, Layout layout, double height, bool up)
    {
        for (var lane = 0; lane < layout.X.Length; lane++)
        {
            var texture = pressed[lane] ? skin!.PressedKeys[lane] : skin!.Keys[lane]; var width = skin.Widths[lane] * layout.Scale;
            if (texture == null)
            { context.FillRectangle(pressed[lane] ? SlotPressedFillBrush : SlotFillBrush, new Rect(layout.X[lane], skin.HitPosition * layout.Scale, width, height - skin.HitPosition * layout.Scale)); continue; }
            var imageHeight = texture.LogicalHeight / texture.LogicalWidth * width;
            DrawSprite(context, texture, new Rect(layout.X[lane], height - imageHeight, width, imageHeight), 0,
                flipY: up && !skin.Flip("KeyFlipWhenUpsideDown", lane, pressed[lane] ? "D" : "", true));
        }
    }

    private void DrawSkinNote(DrawingContext context, PlaytestRuntimeNote note, double judgmentY, double pixelsPerUnit)
    {
        var lane = note.Note.Lane; var layout = currentSkinLayout!; var scale = layout.Scale;
        var width = skin!.Widths[lane] * scale; var x = layout.X[lane]; var up = upsideDown || skin.UpsideDown;
        var y = judgmentY - (note.StartScrollPosition - currentScrollPosition) * pixelsPerUnit;
        if (note.Note.IsHold && note.HeadJudged && note.Holding && currentMilliseconds >= note.StartMilliseconds) y = judgmentY;
        var tailY = judgmentY - (note.EndScrollPosition - currentScrollPosition) * pixelsPerUnit;
        var texture = note.Note.IsHold ? skin.Heads[lane] : skin.Notes[lane];
        var imageHeight = texture == null ? noteHeight : texture.LogicalHeight / texture.LogicalWidth * skin.HeightScaleWidth * scale;
        using var opacity = context.PushOpacity(note.Grade == PlaytestHitGrade.Miss ? .35 : 1);
        if (note.Note.IsHold)
        {
            var body = skin.Bodies[lane]; var top = Math.Min(y, tailY); var bottom = Math.Max(y, tailY);
            var bodyRect = new Rect(x, top, width, Math.Max(.1, bottom - top));
            if (body == null) context.FillRectangle(note.Holding ? HoldActiveFillBrush : HoldFillBrush, bodyRect);
            else
            {
                using var clip = context.PushClip(bodyRect);
                var styleValue = (int)skin.Config.Number("NoteBodyStyle" + lane, skin.Config.Number("NoteBodyStyle", 1));
                if (styleValue == 0) DrawSprite(context, body, bodyRect, note.Holding ? currentMilliseconds / 1000 : 0);
                else
                {
                    var tileHeight = Math.Max(1, body.LogicalHeight / body.LogicalWidth * width);
                    // Clip first. Long offscreen holds must not produce thousands of texture draws.
                    var visibleTop = Math.Max(0, top); var visibleBottom = Math.Min(Bounds.Height, bottom);
                    var anchor = styleValue == 3 ? bottom : top;
                    var tileStart = anchor + Math.Floor((visibleTop - anchor) / tileHeight) * tileHeight;
                    for (var tile = tileStart; tile < visibleBottom; tile += tileHeight)
                        DrawSprite(context, body, new Rect(x, tile, width, tileHeight), note.Holding ? currentMilliseconds / 1000 : 0,
                            flipY: up && !skin.Flip("NoteFlipWhenUpsideDown", lane, "L", true));
                }
            }
            var tail = skin.Tails[lane];
            if (tail != null)
            {
                var tailHeight = tail.LogicalHeight / tail.LogicalWidth * skin.HeightScaleWidth * scale;
                // Official LegacyHoldNoteTailPiece reverses the note direction:
                // a downscroll tail is top-anchored at its timestamp.
                DrawSprite(context, tail, new Rect(x, tailY, width, tailHeight), currentMilliseconds / 1000,
                    flipY: skin.Flip("NoteFlipWhenUpsideDown", lane, "T", true) ^ up);
            }
            else DrawHead(context, new Rect(x, tailY, width, imageHeight), NoteFillBrush, NotePen);
        }
        if (!note.HeadJudged || note.Holding || !note.Note.IsHold)
        {
            if (texture != null) DrawSprite(context, texture, new Rect(x, y - imageHeight, width, imageHeight), currentMilliseconds / 1000,
                flipY: up && !skin.Flip("NoteFlipWhenUpsideDown", lane, note.Note.IsHold ? "H" : "", true));
            else DrawHead(context, new Rect(x, y - imageHeight, width, imageHeight), OsuManiaSkin.DefaultKind(session!.KeyCount, lane) == "S" ? CenterNoteFillBrush : NoteFillBrush, NotePen);
        }
    }

    private void DrawSkinLights(DrawingContext context, Layout layout, double judgment)
    {
        for (var lane = 0; lane < layout.X.Length; lane++)
        {
            var elapsed = laneHitTicks[lane] == 0 ? 10 : Stopwatch.GetElapsedTime(laneHitTicks[lane]).TotalSeconds;
            var held = session!.IsLaneHolding(lane);
            var image = held ? skin!.Texture("LightingL", "lightingL") : skin!.Texture("LightingN", "lightingN");
            if (image == null || !held && elapsed > .25) continue;
            var widths = skin.Config.Numbers(held ? "LightingLWidth" : "LightingNWidth", layout.X.Length, 0);
            var width = (widths[lane] > 0 ? widths[lane] : skin.Widths[lane] * 2) * layout.Scale;
            var height = width * image.LogicalHeight / image.LogicalWidth;
            using var opacity = context.PushOpacity(held ? 1 : Math.Max(0, 1 - elapsed * 4));
            DrawSprite(context, image, new Rect(layout.X[lane] + skin.Widths[lane] * layout.Scale / 2 - width / 2, judgment - height / 2, width, height), held ? currentMilliseconds / 1000 : elapsed, blend: BitmapBlendingMode.Plus);
        }
    }

    private void DrawSkinBarLines(DrawingContext context, Layout layout, double judgment, double height)
    {
        // Grid queries follow musical time; position uses the same continuous BPM/SV map as notes.
        var beat = session!.TimingMap.MillisecondsToBeat(currentMilliseconds).Value;
        var span = Math.Clamp(WindowsPlaytestScrollVelocityPolicy.ScrollTimeMilliseconds(maniaSpeed) / 1000 * session.StableScrollMap.BaseBpm / 60 * 2 + 8, 8, 160);
        for (var b = Math.Max(0, Math.Floor((beat - span) / 4) * 4); b <= beat + span; b += 4)
        {
            var ms = session.TimingMap.BeatToMilliseconds(LumaChart.Core.Models.BeatPosition.FromDouble(b));
            var y = judgment - (session.ScrollPositionAtMilliseconds(ms) - currentScrollPosition) * PixelsPerScrollUnit(height);
            if (y < 0 || y > height) continue;
            context.FillRectangle(skin!.Colour("ColourBarline", Colors.White), new Rect(layout.Start, y, layout.Width, Math.Clamp(skin.Config.Number("BarlineHeight", 1.2), 0, 10) * layout.Scale));
        }
    }

    private void DrawGameplayHud(DrawingContext context, Size size)
    {
        if (session == null) return;
        if (hudHeight != size.Height) { hudHeight = size.Height; scoreFormatted = accuracyFormatted = comboFormatted = null; }
        var scale = size.Height / 480; var center = skin == null ? size.Width / 2 : SkinLayout(size).Center;
        var score = session.Score.ToString("D7", CultureInfo.InvariantCulture); var accuracy = session.Accuracy.ToString("P2", CultureInfo.InvariantCulture);
        var combo = session.Combo.ToString(CultureInfo.InvariantCulture);
        if (scoreFormatted == null || hudScore != score) { hudScore = score; scoreFormatted = Text(score, 26 * scale); }
        if (accuracyFormatted == null || hudAccuracy != accuracy) { hudAccuracy = accuracy; accuracyFormatted = Text(accuracy, 15 * scale); }
        if (comboFormatted == null || hudCombo != combo) { hudCombo = combo; comboFormatted = Text(combo, 28 * scale); }
        var scoreWidth = DrawSkinNumber(context, score, new Point(size.Width - 16 * scale, 10 * scale), scale, false, true);
        if (scoreWidth == 0) context.DrawText(scoreFormatted, new Point(size.Width - scoreFormatted.Width - 16 * scale, 10 * scale));
        if (DrawSkinNumber(context, accuracy.Replace(" ", ""), new Point(size.Width - 16 * scale, 44 * scale), scale * .65, false, true) == 0)
            context.DrawText(accuracyFormatted, new Point(size.Width - accuracyFormatted.Width - 16 * scale, 44 * scale));
        var comboY = (skin?.ComboPosition ?? 111) * scale;
        if (session.Combo > 0 && DrawSkinNumber(context, combo, new Point(center, comboY), scale, true, false) == 0)
            context.DrawText(comboFormatted, new Point(center - comboFormatted.Width / 2, comboY - comboFormatted.Height / 2));
        helpFormatted ??= Text("Esc 暂停 · F1 设置 · F3/F4 落速 · F11 全屏", 10);
        context.DrawText(helpFormatted, new Point(12, size.Height - 22));
        var progress = Math.Clamp((currentMilliseconds - session.StartMilliseconds) / Math.Max(1, session.EndMilliseconds - session.StartMilliseconds), 0, 1);
        context.FillRectangle(HoldHeadFillBrush, new Rect(size.Width - 140 * scale, 66 * scale, 124 * scale * progress, 2 * scale));
        if (session.Combo > 0 && session.Combo % 100 == 0 && session.Combo != lastBurstCombo) { lastBurstCombo = session.Combo; lastBurstTick = Stopwatch.GetTimestamp(); }
        var burstAge = Stopwatch.GetElapsedTime(lastBurstTick).TotalSeconds; var burst = skin?.Texture("comboburst-mania");
        if (burst != null && burstAge < .8)
            using (context.PushOpacity(1 - burstAge / .8)) DrawSprite(context, burst, new Rect(center + 100 * scale, size.Height - burst.LogicalHeight * scale, burst.LogicalWidth * scale, burst.LogicalHeight * scale), burstAge, skin!.AnimationFps);
    }

    private double DrawSkinNumber(DrawingContext context, string value, Point position, double scale, bool combo, bool right)
    {
        if (skin == null) return 0;
        var prefix = skin.Fonts.Text(combo ? "ComboPrefix" : "ScorePrefix", "score");
        var glyphs = value.Select(c => skin.Texture(prefix + "-" + (c == '.' ? "dot" : c == '%' ? "percent" : c.ToString()))).ToArray();
        if (glyphs.Any(g => g == null)) return 0;
        var overlap = skin.Fonts.Number(combo ? "ComboOverlap" : "ScoreOverlap", 0) * scale;
        var width = glyphs.Sum(g => g!.LogicalWidth * scale) - Math.Max(0, glyphs.Length - 1) * overlap;
        var x = position.X - (right ? width : width / 2);
        foreach (var glyph in glyphs) { var g = glyph!; DrawSprite(context, g, new Rect(x, position.Y - (combo ? g.LogicalHeight * scale / 2 : 0), g.LogicalWidth * scale, g.LogicalHeight * scale), 0); x += g.LogicalWidth * scale - overlap; }
        return width;
    }

    private static void DrawSprite(DrawingContext context, SkinAnimation texture, Rect rect, double seconds, double fps = 60, bool flipY = false, BitmapBlendingMode blend = BitmapBlendingMode.SourceOver)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var frame = texture.Frame(seconds, fps);
        using var blendScope = context.PushRenderOptions(new RenderOptions { BitmapBlendingMode = blend });
        using var flip = context.PushTransform(flipY ? new Matrix(1, 0, 0, -1, 0, rect.Top + rect.Bottom) : Matrix.Identity);
        context.DrawImage(frame, new Rect(frame.Size), rect);
    }
    private static FormattedText Text(string text, double size) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), size, NoteFillBrush);
    private sealed record Layout(double[] X, double Scale, double Start, double Width, double Center);
}

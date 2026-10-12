using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Controls;
using LumaChart.Core.Models;
using SkiaSharp;

internal static class DrawToolSpecs
{
    public static void Run()
    {
        var session = new EditorSession { SnapDivision = 4 };
        session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Tap };
        var window = new Window { Width = 1000, Height = 800, Content = surface };
        window.Show(); Dispatcher.UIThread.RunJobs();
        surface.SetCurrentBeat(4);
        var probe = surface.GetPlacementEvidenceProbe()!;
        var lane = probe.TapTarget.Lane;
        Point At(double beat) => new(probe.TapTarget.SurfacePoint.X,
            surface.Bounds.Height * surface.JudgementLinePosition - (beat - 4) * surface.PixelsPerBeat - 1);
        void Reset(params ChartNote[] notes) { session.ReplaceDocument(new ChartDocument { KeyCount = 7, Notes = notes.Select(n => n.Clone()).ToList() }); surface.SetCurrentBeat(4); }
        void Drag(Point from, Point to, MouseButton button)
        {
            window.MouseDown(from, button);
            window.MouseMove(to, button == MouseButton.Middle ? RawInputModifiers.MiddleMouseButton : RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, button);
        }
        Reset(); Drag(At(4), At(5), MouseButton.Left);
        Require(session.Document.Notes.Single().Beat.Value == 4 && !session.Document.Notes.Single().IsHold, "tap drag created LN");
        surface.Tool = TimelineTool.Hold;
        Reset(); Drag(At(4), At(4), MouseButton.Left);
        Require(!session.Document.Notes.Single().IsHold, "hold click created a unit LN");
        Reset(); Drag(At(5), At(4), MouseButton.Left);
        Require(session.Document.Notes.Single().Beat.Value == 4 && session.Document.Notes.Single().EndBeat!.Value.Value == 5, "backward hold drag failed");
        foreach (var tool in new[] { TimelineTool.Tap, TimelineTool.Hold })
        {
            surface.Tool = tool;
            var original = new ChartNote { Lane = lane, Beat = BeatPosition.FromDouble(4), EndBeat = BeatPosition.FromDouble(5), Sound = "original.wav" };
            Reset(original);
            Drag(At(5) - new Vector(0, surface.NoteHeight - 3), At(5.5), MouseButton.Left);
            Require(session.Document.Notes.Single().Id == original.Id && session.Document.Notes.Single().Beat.Value == 4 &&
                session.Document.Notes.Single().EndBeat!.Value.Value == 5.5, "visible tail cap moved the whole LN instead of resizing");
            Reset(original.Clone());
            Drag(At(4), At(4.5), MouseButton.Middle);
            Require(session.Document.Notes.Single().Id == original.Id && session.Document.Notes.Single().Beat.Value == 4.5 &&
                session.Document.Notes.Single().EndBeat!.Value.Value == 5.5 && surface.CurrentBeat == 4, "middle note drag failed or panned the viewport");
            Reset(original.Clone());
            Drag(At(4), At(4.5), MouseButton.Left);
            var replacement = session.Document.Notes.Single();
            Require(replacement.Id != original.Id && replacement.Beat.Value == 4 && replacement.IsHold == (tool == TimelineTool.Hold), "draw-tool click moved the original instead of replacing");
            Require(session.Undo() && session.Document.Notes.Single().Id == original.Id && session.Document.Notes.Single().Sound == "original.wav", "replacement was not one undo operation");
            Require(session.Redo() && session.Document.Notes.Single().Id == replacement.Id, "replacement redo failed");
            Reset(); Drag(At(4), At(5), MouseButton.Middle);
            Require(session.Document.Notes.Count == 0 && surface.CurrentBeat == 4, "middle drag on blank space in drawing tool panned or placed a note");
            Reset(original.Clone()); session.IsReadOnly = true;
            Drag(At(4), At(4.5), MouseButton.Left);
            Require(session.Document.Notes.Single().Id == original.Id, "read-only replacement mutated notes");
            session.IsReadOnly = false;
            Reset(original.Clone());
            session.SetOwnerProtection([original.Id], true); session.AllowOwnerProtectedEdits = false;
            Drag(At(4), At(4.5), MouseButton.Left);
            Require(session.Document.Notes.Single().Id == original.Id, "replacement changed an owner-locked note");
            session.AllowOwnerProtectedEdits = true;
            Reset(original.Clone());
            window.MouseDown(At(4), MouseButton.Left);
            surface.Tool = TimelineTool.Select;
            window.MouseUp(At(4.5), MouseButton.Left);
            Require(session.Document.Notes.Single().Id == original.Id, "canceling replacement lost original note");
            surface.Tool = tool;
            Reset(original.Clone());
            window.MouseDown(At(4), MouseButton.Left);
            session.Mutate(d => d.Notes.Add(new ChartNote { Lane = (lane + 1) % 7, Beat = BeatPosition.FromDouble(6) }));
            window.MouseUp(At(4.5), MouseButton.Left);
            Require(session.Document.Notes.Count == 2 && session.Document.Notes.Any(n => n.Id == original.Id), "stale replacement overwrote an intervening edit");
        }
        Reset(); surface.Tool = TimelineTool.Tap;
        window.MouseMove(new Point(2, 2)); Dispatcher.UIThread.RunJobs();
        using var before = Capture(window);
        var mouse = At(4) + new Vector(13, 17);
        window.MouseMove(mouse); Dispatcher.UIThread.RunJobs();
        using var after = Capture(window);
        var x = (int)mouse.X;
        Require(before.GetPixel(x, (int)mouse.Y - 5) != after.GetPixel(x, (int)mouse.Y - 5) &&
            before.GetPixel(x, (int)mouse.Y + 3) == after.GetPixel(x, (int)mouse.Y + 3), "hover note did not follow pointer with its bottom at pointer tip");
        Require(session.Document.Notes.Count == 0, "hover preview committed a note");
        window.MouseDown(mouse, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        using var pressedPreview = Capture(window);
        Require(pressedPreview.GetPixel(x + 22, (int)mouse.Y - 5) != before.GetPixel(x + 22, (int)mouse.Y - 5), "pointer preview disappeared while placing with button held");
        window.MouseUp(mouse, MouseButton.Left);
        surface.Tool = TimelineTool.Select;
        Drag(At(4), At(5), MouseButton.Middle);
        Require(surface.CurrentBeat != 4, "middle viewport pan disappeared for selection tool");
        window.Close();
        Console.WriteLine("PASS draw tools, atomic replacement/undo, middle movement, full LN tail cap and pointer-anchored preview");
    }

    private static SKBitmap Capture(Window window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var stream = new MemoryStream(); frame.Save(stream, PngBitmapEncoderOptions.Default); stream.Position = 0;
        return SKBitmap.Decode(stream);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using System.Reflection;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Rendering;
using LumaChart.Core.Models;

internal static class PlacementPrecisionSpecs
{
    public static void Run()
    {
        var session = new EditorSession { SnapDivision = 4 };
        var surface = new TimelineSurface { Session = session, Tool = TimelineTool.Tap };
        var window = new Window { Width = 1000, Height = 800, Content = surface };
        window.Show(); Dispatcher.UIThread.RunJobs();
        foreach (var height in new[] { 8d, 32d, 100d })
        foreach (var raw in new[] { 4.01, 4.1, 4.125, 4.24 })
        {
            session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
            surface.ConfigureView(84, 280, 77, height, 1, .79, 1.5, .85);
            surface.SetCurrentBeat(4); Dispatcher.UIThread.RunJobs();
            var probe = surface.GetPlacementEvidenceProbe()!;
            var point = new Point(probe.TapTarget.SurfacePoint.X, surface.Bounds.Height * surface.JudgementLinePosition - (raw - 4) * surface.PixelsPerBeat);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            var note = session.Document.Notes.Single();
            var expected = raw < 4.125 ? 4 : 4.25;
            if (note.Beat.Value != expected) throw new InvalidOperationException("placement did not choose the nearest grid independently of thickness");
            var geometry = typeof(TimelineSurface).GetMethod("GetGeometry", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(surface, [7]);
            var boundsMethod = typeof(TimelineSurface).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(m => m.Name == "GetNoteBounds" && m.GetParameters().Length == 2);
            var bounds = (Rect)boundsMethod.Invoke(surface, [note, geometry])!;
            var gridY = surface.Bounds.Height * surface.JudgementLinePosition - (expected - 4) * surface.PixelsPerBeat;
            var halfLine = (double)typeof(TimelineSurface).GetMethod("GridLineHalfThickness", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [note.Beat])!;
            if (Math.Abs(bounds.Bottom - (gridY - halfLine)) > .001) throw new InvalidOperationException("note bottom does not align to the grid upper edge");
            window.MouseDown(new Point(5, 100), MouseButton.Right); window.MouseUp(new Point(5, 100), MouseButton.Right);
            if (session.Selection.Count != 0 || session.Document.Notes.Count != 1) throw new InvalidOperationException("blank right-click failed to deselect without deleting");
        }
        session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
        session.SnapDivision = 16;
        surface.ConfigureView(84, 280, 77, 32, 1, .79, 1.5, .85);
        surface.SetCurrentBeat(4);
        var holdProbe = surface.GetPlacementEvidenceProbe()!;
        surface.Tool = TimelineTool.Hold;
        window.MouseDown(holdProbe.HoldStartTarget.SurfacePoint, MouseButton.Left);
        window.MouseMove(holdProbe.HoldEndTarget.SurfacePoint, RawInputModifiers.LeftMouseButton);
        window.MouseUp(holdProbe.HoldEndTarget.SurfacePoint, MouseButton.Left);
        var hold = session.Document.Notes.Single();
        var tail = surface.GetSelectedHoldTailEvidenceTarget()!;
        foreach (var delta in new[] { .04, -.04 })
        {
            var targetBeat = holdProbe.HoldEndTarget.Beat.Value + delta;
            var targetY = surface.Bounds.Height * surface.JudgementLinePosition - (targetBeat - 4) * surface.PixelsPerBeat;
            window.MouseDown(tail.SurfacePoint, MouseButton.Left);
            window.MouseMove(new Point(tail.SurfacePoint.X, targetY), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(tail.SurfacePoint.X, targetY), MouseButton.Left);
            if (session.Document.Notes.Single().EndBeat != TimelinePlacementGeometry.NearestGrid(targetBeat, 16))
                throw new InvalidOperationException("LN tail did not snap to nearest mouse grid in both directions");
            tail = surface.GetSelectedHoldTailEvidenceTarget()!;
        }
        if (!TimelineHoldTailResize.IsHit(new Point(50, 100), 20, 80, 200, 100, 32) ||
            TimelineHoldTailResize.IsHit(new Point(50, 107), 20, 80, 200, 100, 32) ||
            TimelineHoldTailResize.IsHit(new Point(19, 100), 20, 80, 200, 100, 32))
            throw new InvalidOperationException("LN tail resize handle still extended outside its precise area");
        window.Close();
        Console.WriteLine("PASS nearest-grid placement/LN drag, bottom time anchor, blank deselection and precise LN-tail hit area");
    }
}

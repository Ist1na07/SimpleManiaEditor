using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
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
        foreach (var raw in new[] { 4.01, 4.1, 4.24 })
        {
            session.ReplaceDocument(new ChartDocument { KeyCount = 7 });
            surface.ConfigureView(84, 280, 77, height, 1, .79, 1.5, .85);
            surface.SetCurrentBeat(4); Dispatcher.UIThread.RunJobs();
            var probe = surface.GetPlacementEvidenceProbe()!;
            var point = new Point(probe.TapTarget.SurfacePoint.X, surface.Bounds.Height * surface.JudgementLinePosition - (raw - 4) * surface.PixelsPerBeat);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            if (session.Document.Notes.Single().Beat.Value != 4) throw new InvalidOperationException("placement depended on note thickness or rounded into the next grid");
        }
        if (!TimelineHoldTailResize.IsHit(new Point(50, 100), 20, 80, 200, 100, 32) ||
            TimelineHoldTailResize.IsHit(new Point(50, 107), 20, 80, 200, 100, 32) ||
            TimelineHoldTailResize.IsHit(new Point(19, 100), 20, 80, 200, 100, 32))
            throw new InvalidOperationException("LN tail resize handle still extended outside its precise area");
        window.Close();
        Console.WriteLine("PASS thickness-independent previous-grid placement and precise LN-tail hit area");
    }
}

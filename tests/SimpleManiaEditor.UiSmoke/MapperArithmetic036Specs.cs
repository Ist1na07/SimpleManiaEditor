using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using LumaChart.Application.Editing;
using LumaChart.Avalonia.Controls;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

internal static class MapperArithmetic036Specs
{
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void MusicalPatterns()
    {
        var odd=new BeatPosition(3,1,999983);var seventh=new BeatPosition(2,1,7);
        Require(BeatArithmetic.Offset(seventh,odd,odd)==seventh,"fraction cancellation rounded a recoverable seventh");
        foreach(var bpm in new[]{104.04,173.123,240d})
        {
            var expected=new[]{new BeatPosition(8,0,1),new BeatPosition(8,1,7),new BeatPosition(8,1,3),new BeatPosition(8,1,4),new BeatPosition(8,1,5),new BeatPosition(8,1,9),new BeatPosition(9,0,1)};
            var text="osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n475,"+(60000/bpm).ToString("R",System.Globalization.CultureInfo.InvariantCulture)+",4,1,0,0,1,0\n[HitObjects]\n";
            foreach(var (beat,i) in expected.Select((b,i)=>(b,i))) text+=$"{36+i*73},192,{Math.Floor(475+beat.Value*60000/bpm)},1,0,0:0:0:0:\n";
            var source=OsuManiaSerializer.Parse(text);var before=System.Text.Json.JsonSerializer.Serialize(source);
            var pattern=PatternLibrary.Capture("grid",7,source.Notes,sourceDocument:source);
            pattern=System.Text.Json.JsonSerializer.Deserialize<MapperPattern>(System.Text.Json.JsonSerializer.Serialize(pattern))!;
            var scaled=PatternLibrary.Materialize(pattern,2);var anchor=new BeatPosition(32,1,7);
            var placed=NotePastePlacement.Preview(scaled,anchor,0,7);
            for(var i=0;i<expected.Length;i++)
            {
                var relative=BeatArithmetic.Offset(BeatPosition.Zero,expected[placed[i].Lane],expected[0]);
                Require(placed[i].Beat==BeatArithmetic.Offset(anchor,BeatArithmetic.Scale(relative,2),BeatPosition.Zero),$"capture/save/2x/paste drift: BPM{bpm}, note{i}, got{placed[i].Beat}");
            }
            Require(before==System.Text.Json.JsonSerializer.Serialize(source),"musical extraction modified native source document");
        }
        var intentional=OsuManiaSerializer.Parse("osu file format v14\n[General]\nMode:3\n[Difficulty]\nCircleSize:7\n[TimingPoints]\n0,500,4,1,0,0,1,0\n[HitObjects]\n36,192,1500,1,0,0:0:0:0:\n109,192,1558,1,0,0:0:0:0:\n");
        var pattern2=PatternLibrary.Capture("offgrid",7,intentional.Notes,sourceDocument:intentional);
        Require(Math.Abs(PatternLibrary.Materialize(pattern2)[1].Beat.Value-.116)<1e-6,"intentional off-grid offset was over-calibrated");
        Console.WriteLine("PASS 036 exact cancellation, native integer-ms musical extraction across BPMs/sevenths/thirds/fifths/ninths, JSON/2x/anchor paste, intentional offset/source preservation");
    }
    public static void MarqueeOutsideOnly()
    {
        var session=new EditorSession();session.ReplaceDocument(new ChartDocument{KeyCount=7});
        var surface=new TimelineSurface{Session=session,Tool=TimelineTool.Select};var window=new Window{Width=1000,Height=800,Content=surface};window.Show();Program.Pump(20);surface.SetCurrentBeat(8);
        var p=surface.GetPlacementEvidenceProbe()!.TapTarget.SurfacePoint.WithY(surface.Bounds.Height-5);
        window.MouseDown(p,MouseButton.Left);Program.Pump(230);window.MouseMove(p-new Vector(0,4),RawInputModifiers.LeftMouseButton);Program.Pump(120);
        Require(surface.CurrentBeat==8,"marquee inside bottom band changed paused time");
        window.MouseMove(p.WithY(surface.Bounds.Height+20),RawInputModifiers.LeftMouseButton);Program.Pump(160);
        Require(surface.CurrentBeat<8,"marquee dragged below viewport failed to scroll");window.MouseUp(p.WithY(surface.Bounds.Height+20),MouseButton.Left);window.Close();Program.Pump(20);
        Console.WriteLine("PASS 036 marquee inside-edge remains stationary and scrolls only beyond viewport");
    }
}

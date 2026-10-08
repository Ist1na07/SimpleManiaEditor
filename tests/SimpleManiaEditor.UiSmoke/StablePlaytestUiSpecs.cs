using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LumaChart.Application.Playtest;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

internal static class StablePlaytestUiSpecs
{
    public static void Run(string screenshots)
    {
        var source = new ChartDocument { KeyCount=7, Notes=[new ChartNote {Lane=3,Beat=new(2,0,1),EndBeat=new(3,0,1)}] };
        var settings = new WindowsPlaytestSettings(autoPlay:false, inputOffsetMilliseconds:50, fullScreen:false);
        var window = new PlaytestWindow(source,null,Path.Combine(Path.GetTempPath(),"stable-preview.mc"),0,settings,
            new WindowsKeySoundSettings(),1,true,.8f,60,1,1,false);
        window.Show(); Program.Pump(100);
        window.KeyPress(Key.F1, RawInputModifiers.None, PhysicalKey.F1, "F1");
        window.KeyRelease(Key.F1, RawInputModifiers.None, PhysicalKey.F1, "F1"); Program.Pump(20);
        window.GetVisualDescendants().OfType<TabControl>().Single(c => AutomationProperties.GetAutomationId(c) == "Playtest.SettingsTabs").SelectedIndex = 1;
        Program.Pump(20);
        var session = Field<PlaytestSession>(window,"session");
        Require(session.UsesStableRules && settings.Ruleset == PlaytestRuleset.OsuStable,"default profile did not reach gameplay");
        var profile = window.GetVisualDescendants().OfType<ComboBox>().Single(c=>AutomationProperties.GetAutomationId(c)=="Playtest.Ruleset");
        Require(profile.SelectedIndex==0 && !Field<Slider>(window,"bestWindowSlider").IsEnabled,"stable UI allowed legacy windows to override OD");
        void Time(double ms) { Set(window,"silentAnchorMilliseconds",ms); Set(window,"playing",true); }
        Time(1050);
        var pause = Field<Button>(window,"pauseButton"); pause.Focus();
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Require(session.Notes[0].HeadJudged && session.Notes[0].Grade==PlaytestHitGrade.Best &&
            Field<HashSet<Key>>(window,"keysDown").Contains(Key.Space),"7K center Space was routed to pause/focused button instead of gameplay");
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Require(!session.Notes[0].Completed && session.Judged==0,"key repeat retriggered LN");
        Time(1550); window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Require(session.Judged==1 && session.Accuracy==1,"release did not use same offset clock as head");
        session.Prepare();
        Time(1130); Invoke(window,"RunFrame");
        Require(!session.Notes[0].HeadJudged,"frame expired notes on a different clock than offset input");
        session.Prepare();
        Field<PlaytestSurface>(window,"playfield").SetSession(session);
        Time(1050); window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        var surface=Field<PlaytestSurface>(window,"playfield"); surface.SetCurrentTime(1100);
        Program.Pump(30);
        using(var frame=window.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots,"playtest-stable.png"),PngBitmapEncoderOptions.Default);
        Time(1550); window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        profile.SelectedIndex=1; Program.Pump(30);
        Require(!session.UsesStableRules && Field<Slider>(window,"bestWindowSlider").IsEnabled,"legacy fallback profile did not reset gameplay/enable its windows");
        profile.SelectedIndex=0; Program.Pump(30);
        WindowsPlaytestSettingsStore.Save(settings);
        Require(WindowsPlaytestSettingsStore.Load().Ruleset==PlaytestRuleset.OsuStable,"ruleset preference did not persist");
        window.Close(); Program.Pump(40);
        var large = new PlaytestSession(new ChartDocument {KeyCount=7,Notes=Enumerable.Range(0,100_000)
            .Select(i=>new ChartNote {Lane=i%7,Beat=BeatPosition.FromDouble(i*.02)}).ToList()},0,false,ruleset:PlaytestRuleset.OsuStable);
        var largeSurface=new PlaytestSurface(); largeSurface.SetSession(large); largeSurface.Configure(settings); largeSurface.SetCurrentTime(50_000);
        var culling=largeSurface.EvaluateViewportForDiagnostics(720);
        Require(culling.SingleCustomControl && culling.PerNoteControlCount==0 && culling.LastVisitedNotes<300,
            "stable scrolling traversed the whole 100k-note chart instead of culling visible notes");
        Console.WriteLine("PASS stable playtest UI, OD profile, center Space/repeat, release and frame offset clock, fallback and persistence");
    }
    private static T Field<T>(object source,string name)=>(T)source.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(source)!;
    private static void Set(object source,string name,object value)=>source.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(source,value);
    private static void Invoke(object source,string name)=>source.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(source,null);
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}

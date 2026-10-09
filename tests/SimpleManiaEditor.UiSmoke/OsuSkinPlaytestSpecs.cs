using System.IO.Compression;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LumaChart.Application.Playtest;
using LumaChart.Avalonia.Controls;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;
using SkiaSharp;
using NAudio.Wave;

internal static class OsuSkinPlaytestSpecs
{
    public static void Run(string root, string screenshots)
    {
        var osuRoot = Path.Combine(root, "osu fixture"); var skinRoot = Path.Combine(osuRoot, "Skins", "Mapper skin");
        Directory.CreateDirectory(Path.Combine(skinRoot, "notes"));
        var ini = """
            [General]
            Name: Mapper fixture
            Version: 2.7
            [Fonts]
            ScorePrefix: numbers
            ComboPrefix: numbers
            [Mania]
            ColumnStart: 210
            Keys: 4
            ColumnWidth: 25,25,25,25
            [Mania]
            Keys: 7
            ColumnStart: 136
            ColumnWidth: 30,35,30,45,30,35,30
            ColumnSpacing: 1,2,1,2,1,2
            ColumnLineWidth: 0,0,0,0,0,0,0,0
            HitPosition: 420
            ScorePosition: 220
            ComboPosition: 120
            NoteImage3: notes/gold
            NoteImage3H: notes/gold
            NoteImage3T: notes/gold
            Key0: A
            Key1: S
            Key2: D
            Key3: Space
            Key4: J
            Key5: K
            Key6: L
            Colour1: 10,20,30,255
            NoteBodyStyle: 1
            """;
        File.WriteAllText(Path.Combine(skinRoot, "skin.ini"), ini);
        Image("notes/gold@2x.png", SKColors.Gold, 100, 20);
        Image("mania-note1.png", SKColors.White, 50, 10);
        Image("mania-note2-0.png", SKColors.DeepSkyBlue, 50, 10);
        Image("mania-note2-1.png", SKColors.Cyan, 50, 10);
        Image("mania-note1L.png", SKColors.Blue, 50, 8);
        Image("mania-key1.png", SKColors.Gray, 50, 90);
        Image("mania-key1D.png", SKColors.Lime, 50, 90);
        Image("mania-hit300g.png", SKColors.Lime, 100, 30);
        Image("mania-stage-light.png", SKColors.White, 50, 480);
        Image("lightingN.png", SKColors.White, 50, 20);
        Image("pause-continue.png", SKColors.DeepSkyBlue, 200, 40);
        using (var wave = new WaveFileWriter(Path.Combine(skinRoot,"normal-hitnormal.wav"),WaveFormat.CreateIeeeFloatWaveFormat(44100,2)))
        { var samples = Enumerable.Range(0,882).Select(i => (float)(Math.Sin(i * Math.PI / 20) * .1)).ToArray(); wave.WriteSamples(samples,0,samples.Length); }
        for (var d = 0; d < 10; d++) Image("numbers-" + d + ".png", SKColors.White, 12, 22);
        Image("numbers-dot.png", SKColors.White, 5, 22); Image("numbers-percent.png", SKColors.White, 16, 22);
        void Image(string name, SKColor colour, int width, int height)
        { using var bitmap = new SKBitmap(width, height); bitmap.Erase(colour); using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var file = File.Create(Path.Combine(skinRoot, name)); data.SaveTo(file); }

        var cfg = Path.Combine(osuRoot, "osu!.fixture.cfg");
        File.WriteAllText(cfg, "ManiaLayouts7K = A S D Space J K L\nManiaLayouts4K = D F J K\nManiaLayouts5K = D F J K BADKEY\nManiaSpeed = 26\nSkin = Mapper skin\nkeyIncreaseSpeed = F6\nkeyDecreaseSpeed = F7\nkeyPause = Escape\nUsername = do-not-copy\nPassword = do-not-copy\n");
        var imported = OsuPlaytestImport.Read(cfg, WindowsPlaytestSettings.Default.With(fullScreen:false));
        Require(imported.Settings.BindingsFor(7)[0] == "A" && imported.Settings.ManiaSpeed == 26 && imported.Settings.SkinPath == skinRoot, "cfg bindings/speed/skin failed");
        Require(imported.Settings.BindingsFor(5).SequenceEqual(WindowsPlaytestSettings.CreateDefaultBindings(5)), "invalid key layout replaced valid settings");
        WindowsPlaytestSettingsStore.Save(imported.Settings);
        var json = File.ReadAllText(WindowsPlaytestSettingsStore.SettingsPath);
        Require(!json.Contains("do-not-copy") && WindowsPlaytestSettingsStore.Load().ManiaSpeed == 26, "cfg leaked credentials or new settings did not persist");
        Require(OsuPlaytestImport.NormalizeKey("Oem1") == "OemSemicolon" && OsuPlaytestImport.NormalizeKey("Return") == "Enter" && OsuPlaytestImport.NormalizeKey("65535") == null, "key alias/validation failed");
        Require(!WindowsPlaytestKeyRoutingPolicy.IsPlaybackToggle(Key.Enter,["Enter"]) && !WindowsPlaytestKeyRoutingPolicy.IsPlaybackToggle(Key.Enter,["Return"]), "imported Enter lane was overridden by transport");
        Require(Math.Abs(WindowsPlaytestScrollVelocityPolicy.ScrollTimeMilliseconds(20) - 574.25) < .00001, "official speed formula mismatch");
        Require(Math.Abs(WindowsPlaytestScrollVelocityPolicy.LazerPixelsPerSecond(20, 960) / WindowsPlaytestScrollVelocityPolicy.LazerPixelsPerSecond(20, 480) - 2) < .00001, "scroll speed changed with resolution");

        var load = OsuManiaSkin.LoadAsync(skinRoot, 7, CancellationToken.None); load.Wait(); using var skin = load.Result;
        Require(skin.Widths[3] == 45 && skin.HitPosition == 420 && skin.Notes[3]!.Density == 2 && skin.Notes[1]!.Frames.Length == 2, "skin key sections/HD/custom path/animation failed");
        using (var tintedData = new MemoryStream())
        {
            skin.StageLights[0]!.Frames[0].Save(tintedData,PngBitmapEncoderOptions.Default); tintedData.Position = 0; using var tinted = SKBitmap.Decode(tintedData);
            Require(tinted.GetPixel(0,0).Red == 55 && tinted.GetPixel(0,0).Green == 255, "skin column light tint failed");
        }
        Require(ReferenceEquals(skin.Tails[0], skin.Heads[0]), "LN tail did not fall back to head");
        Require(OsuManiaSkin.DefaultKind(7, 4) == "1" && OsuManiaSkin.DefaultKind(7, 5) == "2", "default symmetrical 7K mapping wrong");
        var document = new ChartDocument { KeyCount = 7, Notes = [new ChartNote { Lane = 3, Beat = new(2,0,1), EndBeat = new(6,0,1) }, new ChartNote { Lane = 1, Beat = new(3,0,1) }] };
        var surface = new PlaytestSurface(); surface.SetSession(new PlaytestSession(document,0,false,ruleset:PlaytestRuleset.OsuStable)); surface.Configure(imported.Settings); surface.SetSkin(skin);
        var host = new Window { Width = 1280, Height = 720, Content = surface }; host.Show(); Program.Pump(20);
        surface.SetCurrentTime(900); surface.SetLanePressed(0, true); surface.ReportJudgement(PlaytestHitGrade.Best, -5, 0);
        Program.Pump(30);
        using (var frame = host.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots,"playtest-osu-skin.png"), PngBitmapEncoderOptions.Default);
        Require(surface.Diagnostics.LastVisitedNotes <= 2 && surface.LaneAt(0) == -1, "skin render culling/lane hit regions failed");
        surface.SetCurrentTime(2850); Program.Pump(20);
        using (var frame = host.CaptureRenderedFrame()!)
        using (var bytes = new MemoryStream())
        {
            frame.Save(bytes, PngBitmapEncoderOptions.Default); bytes.Position = 0; using var pixels = SKBitmap.Decode(bytes);
            var center = (int)Enumerable.Range(0,(int)surface.Bounds.Width).Where(x => surface.LaneAt(x) == 3).Average();
            var tailY = surface.Bounds.Height * 420 / 480 - 150 * (surface.Bounds.Height * 402 / 480) * 26 / 11485;
            Require(pixels.GetPixel(center,(int)tailY + 3) == SKColors.Gold,"LN tail did not use official top anchor at its timestamp");
        }
        var fieldSize = surface.Bounds.Size;
        surface.Configure(imported.Settings.With(upsideDown:true)); Program.Pump(20);
        using (var frame = host.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots,"playtest-osu-skin-upscroll.png"), PngBitmapEncoderOptions.Default);
        Require(surface.Bounds.Size == fieldSize, "skin scroll direction resized field"); host.Close(); surface.SetSkin(null);
        var many = new ChartDocument { KeyCount=7, Notes=Enumerable.Range(0,100000).Select(i=>new ChartNote {Lane=i%7,Beat=BeatPosition.FromDouble(i*.02)}).ToList() };
        var denseSession = new PlaytestSession(many,0,false,ruleset:PlaytestRuleset.OsuStable);
        surface.SetSession(denseSession); surface.SetSkin(skin); surface.SetCurrentTime(50000);
        Require(surface.EvaluateViewportForDiagnostics(720).LastVisitedNotes < 150 && surface.Diagnostics.PerNoteControlCount == 0, "100k skin notes were not culled"); surface.SetSkin(null);

        var archive = Path.Combine(root,"fixture.osk"); ZipFile.CreateFromDirectory(skinRoot, archive);
        var unpacked = OsuManiaSkin.ImportArchiveAsync(archive,CancellationToken.None).GetAwaiter().GetResult();
        Require(File.Exists(Path.Combine(unpacked,"notes","gold@2x.png")), "osk nested import failed");
        var badZip = Path.Combine(root,"bad.osk");
        using (var zip = ZipFile.Open(badZip,ZipArchiveMode.Create)) { using var stream = new StreamWriter(zip.CreateEntry("../escape.ini").Open()); stream.Write("bad"); }
        try { OsuManiaSkin.ImportArchiveAsync(badZip,CancellationToken.None).GetAwaiter().GetResult(); throw new InvalidOperationException("unsafe archive accepted"); }
        catch (InvalidDataException) { }
        Require(!File.Exists(Path.Combine(AvaloniaAppPaths.LocalDataRoot,"skins","escape.ini")),"archive escaped import root");
        File.AppendAllText(Path.Combine(skinRoot,"skin.ini"),"\nNoteImage0: ../../../escape\n");
        using var safe = OsuManiaSkin.LoadAsync(skinRoot,7,CancellationToken.None).GetAwaiter().GetResult();
        Require(safe.Notes[0] == null, "external skin texture path accepted");

        var game = new PlaytestWindow(document,null,Path.Combine(root,"preview.mc"),0,imported.Settings.With(autoPlay:false),new WindowsKeySoundSettings(),1,true,.8f,60,1,1,false);
        game.Show(); Program.PumpUntil(() => Field<bool>(game,"skinSoundReady"));
        game.KeyPress(Key.F11,RawInputModifiers.None,PhysicalKey.F11,"F11");
        game.KeyPress(Key.F11,RawInputModifiers.None,PhysicalKey.F11,"F11");
        game.KeyRelease(Key.F11,RawInputModifiers.None,PhysicalKey.F11,"F11");
        Require(game.WindowState == WindowState.FullScreen && Field<WindowsPlaytestSettings>(game,"settings").FullScreen,"F11 full screen or repeat guard failed");
        game.KeyPress(Key.F11,RawInputModifiers.None,PhysicalKey.F11,"F11"); game.KeyRelease(Key.F11,RawInputModifiers.None,PhysicalKey.F11,"F11");
        Require(game.WindowState == WindowState.Normal && !Field<WindowsPlaytestSettings>(game,"settings").FullScreen,"F11 window restore failed");
        var field = Field<PlaytestSurface>(game,"playfield"); var before = field.Bounds.Size;
        game.KeyPress(Key.F1,RawInputModifiers.None,PhysicalKey.F1,"F1"); Program.Pump(20); game.KeyRelease(Key.F1,RawInputModifiers.None,PhysicalKey.F1,"F1");
        Require(field.Bounds.Size == before && Field<Border>(game,"settingsSide").IsVisible,"settings panel resized gameplay or failed to open");
        game.KeyPress(Key.F6,RawInputModifiers.None,PhysicalKey.F6,"F6"); game.KeyRelease(Key.F6,RawInputModifiers.None,PhysicalKey.F6,"F6");
        Require(Field<WindowsPlaytestSettings>(game,"settings").ManiaSpeed == 27,"imported speed hotkey failed");
        game.KeyPress(Key.F7,RawInputModifiers.None,PhysicalKey.F7,"F7"); game.KeyRelease(Key.F7,RawInputModifiers.None,PhysicalKey.F7,"F7");
        Require(Field<WindowsPlaytestSettings>(game,"settings").ManiaSpeed == 26,"decrease hotkey failed");
        game.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,"Escape"); game.KeyRelease(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,"Escape"); Program.Pump(20);
        Require(Field<Border>(game,"pauseOverlay").IsVisible,"Esc did not close settings to pause menu");
        var pause = game.GetVisualDescendants().OfType<Button>().Single(b=>AutomationProperties.GetAutomationId(b)=="Playtest.Game.Continue");
        Require(pause.Content is Image,"pause assets not applied");
        game.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,"Enter"); game.KeyRelease(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,"Enter"); Program.Pump(40);
        Require(game.Diagnostics.Playing && !Field<Border>(game,"gameChrome").IsVisible && !Field<Border>(game,"pauseOverlay").IsVisible,"gameplay chrome was not hidden");
        typeof(PlaytestWindow).GetField("silentAnchorMilliseconds",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(game,900d - Field<System.Diagnostics.Stopwatch>(game,"silentClock").Elapsed.TotalMilliseconds);
        typeof(PlaytestWindow).GetMethod("RunFrame",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(game,null); Program.Pump(20);
        typeof(PlaytestWindow).GetField("silentAnchorMilliseconds",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(game,1000d - Field<System.Diagnostics.Stopwatch>(game,"silentClock").Elapsed.TotalMilliseconds);
        game.KeyPress(Key.Space,RawInputModifiers.None,PhysicalKey.Space," "); Program.Pump(10);
        Require(game.Diagnostics.ManualKeySoundAccepted > 0,"preloaded skin hitsound did not reach the simulated output");
        game.KeyRelease(Key.Space,RawInputModifiers.None,PhysicalKey.Space," ");
        using (var frame = game.CaptureRenderedFrame()!) frame.Save(Path.Combine(screenshots,"playtest-game-full-surface.png"),PngBitmapEncoderOptions.Default);
        game.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,"Escape");
        game.KeyPress(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,"Escape");
        game.KeyRelease(Key.Escape,RawInputModifiers.None,PhysicalKey.Escape,"Escape"); Program.Pump(20);
        Require(!game.Diagnostics.Playing && game.IsVisible,"Esc quit instead of pausing");
        game.KeyPress(Key.OemTilde,RawInputModifiers.None,PhysicalKey.None,"Tilde"); game.KeyRelease(Key.OemTilde,RawInputModifiers.None,PhysicalKey.None,"Tilde"); Program.Pump(30);
        Require(game.Diagnostics.Playing && game.Diagnostics.Judged == 0 && Field<System.Diagnostics.Stopwatch>(game,"silentClock").ElapsedMilliseconds < 1000,"aliased retry hotkey did not restart");
        game.Close(); Program.Pump(30);
        Console.WriteLine("PASS osu skin/HD/animation/custom paths, cfg import/privacy, .osk path guard, lazer speed and game/pause/settings layout");
    }
    private static T Field<T>(object instance,string name) => (T)instance.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
}

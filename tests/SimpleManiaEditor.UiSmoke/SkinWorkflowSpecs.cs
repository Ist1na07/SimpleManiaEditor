using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using LumaChart.Avalonia.Dialogs;
using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;
using SkiaSharp;

internal static class SkinWorkflowSpecs
{
    public static void Run(string root, string screenshots)
    {
        var container = Path.Combine(root,"skin-workflow"); Directory.CreateDirectory(container);
        var a = Fixture("A", SKColors.Gold); var b = Fixture("B", SKColors.Cyan);
        string Fixture(string name, SKColor colour)
        {
            var folder = Path.Combine(container,name); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"skin.ini"),$"[General]\nName: {name}\nVersion: 2.7\n[Mania]\nColumnStart: 110\nKeys: 4\nColumnWidth: 22,22,22,22\nKeys: 7\nColumnWidth: 40,40,40,50,40,40,40\nNoteImage3: note.png\nHitPosition: 430\n");
            using var bitmap = new SKBitmap(80,16); bitmap.Erase(colour); using var png = bitmap.Encode(SKEncodedImageFormat.Png,100);
            foreach (var file in new[] {"note-0@2x.png","note-1@2x.png","mania-note1.png","mania-note2.png","mania-noteS.png"})
            { using var stream=File.Create(Path.Combine(folder,file)); png.SaveTo(stream); }
            File.WriteAllText(Path.Combine(folder,"normal-hitnormal.wav"),"broken optional audio");
            return folder;
        }
        var registered = OsuSkinLibrary.RegisterFolder(container);
        Require(registered.Count == 2 && OsuSkinLibrary.List().Any(e => e.Path == a),"skin container directory did not populate persisted catalogue");
        using (var parsed = Wait(OsuManiaSkin.LoadAsync(a,7,CancellationToken.None)))
            Require(parsed.Widths[3] == 50 && parsed.HitPosition == 430 && parsed.Notes[3]!.Frames.Length == 2 && parsed.Notes[3]!.Density == 2,
                "official multi-Keys block or extension-bearing animated lookup did not load");
        WindowsPlaytestSettingsStore.Save(new WindowsPlaytestSettings(autoPlay:false,fullScreen:false));
        var first = Window(); first.Show();
        Field<CheckBox>(first,"autoPlayBox").IsChecked = true;
        var pending = first.SelectSkinAsync(a);
        Require(WindowsPlaytestSettingsStore.Load().SkinPath == a,"selection was not saved before async loading finished");
        first.Close(); Wait(pending); Program.Pump(30);
        Require(WindowsPlaytestSettingsStore.Load().SkinPath == a,"closing during skin load reverted the selection");
        Require(WindowsPlaytestSettingsStore.Load().AutoPlay,"preference edits during initial loading were silently discarded");
        var reopened = Window(); reopened.Show(); Program.Pump(100); Wait(Field<Task<bool>>(reopened,"skinLoadTask"));
        Require(reopened.WindowState != WindowState.FullScreen,"editor fullscreen state overrode the saved playtest window setting");
        Require(Field<OsuManiaSkin>(reopened,"activeSkin").Root == a && !Field<bool>(reopened,"skinSoundReady"),"reopen did not restore graphics or broken sound rejected the skin");
        reopened.KeyPress(Key.F1,RawInputModifiers.None,PhysicalKey.F1,"F1"); reopened.KeyRelease(Key.F1,RawInputModifiers.None,PhysicalKey.F1,"F1"); Program.Pump(20);
        var picker = reopened.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c)=="Playtest.SkinSelector");
        Require(((OsuSkinEntry)picker.SelectedItem!).Path == a,"selector reset on re-entry");
        Require(!reopened.GetVisualDescendants().Any(c => AutomationProperties.GetAutomationId(c) is "Playtest.NoteStyle" or "Playtest.NoteWidth" or "Playtest.NoteHeight" or "Playtest.FieldWidth" or "Playtest.JudgementLine" or "Playtest.ComboY" or "Playtest.AccuracyY"),"removed note-style controls still exist");
        picker.SelectedItem = OsuSkinLibrary.List(a).Single(e=>e.Path == b); // Different record instances compare by value.
        Wait(Field<Task<bool>>(reopened,"skinLoadTask"));
        Require(Field<OsuManiaSkin>(reopened,"activeSkin").Name == "B" && WindowsPlaytestSettingsStore.Load().SkinPath == b,"dropdown selection did not load and persist B");
        var older = reopened.SelectSkinAsync(a); var newer = reopened.SelectSkinAsync(b); Wait(newer); Wait(older);
        Require(Field<OsuManiaSkin>(reopened,"activeSkin").Root == b,"stale asynchronous selection replaced the newer skin");
        using (var image = reopened.CaptureRenderedFrame()!) image.Save(Path.Combine(screenshots,"playtest-skin-selector.png"),PngBitmapEncoderOptions.Default);
        reopened.Close(); Program.Pump(30);
        var again = Window(); again.Show(); Program.Pump(80); Wait(Field<Task<bool>>(again,"skinLoadTask"));
        Require(Field<OsuManiaSkin>(again,"activeSkin").Name == "B" && OsuSkinLibrary.List().Count >= 3,"skin and catalogue did not survive subsequent reopen");
        var missing = Path.Combine(container,"missing"); Wait(again.SelectSkinAsync(missing)); again.Close(); Program.Pump(20);
        var recovery = Window(); recovery.Show(); Program.Pump(80); Wait(Field<Task<bool>>(recovery,"skinLoadTask"));
        Require(WindowsPlaytestSettingsStore.Load().SkinPath == missing && Field<ComboBox>(recovery,"skinPicker").SelectedItem is OsuSkinEntry { Available:false },"missing skin silently erased its saved selection");
        Wait(recovery.SelectSkinAsync("")); recovery.Close(); Program.Pump(20);
        WindowsPlaytestSettingsStore.Save(new WindowsPlaytestSettings(autoPlay:false,fullScreen:false,skinPath:a));
        var startup = new PlaytestWindow(new ChartDocument {KeyCount=7,Notes=[new ChartNote{Lane=3,Beat=new(2,0,1)}]},null,Path.Combine(root,"startup.mc"),0,
            WindowsPlaytestSettingsStore.Load(),new WindowsKeySoundSettings(),1,true,.8f,60,1,1,false,autoStart:true);
        startup.Show(); startup.KeyPress(Key.F1,RawInputModifiers.None,PhysicalKey.F1,"F1"); startup.KeyRelease(Key.F1,RawInputModifiers.None,PhysicalKey.F1,"F1");
        Wait(Field<Task<bool>>(startup,"skinLoadTask")); Program.Pump(100);
        Require(!startup.Diagnostics.Playing && Field<Border>(startup,"settingsSide").IsVisible,"initial autostart interrupted skin selection/settings");
        startup.Close(); Program.Pump(20);
        Console.WriteLine("PASS persistent skin catalogue/selector, official multi-Keys and animated paths, close during loading, repeated reopen, rapid switch and missing-skin recovery");

        PlaytestWindow Window() => new(new ChartDocument {KeyCount=7,Notes=[new ChartNote {Lane=3,Beat=new(2,0,1)}]},null,Path.Combine(root,"skin-workflow.mc"),0,
            WindowsPlaytestSettingsStore.Load(),new WindowsKeySoundSettings(),1,true,.8f,60,1,1,true);
    }
    private static T Wait<T>(Task<T> task)
    { var end=DateTime.UtcNow.AddSeconds(10); while(!task.IsCompleted && DateTime.UtcNow<end) Program.Pump(5); if(!task.IsCompleted) throw new TimeoutException("skin load timed out"); return task.GetAwaiter().GetResult(); }
    private static T Field<T>(object value,string name) => (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Require(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
}

using LumaChart.Application.Workspaces;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class SongsProtectionSpecs
{
    public static void Run(string root)
    {
        var songs = Path.Combine(root, "game", "Songs");
        var set = Path.Combine(songs, "100 Sample song");
        Directory.CreateDirectory(Path.Combine(set, "samples"));
        var source = Path.Combine(set, "map.osu");
        OsuManiaSerializer.Save(new ChartDocument { KeyCount = 7, Metadata = new ChartMetadata { AudioFile = "tone.wav" },
            Notes = [new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(4) }] }, source);
        Program.WriteSimulationWave(Path.Combine(set, "tone.wav"));
        File.WriteAllBytes(Path.Combine(set, "samples", "click.wav"), [1, 2, 3, 4]);
        var originalFiles = Directory.GetFiles(songs, "*", SearchOption.AllDirectories).ToDictionary(Path.GetFullPath, File.ReadAllBytes);
        var creation = Path.Combine(root, "creation");
        var workspace = new DesktopDocumentWorkspace(songs, Path.Combine(root, "protected-editing"), "guest", fallbackLibraryRoot: () => creation);
        var tab = workspace.OpenAsync(source).GetAwaiter().GetResult();
        Require(!OsuSongsWorkspacePolicy.IsSongsPath(tab.SourcePath) && tab.SourcePath.StartsWith(creation), "Songs chart was not opened as a separate creation copy");
        Require(File.Exists(Path.Combine(Path.GetDirectoryName(tab.SourcePath)!, "samples", "click.wav")), "nested samples were not copied with the mapset");
        tab.Session.Mutate(d => d.Notes.Add(new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(5) }));
        var saved = workspace.SaveAsync(tab.Id).GetAwaiter().GetResult();
        Require(saved.Status == DesktopChartSaveStatus.Saved && OsuManiaSerializer.Load(tab.SourcePath).Notes.Count == 2, "creation copy did not save edits");
        Require(Directory.GetFiles(songs, "*", SearchOption.AllDirectories).Length == originalFiles.Count &&
            originalFiles.All(item => File.ReadAllBytes(item.Key).SequenceEqual(item.Value)), "opening or saving modified Songs files or created editor artifacts there");
        try { workspace.SaveAsAsync(tab.Id, Path.Combine(set, "unsafe.osu")).GetAwaiter().GetResult(); throw new InvalidOperationException("Save As wrote into Songs"); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Songs 是游戏")) { }
        var reopened = workspace.OpenAsync(source).GetAwaiter().GetResult();
        Require(reopened.SourcePath == tab.SourcePath && reopened.Session.Document.Notes.Count == 2, "reimport overwrote an edited creation copy");
        var created = workspace.CreateAsync(new ChartDocument { KeyCount = 7 }).GetAwaiter().GetResult();
        Require(!OsuSongsWorkspacePolicy.IsSongsPath(created.SourcePath), "new chart used Songs as creation root");
        Console.WriteLine("PASS Songs source isolation, nested assets, protected save, reimport and safe creation root");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LumaChart.Application.Workspaces;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

internal static class WorkspaceCheckpointSpecs
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "sme-checkpoint-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "maps"); var editing = Path.Combine(root, "editing");
        Directory.CreateDirectory(library);
        var source = Path.Combine(library, "source.osu");
        OsuManiaSerializer.Save(new ChartDocument { KeyCount = 7, Notes = [new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(4) }] }, source);
        var original = File.ReadAllBytes(source);
        var workspace = new DesktopDocumentWorkspace(library, editing, "test");
        await workspace.InitializeAsync(); var tab = await workspace.OpenAsync(source);
        tab.Session.Mutate(d => d.Notes.Add(new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(5) }));
        await workspace.PersistAsync();
        var manifestPath = Path.Combine(editing, "desktop-workspace", "workspace.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        Require(manifest["Tabs"]![0]!["WorkingSha256"]!.GetValue<string>() == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tab.WorkingPath))),
            "checkpoint hash differs from the cache it wrote");
        var restarted = new DesktopDocumentWorkspace(library, editing, "test"); await restarted.InitializeAsync();
        Require(restarted.ActiveTab!.Session.Document.Notes.Count == 2 && restarted.ActiveTab.Session.IsDirty && restarted.RecoveryWarnings.Count == 0,
            "ordinary unsaved checkpoint was lost or warned on clean restart");
        Require(File.ReadAllBytes(source).SequenceEqual(original), "checkpoint touched source");
        // Emulate the stale fingerprint produced by 0.1.16 and earlier.
        manifest["Tabs"]![0]!["WorkingSha256"] = new string('0', 64); File.WriteAllText(manifestPath, manifest.ToJsonString());
        var legacy = new DesktopDocumentWorkspace(library, editing, "test"); await legacy.InitializeAsync();
        var recovered = legacy.ActiveTab!;
        Require(recovered.RequiresRecoverySaveAs && recovered.Session.Document.Notes.Count == 2 && recovered.Session.IsDirty,
            "legacy dirty cache was replaced by source");
        await legacy.PersistAsync();
        var twice = new DesktopDocumentWorkspace(library, editing, "test"); await twice.InitializeAsync();
        Require(twice.ActiveTab!.RequiresRecoverySaveAs && twice.RecoveryWarnings.Count == 0, "recovery review state did not persist or warned repeatedly");
        try { await twice.SaveAsync(twice.ActiveTab.Id); throw new Exception("unverified cache overwrote source"); }
        catch (IOException) { }
        try { await twice.SaveAsAsync(twice.ActiveTab.Id, source); throw new Exception("unverified Save As overwrote original"); }
        catch (IOException) { }
        var reviewed = Path.Combine(library, "reviewed.osu");
        await twice.SaveAsAsync(twice.ActiveTab.Id, reviewed);
        Require(!twice.ActiveTab.RequiresRecoverySaveAs && OsuManiaSerializer.Load(reviewed).Notes.Count == 2 && File.ReadAllBytes(source).SequenceEqual(original),
            "reviewed recovery did not save independently");
        File.WriteAllText(twice.ActiveTab.WorkingPath, "invalid cache retained for investigation");
        var corrupt = new DesktopDocumentWorkspace(library, editing, "test"); await corrupt.InitializeAsync();
        Require(corrupt.ActiveTab!.Session.Document.Notes.Count == 2 && Directory.GetFiles(Path.Combine(editing, "desktop-workspace", "unverified"), "*.mc").Length == 1,
            "invalid cache was overwritten without retaining a copy");
        Require(await corrupt.CloseAsync(corrupt.ActiveTab.Id, discardUnsavedChanges: false), "recovered clean source could not close");
        Require(Directory.GetFiles(Path.Combine(editing, "desktop-workspace", "unverified"), "*.mc").Length == 1,
            "closing recovered source deleted the retained unverified cache");
        await legacy.ReloadFromSourceAsync(recovered.Id);
        Require(!recovered.RequiresRecoverySaveAs && recovered.Session.Document.Notes.Count == 1 && File.ReadAllBytes(source).SequenceEqual(original),
            "explicit reload did not leave recovery review mode");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

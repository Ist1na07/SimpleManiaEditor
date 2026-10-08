using LumaChart.Avalonia.Infrastructure;
using LumaChart.Avalonia.Controls;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Application.Editing;

internal static class TrackWorkspaceSpecs
{
    public static void Run(string root)
    {
        var directory = Path.Combine(root, "track-specs");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "main.osu");
        var main = new ChartDocument { KeyCount = 7, Notes = [new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(4) }] };
        OsuManiaSerializer.Save(main, source);
        var original = File.ReadAllBytes(source);
        var storage = Path.Combine(directory, "workspace");
        var workspace = new EditorTrackWorkspace(source, storage);
        var draft = workspace.Add(EditorTrackKind.Draft, main);
        draft.Session!.Mutate(d => d.Notes.Add(new ChartNote { Lane = 2, Beat = BeatPosition.FromDouble(8), EndBeat = BeatPosition.FromDouble(9) }));
        draft.SyncScroll = false; draft.ViewBeat = 8;
        var reference = workspace.Add(EditorTrackKind.Reference, main, main, source);
        workspace.Add(EditorTrackKind.Hitsound, main);
        workspace.Add(EditorTrackKind.Density, main);
        workspace.Add(EditorTrackKind.Difficulty, main);
        Wait(workspace.SaveAsync());
        Require(File.ReadAllBytes(source).SequenceEqual(original) && main.Notes.Count == 1, "draft/reference workspace modified source chart");
        var loaded = new EditorTrackWorkspace(source, storage);
        Require(!loaded.LoadFailed && loaded.Tracks.Count == 5, "workspace track kinds did not survive reopening");
        var restoredDraft = loaded.Tracks.Single(t => t.Kind == EditorTrackKind.Draft);
        Require(restoredDraft.Session!.Document.Notes.Single().IsHold && !restoredDraft.SyncScroll && restoredDraft.ViewBeat == 8,
            "draft notes or independent viewport were lost");
        Require(loaded.Tracks.Single(t => t.Kind == EditorTrackKind.Reference).Session!.IsReadOnly, "reopened reference was editable");
        restoredDraft.Session.BeginInteractiveMutation();
        restoredDraft.Session.UpdateInteractiveMutation(d => d.Notes[0].Lane = 5);
        Wait(loaded.SaveAsync());
        var snapshot = Path.Combine(loaded.DirectoryPath, restoredDraft.Id.ToString("N") + ".mc");
        Require(MalodyMcSerializer.Parse(File.ReadAllText(snapshot)).Notes[0].Lane == 2, "autosave leaked an uncommitted gesture");
        restoredDraft.Session.CancelInteractiveMutation();
        restoredDraft.Visible = false;
        loaded.MarkLayoutChanged();
        Wait(loaded.SaveAsync());
        Require(new EditorTrackWorkspace(source, storage).Tracks.Single(t => t.Id == restoredDraft.Id).Visible == false,
            "hidden draft was dropped instead of preserved");
        Require(File.Exists(Path.Combine(loaded.DirectoryPath, "workspace.json.previous")), "manifest backup was not retained");
        var copiedSource = Path.Combine(directory, "saved-as.osu");
        var copied = loaded.CopyForSource(copiedSource);
        Wait(copied.SaveAsync());
        var copiedReload = new EditorTrackWorkspace(copiedSource, storage);
        Require(copiedReload.Tracks.Count == 5 && copiedReload.Tracks.Single(t => t.Kind == EditorTrackKind.Draft).Session!.Document.Notes.Count == 1,
            "Save As failed to copy track data independently");
        var manifest = Path.Combine(loaded.DirectoryPath, "workspace.json");
        File.WriteAllText(manifest, "corrupt manifest");
        var failed = new EditorTrackWorkspace(source, storage);
        Require(failed.LoadFailed, "corrupt manifest did not fail closed");
        Wait(failed.SaveAsync());
        Require(File.ReadAllText(manifest) == "corrupt manifest", "failed load overwrote recoverable data");
        try { failed.Add(EditorTrackKind.Draft, main); throw new InvalidOperationException("corrupt workspace accepted a new write"); }
        catch (InvalidDataException) { }
        var selected = reference.Session!;
        selected.Document.Notes[0].Sound = "other-song.wav";
        selected.Selection.Add(selected.Document.Notes[0].Id);
        var pattern = MixedSelectionEditingService.Copy(selected, selected.Selection, []).PatternOnly();
        Require(pattern.SnapshotNotes()[0].Sound == null && selected.Document.Notes[0].Sound == "other-song.wav",
            "pattern copy kept foreign resources or modified its reference source");
        var metric = new ChartDocument { KeyCount = 7, Notes = [new ChartNote { Lane = 0, Beat = BeatPosition.Zero },
            new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(.04) },
            new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(.1), IsDraft = true }] };
        Require(EditorMetricTrackSurface.Calculate(metric, false).Sum(s => s.Value) == 2, "density counted drafts or lost official heads");
        Require(EditorMetricTrackSurface.Calculate(metric, true).Sum(s => s.Value) > 2.7, "experimental load ignored a close same-lane repeat");
        Console.WriteLine("PASS track persistence, source isolation, committed recovery, corrupt-manifest guard and metric semantics");
    }

    private static void Wait(Task task) { task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

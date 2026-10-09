using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LumaChart.Application.Editing;
using LumaChart.Core.IO;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Infrastructure;

public enum EditorTrackKind { Draft, Reference, Hitsound, Density, Difficulty }

public sealed class EditorTrack
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public EditorTrackKind Kind { get; init; }
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public bool SyncScroll { get; set; } = true;
    public double ViewBeat { get; set; }
    public string SourcePath { get; init; } = "";
    public EditorSession? Session { get; internal set; }
    internal long SavedSerial { get; set; } = -1;
}

/// <summary>Plaintext side workspace: atomic manifest plus independent .mc snapshots. Never writes the source chart.</summary>
public sealed class EditorTrackWorkspace
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath { get; }
    public string PrimarySourcePath { get; }
    public List<EditorTrack> Tracks { get; } = [];
    public List<string> BoardOrder { get; } = [];
    public bool LoadFailed { get; private set; }
    public bool HasPendingChanges => layoutDirty || Tracks.Any(t => t.Kind is EditorTrackKind.Draft or EditorTrackKind.Reference &&
        t.Session != null && t.Session.ChangeSerial != t.SavedSerial);
    private bool layoutDirty;
    private readonly SemaphoreSlim saveGate = new(1);

    public EditorTrackWorkspace(string sourcePath, string storageRoot)
    {
        PrimarySourcePath = Path.GetFullPath(sourcePath);
        var canonical = PrimarySourcePath.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        DirectoryPath = Path.Combine(storageRoot, hash);
        var manifest = Path.Combine(DirectoryPath, "workspace.json");
        if (!File.Exists(manifest)) return;
        try
        {
            var persisted = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifest), JsonOptions)
                ?? throw new InvalidDataException("轨道清单为空。");
            if (persisted.Version != 1) throw new InvalidDataException("轨道清单版本不受支持。");
            BoardOrder.AddRange(persisted.BoardOrder.Distinct());
            foreach (var item in persisted.Tracks)
            {
                if (item.Id == Guid.Empty || !Enum.IsDefined(item.Kind) || Tracks.Any(t => t.Id == item.Id))
                    throw new InvalidDataException("轨道清单无效。");
                var track = new EditorTrack { Id = item.Id, Kind = item.Kind, Name = item.Name, Visible = item.Visible,
                    SyncScroll = item.SyncScroll, ViewBeat = double.IsFinite(item.ViewBeat) ? Math.Max(0, item.ViewBeat) : 0,
                    SourcePath = item.SourcePath };
                if (track.Kind is EditorTrackKind.Draft or EditorTrackKind.Reference)
                {
                    var file = Path.Combine(DirectoryPath, track.Id.ToString("N") + ".mc");
                    var session = new EditorSession();
                    session.ReplaceDocument(MalodyMcSerializer.Parse(File.ReadAllText(file), file));
                    session.IsReadOnly = track.Kind == EditorTrackKind.Reference;
                    session.CanEditTiming = false;
                    session.CanEditMetadata = false;
                    track.Session = session;
                    track.SavedSerial = session.ChangeSerial;
                }
                Tracks.Add(track);
            }
        }
        catch { LoadFailed = true; Tracks.Clear(); }
    }

    public EditorTrack Add(EditorTrackKind kind, ChartDocument main, ChartDocument? reference = null, string referencePath = "")
    {
        if (LoadFailed) throw new InvalidDataException("轨道资料读取失败；已保留原文件，不能覆盖。请检查 workspace.json 及 .mc 快照。");
        var track = new EditorTrack { Kind = kind, Name = kind switch
        {
            EditorTrackKind.Draft => "草稿", EditorTrackKind.Reference => "对照", EditorTrackKind.Hitsound => "hitsound",
            EditorTrackKind.Density => "局部密度", _ => "局部难度（实验）"
        }, SourcePath = referencePath };
        if (kind is EditorTrackKind.Draft or EditorTrackKind.Reference)
        {
            var document = reference?.Clone() ?? new ChartDocument { KeyCount = main.KeyCount,
                Metadata = main.Metadata.Clone(), Timing = main.Timing.Select(t => t.Clone()).ToList() };
            var session = new EditorSession();
            session.ReplaceDocument(document);
            session.IsReadOnly = kind == EditorTrackKind.Reference;
            session.CanEditTiming = false;
            session.CanEditMetadata = false;
            track.Session = session;
        }
        Tracks.Add(track);
        layoutDirty = true;
        return track;
    }

    public void MarkLayoutChanged() => layoutDirty = true;

    public EditorTrackWorkspace CopyForSource(string newSourcePath)
    {
        var destination = new EditorTrackWorkspace(newSourcePath, Path.GetDirectoryName(DirectoryPath)!);
        // Existing or damaged destination workspaces are never overwritten by Save As.
        if (destination.LoadFailed || destination.Tracks.Count > 0) return destination;
        foreach (var source in Tracks)
        {
            var copy = new EditorTrack { Id = source.Id, Kind = source.Kind, Name = source.Name, Visible = source.Visible,
                SyncScroll = source.SyncScroll, ViewBeat = source.ViewBeat, SourcePath = source.SourcePath };
            if (source.Kind is EditorTrackKind.Draft or EditorTrackKind.Reference && source.Session != null)
            {
                var session = new EditorSession { IsReadOnly = source.Session.IsReadOnly, CanEditTiming = false, CanEditMetadata = false };
                session.ReplaceDocument(source.Session.CaptureCommittedDocument());
                copy.Session = session;
            }
            destination.Tracks.Add(copy);
        }
        destination.BoardOrder.AddRange(BoardOrder);
        destination.layoutDirty = true;
        return destination;
    }

    public async Task SaveAsync()
    {
        if (LoadFailed) return;
        await saveGate.WaitAsync();
        try
        {
            var documents = Tracks.Where(t => t.Kind is EditorTrackKind.Draft or EditorTrackKind.Reference &&
                t.Session != null && t.Session.ChangeSerial != t.SavedSerial)
                .Select(t => (Track: t, Serial: t.Session!.ChangeSerial, Document: t.Session.CaptureCommittedDocument())).ToArray();
            if (!layoutDirty && documents.Length == 0) return;
            var layout = new Manifest { PrimarySourcePath = PrimarySourcePath, BoardOrder = BoardOrder.ToArray(), Tracks = Tracks.Select(t => new Descriptor { Id = t.Id, Kind = t.Kind, Name = t.Name,
                Visible = t.Visible, SyncScroll = t.SyncScroll, ViewBeat = t.ViewBeat, SourcePath = t.SourcePath }).ToArray() };
            layoutDirty = false;
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(DirectoryPath);
                    foreach (var item in documents)
                        AtomicWrite(Path.Combine(DirectoryPath, item.Track.Id.ToString("N") + ".mc"), MalodyMcSerializer.Serialize(item.Document));
                    AtomicWrite(Path.Combine(DirectoryPath, "workspace.json"), JsonSerializer.Serialize(layout, JsonOptions));
                });
                foreach (var item in documents) item.Track.SavedSerial = item.Serial;
            }
            catch { layoutDirty = true; throw; }
        }
        finally { saveGate.Release(); }
    }

    private static void AtomicWrite(string destination, string content)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            { writer.Write(content); writer.Flush(); stream.Flush(flushToDisk: true); }
            if (File.Exists(destination)) File.Copy(destination, destination + ".previous", overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public string PrimarySourcePath { get; set; } = "";
        public string[] BoardOrder { get; set; } = [];
        public Descriptor[] Tracks { get; set; } = [];
    }
    private sealed class Descriptor
    {
        public Guid Id { get; set; }
        public EditorTrackKind Kind { get; set; }
        public string Name { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool SyncScroll { get; set; } = true;
        public double ViewBeat { get; set; }
        public string SourcePath { get; set; } = "";
    }
}

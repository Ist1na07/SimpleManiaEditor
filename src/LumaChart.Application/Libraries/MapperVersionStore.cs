using System.Text;
using LibGit2Sharp;
using LumaChart.Application.Workspaces;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Libraries;

public sealed record MapperVersion(string Id, string Message, DateTimeOffset Time)
{
    public override string ToString() => $"{Time.LocalDateTime:MM-dd HH:mm:ss}  {Message}  · {Id[..7]}";
}
public sealed record MapperNoteChange(int Lane, double StartMs, double? EndMs, bool Added);
public sealed record MapperSemanticDiff(IReadOnlyList<MapperNoteChange> Notes, string[] Fields, string[] Timing, string[] Resources)
{
    public string Summary => $"+{Notes.Count(n => n.Added)} / −{Notes.Count(n => !n.Added)} 音符 · {Timing.Length} Timing · {Fields.Length} 属性 · {Resources.Length} 资源";
}

/// <summary>A real, offline Git repository per chart identity; libgit2 is bundled, no Git installation needed.
/// Refs/objects live in the profile, outside Songs and user mapsets. Restores create new history, never reset it.</summary>
public sealed class MapperVersionStore
{
    private readonly string root;
    private readonly MapperLibraryStore library;
    private readonly SemaphoreSlim gate = new(1, 1);
    public MapperVersionStore(string profile, MapperLibraryStore library)
    { root = Path.Combine(profile, "Versions"); OsuFileSafetyPolicy.RequireWritable(root); Directory.CreateDirectory(root); this.library = library; }
    public string RepositoryPath(string chart) => Path.Combine(root, library.Identity(chart) + ".git");
    public IReadOnlyList<MapperVersion> List(string chart)
    {
        var path = RepositoryPath(chart); if (!Directory.Exists(path)) return [];
        using var repository = new Repository(path);
        return repository.Commits.Take(1000).Select(c => new MapperVersion(c.Sha, c.MessageShort, c.Committer.When)).ToArray();
    }
    public async Task<MapperVersion?> SaveAsync(string chart, ChartDocument document, string message, bool includeResources)
    {
        var snapshot = document.Clone();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var path = RepositoryPath(chart); if (!Directory.Exists(path)) Repository.Init(path, isBare: true);
                using var repository = new Repository(path);
                var parent = repository.Head.Tip;
                var definition = parent == null ? new TreeDefinition() : TreeDefinition.From(parent.Tree);
                void AddText(string name, string text)
                { using var input = new MemoryStream(Encoding.UTF8.GetBytes(text)); definition.Add(name, repository.ObjectDatabase.CreateBlob(input), Mode.NonExecutableFile); }
                AddText("editor.mc", MalodyMcSerializer.Serialize(snapshot));
                var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".osu");
                try { OsuManiaSerializer.Save(snapshot.Clone(), temporary); AddText("chart.osu", File.ReadAllText(temporary)); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                if (includeResources)
                {
                    if (parent?.Tree["resources"] != null) definition.Remove("resources");
                    var folder = Path.GetDirectoryName(Path.GetFullPath(chart))!;
                    foreach (var file in EnumerateResources(folder))
                    {
                        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var blob = repository.ObjectDatabase.CreateBlob(input);
                        definition.Add("resources/" + Path.GetRelativePath(folder, file).Replace('\\', '/'), blob, Mode.NonExecutableFile);
                    }
                }
                var tree = repository.ObjectDatabase.CreateTree(definition);
                if (parent != null && tree.Id == parent.Tree.Id) return null;
                var signature = new Signature("Mapper", "mapper@localhost", DateTimeOffset.Now);
                var commit = repository.ObjectDatabase.CreateCommit(signature, signature, string.IsNullOrWhiteSpace(message) ? "保存" : message.Trim(), tree,
                    parent == null ? [] : new[] { parent }, false);
                if (repository.Refs["refs/heads/main"] == null) repository.Refs.Add("refs/heads/main", commit.Id);
                else repository.Refs.UpdateTarget(repository.Refs["refs/heads/main"], commit.Id);
                repository.Refs.UpdateTarget(repository.Refs.Head, "refs/heads/main");
                return new MapperVersion(commit.Sha, commit.MessageShort, signature.When);
            }).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private static IEnumerable<string> EnumerateResources(string folder)
    {
        var stack = new Stack<string>(); stack.Push(folder);
        while (stack.TryPop(out var directory))
            foreach (var file in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("版本资源包含目录链接，请先整理为独立文件。");
                if (Path.GetFileName(file).StartsWith('.')) continue;
                if (Directory.Exists(file)) { stack.Push(file); continue; }
                if (Path.GetExtension(file).ToLowerInvariant() is ".osu" or ".mc" or ".log" or ".db" or ".cfg") continue;
                yield return file;
            }
    }
    public ChartDocument Read(string chart, string revision)
    {
        using var repository = new Repository(RepositoryPath(chart));
        var commit = repository.Lookup<Commit>(revision) ?? throw new IOException("找不到版本。");
        var blob = (Blob)commit.Tree["editor.mc"].Target;
        var document = MalodyMcSerializer.Parse(blob.GetContentText()); document.FilePath = chart; return document;
    }
    public IReadOnlyList<string> ChangedResources(string chart, string revision)
    {
        using var repository = new Repository(RepositoryPath(chart));
        var commit = repository.Lookup<Commit>(revision) ?? throw new IOException("找不到版本。");
        var parent = commit.Parents.FirstOrDefault();
        return repository.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree).Where(change => change.Path.StartsWith("resources/", StringComparison.Ordinal))
            .Select(change => change.Status + " " + change.Path[10..]).ToArray();
    }
    public void Export(string chart, string revision, string newFolder)
    {
        OsuFileSafetyPolicy.RequireWritable(newFolder);
        if (Directory.Exists(newFolder) || File.Exists(newFolder)) throw new IOException("版本导出目录必须不存在，避免覆盖当前作品。");
        using var repository = new Repository(RepositoryPath(chart));
        var commit = repository.Lookup<Commit>(revision) ?? throw new IOException("找不到版本。");
        Directory.CreateDirectory(newFolder);
        void WriteTree(Tree tree, string relative)
        {
            foreach (var entry in tree)
            {
                var next = Path.Combine(relative, entry.Name);
                if (entry.Target is Tree child) WriteTree(child, next);
                else if (entry.Target is Blob blob)
                {
                    var target = Path.GetFullPath(Path.Combine(newFolder, next));
                    if (!OsuFileSafetyPolicy.IsWithin(target, newFolder)) throw new IOException("版本包含不安全路径。");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); using var input = blob.GetContentStream(); using var output = File.Create(target); input.CopyTo(output);
                }
            }
        }
        if (commit.Tree["resources"]?.Target is Tree resources) WriteTree(resources, "");
        using var chartInput = ((Blob)commit.Tree["chart.osu"].Target).GetContentStream();
        using var chartOutput = new FileStream(Path.Combine(newFolder, Path.GetFileNameWithoutExtension(chart) + ".osu"), FileMode.CreateNew); chartInput.CopyTo(chartOutput);
    }
    public static MapperSemanticDiff Diff(ChartDocument before, ChartDocument after, string[]? resources = null)
    {
        static Dictionary<(int Lane, long Start, long? End), int> Notes(ChartDocument document)
        {
            var timing = new TimingMap(document.Timing);
            return document.Notes.Where(note => !note.IsDraft).GroupBy(n => (n.Lane, (long)Math.Round(timing.BeatToMilliseconds(n.Beat)),
                n.EndBeat.HasValue ? (long?)Math.Round(timing.BeatToMilliseconds(n.EndBeat.Value)) : null)).ToDictionary(g => g.Key, g => g.Count());
        }
        var old = Notes(before); var current = Notes(after); var changes = new List<MapperNoteChange>();
        foreach (var key in old.Keys.Concat(current.Keys).Distinct())
        {
            var count = current.GetValueOrDefault(key) - old.GetValueOrDefault(key);
            for (var i = 0; i < Math.Abs(count); i++) changes.Add(new(key.Lane, key.Start, key.End, count > 0));
        }
        static string[] TimingLines(ChartDocument doc) => doc.Timing.Select(t => $"{t.Beat} BPM {t.Bpm} Delay {t.Delay} {t.Effect} {t.EffectValue} {t.UnknownData}").ToArray();
        var fields = typeof(ChartMetadata).GetProperties().Where(property => !Equals(property.GetValue(before.Metadata), property.GetValue(after.Metadata)))
            .Select(property => $"{property.Name}: {property.GetValue(before.Metadata)} → {property.GetValue(after.Metadata)}").ToList();
        if (before.KeyCount != after.KeyCount) fields.Add($"键数: {before.KeyCount} → {after.KeyCount}");
        var left = TimingLines(before); var right = TimingLines(after);
        var timingChanges = left.Except(right).Select(t => "− " + t).Concat(right.Except(left).Select(t => "+ " + t)).ToArray();
        return new(changes.OrderBy(n => n.StartMs).ToArray(), fields.ToArray(), timingChanges, resources ?? []);
    }
    public static void CopyDocument(ChartDocument target, ChartDocument source)
    {
        var copy = source.Clone(); target.Metadata = copy.Metadata; target.KeyCount = copy.KeyCount; target.Notes = copy.Notes;
        target.Timing = copy.Timing; target.UnknownRoot = copy.UnknownRoot; target.UnknownMeta = copy.UnknownMeta;
        target.UnknownSong = copy.UnknownSong; target.UnknownModeExt = copy.UnknownModeExt; target.UnknownEffects = copy.UnknownEffects; target.UnknownNotes = copy.UnknownNotes;
    }
}

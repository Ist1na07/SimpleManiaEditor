using System.Security.Cryptography;
using System.Text.Json;
using LumaChart.Application.Workspaces;

namespace LumaChart.Application.Libraries;

public sealed record TransferFile(string RelativePath, long Bytes, string Sha256);
public sealed record MapsetTransferPlan(string Id, string Source, string Destination, bool Move, TransferFile[] Files)
{
    public long Bytes => Files.Sum(file => file.Bytes);
}
public sealed record MapsetTransferRecord(MapsetTransferPlan Plan, string Stage, string Backup, string State, DateTimeOffset Time);

/// <summary>Only this explicit, previewed workflow may publish a new mapset into Songs.
/// Never overwrites an existing destination. Copy, verify, durable journal, publish, optional source removal.
/// Interrupted transactions remain recoverable, including across volumes; backups are never silently purged.</summary>
public sealed class MapsetTransferService
{
    private readonly string journalRoot;
    private readonly MapperLibraryStore? library;
    private readonly Func<bool> gameRunning;
    private readonly SemaphoreSlim gate = new(1, 1);
    public Action<string>? FaultInjection { get; set; }
    public MapsetTransferService(string profile, MapperLibraryStore? library = null, Func<bool>? gameRunning = null)
    {
        this.library = library; this.gameRunning = gameRunning ?? IsGameRunning;
        journalRoot = Path.Combine(profile, "Transfers"); OsuFileSafetyPolicy.RequireWritable(journalRoot);
        Directory.CreateDirectory(journalRoot);
    }
    public static MapsetTransferPlan Preview(string sourceFolder, string destinationRoot, bool move)
    {
        var source = OsuFileSafetyPolicy.ResolvePhysicalPath(sourceFolder);
        var root = OsuFileSafetyPolicy.ResolvePhysicalPath(destinationRoot);
        if (!Directory.Exists(source) || !Directory.Exists(root)) throw new IOException("源或目标目录不存在。");
        static bool GameRoot(string path) => new[] { "osu!.exe", "osu.exe", "osu!.db", "collection.db", "client.realm" }.Any(name => File.Exists(Path.Combine(path, name)));
        if (GameRoot(source) || Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)).Equals("Songs", StringComparison.OrdinalIgnoreCase))
            throw new IOException("不能迁移 osu! 安装目录或整个 Songs，请选择其中一套歌曲文件夹。");
        if (GameRoot(root)) throw new IOException("目标是 osu! 安装目录，请选择它的 Songs 文件夹。");
        if (OsuFileSafetyPolicy.IsWithin(root, source))
            throw new IOException("源与目标目录不能相互包含；请选择另一处曲库。");
        if (!Directory.EnumerateFiles(source, "*.osu").Any()) throw new IOException("请选择包含 .osu 的歌曲文件夹，不能迁移整个 Songs 或曲库根目录。");
        var files = Inventory(source);
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); var destination = Path.Combine(root, name); var suffix = 1;
        while (Directory.Exists(destination) || File.Exists(destination)) destination = Path.Combine(root, name + " (" + suffix++ + ")");
        return new(Guid.NewGuid().ToString("N"), source, destination, move, files);
    }
    private static TransferFile[] Inventory(string folder)
    {
        NoLink(folder); var files = new List<TransferFile>(); var stack = new Stack<string>(); stack.Push(folder);
        while (stack.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(stack.Pop()))
            {
                NoLink(entry);
                if (Directory.Exists(entry))
                {
                    if (Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("歌曲文件夹含独立 .git 仓库；请先使用创作历史或整理该目录。");
                    stack.Push(entry); continue;
                }
                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Add(new(Path.GetRelativePath(folder, entry), stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
            }
        }
        return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static void NoLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("迁移不跟随目录链接或符号链接。");
    }
    private static string Inside(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!OsuFileSafetyPolicy.IsWithin(path, root) || path == Path.GetFullPath(root)) throw new IOException("迁移清单包含不安全路径。");
        return path;
    }
    private static void Verify(string folder, TransferFile[] expected)
    {
        if (!Inventory(folder).SequenceEqual(expected)) throw new IOException("文件在预览后发生变化，已停止迁移；请刷新预览。");
    }
    public IReadOnlyList<MapsetTransferRecord> History() => Directory.EnumerateFiles(journalRoot, "*.json")
        .Select(file => JsonSerializer.Deserialize<MapsetTransferRecord>(File.ReadAllBytes(file)) ?? throw new IOException("迁移日志损坏：" + file))
        .OrderByDescending(item => item.Time).ToArray();
    private void Save(MapsetTransferRecord record)
    {
        var path = Path.Combine(journalRoot, record.Plan.Id + ".json");
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, record); stream.Flush(true); }
        File.Move(temp, path, true);
    }
    private static void CopyVerified(string source, string target, TransferFile[] files, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); var from = Inside(source, file.RelativePath); var to = Inside(target, file.RelativePath);
            NoLink(from); Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            input.CopyTo(output); output.Flush(true);
        }
        Verify(target, files);
    }
    private static void RemoveVerified(string folder, TransferFile[] files)
    {
        Verify(folder, files);
        // Keep all source files locked against writes until the deletion phase is complete.
        var streams = new List<FileStream>();
        try
        {
            foreach (var file in files) streams.Add(new FileStream(Inside(folder, file.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            for (var i = 0; i < files.Length; i++)
            {
                streams[i].Position = 0;
                if (Convert.ToHexString(SHA256.HashData(streams[i])) != files[i].Sha256) throw new IOException("源文件发生变化，取消清理。");
            }
            foreach (var file in files) File.Delete(Inside(folder, file.RelativePath));
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
        foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(s => s.Length))
        { NoLink(directory); if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
        Directory.Delete(folder); // A concurrent new file prevents removal, rather than being recursively deleted.
    }
    private void CheckGameClosed(MapsetTransferPlan plan)
    {
        if (!(OsuFileSafetyPolicy.IsProtectedPath(plan.Source) || OsuFileSafetyPolicy.IsProtectedPath(plan.Destination))) return;
        if (gameRunning()) throw new IOException("请先退出 osu!，再执行涉及 Songs 的迁移或回退。");
    }
    private static bool IsGameRunning()
    {
        var running = false;
        foreach (var name in new[] { "osu!", "osu" })
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            { using (process) { if (!process.HasExited) running = true; } }
        return running;
    }
    public async Task<MapsetTransferRecord> ExecuteAsync(MapsetTransferPlan plan, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (!Guid.TryParseExact(plan.Id, "N", out _) || plan.Files.Length == 0 ||
                    OsuFileSafetyPolicy.IsWithin(plan.Destination, plan.Source) || OsuFileSafetyPolicy.IsWithin(plan.Source, plan.Destination))
                    throw new IOException("迁移计划无效，请重新预览。");
                CheckGameClosed(plan); Verify(plan.Source, plan.Files);
                if (Directory.Exists(plan.Destination) || File.Exists(plan.Destination)) throw new IOException("目标已出现同名文件夹，请重新预览。");
                var stage = Path.Combine(Path.GetDirectoryName(plan.Destination)!, ".sme-transfer-" + plan.Id);
                if (Directory.Exists(stage) || File.Exists(stage) || File.Exists(Path.Combine(journalRoot, plan.Id + ".json")))
                    throw new IOException("该事务已有记录，请在迁移记录中恢复，或重新生成预览。");
                var record = new MapsetTransferRecord(plan, stage, Path.Combine(journalRoot, plan.Id + "-source"), "Preparing", DateTimeOffset.UtcNow);
                Save(record);
                CopyVerified(plan.Source, stage, plan.Files, token); FaultInjection?.Invoke("Copied");
                if (plan.Move) CopyVerified(plan.Source, record.Backup, plan.Files, token);
                Verify(plan.Source, plan.Files); token.ThrowIfCancellationRequested();
                record = record with { State = "Publishing" }; Save(record);
                Directory.Move(stage, plan.Destination); FaultInjection?.Invoke("Published");
                record = record with { State = "Published" }; Save(record);
                if (plan.Move)
                {
                    record = record with { State = "RemovingSource" }; Save(record);
                    RemoveVerified(plan.Source, plan.Files); FaultInjection?.Invoke("SourceRemoved");
                }
                library?.LinkCopiesBatch(plan.Files.Where(file => file.RelativePath.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)).Select(file => (Path.Combine(plan.Source, file.RelativePath), Path.Combine(plan.Destination, file.RelativePath))));
                record = record with { State = "Complete" }; Save(record); return record;
            }, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task RollbackAsync(string id)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var record = History().Single(item => item.Plan.Id == id); var plan = record.Plan;
                if (record.State == "RolledBack") return;
                CheckGameClosed(plan);
                // Refuse to discard edits made in the new destination after publication.
                if (Directory.Exists(plan.Destination))
                {
                    if (record.State == "RollingBack") VerifySubset(plan.Destination, plan.Files);
                    else Verify(plan.Destination, plan.Files);
                }
                if (plan.Move && Directory.Exists(record.Backup))
                {
                    Verify(record.Backup, plan.Files);
                    foreach (var file in plan.Files)
                    {
                        var target = Inside(plan.Source, file.RelativePath);
                        if (File.Exists(target))
                        {
                            using var input = File.OpenRead(target);
                            if (Convert.ToHexString(SHA256.HashData(input)) != file.Sha256) throw new IOException("源位置有新修改，不能自动回退；备份已保留。");
                        }
                    }
                    Directory.CreateDirectory(plan.Source);
                    foreach (var file in plan.Files)
                    {
                        var target = Inside(plan.Source, file.RelativePath); if (File.Exists(target)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Inside(record.Backup, file.RelativePath), target, false);
                    }
                    Verify(plan.Source, plan.Files);
                }
                else Verify(plan.Source, plan.Files);
                // Record rollback intent before removal. Recovery can repeat this safely.
                Save(record with { State = "RollingBack" });
                if (Directory.Exists(plan.Destination)) RemoveVerified(plan.Destination, VerifySubset(plan.Destination, plan.Files));
                if (Directory.Exists(record.Stage))
                {
                    // A cancelled preparation can contain only a prefix of the original files.
                    var actual = Inventory(record.Stage);
                    foreach (var file in actual)
                    {
                        var expected = plan.Files.SingleOrDefault(item => item.RelativePath == file.RelativePath);
                        if (expected == null || file.Bytes > expected.Bytes) throw new IOException("临时目录出现未知内容，已保留供检查。");
                        // A power loss can leave the last staged file truncated. Only discard a
                        // verified prefix of the intact original, never arbitrary partial content.
                        using var original = File.OpenRead(Inside(plan.Source, file.RelativePath));
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[65536]; var remaining = file.Bytes;
                        while (remaining > 0)
                        {
                            var read = original.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read == 0) throw new IOException("源文件不完整，临时目录已保留。");
                            hash.AppendData(buffer, 0, read); remaining -= read;
                        }
                        if (Convert.ToHexString(hash.GetHashAndReset()) != file.Sha256) throw new IOException("临时文件与源文件不符，已保留供检查。");
                    }
                    RemoveVerified(record.Stage, actual);
                }
                Save(record with { State = "RolledBack" });
            }).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private static TransferFile[] VerifySubset(string folder, TransferFile[] expected)
    {
        var actual = Inventory(folder);
        if (actual.Any(file => !expected.Contains(file))) throw new IOException("回退目录出现新增或修改内容，已停止清理。");
        return actual;
    }
}

using LumaChart.Application.Workspaces;
using osu.Framework.Platform;
using osu.Game.IO;

namespace SimpleManiaEditor.OsuHost;

/// <summary>The embedded player has one editor-owned profile, never a standalone
/// client's storage.ini redirect or data-migration capability.</summary>
internal sealed class FixedPlaytestStorage : WrappedStorage
{
    private readonly string root;
    public FixedPlaytestStorage(Storage storage) : base(storage)
    {
        root = OsuFileSafetyPolicy.ResolvePhysicalPath(storage.GetFullPath(string.Empty));
        OsuFileSafetyPolicy.RequireWritable(root);
    }
    private void Check(string path)
    {
        var candidate = base.GetFullPath(path);
        if (!OsuFileSafetyPolicy.IsWithin(candidate, root))
            throw new UnauthorizedAccessException("试玩只能访问编辑器自己的数据目录，不能跳转或迁移到其他客户端目录。");
    }
    public override string GetFullPath(string path, bool createIfNotExisting = false)
    {
        Check(path);
        return base.GetFullPath(path, createIfNotExisting);
    }
    public override Stream GetStream(string path, FileAccess access = FileAccess.Read, FileMode mode = FileMode.OpenOrCreate)
    {
        Check(path);
        return base.GetStream(path, access, mode);
    }
    public override void Delete(string path) { Check(path); base.Delete(path); }
    public override void DeleteDirectory(string path) { Check(path); base.DeleteDirectory(path); }
    public override void Move(string from, string to) { Check(from); Check(to); base.Move(from, to); }
    public override IEnumerable<string> GetFiles(string path, string pattern = "*") { Check(path); return base.GetFiles(path, pattern); }
    public override IEnumerable<string> GetDirectories(string path) { Check(path); return base.GetDirectories(path); }
}

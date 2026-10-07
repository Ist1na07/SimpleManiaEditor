using LumaChart.Core.Localization;

namespace LumaChart.Application.Workspaces;

public readonly record struct LocalLibraryRootResolution(string RequestedRoot, string ResolvedRoot)
{
    public bool FollowedLink => !string.Equals(
        RequestedRoot,
        ResolvedRoot,
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves only a user-selected library root. Recursive scans still skip descendant reparse
/// points, so accepting a linked root does not make traversal follow links that escape it.
/// </summary>
public static class LocalLibraryRootResolver
{
    private const int MaximumRootLinkDepth = 32;

    public static LocalLibraryRootResolution Resolve(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("工作目录不能为空。", nameof(root));

        string requestedRoot;
        try
        {
            requestedRoot = Normalize(root);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            throw new IOException(LocalizationCatalog.Format("工作目录路径无效：{0}", root), exception);
        }

        var current = requestedRoot;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < MaximumRootLinkDepth; depth++)
        {
            if (!visited.Add(current))
                throw new IOException(LocalizationCatalog.Format("工作目录的符号链接形成循环，已停止扫描：{0}", requestedRoot));

            var directory = new DirectoryInfo(current);
            FileAttributes attributes;
            try
            {
                if (!directory.Exists)
                    throw new DirectoryNotFoundException(
                        LocalizationCatalog.Format("工作目录不存在、无法访问，或目录链接的目标已经断开：{0}", current));
                attributes = directory.Attributes;
            }
            catch (DirectoryNotFoundException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(LocalizationCatalog.Format("无法访问工作目录：{0}", current), exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
                return new LocalLibraryRootResolution(requestedRoot, Normalize(directory.FullName));

            FileSystemInfo? target;
            try
            {
                target = directory.ResolveLinkTarget(returnFinalTarget: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(LocalizationCatalog.Format("无法解析工作目录的符号链接或重解析点：{0}", current), exception);
            }

            if (target == null)
                throw new IOException(LocalizationCatalog.Format("工作目录是无法安全解析的重解析点，已停止扫描：{0}", current));
            try
            {
                current = Normalize(target.FullName);
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or NotSupportedException)
            {
                throw new IOException(LocalizationCatalog.Format("工作目录链接指向无效路径：{0}", current), exception);
            }
        }

        throw new IOException(
            LocalizationCatalog.Format("工作目录的符号链接层级超过安全上限 {0}，已停止扫描：{1}", MaximumRootLinkDepth, requestedRoot));
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

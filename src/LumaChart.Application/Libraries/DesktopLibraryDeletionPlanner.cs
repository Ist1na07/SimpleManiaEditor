using LumaChart.Core.Localization;

namespace LumaChart.Application.Libraries;

public enum DesktopLibraryDeletionBlockReason
{
    None,
    EmptySelection,
    InvalidFolderPath,
    UnsafeWorkspaceBoundary,
    OpenDocument,
    ProtectedFolder,
    ContainsOtherChartFolders
}

public sealed record DesktopLibraryOpenDocumentReference(
    string DisplayTitle,
    IReadOnlyList<string> Paths);

public sealed record DesktopLibraryDeletionTarget(
    DesktopLibraryFolderEntry Folder,
    string FullPath);

public sealed record DesktopLibraryDeletionPlan(
    bool CanExecute,
    DesktopLibraryDeletionBlockReason BlockReason,
    string? BlockMessage,
    IReadOnlyList<DesktopLibraryDeletionTarget> Targets,
    IReadOnlyList<string> BlockingDocumentTitles,
    int BlockingDocumentCount,
    int ChartCount,
    IReadOnlyList<string> PreviewItems,
    int OmittedPreviewCount,
    string PreviewText,
    string? ConfirmationMessage,
    bool PermanentDeletion = false)
{
    public int FolderCount => Targets.Count;
}

/// <summary>
/// Builds the UI-free, fail-closed deletion plan used by the Windows chart library.
/// The path, open-document, de-duplication and confirmation-copy rules intentionally
/// match the Windows/WPF 1.1.0 behavior. The caller remains responsible for invoking
/// a Windows recycle-bin adapter only after a successful plan is confirmed.
/// </summary>
public static class DesktopLibraryDeletionPlanner
{
    public const int MaximumConfirmationPreviewItems = 6;
    public const int MaximumBlockingDocumentTitles = 5;
    public const string InvalidFolderPathMessage = "选中项包含无效路径，已取消删除。";
    public const string UnsafeWorkspaceBoundaryMessage =
        "不能删除工作目录根目录或工作目录之外的文件夹。";
    public const string ProtectedFolderMessage =
        "为防止误删，不能删除磁盘根目录、用户目录或系统文件夹。";
    public const string ContainsOtherChartFoldersMessage =
        "选中的文件夹中还包含其他谱面文件夹，为防止误删已取消删除。";
    public const string RecycleChoiceLabel = "移入回收站";
    public const string PermanentDeleteChoiceLabel = "永久删除";

    /// <summary>
    /// Platforms without a recycle bin delete outright, so their confirmation
    /// copy must say so instead of promising a restorable recycle.
    /// </summary>
    public static bool DefaultPermanentDeletion => !OperatingSystem.IsWindows();

    public static string GetChoiceLabel(bool permanentDeletion) =>
        permanentDeletion ? PermanentDeleteChoiceLabel : RecycleChoiceLabel;

    public static DesktopLibraryDeletionPlan Create(
        string workspaceRoot,
        IEnumerable<DesktopLibraryFolderEntry> selectedFolders,
        IEnumerable<DesktopLibraryOpenDocumentReference>? openDocuments = null,
        bool? permanentDeletion = null)
    {
        ArgumentNullException.ThrowIfNull(selectedFolders);
        var permanent = permanentDeletion ?? DefaultPermanentDeletion;

        var selected = selectedFolders.ToArray();
        if (selected.Length == 0)
            return Blocked(DesktopLibraryDeletionBlockReason.EmptySelection, blockMessage: null);

        if (!TryNormalizePath(workspaceRoot, out var normalizedWorkspaceRoot))
            return Blocked(
                DesktopLibraryDeletionBlockReason.UnsafeWorkspaceBoundary,
                UnsafeWorkspaceBoundaryMessage);

        var normalizedSelections = new List<DesktopLibraryDeletionTarget>(selected.Length);
        foreach (var folder in selected)
        {
            if (folder == null || !TryNormalizePath(folder.FolderPath, out var fullPath))
                return Blocked(
                    DesktopLibraryDeletionBlockReason.InvalidFolderPath,
                    InvalidFolderPathMessage);
            if (!IsStrictDescendant(normalizedWorkspaceRoot, fullPath))
                return Blocked(
                    DesktopLibraryDeletionBlockReason.UnsafeWorkspaceBoundary,
                    UnsafeWorkspaceBoundaryMessage);
            normalizedSelections.Add(new DesktopLibraryDeletionTarget(folder, fullPath));
        }

        // Parents are considered before descendants even when an authored path contains
        // redundant dot segments. This preserves WPF's shortest-path-first behavior while
        // making the normalized plan deterministic and preventing duplicate shell requests.
        var targets = new List<DesktopLibraryDeletionTarget>(normalizedSelections.Count);
        foreach (var candidate in normalizedSelections
                     .OrderBy(item => item.FullPath.Length))
        {
            if (targets.Any(parent => IsSameOrDescendant(candidate.FullPath, parent.FullPath)))
                continue;
            targets.Add(candidate);
        }

        // A library card is the chart's direct parent folder. With a broad
        // library root (for example the user profile) that folder can be the
        // Desktop or Documents itself, so recycling "the song folder" would
        // take the whole known folder with it. Refuse instead of guessing.
        if (targets.Any(target => IsProtectedFolder(target.FullPath)))
            return Blocked(
                DesktopLibraryDeletionBlockReason.ProtectedFolder,
                ProtectedFolderMessage);

        var blockedDocuments = (openDocuments ?? [])
            .Where(document => document != null &&
                               (document.Paths ?? []).Any(path =>
                                   targets.Any(target => IsSameOrDescendant(path, target.FullPath))))
            .ToArray();
        if (blockedDocuments.Length > 0)
        {
            var titles = blockedDocuments
                .Select(document => document.DisplayTitle)
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumBlockingDocumentTitles)
                .ToArray();
            var names = string.Join("、", titles);
            return new DesktopLibraryDeletionPlan(
                CanExecute: false,
                BlockReason: DesktopLibraryDeletionBlockReason.OpenDocument,
                BlockMessage:
                    LocalizationCatalog.Format("选中文件夹中有正在编辑的谱面：{0}。\n\n请先关闭对应谱面标签，再执行删除。", names),
                Targets: targets,
                BlockingDocumentTitles: titles,
                BlockingDocumentCount: blockedDocuments.Length,
                ChartCount: 0,
                PreviewItems: [],
                OmittedPreviewCount: 0,
                PreviewText: string.Empty,
                ConfirmationMessage: null);
        }

        // Count what the delete will actually remove: every chart file below each
        // target, not only the charts indexed directly in it. Charts in a nested
        // folder that belong to no selected card mean the target also holds
        // other song folders, which this card-level delete must not take.
        var ownedCharts = selected
            .SelectMany(folder => folder.Charts ?? [])
            .Select(chart => TryNormalizePath(chart?.ChartPath, out var path) ? path : null)
            .Where(path => path != null)
            .Cast<string>()
            .ToHashSet(PathComparer);
        var chartCount = 0;
        foreach (var target in targets)
        {
            if (!TryCountChartsForDeletion(target.FullPath, ownedCharts, out var count))
                return Blocked(
                    DesktopLibraryDeletionBlockReason.ContainsOtherChartFolders,
                    ContainsOtherChartFoldersMessage);
            chartCount += count;
        }
        var previewItems = targets
            .Take(MaximumConfirmationPreviewItems)
            .Select(target => $"• {target.Folder.DisplayTitle}  ({target.Folder.RelativeFolderPath})")
            .ToArray();
        var omittedPreviewCount = Math.Max(0, targets.Count - previewItems.Length);
        var previewText = string.Join("\n", previewItems);
        if (omittedPreviewCount > 0)
            previewText += LocalizationCatalog.Format("\n…以及其他 {0} 个文件夹", omittedPreviewCount);

        var confirmationMessage = permanent
            ? LocalizationCatalog.Format("确定永久删除 {0} 个歌曲文件夹吗？\n其中包含 {1} 张谱面，文件夹内的音频和图片也会一起删除。\n删除后无法恢复。\n\n{2}", targets.Count, chartCount, previewText)
            : LocalizationCatalog.Format("确定将 {0} 个歌曲文件夹移入回收站吗？\n其中包含 {1} 张谱面，并会一起移除文件夹内的音频和图片。\n\n{2}", targets.Count, chartCount, previewText);
        return new DesktopLibraryDeletionPlan(
            CanExecute: true,
            BlockReason: DesktopLibraryDeletionBlockReason.None,
            BlockMessage: null,
            Targets: targets,
            BlockingDocumentTitles: [],
            BlockingDocumentCount: 0,
            ChartCount: chartCount,
            PreviewItems: previewItems,
            OmittedPreviewCount: omittedPreviewCount,
            PreviewText: previewText,
            ConfirmationMessage: confirmationMessage,
            PermanentDeletion: permanent);
    }

    /// <summary>
    /// True for a volume or share root, and for any folder that is, or contains,
    /// the user profile, a known shell folder, a system folder or the
    /// application's own folder.
    /// </summary>
    public static bool IsProtectedFolder(string path)
    {
        if (!TryNormalizePath(path, out var normalized)) return true;
        try
        {
            var root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root) ||
                PathComparer.Equals(
                    Path.TrimEndingDirectorySeparator(root),
                    Path.TrimEndingDirectorySeparator(normalized)))
                return true;
        }
        catch
        {
            return true;
        }
        return EnumerateProtectedFolders().Any(folder => IsSameOrDescendant(folder, normalized));
    }

    private static IEnumerable<string> EnumerateProtectedFolders()
    {
        foreach (var folder in ProtectedSpecialFolders)
        {
            string path;
            try
            {
                path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            }
            catch
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }

        var profile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            // Known folders without a SpecialFolder value.
            foreach (var name in new[]
                     {
                         "Downloads", "OneDrive", "Saved Games", "Contacts", "Links",
                         "Searches", "3D Objects", "AppData"
                     })
                yield return Path.Combine(profile, name);
        }

        var temporary = Path.GetTempPath();
        if (!string.IsNullOrWhiteSpace(temporary)) yield return temporary;
        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory)) yield return AppContext.BaseDirectory;
    }

    private static readonly Environment.SpecialFolder[] ProtectedSpecialFolders =
    [
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.Desktop,
        Environment.SpecialFolder.DesktopDirectory,
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.MyMusic,
        Environment.SpecialFolder.MyPictures,
        Environment.SpecialFolder.MyVideos,
        Environment.SpecialFolder.Favorites,
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.CommonDesktopDirectory,
        Environment.SpecialFolder.CommonDocuments,
        Environment.SpecialFolder.CommonMusic,
        Environment.SpecialFolder.CommonPictures,
        Environment.SpecialFolder.CommonVideos,
        Environment.SpecialFolder.StartMenu,
        Environment.SpecialFolder.Templates,
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles,
        Environment.SpecialFolder.CommonProgramFilesX86
    ];

    /// <summary>
    /// Counts the distinct chart files a folder delete removes (on disk and in
    /// the selected cards). Returns false when a nested folder holds a chart
    /// that no selected card owns. Reparse points are not followed; the
    /// recycle adapter refuses them separately.
    /// </summary>
    private static bool TryCountChartsForDeletion(
        string target,
        IReadOnlySet<string> ownedCharts,
        out int chartCount)
    {
        var charts = ownedCharts
            .Where(path => IsSameOrDescendant(path, target))
            .ToHashSet(PathComparer);
        chartCount = charts.Count;
        if (!Directory.Exists(target)) return true;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        try
        {
            foreach (var file in Directory.EnumerateFiles(target, "*", options))
            {
                if (!IsChartFile(file) || !TryNormalizePath(file, out var chart)) continue;
                var parent = Path.GetDirectoryName(chart);
                var direct = parent != null && PathComparer.Equals(
                    Path.TrimEndingDirectorySeparator(parent),
                    target);
                if (!direct && !ownedCharts.Contains(chart)) return false;
                charts.Add(chart);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable tree cannot be proven free of other song folders.
            return false;
        }
        chartCount = charts.Count;
        return true;
    }

    private static bool IsChartFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".osu", StringComparison.OrdinalIgnoreCase);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static DesktopLibraryDeletionPlan Blocked(
        DesktopLibraryDeletionBlockReason reason,
        string? blockMessage) =>
        new(
            CanExecute: false,
            BlockReason: reason,
            BlockMessage: blockMessage,
            Targets: [],
            BlockingDocumentTitles: [],
            BlockingDocumentCount: 0,
            ChartCount: 0,
            PreviewItems: [],
            OmittedPreviewCount: 0,
            PreviewText: string.Empty,
            ConfirmationMessage: null);

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return normalized.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsStrictDescendant(string root, string candidate)
    {
        try
        {
            var relative = Path.GetRelativePath(root, candidate);
            return relative != "." && IsRelativeDescendant(relative);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        if (!TryNormalizePath(candidate, out var normalizedCandidate) ||
            !TryNormalizePath(parent, out var normalizedParent))
            return false;
        try
        {
            var relative = Path.GetRelativePath(normalizedParent, normalizedCandidate);
            return relative == "." || IsRelativeDescendant(relative);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRelativeDescendant(string relative) =>
        !Path.IsPathRooted(relative) &&
        relative != ".." &&
        !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
}

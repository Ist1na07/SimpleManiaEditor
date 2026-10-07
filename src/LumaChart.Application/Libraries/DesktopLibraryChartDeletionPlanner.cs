using LumaChart.Core.Localization;

namespace LumaChart.Application.Libraries;

public enum DesktopLibraryChartDeletionBlockReason
{
    None,
    InvalidChartPath,
    UnsafeWorkspaceBoundary,
    OpenDocument
}

public sealed record DesktopLibraryChartDeletionPlan(
    bool CanExecute,
    DesktopLibraryChartDeletionBlockReason BlockReason,
    string? BlockMessage,
    DesktopLibraryChartEntry? Chart,
    string? FullPath,
    string? ConfirmationMessage,
    bool PermanentDeletion = false);

/// <summary>
/// Builds the UI-free, fail-closed single-chart deletion plan used by the
/// Windows chart library. This intentionally matches WPF 1.1.0: only the
/// chart file is recycled, paths outside the active workspace are rejected,
/// and any matching open document blocks the operation.
/// </summary>
public static class DesktopLibraryChartDeletionPlanner
{
    public const string InvalidChartPathMessage = "谱面路径无效。";
    public const string UnsafeWorkspaceBoundaryMessage =
        "不能删除工作目录之外的谱面。";
    public const string OpenDocumentMessage =
        "这张谱面正在编辑。请先关闭对应谱面标签，再执行删除。";
    public const string RecycleChoiceLabel = DesktopLibraryDeletionPlanner.RecycleChoiceLabel;
    public const string PermanentDeleteChoiceLabel = DesktopLibraryDeletionPlanner.PermanentDeleteChoiceLabel;

    /// <param name="permanentDeletion">
    /// True when the file is deleted outright instead of recycled; defaults to
    /// <see cref="DesktopLibraryDeletionPlanner.DefaultPermanentDeletion"/>.
    /// </param>
    public static DesktopLibraryChartDeletionPlan Create(
        string workspaceRoot,
        DesktopLibraryChartEntry? chart,
        IEnumerable<DesktopLibraryOpenDocumentReference>? openDocuments = null,
        bool? permanentDeletion = null)
    {
        var permanent = permanentDeletion ?? DesktopLibraryDeletionPlanner.DefaultPermanentDeletion;
        if (chart == null || !TryNormalizePath(chart.ChartPath, out var chartPath))
            return Blocked(
                DesktopLibraryChartDeletionBlockReason.InvalidChartPath,
                InvalidChartPathMessage,
                chart);

        if (!TryNormalizePath(workspaceRoot, out var workspacePath) ||
            !IsSameOrDescendant(chartPath, workspacePath))
            return Blocked(
                DesktopLibraryChartDeletionBlockReason.UnsafeWorkspaceBoundary,
                UnsafeWorkspaceBoundaryMessage,
                chart);

        var isOpen = (openDocuments ?? [])
            .Where(document => document != null)
            .SelectMany(document => document.Paths ?? [])
            .Any(path => PathsEqual(path, chartPath));
        if (isOpen)
            return Blocked(
                DesktopLibraryChartDeletionBlockReason.OpenDocument,
                OpenDocumentMessage,
                chart,
                chartPath);

        var confirmation = permanent
            ? LocalizationCatalog.Format("确定永久删除这张谱面吗？删除后无法恢复。\n\n{0}\n{1}\n\n只删除谱面文件，不影响同目录的共用音频和图片。", chart.PickerTitle, chart.RelativePath)
            : LocalizationCatalog.Format("确定将这张谱面移入回收站吗？\n\n{0}\n{1}\n\n只移除谱面文件，不影响同目录的共用音频和图片。", chart.PickerTitle, chart.RelativePath);
        return new DesktopLibraryChartDeletionPlan(
            CanExecute: true,
            BlockReason: DesktopLibraryChartDeletionBlockReason.None,
            BlockMessage: null,
            Chart: chart,
            FullPath: chartPath,
            ConfirmationMessage: confirmation,
            PermanentDeletion: permanent);
    }

    private static DesktopLibraryChartDeletionPlan Blocked(
        DesktopLibraryChartDeletionBlockReason reason,
        string message,
        DesktopLibraryChartEntry? chart,
        string? fullPath = null) =>
        new(
            CanExecute: false,
            BlockReason: reason,
            BlockMessage: message,
            Chart: chart,
            FullPath: fullPath,
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

    private static bool PathsEqual(string? left, string right) =>
        TryNormalizePath(left, out var normalizedLeft) &&
        string.Equals(normalizedLeft, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        try
        {
            var relative = Path.GetRelativePath(parent, candidate);
            return relative == "." ||
                   (!Path.IsPathRooted(relative) &&
                    relative != ".." &&
                    !relative.StartsWith(
                        ".." + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal) &&
                    !relative.StartsWith(
                        ".." + Path.AltDirectorySeparatorChar,
                        StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }
}

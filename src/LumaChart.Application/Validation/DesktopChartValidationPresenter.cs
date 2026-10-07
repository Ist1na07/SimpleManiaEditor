using LumaChart.Core.Localization;
using LumaChart.Core.Models;
using LumaChart.Core.Validation;

namespace LumaChart.Application.Validation;

/// <summary>
/// UI-independent instructions for reproducing WPF's first-issue navigation.
/// ExactBeat deliberately retains the validator's authored rational expression;
/// callers should only convert it to a double at the final timeline boundary.
/// </summary>
public sealed record DesktopChartIssueNavigationPlan(
    BeatPosition ExactBeat,
    IReadOnlyList<Guid> NoteIds,
    IReadOnlyList<int> HiddenGroupsToReveal,
    int InspectorTabIndex,
    string HoverStatusText);

/// <summary>
/// Builds the selection, hidden-group, inspector, and status changes that WPF's
/// LocateChartIssue method applies. This class intentionally does not detect or
/// rank issues; that remains the responsibility of <see cref="ChartValidator"/>.
/// </summary>
public static class DesktopChartIssueNavigationPlanner
{
    public const int WpfChartInspectorTabIndex = 2;

    public static DesktopChartIssueNavigationPlan Create(
        ChartDocument document,
        ChartIssue issue,
        IEnumerable<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(issue);

        var noteIds = issue.NoteIds.Distinct().ToArray();
        var noteIdSet = noteIds.ToHashSet();
        var issueGroups = document.Notes
            .Where(note => noteIdSet.Contains(note.Id))
            .Select(note => note.Group)
            .Distinct()
            .ToArray();
        var hiddenGroupSet = hiddenGroups?.ToHashSet();
        var hiddenGroupsToReveal = hiddenGroupSet == null
            ? issueGroups
            : issueGroups.Where(hiddenGroupSet.Contains).ToArray();

        return new DesktopChartIssueNavigationPlan(
            issue.Beat,
            noteIds,
            hiddenGroupsToReveal,
            WpfChartInspectorTabIndex,
            LocalizationCatalog.Format("谱面错误：{0} · {1}", issue.Message, issue.Beat));
    }
}

/// <summary>
/// The complete WPF-compatible presentation of one structural validation pass.
/// Both manual-check and save-blocking copy are produced from the same ordered
/// Core validator result so a UI cannot accidentally show different counts or
/// navigate to a different first issue.
/// </summary>
public sealed record DesktopChartValidationPresentation(
    IReadOnlyList<ChartIssue> Issues,
    int OverlappingNoteCount,
    int CrossingLongNoteCount,
    ChartIssue? FirstIssue,
    string ManualCheckMessage,
    string? SaveBlockingMessage,
    DesktopChartIssueNavigationPlan? Navigation)
{
    public int TotalIssueCount => Issues.Count;
    public bool CanSave => Issues.Count == 0;
}

public static class DesktopChartValidationPresenter
{
    public const string DialogTitle = "谱面检查器";
    public const string ManualCheckSuccessMessage = "未发现重叠键或 LN 交叉。";

    public static DesktopChartValidationPresentation Create(
        ChartDocument document,
        IEnumerable<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var issues = ChartValidator.Validate(document).ToArray();
        var overlappingNoteCount = issues.Count(issue =>
            issue.Kind == ChartIssueKind.OverlappingNotes);
        var crossingLongNoteCount = issues.Length - overlappingNoteCount;
        var firstIssue = issues.FirstOrDefault();
        if (firstIssue == null)
        {
            return new DesktopChartValidationPresentation(
                issues,
                overlappingNoteCount,
                crossingLongNoteCount,
                null,
                ManualCheckSuccessMessage,
                null,
                null);
        }

        var manualCheckMessage =
            LocalizationCatalog.Format("发现 {0} 项错误：重叠键 {1}，LN 交叉 {2}。\n\n已定位最靠下的一项：{3}（{4}）。", issues.Length, overlappingNoteCount, crossingLongNoteCount, firstIssue.Message, firstIssue.Beat);
        var saveBlockingMessage =
            LocalizationCatalog.Format("保存已暂停：发现 {0} 项谱面错误。\n\n重叠键：{1}\nLN 交叉：{2}\n\n已定位并选中最靠下的一项，请修复后再次保存。", issues.Length, overlappingNoteCount, crossingLongNoteCount);

        return new DesktopChartValidationPresentation(
            issues,
            overlappingNoteCount,
            crossingLongNoteCount,
            firstIssue,
            manualCheckMessage,
            saveBlockingMessage,
            DesktopChartIssueNavigationPlanner.Create(document, firstIssue, hiddenGroups));
    }
}

using LumaChart.Core.Localization;
using LumaChart.Core.Models;

namespace LumaChart.Core.Validation;

public enum ChartIssueKind
{
    OverlappingNotes,
    CrossingLongNotes
}

public sealed record ChartIssue(
    ChartIssueKind Kind,
    BeatPosition Beat,
    int Lane,
    IReadOnlyList<Guid> NoteIds,
    string Message);

/// <summary>
/// Checks structural errors that make a fixed-lane mania chart ambiguous to play.
/// Draft notes are intentionally excluded because they are not exported as playable notes.
/// </summary>
public static class ChartValidator
{
    private const double Epsilon = 1e-7;

    public static IReadOnlyList<ChartIssue> Validate(ChartDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var issues = new List<ChartIssue>();

        foreach (var laneGroup in document.Notes.Where(note => !note.IsDraft)
                     .GroupBy(note => note.Lane).OrderBy(group => group.Key))
        {
            var notes = laneGroup.OrderBy(note => note.Beat.Value)
                .ThenBy(note => note.EndBeat?.Value ?? note.Beat.Value).ToArray();
            for (var leftIndex = 0; leftIndex < notes.Length; leftIndex++)
            {
                var left = notes[leftIndex];
                var leftEnd = left.EndBeat?.Value ?? left.Beat.Value;
                for (var rightIndex = leftIndex + 1; rightIndex < notes.Length; rightIndex++)
                {
                    var right = notes[rightIndex];
                    if (right.Beat.Value > leftEnd + Epsilon &&
                        right.Beat.Value > left.Beat.Value + Epsilon) break;

                    var rightEnd = right.EndBeat?.Value ?? right.Beat.Value;
                    var sameStart = Math.Abs(left.Beat.Value - right.Beat.Value) <= Epsilon;
                    var tapInsideHold = (!left.IsHold && right.IsHold &&
                                         left.Beat.Value > right.Beat.Value + Epsilon && left.Beat.Value < rightEnd - Epsilon) ||
                                        (!right.IsHold && left.IsHold &&
                                         right.Beat.Value > left.Beat.Value + Epsilon && right.Beat.Value < leftEnd - Epsilon);
                    var holdOverlap = left.IsHold && right.IsHold &&
                                      right.Beat.Value < leftEnd - Epsilon && left.Beat.Value < rightEnd - Epsilon;

                    if (left.IsHold && right.IsHold && holdOverlap && !sameStart &&
                        right.Beat.Value < leftEnd - Epsilon && leftEnd < rightEnd - Epsilon)
                    {
                        issues.Add(new ChartIssue(ChartIssueKind.CrossingLongNotes,
                            right.Beat, laneGroup.Key, [left.Id, right.Id],
                            LocalizationCatalog.Format("第 {0} 轨的两个 LN 发生交叉", laneGroup.Key + 1)));
                    }
                    else if (sameStart || tapInsideHold || holdOverlap)
                    {
                        var beat = left.Beat.Value >= right.Beat.Value ? left.Beat : right.Beat;
                        issues.Add(new ChartIssue(ChartIssueKind.OverlappingNotes,
                            beat, laneGroup.Key, [left.Id, right.Id],
                            LocalizationCatalog.Format("第 {0} 轨存在重叠 Note", laneGroup.Key + 1)));
                    }
                }
            }
        }

        return issues.OrderBy(issue => issue.Beat.Value).ThenBy(issue => issue.Lane).ToArray();
    }
}

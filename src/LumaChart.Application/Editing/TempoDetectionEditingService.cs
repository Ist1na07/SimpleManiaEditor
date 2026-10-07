using LumaChart.Application.Audio;
using LumaChart.Core.Localization;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Editing;

public enum TempoDetectionApplyStatus
{
    Applied,
    Unchanged,
    Denied,
    Stale
}

public sealed record TempoDetectionApplyResult(
    TempoDetectionApplyStatus Status,
    string Message,
    int CandidateCount = 0,
    int ReplacedBpmCount = 0,
    bool MetadataBpmUpdated = false,
    bool ScrollCompensationApplied = false,
    string? PermissionCategory = null);

public sealed record TempoTimelinePlan(
    IReadOnlyList<TempoTimingCandidate> Candidates,
    int ExistingBpmCount,
    int ManagedScrollCount,
    double ReferenceBpm);

/// <summary>
/// Shared permission, stale-result and atomic-mutation boundary for single and
/// variable BPM analysis. UI hosts only present candidates and user choices.
/// </summary>
public static class TempoDetectionEditingService
{
    public static TempoTimelinePlan CreateTimelinePlan(
        ChartDocument document,
        VariableTempoEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(estimate);
        var candidates = VariableTempoEstimator.MapToTiming(estimate).ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("没有生成可写入的 BPM 候选。");
        var managedBeats = document.Timing
            .Where(timing => timing.Bpm.HasValue)
            .Select(timing => timing.Beat)
            .Concat(candidates.Select(candidate => candidate.Beat))
            .ToArray();
        var managedScrollCount = document.Timing.Count(timing =>
            timing.Effect == TimingEffectKind.Scroll &&
            managedBeats.Any(beat => beat.CompareTo(timing.Beat) == 0));
        return new TempoTimelinePlan(
            candidates,
            document.Timing.Count(timing => timing.Bpm.HasValue),
            managedScrollCount,
            document.Metadata.Bpm);
    }

    public static TempoDetectionApplyResult ApplySingle(
        EditorSession session,
        BeatAnalysisResult result,
        long expectedChangeSerial)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateResult(result);
        if (session.ChangeSerial != expectedChangeSerial)
            return Stale("确认期间谱面已发生变化，单 BPM 检测结果已取消；请重新检测。");
        if (session.IsReadOnly)
            return Denied("观察者不能写入 BPM 或 Delay。", "观察者只读");
        if (!session.CanEditTiming)
            return Denied("BPM 与 Delay 属于 Timing，需要 Effect 权限。", "Effect");
        if (!session.AllowOwnerProtectedEdits && session.IsBeatOwnerProtected(BeatPosition.Zero))
            return Denied("第一个 Timing 位于房主锁定区域，检测结果未写入。", "房主锁定区域");

        var document = session.Document;
        // Same record TimingMap plays and the Offset field edits.
        var first = FindZeroBeatTiming(document);
        var updateMetadata = session.CanEditMetadata;
        var timingChanged = first == null ||
                            !first.Bpm!.Value.Equals(result.Bpm) ||
                            !first.Delay.Equals(result.OffsetMilliseconds);
        var metadataChanged = updateMetadata && !document.Metadata.Bpm.Equals(result.Bpm);
        if (!timingChanged && !metadataChanged)
            return new TempoDetectionApplyResult(
                TempoDetectionApplyStatus.Unchanged,
                "检测结果与当前第一个 Timing 相同。",
                CandidateCount: 1);

        var serial = session.ChangeSerial;
        session.Mutate(next =>
        {
            var target = FindZeroBeatTiming(next);
            if (target == null)
            {
                target = new TimingEvent { Beat = BeatPosition.Zero };
                next.Timing.Add(target);
            }
            target.Bpm = result.Bpm;
            target.Delay = result.OffsetMilliseconds;
            if (updateMetadata) next.Metadata.Bpm = result.Bpm;
        });
        if (session.ChangeSerial == serial)
            return Denied("当前细分权限不允许写入检测结果。", null);
        return new TempoDetectionApplyResult(
            TempoDetectionApplyStatus.Applied,
            updateMetadata
                ? "检测结果已写入第一个 Timing，并同步更新谱面信息 BPM。"
                : "检测结果已写入第一个 Timing；谱面信息 BPM 因权限不足未更新。",
            CandidateCount: 1,
            MetadataBpmUpdated: updateMetadata);
    }

    public static TempoDetectionApplyResult ApplyTimeline(
        EditorSession session,
        TempoTimelinePlan plan,
        double initialOffsetMilliseconds,
        bool applyScrollCompensation,
        long expectedChangeSerial)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Candidates.Count == 0)
            throw new ArgumentException("The timeline plan has no candidates.", nameof(plan));
        if (!double.IsFinite(initialOffsetMilliseconds))
            throw new ArgumentOutOfRangeException(nameof(initialOffsetMilliseconds));
        if (session.ChangeSerial != expectedChangeSerial)
            return Stale("确认期间谱面已发生变化。为避免覆盖刚同步或刚编辑的 Timing，本次预测结果已取消；请重新检测。");
        if (session.IsReadOnly)
            return Denied("观察者不能替换 BPM 时间线。", "观察者只读");
        if (!session.CanEditTiming)
            return Denied("替换 BPM 时间线需要 Effect 权限。", "Effect");

        var managedBeats = session.Document.Timing
            .Where(timing => timing.Bpm.HasValue)
            .Select(timing => timing.Beat)
            .Concat(plan.Candidates.Select(candidate => candidate.Beat))
            .ToArray();
        if (!session.AllowOwnerProtectedEdits && managedBeats.Any(session.IsBeatOwnerProtected))
            return Denied(
                "旧 BPM 或新候选 BPM 的位置命中房主锁定区域；整体替换已取消。",
                "房主锁定区域");
        if (applyScrollCompensation &&
            !session.Document.Metadata.Bpm.Equals(plan.ReferenceBpm))
            return Stale("确认期间谱面信息 BPM 已变化，流速补正结果已取消；请重新检测。");

        var serial = session.ChangeSerial;
        var replaced = 0;
        session.Mutate(document => replaced = applyScrollCompensation
            ? TempoTimelineApplicator.ReplaceBpmEvents(
                document,
                plan.Candidates,
                initialOffsetMilliseconds,
                plan.ReferenceBpm)
            : TempoTimelineApplicator.ReplaceBpmEvents(
                document,
                plan.Candidates,
                initialOffsetMilliseconds));
        if (session.ChangeSerial == serial)
            return Denied("当前细分权限不允许写入预测结果。", null);

        return new TempoDetectionApplyResult(
            TempoDetectionApplyStatus.Applied,
            applyScrollCompensation
                ? LocalizationCatalog.Format("已用 {0} 个预测 BPM 段替换 {1} 个旧 BPM 记录，并按基准 {2:0.###} 生成 Scroll 流速补正。", plan.Candidates.Count, replaced, plan.ReferenceBpm)
                : LocalizationCatalog.Format("已用 {0} 个预测 BPM 段替换 {1} 个旧 BPM 记录；Effect 已保留。", plan.Candidates.Count, replaced),
            plan.Candidates.Count,
            replaced,
            ScrollCompensationApplied: applyScrollCompensation);
    }

    private static TimingEvent? FindZeroBeatTiming(ChartDocument document)
    {
        var timing = TimingMap.FindSongOffsetTiming(document.Timing);
        return timing != null && timing.Beat.CompareTo(BeatPosition.Zero) == 0 ? timing : null;
    }

    private static void ValidateResult(BeatAnalysisResult result)
    {
        if (!double.IsFinite(result.Bpm) || result.Bpm is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(result), "Detected BPM is invalid.");
        if (!double.IsFinite(result.OffsetMilliseconds) ||
            result.OffsetMilliseconds is < -600_000 or > 600_000)
            throw new ArgumentOutOfRangeException(nameof(result), "Detected offset is invalid.");
    }

    private static TempoDetectionApplyResult Denied(string message, string? category) =>
        new(TempoDetectionApplyStatus.Denied, message, PermissionCategory: category);

    private static TempoDetectionApplyResult Stale(string message) =>
        new(TempoDetectionApplyStatus.Stale, message);
}

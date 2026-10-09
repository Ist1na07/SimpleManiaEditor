using System.Globalization;
using System.Text.Json.Nodes;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Editing;

public sealed record OsuTimingEdit(double Milliseconds, bool IsBpm, double Value, OsuTimingPointSettings Settings);

/// <summary>An absolute millisecond timing command. Changing timing preserves existing osu object times.</summary>
public static class OsuTimingEditingService
{
    public static Guid Apply(EditorSession session, Guid? id, OsuTimingEdit edit)
    {
        if (!OsuSourceEnvelope.HasSource(session.Document)) throw new InvalidOperationException("请在 osu! 谱面中使用原生 Timing 编辑。");
        if (session.IsReadOnly || !session.CanEditTiming) throw new InvalidOperationException("当前谱面不允许编辑 Timing。");
        if (!double.IsFinite(edit.Milliseconds) || Math.Abs(edit.Milliseconds) > 1_000_000_000 ||
            !double.IsFinite(edit.Value) || edit.Value <= 0 || (edit.IsBpm && edit.Value > ChartDocument.MaximumTimingBpm))
            throw new ArgumentOutOfRangeException(nameof(edit), "时间及 BPM / SV 必须是有效数字，BPM / SV 必须为正。");
        var milliseconds = Math.Round(edit.Milliseconds, MidpointRounding.AwayFromZero);
        var existing = id.HasValue ? session.Document.Timing.FirstOrDefault(t => t.Id == id) : null;
        if (id.HasValue && existing == null) throw new InvalidOperationException("所选事件已经不存在。");
        var map = new TimingMap(session.Document.Timing);
        var beat = map.MillisecondsToBeat(milliseconds, BeatPosition.MaximumPreservedDenominator);
        if (!session.AllowOwnerProtectedEdits &&
            (session.IntersectsOwnerProtectedRegion(beat, beat) || existing != null && session.IntersectsOwnerProtectedRegion(existing.Beat, existing.Beat)))
            throw new InvalidOperationException("该 Timing 位于锁定区域。");
        var replacement = new TimingEvent { Id = existing?.Id ?? Guid.NewGuid(), Beat = beat,
            Bpm = edit.IsBpm ? edit.Value : null, Effect = edit.IsBpm ? null : TimingEffectKind.Scroll,
            EffectValue = edit.IsBpm ? 1 : edit.Value, EffectValueText = (edit.IsBpm ? 1 : edit.Value).ToString("R", CultureInfo.InvariantCulture),
            UnknownData = existing?.UnknownData?.DeepClone().AsObject() ?? new(), OnlineCreatorUserId = existing?.OnlineCreatorUserId ?? 0 };
        edit.Settings.Apply(replacement);
        // ReanchorTiming consumes this timestamp before rebuilding the native timing map.
        replacement.UnknownData["_sme_requested_timing_ms"] = milliseconds;
        var serial = session.ChangeSerial;
        session.Mutate(document =>
        {
            if (existing != null) document.Timing.RemoveAll(t => t.Id == existing.Id);
            document.Timing.Add(replacement);
        });
        return session.ChangeSerial != serial ? replacement.Id : Guid.Empty;
    }
}

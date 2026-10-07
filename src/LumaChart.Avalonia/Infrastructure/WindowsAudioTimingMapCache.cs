using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Retains the timing map while editor changes do not affect beat/audio
/// conversion. Note, metadata and non-BPM effect edits must not invalidate the
/// comparatively expensive WAVE/SPEC scenes that reference this map.
/// </summary>
public sealed class WindowsAudioTimingMapCache
{
    private readonly record struct PointRevision(
        BeatPosition Beat,
        long BpmBits,
        long DelayBits);

    private TimingMap? timingMap;
    private Guid? tabId;
    private ChartDocument? document;
    private long observedChangeSerial = -1;
    private PointRevision[] revision = [];

    public int RebuildCount { get; private set; }

    public TimingMap Get(
        Guid requestedTabId,
        ChartDocument requestedDocument,
        long changeSerial)
    {
        ArgumentNullException.ThrowIfNull(requestedDocument);

        if (timingMap == null || tabId != requestedTabId)
        {
            revision = CaptureRevision(requestedDocument);
            timingMap = BuildTimingMap(requestedDocument);
        }
        else if (observedChangeSerial != changeSerial ||
                 !ReferenceEquals(document, requestedDocument))
        {
            var nextRevision = CaptureRevision(requestedDocument);
            if (!revision.AsSpan().SequenceEqual(nextRevision))
                timingMap = BuildTimingMap(requestedDocument);
            revision = nextRevision;
        }

        tabId = requestedTabId;
        document = requestedDocument;
        observedChangeSerial = changeSerial;
        return timingMap;
    }

    public bool TryGet(
        Guid requestedTabId,
        ChartDocument requestedDocument,
        long changeSerial,
        out TimingMap result)
    {
        if (timingMap == null || tabId != requestedTabId)
        {
            result = null!;
            return false;
        }

        result = Get(requestedTabId, requestedDocument, changeSerial);
        return true;
    }

    public void Clear()
    {
        timingMap = null;
        tabId = null;
        document = null;
        observedChangeSerial = -1;
        revision = [];
    }

    private TimingMap BuildTimingMap(ChartDocument requestedDocument)
    {
        RebuildCount++;
        return new TimingMap(requestedDocument.Timing);
    }

    private static PointRevision[] CaptureRevision(ChartDocument document)
    {
        var ordered = document.Timing
            .Select((item, index) => (Item: item, Index: index))
            .Where(entry =>
                entry.Item.Bpm is > 0 &&
                double.IsFinite(entry.Item.Bpm.Value))
            .OrderBy(entry => entry.Item.Beat)
            .ThenBy(entry => entry.Index);
        var points = new List<PointRevision>();
        foreach (var entry in ordered)
        {
            var point = new PointRevision(
                entry.Item.Beat,
                BitConverter.DoubleToInt64Bits(entry.Item.Bpm!.Value),
                BitConverter.DoubleToInt64Bits(
                    double.IsFinite(entry.Item.Delay) ? entry.Item.Delay : 0));
            if (points.Count > 0 &&
                points[^1].Beat.CompareTo(point.Beat) == 0)
                points[^1] = point;
            else
                points.Add(point);
        }
        return [.. points];
    }
}

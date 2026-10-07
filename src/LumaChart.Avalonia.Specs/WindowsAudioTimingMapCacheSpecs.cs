using LumaChart.Avalonia.Infrastructure;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsAudioTimingMapCacheSpecs
{
    public static IReadOnlyList<WindowsAudioTimingMapCacheSpecResult> Run()
    {
        return
        [
            VerifyNoteAndMetadataEditsRetainMap(),
            VerifyReplacementDocumentWithSameTimingRetainsMap(),
            VerifyBpmAndDelayEditsRebuildMap(),
            VerifyTabChangeRebuildsMap()
        ];
    }

    private static WindowsAudioTimingMapCacheSpecResult
        VerifyNoteAndMetadataEditsRetainMap()
    {
        var cache = new WindowsAudioTimingMapCache();
        var tabId = Guid.NewGuid();
        var document = CreateDocument();
        var initial = cache.Get(tabId, document, 0);
        document.Notes.Add(new ChartNote
        {
            Lane = 2,
            Beat = BeatPosition.FromDouble(4)
        });
        var afterNote = cache.Get(tabId, document, 1);
        document.Metadata.Title = "Note-only edit";
        document.Timing.Add(new TimingEvent
        {
            Beat = BeatPosition.FromDouble(8),
            Effect = TimingEffectKind.Scroll,
            EffectValue = 1.5
        });
        var afterMetadataAndScroll = cache.Get(tabId, document, 2);
        var passed =
            ReferenceEquals(initial, afterNote) &&
            ReferenceEquals(initial, afterMetadataAndScroll) &&
            cache.RebuildCount == 1;
        return Result(
            "windows-audio-timing-map-note-edit-retains-scene-key",
            passed,
            $"sameNote={ReferenceEquals(initial, afterNote)}; " +
            $"sameMetadata={ReferenceEquals(initial, afterMetadataAndScroll)}; " +
            $"builds={cache.RebuildCount}");
    }

    private static WindowsAudioTimingMapCacheSpecResult
        VerifyReplacementDocumentWithSameTimingRetainsMap()
    {
        var cache = new WindowsAudioTimingMapCache();
        var tabId = Guid.NewGuid();
        var document = CreateDocument();
        var initial = cache.Get(tabId, document, 4);
        var replacement = document.Clone();
        replacement.Notes.Add(new ChartNote
        {
            Lane = 1,
            Beat = BeatPosition.FromDouble(2)
        });
        var afterUndoStyleReplacement = cache.Get(tabId, replacement, 5);
        var passed =
            ReferenceEquals(initial, afterUndoStyleReplacement) &&
            cache.RebuildCount == 1;
        return Result(
            "windows-audio-timing-map-note-undo-retains-scene-key",
            passed,
            $"same={ReferenceEquals(initial, afterUndoStyleReplacement)}; " +
            $"builds={cache.RebuildCount}");
    }

    private static WindowsAudioTimingMapCacheSpecResult
        VerifyBpmAndDelayEditsRebuildMap()
    {
        var cache = new WindowsAudioTimingMapCache();
        var tabId = Guid.NewGuid();
        var document = CreateDocument();
        var initial = cache.Get(tabId, document, 0);
        document.Timing[0].Bpm = 180;
        var afterBpm = cache.Get(tabId, document, 1);
        document.Timing[0].Delay = 32;
        var afterDelay = cache.Get(tabId, document, 2);
        var passed =
            !ReferenceEquals(initial, afterBpm) &&
            !ReferenceEquals(afterBpm, afterDelay) &&
            cache.RebuildCount == 3 &&
            Math.Abs(afterDelay.BpmAt(BeatPosition.Zero) - 180) < .001;
        return Result(
            "windows-audio-timing-map-bpm-delay-edit-rebuilds",
            passed,
            $"bpmChanged={!ReferenceEquals(initial, afterBpm)}; " +
            $"delayChanged={!ReferenceEquals(afterBpm, afterDelay)}; " +
            $"builds={cache.RebuildCount}");
    }

    private static WindowsAudioTimingMapCacheSpecResult VerifyTabChangeRebuildsMap()
    {
        var cache = new WindowsAudioTimingMapCache();
        var document = CreateDocument();
        var first = cache.Get(Guid.NewGuid(), document, 0);
        var secondTab = Guid.NewGuid();
        var second = cache.Get(secondTab, document, 0);
        var found = cache.TryGet(secondTab, document, 0, out var cached);
        cache.Clear();
        var cleared = !cache.TryGet(secondTab, document, 0, out _);
        var passed =
            !ReferenceEquals(first, second) &&
            found &&
            ReferenceEquals(second, cached) &&
            cleared &&
            cache.RebuildCount == 2;
        return Result(
            "windows-audio-timing-map-tab-scope-and-clear",
            passed,
            $"tabChanged={!ReferenceEquals(first, second)}; found={found}; " +
            $"cleared={cleared}; builds={cache.RebuildCount}");
    }

    private static ChartDocument CreateDocument() => new()
    {
        Timing =
        [
            new TimingEvent
            {
                Beat = BeatPosition.Zero,
                Bpm = 120,
                Delay = 0
            }
        ]
    };

    private static WindowsAudioTimingMapCacheSpecResult Result(
        string name,
        bool passed,
        string detail) => new(name, passed, detail);
}

internal sealed record WindowsAudioTimingMapCacheSpecResult(
    string Name,
    bool Passed,
    string Detail);

using LumaChart.Application.Editing;
using LumaChart.Avalonia.Editing;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsTimingEffectMemorySpecs
{
    public static WindowsTimingEffectMemorySpecResult[] Run()
    {
        var memory = new WindowsTimingEffectMemory();
        var initial = memory.Current;
        memory.Remember(new CompleteTimingEdit(
            new BeatPosition(3, 10, 12),
            155,
            0,
            null,
            "9.0",
            "ignored"));
        var bpmOnly = memory.Current;
        memory.Remember(new CompleteTimingEdit(
            new BeatPosition(4, 0, 1),
            null,
            0,
            TimingEffectKind.Custom,
            " 2e-3 ",
            " lane.glow "));
        var custom = memory.Current;
        memory.Remember(new CompleteTimingEdit(
            new BeatPosition(5, 0, 1),
            null,
            0,
            TimingEffectKind.Scroll,
            "0.7500",
            "must-not-replace-custom"));
        var scroll = memory.Current;

        return
        [
            Check(
                WindowsTimingEffectMemory.Contract ==
                "wpf-1.1.0-last-effect-memory-r1" &&
                initial == new WindowsTimingEffectMemorySnapshot(
                    TimingEffectKind.Scroll,
                    "1",
                    "custom"),
                "timing-effect-memory-default-matches-wpf-110",
                $"contract={WindowsTimingEffectMemory.Contract}; initial={initial}"),
            Check(
                bpmOnly == initial,
                "timing-effect-memory-ignores-bpm-only-row",
                $"before={initial}; after={bpmOnly}"),
            Check(
                custom == new WindowsTimingEffectMemorySnapshot(
                    TimingEffectKind.Custom,
                    "2e-3",
                    "lane.glow"),
                "timing-effect-memory-preserves-custom-number-text-and-field",
                $"custom={custom}"),
            Check(
                scroll == new WindowsTimingEffectMemorySnapshot(
                    TimingEffectKind.Scroll,
                    "0.7500",
                    "lane.glow"),
                "timing-effect-memory-retains-last-custom-field-across-effects",
                $"scroll={scroll}")
        ];
    }

    private static WindowsTimingEffectMemorySpecResult Check(
        bool passed,
        string name,
        string detail) =>
        new(name, passed, detail);
}

internal sealed record WindowsTimingEffectMemorySpecResult(
    string Name,
    bool Passed,
    string Detail);

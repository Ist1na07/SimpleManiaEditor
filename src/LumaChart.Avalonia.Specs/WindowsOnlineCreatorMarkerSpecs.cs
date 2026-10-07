using LumaChart.Avalonia.Editing;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsOnlineCreatorMarkerSpecs
{
    public static WindowsOnlineCreatorMarkerSpecResult[] Run()
    {
        return
        [
            Check(
                WindowsOnlineCreatorMarkerPolicy.Contract ==
                "wpf-1.1.0-online-creator-marker-r1" &&
                WindowsOnlineCreatorMarkerPolicy.TimingMarkerSize == 12 &&
                WindowsOnlineCreatorMarkerPolicy.NoteMarkerSize(20) == 11.6 &&
                WindowsOnlineCreatorMarkerPolicy.NoteMarkerSize(8) == 11 &&
                WindowsOnlineCreatorMarkerPolicy.NoteMarkerSize(40) == 15,
                "online-creator-marker-geometry-matches-wpf-110",
                $"contract={WindowsOnlineCreatorMarkerPolicy.Contract}; " +
                $"timing={WindowsOnlineCreatorMarkerPolicy.TimingMarkerSize}; " +
                $"note20={WindowsOnlineCreatorMarkerPolicy.NoteMarkerSize(20):R}"),
            Check(
                WindowsOnlineCreatorMarkerPolicy.ShouldDraw(false, 7) &&
                !WindowsOnlineCreatorMarkerPolicy.ShouldDraw(true, 7) &&
                !WindowsOnlineCreatorMarkerPolicy.ShouldDraw(false, 0),
                "online-creator-marker-hidden-during-playback",
                "idle-user7=true; playback-user7=false; idle-unattributed=false"),
            Check(
                WindowsOnlineCreatorMarkerPolicy.ColorIndex(12, 12) == 0 &&
                WindowsOnlineCreatorMarkerPolicy.ColorIndex(13, 12) == 1 &&
                WindowsOnlineCreatorMarkerPolicy.ColorIndex(long.MaxValue, 12) == 7,
                "online-creator-marker-color-is-user-id-deterministic",
                $"12={WindowsOnlineCreatorMarkerPolicy.ColorIndex(12, 12)}; " +
                $"13={WindowsOnlineCreatorMarkerPolicy.ColorIndex(13, 12)}; " +
                $"max={WindowsOnlineCreatorMarkerPolicy.ColorIndex(long.MaxValue, 12)}"),
            Check(
                WindowsOnlineCreatorMarkerPolicy.FallbackInitial(" alice ") == "A" &&
                WindowsOnlineCreatorMarkerPolicy.FallbackInitial(" 中文 ") == "中" &&
                WindowsOnlineCreatorMarkerPolicy.FallbackInitial("  ") == string.Empty,
                "online-creator-marker-fallback-initial-matches-wpf",
                "alice=A; 中文=中; blank=empty"),
            Check(
                WindowsOnlineCreatorMarkerPolicy.StatusSuffix(" toki ") ==
                "  ·  创建者 toki" &&
                WindowsOnlineCreatorMarkerPolicy.ToolTipText(" toki ") ==
                "创建者  toki" &&
                WindowsOnlineCreatorMarkerPolicy.ToolTipText(" ") == null,
                "online-creator-hover-status-and-tooltip-match-wpf",
                "status='  ·  创建者 toki'; tooltip='创建者  toki'; blank=null")
        ];
    }

    private static WindowsOnlineCreatorMarkerSpecResult Check(
        bool passed,
        string name,
        string detail) =>
        new(name, passed, detail);
}

internal sealed record WindowsOnlineCreatorMarkerSpecResult(
    string Name,
    bool Passed,
    string Detail);

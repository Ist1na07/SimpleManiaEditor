using Avalonia;
using Avalonia.Input;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Infrastructure;

public enum WindowsTimingListShortcutCommand
{
    None,
    Delete,
    CopyEffect,
    PasteEffect
}

public sealed record WindowsTimingEffectOption(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record WindowsMetadataKeyCountOption(int Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record WindowsMetadataStats(int Notes, int Holds, int Timing);

public static class WindowsInspectorParityPolicy
{
    private static readonly IReadOnlyList<string> MetadataFocusOrder =
        Array.AsReadOnly(new[]
        {
            "Metadata.Title",
            "Metadata.RomanizeTitle",
            "Metadata.TitleOriginal",
            "Metadata.Artist",
            "Metadata.RomanizeArtist",
            "Metadata.ArtistOriginal",
            "Metadata.Creator",
            "Metadata.Version",
            "Metadata.Bpm",
            "Metadata.KeyCount",
            "Metadata.AudioFile",
            "Metadata.AudioSelect",
            "Metadata.AnalyzeSingleTempo",
            "Metadata.AnalyzeVariableTempo",
            "Metadata.Offset",
            "Metadata.PreviewTime",
            "Metadata.PreviewHere",
            "Metadata.CoverFile",
            "Metadata.CoverSelect",
            "Metadata.BackgroundFile",
            "Metadata.BackgroundSelect",
            "Metadata.Apply"
        });

    private static readonly IReadOnlyList<string> TimingFocusOrder =
        Array.AsReadOnly(new[]
        {
            "Inspector.Timing.Beat",
            "Inspector.Timing.Delay",
            "Inspector.Timing.Bpm",
            "Inspector.Timing.Effect",
            "Inspector.Timing.Value",
            "Inspector.Timing.CustomField",
            "Inspector.Timing.List",
            "Inspector.Timing.Add",
            "Inspector.Timing.Apply",
            "Inspector.Timing.Delete",
            "Inspector.Timing.CopyEffect",
            "Inspector.Timing.PasteAtRedLine"
        });

    private static readonly IReadOnlyList<string> NoteFocusOrder =
        Array.AsReadOnly(new[]
        {
            "Inspector.Note.Beat",
            "Inspector.Note.EndBeat",
            "Inspector.Note.Lane",
            "Inspector.Note.Volume",
            "Inspector.Note.Sound",
            "Inspector.Note.SoundExisting",
            "Inspector.Note.SoundSelect",
            "Inspector.Note.Group",
            "Inspector.Note.Apply",
            "Inspector.Note.Delete"
        });

    private static readonly IReadOnlyList<string> GroupStaticFocusOrder =
        Array.AsReadOnly(new[]
        {
            "Inspector.Group.CreateAndUse",
            "Inspector.Group.Id",
            "Inspector.Group.SetCurrent",
            "Inspector.Group.AssignSelection",
            "Inspector.Group.ShowAll",
            "Inspector.Group.HideNonzero",
            "Inspector.Group.ClearSelection"
        });

    private static readonly IReadOnlyList<string> SettingsStaticFocusOrder =
        Array.AsReadOnly(new[]
        {
            "Settings.Language",
            "Settings.Snap",
            "Settings.FixedSnap",
            "Settings.AnchorLine",
            "Settings.AnchorLineAbove",
            "Settings.AnchorCell",
            "Settings.AnchorCellBottom",
            "Settings.GridLaneWidth",
            "Settings.MainEditorHorizontalOffset",
            "Settings.GridBeatHeight",
            "Settings.NoteWidth",
            "Settings.NoteHeight",
            "Settings.NoteOpacity",
            "Settings.JudgementLinePosition",
            "Settings.JudgementLineThickness",
            "Settings.JudgementLineOpacity",
            "Settings.ProEditorColumns",
            "Settings.UiScale",
            "Settings.FontScale",
            "FrameRateLimitBox",
            "Settings.ResetEditorView",
            "Settings.ResetFloatingLayout",
            "Settings.AutoBackupEnabled",
            "Settings.AutoBackupMinutes",
            "Settings.BackupNow",
            "Settings.RestoreBackup",
            "Settings.OpenBackupFolder",
            "Settings.Cache.Backups",
            "Settings.Cache.UnopenedEditing",
            "Settings.Cache.OnlineResources",
            "Settings.Cache.CleanSelected",
            "Settings.Shortcuts.Reset",
            "Audio.Volume",
            "KeySound.Enabled",
            "KeySound.Preset",
            "KeySound.CustomFile",
            "KeySound.Volume",
            "KeySound.LongNoteEnd",
            "KeySound.GlobalOffset",
            "KeySound.ChartOffset",
            "Audio.PreservePitch",
            "Settings.About",
            "Settings.Bilibili",
            "Settings.Sponsor",
            "Settings.CheckForUpdates"
        });

    private static readonly IReadOnlyList<string> SettingsExpandedFocusOrder =
        BuildSettingsExpandedFocusOrder();

    public static IReadOnlyList<string> Wpf110TabHeaders { get; } =
        ["谱面", "Timing Edit", "Note", "分组", "设置"];

    public static IReadOnlyList<string> Wpf110TimingActionLabels { get; } =
        ["新增", "应用", "删除", "复制 Effect", "粘贴到红线"];

    public static IReadOnlyList<WindowsTimingEffectOption> Wpf110TimingEffectOptions { get; } =
    [
        new("None", "无"),
        new("Scroll", "Scroll"),
        new("Jump", "Jump"),
        new("HS", "HS"),
        new("Sign", "Sign"),
        new("Custom", "自定义字段")
    ];

    public static IReadOnlyList<WindowsMetadataKeyCountOption> Wpf110MetadataKeyCountOptions { get; } =
        Enumerable.Range(4, 7)
            .Select(value => new WindowsMetadataKeyCountOption(value, $"{value} Keys"))
            .ToArray();

    public const string Wpf110MetadataTitle = "谱面信息";
    public const string Wpf110MetadataSubtitle = "Malody Key / osu!mania 元数据";
    public const string Wpf110MetadataTitleAsciiLabel = "歌曲名";
    public const string Wpf110MetadataArtistAsciiLabel = "艺术家";
    public const string Wpf110MetadataKeyCountLabel = "轨道数";
    public const string Wpf110MetadataApplyLabel = "应用谱面信息";
    public static IReadOnlyList<string> Wpf110MetadataStatLabels { get; } =
        ["Notes", "Holds", "Timing"];

    public static IReadOnlyList<string> Wpf110NoteActionLabels { get; } =
        ["选择已有 Key 音", "选择文件", "应用", "删除"];

    public static IReadOnlyList<string> Wpf110MetadataFocusOrder =>
        MetadataFocusOrder;

    public static IReadOnlyList<string> Wpf110TimingFocusOrder =>
        TimingFocusOrder;

    public static IReadOnlyList<string> Wpf110NoteFocusOrder =>
        NoteFocusOrder;

    public static IReadOnlyList<string> Wpf110GroupStaticFocusOrder =>
        GroupStaticFocusOrder;

    public static IReadOnlyList<string> Wpf110SettingsStaticFocusOrder =>
        SettingsStaticFocusOrder;

    public static IReadOnlyList<string> Wpf110SettingsExpandedFocusOrder =>
        SettingsExpandedFocusOrder;

    public static int GroupDynamicFocusStartIndex =>
        GroupStaticFocusOrder.Count;

    public static Thickness TimingContentMargin => new(22, 14, 22, 20);
    public static Thickness NoteContentPadding => new(22, 14, 22, 24);
    public static Thickness GroupContentPadding => new(18, 14, 18, 24);
    public static Thickness SettingsContentPadding => new(22, 14, 22, 24);
    public static Thickness SettingsSeparatorMargin => new(0, 18, 0, 10);
    public const double InspectorContentMaximumWidth = 286;
    public const double NoteHintMaximumWidth = 258;

    // Avalonia's scaled StackPanel can arrange the final Metadata rows beyond
    // the ScrollViewer's reported extent. Reserve enough content-owned space
    // for the WPF Apply/Status footer to remain physically reachable.
    public const double MetadataBottomScrollClearance = 64;
    public const double MetadataTitleFontSize = 25;
    public const double MetadataStatValueFontSize = 20;
    public const double MetadataStatsTopMargin = 22;
    public const double MetadataPairGap = 10;
    public const double MetadataRomanizeActionColumnWidth = 84;
    public const double MetadataFileActionColumnWidth = 86;
    public const double MetadataPreviewActionColumnWidth = 76;
    public static Thickness MetadataToolButtonMargin => new(2, 0);
    public static Thickness MetadataSingleTempoActionMargin => new(3, 7, 3, 2);
    public static Thickness MetadataVariableTempoActionMargin => new(3, 3, 3, 2);
    public static Thickness MetadataApplyActionMargin => new(3, 18, 3, 0);
    public const double SettingsBottomScrollClearance = 64;
    public const double TimingTitleFontSize = 25;
    public const double NoteTitleFontSize = 25;
    public const double GroupTitleFontSize = 24;
    public const double SettingsTitleFontSize = 25;
    public const double SettingsSectionTitleFontSize = 17;

    public static WindowsMetadataStats MeasureMetadataStats(
        ChartDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var notes = 0;
        var holds = 0;
        foreach (var note in document.Notes)
        {
            if (note.IsDraft) continue;
            notes++;
            if (note.IsHold) holds++;
        }
        return new WindowsMetadataStats(notes, holds, document.Timing.Count);
    }
    public const double SettingsKeySoundActionWidth = 90;
    public const bool SettingsPreservePitchEnabledWithoutLoadedAudio = true;
    public const int FloatingPaletteColumnSpan = 1;
    public const double TimingListTopMargin = 14;
    public const double TimingActionsTopMargin = 12;
    public const double TimingClipboardActionsTopMargin = 6;
    public const double TimingActionMinimumHeight = 37;
    public const double QuickEditPaletteExpandedContentWidth = 224;
    public const double QuickEditPaletteHorizontalChrome = 12;
    public const double QuickEditPaletteDefaultRightMargin = 18;
    public const double QuickValuesPaletteWidth = 112;
    public const double QuickValuesControlHeight = 26;
    public const double QuickValuesSectionGap = 3;
    public const double FloatingPaletteGap = 8;
    public const double QuickValuesPaletteDefaultRightMargin =
        QuickEditPaletteDefaultRightMargin +
        QuickEditPaletteExpandedContentWidth +
        QuickEditPaletteHorizontalChrome +
        FloatingPaletteGap;

    public static bool FloatingPalettesAreSeparatedByDefault =>
        QuickValuesPaletteDefaultRightMargin >=
        QuickEditPaletteDefaultRightMargin +
        QuickEditPaletteExpandedContentWidth +
        QuickEditPaletteHorizontalChrome +
        FloatingPaletteGap;

    public static bool ShouldPopulateTimingFields(
        bool force,
        bool draftDirty,
        Guid? draftTimingId,
        Guid timingId) =>
        force || !draftDirty || draftTimingId != timingId;

    public static int ResolveMetadataTabIndex(string? automationId) =>
        ResolveTabIndex(MetadataFocusOrder, automationId);

    public static int ResolveTimingTabIndex(string? automationId) =>
        ResolveTabIndex(TimingFocusOrder, automationId);

    public static int ResolveNoteTabIndex(string? automationId) =>
        ResolveTabIndex(NoteFocusOrder, automationId);

    public static int ResolveGroupStaticTabIndex(string? automationId) =>
        ResolveTabIndex(GroupStaticFocusOrder, automationId);

    public static int ResolveSettingsTabIndex(string? automationId) =>
        ResolveTabIndex(SettingsExpandedFocusOrder, automationId);

    public static bool AllowWindowUiMutation(
        bool teardownStarted,
        bool closePreflightComplete) =>
        !teardownStarted && !closePreflightComplete;

    public static WindowsTimingListShortcutCommand ResolveTimingListShortcut(
        Key reportedKey,
        PhysicalKey physicalKey,
        string? keySymbol,
        KeyModifiers modifiers,
        Key? pressedKeyFallback = null)
    {
        var key = WindowsEditorShortcutGesture.ResolveReportedKey(
            reportedKey,
            physicalKey,
            keySymbol,
            pressedKeyFallback);
        if (key == Key.Delete)
            return WindowsTimingListShortcutCommand.Delete;
        if (!modifiers.HasFlag(KeyModifiers.Control))
            return WindowsTimingListShortcutCommand.None;
        return key switch
        {
            Key.C => WindowsTimingListShortcutCommand.CopyEffect,
            Key.V => WindowsTimingListShortcutCommand.PasteEffect,
            _ => WindowsTimingListShortcutCommand.None
        };
    }

    private static int ResolveTabIndex(
        IReadOnlyList<string> order,
        string? automationId)
    {
        for (var index = 0; index < order.Count; index++)
        {
            if (string.Equals(
                    order[index],
                    automationId,
                    StringComparison.Ordinal))
                return index;
        }

        return -1;
    }

    private static IReadOnlyList<string> BuildSettingsExpandedFocusOrder()
    {
        const int resetShortcutsIndex = 30;
        var result = new List<string>(
            SettingsStaticFocusOrder.Count +
            WindowsEditorShortcutSettings.Catalog.Count);
        result.AddRange(SettingsStaticFocusOrder.Take(resetShortcutsIndex + 1));
        result.AddRange(WindowsEditorShortcutSettings.Catalog.Select(
            descriptor => $"Shortcut_{descriptor.Id}"));
        result.AddRange(SettingsStaticFocusOrder.Skip(resetShortcutsIndex + 1));
        return result.AsReadOnly();
    }
}

using LumaChart.Avalonia.Infrastructure;
using System.Globalization;
using LumaChart.Application.Editing;
using LumaChart.Application.Workspaces;
using LumaChart.Core.Models;

namespace LumaChart.Avalonia.Editing;

public sealed record WindowsNotePropertyFields(
    string Beat,
    string EndBeat,
    string Lane,
    string Volume,
    string Sound,
    string Group);

public sealed record WindowsNoteInspectorState(
    int SelectedCount,
    string Hint,
    Guid? NoteId,
    WindowsNotePropertyFields? Fields);

public sealed record WindowsNotePropertyApplyResult(
    Guid NoteId,
    BeatPosition Beat,
    BeatPosition? EndBeat,
    bool IsDraft,
    int Column,
    int Volume,
    string? Sound,
    int Group);

public sealed record WindowsNoteSoundAssignmentResult(
    Guid NoteId,
    string? Sound,
    bool Applied);

public sealed record WindowsNoteSoundAssignmentPolicy(
    bool IsOnlineRoom,
    Func<string, bool>? IsFrozenResource);

/// <summary>
/// Windows Avalonia adapter for the WPF Note property inspector contract. UI code
/// owns only field presentation; parsing, permission/lock preflight, and the single
/// history mutation live here.
/// </summary>
public static class WindowsNotePropertyCommand
{
    public static WindowsNoteInspectorState ReadInspectorState(
        EditorSession session,
        IReadOnlySet<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var selected = SelectedNotes(session, hiddenGroups);
        if (selected.Length == 0)
            return new WindowsNoteInspectorState(0, "选择一个 Note 进行编辑", null, null);
        if (selected.Length > 1)
            return new WindowsNoteInspectorState(
                selected.Length,
                AvaloniaLocalizationCatalog.Format("已选择 {0} 个 Note", selected.Length),
                null,
                null);

        var note = selected[0];
        return new WindowsNoteInspectorState(
            1,
            (note.IsHold ? "Hold Note" : "Tap Note") +
            (session.IsOwnerProtected(note)
                ? AvaloniaLocalizationCatalog.Text("  ·  房主锁定")
                : string.Empty),
            note.Id,
            new WindowsNotePropertyFields(
                note.Beat.ToString(),
                note.EndBeat?.ToString() ?? string.Empty,
                // Apply parses this back with InvariantCulture. Formatting with the
                // current culture wrote a non-ASCII minus for negative drafts
                // (sv, ar, …), which then failed to parse on an untouched field.
                note.IsDraft
                    ? string.Create(CultureInfo.InvariantCulture, $"D:{note.DraftColumn}")
                    : (note.Lane + 1).ToString(CultureInfo.InvariantCulture),
                note.Volume.ToString(CultureInfo.InvariantCulture),
                note.Sound ?? string.Empty,
                note.Group.ToString(CultureInfo.InvariantCulture)));
    }

    public static WindowsNotePropertyApplyResult Apply(
        EditorSession session,
        WindowsNotePropertyFields fields,
        WindowsNoteSoundAssignmentPolicy? soundPolicy = null,
        IReadOnlySet<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(fields);
        EnsureNoteEditAllowed(session);

        var selected = SelectedNotes(session, hiddenGroups);
        if (selected.Length != 1)
            throw new InvalidOperationException("请只选择一个 Note。");
        var source = selected[0];
        if (!session.AllowOwnerProtectedEdits && session.IsOwnerProtected(source))
            throw new InvalidOperationException("该 Note 已由房主锁定，成员不能修改。");

        if (!BeatPosition.TryParse(fields.Beat, out var beat) ||
            beat.CompareTo(BeatPosition.Zero) < 0)
            throw new InvalidOperationException("Beat 必须是非负的有效有理 Beat。");

        var laneText = fields.Lane.Trim();
        var isDraft = laneText.StartsWith("D:", StringComparison.OrdinalIgnoreCase);
        var columnText = isDraft ? laneText[2..].Trim() : laneText;
        if (!int.TryParse(
                columnText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedColumn) ||
            !IsColumnInRange(parsedColumn, isDraft, session.Document.KeyCount))
            throw new InvalidOperationException(
                isDraft
                    ? AvaloniaLocalizationCatalog.Format("草稿列范围为 -64–{0}。", session.Document.KeyCount + 63)
                    : AvaloniaLocalizationCatalog.Format("主轨范围为 1–{0}。", session.Document.KeyCount));

        if (!int.TryParse(
                fields.Volume,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsedVolume))
            throw new InvalidOperationException("音量必须是整数。");
        var volume = Math.Clamp(parsedVolume, 0, 100);

        if (!int.TryParse(
                fields.Group,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var group) ||
            group is < 0 or > ushort.MaxValue)
            throw new InvalidOperationException("Group ID 范围为 0–65535。");

        BeatPosition? endBeat = null;
        if (!string.IsNullOrWhiteSpace(fields.EndBeat))
        {
            if (!BeatPosition.TryParse(fields.EndBeat, out var parsedEnd) ||
                parsedEnd.CompareTo(beat) <= 0)
                throw new InvalidOperationException("End Beat 必须晚于 Beat。");
            endBeat = parsedEnd;
        }

        if (!session.AllowOwnerProtectedEdits &&
            session.IntersectsOwnerProtectedRegion(beat, endBeat ?? beat))
            throw new InvalidOperationException("目标位置位于房主锁定选区，成员不能移动或修改 Note。");

        var sound = ValidateSoundAssignment(session.Document, fields.Sound, soundPolicy);
        var noteId = source.Id;
        session.Mutate(document =>
        {
            var note = document.Notes.First(candidate => candidate.Id == noteId);
            note.Beat = beat;
            note.EndBeat = endBeat;
            note.IsDraft = isDraft;
            note.DraftColumn = isDraft ? parsedColumn : 0;
            note.Lane = isDraft ? 0 : parsedColumn - 1;
            note.Volume = volume;
            note.Sound = sound;
            note.Group = group;
        });

        return new WindowsNotePropertyApplyResult(
            noteId,
            beat,
            endBeat,
            isDraft,
            parsedColumn,
            volume,
            sound,
            group);
    }

    public static WindowsNoteSoundAssignmentResult AssignSound(
        EditorSession session,
        string? sound,
        WindowsNoteSoundAssignmentPolicy? soundPolicy = null,
        IReadOnlySet<int>? hiddenGroups = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureNoteEditAllowed(session);

        var selected = SelectedNotes(session, hiddenGroups);
        if (selected.Length != 1)
            throw new InvalidOperationException("请只选择一个 Note。");
        var source = selected[0];
        if (!session.AllowOwnerProtectedEdits && session.IsOwnerProtected(source))
            throw new InvalidOperationException("该 Note 已由房主锁定，成员不能修改。");

        var validated = ValidateSoundAssignment(session.Document, sound, soundPolicy);
        if (string.Equals(source.Sound, validated, StringComparison.Ordinal))
            return new WindowsNoteSoundAssignmentResult(source.Id, validated, false);

        var noteId = source.Id;
        session.Mutate(document =>
            document.Notes.First(candidate => candidate.Id == noteId).Sound = validated);
        return new WindowsNoteSoundAssignmentResult(noteId, validated, true);
    }

    private static ChartNote[] SelectedNotes(
        EditorSession session,
        IReadOnlySet<int>? hiddenGroups) =>
        session.Document.Notes
            .Where(note => session.Selection.Contains(note.Id) &&
                           (hiddenGroups == null || !hiddenGroups.Contains(note.Group)))
            .ToArray();

    private static bool IsColumnInRange(int column, bool isDraft, int keyCount) =>
        isDraft
            ? column >= -64 && column <= keyCount + 63
            : column >= 1 && column <= keyCount;

    private static string? ValidateSoundAssignment(
        ChartDocument document,
        string? value,
        WindowsNoteSoundAssignmentPolicy? soundPolicy)
    {
        var requested = value?.Trim();
        if (string.IsNullOrWhiteSpace(requested)) return null;

        var safeName = Path.GetFileName(requested);
        if (string.IsNullOrWhiteSpace(safeName) ||
            !string.Equals(requested, safeName, StringComparison.Ordinal))
            throw new InvalidOperationException("Key 音只保存不带目录的纯文件名。");
        if (!ChartAssetImportService.IsSupportedNoteSoundFile(safeName))
            throw new InvalidOperationException("Key 音仅支持 WAV、OGG、MP3、FLAC、M4A 或 AAC。");

        var chartPath = document.FilePath;
        if (string.IsNullOrWhiteSpace(chartPath))
            throw new InvalidOperationException(AvaloniaLocalizationCatalog.Format("谱面目录中找不到 Key 音文件“{0}”。", safeName));
        string resolved;
        try
        {
            var chartDirectory = Path.GetDirectoryName(Path.GetFullPath(chartPath));
            if (string.IsNullOrWhiteSpace(chartDirectory))
                throw new InvalidOperationException();
            resolved = Path.Combine(chartDirectory, safeName);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            throw new InvalidOperationException(AvaloniaLocalizationCatalog.Format("谱面目录中找不到 Key 音文件“{0}”。", safeName), exception);
        }
        if (!File.Exists(resolved))
            throw new InvalidOperationException(AvaloniaLocalizationCatalog.Format("谱面目录中找不到 Key 音文件“{0}”。", safeName));
        if (soundPolicy?.IsOnlineRoom == true &&
            (soundPolicy.IsFrozenResource == null || !soundPolicy.IsFrozenResource(resolved)))
            throw new InvalidOperationException("联机房间只能引用建房时已经冻结并上传的 Key 音文件。");
        return safeName;
    }

    private static void EnsureNoteEditAllowed(EditorSession session)
    {
        if (session.IsReadOnly)
            throw new InvalidOperationException("当前谱面为只读状态，不能修改 Note 属性。");
        if (!session.CanEditNotes)
            throw new InvalidOperationException("当前没有编辑 Note 的权限。");
    }
}

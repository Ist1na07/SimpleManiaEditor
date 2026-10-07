using LumaChart.Core.Localization;
using System.Globalization;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;

namespace LumaChart.Application.Editing;

public sealed record ChartMetadataDraft(
    string Title,
    string TitleOriginal,
    string Artist,
    string ArtistOriginal,
    string Creator,
    string Version,
    string PreviewTime,
    string Bpm,
    string Offset,
    int KeyCount,
    string OverallDifficulty,
    string HpDrainRate,
    string CoverFile = "",
    string BackgroundFile = "");

public sealed record ChartMetadataDraftRebaseResult(
    ChartMetadataDraft Draft,
    int PreservedLocalFieldCount,
    int UpdatedAuthoritativeFieldCount,
    bool HasLocalChanges);

public sealed record ChartMetadataEdit(
    string Title,
    string TitleOriginal,
    string Artist,
    string ArtistOriginal,
    string Creator,
    string Version,
    int PreviewTime,
    double Bpm,
    double Offset,
    int KeyCount,
    double OverallDifficulty,
    double HpDrainRate,
    string CoverFile = "",
    string BackgroundFile = "");

public enum ChartMetadataApplyStatus
{
    Applied,
    Unchanged,
    Denied
}

public sealed record ChartMetadataApplyResult(
    ChartMetadataApplyStatus Status,
    string Message,
    string? PermissionCategory = null,
    int RemovedNoteCount = 0);

/// <summary>
/// Shared metadata validation and atomic editor mutation used by desktop hosts.
/// UI layers own controls and resource pickers, while all normalization,
/// permission planning and document mutation stays here.
/// </summary>
public static class ChartMetadataEditingService
{
    public static ChartMetadataDraft Capture(ChartDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new ChartMetadataDraft(
            document.Metadata.Title,
            document.Metadata.TitleOriginal,
            document.Metadata.Artist,
            document.Metadata.ArtistOriginal,
            document.Metadata.Creator,
            document.Metadata.Version,
            document.Metadata.PreviewTime.ToString(CultureInfo.InvariantCulture),
            document.Metadata.Bpm.ToString("R", CultureInfo.InvariantCulture),
            SongOffset(document).ToString("R", CultureInfo.InvariantCulture),
            document.KeyCount,
            document.Metadata.OverallDifficulty.ToString("R", CultureInfo.InvariantCulture),
            document.Metadata.HpDrainRate.ToString("R", CultureInfo.InvariantCulture),
            document.Metadata.CoverFile,
            document.Metadata.BackgroundFile);
    }

    public static bool TryParseDraft(
        ChartMetadataDraft draft,
        out ChartMetadataEdit? edit,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var title = CleanSingleLine(draft.Title);
        var artist = CleanSingleLine(draft.Artist);
        var creator = CleanSingleLine(draft.Creator);
        var version = CleanSingleLine(draft.Version);
        var titleOriginal = CleanSingleLine(draft.TitleOriginal);
        var artistOriginal = CleanSingleLine(draft.ArtistOriginal);
        if (!TryNormalizeOptionalResourceReference(draft.CoverFile, out var coverFile) ||
            !TryNormalizeOptionalResourceReference(draft.BackgroundFile, out var backgroundFile))
        {
            edit = null;
            error = "封面和背景必须使用谱面目录内的安全相对路径。";
            return false;
        }

        if (!IsCommunityAscii(title) || !IsCommunityAscii(artist))
        {
            edit = null;
            error = "歌曲名和艺术家必须使用单行 ASCII 英文字符；原始文字请填写在原文栏。";
            return false;
        }
        if (!IsSafeSingleLine(creator) || !IsSafeSingleLine(version) ||
            !IsSafeSingleLine(titleOriginal) || !IsSafeSingleLine(artistOriginal))
        {
            edit = null;
            error = "谱面文字不能包含换行或控制字符。";
            return false;
        }
        if (draft.KeyCount is < 1 or > 18)
        {
            edit = null;
            error = "轨道数必须在 1–18 之间。";
            return false;
        }
        if (!TryNumber(draft.Bpm, out var bpm) || bpm is <= 0 or > 10_000)
        {
            edit = null;
            error = "谱面 BPM 必须是大于 0 且不超过 10000 的数字。";
            return false;
        }
        if (!TryNumber(draft.Offset, out var offset) || offset is < -600_000 or > 600_000)
        {
            edit = null;
            error = "音频 Offset 必须是 -600000–600000 之间的毫秒数。";
            return false;
        }
        if (!int.TryParse(
                draft.PreviewTime,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var previewTime) || previewTime < 0)
        {
            edit = null;
            error = "预览时间必须是大于等于 0 的整数毫秒。";
            return false;
        }
        if (!TryNumber(draft.OverallDifficulty, out var overallDifficulty) ||
            overallDifficulty is < 0 or > 10)
        {
            edit = null;
            error = "OD 必须是 0–10 之间的数字。";
            return false;
        }
        if (!TryNumber(draft.HpDrainRate, out var hpDrainRate) || hpDrainRate is < 0 or > 10)
        {
            edit = null;
            error = "HP 必须是 0–10 之间的数字。";
            return false;
        }

        edit = new ChartMetadataEdit(
            string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
            titleOriginal,
            string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
            artistOriginal,
            creator,
            version,
            previewTime,
            bpm,
            offset,
            draft.KeyCount,
            overallDifficulty,
            hpDrainRate,
            coverFile,
            backgroundFile);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Parses the editor form for local preservation operations such as
    /// backup, export and close preflight. WPF deliberately keeps those paths
    /// usable for unfinished/private charts: invalid numeric draft values fall
    /// back to the committed document and community-only ASCII/default-title
    /// rules are not imposed. Unsafe paths and control characters remain
    /// rejected or normalized.
    /// </summary>
    public static bool TryParseDraftForLocalPreservation(
        ChartMetadataDraft draft,
        ChartDocument fallback,
        out ChartMetadataEdit? edit,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(fallback);
        if (!TryNormalizeOptionalResourceReference(draft.CoverFile, out var coverFile) ||
            !TryNormalizeOptionalResourceReference(draft.BackgroundFile, out var backgroundFile))
        {
            edit = null;
            error = "封面和背景必须使用谱面目录内的安全相对路径。";
            return false;
        }

        var keyCount = draft.KeyCount is >= 1 and <= 18
            ? draft.KeyCount
            : fallback.KeyCount;
        var bpm = TryNumber(draft.Bpm, out var parsedBpm) &&
                  parsedBpm is > 0 and <= 10_000
            ? parsedBpm
            : fallback.Metadata.Bpm;
        var offset = TryNumber(draft.Offset, out var parsedOffset) &&
                     parsedOffset is >= -600_000 and <= 600_000
            ? parsedOffset
            : SongOffset(fallback);
        var previewTime = int.TryParse(
                              draft.PreviewTime,
                              NumberStyles.Integer,
                              CultureInfo.InvariantCulture,
                              out var parsedPreview) &&
                          parsedPreview >= 0
            ? parsedPreview
            : fallback.Metadata.PreviewTime;
        var overallDifficulty = TryNumber(
                                    draft.OverallDifficulty,
                                    out var parsedOverallDifficulty) &&
                                parsedOverallDifficulty is >= 0 and <= 10
            ? parsedOverallDifficulty
            : fallback.Metadata.OverallDifficulty;
        var hpDrainRate = TryNumber(draft.HpDrainRate, out var parsedHpDrainRate) &&
                          parsedHpDrainRate is >= 0 and <= 10
            ? parsedHpDrainRate
            : fallback.Metadata.HpDrainRate;

        edit = new ChartMetadataEdit(
            CleanSafeSingleLine(draft.Title),
            CleanSafeSingleLine(draft.TitleOriginal),
            CleanSafeSingleLine(draft.Artist),
            CleanSafeSingleLine(draft.ArtistOriginal),
            CleanSafeSingleLine(draft.Creator),
            CleanSafeSingleLine(draft.Version),
            previewTime,
            bpm,
            offset,
            keyCount,
            overallDifficulty,
            hpDrainRate,
            coverFile,
            backgroundFile);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Rebases an uncommitted form over a newer authoritative document the
    /// same way the WPF editor does: only fields changed from the prior
    /// baseline stay local; untouched fields follow the remote document.
    /// </summary>
    public static ChartMetadataDraft RebaseDraft(
        ChartMetadataDraft current,
        ChartMetadataDraft baseline,
        ChartMetadataDraft authoritative) =>
        RebaseDraftWithDiagnostics(current, baseline, authoritative).Draft;

    public static ChartMetadataDraftRebaseResult RebaseDraftWithDiagnostics(
        ChartMetadataDraft current,
        ChartMetadataDraft baseline,
        ChartMetadataDraft authoritative)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(authoritative);

        var preserved = 0;
        var updated = 0;
        string RebaseText(string currentValue, string baselineValue, string authoritativeValue)
        {
            if (!string.Equals(authoritativeValue, baselineValue, StringComparison.Ordinal))
                updated++;
            if (string.Equals(currentValue, baselineValue, StringComparison.Ordinal))
                return authoritativeValue;
            preserved++;
            return currentValue;
        }

        int RebaseNumber(int currentValue, int baselineValue, int authoritativeValue)
        {
            if (authoritativeValue != baselineValue) updated++;
            if (currentValue == baselineValue) return authoritativeValue;
            preserved++;
            return currentValue;
        }

        var rebased = new ChartMetadataDraft(
            RebaseText(current.Title, baseline.Title, authoritative.Title),
            RebaseText(current.TitleOriginal, baseline.TitleOriginal, authoritative.TitleOriginal),
            RebaseText(current.Artist, baseline.Artist, authoritative.Artist),
            RebaseText(current.ArtistOriginal, baseline.ArtistOriginal, authoritative.ArtistOriginal),
            RebaseText(current.Creator, baseline.Creator, authoritative.Creator),
            RebaseText(current.Version, baseline.Version, authoritative.Version),
            RebaseText(current.PreviewTime, baseline.PreviewTime, authoritative.PreviewTime),
            RebaseText(current.Bpm, baseline.Bpm, authoritative.Bpm),
            RebaseText(current.Offset, baseline.Offset, authoritative.Offset),
            RebaseNumber(current.KeyCount, baseline.KeyCount, authoritative.KeyCount),
            RebaseText(
                current.OverallDifficulty,
                baseline.OverallDifficulty,
                authoritative.OverallDifficulty),
            RebaseText(current.HpDrainRate, baseline.HpDrainRate, authoritative.HpDrainRate),
            RebaseText(current.CoverFile, baseline.CoverFile, authoritative.CoverFile),
            RebaseText(current.BackgroundFile, baseline.BackgroundFile, authoritative.BackgroundFile));
        return new ChartMetadataDraftRebaseResult(
            rebased,
            preserved,
            updated,
            !DraftEquals(rebased, authoritative));
    }

    public static bool DraftEquals(ChartMetadataDraft left, ChartMetadataDraft right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.KeyCount == right.KeyCount &&
               string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
               string.Equals(left.TitleOriginal, right.TitleOriginal, StringComparison.Ordinal) &&
               string.Equals(left.Artist, right.Artist, StringComparison.Ordinal) &&
               string.Equals(left.ArtistOriginal, right.ArtistOriginal, StringComparison.Ordinal) &&
               string.Equals(left.Creator, right.Creator, StringComparison.Ordinal) &&
               string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
               string.Equals(left.PreviewTime, right.PreviewTime, StringComparison.Ordinal) &&
               string.Equals(left.Bpm, right.Bpm, StringComparison.Ordinal) &&
               string.Equals(left.Offset, right.Offset, StringComparison.Ordinal) &&
               string.Equals(
                   left.OverallDifficulty,
                   right.OverallDifficulty,
                   StringComparison.Ordinal) &&
               string.Equals(left.HpDrainRate, right.HpDrainRate, StringComparison.Ordinal) &&
               string.Equals(left.CoverFile, right.CoverFile, StringComparison.Ordinal) &&
               string.Equals(left.BackgroundFile, right.BackgroundFile, StringComparison.Ordinal);
    }

    public static ChartMetadataApplyResult Apply(
        EditorSession session,
        ChartMetadataEdit edit,
        bool enforceCommunityRules = true)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(edit);
        ValidateEdit(edit, enforceCommunityRules);

        var document = session.Document;
        var metadataChanged = !MetadataEquals(document.Metadata, edit);
        var keyCountChanged = document.KeyCount != edit.KeyCount;
        var timingChanged = !SongOffset(document).Equals(edit.Offset);
        if (!metadataChanged && !keyCountChanged && !timingChanged)
            return new ChartMetadataApplyResult(ChartMetadataApplyStatus.Unchanged, "谱面信息没有变化。");

        var removedNotes = keyCountChanged
            ? document.Notes.Count(note => !note.IsDraft && note.Lane >= edit.KeyCount)
            : 0;
        if (session.IsReadOnly)
            return Denied("观察者不能修改谱面信息、轨道数或 Offset。", "观察者只读", removedNotes);
        if ((metadataChanged || keyCountChanged) && !session.CanEditMetadata)
            return Denied("修改 Metadata 或 KeyCount 需要谱面信息权限。", "谱面信息", removedNotes);
        if (timingChanged && !session.CanEditTiming)
            return Denied("Offset 属于 Timing，需要 Effect 权限。", "Effect", removedNotes);
        if (removedNotes > 0 && !session.CanEditNotes)
            return Denied("降低轨道数会删除右侧 Note，需要 Note 权限。", "Note", removedNotes);
        // The song-offset record is always at beat 0 (created there if missing).
        if (timingChanged && !session.AllowOwnerProtectedEdits && session.IsBeatOwnerProtected(BeatPosition.Zero))
            return Denied("第一个 Timing 位于房主锁定区域，Offset 未应用。", "房主锁定区域", removedNotes);

        var serial = session.ChangeSerial;
        session.Mutate(next =>
        {
            next.Metadata.Title = edit.Title;
            next.Metadata.TitleOriginal = edit.TitleOriginal;
            next.Metadata.Artist = edit.Artist;
            next.Metadata.ArtistOriginal = edit.ArtistOriginal;
            next.Metadata.Creator = edit.Creator;
            next.Metadata.Version = edit.Version;
            next.Metadata.PreviewTime = edit.PreviewTime;
            next.Metadata.Bpm = edit.Bpm;
            next.Metadata.OverallDifficulty = edit.OverallDifficulty;
            next.Metadata.HpDrainRate = edit.HpDrainRate;
            next.Metadata.CoverFile = edit.CoverFile;
            next.Metadata.BackgroundFile = edit.BackgroundFile;
            next.KeyCount = edit.KeyCount;
            if (timingChanged) next.GetOrCreateSongOffsetTiming().Delay = edit.Offset;
        });
        if (session.ChangeSerial == serial)
            return Denied("当前细分权限不允许这组谱面信息修改。", null, removedNotes);

        var suffix = removedNotes > 0 ? LocalizationCatalog.Format("；删除了 {0} 个超出新轨道数的 Note", removedNotes) : string.Empty;
        return new ChartMetadataApplyResult(
            ChartMetadataApplyStatus.Applied,
            LocalizationCatalog.Format("谱面信息已原子应用{0}。", suffix),
            RemovedNoteCount: removedNotes);
    }

    public static ChartMetadataApplyResult SetPreviewTime(EditorSession session, int previewTime)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (previewTime < 0) throw new ArgumentOutOfRangeException(nameof(previewTime));
        if (session.Document.Metadata.PreviewTime == previewTime)
            return new ChartMetadataApplyResult(ChartMetadataApplyStatus.Unchanged, "预览时间没有变化。");
        if (session.IsReadOnly)
            return Denied("观察者不能修改谱面预览时间。", "观察者只读");
        if (!session.CanEditMetadata)
            return Denied("设置预览时间需要谱面信息权限。", "谱面信息");

        var serial = session.ChangeSerial;
        session.Mutate(document => document.Metadata.PreviewTime = previewTime);
        return session.ChangeSerial == serial
            ? Denied("当前细分权限不允许修改预览时间。")
            : new ChartMetadataApplyResult(
                ChartMetadataApplyStatus.Applied,
                LocalizationCatalog.Format("已将当前播放位置 {0} ms 设为预览时间。", previewTime));
    }

    public static ChartMetadataApplyResult SetAudioFile(EditorSession session, string audioFile)
    {
        ArgumentNullException.ThrowIfNull(session);
        audioFile = NormalizeResourceReference(audioFile);
        if (session.Document.Metadata.AudioFile == audioFile)
            return new ChartMetadataApplyResult(ChartMetadataApplyStatus.Unchanged, "谱面音频没有变化。");
        if (session.IsReadOnly)
            return Denied("观察者不能修改谱面音频。", "观察者只读");
        if (!session.CanEditMetadata)
            return Denied("选择谱面音频需要谱面信息权限。", "谱面信息");

        var serial = session.ChangeSerial;
        session.Mutate(document => document.Metadata.AudioFile = audioFile);
        return session.ChangeSerial == serial
            ? Denied("当前细分权限不允许修改谱面音频。")
            : new ChartMetadataApplyResult(
                ChartMetadataApplyStatus.Applied,
                LocalizationCatalog.Format("谱面音频已设为 {0}。", audioFile));
    }

    public static int ResolvePreviewTime(
        bool transportPlaying,
        double transportMilliseconds,
        IEnumerable<TimingEvent> timing,
        BeatPosition currentBeat)
    {
        ArgumentNullException.ThrowIfNull(timing);
        var milliseconds = transportPlaying
            ? transportMilliseconds
            : new TimingMap(timing).BeatToMilliseconds(currentBeat);
        if (!double.IsFinite(milliseconds))
            throw new ArgumentOutOfRangeException(nameof(transportMilliseconds), "The current playback position is not finite.");
        return (int)Math.Clamp(
            Math.Round(milliseconds, MidpointRounding.AwayFromZero),
            0,
            int.MaxValue);
    }

    private static ChartMetadataApplyResult Denied(
        string message,
        string? permissionCategory = null,
        int removedNotes = 0) =>
        new(ChartMetadataApplyStatus.Denied, message, permissionCategory, removedNotes);

    private static bool MetadataEquals(ChartMetadata metadata, ChartMetadataEdit edit) =>
        metadata.Title == edit.Title && metadata.TitleOriginal == edit.TitleOriginal &&
        metadata.Artist == edit.Artist && metadata.ArtistOriginal == edit.ArtistOriginal &&
        metadata.Creator == edit.Creator && metadata.Version == edit.Version &&
        metadata.PreviewTime == edit.PreviewTime && metadata.Bpm.Equals(edit.Bpm) &&
        metadata.OverallDifficulty.Equals(edit.OverallDifficulty) &&
        metadata.HpDrainRate.Equals(edit.HpDrainRate) &&
        metadata.CoverFile == edit.CoverFile &&
        metadata.BackgroundFile == edit.BackgroundFile;

    private static void ValidateEdit(
        ChartMetadataEdit edit,
        bool enforceCommunityRules)
    {
        if (enforceCommunityRules &&
            (!IsCommunityAscii(edit.Title) || !IsCommunityAscii(edit.Artist)))
            throw new ArgumentException("Title and artist must contain printable ASCII characters.", nameof(edit));
        if (!IsSafeSingleLine(edit.TitleOriginal) || !IsSafeSingleLine(edit.ArtistOriginal) ||
            !IsSafeSingleLine(edit.Creator) || !IsSafeSingleLine(edit.Version))
            throw new ArgumentException("Metadata text must be a safe single line.", nameof(edit));
        if (edit.PreviewTime < 0) throw new ArgumentOutOfRangeException(nameof(edit));
        if (!double.IsFinite(edit.Bpm) || edit.Bpm is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(edit));
        if (!double.IsFinite(edit.Offset) || edit.Offset is < -600_000 or > 600_000)
            throw new ArgumentOutOfRangeException(nameof(edit));
        if (edit.KeyCount is < 1 or > 18) throw new ArgumentOutOfRangeException(nameof(edit));
        if (!double.IsFinite(edit.OverallDifficulty) || edit.OverallDifficulty is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(edit));
        if (!double.IsFinite(edit.HpDrainRate) || edit.HpDrainRate is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(edit));
        if (!TryNormalizeOptionalResourceReference(edit.CoverFile, out var coverFile) ||
            !string.Equals(coverFile, edit.CoverFile, StringComparison.Ordinal) ||
            !TryNormalizeOptionalResourceReference(edit.BackgroundFile, out var backgroundFile) ||
            !string.Equals(backgroundFile, edit.BackgroundFile, StringComparison.Ordinal))
            throw new ArgumentException(
                "Cover and background must use normalized safe relative paths.",
                nameof(edit));
    }

    private static bool TryNumber(string text, out double value)
    {
        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                     double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        return parsed && double.IsFinite(value);
    }

    private static string CleanSingleLine(string value) =>
        string.Join(' ', value.Replace('\r', ' ').Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string CleanSafeSingleLine(string value) =>
        string.Join(
            ' ',
            new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsCommunityAscii(string value) =>
        !value.Any(character => character is < ' ' or > '~') &&
        !value.Contains('\r') && !value.Contains('\n');

    private static bool IsSafeSingleLine(string value) =>
        !value.Any(char.IsControl) && !value.Contains('\r') && !value.Contains('\n');

    // The Offset field is the song offset: the Delay of the beat-0 BPM record
    // TimingMap plays (the last one at that beat), not the first BPM row in list
    // order. With no beat-0 record the song offset is 0; the first later BPM
    // row's Delay is a section pause and must not be shown or edited here.
    private static double SongOffset(ChartDocument document) =>
        TimingMap.FindSongOffsetTiming(document.Timing)?.Delay ?? 0;

    private static string NormalizeResourceReference(string value)
    {
        value = value.Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..") ||
            value.Any(char.IsControl))
            throw new ArgumentException("谱面音频必须使用安全的相对路径。", nameof(value));
        return value;
    }

    private static bool TryNormalizeOptionalResourceReference(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
        if (normalized.Length == 0) return true;
        if (Path.IsPathRooted(normalized) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or "..") ||
            normalized.Any(char.IsControl))
        {
            normalized = string.Empty;
            return false;
        }
        return true;
    }
}

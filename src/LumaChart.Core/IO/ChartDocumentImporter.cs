using System.Text;
using LumaChart.Core.Models;

namespace LumaChart.Core.IO;

public enum ChartImportFormat
{
    MalodyMc,
    OsuMania
}

/// <summary>
/// Parses caller-provided chart text or streams without treating the source name as a local path.
/// The caller owns every stream passed to this type.
/// </summary>
public static class ChartDocumentImporter
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static ChartImportFormat GetFormatFromSourceName(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var candidate = sourceName.Trim();
        var extension = candidate.StartsWith('.') &&
                        candidate.IndexOfAny(['/', '\\']) < 0
            ? candidate
            : Path.GetExtension(candidate);
        return extension.ToLowerInvariant() switch
        {
            ".mc" => ChartImportFormat.MalodyMc,
            ".osu" => ChartImportFormat.OsuMania,
            _ => throw new NotSupportedException(
                $"Unsupported chart source extension '{extension}'. Only .mc and .osu are supported.")
        };
    }

    public static ChartDocument ImportText(
        string text,
        string sourceName,
        string defaultCreator) =>
        ImportText(text, GetFormatFromSourceName(sourceName), defaultCreator,
            OsuNoteTimingImportMode.PreserveMilliseconds);

    public static ChartDocument ImportText(
        string text,
        string sourceName,
        string defaultCreator,
        OsuNoteTimingImportMode osuNoteTimingMode) =>
        ImportText(text, GetFormatFromSourceName(sourceName), defaultCreator, osuNoteTimingMode);

    public static ChartDocument ImportText(
        string text,
        ChartImportFormat format,
        string defaultCreator)
        => ImportText(text, format, defaultCreator, OsuNoteTimingImportMode.PreserveMilliseconds);

    public static ChartDocument ImportText(
        string text,
        ChartImportFormat format,
        string defaultCreator,
        OsuNoteTimingImportMode osuNoteTimingMode)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(defaultCreator);
        if (!Enum.IsDefined(osuNoteTimingMode))
            throw new ArgumentOutOfRangeException(nameof(osuNoteTimingMode));
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        var document = format switch
        {
            ChartImportFormat.MalodyMc => MalodyMcSerializer.Parse(text, path: null, defaultCreator),
            ChartImportFormat.OsuMania => OsuManiaSerializer.Parse(text, path: null, defaultCreator, osuNoteTimingMode),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown chart import format.")
        };

        // A display name, content URI, or external source path is only a format hint.
        // Persistence code may set FilePath after copying into app-owned storage.
        document.FilePath = null;
        return document;
    }

    public static Task<ChartDocument> ImportAsync(
        Stream source,
        string sourceName,
        string defaultCreator,
        CancellationToken cancellationToken = default) =>
        ImportAsync(source, GetFormatFromSourceName(sourceName), defaultCreator,
            OsuNoteTimingImportMode.PreserveMilliseconds, cancellationToken);

    public static Task<ChartDocument> ImportAsync(
        Stream source,
        string sourceName,
        string defaultCreator,
        OsuNoteTimingImportMode osuNoteTimingMode,
        CancellationToken cancellationToken = default) =>
        ImportAsync(source, GetFormatFromSourceName(sourceName), defaultCreator,
            osuNoteTimingMode, cancellationToken);

    public static async Task<ChartDocument> ImportAsync(
        Stream source,
        ChartImportFormat format,
        string defaultCreator,
        CancellationToken cancellationToken = default)
        => await ImportAsync(source, format, defaultCreator,
            OsuNoteTimingImportMode.PreserveMilliseconds, cancellationToken).ConfigureAwait(false);

    public static async Task<ChartDocument> ImportAsync(
        Stream source,
        ChartImportFormat format,
        string defaultCreator,
        OsuNoteTimingImportMode osuNoteTimingMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(defaultCreator);
        if (!source.CanRead)
            throw new ArgumentException("The chart source stream must be readable.", nameof(source));
        if (format is not (ChartImportFormat.MalodyMc or ChartImportFormat.OsuMania))
            throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown chart import format.");
        if (!Enum.IsDefined(osuNoteTimingMode))
            throw new ArgumentOutOfRangeException(nameof(osuNoteTimingMode));

        cancellationToken.ThrowIfCancellationRequested();
        using var content = new MemoryStream();
        await source.CopyToAsync(content, 81920, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content.GetBuffer();
        var length = checked((int)content.Length);
        var offset = length >= 3 &&
                     bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;
        var text = StrictUtf8.GetString(bytes, offset, length - offset);
        return ImportText(text, format, defaultCreator, osuNoteTimingMode);
    }
}

using LumaChart.Core.Models;

namespace LumaChart.Application.Exporting;

/// <summary>
/// Windows export-picker naming policy. Every export mode starts with the
/// authored original song title plus mapper name; format and mode only select
/// the extension.
/// </summary>
public static class ChartExportFileNamePolicy
{
    public static string SuggestedFileName(ChartDocument document, string extension)
    {
        ArgumentNullException.ThrowIfNull(document);
        var stem = string.IsNullOrWhiteSpace(document.Metadata.TitleOriginal)
            ? document.Metadata.Title
            : document.Metadata.TitleOriginal;
        var title = SafeStem(stem);
        var creator = SafeOptionalPart(document.Metadata.Creator);
        return (creator.Length == 0 ? title : $"{title} - {creator}") +
               NormalizeExtension(extension);
    }

    public static string SafeStem(string? value)
    {
        value ??= string.Empty;
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');
        value = value.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "Untitled" : value;
    }

    private static string SafeOptionalPart(string? value)
    {
        value ??= string.Empty;
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');
        return value.Trim().TrimEnd('.');
    }

    private static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        extension = extension.Trim();
        return extension.StartsWith('.') ? extension : "." + extension;
    }
}

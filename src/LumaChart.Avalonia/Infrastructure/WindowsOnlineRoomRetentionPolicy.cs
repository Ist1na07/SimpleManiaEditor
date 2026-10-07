using LumaChart.Application.Workspaces;

namespace LumaChart.Avalonia.Infrastructure;

public enum WindowsOnlineRoomExitContext
{
    ExplicitTabClose,
    ApplicationClose
}

/// <summary>
/// Recognizes materialized room documents owned by the Windows Avalonia client
/// and converts a restored room tab into an ordinary, fully editable local tab.
/// A process restart must never leave stale room permissions, owner locks, or
/// online creator identities attached to a document without a live room binding.
/// </summary>
public static class WindowsOnlineRoomRetentionPolicy
{
    public static bool ShouldPreserveDraftOnConfirmedExit(
        WindowsOnlineRoomExitContext context) =>
        context == WindowsOnlineRoomExitContext.ApplicationClose;

    public static string CreatePreservedSourcePath(
        string roomDocumentRoot,
        Guid roomId,
        Guid tabId,
        DateTimeOffset preservedAt,
        string sourceExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomDocumentRoot);
        if (roomId == Guid.Empty) throw new ArgumentException("Room ID must not be empty.", nameof(roomId));
        if (tabId == Guid.Empty) throw new ArgumentException("Tab ID must not be empty.", nameof(tabId));
        var extension = sourceExtension.Equals(".osu", StringComparison.OrdinalIgnoreCase)
            ? ".osu"
            : ".mc";
        // Invariant culture: a non-Gregorian current calendar (for example the
        // Thai Buddhist year) must not change the retained file name.
        var fileName = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{preservedAt.UtcDateTime:yyyyMMddTHHmmssfffffffZ}-{tabId:N}{extension}");
        return Path.Combine(
            Path.GetFullPath(roomDocumentRoot),
            "retained",
            roomId.ToString("N"),
            fileName);
    }

    public static bool IsMaterializedRoomSource(string? sourcePath, string roomDocumentRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;
        ArgumentException.ThrowIfNullOrWhiteSpace(roomDocumentRoot);

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(roomDocumentRoot));
            var candidate = Path.GetFullPath(sourcePath);
            var relative = Path.GetRelativePath(root, candidate);
            return !Path.IsPathRooted(relative) &&
                   !relative.Equals(".", StringComparison.Ordinal) &&
                   !relative.Equals("..", StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       ".." + Path.DirectorySeparatorChar,
                       StringComparison.Ordinal) &&
                   !relative.StartsWith(
                       ".." + Path.AltDirectorySeparatorChar,
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static int PrepareForLocalEditing(DesktopChartTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        tab.Session.ReplaceOwnerProtectedRegions([]);
        var attributedObjectCount =
            tab.Session.Document.Notes.Count(note => note.OnlineCreatorUserId != 0) +
            tab.Session.Document.Timing.Count(timing => timing.OnlineCreatorUserId != 0);
        if (attributedObjectCount > 0)
        {
            tab.Session.ApplyRemoteMutation(document =>
            {
                foreach (var note in document.Notes) note.OnlineCreatorUserId = 0;
                foreach (var timing in document.Timing) timing.OnlineCreatorUserId = 0;
            });
        }

        tab.Session.AllowOwnerProtectedEdits = true;
        tab.Session.IsReadOnly = false;
        tab.Session.CanEditMetadata = true;
        tab.Session.CanEditTiming = true;
        tab.Session.CanEditNotes = true;
        return attributedObjectCount;
    }
}

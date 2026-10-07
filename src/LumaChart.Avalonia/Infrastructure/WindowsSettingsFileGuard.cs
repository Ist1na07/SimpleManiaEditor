using System.Collections.Concurrent;
using System.Globalization;

namespace LumaChart.Avalonia.Infrastructure;

/// <summary>
/// Shared read/write guard for the per-user settings files. A settings store
/// used to swallow every load failure and return defaults, so the next save
/// silently replaced the user's real settings with those defaults. With this
/// guard a missing file still means "defaults", but a file that exists and
/// cannot be used is either moved aside as <c>&lt;name&gt;.corrupt-&lt;UTC time&gt;</c>
/// (unparsable content) or protected from being overwritten for the rest of
/// the session (an I/O or permission failure, where the content may be fine).
/// </summary>
public static class WindowsSettingsFileGuard
{
    private const int ReadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(60);
    private static readonly ConcurrentDictionary<string, byte> WriteBlockedPaths =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Reads an existing settings file. Returns false only when it does not
    /// exist. Transient I/O errors (sharing violations from backup or antivirus
    /// tools) are retried; a persistent failure is thrown to the caller's
    /// <see cref="ReportLoadFailure"/>.
    /// </summary>
    public static bool TryReadExisting(string path, out byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        bytes = [];
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                bytes = File.ReadAllBytes(path);
                // The stores parse these bytes directly, and System.Text.Json
                // rejects a UTF-8 byte order mark there. File.ReadAllText used to
                // strip it, so a settings file saved with a BOM (by an editor such
                // as Notepad, or by an older writer) would now be quarantined and
                // reset. Drop the mark before handing the bytes on.
                if (bytes is [0xEF, 0xBB, 0xBF, ..]) bytes = bytes[3..];
                // The file is readable again; parsing decides what happens next.
                WriteBlockedPaths.TryRemove(Key(path), out _);
                return true;
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                Thread.Sleep(RetryDelay * attempt);
            }
        }
    }

    /// <summary>
    /// Handles an exception from a store's Load. I/O and permission failures
    /// leave the file untouched and block writes to it for this session;
    /// content failures move the file aside so a later save cannot destroy it.
    /// </summary>
    public static void ReportLoadFailure(string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (exception is IOException or UnauthorizedAccessException)
        {
            BlockWrites(path);
            WindowsDiagnosticLog.Current?.WriteException(
                "settings-file-unreadable",
                exception,
                fatal: false);
            return;
        }

        if (!TryQuarantine(path, out _)) BlockWrites(path);
        WindowsDiagnosticLog.Current?.WriteException(
            "settings-file-quarantined",
            exception,
            fatal: false);
    }

    /// <summary>
    /// False while an existing settings file could not be verified; the store
    /// must then keep its in-memory settings and skip the write.
    /// </summary>
    public static bool CanWrite(string path) =>
        string.IsNullOrWhiteSpace(path) || !WriteBlockedPaths.ContainsKey(Key(path));

    /// <summary>
    /// Moves an unusable file to <c>&lt;path&gt;.corrupt-&lt;UTC time&gt;</c>. Returns
    /// true when nothing remains at <paramref name="path"/>.
    /// </summary>
    public static bool TryQuarantine(string path, out string quarantinePath)
    {
        quarantinePath = string.Empty;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        for (var suffix = 0; suffix < 100; suffix++)
        {
            var candidate = suffix == 0
                ? $"{path}.corrupt-{stamp}"
                : $"{path}.corrupt-{stamp}-{suffix}";
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            try
            {
                File.Move(path, candidate);
                quarantinePath = candidate;
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(candidate)) continue;
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Records a write failure from a store's Save without letting it escape
    /// to a timer or event handler.
    /// </summary>
    public static void ReportSaveFailure(string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WindowsDiagnosticLog.Current?.WriteException(
            "settings-file-save-failed",
            exception,
            fatal: false);
    }

    private static void BlockWrites(string path) => WriteBlockedPaths[Key(path)] = 0;

    private static string Key(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }
}

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LumaChart.Avalonia.Infrastructure;

internal sealed class WindowsDiagnosticLog : IDisposable
{
    public const string Schema = "lumachart.windows.avalonia.diagnostic.v1";
    public const int MaximumFiles = 8;
    public const long MaximumFileBytes = 1024 * 1024;
    public const int MaximumTextLength = 32 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.CultureInvariant);

    private static readonly Regex JsonSecretPattern = new(
        @"(?i)""(?<key>password|passwd|pwd|token|access_token|refresh_token|secret|authorization|roomPassword|verificationCode|验证码|密码)""\s*:\s*""(?:\\.|[^""\\])*""",
        RegexOptions.CultureInvariant);

    private static readonly Regex NamedSecretPattern = new(
        @"(?i)\b(?<key>password|passwd|pwd|token|access_token|refresh_token|secret|authorization|roomPassword|verificationCode)\b\s*[:=]\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\s,;]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex ChineseSecretPattern = new(
        @"(?<key>验证码|房间密码|密码)\s*[:=：]\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\s,;，；]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant);

    private static readonly Regex LongOpaqueValuePattern = new(
        @"(?<![A-Za-z0-9])[A-Za-z0-9+/=_-]{64,}(?![A-Za-z0-9])",
        RegexOptions.CultureInvariant);

    private readonly object gate = new();
    private readonly string logRoot;
    private readonly string fileStem;
    private FileStream? stream;
    private int partIndex;
    private bool disposed;
    private static WindowsDiagnosticLog? current;

    private WindowsDiagnosticLog(string logRoot)
    {
        this.logRoot = Path.GetFullPath(logRoot);
        // Invariant culture: file names must not follow a non-Gregorian
        // current calendar (for example the Thai Buddhist year).
        var timestamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd'T'HHmmssfff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        fileStem =
            $"diagnostic-{timestamp}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        TryOpenNextPart();
    }

    public string? CurrentLogPath { get; private set; }

    public bool IsAvailable => stream != null;

    /// <summary>
    /// The process log started by <see cref="Start"/>, for infrastructure that
    /// must record a handled failure (for example a settings write) without
    /// having the log passed in. Null before startup and after disposal.
    /// </summary>
    public static WindowsDiagnosticLog? Current => Volatile.Read(ref current);

    public static WindowsDiagnosticLog Start(string logRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logRoot);
        var log = new WindowsDiagnosticLog(logRoot);
        Volatile.Write(ref current, log);
        return log;
    }

    public void WriteLifecycle(string eventName, string severity = "information")
    {
        WriteRecord(new WindowsDiagnosticRecord(
            Schema,
            DateTimeOffset.UtcNow,
            eventName,
            severity,
            Environment.ProcessId,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSDescription,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            null,
            null,
            null,
            null));
    }

    public void WriteException(string eventName, Exception exception, bool fatal)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteRecord(new WindowsDiagnosticRecord(
            Schema,
            DateTimeOffset.UtcNow,
            eventName,
            fatal ? "fatal" : "error",
            Environment.ProcessId,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSDescription,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
            exception.GetType().FullName,
            Sanitize(exception.Message),
            Sanitize(exception.StackTrace),
            fatal));
    }

    internal static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var sanitized = BearerPattern.Replace(value, "Bearer [REDACTED]");
        sanitized = JsonSecretPattern.Replace(
            sanitized,
            match => $"\"{match.Groups["key"].Value}\":\"[REDACTED]\"");
        sanitized = NamedSecretPattern.Replace(
            sanitized,
            match => $"{match.Groups["key"].Value}=[REDACTED]");
        sanitized = ChineseSecretPattern.Replace(
            sanitized,
            match => $"{match.Groups["key"].Value}=[REDACTED]");
        sanitized = JwtPattern.Replace(sanitized, "[REDACTED]");
        sanitized = LongOpaqueValuePattern.Replace(sanitized, "[REDACTED]");
        if (sanitized.Length > MaximumTextLength)
            sanitized = sanitized[..MaximumTextLength] + "…[TRUNCATED]";
        return sanitized;
    }

    private void WriteRecord(WindowsDiagnosticRecord record)
    {
        lock (gate)
        {
            if (disposed || stream == null) return;
            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                var requiredBytes = payload.LongLength + 1;
                if (stream.Position > 0 &&
                    stream.Position + requiredBytes > MaximumFileBytes)
                {
                    stream.Dispose();
                    stream = null;
                    TryOpenNextPart();
                }

                if (stream == null || requiredBytes > MaximumFileBytes) return;
                stream.Write(payload);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                TryCloseStream();
            }
        }
    }

    private void TryOpenNextPart()
    {
        try
        {
            Directory.CreateDirectory(logRoot);
            PruneOldLogs(logRoot);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var candidate = Path.Combine(
                    logRoot,
                    $"{fileStem}-part{partIndex++:00}.jsonl");
                try
                {
                    stream = new FileStream(
                        candidate,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        16 * 1024,
                        FileOptions.WriteThrough);
                    CurrentLogPath = candidate;
                    return;
                }
                catch (IOException)
                {
                    // A second process may have won the filename. The GUID stem
                    // makes this exceptional, but retry without weakening safety.
                }
            }
        }
        catch
        {
            // Diagnostics must never prevent the editor from starting or closing.
        }
    }

    internal static void PruneOldLogs(string logRoot)
    {
        if (!Directory.Exists(logRoot)) return;
        var files = new DirectoryInfo(logRoot)
            .EnumerateFiles("diagnostic-*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(MaximumFiles - 1)
            .ToArray();
        foreach (var file in files)
        {
            try { file.Delete(); }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            TryCloseStream();
        }
        Interlocked.CompareExchange(ref current, null, this);
    }

    private void TryCloseStream()
    {
        try { stream?.Dispose(); }
        catch
        {
        }
        stream = null;
    }

    private sealed record WindowsDiagnosticRecord(
        string Schema,
        DateTimeOffset Utc,
        string Event,
        string Severity,
        int ProcessId,
        string ProcessArchitecture,
        string OperatingSystem,
        string? ApplicationVersion,
        string? ExceptionType,
        string? Message,
        string? StackTrace,
        bool? Fatal);
}

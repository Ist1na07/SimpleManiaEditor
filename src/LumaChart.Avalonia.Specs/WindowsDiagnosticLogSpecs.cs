using System.Text.Json;
using LumaChart.Avalonia.Infrastructure;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsDiagnosticLogSpecs
{
    public static IReadOnlyList<WindowsDiagnosticLogSpecResult> Run(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);
        return
        [
            VerifyStructuredLifecycleAndFatalRecords(root),
            VerifySecretRedaction(),
            VerifyRetentionBound(root),
            VerifyControlledFaultGuard(),
            VerifyFatalNotice()
        ];
    }

    private static WindowsDiagnosticLogSpecResult VerifyStructuredLifecycleAndFatalRecords(
        string root)
    {
        var caseRoot = Path.Combine(root, "structured");
        Directory.CreateDirectory(caseRoot);
        string? logPath;
        using (var log = WindowsDiagnosticLog.Start(caseRoot))
        {
            log.WriteLifecycle("application-starting");
            log.WriteException(
                "application-fatal-exception",
                new InvalidOperationException(
                    "Bearer real-token password=real-password 房间密码：real-room-password"),
                fatal: true);
            logPath = log.CurrentLogPath;
        }

        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            return Fail("windows-diagnostic-structured-jsonl", "Log file was not created.");
        var lines = File.ReadAllLines(logPath);
        if (lines.Length != 2)
            return Fail(
                "windows-diagnostic-structured-jsonl",
                $"Expected 2 records, found {lines.Length}.");
        using var startup = JsonDocument.Parse(lines[0]);
        using var fatal = JsonDocument.Parse(lines[1]);
        var fatalText = fatal.RootElement.GetRawText();
        var passed =
            startup.RootElement.GetProperty("Schema").GetString() ==
            WindowsDiagnosticLog.Schema &&
            startup.RootElement.GetProperty("Event").GetString() ==
            "application-starting" &&
            fatal.RootElement.GetProperty("Event").GetString() ==
            "application-fatal-exception" &&
            fatal.RootElement.GetProperty("Fatal").GetBoolean() &&
            fatalText.Contains("[REDACTED]", StringComparison.Ordinal) &&
            !fatalText.Contains("real-token", StringComparison.Ordinal) &&
            !fatalText.Contains("real-password", StringComparison.Ordinal) &&
            !fatalText.Contains("real-room-password", StringComparison.Ordinal);
        return passed
            ? Pass(
                "windows-diagnostic-structured-jsonl",
                "Lifecycle/fatal JSONL records are valid, flushed, and redacted.")
            : Fail(
                "windows-diagnostic-structured-jsonl",
                "Structured record or redaction contract drifted.");
    }

    private static WindowsDiagnosticLogSpecResult VerifySecretRedaction()
    {
        const string jwt =
            "eyJabcdefghijk.eyJlmnopqrstuv.abcdefghijklmnopqrstuvwxyz";
        var raw =
            "Authorization: Bearer abc.def_ghi " +
            "\"refresh_token\":\"json-secret\" " +
            "password='quoted-secret' 验证码=123456 " +
            jwt;
        var sanitized = WindowsDiagnosticLog.Sanitize(raw) ?? string.Empty;
        var passed =
            sanitized.Contains("[REDACTED]", StringComparison.Ordinal) &&
            sanitized.Contains("\"refresh_token\":\"[REDACTED]\"", StringComparison.Ordinal) &&
            sanitized.Contains("password=[REDACTED]", StringComparison.Ordinal) &&
            sanitized.Contains("验证码=[REDACTED]", StringComparison.Ordinal) &&
            !sanitized.Contains("abc.def_ghi", StringComparison.Ordinal) &&
            !sanitized.Contains("json-secret", StringComparison.Ordinal) &&
            !sanitized.Contains("quoted-secret", StringComparison.Ordinal) &&
            !sanitized.Contains("123456", StringComparison.Ordinal) &&
            !sanitized.Contains(jwt, StringComparison.Ordinal);
        return passed
            ? Pass(
                "windows-diagnostic-secret-redaction",
                "Bearer, JSON, named, Chinese, and JWT secrets are redacted.")
            : Fail(
                "windows-diagnostic-secret-redaction",
                $"Unexpected sanitized value: {sanitized}");
    }

    private static WindowsDiagnosticLogSpecResult VerifyRetentionBound(string root)
    {
        var caseRoot = Path.Combine(root, "retention");
        Directory.CreateDirectory(caseRoot);
        for (var index = 0; index < WindowsDiagnosticLog.MaximumFiles + 4; index++)
        {
            var path = Path.Combine(
                caseRoot,
                $"diagnostic-20000101T000000000Z-1-{index:D32}-part00.jsonl");
            File.WriteAllText(path, "{}\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-index));
        }

        WindowsDiagnosticLog.PruneOldLogs(caseRoot);
        var remaining = Directory.GetFiles(
            caseRoot,
            "diagnostic-*.jsonl",
            SearchOption.TopDirectoryOnly);
        var passed = remaining.Length <= WindowsDiagnosticLog.MaximumFiles - 1;
        return passed
            ? Pass(
                "windows-diagnostic-retention-bound",
                $"Prune retained {remaining.Length} files before opening the active log.")
            : Fail(
                "windows-diagnostic-retention-bound",
                $"Retention left {remaining.Length} old files.");
    }

    private static WindowsDiagnosticLogSpecResult VerifyControlledFaultGuard()
    {
        var arguments = new[] { WindowsFatalErrorPolicy.ControlledFaultArgument };
        var accepted = WindowsFatalErrorPolicy.ShouldTriggerControlledFault(
            arguments,
            "1",
            @"D:\isolated-diagnostics");
        var passed =
            accepted &&
            !WindowsFatalErrorPolicy.ShouldTriggerControlledFault(
                arguments,
                null,
                @"D:\isolated-diagnostics") &&
            !WindowsFatalErrorPolicy.ShouldTriggerControlledFault(
                arguments,
                "1",
                null) &&
            !WindowsFatalErrorPolicy.ShouldTriggerControlledFault(
                Array.Empty<string>(),
                "1",
                @"D:\isolated-diagnostics");
        return passed
            ? Pass(
                "windows-diagnostic-controlled-fault-guard",
                "Fault injection requires the argument, explicit environment gate, and isolated data root.")
            : Fail(
                "windows-diagnostic-controlled-fault-guard",
                "Controlled fault could escape its three-part evidence gate.");
    }

    private static WindowsDiagnosticLogSpecResult VerifyFatalNotice()
    {
        var request = WindowsFatalErrorPolicy.CreateNotice(
            @"D:\isolated-diagnostics\Logs\diagnostic.jsonl");
        var passed =
            request.Icon == WindowsNativeMessageBoxIcon.Error &&
            request.Caption == "LumaChart Studio - 运行错误" &&
            request.Message.Contains(
                @"D:\isolated-diagnostics\Logs\diagnostic.jsonl",
                StringComparison.Ordinal) &&
            !request.Message.Contains("exception", StringComparison.OrdinalIgnoreCase);
        return passed
            ? Pass(
                "windows-diagnostic-fatal-notice",
                "Fatal notice exposes only the diagnostic path and no exception details.")
            : Fail(
                "windows-diagnostic-fatal-notice",
                "Fatal notice content or icon drifted.");
    }

    private static WindowsDiagnosticLogSpecResult Pass(string name, string detail) =>
        new(name, true, detail);

    private static WindowsDiagnosticLogSpecResult Fail(string name, string detail) =>
        new(name, false, detail);
}

internal sealed record WindowsDiagnosticLogSpecResult(
    string Name,
    bool Passed,
    string Detail);

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LumaChart.Avalonia.Infrastructure;

public sealed record WindowsSingleInstanceOpenRequest(
    IReadOnlyList<string> Paths,
    bool Activate,
    int SenderProcessId,
    DateTimeOffset SentUtc);

public sealed record WindowsSingleInstanceStartup(
    bool ContinueStartup,
    bool ForwardedToPrimary,
    WindowsSingleInstanceCoordinator? Coordinator);

public static class WindowsSingleInstancePolicy
{
    public const string ScopeEnvironment =
        "LUMACHART_AVALONIA_SINGLE_INSTANCE_SCOPE";
    public const string DisableEnvironment =
        "LUMACHART_AVALONIA_ALLOW_MULTIPLE_INSTANCES";
    public const string EvidenceForceCoordinateArgument =
        "--evidence-single-instance";
    public const int MaximumForwardedPaths = 256;
    public const int MaximumPayloadBytes = 1024 * 1024;

    private static readonly string[] SupportedExtensions =
        [".mc", ".osu", ".mcz", ".osz"];

    public static bool ShouldCoordinate(
        IReadOnlyList<string> arguments,
        string? allowMultipleInstances)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.Equals(
                allowMultipleInstances,
                "1",
                StringComparison.Ordinal))
            return false;
        if (arguments.Contains(
                EvidenceForceCoordinateArgument,
                StringComparer.OrdinalIgnoreCase))
            return true;

        // Every evidence process owns an explicit isolated profile and several
        // formal gates intentionally run two real Avalonia clients. Production
        // launches never carry these switches.
        return !arguments.Any(argument =>
            argument.StartsWith("--evidence-", StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> ExtractSupportedPaths(
        IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument) ||
                argument.StartsWith("-", StringComparison.Ordinal) ||
                !IsSupportedOpenPath(argument))
                continue;

            string fullPath;
            try { fullPath = Path.GetFullPath(argument); }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!File.Exists(fullPath) || !seen.Add(fullPath)) continue;
            paths.Add(fullPath);
            if (paths.Count == MaximumForwardedPaths) break;
        }

        return paths;
    }

    public static bool IsSupportedOpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path);
        return SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static string ResolveScope(string? requestedScope)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var userIdentity = WindowsIdentity.GetCurrent().User?.Value ??
                           Environment.UserName;
        var userHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(userIdentity)))[..16];
        var requestedHash = string.IsNullOrWhiteSpace(requestedScope)
            ? "default"
            : Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(requestedScope.Trim())))[..16];
        return $"LumaChartStudio.Avalonia.v1.s{session}.{userHash}.{requestedHash}";
    }
}

public sealed class WindowsSingleInstanceCoordinator : IDisposable
{
    private sealed record WireRequest(
        string[] Paths,
        bool Activate,
        int SenderProcessId,
        DateTimeOffset SentUtc);

    private readonly Mutex instanceMarker;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<WindowsSingleInstanceOpenRequest> pending = new();
    private readonly Task listenerTask;
    private bool disposed;

    private WindowsSingleInstanceCoordinator(Mutex instanceMarker, string pipeName)
    {
        this.instanceMarker = instanceMarker;
        this.pipeName = pipeName;
        listenerTask = ListenAsync(lifetime.Token);
    }

    public event EventHandler? RequestAvailable;

    public static async Task<WindowsSingleInstanceStartup> CreateOrForwardAsync(
        IReadOnlyList<string> arguments,
        string? requestedScope,
        string? allowMultipleInstances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!WindowsSingleInstancePolicy.ShouldCoordinate(
                arguments,
                allowMultipleInstances))
            return new WindowsSingleInstanceStartup(
                ContinueStartup: true,
                ForwardedToPrimary: false,
                Coordinator: null);

        var scope = WindowsSingleInstancePolicy.ResolveScope(requestedScope);
        var mutexName = $@"Local\{scope}";
        var pipeName = scope;
        var marker = new Mutex(
            initiallyOwned: false,
            name: mutexName,
            createdNew: out var createdNew);
        if (createdNew)
            return new WindowsSingleInstanceStartup(
                ContinueStartup: true,
                ForwardedToPrimary: false,
                Coordinator: new WindowsSingleInstanceCoordinator(marker, pipeName));

        marker.Dispose();
        var request = new WireRequest(
            WindowsSingleInstancePolicy.ExtractSupportedPaths(arguments).ToArray(),
            Activate: true,
            Environment.ProcessId,
            DateTimeOffset.UtcNow);
        if (await TryForwardAsync(pipeName, request, cancellationToken).ConfigureAwait(false))
            return new WindowsSingleInstanceStartup(
                ContinueStartup: false,
                ForwardedToPrimary: true,
                Coordinator: null);

        // The prior process may have exited after this launch observed its
        // marker. Retry marker ownership once instead of creating two editors.
        marker = new Mutex(
            initiallyOwned: false,
            name: mutexName,
            createdNew: out createdNew);
        if (createdNew)
            return new WindowsSingleInstanceStartup(
                ContinueStartup: true,
                ForwardedToPrimary: false,
                Coordinator: new WindowsSingleInstanceCoordinator(marker, pipeName));

        marker.Dispose();
        throw new IOException(
            "另一个 LumaChart Studio Avalonia 实例正在启动，但暂时无法接收文件。");
    }

    public bool TryDequeue(out WindowsSingleInstanceOpenRequest request) =>
        pending.TryDequeue(out request!);

    private static async Task<bool> TryForwardAsync(
        string pipeName,
        WireRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        if (payload.Length > WindowsSingleInstancePolicy.MaximumPayloadBytes)
            throw new InvalidOperationException("单实例文件请求超过安全上限。");

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var connectTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(350));
                await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

                var header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await client.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);

                var acknowledgment = new byte[1];
                await ReadExactlyAsync(
                        client,
                        acknowledgment,
                        cancellationToken)
                    .ConfigureAwait(false);
                return acknowledgment[0] == 1;
            }
            catch (Exception exception) when (
                (exception is IOException or TimeoutException or OperationCanceledException) &&
                !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    inBufferSize: 4096,
                    outBufferSize: 4096);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var header = new byte[sizeof(int)];
                await ReadExactlyAsync(server, header, cancellationToken).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > WindowsSingleInstancePolicy.MaximumPayloadBytes)
                    throw new InvalidDataException("Invalid single-instance payload length.");

                var payload = new byte[length];
                await ReadExactlyAsync(server, payload, cancellationToken).ConfigureAwait(false);
                var wire = JsonSerializer.Deserialize<WireRequest>(payload) ??
                           throw new InvalidDataException("Invalid single-instance payload.");
                var paths = WindowsSingleInstancePolicy
                    .ExtractSupportedPaths(wire.Paths ?? [])
                    .ToArray();
                pending.Enqueue(new WindowsSingleInstanceOpenRequest(
                    paths,
                    wire.Activate,
                    wire.SenderProcessId,
                    wire.SentUtc));

                await server.WriteAsync(
                        new byte[] { 1 },
                        cancellationToken)
                    .ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);
                RequestAvailable?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A sender can disappear between connect and payload write.
                // The next server instance remains available.
            }
            catch (InvalidDataException) when (!cancellationToken.IsCancellationRequested)
            {
                // Malformed local requests are rejected without terminating
                // the editor or poisoning the next request.
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                // Anything else from one request (malformed JSON, a null path
                // list, a failing RequestAvailable handler) must not end the
                // listener: later launches would then neither forward their
                // files nor be able to start, since this process owns the marker.
                WindowsDiagnosticLog.Current?.WriteException(
                    "single-instance-request-rejected",
                    exception,
                    fatal: false);
                // A persistent failure (for example creating the pipe) must not
                // turn into a hot loop.
                try { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(
                    buffer[total..],
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try { listenerTask.GetAwaiter().GetResult(); }
        catch
        {
            // Shutdown must complete even if the listener faulted; rethrowing
            // here replaced the application's normal exit with a crash.
        }
        lifetime.Dispose();
        instanceMarker.Dispose();
    }
}

using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using LumaChart.Application.Workspaces;
using LumaChart.Avalonia.Services;
using LumaChart.Online;
using LumaChart.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LumaChart.Avalonia.Specs;

internal static class WindowsRoomResourceResilienceSpecs
{
    private static readonly Guid RoomId =
        Guid.Parse("a7100000-0000-0000-0000-000000000001");

    public static async Task<WindowsRoomResourceResilienceSpecResult> RunAsync(
        string isolationRoot)
    {
        try
        {
            return new WindowsRoomResourceResilienceSpecResult(
                "windows-room-resource-real-http-interruption-concurrency-atomic-repair",
                true,
                await VerifyAsync(isolationRoot).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return new WindowsRoomResourceResilienceSpecResult(
                "windows-room-resource-real-http-interruption-concurrency-atomic-repair",
                false,
                exception.ToString());
        }
    }

    private static async Task<string> VerifyAsync(string isolationRoot)
    {
        const string token = "windows-room-resource-resilience-token";
        var root = Path.GetFullPath(Path.Combine(
            isolationRoot,
            Guid.NewGuid().ToString("N")));
        var cacheRoot = Path.Combine(root, "cache");
        var documentRoot = Path.Combine(root, "documents");
        var scratchRoot = Path.Combine(root, "scratch");
        Directory.CreateDirectory(cacheRoot);

        var sourceController = new AvaloniaWorkspaceController(
            Path.Combine(root, "source-library"),
            Path.Combine(root, "source-editing"),
            "resource-resilience-owner");
        await sourceController.InitializeAsync().ConfigureAwait(false);
        var sourceTab = await sourceController
            .CreateAsync("Room Resource Resilience", "resource-resilience-owner")
            .ConfigureAwait(false);
        await sourceController.PersistAsync().ConfigureAwait(false);
        var chartBytes = await File.ReadAllBytesAsync(sourceTab.WorkingPath).ConfigureAwait(false);
        var keyBytes = new byte[256 * 1024];
        for (var index = 0; index < keyBytes.Length; index++)
            keyBytes[index] = (byte)((index * 31 + 17) & 0xff);

        var chartHash = Hash(chartBytes);
        var keyHash = Hash(keyBytes);
        var cachedKeyCorruption = MutateSameLength(keyBytes, 0x5a);
        var downloadedKeyCorruption = MutateSameLength(keyBytes, 0xa5);
        var alternateKeyBytes = MutateSameLength(keyBytes, 0x7f);
        var chartCorruption = MutateSameLength(chartBytes, 0x3c);
        var alternateKeyHash = Hash(alternateKeyBytes);
        Require(!Hash(cachedKeyCorruption).Equals(keyHash, StringComparison.OrdinalIgnoreCase),
            "same-length cache corruption fixture did not change SHA-256");
        Require(!cachedKeyCorruption.AsSpan().SequenceEqual(downloadedKeyCorruption),
            "cached and downloaded corruption fixtures must be distinguishable");
        Require(!alternateKeyHash.Equals(keyHash, StringComparison.OrdinalIgnoreCase),
            "same-name replacement fixture did not change SHA-256");

        var entries = new[]
        {
            new OnlineRoomResourceEntryDto("working.mc", chartBytes.LongLength, chartHash),
            new OnlineRoomResourceEntryDto("keys/hit.wav", keyBytes.LongLength, keyHash)
        };
        var manifest = new OnlineRoomResourceManifestDto(
            entries.Sum(entry => entry.Length),
            new string('0', 64),
            "working.mc",
            entries);
        var keyCachePath = Path.Combine(cacheRoot, keyHash);
        var chartCachePath = Path.Combine(cacheRoot, chartHash);
        await File.WriteAllBytesAsync(keyCachePath, cachedKeyCorruption).ConfigureAwait(false);

        var server = new ResourceFaultServer(
            token,
            manifest,
            chartHash,
            chartBytes,
            keyHash,
            keyBytes,
            downloadedKeyCorruption,
            alternateKeyHash,
            alternateKeyBytes);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        server.Map(app);
        await app.StartAsync().ConfigureAwait(false);
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.SingleOrDefault(value =>
                value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            if (!Uri.TryCreate(
                    address?.TrimEnd('/') + "/",
                    UriKind.Absolute,
                    out var endpoint) || endpoint == null)
            {
                throw new InvalidOperationException(
                    "Kestrel did not publish its loopback resource endpoint");
            }

            using var client = new HttpClient
            {
                BaseAddress = endpoint,
                Timeout = TimeSpan.FromSeconds(15)
            };
            var session = new OnlineAccountSession(
                client,
                endpoint,
                new MemoryTokenStore(token),
                eventClient: client);
            Require(await session.TryRestoreAsync().ConfigureAwait(false),
                "the real HTTP room-resource fixture did not restore its token");
            var service = new WindowsOnlineRoomResourceService(
                cacheRoot,
                documentRoot,
                scratchRoot);

            var initial = await service.InspectAsync(manifest).ConfigureAwait(false);
            Require(initial.MissingBytes == manifest.TotalBytes && initial.ReusedCount == 0,
                "same-length cached corruption was incorrectly reused");
            Require((await File.ReadAllBytesAsync(keyCachePath).ConfigureAwait(false))
                    .AsSpan().SequenceEqual(cachedKeyCorruption),
                "inspection destroyed the previous cache entry before atomic replacement");

            var interrupted = await CaptureFailureAsync(() =>
                    service.DownloadAndMaterializeAsync(session, RoomId, manifest))
                .ConfigureAwait(false);
            Require(interrupted is HttpRequestException or IOException or InvalidDataException,
                $"mid-stream disconnect returned an unexpected failure: {interrupted.GetType().Name}");
            Require(server.ChartDownloadAttempts == 1 && server.KeyDownloadAttempts == 0,
                "the first real HTTP attempt did not stop at the injected chart disconnect");
            Require(!File.Exists(chartCachePath),
                "an interrupted blob was published to the content-addressed cache");
            RequireNoPartFiles(root, "mid-stream disconnect");

            var wrongHash = await CaptureFailureAsync(() =>
                    service.DownloadAndMaterializeAsync(session, RoomId, manifest))
                .ConfigureAwait(false);
            Require(wrongHash is InvalidDataException &&
                    wrongHash.Message.Contains("SHA-256", StringComparison.Ordinal),
                "same-length HTTP corruption did not fail the streaming SHA-256 gate");
            Require(server.ChartDownloadAttempts == 2 && server.KeyDownloadAttempts == 1,
                "the second real HTTP attempt did not reach the injected key corruption");
            Require(Hash(await File.ReadAllBytesAsync(chartCachePath).ConfigureAwait(false))
                    .Equals(chartHash, StringComparison.OrdinalIgnoreCase),
                "the valid chart cache was not committed before the later key failure");
            Require((await File.ReadAllBytesAsync(keyCachePath).ConfigureAwait(false))
                    .AsSpan().SequenceEqual(cachedKeyCorruption),
                "a failed same-length download replaced the prior cache entry non-atomically");
            RequireNoPartFiles(root, "same-length SHA failure");

            var tamperInjected = 0;
            var progress = new InlineProgress<WindowsOnlineRoomTransferProgress>(value =>
            {
                if (value.CurrentPath.Equals("keys/hit.wav", StringComparison.OrdinalIgnoreCase) &&
                    value.TransferredBytes == value.TotalBytes &&
                    Interlocked.Exchange(ref tamperInjected, 1) == 0)
                {
                    File.WriteAllBytes(chartCachePath, chartCorruption);
                }
            });
            var materialized = await service.DownloadAndMaterializeAsync(
                    session,
                    RoomId,
                    manifest,
                    progress)
                .ConfigureAwait(false);

            Require(tamperInjected == 1,
                "the post-inspection same-length cache tamper was not injected");
            Require(server.ChartDownloadAttempts == 3 && server.KeyDownloadAttempts == 2,
                "materialization did not automatically re-download the post-inspection corruption once");
            RequireNoPartFiles(root, "successful recovery");
            Require(Hash(await File.ReadAllBytesAsync(chartCachePath).ConfigureAwait(false))
                    .Equals(chartHash, StringComparison.OrdinalIgnoreCase) &&
                    Hash(await File.ReadAllBytesAsync(keyCachePath).ConfigureAwait(false))
                    .Equals(keyHash, StringComparison.OrdinalIgnoreCase),
                "the recovered content-addressed cache is not structurally complete");

            var materializedChart = Path.Combine(materialized.DirectoryPath, "working.mc");
            var materializedKey = Path.Combine(materialized.DirectoryPath, "keys", "hit.wav");
            Require(Hash(await File.ReadAllBytesAsync(materializedChart).ConfigureAwait(false))
                    .Equals(chartHash, StringComparison.OrdinalIgnoreCase) &&
                    Hash(await File.ReadAllBytesAsync(materialized.ChartPath).ConfigureAwait(false))
                    .Equals(chartHash, StringComparison.OrdinalIgnoreCase) &&
                    Hash(await File.ReadAllBytesAsync(materializedKey).ConfigureAwait(false))
                    .Equals(keyHash, StringComparison.OrdinalIgnoreCase),
                "recovered cache bytes changed during verified materialization");
            Require(Directory.EnumerateDirectories(documentRoot).Count() == 1,
                "failed materialization staging directories were not cleaned");

            var openedController = new AvaloniaWorkspaceController(
                Path.Combine(root, "opened-library"),
                Path.Combine(root, "opened-editing"),
                "resource-resilience-viewer");
            await openedController.InitializeAsync().ConfigureAwait(false);
            var opened = await openedController.OpenAsync(materialized.ChartPath).ConfigureAwait(false);
            Require(opened.Session.Document.Metadata.Title == "Room Resource Resilience",
                "the recovered real MC did not reopen structurally");

            var aclDocumentRoot = Path.Combine(root, "acl-documents");
            Directory.CreateDirectory(aclDocumentRoot);
            var aclService = new WindowsOnlineRoomResourceService(
                cacheRoot,
                aclDocumentRoot,
                Path.Combine(root, "acl-scratch"));
            var aclDirectory = new DirectoryInfo(aclDocumentRoot);
            var aclSecurity = aclDirectory.GetAccessControl(AccessControlSections.Access);
            var originalAcl = aclSecurity.GetSecurityDescriptorSddlForm(
                AccessControlSections.Access);
            var currentUser = WindowsIdentity.GetCurrent().User ??
                              throw new InvalidOperationException(
                                  "the Windows ACL spec could not resolve the current user SID");
            aclSecurity.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.CreateFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny));
            aclDirectory.SetAccessControl(aclSecurity);
            Exception aclFailure;
            var chartAttemptsBeforeAcl = server.ChartDownloadAttempts;
            var keyAttemptsBeforeAcl = server.KeyDownloadAttempts;
            try
            {
                aclFailure = await CaptureFailureAsync(() =>
                        aclService.DownloadAndMaterializeAsync(
                            session,
                            RoomId,
                            manifest))
                    .ConfigureAwait(false);
                Require(aclFailure is UnauthorizedAccessException,
                    $"real NTFS destination denial returned {aclFailure.GetType().Name} instead of UnauthorizedAccessException");
                Require(!aclFailure.Message.Contains(
                        "本地房间资源缓存在物化期间发生变化",
                        StringComparison.Ordinal),
                    "real NTFS destination denial was misclassified as cache corruption");
                Require(server.ChartDownloadAttempts == chartAttemptsBeforeAcl &&
                        server.KeyDownloadAttempts == keyAttemptsBeforeAcl,
                    "real NTFS destination denial caused an unnecessary resource download");
                Require(!Directory.EnumerateDirectories(aclDocumentRoot).Any(),
                    "real NTFS destination denial left a partial room document folder");
                RequireNoPartFiles(root, "real NTFS destination denial");
            }
            finally
            {
                var restoreSecurity = aclDirectory.GetAccessControl(
                    AccessControlSections.Access);
                restoreSecurity.SetSecurityDescriptorSddlForm(
                    originalAcl,
                    AccessControlSections.Access);
                aclDirectory.SetAccessControl(restoreSecurity);
            }

            var aclRecovered = await aclService.DownloadAndMaterializeAsync(
                    session,
                    RoomId,
                    manifest)
                .ConfigureAwait(false);
            Require(server.ChartDownloadAttempts == chartAttemptsBeforeAcl &&
                    server.KeyDownloadAttempts == keyAttemptsBeforeAcl,
                "permission recovery did not reuse the already verified resource cache");
            Require(Hash(await File.ReadAllBytesAsync(aclRecovered.ChartPath)
                        .ConfigureAwait(false))
                    .Equals(chartHash, StringComparison.OrdinalIgnoreCase) &&
                    Hash(await File.ReadAllBytesAsync(Path.Combine(
                                aclRecovered.DirectoryPath,
                                "keys",
                                "hit.wav"))
                            .ConfigureAwait(false))
                        .Equals(keyHash, StringComparison.OrdinalIgnoreCase),
                "permission recovery did not materialize exact cached room resources");
            Require(Directory.EnumerateDirectories(aclDocumentRoot).Count() == 1,
                "permission recovery retained an unexpected number of room document folders");
            RequireNoPartFiles(root, "real NTFS destination permission recovery");

            var concurrentCacheRoot = Path.Combine(root, "concurrent-cache");
            var concurrentDocumentRoot = Path.Combine(root, "concurrent-documents");
            var concurrentScratchRoot = Path.Combine(root, "concurrent-scratch");
            var concurrentEntries = new[]
            {
                new OnlineRoomResourceEntryDto("working.mc", chartBytes.LongLength, chartHash),
                new OnlineRoomResourceEntryDto("keys/hit.wav", keyBytes.LongLength, keyHash),
                new OnlineRoomResourceEntryDto("keys/hit-copy.wav", keyBytes.LongLength, keyHash)
            };
            var concurrentManifest = new OnlineRoomResourceManifestDto(
                concurrentEntries.Sum(entry => entry.Length),
                new string('0', 64),
                "working.mc",
                concurrentEntries);
            var concurrentService = new WindowsOnlineRoomResourceService(
                concurrentCacheRoot,
                concurrentDocumentRoot,
                concurrentScratchRoot);
            var chartAttemptsBeforeConcurrent = server.ChartDownloadAttempts;
            var keyAttemptsBeforeConcurrent = server.KeyDownloadAttempts;
            server.ValidResponseDelayMilliseconds = 200;
            WindowsOnlineRoomMaterialization[] concurrentMaterializations;
            try
            {
                concurrentMaterializations = await Task.WhenAll(
                        concurrentService.DownloadAndMaterializeAsync(
                            session,
                            RoomId,
                            concurrentManifest),
                        concurrentService.DownloadAndMaterializeAsync(
                            session,
                            RoomId,
                            concurrentManifest))
                    .ConfigureAwait(false);
            }
            finally
            {
                server.ValidResponseDelayMilliseconds = 0;
            }

            Require(server.ChartDownloadAttempts == chartAttemptsBeforeConcurrent + 1 &&
                    server.KeyDownloadAttempts == keyAttemptsBeforeConcurrent + 1,
                "concurrent duplicate joins did not single-flight each content hash");
            Require(concurrentMaterializations.Select(item => item.DirectoryPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
                "concurrent room joins did not retain independent materialized workspaces");
            foreach (var concurrentMaterialization in concurrentMaterializations)
            {
                var concurrentChart = Path.Combine(
                    concurrentMaterialization.DirectoryPath,
                    "working.mc");
                var concurrentKey = Path.Combine(
                    concurrentMaterialization.DirectoryPath,
                    "keys",
                    "hit.wav");
                var concurrentKeyCopy = Path.Combine(
                    concurrentMaterialization.DirectoryPath,
                    "keys",
                    "hit-copy.wav");
                Require(Hash(await File.ReadAllBytesAsync(concurrentChart).ConfigureAwait(false))
                            .Equals(chartHash, StringComparison.OrdinalIgnoreCase) &&
                        Hash(await File.ReadAllBytesAsync(concurrentKey).ConfigureAwait(false))
                            .Equals(keyHash, StringComparison.OrdinalIgnoreCase) &&
                        Hash(await File.ReadAllBytesAsync(concurrentKeyCopy).ConfigureAwait(false))
                            .Equals(keyHash, StringComparison.OrdinalIgnoreCase),
                    "same-hash cross-name resources changed during concurrent materialization");
            }
            RequireNoPartFiles(root, "concurrent duplicate join");

            var concurrentChartCachePath = Path.Combine(concurrentCacheRoot, chartHash);
            await File.WriteAllBytesAsync(concurrentChartCachePath, chartCorruption)
                .ConfigureAwait(false);
            var chartAttemptsBeforeOccupied = server.ChartDownloadAttempts;
            var keyAttemptsBeforeOccupied = server.KeyDownloadAttempts;
            await using (var heldCache = new FileStream(
                             concurrentChartCachePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read))
            {
                var occupiedFailure = await CaptureFailureAsync(async () =>
                    {
                        _ = await concurrentService.DownloadAndMaterializeAsync(
                                session,
                                RoomId,
                                concurrentManifest)
                            .ConfigureAwait(false);
                    })
                    .ConfigureAwait(false);
                Require(occupiedFailure is IOException or UnauthorizedAccessException,
                    $"occupied invalid cache returned an unexpected {occupiedFailure.GetType().Name}");
                Require(server.ChartDownloadAttempts == chartAttemptsBeforeOccupied + 1 &&
                        server.KeyDownloadAttempts == keyAttemptsBeforeOccupied,
                    "occupied invalid cache retry downloaded an unexpected resource set");
                Require(!Hash(await File.ReadAllBytesAsync(concurrentChartCachePath)
                                .ConfigureAwait(false))
                            .Equals(chartHash, StringComparison.OrdinalIgnoreCase),
                    "failed occupied-cache replacement destroyed the diagnostic corruption");
                RequireNoPartFiles(root, "occupied invalid cache failure");
            }

            var repairedAfterRelease = await concurrentService.DownloadAndMaterializeAsync(
                    session,
                    RoomId,
                    concurrentManifest)
                .ConfigureAwait(false);
            Require(server.ChartDownloadAttempts == chartAttemptsBeforeOccupied + 2 &&
                    server.KeyDownloadAttempts == keyAttemptsBeforeOccupied,
                "released invalid cache was not repaired with exactly one chart download");
            Require(Hash(await File.ReadAllBytesAsync(concurrentChartCachePath)
                        .ConfigureAwait(false))
                    .Equals(chartHash, StringComparison.OrdinalIgnoreCase) &&
                    Hash(await File.ReadAllBytesAsync(repairedAfterRelease.ChartPath)
                        .ConfigureAwait(false))
                    .Equals(chartHash, StringComparison.OrdinalIgnoreCase),
                "released occupied cache did not recover exact chart bytes");
            RequireNoPartFiles(root, "occupied invalid cache recovery");

            var replacementEntries = new[]
            {
                new OnlineRoomResourceEntryDto("working.mc", chartBytes.LongLength, chartHash),
                new OnlineRoomResourceEntryDto(
                    "keys/hit.wav",
                    alternateKeyBytes.LongLength,
                    alternateKeyHash)
            };
            var replacementManifest = new OnlineRoomResourceManifestDto(
                replacementEntries.Sum(entry => entry.Length),
                new string('0', 64),
                "working.mc",
                replacementEntries);
            var alternateAttemptsBefore = server.AlternateKeyDownloadAttempts;
            var chartAttemptsBeforeReplacement = server.ChartDownloadAttempts;
            var replacement = await concurrentService.DownloadAndMaterializeAsync(
                    session,
                    RoomId,
                    replacementManifest)
                .ConfigureAwait(false);
            Require(server.ChartDownloadAttempts == chartAttemptsBeforeReplacement &&
                    server.AlternateKeyDownloadAttempts == alternateAttemptsBefore + 1,
                "same-name different-content manifest did not reuse chart and fetch only replacement content");
            Require(Hash(await File.ReadAllBytesAsync(Path.Combine(
                            replacement.DirectoryPath,
                            "keys",
                            "hit.wav"))
                        .ConfigureAwait(false))
                    .Equals(alternateKeyHash, StringComparison.OrdinalIgnoreCase) &&
                    Hash(await File.ReadAllBytesAsync(Path.Combine(concurrentCacheRoot, keyHash))
                        .ConfigureAwait(false))
                    .Equals(keyHash, StringComparison.OrdinalIgnoreCase),
                "same-name different-content materialization overwrote content-addressed history");
            RequireNoPartFiles(root, "same-name different-content materialization");

            return "real Kestrel/OnlineAccountSession/filesystem: mid-stream disconnect and same-length " +
                   "HTTP corruption left no .part or bad publication; cached same-length tamper was rejected; " +
                   "post-inspection tamper triggered one verified re-download; real NTFS destination denial " +
                   "propagated without cache misclassification and recovered from the verified cache; concurrent duplicate joins " +
                   "single-flighted same-hash cross-name content; occupied invalid cache remained atomic and " +
                   "repaired after release; same-name replacement preserved content-addressed history; real MC reopen passed";
        }
        finally
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Exception> CaptureFailureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return exception;
        }
        throw new InvalidOperationException("the injected room-resource failure unexpectedly succeeded");
    }

    private static void RequireNoPartFiles(string root, string phase)
    {
        var parts = Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).ToArray();
        Require(parts.Length == 0,
            $"{phase} left temporary resource files: {string.Join(", ", parts)}");
    }

    private static byte[] MutateSameLength(byte[] source, byte mask)
    {
        var result = source.ToArray();
        Require(result.Length > 0, "the corruption fixture must not be empty");
        result[result.Length / 2] ^= mask;
        return result;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ResourceFaultServer(
        string token,
        OnlineRoomResourceManifestDto manifest,
        string chartHash,
        byte[] chartBytes,
        string keyHash,
        byte[] keyBytes,
        byte[] downloadedKeyCorruption,
        string alternateKeyHash,
        byte[] alternateKeyBytes)
    {
        private int chartDownloadAttempts;
        private int keyDownloadAttempts;
        private int alternateKeyDownloadAttempts;
        private int validResponseDelayMilliseconds;

        public int ChartDownloadAttempts => Volatile.Read(ref chartDownloadAttempts);
        public int KeyDownloadAttempts => Volatile.Read(ref keyDownloadAttempts);
        public int AlternateKeyDownloadAttempts => Volatile.Read(ref alternateKeyDownloadAttempts);
        public int ValidResponseDelayMilliseconds
        {
            get => Volatile.Read(ref validResponseDelayMilliseconds);
            set => Volatile.Write(ref validResponseDelayMilliseconds, value);
        }

        public void Map(WebApplication app)
        {
            app.MapGet("/api/me", (HttpContext context) =>
            {
                RequireBearer(context);
                return Results.Json(new OnlineUserProfileDto(
                    97,
                    "resource-resilience-owner",
                    "Resource Resilience Owner",
                    "user",
                    "default",
                    DateTimeOffset.Parse("2026-07-25T00:00:00Z"),
                    12345));
            });
            app.MapGet($"/api/rooms/{RoomId:D}/resources", (HttpContext context) =>
            {
                RequireBearer(context);
                return Results.Json(manifest);
            });
            app.MapGet(
                $"/api/rooms/{RoomId:D}/resources/files/{{sha256}}",
                async (HttpContext context, string sha256) =>
                    await DownloadAsync(context, sha256).ConfigureAwait(false));
        }

        private async Task DownloadAsync(HttpContext context, string sha256)
        {
            RequireBearer(context);
            if (sha256.Equals(chartHash, StringComparison.OrdinalIgnoreCase))
            {
                var attempt = Interlocked.Increment(ref chartDownloadAttempts);
                if (attempt == 1)
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength = chartBytes.LongLength;
                    var partialLength = Math.Max(1, chartBytes.Length / 2);
                    await context.Response.Body.WriteAsync(
                            chartBytes.AsMemory(0, partialLength),
                            context.RequestAborted)
                        .ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted)
                        .ConfigureAwait(false);
                    context.Abort();
                    return;
                }

                await WriteBytesAsync(context, chartBytes).ConfigureAwait(false);
                return;
            }

            if (sha256.Equals(keyHash, StringComparison.OrdinalIgnoreCase))
            {
                var attempt = Interlocked.Increment(ref keyDownloadAttempts);
                await WriteBytesAsync(
                        context,
                        attempt == 1 ? downloadedKeyCorruption : keyBytes)
                    .ConfigureAwait(false);
                return;
            }

            if (sha256.Equals(alternateKeyHash, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref alternateKeyDownloadAttempts);
                await WriteBytesAsync(context, alternateKeyBytes).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }

        private async Task WriteBytesAsync(HttpContext context, byte[] bytes)
        {
            var delay = ValidResponseDelayMilliseconds;
            if (delay > 0)
                await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = bytes.LongLength;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted)
                .ConfigureAwait(false);
        }

        private void RequireBearer(HttpContext context)
        {
            if (!context.Request.Headers.Authorization.ToString()
                    .Equals($"Bearer {token}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("room-resource resilience bearer token mismatch");
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class MemoryTokenStore(string token) : ISessionTokenStore
    {
        private string? value = token;

        public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(value);
        }

        public ValueTask SaveAsync(string token, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value = token;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value = null;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record WindowsRoomResourceResilienceSpecResult(
    string Name,
    bool Passed,
    string Detail);

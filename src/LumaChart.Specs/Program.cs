using LumaChart.Application.Editing;
using LumaChart.Application.Collaboration;
using LumaChart.Core.Editing;
using LumaChart.Core.Analysis;
using LumaChart.Core.IO;
using LumaChart.Core.Models;
using LumaChart.Core.Timing;
using LumaChart.Core.Validation;
#if LUMACHART_SERVER
using LumaChart.Server;
using LumaChart.Server.Security;
#endif
using LumaChart.Online;
using LumaChart.Storage;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
// The server-backed specs below only build where LumaChart.Server is present;
// LumaChart.Specs.csproj defines LUMACHART_SERVER when it is, and the
// client-only open-source snapshot builds and runs the rest without it.
#if LUMACHART_SERVER
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
#endif

if (args is ["--rework", var chartPath])
{
    var chart = Path.GetExtension(chartPath).Equals(".osu", StringComparison.OrdinalIgnoreCase)
        ? OsuManiaSerializer.Load(chartPath)
        : MalodyMcSerializer.Load(chartPath);
    var result = ReworkDifficultyAnalyzer.Analyze(chart, overallDifficulty: chart.Metadata.OverallDifficulty);
    Console.WriteLine($"{result.StarRating:R}|{result.LongNoteRatio:R}|{result.NoteCount}|{result.ReferenceDifficulty}");
    return 0;
}
if (args is ["--normalize-osu", var sourcePath, var destinationPath])
{
    OsuManiaSerializer.Save(OsuManiaSerializer.Load(sourcePath), destinationPath);
    return 0;
}

var tests = new (string Name, Action Body)[]
{
    ("Beat position parsing and snapping", TestBeatPosition),
    ("Timing map round-trip", TestTimingMap),
    ("Playtest Scroll distance is piecewise and BPM-aware", TestScrollSpeedMap),
    ("Malody delay aligns the audio timeline", TestMalodyDelayAlignment),
    ("Malody .mc compatibility", TestMalody),
    ("Malody game-index export envelope stays compact and type-safe", TestMalodyGameIndexCompatibility),
    ("Non-key Malody and osu modes are rejected", TestUnsupportedGameModes),
    ("Malody sparse and legacy columns stay unambiguous", TestMalodyColumnNumbering),
    ("Malody mixed official/custom effects do not duplicate", TestMalodyMixedEffectRoundTrip),
    ("Online creator identity never leaks into chart exports", TestOnlineCreatorIdentityIsRuntimeOnly),
    ("Non-finite editor values normalize before save", TestNonFiniteNormalization),
    ("New offline charts use the guest creator identity", TestNewChartDefaultCreator),
    ("Malody BGM note compatibility", TestMalodyBackgroundMusicNote),
    ("osu!mania compatibility", TestOsu),
    ("osu duplicate fields and sections are tolerated", TestOsuDuplicateFields),
    ("osu unspecified preview normalizes for MC editing", TestOsuUnspecifiedPreview),
    ("osu OD and HP round-trip/export override", TestOsuDifficultySettings),
    ("osu offset and BPM round-trip", TestOsuOffsetRoundTrip),
    ("osu BPM changes support exact backward seeking", TestOsuBpmChangeBackwardSeek),
    ("osu import calibration preserves offsets and common subdivisions", OsuBeatCalibrationSpecs.Run),
    ("Pure stream/text chart imports preserve cancellation path and ownership boundaries", ChartDocumentImporterSpecs.Run),
    ("Batch move is one undo operation", TestBatchMoveUndo),
    ("Cross-platform application editing preserves offline save and history semantics", TestApplicationEditingLifecycle),
    ("Local document workspace preserves Library tabs atomic saves and recovery", LocalDocumentWorkspaceSpecs.Run),
    ("Cross-platform basic editing closes Tap LN selection timing metadata and validation", BasicChartEditingSpecs.Run),
    ("Timeline direct seeks align to the selected grid and wheel navigation moves one cell", TimelineGridNavigationSpecs.Run),
    ("Chart asset import and managed cache cleanup keep workspace boundaries safe", WorkspaceUtilitySpecs.Run),
    ("Pure variable-tempo estimation merges stable BPM windows and maps musical change points", VariableTempoEstimatorSpecs.Run),
    ("Online undo preserves other members and rejects same-object conflicts", TestScopedOnlineUndo),
    ("Online granular permissions reject only forbidden chart categories", TestGranularOnlinePermissions),
#if LUMACHART_SERVER
    ("Cross-platform online codec preserves the complete room chart contract", TestOnlineChartSyncCodec),
#endif
    ("Cross-platform auth transport preserves session HTTP contracts", TestOnlineAuthTransport),
#if LUMACHART_SERVER
    ("Cross-platform room transport preserves room HTTP contracts", TestOnlineRoomTransport),
    ("Cross-platform resource transport preserves manifest and blob streaming contracts", TestOnlineResourceTransport),
    ("Cross-platform resource upload transport preserves plan blob and commit contracts", TestOnlineResourceUploadTransport),
    ("Cross-platform chart transport preserves every room chart REST contract", TestOnlineChartTransport),
#endif
    ("Cross-platform chart event transport preserves streaming SSE contracts", TestOnlineChartEventTransport),
#if LUMACHART_SERVER
    ("Cross-platform room lifecycle transport preserves membership and live HTTP contracts", TestOnlineRoomLifecycleTransport),
    ("Room lease policy preserves refresh display and cleanup boundaries", RoomLeasePolicySpecs.Run),
    ("Cross-platform account transport preserves profile and avatar HTTP contracts", TestOnlineAccountTransport),
    ("Cross-platform name-color transport submits only an owned-tag reference", TestOnlineNameColorTransport),
    ("Cross-platform community transport preserves discovery follow and avatar contracts", TestOnlineCommunityTransport),
    ("Cross-platform follow notifications preserve cursor, paging and plain-text contracts", TestOnlineNotificationTransport),
    ("Cross-platform admin transport preserves owner user and avatar contracts", TestOnlineAdminTransport),
#endif
    ("Room chart reducer preserves pending intent across acknowledgements and generations", TestRoomChartSyncReducer),
    ("Room chart coordinator schedules retries feeds and rollback deterministically", RoomChartSyncCoordinatorSpecs.Run),
    ("Secure token state and auth errors fail closed", TestOnlineAuthFailureBoundaries),
#if LUMACHART_SERVER
    ("Room chart operations preserve metadata and key-count changes", TestRoomChartOperationContract),
    ("Room chart server rejects invalid idempotency, permissions and resource paths", TestRoomChartServerValidation),
    ("Room chart lock selections toggle exact unions and differences atomically", TestRoomChartLockSelectionToggle),
    ("Room chart generations, rollback IDs and numeric bounds stay safe", TestRoomChartSafetyBoundaries),
    ("Room operation receipts compact safely and byte budgets preserve idempotent retries", TestRoomChartReceiptCompactionAndBudget),
    ("Room rollback deltas reset every client's pending history exactly once", TestRoomRollbackAuthoritativeBaseline),
    ("Room resource plan deduplicates identical blobs across paths", TestRoomResourceDeduplication),
    ("Room resource blobs detect same-length corruption and recover by reupload", TestRoomResourceIntegrityRecovery),
    ("Room resource cleanup releases inactive and expired upload plans", TestRoomResourceUploadPlanCleanup),
    ("Unreferenced room blobs retain a full 24-hour reuse window", TestRoomResourceBlobRetention),
    ("Room resource commits reject stale plans and freeze the first published manifest", TestRoomResourcePlanConcurrency),
    ("Room resource cache enforces a cross-room blob file ceiling", TestRoomResourceBlobFileLimit),
    ("Room resource uploads isolate slow streams, share per-room limits and enforce total timeouts", TestRoomResourceUploadStreaming),
    ("Legacy room ZIP validation blocks traversal, duplicates, entry floods and compression bombs", TestLegacyRoomArchiveValidation),
    ("Large chart requests are assigned to bounded concurrency gates", TestChartRequestConcurrencyClassification),
    ("Room chart event subscriptions survive concurrent empty-holder turnover", TestRoomChartChangeFeedConcurrency),
#endif
    ("Remote room edits safely interrupt an LN tail gesture", TestRemoteEditInterruptsInteractiveMutation),
    ("LN and rice-note conversions preserve grid boundaries", TestNoteTypeConversions),
    ("Owner locks apply in batches and protect notes from members", TestOwnerProtectedBatch),
#if LUMACHART_SERVER
    ("Owner selection regions block alternate subdivisions and effects", TestOwnerProtectedRegion),
#endif
    ("Owner lock selection keeps single objects exact and multi-object selections continuous", TestOwnerLockSelectionPlanner),
#if LUMACHART_SERVER
    ("Public account names reject impersonation and obfuscated profanity", TestPublicNamePolicy),
    ("Public room and chat text handles Unicode and rejects hidden controls", TestPublicTextPolicy),
    ("Authentication captcha and private email contracts stay enforced", TestAuthenticationContracts),
    ("Rate-limit sessions require prior authentication and stay bounded", TestRateLimitSessionRegistry),
    ("Admin user tags normalize, color and migrate safely", TestUserTagPolicy),
#endif
    ("Failed edits leave the document untouched", TestFailedMutationRollback),
    ("Draft notes persist without exporting as official notes", TestDraftNotes),
    ("Chart validator finds overlaps and crossing long notes", TestChartValidator),
    ("4K Rework difficulty ignores draft notes", TestReworkDifficulty),
    ("Rework rejects non-4K charts", TestReworkRejectsNon4K),
    ("Large chart normalization", TestLargeChart),
    ("Core model, format and editing fixes keep timing, resources and round-trips exact", CoreFixSpecs.Run),
    ("Workspace, library and managed-storage fixes fail closed and never lose recoverable data", WorkspaceFixSpecs.Run),
#if LUMACHART_SERVER
    ("Server chart gates partition by account and room, and IPv6 limits group by /64", ServerFixSpecs.RunGatesAndPartitions),
    ("Server authentication hardening bounds captcha, login, room password, email-code and hashing abuse", ServerFixSpecs.RunAuthenticationHardening),
    ("Server chart state stays below jsonb limits and single-pass replay matches per-batch replay", ServerFixSpecs.RunChartStateAndReplay),
    ("Server resource publication, cleanup, avatar GC and charting time stay consistent", ServerFixSpecs.RunStorageAndAccounting),
#endif
    ("Client sync fixes never drop local edits and keep reads, deadlines and sessions consistent", SyncFixSpecs.Run)
};

static void TestNewChartDefaultCreator()
{
    Assert(new ChartDocument().Metadata.Creator == "guest",
        "a new offline chart exposed the operating-system account instead of guest");
}

#if LUMACHART_SERVER
static void TestPublicNamePolicy()
{
    Assert(PublicNamePolicy.TryValidateUsername("MCPsDEPE", out var username, out _)
           && username == "MCPsDEPE", "a normal username was rejected");
    Assert(PublicNamePolicy.TryValidateDisplayName("谱师_01", out var displayName, out _)
           && displayName == "谱师_01", "a normal Unicode display name was rejected");

    foreach (var forbidden in new[]
             {
                 "admin", "Administrator", "r.o_o-t", "Luma Chart Official",
                 "f.u_c-k", "d1ck", "PUSSY"
             })
    {
        Assert(!PublicNamePolicy.TryValidateDisplayName(forbidden, out _, out _),
            $"forbidden display name was accepted: {forbidden}");
    }

    Assert(!PublicNamePolicy.TryValidateDisplayName("good\nname", out _, out _),
        "a display name containing a newline was accepted");
}

static void TestPublicTextPolicy()
{
    Assert(PublicTextPolicy.TryNormalizeSingleLine("  房间  😀   test  ", 1, 40, out var room, out _) &&
           room == "房间 😀 test", "valid room text was not normalized predictably");
    Assert(PublicTextPolicy.TryNormalizeSingleLine("hello\r\nworld", 1, 500, out var chat, out _) &&
           chat == "hello world", "chat whitespace was not normalized to one line");
    Assert(!PublicTextPolicy.TryNormalizeSingleLine("safe\u202Eexe", 1, 40, out _, out _),
        "a bidi override control was accepted");
    Assert(!PublicTextPolicy.TryNormalizeSingleLine("zero\u200Bwidth", 1, 40, out _, out _),
        "a zero-width spoofing character was accepted");
    Assert(PublicTextPolicy.TryValidateSecret("房间@1234!", 4, 64, out _),
        "ordinary Unicode/symbol room passwords were rejected");
}

static void TestAuthenticationContracts()
{
    Assert(OsuOAuthService.ResolveSetting("", " environment-value ") == "environment-value" &&
           OsuOAuthService.ResolveSetting(" configured-value ", "environment-value") ==
           "configured-value" &&
           OsuOAuthService.ResolveSetting("   ", null) == string.Empty,
        "blank appsettings values prevented osu OAuth from reading the production environment");

    var captcha = new CaptchaService();
    Assert(captcha.TryCreate("captcha-spec", out var challenge, out var retryAfter) && retryAfter == 0,
        "the first captcha challenge was unexpectedly rate limited");
    Assert(!captcha.TryCreate("captcha-spec", out _, out retryAfter) && retryAfter > 0,
        "the captcha refresh cooldown was not enforced per client");

    var entriesField = typeof(CaptchaService).GetField(
        "entries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    var entries = entriesField?.GetValue(captcha)
                  ?? throw new InvalidOperationException("captcha entry storage was not found");
    var countProperty = entries.GetType().GetProperty("Count")
                        ?? throw new InvalidOperationException("captcha entry count was not found");
    Assert((int)countProperty.GetValue(entries)! == 1, "the captcha challenge was not stored");
    Assert(!captcha.Validate(challenge.Id, "wrong"), "an incorrect captcha answer was accepted");
    Assert((int)countProperty.GetValue(entries)! == 0,
        "an attempted captcha challenge remained reusable");
    Assert(!captcha.Validate(challenge.Id, "wrong"), "a consumed captcha challenge was reusable");

    var login = new LoginRequest("user", "password", Guid.NewGuid(), "12345");
    Assert(login.CaptchaId != Guid.Empty && login.CaptchaAnswer == "12345",
        "the login contract lost its mandatory captcha fields");
    Assert(EmailAddressPolicy.TryNormalize("User@EXAMPLE.COM", out var normalized) &&
           normalized == "User@example.com" &&
           EmailAddressPolicy.NormalizeForLookup(normalized) ==
           EmailAddressPolicy.NormalizeForLookup("user@example.com"),
        "email canonicalization no longer supports case-insensitive uniqueness");
    Assert(!EmailAddressPolicy.TryNormalize("safe\u202E@example.com", out _),
        "an email containing a bidi formatting control was accepted");
    Assert(EmailVerificationCodePolicy.IsValid("012345"),
        "a valid six-digit email code was rejected");
    foreach (var invalidCode in new[] { "12345", "1234567", "12345a", " 12345", "１２３４５６", "12345\n" })
        Assert(!EmailVerificationCodePolicy.IsValid(invalidCode),
            $"an invalid email verification code was accepted: {invalidCode.Length} chars");

    const string unmaskedEmail = "NewAddress@example.com";
    var maskedEmail = EmailAddressPolicy.MaskForNotification(unmaskedEmail);
    Assert(maskedEmail == "N***s@example.com" &&
           !maskedEmail.Contains("NewAddress", StringComparison.OrdinalIgnoreCase),
        "the old-address notification exposed the complete new email address");

    var cooldown = new EmailVerificationRequestCooldown();
    var now = DateTimeOffset.UtcNow;
    var writes = 0;
    var concurrentResults = new EmailQueueResult[32];
    Parallel.For(0, concurrentResults.Length, index =>
    {
        var address = index % 2 == 0 ? "User@example.com" : "user@EXAMPLE.COM";
        concurrentResults[index] = cooldown.TryQueue(
            address, "register", now, () =>
            {
                Interlocked.Increment(ref writes);
                return true;
            });
    });
    Assert(concurrentResults.Count(result => result == EmailQueueResult.Queued) == 1 && writes == 1 &&
           concurrentResults.Count(result => result == EmailQueueResult.Suppressed) == 31,
        "concurrent registration-code requests bypassed the per-email/purpose cooldown");
    Assert(cooldown.TryQueue("user@example.com", "reset", now, () => true) == EmailQueueResult.Queued,
        "one verification purpose incorrectly cooled down another purpose");
    Assert(cooldown.TryQueue("user@example.com", "register", now.AddSeconds(61), () => true) ==
           EmailQueueResult.Queued,
        "an expired email-code cooldown did not reopen");
    Assert(cooldown.TryQueue("other@example.com", "register", now, () => false) == EmailQueueResult.Full &&
           cooldown.TryQueue("other@example.com", "register", now, () => true) == EmailQueueResult.Queued,
        "a full queue incorrectly consumed the email-code cooldown");

    Assert(!EmailVerificationDispatchPolicy.ShouldSend("register", emailExists: true) &&
           EmailVerificationDispatchPolicy.ShouldSend("register", emailExists: false) &&
           EmailVerificationDispatchPolicy.ShouldSend("reset", emailExists: true) &&
           !EmailVerificationDispatchPolicy.ShouldSend("reset", emailExists: false) &&
           !EmailVerificationDispatchPolicy.ShouldSend("change_email", emailExists: true) &&
           EmailVerificationDispatchPolicy.ShouldSend("change_email", emailExists: false),
        "background email eligibility exposed or inverted an account-existence branch");
    Assert(ChangeEmailDisclosurePolicy.Resolve(emailCodeConsumed: false, targetEmailTaken: false) ==
           ChangeEmailStatus.InvalidEmailCode &&
           ChangeEmailDisclosurePolicy.Resolve(emailCodeConsumed: false, targetEmailTaken: true) ==
           ChangeEmailStatus.InvalidEmailCode,
        "change-email availability was disclosed before a verification code was consumed");
    Assert(ChangeEmailDisclosurePolicy.Resolve(emailCodeConsumed: true, targetEmailTaken: true) ==
           ChangeEmailStatus.EmailTaken &&
           ChangeEmailDisclosurePolicy.Resolve(emailCodeConsumed: true, targetEmailTaken: false) ==
           ChangeEmailStatus.Success,
        "change-email availability was not classified after successful verification");
    Assert(typeof(UserProfile).GetProperty(nameof(UserProfile.Email)) != null,
        "the private current-user profile no longer exposes its bound email");
    Assert(typeof(PlayerProfile).GetProperty("Email") == null,
        "a public player profile exposed a private email address");
}

static void TestRateLimitSessionRegistry()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
    var registry = new RateLimitSessionRegistry(clock, TimeSpan.FromMinutes(5), maximumEntries: 2);
    var realIp = IPAddress.Parse("203.0.113.19");
    var expectedIpPartition = "ip:203.0.113.19";

    Assert(registry.ResolvePartition("Bearer random-a", realIp) == expectedIpPartition &&
           registry.ResolvePartition("Bearer random-b", realIp) == expectedIpPartition &&
           registry.ResolvePartition("not-bearer", realIp) == expectedIpPartition &&
           registry.ResolvePartition("Bearer random-c", realIp.MapToIPv6()) == expectedIpPartition &&
           registry.Count == 0,
        "unknown bearer values created distinct or registered rate-limit partitions");

    const string authenticatedToken = "authenticated-session-token";
    var authenticatedHash = RateLimitSessionRegistry.ComputeTokenHash(authenticatedToken);
    Assert(registry.ResolvePartition($"Bearer {authenticatedToken}", realIp) == expectedIpPartition,
        "an unregistered bearer token received a session partition");
    Assert(registry.RegisterAuthenticatedSession(authenticatedHash) &&
           registry.ResolvePartition($"Bearer {authenticatedToken}", realIp) == $"session:{authenticatedHash}",
        "a database-authenticated token did not receive a stable session partition");
    Assert(!registry.RegisterAuthenticatedSession("not-a-token-hash"),
        "an invalid token hash was admitted to the authenticated-session registry");

    clock.Advance(TimeSpan.FromMinutes(4));
    registry.RegisterAuthenticatedSession(authenticatedHash);
    clock.Advance(TimeSpan.FromMinutes(2));
    Assert(registry.ResolvePartition($"Bearer {authenticatedToken}", realIp) == $"session:{authenticatedHash}",
        "successful reauthentication did not refresh the registry TTL");
    clock.Advance(TimeSpan.FromMinutes(4));
    Assert(registry.ResolvePartition($"Bearer {authenticatedToken}", realIp) == expectedIpPartition &&
           registry.Count == 0,
        "an expired authenticated session retained its limiter partition");

    registry.RegisterAuthenticatedSession(authenticatedHash);
    Assert(registry.RemoveAuthenticatedSession(authenticatedHash) &&
           registry.ResolvePartition($"Bearer {authenticatedToken}", realIp) == expectedIpPartition,
        "logout did not remove the authenticated limiter session");

    var boundedRegistry = new RateLimitSessionRegistry(clock, TimeSpan.FromHours(1), maximumEntries: 2);
    var tokens = new[] { "bounded-token-1", "bounded-token-2", "bounded-token-3" };
    var hashes = tokens.Select(RateLimitSessionRegistry.ComputeTokenHash).ToArray();
    boundedRegistry.RegisterAuthenticatedSession(hashes[0]);
    clock.Advance(TimeSpan.FromSeconds(1));
    boundedRegistry.RegisterAuthenticatedSession(hashes[1]);
    clock.Advance(TimeSpan.FromSeconds(1));
    boundedRegistry.RegisterAuthenticatedSession(hashes[2]);
    Assert(boundedRegistry.Count == 2 &&
           boundedRegistry.ResolvePartition($"Bearer {tokens[0]}", realIp) == expectedIpPartition &&
           boundedRegistry.ResolvePartition($"Bearer {tokens[1]}", realIp) == $"session:{hashes[1]}" &&
           boundedRegistry.ResolvePartition($"Bearer {tokens[2]}", realIp) == $"session:{hashes[2]}",
        "the registry exceeded its bound or failed to evict its oldest session");

    clock.Advance(TimeSpan.FromHours(2));
    var replacementToken = "bounded-token-4";
    boundedRegistry.RegisterAuthenticatedSession(RateLimitSessionRegistry.ComputeTokenHash(replacementToken));
    Assert(boundedRegistry.Count == 1 && boundedRegistry.PruneExpired() == 0,
        "expired registry entries were not removed during bounded maintenance");
}

static void TestUserTagPolicy()
{
    var source = new[]
    {
        new LumaChart.Server.UserTag("Beta Tester", "#33aacc", true),
        new LumaChart.Server.UserTag("Sponsor", "#F2B84B")
    };
    Assert(UserTagPolicy.TryNormalize(source, out var normalized, out _) &&
           normalized[0].Color == "#33AACC" && normalized[0].IsNameColor && normalized[1].Name == "Sponsor",
        "valid colored tags were not normalized");
    var encoded = UserTagPolicy.Encode(normalized);
    var decoded = UserTagPolicy.Decode(encoded);
    Assert(decoded.SequenceEqual(normalized) && UserTagPolicy.Summary(encoded) == "Beta Tester · Sponsor",
        "multi-tag JSON did not round-trip");
    Assert(UserTagPolicy.Decode("Legacy").Single() == new LumaChart.Server.UserTag("Legacy", "#D2AE6E"),
        "legacy single tag did not migrate");
    Assert(!UserTagPolicy.TryNormalize([
            new LumaChart.Server.UserTag("Beta", "#FFFFFF"),
            new LumaChart.Server.UserTag("beta", "#000000")], out _, out _),
        "case-insensitive duplicate tags were accepted");
    Assert(!UserTagPolicy.TryNormalize([new LumaChart.Server.UserTag("unsafe\u202E", "red")], out _, out _),
        "unsafe tag text/color was accepted");
    Assert(!UserTagPolicy.TryNormalize([
            new LumaChart.Server.UserTag("Beta", "#FFFFFF", true),
            new LumaChart.Server.UserTag("Sponsor", "#F2B84B", true)], out _, out _),
        "multiple name-color tags were accepted");

    Assert(UserTagPolicy.TrySelectNameColor(normalized, " sponsor ", out var selected) &&
           !selected[0].IsNameColor && selected[1].IsNameColor &&
           normalized[0].IsNameColor && !normalized[1].IsNameColor,
        "a user could not select one of their own tags without mutating the source list");
    Assert(!UserTagPolicy.TrySelectNameColor(normalized, "Not owned", out _) &&
           !UserTagPolicy.TrySelectNameColor(normalized, "unsafe\u202E", out _),
        "an absent or unsafe tag was accepted as the user's name color");
    Assert(UserTagPolicy.TrySelectNameColor(selected, null, out var cleared) &&
           cleared.All(tag => !tag.IsNameColor) &&
           UserTagPolicy.Decode(UserTagPolicy.Encode(selected)).Single(tag => tag.Name == "Sponsor").IsNameColor,
        "clearing or persisting a self-selected name color lost its exact tag marker");
}
#endif

var failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"PASS  {name}"); }
    catch (Exception exception) { failed++; Console.WriteLine($"FAIL  {name}\n      {exception.Message}"); }
}
Console.WriteLine($"\n{tests.Length - failed}/{tests.Length} specs passed.");
return failed;

static void TestBeatPosition()
{
    Assert(BeatPosition.TryParse("3:2/4", out var beat), "fraction did not parse");
    Assert(beat.ToString() == "3:2/4", "an explicit fraction lost its authored denominator");
    var equivalent = new BeatPosition(3, 1, 2);
    Assert(beat == equivalent && beat.CompareTo(equivalent) == 0 &&
           beat.GetHashCode() == equivalent.GetHashCode() &&
           new HashSet<BeatPosition> { beat, equivalent }.Count == 1,
        "equivalent fraction spellings no longer have mathematical equality/hash semantics");
    var defaultBeat = default(BeatPosition);
    Assert(defaultBeat == BeatPosition.Zero && defaultBeat.CompareTo(new BeatPosition(1, 0, 1)) < 0 &&
           defaultBeat.GetHashCode() == BeatPosition.Zero.GetHashCode() && defaultBeat.ToString() == "0:0/1",
        "default BeatPosition escaped zero comparison/hash/format semantics");
    Assert(BeatPosition.TryParse("2.25", out beat) && Math.Abs(beat.Value - 2.25) < 1e-8, "decimal did not parse");
    Assert(BeatPosition.FromDouble(1.126).Snap(4).ToString() == "1:1/4", "snap is incorrect");
    Assert(BeatPosition.FromDouble(.5).Snap(6).ToString() == "0:3/6",
        "snap reduced the selected subdivision expression");
    Assert(BeatPosition.SnapToCellCenter(1d / 6 + .001, 6).ToString() == "0:3/12" &&
           BeatPosition.SnapToCellCenter(4d / 6 + .001, 6).ToString() == "0:9/12",
        "cell-center snapping did not store (2k-1)/(2N)");
    Assert(new BeatPosition(0, -1, 4).ToString() == "-1:3/4", "negative beat normalization is incorrect");
    Assert(!BeatPosition.TryParse("NaN", out _), "non-finite beat text was accepted");
    Assert(!BeatPosition.TryParse("2147483647:2147483647/1", out _), "overflowing beat text escaped TryParse");
    AssertThrows<ArgumentOutOfRangeException>(
        () => BeatPosition.Preserve(0, 1, BeatPosition.MaximumPreservedDenominator + 1),
        "an expression beyond the online denominator ceiling was accepted");
    var closeLower = new BeatPosition(2_000_000_000, 1, 2_000_000_000);
    var closeUpper = new BeatPosition(2_000_000_000, 1, 1_999_999_999);
    Assert(closeLower.CompareTo(closeUpper) < 0, "large rational beats lost exact ordering");
}

static void TestNoteTypeConversions()
{
    var ln = new ChartNote
    {
        Lane = 0,
        Beat = BeatPosition.FromDouble(1),
        EndBeat = BeatPosition.FromDouble(2)
    };
    var boundary = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(3) };
    var chart = new ChartDocument { Notes = [ln, boundary] };
    var filled = NoteTypeTransformService.ConvertLongNotesToTaps(
        chart, new HashSet<Guid> { ln.Id }, 4, LongNoteToTapMode.Fill);
    Assert(filled.ConvertedCount == 1 && filled.CreatedCount == 4,
        "LN fill did not create one rice note per 1/4 grid line through the tail");
    Assert(chart.Notes.Count(note => note.Lane == 0 && note.Beat.Value is >= 1 and <= 2) == 5,
        "LN fill positions were incomplete");

    var head = new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(4) };
    var next = new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(5) };
    var conversionChart = new ChartDocument { Notes = [head, next] };
    var converted = NoteTypeTransformService.ConvertTapsToLongNotes(
        conversionChart, new HashSet<Guid> { head.Id }, 4);
    Assert(converted.ConvertedCount == 1 && head.EndBeat?.CompareTo(BeatPosition.FromDouble(4.75)) == 0,
        "rice note tail did not stop one grid unit before the next note");

    var last = new ChartNote { Lane = 2, Beat = BeatPosition.FromDouble(8) };
    conversionChart.Notes.Add(last);
    var skipped = NoteTypeTransformService.ConvertTapsToLongNotes(
        conversionChart, new HashSet<Guid> { last.Id }, 4);
    Assert(skipped.SkippedCount == 1 && !last.IsHold,
        "last rice note without a same-column successor became an invalid LN");
}

static void TestTimingMap()
{
    var map = new TimingMap([
        new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120, Delay = 250 },
        new TimingEvent { Beat = new BeatPosition(4, 0, 1), Bpm = 180 }
    ]);
    Assert(Math.Abs(map.BeatToMilliseconds(new BeatPosition(2, 0, 1)) - 750) < .01, "Malody delay sign conversion failed");
    var source = new BeatPosition(6, 1, 2);
    Assert(Math.Abs(map.MillisecondsToBeat(map.BeatToMilliseconds(source)).Value - source.Value) < .001, "timing round-trip drifted");
    var sameBeat = new TimingMap([
        new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120, Delay = 100 },
        new TimingEvent { Beat = BeatPosition.Zero, Bpm = 180, Delay = 50 }
    ]);
    Assert(Math.Abs(sameBeat.BpmAt(BeatPosition.Zero) - 180) < .001, "same-beat BPM did not use source order");
    Assert(Math.Abs(sameBeat.BeatToMilliseconds(BeatPosition.Zero) + 50) < .001,
        "overridden same-beat delay was applied more than once");
    var sectionDelay = new TimingMap([
        new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 },
        new TimingEvent { Beat = BeatPosition.FromDouble(4), Bpm = 120, Delay = 500 },
        new TimingEvent { Beat = BeatPosition.FromDouble(8), Bpm = 120, Delay = 250 }
    ]);
    Assert(Math.Abs(sectionDelay.BeatToMilliseconds(BeatPosition.FromDouble(8)) - 4750) < .01,
        "later Malody section delays were applied as repeated negative song offsets");
    Assert(Math.Abs(
            sectionDelay.MillisecondsToBeat(
                sectionDelay.BeatToMilliseconds(BeatPosition.FromDouble(10))).Value - 10) < .001,
        "section-delay timing did not round-trip");
    Assert(sectionDelay.MillisecondsToBeat(2250) == BeatPosition.FromDouble(4),
        "milliseconds inside a positive section delay advanced past the BPM boundary");
    var nonMonotonicDelay = new TimingMap([
        new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 },
        new TimingEvent { Beat = BeatPosition.FromDouble(4), Bpm = 120 },
        new TimingEvent { Beat = BeatPosition.FromDouble(8), Bpm = 120, Delay = -3500 }
    ]);
    Assert(Math.Abs(nonMonotonicDelay.MillisecondsToBeat(1000).Value - 9) < .001,
        "negative section delay did not select the latest matching timing segment");
}

static void TestScrollSpeedMap()
{
    var events = new List<TimingEvent>
    {
        new() { Beat = BeatPosition.Zero, Bpm = 120 },
        new() { Beat = BeatPosition.FromDouble(4), Bpm = 240 },
        new() { Beat = BeatPosition.FromDouble(1), Effect = TimingEffectKind.Sign, EffectValue = 99 },
        new() { Beat = BeatPosition.FromDouble(1.5), Effect = TimingEffectKind.Custom,
            EffectFieldName = "showbar", EffectValue = 0 },
        new() { Beat = BeatPosition.FromDouble(2), Effect = TimingEffectKind.Scroll, EffectValue = .5 },
        new() { Beat = BeatPosition.FromDouble(6), Effect = TimingEffectKind.Scroll, EffectValue = 2 }
    };
    var scroll = new ScrollSpeedMap(events);
    Assert(Math.Abs(scroll.PositionAt(BeatPosition.FromDouble(2)) - 2) < .001, "default Scroll was not 1x");
    Assert(Math.Abs(scroll.MultiplierAt(BeatPosition.FromDouble(1.75)) - 1) < .001,
        "non-Scroll effects changed playtest scroll speed");
    Assert(Math.Abs(scroll.PositionAt(BeatPosition.FromDouble(6)) - 4) < .001, "0.5x Scroll distance was incorrect");
    Assert(Math.Abs(scroll.PositionAt(BeatPosition.FromDouble(7)) - 6) < .001, "2x Scroll distance was incorrect");
    var sameBeat = new ScrollSpeedMap([
        new TimingEvent { Beat = BeatPosition.Zero, Effect = TimingEffectKind.Scroll, EffectValue = .5 },
        new TimingEvent { Beat = BeatPosition.Zero, Effect = TimingEffectKind.Scroll, EffectValue = 1.75 }
    ]);
    Assert(Math.Abs(sameBeat.MultiplierAt(BeatPosition.Zero) - 1.75) < .001,
        "same-beat Scroll did not use source order");

    var timing = new TimingMap(events);
    var beforeStart = timing.BeatToMilliseconds(BeatPosition.FromDouble(2));
    var beforeEnd = timing.BeatToMilliseconds(BeatPosition.FromDouble(4));
    var afterStart = timing.BeatToMilliseconds(BeatPosition.FromDouble(4));
    var afterEnd = timing.BeatToMilliseconds(BeatPosition.FromDouble(6));
    Assert(Math.Abs((beforeEnd - beforeStart) - 1000) < .01, "120 BPM duration was incorrect");
    Assert(Math.Abs((afterEnd - afterStart) - 500) < .01, "240 BPM did not double visual motion per second");
    Assert(Math.Abs(scroll.Distance(BeatPosition.FromDouble(2), BeatPosition.FromDouble(4)) -
                    scroll.Distance(BeatPosition.FromDouble(4), BeatPosition.FromDouble(6))) < .001,
        "BPM incorrectly changed beat distance instead of its time rate");
}

static void TestMalodyDelayAlignment()
{
    // Regression from KuusouBlueMoon: its first LN is beat 2.5, BPM is 135,
    // and Malody offset is +343 ms. The strong audio onset is at ~0.770 s.
    var map = new TimingMap([
        new TimingEvent { Beat = BeatPosition.Zero, Bpm = 135, Delay = 343 }
    ]);
    var firstNoteMilliseconds = map.BeatToMilliseconds(BeatPosition.FromDouble(2.5));
    Assert(Math.Abs(firstNoteMilliseconds - 768.111111) < .01,
        "Malody delay made the audio lead the first note by twice the offset");

    // Negative delay moves beat zero into the audio instead of before it. This is
    // the mapping used by waveform/spectrogram sampling and by playback seeking,
    // so both directions must retain the exact same 311 ms origin.
    var negativeOffsetMap = new TimingMap([
        new TimingEvent { Beat = BeatPosition.Zero, Bpm = 170, Delay = -311 }
    ]);
    Assert(Math.Abs(negativeOffsetMap.BeatToMilliseconds(BeatPosition.Zero) - 311) < .01,
        "negative Malody delay used the wrong audio origin");
    Assert(Math.Abs(negativeOffsetMap.MillisecondsToBeat(311).Value) < .0001,
        "negative Malody delay did not round-trip at beat zero");
}

static void TestMalody()
{
    const string json = """
    {
      "meta":{"id":42,"creator":"Mapper","cover":"cover.jpg","background":"bg.jpg","version":"Hard","preview":40000,"mode":0,"aimode":"keep","mode_ext":{"column":4,"custom_lane":7},"song":{"id":99,"title":"Track","titleorg":"トラック","artist":"Artist","artistorg":"アーティスト","file":"song.ogg","bpm":150,"license":"test"}},
      "time":[{"beat":[0,0,1],"bpm":150,"delay":125,"signature":"keep"}],
      "effect":[{"beat":[1,0,1],"scroll":1.5},{"beat":[2,0,1],"jump":2},{"beat":[3,0,1],"hs":0.8}],
      "note":[{"beat":[1,0,1],"column":0,"group":4,"skin":"keep"},{"beat":[2,0,1],"endbeat":[3,0,1],"column":3}],
      "noteref":[{"beat":[0,4,12],"column":8,"draft_tag":3},{"beat":[0,4,12],"column":10},{"beat":[0,8,12],"endbeat":[1,0,1],"column":9}],
      "custom":{"preserve":true}
    }
    """;
    var chart = MalodyMcSerializer.Parse(json);
    Assert(chart.KeyCount == 4 && chart.Notes.Count == 5 && chart.Notes.Count(n => n.IsDraft) == 3, "notes/noteref did not import");
    Assert(chart.Metadata.TitleOriginal == "トラック" && chart.Metadata.ArtistOriginal == "アーティスト" &&
           chart.Metadata.Bpm == 150, "Malody original metadata or chart BPM did not import");
    Assert(chart.Metadata.CoverFile == "cover.jpg" && chart.Metadata.BackgroundFile == "bg.jpg" && chart.Metadata.PreviewTime == 40000, "Malody media metadata did not import");
    Assert(chart.Notes.Any(n => n.IsDraft && n.DraftColumn == 10), "noteref column did not import");
    var authoredDraftBeat = chart.Notes.Single(n => n.IsDraft && n.DraftColumn == 8).Beat;
    Assert(authoredDraftBeat is { Numerator: 4, Denominator: 12 },
        "Malody import reduced an authored Beat denominator");
    Assert(chart.Notes.Any(n => !n.IsDraft && n.Group == 4) && chart.Notes.Any(n => !n.IsDraft && n.IsHold), "official note fields did not import");
    Assert(chart.Timing.Count(t => t.Effect.HasValue) == 3, "official effects did not import");
    chart.Metadata.Bpm = 175.25;
    var serialized = MalodyMcSerializer.Serialize(chart);
    var serializedRoot = System.Text.Json.Nodes.JsonNode.Parse(serialized)!.AsObject();
    var serializedMeta = serializedRoot["meta"]!.AsObject();
    var serializedSong = serializedMeta["song"]!.AsObject();
    Assert(serialized.Contains("\"scroll\"") && serialized.Contains("\"jump\"") && serialized.Contains("\"hs\""), "official effects did not export");
    Assert(serialized.Contains("\"custom\""), "unknown root data was lost");
    Assert(serializedRoot["note"]!.AsArray().Any(node => node?["group"]?.GetValue<int>() == 4),
        "note group did not export");
    Assert(serializedSong["titleorg"]?.GetValue<string>() == "トラック" &&
           serializedMeta["preview"]?.GetValue<int>() == 40000 &&
           serializedSong["bpm"]?.GetValue<double>() == 175.25, "Malody metadata did not export");
    Assert(serializedMeta["id"]?.GetValue<int>() == 42 && serializedSong["id"]?.GetValue<int>() == 99 &&
           serializedMeta["aimode"]?.GetValue<string>() == "keep" &&
           serializedMeta["mode_ext"]?["custom_lane"]?.GetValue<int>() == 7 &&
           serializedSong["license"]?.GetValue<string>() == "test" &&
           serializedRoot["time"]?[0]?["signature"]?.GetValue<string>() == "keep" &&
           serializedRoot["note"]!.AsArray().Any(node => node?["skin"]?.GetValue<string>() == "keep") &&
           serializedRoot["noteref"]!.AsArray().Any(node => node?["draft_tag"]?.GetValue<int>() == 3),
        "unknown Malody metadata or per-note fields were lost during round-trip");
    Assert(!serialized.Contains("SV 曲线", StringComparison.OrdinalIgnoreCase), "removed SV generator leaked into output");
    var metadataRoundTrip = MalodyMcSerializer.Parse(serialized);
    Assert(metadataRoundTrip.Metadata.Bpm == 175.25 &&
           metadataRoundTrip.Timing.First(timing => timing.Bpm.HasValue).Bpm == 150,
        "independent chart BPM was replaced by the first Timing BPM during Malody round-trip");
    var expressionRoundTrip = metadataRoundTrip.Notes
        .Single(n => n.IsDraft && n.DraftColumn == 8).Beat;
    Assert(expressionRoundTrip is { Numerator: 4, Denominator: 12 },
        "Malody save/reopen did not retain the authored Beat expression");
}

static void TestMalodyGameIndexCompatibility()
{
    var chart = new ChartDocument
    {
        Metadata = new ChartMetadata { AudioFile = "audio.ogg" },
        Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 132.1, Delay = 173.848846 }]
    };
    var serialized = MalodyMcSerializer.Serialize(chart);
    Assert(!serialized.Contains('\r') && !serialized.Contains('\n'),
        "Malody export was not compact single-line JSON");

    var root = System.Text.Json.Nodes.JsonNode.Parse(serialized)!.AsObject();
    Assert(root.Select(pair => pair.Key).SequenceEqual(["meta", "time", "note", "extra"]),
        "Malody compatibility envelope/order changed");
    Assert(root.ContainsKey("extra") && root["extra"] is null &&
           root["meta"]?["id"]?.GetValue<int>() == 0 &&
           root["meta"]?["aimode"]?.GetValue<string>() == "",
        "new Malody charts omitted the game-index compatibility fields");
    var backgroundMusic = root["note"]!.AsArray()
        .Single(node => node?["type"]?.GetValue<int>() == 1)!;
    Assert(backgroundMusic["offset"]?.GetValue<int>() == 174,
        "BGM note offset was not emitted as the integer required by Malody");
    Assert(Math.Abs(root["time"]![0]!["delay"]!.GetValue<double>() - 173.848846) < .0000001,
        "rounding the BGM event also destroyed precise timing delay data");

    var repaired = MalodyMcSerializer.Parse("""
        {"meta":{"mode":0,"mode_ext":{"column":4},"song":{"file":"audio.ogg"}},
         "time":[{"beat":[0,0,1],"bpm":132.1,"delay":173.848846}],
         "note":[{"beat":[0,0,1],"sound":"audio.ogg","offset":173.848846,"type":1}]}
        """);
    var repairedRoot = System.Text.Json.Nodes.JsonNode.Parse(MalodyMcSerializer.Serialize(repaired))!;
    Assert(repairedRoot["note"]![0]!["offset"]!.GetValue<int>() == 174,
        "an existing invalid floating-point BGM offset was not repaired for Malody indexing");

    var path = Path.Combine(Path.GetTempPath(), $"lumachart-malody-compact-{Guid.NewGuid():N}.mc");
    try
    {
        MalodyMcSerializer.Save(chart.Clone(), path);
        var bytes = File.ReadAllBytes(path);
        Assert((bytes.Length < 3 || bytes[0] != 0xEF || bytes[1] != 0xBB || bytes[2] != 0xBF) &&
               !File.ReadAllText(path).Contains('\n'),
            "saved Malody chart used a BOM or multiline encoding");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void TestUnsupportedGameModes()
{
    try
    {
        MalodyMcSerializer.Parse("""
        {"meta":{"mode":3,"mode_ext":{},"song":{}},"time":[],"note":[]}
        """);
        Assert(false, "non-key Malody mode was accepted");
    }
    catch (NotSupportedException) { }

    try
    {
        OsuManiaSerializer.Parse("""
        osu file format v14

        [General]
        Mode: 0

        [Difficulty]
        CircleSize:4

        [TimingPoints]
        0,500,4,2,0,100,1,0

        [HitObjects]
        """);
        Assert(false, "non-mania osu mode was accepted");
    }
    catch (NotSupportedException) { }
}

static void TestMalodyBackgroundMusicNote()
{
    const string json = """
    {
      "meta":{"creator":"Mapper","version":"Hard","mode":0,"mode_ext":{"column":4},"song":{"title":"BGM Event","artist":"Artist"}},
      "time":[{"beat":[0,0,1],"bpm":220}],
      "note":[
        {"beat":[0,0,1],"sound":"song.ogg","vol":100,"offset":145,"type":1},
        {"beat":[1,0,1],"column":0}
      ]
    }
    """;
    var chart = MalodyMcSerializer.Parse(json);
    Assert(chart.Metadata.AudioFile == "song.ogg", "BGM type:1 sound was not used as the chart audio");
    Assert(Math.Abs(chart.Timing.First(timing => timing.Bpm.HasValue).Delay - 145) < .001,
        "BGM type:1 offset was not mapped to the first timing delay");
    var map = new TimingMap(chart.Timing);
    Assert(Math.Abs(map.BeatToMilliseconds(BeatPosition.FromDouble(1)) - (60_000d / 220 - 145)) < .001,
        "Malody BGM offset used the wrong sign on the audio timeline");
    Assert(chart.UnknownNotes.Count == 1 && chart.Notes.Count == 1, "BGM note was not preserved separately from playable notes");
    var serialized = MalodyMcSerializer.Serialize(chart);
    var backgroundMusic = System.Text.Json.Nodes.JsonNode.Parse(serialized)!["note"]!.AsArray()
        .Single(node => node?["type"]?.GetValue<int>() == 1)!;
    Assert(backgroundMusic["sound"]?.GetValue<string>() == "song.ogg" &&
           backgroundMusic["offset"]?.GetValue<int>() == 145,
        "BGM type:1 event was lost or desynchronized during save");
    var roundTrip = MalodyMcSerializer.Parse(serialized);
    Assert(roundTrip.Metadata.AudioFile == "song.ogg" && Math.Abs(roundTrip.Timing.First(timing => timing.Bpm.HasValue).Delay - 145) < .001,
        "BGM event changed after MC round-trip");

    const string independentOffsetJson = """
    {"meta":{"mode":0,"mode_ext":{"column":4},"song":{"file":"song.ogg"}},
     "time":[{"beat":[0,0,1],"bpm":160,"delay":2.30880237}],
     "note":[{"beat":[0,0,1],"sound":"song.ogg","offset":-1498,"type":1}]}
    """;
    var independentOffset = MalodyMcSerializer.Parse(independentOffsetJson);
    var independentRoot = System.Text.Json.Nodes.JsonNode.Parse(
        MalodyMcSerializer.Serialize(independentOffset))!;
    Assert(independentRoot["note"]![0]!["offset"]!.GetValue<int>() == -1498 &&
           Math.Abs(independentRoot["time"]![0]!["delay"]!.GetValue<double>() - 2.30880237) < .000000001,
        "an independent Malody BGM offset was overwritten by timing delay");
}

static void TestMalodyColumnNumbering()
{
    const string sparseOfficial = """
    {"meta":{"mode_ext":{"column":4},"song":{}},"time":[{"beat":[0,0,1],"bpm":120}],
     "note":[{"beat":[1,0,1],"column":1},{"beat":[2,0,1],"column":2},{"beat":[3,0,1],"column":3}]}
    """;
    var official = MalodyMcSerializer.Parse(sparseOfficial);
    Assert(official.Notes.Select(note => note.Lane).SequenceEqual([1, 2, 3]),
        "valid zero-based sparse columns were shifted left");

    const string legacyOneBased = """
    {"meta":{"mode_ext":{"column":4},"song":{}},"time":[{"beat":[0,0,1],"bpm":120}],
     "note":[{"beat":[1,0,1],"column":1},{"beat":[2,0,1],"column":4}]}
    """;
    var legacy = MalodyMcSerializer.Parse(legacyOneBased);
    Assert(legacy.Notes.Select(note => note.Lane).SequenceEqual([0, 3]),
        "unambiguous legacy one-based columns were not migrated");
}

static void TestMalodyMixedEffectRoundTrip()
{
    const string json = """
    {"meta":{"mode_ext":{"column":4},"song":{}},"time":[{"beat":[0,0,1],"bpm":120}],
     "effect":[{"beat":[2,0,1],"scroll":1.25,"custom_curve":7},
               {"beat":[3,0,1],"sign":1},{"beat":[4,0,1],"showbar":1.0}],"note":[]}
    """;
    var chart = MalodyMcSerializer.Parse(json);
    var serialized = MalodyMcSerializer.Serialize(chart);
    var effects = System.Text.Json.Nodes.JsonNode.Parse(serialized)!["effect"]!.AsArray();
    Assert(effects.Count(node => node?["scroll"] is not null) == 1,
        "official effect was duplicated while preserving a custom field");
    Assert(effects.Count(node => node?["custom_curve"]?.GetValue<int>() == 7) == 1,
        "custom effect field was not preserved exactly once");
    Assert(chart.Timing.Any(item => item.Effect == TimingEffectKind.Sign && item.EffectValueText == "1"),
        "sign effect did not import");
    Assert(chart.Timing.Any(item => item.Effect == TimingEffectKind.Custom && item.EffectFieldName == "showbar" &&
                                    item.EffectValueText == "1.0"),
        "custom effect name or exact numeric spelling did not import");
    Assert(effects.Single(node => node?["sign"] is not null)?["sign"]?.ToJsonString() == "1" &&
           effects.Single(node => node?["showbar"] is not null)?["showbar"]?.ToJsonString() == "1.0",
        "integer/decimal effect spelling changed during round-trip");
}

static void TestOnlineCreatorIdentityIsRuntimeOnly()
{
    var chart = new ChartDocument
    {
        Notes = [new ChartNote { Beat = new BeatPosition(1, 0, 1), Lane = 0, OnlineCreatorUserId = 42 }],
        Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120, OnlineCreatorUserId = 42 }]
    };
    var serialized = MalodyMcSerializer.Serialize(chart);
    Assert(!serialized.Contains("OnlineCreator", StringComparison.OrdinalIgnoreCase) &&
           !serialized.Contains("CreatorUserId", StringComparison.OrdinalIgnoreCase),
        "multiplayer creator identity leaked into Malody JSON");
    var roundTrip = MalodyMcSerializer.Parse(serialized);
    Assert(roundTrip.Notes.All(note => note.OnlineCreatorUserId == 0) &&
           roundTrip.Timing.All(item => item.OnlineCreatorUserId == 0),
        "runtime-only creator identity unexpectedly survived a chart export");
}

static void TestNonFiniteNormalization()
{
    var chart = new ChartDocument
    {
        Metadata = new ChartMetadata
        {
            Bpm = double.NaN,
            OverallDifficulty = double.NaN,
            HpDrainRate = double.PositiveInfinity
        },
        Timing =
        [
            new TimingEvent { Beat = BeatPosition.Zero, Bpm = double.NaN, Effect = TimingEffectKind.Jump,
                EffectValue = double.NegativeInfinity, Delay = double.NaN }
        ]
    };
    chart.Normalize();
    Assert(chart.Metadata.Bpm == 120 && chart.Metadata.OverallDifficulty == 8 && chart.Metadata.HpDrainRate == 8,
        "non-finite chart BPM/OD/HP did not reset to safe defaults");
    Assert(chart.Timing.Any(point => point.Effect == TimingEffectKind.Jump && point.EffectValue == 1 && point.Delay == 0),
        "valid effect data was lost while repairing non-finite timing values");
    Assert(chart.Timing.Any(point => point.Bpm == 120), "invalid BPM did not receive a safe fallback");
    var serialized = MalodyMcSerializer.Serialize(chart);
    Assert(!serialized.Contains("NaN") && !serialized.Contains("Infinity"),
        "non-finite values reached serialized JSON");
}

static void TestOsu()
{
    const string osu = """
    osu file format v14
    [General]
    AudioFilename: song.mp3
    PreviewTime: 12345
    Mode: 3
    [Metadata]
    Title:Test
    Artist:Artist
    Creator:Mapper
    Version:Insane
    [Difficulty]
    HPDrainRate:6.5
    CircleSize:4
    OverallDifficulty:9.2
    [Events]
    0,0,"background.jpg",0,0
    [TimingPoints]
    100,500,4,2,1,70,1,0
    600,-50,4,2,1,70,0,0
    [HitObjects]
    64,192,600,1,0,0:0:0:75:clap.wav
    448,192,1100,128,0,2100:0:0:0:0:
    """;
    var chart = OsuManiaSerializer.Parse(osu);
    Assert(chart.KeyCount == 4 && chart.Notes.Count == 2 && chart.Notes[1].IsHold, "osu notes did not import");
    Assert(chart.Timing.Any(t => t.Effect == TimingEffectKind.Scroll && Math.Abs(t.EffectValue - 2) < .001), "osu inherited SV did not import");
    Assert(chart.Notes[0].Sound == "clap.wav" && chart.Notes[0].Volume == 75, "osu custom hit sample did not import");
    Assert(chart.Notes[1].Volume == 100, "osu inherited hit-sample volume 0 was incorrectly treated as mute");
    Assert(chart.Metadata.PreviewTime == 12345 && chart.Metadata.BackgroundFile == "background.jpg", "osu media metadata did not import");
    Assert(chart.Metadata.Bpm == 120 && Math.Abs(chart.Metadata.OverallDifficulty - 9.2) < .001 &&
           Math.Abs(chart.Metadata.HpDrainRate - 6.5) < .001,
        "osu chart BPM/OD/HP did not import");
    const double independentChartBpm = 175.1234567890123;
    chart.Metadata.Bpm = independentChartBpm;
    var path = Path.Combine(Path.GetTempPath(), $"lumachart-{Guid.NewGuid():N}.osu");
    try
    {
        OsuManiaSerializer.Save(chart, path);
        var saved = File.ReadAllText(path);
        Assert(saved.StartsWith("osu file format v14") && saved.Contains("Mode: 3") && saved.Contains("[HitObjects]") &&
                saved.Contains("PreviewTime: 12345") && saved.Contains("background.jpg") && saved.Contains(",-50,"), "osu export is invalid");
        Assert(saved.Contains("0:0:0:75:clap.wav"), "osu custom hit sample did not export");
        Assert(saved.Contains("LumaChartBPM:" + independentChartBpm.ToString("R",
                    System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "osu export did not retain the independent chart-level BPM extension");
        var roundTrip = OsuManiaSerializer.Load(path);
        Assert(roundTrip.Metadata.Bpm == independentChartBpm &&
               Math.Abs(roundTrip.Timing.First(timing => timing.Bpm.HasValue).Bpm!.Value - 120) < .001,
            "osu save/reopen collapsed the independent chart BPM onto the first Timing BPM");
    }
    finally { if (File.Exists(path)) File.Delete(path); }

    var explicitExtension = OsuManiaSerializer.Parse(osu.Replace(
        "Version:Insane", "Version:Insane\nLumaChartBPM:155", StringComparison.Ordinal));
    Assert(explicitExtension.Metadata.Bpm == 155,
        "osu LumaChartBPM extension was not preferred over the first Timing BPM");
    var invalidExtension = OsuManiaSerializer.Parse(osu.Replace(
        "Version:Insane", "Version:Insane\nLumaChartBPM:NaN", StringComparison.Ordinal));
    Assert(invalidExtension.Metadata.Bpm == 120,
        "an invalid osu LumaChartBPM extension did not fall back to the first Timing BPM");
}

static void TestOsuUnspecifiedPreview()
{
    const string osu = """
    osu file format v14
    [General]
    AudioFilename: song.ogg
    PreviewTime: -1
    Mode: 3
    [Metadata]
    Title:Preview Sentinel
    Artist:Artist
    Creator:Mapper
    Version:Test
    [Difficulty]
    CircleSize:4
    [TimingPoints]
    0,500,4,2,1,70,1,0
    [HitObjects]
    """;
    var chart = OsuManiaSerializer.Parse(osu);
    Assert(chart.Metadata.PreviewTime == 0, "osu PreviewTime -1 leaked into MC editor metadata");
    chart.Metadata.PreviewTime = -1;
    chart.Normalize();
    Assert(chart.Metadata.PreviewTime == 0, "chart normalization retained a negative preview time");
}

static void TestOsuDuplicateFields()
{
    const string osu = """
    osu file format v14
    [General]
    AudioFilename: first.ogg
    AudioFilename: final.ogg
    Mode: 3
    [Metadata]
    Title:First
    [Metadata]
    Title:Final
    Artist:Artist
    Creator:Mapper
    Version:Test
    [Difficulty]
    CircleSize:4
    [TimingPoints]
    375,500,4,2,1,70,1,0
    375,400,4,2,1,70,1,0
    [HitObjects]
    """;
    var chart = OsuManiaSerializer.Parse(osu);
    Assert(chart.Metadata.AudioFile == "final.ogg" && chart.Metadata.Title == "Final",
        "duplicate osu fields/sections did not use the final value");
    Assert(Math.Abs(new TimingMap(chart.Timing).BpmAt(BeatPosition.Zero) - 150) < .001,
        "same-time osu BPM records did not preserve source order");
    Assert(Math.Abs(new TimingMap(chart.Timing).BeatToMilliseconds(BeatPosition.Zero) - 375) < .001,
        "same-time osu BPM records discarded the non-zero song offset");
}

static void TestOsuDifficultySettings()
{
    var chart = new ChartDocument
    {
        Metadata = new ChartMetadata { OverallDifficulty = 7.25, HpDrainRate = 4.5 }
    };
    var path = Path.Combine(Path.GetTempPath(), $"lumachart-difficulty-{Guid.NewGuid():N}.osu");
    try
    {
        OsuManiaSerializer.Save(chart, path);
        var saved = File.ReadAllText(path);
        Assert(saved.Contains("OverallDifficulty:7.25") && saved.Contains("HPDrainRate:4.5"),
            "osu OD/HP were not preserved by a normal save");
        OsuManiaSerializer.Save(chart, path, overallDifficulty: 8.75, hpDrainRate: 9);
        saved = File.ReadAllText(path);
        Assert(saved.Contains("OverallDifficulty:8.75") && saved.Contains("HPDrainRate:9"),
            "osu export OD/HP overrides were not written");
        OsuManiaSerializer.Save(chart, path, overallDifficulty: double.NaN, hpDrainRate: double.PositiveInfinity);
        saved = File.ReadAllText(path);
        Assert(saved.Contains("OverallDifficulty:8") && saved.Contains("HPDrainRate:8") &&
               !saved.Contains("NaN") && !saved.Contains("Infinity"),
            "non-finite osu difficulty overrides reached the output file");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void TestOsuOffsetRoundTrip()
{
    const string osu = """
    osu file format v14
    [General]
    AudioFilename: offset-test.ogg
    Mode: 3
    [Metadata]
    Title:Offset Test
    Artist:Artist
    Creator:Mapper
    Version:Timing
    [Difficulty]
    CircleSize:4
    [TimingPoints]
    -125,500,4,2,1,70,1,0
    1875,400,4,2,1,70,1,0
    [HitObjects]
    64,192,-125,1,0,0:0:0:100:
    192,192,875,1,0,0:0:0:100:
    320,192,2275,128,0,3075:0:0:0:100:
    """;
    var chart = OsuManiaSerializer.Parse(osu);
    var firstTiming = chart.Timing.First(point => point.Bpm.HasValue);
    Assert(Math.Abs(firstTiming.Delay - 125) < .0001, "negative osu offset was not converted to Malody delay semantics");
    var beforeMap = new TimingMap(chart.Timing);
    var beforeTimes = chart.Notes.Select(note => beforeMap.BeatToMilliseconds(note.Beat)).ToArray();
    Assert(Math.Abs(beforeTimes[0] + 125) < .01 && Math.Abs(beforeTimes[1] - 875) < .01 && Math.Abs(beforeTimes[2] - 2275) < .01,
        "osu note times drifted while converting to the MC timing model");

    var path = Path.Combine(Path.GetTempPath(), $"lumachart-offset-{Guid.NewGuid():N}.osu");
    try
    {
        OsuManiaSerializer.Save(chart, path);
        var roundTrip = OsuManiaSerializer.Load(path);
        var afterMap = new TimingMap(roundTrip.Timing);
        var afterTimes = roundTrip.Notes.Select(note => afterMap.BeatToMilliseconds(note.Beat)).ToArray();
        Assert(beforeTimes.Zip(afterTimes).All(pair => Math.Abs(pair.First - pair.Second) <= .51),
            "osu save/package round-trip changed note timestamps");
        Assert(roundTrip.Timing.Count(point => point.Bpm.HasValue) == 2 &&
               Math.Abs(roundTrip.Timing.First(point => point.Bpm.HasValue).Delay - 125) < .01,
            "osu save/package round-trip changed offset or BPM records");
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static void TestOsuBpmChangeBackwardSeek()
{
    // Regression from Reg-10 Hiensou / Hinaka_Yuki. The editor used to seek the
    // live decoder in-place, so returning through the overview after the 69 BPM
    // section could retain the later segment's buffered playback position.
    const string osu = """
    osu file format v14
    [General]
    AudioFilename: hiensou.mp3
    Mode: 3
    [Metadata]
    Title:Hiensou
    Artist:Various Artists
    Creator:Muses
    Version:Reg-10 Hiensou
    [Difficulty]
    CircleSize:4
    [TimingPoints]
    0,434.782608695652,4,2,1,70,1,0
    73043,869.565217391304,4,2,1,70,1,0
    100869,434.782608695652,4,2,1,70,1,0
    [HitObjects]
    64,192,72000,1,0,0:0:0:100:
    192,192,80000,1,0,0:0:0:100:
    320,192,102000,1,0,0:0:0:100:
    """;

    var chart = OsuManiaSerializer.Parse(osu);
    var map = new TimingMap(chart.Timing);
    var bpmPoints = chart.Timing.Where(point => point.Bpm.HasValue).OrderBy(point => point.Beat).ToArray();
    Assert(bpmPoints.Length == 3, "Hiensou BPM records were not imported");
    // BeatPosition is intentionally Malody-compatible (1/1920-beat precision),
    // so the imported decimal timing point may quantize by a fraction of a ms.
    Assert(Math.Abs(map.BeatToMilliseconds(bpmPoints[1].Beat) - 73_043) < .25 &&
           Math.Abs(map.BeatToMilliseconds(bpmPoints[2].Beat) - 100_869) < .25,
        "BPM boundaries accumulated timestamp drift");
    Assert(Math.Abs(map.BpmAt(map.MillisecondsToBeat(80_000)) - 69) < .001,
        "the slow BPM segment was not selected");

    // Exercise the same direction as the UI bug: pass both changes, then jump back
    // before them, followed by seeks to either side of each boundary.
    double[] seekSequence = [110_000, 105_000, 60_000, 72_000, 80_000, 102_000, 50_000];
    foreach (var milliseconds in seekSequence)
    {
        var beat = map.MillisecondsToBeat(milliseconds);
        var roundTrip = map.BeatToMilliseconds(beat);
        Assert(Math.Abs(roundTrip - milliseconds) < .25,
            $"backward seek drifted at {milliseconds:0.###} ms (got {roundTrip:0.###} ms)");
    }
}

static void TestLargeChart()
{
    var chart = new ChartDocument { KeyCount = 7, Notes = [] };
    for (var i = 99_999; i >= 0; i--) chart.Notes.Add(new ChartNote { Lane = i % 7, Beat = BeatPosition.FromDouble(i / 16d) });
    chart.Normalize();
    Assert(chart.Notes.Count == 100_000 && chart.Notes[0].Beat.Value <= chart.Notes[^1].Beat.Value, "large chart normalization failed");
}

static void TestBatchMoveUndo()
{
    var first = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(1), Group = 2 };
    var second = new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(2), EndBeat = BeatPosition.FromDouble(3), Group = 2 };
    var session = new EditorSession();
    session.ReplaceDocument(new ChartDocument { KeyCount = 4, Notes = [first, second] });
    session.Selection.UnionWith([first.Id, second.Id]);
    session.BeginInteractiveMutation();
    session.UpdateInteractiveMutation(chart =>
    {
        foreach (var note in chart.Notes)
        {
            note.Lane += 1;
            note.Beat += .5;
            if (note.EndBeat.HasValue) note.EndBeat += .5;
        }
    });
    session.CommitInteractiveMutation();
    Assert(session.Document.Notes.All(n => n.Lane is 1 or 2), "batch move did not update every selected note");
    Assert(session.Undo(), "batch move did not create an undo operation");
    Assert(session.Document.Notes[0].Lane == 0 && session.Document.Notes[0].Beat.Value == 1, "undo did not restore batch origin");
    Assert(session.Redo() && session.Document.Notes[0].Lane == 1, "redo did not restore the batch move");
}

static void TestApplicationEditingLifecycle()
{
    var session = new EditorSession();
    var changed = 0;
    session.Changed += (_, _) => changed++;
    session.ReplaceDocument(new ChartDocument
    {
        KeyCount = 4,
        Metadata = new ChartMetadata { Title = "Offline baseline" },
        Notes = [],
        Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 }]
    });

    var noteId = Guid.NewGuid();
    session.Mutate(chart => chart.Notes.Add(new ChartNote
    {
        Id = noteId,
        Lane = 2,
        Beat = new BeatPosition(4, 1, 2),
        EndBeat = new BeatPosition(6, 0, 1),
        Sound = "keysounds/hit.wav",
        Volume = 72,
        Group = 9
    }));
    Assert(session.IsDirty && session.ChangeSerial == 1 &&
           session.Document.Notes.Single().Id == noteId,
        "an offline mutation was not recorded by the extracted application session");

    session.MarkSaved();
    Assert(!session.IsDirty && session.ChangeSerial == 1,
        "marking an offline chart saved changed its document revision");
    Assert(session.Undo() && session.Document.Notes.Count == 0 &&
           session.IsDirty && session.ChangeSerial == 2,
        "offline undo changed after extracting the application layer");
    Assert(session.Redo() && session.Document.Notes.Single().Id == noteId &&
           session.IsDirty && session.ChangeSerial == 3,
        "offline redo changed after extracting the application layer");
    session.MarkSaved();
    Assert(!session.IsDirty && changed == 6,
        "offline save/history notifications changed after extracting the application layer");

    var noOpInteractive = new EditorSession();
    noOpInteractive.ReplaceDocument(new ChartDocument { KeyCount = 4 });
    noOpInteractive.BeginInteractiveMutation();
    noOpInteractive.CommitInteractiveMutation();
    Assert(!noOpInteractive.IsDirty && noOpInteractive.ChangeSerial == 0 && !noOpInteractive.Undo(),
        "an unchanged interactive edit created dirty state or an undo entry");

    var referencedAssemblies = typeof(EditorSession).Assembly.GetReferencedAssemblies()
        .Select(reference => reference.Name ?? string.Empty)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(typeof(EditorSession).Assembly.GetName().Name == "LumaChart.Application" &&
           typeof(OwnerProtectedRegion).Assembly.GetName().Name == "LumaChart.Core" &&
           !referencedAssemblies.Overlaps(["PresentationCore", "PresentationFramework", "WindowsBase"]),
        "the extracted application layer retained a WPF dependency or moved the core lock model upward");
}

static void TestScopedOnlineUndo()
{
    var mine = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(1) };
    var remote = new ChartNote { Lane = 3, Beat = BeatPosition.FromDouble(2), OnlineCreatorUserId = 2 };
    var session = new EditorSession();
    session.ReplaceDocument(new ChartDocument { KeyCount = 4, Notes = [mine] });
    session.Mutate(chart => chart.Notes.Single(note => note.Id == mine.Id).Lane = 1);
    session.ApplyRemoteMutation(chart => chart.Notes.Add(remote.Clone()));

    Assert(session.UndoOwnChanges() == ScopedHistoryResult.Applied,
        "scoped online undo did not apply the local operation");
    Assert(session.Document.Notes.Single(note => note.Id == mine.Id).Lane == 0 &&
           session.Document.Notes.Any(note => note.Id == remote.Id),
        "scoped online undo reverted another member's note");
    Assert(session.RedoOwnChanges() == ScopedHistoryResult.Applied &&
           session.Document.Notes.Single(note => note.Id == mine.Id).Lane == 1 &&
           session.Document.Notes.Any(note => note.Id == remote.Id),
        "scoped online redo lost another member's note");

    session.Mutate(chart => chart.Notes.Single(note => note.Id == mine.Id).Lane = 2);
    session.ApplyRemoteMutation(chart => chart.Notes.Single(note => note.Id == mine.Id).Lane = 3);
    Assert(session.UndoOwnChanges() == ScopedHistoryResult.Conflict &&
           session.Document.Notes.Single(note => note.Id == mine.Id).Lane == 3,
        "scoped online undo overwrote a later same-note remote edit");

    var normalizedSoundSession = new EditorSession();
    var added = new ChartNote { Lane = 2, Beat = BeatPosition.FromDouble(3), Sound = null };
    normalizedSoundSession.ReplaceDocument(new ChartDocument { KeyCount = 4 });
    normalizedSoundSession.Mutate(chart => chart.Notes.Add(added.Clone()));
    normalizedSoundSession.ApplyRemoteMutation(chart =>
        chart.Notes.Single(note => note.Id == added.Id).Sound = string.Empty);
    Assert(normalizedSoundSession.UndoOwnChanges() == ScopedHistoryResult.Applied &&
           normalizedSoundSession.Document.Notes.Count == 0,
        "scoped online undo treated server-normalized empty Note sound as a conflicting edit");

    var authoritativeResetSession = new EditorSession();
    authoritativeResetSession.ReplaceDocument(new ChartDocument { KeyCount = 4, Notes = [mine.Clone()] });
    authoritativeResetSession.Mutate(chart => chart.Notes.Single().Lane = 1);
    authoritativeResetSession.Mutate(chart => chart.Notes.Single().Lane = 2);
    Assert(authoritativeResetSession.Undo(), "test setup did not create both undo and redo history");
    authoritativeResetSession.ReplaceDocument(new ChartDocument
    {
        KeyCount = 4,
        Notes = [new ChartNote { Lane = 3, Beat = BeatPosition.FromDouble(8) }]
    }, markDirty: true);
    Assert(!authoritativeResetSession.Undo() && !authoritativeResetSession.Redo() &&
           authoritativeResetSession.UndoOwnChanges() == ScopedHistoryResult.None &&
           authoritativeResetSession.RedoOwnChanges() == ScopedHistoryResult.None &&
           authoritativeResetSession.Document.Notes.Single().Lane == 3,
        "an authoritative chart replacement retained undo/redo entries from the previous generation");
}

static void TestGranularOnlinePermissions()
{
    Assert(CollaborationRevisionPolicy.GetPermissionDeniedCategory("viewer_read_only") == "谱面" &&
           CollaborationRevisionPolicy.GetPermissionDeniedCategory("observer_read_only") == "谱面" &&
           CollaborationRevisionPolicy.GetPermissionDeniedCategory("note_permission_required") == "Note" &&
           CollaborationRevisionPolicy.GetPermissionDeniedCategory("timing_permission_required") == "Effect" &&
           CollaborationRevisionPolicy.GetPermissionDeniedCategory("metadata_permission_required") == "谱面信息",
        "client permission notices did not recognize current or legacy server error codes");
    var session = new EditorSession
    {
        CanEditNotes = true,
        CanEditTiming = false,
        CanEditMetadata = false
    };
    session.ReplaceDocument(new ChartDocument
    {
        KeyCount = 4,
        Metadata = new ChartMetadata { Title = "Before" },
        Notes = [],
        Timing = [new TimingEvent { Bpm = 120 }]
    });
    var denied = new List<string>();
    session.PermissionDenied += (_, category) => denied.Add(category);

    session.Mutate(chart => chart.Notes.Add(new ChartNote { Lane = 0, Beat = BeatPosition.Zero }));
    Assert(session.Document.Notes.Count == 1, "default editor Note permission rejected an allowed Note edit");

    session.Mutate(chart => chart.Timing.Single().Bpm = 180);
    Assert(session.Document.Timing.Single().Bpm == 120 && denied.LastOrDefault() == "Effect",
        "Timing permission did not roll back a forbidden edit");

    session.Mutate(chart => chart.Metadata.Title = "After");
    Assert(session.Document.Metadata.Title == "Before" && denied.LastOrDefault() == "谱面信息",
        "metadata permission did not roll back a forbidden edit");

    session.ApplyRemoteMutation(chart =>
    {
        chart.Timing.Single().Bpm = 200;
        chart.Metadata.Title = "Server";
    });
    Assert(session.Document.Timing.Single().Bpm == 200 && session.Document.Metadata.Title == "Server",
        "authoritative remote state was incorrectly blocked by local permissions");

    session.CanEditNotes = false;
    Assert(session.UndoOwnChanges() == ScopedHistoryResult.PermissionDenied && session.Document.Notes.Count == 1,
        "online undo bypassed a newly revoked Note permission");

    session.CanEditMetadata = true;
    session.CanEditTiming = true;
    session.Mutate(chart =>
    {
        chart.Metadata.Title = "Granted";
        chart.KeyCount = 7;
        chart.Timing.Single().Bpm = 220;
    });
    Assert(session.Document.Metadata.Title == "Granted" && session.Document.KeyCount == 7 &&
           session.Document.Timing.Single().Bpm == 220,
        "newly granted granular permissions did not take effect immediately");
}

#if LUMACHART_SERVER
static void TestOnlineChartSyncCodec()
{
    var retainedNoteId = Guid.NewGuid();
    var deletedNoteId = Guid.NewGuid();
    var retainedTimingId = Guid.NewGuid();
    var deletedTimingId = Guid.NewGuid();
    var baseline = new ChartDocument
    {
        KeyCount = 4,
        Metadata = new ChartMetadata
        {
            Title = "Before",
            Artist = "Artist",
            Creator = "Mapper",
            Version = "Draft",
            AudioFile = "audio.ogg",
            Bpm = 120,
            OverallDifficulty = 7,
            HpDrainRate = 6
        },
        Notes =
        [
            new ChartNote
            {
                Id = retainedNoteId, Lane = 0, Beat = new BeatPosition(1, 1, 4),
                Sound = "old.wav", Volume = 80, Group = 2, OnlineCreatorUserId = 11,
                UnknownData = System.Text.Json.Nodes.JsonNode.Parse("{\"legacy\":true}")!.AsObject()
            },
            new ChartNote { Id = deletedNoteId, Lane = 1, Beat = new BeatPosition(2, 0, 1) }
        ],
        Timing =
        [
            new TimingEvent
            {
                Id = retainedTimingId, Beat = BeatPosition.Zero, Bpm = 120, Delay = 12,
                OnlineCreatorUserId = 11
            },
            new TimingEvent
            {
                Id = deletedTimingId, Beat = new BeatPosition(2, 0, 1),
                Effect = TimingEffectKind.Scroll, EffectValue = 1.2, EffectValueText = "1.2"
            }
        ]
    };
    baseline.UnknownRoot["preservedByTemplate"] = true;
    baseline.Normalize();

    var current = baseline.Clone();
    current.KeyCount = 6;
    current.Metadata = new ChartMetadata
    {
        Title = "After",
        TitleOriginal = "原标题",
        Artist = "Artist 2",
        ArtistOriginal = "原艺术家",
        Creator = "Mapper 2",
        Version = "Final",
        AudioFile = "audio/final.ogg",
        CoverFile = "images/cover.jpg",
        BackgroundFile = "images/background.jpg",
        PreviewTime = 12_345,
        Bpm = 175.25,
        OverallDifficulty = 8.5,
        HpDrainRate = 4.5
    };
    var retainedNote = current.Notes.Single(note => note.Id == retainedNoteId);
    retainedNote.Lane = 4;
    retainedNote.Sound = "keysounds/final.wav";
    retainedNote.Volume = 73;
    retainedNote.Group = 7;
    retainedNote.OnlineCreatorUserId = 31;
    current.Notes.RemoveAll(note => note.Id == deletedNoteId);
    var longNoteId = Guid.NewGuid();
    current.Notes.Add(new ChartNote
    {
        Id = longNoteId,
        Lane = 5,
        Beat = BeatPosition.Preserve(5, 3, 6),
        EndBeat = BeatPosition.Preserve(7, 9, 12),
        Sound = "keysounds/ln.wav",
        Volume = 64,
        Group = 12,
        IsDraft = false,
        DraftColumn = -3,
        OnlineCreatorUserId = 32,
        UnknownData = System.Text.Json.Nodes.JsonNode.Parse("{\"custom\":\"note\"}")!.AsObject()
    });

    var retainedTiming = current.Timing.Single(item => item.Id == retainedTimingId);
    retainedTiming.Bpm = 180;
    retainedTiming.Delay = 24;
    retainedTiming.OnlineCreatorUserId = 41;
    retainedTiming.UnknownData = System.Text.Json.Nodes.JsonNode.Parse("{\"timing\":1}")!.AsObject();
    current.Timing.RemoveAll(item => item.Id == deletedTimingId);
    var signTimingId = Guid.NewGuid();
    var customTimingId = Guid.NewGuid();
    current.Timing.Add(new TimingEvent
    {
        Id = signTimingId,
        Beat = new BeatPosition(3, 0, 1),
        Effect = TimingEffectKind.Sign,
        EffectValue = -2.5,
        EffectValueText = "-2.50",
        OnlineCreatorUserId = 42
    });
    current.Timing.Add(new TimingEvent
    {
        Id = customTimingId,
        Beat = new BeatPosition(4, 1, 2),
        Effect = TimingEffectKind.Custom,
        EffectValue = 1.25,
        EffectValueText = "1.250",
        EffectFieldName = "camera_zoom",
        OnlineCreatorUserId = 43,
        UnknownData = System.Text.Json.Nodes.JsonNode.Parse("{\"raw\":\"kept\"}")!.AsObject()
    });
    current.Normalize();

    var initialLockRanges = new[]
    {
        OnlineChartSyncCodec.CreateLockRange(
            BeatPosition.Preserve(5, 5, 6),
            BeatPosition.Preserve(6, 1, 12))
    };
    var initialize = OnlineChartSyncCodec.CreateInitializeRequest(current, initialLockRanges);
    Assert(initialize.KeyCount == 6 && initialize.Metadata is { Title: "After", Bpm: 175.25 } &&
           initialize.Notes.Count == 2 && initialize.Timing.Count == 3 &&
           initialize.InitialLocks?.Single() == new OnlineChartLockRange(
               new OnlineSyncBeat(5, 5, 6), new OnlineSyncBeat(6, 1, 12)),
        "the extracted online initializer lost metadata, key-count or chart objects");
    var initializerWithoutLocksJson = System.Text.Json.JsonSerializer.Serialize(
        OnlineChartSyncCodec.CreateInitializeRequest(current),
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    using (var initializerWithoutLocks = System.Text.Json.JsonDocument.Parse(initializerWithoutLocksJson))
        Assert(!initializerWithoutLocks.RootElement.TryGetProperty("initialLocks", out _),
            "a legacy chart initializer started emitting an unnecessary null initialLocks field");
    var onlineLongNote = initialize.Notes.Single(note => note.Id == longNoteId);
    Assert(onlineLongNote.Lane == 5 && onlineLongNote.Beat == new OnlineSyncBeat(5, 3, 6) &&
           onlineLongNote.EndBeat == new OnlineSyncBeat(7, 9, 12) &&
           onlineLongNote.Sound == "keysounds/ln.wav" && onlineLongNote.Volume == 64 &&
           onlineLongNote.Group == 12 && onlineLongNote.CreatorUserId == 32 &&
           onlineLongNote.UnknownJson?.Contains("custom", StringComparison.Ordinal) == true,
        "Note/LN fields did not survive conversion into the online contract");
    var onlineCustom = initialize.Timing.Single(item => item.Id == customTimingId);
    Assert(onlineCustom.Effect == (int)TimingEffectKind.Custom && onlineCustom.EffectValue == 1.25 &&
           onlineCustom.EffectValueText == "1.250" && onlineCustom.EffectFieldName == "camera_zoom" &&
           onlineCustom.CreatorUserId == 43 && onlineCustom.UnknownJson?.Contains("raw", StringComparison.Ordinal) == true &&
           initialize.Timing.Single(item => item.Id == signTimingId).Effect == (int)TimingEffectKind.Sign,
        "Timing/Effect/Sign/Custom fields did not survive conversion into the online contract");

    var batch = OnlineChartSyncCodec.CreateBatch(7, baseline, current);
    Assert(batch.BaseRevision == 7 && batch.OperationId != Guid.Empty &&
           batch.Metadata is { Version: "Final", Bpm: 175.25 } && batch.KeyCount == 6 &&
           batch.UpsertNotes.Select(note => note.Id).ToHashSet().SetEquals([retainedNoteId, longNoteId]) &&
           batch.DeleteNoteIds.SequenceEqual([deletedNoteId]) &&
           batch.UpsertTiming.Select(item => item.Id).ToHashSet()
               .SetEquals([retainedTimingId, signTimingId, customTimingId]) &&
           batch.DeleteTimingIds.SequenceEqual([deletedTimingId]),
        "the online diff did not keep Metadata/KeyCount atomic with Note and Timing changes");

    var earlierNote = batch.UpsertNotes.Single(note => note.Id == retainedNoteId) with { Lane = 2 };
    var earlierBatch = new OnlineChartOperationBatch(7, [earlierNote], [], [], [], Guid.NewGuid());
    var operations = new[]
    {
        new AppliedOnlineChartOperation(9, 31, batch, DateTimeOffset.UtcNow.AddSeconds(1)),
        new AppliedOnlineChartOperation(8, 30, earlierBatch, DateTimeOffset.UtcNow)
    };
    var applied = OnlineChartSyncCodec.ApplyOperations(baseline, operations);
    Assert(applied.KeyCount == 6 && applied.Metadata.Title == "After" && applied.Metadata.Bpm == 175.25 &&
           applied.Notes.Single(note => note.Id == retainedNoteId).Lane == 4 &&
           applied.Notes.Any(note => note.Id == longNoteId) &&
           applied.Notes.All(note => note.Id != deletedNoteId) &&
           applied.Timing.Any(item => item.Id == customTimingId) &&
           applied.Timing.All(item => item.Id != deletedTimingId),
        "out-of-order online operations were not applied in revision order");

    var snapshot = new OnlineChartSnapshot(9, initialize.KeyCount, initialize.Notes,
        initialize.Timing, [], initialize.Metadata);
    var canonical = OnlineChartSyncCodec.CreateCanonicalDocument(baseline, snapshot);
    var canonicalLongNote = canonical.Notes.Single(note => note.Id == longNoteId);
    Assert(canonical.KeyCount == 6 && canonical.Metadata.Version == "Final" && canonical.Metadata.Bpm == 175.25 &&
           canonicalLongNote.Beat is { Numerator: 3, Denominator: 6 } &&
           canonicalLongNote.EndBeat is { } canonicalEndBeat &&
           canonicalEndBeat is { Numerator: 9, Denominator: 12 } &&
           canonical.Timing.Single(item => item.Id == customTimingId).EffectFieldName == "camera_zoom" &&
           canonical.Notes.Single(note => note.Id == longNoteId).UnknownData?["custom"]?.GetValue<string>() == "note" &&
           canonical.Timing.Single(item => item.Id == customTimingId).UnknownData?["raw"]?.GetValue<string>() == "kept" &&
           canonical.UnknownRoot["preservedByTemplate"]?.GetValue<bool>() == true,
        "a full online snapshot did not rebuild a complete canonical document");

    var requestDocument = current.Clone();
    var serverCanonical = current.Clone();
    serverCanonical.Metadata.Title = "Remote title";
    var remoteNoteId = Guid.NewGuid();
    serverCanonical.Notes.Add(new ChartNote
    {
        Id = remoteNoteId, Lane = 0, Beat = new BeatPosition(9, 0, 1), OnlineCreatorUserId = 99
    });
    var newerLocal = requestDocument.Clone();
    newerLocal.Metadata.Version = "Local after request";
    var localNoteId = Guid.NewGuid();
    newerLocal.Notes.Add(new ChartNote { Id = localNoteId, Lane = 1, Beat = new BeatPosition(10, 0, 1) });
    var merged = OnlineChartSyncCodec.MergePendingLocalChanges(serverCanonical, requestDocument, newerLocal);
    Assert(merged.Metadata.Title == "Remote title" && merged.Metadata.Version == "Local after request" &&
           merged.Notes.Any(note => note.Id == remoteNoteId) && merged.Notes.Any(note => note.Id == localNoteId),
        "late committed retry merge lost independent remote or post-request local edits");

    var currentLockId = Guid.NewGuid();
    var legacyLockId = Guid.NewGuid();
    var regions = OnlineChartSyncCodec.ToOwnerRegions(
    [
        new OnlineChartLock(currentLockId, new OnlineSyncBeat(5, 1, 6),
            new OnlineSyncBeat(5, 1, 3), -64, 81, 1),
        new OnlineChartLock(legacyLockId, new OnlineSyncBeat(5, 1, 2),
            new OnlineSyncBeat(6, 1, 2), 1, 2, 1)
    ]);
    Assert(regions.Single(region => region.Id == currentLockId) ==
           new OwnerProtectedRegion(currentLockId, new BeatPosition(5, 1, 6), new BeatPosition(5, 1, 3)) &&
           regions.Single(region => region.Id == legacyLockId) ==
           new OwnerProtectedRegion(legacyLockId, new BeatPosition(4, 0, 1), new BeatPosition(8, 0, 1)),
        "exact full-width locks were expanded or legacy lane locks lost compatibility normalization");

    var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var clientBatchJson = System.Text.Json.JsonSerializer.Serialize(batch, jsonOptions);
    Assert(!clientBatchJson.Contains("\"isEmpty\"", StringComparison.OrdinalIgnoreCase),
        "the client-only IsEmpty helper leaked into the room wire protocol");
    var serverBatch = System.Text.Json.JsonSerializer.Deserialize<RoomChartOperationBatch>(
        clientBatchJson, jsonOptions);
    Assert(serverBatch?.Metadata?.Title == "After" && serverBatch.KeyCount == 6 &&
           serverBatch.UpsertNotes.Single(note => note.Id == longNoteId) is
               { Beat: { Bar: 5, Numerator: 3, Denominator: 6 },
                 EndBeat: { Bar: 7, Numerator: 9, Denominator: 12 } } &&
           serverBatch.UpsertNotes.Single(note => note.Id == longNoteId) is
               { Lane: 5, Sound: "keysounds/ln.wav", Volume: 64, Group: 12, IsDraft: false, DraftColumn: -3,
                 CreatorUserId: 32 } &&
           serverBatch.UpsertTiming.Single(item => item.Id == retainedTimingId) is
               { Beat: { Bar: 0, Numerator: 0, Denominator: 1 }, Bpm: 180, Delay: 24, CreatorUserId: 41 } &&
           serverBatch.UpsertTiming.Single(item => item.Id == customTimingId) is
               { Beat: { Bar: 4, Numerator: 1, Denominator: 2 }, EffectFieldName: "camera_zoom" } &&
           serverBatch.Metadata is
               { TitleOriginal: "原标题", Artist: "Artist 2", ArtistOriginal: "原艺术家", Creator: "Mapper 2",
                 Version: "Final", AudioFile: "audio/final.ogg", CoverFile: "images/cover.jpg",
                  BackgroundFile: "images/background.jpg", PreviewTime: 12345,
                  OverallDifficulty: 8.5, HpDrainRate: 4.5, Bpm: 175.25 },
        "client operation JSON no longer matches the server room contract");

    var legacyMetadata = new OnlineChartMetadata(
        "Legacy", "", "Artist", "", "Mapper", "Old", "audio.ogg", "", "", 0, 8, 8);
    var legacyMetadataJson = System.Text.Json.JsonSerializer.Serialize(legacyMetadata, jsonOptions);
    Assert(!legacyMetadataJson.Contains("\"bpm\"", StringComparison.OrdinalIgnoreCase),
        "a null chart BPM changed the legacy metadata wire payload/hash");
    var legacySnapshot = new OnlineChartSnapshot(10, initialize.KeyCount, initialize.Notes,
        initialize.Timing, [], legacyMetadata);
    var legacyCanonical = OnlineChartSyncCodec.CreateCanonicalDocument(baseline, legacySnapshot);
    Assert(legacyCanonical.Metadata.Bpm == 180,
        "legacy snapshot metadata did not derive chart BPM from its first valid Timing event");
    var legacyMetadataBatch = new OnlineChartOperationBatch(
        10, [], [], [], [], Guid.NewGuid(), legacyMetadata);
    var legacyApplied = OnlineChartSyncCodec.ApplyOperations(current,
        [new AppliedOnlineChartOperation(11, 1, legacyMetadataBatch, DateTimeOffset.UtcNow)]);
    Assert(legacyApplied.Metadata.Bpm == 175.25,
        "legacy metadata update cleared a newer chart BPM");

    var compatibleServerBatch = serverBatch
        ?? throw new InvalidOperationException("client operation JSON did not deserialize on the server contract");
    var serverLockId = Guid.NewGuid();
    var serverLock = new RoomChartLock(serverLockId, new SyncBeat(8, 0, 1),
        new SyncBeat(12, 0, 1), -64, 81, 77);
    var serverOperation = new AppliedRoomChartOperation(15, 77, compatibleServerBatch,
        new DateTimeOffset(2026, 7, 20, 5, 0, 0, TimeSpan.Zero));
    var serverDelta = new RoomChartDelta(15, [serverOperation], [serverLock], false, 12, true);
    var clientDelta = System.Text.Json.JsonSerializer.Deserialize<OnlineChartDelta>(
        System.Text.Json.JsonSerializer.Serialize(serverDelta, jsonOptions), jsonOptions);
    var serverApply = new RoomChartApplyResult(new RoomChartSnapshot(15, 4, [], [], [], null), null);
    var clientApply = System.Text.Json.JsonSerializer.Deserialize<OnlineChartApplyResult>(
        System.Text.Json.JsonSerializer.Serialize(serverApply, jsonOptions), jsonOptions);
    Assert(clientDelta is { CurrentRevision: 15, AuthoritativeBaselineRevision: 12, RequiresFullSnapshot: true } &&
           clientDelta.Operations.Single() is { Revision: 15, ActorUserId: 77 } clientOperation &&
           clientOperation.Batch.OperationId == batch.OperationId &&
           clientOperation.Batch.Metadata?.Title == "After" && clientOperation.Batch.KeyCount == 6 &&
           clientOperation.Batch.UpsertNotes.Single(note => note.Id == longNoteId).EndBeat ==
               new OnlineSyncBeat(7, 9, 12) &&
           clientOperation.Batch.UpsertTiming.Single(item => item.Id == customTimingId).EffectFieldName ==
               "camera_zoom" &&
           clientDelta.Locks.Single() is { Id: var receivedLockId, StartLane: -64, EndLane: 81, OwnerUserId: 77 } &&
           receivedLockId == serverLockId &&
           clientApply is { Snapshot.Revision: 15, AppliedOperation: null },
        "non-empty delta, lock, full-snapshot or nullable applied-operation JSON drifted between client and server");

    var restoredSnapshotId = Guid.NewGuid();
    var safetySnapshotId = Guid.NewGuid();
    var restoredAt = new DateTimeOffset(2026, 7, 20, 5, 1, 2, TimeSpan.Zero);
    var serverRollback = new RoomChartRollbackResult(
        new RoomChartSnapshot(21, 6, compatibleServerBatch.UpsertNotes,
            compatibleServerBatch.UpsertTiming, [serverLock], compatibleServerBatch.Metadata),
        new RoomChartSavedSnapshot(restoredSnapshotId, "Before chorus", 14, 77,
            "Owner", 321, 12, false, restoredAt),
        safetySnapshotId);
    var clientRollback = System.Text.Json.JsonSerializer.Deserialize<OnlineChartRollbackResult>(
        System.Text.Json.JsonSerializer.Serialize(serverRollback, jsonOptions), jsonOptions);
    Assert(clientRollback is { Snapshot.Revision: 21, Snapshot.KeyCount: 6 } &&
           clientRollback.Snapshot.Notes.Any(note => note.Id == longNoteId) &&
           clientRollback.Snapshot.Timing.Any(item => item.Id == customTimingId) &&
           clientRollback.Snapshot.Locks.Single().Id == serverLockId &&
           clientRollback.RestoredSnapshot is
               { Id: var receivedSnapshotId, Name: "Before chorus", SourceRevision: 14,
                 CreatorUserId: 77, CreatorDisplayName: "Owner", NoteCount: 321,
                 TimingCount: 12, IsAutomatic: false, CreatedAt: var receivedCreatedAt } &&
           receivedSnapshotId == restoredSnapshotId && receivedCreatedAt == restoredAt &&
           clientRollback.SafetySnapshotId == safetySnapshotId,
        "saved-snapshot or rollback JSON drifted between client and server");

    var onlineReferences = typeof(OnlineChartSyncCodec).Assembly.GetReferencedAssemblies()
        .Select(reference => reference.Name ?? string.Empty)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(typeof(OnlineChartSyncCodec).Assembly.GetName().Name == "LumaChart.Online" &&
           !onlineReferences.Overlaps(["PresentationCore", "PresentationFramework", "WindowsBase"]),
        "the extracted online contract retained a WPF dependency");
}
#endif

static void TestOnlineAuthTransport() =>TestOnlineAuthTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineAuthTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var createdAt = new DateTimeOffset(2026, 7, 20, 5, 20, 0, TimeSpan.Zero);
    var expiresAt = createdAt.AddDays(30);
    var user = new OnlineUserProfileDto(
        42,
        "mapper42",
        "Mapper 42",
        "editor",
        "avatar-key",
        createdAt,
        7_654,
        "bio",
        true,
        false,
        3,
        4,
        "Beta",
        [new OnlineUserTagDto("Beta Tester", "#4A90A4", true)],
        "mapper42@example.test");
    var login = new OnlineLoginResponseDto("login-token", expiresAt, user);
    var registration = login with { AccessToken = "registration-token" };

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(login, jsonOptions), Encoding.UTF8, "application/json")
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(registration, jsonOptions), Encoding.UTF8, "application/json")
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(user, jsonOptions), Encoding.UTF8, "application/json")
    });
    // Desktop logout has always been local-first: every HTTP status is ignored.
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Client-Profile", "Test-Client-A");
    var api = new OnlineAuthApi(client, new Uri("https://unit.lumachart.invalid/"));

    var loginResult = await api.LoginAsync(new OnlineLoginRequestDto(
        "mapper42", "secret", Guid.Parse("11111111-2222-3333-4444-555555555555"), "123456"));
    var registrationResult = await api.RegisterAsync(new OnlineRegistrationRequestDto(
        "mapper43", "secret2", "Mapper 43", "mapper43@example.test", "654321"));
    var restoredUser = await api.GetCurrentUserAsync("restore-token");
    await api.LogoutAsync("logout-token");

    Assert(loginResult is { AccessToken: "login-token", User.Id: 42 } &&
           loginResult.User.Tags?.Single() is { Name: "Beta Tester", IsNameColor: true } &&
           loginResult.User.Email == "mapper42@example.test" &&
           registrationResult?.AccessToken == "registration-token" &&
           restoredUser is { Id: 42, Role: "editor", ChartingSeconds: 7_654 },
        "auth wire DTOs lost session or private-profile fields");

    Assert(handler.Requests.Count == 4, "auth transport sent an unexpected number of requests");
    var loginRequest = handler.Requests[0];
    var registerRequest = handler.Requests[1];
    var meRequest = handler.Requests[2];
    var logoutRequest = handler.Requests[3];
    Assert(loginRequest.Method == HttpMethod.Post &&
           loginRequest.RequestUri == new Uri("https://unit.lumachart.invalid/api/auth/login") &&
           loginRequest.AuthorizationParameter == null,
        "login method, endpoint or authorization changed during extraction");
    Assert(registerRequest.Method == HttpMethod.Post &&
           registerRequest.RequestUri == new Uri("https://unit.lumachart.invalid/api/auth/register") &&
           registerRequest.AuthorizationParameter == null,
        "registration method, endpoint or authorization changed during extraction");
    Assert(meRequest.Method == HttpMethod.Get &&
           meRequest.RequestUri == new Uri("https://unit.lumachart.invalid/api/me") &&
           meRequest.AuthorizationScheme == "Bearer" &&
           meRequest.AuthorizationParameter == "restore-token",
        "session restore no longer uses GET /api/me with the exact bearer token");
    Assert(logoutRequest.Method == HttpMethod.Post &&
           logoutRequest.RequestUri == new Uri("https://unit.lumachart.invalid/api/auth/logout") &&
           logoutRequest.AuthorizationScheme == "Bearer" &&
           logoutRequest.AuthorizationParameter == "logout-token",
        "logout method, endpoint or bearer token changed during extraction");
    Assert(handler.Requests.All(request =>
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Test-Client-A")),
        "version or client-profile headers were lost by the injected transport");

    using var loginJson = System.Text.Json.JsonDocument.Parse(loginRequest.Body!);
    using var registerJson = System.Text.Json.JsonDocument.Parse(registerRequest.Body!);
    Assert(loginJson.RootElement.GetProperty("username").GetString() == "mapper42" &&
           loginJson.RootElement.GetProperty("captchaId").GetGuid() ==
               Guid.Parse("11111111-2222-3333-4444-555555555555") &&
           loginJson.RootElement.GetProperty("captchaAnswer").GetString() == "123456" &&
           !loginJson.RootElement.TryGetProperty("Username", out _),
        "login JSON stopped using the existing Web/camelCase contract");
    Assert(registerJson.RootElement.GetProperty("displayName").GetString() == "Mapper 43" &&
           registerJson.RootElement.GetProperty("emailCode").GetString() == "654321" &&
           !registerJson.RootElement.TryGetProperty("DisplayName", out _),
        "registration JSON stopped using the existing Web/camelCase contract");
}

#if LUMACHART_SERVER
static void TestOnlineRoomTransport() =>TestOnlineRoomTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineRoomTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var roomId = Guid.Parse("a1111111-b222-c333-d444-e55555555555");
    var createdAt = new DateTimeOffset(2026, 7, 20, 6, 20, 0, TimeSpan.Zero);
    var serverRoom = new RoomSummary(
        roomId,
        100_321,
        "Android Room",
        4_294_967_300,
        "Owner",
        3,
        8,
        false,
        true,
        true,
        8_589_934_700,
        createdAt);
    var visibleServerRoom = serverRoom with { IsHidden = false };
    var serverDirectory = new RoomDirectory([visibleServerRoom], 2, 10, 11);
    var serverCreated = new CreatedRoom(serverRoom);

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverDirectory));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverRoom));
    handler.Enqueue(JsonResponse(HttpStatusCode.Created, serverCreated));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverRoom));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverRoom));
    handler.Enqueue(JsonResponse(
        HttpStatusCode.NotFound,
        new ApiError("room_not_found", "房间不存在或已经关闭。")));
    var invalidCharsetContent = new StringContent("{}", Encoding.UTF8, "application/json");
    invalidCharsetContent.Headers.ContentType!.CharSet = "not-a-real-encoding";
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound)
    {
        Content = invalidCharsetContent
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    var blockingJson = Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(serverDirectory, jsonOptions));
    using var blockingStream = new BlockingReadStream(blockingJson);
    var blockingContent = new StreamContent(blockingStream);
    blockingContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = blockingContent });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Client-Profile", "Tablet-Spec");
    var endpoint = new Uri("https://unit.lumachart.invalid/");
    var api = new OnlineRoomApi(client, endpoint);
    AssertThrows<ArgumentException>(
        () => _ = new OnlineRoomApi(client, new Uri("http://insecure.example.test/")),
        "the shared room transport accepted a non-loopback plaintext HTTP endpoint");
    _ = new OnlineRoomApi(client, new Uri("http://127.0.0.1:5080/"));

    var directory = await api.GetVisibleRoomsAsync("  hidden room/谱  ", 0);
    var found = await api.FindRoomByNumberAsync(serverRoom.RoomNumber, "find-token");
    var created = await api.CreateRoomAsync(
        new OnlineCreateRoomRequestDto("Android Room", false, null, true), "create-token");
    var joined = await api.JoinRoomAsync(
        roomId, new OnlineJoinRoomRequestDto(null, "editor"), "editor-token");
    var watched = await api.JoinRoomAsync(
        roomId, new OnlineJoinRoomRequestDto("private-pass", "viewer"), "viewer-token");

    Assert(directory is { Page: 2, PageSize: 10, TotalCount: 11 } &&
           directory.Rooms.Single() is
           {
               Id: var directoryRoomId,
               RoomNumber: 100_321,
               Name: "Android Room",
               OwnerUserId: 4_294_967_300,
               OwnerDisplayName: "Owner",
               MemberCount: 3,
               MaximumMembers: 8,
               IsPrivate: false,
               IsHidden: false,
               ResourcesReady: true,
               ResourceBytes: 8_589_934_700,
               CreatedAt: var directoryCreatedAt
           } && directoryRoomId == roomId && directoryCreatedAt == createdAt &&
           found is { IsHidden: true } && created?.Room == found &&
           joined == found && watched == found,
        "room wire DTOs lost a directory, Int64 identifier or room-summary field");

    OnlineApiException? notFound = null;
    try
    {
        await api.FindRoomByNumberAsync(999_999, "error-token");
    }
    catch (OnlineApiException exception)
    {
        notFound = exception;
    }
    Assert(notFound is
           {
               Code: "room_not_found",
               StatusCode: HttpStatusCode.NotFound,
               Message: "房间不存在或已经关闭。"
           },
        "room transport errors lost the server code, status or message");

    var missingAvailability = await api.ProbeRoomByNumberAsync(
        888_888, "probe-missing-token");
    var activeAvailability = await api.ProbeRoomByNumberAsync(
        100_321, "probe-active-token");
    Assert(missingAvailability == OnlineRoomAvailability.NotFound &&
           activeAvailability == OnlineRoomAvailability.Available,
        "room status probing parsed a malformed body or changed 2xx/404 classification");

    using var cancellation = new CancellationTokenSource();
    var pendingDirectory = api.GetVisibleRoomsAsync(string.Empty, 1, cancellation.Token);
    var cancellationObserved = false;
    try
    {
        await blockingStream.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            _ = await pendingDirectory.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        catch (TimeoutException)
        {
        }
    }
    finally
    {
        cancellation.Cancel();
        blockingStream.Release();
    }
    if (!pendingDirectory.IsCompleted)
    {
        try { _ = await pendingDirectory.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }
    Assert(cancellationObserved,
        "room transport cancellation did not reach the HTTP response body");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "room transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 10,
        "room transport sent an unexpected number of requests");
    var directoryRequest = handler.Requests[0];
    var findRequest = handler.Requests[1];
    var createRequest = handler.Requests[2];
    var editorRequest = handler.Requests[3];
    var viewerRequest = handler.Requests[4];
    var errorRequest = handler.Requests[5];
    var missingProbeRequest = handler.Requests[6];
    var activeProbeRequest = handler.Requests[7];
    var canceledRequest = handler.Requests[8];
    var ownershipRequest = handler.Requests[9];
    var escapedQuery = Uri.EscapeDataString("hidden room/谱");
    Assert(directoryRequest.Method == HttpMethod.Get &&
           directoryRequest.RequestUri == new Uri(endpoint, $"api/rooms?page=1&q={escapedQuery}") &&
           directoryRequest.AuthorizationParameter == null,
        "room directory stopped clamping pages, trimming/escaping q or remaining public");
    Assert(findRequest.Method == HttpMethod.Get &&
           findRequest.RequestUri == new Uri(endpoint, "api/rooms/by-number/100321") &&
           findRequest.AuthorizationScheme == "Bearer" &&
           findRequest.AuthorizationParameter == "find-token",
        "room-number lookup method, URL or bearer token changed");
    Assert(createRequest.Method == HttpMethod.Post &&
           createRequest.RequestUri == new Uri(endpoint, "api/rooms") &&
           createRequest.AuthorizationParameter == "create-token",
        "room creation method, URL or bearer token changed");
    var joinUri = new Uri(endpoint, $"api/rooms/{roomId:D}/join");
    Assert(editorRequest.Method == HttpMethod.Post && editorRequest.RequestUri == joinUri &&
           editorRequest.AuthorizationParameter == "editor-token" &&
           viewerRequest.Method == HttpMethod.Post && viewerRequest.RequestUri == joinUri &&
           viewerRequest.AuthorizationParameter == "viewer-token",
        "join and spectate no longer share the exact authenticated room endpoint");
    Assert(errorRequest.RequestUri == new Uri(endpoint, "api/rooms/by-number/999999") &&
           errorRequest.AuthorizationParameter == "error-token" &&
           missingProbeRequest.RequestUri == new Uri(endpoint, "api/rooms/by-number/888888") &&
           missingProbeRequest.AuthorizationParameter == "probe-missing-token" &&
           activeProbeRequest.RequestUri == new Uri(endpoint, "api/rooms/by-number/100321") &&
           activeProbeRequest.AuthorizationParameter == "probe-active-token" &&
           canceledRequest.RequestUri == new Uri(endpoint, "api/rooms?page=1&q=") &&
           ownershipRequest.RequestUri == new Uri(endpoint, "ownership-probe"),
        "room error, cancellation or ownership probe used an unexpected URL");
    Assert(handler.Requests.All(request =>
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Tablet-Spec")),
        "version or client-profile headers were lost by the room transport");

    using var createJson = System.Text.Json.JsonDocument.Parse(createRequest.Body!);
    using var editorJson = System.Text.Json.JsonDocument.Parse(editorRequest.Body!);
    using var viewerJson = System.Text.Json.JsonDocument.Parse(viewerRequest.Body!);
    var serverCreateRequest = System.Text.Json.JsonSerializer.Deserialize<CreateRoomRequest>(
        createRequest.Body!, jsonOptions);
    var serverEditorRequest = System.Text.Json.JsonSerializer.Deserialize<JoinRoomRequest>(
        editorRequest.Body!, jsonOptions);
    var serverViewerRequest = System.Text.Json.JsonSerializer.Deserialize<JoinRoomRequest>(
        viewerRequest.Body!, jsonOptions);
    Assert(createJson.RootElement.GetProperty("name").GetString() == "Android Room" &&
           !createJson.RootElement.GetProperty("isPrivate").GetBoolean() &&
           createJson.RootElement.GetProperty("password").ValueKind ==
               System.Text.Json.JsonValueKind.Null &&
           createJson.RootElement.GetProperty("isHidden").GetBoolean() &&
           !createJson.RootElement.TryGetProperty("Name", out _) &&
           serverCreateRequest == new CreateRoomRequest("Android Room", false, null, true) &&
           createRequest.HasContentHeader("Content-Type", "application/json; charset=utf-8"),
        "room creation JSON stopped using the existing camelCase/null-password contract");
    Assert(editorJson.RootElement.GetProperty("password").ValueKind ==
               System.Text.Json.JsonValueKind.Null &&
           editorJson.RootElement.GetProperty("memberRole").GetString() == "editor" &&
           viewerJson.RootElement.GetProperty("password").GetString() == "private-pass" &&
           viewerJson.RootElement.GetProperty("memberRole").GetString() == "viewer" &&
           !viewerJson.RootElement.TryGetProperty("MemberRole", out _) &&
           serverEditorRequest == new JoinRoomRequest(null, "editor") &&
           serverViewerRequest == new JoinRoomRequest("private-pass", "viewer") &&
           editorRequest.HasContentHeader("Content-Type", "application/json; charset=utf-8") &&
           viewerRequest.HasContentHeader("Content-Type", "application/json; charset=utf-8"),
        "join or spectate JSON stopped preserving password and memberRole semantics");

    Assert(typeof(OnlineRoomApi).Assembly == typeof(OnlineAuthApi).Assembly,
        "room transport was not added to the guarded cross-platform online assembly");

    HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T body) => new(statusCode)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body, jsonOptions), Encoding.UTF8, "application/json")
    };
}

static void TestOnlineResourceTransport() =>
    TestOnlineResourceTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineResourceTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var roomId = Guid.Parse("f1111111-a222-b333-c444-d55555555555");
    var manifestHash = new string('a', 64);
    var chartHash = new string('b', 64);
    var audioHash = new string('c', 64);
    const long manifestBytes = 8_589_934_700;
    const long chartBytes = 4_294_967_300;
    var serverManifest = new RoomResourceManifest(
        manifestBytes,
        manifestHash,
        "working.mc",
        [
            new RoomResourceEntry("working.mc", chartBytes, chartHash),
            new RoomResourceEntry("audio/song.ogg", 7_654_321, audioHash)
        ]);
    var blobPayload = Enumerable.Range(0, 4_097)
        .Select(index => (byte)(index * 31 + 7))
        .ToArray();
    var archivePayload = Enumerable.Range(0, 8_257)
        .Select(index => (byte)(index * 17 + 11))
        .ToArray();

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(serverManifest, jsonOptions),
            Encoding.UTF8,
            "application/json")
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new ShortReadStream(blobPayload, 13))
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new ShortReadStream(archivePayload, 17))
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(
                new ApiError("resource_not_found", "房间中没有该资源或缓存已过期。"),
                jsonOptions),
            Encoding.UTF8,
            "application/json")
    });
    using var blockingSource = new BlockingReadStream([9, 8, 7, 6]);
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(blockingSource)
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "X-LumaChart-Client-Profile", "Resource-Tablet-Spec");
    var endpoint = new Uri("https://unit.lumachart.invalid/");
    var api = new OnlineResourceApi(client, endpoint);
    AssertThrows<ArgumentException>(
        () => _ = new OnlineResourceApi(client, new Uri("http://insecure.example.test/")),
        "the shared resource transport accepted a non-loopback plaintext HTTP endpoint");
    _ = new OnlineResourceApi(client, new Uri("http://127.0.0.1:5080/"));

    var manifest = await api.GetManifestAsync(roomId, "manifest-token");
    Assert(manifest is
           {
               TotalBytes: manifestBytes,
               Sha256: var returnedManifestHash,
               ChartEntry: "working.mc",
               Entries.Count: 2
           } && returnedManifestHash == manifestHash &&
           manifest.Entries[0] is
           {
               Path: "working.mc",
               Length: chartBytes,
               Sha256: var returnedChartHash
           } && returnedChartHash == chartHash &&
           manifest.Entries[1] is
           {
               Path: "audio/song.ogg",
               Length: 7_654_321,
               Sha256: var returnedAudioHash
           } && returnedAudioHash == audioHash,
        "resource manifest DTOs lost an Int64, archive field or entry field");

    using var destination = new TrackingWriteStream();
    var transferred = await api.DownloadBlobAsync(
        roomId, chartHash, "blob-token", destination);
    Assert(transferred == blobPayload.LongLength &&
           destination.ToArray().SequenceEqual(blobPayload) &&
           !destination.IsDisposed,
        "short HTTP reads were truncated or the caller-owned destination was disposed");

    using var archiveDestination = new TrackingWriteStream();
    var archiveProgress = new List<long>();
    var archiveTransferred = await api.DownloadArchiveAsync(
        roomId,
        "archive-token",
        archiveDestination,
        new InlineProgress<long>(archiveProgress.Add));
    Assert(archiveTransferred == archivePayload.LongLength &&
           archiveDestination.ToArray().SequenceEqual(archivePayload) &&
           !archiveDestination.IsDisposed,
        "short archive reads were truncated or the caller-owned destination was disposed");
    Assert(archiveProgress.Count > 1 &&
           archiveProgress[0] == 17 &&
           archiveProgress[^1] == archivePayload.LongLength &&
           archiveProgress.Zip(archiveProgress.Skip(1)).All(pair => pair.First < pair.Second),
        "archive progress was not reported as a strictly increasing cumulative byte count");

    using var errorDestination = new TrackingWriteStream();
    OnlineApiException? structuredFailure = null;
    try
    {
        await api.DownloadBlobAsync(roomId, audioHash, "error-token", errorDestination);
    }
    catch (OnlineApiException exception)
    {
        structuredFailure = exception;
    }
    Assert(structuredFailure is
           {
               Code: "resource_not_found",
               StatusCode: HttpStatusCode.NotFound,
               Message: "房间中没有该资源或缓存已过期。"
           } && errorDestination.Length == 0 && !errorDestination.IsDisposed,
        "resource errors lost their structure, wrote a failed body or disposed the destination");

    using var canceledDestination = new TrackingWriteStream();
    using var cancellation = new CancellationTokenSource();
    var canceledDownload = api.DownloadBlobAsync(
        roomId, chartHash, "cancel-token", canceledDestination,
        cancellationToken: cancellation.Token);
    var cancellationObserved = false;
    try
    {
        await blockingSource.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            _ = await canceledDownload.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        catch (TimeoutException)
        {
        }
    }
    finally
    {
        cancellation.Cancel();
        blockingSource.Release();
    }
    if (!canceledDownload.IsCompleted)
    {
        try { _ = await canceledDownload.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }
    Assert(cancellationObserved && !canceledDestination.IsDisposed,
        "resource response-body cancellation was not observed or disposed the caller destination");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "resource-ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "resource transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 6,
        "resource transport sent an unexpected number of requests");
    var manifestRequest = handler.Requests[0];
    var blobRequest = handler.Requests[1];
    var archiveRequest = handler.Requests[2];
    var errorRequest = handler.Requests[3];
    var cancellationRequest = handler.Requests[4];
    var ownershipRequest = handler.Requests[5];
    Assert(manifestRequest.Method == HttpMethod.Get &&
           manifestRequest.RequestUri == new Uri(endpoint, $"api/rooms/{roomId:D}/resources") &&
           manifestRequest.AuthorizationScheme == "Bearer" &&
           manifestRequest.AuthorizationParameter == "manifest-token",
        "resource manifest method, URL or bearer token changed during extraction");
    var chartBlobUri = new Uri(endpoint, $"api/rooms/{roomId:D}/resources/files/{chartHash}");
    Assert(blobRequest.Method == HttpMethod.Get && blobRequest.RequestUri == chartBlobUri &&
           blobRequest.AuthorizationScheme == "Bearer" &&
           blobRequest.AuthorizationParameter == "blob-token" &&
           archiveRequest.Method == HttpMethod.Get &&
           archiveRequest.RequestUri == new Uri(endpoint, $"api/rooms/{roomId:D}/resources/archive") &&
           archiveRequest.AuthorizationScheme == "Bearer" &&
           archiveRequest.AuthorizationParameter == "archive-token" &&
           errorRequest.Method == HttpMethod.Get &&
           errorRequest.RequestUri == new Uri(endpoint, $"api/rooms/{roomId:D}/resources/files/{audioHash}") &&
           errorRequest.AuthorizationParameter == "error-token" &&
           cancellationRequest.Method == HttpMethod.Get &&
           cancellationRequest.RequestUri == chartBlobUri &&
           cancellationRequest.AuthorizationParameter == "cancel-token",
        "resource blob method, URL or per-request bearer token changed during extraction");
    Assert(ownershipRequest.RequestUri == new Uri(endpoint, "resource-ownership-probe") &&
           ownershipRequest.AuthorizationParameter == null,
        "resource transport ownership probe used an unexpected URL or authorization");
    Assert(handler.Requests.All(request =>
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Resource-Tablet-Spec")),
        "version or client-profile headers were lost by the resource transport");
    Assert(typeof(OnlineResourceApi).Assembly == typeof(OnlineRoomApi).Assembly,
        "resource transport was not added to the guarded cross-platform online assembly");
}

static void TestOnlineResourceUploadTransport() =>
    TestOnlineResourceUploadTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineResourceUploadTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var endpoint = new Uri("https://upload.unit.lumachart.invalid/");
    var roomId = Guid.Parse("e1111111-a222-b333-c444-d55555555555");
    var chartHash = new string('d', 64);
    var blobHash = new string('e', 64);
    var manifestHash = new string('f', 64);
    const long chartBytes = 4_294_967_301;
    const long totalBytes = 8_589_934_701;
    const long missingBytes = 4_294_967_307;
    var uploadPayload = Enumerable.Range(0, 4_103)
        .Select(index => (byte)(index * 29 + 5))
        .ToArray();
    OnlineRoomResourceEntryDto[] entries =
    [
        new("working.mc", chartBytes, chartHash),
        new("audio/song.ogg", uploadPayload.LongLength, blobHash)
    ];

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(
                new RoomResourcePlan(totalBytes, missingBytes, [blobHash]), jsonOptions),
            Encoding.UTF8,
            "application/json")
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(
                new RoomResourceManifest(
                    totalBytes,
                    manifestHash,
                    "working.mc",
                    [
                        new RoomResourceEntry("working.mc", chartBytes, chartHash),
                        new RoomResourceEntry("audio/song.ogg", uploadPayload.LongLength, blobHash)
                    ]),
                jsonOptions),
            Encoding.UTF8,
            "application/json")
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(
                new ApiError("invalid_resource_manifest", "资源清单无效。"), jsonOptions),
            Encoding.UTF8,
            "application/json")
    });
    // A canceled request stops while the fake handler is serializing its body, so
    // its queued response remains available to the ownership probe. Keep one extra
    // response to make a cancellation regression fail on the explicit assertion,
    // rather than obscuring it with an empty fake queue.
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler);
    var api = new OnlineResourceApi(client, endpoint);

    var plan = await api.PlanAsync(roomId, entries, "plan-token");
    Assert(plan is
           {
               TotalBytes: totalBytes,
               MissingBytes: missingBytes,
               MissingSha256.Count: 1
           } && plan.MissingSha256[0] == blobHash,
        "resource upload plan lost Int64 totals or the server's missing SHA-256 set");

    using var uploadSource = new ShortReadStream(uploadPayload, 11);
    var uploadProgress = new List<long>();
    await api.UploadBlobAsync(
        roomId,
        blobHash,
        uploadSource,
        uploadPayload.LongLength,
        "blob-upload-token",
        new InlineProgress<long>(uploadProgress.Add));
    Assert(!uploadSource.IsDisposed,
        "resource upload transport disposed the caller-owned source Stream");
    Assert(uploadProgress.Count > 1 &&
           uploadProgress[0] == 11 &&
           uploadProgress[^1] == uploadPayload.LongLength &&
           uploadProgress.Zip(uploadProgress.Skip(1)).All(pair => pair.First < pair.Second),
        "blob upload progress was not a strictly increasing per-blob cumulative byte count");

    var committed = await api.CommitAsync(roomId, entries, "commit-token");
    Assert(committed is
           {
               TotalBytes: totalBytes,
               Sha256: var returnedManifestHash,
               ChartEntry: "working.mc",
               Entries.Count: 2
           } && returnedManifestHash == manifestHash &&
           committed.Entries[0] is
           {
               Path: "working.mc",
               Length: chartBytes,
               Sha256: var returnedChartHash
           } && returnedChartHash == chartHash &&
           committed.Entries[1] is
           {
               Path: "audio/song.ogg",
               Length: var returnedBlobBytes,
               Sha256: var returnedBlobHash
           } && returnedBlobBytes == uploadPayload.LongLength && returnedBlobHash == blobHash,
        "resource commit response lost an Int64, manifest field or entry field");

    OnlineApiException? structuredFailure = null;
    try
    {
        _ = await api.PlanAsync(roomId, entries, "error-token");
    }
    catch (OnlineApiException exception)
    {
        structuredFailure = exception;
    }
    Assert(structuredFailure is
           {
               Code: "invalid_resource_manifest",
               StatusCode: HttpStatusCode.BadRequest,
               Message: "资源清单无效。"
           },
        "resource upload errors lost their server code, status or message");

    using var blockingSource = new BlockingReadStream([9, 8, 7, 6]);
    using var uploadCancellation = new CancellationTokenSource();
    var canceledUpload = api.UploadBlobAsync(
        roomId,
        blobHash,
        blockingSource,
        4,
        "cancel-upload-token",
        cancellationToken: uploadCancellation.Token);
    var cancellationObserved = false;
    try
    {
        await blockingSource.Started.WaitAsync(TimeSpan.FromSeconds(2));
        uploadCancellation.Cancel();
        try
        {
            await canceledUpload.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        catch (TimeoutException)
        {
        }
    }
    finally
    {
        uploadCancellation.Cancel();
        blockingSource.Release();
    }
    if (!canceledUpload.IsCompleted)
    {
        try { await canceledUpload.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }
    Assert(cancellationObserved && !blockingSource.IsDisposed,
        "upload cancellation did not reach source.ReadAsync or disposed the caller source");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "upload-ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "resource upload transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 5,
        "resource upload transport sent an unexpected number of completed requests");
    var planRequest = handler.Requests[0];
    var blobRequest = handler.Requests[1];
    var commitRequest = handler.Requests[2];
    var errorRequest = handler.Requests[3];
    var ownershipRequest = handler.Requests[4];
    Assert(planRequest.Method == HttpMethod.Post &&
           planRequest.RequestUri == new Uri(endpoint, $"api/rooms/{roomId:D}/resources/plan") &&
           planRequest.AuthorizationScheme == "Bearer" &&
           planRequest.AuthorizationParameter == "plan-token" &&
           blobRequest.Method == HttpMethod.Put &&
           blobRequest.RequestUri == new Uri(endpoint, $"api/rooms/{roomId:D}/resources/blobs/{blobHash}") &&
           blobRequest.AuthorizationScheme == "Bearer" &&
           blobRequest.AuthorizationParameter == "blob-upload-token" &&
           commitRequest.Method == HttpMethod.Post &&
           commitRequest.RequestUri == new Uri(endpoint, $"api/rooms/{roomId:D}/resources/commit") &&
           commitRequest.AuthorizationScheme == "Bearer" &&
           commitRequest.AuthorizationParameter == "commit-token",
        "resource plan/blob/commit method, URL or per-request bearer token changed");
    Assert(blobRequest.BodyBytes?.SequenceEqual(uploadPayload) == true &&
           blobRequest.HasContentHeader("Content-Type", "application/octet-stream") &&
           blobRequest.HasContentHeader(
               "Content-Length", uploadPayload.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        "blob upload changed its complete short-read body, media type or declared Content-Length");

    using var planJson = System.Text.Json.JsonDocument.Parse(planRequest.Body!);
    using var commitJson = System.Text.Json.JsonDocument.Parse(commitRequest.Body!);
    var planRoot = planJson.RootElement;
    var commitRoot = commitJson.RootElement;
    Assert(planRoot.TryGetProperty("entries", out var planEntries) &&
           !planRoot.TryGetProperty("Entries", out _) &&
           planEntries.GetArrayLength() == 2 &&
           planEntries[0].GetProperty("path").GetString() == "working.mc" &&
           planEntries[0].GetProperty("length").GetInt64() == chartBytes &&
           planEntries[0].GetProperty("sha256").GetString() == chartHash &&
           planEntries[1].GetProperty("length").GetInt64() == uploadPayload.LongLength &&
           commitRoot.TryGetProperty("entries", out var commitEntries) &&
           !commitRoot.TryGetProperty("Entries", out _) &&
           commitEntries.GetRawText() == planEntries.GetRawText(),
        "resource plan/commit request DTOs lost camelCase, Int64 or complete entry fields");
    Assert(errorRequest.Method == HttpMethod.Post &&
           errorRequest.RequestUri == planRequest.RequestUri &&
           errorRequest.AuthorizationParameter == "error-token" &&
           ownershipRequest.RequestUri == new Uri(endpoint, "upload-ownership-probe") &&
           ownershipRequest.AuthorizationParameter == null,
        "resource upload error or ownership probe request changed unexpectedly");
}

static void TestOnlineChartTransport() =>
    TestOnlineChartTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineChartTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var endpoint = new Uri("https://chart.unit.lumachart.invalid/");
    var roomId = Guid.Parse("d1111111-a222-b333-c444-d55555555555");
    var noteId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    var timingId = Guid.Parse("21111111-2222-3333-4444-555555555555");
    var lockId = Guid.Parse("31111111-2222-3333-4444-555555555555");
    var operationId = Guid.Parse("41111111-2222-3333-4444-555555555555");
    var snapshotId = Guid.Parse("51111111-2222-3333-4444-555555555555");
    var safetySnapshotId = Guid.Parse("61111111-2222-3333-4444-555555555555");
    const long baseRevision = 4_294_967_300;
    const long currentRevision = 4_294_967_301;
    const long actorUserId = 8_589_934_700;
    var createdAt = new DateTimeOffset(2026, 7, 20, 8, 9, 10, TimeSpan.Zero);

    var metadata = new OnlineChartMetadata(
        "Transport title", "传输标题", "Transport artist", "传输艺术家", "Mapper",
        "REST", "audio/song.ogg", "cover.jpg", "background.jpg", 12_345, 8.5, 4.5);
    var note = new OnlineChartNote(
        noteId, 6, new OnlineSyncBeat(4, 1, 2), new OnlineSyncBeat(8, 3, 4),
        "keysounds/hit.wav", 64, 12, false, -3, "{\"future\":true}", actorUserId);
    var timing = new OnlineChartTiming(
        timingId, new OnlineSyncBeat(2, 1, 4), 180, 24, 7, 1.25,
        "1.25", "camera_zoom", "{\"custom\":1}", actorUserId);
    var initialLockRange = new OnlineChartLockRange(
        new OnlineSyncBeat(5, 5, 6), new OnlineSyncBeat(6, 1, 12));
    var initializeRequest = new InitializeOnlineChartRequest(
        7, [note], [timing], metadata, [initialLockRange]);
    var operationBatch = new OnlineChartOperationBatch(
        baseRevision, [note], [Guid.Parse("71111111-2222-3333-4444-555555555555")],
        [timing], [Guid.Parse("81111111-2222-3333-4444-555555555555")],
        operationId, metadata, 7);

    var serverInitialize = System.Text.Json.JsonSerializer.Deserialize<InitializeRoomChartRequest>(
                               System.Text.Json.JsonSerializer.Serialize(initializeRequest, jsonOptions),
                               jsonOptions)
                           ?? throw new InvalidOperationException("chart initialize compatibility setup failed");
    var serverBatch = System.Text.Json.JsonSerializer.Deserialize<RoomChartOperationBatch>(
                          System.Text.Json.JsonSerializer.Serialize(operationBatch, jsonOptions),
                          jsonOptions)
                      ?? throw new InvalidOperationException("chart operation compatibility setup failed");
    var serverLock = new RoomChartLock(
        lockId, new SyncBeat(4, 0, 1), new SyncBeat(8, 0, 1), -64, 81, actorUserId);
    var serverSnapshot = new RoomChartSnapshot(
        currentRevision,
        serverInitialize.KeyCount,
        serverInitialize.Notes,
        serverInitialize.Timing,
        [serverLock],
        serverInitialize.Metadata);
    var serverOperation = new AppliedRoomChartOperation(
        currentRevision, actorUserId, serverBatch, createdAt);
    var serverDelta = new RoomChartDelta(
        currentRevision, [serverOperation], [serverLock], true, baseRevision, true);
    var serverApply = new RoomChartApplyResult(serverSnapshot, serverOperation);
    var serverSavedSnapshot = new RoomChartSavedSnapshot(
        snapshotId, "Before chorus", baseRevision, actorUserId, "Owner",
        4_294_967, 65_537, false, createdAt);
    var serverRollback = new RoomChartRollbackResult(
        serverSnapshot, serverSavedSnapshot, safetySnapshotId);
    var serverLockSelection = new ToggleRoomChartLockSelectionResult(false, [serverLock]);

    using var blockingJson = new BlockingReadStream(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(serverSnapshot, jsonOptions)));
    var blockingContent = new StreamContent(blockingJson);
    blockingContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverSnapshot));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverSnapshot));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverDelta));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverApply));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverDelta));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[] { serverLock }));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverLockSelection));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[] { serverSavedSnapshot }));
    handler.Enqueue(JsonResponse(HttpStatusCode.Created, serverSavedSnapshot));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverRollback));
    var budgetResponse = JsonResponse(
        HttpStatusCode.TooManyRequests,
        new ApiError("chart_write_budget", "谱面写入数据量过大，请稍后重试或缩小单次操作。"));
    budgetResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
    handler.Enqueue(budgetResponse);
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = blockingContent });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "X-LumaChart-Client-Profile", "Chart-Tablet-Spec");
    var api = new OnlineChartApi(client, endpoint);
    AssertThrows<ArgumentException>(
        () => _ = new OnlineChartApi(client, new Uri("http://insecure.example.test/")),
        "the shared chart transport accepted a non-loopback plaintext HTTP endpoint");
    _ = new OnlineChartApi(client, new Uri("http://127.0.0.1:5080/"));

    var initialized = await api.InitializeAsync(roomId, initializeRequest, "initialize-token");
    var snapshot = await api.GetSnapshotAsync(roomId, "snapshot-token");
    var delta = await api.GetDeltaAsync(roomId, baseRevision, "delta-token");
    var applied = await api.ApplyOperationsAsync(roomId, operationBatch, "operation-token");
    var appliedDelta = await api.ApplyOperationsDeltaAsync(
        roomId, operationBatch, "operation-delta-token");
    var locks = await api.ToggleLockAsync(
        roomId, new OnlineSyncBeat(5, 5, 6), new OnlineSyncBeat(6, 1, 12), "lock-token");
    var lockSelection = await api.ToggleLockSelectionAsync(
        roomId,
        [
            new OnlineChartLockRange(
                new OnlineSyncBeat(5, 5, 6),
                new OnlineSyncBeat(6, 1, 12)),
            new OnlineChartLockRange(
                new OnlineSyncBeat(8, 3, 6),
                new OnlineSyncBeat(8, 7, 12))
        ],
        "lock-selection-token");
    var savedSnapshots = await api.GetSavedSnapshotsAsync(roomId, "snapshots-token");
    var createdSnapshot = await api.CreateSavedSnapshotAsync(
        roomId, "Before chorus", "create-snapshot-token");
    var rollback = await api.RollbackSavedSnapshotAsync(
        roomId, snapshotId, operationId, "rollback-token");

    Assert(initialized is
           {
               Revision: currentRevision,
               KeyCount: 7,
               Notes.Count: 1,
               Timing.Count: 1,
               Locks.Count: 1,
               Metadata.TitleOriginal: "传输标题"
           } && initialized.Notes[0] is
           {
               Id: var returnedNoteId,
               Lane: 6,
               CreatorUserId: actorUserId,
               EndBeat: { Bar: 8, Numerator: 3, Denominator: 4 }
           } && returnedNoteId == noteId &&
           snapshot?.Revision == currentRevision &&
           delta is
           {
               CurrentRevision: currentRevision,
               HasMore: true,
               AuthoritativeBaselineRevision: baseRevision,
               RequiresFullSnapshot: true,
               Operations.Count: 1,
               Locks.Count: 1
           } && delta.Operations[0] is
           {
               ActorUserId: actorUserId,
               CreatedAt: var returnedCreatedAt,
               Batch.OperationId: var returnedOperationId,
               Batch.KeyCount: 7,
               Batch.Metadata.Title: "Transport title"
           } && returnedCreatedAt == createdAt && returnedOperationId == operationId &&
           applied is
           {
               Snapshot.Revision: currentRevision,
               AppliedOperation.ActorUserId: actorUserId
           } &&
           appliedDelta?.Operations.Single().Batch.OperationId == operationId &&
           locks?.Single() is
           {
               Id: var returnedLockId,
               StartLane: -64,
               EndLane: 81,
               OwnerUserId: actorUserId
           } && returnedLockId == lockId &&
           lockSelection is
           {
               IsUnlock: false,
               Locks.Count: 1
           } && lockSelection.Locks[0].Id == lockId &&
           savedSnapshots?.Single() is
           {
               Id: var returnedSavedId,
               SourceRevision: baseRevision,
               CreatorUserId: actorUserId,
               CreatorDisplayName: "Owner",
               NoteCount: 4_294_967,
               TimingCount: 65_537,
               IsAutomatic: false,
               CreatedAt: var returnedSavedAt
           } && returnedSavedId == snapshotId && returnedSavedAt == createdAt &&
           createdSnapshot?.Id == snapshotId &&
           rollback is
           {
               Snapshot.Revision: currentRevision,
               RestoredSnapshot.Id: var restoredId,
               SafetySnapshotId: var returnedSafetyId
           } && restoredId == snapshotId && returnedSafetyId == safetySnapshotId,
        "chart REST response DTOs lost snapshot, delta, operation, lock, saved-snapshot or Int64 fields");

    OnlineApiException? structuredFailure = null;
    try
    {
        _ = await api.ApplyOperationsAsync(roomId, operationBatch, "budget-token");
    }
    catch (OnlineApiException exception)
    {
        structuredFailure = exception;
    }
    Assert(structuredFailure is
           {
               Code: "chart_write_budget",
               StatusCode: HttpStatusCode.TooManyRequests,
               RetryAfter: var retryAfter
           } && retryAfter == TimeSpan.FromSeconds(7) &&
           structuredFailure.Message.Contains("谱面写入数据量过大", StringComparison.Ordinal),
        "chart transport errors lost server code, status, message or Retry-After");

    using var cancellation = new CancellationTokenSource();
    var pendingSnapshot = api.GetSnapshotAsync(roomId, "cancel-token", cancellation.Token);
    var cancellationObserved = false;
    try
    {
        await blockingJson.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            _ = await pendingSnapshot.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        catch (TimeoutException)
        {
        }
    }
    finally
    {
        cancellation.Cancel();
        blockingJson.Release();
    }
    if (!pendingSnapshot.IsCompleted)
    {
        try { _ = await pendingSnapshot.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }
    Assert(cancellationObserved,
        "chart transport cancellation did not reach the JSON response body");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "chart-ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "chart transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 13,
        "chart transport sent an unexpected number of requests");
    (HttpMethod Method, string Path, string? Token)[] expectedRequests =
    [
        (HttpMethod.Put, $"api/rooms/{roomId:D}/chart", "initialize-token"),
        (HttpMethod.Get, $"api/rooms/{roomId:D}/chart", "snapshot-token"),
        (HttpMethod.Get, $"api/rooms/{roomId:D}/chart/delta?after={baseRevision}", "delta-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chart/operations", "operation-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chart/operations/delta", "operation-delta-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chart/locks/toggle", "lock-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chart/locks/toggle-selection", "lock-selection-token"),
        (HttpMethod.Get, $"api/rooms/{roomId:D}/chart/snapshots", "snapshots-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chart/snapshots", "create-snapshot-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chart/snapshots/{snapshotId:D}/rollback", "rollback-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chart/operations", "budget-token"),
        (HttpMethod.Get, $"api/rooms/{roomId:D}/chart", "cancel-token")
    ];
    for (var index = 0; index < expectedRequests.Length; index++)
    {
        var expected = expectedRequests[index];
        var actual = handler.Requests[index];
        Assert(actual.Method == expected.Method &&
               actual.RequestUri == new Uri(endpoint, expected.Path) &&
               actual.AuthorizationScheme == "Bearer" &&
               actual.AuthorizationParameter == expected.Token,
            $"chart REST request {index} changed method, URL or bearer token");
    }
    var ownershipRequest = handler.Requests[12];
    Assert(ownershipRequest.Method == HttpMethod.Get &&
           ownershipRequest.RequestUri == new Uri(endpoint, "chart-ownership-probe") &&
           ownershipRequest.AuthorizationParameter == null,
        "chart transport ownership probe changed unexpectedly");
    Assert(handler.Requests.All(request =>
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Chart-Tablet-Spec")),
        "version or client-profile headers were lost by the chart transport");

    foreach (var index in new[] { 0, 3, 4, 5, 6, 8, 9, 10 })
        Assert(handler.Requests[index].HasContentHeader(
                "Content-Type", "application/json; charset=utf-8"),
            $"chart JSON request {index} lost its media type");

    var sentInitialize = System.Text.Json.JsonSerializer.Deserialize<InitializeRoomChartRequest>(
                             handler.Requests[0].Body!, jsonOptions)
                         ?? throw new InvalidOperationException("initialize request did not match server DTO");
    var sentOperation = System.Text.Json.JsonSerializer.Deserialize<RoomChartOperationBatch>(
                            handler.Requests[3].Body!, jsonOptions)
                        ?? throw new InvalidOperationException("operation request did not match server DTO");
    var sentOperationDelta = System.Text.Json.JsonSerializer.Deserialize<RoomChartOperationBatch>(
                                 handler.Requests[4].Body!, jsonOptions)
                             ?? throw new InvalidOperationException("operation delta did not match server DTO");
    var sentLock = System.Text.Json.JsonSerializer.Deserialize<ToggleRoomChartLockRequest>(
                       handler.Requests[5].Body!, jsonOptions)
                   ?? throw new InvalidOperationException("lock request did not match server DTO");
    var sentLockSelection = System.Text.Json.JsonSerializer.Deserialize<ToggleRoomChartLockSelectionRequest>(
                                handler.Requests[6].Body!, jsonOptions)
                            ?? throw new InvalidOperationException("lock selection request did not match server DTO");
    var sentCreate = System.Text.Json.JsonSerializer.Deserialize<CreateRoomChartSavedSnapshotRequest>(
                         handler.Requests[8].Body!, jsonOptions)
                     ?? throw new InvalidOperationException("snapshot request did not match server DTO");
    var sentRollback = System.Text.Json.JsonSerializer.Deserialize<RollbackRoomChartSavedSnapshotRequest>(
                           handler.Requests[9].Body!, jsonOptions)
                       ?? throw new InvalidOperationException("rollback request did not match server DTO");
    using var initializeJson = System.Text.Json.JsonDocument.Parse(handler.Requests[0].Body!);
    using var operationJson = System.Text.Json.JsonDocument.Parse(handler.Requests[3].Body!);
    Assert(initializeJson.RootElement.TryGetProperty("keyCount", out var wireKeyCount) &&
           wireKeyCount.GetInt32() == 7 &&
           initializeJson.RootElement.TryGetProperty("notes", out _) &&
           initializeJson.RootElement.TryGetProperty("timing", out _) &&
           initializeJson.RootElement.TryGetProperty("metadata", out _) &&
           initializeJson.RootElement.TryGetProperty("initialLocks", out var wireInitialLocks) &&
           wireInitialLocks.GetArrayLength() == 1 &&
           !initializeJson.RootElement.TryGetProperty("KeyCount", out _) &&
           sentInitialize.Notes.Single().CreatorUserId == actorUserId &&
           sentInitialize.Timing.Single().EffectFieldName == "camera_zoom" &&
           sentInitialize.Metadata?.AudioFile == "audio/song.ogg" &&
           sentInitialize.InitialLocks?.Single() == new RoomChartLockRange(
               new SyncBeat(5, 5, 6), new SyncBeat(6, 1, 12)),
        "initialize JSON stopped matching the camelCase server chart DTO");
    Assert(operationJson.RootElement.TryGetProperty("baseRevision", out var wireRevision) &&
           wireRevision.GetInt64() == baseRevision &&
           operationJson.RootElement.TryGetProperty("operationId", out var wireOperationId) &&
           wireOperationId.GetGuid() == operationId &&
           operationJson.RootElement.TryGetProperty("metadata", out _) &&
           operationJson.RootElement.TryGetProperty("keyCount", out var operationKeyCount) &&
           operationKeyCount.GetInt32() == 7 &&
           !operationJson.RootElement.TryGetProperty("isEmpty", out _) &&
           !operationJson.RootElement.TryGetProperty("IsEmpty", out _) &&
           handler.Requests[3].Body == handler.Requests[4].Body &&
           sentOperationDelta.OperationId == operationId &&
           sentOperation.Metadata?.TitleOriginal == "传输标题" &&
           sentOperation.DeleteNoteIds.Count == 1 && sentOperation.DeleteTimingIds.Count == 1 &&
           sentLock == new ToggleRoomChartLockRequest(new SyncBeat(5, 5, 6), new SyncBeat(6, 1, 12)) &&
           sentLockSelection.Ranges.SequenceEqual(
           [
               new RoomChartLockRange(new SyncBeat(5, 5, 6), new SyncBeat(6, 1, 12)),
               new RoomChartLockRange(new SyncBeat(8, 3, 6), new SyncBeat(8, 7, 12))
           ]) &&
           sentCreate.Name == "Before chorus" && sentRollback.OperationId == operationId,
        "chart write JSON lost camelCase, Int64, operationId, Metadata+KeyCount or leaked IsEmpty");
    Assert(typeof(OnlineChartApi).Assembly == typeof(OnlineResourceApi).Assembly,
        "chart transport was not added to the guarded cross-platform online assembly");

    HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T body) => new(statusCode)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body, jsonOptions),
            Encoding.UTF8,
            "application/json")
    };
}
#endif

static void TestOnlineChartEventTransport() =>
    TestOnlineChartEventTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineChartEventTransportAsync()
{
    var endpoint = new Uri("https://events.unit.lumachart.invalid/");
    var roomId = Guid.Parse("c1111111-a222-b333-c444-d55555555555");
    var mixedFrames = Encoding.UTF8.GetBytes(
        "retry: 1000\n\n" +
        ": keep-alive\n\r\n" +
        "data: 7\n" +
        "DaTa: 8\r\n" +
        "data:42\n" +
        "data: invalid\n" +
        "data: 999999999999999999999999\n" +
        "data: -7\n" +
        "data: 9\ndata: 10\n\n" +
        "event: ignored\n" +
        "data: 13");
    using var mixedSource = new ShortReadStream(mixedFrames, 1);
    using var gatedSource = new PrefixThenTerminalReadStream(
        Encoding.UTF8.GetBytes("data: 11\r\n\r\ndata: 12\n\n"),
        maxChunkBytes: 1,
        blockAfterPrefix: true);
    using var faultSource = new PrefixThenTerminalReadStream(
        Encoding.UTF8.GetBytes("data: 21\n\n"),
        maxChunkBytes: 1,
        terminalException: new IOException("SSE connection reset"));

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(mixedSource)
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(gatedSource)
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(faultSource)
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
    {
        Content = new ByteArrayContent([])
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden)
    {
        Content = new StringContent(
            "{\"code\":\"member_required\",\"message\":\"请先加入房间。\"}",
            Encoding.UTF8,
            "application/json")
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound)
    {
        Content = new StringContent(
            "{\"code\":\"chart_not_ready\",\"message\":\"联机谱面尚未初始化。\"}",
            Encoding.UTF8,
            "application/json")
    });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "X-LumaChart-Client-Profile", "Sse-Tablet-Spec");
    var api = new OnlineChartEventApi(client, endpoint);
    AssertThrows<ArgumentException>(
        () => _ = new OnlineChartEventApi(client, new Uri("http://insecure.example.test/")),
        "the shared SSE transport accepted a non-loopback plaintext HTTP endpoint");
    _ = new OnlineChartEventApi(client, new Uri("http://127.0.0.1:5080/"));

    var revisions = new List<long>();
    await api.ListenForRevisionsAsync(
        roomId,
        "mixed-token",
        revision =>
        {
            revisions.Add(revision);
            return Task.CompletedTask;
        });
    Assert(revisions.SequenceEqual(new long[] { 7, 8, 42, -7, 9, 10, 13 }) &&
           mixedSource.IsDisposed,
        "SSE short reads, LF/CRLF, raw revisions, multi-data lines or EOF tail behavior changed");

    var firstCallbackStarted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var allowFirstCallback = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var secondCallbackSeen = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var gatedCancellation = new CancellationTokenSource();
    var gatedListen = api.ListenForRevisionsAsync(
        roomId,
        "gated-token",
        async revision =>
        {
            if (revision == 11)
            {
                firstCallbackStarted.TrySetResult();
                await allowFirstCallback.Task;
            }
            else if (revision == 12)
            {
                secondCallbackSeen.TrySetResult();
            }
        },
        gatedCancellation.Token);
    try
    {
        await firstCallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var prematureSecond = await Task.WhenAny(
            secondCallbackSeen.Task,
            Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert(prematureSecond != secondCallbackSeen.Task && !gatedListen.IsCompleted,
            "SSE callbacks stopped applying ordered backpressure or the response was pre-buffered");
        allowFirstCallback.TrySetResult();
        await secondCallbackSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await gatedSource.Started.WaitAsync(TimeSpan.FromSeconds(2));
        gatedCancellation.Cancel();
        await AssertCanceledAsync(gatedListen, "SSE blocking read did not observe cancellation");
    }
    finally
    {
        allowFirstCallback.TrySetResult();
        gatedCancellation.Cancel();
        gatedSource.Release();
    }
    Assert(gatedSource.IsDisposed,
        "SSE cancellation did not dispose the response-owned source stream");

    var faultRevisions = new List<long>();
    IOException? streamFailure = null;
    try
    {
        await api.ListenForRevisionsAsync(
            roomId,
            "fault-token",
            revision =>
            {
                faultRevisions.Add(revision);
                return Task.CompletedTask;
            });
    }
    catch (IOException exception)
    {
        streamFailure = exception;
    }
    Assert(streamFailure?.Message == "SSE connection reset" &&
           faultRevisions.SequenceEqual(new long[] { 21 }) &&
           faultSource.IsDisposed,
        "SSE midstream failures did not preserve prior callbacks, IOException or source disposal");

    var unauthorized = await CaptureEventFailureAsync("unauthorized-token");
    var forbidden = await CaptureEventFailureAsync("forbidden-token");
    var notFound = await CaptureEventFailureAsync("not-found-token");
    Assert(unauthorized is { StatusCode: HttpStatusCode.Unauthorized, Code: null } &&
           forbidden is
           {
               StatusCode: HttpStatusCode.Forbidden,
               Code: "member_required",
               Message: "请先加入房间。"
           } &&
           notFound is
           {
               StatusCode: HttpStatusCode.NotFound,
               Code: "chart_not_ready",
               Message: "联机谱面尚未初始化。"
           },
        "SSE HTTP setup errors lost their status, structured code or message");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "sse-ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "SSE transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 7,
        "SSE transport sent an unexpected number of requests");
    for (var index = 0; index < 6; index++)
    {
        var request = handler.Requests[index];
        Assert(request.Method == HttpMethod.Get &&
               request.RequestUri == new Uri(endpoint, $"api/rooms/{roomId:D}/chart/events") &&
               request.AuthorizationScheme == "Bearer" &&
               request.HasHeader("Accept", "text/event-stream") &&
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Sse-Tablet-Spec"),
            $"SSE request {index} changed method, URL, headers or transport defaults");
    }
    Assert(handler.Requests[0].AuthorizationParameter == "mixed-token" &&
           handler.Requests[1].AuthorizationParameter == "gated-token" &&
           handler.Requests[2].AuthorizationParameter == "fault-token" &&
           handler.Requests[3].AuthorizationParameter == "unauthorized-token" &&
           handler.Requests[4].AuthorizationParameter == "forbidden-token" &&
           handler.Requests[5].AuthorizationParameter == "not-found-token" &&
           handler.Requests[6].RequestUri == new Uri(endpoint, "sse-ownership-probe"),
        "SSE per-request bearer tokens or ownership probe changed");
    Assert(typeof(OnlineChartEventApi).Assembly == typeof(OnlineChartApi).Assembly,
        "SSE transport was not added to the guarded cross-platform Online assembly");

    async Task<OnlineApiException?> CaptureEventFailureAsync(string token)
    {
        try
        {
            await api.ListenForRevisionsAsync(roomId, token, _ => Task.CompletedTask);
            return null;
        }
        catch (OnlineApiException exception)
        {
            return exception;
        }
    }

    static async Task AssertCanceledAsync(Task task, string failureMessage)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert(false, failureMessage);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            Assert(false, failureMessage);
        }
    }
}

#if LUMACHART_SERVER
static void TestOnlineRoomLifecycleTransport() =>
    TestOnlineRoomLifecycleTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineRoomLifecycleTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var endpoint = new Uri("https://lifecycle.unit.lumachart.invalid/");
    var roomId = Guid.Parse("b1111111-a222-b333-c444-d55555555555");
    var invitationId = Guid.Parse("a1111111-b222-c333-d444-e55555555555");
    const long invitedUserId = 4_294_967_301;
    const long ownerUserId = 8_589_934_701;
    const long chatMessageId = 4_294_967_311;
    const long latestMessageId = 8_589_934_711;
    var createdAt = new DateTimeOffset(2026, 7, 20, 9, 10, 11, TimeSpan.Zero);
    var serverRoom = new RoomSummary(
        roomId, 4_294_967_321, "Lifecycle Room", ownerUserId, "Owner", 2, 8,
        false, true, true, 8_589_934_721, createdAt);
    var serverInvitation = new RoomInvitation(
        invitationId, roomId, serverRoom.Name, ownerUserId, "Owner", "owner-avatar", createdAt.AddMinutes(5));
    var serverMember = new RoomMember(
        invitedUserId, "member", "Member", "member-avatar", "editor", createdAt,
        true, "Contributor", [new UserTag("Mapper", "#123456", true)],
        true, false, true);
    var offlineMember = new RoomMember(
        invitedUserId + 1, "offline", "Offline Member", "offline-avatar", "viewer", createdAt.AddMinutes(-1),
        false, "Former", [new UserTag("Former", "#654321", true)],
        false, false, false);
    var serverChat = new RoomChatMessage(
        chatMessageId, invitedUserId, "member", "Member", "member-avatar", "editor",
        "hello lifecycle", createdAt.AddSeconds(1), "Contributor",
        [new UserTag("Mapper", "#123456", true)]);
    var serverLive = new RoomLiveState(
        serverRoom, [serverMember, offlineMember], [serverChat], latestMessageId);

    using var blockingJson = new BlockingReadStream(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(serverLive, jsonOptions)));
    var blockingContent = new StreamContent(blockingJson);
    blockingContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(JsonResponse(HttpStatusCode.Created, serverInvitation));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[] { serverInvitation }));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverRoom));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverLive));
    handler.Enqueue(JsonResponse(HttpStatusCode.Created, serverChat));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(MalformedResponse(HttpStatusCode.NotFound));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
    handler.Enqueue(MalformedResponse(HttpStatusCode.Forbidden));
    handler.Enqueue(MalformedResponse(HttpStatusCode.NotFound));
    handler.Enqueue(MalformedResponse(HttpStatusCode.Forbidden));
    handler.Enqueue(MalformedResponse(HttpStatusCode.NotFound));
    handler.Enqueue(MalformedResponse(HttpStatusCode.Forbidden));
    handler.Enqueue(MalformedResponse(HttpStatusCode.NotFound));
    handler.Enqueue(JsonResponse(
        HttpStatusCode.Forbidden,
        new ApiError("owner_required", "只有房主可以修改成员身份。")));
    var chatRateLimit = JsonResponse(
        HttpStatusCode.TooManyRequests,
        new ApiError("chat_rate_limited", "发送过快，请等待 9 秒。"));
    chatRateLimit.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
    handler.Enqueue(chatRateLimit);
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = blockingContent });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "X-LumaChart-Client-Profile", "Lifecycle-Tablet-Spec");
    var api = new OnlineRoomLifecycleApi(client, endpoint);
    AssertThrows<ArgumentException>(
        () => _ = new OnlineRoomLifecycleApi(client, new Uri("http://insecure.example.test/")),
        "the shared lifecycle transport accepted a non-loopback plaintext HTTP endpoint");
    _ = new OnlineRoomLifecycleApi(client, new Uri("http://127.0.0.1:5080/"));

    var invited = await api.InviteAsync(roomId, invitedUserId, "invite-token");
    var invitations = await api.GetInvitationsAsync("list-token");
    var canceled = await api.RespondToInvitationAsync(
        invitationId, "cancel", "cancel-invite-token");
    var accepted = await api.RespondToInvitationAsync(
        invitationId, "watch", "accept-invite-token");
    await api.UpdateMemberRoleAsync(
        roomId, invitedUserId, "viewer", "role-token");
    await api.UpdateMemberAccessAsync(
        roomId, invitedUserId, "editor", true, false, true, "access-token");
    await api.RemoveMemberAsync(
        roomId, invitedUserId, -17, "remove-token");
    var live = await api.GetLiveStateAsync(
        roomId, latestMessageId, "live-token");
    var chat = await api.SendChatMessageAsync(
        roomId, "hello lifecycle", "chat-token");
    var heartbeat = await api.SendHeartbeatAsync(roomId, "heartbeat-token");
    var leave = await api.LeaveAsync(roomId, "leave-token");
    var emptyAccepted = await api.RespondToInvitationAsync(
        invitationId, "join", "empty-invite-token");

    Assert(invited is
           {
               Id: var returnedInvitationId,
               RoomId: var returnedInvitationRoomId,
               RoomName: "Lifecycle Room",
               InviterUserId: ownerUserId,
               InviterDisplayName: "Owner",
               InviterAvatarKey: "owner-avatar",
               ExpiresAt: var returnedExpiry
           } && returnedInvitationId == invitationId && returnedInvitationRoomId == roomId &&
           returnedExpiry == createdAt.AddMinutes(5) &&
           invitations?.Single().InviterUserId == ownerUserId &&
           canceled is { Kind: OnlineRoomInvitationResponseKind.Canceled, Room: null } &&
           accepted is
           {
               Kind: OnlineRoomInvitationResponseKind.Room,
               Room.RoomNumber: 4_294_967_321,
               Room.OwnerUserId: ownerUserId,
               Room.ResourceBytes: 8_589_934_721
           } &&
           emptyAccepted is { Kind: OnlineRoomInvitationResponseKind.Room, Room: null } &&
           live is
           {
               Outcome: OnlineRoomLifecycleOutcome.Success,
               Value.LatestMessageId: latestMessageId,
               Value.Members.Count: 2,
               Value.Messages.Count: 1
           } && live.Value.Members[0] is
           {
               UserId: invitedUserId,
               MemberRole: "editor",
               IsOnline: true,
               CustomLabel: "Contributor",
               CanEditMetadata: true,
               CanEditTiming: false,
               CanEditNotes: true,
               Tags.Count: 1
           } && live.Value.Members[1] is
           {
               UserId: invitedUserId + 1,
               DisplayName: "Offline Member",
               IsOnline: false,
               Tags.Count: 1
           } && live.Value.Messages[0] is
           {
               Id: chatMessageId,
               UserId: invitedUserId,
               Message: "hello lifecycle",
               Tags.Count: 1
           } &&
           chat is
           {
               Outcome: OnlineRoomLifecycleOutcome.Success,
               Value.Id: chatMessageId,
               Value.UserId: invitedUserId
           } &&
           heartbeat == OnlineRoomLifecycleOutcome.Success &&
           leave == OnlineRoomLifecycleOutcome.NotFound,
        "lifecycle response DTOs lost invitation, room, member, chat or Int64 fields");

    var liveForbidden = await api.GetLiveStateAsync(roomId, 1, "live-403-token");
    var liveNotFound = await api.GetLiveStateAsync(roomId, 2, "live-404-token");
    var chatForbidden = await api.SendChatMessageAsync(roomId, "forbidden", "chat-403-token");
    var chatNotFound = await api.SendChatMessageAsync(roomId, "not found", "chat-404-token");
    var heartbeatForbidden = await api.SendHeartbeatAsync(roomId, "heartbeat-403-token");
    var heartbeatNotFound = await api.SendHeartbeatAsync(roomId, "heartbeat-404-token");
    Assert(liveForbidden is { Outcome: OnlineRoomLifecycleOutcome.Forbidden, Value: null } &&
           liveNotFound is { Outcome: OnlineRoomLifecycleOutcome.NotFound, Value: null } &&
           chatForbidden is { Outcome: OnlineRoomLifecycleOutcome.Forbidden, Value: null } &&
           chatNotFound is { Outcome: OnlineRoomLifecycleOutcome.NotFound, Value: null } &&
           heartbeatForbidden == OnlineRoomLifecycleOutcome.Forbidden &&
           heartbeatNotFound == OnlineRoomLifecycleOutcome.NotFound,
        "lifecycle 403/404 status outcomes attempted to parse malformed response bodies");

    OnlineApiException? roleFailure = null;
    try
    {
        await api.UpdateMemberRoleAsync(
            roomId, invitedUserId, "editor", "role-error-token");
    }
    catch (OnlineApiException exception)
    {
        roleFailure = exception;
    }
    OnlineApiException? chatFailure = null;
    try
    {
        _ = await api.SendChatMessageAsync(roomId, "too fast", "chat-rate-token");
    }
    catch (OnlineApiException exception)
    {
        chatFailure = exception;
    }
    Assert(roleFailure is
           {
               StatusCode: HttpStatusCode.Forbidden,
               Code: "owner_required",
               Message: "只有房主可以修改成员身份。"
           } &&
           chatFailure is
           {
               StatusCode: HttpStatusCode.TooManyRequests,
               Code: "chat_rate_limited",
               Message: "发送过快，请等待 9 秒。",
               RetryAfter: var retryAfter
           } && retryAfter == TimeSpan.FromSeconds(9),
        "role or chat errors lost structured code, status, message or Retry-After");

    using var cancellation = new CancellationTokenSource();
    var pendingLive = api.GetLiveStateAsync(
        roomId, latestMessageId + 1, "cancel-live-token", cancellation.Token);
    var cancellationObserved = false;
    try
    {
        await blockingJson.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            _ = await pendingLive.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        catch (TimeoutException)
        {
        }
    }
    finally
    {
        cancellation.Cancel();
        blockingJson.Release();
    }
    if (!pendingLive.IsCompleted)
    {
        try { _ = await pendingLive.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }
    Assert(cancellationObserved,
        "lifecycle transport cancellation did not reach the blocking JSON response body");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "lifecycle-ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "lifecycle transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 22,
        "lifecycle transport sent an unexpected number of requests");
    (HttpMethod Method, string Path, string Token)[] expectedRequests =
    [
        (HttpMethod.Post, $"api/rooms/{roomId:D}/invitations", "invite-token"),
        (HttpMethod.Get, "api/me/invitations", "list-token"),
        (HttpMethod.Post, $"api/me/invitations/{invitationId:D}/respond", "cancel-invite-token"),
        (HttpMethod.Post, $"api/me/invitations/{invitationId:D}/respond", "accept-invite-token"),
        (HttpMethod.Put, $"api/rooms/{roomId:D}/members/{invitedUserId}/role", "role-token"),
        (HttpMethod.Put, $"api/rooms/{roomId:D}/members/{invitedUserId}/access", "access-token"),
        (HttpMethod.Delete, $"api/rooms/{roomId:D}/members/{invitedUserId}?banMinutes=-17", "remove-token"),
        (HttpMethod.Get, $"api/rooms/{roomId:D}/live?after={latestMessageId}", "live-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chat", "chat-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/heartbeat", "heartbeat-token"),
        (HttpMethod.Delete, $"api/rooms/{roomId:D}", "leave-token"),
        (HttpMethod.Post, $"api/me/invitations/{invitationId:D}/respond", "empty-invite-token"),
        (HttpMethod.Get, $"api/rooms/{roomId:D}/live?after=1", "live-403-token"),
        (HttpMethod.Get, $"api/rooms/{roomId:D}/live?after=2", "live-404-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chat", "chat-403-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chat", "chat-404-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/heartbeat", "heartbeat-403-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/heartbeat", "heartbeat-404-token"),
        (HttpMethod.Put, $"api/rooms/{roomId:D}/members/{invitedUserId}/role", "role-error-token"),
        (HttpMethod.Post, $"api/rooms/{roomId:D}/chat", "chat-rate-token"),
        (HttpMethod.Get, $"api/rooms/{roomId:D}/live?after={latestMessageId + 1}", "cancel-live-token")
    ];
    for (var index = 0; index < expectedRequests.Length; index++)
    {
        var expected = expectedRequests[index];
        var actual = handler.Requests[index];
        Assert(actual.Method == expected.Method &&
               actual.RequestUri == new Uri(endpoint, expected.Path) &&
               actual.AuthorizationScheme == "Bearer" &&
               actual.AuthorizationParameter == expected.Token,
            $"lifecycle request {index} changed method, URL or bearer token");
    }
    Assert(handler.Requests[21].RequestUri == new Uri(endpoint, "lifecycle-ownership-probe") &&
           handler.Requests[21].AuthorizationParameter == null,
        "lifecycle ownership probe changed unexpectedly");
    Assert(handler.Requests.All(request =>
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Lifecycle-Tablet-Spec")),
        "version or client-profile headers were lost by the lifecycle transport");

    foreach (var index in new[] { 0, 2, 3, 4, 5, 8, 11, 14, 15, 18, 19 })
        Assert(handler.Requests[index].HasContentHeader(
                "Content-Type", "application/json; charset=utf-8"),
            $"lifecycle JSON request {index} lost its media type");

    var inviteRequest = System.Text.Json.JsonSerializer.Deserialize<RoomInviteRequest>(
                            handler.Requests[0].Body!, jsonOptions)
                        ?? throw new InvalidOperationException("invite JSON did not match the server DTO");
    var cancelRequest = System.Text.Json.JsonSerializer.Deserialize<RoomInviteResponseRequest>(
                            handler.Requests[2].Body!, jsonOptions)
                        ?? throw new InvalidOperationException("cancel response JSON did not match the server DTO");
    var roomRequest = System.Text.Json.JsonSerializer.Deserialize<RoomInviteResponseRequest>(
                          handler.Requests[3].Body!, jsonOptions)
                      ?? throw new InvalidOperationException("room response JSON did not match the server DTO");
    var emptyRoomRequest = System.Text.Json.JsonSerializer.Deserialize<RoomInviteResponseRequest>(
                               handler.Requests[11].Body!, jsonOptions)
                           ?? throw new InvalidOperationException("empty room response JSON did not match the server DTO");
    var roleRequest = System.Text.Json.JsonSerializer.Deserialize<UpdateRoomMemberRoleRequest>(
                          handler.Requests[4].Body!, jsonOptions)
                      ?? throw new InvalidOperationException("role JSON did not match the server DTO");
    var accessRequest = System.Text.Json.JsonSerializer.Deserialize<UpdateRoomMemberAccessRequest>(
                            handler.Requests[5].Body!, jsonOptions)
                        ?? throw new InvalidOperationException("access JSON did not match the server DTO");
    var chatRequest = System.Text.Json.JsonSerializer.Deserialize<SendRoomChatRequest>(
                          handler.Requests[8].Body!, jsonOptions)
                      ?? throw new InvalidOperationException("chat JSON did not match the server DTO");
    using var inviteJson = System.Text.Json.JsonDocument.Parse(handler.Requests[0].Body!);
    using var accessJson = System.Text.Json.JsonDocument.Parse(handler.Requests[5].Body!);
    Assert(inviteRequest.UserId == invitedUserId &&
           inviteJson.RootElement.TryGetProperty("userId", out var wireUserId) &&
           wireUserId.GetInt64() == invitedUserId &&
           !inviteJson.RootElement.TryGetProperty("UserId", out _) &&
           cancelRequest.Action == "cancel" && roomRequest.Action == "watch" &&
           emptyRoomRequest.Action == "join" &&
           roleRequest.MemberRole == "viewer" &&
           accessRequest == new UpdateRoomMemberAccessRequest("editor", true, false, true) &&
           accessJson.RootElement.TryGetProperty("memberRole", out _) &&
           accessJson.RootElement.TryGetProperty("canEditMetadata", out _) &&
           accessJson.RootElement.TryGetProperty("canEditTiming", out _) &&
           accessJson.RootElement.TryGetProperty("canEditNotes", out _) &&
           !accessJson.RootElement.TryGetProperty("MemberRole", out _) &&
           chatRequest.Message == "hello lifecycle",
        "lifecycle request JSON lost camelCase, Int64, invitation action, role or access flags");
    Assert(typeof(OnlineRoomLifecycleApi).Assembly == typeof(OnlineRoomApi).Assembly,
        "lifecycle transport was not added to the guarded cross-platform Online assembly");

    HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T body) => new(statusCode)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body, jsonOptions),
            Encoding.UTF8,
            "application/json")
    };

    static HttpResponseMessage MalformedResponse(HttpStatusCode statusCode) => new(statusCode)
    {
        Content = new StringContent("{ definitely-not-json", Encoding.UTF8, "application/json")
    };
}

static void TestOnlineAccountTransport() =>
    TestOnlineAccountTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineAccountTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var endpoint = new Uri("https://account.unit.lumachart.invalid/");
    var now = new DateTimeOffset(2026, 7, 20, 10, 20, 30, TimeSpan.Zero);
    const long userId = 8_589_934_711;
    const long chartingSeconds = 4_294_967_321;
    var serverConfiguration = new AppConfiguration(
        "About account transport", "1.0.0", "1.0.0", "No migration notice",
        "https://download.unit.invalid/lumachart", now);
    var serverCaptcha = new CaptchaChallenge(
        Guid.Parse("a2222222-b333-c444-d555-e66666666666"), "data:image/png;base64,AAE=", now.AddMinutes(2));
    var serverUser = new UserProfile(
        userId,
        "account-user",
        "Account User",
        "user",
        "avatar-account",
        now.AddYears(-1),
        chartingSeconds,
        "Cross-platform profile",
        true,
        false,
        17,
        19,
        "Verifier",
        [new UserTag("Mapper", "#123456", true)],
        "account@example.invalid");
    var serverHeartbeat = new ChartingHeartbeatResponse(
        chartingSeconds + 9, true, now.AddSeconds(5));
    var avatarBytes = Enumerable.Range(0, 4_103)
        .Select(index => (byte)((index * 17 + 3) % 251))
        .ToArray();

    using var blockingAvatar = new BlockingReadStream([1, 2, 3, 4]);
    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverConfiguration));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverConfiguration));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverCaptcha));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverUser));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{ command-body-is-ignored", Encoding.UTF8, "application/json")
    });
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverUser));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverHeartbeat));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverUser));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([])
    });
    var throttledProfile = JsonResponse(
        HttpStatusCode.TooManyRequests,
        new ApiError("profile_rate_limited", "资料更新过于频繁。"));
    throttledProfile.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(13));
    handler.Enqueue(throttledProfile);
    // A canceled upload exits while the handler is reading request content, so this
    // queued response is intentionally consumed by the later ownership probe.
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "X-LumaChart-Client-Profile", "Account-Tablet-Spec");
    var api = new OnlineAccountApi(client, endpoint);
    AssertThrows<ArgumentException>(
        () => _ = new OnlineAccountApi(client, new Uri("http://insecure.example.test/")),
        "the shared account transport accepted a non-loopback plaintext HTTP endpoint");
    _ = new OnlineAccountApi(client, new Uri("http://127.0.0.1:5080/"));
    AssertThrows<ArgumentOutOfRangeException>(
        () => api.UploadAvatarAsync(Stream.Null, -1, "invalid-length-token")
            .GetAwaiter().GetResult(),
        "the account avatar transport accepted a negative declared length");

    var configuration = await api.GetAppConfigurationAsync();
    var updatedConfiguration = await api.UpdateAppConfigurationAsync(
        new OnlineUpdateAppConfigurationRequestDto(
            "About account transport", "1.0.0", "1.0.0", "No migration notice",
            "https://download.unit.invalid/lumachart"),
        "admin-config-token");
    var captcha = await api.GetCaptchaAsync();
    await api.SendEmailVerificationCodeAsync(
        new OnlineSendEmailVerificationCodeRequestDto(
            "register@example.invalid", "register", serverCaptcha.Id, "123456"),
        "must-not-leak-token");
    await api.SendEmailVerificationCodeAsync(
        new OnlineSendEmailVerificationCodeRequestDto(
            "change@example.invalid", "ChAnGe_EmAiL", serverCaptcha.Id, "654321"),
        "change-email-code-token");
    var changedEmail = await api.ChangeEmailAsync(
        new OnlineChangeEmailRequestDto("old-password", "change@example.invalid", "654321"),
        "change-email-token");
    await api.ResetPasswordAsync(
        new OnlineResetPasswordRequestDto(
            "reset@example.invalid", "112233", "replacement-password"));
    await api.ChangePasswordAsync(
        new OnlineChangePasswordRequestDto("current-password", "new-password"),
        "change-password-token");
    var updatedProfile = await api.UpdateProfileAsync(
        new OnlineUpdateProfileRequestDto("Account User", "Cross-platform profile", true, false),
        "profile-token");
    var heartbeat = await api.SendChartingHeartbeatAsync(
        new OnlineChartingHeartbeatRequestDto(
            Guid.Parse("b3333333-c444-d555-e666-f77777777777"),
            4_294_967_331,
            true),
        "heartbeat-token");

    using var avatarSource = new ShortReadStream(avatarBytes, 11);
    var avatarProgress = new List<long>();
    var avatarUser = await api.UploadAvatarAsync(
        avatarSource,
        avatarBytes.LongLength,
        "avatar-token",
        new InlineProgress<long>(avatarProgress.Add));
    Assert(!avatarSource.IsDisposed,
        "the account avatar transport disposed the caller-owned upload source");

    var emptyConfiguration = await api.GetAppConfigurationAsync();
    OnlineApiException? profileFailure = null;
    try
    {
        _ = await api.UpdateProfileAsync(
            new OnlineUpdateProfileRequestDto("Rate Limited", "", false, false),
            "profile-rate-token");
    }
    catch (OnlineApiException exception)
    {
        profileFailure = exception;
    }

    Assert(configuration is
           {
               AboutContent: "About account transport",
               LatestVersion: "1.0.0",
               MinimumLoginVersion: "1.0.0",
               UpdateNotice: "No migration notice",
               DownloadUrl: "https://download.unit.invalid/lumachart",
               UpdatedAt: var configurationUpdatedAt
           } && configurationUpdatedAt == now && updatedConfiguration == configuration &&
           captcha is
           {
               Id: var captchaId,
               ImageBase64: "data:image/png;base64,AAE=",
               ExpiresAt: var captchaExpiresAt
           } && captchaId == serverCaptcha.Id && captchaExpiresAt == serverCaptcha.ExpiresAt &&
           changedEmail is
           {
               Id: userId,
               Username: "account-user",
               DisplayName: "Account User",
               Role: "user",
               AvatarKey: "avatar-account",
               ChartingSeconds: chartingSeconds,
               FollowingCount: 17,
               FollowerCount: 19,
               CustomLabel: "Verifier",
               Tags.Count: 1,
               Email: "account@example.invalid"
           } && updatedProfile is
           {
               Id: userId,
               ChartingSeconds: chartingSeconds,
               Tags.Count: 1,
               Email: "account@example.invalid"
           } && avatarUser is
           {
               Id: userId,
               AvatarKey: "avatar-account",
               Tags.Count: 1
           } &&
           heartbeat is
           {
               ChartingSeconds: chartingSeconds + 9,
               IsCounting: true,
               LastOperationAt: var lastOperationAt
           } && lastOperationAt == serverHeartbeat.LastOperationAt && emptyConfiguration == null,
        "account response DTOs lost configuration, captcha, user, tag, heartbeat or Int64 fields");
    Assert(profileFailure is
           {
               StatusCode: HttpStatusCode.TooManyRequests,
               Code: "profile_rate_limited",
               Message: "资料更新过于频繁。",
               RetryAfter: var retryAfter
           } && retryAfter == TimeSpan.FromSeconds(13),
        "account errors lost structured code, status, message or Retry-After");
    Assert(avatarProgress.Count > 1 && avatarProgress[^1] == avatarBytes.LongLength &&
           avatarProgress.SequenceEqual(avatarProgress.Order()) &&
           avatarProgress.Distinct().Count() == avatarProgress.Count,
        "avatar upload progress was not cumulative, increasing and complete under short reads");

    using var uploadCancellation = new CancellationTokenSource();
    var pendingUpload = api.UploadAvatarAsync(
        blockingAvatar, 4, "cancel-avatar-token", cancellationToken: uploadCancellation.Token);
    var uploadCanceled = false;
    try
    {
        await blockingAvatar.Started.WaitAsync(TimeSpan.FromSeconds(2));
        uploadCancellation.Cancel();
        try
        {
            _ = await pendingUpload.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            uploadCanceled = true;
        }
        catch (TimeoutException)
        {
        }
    }
    finally
    {
        uploadCancellation.Cancel();
        blockingAvatar.Release();
    }
    if (!pendingUpload.IsCompleted)
    {
        try { _ = await pendingUpload.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }
    Assert(uploadCanceled && !blockingAvatar.IsDisposed,
        "avatar upload cancellation did not reach the caller-owned source without disposing it");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "account-ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "the account transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 14,
        "account transport sent an unexpected number of completed requests");
    (HttpMethod Method, string Path, string? Token)[] expectedRequests =
    [
        (HttpMethod.Get, "api/app-config", null),
        (HttpMethod.Put, "api/admin/app-config", "admin-config-token"),
        (HttpMethod.Get, "api/auth/captcha", null),
        (HttpMethod.Post, "api/auth/email-codes", null),
        (HttpMethod.Post, "api/auth/email-codes", "change-email-code-token"),
        (HttpMethod.Put, "api/me/email", "change-email-token"),
        (HttpMethod.Post, "api/auth/reset-password", null),
        (HttpMethod.Post, "api/me/password", "change-password-token"),
        (HttpMethod.Put, "api/me/profile", "profile-token"),
        (HttpMethod.Post, "api/profile/charting/heartbeat", "heartbeat-token"),
        (HttpMethod.Put, "api/me/avatar", "avatar-token"),
        (HttpMethod.Get, "api/app-config", null),
        (HttpMethod.Put, "api/me/profile", "profile-rate-token"),
        (HttpMethod.Get, "account-ownership-probe", null)
    ];
    for (var index = 0; index < expectedRequests.Length; index++)
    {
        var expected = expectedRequests[index];
        var actual = handler.Requests[index];
        Assert(actual.Method == expected.Method &&
               actual.RequestUri == new Uri(endpoint, expected.Path) &&
               (expected.Token == null
                   ? actual.AuthorizationScheme == null && actual.AuthorizationParameter == null
                   : actual.AuthorizationScheme == "Bearer" &&
                     actual.AuthorizationParameter == expected.Token),
            $"account request {index} changed method, URL or bearer policy");
    }
    Assert(handler.Requests.All(request =>
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Account-Tablet-Spec")),
        "version or client-profile headers were lost by the account transport");
    foreach (var index in new[] { 1, 3, 4, 5, 6, 7, 8, 9, 12 })
        Assert(handler.Requests[index].HasContentHeader(
                "Content-Type", "application/json; charset=utf-8"),
            $"account JSON request {index} lost its media type");
    Assert(handler.Requests[10].HasContentHeader(
               "Content-Type", "application/octet-stream") &&
           handler.Requests[10].HasContentHeader(
               "Content-Length", avatarBytes.LongLength.ToString(
                   System.Globalization.CultureInfo.InvariantCulture)) &&
           handler.Requests[10].BodyBytes?.SequenceEqual(avatarBytes) == true,
        "avatar upload lost its exact bytes, octet-stream type or declared Content-Length");

    var configurationRequest = System.Text.Json.JsonSerializer.Deserialize<UpdateAppConfigurationRequest>(
                                   handler.Requests[1].Body!, jsonOptions)
                               ?? throw new InvalidOperationException("app configuration JSON did not match the server DTO");
    var publicCodeRequest = System.Text.Json.JsonSerializer.Deserialize<SendEmailVerificationCodeRequest>(
                                handler.Requests[3].Body!, jsonOptions)
                            ?? throw new InvalidOperationException("public email code JSON did not match the server DTO");
    var privateCodeRequest = System.Text.Json.JsonSerializer.Deserialize<SendEmailVerificationCodeRequest>(
                                 handler.Requests[4].Body!, jsonOptions)
                             ?? throw new InvalidOperationException("private email code JSON did not match the server DTO");
    var changeEmailRequest = System.Text.Json.JsonSerializer.Deserialize<ChangeEmailRequest>(
                                 handler.Requests[5].Body!, jsonOptions)
                             ?? throw new InvalidOperationException("change email JSON did not match the server DTO");
    var resetPasswordRequest = System.Text.Json.JsonSerializer.Deserialize<ResetPasswordRequest>(
                                   handler.Requests[6].Body!, jsonOptions)
                               ?? throw new InvalidOperationException("reset password JSON did not match the server DTO");
    var changePasswordRequest = System.Text.Json.JsonSerializer.Deserialize<ChangePasswordRequest>(
                                    handler.Requests[7].Body!, jsonOptions)
                                ?? throw new InvalidOperationException("change password JSON did not match the server DTO");
    var profileRequest = System.Text.Json.JsonSerializer.Deserialize<UpdateProfileRequest>(
                             handler.Requests[8].Body!, jsonOptions)
                         ?? throw new InvalidOperationException("profile JSON did not match the server DTO");
    var heartbeatRequest = System.Text.Json.JsonSerializer.Deserialize<ChartingHeartbeatRequest>(
                               handler.Requests[9].Body!, jsonOptions)
                           ?? throw new InvalidOperationException("heartbeat JSON did not match the server DTO");
    using var heartbeatJson = System.Text.Json.JsonDocument.Parse(handler.Requests[9].Body!);
    Assert(configurationRequest == new UpdateAppConfigurationRequest(
               "About account transport", "1.0.0", "1.0.0", "No migration notice",
               "https://download.unit.invalid/lumachart") &&
           publicCodeRequest.Purpose == "register" &&
           publicCodeRequest.Email == "register@example.invalid" &&
           privateCodeRequest.Purpose == "ChAnGe_EmAiL" &&
           changeEmailRequest == new ChangeEmailRequest(
               "old-password", "change@example.invalid", "654321") &&
           resetPasswordRequest == new ResetPasswordRequest(
               "reset@example.invalid", "112233", "replacement-password") &&
           changePasswordRequest == new ChangePasswordRequest(
               "current-password", "new-password") &&
           profileRequest == new UpdateProfileRequest(
               "Account User", "Cross-platform profile", true, false) &&
           heartbeatRequest.OperationSerial == 4_294_967_331 && heartbeatRequest.IsEditing &&
           heartbeatJson.RootElement.TryGetProperty("operationSerial", out var operationSerial) &&
           operationSerial.GetInt64() == 4_294_967_331 &&
           !heartbeatJson.RootElement.TryGetProperty("OperationSerial", out _),
        "account request JSON lost server shape, camelCase, private fields or Int64 values");
    Assert(typeof(OnlineAccountApi).Assembly == typeof(OnlineAuthApi).Assembly &&
           typeof(OnlineAccountApi).GetMethod(nameof(OnlineAccountApi.UploadAvatarAsync))?
               .GetParameters()[0].ParameterType == typeof(Stream),
        "account transport did not retain the cross-platform Stream boundary");

    HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T body) => new(statusCode)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body, jsonOptions),
            Encoding.UTF8,
            "application/json")
    };
}

static void TestOnlineNameColorTransport() =>
    TestOnlineNameColorTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineNameColorTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var endpoint = new Uri("https://name-color.unit.lumachart.invalid/");
    var now = new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero);
    var selectedUser = new UserProfile(
        101,
        "tag-owner",
        "Tag Owner",
        "user",
        "default",
        now,
        0,
        Tags:
        [
            new UserTag("Mapper", "#2299DD"),
            new UserTag("Supporter", "#F2B84B", true)
        ]);
    var clearedUser = selectedUser with
    {
        Tags = selectedUser.Tags?.Select(tag => tag with { IsNameColor = false }).ToArray()
    };

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, selectedUser));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, clearedUser));
    using var client = new HttpClient(handler);
    var api = new OnlineAccountApi(client, endpoint);

    var selected = await api.UpdateNameColorAsync("Supporter", "select-tag-token");
    var cleared = await api.UpdateNameColorAsync(null, "clear-tag-token");

    Assert(selected?.Tags?.Single(tag => tag.IsNameColor) is
               { Name: "Supporter", Color: "#F2B84B" } &&
           cleared?.Tags?.All(tag => !tag.IsNameColor) == true,
        "name-color responses lost the selected tag marker or clear operation");
    Assert(handler.Requests.Count == 2 &&
           handler.Requests[0].Method == HttpMethod.Put &&
           handler.Requests[0].RequestUri == new Uri(endpoint, "api/me/name-color") &&
           handler.Requests[0].AuthorizationScheme == "Bearer" &&
           handler.Requests[0].AuthorizationParameter == "select-tag-token" &&
           handler.Requests[1].AuthorizationParameter == "clear-tag-token",
        "name-color transport changed its authenticated PUT contract");

    var selectionRequest = System.Text.Json.JsonSerializer.Deserialize<UpdateNameColorRequest>(
                               handler.Requests[0].Body!, jsonOptions)
                           ?? throw new InvalidOperationException(
                               "name-color selection JSON did not match the server DTO");
    var clearRequest = System.Text.Json.JsonSerializer.Deserialize<UpdateNameColorRequest>(
                           handler.Requests[1].Body!, jsonOptions)
                       ?? throw new InvalidOperationException(
                           "name-color clear JSON did not match the server DTO");
    using var selectionJson = System.Text.Json.JsonDocument.Parse(handler.Requests[0].Body!);
    using var clearJson = System.Text.Json.JsonDocument.Parse(handler.Requests[1].Body!);
    Assert(selectionRequest.TagName == "Supporter" && clearRequest.TagName == null &&
           selectionJson.RootElement.EnumerateObject().Select(property => property.Name)
               .SequenceEqual(["tagName"]) &&
           selectionJson.RootElement.GetProperty("tagName").GetString() == "Supporter" &&
           clearJson.RootElement.GetProperty("tagName").ValueKind ==
               System.Text.Json.JsonValueKind.Null &&
           typeof(UpdateNameColorRequest).GetProperty("Color") == null &&
           typeof(OnlineUpdateNameColorRequestDto).GetProperty("Color") == null,
        "name-color request exposed an arbitrary color or lost its camelCase nullable tag reference");

    HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T body) => new(statusCode)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body, jsonOptions),
            Encoding.UTF8,
            "application/json")
    };
}

static void TestOnlineCommunityTransport() =>
    TestOnlineCommunityTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineCommunityTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var endpoint = new Uri("https://community.unit.lumachart.invalid/");
    const long targetUserId = 4_294_967_399;
    const long chartingSeconds = 8_589_934_799;
    var roomId = Guid.Parse("c1111111-a222-b333-c444-d55555555555");
    var serverPlayer = new PlayerProfile(
        targetUserId,
        "Community Target",
        "public/avatar-key",
        "Complete public profile",
        chartingSeconds,
        31,
        47,
        true,
        true,
        true,
        false,
        true,
        "Featured",
        [new UserTag("Tournament Mapper", "#12ABEF", true)]);
    var actingUserAfterFollow = new UserProfile(
        4_294_967_411,
        "acting-user",
        "Acting User",
        "editor",
        "acting-avatar",
        new DateTimeOffset(2026, 7, 20, 10, 15, 0, TimeSpan.Zero),
        8_589_934_811,
        "Private authoritative profile",
        true,
        false,
        12,
        19,
        "Supporter",
        [new UserTag("Early User", "#A1B2C3", false)],
        "acting@example.invalid");
    var actingUserAfterUnfollow = actingUserAfterFollow with { FollowingCount = 11 };
    var listDirectory = new PlayerDirectory(17, [serverPlayer], 1, 100, 321);
    var searchDirectory = new PlayerDirectory(1, [serverPlayer], 3, 100, 1);
    var leaderboardDirectory = new PlayerDirectory(9, [serverPlayer], 1, 50, 50);
    var followingDirectory = new PlayerDirectory(1, [serverPlayer], 1, 100, 4);
    var followersDirectory = new PlayerDirectory(0, [serverPlayer], 3, 100, 205);
    var avatarPayload = Enumerable.Range(0, 4_101)
        .Select(index => (byte)(index * 23 + 9))
        .ToArray();

    using var avatarSource = new ShortReadStream(avatarPayload, 13);
    var avatarContent = new ResponseHeadersReadProbeContent(avatarSource);
    avatarContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    avatarContent.Headers.ContentLength = avatarPayload.LongLength;
    using var blockingAvatarSource = new BlockingReadStream([7, 6, 5, 4]);
    var blockingAvatarContent = new ResponseHeadersReadProbeContent(blockingAvatarSource);

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, listDirectory));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, searchDirectory));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, leaderboardDirectory));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([])
    });
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, followingDirectory));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, followersDirectory));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverPlayer));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, serverPlayer));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, actingUserAfterFollow));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, actingUserAfterUnfollow));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = avatarContent });
    var rateLimitedResponse = JsonResponse(
        HttpStatusCode.TooManyRequests,
        new ApiError("community_rate_limited", "请等待 11 秒后重试。"));
    rateLimitedResponse.Headers.RetryAfter =
        new RetryConditionHeaderValue(TimeSpan.FromSeconds(11));
    handler.Enqueue(rateLimitedResponse);
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = blockingAvatarContent });
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "X-LumaChart-Client-Profile", "Community-Tablet-Spec");
    var api = new OnlineCommunityApi(client, endpoint);
    AssertThrows<ArgumentException>(
        () => _ = new OnlineCommunityApi(client, new Uri("community", UriKind.Relative)),
        "community transport accepted a relative endpoint");
    AssertThrows<ArgumentException>(
        () => _ = new OnlineCommunityApi(client, new Uri("http://community.example.test/")),
        "community transport accepted non-loopback plaintext HTTP");
    _ = new OnlineCommunityApi(client, new Uri("http://127.0.0.1:5080/"));

    var listed = await api.GetPlayersAsync(-7, "list-token");
    var searched = await api.SearchPlayersAsync("A B&谱?/+", 3, "search-token");
    var leaderboard = await api.GetLeaderboardAsync("leaderboard-token");
    var friends = await api.GetFriendsAsync("friends-token");
    var following = await api.GetFollowingAsync(-2, "following-token");
    var followers = await api.GetFollowersAsync(3, "followers-token");
    var player = await api.GetPlayerAsync(targetUserId, null, "player-token");
    var roomPlayer = await api.GetPlayerAsync(targetUserId, roomId, "room-player-token");
    var followed = await api.SetFollowAsync(targetUserId, true, "follow-token");
    var unfollowed = await api.SetFollowAsync(targetUserId, false, "unfollow-token");

    Assert(listed is
           {
               OnlineCount: 17,
               Page: 1,
               PageSize: 100,
               TotalCount: 321,
               Players.Count: 1
           } && listed.Players[0].Id == targetUserId &&
           searched is { OnlineCount: 1, Page: 3, PageSize: 100, TotalCount: 1 } &&
           leaderboard is { OnlineCount: 9, Page: 1, PageSize: 50, TotalCount: 50 } &&
           friends == null &&
           following is { OnlineCount: 1, Page: 1, PageSize: 100, TotalCount: 4, Players.Count: 1 } &&
           followers is { OnlineCount: 0, Page: 3, PageSize: 100, TotalCount: 205, Players.Count: 1 } &&
           player?.Id == targetUserId && roomPlayer?.Id == targetUserId,
        "community directory/player DTOs or successful empty-body semantics changed");
    Assert(followed is
           {
               Id: 4_294_967_411,
               Username: "acting-user",
               DisplayName: "Acting User",
               Role: "editor",
               AvatarKey: "acting-avatar",
               ChartingSeconds: 8_589_934_811,
               Bio: "Private authoritative profile",
               HidePublicAvatar: true,
               HidePublicName: false,
               FollowingCount: 12,
               FollowerCount: 19,
               CustomLabel: "Supporter",
               Email: "acting@example.invalid",
               Tags.Count: 1
           } && followed.Tags[0] is
           {
               Name: "Early User",
               Color: "#A1B2C3",
               IsNameColor: false
           } && unfollowed is
           {
               Id: 4_294_967_411,
               FollowingCount: 11,
               Email: "acting@example.invalid"
           },
        "follow response stopped using the authoritative OnlineUserProfileDto or lost full Int64/tag fields");

    using var avatarDestination = new TrackingWriteStream();
    var avatarProgress = new List<long>();
    var avatar = await api.DownloadAvatarAsync(
        "folder/avatar +谱?.png",
        avatarDestination,
        new InlineProgress<long>(avatarProgress.Add));
    Assert(avatar is
           {
               ContentType: "image/png",
               ContentLength: var returnedLength,
               TransferredBytes: var transferred
           } && returnedLength == avatarPayload.LongLength &&
           transferred == avatarPayload.LongLength &&
           avatarDestination.ToArray().SequenceEqual(avatarPayload) &&
           !avatarDestination.IsDisposed && !avatarContent.SerializeAttempted,
        "avatar download buffered the response, lost metadata/bytes or disposed the destination");
    Assert(avatarProgress.Count > 1 && avatarProgress[0] == 13 &&
           avatarProgress[^1] == avatarPayload.LongLength &&
           avatarProgress.Zip(avatarProgress.Skip(1)).All(pair => pair.First < pair.Second),
        "avatar progress was not a strictly increasing cumulative count across 13-byte reads");

    OnlineApiException? structuredFailure = null;
    try
    {
        _ = await api.GetLeaderboardAsync("error-token");
    }
    catch (OnlineApiException exception)
    {
        structuredFailure = exception;
    }
    Assert(structuredFailure is
           {
               Code: "community_rate_limited",
               StatusCode: HttpStatusCode.TooManyRequests,
               Message: "请等待 11 秒后重试。",
               RetryAfter: var retryAfter
           } && retryAfter == TimeSpan.FromSeconds(11),
        "community errors lost structured status/code/message or Retry-After");

    using var canceledDestination = new TrackingWriteStream();
    using var cancellation = new CancellationTokenSource();
    var canceledDownload = api.DownloadAvatarAsync(
        "blocking-avatar",
        canceledDestination,
        cancellationToken: cancellation.Token);
    var cancellationObserved = false;
    try
    {
        await blockingAvatarSource.Started.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            _ = await canceledDownload.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        catch (TimeoutException)
        {
        }
    }
    finally
    {
        cancellation.Cancel();
        blockingAvatarSource.Release();
    }
    if (!canceledDownload.IsCompleted)
    {
        try { _ = await canceledDownload.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }
    Assert(cancellationObserved && !canceledDestination.IsDisposed &&
           !blockingAvatarContent.SerializeAttempted,
        "avatar cancellation did not reach the blocking source or disposed the destination");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "community-ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "community transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 14,
        "community transport sent an unexpected number of requests");
    var escapedQuery = "A%20B%26%E8%B0%B1%3F%2F%2B";
    var escapedAvatarKey = "folder%2Favatar%20%2B%E8%B0%B1%3F.png";
    (HttpMethod Method, string Path, string? Token)[] expectedRequests =
    [
        (HttpMethod.Get, "api/players?page=1", "list-token"),
        (HttpMethod.Get, $"api/players/search?page=3&q={escapedQuery}", "search-token"),
        (HttpMethod.Get, "api/players/leaderboard?limit=50", "leaderboard-token"),
        (HttpMethod.Get, "api/me/friends", "friends-token"),
        (HttpMethod.Get, "api/me/following?page=1", "following-token"),
        (HttpMethod.Get, "api/me/followers?page=3", "followers-token"),
        (HttpMethod.Get, $"api/players/{targetUserId}", "player-token"),
        (HttpMethod.Get, $"api/players/{targetUserId}?roomId={roomId:D}", "room-player-token"),
        (HttpMethod.Put, $"api/players/{targetUserId}/follow", "follow-token"),
        (HttpMethod.Delete, $"api/players/{targetUserId}/follow", "unfollow-token"),
        (HttpMethod.Get, $"api/avatars/{escapedAvatarKey}", null),
        (HttpMethod.Get, "api/players/leaderboard?limit=50", "error-token"),
        (HttpMethod.Get, "api/avatars/blocking-avatar", null),
        (HttpMethod.Get, "community-ownership-probe", null)
    ];
    for (var index = 0; index < expectedRequests.Length; index++)
    {
        var expected = expectedRequests[index];
        var actual = handler.Requests[index];
        Assert(actual.Method == expected.Method &&
               actual.RequestUri == new Uri(endpoint, expected.Path) &&
               (expected.Token == null
                   ? actual.AuthorizationScheme == null && actual.AuthorizationParameter == null
                   : actual.AuthorizationScheme == "Bearer" &&
                     actual.AuthorizationParameter == expected.Token),
            $"community request {index} changed method, URL or bearer token");
    }
    Assert(handler.Requests[1].RequestUri?.PathAndQuery ==
               $"/api/players/search?page=3&q={escapedQuery}" &&
           handler.Requests[10].AuthorizationParameter == null &&
           handler.Requests[12].AuthorizationParameter == null,
        "community query ordering/escaping changed or public avatars acquired Authorization");
    Assert(handler.Requests.All(request =>
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Community-Tablet-Spec")),
        "version or client-profile headers were lost by the community transport");
    Assert(typeof(OnlineCommunityApi).Assembly == typeof(OnlineAuthApi).Assembly &&
           typeof(OnlineCommunityApi).GetMethod(nameof(OnlineCommunityApi.SetFollowAsync))?
               .ReturnType == typeof(Task<OnlineUserProfileDto>),
        "community transport left the guarded Online assembly or stopped returning the authoritative user profile");

    HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T body) => new(statusCode)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body, jsonOptions),
            Encoding.UTF8,
            "application/json")
    };
}

static void TestOnlineNotificationTransport() =>
    TestOnlineNotificationTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineNotificationTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var endpoint = new Uri("https://notifications.unit.lumachart.invalid/");
    const long cursor = 8_589_934_721;
    var createdAt = new DateTimeOffset(2026, 7, 21, 9, 30, 0, TimeSpan.Zero);
    var bootstrapPage = new UserNotificationPage(cursor, false, []);
    var deltaPage = new UserNotificationPage(
        cursor + 2,
        false,
        [
            new UserNotification(cursor + 1, "follow", 4_294_967_401, "谱师 A", createdAt),
            new UserNotification(cursor + 2, "follow", 4_294_967_402, "匿名用户", createdAt.AddSeconds(1))
        ]);

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, bootstrapPage));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, deltaPage));
    using var client = new HttpClient(handler);
    var api = new OnlineCommunityApi(client, endpoint);

    var bootstrap = await api.GetNotificationsAsync(null, 0, "bootstrap-token");
    var delta = await api.GetNotificationsAsync(cursor, int.MaxValue, "delta-token");
    AssertThrows<ArgumentOutOfRangeException>(
        () => _ = api.GetNotificationsAsync(-1, 20, "invalid-token"),
        "notification transport accepted a negative cursor");

    Assert(bootstrap is
           {
               NextCursor: cursor,
               HasMore: false,
               Notifications.Count: 0
           } && delta is
           {
               NextCursor: cursor + 2,
               HasMore: false,
               Notifications.Count: 2
           } && delta.Notifications[0] is
           {
               Id: cursor + 1,
               Type: "follow",
               ActorUserId: 4_294_967_401,
               ActorDisplayName: "谱师 A"
           } && delta.Notifications[1].ActorDisplayName == "匿名用户" &&
           delta.Notifications[1].CreatedAt == createdAt.AddSeconds(1),
        "notification DTOs lost Int64 cursors, structured actor identity or timestamps");
    Assert(handler.Requests.Count == 2 &&
           handler.Requests[0].Method == HttpMethod.Get &&
           handler.Requests[0].RequestUri ==
               new Uri(endpoint, "api/me/notifications?limit=1") &&
           handler.Requests[0].AuthorizationScheme == "Bearer" &&
           handler.Requests[0].AuthorizationParameter == "bootstrap-token" &&
           handler.Requests[1].RequestUri ==
               new Uri(endpoint, $"api/me/notifications?limit=50&after={cursor}") &&
           handler.Requests[1].AuthorizationParameter == "delta-token",
        "notification transport changed its authenticated cursor URL or page-size clamps");
    Assert(OnlineCommunityApi.MaximumNotificationPageSize ==
               Database.MaximumNotificationPageSize &&
           Database.DefaultNotificationPageSize > 0 &&
           Database.DefaultNotificationPageSize <= Database.MaximumNotificationPageSize &&
           typeof(UserNotification).GetProperty("Message") == null &&
           typeof(OnlineUserNotificationDto).GetProperty("Message") == null &&
           typeof(OnlineUserNotificationDto).GetProperty("Html") == null,
        "notification limits diverged or the transport exposed server-authored display markup");

    HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T body) => new(statusCode)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body, jsonOptions),
            Encoding.UTF8,
            "application/json")
    };
}

static void TestOnlineAdminTransport() =>
    TestOnlineAdminTransportAsync().GetAwaiter().GetResult();

static async Task TestOnlineAdminTransportAsync()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var endpoint = new Uri("https://admin.unit.lumachart.invalid/");
    const long userId = 4_294_967_501;
    const long chartingSeconds = 8_589_934_901;
    var createdAt = new DateTimeOffset(2026, 7, 20, 11, 30, 0, TimeSpan.Zero);
    var labeledPlayer = new PlayerProfile(
        userId,
        "Admin Target",
        "target-avatar",
        "Public target biography",
        chartingSeconds,
        41,
        52,
        true,
        false,
        true,
        false,
        false,
        "Gold",
        [new UserTag("Ranked Mapper", "#19A2FF", true)]);
    var privateUser = new UserProfile(
        userId,
        "admin-target",
        "Admin Target",
        "editor",
        "target-avatar",
        createdAt,
        chartingSeconds,
        "Private target biography",
        true,
        true,
        41,
        52,
        "Gold",
        [new UserTag("Ranked Mapper", "#19A2FF", true)],
        "target@example.invalid");
    var updatedUser = privateUser with
    {
        Username = "renamed-target",
        DisplayName = "Renamed Target",
        Bio = "Updated by owner",
        Tags = [new UserTag("Owner Reviewed", "#ABCDEF", false)]
    };
    var taggedUser = updatedUser with
    {
        Tags =
        [
            new UserTag("Pure Tag Update", "#010203", false),
            new UserTag("Name Tint", "#AABBCC", true)
        ]
    };
    var avatarUser = taggedUser with { AvatarKey = "uploaded-avatar-key" };
    var resetUser = taggedUser with { AvatarKey = "default" };
    var avatarPayload = Enumerable.Range(0, 4_099)
        .Select(index => (byte)(index * 37 + 3))
        .ToArray();

    using var handler = new SpecHttpMessageHandler();
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, labeledPlayer));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, privateUser));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, updatedUser));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, taggedUser));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, avatarUser));
    handler.Enqueue(JsonResponse(HttpStatusCode.OK, resetUser));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([])
    });
    var rateLimitedResponse = JsonResponse(
        HttpStatusCode.TooManyRequests,
        new ApiError("admin_rate_limited", "管理员操作过快，请等待 13 秒。"));
    rateLimitedResponse.Headers.RetryAfter =
        new RetryConditionHeaderValue(TimeSpan.FromSeconds(13));
    handler.Enqueue(rateLimitedResponse);
    // The canceled upload stops while the fake handler is serializing its body,
    // leaving its response for the ownership probe. One extra response keeps a
    // cancellation regression focused on the explicit cancellation assertion.
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-LumaChart-Version", "1.0.0");
    client.DefaultRequestHeaders.TryAddWithoutValidation(
        "X-LumaChart-Client-Profile", "Admin-Tablet-Spec");
    var api = new OnlineAdminApi(client, endpoint);
    AssertThrows<ArgumentException>(
        () => _ = new OnlineAdminApi(client, new Uri("admin", UriKind.Relative)),
        "admin transport accepted a relative endpoint");
    AssertThrows<ArgumentException>(
        () => _ = new OnlineAdminApi(client, new Uri("http://admin.example.test/")),
        "admin transport accepted non-loopback plaintext HTTP");
    _ = new OnlineAdminApi(client, new Uri("http://localhost:5080/"));

    var labelResult = await api.SetUserCustomLabelAsync(userId, "Gold", "label-token");
    var user = await api.GetUserAsync(userId, "get-token");
    var updated = await api.UpdateUserAsync(
        userId,
        new OnlineAdminUpdateUserRequestDto(
            "renamed-target",
            "Renamed Target",
            "Updated by owner",
            [new OnlineUserTagDto("Owner Reviewed", "#ABCDEF", false)]),
        "update-token");
    var tagged = await api.UpdateUserAsync(
        userId,
        new OnlineAdminUpdateUserRequestDto(
            null,
            null,
            null,
            [
                new OnlineUserTagDto("Pure Tag Update", "#010203", false),
                new OnlineUserTagDto("Name Tint", "#AABBCC", true)
            ]),
        "tags-token");

    using var avatarSource = new ShortReadStream(avatarPayload, 11);
    var avatarProgress = new List<long>();
    var avatar = await api.UploadUserAvatarAsync(
        userId,
        avatarSource,
        avatarPayload.LongLength,
        "avatar-token",
        new InlineProgress<long>(avatarProgress.Add));
    var reset = await api.ResetUserAvatarAsync(userId, "reset-token");
    var emptyUser = await api.GetUserAsync(userId, "empty-token");

    Assert(labelResult is
           {
               Id: userId,
               DisplayName: "Admin Target",
               AvatarKey: "target-avatar",
               Bio: "Public target biography",
               ChartingSeconds: chartingSeconds,
               FollowingCount: 41,
               FollowerCount: 52,
               IsFollowing: true,
               IsMutual: false,
               IsOnline: true,
               IsNameHidden: false,
               IsAvatarHidden: false,
               CustomLabel: "Gold",
               Tags.Count: 1
           } && labelResult.Tags[0] is
           {
               Name: "Ranked Mapper",
               Color: "#19A2FF",
               IsNameColor: true
           },
        "admin label response lost the complete public PlayerProfile/Int64/tag contract");
    Assert(user is
           {
               Id: userId,
               Username: "admin-target",
               DisplayName: "Admin Target",
               Role: "editor",
               AvatarKey: "target-avatar",
               CreatedAt: var returnedCreatedAt,
               ChartingSeconds: chartingSeconds,
               Bio: "Private target biography",
               HidePublicAvatar: true,
               HidePublicName: true,
               FollowingCount: 41,
               FollowerCount: 52,
               CustomLabel: "Gold",
               Email: "target@example.invalid",
               Tags.Count: 1
           } && returnedCreatedAt == createdAt &&
           user.Tags[0] is { Name: "Ranked Mapper", Color: "#19A2FF", IsNameColor: true } &&
           updated is
           {
               Id: userId,
               Username: "renamed-target",
               DisplayName: "Renamed Target",
               Bio: "Updated by owner",
               Email: "target@example.invalid",
               Tags.Count: 1
           } && tagged is { Id: userId, Tags.Count: 2 } &&
           tagged.Tags[1] is { Name: "Name Tint", Color: "#AABBCC", IsNameColor: true } &&
           avatar is { Id: userId, AvatarKey: "uploaded-avatar-key", ChartingSeconds: chartingSeconds } &&
           reset is { Id: userId, AvatarKey: "default", Email: "target@example.invalid" } &&
           emptyUser == null,
        "admin private UserProfile responses lost fields, tags, Int64 values or empty-body semantics");
    Assert(!avatarSource.IsDisposed && avatarProgress.Count > 1 &&
           avatarProgress[0] == 11 && avatarProgress[^1] == avatarPayload.LongLength &&
           avatarProgress.Zip(avatarProgress.Skip(1)).All(pair => pair.First < pair.Second),
        "admin avatar upload disposed its source or lost cumulative 11-byte-read progress");

    OnlineApiException? structuredFailure = null;
    try
    {
        _ = await api.SetUserCustomLabelAsync(userId, "Too Fast", "error-token");
    }
    catch (OnlineApiException exception)
    {
        structuredFailure = exception;
    }
    Assert(structuredFailure is
           {
               Code: "admin_rate_limited",
               StatusCode: HttpStatusCode.TooManyRequests,
               Message: "管理员操作过快，请等待 13 秒。",
               RetryAfter: var retryAfter
           } && retryAfter == TimeSpan.FromSeconds(13),
        "admin errors lost structured status/code/message or Retry-After");

    using var blockingSource = new BlockingReadStream([9, 8, 7, 6]);
    using var uploadCancellation = new CancellationTokenSource();
    var canceledUpload = api.UploadUserAvatarAsync(
        userId,
        blockingSource,
        4,
        "cancel-avatar-token",
        cancellationToken: uploadCancellation.Token);
    var cancellationObserved = false;
    try
    {
        await blockingSource.Started.WaitAsync(TimeSpan.FromSeconds(2));
        uploadCancellation.Cancel();
        try
        {
            _ = await canceledUpload.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        catch (TimeoutException)
        {
        }
    }
    finally
    {
        uploadCancellation.Cancel();
        blockingSource.Release();
    }
    if (!canceledUpload.IsCompleted)
    {
        try { _ = await canceledUpload.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
    }
    Assert(cancellationObserved && !blockingSource.IsDisposed,
        "admin avatar cancellation did not reach source.ReadAsync or disposed the caller source");

    using var ownershipResponse = await client.GetAsync(new Uri(endpoint, "admin-ownership-probe"));
    Assert(ownershipResponse.StatusCode == HttpStatusCode.NoContent,
        "admin transport disposed the caller-owned HttpClient");

    Assert(handler.Requests.Count == 9,
        "admin transport sent an unexpected number of completed requests");
    (HttpMethod Method, string Path, string? Token)[] expectedRequests =
    [
        (HttpMethod.Put, $"api/admin/users/{userId}/label", "label-token"),
        (HttpMethod.Get, $"api/admin/users/{userId}", "get-token"),
        (HttpMethod.Put, $"api/admin/users/{userId}", "update-token"),
        (HttpMethod.Put, $"api/admin/users/{userId}", "tags-token"),
        (HttpMethod.Put, $"api/admin/users/{userId}/avatar", "avatar-token"),
        (HttpMethod.Delete, $"api/admin/users/{userId}/avatar", "reset-token"),
        (HttpMethod.Get, $"api/admin/users/{userId}", "empty-token"),
        (HttpMethod.Put, $"api/admin/users/{userId}/label", "error-token"),
        (HttpMethod.Get, "admin-ownership-probe", null)
    ];
    for (var index = 0; index < expectedRequests.Length; index++)
    {
        var expected = expectedRequests[index];
        var actual = handler.Requests[index];
        Assert(actual.Method == expected.Method &&
               actual.RequestUri == new Uri(endpoint, expected.Path) &&
               (expected.Token == null
                   ? actual.AuthorizationScheme == null && actual.AuthorizationParameter == null
                   : actual.AuthorizationScheme == "Bearer" &&
                     actual.AuthorizationParameter == expected.Token),
            $"admin request {index} changed method, URL or bearer token");
    }
    foreach (var index in new[] { 0, 2, 3, 7 })
        Assert(handler.Requests[index].HasContentHeader(
                "Content-Type", "application/json; charset=utf-8"),
            $"admin JSON request {index} lost its media type");
    Assert(handler.Requests[4].BodyBytes?.SequenceEqual(avatarPayload) == true &&
           handler.Requests[4].HasContentHeader("Content-Type", "application/octet-stream") &&
           handler.Requests[4].HasContentHeader(
               "Content-Length",
               avatarPayload.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        "admin avatar upload changed its complete body, media type or declared Content-Length");

    var labelRequest = System.Text.Json.JsonSerializer.Deserialize<UpdateUserLabelRequest>(
                           handler.Requests[0].Body!, jsonOptions)
                       ?? throw new InvalidOperationException("admin label JSON did not match the server DTO");
    var updateRequest = System.Text.Json.JsonSerializer.Deserialize<AdminUpdateUserRequest>(
                            handler.Requests[2].Body!, jsonOptions)
                        ?? throw new InvalidOperationException("admin update JSON did not match the server DTO");
    var tagsRequest = System.Text.Json.JsonSerializer.Deserialize<AdminUpdateUserRequest>(
                          handler.Requests[3].Body!, jsonOptions)
                      ?? throw new InvalidOperationException("admin tags JSON did not match the server DTO");
    using var labelJson = System.Text.Json.JsonDocument.Parse(handler.Requests[0].Body!);
    using var updateJson = System.Text.Json.JsonDocument.Parse(handler.Requests[2].Body!);
    using var tagsJson = System.Text.Json.JsonDocument.Parse(handler.Requests[3].Body!);
    Assert(labelRequest.Label == "Gold" &&
           labelJson.RootElement.TryGetProperty("label", out _) &&
           !labelJson.RootElement.TryGetProperty("Label", out _) &&
           updateRequest.Username == "renamed-target" &&
           updateRequest.DisplayName == "Renamed Target" &&
           updateRequest.Bio == "Updated by owner" &&
           updateRequest.Tags?.Single() is
               { Name: "Owner Reviewed", Color: "#ABCDEF", IsNameColor: false } &&
           updateJson.RootElement.TryGetProperty("username", out _) &&
           updateJson.RootElement.TryGetProperty("displayName", out _) &&
           updateJson.RootElement.TryGetProperty("bio", out _) &&
           updateJson.RootElement.TryGetProperty("tags", out var updateTags) &&
           updateTags[0].TryGetProperty("isNameColor", out _) &&
           !updateJson.RootElement.TryGetProperty("Username", out _) &&
           tagsRequest.Username == null && tagsRequest.DisplayName == null && tagsRequest.Bio == null &&
           tagsRequest.Tags is { Count: 2 } && tagsRequest.Tags[1].IsNameColor &&
           tagsJson.RootElement.GetProperty("username").ValueKind ==
               System.Text.Json.JsonValueKind.Null &&
           tagsJson.RootElement.GetProperty("displayName").ValueKind ==
               System.Text.Json.JsonValueKind.Null &&
           tagsJson.RootElement.GetProperty("bio").ValueKind ==
               System.Text.Json.JsonValueKind.Null,
        "admin request JSON lost server DTO shape, camelCase, nullable pure-tag fields or tags");
    Assert(handler.Requests.All(request =>
               request.HasHeader("X-LumaChart-Version", "1.0.0") &&
               request.HasHeader("X-LumaChart-Client-Profile", "Admin-Tablet-Spec")),
        "version or client-profile headers were lost by the admin transport");

    var uploadMethod = typeof(OnlineAdminApi).GetMethod(
        nameof(OnlineAdminApi.UploadUserAvatarAsync));
    var onlineReferences = typeof(OnlineAdminApi).Assembly.GetReferencedAssemblies();
    Assert(typeof(OnlineAdminApi).Assembly == typeof(OnlineAuthApi).Assembly &&
           uploadMethod?.GetParameters()[1].ParameterType == typeof(Stream) &&
           uploadMethod.GetParameters().All(parameter =>
               !parameter.Name!.Contains("path", StringComparison.OrdinalIgnoreCase) &&
               parameter.ParameterType != typeof(FileInfo)) &&
           !onlineReferences.Any(reference => reference.Name is
               "PresentationCore" or "PresentationFramework" or "WindowsBase"),
        "admin transport reintroduced a WPF, filesystem-path or non-shared API dependency");

    HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T body) => new(statusCode)
    {
        Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(body, jsonOptions),
            Encoding.UTF8,
            "application/json")
    };
}
#endif

static void TestRoomChartSyncReducer()
{
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var rootNoteId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    var submittedNoteId = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    var afterRequestNoteId = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    var afterSecondRequestNoteId = Guid.Parse("a0000000-0000-0000-0000-000000000004");
    var remoteNoteId = Guid.Parse("a0000000-0000-0000-0000-000000000005");
    var compactRemoteNoteId = Guid.Parse("a0000000-0000-0000-0000-000000000006");
    var timingId = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    var firstOperationId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    var ignoredRetryId = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    var secondOperationId = Guid.Parse("c0000000-0000-0000-0000-000000000003");
    var thirdOperationId = Guid.Parse("c0000000-0000-0000-0000-000000000004");
    var baseline = CreateBaseline(rootNoteId, timingId);

    // Prepare must install the immutable write before transport. Losing the first
    // acknowledgement and preparing again must reuse the exact ID and wire body.
    var submitted = baseline.Clone();
    submitted.Metadata.Title = "Submitted title";
    submitted.Notes.Single(note => note.Id == rootNoteId).Lane = 1;
    submitted.Notes.Add(new ChartNote
    {
        Id = submittedNoteId,
        Lane = 2,
        Beat = BeatPosition.FromDouble(2)
    });
    submitted.Normalize();
    var currentAfterRequest = submitted.Clone();
    currentAfterRequest.Metadata.Version = "Typed after request";
    currentAfterRequest.Notes.Add(new ChartNote
    {
        Id = afterRequestNoteId,
        Lane = 3,
        Beat = BeatPosition.FromDouble(3)
    });
    currentAfterRequest.Normalize();

    var initialState = RoomChartSyncState.Create(40, baseline, generation: 7);
    var firstPreparation = RoomChartSyncReducer.PrepareAttempt(
        initialState, submitted, currentChangeSerial: 10, firstOperationId);
    Assert(!initialState.HasPendingWrite && firstPreparation.State.PendingWrite is
           {
               OperationId: var installedOperationId,
               BaseRevision: 40,
               RequestChangeSerial: 10
           } && installedOperationId == firstOperationId &&
           ReferenceEquals(firstPreparation.State.PendingWrite, firstPreparation.Attempt.PendingWrite) &&
           firstPreparation.Attempt is
           {
               Generation: 7,
               RequestedRevision: 40,
               RequestChangeSerial: 10,
               IsWrite: true
           } && firstPreparation.Attempt.Batch.OperationId == firstOperationId,
        "reducer did not install a stable pending write before exposing the transport attempt");
    submitted.Notes.Single(note => note.Id == submittedNoteId).Lane = 0;
    Assert(firstPreparation.State.PendingWrite!.RequestDocument.Notes
               .Single(note => note.Id == submittedNoteId).Lane == 2,
        "pending write retained a mutable caller document reference");

    var retryPreparation = RoomChartSyncReducer.PrepareAttempt(
        firstPreparation.State, currentAfterRequest, currentChangeSerial: 11, ignoredRetryId);
    var firstWireBody = System.Text.Json.JsonSerializer.Serialize(
        firstPreparation.Attempt.Batch, jsonOptions);
    var retryWireBody = System.Text.Json.JsonSerializer.Serialize(
        retryPreparation.Attempt.Batch, jsonOptions);
    Assert(ReferenceEquals(firstPreparation.State, retryPreparation.State) &&
           ReferenceEquals(firstPreparation.Attempt.Batch, retryPreparation.Attempt.Batch) &&
           retryPreparation.Attempt.Batch.OperationId == firstOperationId &&
           retryPreparation.Attempt.RequestChangeSerial == 10 &&
           firstWireBody == retryWireBody &&
           !retryWireBody.Contains(ignoredRetryId.ToString("D"), StringComparison.OrdinalIgnoreCase),
        "ACK-loss retry changed the immutable batch, OperationId or request snapshot");

    // A deterministic push rejection proves that the submitted request was not
    // committed. Retire it atomically, recover from a signed reducer token, and
    // replay only edits made after that rejected request was captured.
    var rejectedRequestNoteId = Guid.Parse("c2000000-0000-0000-0000-000000000001");
    var rejectedLaterNoteId = Guid.Parse("c2000000-0000-0000-0000-000000000002");
    var rejectedRemoteNoteId = Guid.Parse("c2000000-0000-0000-0000-000000000003");
    var rejectedLocal = baseline.Clone();
    rejectedLocal.Metadata.Title = "Rejected title";
    rejectedLocal.Notes.Add(new ChartNote
    {
        Id = rejectedRequestNoteId, Lane = 1, Beat = BeatPosition.FromDouble(21)
    });
    rejectedLocal.Normalize();
    var rejectedState = RoomChartSyncState.Create(40, baseline, generation: 12);
    var rejectedAttempt = RoomChartSyncReducer.PrepareAttempt(
        rejectedState,
        rejectedLocal,
        currentChangeSerial: 20,
        Guid.Parse("c2100000-0000-0000-0000-000000000001"));
    var rejectedCurrent = rejectedLocal.Clone();
    rejectedCurrent.Metadata.Version = "Typed after rejected request";
    rejectedCurrent.Notes.Add(new ChartNote
    {
        Id = rejectedLaterNoteId, Lane = 2, Beat = BeatPosition.FromDouble(22)
    });
    rejectedCurrent.Normalize();
    var rejectedRecovery = RoomChartSyncReducer.PrepareRejectedAttemptRecovery(
        rejectedAttempt.State,
        rejectedAttempt.Attempt,
        RoomChartApplyMode.OrdinaryRemoteMerge);
    Assert(rejectedRecovery.State is { Generation: 12, Revision: 40, PendingWrite: null } &&
           rejectedRecovery.FullSnapshotRequest is
           {
               Reason: RoomChartFullSnapshotReason.DeterministicWriteRejection,
               Mode: RoomChartApplyMode.OrdinaryRemoteMerge,
               ReplayMode: RoomChartFullSnapshotReplayMode.PreservePostAttemptLocalChanges,
               PreserveLocalChanges: true,
               AcknowledgePendingWrite: false,
               RebasePendingWrite: false
           },
        "deterministic rejection did not atomically retire pending state and issue post-request recovery");
    var forgedAttempt = RoomChartSyncReducer.PrepareAttempt(
        RoomChartSyncState.Create(40, baseline, generation: 12),
        rejectedLocal,
        currentChangeSerial: 20,
        rejectedAttempt.Attempt.Batch.OperationId);
    AssertThrows<InvalidOperationException>(
        () => RoomChartSyncReducer.PrepareRejectedAttemptRecovery(
            rejectedAttempt.State,
            forgedAttempt.Attempt,
            RoomChartApplyMode.OrdinaryRemoteMerge),
        "a rejected recovery token was forged from a different pending-write identity");
    var rejectedServer = baseline.Clone();
    rejectedServer.Metadata.Creator = "Recovery remote mapper";
    rejectedServer.Notes.Add(new ChartNote
    {
        Id = rejectedRemoteNoteId,
        Lane = 3,
        Beat = BeatPosition.FromDouble(23),
        OnlineCreatorUserId = 82
    });
    rejectedServer.Normalize();
    var rejectedSnapshot = SnapshotFrom(rejectedServer, 44);
    var staleRejectedRecovery = RoomChartSyncReducer.ApplyFullSnapshot(
        RoomChartSyncState.Create(41, baseline, generation: 12),
        rejectedRecovery.FullSnapshotRequest,
        rejectedSnapshot,
        rejectedCurrent,
        currentChangeSerial: 21);
    Assert(staleRejectedRecovery.IgnoredStaleResponse && staleRejectedRecovery.ApplyPlan == null,
        "a stale rejected-attempt full snapshot escaped its reducer state token");
    var rejectedReduction = RoomChartSyncReducer.ApplyFullSnapshot(
        rejectedRecovery.State,
        rejectedRecovery.FullSnapshotRequest,
        rejectedSnapshot,
        rejectedCurrent,
        currentChangeSerial: 21);
    var rejectedPlan = rejectedReduction.ApplyPlan
        ?? throw new InvalidOperationException("deterministic rejection recovery did not produce a plan");
    var rejectedMerged = rejectedPlan.Document;
    Assert(rejectedPlan.Mode == RoomChartApplyMode.OrdinaryRemoteMerge &&
           !rejectedPlan.ClearHistory && !rejectedPlan.GenerationAdvanced &&
           rejectedPlan.HasConcurrentLocalChanges && rejectedPlan.ShouldRequestAnotherSync &&
           rejectedPlan.State is { Generation: 12, Revision: 44, PendingWrite: null } &&
           rejectedMerged.Metadata.Title == baseline.Metadata.Title &&
           rejectedMerged.Metadata.Creator == "Recovery remote mapper" &&
           rejectedMerged.Metadata.Version == "Typed after rejected request" &&
           rejectedMerged.Notes.All(note => note.Id != rejectedRequestNoteId) &&
           rejectedMerged.Notes.Any(note => note.Id == rejectedLaterNoteId) &&
           rejectedMerged.Notes.Any(note => note.Id == rejectedRemoteNoteId),
        "deterministic rejection replayed the rejected request or lost later local/remote edits");

    // chart_baseline_changed is different: its snapshot is authoritative, clears
    // all pending/local history, and starts a new generation.
    var baselineChangedSession = new EditorSession();
    baselineChangedSession.ReplaceDocument(baseline.Clone());
    baselineChangedSession.Mutate(document => document.Metadata.Title = "Must be discarded");
    var baselineChangedState = RoomChartSyncState.Create(60, baseline, generation: 32);
    var baselineChangedAttempt = RoomChartSyncReducer.PrepareAttempt(
        baselineChangedState,
        baselineChangedSession.Document,
        baselineChangedSession.ChangeSerial,
        Guid.Parse("c2200000-0000-0000-0000-000000000001"));
    baselineChangedSession.Mutate(document => document.Metadata.Version = "Also discarded");
    var baselineChangedRecovery = RoomChartSyncReducer.PrepareRejectedAttemptRecovery(
        baselineChangedAttempt.State,
        baselineChangedAttempt.Attempt,
        RoomChartApplyMode.AuthoritativeReset);
    var authoritativeServer = baseline.Clone();
    authoritativeServer.Metadata.Title = "New authoritative baseline";
    authoritativeServer.Normalize();
    var baselineChangedReduction = RoomChartSyncReducer.ApplyFullSnapshot(
        baselineChangedRecovery.State,
        baselineChangedRecovery.FullSnapshotRequest,
        SnapshotFrom(authoritativeServer, 65),
        baselineChangedSession.Document,
        baselineChangedSession.ChangeSerial);
    var baselineChangedPlan = baselineChangedReduction.ApplyPlan
        ?? throw new InvalidOperationException("authoritative rejection recovery did not produce a plan");
    Assert(baselineChangedRecovery.State.PendingWrite == null &&
           baselineChangedRecovery.FullSnapshotRequest is
           {
               Reason: RoomChartFullSnapshotReason.AuthoritativeBaselineChanged,
               Mode: RoomChartApplyMode.AuthoritativeReset,
               ReplayMode: RoomChartFullSnapshotReplayMode.DiscardLocalChanges,
               PreserveLocalChanges: false
           } &&
           baselineChangedPlan.ClearHistory && baselineChangedPlan.GenerationAdvanced &&
           baselineChangedPlan.State is { Generation: 33, Revision: 65, PendingWrite: null } &&
           baselineChangedPlan.Document.Metadata.Title == "New authoritative baseline" &&
           baselineChangedPlan.Document.Metadata.Version == baseline.Metadata.Version,
        "chart_baseline_changed retained rejected intent or failed to advance the generation");
    ApplyPlanToSession(baselineChangedSession, baselineChangedPlan);
    Assert(!baselineChangedSession.Undo() && !baselineChangedSession.Redo() &&
           baselineChangedSession.Document.Metadata.Title == "New authoritative baseline",
        "authoritative rejection recovery did not clear the pre-baseline history");

    // A normal acknowledgement may arrive together with a later remote operation.
    // Merge both into canonical state, then reapply only edits made after the request.
    var canonicalAfterOwnWrite = OnlineChartSyncCodec.ApplyOperations(
        baseline,
        [AppliedOperation(41, 71, retryPreparation.Attempt.Batch)]);
    var remoteDocument = canonicalAfterOwnWrite.Clone();
    remoteDocument.Metadata.Creator = "Remote mapper";
    remoteDocument.Notes.Add(new ChartNote
    {
        Id = remoteNoteId,
        Lane = 0,
        Beat = BeatPosition.FromDouble(4),
        OnlineCreatorUserId = 72
    });
    remoteDocument.Normalize();
    var remoteBatch = OnlineChartSyncCodec.CreateBatch(
        41,
        canonicalAfterOwnWrite,
        remoteDocument,
        Guid.Parse("d0000000-0000-0000-0000-000000000001"));
    var ordinaryDelta = new OnlineChartDelta(
        42,
        [
            AppliedOperation(41, 71, retryPreparation.Attempt.Batch),
            AppliedOperation(42, 72, remoteBatch)
        ],
        [],
        false);
    var ordinaryReduction = RoomChartSyncReducer.ReduceDelta(
        retryPreparation.State,
        retryPreparation.Attempt,
        ordinaryDelta,
        currentAfterRequest,
        currentChangeSerial: 11);
    var ordinaryPlan = ordinaryReduction.ApplyPlan
        ?? throw new InvalidOperationException("ordinary acknowledgement did not produce an apply plan");
    var ordinaryDocument = ordinaryPlan.Document;
    Assert(ordinaryReduction.Disposition == RoomChartDeltaDisposition.Applied &&
           ordinaryPlan.Mode == RoomChartApplyMode.OrdinaryRemoteMerge &&
           !ordinaryPlan.ClearHistory && !ordinaryPlan.GenerationAdvanced &&
           ordinaryPlan.PendingWriteAcknowledged && ordinaryPlan.HasConcurrentLocalChanges &&
           ordinaryPlan.State is { Revision: 42, Generation: 7, PendingWrite: null } &&
           ordinaryDocument.Metadata.Title == "Submitted title" &&
           ordinaryDocument.Metadata.Creator == "Remote mapper" &&
           ordinaryDocument.Metadata.Version == "Typed after request" &&
           ordinaryDocument.Notes.Count(note => note.Id == submittedNoteId) == 1 &&
           ordinaryDocument.Notes.Any(note => note.Id == remoteNoteId) &&
           ordinaryDocument.Notes.Any(note => note.Id == afterRequestNoteId),
        "ordinary ACK merge lost remote state, duplicated the request or dropped later local edits");

    // The exact retry may be answered only by a lightweight receipt after operation
    // compaction. The full snapshot acknowledges the request but keeps newer local intent.
    var secondPreparation = RoomChartSyncReducer.PrepareAttempt(
        ordinaryPlan.State, ordinaryDocument, currentChangeSerial: 11, secondOperationId);
    Assert(secondPreparation.Attempt.IsWrite &&
           secondPreparation.Attempt.Batch.BaseRevision == 42 &&
           secondPreparation.Attempt.Batch.OperationId == secondOperationId &&
           secondPreparation.Attempt.Batch.UpsertNotes.Any(note => note.Id == afterRequestNoteId) &&
           secondPreparation.Attempt.Batch.UpsertNotes.All(note => note.Id != remoteNoteId),
        "post-ACK preparation did not isolate only the still-local intent");
    var currentAfterSecondRequest = ordinaryDocument.Clone();
    currentAfterSecondRequest.Metadata.CoverFile = "after-second-request.png";
    currentAfterSecondRequest.Notes.Add(new ChartNote
    {
        Id = afterSecondRequestNoteId,
        Lane = 1,
        Beat = BeatPosition.FromDouble(5)
    });
    currentAfterSecondRequest.Normalize();
    var compactSignal = new OnlineChartDelta(
        50, [], [], false, AuthoritativeBaselineRevision: null, RequiresFullSnapshot: true);
    var compactReduction = RoomChartSyncReducer.ReduceDelta(
        secondPreparation.State,
        secondPreparation.Attempt,
        compactSignal,
        currentAfterSecondRequest,
        currentChangeSerial: 12);
    var compactRequest = compactReduction.FullSnapshotRequest
        ?? throw new InvalidOperationException("receipt compaction did not request a full snapshot");
    Assert(compactReduction.Disposition == RoomChartDeltaDisposition.RequiresFullSnapshot &&
           compactRequest is
           {
               Reason: RoomChartFullSnapshotReason.ServerCompaction,
               Mode: RoomChartApplyMode.OrdinaryRemoteMerge,
               AcknowledgePendingWrite: true,
               RebasePendingWrite: false,
               PreserveLocalChanges: true
           } && compactReduction.State.PendingWrite?.OperationId == secondOperationId,
        "ordinary receipt compaction was mistaken for rollback or cleared pending before recovery");

    var serverAfterSecondWrite = OnlineChartSyncCodec.ApplyOperations(
        ordinaryPlan.State.CanonicalDocument,
        [AppliedOperation(43, 71, secondPreparation.Attempt.Batch)]);
    var compactServerDocument = serverAfterSecondWrite.Clone();
    compactServerDocument.Metadata.ArtistOriginal = "远端艺术家";
    compactServerDocument.Notes.Add(new ChartNote
    {
        Id = compactRemoteNoteId,
        Lane = 2,
        Beat = BeatPosition.FromDouble(6),
        OnlineCreatorUserId = 73
    });
    compactServerDocument.Normalize();
    var compactSnapshot = SnapshotFrom(compactServerDocument, 50);
    var stateAdvancedWhileSnapshotWasInFlight = RoomChartSyncState.Create(
        51, compactServerDocument, compactReduction.State.Generation);
    var staleCompactFull = RoomChartSyncReducer.ApplyFullSnapshot(
        stateAdvancedWhileSnapshotWasInFlight,
        compactRequest,
        compactSnapshot,
        currentAfterSecondRequest,
        currentChangeSerial: 12);
    Assert(staleCompactFull.IgnoredStaleResponse &&
           ReferenceEquals(staleCompactFull.State, stateAdvancedWhileSnapshotWasInFlight) &&
           staleCompactFull.ApplyPlan == null && staleCompactFull.State.Revision == 51,
        "a delayed full snapshot overwrote state advanced within the same generation");
    var compactFullReduction = RoomChartSyncReducer.ApplyFullSnapshot(
        compactReduction.State,
        compactRequest,
        compactSnapshot,
        currentAfterSecondRequest,
        currentChangeSerial: 12);
    var compactPlan = compactFullReduction.ApplyPlan
        ?? throw new InvalidOperationException("compacted receipt snapshot did not produce an apply plan");
    var compactMerged = compactPlan.Document;
    Assert(!compactFullReduction.IgnoredStaleResponse &&
           compactPlan.Mode == RoomChartApplyMode.OrdinaryRemoteMerge &&
           !compactPlan.ClearHistory && compactPlan.PendingWriteAcknowledged &&
           !compactPlan.PendingWriteRebased && compactPlan.ShouldRequestAnotherSync &&
           compactPlan.State is { Revision: 50, Generation: 7, PendingWrite: null } &&
           compactMerged.Metadata.ArtistOriginal == "远端艺术家" &&
           compactMerged.Metadata.CoverFile == "after-second-request.png" &&
           compactMerged.Notes.Count(note => note.Id == afterRequestNoteId) == 1 &&
           compactMerged.Notes.Any(note => note.Id == compactRemoteNoteId) &&
           compactMerged.Notes.Any(note => note.Id == afterSecondRequestNoteId),
        "full compact recovery replayed acknowledged content or lost remote/post-request changes");
    var thirdPreparation = RoomChartSyncReducer.PrepareAttempt(
        compactPlan.State, compactMerged, currentChangeSerial: 12, thirdOperationId);
    Assert(thirdPreparation.Attempt.Batch.BaseRevision == 50 &&
           thirdPreparation.Attempt.Batch.OperationId == thirdOperationId &&
           thirdPreparation.Attempt.Batch.UpsertNotes.Any(note => note.Id == afterSecondRequestNoteId) &&
           thirdPreparation.Attempt.Batch.UpsertNotes.All(note =>
               note.Id != afterRequestNoteId && note.Id != compactRemoteNoteId),
        "compact recovery did not prepare only post-request intent against the new canonical revision");

    // A genuine revision regression retires the invalid BaseRevision but is not an
    // authoritative rollback: merge all local intent, preserve history, then prepare
    // a fresh ID/batch against the regressed canonical document.
    var regressionRequestId = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    var regressionLaterId = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    var regressionRedoId = Guid.Parse("e0000000-0000-0000-0000-000000000003");
    var regressionRemoteId = Guid.Parse("e0000000-0000-0000-0000-000000000004");
    var regressionOldOperationId = Guid.Parse("e1000000-0000-0000-0000-000000000001");
    var regressionNewOperationId = Guid.Parse("e1000000-0000-0000-0000-000000000002");
    var regressionSession = new EditorSession();
    regressionSession.ReplaceDocument(baseline.Clone());
    regressionSession.Mutate(document =>
    {
        document.Metadata.Title = "Regression request";
        document.Notes.Add(new ChartNote
        {
            Id = regressionRequestId, Lane = 1, Beat = BeatPosition.FromDouble(7)
        });
    });
    var regressionState = RoomChartSyncState.Create(90, baseline, generation: 20);
    var regressionPreparation = RoomChartSyncReducer.PrepareAttempt(
        regressionState,
        regressionSession.Document,
        regressionSession.ChangeSerial,
        regressionOldOperationId);
    regressionSession.Mutate(document =>
    {
        document.Metadata.Version = "Regression later";
        document.Notes.Add(new ChartNote
        {
            Id = regressionLaterId, Lane = 2, Beat = BeatPosition.FromDouble(8)
        });
    });
    regressionSession.Mutate(document =>
    {
        document.Notes.Add(new ChartNote
        {
            Id = regressionRedoId, Lane = 3, Beat = BeatPosition.FromDouble(9)
        });
    });
    Assert(regressionSession.UndoOwnChanges() == ScopedHistoryResult.Applied,
        "revision-regression fixture did not create redo history");
    var regressionDelta = new OnlineChartDelta(12, [], [], false);
    var regressionReduction = RoomChartSyncReducer.ReduceDelta(
        regressionPreparation.State,
        regressionPreparation.Attempt,
        regressionDelta,
        regressionSession.Document,
        regressionSession.ChangeSerial);
    var regressionRequest = regressionReduction.FullSnapshotRequest
        ?? throw new InvalidOperationException("revision regression did not request a full snapshot");
    Assert(regressionReduction.Disposition == RoomChartDeltaDisposition.RequiresFullSnapshot &&
           regressionRequest is
           {
               Reason: RoomChartFullSnapshotReason.RevisionRegression,
               Mode: RoomChartApplyMode.OrdinaryRemoteMerge,
               AcknowledgePendingWrite: false,
               RebasePendingWrite: true,
               PreserveLocalChanges: true
           },
        "revision regression was classified as an authoritative history reset");
    var regressedServerDocument = baseline.Clone();
    regressedServerDocument.Metadata.Creator = "Regressed remote mapper";
    regressedServerDocument.Notes.Add(new ChartNote
    {
        Id = regressionRemoteId,
        Lane = 0,
        Beat = BeatPosition.FromDouble(10),
        OnlineCreatorUserId = 88
    });
    regressedServerDocument.Normalize();
    var regressionFullReduction = RoomChartSyncReducer.ApplyFullSnapshot(
        regressionReduction.State,
        regressionRequest,
        SnapshotFrom(regressedServerDocument, 12),
        regressionSession.Document,
        regressionSession.ChangeSerial);
    var regressionPlan = regressionFullReduction.ApplyPlan
        ?? throw new InvalidOperationException("revision regression snapshot did not produce an apply plan");
    var regressionMerged = regressionPlan.Document;
    Assert(regressionPlan.Mode == RoomChartApplyMode.OrdinaryRemoteMerge &&
           !regressionPlan.ClearHistory && regressionPlan.PendingWriteRebased &&
           regressionPlan.RequiresFreshWritePreparation && regressionPlan.ShouldRequestAnotherSync &&
           regressionPlan.State is { Revision: 12, Generation: 20, PendingWrite: null } &&
           regressionMerged.Metadata.Creator == "Regressed remote mapper" &&
           regressionMerged.Metadata.Title == "Regression request" &&
           regressionMerged.Metadata.Version == "Regression later" &&
           regressionMerged.Notes.Any(note => note.Id == regressionRemoteId) &&
           regressionMerged.Notes.Any(note => note.Id == regressionRequestId) &&
           regressionMerged.Notes.Any(note => note.Id == regressionLaterId) &&
           regressionMerged.Notes.All(note => note.Id != regressionRedoId),
        "revision regression dropped pending intent or failed to retire the invalid transport batch");
    ApplyPlanToSession(regressionSession, regressionPlan);
    Assert(regressionSession.RedoOwnChanges() == ScopedHistoryResult.Applied &&
           regressionSession.Document.Notes.Any(note => note.Id == regressionRedoId) &&
           regressionSession.Document.Notes.Any(note => note.Id == regressionRemoteId) &&
           regressionSession.UndoOwnChanges() == ScopedHistoryResult.Applied &&
           regressionSession.Document.Notes.All(note => note.Id != regressionRedoId) &&
           regressionSession.Document.Notes.Any(note => note.Id == regressionRemoteId),
        "ordinary revision recovery cleared undo/redo or let scoped history erase remote content");
    var rebuiltPreparation = RoomChartSyncReducer.PrepareAttempt(
        regressionPlan.State,
        regressionSession.Document,
        regressionSession.ChangeSerial,
        regressionNewOperationId);
    var rebuiltIds = rebuiltPreparation.Attempt.Batch.UpsertNotes.Select(note => note.Id).ToHashSet();
    Assert(rebuiltPreparation.Attempt.Batch.BaseRevision == 12 &&
           rebuiltPreparation.Attempt.Batch.OperationId == regressionNewOperationId &&
           rebuiltPreparation.Attempt.Batch.OperationId != regressionOldOperationId &&
           rebuiltIds.Contains(regressionRequestId) && rebuiltIds.Contains(regressionLaterId) &&
           !rebuiltIds.Contains(regressionRemoteId),
        "revision regression did not rebuild the complete pending intent on the new baseline/new ID");
    var rebuiltRetry = RoomChartSyncReducer.PrepareAttempt(
        rebuiltPreparation.State,
        regressionSession.Document,
        regressionSession.ChangeSerial,
        Guid.Parse("e1000000-0000-0000-0000-000000000003"));
    Assert(rebuiltRetry.Attempt.Batch.OperationId == regressionNewOperationId &&
           System.Text.Json.JsonSerializer.Serialize(rebuiltRetry.Attempt.Batch, jsonOptions) ==
           System.Text.Json.JsonSerializer.Serialize(rebuiltPreparation.Attempt.Batch, jsonOptions),
        "rebased pending intent did not become a new stable exact-retry batch");

    // Even without a generation change, a delayed response older than an already
    // installed revision must be ignored rather than regressing canonical state.
    var staleRemoteId = Guid.Parse("f0000000-0000-0000-0000-000000000001");
    var staleState = RoomChartSyncState.Create(30, baseline, generation: 31);
    var staleAttempt = RoomChartSyncReducer.PrepareAttempt(
        staleState, baseline, currentChangeSerial: 0, Guid.NewGuid()).Attempt;
    var advancedServerDocument = baseline.Clone();
    advancedServerDocument.Notes.Add(new ChartNote
    {
        Id = staleRemoteId, Lane = 1, Beat = BeatPosition.FromDouble(11), OnlineCreatorUserId = 90
    });
    advancedServerDocument.Normalize();
    var advancedBatch = OnlineChartSyncCodec.CreateBatch(
        30,
        baseline,
        advancedServerDocument,
        Guid.Parse("f1000000-0000-0000-0000-000000000001"));
    var advancedReduction = RoomChartSyncReducer.ReduceDelta(
        staleState,
        staleAttempt,
        new OnlineChartDelta(35, [AppliedOperation(35, 90, advancedBatch)], [], false),
        baseline,
        currentChangeSerial: 0);
    var advancedState = advancedReduction.ApplyPlan?.State
        ?? throw new InvalidOperationException("newer ordinary delta was not applied");
    var staleReduction = RoomChartSyncReducer.ReduceDelta(
        advancedState,
        staleAttempt,
        new OnlineChartDelta(31, [], [], false),
        advancedServerDocument,
        currentChangeSerial: 1);
    Assert(staleReduction.Disposition == RoomChartDeltaDisposition.IgnoredStaleResponse &&
           ReferenceEquals(staleReduction.State, advancedState) &&
           staleReduction.ApplyPlan == null && staleReduction.FullSnapshotRequest == null &&
           staleReduction.State.Revision == 35 &&
           staleReduction.State.CanonicalDocument.Notes.Any(note => note.Id == staleRemoteId),
        "late same-generation response regressed or replaced newer reducer state");

    // A direct owner rollback is the destructive boundary: clear pending and history,
    // advance generation, and reject every response prepared in the old generation.
    var rollbackLocalId = Guid.Parse("fa000000-0000-0000-0000-000000000001");
    var rollbackLaterId = Guid.Parse("fa000000-0000-0000-0000-000000000002");
    var rollbackAuthoritativeId = Guid.Parse("fa000000-0000-0000-0000-000000000003");
    var rollbackLockId = Guid.Parse("fa000000-0000-0000-0000-000000000004");
    var rollbackSession = new EditorSession();
    rollbackSession.ReplaceDocument(baseline.Clone());
    rollbackSession.Mutate(document => document.Notes.Add(new ChartNote
    {
        Id = rollbackLocalId, Lane = 1, Beat = BeatPosition.FromDouble(12)
    }));
    var directState = RoomChartSyncState.Create(70, baseline, generation: 50);
    var directPreparation = RoomChartSyncReducer.PrepareAttempt(
        directState,
        rollbackSession.Document,
        rollbackSession.ChangeSerial,
        Guid.Parse("fb000000-0000-0000-0000-000000000001"));
    rollbackSession.Mutate(document => document.Notes.Add(new ChartNote
    {
        Id = rollbackLaterId, Lane = 2, Beat = BeatPosition.FromDouble(13)
    }));
    rollbackSession.Mutate(document => document.Metadata.Version = "redo-before-rollback");
    Assert(rollbackSession.UndoOwnChanges() == ScopedHistoryResult.Applied &&
           rollbackSession.RedoOwnChanges() == ScopedHistoryResult.Applied &&
           rollbackSession.UndoOwnChanges() == ScopedHistoryResult.Applied,
        "direct rollback fixture did not contain both undo and redo history");
    var rollbackDocument = baseline.Clone();
    rollbackDocument.KeyCount = 6;
    rollbackDocument.Metadata.Title = "Authoritative rollback";
    rollbackDocument.Notes =
    [
        new ChartNote
        {
            Id = rollbackAuthoritativeId, Lane = 5, Beat = BeatPosition.FromDouble(16),
            OnlineCreatorUserId = 1
        }
    ];
    rollbackDocument.Normalize();
    var restoredSnapshotId = Guid.Parse("fc000000-0000-0000-0000-000000000001");
    var safetySnapshotId = Guid.Parse("fc000000-0000-0000-0000-000000000002");
    var rollbackResult = new OnlineChartRollbackResult(
        SnapshotFrom(
            rollbackDocument,
            80,
            [new OnlineChartLock(
                rollbackLockId,
                new OnlineSyncBeat(4, 0, 1),
                new OnlineSyncBeat(8, 0, 1),
                -64,
                81,
                1)]),
        new OnlineChartSavedSnapshot(
            restoredSnapshotId,
            "Restored baseline",
            55,
            1,
            "Owner",
            1,
            1,
            false,
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)),
        safetySnapshotId);
    var rollbackOperationId = Guid.Parse("fd000000-0000-0000-0000-000000000001");
    var rollbackAttempt = RoomChartSyncReducer.PrepareDirectRollback(
        directPreparation.State, restoredSnapshotId, rollbackOperationId);
    var rollbackReduction = RoomChartSyncReducer.ApplyDirectRollback(
        directPreparation.State, rollbackAttempt, rollbackResult, rollbackSession.Document);
    var rollbackPlan = rollbackReduction.ApplyPlan;
    Assert(!rollbackReduction.IgnoredStaleResponse &&
           rollbackAttempt.Generation == 50 && rollbackAttempt.RequestedRevision == 70 &&
           rollbackAttempt.SnapshotId == restoredSnapshotId &&
           rollbackAttempt.OperationId == rollbackOperationId &&
           rollbackPlan.Mode == RoomChartApplyMode.AuthoritativeReset &&
           rollbackPlan.ClearHistory && rollbackPlan.GenerationAdvanced &&
           rollbackPlan.PreviousGeneration == 50 &&
           rollbackPlan.State is { Generation: 51, Revision: 80, PendingWrite: null } &&
           rollbackPlan.Document.KeyCount == 6 &&
           rollbackPlan.Document.Metadata.Title == "Authoritative rollback" &&
           rollbackPlan.Document.Notes.Single().Id == rollbackAuthoritativeId &&
           rollbackPlan.OwnerProtectedRegions.Single() == new OwnerProtectedRegion(
               rollbackLockId, new BeatPosition(4, 0, 1), new BeatPosition(8, 0, 1)) &&
           rollbackReduction.RestoredSnapshot.Id == restoredSnapshotId &&
           rollbackReduction.SafetySnapshotId == safetySnapshotId,
        "direct rollback did not create a new authoritative generation and clear pending state");
    ApplyPlanToSession(rollbackSession, rollbackPlan);
    Assert(!rollbackSession.Undo() && !rollbackSession.Redo() &&
           rollbackSession.UndoOwnChanges() == ScopedHistoryResult.None &&
           rollbackSession.RedoOwnChanges() == ScopedHistoryResult.None &&
           rollbackSession.Document.Notes.Single().Id == rollbackAuthoritativeId &&
           rollbackSession.OwnerProtectedRegions.Single().Id == rollbackLockId,
        "authoritative rollback plan did not clear old document history and install locks");
    var lateOldGenerationDelta = new OnlineChartDelta(
        81,
        [AppliedOperation(81, 71, directPreparation.Attempt.Batch)],
        [],
        false);
    var lateOldGeneration = RoomChartSyncReducer.ReduceDelta(
        rollbackPlan.State,
        directPreparation.Attempt,
        lateOldGenerationDelta,
        rollbackSession.Document,
        rollbackSession.ChangeSerial);
    Assert(lateOldGeneration.Disposition == RoomChartDeltaDisposition.IgnoredStaleResponse &&
           ReferenceEquals(lateOldGeneration.State, rollbackPlan.State) &&
           lateOldGeneration.State.PendingWrite == null &&
           rollbackSession.Document.Notes.Single().Id == rollbackAuthoritativeId &&
           rollbackSession.UndoOwnChanges() == ScopedHistoryResult.None,
        "old-generation response escaped the rollback generation guard");
    var duplicateRollback = RoomChartSyncReducer.ApplyDirectRollback(
        rollbackPlan.State, rollbackAttempt, rollbackResult, rollbackSession.Document);
    Assert(duplicateRollback.IgnoredStaleResponse &&
           ReferenceEquals(duplicateRollback.State, rollbackPlan.State),
        "a delayed duplicate rollback response cleared the new generation twice");

    // A successful write can be beyond the first 256-operation delta page. The
    // response itself proves commit; chase later pages with GET and never POST the
    // same BaseRevision page forever or generate a duplicate write ID.
    var backlogNoteId = Guid.Parse("fe000000-0000-0000-0000-000000000001");
    var backlogOperationId = Guid.Parse("fe100000-0000-0000-0000-000000000001");
    var ignoredBacklogId = Guid.Parse("fe100000-0000-0000-0000-000000000002");
    var afterBacklogId = Guid.Parse("fe100000-0000-0000-0000-000000000003");
    var backlogLocal = baseline.Clone();
    backlogLocal.Notes.Add(new ChartNote
    {
        Id = backlogNoteId,
        Lane = 2,
        Beat = BeatPosition.FromDouble(20)
    });
    backlogLocal.Normalize();
    var backlogState = RoomChartSyncState.Create(100, baseline, generation: 60);
    var backlogPreparation = RoomChartSyncReducer.PrepareAttempt(
        backlogState, backlogLocal, currentChangeSerial: 1, backlogOperationId);
    var firstBacklogPage = Enumerable.Range(101, 256)
        .Select(revision => AppliedOperation(
            revision,
            actorUserId: 900 + revision,
            new OnlineChartOperationBatch(
                revision - 1,
                [], [], [], [],
                new Guid(revision, 0, 0, new byte[8]))))
        .ToArray();
    var firstBacklogReduction = RoomChartSyncReducer.ReduceDelta(
        backlogPreparation.State,
        backlogPreparation.Attempt,
        new OnlineChartDelta(400, firstBacklogPage, [], HasMore: true),
        backlogLocal,
        currentChangeSerial: 1);
    var firstBacklogPlan = firstBacklogReduction.ApplyPlan
        ?? throw new InvalidOperationException("first backlog page did not produce an apply plan");
    Assert(firstBacklogPlan.State is
           {
               Revision: 356,
               PendingWrite.CommitConfirmed: true
           } && firstBacklogPlan.State.PendingWrite.OperationId == backlogOperationId &&
           firstBacklogPlan.Document.Notes.Any(note => note.Id == backlogNoteId) &&
           firstBacklogPlan.ShouldRequestAnotherSync,
        "successful paginated write did not preserve intent in commit-confirmed receipt chasing state");
    var backlogPullPreparation = RoomChartSyncReducer.PrepareAttempt(
        firstBacklogPlan.State,
        firstBacklogPlan.Document,
        currentChangeSerial: 1,
        ignoredBacklogId);
    Assert(!backlogPullPreparation.Attempt.IsWrite &&
           backlogPullPreparation.Attempt.Batch.IsEmpty &&
           backlogPullPreparation.Attempt.RequestedRevision == 356 &&
           backlogPullPreparation.Attempt.PendingWrite?.OperationId == backlogOperationId &&
           backlogPullPreparation.State.PendingWrite?.OperationId == backlogOperationId,
        "commit-confirmed pending write was POSTed again instead of chasing the next page with GET");
    var finalBacklogPage = Enumerable.Range(357, 43)
        .Select(revision => AppliedOperation(
            revision,
            actorUserId: 900 + revision,
            new OnlineChartOperationBatch(
                revision - 1,
                [], [], [], [],
                new Guid(revision, 0, 0, new byte[8]))))
        .Append(AppliedOperation(400, 71, backlogPreparation.Attempt.Batch))
        .ToArray();
    var finalBacklogReduction = RoomChartSyncReducer.ReduceDelta(
        backlogPullPreparation.State,
        backlogPullPreparation.Attempt,
        new OnlineChartDelta(400, finalBacklogPage, [], HasMore: false),
        firstBacklogPlan.Document,
        currentChangeSerial: 1);
    var finalBacklogPlan = finalBacklogReduction.ApplyPlan
        ?? throw new InvalidOperationException("final backlog page did not produce an apply plan");
    Assert(finalBacklogPlan.PendingWriteAcknowledged &&
           finalBacklogPlan.State is { Revision: 400, PendingWrite: null } &&
           finalBacklogPlan.Document.Notes.Count(note => note.Id == backlogNoteId) == 1 &&
           !finalBacklogPlan.ShouldRequestAnotherSync,
        "later-page acknowledgement did not finish receipt chasing exactly once");
    var afterBacklogPreparation = RoomChartSyncReducer.PrepareAttempt(
        finalBacklogPlan.State,
        finalBacklogPlan.Document,
        currentChangeSerial: 1,
        afterBacklogId);
    Assert(!afterBacklogPreparation.Attempt.IsWrite &&
           afterBacklogPreparation.Attempt.Batch.IsEmpty &&
           afterBacklogPreparation.State.PendingWrite == null,
        "completed backlog acknowledgement generated a duplicate write");

    var applicationReferences = typeof(RoomChartSyncReducer).Assembly.GetReferencedAssemblies()
        .Select(reference => reference.Name ?? string.Empty)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(typeof(RoomChartSyncReducer).Assembly == typeof(EditorSession).Assembly &&
           applicationReferences.Contains("LumaChart.Online") &&
           !applicationReferences.Overlaps(["PresentationCore", "PresentationFramework", "WindowsBase"]),
        "B1 reducer is not in the shared Application layer or retained a WPF dependency");

    static ChartDocument CreateBaseline(Guid noteId, Guid eventId)
    {
        var document = new ChartDocument
        {
            KeyCount = 4,
            Metadata = new ChartMetadata
            {
                Title = "Baseline",
                Artist = "Base artist",
                Creator = "Base mapper",
                Version = "Base version",
                AudioFile = "audio.ogg",
                OverallDifficulty = 7,
                HpDrainRate = 6
            },
            Notes =
            [
                new ChartNote
                {
                    Id = noteId, Lane = 0, Beat = BeatPosition.FromDouble(1),
                    OnlineCreatorUserId = 7
                }
            ],
            Timing =
            [
                new TimingEvent
                {
                    Id = eventId, Beat = BeatPosition.Zero, Bpm = 120,
                    OnlineCreatorUserId = 7
                }
            ]
        };
        document.Normalize();
        return document;
    }

    static AppliedOnlineChartOperation AppliedOperation(
        long revision,
        long actorUserId,
        OnlineChartOperationBatch batch) =>
        new(revision, actorUserId, batch,
            new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero).AddSeconds(revision));

    static OnlineChartSnapshot SnapshotFrom(
        ChartDocument document,
        long revision,
        IReadOnlyList<OnlineChartLock>? locks = null)
    {
        var initialized = OnlineChartSyncCodec.CreateInitializeRequest(document);
        return new OnlineChartSnapshot(
            revision,
            initialized.KeyCount,
            initialized.Notes,
            initialized.Timing,
            locks ?? [],
            initialized.Metadata);
    }

    static void ApplyPlanToSession(EditorSession session, RoomChartApplyPlan plan)
    {
        if (plan.ApplyDocument)
        {
            var next = plan.Document;
            if (plan.ClearHistory)
            {
                session.ReplaceDocument(next, markDirty: true);
            }
            else
            {
                session.ApplyRemoteMutation(target => ReplaceContent(target, next));
            }
        }
        session.ReplaceOwnerProtectedRegions(plan.OwnerProtectedRegions);
    }

    static void ReplaceContent(ChartDocument target, ChartDocument source)
    {
        target.Metadata = source.Metadata.Clone();
        target.KeyCount = source.KeyCount;
        target.Notes.Clear();
        target.Notes.AddRange(source.Notes.Select(note => note.Clone()));
        target.Timing.Clear();
        target.Timing.AddRange(source.Timing.Select(item => item.Clone()));
    }
}

static void TestOnlineAuthFailureBoundaries() =>
    TestOnlineAuthFailureBoundariesAsync().GetAwaiter().GetResult();

static async Task TestOnlineAuthFailureBoundariesAsync()
{
    var store = new FakeSessionTokenStore { Token = "stored-token" };
    var tokenState = new OnlineSessionTokenState(store);
    Assert(await tokenState.RestoreAsync() == "stored-token" &&
           tokenState.CurrentToken == "stored-token" && store.LoadCalls == 1,
        "secure token restore did not populate the in-memory session exactly once");
    await tokenState.StoreAsync("new-token");
    Assert(tokenState.CurrentToken == "new-token" && store.Token == "new-token" && store.SaveCalls == 1,
        "a securely persisted token was not committed to the in-memory session");
    await tokenState.ClearAsync();
    Assert(tokenState.CurrentToken == null && store.Token == null && store.DeleteCalls == 1,
        "clearing the session did not clear both memory and secure storage");

    var failingSaveStore = new FakeSessionTokenStore
    {
        Token = "old-token",
        SaveException = new IOException("secure storage unavailable")
    };
    var failingSaveState = new OnlineSessionTokenState(failingSaveStore);
    AssertThrows<IOException>(
        () => failingSaveState.StoreAsync("must-not-be-used").AsTask().GetAwaiter().GetResult(),
        "secure-store write failure was silently accepted");
    Assert(failingSaveState.CurrentToken == null && failingSaveStore.Token == null &&
           failingSaveStore.DeleteCalls == 1,
        "secure-store write failure did not fail closed and remove the stale token");

    var failingLoadStore = new FakeSessionTokenStore
    {
        LoadException = new InvalidDataException("encrypted token is corrupt")
    };
    var failingLoadState = new OnlineSessionTokenState(failingLoadStore);
    AssertThrows<InvalidDataException>(
        () => failingLoadState.RestoreAsync().AsTask().GetAwaiter().GetResult(),
        "secure-store read failure was converted into a permissive session");
    Assert(failingLoadState.CurrentToken == null,
        "secure-store read failure left an authenticated in-memory token");

    var failingDeleteStore = new FakeSessionTokenStore
    {
        Token = "cannot-delete",
        DeleteException = new IOException("secure token is locked")
    };
    var failingDeleteState = new OnlineSessionTokenState(failingDeleteStore);
    await failingDeleteState.RestoreAsync();
    AssertThrows<IOException>(
        () => failingDeleteState.ClearAsync().AsTask().GetAwaiter().GetResult(),
        "an explicit secure-token delete failure was reported as success");
    Assert(failingDeleteState.CurrentToken == null,
        "an explicit secure-token delete failure left the in-memory bearer enabled");

    var doubleFailureStore = new FakeSessionTokenStore
    {
        Token = "stale-token",
        SaveException = new IOException("secure save failed"),
        DeleteException = new UnauthorizedAccessException("secure delete failed")
    };
    var doubleFailureState = new OnlineSessionTokenState(doubleFailureStore);
    AssertThrows<AggregateException>(
        () => doubleFailureState.StoreAsync("replacement-token").AsTask().GetAwaiter().GetResult(),
        "save plus cleanup failure was hidden instead of remaining observable");
    Assert(doubleFailureState.CurrentToken == null,
        "save plus cleanup failure left the replacement bearer enabled");

    var blockingStore = new BlockingSessionTokenStore();
    var serializedState = new OnlineSessionTokenState(blockingStore);
    var pendingStore = serializedState.StoreAsync("serialized-token").AsTask();
    await blockingStore.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var pendingClear = serializedState.ClearAsync().AsTask();
    blockingStore.ReleaseSave();
    await Task.WhenAll(pendingStore, pendingClear);
    Assert(serializedState.CurrentToken == null && blockingStore.Token == null &&
           blockingStore.Operations.SequenceEqual(["save", "delete"]),
        "concurrent secure-token operations were not serialized in call order");

    var now = new DateTimeOffset(2026, 7, 20, 6, 0, 0, TimeSpan.Zero);
    var timeProvider = new SpecTimeProvider(now);
    using var handler = new SpecHttpMessageHandler();
    var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent(
            "{\"code\":\"rate_limited\",\"message\":\"请求过于频繁。\"}",
            Encoding.UTF8,
            "application/json")
    };
    throttled.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(17));
    handler.Enqueue(throttled);
    using var client = new HttpClient(handler);
    var api = new OnlineAuthApi(
        client,
        new Uri("https://unit.lumachart.invalid/"),
        timeProvider,
        status => $"localized-http-{(int)status}");
    AssertThrows<ArgumentException>(
        () => _ = new OnlineAuthApi(client, new Uri("http://insecure.example.test/")),
        "the shared auth transport accepted a non-loopback plaintext HTTP endpoint");
    _ = new OnlineAuthApi(client, new Uri("http://127.0.0.1:5080/"));
    OnlineApiException? failure = null;
    try
    {
        await api.LoginAsync(new OnlineLoginRequestDto("user", "pass", Guid.NewGuid(), "123456"));
    }
    catch (OnlineApiException exception)
    {
        failure = exception;
    }
    Assert(failure is
           {
               Code: "rate_limited",
               StatusCode: HttpStatusCode.TooManyRequests,
               Message: "请求过于频繁。",
               RetryAfter: var retryAfter
           } && retryAfter == TimeSpan.FromSeconds(17),
        "auth errors lost the server code, status, message or injected-clock Retry-After");

    handler.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent("not-json", Encoding.UTF8, "application/json")
    });
    OnlineApiException? fallbackFailure = null;
    try
    {
        await api.GetCurrentUserAsync("still-private");
    }
    catch (OnlineApiException exception)
    {
        fallbackFailure = exception;
    }
    Assert(fallbackFailure is
           {
               Message: "localized-http-503",
               StatusCode: HttpStatusCode.ServiceUnavailable
           },
        "the platform adapter could not inject its localized HTTP fallback");

    var forbiddenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PresentationCore", "PresentationFramework", "WindowsBase", "NAudio", "NAudio.Core",
        "System.Security.Cryptography.ProtectedData", "Mono.Android", "Microsoft.Android",
        "LumaChart.App"
    };
    foreach (var assembly in new[]
             {
                 typeof(BeatPosition).Assembly,
                 typeof(EditorSession).Assembly,
                 typeof(ISessionTokenStore).Assembly,
                 typeof(OnlineAuthApi).Assembly
             })
    {
        var references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(!references.Overlaps(forbiddenReferences),
            $"shared assembly {assembly.GetName().Name} retained a UI, Windows, DPAPI or Android dependency");
        var hasNativeImports = assembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Instance))
            .SelectMany(method => method.CustomAttributes)
            .Any(attribute => attribute.AttributeType.FullName is
                "System.Runtime.InteropServices.DllImportAttribute" or
                "System.Runtime.InteropServices.LibraryImportAttribute");
        Assert(!hasNativeImports,
            $"shared assembly {assembly.GetName().Name} contains a platform native import");
    }
    Assert(typeof(IAppStorageLocations).GetProperties().Select(property => property.Name)
               .SequenceEqual(["Settings", "PersistentEditing", "ResourceCache", "SessionScratch"]),
        "the storage-location abstraction no longer exposes the four required roots");
}

#if LUMACHART_SERVER
static void TestRoomChartOperationContract()
{
    var metadata = new RoomChartMetadata(
        "Title", "原文", "Artist", "原文艺术家", "Mapper", "Hard",
        "audio.ogg", "cover.jpg", "background.jpg", 12_345, 8, 8, 175);
    var batch = new RoomChartOperationBatch(4, [], [], [], [], Guid.NewGuid(), metadata, 7);
    var json = System.Text.Json.JsonSerializer.Serialize(batch,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    var roundTrip = System.Text.Json.JsonSerializer.Deserialize<RoomChartOperationBatch>(json,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    Assert(roundTrip?.Metadata == metadata && roundTrip.Metadata.Bpm == 175 && roundTrip.KeyCount == 7,
        "metadata or key-count was lost from a room chart operation");
}

static void TestRoomChartServerValidation()
{
    var metadata = new RoomChartMetadata(
        "Title", "原文", "Artist", "原文艺术家", "Mapper", "Hard",
        "audio/song.ogg", "images/cover.jpg", "images/background.jpg", 12_345, 8, 8, 175);
    var validBatch = new RoomChartOperationBatch(
        4, [], [], [], [], Guid.NewGuid(), metadata, null);
    RoomChartSyncStore.ValidateBatch(validBatch);
    RoomChartSyncStore.ValidateMetadata(metadata);
    var concurrentMetadata = RoomChartSyncStore.MergeMetadataChanges(
        metadata,
        metadata with { Title = "Remote title", Bpm = 160 },
        metadata with { Version = "Local version", Bpm = null });
    Assert(concurrentMetadata.Title == "Remote title" && concurrentMetadata.Version == "Local version" &&
           concurrentMetadata.Bpm == 160,
        "independent concurrent metadata fields did not merge or a legacy update cleared chart BPM");
    var staleFallbackMetadata = RoomChartSyncStore.MergeMetadataChanges(
        metadata with { Bpm = null },
        metadata with { Title = "Remote title", Bpm = 160 },
        metadata with { Version = "Local version", Bpm = 120 });
    Assert(staleFallbackMetadata.Bpm == 160 && staleFallbackMetadata.Title == "Remote title" &&
           staleFallbackMetadata.Version == "Local version",
        "a synthesized BPM fallback from a legacy baseline overwrote the concurrent server BPM");
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.ValidateMetadata(metadata with { Bpm = double.NaN }),
        "a non-finite chart BPM was accepted by the room boundary");

    AssertThrows<ArgumentException>(() => RoomChartSyncStore.ValidateBatch(
        validBatch with { OperationId = Guid.Empty }),
        "an empty OperationId was accepted");
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.EnsureBaseRevisionAccepted(4, 7, 5),
        "an operation older than a snapshot rollback barrier was accepted");
    AssertThrows<RoomChartPermissionException>(() => RoomChartSyncStore.EnsureMemberCanApply(
            new RoomMemberAccess("viewer", true, true, true), validBatch),
        "a viewer bypassed role-level read-only enforcement through remembered permissions");

    var outOfRange = new RoomChartNote(
        Guid.NewGuid(), 4, new SyncBeat(1, 0, 1), null,
        null, 100, 0, false, 0, null, 1);
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.EnsureNotesFitKeyCount(4, [outOfRange]),
        "KeyCount shrink retained a Note outside the accepted lane range");

    var unsafeSound = outOfRange with { Lane = 0, Sound = "../outside.wav" };
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.ValidateNote(unsafeSound, 4),
        "a traversal path was accepted as an online Note sound");
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.ValidateMetadata(
            metadata with { BackgroundFile = "C:\\Users\\Public\\outside.png" }),
        "an absolute path was accepted as online chart metadata");

    var exactLock = RoomChartSyncStore.NormalizeLockRange(new ToggleRoomChartLockRequest(
        new SyncBeat(6, 1, 12), new SyncBeat(5, 5, 6)));
    Assert(exactLock.StartBeat == new SyncBeat(5, 5, 6) &&
           exactLock.EndBeat == new SyncBeat(6, 1, 12),
        "the server expanded or reordered an exact rational lock range incorrectly");
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.NormalizeLockRange(
            new ToggleRoomChartLockRequest(new SyncBeat(5, 5, 6), new SyncBeat(5, 5, 6))),
        "the server accepted an empty lock range and could expand it outside the selection");

    const long ownerUserId = 4_294_967_300;
    var initialRequest = new InitializeRoomChartRequest(
        4,
        [],
        [],
        metadata,
        [
            new RoomChartLockRange(new SyncBeat(6, 1, 12), new SyncBeat(5, 5, 6)),
            new RoomChartLockRange(new SyncBeat(20, 3, 6), new SyncBeat(21, 1, 4))
        ]);
    var initialLocks = RoomChartSyncStore.NormalizeInitialLocks(initialRequest, ownerUserId);
    Assert(initialLocks.Count == 2 &&
           initialLocks.Select(item => item.Id).All(id => id != Guid.Empty) &&
           initialLocks.Select(item => item.Id).Distinct().Count() == 2 &&
           initialLocks[0].StartBeat == new SyncBeat(5, 5, 6) &&
           initialLocks[0].EndBeat == new SyncBeat(6, 1, 12) &&
           initialLocks[1].StartBeat == new SyncBeat(20, 3, 6) &&
           initialLocks.All(item =>
               item.StartLane == RoomChartSyncStore.FullWidthLockStartLane &&
               item.EndLane == RoomChartSyncStore.FullWidthLockEndLane &&
               item.OwnerUserId == ownerUserId),
        "initial locks were not server-owned exact full-width ranges");
    Assert(RoomChartSyncStore.NormalizeInitialLocks(
               initialRequest with { InitialLocks = null }, ownerUserId).Count == 0,
        "a legacy initializer without initialLocks did not remain compatible");
    var tooManyInitialLocks = Enumerable.Repeat(
        new RoomChartLockRange(new SyncBeat(0, 0, 1), new SyncBeat(0, 1, 4)),
        RoomChartSyncStore.MaximumInitialLockRanges + 1).ToArray();
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.NormalizeInitialLocks(
            initialRequest with { InitialLocks = tooManyInitialLocks }, ownerUserId),
        "the server accepted more than 4096 initial lock ranges");
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.NormalizeInitialLocks(
            initialRequest with
            {
                InitialLocks =
                [new RoomChartLockRange(new SyncBeat(5, 5, 6), new SyncBeat(5, 5, 6))]
            },
            ownerUserId),
        "an empty initial lock range was accepted");

    var storedExactLock = new RoomChartLock(
        Guid.NewGuid(), new SyncBeat(5, 5, 6), new SyncBeat(6, 1, 12),
        RoomChartSyncStore.FullWidthLockStartLane,
        RoomChartSyncStore.FullWidthLockEndLane,
        ownerUserId);
    var readExactLock = RoomChartSyncStore.NormalizeStoredLock(storedExactLock);
    Assert(readExactLock.StartBeat == storedExactLock.StartBeat &&
           readExactLock.EndBeat == storedExactLock.EndBeat,
        "a persisted exact fractional full-width lock was expanded to whole measures while reading");
    var precisionStoredLock = storedExactLock with
    {
        Id = Guid.NewGuid(),
        StartBeat = new SyncBeat(1_000_000, 1, 1_000_000),
        EndBeat = new SyncBeat(1_000_000, 1, 999_999)
    };
    var readPrecisionLock = RoomChartSyncStore.NormalizeStoredLock(precisionStoredLock);
    Assert(readPrecisionLock.StartBeat == precisionStoredLock.StartBeat &&
           readPrecisionLock.EndBeat == precisionStoredLock.EndBeat,
        "double precision made an exact non-empty full-width lock look like a legacy empty range");
    var readLegacyLock = RoomChartSyncStore.NormalizeStoredLock(storedExactLock with
    {
        StartLane = 0,
        EndLane = 3
    });
    Assert(readLegacyLock.StartBeat == new SyncBeat(4, 0, 1) &&
           readLegacyLock.EndBeat == new SyncBeat(8, 0, 1) &&
           readLegacyLock.StartLane == RoomChartSyncStore.FullWidthLockStartLane &&
           readLegacyLock.EndLane == RoomChartSyncStore.FullWidthLockEndLane,
        "legacy lane locks stopped receiving their compatibility measure expansion");
}

static void TestRoomChartLockSelectionToggle()
{
    const long ownerUserId = 9_001;
    static RoomChartLock Existing(SyncBeat start, SyncBeat end, long ownerUserId) => new(
        Guid.NewGuid(), start, end,
        RoomChartSyncStore.FullWidthLockStartLane,
        RoomChartSyncStore.FullWidthLockEndLane,
        ownerUserId);
    static ToggleRoomChartLockSelectionRequest Select(params RoomChartLockRange[] ranges) => new(ranges);

    var extend = RoomChartSyncStore.PlanLockSelectionToggle(
        [Existing(new SyncBeat(0, 0, 1), new SyncBeat(0, 1, 4), ownerUserId)],
        ownerUserId,
        Select(new RoomChartLockRange(new SyncBeat(0, 0, 1), new SyncBeat(0, 1, 2))));
    Assert(!extend.IsUnlock && extend.Locks.Count == 1 &&
           extend.Locks[0].StartBeat == new SyncBeat(0, 0, 1) &&
           extend.Locks[0].EndBeat == new SyncBeat(0, 1, 2),
        "a partly covered wider selection removed its existing lock instead of extending the union");

    var split = RoomChartSyncStore.PlanLockSelectionToggle(
        [Existing(new SyncBeat(0, 0, 1), new SyncBeat(0, 3, 4), ownerUserId)],
        ownerUserId,
        Select(new RoomChartLockRange(new SyncBeat(0, 1, 4), new SyncBeat(0, 1, 2))));
    Assert(split.IsUnlock && split.Locks.Count == 2 &&
           split.Locks[0].StartBeat == new SyncBeat(0, 0, 1) &&
           split.Locks[0].EndBeat == new SyncBeat(0, 1, 4) &&
           split.Locks[1].StartBeat == new SyncBeat(0, 1, 2) &&
           split.Locks[1].EndBeat == new SyncBeat(0, 3, 4),
        "unlocking a covered middle selection did not preserve both unselected lock remnants");

    var mixedCoverage = RoomChartSyncStore.PlanLockSelectionToggle(
        [Existing(new SyncBeat(0, 0, 1), new SyncBeat(0, 1, 4), ownerUserId)],
        ownerUserId,
        Select(
            new RoomChartLockRange(new SyncBeat(0, 0, 1), new SyncBeat(0, 1, 4)),
            new RoomChartLockRange(new SyncBeat(2, 0, 1), new SyncBeat(2, 1, 4))));
    Assert(!mixedCoverage.IsUnlock && mixedCoverage.Locks.Count == 2 &&
           mixedCoverage.Locks[0].StartBeat == new SyncBeat(0, 0, 1) &&
           mixedCoverage.Locks[0].EndBeat == new SyncBeat(0, 1, 4) &&
           mixedCoverage.Locks[1].StartBeat == new SyncBeat(2, 0, 1) &&
           mixedCoverage.Locks[1].EndBeat == new SyncBeat(2, 1, 4),
        "one covered disjoint range changed a mixed selection into an unlock and discarded its uncovered range");

    var equivalentBoundary = RoomChartSyncStore.PlanLockSelectionToggle(
        [Existing(new SyncBeat(0, 0, 1), new SyncBeat(0, 3, 6), ownerUserId)],
        ownerUserId,
        Select(new RoomChartLockRange(new SyncBeat(0, 1, 2), new SyncBeat(0, 3, 4))));
    Assert(!equivalentBoundary.IsUnlock && equivalentBoundary.Locks.Count == 1 &&
           equivalentBoundary.Locks[0].StartBeat == new SyncBeat(0, 0, 1) &&
           equivalentBoundary.Locks[0].EndBeat == new SyncBeat(0, 3, 4),
        "equivalent 3/6 and 1/2 endpoints were treated as a gap or overlap instead of an adjacent union");

    var equivalentCoverage = RoomChartSyncStore.PlanLockSelectionToggle(
        [Existing(new SyncBeat(0, 0, 1), new SyncBeat(0, 3, 6), ownerUserId)],
        ownerUserId,
        Select(new RoomChartLockRange(new SyncBeat(0, 0, 1), new SyncBeat(0, 1, 2))));
    Assert(equivalentCoverage.IsUnlock && equivalentCoverage.Locks.Count == 0,
        "equivalent rational endpoints did not count as full selection coverage");

    var outerStart = new SyncBeat(70, 1, 999_983);
    var firstCutStart = new SyncBeat(70, 100, 999_983);
    var firstCutEnd = new SyncBeat(70, 200, 999_983);
    var overlapBoundary = new SyncBeat(70, 500_000, 999_983);
    var secondCutEnd = new SyncBeat(70, 600_000, 999_983);
    var outerEnd = new SyncBeat(71, 7, 999_983);
    var complexDifference = RoomChartSyncStore.PlanLockSelectionToggle(
        [
            Existing(outerStart, overlapBoundary, ownerUserId),
            Existing(firstCutEnd, outerEnd, ownerUserId)
        ],
        ownerUserId,
        Select(
            new RoomChartLockRange(firstCutStart, firstCutEnd),
            new RoomChartLockRange(overlapBoundary, secondCutEnd)));
    Assert(complexDifference.IsUnlock && complexDifference.Locks.Count == 3 &&
           complexDifference.Locks[0].StartBeat == outerStart &&
           complexDifference.Locks[0].EndBeat == firstCutStart &&
           complexDifference.Locks[1].StartBeat == firstCutEnd &&
           complexDifference.Locks[1].EndBeat == overlapBoundary &&
           complexDifference.Locks[2].StartBeat == secondCutEnd &&
           complexDifference.Locks[2].EndBeat == outerEnd,
        "complex exact-rational lock subtraction did not preserve all three outside remnants");

    var complexRefill = RoomChartSyncStore.PlanLockSelectionToggle(
        complexDifference.Locks,
        ownerUserId,
        Select(
            new RoomChartLockRange(firstCutStart, firstCutEnd),
            new RoomChartLockRange(overlapBoundary, secondCutEnd)));
    Assert(!complexRefill.IsUnlock && complexRefill.Locks.Count == 1 &&
           complexRefill.Locks[0].StartBeat == outerStart &&
           complexRefill.Locks[0].EndBeat == outerEnd,
        "relocking two exact-rational internal gaps did not merge the original continuous range");

    Assert(extend.Locks.Concat(split.Locks).Concat(mixedCoverage.Locks)
               .Concat(equivalentBoundary.Locks)
               .Concat(complexDifference.Locks)
               .Concat(complexRefill.Locks)
               .All(item => item.Id != Guid.Empty && item.OwnerUserId == ownerUserId &&
                            item.StartLane == RoomChartSyncStore.FullWidthLockStartLane &&
                            item.EndLane == RoomChartSyncStore.FullWidthLockEndLane),
        "the selection planner produced a non-authoritative id, owner or lane envelope");
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.PlanLockSelectionToggle(
            [], ownerUserId, Select()),
        "an empty atomic lock selection was accepted");
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.PlanLockSelectionToggle(
            [], ownerUserId,
            new ToggleRoomChartLockSelectionRequest(Enumerable.Repeat(
                new RoomChartLockRange(new SyncBeat(0, 0, 1), new SyncBeat(0, 1, 4)),
                RoomChartSyncStore.MaximumLockSelectionRanges + 1).ToArray())),
        "an oversized atomic lock selection was accepted");
}

static void TestRoomChartSafetyBoundaries()
{
    Assert(CollaborationRevisionPolicy.RequiresFullSnapshot(12, 0) &&
           CollaborationRevisionPolicy.RequiresFullSnapshot(12, 11) &&
           !CollaborationRevisionPolicy.RequiresFullSnapshot(12, 12) &&
           !CollaborationRevisionPolicy.RequiresFullSnapshot(12, 13),
        "server revision regression did not require a full snapshot replacement");

    Assert(RoomChartSyncStore.InitializeChartSql.Contains("ON CONFLICT (room_id) DO NOTHING",
               StringComparison.Ordinal) &&
           !RoomChartSyncStore.InitializeChartSql.Contains("DO UPDATE", StringComparison.OrdinalIgnoreCase),
        "chart initialization is not create-once at the database conflict boundary");

    Assert(RoomChartSyncStore.ReadOperationByIdSql.Contains("room_id = $1", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadOperationByIdSql.Contains("actor_user_id = $2", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadOperationByIdSql.Contains("operation_id = $3", StringComparison.Ordinal),
        "OperationId lookup is not scoped to the exact room and authenticated actor");
    // An exact weak-network retry can arrive after rollback advanced the compacted
    // snapshot baseline. It must be recognized before stale-base validation, while a
    // new operation with the same stale base must still be rejected.
    RoomChartSyncStore.EnsureRequestBaseRevisionAccepted(
        operationAlreadyExists: true, baseRevision: 4, currentRevision: 12, snapshotRevision: 10);
    AssertThrows<RoomChartBaselineException>(() => RoomChartSyncStore.EnsureRequestBaseRevisionAccepted(
            operationAlreadyExists: false, baseRevision: 4, currentRevision: 12, snapshotRevision: 10),
        "a new cross-generation operation did not produce the recoverable baseline-changed signal");

    var retryBatch = new RoomChartOperationBatch(
        4, [], [Guid.NewGuid()], [], [], Guid.NewGuid());
    var downgradedViewer = new RoomMemberAccess("viewer", false, false, false);
    RoomChartSyncStore.EnsureMemberCanApplyRequest(
        downgradedViewer, operationAlreadyExists: true, retryBatch);
    AssertThrows<RoomChartPermissionException>(() => RoomChartSyncStore.EnsureMemberCanApplyRequest(
            downgradedViewer, operationAlreadyExists: false, retryBatch),
        "a newly submitted viewer operation bypassed current granular permissions");
    AssertThrows<RoomChartPermissionException>(() => RoomChartSyncStore.EnsureMemberCanApplyRequest(
            null, operationAlreadyExists: true, retryBatch),
        "a removed member used an old OperationId to read the current room chart");

    var legacyNoteId = Guid.NewGuid();
    var legacyBatch = new RoomChartOperationBatch(
        4,
        [new RoomChartNote(
            legacyNoteId, 1, new SyncBeat(2, 2, 4), null, " samples\\hit.wav ",
            100, 0, false, 0, null, 17)],
        [], [], [], Guid.NewGuid());
    var stateNote = RoomChartSyncStore.NormalizeNoteForState(legacyBatch.UpsertNotes.Single());
    var stateTiming = RoomChartSyncStore.NormalizeTimingForState(new RoomChartTiming(
        Guid.NewGuid(), new SyncBeat(3, 3, 6), 120, 0, null, 1, "1", "", null, 17));
    Assert(stateNote.Beat == new SyncBeat(2, 2, 4) && stateNote.Sound == "samples/hit.wav" &&
           stateTiming.Beat == new SyncBeat(3, 3, 6),
        "normal room state reduced an authored Beat expression");
    var serverNormalizedLegacyBatch = legacyBatch with
    {
        UpsertNotes =
        [
            legacyBatch.UpsertNotes.Single() with
            {
                Beat = new SyncBeat(2, 1, 2),
                Sound = "samples/hit.wav",
                CreatorUserId = 999
            }
        ]
    };
    Assert(RoomChartSyncStore.ComputeLegacyOperationPayloadHash(legacyBatch) ==
           RoomChartSyncStore.ComputeLegacyOperationPayloadHash(serverNormalizedLegacyBatch),
        "legacy idempotency comparison did not ignore normalization/server-owned creator fields");
    Assert(RoomChartSyncStore.ComputeLegacyOperationPayloadHash(legacyBatch) !=
           RoomChartSyncStore.ComputeLegacyOperationPayloadHash(
               legacyBatch with { DeleteNoteIds = [legacyNoteId] }),
        "a legacy OperationId with a different payload was incorrectly accepted");
    var legacyMetadataHashBatch = new RoomChartOperationBatch(
        4, [], [], [], [], Guid.Parse("11111111-2222-3333-4444-555555555555"),
        new RoomChartMetadata(
            "Title", "原文", "Artist", "原文艺术家", "Mapper", "Hard",
            "audio/song.ogg", "images/cover.jpg", "images/background.jpg", 12_345, 8, 8));
    var legacyMetadataPayloadHash = RoomChartSyncStore.ComputeLegacyOperationPayloadHash(legacyMetadataHashBatch);
    Assert(legacyMetadataPayloadHash ==
           "37c8b63bf4f026992230f8cefbf62669947d2e33f6ed89d8c6d563d6695e0102",
        $"nullable chart BPM changed a legacy operation payload hash: {legacyMetadataPayloadHash}");

    var roomId = Guid.NewGuid();
    var otherRoomId = Guid.NewGuid();
    const long actorUserId = 17;
    var otherOperationId = Guid.NewGuid();
    var operationId = Guid.NewGuid();
    var safetyId = RoomChartSyncStore.CreateRollbackSafetySnapshotId(
        roomId, actorUserId, operationId);
    Assert(safetyId != Guid.Empty &&
           safetyId == RoomChartSyncStore.CreateRollbackSafetySnapshotId(
               roomId, actorUserId, operationId) &&
           safetyId != RoomChartSyncStore.CreateRollbackSafetySnapshotId(
               roomId, actorUserId, otherOperationId) &&
           safetyId != RoomChartSyncStore.CreateRollbackSafetySnapshotId(
               otherRoomId, actorUserId, operationId) &&
           safetyId != RoomChartSyncStore.CreateRollbackSafetySnapshotId(
               roomId, actorUserId + 1, operationId),
        "rollback retries did not derive one stable room/actor/operation-scoped safety snapshot ID");
    AssertThrows<ArgumentException>(
        () => RoomChartSyncStore.CreateRollbackSafetySnapshotId(
            roomId, actorUserId, Guid.Empty),
        "an empty rollback OperationId produced a safety snapshot ID");
    AssertThrows<ArgumentException>(
        () => RoomChartSyncStore.CreateRollbackSafetySnapshotId(
            Guid.Empty, actorUserId, operationId),
        "an empty room ID produced a rollback safety snapshot ID");

    var restoredAt = DateTimeOffset.UtcNow;
    var restoredResult = new RoomChartSavedSnapshot(
        Guid.NewGuid(), "Before chorus", 42, actorUserId, "Owner", 123, 7, false, restoredAt);
    var restoredRoundTrip = RoomChartSyncStore.DeserializeRollbackResult(
        RoomChartSyncStore.SerializeRollbackResult(restoredResult));
    Assert(restoredRoundTrip == restoredResult,
        "compact rollback result metadata did not survive persistence serialization");

    Assert(RoomChartSyncStore.GetNextRevision(41) == 42,
        "normal room revision increment failed");
    AssertThrows<OverflowException>(
        () => RoomChartSyncStore.GetNextRevision(long.MaxValue),
        "room revision overflow wrapped instead of being rejected");
    Assert(RoomChartSyncStore.ComputeRollbackRequestHash(Guid.NewGuid()) !=
           RoomChartSyncStore.ComputeRollbackRequestHash(Guid.NewGuid()),
        "different rollback targets produced the same request identity");

    var tooLate = new RoomChartNote(
        Guid.NewGuid(), 0, new SyncBeat(RoomChartSyncStore.MaximumBeatBar + 1, 0, 1), null,
        null, 100, 0, false, 0, null, 1);
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.ValidateNote(tooLate, 4),
        "an online Note beyond the supported Beat range was accepted");
    RoomChartSyncStore.EnsureChartObjectLimits(
        RoomChartSyncStore.MaximumChartNotes,
        RoomChartSyncStore.MaximumChartTimingEvents);
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.EnsureChartObjectLimits(
            RoomChartSyncStore.MaximumChartNotes + 1,
            RoomChartSyncStore.MaximumChartTimingEvents),
        "online operations could grow a room beyond the maximum Note count");
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.EnsureChartObjectLimits(
            RoomChartSyncStore.MaximumChartNotes,
            RoomChartSyncStore.MaximumChartTimingEvents + 1),
        "online operations could grow a room beyond the maximum Timing count");

    var hugeDelay = new RoomChartTiming(
        Guid.NewGuid(), new SyncBeat(0, 0, 1), 120,
        RoomChartSyncStore.MaximumTimingMagnitude + 1, null, 0, "0", "", null, 1);
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.ValidateTiming(hugeDelay),
        "an excessive finite Timing delay was accepted");
    var hugeEffect = new RoomChartTiming(
        Guid.NewGuid(), new SyncBeat(0, 0, 1), null, 0, 0,
        RoomChartSyncStore.MaximumTimingMagnitude + 1,
        (RoomChartSyncStore.MaximumTimingMagnitude + 1).ToString(
            System.Globalization.CultureInfo.InvariantCulture), "", null, 1);
    AssertThrows<ArgumentException>(() => RoomChartSyncStore.ValidateTiming(hugeEffect),
        "an excessive finite Effect value was accepted");
}

static void TestRoomChartReceiptCompactionAndBudget()
{
    Assert(!RoomChartSyncStore.ShouldCompactOperationLog(0, 0, 1) &&
           !RoomChartSyncStore.ShouldCompactOperationLog(
               RoomChartSyncStore.MaximumPendingOperationCount - 2,
               RoomChartSyncStore.MaximumPendingOperationBytes - 2,
               1),
        "a bounded operation log compacted before either threshold was reached");
    Assert(RoomChartSyncStore.ShouldCompactOperationLog(
               RoomChartSyncStore.MaximumPendingOperationCount - 1, 0, 1) &&
           RoomChartSyncStore.ShouldCompactOperationLog(
               0, RoomChartSyncStore.MaximumPendingOperationBytes - 1, 1) &&
           RoomChartSyncStore.ShouldCompactOperationLog(
               0, 0, RoomChartSyncStore.MaximumPendingOperationBytes),
        "operation-count or serialized-byte thresholds did not trigger compaction");
    Assert(RoomChartSyncStore.GetNextReceiptCount(
               RoomChartSyncStore.MaximumReceiptCount - 1) == RoomChartSyncStore.MaximumReceiptCount,
        "the last permitted room receipt was rejected");
    AssertThrows<RoomChartCapacityException>(
        () => RoomChartSyncStore.GetNextReceiptCount(RoomChartSyncStore.MaximumReceiptCount),
        "the per-room idempotency receipt hard bound could be exceeded");

    Assert(RoomChartSyncStore.ShouldRequireFullSnapshot(41, 42) &&
           !RoomChartSyncStore.ShouldRequireFullSnapshot(42, 42) &&
           !RoomChartSyncStore.ShouldRequireFullSnapshot(43, 42),
        "a client behind the compacted snapshot was not distinguished from a delta-capable client");
    var compactedDelta = new RoomChartDelta(42, [], [], false, null, true);
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var compactedRoundTrip = System.Text.Json.JsonSerializer.Deserialize<RoomChartDelta>(
        System.Text.Json.JsonSerializer.Serialize(compactedDelta, jsonOptions), jsonOptions);
    Assert(compactedRoundTrip?.RequiresFullSnapshot == true &&
           compactedRoundTrip.CurrentRevision == 42 && compactedRoundTrip.Operations.Count == 0,
        "the compacted-delta full-snapshot signal was lost in transport serialization");
    var rollbackFullDelta = new RoomChartDelta(43, [], [], false, 42, true);
    var rollbackFullRoundTrip = System.Text.Json.JsonSerializer.Deserialize<RoomChartDelta>(
        System.Text.Json.JsonSerializer.Serialize(rollbackFullDelta, jsonOptions), jsonOptions);
    Assert(rollbackFullRoundTrip?.RequiresFullSnapshot == true &&
           CollaborationRevisionPolicy.RequiresAuthoritativeReset(
               41, rollbackFullRoundTrip.AuthoritativeBaselineRevision),
        "a compacted rollback lost the authoritative history-reset marker");
    Assert(CollaborationRevisionPolicy.IsWriteBudgetThrottle("chart_write_budget") &&
           !CollaborationRevisionPolicy.IsWriteBudgetThrottle("other") &&
           CollaborationRevisionPolicy.IsPermanentRoomWriteBlock("chart_room_capacity") &&
           !CollaborationRevisionPolicy.IsPermanentRoomWriteBlock("chart_write_budget") &&
           CollaborationRevisionPolicy.NormalizeWriteBudgetRetryAfter(TimeSpan.FromSeconds(45)) ==
           TimeSpan.FromSeconds(45) &&
           CollaborationRevisionPolicy.NormalizeWriteBudgetRetryAfter(null) == TimeSpan.FromMinutes(1) &&
           CollaborationRevisionPolicy.NormalizeWriteBudgetRetryAfter(TimeSpan.FromHours(2)) ==
           TimeSpan.FromHours(1),
        "client write throttles could ignore Retry-After or retry a permanently full room forever");

    Assert(RoomChartSyncStore.ReadOperationByIdSql.Contains(
               "FROM room_chart_operation_receipts", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadOperationByIdSql.Contains("LEFT JOIN room_chart_operations", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadOperationByIdSql.Contains("receipt.room_id = $1", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadOperationByIdSql.Contains("receipt.actor_user_id = $2", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadOperationByIdSql.Contains("receipt.operation_id = $3", StringComparison.Ordinal),
        "duplicate lookup did not remain receipt-centric and scoped after payload compaction");
    Assert(RoomChartSyncStore.ReadAuthoritativeBaselineAfterSql.Contains(
               "room_chart_operation_receipts", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadAuthoritativeBaselineAfterSql.Contains("room_id = $1", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadAuthoritativeBaselineAfterSql.Contains("revision > $2", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReadAuthoritativeBaselineAfterSql.Contains(
               "rollback_result_json IS NOT NULL", StringComparison.Ordinal),
        "full-snapshot recovery could not distinguish ordinary compaction from a crossed rollback receipt");
    Assert(RoomChartSyncStore.ReceiptBackfillSql.Contains("ON CONFLICT", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReceiptBackfillSql.Contains("DELETE FROM room_chart_operations", StringComparison.Ordinal) &&
           RoomChartSyncStore.ReceiptBackfillSql.Contains("receipt_count = stats.receipt_count", StringComparison.Ordinal) &&
           !RoomChartSyncStore.ReceiptBackfillSql.Contains(
               "DELETE FROM room_chart_operation_receipts", StringComparison.Ordinal),
        "restart migration could delete old ordinary receipts after compaction or rollback");
    Assert(RoomChartSyncStore.ReadOperationByIdSql.Contains(
               "receipt.rollback_result_json", StringComparison.Ordinal),
        "persistent rollback results were not read from lightweight receipts");
    using (var receiptCommand = new Npgsql.NpgsqlCommand())
    {
        RoomChartSyncStore.BindOperationReceiptParameters(
            receiptCommand, Guid.NewGuid(), 7, Guid.NewGuid(), 9,
            new string('a', 64), null);
        Assert(receiptCommand.Parameters.Count == 6 &&
               receiptCommand.Parameters.Cast<Npgsql.NpgsqlParameter>()
                   .All(parameter => string.IsNullOrEmpty(parameter.ParameterName)),
            "receipt INSERT mixed named parameters with PostgreSQL positional placeholders");
    }

    var minuteClock = new ManualTimeProvider(
        new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
    var minuteBudget = new ChartWriteBudgetRegistry(minuteClock);
    Assert(minuteBudget.TryConsume(
               17, ChartWriteBudgetRegistry.MaximumBytesPerMinute, out _),
        "the documented per-minute byte allowance was rejected");
    Assert(!minuteBudget.TryConsume(17, 1, out var minuteRetry) && minuteRetry > TimeSpan.Zero,
        "the per-minute chart byte budget could be exceeded");
    RoomChartSyncStore.EnsureWriteBudgetAccepted(
        operationAlreadyExists: true, minuteBudget, 17,
        ChartWriteBudgetRegistry.MaximumBytesPerMinute);
    AssertThrows<RoomChartWriteBudgetException>(
        () => RoomChartSyncStore.EnsureWriteBudgetAccepted(
            operationAlreadyExists: false, minuteBudget, 17, 1),
        "a unique write bypassed an exhausted budget");

    var hourClock = new ManualTimeProvider(
        new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
    var hourBudget = new ChartWriteBudgetRegistry(hourClock);
    for (var window = 0; window < 8; window++)
    {
        if (window > 0) hourClock.Advance(TimeSpan.FromMinutes(1));
        Assert(hourBudget.TryConsume(
                23, ChartWriteBudgetRegistry.MaximumBytesPerMinute, out _),
            "the hourly chart budget rejected bytes before reaching its documented ceiling");
    }
    hourClock.Advance(TimeSpan.FromMinutes(1));
    Assert(!hourBudget.TryConsume(23, 1, out var hourRetry) && hourRetry >= TimeSpan.FromMinutes(51),
        "the per-hour chart byte budget could be exceeded");
    hourClock.Advance(TimeSpan.FromMinutes(52));
    Assert(hourBudget.TryConsume(
            23, ChartWriteBudgetRegistry.MaximumBytesPerMinute, out _),
        "the hourly chart byte window did not reset");
    hourClock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
    Assert(hourBudget.PruneExpired() == 1 && hourBudget.Count == 0,
        "expired authenticated chart-budget entries were retained indefinitely");

    Assert(ChartWriteBudgetRegistry.MaximumEntries == 50_000,
        "the production chart-budget registry hard bound changed unexpectedly");
    var boundedBudget = new ChartWriteBudgetRegistry(minuteClock, maximumEntries: 2);
    Assert(boundedBudget.TryConsume(1, 1, out _) &&
           boundedBudget.TryConsume(2, 1, out _) &&
           boundedBudget.TryConsume(3, 1, out _) && boundedBudget.Count == 2,
        "the chart-budget registry exceeded its configured user-entry hard bound");

    var acknowledgedNote = new ChartNote
    {
        Lane = 1,
        Beat = BeatPosition.FromDouble(2)
    };
    var acknowledgedRequest = new ChartDocument
    {
        KeyCount = 4,
        Metadata = new ChartMetadata { Title = "Submitted", Creator = "Mapper" },
        Notes = [acknowledgedNote.Clone()],
        Timing = [new TimingEvent { Bpm = 120 }]
    };
    var serverCanonical = acknowledgedRequest.Clone();
    serverCanonical.Metadata.Title = "Server canonical";
    serverCanonical.Metadata.Creator = "Remote mapper";
    serverCanonical.Metadata.Version = "Remote version";
    serverCanonical.Notes.Single().Lane = 2;
    var remoteNote = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(3) };
    serverCanonical.Notes.Add(remoteNote.Clone());
    var currentLocal = acknowledgedRequest.Clone();
    currentLocal.Metadata.Title = "Typed after request";
    var afterRequestNote = new ChartNote { Lane = 3, Beat = BeatPosition.FromDouble(4) };
    currentLocal.Notes.Add(afterRequestNote.Clone());

    var recovered = CollaborationDocumentMergePolicy.ReapplyChangesAfterAcknowledgedRequest(
        serverCanonical, acknowledgedRequest, currentLocal);
    Assert(recovered.Metadata.Title == "Typed after request" &&
           recovered.Metadata.Creator == "Remote mapper" &&
           recovered.Metadata.Version == "Remote version" &&
           recovered.Notes.Single(note => note.Id == acknowledgedNote.Id).Lane == 2 &&
           recovered.Notes.Count(note => note.Id == acknowledgedNote.Id) == 1 &&
           recovered.Notes.Any(note => note.Id == remoteNote.Id) &&
           recovered.Notes.Any(note => note.Id == afterRequestNote.Id),
        "compaction acknowledgement recovery replayed the submitted request or lost post-request Note/Metadata edits");
}

static void TestRoomRollbackAuthoritativeBaseline()
{
    var operationId = Guid.NewGuid();
    var rollbackBatch = new RoomChartOperationBatch(
        41, [], [], [], [], operationId,
        new RoomChartMetadata(
            "Restored", "", "Artist", "", "Owner", "Rollback",
            "audio.ogg", "", "", 0, 8, 8),
        4);
    var delta = new RoomChartDelta(
        42,
        [new AppliedRoomChartOperation(42, 7, rollbackBatch, DateTimeOffset.UtcNow)],
        [],
        false,
        42);
    var jsonOptions = new System.Text.Json.JsonSerializerOptions(
        System.Text.Json.JsonSerializerDefaults.Web);
    var serialized = System.Text.Json.JsonSerializer.Serialize(delta, jsonOptions);
    var roundTrip = System.Text.Json.JsonSerializer.Deserialize<RoomChartDelta>(serialized, jsonOptions);
    Assert(roundTrip?.AuthoritativeBaselineRevision == 42 &&
           roundTrip.Operations.Single().Batch.OperationId == operationId,
        "rollback authoritative-baseline marker was lost from delta serialization");

    Assert(CollaborationRevisionPolicy.RequiresAuthoritativeReset(41, roundTrip!.AuthoritativeBaselineRevision) &&
           !CollaborationRevisionPolicy.RequiresAuthoritativeReset(42, roundTrip.AuthoritativeBaselineRevision) &&
           !CollaborationRevisionPolicy.RequiresAuthoritativeReset(43, roundTrip.AuthoritativeBaselineRevision) &&
           !CollaborationRevisionPolicy.RequiresAuthoritativeReset(41, null),
        "rollback baseline was not applied once, or would reapply on the owner's delayed delta");
    Assert(CollaborationRevisionPolicy.IsAuthoritativeBaselineMarkerValid(42, [41, 42]) &&
           CollaborationRevisionPolicy.IsAuthoritativeBaselineMarkerValid(null, []) &&
           !CollaborationRevisionPolicy.IsAuthoritativeBaselineMarkerValid(42, []) &&
           !CollaborationRevisionPolicy.IsAuthoritativeBaselineMarkerValid(42, [41, 43]),
        "an empty or mismatched delta could carry an unprovable authoritative baseline marker");

    var oldNote = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(1) };
    var memberSession = new EditorSession();
    memberSession.ReplaceDocument(new ChartDocument { KeyCount = 4, Notes = [oldNote] });
    memberSession.Mutate(chart => chart.Notes.Single().Lane = 1);
    memberSession.Mutate(chart => chart.Notes.Single().Lane = 2);
    Assert(memberSession.Undo(), "member rollback test did not create both undo and redo history");

    if (CollaborationRevisionPolicy.RequiresAuthoritativeReset(
            41, roundTrip.AuthoritativeBaselineRevision))
    {
        memberSession.ReplaceDocument(new ChartDocument
        {
            KeyCount = 4,
            Metadata = new ChartMetadata { Title = "Restored" },
            Notes = [new ChartNote { Lane = 3, Beat = BeatPosition.FromDouble(8) }]
        }, markDirty: true);
    }

    Assert(!memberSession.Undo() && !memberSession.Redo() &&
           memberSession.UndoOwnChanges() == ScopedHistoryResult.None &&
           memberSession.RedoOwnChanges() == ScopedHistoryResult.None &&
           memberSession.Document.Metadata.Title == "Restored" &&
           memberSession.Document.Notes.Single().Lane == 3,
        "a non-owner member retained pre-rollback pending Undo/Redo state");

    var legacyDelta = new RoomChartDelta(42, [], [], false);
    Assert(legacyDelta.AuthoritativeBaselineRevision == null,
        "ordinary deltas were incorrectly marked as authoritative resets");
}

static void TestRoomResourceDeduplication()
{
    var root = Path.Combine(Path.GetTempPath(), $"lumachart-resource-spec-{Guid.NewGuid():N}");
    var roomRoot = Path.Combine(root, "rooms");
    var blobRoot = Path.Combine(root, "blobs");
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RoomStorage:Root"] = roomRoot,
                ["RoomStorage:BlobCacheRoot"] = blobRoot,
                ["RoomStorage:MaximumBlobCacheBytes"] = (2L * 1024 * 1024 * 1024).ToString()
            }).Build();
        var environment = new SpecWebHostEnvironment { ContentRootPath = root, WebRootPath = root };
        var store = new RoomResourceStore(configuration, environment);
        byte[] content = [1, 2, 3];
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        Directory.CreateDirectory(blobRoot);
        File.WriteAllBytes(Path.Combine(blobRoot, hash), content);
        var plan = store.PlanAsync(Guid.NewGuid(),
            [
                new RoomResourceEntry("working.mc", 3, hash),
                new RoomResourceEntry("copies/same.mc", 3, hash)
            ], CancellationToken.None).GetAwaiter().GetResult();
        Assert(plan.TotalBytes == 6 && plan.MissingBytes == 0 && plan.MissingSha256.Count == 0,
            "identical cached content at two safe paths was not reused as one blob");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestRoomResourceIntegrityRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"lumachart-resource-integrity-spec-{Guid.NewGuid():N}");
    var roomRoot = Path.Combine(root, "rooms");
    var blobRoot = Path.Combine(root, "blobs");
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RoomStorage:Root"] = roomRoot,
                ["RoomStorage:BlobCacheRoot"] = blobRoot,
                ["RoomStorage:MaximumBlobCacheBytes"] = (2L * 1024 * 1024 * 1024).ToString()
            }).Build();
        var environment = new SpecWebHostEnvironment { ContentRootPath = root, WebRootPath = root };
        var store = new RoomResourceStore(configuration, environment);
        byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] corrupted = [8, 7, 6, 5, 4, 3, 2, 1];
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant();
        RoomResourceEntry[] entries = [new("working.mc", original.Length, hash)];
        var ownerRoom = Guid.NewGuid();
        var reuseRoom = Guid.NewGuid();

        var initialPlan = store.PlanAsync(ownerRoom, entries, CancellationToken.None).GetAwaiter().GetResult();
        Assert(initialPlan.MissingSha256.SequenceEqual([hash]) && initialPlan.MissingBytes == original.Length,
            "a new room resource blob was not requested for upload");
        using (var upload = new MemoryStream(original, writable: false))
            store.SaveBlobAsync(ownerRoom, hash, upload, original.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        store.CommitAsync(ownerRoom, entries, CancellationToken.None).GetAwaiter().GetResult();

        var archivePath = Path.Combine(roomRoot, ownerRoom.ToString("N"), "resources.zip");
        var originalArchive = File.ReadAllBytes(archivePath);
        var archiveWriteTime = File.GetLastWriteTimeUtc(archivePath);
        var corruptArchive = originalArchive.ToArray();
        corruptArchive[corruptArchive.Length / 2] ^= 0x5a;
        File.WriteAllBytes(archivePath, corruptArchive);
        File.SetLastWriteTimeUtc(archivePath, archiveWriteTime.AddSeconds(2));
        Assert(new FileInfo(archivePath).Length == originalArchive.Length,
            "the archive integrity fixture did not preserve its length");
        Assert(store.GetManifestAsync(ownerRoom, CancellationToken.None).GetAwaiter().GetResult() == null &&
               store.GetVerifiedArchivePathAsync(ownerRoom, CancellationToken.None).GetAwaiter().GetResult() == null,
            "a same-length archive replacement remained downloadable under a stale manifest hash");

        File.WriteAllBytes(archivePath, originalArchive);
        File.SetLastWriteTimeUtc(archivePath, archiveWriteTime.AddSeconds(4));
        Assert(store.GetManifestAsync(ownerRoom, CancellationToken.None).GetAwaiter().GetResult() != null &&
               store.GetVerifiedArchivePathAsync(ownerRoom, CancellationToken.None).GetAwaiter().GetResult() == archivePath,
            "restoring the manifest-matching archive did not recover verified downloads");

        var reusePlan = store.PlanAsync(reuseRoom, entries, CancellationToken.None).GetAwaiter().GetResult();
        Assert(reusePlan.MissingSha256.Count == 0 && reusePlan.MissingBytes == 0,
            "a verified resource blob was not reused by a second room");

        var blobPath = Path.Combine(blobRoot, hash);
        var verifiedWriteTime = File.GetLastWriteTimeUtc(blobPath);
        File.WriteAllBytes(blobPath, corrupted);
        File.SetLastWriteTimeUtc(blobPath, verifiedWriteTime.AddSeconds(5));
        Assert(new FileInfo(blobPath).Length == original.Length,
            "the integrity fixture did not preserve the blob length");

        AssertThrows<InvalidOperationException>(
            () => store.CommitAsync(reuseRoom, entries, CancellationToken.None).GetAwaiter().GetResult(),
            "commit accepted a same-length blob whose SHA-256 no longer matched its name");
        Assert(store.GetRoomBlobPathAsync(ownerRoom, hash, CancellationToken.None).GetAwaiter().GetResult() == null,
            "download exposed a same-length blob whose SHA-256 no longer matched its manifest");

        var repairPlan = store.PlanAsync(reuseRoom, entries, CancellationToken.None).GetAwaiter().GetResult();
        Assert(repairPlan.MissingSha256.SequenceEqual([hash]) && repairPlan.MissingBytes == original.Length,
            "plan did not turn a same-length corrupt blob back into a missing upload");
        using (var repair = new MemoryStream(original, writable: false))
            store.SaveBlobAsync(reuseRoom, hash, repair, original.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        store.CommitAsync(reuseRoom, entries, CancellationToken.None).GetAwaiter().GetResult();

        var recoveredPath = store.GetRoomBlobPathAsync(ownerRoom, hash, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(recoveredPath != null && File.ReadAllBytes(recoveredPath).SequenceEqual(original),
            "reupload did not restore the shared blob for existing room downloads");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestRoomResourceUploadPlanCleanup()
{
    var root = Path.Combine(Path.GetTempPath(), $"lumachart-resource-plan-spec-{Guid.NewGuid():N}");
    var roomRoot = Path.Combine(root, "rooms");
    var blobRoot = Path.Combine(root, "blobs");
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RoomStorage:Root"] = roomRoot,
                ["RoomStorage:BlobCacheRoot"] = blobRoot,
                ["RoomStorage:MaximumBlobCacheBytes"] = (2L * 1024 * 1024 * 1024).ToString()
            }).Build();
        var environment = new SpecWebHostEnvironment { ContentRootPath = root, WebRootPath = root };
        var timeProvider = new SpecTimeProvider(DateTimeOffset.UtcNow);
        var store = new RoomResourceStore(configuration, environment, timeProvider);
        byte[] content = [11, 22, 33, 44];
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        RoomResourceEntry[] entries = [new("working.mc", content.Length, hash)];
        var inactiveRoom = Guid.NewGuid();
        var expiringRoom = Guid.NewGuid();
        var freshRoom = Guid.NewGuid();

        var incomingRoot = Path.Combine(roomRoot, ".incoming");
        var staleIncoming = Path.Combine(
            incomingRoot, $"{Guid.NewGuid():N}.{Guid.NewGuid():N}.legacy-upload");
        var activeIncoming = Path.Combine(
            incomingRoot, $"{Guid.NewGuid():N}.{Guid.NewGuid():N}.legacy-upload");
        var unrelatedIncoming = Path.Combine(incomingRoot, "manual-backup.legacy-upload");
        File.WriteAllBytes(staleIncoming, [1]);
        File.WriteAllBytes(activeIncoming, [2]);
        File.WriteAllBytes(unrelatedIncoming, [3]);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        File.SetLastWriteTimeUtc(staleIncoming, now - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(activeIncoming, now - TimeSpan.FromMinutes(30));
        File.SetLastWriteTimeUtc(unrelatedIncoming, now - TimeSpan.FromHours(2));

        store.PlanAsync(inactiveRoom, entries, CancellationToken.None).GetAwaiter().GetResult();
        store.PlanAsync(expiringRoom, entries, CancellationToken.None).GetAwaiter().GetResult();
        store.DeleteExceptAsync(new HashSet<Guid> { expiringRoom }).GetAwaiter().GetResult();
        Assert(!File.Exists(staleIncoming) && File.Exists(activeIncoming) && File.Exists(unrelatedIncoming),
            "incoming cleanup did not delete only stale, strictly named legacy upload temporaries");
        Assert(store.PendingUploadPlanCount == 1,
            "cleanup retained an upload plan for a room outside the active set");
        AssertThrows<InvalidOperationException>(() =>
        {
            using var source = new MemoryStream(content, writable: false);
            store.SaveBlobAsync(inactiveRoom, hash, source, content.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        }, "an inactive room kept using its removed upload plan");

        timeProvider.Advance(TimeSpan.FromMinutes(31));
        store.PlanAsync(freshRoom, entries, CancellationToken.None).GetAwaiter().GetResult();
        store.DeleteExceptAsync(new HashSet<Guid> { expiringRoom, freshRoom }).GetAwaiter().GetResult();
        Assert(store.PendingUploadPlanCount == 1,
            "cleanup did not remove an expired plan while retaining a fresh active plan");
        AssertThrows<InvalidOperationException>(() =>
        {
            using var source = new MemoryStream(content, writable: false);
            store.SaveBlobAsync(expiringRoom, hash, source, content.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        }, "an expired room kept using its removed upload plan");

        using (var source = new MemoryStream(content, writable: false))
            store.SaveBlobAsync(freshRoom, hash, source, content.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        store.CommitAsync(freshRoom, entries, CancellationToken.None).GetAwaiter().GetResult();
        Assert(store.PendingUploadPlanCount == 0,
            "a committed upload plan remained in the in-memory plan store");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestRoomResourceBlobRetention()
{
    var root = Path.Combine(Path.GetTempPath(), $"lumachart-resource-retention-spec-{Guid.NewGuid():N}");
    var roomRoot = Path.Combine(root, "rooms");
    var blobRoot = Path.Combine(root, "blobs");
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RoomStorage:Root"] = roomRoot,
                ["RoomStorage:BlobCacheRoot"] = blobRoot,
                ["RoomStorage:MaximumBlobCacheBytes"] = (2L * 1024 * 1024 * 1024).ToString()
            }).Build();
        var environment = new SpecWebHostEnvironment { ContentRootPath = root, WebRootPath = root };
        var timeProvider = new SpecTimeProvider(DateTimeOffset.UtcNow);
        var store = new RoomResourceStore(configuration, environment, timeProvider);
        byte[] content = [31, 41, 59, 26];
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();
        RoomResourceEntry[] entries = [new("working.mc", content.Length, hash)];
        var roomId = Guid.NewGuid();

        store.PlanAsync(roomId, entries, CancellationToken.None).GetAwaiter().GetResult();
        using (var source = new MemoryStream(content, writable: false))
            store.SaveBlobAsync(roomId, hash, source, content.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        store.CommitAsync(roomId, entries, CancellationToken.None).GetAwaiter().GetResult();

        // The room can remain active longer than the retention period. Closing it
        // starts a new unreferenced window instead of deleting an old-but-protected
        // blob during the next cleanup pass.
        timeProvider.Advance(TimeSpan.FromHours(25));
        store.DeleteAsync(roomId).GetAwaiter().GetResult();
        store.DeleteExceptAsync(new HashSet<Guid>()).GetAwaiter().GetResult();
        var blobPath = Path.Combine(blobRoot, hash);
        Assert(File.Exists(blobPath),
            "a long-lived room blob was deleted immediately when it became unreferenced");

        timeProvider.Advance(TimeSpan.FromHours(23));
        store.DeleteExceptAsync(new HashSet<Guid>()).GetAwaiter().GetResult();
        Assert(File.Exists(blobPath),
            "an unreferenced blob was deleted before its full 24-hour reuse window elapsed");

        timeProvider.Advance(TimeSpan.FromHours(2));
        store.DeleteExceptAsync(new HashSet<Guid>()).GetAwaiter().GetResult();
        Assert(!File.Exists(blobPath),
            "an unreferenced blob survived beyond its 24-hour reuse window");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestRoomResourcePlanConcurrency()
{
    var root = Path.Combine(Path.GetTempPath(), $"lumachart-resource-plan-race-spec-{Guid.NewGuid():N}");
    var roomRoot = Path.Combine(root, "rooms");
    var blobRoot = Path.Combine(root, "blobs");
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RoomStorage:Root"] = roomRoot,
                ["RoomStorage:BlobCacheRoot"] = blobRoot,
                ["RoomStorage:MaximumBlobCacheBytes"] = (2L * 1024 * 1024 * 1024).ToString()
            }).Build();
        var environment = new SpecWebHostEnvironment { ContentRootPath = root, WebRootPath = root };
        var store = new RoomResourceStore(configuration, environment);
        byte[] firstContent = [1, 3, 5, 7];
        byte[] secondContent = [2, 4, 6, 8];
        var firstHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(firstContent)).ToLowerInvariant();
        var secondHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(secondContent)).ToLowerInvariant();
        RoomResourceEntry[] firstEntries = [new("working.mc", firstContent.Length, firstHash)];
        RoomResourceEntry[] secondEntries = [new("working.mc", secondContent.Length, secondHash)];

        // Replacing a plan before a commit makes the older generation stale without
        // removing the replacement plan.
        var staleRoom = Guid.NewGuid();
        store.PlanAsync(staleRoom, firstEntries, CancellationToken.None).GetAwaiter().GetResult();
        store.PlanAsync(staleRoom, secondEntries, CancellationToken.None).GetAwaiter().GetResult();
        AssertThrows<InvalidOperationException>(
            () => store.CommitAsync(staleRoom, firstEntries, CancellationToken.None).GetAwaiter().GetResult(),
            "a commit accepted a replaced upload plan");
        Assert(store.PendingUploadPlanCount == 1,
            "rejecting a stale commit deleted the replacement upload plan");
        using (var source = new MemoryStream(secondContent, writable: false))
            store.SaveBlobAsync(staleRoom, secondHash, source, secondContent.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        store.CommitAsync(staleRoom, secondEntries, CancellationToken.None).GetAwaiter().GetResult();

        // A new plan must wait behind a publishing commit. Once that first commit
        // freezes the room, the waiting plan is rejected instead of replacing it.
        var replacementRoom = Guid.NewGuid();
        store.PlanAsync(replacementRoom, firstEntries, CancellationToken.None).GetAwaiter().GetResult();
        using (var source = new MemoryStream(firstContent, writable: false))
            store.SaveBlobAsync(replacementRoom, firstHash, source, firstContent.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        var archiveGateField = typeof(RoomResourceStore).GetField(
            "archiveWriteGate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var archiveGate = archiveGateField?.GetValue(store) as SemaphoreSlim
                          ?? throw new InvalidOperationException("room archive gate was not found");
        archiveGate.Wait();
        Task<RoomResourceManifest> firstCommit;
        Task<RoomResourcePlan> waitingPlan;
        var reachedArchiveGate = false;
        try
        {
            firstCommit = store.CommitAsync(replacementRoom, firstEntries, CancellationToken.None);
            reachedArchiveGate = !firstCommit.IsCompleted;
            waitingPlan = store.PlanAsync(replacementRoom, secondEntries, CancellationToken.None);
            Assert(!waitingPlan.IsCompleted,
                "a replacement plan bypassed a commit that held the per-room publication gate");
        }
        finally
        {
            archiveGate.Release();
        }
        Assert(reachedArchiveGate, "the plan replacement fixture did not reach the archive gate");
        var firstManifest = firstCommit.GetAwaiter().GetResult();
        AssertThrows<RoomResourceManifestLockedException>(
            () => waitingPlan.GetAwaiter().GetResult(),
            "a plan waiting behind the first publication replaced the frozen manifest");
        Assert(store.PendingUploadPlanCount == 0,
            "publishing a frozen manifest retained a newer upload plan for the same room");

        var publishedRoomFolder = Path.Combine(roomRoot, replacementRoom.ToString("N"));
        var publishedArchivePath = Path.Combine(publishedRoomFolder, "resources.zip");
        var publishedManifestPath = Path.Combine(publishedRoomFolder, "resources.json");
        var publishedArchive = File.ReadAllBytes(publishedArchivePath);
        var publishedManifest = File.ReadAllBytes(publishedManifestPath);

        AssertThrows<RoomResourceManifestLockedException>(
            () => store.PlanAsync(replacementRoom, secondEntries, CancellationToken.None).GetAwaiter().GetResult(),
            "a published room accepted a second modern upload plan");
        AssertThrows<RoomResourceManifestLockedException>(() =>
        {
            using var source = new MemoryStream(secondContent, writable: false);
            store.SaveBlobAsync(replacementRoom, secondHash, source, secondContent.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        }, "a published room accepted another resource blob");
        AssertThrows<RoomResourceManifestLockedException>(
            () => store.CommitAsync(replacementRoom, secondEntries, CancellationToken.None).GetAwaiter().GetResult(),
            "a published room accepted a second modern manifest commit");

        byte[] legacyReplacement;
        using (var stream = new MemoryStream())
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry("working.mc").Open())
                entry.Write(secondContent);
            legacyReplacement = stream.ToArray();
        }
        AssertThrows<RoomResourceManifestLockedException>(() =>
        {
            using var source = new MemoryStream(legacyReplacement, writable: false);
            store.SaveAsync(replacementRoom, source, legacyReplacement.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        }, "a published room accepted a replacement legacy resource archive");

        var retainedManifest = store.GetManifestAsync(replacementRoom, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(retainedManifest != null && retainedManifest.Sha256 == firstManifest.Sha256 &&
               retainedManifest.TotalBytes == firstManifest.TotalBytes &&
               File.ReadAllBytes(publishedArchivePath).SequenceEqual(publishedArchive) &&
               File.ReadAllBytes(publishedManifestPath).SequenceEqual(publishedManifest),
            "a rejected resource replacement changed the first manifest, archive, size or SHA-256");

        for (var index = 0; index < 32; index++)
        {
            var transientRoom = Guid.NewGuid();
            store.PlanAsync(transientRoom, firstEntries, CancellationToken.None).GetAwaiter().GetResult();
            store.CommitAsync(transientRoom, firstEntries, CancellationToken.None).GetAwaiter().GetResult();
        }
        Assert(store.TrackedRoomGateCount == 0,
            "completed operations retained per-room serialization holders indefinitely");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestRoomResourceBlobFileLimit()
{
    var root = Path.Combine(Path.GetTempPath(), $"lumachart-resource-file-limit-spec-{Guid.NewGuid():N}");
    var roomRoot = Path.Combine(root, "rooms");
    var blobRoot = Path.Combine(root, "blobs");
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RoomStorage:Root"] = roomRoot,
                ["RoomStorage:BlobCacheRoot"] = blobRoot,
                ["RoomStorage:MaximumBlobCacheBytes"] = (2L * 1024 * 1024 * 1024).ToString(),
                ["RoomStorage:MaximumBlobCacheFiles"] = "2"
            }).Build();
        var environment = new SpecWebHostEnvironment { ContentRootPath = root, WebRootPath = root };
        var store = new RoomResourceStore(configuration, environment);
        byte[][] contents = [[1, 2, 3], [4, 5, 6], [7, 8, 9]];
        var hashes = contents.Select(content =>
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant())
            .ToArray();

        for (var index = 0; index < 2; index++)
        {
            var roomId = Guid.NewGuid();
            RoomResourceEntry[] entries = [new("working.mc", contents[index].Length, hashes[index])];
            store.PlanAsync(roomId, entries, CancellationToken.None).GetAwaiter().GetResult();
            using var source = new MemoryStream(contents[index], writable: false);
            store.SaveBlobAsync(roomId, hashes[index], source, contents[index].Length, CancellationToken.None)
                .GetAwaiter().GetResult();
            store.CommitAsync(roomId, entries, CancellationToken.None).GetAwaiter().GetResult();
        }

        var rejectedRoom = Guid.NewGuid();
        RoomResourceEntry[] rejectedEntries = [new("working.mc", contents[2].Length, hashes[2])];
        AssertThrows<InvalidOperationException>(
            () => store.PlanAsync(rejectedRoom, rejectedEntries, CancellationToken.None).GetAwaiter().GetResult(),
            "small blobs spread across rooms bypassed the global cache file ceiling");

        // Reusing or repairing an existing hash must remain possible at the ceiling,
        // because neither operation consumes another inode.
        var repairRoom = Guid.NewGuid();
        var existingPath = Path.Combine(blobRoot, hashes[0]);
        var previousWriteTime = File.GetLastWriteTimeUtc(existingPath);
        File.WriteAllBytes(existingPath, [3, 2, 1]);
        File.SetLastWriteTimeUtc(existingPath, previousWriteTime.AddSeconds(2));
        RoomResourceEntry[] repairEntries = [new("working.mc", contents[0].Length, hashes[0])];
        var repairPlan = store.PlanAsync(repairRoom, repairEntries, CancellationToken.None).GetAwaiter().GetResult();
        Assert(repairPlan.MissingSha256.SequenceEqual([hashes[0]]),
            "a corrupt existing blob was not scheduled for repair at the file ceiling");
        using (var source = new MemoryStream(contents[0], writable: false))
            store.SaveBlobAsync(repairRoom, hashes[0], source, contents[0].Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        store.CommitAsync(repairRoom, repairEntries, CancellationToken.None).GetAwaiter().GetResult();
        Assert(Directory.EnumerateFiles(blobRoot)
                   .Count(path => Path.GetFileName(path).Length == 64) == 2,
            "repairing an existing blob changed the cache file count");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestRoomResourceUploadStreaming()
{
    var root = Path.Combine(Path.GetTempPath(), $"lumachart-resource-streaming-spec-{Guid.NewGuid():N}");
    var roomRoot = Path.Combine(root, "rooms");
    var blobRoot = Path.Combine(root, "blobs");
    try
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RoomStorage:Root"] = roomRoot,
                ["RoomStorage:BlobCacheRoot"] = blobRoot,
                ["RoomStorage:MaximumBlobCacheBytes"] = (2L * 1024 * 1024 * 1024).ToString()
            }).Build();
        var environment = new SpecWebHostEnvironment { ContentRootPath = root, WebRootPath = root };
        var store = new RoomResourceStore(configuration, environment, TimeProvider.System, TimeSpan.FromSeconds(5));
        byte[] slowContent = [10, 20, 30, 40];
        byte[] fastContent = [50, 60, 70, 80];
        var slowHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(slowContent)).ToLowerInvariant();
        var fastHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fastContent)).ToLowerInvariant();
        var slowRoom = Guid.NewGuid();
        var fastRoom = Guid.NewGuid();
        RoomResourceEntry[] slowEntries = [new("working.mc", slowContent.Length, slowHash)];
        RoomResourceEntry[] fastEntries = [new("working.mc", fastContent.Length, fastHash)];
        store.PlanAsync(slowRoom, slowEntries, CancellationToken.None).GetAwaiter().GetResult();
        store.PlanAsync(fastRoom, fastEntries, CancellationToken.None).GetAwaiter().GetResult();

        using var slowSource = new BlockingReadStream(slowContent);
        var slowUpload = store.SaveBlobAsync(
            slowRoom, slowHash, slowSource, slowContent.Length, CancellationToken.None);
        slowSource.Started.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Task fastUpload;
        bool fastCompletedBeforeRelease;
        try
        {
            using var fastSource = new MemoryStream(fastContent, writable: false);
            fastUpload = store.SaveBlobAsync(
                fastRoom, fastHash, fastSource, fastContent.Length, CancellationToken.None);
            var first = Task.WhenAny(fastUpload, Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
            fastCompletedBeforeRelease = ReferenceEquals(first, fastUpload) && fastUpload.IsCompletedSuccessfully;
        }
        finally
        {
            slowSource.Release();
        }
        fastUpload.GetAwaiter().GetResult();
        slowUpload.GetAwaiter().GetResult();
        Assert(fastCompletedBeforeRelease,
            "one slow request body held the global blob installation gate and blocked another room upload");

        byte[] legacyArchiveBytes;
        using (var archiveBuffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(archiveBuffer, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry("working.mc").Open())
                entry.Write([123, 125]);
            legacyArchiveBytes = archiveBuffer.ToArray();
        }

        // A legacy whole-ZIP request may stream slowly, but must not hold the
        // archive publication gate needed by another room's modern commit.
        var legacyRoom = Guid.NewGuid();
        using (var slowLegacySource = new BlockingReadStream(legacyArchiveBytes))
        {
            var slowLegacyUpload = store.SaveAsync(
                legacyRoom, slowLegacySource, legacyArchiveBytes.Length, CancellationToken.None);
            slowLegacySource.Started.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Task<RoomResourceManifest> commitDuringLegacy;
            bool commitCompletedBeforeLegacyRelease;
            try
            {
                commitDuringLegacy = store.CommitAsync(fastRoom, fastEntries, CancellationToken.None);
                var first = Task.WhenAny(commitDuringLegacy, Task.Delay(TimeSpan.FromSeconds(2)))
                    .GetAwaiter().GetResult();
                commitCompletedBeforeLegacyRelease = ReferenceEquals(first, commitDuringLegacy) &&
                                                     commitDuringLegacy.IsCompletedSuccessfully;
            }
            finally
            {
                slowLegacySource.Release();
            }
            commitDuringLegacy.GetAwaiter().GetResult();
            slowLegacyUpload.GetAwaiter().GetResult();
            Assert(commitCompletedBeforeLegacyRelease,
                "a slow legacy ZIP body held the global archive gate and blocked another room commit");
        }

        // The per-room ceiling is shared by modern blob and legacy ZIP streams.
        // A third upload in one room is rejected without consuming a global slot,
        // and both protocols release their counts after completion.
        byte[] sharedFirst = [91, 92, 93, 94];
        byte[] sharedSecond = [95, 96, 97, 98];
        var sharedFirstHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(sharedFirst)).ToLowerInvariant();
        var sharedSecondHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(sharedSecond)).ToLowerInvariant();
        var sharedRoom = Guid.NewGuid();
        RoomResourceEntry[] sharedEntries =
        [
            new("working.mc", sharedFirst.Length, sharedFirstHash),
            new("audio.bin", sharedSecond.Length, sharedSecondHash)
        ];
        store.PlanAsync(sharedRoom, sharedEntries, CancellationToken.None).GetAwaiter().GetResult();
        using (var sharedModernSource = new BlockingReadStream(sharedFirst))
        using (var sharedLegacySource = new BlockingReadStream(legacyArchiveBytes))
        {
            var sharedModernUpload = store.SaveBlobAsync(
                sharedRoom, sharedFirstHash, sharedModernSource, sharedFirst.Length, CancellationToken.None);
            var sharedLegacyUpload = store.SaveAsync(
                sharedRoom, sharedLegacySource, legacyArchiveBytes.Length, CancellationToken.None);
            sharedModernSource.Started.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            sharedLegacySource.Started.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            try
            {
                AssertThrows<InvalidOperationException>(() =>
                {
                    using var thirdSource = new MemoryStream(sharedSecond, writable: false);
                    store.SaveBlobAsync(
                            sharedRoom, sharedSecondHash, thirdSource, sharedSecond.Length, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }, "legacy and modern streams did not share the two-upload per-room ceiling");
                // Finish the modern blob before publishing the legacy archive. This
                // keeps the concurrency-limit fixture deterministic under the room
                // publication gate introduced by one-time resource freezing.
                sharedModernSource.Release();
                sharedModernUpload.GetAwaiter().GetResult();
            }
            finally
            {
                sharedModernSource.Release();
                sharedLegacySource.Release();
            }
            sharedLegacyUpload.GetAwaiter().GetResult();
        }
        AssertThrows<RoomResourceManifestLockedException>(() =>
        {
            using var retryShared = new MemoryStream(sharedSecond, writable: false);
            store.SaveBlobAsync(
                    sharedRoom, sharedSecondHash, retryShared, sharedSecond.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        }, "a completed legacy upload did not freeze later modern blob uploads");
        AssertThrows<RoomResourceManifestLockedException>(
            () => store.CommitAsync(sharedRoom, sharedEntries, CancellationToken.None).GetAwaiter().GetResult(),
            "a completed legacy upload did not freeze a later modern commit");

        var timeoutRoot = Path.Combine(root, "timeout");
        var timeoutConfiguration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["RoomStorage:Root"] = Path.Combine(timeoutRoot, "rooms"),
                ["RoomStorage:BlobCacheRoot"] = Path.Combine(timeoutRoot, "blobs"),
                ["RoomStorage:MaximumBlobCacheBytes"] = (2L * 1024 * 1024 * 1024).ToString()
            }).Build();
        var timeoutEnvironment = new SpecWebHostEnvironment
            { ContentRootPath = timeoutRoot, WebRootPath = timeoutRoot };
        var timeoutStore = new RoomResourceStore(
            timeoutConfiguration, timeoutEnvironment, TimeProvider.System, TimeSpan.FromMilliseconds(250));
        var timeoutModernRoom = Guid.NewGuid();
        var timeoutLegacyRoom = Guid.NewGuid();
        timeoutStore.PlanAsync(timeoutModernRoom, slowEntries, CancellationToken.None).GetAwaiter().GetResult();
        using (var neverCompletes = new BlockingReadStream(slowContent))
            AssertThrows<InvalidOperationException>(
                () => timeoutStore.SaveBlobAsync(
                        timeoutModernRoom, slowHash, neverCompletes, slowContent.Length, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "a stalled blob upload did not enforce its total timeout");
        Assert(!Directory.EnumerateFiles(Path.Combine(timeoutRoot, "blobs"), "*.upload").Any(),
            "a timed-out blob upload left its temporary file behind");
        using (var neverCompletesLegacy = new BlockingReadStream(legacyArchiveBytes))
            AssertThrows<InvalidOperationException>(
                () => timeoutStore.SaveAsync(
                        timeoutLegacyRoom, neverCompletesLegacy, legacyArchiveBytes.Length, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "a stalled legacy ZIP upload did not enforce its total timeout");
        Assert(!Directory.EnumerateFiles(
                    Path.Combine(timeoutRoot, "rooms", ".incoming"), "*.legacy-upload").Any(),
            "a timed-out legacy ZIP upload left its temporary file behind");
        using (var retryLegacy = new MemoryStream(legacyArchiveBytes, writable: false))
            timeoutStore.SaveAsync(
                    timeoutLegacyRoom, retryLegacy, legacyArchiveBytes.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        using (var retry = new MemoryStream(slowContent, writable: false))
            timeoutStore.SaveBlobAsync(timeoutModernRoom, slowHash, retry, slowContent.Length, CancellationToken.None)
                .GetAwaiter().GetResult();
        timeoutStore.CommitAsync(timeoutModernRoom, slowEntries, CancellationToken.None).GetAwaiter().GetResult();
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestLegacyRoomArchiveValidation()
{
    var root = Path.Combine(Path.GetTempPath(), $"lumachart-legacy-zip-spec-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var valid = Path.Combine(root, "valid.zip");
        using (var archive = ZipFile.Open(valid, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("working.mc").Open());
            writer.Write("{}");
        }
        RoomResourceStore.ValidateLegacyArchive(valid);

        var traversal = Path.Combine(root, "traversal.zip");
        using (var archive = ZipFile.Open(traversal, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../working.mc").Open());
            writer.Write("{}");
        }
        AssertThrows<InvalidOperationException>(() => RoomResourceStore.ValidateLegacyArchive(traversal),
            "legacy room ZIP traversal was accepted");

        var duplicate = Path.Combine(root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
        {
            archive.CreateEntry("working.mc").Open().Dispose();
            archive.CreateEntry("WORKING.MC").Open().Dispose();
        }
        AssertThrows<InvalidOperationException>(() => RoomResourceStore.ValidateLegacyArchive(duplicate),
            "case-insensitive duplicate room ZIP paths were accepted");

        var directoryFlood = Path.Combine(root, "directory-flood.zip");
        using (var archive = ZipFile.Open(directoryFlood, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("working.mc").Open()))
                writer.Write("{}");
            for (var index = 0; index < RoomResourceStore.MaximumFileCount; index++)
                archive.CreateEntry($"directories/{index:D4}/");
        }
        AssertThrows<InvalidOperationException>(
            () => RoomResourceStore.ValidateLegacyArchive(directoryFlood),
            "legacy ZIP directory entries bypassed the central-directory entry ceiling");

        var bomb = Path.Combine(root, "bomb.zip");
        using (var archive = ZipFile.Open(bomb, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("working.mc", CompressionLevel.SmallestSize).Open())
        {
            var zeros = new byte[2 * 1024 * 1024];
            stream.Write(zeros);
        }
        AssertThrows<InvalidOperationException>(() => RoomResourceStore.ValidateLegacyArchive(bomb),
            "an extreme-compression room ZIP entry was accepted");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

static void TestChartRequestConcurrencyClassification()
{
    using var gate = new ChartRequestConcurrencyGate();
    var roomId = Guid.NewGuid();

    Assert(Classify(HttpMethods.Put, $"/api/rooms/{roomId:D}/chart") == "full-chart",
        "chart initialization bypassed the full-chart concurrency gate");
    Assert(Classify(HttpMethods.Get, $"/api/rooms/{roomId:D}/chart") == "full-chart",
        "full chart download bypassed the full-chart concurrency gate");
    Assert(Classify(HttpMethods.Post, $"/api/rooms/{roomId:D}/chart/operations") == "operation" &&
           Classify(HttpMethods.Post, $"/api/rooms/{roomId:D}/chart/operations/delta") == "operation",
        "chart operation endpoints bypassed their concurrency gate");
    Assert(Classify(HttpMethods.Post, $"/api/rooms/{roomId:D}/chart/snapshots") == "snapshot" &&
           Classify(HttpMethods.Post, $"/api/rooms/{roomId:D}/chart/snapshots/{Guid.NewGuid():D}/rollback") == "snapshot",
        "snapshot endpoints bypassed their concurrency gate");
    Assert(Classify(HttpMethods.Get, $"/api/rooms/{roomId:D}/chart/delta") == null,
        "small polling deltas were unnecessarily placed behind a large-request gate");

    string? Classify(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return gate.Classify(context.Request);
    }
}

static void TestRoomChartChangeFeedConcurrency() =>
    TestRoomChartChangeFeedConcurrencyAsync().GetAwaiter().GetResult();

static async Task TestRoomChartChangeFeedConcurrencyAsync()
{
    var feed = new RoomChartChangeFeed();
    var roomId = Guid.NewGuid();

    // This models the SSE handshake: the subscription exists before the snapshot
    // read, so a revision committed during that read remains queued afterwards.
    await using (var subscription = feed.Subscribe(roomId))
    {
        feed.Publish(roomId, 4);
        Assert(await subscription.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)),
            "a revision published during the initial snapshot read was lost");
        Assert(subscription.Reader.TryRead(out var revision) && revision == 4,
            "the SSE handshake did not retain the revision published during snapshot read");

        feed.Publish(roomId, 5);
        feed.Publish(roomId, 6);
        Assert(await subscription.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)) &&
               subscription.Reader.TryRead(out revision) && revision == 6,
            "the bounded revision channel did not coalesce to the latest wake-up");
    }
    Assert(feed.TrackedRoomCount == 0,
        "disposing the last chart-event subscription retained an empty room holder");

    // Repeatedly overlap the last group of unsubscriptions with a new group of
    // subscriptions. Every replacement must remain reachable by Publish; the old
    // dictionary-of-dictionaries implementation could detach one of these groups.
    for (var round = 0; round < 64; round++)
    {
        var previous = Enumerable.Range(0, 12).Select(_ => feed.Subscribe(roomId)).ToArray();
        using var start = new ManualResetEventSlim();
        var disposals = previous.Select(subscription => Task.Run(async () =>
        {
            start.Wait();
            await subscription.DisposeAsync();
        })).ToArray();
        var replacements = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return feed.Subscribe(roomId);
        })).ToArray();

        start.Set();
        await Task.WhenAll(disposals);
        var active = await Task.WhenAll(replacements);
        Assert(feed.SubscriberCount(roomId) == active.Length,
            "concurrent empty-holder turnover lost a registered chart-event subscriber");

        var expectedRevision = 100L + round;
        feed.Publish(roomId, expectedRevision);
        foreach (var subscription in active)
        {
            Assert(await subscription.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)),
                "a replacement chart-event subscription became orphaned");
            Assert(subscription.Reader.TryRead(out var revision) && revision == expectedRevision,
                "a replacement subscription received the wrong chart revision");
        }
        foreach (var subscription in active) await subscription.DisposeAsync();
        Assert(feed.TrackedRoomCount == 0,
            "concurrent subscription turnover leaked an empty room holder");
    }

    var closing = feed.Subscribe(roomId);
    feed.Close(roomId);
    await closing.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    await closing.DisposeAsync();
    Assert(feed.TrackedRoomCount == 0,
        "closing a room retained its chart-event holder");
}
#endif

static void TestRemoteEditInterruptsInteractiveMutation()
{
    var hold = new ChartNote
    {
        Lane = 1,
        Beat = BeatPosition.FromDouble(2),
        EndBeat = BeatPosition.FromDouble(4)
    };
    var session = new EditorSession();
    session.ReplaceDocument(new ChartDocument { KeyCount = 4, Notes = [hold] });
    session.BeginInteractiveMutation();
    session.UpdateInteractiveMutation(chart => chart.Notes.Single().EndBeat = BeatPosition.FromDouble(6));
    Assert(session.IsInteractiveMutationActive, "interactive LN resize did not start");
    Assert(session.CaptureCommittedDocument().Notes.Single().EndBeat == BeatPosition.FromDouble(4),
        "a multiplayer snapshot exposed an uncommitted drag position");

    var attributionOnly = session.CaptureCommittedDocument();
    attributionOnly.Notes.Single().OnlineCreatorUserId = 42;
    Assert(session.TryMergeRemoteAttributionDuringInteractiveMutation(attributionOnly) &&
           session.IsInteractiveMutationActive &&
           session.Document.Notes.Single().EndBeat == BeatPosition.FromDouble(6) &&
           session.Document.Notes.Single().OnlineCreatorUserId == 42,
        "a delayed creator-attribution acknowledgement interrupted the next drag");

    var realRemoteChange = session.CaptureCommittedDocument();
    realRemoteChange.Notes.Single().Beat = BeatPosition.FromDouble(3);
    Assert(!session.TryMergeRemoteAttributionDuringInteractiveMutation(realRemoteChange) &&
           session.IsInteractiveMutationActive,
        "a real concurrent edit was mistaken for attribution-only acknowledgement");

    session.ApplyRemoteMutation(chart => chart.Notes.Single().EndBeat = BeatPosition.FromDouble(5));

    Assert(!session.IsInteractiveMutationActive, "remote update left a stale interactive snapshot active");
    Assert(session.Document.Notes.Single().EndBeat == BeatPosition.FromDouble(5),
        "server-authoritative LN end was not preserved after interrupting a local resize");

    session.BeginInteractiveMutation();
    session.UpdateInteractiveMutation(chart => chart.Notes.Single().EndBeat = BeatPosition.FromDouble(7));
    session.ApplyRemoteMutation(chart => chart.Notes.Single().EndBeat = null);
    Assert(!session.IsInteractiveMutationActive && !session.Document.Notes.Single().IsHold,
        "server-authoritative LN-to-rice conversion did not survive an interrupted local resize");
}

static void TestOwnerProtectedBatch()
{
    var tap = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(1) };
    var hold = new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(2), EndBeat = BeatPosition.FromDouble(4) };
    var session = new EditorSession();
    session.ReplaceDocument(new ChartDocument { KeyCount = 4, Notes = [tap, hold] });
    session.Selection.UnionWith([tap.Id, hold.Id]);
    Assert(session.SetOwnerProtection(session.Selection, true) == 2 &&
           session.OwnerProtectedNotes.SetEquals(session.Selection) && !session.IsDirty,
        "batch owner lock was incomplete or polluted chart dirty state");

    session.AllowOwnerProtectedEdits = false;
    try
    {
        session.Mutate(chart => chart.Notes.RemoveAll(note => session.Selection.Contains(note.Id)));
        Assert(false, "a member deleted owner-protected notes");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("房主锁定", StringComparison.Ordinal)) { }
    Assert(session.Document.Notes.Count == 2 && !session.IsDirty,
        "rejected member edit did not roll back cleanly");

    session.AllowOwnerProtectedEdits = true;
    Assert(session.SetOwnerProtection([tap.Id, hold.Id], false) == 2 && session.OwnerProtectedNotes.Count == 0,
        "batch owner unlock failed");
}

#if LUMACHART_SERVER
static void TestOwnerProtectedRegion()
{
    var session = new EditorSession();
    session.ReplaceDocument(new ChartDocument
    {
        KeyCount = 4,
        Notes = [new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(1) }],
        Timing =
        [
            new TimingEvent { Beat = BeatPosition.Zero, Bpm = 120 },
            new TimingEvent { Beat = BeatPosition.FromDouble(1.5), Effect = TimingEffectKind.Scroll, EffectValue = 1 }
        ]
    });
    session.AddOwnerProtectedRegion(BeatPosition.FromDouble(0), BeatPosition.FromDouble(4));
    Assert(session.IntersectsOwnerProtectedRegion(new BeatPosition(1, 1, 7), new BeatPosition(1, 1, 7)),
        "position preflight missed an alternate-subdivision note inside the locked region");
    Assert(session.IntersectsOwnerProtectedRegion(BeatPosition.FromDouble(1.5), BeatPosition.FromDouble(1.5)),
        "time-only owner lock left an unlocked lane");
    Assert(!session.IntersectsOwnerProtectedRegion(BeatPosition.FromDouble(4), BeatPosition.FromDouble(4)),
        "end-exclusive owner lock leaked into the following measure");
    session.AddOwnerProtectedRegion(BeatPosition.FromDouble(5), BeatPosition.FromDouble(6));
    Assert(!session.IntersectsOwnerProtectedRegion(
            BeatPosition.FromDouble(4), BeatPosition.FromDouble(5)),
        "an LN ending exactly at a lock start was treated as intersecting the lock");
    Assert(!RoomChartSyncStore.LockTouchesNote(
            new RoomChartLock(
                Guid.NewGuid(), new SyncBeat(5, 0, 1), new SyncBeat(6, 0, 1),
                -64, 81, 1),
            new RoomChartNote(
                Guid.NewGuid(), 0, new SyncBeat(4, 0, 1), new SyncBeat(5, 0, 1),
                null, 100, 0, false, 0, null, 2)),
        "the server treated an LN end boundary as part of the following owner lock");
    var precisionStart = new SyncBeat(1_000_000, 1, 1_000_000);
    var precisionEnd = new SyncBeat(1_000_000, 1, 999_999);
    var precisionLock = new RoomChartLock(
        Guid.NewGuid(), precisionStart, precisionEnd, -64, 81, 1);
    Assert(RoomChartSyncStore.LockTouchesNote(
            precisionLock,
            new RoomChartNote(
                Guid.NewGuid(), 0, precisionStart, null,
                null, 100, 0, false, 0, null, 2)),
        "double precision collapsed a valid rational lock interval around a tap Note");
    Assert(RoomChartSyncStore.LockTouchesNote(
            precisionLock,
            new RoomChartNote(
                Guid.NewGuid(), 0, precisionStart, precisionEnd,
                null, 100, 0, false, 0, null, 2)),
        "a very short rational LN was mistaken for a point outside its lock interval");
    Assert(RoomChartSyncStore.LockTouchesTiming(
            precisionLock,
            new RoomChartTiming(
                Guid.NewGuid(), precisionStart, 120, 0, null, 0,
                string.Empty, string.Empty, null, 2)),
        "double precision collapsed a valid rational lock interval around Timing");
    Assert(session.IsBeatOwnerProtected(BeatPosition.FromDouble(1.5)),
        "effect preflight missed a beat inside the locked region");
    session.AllowOwnerProtectedEdits = false;

    try
    {
        session.Mutate(chart => chart.Notes.Add(new ChartNote
        {
            Lane = 2,
            Beat = new BeatPosition(1, 1, 7)
        }));
        Assert(false, "a member inserted an alternate-subdivision note into a locked region");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("锁定选区", StringComparison.Ordinal)) { }

    try
    {
        session.Mutate(chart => chart.Timing.First(timing => timing.Effect.HasValue).EffectValue = .25);
        Assert(false, "a member changed an effect inside a locked region");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("锁定选区", StringComparison.Ordinal)) { }

    try
    {
        session.Mutate(chart => chart.Notes.Add(new ChartNote
        {
            Lane = 0,
            Beat = BeatPosition.FromDouble(1.25)
        }));
        Assert(false, "a member inserted a note on another lane inside a locked measure");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("锁定选区", StringComparison.Ordinal)) { }

    session.Mutate(chart => chart.Notes.Add(new ChartNote
    {
        Lane = 0,
        Beat = BeatPosition.FromDouble(4)
    }));
    Assert(session.Document.Notes.Count == 2, "the measure after a lock should remain editable");
}
#endif

static void TestOwnerLockSelectionPlanner()
{
    var firstStart = BeatPosition.Preserve(5, 9, 12);
    var firstEnd = BeatPosition.Preserve(5, 10, 12);
    var secondStart = BeatPosition.Preserve(8, 3, 6);
    var secondEnd = BeatPosition.Preserve(8, 4, 6);
    var disjoint = OwnerLockSelectionPlanner.BuildDisjointRanges(
    [
        new OwnerLockSelectionRange(secondStart, secondEnd),
        new OwnerLockSelectionRange(firstStart, firstEnd)
    ]);

    Assert(disjoint.Count == 2,
        "disjoint selected objects were expanded into one continuous owner lock");
    Assert(disjoint[0].StartBeat.Bar == 5 && disjoint[0].StartBeat.Numerator == 9 &&
           disjoint[0].StartBeat.Denominator == 12 &&
           disjoint[0].EndBeat.Numerator == 10 && disjoint[0].EndBeat.Denominator == 12,
        "owner-lock planning did not preserve the exact rational endpoints");
    var gap = new OwnerProtectedRegion(
        Guid.NewGuid(), BeatPosition.Preserve(6, 0, 1), BeatPosition.Preserve(7, 0, 1));
    Assert(disjoint.All(range => !range.Intersects(gap)),
        "an unselected gap was included in a planned owner-lock range");

    var continuousSelection = OwnerLockSelectionPlanner.BuildSelectedObjectRanges(
    [
        new OwnerLockSelectionRange(secondStart, secondEnd),
        new OwnerLockSelectionRange(firstStart, firstEnd)
    ]);
    Assert(continuousSelection.Count == 1 &&
           continuousSelection[0].StartBeat == firstStart &&
           continuousSelection[0].EndBeat == secondEnd &&
           continuousSelection[0].Intersects(gap),
        "a marquee/Shift multi-object selection was split into separate lock segments");
    var exactSingleSelection = OwnerLockSelectionPlanner.BuildSelectedObjectRanges(
        [new OwnerLockSelectionRange(firstStart, firstEnd)]);
    Assert(exactSingleSelection.Count == 1 && exactSingleSelection[0] == disjoint[0],
        "a single selected object was widened instead of retaining its exact row/LN interval");

    var merged = OwnerLockSelectionPlanner.BuildDisjointRanges(
    [
        new OwnerLockSelectionRange(
            BeatPosition.Preserve(2, 2, 4), BeatPosition.Preserve(2, 1, 4)),
        new OwnerLockSelectionRange(
            BeatPosition.Preserve(2, 2, 4), BeatPosition.Preserve(2, 3, 4)),
        new OwnerLockSelectionRange(
            BeatPosition.Preserve(2, 3, 4), BeatPosition.Preserve(3, 0, 1)),
        new OwnerLockSelectionRange(BeatPosition.Zero, BeatPosition.Zero)
    ]);
    Assert(merged.Count == 1 &&
           merged[0].StartBeat.CompareTo(BeatPosition.Preserve(2, 1, 4)) == 0 &&
           merged[0].EndBeat.CompareTo(BeatPosition.Preserve(3, 0, 1)) == 0,
        "overlapping, adjacent or reversed owner-lock ranges were not normalized into their exact union");

    var threeQuarterRows = OwnerLockSelectionPlanner.BuildDisjointRanges(
    [
        OwnerLockSelectionPlanner.BuildPointRange(BeatPosition.Preserve(0, 0, 1), 4),
        OwnerLockSelectionPlanner.BuildPointRange(BeatPosition.Preserve(0, 1, 4), 4),
        OwnerLockSelectionPlanner.BuildPointRange(new BeatPosition(0, 1, 2), 4)
    ]);
    Assert(threeQuarterRows.Count == 1 &&
           threeQuarterRows[0].StartBeat.CompareTo(BeatPosition.Zero) == 0 &&
           threeQuarterRows[0].EndBeat is { Bar: 0, Numerator: 3, Denominator: 4 },
        "three selected quarter rows expanded past the exact [0, 3/4) owner-lock union");

    var mixedDivisionPoint = OwnerLockSelectionPlanner.BuildPointRange(
        BeatPosition.Preserve(0, 5, 6), 4);
    Assert(mixedDivisionPoint.EndBeat is { Bar: 0, Numerator: 11, Denominator: 12 },
        "a 5/6 point at quarter division covered more than its single 1/12 common-denominator row");
    var preservedSixthCell = OwnerLockSelectionPlanner.BuildPointRange(
        BeatPosition.Preserve(0, 9, 12), 4);
    Assert(preservedSixthCell.EndBeat is { Bar: 0, Numerator: 10, Denominator: 12 },
        "the preserved 9/12 sixth-grid row was widened beyond one common-denominator row");
    var oversizedLcmPoint = OwnerLockSelectionPlanner.BuildPointRange(
        BeatPosition.Preserve(0, 1, 999_983), 4);
    Assert(oversizedLcmPoint.StartBeat == BeatPosition.Preserve(0, 1, 999_983) &&
           oversizedLcmPoint.EndBeat is { Bar: 0, Numerator: 2, Denominator: 999_983 },
        "an imported point whose LCM exceeds the wire limit did not fall back to one exact representable row");

    var existingCoverage = new[]
    {
        new OwnerLockSelectionRange(BeatPosition.Preserve(0, 0, 4), BeatPosition.Preserve(0, 1, 4))
    };
    Assert(OwnerLockSelectionPlanner.IsFullyCovered(
            [OwnerLockSelectionPlanner.BuildPointRange(BeatPosition.Preserve(0, 0, 4), 4)],
            existingCoverage),
        "an exactly covered owner-lock row was not recognized as unlockable");
    Assert(!OwnerLockSelectionPlanner.IsFullyCovered(
            [
                OwnerLockSelectionPlanner.BuildPointRange(BeatPosition.Preserve(0, 0, 4), 4),
                OwnerLockSelectionPlanner.BuildPointRange(BeatPosition.Preserve(0, 2, 4), 4)
            ],
            existingCoverage),
        "a partly covered wide selection was incorrectly switched to destructive unlock mode");

    Assert(OwnerLockSelectionPlanner.BuildAutomaticNoteRanges([], 4).Count == 0,
        "an empty chart produced automatic owner-lock ranges");
    var automaticNotes = new[]
    {
        new ChartNote { Beat = BeatPosition.Preserve(2, 0, 1) },
        // The first tap ends at 2:1/4, so this leaves exactly four empty beats.
        new ChartNote { Beat = BeatPosition.Preserve(6, 1, 4) },
        // The preceding tap ends at 6:2/4; this leaves 4 + 1/12 empty beats.
        new ChartNote { Beat = BeatPosition.Preserve(10, 7, 12) }
    };
    var automatic = OwnerLockSelectionPlanner.BuildAutomaticNoteRanges(automaticNotes, 4);
    Assert(automatic.Count == 2 &&
           automatic[0].StartBeat.CompareTo(BeatPosition.Preserve(2, 0, 1)) == 0 &&
           automatic[0].EndBeat.CompareTo(BeatPosition.Preserve(6, 2, 4)) == 0 &&
           automatic[1].StartBeat.CompareTo(BeatPosition.Preserve(10, 7, 12)) == 0 &&
           automatic[1].EndBeat is { Bar: 10, Numerator: 8, Denominator: 12 },
        "automatic owner-lock planning did not keep a four-beat gap joined or split a longer empty section");
    var automaticReversed = OwnerLockSelectionPlanner.BuildAutomaticNoteRanges(
        automaticNotes.Reverse(), 4);
    Assert(automatic.SequenceEqual(automaticReversed),
        "automatic owner-lock planning depended on Note input order");

    var longHoldStart = BeatPosition.Preserve(20, 0, 1);
    var longHoldEnd = BeatPosition.Preserve(26, 0, 1);
    var longHold = OwnerLockSelectionPlanner.BuildAutomaticNoteRanges(
    [
        new ChartNote { Beat = longHoldStart, EndBeat = longHoldEnd }
    ], 4);
    Assert(longHold.Count == 1 && longHold[0] == new OwnerLockSelectionRange(longHoldStart, longHoldEnd),
        "an LN spanning more than one measure was split as if its interior were empty");

    AssertThrows<InvalidOperationException>(() => OwnerLockSelectionPlanner.BuildAutomaticNoteRanges(
            Enumerable.Range(0, OwnerLockSelectionPlanner.MaximumAutomaticLockRanges + 1)
                .Select(index => new ChartNote
                {
                    Beat = BeatPosition.Preserve(index * 5, 0, 4)
                }),
            4),
        "automatic owner-lock planning silently exceeded its 4,096-section safety limit");

    TestTimelineDirectObjectSelection();
}

static void TestTimelineDirectObjectSelection()
{
    var note = Guid.NewGuid();
    var staleNote = Guid.NewGuid();
    var timing = Guid.NewGuid();
    var staleTiming = Guid.NewGuid();
    var noteSelection = new HashSet<Guid> { note, staleNote };
    var timingSelection = new HashSet<Guid> { timing, staleTiming };

    // This is the important regression: clicking an object that is already part
    // of a wide selection must still collapse the selection to that one object.
    TimelineDirectObjectSelection.SelectNote(noteSelection, timingSelection, note);
    Assert(noteSelection.SetEquals([note]) && timingSelection.Count == 0,
        "a direct Note click retained stale Note or Timing rows from a previous wide selection");
    var selectedLockRanges = OwnerLockSelectionPlanner.BuildSelectedObjectRanges(
        new[]
        {
            (Id: note, Beat: BeatPosition.Preserve(4, 1, 4)),
            (Id: staleNote, Beat: BeatPosition.Preserve(20, 2, 4))
        }
        .Where(item => noteSelection.Contains(item.Id))
        .Select(item => OwnerLockSelectionPlanner.BuildPointRange(item.Beat, 4)));
    Assert(selectedLockRanges.Count == 1 &&
           selectedLockRanges[0].StartBeat == BeatPosition.Preserve(4, 1, 4),
        "a direct Note click still planned lock ranges for stale selected objects");

    noteSelection.UnionWith([staleNote]);
    timingSelection.UnionWith([timing, staleTiming]);
    TimelineDirectObjectSelection.SelectTiming(noteSelection, timingSelection, timing);
    Assert(noteSelection.Count == 0 && timingSelection.SetEquals([timing]),
        "a direct Timing click on an already-selected row retained the previous Timing batch");

    noteSelection.UnionWith([note, staleNote]);
    timingSelection.UnionWith([timing, staleTiming]);
    TimelineDirectObjectSelection.SelectNote(noteSelection, timingSelection, note, preserveNoteBatch: true);
    Assert(noteSelection.SetEquals([note, staleNote]) && timingSelection.Count == 0,
        "the explicit Ctrl Note gesture did not preserve its Note batch or retained stale Timing rows");

    timingSelection.Add(timing);
    var lockedAnchorCanMove = TimelineDirectObjectSelection.TrySelectNoteForMove(
        noteSelection,
        timingSelection,
        note,
        preserveNoteBatch: true,
        candidate => candidate != note);
    Assert(!lockedAnchorCanMove && noteSelection.SetEquals([note, staleNote]) && timingSelection.Count == 0,
        "a locked Ctrl-drag anchor borrowed movable Notes from the preserved selection batch");

    var unlockedAnchorCanMove = TimelineDirectObjectSelection.TrySelectNoteForMove(
        noteSelection,
        timingSelection,
        staleNote,
        preserveNoteBatch: true,
        candidate => candidate == staleNote);
    Assert(unlockedAnchorCanMove && noteSelection.SetEquals([note, staleNote]),
        "an unlocked Ctrl-drag anchor no longer preserved its Note batch");
}

static void TestDraftNotes()
{
    var chart = new ChartDocument
    {
        KeyCount = 4,
        Notes =
        [
            new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(1) },
            new ChartNote { IsDraft = true, DraftColumn = -2, Beat = BeatPosition.FromDouble(1.5), EndBeat = BeatPosition.FromDouble(2.5) },
            new ChartNote { IsDraft = true, DraftColumn = 5, Beat = BeatPosition.FromDouble(3) }
        ]
    };
    var serialized = MalodyMcSerializer.Serialize(chart);
    Assert(serialized.Contains("\"noteref\"") && !serialized.Contains("_luma_draft_notes"), "Malody noteref was not saved");
    var roundTrip = MalodyMcSerializer.Parse(serialized);
    Assert(roundTrip.Notes.Count(n => n.IsDraft) == 2 && roundTrip.Notes.Any(n => n.IsDraft && n.DraftColumn == -2 && n.IsHold), "draft notes did not round-trip");
    var osuPath = Path.Combine(Path.GetTempPath(), $"lumachart-draft-{Guid.NewGuid():N}.osu");
    try
    {
        OsuManiaSerializer.Save(roundTrip, osuPath);
        var hitObjectSection = File.ReadAllText(osuPath).Split("[HitObjects]", 2)[1];
        Assert(hitObjectSection.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length == 1, "draft notes leaked into osu hit objects");
    }
    finally { if (File.Exists(osuPath)) File.Delete(osuPath); }
}

static void TestFailedMutationRollback()
{
    var session = new EditorSession();
    session.ReplaceDocument(new ChartDocument
    {
        Notes = [new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(1) }]
    });
    try
    {
        session.Mutate(document =>
        {
            document.Notes[0].Lane = 3;
            throw new InvalidOperationException("simulated edit failure");
        });
        Assert(false, "failing mutation did not propagate its error");
    }
    catch (InvalidOperationException exception) when (exception.Message == "simulated edit failure") { }
    Assert(session.Document.Notes[0].Lane == 0 && !session.IsDirty && !session.Undo(),
        "failing mutation left partial data or an invalid undo entry");

    try
    {
        session.ApplyRemoteMutation(document =>
        {
            document.Notes[0].Lane = 2;
            throw new InvalidOperationException("simulated remote edit failure");
        });
        Assert(false, "failing remote mutation did not propagate its error");
    }
    catch (InvalidOperationException exception) when (exception.Message == "simulated remote edit failure") { }
    Assert(session.Document.Notes[0].Lane == 0 && !session.IsDirty && session.ChangeSerial == 0,
        "failing remote mutation left partial authoritative data or changed the revision");
}

static void TestChartValidator()
{
    var duplicateA = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(1) };
    var duplicateB = new ChartNote { Lane = 0, Beat = BeatPosition.FromDouble(1) };
    var crossingA = new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(2), EndBeat = BeatPosition.FromDouble(4) };
    var crossingB = new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(3), EndBeat = BeatPosition.FromDouble(5) };
    var boundaryTap = new ChartNote { Lane = 2, Beat = BeatPosition.FromDouble(7) };
    var boundaryHold = new ChartNote { Lane = 2, Beat = BeatPosition.FromDouble(6), EndBeat = BeatPosition.FromDouble(7) };
    var draftDuplicate = new ChartNote { IsDraft = true, DraftColumn = 0, Beat = BeatPosition.FromDouble(1) };
    var chart = new ChartDocument
    {
        KeyCount = 4,
        Notes = [duplicateA, duplicateB, crossingA, crossingB, boundaryTap, boundaryHold, draftDuplicate]
    };
    var issues = ChartValidator.Validate(chart);
    Assert(issues.Count == 2, "validator reported false positives or missed structural errors");
    Assert(issues[0].Kind == ChartIssueKind.OverlappingNotes && issues[0].NoteIds.Count == 2,
        "same-beat overlap was not reported with both notes");
    Assert(issues[1].Kind == ChartIssueKind.CrossingLongNotes && Math.Abs(issues[1].Beat.Value - 3) < .001,
        "interleaved LN interval was not classified as a crossing");
}

static void TestReworkDifficulty()
{
    var chart = new ChartDocument
    {
        KeyCount = 4,
        Timing = [new TimingEvent { Beat = BeatPosition.Zero, Bpm = 180, Delay = 37 }]
    };
    for (var i = 0; i < 192; i++)
    {
        var beat = BeatPosition.FromDouble(i / 4d + 1);
        chart.Notes.Add(new ChartNote
        {
            Lane = (i * 3 + i / 7) % 4,
            Beat = beat,
            EndBeat = i % 11 == 0 ? beat + .75 : null
        });
    }
    for (var i = 0; i < 64; i++)
        chart.Notes.Add(new ChartNote { IsDraft = true, DraftColumn = i % 12 - 4, Beat = BeatPosition.FromDouble(1 + i / 16d) });

    var withDrafts = ReworkDifficultyAnalyzer.Analyze(chart);
    var withoutDraftsChart = chart.Clone();
    withoutDraftsChart.Notes.RemoveAll(note => note.IsDraft);
    var withoutDrafts = ReworkDifficultyAnalyzer.Analyze(withoutDraftsChart);
    Assert(withDrafts.NoteCount == 192 && withDrafts.LongNoteCount == 18, "Rework included draft notes or miscounted LN notes");
    Assert(double.IsFinite(withDrafts.StarRating) && withDrafts.StarRating > 0, "Rework returned an invalid star rating");
    Assert(Math.Abs(withDrafts.StarRating - withoutDrafts.StarRating) < 1e-12, "draft notes changed the Rework result");
    Assert(withDrafts.ReferenceDifficulty == withoutDrafts.ReferenceDifficulty,
        "draft notes changed the Rework reference grade");
    Assert(withDrafts.Peaks.Count > 0 && withDrafts.Peaks.All(peak => peak.Beat.Value >= 0), "Rework did not expose navigable peak sections");
    Assert(withDrafts.Patterns.Count > 0 &&
           Math.Abs(withDrafts.Patterns.Sum(pattern => pattern.Ratio) - 1) < 1e-9 &&
           !string.IsNullOrWhiteSpace(withDrafts.PatternFamily),
        "Rework did not expose a normalized pattern breakdown");
    var faster = ReworkDifficultyAnalyzer.Analyze(withoutDraftsChart, 1.2);
    Assert(faster.StarRating > withoutDrafts.StarRating, "higher playback rate did not increase Rework difficulty");
    var odZero = ReworkDifficultyAnalyzer.Analyze(withoutDraftsChart, overallDifficulty: 0);
    var odTen = ReworkDifficultyAnalyzer.Analyze(withoutDraftsChart, overallDifficulty: 10);
    var odClamped = ReworkDifficultyAnalyzer.Analyze(withoutDraftsChart, overallDifficulty: 25);
    Assert(double.IsFinite(odZero.StarRating) && double.IsFinite(odTen.StarRating) &&
           odZero.OverallDifficulty == 0 && odTen.OverallDifficulty == 10 && odClamped.OverallDifficulty == 10,
        "Rework OD slider endpoints were not accepted");
}

static void TestReworkRejectsNon4K()
{
    var chart = new ChartDocument
    {
        KeyCount = 7,
        Notes =
        [
            new ChartNote { Lane = 0, Beat = BeatPosition.Zero },
            new ChartNote { Lane = 1, Beat = BeatPosition.FromDouble(1) }
        ]
    };
    try
    {
        ReworkDifficultyAnalyzer.Analyze(chart);
        Assert(false, "non-4K chart was accepted by Rework");
    }
    catch (NotSupportedException) { }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException(message);
}

#if LUMACHART_SERVER
internal sealed class SpecWebHostEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "LumaChart.Specs";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
#endif

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
    public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
}

internal sealed class SpecTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private DateTimeOffset utcNow = initialUtcNow;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
}

internal sealed class ResponseHeadersReadProbeContent(Stream source) : HttpContent
{
    public bool SerializeAttempted { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        SerializeAttempted = true;
        return Task.FromException(new InvalidOperationException(
            "The response body was buffered before the caller requested its stream."));
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        Task.FromResult(source);

    protected override Task<Stream> CreateContentReadStreamAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(source);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

internal sealed class ShortReadStream(byte[] content, int maximumReadBytes) : Stream
{
    private readonly MemoryStream inner = new(content, writable: false);
    private readonly int maximumReadBytes = maximumReadBytes > 0
        ? maximumReadBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumReadBytes));

    public bool IsDisposed { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, Math.Min(count, maximumReadBytes));

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumReadBytes)], cancellationToken);

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class TrackingWriteStream : MemoryStream
{
    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) IsDisposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class PrefixThenTerminalReadStream : Stream
{
    private readonly MemoryStream prefix;
    private readonly int maximumReadBytes;
    private readonly bool blockAfterPrefix;
    private readonly Exception? terminalException;
    private readonly TaskCompletionSource<bool> started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool terminalExceptionThrown;

    public PrefixThenTerminalReadStream(
        byte[] prefix,
        int maxChunkBytes,
        bool blockAfterPrefix = false,
        Exception? terminalException = null)
    {
        this.prefix = new MemoryStream(prefix, writable: false);
        maximumReadBytes = maxChunkBytes > 0
            ? maxChunkBytes
            : throw new ArgumentOutOfRangeException(nameof(maxChunkBytes));
        this.blockAfterPrefix = blockAfterPrefix;
        this.terminalException = terminalException;
    }

    public Task Started => started.Task;
    public bool IsDisposed { get; private set; }
    public void Release() => released.TrySetResult(true);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => prefix.Length;
    public override long Position
    {
        get => prefix.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask()
            .GetAwaiter().GetResult();

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (prefix.Position < prefix.Length)
            return await prefix.ReadAsync(
                buffer[..Math.Min(buffer.Length, maximumReadBytes)], cancellationToken);

        if (terminalException != null && !terminalExceptionThrown)
        {
            terminalExceptionThrown = true;
            throw terminalException;
        }

        if (!blockAfterPrefix) return 0;
        started.TrySetResult(true);
        await released.Task.WaitAsync(cancellationToken);
        return 0;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
            Release();
            prefix.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class BlockingReadStream(byte[] content) : Stream
{
    private readonly MemoryStream inner = new(content, writable: false);
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => started.Task;
    public bool IsDisposed { get; private set; }
    public void Release() => released.TrySetResult(true);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        started.TrySetResult(true);
        await released.Task.WaitAsync(cancellationToken);
        return await inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class SpecHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> responses = new();

    public List<CapturedSpecHttpRequest> Requests { get; } = [];

    public void Enqueue(HttpResponseMessage response) => responses.Enqueue(response);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (responses.Count == 0)
            throw new InvalidOperationException("The fake HTTP response queue is empty.");
        var bodyBytes = request.Content == null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var body = bodyBytes == null ? null : Encoding.UTF8.GetString(bodyBytes);
        var headers = request.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var contentHeaders = request.Content?.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase) ??
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        Requests.Add(new CapturedSpecHttpRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            body,
            bodyBytes,
            headers,
            contentHeaders));
        return responses.Dequeue();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            while (responses.TryDequeue(out var response)) response.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed record CapturedSpecHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string? Body,
    byte[]? BodyBytes,
    IReadOnlyDictionary<string, string[]> Headers,
    IReadOnlyDictionary<string, string[]> ContentHeaders)
{
    public bool HasHeader(string name, string expected) =>
        Headers.TryGetValue(name, out var values) && values.Contains(expected, StringComparer.Ordinal);

    public bool HasContentHeader(string name, string expected) =>
        ContentHeaders.TryGetValue(name, out var values) && values.Contains(expected, StringComparer.Ordinal);
}

internal sealed class FakeSessionTokenStore : ISessionTokenStore
{
    public string? Token { get; set; }
    public Exception? LoadException { get; init; }
    public Exception? SaveException { get; init; }
    public Exception? DeleteException { get; init; }
    public int LoadCalls { get; private set; }
    public int SaveCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadCalls++;
        return LoadException == null
            ? ValueTask.FromResult(Token)
            : ValueTask.FromException<string?>(LoadException);
    }

    public ValueTask SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCalls++;
        if (SaveException != null) return ValueTask.FromException(SaveException);
        Token = token;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCalls++;
        if (DeleteException != null) return ValueTask.FromException(DeleteException);
        Token = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class BlockingSessionTokenStore : ISessionTokenStore
{
    private readonly TaskCompletionSource<bool> releaseSave =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> SaveStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Operations { get; } = [];
    public string? Token { get; private set; }

    public ValueTask<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("load");
        return ValueTask.FromResult(Token);
    }

    public async ValueTask SaveAsync(string token, CancellationToken cancellationToken = default)
    {
        Operations.Add("save");
        SaveStarted.TrySetResult(true);
        await releaseSave.Task.WaitAsync(cancellationToken);
        Token = token;
    }

    public ValueTask DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Operations.Add("delete");
        Token = null;
        return ValueTask.CompletedTask;
    }

    public void ReleaseSave() => releaseSave.TrySetResult(true);
}

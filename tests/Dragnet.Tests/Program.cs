using Data.Models.Client;
using Dragnet;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;
using Dragnet.Web;
using Microsoft.Extensions.Logging;
using SharedLibraryCore.Alerts;
using SharedLibraryCore.Interfaces;
using System.IO.Compression;
using System.Text.Json;

var tests = new (string Name, Func<Task> Test)[]
{
    ("identity service updates configured display name without rotating keys", TestIdentityRenamesWithoutRotatingKeys),
    ("trust service persists and evaluates auto-approval", TestTrustServicePersistsAsync),
    ("review service denies pending ban and blocks untrusted approval", TestReviewTransitionsAsync),
    ("bulk review approves trusted selections with individual audits", TestBulkReviewAsync),
    ("peer store tracks bootstrap, errors, removal, and send cursor", TestPeerStoreAsync),
    ("diagnostics summarize telemetry without exposing secrets", TestDiagnosticsSanitizationAsync),
    ("peer gossip selection rotates fairly and persists", TestPeerGossipRotationAsync),
    ("statistics aggregate participating servers and shared bans", TestStatisticsAsync),
    ("onboarding verifies public health and readiness", TestOnboardingReadinessAsync),
    ("directory lists only opted-in healthy networks", TestDirectoryListingsAsync),
    ("network profiles summarize trust review health and coverage", TestNetworkProfileAsync),
    ("notification inbox persists deduplicates and acknowledges per administrator", TestNotificationInboxAsync),
    ("notification webhook completes delivery for local events", TestNotificationWebhookAsync),
    ("heartbeat peer proofs reject tampering", TestHeartbeatPeerProofValidationAsync),
    ("peer capability negotiation preserves legacy identity signatures", TestLegacyPeerSignatureCompatibility),
    ("event store expires elapsed temp bans", TestEventStoreExpiresElapsedTempBansAsync),
    ("import service skips disabled and already imported events", TestImportServiceSkipsAsync),
    ("import service queues unknown players", TestImportServiceQueuesUnknownPlayersAsync),
    ("heartbeat response sends approved events once", TestHeartbeatResponseBatchAsync),
    ("delivery acknowledgements replay gaps and support resync", TestDeliveryAcknowledgementReplayAsync),
    ("signed evidence updates propagate only from the ban origin", TestEvidenceUpdatesAsync),
    ("signed ban attestations propagate and update coverage", TestBanAttestationsAsync),
    ("ledger attestations backfill existing approved bans", TestAttestationBackfillAsync),
    ("public ledger renders searchable ban coverage", TestPublicLedgerAsync),
    ("webfront dashboard interaction renders as navigation content", TestWebfrontDashboardRendersAsync),
    ("heartbeat validation rejects oversized and invalid requests", TestHeartbeatValidationAsync),
    ("outbound heartbeat errors include response body", TestOutboundHeartbeatErrorIncludesBodyAsync),
    ("outbound heartbeat uses numeric enum wire format", TestOutboundHeartbeatUsesNumericEnumWireFormatAsync),
    ("update service compares release versions", TestUpdateVersionComparisonAsync),
    ("update service reads GitHub release metadata", TestUpdateReleaseMetadataAsync),
    ("update service falls back to GitHub release feed", TestUpdateReleaseFeedFallbackAsync),
    ("update service refreshes stale dashboard loads once", TestUpdatePageLoadRefreshAsync),
    ("update service safely stages official releases and notifies administrators", TestAutomaticUpdateInstallAsync)
};

var failed = 0;
foreach (var (name, test) in tests)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {name}");
        Console.WriteLine(ex);
    }
}

if (failed > 0)
{
    Console.WriteLine($"{failed} test(s) failed.");
    return failed;
}

Console.WriteLine($"{tests.Length} test(s) passed.");
return 0;

static Task TestIdentityRenamesWithoutRotatingKeys()
{
    using var testDir = new SyncTestDirectory();
    var identityService = new DragnetIdentityService(testDir.Path);
    var first = identityService.LoadOrCreate("Old Name");
    var renamed = identityService.LoadOrCreate("New Name");

    Assert.Equal(first.OriginId, renamed.OriginId, "origin id should not change when display name changes");
    Assert.Equal(first.PublicKeyPem, renamed.PublicKeyPem, "public key should not change when display name changes");
    Assert.Equal("New Name", renamed.OriginName, "configured origin name should update stored display name");
    return Task.CompletedTask;
}

static async Task TestTrustServicePersistsAsync()
{
    var configuration = new DragnetConfiguration();
    var handler = new RecordingConfigurationHandler<DragnetConfiguration>();
    var trustService = new DragnetTrustService(configuration, handler);
    var envelope = CreateEnvelope(originId: "origin-1", eventType: DragnetEventType.BanCreated);

    Assert.False(trustService.Evaluate(envelope).IsTrusted, "origin should start untrusted");

    await trustService.TrustAsync("origin-1", "Origin One", autoApproveBans: true, autoApproveLifts: false, CancellationToken.None);

    var decision = trustService.Evaluate(envelope);
    Assert.True(decision.IsTrusted, "origin should be trusted");
    Assert.True(decision.AutoApprove, "ban events should auto-approve");
    Assert.Equal(1, handler.SetCount, "trust change should persist");

    Assert.True(await trustService.UntrustAsync("origin-1", CancellationToken.None), "untrust should remove origin");
    Assert.False(trustService.Evaluate(envelope).IsTrusted, "origin should be untrusted after removal");
    Assert.Equal(2, handler.SetCount, "untrust should persist");
}

static async Task TestReviewTransitionsAsync()
{
    await using var testDir = new TestDirectory();
    var store = new DragnetEventStore(testDir.Path);
    await store.LoadAsync(CancellationToken.None);
    var configuration = new DragnetConfiguration();
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        store,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(store, importService, trustService);
    var pendingBan = CreateEnvelope(originId: "origin-2", eventType: DragnetEventType.BanCreated) with
    {
        PlayerNetworkId = "not-a-network-id"
    };

    await store.UpsertAsync(new DragnetStoredEvent
    {
        Event = pendingBan,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);

    var approval = await reviewService.ApplyActionAsync(
        pendingBan.EventId,
        DragnetReviewAction.ApproveBan,
        null,
        "Reviewer",
        42,
        CancellationToken.None);
    Assert.False(approval.Success, "untrusted approval should fail");
    Assert.Contains("not trusted", approval.Message, "failure should explain trust gate");

    await trustService.TrustAsync(pendingBan.OriginId, pendingBan.OriginName, false, false, CancellationToken.None);
    approval = await reviewService.ApplyActionAsync(
        pendingBan.EventId,
        DragnetReviewAction.ApproveBan,
        null,
        "Reviewer",
        42,
        CancellationToken.None);
    Assert.True(approval.Success, "trusted approval should succeed even when import queues");
    Assert.Contains("Queued", approval.Message, "approval should explain queued import state");
    var stored = await store.GetAsync(pendingBan.EventId, CancellationToken.None);
    Assert.NotNull(stored, "approved queued event should remain stored");
    Assert.Equal(DragnetReviewState.ApprovedBan, stored!.ReviewState, "queued import should still record approval");
    Assert.Contains("Queued", stored.ImportError ?? "", "queued import should persist import error");
    Assert.Equal("Reviewer", stored.ReviewedByName, "reviewer name should be recorded");
    Assert.Equal(42, stored.ReviewedByClientId, "reviewer client id should be recorded");
    Assert.Equal(1, stored.AuditTrail.Count, "review action should append one audit entry");
    Assert.Equal(DragnetReviewState.PendingBan, stored.AuditTrail[0].PreviousState, "audit should record previous state");
    Assert.Equal(DragnetReviewState.ApprovedBan, stored.AuditTrail[0].NewState, "audit should record new state");

    var secondPendingBan = CreateEnvelope(originId: "origin-3", eventType: DragnetEventType.BanCreated) with
    {
        PlayerNetworkId = "not-a-network-id"
    };
    await store.UpsertAsync(new DragnetStoredEvent
    {
        Event = secondPendingBan,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);

    var denial = await reviewService.ApplyActionAsync(
        secondPendingBan.EventId,
        DragnetReviewAction.DenyBan,
        "local note",
        "Second Reviewer",
        84,
        CancellationToken.None);
    Assert.True(denial.Success, "deny should succeed without trust");

    stored = await store.GetAsync(secondPendingBan.EventId, CancellationToken.None);
    Assert.NotNull(stored, "stored event should exist");
    Assert.Equal(DragnetReviewState.DeniedBan, stored!.ReviewState, "event should be denied");
    Assert.Equal("local note", stored.LocalDecisionReason, "decision note should be stored");
    Assert.Equal("Second Reviewer", stored.ReviewedByName, "denial reviewer should be recorded");

    var ignored = await reviewService.ApplyActionAsync(
        secondPendingBan.EventId,
        DragnetReviewAction.IgnoreBan,
        "reconsider later",
        "Third Reviewer",
        126,
        CancellationToken.None);
    Assert.True(ignored.Success, "a denied ban should be changeable to ignored");

    stored = await store.GetAsync(secondPendingBan.EventId, CancellationToken.None);
    Assert.NotNull(stored, "reconsidered event should remain stored");
    Assert.Equal(DragnetReviewState.IgnoredBan, stored!.ReviewState, "event should become ignored");
    Assert.Equal(2, stored.AuditTrail.Count, "reconsideration should append an audit entry");
    Assert.Equal(DragnetReviewState.DeniedBan, stored.AuditTrail[1].PreviousState,
        "reconsideration audit should retain the prior decision");
    Assert.Equal(DragnetReviewState.IgnoredBan, stored.AuditTrail[1].NewState,
        "reconsideration audit should record the new decision");

    await trustService.TrustAsync(
        secondPendingBan.OriginId,
        secondPendingBan.OriginName,
        false,
        false,
        CancellationToken.None);
    var reconsideredApproval = await reviewService.ApplyActionAsync(
        secondPendingBan.EventId,
        DragnetReviewAction.ApproveBan,
        "evidence reviewed",
        "Final Reviewer",
        168,
        CancellationToken.None);
    Assert.True(reconsideredApproval.Success, "an ignored ban should be changeable to approved");

    stored = await store.GetAsync(secondPendingBan.EventId, CancellationToken.None);
    Assert.NotNull(stored, "approved reconsidered event should remain stored");
    Assert.Equal(DragnetReviewState.ApprovedBan, stored!.ReviewState,
        "reconsidered event should become approved");
    Assert.Equal(3, stored.AuditTrail.Count, "approval after reconsideration should append an audit entry");

    var reverseApproval = await reviewService.ApplyActionAsync(
        secondPendingBan.EventId,
        DragnetReviewAction.DenyBan,
        "attempted reversal",
        "Final Reviewer",
        168,
        CancellationToken.None);
    Assert.False(reverseApproval.Success, "approved events should remain terminal after possible import");
    Assert.Contains("cannot be changed", reverseApproval.Message,
        "terminal approval failure should explain that the decision cannot be changed");
}

static async Task TestBulkReviewAsync()
{
    await using var testDir = new TestDirectory();
    var store = new DragnetEventStore(testDir.Path);
    await store.LoadAsync(CancellationToken.None);
    var configuration = new DragnetConfiguration();
    var trustService = new DragnetTrustService(
        configuration,
        new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        store,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(store, importService, trustService);
    var trustedOne = CreateEnvelope("trusted-origin", DragnetEventType.BanCreated) with
    {
        EventId = "bulk-trusted-one",
        PlayerNetworkId = "unknown-one"
    };
    var trustedTwo = CreateEnvelope("trusted-origin", DragnetEventType.BanCreated) with
    {
        EventId = "bulk-trusted-two",
        PlayerNetworkId = "unknown-two"
    };
    var untrusted = CreateEnvelope("untrusted-origin", DragnetEventType.BanCreated) with
    {
        EventId = "bulk-untrusted",
        PlayerNetworkId = "unknown-three"
    };
    foreach (var envelope in new[] { trustedOne, trustedTwo, untrusted })
    {
        await store.UpsertAsync(new DragnetStoredEvent
        {
            Event = envelope,
            ReviewState = DragnetReviewState.PendingBan
        }, CancellationToken.None);
    }

    await trustService.TrustAsync(
        trustedOne.OriginId,
        trustedOne.OriginName,
        false,
        false,
        CancellationToken.None);
    var result = await reviewService.ApplyBulkActionAsync(
        [trustedOne.EventId, trustedTwo.EventId, trustedOne.EventId, untrusted.EventId],
        DragnetReviewAction.ApproveBan,
        "Bulk approval",
        "Bulk Reviewer",
        99,
        CancellationToken.None);

    Assert.True(result.Success, "valid bulk request should complete even when individual items fail");
    Assert.Equal(2, result.SucceededCount, "trusted unique selections should approve");
    Assert.Equal(1, result.FailedCount, "untrusted selection should be reported as failed");
    Assert.Contains("not trusted", result.Failures.Single(), "bulk failure should preserve the trust reason");
    foreach (var eventId in new[] { trustedOne.EventId, trustedTwo.EventId })
    {
        var stored = await store.GetAsync(eventId, CancellationToken.None);
        Assert.Equal(DragnetReviewState.ApprovedBan, stored!.ReviewState,
            "trusted bulk selection should be approved");
        Assert.Equal("Bulk Reviewer", stored.ReviewedByName,
            "bulk approval should preserve reviewer identity");
        Assert.Equal(1, stored.AuditTrail.Count,
            "each bulk-approved ban should receive one audit entry");
    }

    var untrustedStored = await store.GetAsync(untrusted.EventId, CancellationToken.None);
    Assert.Equal(DragnetReviewState.PendingBan, untrustedStored!.ReviewState,
        "failed bulk selection should remain pending");
    Assert.Equal(0, untrustedStored.AuditTrail.Count,
        "failed bulk selection should not create an audit transition");

    result = await reviewService.ApplyBulkActionAsync(
        Enumerable.Range(0, 101).Select(index => $"missing-{index}").ToList(),
        DragnetReviewAction.ApproveBan,
        null,
        "Bulk Reviewer",
        99,
        CancellationToken.None);
    Assert.False(result.Success, "bulk review should reject more than 100 unique events");
    Assert.Contains("at most 100", result.Message, "bulk limit failure should be explicit");
}

static async Task TestPeerStoreAsync()
{
    await using var testDir = new TestDirectory();
    var store = new DragnetPeerStore(testDir.Path);
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet",
        BootstrapPeers =
        [
            new DragnetPeerConfiguration
            {
                Endpoint = "https://bootstrap.example/dragnet",
                ExpectedOriginId = "bootstrap-origin"
            },
            new DragnetPeerConfiguration
            {
                Endpoint = "https://local.example/dragnet",
                ExpectedOriginId = "local-self"
            }
        ]
    };

    await store.LoadAsync(configuration, CancellationToken.None);
    var peers = await store.ListAsync(CancellationToken.None);
    var bootstrap = peers.Single(peer => peer.OriginId == "bootstrap-origin");
    Assert.True(bootstrap.IsBootstrap, "configured peer should be marked bootstrap");
    Assert.False(peers.Any(peer => peer.Endpoint == "https://local.example/dragnet"),
        "configured local endpoint should not be added as a peer");

    await store.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = "discovered-origin",
        OriginName = "Discovered",
        PublicEndpoint = "https://discovered.example/dragnet",
        ServerCount = 3
    }, CancellationToken.None);
    await store.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = "https://discovered.example/dragnet",
        OriginName = "https://discovered.example/dragnet",
        PublicEndpoint = "https://discovered.example/dragnet"
    }, CancellationToken.None);
    Assert.Equal(
        1,
        (await store.ListAsync(CancellationToken.None)).Count(peer =>
            peer.Endpoint == "https://discovered.example/dragnet"),
        "provisional gossip identity should not duplicate a canonical endpoint");
    await store.AddManualPeerAsync("https://manual.example/dragnet", null, CancellationToken.None);
    await store.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = "local-self",
        OriginName = "Local",
        PublicEndpoint = "https://local.example/dragnet"
    }, CancellationToken.None);
    await store.MarkErrorAsync("discovered-origin", "boom", CancellationToken.None);
    await store.ClearErrorAsync("discovered-origin", CancellationToken.None);

    var manual = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.Endpoint == "https://manual.example/dragnet");
    Assert.False(manual.IsBootstrap, "manually added peer should not be marked bootstrap");
    Assert.Equal("https://manual.example/dragnet", manual.OriginId, "manual peer should use endpoint as provisional origin id");

    await store.LoadAsync(configuration, CancellationToken.None);
    Assert.False((await store.ListAsync(CancellationToken.None)).Any(peer => peer.Endpoint == "https://local.example/dragnet"),
        "stored local endpoint should be pruned on load");

    var discovered = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "discovered-origin");
    Assert.Null(discovered.LastError, "clear error should remove error");

    await store.MarkErrorAsync("discovered-origin", "temporary", CancellationToken.None, failureThreshold: 3);
    await store.MarkErrorAsync("discovered-origin", "temporary", CancellationToken.None, failureThreshold: 3);
    discovered = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "discovered-origin");
    Assert.Null(discovered.LastError, "transient failures below threshold should not show an error");
    Assert.Equal(2, discovered.ConsecutiveFailures, "transient failure count should be retained");

    await store.MarkErrorAsync("discovered-origin", "sustained", CancellationToken.None, failureThreshold: 3);
    discovered = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "discovered-origin");
    Assert.Equal("sustained", discovered.LastError, "threshold failure should become visible");

    discovered.FirstFailureAtUtc = DateTimeOffset.UtcNow.AddHours(-1);
    await store.MarkErrorAsync(
        "discovered-origin",
        "still unavailable",
        CancellationToken.None,
        failureThreshold: 3,
        quarantineAfter: TimeSpan.FromMinutes(30));
    discovered = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "discovered-origin");
    Assert.True(discovered.QuarantinedAtUtc is not null,
        "sustained failures should quarantine a peer");
    Assert.False((await store.SelectForGossipAsync(
            10,
            TimeSpan.FromHours(2),
            null,
            null,
            CancellationToken.None))
        .Any(peer => peer.OriginId == "discovered-origin"),
        "quarantined peers should not be advertised through gossip");
    var firstRecoveryTargets = await store.SelectHeartbeatTargetsAsync(
        TimeSpan.FromMinutes(10),
        CancellationToken.None);
    Assert.True(firstRecoveryTargets.Any(peer => peer.OriginId == "discovered-origin"),
        "a quarantined peer should receive a recovery probe");
    var immediateRecoveryTargets = await store.SelectHeartbeatTargetsAsync(
        TimeSpan.FromMinutes(10),
        CancellationToken.None);
    Assert.False(immediateRecoveryTargets.Any(peer => peer.OriginId == "discovered-origin"),
        "quarantined peers should not be probed every heartbeat");
    Assert.True(immediateRecoveryTargets.Any(peer => peer.OriginId == "bootstrap-origin"),
        "a failed peer should not prevent another bootstrap or discovered peer from being contacted");

    bootstrap = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "bootstrap-origin");
    bootstrap.FirstFailureAtUtc = DateTimeOffset.UtcNow.AddHours(-1);
    await store.MarkErrorAsync(
        "bootstrap-origin",
        "bootstrap unavailable",
        CancellationToken.None,
        quarantineAfter: TimeSpan.FromMinutes(30));
    await store.LoadAsync(configuration, CancellationToken.None);
    bootstrap = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "bootstrap-origin");
    Assert.True(bootstrap.IsBootstrap, "configured bootstrap role should survive reload");
    Assert.True(bootstrap.QuarantinedAtUtc is not null,
        "reloading configured bootstrap peers should preserve quarantine state");

    await store.MarkHeartbeatSucceededAsync("discovered-origin", new DragnetPeerInfo
    {
        OriginId = "canonical-origin",
        OriginName = "Canonical",
        PublicEndpoint = "https://discovered.example/dragnet",
        ServerCount = 4
    }, CancellationToken.None, latencyMs: 125);
    var reconciledPeers = await store.ListAsync(CancellationToken.None);
    Assert.False(reconciledPeers.Any(peer => peer.OriginId == "discovered-origin"),
        "successful heartbeat should remove the provisional identity");
    var canonical = reconciledPeers.Single(peer => peer.OriginId == "canonical-origin");
    Assert.Null(canonical.LastError, "successful heartbeat should clear visible errors");
    Assert.Equal(0, canonical.ConsecutiveFailures, "successful heartbeat should reset failure count");
    Assert.Null(canonical.QuarantinedAtUtc, "successful heartbeat should restore a quarantined peer");
    Assert.Null(canonical.LastRecoveryProbeAtUtc, "successful heartbeat should clear recovery probe state");
    Assert.Equal(4, canonical.ServerCount, "successful heartbeat should retain advertised server count");
    Assert.Equal(1L, canonical.HeartbeatSuccessCount, "successful heartbeat should increment success telemetry");
    Assert.True(canonical.HeartbeatFailureCount >= 5,
        "failed heartbeat attempts should remain in cumulative telemetry");
    Assert.Equal(125d, canonical.LastHeartbeatLatencyMs,
        "successful heartbeat should persist measured latency");
    Assert.True(canonical.TelemetryEvents.Any(item =>
            item.Type == DragnetPeerTelemetryEventType.Recovered),
        "successful probe after failures should record a recovery transition");

    await store.LoadAsync(configuration, CancellationToken.None);
    canonical = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "canonical-origin");
    Assert.Equal(125d, canonical.LastHeartbeatLatencyMs,
        "peer latency telemetry should survive restart");

    var sentEvent = CreateEnvelope(originId: "local", eventType: DragnetEventType.BanCreated) with
    {
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
    };
    await store.MarkEventBatchSentAsync("canonical-origin", [sentEvent], CancellationToken.None);
    discovered = (await store.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "canonical-origin");
    Assert.Equal(sentEvent.CreatedAtUtc, discovered.LastEventSentAtUtc, "send cursor should advance");

    Assert.True(await store.RemoveAsync("canonical-origin", CancellationToken.None), "discovered peer should be removable");
    Assert.False((await store.ListAsync(CancellationToken.None)).Any(peer => peer.OriginId == "canonical-origin"),
        "removed peer should not remain");
}

static Task TestDiagnosticsSanitizationAsync()
{
    var now = DateTimeOffset.UtcNow;
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet",
        NotificationWebhookUrl = "https://discord.example/secret-webhook",
        BootstrapPeers =
        [
            new DragnetPeerConfiguration
            {
                Endpoint = "https://bootstrap.example/dragnet",
                ExpectedOriginId = "expected-secret-origin"
            }
        ],
        TrustedOrigins =
        [
            new DragnetTrustConfiguration
            {
                OriginId = "trusted-secret-origin",
                DisplayName = "Trusted"
            }
        ]
    };
    var peer = new DragnetPeerRecord
    {
        OriginId = "peer-origin",
        OriginName = "Peer Network",
        Endpoint = "https://peer.example/dragnet",
        Version = "0.1.0-beta.24",
        LastSeenUtc = now,
        HeartbeatAttemptCount = 10,
        HeartbeatSuccessCount = 8,
        HeartbeatFailureCount = 2,
        LastHeartbeatLatencyMs = 180,
        AverageHeartbeatLatencyMs = 150,
        LastHeartbeatSucceededAtUtc = now,
        TelemetryEvents =
        [
            new DragnetPeerTelemetryEvent
            {
                Type = DragnetPeerTelemetryEventType.Recovered,
                OccurredAtUtc = now,
                Detail = "Heartbeat communication recovered.",
                LatencyMs = 180
            }
        ]
    };
    var report = DragnetDiagnosticsService.Create(
        configuration,
        [peer],
        [],
        DragnetUpdateStatus.Initial with
        {
            CurrentVersion = DragnetBuildInfo.Version,
            IsChecking = false
        },
        now);
    var json = JsonSerializer.Serialize(report);

    Assert.Equal(1, report.ActivePeerCount, "diagnostics should count active peers");
    Assert.Equal(80d, report.Peers.Single().HeartbeatSuccessRate,
        "diagnostics should calculate heartbeat success rate");
    Assert.Equal(1, report.Configuration.BootstrapPeerCount,
        "diagnostics should expose bootstrap count without endpoint details");
    Assert.Equal(1, report.Configuration.TrustedOriginCount,
        "diagnostics should expose trust count without trust identities");
    Assert.False(json.Contains("secret-webhook", StringComparison.Ordinal),
        "diagnostics must not expose webhook URLs");
    Assert.False(json.Contains("expected-secret-origin", StringComparison.Ordinal),
        "diagnostics must not expose configured bootstrap identity pins");
    Assert.False(json.Contains("trusted-secret-origin", StringComparison.Ordinal),
        "diagnostics must not expose trusted origin identities");
    return Task.CompletedTask;
}

static async Task TestStatisticsAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration();
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    await peerStore.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = "remote",
        OriginName = "Remote",
        PublicEndpoint = "https://remote.example/dragnet",
        ServerCount = 3
    }, CancellationToken.None);
    await peerStore.AddManualPeerAsync(
        "https://remote.example/dragnet",
        "https://remote.example/dragnet",
        CancellationToken.None);
    await peerStore.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = "quarantined",
        OriginName = "Quarantined",
        PublicEndpoint = "https://quarantined.example/dragnet",
        ServerCount = 9
    }, CancellationToken.None);
    await peerStore.MarkErrorAsync("quarantined", "offline", CancellationToken.None);
    var quarantined = (await peerStore.ListAsync(CancellationToken.None))
        .Single(peer => peer.OriginId == "quarantined");
    quarantined.FirstFailureAtUtc = DateTimeOffset.UtcNow.AddHours(-1);
    await peerStore.MarkErrorAsync(
        "quarantined",
        "offline",
        CancellationToken.None,
        quarantineAfter: TimeSpan.FromMinutes(30));
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = CreateEnvelope("local", DragnetEventType.BanCreated),
        ReviewState = DragnetReviewState.ApprovedBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = CreateEnvelope("remote", DragnetEventType.BanCreated),
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = CreateEnvelope("remote", DragnetEventType.BanLifted),
        ReviewState = DragnetReviewState.PendingLift
    }, CancellationToken.None);

    var service = new DragnetStatisticsService(eventStore, peerStore, () => 5, configuration);
    var statistics = await service.GetAsync(CancellationToken.None);

    Assert.Equal(8, statistics.ParticipatingServerCount, "statistics should deduplicate server counts by endpoint");
    Assert.Equal(2, statistics.ParticipatingNodeCount, "statistics should include local and peer nodes");
    Assert.Equal(2, statistics.SharedBanCount, "statistics should count unique ban-created events");
}

static async Task TestPeerGossipRotationAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PeerStaleAfter = TimeSpan.FromMinutes(10)
    };
    var store = new DragnetPeerStore(testDir.Path);
    await store.LoadAsync(configuration, CancellationToken.None);

    for (var index = 1; index <= 6; index++)
    {
        await store.UpsertAsync(new DragnetPeerInfo
        {
            OriginId = $"peer-{index}",
            OriginName = $"Peer {index}",
            PublicEndpoint = $"https://peer-{index}.example/dragnet",
            SeenAtUtc = index == 6
                ? DateTimeOffset.UtcNow.AddMinutes(-20)
                : DateTimeOffset.UtcNow
        }, CancellationToken.None, identityVerified: index is 1 or 6);
    }

    await store.MarkErrorAsync("peer-5", "offline", CancellationToken.None);

    var first = await store.SelectForGossipAsync(
        2,
        configuration.PeerStaleAfter,
        null,
        null,
        CancellationToken.None);
    Assert.Equal(2, first.Count, "first gossip batch should honor maximum");
    Assert.True(first.Any(peer => peer.OriginId == "peer-1"),
        "verified peer should win the tie among never-advertised eligible peers");
    Assert.False(first.Any(peer => peer.OriginId is "peer-5" or "peer-6"),
        "errored and stale peers should be excluded");

    var second = await store.SelectForGossipAsync(
        2,
        configuration.PeerStaleAfter,
        null,
        null,
        CancellationToken.None);
    Assert.True(second.Any(peer => peer.OriginId is "peer-3" or "peer-4"),
        "never-advertised eligible peers should rotate into the next batch");
    Assert.False(first.Select(peer => peer.OriginId).Intersect(second.Select(peer => peer.OriginId)).Any(),
        "eligible peers should not repeat before never-advertised peers receive exposure");

    var excluded = await store.SelectForGossipAsync(
        4,
        configuration.PeerStaleAfter,
        "peer-1",
        "https://peer-1.example/dragnet",
        CancellationToken.None);
    Assert.False(excluded.Any(peer => peer.OriginId == "peer-1"),
        "heartbeat counterpart should not be advertised back to itself");

    await store.LoadAsync(configuration, CancellationToken.None);
    var persisted = await store.ListAsync(CancellationToken.None);
    Assert.True(persisted
            .Where(peer => peer.OriginId is "peer-1" or "peer-2" or "peer-3" or "peer-4")
            .All(peer => peer.LastAdvertisedAtUtc is not null),
        "advertisement timestamps should survive restart");
}

static async Task TestOnboardingReadinessAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        OriginName = "Test Network",
        PublicEndpoint = "https://local.example/dragnet",
        UpdateCheckEnabled = false
    };
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate(configuration.OriginName);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    using var updateService = new DragnetUpdateService(
        configuration,
        new TestLogger<DragnetUpdateService>(),
        new HttpClient(new StaticResponseHandler(System.Net.HttpStatusCode.OK, "{}")));
    using var healthClient = new HttpClient(new StaticResponseHandler(
        System.Net.HttpStatusCode.OK,
        CreateSignedHealthResponse(identityService, identity, configuration.PublicEndpoint!)));
    var onboarding = new DragnetOnboardingService(
        configuration,
        identity,
        identityService,
        peerStore,
        updateService,
        healthClient);

    var status = await onboarding.GetStatusAsync(CancellationToken.None);
    Assert.True(status.IdentityConfigured, "named identity should pass onboarding");
    Assert.True(status.EndpointConfigured, "absolute endpoint should pass configuration check");
    Assert.True(status.EndpointUsesHttps, "https endpoint should pass transport check");
    Assert.True(status.EndpointReachable, "successful health route should pass reachability check");
    Assert.True(status.EndpointIdentityMatched, "matching fingerprint should pass identity check");
    Assert.True(status.EndpointSignatureVerified, "signed health response should pass proof check");
    Assert.True(status.EndpointVerified, "matching public health identity should verify endpoint");
    Assert.False(status.PeerConnected, "no peer should not pass connectivity check");
}

static async Task TestDirectoryListingsAsync()
{
    await using var testDir = new TestDirectory();
    var defaults = DragnetConfiguration.CreateDefault();
    Assert.Equal(
        DragnetConfiguration.OfficialBootstrapEndpoint,
        defaults.BootstrapPeers.Single().Endpoint,
        "new installations should receive the official bootstrap endpoint");

    var configuration = new DragnetConfiguration
    {
        OriginName = "Local Network",
        PublicEndpoint = "https://local.example/dragnet",
        DirectoryListingEnabled = true,
        DirectoryRegion = "North America",
        DirectoryWebsite = "https://local.example"
    };
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate(configuration.OriginName);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var remoteIdentityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "remote-identity"));
    var remoteIdentity = remoteIdentityService.LoadOrCreate("Listed Peer");
    var listedPeer = CreateSignedPeerInfo(
        remoteIdentityService,
        remoteIdentity,
        "https://listed.example/dragnet",
        directoryListed: true);
    await peerStore.UpsertAsync(listedPeer, CancellationToken.None, identityVerified: true);
    await peerStore.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = "private-peer",
        OriginName = "Private Peer",
        PublicEndpoint = "https://private.example/dragnet",
        DirectoryListed = false,
        ServerCount = 2
    }, CancellationToken.None);

    var service = new DragnetDirectoryService(configuration, identity, peerStore, () => 4);
    var entries = await service.ListAsync(CancellationToken.None);

    Assert.Equal(2, entries.Count, "directory should contain local and opted-in remote networks only");
    var local = entries.Single(entry => entry.OriginId == identity.OriginId);
    Assert.Equal(4, local.ServerCount, "local listing should advertise monitored server count");
    Assert.Equal("North America", local.Region, "local listing should include configured region");
    var remote = entries.Single(entry => entry.OriginId == remoteIdentity.OriginId);
    Assert.Equal("Europe", remote.Region, "gossiped directory metadata should be retained");
    Assert.False(remote.Verified, "signed gossip alone should not verify endpoint ownership");
    Assert.False(entries.Any(entry => entry.OriginId == "private-peer"),
        "non-opted-in peers must not be exposed by the directory");

    await peerStore.MarkHeartbeatSucceededAsync(
        remoteIdentity.OriginId,
        listedPeer,
        CancellationToken.None,
        identityVerified: true);
    entries = await service.ListAsync(CancellationToken.None);
    remote = entries.Single(entry => entry.OriginId == remoteIdentity.OriginId);
    Assert.True(remote.Verified, "direct signed heartbeat should verify endpoint ownership");
    Assert.Equal("Direct signed heartbeat", remote.VerificationMethod,
        "verified listing should explain the verification method");
    await peerStore.UpsertAsync(listedPeer with
    {
        OriginName = "Unsigned Downgrade",
        PublicKeyPem = null,
        Signature = null
    }, CancellationToken.None);
    remote = (await service.ListAsync(CancellationToken.None))
        .Single(entry => entry.OriginId == remoteIdentity.OriginId);
    Assert.Equal("Listed Peer", remote.OriginName,
        "unsigned gossip must not overwrite previously verified metadata");

    await peerStore.MarkErrorAsync(remoteIdentity.OriginId, "offline", CancellationToken.None);
    entries = await service.ListAsync(CancellationToken.None);
    Assert.False(entries.Any(entry => entry.OriginId == remoteIdentity.OriginId),
        "errored peers should be omitted until a healthy heartbeat clears the error");
}

static async Task TestNetworkProfileAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local Network");
    var remoteIdentityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "remote"));
    var remoteIdentity = remoteIdentityService.LoadOrCreate("Profile Network");
    var peerInfo = CreateSignedPeerInfo(
        remoteIdentityService,
        remoteIdentity,
        "https://profile.example/dragnet",
        directoryListed: true) with
    {
        Version = "0.1.0-beta.12",
        SupportsDeliveryAcknowledgements = true,
        SupportsEvidenceUpdates = true,
        SupportsBanAttestations = true,
        SupportsAttestationRefreshRequests = true
    };
    await peerStore.UpsertAsync(peerInfo, CancellationToken.None, identityVerified: true);
    await peerStore.MarkHeartbeatSucceededAsync(
        remoteIdentity.OriginId,
        peerInfo,
        CancellationToken.None,
        identityVerified: true);
    configuration.TrustedOrigins.Add(new DragnetTrustConfiguration
    {
        OriginId = remoteIdentity.OriginId,
        DisplayName = remoteIdentity.OriginName,
        AutoApproveBans = true
    });

    var approved = CreateEnvelope(remoteIdentity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "profile-approved",
        OriginName = remoteIdentity.OriginName,
        Iw4mAdminPenaltyId = 101,
        PlayerName = "Approved Player",
        Reason = "Aimbot",
        EvidenceUrl = "https://youtu.be/profile"
    };
    var denied = CreateEnvelope(remoteIdentity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "profile-denied",
        OriginName = remoteIdentity.OriginName,
        Iw4mAdminPenaltyId = 102,
        PlayerName = "Denied Player"
    };
    var ignored = CreateEnvelope(remoteIdentity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "profile-ignored",
        OriginName = remoteIdentity.OriginName,
        Iw4mAdminPenaltyId = 103,
        PlayerName = "Ignored Player"
    };
    var pending = CreateEnvelope(remoteIdentity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "profile-pending",
        OriginName = remoteIdentity.OriginName,
        Iw4mAdminPenaltyId = 104,
        PlayerName = "Pending Player"
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = approved,
        ReviewState = DragnetReviewState.ApprovedBan,
        BanAttestations =
        [
            new DragnetBanAttestation
            {
                AttestationId = "profile-attestation",
                EventId = approved.EventId,
                NetworkOriginId = identity.OriginId,
                NetworkName = identity.OriginName,
                PublicEndpoint = configuration.PublicEndpoint,
                NetworkPublicKeyPem = identity.PublicKeyPem,
                ServerCount = 2,
                ServerNames = ["TDM", "Domination"],
                Status = DragnetBanCoverageStatus.Enforced,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Signature = "signature"
            }
        ]
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = denied,
        ReviewState = DragnetReviewState.DeniedBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = ignored,
        ReviewState = DragnetReviewState.IgnoredBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = pending,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = CreateEnvelope(remoteIdentity.OriginId, DragnetEventType.BanLifted) with
        {
            EventId = "profile-lift",
            OriginName = remoteIdentity.OriginName,
            Iw4mAdminPenaltyId = approved.Iw4mAdminPenaltyId,
            PlayerNetworkId = approved.PlayerNetworkId,
            CreatedAtUtc = approved.CreatedAtUtc.AddMinutes(1)
        },
        ReviewState = DragnetReviewState.ApprovedLift
    }, CancellationToken.None);

    var deliveryOne = CreateEnvelope(identity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "profile-delivery-one"
    };
    var deliveryTwo = CreateEnvelope(identity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "profile-delivery-two"
    };
    await peerStore.MarkEventBatchSentAsync(
        remoteIdentity.OriginId,
        [deliveryOne, deliveryTwo],
        CancellationToken.None,
        trackAcknowledgements: true);
    await peerStore.MarkEventsAcknowledgedAsync(
        remoteIdentity.OriginId,
        [deliveryOne.EventId],
        CancellationToken.None);
    await peerStore.MarkErrorAsync(
        remoteIdentity.OriginId,
        "private proxy response body",
        CancellationToken.None);

    var service = new DragnetNetworkProfileService(
        configuration,
        eventStore,
        peerStore,
        identity,
        () => 2);
    var profile = await service.GetAsync(remoteIdentity.OriginId, CancellationToken.None);
    Assert.NotNull(profile, "known peer should produce a network profile");
    Assert.Equal(4, profile!.SubmittedBanCount, "profile should count canonical submitted bans");
    Assert.Equal(1, profile.SubmittedLiftCount, "profile should count submitted lifts");
    Assert.Equal(3, profile.ActiveBanCount, "profile should exclude lifted bans from active totals");
    Assert.Equal(25, profile.EvidenceRatePercent, "profile should calculate evidence coverage");
    Assert.Equal(1, profile.ApprovedBanCount, "profile should count local approvals");
    Assert.Equal(1, profile.DeniedBanCount, "profile should count local denials");
    Assert.Equal(1, profile.IgnoredBanCount, "profile should count local ignores");
    Assert.Equal(1, profile.PendingBanCount, "profile should count pending reviews");
    Assert.Equal(33, profile.ApprovalRatePercent, "profile should calculate approval rate from reviewed bans");
    Assert.Equal(25, profile.EnforcementCoveragePercent,
        "profile should calculate enforcement across all eligible ban-network slots");
    Assert.Equal(50, profile.DeliveryAcknowledgementPercent,
        "profile should calculate acknowledged delivery reliability");
    Assert.True(profile.TrustedByThisNetwork, "profile should expose this node's trust policy");
    Assert.True(profile.AutoApproveBans, "profile should expose automatic ban approval policy");
    Assert.Equal("Errored", profile.Health, "profile should expose current transport health");

    var html = await service.RenderHtmlAsync(remoteIdentity.OriginId, CancellationToken.None);
    Assert.NotNull(html, "known peer profile should render HTML");
    Assert.Contains("Profile Network", html!, "profile should render the network identity");
    Assert.Contains("This instance's trust and review history", html!,
        "profile should label local review metrics");
    Assert.Contains("Approved Player", html!, "profile should list recent submitted bans");
    Assert.False(html!.Contains("/dragnet/ledger", StringComparison.OrdinalIgnoreCase),
        "network profile HTML should not link to removed standalone ledger routes");
    Assert.False(html!.Contains("private proxy response body", StringComparison.Ordinal),
        "public profile must not expose raw transport failure content");
    Assert.True(await service.GetAsync("unknown-network", CancellationToken.None) is null,
        "unknown networks should not produce profiles");
}

static async Task TestNotificationInboxAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        NotificationsEnabled = true,
        StalePendingReviewAfter = TimeSpan.FromHours(1),
        InGameNotificationSummariesEnabled = false
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var notificationPath = System.IO.Path.Combine(testDir.Path, "notifications");
    var notificationStore = new DragnetNotificationStore(notificationPath);
    await notificationStore.LoadAsync(CancellationToken.None);
    var service = new DragnetNotificationService(
        configuration,
        notificationStore,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetNotificationService>());
    var envelope = CreateEnvelope("notification-origin", DragnetEventType.BanCreated) with
    {
        EventId = "notification-ban",
        PlayerName = "Notification Player",
        Reason = "Wallhack"
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = envelope,
        ReviewState = DragnetReviewState.PendingBan,
        FirstSeenUtc = DateTimeOffset.UtcNow.AddHours(-2)
    }, CancellationToken.None);

    await service.NotifyNewEventAsync(envelope, CancellationToken.None);
    await service.NotifyNewEventAsync(envelope, CancellationToken.None);
    await service.SyncStaleReviewsAsync(CancellationToken.None);
    await service.SyncStaleReviewsAsync(CancellationToken.None);

    var adminOne = await service.ListForClientAsync(101, CancellationToken.None);
    Assert.Equal(2, adminOne.Count, "new and stale alerts should be stored once each");
    Assert.Equal(1, adminOne.Count(item => item.Type is DragnetNotificationType.NewBan),
        "duplicate event ingestion should not duplicate new-ban notifications");
    Assert.Equal(1, adminOne.Count(item => item.Type is DragnetNotificationType.StaleReview),
        "repeated stale scans should not duplicate stale notifications");

    var newBan = adminOne.Single(item => item.Type is DragnetNotificationType.NewBan);
    Assert.True(await service.AcknowledgeAsync(newBan.NotificationId, 101, CancellationToken.None),
        "administrator should be able to acknowledge one notification");
    Assert.Equal(1, (await service.ListForClientAsync(101, CancellationToken.None)).Count,
        "acknowledged notification should disappear for that administrator");
    Assert.Equal(2, (await service.ListForClientAsync(202, CancellationToken.None)).Count,
        "one administrator's acknowledgement must not hide another administrator's alerts");
    Assert.Equal(1, await service.AcknowledgeAllAsync(101, CancellationToken.None),
        "acknowledge all should affect only remaining unread notifications");
    Assert.Equal(0, (await service.ListForClientAsync(101, CancellationToken.None)).Count,
        "administrator should have no unread alerts after acknowledging all");

    var reloadedStore = new DragnetNotificationStore(notificationPath);
    await reloadedStore.LoadAsync(CancellationToken.None);
    var reloadedService = new DragnetNotificationService(
        configuration,
        reloadedStore,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetNotificationService>());
    Assert.Equal(0, (await reloadedService.ListForClientAsync(101, CancellationToken.None)).Count,
        "acknowledgements should survive a plugin restart");
    Assert.Equal(2, (await reloadedService.ListForClientAsync(202, CancellationToken.None)).Count,
        "unread alerts for other administrators should survive a plugin restart");
}

static async Task TestNotificationWebhookAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        NotificationsEnabled = true,
        NotificationWebhookUrl = "https://discord.example.test/webhook"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var notificationStore = new DragnetNotificationStore(System.IO.Path.Combine(testDir.Path, "notifications"));
    await notificationStore.LoadAsync(CancellationToken.None);
    var handler = new StaticResponseHandler(System.Net.HttpStatusCode.NoContent, "");
    using var service = new DragnetNotificationService(
        configuration,
        notificationStore,
        eventStore,
        () => null!,
        new TestLogger<DragnetNotificationService>(),
        httpClient: new HttpClient(handler));
    var envelope = CreateEnvelope("local-webhook", DragnetEventType.BanCreated) with
    {
        PlayerName = "Webhook Player",
        Reason = "Webhook reason"
    };

    await service.NotifyNewEventAsync(envelope, CancellationToken.None);

    Assert.Equal(1, handler.RequestCount,
        "notification creation should complete one webhook request before returning");
    var webhookBody = handler.LastRequestBody ?? "";
    Assert.Contains("\"embeds\"", webhookBody,
        "Discord webhook should use a structured embed");
    using var webhookDocument = JsonDocument.Parse(webhookBody);
    var webhookFields = webhookDocument.RootElement
        .GetProperty("embeds")[0]
        .GetProperty("fields")
        .EnumerateArray()
        .ToList();
    Assert.True(webhookFields.Any(field => field.GetProperty("name").GetString() == "ᴘʟᴀʏᴇʀ"),
        "Discord embed should use small-caps player column headers");
    Assert.True(webhookFields.Any(field => field.GetProperty("name").GetString() == "ɴᴇᴛᴡᴏʀᴋ"),
        "Discord embed should use small-caps network column headers");
    Assert.True(webhookFields.Any(field =>
            field.GetProperty("name").GetString() == "ᴘʟᴀᴛꜰᴏʀᴍ" &&
            field.GetProperty("value").GetString() == envelope.PlayerGame),
        "Discord embed should identify the platform the ban came from");
    Assert.True(webhookFields.Count(field => field.GetProperty("inline").GetBoolean()) >= 3,
        "Discord embed should arrange summary fields in columns");
    Assert.Equal(0, webhookDocument.RootElement
            .GetProperty("allowed_mentions")
            .GetProperty("parse")
            .GetArrayLength(),
        "Discord webhook should suppress accidental mentions");

    await service.NotifyUpdateInstalledAsync("0.1.0-beta.21", CancellationToken.None);

    Assert.Equal(2, handler.RequestCount,
        "update notification creation should complete one webhook request before returning");
    var updateBody = handler.LastRequestBody ?? "";
    using var updateDocument = JsonDocument.Parse(updateBody);
    var updateFields = updateDocument.RootElement
        .GetProperty("embeds")[0]
        .GetProperty("fields")
        .EnumerateArray()
        .ToList();
    Assert.True(updateFields.Count(field => field.GetProperty("inline").GetBoolean()) >= 3,
        "update Discord embed should arrange status fields in columns");
    Assert.True(updateFields.Any(field =>
            field.GetProperty("name").GetString() == "ʀᴇǫᴜɪʀᴇᴅ" &&
            field.GetProperty("value").GetString() == "Restart IW4MAdmin"),
        "update Discord embed should identify the restart requirement as a field");
}

static async Task TestHeartbeatPeerProofValidationAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local");
    var remoteIdentityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "remote"));
    var remoteIdentity = remoteIdentityService.LoadOrCreate("Remote");
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    var transport = new DragnetTransportService(
        configuration,
        eventStore,
        peerStore,
        identity,
        identityService,
        reviewService,
        trustService,
        () => 1,
        new TestLogger<DragnetTransportService>());
    var signed = CreateSignedPeerInfo(
        remoteIdentityService,
        remoteIdentity,
        "https://remote.example/dragnet",
        directoryListed: true);

    await transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = signed
    }, CancellationToken.None);
    var stored = (await peerStore.ListAsync(CancellationToken.None)).Single();
    Assert.True(stored.IdentityVerified, "valid peer proof should be retained as verified identity");
    Assert.Null(stored.EndpointVerifiedAtUtc, "inbound heartbeat alone should not verify advertised endpoint ownership");

    var tampered = signed with { OriginName = "Tampered Name" };
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
        {
            Sender = tampered
        }, CancellationToken.None),
        "tampered signed metadata should be rejected");

    var stale = CreateSignedPeerInfo(
        remoteIdentityService,
        remoteIdentity,
        "https://remote.example/dragnet",
        directoryListed: true,
        seenAtUtc: DateTimeOffset.UtcNow.Subtract(configuration.PeerStaleAfter).AddMinutes(-1));
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
        {
            Sender = stale
        }, CancellationToken.None),
        "stale direct identity proofs should be rejected");
}

static Task TestLegacyPeerSignatureCompatibility()
{
    var peer = new DragnetPeerInfo
    {
        OriginId = "legacy-compatible",
        OriginName = "Legacy Compatible",
        PublicEndpoint = "https://legacy.example/dragnet",
        ServerCount = 3,
        DirectoryListed = true,
        Region = "Europe",
        Website = "https://legacy.example",
        Version = "0.1.0-alpha.18",
        PublicKeyPem = "public-key",
        Signature = "signature",
        SupportsDeliveryAcknowledgements = true,
        SupportsEvidenceUpdates = true,
        SupportsBanAttestations = true,
        SupportsAttestationRefreshRequests = true,
        SeenAtUtc = DateTimeOffset.Parse("2026-06-11T12:00:00Z")
    };
    var legacyPayload = JsonSerializer.Serialize(new
    {
        peer.OriginId,
        peer.OriginName,
        peer.PublicEndpoint,
        peer.ServerCount,
        peer.DirectoryListed,
        peer.Region,
        peer.Website,
        peer.Version,
        peer.PublicKeyPem,
        Signature = (string?)null,
        peer.SeenAtUtc
    }, DragnetJson.Options);
    var wirePayload = JsonSerializer.Serialize(peer, DragnetJson.Options);

    Assert.Equal(legacyPayload, peer.GetSigningPayload(),
        "new capability fields must not change the alpha.16 identity signing payload");
    Assert.False(peer.GetSigningPayload().Contains("supportsDeliveryAcknowledgements", StringComparison.Ordinal),
        "capability negotiation should remain outside the signed legacy identity payload");
    Assert.False(peer.GetSigningPayload().Contains("supportsBanAttestations", StringComparison.Ordinal),
        "new ledger capability fields must remain outside the legacy identity signature");
    Assert.False(peer.GetSigningPayload().Contains("supportsAttestationRefreshRequests", StringComparison.Ordinal),
        "refresh capability must remain outside the legacy identity signature");
    using var wireDocument = JsonDocument.Parse(wirePayload);
    Assert.True(
        wireDocument.RootElement.GetProperty("supportsDeliveryAcknowledgements").GetBoolean(),
        "capability support must still be advertised on the wire");
    Assert.True(
        wireDocument.RootElement.GetProperty("supportsEvidenceUpdates").GetBoolean(),
        "evidence update support must be advertised outside the legacy signature");
    Assert.True(
        wireDocument.RootElement.GetProperty("supportsBanAttestations").GetBoolean(),
        "ban attestation support must be advertised outside the legacy signature");
    Assert.True(
        wireDocument.RootElement.GetProperty("supportsAttestationRefreshRequests").GetBoolean(),
        "attestation refresh support must be advertised outside the legacy signature");
    return Task.CompletedTask;
}

static async Task TestEventStoreExpiresElapsedTempBansAsync()
{
    await using var testDir = new TestDirectory();
    var store = new DragnetEventStore(testDir.Path);
    await store.LoadAsync(CancellationToken.None);
    var expired = CreateEnvelope(originId: "origin-expired", eventType: DragnetEventType.BanCreated) with
    {
        PenaltyKind = DragnetPenaltyKind.TempBan,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
    };
    var active = CreateEnvelope(originId: "origin-active", eventType: DragnetEventType.BanCreated) with
    {
        PenaltyKind = DragnetPenaltyKind.TempBan,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10)
    };

    await store.UpsertAsync(new DragnetStoredEvent
    {
        Event = expired,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    await store.UpsertAsync(new DragnetStoredEvent
    {
        Event = active,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);

    await store.ExpireElapsedTempBansAsync(DateTimeOffset.UtcNow, CancellationToken.None);

    Assert.Equal(DragnetReviewState.ExpiredBan,
        (await store.GetAsync(expired.EventId, CancellationToken.None))!.ReviewState,
        "elapsed temp-ban should be expired");
    Assert.Equal(DragnetReviewState.PendingBan,
        (await store.GetAsync(active.EventId, CancellationToken.None))!.ReviewState,
        "active temp-ban should remain pending");
}

static async Task TestImportServiceSkipsAsync()
{
    await using var testDir = new TestDirectory();
    var store = new DragnetEventStore(testDir.Path);
    await store.LoadAsync(CancellationToken.None);
    var disabledConfiguration = new DragnetConfiguration
    {
        ImportApprovedEvents = false
    };
    var disabledImportService = new DragnetImportService(
        disabledConfiguration,
        store,
        managerFactory: () => throw new InvalidOperationException("manager should not be resolved for disabled imports"),
        logger: new TestLogger<DragnetImportService>());
    var disabledEvent = new DragnetStoredEvent
    {
        Event = CreateEnvelope(originId: "remote-disabled", eventType: DragnetEventType.BanCreated),
        ReviewState = DragnetReviewState.ApprovedBan
    };

    var disabledResult = await disabledImportService.ImportApprovedAsync(disabledEvent, CancellationToken.None);
    Assert.True(disabledResult.Success, "disabled import should be a successful skip");
    Assert.False(disabledResult.Imported, "disabled import should not mark imported");
    Assert.Contains("disabled", disabledResult.Message, "disabled import should explain skip");

    var alreadyImportedService = new DragnetImportService(
        new DragnetConfiguration(),
        store,
        managerFactory: () => throw new InvalidOperationException("manager should not be resolved for already imported events"),
        logger: new TestLogger<DragnetImportService>());
    var alreadyImported = new DragnetStoredEvent
    {
        Event = CreateEnvelope(originId: "remote-imported", eventType: DragnetEventType.BanCreated),
        ReviewState = DragnetReviewState.ApprovedBan,
        ImportedAtUtc = DateTimeOffset.UtcNow
    };

    var alreadyImportedResult = await alreadyImportedService.ImportApprovedAsync(alreadyImported, CancellationToken.None);
    Assert.True(alreadyImportedResult.Success, "already imported event should be a successful skip");
    Assert.False(alreadyImportedResult.Imported, "already imported event should not import again");
    Assert.Contains("already imported", alreadyImportedResult.Message, "already imported skip should explain reason");
}

static async Task TestImportServiceQueuesUnknownPlayersAsync()
{
    await using var testDir = new TestDirectory();
    var store = new DragnetEventStore(testDir.Path);
    await store.LoadAsync(CancellationToken.None);
    var importService = new DragnetImportService(
        new DragnetConfiguration(),
        store,
        managerFactory: () => throw new InvalidOperationException("manager should not be resolved for invalid network ids"),
        logger: new TestLogger<DragnetImportService>());
    var unknownPlayer = CreateEnvelope(originId: "remote-unknown", eventType: DragnetEventType.BanCreated) with
    {
        PlayerNetworkId = "not-a-network-id"
    };
    var storedEvent = new DragnetStoredEvent
    {
        Event = unknownPlayer,
        ReviewState = DragnetReviewState.ApprovedBan
    };

    await store.UpsertAsync(storedEvent, CancellationToken.None);

    var result = await importService.ImportApprovedAsync(storedEvent, CancellationToken.None);

    Assert.True(result.Success, "unknown player import should queue successfully");
    Assert.False(result.Imported, "unknown player import should not mark imported");
    Assert.Contains("Queued", result.Message, "unknown player import should explain queue state");

    var stored = await store.GetAsync(unknownPlayer.EventId, CancellationToken.None);
    Assert.NotNull(stored, "queued event should remain stored");
    Assert.NotNull(stored!.ImportError, "queued event should persist import error");
    Assert.Contains("Queued", stored.ImportError!, "queued event should persist retry reason");
    Assert.Null(stored.ImportedAtUtc, "queued event should not have imported timestamp");
}

static async Task TestHeartbeatResponseBatchAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        MaxEventsPerHeartbeat = 2,
        MaxKnownPeersPerHeartbeat = 1,
        PublicEndpoint = "https://local.example/dragnet",
        RequireHttps = true
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local");
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    var transport = new DragnetTransportService(
        configuration,
        eventStore,
        peerStore,
        identity,
        identityService,
        reviewService,
        trustService,
        () => 1,
        new TestLogger<DragnetTransportService>());
    var approvedOldest = CreateEnvelope(originId: "local", eventType: DragnetEventType.BanCreated) with
    {
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
    };
    var pending = CreateEnvelope(originId: "local", eventType: DragnetEventType.BanCreated) with
    {
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2)
    };
    var expired = CreateEnvelope(originId: "local", eventType: DragnetEventType.BanCreated) with
    {
        CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        PenaltyKind = DragnetPenaltyKind.TempBan,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1)
    };
    var approvedLift = CreateEnvelope(originId: "local", eventType: DragnetEventType.BanLifted) with
    {
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = approvedOldest,
        ReviewState = DragnetReviewState.ApprovedBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = pending,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = expired,
        ReviewState = DragnetReviewState.ApprovedBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = approvedLift,
        ReviewState = DragnetReviewState.ApprovedLift
    }, CancellationToken.None);

    var firstResponse = await transport.HandleHeartbeatAsync(CreateHeartbeatRequest("remote"), CancellationToken.None);

    Assert.Equal(2, firstResponse.Events.Count, "heartbeat should include approved, unexpired events up to limit");
    Assert.Equal(approvedOldest.EventId, firstResponse.Events[0].EventId, "heartbeat should send events oldest first");
    Assert.Equal(approvedLift.EventId, firstResponse.Events[1].EventId, "heartbeat should include approved lift");

    var secondResponse = await transport.HandleHeartbeatAsync(CreateHeartbeatRequest("remote"), CancellationToken.None);
    Assert.Equal(0, secondResponse.Events.Count, "heartbeat should not resend events already sent to peer");
}

static async Task TestDeliveryAcknowledgementReplayAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        MaxEventsPerHeartbeat = 10,
        PublicEndpoint = "https://local.example/dragnet"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local");
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    var transport = new DragnetTransportService(
        configuration,
        eventStore,
        peerStore,
        identity,
        identityService,
        reviewService,
        trustService,
        () => 1,
        new TestLogger<DragnetTransportService>());
    var sharedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
    var first = CreateEnvelope(identity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "same-time-a",
        CreatedAtUtc = sharedTimestamp
    };
    var second = CreateEnvelope("local", DragnetEventType.BanCreated) with
    {
        EventId = "same-time-b",
        CreatedAtUtc = sharedTimestamp
    };
    foreach (var envelope in new[] { first, second })
    {
        await eventStore.UpsertAsync(new DragnetStoredEvent
        {
            Event = envelope,
            ReviewState = DragnetReviewState.ApprovedBan
        }, CancellationToken.None);
    }

    var unsignedEvidence = new DragnetEvidenceUpdate
    {
        UpdateId = "evidence-update-a",
        EventId = first.EventId,
        OriginId = identity.OriginId,
        OriginName = identity.OriginName,
        OriginPublicKeyPem = identity.PublicKeyPem,
        EvidenceUrl = "https://www.youtube.com/watch?v=evidence",
        SubmittedByName = "Local Admin",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Signature = ""
    };
    var signedEvidence = unsignedEvidence with
    {
        Signature = identityService.Sign(identity, unsignedEvidence.GetSigningPayload())
    };
    await eventStore.SetEvidenceUpdateAsync(signedEvidence, CancellationToken.None);

    var request = CreateHeartbeatRequest("remote") with
    {
        Sender = CreateHeartbeatRequest("remote").Sender with
        {
            SupportsDeliveryAcknowledgements = true,
            SupportsEvidenceUpdates = true
        }
    };
    var initial = await transport.HandleHeartbeatAsync(request, CancellationToken.None);
    Assert.Equal(2, initial.Events.Count, "acknowledgement peer should receive all equal-timestamp events");
    Assert.Equal(1, initial.EvidenceUpdates.Count, "capable peer should receive signed evidence updates");

    var replay = await transport.HandleHeartbeatAsync(request, CancellationToken.None);
    Assert.Equal(2, replay.Events.Count, "unacknowledged events should replay");

    var acknowledged = await transport.HandleHeartbeatAsync(request with
    {
        AcknowledgedEventIds = initial.Events
            .Select(item => item.EventId)
            .Concat(initial.EvidenceUpdates.Select(item => item.UpdateId))
            .ToList()
    }, CancellationToken.None);
    Assert.Equal(0, acknowledged.Events.Count, "acknowledged events should stop replaying");
    Assert.Equal(0, acknowledged.EvidenceUpdates.Count, "acknowledged evidence updates should stop replaying");
    var peer = (await peerStore.ListAsync(CancellationToken.None))
        .Single(item => item.OriginId == "remote");
    Assert.Equal(3, peer.EventDeliveries.Count(item => item.AcknowledgedAtUtc is not null),
        "peer delivery records should persist event and evidence acknowledgements");
    Assert.NotNull(peer.LastSyncVerifiedAtUtc, "acknowledgements should update sync verification time");

    Assert.True(await peerStore.RequestResyncAsync("remote", CancellationToken.None),
        "manual resync should clear delivery coverage");
    var resync = await transport.HandleHeartbeatAsync(request, CancellationToken.None);
    Assert.Equal(2, resync.Events.Count, "manual resync should replay all active approved events");

    await peerStore.QueueAcknowledgementsAsync(
        "remote",
        ["remote-event-a", "remote-event-b"],
        CancellationToken.None);
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var pending = await peerStore.GetPendingAcknowledgementsAsync(
        "remote",
        10,
        CancellationToken.None);
    Assert.Equal(2, pending.Count, "queued response acknowledgements should survive restart");
    await peerStore.MarkAcknowledgementsSentAsync("remote", pending, CancellationToken.None);
    Assert.Equal(
        0,
        (await peerStore.GetPendingAcknowledgementsAsync("remote", 10, CancellationToken.None)).Count,
        "successfully transmitted acknowledgements should leave the queue");

    Assert.True(
        await peerStore.QueueAttestationRefreshAsync(
            "remote",
            ["same-time-a", "same-time-b"],
            CancellationToken.None),
        "coverage refresh requests should queue for known peers");
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var refreshRequests = await peerStore.GetPendingAttestationRefreshAsync(
        "remote",
        10,
        CancellationToken.None);
    Assert.Equal(2, refreshRequests.Count, "coverage refresh requests should survive restart");
    await peerStore.MarkAttestationRefreshSentAsync(
        "remote",
        refreshRequests,
        CancellationToken.None);
    Assert.Equal(
        0,
        (await peerStore.GetPendingAttestationRefreshAsync("remote", 10, CancellationToken.None)).Count,
        "successful coverage refresh delivery should clear the queue");
}

static async Task TestEvidenceUpdatesAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local");
    var remoteIdentityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "remote"));
    var remoteIdentity = remoteIdentityService.LoadOrCreate("Remote");
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    var transport = new DragnetTransportService(
        configuration,
        eventStore,
        peerStore,
        identity,
        identityService,
        reviewService,
        trustService,
        () => 1,
        new TestLogger<DragnetTransportService>());
    var remoteBan = CreateEnvelope(remoteIdentity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "remote-ban-with-evidence",
        OriginName = remoteIdentity.OriginName,
        OriginPublicKeyPem = remoteIdentity.PublicKeyPem
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = remoteBan,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    var unsignedUpdate = new DragnetEvidenceUpdate
    {
        UpdateId = "remote-evidence-update",
        EventId = remoteBan.EventId,
        OriginId = remoteIdentity.OriginId,
        OriginName = remoteIdentity.OriginName,
        OriginPublicKeyPem = remoteIdentity.PublicKeyPem,
        EvidenceUrl = "https://youtu.be/example",
        SubmittedByName = "Remote Admin",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Signature = ""
    };
    var signedUpdate = unsignedUpdate with
    {
        Signature = remoteIdentityService.Sign(remoteIdentity, unsignedUpdate.GetSigningPayload())
    };
    var sender = CreateSignedPeerInfo(
        remoteIdentityService,
        remoteIdentity,
        "https://remote.example/dragnet",
        directoryListed: false) with
    {
        SupportsDeliveryAcknowledgements = true,
        SupportsEvidenceUpdates = true
    };
    var accepted = await transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = sender,
        EvidenceUpdates = [signedUpdate]
    }, CancellationToken.None);

    Assert.True(accepted.AcknowledgedEventIds.Contains(signedUpdate.UpdateId),
        "valid origin-signed evidence update should be acknowledged");
    var stored = await eventStore.GetAsync(remoteBan.EventId, CancellationToken.None);
    Assert.Equal(signedUpdate.EvidenceUrl, stored!.EvidenceUpdate!.EvidenceUrl,
        "valid evidence update should be attached without replacing the original ban");

    var tampered = signedUpdate with
    {
        UpdateId = "tampered-evidence-update",
        EvidenceUrl = "https://evil.example/tampered"
    };
    var rejected = await transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = sender,
        EvidenceUpdates = [tampered]
    }, CancellationToken.None);
    Assert.False(rejected.AcknowledgedEventIds.Contains(tampered.UpdateId),
        "tampered evidence update must not be acknowledged");
    stored = await eventStore.GetAsync(remoteBan.EventId, CancellationToken.None);
    Assert.Equal(signedUpdate.EvidenceUrl, stored!.EvidenceUpdate!.EvidenceUrl,
        "tampered evidence must not replace accepted evidence");
}

static async Task TestBanAttestationsAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local");
    var remoteIdentityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "remote"));
    var remoteIdentity = remoteIdentityService.LoadOrCreate("Remote Network");
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    var attestationService = new DragnetAttestationService(
        configuration,
        eventStore,
        identity,
        identityService,
        () => 2,
        () => ["Local TDM", "Local Domination"]);
    var transport = new DragnetTransportService(
        configuration,
        eventStore,
        peerStore,
        identity,
        identityService,
        reviewService,
        trustService,
        () => 2,
        new TestLogger<DragnetTransportService>(),
        attestationService);
    var ban = CreateEnvelope("ban-origin", DragnetEventType.BanCreated) with
    {
        EventId = "ledger-ban"
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = ban,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    var localBan = CreateEnvelope(identity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "local-refresh-ban",
        OriginName = identity.OriginName,
        OriginPublicKeyPem = identity.PublicKeyPem,
        Iw4mAdminPenaltyId = 99
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = localBan,
        ReviewState = DragnetReviewState.ApprovedBan
    }, CancellationToken.None);
    var unsigned = new DragnetBanAttestation
    {
        AttestationId = DragnetAttestationService.CreateAttestationId(
            remoteIdentity.OriginId,
            ban.EventId),
        EventId = ban.EventId,
        NetworkOriginId = remoteIdentity.OriginId,
        NetworkName = remoteIdentity.OriginName,
        PublicEndpoint = "https://remote.example/dragnet",
        NetworkPublicKeyPem = remoteIdentity.PublicKeyPem,
        ServerCount = 4,
        ServerNames = ["Remote TDM", "Remote Domination", "Remote Hardpoint", "Remote KC"],
        Status = DragnetBanCoverageStatus.Enforced,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        Signature = ""
    };
    var signed = unsigned with
    {
        Signature = remoteIdentityService.Sign(remoteIdentity, unsigned.GetSigningPayload())
    };
    var sender = CreateSignedPeerInfo(
        remoteIdentityService,
        remoteIdentity,
        "https://remote.example/dragnet",
        directoryListed: false) with
    {
        SupportsDeliveryAcknowledgements = true,
        SupportsBanAttestations = true,
        SupportsAttestationRefreshRequests = true
    };
    var response = await transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = sender,
        BanAttestations = [signed]
    }, CancellationToken.None);
    var deliveryKey = DragnetPeerStore.AttestationDeliveryKey(signed);
    Assert.True(response.AcknowledgedEventIds.Contains(deliveryKey),
        "valid signed attestation should be acknowledged");
    var stored = await eventStore.GetAsync(ban.EventId, CancellationToken.None);
    Assert.Equal(DragnetBanCoverageStatus.Enforced, stored!.BanAttestations.Single().Status,
        "accepted attestation should update ban coverage");

    var tampered = signed with
    {
        UpdatedAtUtc = signed.UpdatedAtUtc.AddMinutes(1),
        Status = DragnetBanCoverageStatus.Accepted
    };
    response = await transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = sender,
        BanAttestations = [tampered]
    }, CancellationToken.None);
    Assert.False(response.AcknowledgedEventIds.Contains(DragnetPeerStore.AttestationDeliveryKey(tampered)),
        "tampered attestation must not be acknowledged");
    stored = await eventStore.GetAsync(ban.EventId, CancellationToken.None);
    Assert.Equal(DragnetBanCoverageStatus.Enforced, stored!.BanAttestations.Single().Status,
        "tampered attestation must not change coverage");

    await transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = sender,
        AttestationRefreshEventIds = [localBan.EventId]
    }, CancellationToken.None);
    var refreshedLocal = await eventStore.GetAsync(localBan.EventId, CancellationToken.None);
    Assert.Equal(
        DragnetBanCoverageStatus.Enforced,
        refreshedLocal!.BanAttestations.Single(attestation =>
            attestation.NetworkOriginId == identity.OriginId).Status,
        "authenticated refresh request should republish local coverage");
}

static async Task TestPublicLedgerAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Coverage Network");
    var staleIdentityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "stale-identity"));
    var staleIdentity = staleIdentityService.LoadOrCreate("Stale Queue Network");
    var ban = CreateEnvelope("origin-ledger", DragnetEventType.BanCreated) with
    {
        EventId = "public-ledger-ban",
        PlayerName = "Ledger Player",
        Reason = "Aimbot evidence",
        AdminName = "Ledger Admin",
        EvidenceUrl = "https://youtu.be/evidence"
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = ban,
        ReviewState = DragnetReviewState.PendingBan,
        LocalDecisionReason = "private reviewer note",
        BanAttestations =
        [
            new DragnetBanAttestation
            {
                AttestationId = "origin-ledger-attestation",
                EventId = ban.EventId,
                NetworkOriginId = ban.OriginId,
                NetworkName = ban.OriginName,
                PublicEndpoint = "https://origin.example/dragnet",
                NetworkPublicKeyPem = "origin-public-key",
                ServerCount = 99,
                ServerNames = ["Origin Server"],
                Status = DragnetBanCoverageStatus.Enforced,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Signature = "origin-signature"
            },
            new DragnetBanAttestation
            {
                AttestationId = "ledger-attestation",
                EventId = ban.EventId,
                NetworkOriginId = identity.OriginId,
                NetworkName = identity.OriginName,
                PublicEndpoint = "https://coverage.example/dragnet",
                NetworkPublicKeyPem = identity.PublicKeyPem,
                ServerCount = 3,
                ServerNames = ["TDM", "Domination", "Hardpoint"],
                Status = DragnetBanCoverageStatus.Enforced,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Signature = "signature"
            },
            new DragnetBanAttestation
            {
                AttestationId = "stale-ledger-attestation",
                EventId = ban.EventId,
                NetworkOriginId = staleIdentity.OriginId,
                NetworkName = staleIdentity.OriginName,
                PublicEndpoint = "https://stale.example/dragnet",
                NetworkPublicKeyPem = staleIdentity.PublicKeyPem,
                ServerCount = 1,
                ServerNames = ["Queued TDM"],
                Status = DragnetBanCoverageStatus.Queued,
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
                Signature = "signature"
            }
        ]
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = ban with
        {
            EventId = "public-ledger-ban-duplicate",
            CreatedAtUtc = ban.CreatedAtUtc.AddSeconds(1)
        },
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    var zeroIdTempBan = CreateEnvelope("zero-id-origin", DragnetEventType.BanCreated) with
    {
        EventId = "zero-id-tempban",
        PlayerNetworkId = "zero-id-player",
        PenaltyKind = DragnetPenaltyKind.TempBan,
        Iw4mAdminPenaltyId = 0,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = zeroIdTempBan,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = CreateEnvelope(zeroIdTempBan.OriginId, DragnetEventType.BanLifted) with
        {
            EventId = "public-ledger-lift",
            PlayerNetworkId = zeroIdTempBan.PlayerNetworkId,
            PlayerGame = zeroIdTempBan.PlayerGame,
            Iw4mAdminPenaltyId = 0,
            PenaltyKind = DragnetPenaltyKind.TempBan,
            CreatedAtUtc = zeroIdTempBan.CreatedAtUtc.AddMinutes(1)
        },
        ReviewState = DragnetReviewState.PendingLift
    }, CancellationToken.None);
    for (var index = 0; index < 9; index++)
    {
        await eventStore.UpsertAsync(new DragnetStoredEvent
        {
            Event = CreateEnvelope($"page-origin-{index}", DragnetEventType.BanCreated) with
            {
                EventId = $"public-ledger-page-ban-{index}",
                PlayerName = $"Paged Player {index}",
                Iw4mAdminPenaltyId = 200 + index,
                CreatedAtUtc = ban.CreatedAtUtc.AddMinutes(10 + index)
            },
            ReviewState = DragnetReviewState.PendingBan
        }, CancellationToken.None);
    }

    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    await peerStore.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = staleIdentity.OriginId,
        OriginName = staleIdentity.OriginName,
        PublicEndpoint = "https://stale.example/dragnet",
        ServerCount = 1,
        SeenAtUtc = DateTimeOffset.UtcNow
    }, CancellationToken.None, identityVerified: true);
    await peerStore.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = "unavailable-network",
        OriginName = "Unavailable Network",
        PublicEndpoint = "https://unavailable.example/dragnet",
        ServerCount = 2,
        SeenAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
    }, CancellationToken.None, identityVerified: true);
    var ledger = new DragnetLedgerService(configuration, eventStore, peerStore, () => 2, identity);
    var snapshot = await ledger.GetSnapshotAsync(CancellationToken.None);
    var ledgerBan = snapshot.Bans.Single(item =>
        item.EventIds.Contains(ban.EventId, StringComparer.OrdinalIgnoreCase));
    var liftedZeroIdBan = snapshot.Bans.Single(item => item.EventId == zeroIdTempBan.EventId);
    Assert.Equal(1, ledgerBan.DuplicateEventCount, "ledger should consolidate duplicate penalty events");
    Assert.Equal(2, ledgerBan.AcceptedNetworkCount, "ledger should count signed network coverage");
    Assert.Equal(3, ledgerBan.EnforcedServerCount, "ledger should sum enforced server coverage");
    Assert.Equal(2, ledgerBan.Attestations.Count, "ledger should include every active eligible network");
    Assert.Equal(0, ledgerBan.UnreportedNetworkCount, "inactive networks should not skew missing report counts");
    Assert.Equal(0, ledgerBan.UnavailableNetworkCount, "inactive networks should not skew unavailable counts");
    Assert.Equal(1, ledgerBan.StaleReportCount, "ledger should flag stale non-enforced reports");
    Assert.Equal("Needs attention", ledgerBan.ReconciliationStatus,
        "stale or unavailable coverage should require attention");
    Assert.Equal("Lifted", liftedZeroIdBan.Status,
        "a lift should close a zero-penalty-id tempban for the same origin and player");
    Assert.False(
        ledgerBan.Attestations.Any(attestation =>
            attestation.NetworkOriginId.Equals(ban.OriginId, StringComparison.OrdinalIgnoreCase)),
        "ledger should expose only peer propagation attestations");
    Assert.Equal("IW4", ledgerBan.PlayerGame!, "ledger JSON projection should identify the player platform");
    Assert.Equal("https://youtu.be/evidence", ledgerBan.EvidenceUrl!, "ledger JSON projection should expose public HTTPS evidence");
    Assert.True(ledgerBan.Attestations.Any(attestation => attestation.NetworkName == "Coverage Network"),
        "ledger JSON projection should identify attesting networks");
    Assert.True(ledgerBan.Attestations.Any(attestation => attestation.NetworkName == "Stale Queue Network"),
        "ledger JSON projection should identify stale queued reports");
    Assert.False(ledgerBan.Attestations.Any(attestation => attestation.NetworkName == "Unavailable Network"),
        "ledger JSON projection should omit inactive networks from current coverage");
    Assert.True(ledgerBan.Attestations.Any(attestation =>
            attestation.ServerNames.SequenceEqual(["TDM", "Domination", "Hardpoint"])),
        "ledger JSON projection should name covered servers");
    Assert.Equal("Ledger Admin", ledgerBan.AdminName,
        "ledger JSON projection should include the issuing administrator");
    Assert.Equal(11, snapshot.Bans.Count, "ledger module should have enough rows to paginate at eight per page");
}

static async Task TestAttestationBackfillAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local Network");
    var localBan = CreateEnvelope(identity.OriginId, DragnetEventType.BanCreated) with
    {
        EventId = "existing-local-ban",
        OriginName = identity.OriginName,
        OriginPublicKeyPem = identity.PublicKeyPem
    };
    var approvedRemoteBan = CreateEnvelope("remote-origin", DragnetEventType.BanCreated) with
    {
        EventId = "existing-approved-remote-ban"
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = localBan,
        ReviewState = DragnetReviewState.ApprovedBan
    }, CancellationToken.None);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = approvedRemoteBan,
        ReviewState = DragnetReviewState.ApprovedBan,
        ImportError = "Queued: awaiting player"
    }, CancellationToken.None);
    var service = new DragnetAttestationService(
        configuration,
        eventStore,
        identity,
        identityService,
        () => 5,
        () => ["TDM", "Domination", "Hardpoint", "Kill Confirmed", "Hardpoint EU"]);
    await service.BackfillAsync(CancellationToken.None);

    var storedLocal = await eventStore.GetAsync(localBan.EventId, CancellationToken.None);
    var storedRemote = await eventStore.GetAsync(approvedRemoteBan.EventId, CancellationToken.None);
    Assert.Equal(DragnetBanCoverageStatus.Enforced, storedLocal!.BanAttestations.Single().Status,
        "existing local bans should backfill as enforced");
    Assert.Equal(DragnetBanCoverageStatus.Queued, storedRemote!.BanAttestations.Single().Status,
        "existing queued approvals should backfill as queued");
    Assert.Equal(5, storedLocal.BanAttestations.Single().ServerCount,
        "backfill should publish current network server coverage");
    Assert.Equal(5, storedLocal.BanAttestations.Single().ServerNames.Count,
        "backfill should publish covered server names");
}

static async Task TestWebfrontDashboardRendersAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet",
        UpdateCheckEnabled = false,
        WebfrontPermission = EFClient.Permission.SeniorAdmin,
        ReviewPermission = EFClient.Permission.Owner,
        TrustPermission = EFClient.Permission.Owner,
        PeerManagementPermission = EFClient.Permission.SeniorAdmin
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local Network");
    var legacyEvent = CreateEnvelope(originId: "legacy-origin", eventType: DragnetEventType.BanCreated) with
    {
        CreatedAtUtc = DateTimeOffset.MinValue,
        Iw4mAdminPenaltyId = 0
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = legacyEvent,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    var bulkEvent = CreateEnvelope(originId: "bulk-origin", eventType: DragnetEventType.BanCreated) with
    {
        EventId = "dashboard-bulk-event",
        PlayerName = "Bulk Player"
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = bulkEvent,
        ReviewState = DragnetReviewState.PendingBan
    }, CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    await peerStore.UpsertAsync(new DragnetPeerInfo
    {
        OriginId = "dashboard-peer",
        OriginName = "Dashboard Peer",
        PublicEndpoint = "https://peer.example/dragnet",
        SupportsDeliveryAcknowledgements = true,
        SupportsBanAttestations = true,
        SupportsAttestationRefreshRequests = true
    }, CancellationToken.None);
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    await trustService.TrustAsync(
        bulkEvent.OriginId,
        bulkEvent.OriginName,
        false,
        false,
        CancellationToken.None);
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    using var updateService = new DragnetUpdateService(
        configuration,
        new TestLogger<DragnetUpdateService>(),
        new HttpClient(new StaticResponseHandler(System.Net.HttpStatusCode.OK, "{}")));
    using var healthClient = new HttpClient(new StaticResponseHandler(
        System.Net.HttpStatusCode.OK,
        CreateSignedHealthResponse(identityService, identity, configuration.PublicEndpoint!)));
    var onboardingService = new DragnetOnboardingService(
        configuration,
        identity,
        identityService,
        peerStore,
        updateService,
        healthClient);
    var configurationHandler = new RecordingConfigurationHandler<DragnetConfiguration>();
    var directoryService = new DragnetDirectoryService(
        configuration,
        identity,
        peerStore,
        () => 1);
    var ledgerService = new DragnetLedgerService(
        configuration,
        eventStore,
        peerStore,
        () => 1,
        identity);
    var networkProfileService = new DragnetNetworkProfileService(
        configuration,
        eventStore,
        peerStore,
        identity,
        () => 1);
    var notificationStore = new DragnetNotificationStore(
        System.IO.Path.Combine(testDir.Path, "notifications"));
    await notificationStore.LoadAsync(CancellationToken.None);
    var notificationService = new DragnetNotificationService(
        configuration,
        notificationStore,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetNotificationService>());
    await notificationService.NotifyNewEventAsync(bulkEvent, CancellationToken.None);
    var webfront = new DragnetWebfrontService(
        configuration,
        eventStore,
        peerStore,
        reviewService,
        trustService,
        updateService,
        onboardingService,
        directoryService,
        ledgerService,
        networkProfileService,
        identity,
        identityService,
        configurationHandler,
        managerFactory: () => null!,
        notificationService);
    var interaction = await webfront.CreateNavigationInteractionAsync(CancellationToken.None);

    Assert.Equal(InteractionType.TemplateContent, interaction.InteractionType, "dashboard should render as an IW4MAdmin navigation page");
    Assert.Equal(2, (int)interaction.InteractionType, "dashboard interaction type should match IW4MAdmin script nav pages");
    Assert.Equal(EFClient.Permission.SeniorAdmin, interaction.MinimumPermission, "dashboard should use configured webfront permission");

    var reviewInteraction = await webfront.CreateReviewInteractionAsync(CancellationToken.None);
    var trustInteraction = await webfront.CreateTrustInteractionAsync(CancellationToken.None);
    var peerInteraction = await webfront.CreatePeerInteractionAsync(CancellationToken.None);
    var notificationInteraction = await webfront.CreateNotificationInteractionAsync(CancellationToken.None);
    Assert.Equal(EFClient.Permission.Owner, reviewInteraction.MinimumPermission, "review action should use configured review permission");
    Assert.Equal(EFClient.Permission.Owner, trustInteraction.MinimumPermission, "trust action should use configured trust permission");
    Assert.Equal(EFClient.Permission.SeniorAdmin, peerInteraction.MinimumPermission, "peer action should use configured peer permission");
    Assert.Equal(EFClient.Permission.Owner, notificationInteraction.MinimumPermission,
        "notification acknowledgement should use configured review permission");

    var html = await interaction.Action(0, null, null, new Dictionary<string, string>
    {
        ["eventId"] = legacyEvent.EventId
    }, CancellationToken.None);
    Assert.Contains("Peer transport", html, "dashboard should include peer section");
    Assert.Contains("dragnet-updates-modal", html, "dashboard should include update rollout operations");
    Assert.Contains("dragnet-diagnostics-modal", html, "dashboard should include network diagnostics");
    Assert.Contains("Download diagnostics", html, "diagnostics should expose the sanitized download");
    Assert.Contains("Peer health", html, "diagnostics should summarize per-peer health");
    Assert.Contains("Connection timeline", html, "diagnostics should include recent peer transitions");
    Assert.Contains("data-tip=\"Diagnostics\"", html, "dashboard navigation should expose diagnostics");
    Assert.Contains("Network versions", html, "update rollout should summarize active peer versions");
    Assert.Contains("Rollout history", html, "update rollout should include persistent lifecycle history");
    Assert.Contains("data-tip=\"Updates\"", html, "dashboard navigation should expose update operations");
    Assert.Contains("class=\"dragnet-peer-list\"", html, "peer transport should use the responsive peer list");
    Assert.False(
        html.Contains("<th class=\"px-4 py-2\">Endpoint</th>", StringComparison.Ordinal),
        "peer transport should not use the wide fixed-column table");
    Assert.Contains(".dragnet-top-nav{position:sticky;top:4rem}", html,
        "Dragnet navigation should stick below IW4MAdmin header controls");
    Assert.False(
        html.Contains(".dragnet-top-nav{position:sticky;top:4rem;z-index:", StringComparison.Ordinal),
        "Dragnet navigation should not create a stacking layer over IW4MAdmin overlays");
    Assert.Contains("#dragnet-peer-modal .dragnet-modal-body{overflow-x:hidden}", html,
        "peer transport modal should suppress horizontal overflow");
    Assert.Contains("Dragnet events", html, "dashboard should include event section");
    Assert.Contains("aria-label=\"Dragnet navigation\"", html, "dashboard should include an in-page Dragnet nav menu");
    Assert.Contains("dragnet-ledger-modal", html, "dashboard nav should open the public ledger module");
    Assert.Contains("network-profile-", html, "dashboard should render network profile modules");
    Assert.False(html.Contains("/dragnet/network?id=", StringComparison.OrdinalIgnoreCase),
        "dashboard should not link to removed network profile pages");
    Assert.Contains("Notification inbox", html, "dashboard should include the notification inbox");
    Assert.Contains("data-tip=\"Notifications\"", html, "dashboard should expose notifications as an icon tooltip");
    Assert.Contains("onpointerdown=\"dragnetPrepareDynamicAction(this)\"", html,
        "notification actions should release the Dragnet dialog before IW4MAdmin opens its action modal");
    Assert.Contains("function dragnetPrepareDynamicAction(button)", html,
        "dashboard should include the native action modal handoff helper");
    Assert.Contains(
        "%22InteractionId%22%3A%22Dragnet%3A%3ANotification%22%2C%22ActionButtonLabel%22%3A%22Acknowledge%22%2C%22Name%22%3A%22Acknowledge%22%2C%22ShouldRefresh%22%3A%22true%22",
        html,
        "notification acknowledgements should refresh the dashboard after success");
    Assert.Contains("<span class=\"rounded-full bg-surface-alt px-1.5 text-xs text-muted\">1</span>", html,
        "dashboard should display the administrator's unread count as an icon badge");
    Assert.False(html.Contains("Acknowledgements are personal", StringComparison.Ordinal),
        "notification module should not render the old explanatory copy");
    Assert.Contains("Deployment readiness", html, "dashboard should include onboarding diagnostics");
    Assert.False(
        html.Contains("px-4 py-3 border-b border-r border-line/60", StringComparison.Ordinal),
        "deployment readiness checks should not render internal divider borders");
    Assert.Contains(
        "grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-2",
        html,
        "release status should use spacing instead of internal dividers");
    Assert.False(
        html.Contains("<tr class=\"border-b border-line/60\">", StringComparison.Ordinal),
        "dashboard data tables should use spacing and hover states instead of row dividers");
    Assert.False(
        html.Contains("p-4 border-r border-line/60 border-b border-line/60", StringComparison.Ordinal),
        "event detail cells should not render divider borders");
    Assert.Contains(
        "grid grid-cols-1 lg:grid-cols-3 gap-2 p-2",
        html,
        "event details should use spaced surface cells");
    Assert.Contains("Signed proof", html, "dashboard should expose identity proof diagnostics");
    Assert.Contains("Deployment guide", html, "dashboard should include endpoint-specific deployment guidance");
    Assert.Contains("/dragnet/setup-guide", html, "dashboard should link to the shareable setup guide");
    Assert.Contains("Community directory", html, "dashboard should include the opt-in directory");
    Assert.Contains("Acknowledged deliveries", html, "dashboard should summarize acknowledged event delivery");
    Assert.Contains("Pending deliveries", html, "dashboard should summarize incomplete event delivery");
    Assert.Contains("Delivery", html, "peer table should expose delivery coverage");
    Assert.Contains("Verify sync", html, "peer actions should expose delivery verification");
    Assert.Contains("Resync", html, "peer actions should expose event replay");
    Assert.Contains("Refresh coverage", html, "capable peers should expose attestation refresh");
    Assert.Contains("Select all", html, "dashboard should expose selecting all eligible pending bans");
    Assert.Contains("Approve selected", html, "dashboard should expose bulk approval");
    Assert.Contains("dragnet-bulk-ban", html, "trusted pending bans should render selection checkboxes");
    Assert.Contains("Configure", html, "dashboard should expose the setup action");
    Assert.Contains($"Dragnet {DragnetBuildInfo.Version}", html, "dashboard should show deployed Dragnet version");
    Assert.Contains("Queued imports", html, "dashboard should distinguish queued imports");
    Assert.Contains("Degraded peers", html, "dashboard should expose transient peer health");
    Assert.Contains("Gossip eligible", html, "dashboard should show gossip eligibility");
    Assert.Contains("Advertised recently", html, "dashboard should show recent gossip coverage");
    Assert.Contains("Last advertised", html, "peer table should show the persisted gossip cursor");
    Assert.Contains("Unknown", html, "dashboard should render unknown legacy timestamps and penalty ids without huge ages");
    Assert.Contains(
        "data-enhance-nav=\"false\"",
        html,
        "dashboard filter links should force a fresh interaction render when query parameters change");

    var localEvent = CreateEnvelope(originId: identity.OriginId, eventType: DragnetEventType.BanCreated) with
    {
        OriginName = "Historical Local Name"
    };
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = localEvent,
        ReviewState = DragnetReviewState.ApprovedBan
    }, CancellationToken.None);
    html = await interaction.Action(0, null, null, new Dictionary<string, string>
    {
        ["filter"] = DragnetEventFilter.Local.ToString()
    }, CancellationToken.None);
    Assert.Contains("Local outbound event", html, "local event detail should be identified as outbound");
    Assert.Contains("Add evidence", html, "local ban detail should allow origin administrators to add evidence");
    Assert.Contains(">Local<", html, "local event origin and trust should be identified as local");
    Assert.Contains("Outbound", html, "local events should not look like pending imports");
    Assert.False(html.Contains("Historical Local Name</td>", StringComparison.OrdinalIgnoreCase),
        "historical local origin names should not make local events look remote");
}

static Task TestUpdateVersionComparisonAsync()
{
    Assert.True(
        DragnetUpdateService.CompareVersions("v0.1.2", "0.1.1") > 0,
        "newer stable version should be detected");
    Assert.True(
        DragnetUpdateService.CompareVersions("v0.1.1-alpha.6", "0.1.1") < 0,
        "prerelease should compare below the matching stable release");
    Assert.Equal(
        0,
        DragnetUpdateService.CompareVersions("v0.1.1", "0.1.1"),
        "v prefix should not affect comparison");
    Assert.True(
        DragnetUpdateService.CompareVersions("v0.1.2-alpha.10", "v0.1.2-alpha.9") > 0,
        "numeric prerelease identifiers should compare numerically");
    return Task.CompletedTask;
}

static async Task TestUpdateReleaseMetadataAsync()
{
    var configuration = new DragnetConfiguration
    {
        UpdateCheckEnabled = true,
        AutoUpdateEnabled = false,
        ReleaseApiUrl = "https://api.example.test/releases/latest"
    };
    using var httpClient = new HttpClient(new StaticResponseHandler(
        System.Net.HttpStatusCode.OK,
        """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://example.test/releases/v0.2.0"
        }
        """));
    using var updateService = new DragnetUpdateService(
        configuration,
        new TestLogger<DragnetUpdateService>(),
        httpClient);

    updateService.Start();
    for (var attempt = 0; attempt < 50 && updateService.Status.CheckedAtUtc is null; attempt++)
    {
        await Task.Delay(20);
    }

    var status = updateService.Status;
    Assert.Equal("0.2.0", status.LatestVersion, "release tag should be normalized");
    Assert.True(status.UpdateAvailable, "newer release should be reported");
    Assert.Equal(
        "https://example.test/releases/v0.2.0",
        status.ReleaseUrl,
        "release URL should be retained");
}

static async Task TestUpdateReleaseFeedFallbackAsync()
{
    var configuration = new DragnetConfiguration
    {
        UpdateCheckEnabled = true,
        AutoUpdateEnabled = false,
        ReleaseApiUrl = "https://api.example.test/releases/latest",
        ReleaseFeedUrl = "https://example.test/releases.atom"
    };
    using var httpClient = new HttpClient(new RoutingResponseHandler(request =>
        request.RequestUri == new Uri(configuration.ReleaseApiUrl)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"API rate limit exceeded"}""")
            }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <feed xmlns="http://www.w3.org/2005/Atom">
                      <entry>
                        <link rel="alternate" href="https://example.test/releases/v0.2.1"/>
                        <title>v0.2.1</title>
                      </entry>
                    </feed>
                    """,
                    System.Text.Encoding.UTF8,
                    "application/atom+xml")
            }));
    using var updateService = new DragnetUpdateService(
        configuration,
        new TestLogger<DragnetUpdateService>(),
        httpClient);

    updateService.Start();
    for (var attempt = 0; attempt < 50 && updateService.Status.CheckedAtUtc is null; attempt++)
    {
        await Task.Delay(20);
    }

    var status = updateService.Status;
    Assert.Equal("0.2.1", status.LatestVersion, "feed release tag should be normalized");
    Assert.True(status.UpdateAvailable, "feed fallback should report a newer release");
    Assert.Null(status.CheckError, "successful feed fallback should clear API failure");
}

static async Task TestUpdatePageLoadRefreshAsync()
{
    var configuration = new DragnetConfiguration
    {
        UpdateCheckEnabled = true,
        AutoUpdateEnabled = false,
        PageLoadUpdateCheckMaxAge = TimeSpan.FromMinutes(5),
        ReleaseApiUrl = "https://api.example.test/releases/latest"
    };
    var handler = new CountingResponseHandler(
        System.Net.HttpStatusCode.OK,
        """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://example.test/releases/v0.2.0"
        }
        """);
    using var updateService = new DragnetUpdateService(
        configuration,
        new TestLogger<DragnetUpdateService>(),
        new HttpClient(handler));

    await Task.WhenAll(
        updateService.RefreshForPageLoadAsync(CancellationToken.None),
        updateService.RefreshForPageLoadAsync(CancellationToken.None));
    await updateService.RefreshForPageLoadAsync(CancellationToken.None);

    Assert.Equal(1, handler.RequestCount, "concurrent and recent page loads should share the cached update check");
    Assert.Equal("0.2.0", updateService.Status.LatestVersion, "page-load refresh should populate release metadata");
}

static async Task TestAutomaticUpdateInstallAsync()
{
    await using var testDir = new TestDirectory();
    var deployedPath = System.IO.Path.Combine(testDir.Path, "Dragnet.dll");
    File.Copy(typeof(DragnetUpdateService).Assembly.Location, deployedPath);
    var version = DragnetBuildInfo.Version;
    var tag = $"v{version}";
    var assetUrl =
        $"https://github.com/SebzIO/iw4madmin-dragnet/releases/download/{tag}/" +
        $"Dragnet.IW4MAdmin.Plugin-{tag}.zip";
    var packageBytes = CreateReleasePackage(tag, typeof(DragnetUpdateService).Assembly.Location);
    var configuration = new DragnetConfiguration
    {
        UpdateCheckEnabled = true,
        AutoUpdateEnabled = true,
        ReleaseApiUrl = "https://api.example.test/releases/latest"
    };
    var notificationStore = new DragnetNotificationStore(System.IO.Path.Combine(testDir.Path, "notifications"));
    await notificationStore.LoadAsync(CancellationToken.None);
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var alertManager = new RecordingAlertManager();
    using var notificationService = new DragnetNotificationService(
        configuration,
        notificationStore,
        eventStore,
        () => null!,
        new TestLogger<DragnetNotificationService>(),
        alertManager);
    using var httpClient = new HttpClient(new RoutingResponseHandler(request =>
    {
        if (request.RequestUri == new Uri(configuration.ReleaseApiUrl))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "tag_name": "{{tag}}",
                      "html_url": "https://github.com/SebzIO/iw4madmin-dragnet/releases/tag/{{tag}}",
                      "assets": [
                        {
                          "name": "Dragnet.IW4MAdmin.Plugin-{{tag}}.zip",
                          "browser_download_url": "{{assetUrl}}"
                        }
                      ]
                    }
                    """)
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(packageBytes)
        };
    }));
    using var updateService = new DragnetUpdateService(
        configuration,
        new TestLogger<DragnetUpdateService>(),
        httpClient,
        notificationService: notificationService,
        pluginPath: deployedPath,
        currentVersion: "0.1.0-beta.1");

    await updateService.RefreshForPageLoadAsync(CancellationToken.None);

    Assert.Equal(version, updateService.Status.InstalledVersion,
        "auto-update should stage the advertised DLL version");
    Assert.True(updateService.Status.RestartRequired,
        "staged update should require an IW4MAdmin restart");
    Assert.Null(updateService.Status.InstallError,
        "valid official package should install without an error");
    Assert.True(File.Exists(System.IO.Path.Combine(testDir.Path, "Dragnet.dll.bak-0.1.0-beta.1")),
        "auto-update should back up the deployed DLL");
    var deployedVersion = System.Diagnostics.FileVersionInfo
        .GetVersionInfo(deployedPath)
        .ProductVersion?
        .Split('+', 2)[0];
    Assert.Equal(version, deployedVersion,
        "deployed DLL should match the release version");
    var notification = (await notificationStore.ListAsync(CancellationToken.None)).Single();
    Assert.Equal(DragnetNotificationType.UpdateInstalled, notification.Type,
        "successful auto-update should create an administrator notification");
    Assert.Contains("Restart IW4MAdmin", notification.Message,
        "update notification should explain the required restart");
    Assert.Equal(1, alertManager.Alerts.Count,
        "successful auto-update should create one native IW4MAdmin alert");
    Assert.Contains("Restart IW4MAdmin", alertManager.Alerts.Single().Message,
        "native IW4MAdmin alert should explain the required restart");
    Assert.True(File.Exists(System.IO.Path.Combine(testDir.Path, "update-history.json")),
        "auto-update should persist rollout history beside a custom deployed DLL");
    Assert.True(updateService.History.Any(entry =>
            entry.Stage == DragnetUpdateStage.Staged &&
            entry.Version == version),
        "successful auto-update should record a staged lifecycle event");

    using var restartedUpdateService = new DragnetUpdateService(
        configuration,
        new TestLogger<DragnetUpdateService>(),
        new HttpClient(new StaticResponseHandler(System.Net.HttpStatusCode.OK, "{}")),
        pluginPath: deployedPath,
        currentVersion: version);
    Assert.False(restartedUpdateService.Status.RestartRequired,
        "loading the staged version after restart should clear restart-required state");
    Assert.True(restartedUpdateService.History.Any(entry =>
            entry.Stage == DragnetUpdateStage.Applied &&
            entry.Version == version),
        "loading the staged version after restart should record an applied lifecycle event");
}

static async Task TestHeartbeatValidationAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        MaxEventsPerHeartbeat = 1,
        MaxKnownPeersPerHeartbeat = 1,
        RequireHttps = true
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local");
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    var transport = new DragnetTransportService(
        configuration,
        eventStore,
        peerStore,
        identity,
        identityService,
        reviewService,
        trustService,
        () => 1,
        new TestLogger<DragnetTransportService>());

    await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = new DragnetPeerInfo
        {
            OriginId = "remote",
            OriginName = "Remote",
            PublicEndpoint = "http://remote.example/dragnet"
        }
    }, CancellationToken.None), "http sender endpoint should be rejected");

    await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = new DragnetPeerInfo
        {
            OriginId = "remote",
            OriginName = "Remote",
            PublicEndpoint = "https://remote.example/dragnet"
        },
        KnownPeers =
        [
            new DragnetPeerInfo { OriginId = "p1", OriginName = "P1", PublicEndpoint = "https://p1.example/dragnet" },
            new DragnetPeerInfo { OriginId = "p2", OriginName = "P2", PublicEndpoint = "https://p2.example/dragnet" }
        ]
    }, CancellationToken.None), "known peer limit should be enforced");

    await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = new DragnetPeerInfo
        {
            OriginId = "remote",
            OriginName = "Remote",
            PublicEndpoint = "https://remote.example/dragnet"
        },
        Events =
        [
            CreateEnvelope(originId: "remote", eventType: DragnetEventType.BanCreated),
            CreateEnvelope(originId: "remote", eventType: DragnetEventType.BanLifted)
        ]
    }, CancellationToken.None), "event limit should be enforced");

    await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = new DragnetPeerInfo
        {
            OriginId = "remote",
            OriginName = "Remote",
            PublicEndpoint = "https://remote.example/dragnet"
        },
        AcknowledgedEventIds = ["event-1", "event-2"]
    }, CancellationToken.None), "event acknowledgement limit should be enforced");

    await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = new DragnetPeerInfo
        {
            OriginId = "remote",
            OriginName = "Remote",
            PublicEndpoint = "https://remote.example/dragnet"
        },
        EvidenceUpdates =
        [
            CreateEvidenceUpdate("evidence-1", "event-1"),
            CreateEvidenceUpdate("evidence-2", "event-2")
        ]
    }, CancellationToken.None), "evidence update limit should be enforced");

    await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = new DragnetPeerInfo
        {
            OriginId = "remote",
            OriginName = "Remote",
            PublicEndpoint = "https://remote.example/dragnet"
        },
        BanAttestations =
        [
            CreateBanAttestation("attestation-1", "event-1"),
            CreateBanAttestation("attestation-2", "event-2")
        ]
    }, CancellationToken.None), "ban attestation limit should be enforced");

    await Assert.ThrowsAsync<InvalidOperationException>(() => transport.HandleHeartbeatAsync(new DragnetHeartbeatRequest
    {
        Sender = new DragnetPeerInfo
        {
            OriginId = "remote",
            OriginName = "Remote",
            PublicEndpoint = "https://remote.example/dragnet"
        },
        AttestationRefreshEventIds = ["event-1", "event-2"]
    }, CancellationToken.None), "attestation refresh limit should be enforced");
}

static async Task TestOutboundHeartbeatErrorIncludesBodyAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet",
        PeerFailureThreshold = 1
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    await peerStore.AddManualPeerAsync("https://remote.example/dragnet", null, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local");
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    using var httpClient = new HttpClient(new StaticResponseHandler(
        System.Net.HttpStatusCode.BadRequest,
        "{\"error\":\"Known peer endpoint must be absolute HTTPS.\"}"));
    var transport = new DragnetTransportService(
        configuration,
        eventStore,
        peerStore,
        identity,
        identityService,
        reviewService,
        trustService,
        () => 1,
        new TestLogger<DragnetTransportService>(),
        httpClient);

    transport.Start();
    await Task.Delay(150);
    await transport.StopAsync();

    var peer = (await peerStore.ListAsync(CancellationToken.None)).Single();
    Assert.NotNull(peer.LastError, "failed outbound heartbeat should store peer error");
    Assert.Contains("Known peer endpoint", peer.LastError!, "peer error should include response body");
}

static async Task TestOutboundHeartbeatUsesNumericEnumWireFormatAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet"
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    await peerStore.AddManualPeerAsync("https://remote.example/dragnet", null, CancellationToken.None);
    var identityService = new DragnetIdentityService(System.IO.Path.Combine(testDir.Path, "identity"));
    var identity = identityService.LoadOrCreate("Local");
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    await eventStore.UpsertAsync(new DragnetStoredEvent
    {
        Event = CreateEnvelope(originId: identity.OriginId, eventType: DragnetEventType.BanCreated),
        ReviewState = DragnetReviewState.ApprovedBan
    }, CancellationToken.None);
    var handler = new StaticResponseHandler(
        System.Net.HttpStatusCode.OK,
        "{\"receiver\":{\"originId\":\"remote\",\"originName\":\"Remote\",\"publicEndpoint\":\"https://remote.example/dragnet\"},\"knownPeers\":[],\"events\":[]}");
    using var httpClient = new HttpClient(handler);
    var transport = new DragnetTransportService(
        configuration,
        eventStore,
        peerStore,
        identity,
        identityService,
        reviewService,
        trustService,
        () => 1,
        new TestLogger<DragnetTransportService>(),
        httpClient);

    transport.Start();
    await Task.Delay(150);
    await transport.StopAsync();

    Assert.NotNull(handler.LastRequestBody, "heartbeat should send a request body");
    Assert.Contains("\"eventType\":0", handler.LastRequestBody!, "wire heartbeat should use numeric enum values");
    var peers = await peerStore.ListAsync(CancellationToken.None);
    Assert.Equal(1, peers.Count, "successful heartbeat should reconcile the provisional peer");
    Assert.Equal("remote", peers[0].OriginId, "successful heartbeat should retain the canonical peer identity");
    Assert.Null(peers[0].LastError, "successful heartbeat should clear peer errors");
}

static DragnetHeartbeatRequest CreateHeartbeatRequest(string originId) => new()
{
    Sender = new DragnetPeerInfo
    {
        OriginId = originId,
        OriginName = originId,
        PublicEndpoint = $"https://{originId}.example/dragnet"
    }
};

static byte[] CreateReleasePackage(string tag, string pluginPath)
{
    using var stream = new MemoryStream();
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        var entry = archive.CreateEntry(
            $"Dragnet.IW4MAdmin.Plugin-{tag}/Plugins/Dragnet.dll",
            CompressionLevel.NoCompression);
        using var destination = entry.Open();
        using var source = File.OpenRead(pluginPath);
        source.CopyTo(destination);
    }

    return stream.ToArray();
}

static DragnetPeerInfo CreateSignedPeerInfo(
    DragnetIdentityService identityService,
    DragnetIdentityDocument identity,
    string publicEndpoint,
    bool directoryListed,
    DateTimeOffset? seenAtUtc = null)
{
    var unsigned = new DragnetPeerInfo
    {
        OriginId = identity.OriginId,
        OriginName = identity.OriginName,
        PublicEndpoint = publicEndpoint,
        ServerCount = 3,
        DirectoryListed = directoryListed,
        Region = "Europe",
        Website = "https://listed.example",
        Version = DragnetBuildInfo.Version,
        PublicKeyPem = identity.PublicKeyPem,
        SeenAtUtc = seenAtUtc ?? DateTimeOffset.UtcNow
    };
    return unsigned with
    {
        Signature = identityService.Sign(identity, unsigned.GetSigningPayload())
    };
}

static string CreateSignedHealthResponse(
    DragnetIdentityService identityService,
    DragnetIdentityDocument identity,
    string publicEndpoint)
{
    var unsigned = new DragnetHealthResponse
    {
        Status = "ready",
        Version = DragnetBuildInfo.Version,
        OriginId = identity.OriginId,
        OriginName = identity.OriginName,
        ServerCount = 1,
        PublicEndpoint = publicEndpoint,
        PublicKeyPem = identity.PublicKeyPem,
        CheckedAtUtc = DateTimeOffset.UtcNow
    };
    var signed = unsigned with
    {
        Signature = identityService.Sign(identity, unsigned.GetSigningPayload())
    };
    return System.Text.Json.JsonSerializer.Serialize(signed, DragnetJson.Options);
}

static DragnetEventEnvelope CreateEnvelope(
    string originId,
    DragnetEventType eventType)
{
    var now = DateTimeOffset.UtcNow;
    return new DragnetEventEnvelope
    {
        EventId = $"{originId}-{eventType}-{Guid.NewGuid():N}",
        EventType = eventType,
        OriginId = originId,
        OriginName = originId,
        OriginServerName = "Server",
        OriginPublicKeyPem = "public-key",
        PenaltyKind = DragnetPenaltyKind.Ban,
        Iw4mAdminPenaltyId = 1,
        PlayerNetworkId = "123",
        PlayerGame = "IW4",
        PlayerName = "Player",
        Reason = "Reason",
        CreatedAtUtc = now,
        Signature = "signature"
    };
}

static DragnetEvidenceUpdate CreateEvidenceUpdate(string updateId, string eventId) => new()
{
    UpdateId = updateId,
    EventId = eventId,
    OriginId = "remote",
    OriginName = "Remote",
    OriginPublicKeyPem = "public-key",
    EvidenceUrl = "https://example.test/evidence",
    SubmittedByName = "Admin",
    CreatedAtUtc = DateTimeOffset.UtcNow,
    Signature = "signature"
};

static DragnetBanAttestation CreateBanAttestation(string attestationId, string eventId) => new()
{
    AttestationId = attestationId,
    EventId = eventId,
    NetworkOriginId = "remote",
    NetworkName = "Remote",
    NetworkPublicKeyPem = "public-key",
    ServerCount = 1,
    ServerNames = ["Server"],
    Status = DragnetBanCoverageStatus.Accepted,
    UpdatedAtUtc = DateTimeOffset.UtcNow,
    Signature = "signature"
};

public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}. Expected {expected}, got {actual}.");
        }
    }

    public static void Null(object? value, string message)
    {
        if (value is not null)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void NotNull(object? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Contains(string expected, string actual, string message)
    {
        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{message}. Expected to find '{expected}' in '{actual}'.");
        }
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{message}. Expected {typeof(TException).Name}, got {ex.GetType().Name}.", ex);
        }

        throw new InvalidOperationException($"{message}. Expected {typeof(TException).Name}.");
    }
}

public sealed class RecordingConfigurationHandler<TConfiguration> : IConfigurationHandlerV2<TConfiguration>
    where TConfiguration : class
{
    public int SetCount { get; private set; }

    public string Filename => "test.json";

    public event Action<TConfiguration>? Updated;

    public Task<TConfiguration> Get(string configurationName, TConfiguration defaultConfiguration = null!) =>
        Task.FromResult(defaultConfiguration);

    public Task Set(TConfiguration configuration)
    {
        SetCount++;
        Updated?.Invoke(configuration);
        return Task.CompletedTask;
    }

    public Task Set()
    {
        SetCount++;
        return Task.CompletedTask;
    }
}

public sealed class TestLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
    }
}

public sealed class RecordingAlertManager : IAlertManager
{
    public List<Alert.AlertState> Alerts { get; } = [];

    public EventHandler<Alert.AlertState> OnAlertConsumed { get; set; } = (_, _) => { };

    public Task Initialize() => Task.CompletedTask;

    public IEnumerable<Alert.AlertState> RetrieveAlerts(
        SharedLibraryCore.Database.Models.EFClient client) =>
        Alerts;

    public void AddAlert(Alert.AlertState alert) => Alerts.Add(alert);

    public void MarkAlertAsRead(Guid alertId)
    {
    }

    public void MarkAllAlertsAsRead(int recipientId)
    {
    }

    public void RegisterStaticAlertSource(
        Func<Task<IEnumerable<Alert.AlertState>>> alertSource)
    {
    }
}

public sealed class StaticResponseHandler : HttpMessageHandler
{
    private readonly System.Net.HttpStatusCode _statusCode;
    private readonly string _body;

    public string? LastRequestBody { get; private set; }
    public int RequestCount { get; private set; }

    public StaticResponseHandler(System.Net.HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}

public sealed class RoutingResponseHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responseFactory(request));
}

public sealed class CountingResponseHandler(
    System.Net.HttpStatusCode statusCode,
    string body) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

public sealed class TestDirectory : IAsyncDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"dragnet-tests-{Guid.NewGuid():N}");

    public TestDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class SyncTestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"dragnet-tests-{Guid.NewGuid():N}");

    public SyncTestDirectory()
    {
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

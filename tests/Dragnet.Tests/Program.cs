using Data.Models.Client;
using Dragnet.Configuration;
using Dragnet.Identity;
using Dragnet.Models;
using Dragnet.Services;
using Dragnet.Storage;
using Dragnet.Transport;
using Dragnet.Web;
using Microsoft.Extensions.Logging;
using SharedLibraryCore.Interfaces;

var tests = new (string Name, Func<Task> Test)[]
{
    ("identity service updates configured display name without rotating keys", TestIdentityRenamesWithoutRotatingKeys),
    ("trust service persists and evaluates auto-approval", TestTrustServicePersistsAsync),
    ("review service denies pending ban and blocks untrusted approval", TestReviewTransitionsAsync),
    ("peer store tracks bootstrap, errors, removal, and send cursor", TestPeerStoreAsync),
    ("event store expires elapsed temp bans", TestEventStoreExpiresElapsedTempBansAsync),
    ("import service skips disabled and already imported events", TestImportServiceSkipsAsync),
    ("import service queues unknown players", TestImportServiceQueuesUnknownPlayersAsync),
    ("heartbeat response sends approved events once", TestHeartbeatResponseBatchAsync),
    ("webfront dashboard interaction renders as navigation content", TestWebfrontDashboardRendersAsync),
    ("heartbeat validation rejects oversized and invalid requests", TestHeartbeatValidationAsync),
    ("outbound heartbeat errors include response body", TestOutboundHeartbeatErrorIncludesBodyAsync),
    ("outbound heartbeat uses numeric enum wire format", TestOutboundHeartbeatUsesNumericEnumWireFormatAsync)
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

    var secondPendingBan = CreateEnvelope(originId: "origin-3", eventType: DragnetEventType.BanCreated);
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
        PublicEndpoint = "https://discovered.example/dragnet"
    }, CancellationToken.None);
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

    await store.MarkHeartbeatSucceededAsync("discovered-origin", new DragnetPeerInfo
    {
        OriginId = "canonical-origin",
        OriginName = "Canonical",
        PublicEndpoint = "https://discovered.example/dragnet"
    }, CancellationToken.None);
    var reconciledPeers = await store.ListAsync(CancellationToken.None);
    Assert.False(reconciledPeers.Any(peer => peer.OriginId == "discovered-origin"),
        "successful heartbeat should remove the provisional identity");
    var canonical = reconciledPeers.Single(peer => peer.OriginId == "canonical-origin");
    Assert.Null(canonical.LastError, "successful heartbeat should clear visible errors");
    Assert.Equal(0, canonical.ConsecutiveFailures, "successful heartbeat should reset failure count");

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

static async Task TestWebfrontDashboardRendersAsync()
{
    await using var testDir = new TestDirectory();
    var configuration = new DragnetConfiguration
    {
        PublicEndpoint = "https://local.example/dragnet",
        WebfrontPermission = EFClient.Permission.SeniorAdmin,
        ReviewPermission = EFClient.Permission.Owner,
        TrustPermission = EFClient.Permission.Owner,
        PeerManagementPermission = EFClient.Permission.SeniorAdmin
    };
    var eventStore = new DragnetEventStore(System.IO.Path.Combine(testDir.Path, "events"));
    await eventStore.LoadAsync(CancellationToken.None);
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
    var peerStore = new DragnetPeerStore(System.IO.Path.Combine(testDir.Path, "peers"));
    await peerStore.LoadAsync(configuration, CancellationToken.None);
    var trustService = new DragnetTrustService(configuration, new RecordingConfigurationHandler<DragnetConfiguration>());
    var importService = new DragnetImportService(
        configuration,
        eventStore,
        managerFactory: () => null!,
        logger: new TestLogger<DragnetImportService>());
    var reviewService = new DragnetReviewService(eventStore, importService, trustService);
    var webfront = new DragnetWebfrontService(
        configuration,
        eventStore,
        peerStore,
        reviewService,
        trustService,
        managerFactory: () => null!);
    var interaction = await webfront.CreateNavigationInteractionAsync(CancellationToken.None);

    Assert.Equal(InteractionType.TemplateContent, interaction.InteractionType, "dashboard should render as an IW4MAdmin navigation page");
    Assert.Equal(2, (int)interaction.InteractionType, "dashboard interaction type should match IW4MAdmin script nav pages");
    Assert.Equal(EFClient.Permission.SeniorAdmin, interaction.MinimumPermission, "dashboard should use configured webfront permission");

    var reviewInteraction = await webfront.CreateReviewInteractionAsync(CancellationToken.None);
    var trustInteraction = await webfront.CreateTrustInteractionAsync(CancellationToken.None);
    var peerInteraction = await webfront.CreatePeerInteractionAsync(CancellationToken.None);
    Assert.Equal(EFClient.Permission.Owner, reviewInteraction.MinimumPermission, "review action should use configured review permission");
    Assert.Equal(EFClient.Permission.Owner, trustInteraction.MinimumPermission, "trust action should use configured trust permission");
    Assert.Equal(EFClient.Permission.SeniorAdmin, peerInteraction.MinimumPermission, "peer action should use configured peer permission");

    var html = await interaction.Action(0, null, null, new Dictionary<string, string>
    {
        ["eventId"] = legacyEvent.EventId
    }, CancellationToken.None);
    Assert.Contains("Peer transport", html, "dashboard should include peer section");
    Assert.Contains("Dragnet events", html, "dashboard should include event section");
    Assert.Contains("Unknown", html, "dashboard should render unknown legacy timestamps and penalty ids without huge ages");
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

public sealed class StaticResponseHandler : HttpMessageHandler
{
    private readonly System.Net.HttpStatusCode _statusCode;
    private readonly string _body;

    public string? LastRequestBody { get; private set; }

    public StaticResponseHandler(System.Net.HttpStatusCode statusCode, string body)
    {
        _statusCode = statusCode;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
        };
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

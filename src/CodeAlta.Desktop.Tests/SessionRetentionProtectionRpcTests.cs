using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SessionRetentionProtectionRpcTests
{
    [TestMethod]
    public void SessionRetention_CompositionKeepsUnqualifiedExternalOwnerUnknown()
    {
        var protection = DesktopSessionRetention.CreateProtection();
        protection.Publish(SessionRetentionProtectionService.Owner, [], true);
        Assert.IsNull(protection.TryReserveRetirement("otherwise-old"), "A restored page does not certify external-work ownership.");
    }

    [TestMethod]
    public void SessionRetention_GlobalPolicyReadsOnlyIsolatedRootAndFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-retention-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            var now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
            Assert.AreEqual(now.AddMonths(-3), DesktopSessionRetention.ReadCutoff(config, now));
            File.WriteAllText(Path.Combine(root, "config.toml"), "[storage.session_retention]\nenabled = false\namount = 1\nunit = 'months'\n");
            Assert.IsNull(DesktopSessionRetention.ReadCutoff(config, now));
            File.WriteAllText(Path.Combine(root, "config.toml"), "invalid = [\n");
            Assert.AreEqual(now, DesktopSessionRetention.ReadCutoff(config, now), "Invalid configuration must not become an enabled default.");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task SessionRetention_AllSpaceClaimsAreEpochBoundMonotonicAndNotReadiness()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants([SessionRetentionProtectionService.Owner]);
        var rpc = new SessionRetentionProtectionService(protection, "epoch");
        Assert.IsNull(protection.TryReserveRetirement("closed"));
        var first = new SessionRetentionProtectionRequest("epoch", "page-a", 1, ["hidden-space", "draft-only"], false);
        Assert.IsTrue((await rpc.PublishAsync(first, default)).Accepted);
        Assert.IsFalse((await rpc.PublishAsync(first, default)).Accepted, "Stale revisions are not authorization.");
        Assert.IsFalse((await rpc.PublishAsync(first with { ExpectedEpoch = "old", Revision = 2 }, default)).Accepted);
        Assert.IsFalse((await rpc.PublishAsync(first with { Revision = 2, Ready = true }, default)).Accepted,
            "Effect snapshots cannot claim full admission readiness.");
        Assert.IsFalse((await rpc.PublishAsync(first with { PageId = "page-b", SessionIds = [] }, default)).Accepted,
            "A different renderer cannot replace an active page without the native lifecycle boundary.");
        rpc.InvalidatePage();
        Assert.IsTrue((await rpc.PublishAsync(first with { PageId = "page-b", SessionIds = [] }, default)).Accepted);
        Assert.IsNull(protection.TryReserveRetirement("closed"), "Reload/disconnect never expires unknown protection.");
    }

    [TestMethod]
    public async Task SessionRetention_RacingClaimCannotOpenAnAlreadyReservedSession()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants([SessionRetentionProtectionService.Owner]);
        var rpc = new SessionRetentionProtectionService(protection, "epoch");
        protection.Publish(SessionRetentionProtectionService.Owner, [], true); // fixture-only qualified owner
        using var retirement = protection.TryReserveRetirement("old");
        Assert.IsNotNull(retirement);
        var result = await rpc.PublishAsync(new("epoch", "page", 1, ["old"], false), default);
        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("session_retiring", result.Reason);
    }

    [TestMethod]
    public async Task SessionRetention_InvalidIdsAndUnavailableHostFailClosed()
    {
        Assert.IsFalse((await new SessionRetentionProtectionService().PublishAsync(new("epoch", "page", 1, [], false), default)).Accepted);
        var protection = new SessionRetentionProtection();
        var rpc = new SessionRetentionProtectionService(protection, "epoch");
        Assert.IsFalse((await rpc.PublishAsync(new("epoch", "page", 1, [" old "], false), default)).Accepted);
    }

    [TestMethod]
    public async Task SessionRetention_RegisteredWindowsRestoreIndependentlyAndDisconnectRetainsClaims()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["window-a", "window-b"]);
        SessionRetentionProtectionService Window(string owner) => new("epoch",
            (ids, ready) => protection.Publish(owner, ids, ready), reason => protection.MarkUnknown(owner, reason));
        var first = Window("window-a");
        var second = Window("window-b");
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        await using var firstLifetime = first.WatchLifetime(new("epoch", "page-a"), "document-a", firstCancellation.Token).GetAsyncEnumerator();
        await using var secondLifetime = second.WatchLifetime(new("epoch", "page-b"), "document-b", secondCancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await firstLifetime.MoveNextAsync());
        Assert.IsTrue(firstLifetime.Current.Accepted);
        Assert.IsTrue(await secondLifetime.MoveNextAsync());
        Assert.IsTrue(secondLifetime.Current.Accepted);
        Assert.IsTrue((await first.PublishForDocumentAsync(new("epoch", "page-a", 1, [], false), "document-a", default)).Accepted);
        Assert.IsNull(protection.TryReserveRetirement("closed"), "Connection/inventory alone does not finish restoration.");
        Assert.IsTrue((await first.PublishForDocumentAsync(new("epoch", "page-a", 2, ["draft"], true), "document-a", default)).Accepted);
        Assert.IsNull(protection.TryReserveRetirement("closed"), "A restored window cannot authorize another unknown window.");
        Assert.IsTrue((await second.PublishForDocumentAsync(new("epoch", "page-b", 1, ["hidden-space"], true), "document-b", default)).Accepted);
        using (var closed = protection.TryReserveRetirement("closed")) Assert.IsNotNull(closed, "Cleanup remains functional for unprotected sessions.");
        Assert.IsNull(protection.TryReserveRetirement("draft"));
        Assert.IsNull(protection.TryReserveRetirement("hidden-space"));
        using (var eligible = protection.TryReserveRetirement("eligible"))
        {
            Assert.IsNotNull(eligible);
            Assert.IsFalse((await first.PublishForDocumentAsync(new("epoch", "page-a", 3, ["eligible"], true), "document-a", default)).Accepted,
                "An acknowledged restoration is not a bypass around the retirement reservation.");
        }
        second.InvalidatePage();
        Assert.IsNull(protection.TryReserveRetirement("closed"));
        Assert.IsFalse((await second.PublishForDocumentAsync(new("epoch", "page-b", 2, [], true), "document-b", default)).Accepted,
            "Late callbacks from the disconnected renderer cannot restore readiness.");
        await using var replacement = second.WatchLifetime(new("epoch", "replacement"), "replacement-document", secondCancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await replacement.MoveNextAsync());
        Assert.IsTrue(replacement.Current.Accepted);
        await secondLifetime.DisposeAsync();
        Assert.IsTrue((await second.PublishForDocumentAsync(new("epoch", "replacement", 1, [], true), "replacement-document", default)).Accepted);
        Assert.IsFalse((await second.PublishForDocumentAsync(new("epoch", "replacement", 2, [], true), "document-b", default)).Accepted,
            "A replaced document cannot reuse the replacement page's claimed identity.");
        Assert.IsNull(protection.TryReserveRetirement("hidden-space"), "Page replacement never releases the preceding page's IDs.");
        Assert.IsNull(protection.TryReserveRetirement("eligible"), "Replacement cannot restart the single startup pass's readiness.");
    }

    [TestMethod]
    public async Task SessionRetention_FirstUnpublishedOldPageAndAmbiguityCannotFinishReady()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["window"]);
        var rpc = new SessionRetentionProtectionService("epoch", (ids, ready) => protection.Publish("window", ids, ready),
            reason => protection.MarkUnknown("window", reason));
        rpc.InvalidatePage(); // A native replacement happens before the old page's very first queued RPC.
        Assert.IsFalse((await rpc.PublishForDocumentAsync(new("epoch", "unpublished-old-page", 1, [], true), "old-document", default)).Accepted);
        Assert.IsNull(protection.TryReserveRetirement("old"));

        var ambiguous = new SessionRetentionProtectionService("epoch", (ids, ready) => protection.Publish("window", ids, ready),
            reason => protection.MarkUnknown("window", reason));
        using var cancellation = new CancellationTokenSource();
        await using var lifetime = ambiguous.WatchLifetime(new("epoch", "page"), "document", cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await lifetime.MoveNextAsync());
        Assert.IsTrue((await ambiguous.PublishForDocumentAsync(new("epoch", "page", 1, ["recoverable"], false), "document", default)).Accepted);
        Assert.IsNull(protection.TryReserveRetirement("old"));
    }

    [TestMethod]
    public async Task SessionRetention_TrustedLifetimeIsRequiredAndCancellationRevokesBeforeChannelDrain()
    {
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["window"]);
        var rpc = new SessionRetentionProtectionService("epoch", (ids, ready) => protection.Publish("window", ids, ready),
            reason => protection.MarkUnknown("window", reason));
        var claim = new SessionRetentionProtectionRequest("epoch", "page", 1, ["draft"], true);
        Assert.IsFalse((await rpc.PublishForDocumentAsync(claim, "document", default)).Accepted);
        using var cancellation = new CancellationTokenSource();
        var stream = rpc.WatchLifetime(new("epoch", "page"), "document", default);
        Assert.IsFalse((await rpc.PublishForDocumentAsync(claim, "document", default)).Accepted, "An unstarted channel is not a lifetime acknowledgement.");
        await using var enumerator = stream.GetAsyncEnumerator(cancellation.Token);
        Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.IsTrue(enumerator.Current.Accepted);
        await using var competing = rpc.WatchLifetime(new("epoch", "other-page"), "other-document", default).GetAsyncEnumerator();
        Assert.IsTrue(await competing.MoveNextAsync());
        Assert.IsFalse(competing.Current.Accepted, "A second page cannot replace an active authenticated watch.");
        Assert.IsFalse((await rpc.PublishForDocumentAsync(claim, "different-document", default)).Accepted);
        Assert.IsTrue((await rpc.PublishForDocumentAsync(claim, "document", default)).Accepted);
        using (var eligible = protection.TryReserveRetirement("old")) Assert.IsNotNull(eligible);
        cancellation.Cancel();
        Assert.IsNull(protection.TryReserveRetirement("old"), "Do not wait for stream/resource disposal to mark unknown.");
        Assert.IsFalse((await rpc.PublishForDocumentAsync(claim with { Revision = 2 }, "document", default)).Accepted);
    }
}

using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>Unadmitted fixtures. Only the two named real methods create fresh owned roots and a fake-only host.</summary>
[TestClass]
public sealed class DesktopOwnedSessionTests
{
    [TestMethod]
    public void ImageOnlySend_ForwardsEmptyTextAndExactLocalMetadata()
    {
        OwnedTextSendRequest? captured = null;
        var service = new SessionOperationsService("epoch", request => { captured = request; return new(OwnedSessionCommandAdmissionKind.Busy); },
            _ => throw new AssertFailedException("Unexpected abort"));
        var request = new SessionSendRequest("epoch", "key", "session", "")
        {
            Selection = new("provider", "default", "image-model", null),
            Images = [new("Local / title", "image/png", "AA==")],
        };
        Assert.AreEqual("busy", service.Send(request, CancellationToken.None).Status);
        Assert.AreEqual("", captured!.Text);
        Assert.AreEqual("Local / title", captured.Images![0].Title);
        Assert.AreEqual("AA==", captured.Images[0].Base64);
        captured = null;
        Assert.AreEqual("invalid_request", service.Send(request with { Text = " \r\n" }, CancellationToken.None).Status);
        Assert.AreEqual("invalid_request", service.Send(request with { Images = [] }, CancellationToken.None).Status);
        Assert.AreEqual("stale_epoch", service.Send(request with { ExpectedEpoch = "old" }, CancellationToken.None).Status);
        Assert.IsNull(captured);
    }

    [TestMethod]
    public async Task ReferenceContractsRefuseUnownedAndStaleAndFreezeExpectedScope()
    {
        OwnedTextSendRequest? captured = null;
        var service = new SessionOperationsService("epoch", request => { captured = request; return new(OwnedSessionCommandAdmissionKind.Busy); },
            _ => throw new AssertFailedException("Unexpected abort"));
        var request = new SessionSendRequest("epoch", "key", "session", "@file") { References = new("project", "/project") };
        Assert.AreEqual("busy", service.Send(request, CancellationToken.None).Status);
        Assert.AreEqual(new OwnedProjectReferenceScope("project", "/project"), captured!.References);
        Assert.AreEqual("invalid_request", service.Send(request with { References = new("", "/project") }, CancellationToken.None).Status);
        var search = new SessionReferenceSearchRequest("epoch", "project", "/project", null, "file");
        Assert.AreEqual("unconfigured", (await new SessionOperationsService().SearchReferencesAsync(search, CancellationToken.None)).Status);
        // Stopping a background task is checked like every request of the page before anything is asked.
        Assert.AreEqual("unconfigured", (await new SessionOperationsService().StopBackgroundTaskAsync(new("epoch", "session", "b1"), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.StopBackgroundTaskAsync(new("another", "session", "b1"), CancellationToken.None)).Status);
        foreach (var refused in new SessionStopBackgroundTaskRequest[] { new("epoch", " ", "b1"), new("epoch", "session", ""), new("epoch", "session", " b1"), new("epoch", "session", new string('x', 257)) })
            Assert.AreEqual("invalid_request", (await service.StopBackgroundTaskAsync(refused, CancellationToken.None)).Status);
        // Remote Control is checked the same way, and a host that owns no session has none.
        Assert.AreEqual("unconfigured", (await new SessionOperationsService().SetRemoteControlAsync(new("epoch", "session", true), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.SetRemoteControlAsync(new("another", "session", true), CancellationToken.None)).Status);
        foreach (var refused in new SessionRemoteControlRequest[] { new("epoch", " ", true), new("epoch", " session", true), new("epoch", new string('x', 257), false) })
            Assert.AreEqual("invalid_request", (await service.SetRemoteControlAsync(refused, CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await service.SetRemoteControlAsync(new("epoch", "session", true), CancellationToken.None)).Status);
        // A page whose host owns no session has no task to stop.
        Assert.AreEqual(("unavailable", "session"), ((await service.StopBackgroundTaskAsync(new("epoch", "session", "b1"), CancellationToken.None)).Status, "session"));
        Assert.AreEqual("stale_epoch", (await service.SearchReferencesAsync(search with { ExpectedEpoch = "old" }, CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await service.SearchReferencesAsync(search, CancellationToken.None)).Status);
        var observation = new SessionReferenceObservationRequest("epoch", "project", "/project", null, "@file");
        Assert.AreEqual("unconfigured", (await new SessionOperationsService().ObserveReferencesAsync(observation, CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.ObserveReferencesAsync(observation with { ExpectedEpoch = "old" }, CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await service.ObserveReferencesAsync(observation, CancellationToken.None)).Status);
        Assert.AreEqual("invalid_request", (await service.ObserveReferencesAsync(observation with { Text = new string('x', 32769) }, CancellationToken.None)).Status);
    }

    [TestMethod]
    public void SelectedSend_ValidatesAndForwardsExactConfiguration()
    {
        OwnedTextSendRequest? captured = null;
        var service = new SessionOperationsService("epoch", request =>
        {
            captured = request;
            return new(OwnedSessionCommandAdmissionKind.Busy);
        }, _ => throw new AssertFailedException("Unexpected abort"));
        var selection = new SessionSelection("provider", "plan", "model", "High");
        var request = new SessionSendRequest("epoch", "key", "session", "text") { Selection = selection };
        Assert.AreEqual("busy", service.Send(request, CancellationToken.None).Status);
        Assert.AreEqual(new OwnedSessionSelection("provider", "plan", "model", AgentReasoningEffort.High), captured!.Selection);
        // The mode goes to the host as it is: null keeps the session's, "provider" goes back to the provider's.
        foreach (var mode in new[] { "acceptEdits", OwnedSessionSelection.ProviderPermissionMode })
        {
            Assert.AreEqual("busy", service.Send(request with { ClientRequestId = mode, Selection = selection with { PermissionMode = mode } }, CancellationToken.None).Status);
            Assert.AreEqual(mode, captured!.Selection!.PermissionMode);
        }

        captured = null;
        foreach (var invalid in new[] { selection with { ReasoningEffort = "999" }, selection with { ReasoningEffort = "1" },
            selection with { AgentPromptId = "../bad\ud800" }, selection with { ModelId = new string('x', 257) },
            selection with { PermissionMode = " auto" }, selection with { PermissionMode = "" }, selection with { PermissionMode = new string('x', 257) } })
            Assert.AreEqual("invalid_request", service.Send(request with { Selection = invalid }, CancellationToken.None).Status);
        Assert.IsNull(captured);
        Assert.AreEqual("stale_epoch", service.Send(request with { ExpectedEpoch = "old" }, CancellationToken.None).Status);
        Assert.IsNull(captured);
    }

    [TestMethod]
    public Task PermissionMode_ChoicesNameTheModesOfTheProvider_AndASendGivesOneToTheSession() => RealFixture.RunAsync(async f =>
    {
        f.Host.ModelProviderRegistry.RegisterOrReplace(f.Provider.Descriptor with
            { PermissionModes = ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"], DefaultPermissionMode = "plan" },
            () => new FakeRuntime(f.Provider));
        f.Provider.Release.TrySetResult();
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");

        var choices = await f.Wait(f.Keep(service.Choices(new("fixture-epoch", f.SessionId), CancellationToken.None)));
        Assert.AreEqual("ok", choices.Status);
        // Plan stays a mode of the provider's configuration.
        CollectionAssert.AreEqual(new[] { "default", "acceptEdits", "auto", "dontAsk", "bypassPermissions" }, choices.PermissionModes!.Select(mode => mode.Id).ToArray());
        Assert.AreEqual("plan", choices.DefaultPermissionMode);
        Assert.IsNull(choices.Current!.PermissionMode);

        async Task<OwnedSessionCommandResult> SendAsync(string key, string mode)
        {
            // No model keeps the one of the session.
            var selection = new SessionSelection("owned-fixture", "default", null, null) { PermissionMode = mode };
            Assert.AreEqual("accepted", service.Send(new("fixture-epoch", key, f.SessionId, "text") { Selection = selection }, CancellationToken.None).Status);
            var receipt = f.Retain(f.Host.Commands.AdmitSend(new(key, f.SessionId, "text") { Selection = new("owned-fixture", "default", null, null) { PermissionMode = mode } }));
            var result = await f.Wait(receipt.Completion);
            if (result.Outcome != OwnedSessionCommandOutcome.Completed) return result;
            var marker = Guid.NewGuid().ToString("N");
            f.Provider.EmitIdle!(marker);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await f.Keep(Committed());
            return result;

            async Task Committed()
            {
                await foreach (var value in f.Host.RuntimeService.StreamEventsAsync(deadline.Token))
                    if (value is SessionAgentEvent { Event: AgentSessionUpdateEvent update }
                        && update.Kind == AgentSessionUpdateKind.Idle && update.Message == marker) return;
                Assert.Fail("Idle marker was not committed.");
            }
        }

        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await SendAsync("accept", "acceptEdits")).Outcome);
        Assert.AreEqual("acceptEdits", f.Provider.PermissionMode);
        Assert.AreEqual("acceptEdits", (await f.Wait(f.Keep(service.Choices(new("fixture-epoch", f.SessionId), CancellationToken.None)))).Current!.PermissionMode);
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, (await SendAsync("plan", "plan")).Outcome);
        Assert.AreEqual("acceptEdits", f.Provider.PermissionMode);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await SendAsync("provider", OwnedSessionSelection.ProviderPermissionMode)).Outcome);
        Assert.IsNull(f.Provider.PermissionMode);
        Assert.IsNull((await f.Wait(f.Keep(service.Choices(new("fixture-epoch", f.SessionId), CancellationToken.None)))).Current!.PermissionMode);
    });

    [TestMethod]
    public async Task SelectionChoices_RejectsUnconfiguredAndStaleHosts()
    {
        Assert.AreEqual("unconfigured", (await new SessionOperationsService().Choices(new("epoch", "session"), CancellationToken.None)).Status);
        var service = new SessionOperationsService("epoch", _ => throw new AssertFailedException("Unexpected send"),
            _ => throw new AssertFailedException("Unexpected abort"));
        Assert.AreEqual("stale_epoch", (await service.Choices(new("old", "session"), CancellationToken.None)).Status);
        Assert.AreEqual("invalid_request", (await service.Choices(new("epoch", " session"), CancellationToken.None)).Status);
    }

    [TestMethod]
    public void OwnedFlags_RequireCompleteExplicitConsentBeforeAcquisition()
    {
        var root = OperatingSystem.IsWindows() ? @"Q:\owned" : "/owned";
        var args = new[] { "--data-root", root + "/browser", "--catalog-root", root + "/copy", "--allow-catalog-cache",
            "--allow-owned-host", "--project-root", root + "/project", "--discovery-home", root + "/home",
            "--instruction-root", root, "--builtin-skill-root", root + "/builtin" };
        bool Exists(string path) => !path.EndsWith("browser", StringComparison.Ordinal);
        Assert.IsTrue(DesktopCommandLine.TryParse(args, Exists, _ => false, out var options, out _));
        Assert.IsNotNull(options!.Owned);
        Assert.IsFalse(DesktopCommandLine.TryParse(args.Where(value => value != "--allow-owned-host").ToArray(), Exists, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse(args[..^2], Exists, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse([.. args, "--allow-owned-host"], Exists, _ => false, out _, out _));
    }

    [TestMethod]
    public void LegacyBranches_DoNotComposeOwnedHost()
    {
        var root = OperatingSystem.IsWindows() ? @"Q:\literal" : "/literal";
        Assert.IsTrue(DesktopCommandLine.TryParse(["--data-root", root], _ => false, _ => false, out var options, out _));
        Assert.IsNull(options!.Owned);
        var disabled = new SessionOperationsService();
        Assert.AreEqual("unconfigured", disabled.Send(new("epoch", "key", "session", "text"), CancellationToken.None).Status);
        Assert.AreEqual("unconfigured", disabled.Receipts(new("epoch", 0)).Status);
    }

    [TestMethod]
    public void StaleEpoch_RejectsBeforeOwnerAdmission()
    {
        var service = new SessionOperationsService("current", _ => throw new AssertFailedException("Admission called"),
            _ => throw new AssertFailedException("Abort called"));
        Assert.AreEqual("stale_epoch", service.Send(new("old", "key", "session", "text"), CancellationToken.None).Status);
        Assert.AreEqual("stale_epoch", service.Abort(new("old", "key", Guid.NewGuid().ToString("D")), CancellationToken.None).Status);
        var canonical = "abcdefab-1234-5678-9abc-abcdefabcdef";
        foreach (var target in new[] { " " + canonical, canonical + " ", new string(' ', 7000) + canonical, canonical.ToUpperInvariant() })
            Assert.AreEqual("invalid_request", service.Abort(new("current", "key", target), CancellationToken.None).Status);
        foreach (var text in new[] { new string('x', 32769), "\ud800", "\udc00", "x\ud800x" })
            Assert.AreEqual("invalid_request", service.Send(new("current", "key", "session", text), CancellationToken.None).Status);
        foreach (var identity in new[] { new string('x', 257), "\ud800", "\udc00" })
        {
            Assert.AreEqual("invalid_request", service.Send(new("current", identity, "session", "text"), CancellationToken.None).Status);
            Assert.AreEqual("invalid_request", service.Send(new("current", "key", identity, "text"), CancellationToken.None).Status);
        }
    }

    [TestMethod]
    public Task Admission_RetainsReceiptAndPreservesOwnerReplay() => RealFixture.RunAsync(async f =>
    {
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        var request = new SessionSendRequest("fixture-epoch", "same", f.SessionId, "exact text");
        var accepted = service.Send(request, CancellationToken.None);
        // Retain the owner's replay completion before any assertion or page projection can fail.
        var receipt = f.Retain(f.Host.Commands.AdmitSend(new("same", f.SessionId, "exact text")));
        var page = service.Receipts(new("fixture-epoch", 0));
        Assert.AreEqual("accepted", accepted.Status);
        Assert.HasCount(1, page.Rows);
        Assert.AreEqual(accepted.Receipt!.OperationId, page.Rows[0].OperationId);
        Assert.AreEqual("replay", service.Send(request, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Send(request with { Text = "changed" }, CancellationToken.None).Status);
        await f.Ready(receipt);
        f.Provider.Release.TrySetResult();
        await f.Wait(receipt.Completion);
        Assert.AreEqual("Completed", service.Receipts(new("fixture-epoch", 0)).Rows[0].Outcome);
    });

    [TestMethod]
    public Task ReceiptPages_ShowWhatTheHostKeeps_ThePendingCommandsFirstThenTheMostRecent() => RealFixture.RunAsync(async f =>
    {
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        var capacity = f.Host.Commands.ReceiptCapacity;
        Assert.IsTrue(capacity % 64 == 0 && capacity >= 128, "The pages below assume whole pages.");
        var sent = service.Send(new("fixture-epoch", "send", f.SessionId, "text"), CancellationToken.None);
        var send = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "text")));
        Assert.AreEqual("accepted", sent.Status);
        await f.Ready(send);
        var target = sent.Receipt!.OperationId;

        // More controls of the pending send than the host keeps receipts. Each one settles; the send stays pending.
        var count = capacity + 44;
        for (var index = 0; index < count; index++)
        {
            Assert.AreEqual("accepted", service.Abort(new("fixture-epoch", "abort-" + index, target), CancellationToken.None).Status, "abort-" + index);
            if (index == 0) await f.Wait(f.Retain(f.Host.Commands.AdmitAbort(new("abort-0", send.OperationId))).Completion);
        }

        List<SessionReceiptView> Pages()
        {
            var rows = new List<SessionReceiptView>();
            for (int? offset = 0; offset is { } at;)
            {
                var page = service.Receipts(new("fixture-epoch", at));
                Assert.AreEqual("ok", page.Status);
                Assert.IsTrue(page.Next is null || page.Rows.Length == 64 && page.Next == at + 64);
                rows.AddRange(page.Rows);
                offset = page.Next;
            }

            return rows;
        }

        // The pending send is first, although it is the oldest: it is what can still be acted on. The settled
        // controls follow, the most recent first, and the oldest of them made room.
        var kept = Pages();
        Assert.AreEqual(capacity, kept.Count);
        Assert.AreEqual(("send", "pending"), (kept[0].ClientRequestId, kept[0].State));
        CollectionAssert.AreEqual(Enumerable.Range(0, capacity - 1).Select(index => "abort-" + (count - 1 - index)).ToArray(),
            kept.Skip(1).Select(static row => row.ClientRequestId).ToArray());
        Assert.IsTrue(kept.Skip(1).All(static row => row.State == "terminal"));
        Assert.AreEqual(64, service.Receipts(new("fixture-epoch", 0)).Next);

        // A key whose receipt made room is not run again, and one that is kept is answered with its receipt.
        Assert.AreEqual(("expired", (SessionReceiptView?)null), Answer(service.Abort(new("fixture-epoch", "abort-0", target), CancellationToken.None)));
        Assert.AreEqual("replay", service.Abort(new("fixture-epoch", "abort-" + (count - 1), target), CancellationToken.None).Status);
        Assert.AreEqual(capacity, Pages().Count);

        // Once it has settled, the send is the oldest of the settled commands: the last one.
        f.Provider.Release.TrySetResult();
        await f.Wait(send.Completion);
        kept = Pages();
        Assert.AreEqual(capacity, kept.Count);
        Assert.AreEqual("abort-" + (count - 1), kept[0].ClientRequestId);
        Assert.AreEqual(("send", "terminal"), (kept[^1].ClientRequestId, kept[^1].State));
        foreach (var offset in new[] { -64, 1, capacity + 64 })
            Assert.AreEqual("invalid_cursor", service.Receipts(new("fixture-epoch", offset)).Status);

        static (string Status, SessionReceiptView? Receipt) Answer(SessionAdmission admission) => (admission.Status, admission.Receipt);
    });

    [TestMethod]
    public void ReceiptPages_BoundWorstCaseGeneratedJson()
    {
        var escaped = new string('\u0001', 256);
        var row = new SessionReceiptView(escaped, escaped, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            "Send", "terminal", "Completed", new string('\u0001', 64), escaped);
        var epoch = new string('\u0001', 64);
        var page = SessionOperationsService.ProjectPage(epoch, Enumerable.Repeat(row, 64).ToArray(), 64);
        Assert.AreEqual("ok", page.Status);
        Assert.HasCount(64, page.Rows);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(page, DesktopJsonContext.Default.SessionReceiptPage);
        // NeoRpcHost.SendSuccessResultAsync emits neoastra/kind/id/ok/value. The default
        // request ID bound is 128 ASCII units; allow six-byte escaping plus 128 syntax bytes.
        const int resultEnvelope = 128 * 6 + 128;
        Assert.IsTrue(bytes.Length + resultEnvelope <= 64 * 6400 + 8192);
        Assert.IsTrue(bytes.Length + resultEnvelope <= 448 * 1024);
        var request = new SessionSendRequest(epoch, escaped, escaped, new string('\u0001', 32768));
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, DesktopJsonContext.Default.SessionSendRequest);
        // An 8192-byte allowance covers request ID, method, generated contract hash and syntax.
        Assert.IsTrue(requestBytes.Length + 8192 <= 208 * 1024);
        var unicode = row with { SessionId = string.Concat(Enumerable.Repeat("\ud83d\ude00", 128)) };
        Assert.AreEqual("ok", SessionOperationsService.ProjectPage(epoch, [unicode], null).Status);
        foreach (var invalid in new[] { row with { OperationId = " " + row.OperationId }, row with { TargetOperationId = row.TargetOperationId + new string(' ', 7000) },
            row with { OperationId = "ABCDEFAB-1234-5678-9ABC-ABCDEFABCDEF" }, row with { SessionId = escaped + "x" },
            row with { SessionId = "\ud800" }, row with { Code = "\udc00" }, row with { RunId = escaped + "x" } })
            Assert.AreEqual("wire_limit", SessionOperationsService.ProjectPage(epoch, [invalid], null).Status);
        Assert.AreEqual("wire_limit", SessionOperationsService.ProjectPage("epoch", Enumerable.Repeat(row, 65).ToArray(), null).Status);
    }

    [TestMethod]
    public void SteerRpc_RejectsBeforeAdmissionAndBoundsGeneratedJson()
    {
        var service = new SessionOperationsService("current", _ => throw new AssertFailedException("Send called"),
            _ => throw new AssertFailedException("Abort called"), _ => throw new AssertFailedException("Steer called"));
        var request = new SessionSteerRequest("current", "key", "session", "abcdefab-1234-5678-9abc-abcdefabcdef", "1", "run", "text");
        Assert.AreEqual("unconfigured", new SessionOperationsService().Steer(request, CancellationToken.None).Status);
        Assert.AreEqual("stale_epoch", service.Steer(request with { ExpectedEpoch = "old" }, CancellationToken.None).Status);
        foreach (var invalid in new[] { request with { ExpectedRuntimeInstanceId = request.ExpectedRuntimeInstanceId.ToUpperInvariant() },
            request with { ExpectedRuntimeInstanceId = Guid.Empty.ToString("D") }, request with { ExpectedAttachmentGeneration = "01" },
            request with { ExpectedAttachmentGeneration = "0" }, request with { ExpectedAttachmentGeneration = "9223372036854775808" },
            request with { ExpectedRunId = "" }, request with { ExpectedRunId = " run" }, request with { Text = "\ud800" },
            request with { Text = new string('x', 32769) }, request with { ClientRequestId = new string('x', 257) } })
            Assert.AreEqual("invalid_request", service.Steer(invalid, CancellationToken.None).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Steer(request, cancelled.Token));
        service.CloseAdmission();
        Assert.AreEqual("closed", service.Steer(request, CancellationToken.None).Status);
        var escaped = new string('\u0001', 256);
        var maximum = request with { ExpectedEpoch = new string('\u0001', 64), ClientRequestId = escaped, SessionId = escaped,
            ExpectedRunId = escaped, ExpectedAttachmentGeneration = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), Text = new string('\u0001', 32768) };
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(maximum, DesktopJsonContext.Default.SessionSteerRequest).Length + 8192 <= 208 * 1024);
        var row = new SessionReceiptView("key", "session", Guid.NewGuid().ToString("D"), null, "Steer", "terminal", "Completed", null, "run");
        Assert.AreEqual("ok", SessionOperationsService.ProjectPage("epoch", [row], null).Status);
    }

    [TestMethod]
    public Task SteerRpc_ActualOwnedRouteRetainsReplayAndKind() => RealFixture.RunAsync(async f =>
    {
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        service.Send(new("fixture-epoch", "send", f.SessionId, "text"), CancellationToken.None);
        var send = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "text")));
        await f.Ready(send);
        f.Provider.Release.TrySetResult();
        await f.Wait(send.Completion);
        var state = await f.Wait(f.Keep(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId)));
        var request = new SessionSteerRequest("fixture-epoch", "steer", f.SessionId, state.RuntimeInstanceId.ToString("D"),
            state.Entry!.AttachmentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture), state.Entry.ActiveRunId!, "exact text");
        var admitted = service.Steer(request, CancellationToken.None);
        var steer = f.Retain(f.Host.Commands.AdmitSteer(new("steer", f.SessionId, state.RuntimeInstanceId,
            state.Entry.AttachmentGeneration, request.ExpectedRunId, request.Text)));
        Assert.AreEqual("accepted", admitted.Status);
        Assert.AreEqual("replay", service.Steer(request, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Steer(request with { Text = "changed" }, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Send(new("fixture-epoch", "steer", f.SessionId, "exact text"), CancellationToken.None).Status);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Wait(steer.Completion)).Outcome);
        var row = service.Receipts(new("fixture-epoch", 0)).Rows.Single(value => value.ClientRequestId == "steer");
        Assert.AreEqual("Steer", row.Kind);
        Assert.AreEqual(request.ExpectedRunId, row.RunId);
    });

    [TestMethod]
    public void CompactRpc_RejectsBeforeAdmissionAndBoundsGeneratedJson()
    {
        var service = new SessionOperationsService("current", _ => throw new AssertFailedException("Send called"),
            _ => throw new AssertFailedException("Abort called"), null, _ => throw new AssertFailedException("Compact called"));
        var request = new SessionCompactRequest("current", "key", "session", "abcdefab-1234-5678-9abc-abcdefabcdef", "1");
        Assert.AreEqual("unconfigured", new SessionOperationsService().Compact(request, CancellationToken.None).Status);
        Assert.AreEqual("stale_epoch", service.Compact(request with { ExpectedEpoch = "old" }, CancellationToken.None).Status);
        foreach (var invalid in new[] { request with { ExpectedRuntimeInstanceId = request.ExpectedRuntimeInstanceId.ToUpperInvariant() },
            request with { ExpectedRuntimeInstanceId = Guid.Empty.ToString("D") }, request with { ExpectedAttachmentGeneration = "01" },
            request with { ExpectedAttachmentGeneration = "0" }, request with { ExpectedAttachmentGeneration = "9223372036854775808" },
            request with { SessionId = " session" }, request with { SessionId = "\ud800" }, request with { ClientRequestId = new string('x', 257) } })
            Assert.AreEqual("invalid_request", service.Compact(invalid, CancellationToken.None).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Compact(request, cancelled.Token));
        service.CloseAdmission();
        Assert.AreEqual("closed", service.Compact(request, CancellationToken.None).Status);
        var escaped = new string('\u0001', 256);
        var maximum = request with { ExpectedEpoch = new string('\u0001', 64), ClientRequestId = escaped, SessionId = escaped,
            ExpectedAttachmentGeneration = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(maximum, DesktopJsonContext.Default.SessionCompactRequest).Length + 8192 <= 16 * 1024);
        var row = new SessionReceiptView("key", "session", Guid.NewGuid().ToString("D"), null, "Compact", "terminal", "Completed", null, null);
        Assert.AreEqual("ok", SessionOperationsService.ProjectPage("epoch", [row], null).Status);
    }

    [TestMethod]
    public Task CompactRpc_ActualOwnedRouteRetainsReplayAndKind() => RealFixture.RunAsync(async f =>
    {
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        service.Send(new("fixture-epoch", "send", f.SessionId, "text"), CancellationToken.None);
        var send = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "text")));
        await f.Ready(send);
        f.Provider.Release.TrySetResult();
        await f.Wait(send.Completion);
        var marker = Guid.NewGuid().ToString("N");
        f.Provider.EmitIdle!(marker);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await f.Keep(Committed());
        async Task Committed()
        {
            await foreach (var value in f.Host.RuntimeService.StreamEventsAsync(deadline.Token))
                if (value is SessionAgentEvent { Event: AgentSessionUpdateEvent update }
                    && update.Kind == AgentSessionUpdateKind.Idle && update.Message == marker) return;
            Assert.Fail("Idle marker was not committed.");
        }
        var state = await f.Wait(f.Keep(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId)));
        var request = new SessionCompactRequest("fixture-epoch", "compact", f.SessionId, state.RuntimeInstanceId.ToString("D"),
            state.Entry!.AttachmentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var admitted = service.Compact(request, CancellationToken.None);
        var compact = f.Retain(f.Host.Commands.AdmitCompact(new("compact", f.SessionId, state.RuntimeInstanceId, state.Entry.AttachmentGeneration)));
        Assert.AreEqual("accepted", admitted.Status);
        Assert.AreEqual("replay", service.Compact(request, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Compact(request with { ExpectedAttachmentGeneration = "999" }, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Send(new("fixture-epoch", "compact", f.SessionId, "text"), CancellationToken.None).Status);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Wait(compact.Completion)).Outcome);
        Assert.AreEqual(1, f.Provider.Compactions);
        var row = service.Receipts(new("fixture-epoch", 0)).Rows.Single(value => value.ClientRequestId == "compact");
        Assert.AreEqual("Compact", row.Kind);
        Assert.IsNull(row.RunId);
    });

    [TestMethod]
    public void AbortRunRpc_RejectsBeforeAdmissionAndBoundsGeneratedJson()
    {
        var calls = 0;
        var service = new SessionOperationsService("current", _ => throw new AssertFailedException("Send called"),
            _ => throw new AssertFailedException("Abort called"), null, _ => throw new AssertFailedException("Compact called"),
            _ => { calls++; throw new AssertFailedException("AbortRun called"); });
        var request = new SessionAbortRunRequest("current", "key", "session", "abcdefab-1234-5678-9abc-abcdefabcdef", "1", "run");
        Assert.AreEqual("unconfigured", new SessionOperationsService().AbortRun(request, CancellationToken.None).Status);
        Assert.AreEqual("stale_epoch", service.AbortRun(request with { ExpectedEpoch = "old" }, CancellationToken.None).Status);
        foreach (var invalid in new[] { request with { ExpectedRuntimeInstanceId = request.ExpectedRuntimeInstanceId.ToUpperInvariant() },
            request with { ExpectedRuntimeInstanceId = Guid.Empty.ToString("D") }, request with { ExpectedAttachmentGeneration = "01" },
            request with { ExpectedAttachmentGeneration = "0" }, request with { ExpectedAttachmentGeneration = "9223372036854775808" },
            request with { ExpectedRunId = " run" }, request with { ExpectedRunId = "\ud800" }, request with { ExpectedRunId = new string('r', 257) },
            request with { SessionId = "\udfff" }, request with { ClientRequestId = new string('x', 257) } })
            Assert.AreEqual("invalid_request", service.AbortRun(invalid, CancellationToken.None).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.AbortRun(request, cancelled.Token));
        service.CloseAdmission();
        Assert.AreEqual("closed", service.AbortRun(request, CancellationToken.None).Status);
        Assert.AreEqual(0, calls);
        var escaped = new string('\u0001', 256);
        var maximum = request with { ExpectedEpoch = new string('\u0001', 64), ClientRequestId = escaped, SessionId = escaped,
            ExpectedRunId = escaped, ExpectedAttachmentGeneration = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(maximum, DesktopJsonContext.Default.SessionAbortRunRequest);
        Assert.IsTrue(bytes.Length + 8192 <= 16 * 1024);
        Assert.AreEqual(maximum, JsonSerializer.Deserialize(bytes, DesktopJsonContext.Default.SessionAbortRunRequest));
        var row = new SessionReceiptView("key", "session", Guid.NewGuid().ToString("D"), null, "AbortRun", "terminal", "Completed", "cancellation_signalled", "run");
        Assert.AreEqual("ok", SessionOperationsService.ProjectPage("epoch", [row], null).Status);
    }

    [TestMethod]
    public Task AbortRunRpc_ActualOwnedUnsupportedReceiptRetainsReplayAndKind() => RealFixture.RunAsync(async f =>
    {
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        var sendAdmission = service.Send(new("fixture-epoch", "send", f.SessionId, "text"), CancellationToken.None);
        var send = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "text")));
        Assert.AreEqual("accepted", sendAdmission.Status);
        await f.Ready(send);
        var state = await f.Wait(f.Keep(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId)));
        var request = new SessionAbortRunRequest("fixture-epoch", "abort-run", f.SessionId, state.RuntimeInstanceId.ToString("D"),
            state.Entry!.AttachmentGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture), "fixture-run");
        var admitted = service.AbortRun(request, CancellationToken.None);
        var abort = f.Retain(f.Host.Commands.AdmitAbortRun(new("abort-run", f.SessionId, state.RuntimeInstanceId, state.Entry.AttachmentGeneration, "fixture-run")));
        Assert.AreEqual("accepted", admitted.Status);
        Assert.AreEqual("replay", service.AbortRun(request, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.AbortRun(request with { ExpectedRunId = "other" }, CancellationToken.None).Status);
        Assert.AreEqual("conflict", service.Abort(new("fixture-epoch", "abort-run", send.OperationId.ToString("D")), CancellationToken.None).Status);
        Assert.AreEqual("abort_run_unsupported", (await f.Wait(abort.Completion)).Code);
        Assert.IsFalse(send.Completion.IsCompleted);
        var row = service.Receipts(new("fixture-epoch", 0)).Rows.Single(value => value.ClientRequestId == "abort-run");
        Assert.AreEqual("AbortRun", row.Kind);
        Assert.IsNull(row.TargetOperationId);
    });

    [TestMethod]
    public async Task Shutdown_RetainsLeaseUntilAllOwnedWorkIsConfirmed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.IsFalse(DesktopApplication.CanReleaseOwnedLease(Task.CompletedTask, gate.Task, true));
            Assert.IsFalse(DesktopApplication.CanReleaseOwnedLease(Task.CompletedTask, Task.CompletedTask, false));
            gate.TrySetResult();
            await wait;
            Assert.IsTrue(DesktopApplication.CanReleaseOwnedLease(Task.CompletedTask, gate.Task, true));
        }
        finally { gate.TrySetResult(); await wait; }
    }

    [TestMethod]
    public Task ReminderFiring_RealOwnerDeliversExactTextTwiceWithDistinctReceipts() => RealFixture.RunAsync(async f =>
    {
        using var clock = new ReminderRpcTests.LiteralClock();
        await using var reminders = new ReminderService("fixture-epoch", (id, _) => Task.FromResult(id == f.SessionId),
            request => f.Host.Commands.AdmitSend(request), clock);
        const string text = "full reminder\nsecond line with emoji 😀";
        var created = await f.Wait(reminders.Create(new("fixture-epoch", f.SessionId, text, 60, 2), default));
        Assert.AreEqual("ok", created.Status);
        await f.Wait(clock.TimerCreated());
        clock.Advance(TimeSpan.FromSeconds(60));
        var first = await f.Wait(f.Provider.Sends.Reader.ReadAsync().AsTask());
        Assert.AreEqual(f.SessionId, first.SessionId);
        Assert.AreEqual(text, ((AgentInputItem.Text)first.Options.Input.Items.Single()).Value);
        var firstReplay = f.Host.Commands.AdmitSend(new($"reminder:{created.ReminderId}:0", f.SessionId, text));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, firstReplay.Kind);
        var firstReceipt = f.Retain(firstReplay);
        Assert.IsFalse(firstReceipt.Completion.IsCompleted);
        f.Provider.Release.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Wait(firstReceipt.Completion)).Outcome);
        var once = await f.Wait(ReminderCount(reminders, f.SessionId, 1));
        Assert.AreEqual(1, once.ActiveCount);
        Assert.AreEqual(0, once.CompletedCount);
        Assert.AreEqual(0, once.Reminders.Single().LastExitCode);
        await f.Wait(clock.TimerCreated());
        clock.Advance(TimeSpan.FromSeconds(60));
        var second = await f.Wait(f.Provider.Sends.Reader.ReadAsync().AsTask());
        Assert.AreEqual(f.SessionId, second.SessionId);
        Assert.AreEqual(text, ((AgentInputItem.Text)second.Options.Input.Items.Single()).Value);
        var secondReplay = f.Host.Commands.AdmitSend(new($"reminder:{created.ReminderId}:1", f.SessionId, text));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, secondReplay.Kind);
        var secondReceipt = f.Retain(secondReplay);
        Assert.AreNotEqual(firstReceipt.OperationId, secondReceipt.OperationId);
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Wait(secondReceipt.Completion)).Outcome);
        var done = await f.Wait(ReminderCount(reminders, f.SessionId, 2));
        Assert.AreEqual(0, done.ActiveCount);
        Assert.AreEqual(1, done.CompletedCount);
        Assert.AreEqual(0, done.Reminders.Single().LastExitCode);
        Assert.IsFalse(f.Provider.Sends.Reader.TryRead(out _));
    });

    [TestMethod]
    public Task ReminderDeleteAndShutdown_DoNotRetractAcceptedOwnerSend() => RealFixture.RunAsync(async f =>
    {
        using var clock = new ReminderRpcTests.LiteralClock();
        var reminders = new ReminderService("fixture-epoch", (id, _) => Task.FromResult(id == f.SessionId),
            request => f.Host.Commands.AdmitSend(request), clock);
        var created = await f.Wait(reminders.Create(new("fixture-epoch", f.SessionId, "retained exact text", 60, 3), default));
        Assert.AreEqual("ok", created.Status);
        await f.Wait(clock.TimerCreated());
        clock.Advance(TimeSpan.FromSeconds(60));
        var delivered = await f.Wait(f.Provider.Sends.Reader.ReadAsync().AsTask());
        Assert.AreEqual(f.SessionId, delivered.SessionId);
        Assert.AreEqual("retained exact text", ((AgentInputItem.Text)delivered.Options.Input.Items.Single()).Value);
        var replay = f.Host.Commands.AdmitSend(new($"reminder:{created.ReminderId}:0", f.SessionId, "retained exact text"));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, replay.Kind);
        var receipt = f.Retain(replay);
        Assert.AreEqual("ok", reminders.Delete(new("fixture-epoch", f.SessionId, created.ReminderId!, created.ReminderId!), default).Status);
        Assert.HasCount(0, (await f.Wait(reminders.List(new("fixture-epoch", f.SessionId), default))).Reminders);
        reminders.CloseAdmission();
        Assert.AreEqual("closed", (await f.Wait(reminders.Create(new("fixture-epoch", f.SessionId, "new", 60, 1), default))).Status);
        var drain = f.Keep(reminders.DisposeAsync().AsTask());
        Assert.IsFalse(drain.IsCompleted, "Captured admitted send must be joined, not cancelled or forgotten.");
        Assert.IsFalse(receipt.Completion.IsCompleted);
        f.Provider.Release.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Wait(receipt.Completion)).Outcome);
        await f.Wait(drain);
        await f.Wait(f.CloseHost());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.IsFalse(f.Provider.Sends.Reader.TryRead(out _));
    });

    [TestMethod]
    public Task ReminderFiring_FailedOwnerReceiptIsOneAttemptNotAnAutomaticRetry() => RealFixture.RunAsync(async f =>
    {
        using var clock = new ReminderRpcTests.LiteralClock();
        f.Provider.FailSend = true;
        await using var reminders = new ReminderService("fixture-epoch", (id, _) => Task.FromResult(id == f.SessionId),
            request => f.Host.Commands.AdmitSend(request), clock);
        var created = await f.Wait(reminders.Create(new("fixture-epoch", f.SessionId, "failure text", 60, 1), default));
        Assert.AreEqual("ok", created.Status);
        await f.Wait(clock.TimerCreated());
        clock.Advance(TimeSpan.FromSeconds(60));
        var send = await f.Wait(f.Provider.Sends.Reader.ReadAsync().AsTask());
        Assert.AreEqual(f.SessionId, send.SessionId);
        var replay = f.Host.Commands.AdmitSend(new($"reminder:{created.ReminderId}:0", f.SessionId, "failure text"));
        Assert.AreEqual(OwnedSessionCommandAdmissionKind.Replay, replay.Kind);
        var receipt = f.Retain(replay);
        f.Provider.Release.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Failed, (await f.Wait(receipt.Completion)).Outcome);
        var done = await f.Wait(ReminderCount(reminders, f.SessionId, 1));
        Assert.AreEqual(1, done.CompletedCount);
        Assert.AreEqual(1, done.Reminders.Single().FiredCount);
        Assert.AreNotEqual(0, done.Reminders.Single().LastExitCode);
        Assert.IsNotNull(done.Reminders.Single().LastError);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.IsFalse(f.Provider.Sends.Reader.TryRead(out _));
    });

    [TestMethod]
    public Task ReminderFiring_DuringATurnOfTheSession_IsKeptForTheSessionAndNotRefused() => RealFixture.RunAsync(async f =>
    {
        using var clock = new ReminderRpcTests.LiteralClock();
        await using var reminders = new ReminderService("fixture-epoch", (id, _) => Task.FromResult(id == f.SessionId),
            request => f.Host.Commands.AdmitSend(request),
            (id, content) => f.Host.RuntimeService.DeliverHostPromptToRunningTurnAsync(id, content, ReminderService.PromptKind), clock);
        // A turn of the session runs: the provider has taken the prompt and has not said that it is idle.
        var send = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "work")));
        await f.Ready(send);
        f.Provider.Release.TrySetResult();
        Assert.AreEqual(OwnedSessionCommandOutcome.Completed, (await f.Wait(send.Completion)).Outcome);
        Assert.AreEqual("work", ((AgentInputItem.Text)(await f.Wait(f.Provider.Sends.Reader.ReadAsync().AsTask())).Options.Input.Items.Single()).Value);
        var state = await f.Wait(f.Keep(f.Host.RuntimeService.GetCurrentStateAsync(f.SessionId)));
        Assert.IsNotNull(state.Entry!.ActiveRunId);

        var created = await f.Wait(reminders.Create(new("fixture-epoch", f.SessionId, "check the build", 60, 1), default));
        Assert.AreEqual("ok", created.Status);
        await f.Wait(clock.TimerCreated());
        clock.Advance(TimeSpan.FromSeconds(60));
        var done = await f.Wait(ReminderCount(reminders, f.SessionId, 1));
        Assert.AreEqual(0, done.Reminders.Single().LastExitCode);
        Assert.IsNull(done.Reminders.Single().LastError);

        // The provider of the fixture takes nothing in a turn that runs, so the reminder waits for the end of the
        // turn and starts the next one.
        Assert.IsFalse(f.Provider.Sends.Reader.TryRead(out _));
        f.Provider.EmitIdle!("the turn ended");
        var next = await f.Wait(f.Provider.Sends.Reader.ReadAsync().AsTask());
        Assert.AreEqual(f.SessionId, next.SessionId);
        Assert.AreEqual("check the build", ((AgentInputItem.Text)next.Options.Input.Items.Single()).Value);
    });

    private static async Task<ReminderListResponse> ReminderCount(ReminderService reminders, string sessionId, int count)
    {
        for (var attempt = 0; attempt < 100000; attempt++)
        {
            var list = await reminders.List(new("fixture-epoch", sessionId), default);
            if (list.Reminders.Single().FiredCount == count) return list;
            await Task.Yield();
        }
        Assert.Fail("The owner receipt did not settle the reminder firing.");
        throw new InvalidOperationException("Unreachable.");
    }

    [TestMethod]
    public Task OwnedRpc_UsesRealHostCachedStoreAndFakeProvider() => RealFixture.RunAsync(async f =>
    {
        var workspace = new WorkspaceService(f.Host.WorkspaceReads);
        var snapshotTask = f.Keep(workspace.SnapshotAsync(new(), CancellationToken.None));
        var snapshot = await f.Wait(snapshotTask);
        Assert.IsTrue(snapshot.Sessions.Any(session => session.Id == f.SessionId));
        var historyTask = f.Keep(workspace.HistoryAsync(new(f.SessionId, null), CancellationToken.None));
        var history = await f.Wait(historyTask);
        Assert.AreEqual("ok", history.Status);
        var service = new SessionOperationsService(f.Host.Commands, "fixture-epoch");
        var admission = service.Send(new("fixture-epoch", "send", f.SessionId, "text"), CancellationToken.None);
        var receipt = f.Retain(f.Host.Commands.AdmitSend(new("send", f.SessionId, "text")));
        Assert.AreEqual("accepted", admission.Status);
        await f.Ready(receipt);
        f.Provider.Release.TrySetResult();
        await f.Wait(receipt.Completion);
        Assert.AreEqual("Completed", service.Receipts(new("fixture-epoch", 0)).Rows.Single().Outcome);
    });

    private sealed class RealFixture
    {
        private readonly object _gate = new();
        private readonly List<Task> _work = [], _observers = [], _cleanup = [];
        private readonly List<Exception> _failures = [];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeAlta-desktop-owned-" + Guid.NewGuid().ToString("N"));
        private Task? _disposal;
        private bool _ownsRoot;
        private volatile bool _closing;
        private CodeAltaHost? _host;
        internal CodeAltaHost Host => _host ?? throw new InvalidOperationException("No fixture host.");
        internal string SessionId { get; } = Guid.CreateVersion7().ToString();
        internal FakeProvider Provider { get; } = new();

        internal Task Keep(Task task)
        {
            lock (_gate) { _work.Add(task); _observers.Add(ObserveAsync(task)); }
            return task;
        }
        internal Task<T> Keep<T>(Task<T> task) { Keep((Task)task); return task; }
        private async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { lock (_gate) _failures.Add(ex); }
        }
        internal Task Wait(Task task) => Keep(task.WaitAsync(TimeSpan.FromSeconds(5)));
        internal Task<T> Wait<T>(Task<T> task) => Keep(task.WaitAsync(TimeSpan.FromSeconds(5)));
        internal OwnedSessionCommandReceipt Retain(OwnedSessionCommandAdmission admission)
        {
            if (admission.Receipt is not null) Keep(admission.Receipt.Completion);
            Assert.IsNotNull(admission.Receipt);
            return admission.Receipt;
        }
        internal Task Ready(OwnedSessionCommandReceipt receipt) => Keep(ReadyCoreAsync(receipt));
        private async Task ReadyCoreAsync(OwnedSessionCommandReceipt receipt)
        {
            var race = Keep(Task.WhenAny(Provider.Started.Task, receipt.Completion));
            await Wait(race);
            if (Provider.Started.Task.IsCompletedSuccessfully) return;
            var result = await Wait(receipt.Completion);
            Assert.Fail($"Send readiness: Outcome={result.Outcome}; Code={result.Code}");
        }
        private Task DisposeHost()
        {
            lock (_gate) return _disposal ??= Keep(Host.DisposeAsync().AsTask());
        }
        internal Task CloseHost() => DisposeHost();

        internal static async Task RunAsync(Func<RealFixture, Task> body)
        {
            var f = new RealFixture();
            Task? setup = null;
            Task? bodyTask = null;
            try
            {
                setup = f.Keep(f.SetupAsync());
                await f.Wait(setup);
                bodyTask = f.Keep(body(f));
                await f.Wait(bodyTask);
            }
            catch (Exception ex) { lock (f._gate) f._failures.Add(ex); }
            finally
            {
                Task[] available;
                lock (f._gate)
                {
                    f._closing = true;
                    f.Provider.Release.TrySetResult();
                    if (f._host is not null) _ = f.DisposeHost();
                    available = [.. f._work, .. f._observers];
                }
                // Start every available setup/body/read/disposal/observer deadline before awaiting any.
                var availableJoin = f.DrainAsync(available);
                var confirmed = await availableJoin;
                // A timed-out producer may still add work. Its root remains retained, not finalized.
                if ((setup is null || setup.IsCompleted) && (bodyTask is null || bodyTask.IsCompleted))
                {
                    Task[] snapshot;
                    lock (f._gate) snapshot = [.. f._work, .. f._observers];
                    var finalJoin = f.DrainAsync(snapshot);
                    confirmed &= await finalJoin;
                    lock (f._gate)
                        if (confirmed && f._failures.Count == 0 && f._ownsRoot) Directory.Delete(f._root, recursive: true);
                }
            }
            Exception[] failures;
            lock (f._gate) failures = [.. f._failures];
            if (failures.Length != 0) throw new AggregateException("Fixture failed; root retained: " + f._root, failures);
        }

        private async Task<bool> DrainAsync(IEnumerable<Task> tasks)
        {
            var waits = tasks.Distinct().Select(task => task.WaitAsync(TimeSpan.FromSeconds(5))).ToArray();
            lock (_gate) _cleanup.AddRange(waits);
            var confirmed = true;
            foreach (var wait in waits)
            {
                try { await wait.ConfigureAwait(false); }
                catch (Exception ex) { lock (_gate) _failures.Add(ex); confirmed = false; }
            }
            return confirmed;
        }

        private async Task SetupAsync()
        {
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(_root)!); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Reparse ancestry.");
            if (Directory.Exists(_root) || File.Exists(_root)) throw new InvalidOperationException("Fixture root exists.");
            Directory.CreateDirectory(_root);
            _ownsRoot = true;
            var global = Path.Combine(_root, "global");
            var projectPath = Path.Combine(_root, "project");
            var home = Path.Combine(_root, "home");
            var builtin = Path.Combine(_root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(builtin, ".git"));
            File.WriteAllText(Path.Combine(builtin, ".git", "config"), "[core]\nignorecase = true\nexcludesfile = fixture.ignore\n");
            File.WriteAllText(Path.Combine(builtin, ".git", "fixture.ignore"), "");
            Directory.CreateDirectory(Path.Combine(builtin, "fixture-builtin"));
            File.WriteAllText(Path.Combine(builtin, "fixture-builtin", "SKILL.md"), "---\nname: fixture-builtin\ndescription: Owned fixture.\n---\nFixture only.\n");
            var options = new CatalogOptions { GlobalRoot = global };
            var projectTask = Keep(new ProjectCatalog(options).UpsertFromPathAsync(projectPath, CancellationToken.None));
            var project = await projectTask;
            var session = new SessionViewDescriptor
            {
                SessionId = SessionId, Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id,
                ProviderId = "owned-fixture", ProviderKey = "owned-fixture", ModelId = "fixture-model", AgentPromptId = "default",
                WorkingDirectory = projectPath, Title = "Owned fixture", CreatedAt = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero),
            };
            var journal = new SessionViewJournalStore(options);
            var header = Keep(journal.EnsureHeaderAsync(session, CancellationToken.None));
            await header;
            var store = journal.CreateSessionStore();
            var summary = Keep(store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = SessionId, ProviderId = Provider.Descriptor.ProviderId, ProviderKey = "owned-fixture",
                ModelId = session.ModelId, AgentPromptId = session.AgentPromptId, ReasoningEffort = session.ReasoningEffort,
                WorkingDirectory = projectPath, Title = session.Title, CreatedAt = session.CreatedAt, UpdatedAt = session.CreatedAt,
            }, CancellationToken.None));
            await summary;
            var append = Keep(journal.AppendStateAsync(session, new SessionViewLocalState
            {
                ProviderKey = session.ProviderKey, ModelId = session.ModelId, ReasoningEffort = session.ReasoningEffort, AgentPromptId = session.AgentPromptId,
            }, CancellationToken.None));
            await append;
            var read = Keep(store.GetSessionAsync(SessionId, CancellationToken.None));
            var metadata = await read;
            Assert.IsNotNull(metadata);
            Assert.AreEqual(SessionId, metadata.SessionId);
            Assert.AreEqual(projectPath, metadata.WorkspacePath);
            Assert.AreEqual("owned-fixture", metadata.ProviderKey);
            Assert.AreEqual("fixture-model", metadata.ModelId);
            Assert.AreEqual("default", metadata.AgentPromptId);
            Assert.IsNotNull(metadata.ViewState);
            Assert.AreEqual("owned-fixture", metadata.ViewState.ProviderKey);
            Assert.AreEqual("fixture-model", metadata.ViewState.ModelId);
            Assert.AreEqual("default", metadata.ViewState.AgentPromptId);
            Assert.AreEqual(metadata.ReasoningEffort, metadata.ViewState.ReasoningEffort);
            var creation = Keep(CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, _root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, OwnsLogging = false, IsHeadless = true,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(Provider.Descriptor, () => new FakeRuntime(Provider)),
            }, CancellationToken.None));
            var host = await creation;
            lock (_gate) _host = host;
            if (_closing) await DisposeHost();
        }
    }

    private sealed class FakeProvider
    {
        internal Action<string>? EmitIdle { get; set; }
        internal int Compactions { get; set; }
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Channel<(string SessionId, AgentSendOptions Options)> Sends = Channel.CreateUnbounded<(string, AgentSendOptions)>();
        internal bool FailSend { get; set; }
        internal string? PermissionMode { get; set; }
        internal ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("owned-fixture"), "Owned fixture") { DefaultModelId = "fixture-model" };
    }

    private sealed class FakeRuntime(FakeProvider provider) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => provider.Descriptor;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No probes.");
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("No turn executor.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
        {
            provider.PermissionMode = options.PermissionMode;
            return Task.FromResult<IAgentSession>(new FakeSession(provider, options.SessionId!, options.WorkingDirectory));
        }
        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
        {
            provider.PermissionMode = options.PermissionMode;
            return Task.FromResult<IAgentSession>(new FakeSession(provider, sessionId, options.WorkingDirectory));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSession(FakeProvider provider, string sessionId, string? cwd) : IAgentSession, IAgentIdleCompactionProvider, IAgentPermissionModeProvider
    {
        public ModelProviderId ProviderId => provider.Descriptor.ProviderId;
        public string? PermissionMode => provider.PermissionMode;
        public void SetPermissionMode(string? permissionMode) => provider.PermissionMode = permissionMode;
        public string SessionId => sessionId;
        public string? WorkspacePath => cwd;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public IDisposable Subscribe(Action<AgentEvent> handler)
        {
            provider.EmitIdle = marker =>
            {
                handler(new AgentSessionUpdateEvent(ProviderId, SessionId, DateTimeOffset.UtcNow, null, AgentSessionUpdateKind.Idle, marker));
            };
            return new Subscription(() => provider.EmitIdle = null);
        }
        public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
        {
            provider.Started.TrySetResult();
            provider.Sends.Writer.TryWrite((sessionId, options));
            await provider.Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.FailSend) throw new InvalidOperationException("Fixture send failed.");
            return new("fixture-run");
        }
        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.ExpectedRunId?.Value != "fixture-run") throw new InvalidOperationException("Wrong fixture run.");
            return Task.FromResult(new AgentRunId("fixture-run"));
        }
        public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No compaction.");
        public Task<AgentCompactionOutcome?> TryCompactWhenIdleAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            provider.Compactions++;
            return Task.FromResult<AgentCompactionOutcome?>(new(true, "inert completed compaction"));
        }
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }
}

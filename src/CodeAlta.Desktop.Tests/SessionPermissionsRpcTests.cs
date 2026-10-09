using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

// Pure literal transport adapters and CLI existence facts only. No host/runtime/native/storage/provider construction.
[TestClass]
public sealed class SessionPermissionsRpcTests
{
    private const string Epoch = "abcdefab-1234-5678-9abc-abcdefabcdef";
    private static SessionOwnedPermissionSnapshot Entry(string session = "session")
    {
        var attempt = new SessionPermissionHandle(session, null, "interaction", Guid.NewGuid());
        return new(new(Guid.NewGuid(), Guid.NewGuid(), long.MaxValue, attempt),
            new(attempt, new ModelProviderId("fake"), DateTimeOffset.UnixEpoch, "commandExecution", "inert command", "Q:\\fixture", null, null));
    }
    private static SessionPermissionsService Service(SessionOwnedPermissionPage page,
        Func<SessionOwnedPermissionHandle, AgentPermissionDecisionKind, CancellationToken, ValueTask<bool>>? resolve = null)
        => new((_, _) => ValueTask.FromResult(page), resolve ?? ((_, _, _) => throw new AssertFailedException("Resolve forbidden.")), Epoch, static () => true);

    [TestMethod]
    public void CommandReviewFlag_IsExplicitOwnedOnlyAndDefaultOff()
    {
        var root = OperatingSystem.IsWindows() ? @"Q:\owned" : "/owned";
        var args = new[] { "--data-root", root + "/browser", "--catalog-root", root + "/copy", "--allow-catalog-cache",
            "--allow-owned-host", "--project-root", root + "/project", "--discovery-home", root + "/home",
            "--instruction-root", root, "--builtin-skill-root", root + "/builtin" };
        bool Exists(string path) => !path.EndsWith("browser", StringComparison.Ordinal);
        Assert.IsTrue(DesktopCommandLine.TryParse(args, Exists, _ => false, out var disabled, out _));
        Assert.IsFalse(disabled!.ReviewOwnedCommandPermissions);
        Assert.IsTrue(DesktopCommandLine.TryParse([.. args, "--review-owned-command-permissions"], Exists, _ => false, out var enabled, out _));
        Assert.IsTrue(enabled!.ReviewOwnedCommandPermissions);
        Assert.IsFalse(DesktopCommandLine.TryParse([.. args, "--review-owned-command-permissions", "--review-owned-command-permissions"], Exists, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse(["--data-root", root + "/browser", "--review-owned-command-permissions"], Exists, _ => false, out _, out _));
        Assert.IsFalse(DesktopCommandLine.TryParse([.. args.Where(arg => arg != "--allow-owned-host"), "--review-owned-command-permissions"], Exists, _ => false, out _, out _));
        Assert.IsFalse(new BootService().Status(new()).CommandReviewEnabled);
        Assert.IsFalse(new BootService(Epoch).Status(new()).CommandReviewEnabled);
        Assert.IsTrue(new BootService(Epoch, true).Status(new()).CommandReviewEnabled);
    }

    [TestMethod]
    public async Task ListValidationEpochDisabledAndPreCancellation_DoNotInvokeBackend()
    {
        foreach (var enabled in new[] { false, true })
        {
            var service = new SessionPermissionsService((_, _) => throw new AssertFailedException("List forbidden."),
                (_, _, _) => throw new AssertFailedException("Resolve forbidden."), Epoch, () => enabled);
            foreach (var identity in new[] { "", " padded ", "bad\n", "\ud800", "\udc00", new string('x', 129) })
                Assert.AreEqual("invalid_request", (await service.ListAsync(new(Epoch, identity), default)).Status);
            Assert.AreEqual("invalid_request", (await service.ListAsync(null!, default)).Status);
            Assert.AreEqual("stale_epoch", (await service.ListAsync(new("old", "session"), default)).Status);
            if (enabled) await Assert.ThrowsAsync<OperationCanceledException>(() => service.ListAsync(new(Epoch, "session"), new CancellationToken(true)));
            else Assert.AreEqual("disabled", (await service.ListAsync(new(Epoch, "session"), default)).Status);
        }
    }

    [TestMethod]
    public async Task ResolveValidationAndExactAdapter_ForwardOnlyBoundedSupportedDecisions()
    {
        var entry = Entry();
        var seed = await Service(new([entry], false)).ListAsync(new(Epoch, "session"), default);
        var handle = seed.Entries.Single().Handle;
        var called = 0;
        var service = Service(new([entry], false), (actual, decision, token) =>
        {
            Assert.AreEqual(entry.Handle, actual);
            Assert.AreEqual(CancellationToken.None, token);
            Assert.AreEqual(new[] { AgentPermissionDecisionKind.AllowOnce, AgentPermissionDecisionKind.Deny, AgentPermissionDecisionKind.Cancel }[called++], decision);
            return ValueTask.FromResult(decision != AgentPermissionDecisionKind.Deny);
        });
        foreach (var bad in new[] { handle with { OperationId = Epoch.ToUpperInvariant() }, handle with { RuntimeInstanceId = Guid.Empty.ToString("D") },
            handle with { AttemptId = " " + Epoch }, handle with { AttachmentGeneration = "01" }, handle with { AttachmentGeneration = "9223372036854775808" },
            handle with { AttachmentGeneration = "0" }, handle with { SessionId = "bad\0" }, handle with { RunId = " " }, handle with { InteractionId = "\ud800" } })
            Assert.AreEqual("invalid_request", (await service.ResolveAsync(new(Epoch, bad, "allow_once"), default)).Status);
        foreach (var decision in new[] { "AllowOnce", "allow_for_session", "", "approve" })
            Assert.AreEqual("invalid_request", (await service.ResolveAsync(new(Epoch, handle, decision), default)).Status);
        Assert.AreEqual("invalid_request", (await service.ResolveAsync(null!, default)).Status);
        Assert.AreEqual("invalid_request", (await service.ResolveAsync(new(Epoch, null!, "allow_once"), default)).Status);
        Assert.AreEqual("stale_epoch", (await service.ResolveAsync(new("old", handle, "allow_once"), default)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ResolveAsync(new(Epoch, handle, "allow_once"), new CancellationToken(true)));
        var disabled = new SessionPermissionsService((_, _) => throw new AssertFailedException("List forbidden."),
            (_, _, _) => throw new AssertFailedException("Resolve forbidden."), Epoch, static () => false);
        Assert.AreEqual("disabled", (await disabled.ResolveAsync(new(Epoch, handle, "allow_once"), default)).Status);
        Assert.AreEqual(0, called);
        foreach (var (decision, status) in new[] { ("allow_once", "resolved"), ("deny", "rejected"), ("cancel", "resolved") })
        {
            var result = await service.ResolveAsync(new(Epoch, handle, decision), default);
            Assert.AreEqual(status, result.Status);
            Assert.AreEqual(handle, result.Handle);
            Assert.AreEqual(Epoch, result.HostEpoch);
        }
        Assert.AreEqual(3, called);
    }

    [TestMethod]
    public async Task InvalidBackendOutput_IsRefusedWithoutPartialCommandsOrTruncation()
    {
        var entry = Entry();
        foreach (var bad in new[] { entry with { Handle = entry.Handle with { OperationId = Guid.Empty } },
            entry with { Handle = entry.Handle with { AttachmentGeneration = 0 } }, entry with { Request = entry.Request with { Command = new string('x', 4097) } },
            entry with { Request = entry.Request with { WorkingDirectory = "\ud800" } }, entry with { Request = entry.Request with { Reason = new string('x', 1025) } },
            entry with { Request = entry.Request with { GrantRoot = "unexpected" } }, entry with { Request = entry.Request with { Kind = "fileChange" } },
            entry with { Request = entry.Request with { ProviderId = new ModelProviderId("ba\nd") } },
            entry with { Request = entry.Request with { Handle = entry.Request.Handle with { RunId = "wrong" } } }, Entry("other") })
        {
            var result = await Service(new([bad], false)).ListAsync(new(Epoch, "session"), default);
            Assert.AreEqual("wire_limit", result.Status);
            Assert.AreEqual(0, result.Entries.Length);
        }
        Assert.AreEqual("wire_limit", (await Service(new([entry, entry], false)).ListAsync(new(Epoch, "session"), default)).Status);
        Assert.AreEqual("wire_limit", (await Service(new(Enumerable.Range(0, 5).Select(_ => Entry()).ToArray(), false)).ListAsync(new(Epoch, "session"), default)).Status);
    }

    [TestMethod]
    public async Task GeneratedMaximumEscapingFitsBudgetAndPreservesCompleteCommands()
    {
        var id = new string('"', 128);
        var command = new string('\u0001', 4096);
        var text = new string('\u0001', 1024);
        var entries = Enumerable.Range(0, 4).Select(_ =>
        {
            var entry = Entry(id);
            var attempt = entry.Handle.Attempt with { RunId = id, InteractionId = id };
            return entry with { Handle = entry.Handle with { Attempt = attempt }, Request = entry.Request with {
                Handle = attempt, ProviderId = new ModelProviderId(id), Command = command, WorkingDirectory = text, Reason = text } };
        }).ToArray();
        var response = await Service(new(entries, true)).ListAsync(new(Epoch, id), default);
        Assert.AreEqual("ok", response.Status);
        Assert.IsTrue(response.HasMore);
        var json = JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.SessionPermissionsPage);
        Console.WriteLine($"Owned command response: {json.Length}; with framing: {json.Length + 4096}; budget: {SessionPermissionsService.MaximumResponseBytes}.");
        Assert.IsTrue(json.Length + 4096 < SessionPermissionsService.MaximumResponseBytes);
        using var parsed = JsonDocument.Parse(json);
        var row = parsed.RootElement.GetProperty("entries")[0];
        Assert.AreEqual(command, row.GetProperty("command").GetString());
        Assert.AreEqual("9223372036854775807", row.GetProperty("handle").GetProperty("attachmentGeneration").GetString());
        var resolve = new SessionPermissionResolveRequest(Epoch, response.Entries[0].Handle, "allow_once");
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(resolve, DesktopJsonContext.Default.SessionPermissionResolveRequest).Length + 4096 < 8192);
    }

    [TestMethod]
    public async Task BackendFailuresAreSanitizedAndResolutionFailureIsUncertain()
    {
        var entry = Entry();
        var handle = (await Service(new([entry], false)).ListAsync(new(Epoch, "session"), default)).Entries[0].Handle;
        var service = new SessionPermissionsService((_, _) => ValueTask.FromException<SessionOwnedPermissionPage>(new IOException("private")),
            (_, _, _) => ValueTask.FromException<bool>(new IOException("private")), Epoch, static () => true);
        var page = await service.ListAsync(new(Epoch, "session"), default);
        var result = await service.ResolveAsync(new(Epoch, handle, "allow_once"), default);
        Assert.AreEqual("read_failed", page.Status);
        Assert.AreEqual("uncertain", result.Status);
        Assert.IsFalse(JsonSerializer.Serialize(result, DesktopJsonContext.Default.SessionPermissionResolution).Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CallerCancellation_IsForwardedThroughBothMailboxAdapters()
    {
        var entry = Entry();
        var handle = (await Service(new([entry], false)).ListAsync(new(Epoch, "session"), default)).Entries[0].Handle;
        using var listCancellation = new CancellationTokenSource();
        using var resolveCancellation = new CancellationTokenSource();
        var service = new SessionPermissionsService((session, token) =>
        {
            Assert.AreEqual("session", session);
            Assert.AreEqual(listCancellation.Token, token);
            listCancellation.Cancel();
            return ValueTask.FromCanceled<SessionOwnedPermissionPage>(token);
        }, (actual, _, token) =>
        {
            Assert.AreEqual(entry.Handle, actual);
            Assert.AreEqual(resolveCancellation.Token, token);
            resolveCancellation.Cancel();
            return ValueTask.FromCanceled<bool>(token);
        }, Epoch, static () => true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ListAsync(new(Epoch, "session"), listCancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ResolveAsync(new(Epoch, handle, "allow_once"), resolveCancellation.Token));
    }
}

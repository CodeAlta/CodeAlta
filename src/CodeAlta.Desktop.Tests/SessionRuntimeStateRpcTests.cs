using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

// Literal transport inputs only; runtime state transitions are tested against the isolated actual runtime.
[TestClass]
public sealed class SessionRuntimeStateRpcTests
{
    private const string Epoch = "00000000-0000-0000-0000-000000000001";

    [TestMethod]
    public async Task ActivityProjectionPreservesOffsetAndExactCountsAndRejectsInvalidTime()
    {
        var entry = new SessionRuntimeCurrentEntry(1, false, false, null, false, "fake", "fake", null, null, null, null)
        { Activity = new(DateTimeOffset.Parse("2026-01-01T12:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture), long.MaxValue, 2) };
        var state = new SessionRuntimeCurrentState(Guid.NewGuid(), "session", false, entry);
        var service = new SessionRuntimeStateService((_, _) => Task.FromResult(state), Epoch);
        var value = await service.CurrentAsync(new(Epoch, "session"), default);
        Assert.AreEqual("9223372036854775807", value.Entry!.Activity!.AdmittedEvents);
        Assert.AreEqual("2", value.Entry.Activity.OmittedEvents);
        StringAssert.EndsWith(value.Entry.Activity.Timestamp!, "+02:00");
        Assert.AreEqual("admitted_agent_event", value.Entry.Activity.Source);
        state = state with { Entry = entry with { Activity = new(DateTimeOffset.MinValue, 1, 0) } };
        Assert.AreEqual("wire_limit", (await service.CurrentAsync(new(Epoch, "session"), default)).Status);
        foreach (var invalid in new[] { new SessionRuntimeActivity(null, 1, 0), new(null, -1, 0), new(null, 0, -1), new(entry.Activity.Timestamp, 0, 0) })
        {
            state = state with { Entry = entry with { Activity = invalid } };
            Assert.AreEqual("wire_limit", (await service.CurrentAsync(new(Epoch, "session"), default)).Status);
        }
    }

    [TestMethod]
    public async Task ScopedObservationBindsIdentityScopeAndEpochWithBoundedProjection()
    {
        var count = 0;
        var state = new SessionRuntimeCurrentState(Guid.NewGuid(), "session", true,
            new(9, false, true, "run", true, "fake", "fake", null, null, null, null));
        var service = new SessionRuntimeStateService((id, date, scope, project, path, token) =>
        {
            count++; Assert.AreEqual("session", id); Assert.AreEqual("global", scope);
            Assert.IsNull(project); Assert.IsNull(path);
            return Task.FromResult(new OwnedRuntimeObservation("ok", state));
        }, Epoch);
        var request = new SessionRuntimeScopedRequest(Epoch, "session", DateTimeOffset.UnixEpoch.ToString("O"), "global", null, null);
        Assert.AreEqual("invalid_request", (await service.ObserveAsync(request with { SessionId = "../session" }, default)).Status);
        Assert.AreEqual("invalid_request", (await service.ObserveAsync(request with { Scope = "project" }, default)).Status);
        Assert.AreEqual("stale_epoch", (await service.ObserveAsync(request with { ExpectedHostEpoch = "old" }, default)).Status);
        Assert.AreEqual(0, count);
        var response = await service.ObserveAsync(request, default);
        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual("global", response.Scope);
        Assert.IsTrue(response.Observation!.CoordinatorTransitionInProgress);
        Assert.IsTrue(response.Observation.Entry!.IsRetiring);
        Assert.AreEqual("9", response.Observation.Entry.AttachmentGeneration);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.SessionRuntimeScopedResponse).Length < 64 * 1024);
        state = state with { Entry = null, CoordinatorTransitionInProgress = false };
        Assert.IsNull((await service.ObserveAsync(request, default)).Observation!.Entry);
        state = state with { SessionId = "other" };
        Assert.AreEqual("wire_limit", (await service.ObserveAsync(request, default)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ObserveAsync(request, new CancellationToken(true)));
        foreach (var (exception, status) in new (Exception, string)[] {
            (new ObjectDisposedException("private"), "closed"), (new IOException("private"), "read_failed") })
        {
            var failed = new SessionRuntimeStateService((_, _, _, _, _, _) => Task.FromException<OwnedRuntimeObservation>(exception), Epoch);
            var refusal = await failed.ObserveAsync(request, default);
            Assert.AreEqual(status, refusal.Status);
            Assert.IsNull(refusal.Observation);
        }
    }

    [TestMethod]
    public async Task InvalidIdentityEpochAndPreCancellation_DoNotInvokeRuntime()
    {
        var service = new SessionRuntimeStateService((_, _) => throw new AssertFailedException("Query forbidden."), Epoch);
        foreach (var request in new SessionRuntimeStateRequest?[] { null, new(Epoch, ""), new(Epoch, " padded "),
            new(Epoch, new string('x', 257)), new(Epoch, "\ud800"), new(Epoch, "\udc00") })
            Assert.AreEqual("invalid_request", (await service.CurrentAsync(request!, default)).Status);
        Assert.AreEqual("stale_epoch", (await service.CurrentAsync(new("old", "session"), default)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CurrentAsync(new(Epoch, "session"), new CancellationToken(true)));
    }

    [TestMethod]
    public async Task SelectedSnapshotAndFailures_AreExplicitAndDoNotLeakExceptions()
    {
        var snapshot = new SessionRuntimeCurrentState(Guid.NewGuid(), "session", true, null);
        var service = new SessionRuntimeStateService((id, _) => { Assert.AreEqual("session", id); return Task.FromResult(snapshot); }, Epoch);
        var result = await service.CurrentAsync(new(Epoch, "session"), default);
        Assert.AreEqual("ok", result.Status);
        Assert.IsNull(result.Entry);
        Assert.IsTrue(result.CoordinatorTransitionInProgress);
        Assert.AreEqual(snapshot.RuntimeInstanceId.ToString("D"), result.RuntimeInstanceId);
        foreach (var (exception, status) in new (Exception, string)[] {
            (new ObjectDisposedException("private"), "closed"), (new IOException("private"), "read_failed") })
        {
            var failing = new SessionRuntimeStateService((_, _) => Task.FromException<SessionRuntimeCurrentState>(exception), Epoch);
            var error = await failing.CurrentAsync(new(Epoch, "session"), default);
            Assert.AreEqual(status, error.Status);
            Assert.IsNull(error.RuntimeInstanceId);
            Assert.IsNull(error.CoordinatorTransitionInProgress);
            Assert.IsNull(error.Entry);
            Assert.IsFalse(JsonSerializer.Serialize(error, DesktopJsonContext.Default.SessionRuntimeStateResponse).Contains("private", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task CallerCancellationDuringQuery_IsForwardedWithoutBecomingAbsentState()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new SessionRuntimeStateService((_, token) =>
        {
            Assert.AreEqual(cancellation.Token, token);
            cancellation.Cancel();
            return Task.FromCanceled<SessionRuntimeCurrentState>(token);
        }, Epoch);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CurrentAsync(new(Epoch, "session"), cancellation.Token));
    }

    [TestMethod]
    public async Task MalformedAndOversizedOutput_IsRefusedWithoutTruncation()
    {
        var entry = new SessionRuntimeCurrentEntry(1, false, false, "run", false, "fake", "key", "model", null, "default", null);
        var good = new SessionRuntimeCurrentState(Guid.NewGuid(), "session", false, entry);
        foreach (var bad in new[] { good with { SessionId = "other" }, good with { RuntimeInstanceId = Guid.Empty },
            good with { Entry = entry with { AttachmentGeneration = 0 } },
            good with { Entry = entry with { ActiveRunId = "\ud800" } },
            good with { Entry = entry with { ActiveRunId = " padded " } },
            good with { Entry = entry with { ProviderId = new string('x', 257) } },
            good with { Entry = entry with { ProviderKey = "\udc00" } },
            good with { Entry = entry with { ModelId = new string('x', 257) } },
            good with { Entry = entry with { ReasoningEffort = (AgentReasoningEffort)int.MaxValue } },
            good with { Entry = entry with { AgentPromptId = "\ud800" } },
            good with { Entry = entry with { PendingAgentPromptId = new string('x', 257) } } })
        {
            var service = new SessionRuntimeStateService((_, _) => Task.FromResult(bad), Epoch);
            var result = await service.CurrentAsync(new(Epoch, "session"), default);
            Assert.AreEqual("wire_limit", result.Status);
            Assert.IsNull(result.Entry);
            Assert.IsNull(result.CoordinatorTransitionInProgress);
        }
    }

    [TestMethod]
    public async Task MaximumEscapingFitsBudget_AndGenerationIsAnExactDecimalString()
    {
        var id = new string('"', 256);
        var text = new string('\0', 256);
        var snapshot = new SessionRuntimeCurrentState(Guid.NewGuid(), id, true,
            new(long.MaxValue, true, true, text, true, text, text, text, AgentReasoningEffort.High, text, text));
        var service = new SessionRuntimeStateService((_, _) => Task.FromResult(snapshot), Epoch);
        var response = await service.CurrentAsync(new(Epoch, id), default);
        Assert.AreEqual("ok", response.Status);
        var json = JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.SessionRuntimeStateResponse);
        Console.WriteLine($"Escaped current-state response: {json.Length}; with framing allowance: {json.Length + 4096}; budget: {SessionRuntimeStateService.MaximumResponseBytes}.");
        Assert.IsTrue(json.Length + 4096 < SessionRuntimeStateService.MaximumResponseBytes);
        using var parsed = JsonDocument.Parse(json);
        var generation = parsed.RootElement.GetProperty("entry").GetProperty("attachmentGeneration");
        Assert.AreEqual(JsonValueKind.String, generation.ValueKind);
        Assert.AreEqual("9223372036854775807", generation.GetString());
        Assert.AreEqual(text, response.Entry!.PendingAgentPromptId);
    }
}

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

    [TestMethod]
    public void UnaryQuery_IsRegisteredOnlyForOwnedDesktop_AndUsesActualRuntime()
    {
        string Read(string path) => File.ReadAllText(Path.Combine(DesktopArchitectureTests.SourceRoot, "CodeAlta", path));
        var app = Read("Desktop/DesktopApplication.cs");
        var registration = "builder.AddSessionRuntimeStateService(new SessionRuntimeStateService(host.RuntimeService, epoch));";
        Assert.AreEqual(1, app.Split(registration, StringSplitOptions.None).Length - 1);
        Assert.IsTrue(app.IndexOf(registration, StringComparison.Ordinal) > app.IndexOf("private async ValueTask RunOwnedAsync", StringComparison.Ordinal));
        Assert.IsTrue(app.IndexOf(registration, StringComparison.Ordinal) < app.IndexOf("private async ValueTask RunAsync", StringComparison.Ordinal));
        StringAssert.Contains(Read("Desktop/Rpc/SessionRuntimeStateRpc.cs"), "runtime.GetCurrentStateAsync");
        StringAssert.Contains(Read("frontend/src/main.tsx"), "useState(() => createRuntimeStateReader(sessionRuntimeState.current))");
        StringAssert.Contains(Read("frontend/src/OwnedSessionPanel.tsx"), "runtimeReader.forSelection({ expectedHostEpoch: epoch, sessionId }, controller.signal, setRuntimeState)");
        using var manifest = JsonDocument.Parse(Read("obj/neoastra/neoastra.manifest.json"));
        var command = manifest.RootElement.GetProperty("services").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "runtimeState")
            .GetProperty("commands").EnumerateArray().Single();
        Assert.AreEqual("current", command.GetProperty("name").GetString());
        Assert.IsFalse(command.TryGetProperty("channel", out var channel) && channel.GetBoolean());
        var runtime = File.ReadAllText(Path.Combine(DesktopArchitectureTests.SourceRoot, "CodeAlta.Orchestration", "Runtime", "SessionRuntimeService.cs"));
        var start = runtime.IndexOf("public async Task<SessionRuntimeCurrentState> GetCurrentStateAsync", StringComparison.Ordinal);
        var end = runtime.IndexOf("public async Task<bool> HasActiveRunAsync", start, StringComparison.Ordinal);
        var query = runtime[start..end];
        StringAssert.Contains(query, "AdmitAsync(() => GetCurrentStateOwnedBodyAsync(sessionId), cancellationToken)");
        StringAssert.Contains(query, "_sessionActors.TryGet(sessionId, out var actor)");
        StringAssert.Contains(query, "actor.QueryAsync");
        StringAssert.Contains(query, "CancellationToken.None");
        foreach (var forbidden in new[] { "GetActorForWork(", "GetOrCreate(", "_sessionViewCatalog.", "_agentHub.", "_projectCatalog.", "Display.", "ReadLatestLocalStateAsync(" })
            Assert.IsFalse(query.Contains(forbidden, StringComparison.Ordinal), forbidden);
    }
}

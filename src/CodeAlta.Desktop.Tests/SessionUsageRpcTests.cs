using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class SessionUsageRpcTests
{
    private const string Epoch = "00000000-0000-0000-0000-000000000001";
    private const string ProjectId = "00000000-0000-0000-0000-000000000002";

    [TestMethod]
    public async Task WrongEpochOrScopeAndPreCancellationDoNotQueryOwner()
    {
        var service = new SessionUsageService((_, _, _, _, _) => throw new AssertFailedException("Read forbidden."), Epoch);
        Assert.AreEqual("stale_epoch", (await service.ReadAsync(new("old", "session", "global", null, null), default)).Status);
        foreach (var request in new SessionUsageRequest?[] { null, new(Epoch, "", "global", null, null),
            new(Epoch, "session", "global", ProjectId, null), new(Epoch, "session", "project", null, null),
            new(Epoch, "session", "project", ProjectId, "relative"), new(Epoch, "../x", "global", null, null) })
            Assert.AreEqual("invalid_request", (await service.ReadAsync(request!, default)).Status);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReadAsync(new(Epoch, "session", "global", null, null), new CancellationToken(true)));
    }

    [TestMethod]
    public async Task TypedLargeNumbersZeroAndInvalidOmissionFlagsSerializeWithoutAliasesOrPrivateText()
    {
        var time = new DateTimeOffset(2026, 9, 25, 10, 11, 12, TimeSpan.Zero);
        var usage = new SessionRuntimeUsageObservation(long.MaxValue, time, time, AgentUsageScope.LastOperation,
            AgentUsageSource.CodexTokenCountEvent,
            new(0, long.MaxValue, 0), new(long.MaxValue, 0, null, null, null, null, 0, null), true, true);
        var state = new OwnedUsageReadResult("ok", Guid.NewGuid(), "session", long.MaxValue, usage, long.MaxValue);
        var service = new SessionUsageService((_, _, _, _, _) => Task.FromResult(state), Epoch);
        var response = await service.ReadAsync(new(Epoch, "session", "project", ProjectId, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)), default);
        Assert.AreEqual("ok", response.Status);
        var json = JsonSerializer.Serialize(response, DesktopJsonContext.Default.SessionUsageResponse);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        Assert.AreEqual(long.MaxValue.ToString(), root.GetProperty("attachmentGeneration").GetString());
        Assert.AreEqual(long.MaxValue.ToString(), root.GetProperty("observation").GetProperty("sequence").GetString());
        Assert.AreEqual("0", root.GetProperty("observation").GetProperty("window").GetProperty("currentTokens").GetString());
        Assert.AreEqual(long.MaxValue.ToString(), root.GetProperty("observation").GetProperty("window").GetProperty("tokenLimit").GetString());
        Assert.IsTrue(response.Observation!.HadInvalidValues);
        Assert.IsTrue(response.Observation.HadOmittedData);
        Assert.IsFalse(json.Contains("private", StringComparison.Ordinal));
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(json) <= SessionUsageService.MaximumResponseBytes);
    }

    [TestMethod]
    public async Task NoObservationTransitionsAndFaultsNeverImplyZero()
    {
        var service = new SessionUsageService((_, _, _, _, _) => Task.FromResult(new OwnedUsageReadResult("no_observation", Guid.NewGuid(), "session", 1, null, 0)), Epoch);
        var request = new SessionUsageRequest(Epoch, "session", "global", null, null);
        var empty = await service.ReadAsync(request, default);
        Assert.AreEqual("no_observation", empty.Status);
        Assert.IsNull(empty.Observation);
        foreach (var status in new[] { "transition", "stale_attachment", "metadata_missing", "missing_project", "incomplete_project" })
        {
            service = new SessionUsageService((_, _, _, _, _) => Task.FromResult(new OwnedUsageReadResult(status, Guid.NewGuid(), "session", null, null, 0)), Epoch);
            var result = await service.ReadAsync(request, default);
            Assert.AreEqual(status, result.Status);
            Assert.IsNull(result.AttachmentGeneration);
            Assert.IsNull(result.Observation);
        }
        var failure = new SessionUsageService((_, _, _, _, _) => throw new IOException("private path"), Epoch);
        var error = await failure.ReadAsync(request, default);
        Assert.AreEqual("read_failed", error.Status);
        Assert.IsFalse(JsonSerializer.Serialize(error, DesktopJsonContext.Default.SessionUsageResponse).Contains("private path", StringComparison.Ordinal));
        var bad = new SessionRuntimeUsageObservation(1, null, null, AgentUsageScope.Unknown, AgentUsageSource.Unknown,
            null, new(InputTokens: -1, OutputTokens: null, CacheReadTokens: null, CacheWriteTokens: null,
                CachedInputTokens: null, ReasoningTokens: null, Cost: double.NaN, DurationMs: null), true, true);
        var invalid = new SessionUsageService((_, _, _, _, _) => Task.FromResult(new OwnedUsageReadResult("ok", Guid.NewGuid(), "session", 1, bad, 0)), Epoch);
        Assert.AreEqual("read_failed", (await invalid.ReadAsync(request, default)).Status);
    }
}

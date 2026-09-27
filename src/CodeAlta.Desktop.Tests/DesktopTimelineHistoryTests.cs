using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>Literal RPC callbacks only; no store, provider, native window or host acquisition.</summary>
[TestClass]
public sealed class DesktopTimelineHistoryTests
{
    [TestMethod]
    public async Task Timeline_ProjectsLargeRecordAndCarriesFullSourceIdentityWithinWireBudget()
    {
        var value = new AgentContentCompletedEvent(new("p"), "event-session", DateTimeOffset.UnixEpoch, null,
            AgentContentKind.Assistant, "content", null, new string('\u0001', 2 * 1024 * 1024));
        var result = await WorkspaceService.ReadTimelineAsync(new("selected", null), (_, _, _) => Task.FromResult(
            new AgentSessionHistoryPage([new(0, value) { SourceEnd = 4_000_000 }], null, false)
            { Revision = new("selected", 4_000_000, 7) }), CancellationToken.None);
        Assert.AreEqual("ok", result.Page.Status);
        Assert.IsTrue(result.Page.Entries.Single().TextTruncated);
        Assert.AreEqual("selected", result.Revision!.SessionId);
        Assert.AreEqual("event-session", result.Page.Entries.Single().SessionId);
        Assert.AreEqual("4000000", result.Sources.Single().End);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.TimelineHistoryResponse).Length + 8192 < 700 * 1024);
        var limited = await WorkspaceService.ReadTimelineAsync(new("selected", null), (_, _, _) =>
            Task.FromException<AgentSessionHistoryPage>(new AgentSessionHistoryException("record_too_large")), CancellationToken.None);
        Assert.AreEqual("timeline_record_too_large", limited.Page.Status);
        Assert.IsNull(limited.Revision); Assert.AreEqual(0, limited.Sources.Length);
    }

    [TestMethod]
    public async Task Source_ValidatesRangeBeforeRead_AndBoundsEscapedResponse()
    {
        var request = new HistorySourceRequest(new("selected", "4000000", "7"), "0", "4000000", "0");
        foreach (var bad in new[] { request with { Offset = "-1" }, request with { Offset = "4000000" },
            request with { End = "4000001" }, request with { End = "9223372036854775808" } })
            Assert.AreEqual("invalid_cursor", (await WorkspaceService.ReadSourceAsync(bad, (_, _, _, _, _) =>
                throw new AssertFailedException("Invalid request reached storage."), CancellationToken.None)).Status);
        var result = await WorkspaceService.ReadSourceAsync(request, (revision, start, end, offset, _) =>
        {
            Assert.AreEqual("selected", revision.SessionId); Assert.AreEqual(0L, offset);
            Assert.AreEqual(0L, start); Assert.AreEqual(4_000_000L, end);
            return Task.FromResult(new AgentHistorySourceChunk(new string('\u0001', 16 * 1024), 16 * 1024));
        }, CancellationToken.None);
        Assert.AreEqual("ok", result.Status);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.HistorySourceResponse).Length < 104 * 1024);
        Assert.AreEqual("wire_limit", (await WorkspaceService.ReadSourceAsync(request, (_, _, _, _, _) =>
            Task.FromResult(new AgentHistorySourceChunk(new string('a', 16 * 1024 + 1), null)), CancellationToken.None)).Status);
        foreach (var code in new[] { "history_changed", "invalid_cursor", "outside_root", "unsupported_format" })
            Assert.AreEqual(code, (await WorkspaceService.ReadSourceAsync(request, (_, _, _, _, _) =>
                Task.FromException<AgentHistorySourceChunk>(new AgentSessionHistoryException(code)), CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Timeline_FullHundredEscapedRowsAndSourceEnvelopeFitUnchangedResponseCeiling()
    {
        var identity = new string('é', 128);
        var rows = Enumerable.Range(0, 100).Select(i => new AgentSessionHistoryEntry(i * 100_000L,
            new AgentContentCompletedEvent(new(identity), identity, DateTimeOffset.UnixEpoch, new(identity),
                AgentContentKind.Assistant, identity, identity, new string('\u0001', 32 * 1024)))
            { SourceEnd = (i + 1) * 100_000L }).ToArray();
        var result = await WorkspaceService.ReadTimelineAsync(new(identity, null), (_, _, _) => Task.FromResult(
            new AgentSessionHistoryPage(rows, new(identity, 10_000_000, 7, 1), false)
            { Revision = new(identity, 10_000_000, 7) }), CancellationToken.None);
        Assert.AreEqual("ok", result.Page.Status);
        Assert.AreEqual(100, result.Page.Entries.Length); Assert.AreEqual(100, result.Sources.Length);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.TimelineHistoryResponse);
        Assert.IsTrue(bytes.Length + 8192 < 700 * 1024, $"Serialized DTO plus envelope reserve: {bytes.Length + 8192}");
        var invalid = await WorkspaceService.ReadTimelineAsync(new("selected", null), (_, _, _) => Task.FromResult(
            new AgentSessionHistoryPage([], null, false) { Revision = new("other", 100, 7) }), CancellationToken.None);
        Assert.AreEqual("invalid_cursor", invalid.Page.Status);
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => WorkspaceService.ReadSourceAsync(
            new(new("s", "100", "7"), "0", "100", "0"), (_, _, _, _, _) =>
                throw new AssertFailedException("Canceled source read reached storage."), canceled));
    }
}

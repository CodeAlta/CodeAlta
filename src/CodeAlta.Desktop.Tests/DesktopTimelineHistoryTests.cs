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
    public async Task NewestTimeline_IsUnchangedWithoutRows_WhileTheJournalKeepsTheKnownStamp()
    {
        var value = new AgentContentCompletedEvent(new("p"), "selected", DateTimeOffset.UnixEpoch, null, AgentContentKind.Assistant, "content", null, "text");
        var page = new AgentSessionHistoryPage([new(0, value) { SourceEnd = 10 }], null, false) { Revision = new("selected", 10, 7) };
        string? asked = "not asked";
        Task<CodeAlta.Orchestration.Runtime.OwnedTimelineTail> Read(string id, string? known, bool unchanged, string? stamp)
        {
            asked = known;
            return Task.FromResult(new CodeAlta.Orchestration.Runtime.OwnedTimelineTail(unchanged ? null : page, stamp));
        }

        var read = await WorkspaceService.ReadTimelineTailAsync(new("selected", null), (id, known, _) => Read(id, known, false, "stamp-1"), default);
        Assert.AreEqual(("ok", "stamp-1"), (read.Page.Status, read.Stamp));
        Assert.IsNull(asked);
        Assert.AreEqual("7", read.Revision!.LastWriteUtcTicks);
        StringAssert.Contains(JsonSerializer.Serialize(read, DesktopJsonContext.Default.TimelineHistoryResponse), "\"stamp\":\"stamp-1\"");

        var unchanged = await WorkspaceService.ReadTimelineTailAsync(new("selected", null, "stamp-1"), (id, known, _) => Read(id, known, true, known), default);
        Assert.AreEqual("stamp-1", asked);
        Assert.AreEqual(("unchanged", 0, "stamp-1"), (unchanged.Page.Status, unchanged.Page.Entries.Length, unchanged.Stamp));
        Assert.IsNull(unchanged.Revision);
        Assert.IsEmpty(unchanged.Sources);

        // An uncertain read has no stamp, and a stamp no host could have given is not passed on.
        Assert.IsNull((await WorkspaceService.ReadTimelineTailAsync(new("selected", null), (id, known, _) => Read(id, known, false, null), default)).Stamp);
        Assert.IsFalse(JsonSerializer.Serialize(await WorkspaceService.ReadTimelineTailAsync(new("selected", null), (id, known, _) => Read(id, known, false, null), default),
            DesktopJsonContext.Default.TimelineHistoryResponse).Contains("\"stamp\"", StringComparison.Ordinal));
        await WorkspaceService.ReadTimelineTailAsync(new("selected", null, new string('s', 513)), (id, known, _) => Read(id, known, false, null), default);
        Assert.IsNull(asked);
        // Invalid requests are refused before any read, as before.
        Assert.AreEqual("invalid_cursor", (await WorkspaceService.ReadTimelineTailAsync(new(" ", null, "stamp-1"),
            (_, _, _) => throw new AssertFailedException("Invalid request reached storage."), default)).Page.Status);
    }

    [TestMethod]
    public async Task HistoryReads_RefusedForAFullHost_AreBusy()
    {
        var busy = Task.FromException<AgentSessionHistoryPage>(new CodeAlta.Orchestration.Runtime.OwnedWorkspaceBusyException());
        Assert.AreEqual("busy", (await WorkspaceService.ReadTimelineAsync(new("selected", null), (_, _, _) => busy, default)).Page.Status);
        Assert.AreEqual("busy", (await WorkspaceService.ReadHistoryTailAsync(new("selected", null), (_, _, _) => busy, default)).Status);
        Assert.AreEqual("busy", (await WorkspaceService.ReadTimelineTailAsync(new("selected", null), (_, _, _) =>
            Task.FromException<CodeAlta.Orchestration.Runtime.OwnedTimelineTail>(new CodeAlta.Orchestration.Runtime.OwnedWorkspaceBusyException()), default)).Page.Status);
        Assert.AreEqual("busy", (await WorkspaceService.ReadSourceAsync(new HistorySourceRequest(new("selected", "100", "7"), "0", "100", "0"),
            (_, _, _, _, _) => Task.FromException<AgentHistorySourceChunk>(new CodeAlta.Orchestration.Runtime.OwnedWorkspaceBusyException()), default)).Status);
    }

    [TestMethod]
    public async Task Timeline_NamesThePromptsTheHostSent_ByTheirProvenance()
    {
        AgentContentCompletedEvent Content(AgentContentKind kind, string id, string? provenance) => new(new("p"), "child", DateTimeOffset.UnixEpoch, new("run"), kind, id, null, "text")
        {
            Provenance = provenance,
        };
        var result = await WorkspaceService.ReadTimelineAsync(new("child", null), (_, _, _) => Task.FromResult(
            new AgentSessionHistoryPage([new(0, Content(AgentContentKind.User, "user:1", AgentPromptProvenance.Reminder)) { SourceEnd = 10 },
                new(10, Content(AgentContentKind.User, "user:2", null)) { SourceEnd = 20 },
                new(20, Content(AgentContentKind.User, "user:3", "Not An Id")) { SourceEnd = 30 },
                new(30, Content(AgentContentKind.Assistant, "assistant:1", AgentPromptProvenance.Reminder)) { SourceEnd = 40 }], null, false)
            { Revision = new("child", 40, 7) }), CancellationToken.None);

        Assert.AreEqual("ok", result.Page.Status);
        // A prompt of the host is named by its provenance; a prompt of the user, a malformed provenance and an answer of the model are not.
        CollectionAssert.AreEqual(new[] { AgentPromptProvenance.Reminder, null, null, null }, result.Page.Entries.Select(static entry => entry.Provenance).ToArray());
        StringAssert.Contains(JsonSerializer.Serialize(result, DesktopJsonContext.Default.TimelineHistoryResponse), "\"provenance\":\"reminder\"");
    }

    [TestMethod]
    public async Task Timeline_NamesTheSessionOfTheAgentThatSentAPrompt()
    {
        AgentContentCompletedEvent Content(AgentContentKind kind, string id, string? source) => new(new("p"), "child", DateTimeOffset.UnixEpoch, new("run"), kind, id, null, "text", SourceSessionId: source);
        var result = await WorkspaceService.ReadTimelineAsync(new("child", null), (_, _, _) => Task.FromResult(
            new AgentSessionHistoryPage([new(0, Content(AgentContentKind.User, "user:1", "parent-session")) { SourceEnd = 10 },
                new(10, Content(AgentContentKind.User, "user:2", null)) { SourceEnd = 20 },
                new(20, Content(AgentContentKind.Assistant, "assistant:1", "parent-session")) { SourceEnd = 30 }], null, false)
            { Revision = new("child", 30, 7) }), CancellationToken.None);

        Assert.AreEqual("ok", result.Page.Status);
        // The prompt of a parent names it; the prompt of a person and the answer of the model name nobody.
        CollectionAssert.AreEqual(new[] { "parent-session", null, null }, result.Page.Entries.Select(static entry => entry.SourceSessionId).ToArray());
        StringAssert.Contains(JsonSerializer.Serialize(result, DesktopJsonContext.Default.TimelineHistoryResponse), "\"sourceSessionId\":\"parent-session\"");
        // An identity that is no identity is refused with the page, as the others are.
        var refused = await WorkspaceService.ReadTimelineAsync(new("child", null), (_, _, _) => Task.FromResult(
            new AgentSessionHistoryPage([new(0, Content(AgentContentKind.User, "user:1", new string('x', 300))) { SourceEnd = 10 }], null, false)
            { Revision = new("child", 10, 7) }), CancellationToken.None);
        Assert.AreNotEqual("ok", refused.Page.Status);
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

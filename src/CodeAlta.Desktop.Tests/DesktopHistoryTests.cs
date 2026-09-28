using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>Literal callbacks and DTOs only; never constructs the service or a store.</summary>
[TestClass]
public sealed class DesktopHistoryTests
{
    [TestMethod]
    public void FileProjection_PreservesLaterPathsBeforeRawDetailsTruncation()
    {
        var diff = "diff --git a/large.cs b/large.cs\n--- a/large.cs\n+++ b/large.cs\n@@ -0,0 +1,1 @@\n+"
            + new string('x', 9000) + "\ndiff --git a/last.cs b/last.cs\n--- a/last.cs\n+++ b/last.cs\n@@ -1 +1 @@\n-old\n+new\n";
        var result = HistoryFileProjection.Project(JsonSerializer.Serialize(new { diff }), 16000, out var cost);
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Rows.Length);
        Assert.AreEqual("last.cs", result.Rows[1].Path);
        Assert.IsNull(result.Rows[0].Diff);
        StringAssert.Contains(result.Rows[1].Diff!, "+new");
        Assert.IsTrue(result.Partial);
        Assert.IsTrue(cost <= 16000);
    }

    [TestMethod]
    public async Task Read_UsesMandatoryCallbackAndPreservesCancellation()
    {
        var calls = 0;
        var request = new HistoryRequest("selected", null);
        var result = await WorkspaceService.ReadHistoryAsync(request, (id, cursor, token) =>
        {
            calls++;
            Assert.AreEqual("selected", id);
            Assert.IsNull(cursor);
            Assert.AreEqual(CancellationToken.None, token);
            return Task.FromResult(new AgentSessionHistoryPage([], null, false));
        }, CancellationToken.None);
        Assert.AreEqual(1, calls);
        Assert.AreEqual("ok", result.Status);
        var cancellation = new OperationCanceledException("literal callback cancellation");
        var actual = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => WorkspaceService.ReadHistoryAsync(request,
            (_, _, _) => Task.FromException<AgentSessionHistoryPage>(cancellation), CancellationToken.None));
        Assert.AreSame(cancellation, actual);
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => WorkspaceService.ReadHistoryAsync(request,
            (_, _, _) => Task.FromCanceled<AgentSessionHistoryPage>(canceled), canceled));
        Assert.AreEqual("unconfigured", (await WorkspaceService.ReadHistoryAsync(request, null, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Read_ValidatesRequestBeforeStorage()
    {
        foreach (var request in new[]
        {
            new HistoryRequest("", null), new HistoryRequest(new string('x', 257), null),
            new HistoryRequest("s", new(2, "s", "10", "7", "2")),
            new HistoryRequest("s", new(1, "other", "10", "7", "2")),
            new HistoryRequest("s", new(1, "s", "10", "7", "0")),
            new HistoryRequest("s", new(1, "s", "10", "7", "1.5")),
            new HistoryRequest("s", new(1, "s", "10", "7", "9223372036854775808"))
        })
        {
            var result = await WorkspaceService.ReadHistoryAsync(request, (_, _, _) =>
                throw new AssertFailedException("Invalid input reached storage."), CancellationToken.None);
            Assert.AreEqual("invalid_cursor", result.Status);
        }
        var valid = new HistoryRequest("s", new(1, "s", "9007199254740993", "7", "9007199254740992"));
        await WorkspaceService.ReadHistoryAsync(valid, (_, cursor, _) =>
        {
            Assert.AreEqual(9007199254740993L, cursor!.Length);
            Assert.AreEqual(9007199254740992L, cursor.Offset);
            return Task.FromResult(new AgentSessionHistoryPage([], null, false));
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task Read_MapsExpectedFailuresWithoutInfrastructureDetails()
    {
        foreach (var code in new[] { "history_changed", "missing_session", "outside_root", "unsupported_format", "record_too_large", "corrupt_record" })
        {
            var response = await WorkspaceService.ReadHistoryAsync(new("s", null), (_, _, _) =>
                Task.FromException<AgentSessionHistoryPage>(new AgentSessionHistoryException(code)), CancellationToken.None);
            Assert.AreEqual(code, response.Status);
            Assert.AreEqual(0, response.Entries.Length);
            Assert.IsNull(response.Next);
        }
        var failure = await WorkspaceService.ReadHistoryAsync(new("s", null), (_, _, _) =>
            Task.FromException<AgentSessionHistoryPage>(new IOException("private/cache/credentials")), CancellationToken.None);
        Assert.AreEqual("read_failed", failure.Status);
        Assert.IsFalse(JsonSerializer.Serialize(failure, DesktopJsonContext.Default.HistoryResponse).Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadTail_UsesSeparateVersionedCursorAndPreservesWireAndFailureGuards()
    {
        var request = new HistoryRequest("selected", null);
        var page = await WorkspaceService.ReadHistoryTailAsync(request, (id, cursor, _) =>
        {
            Assert.AreEqual("selected", id);
            Assert.IsNull(cursor);
            return Task.FromResult(new AgentSessionHistoryPage([], new("selected", 400, 7, 200), false));
        }, CancellationToken.None);
        Assert.AreEqual(2, page.Next!.Version);
        var continued = new HistoryRequest("selected", page.Next);
        var result = await WorkspaceService.ReadHistoryTailAsync(continued, (_, cursor, _) =>
        {
            Assert.AreEqual(200L, cursor!.Offset);
            return Task.FromResult(new AgentSessionHistoryPage([], null, false));
        }, CancellationToken.None);
        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual("invalid_cursor", (await WorkspaceService.ReadHistoryAsync(continued, (_, _, _) =>
            throw new AssertFailedException("Tail cursor reached forward reader."), CancellationToken.None)).Status);
        Assert.AreEqual("invalid_cursor", (await WorkspaceService.ReadHistoryTailAsync(new("selected", page.Next with { Version = 1 }),
            (_, _, _) => throw new AssertFailedException("Forward cursor reached tail reader."), CancellationToken.None)).Status);
        Assert.AreEqual("unconfigured", (await WorkspaceService.ReadHistoryTailAsync(request, null, CancellationToken.None)).Status);
        Assert.AreEqual("history_changed", (await WorkspaceService.ReadHistoryTailAsync(continued,
            (_, _, _) => Task.FromException<AgentSessionHistoryPage>(new AgentSessionHistoryException("history_changed")), CancellationToken.None)).Status);
        Assert.AreEqual("wire_limit", (await WorkspaceService.ReadHistoryTailAsync(request,
            (_, _, _) => Task.FromResult(new AgentSessionHistoryPage(Enumerable.Range(0, 101)
                .Select(i => new AgentSessionHistoryEntry(i, new AgentErrorEvent(new("p"), "runtime", DateTimeOffset.UnixEpoch, "error"))).ToArray(),
                new("selected", 400, 7, 200), false)), CancellationToken.None)).Status);
    }

    [TestMethod]
    public void Projection_PreservesPersistedEventMeaning()
    {
        var events = new AgentEvent[]
        {
            new AgentContentDeltaEvent(new("provider"), "runtime-session", DateTimeOffset.UnixEpoch, new("run"), AgentContentKind.Reasoning, "content", "parent", "delta"),
            new AgentContentCompletedEvent(new("provider"), "runtime-session", DateTimeOffset.UnixEpoch, new("run"), AgentContentKind.Assistant, "content", "parent", "final"),
            new AgentActivityEvent(new("provider"), "runtime-session", DateTimeOffset.UnixEpoch, null, AgentActivityKind.ToolCall, AgentActivityPhase.Started, "activity", "parent", "tool", "message"),
            new AgentRawEvent(new("provider"), "runtime-session", DateTimeOffset.UnixEpoch, "provider.raw", JsonSerializer.SerializeToElement(new { secret = "omitted" })),
            new AgentNotesEvent(new("provider"), "runtime-session", DateTimeOffset.UnixEpoch, null, AgentNotesUpdateKind.Cleared, ""),
            new AgentErrorEvent(new("provider"), "runtime-session", DateTimeOffset.UnixEpoch, "persisted error")
        };
        var result = WorkspaceService.ProjectHistory(new(events.Select((value, i) => new AgentSessionHistoryEntry(i, value)).ToArray(), null, true));
        CollectionAssert.AreEqual(new[] { "contentDelta", "contentCompleted", "activity", "raw", "notes", "error" }, result.Entries.Select(value => value.EventType).ToArray());
        Assert.AreEqual("runtime-session", result.Entries[0].SessionId);
        Assert.AreEqual("run", result.Entries[0].RunId);
        Assert.AreEqual("Reasoning", result.Entries[0].Kind);
        Assert.AreEqual("Started", result.Entries[2].Phase);
        Assert.AreEqual("tool", result.Entries[2].Name);
        Assert.IsTrue(result.Entries[3].BodyOmitted);
        Assert.IsNull(result.Entries[3].Text);
        Assert.AreEqual("Cleared", result.Entries[4].Kind);
        Assert.AreEqual("", result.Entries[4].Text);
        Assert.AreEqual("persisted error", result.Entries[5].Text);
        Assert.IsFalse(JsonSerializer.Serialize(result, DesktopJsonContext.Default.HistoryResponse).Contains("secret", StringComparison.Ordinal));
        Assert.IsTrue(result.TailOmitted);
    }

    [TestMethod]
    public void Projection_RejectsMissingRequiredEventIdentities()
    {
        foreach (var value in new AgentEvent[]
        {
            new AgentErrorEvent(default, "runtime", DateTimeOffset.UnixEpoch, "error"),
            new AgentErrorEvent(new("p"), null!, DateTimeOffset.UnixEpoch, "error"),
            new AgentErrorEvent(new("p"), "", DateTimeOffset.UnixEpoch, "error"),
            new AgentErrorEvent(new("p"), " ", DateTimeOffset.UnixEpoch, "error"),
        })
        {
            // Missing JSON constructor arguments can yield these values despite nonnullable annotations.
            Assert.ThrowsExactly<InvalidDataException>(() => WorkspaceService.ProjectHistory(
                new AgentSessionHistoryPage([new AgentSessionHistoryEntry(0, value)], null, false)));
        }
    }

    [TestMethod]
    public void Projection_BoundsGeneratedJsonAndMarksOmissions()
    {
        var text = new string('\u0001', 120_000) + "😀";
        var entries = Enumerable.Range(0, 100).Select(i => new AgentSessionHistoryEntry(i,
            new AgentContentCompletedEvent(new("p"), "runtime", DateTimeOffset.UnixEpoch, null,
                AgentContentKind.Assistant, "content", null, text))).ToArray();
        var response = WorkspaceService.ProjectHistory(new(entries, new("selected", 9007199254740993L, 7, 9007199254740992L), false));
        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual(100, response.Entries.Length);
        Assert.IsTrue(response.Entries.All(value => value.TextTruncated));
        Assert.IsTrue(response.Entries.All(value => value.Text!.Length > 512));
        Assert.AreEqual("9007199254740992", response.Next!.Offset);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.HistoryResponse).Length < 768 * 1024);
        var surrogate = WorkspaceService.ProjectHistory(new([new(0,
            new AgentContentCompletedEvent(new("p"), "runtime", DateTimeOffset.UnixEpoch, null,
                AgentContentKind.Assistant, "content", null, new string('a', 32_767) + "😀z"))], null, false));
        Assert.AreEqual(32_767, surrogate.Entries.Single().Text!.Length);
        Assert.IsTrue(surrogate.Entries.Single().TextTruncated);
    }

    [TestMethod]
    public void Projection_PreservesUsefulMarkdownWithinTheBoundedResponse()
    {
        var markdown = "# Result\n\n" + string.Join('\n', Enumerable.Range(0, 400).Select(index => $"- item {index}"));
        var response = WorkspaceService.ProjectHistory(new([new(0,
            new AgentContentCompletedEvent(new("p"), "runtime", DateTimeOffset.UnixEpoch, null,
                AgentContentKind.Assistant, "content", null, markdown))], null, false));

        Assert.AreEqual("ok", response.Status);
        Assert.AreEqual(markdown, response.Entries.Single().Text);
        Assert.IsFalse(response.Entries.Single().TextTruncated);
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(response, DesktopJsonContext.Default.HistoryResponse).Length < 768 * 1024);
    }

    [TestMethod]
    public void Projection_ProvidesDetailedTimelinePayloads()
    {
        var prompt = new AgentSystemPromptEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"),
            "session_start", "sha256:abc", "default", "System text", "Developer text",
            new("native-system-and-developer", true, false), JsonSerializer.SerializeToElement(new { tools = 3 }),
            new(3, 4, 7, 11, 14), new("initial", ["system"], [], []),
            new("default", "Default", ".alta/prompts/agents/default.prompt.md"));
        var usage = new AgentSessionUsage(
            new(6400, 128000, 12),
            new("model-1", InputTokens: 100, OutputTokens: 25, ReasoningTokens: 8, Cost: .01, DurationMs: 1234, ReasoningEffort: "high"),
            Scope: AgentUsageScope.CurrentWindow, Source: AgentUsageSource.ProviderUsage);
        var events = new AgentEvent[]
        {
            prompt,
            new AgentPlanSnapshotEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"),
                new(AgentPlanChangeKind.Updated, "Implementation plan", [new("Inspect", AgentPlanStepStatus.Completed), new("Build", AgentPlanStepStatus.InProgress)])),
            new AgentSessionUpdateEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"),
                AgentSessionUpdateKind.UsageUpdated, "Usage refreshed", JsonSerializer.SerializeToElement(new { source = "provider" }), usage),
            new AgentActivityEvent(new("provider"), "session", DateTimeOffset.UnixEpoch, new("run"),
                AgentActivityKind.FileChange, AgentActivityPhase.Completed, "activity", null, "Update files", "Changed 2 files",
                JsonSerializer.SerializeToElement(new { files = new[] { "a.cs", "b.cs" } })),
        };

        var response = WorkspaceService.ProjectHistory(new(events.Select((value, index) => new AgentSessionHistoryEntry(index, value)).ToArray(), null, false));

        Assert.AreEqual("Default", response.Entries[0].Name);
        StringAssert.Contains(response.Entries[0].Text, "Developer text");
        StringAssert.Contains(response.Entries[0].Text, "Approximate tokens");
        StringAssert.Contains(response.Entries[0].Details, "tools");
        StringAssert.Contains(response.Entries[1].Text, "- [x] Inspect");
        StringAssert.Contains(response.Entries[1].Text, "in progress");
        StringAssert.Contains(response.Entries[2].Text, "6400 / 128000 tokens");
        StringAssert.Contains(response.Entries[2].Text, "Reasoning tokens");
        StringAssert.Contains(response.Entries[3].Details, "a.cs");
        Assert.IsFalse(response.Entries.Any(static value => value.BodyOmitted));
    }
}

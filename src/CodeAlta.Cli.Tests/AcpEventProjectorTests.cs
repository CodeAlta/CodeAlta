using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Cli.Tests;

/// <summary>The projection of runtime events onto ACP, for the events a fake provider does not produce.</summary>
[TestClass]
public sealed class AcpEventProjectorTests
{
    private const string Root = "root-session";
    private static readonly ModelProviderId Provider = new("fake");

    [TestMethod]
    public async Task CompactionRetryFallbackAndErrors_AreCustomNotifications()
    {
        var sink = new CaptureSink();
        var projector = new AcpEventProjector(sink);
        var now = DateTimeOffset.UtcNow;
        var details = JsonDocument.Parse("""{"reason":"QuotaExhausted","from":"a:one","to":"b:two"}""").RootElement.Clone();

        await projector.ProjectAsync(Of(new AgentSessionUpdateEvent(Provider, Root, now, null, AgentSessionUpdateKind.CompactionStarted, "Compacting.")), default);
        await projector.ProjectAsync(Of(new AgentSessionUpdateEvent(Provider, Root, now, null, AgentSessionUpdateKind.CompactionCompleted, null)), default);
        await projector.ProjectAsync(Of(new AgentSessionUpdateEvent(Provider, Root, now, null, AgentSessionUpdateKind.Reconnecting, "Retrying in 2 s.")), default);
        await projector.ProjectAsync(Of(new AgentSessionUpdateEvent(Provider, Root, now, null, AgentSessionUpdateKind.ProviderFallback, "Going on with b.", details)), default);
        await projector.ProjectAsync(Of(new AgentErrorEvent(Provider, Root, now, "Out of credits.") { Failure = new AgentFailureInfo(AgentFailureKind.QuotaExhausted) }), default);

        CollectionAssert.AreEqual(new[]
        {
            CodeAltaAcpExtensions.CompactionMethod, CodeAltaAcpExtensions.CompactionMethod, CodeAltaAcpExtensions.RetryMethod,
            CodeAltaAcpExtensions.FallbackMethod, CodeAltaAcpExtensions.ErrorMethod,
        }, sink.Extensions.Select(static item => item.Method).ToArray());
        Assert.IsTrue(sink.Extensions.All(static item => item.Parameters.GetProperty("sessionId").GetString() == Root));
        Assert.AreEqual("started", sink.Extensions[0].Parameters.GetProperty("phase").GetString());
        Assert.AreEqual("completed", sink.Extensions[1].Parameters.GetProperty("phase").GetString());
        Assert.AreEqual("Retrying in 2 s.", sink.Extensions[2].Parameters.GetProperty("message").GetString());
        Assert.AreEqual("a:one", sink.Extensions[3].Parameters.GetProperty("from").GetString());
        Assert.AreEqual("b:two", sink.Extensions[3].Parameters.GetProperty("to").GetString());
        Assert.AreEqual("quota_exhausted", sink.Extensions[4].Parameters.GetProperty("kind").GetString());
        Assert.AreEqual(0, sink.Updates.Count);
        Assert.IsTrue(CodeAltaAcpExtensions.Methods.All(static method => method.StartsWith("_codealta/", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PlanAndUsage_AreStandardUpdates()
    {
        var sink = new CaptureSink();
        var projector = new AcpEventProjector(sink);
        var now = DateTimeOffset.UtcNow;

        await projector.ProjectAsync(Of(new AgentPlanSnapshotEvent(Provider, Root, now, null, new AgentPlanSnapshot(AgentPlanChangeKind.Created, null,
            [new AgentPlanStep("Read", AgentPlanStepStatus.Completed), new AgentPlanStep("Write", AgentPlanStepStatus.InProgress), new AgentPlanStep("Test", null)]))), default);
        await projector.ProjectAsync(Of(new AgentSessionUpdateEvent(Provider, Root, now, null, AgentSessionUpdateKind.UsageUpdated, null,
            Usage: new AgentSessionUsage(Window: new AgentWindowUsageSnapshot(CurrentTokens: 1200, TokenLimit: 200000, MessageCount: 4)))), default);
        // Usage without the window: nothing ACP can say.
        await projector.ProjectAsync(Of(new AgentSessionUpdateEvent(Provider, Root, now, null, AgentSessionUpdateKind.UsageUpdated, null, Usage: new AgentSessionUsage())), default);

        Assert.AreEqual(2, sink.Updates.Count);
        var plan = sink.Updates[0].Update.Value;
        Assert.AreEqual("plan", plan.GetProperty("sessionUpdate").GetString());
        CollectionAssert.AreEqual(new[] { "completed", "in_progress", "pending" },
            plan.GetProperty("entries").EnumerateArray().Select(static entry => entry.GetProperty("status").GetString()).ToArray());
        var usage = sink.Updates[1].Update.Value;
        Assert.AreEqual("usage_update", usage.GetProperty("sessionUpdate").GetString());
        Assert.AreEqual(1200, usage.GetProperty("used").GetInt32());
        Assert.AreEqual(200000, usage.GetProperty("size").GetInt32());
    }

    [TestMethod]
    public async Task ToolCalls_MapTheirKind_AndATaskSeenOnlyAtItsEnd_IsAnnouncedOnce()
    {
        var sink = new CaptureSink();
        var projector = new AcpEventProjector(sink);
        var now = DateTimeOffset.UtcNow;
        var details = JsonDocument.Parse("""{"toolName":"shell_command","arguments":{"command":"dotnet test"}}""").RootElement.Clone();

        await projector.ProjectAsync(Of(new AgentActivityEvent(Provider, Root, now, null, AgentActivityKind.ToolCall, AgentActivityPhase.Failed,
            "call-9", null, "shell_command", "exit code 1", details)), default);

        var call = sink.Updates.Single().Update.Value;
        Assert.AreEqual("tool_call", call.GetProperty("sessionUpdate").GetString());
        Assert.AreEqual("execute", call.GetProperty("kind").GetString());
        Assert.AreEqual("failed", call.GetProperty("status").GetString());
        Assert.AreEqual("shell_command dotnet test", call.GetProperty("title").GetString());
        Assert.AreEqual("exit code 1", call.GetProperty("content")[0].GetProperty("content").GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task ToolsOfProviderHarnesses_HaveTheirKind_WhateverTheCase()
    {
        // A session on Claude Code reports its own tools by their names: Read is a read, not "other".
        var sink = new CaptureSink();
        var projector = new AcpEventProjector(sink);
        var details = JsonDocument.Parse("""{"toolName":"Read","arguments":{"file_path":"/repo/Program.cs"}}""").RootElement.Clone();
        await projector.ProjectAsync(Of(new AgentActivityEvent(Provider, Root, DateTimeOffset.UtcNow, null, AgentActivityKind.DynamicToolCall, AgentActivityPhase.Started,
            "call-1", null, "Read", null, details)), default);
        var call = sink.Updates.Single().Update.Value;
        Assert.AreEqual("read", call.GetProperty("kind").GetString());
        Assert.AreEqual("Read /repo/Program.cs", call.GetProperty("title").GetString());

        foreach (var (name, kind) in new[]
        {
            ("Read", "read"), ("NotebookRead", "read"), ("LS", "read"), ("view", "read"),
            ("Edit", "edit"), ("MultiEdit", "edit"), ("Write", "edit"), ("NotebookEdit", "edit"), ("apply_patch", "edit"), ("str_replace_editor", "edit"),
            ("Bash", "execute"), ("PowerShell", "execute"), ("shell", "execute"), ("exec_command", "execute"), ("run_shell_command", "execute"),
            ("Grep", "search"), ("Glob", "search"), ("WebFetch", "fetch"), ("WebSearch", "fetch"), ("web_search", "fetch"),
            ("TodoWrite", "think"), ("update_plan", "think"), ("ExitPlanMode", "switch_mode"),
            ("delete_file_or_dir", "delete"), ("rename_file_or_dir", "move"),
            // Without case, as the names of the ACP plugin and of other harnesses vary.
            ("read", "read"), ("BASH", "execute"), ("glob", "search"), ("webfetch", "fetch"),
        })
        {
            Assert.AreEqual(kind, AcpEventProjector.KindOf(name, AgentActivityKind.DynamicToolCall).Value.GetString(), name);
        }

        Assert.AreEqual("other", AcpEventProjector.KindOf("mcp__docs__search_pages", AgentActivityKind.McpToolCall).Value.GetString());
        Assert.AreEqual("execute", AcpEventProjector.KindOf("anything", AgentActivityKind.CommandExecution).Value.GetString());
    }

    [TestMethod]
    public async Task CompletedText_RepairsWhatTheStreamDropped()
    {
        var sink = new CaptureSink();
        var projector = new AcpEventProjector(sink);
        var run = new AgentRunId("run-1");

        // The last deltas were dropped: the rest of the text is sent once it is complete.
        await projector.ProjectAsync(Of(Delta(run, AgentContentKind.Assistant, "a1", "Hello")), default);
        await projector.ProjectAsync(Of(Completed(run, AgentContentKind.Assistant, "a1", "Hello, world.")), default);
        // A delta in the middle was dropped: the text after the common part is sent, and the client is told.
        await projector.ProjectAsync(Of(Delta(run, AgentContentKind.Reasoning, "t1", "Think ")), default);
        await projector.ProjectAsync(Of(Delta(run, AgentContentKind.Reasoning, "t1", "hard.")), default);
        await projector.ProjectAsync(Of(Completed(run, AgentContentKind.Reasoning, "t1", "Think about it hard.")), default);
        // Nothing was dropped: nothing more is sent.
        await projector.ProjectAsync(Of(Delta(run, AgentContentKind.Assistant, "a2", "Done.")), default);
        await projector.ProjectAsync(Of(Completed(run, AgentContentKind.Assistant, "a2", "Done.")), default);

        CollectionAssert.AreEqual(new[] { "Hello", ", world.", "Done." }, Chunks(sink, "agent_message_chunk"));
        CollectionAssert.AreEqual(new[] { "Think ", "hard.", "about it hard." }, Chunks(sink, "agent_thought_chunk"));
        var dropped = sink.Extensions.Single();
        Assert.AreEqual(CodeAltaAcpExtensions.ErrorMethod, dropped.Method);
        Assert.AreEqual("events_dropped", dropped.Parameters.GetProperty("kind").GetString());
        Assert.AreEqual(Root, dropped.Parameters.GetProperty("sessionId").GetString());
    }

    [TestMethod]
    public async Task TextStreamedInARunThatFailed_DoesNotHideTheAnswerOfTheNextRun()
    {
        var sink = new CaptureSink();
        var projector = new AcpEventProjector(sink);
        var failed = new AgentRunId("run-1");
        var next = new AgentRunId("run-2");

        await projector.ProjectAsync(Of(Delta(failed, AgentContentKind.Assistant, "a1", "Partial")), default);
        await projector.ProjectAsync(Of(new AgentErrorEvent(Provider, Root, DateTimeOffset.UtcNow, "Connection lost.", runId: failed)), default);
        // The next run streams nothing: its answer is sent whole.
        await projector.ProjectAsync(Of(Completed(next, AgentContentKind.Assistant, "a2", "Whole answer.")), default);
        // A run without an id ends with its idle event.
        await projector.ProjectAsync(Of(Delta(null, AgentContentKind.Reasoning, "t1", "Half")), default);
        await projector.ProjectAsync(Of(new AgentSessionUpdateEvent(Provider, Root, DateTimeOffset.UtcNow, null, AgentSessionUpdateKind.Idle, null)), default);
        await projector.ProjectAsync(Of(Completed(null, AgentContentKind.Reasoning, "t2", "A whole thought.")), default);

        CollectionAssert.AreEqual(new[] { "Partial", "Whole answer." }, Chunks(sink, "agent_message_chunk"));
        CollectionAssert.AreEqual(new[] { "Half", "A whole thought." }, Chunks(sink, "agent_thought_chunk"));
    }

    [TestMethod]
    public async Task FileChanges_AreDiffsOnlyWithTheWholeContents()
    {
        var sink = new CaptureSink();
        var projector = new AcpEventProjector(sink);
        var path = Path.Combine(Path.GetTempPath(), "codealta-acp", "a.txt");
        var pathJson = JsonSerializer.Serialize(path);

        // A replacement knows fragments only: the client is given the unified diff of the call.
        var replace = await ToolOutputAsync(projector, sink, "call-1", "replace_in_file",
            $$"""{"toolName":"replace_in_file","arguments":{"path":{{pathJson}},"old_string":"two","new_string":"2"},"modifiedFiles":[{{pathJson}}],"diff":"--- a/a.txt\n+++ b/a.txt\n@@ -1,3 +1,3 @@\n one\n-two\n+2\n three\n"}""");
        Assert.IsFalse(replace.Any(static item => item.GetProperty("type").GetString() == "diff"), replace.Count.ToString());
        StringAssert.Contains(replace[0].GetProperty("content").GetProperty("text").GetString(), "-two\n+2");

        // A new file is known whole: a diff from nothing.
        var created = await ToolOutputAsync(projector, sink, "call-2", "write_file",
            $$"""{"toolName":"write_file","arguments":{"path":{{pathJson}},"content":"one\ntwo\n"},"modifiedFiles":[{{pathJson}}],"diff":"diff --git a/a.txt b/a.txt\nnew file mode 100644\n--- /dev/null\n+++ b/a.txt\n@@ -0,0 +1,2 @@\n+one\n+two\n"}""");
        var diff = created.Single(static item => item.GetProperty("type").GetString() == "diff");
        Assert.AreEqual(path, diff.GetProperty("path").GetString());
        Assert.IsTrue(!diff.TryGetProperty("oldText", out var oldText) || oldText.ValueKind == JsonValueKind.Null);
        Assert.AreEqual("one\ntwo\n", diff.GetProperty("newText").GetString());

        // A file written over: what it was is not known, the unified diff is given.
        var rewritten = await ToolOutputAsync(projector, sink, "call-3", "write_file",
            $$"""{"toolName":"write_file","arguments":{"path":{{pathJson}},"content":"three\n"},"modifiedFiles":[{{pathJson}}],"diff":"--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1 @@\n-one\n-two\n+three\n"}""");
        Assert.IsFalse(rewritten.Any(static item => item.GetProperty("type").GetString() == "diff"));
        StringAssert.Contains(rewritten[0].GetProperty("content").GetProperty("text").GetString(), "+three");
    }

    [TestMethod]
    public async Task ALongToolOutput_IsCutInTheUpdate_WhichSaysHowLongTheWholeIs()
    {
        var sink = new CaptureSink();
        var projector = new AcpEventProjector(sink);

        var content = await ToolOutputAsync(projector, sink, "call-1", "exec", """{"toolName":"exec","arguments":{}}""", new string('x', 12_000));

        var text = content.Single().GetProperty("content").GetProperty("text").GetString()!;
        StringAssert.EndsWith(text, "\n[truncated in this update: 8,192 of 12,000 characters]");
        Assert.AreEqual(AcpEventProjector.MaximumToolOutput, text.IndexOf('\n', StringComparison.Ordinal));
    }

    // The content of the update a tool output became.
    private static async Task<List<JsonElement>> ToolOutputAsync(AcpEventProjector projector, CaptureSink sink, string callId, string tool, string details, string output = "Done.")
    {
        var now = DateTimeOffset.UtcNow;
        var json = JsonDocument.Parse(details).RootElement.Clone();
        await projector.ProjectAsync(Of(new AgentActivityEvent(Provider, Root, now, null, AgentActivityKind.ToolCall, AgentActivityPhase.Started, callId, null, tool, null, json)), default);
        await projector.ProjectAsync(Of(new AgentContentCompletedEvent(Provider, Root, now, null, AgentContentKind.ToolOutput, callId + "-output", callId, output, json)), default);
        var update = sink.Updates[^1].Update.Value;
        Assert.AreEqual("tool_call_update", update.GetProperty("sessionUpdate").GetString());
        return [.. update.GetProperty("content").EnumerateArray()];
    }

    private static AgentContentDeltaEvent Delta(AgentRunId? run, AgentContentKind kind, string contentId, string text)
        => new(Provider, Root, DateTimeOffset.UtcNow, run, kind, contentId, null, text);

    private static AgentContentCompletedEvent Completed(AgentRunId? run, AgentContentKind kind, string contentId, string text)
        => new(Provider, Root, DateTimeOffset.UtcNow, run, kind, contentId, null, text);

    private static string[] Chunks(CaptureSink sink, string kind)
        => [.. sink.Updates.Select(static item => item.Update.Value).Where(update => update.GetProperty("sessionUpdate").GetString() == kind)
            .Select(static update => update.GetProperty("content").GetProperty("text").GetString()!)];

    private static HeadlessSessionEvent Of(AgentEvent agentEvent) => new(Root, new SessionAgentEvent(agentEvent.SessionId, agentEvent));

    private sealed class CaptureSink : IAcpEventSink
    {
        public List<(string SessionId, SessionUpdate Update)> Updates { get; } = [];

        public List<(string Method, JsonElement Parameters)> Extensions { get; } = [];

        public ValueTask SessionUpdateAsync(string sessionId, SessionUpdate update, CancellationToken cancellationToken)
        {
            Updates.Add((sessionId, update));
            return ValueTask.CompletedTask;
        }

        public ValueTask ExtensionAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
        {
            Extensions.Add((method, parameters));
            return ValueTask.CompletedTask;
        }
    }
}

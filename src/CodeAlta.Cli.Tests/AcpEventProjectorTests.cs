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

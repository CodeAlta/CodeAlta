using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;

namespace CodeAlta.Cli.Tests;

/// <summary><c>altacli run</c> end to end, in the test process, on temporary roots and a fake provider.</summary>
[TestClass]
public sealed class CliRunTests
{
    private const string Plain = "--no-plugins";

    [TestMethod]
    public async Task Text_WritesOnlyTheFinalAnswerToStdout_AndTheProgressToStderr()
    {
        using var root = new CliTestRoot();
        File.WriteAllText(Path.Combine(root.ProjectRoot, "a.txt"), "content");
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.CallTool("list_dir", """{"path":"."}"""));
        provider.Turns.Enqueue(FakeProvider.Stream("The final answer.", "Thinking."));

        var run = await root.RunAsync(provider, ["run", Plain, "list", "the", "files"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        Assert.AreEqual("The final answer.\n", run.Stdout);
        StringAssert.Contains(run.Stderr, "> list_dir .");
        Assert.IsFalse(run.Stderr.Contains("The final answer.", StringComparison.Ordinal), run.Stderr);
        Assert.AreEqual("list the files", LastUserText(provider));
    }

    [TestMethod]
    public async Task Quiet_WritesNoProgress()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.CallTool("list_dir", """{"path":"."}"""));

        var run = await root.RunAsync(provider, ["run", Plain, "-q", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        Assert.AreEqual("Done.\n", run.Stdout);
        Assert.AreEqual(string.Empty, run.Stderr);
    }

    [TestMethod]
    [DoNotParallelize]
    public void TakeOverConsole_SendsStrayWritesOfConsoleOutToStderr()
    {
        var original = Console.Out;
        var stderr = new StringWriter();
        try
        {
            var writer = CliStreams.TakeOverConsole(stderr);
            Console.WriteLine("stray");
            Console.Out.Write("more");
            writer.Flush();
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.AreEqual("stray" + Environment.NewLine + "more", stderr.ToString());
    }

    [TestMethod]
    public async Task Jsonl_IsAcpNotifications_HeaderFirst_UpdatesAsSessionNotifications_ResultLast()
    {
        using var root = new CliTestRoot();
        File.WriteAllText(Path.Combine(root.ProjectRoot, "a.txt"), "content");
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.CallTool("read_file", """{"path":"a.txt"}"""));
        provider.Turns.Enqueue(FakeProvider.Stream("All done.", "Let me think."));

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--model", "fake:fake-model@high", "read a.txt"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        Assert.IsTrue(run.Stdout.EndsWith('\n'), "Each line ends with a line feed.");
        Assert.IsFalse(run.Stdout.Contains('\r'), "Lines end with LF alone.");
        var lines = run.Lines.Select(static line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        foreach (var line in lines)
        {
            Assert.AreEqual("2.0", line.GetProperty("jsonrpc").GetString());
            Assert.IsFalse(line.TryGetProperty("id", out _), "A notification has no id.");
        }

        var header = lines[0];
        Assert.AreEqual(CodeAltaAcpExtensions.SessionMethod, header.GetProperty("method").GetString());
        var sessionId = header.GetProperty("params").GetProperty("sessionId").GetString()!;
        Assert.AreEqual(1, header.GetProperty("params").GetProperty("schemaVersion").GetInt32());
        Assert.IsTrue(header.GetProperty("params").GetProperty("created").GetBoolean());
        Assert.AreEqual(root.ProjectRoot, header.GetProperty("params").GetProperty("cwd").GetString());
        Assert.AreEqual("fake", header.GetProperty("params").GetProperty("provider").GetString());
        Assert.AreEqual("fake", header.GetProperty("params").GetProperty("providerType").GetString(), "the type of the provider, which is its id unless it says one");
        Assert.AreEqual("fake-model", header.GetProperty("params").GetProperty("model").GetString());
        Assert.AreEqual("high", header.GetProperty("params").GetProperty("reasoning").GetString());
        Assert.AreEqual("default", header.GetProperty("params").GetProperty("agentPrompt").GetString());
        Assert.AreEqual("visible", header.GetProperty("params").GetProperty("storage").GetString());

        var notificationInfo = (JsonTypeInfo<SessionNotification>)AcpClient.CreateJsonSerializerOptions().GetTypeInfo(typeof(SessionNotification));
        var updates = lines.Where(static line => line.GetProperty("method").GetString() == "session/update")
            .Select(line => line.GetProperty("params").Deserialize(notificationInfo)!)
            .ToArray();
        Assert.IsTrue(updates.All(update => update.SessionId.Value == sessionId));
        var kinds = updates.Select(static update => AcpJson.GetString(update.Update.Value, "sessionUpdate")).ToArray();
        CollectionAssert.Contains(kinds, "agent_message_chunk");
        CollectionAssert.Contains(kinds, "agent_thought_chunk");
        var text = string.Concat(updates.Where(static update => AcpJson.GetString(update.Update.Value, "sessionUpdate") == "agent_message_chunk")
            .Select(static update => update.Update.Value.GetProperty("content").GetProperty("text").GetString()));
        Assert.AreEqual("All done.", text);
        var call = updates.Single(static update => AcpJson.GetString(update.Update.Value, "sessionUpdate") == "tool_call").Update.Value;
        Assert.AreEqual("call-1", call.GetProperty("toolCallId").GetString());
        Assert.AreEqual("read_file a.txt", call.GetProperty("title").GetString());
        Assert.AreEqual("read", call.GetProperty("kind").GetString());
        CollectionAssert.Contains(new[] { "pending", "in_progress" }, call.GetProperty("status").GetString());
        Assert.AreEqual("a.txt", call.GetProperty("rawInput").GetProperty("path").GetString());
        var meta = call.GetProperty("_meta").GetProperty(CodeAltaAcpExtensions.MetaKey);
        Assert.AreEqual("read_file", meta.GetProperty("tool").GetString(), "The extras of CodeAlta are in _meta, never at the root.");
        Assert.IsFalse(call.TryGetProperty("tool", out _));
        var ended = updates.Where(static update => AcpJson.GetString(update.Update.Value, "sessionUpdate") == "tool_call_update").Select(static update => update.Update.Value).ToArray();
        Assert.IsTrue(ended.Any(static update => AcpJson.GetString(update, "status") == "completed"));
        Assert.IsTrue(ended.Any(static update => AcpJson.TryGetArray(update, "content", out var content)
            && content.EnumerateArray().Any(static item => item.GetProperty("content").GetProperty("text").GetString()!.Contains("content", StringComparison.Ordinal))),
            "The output of the tool is in an update.");

        var result = lines[^1];
        Assert.AreEqual(CodeAltaAcpExtensions.ResultMethod, result.GetProperty("method").GetString());
        var parameters = result.GetProperty("params");
        Assert.AreEqual(sessionId, parameters.GetProperty("sessionId").GetString());
        Assert.AreEqual("completed", parameters.GetProperty("status").GetString());
        Assert.AreEqual("end_turn", parameters.GetProperty("stopReason").GetString());
        Assert.AreEqual(0, parameters.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("All done.", parameters.GetProperty("finalMessage").GetString());
        Assert.AreEqual(JsonValueKind.Null, parameters.GetProperty("failure").ValueKind);
        Assert.IsFalse(string.IsNullOrEmpty(parameters.GetProperty("runId").GetString()));
        var usage = parameters.GetProperty("usage");
        // The turn that called the tool reported no usage; the one that answered did.
        Assert.AreEqual(100, usage.GetProperty("inputTokens").GetInt32());
        Assert.AreEqual(20, usage.GetProperty("outputTokens").GetInt32());
        Assert.AreEqual(120, usage.GetProperty("totalTokens").GetInt32());
        Assert.AreEqual(40, usage.GetProperty("cachedReadTokens").GetInt32());
        Assert.AreEqual(5, usage.GetProperty("thoughtTokens").GetInt32());
        Assert.AreEqual(0.25, parameters.GetProperty("cost").GetProperty("amount").GetDouble(), 1e-9);
        Assert.AreEqual("USD", parameters.GetProperty("cost").GetProperty("currency").GetString());
        Assert.IsTrue(parameters.GetProperty("durationMs").GetInt64() >= 0);
        Assert.AreEqual(1, lines.Count(static line => line.GetProperty("method").GetString() == CodeAltaAcpExtensions.ResultMethod));
    }

    [TestMethod]
    public async Task Jsonl_UsageTheProviderDidNotReport_IsNull_NotZero()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        provider.Turns.Enqueue((_, _, _) => Task.FromResult(FakeProvider.Answer("No usage.") with { Usage = null }));

        var none = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, none.ExitCode, none.Stderr);
        Assert.AreEqual(JsonValueKind.Null, Result(none).GetProperty("usage").ValueKind, "No usage reported: unknown, not zero.");
        Assert.AreEqual(JsonValueKind.Null, Result(none).GetProperty("cost").ValueKind);

        provider.Turns.Enqueue((_, _, _) => Task.FromResult(FakeProvider.Answer("Some usage.") with
        {
            Usage = new AgentSessionUsage(LastOperation: new AgentOperationUsageSnapshot(Model: "fake-model", InputTokens: 10, OutputTokens: 2)),
        }));

        var partial = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hello"]);

        Assert.AreEqual(CliExitCodes.Completed, partial.ExitCode, partial.Stderr);
        var usage = Result(partial).GetProperty("usage");
        Assert.AreEqual(10, usage.GetProperty("inputTokens").GetInt32());
        Assert.AreEqual(2, usage.GetProperty("outputTokens").GetInt32());
        foreach (var unknown in new[] { "cachedReadTokens", "cachedWriteTokens", "thoughtTokens" })
        {
            Assert.IsTrue(!usage.TryGetProperty(unknown, out var value) || value.ValueKind == JsonValueKind.Null, $"{unknown} was not reported: {usage}");
        }
    }

    [TestMethod]
    public async Task Jsonl_ACostTheProviderDoesNotReport_IsEstimatedFromThePublicPriceOfTheModel_AndMarkedSo()
    {
        using var root = new CliTestRoot();
        // A million tokens of input, of which 400k read from the cache and 100k written to it, and 50k of output.
        static FakeTurn Unpriced(string model) => (_, _, _) => Task.FromResult(FakeProvider.Answer("Done.") with
        {
            Usage = new AgentSessionUsage(LastOperation: new AgentOperationUsageSnapshot(Model: model, InputTokens: 1_000_000, OutputTokens: 50_000,
                CachedInputTokens: 400_000, CacheWriteTokens: 100_000)),
        });

        // The prices of the bundled models.dev catalog: $4, $0.20, $5, $20 a million tokens for Claude Opus 5.5, and
        // $10, $1, $12.50, $50 for GPT-6 Astra below 272k tokens.
        foreach (var (model, expected) in new[] { ("claude-opus-5-5", 2.0m + 0.08m + 0.5m + 1.0m), ("gpt-6-astra", 5.0m + 0.4m + 1.25m + 2.5m) })
        {
            var provider = new FakeProvider(model: model);
            provider.Turns.Enqueue(Unpriced(model));
            var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--model", "fake:" + model, "hello"]);

            Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
            var cost = Result(run).GetProperty("cost");
            Assert.AreEqual(expected, cost.GetProperty("amount").GetDecimal(), model);
            Assert.AreEqual("USD", cost.GetProperty("currency").GetString());
            Assert.IsTrue(cost.GetProperty("_meta").GetProperty(CodeAltaAcpExtensions.MetaKey).GetProperty("estimated").GetBoolean(), "an estimate says so");
        }

        // The price a provider gives its model comes first; input of the cache is priced as input when it has no price of its own.
        var priced = new FakeProvider(models: [new AgentModelInfo("fake-model", Capabilities: new Dictionary<string, object?>
        {
            ["inputCostPerMillionTokens"] = 2m, ["outputCostPerMillionTokens"] = 10m,
        })]);
        priced.Turns.Enqueue(Unpriced("fake-model"));
        var own = await root.RunAsync(priced, ["run", Plain, "--format", "jsonl", "hello"]);
        Assert.AreEqual(2.0m + 0.5m, Result(own).GetProperty("cost").GetProperty("amount").GetDecimal());

        // A cost the provider reports is never replaced, nor marked.
        var reported = new FakeProvider(model: "claude-opus-5-5");
        var run2 = await root.RunAsync(reported, ["run", Plain, "--format", "jsonl", "--model", "fake:claude-opus-5-5", "hello"]);
        var cost2 = Result(run2).GetProperty("cost");
        Assert.AreEqual(0.25, cost2.GetProperty("amount").GetDouble(), 1e-9);
        Assert.IsFalse(cost2.TryGetProperty("_meta", out _));
    }

    [TestMethod]
    public async Task Jsonl_IsReadByAnAcpClient()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.CallTool("list_dir", """{"path":"."}"""));
        provider.Turns.Enqueue(FakeProvider.Stream("Listed."));
        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "list"]);
        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);

        // An agent that answers initialize, then writes the lines of the run as they are.
        var toAgent = new Pipe();
        var fromAgent = new Pipe();
        var agent = Task.Run(async () =>
        {
            using var reader = new StreamReader(toAgent.Reader.AsStream(), Encoding.UTF8);
            var request = JsonDocument.Parse((await reader.ReadLineAsync())!).RootElement;
            var id = request.GetProperty("id").GetRawText();
            var output = fromAgent.Writer.AsStream();
            await output.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":1,\"agentCapabilities\":{}}}\n"));
            await output.WriteAsync(Encoding.UTF8.GetBytes(run.Stdout));
            await output.FlushAsync();
        });
        var messages = new ConcurrentQueue<AcpServerMessage>();
        await using var client = await AcpClient.ConnectAsync(fromAgent.Reader.AsStream(), toAgent.Writer.AsStream(), new InitializeRequest
        {
            ClientInfo = new Implementation { Name = "test", Version = "1.0.0" },
            ClientCapabilities = new ClientCapabilities(),
            ProtocolVersion = ProtocolVersion.From(1),
        }, messages.Enqueue).WaitAsync(TimeSpan.FromSeconds(30));
        await agent.WaitAsync(TimeSpan.FromSeconds(30));
        var expected = run.Lines.Length;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (messages.Count < expected && DateTime.UtcNow < deadline) await Task.Delay(20);

        var methods = messages.Select(static message => message.Method).ToArray();
        Assert.AreEqual(expected, methods.Length);
        Assert.AreEqual(CodeAltaAcpExtensions.SessionMethod, methods[0]);
        Assert.AreEqual(CodeAltaAcpExtensions.ResultMethod, methods[^1]);
        Assert.IsTrue(messages.All(static message => message.RequestId is null), "Every line is a notification.");
        Assert.IsTrue(methods.Contains("session/update"));
    }

    [TestMethod]
    public async Task Jsonl_AnnouncesAChildSession_WithTheUpdatesOfItsOwnSession()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var childId = Guid.CreateVersion7().ToString();
        provider.Script(childId, FakeProvider.Stream("Child result."));
        provider.Turns.Enqueue(FakeProvider.DoThenReply(async request =>
        {
            var host = root.LastHost!;
            var options = new CodeAlta.Orchestration.Runtime.SessionExecutionOptions
            {
                ProviderId = provider.Descriptor.ProviderId,
                ProviderKey = provider.Descriptor.ProviderId.Value,
                Model = provider.Model,
                WorkingDirectory = root.ProjectRoot,
                ProjectRoots = [root.ProjectRoot],
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            };
            var child = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, options, "Child work", request.SessionId, null, null, null, childId);
            _ = host.RuntimeService.SendAsync(child, options, new AgentSendOptions { Input = AgentInput.Text("child work") });
        }, "Started a child."));
        provider.Turns.Enqueue(FakeProvider.Reply("The child said it."));

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "delegate"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        var lines = run.Lines.Select(static line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var rootId = lines[0].GetProperty("params").GetProperty("sessionId").GetString();
        var started = lines.Single(static line => line.GetProperty("method").GetString() == CodeAltaAcpExtensions.SessionStartedMethod).GetProperty("params");
        Assert.AreEqual(childId, started.GetProperty("sessionId").GetString());
        Assert.AreEqual(rootId, started.GetProperty("parentSessionId").GetString());
        Assert.AreEqual("Child work", started.GetProperty("title").GetString());
        var startedAt = Array.FindIndex(lines, static line => line.GetProperty("method").GetString() == CodeAltaAcpExtensions.SessionStartedMethod);
        var childUpdates = lines.Select((line, index) => (line, index)).Where(item => item.line.GetProperty("method").GetString() == "session/update"
            && item.line.GetProperty("params").GetProperty("sessionId").GetString() == childId).ToArray();
        Assert.IsTrue(childUpdates.Length > 0, "The child's updates carry its own session id.");
        Assert.IsTrue(childUpdates.All(item => item.index > startedAt), "The child is announced before its updates.");
        Assert.AreEqual("The child said it.", lines[^1].GetProperty("params").GetProperty("finalMessage").GetString());
    }

    [TestMethod]
    public async Task Failed_ExitsWithOne_AndReportsTheFailure()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.Fail(AgentFailureKind.Other, "The tool broke."));

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "go"]);

        Assert.AreEqual(CliExitCodes.Failed, run.ExitCode, run.Stderr);
        var result = Result(run);
        Assert.AreEqual("failed", result.GetProperty("status").GetString());
        Assert.AreEqual(1, result.GetProperty("exitCode").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, result.GetProperty("stopReason").ValueKind);
        Assert.AreEqual(JsonValueKind.Object, result.GetProperty("failure").ValueKind);
        Assert.IsTrue(Result(run).GetRawText().Length > 0);
        StringAssert.Contains(run.Stderr, "failed");
    }

    [TestMethod]
    public async Task CodeMode_ARunWhoseScriptCallsTools_CompletesWithItsAnswerAndTheUsageOfEveryRequest()
    {
        using var root = new CliTestRoot();
        File.WriteAllText(Path.Combine(root.ProjectRoot, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(root.ProjectRoot, "b.txt"), "beta");
        var provider = new FakeProvider();
        // The records of the calls of a script are written after the script, as the answer that follows is.
        const string script = """
            const [a, b] = await Promise.all([tools.read_file({ path: "a.txt" }), tools.read_file({ path: "b.txt" })]);
            await tools.write_file({ path: "b.txt", content: "beta and " + a.content.length });
            return "read both";
            """;
        provider.Turns.Enqueue((_, _, _) => Task.FromResult(new AgentTurnResponse
        {
            AssistantMessage = new AgentConversationMessage(AgentConversationRole.Assistant,
                [new AgentMessagePart.ToolCall("call-1", "exec", JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["code"] = script }))]),
            Usage = new AgentSessionUsage(LastOperation: new AgentOperationUsageSnapshot(Model: "fake-model", InputTokens: 70, OutputTokens: 10)),
        }));
        provider.Turns.Enqueue(FakeProvider.Reply("Edited b.txt."));

        var run = await root.RunAsync(provider, ["run", Plain, "--no-mcp", "--ephemeral", "--code-mode=on", "--format", "jsonl", "read and edit"]);

        var result = Result(run);
        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, $"{result.GetRawText()}\n{run.Stderr}");
        Assert.AreEqual("Edited b.txt.", result.GetProperty("finalMessage").GetString());
        Assert.AreEqual(170, result.GetProperty("usage").GetProperty("inputTokens").GetInt32(), result.GetRawText());
        Assert.AreEqual("beta and 5", File.ReadAllText(Path.Combine(root.ProjectRoot, "b.txt")));
    }

    [TestMethod]
    public async Task Timeout_AbortsTheRun_AndExitsWithFour()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var never = new TaskCompletionSource();
        provider.Turns.Enqueue(FakeProvider.WaitThenReply(never.Task, "Too late."));

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--timeout", "1", "wait"]);

        Assert.AreEqual(CliExitCodes.Timeout, run.ExitCode, run.Stderr);
        var result = Result(run);
        Assert.AreEqual("timeout", result.GetProperty("status").GetString());
        Assert.AreEqual("cancelled", result.GetProperty("stopReason").GetString());
        Assert.AreEqual("timeout", result.GetProperty("abortReason").GetString());
    }

    [TestMethod]
    public async Task Cancel_AbortsTheRunWithTheReasonSignal_AndExitsWithThree()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var never = new TaskCompletionSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Turns.Enqueue(FakeProvider.WaitThenReply(never.Task, "Too late.", started));
        using var signal = new CancellationTokenSource();

        var running = root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "wait"], cancellationToken: signal.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        signal.Cancel();
        var run = await running;

        Assert.AreEqual(CliExitCodes.Aborted, run.ExitCode, run.Stderr);
        var result = Result(run);
        Assert.AreEqual("aborted", result.GetProperty("status").GetString());
        Assert.AreEqual("cancelled", result.GetProperty("stopReason").GetString());
        Assert.AreEqual("signal", result.GetProperty("abortReason").GetString());
    }

    [TestMethod]
    public async Task Cancel_OfAProviderThatIgnoresCancellation_EndsWithinTheGrace_AndExitsWithThree()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var never = new TaskCompletionSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Turns.Enqueue(FakeProvider.IgnoreCancellation(never.Task, "Too late.", started));
        using var signal = new CancellationTokenSource();
        try
        {
            var running = root.RunWithEnvironmentAsync(Bounded(root, provider), ["run", Plain, "--format", "jsonl", "wait"], cancellationToken: signal.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            signal.Cancel();
            var run = await running.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), $"Ended after {watch.Elapsed}: the abort and the shutdown are bounded.");
            Assert.AreEqual(CliExitCodes.Aborted, run.ExitCode, run.Stderr);
            var result = Result(run);
            Assert.AreEqual("aborted", result.GetProperty("status").GetString());
            Assert.AreEqual("signal", result.GetProperty("abortReason").GetString());
        }
        finally
        {
            never.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Timeout_OfAProviderThatIgnoresCancellation_EndsWithinTheGrace_AndExitsWithFour()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var never = new TaskCompletionSource();
        provider.Turns.Enqueue(FakeProvider.IgnoreCancellation(never.Task, "Too late."));
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var run = await root.RunWithEnvironmentAsync(Bounded(root, provider), ["run", Plain, "--format", "jsonl", "--timeout", "1", "wait"]).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(16), $"Ended after {watch.Elapsed}: the abort and the shutdown are bounded.");
            Assert.AreEqual(CliExitCodes.Timeout, run.ExitCode, run.Stderr);
            Assert.AreEqual("timeout", Result(run).GetProperty("abortReason").GetString());
        }
        finally
        {
            never.TrySetResult();
        }
    }

    // A short abort grace and shutdown limit, for the runs a provider does not let stop.
    private static CliEnvironment Bounded(CliTestRoot root, FakeProvider provider)
        => root.Environment(provider) with { AbortGrace = TimeSpan.FromSeconds(1), ShutdownLimit = TimeSpan.FromSeconds(2) };

    [TestMethod]
    public async Task InvalidArguments_ExitWithTwo_WithAResultInJsonl()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var unknown = await root.RunAsync(provider, ["run", "--no-such-option", "hi"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, unknown.ExitCode);
        Assert.AreEqual(string.Empty, unknown.Stdout);

        var badTimeout = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--timeout", "soon", "hi"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, badTimeout.ExitCode);
        Assert.AreEqual("invalid_usage", Result(badTimeout).GetProperty("status").GetString());
        Assert.AreEqual(1, badTimeout.Lines.Length, "Only the result: there is no session.");

        var badFormat = await root.RunAsync(provider, ["run", Plain, "--format", "xml", "hi"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, badFormat.ExitCode);

        var noPrompt = await root.RunAsync(provider, ["run", Plain]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, noPrompt.ExitCode);
        StringAssert.Contains(noPrompt.Stderr, "No prompt");

        var missing = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--resume", Guid.CreateVersion7().ToString(), "hi"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, missing.ExitCode, missing.Stderr);
        Assert.AreEqual("invalid_usage", Result(missing).GetProperty("status").GetString());

        Assert.AreEqual(CliExitCodes.InvalidUsage, (await root.RunAsync(provider, [])).ExitCode);
        Assert.AreEqual(CliExitCodes.InvalidUsage, (await root.RunAsync(provider, ["run", Plain, "--disable-tools", "[", "hi"])).ExitCode);
        var noProfile = await root.RunAsync(provider, ["run", Plain, "--sandbox", "nowhere", "hi"]);
        Assert.AreEqual(CliExitCodes.InvalidUsage, noProfile.ExitCode, noProfile.Stderr);
        StringAssert.Contains(noProfile.Stderr, "not a sandbox profile");
    }

    [TestMethod]
    public async Task SessionInUse_ExitsWithFive_WhenAnotherHolderHasItsLease()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        var sessionId = Guid.CreateVersion7().ToString();
        using var held = new SessionLeaseStore(new CatalogOptions { GlobalRoot = root.Profile })
            .TryAcquire(sessionId, new SessionLeaseOwner(SessionLeaseOwner.DesktopKind, "test"));
        Assert.IsNotNull(held);

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--session-id", sessionId, "hi"]);

        Assert.AreEqual(CliExitCodes.SessionInUse, run.ExitCode, run.Stderr);
        Assert.AreEqual("session_in_use", Result(run).GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task ProviderNotReady_ExitsWithSix()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider(availability: ModelProviderAvailability.Failed);

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "hi"]);

        Assert.AreEqual(CliExitCodes.ProviderNotReady, run.ExitCode, run.Stderr);
        Assert.AreEqual("provider_not_ready", Result(run).GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task OutputLastMessage_WritesTheFinalAnswer()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.Reply("Écrit."));
        var path = Path.Combine(root.Root, "out", "last.md");

        var run = await root.RunAsync(provider, ["run", Plain, "--output-last-message", path, "write"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("Écrit."), File.ReadAllBytes(path), "UTF-8 without a byte order mark.");
    }

    [TestMethod]
    public async Task OutputLastMessage_ThatCannotBeWritten_ExitsWithOne_AndReportsIt()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.Reply("Lost."));
        // A file stands where the folder of the destination should be.
        var blocker = Path.Combine(root.Root, "blocker");
        File.WriteAllText(blocker, "a file");

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--output-last-message", Path.Combine(blocker, "last.md"), "write"]);

        Assert.AreEqual(CliExitCodes.Failed, run.ExitCode, run.Stderr);
        var result = Result(run);
        Assert.AreEqual("failed", result.GetProperty("status").GetString());
        Assert.AreEqual(1, result.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("output_write", result.GetProperty("failure").GetProperty("kind").GetString());
        StringAssert.Contains(result.GetProperty("failure").GetProperty("message").GetString(), "last.md");
        Assert.AreEqual("Lost.", result.GetProperty("finalMessage").GetString(), "The answer is still in the result.");
    }

    [TestMethod]
    public async Task OutputSchema_GivesTheJsonAlone_WhenTheAnswerMatches()
    {
        using var root = new CliTestRoot();
        var schema = Path.Combine(root.Root, "schema.json");
        File.WriteAllText(schema, """{"type":"object","properties":{"ok":{"type":"boolean"},"count":{"type":"integer"}},"required":["ok"],"additionalProperties":false}""");
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.Reply("Here it is:\n```json\n{\"ok\": true, \"count\": 2}\n```"));

        var run = await root.RunAsync(provider, ["run", Plain, "--output-schema", schema, "report"]);

        Assert.AreEqual(CliExitCodes.Completed, run.ExitCode, run.Stderr);
        Assert.AreEqual("{\"ok\": true, \"count\": 2}\n", run.Stdout);
        StringAssert.Contains(LastUserText(provider), "JSON Schema");
        StringAssert.Contains(LastUserText(provider), "\"additionalProperties\":false");
    }

    [TestMethod]
    public async Task OutputSchema_ExitsWithSeven_WhenTheAnswerDoesNotMatch()
    {
        using var root = new CliTestRoot();
        var schema = Path.Combine(root.Root, "schema.json");
        File.WriteAllText(schema, """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""");
        var provider = new FakeProvider();
        provider.Turns.Enqueue(FakeProvider.Reply("""{"ok": "yes", "extra": 1}"""));

        var run = await root.RunAsync(provider, ["run", Plain, "--format", "jsonl", "--output-schema", schema, "report"]);

        Assert.AreEqual(CliExitCodes.InvalidOutput, run.ExitCode, run.Stderr);
        var result = Result(run);
        Assert.AreEqual("invalid_output", result.GetProperty("status").GetString());
        var message = result.GetProperty("failure").GetProperty("message").GetString()!;
        StringAssert.Contains(message, "$.ok");
        StringAssert.Contains(message, "'extra' is not allowed");
    }

    [TestMethod]
    public async Task Stdin_IsThePromptWithADash_AndIsAppendedToTheWordsInABlock()
    {
        using var root = new CliTestRoot();
        var provider = new FakeProvider();

        var dash = await root.RunAsync(provider, ["run", Plain, "-"], stdin: "﻿From stdin.\n");
        Assert.AreEqual(CliExitCodes.Completed, dash.ExitCode, dash.Stderr);
        Assert.AreEqual("From stdin.\n", LastUserText(provider), "The byte order mark is dropped.");

        var piped = await root.RunAsync(provider, ["run", Plain, "Review", "this"], stdin: "diff --git a b\n");
        Assert.AreEqual(CliExitCodes.Completed, piped.ExitCode, piped.Stderr);
        Assert.AreEqual("Review this\n\n<stdin>\ndiff --git a b\n</stdin>", LastUserText(provider));

        var alone = await root.RunAsync(provider, ["run", Plain], stdin: "Only stdin");
        Assert.AreEqual(CliExitCodes.Completed, alone.ExitCode, alone.Stderr);
        Assert.AreEqual("Only stdin", LastUserText(provider));

        var empty = await root.RunAsync(provider, ["run", Plain, "Just words"], stdin: string.Empty);
        Assert.AreEqual(CliExitCodes.Completed, empty.ExitCode, empty.Stderr);
        Assert.AreEqual("Just words", LastUserText(provider), "An empty input adds no block.");
    }

    internal static JsonElement Result(CliRun run)
    {
        var last = JsonDocument.Parse(run.Lines[^1]).RootElement;
        Assert.AreEqual(CodeAltaAcpExtensions.ResultMethod, last.GetProperty("method").GetString(), run.Stdout);
        return last.GetProperty("params").Clone();
    }

    // The text of the last prompt of the user the provider was sent.
    private static string LastUserText(FakeProvider provider)
    {
        var request = provider.Requests.Last();
        var message = request.Conversation.Last(static message => message.Role == AgentConversationRole.User);
        return message.Parts.OfType<AgentMessagePart.Text>().Select(static part => part.Value).Last();
    }
}

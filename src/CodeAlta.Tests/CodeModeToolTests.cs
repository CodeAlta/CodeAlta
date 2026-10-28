using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeModeToolTests
{
    [TestMethod]
    public async Task Exec_RunsAScript_OverTheToolsTheMcpServersAndAlta()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var invoker = new FakeTools();
        var (exec, _) = Create(root.Path, runs, sourceFolder: Path.Combine(root.Path, "sources"));

        var result = await InvokeAsync(exec, """
            const file = await tools.read_file({ path: "a.txt" });
            const issues = await mcp.github.list_issues({ state: "open" });
            await alta.prompt.create({ promptId: "reviewer", scope: "global", stdin: "Review." });
            const shown = await alta.prompt.show({ promptId: "reviewer", scope: "global" });
            return { content: file.content, issues, prompt: shown.type };
            """, invoker);

        Assert.IsTrue(result.Success, Text(result));
        using (var value = ValueOf(result))
        {
            Assert.AreEqual("""{"content":"hello","issues":"2 open issues","prompt":"alta.prompt"}""", value.RootElement.GetRawText());
        }

        CollectionAssert.AreEqual(new[] { "read_file", "github_list_issues" }, invoker.Calls.ToArray());
        // The source of the run is kept, for the model to fix it with its file tools.
        var source = Text(result).Split('\n').Single(static line => line.StartsWith("Source: ", StringComparison.Ordinal))["Source: ".Length..];
        StringAssert.Contains(File.ReadAllText(source), "tools.read_file");
    }

    [TestMethod]
    public async Task Exec_KeepsALongResultWhole_InTheFolderOfItsSession()
    {
        using var root = TestTempDirectory.Create();
        var folder = Path.Combine(root.Path, "tool-output");
        await using var runs = new CodeModeRunRegistry(null) { ResultFolder = (_, _) => ValueTask.FromResult<string?>(folder) };
        var (exec, _) = Create(root.Path, runs, sourceFolder: Path.Combine(root.Path, "sources"));

        var result = await InvokeAsync(exec, """
            // @exec: {"max_output_tokens": 300}
            return Array.from({ length: 400 }, (_, i) => `- line ${i} with \`code\``).join("\n");
            """, new FakeTools());

        var text = Text(result);
        Assert.IsTrue(result.Success, text);
        var line = text.Split('\n').Single(static line => line.StartsWith(CodeModeResultFormatter.FullResultPrefix, StringComparison.Ordinal));
        var path = line[CodeModeResultFormatter.FullResultPrefix.Length..line.LastIndexOf(" (", StringComparison.Ordinal)];
        Assert.AreEqual(folder, Path.GetDirectoryName(path));
        var whole = File.ReadAllText(path);
        StringAssert.StartsWith(whole, "- line 0 with `code`\n");
        StringAssert.EndsWith(whole, "- line 399 with `code`");
        StringAssert.Contains(text, "characters left out: they are in the full result …");
        StringAssert.EndsWith(text, "- line 399 with `code`\n");
        Assert.IsFalse(text.Contains("\\u0060", StringComparison.Ordinal), text);
        // The source is named under the header: the result is the end of the text.
        Assert.IsLessThan(text.IndexOf("\nResult:\n", StringComparison.Ordinal), text.IndexOf("\nSource: ", StringComparison.Ordinal));
        // Formatted again (the prompt, the store), the result names the same file, written once.
        var run = runs.FindAny(runs.List("session-1").Single().Id)!;
        StringAssert.Contains(await runs.FormatResultAsync(run, await run.Completion), line + "\n");
        Assert.HasCount(1, Directory.GetFiles(folder));
    }

    [TestMethod]
    public async Task Exec_LeavesOutTheToolsOfTheModelAndItself()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            const found = await codemode.help({ path: "tools" });
            return [found.includes("tools.read_file"), found.includes("tools.exec"), found.includes("tools.alta"), await tools.exec({ code: "1" }).then(() => "called", e => e.name)];
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        CollectionAssert.AreEqual(new object[] { true, false, false, "TypeError" },
            value.RootElement.EnumerateArray().Select(static item => item.ValueKind == JsonValueKind.String ? (object)item.GetString()! : item.GetBoolean()).ToArray());
    }

    [TestMethod]
    public async Task Discovery_FindsAToolByWhatItDoes_AndDescribesIt()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            const hits = await codemode.search({ query: "issues" });
            const declaration = await codemode.describe({ path: hits[0].path });
            return { path: hits[0].path, declaration };
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        Assert.AreEqual("mcp.github.list_issues", value.RootElement.GetProperty("path").GetString());
        StringAssert.Contains(value.RootElement.GetProperty("declaration").GetString(), "list_issues(");
    }

    [TestMethod]
    public async Task Discovery_SaysWhyNothingMatched_AndDescribesACommandWithTheTypesItUses()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            return { none: await codemode.search({ query: "zzqx" }), declaration: await codemode.describe({ path: "alta.prompt.show" }) };
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        StringAssert.Contains(value.RootElement.GetProperty("none").GetString(), "alta.mcp.activate");
        var declaration = value.RootElement.GetProperty("declaration").GetString()!;
        StringAssert.Contains(declaration, "show(");
        Assert.IsFalse(declaration.Contains("interface UsageTotals", StringComparison.Ordinal), "a type the command does not use is left out");
    }

    [TestMethod]
    public void ReferencedTypes_FollowWhatTheTypesUse()
    {
        const string types = "/** A. */\ninterface A { b: B; }\n\ninterface B { n: number; }\n\ntype C = \"x\";";

        var text = CodeModeDiscovery.ReferencedTypes("declare function f(): Promise<A>;", types);

        StringAssert.Contains(text, "interface A");
        StringAssert.Contains(text, "interface B");
        Assert.IsFalse(text.Contains("type C", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Agent_StartsASessionPerCall_AndResolvesWithItsAnswer()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var agents = new FakeAgents();
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = agents }).Single(static tool => tool.Spec.Name == "exec");

        var result = await InvokeAsync(exec, """
            const answers = await step("review", () => Promise.all([agent("Review A.", { tier: "quick" }), agent("Review B.", { title: "B" })]));
            try { await agent("fail"); } catch (e) { answers.push(`${e.name}:${e.code}`); }
            return answers;
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        CollectionAssert.AreEqual(new[] { "answer to Review A.", "answer to Review B.", "AgentError:failed" },
            value.RootElement.EnumerateArray().Select(static item => item.GetString()).ToArray());
        var first = agents.Requests.Single(static request => request.Prompt == "Review A.");
        Assert.AreEqual("quick", first.Tier);
        Assert.AreEqual("review", first.StepId, "a session belongs to the step that started it");
        StringAssert.StartsWith(first.RunId, "run-");
        Assert.IsTrue(agents.Requests.All(static request => request.Parent == "session-1"));
    }

    [TestMethod]
    public async Task AWarningOfTheSessionsOfAScript_GoesToItsOutput_WhetherTheSessionAnsweredOrFailed()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = new WarningAgents() }).Single(static tool => tool.Spec.Name == "exec");

        var result = await InvokeAsync(exec, """
            const answer = await agent("Look.", { tier: "quick" });
            const failed = await agent("fail", { tier: "complex" }).catch(e => e.code);
            const kept = await session({ tier: "standard" });
            return [answer, failed, kept.id];
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using (var value = ValueOf(result))
        {
            Assert.AreEqual("""["answer","failed","kept-1"]""", value.RootElement.GetRawText());
        }

        foreach (var tier in new[] { "quick", "complex", "standard" })
        {
            StringAssert.Contains(Text(result), $"tier `{tier}` is not set up: its sessions use this session's model (provider:model).");
        }
    }

    // Sessions whose start says that their tier is not set up.
    private sealed class WarningAgents : ICodeModeAgents
    {
        private static string Warning(CodeModeAgentRequest request) => $"tier `{request.Tier}` is not set up: its sessions use this session's model (provider:model).";

        public Task<CodeModeAgentResult> RunAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
            => Task.FromResult(request.Prompt == "fail"
                ? new CodeModeAgentResult("child-x", false, "failed", null, "it broke", Orchestration.Runtime.SessionUsageTotals.None, TimeSpan.Zero) { Warning = Warning(request) }
                : new CodeModeAgentResult("child-1", true, "completed", "answer", null, Orchestration.Runtime.SessionUsageTotals.None, TimeSpan.Zero) { Warning = Warning(request) });

        public Task<CodeModeAgentResult> OpenAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new CodeModeAgentResult("kept-1", true, "opened", null, null, Orchestration.Runtime.SessionUsageTotals.None, TimeSpan.Zero) { Warning = Warning(request) });

        public Task<CodeModeAgentResult> AskAsync(string runId, string sessionId, string prompt, JsonElement? schema, TimeSpan? timeout, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CloseAsync(string runId, string sessionId) => Task.CompletedTask;
    }

    private sealed class FakeAgents : ICodeModeAgents
    {
        public System.Collections.Concurrent.ConcurrentBag<(string Parent, string Prompt, string? Tier, string? StepId, string RunId)> Requests { get; } = [];

        public System.Collections.Concurrent.ConcurrentQueue<CodeModeAgentRequest> All { get; } = new();

        public Task<CodeModeAgentResult> RunAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
        {
            All.Enqueue(request);
            Requests.Add((parentSessionId, request.Prompt, request.Tier, request.StepId, request.RunId));
            return Task.FromResult(request.Prompt == "fail"
                ? new CodeModeAgentResult("child-x", false, "failed", null, "it broke", Orchestration.Runtime.SessionUsageTotals.None, TimeSpan.Zero)
                : new CodeModeAgentResult("child-" + request.Prompt, true, "completed", "answer to " + request.Prompt, null, Orchestration.Runtime.SessionUsageTotals.None, TimeSpan.Zero));
        }

        public System.Collections.Concurrent.ConcurrentQueue<string> Kept { get; } = new();

        public Task<CodeModeAgentResult> OpenAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
        {
            All.Enqueue(request);
            Kept.Enqueue("open " + request.Title);
            return Task.FromResult(new CodeModeAgentResult("kept-1", true, "opened", null, null, Orchestration.Runtime.SessionUsageTotals.None, TimeSpan.Zero));
        }

        public Task<CodeModeAgentResult> AskAsync(string runId, string sessionId, string prompt, JsonElement? schema, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            Kept.Enqueue($"ask {sessionId} {prompt}");
            return Task.FromResult(new CodeModeAgentResult(sessionId, true, "completed", "reply to " + prompt, null, Orchestration.Runtime.SessionUsageTotals.None, TimeSpan.Zero));
        }

        public Task CloseAsync(string runId, string sessionId)
        {
            Kept.Enqueue("close " + sessionId);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task Agent_AndSession_TakeSubSessionsOff_ForTheirChild()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var agents = new FakeAgents();
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = agents }).Single(static tool => tool.Spec.Name == "exec");

        var result = await InvokeAsync(exec, """
            await agent("leaf", { subSessions: false });
            await agent("branch");
            await session({ title: "kept", subSessions: false });
            return "ok";
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        var requests = agents.All.ToArray();
        Assert.IsFalse(requests.Single(static request => request.Prompt == "leaf").SubSessions);
        Assert.IsTrue(requests.Single(static request => request.Prompt == "branch").SubSessions);
        Assert.IsFalse(requests.Single(static request => request.Title == "kept").SubSessions);
        StringAssert.Contains(exec.Spec.Description, "subSessions: false");
    }

    [TestMethod]
    public async Task ASessionWithoutSubSessions_HasNoAgentNorSession_InItsScriptsNorInTheDescriptionOfExec()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var agents = new FakeAgents();
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher,
            new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path, SessionOptions = new AgentSessionOptions { SubSessions = false } },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = agents }).Single(static tool => tool.Spec.Name == "exec");

        Assert.DoesNotContain("agent(", exec.Spec.Description);
        Assert.DoesNotContain("session(", exec.Spec.Description);
        Assert.DoesNotContain("sub-agent", exec.Spec.Description);
        StringAssert.Contains(exec.Spec.Description, "`parallel(thunks, { limit })`");

        var result = await InvokeAsync(exec, """
            const found = await codemode.search({ query: "agent session sub-agent create child" });
            const paths = typeof found === "string" ? [] : found.map((hit) => hit.path);
            const refused = await agent("Review.").then(() => "called", (e) => e.message);
            const kept = await session({ title: "x" }).then(() => "called", (e) => e.message);
            return { paths, refused, kept };
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        var paths = value.RootElement.GetProperty("paths").EnumerateArray().Select(static item => item.GetString()!).ToArray();
        Assert.IsFalse(paths.Any(static path => path is "agent" or "session" || path.StartsWith("alta.session.create", StringComparison.Ordinal)
            || path.StartsWith("alta.session.fork", StringComparison.Ordinal)), string.Join(", ", paths));
        StringAssert.Contains(value.RootElement.GetProperty("refused").GetString(), "`codemode.agent` is not a function of this session");
        StringAssert.Contains(value.RootElement.GetProperty("kept").GetString(), "`codemode.session.open` is not a function of this session");
        Assert.IsTrue(agents.All.IsEmpty);
    }

    [TestMethod]
    public async Task Session_KeepsOneSessionForSeveralPrompts_AndClosesIt()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var agents = new FakeAgents();
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = agents }).Single(static tool => tool.Spec.Name == "exec");

        var result = await InvokeAsync(exec, """
            const reviewer = await session({ title: "Reviewer" });
            const first = await reviewer.run("one");
            const second = await reviewer.run("two");
            await reviewer.close();
            return [reviewer.id, first, second];
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        Assert.AreEqual("""["kept-1","reply to one","reply to two"]""", value.RootElement.GetRawText());
        CollectionAssert.AreEqual(new[] { "open Reviewer", "ask kept-1 one", "ask kept-1 two", "close kept-1" }, agents.Kept.ToArray());
    }

    [TestMethod]
    public async Task AFailedTool_IsAnErrorTheScriptCatches()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            try { await tools.read_file({ path: "missing.txt" }); return "no error"; }
            catch (e) { return [e.name, e.code, e.message]; }
            """, new FakeTools());

        using var value = ValueOf(result);
        Assert.AreEqual("""["ToolError","read_file","missing.txt does not exist."]""", value.RootElement.GetRawText());
    }

    [TestMethod]
    public async Task ARunPastItsYield_GoesOnInTheBackground_AndWaitCollectsItOnce()
    {
        using var root = TestTempDirectory.Create();
        var delivered = new List<string>();
        await using var runs = new CodeModeRunRegistry((_, prompt, _) => { lock (delivered) delivered.Add(prompt); return Task.CompletedTask; });
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var (exec, wait) = Create(root.Path, runs);

        var started = await InvokeAsync(exec, """
            // @exec: {"title": "slow", "yield_ms": 50}
            const file = await tools.read_file({ path: "a.txt" });
            return file.content;
            """, invoker);
        StringAssert.StartsWith(Text(started), "Script running as run-");
        var runId = runs.List("session-1").Single().Id;
        // A script in the background is a background task of its session, found by the call that runs it.
        var task = runs.ListTasks("session-1").Single();
        Assert.AreEqual(("workflow", "slow", "call-1", (AgentBackgroundTaskOutcome?)null), (task.Kind, task.Description, task.ToolCallId, task.Outcome));

        // The run ends while wait waits for it: the result is the wait's, not a prompt.
        var waiting = InvokeAsync(wait, $$"""{"run":"{{runId}}","yield_ms":10000}""");
        invoker.Gate.SetResult();
        var waited = await waiting;
        var again = await InvokeAsync(wait, $$"""{"run":"{{runId}}"}""");

        Assert.IsTrue(waited.Success, Text(waited));
        StringAssert.Contains(Text(waited), "hello");
        StringAssert.Contains(Text(again), "already given");
        Assert.IsEmpty(delivered, "a result that wait took is not delivered again");
    }

    [TestMethod]
    public async Task AShortWait_LastsAtLeastTheFloor_SoThatTheEndOfTheRunIsItsResult()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var (exec, wait) = Create(root.Path, runs);

        await InvokeAsync(exec, """
            // @exec: {"yield_ms": 10}
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        var runId = runs.List("session-1").Single().Id;

        // A wait of 100 ms is held to the floor (10 s): the run that ends after a second is its result, not a poll.
        var waiting = InvokeAsync(wait, $$"""{"run":"{{runId}}","yield_ms":100}""");
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.IsFalse(waiting.IsCompleted, "the wait is not over after 100 ms");
        invoker.Gate.SetResult();
        var waited = await waiting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(waited.Success, Text(waited));
        StringAssert.Contains(Text(waited), "hello");
    }

    [TestMethod]
    public async Task TheDescriptionsOfExecAndWait_AskForBatches_AndOneLongWait_TheSameForEverySession()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, wait) = Create(root.Path, runs);
        var (again, _) = Create(root.Path, runs);

        StringAssert.Contains(exec.Spec.Description, "Use it to batch independent reads and searches, or edits you have decided, into one step");
        StringAssert.Contains(exec.Spec.Description, "In a batch, use `Promise.allSettled`");
        StringAssert.Contains(exec.Spec.Description, $"`max_output_tokens` is {CodeModeResultFormatter.DefaultMaxOutputTokens} by default");
        Assert.DoesNotContain("\"max_output_tokens\": ", exec.Spec.Description);
        StringAssert.Contains(wait.Spec.Description, "30000 by default");
        StringAssert.Contains(wait.Spec.Description, "Prefer one long wait, or other work, to repeated short ones");
        Assert.AreEqual(exec.Spec.Description, again.Spec.Description, "the prompt cache holds");
    }

    [TestMethod]
    public async Task AltaWorkflow_ListsReadsAndCancelsRuns_AndASessionCancelsOnlyRunsOfSessionsItReaches()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var gate = new TaskCompletionSource();
        var run = runs.TryStart("session-1", "Slow", 1000, "call-7", async (_, token) =>
        {
            await gate.Task.WaitAsync(token);
            return new CodeModeRunResult(CodeModeRunStatus.Completed, TimeSpan.Zero) { ValueJson = "42" };
        }, out _)!;
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root.Path }).Add(registry).Add(runs));

        var listed = await dispatcher.InvokeAsync(["workflow", "list", "--all"]);
        var other = await dispatcher.InvokeAsync(["workflow", "cancel", run.Id], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-2" });
        var early = await dispatcher.InvokeAsync(["workflow", "result", run.Id]);
        var cancelled = await dispatcher.InvokeAsync(["workflow", "cancel", run.Id], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1" });
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var result = await dispatcher.InvokeAsync(["workflow", "result", run.Id]);
        var shown = await dispatcher.InvokeAsync(["workflow", "show", run.Id, "--profile"]);
        var missing = await dispatcher.InvokeAsync(["workflow", "show", "run-nope"]);

        Assert.AreEqual(AltaExitCodes.Success, listed.ExitCode, listed.Transcript);
        StringAssert.Contains(listed.Stdout, $"\"id\":\"{run.Id}\"");
        StringAssert.Contains(listed.Stdout, "\"state\":\"running\"");
        StringAssert.Contains(listed.Stdout, "\"callId\":\"call-7\"");
        Assert.AreNotEqual(AltaExitCodes.Success, other.ExitCode, "a session does not cancel the run of a session it cannot reach");
        StringAssert.Contains(early.Transcript, "workflow.running");
        Assert.AreEqual(AltaExitCodes.Success, cancelled.ExitCode, cancelled.Transcript);
        StringAssert.Contains(cancelled.Stdout, "\"wasRunning\":true");
        StringAssert.Contains(result.Stdout, "\"state\":\"cancelled\"");
        StringAssert.Contains(result.Stdout, "Script stopped");
        StringAssert.Contains(result.Stdout, "stopped by this session");
        StringAssert.Contains(missing.Transcript, "workflow.notFound");
        Assert.AreEqual(AltaExitCodes.Success, shown.ExitCode, shown.Transcript);
        StringAssert.Contains(shown.Stdout, "\"profile\":{\"durationMs\"");
    }

    [TestMethod]
    public async Task ARunThatEndsInTheBackground_IsDeliveredToItsSession()
    {
        using var root = TestTempDirectory.Create();
        var delivered = new TaskCompletionSource<(string Session, string Prompt)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runs = new CodeModeRunRegistry((session, prompt, _) => { delivered.TrySetResult((session, prompt)); return Task.CompletedTask; });
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var (exec, _) = Create(root.Path, runs);

        await InvokeAsync(exec, """
            // @exec: {"title": "slow", "yield_ms": 50}
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        invoker.Gate.SetResult();
        var (session, prompt) = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("session-1", session);
        StringAssert.StartsWith(prompt, "Script run-");
        StringAssert.Contains(prompt, "(slow) ended:");
        StringAssert.Contains(prompt, "hello");
    }

    [TestMethod]
    public async Task ARunThatEndsInTheBackground_DeliversItsPicturesWithItsPrompt()
    {
        using var root = TestTempDirectory.Create();
        File.WriteAllBytes(Path.Combine(root.Path, "chart.png"), Png);
        var delivered = new TaskCompletionSource<(string Prompt, IReadOnlyList<PromptImageAttachment> Images)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runs = new CodeModeRunRegistry((_, prompt, images, _) => { delivered.TrySetResult((prompt, images)); return Task.CompletedTask; }, hold: null);
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var (exec, _) = Create(root.Path, runs);

        await InvokeAsync(exec, """
            // @exec: {"title": "slow", "yield_ms": 50}
            image("chart.png", { title: "Chart" });
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        invoker.Gate.SetResult();
        var (prompt, images) = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        StringAssert.Contains(prompt, "Images: 1 attached (Chart)\n");
        var image = images.Single();
        Assert.AreEqual("Chart", image.Title);
        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual(".png", image.FileExtension);
        CollectionAssert.AreEqual(Png, image.Bytes);
    }

    [TestMethod]
    public async Task Exec_GivesThePicturesOfTheScript_AfterItsText_AndSaysWhichWereSkipped()
    {
        using var root = TestTempDirectory.Create();
        Directory.CreateDirectory(Path.Combine(root.Path, "out"));
        File.WriteAllBytes(Path.Combine(root.Path, "out", "chart.png"), Png);
        File.WriteAllText(Path.Combine(root.Path, "notes.txt"), "not a picture");
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            image("out/chart.png", { title: "Chart" });
            image("missing.png");
            image("notes.txt");
            return "done";
            """, new FakeTools());

        var text = Text(result);
        Assert.IsTrue(result.Success, text);
        var image = (AgentToolResultItem.Image)result.Items[1];
        Assert.AreEqual("image/png", image.MediaType);
        Assert.AreEqual("Chart", image.DisplayName);
        CollectionAssert.AreEqual(Png, Convert.FromBase64String(image.Base64Data));
        Assert.HasCount(2, result.Items);
        StringAssert.Contains(text, "Images: 1 attached (Chart)\n");
        StringAssert.Contains(text, $"image skipped: {Path.Combine(root.Path, "missing.png")}: no such file.\n");
        StringAssert.Contains(text, $"image skipped: {Path.Combine(root.Path, "notes.txt")}: not a PNG, JPEG, GIF or WebP picture.\n");
        // The result stays the end of the text.
        StringAssert.EndsWith(text, "Result:\ndone\n");
    }

    // A picture of one pixel.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    [TestMethod]
    public async Task ARunOverTheLimitOfItsSession_IsRefused()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null, maxPerSession: 1);
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var (exec, wait) = Create(root.Path, runs);

        await InvokeAsync(exec, """
            // @exec: {"yield_ms": 10}
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        var refused = await InvokeAsync(exec, "return 1;", invoker);
        var stopped = await InvokeAsync(wait, $$"""{"run":"{{runs.List("session-1").Single().Id}}","terminate":true,"yield_ms":10000}""");

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(Text(refused), "already runs 1 scripts");
        StringAssert.StartsWith(Text(stopped), "Script stopped");
    }

    [TestMethod]
    public async Task Workflow_RunsAScriptFile_ReadThroughTheSessionsReadFile()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var invoker = new FakeTools();
        invoker.Files["review.js"] = "return `reviewed ${args.pr}`;";
        var (exec, _) = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """return await workflow({ scriptPath: "review.js" }, { pr: 231 });""", invoker);

        Assert.IsTrue(result.Success, Text(result));
        StringAssert.Contains(Text(result), "reviewed 231");
    }

    [TestMethod]
    public async Task Workflow_RunsAScriptOfTheLibrary_TheProjectsFirst_AndChecksItsArguments()
    {
        using var root = TestTempDirectory.Create();
        var project = Path.Combine(root.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var library = new CodeModeScriptLibrary(new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") });
        const string Meta = """export const meta = { name: "review", description: "Reviews.", params: { pr: { type: "integer" } } };""";
        Assert.IsNull(library.Save("review", CodeModeScriptScope.Global, Meta + "\nreturn `global ${args.pr}`;", null, null).Refusal);
        Assert.IsNull(library.Save("review", CodeModeScriptScope.Project, Meta + "\nreturn `project ${args.pr}`;", project, null).Refusal);
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(project, runs, library: library);

        var result = await InvokeAsync(exec, """
            const errors = [];
            for (const args of [{}, { pr: "7" }, { pr: 7, depth: 2 }]) {
              try { await workflow("review", args); } catch (e) { errors.push(`${e.code}: ${e.message}`); }
            }
            try { await workflow("nope"); } catch (e) { errors.push(e.code); }
            return [await workflow("review", { pr: 7 }), ...errors];
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        CollectionAssert.AreEqual(new[]
        {
            "project 7",
            "codemode.workflowArguments: workflow(\"review\", args): the argument `pr` is missing.",
            "codemode.workflowArguments: workflow(\"review\", args): the argument `pr` must be integer, not string.",
            "codemode.workflowArguments: workflow(\"review\", args): the script takes no argument `depth`: it takes `pr`.",
            "codemode.workflowNotFound",
        }, value.RootElement.EnumerateArray().Select(static item => item.GetString()).ToArray());
    }

    [TestMethod]
    public async Task Discovery_FindsTheScriptsOfTheLibrary_ByWhatTheyDo_AndDescribesHowToRunThem()
    {
        using var root = TestTempDirectory.Create();
        var project = Path.Combine(root.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var library = new CodeModeScriptLibrary(new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") });
        Assert.IsNull(library.Save("triage", CodeModeScriptScope.Project, """
            export const meta = { name: "triage", description: "Sorts the backlog.", whenToUse: "Every morning, on the open issues.", params: { limit: { type: "integer", default: 20 } } };
            return args.limit;
            """, project, null).Refusal);
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(project, runs, library: library);

        var result = await InvokeAsync(exec, """
            const hits = await codemode.search({ query: "triage backlog" });
            return { first: hits[0], declaration: await codemode.describe({ path: "workflow:triage" }), missing: await codemode.describe({ path: "workflow:nope" }).catch(e => e.message) };
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        Assert.AreEqual("workflow:triage", value.RootElement.GetProperty("first").GetProperty("path").GetString(), "found by its description and when to use it");
        StringAssert.Contains(value.RootElement.GetProperty("first").GetProperty("description").GetString(), "workflow(\"triage\", args)");
        StringAssert.Contains(value.RootElement.GetProperty("declaration").GetString(), "declare function workflow(name: \"triage\", args: { limit?: number }): Promise<unknown>;");
        StringAssert.Contains(value.RootElement.GetProperty("missing").GetString(), "No script `nope`");
    }

    [TestMethod]
    public async Task Discovery_KnowsTheFunctionsCalledByName_FirstWhenTheyMatch_AndOnlyThoseTheRunHas()
    {
        using var root = TestTempDirectory.Create();
        var project = Path.Combine(root.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var library = new CodeModeScriptLibrary(new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") });
        Assert.IsNull(library.Save("release-code", CodeModeScriptScope.Project, """
            export const meta = { name: "release-code", description: "Computes the release code of a version.", params: { channel: { type: "string" } } };
            return args.channel;
            """, project, null).Refusal);
        await using var runs = new CodeModeRunRegistry(null);
        var (dispatcher, _) = Dispatcher(project);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = project },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = new FakeAgents(), Library = library }).Single(static tool => tool.Spec.Name == "exec");
        const string Script = """
            const paths = async (query) => (await codemode.search({ query })).map(hit => hit.path);
            return {
              sessions: await paths("session create send"),
              library: await paths("script run library"),
              help: await codemode.help(),
              workflow: await codemode.help({ path: "workflow" }),
              agent: await codemode.describe({ path: "agent" }),
              load: await codemode.describe({ path: "load" }),
            };
            """;

        var result = await InvokeAsync(exec, Script, new FakeTools());
        // A run without sessions to start has no agent().
        var (bare, _) = Create(project, runs, library: library);
        var without = await InvokeAsync(bare, "return { help: await codemode.help(), agent: await codemode.describe({ path: 'agent' }).catch(e => e.message) };", new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        var root0 = value.RootElement;
        var sessions = root0.GetProperty("sessions").EnumerateArray().Select(static hit => hit.GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "session", "agent" }, sessions.Take(2).ToArray(), "the functions that wait for a session's answer come before alta.session.*");
        CollectionAssert.Contains(sessions, "alta.session.create");
        var found = root0.GetProperty("library").EnumerateArray().Select(static hit => hit.GetString()).ToList();
        Assert.AreEqual("workflow", found[0]);
        CollectionAssert.Contains(found, "workflow:release-code", "a query for library scripts lists them");
        StringAssert.Contains(root0.GetProperty("help").GetString(), "Functions called by name: agent, session, workflow");
        StringAssert.Contains(root0.GetProperty("workflow").GetString(), "declare function workflow(name: string");
        StringAssert.Contains(root0.GetProperty("agent").GetString(), "declare function agent(prompt: string");
        StringAssert.Contains(root0.GetProperty("load").GetString(), "declare function load(key: string)");
        Assert.IsTrue(without.Success, Text(without));
        using var bareValue = ValueOf(without);
        Assert.IsFalse(bareValue.RootElement.GetProperty("help").GetString()!.Contains("agent", StringComparison.Ordinal), "no agent() without sessions to start");
        StringAssert.Contains(bareValue.RootElement.GetProperty("agent").GetString(), "not a function");
    }

    [TestMethod]
    public async Task AScriptWithMetaTool_IsADeferredToolOfTheSessionsOfItsProject_ThatRunsItThroughExec()
    {
        using var root = TestTempDirectory.Create();
        var project = Path.Combine(root.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var options = new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") };
        var library = new CodeModeScriptLibrary(options);
        const string Tool = """
            export const meta = { name: "review-pr", description: "Reviews a pull request.", tool: true, params: { pr: { type: "integer" } } };
            return `reviewed ${args.pr}`;
            """;
        Assert.IsNull(library.Save("review-pr", CodeModeScriptScope.Project, Tool, project, null).Refusal);
        Assert.IsNull(library.Save("mine", CodeModeScriptScope.Global, Tool.Replace("review-pr", "mine", StringComparison.Ordinal), null, null).Refusal);
        Assert.IsNull(library.Save("plain", CodeModeScriptScope.Project, "export const meta = { name: \"plain\" };\nreturn 1;", project, null).Refusal);
        File.WriteAllText(Path.Combine(options.GlobalRoot, "config.toml"), "[scripts.spaces]\nmine = [\"work\"]\n");
        var (dispatcher, _) = Dispatcher(root.Path);
        await using var runs = new CodeModeRunRegistry(null);
        var codeMode = new CodeModeSessionTools(new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Library = library }, () => "on");
        AgentToolDefinition[] others = [new(new AgentToolSpec("alta", "alta", Schema("{}")), static (_, _) => Task.FromResult(new AgentToolResult(true, [])))];
        string[] Names(SpaceFilter? spaces) => [.. codeMode.Create(dispatcher, new AltaSessionToolOptions { WorkingDirectory = project, Spaces = spaces }, others, null, null).Select(static tool => tool.Spec.Name)];
        var store = new CodeAltaConfigStore(options);

        CollectionAssert.AreEqual(new[] { "alta", "exec", "wait", "review_pr" }, Names(null), "without the space of the session, the scripts of the project only");
        CollectionAssert.AreEqual(new[] { "alta", "exec", "wait", "review_pr" }, Names(SpaceFilter.Load(store, "home")));
        CollectionAssert.AreEqual(new[] { "alta", "exec", "wait", "mine", "review_pr" }, Names(SpaceFilter.Load(store, "work")));

        var tool = codeMode.Create(dispatcher, new AltaSessionToolOptions { WorkingDirectory = project, CallerKind = "cli" }, others, null, null).Single(static tool => tool.Spec.Name == "review_pr");
        Assert.IsTrue(tool.Spec.Deferred);
        Assert.AreEqual("integer", tool.Spec.InputSchema.GetProperty("properties").GetProperty("pr").GetProperty("type").GetString(), "its schema is its params");
        var called = await tool.Handler(new AgentToolInvocation(new ModelProviderId("provider"), "session-1", "call-9", "review_pr", Schema("""{"pr":31}""")) { Tools = new FakeTools() },
            CancellationToken.None);

        Assert.IsTrue(called.Success, Text(called));
        using var value = ValueOf(called);
        Assert.AreEqual("reviewed 31", value.RootElement.GetString());
        Assert.AreEqual("review-pr", runs.List("session-1").Single().Title, "the run is named after the script");
    }

    [TestMethod]
    public void SessionTools_AddExecAndWait_OnlyWhileCodeModeIsOn()
    {
        using var root = TestTempDirectory.Create();
        var (dispatcher, _) = Dispatcher(root.Path);
        var mode = "off";
        var runs = new CodeModeRunRegistry(null);
        // A host without the tools of Code Mode gives no session exec, and its instructions speak of none.
        Assert.AreEqual("off", runs.CodeModeOf("automation-session", null, null));
        var codeMode = new CodeModeSessionTools(new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache() }, () => mode);
        AgentToolDefinition[] others = [new(new AgentToolSpec("alta", "alta", Schema("{}")), static (_, _) => Task.FromResult(new AgentToolResult(true, [])))];

        var off = codeMode.Create(dispatcher, new AltaSessionToolOptions(), others, null, null);
        Assert.AreEqual("off", runs.CodeModeOf("automation-session", null, null));
        // The session of an automation that runs a script has exec while the setting is off, and the instructions say so.
        runs.RequireCodeMode("automation-session");
        Assert.AreEqual("on", runs.CodeModeOf("automation-session", null, null));
        Assert.AreEqual("off", runs.CodeModeOf("other", null, null));
        CollectionAssert.AreEqual(new[] { "alta", "exec", "wait" },
            codeMode.Create(dispatcher, new AltaSessionToolOptions { SourceSessionIdProvider = static () => "automation-session" }, others, null, null).Select(static tool => tool.Spec.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "alta" }, codeMode.Create(dispatcher, new AltaSessionToolOptions { SourceSessionId = "other" }, others, null, null).Select(static tool => tool.Spec.Name).ToArray());
        mode = "on";
        var with = codeMode.Create(dispatcher, new AltaSessionToolOptions(), others, null, null);
        mode = "only";
        var only = codeMode.Create(dispatcher, new AltaSessionToolOptions(), others, null, null);
        Assert.AreEqual("only", runs.CodeModeOf("other", null, null));

        CollectionAssert.AreEqual(new[] { "alta" }, off.Select(static tool => tool.Spec.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "alta", "exec", "wait" }, with.Select(static tool => tool.Spec.Name).ToArray());
        Assert.IsTrue(with.Skip(1).All(static tool => tool.Spec.Exposure == AgentToolExposure.Model), "a script does not run scripts through exec");
        Assert.IsFalse(with.Any(static tool => tool.Spec.ScriptsTakeOver));
        Assert.IsTrue(only.Single(static tool => tool.Spec.Name == "exec").Spec.ScriptsTakeOver, "in only, exec takes over the other tools");
    }

    [TestMethod]
    public void SessionTools_AndInstructions_AgreeOnTheModeOfEachProviderAndModel()
    {
        using var root = TestTempDirectory.Create();
        var (dispatcher, _) = Dispatcher(root.Path);
        var catalog = new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") };
        Directory.CreateDirectory(catalog.GlobalRoot);
        var config = Path.Combine(catalog.GlobalRoot, "config.toml");
        File.WriteAllText(config, "[providers.copilot]\ntype = \"openai-chat\"\n");
        var runs = new CodeModeRunRegistry(null);
        var codeMode = CodeModeSessionTools.FromSettings(runs, catalog, null);
        AgentToolDefinition[] others = [new(new AgentToolSpec("alta", "alta", Schema("{}")), static (_, _) => Task.FromResult(new AgentToolResult(true, [])))];
        string Tools(string session, string model) => string.Join(",", codeMode.Create(dispatcher, new AltaSessionToolOptions { SourceSessionId = session }, others, "copilot", model)
            .Select(static tool => tool.Spec.Name));
        void Agree(string session, string model, string mode, string tools)
        {
            Assert.AreEqual(tools, Tools(session, model), $"{model}: its tools");
            Assert.AreEqual(mode, runs.CodeModeOf(session, "copilot", model), $"{model}: its instructions");
        }

        // Without a setting, Code Mode is on.
        Agree("gpt", "gpt-6.1-sol", "on", "alta,exec,wait");
        Agree("haiku", "claude-haiku-4.5", "on", "alta,exec,wait");

        // A model turned off, beside one of the same provider that is not.
        File.WriteAllText(config, "[providers.copilot]\ntype = \"openai-chat\"\n\n[providers.copilot.model_overrides.\"claude-haiku-4.5\"]\ncode_mode = \"off\"\n");
        Agree("gpt", "gpt-6.1-sol", "on", "alta,exec,wait");
        Agree("haiku", "claude-haiku-4.5", "off", "alta");

        // The model of a provider wins over the provider, which wins over every session.
        File.WriteAllText(config, "[code_mode]\nmode = \"only\"\n\n[providers.copilot]\ntype = \"openai-chat\"\ncode_mode = \"off\"\n\n[providers.copilot.model_overrides.\"claude-haiku-4.5\"]\ncode_mode = \"on\"\n");
        Agree("gpt", "gpt-6.1-sol", "off", "alta");
        Agree("haiku", "claude-haiku-4.5", "on", "alta,exec,wait");
    }

    [TestMethod]
    public void AStructuredResult_IsTheValueOfTheCall_EvenWhenTheToolFailed()
    {
        var failed = new AgentToolResult(false, [new AgentToolResultItem.Text("exit 1")], "exit 1") { Structured = Schema("""{"exitCode":1}""") };
        var text = new AgentToolResult(true, [new AgentToolResultItem.Text("a"), new AgentToolResultItem.Text("b")]);

        Assert.AreEqual("""{"exitCode":1}""", ToolScriptBinding.Read(failed, "shell_command").ValueJson);
        Assert.AreEqual("\"a\\nb\"", ToolScriptBinding.Read(text, "x").ValueJson);
    }

    private const string TwoReads = """
        // @exec: {"title": "Two reads"}
        const a = await step("a", async () => (await tools.read_file({ path: "a.txt" })).content);
        const b = await step("b", async () => (await tools.read_file({ path: "b.txt" })).content);
        return a + " " + b;
        """;

    [TestMethod]
    public async Task Resume_ReplaysTheStepsThatCompleted_WithoutCallingTheirTools()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        await runs.UseStoreAsync(new CodeModeRunStore(Path.Combine(root.Path, "runs")));
        var (exec, _) = Create(root.Path, runs);
        var first = new FakeTools();

        var failed = await InvokeAsync(exec, TwoReads, first);
        var runId = runs.List("session-1").Single().Id;
        var fixedTools = new FakeTools();
        fixedTools.Files["b.txt"] = "world";
        var resumed = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}"}""", fixedTools);

        Assert.IsFalse(failed.Success, "b.txt does not exist in the first run");
        Assert.IsTrue(resumed.Success, Text(resumed));
        using (var value = ValueOf(resumed))
        {
            Assert.AreEqual("hello world", value.RootElement.GetString());
        }

        CollectionAssert.AreEqual(new[] { "read_file" }, fixedTools.Calls.ToArray(), "step a gives its recorded result: only b reads");
        StringAssert.Contains(Text(resumed), "Resume: step `b` has no recorded result");
        var second = runs.List("session-1").Single(run => run.Id != runId);
        Assert.AreEqual("Two reads", second.Title, "the kept script runs with its own options");
        // The store writes off the run's thread: what it keeps is read once written.
        await runs.WhenKeptAsync(second.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(runId, runs.Store!.TryLoad(second.Id)?.ResumesRunId);
    }

    [TestMethod]
    public async Task Resume_IsRefused_ForAnUnknownRun_AndForAChangedScript()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        await runs.UseStoreAsync(new CodeModeRunStore(Path.Combine(root.Path, "runs")));
        var (exec, _) = Create(root.Path, runs);
        await InvokeAsync(exec, TwoReads, new FakeTools());
        var runId = runs.List("session-1").Single().Id;

        var unknown = await InvokeAsync(exec, """// @exec: {"resume": "run-000000"}""", new FakeTools());
        var changed = await InvokeAsync(exec, $$"""
            // @exec: {"resume": "{{runId}}"}
            return await step("a", async () => 1);
            """, new FakeTools());
        var rerun = await InvokeAsync(exec, $$"""
            // @exec: {"resume": "{{runId}}", "replay": "all"}
            return "changed: " + await step("a", async () => 1);
            """, new FakeTools());

        Assert.IsFalse(unknown.Success);
        StringAssert.Contains(Text(unknown), "No run `run-000000` of this session");
        Assert.IsFalse(changed.Success);
        StringAssert.Contains(Text(changed), "a changed script does not resume");
        Assert.IsTrue(rerun.Success, Text(rerun));
        using var value = ValueOf(rerun);
        Assert.AreEqual("changed: hello", value.RootElement.GetString(), "a rerun runs the changed code over the recorded result of a");
    }

    [TestMethod]
    public async Task Resume_RefusesToRunAgainACallThatWasInFlight_UntilTheOptionsSaySo()
    {
        using var root = TestTempDirectory.Create();
        var store = Path.Combine(root.Path, "runs");
        await using var before = new CodeModeRunRegistry(null);
        await before.UseStoreAsync(new CodeModeRunStore(store));
        var (execBefore, _) = Create(root.Path, before);
        var hanging = new FakeTools { Gate = new TaskCompletionSource() };
        const string code = """
            // @exec: {"yield_ms": 50}
            return await step("issues", async () => await mcp.github.list_issues({ state: "open" }));
            """;
        await InvokeAsync(execBefore, code, hanging);
        var runId = before.List("session-1").Single().Id;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (before.Store!.TryLoad(runId)?.InFlightCalls.Count is not 1)
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "the call in flight was not recorded");
            await Task.Delay(20);
        }

        // CodeAlta starts again: the run is interrupted, with its call in flight.
        await using var after = new CodeModeRunRegistry(null);
        await after.UseStoreAsync(new CodeModeRunStore(store));
        var (exec, _) = Create(root.Path, after);
        var tools = new FakeTools();
        var refused = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}"}""", tools);
        var agreed = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}", "rerun_in_flight": true}""", tools);
        hanging.Gate.SetResult();

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(Text(refused), "`mcp.github.list_issues` in step `issues` with {\"state\":\"open\"}");
        Assert.IsTrue(agreed.Success, Text(agreed));
        CollectionAssert.AreEqual(new[] { "github_list_issues" }, tools.Calls.ToArray());
    }

    // A library of one project with the scripts Run… and the automations start with `return await workflow("name", args)`.
    private static (string Project, CodeModeScriptLibrary Library) LibraryOf(string root)
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        var library = new CodeModeScriptLibrary(new CatalogOptions { GlobalRoot = Path.Combine(root, "home") });
        Assert.IsNull(library.Save("count-steps", CodeModeScriptScope.Project, """
            export const meta = { name: "count-steps", description: "Reads two files, then counts." };
            const read = await step("read", async () => [(await tools.read_file({ path: "a.txt" })).content, (await tools.read_file({ path: "a.txt" })).content]);
            return await step("count", async () => (await tools.read_file({ path: "a.txt" })).content + ":" + read.length);
            """, project, null).Refusal);
        Assert.IsNull(library.Save("slow-steps", CodeModeScriptScope.Project, """
            export const meta = { name: "slow-steps", description: "Reads a file, then lists the issues." };
            const read = await step("read", async () => (await tools.read_file({ path: "a.txt" })).content);
            return read + " " + await step("issues", async () => await mcp.github.list_issues({ state: "open" }));
            """, project, null).Refusal);
        return (project, library);
    }

    [TestMethod]
    public async Task Rerun_OfALibraryScript_GivesTheRecordedResultsOfItsSteps_WithoutAnyCall()
    {
        using var root = TestTempDirectory.Create();
        var (project, library) = LibraryOf(root.Path);
        await using var runs = new CodeModeRunRegistry(null);
        await runs.UseStoreAsync(new CodeModeRunStore(Path.Combine(root.Path, "runs")));
        var (exec, _) = Create(project, runs, library: library);
        var first = new FakeTools();

        var ran = await InvokeAsync(exec, """return await workflow("count-steps", {});""", first);
        var runId = runs.List("session-1").Single().Id;
        var tools = new FakeTools();
        var rerun = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}", "replay": "all"}""", tools);
        var fromCount = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}", "from": "count-steps/count"}""", tools);

        Assert.IsTrue(ran.Success, Text(ran));
        Assert.AreEqual(3, first.Calls.Count);
        CollectionAssert.AreEqual(new[] { "count-steps#0/read#0", "count-steps#0/count#0" }, runs.Store!.TryLoad(runId)!.Steps.Select(static step => step.Key).ToArray());
        Assert.IsTrue(rerun.Success, Text(rerun));
        using (var value = ValueOf(rerun))
        {
            Assert.AreEqual("hello:2", value.RootElement.GetString());
        }

        Assert.IsTrue(fromCount.Success, Text(fromCount));
        CollectionAssert.AreEqual(new[] { "read_file" }, tools.Calls.ToArray(), "the rerun calls nothing; the rerun from count calls only count's read");
    }

    [TestMethod]
    public async Task Resume_OfARunInterruptedInsideALibraryScript_ReplaysItsStepsBefore_AndChecksTheCallsInFlight()
    {
        using var root = TestTempDirectory.Create();
        var (project, library) = LibraryOf(root.Path);
        var store = Path.Combine(root.Path, "runs");
        await using var before = new CodeModeRunRegistry(null);
        await before.UseStoreAsync(new CodeModeRunStore(store));
        var (execBefore, _) = Create(project, before, library: library);
        var hanging = new FakeTools { Gate = new TaskCompletionSource(), GatedTool = "github_list_issues" };
        await InvokeAsync(execBefore, """
            // @exec: {"yield_ms": 50}
            return await workflow("slow-steps", {});
            """, hanging);
        var runId = before.List("session-1").Single().Id;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (before.Store!.TryLoad(runId) is not { Steps.Count: 1, InFlightCalls.Count: 1 })
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "the step and the call in flight were not recorded");
            await Task.Delay(20);
        }

        // CodeAlta starts again while the run waited for the issues.
        await using var after = new CodeModeRunRegistry(null);
        await after.UseStoreAsync(new CodeModeRunStore(store));
        var (exec, _) = Create(project, after, library: library);
        var tools = new FakeTools();
        var refused = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}"}""", tools);
        var resumed = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}", "rerun_in_flight": true}""", tools);
        hanging.Gate.SetResult();

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(Text(refused), "`mcp.github.list_issues` in step `slow-steps#0/issues`");
        Assert.IsTrue(resumed.Success, Text(resumed));
        using var value = ValueOf(resumed);
        Assert.AreEqual("hello 2 open issues", value.RootElement.GetString());
        CollectionAssert.AreEqual(new[] { "github_list_issues" }, tools.Calls.ToArray(), "the step read gives its recorded result");
    }

    [TestMethod]
    public async Task Ask_ReachesTheQuestionsOfTheHost_WithItsStepAndChoices_AndIsUnavailableWithoutThem()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var questions = new FakeQuestions();
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Questions = questions }).Single(static tool => tool.Spec.Name == "exec");
        var (withoutQuestions, _) = Create(root.Path, runs);

        var answered = await InvokeAsync(exec, """
            const tag = await step("tag", () => ask("Which tag?", { choices: ["v1", "v2"], default: "v1" }));
            const go = await ask("Go?").catch((e) => `${e.name}:${e.code}`);
            return [tag, go];
            """, new FakeTools());
        var unavailable = await InvokeAsync(withoutQuestions, """return await ask("Go?").catch((e) => `${e.code}: ${e.message}`);""", new FakeTools());

        Assert.IsTrue(answered.Success, Text(answered));
        using (var value = ValueOf(answered))
        {
            Assert.AreEqual("""["v2","AskError:cancelled"]""", value.RootElement.GetRawText());
        }

        var first = questions.Requests.First();
        Assert.AreEqual(("session-1", "Which tag?", "tag", "v1"), (first.SessionId, first.Question, first.StepId, first.Default));
        CollectionAssert.AreEqual(new[] { "v1", "v2" }, first.Choices.ToArray());
        CollectionAssert.Contains(runs.List("session-1").Select(static run => run.Id).ToArray(), first.RunId);
        using (var value = ValueOf(unavailable))
        {
            StringAssert.StartsWith(value.RootElement.GetString(), "unavailable: Nobody can be asked here");
        }
    }

    // The user of the tests: the second choice of a question with choices, a dismissal otherwise.
    private sealed class FakeQuestions : ICodeModeQuestions
    {
        public System.Collections.Concurrent.ConcurrentQueue<CodeModeQuestionRequest> Requests { get; } = new();

        public Task<CodeModeQuestionAnswer> AskAsync(CodeModeQuestionRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult(request.Choices.Count > 1
                ? new CodeModeQuestionAnswer(CodeModeQuestionOutcome.Answered, request.Choices[1], null)
                : new CodeModeQuestionAnswer(CodeModeQuestionOutcome.Cancelled, null, "The user dismissed the question."));
        }
    }

    [TestMethod]
    public async Task ASleepingScript_GoesToTheBackground_AtTheYieldOfExec()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(root.Path, runs);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await InvokeAsync(exec, """
            // @exec: {"yield_ms": 1500}
            await sleep("1h");
            return 1;
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        Assert.IsLessThan(TimeSpan.FromSeconds(30), clock.Elapsed, "the turn does not wait for the sleep");
        var run = runs.FindAny(runs.List("session-1").Single().Id)!;
        Assert.IsTrue(run.Running);
        Assert.IsTrue(run.Backgrounded, "a sleeping script goes on in the background");
        run.Stop();
    }

    [TestMethod]
    public async Task AltaWorkflowResume_ChecksTheRunAndTheCaller_BeforeItAsksTheSession()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root.Path }).Add(registry).Add(runs));
        var noStore = await dispatcher.InvokeAsync(["workflow", "resume", "run-1"]);
        await runs.UseStoreAsync(new CodeModeRunStore(Path.Combine(root.Path, "runs")));
        var (exec, _) = Create(root.Path, runs);
        await InvokeAsync(exec, TwoReads, new FakeTools());
        var runId = runs.List("session-1").Single().Id;

        var missing = await dispatcher.InvokeAsync(["workflow", "resume", "run-000000"]);
        var both = await dispatcher.InvokeAsync(["workflow", "resume", runId, "--from", "a", "--all"]);
        var unknownStep = await dispatcher.InvokeAsync(["workflow", "resume", runId, "--from", "zz"]);
        var other = await dispatcher.InvokeAsync(["workflow", "resume", runId], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-2" });

        StringAssert.Contains(noStore.Transcript, "keeps no record of runs");
        StringAssert.Contains(missing.Transcript, "workflow.notFound");
        StringAssert.Contains(both.Transcript, "usage.fromAll");
        StringAssert.Contains(unknownStep.Transcript, "recorded no step `zz#0`. Its steps: a#0, b#0.");
        StringAssert.Contains(other.Transcript, "workflow.notOwnRun");
    }

    private static (AltaCommandDispatcher Dispatcher, AltaCommandRegistry Registry) Dispatcher(string root)
    {
        var registry = new AltaCommandRegistry();
        return (new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root }).Add(registry)), registry);
    }

    private static (AgentToolDefinition Exec, AgentToolDefinition Wait) Create(string root, CodeModeRunRegistry runs, string? sourceFolder = null,
        CodeModeScriptLibrary? library = null)
    {
        var (dispatcher, _) = Dispatcher(root);
        var tools = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root }, new CodeModeToolOptions
        {
            Runs = runs,
            Cache = new CodeModeScriptCache(),
            Library = library,
            SourceFolder = sourceFolder is null ? null : _ => sourceFolder,
        });
        return (tools.Single(static tool => tool.Spec.Name == "exec"), tools.Single(static tool => tool.Spec.Name == "wait"));
    }

    private static Task<AgentToolResult> InvokeAsync(AgentToolDefinition tool, string code, FakeTools invoker)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteEndObject();
        }

        return tool.Handler(new AgentToolInvocation(new ModelProviderId("provider"), "session-1", "call-1", tool.Spec.Name, JsonDocument.Parse(stream.ToArray()).RootElement.Clone()) { Tools = invoker }, CancellationToken.None);
    }

    private static Task<AgentToolResult> InvokeAsync(AgentToolDefinition tool, string arguments)
        => tool.Handler(new AgentToolInvocation(new ModelProviderId("provider"), "session-1", "call-2", tool.Spec.Name, Schema(arguments)), CancellationToken.None);

    private static string Text(AgentToolResult result) => string.Join('\n', result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    // The value of a finished run: what follows `Result:`, the end of the text. An object is in a block of JSON, a text is itself.
    private static JsonDocument ValueOf(AgentToolResult result)
    {
        var text = Text(result);
        var index = text.IndexOf("\nResult:\n", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, index, text);
        var value = text[(index + "\nResult:\n".Length)..].TrimEnd('\n');
        if (value.StartsWith("```json\n", StringComparison.Ordinal))
        {
            return JsonDocument.Parse(value["```json\n".Length..^"\n```".Length]);
        }

        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(value));
        }
    }

    private static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // The tools of a run: read_file with a structured result, a tool of an MCP server, a tool of the model only.
    private sealed class FakeTools : IAgentRunToolInvoker
    {
        public List<string> Calls { get; } = [];

        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal) { ["a.txt"] = "hello" };

        public TaskCompletionSource? Gate { get; init; }

        // The tool the gate holds; null holds every tool.
        public string? GatedTool { get; init; }

        public IReadOnlyList<AgentToolSpec> ListTools() =>
        [
            new("read_file", "Reads a file.", Schema("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")) { RunsInParallel = true, Idempotent = true },
            new("github_list_issues", "Lists the issues of a repository.", Schema("""{"type":"object","properties":{"state":{"type":"string"}}}""")) { Group = "github", ScriptName = "list_issues" },
            new("alta", "alta", Schema("""{"type":"object"}""")),
        ];

        public async Task<AgentToolResult> InvokeAsync(AgentNestedToolCall call, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add(call.ToolName);
            if (Gate is not null && (GatedTool is null || GatedTool == call.ToolName)) await Gate.Task.WaitAsync(cancellationToken);
            if (call.ToolName == "read_file")
            {
                var path = call.Arguments.GetProperty("path").GetString()!;
                if (!Files.TryGetValue(path, out var content)) return new AgentToolResult(false, [new AgentToolResultItem.Text($"{path} does not exist.")], $"{path} does not exist.");
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", path);
                    writer.WriteString("content", content);
                    writer.WriteBoolean("more", false);
                    writer.WriteEndObject();
                }

                return new AgentToolResult(true, [new AgentToolResultItem.Text(content)]) { Structured = JsonDocument.Parse(stream.ToArray()).RootElement.Clone() };
            }

            return new AgentToolResult(true, [new AgentToolResultItem.Text("2 open issues")]);
        }
    }
}

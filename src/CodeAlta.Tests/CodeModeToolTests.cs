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
        var invoker = new FakeTools { Alta = AltaTool(root.Path) };
        var exec = Create(root.Path, runs, sourceFolder: Path.Combine(root.Path, "sources"));

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

        // Every call of the script is a call of a tool of the session, `alta` included.
        CollectionAssert.AreEqual(new[] { "read_file", "github_list_issues", "alta", "alta" }, invoker.Calls.ToArray());
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
        var exec = Create(root.Path, runs, sourceFolder: Path.Combine(root.Path, "sources"));

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
        StringAssert.Contains(text, " characters): they are in the full result …");
        StringAssert.Contains(line, " (400 lines, ");
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
    public async Task Exec_KeepsALongResultWhole_WithTheSecretsOfTheMachineMasked()
    {
        const string secret = "sk-exec-5e0d1c9b3a";
        using var root = TestTempDirectory.Create();
        var folder = Path.Combine(root.Path, "tool-output");
        await using var runs = new CodeModeRunRegistry(null)
        {
            ResultFolder = (_, _) => ValueTask.FromResult<string?>(folder),
            SecretMasker = new CodeAlta.Agent.Runtime.AgentSecretMasker(() => [secret]),
        };
        var exec = Create(root.Path, runs, sourceFolder: Path.Combine(root.Path, "sources"));

        // The script builds the secret: its source holds none.
        var result = await InvokeAsync(exec, """
            // @exec: {"max_output_tokens": 300}
            const key = "sk-exec-" + "5e0d1c9b3a";
            return { lines: Array.from({ length: 400 }, (_, i) => `line ${i} ${key}`).join("\n") };
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        var files = Directory.GetFiles(folder);
        Assert.IsNotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal), $"{Path.GetFileName(file)} holds the secret.");
            StringAssert.Contains(text, "line 399 ***");
        }
    }

    [TestMethod]
    public void Exec_HasARawTextForm_WithTheGrammarOfCodex_AndKeepsItsJsonForm()
    {
        using var root = TestTempDirectory.Create();
        var (dispatcher, _) = Dispatcher(root.Path);
        var options = new CodeModeToolOptions { Runs = new CodeModeRunRegistry(null), Cache = new CodeModeScriptCache() };
        foreach (var subSessions in new[] { true, false })
        {
            var caller = new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path };
            caller = caller with { SessionOptions = caller.SessionOptions with { SubSessions = subSessions } };
            var tools = CodeModeToolFactory.Create(dispatcher, caller, options);
            var exec = tools.Single(static tool => tool.Spec.Name == "exec");

            Assert.IsTrue(exec.Spec.InputSchema.GetProperty("properties").TryGetProperty("code", out _));
            Assert.IsFalse(exec.Spec.Description.Contains("FREEFORM", StringComparison.Ordinal));
            var freeform = exec.Spec.FreeformInput;
            Assert.IsNotNull(freeform);
            Assert.AreEqual("code", freeform.ArgumentName);
            Assert.AreEqual(AgentToolFreeformSyntax.Lark, freeform.Syntax);
            StringAssert.StartsWith(freeform.Definition, "start: pragma_source | plain_source\n");
            StringAssert.Contains(freeform.Definition, "PRAGMA_LINE: /[ \\t]*\\/\\/ @exec:[^\\r\\n]*/");
            // The raw-text form says to send the source itself; the rest of its description is the JSON form's.
            StringAssert.Contains(freeform.Description, "This is a FREEFORM tool: send the raw JavaScript source itself as the input, not JSON");
            Assert.AreEqual(exec.Spec.Description, freeform.Description!.Replace(" This is a FREEFORM tool: send the raw JavaScript source itself as the input, not JSON, a quoted string or a Markdown code fence.", string.Empty, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task Exec_ATextToolTakesItsTextAlone_OrInItsObject()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var invoker = new PatchTools();
        var exec = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            const a = await tools.apply_patch("*** Begin Patch\n*** Add File: a.txt\n+a\n*** End Patch");
            const b = await tools.apply_patch({ input: "*** Begin Patch\n*** Add File: b.txt\n+b\n*** End Patch" });
            let refused = "";
            try { await tools.read_file("a.txt"); } catch (e) { refused = e.message; }
            return { a, b, refused };
            """, invoker);

        Assert.IsTrue(result.Success, Text(result));
        CollectionAssert.AreEqual(
            new[] { "*** Begin Patch\n*** Add File: a.txt\n+a\n*** End Patch", "*** Begin Patch\n*** Add File: b.txt\n+b\n*** End Patch" },
            invoker.Patches.ToArray());
        using var value = ValueOf(result);
        // A tool whose input is not one text takes an object only.
        StringAssert.Contains(value.RootElement.GetProperty("refused").GetString(), "args must be an object");
    }

    [TestMethod]
    public async Task Exec_LeavesOutTheToolsOfTheModelAndItself()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var exec = Create(root.Path, runs);

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
        var exec = Create(root.Path, runs);

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
        var exec = Create(root.Path, runs);

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
    public async Task Agent_AndSession_TakeTheModelSelectionOfSessionCreate_UnderItsNamesToo()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var agents = new FakeAgents();
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = agents }).Single(static tool => tool.Spec.Name == "exec");

        var result = await InvokeAsync(exec, """
            await agent("one", { model: "codex:gpt@low", reasoning: "max", agent: "plan", sandbox: "workspace", timeout: 5000 });
            await agent("two", { provider: "codex", model: "gpt", effort: "xhigh", promptId: "plan" });
            await agent("colon", { provider: "ollama", model: "llama3:8b" });
            await agent("three", { modelRef: "codex:gpt", reasoning: "none", title: undefined, tier: null });
            await session({ title: "kept", provider: "codex", model: "codex:gpt", reasoning: "minimal" });
            return "done";
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        var requests = agents.All.ToArray();
        Assert.AreEqual(("codex:gpt@low", AgentReasoningEffort.Max, "plan", "workspace", TimeSpan.FromSeconds(5)),
            (requests[0].ModelRef, requests[0].Effort, requests[0].AgentPromptId, requests[0].Sandbox, requests[0].Timeout));
        Assert.AreEqual(("codex:gpt", AgentReasoningEffort.XHigh, "plan"), (requests[1].ModelRef, requests[1].Effort, requests[1].AgentPromptId));
        Assert.AreEqual("ollama:llama3:8b", requests[2].ModelRef, "the id of a model may hold a colon of its own");
        Assert.AreEqual(("codex:gpt", AgentReasoningEffort.None, null, null), (requests[3].ModelRef, requests[3].Effort, requests[3].Title, requests[3].Tier));
        Assert.AreEqual(("codex:gpt", AgentReasoningEffort.Minimal), (requests[4].ModelRef, requests[4].Effort));
    }

    // Issue #475: `reasoning` was dropped in silence, and the session ran at another effort than the one asked for.
    [TestMethod]
    public async Task Agent_AndSession_RefuseAnOptionTheyDoNotTake_AndStartNoSession()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var agents = new FakeAgents();
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = agents }).Single(static tool => tool.Spec.Name == "exec");

        var result = await InvokeAsync(exec, """
            const failure = (promise) => promise.then(() => "started", (e) => `${e.name}: ${e.message}`);
            const kept = await session({ title: "kept" });
            return [
              await failure(agent("a", { reasonning: "max" })),
              await failure(agent("a", { reasoning: "extreme" })),
              await failure(agent("a", { reasoning: "max", effort: "low" })),
              await failure(agent("a", { timeout: "5m" })),
              await failure(agent("a", { worktree: "yes" })),
              await failure(agent("a", { provider: "codex" })),
              await failure(session({ schema: {} })),
              await failure(kept.run("a", { tier: "quick" })),
            ];
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        var failures = value.RootElement.EnumerateArray().Select(static item => item.GetString()!).ToArray();
        StringAssert.Contains(failures[0], "TypeError: agent(prompt, options): unknown option `reasonning`. Did you mean `reasoning`? It takes: title, model, provider, tier, reasoning, agent,");
        StringAssert.Contains(failures[1], "'extreme' is not a reasoning effort");
        StringAssert.Contains(failures[2], "`reasoning` and `effort` are the same option");
        StringAssert.Contains(failures[3], "`timeout` is a positive integer");
        StringAssert.Contains(failures[4], "`worktree` is a boolean");
        StringAssert.Contains(failures[5], "`provider` goes with `model`");
        StringAssert.Contains(failures[6], "session(options): unknown option `schema`");
        StringAssert.Contains(failures[7], "session.run(prompt, options): unknown option `tier`. It takes: timeout, schema.");
        Assert.AreEqual(1, agents.All.Count, "only the session that was kept started");
    }

    [TestMethod]
    public async Task Describe_OfAgentAndSession_DeclaresEveryOptionTheirFunctionsTake()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = new FakeAgents() }).Single(static tool => tool.Spec.Name == "exec");

        var result = await InvokeAsync(exec, """return { agent: await codemode.describe({ path: "agent" }), session: await codemode.describe({ path: "session" }) };""", new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        var declarations = new[] { value.RootElement.GetProperty("agent").GetString()!, value.RootElement.GetProperty("session").GetString()! };
        foreach (var (declaration, function, own) in new[] { (declarations[0], CodeModeAgentBinding.Function, "prompt"), (declarations[1], CodeModeAgentBinding.Functions[1], "") })
        {
            using var schema = JsonDocument.Parse(function.InputSchemaJson!);
            foreach (var option in schema.RootElement.GetProperty("properties").EnumerateObject().Where(option => option.Name != own))
            {
                StringAssert.Contains(declaration, option.Name + "?: ", $"`{option.Name}` of {function.Path}");
            }
        }

        StringAssert.Contains(declarations[0], "reasoning?: \"minimal\" | \"low\" | \"medium\" | \"high\" | \"xhigh\" | \"max\" | \"none\"");
        StringAssert.Contains(declarations[1], "run(prompt: string, options?: {");
    }

    [TestMethod]
    public async Task AnAltaCallOfAScript_IsACallOfTheAltaToolOfItsSession_WhichItsHooksCheck()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var exec = Create(root.Path, runs);
        const string script = """
            return await step("write", () => alta.prompt.create({ promptId: "reviewer", scope: "global", stdin: "Review." }))
              .then((created) => created.type, (e) => `${e.name}: ${e.message}`);
            """;

        var allowed = new FakeTools { Alta = AltaTool(root.Path) };
        var ran = await InvokeAsync(exec, script, allowed);
        var blocked = new FakeTools { Alta = AltaTool(root.Path), AltaBlocked = "The policy of this project refuses `alta prompt create`." };
        var refused = await InvokeAsync(exec, script, blocked);

        using var value = ValueOf(ran);
        StringAssert.StartsWith(value.RootElement.GetString(), "alta.prompt", Text(ran));
        var call = allowed.AltaCalls.Single();
        Assert.AreEqual("""{"args":["prompt","create","--scope=global","--stdin","--","reviewer"],"stdin":"Review."}""", call.Arguments.GetRawText(), "a hook sees the command line the tool runs");
        Assert.AreEqual("write", call.StepId);
        StringAssert.StartsWith(call.ScriptRunId, "run-");
        using var error = ValueOf(refused);
        Assert.AreEqual("ToolError: The policy of this project refuses `alta prompt create`.", error.RootElement.GetString(), Text(refused));

        // A hook that replaces the result: what is not the output of the command is a failure, never a value.
        using var other = JsonDocument.Parse("""{"note":"redacted"}""");
        foreach (var success in new[] { true, false })
        {
            var replaced = await InvokeAsync(exec, script, new FakeTools
            {
                Alta = AltaTool(root.Path),
                AltaReplaced = new AgentToolResult(success, [new AgentToolResultItem.Text("redacted")]) { Structured = other.RootElement.Clone() },
            });
            using var answer = ValueOf(replaced);
            StringAssert.StartsWith(answer.RootElement.GetString(), "ToolError: ", Text(replaced));
        }
    }

    // The sub-sessions of a session answer it by themselves: a turn is never held for them, whatever the script or the
    // model asks, so that the user can always reach the session.
    [TestMethod]
    public async Task AScriptThatWaitsForSubAgents_IsNotWaitedFor_ByItsExecPastTheUsualYield()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var agents = new GatedAgents();
        var (dispatcher, _) = Dispatcher(root.Path);
        var tools = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Agents = agents, DefaultYield = TimeSpan.FromMilliseconds(100) });
        var exec = tools.Single(static tool => tool.Spec.Name == "exec");

        var started = await InvokeAsync(exec, """
            // @exec: {"yield_ms": 600000}
            return await Promise.all([agent("Review A."), agent("Review B.")]);
            """, new FakeTools()).WithinAsync(TestTime.Seconds(30));
        var runId = runs.List("session-1").Single().Id;
        agents.Release.SetResult();
        var result = await runs.FindAny(runId)!.Completion.WithinAsync(TestTime.Seconds(30));

        StringAssert.Contains(Text(started), "It waits for 2 sub-agent sessions.");
        StringAssert.Contains(Text(started), "its result comes back to this session as a prompt when it ends");
        StringAssert.Contains(result.ValueJson, "answer to Review A.", "the script itself goes on to its end");
    }

    private sealed class GatedAgents : ICodeModeAgents
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CodeModeAgentResult> RunAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken)
        {
            await Release.Task.WaitAsync(cancellationToken);
            return new CodeModeAgentResult("child", true, "completed", "answer to " + request.Prompt, null, Orchestration.Runtime.SessionUsageTotals.None, TimeSpan.Zero);
        }

        public Task<CodeModeAgentResult> OpenAsync(string parentSessionId, CodeModeAgentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CodeModeAgentResult> AskAsync(string runId, string sessionId, string prompt, JsonElement? schema, TimeSpan? timeout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CloseAsync(string runId, string sessionId) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task AnExecOfASession_ThatComesWithoutTheToolsOfItsSession_IsRefused_NotRunUnchecked()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "agent", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), OfSessions = true }).Single();

        // What a provider does with a call it cannot match to a turn: it runs the tool with no tools of the session.
        var detached = await InvokeAsync(exec, """{"code":"return await alta.prompt.list({});"}""");
        var started = runs.List("session-1").Count;
        var inTurn = await InvokeAsync(exec, "return (await alta.prompt.list({})).length > 0;", new FakeTools { Alta = AltaTool(root.Path) });

        Assert.IsFalse(detached.Success);
        StringAssert.Contains(Text(detached), "outside a turn of its session");
        Assert.AreEqual(0, started, "no run started");
        Assert.IsTrue(inTurn.Success, Text(inTurn));
    }

    [TestMethod]
    public async Task Describe_OfAnAltaCommand_PointsToItsHelp_AndDescribesThatHelpToo()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var exec = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            return {
              command: await codemode.describe({ path: "alta.prompt.show" }),
              help: await codemode.describe({ path: "alta.prompt.show.help" }),
              text: await alta.prompt.show.help(),
            };
            """, new FakeTools { Alta = AltaTool(root.Path) });

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        StringAssert.Contains(value.RootElement.GetProperty("command").GetString(), "`await alta.prompt.show.help()` gives its help");
        StringAssert.Contains(value.RootElement.GetProperty("help").GetString(), "Promise<string>");
        StringAssert.StartsWith(value.RootElement.GetProperty("text").GetString(), "Usage: alta prompt show");
    }

    [TestMethod]
    public async Task ASessionWhoseAltaToolIsOff_HasNoAltaInItsScripts()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache() }).Single(static tool => tool.Spec.Name == "exec");
        const string script = """
            const help = await codemode.help({});
            return { namespaces: help, list: typeof alta === "undefined" ? "none" : typeof alta.prompt, run: typeof alta === "undefined" ? "none" : typeof alta.run };
            """;

        var with = await InvokeAsync(exec, script, new FakeTools());
        var without = await InvokeAsync(exec, script, new WithoutAlta(new FakeTools()));

        using var offered = ValueOf(with);
        Assert.AreEqual("function", offered.RootElement.GetProperty("run").GetString(), Text(with));
        using var value = ValueOf(without);
        Assert.AreEqual("none", value.RootElement.GetProperty("list").GetString(), Text(without));
        Assert.IsFalse(value.RootElement.GetProperty("namespaces").GetString()!.Contains("alta (", StringComparison.Ordinal));
    }

    private sealed class WithoutAlta(IAgentRunToolInvoker inner) : IAgentRunToolInvoker
    {
        public IReadOnlyList<AgentToolSpec> ListTools() => [.. inner.ListTools().Where(static spec => spec.Name != "alta")];

        public Task<AgentToolResult> InvokeAsync(AgentNestedToolCall call, CancellationToken cancellationToken) => inner.InvokeAsync(call, cancellationToken);
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
        var exec = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            try { await tools.read_file({ path: "missing.txt" }); return "no error"; }
            catch (e) { return [e.name, e.code, e.message]; }
            """, new FakeTools());

        using var value = ValueOf(result);
        Assert.AreEqual("""["ToolError","read_file","missing.txt does not exist."]""", value.RootElement.GetRawText());
    }

    [TestMethod]
    public async Task ARunPastItsYield_GoesOnInTheBackground_AndItsResultComesBackAsOnePrompt()
    {
        using var root = TestTempDirectory.Create();
        var delivered = new List<string>();
        await using var runs = new CodeModeRunRegistry((_, prompt, _) => { lock (delivered) delivered.Add(prompt); return Task.CompletedTask; });
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var exec = Create(root.Path, runs);

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

        await EndAsync(runs, runId, invoker);

        lock (delivered)
        {
            StringAssert.Contains(delivered.Single(), "hello", "the result finds its session by itself");
        }
    }

    [TestMethod]
    public async Task ARunThatEndsDuringTheTurnThatStartedIt_IsAPromptOnceThatTurnEnds()
    {
        using var root = TestTempDirectory.Create();
        var delivered = new List<string>();
        string? turn = "turn-1";
        await using var runs = new CodeModeRunRegistry((_, prompt, _) => { lock (delivered) delivered.Add(prompt); return Task.CompletedTask; })
        {
            ActiveTurnOf = (_, _) => Task.FromResult<string?>(turn),
        };
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var exec = Create(root.Path, runs);
        await InvokeAsync(exec, """
            // @exec: {"yield_ms": 50}
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        var runId = runs.List("session-1").Single().Id;

        await EndAsync(runs, runId, invoker);
        Assert.IsEmpty(delivered);
        await runs.TurnEndedAsync("session-1");
        Assert.IsEmpty(delivered, "the turn still runs: its end comes later");

        turn = null;
        await runs.TurnEndedAsync("session-1");
        StringAssert.Contains(delivered.Single(), "hello", "the result comes back as a prompt once the turn ended");
        await runs.TurnEndedAsync("session-1");
        Assert.HasCount(1, delivered, "and only once");
    }

    [TestMethod]
    public async Task ARunThatEndsAfterTheTurnThatStartedIt_IsAPromptAtOnce()
    {
        using var root = TestTempDirectory.Create();
        var delivered = new List<string>();
        string? turn = "turn-1";
        await using var runs = new CodeModeRunRegistry((_, prompt, _) => { lock (delivered) delivered.Add(prompt); return Task.CompletedTask; })
        {
            ActiveTurnOf = (_, _) => Task.FromResult<string?>(turn),
        };
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var exec = Create(root.Path, runs);
        await InvokeAsync(exec, """
            // @exec: {"yield_ms": 50}
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        var runId = runs.List("session-1").Single().Id;

        // The turn ended, and another one of the user runs: the result reaches the session as it does today.
        turn = "turn-2";
        await EndAsync(runs, runId, invoker);
        StringAssert.Contains(delivered.Single(), "hello");
    }

    // Lets a run that waits at its gate end, and waits until the registry has handled its end.
    private static async Task EndAsync(CodeModeRunRegistry runs, string runId, FakeTools invoker)
    {
        var ended = new TaskCompletionSource();
        void OnEnded(string id) { if (id == runId) ended.TrySetResult(); }
        runs.RunEnded += OnEnded;
        try
        {
            invoker.Gate!.SetResult();
            await ended.Task.WithinAsync(TestTime.Seconds(20));
        }
        finally
        {
            runs.RunEnded -= OnEnded;
        }
    }

    [TestMethod]
    public async Task TheDescriptionOfExec_AsksForBatches_AndNotToWait_TheSameForEverySession()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var exec = Create(root.Path, runs);
        var again = Create(root.Path, runs);

        StringAssert.Contains(exec.Spec.Description, "Use it to batch independent reads and searches, or edits you have decided, into one step");
        StringAssert.Contains(exec.Spec.Description, "In a batch, use `Promise.allSettled`");
        StringAssert.Contains(exec.Spec.Description, $"`max_output_tokens` is {CodeModeResultFormatter.DefaultMaxOutputTokens} by default");
        Assert.DoesNotContain("\"max_output_tokens\": ", exec.Spec.Description);
        StringAssert.Contains(exec.Spec.Description, "Do not wait for it: while a tool call waits, the user cannot reach you.");
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
        await run.Completion.WithinAsync(TestTime.Seconds(10));
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
        var exec = Create(root.Path, runs);

        await InvokeAsync(exec, """
            // @exec: {"title": "slow", "yield_ms": 50}
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        invoker.Gate.SetResult();
        var (session, prompt) = await delivered.Task.WithinAsync(TestTime.Seconds(10));

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
        var exec = Create(root.Path, runs);

        await InvokeAsync(exec, """
            // @exec: {"title": "slow", "yield_ms": 50}
            image("chart.png", { title: "Chart" });
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        invoker.Gate.SetResult();
        var (prompt, images) = await delivered.Task.WithinAsync(TestTime.Seconds(10));

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
        var exec = Create(root.Path, runs);

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

    // A picture is read as the session reads one: a file its sandbox or its tools keep from it is not attached.
    [TestMethod]
    public async Task APictureOfAScript_IsReadThroughTheImageToolOfItsSession_NeverBesideIt()
    {
        using var root = TestTempDirectory.Create();
        File.WriteAllBytes(Path.Combine(root.Path, "chart.png"), Png);
        await using var runs = new CodeModeRunRegistry(null);
        var exec = Create(root.Path, runs);
        const string script = """image("chart.png"); return "done";""";

        var refused = await InvokeAsync(exec, script, new FakeTools { ImageRefusal = "This file is outside the sandbox of the session." });
        var without = await InvokeAsync(exec, script, new FakeTools { ImageRefusal = "none" });

        Assert.HasCount(1, refused.Items, "no picture is attached");
        StringAssert.Contains(Text(refused), $"image skipped: {Path.Combine(root.Path, "chart.png")}: This file is outside the sandbox of the session.");
        Assert.HasCount(1, without.Items);
        StringAssert.Contains(Text(without), "this session has no `view_image` tool");
    }

    [TestMethod]
    public async Task Exec_JournalsEachPresentedPart_AndTellsThemOnceWithTheResult()
    {
        using var root = TestTempDirectory.Create();
        var records = new List<AgentWorkflowEvent>();
        await using var runs = new CodeModeRunRegistry(null) { Journal = (_, record) => { lock (records) records.Add(record); return Task.CompletedTask; } };
        var exec = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            present("## Reading");
            const content = await step("read", async () => { const read = await tools.read_file({ path: "a.txt" }); present(`- read **${read.content.length}** characters`); return read.content; });
            return { content };
            """, new FakeTools());

        var text = Text(result);
        Assert.IsTrue(result.Success, text);
        await runs.FindAny(runs.List("session-1").Single().Id)!.JournalWritten;
        AgentWorkflowEvent[] presented;
        lock (records) presented = [.. records.Where(static record => record.Kind == AgentWorkflowEventKind.Presented)];
        CollectionAssert.AreEqual(new[] { ("## Reading", (string?)null), ("- read **5** characters", "read") }, presented.Select(static record => (record.Markdown!, record.StepId)).ToArray());
        Assert.IsTrue(presented.All(static record => record.CallId == "call-1"), "the card of the call reads them");
        Assert.IsNull(records.Single(static record => record.Kind == AgentWorkflowEventKind.RunStarted).ForUser, "a run of an agent");
        Assert.AreEqual(1, text.Split(CodeModeResultFormatter.PresentedHeading).Length - 1, "told once");
        StringAssert.Contains(text, CodeModeResultFormatter.PresentedHeading + "\n```markdown\n## Reading\n\n- read **5** characters\n```\n");
        StringAssert.EndsWith(text, "Result:\n```json\n{\"content\":\"hello\"}\n```\n");
    }

    [TestMethod]
    public async Task ARunALauncherOfTheLibraryAsksFor_IsForTheUser()
    {
        using var root = TestTempDirectory.Create();
        var records = new List<AgentWorkflowEvent>();
        await using var runs = new CodeModeRunRegistry(null) { Journal = (_, record) => { lock (records) records.Add(record); return Task.CompletedTask; } };
        var exec = Create(root.Path, runs);
        const string Code = """
            // @exec: {"title": "review", "yield_ms": 600000}
            return await workflow("review", {"pr":1});
            """;

        await InvokeAsync(exec, Code, new FakeTools());
        runs.RequireCodeMode("session-1");
        await InvokeAsync(exec, Code, new FakeTools());
        await InvokeAsync(exec, "const value = await workflow(\"review\", {});\nreturn value;", new FakeTools());
        foreach (var run in runs.List("session-1")) await runs.FindAny(run.Id)!.JournalWritten;

        bool?[] forUser;
        lock (records) forUser = [.. records.Where(static record => record.Kind == AgentWorkflowEventKind.RunStarted).Select(static record => record.ForUser)];
        CollectionAssert.AreEqual(new bool?[] { null, true, null }, forUser, "only the call a launcher asks for, in a session it started");
    }

    // A picture of one pixel.
    private static readonly byte[] Png =Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    [TestMethod]
    public async Task ARunOverTheLimitOfItsSession_IsRefused()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null, maxPerSession: 1);
        var invoker = new FakeTools { Gate = new TaskCompletionSource() };
        var exec = Create(root.Path, runs);

        await InvokeAsync(exec, """
            // @exec: {"yield_ms": 10}
            return (await tools.read_file({ path: "a.txt" })).content;
            """, invoker);
        var refused = await InvokeAsync(exec, "return 1;", invoker);
        runs.Find("session-1", runs.List("session-1").Single().Id)!.Stop();

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(Text(refused), "already runs 1 scripts");
        StringAssert.Contains(Text(refused), "alta workflow cancel <run-id>");
    }

    [TestMethod]
    public async Task Workflow_RunsAScriptFile_ReadThroughTheSessionsReadFile()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var invoker = new FakeTools();
        invoker.Files["review.js"] = "return `reviewed ${args.pr}`;";
        var exec = Create(root.Path, runs);

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
        var exec = Create(project, runs, library: library);

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
        var exec = Create(project, runs, library: library);

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
              listing: await paths("library script list run"),
              prefix: await paths("workflow:"),
              help: await codemode.help(),
              workflow: await codemode.help({ path: "workflow" }),
              agent: await codemode.describe({ path: "agent" }),
              load: await codemode.describe({ path: "load" }),
            };
            """;

        var result = await InvokeAsync(exec, Script, new FakeTools());
        // A run without sessions to start has no agent().
        var bare = Create(project, runs, library: library);
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
        CollectionAssert.Contains(root0.GetProperty("listing").EnumerateArray().Select(static hit => hit.GetString()).ToList(), "workflow:release-code",
            "the scripts of the library are among the first results of a query for them, before the many commands that list or run something");
        CollectionAssert.Contains(root0.GetProperty("prefix").EnumerateArray().Select(static hit => hit.GetString()).ToList(), "workflow:release-code");
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

        CollectionAssert.AreEqual(new[] { "alta", "exec", "review_pr" }, Names(null), "without the space of the session, the scripts of the project only");
        CollectionAssert.AreEqual(new[] { "alta", "exec", "review_pr" }, Names(SpaceFilter.Load(store, "home")));
        CollectionAssert.AreEqual(new[] { "alta", "exec", "mine", "review_pr" }, Names(SpaceFilter.Load(store, "work")));

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
        CollectionAssert.AreEqual(new[] { "alta", "exec" },
            codeMode.Create(dispatcher, new AltaSessionToolOptions { SourceSessionIdProvider = static () => "automation-session" }, others, null, null).Select(static tool => tool.Spec.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "alta" }, codeMode.Create(dispatcher, new AltaSessionToolOptions { SourceSessionId = "other" }, others, null, null).Select(static tool => tool.Spec.Name).ToArray());
        mode = "on";
        var with = codeMode.Create(dispatcher, new AltaSessionToolOptions(), others, null, null);
        mode = "only";
        var only = codeMode.Create(dispatcher, new AltaSessionToolOptions(), others, null, null);
        Assert.AreEqual("only", runs.CodeModeOf("other", null, null));

        CollectionAssert.AreEqual(new[] { "alta" }, off.Select(static tool => tool.Spec.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "alta", "exec" }, with.Select(static tool => tool.Spec.Name).ToArray());
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
        Agree("gpt", "gpt-6.1-sol", "on", "alta,exec");
        Agree("haiku", "claude-haiku-4.5", "on", "alta,exec");

        // A model turned off, beside one of the same provider that is not.
        File.WriteAllText(config, "[providers.copilot]\ntype = \"openai-chat\"\n\n[providers.copilot.model_overrides.\"claude-haiku-4.5\"]\ncode_mode = \"off\"\n");
        Agree("gpt", "gpt-6.1-sol", "on", "alta,exec");
        Agree("haiku", "claude-haiku-4.5", "off", "alta");

        // The model of a provider wins over the provider, which wins over every session.
        File.WriteAllText(config, "[code_mode]\nmode = \"only\"\n\n[providers.copilot]\ntype = \"openai-chat\"\ncode_mode = \"off\"\n\n[providers.copilot.model_overrides.\"claude-haiku-4.5\"]\ncode_mode = \"on\"\n");
        Agree("gpt", "gpt-6.1-sol", "off", "alta");
        Agree("haiku", "claude-haiku-4.5", "on", "alta,exec");
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
        var exec = Create(root.Path, runs);
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
        await runs.WhenKeptAsync(second.Id).WithinAsync(TestTime.Seconds(10));
        Assert.AreEqual(runId, runs.Store!.TryLoad(second.Id)?.ResumesRunId);
    }

    [TestMethod]
    public async Task Resume_IsRefused_ForAnUnknownRun_AndForAChangedScript()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        await runs.UseStoreAsync(new CodeModeRunStore(Path.Combine(root.Path, "runs")));
        var exec = Create(root.Path, runs);
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
        var execBefore = Create(root.Path, before);
        var hanging = new FakeTools { Gate = new TaskCompletionSource() };
        const string code = """
            // @exec: {"yield_ms": 50}
            return await step("issues", async () => await mcp.github.list_issues({ state: "open" }));
            """;
        await InvokeAsync(execBefore, code, hanging);
        var runId = before.List("session-1").Single().Id;
        var deadline = DateTime.UtcNow + TestTime.Seconds(10);
        while (before.Store!.TryLoad(runId)?.InFlightCalls.Count is not 1)
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "the call in flight was not recorded");
            await Task.Delay(20);
        }

        // CodeAlta starts again: the run is interrupted, with its call in flight.
        await using var after = new CodeModeRunRegistry(null);
        await after.UseStoreAsync(new CodeModeRunStore(store));
        var exec = Create(root.Path, after);
        var tools = new FakeTools();
        var refused = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}"}""", tools);
        var agreed = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}", "rerun_in_flight": true}""", tools);
        hanging.Gate.SetResult();

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(Text(refused), "`mcp.github.list_issues` in step `issues` with {\"state\":\"open\"}");
        Assert.IsTrue(agreed.Success, Text(agreed));
        CollectionAssert.AreEqual(new[] { "github_list_issues" }, tools.Calls.ToArray());
    }

    [TestMethod]
    public async Task Resume_RunsANotificationAgain_RatherThanStopOnIt()
    {
        using var root = TestTempDirectory.Create();
        var store = Path.Combine(root.Path, "runs");
        await using var before = new CodeModeRunRegistry(null);
        await before.UseStoreAsync(new CodeModeRunStore(store));
        var execBefore = Create(root.Path, before);
        var hanging = new FakeTools { Gate = new TaskCompletionSource() };
        await InvokeAsync(execBefore, """
            // @exec: {"yield_ms": 50}
            await notify("Started");
            return await mcp.github.list_issues({ state: "open" });
            """, hanging);
        var runId = before.List("session-1").Single().Id;
        var deadline = DateTime.UtcNow + TestTime.Seconds(10);
        while (before.Store!.TryLoad(runId)?.InFlightCalls.Count is not 1)
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "the call in flight was not recorded");
            await Task.Delay(20);
        }

        // The notification shown before is no call that may have changed something: CodeAlta starts again, and only the call
        // in flight holds the resume.
        Assert.IsEmpty(before.Store.TryLoad(runId)!.CompletedCalls);
        await using var after = new CodeModeRunRegistry(null);
        await after.UseStoreAsync(new CodeModeRunStore(store));
        var refused = await InvokeAsync(Create(root.Path, after), $$"""// @exec: {"resume": "{{runId}}"}""", new FakeTools());
        hanging.Gate.SetResult();

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(Text(refused), "`mcp.github.list_issues` outside any step");
        Assert.DoesNotContain("codemode.notify", Text(refused));
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
        var exec = Create(project, runs, library: library);
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
        var execBefore = Create(project, before, library: library);
        var hanging = new FakeTools { Gate = new TaskCompletionSource(), GatedTool = "github_list_issues" };
        await InvokeAsync(execBefore, """
            // @exec: {"yield_ms": 50}
            return await workflow("slow-steps", {});
            """, hanging);
        var runId = before.List("session-1").Single().Id;
        var deadline = DateTime.UtcNow + TestTime.Seconds(10);
        while (before.Store!.TryLoad(runId) is not { Steps.Count: 1, InFlightCalls.Count: 1 })
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "the step and the call in flight were not recorded");
            await Task.Delay(20);
        }

        // CodeAlta starts again while the run waited for the issues.
        await using var after = new CodeModeRunRegistry(null);
        await after.UseStoreAsync(new CodeModeRunStore(store));
        var exec = Create(project, after, library: library);
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
        var withoutQuestions = Create(root.Path, runs);

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
    public async Task Resume_UsesTheNewCallsYield_NotTheYieldInTheKeptSource()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        await runs.UseStoreAsync(new CodeModeRunStore(Path.Combine(root.Path, "runs")));
        var (dispatcher, _) = Dispatcher(root.Path);
        var exec = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { WorkingDirectory = root.Path },
            new CodeModeToolOptions { Runs = runs, Cache = new(), DefaultYield = TimeSpan.FromMilliseconds(50) }).Single();
        var original = await InvokeAsync(exec, """
            // @exec: {"yield_ms": 600000}
            return await tools.read_file({ path: "a.txt" });
            """, new FakeTools());
        Assert.IsTrue(original.Success, Text(original));
        var runId = runs.List("session-1").Single().Id;
        var tools = new FakeTools { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            var resumed = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}"}""", tools).WithinAsync(TestTime.Seconds(5));
            StringAssert.Contains(Text(resumed), "in the background");
        }
        finally
        {
            tools.Gate.SetResult();
        }
    }

    [TestMethod]
    public async Task ANestedWorkflowsUnawaitedSleep_DoesNotLeaveItsCallerSleeping()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var tools = new FakeTools { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), GatedTool = "github_list_issues" };
        tools.Files["nested.js"] = """sleep("1h"); return 1;""";
        var exec = Create(root.Path, runs);
        var started = await InvokeAsync(exec, """
            // @exec: {"yield_ms": 1}
            await workflow({ scriptPath: "nested.js" });
            return await mcp.github.list_issues({});
            """, tools);
        Assert.IsTrue(started.Success, Text(started));
        var run = runs.FindAny(runs.List("session-1").Single().Id)!;
        try
        {
            async Task WaitForParentCallAsync()
            {
                while (run.Running && run.PendingCalls(["mcp.github.list_issues"]) == 0) await Task.Delay(10);
            }

            await WaitForParentCallAsync().WithinAsync(TestTime.Seconds(30));
            Assert.IsTrue(run.Running);
            Assert.IsFalse(run.Sleeping, "the nested engine cancelled its unawaited call");
        }
        finally
        {
            tools.Gate.SetResult();
        }
    }

    [TestMethod]
    public async Task ASleepingScript_GoesToTheBackground_AtTheYieldOfExec()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var exec = Create(root.Path, runs);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await InvokeAsync(exec, """
            // @exec: {"yield_ms": 1500}
            await sleep("1h");
            return 1;
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        Assert.IsLessThan(TestTime.Seconds(30), clock.Elapsed, "the turn does not wait for the sleep");
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
        var exec = Create(root.Path, runs);
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

    private static AgentToolDefinition AltaTool(string root)
        => AltaSessionToolFactory.Create(Dispatcher(root).Dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root });

    private static AgentToolDefinition Create(string root, CodeModeRunRegistry runs, string? sourceFolder = null,
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
        return tools.Single();
    }

    private static Task<AgentToolResult> InvokeAsync(AgentToolDefinition tool, string code, IAgentRunToolInvoker invoker)
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

    // The tools of a run: apply_patch, whose input is one text, and read_file, whose input is an object.
    private sealed class PatchTools : IAgentRunToolInvoker
    {
        public List<string> Patches { get; } = [];

        public IReadOnlyList<AgentToolSpec> ListTools() =>
        [
            new("apply_patch", "Edits files.", Schema("""{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]}"""))
            {
                FreeformInput = new AgentToolFreeformInput("input", AgentToolFreeformSyntax.Lark, "start: /[\\s\\S]+/"),
            },
            new("read_file", "Reads a file.", Schema("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")),
        ];

        public Task<AgentToolResult> InvokeAsync(AgentNestedToolCall call, CancellationToken cancellationToken)
        {
            if (call.ToolName == "apply_patch")
            {
                lock (Patches) Patches.Add(call.Arguments.GetProperty("input").GetString()!);
            }

            return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text("done")]));
        }
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
            .. ImageRefusal == "none" ? Array.Empty<AgentToolSpec>() : [new AgentToolSpec("view_image", "Looks at a picture.", Schema("""{"type":"object","properties":{"path":{"type":"string"}}}"""))],
        ];

        // What the tool of the session that looks at pictures answers instead of the picture; null gives the picture, and
        // "none" is a session without that tool.
        public string? ImageRefusal { get; init; }

        // The `alta` tool of the session, which the calls of `alta.*` go through; null answers them as any other tool.
        public AgentToolDefinition? Alta { get; init; }

        public List<AgentNestedToolCall> AltaCalls { get; } = [];

        // What a hook of the session answers a call of `alta` with instead of running it; null runs it.
        public string? AltaBlocked { get; init; }

        // What a hook of the session answers a call of `alta` with after it ran, in the place of its result; null leaves it.
        public AgentToolResult? AltaReplaced { get; init; }

        public async Task<AgentToolResult> InvokeAsync(AgentNestedToolCall call, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add(call.ToolName);
            if (call.ToolName == "view_image")
            {
                var file = call.Arguments.GetProperty("path").GetString()!;
                if (ImageRefusal is { } refused) return new AgentToolResult(false, [new AgentToolResultItem.Text(refused)], refused);
                return File.Exists(file)
                    ? new AgentToolResult(true, [new AgentToolResultItem.Image(Convert.ToBase64String(File.ReadAllBytes(file)), "image/png")])
                    : new AgentToolResult(false, [new AgentToolResultItem.Text("no such file.")], "no such file.");
            }

            if (call.ToolName == "alta" && Alta is not null)
            {
                lock (AltaCalls) AltaCalls.Add(call);
                if (AltaBlocked is { } blocked) return new AgentToolResult(false, [new AgentToolResultItem.Text(blocked)], blocked);
                if (AltaReplaced is { } replaced) return replaced;
                return await Alta.Handler(new AgentToolInvocation(new ModelProviderId("provider"), "session-1", "call-1/1", "alta", call.Arguments)
                {
                    ParentToolCallId = "call-1",
                    ScriptRunId = call.ScriptRunId,
                    ScriptStepId = call.StepId,
                }, cancellationToken);
            }

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

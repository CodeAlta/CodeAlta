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
    public async Task Exec_LeavesOutTheToolsOfTheModelAndItself()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var (exec, _) = Create(root.Path, runs);

        var result = await InvokeAsync(exec, """
            const found = await codemode.help({ path: "tools" });
            return [found.includes("tools.read_file"), found.includes("tools.exec"), found.includes("tools.alta"), typeof tools.exec];
            """, new FakeTools());

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        CollectionAssert.AreEqual(new object[] { true, false, false, "undefined" },
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
    public void SessionTools_AddExecAndWait_OnlyWhileCodeModeIsOn()
    {
        using var root = TestTempDirectory.Create();
        var (dispatcher, _) = Dispatcher(root.Path);
        var on = false;
        var codeMode = new CodeModeSessionTools(new CodeModeToolOptions { Runs = new CodeModeRunRegistry(null), Cache = new CodeModeScriptCache() }, () => on);
        AgentToolDefinition[] others = [new(new AgentToolSpec("alta", "alta", Schema("{}")), static (_, _) => Task.FromResult(new AgentToolResult(true, [])))];

        var off = codeMode.Create(dispatcher, new AltaSessionToolOptions(), others);
        on = true;
        var with = codeMode.Create(dispatcher, new AltaSessionToolOptions(), others);

        CollectionAssert.AreEqual(new[] { "alta" }, off.Select(static tool => tool.Spec.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "alta", "exec", "wait" }, with.Select(static tool => tool.Spec.Name).ToArray());
        Assert.IsTrue(with.Skip(1).All(static tool => tool.Spec.Exposure == AgentToolExposure.Model), "a script does not run scripts through exec");
    }

    [TestMethod]
    public void AStructuredResult_IsTheValueOfTheCall_EvenWhenTheToolFailed()
    {
        var failed = new AgentToolResult(false, [new AgentToolResultItem.Text("exit 1")], "exit 1") { Structured = Schema("""{"exitCode":1}""") };
        var text = new AgentToolResult(true, [new AgentToolResultItem.Text("a"), new AgentToolResultItem.Text("b")]);

        Assert.AreEqual("""{"exitCode":1}""", ToolScriptBinding.Read(failed, "shell_command").ValueJson);
        Assert.AreEqual("\"a\\nb\"", ToolScriptBinding.Read(text, "x").ValueJson);
    }

    private static (AltaCommandDispatcher Dispatcher, AltaCommandRegistry Registry) Dispatcher(string root)
    {
        var registry = new AltaCommandRegistry();
        return (new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root }).Add(registry)), registry);
    }

    private static (AgentToolDefinition Exec, AgentToolDefinition Wait) Create(string root, CodeModeRunRegistry runs, string? sourceFolder = null)
    {
        var (dispatcher, _) = Dispatcher(root);
        var tools = CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root }, new CodeModeToolOptions
        {
            Runs = runs,
            Cache = new CodeModeScriptCache(),
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

    // The value of a finished run: the line after `Result:`.
    private static JsonDocument ValueOf(AgentToolResult result)
    {
        var lines = Text(result).Split('\n');
        var index = Array.IndexOf(lines, "Result:");
        Assert.IsGreaterThanOrEqualTo(0, index, Text(result));
        return JsonDocument.Parse(lines[index + 1]);
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

        public IReadOnlyList<AgentToolSpec> ListTools() =>
        [
            new("read_file", "Reads a file.", Schema("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""")) { RunsInParallel = true },
            new("github_list_issues", "Lists the issues of a repository.", Schema("""{"type":"object","properties":{"state":{"type":"string"}}}""")) { Group = "github", ScriptName = "list_issues" },
            new("alta", "alta", Schema("""{"type":"object"}""")),
        ];

        public async Task<AgentToolResult> InvokeAsync(AgentNestedToolCall call, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add(call.ToolName);
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
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

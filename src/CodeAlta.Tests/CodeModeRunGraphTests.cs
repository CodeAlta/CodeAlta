using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Tests;

/// <summary>
/// The graph of a run (the steps its script declares, and how far each went), its Mermaid text, the store as the index of
/// the runs after a restart, and the values scripts keep with <c>store</c> and <c>load</c>.
/// </summary>
[TestClass]
public sealed class CodeModeRunGraphTests
{
    private static AgentWorkflowEvent Record(AgentWorkflowEventKind kind, Func<AgentWorkflowEvent, AgentWorkflowEvent>? fill = null)
    {
        var record = new AgentWorkflowEvent(new ModelProviderId("codealta"), "session-1", DateTimeOffset.UnixEpoch, null, kind, "run-1");
        return fill is null ? record : fill(record);
    }

    private static readonly AgentWorkflowStep[] Declared =
    [
        new("plan", "Split the question"),
        new("explore", "Research each part") { After = ["plan"], Each = true },
        new("check", "Check the sources") { After = ["plan"] },
        new("write", "Write \"the\" answer") { After = ["explore", "check"] },
    ];

    [TestMethod]
    public void TheGraph_DrawsTheDeclaredSteps_FromTheStart_ThenHowFarEachWent()
    {
        var records = new[]
        {
            Record(AgentWorkflowEventKind.RunStarted),
            Record(AgentWorkflowEventKind.StepsDeclared, record => record with { Steps = Declared }),
            Record(AgentWorkflowEventKind.StepStarted, record => record with { StepId = "plan" }),
            Record(AgentWorkflowEventKind.StepEnded, record => record with { StepId = "plan", Status = "completed" }),
            Record(AgentWorkflowEventKind.StepStarted, record => record with { StepId = "explore" }),
            Record(AgentWorkflowEventKind.SessionStarted, record => record with { StepId = "explore", ChildSessionId = "child-1" }),
            Record(AgentWorkflowEventKind.SessionStarted, record => record with { StepId = "explore", ChildSessionId = "child-2" }),
            Record(AgentWorkflowEventKind.StepStarted, record => record with { StepId = "extra" }),
        };

        var graph = CodeModeRunGraph.Read(records);

        Assert.AreEqual("plan:completed:0:-|explore:running:2:plan|check:pending:0:plan|write:pending:0:explore,check|extra:running:0:write",
            string.Join('|', graph.Select(static step => $"{step.Id}:{step.State}:{step.Sessions}:{(step.After.Count == 0 ? "-" : string.Join(',', step.After))}")));
        Assert.IsFalse(graph[^1].Declared, "a step that ran without being declared comes after the others");
        Assert.AreEqual("""
            flowchart LR
              s0["Split the question · completed"]:::completed
              s1["Research each part × · running · 2 sessions"]:::running
              s2["Check the sources · pending"]:::pending
              s3["Write #quot;the#quot; answer · pending"]:::pending
              s4["extra · running"]:::running
              s0 --> s1
              s0 --> s2
              s1 --> s3
              s2 --> s3
              s3 --> s4
              classDef pending fill:#f1f3f5,stroke:#adb5bd,color:#495057,stroke-dasharray:4 3
              classDef running fill:#e7f5ff,stroke:#1c7ed6,color:#1864ab
              classDef completed fill:#ebfbee,stroke:#2f9e44,color:#2b8a3e

            """.ReplaceLineEndings("\n"), CodeModeRunGraph.ToMermaid(graph));
    }

    [TestMethod]
    public void TheGraph_OfARunThatEnded_StopsWhatStillRan_AndShowsAStepThatWaitsForTheUser()
    {
        var waiting = CodeModeRunGraph.Read(
        [
            Record(AgentWorkflowEventKind.StepsDeclared, record => record with { Steps = Declared[..2] }),
            Record(AgentWorkflowEventKind.StepStarted, record => record with { StepId = "plan" }),
            Record(AgentWorkflowEventKind.WaitStarted, record => record with { StepId = "plan", Status = "question" }),
        ]);
        var interrupted = CodeModeRunGraph.Read(
        [
            Record(AgentWorkflowEventKind.StepsDeclared, record => record with { Steps = Declared[..2] }),
            Record(AgentWorkflowEventKind.StepStarted, record => record with { StepId = "plan" }),
            Record(AgentWorkflowEventKind.RunEnded, record => record with { Status = "interrupted" }),
        ]);

        Assert.AreEqual("waiting", waiting[0].State);
        Assert.AreEqual(("interrupted", "pending"), (interrupted[0].State, interrupted[1].State));
        Assert.AreEqual("flowchart LR\n", CodeModeRunGraph.ToMermaid([]), "a run without steps is an empty flowchart");
    }

    [TestMethod]
    public async Task ARunOfAScriptWithSteps_RecordsTheStepsItDeclares_BeforeItsFirstStep()
    {
        using var root = TestTempDirectory.Create();
        var records = new List<AgentWorkflowEvent>();
        await using var runs = new CodeModeRunRegistry(null) { Journal = (_, record) => { lock (records) records.Add(record); return Task.CompletedTask; } };
        var exec = Exec(root.Path, runs);

        var result = await InvokeAsync(exec, """
            export const meta = { name: "two", steps: [{ id: "a", title: "First" }, { id: "b", title: "Then", each: true }] };
            await step("a", async () => 1);
            return await step("b", async () => 2);
            """);

        Assert.IsTrue(result.Success, Text(result));
        await runs.FindAny(runs.List("session-1").Single().Id)!.JournalWritten;
        var declared = records.Single(static record => record.Kind == AgentWorkflowEventKind.StepsDeclared);
        Assert.AreEqual("a:First:-|b:Then:each", string.Join('|', declared.Steps!.Select(static step => $"{step.Id}:{step.Title}:{(step.Each ? "each" : "-")}")));
        Assert.IsLessThan(records.FindIndex(static record => record.Kind == AgentWorkflowEventKind.StepStarted), records.IndexOf(declared));
        Assert.AreEqual("a:completed|b:completed", string.Join('|', CodeModeRunGraph.Read(records).Select(static step => $"{step.Id}:{step.State}")));
    }

    [TestMethod]
    public async Task AfterARestart_TheStoreListsTheRunsThatEnded_ShowsThem_AndGivesTheirResult()
    {
        using var root = TestTempDirectory.Create();
        var store = Path.Combine(root.Path, "runs");
        string runId;
        await using (var before = new CodeModeRunRegistry(null))
        {
            await before.UseStoreAsync(new CodeModeRunStore(store));
            var result = await InvokeAsync(Exec(root.Path, before), """
                // @exec: {"title": "Kept"}
                return 42;
                """);
            Assert.IsTrue(result.Success, Text(result));
            runId = before.List("session-1").Single().Id;
            // The end of the run is written after its result is given: wait for it, as a run that ended before CodeAlta closed.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (before.Store!.TryLoad(runId) is not { EndedAt: not null })
            {
                Assert.IsLessThan(deadline, DateTime.UtcNow, "the end of the run was not written");
                await Task.Delay(20);
            }
        }

        // CodeAlta starts again: the run is no longer in memory, the store still has it.
        await using var after = new CodeModeRunRegistry(null);
        await after.UseStoreAsync(new CodeModeRunStore(store));
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root.Path }).Add(registry).Add(after));

        var listed = await dispatcher.InvokeAsync(["workflow", "list", "--all"]);
        var shown = await dispatcher.InvokeAsync(["workflow", "show", runId, "--format", "mermaid"]);
        var result2 = await dispatcher.InvokeAsync(["workflow", "result", runId]);
        var badFormat = await dispatcher.InvokeAsync(["workflow", "show", runId, "--format", "svg"]);
        var detail = await after.ReadDetailAsync(runId);

        Assert.IsEmpty(after.List(), "nothing runs in this process");
        StringAssert.Contains(listed.Stdout, $"\"id\":\"{runId}\"");
        StringAssert.Contains(listed.Stdout, "\"state\":\"completed\"");
        StringAssert.Contains(listed.Stdout, "\"title\":\"Kept\"");
        Assert.AreEqual(AltaExitCodes.Success, shown.ExitCode, shown.Transcript);
        StringAssert.Contains(shown.Stdout, "\"mermaid\":\"flowchart LR\\n\"");
        Assert.AreEqual(AltaExitCodes.Success, result2.ExitCode, result2.Transcript);
        StringAssert.Contains(result2.Stdout, "42");
        StringAssert.Contains(badFormat.Transcript, "usage.invalidFormat");
        Assert.IsNotNull(detail);
        Assert.AreEqual(("completed", "// @exec: {\"title\": \"Kept\"}\nreturn 42;"), (detail.Info.Status, detail.Source!.ReplaceLineEndings("\n")));
        StringAssert.Contains(detail.Result, "42");
        Assert.IsNull(await after.ReadDetailAsync("run-000000"));
    }

    [TestMethod]
    public async Task StoreAndLoad_KeepValuesForTheLaterScriptsOfASession_AndNotForAnother()
    {
        using var root = TestTempDirectory.Create();
        var values = new CodeModeValueStore(Path.Combine(root.Path, "values"));
        await using var runs = new CodeModeRunRegistry(null);
        var exec = Exec(root.Path, runs, values);

        var first = await InvokeAsync(exec, """
            await store("seen", { count: 2, names: ["a"] });
            await store("gone", 1);
            await store("gone", undefined);
            return [await load("seen"), await load("gone"), await load("never")];
            """);
        var later = await InvokeAsync(exec, """return (await load("seen")).count;""");
        var other = await InvokeAsync(exec, """return [await load("seen")];""", sessionId: "session-2");

        Assert.IsTrue(first.Success, Text(first));
        using (var value = ValueOf(first)) Assert.AreEqual("""[{"count":2,"names":["a"]},null,null]""", value.RootElement.GetRawText(), "undefined comes back as null in the result");
        using (var value = ValueOf(later)) Assert.AreEqual("2", value.RootElement.GetRawText());
        using (var value = ValueOf(other)) Assert.AreEqual("[null]", value.RootElement.GetRawText(), "another session does not see them");
        Assert.IsTrue(File.Exists(Path.Combine(values.Root, "session-1.json")));
    }

    [TestMethod]
    public async Task Store_RefusesAValueOver64KB_AndTheValuesOfASessionOver1MB_WithWhatToDo()
    {
        using var root = TestTempDirectory.Create();
        await using var runs = new CodeModeRunRegistry(null);
        var exec = Exec(root.Path, runs, new CodeModeValueStore(Path.Combine(root.Path, "values")));

        var result = await InvokeAsync(exec, """
            const errors = [];
            try { await store("big", "x".repeat(70 * 1024)); } catch (e) { errors.push(`${e.code}: ${e.message}`); }
            for (let i = 0; i < 16; i++) await store(`part-${i}`, "y".repeat(62 * 1024));
            try { await store("part-16", "y".repeat(62 * 1024)); errors.push("stored"); } catch (e) { errors.push(`${e.code}: ${e.message}`); }
            try { await store("", 1); } catch (e) { errors.push(e.name); }
            try { await store("f", () => 1); } catch (e) { errors.push(e.name); }
            return errors;
            """);

        Assert.IsTrue(result.Success, Text(result));
        using var value = ValueOf(result);
        var errors = value.RootElement.EnumerateArray().Select(static item => item.GetString()!).ToArray();
        StringAssert.StartsWith(errors[0], "codemode.storeRefused: store(\"big\", value): the value takes 71 KB of JSON, more than the 64 KB a value may take.");
        StringAssert.StartsWith(errors[1], "codemode.storeRefused: store(\"part-16\", value): the values of this session would take");
        StringAssert.Contains(errors[1], "more than the 1 MB they may take. Remove some with store(key, undefined).");
        CollectionAssert.AreEqual(new[] { "TypeError", "TypeError" }, errors[2..]);
    }

    [TestMethod]
    public async Task AReplayedStep_DoesNotStoreAgain_AndWithoutAStoreTheValuesLastAsLongAsTheRun()
    {
        using var root = TestTempDirectory.Create();
        var values = new CodeModeValueStore(Path.Combine(root.Path, "values"));
        await using var runs = new CodeModeRunRegistry(null);
        await runs.UseStoreAsync(new CodeModeRunStore(Path.Combine(root.Path, "runs")));
        var exec = Exec(root.Path, runs, values);
        const string Code = """
            // @exec: {"title": "Counts"}
            await step("count", async () => { await store("n", ((await load("n")) ?? 0) + 1); return 1; });
            return await load("n");
            """;

        var ran = await InvokeAsync(exec, Code);
        var runId = runs.List("session-1").Single().Id;
        await runs.WhenKeptAsync(runId);
        var rerun = await InvokeAsync(exec, $$"""// @exec: {"resume": "{{runId}}", "replay": "all"}""");
        await using var cli = new CodeModeRunRegistry(null);
        var inRun = await InvokeAsync(Exec(root.Path, cli), """await store("k", 1); return await load("k");""");
        var nextRun = await InvokeAsync(Exec(root.Path, cli), """return [await load("k")];""");

        using (var value = ValueOf(ran)) Assert.AreEqual("1", value.RootElement.GetRawText());
        Assert.IsTrue(rerun.Success, Text(rerun));
        using (var value = ValueOf(rerun)) Assert.AreEqual("1", value.RootElement.GetRawText(), "the replayed step did not store again");
        using (var value = ValueOf(inRun)) Assert.AreEqual("1", value.RootElement.GetRawText());
        using (var value = ValueOf(nextRun)) Assert.AreEqual("[null]", value.RootElement.GetRawText(), "without a store, a value lasts as long as its run");
    }

    private static AgentToolDefinition Exec(string root, CodeModeRunRegistry runs, CodeModeValueStore? values = null)
    {
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(new CatalogOptions { GlobalRoot = root }).Add(registry));
        return CodeModeToolFactory.Create(dispatcher, new AltaSessionToolOptions { CallerKind = "cli", WorkingDirectory = root },
            new CodeModeToolOptions { Runs = runs, Cache = new CodeModeScriptCache(), Values = values }).Single(static tool => tool.Spec.Name == "exec");
    }

    private static Task<AgentToolResult> InvokeAsync(AgentToolDefinition tool, string code, string sessionId = "session-1")
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteEndObject();
        }

        return tool.Handler(new AgentToolInvocation(new ModelProviderId("provider"), sessionId, "call-1", tool.Spec.Name, JsonDocument.Parse(stream.ToArray()).RootElement.Clone()),
            CancellationToken.None);
    }

    private static string Text(AgentToolResult result) => string.Join('\n', result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value));

    private static JsonDocument ValueOf(AgentToolResult result)
    {
        var lines = Text(result).Split('\n');
        var index = Array.IndexOf(lines, "Result:");
        Assert.IsGreaterThanOrEqualTo(0, index, Text(result));
        return JsonDocument.Parse(lines[index + 1]);
    }
}

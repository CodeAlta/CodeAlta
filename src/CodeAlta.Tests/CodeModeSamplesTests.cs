using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog;
using CodeAlta.CodeMode;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Tests;

/// <summary>The samples CodeAlta ships in <c>content/scripts</c>: they parse, declare what they take, and run their flow over fakes.</summary>
[TestClass]
public sealed class CodeModeSamplesTests
{
    private static readonly string[] Samples = ["compare-models", "fix-failing-tests", "research", "review-pr", "triage-issues"];

    [TestMethod]
    public void TheSamples_AreShippedBesideTheExecutable_AndInTheLibraryAsSamples()
    {
        using var root = TestTempDirectory.Create();
        var library = new CodeModeScriptLibrary(new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") }, CodeModeScriptLibrary.ShippedRoot);

        Assert.IsNotNull(CodeModeScriptLibrary.ShippedRoot, "content/scripts is copied beside the executable");
        var listed = library.List(null);
        CollectionAssert.AreEqual(Samples, listed.Select(static script => script.Name).ToArray());
        Assert.IsTrue(listed.All(static script => script.Scope == CodeModeScriptScope.BuiltIn));
    }

    [TestMethod]
    public async Task EachSample_Parses_HasAFullMeta_AndPassesAltaScriptCheck()
    {
        using var root = TestTempDirectory.Create();
        var library = new CodeModeScriptLibrary(new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") }, CodeModeScriptLibrary.ShippedRoot);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection()
            .Add(new CatalogOptions { GlobalRoot = Path.Combine(root.Path, "home") }).Add(registry).Add(library));

        foreach (var name in Samples)
        {
            var script = library.Find(name, null)!;
            Assert.IsNull(script.Problem, $"{name}: {script.Problem}");
            var meta = script.Source.Descriptor;
            Assert.IsNotNull(meta, $"{name} has a meta");
            Assert.AreEqual(name, meta.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(meta.Title), name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(meta.Description), name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(meta.WhenToUse), name);
            Assert.IsNotNull(script.ParametersSchemaJson, $"{name} declares its params");
            Assert.IsGreaterThanOrEqualTo(2, script.Steps.Count, name);
            Assert.IsTrue(script.Steps.Any(static step => step.Each), $"{name} fans out in a step");
            var ids = script.Steps.Select(static step => step.Id).ToHashSet(StringComparer.Ordinal);
            Assert.IsTrue(script.Steps.All(step => step.After is null || step.After.All(ids.Contains)), $"{name}: a step runs after a step it declares");
            // Every step the code runs is declared, and every declared step runs.
            foreach (var id in ids) StringAssert.Contains(script.Source.Original, $"step(\"{id}\"", $"{name} runs its step {id}");

            var checkedScript = await dispatcher.InvokeAsync(["script", "check", script.Path]);
            Assert.AreEqual(AltaExitCodes.Success, checkedScript.ExitCode, $"{name}: {checkedScript.Transcript}");
            StringAssert.Contains(checkedScript.Stdout, "\"valid\":true", name);
        }

        Assert.IsTrue(library.Find("review-pr", null)!.Source.Descriptor!.Tool, "review-pr is offered as a tool");
        Assert.IsNull(library.Find("review-pr", null)!.CheckArguments("""{"pr":12}"""), "the defaults make pr the only argument needed");
    }

    [TestMethod]
    public async Task ReviewPr_ReadsThePullRequest_ReviewsEachLensInParallel_AndMergesTheFindings()
    {
        var fakes = new Fakes();
        fakes.Alta["alta.issue.show"] = arguments => new JsonObject
        {
            ["type"] = "alta.issue", ["kind"] = "pull_request", ["id"] = arguments["id"]!.GetValue<string>(), ["title"] = "Fix the parser",
            ["url"] = "https://example.com/pr/12", ["sourceBranch"] = "fix/parser", ["targetBranch"] = "main", ["description"] = "Fixes quoted keys.",
        };

        var result = await RunAsync("review-pr", """{"pr":12,"lenses":["correctness","security"]}""", fakes);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("\"findings: Review #12\"", result.ValueJson);
        var show = fakes.Calls.Single(static call => call.Path == "alta.issue.show");
        Assert.AreEqual("""{"id":"12","kind":"pr"}""", show.ArgumentsJson);
        var reviews = fakes.Agents.Where(static request => request.Step == "review").ToArray();
        CollectionAssert.AreEquivalent(new[] { "Review #12: correctness", "Review #12: security" }, reviews.Select(static request => request.Title).ToArray());
        Assert.IsTrue(reviews.All(static request => request.Tier == "standard" && request.Prompt.Contains("It merges fix/parser into main.", StringComparison.Ordinal)
            && request.Prompt.Contains("Fixes quoted keys.", StringComparison.Ordinal)));
        Assert.AreEqual(2, fakes.MostAtOnce, "the lenses are reviewed at the same time");
        var merge = fakes.Agents.Single(static request => request.Step == "merge");
        StringAssert.Contains(merge.Prompt, "## correctness\nfindings: Review #12: correctness");
        StringAssert.Contains(merge.Prompt, "## security\nfindings: Review #12: security");
    }

    [TestMethod]
    public async Task ReviewPr_FromAnAutomationOnPullRequests_ReadsTheNumberOfThePullRequestFromItsEvent()
    {
        var fakes = new Fakes();
        fakes.Alta["alta.issue.show"] = arguments => new JsonObject { ["type"] = "alta.issue", ["id"] = arguments["id"]!.GetValue<string>(), ["title"] = "T", ["url"] = "u" };
        // What an automation gives a script that declares `event`: the text that follows its prompt.
        const string Event = "This automation was started by the GitHub repository a/b: pull request #31 was opened.\nTitle: Fixes #7";

        var started = await RunAsync("review-pr", new JsonObject { ["event"] = Event }.ToJsonString(), fakes);
        var without = await RunAsync("review-pr", "{}", new Fakes());

        Assert.AreEqual(CodeModeRunStatus.Completed, started.Status, started.Error?.Message);
        Assert.AreEqual("""{"id":"31","kind":"pr"}""", fakes.Calls.Single(static call => call.Path == "alta.issue.show").ArgumentsJson);
        Assert.AreEqual(CodeModeRunStatus.Failed, without.Status);
        StringAssert.Contains(without.Error?.Message, "review-pr needs the number of a pull request");
    }

    [TestMethod]
    public async Task Research_SplitsTheQuestion_ResearchesEachPart_AndWritesOneAnswer_EvenWhenAPartFails()
    {
        var fakes = new Fakes { FailingTitle = "Research: Where is it tested?" };
        fakes.Values["Split the question"] = new JsonArray("Where is it parsed?", "Where is it tested?");

        var result = await RunAsync("research", """{"question":"How are TOML keys parsed?","angles":2}""", fakes);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        Assert.AreEqual("\"findings: Write the answer\"", result.ValueJson);
        var plan = fakes.Agents.Single(static request => request.Step == "plan");
        Assert.AreEqual("quick", plan.Tier);
        StringAssert.Contains(plan.Schema, "\"maxItems\":2");
        CollectionAssert.AreEquivalent(new[] { "Research: Where is it parsed?", "Research: Where is it tested?" },
            fakes.Agents.Where(static request => request.Step == "explore").Select(static request => request.Title).ToArray());
        var answer = fakes.Agents.Single(static request => request.Step == "synthesize");
        StringAssert.Contains(answer.Prompt, "## Where is it parsed?\nfindings: Research: Where is it parsed?");
        StringAssert.Contains(answer.Prompt, "## Where is it tested?\n(this research failed)", "a part that failed is said, not dropped");
    }

    [TestMethod]
    public async Task TriageIssues_ClassifiesEachOpenIssue_AndReportsThemByPriority()
    {
        var fakes = new Fakes();
        fakes.Alta["alta.issue.list"] = _ => new JsonArray(
            new JsonObject { ["type"] = "alta.issue", ["id"] = "1", ["title"] = "Typo", ["url"] = "u1" },
            new JsonObject { ["type"] = "alta.issue", ["id"] = "2", ["title"] = "Crash", ["url"] = "u2", ["labels"] = new JsonArray("bug") },
            new JsonObject { ["type"] = "alta.issue.summary", ["count"] = 2 });
        fakes.Values["Triage #1"] = new JsonObject { ["kind"] = "docs", ["priority"] = "low", ["summary"] = "A typo." };
        fakes.Values["Triage #2"] = new JsonObject { ["kind"] = "bug", ["priority"] = "high", ["summary"] = "It crashes." };

        var result = await RunAsync("triage-issues", "{}", fakes);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        var report = JsonNode.Parse(result.ValueJson!)!.GetValue<string>().Split('\n');
        CollectionAssert.AreEqual(new[] { "| [#2](u2) | bug | high | It crashes. |", "| [#1](u1) | docs | low | A typo. |" }, report[2..]);
    }

    [TestMethod]
    public async Task CompareModels_AsksEachModel_ThenTheJudge()
    {
        var fakes = new Fakes();

        var result = await RunAsync("compare-models", """{"task":"Sum 1 to 10.","models":["a:one","b:two"],"judge":"c:three"}""", fakes);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        CollectionAssert.AreEquivalent(new[] { "a:one", "b:two" }, fakes.Agents.Where(static request => request.Step == "answer").Select(static request => request.Model).ToArray());
        Assert.AreEqual("c:three", fakes.Agents.Single(static request => request.Step == "judge").Model);
    }

    [TestMethod]
    public async Task FixFailingTests_GivesEachFailingTestToASession_OneAtATimeByDefault()
    {
        var fakes = new Fakes();
        fakes.Values["Find the failing tests"] = new JsonArray("A.One", "A.Two");

        var result = await RunAsync("fix-failing-tests", "{}", fakes);

        Assert.AreEqual(CodeModeRunStatus.Completed, result.Status, result.Error?.Message);
        CollectionAssert.AreEqual(new[] { "Fix A.One", "Fix A.Two" }, fakes.Agents.Where(static request => request.Step == "fix").Select(static request => request.Title).ToArray());
        Assert.AreEqual(1, fakes.MostAtOnce, "the sessions share the folder of the project: one at a time by default");
        StringAssert.Contains(result.ValueJson, "2 of 2 failing tests");
    }

    private static Task<CodeModeRunResult> RunAsync(string name, string argumentsJson, Fakes fakes)
    {
        var source = CodeModeSource.Prepare(File.ReadAllText(Path.Combine(CodeModeScriptLibrary.ShippedRoot!, name + ".js")), name + ".js");
        var api = new CodeModeApi([new CodeModeFunction("codemode.agent"), new CodeModeFunction("alta.issue.show"), new CodeModeFunction("alta.issue.list")]);
        return new CodeModeHost(api, new CodeModeScriptCache()).RunAsync(source, fakes, new CodeModeRunOptions { ArgumentsJson = argumentsJson });
    }

    // The sessions and the commands of the samples: a session answers "findings: <its title>", or the value its title is given.
    private sealed class Fakes : ICodeModeCallHandler
    {
        private int _running;

        public ConcurrentQueue<CodeModeCall> Calls { get; } = new();

        public ConcurrentQueue<(string? Step, string Prompt, string? Title, string? Tier, string? Model, string? Schema)> Agents { get; } = new();

        public Dictionary<string, Func<JsonObject, JsonNode>> Alta { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, JsonNode> Values { get; } = new(StringComparer.Ordinal);

        public string? FailingTitle { get; init; }

        public int MostAtOnce { get; private set; }

        public async ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken)
        {
            Calls.Enqueue(call);
            var arguments = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
            if (Alta.TryGetValue(call.Path, out var command)) return CodeModeCallResult.Success(command(arguments).ToJsonString());
            if (call.Path != "codemode.agent") return CodeModeCallResult.Failure(new CodeModeCallError("ToolError", "no fake " + call.Path));
            var title = arguments["title"]?.GetValue<string>();
            Agents.Enqueue((call.StepId, arguments["prompt"]!.GetValue<string>(), title, arguments["tier"]?.GetValue<string>(), arguments["model"]?.GetValue<string>(),
                arguments["schema"]?.ToJsonString()));
            lock (Agents) MostAtOnce = Math.Max(MostAtOnce, ++_running);
            // The sessions of a step overlap when the script lets them.
            await Task.Delay(100, cancellationToken);
            lock (Agents) _running--;
            if (title == FailingTitle) return CodeModeCallResult.Failure(new CodeModeCallError("AgentError", "the session failed"));
            var reply = title is not null && Values.TryGetValue(title, out var value)
                ? new JsonObject { ["value"] = value.DeepClone() }
                : new JsonObject { ["answer"] = arguments["title"] is null ? "findings" : "findings: " + title };
            return CodeModeCallResult.Success(reply.ToJsonString());
        }
    }
}

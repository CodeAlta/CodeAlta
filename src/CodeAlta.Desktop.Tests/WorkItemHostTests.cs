using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Catalog.WorkItems;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Desktop.WorkItems;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The tasks and the plans as the application has them: the `alta task` and `alta plan` commands of a session,
/// and the service the page lists, reads and decides them through, over the same files.
/// </summary>
[TestClass]
public sealed class WorkItemHostTests
{
    private const string Epoch = "8f2f3b3c-0f6e-4d0b-9c1e-2f1d5f6f7a10";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task ASession_ProposesATask_AndThePageShowsItToThatSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        var notices = 0;
        fixture.Items.Changed += () => notices++;

        var created = await fixture.One("alta.task.created", ["task", "create", "--title", "Report why a probe failed", "--kind", "gap", "--summary", "The page says nothing.", "--stdin"],
            "## Why\nNothing is shown.\n");

        var id = Text(created, "id")!;
        Assert.AreEqual(("pending", "gap", fixture.Project.Id), (Text(created, "status"), Text(created, "kind"), Text(created, "projectId")));
        Assert.AreEqual(".alta/tasks/" + id + ".md", Text(created, "file"));
        StringAssert.Contains(Text(created, "nextStep"), "Do not start it yourself");
        Assert.AreEqual(1, notices, "The page is told at once.");

        var listed = await fixture.Rpc.ListAsync(new(Epoch), default);
        var project = listed.Projects.Single();
        Assert.AreEqual(("ok", fixture.Project.Id), (listed.Status, project.ProjectId));
        var row = project.Tasks.Single();
        Assert.AreEqual((id, "task", "Report why a probe failed", "The page says nothing.", "gap", "pending", "session-of-the-user"),
            (row.Id, row.Kind, row.Title, row.Summary, row.Category, row.Status, row.ProposedBy));
        Assert.IsFalse(row.Acknowledged);
        Assert.AreEqual(("delete", "worktree", true), (listed.Settings!.CompletedTasks, listed.Settings.Start, listed.Settings.Propose));

        var text = await fixture.Rpc.ReadAsync(new(Epoch, fixture.Project.Id, "task", id), default);
        Assert.AreEqual(("ok", "## Why\nNothing is shown.\n", false), (text.Status, text.Markdown, text.Truncated));

        // The other project has nothing, and a request for it says so without reading this one.
        Assert.IsEmpty((await fixture.Rpc.ListAsync(new(Epoch, [fixture.Other.Id]), default)).Projects);
        Assert.HasCount(1, (await fixture.Rpc.ListAsync(new(Epoch, [fixture.Other.Id, fixture.Project.Id, "unknown"]), default)).Projects);
    }

    [TestMethod]
    public async Task TaskCommands_ListStartCompleteAndSetAside()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = Text(await fixture.One("alta.task.created", ["task", "create", "--title", "First", "--content", "Body one."]), "id")!;
        var second = Text(await fixture.One("alta.task.created", ["task", "create", "--title", "Second", "--kind", "problem", "--content", "Body two."]), "id")!;

        var (_, listed, _) = await fixture.Run(["task", "list"]);
        CollectionAssert.AreEquivalent(new[] { first, second }, Of(listed, "alta.task").Select(static record => Text(record, "id")).ToArray());
        Assert.AreEqual("2", Text(Of(listed, "alta.task.summary").Single(), "count"));

        var shown = await fixture.One("alta.task", ["task", "show", first + ".md"]);
        Assert.AreEqual("Body one.", Text(shown, "markdown"));

        Assert.AreEqual("in-progress", Text(await fixture.One("alta.task.changed", ["task", "start", first]), "status"));
        Assert.AreEqual("session-of-the-user", fixture.Items.GetLink(fixture.Project.Id, "task", first)!.Runner);
        Assert.AreEqual("in-progress", Text(await fixture.One("alta.task", ["task", "show", first]), "status"));

        Assert.AreEqual("later", Text(await fixture.One("alta.task.changed", ["task", "later", second]), "status"));
        var (_, later, _) = await fixture.Run(["task", "list", "--status", "later"]);
        Assert.AreEqual(second, Text(Of(later, "alta.task").Single(), "id"));
        Assert.AreEqual("pending", Text(await fixture.One("alta.task.changed", ["task", "reopen", second]), "status"));

        var completed = await fixture.One("alta.task.changed", ["task", "complete", first]);
        Assert.AreEqual(("done", "False"), (Text(completed, "status"), Text(completed, "kept")));
        Assert.IsNull(fixture.Items.GetTask(fixture.Project, first));
        Assert.AreEqual("removed", Text(await fixture.One("alta.task.changed", ["task", "dismiss", second]), "status"));

        // What is wrong is said, with what to do.
        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.Run(["task", "complete", first])).Code);
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["task", "create", "--title", "No description"])).Code);
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["task", "create", "--title", "Wrong kind", "--kind", "feature", "--content", "Body."])).Code);
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["task", "list", "--status", "someday"])).Code);
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["task", "start", "x"], caller: new AltaCallerIdentity { Kind = "mcp", SourceProjectId = fixture.Project.Id })).Code);
        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.Run(["task", "list", "--project", "no-such-project"])).Code);
    }

    [TestMethod]
    public async Task ASession_IsToldWhenItMayNotPropose()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var index = 0; index < WorkItemService.MaximumProposals; index++) await fixture.One("alta.task.created", ["task", "create", "--title", "Task " + index, "--content", "Body."]);

        var (code, _, text) = await fixture.Run(["task", "create", "--title", "One too many", "--content", "Body."]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, code);
        StringAssert.Contains(text, "task.tooManyProposals");

        fixture.Items.SaveSettings(WorkItemSettings.Default with { Propose = false });
        var (denied, _, why) = await fixture.Run(["task", "create", "--title", "Not now", "--content", "Body."], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "another-session", SourceProjectId = fixture.Project.Id });
        Assert.AreEqual(AltaExitCodes.PolicyDenied, denied);
        StringAssert.Contains(why, "task.proposalsDisabled");
        StringAssert.Contains(why, "mention the finding in your answer");
    }

    [TestMethod]
    public async Task PlanCommands_ListReadAndApprove_AndTheApprovedPlanIsShownToItsSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        var plans = Directory.CreateDirectory(Path.Combine(fixture.Project.ProjectPath, ".alta", "plans")).FullName;
        File.WriteAllText(Path.Combine(plans, "2026-06-03-old.md"), "# Old plan\n\n- Status: Completed\n- Created: 2026-06-03\n");
        File.WriteAllText(Path.Combine(plans, "2026-10-07-new.md"), "---\ntitle: New plan\nstatus: draft\ncreated: 2026-10-07\nsummary: What it does.\n---\n\n# New plan\n\nText.\n");

        var (_, open, _) = await fixture.Run(["plan", "list"]);
        Assert.AreEqual("2026-10-07-new", Text(Of(open, "alta.plan").Single(), "id"), "A plan that is done is not one to list by default.");
        var (_, all, _) = await fixture.Run(["plan", "list", "--status", "all"]);
        Assert.HasCount(2, Of(all, "alta.plan"));

        var shown = await fixture.One("alta.plan", ["plan", "show", "2026-10-07-new"]);
        Assert.AreEqual(("New plan", "draft", "# New plan\n\nText.\n"), (Text(shown, "title"), Text(shown, "status"), Text(shown, "markdown")));

        var approved = await fixture.One("alta.plan.changed", ["plan", "status", "2026-10-07-new", "approved"]);
        Assert.AreEqual("approved", Text(approved, "status"));
        StringAssert.Contains(Text(approved, "nextStep"), "Do not start the work");
        StringAssert.Contains(File.ReadAllText(Path.Combine(plans, "2026-10-07-new.md")), "status: approved");

        var row = (await fixture.Rpc.ListAsync(new(Epoch), default)).Projects.Single().Plans.First();
        Assert.AreEqual(("2026-10-07-new", "plan", "approved", "session-of-the-user"), (row.Id, row.Kind, row.Status, row.ProposedBy));
        var old = (await fixture.Rpc.ListAsync(new(Epoch), default)).Projects.Single().Plans.Last();
        Assert.AreEqual(("Old plan", "done", (string?)null), (old.Title, old.Status, old.StatusText));

        await fixture.One("alta.plan.changed", ["plan", "status", "2026-10-07-new", "in-progress"]);
        Assert.AreEqual("session-of-the-user", fixture.Items.GetLink(fixture.Project.Id, "plan", "2026-10-07-new")!.Runner);
        await fixture.One("alta.plan.changed", ["plan", "status", "2026-10-07-new", "done"]);
        Assert.IsNull(fixture.Items.GetLink(fixture.Project.Id, "plan", "2026-10-07-new")!.Runner);

        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["plan", "status", "2026-10-07-new", "finished"])).Code);
        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.Run(["plan", "show", "missing"])).Code);
        await fixture.One("alta.plan.removed", ["plan", "remove", "2026-06-03-old"]);
        Assert.IsFalse(File.Exists(Path.Combine(plans, "2026-06-03-old.md")));

        // A user who does not keep completed plans: the plan that is done goes, and the agent is told.
        fixture.Items.SaveSettings(WorkItemSettings.Default with { CompletedPlans = WorkItemClosing.Delete });
        Assert.IsTrue(fixture.Items.SetPlanStatus(fixture.Project, "2026-10-07-new", WorkPlanStatus.InProgress));
        var gone = await fixture.One("alta.plan.removed", ["plan", "status", "2026-10-07-new", "done"]);
        Assert.AreEqual("done", Text(gone, "status"));
        StringAssert.Contains(Text(gone, "message"), "Its file was deleted");
        Assert.IsFalse(File.Exists(Path.Combine(plans, "2026-10-07-new.md")));
    }

    [TestMethod]
    public async Task ThePage_DecidesAnItem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Text(await fixture.One("alta.task.created", ["task", "create", "--title", "A task", "--content", "Body."]), "id")!;
        Task<WorkItemActionResponse> Act(string action, string? value = null, string? session = null, string kind = "task", string? item = null)
            => fixture.Rpc.ActAsync(new(Epoch, fixture.Project.Id, kind, item ?? id, action, value, session, fixture.Project.ProjectPath), default);

        Assert.AreEqual("ok", (await Act("acknowledge")).Status);
        Assert.IsTrue((await fixture.Rpc.ListAsync(new(Epoch), default)).Projects.Single().Tasks.Single().Acknowledged);
        Assert.AreEqual("ok", (await Act("later")).Status);
        Assert.AreEqual("later", (await fixture.Rpc.ListAsync(new(Epoch), default)).Projects.Single().Tasks.Single().Status);
        Assert.AreEqual("ok", (await Act("reopen")).Status);

        // Started in the session that shows it: the page gets the prompt and sends it as the user would.
        var here = await Act("start_here", session: "session-of-the-user");
        Assert.AreEqual(("ok", "session-of-the-user", (string?)null), (here.Status, here.SessionId, here.AgentPromptId));
        StringAssert.Contains(here.Prompt, "# A task\n\nBody.");
        Assert.AreEqual("session-of-the-user", fixture.Items.GetLink(fixture.Project.Id, "task", id)!.Runner);
        Assert.AreEqual("ok", (await Act("release")).Status);
        Assert.IsNull(fixture.Items.GetLink(fixture.Project.Id, "task", id)!.Runner);
        Assert.AreEqual("invalid_request", (await Act("start_here")).Status, "Without a session there is nowhere to send the prompt.");

        // Started in a new session: the host creates it, with the model of the session that showed the item.
        var started = await Act("start_worktree", session: "session-of-the-user");
        Assert.AreEqual(("ok", "new-session"), (started.Status, started.SessionId));
        Assert.AreEqual((fixture.Project.Id, "task", id, true, "session-of-the-user"), fixture.Runner.Starts.Single());
        fixture.Runner.Problem = ("The worktree could not be created.", "worktree_not_repository");
        var refused = await Act("start_session");
        Assert.AreEqual(("refused", "The worktree could not be created.", "worktree_not_repository"), (refused.Status, refused.Message, refused.Reason));

        Assert.AreEqual("ok", (await Act("dismiss")).Status);
        Assert.AreEqual("not_found", (await Act("later")).Status);

        // A plan is carried out by the default agent, whatever its session was planning with.
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(fixture.Project.ProjectPath, ".alta", "plans")).FullName, "plan.md"), "---\nstatus: approved\n---\n# Plan\n");
        var plan = await Act("start_here", session: "session-of-the-user", kind: "plan", item: "plan");
        Assert.AreEqual(("ok", "default"), (plan.Status, plan.AgentPromptId));
        StringAssert.StartsWith(plan.Prompt, "Execute the approved plan at `.alta/plans/plan.md`.");
        Assert.AreEqual("ok", (await Act("status", "done", kind: "plan", item: "plan")).Status);
        Assert.AreEqual("invalid_request", (await Act("status", "finished", kind: "plan", item: "plan")).Status);
        Assert.AreEqual("invalid_request", (await Act("later", kind: "plan", item: "plan")).Status, "Later is the status of a task.");
        Assert.AreEqual("ok", (await Act("remove", kind: "plan", item: "plan")).Status);

        Assert.AreEqual("invalid_request", (await Act("later", item: "../escape")).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.ActAsync(new("another", fixture.Project.Id, "task", id, "later"), default)).Status);
        Assert.AreEqual("unavailable", (await new WorkItemsService().ListAsync(new(Epoch), default)).Status);
    }

    [TestMethod]
    public async Task ThePage_SavesTheSettings_AndIsToldOfEveryChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var stop = new CancellationTokenSource();
        var events = fixture.Rpc.WatchAsync(new(Epoch), stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.AreEqual(0, events.Current.Revision);

        var saved = await fixture.Rpc.SaveSettingsAsync(new(Epoch, new WorkItemsSettings(false, false, "keep", "delete", "delete", "here")), default);

        Assert.AreEqual(("ok", new WorkItemsSettings(false, false, "keep", "delete", "delete", "here")), (saved.Status, saved.Settings));
        Assert.AreEqual(new WorkItemSettings(false, false, WorkItemClosing.Keep, WorkItemClosing.Delete, WorkItemClosing.Delete, WorkItemStart.Here), fixture.Items.Settings);
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.AreEqual(1, events.Current.Revision);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.SaveSettingsAsync(new(Epoch, new WorkItemsSettings(true, true, "archive", "delete", "keep", "here")), default)).Status);

        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await events.MoveNextAsync());
        StringAssert.Contains(JsonSerializer.Serialize(saved, DesktopJsonContext.Default.WorkItemsSettingsResponse), "\"start\":\"here\"");
    }

    private static string? Text(JsonElement record, string name) => record.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static JsonElement[] Of(List<JsonElement> records, string type) => [.. records.Where(record => Text(record, "type") == type)];

    private sealed class FakeRunner : IWorkItemRunner
    {
        internal List<(string ProjectId, string Kind, string Id, bool Worktree, string? Like)> Starts { get; } = [];

        internal (string Message, string Reason)? Problem { get; set; }

        public Task<WorkItemStartResult> StartAsync(ProjectDescriptor project, string kind, string id, bool worktree, string? likeSessionId)
        {
            if (Problem is { } problem) return Task.FromResult(new WorkItemStartResult(null, problem.Message, problem.Reason));
            Starts.Add((project.Id, kind, id, worktree, likeSessionId));
            return Task.FromResult(new WorkItemStartResult("new-session", null));
        }
    }

    // The work items of an application over a real project catalog: the page's service and the alta commands on the same files.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, CatalogOptions options, ProjectCatalog projects, ProjectDescriptor project, ProjectDescriptor other)
        {
            (_root, Project, Other) = (root, project, other);
            Items = new WorkItemService(new CodeAltaConfigStore(options), Path.Combine(root, "state"));
            Rpc = new WorkItemsService(Items, projects, Runner, Epoch);
            var services = new AltaServiceCollection().Add(projects).Add(Items);
            var registry = new AltaCommandRegistry();
            Alta = new AltaCommandDispatcher(registry, services);
            services.Add(registry).Add(Alta);
            Session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-of-the-user", SourceProjectId = project.Id };
        }

        public ProjectDescriptor Project { get; }
        public ProjectDescriptor Other { get; }
        public WorkItemService Items { get; }
        public FakeRunner Runner { get; } = new();
        public WorkItemsService Rpc { get; }
        public AltaCommandDispatcher Alta { get; }
        public AltaCallerIdentity Session { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("codealta-work-item-host-").FullName;
            var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName };
            var projects = new ProjectCatalog(options);
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "other")).FullName);
            return new Fixture(root, options, projects, project, other);
        }

        public async Task<(int Code, List<JsonElement> Records, string Text)> Run(string[] arguments, string? stdin = null, AltaCallerIdentity? caller = null)
        {
            var result = await Alta.InvokeAsync(arguments, stdin, caller ?? Session, Project.ProjectPath).AsTask().WaitAsync(Patience);
            var records = new List<JsonElement>();
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }

            return (result.ExitCode, records, result.Stdout + result.Stderr);
        }

        public async Task<JsonElement> One(string type, string[] arguments, string? stdin = null)
        {
            var (code, records, text) = await Run(arguments, stdin);
            Assert.AreEqual(AltaExitCodes.Success, code, text);
            return records.Single(record => record.GetProperty("type").GetString() == type);
        }

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return ValueTask.CompletedTask;
        }
    }
}

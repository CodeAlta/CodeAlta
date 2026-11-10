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
    private static readonly TimeSpan Patience = TestTime.Seconds(30);

    [TestMethod]
    public async Task ASandboxedSession_ReachesTheTasksAndPlansOfItsOwnProjectAlone()
    {
        await using var fixture = await Fixture.CreateAsync(sandboxed: true);
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["task", "list"])).Code, "its own project");
        foreach (var arguments in new[] { new[] { "task", "list", "--project", fixture.Other.Id }, ["plan", "list", "--all"], ["plan", "show", "x", "--project", fixture.Other.Id] })
        {
            var (code, records, text) = await fixture.Run(arguments);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, code, text);
            Assert.AreEqual("sandbox.projectDenied", records.First(static record => record.GetProperty("type").GetString() == "alta.error").GetProperty("code").GetString(), text);
        }
    }

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
        // When its file was last written: the `#` picker of a prompt lists the most recent first.
        var written = File.GetLastWriteTimeUtc(Path.Combine(fixture.Project.ProjectPath, row.File));
        Assert.AreEqual(new DateTimeOffset(written, TimeSpan.Zero), row.Updated);
        Assert.AreEqual(("delete", "worktree", true), (listed.Settings!.CompletedTasks, listed.Settings.Start, listed.Settings.Propose));

        var text = await fixture.Rpc.ReadAsync(new(Epoch, fixture.Project.Id, "task", id), default);
        Assert.AreEqual(("ok", "## Why\nNothing is shown.\n", false), (text.Status, text.Markdown, text.Truncated));

        // The other project has nothing, and a request for it says so without reading this one.
        Assert.IsEmpty((await fixture.Rpc.ListAsync(new(Epoch, [fixture.Other.Id]), default)).Projects);
        Assert.HasCount(1, (await fixture.Rpc.ListAsync(new(Epoch, [fixture.Other.Id, fixture.Project.Id, "unknown"]), default)).Projects);
    }

    [TestMethod]
    public async Task Tags_AreTheUsers_AndAgentsApplyThemToTasks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (items, project) = (fixture.Items, fixture.Project);
        Assert.AreEqual("0", Text(await fixture.One("alta.tag.summary", ["tag", "list"]), "count"));
        var (refused, _, why) = await fixture.Run(["task", "create", "--title", "Untagged", "--tag", "mobile", "--content", "Body."]);
        Assert.AreEqual(AltaExitCodes.Usage, refused, "A task cannot have a tag the project does not have.");
        StringAssert.Contains(why, "Only the user creates tags");

        Assert.AreEqual(WorkTagChange.Done, items.SetTag(project, "mobile", "The phone app."));
        Assert.AreEqual(WorkTagChange.Done, items.SetTag(project, "CI", null));
        var tagged = await fixture.One("alta.task.created", ["task", "create", "--title", "Tagged", "--tag", "mobile", "--tag", "ci", "--content", "Body."]);
        var shortId = Text(tagged, "shortId")!;
        Assert.AreEqual("[\"mobile\",\"ci\"]", Text(tagged, "tags"));
        var plain = Text(await fixture.One("alta.task.created", ["task", "create", "--title", "Plain", "--content", "Body."]), "id")!;
        (refused, _, why) = await fixture.Run(["task", "create", "--title", "Unknown", "--tag", "desktop", "--content", "Body."]);
        Assert.AreEqual(AltaExitCodes.Usage, refused);
        StringAssert.Contains(why, "Its tags are: ci, mobile");

        var (_, listed, _) = await fixture.Run(["tag", "list"]);
        CollectionAssert.AreEqual(new[] { "mobile:The phone app.:1", "ci::1" },
            Of(listed, "alta.tag").Select(static tag => $"{Text(tag, "name")}:{Text(tag, "description")}:{Text(tag, "openTasks")}").ToArray());
        var (_, filtered, _) = await fixture.Run(["task", "list", "--tag", "#Mobile"]);
        Assert.AreEqual(shortId, Text(Of(filtered, "alta.task").Single(), "shortId"));

        Assert.AreEqual("[\"ci\",\"mobile\"]", Text(await fixture.One("alta.task.tagged", ["task", "tag", plain[^4..], "--add", "ci", "--add", "mobile", "--remove", "none"]), "tags"));
        Assert.AreEqual("[\"ci\"]", Text(await fixture.One("alta.task.tagged", ["task", "tag", plain[^4..], "--remove", "mobile"]), "tags"));
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["task", "tag", plain[^4..], "--add", "desktop"])).Code);

        // Renaming and deleting change the tags file and every task that has the tag.
        Assert.AreEqual(WorkTagChange.Exists, items.RenameTag(project, "mobile", "ci"));
        Assert.AreEqual(WorkTagChange.NotFound, items.RenameTag(project, "desktop", "web"));
        Assert.AreEqual(WorkTagChange.Done, items.RenameTag(project, "mobile", "phone"));
        Assert.AreEqual(WorkTagChange.Done, items.DeleteTag(project, "ci"));
        CollectionAssert.AreEqual(new[] { "phone" }, items.FindTasks(project, shortId).Single().Tags.ToArray());
        Assert.AreEqual(0, items.FindTasks(project, plain[^4..]).Single().Tags.Count);
        var file = File.ReadAllText(Path.Combine(project.ProjectPath, ".alta", "tags.yaml"));
        StringAssert.Contains(file, "  phone: \"The phone app.\"\n");
        Assert.IsFalse(file.Contains("ci:", StringComparison.Ordinal), file);

        // A tag written on a task by hand is a tag of the project, not listed in its file until the user describes it.
        Assert.IsTrue(items.SetTaskTags(project, plain, ["handmade"]));
        Assert.IsFalse(items.ListTags(project).Tags.Single(tag => tag.Name == "handmade").Listed);
        Assert.AreEqual(WorkTagChange.Done, items.SetTag(project, "handmade", "Written by hand."));
        Assert.IsTrue(items.ListTags(project).Tags.Single(tag => tag.Name == "handmade").Listed);

        Assert.AreEqual(WorkTagChange.Done, items.SetTagDeclined(project, "ci-noise", declined: true));
        Assert.AreEqual("[\"ci-noise\"]", Text(await fixture.One("alta.tag.summary", ["tag", "list"]), "declined"));
        Assert.AreEqual(WorkTagChange.Done, items.SetTag(project, "ci-noise", null));
        Assert.AreEqual(0, items.ListTags(project).Declined.Count, "A declined name the user creates is no longer declined.");

        // A tags file that cannot be read is not written over.
        File.WriteAllText(Path.Combine(project.ProjectPath, ".alta", "tags.yaml"), "tags: [unclosed\n  - : :");
        Assert.AreEqual(WorkTagChange.Failed, items.SetTag(project, "new", null));
        Assert.AreEqual("tags: [unclosed\n  - : :", File.ReadAllText(Path.Combine(project.ProjectPath, ".alta", "tags.yaml")));
    }

    [TestMethod]
    public async Task TagProposals_WaitForTheUser_WhoAppliesOrDismissesThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (items, project) = (fixture.Items, fixture.Project);
        var ids = new List<string>();
        foreach (var title in new[] { "One", "Two", "Three", "Four" })
        {
            ids.Add(Text(await fixture.One("alta.task.created", ["task", "create", "--title", title, "--content", "Body."]), "id")!);
        }

        var shortIds = ids.Select(static id => id[^4..]).ToArray();
        Assert.AreEqual(WorkTagChange.Done, items.SetTag(project, "mobile", "The phone app."));
        Assert.IsTrue(items.SetTaskTags(project, ids[3], ["mobile"]));

        // A session found the tasks a tag fits: the ones that have it already are left out, and nothing is tagged yet.
        var found = await fixture.One("alta.tag.matches", ["tag", "matches", "mobile", "--stdin"], $"{shortIds[0]} the phone app\n- {shortIds[1]}: its server\n{shortIds[3]} already");
        Assert.AreEqual($"[\"{ids[0]}\",\"{ids[1]}\"]", Text(found, "tasks"));
        Assert.AreEqual(0, items.GetTask(project, ids[0])!.Tags.Count);
        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.Run(["tag", "matches", "desktop", "--task", shortIds[0]])).Code, "only an existing tag");
        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.Run(["tag", "matches", "mobile", "--task", "zzzz"])).Code, "only open tasks");
        // Every task named has the tag already: nothing is given to the user, and the proposal that waits stays as it is.
        var (allTagged, _, allTaggedWhy) = await fixture.Run(["tag", "matches", "mobile", "--task", shortIds[3]]);
        Assert.AreEqual(AltaExitCodes.Usage, allTagged);
        StringAssert.Contains(allTaggedWhy, "already has the tag 'mobile'");
        CollectionAssert.AreEqual(new[] { ids[0], ids[1] }, items.ListTagProposals(project.Id).Single().Tasks.Select(static match => match.TaskId).ToArray());

        // The page lists the proposal and applies it to the tasks the user kept.
        var listed = (await fixture.Rpc.ListAsync(new(Epoch, [project.Id]), default)).Projects.Single().TagProposals!.Single();
        Assert.AreEqual(("mobile", false, "the phone app"), (listed.Name, listed.NewTag, listed.Tasks[0].Reason));
        Assert.AreEqual("ok", (await fixture.Rpc.TagAsync(new(Epoch, project.Id, "apply", "mobile", null, [ids[1]]), default)).Status);
        CollectionAssert.AreEqual(new[] { "mobile" }, items.GetTask(project, ids[1])!.Tags.ToArray());
        Assert.AreEqual(0, items.GetTask(project, ids[0])!.Tags.Count, "a task the user unticked is not tagged");
        Assert.AreEqual(0, items.ListTagProposals(project.Id).Count);

        // A new tag needs three open tasks, and is not a tag before the user creates it.
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["tag", "propose", "--name", "parser", "--description", "The parser.", "--task", shortIds[0], "--task", shortIds[1]])).Code);
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["tag", "propose", "--name", "mobile", "--description", "Again.", "--task", shortIds[0]])).Code, "an existing tag is given, not proposed");
        var proposed = await fixture.One("alta.tag.proposed", ["tag", "propose", "--name", "parser", "--description", "The parser of the files.", "--task", shortIds[0], "--task", shortIds[1], "--task", shortIds[2]]);
        Assert.AreEqual("parser", Text(proposed, "name"));
        Assert.IsTrue(items.ListTags(project).Tags.All(static tag => tag.Name != "parser"));
        Assert.AreEqual(AltaExitCodes.PolicyDenied, (await fixture.Run(["tag", "propose", "--name", "lexer", "--description", "The lexer.", "--task", shortIds[0], "--task", shortIds[1], "--task", shortIds[2]])).Code,
            "one new tag of a session waits at a time");

        // Applied: the tag is created with the description the user left, and given to the tasks kept.
        Assert.AreEqual("ok", (await fixture.Rpc.TagAsync(new(Epoch, project.Id, "apply", "parser", "Parsing files.", [ids[0], ids[2]]), default)).Status);
        Assert.AreEqual("Parsing files.", items.ListTags(project).Tags.Single(static tag => tag.Name == "parser").Description);
        CollectionAssert.AreEqual(new[] { "parser" }, items.GetTask(project, ids[2])!.Tags.ToArray());

        // Dismissed: a new tag is declined, and no agent proposes it again.
        await fixture.One("alta.tag.proposed", ["tag", "propose", "--name", "lexer", "--description", "The lexer.", "--task", shortIds[0], "--task", shortIds[1], "--task", shortIds[2]]);
        Assert.AreEqual("ok", (await fixture.Rpc.TagAsync(new(Epoch, project.Id, "dismiss", "lexer"), default)).Status);
        CollectionAssert.Contains(items.ListTags(project).Declined.ToArray(), "lexer");
        Assert.AreEqual(AltaExitCodes.PolicyDenied, (await fixture.Run(["tag", "propose", "--name", "lexer", "--description", "The lexer.", "--task", shortIds[0], "--task", shortIds[1], "--task", shortIds[2]])).Code);

        // Finding the tasks of a tag starts a session with what the tag is for.
        var find = await fixture.Rpc.TagAsync(new(Epoch, project.Id, "find", "mobile"), default);
        Assert.AreEqual(("ok", "finder-session"), (find.Status, find.SessionId));
        Assert.AreEqual(("mobile", "The phone app."), fixture.Runner.Finds.Single());
        Assert.AreEqual("not_found", (await fixture.Rpc.TagAsync(new(Epoch, project.Id, "find", "desktop"), default)).Status);
        StringAssert.Contains(WorkItemPrompts.ForTagMatches("mobile", "The phone app."), "alta tag matches mobile --stdin");
        // Without a description the session goes by the name, and finds nothing when the name is not clear.
        StringAssert.Contains(WorkItemPrompts.ForTagMatches("v2", null), "go by its name. When the name does not say clearly what the tag covers, say so and run nothing.");
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

        // Each task is listed with its short id, which the commands take in place of its id.
        var shortId = first[^4..];
        Assert.AreEqual(shortId, Text(Of(listed, "alta.task").Single(record => Text(record, "id") == first), "shortId"));
        Assert.AreEqual(first, Text(await fixture.One("alta.task", ["task", "show", shortId]), "id"));
        File.Copy(Path.Combine(fixture.Project.ProjectPath, ".alta", "tasks", first + ".md"), Path.Combine(fixture.Project.ProjectPath, ".alta", "tasks", "2026-01-01-twin-" + shortId + ".md"));
        var (ambiguous, _, why) = await fixture.Run(["task", "start", shortId]);
        Assert.AreEqual(AltaExitCodes.Usage, ambiguous, "A short id that two tasks share names neither.");
        StringAssert.Contains(why, "2026-01-01-twin-" + shortId);
        File.Delete(Path.Combine(fixture.Project.ProjectPath, ".alta", "tasks", "2026-01-01-twin-" + shortId + ".md"));

        Assert.AreEqual("in-progress", Text(await fixture.One("alta.task.changed", ["task", "start", first]), "status"));
        Assert.AreEqual("session-of-the-user", fixture.Items.GetLink(fixture.Project.Id, "task", first)!.Runner);
        Assert.AreEqual("in-progress", Text(await fixture.One("alta.task", ["task", "show", first]), "status"));
        // The session that proposed the task started it: its card stays, saying so, until the user puts it away.
        Assert.IsFalse(fixture.Items.GetLink(fixture.Project.Id, "task", first)!.Acknowledged);
        // Another session that starts a task puts its card away: the session that proposed it no longer offers it.
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["task", "start", second],
            caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "another-session", SourceProjectId = fixture.Project.Id })).Code);
        Assert.IsTrue(fixture.Items.GetLink(fixture.Project.Id, "task", second)!.Acknowledged);

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
    public async Task ThePage_SeesWhatAnItemRunsWith_AndStartsItWithWhatTheUserChose()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = fixture.Items.CreateTask(fixture.Project, new WorkTaskDraft("A task", null, null, "Body."), "session-of-the-user", new WorkItemSelection("codex", "gpt-a", "high")).Task!.Id;
        var plain = fixture.Items.CreateTask(fixture.Project, new WorkTaskDraft("Another task", null, null, "Body."), null).Task!.Id;

        var rows = (await fixture.Rpc.ListAsync(new(Epoch), default)).Projects.Single().Tasks;
        Assert.AreEqual(new WorkItemRunsWith("codex", "gpt-a", "high"), rows.Single(row => row.Id == id).RunsWith);
        Assert.IsNull(rows.Single(row => row.Id == plain).RunsWith, "Nothing is known: the page proposes the default.");

        Task<WorkItemActionResponse> Start(string? provider, string? model = null, string? effort = null)
            => fixture.Rpc.ActAsync(new(Epoch, fixture.Project.Id, "task", id, "start_session", ProviderId: provider, ModelId: model, ReasoningEffort: effort), default);

        // Nothing chosen: the runner takes what is recorded with the item, then the defaults.
        Assert.AreEqual("ok", (await Start(null)).Status);
        Assert.IsNull(fixture.Runner.Asked.Single());
        Assert.AreEqual("ok", (await Start("anthropic", "claude", "low")).Status);
        Assert.AreEqual(new WorkItemSelection("anthropic", "claude", "low"), fixture.Runner.Asked[^1]);
        Assert.AreEqual("ok", (await Start("anthropic")).Status);
        Assert.AreEqual(new WorkItemSelection("anthropic"), fixture.Runner.Asked[^1]);

        // A model is of a provider, an effort of a model, and none of them is a control character away from another.
        Assert.AreEqual("invalid_request", (await Start(null, "claude")).Status);
        Assert.AreEqual("invalid_request", (await Start("anthropic", null, "low")).Status);
        Assert.AreEqual("invalid_request", (await Start("anthro\npic")).Status);
        Assert.HasCount(3, fixture.Runner.Asked);
        Assert.IsTrue(fixture.Runner.Settings.All(static settings => settings is null), "Nothing chosen besides the model: the defaults.");

        // The permission mode, the review and the sandbox of the form of the Agent chip go with the start.
        Assert.AreEqual("ok", (await fixture.Rpc.ActAsync(new(Epoch, fixture.Project.Id, "task", id, "start_session", AgentPromptId: "plan", PermissionMode: "acceptEdits",
            ReviewBeforeCommit: true, Sandbox: "off"), default)).Status);
        Assert.AreEqual(new SessionStartSettings("plan", "acceptEdits", true, "off"), fixture.Runner.Settings[^1]);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.ActAsync(new(Epoch, fixture.Project.Id, "task", id, "start_session", Sandbox: "of\nf"), default)).Status);
    }

    [TestMethod]
    public async Task TheRunner_GivesTheStarter_WhatTheUserChoseAndWhatIsRecordedWithTheItem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = fixture.Items.CreateTask(fixture.Project, new WorkTaskDraft("A task", null, null, "Body."), "session-of-the-user", new WorkItemSelection("codex", "gpt-a", "high")).Task!.Id;
        var starter = new RecordingStarter();
        var runner = new WorkItemRunner(starter, fixture.Items);

        Assert.AreEqual("new-session", (await runner.StartAsync(fixture.Project, "task", id, worktree: false, likeSessionId: "session-1", asked: null, settings: null)).SessionId);
        Assert.AreEqual(("session-1", null, new WorkItemSelection("codex", "gpt-a", "high")), (starter.Like, starter.Model!.Asked, starter.Model.Recorded));
        Assert.AreEqual((null, null, null), (starter.Model.PermissionMode, starter.Model.ReviewBeforeCommit, starter.Model.Sandbox));
        Assert.AreEqual("new-session", fixture.Items.GetLink(fixture.Project.Id, "task", id)!.Runner);

        await runner.StartAsync(fixture.Project, "task", id, worktree: true, likeSessionId: null, asked: new("anthropic", "claude"), settings: new("reviewer", "plan", false, "strict"));
        Assert.AreEqual((null, new WorkItemSelection("anthropic", "claude"), new WorkItemSelection("codex", "gpt-a", "high")), (starter.Like, starter.Model!.Asked, starter.Model.Recorded));
        Assert.AreEqual(("reviewer", "plan", (bool?)false, "strict"), (starter.Model.AgentPromptId, starter.Model.PermissionMode, starter.Model.ReviewBeforeCommit, starter.Model.Sandbox));
    }

    private sealed class RecordingStarter : ISessionStarter
    {
        internal string? Like { get; private set; }

        internal SessionStartModel? Model { get; private set; }

        public Task<SessionStartResult> StartAsync(ProjectDescriptor project, string title, Func<string, string> prompt, bool worktree, string? likeSessionId, string origin, SessionStartModel? model)
        {
            (Like, Model) = (likeSessionId, model);
            return Task.FromResult(new SessionStartResult("new-session", null));
        }
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
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WithinAsync(Patience));
        Assert.AreEqual(0, events.Current.Revision);

        var saved = await fixture.Rpc.SaveSettingsAsync(new(Epoch, new WorkItemsSettings(false, false, "keep", "delete", "delete", "here")), default);

        Assert.AreEqual(("ok", new WorkItemsSettings(false, false, "keep", "delete", "delete", "here")), (saved.Status, saved.Settings));
        Assert.AreEqual(new WorkItemSettings(false, false, WorkItemClosing.Keep, WorkItemClosing.Delete, WorkItemClosing.Delete, WorkItemStart.Here), fixture.Items.Settings);
        Assert.IsTrue(await events.MoveNextAsync().AsTask().WithinAsync(Patience));
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

        internal List<WorkItemSelection?> Asked { get; } = [];

        internal List<SessionStartSettings?> Settings { get; } = [];

        internal (string Message, string Reason)? Problem { get; set; }

        internal List<(string Name, string? Description)> Finds { get; } = [];

        public Task<WorkItemStartResult> FindTagTasksAsync(ProjectDescriptor project, string name, string? description)
        {
            Finds.Add((name, description));
            return Task.FromResult(new WorkItemStartResult("finder-session", null));
        }

        public Task<WorkItemStartResult> StartAsync(ProjectDescriptor project, string kind, string id, bool worktree, string? likeSessionId, WorkItemSelection? asked,
            SessionStartSettings? settings)
        {
            if (Problem is { } problem) return Task.FromResult(new WorkItemStartResult(null, problem.Message, problem.Reason));
            Starts.Add((project.Id, kind, id, worktree, likeSessionId));
            Asked.Add(asked);
            Settings.Add(settings);
            return Task.FromResult(new WorkItemStartResult("new-session", null));
        }
    }

    // The work items of an application over a real project catalog: the page's service and the alta commands on the same files.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, CatalogOptions options, ProjectCatalog projects, ProjectDescriptor project, ProjectDescriptor other, bool sandboxed)
        {
            (_root, Project, Other) = (root, project, other);
            Items = new WorkItemService(new CodeAltaConfigStore(options), Path.Combine(root, "state"));
            Rpc = new WorkItemsService(Items, projects, Runner, Epoch);
            var services = new AltaServiceCollection().Add(projects).Add(Items);
            if (sandboxed) services.Add(new AltaSandboxedSessions((id, _) => Task.FromResult(id == "session-of-the-user" ? new AltaSessionSandbox("strict", []) : null)));
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

        public static async Task<Fixture> CreateAsync(bool sandboxed = false)
        {
            var root = Directory.CreateTempSubdirectory("codealta-work-item-host-").FullName;
            var options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName };
            var projects = new ProjectCatalog(options);
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "other")).FullName);
            return new Fixture(root, options, projects, project, other, sandboxed);
        }

        public async Task<(int Code, List<JsonElement> Records, string Text)> Run(string[] arguments, string? stdin = null, AltaCallerIdentity? caller = null)
        {
            var result = await Alta.InvokeAsync(arguments, stdin, caller ?? Session, Project.ProjectPath).AsTask().WithinAsync(Patience);
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

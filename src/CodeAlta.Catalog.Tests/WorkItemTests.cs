using CodeAlta.Catalog.WorkItems;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class WorkItemTests
{
    [TestMethod]
    public void Plan_WrittenBeforeTheFrontMatter_IsReadFromItsTitleAndItsStatusLine()
    {
        const string Text = """
            # MCP OpenAI strict schema rejection

            - Status: Implemented (automated verification passed; live Atlassian manual check not run)
            - Plan file: `.alta/plans/2026-06-17-mcp-openai-strict-schema.md`
            - Created: 2026-06-17
            - Task: Investigate and fix MCP direct tools whose JSON schemas are rejected.

            ## Objective
            """;

        var plan = WorkItemFiles.ParsePlan(Path.Combine("plans", "2026-06-17-mcp-openai-strict-schema.md"), Text);

        Assert.AreEqual("2026-06-17-mcp-openai-strict-schema", plan.Id);
        Assert.AreEqual("MCP OpenAI strict schema rejection", plan.Title);
        Assert.AreEqual(WorkPlanStatus.Done, plan.Status);
        Assert.AreEqual("Implemented (automated verification passed; live Atlassian manual check not run)", plan.StatusText, "What the file says is kept when it is more than a status.");
        Assert.AreEqual("2026-06-17", plan.Created);
        Assert.AreEqual("Investigate and fix MCP direct tools whose JSON schemas are rejected.", plan.Summary);
        Assert.IsFalse(plan.HasFrontMatter);
    }

    [TestMethod]
    public void Plan_WithAFrontMatter_IsReadFromIt()
    {
        const string Text = "---\ntitle: \"Filter the task list by kind\"\nstatus: approved\ncreated: 2026-10-07\nsummary: Adds a --kind option.\n---\n\n# Another heading\n\nBody.\n";

        var plan = WorkItemFiles.ParsePlan("2026-10-07-kind-filter.md", Text);

        Assert.AreEqual(("Filter the task list by kind", WorkPlanStatus.Approved, "2026-10-07", "Adds a --kind option."), (plan.Title, plan.Status, plan.Created, plan.Summary));
        Assert.IsNull(plan.StatusText);
        Assert.IsTrue(plan.HasFrontMatter);
        Assert.AreEqual("# Another heading\n\nBody.\n", WorkItemFiles.Body(Text));
    }

    [TestMethod]
    [DataRow("Draft", WorkPlanStatus.Draft)]
    [DataRow("Approved", WorkPlanStatus.Approved)]
    [DataRow("ready", WorkPlanStatus.Approved)]
    [DataRow("In progress", WorkPlanStatus.InProgress)]
    [DataRow("in-progress", WorkPlanStatus.InProgress)]
    [DataRow("Functional-first execution resumed on 2026-09-22.", WorkPlanStatus.InProgress)]
    [DataRow("Completed", WorkPlanStatus.Done)]
    [DataRow("Complete — implemented and verified on `main`.", WorkPlanStatus.Done)]
    [DataRow("Done", WorkPlanStatus.Done)]
    [DataRow("Blocked", WorkPlanStatus.Blocked)]
    [DataRow("something nobody wrote before", WorkPlanStatus.Draft)]
    [DataRow(null, WorkPlanStatus.Draft)]
    public void PlanStatus_IsReadFromWhatPeopleAndOlderPromptsWrote(string? text, WorkPlanStatus expected)
        => Assert.AreEqual(expected, WorkItemFiles.ParsePlanStatus(text));

    [TestMethod]
    public void Status_IsChangedInPlace_WhateverTheFileLooksLike()
    {
        // A front matter that has a status, one that has none, a list under the title, and nothing at all.
        Assert.AreEqual("---\r\ntitle: A\r\nstatus: done\r\n---\r\n\r\nBody\r\n", WorkItemFiles.WithStatus("---\r\ntitle: A\r\nstatus: draft\r\n---\r\n\r\nBody\r\n", "done"));
        Assert.AreEqual("---\ntitle: A\nstatus: approved\n---\nBody", WorkItemFiles.WithStatus("---\ntitle: A\n---\nBody", "approved"));
        Assert.AreEqual("# Title\n\n- Status: In progress\n- Created: 2026-06-17\n", WorkItemFiles.WithStatus("# Title\n\n- Status: Approved (by the user)\n- Created: 2026-06-17\n", "in-progress"));
        Assert.AreEqual("---\nstatus: done\n---\n\n# Title\n\nBody\n", WorkItemFiles.WithStatus("# Title\n\nBody\n", "done"));

        Assert.AreEqual(WorkPlanStatus.InProgress, WorkItemFiles.ParsePlan("p.md", WorkItemFiles.WithStatus("# Title\n\n- Status: Approved\n", "in-progress")).Status);
    }

    [TestMethod]
    public void Task_IsWrittenAndReadBack()
    {
        var text = WorkItemFiles.SerializeTask("Report the \"why\" of a failed probe", "gap", WorkTaskStatus.Pending, "2026-10-07", "One line: with a colon.", "## Why\r\n\r\nBecause.\r\n");

        var task = WorkItemFiles.ParseTask(Path.Combine("tasks", "2026-10-07-report-the-why.md"), text);

        Assert.AreEqual("2026-10-07-report-the-why", task.Id);
        Assert.AreEqual("Report the \"why\" of a failed probe", task.Title);
        Assert.AreEqual(("gap", WorkTaskStatus.Pending, "2026-10-07", "One line: with a colon."), (task.Kind, task.Status, task.Created, task.Summary));
        Assert.AreEqual("## Why\n\nBecause.", task.Body);
        StringAssert.StartsWith(text, "---\ntitle: ");
    }

    [TestMethod]
    public void Task_WrittenByHand_IsAPendingImprovementNamedAfterItsHeading()
    {
        var task = WorkItemFiles.ParseTask("2026-10-01-by-hand.md", "# Tidy the build script\n\nIt repeats itself.\n");

        Assert.AreEqual(("Tidy the build script", "improvement", WorkTaskStatus.Pending, "2026-10-01"), (task.Title, task.Kind, task.Status, task.Created));
        Assert.IsNull(task.Summary);
    }

    [TestMethod]
    public void Ids_AreFileNamesWithoutAFolder()
    {
        Assert.AreEqual("2026-10-07-fix-the-cafe-test-on-windows", WorkItemFiles.NewId(new DateOnly(2026, 10, 7), "  Fix the café test (on Windows)! "));
        Assert.AreEqual("2026-10-07-task", WorkItemFiles.NewId(new DateOnly(2026, 10, 7), "???"));
        Assert.IsTrue(WorkItemFiles.NewId(new DateOnly(2026, 10, 7), new string('a', 300)).Length <= 80);
        foreach (var id in new[] { "2026-06-17-mcp-openai-strict-schema", "Plan_2.v1", "a" }) Assert.IsTrue(WorkItemFiles.IsValidId(id), id);
        foreach (var id in new[] { "", "..", "../secret", "a/b", "a\\b", ".hidden", "name.", "with space", new string('a', 161), null }) Assert.IsFalse(WorkItemFiles.IsValidId(id), id);
    }

    [TestMethod]
    public async Task Tasks_AreCreatedListedAndClosed_AsTheSettingsSay()
    {
        using var root = new TempRoot();
        var (items, project) = await root.CreateAsync();
        var changes = 0;
        items.Changed += () => changes++;

        var first = items.CreateTask(project, new WorkTaskDraft("Report why a probe failed", "gap", "The page says nothing.", "## Why\nNothing is shown."), "session-1").Task!;
        var second = items.CreateTask(project, new WorkTaskDraft("Report why a probe failed", null, null, "Again."), null).Task!;

        Assert.AreEqual(2, changes);
        Assert.AreNotEqual(first.Id, second.Id, "Two tasks of a day with one title have two files.");
        // An id is the day, the title and a short random end, which keeps two checkouts of a project from creating one file.
        foreach (var id in new[] { first.Id, second.Id })
        {
            StringAssert.Matches(id, new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2}-report-why-a-probe-failed-[23456789bcdfghjkmnpqrstvwxz]{4}$"));
            Assert.IsTrue(WorkItemFiles.IsValidId(id));
        }
        Assert.IsTrue(File.Exists(Path.Combine(project.ProjectPath, ".alta", "tasks", first.Id + ".md")));
        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, items.ListTasks(project).Select(static task => task.Id).ToArray());
        Assert.AreEqual("session-1", items.GetLink(project.Id, WorkItemKinds.Task, first.Id)!.ProposedBy);
        Assert.IsNull(items.GetLink(project.Id, WorkItemKinds.Task, second.Id), "A task nobody proposed from a session has no link.");

        // Later and back: the file keeps everything else.
        Assert.IsTrue(items.SetTaskStatus(project, first.Id, WorkTaskStatus.Later));
        Assert.AreEqual((WorkTaskStatus.Later, "The page says nothing."), (items.GetTask(project, first.Id)!.Status, items.GetTask(project, first.Id)!.Summary));
        Assert.IsTrue(items.SetTaskStatus(project, first.Id, WorkTaskStatus.Pending));

        // Completed and dismissed tasks are deleted unless the user keeps them.
        items.SetRunner(project.Id, WorkItemKinds.Task, first.Id, "session-2");
        Assert.IsTrue(items.CompleteTask(project, first.Id));
        Assert.IsNull(items.GetTask(project, first.Id));
        Assert.IsNull(items.GetLink(project.Id, WorkItemKinds.Task, first.Id));
        Assert.IsFalse(items.CompleteTask(project, first.Id));

        items.SaveSettings(WorkItemSettings.Default with { CompletedTasks = WorkItemClosing.Keep, DismissedTasks = WorkItemClosing.Keep });
        var kept = items.CreateTask(project, new WorkTaskDraft("Keep me", "problem", null, "Body."), "session-1").Task!;
        items.SetRunner(project.Id, WorkItemKinds.Task, kept.Id, "session-2");
        Assert.IsTrue(items.CompleteTask(project, kept.Id));
        Assert.AreEqual(WorkTaskStatus.Done, items.GetTask(project, kept.Id)!.Status);
        Assert.IsNull(items.GetLink(project.Id, WorkItemKinds.Task, kept.Id)!.Runner, "No session carries out a task that is done.");
        Assert.IsTrue(items.DismissTask(project, second.Id));
        Assert.AreEqual(WorkTaskStatus.Dismissed, items.GetTask(project, second.Id)!.Status);
        Assert.IsTrue(items.RemoveTask(project, second.Id), "Removing deletes the file whatever the settings say.");
        Assert.IsNull(items.GetTask(project, second.Id));
    }

    [TestMethod]
    public async Task Tasks_AreRefused_WhenProposalsAreOffOrASessionHasTooMany()
    {
        using var root = new TempRoot();
        var (items, project) = await root.CreateAsync();
        var draft = new WorkTaskDraft("A task", null, null, "Body.");

        for (var index = 0; index < WorkItemService.MaximumProposals; index++) Assert.IsNotNull(items.CreateTask(project, draft, "session-1").Task);
        Assert.AreEqual(WorkTaskRefusal.TooManyProposals, items.CreateTask(project, draft, "session-1").Refusal);
        Assert.IsNotNull(items.CreateTask(project, draft, "session-2").Task, "Another session has its own count.");
        // A decision of the user makes room: the task is no longer a proposal that waits.
        items.SetTaskStatus(project, items.ListTasks(project).First(task => items.GetLink(project.Id, WorkItemKinds.Task, task.Id)?.ProposedBy == "session-1").Id, WorkTaskStatus.Later);
        Assert.IsNotNull(items.CreateTask(project, draft, "session-1").Task);

        items.SaveSettings(WorkItemSettings.Default with { Propose = false });
        Assert.AreEqual(WorkTaskRefusal.Disabled, items.CreateTask(project, draft, "session-3").Refusal);
        Assert.IsNotNull(items.CreateTask(project, draft, null).Task, "The setting is about what agents propose.");

        Assert.ThrowsExactly<ArgumentException>(() => items.CreateTask(project, new WorkTaskDraft(" ", null, null, "Body."), null));
        Assert.ThrowsExactly<ArgumentException>(() => items.CreateTask(project, new WorkTaskDraft("Title", "feature", null, "Body."), null));
        Assert.ThrowsExactly<ArgumentException>(() => items.CreateTask(project, new WorkTaskDraft("Title", null, null, "  "), null));
        Assert.ThrowsExactly<ArgumentException>(() => items.CreateTask(project, new WorkTaskDraft("Title", null, null, new string('x', WorkItemFiles.MaximumTaskBody + 1)), null));
    }

    [TestMethod]
    public async Task Plans_AreListedNewestFirst_ChangedInPlaceAndRemoved()
    {
        using var root = new TempRoot();
        var (items, project) = await root.CreateAsync();
        var plans = Directory.CreateDirectory(Path.Combine(project.ProjectPath, ".alta", "plans")).FullName;
        File.WriteAllText(Path.Combine(plans, "2026-06-03-old.md"), "# Old plan\r\n\r\n- Status: Approved\r\n- Created: 2026-06-03\r\n\r\n## Objective\r\n");
        File.WriteAllText(Path.Combine(plans, "2026-10-07-new.md"), "---\ntitle: New plan\nstatus: draft\ncreated: 2026-10-07\nsummary: What it does.\n---\n\n# New plan\n\n" + new string('x', 5000) + "\n");
        File.WriteAllText(Path.Combine(plans, "notes.txt"), "not a plan");

        CollectionAssert.AreEqual(new[] { "2026-10-07-new", "2026-06-03-old" }, items.ListPlans(project).Select(static plan => plan.Id).ToArray());

        Assert.IsTrue(items.SetPlanStatus(project, "2026-06-03-old", WorkPlanStatus.Done));
        Assert.AreEqual("# Old plan\r\n\r\n- Status: Done\r\n- Created: 2026-06-03\r\n\r\n## Objective\r\n", File.ReadAllText(Path.Combine(plans, "2026-06-03-old.md")));
        Assert.AreEqual(WorkPlanStatus.Done, items.GetPlan(project, "2026-06-03-old")!.Status);

        var text = items.ReadMarkdown(project, WorkItemKinds.Plan, "2026-10-07-new", 100)!.Value;
        StringAssert.StartsWith(text.Markdown, "# New plan");
        Assert.AreEqual((100, true), (text.Markdown.Length, text.Truncated));
        Assert.IsFalse(items.ReadMarkdown(project, WorkItemKinds.Plan, "2026-06-03-old", 100_000)!.Value.Truncated);
        Assert.IsNull(items.ReadMarkdown(project, WorkItemKinds.Plan, "missing", 100));
        Assert.IsNull(items.GetPlan(project, "../2026-06-03-old"));

        items.Propose(project.Id, WorkItemKinds.Plan, "2026-10-07-new", "session-1");
        Assert.IsTrue(items.RemovePlan(project, "2026-10-07-new"));
        Assert.IsFalse(File.Exists(Path.Combine(plans, "2026-10-07-new.md")));
        Assert.IsNull(items.GetLink(project.Id, WorkItemKinds.Plan, "2026-10-07-new"));
        Assert.IsFalse(items.RemovePlan(project, "2026-10-07-new"));
    }

    [TestMethod]
    public async Task Plan_OfASessionInAWorktree_IsTheCopyOfThatWorktree()
    {
        using var root = new TempRoot();
        var (items, project) = await root.CreateAsync();
        var worktree = Directory.CreateDirectory(Path.Combine(root.Path, "worktree")).FullName;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project.ProjectPath, ".alta", "plans")).FullName, "plan.md"), "---\nstatus: approved\n---\n# Plan\n");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(worktree, ".alta", "plans")).FullName, "plan.md"), "---\nstatus: approved\n---\n# Plan\n");

        Assert.IsTrue(items.SetPlanStatus(project, "plan", WorkPlanStatus.InProgress, worktree));

        Assert.AreEqual(WorkPlanStatus.InProgress, items.GetPlan(project, "plan", worktree)!.Status);
        Assert.AreEqual(WorkPlanStatus.Approved, items.GetPlan(project, "plan")!.Status, "The folder of the project keeps its copy as it is: the branch brings the change when it is merged.");

        // A worktree that does not have the plan works on the one of the project.
        File.Delete(Path.Combine(worktree, ".alta", "plans", "plan.md"));
        Assert.IsTrue(items.SetPlanStatus(project, "plan", WorkPlanStatus.Done, worktree));
        Assert.AreEqual(WorkPlanStatus.Done, items.GetPlan(project, "plan")!.Status);
    }

    [TestMethod]
    public async Task Plan_ThatIsDone_IsKeptOrDeletedAsTheUserChose()
    {
        using var root = new TempRoot();
        var (items, project) = await root.CreateAsync();
        var plans = Directory.CreateDirectory(Path.Combine(project.ProjectPath, ".alta", "plans")).FullName;
        var worktree = Directory.CreateDirectory(Path.Combine(root.Path, "worktree")).FullName;
        var copies = Directory.CreateDirectory(Path.Combine(worktree, ".alta", "plans")).FullName;
        foreach (var name in new[] { "kept", "deleted", "branch" })
        {
            File.WriteAllText(Path.Combine(plans, name + ".md"), "---\nstatus: approved\n---\n# Plan\n");
            items.Propose(project.Id, WorkItemKinds.Plan, name, "session-1");
            items.SetRunner(project.Id, WorkItemKinds.Plan, name, "session-2");
        }

        File.Copy(Path.Combine(plans, "branch.md"), Path.Combine(copies, "branch.md"));

        // Kept, as plans always were: the file says done, nobody carries it out and its card does not come back.
        Assert.IsTrue(items.SetPlanStatus(project, "kept", WorkPlanStatus.Done));
        Assert.AreEqual(WorkPlanStatus.Done, items.GetPlan(project, "kept")!.Status);
        var link = items.GetLink(project.Id, WorkItemKinds.Plan, "kept")!;
        Assert.AreEqual((null, true), (link.Runner, link.Acknowledged));

        items.SaveSettings(WorkItemSettings.Default with { CompletedPlans = WorkItemClosing.Delete });
        Assert.IsTrue(items.SetPlanStatus(project, "deleted", WorkPlanStatus.InProgress), "Only a plan that is done is deleted.");
        Assert.IsTrue(File.Exists(Path.Combine(plans, "deleted.md")));
        Assert.IsTrue(items.SetPlanStatus(project, "deleted", WorkPlanStatus.Done));
        Assert.IsFalse(File.Exists(Path.Combine(plans, "deleted.md")));
        Assert.IsNull(items.GetLink(project.Id, WorkItemKinds.Plan, "deleted"));
        Assert.IsTrue(File.Exists(Path.Combine(plans, "kept.md")), "A plan that was done before the choice stays.");

        // A session in a worktree deletes its copy; the one of the project goes when the branch is merged.
        Assert.IsTrue(items.SetPlanStatus(project, "branch", WorkPlanStatus.Done, worktree));
        Assert.IsFalse(File.Exists(Path.Combine(copies, "branch.md")));
        Assert.IsTrue(File.Exists(Path.Combine(plans, "branch.md")));
        link = items.GetLink(project.Id, WorkItemKinds.Plan, "branch")!;
        Assert.AreEqual((null, true), (link.Runner, link.Acknowledged));
    }

    [TestMethod]
    public async Task Links_AreKeptForTheNextStart_AndSayWhoProposedAndWhoRuns()
    {
        using var root = new TempRoot();
        var (items, project) = await root.CreateAsync();
        var task = items.CreateTask(project, new WorkTaskDraft("A task", null, null, "Body."), "session-1").Task!;

        items.SetRunner(project.Id, WorkItemKinds.Task, task.Id, "session-2");
        items.Acknowledge(project.Id, WorkItemKinds.Plan, "a-plan");

        var again = root.Service();
        var link = again.GetLink(project.Id, WorkItemKinds.Task, task.Id)!;
        Assert.AreEqual(("session-1", "session-2", true), (link.ProposedBy, link.Runner, link.StartedAt is not null));
        Assert.IsTrue(again.GetLink(project.Id, WorkItemKinds.Plan, "a-plan")!.Acknowledged);
        again.SetRunner(project.Id, WorkItemKinds.Task, task.Id, null);
        Assert.IsNull(root.Service().GetLink(project.Id, WorkItemKinds.Task, task.Id)!.Runner);
        Assert.ThrowsExactly<ArgumentException>(() => again.Propose(project.Id, "note", task.Id, "session-1"));

        // A file that is not what the application wrote is an empty one.
        File.WriteAllText(Path.Combine(root.Path, "state", "work_items.json"), "{ not json");
        Assert.IsEmpty(root.Service().Links);
    }

    [TestMethod]
    public async Task Links_KeepWhatTheSessionThatProposedAnItemRanWith()
    {
        using var root = new TempRoot();
        var (items, project) = await root.CreateAsync();
        var task = items.CreateTask(project, new WorkTaskDraft("A task", null, null, "Body."), "session-1", new WorkItemSelection(" codex ", "gpt-a", "High")).Task!;

        // The next run of the application starts the task with the same provider, model and effort.
        Assert.AreEqual(new WorkItemSelection("codex", "gpt-a", "high"), root.Service().GetLink(project.Id, WorkItemKinds.Task, task.Id)!.RunsWith);

        // It outlives the card being put away and the task being started and released.
        items.Acknowledge(project.Id, WorkItemKinds.Task, task.Id);
        items.SetRunner(project.Id, WorkItemKinds.Task, task.Id, "session-2");
        items.SetRunner(project.Id, WorkItemKinds.Task, task.Id, null);
        Assert.AreEqual("codex", root.Service().GetLink(project.Id, WorkItemKinds.Task, task.Id)!.RunsWith!.ProviderKey);

        // A plan takes those of the session that had it approved; approving again without them keeps them.
        items.Propose(project.Id, WorkItemKinds.Plan, "a-plan", "session-1", new WorkItemSelection("anthropic", null, " "));
        Assert.AreEqual(new WorkItemSelection("anthropic"), items.GetLink(project.Id, WorkItemKinds.Plan, "a-plan")!.RunsWith);
        items.Propose(project.Id, WorkItemKinds.Plan, "a-plan", "session-3");
        Assert.AreEqual(("session-3", "anthropic"), (items.GetLink(project.Id, WorkItemKinds.Plan, "a-plan")!.ProposedBy, items.GetLink(project.Id, WorkItemKinds.Plan, "a-plan")!.RunsWith!.ProviderKey));

        // Without a provider there is nothing to keep, and a task nobody proposed with nothing keeps no link.
        var plain = items.CreateTask(project, new WorkTaskDraft("Another", null, null, "Body."), null, new WorkItemSelection(" ", "gpt-a")).Task!;
        Assert.IsNull(items.GetLink(project.Id, WorkItemKinds.Task, plain.Id));
        var outside = items.CreateTask(project, new WorkTaskDraft("A third", null, null, "Body."), null, new WorkItemSelection("codex")).Task!;
        Assert.AreEqual("codex", items.GetLink(project.Id, WorkItemKinds.Task, outside.Id)!.RunsWith!.ProviderKey);

        // The task that is removed leaves nothing behind.
        Assert.IsTrue(items.RemoveTask(project, task.Id));
        Assert.IsNull(root.Service().GetLink(project.Id, WorkItemKinds.Task, task.Id));
    }

    [TestMethod]
    public void Settings_ReadWhatIsWritten_AndDefaultOtherwise()
    {
        Assert.AreEqual(WorkItemSettings.Default, WorkItemSettings.Read(null));
        Assert.AreEqual(new WorkItemSettings(false, true, WorkItemClosing.Keep, WorkItemClosing.Delete, WorkItemClosing.Delete, WorkItemStart.Here),
            WorkItemSettings.Read(new CodeAltaWorkItemSettingsDocument { Propose = false, CompletedTasks = "KEEP", DismissedTasks = "archive", CompletedPlans = "delete", Start = " here " }));
        Assert.AreEqual(WorkItemClosing.Keep, WorkItemSettings.Default.CompletedPlans, "A plan that is done is kept unless the user says otherwise.");
        Assert.AreEqual(("keep", "session"), (WorkItemSettings.NameOf(WorkItemClosing.Keep), WorkItemSettings.NameOf(WorkItemStart.Session)));
    }

    [TestMethod]
    public async Task Settings_AreWrittenInTheConfiguration_OnlyWhereTheyDifferFromTheDefault()
    {
        using var root = new TempRoot();
        var (items, _) = await root.CreateAsync();

        items.SaveSettings(WorkItemSettings.Default with { Notify = false, Start = WorkItemStart.Session });

        var text = File.ReadAllText(root.Options.ConfigPath);
        StringAssert.Contains(text, "[work_items]");
        StringAssert.Contains(text, "notify = false");
        StringAssert.Contains(text, "start = \"session\"");
        Assert.IsFalse(text.Contains("propose", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("completed_plans", StringComparison.Ordinal));

        items.SaveSettings(WorkItemSettings.Default with { CompletedTasks = WorkItemClosing.Keep, CompletedPlans = WorkItemClosing.Delete });
        text = File.ReadAllText(root.Options.ConfigPath);
        StringAssert.Contains(text, "completed_tasks = \"keep\"");
        StringAssert.Contains(text, "completed_plans = \"delete\"");
        Assert.IsFalse(text.Contains("notify", StringComparison.Ordinal));
        items.SaveSettings(WorkItemSettings.Default with { Notify = false, Start = WorkItemStart.Session });
        Assert.AreEqual(WorkItemSettings.Default with { Notify = false, Start = WorkItemStart.Session }, root.Service().Settings);

        items.SaveSettings(WorkItemSettings.Default);
        Assert.IsFalse(File.ReadAllText(root.Options.ConfigPath).Contains("[work_items]", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Prompts_GiveTheTaskItselfAndThePlanWhereTheSessionFindsIt()
    {
        using var root = new TempRoot();
        var project = Directory.CreateDirectory(Path.Combine(root.Path, "project")).FullName;
        var worktree = Directory.CreateDirectory(Path.Combine(root.Path, "worktree")).FullName;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project, ".alta", "plans")).FullName, "plan.md"), "# Plan\n");
        var plan = new WorkPlan("plan", "The plan", null, WorkPlanStatus.Approved, null, null, Path.Combine(project, ".alta", "plans", "plan.md"), true);

        var task = WorkItemPrompts.ForTask(new WorkTask("2026-10-07-a-task", "A task", null, "gap", WorkTaskStatus.Pending, null, "x", "## Why\nBecause."));
        StringAssert.Contains(task, "# A task\n\n## Why\nBecause.");
        StringAssert.Contains(task, "alta task start 2026-10-07-a-task");
        StringAssert.Contains(task, "alta task complete 2026-10-07-a-task");

        StringAssert.StartsWith(WorkItemPrompts.ForPlan(plan, project, project), "Execute the approved plan at `.alta/plans/plan.md`.");
        // A worktree has what is committed: the plan that is not there is read from the project, and not copied.
        var elsewhere = WorkItemPrompts.ForPlan(plan, project, worktree);
        StringAssert.Contains(elsewhere, "alta plan show plan");
        StringAssert.Contains(elsewhere, "Do not copy it into this worktree");
        StringAssert.Contains(elsewhere, project);
        Assert.AreEqual(WorkItemPrompts.MaximumSessionTitle, WorkItemPrompts.SessionTitle(new string('t', 400)).Length);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("codealta-work-items-").FullName;

        public CatalogOptions Options => new() { GlobalRoot = System.IO.Path.Combine(Path, "global") };

        public WorkItemService Service() => new(new CodeAltaConfigStore(Options), System.IO.Path.Combine(Path, "state"));

        public async Task<(WorkItemService Items, ProjectDescriptor Project)> CreateAsync()
        {
            Directory.CreateDirectory(Options.GlobalRoot);
            var project = await new ProjectCatalog(Options).UpsertFromPathAsync(Directory.CreateDirectory(System.IO.Path.Combine(Path, "app")).FullName);
            return (Service(), project);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

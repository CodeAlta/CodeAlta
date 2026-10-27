using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Automations;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Scripts;

namespace CodeAlta.Desktop.Tests;

/// <summary>The automations as the page, the alta commands and the host of the application use them.</summary>
[TestClass]
public sealed class AutomationHostTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset Noon = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task Rpc_Lists_Saves_AndMovesAnAutomationBetweenTheFiles()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("unavailable", (await new AutomationsService().ListAsync(new(Epoch), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.ListAsync(new("other"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.SaveAsync(new("other", Input("x"), null), default)).Status);

        var empty = await fixture.Rpc.RefreshAsync(new(Epoch), default);
        Assert.AreEqual(("ok", false, true, 0), (empty.Status, empty.Paused, empty.Scanned, empty.Items.Count));

        // In the configuration of the user, running in a project.
        var saved = await fixture.Rpc.SaveAsync(new(Epoch, Input("  Morning brief  ") with { ProjectId = fixture.Project.Id, Provider = "codex", Model = "gpt", Effort = "High",
            PermissionMode = " acceptEdits ", ReviewBeforeCommit = true, Sandbox = "off" }, null), default);
        Assert.AreEqual("ok", saved.Status, saved.Message);
        Assert.IsTrue(AutomationDefinition.IsId(saved.Id));
        var text = File.ReadAllText(fixture.GlobalPath);
        StringAssert.Contains(text, $"[automations.{saved.Id}]");
        StringAssert.Contains(text, "model = \"codex:gpt@high\"");
        StringAssert.Contains(text, "permission_mode = \"acceptEdits\"");
        StringAssert.Contains(text, $"project = \"{fixture.Project.ProjectPath.Replace('\\', '/')}\"");

        var listed = await fixture.Rpc.ListAsync(new(Epoch), default);
        var item = listed.Items.Single();
        Assert.AreEqual((saved.Id, "Morning brief", true, "Do the thing.", fixture.Project.Id, fixture.Project.DisplayName, fixture.Project.ProjectPath),
            (item.Id, item.Name, item.Enabled, item.Prompt, item.ProjectId, item.ProjectName, item.ProjectFolder));
        Assert.AreEqual((null, fixture.GlobalPath, "codex", "gpt", "High", null, false), (item.StoreProjectId, item.File, item.Provider, item.Model, item.Effort, item.Agent, item.CatchUp));
        Assert.AreEqual(("acceptEdits", (bool?)true, "off"), (item.PermissionMode, item.ReviewBeforeCommit, item.Sandbox));
        var trigger = item.Triggers.Single();
        Assert.AreEqual(("daily", "09:00,17:30", "opened", "trusted"), (trigger.Type, string.Join(',', trigger.At), trigger.Event, trigger.Authors));
        Assert.AreEqual(Noon.AddHours(5).AddMinutes(30), item.NextRunAt);
        Assert.AreEqual((false, null, null, null, null, true), (item.Running, item.LastRun, item.Problem, item.Repository, item.WatchProblem, item.Allowed));
        CollectionAssert.AreEqual(new[] { Noon.AddHours(5).AddMinutes(30), Noon.AddHours(21) }, listed.Upcoming.Select(static time => time.At).ToArray(), "The day to come, soonest first.");
        Assert.IsTrue(listed.Upcoming.All(time => time.AutomationId == saved.Id));

        // Saved again with its project: it leaves the file of the user for the one of the project.
        var moved = await fixture.Rpc.SaveAsync(new(Epoch, Input("Morning brief") with { Id = saved.Id, ProjectId = fixture.Project.Id, CatchUp = true }, fixture.Project.Id), default);
        Assert.AreEqual(("ok", saved.Id), (moved.Status, moved.Id));
        Assert.IsFalse(File.ReadAllText(fixture.GlobalPath).Contains(saved.Id!, StringComparison.Ordinal));
        var projectFile = Path.Combine(fixture.Project.ProjectPath, ".alta", "config.toml");
        var stored = File.ReadAllText(projectFile);
        StringAssert.Contains(stored, $"[automations.{saved.Id}]");
        Assert.IsFalse(stored.Contains("project =", StringComparison.Ordinal), "A project's own file runs its automations in that project.");
        item = (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single();
        Assert.AreEqual((fixture.Project.Id, fixture.Project.Id, projectFile, true), (item.ProjectId, item.StoreProjectId, item.File, item.CatchUp));
        Assert.IsTrue(item.Allowed && item.NextRunAt is not null, "What the user saved in the file of a project is allowed by that.");

        // The repository changes it: its triggers wait for the user, whatever its switch says, until it is allowed.
        File.WriteAllText(projectFile, stored.Replace("Do the thing.", "Do what the repository says.", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(projectFile, DateTime.UtcNow.AddMinutes(5));
        item = (await fixture.Rpc.RefreshAsync(new(Epoch), default)).Items.Single();
        Assert.AreEqual((false, null, "Do what the repository says."), (item.Allowed, item.NextRunAt, item.Prompt));
        Assert.AreEqual("ok", (await fixture.Rpc.SetEnabledAsync(new(Epoch, saved.Id!, true), default)).Status);
        Assert.IsFalse((await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single().Allowed);
        Assert.AreEqual("stale_epoch", fixture.Rpc.Allow(new("other", saved.Id!)).Status);
        Assert.AreEqual("invalid_request", fixture.Rpc.Allow(new(Epoch, "nope")).Status);
        Assert.AreEqual("refused", fixture.Rpc.Allow(new(Epoch, "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77")).Status);
        Assert.AreEqual(("ok", saved.Id), (fixture.Rpc.Allow(new(Epoch, saved.Id!)).Status, saved.Id));
        item = (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single();
        Assert.IsTrue(item.Allowed && item.NextRunAt is not null);

        // What the user wrote wrong is refused with the reason; what the page never sends is not a request.
        Assert.AreEqual(("refused", "An automation has a name."), Outcome(await fixture.Rpc.SaveAsync(new(Epoch, Input(" "), null), default)));
        Assert.AreEqual(("refused", "A model or a reasoning effort needs its provider."), Outcome(await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Model = "gpt" }, null), default)));
        Assert.AreEqual("refused", (await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { ProjectId = "unknown" }, null), default)).Status);
        Assert.AreEqual("refused", (await fixture.Rpc.SaveAsync(new(Epoch, Input("x"), "unknown"), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Id = "not-an-id" }, null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Effort = "extreme" }, null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Triggers = [new("sometimes", 0, 1, [], [], null, "opened", "trusted", null, null)] }, null), default)).Status);
        Assert.AreEqual(1, (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Count);
    }

    [TestMethod]
    [DataRow("jira", "created", "created")]
    [DataRow("jira", "updated", "updated")]
    [DataRow("jira", "opened", "created")]
    [DataRow("issue", "opened", "opened")]
    [DataRow("pull_request", "opened", "opened")]
    [DataRow("pull_request", "updated", "updated")]
    public async Task Rpc_SavesAndReloadsTheEventOfItsTracker(string kind, string requestedEvent, string savedEvent)
    {
        await using var fixture = await Fixture.CreateAsync();
        var input = Input("Track an event") with
        {
            ProjectId = fixture.Project.Id,
            Triggers = [new(kind, 0, 1, [], [], null, requestedEvent, "trusted", null, null)],
        };

        var saved = await fixture.Rpc.SaveAsync(new(Epoch, input, null), default);

        Assert.AreEqual("ok", saved.Status, saved.Message);
        StringAssert.Contains(File.ReadAllText(fixture.GlobalPath), $"type = \"{kind}\", event = \"{savedEvent}\"");
        var item = (await fixture.Rpc.RefreshAsync(new(Epoch), default)).Items.Single();
        Assert.AreEqual(saved.Id, item.Id);
        Assert.AreEqual((kind, savedEvent), (item.Triggers.Single().Type, item.Triggers.Single().Event));
        Assert.AreEqual(0, fixture.Runner.Starts.Count, "Saving an event trigger does not run it.");
        Assert.AreEqual(0, fixture.Commands.Count);
    }

    [TestMethod]
    public async Task Rpc_StillRefusesAnUpdatedRepositoryIssue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var input = Input("Track an issue") with { Triggers = [new("issue", 0, 1, [], [], null, "updated", "trusted", null, null)] };

        Assert.AreEqual(("refused", "An issue trigger starts when an issue is opened."), Outcome(await fixture.Rpc.SaveAsync(new(Epoch, input, null), default)));
        Assert.AreEqual(0, fixture.Service.Snapshot.Entries.Count);
    }

    [TestMethod]
    public async Task Rpc_Enables_Pauses_Runs_AndDeletes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = (await fixture.Rpc.SaveAsync(new(Epoch, Input("Nightly"), null), default)).Id!;

        Assert.AreEqual("ok", (await fixture.Rpc.SetEnabledAsync(new(Epoch, id, false), default)).Status);
        var item = (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single();
        Assert.AreEqual((false, null), (item.Enabled, item.NextRunAt));
        StringAssert.Contains(File.ReadAllText(fixture.GlobalPath), "enabled = false");
        Assert.AreEqual("ok", (await fixture.Rpc.SetEnabledAsync(new(Epoch, id, true), default)).Status);
        Assert.IsNotNull((await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single().NextRunAt);

        Assert.AreEqual("ok", fixture.Rpc.SetPaused(new(Epoch, true)).Status);
        var paused = await fixture.Rpc.ListAsync(new(Epoch), default);
        Assert.AreEqual((true, null, 0), (paused.Paused, paused.Items.Single().NextRunAt, paused.Upcoming.Count));

        // Running by hand works while paused.
        var run = await fixture.Rpc.RunAsync(new(Epoch, id), default);
        Assert.AreEqual(("ok", id, "Nightly", "session-1", "manual", "running"), (run.Status, run.Run!.AutomationId, run.Run.Name, run.Run.SessionId, run.Run.Trigger, run.Run.Status));
        item = (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single();
        Assert.AreEqual((true, run.Run.Id), (item.Running, item.LastRun!.Id));
        (await fixture.Runner.NextAsync()).Complete(new(AutomationRun.Completed, null));
        await fixture.UntilAsync(() => !fixture.Service.IsRunning(id));
        var runs = fixture.Rpc.Runs(new(Epoch, id, null));
        Assert.AreEqual(("ok", "completed"), (runs.Status, runs.Runs.Single().Status));
        Assert.IsNotNull(runs.Runs.Single().EndedAt);
        Assert.AreEqual(1, (await fixture.Rpc.ListAsync(new(Epoch), default)).Runs.Count);
        Assert.AreEqual(0, fixture.Rpc.Runs(new(Epoch, "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77", 5)).Runs.Count);

        Assert.AreEqual("not_found", (await fixture.Rpc.RunAsync(new(Epoch, "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77"), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.RunAsync(new(Epoch, "nope"), default)).Status);
        Assert.AreEqual("invalid_request", fixture.Rpc.Runs(new(Epoch, "nope", null)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.DeleteAsync(new(Epoch, "nope"), default)).Status);
        Assert.AreEqual(("refused", "There is no such automation."), Outcome(await fixture.Rpc.SetEnabledAsync(new(Epoch, "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77", true), default)));

        Assert.AreEqual(("ok", id), ((await fixture.Rpc.DeleteAsync(new(Epoch, id), default)) is var deleted ? deleted.Status : null, deleted.Id));
        var after = await fixture.Rpc.ListAsync(new(Epoch), default);
        Assert.AreEqual((0, 1), (after.Items.Count, after.Runs.Count), "The runs of an automation outlive it.");
        Assert.AreEqual(("refused", "There is no such automation."), Outcome(await fixture.Rpc.DeleteAsync(new(Epoch, id), default)));
    }

    [TestMethod]
    public async Task Rpc_PreviewsATrigger_AndTellsWhenSomethingChanges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = fixture.Rpc.Preview(new(Epoch, new("hourly", 15, 6, [], [], null, "opened", "trusted", null, null)));
        Assert.AreEqual("ok", preview.Status);
        CollectionAssert.AreEqual(new[] { 12, 18, 0, 6, 12 }, preview.Times.Select(static time => time.Hour).ToArray());
        Assert.IsTrue(preview.Times.All(static time => time.Minute == 15));
        var wrong = fixture.Rpc.Preview(new(Epoch, new("cron", 0, 1, [], [], "61 * * * *", "opened", "trusted", null, null)));
        Assert.AreEqual("refused", wrong.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(wrong.Message));
        Assert.AreEqual("invalid_request", fixture.Rpc.Preview(new(Epoch, new("daily", 0, 1, ["25:00"], [], null, "opened", "trusted", null, null))).Status);
        Assert.AreEqual("stale_epoch", fixture.Rpc.Preview(new("other", new("daily", 0, 1, ["09:00"], [], null, "opened", "trusted", null, null))).Status);

        using var stop = new CancellationTokenSource(Patience);
        await using var events = fixture.Rpc.WatchAsync(new(Epoch), stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual(0, events.Current.Revision);
        fixture.Rpc.SetPaused(new(Epoch, true));
        Assert.IsTrue(await events.MoveNextAsync());
        Assert.AreEqual(1, events.Current.Revision);
        await using var none = fixture.Rpc.WatchAsync(new("other"), stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.IsFalse(await none.MoveNextAsync());
    }

    [TestMethod]
    public async Task TheCommands_ExistOnlyWhereAHostHasAutomations()
    {
        var services = new AltaServiceCollection();
        var registry = new AltaCommandRegistry();
        var plain = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(plain);
        Assert.AreEqual(AltaExitCodes.Usage, (await plain.InvokeAsync(["automation", "list"])).ExitCode);
        Assert.IsFalse((await plain.InvokeAsync(["--help"])).Stdout.Contains("Use the automations", StringComparison.Ordinal));
        Assert.IsFalse((await plain.InvokeAsync(["tool", "list"])).Stdout.Contains("automation run", StringComparison.Ordinal));

        await using var fixture = await Fixture.CreateAsync();
        StringAssert.Contains((await fixture.Alta.InvokeAsync(["--help"])).Stdout, "Use the automations");
        var tools = (await fixture.Alta.InvokeAsync(["tool", "list"])).Stdout;
        foreach (var command in new[] { "list", "show", "current", "runs", "run", "create", "enable", "disable", "delete" })
        {
            StringAssert.Contains(tools, "automation " + command);
            var help = await fixture.Alta.InvokeAsync(["automation", command, "--help"]);
            Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, command);
            Assert.IsTrue(help.IsHelp && help.Stdout.Length > 0, command);
        }
    }

    [TestMethod]
    public async Task ASession_CreatesAnAutomation_RunsIt_AndTheSessionItStartsFindsIt()
    {
        await using var fixture = await Fixture.CreateAsync();

        // Without saying where, it runs in the project of the session and is kept with the user.
        var created = await fixture.One("alta.automation.created", "automation", "create", "--name", "Nightly review", "--trigger", "daily@23:00", "--trigger", "issue@opened+anyone",
            "--model", "codex:gpt@low", "--catch-up", "--content", "Review what changed today.");
        var id = Text(created, "id")!;
        Assert.IsTrue(AutomationDefinition.IsId(id));
        Assert.AreEqual(("Nightly review", "True", "project", fixture.Project.Id, fixture.GlobalPath, "codex:gpt@low"),
            (Text(created, "name"), Text(created, "enabled"), Text(created, "runsIn"), Text(created, "projectId"), Text(created, "file"), Text(created, "model")));
        CollectionAssert.AreEqual(new[] { "daily@23:00", "issue@opened+anyone" }, created.GetProperty("triggers").EnumerateArray().Select(static value => value.GetString()).ToArray());
        Assert.AreEqual(Noon.AddHours(11), created.GetProperty("nextRunAt").GetDateTimeOffset());
        Assert.IsTrue(fixture.Service.Snapshot.Find(id)!.Definition.CatchUp);

        // In the file of its project, from stdin, disabled, as a chat.
        var (code, records, output) = await fixture.Run(["automation", "create", "--name", "Shared", "--store", "project", "--disabled", "--stdin"], "First line.\nSecond line.\n");
        Assert.AreEqual(AltaExitCodes.Success, code, output);
        var stored = Of(records, "alta.automation.created").Single();
        var shared = Text(stored, "id")!;
        Assert.AreEqual((Path.Combine(fixture.Project.ProjectPath, ".alta", "config.toml"), "False", "True"), (Text(stored, "file"), Text(stored, "enabled"), Text(stored, "allowed")));
        Assert.AreEqual("First line.\nSecond line.", fixture.Service.Snapshot.Find(shared)!.Definition.Prompt);
        var chat = Text(await fixture.One("alta.automation.created", "automation", "create", "--name", "A chat", "--chat", "--content", "Say hello."), "id")!;

        foreach (var (arguments, expected) in new (string[], string)[]
                 {
                     (["automation", "create", "--content", "x"], "usage.missingName"),
                     (["automation", "create", "--name", "x"], "usage.missingContent"),
                     (["automation", "create", "--name", "x", "--content", "y", "--stdin"], "usage.contentConflict"),
                     (["automation", "create", "--name", "x", "--content", "y", "--store", "elsewhere"], "usage.invalidStore"),
                     (["automation", "create", "--name", "x", "--content", "y", "--chat", "--store", "project"], "usage.invalidStore"),
                     (["automation", "create", "--name", "x", "--content", "y", "--chat", "--project", fixture.Project.Id], "usage.scopeConflict"),
                     (["automation", "create", "--name", "x", "--content", "y", "--trigger", "sometimes"], "automation.refused"),
                     (["automation", "create", "--name", "x", "--content", "y", "--model", "codex@extreme"], "automation.refused"),
                     (["automation", "list", "--project", fixture.Project.Id, "--chats"], "usage.scopeConflict"),
                     (["automation", "runs", "--limit", "0"], "usage.invalidLimit"),
                     (["automation", "show"], "Missing required argument"),
                 })
        {
            var refused = await fixture.Run(arguments);
            Assert.AreEqual(AltaExitCodes.Usage, refused.Code, string.Join(' ', arguments));
            StringAssert.Contains(refused.Text, expected);
        }

        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.Run(["automation", "create", "--name", "x", "--content", "y", "--project", "nowhere"])).Code);
        Assert.AreEqual(3, fixture.Service.Snapshot.Entries.Count, "A refused command writes nothing.");

        // Listing: all, the ones of a project, the chats.
        var all = await fixture.Run(["automation", "list"]);
        CollectionAssert.AreEqual(new[] { "A chat", "Nightly review", "Shared" }, Of(all.Records, "alta.automation").Select(static record => Text(record, "name")).ToArray());
        Assert.IsTrue(Of(all.Records, "alta.automation").All(static record => Text(record, "prompt") is null), "A list does not carry the prompts.");
        Assert.AreEqual("3", Text(Of(all.Records, "alta.automation.summary").Single(), "count"));
        Assert.AreEqual(2, Of((await fixture.Run(["automation", "list", "--project", fixture.Project.Slug])).Records, "alta.automation").Length);
        Assert.AreEqual(0, Of((await fixture.Run(["automation", "list", "--project", fixture.Other.Id])).Records, "alta.automation").Length);
        Assert.AreEqual(chat, Text(Of((await fixture.Run(["automation", "list", "--chats"])).Records, "alta.automation").Single(), "id"));

        // Shown by the start of its id, with its prompt.
        var shown = await fixture.One("alta.automation", "automation", "show", id[..13]);
        Assert.AreEqual((id, "Review what changed today."), (Text(shown, "id"), Text(shown, "prompt")));
        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.Run(["automation", "show", "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77"])).Code);

        // Run now: the answer comes with the session, and that session finds what started it.
        var run = await fixture.One("alta.automation.run", "automation", "run", id);
        Assert.AreEqual((id, "Nightly review", "manual", "running", "session-1", fixture.Project.Id), (Text(run, "automationId"), Text(run, "name"), Text(run, "trigger"), Text(run, "status"), Text(run, "sessionId"), Text(run, "projectId")));
        var start = await fixture.Runner.NextAsync();
        Assert.AreEqual("Review what changed today.", start.Prompt);
        var current = await fixture.Run(["automation", "current"], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = fixture.Project.Id });
        Assert.AreEqual(AltaExitCodes.Success, current.Code, current.Text);
        var found = Of(current.Records, "alta.automation").Single();
        Assert.AreEqual((id, "Review what changed today."), (Text(found, "id"), Text(found, "prompt")));
        Assert.AreEqual(Text(run, "id"), Text(Of(current.Records, "alta.automation.run").Single(), "id"));
        Assert.AreEqual("session-1", Text(Of((await fixture.Run(["automation", "current", "--session", "session-1"], caller: AltaCallerIdentity.Cli)).Records, "alta.automation.run").Single(), "sessionId"));
        Assert.AreEqual("session-of-the-user", Text(Of((await fixture.Run(["automation", "current"])).Records, "alta.automation.none").Single(), "sessionId"), "The session of the fixture was started by the user.");
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.Run(["automation", "current"], caller: AltaCallerIdentity.Cli)).Code);

        // The session an automation started reads the automations; it neither runs nor changes one.
        var started = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = fixture.Project.Id };
        foreach (var arguments in new string[][]
                 {
                     ["automation", "run", chat], ["automation", "create", "--name", "Again", "--content", "Run again.", "--trigger", "cron@* * * * *"],
                     ["automation", "enable", shared], ["automation", "disable", id], ["automation", "delete", id],
                 })
        {
            var denied = await fixture.Run(arguments, caller: started);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.Code, string.Join(' ', arguments));
            StringAssert.Contains(denied.Text, "automation.startedByAutomation");
        }

        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["automation", "list"], caller: started)).Code);
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["automation", "show", id], caller: started)).Code);
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["automation", "runs", id], caller: started)).Code);
        Assert.AreEqual(3, fixture.Service.Snapshot.Entries.Count);
        Assert.AreEqual(1, fixture.Runner.Starts.Count);

        start.Complete(new(AutomationRun.Completed, null));
        await fixture.UntilAsync(() => !fixture.Service.IsRunning(id));
        var runs = await fixture.Run(["automation", "runs", id, "--limit", "5"]);
        var ended = Of(runs.Records, "alta.automation.run").Single();
        Assert.AreEqual(("completed", "session-1"), (Text(ended, "status"), Text(ended, "sessionId")));
        Assert.AreEqual("1", Text(Of(runs.Records, "alta.automation.run.summary").Single(), "count"));
        Assert.AreEqual(1, Of((await fixture.Run(["automation", "runs"])).Records, "alta.automation.run").Length);

        // A run that cannot start says why, and the command fails.
        fixture.Runner.Refuse = "The provider 'codex' is not enabled.";
        var failed = await fixture.Run(["automation", "run", id]);
        Assert.AreEqual(AltaExitCodes.Failure, failed.Code);
        StringAssert.Contains(failed.Text, "The provider 'codex' is not enabled.");
        Assert.AreEqual("failed", Text(Of(failed.Records, "alta.automation.run").Single(), "status"));

        var disabled = await fixture.One("alta.automation.changed", "automation", "disable", id);
        Assert.AreEqual((id, "False"), (Text(disabled, "id"), Text(disabled, "enabled")));
        Assert.IsFalse(fixture.Service.Snapshot.Find(id)!.Definition.Enabled);
        Assert.AreEqual("True", Text(await fixture.One("alta.automation.changed", "automation", "enable", id), "enabled"));
        Assert.IsTrue(fixture.Service.Snapshot.Find(shared) is { Definition.Enabled: false }, "Another automation is not touched.");

        Assert.AreEqual(id, Text(await fixture.One("alta.automation.deleted", "automation", "delete", id), "id"));
        Assert.IsNull(fixture.Service.Snapshot.Find(id));
        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.Run(["automation", "delete", id])).Code);
        Assert.AreEqual(2, Of((await fixture.Run(["automation", "runs", id])).Records, "alta.automation.run").Length, "Its runs can still be listed.");
    }

    [TestMethod]
    public async Task Rpc_SavesACommandTrigger_ListsIt_AndSaysWhatIsWrongWithItsCommand()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tools = Directory.CreateDirectory(Path.Combine(fixture.Project.ProjectPath, "tools")).FullName;
        AutomationTriggerItem Command(string? command, string? folder) => new("command", 0, 1, [], [], null, "opened", "trusted", command, folder);

        var saved = await fixture.Rpc.SaveAsync(new(Epoch, Input("Watch the build") with { ProjectId = fixture.Project.Id, Triggers = [Command("  gh run watch 123 --exit-status ", " tools ")] }, null), default);
        Assert.AreEqual("ok", saved.Status, saved.Message);
        StringAssert.Contains(File.ReadAllText(fixture.GlobalPath), "triggers = [\n  { type = \"command\", command = \"gh run watch 123 --exit-status\", cwd = \"tools\" },\n]");

        var item = (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single();
        var trigger = item.Triggers.Single();
        Assert.AreEqual(("command", "gh run watch 123 --exit-status", "tools", null, null), (trigger.Type, trigger.Command, trigger.Folder, item.NextRunAt, item.WatchProblem));
        // It is not a time on the clock: there is nothing to preview, and nothing in the day to come.
        var preview = fixture.Rpc.Preview(new(Epoch, trigger));
        Assert.AreEqual(("ok", 0), (preview.Status, preview.Times.Count));
        Assert.AreEqual(0, (await fixture.Rpc.ListAsync(new(Epoch), default)).Upcoming.Count);

        // Saved in the window, its command runs, in the folder it names.
        fixture.Service.Tick(Noon);
        Assert.AreEqual(tools, fixture.Commands.Single("gh run watch 123 --exit-status").Folder);

        // A chat has one too; a folder that does not exist is said on the automation.
        var chat = await fixture.Rpc.SaveAsync(new(Epoch, Input("A chat") with { Triggers = [Command("wait-for-it", "no-such-folder-of-codealta")] }, null), default);
        Assert.AreEqual("ok", chat.Status, chat.Message);
        fixture.Service.Tick(Noon);
        var listed = (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single(candidate => candidate.Id == chat.Id);
        StringAssert.StartsWith(listed.WatchProblem, "The folder of its command does not exist: ");
        Assert.AreEqual((null, null), (listed.Triggers.Single().Expression, listed.ProjectId));

        // The other triggers carry neither a command nor a folder, whatever the page sends.
        var daily = await fixture.Rpc.SaveAsync(new(Epoch, Input("Daily") with { Triggers = [new("daily", 0, 1, ["09:00"], [], null, "opened", "trusted", "left over", "left over")] }, null), default);
        var plain = (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Single(candidate => candidate.Id == daily.Id).Triggers.Single();
        Assert.AreEqual((null, null), (plain.Command, plain.Folder));

        Assert.AreEqual(("refused", "A command trigger has a command."), Outcome(await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Triggers = [Command(" ", null)] }, null), default)));
        Assert.AreEqual(("refused", "A command trigger has a command."), Outcome(await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Triggers = [Command(null, null)] }, null), default)));
        Assert.AreEqual("refused", (await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Triggers = [Command("one\ntwo", null)] }, null), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Triggers = [Command(new string('x', AutomationTrigger.MaximumCommandLength * 2 + 1), null)] }, null), default)).Status);
        Assert.AreEqual(3, (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Count);
    }

    [TestMethod]
    public async Task ASession_CreatesACommandTrigger_UnlessTheHostHasTheUserReviewItsCommands()
    {
        await using (var fixture = await Fixture.CreateAsync())
        {
            var created = await fixture.One("alta.automation.created", "automation", "create", "--name", "Watch the build", "--chat", "--trigger", "command@gh run watch 123 --exit-status",
                "--content", "Say what the build did.");
            CollectionAssert.AreEqual(new[] { "command@gh run watch 123 --exit-status" }, created.GetProperty("triggers").EnumerateArray().Select(static value => value.GetString()).ToArray());
            var trigger = fixture.Service.Snapshot.Find(Text(created, "id")!)!.Definition.Triggers.Single();
            Assert.AreEqual((AutomationTriggerKind.Command, "gh run watch 123 --exit-status", null), (trigger.Kind, trigger.Command, trigger.Folder));
            StringAssert.Contains((await fixture.Alta.InvokeAsync(["automation", "--help"])).Stdout, "command@<command line>");

            var empty = await fixture.Run(["automation", "create", "--name", "x", "--content", "y", "--trigger", "command@"]);
            Assert.AreEqual(AltaExitCodes.Usage, empty.Code);
            StringAssert.Contains(empty.Text, "A command trigger has a command.");
        }

        // The command of a trigger is one nobody reviewed: such a host takes none from a session.
        await using var review = await Fixture.CreateAsync(acceptsCommands: false);
        var denied = await review.Run(["automation", "create", "--name", "Watch", "--chat", "--trigger", "daily@09:00", "--trigger", "command@wait-for-it", "--content", "x"]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.Code, denied.Text);
        StringAssert.Contains(denied.Text, "automation.commandDenied");
        StringAssert.Contains(denied.Text, "The user creates it in the Automations tab.");
        Assert.AreEqual(0, review.Service.Snapshot.Entries.Count, "Nothing is written.");
        Assert.AreEqual(AltaExitCodes.Success, (await review.Run(["automation", "create", "--name", "Daily", "--chat", "--trigger", "daily@09:00", "--content", "x"])).Code);

        // The user still writes one in the window. A session does not start its command; it may end it.
        var input = Input("By the user") with { Enabled = false, Triggers = [new("command", 0, 1, [], [], null, "opened", "trusted", "wait-for-it", null)] };
        var id = (await review.Rpc.SaveAsync(new(Epoch, input, null), default)).Id!;
        var enable = await review.Run(["automation", "enable", id]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, enable.Code, enable.Text);
        StringAssert.Contains(enable.Text, "automation.commandDenied");
        Assert.IsFalse(review.Service.Snapshot.Find(id)!.Definition.Enabled);
        Assert.AreEqual("ok", (await review.Rpc.SetEnabledAsync(new(Epoch, id, true), default)).Status);
        review.Service.Tick(Noon);
        var command = review.Commands.Single("wait-for-it");
        Assert.AreEqual("False", Text(await review.One("alta.automation.changed", "automation", "disable", id), "enabled"));
        review.Service.Tick(Noon);
        Assert.IsTrue(command.Killed);
        Assert.AreEqual(id, Text(await review.One("alta.automation.deleted", "automation", "delete", id), "id"));
        Assert.AreEqual(1, review.Commands.Count);
    }

    [TestMethod]
    public async Task ASessionWhoseCommandsAreReviewed_GivesAnAutomationNoCommand_WhileAnotherOfTheSameHostDoes()
    {
        // The host says it for each caller: the session of the fixture is reviewed, the others are not.
        await using var fixture = await Fixture.CreateAsync(policy: new AltaCommandReviewPolicy(AcceptsCommands: true)
            { AcceptsCommandsOf = static sessionId => sessionId != "session-of-the-user" });
        var denied = await fixture.Run(["automation", "create", "--name", "Watch", "--chat", "--trigger", "daily@09:00", "--trigger", "Command @wait-for-it", "--content", "x"]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.Code, denied.Text);
        StringAssert.Contains(denied.Text, "automation.commandDenied");
        Assert.AreEqual(0, fixture.Service.Snapshot.Entries.Count, "Nothing is written.");
        // An automation without a command is one it may create.
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["automation", "create", "--name", "Daily", "--chat", "--trigger", "daily@09:00", "--content", "x"])).Code);

        // One the user wrote: the session does not start its command, another session of the same host does, and
        // the reviewed one may end it.
        var input = Input("By the user") with { Enabled = false, Triggers = [new("command", 0, 1, [], [], null, "opened", "trusted", "wait-for-it", null)] };
        var id = (await fixture.Rpc.SaveAsync(new(Epoch, input, null), default)).Id!;
        var enable = await fixture.Run(["automation", "enable", id]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, enable.Code, enable.Text);
        StringAssert.Contains(enable.Text, "automation.commandDenied");
        Assert.IsFalse(fixture.Service.Snapshot.Find(id)!.Definition.Enabled);
        var other = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "another-session", SourceProjectId = fixture.Project.Id };
        Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["automation", "enable", id], caller: other)).Code);
        Assert.IsTrue(fixture.Service.Snapshot.Find(id)!.Definition.Enabled);
        Assert.AreEqual("False", Text(await fixture.One("alta.automation.changed", "automation", "disable", id), "enabled"));
    }

    [TestMethod]
    [DataRow("default", "create")]
    [DataRow("default", "enable")]
    [DataRow("default", "run")]
    [DataRow("acceptEdits", "create")]
    [DataRow("acceptEdits", "enable")]
    [DataRow("acceptEdits", "run")]
    [DataRow("unknown-session", "create")]
    [DataRow("unknown-session", "enable")]
    [DataRow("unknown-session", "run")]
    public Task SameAsCreator_RefusesDeferredAutomationWorkFromAReviewedOrUnknownSession(string mode, string verb)
        => WithPermissionCallerAsync(mode, async (fixture, host, caller) =>
        {
            // User-owned automations have no inherited mode. Even a disabled one cannot be enabled or run
            // to hand work to a less-reviewed session; the user can still manage it in the window.
            var saved = await fixture.Rpc.SaveAsync(new(Epoch, Input("By the user") with { Enabled = false }, null), default);
            Assert.AreEqual("ok", saved.Status, saved.Message);
            var before = File.ReadAllText(fixture.GlobalPath);
            fixture.Runner.Refuse = "The admission guard should not reach the runner.";
            var arguments = verb == "create"
                ? new[] { "automation", "create", "--name", "Deferred", "--chat", "--trigger", "daily@09:00", "--content", "Work." }
                : new[] { "automation", verb, saved.Id! };

            var denied = await fixture.Run(arguments, caller: caller);

            Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.Code, denied.Text);
            StringAssert.Contains(denied.Text, "automation.permissionDenied");
            StringAssert.Contains(denied.Text, "The user creates, enables or runs it in the Automations tab.");
            Assert.AreEqual(before, File.ReadAllText(fixture.GlobalPath), "Refusal changes no definition or enabled state.");
            Assert.AreEqual(0, fixture.Runner.Starts.Count);
            Assert.AreEqual(0, fixture.Commands.Count);
            Assert.AreEqual(0, fixture.Service.Runs(null, 10).Count);
            Assert.IsFalse(fixture.Service.Snapshot.Find(saved.Id!)!.Definition.Enabled);
        });

    [TestMethod]
    [DataRow("bypassPermissions", true, true, false, false)]
    [DataRow("default", true, false, false, false)]
    [DataRow("default", false, true, true, false)]
    [DataRow("default", true, true, false, true)]
    public Task SameAsCreator_PreservesBypassDefaultLegacyAndNoSessionCallers(string mode, bool modes, bool inherit, bool review, bool noSession)
        => WithPermissionCallerAsync(mode, async (fixture, host, sessionCaller) =>
        {
            var caller = noSession ? AltaCallerIdentity.Cli : sessionCaller;
            var created = await fixture.Run(["automation", "create", "--name", "Compatible", "--chat", "--disabled", "--trigger", "daily@09:00", "--content", "Work."], caller: caller);
            Assert.AreEqual(AltaExitCodes.Success, created.Code, created.Text);
            var id = Text(Of(created.Records, "alta.automation.created").Single(), "id")!;
            Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(["automation", "enable", id], caller: caller)).Code);
            // Reaching a deliberately refusing fake proves admission without executing a session or command.
            fixture.Runner.Refuse = "Controlled runner refusal.";
            var run = await fixture.Run(["automation", "run", id], caller: caller);
            Assert.AreEqual(AltaExitCodes.Failure, run.Code, run.Text);
            StringAssert.Contains(run.Text, "Controlled runner refusal.");
            Assert.AreEqual(1, fixture.Runner.Starts.Count);
            Assert.AreEqual(0, fixture.Commands.Count);
        }, sessionPermissionModes: modes, inherit: () => inherit, review: () => review);

    [TestMethod]
    public Task WhatASessionCanUse_ListsThePluginsOfItsSpaceThatAreTurnedOn_AlsoWhenNoPluginRuns()
        => WithPermissionCallerAsync(SessionPermissionModes.Bypass, async (fixture, host, caller) =>
        {
            // A window with explicit roots starts no plugin: what its settings turn on is still what the session has.
            Assert.AreEqual(0, host.PluginRuntime.ActivePlugins.Count);
            await File.WriteAllTextAsync(Path.Combine(host.CatalogOptions.GlobalRoot!, "config.toml"),
                "[plugins.jira]\nspaces = [\"work\"]\n\n[plugins.git]\nenabled = false\n");
            var resources = await new DesktopSessionResources(host, null, DesktopPlugins.BuiltIns).ReadAsync(caller.SourceSessionId!, default);

            Assert.IsNotNull(resources);
            Assert.AreEqual("default", resources.SpaceId);
            CollectionAssert.Contains(resources.Plugins.ToArray(), "MCP");
            CollectionAssert.Contains(resources.Plugins.ToArray(), "Statistics");
            CollectionAssert.DoesNotContain(resources.Plugins.ToArray(), "Jira", "Jira is in another space.");
            CollectionAssert.DoesNotContain(resources.Plugins.ToArray(), "Git", "Git is turned off.");
        });

    [TestMethod]
    public Task ASessionWithoutTheShell_StartsNoCommand_InAJobOrAnAutomation()
        => WithPermissionCallerAsync(SessionPermissionModes.Bypass, async (fixture, host, caller) =>
        {
            File.WriteAllText(Path.Combine(host.CatalogOptions.GlobalRoot!, "config.toml"), "[tools]\ndisabled = [\"shell_command\"]\n");

            var command = await fixture.Run(["automation", "create", "--name", "Watch", "--chat", "--trigger", "command@gh run watch 1", "--content", "x"], caller: caller);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, command.Code, command.Text);
            StringAssert.Contains(command.Text, "automation.shellDisabled");
            StringAssert.Contains(command.Text, "`shell_command` is turned off for this session");
            Assert.AreEqual(0, fixture.Service.Snapshot.Entries.Count, "Nothing is written.");
            // Nor one without a command: its sessions have the tools of their own space and agent prompt.
            var daily = await fixture.Run(["automation", "create", "--name", "Daily", "--chat", "--trigger", "daily@09:00", "--content", "x"], caller: caller);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, daily.Code, daily.Text);
            StringAssert.Contains(daily.Text, "settings.toolsDisabled");
            Assert.AreEqual(0, fixture.Service.Snapshot.Entries.Count);
            var saved = await fixture.Rpc.SaveAsync(new(Epoch, Input("User command") with
            {
                Enabled = false, Triggers = [new("command", 0, 1, [], [], null, "opened", "trusted", "never-run", null)],
            }, null), default);
            var enable = await fixture.Run(["automation", "enable", saved.Id!], caller: caller);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, enable.Code, enable.Text);
            StringAssert.Contains(enable.Text, "automation.shellDisabled");

            var job = await fixture.Run(["job", "start", "--command", "echo hi"], caller: caller);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, job.Code, job.Text);
            StringAssert.Contains(job.Text, "job.shellDisabled");
            Assert.AreEqual(0, fixture.Commands.Count);
        });

    [TestMethod]
    public Task ASessionWithoutTheShell_GivesItToNoSession_AndChangesNoSettingItsToolsComeFrom()
        => WithPermissionCallerAsync(SessionPermissionModes.Bypass, async (fixture, host, caller) =>
        {
            var prompts = Directory.CreateDirectory(Path.Combine(host.CatalogOptions.GlobalRoot!, "prompts", "agents")).FullName;
            File.WriteAllText(Path.Combine(prompts, "reader.prompt.md"), "---\nname: Reader\ndisabled_tools: [shell_command]\n---\nRead only.\n");
            async Task Refused(string code, params string[] arguments)
            {
                var refused = await fixture.Run(arguments, stdin: "---\nname: X\n---\nX.\n", caller: caller);
                Assert.AreEqual(AltaExitCodes.PolicyDenied, refused.Code, string.Join(' ', arguments) + ": " + refused.Text);
                StringAssert.Contains(refused.Text, code, string.Join(' ', arguments));
            }

            // A session narrows itself, and does not widen back.
            var narrowed = await fixture.Run(["session", "set_agent", "--prompt-id", "reader"], caller: caller);
            Assert.AreEqual(AltaExitCodes.Success, narrowed.Code, narrowed.Text);
            await Refused("session.widerTools", "session", "set_agent", "--prompt-id", "default");

            // A session it creates does without what it does without.
            await Refused("session.widerTools", "session", "create", "--global", "--title", "Child");
            var child = await fixture.Run(["session", "create", "--global", "--title", "Child", "--prompt-id", "reader"], caller: caller);
            Assert.AreEqual(AltaExitCodes.Success, child.Code, child.Text);

            // It hands no prompt to a session that has the shell, which would run the commands for it.
            var wide = (await host.RuntimeService.CreateGlobalSessionAsync(new SessionExecutionOptions
            {
                ProviderId = new ModelProviderId("automation-fixture"), WorkingDirectory = host.CatalogOptions.GlobalRoot!,
                OnPermissionRequest = host.RuntimeService.Permissions.OwnedDefaultPermissionHandler,
            }, "Has the shell", caller.SourceSessionId, createdBy: null)).SessionId;
            await Refused("session.widerTools", "session", "send", wide, "--stdin");
            await Refused("session.widerTools", "session", "request", wide, "--stdin");
            await Refused("session.widerTools", "reminder", "create", "--session", wide, "--duration", "60", "--content", "Run it.");
            await Refused("session.widerTools", "session", "fork", wide);

            // Nor does it change what its tools, and those of other sessions, come from.
            await Refused("settings.toolsDisabled", "prompt", "create", "free", "--scope", "global", "--stdin");
            await Refused("settings.toolsDisabled", "prompt", "edit", "reader", "--scope", "global", "--stdin");
            await Refused("settings.toolsDisabled", "space", "create", "--name", "Free");
            await Refused("settings.toolsDisabled", "automation", "create", "--name", "Free", "--chat", "--trigger", "daily@09:00", "--content", "x");
            StringAssert.Contains(File.ReadAllText(Path.Combine(prompts, "reader.prompt.md")), "disabled_tools", "The prompt is untouched.");
        }, catalog: true);

    [TestMethod]
    public Task SameAsCreator_ChecksEveryCreateShapeWithoutWeakeningCommandReviewOrNonStartingActions()
        => WithPermissionCallerAsync(SessionPermissionModes.Ask, async (fixture, host, caller) =>
        {
            foreach (var extra in new string[][] { [], ["--disabled"], ["--trigger", "issue@opened"], ["--trigger", "jira@created"], ["--trigger", "daily@09:00", "--disabled"] })
            {
                var denied = await fixture.Run(["automation", "create", "--name", "Deferred", "--chat", "--content", "Work.", .. extra], caller: caller);
                Assert.AreEqual(AltaExitCodes.PolicyDenied, denied.Code, denied.Text);
                StringAssert.Contains(denied.Text, "automation.permissionDenied");
            }

            var command = await fixture.Run(["automation", "create", "--name", "Command", "--chat", "--trigger", "command@never-run", "--content", "Work."], caller: caller);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, command.Code, command.Text);
            StringAssert.Contains(command.Text, "automation.commandDenied", "The raw command review guard remains independent.");
            Assert.AreEqual(0, fixture.Service.Snapshot.Entries.Count);
            var saved = await fixture.Rpc.SaveAsync(new(Epoch, Input("User command") with
            {
                Enabled = false, Triggers = [new("command", 0, 1, [], [], null, "opened", "trusted", "never-run", null)],
            }, null), default);
            Assert.AreEqual("ok", saved.Status, saved.Message);
            var enable = await fixture.Run(["automation", "enable", saved.Id!], caller: caller);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, enable.Code, enable.Text);
            StringAssert.Contains(enable.Text, "automation.commandDenied");
            foreach (var arguments in new string[][] { ["automation", "list"], ["automation", "show", saved.Id!], ["automation", "runs"], ["automation", "disable", saved.Id!], ["automation", "delete", saved.Id!] })
                Assert.AreEqual(AltaExitCodes.Success, (await fixture.Run(arguments, caller: caller)).Code, string.Join(' ', arguments));
            Assert.AreEqual(0, fixture.Runner.Starts.Count);
            Assert.AreEqual(0, fixture.Commands.Count);
        });

    [TestMethod]
    public async Task SameAsCreator_ReadsCurrentSettingsButNeverReliesOnFutureDefaults()
    {
        var inherit = false;
        var review = true;
        await WithPermissionCallerAsync(SessionPermissionModes.Ask, (fixture, host, caller) =>
        {
            var runtime = host.RuntimeService;
            Assert.IsTrue(runtime.AcceptsDeferredPromptFrom(caller.SourceSessionId));
            inherit = true;
            Assert.IsFalse(runtime.AcceptsDeferredPromptFrom(caller.SourceSessionId), "Even matching reviewed defaults may change before the trigger runs.");
            review = false;
            Assert.IsFalse(runtime.AcceptsDeferredPromptFrom(caller.SourceSessionId), "An explicit creator mode remains restrictive.");
            Assert.IsFalse(runtime.AcceptsDeferredPromptFrom("unknown-session"), "An unknown caller is not the application's bypass default.");
            Assert.IsTrue(runtime.AcceptsDeferredPromptFrom(null), "A user's no-session client remains outside agent inheritance.");
            inherit = false;
            Assert.IsTrue(runtime.AcceptsDeferredPromptFrom(caller.SourceSessionId));
            return Task.CompletedTask;
        }, inherit: () => inherit, review: () => review, providerMode: SessionPermissionModes.Ask);
    }

    [TestMethod]
    public void TriggerText_ReadsWhatItWrites_AndSaysWhatIsWrong()
    {
        foreach (var text in new[]
                 {
                     "daily@09:00", "daily@09:00,17:30", "hourly@15", "hourly@0/6", "weekly@mon,thu@08:30", "weekly@sun@00:00,12:00", "cron@0 9 * * 1-5",
                     "issue@opened", "issue@opened+anyone", "pull_request@opened", "pull_request@updated", "pull_request@updated+anyone",
                 })
        {
            Assert.IsTrue(AutomationTriggerText.TryParse(text, out var trigger, out var problem), text + ": " + problem);
            Assert.AreEqual(text, AutomationTriggerText.Format(trigger!));
        }

        // What is written loosely is read, and written back in one form.
        foreach (var (loose, strict) in new[]
                 {
                     (" Daily@17:30, 9:00 ", "daily@09:00,17:30"), ("hourly@5/1", "hourly@5"), ("weekly@Thu,mon,thu@8:30", "weekly@mon,thu@08:30"),
                     ("cron@ 0   9 * * 1-5 ", "cron@0 9 * * 1-5"), ("issue", "issue@opened"), ("pr@updated", "pull_request@updated"), ("PR@opened+ANYONE", "pull_request@opened+anyone"),
                 })
        {
            Assert.IsTrue(AutomationTriggerText.TryParse(loose, out var trigger, out var problem), loose + ": " + problem);
            Assert.AreEqual(strict, AutomationTriggerText.Format(trigger!));
        }

        foreach (var wrong in new[]
                 {
                     "", "sometimes", "daily", "daily@25:00", "daily@noon", "hourly@60", "hourly@x", "hourly@1/2/3", "hourly@0/13", "weekly@mon", "weekly@funday@09:00",
                     "weekly@mon@", "cron@* * *", "cron@61 * * * *", "issue@closed", "issue@updated", "pull_request@merged",
                 })
        {
            Assert.IsFalse(AutomationTriggerText.TryParse(wrong, out var trigger, out var problem), wrong);
            Assert.IsNull(trigger);
            Assert.IsFalse(string.IsNullOrWhiteSpace(problem), wrong);
        }
    }

    [TestMethod]
    public async Task Upcoming_ListsADenseScheduleOncePerSlot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var minute = (await fixture.Rpc.SaveAsync(new(Epoch, Input("Every minute") with { Triggers = [new("cron", 0, 1, [], [], "* * * * *", "opened", "trusted", null, null)] }, null), default)).Id!;
        var twice = (await fixture.Rpc.SaveAsync(new(Epoch, Input("Twice"), null), default)).Id!;
        var manual = (await fixture.Rpc.SaveAsync(new(Epoch, Input("By hand") with { Triggers = [] }, null), default)).Id!;
        var disabled = (await fixture.Rpc.SaveAsync(new(Epoch, Input("Disabled") with { Enabled = false }, null), default)).Id!;

        var upcoming = fixture.Service.Upcoming(TimeSpan.FromHours(24), 24, 1000);

        Assert.AreEqual(24, upcoming.Count(time => time.Id == minute), "One time in each slot, not one for each minute.");
        Assert.AreEqual(Noon.AddMinutes(1), upcoming.First(time => time.Id == minute).At, "The first time of a slot is the one listed.");
        CollectionAssert.AreEqual(new[] { Noon.AddHours(5).AddMinutes(30), Noon.AddHours(21) }, upcoming.Where(time => time.Id == twice).Select(static time => time.At).ToArray());
        Assert.IsFalse(upcoming.Any(time => time.Id == manual || time.Id == disabled));
        CollectionAssert.AreEqual(upcoming.OrderBy(static time => time.At).ToArray(), upcoming.ToArray());
        Assert.AreEqual(5, fixture.Service.Upcoming(TimeSpan.FromHours(24), 24, 5).Count);
        Assert.AreEqual(0, fixture.Service.Upcoming(TimeSpan.FromHours(24), 0, 5).Count);
        fixture.Service.Paused = true;
        Assert.AreEqual(0, fixture.Service.Upcoming(TimeSpan.FromHours(24), 24, 1000).Count);
    }

    [TestMethod]
    public async Task Runner_StartsASessionNamedAfterTheAutomation_ThatTheWorkspaceLinksBackToIt()
    {
        var root = Directory.CreateTempSubdirectory("codealta-automation-runner-").FullName;
        try
        {
            var global = Path.Combine(root, "global");
            var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            var provider = new RunnerProvider();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => new RunnerRuntime(provider)),
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            // The project is in one space: the runs of its automations are there; a chat run is in the default space.
            Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Business")).Succeeded);
            Assert.IsTrue((await host.SpaceCatalog.AssignAsync(project.Id, ["business"], null)).Succeeded);
            var runner = new AutomationRunner(host);
            var source = new AutomationSource(Path.Combine(global, "config.toml"), null, null);
            const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";
            AutomationEntry Entry(AutomationDefinition definition, bool inProject = true) => new(definition, source, inProject ? project.Id : null, inProject ? project.ProjectPath : null, null);

            // What an automation names is checked before a session exists.
            foreach (var (entry, expected) in new (AutomationEntry, string)[]
                     {
                         (Entry(new(Id, "x") { Model = new("missing", null, null) }), "The provider 'missing' is not enabled."),
                         (Entry(new(Id, "x") { Model = new(provider.Descriptor.ProviderId.Value, "other-model", null) }), "The provider 'automation-fixture' does not offer the model 'other-model'."),
                         (Entry(new(Id, "x") { Model = new(provider.Descriptor.ProviderId.Value, "fixture-model", AgentReasoningEffort.XHigh) }), "The model 'fixture-model' has no reasoning effort 'xhigh'."),
                         (Entry(new(Id, "x") { Agent = "nobody" }), "There is no agent prompt 'nobody' in the space 'business'."),
                         (Entry(new(Id, "x") { PermissionMode = "nowhere" }), "The provider 'automation-fixture' does not offer the permission mode 'nowhere'."),
                         (new(new(Id, "x"), source, "unknown-project", projectPath, null), "Its project is no longer one of the projects of CodeAlta."),
                     })
            {
                var refused = await runner.StartAsync(entry, "run-refused", "Prompt.", null, default);
                Assert.AreEqual((null, expected, null), (refused.SessionId, refused.Problem, refused.Completion));
            }

            var workspace = new WorkspaceService(host, Epoch);
            Assert.AreEqual(0, (await workspace.SnapshotAsync(new(), default)).Sessions.Length, "A refused run leaves no session behind.");

            var start = await runner.StartAsync(Entry(new(Id, "Nightly review") { Model = new(provider.Descriptor.ProviderId.Value, null, AgentReasoningEffort.Low), ReviewBeforeCommit = true }),
                "run-1", "Review what changed.", null, default);
            Assert.IsNull(start.Problem);
            Assert.AreEqual((bool?)true, (await host.Commands.GetReviewBeforeCommitAsync(start.SessionId!)).Own, "The session is given the review the automation chose.");
            Assert.IsNotNull(start.Completion);
            var (sessionId, sent) = await provider.Sends.Reader.ReadAsync().AsTask().WaitAsync(Patience);
            Assert.AreEqual(start.SessionId, sessionId);
            Assert.AreEqual("Review what changed.", string.Concat(sent.Input.Items.OfType<AgentInputItem.Text>().Select(static text => text.Value)));
            provider.Release.SetResult();
            Assert.AreEqual(AutomationRun.Completed, (await start.Completion.WaitAsync(Patience)).Status);

            var session = (await workspace.SnapshotAsync(new(), default)).Sessions.Single();
            Assert.AreEqual((start.SessionId, "Nightly review", Id, project.Id, "automation-fixture"), (session.Id, session.Title, session.AutomationId, session.ProjectId, session.ProviderKey));
            Assert.AreEqual("business", session.SpaceId);

            // As a chat, in no project, started by an event: what it was about names the session too.
            var chat = await runner.StartAsync(Entry(new(Id, "A chat"), inProject: false), "run-2", "Say hello.", "#12  Crash\non start", default);
            Assert.IsNull(chat.Problem);
            Assert.AreEqual(AutomationRun.Completed, (await chat.Completion!.WaitAsync(Patience)).Status);
            var sessions = (await workspace.SnapshotAsync(new(), default)).Sessions;
            var started = sessions.Single(candidate => candidate.Id == chat.SessionId);
            Assert.AreEqual((null, Id, "A chat · #12 Crash on start"), (started.ProjectId, started.AutomationId, started.Title));
            Assert.AreEqual("default", started.SpaceId);
            Assert.AreEqual("Triage", AutomationRunner.SessionTitle("Triage", " "));
            Assert.AreEqual(8 + 3 + 80, AutomationRunner.SessionTitle("Triage 1", "#7 " + new string('x', 200)).Length, "A long title of an issue is cut.");
            await workspace.CloseSessionsAsync();
            await workspace.CloseImportsAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task Runner_RunsTheScriptOfAnAutomation_WithItsArgumentsAndTheEventThatStartedIt()
    {
        var root = Directory.CreateTempSubdirectory("codealta-automation-script-").FullName;
        try
        {
            var global = Path.Combine(root, "global");
            var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            var provider = new RunnerProvider();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => new RunnerRuntime(provider)),
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            Assert.IsNull(host.CodeModeScripts.Save("triage", CodeModeScriptScope.Project, """
                export const meta = { name: "triage", description: "Triage.", params: { days: { type: "integer" }, event: { type: "string", default: "" } } };
                return args.days;
                """, project.ProjectPath, null).Refusal);
            var runner = new AutomationRunner(host);
            var source = new AutomationSource(Path.Combine(global, "config.toml"), null, null);
            const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d78";
            AutomationEntry Entry(AutomationDefinition definition) => new(definition, source, project.Id, project.ProjectPath, null);

            // A script that is not in the library, or arguments it refuses, start no session.
            foreach (var (definition, expected) in new (AutomationDefinition, string)[]
                     {
                         (new(Id, "x") { Script = "missing" }, "There is no script 'missing' in the library of its project."),
                         (new(Id, "x") { Script = "triage" }, "The script 'triage' refuses its arguments: the argument `days` is missing."),
                         (new(Id, "x") { Script = "triage", ScriptArguments = "{\"days\": \"one\"}" }, "The script 'triage' refuses its arguments: the argument `days` must be integer, not string."),
                     })
            {
                var refused = await runner.StartAsync(Entry(definition), "run-refused", string.Empty, null, default);
                Assert.AreEqual((null, expected, null), (refused.SessionId, refused.Problem, refused.Completion));
            }

            var start = await runner.StartAsync(Entry(new(Id, "Triage") { Script = "triage", ScriptArguments = "{\"days\": 1}" }), "run-1",
                "\n\n---\nThis automation was started by its command.", null, default);
            Assert.IsNull(start.Problem);
            var (sessionId, sent) = await provider.Sends.Reader.ReadAsync().AsTask().WaitAsync(Patience);
            Assert.AreEqual(start.SessionId, sessionId);
            var text = string.Concat(sent.Input.Items.OfType<AgentInputItem.Text>().Select(static item => item.Value));
            StringAssert.Contains(text, "Run the script `triage` of the library with `exec`");
            StringAssert.Contains(text, "return await workflow(\"triage\", {\"days\":1,\"event\":\"This automation was started by its command.\"});");
            Assert.IsTrue(host.CodeModeRuns.RequiresCodeMode(sessionId), "Its session has exec whatever the setting of Code Mode says.");
            provider.Release.SetResult();
            Assert.AreEqual(AutomationRun.Completed, (await start.Completion!.WaitAsync(Patience)).Status);
            var workspace = new WorkspaceService(host, Epoch);
            await workspace.CloseSessionsAsync();
            await workspace.CloseImportsAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task Workflows_ListTheLibrary_RunAScriptInANewSession_ListTheRuns_AndAskARunToBeSaved()
    {
        var root = Directory.CreateTempSubdirectory("codealta-workflows-").FullName;
        try
        {
            var global = Path.Combine(root, "global");
            var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            var provider = new RunnerProvider();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => new RunnerRuntime(provider)),
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            Assert.IsNull(host.CodeModeScripts.Save("triage", CodeModeScriptScope.Project, """
                export const meta = {
                  name: "triage", title: "Triage issues", description: "Triage.",
                  params: { days: { type: "integer", description: "How far back." }, labels: { type: "array", optional: true }, event: { type: "string", default: "" } },
                  steps: [{ id: "fetch", title: "Fetch" }, { id: "label", title: "Label", each: true }, { id: "report", title: "Report", after: ["fetch", "label"] }],
                };
                return args.days;
                """, project.ProjectPath, null).Refusal);
            var asked = new List<(string SessionId, string Text)>();
            var rpc = new WorkflowsService(host.CodeModeScripts, host.CodeModeRuns, host.ProjectCatalog, new CodeAlta.Desktop.WorkItems.SessionStarter(host, null, () => null),
                (sessionId, text, _) => { asked.Add((sessionId, text)); return Task.FromResult<string?>(null); }, Epoch);
            Assert.AreEqual("unavailable", new WorkflowsService().Runs(new(Epoch)).Status);
            Assert.AreEqual("stale_epoch", (await rpc.ScriptsAsync(new("other", null), default)).Status);
            Assert.AreEqual("stale_epoch", (await rpc.RunAsync(new("other", "triage", project.Id, null), default)).Status);

            // The library of the project: its script with its parameters and its declared steps.
            var listed = await rpc.ScriptsAsync(new(Epoch, project.Id), default);
            Assert.AreEqual("ok", listed.Status);
            var triage = listed.Scripts.Single();
            Assert.AreEqual(("triage", "project", "Triage issues", "Triage.", null), (triage.Name, triage.Scope, triage.Title, triage.Description, triage.Problem));
            Assert.AreEqual("days:integer:True:How far back.:|labels:array:False::|event:string:False::\"\"",
                string.Join('|', triage.Parameters.Select(static parameter => $"{parameter.Name}:{parameter.Type}:{parameter.Required}:{parameter.Description}:{parameter.Default}")));
            Assert.AreEqual("fetch:-:False|label:-:True|report:fetch,label:False",
                string.Join('|', triage.Steps.Select(static step => $"{step.Id}:{(step.After is null ? "-" : string.Join(',', step.After))}:{step.Each}")));
            Assert.AreEqual(0, (await rpc.ScriptsAsync(new(Epoch, null), default)).Scripts.Count, "Without a project, no script of a project.");
            Assert.AreEqual("not_found", (await rpc.ScriptsAsync(new(Epoch, "missing"), default)).Status);

            // Run… checks the script and its arguments as an automation does, before any session exists.
            foreach (var (name, arguments, expected) in new (string, string?, string)[]
                     {
                         ("missing", null, "There is no script 'missing' in the library of its project."),
                         ("triage", null, "The script 'triage' refuses its arguments: the argument `days` is missing."),
                         ("triage", "{\"days\": 1", "The arguments of the script 'triage' are not a JSON object."),
                         ("triage", "[1]", "The arguments of the script 'triage' are not a JSON object."),
                     })
            {
                var refused = await rpc.RunAsync(new(Epoch, name, project.Id, arguments), default);
                Assert.AreEqual(("refused", null, expected), (refused.Status, refused.SessionId, refused.Message));
            }

            Assert.AreEqual("invalid_request", (await rpc.RunAsync(new(Epoch, "Not A Name", project.Id, null), default)).Status);
            var started = await rpc.RunAsync(new(Epoch, "triage", project.Id, "{\"days\": 2}"), default);
            Assert.AreEqual(("ok", null), (started.Status, started.Message));
            var (sessionId, sent) = await provider.Sends.Reader.ReadAsync().AsTask().WaitAsync(Patience);
            Assert.AreEqual(started.SessionId, sessionId);
            var text = string.Concat(sent.Input.Items.OfType<AgentInputItem.Text>().Select(static item => item.Value));
            StringAssert.Contains(text, "Run the script `triage` of the library with `exec`");
            StringAssert.Contains(text, "return await workflow(\"triage\", {\"days\":2});");
            Assert.IsTrue(host.CodeModeRuns.RequiresCodeMode(sessionId), "Its session has exec whatever the setting of Code Mode says.");
            var workspace = new WorkspaceService(host, Epoch);
            Assert.AreEqual("Triage issues", (await workspace.SnapshotAsync(new(), default)).Sessions.Single(session => session.Id == sessionId).Title);
            provider.Release.SetResult();

            // The runs of the host, running first; an ended one says how it ended and when.
            var gate = new TaskCompletionSource<CodeAlta.CodeMode.CodeModeRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = host.CodeModeRuns.TryStart(sessionId, "first", 1000, "call-1",
                (_, _) => Task.FromResult(new CodeAlta.CodeMode.CodeModeRunResult(CodeAlta.CodeMode.CodeModeRunStatus.Failed, TimeSpan.FromSeconds(3))), out var noRefusal);
            Assert.IsNull(noRefusal);
            await ended!.Completion.WaitAsync(Patience);
            var running = host.CodeModeRuns.TryStart(sessionId, "second", 1000, "call-2", (_, token) => gate.Task.WaitAsync(token), out noRefusal);
            Assert.IsNull(noRefusal);
            var runs = rpc.Runs(new(Epoch));
            Assert.AreEqual("ok", runs.Status);
            Assert.AreEqual($"{running!.Id}:second:running:call-2:False|{ended.Id}:first:failed:call-1:True",
                string.Join('|', runs.Runs.Select(static run => $"{run.Id}:{run.Title}:{run.State}:{run.CallId}:{run.EndedAt is not null}")));
            Assert.AreEqual(sessionId, runs.Runs[0].SessionId);
            Assert.AreEqual(TimeSpan.FromSeconds(3), runs.Runs[1].EndedAt - runs.Runs[1].StartedAt);
            gate.SetResult(new(CodeAlta.CodeMode.CodeModeRunStatus.Completed, TimeSpan.FromSeconds(1)));
            await running.Completion.WaitAsync(Patience);

            // Save as script… asks the session of the run, with the name the user gave.
            var bad = await rpc.SaveAsScriptAsync(new(Epoch, sessionId, ended.Id, "Bad Name"), default);
            Assert.AreEqual(("refused", "'Bad Name' is not a script name: lowercase letters, digits and hyphens, starting with a letter."), (bad.Status, bad.Message));
            Assert.AreEqual("invalid_request", (await rpc.SaveAsScriptAsync(new(Epoch, sessionId, "run 1", "count"), default)).Status);
            Assert.AreEqual(0, asked.Count);
            Assert.AreEqual("ok", (await rpc.SaveAsScriptAsync(new(Epoch, sessionId, ended.Id, "first-run"), default)).Status);
            Assert.AreEqual(sessionId, asked.Single().SessionId);
            StringAssert.Contains(asked[0].Text, $"Turn the script of the run `{ended.Id}` into the script `first-run` of the library");
            StringAssert.Contains(asked[0].Text, $"alta script save first-run --from-run {ended.Id}");
            StringAssert.Contains(asked[0].Text, "meta.params");
            StringAssert.Contains(asked[0].Text, "alta script check");

            // Resume and the reruns of the card ask the session of the run too, for a run the record of runs keeps.
            Assert.AreEqual("refused", (await rpc.ResumeAsync(new(Epoch, sessionId, ended.Id, null, false), default)).Status, "No record of runs is kept yet.");
            await host.KeepCodeModeRunsAsync();
            var kept = host.CodeModeRuns.TryStart(sessionId, "kept", 1000, "call-3", new CodeModeRunStart("return 1;"),
                (_, _) => Task.FromResult(new CodeAlta.CodeMode.CodeModeRunResult(CodeAlta.CodeMode.CodeModeRunStatus.Completed, TimeSpan.Zero)), out noRefusal);
            Assert.IsNull(noRefusal);
            await kept!.Completion.WaitAsync(Patience);
            Assert.AreEqual("invalid_request", (await rpc.ResumeAsync(new(Epoch, sessionId, kept.Id, "a", true), default)).Status);
            Assert.AreEqual("refused", (await rpc.ResumeAsync(new(Epoch, "other-session", kept.Id, null, false), default)).Status);
            Assert.AreEqual("ok", (await rpc.ResumeAsync(new(Epoch, sessionId, kept.Id, null, true), default)).Status);
            Assert.AreEqual(sessionId, asked[^1].SessionId);
            StringAssert.Contains(asked[^1].Text, $"// @exec: {{\"resume\": \"{kept.Id}\", \"replay\": \"all\", \"yield_ms\": 600000}}");
            Assert.IsTrue(File.Exists(Path.Combine(CodeModeRunStore.RootOf(host.CatalogOptions), kept.Id, "run.json")), "the record is under the state root");
            await workspace.CloseSessionsAsync();
            await workspace.CloseImportsAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task WorkStartedFromTheWindow_IsInTheSpaceTheWindowShows()
    {
        var root = Directory.CreateTempSubdirectory("codealta-work-space-").FullName;
        try
        {
            var global = Path.Combine(root, "global");
            var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            var provider = new RunnerProvider();
            provider.Release.SetResult();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => new RunnerRuntime(provider)),
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Business")).Succeeded);
            Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Personal")).Succeeded);
            var shown = "personal";
            var starter = new CodeAlta.Desktop.WorkItems.SessionStarter(host, null, () => shown);

            var started = await starter.StartAsync(project, "A task", _ => "Do the task.", false, null, "task", null);
            Assert.IsNull(started.Problem);
            Assert.AreEqual("personal", (await host.RuntimeService.GetSessionSpaceAsync(started.SessionId!))!.SpaceId);
            await provider.Sends.Reader.ReadAsync().AsTask().WaitAsync(Patience);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task RunsAndWork_StartWithAProviderOfTheirSpace_AndOneNamedOutsideItIsRefused()
    {
        var root = Directory.CreateTempSubdirectory("codealta-provider-space-").FullName;
        try
        {
            var global = Path.Combine(root, "global");
            var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            // The default provider is the one of Personal; Business has an account of its own.
            File.WriteAllText(Path.Combine(global, "config.toml"), """
                [chat]
                default_provider = "automation-fixture"

                [providers.automation-fixture]
                type = "codex"
                spaces = ["personal"]

                [providers.business-account]
                type = "codex"
                spaces = ["business"]
                """);
            var personal = new RunnerProvider();
            var business = new RunnerProvider("business-account");
            personal.Release.SetResult();
            business.Release.SetResult();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                ConfigureModelProviders = registry =>
                {
                    registry.RegisterOrReplace(personal.Descriptor, () => new RunnerRuntime(personal));
                    registry.RegisterOrReplace(business.Descriptor, () => new RunnerRuntime(business));
                },
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Business")).Succeeded);
            Assert.IsTrue((await host.SpaceCatalog.CreateAsync("Personal")).Succeeded);
            Assert.IsTrue((await host.SpaceCatalog.AssignAsync(project.Id, ["business"], null)).Succeeded);
            var workspace = new WorkspaceService(host, Epoch);
            async Task<string?> ProviderOf(string? sessionId) => (await workspace.SnapshotAsync(new(), default)).Sessions.Single(session => session.Id == sessionId).ProviderKey;

            // A run of the project of Business: the account of Business, since the default provider is not there.
            var runner = new AutomationRunner(host);
            var source = new AutomationSource(Path.Combine(global, "config.toml"), null, null);
            const string Id = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";
            var run = await runner.StartAsync(new(new(Id, "Review"), source, project.Id, project.ProjectPath, null), "run-1", "Review.", null, default);
            Assert.IsNull(run.Problem);
            Assert.AreEqual("business-account", await ProviderOf(run.SessionId));
            var named = await runner.StartAsync(new(new(Id, "Review") { Model = new("automation-fixture", null, null) }, source, project.Id, project.ProjectPath, null), "run-2", "Review.", null, default);
            Assert.AreEqual("The provider 'automation-fixture' is not available in the space 'business'.", named.Problem);
            Assert.IsNull(named.SessionId);

            // Work started from the window is in the space shown, with a provider of it.
            var shown = "business";
            var starter = new CodeAlta.Desktop.WorkItems.SessionStarter(host, null, () => shown);
            var work = await starter.StartAsync(project, "A task", _ => "Do the task.", false, null, "task", null);
            Assert.IsNull(work.Problem);
            Assert.AreEqual("business-account", await ProviderOf(work.SessionId));
            var asked = await starter.StartAsync(project, "A task", _ => "Do the task.", false, null, "task", new(new("automation-fixture"), null));
            Assert.AreEqual("The provider 'automation-fixture' is not available in the space 'business'.", asked.Problem);
            shown = "personal";
            var there = await starter.StartAsync(project, "A task", _ => "Do the task.", false, null, "task", null);
            Assert.AreEqual("automation-fixture", await ProviderOf(there.SessionId));
            await workspace.CloseSessionsAsync();
            await workspace.CloseImportsAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task Runner_WhenEveryCommandTheHostKeepsIsPending_StartsNoSession_AndRunsAgainOnceOneHasSettled()
    {
        var root = Directory.CreateTempSubdirectory("codealta-automation-capacity-").FullName;
        try
        {
            var global = Path.Combine(root, "global");
            var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home");
            var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            var provider = new RunnerProvider();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false, OwnedCommandReceiptCapacity = 1,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => new RunnerRuntime(provider)),
            });
            var runner = new AutomationRunner(host);
            var entry = new AutomationEntry(new("0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77", "Every minute") { Prompt = "p" }, new(Path.Combine(global, "config.toml"), null, null), null, null, null);

            // The prompt of the first run is still being sent: it is the one command this host keeps.
            var first = await runner.StartAsync(entry, "run-1", "p", null, default);
            Assert.IsNull(first.Problem);
            var second = await runner.StartAsync(entry, "run-2", "p", null, default);

            Assert.AreEqual((null, "Too many commands are pending in CodeAlta. The automation runs again at its next trigger.", null), (second.SessionId, second.Problem, second.Completion));
            var workspace = new WorkspaceService(host, Epoch);
            Assert.AreEqual(1, (await workspace.SnapshotAsync(new(), default)).Sessions.Length, "The run that could not send its prompt left no session behind.");

            // Once that command has settled its receipt makes room: the host accepts commands for as long as it runs.
            provider.Release.SetResult();
            Assert.AreEqual(AutomationRun.Completed, (await first.Completion!.WaitAsync(Patience)).Status);
            for (var index = 3; index < 6; index++)
            {
                var next = await runner.StartAsync(entry, "run-" + index, "p", null, default);
                Assert.IsNull(next.Problem);
                Assert.AreEqual(AutomationRun.Completed, (await next.Completion!.WaitAsync(Patience)).Status);
            }

            Assert.AreEqual(4, (await workspace.SnapshotAsync(new(), default)).Sessions.Length);
            await workspace.CloseSessionsAsync();
            await workspace.CloseImportsAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AutomationInput Input(string name)
        => new(null, name, true, "Do the thing.", null, null, null, null, null, null, null, null, false, [new("daily", 0, 1, ["17:30", "09:00"], [], null, "opened", "trusted", null, null)]);

    private static (string Status, string? Message) Outcome(AutomationMutationResponse response) => (response.Status, response.Message);

    private static string? Text(JsonElement record, string name) => record.TryGetProperty(name, out var value) ? value.ToString() : null;

    // The records of one type: a command also writes a record of its result.
    private static JsonElement[] Of(List<JsonElement> records, string type) => [.. records.Where(record => Text(record, "type") == type)];

    // Only in-memory provider sessions and disposable catalog/configuration roots: no model sends, credentials,
    // plugins, automation timers or real commands. The caller uses the actual runtime's permission policy.
    private static async Task WithPermissionCallerAsync(string mode, Func<Fixture, CodeAltaHost, AltaCallerIdentity, Task> test,
        bool sessionPermissionModes = true, Func<bool>? inherit = null, Func<bool>? review = null, string? providerMode = null, bool catalog = false)
    {
        var root = Directory.CreateTempSubdirectory("codealta-automation-permissions-").FullName;
        try
        {
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var builtin = Directory.CreateDirectory(Path.Combine(root, "builtin")).FullName;
            var provider = new RunnerProvider();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = project, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                SessionPermissionModes = sessionPermissionModes, InheritPermissionModePolicy = inherit ?? (() => true),
                ReviewOwnedPermissionsPolicy = review ?? (() => false),
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor with { DefaultPermissionMode = providerMode }, () => new RunnerRuntime(provider)),
            });
            var runtime = host.RuntimeService;
            var sessionId = "unknown-session";
            if (mode != sessionId)
            {
                sessionId = (await runtime.CreateGlobalSessionAsync(new SessionExecutionOptions
                {
                    ProviderId = provider.Descriptor.ProviderId, WorkingDirectory = global, PermissionMode = mode,
                    OnPermissionRequest = runtime.Permissions.OwnedDefaultPermissionHandler,
                }, "Automation caller")).SessionId;
            }

            await using var fixture = await Fixture.CreateAsync(runtime: runtime, policy: new AltaCommandReviewPolicy(AcceptsCommands: true)
            {
                AcceptsCommandsOf = id => runtime.GetPermissionPolicy(id ?? string.Empty) == SessionPermissionPolicy.Approve,
            }, catalog: catalog ? host.CatalogOptions : null, spaces: catalog ? host.SpaceCatalog : null);
            await test(fixture, host, new AltaCallerIdentity { Kind = "agent", SourceSessionId = sessionId });
            Assert.IsFalse(provider.Sends.Reader.TryRead(out _), "The permission fixture never sends to a model.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // The automations of an application whose sessions are fakes, over a real project catalog: the page's service
    // and the alta commands on the same automations.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project, ProjectDescriptor other, bool acceptsCommands, AltaCommandReviewPolicy? policy, SessionRuntimeService? runtime, CatalogOptions? catalog, SpaceCatalog? spaces)
        {
            (_root, Project, Other) = (root, project, other);
            Service = new AutomationService(GlobalPath, token => projects.LoadAsync(token), new AutomationStateStore(Path.Combine(root, "state", "automations.json"), false),
                Runner, new AutomationClock { Now = Noon }, TimeZoneInfo.Utc, commands: Commands);
            Rpc = new AutomationsService(Service, projects, Epoch);
            var services = new AltaServiceCollection().Add(projects).Add<IAltaAutomations>(new DesktopAltaAutomations(Service, projects, acceptsCommands));
            if (policy is not null) services.Add(policy);
            if (runtime is not null) services.Add(runtime);
            if (catalog is not null) services.Add(catalog);
            if (spaces is not null) services.Add(spaces);
            var registry = new AltaCommandRegistry();
            Alta = new AltaCommandDispatcher(registry, services);
            services.Add(registry).Add(Alta);
            Session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-of-the-user", SourceProjectId = project.Id };
        }

        public string GlobalPath => Path.Combine(_root, "global", "config.toml");
        public ProjectDescriptor Project { get; }
        public ProjectDescriptor Other { get; }
        public FakeAutomationRunner Runner { get; } = new();
        public FakeAutomationCommands Commands { get; } = new();
        public AutomationService Service { get; }
        public AutomationsService Rpc { get; }
        public AltaCommandDispatcher Alta { get; }
        public AltaCallerIdentity Session { get; }

        /// <param name="acceptsCommands">False for a host that has the user review the commands of its sessions.</param>
        public static async Task<Fixture> CreateAsync(bool acceptsCommands = true, AltaCommandReviewPolicy? policy = null, SessionRuntimeService? runtime = null, CatalogOptions? catalog = null, SpaceCatalog? spaces = null)
        {
            var root = Directory.CreateTempSubdirectory("codealta-automation-host-").FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "other")).FullName);
            var fixture = new Fixture(root, projects, project, other, acceptsCommands, policy, runtime, catalog, spaces);
            await fixture.Service.RefreshAsync();
            return fixture;
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

        public async Task<JsonElement> One(string type, params string[] arguments)
        {
            var (code, records, text) = await Run(arguments);
            Assert.AreEqual(AltaExitCodes.Success, code, text);
            return records.Single(record => record.GetProperty("type").GetString() == type);
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 1000 && !condition(); attempt++) await Task.Delay(10);
            Assert.IsTrue(condition());
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class RunnerProvider(string key = "automation-fixture")
    {
        internal ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId(key), "Automation fixture") { IsDefault = true, DefaultModelId = "fixture-model" };
        internal Channel<(string SessionId, AgentSendOptions Options)> Sends { get; } = Channel.CreateUnbounded<(string, AgentSendOptions)>();
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RunnerRuntime(RunnerProvider provider) : IModelProviderSessionRuntime
    {
        public ModelProviderDescriptor Descriptor => provider.Descriptor;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelProviderProbeResult
            {
                ProviderId = provider.Descriptor.ProviderId,
                Models = [new AgentModelInfo("fixture-model", SupportedReasoningEfforts: [AgentReasoningEffort.Low, AgentReasoningEffort.High])],
            });
        public IModelProviderTurnExecutor CreateTurnExecutor() => throw new InvalidOperationException("No turn executor.");
        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new RunnerSession(provider, options.SessionId!, options.WorkingDirectory));
        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult<IAgentSession>(new RunnerSession(provider, sessionId, options.WorkingDirectory));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RunnerSession(RunnerProvider provider, string sessionId, string? folder) : IAgentSession
    {
        public ModelProviderId ProviderId => provider.Descriptor.ProviderId;
        public string SessionId => sessionId;
        public string? WorkspacePath => folder;
        public async IAsyncEnumerable<AgentEvent> StreamEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public IDisposable Subscribe(Action<AgentEvent> handler) => new Subscription();
        public async Task<AgentRunId> SendAsync(AgentSendOptions options, CancellationToken cancellationToken = default)
        {
            provider.Sends.Writer.TryWrite((sessionId, options));
            await provider.Release.Task.WaitAsync(cancellationToken);
            return new("fixture-run");
        }

        public Task AbortAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<AgentRunId> SteerAsync(AgentSteerOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No steering.");
        public Task CompactAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No compaction.");
        public Task<IReadOnlyList<AgentEvent>> GetHistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentEvent>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Subscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

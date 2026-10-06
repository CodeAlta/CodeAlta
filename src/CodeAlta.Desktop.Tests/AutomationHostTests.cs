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
        var saved = await fixture.Rpc.SaveAsync(new(Epoch, Input("  Morning brief  ") with { ProjectId = fixture.Project.Id, Provider = "codex", Model = "gpt", Effort = "High" }, null), default);
        Assert.AreEqual("ok", saved.Status, saved.Message);
        Assert.IsTrue(AutomationDefinition.IsId(saved.Id));
        var text = File.ReadAllText(fixture.GlobalPath);
        StringAssert.Contains(text, $"[automations.{saved.Id}]");
        StringAssert.Contains(text, "model = \"codex:gpt@high\"");
        StringAssert.Contains(text, $"project = \"{fixture.Project.ProjectPath.Replace('\\', '/')}\"");

        var listed = await fixture.Rpc.ListAsync(new(Epoch), default);
        var item = listed.Items.Single();
        Assert.AreEqual((saved.Id, "Morning brief", true, "Do the thing.", fixture.Project.Id, fixture.Project.DisplayName, fixture.Project.ProjectPath),
            (item.Id, item.Name, item.Enabled, item.Prompt, item.ProjectId, item.ProjectName, item.ProjectFolder));
        Assert.AreEqual((null, fixture.GlobalPath, "codex", "gpt", "High", null, false), (item.StoreProjectId, item.File, item.Provider, item.Model, item.Effort, item.Agent, item.CatchUp));
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
        Assert.AreEqual("invalid_request", (await fixture.Rpc.SaveAsync(new(Epoch, Input("x") with { Triggers = [new("sometimes", 0, 1, [], [], null, "opened", "trusted")] }, null), default)).Status);
        Assert.AreEqual(1, (await fixture.Rpc.ListAsync(new(Epoch), default)).Items.Count);
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
        var preview = fixture.Rpc.Preview(new(Epoch, new("hourly", 15, 6, [], [], null, "opened", "trusted")));
        Assert.AreEqual("ok", preview.Status);
        CollectionAssert.AreEqual(new[] { 12, 18, 0, 6, 12 }, preview.Times.Select(static time => time.Hour).ToArray());
        Assert.IsTrue(preview.Times.All(static time => time.Minute == 15));
        var wrong = fixture.Rpc.Preview(new(Epoch, new("cron", 0, 1, [], [], "61 * * * *", "opened", "trusted")));
        Assert.AreEqual("refused", wrong.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(wrong.Message));
        Assert.AreEqual("invalid_request", fixture.Rpc.Preview(new(Epoch, new("daily", 0, 1, ["25:00"], [], null, "opened", "trusted"))).Status);
        Assert.AreEqual("stale_epoch", fixture.Rpc.Preview(new("other", new("daily", 0, 1, ["09:00"], [], null, "opened", "trusted"))).Status);

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
        var minute = (await fixture.Rpc.SaveAsync(new(Epoch, Input("Every minute") with { Triggers = [new("cron", 0, 1, [], [], "* * * * *", "opened", "trusted")] }, null), default)).Id!;
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
                         (Entry(new(Id, "x") { Agent = "nobody" }), "There is no agent prompt 'nobody'."),
                         (new(new(Id, "x"), source, "unknown-project", projectPath, null), "Its project is no longer one of the projects of CodeAlta."),
                     })
            {
                var refused = await runner.StartAsync(entry, "run-refused", "Prompt.", null, default);
                Assert.AreEqual((null, expected, null), (refused.SessionId, refused.Problem, refused.Completion));
            }

            var workspace = new WorkspaceService(host, Epoch);
            Assert.AreEqual(0, (await workspace.SnapshotAsync(new(), default)).Sessions.Length, "A refused run leaves no session behind.");

            var start = await runner.StartAsync(Entry(new(Id, "Nightly review") { Model = new(provider.Descriptor.ProviderId.Value, null, AgentReasoningEffort.Low) }), "run-1", "Review what changed.", null, default);
            Assert.IsNull(start.Problem);
            Assert.IsNotNull(start.Completion);
            var (sessionId, sent) = await provider.Sends.Reader.ReadAsync().AsTask().WaitAsync(Patience);
            Assert.AreEqual(start.SessionId, sessionId);
            Assert.AreEqual("Review what changed.", string.Concat(sent.Input.Items.OfType<AgentInputItem.Text>().Select(static text => text.Value)));
            provider.Release.SetResult();
            Assert.AreEqual(AutomationRun.Completed, (await start.Completion.WaitAsync(Patience)).Status);

            var session = (await workspace.SnapshotAsync(new(), default)).Sessions.Single();
            Assert.AreEqual((start.SessionId, "Nightly review", Id, project.Id, "automation-fixture"), (session.Id, session.Title, session.AutomationId, session.ProjectId, session.ProviderKey));

            // As a chat, in no project, started by an event: what it was about names the session too.
            var chat = await runner.StartAsync(Entry(new(Id, "A chat"), inProject: false), "run-2", "Say hello.", "#12  Crash\non start", default);
            Assert.IsNull(chat.Problem);
            Assert.AreEqual(AutomationRun.Completed, (await chat.Completion!.WaitAsync(Patience)).Status);
            var sessions = (await workspace.SnapshotAsync(new(), default)).Sessions;
            var started = sessions.Single(candidate => candidate.Id == chat.SessionId);
            Assert.AreEqual((null, Id, "A chat · #12 Crash on start"), (started.ProjectId, started.AutomationId, started.Title));
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
    public async Task Runner_WhenTheHostAcceptsNoMoreCommands_StartsNoSession()
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
            provider.Release.SetResult();
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false, OwnedCommandReceiptCapacity = 1,
                ConfigureModelProviders = registry => registry.RegisterOrReplace(provider.Descriptor, () => new RunnerRuntime(provider)),
            });
            var runner = new AutomationRunner(host);
            var entry = new AutomationEntry(new("0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77", "Every minute") { Prompt = "p" }, new(Path.Combine(global, "config.toml"), null, null), null, null, null);

            var first = await runner.StartAsync(entry, "run-1", "p", null, default);
            Assert.AreEqual(AutomationRun.Completed, (await first.Completion!.WaitAsync(Patience)).Status);
            var second = await runner.StartAsync(entry, "run-2", "p", null, default);

            Assert.AreEqual((null, "CodeAlta has accepted as many commands as it keeps in one run. Restart it to run automations again.", null), (second.SessionId, second.Problem, second.Completion));
            var workspace = new WorkspaceService(host, Epoch);
            Assert.AreEqual(1, (await workspace.SnapshotAsync(new(), default)).Sessions.Length, "The run that could not send its prompt left no session behind.");
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
        => new(null, name, true, "Do the thing.", null, null, null, null, null, false, [new("daily", 0, 1, ["17:30", "09:00"], [], null, "opened", "trusted")]);

    private static (string Status, string? Message) Outcome(AutomationMutationResponse response) => (response.Status, response.Message);

    private static string? Text(JsonElement record, string name) => record.TryGetProperty(name, out var value) ? value.ToString() : null;

    // The records of one type: a command also writes a record of its result.
    private static JsonElement[] Of(List<JsonElement> records, string type) => [.. records.Where(record => Text(record, "type") == type)];

    // The automations of an application whose sessions are fakes, over a real project catalog: the page's service
    // and the alta commands on the same automations.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project, ProjectDescriptor other)
        {
            (_root, Project, Other) = (root, project, other);
            Service = new AutomationService(GlobalPath, token => projects.LoadAsync(token), new AutomationStateStore(Path.Combine(root, "state", "automations.json"), false),
                Runner, new AutomationClock { Now = Noon }, TimeZoneInfo.Utc);
            Rpc = new AutomationsService(Service, projects, Epoch);
            var services = new AltaServiceCollection().Add(projects).Add<IAltaAutomations>(new DesktopAltaAutomations(Service, projects));
            var registry = new AltaCommandRegistry();
            Alta = new AltaCommandDispatcher(registry, services);
            services.Add(registry).Add(Alta);
            Session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-of-the-user", SourceProjectId = project.Id };
        }

        public string GlobalPath => Path.Combine(_root, "global", "config.toml");
        public ProjectDescriptor Project { get; }
        public ProjectDescriptor Other { get; }
        public FakeAutomationRunner Runner { get; } = new();
        public AutomationService Service { get; }
        public AutomationsService Rpc { get; }
        public AltaCommandDispatcher Alta { get; }
        public AltaCallerIdentity Session { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("codealta-automation-host-").FullName;
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "app")).FullName);
            var other = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "other")).FullName);
            var fixture = new Fixture(root, projects, project, other);
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

    private sealed class RunnerProvider
    {
        internal ModelProviderDescriptor Descriptor { get; } = new(new ModelProviderId("automation-fixture"), "Automation fixture") { IsDefault = true, DefaultModelId = "fixture-model" };
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
